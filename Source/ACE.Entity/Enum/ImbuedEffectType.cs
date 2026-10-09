using System;

namespace ACE.Entity.Enum
{
    [Flags]
    public enum ImbuedEffectType: uint
    {
        Undef                           = 0,
        CriticalStrike                  = 0x0001,
        CripplingBlow                   = 0x0002,
        ArmorRending                    = 0x0004,
        SlashRending                    = 0x0008,
        PierceRending                   = 0x0010,
        BludgeonRending                 = 0x0020,
        AcidRending                     = 0x0040,
        ColdRending                     = 0x0080,
        ElectricRending                 = 0x0100,
        FireRending                     = 0x0200,
        MeleeDefense                    = 0x0400,
        MissileDefense                  = 0x0800,
        MagicDefense                    = 0x1000,
        Spellbook                       = 0x2000,
        NetherRending                   = 0x4000,

        /// <summary>
        /// FORK ADDITION - not a retail imbue. The life-magic ("blood") counterpart of the elemental
        /// rends, so a caster can cleave Health resistance the way a Fire wand cleaves Fire.
        /// 0x8000 was the next free bit; every retail value above is unchanged.
        ///
        /// Only does anything alongside the DamageType.Health case in
        /// WorldObject_Weapon.GetRendDamageType and the weaponResistanceMod term in
        /// Creature_Properties' ResistanceType.HealthDrain branch - the retail HealthDrain branch
        /// discards the weapon modifier entirely, so this bit is inert without that change.
        /// </summary>
        HealthRending                   = 0x8000,

        IgnoreSomeMagicProjectileDamage = 0x20000000,
        AlwaysCritical                  = 0x40000000,
        IgnoreAllArmor                  = 0x80000000
    }
}
