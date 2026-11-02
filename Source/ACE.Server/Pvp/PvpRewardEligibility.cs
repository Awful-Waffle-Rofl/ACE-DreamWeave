namespace ACE.Server.Pvp
{
    /// <summary>
    /// Why a seat of a resolved match earns nothing from it. <see cref="None"/> means the seat is eligible. Listed in the order
    /// <see cref="PvpRewardEligibility.Evaluate"/> tests them, so a seat excluded for several reasons reports the first.
    /// </summary>
    public enum PvpRewardExclusion
    {
        /// <summary>Eligible.</summary>
        None,

        /// <summary>The match never went Live (a walkout in Staging or Countdown).</summary>
        NotLive,

        /// <summary>Opposing teams shared an IP household (pvp_arena_block_same_ip off).</summary>
        SameIpOpponents,

        /// <summary>The seat forfeited: any exit other than dying in the match.</summary>
        Forfeited,

        /// <summary>The repeat-pairing cap flagged the seat at formation (Docs/Pvp/DESIGN.md "Repeat-pairing cap").</summary>
        UnpaidRematch,

        /// <summary>The contribution floor withheld the seat (Docs/Pvp/BATTLEGROUNDS.md "Contribution floor").</summary>
        BelowContributionFloor,
    }

    /// <summary>
    /// THE per-seat eligibility rule for anything a resolved match earns: the arena Blood payout, the battleground Marks payout and the
    /// PvP daily progress facts (<see cref="PvpMatchFacts"/>) all read this one predicate, so the three can never disagree about who
    /// earned the match. Pure: every input is an argument. The payouts keep their own master switch (pvp_arena_blood_enabled) and their
    /// own amount tables; this answers only whether the seat counts at all.
    /// </summary>
    public static class PvpRewardEligibility
    {
        public static PvpRewardExclusion Evaluate(bool wentLive, bool sameIpOpponents, bool forfeited, bool unpaidRematch, bool belowContributionFloor)
        {
            if (!wentLive)
                return PvpRewardExclusion.NotLive;

            if (sameIpOpponents)
                return PvpRewardExclusion.SameIpOpponents;

            if (forfeited)
                return PvpRewardExclusion.Forfeited;

            if (unpaidRematch)
                return PvpRewardExclusion.UnpaidRematch;

            if (belowContributionFloor)
                return PvpRewardExclusion.BelowContributionFloor;

            return PvpRewardExclusion.None;
        }

        public static bool IsEligible(bool wentLive, bool sameIpOpponents, bool forfeited, bool unpaidRematch, bool belowContributionFloor)
            => Evaluate(wentLive, sameIpOpponents, forfeited, unpaidRematch, belowContributionFloor) == PvpRewardExclusion.None;
    }
}
