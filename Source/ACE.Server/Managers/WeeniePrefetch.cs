using System;
using System.Collections.Generic;

using log4net;

using ACE.Common;
using ACE.Database;

namespace ACE.Server.Managers
{
    /// <summary>
    /// The server-side front door to the bulk weenie loader (WorldDatabaseWithEntityCache.PrefetchWeenies). Callers
    /// hand it the wcids they are about to create and then run their existing per-id loop unchanged, which is now
    /// all cache hits. It adds three things the database layer cannot do for itself: the switches (read from
    /// PropertyManager), the WorldDatabasePrecaching short-circuit, and one summary log line per call.
    /// <para />
    /// It never clears a cache entry. The self-check that guards fidelity runs inside PrefetchWeenies, before a chunk
    /// is published, and a mismatch discards the chunk rather than evicting anything.
    /// </summary>
    public static class WeeniePrefetch
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public const string BulkLoadKey = "weenie_bulk_load";

        public const string SelfCheckSampleKey = "weenie_bulk_selfcheck_sample";

        public const string LandblockPrefetchKey = "landblock_weenie_prefetch";

        /// <summary>
        /// Points the database layer's switches at PropertyManager. Call once, right after PropertyManager.Initialize.
        /// The providers read PropertyManager on every use, so /modifybool, the web admin panel and a shard-config
        /// resync all take effect on the next read with no hook here. Until this runs (DatabaseManager.Initialize's
        /// boot read of "human") the database layer uses the shipped defaults.
        /// </summary>
        public static void InstallSettings()
        {
            WeenieBulkLoadSettings.EnabledProvider = () => PropertyManager.GetBool(BulkLoadKey, WeenieBulkLoadSettings.DefaultEnabled).Item;
            WeenieBulkLoadSettings.SelfCheckSampleProvider = () => PropertyManager.GetLong(SelfCheckSampleKey, WeenieBulkLoadSettings.DefaultSelfCheckSample).Item;
        }

        /// <summary>
        /// Whether landblock activation should prefetch its weenies: landblock_weenie_prefetch, and weenie_bulk_load
        /// (the master switch) as well. Read by the landblock caller, which lands in a later change.
        /// </summary>
        public static bool LandblockPrefetchEnabled =>
            PropertyManager.GetBool(LandblockPrefetchKey, true).Item && WeenieBulkLoadSettings.Enabled;

        /// <summary>
        /// Bulk-loads <paramref name="wcids"/> into the weenie cache. Returns null, and reads nothing, when
        /// WorldDatabasePrecaching already cached every weenie at boot; a Disabled result when weenie_bulk_load is off.
        /// </summary>
        /// <param name="caller">Names the call site in the summary line, e.g. "loot warm-up".</param>
        /// <param name="gateWait">Per-chunk wait for the bulk gate: TimeSpan.Zero on a world thread (try once, skip
        /// if busy), System.Threading.Timeout.InfiniteTimeSpan for a background warm-up that must complete.</param>
        public static WeenieBulkLoadResult Prefetch(IEnumerable<uint> wcids, TimeSpan gateWait, string caller)
        {
            if (ConfigManager.Config.Server.WorldDatabasePrecaching)
                return null;

            var result = DatabaseManager.World.PrefetchWeenies(wcids, gateWait);

            if (result.Disabled)
                log.Debug($"[WEENIE BULK] {caller}: {result}");
            else if (result.SelfCheckMismatches > 0 || result.ChunksFailed > 0 || result.ChunksPublishFailed > 0)
                log.Warn($"[WEENIE BULK] {caller}: {result} first mismatch: {result.FirstMismatch ?? "none"}");
            else
                log.Info($"[WEENIE BULK] {caller}: {result}");

            return result;
        }
    }
}
