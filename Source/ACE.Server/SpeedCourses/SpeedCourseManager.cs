using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

using log4net;

using ACE.Database.Models.World;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldEvents;
using ACE.Server.WorldObjects;

namespace ACE.Server.SpeedCourses
{
    /// <summary>
    /// The registry of live speed-course runs, one per instance. <see cref="Register"/> is called by Portal.ActOnUse
    /// right after the speed branch arms the challenge; a season with no course in <see cref="Store"/> is a strict
    /// no-op, so a season without a course (the Lost Coastal Archive) behaves exactly as before.
    /// <see cref="Tick"/> runs on the world thread (WorldManager, next to ThreadDungeonManager.Tick) every heartbeat,
    /// throttles itself to <see cref="TickSeconds"/>, ends any run whose instance landblock went away, and enqueues
    /// each live run's tick onto its own landblock, where every world mutation happens.
    /// </summary>
    public static class SpeedCourseManager
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>The run tick and Seal poll cadence.</summary>
        public const double TickSeconds = 0.5;

        /// <summary>How long a run's instance may be missing from the ephemeral registry before it was ever seen there.</summary>
        public const double LandblockRegisterGraceSeconds = 60.0;

        public static SpeedCourseStore Store { get; private set; } = SpeedCourseStore.Empty;

        /// <summary>
        /// The hazard driver each new run gets (read at registration). Default: <see cref="DefaultHazardDriver"/>.
        /// </summary>
        public static Func<SpeedCourseRun, ISpeedHazardDriver> HazardDriverFactory = DefaultHazardDriver;

        /// <summary>
        /// A <see cref="SpeedCourseHazardHost"/> over HazardManager on the run's instance landblock for a course that
        /// declares hazards; the no-op driver for one that declares none (or a run not on a live world).
        /// </summary>
        public static ISpeedHazardDriver DefaultHazardDriver(SpeedCourseRun run)
        {
            if (run?.Course?.Hazards == null || run.Course.Hazards.Count == 0)
                return NoopSpeedHazardDriver.Instance;

            if (run.World is not LiveSpeedCourseWorld live)
                return NoopSpeedHazardDriver.Instance;

            return new SpeedCourseHazardHost(run, new LiveSpeedHazardPort(live.Landblock));
        }

        private sealed class Entry
        {
            public SpeedCourseRun Run;
            public Landblock Landblock;
            public double RegisteredAt;
            public bool SeenLive;
            public int TickQueued;
        }

        private static readonly ConcurrentDictionary<uint, Entry> runs = new ConcurrentDictionary<uint, Entry>();

        private static readonly Stopwatch clock = Stopwatch.StartNew();
        private static double nextTickAt;

        /// <summary>Seconds on the manager's monotonic clock.</summary>
        public static double Now() => clock.Elapsed.TotalSeconds;

        public static int RunCount => runs.Count;

        // ================================================================ store

        /// <summary>Boot: loads speed-courses.json, cross-checked against the Threads store and species tables (Program, after ThreadDungeonManager).</summary>
        public static void Initialize()
        {
            try
            {
                Store = SpeedCourseStore.LoadFromServerConfig(ThreadDungeonManager.Store, WorldEventHooks.Store?.SpeciesTables);
            }
            catch (Exception ex)
            {
                log.Error("[SPEEDCOURSE] loading speed courses threw; no speed courses this boot", ex);
                Store = SpeedCourseStore.Empty;
            }
        }

        /// <summary>/reloadspeedseasons: re-reads the course file. Live runs keep the course they were registered with.</summary>
        public static SpeedCourseStore Reload()
        {
            Initialize();
            return Store;
        }

        /// <summary>Test seam: replaces the store.</summary>
        internal static void SetStoreForTests(SpeedCourseStore store) => Store = store ?? SpeedCourseStore.Empty;

        // ================================================================ registry

        /// <summary>
        /// Registers a course run for <paramref name="player"/>'s new season instance. Never throws. A season with no
        /// course is a no-op. Ends any earlier run of the same runner (they are entering a new instance).
        /// </summary>
        public static void Register(Player player, SpeedSeason season, Landblock instanceLandblock)
        {
            try
            {
                if (player == null || season == null || instanceLandblock == null)
                    return;

                var course = Store?.GetCourse(season.Id);

                if (course == null)
                    return;

                foreach (var other in runs.Values)
                    if (other.Run.OwnerGuid == player.Guid.Full && other.Run.Instance != instanceLandblock.Instance)
                        other.Run.End("runner entered a new instance");

                var instance = instanceLandblock.Instance;
                int runSeed;

                unchecked
                {
                    runSeed = (int)(instance * 2654435761u) ^ Environment.TickCount;
                }

                var world = new LiveSpeedCourseWorld(instanceLandblock, player.Guid.Full);
                var run = new SpeedCourseRun(course, season.Id, season.LevelFloor, season.ObjectiveWcid, player.Level ?? 1, player.Guid.Full, runSeed, world, Now());

                // The driver is built against the run (it reads the run's course and level), then attached before the first tick.
                run.AttachHazardDriver(SafeDriver(run));

                var entry = new Entry { Run = run, Landblock = instanceLandblock, RegisteredAt = Now() };
                run.Ended = r => runs.TryRemove(new System.Collections.Generic.KeyValuePair<uint, Entry>(r.Instance, entry));

                if (runs.TryGetValue(instance, out var previous))
                    previous.Run.End("instance re-registered");

                runs[instance] = entry;

                log.Info($"[SPEEDCOURSE] {run} registered for {player.Name} (0x{player.Guid.Full:X8}, level {player.Level ?? 1}) dungeon={course.DungeonId} family={course.Family} seed={runSeed}");
            }
            catch (Exception ex)
            {
                log.Error($"[SPEEDCOURSE] Register threw for {player?.Name}; the course is not run in this instance", ex);
            }
        }

        private static ISpeedHazardDriver SafeDriver(SpeedCourseRun run)
        {
            try
            {
                return HazardDriverFactory?.Invoke(run) ?? NoopSpeedHazardDriver.Instance;
            }
            catch (Exception ex)
            {
                log.Error($"[SPEEDCOURSE] {run} hazard driver factory threw; the run has no hazards", ex);
                return NoopSpeedHazardDriver.Instance;
            }
        }

        /// <summary>The live run in <paramref name="instance"/>, or null.</summary>
        public static SpeedCourseRun GetRun(uint instance) => runs.TryGetValue(instance, out var e) ? e.Run : null;

        /// <summary>
        /// True when a contribution to objective lock <paramref name="key"/> in <paramref name="landblock"/> belongs to a
        /// live course run's Seal: the run tells the runner its own Seal lines, so the generic objective-lock chat is
        /// suppressed for that key only (WorldObject.ContributeToObjectiveLock). Every other gate in the instance (the
        /// start lever's stub door) keeps its own line.
        /// </summary>
        public static bool OwnsObjectiveChat(Landblock landblock, string key)
            => landblock != null && OwnsObjectiveChat(landblock.Instance, key);

        /// <summary>The predicate behind <see cref="OwnsObjectiveChat(Landblock, string)"/>, over (instance, key).</summary>
        public static bool OwnsObjectiveChat(uint instance, string key)
            => instance != 0 && !string.IsNullOrEmpty(key)
               && GetRun(instance) is SpeedCourseRun run && !run.IsEnded
               && string.Equals(run.Course?.SealKey, key, StringComparison.Ordinal);

        /// <summary>Test seam: puts <paramref name="run"/> in the registry for <paramref name="instance"/> (null removes it).</summary>
        internal static void SetRunForTests(uint instance, SpeedCourseRun run)
        {
            if (run == null)
                runs.TryRemove(instance, out _);
            else
                runs[instance] = new Entry { Run = run, RegisteredAt = Now() };
        }

        // ================================================================ tick (world thread)

        public static void Tick()
        {
            var now = Now();

            if (now < nextTickAt)
                return;

            nextTickAt = now + TickSeconds;

            foreach (var entry in runs.Values)
            {
                var run = entry.Run;

                try
                {
                    if (run.IsEnded)
                    {
                        runs.TryRemove(new System.Collections.Generic.KeyValuePair<uint, Entry>(run.Instance, entry));
                        continue;
                    }

                    var live = LandblockManager.GetEphemeralLandblock(run.Instance);

                    if (ReferenceEquals(live, entry.Landblock))
                    {
                        entry.SeenLive = true;
                    }
                    else if (entry.SeenLive || now - entry.RegisteredAt > LandblockRegisterGraceSeconds)
                    {
                        // World thread: End touches only the puzzle registry, the driver's Stop and the flag.
                        run.End("instance landblock gone");
                        continue;
                    }
                    else
                    {
                        continue;
                    }

                    if (Interlocked.CompareExchange(ref entry.TickQueued, 1, 0) != 0)
                        continue;

                    var tickAt = now;

                    entry.Landblock.EnqueueAction(new ActionEventDelegate(() =>
                    {
                        try
                        {
                            run.Tick(tickAt);
                        }
                        catch (Exception ex)
                        {
                            log.Error($"[SPEEDCOURSE] {run} tick threw", ex);
                        }
                        finally
                        {
                            Interlocked.Exchange(ref entry.TickQueued, 0);
                        }
                    }));
                }
                catch (Exception ex)
                {
                    Interlocked.Exchange(ref entry.TickQueued, 0);
                    log.Error($"[SPEEDCOURSE] {run} manager tick threw", ex);
                }
            }
        }
    }
}
