using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Models.Shard;
using ACE.Server.Entity.Facets;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Task 11 fix round 1, Finding 1 (CRITICAL): TrySwitchFacet used to reuse a helper that degraded
    /// a timed-out/failed character_facet read to an EMPTY list, indistinguishable from a genuine "this
    /// slot was never visited" answer. That false-empty then drove BuildFreshFacetSkills() onto a live
    /// character, silently destroying a real stored build the next time the slot's row was saved. The
    /// fix splits the signal into TryFetch's bool return (false = did not complete, rows == null) versus
    /// a genuine answer (true, rows possibly empty).
    ///
    /// No test in this project constructs a live Player (see MuleCommerceTests.cs's class remarks for
    /// the three independently-documented reasons), so Player.TrySwitchFacet itself cannot be exercised
    /// here. FacetRowFetch.TryFetch is the exact mechanism it depends on for this fix, extracted to a
    /// pure static helper for that reason - these tests exercise it directly, with a fake fetcher and a
    /// short timeout so a "the DB queue is backed up" scenario runs in milliseconds, not
    /// Player.FacetDbTimeoutMs's real 5000ms.
    /// </summary>
    [TestClass]
    public class FacetRowFetchTests
    {
        private const int ShortTimeoutMs = 30;

        [TestMethod]
        public void TryFetch_FetcherAnswersSynchronously_ReturnsTrueWithTheRows()
        {
            var expected = new List<CharacterFacet>
            {
                new CharacterFacet { CharacterId = 1, Slot = 2, SkillsJson = "[]", AbilitiesJson = "{}", EquipJson = "[]" },
            };

            var ok = FacetRowFetch.TryFetch(1, ShortTimeoutMs, (id, callback) => callback(expected), out var rows);

            Assert.IsTrue(ok, "a fetcher that answers immediately must report success");
            Assert.AreSame(expected, rows, "the exact rows the fetcher handed back must be returned unchanged");
        }

        [TestMethod]
        public void TryFetch_FetcherAnswersWithNull_ReturnsTrueWithAnEmptyListNeverNull()
        {
            var ok = FacetRowFetch.TryFetch(1, ShortTimeoutMs, (id, callback) => callback(null), out var rows);

            Assert.IsTrue(ok, "a null rows argument from the fetcher is still a completed answer, not a failure");
            Assert.IsNotNull(rows, "a genuine answer must never come back null - null is reserved for a failed read");
            Assert.AreEqual(0, rows.Count);
        }

        /// <summary>
        /// THE regression case for Finding 1: a fetcher that never calls its callback at all - simulating
        /// the shard read queue being backed up past the timeout - must return FALSE with rows == null,
        /// never a silently-empty (true, empty list) success. A caller that checked only "rows.Count == 0"
        /// instead of the bool return would misread this exact scenario as "slot never visited".
        /// </summary>
        [TestMethod]
        public void TryFetch_FetcherNeverAnswers_ReturnsFalseWithNullRows_NotAnEmptySuccess()
        {
            var ok = FacetRowFetch.TryFetch(1, ShortTimeoutMs, (id, callback) => { /* never calls back - simulates a backed-up read */ }, out var rows);

            Assert.IsFalse(ok, "a read that never completes must be reported as a failure, not collapsed to an empty success");
            Assert.IsNull(rows, "rows must be null on a failed read, so a caller cannot mistake it for a genuine empty answer");
        }

        [TestMethod]
        public void TryFetch_NullFetcher_ReturnsFalseWithNullRows()
        {
            var ok = FacetRowFetch.TryFetch(1, ShortTimeoutMs, null, out var rows);

            Assert.IsFalse(ok);
            Assert.IsNull(rows);
        }

        /// <summary>
        /// A callback that fires AFTER TryFetch has already given up and returned must not throw and must
        /// not corrupt a later call - the whole reason TryFetch uses a Monitor pulse rather than an event
        /// object (see FacetRowFetch's doc comment) is that a late pulse against a lock nobody is
        /// waiting on is inert, unlike a late Set on an event object that could already be disposed.
        /// </summary>
        [TestMethod]
        public void TryFetch_LateCallback_AfterTimeout_DoesNotThrowAndDoesNotAffectTheResult()
        {
            Action<List<CharacterFacet>> capturedCallback = null;

            var ok = FacetRowFetch.TryFetch(1, ShortTimeoutMs, (id, callback) => capturedCallback = callback, out var rows);

            Assert.IsFalse(ok, "no callback fired within the timeout, so this call must already have failed");
            Assert.IsNull(rows);

            Exception thrown = null;

            // Fire the "late" callback well after TryFetch already returned. This must be inert, not throw.
            var lateCallbackRan = new ManualResetEventSlim(false);

            Task.Run(() =>
            {
                try
                {
                    capturedCallback(new List<CharacterFacet>());
                }
                catch (Exception ex)
                {
                    thrown = ex;
                }
                finally
                {
                    lateCallbackRan.Set();
                }
            });

            Assert.IsTrue(lateCallbackRan.Wait(TimeSpan.FromSeconds(5)), "the late callback never ran at all");
            Assert.IsNull(thrown, $"a callback arriving after the timeout must be inert, not throw: {thrown}");
        }

        // ---- TrySaveAll: the write-side mirror -------------------------------------------------
        //
        // Added for the class ability overhaul respec, whose one-shot guard was being stamped after
        // fire-and-forget facet writes. A write lost to a backed-up queue left a stored facet holding a
        // fully-ranked build that could never be cleared again. These exercise the mechanism the sweep
        // now refuses on, with a fake saver so a degraded-queue scenario runs in milliseconds.

        private static CharacterFacet Row(byte slot)
            => new CharacterFacet { CharacterId = 1, Slot = slot, SkillsJson = "[]", AbilitiesJson = "{}", EquipJson = "[]" };

        [TestMethod]
        public void TrySaveAll_EveryRowConfirmsSuccess_ReturnsTrue()
        {
            var saved = new List<CharacterFacet>();

            var ok = FacetRowFetch.TrySaveAll(
                new[] { Row(1), Row(2), Row(3) }, ShortTimeoutMs,
                (row, callback) => { saved.Add(row); callback(true); });

            Assert.IsTrue(ok);
            Assert.AreEqual(3, saved.Count, "every row must be handed to the saver");
        }

        /// <summary>
        /// A row whose write reported FAILURE is the case the sweep must not stamp its guard over. It is
        /// distinct from a timeout and must be reported just as firmly.
        /// </summary>
        [TestMethod]
        public void TrySaveAll_AnyRowReportsFailure_ReturnsFalse()
        {
            var ok = FacetRowFetch.TrySaveAll(
                new[] { Row(1), Row(2) }, ShortTimeoutMs,
                (row, callback) => callback(row.Slot != 2));

            Assert.IsFalse(ok, "one failed row must fail the whole batch - the caller cannot partially stamp");
        }

        /// <summary>
        /// THE regression case: a saver that never answers, simulating a backed-up write queue. Must be
        /// false, never an optimistic success.
        /// </summary>
        [TestMethod]
        public void TrySaveAll_SaverNeverConfirms_ReturnsFalse()
        {
            var ok = FacetRowFetch.TrySaveAll(
                new[] { Row(1) }, ShortTimeoutMs,
                (row, callback) => { /* never confirms - simulates a backed-up write queue */ });

            Assert.IsFalse(ok);
        }

        /// <summary>
        /// The deadline is shared across the batch, not applied per row: N unanswered rows must cost ONE
        /// timeout, not N. A per-row implementation of this method would take at least N * timeout, so
        /// the bound below discriminates between the two rather than merely observing that it finished.
        /// </summary>
        [TestMethod]
        public void TrySaveAll_UnansweredBatch_CostsOneSharedTimeout_NotOnePerRow()
        {
            const int timeoutMs = 100;
            const int rowCount = 10;

            var rows = new List<CharacterFacet>();
            for (byte i = 1; i <= rowCount; i++)
                rows.Add(Row(i));

            var elapsed = Stopwatch.StartNew();

            var ok = FacetRowFetch.TrySaveAll(rows, timeoutMs, (row, callback) => { /* never confirms */ });

            elapsed.Stop();

            Assert.IsFalse(ok);
            Assert.IsTrue(elapsed.ElapsedMilliseconds < rowCount * timeoutMs / 2,
                $"the wait must be bounded by ONE shared deadline; {rowCount} rows took {elapsed.ElapsedMilliseconds} ms against a {timeoutMs} ms timeout, which looks per-row");
        }

        [TestMethod]
        public void TrySaveAll_NoRowsToWrite_IsAVacuousSuccess()
        {
            Assert.IsTrue(FacetRowFetch.TrySaveAll(null, ShortTimeoutMs, (row, callback) => callback(true)));
            Assert.IsTrue(FacetRowFetch.TrySaveAll(new CharacterFacet[0], ShortTimeoutMs, (row, callback) => callback(true)));
        }

        [TestMethod]
        public void TrySaveAll_NullSaver_ReturnsFalse()
        {
            Assert.IsFalse(FacetRowFetch.TrySaveAll(new[] { Row(1) }, ShortTimeoutMs, null));
        }

        [TestMethod]
        public void TrySaveAll_LateConfirmation_AfterTimeout_IsInert()
        {
            Action<bool> captured = null;

            var ok = FacetRowFetch.TrySaveAll(new[] { Row(1) }, ShortTimeoutMs, (row, callback) => captured = callback);

            Assert.IsFalse(ok);

            Exception thrown = null;
            var lateRan = new ManualResetEventSlim(false);

            Task.Run(() =>
            {
                try { captured(true); }
                catch (Exception ex) { thrown = ex; }
                finally { lateRan.Set(); }
            });

            Assert.IsTrue(lateRan.Wait(TimeSpan.FromSeconds(5)), "the late confirmation never ran at all");
            Assert.IsNull(thrown, $"a confirmation arriving after the deadline must be inert, not throw: {thrown}");
        }
    }
}
