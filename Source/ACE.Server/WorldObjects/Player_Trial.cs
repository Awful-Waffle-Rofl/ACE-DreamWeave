using System;
using System.Linq;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.BalanceTrials;
using ACE.Server.Network.GameMessages.Messages;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// Balance Trials, player side (Docs/BalanceTrials/DESIGN.md). A partial, never a subclass: a trial character is an
    /// ordinary Player that a <see cref="BalanceTrials.TrialSession"/> is bound to while it stands in its private room.
    /// Nothing here is persisted; a relog produces a new Player with no session, which the manager reads as the player gone.
    /// </summary>
    partial class Player
    {
        /// <summary>
        /// The trial this player is bound to, or null. Set by <see cref="TrialStarter.TryStart"/> before the entry teleport and
        /// cleared by <see cref="TrialManager.Remove"/> or <see cref="CheckTrialInstanceExit"/>. A reference write, read from
        /// the player's thread and the room's.
        /// </summary>
        internal TrialSession TrialSession;

        /// <summary>
        /// True from TrialStarter binding the session until the player first stands in the room instance. While set,
        /// <see cref="CheckTrialInstanceExit"/> ignores the session, so an earlier teleport's OnTeleportComplete (or refusal)
        /// landing after the bind cannot forfeit it. A refused entry is ended by the manager's arrival grace instead.
        /// </summary>
        internal bool TrialEntryPending;

        /// <summary>
        /// The exact EphemeralRealmExitTo Position TrialStarter wrote for the entry, or null when the trial never stamped (a
        /// refused start). The trial only ever clears a stamp that still equals this one: a later portal into another private
        /// realm (or a refused start before stamping) leaves a stamp that belongs to someone else, and it is kept.
        /// </summary>
        internal Position TrialExitStamp;

        /// <summary>THE player-side membership test: true while a trial session is bound to this player.</summary>
        public bool TrialActive => TrialSession != null;

        private bool trialDeathInProgress;

        /// <summary>
        /// True while the current death sequence is a trial death. Read by the death-penalty skips in Player_Death.cs (item loss,
        /// Olthoi slag, the respawn destination), which run later in the death chain than <see cref="TryBeginTrialDeath"/>.
        /// </summary>
        public bool TrialDeathInProgress => trialDeathInProgress;

        /// <summary>
        /// Death-sequence entry point (called from Player.Die(), beside the Proving Grounds challenges). A death inside the
        /// bound session's room ends the live rep as died (through <see cref="TrialManager.OnPlayerDied"/>) and is penalty-free:
        /// returning true joins Die()'s arenaDeath, so vitae and the enchantment purge are waived, and the latch waives item loss
        /// and sends the respawn to the room start. Returns false, and does nothing, for every other death.
        /// </summary>
        public bool TryBeginTrialDeath()
        {
            var session = TrialSession;

            trialDeathInProgress = session != null && Location != null
                && TrialManager.TryGetInstance(session, out var instance) && Location.Instance == instance;

            if (trialDeathInProgress)
                TrialManager.OnPlayerDied(this);

            return trialDeathInProgress;
        }

        /// <summary>
        /// The manager's half of the stamp rule: clears EphemeralRealmExitTo once a trial ended with this player outside the
        /// room <paramref name="instance"/> and no other trial bound. Runs on the player's own thread (queued by TrialManager.Remove).
        /// </summary>
        internal void ClearTrialExitStampIfLeft(uint instance)
        {
            if (TrialActive || Location == null || Location.Instance == instance)
                return;

            ClearOwnTrialExitStamp();
        }

        /// <summary>
        /// Clears EphemeralRealmExitTo only when it is still the stamp the trial wrote (<see cref="TrialExitStamp"/>, compared by
        /// cell, frame and instance), then forgets the trial's stamp either way. True when it cleared.
        /// </summary>
        internal bool ClearOwnTrialExitStamp()
        {
            var own = TrialExitStamp;
            TrialExitStamp = null;

            if (own == null)
                return false;

            var current = GetPosition(PositionType.EphemeralRealmExitTo);

            if (!IsSameStamp(own, current))
                return false;

            SetPosition(PositionType.EphemeralRealmExitTo, null);
            return true;
        }

        internal static bool IsSameStamp(Position own, Position current)
            => own != null && current != null
               && (ReferenceEquals(own, current) || (own.Equals(current) && own.Instance == current.Instance));

        /// <summary>
        /// The class-ability half of the trial soft reset (TrialReset.Soft), on the room thread. Clears the three cooldown
        /// stores a class ability keeps: Berserk Rage's static ready-time map (keyed by guid, so it survives a relog),
        /// Soul Jump's soulJumpReadyTime and Hunter's Echo's huntersEchoLastTrigger. Then every transient stack, charge,
        /// window and ward pool, through ResetClassAbilityTransientState (the arena-entry precedent, Player_PvpArena.cs).
        /// ResetClassAbilityTransientState deliberately keeps the cooldowns, because outside a trial clearing one would hand
        /// out a free save; a trial rep must start every rep from the same state, so here they go too.
        /// </summary>
        internal void ClearTrialClassAbilityCooldowns()
        {
            berserkRageReadyTimes.TryRemove(Guid.Full, out _);
            soulJumpReadyTime = 0;
            huntersEchoLastTrigger = 0;

            ResetClassAbilityTransientState();
        }

        /// <summary>
        /// The item-spell half of the trial soft reset (TrialReset.Soft step 5b), on the room thread: every worn item that
        /// TrialReset.NeedsSpellActivation selects (spells off, mana left, spells known) goes through the wield path's own
        /// checks and activation, in the order TryEquipObjectWithNetworking and DoHandleActionGetAndWieldItem run them:
        /// CheckWieldRequirements, then TryActivateSpells, which makes the activation checks (CheckUseRequirements) and casts
        /// through CreateItemSpell. Returns how many items it turned on.
        ///
        /// THE BUFFED CHECKS ARE WAIVED, by the rule the PvP template restore uses for gear provably worn
        /// (Player_PvpTemplate.cs, RestorePvpTemplateOwnEquip): this runs after step 2 removed every buff, so a skill or
        /// attribute a buff raised reads low here although the rep wears the item and fights buffed. Only the buff-dependent
        /// checks are waived: the wield kinds CheckWieldRequirements(waiveBuffedRequirements: true) skips, and the two
        /// CheckUseRequirements checks the facet activation waiver skips (arcane lore vs ItemDifficulty, ItemSkillLimit).
        /// Everything else those checks gate on stays live. An item that still ends up off is logged.
        /// </summary>
        internal int ActivateTrialItemSpells()
        {
            var activated = 0;
            var registry = Biota.PropertiesEnchantmentRegistry.Clone(BiotaDatabaseLock);

            foreach (var item in EquippedObjects.Values.ToList())
            {
                if (!TrialReset.NeedsSpellActivation(item, registry))
                    continue;

                var wieldError = CheckWieldRequirements(item, waiveBuffedRequirements: true);
                if (wieldError != WeenieError.None)
                {
                    log.Warn($"[TRIAL] {Name} (0x{Guid.Full:X8}): {item.Name} (0x{item.Guid.Full:X8}) stays off: {wieldError}");
                    continue;
                }

                // The return value matters: an item the "nothing in force" rule picked already reads IsAffecting true, so the
                // flag alone would count a refused activation as a success.
                bool on;
                BeginFacetSpellActivationWaiver(item.Guid.Full);
                try
                {
                    on = TryActivateSpells(item);
                }
                finally
                {
                    EndFacetSpellActivationWaiver(item.Guid.Full);
                }

                if (on && item.IsAffecting)
                    activated++;
                else
                    log.Warn($"[TRIAL] {Name} (0x{Guid.Full:X8}): {item.Name} (0x{item.Guid.Full:X8}) stays off: its activation checks failed");
            }

            return activated;
        }

        /// <summary>
        /// Restores <paramref name="wcid"/> to <paramref name="target"/> in total, counting the pack and any equipped stack
        /// (TrialReset.Soft step 4). Short: topped up through <see cref="RestockTrialConsumable"/>. Over, and
        /// <paramref name="trim"/>: the excess is consumed from the pack (never from an equipped stack). Returns (added,
        /// removed). Room thread.
        /// </summary>
        internal (int Added, int Removed) RestoreTrialConsumable(uint wcid, long target, bool trim)
        {
            var clamped = (int)Math.Clamp(target, 0, int.MaxValue);
            var have = TrialConsumableCount(wcid);

            if (have < clamped)
                return (RestockTrialConsumable(wcid, clamped), 0);

            if (!trim || have == clamped)
                return (0, 0);

            var excess = Math.Min(have - clamped, GetNumInventoryItemsOfWCID(wcid));

            if (excess <= 0 || !TryConsumeFromInventoryWithNetworking(wcid, excess))
            {
                if (excess > 0)
                    log.Warn($"[TRIAL] {Name} (0x{Guid.Full:X8}): consumable {wcid} could not be trimmed by {excess} to {clamped}");
                return (0, 0);
            }

            return (0, excess);
        }

        /// <summary>The pack's stacks of <paramref name="wcid"/> plus any equipped stack of it.</summary>
        private int TrialConsumableCount(uint wcid)
            => GetNumInventoryItemsOfWCID(wcid) + EquippedObjects.Values.Where(i => i.WeenieClassId == wcid).Sum(i => i.StackSize ?? 1);

        /// <summary>
        /// Tops the pack up to <paramref name="target"/> of <paramref name="wcid"/> in total, pack and equipped stacks counted
        /// (never takes any away), the way DeliverArenaBlood delivers Blood: existing non-full pack stacks first, then new
        /// stacks. Stops at the first thing that does not fit. Returns the amount added. Room thread.
        /// </summary>
        internal int RestockTrialConsumable(uint wcid, int target)
        {
            var have = TrialConsumableCount(wcid);
            var amount = target - have;

            if (amount <= 0)
                return 0;

            var weenie = ACE.Database.DatabaseManager.World.GetCachedWeenie(wcid);

            if (weenie == null)
            {
                log.Error($"[TRIAL] {Name} (0x{Guid.Full:X8}): consumable {wcid} is not in the world database; not restocked");
                return 0;
            }

            var maxStack = weenie.IsStackable() ? Math.Max(1, weenie.GetMaxStackSize()) : 1;
            var unitBurden = weenie.IsStackable() ? weenie.GetStackUnitEncumbrance() : (weenie.GetProperty(PropertyInt.EncumbranceVal) ?? 0);

            var stacks = GetInventoryItemsOfWCID(wcid);
            var plan = ACE.Server.Pvp.PvpArenaRewards.PlanStacks(stacks.Select(s => s.StackSize ?? 1).ToList(), maxStack, amount);
            var added = 0;

            try
            {
                foreach (var (index, add) in plan.TopUps)
                {
                    var stack = FindObject(stacks[index].Guid, SearchLocations.MyInventory, out var container, out var rootContainer, out _);

                    if (stack == null || !HasEnoughBurdenToAddToInventory(unitBurden * add) || !AdjustStack(stack, add, container, rootContainer))
                        return added;

                    added += add;
                    Session?.Network.EnqueueSend(new GameMessageSetStackSize(stack));
                }

                foreach (var size in plan.NewStacks)
                {
                    var item = ACE.Server.Factories.WorldObjectFactory.CreateNewWorldObject(wcid);

                    if (item == null)
                        return added;

                    item.SetStackSize(size);

                    if (!TryCreateInInventoryWithNetworking(item))
                    {
                        item.Destroy();
                        return added;
                    }

                    added += size;
                }
            }
            finally
            {
                if (added > 0)
                    Session?.Network.EnqueueSend(new GameMessagePrivateUpdatePropertyInt(this, PropertyInt.EncumbranceVal, EncumbranceVal ?? 0));

                if (added < amount)
                    log.Warn($"[TRIAL] {Name} (0x{Guid.Full:X8}): consumable {wcid} restocked {added} of {amount} (burden or pack space)");
            }

            return added;
        }

        /// <summary>Clears the trial-death latch once the death sequence has fully completed (beside EndSpeedChallengeDeath).</summary>
        public void EndTrialDeath()
        {
            trialDeathInProgress = false;
        }

        /// <summary>
        /// Reconciles the bound session against the player's instance after a teleport completes or is refused (both call sites
        /// sit beside CheckSpeedChallengeInstanceExit). A player whose Location is no longer the session's room (a recall, a
        /// portal, an admin teleport, a refused entry) forfeits: the session is unbound here and ended on its room's thread,
        /// which destroys its creatures and never pulls the player back.
        /// </summary>
        public void CheckTrialInstanceExit()
        {
            var session = TrialSession;

            if (session == null)
                return;

            var registered = TrialManager.TryGetInstance(session, out var instance);

            if (registered && Location != null && Location.Instance == instance)
            {
                // The first arrival in the room ends the entry window.
                TrialEntryPending = false;
                return;
            }

            // The entry teleport has not landed yet: whatever teleport completed (or was refused) here is not a way out.
            if (registered && TrialEntryPending)
                return;

            TrialSession = null;
            TrialEntryPending = false;

            TrialManager.EndSession(session, "player left the room");

            // Left the room: the trial's own exit stamp is spent (the speed, survival and wave exits clear theirs the same way).
            ClearOwnTrialExitStamp();

            Session?.Network?.EnqueueSend(new GameMessageSystemChat("[TRIAL] You have left the trial room. The trial is over.", ChatMessageType.Broadcast));
        }
    }
}
