using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

using ACE.Common;
using ACE.Database;
using ACE.Server.Factories.Entity;
using ACE.Server.Factories.Enum;
using ACE.Server.Factories.Tables;
using ACE.Server.Factories.Tables.Wcids;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// Warms the world-database weenie cache for every wcid Threads loot delivery can create, off the world thread,
    /// once at boot.
    ///
    /// Why. With WorldDatabasePrecaching off (stage and prod), the first time a wcid is created after a restart
    /// WorldDatabaseWithEntityCache.GetCachedWeenie misses and runs a synchronous world-DB read - one query per
    /// property table - inside whatever landblock action asked for it. Pooled delivery materialises hundreds of loot
    /// objects inside landblock actions that run inside the world tick, so every such miss is world-thread stall.
    /// Warming the set up front moves those reads onto a background task before any player can clear a Thread.
    ///
    /// The set is derived from the loot factory's OWN static wcid tables by reflection (<see cref="Collect"/>), not
    /// from a hand-kept list, so a table added to Factories/Tables/Wcids is warmed without anyone remembering to.
    /// What is included and excluded, and why, is in the Collect doc and in the PR that introduced this.
    ///
    /// Thread safety. The background task touches the factory's static tables, which are fully built by their type
    /// initializers (the CLR runs those once, thread-safely) and never written afterwards - it only enumerates them -
    /// and two world-database reads whose miss paths open their own WorldDbContext and write only ConcurrentDictionary
    /// caches: GetCachedWeenie(uint), and, since 2026-09-19, GetScrollWeenie - whose scrollsBySpellID cache was made a
    /// ConcurrentDictionary for this, which also closes a pre-existing race with the landblock threads that write it
    /// today. Every input read from a plain Dictionary elsewhere (Player.MaterialSalvage, the Threads store's
    /// modifiers) is snapshotted on the calling thread BEFORE the task starts.
    /// </summary>
    public static class ThreadLootWeenieWarmup
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>Namespace of the loot factory's per-item-type wcid tables (Factories/Tables/Wcids and its Weapons subfolders).</summary>
        internal const string WcidTableNamespace = "ACE.Server.Factories.Tables.Wcids";

        /// <summary>The highest TreasureDeath tier the factory's tier-indexed tables carry.</summary>
        internal const int MaxTier = 8;

        /// <summary>What <see cref="Collect"/> found: the wcids, and which static fields contributed or were skipped.</summary>
        internal sealed class WarmSet
        {
            public SortedSet<uint> Wcids { get; } = new SortedSet<uint>();
            public List<string> Included { get; } = new List<string>();
            public List<string> Excluded { get; } = new List<string>();
        }

        /// <summary>
        /// Called once from Program.Main, on the startup thread, after ThreadDungeonManager.Initialize (the Threads
        /// store is loaded) and while the world DB is up. Returns the started task, or null when skipped.
        /// </summary>
        public static Task Start()
        {
            if (ConfigManager.Config.Server.WorldDatabasePrecaching)
            {
                log.Info("[DYNDUNGEON] loot weenie warm-up skipped: WorldDatabasePrecaching already cached every weenie");
                return null;
            }

            // Explicit fallback: GetBool's own is false until PropertyManager.DoWork seeds a row, and this ships ON.
            if (!PropertyManager.GetBool("dynamic_dungeons_loot_weenie_warmup", true).Item)
            {
                log.Info("[DYNDUNGEON] loot weenie warm-up skipped: dynamic_dungeons_loot_weenie_warmup is off");
                return null;
            }

            var tierCap = (int)Math.Clamp(PropertyManager.GetLong("dynamic_dungeons_loot_tier_cap", MaxTier).Item, 1, MaxTier);

            // Snapshotted HERE, on the startup thread: both are plain Dictionaries, and the background task must not
            // read one while anything could be writing it.
            var extra = DeliveryWcids(Player.MaterialSalvage.Values.Select(v => (uint)v),
                ThreadDungeonManager.Store?.Modifiers?.Values.Select(m => m.SalvageBaseWcid)).ToList();

            return Task.Run(() => Warm(tierCap, extra));
        }

        /// <summary>
        /// The wcids the delivery path creates OUTSIDE the loot factory: the cache chest and summoned exit, the Trade
        /// Note of the boss bonus, every salvage bag (Player.MaterialSalvage, which the bonus's targeted bags index),
        /// and every salvage-affinity base wcid a modifier can roll. Zeros are dropped.
        /// </summary>
        internal static IEnumerable<uint> DeliveryWcids(IEnumerable<uint> salvageBags, IEnumerable<uint> affinityBases)
        {
            yield return ThreadDungeonRewardSpawner.BossCacheWcid;
            yield return ThreadDungeonRewardSpawner.SummonedExitWcid;
            yield return ThreadDungeonRewardSpawner.TradeNoteWcid;

            foreach (var wcid in (salvageBags ?? Enumerable.Empty<uint>()).Concat(affinityBases ?? Enumerable.Empty<uint>()))
            {
                if (wcid != 0)
                    yield return wcid;
            }
        }

        private static void Warm(int tierCap, IReadOnlyCollection<uint> extra)
        {
            try
            {
                var watch = Stopwatch.StartNew();
                var set = Collect(tierCap, extra);
                var collectMs = watch.ElapsedMilliseconds;

                var scrolls = WarmScrolls(DatabaseManager.World.GetScrollWeenie, wcid => DatabaseManager.World.GetCachedWeenie(wcid) != null);

                var missesBefore = WorldDatabaseWithEntityCache.WeenieCacheMissesOnCurrentThread;
                var missing = new List<uint>();

                foreach (var wcid in set.Wcids)
                {
                    try
                    {
                        if (DatabaseManager.World.GetCachedWeenie(wcid) == null)
                            missing.Add(wcid);
                    }
                    catch (Exception ex)
                    {
                        missing.Add(wcid);
                        log.Warn($"[DYNDUNGEON] loot weenie warm-up: wcid {wcid} threw", ex);
                    }
                }

                var loaded = WorldDatabaseWithEntityCache.WeenieCacheMissesOnCurrentThread - missesBefore;

                log.Info($"[DYNDUNGEON] loot weenie warm-up: wcids={set.Wcids.Count} tierCap={tierCap} tables={set.Included.Count} loaded={loaded} alreadyCached={set.Wcids.Count - loaded} missing={missing.Count} " +
                         $"scrollSpells={scrolls.Spells} scrollWcids={scrolls.Wcids} scrollMissingSpells={scrolls.MissingSpells} scrollThrew={scrolls.Threw} scrollMs={scrolls.ElapsedMs} collectMs={collectMs} ms={watch.ElapsedMilliseconds}");

                if (missing.Count > 0)
                    log.Warn($"[DYNDUNGEON] loot weenie warm-up: {missing.Count} wcid(s) have no weenie in the world database: {string.Join(",", missing.Take(50))}{(missing.Count > 50 ? ",..." : "")}");

                if (log.IsDebugEnabled)
                {
                    log.Debug($"[DYNDUNGEON] loot weenie warm-up tables included: {string.Join(", ", set.Included)}");
                    log.Debug($"[DYNDUNGEON] loot weenie warm-up tables excluded: {string.Join(", ", set.Excluded)}");
                }
            }
            catch (Exception ex)
            {
                // A background task's exception is otherwise unobserved. The warm-up is an optimisation: failing it
                // costs the first clear its misses back, nothing else.
                log.Error("[DYNDUNGEON] loot weenie warm-up failed", ex);
            }
        }

        /// <summary>What <see cref="WarmScrolls"/> did.</summary>
        internal sealed class ScrollWarmSet
        {
            /// <summary>Distinct spell ids asked for (every level of every scroll spell the factory can roll).</summary>
            public int Spells;

            /// <summary>Distinct scroll wcids warmed through GetCachedWeenie.</summary>
            public int Wcids;

            /// <summary>Spell ids the world database has no scroll weenie for. A roll of one of these returns no scroll today too.</summary>
            public int MissingSpells;

            /// <summary>Spell ids whose warm-up threw (counted, not retried).</summary>
            public int Threw;

            public long ElapsedMs;
        }

        /// <summary>
        /// Warms the SCROLL path, which the wcid tables cannot reach (see the EXCLUDED note on <see cref="Collect"/>).
        ///
        /// LootGenerationFactory.CreateAndMutateWcid skips the wcid path for TreasureItemType.Scroll and calls
        /// CreateRandomScroll, which rolls a spell LEVEL (ScrollLevelChance, levels 1..7 across the tiers) and a spell
        /// from ScrollSpells.Table, asks GetScrollWeenie for the scroll weenie teaching that exact spell, and creates
        /// that weenie's own wcid. Both halves cost a world-database read on a miss, and both are warmed here: the
        /// scroll query per spell id, then GetCachedWeenie on the wcid it returned.
        ///
        /// The set is DERIVED, not guessed: every non-Undef entry of ScrollSpells.Table, which is exactly what the
        /// factory draws from (every level of every scroll spell, since a scroll level is a per-roll draw and any
        /// level is reachable at some tier). Duplicate spell ids are asked for once.
        /// </summary>
        /// <param name="getScrollWeenie">Production: DatabaseManager.World.GetScrollWeenie.</param>
        /// <param name="warmWcid">Production: GetCachedWeenie(wcid) != null. Returns whether the wcid has a weenie.</param>
        internal static ScrollWarmSet WarmScrolls(Func<uint, ACE.Entity.Models.Weenie> getScrollWeenie, Func<uint, bool> warmWcid)
        {
            var result = new ScrollWarmSet();
            var watch = Stopwatch.StartNew();

            var spellIds = new SortedSet<uint>();

            foreach (var levels in ScrollSpells.Table)
            {
                if (levels == null)
                    continue;

                foreach (var spell in levels)
                {
                    if (spell != ACE.Entity.Enum.SpellId.Undef)
                        spellIds.Add((uint)spell);
                }
            }

            var wcids = new HashSet<uint>();

            foreach (var spellId in spellIds)
            {
                result.Spells++;

                try
                {
                    var weenie = getScrollWeenie(spellId);

                    if (weenie == null)
                    {
                        result.MissingSpells++;
                        continue;
                    }

                    if (wcids.Add(weenie.WeenieClassId) && warmWcid(weenie.WeenieClassId))
                        result.Wcids++;
                }
                catch (Exception ex)
                {
                    result.Threw++;
                    log.Warn($"[DYNDUNGEON] loot weenie warm-up: scroll spell {spellId} threw", ex);
                }
            }

            result.ElapsedMs = watch.ElapsedMilliseconds;
            return result;
        }

        /// <summary>
        /// Every wcid the loot factory can hand WorldObjectFactory.CreateNewWorldObject for loot at tiers
        /// 1..<paramref name="tierCap"/>, plus <paramref name="extra"/>.
        ///
        /// INCLUDED, by reflection over static fields:
        ///   - every type in <see cref="WcidTableNamespace"/> (the per-item-type tables: armor, clothing, jewelry,
        ///     generic/art, weapons by skill and heritage including the Legacy ones, casters, mana stones, consumables,
        ///     heal kits, lockpicks, spell components, pet devices, society armor, cloaks, coalesced mana) - except
        ///     ScrollWcids, below;
        ///   - AetheriaWcids and GemMaterialChance, which live one namespace up in Factories.Tables;
        ///   - the two wcids LootGenerationFactory.RollWcid names inline: coinstack and the encapsulated spirit.
        /// A field typed List&lt;ChanceTable&lt;WeenieClassName&gt;&gt; is tier-indexed in every one of these tables
        /// (each is read as table[tier - 1]), so only its first <paramref name="tierCap"/> entries are taken; every
        /// other field is taken whole (its tier gating, if any, lives in a roll method reflection cannot see, so it is
        /// over-warmed rather than under-warmed).
        ///
        /// EXCLUDED:
        ///   - fields named _combined: all-tier reverse indexes built FROM the tables above, so they would add nothing
        ///     at the full tier cap and would defeat the tier filter below it;
        ///   - Factories.Tables types other than the two above: spell, cantrip, quality and chance tables carry no wcids;
        ///   - the rare tables (LootGenerationFactory_Rare): a Threads rare is rolled and created at KILL time, not
        ///     during delivery;
        ///   - Olthoi-play slag and glands: an Olthoi-killer ledger entry materialises nothing;
        ///   - ScrollWcids, the whole table, which the scroll path never reads: CreateRandomScroll picks a SPELL and
        ///     asks GetScrollWeenie for the scroll weenie teaching it, then creates THAT weenie's wcid
        ///     (LootGenerationFactory_Scroll.cs). Warming this table would warm mostly wcids no roll asks for, at over
        ///     a thousand entries. The scroll path is warmed instead by <see cref="WarmScrolls"/>, which walks the
        ///     spells the factory actually rolls and warms both halves of each one.
        /// </summary>
        internal static WarmSet Collect(int tierCap, IEnumerable<uint> extra)
        {
            var set = new WarmSet();
            var cap = Math.Clamp(tierCap, 1, MaxTier);

            var types = typeof(AetheriaWcids).Assembly.GetTypes()
                .Where(t => t.Namespace == WcidTableNamespace || (t.Namespace?.StartsWith(WcidTableNamespace + ".", StringComparison.Ordinal) ?? false))
                .Concat(new[] { typeof(AetheriaWcids), typeof(GemMaterialChance) })
                .Distinct()
                .OrderBy(t => t.FullName, StringComparer.Ordinal);

            foreach (var type in types)
            {
                if (type == typeof(ScrollWcids))
                {
                    set.Excluded.Add("ScrollWcids.* (the scroll path creates the wcid GetScrollWeenie returns, not one of these; warmed by WarmScrolls instead)");
                    continue;
                }

                var fields = type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).OrderBy(f => f.Name, StringComparer.Ordinal).ToList();

                // The per-tier tables a tier list indexes are ALSO their own named static fields (T1_T4_Chances and
                // friends), so the tier filter has to follow the table objects, not the field: a table any tier list
                // of this type references is walked only through a tier list index below the cap, never as a field.
                var tierOnly = new HashSet<object>(ReferenceEqualityComparer.Instance);

                foreach (var field in fields)
                {
                    if (!field.Name.StartsWith("_combined", StringComparison.Ordinal) && SafeGet(field) is List<ChanceTable<WeenieClassName>> tierList)
                    {
                        foreach (var table in tierList)
                        {
                            if (table != null)
                                tierOnly.Add(table);
                        }
                    }
                }

                foreach (var field in fields)
                {
                    var label = $"{type.Name}.{field.Name}";

                    if (field.Name.StartsWith("_combined", StringComparison.Ordinal))
                    {
                        set.Excluded.Add($"{label} (derived all-tier index)");
                        continue;
                    }

                    object value;

                    try
                    {
                        value = field.GetValue(null);
                    }
                    catch (Exception ex)
                    {
                        set.Excluded.Add($"{label} (unreadable: {ex.GetType().Name})");
                        continue;
                    }

                    var before = set.Wcids.Count;

                    if (value is List<ChanceTable<WeenieClassName>> tiers)
                    {
                        for (var tier = 0; tier < Math.Min(cap, tiers.Count); tier++)
                            Walk(tiers[tier], set.Wcids, 0, null);
                    }
                    else if (tierOnly.Contains(value))
                    {
                        // A per-tier table: reached through its tier list above, or not at all.
                        continue;
                    }
                    else
                    {
                        Walk(value, set.Wcids, 0, tierOnly);
                    }

                    if (set.Wcids.Count > before || ContainsAnyWcid(value, value is List<ChanceTable<WeenieClassName>> ? null : tierOnly))
                        set.Included.Add(label);
                }
            }

            set.Wcids.Add((uint)WeenieClassName.coinstack);
            set.Wcids.Add((uint)WeenieClassName.ace49485_encapsulatedspirit);
            set.Included.Add("LootGenerationFactory.RollWcid (coinstack, encapsulated spirit)");

            foreach (var wcid in extra ?? Enumerable.Empty<uint>())
            {
                if (wcid != 0)
                    set.Wcids.Add(wcid);
            }

            set.Included.Add("Threads delivery (cache chest, exit portal, trade note, salvage bags, salvage-affinity bases)");

            return set;
        }

        private static object SafeGet(FieldInfo field)
        {
            try
            {
                return field.GetValue(null);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>True when a field holds any wcid at all, so a table whose wcids were all already collected still reports as included.</summary>
        private static bool ContainsAnyWcid(object value, ISet<object> skip)
        {
            var probe = new SortedSet<uint>();
            Walk(value, probe, 0, skip);
            return probe.Count > 0;
        }

        /// <summary>
        /// Collects every WeenieClassName (and GemResult.ClassName) reachable from <paramref name="value"/> through lists,
        /// tuples and dictionaries, never descending into an object in <paramref name="skip"/> (the per-tier tables).
        /// </summary>
        private static void Walk(object value, ISet<uint> into, int depth, ISet<object> skip)
        {
            if (value == null || depth > 6 || (skip != null && !(value is ValueType) && skip.Contains(value)))
                return;

            switch (value)
            {
                case WeenieClassName wcid:
                    if (wcid != WeenieClassName.undef)
                        into.Add((uint)wcid);
                    return;

                case GemResult gem:
                    if (gem.ClassName != WeenieClassName.undef)
                        into.Add((uint)gem.ClassName);
                    return;

                case string _:
                    return;

                case ITuple tuple:
                    for (var i = 0; i < tuple.Length; i++)
                        Walk(tuple[i], into, depth + 1, skip);
                    return;

                case IDictionary dictionary:
                    foreach (DictionaryEntry kv in dictionary)
                    {
                        Walk(kv.Key, into, depth + 1, skip);
                        Walk(kv.Value, into, depth + 1, skip);
                    }
                    return;

                case IEnumerable sequence:
                    foreach (var item in sequence)
                        Walk(item, into, depth + 1, skip);
                    return;
            }
        }
    }
}
