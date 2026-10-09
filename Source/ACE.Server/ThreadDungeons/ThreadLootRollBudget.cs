using System;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// One delivery STEP's allowance, shared by every cache that step fills (Docs/Threads/POOLED-LOOT-CACHE-DESIGN.md
    /// section 4, "Batched delivery").
    ///
    /// Why it exists. Materialising a ledger entry is the expensive half of pooled delivery: it is a
    /// LootGenerationFactory.CreateRandomLootObjects call per roll, and every one of them runs inside a landblock
    /// action, which runs inside LandblockManager's Parallel.ForEach barrier on the single world thread. A pass
    /// that materialised a whole run's ledger in one action therefore froze the world heartbeat for as long as it
    /// took. This budget is what lets ThreadCachePlacer.RequestPlacement do a little of that work, re-enqueue
    /// itself onto the same landblock queue, and let the world tick in between - the same shape
    /// ThreadDungeonSpawner.PlaceBatch already uses for creature placement.
    ///
    /// Two limits, one unit each (2026-09-18):
    ///   - TIME is the unit of a step. A TIMED budget (production: dynamic_dungeons_cache_step_budget_ms) reads an
    ///     injected clock and is exhausted after the roll during which the clock crosses the budget. It hands out
    ///     one roll per claim, so it can stop at that roll rather than at the end of a whole entry.
    ///   - ROLLS are only the hard cap (dynamic_dungeons_cache_roll_batch_size): however cheap the rolls turn out
    ///     to be, a step never materialises more than this many.
    /// An untimed budget (tests, and <see cref="Unlimited"/>) is the #1210 roll counter exactly, except that a fat
    /// entry at the head of a step is now split rather than taken whole.
    ///
    /// Progress is guaranteed: the first claim of a step is always admitted, at least one roll. Nothing admits more
    /// than the roll cap, so a single fat group entry (up to ThreadLootPool.MaxRollsPerKill rolls) is consumed a
    /// slice at a time across steps (ThreadDungeonRun.TryClaimLootRolls) instead of materialising whole.
    ///
    /// It has no thread affinity of its own: one budget belongs to one step, and every step runs on the run
    /// landblock's own queue, one at a time (invariant 3).
    /// </summary>
    public sealed class ThreadLootRollBudget
    {
        /// <summary>
        /// The shipped default of dynamic_dungeons_cache_roll_batch_size, and the default of
        /// ThreadDungeonRun.CacheRollBatchSize for callers (tests included) that never stamp one.
        /// </summary>
        public const int DefaultRollsPerStep = 12;

        /// <summary>The shipped default of dynamic_dungeons_cache_step_budget_ms, and of ThreadDungeonRun.CacheStepBudgetMs.</summary>
        public const int DefaultStepBudgetMs = 8;

        /// <summary>dynamic_dungeons_cache_step_budget_ms is clamped to [<see cref="MinStepBudgetMs"/>, <see cref="MaxStepBudgetMs"/>] at read.</summary>
        public const int MinStepBudgetMs = 1;
        public const int MaxStepBudgetMs = 250;

        private readonly int max;
        private readonly Func<double> elapsedMs;
        private readonly double budgetMs;
        private int spent;
        private bool claimedAny;

        /// <summary>An untimed budget of <paramref name="maxRolls"/> rolls; anything below 1 reads as 1, so a step always makes progress.</summary>
        public ThreadLootRollBudget(int maxRolls) : this(maxRolls, null, double.PositiveInfinity)
        {
        }

        /// <summary>
        /// A timed budget: at most <paramref name="maxRolls"/> rolls (below 1 reads as 1), and exhausted once
        /// <paramref name="elapsedMs"/> reads at least <paramref name="budgetMs"/> after the step's first claim. The
        /// clock is injected so a test can drive it deterministically; production passes the step's own Stopwatch. A
        /// null clock is an untimed budget.
        /// </summary>
        public ThreadLootRollBudget(int maxRolls, Func<double> elapsedMs, double budgetMs)
        {
            max = Math.Max(1, maxRolls);
            this.elapsedMs = elapsedMs;
            this.budgetMs = double.IsNaN(budgetMs) ? double.PositiveInfinity : budgetMs;
        }

        /// <summary>
        /// A fresh budget nothing can exhaust: the pre-batching behaviour, for the unbudgeted public overloads and
        /// for tests that assert on a whole pass at once. A method rather than a property so it is obvious at every
        /// call site that this is a new budget, not a shared one accumulating spend.
        /// </summary>
        public static ThreadLootRollBudget Unlimited() => new ThreadLootRollBudget(int.MaxValue);

        /// <summary>Rolls this step has spent.</summary>
        public int Spent => spent;

        /// <summary>The step's roll cap (dynamic_dungeons_cache_roll_batch_size in production; int.MaxValue when unlimited).</summary>
        public int MaxRolls => max;

        /// <summary>Rolls this step may still spend under the hard cap, never negative.</summary>
        public int Remaining => Math.Max(0, max - spent);

        /// <summary>True when this budget reads a clock (production); false for a pure roll counter.</summary>
        public bool IsTimed => elapsedMs != null;

        /// <summary>True when the step's clock has reached its time budget. Always false for an untimed budget.</summary>
        public bool TimeUp => elapsedMs != null && elapsedMs() >= budgetMs;

        /// <summary>
        /// True once this step is done: its roll cap is spent, or, after at least one claim, its time is up. This is
        /// the ONE continuation signal ThreadCachePlacer's chain reads: a step that stopped for any other reason
        /// (nothing left to deliver, no room to form a cache, a fresh cache that refused everything) is terminal, and
        /// the existing retries own whatever is left.
        /// </summary>
        public bool Exhausted => spent >= max || (claimedAny && TimeUp);

        /// <summary>
        /// How many rolls the next claim may take: 0 once <see cref="Exhausted"/>; one for a timed budget (so it can
        /// stop at the roll that crosses the budget); otherwise what the roll cap has left.
        /// </summary>
        internal int NextClaimRolls => Exhausted ? 0 : IsTimed ? Math.Min(1, Remaining) : Remaining;

        /// <summary>
        /// May the next claim split an entry that does not fit? Always for a timed budget (it claims one roll at a
        /// time). For an untimed one only on the step's first claim, which is what keeps a fat entry from wedging the
        /// head of the ledger while leaving every later claim the #1210 whole-entry rule.
        /// </summary>
        internal bool NextClaimMayBePartial => IsTimed || !claimedAny;

        /// <summary>
        /// Invariant 1, within this step's allowance: claims up to <see cref="NextClaimRolls"/> rolls of the oldest
        /// ledger entry (kill order) and charges them. False when the step is exhausted or nothing fits.
        /// </summary>
        public bool TryClaimNext(ThreadDungeonRun run, out ThreadLootLedgerEntry entry)
        {
            entry = null;

            if (run == null)
                return false;

            var allowance = NextClaimRolls;

            if (allowance < 1)
                return false;

            if (!run.TryClaimLootRolls(allowance, NextClaimMayBePartial, out entry))
                return false;

            Spend(entry.Rolls);
            return true;
        }

        /// <summary>
        /// The prebuilt counterpart of <see cref="TryClaimNext"/>: takes the oldest piece the trickle built ahead of the
        /// clear (ThreadDungeonRun.TryTakePrebuilt) and charges its rolls, exactly as a fresh claim of that piece would
        /// have been charged. That is what keeps delivery's shape - which rolls land in which step, and so which rolls
        /// a group deal sorts together - the same whether a roll was built early or at the clear. A piece is taken
        /// whenever the step can claim at all, whatever its size, so a piece can never wedge the store.
        /// </summary>
        internal bool TryTakePrebuilt(ThreadDungeonRun run, out PrebuiltLoot built)
        {
            built = null;

            if (run == null || NextClaimRolls < 1 || !run.TryTakePrebuilt(out built))
                return false;

            Spend(built.Piece.Rolls);
            return true;
        }

        /// <summary>
        /// Charges rolls this step spent outside <see cref="TryClaimNext"/>: the group deal claims its own entries,
        /// and the boss bonus is charged the number of items it built (each of those is a loot roll too). Always
        /// marks the step as having claimed something, so the time budget now applies and the next claim is
        /// measured against the allowance.
        /// </summary>
        public void Spend(int rolls)
        {
            claimedAny = true;

            if (rolls > 0)
                spent = (int)Math.Min(int.MaxValue, (long)spent + rolls);
        }
    }
}
