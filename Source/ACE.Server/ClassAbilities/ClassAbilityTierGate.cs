namespace ACE.Server.ClassAbilities
{
    /// <summary>
    /// The pure "class tier" unlock rule for the Drift Network trainers. In the class/tier design each class
    /// skill sits in one of three tiers - T1 is open, T2 and T3 are gated - and reaching a gated tier requires
    /// the player to have both <em>earned</em> enough class ability points and <em>invested</em> enough of them
    /// in that class. Kept as a pure static (no live Player) so the thresholds are unit-testable, mirroring
    /// <see cref="ClassAbilityTokenCatalog.Evaluate"/>.
    ///
    /// Thresholds are from the class-ability design tables (Docs/ClassAbilities):
    ///   T1 - open
    ///   T2 - 3 class ability points earned  + 5 points spent in the class
    ///   T3 - 8 class ability points earned  + 15 points spent in the class
    ///
    /// NOTE: this is a distinct axis from the token "tier" in <see cref="ClassAbilityTokenCatalog"/>, which is a
    /// single skill's <em>rank</em> (rank-order is handled there by Evaluate). This gate is about which skills
    /// within a class are reachable at all.
    ///
    /// SUPERSEDED: the live Tier 2/3 gate now runs in
    /// <see cref="ACE.Server.WorldObjects.Player.MeetsClassAbilityTierUnlock"/>, which reads the real skill
    /// class/tier (ClassAbilityDefinition.AbilityClass / Tier, added in #86) and the player's
    /// <see cref="ACE.Server.WorldObjects.Player.PointsSpentInClass"/>, with the thresholds as tunable
    /// properties. This pure type is retained for its unit-tested threshold encoding.
    /// </summary>
    public static class ClassAbilityTierGate
    {
        public const int Tier2PointsEarned = 3;
        public const int Tier2PointsSpentInClass = 5;
        public const int Tier3PointsEarned = 8;
        public const int Tier3PointsSpentInClass = 15;

        /// <summary>The (earned, spentInClass) minimums to reach the given class tier. T1 (and below) is open.</summary>
        public static (int earned, int spentInClass) RequirementFor(int classTier) => classTier switch
        {
            <= 1 => (0, 0),
            2 => (Tier2PointsEarned, Tier2PointsSpentInClass),
            _ => (Tier3PointsEarned, Tier3PointsSpentInClass),   // tier 3 and any higher tier the design may add
        };

        /// <summary>
        /// TRUE if a class tier is unlocked for a player who has earned <paramref name="totalPointsEarned"/>
        /// class ability points lifetime and spent <paramref name="pointsSpentInClass"/> of them in this class.
        /// On FALSE, <paramref name="reason"/> is a player-facing clause ("requires 5 ... spent in this class").
        /// </summary>
        public static bool IsTierUnlocked(int classTier, int totalPointsEarned, int pointsSpentInClass, out string reason)
        {
            reason = null;

            var (needEarned, needSpent) = RequirementFor(classTier);

            if (totalPointsEarned < needEarned)
            {
                reason = $"requires {needEarned:N0} class ability points earned (you have earned {totalPointsEarned:N0})";
                return false;
            }

            if (pointsSpentInClass < needSpent)
            {
                reason = $"requires {needSpent:N0} class ability points spent in this class (you have spent {pointsSpentInClass:N0})";
                return false;
            }

            return true;
        }
    }
}
