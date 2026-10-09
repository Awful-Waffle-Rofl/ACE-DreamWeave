using System;
using System.Globalization;
using System.Reflection;

using log4net;

namespace ACE.Server.Entity.AccountVault
{
    /// <summary>
    /// Names the one fold pass that ran long, so an operator has something to act on.
    ///
    /// ONE OUTPUT, one plain log4net line:
    ///
    ///   [VAULT] fold SLOW PASS ...  - WARN, the moment a single pass takes <see cref="SlowPassMs"/> or
    ///                                 more, rate-limited to one line per
    ///                                 <see cref="SlowPassLogIntervalSeconds"/> so a persistently slow
    ///                                 fleet logs once per window rather than once per pass.
    ///
    /// WHY THE SLOW-TICK LOGGER CANNOT ANSWER THIS. A pass costing 30-50 ms sits ENTIRELY BELOW the
    /// slow-tick threshold, so the slow-tick capture would never record one. That blindness is not
    /// hypothetical for this subsystem: the prod fit of the vault deposit cost produced a NEGATIVE
    /// intercept precisely because sub-threshold time is invisible to that logger, so the observed stall
    /// undercounted. A pass that wants to report its own duration has to time itself.
    ///
    /// WHAT A PASS IS, for reading the line: the candidate scan under stateLock, then per candidate the
    /// class predicate, the round-trip self-check, the take-out-of-the-vault and the class credit, ending
    /// with the biota destroy. Items EXAMINED is the candidate count and items FOLDED is how many of them
    /// really collapsed, so the two differ whenever the predicate refuses and the line carries both.
    ///
    /// THERE IS NO PERIODIC ROLL-UP, and its absence is deliberate rather than an omission. A second
    /// emitter here used to write a `[VAULT] fold over the last 60s` INFO line, drained once per heartbeat
    /// by the background fold rotation, and it went when the rotation did (2026-09-26) along with the
    /// dispatch counters that had measured that rotation's own wait inside store.Enqueue. Keeping it would
    /// have meant keeping an emitter nothing called, and its throughput numbers are already in
    /// /vaultclassfold's progress line, which prints folded, examined, batches and elapsed seconds every
    /// ten seconds - the prod run read 14,839 folded over 110s straight off it. A second instrumentation
    /// path that only restates that is not worth keeping alive. The slow-pass WARN is not a restatement of
    /// anything: it names a SPECIFIC account whose pass ran long, which the progress line cannot.
    ///
    /// WHAT DRIVES IT. Every pass comes from AccountVaultFoldMigration, the engine behind
    /// /vaultclassfold, on its own background thread - so a pass is a migration run's cost and no longer
    /// a world-tick tax. What it still costs is the account's mutation queue, which is what the WARN
    /// line's advice is about: a player opening that vault waits behind the pass in flight.
    ///
    /// The counters behind <see cref="SnapshotForTest"/> are TEST-ONLY observables now. Nothing in
    /// production reads them, and they are reset only by <see cref="ResetForTest"/>, so
    /// <c>maxMs</c> is the worst pass of the whole process rather than of a window.
    ///
    /// Thread model: every field is guarded by <see cref="sync"/>. Enqueue drains on the calling thread
    /// and the tests drive it from several, so the lock is real rather than decorative. It is a leaf lock
    /// - nothing inside it calls back out except the one log write, which is deliberately done OUTSIDE
    /// it.
    /// </summary>
    public static class VaultFoldProfile
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// A single pass at or above this many milliseconds gets its own WARN line.
        ///
        /// Raised from 25.0 after the first measured stage run against a restored copy of prod's shard
        /// (2026-09-24): ordinary worst-pass times sat between 15 and 22 ms with a 95.6 ms cold-start
        /// outlier, so a 25.0 threshold fired on passes that were simply the normal high end and told an
        /// operator nothing. 40.0 leaves the routine band quiet and still catches a pass that is genuinely
        /// out of family.
        /// </summary>
        public const double SlowPassMs = 40.0;

        /// <summary>Minimum spacing between two SLOW PASS lines, so a persistently slow fleet logs once per window rather than once per pass.</summary>
        public const double SlowPassLogIntervalSeconds = 30.0;

        private static readonly object sync = new object();

        // Test-only observables, read through SnapshotForTest. totalMs, maxExamined, maxFolded and
        // maxAccountId were carried purely so the retired roll-up line could print them, and went with it
        // rather than being left to accumulate unread.
        private static long passes;
        private static long idlePasses;
        private static long itemsExamined;
        private static long itemsFolded;
        private static double maxMs;

        private static double nextSlowLineUnixTime;

        /// <summary>
        /// A pass that examined nothing: the store was not ready, or every stored biota had already
        /// been refused once and <c>foldExhausted</c> short-circuited before the candidate scan.
        ///
        /// Counted separately from <see cref="passes"/> on purpose: an idle pass does no work and is the
        /// steady state, so counting the two together would make a genuinely expensive fleet look cheap to
        /// anything reading the ratio.
        /// </summary>
        public static void RecordIdlePass()
        {
            lock (sync)
                idlePasses++;
        }

        /// <summary>
        /// One real pass. <paramref name="elapsedMs"/> covers the candidate scan and every candidate
        /// worked; <paramref name="examined"/> is the candidate count and <paramref name="folded"/> how
        /// many collapsed.
        ///
        /// The WARN write happens after the lock is released, because log4net appenders can block on a
        /// file or a socket and this is a leaf lock taken from whichever thread drained the pass.
        /// </summary>
        public static void RecordPass(uint accountId, double elapsedMs, int examined, int folded, double currentUnixTime)
        {
            string slowLine = null;

            lock (sync)
            {
                passes++;
                itemsExamined += examined;
                itemsFolded += folded;

                if (elapsedMs > maxMs)
                    maxMs = elapsedMs;

                if (elapsedMs >= SlowPassMs && currentUnixTime >= nextSlowLineUnixTime)
                {
                    nextSlowLineUnixTime = currentUnixTime + SlowPassLogIntervalSeconds;

                    slowLine = $"[VAULT] fold SLOW PASS: account {accountId} took {Ms(elapsedMs)} ms for {examined} examined / {folded} folded"
                             + $" ({PerItem(elapsedMs, examined)} ms per item examined). A pass holds that account's mutation queue for its"
                             + $" duration, so a player opening their vault waits behind it. Re-run /vaultclassfold with a smaller --batch,"
                             + $" or turn account_vault_class_storage off to stop the migration."
                             + $" Further slow-pass lines are suppressed for {SlowPassLogIntervalSeconds:N0}s.";
                }
            }

            if (slowLine != null)
                log.Warn(slowLine);
        }

        /// <summary>Test-only reset of every counter and the slow-pass rate limit.</summary>
        internal static void ResetForTest()
        {
            lock (sync)
            {
                passes = 0;
                idlePasses = 0;
                itemsExamined = 0;
                itemsFolded = 0;
                maxMs = 0;
                nextSlowLineUnixTime = 0;
            }
        }

        /// <summary>Test-only read of the counters, so a test can assert on what a pass recorded.</summary>
        internal static (long Passes, long IdlePasses, long Examined, long Folded, double MaxMs) SnapshotForTest()
        {
            lock (sync)
                return (passes, idlePasses, itemsExamined, itemsFolded, maxMs);
        }

        private static string Ms(double value) => value.ToString("0.00", CultureInfo.InvariantCulture);

        private static string PerItem(double ms, long items)
            => items <= 0 ? "n/a" : (ms / items).ToString("0.000", CultureInfo.InvariantCulture);
    }
}
