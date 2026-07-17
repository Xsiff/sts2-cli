using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2Headless;

public partial class RunSimulator
{
    #region Actions

    private Dictionary<string, object?> DoMapSelect(Player player, Dictionary<string, object?>? args)
    {
        if (args == null || !args.ContainsKey("col") || !args.ContainsKey("row"))
            return Error("select_map_node requires 'col' and 'row'");

        _rewardsProcessed = false;
        _pendingCardReward = null;
        _eventOptionChosen = false;
        _lastEventOptionCount = 0;
        _pendingRewards = null;
        _lastKnownHp = player.Creature?.CurrentHp ?? 0;

        var col = Convert.ToInt32(args["col"]);
        var row = Convert.ToInt32(args["row"]);
        var coord = new MapCoord((byte)col, (byte)row);

        Log($"Moving to map coord ({col},{row})");

        WaitForActionExecutor();
        _syncCtx.Pump();

        RunManager.Instance.EnterMapCoord(coord).GetAwaiter().GetResult();
        _syncCtx.Pump();
        WaitForActionExecutor();

        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoPlayCard(Player player, Dictionary<string, object?>? args)
    {
        if (args == null || !args.ContainsKey("card_index"))
            return Error("play_card requires 'card_index'");

        var cardIndex = Convert.ToInt32(args["card_index"]);
        var pcs = player.PlayerCombatState;
        if (pcs == null)
            return Error("Not in combat");

        var hand = pcs.Hand.Cards;
        if (cardIndex < 0 || cardIndex >= hand.Count)
            return Error($"Invalid card index {cardIndex}, hand has {hand.Count} cards");

        var card = hand[cardIndex];

        Creature? target = null;
        var cardTargetType = card.TargetType.ToString();
        if (string.Equals(cardTargetType, "AnyEnemy", StringComparison.Ordinal))
        {
            if (args.TryGetValue("target_index", out var targetObj) && targetObj != null)
            {
                var targetIndex = Convert.ToInt32(targetObj);
                var state = CombatManager.Instance.DebugOnlyGetState();
                if (state != null)
                {
                    var enemies = state.Enemies.Where(e => e != null && e.IsAlive).ToList();
                    if (targetIndex >= 0 && targetIndex < enemies.Count)
                        target = enemies[targetIndex];
                }
            }

            if (target == null)
            {
                var state = CombatManager.Instance.DebugOnlyGetState();
                target = state?.Enemies?.FirstOrDefault(e => e != null && e.IsAlive);
            }
        }

        if (!card.CanPlay(out var reason, out var _))
        {
            return Error($"Cannot play card {card.GetType().Name}: {reason}");
        }

        Log($"Playing card {card.GetType().Name} (index {cardIndex}) targeting {(target != null ? target.Monster?.GetType().Name ?? "creature" : "none")}");

        var handCountBefore = hand.Count;

        var playAction = new PlayCardAction(card, target);
        RunManager.Instance.ActionQueueSet.EnqueueWithoutSynchronizing(playAction);
        WaitForActionExecutor();

        var handAfter = pcs.Hand.Cards;
        if (handAfter.Count == handCountBefore && cardIndex < handAfter.Count && handAfter[cardIndex] == card)
        {
            return Error($"Card could not be played (still in hand after action): {card.GetType().Name} [{card.Id}]");
        }

        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoEndTurn(Player player)
    {
        if (!CombatManager.Instance.IsPlayPhase)
        {
            _syncCtx.Pump();
            if (!CombatManager.Instance.IsPlayPhase)
            {
                if (!CombatManager.Instance.IsInProgress || player.Creature.IsDead)
                    return DetectDecisionPoint();
                Thread.Sleep(100);
                _syncCtx.Pump();
                if (!CombatManager.Instance.IsPlayPhase)
                    return DetectDecisionPoint();
            }
        }

        WaitForActionExecutor();

        Log($"Ending turn (round={CombatManager.Instance.DebugOnlyGetState()?.RoundNumber ?? 0})");
        _turnStarted.Reset();
        _combatEnded.Reset();

        YieldPatches.SuppressYield = true;
        try
        {
            PlayerCmd.EndTurn(player, canBackOut: false);
            _syncCtx.Pump();

            if (CombatManager.Instance.IsInProgress && !CombatManager.Instance.IsPlayPhase && !player.Creature.IsDead)
            {
                for (int i = 0; i < 50; i++)
                {
                    _syncCtx.Pump();
                    if (_turnStarted.IsSet || _combatEnded.IsSet) break;
                    if (!CombatManager.Instance.IsInProgress || player.Creature.IsDead) break;
                    if (CombatManager.Instance.IsPlayPhase) break;
                    Thread.Sleep(5);
                }
            }
        }
        finally
        {
            YieldPatches.SuppressYield = false;
        }

        if (CombatManager.Instance.IsInProgress && !CombatManager.Instance.IsPlayPhase && !player.Creature.IsDead)
        {
            Log("EndTurn stuck, cancelling and retrying with SuppressYield...");
            try
            {
                RunManager.Instance.ActionExecutor.Cancel();
                _syncCtx.Pump();
                Thread.Sleep(50);
                _syncCtx.Pump();

                CombatManager.Instance.UndoReadyToEndTurn(player);
                _syncCtx.Pump();

                YieldPatches.SuppressYield = true;
                try
                {
                    PlayerCmd.EndTurn(player, canBackOut: false);
                    _syncCtx.Pump();
                }
                finally
                {
                    YieldPatches.SuppressYield = false;
                }

                for (int i = 0; i < 100; i++)
                {
                    _syncCtx.Pump();
                    if (_turnStarted.IsSet || _combatEnded.IsSet) break;
                    if (!CombatManager.Instance.IsInProgress || player.Creature.IsDead) break;
                    if (CombatManager.Instance.IsPlayPhase) break;
                    Thread.Sleep(10);
                }
            }
            catch (Exception ex)
            {
                Log($"Cancel retry: {ex.Message}");
            }

            if (CombatManager.Instance.IsInProgress && !CombatManager.Instance.IsPlayPhase && !player.Creature.IsDead)
            {
                var stuckState = CombatManager.Instance.DebugOnlyGetState();
                var stuckEnemies = stuckState?.Enemies?.Where(e => e != null && e.IsAlive)
                    .Select(e => $"{e.Monster?.GetType().Name}(hp={e.CurrentHp})").ToList();
                Log($"EndTurn STILL stuck after retry — nuclear fallback. Round={stuckState?.RoundNumber}, " +
                    $"Enemies=[{string.Join(",", stuckEnemies ?? new())}], " +
                    $"IsPlayPhase={CombatManager.Instance.IsPlayPhase}, " +
                    $"IsInProgress={CombatManager.Instance.IsInProgress}, " +
                    $"ActionExecutor.IsRunning={RunManager.Instance.ActionExecutor.IsRunning}");
                try
                {
                    RunManager.Instance.ActionExecutor.Cancel();
                    _syncCtx.Pump();
                    CombatManager.Instance.UndoReadyToEndTurn(player);
                    _syncCtx.Pump();
                    Thread.Sleep(50);

                    YieldPatches.SuppressYield = true;
                    var endTurnTask = Task.Run(() =>
                    {
                        PlayerCmd.EndTurn(player, canBackOut: false);
                    });

                    for (int i = 0; i < 500; i++)
                    {
                        _syncCtx.Pump();
                        if (endTurnTask.IsCompleted) break;
                        if (_turnStarted.IsSet || _combatEnded.IsSet) break;
                        if (!CombatManager.Instance.IsInProgress || player.Creature.IsDead) break;
                        if (CombatManager.Instance.IsPlayPhase) break;
                        Thread.Sleep(10);
                    }
                    YieldPatches.SuppressYield = false;

                    if (CombatManager.Instance.IsInProgress && !CombatManager.Instance.IsPlayPhase && !player.Creature.IsDead)
                    {
                        for (int i = 0; i < 200; i++)
                        {
                            _syncCtx.Pump();
                            Thread.Sleep(10);
                            if (CombatManager.Instance.IsPlayPhase || !CombatManager.Instance.IsInProgress || player.Creature.IsDead)
                                break;
                        }
                    }

                    if (CombatManager.Instance.IsPlayPhase)
                        Log("Nuclear fallback SUCCEEDED — play phase resumed");
                    else
                    {
                        Log("Nuclear fallback FAILED — forcing game_over to escape deadlock");
                        return GameOverState(false);
                    }
                }
                catch (Exception ex)
                {
                    Log($"Nuclear fallback error: {ex.Message}");
                    YieldPatches.SuppressYield = false;
                }
            }
        }

        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoSelectCardReward(Player player, Dictionary<string, object?>? args)
    {
        if (_cardSelector.HasPendingReward)
        {
            if (args == null || !args.ContainsKey("card_index"))
                return Error("select_card_reward requires 'card_index'");
            var idx = Convert.ToInt32(args["card_index"]);
            Log($"Resolving event card reward: index {idx}");
            _cardSelector.ResolveReward(idx);
            Thread.Sleep(50);
            _syncCtx.Pump();
            WaitForActionExecutor();
            return DetectDecisionPoint();
        }

        if (_pendingCardReward == null)
            return Error("No pending card reward");
        if (args == null || !args.ContainsKey("card_index"))
            return Error("select_card_reward requires 'card_index'");

        var cardIndex = Convert.ToInt32(args["card_index"]);
        var cards = _pendingCardReward.Cards.ToList();
        if (cardIndex < 0 || cardIndex >= cards.Count)
            return Error($"Invalid card index {cardIndex}, {cards.Count} cards available");

        var card = cards[cardIndex];
        Log($"Selected card reward: {card.GetType().Name}");

        try
        {
            MegaCrit.Sts2.Core.Commands.CardPileCmd
                .Add(card, MegaCrit.Sts2.Core.Entities.Cards.PileType.Deck)
                .GetAwaiter().GetResult();
            _syncCtx.Pump();
            RunManager.Instance.RewardSynchronizer.SyncLocalObtainedCard(card);
        }
        catch (Exception ex)
        {
            Log($"Add card to deck: {ex.Message}");
        }

        _pendingCardReward = null;
        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoSkipCardReward(Player player)
    {
        if (_cardSelector.HasPendingReward)
        {
            Log("Skipping event card reward");
            _cardSelector.SkipReward();
            Thread.Sleep(50);
            _syncCtx.Pump();
            WaitForActionExecutor();
            return DetectDecisionPoint();
        }

        if (_pendingCardReward != null)
        {
            Log("Skipping card reward");
            _pendingCardReward.OnSkipped();
            _pendingCardReward = null;
        }

        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoBuyCard(Player player, Dictionary<string, object?>? args)
    {
        if (_runState?.CurrentRoom is not MerchantRoom merchantRoom)
            return Error("Not in a shop");
        if (args == null || !args.ContainsKey("card_index"))
            return Error("buy_card requires 'card_index'");

        var idx = Convert.ToInt32(args["card_index"]);
        var allEntries = merchantRoom.Inventory.CharacterCardEntries
            .Concat(merchantRoom.Inventory.ColorlessCardEntries).ToList();
        if (idx < 0 || idx >= allEntries.Count)
            return Error($"Invalid card index {idx}");

        var entry = allEntries[idx];
        if (!entry.IsStocked) return Error("Card already purchased");
        if (player.Gold < entry.Cost) return Error("Not enough gold");

        try
        {
            entry.OnTryPurchaseWrapper(merchantRoom.Inventory).GetAwaiter().GetResult();
            _syncCtx.Pump();
            Log($"Bought card: {entry.CreationResult?.Card?.GetType().Name ?? "?"} for {entry.Cost}g");
        }
        catch (Exception ex)
        {
            return Error($"Buy card failed: {ex.Message}");
        }

        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoBuyRelic(Player player, Dictionary<string, object?>? args)
    {
        if (_runState?.CurrentRoom is not MerchantRoom merchantRoom)
            return Error("Not in a shop");
        if (args == null || !args.ContainsKey("relic_index"))
            return Error("buy_relic requires 'relic_index'");

        var idx = Convert.ToInt32(args["relic_index"]);
        var entries = merchantRoom.Inventory.RelicEntries;
        if (idx < 0 || idx >= entries.Count) return Error($"Invalid relic index {idx}");

        var entry = entries[idx];
        if (!entry.IsStocked) return Error("Relic already purchased");
        if (player.Gold < entry.Cost) return Error("Not enough gold");

        try
        {
            entry.OnTryPurchaseWrapper(merchantRoom.Inventory).GetAwaiter().GetResult();
            _syncCtx.Pump();
            Log($"Bought relic: {entry.Model.GetType().Name} for {entry.Cost}g");
        }
        catch (Exception ex)
        {
            return Error($"Buy relic failed: {ex.Message}");
        }

        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoBuyPotion(Player player, Dictionary<string, object?>? args)
    {
        if (_runState?.CurrentRoom is not MerchantRoom merchantRoom)
            return Error("Not in a shop");
        if (args == null || !args.ContainsKey("potion_index"))
            return Error("buy_potion requires 'potion_index'");

        var idx = Convert.ToInt32(args["potion_index"]);
        var entries = merchantRoom.Inventory.PotionEntries;
        if (idx < 0 || idx >= entries.Count) return Error($"Invalid potion index {idx}");

        var entry = entries[idx];
        if (!entry.IsStocked) return Error("Potion already purchased");
        if (player.Gold < entry.Cost) return Error("Not enough gold");

        try
        {
            entry.OnTryPurchaseWrapper(merchantRoom.Inventory).GetAwaiter().GetResult();
            _syncCtx.Pump();
            Log($"Bought potion: {entry.Model.GetType().Name} for {entry.Cost}g");
        }
        catch (Exception ex)
        {
            Log($"Buy potion failed: {ex.Message}");
        }

        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoRemoveCard(Player player)
    {
        if (_runState?.CurrentRoom is not MerchantRoom merchantRoom)
            return Error("Not in a shop");

        var removal = merchantRoom.Inventory.CardRemovalEntry;
        if (removal == null) return Error("No card removal available");
        if (player.Gold < removal.Cost) return Error("Not enough gold");

        try
        {
            var task = Task.Run(() => removal.OnTryPurchaseWrapper(merchantRoom.Inventory));
            for (int i = 0; i < 100; i++)
            {
                _syncCtx.Pump();
                if (_cardSelector.HasPending) break;
                if (task.IsCompleted) break;
                Thread.Sleep(10);
            }
            if (_cardSelector.HasPending)
            {
                WaitForActionExecutor();
                return DetectDecisionPoint();
            }
            if (!task.IsCompleted) task.Wait(2000);
            _syncCtx.Pump();
            Log($"Removed card for {removal.Cost}g");
        }
        catch (Exception ex)
        {
            return Error($"Remove card failed: {ex.Message}");
        }

        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoSelectBundle(Player player, Dictionary<string, object?>? args)
    {
        if (_pendingBundleTcs == null || _pendingBundles == null)
            return Error("No pending bundle selection");
        if (args == null || !args.ContainsKey("bundle_index"))
            return Error("select_bundle requires 'bundle_index'");

        var idx = Convert.ToInt32(args["bundle_index"]);
        Log($"Bundle selection: pack {idx}");
        var bundles = _pendingBundles;
        var tcs = _pendingBundleTcs;
        _pendingBundles = null;
        _pendingBundleTcs = null;

        var selected = (idx >= 0 && idx < bundles.Count) ? bundles[idx] : bundles[0];
        tcs.TrySetResult(selected);

        _syncCtx.Pump();
        WaitForActionExecutor();
        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoSelectCards(Player player, Dictionary<string, object?>? args)
    {
        if (!_cardSelector.HasPending)
            return Error("No pending card selection");
        if (args == null || !args.ContainsKey("indices"))
            return Error("select_cards requires 'indices' (comma-separated card indices)");

        var indicesStr = args["indices"]?.ToString() ?? "";
        var indices = indicesStr.Split(',')
            .Select(s => int.TryParse(s.Trim(), out var v) ? v : -1)
            .Where(i => i >= 0)
            .ToArray();

        Log($"Card selection: indices [{string.Join(",", indices)}]");
        _cardSelector.ResolvePendingByIndices(indices);
        _syncCtx.Pump();
        WaitForActionExecutor();

        if (_runState?.CurrentRoom is RestSiteRoom)
        {
            Thread.Sleep(200);
            _syncCtx.Pump();
            WaitForActionExecutor();
            Log("Card selection in rest site (SMITH), forcing to map");
            ForceToMap();
            return MapSelectState();
        }

        if (_runState?.CurrentRoom is MerchantRoom)
        {
            Thread.Sleep(200);
            _syncCtx.Pump();
            WaitForActionExecutor();
            Log("Card selection in shop (card removal), refreshing shop state");
        }

        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoSkipSelect(Player player)
    {
        if (_cardSelector.HasPending)
        {
            Log("Skipping card selection");
            _cardSelector.CancelPending();
            _syncCtx.Pump();
            WaitForActionExecutor();
        }
        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoUsePotion(Player player, Dictionary<string, object?>? args)
    {
        if (args == null || !args.ContainsKey("potion_index"))
            return Error("use_potion requires 'potion_index'");

        var idx = Convert.ToInt32(args["potion_index"]);
        var potionsList = player.Potions?.ToList() ?? new();
        if (idx < 0 || idx >= potionsList.Count) return Error($"Invalid potion index {idx}");
        var potion = potionsList[idx];
        if (potion == null) return Error($"No potion at index {idx}");

        Creature? target = null;
        var potionTargetType = potion.TargetType.ToString();

        if (string.Equals(potionTargetType, "Self", StringComparison.Ordinal) ||
            string.Equals(potionTargetType, "TargetedNoCreature", StringComparison.Ordinal))
        {
            target = player.Creature;
        }
        else if (string.Equals(potionTargetType, "AnyEnemy", StringComparison.Ordinal))
        {
            if (args.TryGetValue("target_index", out var tObj) && tObj != null)
            {
                var targetIdx = Convert.ToInt32(tObj);
                var combatState = CombatManager.Instance.DebugOnlyGetState();
                if (combatState != null)
                {
                    var enemies = combatState.Enemies.Where(e => e != null && e.IsAlive).ToList();
                    if (targetIdx >= 0 && targetIdx < enemies.Count)
                        target = enemies[targetIdx];
                }
            }
            if (target == null && CombatManager.Instance.IsInProgress)
            {
                var combatState = CombatManager.Instance.DebugOnlyGetState();
                target = combatState?.Enemies?.FirstOrDefault(e => e != null && e.IsAlive);
            }
        }

        Log($"Using potion: {potion.GetType().Name} at slot {idx} target={target?.GetType().Name ?? "none"}");
        try
        {
            var action = new MegaCrit.Sts2.Core.GameActions.UsePotionAction(potion, target, CombatManager.Instance.IsInProgress);
            RunManager.Instance.ActionQueueSet.EnqueueWithoutSynchronizing(action);
            WaitForActionExecutor();
            _syncCtx.Pump();

            if (_cardSelector.HasPending || _cardSelector.HasPendingReward)
                return DetectDecisionPoint();

            var afterPotions = player.Potions?.ToList() ?? new();
            if (afterPotions.Contains(potion))
            {
                Log("Potion not consumed by action, manually discarding");
                MegaCrit.Sts2.Core.Commands.PotionCmd.Discard(potion).GetAwaiter().GetResult();
                _syncCtx.Pump();
            }
        }
        catch (Exception ex)
        {
            Log($"Use potion failed: {ex.Message}");
            try
            {
                MegaCrit.Sts2.Core.Commands.PotionCmd.Discard(potion).GetAwaiter().GetResult();
            }
            catch
            {
            }
        }

        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoDiscardPotion(Player player, Dictionary<string, object?>? args)
    {
        if (args == null || !args.ContainsKey("potion_index"))
            return Error("discard_potion requires 'potion_index'");

        var idx = Convert.ToInt32(args["potion_index"]);
        var potionsList = player.Potions?.ToList() ?? new();
        if (idx < 0 || idx >= potionsList.Count) return Error($"Invalid potion index {idx}");
        var potion = potionsList[idx];
        if (potion == null) return Error($"No potion at index {idx}");

        MegaCrit.Sts2.Core.Commands.PotionCmd.Discard(potion).GetAwaiter().GetResult();
        _syncCtx.Pump();
        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoChooseOption(Player player, Dictionary<string, object?>? args)
    {
        if (args == null || !args.ContainsKey("option_index"))
            return Error("choose_option requires 'option_index'");

        var optionIndex = Convert.ToInt32(args["option_index"]);
        Log($"Choosing option {optionIndex}");

        if (_runState?.CurrentRoom is RestSiteRoom restSiteRoom)
        {
            Log($"Rest site: choosing option {optionIndex}");
            try
            {
                var task = Task.Run(() => RunManager.Instance.RestSiteSynchronizer.ChooseLocalOption(optionIndex));
                for (int i = 0; i < 100; i++)
                {
                    _syncCtx.Pump();
                    if (_cardSelector.HasPending) break;
                    if (task.IsCompleted) break;
                    Thread.Sleep(10);
                }
                if (_cardSelector.HasPending)
                {
                    WaitForActionExecutor();
                    return DetectDecisionPoint();
                }
                if (!task.IsCompleted) task.Wait(2000);
                _syncCtx.Pump();
            }
            catch (Exception ex)
            {
                Log($"Rest site ChooseLocalOption failed: {ex.Message}");
            }

            if (!_cardSelector.HasPending)
            {
                Log("Rest site: option chosen (non-Smith), waiting for action then forcing to map");
                WaitForActionExecutor();
                _syncCtx.Pump();
                Thread.Sleep(200);
                _syncCtx.Pump();
                WaitForActionExecutor();
                ForceToMap();
                return MapSelectState();
            }
        }
        else if (_runState?.CurrentRoom is EventRoom)
        {
            var eventSync = RunManager.Instance.EventSynchronizer;
            var localEvent = eventSync?.GetLocalEvent();
            if (localEvent != null && !localEvent.IsFinished)
            {
                var options = localEvent.CurrentOptions;
                var optCountBefore = options?.Count ?? 0;
                if (options != null && optionIndex >= 0 && optionIndex < options.Count)
                {
                    try
                    {
                        _eventOptionChosen = true;
                        _lastEventOptionCount = options.Count;
                        var task = Task.Run(() => options[optionIndex].Chosen());
                        for (int i = 0; i < 100; i++)
                        {
                            _syncCtx.Pump();
                            if (_cardSelector.HasPending || _cardSelector.HasPendingReward) break;
                            if (_pendingBundles != null) break;
                            if (task.IsCompleted) break;
                            Thread.Sleep(10);
                        }
                        if (_cardSelector.HasPending || _cardSelector.HasPendingReward || _pendingBundles != null)
                        {
                            WaitForActionExecutor();
                            return DetectDecisionPoint();
                        }
                        if (!task.IsCompleted) task.Wait(2000);
                        _syncCtx.Pump();
                    }
                    catch (Exception ex)
                    {
                        Log($"Event choose: {ex.Message}");
                    }
                }

                var optCountAfter = localEvent.CurrentOptions?.Count ?? 0;
                if (!localEvent.IsFinished && optCountAfter == optCountBefore && optCountAfter > 0)
                {
                    Log($"Event {localEvent.GetType().Name} didn't advance, force-finishing");
                    ForceToMap();
                }
            }
        }

        WaitForActionExecutor();
        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoLeaveRoom(Player player)
    {
        Log("Leaving room");
        try
        {
            RunManager.Instance.ProceedFromTerminalRewardsScreen().GetAwaiter().GetResult();
        }
        catch
        {
        }
        _syncCtx.Pump();
        WaitForActionExecutor();

        var room = _runState?.CurrentRoom;
        if (room is RestSiteRoom || room is MerchantRoom || room is EventRoom || room is TreasureRoom)
        {
            Log("Force leaving non-combat room to map");
            try
            {
                RunManager.Instance.EnterRoom(new MapRoom()).GetAwaiter().GetResult();
                _syncCtx.Pump();
                WaitForActionExecutor();
            }
            catch (Exception ex)
            {
                Log($"Force leave: {ex.Message}");
            }
        }
        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoProceed(Player player)
    {
        Log("Proceeding");

        var room = _runState?.CurrentRoom;
        if (room is CombatRoom combatRoom && combatRoom.RoomType == RoomType.Boss)
        {
            if (combatRoom.IsPreFinished || !CombatManager.Instance.IsInProgress)
            {
                RunManager.Instance.EnterNextAct().GetAwaiter().GetResult();
                WaitForActionExecutor();
                return DetectDecisionPoint();
            }
        }

        RunManager.Instance.ProceedFromTerminalRewardsScreen().GetAwaiter().GetResult();
        WaitForActionExecutor();
        return DetectDecisionPoint();
    }

    #endregion
}
