using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.Managers;
using ACE.Server.Pinnacles;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The Foolproof salvage rewards: one C# pool (<see cref="FoolproofSalvage.Wcids"/>) that the shipped content
    /// (encounters.json prize entry, Event Crate pool) must equal, and the two chance rolls (Thread Cache boss bonus,
    /// Pinnacle Cache) driven through the tunables at 1.0 and 0.0 with the object factory swapped for a stub.
    /// </summary>
    [TestClass]
    public class FoolproofSalvageTests
    {
        private static uint nextGuid = 0x7EA00000;

        private static readonly uint[] Expected =
        {
            36619, 36620, 36621, 36622, 36623, 36624, 36625, 36626, 36627, 36628, 36634, 36635, 36636, 1002700,
        };

        private static string RepoFile(string relative)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, relative.Replace('/', Path.DirectorySeparatorChar));

                if (File.Exists(candidate))
                    return candidate;

                dir = dir.Parent;
            }

            Assert.Fail($"Could not find {relative} by walking up from {AppContext.BaseDirectory}");
            return null;
        }

        private static WorldObject Stub(uint wcid)
        {
            var weenie = new Weenie { WeenieClassId = wcid, WeenieType = WeenieType.Generic };
            return new GenericObject(weenie, new ObjectGuid(nextGuid++));
        }

        /// <summary>Runs <paramref name="body"/> with the factory stubbed and the named tunable set, restoring both.</summary>
        private static void With(string key, double value, double restore, Action body)
        {
            var factory = FoolproofSalvage.Factory;

            try
            {
                FoolproofSalvage.Factory = Stub;
                Assert.IsTrue(PropertyManager.ModifyDouble(key, value));
                body();
            }
            finally
            {
                FoolproofSalvage.Factory = factory;
                PropertyManager.ModifyDouble(key, restore);
            }
        }

        // ---- the one list ----

        [TestMethod]
        public void The_pool_is_the_fourteen_ruled_wcids_without_the_augmentations()
        {
            CollectionAssert.AreEqual(Expected, FoolproofSalvage.Wcids.ToArray());
            Assert.AreEqual(14, FoolproofSalvage.Wcids.Distinct().Count());

            foreach (var augmentation in new uint[] { 36629, 36630, 36631, 36632, 36633 })
                Assert.IsFalse(FoolproofSalvage.Wcids.Contains(augmentation), $"{augmentation} is an augmentation, not a foolproof");
        }

        [TestMethod]
        public void Shipped_encounter_prize_has_one_single_wcid_entry_per_pool_bag_and_a_peddler_price_each()
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(RepoFile("Content/dungeons/dynamic/encounters.json")));
            var entries = doc.RootElement.GetProperty("prize").GetProperty("entries").EnumerateArray()
                .Where(e => e.GetProperty("id").GetString().StartsWith("foolproof_", StringComparison.Ordinal)).ToList();

            CollectionAssert.AreEqual(FoolproofSalvage.Wcids.Select(w => "foolproof_" + w).ToArray(), entries.Select(e => e.GetProperty("id").GetString()).ToArray());

            foreach (var e in entries)
            {
                var wcids = e.GetProperty("wcids").EnumerateArray().Select(x => x.GetUInt32()).ToArray();
                Assert.AreEqual(1, wcids.Length, "the Peddler sells only single-wcid item entries");
                Assert.AreEqual("foolproof_" + wcids[0], e.GetProperty("id").GetString());
                Assert.AreEqual(1, e.GetProperty("qty").GetInt32(), "qty 1 is a single valuable: a Gambler's Cache never grows it");
                Assert.AreEqual(0.0721, e.GetProperty("weight").GetDouble(), 1e-9);
            }

            CollectionAssert.AreEqual(FoolproofSalvage.Wcids.ToArray(), entries.Select(e => e.GetProperty("wcids")[0].GetUInt32()).ToArray());
            Assert.AreEqual(1.01, entries.Sum(e => e.GetProperty("weight").GetDouble()), 0.001, "about 1% of the prize roll");

            var peddler = doc.RootElement.GetProperty("types").EnumerateArray().Single(e => e.GetProperty("kind").GetString() == "peddler");
            var config = peddler.GetProperty("config");
            var cheap = config.GetProperty("cheap").EnumerateArray().Select(x => x.GetString()).ToList();

            foreach (var wcid in FoolproofSalvage.Wcids)
            {
                Assert.AreEqual(160, config.GetProperty("prices").GetProperty("foolproof_" + wcid).GetInt32());
                Assert.IsFalse(cheap.Contains("foolproof_" + wcid), "not a cheap item");
            }
        }

        [TestMethod]
        public void Shipped_event_crate_pool_carries_each_bag_once_at_the_ruled_weight()
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(RepoFile("Content/events/axes/rewards.json")));
            var standard = doc.RootElement.GetProperty("rewards").EnumerateArray().Single(e => e.GetProperty("id").GetString() == "standard");
            var pool = standard.GetProperty("pool").EnumerateArray().ToList();

            var bags = pool.Where(e => e.GetProperty("weight").GetDouble() == 0.137).ToList();
            CollectionAssert.AreEqual(FoolproofSalvage.Wcids.ToArray(), bags.Select(e => e.GetProperty("wcid").GetUInt32()).ToArray());
            Assert.IsTrue(bags.All(e => e.GetProperty("qty").GetInt32() == 1 && e.GetProperty("scale").GetString() == "chance"));

            var existing = pool.Where(e => e.GetProperty("weight").GetDouble() != 0.137).Sum(e => e.GetProperty("weight").GetDouble());
            Assert.AreEqual(94.0, existing, 1e-9, "the existing entries are unchanged");
            Assert.AreEqual(1.918, bags.Sum(e => e.GetProperty("weight").GetDouble()), 1e-9);
        }

        [TestMethod]
        public void Warmup_covers_the_pool()
        {
            var set = ThreadLootWeenieWarmup.DeliveryWcids(null, null).ToList();
            CollectionAssert.IsSubsetOf(FoolproofSalvage.Wcids.ToArray(), set);
        }

        [TestMethod]
        public void Tunables_ship_at_the_ruled_values_in_the_defaults_table()
        {
            Assert.AreEqual(0.02, FoolproofSalvage.DefaultThreadCacheChance);
            Assert.AreEqual(0.5, FoolproofSalvage.DefaultPinnacleCacheChance);

            var defaults = File.ReadAllLines(RepoFile("Source/config-defaults.tsv"));
            StringAssert.StartsWith(defaults.Single(l => l.StartsWith(FoolproofSalvage.ThreadCacheChanceKey + "\t")), FoolproofSalvage.ThreadCacheChanceKey + "\tdouble\t0.02\t0.02\t");
            StringAssert.StartsWith(defaults.Single(l => l.StartsWith(FoolproofSalvage.PinnacleCacheChanceKey + "\t")), FoolproofSalvage.PinnacleCacheChanceKey + "\tdouble\t0.5\t0.5\t");

            Assert.IsTrue(DefaultPropertyManager.DefaultDoubleProperties.ContainsKey(FoolproofSalvage.ThreadCacheChanceKey));
            Assert.IsTrue(DefaultPropertyManager.DefaultDoubleProperties.ContainsKey(FoolproofSalvage.PinnacleCacheChanceKey));
            Assert.AreEqual(0.02, DefaultPropertyManager.DefaultDoubleProperties[FoolproofSalvage.ThreadCacheChanceKey].Item);
            Assert.AreEqual(0.5, DefaultPropertyManager.DefaultDoubleProperties[FoolproofSalvage.PinnacleCacheChanceKey].Item);
        }

        [TestMethod]
        public void Chances_clamp_to_zero_one()
        {
            Assert.AreEqual(0.0, FoolproofSalvage.Clamp01(-3));
            Assert.AreEqual(1.0, FoolproofSalvage.Clamp01(7));
            Assert.AreEqual(0.0, FoolproofSalvage.Clamp01(double.NaN));
            Assert.AreEqual(0.25, FoolproofSalvage.Clamp01(0.25));
        }

        [TestMethod]
        public void PickRandom_only_returns_pool_members_and_reaches_every_one()
        {
            var seen = new HashSet<uint>();

            for (var i = 0; i < 2000; i++)
            {
                var wcid = FoolproofSalvage.PickRandom();
                Assert.IsTrue(FoolproofSalvage.Wcids.Contains(wcid));
                seen.Add(wcid);
            }

            Assert.AreEqual(14, seen.Count);
        }

        // ---- Thread Cache: what the per-player bonus receives ----

        [TestMethod]
        public void Thread_bonus_step_adds_exactly_one_pool_bag_at_1_and_none_at_0()
        {
            With(FoolproofSalvage.ThreadCacheChanceKey, 1.0, FoolproofSalvage.DefaultThreadCacheChance, () =>
            {
                for (var i = 0; i < 20; i++)
                {
                    var items = new List<WorldObject>();
                    ThreadDungeonRewardSpawner.AddBonusFoolproof(items);
                    Assert.AreEqual(1, items.Count);
                    Assert.IsTrue(FoolproofSalvage.Wcids.Contains(items[0].WeenieClassId));
                }
            });

            With(FoolproofSalvage.ThreadCacheChanceKey, 0.0, FoolproofSalvage.DefaultThreadCacheChance, () =>
            {
                var items = new List<WorldObject>();
                ThreadDungeonRewardSpawner.AddBonusFoolproof(items);
                Assert.AreEqual(0, items.Count);
            });
        }

        [TestMethod]
        public void Thread_bonus_step_hits_near_the_tunable_rate()
        {
            With(FoolproofSalvage.ThreadCacheChanceKey, 0.25, FoolproofSalvage.DefaultThreadCacheChance, () =>
            {
                ACE.Common.ThreadSafeRandom.Reseed(1234);
                var hits = 0;

                for (var i = 0; i < 4000; i++)
                {
                    var items = new List<WorldObject>();
                    ThreadDungeonRewardSpawner.AddBonusFoolproof(items);
                    hits += items.Count;
                }

                Assert.AreEqual(1000.0, hits, 120.0);
            });
        }

        /// <summary>
        /// The step has to be CALLED by every bonus builder. A real run needs the world database (the Trade Note and key
        /// are built from weenies), so the wiring is pinned against source, and the order is the sort's input: after the
        /// key, before the salvage, so the FixedOrder index puts it in the front block.
        /// </summary>
        [TestMethod]
        public void Every_bonus_builder_calls_the_step_between_the_key_and_the_salvage_bags()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadDungeonRewardSpawner.cs");

            foreach (var signature in new[]
            {
                "public static List<WorldObject> BuildBossBonusItems(ThreadDungeonRun run)",
                "public static List<WorldObject> BuildBossBonusItems(ThreadDungeonRun run, double rewardBonus)",
                "public static List<WorldObject> BuildBossBonusPerSeatItems(ThreadDungeonRun run, double rewardBonus)",
            })
            {
                var body = PooledLootSourceText.MethodBody(src, signature);
                var key = body.IndexOf("AddBonusLegendaryKey(run, items);", StringComparison.Ordinal);
                var fool = body.IndexOf("AddBonusFoolproof(items);", StringComparison.Ordinal);
                var bags = body.IndexOf("AddBonusSalvageBags(run, items);", StringComparison.Ordinal);

                Assert.IsTrue(key >= 0 && key < fool && fool < bags, signature);
                Assert.AreEqual(fool, body.LastIndexOf("AddBonusFoolproof(items);", StringComparison.Ordinal), "one roll per builder, never scaled by B");
            }

            var legacy = PooledLootSourceText.MethodBody(src, "public static void TrySpawnBossCache(ThreadDungeonRun run)");
            Assert.IsTrue(legacy.IndexOf("FillLegendaryKey(", StringComparison.Ordinal) < legacy.IndexOf("FillFoolproof(chest, ref nextPlacement)", StringComparison.Ordinal));
            Assert.IsTrue(legacy.IndexOf("FillFoolproof(chest, ref nextPlacement)", StringComparison.Ordinal) < legacy.IndexOf("FillSalvage(", StringComparison.Ordinal));

            StringAssert.Contains(src, "internal static void AddBonusFoolproof(List<WorldObject> items) => FoolproofSalvage.AddThreadCacheRoll(items);");
        }

        [TestMethod]
        public void The_fixed_tag_sorts_the_bag_to_the_front_and_keeps_it_out_of_peddler_payment()
        {
            var note = Stub(1);
            var key = Stub(2);
            var bag = Stub(36619);
            var loot = Stub(5);

            // The bonus list order is [note, key, foolproof, salvage...]; the loot arrives first in the pile.
            var pile = new List<HeldPileItem>
            {
                new HeldPileItem(loot, null, 0),
                new HeldPileItem(note, null, 0, null, 0),
                new HeldPileItem(key, null, 0, null, 1),
                new HeldPileItem(bag, null, 0, null, 2),
            };

            var sorted = ThreadLootStacking.SortPileForPlacement(pile);
            CollectionAssert.AreEqual(new[] { note, key, bag, loot }, sorted.Select(h => h.Item).ToArray());

            Assert.IsFalse(ThreadPeddlerPayment.ItemEligibleForPayment(pile[3]), "a fixed boss bonus item is never payment");
            Assert.IsTrue(ThreadPeddlerPayment.ItemEligibleForPayment(pile[0]), "positive control: plain loot is");
        }

        // ---- Pinnacle Cache ----

        [TestMethod]
        public void Pinnacle_roll_adds_one_pool_bag_first_at_1_and_none_at_0()
        {
            With(FoolproofSalvage.PinnacleCacheChanceKey, 1.0, FoolproofSalvage.DefaultPinnacleCacheChance, () =>
            {
                for (var i = 0; i < 20; i++)
                {
                    var rewards = PinnacleRewards.Roll(200, null, null);
                    Assert.AreEqual(1, rewards.Count, "an empty table still pays the independent roll");
                    Assert.IsTrue(FoolproofSalvage.Wcids.Contains(rewards[0].WeenieClassId));
                }
            });

            With(FoolproofSalvage.PinnacleCacheChanceKey, 0.0, FoolproofSalvage.DefaultPinnacleCacheChance, () =>
            {
                Assert.AreEqual(0, PinnacleRewards.Roll(200, null, null).Count);
            });
        }

        [TestMethod]
        public void Pinnacle_roll_hits_near_half_at_the_shipped_default()
        {
            With(FoolproofSalvage.PinnacleCacheChanceKey, FoolproofSalvage.DefaultPinnacleCacheChance, FoolproofSalvage.DefaultPinnacleCacheChance, () =>
            {
                ACE.Common.ThreadSafeRandom.Reseed(99);
                var hits = 0;

                for (var i = 0; i < 2000; i++)
                    hits += PinnacleRewards.Roll(200, null, null).Count;

                Assert.AreEqual(1000.0, hits, 120.0);
            });
        }
    }
}
