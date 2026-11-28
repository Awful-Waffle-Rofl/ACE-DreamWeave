using System;

using ACE.Entity.Enum;
using ACE.Server.WorldObjects;

namespace ACE.Server.WeaponMods
{
    /// <summary>
    /// The three weapon families this system routes on. A [Flags] enum so a registry row can name the set of
    /// classes it belongs to with one field, while a resolved weapon always carries exactly one flag.
    /// </summary>
    [Flags]
    public enum WeaponClass
    {
        None    = 0,
        Melee   = 1,
        Missile = 2,
        Caster  = 4,

        /// <summary>A shield (armor-slot item with CombatUse.Shield). Deliberately OUTSIDE <see cref="All"/>: a registry row opts in with an explicit | Shield.</summary>
        Shield  = 8,

        All     = Melee | Missile | Caster,
    }

    /// <summary>
    /// Decides which pool a target draws from. Pure: the classification half takes plain enum values so it can
    /// be exercised without a live item.
    ///
    /// EquipmentMods routes on <see cref="EquipMask"/> because gear ratings are slot-shaped. Weapons are not:
    /// a wand and a bow can share a ready slot, so this routes on <see cref="ItemType"/> instead, with
    /// <see cref="CombatUse"/> used to peel off two lookalikes: ammunition (arrows, bolts, darts are
    /// ItemType.MissileWeapon with CombatUse.Ammo), which is rejected as <see cref="WeaponClass.None"/>, and
    /// shields (CombatUse.Shield), which route to their own <see cref="WeaponClass.Shield"/>.
    /// </summary>
    public static class WeaponClassifier
    {
        public static WeaponClass Classify(ItemType itemType, CombatUse? combatUse)
        {
            // shields first: they can carry ItemType.Armor or ItemType.MeleeWeapon, and are their own class
            if (combatUse == CombatUse.Shield)
                return WeaponClass.Shield;

            // ammo carries ItemType.MissileWeapon but is a consumable, not a launcher
            if (combatUse == CombatUse.Ammo)
                return WeaponClass.None;

            // caster first: a wand is ItemType.Caster only, but checking it first keeps the order total
            if ((itemType & ItemType.Caster) != 0)
                return WeaponClass.Caster;

            if ((itemType & ItemType.MissileWeapon) != 0)
                return WeaponClass.Missile;

            if ((itemType & ItemType.MeleeWeapon) != 0)
                return WeaponClass.Melee;

            return WeaponClass.None;
        }

        public static WeaponClass Classify(WorldObject target)
        {
            if (target == null)
                return WeaponClass.None;

            return Classify(target.ItemType, target.CombatUse);
        }
    }
}
