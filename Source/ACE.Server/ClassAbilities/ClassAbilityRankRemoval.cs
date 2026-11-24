using System.Collections.Generic;

namespace ACE.Server.ClassAbilities
{
    /// <summary>
    /// The pure half of removing class ability ranks - a full unlearn (Player.UnlearnClassAbility) or a one-rank
    /// lower (Player.LowerClassAbilityRank, the web panel's "-" button). Player-free and PropertyManager-free so
    /// the refund amount and the strand refusal are unit-testable; Player.ValidateClassAbilityRankRemoval reads
    /// the live ranks and the tier requirements and calls in here.
    /// </summary>
    public static class ClassAbilityRankRemoval
    {
        /// <summary>
        /// The points refunded for going from <paramref name="rank"/> down to <paramref name="newRank"/>: the cost
        /// of exactly the ranks removed. A one-rank lower refunds <c>CostPerRank[rank - 1]</c>; an unlearn
        /// (newRank 0) refunds <c>CumulativeCost(rank)</c>, the figure UnlearnClassAbility has always paid.
        /// </summary>
        public static int Refund(ClassAbilityDefinition skill, int rank, int newRank)
        {
            if (newRank >= rank)
                return 0;

            return skill.CumulativeCost(rank) - skill.CumulativeCost(newRank);
        }

        /// <summary>
        /// The player-facing refusal when leaving <paramref name="skill"/> at <paramref name="newRank"/> would
        /// newly strand a higher-tier ability of the same class below its spent-in-class requirement
        /// (<see cref="ClassAbilityTierGate.FindNewlyStrandedDependent"/>), or null when the removal is allowed.
        /// <paramref name="ranks"/> is the character's current ranks and is not modified.
        ///
        /// newRank 0 is an unlearn and answers the exact sentence UnlearnClassAbility always used; a nonzero
        /// newRank is a one-rank lower and says "lower" instead.
        /// </summary>
        public static string StrandRefusal(ClassAbilityDefinition skill, IReadOnlyDictionary<ClassAbilityId, int> ranks, int newRank,
            int tier2Required, int tier3Required)
        {
            var before = new Dictionary<ClassAbilityId, int>();
            foreach (var (id, r) in ranks)
                before[id] = r;

            var after = new Dictionary<ClassAbilityId, int>(before);

            if (newRank <= 0)
                after.Remove(skill.Id);
            else
                after[skill.Id] = newRank;

            var dependent = ClassAbilityTierGate.FindNewlyStrandedDependent(before, after, skill.AbilityClass,
                tier2Required, tier3Required, out var lowerTierSpent, out var requiredSpent);

            if (dependent == null)
                return null;

            if (newRank <= 0)
                return $"You cannot unlearn {skill.DisplayName}: {dependent.DisplayName} is a Tier {dependent.Tier} {skill.AbilityClass} skill that needs at least {requiredSpent:N0} points spent on lower-tier {skill.AbilityClass} skills, and you would have {lowerTierSpent:N0}. Unlearn {dependent.DisplayName} first.";

            return $"You cannot lower {skill.DisplayName}: {dependent.DisplayName} is a Tier {dependent.Tier} {skill.AbilityClass} skill that needs at least {requiredSpent:N0} points spent on lower-tier {skill.AbilityClass} skills, and you would have {lowerTierSpent:N0}. Unlearn {dependent.DisplayName} first.";
        }
    }
}
