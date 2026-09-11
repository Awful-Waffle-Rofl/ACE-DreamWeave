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

            Meter.CreateObservableGauge("ace.world.heartbeat.age", () =>
            {
                WorldWatchdog.Classify(out var staleSeconds);
                return staleSeconds;
            }, "s", "Seconds since the last world tick, or -1 while the world has never ticked");
        }
    }
}
