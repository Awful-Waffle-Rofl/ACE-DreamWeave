using System;

using ACE.Entity.Enum;
using ACE.Server.WorldObjects;

namespace ACE.Server.WeaponMods
{
    /// <summary>
    /// Pure rules for the DEFENSIVE WEAPON modifier (WeaponModId.DefensiveWeapon, PropertyFloat 8034), renamed
    /// from Arcane Defender and widened from caster-only to every weapon class by owner ruling 2026-10-25.
    ///
    /// THE PER-ITEM CEILING. The registry row's MaxRoll is 200 armor level. A ONE-HANDED MELEE carrier reaches
    /// half of that, 100; a two-handed melee weapon, a missile launcher and a caster all reach the full 200. The
    /// 0..max roll model is unchanged (no floor, an explicit owner choice). The factor below is wired into the
    /// row as <see cref="WeaponModDefinition.CeilingFactor"/>, so every fraction-to-magnitude conversion that is
    /// handed the carrier agrees on the ceiling - see WeaponModDefinition.MaxRollFor.
    ///
    /// WHERE THE LEVEL ENTERS COMBAT (Creature.GetShieldMod, Player.ApplyThornsReflect):
    ///  - NO real shield: the level is a virtual shield on the UNCAPPED path, summed with the ward shield
    ///    (WardShieldMath), exactly as Arcane Defender was. A shieldless defender's Shield skill is often
    ///    untrained, so the Shield-skill cap would zero it.
    ///  - A real shield equipped: the level is added AFTER the Shield-skill cap and BEFORE the attacker's
    ///    ignore-shield term, in the same slot as the ward and summed with it (<see cref="AfterShieldCap"/>).
    ///  - Thorns: the level is added to the shield armor level Thorns reflects from. Thorns still requires an
    ///    equipped shield, so with no shield Defensive Weapon enables neither Thorns nor Shield Block.
    /// The level is STANCE-BOUND everywhere: 0 while the wielder is in NonCombat (Player.GetDefensiveWeaponShieldLevel).
    /// </summary>
    public static class DefensiveWeaponMath
    {
        /// <summary>The share of the row's MaxRoll a one-handed melee carrier reaches: 0.5, so 100 of 200.</summary>
        public const double OneHandedMeleeCeilingFactor = 0.5;

        /// <summary>
        /// TRUE when the item is a TWO-HANDED weapon. Any ONE of three markers counts, because none of them is on
        /// every two-handed weenie:
        ///   - ValidLocations carries EquipMask.TwoHanded (the slot the equip code places it in);
        ///   - WeaponSkill is Skill.TwoHandedCombat (WorldObject.IsTwoHanded, the engine's own test);
        ///   - CombatUse is CombatUse.TwoHanded.
        /// Measured against the local ace_world (2026-10-25): of 179 retail melee weapons with
        /// WeaponSkill = TwoHandedCombat, 178 also carry the ValidLocations bit and one (46830, a Nodachi) carries
        /// only the MeleeWeapon slot; of 62 fork melee weapons with the ValidLocations bit, 34 have no WeaponSkill
        /// at all. PropertyInt.WeaponType is NOT used: 39 retail two-handers lack WeaponType.TwoHanded.
        /// </summary>
        public static bool IsTwoHanded(EquipMask? validLocations, Skill weaponSkill, CombatUse? combatUse)
        {
            if (validLocations.HasValue && (validLocations.Value & EquipMask.TwoHanded) != 0)
                return true;

            if (weaponSkill == Skill.TwoHandedCombat)
                return true;

            return combatUse == CombatUse.TwoHanded;
        }

        /// <summary><see cref="IsTwoHanded(EquipMask?, Skill, CombatUse?)"/> read off an item. False for null.</summary>
        public static bool IsTwoHanded(WorldObject item) =>
            item != null && IsTwoHanded(item.ValidLocations, item.WeaponSkill, item.CombatUse);

        /// <summary>
        /// The ceiling factor for a carrier of <paramref name="weaponClass"/>: <see cref="OneHandedMeleeCeilingFactor"/>
        /// for a one-handed melee weapon, 1.0 for everything else (two-handed melee, missile, caster, and an
        /// unclassifiable item, which reads the full registry ceiling).
        /// </summary>
        public static double CeilingFactor(WeaponClass weaponClass, bool twoHanded) =>
            weaponClass == WeaponClass.Melee && !twoHanded ? OneHandedMeleeCeilingFactor : 1.0;

        /// <summary>
        /// The ceiling factor read off the carrier item - the function the registry row carries as its
        /// <see cref="WeaponModDefinition.CeilingFactor"/>. A null item reads 1.0.
        /// </summary>
        public static double CeilingFactor(WorldObject item)
        {
            if (item == null)
                return 1.0;

            return CeilingFactor(WeaponClassifier.Classify(item), IsTwoHanded(item));
        }

        /// <summary>
        /// The equipped-shield path's level after the Shield-skill cap: the capped shield level, plus the
        /// Defensive Weapon level, plus the ward shield level (WardShieldMath.AfterShieldCap). The ward and
        /// Defensive Weapon SUM, the same as on the no-shield path; the ward's own larger-of-two-wards rule is
        /// already applied inside the ward level. A negative or NaN Defensive Weapon level counts as 0. The
        /// caller then multiplies by the attacker's ignore-shield term.
        /// </summary>
        public static double AfterShieldCap(double cappedShieldLevel, double defensiveWeaponLevel, double wardLevel) =>
            ACE.Server.ClassAbilities.WardShieldMath.AfterShieldCap(cappedShieldLevel, wardLevel) + Sanitize(defensiveWeaponLevel);

        /// <summary>
        /// The armor level Thorns reflects from: the equipped shield's armor level plus the Defensive Weapon
        /// level. The caller has already refused a defender with no shield, so this is never reached shieldless.
        /// </summary>
        public static double ThornsArmorLevel(double shieldArmorLevel, double defensiveWeaponLevel) =>
            shieldArmorLevel + Sanitize(defensiveWeaponLevel);

        /// <summary>A Defensive Weapon level as read: NaN or negative counts as 0.</summary>
        public static double Sanitize(double level) =>
            double.IsNaN(level) || level < 0.0 ? 0.0 : level;
    }
}
