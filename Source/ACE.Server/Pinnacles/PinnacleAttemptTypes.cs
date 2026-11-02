using System;

namespace ACE.Server.Pinnacles
{
    /// <summary>
    /// What one Pinnacle attempt reports when it ends (<see cref="IPinnacleWorld.ReportEnded"/>): the End call's own
    /// arguments plus the fight facts the telemetry needs, captured by PinnacleFight.End so the world needs nothing
    /// from the core afterwards. Immutable.
    /// </summary>
    public sealed class PinnacleAttemptEnd
    {
        public PinnacleAttemptEnd(PinnacleFightState outcome, string reason, bool refund, bool cacheDelivered, bool charged,
            int phase, int phaseCount, int knotLevel, uint knotGuid, uint ownerGuid)
        {
            Outcome = outcome;
            Reason = reason;
            Refund = refund;
            CacheDelivered = cacheDelivered;
            Charged = charged;
            Phase = phase;
            PhaseCount = phaseCount;
            KnotLevel = knotLevel;
            KnotGuid = knotGuid;
            OwnerGuid = ownerGuid;
        }

        /// <summary>Won or Lost, as End set the final state.</summary>
        public PinnacleFightState Outcome { get; }

        /// <summary>The raw End reason text ("hold expired", "fault in tick", ...).</summary>
        public string Reason { get; }

        /// <summary>A win whose cache was never delivered: the entry was refunded.</summary>
        public bool Refund { get; }

        /// <summary>The cache reached the owner, whatever ended the fight afterwards.</summary>
        public bool CacheDelivered { get; }

        /// <summary>The attempt had been charged an entry.</summary>
        public bool Charged { get; }

        /// <summary>The phase the fight was in when it ended.</summary>
        public int Phase { get; }

        public int PhaseCount { get; }

        public int KnotLevel { get; }

        public uint KnotGuid { get; }

        public uint OwnerGuid { get; }
    }

    /// <summary>
    /// One delivered Knot clear (<see cref="IPinnacleWorld.ReportCleared"/>). Immutable. Both clocks are captured at the win:
    /// WallSeconds runs from the fight's Begin (deaths and run-backs count), FightSeconds only while the fight was Running.
    /// </summary>
    public sealed class PinnacleClear
    {
        public PinnacleClear(int knotLevel, double wallSeconds, double fightSeconds, uint ownerGuid)
        {
            KnotLevel = knotLevel;
            WallSeconds = wallSeconds;
            FightSeconds = fightSeconds;
            OwnerGuid = ownerGuid;
        }

        public int KnotLevel { get; }

        public double WallSeconds { get; }

        public double FightSeconds { get; }

        public uint OwnerGuid { get; }

        /// <summary>
        /// THE TIME SWITCH: the seconds the Knot leaderboard records. Owner ruling 2026-10-22: wall clock, deaths count
        /// (<see cref="WallSeconds"/>). FightSeconds stays captured; to rank by the Running-only fight clock instead, change
        /// this one expression to <see cref="FightSeconds"/>.
        /// </summary>
        public double Seconds => WallSeconds;
    }

    /// <summary>
    /// Who opened one Pinnacle copy and when, captured on the owner's thread at <c>CreateCopy</c> so the attempt's
    /// telemetry rows (copy_open, entry, attempt_end) can be written later from the fight's landblock thread with no
    /// further lookups. <see cref="AttemptId"/> is minted once per copy and ties those rows together.
    /// </summary>
    public sealed class PinnacleAttemptContext
    {
        public PinnacleAttemptContext(string pinnacleKey, string ownerName, uint accountId, int? ownerLevel, uint instanceId)
        {
            AttemptId = Guid.NewGuid().ToString("N");
            PinnacleKey = pinnacleKey;
            OwnerName = ownerName;
            AccountId = accountId;
            OwnerLevel = ownerLevel;
            InstanceId = instanceId;
            OpenedUtc = DateTime.UtcNow;
        }

        /// <summary>32 hex characters, minted per copy.</summary>
        public string AttemptId { get; }

        public string PinnacleKey { get; }

        public string OwnerName { get; }

        public uint AccountId { get; }

        public int? OwnerLevel { get; }

        /// <summary>The copy's ephemeral instance id. It RECYCLES; never a join key.</summary>
        public uint InstanceId { get; }

        public DateTime OpenedUtc { get; }
    }
}
