using System;

using ACE.Entity.Enum;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Archmage T1 splash: buff and debuff spells of EVERY school cast 20/40/60/80/100% faster by rank -
    /// protections, banes, self-buffs, vulnerabilities and imperils. DAMAGING SPELLS ARE UNAFFECTED, and
    /// that exclusion is the entry's entire safety property: it speeds up the tedious half of casting
    /// without touching the class's damage-per-second, so it composes with Flat Cast Speed and Overchannel
    /// without multiplying into them.
    ///
    /// SHIPPED (2026-09-12), bespoke integration like Flat Cast Speed rather than a dispatched hook: applied
    /// in Player.GetQuickenedCastingMod (Player_ClassAbilityBuffs.cs), composed into the same
    /// Player.ApplyClassAbilityCastSpeed product Nether Rush and Flat Cast Speed already ride
    /// (WorldObject_Magic's DoWindupGestures/DoCastGesture read the composed result via castSpeedMultiplier).
    ///
    /// THE BUFF/DEBUFF CLASSIFICATION is <see cref="AffectsSpell"/>: MetaSpellType Enchantment or
    /// FellowEnchantment (every stat-mod spell - protections, banes, self-buffs, vulnerabilities, imperils),
    /// EXCLUDING a damage- or heal-over-time spell dressed up as one (Spell.IsDamageOverTime). A war/void/life
    /// bolt is MetaSpellType Projectile/LifeProjectile and never reaches this gate at all, so "a war bolt must
    /// be bit-identical at rank 5" holds regardless of the DoT exclusion; the DoT exclusion exists so a
    /// nether/fire damage-over-time enchantment - damaging, but shaped as a stat mod - is not mistaken for a
    /// buff either.
    ///
    /// NO AFFINITY AT ALL - this is the one entry in the wave with none, and the null is the FINAL state
    /// rather than a Phase 0 placeholder like its siblings. The design table assigns it no legacy skill
    /// rider, so <see cref="ClassAbilityDefinition.AffinitySkill"/> is null and no
    /// Player.GetClassAbilityScaling / GetClassAbilityAffinityMultiplier call exists for it anywhere, which
    /// keeps ClassAbilityAffinityDeclarationTests satisfied without an exception-table entry.
    /// </summary>
    public class QuickenedCastingAbility : IPassiveStatAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.QuickenedCasting,
            AbilityClass = ClassAbilityClass.Archmage,
            Tier = 1,
            Name = "quickened_casting",
            DisplayName = "Quickened Casting",
            Description = "Your buff and debuff spells of every school cast 20/40/60/80/100% faster (by " +
                          "rank) - protections, banes, self-buffs, vulnerabilities, and imperils. Damaging " +
                          "spells are unaffected.",
            MaxRank = 5,
            CostPerRank = new[] { 1, 1, 1, 1, 1 },
            Implemented = true,
        };

        /// <summary>
        /// TRUE when Quickened Casting's cast-speed bonus applies to a spell with the given MetaSpellType /
        /// IsDamageOverTime shape: an Enchantment or FellowEnchantment (buff/debuff, every school) that is
        /// NOT a damage- or heal-over-time spell. Pure and testable without a live Spell/Player - the
        /// classification is the whole safety property of this ability, so it is pulled out here rather than
        /// inlined in Player.GetQuickenedCastingMod.
        /// </summary>
        public static bool AffectsSpell(SpellType metaSpellType, bool isDamageOverTime)
        {
            if (metaSpellType != SpellType.Enchantment && metaSpellType != SpellType.FellowEnchantment)
                return false;

            return !isDamageOverTime;
        }

        /// <summary>The constant cast-speed multiplier at a given rank (1.0 = none): 1 + rank*perRank.</summary>
        public static float CastSpeedMultiplier(int rank, double percentPerRank)
        {
            if (rank <= 0)
                return 1.0f;

            return (float)(1.0 + Math.Max(0, rank) * percentPerRank);
        }

        /// <summary>
        /// Mirrors the rank term in CastSpeedMultiplier above (x100 for display) - no affinity rider and no
        /// equipment mod exist for this ability (no EquipmentModId.QuickenedCasting entry), so both are 0.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var skill = (rank <= 0 ? 0.0 : rank * PropertyManager.GetDouble("class_ability_quickenedcasting_percent_per_rank").Item) * 100.0;

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = skill,
                Affinity = 0.0,
                Gear = 0.0,
                Effective = skill,
                Unit = "%",
                Label = "cast speed (buff/debuff)",
                Per = null,
                CapNote = null,
            };
        }
    }
}
