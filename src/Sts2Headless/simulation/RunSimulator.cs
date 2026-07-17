using System.Reflection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.TestSupport;
using HarmonyLib;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Unlocks;

namespace Sts2Headless;

/// <summary>
/// Full run simulator — manages the game lifecycle from character selection
/// through map navigation, combat, events, rest sites, shops, and act transitions.
/// Drives the engine forward until it hits a "decision point" requiring external input.
/// </summary>
public partial class RunSimulator
{
    private static int? _expectedSaveSchemaVersion;
    private static bool _expectedSaveSchemaVersionReady;
    private static readonly object _expectedSaveSchemaVersionLock = new();

    private RunState? _runState;
    private static bool _modelDbInitialized;
    private static readonly InlineSynchronizationContext _syncCtx = new();
    private readonly ManualResetEventSlim _turnStarted = new(false);
    private readonly ManualResetEventSlim _combatEnded = new(false);
    private static readonly LocLookup _loc = new();
    private bool _eventOptionChosen;
    private int _lastEventOptionCount;

    // Pending rewards for card selection (populated after combat, before proceeding)
    private List<Reward>? _pendingRewards;
    private CardReward? _pendingCardReward;
    private bool _rewardsProcessed;
    private int _goldBeforeCombat;
    private int _lastKnownHp;
    private readonly HeadlessCardSelector _cardSelector = new();
    // Pending bundle selection (Scroll Boxes: pick 1 of N packs)
    private IReadOnlyList<IReadOnlyList<CardModel>>? _pendingBundles;
    private TaskCompletionSource<IEnumerable<CardModel>>? _pendingBundleTcs;

    public Dictionary<string, object?> StartRun(string character, int ascension = 0, string? seed = null)
    {
        try
        {
            EnsureModelDbInitialized();

            var player = CreatePlayer(character);
            if (player == null)
                return Error($"Unknown character: {character}");

            var seedStr = seed ?? "headless_" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Log($"Creating RunState with seed={seedStr}");

            // Use CreateForTest which properly handles mutable copies internally
            _runState = RunState.CreateForTest(
                players: new[] { player },
                ascensionLevel: ascension,
                seed: seedStr
            );

            // Set up RunManager with test mode
            var netService = new NetSingleplayerGameService();
            RunManager.Instance.SetUpTest(_runState, netService);
            LocalContext.NetId = netService.NetId;

            // Force Neow event (blessing selection at start)
            _runState.ExtraFields.StartedWithNeow = true;

            // Generate rooms for all acts
            RunManager.Instance.GenerateRooms();
            Log("Rooms generated");

            // Launch the run
            RunManager.Instance.Launch();
            Log("Run launched");

            // Register event handlers for combat turn transitions
            CombatManager.Instance.TurnStarted += _ => _turnStarted.Set();
            CombatManager.Instance.CombatEnded += _ => _combatEnded.Set();

            // Finalize starting relics
            RunManager.Instance.FinalizeStartingRelics().GetAwaiter().GetResult();
            Log("Starting relics finalized");

            // Enter first act (generates map)
            RunManager.Instance.EnterAct(0, doTransition: false).GetAwaiter().GetResult();
            Log("Entered Act 0");

            // Register card selector for cards that need player choice
            CardSelectCmd.UseSelector(_cardSelector);
            LocPatches._bundleSimRef = this;

            // Now we should be at the map — detect decision point
            return DetectDecisionPoint();
        }
        catch (Exception ex)
        {
            return ErrorWithTrace("StartRun failed", ex);
        }
    }

    // ─── Test/Debug commands ───

    private static readonly System.Reflection.BindingFlags NonPublic =
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;

    /// <summary>Get the backing List&lt;T&gt; behind an IReadOnlyList property via reflection.</summary>
    private static List<T>? GetBackingList<T>(object obj, string fieldName)
    {
        var field = obj.GetType().GetField(fieldName, NonPublic);
        return field?.GetValue(obj) as List<T>;
    }

    private static void SetField(object obj, string fieldName, object? value)
    {
        var field = obj.GetType().GetField(fieldName, NonPublic);
        field?.SetValue(obj, value);
    }

    public Dictionary<string, object?> SetPlayer(Dictionary<string, System.Text.Json.JsonElement> args)
    {
        try
        {
            if (_runState == null) return Error("No run in progress");
            var player = _runState.Players[0];

            if (args.TryGetValue("hp", out var hpEl) && player.Creature != null)
                SetField(player.Creature, "_currentHp", hpEl.GetInt32());
            if (args.TryGetValue("max_hp", out var mhpEl) && player.Creature != null)
                SetField(player.Creature, "_maxHp", mhpEl.GetInt32());
            if (args.TryGetValue("gold", out var goldEl))
                player.Gold = goldEl.GetInt32();

            if (args.TryGetValue("relics", out var relicsEl))
            {
                var list = GetBackingList<RelicModel>(player, "_relics");
                if (list != null)
                {
                    list.Clear();
                    foreach (var rEl in relicsEl.EnumerateArray())
                    {
                        var id = rEl.GetString();
                        if (id == null) continue;
                        var model = ModelDb.GetById<RelicModel>(new ModelId("RELIC", id));
                        if (model != null) list.Add(model.ToMutable());
                    }
                }
            }
            if (args.TryGetValue("deck", out var deckEl))
            {
                // Remove existing cards from RunState tracking
                foreach (var c in player.Deck.Cards.ToList())
                    _runState.RemoveCard(c);
                player.Deck.Clear(silent: true);
                // Add new cards via RunState.CreateCard (sets Owner + registers)
                foreach (var cEl in deckEl.EnumerateArray())
                {
                    var id = cEl.GetString();
                    if (id == null) continue;
                    var canonical = ModelDb.GetById<CardModel>(new ModelId("CARD", id));
                    if (canonical != null)
                    {
                        var card = _runState.CreateCard(canonical, player);
                        player.Deck.AddInternal(card, silent: true);
                    }
                }
            }
            if (args.TryGetValue("potions", out var potionsEl))
            {
                var slots = GetBackingList<PotionModel>(player, "_potionSlots")
                         ?? GetBackingList<PotionModel?>(player, "_potionSlots") as System.Collections.IList;
                if (slots != null)
                {
                    for (int i = 0; i < slots.Count; i++) slots[i] = null;
                    int idx = 0;
                    foreach (var pEl in potionsEl.EnumerateArray())
                    {
                        if (idx >= slots.Count) break;
                        var id = pEl.GetString();
                        if (id != null)
                        {
                            var model = ModelDb.GetById<PotionModel>(new ModelId("POTION", id));
                            if (model != null) slots[idx] = model;
                        }
                        idx++;
                    }
                }
            }

            Log($"SetPlayer: hp={player.Creature?.CurrentHp} gold={player.Gold} relics={player.Relics.Count} deck={player.Deck?.Cards?.Count}");
            return new Dictionary<string, object?>
            {
                ["type"] = "ok",
                ["player"] = PlayerSummary(player),
            };
        }
        catch (Exception ex) { return ErrorWithTrace("SetPlayer failed", ex); }
    }

    public Dictionary<string, object?> EnterRoom(string roomType, string? encounter, string? eventId)
    {
        try
        {
            if (_runState == null) return Error("No run in progress");
            var runState = _runState;
            Log($"EnterRoom: type={roomType} encounter={encounter} event={eventId}");

            AbstractRoom room;
            switch (roomType.ToLowerInvariant())
            {
                case "combat":
                case "monster":
                case "elite":
                {
                    if (string.IsNullOrEmpty(encounter))
                        encounter = "SHRINKER_BEETLE_WEAK"; // default encounter
                    var encModel = ModelDb.GetById<EncounterModel>(new ModelId("ENCOUNTER", encounter));
                    if (encModel == null) return Error($"Unknown encounter: {encounter}");
                    room = new CombatRoom(encModel.ToMutable(), runState);
                    break;
                }
                case "shop":
                    room = new MerchantRoom();
                    break;
                case "rest":
                case "rest_site":
                    room = new RestSiteRoom();
                    break;
                case "event":
                {
                    if (string.IsNullOrEmpty(eventId))
                        return Error("event requires 'event' parameter (e.g. CHANGELING_GROVE)");
                    var evModel = ModelDb.GetById<EventModel>(new ModelId("EVENT", eventId));
                    if (evModel == null) return Error($"Unknown event: {eventId}");
                    room = new EventRoom(evModel);
                    break;
                }
                case "treasure":
                    room = new TreasureRoom(_runState.CurrentActIndex);
                    break;
                default:
                    return Error($"Unknown room type: {roomType}");
            }

            RunManager.Instance.EnterRoom(room).GetAwaiter().GetResult();
            _syncCtx.Pump();
            WaitForActionExecutor();
            return DetectDecisionPoint();
        }
        catch (Exception ex) { return ErrorWithTrace("EnterRoom failed", ex); }
    }

    public Dictionary<string, object?> SetDrawOrder(List<string> cardIds)
    {
        try
        {
            if (_runState == null) return Error("No run in progress");
            var player = _runState.Players[0];
            var pcs = player.PlayerCombatState;
            if (pcs?.DrawPile == null) return Error("Not in combat");

            var drawList = GetBackingList<CardModel>(pcs.DrawPile, "_cards");
            if (drawList == null) return Error("Cannot access draw pile");

            var newOrder = new List<CardModel>();
            var available = new List<CardModel>(drawList);
            foreach (var cardId in cardIds)
            {
                var match = available.FirstOrDefault(c =>
                    c.Id.Entry.Equals(cardId, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    newOrder.Add(match);
                    available.Remove(match);
                }
            }
            newOrder.AddRange(available);

            drawList.Clear();
            drawList.AddRange(newOrder);

            Log($"SetDrawOrder: {newOrder.Count} cards, top={newOrder.FirstOrDefault()?.Id.Entry}");
            return new Dictionary<string, object?>
            {
                ["type"] = "ok",
                ["draw_pile_count"] = drawList.Count,
                ["top_cards"] = newOrder.Take(5).Select(c => _loc.Card(c.Id.Entry)).ToList(),
            };
        }
        catch (Exception ex) { return ErrorWithTrace("SetDrawOrder failed", ex); }
    }

    // ─── Game actions ───
    public Dictionary<string, object?> LoadSave(string saveJson)
    {
        try
        {
            EnsureModelDbInitialized();

            Log("Loading save file...");

            if (!ValidateSaveSchemaVersion(saveJson, out var schemaError))
                return Error($"Save schema mismatch: {schemaError}");

            var readResult = SaveManager.FromJson<SerializableRun>(saveJson);
            if (!readResult.Success || readResult.SaveData == null)
                return Error($"Failed to parse save file: {readResult.Status} {readResult.ErrorMessage}");

            var save = readResult.SaveData;
            Log($"Save loaded: seed={save.SerializableRng?.Seed}, act={save.CurrentActIndex}, ascension={save.Ascension}");

            _runState = RunState.FromSerializable(save);
            if (_runState == null)
                return Error("Failed to create RunState from save");

            Log($"RunState created, players={_runState.Players?.Count}");

            var netService = new NetSingleplayerGameService();
            RunManager.Instance.SetUpSavedSinglePlayer(_runState, save);
            LocalContext.NetId = netService.NetId;

            CombatManager.Instance.TurnStarted += _ => _turnStarted.Set();
            CombatManager.Instance.CombatEnded += _ => _combatEnded.Set();
            CardSelectCmd.UseSelector(_cardSelector);
            LocPatches._bundleSimRef = this;

            var savedRoom = _runState.CurrentRoom;

            // Save visited coords before Launch (EnterAct will clear them)
            var savedVisitedCoords = _runState.VisitedMapCoords?.ToList() ?? new List<MapCoord>();
            var shouldResumeInitialNeow = IsInitialNeowSave(saveJson);
            Log($"Save has {savedVisitedCoords.Count} visited coords");

            RunManager.Instance.Launch();
            Log("Run launched");

            if (savedRoom is MapRoom || savedRoom == null)
            {
                // Preserve Neow for saves created before the first blessing choice.
                // Once the run has visited at least one map node, re-entering Act 1
                // should not send the player back through the Ancient start node.
                if (_runState.CurrentActIndex == 0 && savedVisitedCoords.Count > 0)
                    _runState.ExtraFields.StartedWithNeow = false;
                RunManager.Instance.EnterAct(_runState.CurrentActIndex, doTransition: false).GetAwaiter().GetResult();
                _syncCtx.Pump();
                Log($"Entered Act {_runState.CurrentActIndex}");

                if (shouldResumeInitialNeow && _runState.Map?.StartingMapPoint != null)
                {
                    Log("Restoring initial Neow event");
                    RunManager.Instance.EnterMapCoord(_runState.Map.StartingMapPoint.coord).GetAwaiter().GetResult();
                    _syncCtx.Pump();
                }

                // EnterAct clears visited coords and ActFloor — restore them from save
                if (savedVisitedCoords.Count > 0)
                {
                    if (_runState.VisitedMapCoords == null || _runState.VisitedMapCoords.Count == 0)
                    {
                        foreach (var coord in savedVisitedCoords)
                            _runState.AddVisitedMapCoord(coord);
                    }
                    _runState.ActFloor = savedVisitedCoords.Count;
                    var last = savedVisitedCoords[^1];
                    Log($"Restored map position: floor={_runState.ActFloor}, coord=({last.col},{last.row})");
                }
            }
            else
            {
                Log($"Preserving saved room: {savedRoom.GetType().Name}");
            }

            return DetectDecisionPoint();
        }
        catch (Exception ex)
        {
            return ErrorWithTrace("LoadSave failed", ex);
        }
    }

    /// <summary>
    /// Expected run save <c>schema_version</c> (lazy: first load_save only, so StartRun never fails on reflection).
    /// Order: <c>STS2_SAVE_SCHEMA_VERSION</c> env → reflect sts2.dll → unknown, defer to SaveManager.
    /// </summary>
    private static int? GetExpectedSaveSchemaVersion()
    {
        if (_expectedSaveSchemaVersionReady)
            return _expectedSaveSchemaVersion;
        lock (_expectedSaveSchemaVersionLock)
        {
            if (_expectedSaveSchemaVersionReady)
                return _expectedSaveSchemaVersion;
            _expectedSaveSchemaVersion = ResolveExpectedSaveSchemaVersion();
            _expectedSaveSchemaVersionReady = true;
            return _expectedSaveSchemaVersion;
        }
    }

    private static int? ResolveExpectedSaveSchemaVersion()
    {
        var env = Environment.GetEnvironmentVariable("STS2_SAVE_SCHEMA_VERSION");
        if (!string.IsNullOrWhiteSpace(env) && int.TryParse(env.Trim(), out var envVer))
            return envVer;

        var reflected = TryReflectLatestSaveSchemaVersion();
        if (reflected.HasValue)
            return reflected.Value;

        Console.Error.WriteLine(
            "[Sts2Headless] Could not read save schema from sts2.dll; deferring schema compatibility " +
            "to SaveManager.FromJson. Set STS2_SAVE_SCHEMA_VERSION to enforce a specific version.");
        return null;
    }

    /// <summary>Find static parameterless GetLatestSchemaVersion (or close) on sts2; supports int/uint/long.</summary>
    private static int? TryReflectLatestSaveSchemaVersion()
    {
        var asm = typeof(SerializableRun).Assembly;
        Type[] types;
        try
        {
            types = asm.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(t => t != null).Cast<Type>().ToArray();
        }

        var candidates = new List<(int score, string typeName, int value)>();
        foreach (var t in types)
        {
            MethodInfo? m;
            try
            {
                foreach (var name in new[] { "GetLatestSchemaVersion", "GetLatestVersion" })
                {
                    m = t.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                        null, Type.EmptyTypes, null);
                    if (m == null) continue;
                    var tn = t.FullName ?? "";
                    // Avoid unrelated static GetLatestVersion() elsewhere in the assembly.
                    if (name == "GetLatestVersion" && !tn.Contains("Saves", StringComparison.Ordinal))
                        continue;

                    var conv = TryConvertSchemaNumber(m.Invoke(null, null));
                    if (!conv.HasValue) continue;

                    var score = name == "GetLatestSchemaVersion" ? 100 : 0;
                    if (tn.Contains("Saves", StringComparison.Ordinal)) score += 50;
                    if (tn.Contains("Schema", StringComparison.Ordinal) || tn.Contains("Migration", StringComparison.Ordinal))
                        score += 25;
                    candidates.Add((score, tn, conv.Value));
                }
            }
            catch
            {
                // type may not support full reflection on this runtime
            }
        }

        if (candidates.Count == 0)
            return null;

        var best = candidates.OrderByDescending(c => c.score).ThenBy(c => c.typeName).First();
        return best.value;
    }

    private static int? TryConvertSchemaNumber(object? value) => value switch
    {
        int i => i,
        uint u => u <= int.MaxValue ? (int)u : null,
        long l => l >= int.MinValue && l <= int.MaxValue ? (int)l : null,
        short s => s,
        ushort us => us,
        byte b => b,
        _ => null,
    };

    private static bool ValidateSaveSchemaVersion(string saveJson, out string error)
    {
        error = "";
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(saveJson);
            var root = doc.RootElement;

            if (!root.TryGetProperty("schema_version", out var versionElem))
            {
                error = "missing schema_version";
                return false;
            }

            if (versionElem.ValueKind != System.Text.Json.JsonValueKind.Number ||
                !versionElem.TryGetInt32(out var schemaVersion))
            {
                error = "schema_version is not a valid integer";
                return false;
            }

            var expected = GetExpectedSaveSchemaVersion();
            if (expected.HasValue && schemaVersion != expected.Value)
            {
                error = $"expected v{expected.Value}, got v{schemaVersion}";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = $"could not inspect save: {ex.Message}";
            return false;
        }
    }

    private static bool TrySetPropertyValue(object target, string propertyName, object? value)
    {
        var prop = target.GetType().GetProperty(propertyName);
        if (prop?.CanWrite != true)
            return false;
        prop.SetValue(target, value);
        return true;
    }

    private static bool IsInitialNeowSave(string saveJson)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(saveJson);
            var root = doc.RootElement;

            if (!root.TryGetProperty("current_act_index", out var actIndexElem) || actIndexElem.GetInt32() != 0)
                return false;

            var hasVisitedCoords = root.TryGetProperty("visited_map_coords", out var visitedElem)
                                && visitedElem.ValueKind == System.Text.Json.JsonValueKind.Array
                                && visitedElem.GetArrayLength() > 0;
            if (hasVisitedCoords)
                return false;

            return root.TryGetProperty("extra_fields", out var extraFieldsElem)
                && extraFieldsElem.ValueKind == System.Text.Json.JsonValueKind.Object
                && extraFieldsElem.TryGetProperty("started_with_neow", out var startedElem)
                && startedElem.ValueKind == System.Text.Json.JsonValueKind.True;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryRollbackSerializedSaveToPreRoom(SerializableRun serializableRun, out string error)
    {
        error = "";

        var saveType = serializableRun.GetType();
        var visitedProp = saveType.GetProperty("VisitedMapCoords");
        if (visitedProp == null)
        {
            error = "Save data is missing VisitedMapCoords";
            return false;
        }

        var visitedValue = visitedProp.GetValue(serializableRun);
        var visitedItems = new List<object?>();
        if (visitedValue is System.Collections.IEnumerable visitedEnumerable)
        {
            foreach (var item in visitedEnumerable)
                visitedItems.Add(item);
        }

        if (visitedItems.Count == 0)
        {
            error = "Cannot roll back save before the first room";
            return false;
        }

        visitedItems.RemoveAt(visitedItems.Count - 1);

        var visitedType = visitedProp.PropertyType;
        if (visitedType.IsArray)
        {
            var elementType = visitedType.GetElementType()!;
            var array = Array.CreateInstance(elementType, visitedItems.Count);
            for (int i = 0; i < visitedItems.Count; i++)
                array.SetValue(visitedItems[i], i);
            visitedProp.SetValue(serializableRun, array);
        }
        else if (visitedType.IsGenericType)
        {
            var elementType = visitedType.GetGenericArguments()[0];
            var listType = typeof(List<>).MakeGenericType(elementType);
            var list = (System.Collections.IList)Activator.CreateInstance(listType)!;
            foreach (var item in visitedItems)
                list.Add(item);
            visitedProp.SetValue(serializableRun, list);
        }
        else
        {
            error = $"Unsupported VisitedMapCoords type: {visitedType.Name}";
            return false;
        }

        TrySetPropertyValue(serializableRun, "ActFloor", visitedItems.Count);
        TrySetPropertyValue(serializableRun, "CurrentMapCoord", visitedItems.Count > 0 ? visitedItems[^1] : null);
        TrySetPropertyValue(serializableRun, "PreFinishedRoom", null);
        TrySetPropertyValue(serializableRun, "CurrentRoom", null);
        return true;
    }

    public Dictionary<string, object?> SaveCheckpoint(string? outputPath)
    {
        try
        {
            if (_runState == null)
                return Error("No active run to save");

            if (string.IsNullOrEmpty(outputPath))
                return Error("No output path specified for quit save");

            var currentRoom = _runState.CurrentRoom;
            SerializableRun serializableRun;

            if (currentRoom is MapRoom || currentRoom == null)
            {
                Log($"Saving map checkpoint (room={currentRoom?.GetType().Name ?? "null"}, outputPath={outputPath})...");
                serializableRun = RunManager.Instance.ToSave(currentRoom);
            }
            else
            {
                Log($"Saving pre-room checkpoint from {currentRoom.GetType().Name} (outputPath={outputPath})...");
                serializableRun = RunManager.Instance.ToSave(new MapRoom());
                if (!TryRollbackSerializedSaveToPreRoom(serializableRun, out var rollbackError))
                    return Error($"Cannot save checkpoint: {rollbackError}");
            }

            var saveJson = SaveManager.ToJson(serializableRun);
            Log($"Serialized save: {saveJson.Length} chars");

            var dir = System.IO.Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir))
                System.IO.Directory.CreateDirectory(dir);
            System.IO.File.WriteAllText(outputPath, saveJson);
            Log($"Save written to: {outputPath}");

            return new Dictionary<string, object?>
            {
                ["type"] = "save_result",
                ["success"] = true,
                ["path"] = outputPath,
                ["size"] = saveJson.Length,
                ["room_type"] = currentRoom?.GetType().Name,
            };
        }
        catch (Exception ex)
        {
            return ErrorWithTrace("SaveCheckpoint failed", ex);
        }
    }
    public Dictionary<string, object?> ExecuteAction(string action, Dictionary<string, object?>? args)
    {
        try
        {
            if (_runState == null)
                return Error("No run in progress");

            var player = _runState.Players[0];

            switch (action)
            {
                case "select_map_node":
                    return DoMapSelect(player, args);
                case "play_card":
                    return DoPlayCard(player, args);
                case "end_turn":
                    return DoEndTurn(player);
                case "choose_option":
                    return DoChooseOption(player, args);
                case "select_card_reward":
                    return DoSelectCardReward(player, args);
                case "skip_card_reward":
                    return DoSkipCardReward(player);
                case "buy_card":
                    return DoBuyCard(player, args);
                case "buy_relic":
                    return DoBuyRelic(player, args);
                case "buy_potion":
                    return DoBuyPotion(player, args);
                case "remove_card":
                    return DoRemoveCard(player);
                case "select_bundle":
                    return DoSelectBundle(player, args);
                case "select_cards":
                    return DoSelectCards(player, args);
                case "skip_select":
                    return DoSkipSelect(player);
                case "use_potion":
                    return DoUsePotion(player, args);
                case "discard_potion":
                    return DoDiscardPotion(player, args);
                case "leave_room":
                    return DoLeaveRoom(player);
                case "proceed":
                    return DoProceed(player);
                default:
                    return Error($"Unknown action: {action}");
            }
        }
        catch (Exception ex)
        {
            return ErrorWithTrace($"Action '{action}' failed", ex);
        }
    }

    #region Helpers

    private void WaitForActionExecutor()
    {
        try
        {
            // Ensure sync context is set for this thread
            SynchronizationContext.SetSynchronizationContext(_syncCtx);

            // Pump the synchronization context to execute any pending continuations
            _syncCtx.Pump();

            // Executor may stay "running" while the game awaits headless card selection / reward (e.g. Attack Potion).
            // Spinning here would time out and downstream code could mis-handle an in-flight potion use (BUG-026).
            if (_cardSelector.HasPending || _cardSelector.HasPendingReward)
                return;

            var executor = RunManager.Instance.ActionExecutor;
            if (executor.IsRunning)
            {
                // Pump while waiting for executor
                int maxPumps = 1000;
                for (int i = 0; i < maxPumps; i++)
                {
                    _syncCtx.Pump();
                    if (!executor.IsRunning) break;
                    Thread.Sleep(1);
                }
            }
        }
        catch (Exception ex)
        {
            Log($"WaitForActionExecutor exception: {ex.Message}");
        }
    }

    private void SpinWaitForCombatStable()
    {
        int maxIterations = 200;
        for (int i = 0; i < maxIterations; i++)
        {
            _syncCtx.Pump();
            if (!CombatManager.Instance.IsInProgress) return;
            if (CombatManager.Instance.IsPlayPhase) return;
            WaitForActionExecutor();
            if (CombatManager.Instance.IsPlayPhase || !CombatManager.Instance.IsInProgress) return;
            Thread.Sleep(5);
        }
    }

    /// <summary>Compute what a card would look like after upgrading (stats + cost + description).</summary>
    private Dictionary<string, object?>? GetUpgradedInfo(CardModel card)
    {
        if (!card.IsUpgradable) return null;
        try
        {
            var clone = ModelDb.GetById<CardModel>(card.Id).ToMutable();
            // Apply existing upgrades first
            for (int i = 0; i < card.CurrentUpgradeLevel; i++)
            {
                clone.UpgradeInternal();
                clone.FinalizeUpgradeInternal();
            }
            // Apply one more upgrade
            clone.UpgradeInternal();
            clone.FinalizeUpgradeInternal();

            var stats = new Dictionary<string, object?>();
            try { foreach (var dv in clone.DynamicVars.Values) stats[dv.Name.ToLowerInvariant()] = (int)dv.BaseValue; } catch { }

            // Compare keywords before/after upgrade
            var oldKws = card.Keywords?.Where(k => k != CardKeyword.None).Select(k => k.ToString()).ToHashSet() ?? new();
            var newKws = clone.Keywords?.Where(k => k != CardKeyword.None).Select(k => k.ToString()).ToHashSet() ?? new();
            var addedKws = newKws.Except(oldKws).ToList();
            var removedKws = oldKws.Except(newKws).ToList();

            return new Dictionary<string, object?>
            {
                ["cost"] = clone.EnergyCost?.GetResolved() ?? 0,
                ["stats"] = stats.Count > 0 ? stats : null,
                ["description"] = _loc.Localized("cards", card.Id.Entry + ".description"),
                ["added_keywords"] = addedKws.Count > 0 ? addedKws : null,
                ["removed_keywords"] = removedKws.Count > 0 ? removedKws : null,
            };
        }
        catch { return null; }
    }

    private Dictionary<string, object?> PlayerSummary(Player player)
    {
        return new Dictionary<string, object?>
        {
            ["name"] = _loc.Localized("characters", (player.Character?.Id.Entry ?? "IRONCLAD") + ".title"),
            ["hp"] = player.Creature?.CurrentHp ?? 0,
            ["max_hp"] = player.Creature?.MaxHp ?? 0,
            ["block"] = player.Creature?.Block ?? 0,
            ["gold"] = player.Gold,
            ["relics"] = player.Relics?.Select(r =>
            {
                var vars = new Dictionary<string, object?>();
                try { foreach (var dv in r.DynamicVars.Values) vars[dv.Name] = (int)dv.BaseValue; } catch { }
                return new Dictionary<string, object?>
                {
                    ["name"] = _loc.Relic(r.Id.Entry),
                    ["description"] = _loc.Localized("relics", r.Id.Entry + ".description"),
                    ["vars"] = vars.Count > 0 ? vars : null,
                };
            }).ToList(),
            ["potions"] = player.Potions?.Select((p, i) =>
            {
                if (p == null) return null;
                var pvars = new Dictionary<string, object?>();
                try { foreach (var dv in p.DynamicVars.Values) pvars[dv.Name] = (int)dv.BaseValue; } catch { }
                return new Dictionary<string, object?>
                {
                    ["index"] = i,
                    ["name"] = _loc.Potion(p.Id.Entry),
                    ["description"] = _loc.Localized("potions", p.Id.Entry + ".description"),
                    ["vars"] = pvars.Count > 0 ? pvars : null,
                    ["target_type"] = p.TargetType.ToString(),
                };
            }).Where(x => x != null).ToList(),
            ["deck_size"] = player.Deck?.Cards?.Count(c => c != null) ?? 0,
            ["deck"] = player.Deck?.Cards?.Where(c => c != null).Select(c =>
            {
                var dstats = new Dictionary<string, object?>();
                try { foreach (var dv in c.DynamicVars.Values) dstats[dv.Name.ToLowerInvariant()] = (int)dv.BaseValue; } catch { }
                var dkws = c.Keywords?.Where(k => k != CardKeyword.None).Select(k => k.ToString()).ToList();
                return new Dictionary<string, object?>
                {
                    ["id"] = c.Id.ToString(),
                    ["name"] = _loc.Card(c.Id.Entry),
                    ["cost"] = c.EnergyCost?.GetResolved() ?? 0,
                    ["type"] = c.Type.ToString(),
                    ["upgraded"] = c.IsUpgraded,
                    ["description"] = _loc.Localized("cards", c.Id.Entry + ".description"),
                    ["stats"] = dstats.Count > 0 ? dstats : null,
                    ["keywords"] = dkws?.Count > 0 ? dkws : null,
                    ["after_upgrade"] = GetUpgradedInfo(c),
                };
            }).ToList(),
        };
    }

    /// <summary>Common context added to every decision point.</summary>
    private Dictionary<string, object?> RunContext()
    {
        if (_runState == null) return new();
        var ctx = new Dictionary<string, object?>
        {
            ["act"] = _runState.CurrentActIndex + 1,
            ["act_name"] = _loc.Act(_runState.Act?.Id.Entry ?? "OVERGROWTH"),
            ["floor"] = _runState.ActFloor,
            ["room_type"] = _runState.CurrentRoom?.RoomType.ToString(),
        };

        // Boss encounter info — use BossEncounter?.Id?.Entry
        try
        {
            var bossIdEntry = _runState.Act?.BossEncounter?.Id?.Entry;
            if (!string.IsNullOrEmpty(bossIdEntry))
            {
                var monsterKey = bossIdEntry.EndsWith("_BOSS") ? bossIdEntry[..^5] : bossIdEntry;
                // Handle special mappings
                if (monsterKey == "THE_KIN") monsterKey = "KIN_PRIEST";
                ctx["boss"] = new Dictionary<string, object?>
                {
                    ["id"] = bossIdEntry,
                    ["name"] = _loc.Monster(monsterKey),
                };
            }
        }
        catch { }

        return ctx;
    }

    private static void EnsureModelDbInitialized()
    {
        if (_modelDbInitialized) return;
        _modelDbInitialized = true;

        TestMode.IsOn = true;

        // Install inline sync context on main thread
        SynchronizationContext.SetSynchronizationContext(_syncCtx);

        // Initialize PlatformServices before anything touches PlatformUtil
        try
        {
            // Try to access PlatformUtil to trigger its static init
            // If it fails, it won't be available but most code checks SteamInitializer.Initialized
            var _ = MegaCrit.Sts2.Core.Platform.PlatformUtil.PrimaryPlatform;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WARN] PlatformUtil init: {ex.Message}");
        }

        // Initialize SaveManager with a dummy profile for save/load support
        try { SaveManager.Instance.InitProfileId(0); }
        catch (Exception ex) { Console.Error.WriteLine($"[WARN] SaveManager.InitProfileId: {ex.Message}"); }

        // Initialize progress data for epoch/timeline tracking
        try { SaveManager.Instance.InitProgressData(); }
        catch (Exception ex) { Console.Error.WriteLine($"[WARN] InitProgressData: {ex.Message}"); }

        // Install the Task.Yield patch but keep SuppressYield=false by default.
        // SuppressYield is toggled to true only during EndTurn to prevent boss fight deadlocks.
        PatchTaskYield();

        // Patch Cmd.Wait to be a no-op in headless mode.
        // Cmd.Wait(duration) is used for UI animations (e.g., PreviewCardPileAdd during
        // Vantom's Dismember move adding Wounds). In headless mode, these never complete
        // because there's no Godot scene tree, causing the ActionExecutor to deadlock.
        PatchCmdWait();

        // Initialize localization system (needed for events, cards, etc.)
        InitLocManager();

        var subtypes = MegaCrit.Sts2.Core.Models.AbstractModelSubtypes.All;
        int registered = 0, failed = 0;
        for (int i = 0; i < subtypes.Count; i++)
        {
            try
            {
                ModelDb.Inject(subtypes[i]);
                registered++;
            }
            catch (Exception ex)
            {
                failed++;
                // Only log first few failures to reduce noise
                if (failed <= 5)
                    Console.Error.WriteLine($"[WARN] Failed to register {subtypes[i].Name}: {ex.GetType().Name}: {ex.Message}");
            }
        }
        Console.Error.WriteLine($"[INFO] ModelDb: {registered} registered, {failed} failed out of {subtypes.Count}");

        // Initialize net ID serialization cache (needed for combat actions)
        try
        {
            ModelIdSerializationCache.Init();
            Console.Error.WriteLine("[INFO] ModelIdSerializationCache initialized");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WARN] ModelIdSerializationCache.Init: {ex.Message}");
        }
    }

    private Player? CreatePlayer(string characterName)
    {
        return characterName.ToLowerInvariant() switch
        {
            "ironclad" => Player.CreateForNewRun<Ironclad>(UnlockState.all, 1uL),
            "silent" => Player.CreateForNewRun<Silent>(UnlockState.all, 1uL),
            "defect" => Player.CreateForNewRun<Defect>(UnlockState.all, 1uL),
            "regent" => Player.CreateForNewRun<Regent>(UnlockState.all, 1uL),
            "necrobinder" => Player.CreateForNewRun<Necrobinder>(UnlockState.all, 1uL),
            _ => null
        };
    }

    private static void PatchCmdWait()
    {
        try
        {
            var harmony = new Harmony("sts2headless.cmdwait");
            // Find Cmd.Wait(float) — it's in MegaCrit.Sts2.Core.Commands namespace
            // Find Cmd type via CardPileCmd's assembly (both are in same namespace)
            var cmdPileType = typeof(MegaCrit.Sts2.Core.Commands.CardPileCmd);
            var cmdAsm = cmdPileType.Assembly;
            Type? cmdType = cmdAsm.GetType("MegaCrit.Sts2.Core.Commands.Cmd");
            // If not found by exact name, search by namespace + "Wait" method
            if (cmdType == null)
            {
                foreach (var t in cmdAsm.GetTypes())
                {
                    if (t.Namespace == "MegaCrit.Sts2.Core.Commands")
                    {
                        var waitM = t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly)
                            .Where(m => m.Name == "Wait").ToList();
                        if (waitM.Count > 0)
                        {
                            cmdType = t;
                            Console.Error.WriteLine($"[INFO] Found Wait() in {t.FullName}");
                            break;
                        }
                    }
                }
            }
            if (cmdType != null)
            {
                var waitMethod = cmdType.GetMethod("Wait",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
                    null, new[] { typeof(float) }, null);
                if (waitMethod != null)
                {
                    var prefix = typeof(YieldPatches).GetMethod(nameof(YieldPatches.CmdWaitPrefix),
                        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
                    if (prefix != null)
                    {
                        harmony.Patch(waitMethod, new HarmonyMethod(prefix));
                        Console.Error.WriteLine("[INFO] Patched Cmd.Wait() to no-op (prevents boss fight deadlocks)");
                    }
                }
                else
                {
                    // Try to find any Wait method
                    var methods = cmdType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                        .Where(m => m.Name == "Wait").ToList();
                    foreach (var m in methods)
                    {
                        Console.Error.WriteLine($"[INFO] Found Cmd.Wait({string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name))})");
                        var prefix = typeof(YieldPatches).GetMethod(nameof(YieldPatches.CmdWaitPrefix),
                            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
                        if (prefix != null)
                        {
                            harmony.Patch(m, new HarmonyMethod(prefix));
                            Console.Error.WriteLine($"[INFO] Patched Cmd.Wait variant");
                        }
                    }
                }
            }
            else
            {
                Console.Error.WriteLine("[WARN] Could not find MegaCrit.Sts2.Core.Commands.Cmd type");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WARN] Failed to patch Cmd.Wait: {ex.Message}");
        }
    }

    private static void PatchTaskYield()
    {
        try
        {
            var harmony = new Harmony("sts2headless.yieldpatch");

            // Patch YieldAwaitable.YieldAwaiter.IsCompleted to return true
            // This makes `await Task.Yield()` execute synchronously (continuation runs inline)
            var yieldAwaiterType = typeof(System.Runtime.CompilerServices.YieldAwaitable)
                .GetNestedType("YieldAwaiter");
            if (yieldAwaiterType != null)
            {
                var isCompletedProp = yieldAwaiterType.GetProperty("IsCompleted");
                if (isCompletedProp != null)
                {
                    var getter = isCompletedProp.GetGetMethod();
                    var prefix = typeof(YieldPatches).GetMethod(nameof(YieldPatches.IsCompletedPrefix),
                        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
                    if (getter != null && prefix != null)
                    {
                        harmony.Patch(getter, new HarmonyMethod(prefix));
                        Console.Error.WriteLine("[INFO] Patched Task.Yield() to be synchronous");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WARN] Failed to patch Task.Yield: {ex.Message}");
        }
    }

    internal static class YieldPatches
    {
        // Only suppress Task.Yield() when this flag is set (during end_turn processing)
        public static volatile bool SuppressYield;

        public static bool IsCompletedPrefix(ref bool __result)
        {
            if (SuppressYield)
            {
                __result = true;
                return false;
            }
            return true; // Let normal Yield behavior run
        }

        /// <summary>Harmony prefix: make Cmd.Wait() return completed task immediately (no-op in headless).</summary>
        public static bool CmdWaitPrefix(ref Task __result)
        {
            __result = Task.CompletedTask;
            return false; // Skip original method
        }
    }

    private static void InitLocManager()
    {
        // Create a LocManager instance with stub tables via reflection.
        // LocManager.Initialize() fails because PlatformUtil isn't available,
        // and Harmony can't patch some LocString methods due to JIT issues.
        // Solution: create an uninitialized LocManager, set its _tables, and
        // use Harmony only for the simple LocTable.GetRawText fallback.
        try
        {
            // Create uninitialized LocManager and set Instance
            var instanceProp = typeof(LocManager).GetProperty("Instance",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
            var instance = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(LocManager));
            instanceProp!.SetValue(null, instance);

            // Load REAL localization data from localization_eng/ JSON files
            var tablesField = typeof(LocManager).GetField("_tables",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var tables = new Dictionary<string, LocTable>();

            var locDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "localization_eng");
            if (Directory.Exists(locDir))
            {
                foreach (var file in Directory.GetFiles(locDir, "*.json"))
                {
                    try
                    {
                        var name = Path.GetFileNameWithoutExtension(file);
                        var data = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
                            File.ReadAllText(file));
                        if (data != null)
                            tables[name] = new LocTable(name, data);
                    }
                    catch { }
                }
                Console.Error.WriteLine($"[INFO] Loaded {tables.Count} localization tables from {locDir}");
            }
            else
            {
                Console.Error.WriteLine($"[WARN] Localization dir not found: {locDir}");
                // Fallback: empty tables
                var tableNames = new[] {
                    "achievements","acts","afflictions","ancients","ascension",
                    "bestiary","card_keywords","card_library","card_reward_ui",
                    "card_selection","cards","characters","combat_messages",
                    "credits","enchantments","encounters","epochs","eras",
                    "events","ftues","game_over_screen","gameplay_ui",
                    "inspect_relic_screen","intents","main_menu_ui","map",
                    "merchant_room","modifiers","monsters","orbs","potion_lab",
                    "potions","powers","relic_collection","relics","rest_site_ui",
                    "run_history","settings_ui","static_hover_tips","stats_screen",
                    "timeline","vfx"
                };
                foreach (var name in tableNames)
                    tables[name] = new LocTable(name, new Dictionary<string, string>());
            }
            tablesField!.SetValue(instance, tables);

            // Force English UI
            var localeProp = typeof(LocManager).GetProperty("Language",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
            try { localeProp?.SetValue(instance, "eng"); } catch { }

            // Set CultureInfo
            var cultureProp = typeof(LocManager).GetProperty("CultureInfo",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
            try { cultureProp?.SetValue(instance, System.Globalization.CultureInfo.InvariantCulture); } catch { }

            // Initialize _smartFormatter — the game uses `new SmartFormatter()`
            try
            {
                var sfField = typeof(LocManager).GetField("_smartFormatter",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                // Dump ALL fields (instance + static)
                foreach (var f in typeof(LocManager).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public))
                    Console.Error.WriteLine($"[DEBUG] LocManager {(f.IsStatic?"static":"inst")} field: {f.Name} ({f.FieldType.Name})");
                Console.Error.WriteLine($"[DEBUG] sfField: {sfField?.Name ?? "null"} type: {sfField?.FieldType?.Name ?? "null"}");
                if (sfField != null)
                {
                    try
                    {
                        // List constructors to find the right one
                        var ctors = sfField.FieldType.GetConstructors(
                            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                        Console.Error.WriteLine($"[DEBUG] SmartFormatter has {ctors.Length} constructors:");
                        foreach (var ctor in ctors)
                        {
                            var ps = ctor.GetParameters();
                            Console.Error.WriteLine($"  ({string.Join(", ", ps.Select(p => $"{p.ParameterType.Name} {p.Name}"))})");
                        }
                        // Try the one with fewest params
                        var bestCtor = ctors.OrderBy(c => c.GetParameters().Length).First();
                        var args2 = bestCtor.GetParameters().Select(p =>
                            p.HasDefaultValue ? p.DefaultValue :
                            p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null
                        ).ToArray();
                        var sf = bestCtor.Invoke(args2);
                        // Register extensions using the game's own LoadLocFormatters logic
                        // Call it via reflection on LocManager instance
                        try
                        {
                            var loadMethod = typeof(LocManager).GetMethod("LoadLocFormatters",
                                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                            if (loadMethod != null)
                            {
                                loadMethod.Invoke(instance, null);
                                Console.Error.WriteLine("[INFO] SmartFormatter initialized via LoadLocFormatters");
                            }
                            else
                            {
                                sfField.SetValue(null, sf);
                                Console.Error.WriteLine("[INFO] SmartFormatter set (no LoadLocFormatters found)");
                            }
                        }
                        catch (Exception lfEx)
                        {
                            sfField.SetValue(null, sf);
                            Console.Error.WriteLine($"[WARN] LoadLocFormatters failed: {lfEx.InnerException?.Message ?? lfEx.Message}");
                        }
                    }
                    catch (Exception sfEx)
                    {
                        Console.Error.WriteLine($"[WARN] SmartFormatter create failed: {sfEx.GetType().Name}: {sfEx.Message}");
                        if (sfEx.InnerException != null)
                            Console.Error.WriteLine($"  Inner: {sfEx.InnerException.GetType().Name}: {sfEx.InnerException.Message}");
                    }
                }
                else
                {
                    Console.Error.WriteLine("[WARN] _smartFormatter field not found in LocManager");
                }
            }
            catch (Exception ex) { Console.Error.WriteLine($"[WARN] _smartFormatter init: {ex.GetType().Name}: {ex.Message}\n{ex.InnerException?.Message}"); }

            // Initialize _engTables to point to _tables (avoid null ref in fallback)
            try
            {
                var engTablesField = typeof(LocManager).GetField("_engTables",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                engTablesField?.SetValue(instance, tables);
            }
            catch { }

            Console.Error.WriteLine("[INFO] LocManager initialized with stub tables");

            // Use Harmony to patch methods that need fallback behavior
            var harmony = new Harmony("sts2headless.locpatch");

            // With real loc data loaded, we only need fallback patches for:
            // 1. LocTable.GetRawText — return key for missing entries instead of throwing
            // 2. LocManager.SmartFormat — _smartFormatter is null, return raw text instead
            // We do NOT patch GetFormattedText/GetRawText on LocString anymore
            // so the real localization pipeline works (needed for Neow event etc.)

            var getRawText = typeof(LocTable).GetMethod("GetRawText",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public,
                null, new[] { typeof(string) }, null);
            var prefix = typeof(LocPatches).GetMethod(nameof(LocPatches.GetRawTextPrefix),
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
            if (getRawText != null && prefix != null)
            {
                harmony.Patch(getRawText, new HarmonyMethod(prefix));
                Console.Error.WriteLine("[INFO] Patched LocTable.GetRawText");
            }

            // Patch GetLocString to not throw
            var getLocString = typeof(LocTable).GetMethod("GetLocString");
            var glsPrefix = typeof(LocPatches).GetMethod(nameof(LocPatches.GetLocStringPrefix),
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
            if (getLocString != null && glsPrefix != null)
            {
                try { harmony.Patch(getLocString, new HarmonyMethod(glsPrefix)); }
                catch (Exception ex4) { Console.Error.WriteLine($"[WARN] Failed to patch GetLocString: {ex4.Message}"); }
            }

            // Patch FromChooseABundleScreen to use our card selector
            try
            {
                var bundleMethod = typeof(MegaCrit.Sts2.Core.Commands.CardSelectCmd).GetMethod("FromChooseABundleScreen",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
                var bundlePrefix = typeof(LocPatches).GetMethod(nameof(LocPatches.BundleScreenPrefix),
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
                if (bundleMethod != null && bundlePrefix != null)
                {
                    harmony.Patch(bundleMethod, new HarmonyMethod(bundlePrefix));
                    Console.Error.WriteLine("[INFO] Patched FromChooseABundleScreen");
                }
            }
            catch (Exception ex) { Console.Error.WriteLine($"[WARN] Bundle patch: {ex.Message}"); }

            // Patch Neutralize.OnPlay to avoid NullRef in DamageCmd.Attack().Execute()
            try
            {
                var neutralizeType = typeof(MegaCrit.Sts2.Core.Models.Cards.Neutralize);
                var neutralizeOnPlay = neutralizeType.GetMethod("OnPlay",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (neutralizeOnPlay != null)
                {
                    var neutPrefix = typeof(LocPatches).GetMethod(nameof(LocPatches.NeutralizePrefix),
                        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
                    if (neutPrefix != null)
                    {
                        harmony.Patch(neutralizeOnPlay, new HarmonyMethod(neutPrefix));
                        Console.Error.WriteLine("[INFO] Patched Neutralize.OnPlay");
                    }
                }
            }
            catch (Exception ex) { Console.Error.WriteLine($"[WARN] Neutralize patch: {ex.Message}"); }

            // Patch HasEntry to always return true
            PatchMethod(harmony, typeof(LocTable), "HasEntry", nameof(LocPatches.HasEntryPrefix));

            // Patch IsLocalKey to always return true
            PatchMethod(harmony, typeof(LocTable), "IsLocalKey", nameof(LocPatches.HasEntryPrefix));

            // Patch LocString.Exists (static) to always return true
            var locStringExists = typeof(LocString).GetMethod("Exists",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
            if (locStringExists != null)
            {
                PatchMethod(harmony, locStringExists, nameof(LocPatches.HasEntryPrefix));
            }

            // Patch LocTable.GetLocStringsWithPrefix to return empty list
            PatchMethod(harmony, typeof(LocTable), "GetLocStringsWithPrefix", nameof(LocPatches.GetLocStringsWithPrefixPrefix));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WARN] InitLocManager failed: {ex.Message}");
        }
    }

    private static void PatchMethod(Harmony harmony, Type type, string methodName, string patchName)
    {
        try
        {
            var method = type.GetMethod(methodName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            PatchMethod(harmony, method, patchName);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[WARN] Failed to patch {type.Name}.{methodName}: {ex.Message}"); }
    }

    private static void PatchMethod(Harmony harmony, System.Reflection.MethodInfo? method, string patchName)
    {
        if (method == null) return;
        try
        {
            var prefix = typeof(LocPatches).GetMethod(patchName, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
            if (prefix != null) harmony.Patch(method, new HarmonyMethod(prefix));
        }
        catch (Exception ex) { Console.Error.WriteLine($"[WARN] Failed to patch {method.Name}: {ex.Message}"); }
    }

    internal static class LocPatches
    {
        public static bool GetRawTextPrefix(LocTable __instance, string key, ref string __result)
        {
            // Return key as fallback "translation"
            __result = key;
            return false;
        }

        public static bool GetFormattedTextPrefix(LocString __instance, ref string __result)
        {
            __result = __instance?.LocEntryKey ?? "";
            return false;
        }

        public static bool GetRawTextInstancePrefix(LocString __instance, ref string __result)
        {
            __result = __instance?.LocEntryKey ?? "";
            return false;
        }


        /// <summary>Harmony prefix: replace Neutralize.OnPlay with safe damage+weak.</summary>
        public static bool NeutralizePrefix(CardModel __instance, ref Task __result,
            PlayerChoiceContext choiceContext, CardPlay cardPlay)
        {
            if (cardPlay.Target == null) { __result = Task.CompletedTask; return false; }
            __result = NeutralizeSafe(__instance, choiceContext, cardPlay);
            return false;
        }

        private static async Task NeutralizeSafe(CardModel card, PlayerChoiceContext ctx, CardPlay play)
        {
            try
            {
                await CreatureCmd.Damage(ctx, play.Target!, card.DynamicVars.Damage.BaseValue,
                    MegaCrit.Sts2.Core.ValueProps.ValueProp.Move, card);
                await PowerCmd.Apply<WeakPower>(play.Target!, card.DynamicVars["WeakPower"].BaseValue,
                    card.Owner.Creature, card);
            }
            catch (Exception ex) { Console.Error.WriteLine($"[WARN] Neutralize safe: {ex.Message}"); }
        }

        public static bool HasEntryPrefix(ref bool __result)
        {
            __result = true;
            return false;
        }

        public static bool GetLocStringPrefix(LocTable __instance, string key, ref LocString __result)
        {
            var nameField = typeof(LocTable).GetField("_name",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var tableName = nameField?.GetValue(__instance) as string ?? "_unknown";
            __result = new LocString(tableName, key);
            return false;
        }

        /// <summary>
        /// Intercept bundle selection — store bundles and wait for player to pick a pack index.
        /// </summary>
        public static bool BundleScreenPrefix(
            MegaCrit.Sts2.Core.Entities.Players.Player player,
            IReadOnlyList<IReadOnlyList<CardModel>> bundles,
            ref Task<IEnumerable<CardModel>> __result)
        {
            if (bundles.Count == 0)
            {
                __result = Task.FromResult<IEnumerable<CardModel>>(Array.Empty<CardModel>());
                return false;
            }

            // Store pending bundles for the main loop to present
            var sim = _bundleSimRef;
            if (sim != null)
            {
                sim._pendingBundles = bundles;
                sim._pendingBundleTcs = new TaskCompletionSource<IEnumerable<CardModel>>();
                Console.Error.WriteLine($"[SIM] Bundle selection pending: {bundles.Count} packs");

                __result = sim._pendingBundleTcs.Task;
                return false;
            }

            __result = Task.FromResult<IEnumerable<CardModel>>(bundles[0]);
            return false;
        }

        // Static reference so Harmony patch can access the simulator instance
        internal static RunSimulator? _bundleSimRef;

        public static bool GetLocStringsWithPrefixPrefix(ref IReadOnlyList<LocString> __result)
        {
            __result = new List<LocString>();
            return false;
        }
    }

    private static void Log(string message)
    {
        Console.Error.WriteLine($"[SIM] {message}");
    }

    private static Dictionary<string, object?> Error(string message) =>
        new() { ["type"] = "error", ["message"] = message };

    private static Dictionary<string, object?> ErrorWithTrace(string context, Exception ex)
    {
        var inner = ex;
        while (inner.InnerException != null) inner = inner.InnerException;
        return new Dictionary<string, object?>
        {
            ["type"] = "error",
            ["message"] = $"{context}: {inner.GetType().Name}: {inner.Message}",
            ["stack_trace"] = inner.StackTrace,
        };
    }

    public Dictionary<string, object?> GetFullMap()
    {
        if (_runState?.Map == null)
            return Error("No map available");

        var map = _runState.Map;
        var rows = new List<List<Dictionary<string, object?>>>();
        var currentCoord = _runState.CurrentMapCoord;
        var visited = _runState.VisitedMapCoords;

        for (int row = 0; row < map.GetRowCount(); row++)
        {
            var rowNodes = new List<Dictionary<string, object?>>();
            foreach (var point in map.GetPointsInRow(row))
            {
                if (point == null) continue;
                var children = point.Children?.Select(ch => new Dictionary<string, object?>
                {
                    ["col"] = (int)ch.coord.col,
                    ["row"] = (int)ch.coord.row,
                }).ToList();

                var isVisited = visited?.Any(v => v.col == point.coord.col && v.row == point.coord.row) ?? false;
                var isCurrent = currentCoord.HasValue &&
                    currentCoord.Value.col == point.coord.col && currentCoord.Value.row == point.coord.row;

                rowNodes.Add(new Dictionary<string, object?>
                {
                    ["col"] = (int)point.coord.col,
                    ["row"] = (int)point.coord.row,
                    ["type"] = point.PointType.ToString(),
                    ["children"] = children,
                    ["visited"] = isVisited,
                    ["current"] = isCurrent,
                });
            }
            if (rowNodes.Count > 0)
                rows.Add(rowNodes);
        }

        // Boss node
        var bossNode = new Dictionary<string, object?>
        {
            ["col"] = (int)map.BossMapPoint.coord.col,
            ["row"] = (int)map.BossMapPoint.coord.row,
            ["type"] = map.BossMapPoint.PointType.ToString(),
        };

        // Add boss name/id — use BossEncounter?.Id?.Entry
        try
        {
            var bossIdEntry = _runState.Act?.BossEncounter?.Id?.Entry;
            if (!string.IsNullOrEmpty(bossIdEntry))
            {
                var monsterKey = bossIdEntry.EndsWith("_BOSS") ? bossIdEntry[..^5] : bossIdEntry;
                if (monsterKey == "THE_KIN") monsterKey = "KIN_PRIEST";
                bossNode["id"] = bossIdEntry;
                bossNode["name"] = _loc.Monster(monsterKey);
            }
        }
        catch { }

        return new Dictionary<string, object?>
        {
            ["type"] = "map",
            ["context"] = RunContext(),
            ["rows"] = rows,
            ["boss"] = bossNode,
            ["current_coord"] = currentCoord.HasValue ? new Dictionary<string, object?>
            {
                ["col"] = (int)currentCoord.Value.col,
                ["row"] = (int)currentCoord.Value.row,
            } : null,
        };
    }

    public void CleanUp()
    {
        try
        {
            if (RunManager.Instance.IsInProgress)
                RunManager.Instance.CleanUp(graceful: true);
            _runState = null;
        }
        catch (Exception ex)
        {
            Log($"CleanUp exception: {ex.Message}");
        }
    }

    #endregion
}
