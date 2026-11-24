namespace ACE.Server.ClassAbilities
{
    /// <summary>
    /// The two class ability point price sentences, in ONE place: /abilities points (ClassAbilityCommands.HandlePoints)
    /// replies with them and the Character Details web view shows them, so the two can never quote different wording.
    /// Pure: the caller reads the counters and prices (Player.LumCostForClassAbilityPoints /
    /// XpCostForClassAbilityPoints) and passes them in.
    /// </summary>
    public static class ClassAbilityPointPriceText
    {
        /// <summary>The Luminance lane: points bought so far and the next point's price.</summary>
        public static string Luminance(int purchased, long nextCost)
            => $"Luminance purchases: {purchased:N0} bought so far; the next point costs {nextCost:N0} Luminance. Exchange it at the reckoning-stone by the Skillmaster in the Drift Network. Prices rise with each point and there is no cap.";

        /// <summary>
        /// The experience lane (XP-LANE-SPEC sec 3.3): locked below <paramref name="minLevel"/>, else points bought so far,
        /// the next point's price and the unassigned experience on hand.
        /// </summary>
        public static string Experience(int level, int minLevel, int purchased, long nextCost, long availableExperience)
            => level < minLevel
                ? $"Experience purchases: unlocked at level {minLevel:N0} (you are {level:N0})."
                : $"Experience purchases: {purchased:N0} bought so far; the next point costs {nextCost:N0} experience and you have {availableExperience:N0} unassigned. Prices rise with each point and there is no cap.";
    }
}
