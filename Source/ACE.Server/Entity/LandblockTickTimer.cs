using System;
using System.Diagnostics;

using ACE.Common.Performance;

namespace ACE.Server.Entity
{
    /// <summary>
    /// WaffleACE slow-tick capture: the stopwatched sections of one landblock tick. Each value names exactly
    /// the span one existing stopwatch in Landblock measures (the same readings that go to
    /// ServerPerformanceMonitor.AddToCumulativeEvent), plus physics, which is timed around TickPhysics's own
    /// body. The order is fixed: <see cref="LandblockTickSections.Names"/> is indexed by it.
    /// </summary>
    public enum LandblockTickSection
    {
        /// <summary>TickPhysics: pending additions/removals plus UpdateObjectPhysics over every object. Skipped while dormant.</summary>
        Physics,

        /// <summary>TickMultiThreadedWork: the landblock's own action queue (object creation, shard spawns, encounters, trades).</summary>
        RunActions,

        /// <summary>TickMultiThreadedWork: the Monster_Tick loop. Skipped while dormant.</summary>
        Monster,

        /// <summary>TickMultiThreadedWork: the GeneratorUpdate loop (each generator is due every 5 s).</summary>
        GenUpdate,

        /// <summary>TickMultiThreadedWork: the GeneratorRegeneration loop.</summary>
        GenRegen,

        /// <summary>
        /// TickMultiThreadedWork: the LANDBLOCK's own heartbeat block - decay of every decayable object, the
        /// dormancy decision and the unload-queue check. Runs its body only when heartbeatInterval has elapsed.
        /// Not to be confused with <see cref="WoHeartbeat"/>.
        /// </summary>
        LbHeartbeat,

        /// <summary>TickMultiThreadedWork: the periodic SaveDB of the landblock's objects.</summary>
        DbSave,

        /// <summary>TickSingleThreadedWork: Player_Tick over every player on the landblock.</summary>
        PlayerTick,

        /// <summary>TickSingleThreadedWork: the per-OBJECT WorldObject.Heartbeat loop over objects that are due.</summary>
        WoHeartbeat,
    }

    public static class LandblockTickSections
    {
        public const int Count = 9;

        /// <summary>
        /// The logfmt value for each <see cref="LandblockTickSection"/>, indexed by it. Fixed, short, lowercase
        /// identifiers, so the SLOW_TICK lbN_s1/lbN_s2 values have a closed, low cardinality.
        /// </summary>
        public static readonly string[] Names =
        {
            "physics",
            "run_actions",
            "monster",
            "gen_update",
            "gen_regen",
            "lb_heartbeat",
            "db_save",
            "player_tick",
            "wo_heartbeat",
        };

        /// <summary>
        /// A section below this is treated as zero when choosing the top two. The line renders ms with two
        /// decimals, so anything smaller would print as 0 and name a section that did not measurably run -
        /// every loop's stopwatch reads a few hundred nanoseconds even when the loop body never executes.
        /// </summary>
        public const double MinReportableMs = 0.005;

        /// <summary>
        /// The two largest reportable sections of <paramref name="ms"/>, largest first; -1 for a slot with no
        /// reportable section. A tie keeps the lower index. Allocation-free.
        /// </summary>
        public static void TopTwo(double[] ms, out int s1, out double s1Ms, out int s2, out double s2Ms)
        {
            s1 = -1;
            s1Ms = 0;
            s2 = -1;
            s2Ms = 0;

            for (var i = 0; i < Count; i++)
            {
                var v = ms[i];

                if (v < MinReportableMs)
                    continue;

                if (s1 < 0 || v > s1Ms)
                {
                    s2 = s1;
                    s2Ms = s1Ms;
                    s1 = i;
                    s1Ms = v;
                }
                else if (s2 < 0 || v > s2Ms)
                {
                    s2 = i;
                    s2Ms = v;
                }
            }
        }
    }

    /// <summary>
    /// WaffleACE: one landblock's per-tick timing - the event span its RateMonitors record, and the
    /// per-section breakdown the SLOW_TICK line reports. Extracted from Landblock so the start/stop decision is
    /// testable without constructing a Landblock (which needs dat files).
    ///
    /// The event span. One landblock tick is up to three calls in ONE world-loop iteration: TickPhysics (not
    /// while dormant), TickMultiThreadedWork, TickSingleThreadedWork. The event must span all of the ones that
    /// ran. The first Begin* call of an iteration opens the event (Restart); later ones of the same iteration
    /// Resume it; EndSingleThreaded closes it. Before this class the "is an event open" flag was cleared only by
    /// TickPhysics, which returns early while dormant - so for a dormant landblock TickSingleThreadedWork
    /// Restarted the monitors a second time and threw away the whole multi-threaded span (action queue, both
    /// generator loops, heartbeat, SaveDB). The flag is now set by whichever call opens the event.
    ///
    /// The opening iteration is recorded, so an event left open by a tick that threw before
    /// EndSingleThreaded can never be resumed by a later iteration: its time, and its sections, are dropped
    /// rather than carried over.
    ///
    /// Sections: <see cref="AddSection"/> accumulates into a working array that is zeroed when an event opens
    /// and copied to <see cref="LastSectionMs"/> when it closes - the same point Landblock stamps
    /// LastTickMs/LastTickIteration - so the reported sections are exactly those measured in the iteration
    /// that was stamped. Fixed-size arrays; nothing here allocates.
    ///
    /// Threading: TickPhysics and TickMultiThreadedWork may run on a landblock-group worker thread and
    /// TickSingleThreadedWork on the world thread, but never concurrently for one landblock; the
    /// Parallel.ForEach joins between those phases order the writes.
    /// </summary>
    internal sealed class LandblockTickTimer
    {
        public readonly RateMonitor Monitor5m = new RateMonitor();
        public readonly RateMonitor Monitor1h = new RateMonitor();

        private static readonly double msPerTick = 1000.0 / Stopwatch.Frequency;

        private readonly double[] currentMs = new double[LandblockTickSections.Count];

        /// <summary>The sections of the last CLOSED event, in ms, indexed by <see cref="LandblockTickSection"/>.</summary>
        public readonly double[] LastSectionMs = new double[LandblockTickSections.Count];

        private bool eventOpen;
        private long eventIteration;
        private long physicsStart;

        /// <summary>Always opens a fresh event: physics, when it runs, is the first call of the iteration.</summary>
        public void BeginPhysics(long iteration)
        {
            OpenEvent(iteration);

            physicsStart = Stopwatch.GetTimestamp();
        }

        public void EndPhysics()
        {
            currentMs[(int)LandblockTickSection.Physics] += (Stopwatch.GetTimestamp() - physicsStart) * msPerTick;

            Pause();
        }

        public void BeginMultiThreaded(long iteration) => OpenOrResume(iteration);

        public void EndMultiThreaded() => Pause();

        public void BeginSingleThreaded(long iteration) => OpenOrResume(iteration);

        /// <summary>
        /// Closes the event: both monitors register it, and the sections measured since it opened become
        /// <see cref="LastSectionMs"/>.
        /// </summary>
        public void EndSingleThreaded()
        {
            Monitor5m.RegisterEventEnd();
            Monitor1h.RegisterEventEnd();

            eventOpen = false;

            Array.Copy(currentMs, LastSectionMs, LandblockTickSections.Count);
        }

        /// <summary>Adds one stopwatch reading (seconds, as ServerPerformanceMonitor takes it) to a section.</summary>
        public void AddSection(LandblockTickSection section, double seconds)
        {
            currentMs[(int)section] += seconds * 1000.0;
        }

        private void OpenOrResume(long iteration)
        {
            if (eventOpen && eventIteration == iteration)
            {
                Monitor5m.Resume();
                Monitor1h.Resume();
            }
            else
            {
                OpenEvent(iteration);
            }
        }

        private void OpenEvent(long iteration)
        {
            Monitor5m.Restart();
            Monitor1h.Restart();

            eventOpen = true;
            eventIteration = iteration;

            Array.Clear(currentMs, 0, LandblockTickSections.Count);
        }

        private void Pause()
        {
            Monitor5m.Pause();
            Monitor1h.Pause();
        }
    }
}
