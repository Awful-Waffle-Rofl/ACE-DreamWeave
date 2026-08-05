using System;

using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Archer T3 game-changer: a 6/12/18% chance (by rank) that a missile volley immediately fires a full
    /// second volley at the same targets, at no ammo/stamina cost. Deliberately unscaled
    /// (SKILL-TABLES-PREVIEW). Bespoke: rolled in Player.LaunchMissile via Player.TryClassAbilityDoubleVolley;
    /// the second volley reuses the cleave-style multi-shot arrow machinery. Carries IPassiveStatAbility
    /// because it hooks no combat event of its own.
    /// </summary>
    public class DoubleVolleyAbility : IClassAbility, IPassiveStatAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.DoubleVolley,
            AbilityClass = ClassAbilityClass.Archer,
            Tier = 3,
            Name = "doublevolley",
            DisplayName = "Double Volley",
            Description = "Your missile volleys have a 6/12/18% chance (by rank) to immediately fire a full " +
                          "second volley at the same targets - no ammo, no stamina.",
            MaxRank = 3,
            CostPerRank = new[] { 5, 5, 5 },
            Implemented = true,
        };

        /// <summary>
        /// The re-fire chance at a given rank: base + step*(rank-1) + the equipment-mod term. Ranks 1-3
        /// with the 0.06 defaults -> 6/12/18%. Pure for testability. Returns 0 for rank &lt;= 0.
        ///
        /// <paramref name="gearModChance"/> is the Double Volley equipment mod, a MACHINERY mod: it
        /// amplifies the ability's own roll and is therefore gated behind the same rank check - a player
        /// without Double Volley gets nothing from it, because there is no volley re-fire to boost.
        /// Defaults to 0, which reproduces the pre-equipment-mod behavior exactly.
        /// </summary>
        public static float Chance(int rank, double chanceBase, double chanceStep, double gearModChance = 0.0)
        {
            if (rank <= 0)
                return 0.0f;

            return (float)Math.Max(0.0, chanceBase + (rank - 1) * chanceStep + Math.Max(0.0, gearModChance));
        }

        /// <summary>
        /// Mirrors the terms Player.TryClassAbilityDoubleVolley (Player_ClassAbilities.cs) feeds into
        /// Chance above - the two must stay in step. No affinity rider exists for this ability. No cap on
        /// the chance itself, so Effective always equals Total.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var chanceBase = PropertyManager.GetDouble("class_ability_doublevolley_chance_base").Item;
            var chanceStep = PropertyManager.GetDouble("class_ability_doublevolley_chance_step").Item;

            var skill = rank <= 0 ? 0.0 : (chanceBase + (rank - 1) * chanceStep) * 100.0;
            var gear = player.GetEquippedModValue(EquipmentModId.DoubleVolley) * 100.0;

            var total = skill + gear;

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = skill,
                Affinity = 0.0,
                Gear = gear,
                Effective = total,
                Unit = "pp",
                Label = "re-fire",
                Per = null,
                CapNote = null,
            };
        }
    }
}
