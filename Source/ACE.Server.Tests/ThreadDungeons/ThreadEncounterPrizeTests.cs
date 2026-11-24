using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

using ACE.Server.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The shared encounter prize (ThreadEncounterPrize, encounters.json "prize"): the shipped table, its parser, the pure
    /// roll (the 5% special-gem rate, skip-missing renormalisation, uniform salvage bag count and material draws), the player text, and
    /// source-scan pins for the Defense and Jump hooks, the TryStart stamp and the delivery path. Delivery into a live
    /// pack needs a Player and a loaded landblock and is left to the in-game checks. Reads no PropertyManager key.
    /// </summary>
    [TestClass]
    public class ThreadEncounterPrizeTests
    {
        private const int Samples = 200_000;

        private static readonly uint[] SpecialGems = { 1007900, 1007901, 1008000 };

        /// <summary>Salvaged Amethyst, Green Jade, Obsidian, Tourmaline (owner ruling 2026-11-10).</summary>
        private static readonly uint[] SalvagePool = { 21036, 21051, 21063, 21082 };

        private const uint FiberWcid = 1007000, PatternWcid = 1007003;

        private static readonly ACE.Server.Knots.PinnacleDef[] TwoPinnacles =
        {
            new ACE.Server.Knots.PinnacleDef { Key = "p1", Name = "P One" },
            new ACE.Server.Knots.PinnacleDef { Key = "p2", Name = "P Two" },
        };

        private static ThreadEncounterPrizeTable Shipped()
        {
            var store = ThreadDungeonStoreTests.LoadShipped();
            var diagnostics = store.Diagnostics.Where(d => d.StartsWith("encounters.json", StringComparison.Ordinal)).ToList();
            Assert.AreEqual(0, diagnostics.Count, string.Join("\n", diagnostics));
            return store.Encounters.Prize;
        }

        private static ThreadEncounterPrizeTable Parse(string text, List<string> diagnostics)
        {
            using var doc = JsonDocument.Parse(text);
            return ThreadEncounterPrizeTable.Parse(doc.RootElement.Clone(), "encounters.json", diagnostics);
        }

        /// <summary>Roll counts by wcid over <paramref name="n"/> seeded draws.</summary>
        private static Dictionary<uint, int> Tally(IReadOnlyList<ThreadEncounterPrizeEntry> entries, Func<uint, bool> exists, int seed, int n, List<uint> missing = null,
            int? fiberLevel = 100, IReadOnlyList<ACE.Server.Knots.PinnacleDef> patternPool = null)
        {
            var rng = new Random(seed);
            var counts = new Dictionary<uint, int>();

            for (var i = 0; i < n; i++)
            {
                var pick = ThreadEncounterPrize.Roll(entries, exists, rng, missing, fiberLevel, patternPool ?? TwoPinnacles);
                Assert.IsNotNull(pick);
                counts.TryGetValue(pick.Wcid, out var c);
                counts[pick.Wcid] = c + 1;
            }

            return counts;
        }

        private static double Rate(Dictionary<uint, int> counts, uint wcid, int n) => counts.TryGetValue(wcid, out var c) ? c / (double)n : 0.0;

        // ---- the shipped table ------------------------------------------------------------------------------------

        [TestMethod]
        public void The_shipped_table_carries_the_owner_rulings()
        {
            var table = Shipped();

            CollectionAssert.AreEqual(new[] { "mantle_gem", "amplifier_gem", "aspect_gem", "pattern", "trade_note_250k", "diamond_scarab", "promissory_note", "salvage_bags", "fiber" }
                    .Concat(ACE.Server.ThreadDungeons.FoolproofSalvage.Wcids.Select(w => "foolproof_" + w)).ToList(),
                table.Entries.Select(e => e.Id).ToList());

            foreach (var gem in SpecialGems)
            {
                var entry = table.Entries.Single(e => e.Wcids.Contains(gem));
                Assert.AreEqual(5.0, entry.Weight, $"special gem {gem} weighs 5 (5%)");
                Assert.AreEqual(1, entry.Qty);
                Assert.AreEqual(1, entry.Wcids.Count);
            }

            // Owner rulings 2026-11-11: a Pattern is a 3% special; the other five (a Fiber added) split the remaining 82%.
            var pattern = table.Entries.Single(e => e.Id == "pattern");
            Assert.IsTrue(pattern.IsPattern);
            Assert.AreEqual(3.0, pattern.Weight);
            CollectionAssert.AreEqual(new[] { PatternWcid }, pattern.Wcids.ToList());

            var fiber = table.Entries.Single(e => e.Id == "fiber");
            Assert.IsTrue(fiber.IsFiber);
            Assert.AreEqual(1, fiber.Qty);
            CollectionAssert.AreEqual(new[] { FiberWcid }, fiber.Wcids.ToList());

            var foolproof = table.Entries.Where(e => e.Id.StartsWith("foolproof_", StringComparison.Ordinal)).ToList();
            Assert.AreEqual(14, foolproof.Count);
            Assert.IsTrue(foolproof.All(e => e.Weight == 0.0721 && e.Qty == 1 && e.Wcids.Count == 1), "Foolproof salvage: 14 single bags, about 1% together");

            var others = table.Entries.Where(e => !e.Wcids.Any(w => SpecialGems.Contains(w)) && !e.IsPattern && !foolproof.Contains(e)).ToList();
            Assert.AreEqual(5, others.Count);
            Assert.IsTrue(others.All(e => e.Weight == 16.4), "the remaining 82% is split equally over the other five");
            Assert.AreEqual(100.0, table.Entries.Where(e => !foolproof.Contains(e)).Sum(e => e.Weight), 1e-9, "the original entries are percents of a full table");
            Assert.AreEqual(1.01, foolproof.Sum(e => e.Weight), 0.001, "the Foolproof bags add about 1% on top");

            Assert.AreEqual(5, table.Entries.Single(e => e.Id == "trade_note_250k").Qty);
            CollectionAssert.AreEqual(new uint[] { 20630 }, table.Entries.Single(e => e.Id == "trade_note_250k").Wcids.ToList());
            Assert.AreEqual(5, table.Entries.Single(e => e.Id == "diamond_scarab").Qty);
            CollectionAssert.AreEqual(new uint[] { 7299 }, table.Entries.Single(e => e.Id == "diamond_scarab").Wcids.ToList());
            Assert.AreEqual(5, table.Entries.Single(e => e.Id == "promissory_note").Qty);
            CollectionAssert.AreEqual(new uint[] { 43901 }, table.Entries.Single(e => e.Id == "promissory_note").Wcids.ToList());

            // Owner ruling 2026-11-10: the hammers are gone, replaced by 2-4 full salvage bags at the same weight.
            var bags = table.Entries.Single(e => e.Id == "salvage_bags");
            Assert.IsTrue(bags.IsSalvage);
            CollectionAssert.AreEqual(SalvagePool, bags.Wcids.ToList(), "Amethyst, Green Jade, Obsidian, Tourmaline");
            Assert.AreEqual(2, bags.Salvage.BagsMin);
            Assert.AreEqual(4, bags.Salvage.BagsMax);
            Assert.AreEqual(100, bags.Salvage.Structure, "every bag is full");
            Assert.AreEqual(10, bags.Salvage.Workmanship);
            Assert.AreEqual(1, table.Entries.Count(e => e.IsSalvage));
            Assert.IsFalse(table.Entries.SelectMany(e => e.Wcids).Any(w => (w >= 1001910 && w <= 1001919) || w == 1008001), "no salvage hammer, sealed wrapper or Green Jade Hammer is a prize");
        }

        // ---- the roll ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void Each_special_gem_rolls_at_five_percent()
        {
            var counts = Tally(Shipped().Entries, _ => true, seed: 20261109, n: Samples);

            foreach (var gem in SpecialGems)
            {
                var rate = Rate(counts, gem, Samples);
                Assert.AreEqual(0.05, rate, 0.002, $"special gem {gem} rolled at {rate:P3}");
            }

            Assert.AreEqual(0.03, Rate(counts, PatternWcid, Samples), 0.002, "the Pattern");

            foreach (var wcid in new uint[] { 20630, 7299, 43901, FiberWcid })
                Assert.AreEqual(0.164, Rate(counts, wcid, Samples), 0.004, $"wcid {wcid}");

            // Tally keys a pick by its FIRST wcid, so the salvage entry's rate is the sum over its pool.
            var salvageRate = SalvagePool.Sum(w => Rate(counts, w, Samples));
            Assert.AreEqual(0.164, salvageRate, 0.004, "the salvage entry as a whole");
        }

        [TestMethod]
        public void A_missing_wcid_is_skipped_and_the_weights_renormalise()
        {
            var entries = Shipped().Entries;
            var missing = new List<uint>();

            // Every prize weenie exists on master; the Aspect Gem and the Green Jade bag are taken away here to exercise renormalisation.
            Func<uint, bool> exists = w => w != 1008000 && w != 21051;
            var counts = Tally(entries, exists, seed: 77, n: Samples, missing: missing);

            Assert.AreEqual(0.0, Rate(counts, 1008000, Samples), "a missing gem never rolls");
            Assert.AreEqual(0.0, Rate(counts, 21051, Samples), "a missing salvage bag never rolls");
            CollectionAssert.IsSubsetOf(new uint[] { 1008000, 21051 }, missing.Distinct().ToList(), "each missing wcid is reported");
            Assert.IsFalse(missing.Any(w => w != 1008000 && w != 21051), "only missing wcids are reported");

            // With one gem gone, 95 of weight is left: each remaining gem is 5/95, each other entry 16.4/95.
            Assert.AreEqual(5.0 / 95.0, Rate(counts, 1007900, Samples), 0.002, "mantle renormalised");
            Assert.AreEqual(5.0 / 95.0, Rate(counts, 1007901, Samples), 0.002, "amplifier renormalised");
            Assert.AreEqual(16.4 / 95.0, Rate(counts, 20630, Samples), 0.004, "trade notes renormalised");

            var salvageRate = SalvagePool.Sum(w => Rate(counts, w, Samples));
            Assert.AreEqual(16.4 / 95.0, salvageRate, 0.004, "the salvage entry keeps its full weight while any bag exists");
        }

        // ---- the Knots entries (owner rulings 2026-11-11) ----------------------------------------------------------

        [TestMethod]
        public void A_run_without_a_knot_level_skips_the_fiber_and_the_rest_renormalise()
        {
            var counts = Tally(Shipped().Entries, _ => true, seed: 31, n: Samples, fiberLevel: null);

            Assert.AreEqual(0.0, Rate(counts, FiberWcid, Samples), "no level, no Fiber");
            Assert.AreEqual(5.0 / 83.6, Rate(counts, 1007900, Samples), 0.002, "a gem renormalised over the 83.6 left");
            Assert.AreEqual(16.4 / 83.6, Rate(counts, 20630, Samples), 0.004);

            Assert.AreEqual(0, Tally(Shipped().Entries, _ => true, seed: 31, n: 2000, fiberLevel: 0).GetValueOrDefault(FiberWcid), "a level of 0 is no level");
        }

        [TestMethod]
        public void An_empty_pattern_pool_skips_the_pattern_and_the_rest_renormalise()
        {
            var counts = Tally(Shipped().Entries, _ => true, seed: 32, n: Samples, patternPool: Array.Empty<ACE.Server.Knots.PinnacleDef>());

            Assert.AreEqual(0.0, Rate(counts, PatternWcid, Samples), "no pinnacle the Knots drop could roll, no Pattern");
            Assert.AreEqual(5.0 / 97.0, Rate(counts, 1007900, Samples), 0.002);
            Assert.AreEqual(16.4 / 97.0, Rate(counts, FiberWcid, Samples), 0.004);

            // A missing Pattern weenie is the ordinary missing-wcid path.
            var missing = new List<uint>();
            Assert.AreEqual(0, Tally(Shipped().Entries, w => w != PatternWcid, seed: 33, n: 2000, missing: missing).GetValueOrDefault(PatternWcid));
            CollectionAssert.Contains(missing, PatternWcid);
        }

        [TestMethod]
        public void A_fiber_pick_carries_the_runs_level_and_one_object_per_fiber()
        {
            var one = new ThreadEncounterPrizeEntry("fiber", new[] { FiberWcid }, 1, 1, fiber: true);
            var pick = ThreadEncounterPrize.Roll(new[] { one }, _ => true, new Random(1), null, fiberLevel: 137);
            Assert.AreEqual(137, pick.FiberLevel);
            CollectionAssert.AreEqual(new[] { FiberWcid }, pick.Wcids.ToList());
            Assert.IsNull(pick.Pattern);

            var three = new ThreadEncounterPrizeEntry("fibers", new[] { FiberWcid }, 3, 1, fiber: true);
            CollectionAssert.AreEqual(new[] { FiberWcid, FiberWcid, FiberWcid }, ThreadEncounterPrize.Roll(new[] { three }, _ => true, new Random(1), null, 90).Wcids.ToList(), "a Fiber does not stack");

            Assert.IsNull(ThreadEncounterPrize.Roll(new[] { one }, _ => true, new Random(1), null, null), "the only entry needs a level");
        }

        [TestMethod]
        public void A_pattern_pick_draws_its_pinnacle_uniformly_from_the_pool()
        {
            var entry = new ThreadEncounterPrizeEntry("pattern", new[] { PatternWcid }, 1, 1, pattern: true);
            var pool = new[]
            {
                new ACE.Server.Knots.PinnacleDef { Key = "a", Name = "A" },
                new ACE.Server.Knots.PinnacleDef { Key = "b", Name = "B" },
                new ACE.Server.Knots.PinnacleDef { Key = "c", Name = "C" },
            };
            var rng = new Random(44);
            var byKey = new Dictionary<string, int>();

            for (var i = 0; i < 30000; i++)
            {
                var pick = ThreadEncounterPrize.Roll(new[] { entry }, _ => true, rng, null, null, pool);
                CollectionAssert.AreEqual(new[] { PatternWcid }, pick.Wcids.ToList());
                byKey[pick.Pattern.Key] = byKey.GetValueOrDefault(pick.Pattern.Key) + 1;
            }

            foreach (var key in new[] { "a", "b", "c" })
                Assert.AreEqual(1.0 / 3.0, byKey[key] / 30000.0, 0.015, key);

            Assert.IsNull(ThreadEncounterPrize.Roll(new[] { entry }, _ => true, new Random(1), null, null, null), "no pool, no Pattern");
        }

        [TestMethod]
        public void Fiber_and_pattern_blocks_parse_and_a_bad_one_costs_only_itself()
        {
            var diagnostics = new List<string>();
            var table = Parse("{\"entries\":["
                + "{\"id\":\"fiber\",\"fiber\":{\"qty\":2},\"weight\":16.4},"
                + "{\"id\":\"pattern\",\"pattern\":{},\"weight\":3},"
                + "{\"id\":\"fiber_wcids\",\"wcids\":[1],\"fiber\":{}},"
                + "{\"id\":\"fiber_qty_beside\",\"qty\":2,\"fiber\":{}},"
                + "{\"id\":\"fiber_zero\",\"fiber\":{\"qty\":0}},"
                + "{\"id\":\"fiber_many\",\"fiber\":{\"qty\":11}},"
                + "{\"id\":\"pattern_keys\",\"pattern\":{\"key\":\"x\"}},"
                + "{\"id\":\"pattern_qty\",\"qty\":2,\"pattern\":{}},"
                + "{\"id\":\"pattern_wcids\",\"wcids\":[5],\"pattern\":{}},"
                + "{\"id\":\"both\",\"pattern\":{},\"fiber\":{}}"
                + "]}", diagnostics);

            CollectionAssert.AreEqual(new[] { "fiber", "pattern" }, table.Entries.Select(e => e.Id).ToList());
            var fiber = table.Entries[0];
            Assert.IsTrue(fiber.IsFiber && !fiber.IsPattern && !fiber.IsSalvage);
            Assert.AreEqual(2, fiber.Qty);
            Assert.AreEqual(16.4, fiber.Weight, "a decimal weight parses");
            CollectionAssert.AreEqual(new[] { FiberWcid }, fiber.Wcids.ToList());

            var pattern = table.Entries[1];
            Assert.IsTrue(pattern.IsPattern && !pattern.IsFiber);
            CollectionAssert.AreEqual(new[] { PatternWcid }, pattern.Wcids.ToList());

            Assert.AreEqual(8, diagnostics.Count, string.Join("\n", diagnostics));
            foreach (var (id, fragment) in new[]
            {
                ("fiber_wcids", "names no wcids"), ("fiber_qty_beside", "qty goes inside fiber"), ("fiber_zero", "fiber.qty 0 must be 1 to 10"),
                ("fiber_many", "fiber.qty 11"), ("pattern_keys", "pattern must be an empty object"), ("pattern_qty", "qty must be 1"),
                ("pattern_wcids", "names no wcids"), ("both", "at most one of salvage, fiber or pattern"),
            })
                Assert.IsTrue(diagnostics.Any(d => d.Contains($"'{id}'") && d.Contains(fragment)), $"{id}: {fragment}\n" + string.Join("\n", diagnostics));
        }

        [TestMethod]
        public void Knots_prizes_are_built_by_the_knots_seams_at_the_runs_level_and_pool()
        {
            var prize = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadEncounterPrize.cs");
            var grant = PooledLootSourceText.MethodBody(prize, "public static bool Grant(ThreadDungeonRun run, Player player, string encounterKind, int bonusWaves)");
            var deliver = PooledLootSourceText.MethodBody(prize, "internal static List<(WorldObject Item, string Label)> BuildObjects(ThreadEncounterPrizePick pick, int bonusWaves, Random rng)");

            // The level is the run's Knot level (the stamp KnotDrops.RollFor reads), the pool the Knots drop's own.
            StringAssert.Contains(grant, "var (fiberLevel, patternPool) = KnotsInputs(run);");
            StringAssert.Contains(grant, "Roll(TableSource()?.Entries, WeenieExists, Random.Shared, missing, fiberLevel, patternPool);");
            StringAssert.Contains(prize, "PatternPool = () => Knots.KnotPinnacles.Available(ThreadDungeonManager.Store);");
            StringAssert.Contains(prize, "=> Knots.KnotDrops.KnotsAvailableFor(run)");

            // KnotDrops' seams stamp KnotLevel / PinnacleKey and the names; nothing here re-implements that.
            StringAssert.Contains(deliver, "Knots.KnotDrops.FiberFactory(pick.FiberLevel)");
            StringAssert.Contains(deliver, "Knots.KnotDrops.PatternFactory(pick.Pattern)");
            Assert.IsFalse(prize.Contains("PropertyInt.KnotLevel") || prize.Contains("PropertyString.PinnacleKey"), "no re-implemented stamping");
        }

        private static ThreadDungeonRun KnotsRun(int? level = 150, bool pooled = true, int spawned = 10)
        {
            var spec = new DungeonGemSpec("filos_doom", 200, 6, "any", 1, new (string, double)[0], 0, 0);
            var dungeon = new ACE.Server.ThreadDungeons.Defs.DungeonEntryDef { Id = "filos_doom", Landblock = 0x0150, ExitPortalWcid = 1003601 };
            var run = new ThreadDungeonRun(0x80001234u, 0x50000001u, "Tester", 7, 0x80000099u, spec, dungeon, DateTime.UtcNow, TimeSpan.FromMinutes(180));
            run.MarkKnotLevel(level);
            run.MarkPooledLoot(pooled);
            run.MarkPopulated(spawned, spawned, 0);
            return run;
        }

        [TestMethod]
        public void The_knots_prizes_follow_the_knot_drops_own_gate()
        {
            // Code review 2026-11-11: dynamic_dungeons_fiber_enabled ships false, and while it is off (or the run fails
            // KnotDrops.ShouldRoll) the prize hands out no Fiber and no Pattern; both entries leave the roll.
            var key = ACE.Server.Knots.KnotTunables.FiberEnabledKey;

            try
            {
                Assert.IsTrue(ACE.Server.Managers.PropertyManager.ModifyBool(key, false));
                var off = ThreadEncounterPrize.KnotsInputs(KnotsRun());
                Assert.IsNull(off.FiberLevel, "switch off: no Fiber level");
                Assert.AreEqual(0, off.PatternPool.Count, "switch off: no Pattern pool");

                Assert.IsTrue(ACE.Server.Managers.PropertyManager.ModifyBool(key, true));
                Assert.AreEqual(150, ThreadEncounterPrize.KnotsInputs(KnotsRun()).FiberLevel, "switch on: the run's Knot level");
                Assert.IsTrue(ACE.Server.Knots.KnotDrops.KnotsAvailableFor(KnotsRun()));

                // The run-state half of the gate, with the switch on.
                Assert.IsNull(ThreadEncounterPrize.KnotsInputs(KnotsRun(level: null)).FiberLevel, "no Knot level");
                Assert.IsNull(ThreadEncounterPrize.KnotsInputs(KnotsRun(pooled: false)).FiberLevel, "not pooled loot");
                Assert.IsNull(ThreadEncounterPrize.KnotsInputs(KnotsRun(spawned: 0)).FiberLevel, "nothing spawned");
                Assert.IsNull(ThreadEncounterPrize.KnotsInputs(null).FiberLevel);

                // With the gate closed the roll renormalises over the other seven entries.
                var closed = ThreadEncounterPrize.KnotsInputs(KnotsRun(pooled: false));
                var counts = Tally(Shipped().Entries, _ => true, seed: 41, n: Samples, fiberLevel: closed.FiberLevel, patternPool: closed.PatternPool);
                Assert.AreEqual(0.0, Rate(counts, FiberWcid, Samples));
                Assert.AreEqual(0.0, Rate(counts, PatternWcid, Samples));
                Assert.AreEqual(5.0 / 80.6, Rate(counts, 1007900, Samples), 0.002);
            }
            finally
            {
                ACE.Server.Managers.PropertyManager.ModifyBool(key, ACE.Server.Knots.KnotTunables.DefaultFiberEnabled);
            }
        }

        [TestMethod]
        public void A_fiber_line_counts_the_fibers_delivered_and_an_item_line_its_stack()
        {
            var fibers = new ThreadEncounterPrizeEntry("fiber", new[] { FiberWcid }, 3, 1, fiber: true);
            Assert.AreEqual("Encounter prize: 3 Fiber (Level 120).", ThreadEncounterPrize.ItemDeliveryText(fibers, 1, 3, "Fiber (Level 120)", false));
            Assert.AreEqual("Encounter prize: 2 Fiber (Level 120).", ThreadEncounterPrize.ItemDeliveryText(fibers, 1, 2, "Fiber (Level 120)", false), "one of three failed to place");
            Assert.AreEqual("Encounter prize: Fiber (Level 120).", ThreadEncounterPrize.ItemDeliveryText(new ThreadEncounterPrizeEntry("fiber", new[] { FiberWcid }, 1, 1, fiber: true), 1, 1, "Fiber (Level 120)", false));

            var notes = new ThreadEncounterPrizeEntry("notes", new uint[] { 20630 }, 5, 1);
            Assert.AreEqual("Encounter prize: 5 Trade Note (250,000).", ThreadEncounterPrize.ItemDeliveryText(notes, 5, 1, "Trade Note (250,000)", false), "an item line counts its stack");

            var pattern = new ThreadEncounterPrizeEntry("pattern", new[] { PatternWcid }, 1, 1, pattern: true);
            Assert.AreEqual("Encounter prize: Pattern: P One.", ThreadEncounterPrize.ItemDeliveryText(pattern, 1, 1, "Pattern: P One", false));

            var src = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadEncounterPrize.cs");
            StringAssert.Contains(src, "ItemDeliveryText(pick.Entry, qty, materials.Count, name, anyAtFeet)");
        }

        [TestMethod]
        public void Nothing_available_rolls_null_and_weight_zero_never_rolls()
        {
            var entries = new[]
            {
                new ThreadEncounterPrizeEntry("gone", new uint[] { 1 }, 1, 50),
                new ThreadEncounterPrizeEntry("off", new uint[] { 2 }, 1, 0),
            };

            Assert.IsNull(ThreadEncounterPrize.Roll(entries, w => w == 2, new Random(1)), "the only weighted entry is missing, the other weighs 0");
            Assert.IsNull(ThreadEncounterPrize.Roll(Array.Empty<ThreadEncounterPrizeEntry>(), _ => true, new Random(1)));
            Assert.IsNull(ThreadEncounterPrize.Roll(null, _ => true, new Random(1)));

            var withLive = entries.Append(new ThreadEncounterPrizeEntry("live", new uint[] { 3 }, 4, 1)).ToList();
            var counts = Tally(withLive, _ => true, seed: 5, n: 5000);
            Assert.AreEqual(0.0, Rate(counts, 2, 5000), "weight 0 never rolls");

            var pick = ThreadEncounterPrize.Roll(withLive, w => w == 3, new Random(9));
            Assert.AreEqual(3u, pick.Wcid);
            Assert.AreEqual(4, pick.Qty);
            Assert.AreEqual("live", pick.Entry.Id);
        }

        private static ThreadEncounterPrizeEntry SalvageEntry(int min = 2, int max = 4, uint[] pool = null)
            => new ThreadEncounterPrizeEntry("salvage_bags", pool ?? SalvagePool, 1, 21.25, new ThreadEncounterPrizeSalvage(min, max, 100, 10));

        [TestMethod]
        public void The_salvage_bag_count_is_uniform_from_two_to_four()
        {
            var entries = new[] { SalvageEntry() };
            var rng = new Random(1110);
            var byCount = new Dictionary<int, int>();
            const int n = 90_000;

            for (var i = 0; i < n; i++)
            {
                var pick = ThreadEncounterPrize.Roll(entries, _ => true, rng);
                byCount.TryGetValue(pick.Wcids.Count, out var c);
                byCount[pick.Wcids.Count] = c + 1;
            }

            CollectionAssert.AreEquivalent(new[] { 2, 3, 4 }, byCount.Keys.ToList(), "never fewer than 2 or more than 4");

            foreach (var count in new[] { 2, 3, 4 })
                Assert.AreEqual(1.0 / 3.0, byCount[count] / (double)n, 0.01, $"{count} bags");

            var fixedCount = ThreadEncounterPrize.Roll(new[] { SalvageEntry(3, 3) }, _ => true, new Random(2));
            Assert.AreEqual(3, fixedCount.Wcids.Count, "min == max gives exactly that many");
        }

        [TestMethod]
        public void Each_bag_draws_its_material_uniformly_and_independently()
        {
            var entries = new[] { SalvageEntry() };
            var rng = new Random(4242);
            var byMaterial = new Dictionary<uint, int>();
            var bags = 0;
            var firstTwoEqual = 0;
            const int n = 60_000;

            for (var i = 0; i < n; i++)
            {
                var pick = ThreadEncounterPrize.Roll(entries, _ => true, rng);

                foreach (var wcid in pick.Wcids)
                {
                    byMaterial.TryGetValue(wcid, out var c);
                    byMaterial[wcid] = c + 1;
                    bags++;
                }

                if (pick.Wcids[0] == pick.Wcids[1])
                    firstTwoEqual++;
            }

            foreach (var wcid in SalvagePool)
                Assert.AreEqual(0.25, byMaterial[wcid] / (double)bags, 0.01, $"material {wcid}");

            // Independent draws: two bags of one grant share a material a quarter of the time, not always.
            Assert.AreEqual(0.25, firstTwoEqual / (double)n, 0.01, "bag materials are drawn per bag");

            var withoutJade = new Dictionary<uint, int>();
            var jadeBags = 0;

            for (var i = 0; i < n; i++)
            {
                foreach (var wcid in ThreadEncounterPrize.Roll(entries, w => w != 21051, rng).Wcids)
                {
                    withoutJade.TryGetValue(wcid, out var c);
                    withoutJade[wcid] = c + 1;
                    jadeBags++;
                }
            }

            Assert.IsFalse(withoutJade.ContainsKey(21051), "a missing bag wcid leaves the pool");

            foreach (var wcid in SalvagePool.Where(w => w != 21051))
                Assert.AreEqual(1.0 / 3.0, withoutJade[wcid] / (double)jadeBags, 0.01, $"material {wcid} over the remaining three");
        }

        [TestMethod]
        public void An_empty_salvage_pool_skips_the_entry()
        {
            var missing = new List<uint>();
            Assert.IsNull(ThreadEncounterPrize.Roll(new[] { SalvageEntry() }, _ => false, new Random(3), missing), "no bag weenie exists");
            CollectionAssert.AreEquivalent(SalvagePool, missing, "each missing bag wcid is reported");

            var entries = new[] { SalvageEntry(), new ThreadEncounterPrizeEntry("notes", new uint[] { 43901 }, 10, 21.25) };
            var counts = Tally(entries, w => w == 43901, seed: 8, n: 2000);
            CollectionAssert.AreEqual(new uint[] { 43901 }, counts.Keys.ToList(), "the other entry takes the whole weight");
        }

        [TestMethod]
        public void The_roll_is_deterministic_for_a_seed()
        {
            var entries = Shipped().Entries;
            var r1 = new Random(31);
            var r2 = new Random(31);

            for (var i = 0; i < 200; i++)
                Assert.AreEqual(ThreadEncounterPrize.Roll(entries, _ => true, r1).Wcid, ThreadEncounterPrize.Roll(entries, _ => true, r2).Wcid);
        }

        [TestMethod]
        public void The_missing_weenie_warning_latches_once_per_wcid()
        {
            const uint wcid = 4_000_000_123;
            Assert.IsTrue(ThreadEncounterPrize.FirstMissingWarning(wcid));
            Assert.IsFalse(ThreadEncounterPrize.FirstMissingWarning(wcid));
            Assert.IsTrue(ThreadEncounterPrize.FirstMissingWarning(wcid + 1));
        }

        // ---- the parser -------------------------------------------------------------------------------------------

        [TestMethod]
        public void A_bad_entry_costs_only_itself()
        {
            var diagnostics = new List<string>();
            var table = Parse("{\"entries\":["
                + "{\"id\":\"ok\",\"wcids\":[10],\"qty\":3,\"weight\":2},"
                + "{\"id\":\"Bad Id\",\"wcids\":[11]},"
                + "{\"id\":\"no_wcids\",\"wcids\":[]},"
                + "{\"id\":\"zero_wcid\",\"wcids\":[0]},"
                + "{\"id\":\"qty\",\"wcids\":[12],\"qty\":0},"
                + "{\"id\":\"weight\",\"wcids\":[13],\"weight\":-1},"
                + "{\"id\":\"ok\",\"wcids\":[14]},"
                + "\"not an object\","
                + "{\"id\":\"wrong_type\",\"wcids\":\"x\"},"
                + "{\"id\":\"defaults\",\"wcids\":[15,15]}"
                + "]}", diagnostics);

            CollectionAssert.AreEqual(new[] { "ok", "defaults" }, table.Entries.Select(e => e.Id).ToList(), string.Join("\n", diagnostics));
            Assert.AreEqual(8, diagnostics.Count, string.Join("\n", diagnostics));
            Assert.IsTrue(diagnostics.All(d => d.StartsWith("encounters.json: ", StringComparison.Ordinal)));

            var ok = table.Entries[0];
            Assert.AreEqual(3, ok.Qty);
            Assert.AreEqual(2.0, ok.Weight);

            var defaults = table.Entries[1];
            Assert.AreEqual(1, defaults.Qty, "qty defaults to 1");
            Assert.AreEqual(1.0, defaults.Weight, "weight defaults to 1");
            CollectionAssert.AreEqual(new uint[] { 15 }, defaults.Wcids.ToList(), "a repeated wcid counts once");
        }

        [TestMethod]
        public void A_bad_salvage_block_costs_only_its_entry()
        {
            const string pool = "\"wcids\":[21036,21082]";
            var diagnostics = new List<string>();
            var table = Parse("{\"entries\":["
                + "{\"id\":\"good\",\"weight\":3,\"salvage\":{" + pool + ",\"bags\":{\"min\":2,\"max\":4},\"structure\":100,\"workmanship\":10}},"
                + "{\"id\":\"defaults\",\"salvage\":{\"wcids\":[21063,21063],\"bags\":{\"min\":1,\"max\":10}}},"
                + "{\"id\":\"no_bags\",\"salvage\":{" + pool + "}},"
                + "{\"id\":\"min_zero\",\"salvage\":{" + pool + ",\"bags\":{\"min\":0,\"max\":2}}},"
                + "{\"id\":\"max_below_min\",\"salvage\":{" + pool + ",\"bags\":{\"min\":3,\"max\":2}}},"
                + "{\"id\":\"max_eleven\",\"salvage\":{" + pool + ",\"bags\":{\"min\":1,\"max\":11}}},"
                + "{\"id\":\"structure_zero\",\"salvage\":{" + pool + ",\"bags\":{\"min\":2,\"max\":4},\"structure\":0}},"
                + "{\"id\":\"structure_high\",\"salvage\":{" + pool + ",\"bags\":{\"min\":2,\"max\":4},\"structure\":101}},"
                + "{\"id\":\"work_zero\",\"salvage\":{" + pool + ",\"bags\":{\"min\":2,\"max\":4},\"workmanship\":0}},"
                + "{\"id\":\"work_high\",\"salvage\":{" + pool + ",\"bags\":{\"min\":2,\"max\":4},\"workmanship\":11}},"
                + "{\"id\":\"empty_pool\",\"salvage\":{\"wcids\":[],\"bags\":{\"min\":2,\"max\":4}}},"
                + "{\"id\":\"zero_wcid\",\"salvage\":{\"wcids\":[0],\"bags\":{\"min\":2,\"max\":4}}},"
                + "{\"id\":\"wcids_beside\",\"wcids\":[20630],\"salvage\":{" + pool + ",\"bags\":{\"min\":2,\"max\":4}}},"
                + "{\"id\":\"qty_two\",\"qty\":2,\"salvage\":{" + pool + ",\"bags\":{\"min\":2,\"max\":4}}},"
                + "{\"id\":\"bags_not_object\",\"salvage\":{" + pool + ",\"bags\":3}}"
                + "]}", diagnostics);

            CollectionAssert.AreEqual(new[] { "good", "defaults" }, table.Entries.Select(e => e.Id).ToList(), string.Join("\n", diagnostics));
            Assert.AreEqual(13, diagnostics.Count, string.Join("\n", diagnostics));
            Assert.IsTrue(diagnostics.All(d => d.StartsWith("encounters.json: ", StringComparison.Ordinal) && d.EndsWith("skipped", StringComparison.Ordinal)), string.Join("\n", diagnostics));

            var good = table.Entries[0];
            Assert.IsTrue(good.IsSalvage);
            CollectionAssert.AreEqual(new uint[] { 21036, 21082 }, good.Wcids.ToList());
            Assert.AreEqual(3.0, good.Weight);
            Assert.AreEqual(1, good.Qty);
            Assert.AreEqual((2, 4, 100, 10), (good.Salvage.BagsMin, good.Salvage.BagsMax, good.Salvage.Structure, good.Salvage.Workmanship));

            var defaults = table.Entries[1];
            CollectionAssert.AreEqual(new uint[] { 21063 }, defaults.Wcids.ToList(), "a repeated bag wcid counts once");
            Assert.AreEqual(100, defaults.Salvage.Structure, "structure defaults to a full bag");
            Assert.AreEqual(10, defaults.Salvage.Workmanship, "workmanship defaults to 10");
            Assert.AreEqual((1, 10), (defaults.Salvage.BagsMin, defaults.Salvage.BagsMax), "the bounds 1 and 10 are allowed");
        }

        [TestMethod]
        public void An_absent_section_is_empty_and_silent_and_a_wrong_shape_is_one_diagnostic()
        {
            var diagnostics = new List<string>();

            Assert.AreSame(ThreadEncounterPrizeTable.Empty, ThreadEncounterPrizeTable.Parse(default, "encounters.json", diagnostics));
            Assert.AreEqual(0, diagnostics.Count);

            Assert.AreSame(ThreadEncounterPrizeTable.Empty, Parse("[1,2]", diagnostics));
            Assert.AreEqual(1, diagnostics.Count);

            Assert.AreSame(ThreadEncounterPrizeTable.Empty, Parse("{\"entries\":{}}", diagnostics));
            Assert.AreEqual(2, diagnostics.Count);

            Assert.AreSame(ThreadEncounterPrizeTable.Empty, ThreadEncounterStore.Empty.Prize, "the empty encounter store has no prize");
        }

        // ---- text and stack ---------------------------------------------------------------------------------------

        [TestMethod]
        public void The_player_text_is_verbatim()
        {
            Assert.AreEqual("Encounter prize: 10 Trade Note (250,000).", ThreadEncounterPrize.DeliveryText(10, "Trade Note (250,000)", false));
            Assert.AreEqual("Encounter prize: Rear Guard Mantle.", ThreadEncounterPrize.DeliveryText(1, "Rear Guard Mantle", false));
            Assert.AreEqual("Encounter prize: 5 Diamond Scarab. Your pack is full, so your prize was placed at your feet.",
                ThreadEncounterPrize.DeliveryText(5, "Diamond Scarab", true));
        }

        [TestMethod]
        public void The_salvage_text_groups_the_materials_alphabetically()
        {
            Assert.AreEqual("Encounter prize: 3 bags of salvage (Amethyst x2, Tourmaline).",
                ThreadEncounterPrize.SalvageDeliveryText(new[] { "Tourmaline", "Amethyst", "Amethyst" }, false));
            Assert.AreEqual("Encounter prize: 4 bags of salvage (Amethyst, Green Jade, Obsidian, Tourmaline).",
                ThreadEncounterPrize.SalvageDeliveryText(new[] { "Tourmaline", "Obsidian", "Green Jade", "Amethyst" }, false));
            Assert.AreEqual("Encounter prize: 2 bags of salvage (Obsidian x2).",
                ThreadEncounterPrize.SalvageDeliveryText(new[] { "Obsidian", "Obsidian" }, false));
            Assert.AreEqual("Encounter prize: 1 bag of salvage (Green Jade).",
                ThreadEncounterPrize.SalvageDeliveryText(new[] { "Green Jade" }, false), "one bag is singular");

            // Some bags packed, some on the ground: the full-pack line once, after the whole list.
            Assert.AreEqual("Encounter prize: 3 bags of salvage (Amethyst, Obsidian x2). Your pack is full, so your prize was placed at your feet.",
                ThreadEncounterPrize.SalvageDeliveryText(new[] { "Obsidian", "Amethyst", "Obsidian" }, true));
        }

        [TestMethod]
        public void A_bag_is_labelled_by_its_material_and_built_full()
        {
            Assert.AreEqual("Green Jade", ThreadEncounterPrize.SalvageMaterialLabel("Salvaged Green Jade"));
            Assert.AreEqual("Tourmaline", ThreadEncounterPrize.SalvageMaterialLabel("Salvaged Tourmaline"));
            Assert.AreEqual("Odd Bag", ThreadEncounterPrize.SalvageMaterialLabel("Odd Bag"), "a name without the prefix is kept");
            Assert.AreEqual("Unknown", ThreadEncounterPrize.SalvageMaterialLabel(null));

            // AdminCommands.HandleCISalvage's shape: NumItemsInMaterial = workmanship, ItemWorkmanship = workmanship x that.
            var full = ThreadEncounterPrize.SalvageBagFields(100, 10);
            Assert.AreEqual(("Salvage (100)", (ushort)100, 100, 10), full);
            Assert.AreEqual(10f, (float)full.ItemWorkmanship / full.NumItemsInMaterial, "the displayed workmanship is exactly 10");

            for (var w = 1; w <= 10; w++)
            {
                var f = ThreadEncounterPrize.SalvageBagFields(100, w);
                Assert.AreEqual(w * w, f.ItemWorkmanship);
                Assert.AreEqual(0, f.ItemWorkmanship % f.NumItemsInMaterial, "a whole-number average, as Player.RoundBagWorkmanship leaves a full bag");
                Assert.AreEqual((float)w, (float)f.ItemWorkmanship / f.NumItemsInMaterial);
            }

            Assert.AreEqual("Salvage (50)", ThreadEncounterPrize.SalvageBagFields(50, 10).Name);
        }

        [TestMethod]
        public void The_stack_size_is_clamped_to_the_item()
        {
            CollectionAssert.AreEqual(new[] { 10 }, ThreadEncounterPrize.StackSizes(10, true, 1000).ToArray());
            CollectionAssert.AreEqual(new[] { 100, 100, 50 }, ThreadEncounterPrize.StackSizes(250, true, 100).ToArray(), "above the max it splits, never clamps");
            CollectionAssert.AreEqual(new[] { 1 }, ThreadEncounterPrize.StackSizes(10, false, 1000).ToArray(), "a non-stackable item is one");
            CollectionAssert.AreEqual(new[] { 1 }, ThreadEncounterPrize.StackSizes(10, true, null).ToArray(), "no max stack is one");
            CollectionAssert.AreEqual(new[] { 1 }, ThreadEncounterPrize.StackSizes(0, true, 1000).ToArray());
        }

        // ---- the gate, the stamp and the hooks --------------------------------------------------------------------

        [TestMethod]
        public void Grant_refuses_nulls_and_an_unstamped_run_defaults_on()
        {
            var run = ThreadEncounterTestFixtures.DungeonRun(Array.Empty<ACE.Server.ThreadDungeons.Defs.DungeonSpawnPointDef>());

            Assert.IsTrue(run.EncounterPrizeEnabled, "a run nobody stamped uses the shipped default, true");
            Assert.IsFalse(ThreadEncounterPrize.Grant(run, null, "defense"));
            Assert.IsFalse(ThreadEncounterPrize.Grant(null, null, "jump"));
        }

        [TestMethod]
        public void TryStart_stamps_the_tunable_and_Grant_honours_it()
        {
            var manager = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadDungeonManager.cs");
            StringAssert.Contains(manager, "var encounterPrizeEnabled = PropertyManager.GetBool(\"dynamic_dungeons_encounter_prize_enabled\", true).Item;");
            StringAssert.Contains(manager, "EncounterPrizeEnabled = encounterPrizeEnabled,");

            var prize = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadEncounterPrize.cs");
            var grant = PooledLootSourceText.MethodBody(prize, "public static bool Grant(ThreadDungeonRun run, Player player, string encounterKind, int bonusWaves)");
            var gate = grant.IndexOf("if (!run.EncounterPrizeEnabled)", StringComparison.Ordinal);
            Assert.IsTrue(gate >= 0, "Grant reads the stamp");
            Assert.IsTrue(gate < grant.IndexOf("Roll(", StringComparison.Ordinal), "the gate comes before the roll");

            // Every inventory and world mutation is queued onto the winner's own action queue (their landblock thread).
            StringAssert.Contains(grant, "new ActionChain(player, () => Deliver(run, player, pick, encounterKind, bonusWaves)).EnqueueChain();");
            Assert.IsFalse(grant.Contains("TryCreateInInventory"), "Grant itself never touches the pack");
        }

        [TestMethod]
        public void Delivery_places_each_object_then_plays_one_level_up_effect()
        {
            var prize = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadEncounterPrize.cs");
            var deliver = PooledLootSourceText.MethodBody(prize, "private static void Deliver(ThreadDungeonRun run, Player player, ThreadEncounterPrizePick pick, string encounterKind, int bonusWaves)");
            var build = PooledLootSourceText.MethodBody(prize, "internal static List<(WorldObject Item, string Label)> BuildObjects(ThreadEncounterPrizePick pick, int bonusWaves, Random rng)");

            var online = deliver.IndexOf("PlayerManager.GetOnlinePlayer(player.Guid.Full)", StringComparison.Ordinal);
            var inside = deliver.IndexOf("ThreadRunPresence.IsMemberInside(run, player)", StringComparison.Ordinal);
            var loop = deliver.IndexOf("foreach (var (item, label) in objects)", StringComparison.Ordinal);
            var create = build.IndexOf(": CreateObject(wcid);", StringComparison.Ordinal);
            var bagFields = build.IndexOf("SalvageBagFields(salvage.Structure, salvage.Workmanship)", StringComparison.Ordinal);
            var place = deliver.IndexOf("if (!TryPlace(run, player, item, wcid, encounterKind, out var atFeet))", StringComparison.Ordinal);
            var feetFlag = deliver.IndexOf("anyAtFeet |= atFeet;", StringComparison.Ordinal);
            // Owner ruling 2026-11-11: the item/aetheria level-up effect, never the player level-up one.
            var effect = deliver.IndexOf("player.PlayParticleEffect(PlayScript.AetheriaLevelUp, player.Guid);", StringComparison.Ordinal);
            Assert.IsFalse(deliver.Contains("PlayScript.LevelUp"), "the prize never plays the player level-up effect");
            var loopBody = PooledLootSourceText.MethodBody(deliver.Substring(loop), "foreach (var (item, label) in objects)");

            Assert.IsTrue(online >= 0 && inside > online && loop > inside, "online and inside are re-checked before anything is built");
            Assert.IsTrue(create >= 0 && bagFields > create && loop > inside && place > loop && feetFlag > place, "each object is built (BuildObjects), filled, then placed");
            Assert.IsTrue(effect > loop + loopBody.Length, "the effect plays after the loop");
            Assert.AreEqual(1, CountOf(deliver, "PlayParticleEffect("), "one level-up effect per grant, not per bag");
            Assert.AreEqual(1, CountOf(deliver, "new GameMessageSystemChat("), "one line per grant");
            Assert.IsFalse(deliver.Contains("TryCreateInInventory"), "every object goes through TryPlace");

            var tryPlace = PooledLootSourceText.MethodBody(prize, "private static bool TryPlace(ThreadDungeonRun run, Player player, WorldObject item, uint wcid, string encounterKind, out bool atFeet)");
            var pack = tryPlace.IndexOf("player.TryCreateInInventoryWithNetworking(item, out _, burdenCheck: false)", StringComparison.Ordinal);
            var feet = tryPlace.IndexOf("Creature.DropLootInWorld(new[] { item }, location, player.Guid)", StringComparison.Ordinal);

            Assert.IsTrue(pack >= 0 && feet > pack, "pack first, then the feet fallback");

            // Owner ruling 2026-11-09: burden never sends a prize to the ground, only a pack with no free slot.
            Assert.AreEqual(1, CountOf(tryPlace, "TryCreateInInventoryWithNetworking("), "one pack attempt, without the burden check");

            // An item neither packed nor on a landblock is destroyed by the catch, even one DropLootInWorld already located.
            StringAssert.Contains(tryPlace, "if (!item.IsDestroyed && item.ContainerId == null && item.CurrentLandblock == null)");
        }

        [TestMethod]
        public void The_defense_win_grants_every_member_inside()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/DefenseEncounterType.cs");
            var hook = PooledLootSourceText.MethodBody(src, "internal static void OnDefenseSucceeded(ThreadEncounterInstance instance, DefenseOutcome outcome)");

            StringAssert.Contains(hook, "foreach (var winner in ThreadRunPresence.OnlineMembersInside(run))");
            StringAssert.Contains(hook, "ThreadEncounterPrize.Grant(run, winner, KindName)");

            var finish = PooledLootSourceText.MethodBody(src, "private static void Finish(ThreadEncounterInstance instance, DefenseState state, ACE.Server.Entity.Landblock landblock, DefenseOutcome outcome, int enemiesLeft)");
            StringAssert.Contains(finish, "if (outcome != DefenseOutcome.Failure)\n                OnDefenseSucceeded(instance, outcome);", "only a win (success or timeout) reaches the hook");
        }

        [TestMethod]
        public void The_jump_finisher_gets_one_prize()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/JumpEncounterType.cs");
            var hook = PooledLootSourceText.MethodBody(src, "public static void OnCourseCompleted(ThreadEncounterInstance instance, Player player)");

            StringAssert.Contains(hook, "ThreadEncounterPrize.Grant(instance.Run, player, KindName);");

            // Called from the Finished branch only, which the finisher set latches once per player.
            var ring = PooledLootSourceText.MethodBody(src, "internal static JumpGoalOutcome RingGoal(ThreadEncounterInstance instance, JumpState state, uint guid, string name, bool userInside, DateTime utcNow,");
            Assert.AreEqual(1, CountOf(src, "OnCourseCompleted(instance, player);"), "one call site");
            Assert.IsTrue(ring.IndexOf("case JumpGoalOutcome.Finished:", StringComparison.Ordinal) < ring.IndexOf("OnCourseCompleted(instance, player);", StringComparison.Ordinal));
        }

        private static int CountOf(string text, string token)
        {
            var n = 0;

            for (var i = text.IndexOf(token, StringComparison.Ordinal); i >= 0; i = text.IndexOf(token, i + token.Length, StringComparison.Ordinal))
                n++;

            return n;
        }
    }
}
