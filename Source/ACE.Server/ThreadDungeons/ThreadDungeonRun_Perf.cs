using System;
using System.Threading;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// Performance counters for one run, summed from the threads that do the work and logged once, as the run's
    /// Info summary line, when it ends (ThreadDungeonManager.EndRun). Every field is written with Interlocked, so the
    /// planner (world thread), the placement and delivery chains (the run landblock's thread) and EndRun (any thread)
    /// never need the run's stateLock for them. Diagnostics only: nothing reads these to decide anything.
    /// </summary>
    public sealed class ThreadRunPerfStats
    {
        private long planMs = -1;
        private long placeMs;
        private long placeSteps;
        private long doorPassMs = -1;
        private long doorsUnlocked;
        private long lootSteps;
        private long lootTotalMs;
        private long lootMaxStepMs;
        private long lootChains;
        private long weenieMisses;
        private long bandLow = -1;
        private long planMemoHits;
        private long planMemoLookups;

        /// <summary>DungeonPopulationBuilder.Build, in ms; -1 until the plan was built.</summary>
        public long PlanMs => Interlocked.Read(ref planMs);

        /// <summary>
        /// ThreadPlanCache structural lookups this run's plan build served from cache, and how many it took.
        /// Both 0 when the cache is switched off, which is also what <see cref="PlanCached"/> reports as false.
        /// </summary>
        public long PlanMemoHits => Interlocked.Read(ref planMemoHits);

        public long PlanMemoLookups => Interlocked.Read(ref planMemoLookups);

        /// <summary>
        /// True when the plan build took at least one ThreadPlanCache lookup and every one of them hit - the
        /// "fully warm" case the cache exists to produce. STRUCTURAL lookups only (the fit projection, the two
        /// band searches, the band median and the band standard); the per-wcid memos are deliberately not
        /// counted, because one first-seen boss wcid would otherwise report an entirely warm run as cold.
        /// </summary>
        public bool PlanCached => PlanMemoLookups > 0 && PlanMemoHits == PlanMemoLookups;

        /// <summary>The plan's effective roster band low edge (DungeonSpawnPlan.EffectiveBandLow); -1 until the plan was built.</summary>
        public long BandLow => Interlocked.Read(ref bandLow);

        /// <summary>Total ms across every PlaceBatch step. The door pass is NOT in here: see <see cref="DoorPassMs"/>.</summary>
        public long PlaceMs => Interlocked.Read(ref placeMs);

        public long PlaceSteps => Interlocked.Read(ref placeSteps);

        /// <summary>
        /// The one-off door pass (ThreadDungeonSpawner.DoorPass), in ms, and the doors it unlocked. Its own
        /// counter since the pass became its own chain step (2026-09-21): it used to be charged to the first
        /// placement step, which is exactly what made placeMs unreadable as a per-creature cost.
        ///
        /// -1 until the door step has run, on the <see cref="PlanMs"/> convention: a sub-millisecond pass reads
        /// 0, and a run whose door step never ran at all (plan build threw, or the run ended first) reads -1.
        /// </summary>
        public long DoorPassMs => Interlocked.Read(ref doorPassMs);

        public long DoorsUnlocked => Interlocked.Read(ref doorsUnlocked);

        /// <summary>Cache-delivery steps across every delivery chain of the run.</summary>
        public long LootSteps => Interlocked.Read(ref lootSteps);

        public long LootTotalMs => Interlocked.Read(ref lootTotalMs);

        /// <summary>The single slowest cache-delivery step of the run: the number the time budget exists to bound.</summary>
        public long LootMaxStepMs => Interlocked.Read(ref lootMaxStepMs);

        public long LootChains => Interlocked.Read(ref lootChains);

        /// <summary>World-DB weenie cache misses taken by the run's delivery steps (WorldDatabaseWithEntityCache).</summary>
        public long WeenieMisses => Interlocked.Read(ref weenieMisses);

        public void RecordPlan(long milliseconds, int effectiveBandLow, int memoHits = 0, int memoLookups = 0)
        {
            Interlocked.Exchange(ref planMs, milliseconds);
            Interlocked.Exchange(ref bandLow, effectiveBandLow);
            Interlocked.Exchange(ref planMemoHits, Math.Max(0, memoHits));
            Interlocked.Exchange(ref planMemoLookups, Math.Max(0, memoLookups));
        }

        public void RecordPlaceStep(long milliseconds)
        {
            Interlocked.Add(ref placeMs, milliseconds);
            Interlocked.Increment(ref placeSteps);
        }

        /// <summary>
        /// The door step's own duration and door count. Exchange rather than Add: the pass runs exactly once per
        /// run, so a second call would be a bug, and summing it would hide that.
        /// </summary>
        public void RecordDoorPass(long milliseconds, int doors)
        {
            Interlocked.Exchange(ref doorPassMs, milliseconds);
            Interlocked.Exchange(ref doorsUnlocked, Math.Max(0, doors));
        }

        public void RecordLootStep(long milliseconds, long misses)
        {
            Interlocked.Increment(ref lootSteps);
            Interlocked.Add(ref lootTotalMs, milliseconds);
            Interlocked.Add(ref weenieMisses, Math.Max(0, misses));

            long seen;

            while (milliseconds > (seen = Interlocked.Read(ref lootMaxStepMs)))
            {
                if (Interlocked.CompareExchange(ref lootMaxStepMs, milliseconds, seen) == seen)
                    break;
            }
        }

        public void RecordLootChain() => Interlocked.Increment(ref lootChains);

        // Threads item 4 (loot trickle) and item 8 (stack consolidation), 2026-09-18.
        private long prebuilt;
        private long builtAtClear;
        private long trickleSteps;
        private long trickleMs;
        private long trickleMaxStepMs;
        private long stacksMerged;
        private long trickleDroppedItems;
        private long trickleDroppedRares;

        /// <summary>Loot items the trickle built ahead of the clear (held rares excluded: they were rolled at the kill).</summary>
        public long Prebuilt => Interlocked.Read(ref prebuilt);

        /// <summary>Loot items materialised by delivery at the clear (held rares excluded).</summary>
        public long BuiltAtClear => Interlocked.Read(ref builtAtClear);

        public long TrickleSteps => Interlocked.Read(ref trickleSteps);

        public long TrickleMs => Interlocked.Read(ref trickleMs);

        public long TrickleMaxStepMs => Interlocked.Read(ref trickleMaxStepMs);

        /// <summary>Objects merged away by stack consolidation: the object-count reduction item 8 exists for.</summary>
        public long StacksMerged => Interlocked.Read(ref stacksMerged);

        public void RecordTrickleStep(long milliseconds, int itemsBuilt)
        {
            Interlocked.Increment(ref trickleSteps);
            Interlocked.Add(ref trickleMs, milliseconds);
            Interlocked.Add(ref prebuilt, Math.Max(0, itemsBuilt));

            long seen;

            while (milliseconds > (seen = Interlocked.Read(ref trickleMaxStepMs)))
            {
                if (Interlocked.CompareExchange(ref trickleMaxStepMs, milliseconds, seen) == seen)
                    break;
            }
        }

        /// <summary>
        /// Loot items (held rares excluded) the trickle built and then destroyed because the run Ended between the
        /// claim and the store: already off the ledger and never in the store, so DrainLootPool never sees them.
        /// </summary>
        public long TrickleDroppedItems => Interlocked.Read(ref trickleDroppedItems);

        /// <summary>Held rares destroyed on the same path as <see cref="TrickleDroppedItems"/>.</summary>
        public long TrickleDroppedRares => Interlocked.Read(ref trickleDroppedRares);

        public void RecordTrickleDropped(int items, int rares)
        {
            Interlocked.Add(ref trickleDroppedItems, Math.Max(0, items));
            Interlocked.Add(ref trickleDroppedRares, Math.Max(0, rares));
        }

        public void RecordBuiltAtClear(int items) => Interlocked.Add(ref builtAtClear, Math.Max(0, items));

        public void RecordStacksMerged(int removed) => Interlocked.Add(ref stacksMerged, Math.Max(0, removed));
    }

    public sealed partial class ThreadDungeonRun
    {
        /// <summary>This run's performance counters; see <see cref="ThreadRunPerfStats"/>.</summary>
        public ThreadRunPerfStats Perf { get; } = new ThreadRunPerfStats();
    }
}
