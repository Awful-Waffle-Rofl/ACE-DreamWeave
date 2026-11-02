using System.Collections.Generic;

namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// The minimum a claimant must have put into a run to be paid the outcome crate rather than coal (owner
    /// ruling 2026-11-01: a player under a goal's min-presence or min-contribution threshold is paid COAL).
    /// A threshold of 0 (or less) is OFF. <see cref="None"/> turns both off, which is today's behaviour.
    /// </summary>
    public readonly struct ClaimThresholds
    {
        /// <summary>Seconds the claimant must have been present in the arena (WorldEventPresence.TryGetPresentSeconds). 0 = off.</summary>
        public double MinPresenceSeconds { get; }

        /// <summary>The claimant's share of all damage dealt on the run, 0..1. 0 = off.</summary>
        public double MinContributionShare { get; }

        public ClaimThresholds(double minPresenceSeconds, double minContributionShare)
        {
            MinPresenceSeconds = minPresenceSeconds;
            MinContributionShare = minContributionShare;
        }

        /// <summary>Both thresholds off.</summary>
        public static ClaimThresholds None => new ClaimThresholds(0, 0);

        public bool PresenceOn => MinPresenceSeconds > 0;

        public bool ContributionOn => MinContributionShare > 0;
    }

    /// <summary>What a run that did not pay the success crate (a Failed* outcome) hands its claimants.</summary>
    public enum FailureRewardMode
    {
        /// <summary>The reward axis's consolation crate, one per claimant. Today's behaviour.</summary>
        Consolation,

        /// <summary>
        /// The consolation crate, tagged tier=partial on the claim line. The item is the same as
        /// <see cref="Consolation"/> in this slice; a later slice decides what Partial scales.
        /// </summary>
        Partial,

        /// <summary>Nothing: the claim is refused with <see cref="ClaimDenial.NoRewardForOutcome"/>.</summary>
        None
    }

    /// <summary>
    /// The per-run claim policy. <see cref="Default"/> reproduces the pre-pipeline claim exactly (consolation
    /// on failure, no thresholds); a later slice lets the run's objective supply its own.
    /// </summary>
    public sealed record WorldEventClaimPolicy(FailureRewardMode OnFailure, ClaimThresholds Thresholds)
    {
        public static WorldEventClaimPolicy Default { get; } = new WorldEventClaimPolicy(FailureRewardMode.Consolation, ClaimThresholds.None);

        /// <summary>
        /// One cache for every this many players present, in place of the reward axis's participantsPerCache, or null (the
        /// default) for the axis's own value. Read by WorldEventRewardDelivery when the caches are placed; only a positive
        /// value wins.
        /// </summary>
        public int? ParticipantsPerCache { get; init; }

        /// <summary>The cache rule actually used (pure): <see cref="ParticipantsPerCache"/> when positive, else <paramref name="axisValue"/>.</summary>
        public int EffectiveParticipantsPerCache(int axisValue) => ParticipantsPerCache is int n && n > 0 ? n : axisValue;
    }

    /// <summary>
    /// Every plain value the eligibility stage reads. The handler resolves them (PropertyManager, the presence
    /// and participation ledgers) and the stage stays pure.
    /// </summary>
    public readonly struct ClaimInputs
    {
        /// <summary>world_events_presence_payout_enabled.</summary>
        public bool PresenceEnabled { get; init; }

        /// <summary>The presence fraction was known (WorldEventPresence.TryGetFraction returned true).</summary>
        public bool PresenceKnown { get; init; }

        public double PresenceFraction { get; init; }

        /// <summary>WorldEventPresence.TryGetPresentSeconds returned true. Read only while a presence threshold is on.</summary>
        public bool PresentSecondsKnown { get; init; }

        public double PresentSeconds { get; init; }

        /// <summary>world_events_participation_payout_enabled (the credit rule's switch).</summary>
        public bool CreditEnabled { get; init; }

        /// <summary>WorldEventParticipation.HasCreditOrUnknown: true for any record, and for a missing ledger.</summary>
        public bool HasCredit { get; init; }

        /// <summary>The claimant's damage share could be computed (a ledger with positive total damage). Read only while a contribution threshold is on.</summary>
        public bool DamageShareKnown { get; init; }

        /// <summary>The claimant's damage over the run's total damage, 0..1.</summary>
        public double DamageShare { get; init; }

        /// <summary>WorldEvent.PaysSuccessCrate(outcome).</summary>
        public bool PaysSuccess { get; init; }

        /// <summary>The reward axis's coal wcid; 0 when the axis has none.</summary>
        public uint CoalWcid { get; init; }

        /// <summary>The claim policy; null reads as <see cref="WorldEventClaimPolicy.Default"/>.</summary>
        public WorldEventClaimPolicy Policy { get; init; }
    }

    /// <summary>Why the eligibility stage chose coal.</summary>
    public enum ClaimCoalReason
    {
        /// <summary>Not coal.</summary>
        None,

        /// <summary>The credit rule (presence switch off): no damage or contact credit.</summary>
        NoCredit,

        /// <summary>The presence rule: a known presence of exactly 0.</summary>
        NoPresence,

        /// <summary>Below the policy's MinPresenceSeconds.</summary>
        BelowPresenceThreshold,

        /// <summary>Below the policy's MinContributionShare.</summary>
        BelowContributionThreshold
    }

    /// <summary>Stage 1's answer: the presence decision as today, and whether the claimant is paid coal.</summary>
    public readonly struct ClaimEligibility
    {
        public PresencePayout Presence { get; }

        public bool PayCoal => CoalReason != ClaimCoalReason.None;

        public ClaimCoalReason CoalReason { get; }

        public ClaimEligibility(PresencePayout presence, ClaimCoalReason coalReason)
        {
            Presence = presence;
            CoalReason = coalReason;
        }
    }

    /// <summary>Stage 2's answer: what the primary give hands over, or a denial.</summary>
    public readonly struct ClaimPayout
    {
        /// <summary><see cref="ClaimDenial.None"/>, or <see cref="ClaimDenial.NoRewardForOutcome"/>.</summary>
        public ClaimDenial Denial { get; }

        /// <summary>The outcome crate (success or consolation); the fallback when a coal create fails.</summary>
        public uint CrateWcid { get; }

        /// <summary>Coal when <see cref="PayCoal"/>, otherwise <see cref="CrateWcid"/>.</summary>
        public uint PayoutWcid { get; }

        public bool PayCoal { get; }

        public bool PaysSuccess { get; }

        /// <summary>The presence decision, for WorldEventClaimRules.ResolveStamp / PresenceMessage / PresenceLogSuffix.</summary>
        public PresencePayout Presence { get; }

        public ClaimCoalReason CoalReason { get; }

        /// <summary>The claim line's tier= value, or null when the claim is default (no tier= field).</summary>
        public string Tier { get; }

        public ClaimPayout(ClaimDenial denial, uint crateWcid, uint payoutWcid, bool payCoal, bool paysSuccess, PresencePayout presence, ClaimCoalReason coalReason, string tier)
        {
            Denial = denial;
            CrateWcid = crateWcid;
            PayoutWcid = payoutWcid;
            PayCoal = payCoal;
            PaysSuccess = paysSuccess;
            Presence = presence;
            CoalReason = coalReason;
            Tier = tier;
        }
    }

    /// <summary>Stage 3's answer: bonus crate wcids given after the primary, inside the claim lock. Empty by default.</summary>
    public sealed class ClaimExtras
    {
        public static ClaimExtras Empty { get; } = new ClaimExtras(new List<uint>());

        public IReadOnlyList<uint> Wcids { get; }

        public int Count => Wcids.Count;

        public ClaimExtras(IReadOnlyList<uint> wcids)
        {
            Wcids = wcids ?? new List<uint>();
        }
    }
}
