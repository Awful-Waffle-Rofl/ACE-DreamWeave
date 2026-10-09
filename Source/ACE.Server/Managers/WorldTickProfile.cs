using System;
using System.Diagnostics;

namespace ACE.Server.Managers
{
    /// <summary>
    /// WaffleACE: the leaf phases one world-loop iteration is split into. Every phase is a LEAF: the lap
    /// profiler charges all time since the previous lap to the phase being lapped, so the phases of one
    /// iteration sum exactly to that iteration's measured total (sleep excluded) and nothing is counted twice.
    ///
    /// The numeric order is load-bearing in two places: <see cref="WorldTickPhases.TagNames"/> is indexed by
    /// it, and <see cref="WorldTickPhases.IsInsideUpdateGameWorld"/> treats LbPhysics..WorldManagers as one
    /// contiguous block. Append new phases before Other, and keep the tag table in step.
    /// </summary>
    public enum WorldTickPhase
    {
        /// <summary>PlayerManager.Tick plus IpLimitManager.Tick (WorldManager.UpdateWorld).</summary>
        PlayerManager = 0,
        InboundMessages,
        ActionQueue,
        DelayManager,

        /// <summary>
        /// LandblockManager.TickPhysics, plus the short UpdateGameWorld prologue before it (rate limiter
        /// check, ServerPerformanceMonitor bookkeeping), because that is the time since the previous lap.
        /// </summary>
        LbPhysics,
        LbMultiThreaded,
        LbSingleThreaded,
        LbUnload,
        HouseManager,
        ThreadDungeons,

        /// <summary>FellowshipManager, WorldEventManager, MlDigsiteManager and AccountVaultManager ticks.</summary>
        WorldManagers,

        /// <summary>
        /// NetworkManager.DoSessionWork, plus (on an iteration where the world did NOT update) the
        /// rate-limited early return out of UpdateGameWorld, which is the time since the previous lap.
        /// </summary>
        SessionWork,

        /// <summary>Residual: time after the last lap and before EndIteration (ServerPerformanceMonitor.Tick).</summary>
        Other,
    }

    /// <summary>
    /// Phase names for <see cref="WorldTickPhase"/>. Each is the name segment of that phase's untagged
    /// instruments (ace.world.tick.phase.&lt;name&gt;.time / .duration, ServerMetrics), which the Grafana dashboard
    /// (deploy/monitoring/grafana/dashboards/ace-game-overview.json) turns back into a `phase` label with
    /// label_replace, and the ph_&lt;phase&gt; keys of the SLOW_TICK log line. Renaming one renames a metric.
    /// </summary>
    public static class WorldTickPhases
    {
        public const int Count = 13;

        public static readonly string[] TagNames =
        {
            "player_manager",
            "inbound_messages",
            "action_queue",
            "delay_manager",
            "lb_physics",
            "lb_multithreaded",
            "lb_singlethreaded",
            "lb_unload",
            "house_manager",
            "thread_dungeons",
            "world_managers",
            "session_work",
            "other",
        };

        /// <summary>True for the phases that run inside WorldManager.UpdateGameWorld.</summary>
        public static bool IsInsideUpdateGameWorld(WorldTickPhase phase)
        {
            return phase >= WorldTickPhase.LbPhysics && phase <= WorldTickPhase.WorldManagers;
        }
    }

    /// <summary>
    /// WaffleACE: pure lap timer for one world-loop iteration. No clock of its own and no statics: every call
    /// takes the timestamp, so it is testable with synthetic ticks.
    ///
    /// Time is accumulated in integer ticks, so the phase ticks of one iteration sum EXACTLY to
    /// End - Begin; the millisecond view is derived once, at End. Lap charges everything since the previous
    /// lap (or since Begin) to the named phase and may be called for the same phase more than once per
    /// iteration (the charges add). End charges the residual since the last lap to <see cref="WorldTickPhase.Other"/>.
    /// Allocation-free after construction.
    /// </summary>
    public sealed class TickPhaseAccumulator
    {
        private readonly double ticksPerMs;

        private readonly long[] phaseTicks = new long[WorldTickPhases.Count];

        private long beginTicks;
        private long lastTicks;
        private long endTicks;

        /// <summary>Per-phase milliseconds of the last completed iteration, indexed by <see cref="WorldTickPhase"/>. Valid after End.</summary>
        public readonly double[] PhaseMs = new double[WorldTickPhases.Count];

        /// <param name="ticksPerSecond">Timestamp frequency; Stopwatch.Frequency in production.</param>
        public TickPhaseAccumulator(long ticksPerSecond)
        {
            if (ticksPerSecond <= 0)
                throw new ArgumentOutOfRangeException(nameof(ticksPerSecond));

            ticksPerMs = ticksPerSecond / 1000.0;
        }

        /// <summary>End - Begin of the last completed iteration, in milliseconds.</summary>
        public double TotalMs { get; private set; }

        /// <summary>End - Begin of the last completed iteration, in raw ticks.</summary>
        public long TotalTicks => endTicks - beginTicks;

        public long GetPhaseTicks(WorldTickPhase phase) => phaseTicks[(int)phase];

        public void Begin(long timestamp)
        {
            Array.Clear(phaseTicks, 0, phaseTicks.Length);

            beginTicks = timestamp;
            lastTicks = timestamp;
            endTicks = timestamp;
        }

        public void Lap(WorldTickPhase phase, long timestamp)
        {
            phaseTicks[(int)phase] += timestamp - lastTicks;
            lastTicks = timestamp;
        }

        public void End(long timestamp)
        {
            phaseTicks[(int)WorldTickPhase.Other] += timestamp - lastTicks;
            lastTicks = timestamp;
            endTicks = timestamp;

            for (var i = 0; i < phaseTicks.Length; i++)
                PhaseMs[i] = phaseTicks[i] / ticksPerMs;

            TotalMs = (endTicks - beginTicks) / ticksPerMs;
        }

        /// <summary>Sum of the phases inside UpdateGameWorld, in milliseconds. Valid after End.</summary>
        public double WorldMs
        {
            get
            {
                long ticks = 0;

                for (var i = (int)WorldTickPhase.LbPhysics; i <= (int)WorldTickPhase.WorldManagers; i++)
                    ticks += phaseTicks[i];

                return ticks / ticksPerMs;
            }
        }
    }

    /// <summary>
    /// WaffleACE: world-thread facade over <see cref="TickPhaseAccumulator"/>, called from
    /// WorldManager.UpdateWorld and LandblockManager.Tick. Independent of ServerPerformanceMonitor, which
    /// records nothing while it is not running (prod ships ServerPerformanceMonitorAutoStart=false).
    ///
    /// Cost on every iteration: one Stopwatch.GetTimestamp and a thread-id compare per lap, plus three
    /// GC.CollectionCount reads, one GC.GetTotalPauseDuration and one Interlocked.Read of the weenie cache miss
    /// counter at BeginIteration. No allocation.
    ///
    /// Only the thread that called <see cref="BeginIteration"/> may lap: a call from any other thread is
    /// ignored. That makes a stray call from a Parallel.ForEach body, or from anything else that reaches
    /// LandblockManager.Tick off the world thread, harmless rather than corrupting.
    /// </summary>
    public static class WorldTickProfile
    {
        private static readonly TickPhaseAccumulator accumulator = new TickPhaseAccumulator(Stopwatch.Frequency);

        private static int worldThreadId = -1;

        private static bool active;

        /// <summary>
        /// Monotonic world-loop iteration number, incremented at BeginIteration. Written and read on the world
        /// thread only (Landblock.TickSingleThreadedWork stamps it, SlowTickReporter compares against it).
        /// </summary>
        public static long Iteration { get; private set; }

        internal static int Gc0AtBegin { get; private set; }
        internal static int Gc1AtBegin { get; private set; }
        internal static int Gc2AtBegin { get; private set; }
        internal static TimeSpan GcPauseAtBegin { get; private set; }

        /// <summary>
        /// WorldDatabaseWithEntityCache.WeenieCacheMissTotal at BeginIteration, for the SLOW_TICK weenie_miss field.
        /// That counter is PROCESS-WIDE (an Interlocked long, so safe to read here): the delta counts every cold
        /// weenie read taken during the iteration on any thread - the landblock threads' misses, which are the ones
        /// that stall the tick, but also a background warm-up's, which do not.
        /// </summary>
        internal static long WeenieMissAtBegin { get; private set; }

        /// <summary>The accumulator of the last completed iteration. World thread only.</summary>
        internal static TickPhaseAccumulator Accumulator => accumulator;

        /// <summary>
        /// True on the thread that last called <see cref="BeginIteration"/>. Unlike the guard inside
        /// <see cref="Lap"/> this does not require an iteration to be open, so it can gate work that runs
        /// inside a phase. False before the first BeginIteration, because no thread has claimed the role yet.
        /// </summary>
        public static bool IsWorldThread => worldThreadId != -1 && Environment.CurrentManagedThreadId == worldThreadId;

        public static void BeginIteration()
        {
            worldThreadId = Environment.CurrentManagedThreadId;

            Iteration++;

            Gc0AtBegin = GC.CollectionCount(0);
            Gc1AtBegin = GC.CollectionCount(1);
            Gc2AtBegin = GC.CollectionCount(2);
            GcPauseAtBegin = GC.GetTotalPauseDuration();
            WeenieMissAtBegin = ACE.Database.WorldDatabaseWithEntityCache.WeenieCacheMissTotal;

            accumulator.Begin(Stopwatch.GetTimestamp());

            active = true;
        }

        public static void Lap(WorldTickPhase phase)
        {
            if (!active || Environment.CurrentManagedThreadId != worldThreadId)
                return;

            accumulator.Lap(phase, Stopwatch.GetTimestamp());
        }

        /// <summary>Closes the iteration. Returns false when there was no open iteration on this thread.</summary>
        public static bool EndIteration()
        {
            if (!active || Environment.CurrentManagedThreadId != worldThreadId)
                return false;

            accumulator.End(Stopwatch.GetTimestamp());

            active = false;

            return true;
        }
    }
}
