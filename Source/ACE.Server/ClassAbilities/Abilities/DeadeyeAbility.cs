using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Archer T2 game-changer: a flat percentage bonus to missile damage per rank (+9.5%/rank), scaling
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
            Description = "Increases your missile damage by 9.5% per rank (+28.5% at rank 3). Higher Fletching multiplies the bonus.",
            MaxRank = 3,
            CostPerRank = new[] { 1, 1, 1 },
            Implemented = true,
            AffinitySkill = Skill.Fletching,
        };

        public void ModifyOutgoingDamage(Player attacker, int rank, Creature target, DamageEvent damageEvent)
        {
            if (attacker == null || damageEvent.CombatType != CombatType.Missile)
                return;

            var rankBonus = rank * PropertyManager.GetDouble("class_ability_deadeye_percent_per_rank").Item;

            // Fletching is MULTIPLICATIVE on this ability's OWN rank bonus (2026-09-12 overhaul), not an
            // additive rider beside it - so the skill is worth more the more ranks you have bought, and
            // worth exactly nothing at rank 0. At zero effective Fletching the multiplier is exactly 1.0,
            // leaving the hit bit-identical to rank alone.
            var affinity = attacker.GetClassAbilityAffinityMultiplier(Skill.Fletching);

            // Deadeye equipment mod (STANDALONE): one more additive term inside this ability's own
            // parenthesis, so an owner's mod dilutes into the same axis rather than multiplying on top of
            // it. The rank-0 half lives in Player.ApplyEquipmentModOutgoingDamage and is suppressed
            // whenever this handler runs, so exactly one of the two applies the mod per hit. It stays
            // OUTSIDE the affinity multiply: gear must never compound with the affinity skill.
            var bonus = rankBonus * affinity + attacker.GetEquippedModValue(EquipmentModId.Deadeye);

            damageEvent.Damage *= (float)(1.0 + bonus);
        }

        /// <summary>
        /// Mirrors the rank/affinity/gear terms in ModifyOutgoingDamage above exactly (x100 for display) -
        /// the two must stay in step. No cap on this bonus, so Effective always equals Total.
        ///
        /// Affinity is a MULTIPLIER on the rank term, but it is reported as the AMOUNT that multiplier adds
        /// (rankBonus * affinity - rankBonus), so the three displayed terms stay in the same unit and still
        /// sum to the effective bonus. Reporting the bare factor instead would put a number like "1.34"
        /// beside two percentages.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var rankBonus = rank * PropertyManager.GetDouble("class_ability_deadeye_percent_per_rank").Item;

            var multiplier = player.GetClassAbilityAffinityMultiplier(Skill.Fletching);

            var skill = rankBonus * 100.0;

            var affinity = (rankBonus * multiplier - rankBonus) * 100.0;

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
