using System;

using ACE.Entity.Enum;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Void/Summon T2: the player's combat pet takes 10/17.5/25% less damage by rank (plus a Loyalty rider,
    /// capped by a server tunable), and when the pet dies the summoning device's resummon cooldown is
    /// skipped so the tether can be re-formed immediately.
    ///
    /// Bespoke, so it carries IPassiveStatAbility: the reduction is read at the pet's incoming-damage site
    /// (CombatPet.TakeDamage via Player.GetSoulTetherDamageReductionMod) rather than baked in at summon time
    /// the way Empowered Summons is - so a rank learned mid-fight applies to the pet already in the world.
    /// The cooldown skip is read in WorldObject.CheckUseRequirements via Player.CanSkipCombatPetSummonCooldown.
    /// </summary>
    public class SoulTetherAbility : IClassAbility, IPassiveStatAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.SoulTether,
            AbilityClass = ClassAbilityClass.VoidSummon,
            Tier = 2,
            Name = "soultether",
            DisplayName = "Soul Tether",
            Description = "Your combat pets take 10/17.5/25% less damage (50% cap), and when a combat pet dies " +
                          "its summoning device can be used again immediately. Higher Loyalty increases the " +
                          "damage reduction.",
            MaxRank = 3,
            CostPerRank = new[] { 1, 1, 1 },
            Implemented = true,
            AffinitySkill = Skill.Loyalty,
        };

        /// <summary>
        /// The fraction of incoming pet damage removed at a given rank: base + (rank-1)*step, plus the
        /// Loyalty rider, plus the Soul Tether equipment mod, clamped to <paramref name="cap"/>.
        /// <paramref name="gearModFraction"/> is the Soul Tether equipment mod, a STANDALONE mod: same axis,
        /// additive, NOT rank-gated, so at rank 0 this degenerates to just the (capped) gear reduction.
        /// Defaults to 0, which reproduces the pre-equipment-mod behavior exactly. Pure for testability.
        /// </summary>
        public static double DamageReduction(int rank, double baseReduction, double stepPerRank, double loyaltyFraction, double cap, double gearModFraction = 0.0)
        {
            var gearBonus = Math.Max(0.0, gearModFraction);

            if (rank <= 0 && gearBonus <= 0.0)
                return 0.0;

            var abilityReduction = rank <= 0 ? 0.0 : baseReduction + (rank - 1) * stepPerRank + Math.Max(0.0, loyaltyFraction);

            return Math.Clamp(abilityReduction + gearBonus, 0.0, Math.Max(0.0, cap));
        }

        /// <summary>
        /// The multiplier applied to a combat pet's incoming damage (1.0 = none), i.e. 1 - the reduction.
        /// Pure for testability.
        /// </summary>
        public static float DamageMultiplier(int rank, double baseReduction, double stepPerRank, double loyaltyFraction, double cap, double gearModFraction = 0.0)
        {
            return (float)(1.0 - DamageReduction(rank, baseReduction, stepPerRank, loyaltyFraction, cap, gearModFraction));
        }

        /// <summary>
        /// Mirrors the terms fed into DamageReduction above (x100 for display, "%" since it's a share of
        /// damage) - the two must stay in step. Affinity carries the RAW (unclamped) Loyalty rider like
        /// AcidProc's affinity-cap pattern; Effective applies the same Math.Clamp(abilityReduction +
        /// gearBonus, 0.0, cap) DamageReduction uses. CapNote is set only when the clamp actually reduced
        /// this call's value.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var baseReduction = PropertyManager.GetDouble("class_ability_soultether_base").Item;
            var stepPerRank = PropertyManager.GetDouble("class_ability_soultether_step").Item;
            var cap = PropertyManager.GetDouble("class_ability_soultether_max_reduction").Item;

            var skillReduction = rank <= 0 ? 0.0 : baseReduction + (rank - 1) * stepPerRank;

            var rawAffinity = Math.Max(0.0, player.GetClassAbilityScaling(Skill.Loyalty,
                PropertyManager.GetDouble("class_ability_soultether_loyalty_per_trained").Item,
                PropertyManager.GetDouble("class_ability_soultether_loyalty_per_spec").Item) * 0.01);

            var gearBonus = Math.Max(0.0, player.GetEquippedModValue(EquipmentModId.SoulTether));

            var abilityReduction = skillReduction + rawAffinity;
            var uncapped = abilityReduction + gearBonus;
            var clamped = Math.Clamp(uncapped, 0.0, Math.Max(0.0, cap));

            var skill = skillReduction * 100.0;
            var affinity = rawAffinity * 100.0;
            var gear = gearBonus * 100.0;

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = skill,
                Affinity = affinity,
                Gear = gear,
                Effective = clamped * 100.0,
                Unit = "%",
                Label = "pet DR",
                Per = null,
                CapNote = clamped < uncapped - 0.0000001 ? "tether cap" : null,
            };
        }
    }
}
