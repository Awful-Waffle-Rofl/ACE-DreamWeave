using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using ACE.Database;
using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.Factories.Tables;
using ACE.Server.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The scroll half of the Threads loot warm-up, and the miss attribution that found it (2026-09-19).
    ///
    /// A loot scroll is not created from a wcid table: LootGenerationFactory.CreateAndMutateWcid skips the wcid path
    /// for TreasureItemType.Scroll and CreateRandomScroll asks GetScrollWeenie for the weenie teaching a rolled spell,
    /// then creates THAT weenie's wcid. Both halves miss their own cache, so both are warmed.
    ///
    /// No database and no PropertyManager key is touched: WarmScrolls takes both world-database reads as seams, and
    /// the miss attribution is a static, process-wide counter with its own public record API.
    /// </summary>
    [TestClass]
    public class ThreadScrollWarmupTests
    {
        private static Weenie ScrollWeenie(uint wcid) => new Weenie { WeenieClassId = wcid, WeenieType = WeenieType.Scroll };

        /// <summary>Every non-Undef spell id of ScrollSpells.Table: exactly what CreateRandomScroll can draw.</summary>
        private static SortedSet<uint> TableSpellIds()
        {
            var ids = new SortedSet<uint>();

            foreach (var levels in ScrollSpells.Table.Where(l => l != null))
            {
                foreach (var spell in levels.Where(s => s != SpellId.Undef))
                    ids.Add((uint)spell);
            }

            return ids;
        }

        [TestMethod]
        public void The_warm_up_asks_for_every_scroll_spell_the_factory_can_roll_once_and_warms_the_wcid_each_one_resolves_to()
        {
            var expected = TableSpellIds();
            Assert.IsTrue(expected.Count > 100, $"fixture: ScrollSpells.Table carries the whole scroll set ({expected.Count} spell ids)");

            var asked = new List<uint>();
            var warmed = new List<uint>();

            // Two spell ids share a wcid, so the wcid count is the DISTINCT one: the second ask is already cached.
            var result = ThreadLootWeenieWarmup.WarmScrolls(
                spellId => { asked.Add(spellId); return ScrollWeenie(500000 + spellId % 3); },
                wcid => { warmed.Add(wcid); return true; });

            CollectionAssert.AreEqual(expected.ToList(), asked, "every scroll spell, once, in a stable order");
            Assert.AreEqual(expected.Count, result.Spells);
            Assert.AreEqual(3, result.Wcids);
            CollectionAssert.AreEqual(new List<uint> { 500000, 500001, 500002 }, warmed.OrderBy(w => w).ToList());
            Assert.AreEqual(warmed.Count, warmed.Distinct().Count(), "a wcid is warmed once, not once per spell");
            Assert.AreEqual(0, result.MissingSpells);
            Assert.AreEqual(0, result.Threw);

            Console.WriteLine($"scroll warm set: spells={result.Spells} wcids={result.Wcids}");
        }

        [TestMethod]
        public void A_spell_with_no_scroll_weenie_or_a_read_that_throws_is_counted_and_the_rest_are_still_warmed()
        {
            var total = TableSpellIds().Count;
            var warmed = 0;
            var seen = 0;

            var result = ThreadLootWeenieWarmup.WarmScrolls(
                spellId =>
                {
                    seen++;
                    if (seen == 1) return null;
                    if (seen == 2) throw new InvalidOperationException("world db read failed");
                    return ScrollWeenie(600000 + spellId);
                },
                _ => { warmed++; return true; });

            Assert.AreEqual(total, result.Spells, "the walk finishes");
            Assert.AreEqual(1, result.MissingSpells);
            Assert.AreEqual(1, result.Threw);
            Assert.AreEqual(total - 2, result.Wcids);
            Assert.AreEqual(total - 2, warmed);
        }

        [TestMethod]
        public void The_warm_up_now_walks_the_scroll_path_and_the_scroll_cache_it_writes_is_concurrent()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadLootWeenieWarmup.cs");
            var code = string.Join("\n", src.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal) && !l.TrimStart().StartsWith("///", StringComparison.Ordinal)));

            StringAssert.Contains(code, "WarmScrolls(DatabaseManager.World.GetScrollWeenie, wcid => DatabaseManager.World.GetCachedWeenie(wcid) != null)");
            StringAssert.Contains(code, "scrollSpells={scrolls.Spells}", "the warm-up line reports the scroll counts separately");
            StringAssert.Contains(code, "scrollMs={scrolls.ElapsedMs}");
            StringAssert.Contains(code, "warmed by WarmScrolls instead", "the excluded-tables log line no longer states the scroll exclusion as permanent");
            StringAssert.Contains(src, "ScrollWcids, the whole table, which the scroll path never reads", "and neither does the doc it points at");

            // The background task writes scrollsBySpellID through GetScrollWeenie, and the landblock threads already
            // did; a plain Dictionary there was a real race.
            var db = PooledLootSourceText.Read("Source/ACE.Database/WorldDatabaseWithEntityCache.cs");
            StringAssert.Contains(db, "private readonly ConcurrentDictionary<uint /* Spell ID */, ACE.Entity.Models.Weenie> scrollsBySpellID");

            var scroll = PooledLootSourceText.Read("Source/ACE.Server/Factories/LootGenerationFactory_Scroll.cs");
            StringAssert.Contains(scroll, "DatabaseManager.World.GetScrollWeenie((uint)spellId)", "the path the warm-up mirrors");
            StringAssert.Contains(scroll, "WorldObjectFactory.CreateNewWorldObject(weenie.WeenieClassId)", "and its second half");
        }

        [TestMethod]
        public void A_scroll_weenie_read_that_misses_is_counted_separately_from_the_wcid_cache()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Database/WorldDatabaseWithEntityCache.cs");
            var body = PooledLootSourceText.MethodBody(src, "public ACE.Entity.Models.Weenie GetScrollWeenie(uint spellID)");

            var miss = body.IndexOf("if (!scrollsBySpellID.TryGetValue(spellID, out var weenie))", StringComparison.Ordinal);
            var count = body.IndexOf("System.Threading.Interlocked.Increment(ref scrollWeenieMissTotal);", miss, StringComparison.Ordinal);
            var thread = body.IndexOf("scrollWeenieMissesOnThread++;", count, StringComparison.Ordinal);
            var read = body.IndexOf("new WorldDbContext()", thread, StringComparison.Ordinal);

            Assert.IsTrue(miss >= 0 && count > miss && thread > count && read > thread, "counted on the miss path, before the query");

            var placer = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadCachePlacer.cs");
            StringAssert.Contains(placer, "scrollMiss={scrollMisses}", "the delivery finish line reports them");
            StringAssert.Contains(placer, "dynamic_dungeons_loot_miss_attribution");
        }

        // ------------------------------------------------------------------------------------------------------
        // Miss attribution
        // ------------------------------------------------------------------------------------------------------

        [TestMethod]
        public void Miss_attribution_counts_per_wcid_ranks_the_worst_first_and_can_be_switched_off()
        {
            var enabled = WorldDatabaseWithEntityCache.MissAttributionEnabled;

            try
            {
                WorldDatabaseWithEntityCache.ResetMissAttribution();
                WorldDatabaseWithEntityCache.MissAttributionEnabled = true;

                for (var i = 0; i < 5; i++) WorldDatabaseWithEntityCache.RecordMissedWcid(31000);
                for (var i = 0; i < 9; i++) WorldDatabaseWithEntityCache.RecordMissedWcid(31001);
                WorldDatabaseWithEntityCache.RecordMissedWcid(31002);

                var top = WorldDatabaseWithEntityCache.TopMissedWcids(2);
                CollectionAssert.AreEqual(new List<(uint, int)> { (31001u, 9), (31000u, 5) }, top);
                Assert.AreEqual(3, WorldDatabaseWithEntityCache.MissedWcidCount);
                Assert.AreEqual(0, WorldDatabaseWithEntityCache.TopMissedWcids(0).Count);

                WorldDatabaseWithEntityCache.MissAttributionEnabled = false;
                WorldDatabaseWithEntityCache.RecordMissedWcid(31003);
                Assert.AreEqual(3, WorldDatabaseWithEntityCache.MissedWcidCount, "off records nothing new");

                WorldDatabaseWithEntityCache.ResetMissAttribution();
                Assert.AreEqual(0, WorldDatabaseWithEntityCache.MissedWcidCount);
                Assert.AreEqual(0L, WorldDatabaseWithEntityCache.MissedWcidOverflow);
            }
            finally
            {
                WorldDatabaseWithEntityCache.MissAttributionEnabled = enabled;
                WorldDatabaseWithEntityCache.ResetMissAttribution();
            }
        }

        [TestMethod]
        public void Miss_attribution_is_bounded_and_safe_from_many_threads_at_once()
        {
            var enabled = WorldDatabaseWithEntityCache.MissAttributionEnabled;

            try
            {
                WorldDatabaseWithEntityCache.ResetMissAttribution();
                WorldDatabaseWithEntityCache.MissAttributionEnabled = true;

                const int threads = 8;
                const int perThread = 4000;
                var slack = threads; // the Count check races, so a full dictionary can admit one more per thread in flight

                // Far more distinct wcids than the capacity, plus one wcid every thread hammers.
                Parallel.For(0, threads, t =>
                {
                    for (var i = 0; i < perThread; i++)
                    {
                        WorldDatabaseWithEntityCache.RecordMissedWcid((uint)(1_000_000 + t * perThread + i));
                        WorldDatabaseWithEntityCache.RecordMissedWcid(7);
                    }
                });

                var tracked = WorldDatabaseWithEntityCache.MissedWcidCount;

                Assert.IsTrue(tracked <= WorldDatabaseWithEntityCache.MissAttributionCapacity + slack,
                    $"{tracked} tracked against a capacity of {WorldDatabaseWithEntityCache.MissAttributionCapacity}");
                Assert.IsTrue(tracked >= WorldDatabaseWithEntityCache.MissAttributionCapacity - slack, $"the bound is reached, not undershot ({tracked})");
                Assert.IsTrue(WorldDatabaseWithEntityCache.MissedWcidOverflow > 0, "the wcids past the bound are still counted in total");

                var hot = WorldDatabaseWithEntityCache.TopMissedWcids(1).Single();
                Assert.AreEqual(7u, hot.Wcid);
                Assert.AreEqual(threads * perThread, hot.Misses, "no count lost to a race");

                Console.WriteLine($"tracked={tracked} overflow={WorldDatabaseWithEntityCache.MissedWcidOverflow} hot={hot.Misses}");
            }
            finally
            {
                WorldDatabaseWithEntityCache.MissAttributionEnabled = enabled;
                WorldDatabaseWithEntityCache.ResetMissAttribution();
            }
        }

        [TestMethod]
        public void A_wcid_cache_miss_records_which_wcid_missed()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Database/WorldDatabaseWithEntityCache.cs");
            var body = PooledLootSourceText.MethodBody(src, "public ACE.Entity.Models.Weenie GetCachedWeenie(uint weenieClassId)");

            var thread = body.IndexOf("weenieCacheMissesOnThread++;", StringComparison.Ordinal);
            var record = body.IndexOf("RecordMissedWcid(weenieClassId);", thread, StringComparison.Ordinal);
            var load = body.IndexOf("GetWeenie(weenieClassId);", record, StringComparison.Ordinal);

            Assert.IsTrue(thread >= 0 && record > thread && load > record, "recorded on the miss path, before the DB read");
        }
    }
}
