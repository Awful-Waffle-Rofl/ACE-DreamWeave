using System.Collections.Generic;

using ACE.Entity.Enum;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Missile attacks strike additional nearby targets - one extra arrow per rank, each dealing
    /// the server-tunable class ability damage multiplier, boosted by a per-point Assess Creature rider
    /// (better read of the quarry, harder-hitting volley). Stacks with multi-shot weapons: the
    /// weapon's arrows (at the weapon's own multiplier) are contributed by Player_Missile itself.
    /// </summary>
    public class MultishotAbility : IMissileVolleyAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.Multishot,
            AbilityClass = ClassAbilityClass.Archer,
            Tier = 1,
            Name = "multishot",
            DisplayName = "Multishot",
            Description = "Your missile attacks strike additional nearby targets - one extra arrow per rank. " +
                          "Stacks with multi-shot weapons: their extra arrows are added together. High Assess " +
                          "Creature increases the extra arrows' damage.",
            MaxRank = 3,
            CostPerRank = new[] { 1, 2, 3 },   // Tier-1 GC: rank 1 always 1 point (the class's power splash)
            Implemented = true,
            AffinitySkill = Skill.AssessCreature,
        };

        public void AddExtraShots(Player attacker, int rank, List<float> shotDamageMultipliers)
        {
            var damageMultiplier = PropertyManager.GetDouble("class_ability_multishot_damage_mult").Item;

            // Splitshot equipment mod (MACHINERY): raises the extra-arrow multiplier itself, e.g. 0.75 ->
            // 0.77 at a perfect roll. Read here rather than at a combat site, and only reachable because
            // this handler runs solely for a player who owns Multishot - there are no extra arrows to
            // strengthen without the ability.
            damageMultiplier += attacker == null ? 0.0 : attacker.GetEquippedModValue(EquipmentModId.Splitshot);

            // Assess Creature rider: raw quotient is "percent points", *0.01 -> a damage fraction.
            var assessScale = attacker == null ? 0.0 : attacker.GetClassAbilityScaling(Skill.AssessCreature,
                PropertyManager.GetDouble("class_ability_multishot_assess_per_trained").Item,
                PropertyManager.GetDouble("class_ability_multishot_assess_per_spec").Item) * 0.01;

            var perShot = (float)(damageMultiplier * (1.0 + assessScale));

            for (var i = 0; i < rank; i++)
                shotDamageMultipliers.Add(perShot);
        }

        /// <summary>
        /// HasValue = false. AddExtraShots above computes perShot as (damageMultiplier + gearMod) * (1.0 +
        /// assessScale) - the gear term (Splitshot) and the Assess Creature rider MULTIPLY, they don't sum.
        /// The ClassAbilityReadout contract requires Skill + Affinity + Gear to equal Total meaningfully (see
        /// ClassAbilityReadout's type doc), which only holds for an additive composition; reporting Skill =
        /// damageMultiplier, Affinity = the assess-derived delta, Gear = gearMod here would drop the
        /// gearMod*assessScale cross term and silently understate Total relative to the real per-shot value.
        /// Rank also doesn't move the multiplier at all (it only adds MORE arrows at the same multiplier),
        /// so there is no rank-alone "Skill" term to report in the first place. A single scalar here would
        /// misrepresent the mechanic rather than clarify it.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            return new ClassAbilityReadout
            {
                HasValue = false,
                Skill = 0.0,
                Affinity = 0.0,
                Gear = 0.0,
                Effective = 0.0,
                Unit = "x",
                Label = "extra arrow",
                Per = null,
                CapNote = null,
            };
        }
    }
}
