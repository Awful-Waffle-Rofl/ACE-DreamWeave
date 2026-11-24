using System.Collections.Generic;
using System.Linq;

namespace ACE.Server.ClassAbilities
{
    /// <summary>
    /// Pure check behind the unlearn-time tier gate: the learn-time "points spent in class" prerequisite for
    /// a Tier 2/3 skill only runs when its rank is 0, so removing lower-tier abilities afterwards could leave
    /// a higher-tier skill owned without its prerequisite. This finds such a stranded skill. It reads no
    /// PropertyManager key (the requirements are parameters), so it is safe to run under the test host.
    /// </summary>
    public static class ClassAbilityTierGate
    {
        /// <summary>Fallback requirements, matching the class_ability_tier2/3_spent_required defaults.</summary>
        public const int DefaultTier2SpentRequired = 5;
        public const int DefaultTier3SpentRequired = 15;

        /// <summary>
        /// The four PropertyManager keys behind the Tier 2/3 unlock thresholds. ONE copy: Player.MeetsClassAbilityTierUnlock
        /// and the web panel's tier rows (ClassAbilityPanelTierRequirements.Live) both read these.
        /// </summary>
        public const string Tier2EarnedKey = "class_ability_tier2_cap_required";
        public const string Tier2SpentKey = "class_ability_tier2_spent_required";
        public const string Tier3EarnedKey = "class_ability_tier3_cap_required";
        public const string Tier3SpentKey = "class_ability_tier3_spent_required";

        /// <summary>Which half of a Tier 2/3 unlock requirement is short, if either.</summary>
        public enum TierUnlockShortfall
        {
            None,
            Earned,
            Spent,
        }

        /// <summary>
        /// THE learn-time Tier 2/3 unlock rule, shared by Player.MeetsClassAbilityTierUnlock (which words it per
        /// skill) and the web class ability panel's per-class tier rows: lifetime-earned points first, then points
        /// spent in the class (every owned tier of it). The earned check wins when both are short, matching the
        /// order the player message always reported.
        /// </summary>
        public static TierUnlockShortfall CheckTierUnlock(int totalEarned, int spentInClass, int earnedRequired, int spentRequired)
        {
            if (totalEarned < earnedRequired)
                return TierUnlockShortfall.Earned;

            if (spentInClass < spentRequired)
                return TierUnlockShortfall.Spent;

            return TierUnlockShortfall.None;
        }

        /// <summary>
        /// Returns the first owned ability of <paramref name="cls"/> (lowest Tier, then Id) that fails its
        /// spent-in-class prerequisite under <paramref name="ranks"/>, or null. Only spend on strictly LOWER
        /// tiers counts, so two owned Tier 3 skills cannot prop each other up.
        /// </summary>
        public static ClassAbilityDefinition FindStrandedDependent(
            IReadOnlyDictionary<ClassAbilityId, int> ranks, ClassAbilityClass cls,
            int tier2Required, int tier3Required, out int lowerTierSpent, out int required)
        {
            return FindStranded(ranks, null, cls, tier2Required, tier3Required, out lowerTierSpent, out required);
        }

        /// <summary>
        /// Like <see cref="FindStrandedDependent"/> but only reports a dependent that is NEWLY stranded: owned
        /// and passing its requirement under <paramref name="before"/> (the ranks including the skill about to
        /// be removed) and failing under <paramref name="after"/>. A character already stranded by the old gap
        /// is never blocked on what was already broken, and removing a stranded skill itself is always allowed.
        /// </summary>
        public static ClassAbilityDefinition FindNewlyStrandedDependent(
            IReadOnlyDictionary<ClassAbilityId, int> before, IReadOnlyDictionary<ClassAbilityId, int> after,
            ClassAbilityClass cls, int tier2Required, int tier3Required, out int lowerTierSpent, out int required)
        {
            return FindStranded(after, before, cls, tier2Required, tier3Required, out lowerTierSpent, out required);
        }

        private static ClassAbilityDefinition FindStranded(
            IReadOnlyDictionary<ClassAbilityId, int> ranks, IReadOnlyDictionary<ClassAbilityId, int> before,
            ClassAbilityClass cls, int tier2Required, int tier3Required, out int lowerTierSpent, out int required)
        {
            lowerTierSpent = 0;
            required = 0;

            if (cls == ClassAbilityClass.None)
                return null;

            foreach (var def in OwnedOfClass(ranks, cls).Select(o => o.def).OrderBy(d => d.Tier).ThenBy(d => (int)d.Id))
            {
                if (def.Tier < 2)
                    continue;

                var need = def.Tier >= 3 ? tier3Required : tier2Required;
                var spent = LowerTierSpent(ranks, cls, def.Tier);

                if (spent >= need)
                    continue;

                // already failing before the removal (or not owned then): not caused by this removal
                if (before != null && (!before.TryGetValue(def.Id, out var beforeRank) || beforeRank <= 0
                    || LowerTierSpent(before, cls, def.Tier) < need))
                    continue;

                lowerTierSpent = spent;
                required = need;
                return def;
            }

            return null;
        }

        private static List<(ClassAbilityDefinition def, int rank)> OwnedOfClass(
            IReadOnlyDictionary<ClassAbilityId, int> ranks, ClassAbilityClass cls)
        {
            var owned = new List<(ClassAbilityDefinition def, int rank)>();
            foreach (var (id, rank) in ranks)
            {
                if (rank <= 0 || !ClassAbilityRegistry.Abilities.TryGetValue(id, out var def))
                    continue;
                if (def.AbilityClass == cls)
                    owned.Add((def, rank));
            }
            return owned;
        }

        private static int LowerTierSpent(IReadOnlyDictionary<ClassAbilityId, int> ranks, ClassAbilityClass cls, int tier) =>
            OwnedOfClass(ranks, cls).Where(o => o.def.Tier < tier).Sum(o => o.def.CumulativeCost(o.rank));
    }
}
