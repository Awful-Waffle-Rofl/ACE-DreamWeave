using System;

using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Archmage T2 game-changer: a constant +5% war-magic cast-speed increase per rank (+15% at rank 3),
    /// no stacks or ramp-up. Rides the proven EffectiveCastSpeed path (Player.ApplyClassAbilityCastSpeed),
    /// the same multiplier field Nether Rush feeds - Flat Cast Speed applies to War Magic, Nether Rush to
    /// Void Magic, and they compose cleanly for a hybrid caster. Bespoke; carries IPassiveStatAbility.
    /// </summary>
    public class FlatCastSpeedAbility : IClassAbility, IPassiveStatAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.FlatCastSpeed,
            AbilityClass = ClassAbilityClass.Archmage,
            Tier = 2,
            Name = "flatcastspeed",
            DisplayName = "Flat Cast Speed",
            Description = "Permanently increases your war-magic cast speed by 10% per rank (+30% at rank 3).",
            MaxRank = 3,
            CostPerRank = new[] { 1, 1, 1 },
            Implemented = true,
        };

        /// <summary>The constant cast-speed multiplier at a given rank (1.0 = none): 1 + rank*perRank.</summary>
        public static float CastSpeedMultiplier(int rank, double percentPerRank)
        {
            if (rank <= 0)
                return 1.0f;

            return (float)(1.0 + Math.Max(0, rank) * percentPerRank);
        }

        /// <summary>
        /// Mirrors the rank term in CastSpeedMultiplier above (x100 for display) - no affinity rider and no
        /// equipment mod exist for this ability (no EquipmentModId.FlatCastSpeed entry), so both are 0.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var skill = (rank <= 0 ? 0.0 : rank * PropertyManager.GetDouble("class_ability_flatcastspeed_percent_per_rank").Item) * 100.0;

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = skill,
                Affinity = 0.0,
                Gear = 0.0,
                Effective = skill,
                Unit = "%",
                Label = "cast speed",
                Per = null,
                CapNote = null,
            };
        }
    }
}
