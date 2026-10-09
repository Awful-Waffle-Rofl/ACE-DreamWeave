using System;
using System.Collections.Generic;

using ACE.Entity.Enum.Properties;
using ACE.Server.WorldObjects;

namespace ACE.Server.WeaponMods
{
    /// <summary>
    /// Per-wielder weapon-mod suppression: the rule that switches a player's weapon mods off while some other
    /// ruleset says they do not apply. Today that is the PK facet only (owner ruling 2026-09-25, "No weapon
    /// mods. Apply that to the PK facet as well."), reversing the earlier ruling that weapon mods stayed live
    /// there. This is NOT a second system gate: weapon_mods_enabled is still the one switch for the whole
    /// system. Suppression is a property of the WIELDER, evaluated per read, and it never touches the item -
    /// the records and natives stay exactly as rolled, so leaving the PK facet restores everything at once.
    ///
    /// TWO SHAPES, because the two tiers store differently (see WeaponModDefinition):
    ///
    ///   Tier B stores a fraction that is only ever read through <see cref="WeaponModCombat.ReadWeaponOnly(WorldObject, WeaponModId)"/>,
    ///   so suppression there is one extra condition inside that accessor.
    ///
    ///   Tier A ADDED its magnitude into a native item property (GearDamage, GearCrit, GearCritDamage,
    ///   IgnoreShield) and recorded the added amount on the item. Nothing reads a Tier A record in combat - the
    ///   engine reads the native - so suppression there SUBTRACTS the recorded amount back off at the read
    ///   sites (Creature_Rating's three rating getters and WorldObject.GetIgnoreShieldMod), clamped so the result
    ///   never falls below the row's floor or below what the item had without the mod. Nothing is written, so
    ///   the equipped-items rating cache needs no invalidation when suppression turns on or off.
    /// </summary>
    public static class WeaponModSuppression
    {
        /// <summary>
        /// Whether a player's weapon mods are suppressed, from the rule inputs: the PK facet rule
        /// (Player.IsPkFacetRuleActive) OR the PvP arena weapon-mod mask (Player.IsPvpWeaponModMaskActive,
        /// Docs/Pvp/DESIGN.md H11). A further ruleset ORs its own term in HERE, so that
        /// <see cref="Player.WeaponModSuppressed"/> and every read site pick it up together. Pure.
        /// </summary>
        public static bool Suppressed(bool pkFacetRuleActive, bool pvpArenaMaskActive) => pkFacetRuleActive || pvpArenaMaskActive;

        /// <summary>
        /// How a Player owner's suppression is resolved. A seam only so tests can drive the accessor guard
        /// without a live Player (ACE.Server.Tests cannot construct one); production never reassigns it.
        /// </summary>
        internal static Func<Player, bool> PlayerSuppressedSource = DefaultPlayerSuppressed;

        private static bool DefaultPlayerSuppressed(Player player) => player.WeaponModSuppressed;

        /// <summary>
        /// TRUE only when <paramref name="owner"/> is a Player whose weapon mods are suppressed. A null owner, or
        /// any non-player (a monster, a pet), is NEVER suppressed: an item with no resolvable wielder behaves
        /// exactly as it did before suppression existed.
        /// </summary>
        public static bool SuppressedFor(WorldObject owner) => owner is Player player && PlayerSuppressedSource(player);

        // ---------------- Tier A ----------------

        /// <summary>
        /// A native value with one Tier A weapon-mod amount taken back off: <paramref name="nativeValue"/> minus
        /// <paramref name="recordedMagnitude"/>, clamped at <paramref name="floor"/>. Pure.
        ///
        /// It never RAISES a value: a native that already sits below the floor (malformed data) is returned as
        /// is, and so is any value when the record is absent, zero, negative or not finite. NaN in the native is
        /// passed through untouched rather than laundered into a number.
        /// </summary>
        public static double TierANet(double nativeValue, double recordedMagnitude, double floor)
        {
            if (double.IsNaN(nativeValue))
                return nativeValue;

            if (double.IsNaN(recordedMagnitude) || double.IsInfinity(recordedMagnitude) || recordedMagnitude <= 0.0)
                return nativeValue;

            var net = Math.Max(floor, nativeValue - recordedMagnitude);

            return Math.Min(nativeValue, net);
        }

        /// <summary>
        /// The Tier A row whose native is <paramref name="native"/>, or null. At most one row writes any given
        /// native (WeaponModInteractionTests pins that no two specials share one). A dictionary lookup built once
        /// in WeaponModRegistry, not a scan, because this runs on every rating read of a suppressed player.
        /// </summary>
        public static WeaponModDefinition TierARowFor(PropertyInt native) =>
            WeaponModRegistry.TryGetTierAByNative(native, out var definition) ? definition : null;

        /// <summary>
        /// The Tier A row whose native is <paramref name="native"/>, or null.
        /// </summary>
        public static WeaponModDefinition TierARowFor(PropertyFloat native) =>
            WeaponModRegistry.TryGetTierAByNativeFloat(native, out var definition) ? definition : null;

        /// <summary>
        /// How much of the integer rating <paramref name="native"/> the given equipped items owe to Tier A weapon
        /// mods: the sum, per item, of (native - <see cref="TierANet"/>). Subtracting this from the equipped-items
        /// rating sum yields the rating the wielder would have with no weapon mods. Reads each item's native the
        /// same way the rating cache does (absent = the row's default) and each item's own record, so an item only
        /// ever gives back what it itself was given. 0 when no Tier A row writes <paramref name="native"/>.
        ///
        /// The cache and these live reads agree because a weapon mod is never applied to an equipped item
        /// (WeaponModManager refuses a wielded target; see its "inventory only, never equipped" check).
        /// </summary>
        public static int TierARatingOffset(IEnumerable<WorldObject> equippedItems, PropertyInt native)
        {
            if (equippedItems == null)
                return 0;

            var definition = TierARowFor(native);

            if (definition == null)
                return 0;

            var offset = 0;

            foreach (var item in equippedItems)
            {
                if (item == null)
                    continue;

                var record = item.GetProperty(definition.Record);

                if (record == null)
                    continue;

                var nativeValue = (double)(item.GetProperty(native) ?? (int)definition.NativeDefault);
                var net = TierANet(nativeValue, record.Value, definition.NativeFloor);

                offset += (int)Math.Round(nativeValue - net, MidpointRounding.AwayFromZero);
            }

            return offset;
        }

        /// <summary>
        /// The Tier A offset for one rating read on <paramref name="creature"/>: 0 unless it is a suppressed
        /// Player, otherwise <see cref="TierARatingOffset"/> over its equipped items. The single call the rating
        /// getters in Creature_Rating.cs make, so the guard exists once.
        /// </summary>
        public static int RatingOffsetFor(Creature creature, PropertyInt native)
        {
            if (!SuppressedFor(creature))
                return 0;

            return TierARatingOffset(creature.EquippedObjects.Values, native);
        }

        /// <summary>
        /// The weapon's own IgnoreShield term as GetIgnoreShieldMod reads it, "weapon?.IgnoreShield ?? 0", with
        /// Shield Bypass's recorded amount taken back off when <paramref name="suppressed"/>. The attacker's own
        /// IgnoreShield is not an item and is never touched; the caller still takes the max of the two.
        /// </summary>
        public static double WeaponIgnoreShield(WorldObject weapon, bool suppressed)
        {
            var nativeValue = weapon?.IgnoreShield ?? 0.0;

            if (!suppressed || weapon == null)
                return nativeValue;

            var definition = TierARowFor(PropertyFloat.IgnoreShield);

            if (definition == null)
                return nativeValue;

            var record = weapon.GetProperty(definition.Record);

            if (record == null)
                return nativeValue;

            return TierANet(nativeValue, record.Value, definition.NativeFloor);
        }
    }
}
