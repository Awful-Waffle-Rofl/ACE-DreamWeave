using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

using ACE.Common;
using ACE.Database;
using ACE.Entity.Models;
using ACE.Server.Managers;

using log4net;

namespace ACE.Server.MlDigsite
{
    /// <summary>
    /// Warms the world-database weenie cache for every wcid the ML digsite system can create, off the world
    /// thread, once at boot. The digsite counterpart of ThreadLootWeenieWarmup, and deliberately the same
    /// shape.
    ///
    /// WHY THIS SYSTEM SPECIFICALLY. Digsite wave spawning is bulk creature creation that runs DIRECTLY on the
    /// world thread, with no landblock action between it and every player on the server:
    /// MlDigsiteManager.Tick -> Drive -> DriveWaves -> MlDigsiteSpawner.SpawnWave -> TrySpawn ->
    /// WorldObjectFactory.CreateNewWorldObject / Creature.EnterWorld. With WorldDatabasePrecaching off (stage
    /// and prod) the first creation of a wcid after a restart misses
    /// WorldDatabaseWithEntityCache.GetCachedWeenie and runs a synchronous world-DB read - one query per
    /// property table - inside that tick. A wave is up to ml_digsite_wave_count_cap creatures, every
    /// ml_digsite_inter_wave_seconds, per live encounter, so a cold roster pays those reads where every player
    /// on the shard feels them. Warming the set up front moves them onto a background task before anyone can
    /// finish a dig.
    ///
    /// WHAT IS WARMED, all of it traced from the creation sites rather than hand-listed:
    ///   * every roster entry of every <see cref="MlDigsiteRole"/> (MlDigsiteRoster.Entries) - the only thing
    ///     MlDigsiteSpawner.TrySpawn draws from - so a roster edit cannot desync from the warm set;
    ///   * MlDigsiteRoster.KeptSiraluun, which is NOT a role and so is NOT reachable through Entries, but
    ///     which TrySpawn substitutes for a MiniBoss/Boss draw at ml_digsite_kept_siraluun_chance;
    ///   * the reward path's fixed wcids: MlDigsiteRewards.ChestWcid, .DoubloonWcid and .TradeNoteWcid, named
    ///     from those constants so the warm set follows a retune of either;
    ///   * the three prop wcids MlDigsiteProps.TryPlace is ever handed, read live from MlDigsiteTunables
    ///     (hazard marker, safe marker, interrupt object), so an operator who repoints one warms the new one;
    ///   * the MlTreasure dig path that OPENS a digsite and pays it out: the map itself, the doubloon, and the
    ///     Aun Relaria boss-variant's boss and its two trophies, all named from their own constants;
    ///   * ONE level of create_list expansion over everything above (see <see cref="Warm"/>), which is what
    ///     picks up a roster creature's wielded weapon (Creature.GenerateWieldList creates every Wield row of
    ///     the weenie's create list by wcid, in the Creature constructor, i.e. inside the same world-thread
    ///     spawn) and the Kept Siraluun feather MlDigsiteRewards.FillKeptSiraluunDrops hands to the chest.
    ///
    /// NOT WARMED, deliberately:
    ///   * the chest's rolled loot (MlDigsiteRewards.FillLoot -> LootGenerationFactory): that is the loot
    ///     factory's own wcid space, which ThreadLootWeenieWarmup warms at the same boot - but only up to
    ///     dynamic_dungeons_loot_tier_cap, which is a THREADS tunable an operator can lower without knowing
    ///     the digsite chest also depends on it. Coverage of the chest is therefore conditional: it is
    ///     complete only while that cap is at or above the tier of the treasure_death profile
    ///     ml_digsite_chest_treasure_death_id selects. At the shipped defaults it is exactly complete - the
    ///     cap is 8 and treasure_Type 2001 (row id 241) is tier 8 - so a lowered cap, not this exclusion, is
    ///     what would leave chest loot cold;
    ///   * GenerateWieldedTreasure / GenerateInventoryTreasure, which resolve through the treasure_wielded
    ///     tables rather than a create list, and are not enumerable from the roster;
    ///   * create_list expansion past the first level: a wielded sword's own create list is not a spawn-time
    ///     read, and unbounded recursion would walk a large share of the weenie table.
    ///
    /// THREAD SAFETY. The background task calls exactly one world-database method,
    /// WorldDatabaseWithEntityCache.GetCachedWeenie(uint). Its miss path writes only weenieCache and
    /// weenieClassNameToClassIdCache, both ConcurrentDictionary, through the GetWeenie(WorldDbContext, uint)
    /// override, which opens its own WorldDbContext; it does not touch weenieCacheByType or scrollsBySpellID
    /// (those are written only by PopulateWeenieSpecificCaches and GetScrollWeenie, neither of which is
    /// called from here). Every PropertyManager read happens on the STARTUP thread in <see cref="Start"/>,
    /// before the task is created, so nothing this task reads can be a plain Dictionary being written
    /// elsewhere. Nothing here touches an encounter, a landblock or a WorldObject.
    /// </summary>
    public static class MlDigsiteWeenieWarmup
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>The tunable that turns this pass off. Ships ON, as a kill switch rather than a gate.</summary>
        public const string EnabledKey = "ml_digsite_weenie_warmup";

        /// <summary>Everything <see cref="Collect"/> found, and which source contributed.</summary>
        internal sealed class WarmSet
        {
            public SortedSet<uint> Wcids { get; } = new SortedSet<uint>();

            /// <summary>Human-readable source labels, for the debug line.</summary>
            public List<string> Sources { get; } = new List<string>();
        }

        /// <summary>What one warm pass did. Every count is over DISTINCT wcids.</summary>
        internal sealed class WarmResult
        {
            /// <summary>Wcids asked for in the first pass (the collected set).</summary>
            public int Wcids;

            /// <summary>Of those, the ones that were not cached and were read from the world database now.</summary>
            public int Loaded;

            /// <summary>Of those, the ones some earlier code path had already cached.</summary>
            public int AlreadyCached;

            /// <summary>Of those, the ones the world database has no weenie for. A content bug: a spawn of one fails.</summary>
            public int Missing;

            /// <summary>Of those, the ones whose read threw. Counted, logged, not retried.</summary>
            public int Threw;

            /// <summary>Second-pass wcids: the create_list rows of everything warmed above, minus what was already in the set.</summary>
            public int CreateListWcids;

            public int CreateListLoaded;
            public int CreateListAlreadyCached;
            public int CreateListMissing;
            public int CreateListThrew;

            /// <summary>Every wcid with no weenie, both passes, in ascending order.</summary>
            public List<uint> MissingWcids { get; } = new List<uint>();

            public long ElapsedMs;
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
        /// Called once from Program.Main, on the startup thread, while the world DB is up and well before
        /// WorldManager.Open. Returns the started task, or null when skipped.
        ///
        /// THE RETURN VALUE IS DELIBERATELY NOT AWAITED BY ITS CALLER, exactly as ThreadLootWeenieWarmup's is
        /// not: Task.Run hands the work to a thread-pool thread and returns immediately, so Program.Main runs
        /// straight on through to WorldManager.Open and the login listener. Nothing on the world thread, the
        /// landblock threads or the login path takes a lock this task holds - its only shared state is two
        /// ConcurrentDictionary caches whose readers are lock-free - so a slow pass cannot delay world open or
        /// a player login, only finish later than it might have.
        /// </summary>
        public static Task Start()
        {
            // Both reads happen HERE, on the startup thread, and the same explicit-fallback rule
            // MlDigsiteTunables documents applies: GetBool's own fallback is false until PropertyManager has
            // seeded a row, and this ships ON.
            if (!ShouldRun(ConfigManager.Config.Server.WorldDatabasePrecaching,
                    PropertyManager.GetBool(EnabledKey, true).Item, out var skipReason))
            {
                log.Info($"[ML_DIGSITE] weenie warm-up skipped: {skipReason}");
                return null;
            }

            WarmSet set;

            try
            {
                // Every tunable read is taken here, before the task exists.
                set = Collect(MlDigsiteRoster.Entries, MlDigsiteRoster.KeptSiraluun, PropWcids(), RewardWcids());
            }
            catch (Exception ex)
            {
                log.Error("[ML_DIGSITE] weenie warm-up could not collect its wcid set; nothing warmed", ex);
                return null;
            }

            return Task.Run(() => Run(set));
        }

        /// <summary>
        /// The wcids MlDigsiteProps.TryPlace can be handed: the Boss Rush mechanics' hazard marker, safe-zone
        /// marker and interrupt object. Read from the tunables rather than their defaults, so a repointed
        /// marker is the one warmed. 0 means "placement disabled" and is dropped by <see cref="Collect"/>.
        /// </summary>
        private static IEnumerable<uint> PropWcids()
        {
            yield return MlDigsiteTunables.BossRushHazardMarkerWcid;
            yield return MlDigsiteTunables.BossRushSafeMarkerWcid;
            yield return MlDigsiteTunables.BossRushInterruptWcid;
        }

        /// <summary>
        /// Everything the payout and the dig that opens it create by wcid, named from the constants the
        /// creating code itself reads so the two can never disagree: the reward chest, the two currencies it
        /// is filled with, the treasure map whose dig opens an encounter, and the Aun Relaria boss-variant's
        /// boss and its two trophies (a separate dig mechanism, but the same first-touch stall on the same
        /// island, and the cheapest place to warm it is here).
        /// </summary>
        private static IEnumerable<uint> RewardWcids()
        {
            yield return MlDigsiteRewards.ChestWcid;
            yield return MlDigsiteRewards.DoubloonWcid;
            yield return MlDigsiteRewards.TradeNoteWcid;

            yield return MlTreasure.MlTreasureDrop.TreasureMapWcid;
            yield return MlTreasure.MlTreasureDrop.RelariaBossWcid;
            yield return MlTreasure.MlRelariaTrophy.TrophyWcid;
            yield return MlTreasure.MlRelariaChargeTrophy.TrophyWcid;
        }

        /// <summary>
        /// Every wcid the digsite system can create, DERIVED from the roster's own tables: every entry of
        /// every <see cref="MlDigsiteRole"/> that <paramref name="entriesFor"/> answers with, plus the
        /// Kept Siraluun band (which is not a role), plus the prop and reward wcids traced from their
        /// creation sites. Zeros are dropped - 0 is how a prop placement is turned off, and
        /// WorldObjectFactory.CreateNewWorldObject(0) resolves nothing anyway.
        ///
        /// Walking Enum.GetValues rather than naming the roles means a role added to the enum and wired into
        /// MlDigsiteRoster.Entries is warmed without anyone remembering to come back here.
        /// </summary>
        /// <param name="entriesFor">Production: MlDigsiteRoster.Entries.</param>
        /// <param name="keptSiraluun">Production: MlDigsiteRoster.KeptSiraluun.</param>
        internal static WarmSet Collect(Func<MlDigsiteRole, IReadOnlyList<MlDigsiteRosterEntry>> entriesFor,
            IReadOnlyList<MlDigsiteRosterEntry> keptSiraluun, IEnumerable<uint> propWcids, IEnumerable<uint> rewardWcids)
        {
            var set = new WarmSet();

            if (entriesFor != null)
            {
                foreach (MlDigsiteRole role in Enum.GetValues(typeof(MlDigsiteRole)))
                {
                    var before = set.Wcids.Count;

                    foreach (var entry in entriesFor(role) ?? Array.Empty<MlDigsiteRosterEntry>())
                        Add(set, entry.Wcid);

                    if (set.Wcids.Count > before)
                        set.Sources.Add($"MlDigsiteRoster.Entries({role}) (+{set.Wcids.Count - before})");
                }
            }

            AddAll(set, (keptSiraluun ?? Array.Empty<MlDigsiteRosterEntry>()).Select(e => e.Wcid), "MlDigsiteRoster.KeptSiraluun");
            AddAll(set, propWcids, "MlDigsiteProps placements (hazard marker, safe marker, interrupt object)");
            AddAll(set, rewardWcids, "reward and dig path (chest, doubloon, trade note, map, Relaria boss, trophies)");

            return set;
        }

        private static void Add(WarmSet set, uint wcid)
        {
            if (wcid != 0)
                set.Wcids.Add(wcid);
        }

        private static void AddAll(WarmSet set, IEnumerable<uint> wcids, string label)
        {
            var before = set.Wcids.Count;

            foreach (var wcid in wcids ?? Enumerable.Empty<uint>())
                Add(set, wcid);

            if (set.Wcids.Count > before)
                set.Sources.Add($"{label} (+{set.Wcids.Count - before})");
        }

        /// <summary>The whole background pass: warm, then report. Never throws - an unobserved task exception would be lost.</summary>
        private static void Run(WarmSet set)
        {
            try
            {
                var result = Warm(set, DatabaseManager.World.GetCachedWeenie,
                    () => WorldDatabaseWithEntityCache.WeenieCacheMissesOnCurrentThread);

                log.Info($"[ML_DIGSITE] weenie warm-up: {SummaryLine(result)}");

                if (result.MissingWcids.Count > 0)
                {
                    log.Warn($"[ML_DIGSITE] weenie warm-up: {result.MissingWcids.Count} wcid(s) have no weenie in this world database, so creating one would fail: " +
                             $"{string.Join(",", result.MissingWcids.Take(50))}{(result.MissingWcids.Count > 50 ? ",..." : "")}");
                }

                if (log.IsDebugEnabled)
                    log.Debug($"[ML_DIGSITE] weenie warm-up sources: {string.Join(", ", set.Sources)}");
            }
            catch (Exception ex)
            {
                // The warm-up is an optimisation: failing it costs the first dig its cache misses back, and
                // nothing else. It must never take the process with it.
                log.Error("[ML_DIGSITE] weenie warm-up failed", ex);
            }
        }

        /// <summary>
        /// Loads every wcid in <paramref name="set"/>, then one level of create_list expansion over whatever
        /// came back.
        ///
        /// WHY THE SECOND PASS. A roster creature's weapon is not a roster wcid: Creature's constructor calls
        /// GenerateWieldList, which creates every Wield row of the weenie's create list by wcid
        /// (Creature_Equipment.cs), inside the same world-thread spawn the roster wcid was read in - so a cold
        /// weapon costs the same stall the creature would have. The Kept Siraluun feather is the same shape on
        /// the reward side (MlDigsiteRewards.FillKeptSiraluunDrops creates the create_list row it banked). One
        /// level only, and rows already in the first pass are not asked for twice.
        ///
        /// A wcid is counted LOADED when <paramref name="missCounter"/> moved across its read and
        /// ALREADYCACHED when it did not, which is exactly what
        /// WorldDatabaseWithEntityCache.WeenieCacheMissesOnCurrentThread measures: this whole pass runs on one
        /// thread, so no other thread's misses are mixed in.
        /// </summary>
        /// <param name="getWeenie">Production: DatabaseManager.World.GetCachedWeenie.</param>
        /// <param name="missCounter">Production: WorldDatabaseWithEntityCache.WeenieCacheMissesOnCurrentThread.</param>
        internal static WarmResult Warm(WarmSet set, Func<uint, Weenie> getWeenie, Func<long> missCounter)
        {
            var result = new WarmResult();
            var watch = Stopwatch.StartNew();

            var first = set?.Wcids ?? new SortedSet<uint>();

            result.Wcids = first.Count;

            var createList = new SortedSet<uint>();

            foreach (var wcid in first)
            {
                var weenie = Load(wcid, getWeenie, missCounter, result,
                    ref result.Loaded, ref result.AlreadyCached, ref result.Missing, ref result.Threw);

                if (weenie?.PropertiesCreateList == null)
                    continue;

                foreach (var row in weenie.PropertiesCreateList)
                {
                    if (row != null && row.WeenieClassId != 0 && !first.Contains(row.WeenieClassId))
                        createList.Add(row.WeenieClassId);
                }
            }

            result.CreateListWcids = createList.Count;

            foreach (var wcid in createList)
            {
                Load(wcid, getWeenie, missCounter, result,
                    ref result.CreateListLoaded, ref result.CreateListAlreadyCached, ref result.CreateListMissing, ref result.CreateListThrew);
            }

            result.ElapsedMs = watch.ElapsedMilliseconds;

            return result;
        }

        /// <summary>
        /// One wcid: read it, decide whether the read cost a world-DB load, and bank the outcome. A throw is
        /// caught HERE rather than around the loop, so one bad wcid costs one wcid and the pass carries on
        /// (which is the whole point of warming up front - a wcid that throws here would have thrown inside
        /// the world tick instead).
        /// </summary>
        private static Weenie Load(uint wcid, Func<uint, Weenie> getWeenie, Func<long> missCounter, WarmResult result,
            ref int loaded, ref int alreadyCached, ref int missing, ref int threw)
        {
            var before = missCounter?.Invoke() ?? 0;

            try
            {
                var weenie = getWeenie(wcid);

                if ((missCounter?.Invoke() ?? 0) > before)
                    loaded++;
                else
                    alreadyCached++;

                if (weenie == null)
                {
                    missing++;
                    result.MissingWcids.Add(wcid);
                }

                return weenie;
            }
            catch (Exception ex)
            {
                threw++;
                log.Warn($"[ML_DIGSITE] weenie warm-up: wcid {wcid} threw", ex);
                return null;
            }
        }

        /// <summary>The one summary line's body. Pure, so a test can pin the counts it reports.</summary>
        internal static string SummaryLine(WarmResult result)
        {
            if (result == null)
                return "no result";

            return $"wcids={result.Wcids} loaded={result.Loaded} alreadyCached={result.AlreadyCached} missing={result.Missing} threw={result.Threw} " +
                   $"createListWcids={result.CreateListWcids} createListLoaded={result.CreateListLoaded} createListAlreadyCached={result.CreateListAlreadyCached} " +
                   $"createListMissing={result.CreateListMissing} createListThrew={result.CreateListThrew} ms={result.ElapsedMs}";
        }
    }
}
