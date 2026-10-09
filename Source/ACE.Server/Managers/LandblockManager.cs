using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using ACE.Common;
using ACE.Common.Performance;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Realms;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.Managers
{
    /// <summary>
    /// Handles loading/unloading landblocks, and their adjacencies
    /// </summary>
    public static class LandblockManager
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// Locking mechanism provides concurrent access to collections
        /// </summary>
        private static readonly ReaderWriterLockSlim landblockLock = new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);

        /// <summary>
        /// A table of all the loaded landblocks, keyed by the 64-bit (instance, landblock) pair.
        /// Reading and writing must go through LandblockDictFetch and LandblockDictCommit.
        /// </summary>
        private static readonly Dictionary<ulong, Landblock> landblocks = new Dictionary<ulong, Landblock>();

        /// <summary>
        /// Live ephemeral instances, keyed by their full 32-bit instance id. An entry exists exactly while
        /// its landblock is loaded.
        ///
        /// <para/>
        /// Deliberately NOT protected by <see cref="landblockLock"/>. It used to be a plain Dictionary
        /// written under that lock's write lock and read with no lock at all by
        /// <see cref="GetEphemeralLandblock"/>, which could miss a key that was present while another thread
        /// resized it - and a miss silently relocated the player out of their private instance (see
        /// <see cref="EphemeralInstanceRegistry{T}"/> for the full history). The registry is internally
        /// concurrent, so it may be read and written from any thread with no lock held and no acquisition
        /// order to honour against <see cref="landblockLock"/>.
        /// </summary>
        private static readonly EphemeralInstanceRegistry<Landblock> ephemeralInstances = new EphemeralInstanceRegistry<Landblock>();

        /// <summary>
        /// Composes the 64-bit landblock dictionary key: (instance &lt;&lt; 32) | (landblock | 0xFFFF).
        /// The cell portion of the raw id is normalized away so any cell within a landblock maps to the same key.
        /// </summary>
        internal static ulong LandblockKey(uint rawLandblockId, uint instance)
        {
            return ((ulong)instance << 32) | (rawLandblockId | 0xFFFF);
        }

        private static Landblock LandblockDictFetch(uint rawLandblockId, uint instance)
        {
            landblocks.TryGetValue(LandblockKey(rawLandblockId, instance), out var landblock);
            return landblock;
        }

        private static void LandblockDictCommit(uint rawLandblockId, uint instance, Landblock landblock)
        {
            var key = LandblockKey(rawLandblockId, instance);

            var curr = LandblockDictFetch(rawLandblockId, instance);
            if (landblock != null && curr != null)
                throw new InvalidOperationException($"Attempted to overwrite live landblock {key:X16} (possible thread safety issue)");
            if (landblock == null && curr == null)
                throw new InvalidOperationException($"Attempted to clear unloaded landblock {key:X16} (possible thread safety issue)");

            if (landblock == null)
                landblocks.Remove(key);
            else
                landblocks[key] = landblock;
        }

        /// <summary>
        /// A lookup table of all the currently loaded landblocks
        /// </summary>
        private static readonly HashSet<Landblock> loadedLandblocks = new HashSet<Landblock>();

        private static readonly List<Landblock> landblockGroupPendingAdditions = new List<Landblock>();
        private static readonly List<LandblockGroup> landblockGroups = new List<LandblockGroup>();

        public static int LandblockGroupsCount
        {
            get
            {
                landblockLock.EnterReadLock();
                try
                {
                    return landblockGroups.Count;
                }
                finally
                {
                    landblockLock.ExitReadLock();
                }
            }
        }

        public static List<LandblockGroup> GetLoadedLandblockGroups()
        {
            landblockLock.EnterReadLock();
            try
            {
                return landblockGroups.ToList();
            }
            finally
            {
                landblockLock.ExitReadLock();
            }
        }

        /// <summary>
        /// DestructionQueue is concurrent because it can be added to by multiple threads at once, publicly via AddToDestructionQueue()
        /// </summary>
        private static readonly ConcurrentBag<Landblock> destructionQueue = new ConcurrentBag<Landblock>();

        /// <summary>
        /// Permaloads a list of configurable landblocks if server option is set
        /// </summary>
        public static void PreloadConfigLandblocks()
        {
            if (!ConfigManager.Config.Server.LandblockPreloading)
            {
                log.Info("Preloading Landblocks Disabled...");
                log.Warn("Events may not function correctly as Preloading of Landblocks has disabled.");
                return;
            }

            log.Info("Preloading Landblocks...");

            if (ConfigManager.Config.Server.PreloadedLandblocks == null)
            {
                log.Info("No configuration found for PreloadedLandblocks, please refer to Config.js.example");
                log.Warn("Initializing PreloadedLandblocks with single default for Hebian-To (Global Events)");
                log.Warn("Add a PreloadedLandblocks section to your Config.js file and adjust to meet your needs");
                ConfigManager.Config.Server.PreloadedLandblocks = new List<PreloadedLandblocks> { new PreloadedLandblocks { Id = "E74EFFFF", Description = "Hebian-To (Global Events)", Permaload = true, IncludeAdjacents = true, Enabled = true } };
            }

            log.InfoFormat("Found {0} landblock entries in PreloadedLandblocks configuration, {1} are set to preload.", ConfigManager.Config.Server.PreloadedLandblocks.Count, ConfigManager.Config.Server.PreloadedLandblocks.Count(x => x.Enabled == true));

            foreach (var preloadLandblock in ConfigManager.Config.Server.PreloadedLandblocks)
            {
                if (!preloadLandblock.Enabled)
                {
                    log.DebugFormat("Landblock {0:X4} specified but not enabled in config, skipping", preloadLandblock.Id);
                    continue;
                }

                if (uint.TryParse(preloadLandblock.Id, NumberStyles.HexNumber, CultureInfo.CurrentCulture, out uint landblock))
                {
                    if (landblock == 0)
                    {
                        switch (preloadLandblock.Description)
                        {
                            case "Apartment Landblocks":
                                log.InfoFormat("Preloading landblock group: {0}, IncludeAdjacents = {1}, Permaload = {2}", preloadLandblock.Description, preloadLandblock.IncludeAdjacents, preloadLandblock.Permaload);
                                foreach (var apt in apartmentLandblocks)
                                    PreloadLandblock(apt, preloadLandblock);
                                break;
                        }
                    }
                    else
                        PreloadLandblock(landblock, preloadLandblock);
                }
            }
        }

        private static void PreloadLandblock(uint landblock, PreloadedLandblocks preloadLandblock)
        {
            var landblockID = new LandblockId(landblock);

            // Realms: the config's default Realm of 0 resolves to instance 0, so every
            // pre-existing entry preloads the base world exactly as it did before
            var instance = ACE.Entity.Position.InstanceIDFromVars(preloadLandblock.Realm, 0, false);

            GetLandblock(landblockID, instance, preloadLandblock.IncludeAdjacents, preloadLandblock.Permaload);
            log.DebugFormat("Landblock {0:X4}, ({1}) preloaded in realm {2} (instance 0x{3:X8}). IncludeAdjacents = {4}, Permaload = {5}", landblockID.Landblock, preloadLandblock.Description, preloadLandblock.Realm, instance, preloadLandblock.IncludeAdjacents, preloadLandblock.Permaload);
        }

        private static readonly uint[] apartmentLandblocks =
        {
            0x7200FFFF,
            0x7300FFFF,
            0x7400FFFF,
            0x7500FFFF,
            0x7600FFFF,
            0x7700FFFF,
            0x7800FFFF,
            0x7900FFFF,
            0x7A00FFFF,
            0x7B00FFFF,
            0x7C00FFFF,
            0x7D00FFFF,
            0x7E00FFFF,
            0x7F00FFFF,
            0x8000FFFF,
            0x8100FFFF,
            0x8200FFFF,
            0x8300FFFF,
            0x8400FFFF,
            0x8500FFFF,
            0x8600FFFF,
            0x8700FFFF,
            0x8800FFFF,
            0x8900FFFF,
            0x8A00FFFF,
            0x8B00FFFF,
            0x8C00FFFF,
            0x8D00FFFF,
            0x8E00FFFF,
            0x8F00FFFF,
            0x9000FFFF,
            0x9100FFFF,
            0x9200FFFF,
            0x9300FFFF,
            0x9400FFFF,
            0x9500FFFF,
            0x9600FFFF,
            0x9700FFFF,
            0x9800FFFF,
            0x9900FFFF,
            0x5360FFFF,
            0x5361FFFF,
            0x5362FFFF,
            0x5363FFFF,
            0x5364FFFF,
            0x5365FFFF,
            0x5366FFFF,
            0x5367FFFF,
            0x5368FFFF,
            0x5369FFFF
        };

        private static void ProcessPendingLandblockGroupAdditions()
        {
            if (landblockGroupPendingAdditions.Count == 0)
                return;

            landblockLock.EnterWriteLock();
            try
            {
                for (int i = landblockGroupPendingAdditions.Count - 1; i >= 0; i--)
                {
                    if (landblockGroupPendingAdditions[i].IsDungeon)
                    {
                        // Each dungeon exists in its own group
                        var landblockGroup = new LandblockGroup(landblockGroupPendingAdditions[i]);
                        landblockGroups.Add(landblockGroup);
                    }
                    else
                    {
                        // Find out how many groups this landblock is eligible for
                        var landblockGroupsIndexMatchesByDistance = new List<int>();

                        for (int j = 0; j < landblockGroups.Count; j++)
                        {
                            if (landblockGroups[j].IsDungeon)
                                continue;

                            if (landblockGroups[j].ShouldBeAddedToThisLandblockGroup(landblockGroupPendingAdditions[i]))
                                landblockGroupsIndexMatchesByDistance.Add(j);
                        }

                        if (landblockGroupsIndexMatchesByDistance.Count > 0)
                        {
                            // Add the landblock to the first eligible group
                            landblockGroups[landblockGroupsIndexMatchesByDistance[0]].Add(landblockGroupPendingAdditions[i]);

                            if (landblockGroupsIndexMatchesByDistance.Count > 1)
                            {
                                // Merge the additional eligible groups into the first one
                                for (int j = landblockGroupsIndexMatchesByDistance.Count - 1; j > 0; j--)
                                {
                                    // Copy the j down into 0
                                    foreach (var landblock in landblockGroups[landblockGroupsIndexMatchesByDistance[j]])
                                        landblockGroups[landblockGroupsIndexMatchesByDistance[0]].Add(landblock);

                                    landblockGroups.RemoveAt(landblockGroupsIndexMatchesByDistance[j]);
                                }
                            }
                        }
                        else
                        {
                            // No close groups were found
                            var landblockGroup = new LandblockGroup(landblockGroupPendingAdditions[i]);
                            landblockGroups.Add(landblockGroup);
                        }
                    }

                    landblockGroupPendingAdditions.RemoveAt(i);
                }

                // Debugging todo: comment this out after enough testing
                var count = 0;
                foreach (var group in landblockGroups)
                    count += group.Count;
                if (count != loadedLandblocks.Count)
                    log.Error($"[LANDBLOCK GROUP] ProcessPendingAdditions count ({count}) != loadedLandblocks.Count ({loadedLandblocks.Count})");
            }
            finally
            {
                landblockLock.ExitWriteLock();
            }
        }

        public static void Tick(double portalYearTicks)
        {
            // update positions through physics engine
            ServerPerformanceMonitor.RestartEvent(ServerPerformanceMonitor.MonitorType.LandblockManager_TickPhysics);
            TickPhysics(portalYearTicks);
            ServerPerformanceMonitor.RegisterEventEnd(ServerPerformanceMonitor.MonitorType.LandblockManager_TickPhysics);
            WorldTickProfile.Lap(WorldTickPhase.LbPhysics);

            // Tick all of our Landblocks and WorldObjects (Work that can be multi-threaded)
            ServerPerformanceMonitor.RestartEvent(ServerPerformanceMonitor.MonitorType.LandblockManager_TickMultiThreadedWork);
            TickMultiThreadedWork();
            ServerPerformanceMonitor.RegisterEventEnd(ServerPerformanceMonitor.MonitorType.LandblockManager_TickMultiThreadedWork);
            WorldTickProfile.Lap(WorldTickPhase.LbMultiThreaded);

            // Tick all of our Landblocks and WorldObjects (Work that must be single threaded)
            ServerPerformanceMonitor.RestartEvent(ServerPerformanceMonitor.MonitorType.LandblockManager_TickSingleThreadedWork);
            TickSingleThreadedWork();
            ServerPerformanceMonitor.RegisterEventEnd(ServerPerformanceMonitor.MonitorType.LandblockManager_TickSingleThreadedWork);
            WorldTickProfile.Lap(WorldTickPhase.LbSingleThreaded);

            // clean up inactive landblocks
            UnloadLandblocks();
            WorldTickProfile.Lap(WorldTickPhase.LbUnload);
        }

        /// <summary>
        /// Used to debug cross-landblock group (and potentially cross-thread) operations
        /// </summary>
        public static bool CurrentlyTickingLandblockGroupsMultiThreaded { get; private set; }

        /// <summary>
        /// Used to debug cross-landblock group (and potentially cross-thread) operations
        /// </summary>
        public static readonly ThreadLocal<LandblockGroup> CurrentMultiThreadedTickingLandblockGroup = new ThreadLocal<LandblockGroup>();

        public static readonly RollingAmountOverTimeTracker TickPhysicsEfficiencyTracker = new RollingAmountOverTimeTracker(TimeSpan.FromMinutes(1));
        public static readonly RollingAmountOverTimeTracker TickMultiThreadedWorkEfficiencyTracker = new RollingAmountOverTimeTracker(TimeSpan.FromMinutes(1));

        /// <summary>
        /// Processes physics objects in all active landblocks for updating
        /// </summary>
        private static void TickPhysics(double portalYearTicks)
        {
            ProcessPendingLandblockGroupAdditions();

            var movedObjects = new ConcurrentBag<WorldObject>();

            if (ConfigManager.Config.Server.Threading.MultiThreadedLandblockGroupPhysicsTicking)
            {
                CurrentlyTickingLandblockGroupsMultiThreaded = true;

                try
                {
                    var partitioner = Partitioner.Create(landblockGroups.OrderByDescending(r => r.Count).ThenByDescending(r => r.TickPhysicsTracker.AverageAmount));

                    var sw = new Stopwatch();
                    sw.Start();

                    Parallel.ForEach(partitioner, ConfigManager.Config.Server.Threading.LandblockManagerParallelOptions, landblockGroup =>
                    {
                        CurrentMultiThreadedTickingLandblockGroup.Value = landblockGroup;

                        try
                        {
                            var swInner = new Stopwatch();
                            swInner.Start();

                            foreach (var landblock in landblockGroup)
                            {
                                // Coarse safety net, intentionally redundant with the finer per-object catches
                                // inside Landblock: one bad landblock must degrade loudly instead of an
                                // AggregateException escaping to WorldManager's fatal handler and stopping the
                                // world for everyone.
                                try
                                {
                                    landblock.TickPhysics(portalYearTicks, movedObjects);
                                }
                                catch (Exception ex)
                                {
                                    log.Error($"LandblockManager.TickPhysics(): landblock 0x{landblock.Id} threw and was skipped this tick", ex);
                                }
                            }

                            swInner.Stop();
                            landblockGroup.TickPhysicsTracker.RegisterAmount(swInner.Elapsed.TotalSeconds);
                        }
                        catch (Exception ex)
                        {
                            // Covers the per-group bookkeeping AFTER the landblock foreach (RegisterAmount and
                            // the stopwatch), which the per-landblock catch above does not - a throw here would
                            // otherwise surface as an AggregateException out of Parallel.ForEach and defeat the
                            // "one bad landblock degrades loudly" goal for the whole group.
                            log.Error($"LandblockManager.TickPhysics(): landblock group tick bookkeeping threw and was skipped this tick", ex);
                        }
                        finally
                        {
                            // this is a ThreadLocal on a pooled thread - a stranded value would be read by
                            // whatever runs on that thread next
                            CurrentMultiThreadedTickingLandblockGroup.Value = null;
                        }
                    });

                    sw.Stop();
                    // Calculate Tick Efficiency
                    if (landblockGroups.Count > 0)
                    {
                        var totalSecondsUsedInParallel = landblockGroups.Sum(r => r.TickPhysicsTracker.LastAmount);
                        var totalThreadsUsed = Math.Min(landblockGroups.Count, ConfigManager.Config.Server.Threading.LandblockManagerParallelOptions.MaxDegreeOfParallelism);
                        var efficiency = (totalSecondsUsedInParallel / (sw.Elapsed.TotalSeconds * totalThreadsUsed)) * 100;
                        TickPhysicsEfficiencyTracker.RegisterAmount(efficiency);
                    }
                }
                finally
                {
                    // must be cleared even if the parallel loop throws, or every later reader of this flag
                    // sees a multi-threaded tick that is not running
                    CurrentlyTickingLandblockGroupsMultiThreaded = false;
                }
            }
            else
            {
                foreach (var landblockGroup in landblockGroups)
                {
                    foreach (var landblock in landblockGroup)
                    {
                        try
                        {
                            landblock.TickPhysics(portalYearTicks, movedObjects);
                        }
                        catch (Exception ex)
                        {
                            log.Error($"LandblockManager.TickPhysics(): landblock 0x{landblock.Id} threw and was skipped this tick", ex);
                        }
                    }
                }
            }

            // iterate through objects that have changed landblocks
            foreach (var movedObject in movedObjects)
            {
                // NOTE: The object's Location can now be null, if a player logs out, or an item is picked up
                if (movedObject.Location == null)
                    continue;

                // assume adjacency move here?
                RelocateObjectForPhysics(movedObject, true);
            }
        }

        private static void TickMultiThreadedWork()
        {
            ProcessPendingLandblockGroupAdditions();

            if (ConfigManager.Config.Server.Threading.MultiThreadedLandblockGroupTicking)
            {
                CurrentlyTickingLandblockGroupsMultiThreaded = true;

                try
                {
                    var partitioner = Partitioner.Create(landblockGroups.OrderByDescending(r => r.Count).ThenByDescending(r => r.TickMultiThreadedWorkTracker.AverageAmount));

                    var sw = new Stopwatch();
                    sw.Start();

                    Parallel.ForEach(partitioner, ConfigManager.Config.Server.Threading.LandblockManagerParallelOptions, landblockGroup =>
                    {
                        CurrentMultiThreadedTickingLandblockGroup.Value = landblockGroup;

                        try
                        {
                            var swInner = new Stopwatch();
                            swInner.Start();

                            foreach (var landblock in landblockGroup)
                            {
                                // Coarse safety net, intentionally redundant with the finer per-object catches
                                // inside Landblock: one bad landblock must degrade loudly instead of an
                                // AggregateException escaping to WorldManager's fatal handler and stopping the
                                // world for everyone.
                                try
                                {
                                    landblock.TickMultiThreadedWork(Time.GetUnixTime());
                                }
                                catch (Exception ex)
                                {
                                    log.Error($"LandblockManager.TickMultiThreadedWork(): landblock 0x{landblock.Id} threw and was skipped this tick", ex);
                                }
                            }

                            swInner.Stop();
                            landblockGroup.TickMultiThreadedWorkTracker.RegisterAmount(swInner.Elapsed.TotalSeconds);
                        }
                        catch (Exception ex)
                        {
                            // Covers the per-group bookkeeping AFTER the landblock foreach (RegisterAmount and
                            // the stopwatch), which the per-landblock catch above does not - a throw here would
                            // otherwise surface as an AggregateException out of Parallel.ForEach and defeat the
                            // "one bad landblock degrades loudly" goal for the whole group.
                            log.Error($"LandblockManager.TickMultiThreadedWork(): landblock group tick bookkeeping threw and was skipped this tick", ex);
                        }
                        finally
                        {
                            // this is a ThreadLocal on a pooled thread - a stranded value would be read by
                            // whatever runs on that thread next
                            CurrentMultiThreadedTickingLandblockGroup.Value = null;
                        }
                    });

                    sw.Stop();
                    // Calculate Tick Efficiency
                    if (landblockGroups.Count > 0)
                    {
                        var totalSecondsUsedInParallel = landblockGroups.Sum(r => r.TickMultiThreadedWorkTracker.LastAmount);
                        var totalThreadsUsed = Math.Min(landblockGroups.Count, ConfigManager.Config.Server.Threading.LandblockManagerParallelOptions.MaxDegreeOfParallelism);
                        var efficiency = (totalSecondsUsedInParallel / (sw.Elapsed.TotalSeconds * totalThreadsUsed)) * 100;
                        TickMultiThreadedWorkEfficiencyTracker.RegisterAmount(efficiency);
                    }
                }
                finally
                {
                    // must be cleared even if the parallel loop throws, or every later reader of this flag
                    // sees a multi-threaded tick that is not running
                    CurrentlyTickingLandblockGroupsMultiThreaded = false;
                }
            }
            else
            {
                foreach (var landblockGroup in landblockGroups)
                {
                    foreach (var landblock in landblockGroup)
                    {
                        try
                        {
                            landblock.TickMultiThreadedWork(Time.GetUnixTime());
                        }
                        catch (Exception ex)
                        {
                            log.Error($"LandblockManager.TickMultiThreadedWork(): landblock 0x{landblock.Id} threw and was skipped this tick", ex);
                        }
                    }
                }
            }
        }

        private static void TickSingleThreadedWork()
        {
            ProcessPendingLandblockGroupAdditions();

            foreach (var landblockGroup in landblockGroups)
            {
                foreach (var landblock in landblockGroup)
                {
                    // Coarse safety net, as in the two tick methods above: this runs directly on the world
                    // thread, so an escaping throw stops the world for every landblock, not just this one.
                    try
                    {
                        landblock.TickSingleThreadedWork(Time.GetUnixTime());
                    }
                    catch (Exception ex)
                    {
                        log.Error($"LandblockManager.TickSingleThreadedWork(): landblock 0x{landblock.Id} threw and was skipped this tick", ex);
                    }
                }
            }
        }

        /// <summary>
        /// Adds a WorldObject to the landblock defined by the object's location
        /// </summary>
        /// <param name="loadAdjacents">If TRUE, ensures all of the adjacent landblocks for this WorldObject are loaded</param>
        public static bool AddObject(WorldObject worldObject, bool loadAdjacents = false)
        {
            var block = GetLandblock(worldObject.Location.LandblockId, worldObject.Location.Instance, loadAdjacents);

            return block.AddWorldObject(worldObject);
        }

        /// <summary>
        /// Relocates an object to the appropriate landblock -- Should only be called from physics/worldmanager -- not player!
        /// </summary>
        public static void RelocateObjectForPhysics(WorldObject worldObject, bool adjacencyMove)
        {
            var oldBlock = worldObject.CurrentLandblock;
            var newBlock = GetLandblock(worldObject.Location.LandblockId, worldObject.Location.Instance, true);

            if (newBlock.IsDormant && worldObject is SpellProjectile)
            {
                worldObject.PhysicsObj.set_active(false);
                worldObject.Destroy();
                return;
            }

            // Remove from the old landblock -- force
            if (oldBlock != null)
                oldBlock.RemoveWorldObjectForPhysics(worldObject.Guid, adjacencyMove);
            // Add to the new landblock
            newBlock.AddWorldObjectForPhysics(worldObject);
        }

        public static bool IsLoaded(LandblockId landblockId, uint instance)
        {
            landblockLock.EnterReadLock();
            try
            {
                return LandblockDictFetch(landblockId.Raw, instance) != null;
            }
            finally
            {
                landblockLock.ExitReadLock();
            }
        }

        /// <summary>
        /// Returns a reference to a landblock, loading the landblock if not already active
        /// </summary>
        public static Landblock GetLandblock(LandblockId landblockId, uint instance, bool loadAdjacents, bool permaload = false, EphemeralRealm ephemeralRealm = null)
        {
            if (loadAdjacents && ephemeralRealm != null)
                throw new ArgumentException("Ephemeral instances may not load adjacents (indoor landblocks only)");

            Landblock landblock;

            // Instrumentation only (ace.threads.lb_create.lock_wait): how long this call waited to ENTER the
            // landblock lock, upgradeable read and write together. Per thread, reset per call, and read back by the
            // caller on the same thread straight after the call returns; it changes nothing about the locking.
            LastLockWaitMs = 0;
            var lockWaitStart = System.Diagnostics.Stopwatch.GetTimestamp();

            landblockLock.EnterUpgradeableReadLock();
            LastLockWaitMs += System.Diagnostics.Stopwatch.GetElapsedTime(lockWaitStart).TotalMilliseconds;
            try
            {
                bool setAdjacents = false;

                var landblockIdClean = new LandblockId(landblockId.Raw | 0xFFFF);

                landblock = LandblockDictFetch(landblockIdClean.Raw, instance);

                if (landblock == null)
                {
                    lockWaitStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    landblockLock.EnterWriteLock();
                    LastLockWaitMs += System.Diagnostics.Stopwatch.GetElapsedTime(lockWaitStart).TotalMilliseconds;
                    try
                    {
                        // load up this landblock
                        landblock = new Landblock(landblockIdClean, instance);
                        LandblockDictCommit(landblockIdClean.Raw, instance, landblock);

                        if (ephemeralRealm != null)
                        {
                            landblock.InnerRealmInfo = ephemeralRealm;

                            if (!ephemeralInstances.TryRegister(instance, landblock))
                                log.Error($"LandblockManager: failed to add ephemeral instance {instance:X8} to ephemeral landblocks!");
                        }

                        if (!loadedLandblocks.Add(landblock))
                        {
                            log.Error($"LandblockManager: failed to add {LandblockKey(landblockIdClean.Raw, instance):X16} to active landblocks!");
                            return landblock;
                        }

                        landblockGroupPendingAdditions.Add(landblock);
                    }
                    finally
                    {
                        landblockLock.ExitWriteLock();
                    }

                    landblock.Init();

                    setAdjacents = true;
                }

                if (permaload)
                    landblock.Permaload = true;

                // load adjacents, if applicable
                if (loadAdjacents)
                {
                    var adjacents = GetAdjacentIDs(landblock);
                    foreach (var adjacent in adjacents)
                        GetLandblock(adjacent, instance, false, permaload);

                    setAdjacents = true;
                }

                // cache adjacencies
                if (setAdjacents)
                    SetAdjacents(landblock, true, true);

                // the id is now backed by a registered landblock (or was already loaded), so it no longer
                // needs to be reserved against a colliding allocation. Lock free - see the registry.
                ephemeralInstances.ClearPending(instance);
            }
            finally
            {
                landblockLock.ExitUpgradeableReadLock();
            }

            return landblock;
        }

        /// <summary>
        /// Returns the list of all loaded landblocks
        /// </summary>
        public static List<Landblock> GetLoadedLandblocks()
        {
            landblockLock.EnterReadLock();
            try
            {
                return loadedLandblocks.ToList();
            }
            finally
            {
                landblockLock.ExitReadLock();
            }
        }

        /// <summary>
        /// Returns the active, non-null adjacents for a landblock
        /// </summary>
        private static List<Landblock> GetAdjacents(Landblock landblock)
        {
            var adjacentIDs = GetAdjacentIDs(landblock);

            var adjacents = new List<Landblock>();

            foreach (var adjacentID in adjacentIDs)
            {
                var adjacent = LandblockDictFetch(adjacentID.Raw, landblock.Instance);
                if (adjacent != null)
                    adjacents.Add(adjacent);
            }

            return adjacents;
        }

        /// <summary>
        /// Returns the list of adjacent landblock IDs for a landblock
        /// </summary>
        private static List<LandblockId> GetAdjacentIDs(Landblock landblock)
        {
            var adjacents = new List<LandblockId>();

            if (landblock.IsDungeon)
                return adjacents;   // dungeons have no adjacents

            var north = GetAdjacentID(landblock.Id, Adjacency.North);
            var south = GetAdjacentID(landblock.Id, Adjacency.South);
            var west = GetAdjacentID(landblock.Id, Adjacency.West);
            var east = GetAdjacentID(landblock.Id, Adjacency.East);
            var northwest = GetAdjacentID(landblock.Id, Adjacency.NorthWest);
            var northeast = GetAdjacentID(landblock.Id, Adjacency.NorthEast);
            var southwest = GetAdjacentID(landblock.Id, Adjacency.SouthWest);
            var southeast = GetAdjacentID(landblock.Id, Adjacency.SouthEast);

            if (north != null)
                adjacents.Add(north.Value);
            if (south != null)
                adjacents.Add(south.Value);
            if (west != null)
                adjacents.Add(west.Value);
            if (east != null)
                adjacents.Add(east.Value);
            if (northwest != null)
                adjacents.Add(northwest.Value);
            if (northeast != null)
                adjacents.Add(northeast.Value);
            if (southwest != null)
                adjacents.Add(southwest.Value);
            if (southeast != null)
                adjacents.Add(southeast.Value);

            return adjacents;
        }

        /// <summary>
        /// Returns an adjacent landblock ID for a landblock
        /// </summary>
        private static LandblockId? GetAdjacentID(LandblockId landblock, Adjacency adjacency)
        {
            int lbx = landblock.LandblockX;
            int lby = landblock.LandblockY;

            switch (adjacency)
            {
                case Adjacency.North:
                    lby += 1;
                    break;
                case Adjacency.South:
                    lby -= 1;
                    break;
                case Adjacency.West:
                    lbx -= 1;
                    break;
                case Adjacency.East:
                    lbx += 1;
                    break;
                case Adjacency.NorthWest:
                    lby += 1;
                    lbx -= 1;
                    break;
                case Adjacency.NorthEast:
                    lby += 1;
                    lbx += 1;
                    break;
                case Adjacency.SouthWest:
                    lby -= 1;
                    lbx -= 1;
                    break;
                case Adjacency.SouthEast:
                    lby -= 1;
                    lbx += 1;
                    break;
            }

            if (lbx < 0 || lbx > 254 || lby < 0 || lby > 254)
                return null;

            return new LandblockId((byte)lbx, (byte)lby);
        }

        /// <summary>
        /// Rebuilds the adjacency cache for a landblock, and optionally rebuilds the adjacency caches
        /// for the adjacent landblocks if traverse is true
        /// </summary>
        private static void SetAdjacents(Landblock landblock, bool traverse = true, bool pSync = false)
        {
            landblock.Adjacents = GetAdjacents(landblock);

            if (pSync)
                landblock.PhysicsLandblock.SetAdjacents(landblock.Adjacents);

            if (traverse)
            {
                foreach (var adjacent in landblock.Adjacents)
                    SetAdjacents(adjacent, false);
            }
        }

        /// <summary>
        /// Queues a landblock for thread-safe unloading
        /// </summary>
        public static void AddToDestructionQueue(Landblock landblock)
        {
            destructionQueue.Add(landblock);
        }

        private static readonly System.Diagnostics.Stopwatch swTrySplitEach = new System.Diagnostics.Stopwatch();

        /// <summary>
        /// Processes the destruction queue in a thread-safe manner
        /// </summary>
        private static void UnloadLandblocks()
        {
            // dynamic_dungeons_max_unloads_per_tick: at most this many EPHEMERAL landblocks are torn down per tick;
            // the rest are carried, still loaded and still registered, to later ticks (EphemeralUnloadThrottle).
            // Non-ephemeral landblocks go through UnloadOne exactly as the old drain loop sent them, uncapped.
            // Shutdown is uncapped for ephemerals too.
            var maxEphemeralUnloads = EphemeralUnloadThrottle.Clamp(
                PropertyManager.GetLong("dynamic_dungeons_max_unloads_per_tick", EphemeralUnloadThrottle.DefaultMaxUnloadsPerTick).Item);

            ephemeralUnloadThrottle.Drain(destructionQueue.TryTake, landblock => landblock.IsEphemeral, UnloadOne, maxEphemeralUnloads, unloadingForShutdown);
        }

        /// <summary>
        /// Carries ephemeral landblocks past the per-tick unload cap to later ticks. World thread only: touched by
        /// UnloadLandblocks and nothing else.
        /// </summary>
        private static readonly EphemeralUnloadThrottle<Landblock> ephemeralUnloadThrottle = new EphemeralUnloadThrottle<Landblock>();

        /// <summary>
        /// The body of the old UnloadLandblocks drain loop, moved here verbatim so EphemeralUnloadThrottle can call it
        /// per landblock. Returns false when the landblock was refused (a player arrived since it was queued) and
        /// nothing was torn down, true otherwise. Lock scope and acquisition order are unchanged.
        /// </summary>
        private static bool UnloadOne(Landblock landblock)
        {
            // A landblock is queued by its own heartbeat, but arrivals keep happening after that
            // decision and before this drain: a recall or an admin teleport resolves on the
            // player's action queue, which TickSingleThreadedWork pumps after the heartbeat ran.
            // Re-check at the last moment and put the landblock back into service if someone is
            // standing on it, rather than tearing it down under them. Shutdown is exempt: it has
            // already logged every player off and waited for the count to reach zero, and it then
            // blocks until every landblock is gone, so a refusal there would hang the shutdown.
            if (!unloadingForShutdown && !landblock.TryClaimForDestruction())
                return false;

            // ace.threads.unload.duration: timed for ephemeral landblocks only (Threads are their main
            // producer), across Unload and the deregistration below. The ordinary world's unloads are untimed.
            var unloadStart = landblock.InnerRealmInfo != null ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;

            landblock.Unload();

            bool unloadFailed = false;

            landblockLock.EnterWriteLock();
            try
            {
                // remove from list of managed landblocks
                if (loadedLandblocks.Remove(landblock))
                {
                    LandblockDictCommit(landblock.Id.Raw, landblock.Instance, null);

                    if (landblock.InnerRealmInfo != null)
                        ephemeralInstances.TryUnregister(landblock.Instance);

                    // remove from landblock group
                    for (int i = landblockGroups.Count - 1; i >= 0 ; i--)
                    {
                        if (landblockGroups[i].Remove(landblock))
                        {
                            if (landblockGroups[i].Count == 0)
                                landblockGroups.RemoveAt(i);
                            else if (ConfigManager.Config.Server.Threading.MultiThreadedLandblockGroupPhysicsTicking || ConfigManager.Config.Server.Threading.MultiThreadedLandblockGroupTicking) // Only try to split if multi-threading is enabled
                            {
                                swTrySplitEach.Restart();
                                var splits = landblockGroups[i].TryThrottledSplit();
                                swTrySplitEach.Stop();

                                if (swTrySplitEach.Elapsed.TotalMilliseconds > 3)
                                    log.WarnFormat("[LANDBLOCK GROUP] TrySplit for {0} took: {1:N2} ms", landblockGroups[i], swTrySplitEach.Elapsed.TotalMilliseconds);
                                else if (swTrySplitEach.Elapsed.TotalMilliseconds > 1)
                                    log.DebugFormat("[LANDBLOCK GROUP] TrySplit for {0} took: {1:N2} ms", landblockGroups[i], swTrySplitEach.Elapsed.TotalMilliseconds);

                                if (splits != null)
                                {
                                    if (splits.Count > 0)
                                    {
                                        log.DebugFormat("[LANDBLOCK GROUP] TrySplit resulted in {0} split(s) and took: {1:N2} ms", splits.Count, swTrySplitEach.Elapsed.TotalMilliseconds);
                                        log.DebugFormat("[LANDBLOCK GROUP] split for old: {0}", landblockGroups[i]);
                                    }

                                    foreach (var split in splits)
                                    {
                                        landblockGroups.Add(split);
                                        log.DebugFormat("[LANDBLOCK GROUP] split and new: {0}", split);
                                    }
                                }
                            }

                            break;
                        }
                    }

                    NotifyAdjacents(landblock);
                }
                else
                    unloadFailed = true;
            }
            finally
            {
                landblockLock.ExitWriteLock();
            }

            if (unloadStart != 0)
                ServerMetrics.RecordEphemeralUnload(System.Diagnostics.Stopwatch.GetElapsedTime(unloadStart).TotalMilliseconds);

            if (unloadFailed)
                log.Error($"LandblockManager: failed to unload {landblock.Id.Raw:X8}");

            return true;
        }

        /// <summary>
        /// Notifies the adjacent landblocks to rebuild their adjacency cache
        /// Called when a landblock is unloaded
        /// </summary>
        private static void NotifyAdjacents(Landblock landblock)
        {
            var adjacents = GetAdjacents(landblock);

            foreach (var adjacent in adjacents)
                SetAdjacents(adjacent, false, true);
        }

        /// <summary>
        /// Set once, by the shutdown teardown below, and never cleared - the world does not come back.
        /// It is what exempts shutdown from the arrived-since-queued refusal in UnloadLandblocks, which
        /// would otherwise be able to hang ServerManager's "wait for all landblocks to unload" loop.
        /// </summary>
        private static volatile bool unloadingForShutdown;

        /// <summary>
        /// Used on server shutdown
        /// </summary>
        public static void AddAllActiveLandblocksToDestructionQueue()
        {
            unloadingForShutdown = true;

            landblockLock.EnterWriteLock();
            try
            {
                foreach (var landblock in loadedLandblocks)
                    AddToDestructionQueue(landblock);
            }
            finally
            {
                landblockLock.ExitWriteLock();
            }
        }

        public static EnvironChangeType? GlobalFogColor;

        private static void SetGlobalFogColor(EnvironChangeType environChangeType)
        {
            if (environChangeType.IsFog())
            {
                if (environChangeType == EnvironChangeType.Clear)
                    GlobalFogColor = null;
                else
                    GlobalFogColor = environChangeType;

                foreach (var landblock in loadedLandblocks)
                    landblock.SendCurrentEnviron();
            }
        }

        private static void SendGlobalEnvironSound(EnvironChangeType environChangeType)
        {
            if (environChangeType.IsSound())
            {
                foreach (var landblock in loadedLandblocks)
                    landblock.SendEnvironChange(environChangeType);
            }
        }

        /// <summary>
        /// Returns the live landblock for an ephemeral instance, or null if it isn't loaded.
        ///
        /// <para/>
        /// This was <c>GetEphemeralLandblockUnsafe</c>, and the name was accurate: the read was an
        /// unsynchronized <c>Dictionary.TryGetValue</c> racing structural modifications made under
        /// <see cref="landblockLock"/>, so it could return null for an instance that was in fact live. The
        /// backing store is a concurrent registry now, so a null result means the instance really was not
        /// registered at the moment of the read. It is still only a point-in-time snapshot - the landblock
        /// can unload the instant after this returns - so callers that act on a non-null result must
        /// tolerate that, but they no longer have to treat a null as possibly bogus.
        /// </summary>
        public static Landblock GetEphemeralLandblock(uint instance)
        {
            return ephemeralInstances.Get(instance);
        }

        /// <summary>Live registered ephemeral instances. Lock free (the registry is concurrent); for the metrics gauge.</summary>
        public static int EphemeralInstanceCount => ephemeralInstances.LiveCount;

        /// <summary>
        /// Milliseconds the most recent <see cref="GetLandblock"/> call ON THIS THREAD waited to enter the landblock
        /// lock. Written at the top of every call, so it is only meaningful when read on the same thread right after
        /// one returns (ThreadDungeonManager.TryStart does exactly that). A loadAdjacents call recurses, and the
        /// value then reflects the innermost call; no ephemeral load takes that path.
        /// </summary>
        [ThreadStatic]
        public static double LastLockWaitMs;

        /// <summary>
        /// Allocates a fresh ephemeral instance id for a realm: the ephemeral bit set,
        /// the realm in the middle 15 bits, and a random 16-bit short id, guaranteed
        /// not to collide with a live or pending ephemeral instance.
        /// </summary>
        public static uint RequestNewEphemeralInstanceIDv1(ushort homeRealmId)
        {
            return ephemeralInstances.RequestNewInstanceId(homeRealmId);
        }

        public static void DoEnvironChange(EnvironChangeType environChangeType)
        {
            landblockLock.EnterReadLock();
            try
            {
                if (environChangeType.IsFog())
                    SetGlobalFogColor(environChangeType);
                else
                    SendGlobalEnvironSound(environChangeType);
            }
            finally
            {
                landblockLock.ExitReadLock();
            }
        }
    }
}
