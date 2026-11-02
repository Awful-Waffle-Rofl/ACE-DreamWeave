using System.Collections.Generic;

using ACE.Entity;

namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// The presence idle rule: is a player present but doing nothing? Pure - it is fed a guid, a clock value,
    /// a position, a damage total and the afk flag, and never touches a Player or Session - so the rule is unit
    /// testable with a fake clock.
    ///
    /// RULE. A player is idle when they are flagged afk, OR when they have neither moved more than
    /// <see cref="MoveThresholdMeters"/> nor raised their damage total for at least afkSeconds. The first time
    /// a guid is seen counts as activity (present for the first time is not idle). A null position compares as
    /// moved (Position.DistanceTo(null) is float.MaxValue), so a player with no position is never idle on the
    /// movement rule.
    ///
    /// STALE RECORDS. A guid whose previous observation is at least 2 x samplePeriodSeconds old (it missed a
    /// sample: logged out, or otherwise absent from the online list) is treated as a first sighting again, so
    /// a relog never inherits the idle clock of an earlier stretch.
    ///
    /// OFF. afkSeconds of 0 or less (or NaN) turns the rule off: <see cref="IsIdle"/> is always false, even for
    /// a flagged-afk player, and nothing is tracked.
    ///
    /// THREADING. Not world-thread only: the presence sampler runs on the world thread from the run's Tick,
    /// but also from Finish -> ClosePresence, which WorldEventManager.StopCurrent / TryStopCurrent reach from a
    /// command or admin thread while the world thread may be inside the same run's Tick (the manager ticks the
    /// run outside its own lock). Every record access therefore takes <see cref="sync"/>, a private lock held
    /// only for plain dictionary work. Same pattern as WorldEventPresence.
    /// </summary>
    public sealed class WorldEventIdleTracker
    {
        /// <summary>Movement beyond this distance between two calls counts as activity.</summary>
        public const float MoveThresholdMeters = 0.5f;

        /// <summary>
        /// The default sample period: a run's presence is sampled once per WorldEventManager tick, which runs
        /// at most once a second.
        /// </summary>
        public const double DefaultSamplePeriodSeconds = 1.0;

        private sealed class Record
        {
            public Position LastPosition;
            public float LastDamage;
            public double LastActiveAt;
            public double LastSeenAt;
        }

        private readonly object sync = new object();

        private readonly Dictionary<uint, Record> records = new Dictionary<uint, Record>();

        public WorldEventIdleTracker(double afkSeconds, double samplePeriodSeconds = DefaultSamplePeriodSeconds)
        {
            AfkSeconds = afkSeconds;
            StaleAfterSeconds = 2.0 * (samplePeriodSeconds > 0 ? samplePeriodSeconds : DefaultSamplePeriodSeconds);
        }

        /// <summary>The idle window in seconds, as constructed.</summary>
        public double AfkSeconds { get; }

        /// <summary>A gap since the previous observation at least this long makes the next one a first sighting.</summary>
        public double StaleAfterSeconds { get; }

        /// <summary>True when the rule is on (afkSeconds above 0).</summary>
        public bool Enabled => AfkSeconds > 0;

        /// <summary>How many guids are tracked. Always 0 while the rule is off.</summary>
        public int TrackedCount
        {
            get
            {
                lock (sync)
                    return records.Count;
            }
        }

        /// <summary>
        /// Records this observation and answers whether the player is idle as of <paramref name="now"/>.
        /// Activity (first sighting, a stale record, movement beyond the threshold, or a damage total above the
        /// last one seen) resets the idle clock to <paramref name="now"/>.
        /// </summary>
        public bool IsIdle(uint guid, double now, Position position, float damageDealt, bool flaggedAfk)
        {
            if (!Enabled)
                return false;

            var copy = position != null ? new Position(position) : null;

            lock (sync)
            {
                if (!records.TryGetValue(guid, out var rec))
                {
                    records[guid] = rec = new Record { LastActiveAt = now };
                }
                else
                {
                    var stale = now - rec.LastSeenAt >= StaleAfterSeconds;
                    var moved = rec.LastPosition == null || Moved(rec.LastPosition, position);

                    if (stale || moved || damageDealt > rec.LastDamage)
                        rec.LastActiveAt = now;
                }

                rec.LastPosition = copy;
                rec.LastDamage = damageDealt;
                rec.LastSeenAt = now;

                if (flaggedAfk)
                    return true;

                return now - rec.LastActiveAt >= AfkSeconds;
            }
        }

        /// <summary>Movement test: a realm instance change, or a distance beyond the threshold.</summary>
        private static bool Moved(Position last, Position current)
        {
            if (current == null)
                return true;

            if (last.Instance != current.Instance)
                return true;

            return last.DistanceTo(current) > MoveThresholdMeters;
        }
    }
}
