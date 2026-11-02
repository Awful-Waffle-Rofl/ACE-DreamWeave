using System;
using System.Collections.Generic;
using System.Reflection;

using ACE.Server.Entity;
using ACE.Server.Entity.Actions;

using log4net;

namespace ACE.Server.HazardMechanics
{
    /// <summary>
    /// The one loop per (landblock, instance) that ticks every hazard run on it: a self-requeuing ActionChain
    /// (delay hazard_mech_tick_ms, then an action on the landblock), so every mechanic call, every spawn, every
    /// velocity broadcast and every TakeDamage runs ON THAT LANDBLOCK'S THREAD and nothing is added to the
    /// landblock tick itself.
    ///
    /// The loop ends itself when its landblock has no runs left, when its landblock is no longer the live copy
    /// (it reaps those runs first), or when a newer driver replaced it. A landblock UNLOAD always kills the chain
    /// (the queue is cleared and the copy never ticks again); /reload-landblock clears the queue but kills the chain
    /// only if its tick action was sitting in the queue at that instant. Either way the reaper notices through
    /// HazardRun.LastTickUtc, and <see cref="Ensure"/> replaces a driver not heard from for
    /// max(1 s, 3 x tick_ms) (<see cref="IsStale"/>) - so a Start right after a reload gets a fresh driver within a
    /// second, not after the 5 s run reap window. A driver entry is dropped by its own exit, by
    /// <see cref="Forget"/> when its landblock's last run stops, and by <see cref="Prune"/> at the end of every reap
    /// when its landblock is dead - the last two are what free the entry of a landblock that unloaded mid-run.
    ///
    /// COST per tick: one registry snapshot (a lock and a small list) plus each run's own work. Nothing walks the
    /// online player list; HitPlayers walks only this landblock's players (a spanning region: also those of each
    /// covered neighbour that HazardSpan finds usable - loaded and in this landblock's own tick group, so it ticks
    /// on this same thread - re-checked at the top of every run's tick).
    /// </summary>
    internal sealed class HazardDriver
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private static readonly object Sync = new object();
        private static readonly Dictionary<ulong, HazardDriver> Drivers = new Dictionary<ulong, HazardDriver>();

        private readonly Landblock landblock;
        private readonly ulong key;

        private DateTime lastTickUtc;

        private HazardDriver(Landblock landblock)
        {
            this.landblock = landblock;
            key = landblock.LongId;
            lastTickUtc = DateTime.UtcNow;
        }

        /// <summary>
        /// Makes sure <paramref name="landblock"/> has a live driver. A driver for an OLDER copy of the same
        /// (landblock, instance), or one that has stopped ticking, is replaced; the replaced one notices on its next
        /// tick (if it ever gets one) and exits, so two drivers never tick the same runs.
        /// </summary>
        internal static void Ensure(Landblock landblock)
        {
            if (landblock == null)
                return;

            HazardDriver driver;

            lock (Sync)
            {
                if (Drivers.TryGetValue(landblock.LongId, out var existing)
                    && ReferenceEquals(existing.landblock, landblock)
                    && !IsStale(existing.lastTickUtc, DateTime.UtcNow, HazardTunables.TickMs))
                    return;

                driver = new HazardDriver(landblock);
                Drivers[driver.key] = driver;
            }

            driver.Queue(0.0);
        }

        /// <summary>Pure: has a driver last heard from at <paramref name="lastTickUtc"/> missed about three of its own ticks (at least 1 s)?</summary>
        internal static bool IsStale(DateTime lastTickUtc, DateTime nowUtc, long tickMs)
            => (nowUtc - lastTickUtc).TotalSeconds > Math.Max(HazardDefaults.DriverStaleFloorSeconds, 3.0 * tickMs / 1000.0);

        /// <summary>
        /// Drops <paramref name="landblock"/>'s driver entry if it belongs to that very object AND the landblock still
        /// has no runs - re-checked UNDER the lock, because a Start can register a run and Ensure this same driver
        /// between the caller's check and this call (it would then lose its driver until the 1 s staleness replace).
        /// Its chain, if alive, exits on its next tick. Lock order Sync -> registry, the same as Tick.
        /// </summary>
        internal static void Forget(Landblock landblock)
        {
            if (landblock == null)
                return;

            lock (Sync)
            {
                var same = Drivers.TryGetValue(landblock.LongId, out var d) && ReferenceEquals(d.landblock, landblock);

                if (ShouldForget(same, same && HazardManager.HasRunsOn(landblock)))
                    Drivers.Remove(landblock.LongId);
            }
        }

        /// <summary>Pure: forget an entry only when it is this very landblock's driver and no run is left on it.</summary>
        internal static bool ShouldForget(bool sameObject, bool anyRuns) => sameObject && !anyRuns;

        /// <summary>
        /// Drops every driver entry whose landblock <paramref name="isLive"/> says is dead. The liveness check runs
        /// OUTSIDE the lock (it takes LandblockManager's lock); an entry is removed only if it is still the same driver.
        /// Returns how many were dropped.
        /// </summary>
        internal static int Prune(Func<Landblock, bool> isLive)
        {
            List<KeyValuePair<ulong, HazardDriver>> entries;

            lock (Sync)
                entries = new List<KeyValuePair<ulong, HazardDriver>>(Drivers);

            var dead = DeadKeys(entries, d => isLive == null || isLive(d.landblock));
            var dropped = 0;

            lock (Sync)
            {
                foreach (var (key, driver) in dead)
                {
                    if (Drivers.TryGetValue(key, out var current) && ReferenceEquals(current, driver))
                    {
                        Drivers.Remove(key);
                        dropped++;
                    }
                }
            }

            return dropped;
        }

        /// <summary>Pure: the entries whose value <paramref name="isLive"/> rejects. Generic so tests can use stand-ins for Landblock.</summary>
        internal static List<(TKey Key, TValue Value)> DeadKeys<TKey, TValue>(IEnumerable<KeyValuePair<TKey, TValue>> entries, Func<TValue, bool> isLive)
        {
            var dead = new List<(TKey, TValue)>();

            if (entries == null || isLive == null)
                return dead;

            foreach (var kv in entries)
                if (!isLive(kv.Value))
                    dead.Add((kv.Key, kv.Value));

            return dead;
        }

        internal static int CountForTest()
        {
            lock (Sync)
                return Drivers.Count;
        }

        private void Queue(double delaySeconds)
        {
            var chain = new ActionChain();

            if (delaySeconds > 0.0)
                chain.AddDelaySeconds(delaySeconds);

            chain.AddAction(landblock, Tick);
            chain.EnqueueChain();
        }

        private void Tick()
        {
            lock (Sync)
            {
                if (!Drivers.TryGetValue(key, out var current) || !ReferenceEquals(current, this))
                    return; // replaced

                lastTickUtc = DateTime.UtcNow;
            }

            try
            {
                if (!HazardManager.IsLandblockLive(landblock))
                {
                    HazardManager.StopAll(r => ReferenceEquals(r.Landblock, landblock), HazardOutcome.Reaped, "landblock unloaded");
                    Retire();
                    return;
                }

                var enabled = HazardTunables.Enabled;
                var now = DateTime.UtcNow;

                foreach (var run in HazardManager.RunsOn(landblock))
                {
                    if (!enabled)
                    {
                        HazardManager.Stop(run, HazardOutcome.Stopped, "hazard_mech_enabled switched off");
                        continue;
                    }

                    HazardManager.TickRun(run, now);
                }
            }
            catch (Exception ex)
            {
                log.Error($"[HAZARD] lb=0x{landblock.Id.Landblock:X4} inst=0x{landblock.Instance:X8} driver tick threw", ex);
            }

            lock (Sync)
            {
                if (!Drivers.TryGetValue(key, out var current) || !ReferenceEquals(current, this))
                    return;

                if (!HazardManager.HasRunsOn(landblock))
                {
                    Drivers.Remove(key);
                    return;
                }
            }

            Queue(HazardTunables.TickMs / 1000.0);
        }

        private void Retire()
        {
            lock (Sync)
            {
                if (Drivers.TryGetValue(key, out var current) && ReferenceEquals(current, this))
                    Drivers.Remove(key);
            }
        }
    }
}
