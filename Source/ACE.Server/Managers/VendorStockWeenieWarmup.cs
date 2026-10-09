using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

using ACE.Common;
using ACE.Database;
using ACE.Entity.Models;

using log4net;

namespace ACE.Server.Managers
{
    /// <summary>
    /// Warms the world-database weenie cache for every wcid a vendor's Shop create list can stock, off the world
    /// thread, once at boot. The vendor counterpart of ThreadLootWeenieWarmup and MlDigsiteWeenieWarmup, and
    /// deliberately the same shape.
    ///
    /// WHY. Vendor.ActOnUse queues Vendor.LoadInventory as a landblock action (Vendor.cs ActOnUse), so it runs in
    /// Landblock.TickMultiThreadedWork's actionQueue.RunActions - inside the world tick. LoadInventory creates
    /// every Shop row of the vendor's create list with WorldObjectFactory.CreateNewWorldObject(wcid), and with
    /// WorldDatabasePrecaching off (stage and prod) the first creation of a wcid after a restart misses
    /// WorldDatabaseWithEntityCache.GetCachedWeenie and runs a synchronous world-DB read, one query per property
    /// table. Real case (stage, 2026-09-21): the first open of Tegao Wavecounter (1004130, 895 Shop rows) after a
    /// restart held one landblock's multi-threaded tick for 6.7 s - SLOW_TICK ph_lb_multithreaded=6711.86 - at
    /// the 5-7 ms per cold weenie read the other two warm-ups measure on that host.
    ///
    /// WHAT IS WARMED. Exactly the set LoadInventory can create: the distinct wcid of every create_list row with
    /// DestinationType.Shop whose owning weenie is WeenieType.Vendor (WorldDatabase.GetVendorShopCreateListWcids,
    /// one query). Mule Vendors (WeenieType.Vendor plus PropertyBool.PersonalVendor, constructed as PersonalVendor
    /// by WorldObjectFactory) are excluded by that query, because PersonalVendor.LoadInventory is an empty
    /// override: their stock comes from the account vault store, never the create list.
    ///
    /// NOT WARMED, deliberately:
    ///   * generator profiles flagged RegenLocationType.Shop. Vendor.SetEphemeralValues strips them unless
    ///     vendor_shop_uses_generator is on (it ships off), and LoadInventory's generator branch is commented out;
    ///   * create_list expansion of the stocked items. LoadInventory builds each stocked item with its weenie only;
    ///     nothing the item's own create list names is read on that path.
    ///
    /// SPLIT BY DESIGN: <see cref="Collect"/> only turns the query's ids into the warm set, and <see cref="Fill"/>
    /// only reads each id through the cache. A future bulk-by-ids weenie loader can replace Fill without touching
    /// how the ids are chosen.
    ///
    /// CONCURRENCY WITH THE OTHER WARM-UPS: it runs alongside them, not after them. On stage the Threads loot pass
    /// takes about 29 s (19.4 s of it scroll lookups) and the digsite pass under 1 s (stage log, 2026-09-21
    /// 21:14:59-21:15:28); queuing this pass behind the loot pass would leave every vendor cold for that much
    /// longer after the world opens. Each pass is one sequential reader on its own WorldDbContext, so running all
    /// three costs the world DB three concurrent connections. The digsite pass, which already runs concurrently
    /// with the loot pass, measured 5.4-6.1 ms per read against the loot pass's 7.2 ms, so the existing overlap
    /// shows no sign of harmful contention. A wcid both passes want may be read twice; the cache write is an
    /// idempotent ConcurrentDictionary assignment, so the only cost is the wasted read.
    ///
    /// THREAD SAFETY. The background task calls exactly two world-database methods:
    /// WorldDatabase.GetVendorShopCreateListWcids(), which opens and disposes its own WorldDbContext and touches no
    /// cache, and WorldDatabaseWithEntityCache.GetCachedWeenie(uint), whose miss path writes only the
    /// ConcurrentDictionary caches weenieCache and weenieClassNameToClassIdCache through its own WorldDbContext.
    /// Every PropertyManager and Config read happens on the STARTUP thread in <see cref="Start"/>, before the task
    /// is created. Nothing here touches a landblock, a vendor or a WorldObject.
    /// </summary>
    public static class VendorStockWeenieWarmup
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>The tunable that turns this pass off. Ships ON, as a kill switch rather than a gate.</summary>
        public const string EnabledKey = "vendor_stock_weenie_warmup";

        /// <summary>What one warm pass did. Every count is over DISTINCT wcids.</summary>
        internal sealed class WarmResult
        {
            /// <summary>Distinct wcids the collection step handed over.</summary>
            public int Wcids;

            /// <summary>Of those, the ones that were not cached and were read from the world database now.</summary>
            public int Loaded;

            /// <summary>Of those, the ones some earlier code path had already cached.</summary>
            public int AlreadyCached;

            /// <summary>Of those, the ones the world database has no weenie for. A content bug: that shop row stocks nothing.</summary>
            public int Missing;

            /// <summary>Of those, the ones whose read threw. Counted, logged, not retried.</summary>
            public int Threw;

            /// <summary>Every wcid with no weenie, in ascending order.</summary>
            public List<uint> MissingWcids { get; } = new List<uint>();

            /// <summary>Time spent in the collection query.</summary>
            public long QueryMs;

            /// <summary>Time spent in the per-id cache fill.</summary>
            public long FillMs;
        }

        /// <summary>
        /// Whether the pass should run at all, and why not when it should not. Pure, so the skip rules are
        /// testable without a live PropertyManager (reads of which throw under the test harness).
        /// </summary>
        internal static bool ShouldRun(bool worldDatabasePrecaching, bool tunableEnabled, out string skipReason)
        {
            if (worldDatabasePrecaching)
            {
                skipReason = "WorldDatabasePrecaching already cached every weenie";
                return false;
            }

            if (!tunableEnabled)
            {
                skipReason = EnabledKey + " is off";
                return false;
            }

            skipReason = null;
            return true;
        }

        /// <summary>
        /// Called once from Program.Main, on the startup thread, while the world DB is up and before
        /// WorldManager.Open. Returns the started task, or null when skipped.
        ///
        /// THE RETURN VALUE IS DELIBERATELY NOT AWAITED BY ITS CALLER, exactly as the other two warm-ups' are not:
        /// Task.Run returns immediately, so Program.Main runs straight on to world open and the login listener.
        /// Nothing on the world thread, the landblock threads or the login path takes a lock this task holds, so a
        /// slow pass cannot delay world open or a login, only finish later than it might have.
        /// </summary>
        public static Task Start()
        {
            // Both reads happen HERE, on the startup thread. Explicit fallback: GetBool's own is false until
            // PropertyManager has seeded a row, and this ships ON.
            if (!ShouldRun(ConfigManager.Config.Server.WorldDatabasePrecaching,
                    PropertyManager.GetBool(EnabledKey, true).Item, out var skipReason))
            {
                log.Info($"[VENDOR] vendor stock weenie warm-up skipped: {skipReason}");
                return null;
            }

            return Task.Run(Run);
        }

        /// <summary>The whole background pass: collect, fill, report. Never throws - an unobserved task exception would be lost.</summary>
        private static void Run()
        {
            try
            {
                var watch = Stopwatch.StartNew();

                var wcids = Collect(DatabaseManager.World.GetVendorShopCreateListWcids());

                var queryMs = watch.ElapsedMilliseconds;

                var result = Fill(wcids, DatabaseManager.World.GetCachedWeenie,
                    () => WorldDatabaseWithEntityCache.WeenieCacheMissesOnCurrentThread);

                result.QueryMs = queryMs;

                log.Info($"[VENDOR] vendor stock weenie warm-up: {SummaryLine(result)}");

                if (result.MissingWcids.Count > 0)
                {
                    log.Warn($"[VENDOR] vendor stock weenie warm-up: {result.MissingWcids.Count} Shop create_list wcid(s) have no weenie in this world database, so that shop row stocks nothing: " +
                             $"{string.Join(",", result.MissingWcids.Take(50))}{(result.MissingWcids.Count > 50 ? ",..." : "")}");
                }
            }
            catch (Exception ex)
            {
                // The warm-up is an optimisation: failing it costs the first vendor open its cache misses back, and
                // nothing else. It must never take the process with it.
                log.Error("[VENDOR] vendor stock weenie warm-up failed", ex);
            }
        }

        /// <summary>
        /// The warm set: the collection step's ids, de-duplicated, zeros dropped, ascending. Pure. The query already
        /// returns distinct non-zero ids; this does not rely on that, so a replacement id source cannot make
        /// <see cref="Fill"/> read one wcid twice or ask for wcid 0.
        /// </summary>
        internal static SortedSet<uint> Collect(IEnumerable<uint> wcids)
        {
            var set = new SortedSet<uint>();

            foreach (var wcid in wcids ?? Enumerable.Empty<uint>())
            {
                if (wcid != 0)
                    set.Add(wcid);
            }

            return set;
        }

        /// <summary>
        /// Reads every wcid in <paramref name="wcids"/> through <paramref name="getWeenie"/>, one at a time. A wcid
        /// is counted LOADED when <paramref name="missCounter"/> moved across its read and ALREADYCACHED when it did
        /// not, which is exactly what WorldDatabaseWithEntityCache.WeenieCacheMissesOnCurrentThread measures: this
        /// whole fill runs on one thread, so no other thread's misses are mixed in. A throw is caught per wcid, so
        /// one bad wcid costs one wcid and the pass carries on.
        /// </summary>
        /// <param name="getWeenie">Production: DatabaseManager.World.GetCachedWeenie.</param>
        /// <param name="missCounter">Production: WorldDatabaseWithEntityCache.WeenieCacheMissesOnCurrentThread.</param>
        internal static WarmResult Fill(IReadOnlyCollection<uint> wcids, Func<uint, Weenie> getWeenie, Func<long> missCounter)
        {
            var result = new WarmResult();
            var watch = Stopwatch.StartNew();

            var set = wcids ?? Array.Empty<uint>();

            result.Wcids = set.Count;

            foreach (var wcid in set)
            {
                var before = missCounter?.Invoke() ?? 0;

                try
                {
                    var weenie = getWeenie(wcid);

                    if ((missCounter?.Invoke() ?? 0) > before)
                        result.Loaded++;
                    else
                        result.AlreadyCached++;

                    if (weenie == null)
                    {
                        result.Missing++;
                        result.MissingWcids.Add(wcid);
                    }
                }
                catch (Exception ex)
                {
                    result.Threw++;
                    log.Warn($"[VENDOR] vendor stock weenie warm-up: wcid {wcid} threw", ex);
                }
            }

            result.FillMs = watch.ElapsedMilliseconds;

            return result;
        }

        /// <summary>The one summary line's body. Pure, so a test can pin the counts it reports.</summary>
        internal static string SummaryLine(WarmResult result)
        {
            if (result == null)
                return "no result";

            return $"wcids={result.Wcids} loaded={result.Loaded} alreadyCached={result.AlreadyCached} missing={result.Missing} threw={result.Threw} " +
                   $"queryMs={result.QueryMs} fillMs={result.FillMs} ms={result.QueryMs + result.FillMs}";
        }
    }
}
