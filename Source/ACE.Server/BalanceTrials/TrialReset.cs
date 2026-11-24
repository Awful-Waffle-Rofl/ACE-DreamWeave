using System;
using System.Collections.Generic;
using System.Linq;

using log4net;

using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.Structure;
using ACE.Server.WorldObjects;
using ACE.Server.WorldObjects.Managers;

namespace ACE.Server.BalanceTrials
{
    /// <summary>What one soft reset did, for the log line.</summary>
    public sealed record TrialSoftResetSummary(int EnchantmentsRemoved, int CooldownsCleared, int CloaksCleared, int ConsumablesRestocked, int ConsumablesTrimmed, int ItemsRecharged, int SpellsCast, int BuffsLengthened = 0, int ItemsActivated = 0);

    /// <summary>One consumable the soft reset restores: top up to Target, and when Trim, also take any excess away.</summary>
    public readonly record struct TrialConsumableTarget(uint Wcid, long Target, bool Trim);

    /// <summary>
    /// The soft reset between reps (Docs/BalanceTrials/DESIGN.md 6, rebuild step 4): full vitals, every carried timed
    /// enchantment removed, cooldowns cleared, consumables restored, every possession's mana refilled (worn or packed), then the buff
    /// profile.
    ///
    /// ENCHANTMENTS (final review F1b). Every timed entry on the player that is not item-sourced goes, beneficial ones
    /// included: a proc or potion buff carried out of the last rep would otherwise change the Enchantments digest every
    /// rep. So the Enchantments digest is exactly the buff profile's cast set each rep. Kept: cooldown entries (step 3
    /// clears those), permanent entries (Duration -1, an item's own spells while worn) and entries cast by an item the
    /// player is wearing. The same rule clears every worn item's own registry (an item buff the profile cast on it last
    /// rep), so nothing the profile casts carries into the next rep, whatever its duration.
    ///
    /// ITEM SPELLS. An item that ran dry is switched off (Player.HandleManaDepleted: its spells removed, IsAffecting false)
    /// and stays off when mana is refilled, so step 5b turns it back on through Player.TryActivateSpells, the wield path's
    /// activation, after the wield path's own requirement checks. Its spells are cast with the item as caster and Duration
    /// -1, so step 2 never removes them: an item that stays active keeps them from rep to rep and is never re-cast.
    ///
    /// LONG BUFFS. After the profile is cast, every timed entry it added (on the player and on the worn items) is given
    /// <see cref="ProfileBuffDurationSeconds"/> from now, so no buff can run out inside a rep. They stay timed (never -1,
    /// which would read as an item's own spell), so step 2 still removes them before the next rep.
    ///
    /// CONSUMABLES (final review F1). The archetype's LiftUp.Consumables are topped up to their stacks (never trimmed).
    /// Every other consumable (TrialDigest.IsConsumable) goes back to the MASTER's count: topped up when short, trimmed
    /// when over (a consumable the master does not hold is trimmed to nothing). Trimming takes from the pack only; an
    /// equipped stack (the wielded ammunition) is never trimmed, and new stacks always land in the pack, so an ammunition
    /// stack shot to nothing (dequipped and destroyed) comes back in the pack, not in the ammunition slot.
    ///
    /// THE COOLDOWN STORES IT CLEARS (traced, not guessed; the PR lists them):
    ///   spell and item: the enchantment registry's cooldown entries (EnchantmentManager.StartCooldown writes them, spell id
    ///     0x8000 | CooldownId, SpellCategory_Cooldown, StatModType Cooldown; CheckCooldown reads them). Every shared item
    ///     cooldown (gems, kits, essences, summoning devices) lives there. There is no separate per-spell recast store on a
    ///     player.
    ///   item: a worn cloak's proc delay, Cloak.RollProc's "now - cloak.UseTimestamp &lt; MinDelay".
    ///   class ability: Berserk Rage's static berserkRageReadyTimes map, soulJumpReadyTime and huntersEchoLastTrigger
    ///     (Player.ClearTrialClassAbilityCooldowns), plus every transient stack, charge, window and ward pool through
    ///     Player.ResetClassAbilityTransientState, the arena-entry precedent (Player_PvpArena.cs).
    /// Player.LastUseTracker is declared and never read or written, so there is nothing to clear there.
    ///
    /// Room thread only (the rep stands in the room, so its landblock thread owns it).
    /// </summary>
    public static class TrialReset
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>The duration every buff the profile casts is given (24 h): far longer than any rep, and still timed.</summary>
        public const double ProfileBuffDurationSeconds = 86400;

        /// <param name="buffProfile">The profile step 6 casts: TrialBuffs.EffectiveProfile (the scenario's override, else the archetype's).</param>
        public static TrialSoftResetSummary Soft(Player p, TrialArchetype a, IReadOnlyDictionary<uint, long> masterConsumables, string buffProfile)
        {
            ArgumentNullException.ThrowIfNull(p);
            ArgumentNullException.ThrowIfNull(a);
            ArgumentNullException.ThrowIfNull(masterConsumables);

            // 1. Full vitals.
            p.SetMaxVitals();

            // 2. Every carried timed enchantment that is not item-sourced, harmful or beneficial.
            var registry = p.Biota.PropertiesEnchantmentRegistry.Clone(p.BiotaDatabaseLock);
            var wornGuids = new HashSet<uint>(p.EquippedObjects.Values.Select(i => i.Guid.Full));
            var carried = registry.Where(e => IsCarriedTimedEnchantment(e, wornGuids)).ToList();
            if (carried.Count > 0)
                p.EnchantmentManager.Dispel(carried);

            var enchantmentsRemoved = carried.Count;
            foreach (var item in p.EquippedObjects.Values)
            {
                var itemCarried = item.Biota.PropertiesEnchantmentRegistry.Clone(item.BiotaDatabaseLock).Where(e => IsCarriedTimedEnchantment(e, wornGuids)).ToList();
                if (itemCarried.Count > 0)
                    item.EnchantmentManager.Dispel(itemCarried);
                enchantmentsRemoved += itemCarried.Count;
            }

            // 3. Cooldowns: the registry's cooldown entries, worn cloaks' proc delay, the class-ability timers.
            var cooldowns = registry.Where(IsCooldownEntry).ToList();
            foreach (var entry in cooldowns)
                p.EnchantmentManager.Remove(entry, sound: false);

            var cloaks = 0;
            foreach (var item in p.EquippedObjects.Values)
            {
                if (Cloak.IsCloak(item) && item.UseTimestamp.HasValue)
                {
                    item.UseTimestamp = null;
                    cloaks++;
                }
            }

            p.ClearTrialClassAbilityCooldowns();

            // 4. Consumables: the archetype's to their stacks, every other one to the master's count.
            var consumableWcids = TrialDigest.ConsumableWcids(a);
            var held = p.GetAllPossessions().Where(i => TrialDigest.IsConsumable(i.WeenieClassId, i.WeenieType, consumableWcids)).Select(i => i.WeenieClassId);

            var restocked = 0;
            var trimmed = 0;
            foreach (var t in ConsumableTargets(a.LiftUp?.Consumables, masterConsumables, held))
            {
                var (added, removed) = p.RestoreTrialConsumable(t.Wcid, t.Target, t.Trim);
                restocked += added;
                trimmed += removed;
            }

            // 5. Every item's mana full: worn, in the pack and in side packs (a swapped-in item starts full too).
            var recharged = Recharge(p.GetAllPossessions());

            // 5b. Every worn item whose spells are off (it ran dry in the last rep) gets them back through the player's own
            // wield-time activation, so the rep never has to re-equip it; then mana is topped up again, since activating
            // costs one point (Player.TryActivateSpells). Active items are left alone.
            var registryNow = p.Biota.PropertiesEnchantmentRegistry.Clone(p.BiotaDatabaseLock);
            var removalPending = p.EquippedObjects.Values.Any(i => ItemSpellRemovalPending(i, registryNow));

            var activated = p.ActivateTrialItemSpells();
            if (activated > 0)
                Recharge(p.EquippedObjects.Values);

            // An item that went off under 2 s ago is re-cast now (so the digest sees it), and its pending removal will strip
            // that cast: check the items again once the removal has run.
            if (removalPending)
                RecheckItemSpellsLater(p);

            // 6. The buff profile, its buffs lengthened, then vitals again: a max-vital buff raises the maximum, and the rep starts full.
            var cast = TrialBuffs.Apply(p, buffProfile);
            var lengthened = LengthenProfileBuffs(p, wornGuids);
            p.SetMaxVitals();

            var summary = new TrialSoftResetSummary(enchantmentsRemoved, cooldowns.Count, cloaks, restocked, trimmed, recharged, cast, lengthened, activated);
            log.Debug($"[TRIAL] soft reset {p.Name} (0x{p.Guid.Full:X8}): {summary}");
            return summary;
        }

        /// <summary>
        /// A worn item whose spells the soft reset turns (back) on. It has mana (the wield path's own gate,
        /// Player.TryEquipObjectWithNetworking: "if (item.ItemCurMana &gt; 0) TryActivateSpells(item)"), and either:
        ///   its spells are off (IsAffecting false, which Player.HandleManaDepleted sets when it runs dry; refilling
        ///     ItemCurMana alone never turns them back on) and it knows a spell; or
        ///   it reads as on but nothing it cast is in force: it has a spell TryActivateSpells would cast (not a proc spell, not
        ///     its SpellDID) and no entry cast by it is on the wielder (<paramref name="wielderRegistry"/>) or on itself. That is
        ///     the signature of HandleManaDepleted's removal, which runs 2 s after the item went off and strips the item's
        ///     entries whatever IsAffecting says by then (a reactivation inside that window is undone by it). ALL missing, not
        ///     any: an item spell that never makes an entry would otherwise re-cast the whole item every rep.
        /// </summary>
        public static bool NeedsSpellActivation(WorldObject item, IEnumerable<PropertiesEnchantmentRegistry> wielderRegistry)
        {
            if (item == null || (item.ItemCurMana ?? 0) <= 0)
                return false;

            var known = item.Biota.GetKnownSpellsIds(item.BiotaDatabaseLock);

            if (!item.IsAffecting)
                return known.Count > 0;

            var castable = known.Any(s => !item.HasProcSpell((uint)s) && s != item.SpellDID);
            return castable && !HasItemCastEntries(item, wielderRegistry);
        }

        /// <summary>
        /// HandleManaDepleted's removal has not run yet: the item is off, and entries it cast are still in force. Its removal
        /// (2 s after the item went off) would strip anything re-cast now, so the soft reset checks the item again after it.
        /// </summary>
        public static bool ItemSpellRemovalPending(WorldObject item, IEnumerable<PropertiesEnchantmentRegistry> wielderRegistry)
            => item != null && !item.IsAffecting && HasItemCastEntries(item, wielderRegistry);

        /// <summary>Any entry cast by <paramref name="item"/>, on the wielder or on the item itself (where an item-school spell without an item category lands, Creature.CreateItemSpell).</summary>
        internal static bool HasItemCastEntries(WorldObject item, IEnumerable<PropertiesEnchantmentRegistry> wielderRegistry)
        {
            var guid = item.Guid.Full;
            var own = item.Biota.PropertiesEnchantmentRegistry.Clone(item.BiotaDatabaseLock) ?? new List<PropertiesEnchantmentRegistry>();

            return (wielderRegistry ?? Array.Empty<PropertiesEnchantmentRegistry>()).Any(e => e != null && e.CasterObjectId == guid)
                || own.Any(e => e != null && e.CasterObjectId == guid);
        }

        /// <summary>How long after a soft reset that found a removal pending the item spells are checked again: past HandleManaDepleted's 2 s delay.</summary>
        public const double ItemSpellRecheckSeconds = 2.5;

        /// <summary>
        /// The follow-up pass for a pending removal: <see cref="ItemSpellRecheckSeconds"/> later, on the player's thread, while
        /// the rep is still in its trial, every worn item NeedsSpellActivation selects is activated again and topped up.
        /// </summary>
        private static void RecheckItemSpellsLater(Player p)
        {
            // Only for the trial that scheduled it: a session ended and replaced inside the delay is not this one's.
            var session = p.TrialSession;

            var chain = new ActionChain();
            chain.AddDelaySeconds(ItemSpellRecheckSeconds);
            chain.AddAction(p, () =>
            {
                if (session == null || !ReferenceEquals(p.TrialSession, session))
                    return;

                var again = p.ActivateTrialItemSpells();
                if (again > 0)
                    Recharge(p.EquippedObjects.Values);

                log.Debug($"[TRIAL] {p.Name} (0x{p.Guid.Full:X8}): item spell recheck after a pending removal turned {again} item(s) back on");
            });
            chain.EnqueueChain();
        }

        /// <summary>Sets ItemCurMana to ItemMaxMana on every item that has an ItemMaxMana and is not already full. Returns how many changed.</summary>
        internal static int Recharge(IEnumerable<WorldObject> items)
        {
            var recharged = 0;

            foreach (var item in items)
            {
                if (item.ItemMaxMana.HasValue && item.ItemCurMana != item.ItemMaxMana)
                {
                    item.ItemCurMana = item.ItemMaxMana;
                    recharged++;
                }
            }

            return recharged;
        }

        /// <summary>
        /// Gives every timed entry the profile just cast <see cref="ProfileBuffDurationSeconds"/> from now, on the player and on
        /// every worn item, and re-sends the player's to the client so its spell timers show the new duration. Step 2 removed
        /// every carried timed entry before the profile was cast, so the timed entries left (IsCarriedTimedEnchantment) are
        /// exactly the profile's. Returns how many were lengthened.
        /// </summary>
        internal static int LengthenProfileBuffs(Player p, IReadOnlySet<uint> wornGuids)
        {
            var mine = Lengthen(p.Biota.PropertiesEnchantmentRegistry.Clone(p.BiotaDatabaseLock), wornGuids);

            if (mine.Count > 0)
            {
                p.ChangesDetected = true;
                p.Session?.Network.EnqueueSend(new GameEventMagicUpdateMultipleEnchantments(p.Session, mine.Select(e => new Enchantment(p, e)).ToList()));
            }

            var count = mine.Count;
            foreach (var item in p.EquippedObjects.Values)
            {
                var onItem = Lengthen(item.Biota.PropertiesEnchantmentRegistry.Clone(item.BiotaDatabaseLock), wornGuids);
                if (onItem.Count > 0)
                    item.ChangesDetected = true;
                count += onItem.Count;
            }

            return count;
        }

        /// <summary>
        /// Sets Duration to <see cref="ProfileBuffDurationSeconds"/> and StartTime to 0 (the registry counts StartTime down
        /// from 0; an entry expires once Duration + StartTime reaches 0) on every entry IsCarriedTimedEnchantment selects.
        /// Mutates the entries in place (the registry clone holds the live entries) and returns them.
        /// </summary>
        internal static List<PropertiesEnchantmentRegistry> Lengthen(IEnumerable<PropertiesEnchantmentRegistry> entries, IReadOnlySet<uint> wornGuids)
        {
            var changed = entries.Where(e => IsCarriedTimedEnchantment(e, wornGuids)).ToList();

            foreach (var e in changed)
            {
                e.Duration = ProfileBuffDurationSeconds;
                e.StartTime = 0;
            }

            return changed;
        }

        /// <summary>
        /// Makes the rep attackable. Monster targeting skips a player whose Attackable is false (Monster_Awareness.cs, the
        /// closest-target scan), and a Developer account's first login turns its Creature biota into an Admin weenie whose
        /// Attackable is copied from the "admin" weenie (WorldManager, the OverrideCharacterPermissions block), which is
        /// false. Attackable is a persisted bool; rep characters are trial-only, so forcing it on them is safe. Called at
        /// trial start and before every rep's spawn. Returns true when it changed the value.
        /// </summary>
        public static bool EnsureAttackable(WorldObject rep)
        {
            if (rep == null || rep.Attackable)
                return false;

            rep.Attackable = true;
            return true;
        }

        /// <summary>A cooldown row (EnchantmentManager.StartCooldown): category SpellCategory_Cooldown or StatModType Cooldown.</summary>
        public static bool IsCooldownEntry(PropertiesEnchantmentRegistry e)
            => e != null && (e.SpellCategory == (SpellCategory)EnchantmentManager.SpellCategory_Cooldown || e.StatModType.HasFlag(EnchantmentTypeFlags.Cooldown));

        /// <summary>
        /// An enchantment the soft reset removes: timed (not Duration -1, an item's own spell while worn), not a cooldown
        /// (step 3 clears those), and not cast by an item in <paramref name="wornGuids"/>. Beneficial or harmful alike.
        /// </summary>
        public static bool IsCarriedTimedEnchantment(PropertiesEnchantmentRegistry e, IReadOnlySet<uint> wornGuids)
            => e != null && !IsCooldownEntry(e) && e.Duration != -1 && !(wornGuids != null && wornGuids.Contains(e.CasterObjectId));

        /// <summary>
        /// The consumable restore plan. Pure. The archetype's consumables: top up to their stack, no trim (they win over a
        /// master count for the same wcid). Every other wcid the master holds: its count, trimmed. Every other consumable the
        /// rep holds that the master does not: 0, trimmed. Sorted by wcid.
        /// </summary>
        public static IReadOnlyList<TrialConsumableTarget> ConsumableTargets(IEnumerable<TrialConsumable> liftUp, IReadOnlyDictionary<uint, long> master,
            IEnumerable<uint> heldConsumableWcids)
        {
            var targets = new Dictionary<uint, TrialConsumableTarget>();

            foreach (var kv in master ?? new Dictionary<uint, long>())
                targets[kv.Key] = new TrialConsumableTarget(kv.Key, kv.Value, true);

            foreach (var wcid in heldConsumableWcids ?? Array.Empty<uint>())
                targets.TryAdd(wcid, new TrialConsumableTarget(wcid, 0, true));

            foreach (var c in liftUp ?? Array.Empty<TrialConsumable>())
                targets[c.Wcid] = new TrialConsumableTarget(c.Wcid, c.Stack, false);

            return targets.Values.OrderBy(t => t.Wcid).ToList();
        }
    }
}
