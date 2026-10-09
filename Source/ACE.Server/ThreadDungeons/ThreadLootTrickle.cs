using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// Threads item 4 (2026-09-18): build pooled loot WHILE the run is Active, so the clear only deals and places.
    ///
    /// A pooled run banks each kill as a ledger entry (ThreadLootPool.BankKill) and, before this, materialised every
    /// one of them at the clear. The trickle claims ledger rolls from the head of the ledger - exactly the claim
    /// delivery makes (ThreadLootRollBudget.TryClaimNext: one roll at a time under a time budget, the held rare riding
    /// only an entry's final slice) - materialises them with the same materialiser, and parks the built piece in the
    /// run's prebuilt store (ThreadDungeonRun.TryAddPrebuilt). Delivery takes prebuilt pieces before the ledger and
    /// charges each one exactly what claiming it would have cost, so kill order, the rare rule and the group deal's
    /// sets are unchanged: only WHEN a roll is built moves.
    ///
    /// CADENCE. A chain of steps on the run landblock's own action queue, each step spending at most
    /// <see cref="ThreadDungeonRun.LootTrickleBudgetMs"/> (dynamic_dungeons_loot_trickle_budget_ms) and re-enqueueing
    /// itself (next world tick) only while it ran out of budget with ledger rolls still waiting. The world-thread
    /// ThreadDungeonManager.Tick (once a second) starts a chain when there is work and none is running. Nothing runs
    /// when there is nothing to build, so an idle run costs one cheap check a second.
    ///
    /// WHEN IT DOES NOT RUN (<see cref="ShouldRun"/>, tested by the Tick and again by every step):
    ///   - the run is not Active (Starting has no kills; from Cleared on, delivery owns the ledger);
    ///   - the budget is 0 (the switch-off: pre-trickle behaviour);
    ///   - the prebuilt store already holds <see cref="ThreadDungeonRun.LootTrickleMaxPrebuilt"/> objects (memory cap);
    ///   - the landblock is dormant. Landblock.TickMultiThreadedWork runs the action queue even for a DORMANT landblock
    ///     (only physics and creature ticks are gated), so the trickle must check Landblock.IsDormant itself;
    ///   - no roster member is inside (ThreadRunPresence.OnlineMembersInside): a logged-out or walked-out group
    ///     builds nothing.
    ///
    /// UNLOAD SAFETY. Landblock.Unload clears the action queue, so a queued step may never run. The run's store is the
    /// only source of truth - a step holds no items across ticks - and the chain holds a token like cache delivery
    /// does: re-stamped every step, and taken over by the next Tick once it is <see cref="StaleAfter"/> old.
    /// </summary>
    public static class ThreadLootTrickle
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public const int DefaultBudgetMs = 2;
        public const int MinBudgetMs = 0;
        public const int MaxBudgetMs = 50;

        /// <summary>
        /// Objects the trickle may hold built ahead of a clear. Measured (ZZ probe, 2026-09-18, test harness): a bare
        /// WorldObject about 3.3 KB, a loot-shaped one with ~100 properties and 8 spells about 8 KB. 1500 objects is
        /// therefore roughly 12 MB per run at the cap, against about 21 MB for an uncapped 6-seat run (~2,600 objects).
        /// </summary>
        public const int DefaultMaxPrebuilt = 1500;
        public const int MinMaxPrebuilt = 0;
        public const int MaxMaxPrebuilt = 20000;

        /// <summary>A trickle token older than this is taken over by the next Tick (its step was dropped by an unload).</summary>
        public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(5);

        /// <summary>Test seam for the materialiser. Null (the default) is production: ThreadCacheFiller.EntryMaterializer.</summary>
        internal static Func<ThreadLootLedgerEntry, List<WorldObject>> Materializer;

        /// <summary>Everything that must hold for a trickle step to build anything (see the class comment).</summary>
        internal static bool ShouldRun(ThreadDungeonRun run, bool landblockDormant, bool anyoneInside)
            => run != null && run.PooledLoot && run.State == ThreadDungeonRunState.Active
               && run.LootTrickleBudgetMs > 0
               && run.LedgerCount > 0
               && run.PrebuiltObjects < run.LootTrickleMaxPrebuilt
               && !landblockDormant && anyoneInside;

        private static bool AnyoneInside(ThreadDungeonRun run) => ThreadRunPresence.OnlineMembersInside(run).Count > 0;

        /// <summary>
        /// The world-thread start (ThreadDungeonManager.Tick): enqueues a trickle chain on the run landblock when there is
        /// work and no live chain holds the token. Returns true when a chain was queued.
        /// </summary>
        internal static bool Kick(ThreadDungeonRun run, Landblock landblock, DateTime nowUtc)
        {
            if (landblock == null || !ShouldRun(run, landblock.IsDormant, AnyoneInside(run)))
                return false;

            var token = run.TryBeginTrickle(nowUtc, StaleAfter);

            if (token == 0)
                return false;

            void Step()
            {
                var requeued = false;
                var watch = Stopwatch.StartNew();

                try
                {
                    if (!run.IsTrickleCurrent(token))
                        return;

                    run.RefreshTrickle(token, DateTime.UtcNow);

                    // Re-tested every step on the landblock thread: presence, dormancy and state can all change between
                    // one tick and the next.
                    if (!ShouldRun(run, landblock.IsDormant, AnyoneInside(run)))
                        return;

                    var budget = new ThreadLootRollBudget(run.CacheRollBatchSize, () => watch.Elapsed.TotalMilliseconds, run.LootTrickleBudgetMs);
                    var result = RunStep(run, Materializer ?? ThreadCacheFiller.EntryMaterializer, budget);

                    watch.Stop();
                    ServerMetrics.RecordThreadsLootTrickle(watch.Elapsed.TotalMilliseconds);
                    run.Perf.RecordTrickleStep(watch.ElapsedMilliseconds, result.ItemsBuilt);

                    if (result.More)
                    {
                        landblock.EnqueueAction(new ActionEventDelegate(Step));
                        requeued = true;
                    }
                }
                catch (Exception ex)
                {
                    // EnqueueAction escapes the caller's try/catch, so this guard is the chain's only one; a throwing
                    // step ends the chain, and the next Tick may start a fresh one.
                    log.Error($"[DYNDUNGEON] {run} loot trickle step threw", ex);
                }
                finally
                {
                    if (!requeued)
                        run.EndTrickle(token);
                }
            }

            landblock.EnqueueAction(new ActionEventDelegate(Step));
            return true;
        }

        /// <summary>What one trickle step did.</summary>
        internal sealed class StepResult
        {
            /// <summary>Pieces claimed and stored.</summary>
            public int Pieces;

            /// <summary>Loot items built (held rares excluded: they were rolled at the kill).</summary>
            public int ItemsBuilt;

            /// <summary>Continue on the next tick: the step ran out of budget with work still waiting.</summary>
            public bool More;
        }

        /// <summary>
        /// One trickle step's work, pure over the run and its seams: claims and builds ledger rolls in kill order until
        /// the budget is spent, the store reaches its cap, the ledger is empty or the run leaves Active. A claimed piece
        /// always ends up in exactly one place: the prebuilt store, or - when the run has Ended and refuses it -
        /// destroyed with its held rare. A materialisation that throws loses only its rolls (as at the clear); the
        /// piece's held rare is still stored, so it is delivered at the clear, and the step stops.
        /// </summary>
        internal static StepResult RunStep(ThreadDungeonRun run, Func<ThreadLootLedgerEntry, List<WorldObject>> materialize, ThreadLootRollBudget budget)
        {
            var result = new StepResult();
            var threw = false;

            while (run.State == ThreadDungeonRunState.Active && run.PrebuiltObjects < run.LootTrickleMaxPrebuilt && budget.TryClaimNext(run, out var piece))
            {
                List<WorldObject> items;

                try
                {
                    items = materialize(piece) ?? new List<WorldObject>();
                }
                catch (Exception ex)
                {
                    log.Error($"[DYNDUNGEON] {run} loot trickle materialising a ledger piece threw; its rolls are lost, its rare is kept", ex);
                    items = new List<WorldObject>();
                    threw = true;
                }

                if (!run.TryAddPrebuilt(piece, items))
                {
                    // Ended between the claim and the store: nothing will ever deliver these. The piece is already off
                    // the ledger and never reached the store, so DrainLootPool cannot report it: count and log it here,
                    // and the run summary and unclaimed-loot line carry the counter.
                    var droppedItems = items.Count(i => i != null && !ReferenceEquals(i, piece.HeldRare));
                    var droppedRares = piece.HeldRare != null ? 1 : 0;

                    foreach (var obj in new PrebuiltLoot(piece, items).Objects().ToList())
                        obj.Destroy();

                    run.Perf.RecordTrickleDropped(droppedItems, droppedRares);
                    log.Warn($"[DYNDUNGEON] {run} loot trickle built a ledger piece as the run ended; destroyed {droppedItems} item(s) and {droppedRares} held rare(s)");

                    break;
                }

                result.Pieces++;
                result.ItemsBuilt += items.Count(i => i != null && !ReferenceEquals(i, piece.HeldRare));

                if (threw)
                    break;
            }

            result.More = !threw && budget.Exhausted && run.State == ThreadDungeonRunState.Active
                          && run.LedgerCount > 0 && run.PrebuiltObjects < run.LootTrickleMaxPrebuilt;

            return result;
        }
    }

    public sealed partial class ThreadDungeonRun
    {
        private long trickleToken;
        private long trickleTokenSeq;
        private DateTime trickleStampUtc;

        /// <summary>
        /// Admits one trickle chain: a non-zero token when none is held, or the held one is older than
        /// <paramref name="staleAfter"/> (its step was dropped by Landblock.Unload clearing the queue). 0 otherwise, and
        /// always 0 once Ended.
        /// </summary>
        internal long TryBeginTrickle(DateTime nowUtc, TimeSpan staleAfter)
        {
            lock (stateLock)
            {
                if (state == ThreadDungeonRunState.Ended) return 0;
                if (trickleToken != 0 && nowUtc - trickleStampUtc < staleAfter) return 0;
                trickleToken = ++trickleTokenSeq;
                trickleStampUtc = nowUtc;
                return trickleToken;
            }
        }

        internal bool IsTrickleCurrent(long token) { lock (stateLock) return token != 0 && trickleToken == token; }

        internal void RefreshTrickle(long token, DateTime nowUtc)
        {
            lock (stateLock)
            {
                if (token != 0 && trickleToken == token)
                    trickleStampUtc = nowUtc;
            }
        }

        internal void EndTrickle(long token)
        {
            lock (stateLock)
            {
                if (token != 0 && trickleToken == token)
                    trickleToken = 0;
            }
        }
    }
}
