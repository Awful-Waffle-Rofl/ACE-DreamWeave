using System;

using ACE.Entity.Enum;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Void/Summon T1 splash: every hit one of the caster's combat pets lands heals THAT PET for
    /// 2/4/6/8/10% of the damage it dealt, by rank. The leech goes to the pet, never to the caster - it is a
    /// pet-survivability entry, not a second health pool for the summoner - which is what keeps it from
    /// stacking into the caster's own sustain alongside the class's existing drain effects.
    ///
    /// LIVE 2026-09-12: hooked at the same four pet-landed-hit sites Pet.NotifyOwnerOfDamage already covers
    /// (Creature.TakeDamage for melee/missile, SpellProjectile.DamageTarget, WorldObject_Magic's Boost/Harm
    /// and Transfer/Drain branches), reading Pet.ApplyUmbralSiphonLeech right beside each existing
    /// NotifyOwnerOfDamage call. PvE only - every call site gates on the target NOT being a Player before
    /// calling in, mirroring Umbral Siphon's own healing direction (the pet, never a person).
    ///
    /// AFFINITY: Loyalty multiplies the leech. AffinitySkill = Skill.Loyalty is declared here, and the
    /// matching player.GetClassAbilityAffinityMultiplier(Skill.Loyalty) call lives in this same file's
    /// GetReadout, per ClassAbilityAffinityDeclarationTests.EveryHandlerScalingCall_IsDeclaredAsAffinitySkill.
    /// The live mechanic's own affinity read happens on Player (GetUmbralSiphonLeechFraction in
    /// Player_ClassAbilityBuffs.cs), which is fine - that file is not scanned by the declaration tests.
    ///
    /// Carries IPassiveStatAbility, same as EmpoweredSummonsAbility, because it hooks no combat-event
    /// interface of its own - the mechanic is a bespoke call (Pet.ApplyUmbralSiphonLeech) from the four
    /// pet-landed-hit sites, not something ClassAbilityRegistry dispatches through a hook bucket.
    /// </summary>
    public class UmbralSiphonAbility : IClassAbility, IPassiveStatAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.UmbralSiphon,
            AbilityClass = ClassAbilityClass.VoidSummon,
            Tier = 1,
            Name = "umbral_siphon",
            DisplayName = "Umbral Siphon",
            Description = "Every hit one of your combat pets lands heals that pet for 2/4/6/8/10% of the " +
                          "damage it dealt (by rank). Higher Loyalty multiplies the leech.",
            MaxRank = 5,
            CostPerRank = new[] { 1, 1, 1, 1, 1 },
            Implemented = true,
            AffinitySkill = Skill.Loyalty,
        };

        /// <summary>
        /// The fraction of a pet's dealt damage healed back to it at a given rank (0.0 = none): rank *
        /// percentPerRank, plus the Loyalty rider fraction. Pure for testability - mirrors the shape of
        /// EmpoweredSummonsAbility.StatMultiplier/LeechFraction, but returns an additive FRACTION rather than
        /// a 1+bonus multiplier, since this feeds a heal amount directly rather than scaling a stat.
        /// </summary>
        public static double LeechFraction(int rank, double percentPerRank, double loyaltyFraction)
        {
            if (rank <= 0)
                return 0.0;

            var rankBonus = Math.Max(0, rank) * percentPerRank;

            return rankBonus + Math.Max(0.0, loyaltyFraction);
        }

        /// <summary>
        /// Mirrors the rank/affinity terms fed into LeechFraction above (x100 for display) - the two must
        /// stay in step. No gear term: Umbral Siphon carries no equipment mod.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var rankBonus = rank <= 0 ? 0.0 : rank * PropertyManager.GetDouble("class_ability_umbralsiphon_percent_per_rank").Item;

            // Affinity is a MULTIPLIER on the rank term, reported as the AMOUNT it adds so the displayed
            // terms stay in one unit and still sum to Effective.
            var multiplier = player.GetClassAbilityAffinityMultiplier(Skill.Loyalty);

            var skill = rankBonus * 100.0;

            var affinity = (rankBonus * multiplier - rankBonus) * 100.0;

            var total = skill + affinity;

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = skill,
                Affinity = affinity,
                Gear = 0.0,
                Effective = total,
                Unit = "%",
                Label = "pet self-heal",
                Per = null,
                CapNote = null,
            };
        }
    }
}
