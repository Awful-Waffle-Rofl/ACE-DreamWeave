using System;

using ACE.Entity.Enum;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// ELEMENTAL INVERSION (Archmage T1, Archmage pass 2026-10-24, takes the slot Quickened Casting vacated).
    /// Each of your hits on a monster whose damage type is one of the seven innate resist types
    /// (<see cref="ArchmageResistanceMath.InnateResistTypes"/>: fire, cold, acid, electric, pierce, slash,
    /// bludgeon) - weapon hits and war magic alike - has a 2/4/6/8/10% chance (by rank, multiplied by the Arcane
    /// Lore affinity, clamped to [0, 1]) to be resolved as if the monster's innate resistance to that type were
    /// its WEAKEST innate resistance: the highest innate ResistX among the seven.
    ///
    /// ONLY THE INNATE TERM CHANGES (owner ruling 2026-10-24). The hit keeps its original damage type for
    /// everything: armor, rend/cleave (the original type's WeaponResistanceMod), the original type's
    /// vulnerability and protection enchantments, Hunter's Mark, shields and slayers. Owner example: a fire wand
    /// with fire rend against a monster weakest to slash is still fire damage with fire rend and fire vulns, but
    /// its fire innate term is read as the monster's slash innate value. Implemented as the innateOverride
    /// parameter of Creature.GetResistanceMod(ResistanceType, ...), never as a type swap or a ratio. The seven
    /// include the original type, so a proc can never lower damage.
    ///
    /// On a weapon hit that Volatile Elements (Spellsword T2) re-typed, "its own damage type" here means the
    /// RE-TYPED type: that re-type happens earlier in DamageEvent.DoCalculateDamage, so this roll, its
    /// eligibility (a hit re-typed to Nether is not eligible) and its immunity check all see the rolled type.
    ///
    /// IMMUNITY IS RESPECTED (owner ruling 2026-10-24): when the target's innate ResistX for the hit's own type
    /// is &lt;= 0 there is no override and no roll - an immune monster stays immune.
    ///
    /// NO ROLL for Nether, life damage (Harm, the life projectiles, a life health bolt: HealthDrain), Drain or
    /// DoT ticks. Sites (PvE only, player attacker, non-player defender): the war/void branch of
    /// SpellProjectile.CalculateDamage and DamageEvent.ResolveDefenderHalf for melee and missile, both through
    /// Player.GetArchmageInnateOverride.
    ///
    /// Bespoke integration; carries IPassiveStatAbility because it hooks no dispatched combat event.
    /// </summary>
    public class ElementalInversionAbility : IClassAbility, IPassiveStatAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.ElementalInversion,
            AbilityClass = ClassAbilityClass.Archmage,
            Tier = 1,
            Name = "elemental_inversion",
            DisplayName = "Elemental Inversion",
            Description = "Each of your fire, cold, acid, electric, pierce, slash or bludgeon hits on a monster - spells " +
                          "and weapon attacks alike - has a 2/4/6/8/10% chance (by rank) to treat the monster's natural " +
                          "resistance to that type as its weakest natural resistance among those seven. The hit keeps " +
                          "its own damage type, rends and vulnerabilities, and a monster immune to that type stays immune. " +
                          "Higher Arcane Lore increases the chance.",
            MaxRank = 5,
            CostPerRank = new[] { 1, 1, 1, 1, 1 },
            Implemented = true,
            AffinitySkill = Skill.ArcaneLore,
        };

        /// <summary>
        /// The proc chance: rank x chancePerRank x the affinity multiplier, clamped to [0, 1]. 0 at rank 0.
        /// Pure.
        /// </summary>
        public static double ProcChance(int rank, double chancePerRank, double affinityMultiplier)
        {
            if (rank <= 0)
                return 0.0;

            return Math.Clamp(rank * chancePerRank * Math.Max(0.0, affinityMultiplier), 0.0, 1.0);
        }

        /// <summary>
        /// Mirrors ProcChance (x100 for display): the rank term, plus the AMOUNT the Arcane Lore multiplier
        /// adds, clamped at 100%. No equipment mod exists for this ability, so Gear is 0.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var perRank = PropertyManager.GetDouble("class_ability_elementalinversion_chance_per_rank").Item;

            var rankChance = rank <= 0 ? 0.0 : rank * perRank;

            var multiplier = player.GetClassAbilityAffinityMultiplier(Skill.ArcaneLore);

            var raw = rankChance * multiplier;
            var clamped = ProcChance(rank, perRank, multiplier);

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = rankChance * 100.0,
                Affinity = (raw - rankChance) * 100.0,
                Gear = 0.0,
                Effective = clamped * 100.0,
                Unit = "%",
                Label = "inversion chance",
                Per = null,
                CapNote = clamped < raw - 0.0000001 ? "chance cap" : null,
            };
        }
    }
}
