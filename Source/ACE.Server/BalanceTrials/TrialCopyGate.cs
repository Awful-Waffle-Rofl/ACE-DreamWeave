using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.Managers;

namespace ACE.Server.BalanceTrials
{
    /// <summary>
    /// The in-memory gate that keeps two copies (or a copy and a purge) from running on the same trial character at once.
    /// Every operation that purges or copies a master or a rep takes it before its purge and releases it when it is done:
    ///
    /// - @trial rebuild: the rep EXCLUSIVE (it is purged and written) and the master SHARED (it is only read);
    /// - @trial promote: the master EXCLUSIVE (purged and written) and the rep SHARED (only read);
    /// - @trial unpromote: the master EXCLUSIVE.
    ///
    /// An exclusive hold conflicts with any other hold on that guid; a shared hold conflicts only with an exclusive one.
    /// So a promote and a rebuild never touch the same master or rep together, while several rebuilds from one master
    /// (different reps) may still run back to back without waiting for each other's copy.
    ///
    /// A hold is released by its token: on the copy's onDone (true or false), or at once on any refusal or purge failure
    /// after the take. A copy that never reports (a swallowed shard read) cannot wedge the gate: a hold lapses after
    /// <see cref="HoldSeconds"/>, and a late release of a lapsed token is a no-op, so it cannot free a newer hold.
    ///
    /// THE BROKEN FLAG. A master is marked broken from the moment promote or unpromote starts purging it until a promote's
    /// copy lands on it (or an unpromote's purge completes). A broken master is in an unknown state, partly purged or
    /// empty, so rebuild refuses it; promote and unpromote do not, since re-running one of them is the recovery. The flag
    /// does NOT lapse with the hold: a copy that never reports leaves the master broken, which is the safe reading.
    ///
    /// A late onDone(false) from a hold that has already lapsed still marks the master broken, even if a newer promote has
    /// since landed a healthy copy. That is deliberate and conservative: a false alarm costs one re-run of promote, while
    /// a missed one would let rebuild copy from a master in an unknown state.
    ///
    /// THE LINEAGE. A promote or unpromote records the master's archetype and calibration shas with the broken flag, read
    /// from the stamp BEFORE its purge. The purge deletes the master's biota and with it the stamp, so a promote whose copy
    /// then fails leaves a master with no stamp; a re-run of promote takes the shas from this record instead
    /// (<see cref="TryGetBrokenLineage"/>), and nothing else can recover the slot while the server runs.
    ///
    /// In memory only: a server restart forgets every hold, every broken flag and every lineage. After a restart, a master
    /// left without a stamp is recovered by a reseed (seed-trial-archetypes), which fills any slot whose stamp is not promoted.
    /// Thread-safe: callers run on the world thread, onDone callbacks on any thread.
    /// </summary>
    public sealed class TrialCopyGate
    {
        /// <summary>How long a hold lasts with no release (5 minutes).</summary>
        public const double HoldSeconds = 300.0;

        /// <summary>The one gate the live commands share.</summary>
        public static TrialCopyGate Shared { get; } = new TrialCopyGate(TrialManager.Now);

        private sealed class Hold
        {
            public long Token;
            public bool Exclusive;
            public double Until;
        }

        private readonly object sync = new object();
        private readonly Func<double> clock;
        private readonly Dictionary<uint, List<Hold>> holds = new Dictionary<uint, List<Hold>>();
        private sealed class BrokenRecord
        {
            public string Reason;
            public string ArchetypeSha;
            public string CalibrationSha;

            /// <summary>The clock reading when this mark was made.</summary>
            public double MarkedAt;

            /// <summary>True for the mark a purge-then-copy makes before its purge: a copy is (or was) on its way and has not reported.</summary>
            public bool CopyInFlight;

            /// <summary>Unique per mark (every MarkBroken bumps it), so a late copy can clear only the mark it was made under.</summary>
            public long Generation;
        }

        private readonly Dictionary<uint, BrokenRecord> broken = new Dictionary<uint, BrokenRecord>();
        private long lastToken;
        private long lastGeneration;

        /// <param name="clock">Seconds on a monotonic clock (TrialManager.Now live; a fake in tests).</param>
        public TrialCopyGate(Func<double> clock)
        {
            this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        /// <summary>
        /// Takes <paramref name="exclusive"/> exclusively and <paramref name="shared"/> shared, all or nothing. A guid of 0
        /// in <paramref name="shared"/> is ignored. On false, <paramref name="busyGuid"/> names the first guid that is held.
        /// </summary>
        public bool TryTake(uint exclusive, uint shared, out long token, out uint busyGuid)
        {
            lock (sync)
            {
                var now = clock();

                if (Conflicts(exclusive, true, now))
                    return Busy(exclusive, out token, out busyGuid);

                if (shared != 0 && Conflicts(shared, false, now))
                    return Busy(shared, out token, out busyGuid);

                token = ++lastToken;
                busyGuid = 0;

                Add(exclusive, token, true, now);

                if (shared != 0)
                    Add(shared, token, false, now);

                return true;
            }
        }

        /// <summary>Releases every hold taken under <paramref name="token"/>. A lapsed or unknown token is a no-op. True when a live hold was released.</summary>
        public bool Release(long token)
        {
            lock (sync)
                return ReleaseLocked(token);
        }

        /// <summary>
        /// A copy onto <paramref name="master"/> landed: releases <paramref name="token"/> and clears the broken flag, atomically,
        /// but ONLY while the token is still live. A lapsed token's late success must not clear a flag a newer promote or
        /// unpromote has set since (its purge has run again, so the master is in an unknown state once more).
        /// </summary>
        public bool ReleaseAndClearBroken(long token, uint master)
        {
            lock (sync)
            {
                if (!ReleaseLocked(token))
                    return false;

                broken.Remove(master);
                return true;
            }
        }

        private bool ReleaseLocked(long token)
        {
            var now = clock();
            var released = false;

            foreach (var guid in holds.Keys.ToList())
            {
                var list = holds[guid];

                // Lapsed holds go first, so a lapsed token is never counted as released.
                list.RemoveAll(h => h.Until <= now);

                if (list.RemoveAll(h => h.Token == token) > 0)
                    released = true;

                if (list.Count == 0)
                    holds.Remove(guid);
            }

            return released;
        }

        /// <summary>True when any unexpired hold, shared or exclusive, is on <paramref name="guid"/>.</summary>
        public bool IsBusy(uint guid)
        {
            lock (sync)
                return Live(guid, clock()).Any();
        }

        /// <summary>
        /// Marks <paramref name="master"/> broken, at the current clock reading. When both shas are given they become its lineage;
        /// when they are not, a lineage recorded earlier is kept (a later mark only updates the reason, the time and the flag).
        /// <paramref name="copyInFlight"/> is true only for the mark made just before a purge-then-copy's purge (see
        /// <see cref="TryGetCopyInFlight"/>); every other mark, including a copy's own failure, clears it.
        /// </summary>
        /// <returns>The mark's generation, for <see cref="ClearBrokenIfGeneration"/>.</returns>
        public long MarkBroken(uint master, string reason, string archetypeSha = null, string calibrationSha = null, bool copyInFlight = false)
        {
            lock (sync)
            {
                broken.TryGetValue(master, out var previous);

                var hasLineage = !string.IsNullOrEmpty(archetypeSha) && !string.IsNullOrEmpty(calibrationSha);

                broken[master] = new BrokenRecord
                {
                    Reason = reason ?? "unknown",
                    ArchetypeSha = hasLineage ? archetypeSha : previous?.ArchetypeSha,
                    CalibrationSha = hasLineage ? calibrationSha : previous?.CalibrationSha,
                    MarkedAt = clock(),
                    CopyInFlight = copyInFlight,
                    Generation = ++lastGeneration,
                };

                return lastGeneration;
            }
        }

        /// <summary>
        /// A copy that landed AFTER its hold lapsed: clears <paramref name="master"/>'s broken record only while it is still the mark
        /// of <paramref name="generation"/> (the one made before that copy's purge). Any later mark, by a newer promote, adopt or
        /// unpromote, has a newer generation and is left alone. True when it cleared.
        /// </summary>
        public bool ClearBrokenIfGeneration(uint master, long generation)
        {
            lock (sync)
            {
                if (!broken.TryGetValue(master, out var record) || record.Generation != generation)
                    return false;

                broken.Remove(master);
                return true;
            }
        }

        public void ClearBroken(uint master)
        {
            lock (sync)
                broken.Remove(master);
        }

        public bool IsBroken(uint master, out string reason)
        {
            lock (sync)
            {
                if (broken.TryGetValue(master, out var record))
                {
                    reason = record.Reason;
                    return true;
                }

                reason = null;
                return false;
            }
        }

        /// <summary>
        /// True when <paramref name="master"/>'s broken mark is a purge-then-copy's "copy has not landed" mark, with
        /// <paramref name="ageSeconds"/> the time since it was made. Such a copy may still land after its hold lapses (a copy that
        /// never reports cannot be told from a slow one), so the next purge of that master waits for it (TrialPromote.TryPurgeThenCopy).
        /// </summary>
        public bool TryGetCopyInFlight(uint master, out double ageSeconds)
        {
            lock (sync)
            {
                if (broken.TryGetValue(master, out var record) && record.CopyInFlight)
                {
                    ageSeconds = clock() - record.MarkedAt;
                    return true;
                }

                ageSeconds = 0;
                return false;
            }
        }

        /// <summary>The archetype and calibration shas recorded with <paramref name="master"/>'s broken flag (see THE LINEAGE), if any.</summary>
        public bool TryGetBrokenLineage(uint master, out string archetypeSha, out string calibrationSha)
        {
            lock (sync)
            {
                if (broken.TryGetValue(master, out var record) && !string.IsNullOrEmpty(record.ArchetypeSha) && !string.IsNullOrEmpty(record.CalibrationSha))
                {
                    archetypeSha = record.ArchetypeSha;
                    calibrationSha = record.CalibrationSha;
                    return true;
                }

                archetypeSha = null;
                calibrationSha = null;
                return false;
            }
        }

        private bool Conflicts(uint guid, bool wantExclusive, double now)
            => Live(guid, now).Any(h => wantExclusive || h.Exclusive);

        private IEnumerable<Hold> Live(uint guid, double now)
        {
            if (!holds.TryGetValue(guid, out var list))
                return Enumerable.Empty<Hold>();

            list.RemoveAll(h => h.Until <= now);

            if (list.Count == 0)
                holds.Remove(guid);

            return list.ToArray();
        }

        private void Add(uint guid, long token, bool exclusive, double now)
        {
            if (!holds.TryGetValue(guid, out var list))
                holds[guid] = list = new List<Hold>();

            list.Add(new Hold { Token = token, Exclusive = exclusive, Until = now + HoldSeconds });
        }

        private static bool Busy(uint guid, out long token, out uint busyGuid)
        {
            token = 0;
            busyGuid = guid;
            return false;
        }
    }
}
