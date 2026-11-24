using System;
using System.Collections.Generic;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.WorldObjects;

namespace ACE.Server.WeaponMods
{
    /// <summary>
    /// The fixed numbers and storage of Paragon weapon mods (Paragon 51-55). A Paragon weapon at max level 50
    /// takes up to <see cref="MaxGems"/> vendor-bought gems; each grants <see cref="FractionPerGem"/> of one
    /// chosen <see cref="WeaponModId"/>, and gems may repeat an id.
    ///
    /// STORAGE. Each gem's id sits in one of <see cref="Slots"/>, filled in order with no gaps, so a weapon
    /// with no <see cref="PropertyInt.ParagonModSlot1"/> carries no Paragon value at all. Paragon value is a
    /// SEPARATE LAYER from the Tourmaline-rolled specials: it writes no weapon-mod record and no native
    /// property, so Tourmaline reroll, Amethyst swap, ClearSpecials, SpecialCount, the quality-tier aura and
    /// the integrity gate (which all walk WeaponModRegistry records) never see it.
    /// </summary>
    public static class ParagonModRegistry
    {
        /// <summary>One gem's share of a modifier's perfect (workmanship 10) roll: "+20%".</summary>
        public const double FractionPerGem = 0.20;

        /// <summary>The most gems one weapon can hold (item levels 51 to 55).</summary>
        public const int MaxGems = 5;

        /// <summary>The five slot properties, in fill order.</summary>
        public static readonly PropertyInt[] Slots =
        {
            PropertyInt.ParagonModSlot1,
            PropertyInt.ParagonModSlot2,
            PropertyInt.ParagonModSlot3,
            PropertyInt.ParagonModSlot4,
            PropertyInt.ParagonModSlot5,
        };
    }

    /// <summary>
    /// The combat read path for Paragon weapon mods. Paragon value is ADDITIVE with any Tourmaline-rolled record
    /// of the same modifier, and the sum is clamped at a fraction of 1.0 - a perfect workmanship 10 roll.
    ///
    /// TIER B is folded in inside <see cref="WeaponModCombat.ReadWeaponOnly(WorldObject, WeaponModId, bool, double)"/>,
    /// so the gate and per-wielder suppression that already guard that accessor guard Paragon value too.
    ///
    /// TIER A writes no native here. The rating getters in Creature_Rating.cs and WorldObject.GetIgnoreShieldMod
    /// ADD the Paragon amount at read time (<see cref="RatingAddFor"/>, <see cref="IgnoreShieldAddFor"/>), beside
    /// the suppression offset they already subtract. Because nothing is written, the equipped-items rating cache
    /// (Creature_Equipment, rebuilt only on equip and unequip) needs no invalidation when a gem is added or the
    /// gate or suppression changes: the add is recomputed on every read.
    /// </summary>
    public static class WeaponModParagon
    {
        /// <summary>The item types that can carry Paragon value: melee weapons, missile weapons and casters.</summary>
        public const ItemType CarrierTypes = ItemType.MeleeWeapon | ItemType.MissileWeapon | ItemType.Caster;

        /// <summary>
        /// TRUE when <paramref name="item"/> is a weapon (see <see cref="CarrierTypes"/>). A stray Paragon slot on
        /// anything else - armor, a shield, jewelry - is inert.
        /// </summary>
        public static bool IsCarrier(WorldObject item) => item != null && (item.ItemType & CarrierTypes) != 0;

        /// <summary>TRUE when the item is a weapon carrying at least one Paragon gem (slot 1 is filled).</summary>
        public static bool HasParagonMods(WorldObject weapon) => weapon?.GetProperty(PropertyInt.ParagonModSlot1) != null && IsCarrier(weapon);

        /// <summary>
        /// How many of the weapon's Paragon slots hold <paramref name="modId"/>. Slots are read in order and the
        /// count stops at the first empty slot, because the slots are filled in order with no gaps: a slot after
        /// a gap is malformed data and is not counted.
        /// </summary>
        public static int GemCount(WorldObject weapon, WeaponModId modId)
        {
            if (!IsCarrier(weapon))
                return 0;

            var count = 0;

            foreach (var slot in ParagonModRegistry.Slots)
            {
                var value = weapon.GetProperty(slot);

                if (value == null)
                    break;

                if (value.Value == (int)modId)
                    count++;
            }

            return count;
        }

        /// <summary>
        /// The Paragon fraction the weapon holds for <paramref name="modId"/>:
        /// <see cref="ParagonModRegistry.FractionPerGem"/> times the number of slots holding it. 0 straight away
        /// when slot 1 is empty, which is every weapon that is not a gemmed Paragon. A slot holding an id that is
        /// not a live catalog row is inert: the callers only ask about live rows.
        /// </summary>
        public static double ParagonFraction(WorldObject weapon, WeaponModId modId)
        {
            if (!HasParagonMods(weapon))
                return 0.0;

            return GemCount(weapon, modId) * ParagonModRegistry.FractionPerGem;
        }

        /// <summary>
        /// The fraction a Tier B read resolves: the Tourmaline record (absent, NaN or negative reads 0) plus the
        /// Paragon fraction, clamped to [0, 1]. Pure.
        /// </summary>
        public static double CombinedFraction(double? record, double paragonFraction)
        {
            var recorded = record == null || double.IsNaN(record.Value) || record.Value <= 0.0 ? 0.0 : record.Value;

            return WeaponModValue.Clamp01(WeaponModValue.Clamp01(recorded) + WeaponModValue.Clamp01(paragonFraction));
        }

        // ---------------- overflow (the ", capped" predicate) ----------------

        /// <summary>
        /// Pure: TRUE when a Tourmaline record of <paramref name="recordFraction"/> (an UNROUNDED fraction of the
        /// row's perfect roll) plus <paramref name="gemCount"/> Paragon gems passes a perfect roll, so part of the
        /// last gem is wasted. The ONE overflow formula: the appraisal ", capped" tail and the gem-use confirmation
        /// both read it, so the prompt and the description can never disagree.
        /// </summary>
        public static bool IsOverCap(double recordFraction, int gemCount) =>
            recordFraction + gemCount * ParagonModRegistry.FractionPerGem > 1.0 + 1e-9;

        /// <summary>
        /// TRUE when the weapon's current Paragon gems for <paramref name="definition"/> plus
        /// <paramref name="extraGems"/> more would pass a perfect roll over the Tourmaline record (Tier A and Tier B).
        /// </summary>
        public static bool WouldOverflow(WorldObject weapon, WeaponModDefinition definition, double scale, int extraGems = 1)
        {
            if (weapon == null || definition == null)
                return false;

            return IsOverCap(WeaponModParagonDisplay.RecordFraction(weapon, definition, scale), GemCount(weapon, definition.Id) + extraGems);
        }

        /// <summary><see cref="WouldOverflow(WorldObject, WeaponModDefinition, double, int)"/> by id, at the live magnitude scale. FALSE for a row that is not live.</summary>
        public static bool WouldOverflow(WorldObject weapon, WeaponModId modId, int extraGems = 1) =>
            WeaponModRegistry.TryGet(modId, out var definition) && WouldOverflow(weapon, definition, WeaponModValue.MagnitudeScale(), extraGems);

        // ---------------- Tier A ----------------

        /// <summary>
        /// The amount a Tier A row's Paragon gems ADD on top of what the weapon's native already holds, in the
        /// native's own units. Pure apart from the optional carrier item (used only for a per-item ceiling).
        ///
        ///     ceiling  = the row's perfect workmanship 10 magnitude at <paramref name="scale"/>
        ///     r        = the recorded Tourmaline add (absent reads 0)
        ///     combined = resolve(clamp01(r / ceiling + paragonFraction))   (the same rounding as a roll)
        ///     add      = max(0, combined - r)
        ///
        /// THE USABLE FLOOR (owner rule). On an integer row a Paragon gem must never add +0 on its own unless the
        /// weapon already sits at the ceiling, so an add the rounding would swallow is raised to 1 (never past the
        /// ceiling). 0 when there is no Paragon fraction, no ceiling (a muted scale), or the row is not Tier A.
        /// </summary>
        public static double TierAAdd(WeaponModDefinition definition, double? record, double paragonFraction, double scale, WorldObject item = null)
        {
            if (definition == null || definition.Tier != WeaponModTier.A)
                return 0.0;

            var paragon = WeaponModValue.Clamp01(paragonFraction);

            if (paragon <= 0.0)
                return 0.0;

            var ceiling = WeaponModValue.MaxMagnitude(definition, scale, item);

            if (double.IsNaN(ceiling) || ceiling <= 0.0)
                return 0.0;

            var recorded = record == null || double.IsNaN(record.Value) || double.IsInfinity(record.Value) || record.Value <= 0.0 ? 0.0 : record.Value;

            var tourmalineFraction = WeaponModValue.Clamp01(recorded / ceiling);

            var combined = WeaponModValue.MagnitudeFromFraction(definition, WeaponModValue.Clamp01(tourmalineFraction + paragon), scale, item);

            var add = Math.Max(0.0, combined - recorded);

            if (definition.IsInteger)
            {
                add = Math.Round(add, MidpointRounding.AwayFromZero);

                if (add < 1.0 && recorded < ceiling)
                    add = Math.Max(0.0, Math.Min(1.0, Math.Floor(ceiling - recorded)));
            }

            return add;
        }

        /// <summary>
        /// <see cref="TierAAdd(WeaponModDefinition, double?, double, double, WorldObject)"/> for one weapon: its own
        /// Tourmaline record and its own Paragon gems for the row.
        /// </summary>
        public static double TierAAdd(WorldObject weapon, WeaponModDefinition definition, double scale)
        {
            if (weapon == null || definition == null || !HasParagonMods(weapon))
                return 0.0;

            return TierAAdd(definition, weapon.GetProperty(definition.Record), ParagonFraction(weapon, definition.Id), scale, weapon);
        }

        /// <summary>
        /// The Paragon amount the given equipped items add to the integer rating <paramref name="native"/>: the sum,
        /// over items carrying Paragon slot 1, of <see cref="TierAAdd(WorldObject, WeaponModDefinition, double)"/>
        /// for the Tier A row writing that native. 0 when the gate is off or no Tier A row writes the native. Pure
        /// apart from the item reads.
        /// </summary>
        public static int TierARatingAdd(IEnumerable<WorldObject> equippedItems, PropertyInt native, bool enabled, double scale)
        {
            if (!enabled || equippedItems == null)
                return 0;

            var definition = WeaponModSuppression.TierARowFor(native);

            if (definition == null)
                return 0;

            var add = 0;

            foreach (var item in equippedItems)
            {
                if (item == null || !HasParagonMods(item))
                    continue;

                add += (int)Math.Round(TierAAdd(item, definition, scale), MidpointRounding.AwayFromZero);
            }

            return add;
        }

        /// <summary>
        /// The weenie types that can be WIELDED as a weapon and so can carry Paragon value on a rating read: melee
        /// weapons, missile launchers, casters, and thrown weapons (WeenieType.Missile, the stackable that sits in the
        /// missile-weapon slot itself). Ammunition is deliberately absent: it is never the weapon. Read off
        /// WorldObject.WeenieType, which is the biota's own field rather than a locked property read.
        /// </summary>
        public static bool IsCarrierWeenieType(WeenieType weenieType) =>
            weenieType == WeenieType.MeleeWeapon || weenieType == WeenieType.MissileLauncher ||
            weenieType == WeenieType.Caster || weenieType == WeenieType.Missile;

        /// <summary>The most carriers a rating read can count: a main hand plus a dual-wield off hand.</summary>
        public const int MaxRatingCarriers = 2;

        /// <summary>
        /// The Paragon add for one rating read on <paramref name="creature"/>: 0 when it is a suppressed Player
        /// (the PK facet or the PvP arena mask) or weapon_mods_enabled is off, otherwise
        /// <see cref="TierARatingAdd"/> over its wielded WEAPONS at the live magnitude scale. The single call the
        /// three rating getters in Creature_Rating.cs make.
        ///
        /// ONE PASS, WEAPONS ONLY. The equipped items are walked once and pre-filtered on
        /// <see cref="IsCarrierWeenieType"/> (a lock-free read of the biota's weenie type), so armor, shields,
        /// jewelry and ammunition are never property-read at all. Only a surviving weapon pays a locked read of
        /// <see cref="PropertyInt.ParagonModSlot1"/>, and only a weapon that has it pays the ItemType read and the
        /// tunable reads. At most <see cref="MaxRatingCarriers"/> carriers are counted. Cost per call, for N equipped
        /// items of which W are weapons (W is at most 2 for any legal loadout): the suppression check, N lock-free
        /// weenie-type reads and W slot reads; nothing more unless a weapon is gemmed.
        ///
        /// HUNTER'S ECHO. Inside an Echo weapon scope the creature's weapon accessors answer with the Echo's snapshot
        /// weapon and ignore the live main hand; this read does the same, counting the snapshot weapon alone.
        /// </summary>
        public static int RatingAddFor(Creature creature, PropertyInt native)
        {
            if (creature == null || WeaponModSuppression.SuppressedFor(creature))
                return 0;

            var echo = creature.HuntersEchoWeaponScope;

            if (echo != null)
            {
                if (!HasParagonMods(echo.Weapon))
                    return 0;

                return TierARatingAdd(new[] { echo.Weapon }, native, WeaponModRegistry.Enabled(), WeaponModValue.MagnitudeScale());
            }

            WorldObject first = null;
            WorldObject second = null;

            foreach (var item in creature.EquippedObjects.Values)
            {
                if (item == null || !IsCarrierWeenieType(item.WeenieType) || !HasParagonMods(item))
                    continue;

                if (first == null)
                {
                    first = item;
                    continue;
                }

                second = item;
                break;   // MaxRatingCarriers
            }

            if (first == null)
                return 0;

            var carriers = second == null ? new[] { first } : new[] { first, second };

            return TierARatingAdd(carriers, native, WeaponModRegistry.Enabled(), WeaponModValue.MagnitudeScale());
        }

        /// <summary>
        /// The ceiling on the WEAPON term of WorldObject.GetIgnoreShieldMod: a weapon can ignore at most the whole
        /// shield (1.0), so the shield mod "1 - max(creature, weapon)" never goes negative from the weapon side. This
        /// also caps the pre-existing case where a loot IgnoreShield plus a Tourmaline Shield Bypass add summed past
        /// 1.0. NaN is passed through untouched. Pure.
        /// </summary>
        public static double ClampWeaponIgnoreShield(double weaponTerm) =>
            double.IsNaN(weaponTerm) ? weaponTerm : Math.Min(1.0, weaponTerm);

        /// <summary>
        /// The Paragon add to the weapon's IgnoreShield term (Shield Bypass), as a fraction. 0 when the gate is
        /// off, the attacker is suppressed, or the weapon has no Paragon gems. Pure apart from the item reads.
        /// </summary>
        public static double IgnoreShieldAdd(WorldObject weapon, bool enabled, bool suppressed, double scale)
        {
            if (!enabled || suppressed || !HasParagonMods(weapon))
                return 0.0;

            var definition = WeaponModSuppression.TierARowFor(PropertyFloat.IgnoreShield);

            if (definition == null)
                return 0.0;

            return TierAAdd(weapon, definition, scale);
        }

        /// <summary>
        /// <see cref="IgnoreShieldAdd(WorldObject, bool, bool, double)"/> against the live gate and scale, with
        /// suppression resolved for <paramref name="attacker"/>. The call WorldObject.GetIgnoreShieldMod makes.
        /// </summary>
        public static double IgnoreShieldAddFor(WorldObject weapon, WorldObject attacker)
        {
            if (!HasParagonMods(weapon))
                return 0.0;

            return IgnoreShieldAdd(weapon, WeaponModRegistry.Enabled(), WeaponModSuppression.SuppressedFor(attacker), WeaponModValue.MagnitudeScale());
        }
    }
}
