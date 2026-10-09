using System.Collections.Generic;

namespace ACE.Common
{
    public class GameConfiguration
    {
        public string WorldName { get; set; } = "ACEmulator";

        public NetworkSettings Network { get; set; } = new NetworkSettings();

        public AccountDefaults Accounts { get; set; } = new AccountDefaults();

        public string DatFilesDirectory { get; set; } = "c:\\ACE\\Dats\\";

        public string ModsDirectory { get; set; }

        /// <summary>
        /// The amount of seconds to wait before turning off the server. Default value is 60 (for 1 minute).
        /// </summary>
        public uint ShutdownInterval { get; set; } = 60;

        /// <summary>
        /// Seconds of in-game warning before a container stop (SIGTERM/SIGINT) logs players off. Default 60.
        /// 0 means shut down immediately with no warning. Automatically skipped when nobody is online.
        /// </summary>
        public uint ContainerShutdownWarningSeconds { get; set; } = 60;

        /// <summary>
        /// Master switch for the world-thread watchdog POLL THREAD (WorldWatchdog). Default true.
        /// Leave it on. With it off there is no heartbeat file - and the container healthcheck reads a
        /// missing heartbeat file as unhealthy, so the container reports unhealthy forever - and no
        /// FATAL log line when the world dies, and no self-exit.
        /// It does NOT disable everything: the ace.world.* metrics and the container-stop triage in
        /// Program.InitiateContainerShutdown both go through WorldWatchdog.Classify, which computes the
        /// state on the calling thread from the world thread's own statics and needs no poll thread.
        /// That is deliberate - a shutdown correctness fix must not be switchable off by a monitoring
        /// knob. To back out only a misbehaving self-exit, use WorldWatchdogExitOnDeath.
        /// </summary>
        public bool WorldWatchdogEnabled { get; set; } = true;

        /// <summary>
        /// Seconds since the last world tick before the world is reported as "stalled". Default 60.
        /// Reporting only - reaching this threshold never by itself exits the process; see
        /// WorldWatchdogExitOnStallSeconds.
        /// </summary>
        public int WorldWatchdogStallSeconds { get; set; } = 60;

        /// <summary>
        /// Exit the process when the world thread has actually died (it faulted, or it stopped ticking
        /// outside of a shutdown). Default true. A dead world can never recover on its own, so the only
        /// thing staying up buys is a healthy-looking container that serves nobody.
        /// </summary>
        public bool WorldWatchdogExitOnDeath { get; set; } = true;

        /// <summary>
        /// Exit the process when the world has been stalled (but not dead) for this many seconds.
        /// Default 0, which means never. It ships disabled deliberately: there is no measured baseline
        /// for the worst LEGITIMATE world-thread stall on this fork, so any non-zero value here is a
        /// guess that can kill a healthy server mid-hitch. Measure first, then set it.
        /// </summary>
        public int WorldWatchdogExitOnStallSeconds { get; set; } = 0;

        /// <summary>
        /// Seconds after a shutdown's countdown should have finished before the process force-exits
        /// itself with code 71. Default 240. This covers a world that hangs AFTER a normal shutdown has
        /// begun, where ServerManager.ShutdownServer's waits for players to log off, for landblocks to
        /// unload and for the world to stop are all unbounded and would otherwise block forever.
        /// The full wait is ShutdownInterval + this, so an admin "/shutdown 600" scales with it.
        /// </summary>
        public int ShutdownHardDeadlineSeconds { get; set; } = 240;

        /// <summary>
        /// Cap in seconds on the wait for the shard database queue to drain during a shutdown.
        /// Default 60. The cap matters because SerializedShardDatabase.Stop does not flush the queue,
        /// it abandons it - so this wait is the only thing that actually saves outstanding work, and it
        /// still has to end so the container stop can complete.
        /// </summary>
        public int ShutdownDrainSeconds { get; set; } = 60;

        public bool ServerPerformanceMonitorAutoStart { get; set; } = false;

        /// <summary>
        /// Enables the analytics pipeline (writes online-roster/per-block snapshots and per-character
        /// xp/lum rate rows to the MySql.Analytics database on a background thread). Off by default.
        /// </summary>
        public bool EnableAnalytics { get; set; } = false;

        /// <summary>Seconds between analytics snapshot/rate flushes. Clamped to a minimum of 10.</summary>
        public uint AnalyticsFlushIntervalSeconds { get; set; } = 60;

        /// <summary>Days of raw per-character rate rows to retain before pruning.</summary>
        public uint AnalyticsRetentionDays { get; set; } = 7;

        /// <summary>
        /// Days of public chat rows to retain before pruning. Separate from AnalyticsRetentionDays
        /// because chat is the one analytics stream whose volume is not bounded by the online player
        /// count, and because it is the one carrying player speech - so how long it is kept is a
        /// policy choice, not a storage one.
        /// </summary>
        public uint AnalyticsChatRetentionDays { get; set; } = 30;

        /// <summary>
        /// Days of session_event rows (raw login IPs) to retain before pruning. Separate from
        /// AnalyticsRetentionDays so raw IPs never outlive their intended window just because the rate
        /// tables are kept longer. Docs/Monitoring/IP-INTEGRITY-DESIGN.md section 3.4.
        /// </summary>
        public uint AnalyticsIpRetentionDays { get; set; } = 30;

        /// <summary>
        /// Path to a DB-IP "IP to ASN Lite" (or MaxMind GeoLite2 ASN) .mmdb file, used to fill session_event's
        /// asn/asn_org columns. Empty or missing leaves them NULL. Reloaded when the file's modified time
        /// changes. Never committed and not shipped in the image.
        /// </summary>
        public string AnalyticsAsnDatabasePath { get; set; } = "";

        /// <summary>
        /// Path to a text file of hosting/VPN ASNs (one per line, # comments) used to fill session_event's
        /// is_hosting column. Empty or missing leaves it NULL. Reloaded when the file's modified time changes.
        /// </summary>
        public string AnalyticsHostingAsnListPath { get; set; } = "";

        public ThreadConfiguration Threading { get; set; } = new ThreadConfiguration();

        /// <summary>
        /// The amount of minutes to keep a player object from shard database in memory. Default value is 31 minutes.
        /// </summary>
        public uint ShardPlayerBiotaCacheTime { get; set; } = 31;

        /// <summary>
        /// The amount of minutes to keep a non player object from shard database in memory. Default value is 11 minutes.
        /// </summary>
        public uint ShardNonPlayerBiotaCacheTime { get; set; } = 11;

        public bool WorldDatabasePrecaching { get; set; } = false;

        public bool LandblockPreloading { get; set; } = true;

        public List<PreloadedLandblocks> PreloadedLandblocks { get; set; } = new List<PreloadedLandblocks>()
        {
            new PreloadedLandblocks()
            {
                Id                  = "E74EFFFF",
                Description         = "Hebian-To (Global Events)",
                Permaload           = true,
                IncludeAdjacents    = false,
                Enabled             = true
            },
            new PreloadedLandblocks()
            {
                Id                  = "A9B4FFFF",
                Description         = "Holtburg",
                Permaload           = true,
                IncludeAdjacents    = true,
                Enabled             = false
            },
            new PreloadedLandblocks()
            {
                Id                  = "DA55FFFF",
                Description         = "Shoushi",
                Permaload           = true,
                IncludeAdjacents    = true,
                Enabled             = false
            },
            new PreloadedLandblocks()
            {
                Id                  = "7D64FFFF",
                Description         = "Yaraq",
                Permaload           = true,
                IncludeAdjacents    = true,
                Enabled             = false
            },
            new PreloadedLandblocks()
            {
                Id                  = "0007FFFF",
                Description         = "Town Network",
                Permaload           = true,
                IncludeAdjacents    = false,
                Enabled             = false
            },
            new PreloadedLandblocks()
            {
                Id                  = "00000000",
                Description         = "Apartment Landblocks",
                Permaload           = true,
                IncludeAdjacents    = false,
                Enabled             = false
            }
        };
    }
}
