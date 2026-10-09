using System;
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
    /// (better read of the quarry, harder-hitting volley), up to a shared cap. Stacks with multi-shot
    /// weapons: the weapon's arrows (at the weapon's own multiplier) are contributed by Player_Missile
    /// itself.
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
                          "Stacks with multi-shot weapons: their extra arrows are added together. Each extra " +
                          "arrow deals 60% of a normal arrow's damage; high Assess Creature and gear raise " +
                          "that toward a 100% ceiling.",
            MaxRank = 3,
            CostPerRank = new[] { 1, 1, 1 },   // flat 1/rank (class ability overhaul repricing, 2026-09-12)
            Implemented = true,
            AffinitySkill = Skill.AssessCreature,
        };

        /// <summary>
        /// The clamped per-extra-arrow multiplier: rankBonus * affinityMultiplier + gearMod, clamped to
        /// [rankBonus, cap]. The FLOOR is rankBonus (the flat base) itself, so the Assess Creature rider
        /// and Splitshot gear mod can only ever add; the CEILING (cap) is rank-independent - rank buys
        /// arrow COUNT only (the AddExtraShots for loop), never per-arrow damage. AddExtraShots and
        /// GetReadout both call this, so the two stay in step by construction. Pure, so it is
        /// unit-testable without a live Player (mirrors SpellAoeAbility.BaseDamageMult).
        ///
        /// rankBonus and cap are two independently settable live tunables with no cross-validation, so an
        /// operator can invert them (cap below base). Math.Clamp(value, min, max) throws when min > max,
        /// and that path is reached from the action-queued attack chain (EnqueueAction escapes the
        /// caller's try/catch), so an inverted pair must not throw. An inverted cap degrades to NO CAP
        /// (safeCap -> +Infinity) rather than clamping everything down to the now-lower cap, so the base
        /// still wins as a FLOOR - affinity and gear can still add past it - instead of a value above the
        /// base being dragged back down to it. That preserves the floor invariant above (never below
        /// rankBonus) under every tunable combination, which is the property the other tests here pin,
        /// without silently re-introducing a ceiling the operator's own (inverted) cap value did not ask
        /// for.
        /// </summary>
        public static float PerShotMultiplier(double rankBonus, double affinityMultiplier, double gearMod, double cap)
        {
            var safeCap = cap < rankBonus ? double.PositiveInfinity : cap;
            return (float)Math.Clamp(rankBonus * affinityMultiplier + gearMod, rankBonus, safeCap);
        }

        public void AddExtraShots(Player attacker, int rank, List<float> shotDamageMultipliers)
        {
            var rankBonus = PropertyManager.GetDouble("class_ability_multishot_damage_mult").Item;

            // Assess Creature is MULTIPLICATIVE on this ability's OWN per-shot multiplier (2026-09-12
            // overhaul), not an additive rider applied alongside it. At zero effective Assess Creature the
            // multiplier is exactly 1.0, leaving each extra arrow bit-identical to the flat multiplier alone.
            var affinity = attacker == null ? 1.0 : attacker.GetClassAbilityAffinityMultiplier(Skill.AssessCreature);

            // Splitshot equipment mod (STANDALONE): added OUTSIDE the affinity multiply, so gear never
            // compounds with the affinity skill. Read here rather than at a combat site, and only reachable
            // because this handler runs solely for a player who owns Multishot - there are no extra arrows
            // to strengthen without the ability.
            var gearMod = attacker == null ? 0.0 : attacker.GetEquippedModValue(EquipmentModId.Splitshot);

            // class_ability_multishot_damage_mult_cap: the 2026-10-02 owner-ruled hard cap - see
            // PerShotMultiplier above for the clamp shape.
            var cap = PropertyManager.GetDouble("class_ability_multishot_damage_mult_cap").Item;
            var perShot = PerShotMultiplier(rankBonus, affinity, gearMod, cap);

            for (var i = 0; i < rank; i++)
                shotDamageMultipliers.Add(perShot);
        }

        /// <summary>
        /// Mirrors the terms in AddExtraShots above - the two must stay in step, and both route the final
        /// multiplier through the same PerShotMultiplier. "Skill" here is the flat base multiplier
        /// (rankBonus); Affinity is the raw (unclamped) Assess Creature rider and Gear is the raw
        /// Splitshot mod, with Effective being PerShotMultiplier's clamped result. CapNote is set only
        /// when the clamp actually reduced this call's value.
        ///
        /// HasValue is now TRUE (flipped 2026-10-02, when the cap was introduced): a Skill/Affinity/Gear
        /// breakdown that only ever adds still leaves open whether a build has reached the ceiling, and a
        /// player needs to see that. Rank still only buys arrow COUNT, never per-arrow damage - that half
        /// of the old comment remains true and unchanged.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var rankBonus = PropertyManager.GetDouble("class_ability_multishot_damage_mult").Item;
            var cap = PropertyManager.GetDouble("class_ability_multishot_damage_mult_cap").Item;

            // Null-safe like AddExtraShots above (null -> neutral affinity 1.0, no gear mod), so this is
            // callable with a null player in tests, same as AcidProcAbility.GetReadout.
            var affinityMultiplier = player?.GetClassAbilityAffinityMultiplier(Skill.AssessCreature) ?? 1.0;
            var rider = rankBonus * affinityMultiplier - rankBonus;

            var gearMod = player?.GetEquippedModValue(EquipmentModId.Splitshot) ?? 0.0;

            var uncapped = rankBonus * affinityMultiplier + gearMod;
            var clamped = PerShotMultiplier(rankBonus, affinityMultiplier, gearMod, cap);

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = rankBonus * 100.0,
                Affinity = rider * 100.0,
                Gear = gearMod * 100.0,
                Effective = clamped * 100.0,
                Unit = "%",
                Label = "extra arrow",
                Per = null,
                CapNote = clamped < uncapped - 0.0000001 ? "multishot cap" : null,
            };
        }
    }
}
