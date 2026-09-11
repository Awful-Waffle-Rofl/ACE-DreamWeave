using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using ACE.Common.Extensions;
using ACE.Database;
using ACE.Server.Entity.AccountVault;
using ACE.Server.WorldObjects;

namespace ACE.Server.Managers.Market
{
    /// <summary>
    /// The snapshot backfill: the automatic sweep that runs after a world start, and the
    /// /marketbackfill command an operator drives by hand. Both are the same pass.
    ///
    /// WHY IT EXISTS. A snapshot is projected once, when the listing is created, and nothing
    /// refreshes it afterwards; nothing expires a listing either, since every <see cref="Close"/>
    /// caller is a delist, a sale, the pre-withdraw hook or an account invalidation and none of them
    /// is time-based. So a snapshot field added today reads null forever on every listing that was
    /// already open, and the only correction available was for the seller to re-list.
    ///
    /// WHY IT RE-PROJECTS THE LIVE ITEM rather than the weenie: three systems rewrite an item's
    /// ValidLocations away from its template (CoverageLoomStation, Tailoring, Aetheria), so a
    /// weenie-derived shortcut would write a confidently wrong answer for exactly the items a slot
    /// filter is most likely to be used on.
    ///
    /// WHY IT CAN: listing takes NO CUSTODY. The only TryTakeForSale caller in the server is the
    /// purchase path, so a listed item is still a live WorldObject sitting in the seller's vault and
    /// can be read back through MarketSnapshot.FromItem.
    ///
    /// THE ONE INVARIANT: A BACKFILL MAY ONLY EVER IMPROVE A ROW. Every way this pass could produce
    /// a worse snapshot than the one already stored - a vault that is not ready, an item it cannot
    /// resolve, a projection that throws, a projection that degrades to "Item wcid" over a real name
    /// - is counted and skipped, never written. A listing whose backing cannot be read right now
    /// keeps exactly what it captured.
    /// </summary>
    public static partial class MarketManager
    {
        /// <summary>
        /// The most listings one invocation will look at. Sized for the market this runs against
        /// (order 10^3 Active rows), not for a general-purpose migration: each row costs a
        /// MarketSnapshot.FromItem, which takes the item's BiotaDatabaseLock, and the whole pass runs
        /// inline on a world thread.
        /// </summary>
        public const int MaxBackfillRows = 500;

        /// <summary>
        /// The index generation the two cursors below belong to. A cursor is a position INSIDE one
        /// index, so it is meaningless against a different one: Initialize mints a fresh
        /// <see cref="FeedGeneration"/> whenever it rebuilds, and comparing against it resets the
        /// cursors with no plumbing at the call sites - and, just as importantly, stops one unit
        /// test's half-finished sweep leaking into the next.
        /// </summary>
        private static string backfillCursorGeneration;

        /// <summary>
        /// Where the last real pass stopped, as the (SellerAccountId, Id) pair the walk is ordered
        /// by. (0, 0) means "start from the beginning": listing ids are auto-increment from 1 and
        /// MarketManager.List refuses an actor with account 0, so no real listing can sort at or
        /// below it. Guarded by <see cref="indexLock"/>.
        /// </summary>
        private static uint runCursorAccount;
        private static uint runCursorListing;

        /// <summary>
        /// The preview walk's own position. SEPARATE from the run cursor on purpose: a preview must
        /// be able to survey the whole market without consuming the place a real backfill is going
        /// to resume from, and an operator who previews then runs must not have the run skip the
        /// page the preview just showed them.
        /// </summary>
        private static uint previewCursorAccount;
        private static uint previewCursorListing;

        /// <summary>
        /// Listings whose database write FAILED while their in-memory snapshot had already been
        /// updated. They must be resubmitted regardless of what the JSON comparison says, and this
        /// set is the only thing that can tell them apart from a row that is genuinely up to date.
        ///
        /// WHY THE COMPARISON CANNOT: the pass writes memory BEFORE the database, deliberately, so a
        /// concurrent close carries the new snapshot with it. When the write then fails, the next
        /// pass re-projects the same item and gets something byte-identical to the memory copy it
        /// already wrote - so the row reads as Unchanged, is never resubmitted, and stays stale in
        /// the database until the listing closes. Every later pass reports a clean sweep while the
        /// bad row sits there, and a restart reloads the stale snapshot from the database,
        /// reintroducing exactly what this whole feature exists to fix.
        ///
        /// BOUNDED BY THE INDEX, two ways: every pass prunes ids that are no longer Active listings,
        /// and a rebuilt index clears the set entirely (see
        /// <see cref="SyncBackfillCursorGenerationLocked"/>). So it can never hold more than the
        /// Active listing count, and never holds an id for a listing that has gone away.
        ///
        /// Guarded by <see cref="indexLock"/>.
        /// </summary>
        private static readonly HashSet<uint> backfillWriteFailures = new HashSet<uint>();

        /// <summary>
        /// Sends the next pass of both walks back to the start of the market. The cursors already
        /// reset themselves when a sweep reaches the end, so this is the escape hatch for the other
        /// case: a sweep abandoned half way, or one an operator wants to redo after fixing whatever
        /// was making rows skip.
        ///
        /// DELIBERATELY DOES NOT CLEAR <see cref="backfillWriteFailures"/>. Rewinding the walk is
        /// how an operator retries after a shard problem, so dropping the retry set here would throw
        /// away the one record of which rows still need rewriting - precisely at the moment somebody
        /// is trying to rewrite them.
        /// </summary>
        // ---- the automatic pass ----

        /// <summary>The live kill switch. Default TRUE; see PropertyManager for the full description.</summary>
        public const string AutoBackfillTunable = "market_snapshot_backfill_on_start";

        /// <summary>
        /// How the automatic sweep reads its kill switch. Null means PropertyManager, which is what
        /// production uses.
        ///
        /// THE SEAM EXISTS FOR ONE CASE: a test that makes the read THROW. The unit-test harness
        /// cannot produce that through PropertyManager, because MarketManagerTests.SeedMarketTunables
        /// puts this key in CachedBooleanSettings and GetBool answers from that cache without ever
        /// reaching the shard read that would throw (PropertyManager.cs:101-102). Without the seam
        /// the fail-safe branch in <see cref="AutoBackfillEnabled"/> is unreachable from the suite,
        /// and a later edit could delete it or widen it to swallow an exception that should
        /// propagate, with nothing going red.
        /// </summary>
        internal static Func<bool> AutoBackfillTunableSource { get; set; }

        /// <summary>
        /// Listings per page inside the automatic sweep. Smaller than the operator's cap on purpose:
        /// this runs unattended, so a page is the unit of work between two chances to notice that
        /// the world needs the thread back.
        /// </summary>
        internal const int AutoBackfillPageSize = 100;

        /// <summary>
        /// Hard bounds on one automatic sweep. Both exist so a pathological index - rows that keep
        /// reporting as remaining, a retry set that keeps consuming budget - cannot spin: whichever
        /// is hit first ends the sweep, and the next world start begins another one.
        /// </summary>
        internal const int AutoBackfillMaxPasses = 200;

        internal const int AutoBackfillMaxRows = 20000;

        /// <summary>
        /// How long the sweep waits after world start before looking at anything, and how long it
        /// waits between readiness checks.
        ///
        /// NOT AT BOOT, and the delay is not superstition: MarketManager.Initialize runs at the end
        /// of Program.cs's startup, but AccountVaultManager.GetStore CREATES a store on first use and
        /// its containers load their inventories ASYNCHRONOUSLY. A sweep that ran inline at boot
        /// would find every store not ready, count store_not_ready for every listing, advance its
        /// cursor past all of them and report a clean sweep having repaired nothing.
        /// </summary>
        private static readonly TimeSpan AutoBackfillStartDelay = TimeSpan.FromSeconds(45);

        private static readonly TimeSpan AutoBackfillReadinessDelay = TimeSpan.FromSeconds(15);

        /// <summary>
        /// How many readiness checks the sweep makes before running anyway. BOUNDED on purpose: an
        /// account whose store never loads must not keep a background thread alive indefinitely, and
        /// running anyway is harmless - those listings are counted store_not_ready, nothing is
        /// written for them, and they are still out of date at the next world start, which tries
        /// again. Roughly 20 * 15s = 5 minutes.
        /// </summary>
        private const int AutoBackfillReadinessAttempts = 20;

        private static Thread autoBackfillThread;

        private static volatile bool autoBackfillStopping;

        /// <summary>
        /// Starts the automatic sweep on its own background thread.
        ///
        /// CALLED FROM THE PRODUCTION Initialize() ONLY, exactly like MarketRejectionLog.Start and
        /// for the same reason: the three-seam Initialize every unit test drives must not spawn a
        /// thread that races the test's own assertions, and must not read PropertyManager in a
        /// process with no shard.
        ///
        /// A THREAD RATHER THAN THE WORLD TICK. A page issues a synchronous EF write through the
        /// repository, and putting that on a world thread would stall the simulation for the length
        /// of a shard round trip. The pass is already built for concurrent callers - it takes the
        /// index lock to select, projects outside it, and re-checks each listing under the lock
        /// before writing - which is the same contract the API host's threads already rely on.
        /// </summary>
        /// <param name="startDelay">
        /// Overrides <see cref="AutoBackfillStartDelay"/>. For tests only: production passes nothing,
        /// because the delay is the whole point of not running at boot.
        /// </param>
        public static void StartAutomaticBackfill(TimeSpan? startDelay = null)
        {
            autoBackfillStopping = false;

            var delay = startDelay ?? AutoBackfillStartDelay;

            var thread = new Thread(() => AutomaticBackfillLoop(delay))
            {
                IsBackground = true,
                Name = "MarketSnapshotBackfill",
            };

            autoBackfillThread = thread;
            thread.Start();
        }

        /// <summary>How long <see cref="StopAutomaticBackfill"/> waits for a page in flight.</summary>
        private static readonly TimeSpan AutoBackfillStopTimeout = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Stops the sweep and WAITS for it, bounded. Returns false if it did not stop in time.
        ///
        /// THE JOIN IS NOT OPTIONAL, and an earlier version of this comment was wrong to argue that
        /// it was. The argument was that a sweep mid-page would see the emptied index and stop by
        /// itself - true only for ids re-checked AFTER the clear. An id whose copy was already taken
        /// under the index lock runs on to projection and into the batch, and the repository call
        /// that writes that batch takes no lock at all. Nor does stashing the repository help:
        /// ShardMarketRepository.Db is an expression-bodied property over
        /// DatabaseManager.Shard.BaseDatabase, so it resolves LIVE at call time rather than
        /// capturing. Program.cs calls Shutdown and then DatabaseManager.Stop moments later, so
        /// without this join a restart landing mid-page races the shard worker's teardown.
        ///
        /// The two sibling background writers on that same shutdown path both join with a bounded
        /// timeout for exactly this reason: MarketRejectionLog.Stop and AccountVaultBarrelReaper.Stop.
        /// </summary>
        public static bool StopAutomaticBackfill()
        {
            autoBackfillStopping = true;

            var thread = autoBackfillThread;

            autoBackfillThread = null;

            if (thread == null)
                return true;

            try
            {
                if (thread.Join(AutoBackfillStopTimeout))
                    return true;

                log.Warn($"[MARKET] the automatic snapshot backfill did not stop within {AutoBackfillStopTimeout.TotalSeconds:N0}s. A page may still be writing; if the shard is being torn down, expect one repository error from it.");

                return false;
            }
            catch (Exception ex)
            {
                log.Warn($"[MARKET] could not wait for the automatic snapshot backfill to stop: {ex.GetFullMessage()}");

                return false;
            }
        }

        private static void AutomaticBackfillLoop(TimeSpan startDelay)
        {
            try
            {
                if (!SleepUnlessStopping(startDelay))
                    return;

                WaitForBackfillStores();

                if (autoBackfillStopping)
                    return;

                var outcome = RunAutomaticBackfill();

                if (!outcome.Ran)
                {
                    log.Info($"[MARKET] the automatic snapshot backfill did not run: {outcome.SkipReason}. /marketbackfill run remains available.");
                    return;
                }

                // Loud on purpose, and it says AUTOMATIC: an operator reading a log full of snapshot
                // rewrites has to be able to tell this from somebody running the command.
                log.Info($"[MARKET] AUTOMATIC snapshot backfill finished: {outcome}");
            }
            catch (Exception ex)
            {
                // The outer net. RunAutomaticBackfill already swallows its own errors; this catches
                // anything around it, because an unhandled exception on a background thread would
                // take the process down.
                log.Error($"[MARKET] the automatic snapshot backfill thread stopped on an error; no listing was left in an inconsistent state and /marketbackfill run is still available: {ex.GetFullMessage()}");
            }
        }

        /// <summary>
        /// Waits, bounded, until every account that owns an out-of-date listing can answer for its
        /// vault. Returns either way - see <see cref="AutoBackfillReadinessAttempts"/> for why
        /// running anyway is the right end to a wait that did not finish.
        /// </summary>
        private static void WaitForBackfillStores()
        {
            for (var attempt = 0; attempt < AutoBackfillReadinessAttempts; attempt++)
            {
                if (autoBackfillStopping)
                    return;

                IMarketItemStore store;
                List<uint> accounts;

                lock (indexLock)
                {
                    store = itemStore;

                    accounts = listings.Values
                        .Where(l => l.Status == MarketListingStatus.Active && IsStaleSnapshot(l))
                        .Select(l => l.SellerAccountId)
                        .Distinct()
                        .ToList();
                }

                if (store == null || accounts.Count == 0)
                    return;

                // Outside the lock: IsReady reaches into AccountVaultManager, which takes locks of
                // its own, and the first call for an account also CREATES its store and starts the
                // load this is waiting on.
                var waiting = accounts.Count(a => !store.IsReady(a, out _));

                if (waiting == 0)
                    return;

                log.Debug($"[MARKET] the automatic snapshot backfill is waiting on {waiting} of {accounts.Count} seller vault(s) (attempt {attempt + 1}/{AutoBackfillReadinessAttempts}).");

                if (!SleepUnlessStopping(AutoBackfillReadinessDelay))
                    return;
            }

            log.Warn($"[MARKET] the automatic snapshot backfill stopped waiting for seller vaults after {AutoBackfillReadinessAttempts} attempt(s) and is running anyway; listings whose vault is still not ready are counted store_not_ready, left untouched, and picked up at the next world start.");
        }

        /// <summary>Sleeps in short slices so a shutdown is noticed promptly. False means stop.</summary>
        private static bool SleepUnlessStopping(TimeSpan total)
        {
            var slice = TimeSpan.FromMilliseconds(250);

            for (var waited = TimeSpan.Zero; waited < total; waited += slice)
            {
                if (autoBackfillStopping)
                    return false;

                Thread.Sleep(slice);
            }

            return !autoBackfillStopping;
        }

        /// <summary>
        /// Active listings whose stored snapshot predates the current projection. The number the
        /// automatic sweep is trying to drive to zero, and what it reports as still outstanding.
        ///
        /// Read from the IN-MEMORY index, which is the same source selection uses. A row whose write
        /// failed therefore does NOT appear here: its memory copy was already updated. That row is
        /// held by the retry set instead, and reported as write_failed - and it comes back as stale
        /// on the next world start, because the index is rebuilt from the database.
        /// </summary>
        public static int CountStaleActiveListings()
        {
            lock (indexLock)
                return listings.Values.Count(l => l.Status == MarketListingStatus.Active && IsStaleSnapshot(l));
        }

        /// <summary>
        /// One complete automatic sweep: pages until nothing is left ahead of the cursor, then stops.
        ///
        /// SEPARATE FROM THE THREAD THAT STARTS IT (<see cref="StartAutomaticBackfill"/>) so this
        /// part is synchronous and deterministic, and a test never has to wait for a vault to load.
        ///
        /// It re-uses <see cref="BackfillSnapshots"/> WHOLESALE rather than reimplementing a bulk
        /// variant - the same cursor, the same degraded guard, the same memory-before-database
        /// ordering, the same retry set, the same status-guarded write. There is exactly one pass in
        /// this file, and an operator running /marketbackfill run by hand gets the same code.
        ///
        /// NEVER THROWS. It runs on a background thread during world start, and a cosmetic snapshot
        /// repair must not be able to take that down.
        /// </summary>
        internal static MarketAutoBackfillOutcome RunAutomaticBackfill(int pageSize = AutoBackfillPageSize)
        {
            var outcome = new MarketAutoBackfillOutcome();

            // The RAW index flag, exactly as the pass itself uses: `market_enabled` being off is a
            // trading kill switch, not a reason to leave stored snapshots wrong. What this needs is
            // an index that was actually built, because Initialize deliberately leaves it empty when
            // the listing read failed and a sweep there would be a sweep over a market it cannot see.
            if (!enabled)
            {
                outcome.SkipReason = "the market index has not been built in this process";
                return outcome;
            }

            if (!AutoBackfillEnabled(out var disabledReason))
            {
                outcome.SkipReason = disabledReason;
                return outcome;
            }

            outcome.Ran = true;

            if (pageSize < 1)
                pageSize = 1;

            try
            {
                while (true)
                {
                    // Bounds FIRST, so neither can be overshot by a page. Neither is expected to
                    // bind: the walk cannot stall, because a page always takes at least one forward
                    // row whenever one is ahead of the cursor (the retry budget is half a page,
                    // rounded down, so it can never consume the whole page). They exist because this
                    // loop runs unattended, and "cannot stall" is an argument, not a guarantee.
                    if (outcome.Passes >= AutoBackfillMaxPasses || outcome.Considered >= AutoBackfillMaxRows)
                    {
                        outcome.StoppedAtBound = true;
                        break;
                    }

                    var report = BackfillSnapshots(pageSize, false);

                    // The index went away underneath the sweep - a Shutdown, or a rebuild that left
                    // it disabled. Stop; there is nothing to resume against.
                    //
                    // THIS is how a shutdown ends the sweep, deliberately, rather than a check on
                    // autoBackfillStopping. That flag belongs to the thread, and only
                    // StartAutomaticBackfill clears it - so consulting it here would make this
                    // method refuse to run for the rest of the process after any Shutdown, which is
                    // every unit test after the first.
                    if (report.NotInitialized)
                    {
                        outcome.StoppedAtBound = true;
                        break;
                    }

                    outcome.Absorb(report);

                    if (report.RemainingActive == 0)
                    {
                        outcome.Completed = true;
                        break;
                    }

                    // Belt and braces against the one shape that would spin: a page that SELECTED
                    // nothing from ahead of the cursor while still reporting rows ahead of it. That
                    // should be unreachable, so if it happens the honest thing is to stop and say so
                    // rather than loop.
                    //
                    // ForwardSelected, never Considered. Considered counts rows still Active when
                    // their turn came, so a page whose listings closed under it can advance the
                    // cursor perfectly and still report zero - and a check written against Considered
                    // would end the sweep with an ERROR over ordinary concurrent closes, on exactly
                    // the busy market this is designed to run on. What "no progress" means is that
                    // the cursor did not move.
                    if (report.ForwardSelected == 0)
                    {
                        log.Error($"[MARKET] the automatic snapshot backfill selected no listing from ahead of its cursor while reporting {report.RemainingActive} still ahead of it; stopping this sweep.");
                        outcome.StoppedAtBound = true;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                outcome.Faulted = true;
                outcome.Completed = false;

                log.Error($"[MARKET] the automatic snapshot backfill stopped on an error after {outcome.Passes} page(s); the listings it did rewrite are stored, and the next world start resumes: {ex.GetFullMessage()}");
            }

            // Measured AFTER the sweep, so it reads as what is LEFT rather than as what the last
            // page found when it started.
            outcome.StaleActive = CountStaleActiveListings();

            return outcome;
        }

        /// <summary>
        /// The kill switch, read once per sweep on the backfill's own thread.
        ///
        /// A FAILED READ MEANS DO NOT RUN. An uncached PropertyManager read falls through to
        /// DatabaseManager.ShardConfig and opens a ShardDbContext, so this can throw - and when it
        /// does, the honest position is that we could not confirm the operator has left the switch
        /// on. The cost of declining is one skipped repair on one boot, which the next world start
        /// picks up; the cost of assuming is an unattended write an operator thought they had turned
        /// off. The manual command is unaffected either way.
        /// </summary>
        private static bool AutoBackfillEnabled(out string reason)
        {
            try
            {
                var source = AutoBackfillTunableSource;

                if (source != null ? source() : PropertyManager.GetBool(AutoBackfillTunable, true).Item)
                {
                    reason = null;
                    return true;
                }

                reason = $"{AutoBackfillTunable} is off";
                return false;
            }
            catch (Exception ex)
            {
                reason = $"{AutoBackfillTunable} could not be read";

                log.Warn($"[MARKET] could not read {AutoBackfillTunable}; the automatic snapshot backfill is SKIPPED this start rather than run against an unknown setting: {ex.GetFullMessage()}");

                return false;
            }
        }

        public static void ResetBackfillCursor()
        {
            lock (indexLock)
            {
                backfillCursorGeneration = FeedGeneration;
                runCursorAccount = runCursorListing = 0;
                previewCursorAccount = previewCursorListing = 0;
            }
        }

        /// <summary>
        /// Caller must hold <see cref="indexLock"/>. Drops a cursor - and a retry set - that belongs
        /// to a previous index.
        ///
        /// The retry set is safe to drop on a rebuild because the rebuild HEALS what it recorded: a
        /// stale database row is reloaded into memory as that listing's snapshot, so the next
        /// projection differs from it again and the ordinary comparison resubmits the row on its own.
        /// </summary>
        private static void SyncBackfillCursorGenerationLocked()
        {
            if (string.Equals(backfillCursorGeneration, FeedGeneration, StringComparison.Ordinal))
                return;

            backfillCursorGeneration = FeedGeneration;
            runCursorAccount = runCursorListing = 0;
            previewCursorAccount = previewCursorListing = 0;
            backfillWriteFailures.Clear();
        }

        /// <summary>
        /// Whether a listing lies after the cursor in the walk's own (account, id) ordering.
        ///
        /// One predicate, used to select the forward page and - negated - to select the retries that
        /// sit behind it, so the two halves of a page are complementary by construction and no
        /// listing can be walked twice in one pass.
        /// </summary>
        private static bool IsAfterBackfillCursor(MarketListing listing, uint cursorAccount, uint cursorListing)
        {
            return listing.SellerAccountId > cursorAccount
                   || (listing.SellerAccountId == cursorAccount && listing.Id > cursorListing);
        }

        /// <summary>
        /// Whether a listing's stored snapshot was built by an older projection than the one running
        /// now. Null and 0 both mean "predates versioning", which is the same answer.
        /// </summary>
        private static bool IsStaleSnapshot(MarketListing listing)
            => (listing.Snapshot?.SnapshotVersion ?? 0) < MarketSnapshot.CurrentVersion;

        /// <summary>
        /// The selection predicate, in one place so the forward page and the behind-the-cursor
        /// retries cannot disagree about what qualifies.
        ///
        /// A RETRY QUALIFIES REGARDLESS OF VERSION, and that is not a convenience. The pass whose
        /// write failed had already updated the in-memory snapshot to the current version, and
        /// selection reads memory - so a version gate applied to the retry set would silently skip
        /// exactly the rows whose database copy is known to be behind.
        /// </summary>
        private static bool NeedsBackfill(MarketListing listing, HashSet<uint> retryAfterWriteFailure, bool allVersions)
            => allVersions || retryAfterWriteFailure.Contains(listing.Id) || IsStaleSnapshot(listing);

        /// <summary>
        /// Re-projects the stored snapshot of up to <paramref name="max"/> Active listings.
        ///
        /// <paramref name="dryRun"/> true does everything except the two writes: the in-memory
        /// snapshot is left alone and no database row is touched, so Rewritten reads as "would be
        /// rewritten" and every skip counter means exactly what it means in a real pass.
        ///
        /// ORDERED BY SELLER ACCOUNT then listing id, so each account's vault is read ONCE and the
        /// listings that share it are answered from that one read.
        ///
        /// IT RESUMES, IT DOES NOT RESTART. Each pass begins after the last one's final listing and
        /// advances a cursor, so successive invocations of the SAME command make guaranteed forward
        /// progress and a market larger than <see cref="MaxBackfillRows"/> is reachable in full.
        /// Without that, "walk the first n by a stable ordering" would re-walk the identical rows
        /// every time and the tail of the market would be permanently unreachable - and raising the
        /// cap is not the fix, it only moves the wall. When a pass reaches the end the cursor resets,
        /// so the next one starts a fresh sweep.
        ///
        /// SELECTION IS VERSION GATED by default: only listings whose stored snapshot was built by a
        /// projection older than <see cref="MarketSnapshot.CurrentVersion"/> are walked, which is
        /// what makes a pass over an up-to-date market genuinely free and lets the automatic sweep
        /// run on every world start without re-projecting everything. <paramref name="allVersions"/>
        /// true removes that gate and re-projects regardless - the escape hatch for a projection
        /// BUGFIX that nobody bumped the constant for, which version gating cannot see.
        ///
        /// A listing in the write-failure retry set is walked either way. Its in-memory snapshot was
        /// already updated to the current version by the pass whose write then failed, so a version
        /// gate applied to it would skip the one row that most needs resubmitting.
        /// </summary>
        public static MarketBackfillReport BackfillSnapshots(int max, bool dryRun, bool allVersions = false)
        {
            var report = new MarketBackfillReport();

            // The RAW field, not Enabled, exactly as OnVaultWithdraw reads it: the market_enabled
            // kill switch being off is one of the states an operator is most likely to be fixing
            // things in, and a repair tool that goes blind precisely then is the wrong shape. What
            // this does require is an INDEX - a manager that never booted has no listings to fix.
            if (!enabled)
            {
                report.NotInitialized = true;
                return report;
            }

            if (max < 1)
                max = 1;
            else if (max > MaxBackfillRows)
                max = MaxBackfillRows;

            IMarketItemStore store;
            IMarketRepository repo;
            List<uint> ids;
            int aheadOfCursor;
            int forwardOnPage;

            // The generation this page is taken FROM, captured into a local rather than re-read at
            // the end. backfillCursorGeneration is shared, so any other caller that syncs it - a
            // second concurrent pass, or an operator's /marketbackfill reset - would make an
            // end-of-pass check that compares that field against FeedGeneration compare it against
            // itself and agree, letting this pass commit a position taken from an index that no
            // longer exists.
            string generationAtStart;

            // Listings whose last write failed, so they must be resubmitted even when the projection
            // comes back identical. Copied out under the lock and then read without one.
            HashSet<uint> retryAfterWriteFailure;

            // Where this page ended, which becomes the next pass's starting point.
            uint pageLastAccount = 0;
            uint pageLastListing = 0;

            lock (indexLock)
            {
                store = itemStore;
                repo = repository;

                SyncBackfillCursorGenerationLocked();

                generationAtStart = backfillCursorGeneration;

                // Prune, then copy. This is what bounds the retry set: an id survives only while its
                // listing is still an Active row in the index, so a closed or vanished listing drops
                // out on the next pass instead of accumulating forever.
                backfillWriteFailures.RemoveWhere(
                    failed => !listings.TryGetValue(failed, out var row) || row.Status != MarketListingStatus.Active);

                retryAfterWriteFailure = new HashSet<uint>(backfillWriteFailures);

                var cursorAccount = dryRun ? previewCursorAccount : runCursorAccount;
                var cursorListing = dryRun ? previewCursorListing : runCursorListing;

                report.ResumedFromAccountId = cursorAccount;
                report.ResumedFromListingId = cursorListing;

                // Ids only, and the lock is released before anything is projected:
                // MarketSnapshot.FromItem takes the item's own BiotaDatabaseLock, and holding the
                // index lock across that would put a vault lock underneath the lock every market
                // read and every close needs. Same collect-then-release shape as InvalidateForAccount.
                //
                // The cursor comparison is the SAME (account, id) ordering the walk uses, so "after
                // the cursor" and "later in the walk" cannot disagree.
                var ahead = listings.Values
                    .Where(l => l.Status == MarketListingStatus.Active)
                    .Where(l => NeedsBackfill(l, retryAfterWriteFailure, allVersions))
                    .Where(l => IsAfterBackfillCursor(l, cursorAccount, cursorListing))
                    .OrderBy(l => l.SellerAccountId)
                    .ThenBy(l => l.Id)
                    .ToList();

                aheadOfCursor = ahead.Count;

                // Market-wide and cursor-independent, so it answers "how much is out of date"
                // rather than "how much is left in this sweep". On a full sweep those two diverge
                // completely, which is the case it exists for.
                report.StaleActive = listings.Values
                    .Count(l => l.Status == MarketListingStatus.Active && IsStaleSnapshot(l));

                // A remembered write failure is retried on the very next run even when the walk has
                // already passed it, because otherwise the operator is told the row will be
                // resubmitted while the cursor guarantees it will not be - not until the sweep wraps.
                //
                // The budget is half a page, integer division, so a growing retry set can never take
                // the whole page away from forward progress. max = 1 therefore yields a budget of 0:
                // at a page size of one, moving forward wins, and the retry is picked up on the wrap.
                var retryBudget = max / 2;

                var behind = retryBudget < 1
                    ? new List<MarketListing>()
                    : listings.Values
                        .Where(l => l.Status == MarketListingStatus.Active)
                        // No version filter, and none is needed: retry membership already satisfies
                        // NeedsBackfill on its own.
                        .Where(l => retryAfterWriteFailure.Contains(l.Id))
                        .Where(l => !IsAfterBackfillCursor(l, cursorAccount, cursorListing))
                        .OrderBy(l => l.SellerAccountId)
                        .ThenBy(l => l.Id)
                        .Take(retryBudget)
                        .ToList();

                // Complementary predicates - after the cursor, or not after it - so no listing can
                // appear in both halves and be counted twice in Considered.
                var forward = ahead.Take(max - behind.Count).ToList();

                forwardOnPage = forward.Count;
                report.ForwardSelected = forward.Count;

                // Re-sorted as one page so the walk still meets each account's listings together and
                // reads that account's vault exactly once.
                ids = behind.Concat(forward)
                    .OrderBy(l => l.SellerAccountId)
                    .ThenBy(l => l.Id)
                    .Select(l => l.Id)
                    .ToList();

                // Taken from the FORWARD rows alone. A behind-the-cursor retry must never drag the
                // cursor backwards over listings this pass did not look at.
                if (forward.Count > 0)
                {
                    pageLastAccount = forward[forward.Count - 1].SellerAccountId;
                    pageLastListing = forward[forward.Count - 1].Id;
                }
            }

            if (store == null || repo == null)
            {
                report.NotInitialized = true;
                return report;
            }

            // Forward rows only: a retry pulled in from behind the cursor consumes page budget but
            // does not reduce what still lies ahead of it.
            report.RemainingActive = aheadOfCursor - forwardOnPage;
            report.SweepComplete = report.RemainingActive == 0;

            var pending = new List<MarketListingSnapshotUpdate>();

            // Ids submitted BECAUSE a previous write failed. Kept apart from `pending` so the repair
            // count can be settled against the write's own outcome.
            var retrySubmitted = new List<uint>();

            // ONE ACCOUNT AT A TIME, and its entries read once. Nothing here opens a serialized
            // region: this is a read-only re-projection, the same shape TryGetVaultView already
            // uses (GetEntries then FromItem, no RunSerialized), and AccountVaultStore.GetEntries
            // takes its own stateLock.
            var accountLoaded = false;
            uint account = 0;
            var accountReady = false;
            IReadOnlyList<VaultEntry> entries = null;

            foreach (var id in ids)
            {
                MarketListing listing;

                lock (indexLock)
                    listing = listings.TryGetValue(id, out var live) && live.Status == MarketListingStatus.Active
                        ? Copy(live)
                        : null;

                // Closed between the id sweep above and now. Not an error and not a failure to
                // improve the row: there is no longer a row this pass is allowed to touch.
                if (listing == null)
                {
                    report.ClosedMidPass++;
                    continue;
                }

                report.Considered++;

                if (!accountLoaded || account != listing.SellerAccountId)
                {
                    account = listing.SellerAccountId;
                    accountLoaded = true;

                    // "Not ready" and "holds nothing" are different facts, and an unready store
                    // answers GetEntries with an EMPTY list. Acting on that as "the item is gone"
                    // would blank a whole account's snapshots over a transient shard hiccup, so a
                    // not-ready account skips every listing it owns and is counted as such.
                    accountReady = store.IsReady(account, out _);
                    entries = accountReady ? store.GetEntries(account, 0, -1) : null;
                }

                if (!accountReady)
                {
                    report.StoreNotReady++;
                    continue;
                }

                if (!TryResolveBackfillEntry(entries, listing, out var item))
                {
                    // Also the correct outcome for a purchase in flight, which removes the item from
                    // the seller's vault before ApplySale closes the listing. NEVER invalidate here
                    // and never fabricate a snapshot: this pass has no authority over a listing's
                    // status, and InvalidateForAccount already owns that question.
                    report.ItemUnresolved++;
                    continue;
                }

                ListingSnapshot fresh;

                try
                {
                    fresh = ProjectBackfillSnapshot(store, listing, item);
                }
                catch (Exception ex)
                {
                    log.Error($"[MARKET] backfill could not re-project listing {id} (account {account}, wcid {listing.Wcid}): {ex.GetFullMessage()}. It keeps the snapshot it captured.");
                    report.ProjectionFailed++;
                    continue;
                }

                if (fresh == null)
                {
                    report.ProjectionFailed++;
                    continue;
                }

                if (IsDegradedBackfill(fresh, listing.Snapshot, listing.Wcid))
                {
                    log.Warn($"[MARKET] backfill refused to write listing {id}: the fresh projection is worse than the stored one (name '{fresh.Name}'). It keeps the snapshot it captured.");
                    report.ProjectionDegraded++;
                    continue;
                }

                // Both sides through Serialize, so the comparison is between two normalized JSON
                // documents rather than between a stored string and a freshly formatted one.
                var freshJson = MarketSnapshot.Serialize(fresh);
                var identical = string.Equals(freshJson, MarketSnapshot.Serialize(listing.Snapshot), StringComparison.Ordinal);

                // A row whose last write FAILED is resubmitted even when the projection is identical
                // - because it is identical to the MEMORY copy this pass already wrote, not to the
                // database row, which is still stale. See backfillWriteFailures.
                var mustRetry = retryAfterWriteFailure.Contains(id);

                if (identical && !mustRetry)
                {
                    report.Unchanged++;
                    continue;
                }

                if (dryRun)
                {
                    report.Rewritten++;

                    if (mustRetry)
                        report.RetriedAfterWriteFailure++;

                    report.AddSample(id);
                    continue;
                }

                // MEMORY FIRST, THEN THE DATABASE, and the order is load bearing. Close serializes
                // the IN-MEMORY snapshot through ToRow, so a close landing after this write carries
                // the new JSON with it; writing the database first would let that same close write
                // the OLD snapshot back over ours.
                var stillActive = false;

                lock (indexLock)
                {
                    if (listings.TryGetValue(id, out var live) && live.Status == MarketListingStatus.Active)
                    {
                        stillActive = true;

                        // Skipped for a retry whose memory copy is ALREADY what we are about to
                        // store: that row needs the database write, not a second identical snapshot
                        // object and a sequence bump that would push a no-op row into every feed.
                        if (!identical)
                        {
                            live.Snapshot = fresh;
                            live.Seq = ++changeSequence;
                        }
                    }
                }

                if (!stillActive)
                {
                    report.ClosedMidPass++;
                    continue;
                }

                // Noted, NOT counted. RetriedAfterWriteFailure reports repairs, and whether this is
                // one is not known until the write comes back.
                if (mustRetry)
                    retrySubmitted.Add(id);

                pending.Add(new MarketListingSnapshotUpdate { ListingId = id, SnapshotJson = freshJson });
                report.AddSample(id);
            }

            if (pending.Count > 0)
            {
                var write = repo.UpdateListingSnapshots(pending);

                // Which of the submitted rows still need rewriting. A batch that never ran leaves
                // EVERY row it carried stale on disk and correct in memory, so all of them go in.
                var failedIds = write == null || write.BatchFailed
                    ? pending.Select(row => row.ListingId).ToList()
                    : write.FailedListingIds ?? new List<uint>();

                // Set to true when the repository hands back fewer outcomes than rows. Nothing then
                // identifies WHICH rows went unaccounted, so every row on the batch is treated as
                // unrepaired: an over-approximation, and a self-correcting one, because a row that
                // was in fact written re-projects identical, is resubmitted once, updates, and drops
                // back out. Under-approximating instead would lose the row permanently.
                var unaccounted = false;

                if (write == null || write.BatchFailed)
                {
                    // The batch never ran. The in-memory rows are ahead of the database until the
                    // next successful pass or the next close, which is the safe direction: the feed
                    // is served from memory and a close will persist what memory holds.
                    log.Error($"[MARKET] backfill could not persist {pending.Count} rewritten snapshot(s). They are correct in memory and stale in the database; run /marketbackfill run again once the shard is reachable.");
                    report.WriteFailed = pending.Count;
                }
                else
                {
                    report.Rewritten = write.Updated;

                    // The DAO reports these two SEPARATELY and they must stay separate. A row whose
                    // statement matched nothing closed between its projection and the write, which
                    // is benign; a row whose statement THREW is probably still Active, and its
                    // in-memory snapshot is now permanently ahead of the stored one. Deriving the
                    // second from `pending.Count - updated` - as a bare row count forces - would
                    // file a failing write under closed_mid_pass, where an operator re-running the
                    // pass forever would read it as ordinary churn and never learn anything is wrong.
                    report.ClosedMidPass += write.Missed;
                    report.WriteFailed = write.Failed;

                    // A repository that does not account for every row it was handed must not have
                    // the difference absorbed into the benign counter by default. Attribute it the
                    // loud way.
                    var accounted = write.Updated + write.Missed + write.Failed;

                    if (accounted < pending.Count)
                    {
                        log.Error($"[MARKET] backfill submitted {pending.Count} snapshot row(s) but the repository accounted for only {accounted}. The difference is counted as a write failure, not as a concurrent close, and every row on the batch is remembered for retry.");
                        report.WriteFailed += pending.Count - accounted;
                        unaccounted = true;
                    }
                }

                var failed = new HashSet<uint>(failedIds);

                // Settled against the OUTCOME, not against the submission. A retry that failed again,
                // or that landed in a batch nobody accounted for, is a resubmission - reporting it as
                // a repair would render "rewritten 0, write_failed 1, retried 1" on a pass that
                // repaired nothing.
                report.RetriedAfterWriteFailure =
                    unaccounted ? 0 : retrySubmitted.Count(id => !failed.Contains(id));

                // Record what still needs rewriting, and forget what no longer does. Every submitted
                // row is decided here: it either failed (retry it until a write lands) or it updated
                // or missed, both of which mean stop retrying - a miss because the listing closed and
                // is no longer ours to fix.
                lock (indexLock)
                {
                    foreach (var row in pending)
                    {
                        if (unaccounted || failed.Contains(row.ListingId))
                            backfillWriteFailures.Add(row.ListingId);
                        else
                            backfillWriteFailures.Remove(row.ListingId);
                    }
                }
            }

            // Advance the walk LAST, so a pass that threw part way through does not skip what it
            // never looked at. A sweep that reached the end rewinds to the start of the market.
            lock (indexLock)
            {
                // Against the generation CAPTURED AT THE START, never against the shared field: a
                // rebuild mid-pass makes this page's position meaningless, and comparing the shared
                // field to FeedGeneration would compare it to itself the moment any other caller has
                // already re-synced it.
                if (string.Equals(generationAtStart, FeedGeneration, StringComparison.Ordinal))
                {
                    // Three cases, and the third is why this is not one ternary. The sweep finished,
                    // so rewind; or the page carried forward rows, so move to the last of THOSE; or
                    // the page was retries only, in which case the cursor is left exactly where it
                    // was. Writing pageLast* unconditionally would put a zeroed pair back into the
                    // cursor on a retry-only page and re-walk the whole market.
                    if (report.SweepComplete)
                    {
                        if (dryRun)
                        {
                            previewCursorAccount = 0;
                            previewCursorListing = 0;
                        }
                        else
                        {
                            runCursorAccount = 0;
                            runCursorListing = 0;
                        }
                    }
                    else if (forwardOnPage > 0)
                    {
                        if (dryRun)
                        {
                            previewCursorAccount = pageLastAccount;
                            previewCursorListing = pageLastListing;
                        }
                        else
                        {
                            runCursorAccount = pageLastAccount;
                            runCursorListing = pageLastListing;
                        }
                    }
                }
            }

            log.Info($"[MARKET] snapshot backfill{(dryRun ? " (preview)" : string.Empty)}: {report}");

            return report;
        }

        /// <summary>
        /// Finds the vault row a listing is drawn on, and the exact object to re-project from it.
        ///
        /// THE MEMBER SCAN IS NOT OPTIONAL. A group row is several equivalent whole biotas on one
        /// panel line and exposes only its REPRESENTATIVE as <see cref="VaultEntry.Guid"/>, while a
        /// listing is pinned to whichever member was the representative when it was created. A
        /// deposit of an equivalent item inserts at the front of the group and moves that
        /// representative, so matching on Guid alone would silently skip every listing whose anchor
        /// has since been displaced - and skip it as "unresolved", which looks exactly like the
        /// benign purchase-in-flight case.
        ///
        /// <paramref name="item"/> is the member the listing actually names, not the row's
        /// representative: they are equivalent under the grouping rule, but the listing names one
        /// guid and that is the object whose properties belong in its snapshot. It stays NULL for a
        /// ledger listing, which has no biota behind it at all - true with a null item means "the
        /// ledger row is there", which is everything a ledger projection needs.
        /// </summary>
        private static bool TryResolveBackfillEntry(IReadOnlyList<VaultEntry> entries, MarketListing listing,
                                                    out WorldObject item)
        {
            item = null;

            if (entries == null)
                return false;

            foreach (var candidate in entries)
            {
                if (listing.ItemGuid == null)
                {
                    if (candidate.IsLedger && candidate.Wcid == listing.Wcid)
                        return true;

                    continue;
                }

                if (candidate.IsLedger)
                    continue;

                if (candidate.Guid.Full == listing.ItemGuid.Value)
                {
                    item = candidate.WorldObject;
                    return item != null;
                }

                if (candidate.Members == null)
                    continue;

                foreach (var member in candidate.Members)
                {
                    if (member == null || member.Guid.Full != listing.ItemGuid.Value)
                        continue;

                    item = member;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The fresh projection for one listing.
        ///
        /// A ledger listing goes through the WEENIE, never through
        /// MarketSnapshot.FromLedgerProbe(entry.WorldObject, wcid): a ledger VaultEntry carries a
        /// null WorldObject by construction, and FromLedgerProbe over a null probe returns an EMPTY
        /// snapshot - which the degraded guard would then have to catch, after this pass had already
        /// thrown away the one it was meant to improve on.
        /// </summary>
        private static ListingSnapshot ProjectBackfillSnapshot(IMarketItemStore store, MarketListing listing,
                                                               WorldObject item)
        {
            if (listing.ItemGuid != null)
                return MarketSnapshot.FromItem(item);

            // The production store can build a real probe of the wcid through the account's own
            // world source; anything else (a test double) falls back to the shared weenie
            // projection, which is the same oracle the vault view uses for a ledger row.
            return store is VaultMarketItemStore vaultStore
                ? vaultStore.DescribeLedger(listing.SellerAccountId, listing.Wcid)
                : LedgerSnapshot(listing.Wcid);
        }

        /// <summary>
        /// Whether the fresh projection is WORSE than the stored one, in which case nothing is
        /// written. This is the guard that makes the whole pass safe to run blind.
        ///
        /// Two shapes count as degraded. An EMPTY name means the projection read an object it could
        /// not describe at all. The placeholder "Item wcid" means the oracle behind it - a weenie
        /// lookup, a probe instantiation - failed transiently and fell back; that is a fine answer
        /// for a row that never had a better one, and a regression over a row that did.
        /// </summary>
        private static bool IsDegradedBackfill(ListingSnapshot fresh, ListingSnapshot stored, uint wcid)
        {
            if (fresh == null || string.IsNullOrEmpty(fresh.Name))
                return true;

            var placeholder = $"Item {wcid}";

            if (!string.Equals(fresh.Name, placeholder, StringComparison.Ordinal))
                return false;

            var storedName = stored?.Name;

            return !string.IsNullOrEmpty(storedName) && !string.Equals(storedName, placeholder, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// What one <see cref="MarketManager.BackfillSnapshots"/> invocation did. Every listing this pass
    /// examined lands in exactly ONE outcome counter.
    ///
    /// <see cref="Considered"/> is the sum of those counters with one exception, and it is worth
    /// knowing which: a listing that closed between the id sweep and its own turn is counted in
    /// <see cref="ClosedMidPass"/> without ever being considered, because by then there was no open
    /// row to examine.
    /// </summary>
    public sealed class MarketBackfillReport
    {
        /// <summary>How many sample listing ids the report carries. A handful, for spot-checking in game.</summary>
        public const int MaxSamples = 5;

        /// <summary>
        /// The manager never booted its index (or was shut down), so there was nothing to walk. NOT
        /// the same as the market_enabled kill switch being off, which does NOT block this pass.
        /// </summary>
        public bool NotInitialized;

        /// <summary>Active listings this pass actually examined.</summary>
        public int Considered;

        /// <summary>Snapshots rewritten. In a preview this is what WOULD be rewritten.</summary>
        public int Rewritten;

        /// <summary>Re-projected to byte-identical JSON, so nothing was written.</summary>
        public int Unchanged;

        /// <summary>Skipped because the seller's vault was not ready. Try again later; nothing was lost.</summary>
        public int StoreNotReady;

        /// <summary>The listed item was not in the seller's vault - most often a purchase in flight.</summary>
        public int ItemUnresolved;

        /// <summary>The projection threw. The listing keeps what it captured.</summary>
        public int ProjectionFailed;

        /// <summary>The projection came back worse than the stored snapshot and was refused.</summary>
        public int ProjectionDegraded;

        /// <summary>The listing closed between being picked up and being written.</summary>
        public int ClosedMidPass;

        /// <summary>
        /// Rows whose database write FAILED - the whole batch could not run, or the individual
        /// statement threw. These listings are probably still Active, so their in-memory snapshot is
        /// ahead of the stored one until a later pass succeeds or a close persists it.
        ///
        /// NEVER holds a row that merely matched nothing; that is <see cref="ClosedMidPass"/>. The
        /// two are separated at the DAO precisely so a persistently failing write cannot hide inside
        /// a counter that reads as normal concurrent churn.
        /// </summary>
        public int WriteFailed;

        /// <summary>
        /// Rows resubmitted because an EARLIER pass's write failed for them, AND written this time.
        /// These are included in <see cref="Rewritten"/>; the separate count exists so an operator
        /// can see that a repair actually happened rather than reading a silent zero as "nothing was
        /// ever wrong".
        ///
        /// A repair count, not a submission count: a retry that fails again is counted in
        /// <see cref="WriteFailed"/> and NOT here, because "rewritten 0, write_failed 1, retried 1"
        /// would tell an operator a row was fixed on a pass that fixed nothing. The one exception is
        /// a preview, which submits nothing and so reports what it WOULD retry, exactly as its
        /// <see cref="Rewritten"/> reports what it would rewrite.
        /// </summary>
        public int RetriedAfterWriteFailure;

        /// <summary>
        /// Active listings still AHEAD of the cursor once this pass finished - what the next
        /// invocation of the same command will pick up. Above zero means the sweep is part way
        /// through, not that anything was lost.
        ///
        /// Counted under the SAME selection this pass used, so on a version-gated pass it is stale
        /// rows remaining and on a full sweep it is every Active row remaining.
        /// </summary>
        public int RemainingActive;

        /// <summary>
        /// Listings this page took from AHEAD of the cursor, as selected - which is exactly what
        /// decides whether the cursor moved.
        ///
        /// NOT the same as <see cref="Considered"/>, and the difference is the point: Considered
        /// counts rows that were still Active when their turn came, so a row that closed between
        /// selection and its own re-check is missing from it while the cursor still advanced past
        /// it. Anything asking "did this pass make progress" has to ask THIS, or an unlucky burst of
        /// concurrent closes reads as a stuck sweep.
        /// </summary>
        public int ForwardSelected;

        /// <summary>
        /// Active listings whose stored snapshot is BELOW <see cref="MarketSnapshot.CurrentVersion"/>,
        /// market-wide and independent of the cursor and of this pass's selection. The answer to
        /// "how much is actually out of date", which a preview reports on both arms - on a full sweep
        /// it is the only number that still means that, because RemainingActive there counts every
        /// Active row rather than the stale ones.
        /// </summary>
        public int StaleActive;

        /// <summary>
        /// This pass reached the end of the market, so the cursor has rewound and the next pass
        /// starts a fresh sweep from the beginning. Exactly the inverse of
        /// <see cref="RemainingActive"/> being above zero, named so the rendering does not have to
        /// infer intent from a number.
        /// </summary>
        public bool SweepComplete;

        /// <summary>Where this pass resumed from; both zero means it started at the beginning of the market.</summary>
        public uint ResumedFromAccountId;

        public uint ResumedFromListingId;

        /// <summary>
        /// The first few rewritten listing ids, for spot-checking one in game afterwards. In a real
        /// pass these are the ids SENT to the database rather than the ones it confirmed, since the
        /// batch reports a total and not a per-row result.
        /// </summary>
        public readonly List<uint> SampleRewrittenIds = new List<uint>();

        internal void AddSample(uint listingId)
        {
            if (SampleRewrittenIds.Count < MaxSamples)
                SampleRewrittenIds.Add(listingId);
        }

        public override string ToString()
            => $"considered {Considered}, rewritten {Rewritten}, unchanged {Unchanged}, store_not_ready {StoreNotReady}, " +
               $"item_unresolved {ItemUnresolved}, projection_failed {ProjectionFailed}, projection_degraded {ProjectionDegraded}, " +
               $"closed_mid_pass {ClosedMidPass}, write_failed {WriteFailed}, retried {RetriedAfterWriteFailure}, remaining_active {RemainingActive}, " +
               $"resumed_after {ResumedFromAccountId}/{ResumedFromListingId}, sweep_complete {SweepComplete}";
    }

    /// <summary>
    /// What one AUTOMATIC sweep did, summed over the pages it ran.
    ///
    /// Separate from <see cref="MarketBackfillReport"/> because the two answer different questions:
    /// that one describes a page and carries a cursor, this one describes a whole sweep and has to
    /// be able to say it never started.
    /// </summary>
    public sealed class MarketAutoBackfillOutcome
    {
        /// <summary>False when the sweep declined to start; <see cref="SkipReason"/> then says why.</summary>
        public bool Ran;

        /// <summary>Why nothing ran. Null exactly when <see cref="Ran"/> is true.</summary>
        public string SkipReason;

        /// <summary>Pages actually executed.</summary>
        public int Passes;

        /// <summary>True when the sweep reached the end of the market inside its bounds.</summary>
        public bool Completed;

        /// <summary>
        /// True when a bound stopped the sweep early. NOT an error: the cursor is left where it got
        /// to, and the operator can finish it by hand or leave it to the next world start.
        /// </summary>
        public bool StoppedAtBound;

        /// <summary>True when a page threw. The sweep stops; a world start must never fail over this.</summary>
        public bool Faulted;

        public int Considered;
        public int Rewritten;
        public int Unchanged;
        public int StoreNotReady;
        public int ItemUnresolved;
        public int ProjectionFailed;
        public int ProjectionDegraded;
        public int ClosedMidPass;
        public int WriteFailed;
        public int RetriedAfterWriteFailure;

        /// <summary>Out-of-date Active listings as of the LAST page, so it reads as what is left.</summary>
        public int StaleActive;

        internal void Absorb(MarketBackfillReport report)
        {
            if (report == null)
                return;

            Passes++;

            Considered += report.Considered;
            Rewritten += report.Rewritten;
            Unchanged += report.Unchanged;
            StoreNotReady += report.StoreNotReady;
            ItemUnresolved += report.ItemUnresolved;
            ProjectionFailed += report.ProjectionFailed;
            ProjectionDegraded += report.ProjectionDegraded;
            ClosedMidPass += report.ClosedMidPass;
            WriteFailed += report.WriteFailed;
            RetriedAfterWriteFailure += report.RetriedAfterWriteFailure;

            StaleActive = report.StaleActive;
        }

        public override string ToString()
            => Ran
                ? $"passes {Passes}, considered {Considered}, rewritten {Rewritten}, unchanged {Unchanged}, " +
                  $"store_not_ready {StoreNotReady}, item_unresolved {ItemUnresolved}, projection_failed {ProjectionFailed}, " +
                  $"projection_degraded {ProjectionDegraded}, closed_mid_pass {ClosedMidPass}, write_failed {WriteFailed}, " +
                  $"retried {RetriedAfterWriteFailure}, still_out_of_date {StaleActive}, completed {Completed}, " +
                  $"stopped_at_bound {StoppedAtBound}, faulted {Faulted}"
                : $"did not run: {SkipReason}";
    }
}
