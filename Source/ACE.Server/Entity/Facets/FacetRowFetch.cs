using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

using ACE.Database.Models.Shard;

namespace ACE.Server.Entity.Facets
{
    /// <summary>
    /// The blocking-read-with-timeout mechanism Player.TryGetCharacterFacetRows depends on, extracted
    /// as a pure, Player-independent helper so it is unit-testable. No test in ACE.Server.Tests
    /// constructs a live Player - Player's constructors call DatabaseManager.Authentication.GetAccountById
    /// unconditionally, a real MySQL round-trip unavailable in this test project (independently
    /// documented in MuleCommerceTests.cs, PersonalVendorTests.cs and StageTestCommandsTests.cs) - so
    /// anything here that needs test coverage has to be extractable down to this level, matching the
    /// same reasoning Player.LookupSkillCreditCost's own doc comment gives for why FacetPools stays
    /// DatManager-free.
    ///
    /// TryFetch signals "the read did not complete" (a timeout, or the fetcher never answering its
    /// callback) as a FALSE return with rows == null, DISTINCT from a genuine empty answer (true,
    /// rows.Count == 0). Collapsing the two was Task 11 fix round 1's Finding 1: TrySwitchFacet used
    /// to degrade a timeout to an empty list, which was then read as "slot never visited" and fed
    /// BuildFreshFacetSkills() onto a live character - silently destroying a real stored build
    /// whenever the shard read queue happened to be briefly backed up (an acknowledged real occurrence -
    /// see AccountVaultStore.SaveAndWait's own doc comment). Every LOAD-BEARING caller (a switch, a
    /// rename that looks up an existing row) must treat a false return as its own refusal reason, never
    /// as "no rows". A caller whose answer is merely ADVISORY (a confirmation-prompt peek, a read-only
    /// listing display) may still collapse false to an empty list, since the worst outcome there is an
    /// extra prompt or a stale display, never a mutation.
    /// </summary>
    public static class FacetRowFetch
    {
        /// <summary>
        /// Calls <paramref name="fetcher"/>(<paramref name="characterId"/>, callback) and blocks the
        /// calling thread until the callback fires or <paramref name="timeoutMs"/> elapses. Returns false
        /// (with rows == null) when the callback never fired in time, or when <paramref name="fetcher"/>
        /// itself is null. On success, rows is never null (an empty list stands in for a null callback
        /// argument), matching character_facet's own "absence is meaningful, never backfilled" contract.
        /// </summary>
        public static bool TryFetch(
            uint characterId,
            int timeoutMs,
            Action<uint, Action<List<CharacterFacet>>> fetcher,
            out List<CharacterFacet> rows)
        {
            if (fetcher == null)
            {
                rows = null;
                return false;
            }

            var gate = new object();
            List<CharacterFacet> result = null;
            var fired = false;

            fetcher(characterId, r =>
            {
                lock (gate)
                {
                    result = r;
                    fired = true;
                    Monitor.Pulse(gate);
                }
            });

            lock (gate)
            {
                if (!fired)
                    Monitor.Wait(gate, timeoutMs);

                if (!fired)
                {
                    rows = null;
                    return false;
                }

                rows = result ?? new List<CharacterFacet>();
                return true;
            }
        }

        /// <summary>
        /// The WRITE-side mirror of <see cref="TryFetch"/>: hands every row in <paramref name="rows"/> to
        /// <paramref name="saver"/> and blocks the calling thread until all of them have confirmed or
        /// <paramref name="timeoutMs"/> elapses. Returns TRUE only when every row reported success.
        ///
        /// WHY THIS EXISTS AT ALL. The read side has been defended since Task 11 fix round 1, but the
        /// write side was fire-and-forget everywhere, which is correct only for a caller that merely
        /// refreshes a cached row (Player.SaveFacetRowAsync logs a failure and moves on). It is NOT
        /// correct for a caller that then writes a ONE-SHOT marker saying the work is done: the class
        /// ability overhaul respec stamped its per-character guard unconditionally, so a facet write lost
        /// to a briefly backed-up queue left a stored slot holding a fully-ranked build that could never
        /// be cleared again - the exact free-rebuy double-dip that sweep exists to prevent. Any caller
        /// whose next step mutates, or records completion, on the assumption the write landed must check
        /// this return value and refuse, precisely as a load-bearing reader must refuse a false TryFetch.
        ///
        /// ONE SHARED DEADLINE, NOT ONE PER ROW, deliberately: shard writes are processed in FIFO order
        /// on SerializedShardDatabase's single worker thread, so the last confirmation implies the ones
        /// queued before it, and a per-row timeout would multiply the worst-case world-thread block by
        /// the number of slots.
        ///
        /// Monitor rather than an event object, for the reason <see cref="TryFetch"/> gives: a
        /// confirmation arriving after the deadline pulses a lock nobody is waiting on, which is inert,
        /// where a late Set on an already-disposed event object would throw on the database thread.
        /// </summary>
        public static bool TrySaveAll(
            IReadOnlyList<CharacterFacet> rows,
            int timeoutMs,
            Action<CharacterFacet, Action<bool>> saver)
        {
            if (saver == null)
                return false;

            // Nothing to write is a vacuous success, never a failure - a caller with no dirty rows has
            // nothing that could have been lost, so refusing here would block a sweep for no reason.
            if (rows == null || rows.Count == 0)
                return true;

            var gate = new object();
            var pending = rows.Count;
            var allSucceeded = true;

            foreach (var row in rows)
            {
                saver(row, ok =>
                {
                    lock (gate)
                    {
                        if (!ok)
                            allSucceeded = false;

                        pending--;

                        if (pending == 0)
                            Monitor.Pulse(gate);
                    }
                });
            }

            var elapsed = Stopwatch.StartNew();

            lock (gate)
            {
                while (pending > 0)
                {
                    var remaining = timeoutMs - (int)elapsed.ElapsedMilliseconds;

                    if (remaining <= 0)
                        return false;

                    // Monitor.Wait can return before it is pulsed, so the remaining budget is recomputed
                    // each pass rather than trusting one Wait call to consume the whole timeout.
                    Monitor.Wait(gate, remaining);
                }

                return allSucceeded;
            }
        }
    }
}
