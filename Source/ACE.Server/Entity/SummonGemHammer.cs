using System;
using System.Collections.Generic;

using log4net;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity.Actions;
using ACE.Server.WorldObjects;

namespace ACE.Server.Entity
{
    public enum HammerDecision { Break, RefusedNotADuoGem, RefusedUnbound, RefusedStacked, RefusedPetOut }

    public enum HammerResult { Broken, BrokenHammerKept, Refused, NotInPack, GemUnreadable, BuildFailed, NoPackSpace, AddFailed, GemNotConsumed }

    /// <summary>
    /// The Summon Gem Hammer (Custom Summons Task 7). A non-wieldable Gem flagged PropertyBool.SummonGemHammer. Used on
    /// a BOUND Duo Summon Gem it breaks the gem and restores the two absorbed summon items exactly as they were when
    /// absorbed (charges, dagger-applied ratings, inscription), each with a new guid; the gem's own charge pool is
    /// lost. A blank or half-filled gem, and anything that is not a Duo Gem, is refused and nothing is consumed.
    ///
    /// BREAK ORDER, and why it cannot duplicate:
    ///   1. read both snapshots and build both restored objects (nothing in the world yet);
    ///   2. check the pack has room for BOTH at once - a full pack refuses here with the gem untouched;
    ///   3. add both to the pack in memory only (no network, no save) - a failed add undoes every add;
    ///   4. consume the gem (its row delete is enqueued) - a failure undoes both adds, so the gem stays whole;
    ///   5. only now send and save the two items, so their row saves are enqueued AFTER the gem's delete: the shard
    ///      queue is FIFO, so a crash can lose the summons but can never leave both the gem and its items;
    ///   6. consume exactly one hammer. A hammer that cannot be spent is logged and kept: that costs a hammer, not a
    ///      duplicated summon.
    /// Until step 4 succeeds nothing has reached the client or the database, which is what makes every earlier
    /// failure a clean rollback.
    /// </summary>
    public static class SummonGemHammer
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public const string NoTargetText = "The Summon Gem Hammer must be used on a Duo Summon Gem.";
        public const string NotADuoGemText = "The Summon Gem Hammer only breaks a Duo Summon Gem.";
        public const string UnboundText = "That Duo Summon Gem has not absorbed two summons yet, so there is nothing to break free.";
        public const string StackedText = "Separate the stack first: the hammer breaks one gem at a time.";
        public const string PetOutText = "Dismiss or wait out the gem's creatures before you break it.";
        public const string NoPackSpaceText = "You need room in your pack for two summoning items before you break the gem.";

        public static bool IsHammer(WorldObject wo) => wo != null && wo.GetProperty(PropertyBool.SummonGemHammer) == true;

        public static string RefusalText(HammerDecision decision)
        {
            switch (decision)
            {
                case HammerDecision.RefusedUnbound: return UnboundText;
                case HammerDecision.RefusedStacked: return StackedText;
                case HammerDecision.RefusedPetOut: return PetOutText;
                default: return NotADuoGemText;
            }
        }

        /// <summary>PURE in effect: reads only the target's flag, slots, stack size and pet.</summary>
        public static HammerDecision Decide(WorldObject target)
        {
            if (!DuoSummonGem.IsDuoGem(target))
                return HammerDecision.RefusedNotADuoGem;

            if (!DuoSummonGem.IsBound(target))
                return HammerDecision.RefusedUnbound;

            if (DuoSummonGem.IsStacked(target))
                return HammerDecision.RefusedStacked;

            // the gem's pet is out: breaking it now would strand a live pet pointing at a destroyed device
            if (target is PetDevice device && device.Pet != null)
                return HammerDecision.RefusedPetOut;

            return HammerDecision.Break;
        }

        /// <summary>The break itself, in the order the class remarks give. <paramref name="restored"/> is empty unless it broke.</summary>
        public static HammerResult Break(IDuoGemInventory inventory, WorldObject hammer, WorldObject gem, out HammerDecision decision, out IReadOnlyList<WorldObject> restored)
        {
            restored = Array.Empty<WorldObject>();

            decision = Decide(gem);

            if (decision != HammerDecision.Break)
                return HammerResult.Refused;

            if (!inventory.HasInPack(hammer) || !inventory.HasInPack(gem))
                return HammerResult.NotInPack;

            if (!DuoSummonGem.TryReadSlots(gem, out var a, out var b))
            {
                log.Error($"[DUO GEM] gem 0x{gem.Guid.Full:X8} has a slot that will not read; hammer refused");
                return HammerResult.GemUnreadable;
            }

            // 1. build
            var built = new List<WorldObject>();

            foreach (var snapshot in new[] { a, b })
            {
                var wo = inventory.BuildRestored(snapshot);

                if (wo == null)
                {
                    log.Error($"[DUO GEM] gem 0x{gem.Guid.Full:X8}: wcid {snapshot.Item.WeenieClassId} would not build; hammer refused");
                    DiscardAll(inventory, built);
                    return HammerResult.BuildFailed;
                }

                built.Add(wo);
            }

            // 2. room for both
            if (!inventory.CanAddAll(built))
            {
                DiscardAll(inventory, built);
                return HammerResult.NoPackSpace;
            }

            // 3. add in memory, all or nothing
            var added = new List<WorldObject>();

            try
            {
                foreach (var wo in built)
                {
                    if (!inventory.TryAddToPack(wo))
                    {
                        UndoAll(inventory, added);
                        DiscardAll(inventory, built);
                        return HammerResult.AddFailed;
                    }

                    added.Add(wo);
                }
            }
            catch
            {
                UndoAll(inventory, added);
                DiscardAll(inventory, built);
                throw;
            }

            // 4. the gem goes; if it will not, the adds are undone and the gem stays whole
            bool gemConsumed;

            try
            {
                gemConsumed = inventory.TryConsumeOne(gem);
            }
            catch
            {
                // a throw part-way: the gem's presence decides. Still in the pack -> undo; gone -> keep the items, but
                // first make sure the gem is really destroyed: a throw between its removal from the pack and its
                // Destroy would otherwise leave the gem's row alive beside the two restored items
                if (inventory.HasInPack(gem))
                    UndoAll(inventory, added);
                else
                {
                    if (!gem.IsDestroyed)
                        inventory.Discard(gem);

                    CommitAll(inventory, added);
                }

                throw;
            }

            if (!gemConsumed)
            {
                UndoAll(inventory, added);
                return HammerResult.GemNotConsumed;
            }

            // 5. commit: network and save, after the gem's delete was enqueued
            CommitAll(inventory, added);

            restored = added;

            // 6. exactly one hammer
            if (!inventory.TryConsumeOne(hammer))
            {
                log.Error($"[DUO GEM] gem 0x{gem.Guid.Full:X8} broke but hammer 0x{hammer.Guid.Full:X8} could not be consumed");
                return HammerResult.BrokenHammerKept;
            }

            return HammerResult.Broken;
        }

        private static void UndoAll(IDuoGemInventory inventory, List<WorldObject> added)
        {
            foreach (var wo in added)
            {
                try
                {
                    inventory.UndoAdd(wo);
                }
                catch (Exception ex)
                {
                    log.Error($"[DUO GEM] could not undo the add of 0x{wo.Guid.Full:X8}", ex);
                }
            }

            added.Clear();
        }

        private static void DiscardAll(IDuoGemInventory inventory, List<WorldObject> built)
        {
            foreach (var wo in built)
            {
                try
                {
                    inventory.Discard(wo);
                }
                catch (Exception ex)
                {
                    log.Debug($"[DUO GEM] could not discard an unused restored object", ex);
                }
            }

            built.Clear();
        }

        private static void CommitAll(IDuoGemInventory inventory, List<WorldObject> added)
        {
            foreach (var wo in added)
                inventory.CommitAdd(wo);
        }

        // ---- Player-facing shell ---------------------------------------------------------------------------

        /// <summary>
        /// Entry point from Gem.HandleActionUseOnTarget. Same shape as PetDevice.Refill: refused while busy, a
        /// pre-check before the animation, then the real work re-verified at the end of it under IsBusy. Every exit
        /// sends the use-done event; the caller sends none.
        /// </summary>
        public static void UseOnTarget(Player player, Gem hammer, WorldObject target)
        {
            if (player.IsBusy)
            {
                player.SendUseDoneEvent(WeenieError.YoureTooBusy);
                return;
            }

            var decision = Decide(target);

            if (decision != HammerDecision.Break)
            {
                DuoSummonGem.Say(player, RefusalText(decision));
                player.SendUseDoneEvent();
                return;
            }

            if (PetDevice.VerifyUseRequirements(player, hammer, target) != WeenieError.None)
            {
                DuoSummonGem.Say(player, "Both the hammer and the gem must be in your pack.");
                player.SendUseDoneEvent(WeenieError.YouDoNotPassCraftingRequirements);
                return;
            }

            player.IsBusy = true;

            var animTime = 0.0f;
            var actionChain = new ActionChain();

            if (player.CombatMode != CombatMode.NonCombat)
            {
                var stanceTime = player.SetCombatMode(CombatMode.NonCombat);
                actionChain.AddDelaySeconds(stanceTime);
                animTime += stanceTime;
            }

            animTime += player.EnqueueMotion(actionChain, MotionCommand.ClapHands);

            actionChain.AddAction(player, () =>
            {
                try
                {
                    // re-verify: Break checks the decision, both items in the pack and room again
                    var result = Break(new PlayerDuoGemInventory(player), hammer, target, out var again, out var restored);

                    switch (result)
                    {
                        case HammerResult.Broken:
                        case HammerResult.BrokenHammerKept:
                            DuoSummonGem.Say(player, $"The {target.Name} shatters, and {restored[0].Name} and {restored[1].Name} fall free.");
                            break;
                        case HammerResult.Refused:
                            DuoSummonGem.Say(player, RefusalText(again));
                            break;
                        case HammerResult.NoPackSpace:
                            DuoSummonGem.Say(player, NoPackSpaceText);
                            break;
                        case HammerResult.NotInPack:
                            DuoSummonGem.Say(player, "Both the hammer and the gem must be in your pack.");
                            break;
                        default:
                            DuoSummonGem.Say(player, "The gem will not break. Nothing was lost.");
                            break;
                    }
                }
                catch (Exception ex)
                {
                    log.Error($"[DUO GEM] hammer threw for {player.Name} on 0x{target.Guid.Full:X8}", ex);
                }
                finally
                {
                    player.SendUseDoneEvent();
                    player.IsBusy = false;
                }
            });

            player.EnqueueMotion(actionChain, MotionCommand.Ready);

            actionChain.EnqueueChain();

            player.NextUseTime = DateTime.UtcNow.AddSeconds(animTime);
        }
    }
}
