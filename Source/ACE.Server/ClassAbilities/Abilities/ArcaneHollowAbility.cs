using System;

using ACE.Entity.Enum;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// ARCANE HOLLOW (Archmage T2, Archmage pass 2026-10-24, takes the slot Flat Cast Speed vacated). The magic
    /// counterpart to a hollow weapon: your WAR MAGIC damage of the seven innate resist types
    /// (<see cref="ArchmageResistanceMath.InnateResistTypes"/>: fire, cold, acid, electric, pierce, slash,
    /// bludgeon) ignores 10/20/30% (by rank, multiplied by the Magic Item Tinkering affinity, clamped to [0, 1])
    /// of the target's INNATE resistance to that type. It bypasses innate resistance only - the creature weenie's
    /// ResistX property - never resistance granted by enchantments.
    ///
    /// THE EXSANGUINATE SHAPE (owner ruling): the innate resistance r moves the fraction f of the way to 1.0,
    /// r' = r + f(1 - r), with NO effect when r &gt;= 1 (already weak) or r &lt;= 0 (immune stays immune). It is
    /// handed to Creature.GetResistanceMod(ResistanceType, ...) as the innateOverride, so only the innate factor
    /// moves and the enchantment term stays exactly as it was. When Elemental Inversion procs on the same hit,
    /// the substitution happens first and the ignore applies to the substituted innate value
    /// (ArchmageResistanceMath.InnateOverride).
    ///
    /// SCOPE: the war/void branch of SpellProjectile.CalculateDamage only, through
    /// Player.GetArchmageInnateOverride with magicDamage TRUE. Weapon hits never take it. Nether (void), life
    /// magic (HealthDrain), Drain and DoT ticks are excluded. PvE only (player caster, non-player target).
    ///
    /// Bespoke integration; carries IPassiveStatAbility because it hooks no dispatched combat event.
    /// </summary>
    public class ArcaneHollowAbility : IClassAbility, IPassiveStatAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.ArcaneHollow,
            AbilityClass = ClassAbilityClass.Archmage,
            Tier = 2,
            Name = "arcane_hollow",
            DisplayName = "Arcane Hollow",
            Description = "Your war magic's elemental and physical damage types (fire, cold, acid, electric, pierce, slash " +
                          "and bludgeon) ignore 10/20/30% (by rank) of a monster's innate resistance to that type. It " +
                          "bypasses the monster's natural resistance only, not resistance from enchantments, and does " +
                          "nothing against a type the monster is already weak to. Higher Magic Item Tinkering increases " +
                          "the share.",
            MaxRank = 3,
            CostPerRank = new[] { 1, 1, 1 },
            Implemented = true,
            AffinitySkill = Skill.MagicItemTinkering,
        };

        /// <summary>
        /// The ignored fraction: rank x ignorePerRank x the affinity multiplier, clamped to [0, 1]. 0 at
        /// rank 0. Pure.
        /// </summary>
        public static double IgnoreFraction(int rank, double ignorePerRank, double affinityMultiplier)
        {
            if (rank <= 0)
                return 0.0;

            return Math.Clamp(rank * ignorePerRank * Math.Max(0.0, affinityMultiplier), 0.0, 1.0);
        }

        /// <summary>
        /// Mirrors IgnoreFraction (x100 for display): the rank term, plus the AMOUNT the Magic Item Tinkering
        /// multiplier adds, clamped at 100%. No equipment mod exists for this ability, so Gear is 0.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var perRank = PropertyManager.GetDouble("class_ability_arcanehollow_ignore_per_rank").Item;

            var rankShare = rank <= 0 ? 0.0 : rank * perRank;

            var multiplier = player.GetClassAbilityAffinityMultiplier(Skill.MagicItemTinkering);

            var raw = rankShare * multiplier;
            var clamped = IgnoreFraction(rank, perRank, multiplier);

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = rankShare * 100.0,
                Affinity = (raw - rankShare) * 100.0,
                Gear = 0.0,
                Effective = clamped * 100.0,
                Unit = "%",
                Label = "innate resist ignored",
                Per = null,
                CapNote = clamped < raw - 0.0000001 ? "ignore cap" : null,
            };
        }
    }
}
