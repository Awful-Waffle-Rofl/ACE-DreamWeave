using ACE.Entity.Enum;
using ACE.Server.WorldObjects;

namespace ACE.Server.CombatSimulator
{
    /// <summary>
    /// Describes an incoming hit without requiring a live creature. Every field that perturbs
    /// the defender's mitigation is here, so a cached DefenderProfile can be applied exactly.
    /// </summary>
    public class AttackerSpec
    {
        public string Label { get; init; }

        public CombatType CombatType { get; init; }
        public DamageType DamageType { get; init; }
        public AttackHeight AttackHeight { get; init; }

        /// <summary>Effective attack skill, for the closed-form hit chance.</summary>
        public uint AttackSkill { get; init; }

        public float BaseDamageMin { get; init; }
        public float BaseDamageMax { get; init; }

        public float CritChance { get; init; }

        /// <summary>
        /// PHYSICAL ONLY. The full crit multiplier with the +1.0 base already folded in, as
        /// Entity\DamageEvent.cs:318-323 composes it: (1.0f + GetWeaponCritDamageMod(...)) times
        /// the Execution weapon mod. CombatSimulator.Analytic multiplies BaseDamageMax by this and
        /// REPLACES the rolled base with the product, which is the physical rule.
        ///
        /// The magic path wants a different quantity and reads MagicCritDamageMod instead. Do not
        /// feed this value to it: the two differ by that +1.0, and by the fact that magic ADDS its
        /// crit bonus to the rolled base rather than replacing it.
        /// </summary>
        public float CritDamageMod { get; init; }

        /// <summary>
        /// MAGIC ONLY, and NOT the same quantity as CritDamageMod - which is why it is a separate
        /// field rather than a reinterpretation of that one. This is the RAW weapon crit damage
        /// multiplier with NO +1.0 base: SpellProjectile.cs:646 composes it as
        /// GetWeaponCritDamageMod(...) times (1.0f + the Execution weapon mod), where the physical
        /// path at DamageEvent.cs:318-323 adds 1.0f to the first term and this one does not.
        ///
        /// GetWeaponCritDamageMod returns the weapon's CriticalMultiplier, defaulting to
        /// defaultCritDamageMultiplier = 1.0f (WorldObjects\WorldObject_Weapon.cs:422, :429), so
        /// 1.0f is the neutral value here - an unimbued caster - where the physical field's
        /// neutral value is 2.0f. Defaulted to 1.0f for that reason.
        ///
        /// CombatSimulator.AnalyticMagic multiplies BaseDamageMax by 0.5f and by this, and ADDS
        /// the product to the rolled base rather than replacing it. See that method for the
        /// derivation.
        /// </summary>
        public float MagicCritDamageMod { get; init; } = 1.0f;

        /// <summary>Composed multiplicative pre-mitigation modifiers: power, attribute, slayer, damage rating.</summary>
        public float PreMitigationMod { get; init; } = 1.0f;

        /// <summary>
        /// 1.0 means no rending. Lower values reduce the defender's effective armor level.
        ///
        /// CARRIES CLEAVING TOO, despite the name, which is kept because five files bind to it.
        /// This is the COMPOSED MINIMUM of the rending mod and the armor cleaving mod, because
        /// that is how the engine composes them: Entity\DamageEvent.cs:340-346 takes
        /// Math.Min(armorRendingMod, armorCleavingMod) and hands the single result to the armor
        /// curve. DeveloperCommands.BuildSpecFromCreature builds the same minimum before assigning
        /// it here.
        ///
        /// So do NOT add a separate cleaving field and multiply it in - cleaving is already inside
        /// this value, and a second field would apply it twice.
        /// </summary>
        public float ArmorRendingMod { get; init; } = 1.0f;

        /// <summary>1.0 means the shield applies in full. From 1 - max(attacker.IgnoreShield, weapon.IgnoreShield).</summary>
        public float IgnoreShieldMod { get; init; } = 1.0f;

        public bool IgnoreMagicArmor { get; init; }
        public bool IgnoreMagicResist { get; init; }

        /// <summary>Weapon-derived resistance modifier, which floors the defender's vulnerability mod.</summary>
        public float WeaponResistanceMod { get; init; } = 1.0f;

        /// <summary>A weapon imbued with IgnoreAllArmor zeroes both the armor and shield terms.</summary>
        public bool IgnoreAllArmor { get; init; }

        /// <summary>False when the attack comes from outside the shield's 180 degree frontal arc.</summary>
        public bool WithinShieldArc { get; init; } = true;

        /// <summary>True only for a player attacker. Gates the defender's Battle Hardened PvE multiplier.</summary>
        public bool AttackerIsPlayer { get; init; }
    }
}
