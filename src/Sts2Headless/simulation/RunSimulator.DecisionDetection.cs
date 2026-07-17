using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2Headless;

public partial class RunSimulator
{
    #region Decision Point Detection

    private Dictionary<string, object?> DetectDecisionPoint()
    {
        if (_runState == null)
            return Error("No run in progress");

        var player = _runState.Players[0];

        if (player.Creature != null && player.Creature.IsDead)
        {
            return GameOverState(false);
        }

        if (_pendingBundles != null && _pendingBundleTcs != null && !_pendingBundleTcs.Task.IsCompleted)
        {
            var bundles = _pendingBundles.Select((bundle, i) => new Dictionary<string, object?>
            {
                ["index"] = i,
                ["cards"] = bundle.Select(card =>
                {
                    var stats = new Dictionary<string, object?>();
                    try { foreach (var dv in card.DynamicVars.Values) stats[dv.Name.ToLowerInvariant()] = (int)dv.BaseValue; } catch { }
                    var bkws = card.Keywords?.Where(k => k != CardKeyword.None).Select(k => k.ToString()).ToList();
                    return new Dictionary<string, object?>
                    {
                        ["name"] = _loc.Card(card.Id.Entry),
                        ["cost"] = card.EnergyCost?.GetResolved() ?? 0,
                        ["type"] = card.Type.ToString(),
                        ["rarity"] = card.Rarity.ToString(),
                        ["description"] = _loc.Localized("cards", card.Id.Entry + ".description"),
                        ["stats"] = stats.Count > 0 ? stats : null,
                        ["keywords"] = bkws?.Count > 0 ? bkws : null,
                    };
                }).ToList(),
            }).ToList();

            return new Dictionary<string, object?>
            {
                ["type"] = "decision",
                ["decision"] = "bundle_select",
                ["context"] = RunContext(),
                ["bundles"] = bundles,
                ["player"] = PlayerSummary(player),
            };
        }

        if (_cardSelector.HasPendingReward)
        {
            var rewardCards = _cardSelector.PendingRewardCards!;
            var cards = rewardCards.Select((cr, i) =>
            {
                var stats = new Dictionary<string, object?>();
                try { foreach (var dv in cr.Card.DynamicVars.Values) stats[dv.Name.ToLowerInvariant()] = (int)dv.BaseValue; } catch { }
                var rrkws = cr.Card.Keywords?.Where(k => k != CardKeyword.None).Select(k => k.ToString()).ToList();
                return new Dictionary<string, object?>
                {
                    ["index"] = i,
                    ["id"] = cr.Card.Id.ToString(),
                    ["name"] = _loc.Card(cr.Card.Id.Entry),
                    ["cost"] = cr.Card.EnergyCost?.GetResolved() ?? 0,
                    ["type"] = cr.Card.Type.ToString(),
                    ["rarity"] = cr.Card.Rarity.ToString(),
                    ["description"] = _loc.Localized("cards", cr.Card.Id.Entry + ".description"),
                    ["stats"] = stats.Count > 0 ? stats : null,
                    ["keywords"] = rrkws?.Count > 0 ? rrkws : null,
                    ["after_upgrade"] = GetUpgradedInfo(cr.Card),
                };
            }).ToList();

            return new Dictionary<string, object?>
            {
                ["type"] = "decision",
                ["decision"] = "card_reward",
                ["context"] = RunContext(),
                ["cards"] = cards,
                ["can_skip"] = true,
                ["from_event"] = true,
                ["player"] = PlayerSummary(_runState.Players[0]),
            };
        }

    checkCardSelect:
        if (_cardSelector.HasPending && _cardSelector.PendingOptions != null)
        {
            var opts = _cardSelector.PendingOptions.Select((card, i) =>
            {
                var stats = new Dictionary<string, object?>();
                try { foreach (var dv in card.DynamicVars.Values) stats[dv.Name.ToLowerInvariant()] = (int)dv.BaseValue; } catch { }
                var selkws = card.Keywords?.Where(k => k != CardKeyword.None).Select(k => k.ToString()).ToList();
                return new Dictionary<string, object?>
                {
                    ["index"] = i,
                    ["id"] = card.Id.ToString(),
                    ["name"] = _loc.Card(card.Id.Entry),
                    ["cost"] = card.EnergyCost?.GetResolved() ?? 0,
                    ["type"] = card.Type.ToString(),
                    ["rarity"] = card.Rarity.ToString(),
                    ["upgraded"] = card.IsUpgraded,
                    ["stats"] = stats.Count > 0 ? stats : null,
                    ["description"] = _loc.Localized("cards", card.Id.Entry + ".description"),
                    ["keywords"] = selkws?.Count > 0 ? selkws : null,
                    ["after_upgrade"] = GetUpgradedInfo(card),
                };
            }).ToList();

            return new Dictionary<string, object?>
            {
                ["type"] = "decision",
                ["decision"] = "card_select",
                ["context"] = RunContext(),
                ["cards"] = opts,
                ["min_select"] = _cardSelector.PendingMinSelect,
                ["max_select"] = _cardSelector.PendingMaxSelect,
                ["player"] = PlayerSummary(player),
            };
        }

        if (_pendingCardReward != null)
        {
            return CardRewardState(player, _runState.CurrentRoom as CombatRoom);
        }

        if (RunManager.Instance.IsGameOver)
        {
            return GameOverState(true);
        }

        var room = _runState.CurrentRoom;

        if (room is MapRoom || room == null)
        {
            return MapSelectState();
        }

        if (room is CombatRoom combatRoom)
        {
            _syncCtx.Pump();
            WaitForActionExecutor();

            if (_cardSelector.HasPending && _cardSelector.PendingOptions != null)
            {
                goto checkCardSelect;
            }

            if (CombatManager.Instance.IsInProgress && CombatManager.Instance.IsPlayPhase)
            {
                return CombatPlayState(player);
            }
            if (!CombatManager.Instance.IsInProgress || (player.Creature != null && player.Creature.IsDead))
            {
                return DetectPostCombatState(player, combatRoom);
            }
            for (int i = 0; i < 20; i++)
            {
                _syncCtx.Pump();
                Thread.Sleep(5);
                if (CombatManager.Instance.IsPlayPhase) return CombatPlayState(player);
                if (!CombatManager.Instance.IsInProgress) return DetectPostCombatState(player, combatRoom);
            }
            return CombatPlayState(player);
        }

        if (room is EventRoom eventRoom)
        {
            return EventChoiceState(eventRoom);
        }

        if (room is RestSiteRoom restRoom)
        {
            return RestSiteState(restRoom);
        }

        if (room is MerchantRoom merchantRoom)
        {
            return ShopState(merchantRoom, player);
        }

        if (room is TreasureRoom treasureRoom)
        {
            return TreasureState(treasureRoom);
        }

        return new Dictionary<string, object?>
        {
            ["type"] = "decision",
            ["decision"] = "unknown",
            ["context"] = RunContext(),
            ["room_type"] = room?.GetType().Name,
            ["message"] = "Unknown room type or state",
        };
    }

    private Dictionary<string, object?> MapSelectState()
    {
        var map = _runState?.Map;
        if (map == null)
        {
            Log("Map is null, generating...");
            try
            {
                RunManager.Instance.GenerateMap().GetAwaiter().GetResult();
                _syncCtx.Pump();
                map = _runState?.Map;
            }
            catch (Exception ex)
            {
                Log($"GenerateMap failed: {ex.Message}");
            }
            if (map == null)
                return Error("No map available");
        }
        var currentCoord = _runState!.CurrentMapCoord;

        List<Dictionary<string, object?>> choices;
        if (currentCoord.HasValue)
        {
            var currentPoint = map.GetPoint(currentCoord.Value);
            if (currentPoint == null)
            {
                Log($"GetPoint returned null for coord ({currentCoord.Value.col},{currentCoord.Value.row}), falling back to start");
                choices = new List<Dictionary<string, object?>>();
                var sp = map.StartingMapPoint;
                if (sp?.Children != null)
                {
                    foreach (var child in sp.Children)
                    {
                        choices.Add(new Dictionary<string, object?>
                        {
                            ["col"] = (int)child.coord.col,
                            ["row"] = (int)child.coord.row,
                            ["type"] = child.PointType.ToString(),
                        });
                    }
                }
            }
            else
            {
                choices = (currentPoint.Children ?? Enumerable.Empty<MapPoint>())
                    .Select(child => new Dictionary<string, object?>
                    {
                        ["col"] = (int)child.coord.col,
                        ["row"] = (int)child.coord.row,
                        ["type"] = child.PointType.ToString(),
                    })
                    .ToList();
            }
        }
        else
        {
            var startPoint = map.StartingMapPoint;
            choices = new List<Dictionary<string, object?>>
            {
                new()
                {
                    ["col"] = (int)startPoint.coord.col,
                    ["row"] = (int)startPoint.coord.row,
                    ["type"] = startPoint.PointType.ToString(),
                }
            };
            if (startPoint.Children != null)
            {
                foreach (var child in startPoint.Children)
                {
                    choices.Add(new Dictionary<string, object?>
                    {
                        ["col"] = (int)child.coord.col,
                        ["row"] = (int)child.coord.row,
                        ["type"] = child.PointType.ToString(),
                    });
                }
            }
        }

        return new Dictionary<string, object?>
        {
            ["type"] = "decision",
            ["decision"] = "map_select",
            ["context"] = RunContext(),
            ["choices"] = choices,
            ["player"] = PlayerSummary(_runState.Players[0]),
            ["act"] = _runState.CurrentActIndex + 1,
            ["act_name"] = _loc.Act(_runState.Act?.Id.Entry ?? "OVERGROWTH"),
            ["floor"] = _runState.ActFloor,
        };
    }

    private Dictionary<string, object?> CombatPlayState(Player player)
    {
        var pcs = player.PlayerCombatState;
        var combatState = CombatManager.Instance.DebugOnlyGetState();

        if (player.Creature != null && player.Creature.CurrentHp > 0)
            _lastKnownHp = player.Creature.CurrentHp;

        var hand = BuildCombatCardList(pcs?.Hand?.Cards, includeCanPlay: true, pcs);

        var playerCreatures = combatState?.PlayerCreatures?.ToList();

        var enemies = combatState?.Enemies?
            .Where(e => e != null && e.IsAlive)
            .Select((e, i) =>
            {
                var intents = new List<Dictionary<string, object?>>();
                try
                {
                    if (e.Monster?.NextMove?.Intents != null)
                    {
                        foreach (var intent in e.Monster.NextMove.Intents)
                        {
                            var intentInfo = new Dictionary<string, object?>
                            {
                                ["type"] = intent.IntentType.ToString(),
                            };
                            if (intent is MegaCrit.Sts2.Core.MonsterMoves.Intents.AttackIntent atk && playerCreatures != null)
                            {
                                try
                                {
                                    intentInfo["damage"] = atk.GetTotalDamage(playerCreatures, e);
                                    if (atk.Repeats > 1) intentInfo["hits"] = atk.Repeats;
                                }
                                catch { }
                            }
                            intents.Add(intentInfo);
                        }
                    }
                }
                catch { }

                var ePowers = e.Powers?.Select(pw => new Dictionary<string, object?>
                {
                    ["name"] = _loc.Power(pw.Id.Entry),
                    ["description"] = _loc.Localized("powers", pw.Id.Entry + ".description"),
                    ["amount"] = pw.Amount,
                }).ToList();

                return new Dictionary<string, object?>
                {
                    ["index"] = i,
                    ["name"] = _loc.Monster(e.Monster?.Id.Entry ?? "UNKNOWN"),
                    ["hp"] = e.CurrentHp,
                    ["max_hp"] = e.MaxHp,
                    ["block"] = e.Block,
                    ["intents"] = intents.Count > 0 ? intents : null,
                    ["intends_attack"] = e.Monster?.IntendsToAttack ?? false,
                    ["powers"] = ePowers?.Count > 0 ? ePowers : null,
                };
            }).ToList() ?? new();

        var playerPowers = player.Creature?.Powers?.Select(pw => new Dictionary<string, object?>
        {
            ["name"] = _loc.Power(pw.Id.Entry),
            ["description"] = _loc.Localized("powers", pw.Id.Entry + ".description"),
            ["amount"] = pw.Amount,
        }).ToList();

        var result = new Dictionary<string, object?>
        {
            ["type"] = "decision",
            ["decision"] = "combat_play",
            ["context"] = RunContext(),
            ["round"] = combatState?.RoundNumber ?? 0,
            ["energy"] = pcs?.Energy ?? 0,
            ["max_energy"] = pcs?.MaxEnergy ?? 0,
            ["hand"] = hand,
            ["enemies"] = enemies,
            ["player"] = PlayerSummary(player),
            ["player_powers"] = playerPowers?.Count > 0 ? playerPowers : null,
            ["draw_pile_count"] = pcs?.DrawPile?.Cards?.Count ?? 0,
            ["discard_pile_count"] = pcs?.DiscardPile?.Cards?.Count ?? 0,
            ["deck_state"] = new Dictionary<string, object?>
            {
                ["hand_count"] = pcs?.Hand?.Cards?.Count ?? 0,
                ["draw_pile_count"] = pcs?.DrawPile?.Cards?.Count ?? 0,
                ["discard_pile_count"] = pcs?.DiscardPile?.Cards?.Count ?? 0,
                ["exhaust_pile_count"] = GetCombatPileCount(pcs, "ExhaustPile"),
                ["hand"] = hand,
                ["draw_pile"] = BuildCombatCardList(pcs?.DrawPile?.Cards, includeCanPlay: false, pcs: null),
                ["discard_pile"] = BuildCombatCardList(pcs?.DiscardPile?.Cards, includeCanPlay: false, pcs: null),
                ["exhaust_pile"] = BuildCombatCardList(GetCombatPileCards(pcs, "ExhaustPile"), includeCanPlay: false, pcs: null),
            },
        };

        try
        {
            var orbQueue = pcs?.OrbQueue;
            if (orbQueue?.Orbs?.Count > 0)
            {
                result["orbs"] = orbQueue.Orbs.Select((orb, i) => new Dictionary<string, object?>
                {
                    ["index"] = i,
                    ["name"] = _loc.Localized("orbs", orb.Id.Entry + ".title"),
                    ["type"] = orb.GetType().Name.Replace("Orb", ""),
                    ["passive"] = (int)orb.PassiveVal,
                    ["evoke"] = (int)orb.EvokeVal,
                }).ToList();
                result["orb_slots"] = orbQueue.Capacity;
            }

            if (pcs != null && pcs.Stars >= 0 && player.Character?.Id.Entry == "REGENT")
            {
                result["stars"] = pcs.Stars;
            }

            var osty = player.Osty;
            if (osty != null)
            {
                result["osty"] = new Dictionary<string, object?>
                {
                    ["name"] = _loc.Monster(osty.Monster?.Id.Entry ?? "OSTY"),
                    ["hp"] = osty.CurrentHp,
                    ["max_hp"] = osty.MaxHp,
                    ["block"] = osty.Block,
                    ["alive"] = osty.IsAlive,
                };
            }
            else if (player.Character?.Id.Entry == "NECROBINDER")
            {
                result["osty"] = new Dictionary<string, object?> { ["alive"] = false };
            }
        }
        catch (Exception ex)
        {
            Log($"Character-specific data: {ex.Message}");
        }

        return result;
    }

    private List<Dictionary<string, object?>> BuildCombatCardList(
        IEnumerable<CardModel>? cards,
        bool includeCanPlay,
        PlayerCombatState? pcs)
    {
        return cards?.Select((c, i) => BuildCombatCardInfo(c, i, includeCanPlay, pcs)).ToList() ?? new();
    }

    private Dictionary<string, object?> BuildCombatCardInfo(
        CardModel c,
        int index,
        bool includeCanPlay,
        PlayerCombatState? pcs)
    {
        var stats = new Dictionary<string, object?>();
        try
        {
            foreach (var dv in c.DynamicVars.Values)
                stats[dv.Name.ToLowerInvariant()] = (int)dv.BaseValue;
        }
        catch { }

        var cardInfo = new Dictionary<string, object?>
        {
            ["index"] = index,
            ["id"] = c.Id.ToString(),
            ["name"] = _loc.Card(c.Id.Entry),
            ["cost"] = c.EnergyCost?.GetResolved() ?? 0,
            ["type"] = c.Type.ToString(),
            ["rarity"] = c.Rarity.ToString(),
            ["target_type"] = c.TargetType.ToString(),
            ["upgraded"] = c.IsUpgraded,
            ["description"] = _loc.Localized("cards", c.Id.Entry + ".description"),
            ["stats"] = stats.Count > 0 ? stats : null,
        };

        if (includeCanPlay)
            cardInfo["can_play"] = c.CanPlay(out _, out _);

        var starCost = c.CurrentStarCost;
        if (starCost > 0)
        {
            cardInfo["star_cost"] = starCost;
            if (includeCanPlay && pcs != null && pcs.Stars < starCost)
                cardInfo["can_play"] = false;
        }

        var kws = c.Keywords?.Where(k => k != CardKeyword.None).Select(k => k.ToString()).ToList();
        if (kws?.Count > 0) cardInfo["keywords"] = kws;

        if (c.Enchantment != null)
        {
            cardInfo["enchantment"] = _loc.Localized("enchantments", c.Enchantment.Id.Entry + ".title");
            try { if (c.Enchantment.Amount != 0) cardInfo["enchantment_amount"] = c.Enchantment.Amount; } catch { }
        }

        if (c.Affliction != null)
        {
            cardInfo["affliction"] = _loc.Localized("afflictions", c.Affliction.Id.Entry + ".title");
            try { if (c.Affliction.Amount != 0) cardInfo["affliction_amount"] = c.Affliction.Amount; } catch { }
        }

        return cardInfo;
    }

    private IEnumerable<CardModel>? GetCombatPileCards(PlayerCombatState? pcs, string pilePropertyName)
    {
        var pile = pcs?.GetType().GetProperty(pilePropertyName)?.GetValue(pcs);
        if (pile == null)
            return null;

        var cardsProp = pile.GetType().GetProperty("Cards");
        return cardsProp?.GetValue(pile) as IEnumerable<CardModel>;
    }

    private int GetCombatPileCount(PlayerCombatState? pcs, string pilePropertyName)
    {
        return GetCombatPileCards(pcs, pilePropertyName)?.Count() ?? 0;
    }

    private Dictionary<string, object?> DetectPostCombatState(Player player, CombatRoom combatRoom)
    {
        Log($"Post-combat: RoomType={combatRoom.RoomType}, IsPreFinished={combatRoom.IsPreFinished}");
        _syncCtx.Pump();

        if (_pendingRewards == null && !_rewardsProcessed)
        {
            _goldBeforeCombat = player.Gold;
            try
            {
                var rewardsSet = new RewardsSet(player).WithRewardsFromRoom(combatRoom);
                var rewards = rewardsSet.GenerateWithoutOffering().GetAwaiter().GetResult();
                _syncCtx.Pump();

                var cardRewards = new List<CardReward>();
                foreach (var reward in rewards)
                {
                    if (reward is GoldReward || reward is MegaCrit.Sts2.Core.Rewards.RelicReward
                        || reward is MegaCrit.Sts2.Core.Rewards.PotionReward)
                    {
                        try { reward.OnSelectWrapper().GetAwaiter().GetResult(); _syncCtx.Pump(); }
                        catch (Exception ex) { Log($"Auto-collect reward: {ex.Message}"); }
                    }
                    else if (reward is CardReward cr)
                    {
                        cardRewards.Add(cr);
                    }
                }

                if (cardRewards.Count > 0)
                {
                    _pendingCardReward = cardRewards[0];
                    _pendingRewards = rewards;
                    return CardRewardState(player, combatRoom);
                }

                _pendingRewards = null;
            }
            catch (Exception ex) { Log($"Generate rewards: {ex.Message}"); }
        }

        _pendingCardReward = null;
        _pendingRewards = null;
        _rewardsProcessed = true;

        if (combatRoom.RoomType == RoomType.Boss)
        {
            Log("Boss defeated, entering next act");
            try
            {
                RunManager.Instance.EnterNextAct().GetAwaiter().GetResult();
                _syncCtx.Pump();
                WaitForActionExecutor();
            }
            catch (Exception ex) { Log($"EnterNextAct: {ex.Message}"); }
            return DetectDecisionPoint();
        }

        ForceToMap();
        return MapSelectState();
    }

    private Dictionary<string, object?> CardRewardState(Player player, CombatRoom? combatRoom)
    {
        if (_pendingCardReward == null)
            return DetectPostCombatState(player, combatRoom ?? (_runState?.CurrentRoom as CombatRoom)!);

        var cards = _pendingCardReward.Cards.Select((c, i) =>
        {
            var stats = new Dictionary<string, object?>();
            try { foreach (var dv in c.DynamicVars.Values) stats[dv.Name.ToLowerInvariant()] = (int)dv.BaseValue; } catch { }
            var crkws = c.Keywords?.Where(k => k != CardKeyword.None).Select(k => k.ToString()).ToList();
            return new Dictionary<string, object?>
            {
                ["index"] = i,
                ["id"] = c.Id.ToString(),
                ["name"] = _loc.Card(c.Id.Entry),
                ["cost"] = c.EnergyCost?.GetResolved() ?? 0,
                ["type"] = c.Type.ToString(),
                ["rarity"] = c.Rarity.ToString(),
                ["description"] = _loc.Localized("cards", c.Id.Entry + ".description"),
                ["stats"] = stats.Count > 0 ? stats : null,
                ["keywords"] = crkws?.Count > 0 ? crkws : null,
                ["after_upgrade"] = GetUpgradedInfo(c),
            };
        }).ToList();

        return new Dictionary<string, object?>
        {
            ["type"] = "decision",
            ["decision"] = "card_reward",
            ["context"] = RunContext(),
            ["cards"] = cards,
            ["can_skip"] = _pendingCardReward.CanSkip,
            ["gold_earned"] = _runState!.Players[0].Gold - _goldBeforeCombat,
            ["player"] = PlayerSummary(_runState.Players[0]),
        };
    }

    private void ForceToMap()
    {
        try
        {
            RunManager.Instance.ProceedFromTerminalRewardsScreen().GetAwaiter().GetResult();
            _syncCtx.Pump();
        }
        catch { }

        if (_runState?.CurrentRoom is not MapRoom)
        {
            try { RunManager.Instance.EnterRoom(new MapRoom()).GetAwaiter().GetResult(); _syncCtx.Pump(); }
            catch (Exception ex) { Log($"ForceToMap: {ex.Message}"); }
        }
    }

    private Dictionary<string, object?> EventChoiceState(EventRoom eventRoom)
    {
        var localEvent = RunManager.Instance.EventSynchronizer?.GetLocalEvent();
        _syncCtx.Pump();

        if (_eventOptionChosen && localEvent != null && !localEvent.IsFinished)
        {
            var currentOpts = localEvent.CurrentOptions;
            var sameOptions = currentOpts != null && currentOpts.Count > 0 &&
                _lastEventOptionCount > 0 && currentOpts.Count == _lastEventOptionCount;
            if (sameOptions)
            {
                Log($"Event {localEvent.GetType().Name}: same options after choice, force-finishing");
                _eventOptionChosen = false;
                ForceToMap();
                return MapSelectState();
            }
            _eventOptionChosen = false;
        }

        if (localEvent == null || localEvent.IsFinished)
        {
            Log($"Event {localEvent?.GetType().Name ?? "null"} finished, proceeding");
            try
            {
                RunManager.Instance.ProceedFromTerminalRewardsScreen().GetAwaiter().GetResult();
                _syncCtx.Pump();
            }
            catch { }
            if (_runState?.CurrentRoom is EventRoom)
            {
                try { RunManager.Instance.EnterRoom(new MapRoom()).GetAwaiter().GetResult(); _syncCtx.Pump(); }
                catch { }
            }
            return _runState?.CurrentRoom is MapRoom ? MapSelectState() : DetectDecisionPoint();
        }

        var currentOptions = localEvent.CurrentOptions;
        if (currentOptions == null || currentOptions.Count == 0)
        {
            Log($"Event {localEvent.GetType().Name} has no options, auto-skipping");
            try { RunManager.Instance.EnterRoom(new MapRoom()).GetAwaiter().GetResult(); _syncCtx.Pump(); }
            catch { }
            return MapSelectState();
        }

        var options = currentOptions
            .Select((opt, i) =>
            {
                string? title = null;
                if (opt.Title != null)
                {
                    var t = _loc.Localized(opt.Title.LocTable, opt.Title.LocEntryKey);
                    if (t != opt.Title.LocEntryKey)
                        title = t;
                }
                if (title == null && opt.TextKey != null)
                {
                    var parts = opt.TextKey.Split('.');
                    var optionId = parts.Length > 0 ? parts[^1] : opt.TextKey;
                    var relic = _loc.Relic(optionId);
                    if (relic != optionId + ".title")
                        title = relic;
                    else
                    {
                        var card = _loc.Card(optionId);
                        if (card != optionId + ".title")
                            title = card;
                        else
                            title = optionId.Replace("_", " ");
                    }
                }
                title ??= $"option_{i}";

                string? optDesc = null;
                if (opt.Description != null && !string.IsNullOrEmpty(opt.Description.LocEntryKey))
                {
                    var d = _loc.Localized(opt.Description.LocTable, opt.Description.LocEntryKey);
                    if (d != opt.Description.LocEntryKey)
                        optDesc = d;
                }
                if (optDesc == null && opt.TextKey != null)
                {
                    var parts = opt.TextKey.Split('.');
                    var optionId = parts.Length > 0 ? parts[^1] : opt.TextKey;
                    var rd = _loc.Localized("relics", optionId + ".description");
                    if (rd != optionId + ".description")
                        optDesc = rd;
                }

                Dictionary<string, object?>? optVars = null;
                try
                {
                    if (localEvent.DynamicVars?.Values != null)
                    {
                        optVars = new Dictionary<string, object?>();
                        foreach (var dv in localEvent.DynamicVars.Values)
                            optVars[dv.Name] = (int)dv.BaseValue;
                    }
                }
                catch { }
                if (opt.TextKey != null)
                {
                    try
                    {
                        var parts = opt.TextKey.Split('.');
                        var optionId = parts.Length > 0 ? parts[^1] : opt.TextKey;
                        var relicModel = ModelDb.GetById<RelicModel>(new ModelId("RELIC", optionId));
                        if (relicModel != null)
                        {
                            optVars ??= new Dictionary<string, object?>();
                            var mutable = relicModel.ToMutable();
                            foreach (var dv in mutable.DynamicVars.Values)
                                optVars[dv.Name] = (int)dv.BaseValue;
                        }
                    }
                    catch { }
                }

                return new Dictionary<string, object?>
                {
                    ["index"] = i,
                    ["title"] = title,
                    ["description"] = optDesc,
                    ["text_key"] = opt.TextKey,
                    ["is_locked"] = opt.IsLocked,
                    ["vars"] = optVars?.Count > 0 ? optVars : null,
                };
            }).ToList();

        var eventEntry = localEvent.Id?.Entry ?? localEvent.GetType().Name.ToUpperInvariant();
        var eventName = _loc.Localized("ancients", eventEntry + ".title");
        if (eventName == eventEntry + ".title")
            eventName = _loc.Event(eventEntry);

        string? eventDesc = null;
        if (localEvent.Description != null)
        {
            var d = _loc.Localized(localEvent.Description.LocTable, localEvent.Description.LocEntryKey);
            if (d != localEvent.Description.LocEntryKey)
                eventDesc = d;
        }

        return new Dictionary<string, object?>
        {
            ["type"] = "decision",
            ["decision"] = "event_choice",
            ["context"] = RunContext(),
            ["event_name"] = eventName,
            ["description"] = eventDesc,
            ["options"] = options,
            ["player"] = PlayerSummary(_runState.Players[0]),
        };
    }

    private Dictionary<string, object?> RestSiteState(RestSiteRoom restRoom)
    {
        var options = restRoom.Options;
        var player = _runState!.Players[0];

        if (options == null || options.Count == 0)
        {
            Log("Rest site: options empty, proceeding to map");
            ForceToMap();
            return MapSelectState();
        }

        var optionList = options.Select((opt, i) => new Dictionary<string, object?>
        {
            ["index"] = i,
            ["option_id"] = opt.OptionId,
            ["name"] = opt.GetType().Name,
            ["is_enabled"] = opt.IsEnabled,
        }).ToList();

        return new Dictionary<string, object?>
        {
            ["type"] = "decision",
            ["decision"] = "rest_site",
            ["context"] = RunContext(),
            ["options"] = optionList,
            ["player"] = PlayerSummary(player),
        };
    }

    private Dictionary<string, object?> ShopState(MerchantRoom merchantRoom, Player player)
    {
        var inv = merchantRoom.Inventory;
        if (inv == null) { ForceToMap(); return MapSelectState(); }

        var cards = inv.CharacterCardEntries.Concat(inv.ColorlessCardEntries)
            .Select((e, i) =>
            {
                var card = e.CreationResult?.Card;
                var entry = card?.Id.Entry ?? "?";
                var stats = new Dictionary<string, object?>();
                int cardCost = 0;
                try
                {
                    if (card != null)
                    {
                        cardCost = card.EnergyCost?.GetResolved() ?? 0;
                        var mutable = card.ToMutable();
                        foreach (var dv in mutable.DynamicVars.Values)
                            stats[dv.Name.ToLowerInvariant()] = (int)dv.BaseValue;
                    }
                }
                catch { }
                var shopkws = card?.Keywords?.Where(k => k != CardKeyword.None).Select(k => k.ToString()).ToList();
                return new Dictionary<string, object?>
                {
                    ["index"] = i,
                    ["name"] = _loc.Card(entry),
                    ["type"] = card?.Type.ToString() ?? "?",
                    ["rarity"] = card?.Rarity.ToString() ?? "?",
                    ["card_cost"] = cardCost,
                    ["description"] = _loc.Localized("cards", entry + ".description"),
                    ["stats"] = stats.Count > 0 ? stats : null,
                    ["keywords"] = shopkws?.Count > 0 ? shopkws : null,
                    ["after_upgrade"] = card != null ? GetUpgradedInfo(card) : null,
                    ["cost"] = e.Cost,
                    ["is_stocked"] = e.IsStocked,
                    ["on_sale"] = e.IsOnSale,
                };
            }).ToList();

        var relics = inv.RelicEntries.Select((e, i) => new Dictionary<string, object?>
        {
            ["index"] = i,
            ["name"] = _loc.Relic(e.Model?.Id.Entry ?? "?"),
            ["description"] = _loc.Localized("relics", (e.Model?.Id.Entry ?? "?") + ".description"),
            ["cost"] = e.Cost,
            ["is_stocked"] = e.IsStocked,
        }).ToList();

        var potions = inv.PotionEntries.Select((e, i) => new Dictionary<string, object?>
        {
            ["index"] = i,
            ["name"] = _loc.Potion(e.Model?.Id.Entry ?? "?"),
            ["description"] = _loc.Localized("potions", (e.Model?.Id.Entry ?? "?") + ".description"),
            ["cost"] = e.Cost,
            ["is_stocked"] = e.IsStocked,
        }).ToList();

        var removal = merchantRoom.Inventory.CardRemovalEntry;

        return new Dictionary<string, object?>
        {
            ["type"] = "decision",
            ["decision"] = "shop",
            ["context"] = RunContext(),
            ["cards"] = cards,
            ["relics"] = relics,
            ["potions"] = potions,
            ["card_removal_cost"] = removal?.Cost,
            ["player"] = PlayerSummary(player),
        };
    }

    private Dictionary<string, object?> TreasureState(TreasureRoom treasureRoom)
    {
        Log("Treasure room — collecting rewards");

        WaitForActionExecutor();
        _syncCtx.Pump();

        try
        {
            treasureRoom.DoNormalRewards().GetAwaiter().GetResult();
            _syncCtx.Pump();
            treasureRoom.DoExtraRewardsIfNeeded().GetAwaiter().GetResult();
            _syncCtx.Pump();
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("relic picking session"))
        {
            Log($"Relic session conflict, waiting and retrying: {ex.Message}");
            WaitForActionExecutor();
            _syncCtx.Pump();
            try
            {
                treasureRoom.DoNormalRewards().GetAwaiter().GetResult();
                _syncCtx.Pump();
                treasureRoom.DoExtraRewardsIfNeeded().GetAwaiter().GetResult();
                _syncCtx.Pump();
            }
            catch (Exception retryEx) { Log($"Treasure rewards retry failed: {retryEx.Message}"); }
        }
        catch (Exception ex) { Log($"Treasure rewards: {ex.Message}"); }

        ForceToMap();
        return MapSelectState();
    }

    private Dictionary<string, object?> GameOverState(bool isVictory)
    {
        var player = _runState!.Players[0];
        var summary = PlayerSummary(player);
        if (!isVictory)
            summary["hp"] = _lastKnownHp > 0 ? 0 : (player.Creature?.CurrentHp ?? 0);
        return new Dictionary<string, object?>
        {
            ["type"] = "decision",
            ["decision"] = "game_over",
            ["context"] = RunContext(),
            ["victory"] = isVictory,
            ["player"] = summary,
            ["act"] = _runState.CurrentActIndex + 1,
            ["floor"] = _runState.ActFloor,
        };
    }

    #endregion
}
