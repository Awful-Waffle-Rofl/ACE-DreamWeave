using System;
using System.Diagnostics.Metrics;

namespace ACE.Server.Managers
{
    /// <summary>
    /// In-process metrics surface for the monitoring stack. Instruments are declared here with
    /// <see cref="System.Diagnostics.Metrics"/> (the provider-agnostic BCL API) and scraped
    /// out-of-process by a dotnet-monitor sidecar, which exposes them (plus the built-in .NET
    /// runtime metrics) as a Prometheus endpoint. Nothing here opens a socket or hosts HTTP:
    /// we "instrument in, expose out" so the game process gains no web surface.
    /// See Docs/Monitoring/DESIGN.md §4.1.
    ///
    /// Keep instruments cheap and allocation-free on hot paths: these counters live in the XP /
    /// Luminance grant path. They are deliberately aggregate (server-wide, no per-player labels)
    /// because per-player cardinality belongs in ace_analytics, not Prometheus.
    /// </summary>
    public static class ServerMetrics
    {
        /// <summary>
        /// Meter name a dotnet-monitor sidecar must be configured to collect
        /// (Metrics:Meters -> "ACE.Server").
        /// </summary>
        public const string MeterName = "ACE.Server";

        private static readonly Meter Meter = new Meter(MeterName);

        /// <summary>Total XP actually added to players (post fellowship split, post offline bonus).</summary>
        public static readonly Counter<long> XpGranted =
            Meter.CreateCounter<long>("ace.xp.granted", "xp", "Total XP granted to players");

        /// <summary>Total Luminance actually added to players.</summary>
        public static readonly Counter<long> LumGranted =
            Meter.CreateCounter<long>("ace.lum.granted", "lum", "Total Luminance granted to players");

        /// <summary>
        /// Total UDP payload bytes sent to clients - this is the size of the datagram itself
        /// (header + data + fragments), not the on-the-wire size. UDP+IPv4 headers add roughly
        /// 28 bytes per packet on top of this, so dividing this against PacketsSent lets an
        /// operator estimate wire overhead.
        /// </summary>
        public static readonly Counter<long> BytesSent =
            Meter.CreateCounter<long>("ace.network.bytes.sent", "By", "Total UDP payload bytes sent to clients");

        /// <summary>
        /// Total UDP payload bytes received from clients - this is the size of the datagram itself,
        /// not the on-the-wire size. UDP+IPv4 headers add roughly 28 bytes per packet on top of
        /// this, so dividing this against PacketsReceived lets an operator estimate wire overhead.
        /// </summary>
        public static readonly Counter<long> BytesReceived =
            Meter.CreateCounter<long>("ace.network.bytes.received", "By", "Total UDP payload bytes received from clients");

        /// <summary>Total UDP datagrams sent to clients.</summary>
        public static readonly Counter<long> PacketsSent =
            Meter.CreateCounter<long>("ace.network.packets.sent", "packets", "Total UDP datagrams sent to clients");

        /// <summary>Total UDP datagrams received from clients.</summary>
        public static readonly Counter<long> PacketsReceived =
            Meter.CreateCounter<long>("ace.network.packets.received", "packets", "Total UDP datagrams received from clients");

        private static readonly Histogram<double> WorldTickMs =
            Meter.CreateHistogram<double>("ace.world.tick.duration", "ms", "UpdateGameWorld duration per game tick");

        /// <summary>Records one game-world tick's duration. Called only when the world actually updated.</summary>
        public static void RecordWorldTick(double milliseconds) => WorldTickMs.Record(milliseconds);

        // World tick phases (WorldTickProfile / SlowTickReporter): 13 UNTAGGED counters and 13 UNTAGGED histograms,
        // one of each per WorldTickPhases.TagNames entry, with the tag string as a name segment:
        //
        //  - ace.world.tick.phase.<tag>.time (counter, ms): every iteration, updated or not, so the per-phase totals
        //    stack exactly to the loop's busy wall time (the idle sleep is never inside a phase). Zero laps are not
        //    added: adding 0 to a counter changes nothing.
        //  - ace.world.tick.phase.<tag>.duration (histogram, ms): every phase of every UPDATED iteration, zeros
        //    included, matching ace.world.tick.duration's "only when the world updated" rule.
        //
        // Why untagged: tags are unusable through dotnet-monitor. Its Prometheus store keys every metric by
        // instrument name alone and keeps the last MetricCount (default 3) values per name, and only ONE for a
        // histogram, so tag sets overwrite each other (dotnet-monitor release/8.x
        // src/Microsoft.Diagnostics.Monitoring.WebApi/Metrics/MetricsStore.cs, MetricKey and AddMetric). Observed on
        // stage 2026-09-21 (curl localhost:52325/metrics, 3 scrapes): the tagged ace.world.tick.phase.time showed 3
        // phases per scrape instead of 13, and ace.world.tick.phase.duration only one. Tag nothing in this file.
        //
        // The 13 histograms take this Meter past dotnet-monitor's default MaxHistograms of 20, so both sidecars set
        // DOTNETMONITOR_GlobalCounter__MaxHistograms (deploy/stage and deploy/prod docker-compose.yml).
        //
        // Recording allocates nothing: the instruments are preallocated arrays indexed by WorldTickPhase.

        private static readonly Counter<double>[] WorldTickPhaseTimeMs = BuildPhaseCounters();

        private static readonly Histogram<double>[] WorldTickPhaseMs = BuildPhaseHistograms();

        private static Counter<double>[] BuildPhaseCounters()
        {
            var counters = new Counter<double>[WorldTickPhases.Count];

            for (var i = 0; i < counters.Length; i++)
                counters[i] = Meter.CreateCounter<double>("ace.world.tick.phase." + WorldTickPhases.TagNames[i] + ".time", "ms",
                    "World-loop time spent in phase " + WorldTickPhases.TagNames[i] + ", every iteration");

            return counters;
        }

        private static Histogram<double>[] BuildPhaseHistograms()
        {
            var histograms = new Histogram<double>[WorldTickPhases.Count];

            for (var i = 0; i < histograms.Length; i++)
                histograms[i] = Meter.CreateHistogram<double>("ace.world.tick.phase." + WorldTickPhases.TagNames[i] + ".duration", "ms",
                    "World-loop time in phase " + WorldTickPhases.TagNames[i] + ", per updated iteration");

            return histograms;
        }

        /// <summary>
        /// Records one world-loop iteration's phase split. phaseMs is indexed by WorldTickPhase. Called on the
        /// world thread every iteration; an instrument nothing is listening to costs one Enabled check.
        /// </summary>
        public static void RecordTickPhases(double[] phaseMs, bool worldUpdated)
        {
            for (var i = 0; i < WorldTickPhases.Count; i++)
            {
                var ms = phaseMs[i];

                if (worldUpdated)
                {
                    var histogram = WorldTickPhaseMs[i];

                    if (histogram.Enabled)
                        histogram.Record(ms);
                }

                if (ms > 0)
                {
                    var counter = WorldTickPhaseTimeMs[i];

                    if (counter.Enabled)
                        counter.Add(ms);
                }
            }
        }

        // Threads (dynamic dungeons). Named "<thing>.duration" with unit "ms", the ace.world.tick.duration shape,
        // so dotnet-monitor exports them as aceserver_ace_threads_<thing>_duration_ms (the exporter appends the
        // unit; deploy/monitoring/game-host/dotnet-monitor/README.md). Every one is recorded off the scrape
        // thread, once per event, and carries no tags: per-run cardinality belongs in the logs, not Prometheus.

        private static readonly Histogram<double> ThreadsPlanMs =
            Meter.CreateHistogram<double>("ace.threads.plan.duration", "ms", "Threads: one DungeonPopulationBuilder.Build call");

        /// <summary>
        /// Companions to plan.duration: how many of a plan build's ThreadPlanCache structural lookups were
        /// served from cache and how many had to compute. Two untagged counters rather than one histogram tag,
        /// keeping the "no tags" rule this block opens with; the cached FRACTION is hits / (hits + misses) and
        /// the plan count is already the histogram's own count.
        /// </summary>
        private static readonly Counter<long> ThreadsPlanCacheHits =
            Meter.CreateCounter<long>("ace.threads.plan.cache_hits", "lookups", "Threads: population-plan cache lookups served from cache");

        private static readonly Counter<long> ThreadsPlanCacheMisses =
            Meter.CreateCounter<long>("ace.threads.plan.cache_misses", "lookups", "Threads: population-plan cache lookups that had to compute");

        private static readonly Histogram<double> ThreadsPlaceBatchMs =
            Meter.CreateHistogram<double>("ace.threads.place_batch.duration", "ms", "Threads: one creature PlaceBatch step on the run landblock");

        /// <summary>Companion to place_batch.duration: creatures that entered the world, so ms per creature is derivable.</summary>
        public static readonly Counter<long> ThreadsCreaturesPlaced =
            Meter.CreateCounter<long>("ace.threads.creatures.placed", "creatures", "Threads: creatures placed by PlaceBatch steps");

        /// <summary>
        /// The one-off door pass (ThreadDungeonSpawner.DoorPass), which became its own chain step on 2026-09-21.
        /// Its own histogram rather than a place_batch sample: it places no creatures, so folding it into
        /// place_batch would keep skewing that histogram's p99 on the first step of every run - which is the
        /// spike splitting the step exists to remove. Measured here so moving it out of place_batch does not
        /// make it invisible.
        /// </summary>
        private static readonly Histogram<double> ThreadsDoorPassMs =
            Meter.CreateHistogram<double>("ace.threads.door_pass.duration", "ms", "Threads: the one-off door-unlock step on the run landblock");

        /// <summary>Companion to door_pass.duration: doors actually unlocked, so ms per door is derivable.</summary>
        public static readonly Counter<long> ThreadsDoorsUnlocked =
            Meter.CreateCounter<long>("ace.threads.doors.unlocked", "doors", "Threads: doors unlocked by the door pass");

        private static readonly Histogram<double> ThreadsLandblockCreateMs =
            Meter.CreateHistogram<double>("ace.threads.lb_create.duration", "ms", "Threads: ephemeral run landblock creation, lock wait and Init included");

        private static readonly Histogram<double> ThreadsLandblockCreateLockWaitMs =
            Meter.CreateHistogram<double>("ace.threads.lb_create.lock_wait", "ms", "Threads: of lb_create, time spent waiting to enter LandblockManager's landblock lock");

        private static readonly Histogram<double> ThreadsUnloadMs =
            Meter.CreateHistogram<double>("ace.threads.unload.duration", "ms", "Ephemeral landblock unload (Threads are the main producer)");

        private static readonly Histogram<double> ThreadsLootStepMs =
            Meter.CreateHistogram<double>("ace.threads.loot_step.duration", "ms", "Threads: one pooled-loot cache-delivery step");

        private static readonly Histogram<double> ThreadsLootTrickleMs =
            Meter.CreateHistogram<double>("ace.threads.loot_trickle.duration", "ms", "Threads: one loot-trickle step building ledger rolls while a run is Active");

        /// <summary>
        /// One plan build: its duration, plus how its ThreadPlanCache lookups split. Both counts are 0 when
        /// the cache is switched off (dynamic_dungeons_plan_cache), so neither counter moves and the duration
        /// histogram still records every build.
        /// </summary>
        public static void RecordThreadsPlan(double milliseconds, int cacheHits = 0, int cacheLookups = 0)
        {
            ThreadsPlanMs.Record(milliseconds);

            var hits = Math.Max(0, Math.Min(cacheHits, cacheLookups));
            var misses = Math.Max(0, cacheLookups) - hits;

            if (hits > 0)
                ThreadsPlanCacheHits.Add(hits);

            if (misses > 0)
                ThreadsPlanCacheMisses.Add(misses);
        }

        public static void RecordThreadsPlaceBatch(double milliseconds, int placed)
        {
            ThreadsPlaceBatchMs.Record(milliseconds);

            if (placed > 0)
                ThreadsCreaturesPlaced.Add(placed);
        }

        public static void RecordThreadsDoorPass(double milliseconds, int doorsUnlocked)
        {
            ThreadsDoorPassMs.Record(milliseconds);

            if (doorsUnlocked > 0)
                ThreadsDoorsUnlocked.Add(doorsUnlocked);
        }

        public static void RecordThreadsLandblockCreate(double milliseconds, double lockWaitMilliseconds)
        {
            ThreadsLandblockCreateMs.Record(milliseconds);
            ThreadsLandblockCreateLockWaitMs.Record(lockWaitMilliseconds);
        }

        public static void RecordEphemeralUnload(double milliseconds) => ThreadsUnloadMs.Record(milliseconds);

        public static void RecordThreadsLootStep(double milliseconds) => ThreadsLootStepMs.Record(milliseconds);

        public static void RecordThreadsLootTrickle(double milliseconds) => ThreadsLootTrickleMs.Record(milliseconds);

        // Pending teleports (Player_TeleportWatch): a teleport or login sets Player.Teleporting and only the client's
        // GameActionLoginComplete clears it. UNTAGGED, one counter per kind, per the rule at the top of the tick-phase
        // block. Accounting: started = completed + abandoned + superseded + still pending. pending_10s >= pending_30s
        // always (a first check already past 30s counts both).

        private static readonly Counter<long> TeleportStartedTeleport =
            Meter.CreateCounter<long>("ace.teleport.started.teleport", "teleports", "Pending-teleport windows opened by a non-portal teleport");

        private static readonly Counter<long> TeleportStartedPortal =
            Meter.CreateCounter<long>("ace.teleport.started.portal", "teleports", "Pending-teleport windows opened by a portal");

        private static readonly Counter<long> TeleportStartedLogin =
            Meter.CreateCounter<long>("ace.teleport.started.login", "teleports", "Pending-teleport windows opened by a login");

        public static readonly Counter<long> TeleportCompleted =
            Meter.CreateCounter<long>("ace.teleport.completed", "teleports", "Pending-teleport windows closed by the client's LoginComplete");

        public static readonly Counter<long> TeleportPending10s =
            Meter.CreateCounter<long>("ace.teleport.pending_10s", "teleports", "Teleports still pending 10s after they started");

        public static readonly Counter<long> TeleportPending30s =
            Meter.CreateCounter<long>("ace.teleport.pending_30s", "teleports", "Teleports still pending 30s after they started");

        public static readonly Counter<long> TeleportAbandoned =
            Meter.CreateCounter<long>("ace.teleport.abandoned", "teleports", "Teleports still pending when the player logged out, disconnected or was stuck-logged-off");

        public static readonly Counter<long> TeleportSuperseded =
            Meter.CreateCounter<long>("ace.teleport.superseded", "teleports", "Teleports replaced by another teleport while still pending");

        public static void RecordTeleportStarted(ACE.Server.Entity.TeleportKind kind)
        {
            switch (kind)
            {
                case ACE.Server.Entity.TeleportKind.Portal: TeleportStartedPortal.Add(1); break;
                case ACE.Server.Entity.TeleportKind.Login: TeleportStartedLogin.Add(1); break;
                default: TeleportStartedTeleport.Add(1); break;
            }
        }

        /// <summary>
        /// Registers the observable gauges and forces this type's static initialization so the
        /// counters exist for the sidecar to discover. Call once at startup.
        /// </summary>
        public static void Initialize()
        {
            Meter.CreateObservableGauge("ace.players.online", () => PlayerManager.GetOnlineCount(),
                "players", "Players currently online");

            Meter.CreateObservableGauge("ace.landblocks.loaded", () => LandblockManager.GetLoadedLandblocks().Count,
                "landblocks", "Active loaded landblocks");

            // Threads load. Both read concurrent collections only (ThreadDungeonManager's run registry and
            // LandblockManager's EphemeralInstanceRegistry), so a scrape-thread callback is safe and cannot throw
            // into the metrics endpoint.
            Meter.CreateObservableGauge("ace.threads.runs.live", () => ACE.Server.ThreadDungeons.ThreadDungeonManager.LiveRunCount,
                "runs", "Threads runs not yet Ended");

            Meter.CreateObservableGauge("ace.landblocks.ephemeral.live", () => LandblockManager.EphemeralInstanceCount,
                "instances", "Live ephemeral landblock instances");

            // Depth of the single shard-database work queue. SerializedShardDatabase funnels every
            // shard read and write through ONE worker thread, so this is the first thing that backs
            // up under load and the only direct view of it - a sustained non-zero depth means the
            // world is producing shard work faster than one thread can drain it, which shows up to
            // players as login stalls and delayed saves long before tick time moves.
            //
            // Read the counter, never GetCurrentQueueWaitTime: that measures wait by ENQUEUEING a
            // probe, so scraping it would add queue work on every scrape.
            //
            // Null-guarded defensively, not because a live server reaches it: DatabaseManager
            // sets InitializationFailure on both of its early returns (DatabaseManager.cs:43,:51)
            // and Program.cs:303-307 exits before it ever gets here (:376). The guard costs
            // nothing and keeps the failure mode sane for any other host of this Meter, since an
            // observable callback runs on a scrape thread where an NRE would surface as a dead
            // metrics endpoint rather than as anything that points back to this line.
            Meter.CreateObservableGauge("ace.database.shard.queue.depth",
                () => ACE.Database.DatabaseManager.Shard?.QueueCount ?? 0,
                "items", "Pending operations in the serialized shard database queue");

            // World-thread liveness. These exist because every other gauge above lies about it: they are
            // all served by the metrics scrape thread, which is not the world thread, so ace.players.online
            // kept returning its (stale) count throughout the 2026-09-01 outage while the world thread was
            // dead and every login was failing. Nothing alerted, for 5h22m. These two are the only
            // instruments here whose value goes bad when the world stops.
            //
            // Both call WorldWatchdog.Classify rather than reading its published CurrentState, so they
            // stay truthful even when WorldWatchdogEnabled is false: Classify computes the verdict on
            // the scrape thread from the world thread's own statics and does not need the watchdog's
            // poll thread to be running. Reading the published state instead would make these two report
            // a permanent, confident "0 starting / -1" with the watchdog off - which is precisely the
            // "no observer" condition the outage happened in, dressed up as a healthy metric.
            //
            // Classify is a handful of static reads plus one Environment.TickCount64, so it is cheap
            // enough for a per-scrape callback, and it cannot throw for the same reason the queue-depth
            // guard above exists: an observable callback runs on a scrape thread, where a throw surfaces
            // as a dead metrics endpoint rather than as anything that points back to this line.
            Meter.CreateObservableGauge("ace.world.status", () => (int)WorldWatchdog.Classify(out _),
                "code", "World thread state: 0 starting, 1 ticking, 2 stalled, 3 dead");

            // Bulk weenie loader (WorldDatabaseWithEntityCache.PrefetchWeenies). Observable counters over the
            // database layer's own monotonic Interlocked totals, one UNTAGGED instrument per series (the rule at
            // the top of the tick-phase block: dotnet-monitor drops tags). Each callback is one Interlocked.Read,
            // so it cannot throw into the scrape. chunks_rejected above zero is a fidelity bug: the self-check found
            // a bulk-read weenie differing from its per-id read, and nothing from that chunk was published.
            Meter.CreateObservableCounter("ace.world_db.weenie_bulk.loaded", () => ACE.Database.WorldDatabaseWithEntityCache.BulkLoadedTotal,
                "weenies", "Weenies published into the weenie cache by bulk loads");

            Meter.CreateObservableCounter("ace.world_db.weenie_bulk.gate_timeouts", () => ACE.Database.WorldDatabaseWithEntityCache.BulkGateTimeoutTotal,
                "calls", "Bulk weenie loads that gave up waiting for the bulk gate");

            Meter.CreateObservableCounter("ace.world_db.weenie_bulk.skipped", () => ACE.Database.WorldDatabaseWithEntityCache.BulkIdsSkippedTotal,
                "weenies", "Wcids a bulk load skipped on a gate timeout (left to the per-id path)");

            Meter.CreateObservableCounter("ace.world_db.weenie_bulk.chunks_rejected", () => ACE.Database.WorldDatabaseWithEntityCache.BulkChunksRejectedTotal,
                "chunks", "Bulk weenie chunks discarded by the publish-time self-check");

            Meter.CreateObservableCounter("ace.world_db.weenie_bulk.chunks_stale", () => ACE.Database.WorldDatabaseWithEntityCache.BulkChunksStaleTotal,
                "chunks", "Bulk weenie chunks discarded because a cache clear raced them");

            Meter.CreateObservableCounter("ace.world_db.weenie_bulk.chunks_failed", () => ACE.Database.WorldDatabaseWithEntityCache.BulkChunksFailedTotal,
                "chunks", "Bulk weenie chunks discarded because their read threw");

            Meter.CreateObservableCounter("ace.world_db.weenie_bulk.chunks_publish_failed", () => ACE.Database.WorldDatabaseWithEntityCache.BulkChunksPublishFailedTotal,
                "chunks", "Bulk weenie chunks whose publish threw part-way (added wcids kept, the rest left to the per-id path)");

            Meter.CreateObservableGauge("ace.world.heartbeat.age", () =>
            {
                WorldWatchdog.Classify(out var staleSeconds);
                return staleSeconds;
            }, "s", "Seconds since the last world tick, or -1 while the world has never ticked");
        }
    }
}
