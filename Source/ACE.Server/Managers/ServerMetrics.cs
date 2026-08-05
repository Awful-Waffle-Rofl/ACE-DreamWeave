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
        }
    }
}
