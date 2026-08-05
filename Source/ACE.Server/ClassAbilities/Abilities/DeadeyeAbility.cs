using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Archer T2 game-changer: a flat percentage bonus to missile damage per rank (+8%/rank), scaling
    /// with Fletching (better-made arrows hit harder). Multiplies the post-mitigation hit, so it applies
    /// to every arrow in a Multishot volley too (they all route through Player.DamageTarget).
    /// </summary>
    public class DeadeyeAbility : IOutgoingDamageAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.Deadeye,
            AbilityClass = ClassAbilityClass.Archer,
            Tier = 2,
            Name = "deadeye",
            DisplayName = "Deadeye",
            Description = "Increases your missile damage by 8% per rank (+24% at rank 3). Higher Fletching increases the bonus.",
            MaxRank = 3,
            CostPerRank = new[] { 3, 3, 3 },
            Implemented = true,
        };

        public void ModifyOutgoingDamage(Player attacker, int rank, Creature target, DamageEvent damageEvent)
        {
            if (attacker == null || damageEvent.CombatType != CombatType.Missile)
                return;

            // Deadeye equipment mod (STANDALONE): one more additive term inside this ability's own
            // parenthesis, so an owner's mod dilutes into the same axis rather than multiplying on top of
            // it. The rank-0 half lives in Player.ApplyEquipmentModOutgoingDamage and is suppressed
            // whenever this handler runs, so exactly one of the two applies the mod per hit.
            var bonus = rank * PropertyManager.GetDouble("class_ability_deadeye_percent_per_rank").Item
                + attacker.GetClassAbilityScaling(Skill.Fletching,
                    PropertyManager.GetDouble("class_ability_deadeye_fletching_per_trained").Item,
                    PropertyManager.GetDouble("class_ability_deadeye_fletching_per_spec").Item) * 0.01
                + attacker.GetEquippedModValue(EquipmentModId.Deadeye);

            damageEvent.Damage *= (float)(1.0 + bonus);
        }

        /// <summary>
        /// Mirrors the rank/affinity/gear terms in ModifyOutgoingDamage above exactly (x100 for display) -
        /// the two must stay in step. No cap on this bonus, so Effective always equals Total.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var skill = rank * PropertyManager.GetDouble("class_ability_deadeye_percent_per_rank").Item * 100.0;

            var affinity = player.GetClassAbilityScaling(Skill.Fletching,
                PropertyManager.GetDouble("class_ability_deadeye_fletching_per_trained").Item,
                PropertyManager.GetDouble("class_ability_deadeye_fletching_per_spec").Item) * 0.01 * 100.0;

            var gear = player.GetEquippedModValue(EquipmentModId.Deadeye) * 100.0;

            var total = skill + affinity + gear;

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = skill,
                Affinity = affinity,
                Gear = gear,
                Effective = total,
                Unit = "%",
                Label = "missile dmg",
                Per = null,
                CapNote = null,
            };
        }
    }
}
