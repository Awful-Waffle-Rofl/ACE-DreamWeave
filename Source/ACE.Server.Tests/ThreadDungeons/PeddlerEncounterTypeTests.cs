using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Knots;
using ACE.Server.Managers;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldObjects;

using static ACE.Server.Tests.ThreadDungeons.PeddlerTestFixtures;
using static ACE.Server.Tests.ThreadDungeons.ThreadEncounterTestFixtures;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// PeddlerEncounterType: config parsing against the shipped encounters.json, the pure stock and price builder (count in
    /// range, distinct, at least one cheap, deterministic per seed, prices inside the configured bounds), the bundle shell
    /// rules, RejectRun with pooled loot off, Place (and its cleanup when EnterWorld fails), and a second peddler in the
    /// same run reading the balance the first one reduced.
    ///
    /// Constructing a peddler needs the vendor seeds (ThreadPeddlerVendorTests.SeedConstructible); vendor_shop_uses_generator
    /// is restored in TestCleanup. Every static seam a test swaps is restored in its finally.
    /// </summary>
    [TestClass]
    public class PeddlerEncounterTypeTests
    {
        private const string ShopGeneratorKey = "vendor_shop_uses_generator";

        [ClassInitialize]
        public static void PrimePlayerType(TestContext _)
        {
            ThornsCloseRangeTests.EnsurePlayerTypeInitializable();
        }

        [TestInitialize]
        public void Setup()
        {
            ThreadPeddlerVendorTests.SeedConstructible();
        }

        [TestCleanup]
        public void Cleanup()
        {
            PropertyManager.ModifyBool(ShopGeneratorKey, DefaultPropertyManager.DefaultBooleanProperties[ShopGeneratorKey].Item);
        }

        // ======================================================================================
        // the shipped file
        // ======================================================================================

        private static JsonDocument ShippedFile()
            => JsonDocument.Parse(File.ReadAllText(SalvageRollerVendorTests.FindInSourceTree("Content/dungeons/dynamic/encounters.json")),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

        internal static ThreadEncounterPrizeTable ShippedPrize()
        {
            using var doc = ShippedFile();
            var diagnostics = new List<string>();
            var table = ThreadEncounterPrizeTable.Parse(doc.RootElement.GetProperty("prize").Clone(), "encounters.json", diagnostics);
            Assert.AreEqual(0, diagnostics.Count, string.Join("\n", diagnostics));
            return table;
        }

        internal static JsonElement ShippedRow()
        {
            using var doc = ShippedFile();
            var rows = doc.RootElement.GetProperty("types").EnumerateArray().Where(r => r.GetProperty("kind").GetString() == PeddlerEncounterType.KindName).ToList();
            Assert.AreEqual(1, rows.Count, "one peddler row");
            return rows[0].Clone();
        }

        internal static PeddlerConfig ShippedConfig(out ThreadEncounterPrizeTable prize)
        {
            prize = ShippedPrize();
            var config = PeddlerEncounterType.ParseConfig(ShippedRow().GetProperty("config"), prize, out var error);
            Assert.IsNotNull(config, error);
            return config;
        }

        [TestMethod]
        public void ShippedRow_ParsesToTheOwnerValues()
        {
            var row = ShippedRow();
            var config = ShippedConfig(out _);

            Assert.AreEqual("peddler", row.GetProperty("id").GetString());
            Assert.IsTrue(row.GetProperty("enabled").GetBoolean());
            Assert.AreEqual(1, row.GetProperty("weight").GetDouble());

            Assert.AreEqual(1007981u, config.NpcWcid);
            Assert.AreEqual(1007982u, config.CurrencyWcid);
            Assert.AreEqual(1007983u, config.BundleWcid);
            Assert.AreEqual(120, config.IdleSeconds);
            Assert.AreEqual(5, config.WalkAwaySeconds);
            Assert.AreEqual((3, 5), (config.OffersMin, config.OffersMax));
            Assert.AreEqual((0.85, 1.6), (config.PriceMultiplierMin, config.PriceMultiplierMax));
            Assert.AreEqual(0.10, config.ItemJitter);

            var expected = new Dictionary<string, int>
            {
                { "pattern", 320 }, { "mantle_gem", 240 }, { "amplifier_gem", 240 }, { "aspect_gem", 240 }, { "fiber", 120 },
                { "diamond_scarab", 70 }, { "promissory_note", 60 }, { "trade_note_250k", 50 }, { "salvage_bags", 50 },
            };

            // Foolproof salvage: one single-wcid entry per pool bag, 160 each, none cheap.
            foreach (var wcid in ACE.Server.ThreadDungeons.FoolproofSalvage.Wcids)
                expected["foolproof_" + wcid] = 160;

            CollectionAssert.AreEquivalent(expected.ToList(), config.Prices.ToList());
            CollectionAssert.AreEquivalent(new[] { "trade_note_250k", "promissory_note", "salvage_bags" }, config.Cheap.ToList());
        }

        /// <summary>Owner ruling 2026-11-20: the high-value entries are one per player per run.</summary>
        [TestMethod]
        public void ShippedRow_CapsTheHighValueEntriesOnePerPlayer()
        {
            var config = ShippedConfig(out _);

            var expected = new List<string> { "pattern", "fiber", "mantle_gem", "amplifier_gem", "aspect_gem" };
            expected.AddRange(ACE.Server.ThreadDungeons.FoolproofSalvage.Wcids.Select(w => "foolproof_" + w));

            Assert.AreEqual(19, expected.Count, "sanity: five plus the 14 foolproof bags");
            CollectionAssert.AreEquivalent(expected, config.OnePerPlayer.ToList());
            Assert.IsTrue(config.OnePerPlayer.All(config.Prices.ContainsKey), "every capped id is priced");
            Assert.IsFalse(config.OnePerPlayer.Overlaps(config.Cheap), "no cheap entry is capped");
        }

        [TestMethod]
        public void ParseConfig_OnePerPlayer_UnpricedOrMalformedIsAnError_AbsentIsEmpty()
        {
            var cases = new (string Json, string Fragment)[]
            {
                (ShippedConfigWith(d => { d["onePerPlayer"] = new[] { "pattern", "no_such" }; return d; }), "config.onePerPlayer entry no_such must be a priced entry id"),
                (ShippedConfigWith(d => { d["onePerPlayer"] = new object[] { 7 }; return d; }), "config.onePerPlayer entry Number"),
                (ShippedConfigWith(d => { d["onePerPlayer"] = "pattern"; return d; }), "config.onePerPlayer must be an array"),
            };

            foreach (var (json, fragment) in cases)
            {
                Assert.IsNull(Parse(json, out var error), fragment);
                StringAssert.Contains(error ?? "", fragment, json);
            }

            var absent = Parse(ShippedConfigWith(d => { d.Remove("onePerPlayer"); return d; }), out var absentError);
            Assert.IsNotNull(absent, absentError);
            Assert.AreEqual(0, absent.OnePerPlayer.Count, "absent key: nothing capped");
            Assert.AreEqual(0, new PeddlerConfig().OnePerPlayer.Count, "the default is empty");

            var some = Parse(ShippedConfigWith(d => { d["onePerPlayer"] = new[] { "fiber", "fiber" }; return d; }), out var someError);
            Assert.IsNotNull(some, someError);
            CollectionAssert.AreEquivalent(new[] { "fiber" }, some.OnePerPlayer.ToList(), "a repeated id is one entry");
        }

        // ---- percent-of-pile pricing (owner ruling 2026-11-20, revised: percent term carries the jitter only) ---------

        [TestMethod]
        public void ShippedRow_PricePercent_IsTheOwnerValues()
        {
            var config = ShippedConfig(out _);

            var expected = new Dictionary<string, double>
            {
                { "pattern", 80 }, { "mantle_gem", 80 }, { "amplifier_gem", 80 }, { "aspect_gem", 80 }, { "fiber", 15 },
                { "diamond_scarab", 8.75 }, { "promissory_note", 7.5 }, { "trade_note_250k", 6.25 }, { "salvage_bags", 6.25 },
            };

            foreach (var wcid in ACE.Server.ThreadDungeons.FoolproofSalvage.Wcids)
                expected["foolproof_" + wcid] = 20;

            CollectionAssert.AreEquivalent(expected.ToList(), config.PricePercent.ToList());
            CollectionAssert.AreEquivalent(config.Prices.Keys.ToList(), config.PricePercent.Keys.ToList(), "every priced entry has a percent; the flat prices stay as floors");
        }

        [TestMethod]
        public void MemberPrice_FloorOnASmallPile_PercentOnABigOne_CeilRounding_AndTheClamp()
        {
            Assert.AreEqual(50, PeddlerEncounterType.MemberPrice(50, 6.25, 1.0, 100), "6.25 of 100 is below the floor: the flat price");
            Assert.AreEqual(50, PeddlerEncounterType.MemberPrice(50, 6.25, 1.0, 800), "exactly the floor (50.0): no spurious round-up");
            Assert.AreEqual(73, PeddlerEncounterType.MemberPrice(50, 6.25, 1.0, 1155), "the stage pile: 72.1875 rounds UP to 73");
            Assert.AreEqual(100, PeddlerEncounterType.MemberPrice(50, 6.25, 1.0, 1600), "an exact 100.0 stays 100");
            Assert.AreEqual(924, PeddlerEncounterType.MemberPrice(320, 80, 1.0, 1155), "80% of the stage pile");
            Assert.AreEqual(69, PeddlerEncounterType.MemberPrice(50, 6.25, 1.1, 1000), "the item jitter applies: 68.75 -> 69");
            Assert.AreEqual(57, PeddlerEncounterType.MemberPrice(50, 6.25, 0.9, 1000), "and downward: 56.25 -> 57");
            Assert.AreEqual(PeddlerConfig.MaxPrice, PeddlerEncounterType.MemberPrice(320, 100, 1.1, 10_000_000), "the percent term is clamped to MaxPrice");
            Assert.AreEqual(PeddlerConfig.MaxPrice, PeddlerEncounterType.MemberPrice(320, 100, 1.0, long.MaxValue), "even at the largest basis");
            Assert.AreEqual(150_000, PeddlerEncounterType.MemberPrice(150_000, 100, 1.0, 1000), "the clamp never lowers the flat price");
            Assert.AreEqual(50, PeddlerEncounterType.MemberPrice(50, null, 1.0, 1_000_000), "no percent: flat");
            Assert.AreEqual(50, PeddlerEncounterType.MemberPrice(50, 6.25, 1.0, 0), "no basis: flat");
        }

        /// <summary>The per-spawn multiplier stays on the flat floor and never reaches the percent term (owner revision).</summary>
        [TestMethod]
        public void PlanPrice_PercentTermIgnoresTheSpawnMultiplier()
        {
            var plan = PercentPlan(jitter: 1.0);
            Assert.AreEqual(1.6, plan.SpawnMultiplier, "fixture: a 1.6 spawn multiplier");

            var pattern = plan.Offers.Single(o => o.EntryId == PatternEntry.Id);
            Assert.AreEqual(800, plan.PriceFor(pattern, 1000), "80% of 1000, not 1280");
            Assert.AreEqual(320, plan.PriceFor(pattern, 100), "the floor");
            Assert.AreEqual(50, plan.MinPriceFor(100), "the cheapest at a small basis is the cheapest floor");
            Assert.AreEqual(125, plan.MinPriceFor(2000), "and at a big one, the cheapest share");
        }

        /// <summary>
        /// Golden values computed from the code BEFORE percent pricing (c5f4253d5's BuildStock, same seeds): adding the
        /// jitter factor to an offer must not change a single draw or price, and with no pricePercent every member price
        /// is exactly the flat price.
        /// </summary>
        [TestMethod]
        public void FlatPricing_IsUnchanged_GoldenValues()
        {
            var shipped = ShippedConfig(out var prize);
            var golden = new (int Seed, double Mult, string Stall)[]
            {
                (1, 0.9259498401665827, "aspect_gem=229 diamond_scarab=59 salvage_bags=44"),
                (7, 1.357127081047337, "fiber=148 promissory_note=89 mantle_gem=348 trade_note_250k=73"),
                (42, 1.3933062735448156, "aspect_gem=335 pattern=417 promissory_note=88 trade_note_250k=66 diamond_scarab=93"),
                (2026, 1.5371064391159948, "trade_note_250k=70 fiber=191 foolproof_36626=263"),
            };

            var flatOnly = Parse(ShippedConfigWith(d => { d.Remove("pricePercent"); return d; }), out var error);
            Assert.IsNotNull(flatOnly, error);
            Assert.AreEqual(0, flatOnly.PricePercent.Count, "absent key: flat pricing");

            foreach (var config in new[] { shipped, flatOnly })
            {
                foreach (var (seed, mult, expected) in golden)
                {
                    var stock = PeddlerEncounterType.BuildStock(config, prize.Entries, _ => true, new Random(seed), 120, Pool, out var m);
                    Assert.AreEqual(mult, m, $"seed {seed}: the spawn multiplier");
                    Assert.AreEqual(expected, string.Join(" ", stock.Select(o => $"{o.EntryId}={o.Price}")), $"seed {seed}: the flat stall");

                    foreach (var o in stock)
                        Assert.AreEqual(o.Price, PeddlerEncounterType.FinalPrice(config.Prices[o.EntryId], m, o.JitterFactor - 1), $"seed {seed} {o.EntryId}: the recorded jitter factor is the one the flat price used");
                }
            }

            var flatPlan = new PeddlerPlan(null, flatOnly, PeddlerEncounterType.BuildStock(flatOnly, prize.Entries, _ => true, new Random(42), 120, Pool, out var fm), fm);
            Assert.IsFalse(flatPlan.HasPercentPricing);

            foreach (var o in flatPlan.Offers)
                Assert.AreEqual(o.Price, flatPlan.PriceFor(o, 1_000_000), $"{o.EntryId}: no percent, a huge pile still pays the flat price");
        }

        /// <summary>
        /// Review of 05daac285 (F2): a factory that throws on the second offer must not leak the first display: everything
        /// built is destroyed and nothing is listed, so Arm fails closed. Mutation-proved.
        /// </summary>
        [TestMethod]
        public void BuildMemberListings_AThrowPartWay_DestroysWhatWasBuilt_AndListsNothing()
        {
            var run = ActiveSoloRun();
            var plan = PercentPlan();
            GivePlan(run, plan);

            var built = new List<WorldObject>();
            var oCreate = PeddlerEncounterType.CreateObject;
            var oLookup = PeddlerEncounterType.WeenieLookup;

            try
            {
                PeddlerEncounterType.WeenieLookup = _ => null;
                PeddlerEncounterType.CreateObject = w =>
                {
                    if (built.Count == 1)
                        throw new InvalidOperationException("test: the second factory call fails");

                    var o = Generic(w);
                    built.Add(o);
                    return o;
                };

                var listed = PeddlerEncounterType.BuildMemberListings(run, plan, run.OwnerGuid);

                Assert.AreEqual(0, listed.Count, "nothing listed");
                Assert.AreEqual(1, built.Count, "fixture: the first display was built");
                Assert.IsTrue(built[0].IsDestroyed, "and destroyed, not leaked");
            }
            finally
            {
                PeddlerEncounterType.CreateObject = oCreate;
                PeddlerEncounterType.WeenieLookup = oLookup;
            }
        }

        [TestMethod]
        public void ParseConfig_PricePercent_Errors_AndAbsentIsEmpty()
        {
            var cases = new (string Json, string Fragment)[]
            {
                (ShippedConfigWith(d => { d["pricePercent"] = new Dictionary<string, double> { { "no_such", 5 } }; return d; }), "config.pricePercent.no_such names no prize entry"),
                (ShippedConfigWith(d => { d["pricePercent"] = new Dictionary<string, double> { { "fiber", 0 } }; return d; }), "config.pricePercent.fiber must be a number above 0 and at most 100"),
                (ShippedConfigWith(d => { d["pricePercent"] = new Dictionary<string, double> { { "fiber", 100.5 } }; return d; }), "config.pricePercent.fiber must be"),
                (ShippedConfigWith(d => { d["pricePercent"] = new Dictionary<string, object> { { "fiber", "15" } }; return d; }), "config.pricePercent.fiber must be"),
                (ShippedConfigWith(d => { d["pricePercent"] = new[] { 15 }; return d; }), "config.pricePercent must be an object"),
            };

            foreach (var (json, fragment) in cases)
            {
                Assert.IsNull(Parse(json, out var error), fragment);
                StringAssert.Contains(error ?? "", fragment, json);
            }

            var hundred = Parse(ShippedConfigWith(d => { d["pricePercent"] = new Dictionary<string, double> { { "fiber", 100 } }; return d; }), out var hundredError);
            Assert.IsNotNull(hundred, hundredError);
            Assert.AreEqual(100, hundred.PricePercent["fiber"], "100 is allowed; only the listed entries are percent priced");
            Assert.AreEqual(1, hundred.PricePercent.Count);
            Assert.AreEqual(0, new PeddlerConfig().PricePercent.Count, "the default is flat");

            // A prize entry that exists but has no flat price (weight 0, so it needs none) cannot be percent priced: no floor.
            using var prizeDoc = JsonDocument.Parse("{\"entries\":[{\"id\":\"single\",\"wcids\":[7299],\"qty\":5,\"weight\":1},{\"id\":\"spare\",\"wcids\":[7299],\"qty\":1,\"weight\":0}]}");
            var prize = ThreadEncounterPrizeTable.Parse(prizeDoc.RootElement.Clone(), "test", new List<string>());
            using var cfg = JsonDocument.Parse("{\"prices\":{\"single\":70},\"cheap\":[\"single\"],\"pricePercent\":{\"spare\":5}}");
            Assert.IsNull(PeddlerEncounterType.ParseConfig(cfg.RootElement.Clone(), prize, out var floorError));
            StringAssert.Contains(floorError, "config.pricePercent.spare has no flat price in config.prices");
        }

        /// <summary>The placement block is the Defense row's, key for key (owner spec).</summary>
        [TestMethod]
        public void ShippedRow_PlacementIsDefensesExactly()
        {
            using var doc = ShippedFile();
            var types = doc.RootElement.GetProperty("types").EnumerateArray().ToList();
            var defense = types.Single(r => r.GetProperty("kind").GetString() == "defense").GetProperty("placement").GetRawText();
            var peddler = types.Single(r => r.GetProperty("kind").GetString() == "peddler").GetProperty("placement").GetRawText();

            Assert.AreEqual(Normalize(defense), Normalize(peddler));
        }

        private static string Normalize(string json) => new string(json.Where(c => !char.IsWhiteSpace(c)).ToArray());

        private static PeddlerConfig Parse(string json, out string error)
        {
            using var doc = JsonDocument.Parse(json);
            return PeddlerEncounterType.ParseConfig(doc.RootElement.Clone(), ShippedPrize(), out error);
        }

        private static string ShippedConfigWith(Func<Dictionary<string, object>, Dictionary<string, object>> edit)
        {
            var dict = JsonSerializer.Deserialize<Dictionary<string, object>>(ShippedRow().GetProperty("config").GetRawText());
            return JsonSerializer.Serialize(edit(dict));
        }

        [TestMethod]
        public void ParseConfig_ConfigErrors_AreRefused()
        {
            var cases = new (string Json, string Fragment)[]
            {
                (ShippedConfigWith(d => { d["colour"] = "red"; return d; }), "config.colour is not a peddler key"),
                (ShippedConfigWith(d => { d["windowSeconds"] = 30; return d; }), "config.windowSeconds is not a peddler key"),
                (ShippedConfigWith(d => { d["idleSeconds"] = 2; return d; }), "config.idleSeconds"),
                (ShippedConfigWith(d => { d["idleSeconds"] = 601; return d; }), "config.idleSeconds"),
                (ShippedConfigWith(d => { d["walkAwaySeconds"] = 0.5; return d; }), "config.walkAwaySeconds"),
                (ShippedConfigWith(d => { d["walkAwaySeconds"] = "5"; return d; }), "config.walkAwaySeconds"),
                (ShippedConfigWith(d => { d["offers"] = new { min = 4, max = 2 }; return d; }), "config.offers"),
                (ShippedConfigWith(d => { d["offers"] = new { min = 1, max = 3, step = 1 }; return d; }), "config.offers"),
                (ShippedConfigWith(d => { d["priceMultiplier"] = new { min = 0, max = 1 }; return d; }), "config.priceMultiplier"),
                (ShippedConfigWith(d => { d["itemJitter"] = 0.9; return d; }), "config.itemJitter"),
                (ShippedConfigWith(d => { d["npcWcid"] = 0; return d; }), "config.npcWcid"),
                (ShippedConfigWith(d => { d.Remove("prices"); return d; }), "config.prices"),
                (ShippedConfigWith(d => { d.Remove("cheap"); return d; }), "config.cheap"),
                (ShippedConfigWith(d => { d["cheap"] = new string[0]; return d; }), "config.cheap must name at least one"),
                (ShippedConfigWith(d => { d["cheap"] = new[] { "no_such" }; return d; }), "config.cheap entry no_such"),
                (ShippedConfigWith(d => { d["prices"] = Prices(p => p["no_such"] = 10); return d; }), "config.prices.no_such names no prize entry"),
                (ShippedConfigWith(d => { d["prices"] = Prices(p => p.Remove("fiber")); return d; }), "no price for prize entry fiber"),
                (ShippedConfigWith(d => { d["prices"] = Prices(p => p["fiber"] = 0); return d; }), "config.prices.fiber"),
                ("[]", "config must be an object"),
            };

            foreach (var (json, fragment) in cases)
            {
                Assert.IsNull(Parse(json, out var error), fragment);
                StringAssert.Contains(error ?? "", fragment, json);
            }

            Assert.IsNull(PeddlerEncounterType.ParseConfig(default, ShippedPrize(), out var absent));
            StringAssert.Contains(absent, "config is required");
        }

        /// <summary>
        /// A priced item entry with several wcids would show one item at Place and could build another at the clear: refused.
        /// A salvage entry may list several (it is a bundle, materials drawn at delivery); an unpriced multi-wcid entry with
        /// weight 0 is not the peddler's concern.
        /// </summary>
        [TestMethod]
        public void ParseConfig_RefusesAPricedMultiWcidItemEntry()
        {
            const string prizeJson = "{\"entries\":["
                + "{\"id\":\"gems\",\"wcids\":[1007900,1007901],\"qty\":1,\"weight\":1},"
                + "{\"id\":\"single\",\"wcids\":[7299],\"qty\":5,\"weight\":1},"
                + "{\"id\":\"bags\",\"weight\":1,\"salvage\":{\"wcids\":[21036,21051],\"bags\":{\"min\":2,\"max\":4}}},"
                + "{\"id\":\"spare\",\"wcids\":[1,2],\"qty\":1,\"weight\":0}"
                + "]}";

            using var prizeDoc = JsonDocument.Parse(prizeJson);
            var diagnostics = new List<string>();
            var prize = ThreadEncounterPrizeTable.Parse(prizeDoc.RootElement.Clone(), "test", diagnostics);
            Assert.AreEqual(4, prize.Entries.Count, string.Join("\n", diagnostics));

            PeddlerConfig ParseWith(string prices)
            {
                using var doc = JsonDocument.Parse("{\"prices\":" + prices + ",\"cheap\":[\"single\"]}");
                return PeddlerEncounterType.ParseConfig(doc.RootElement.Clone(), prize, out var e) ?? throw new AssertFailedException(e);
            }

            using (var bad = JsonDocument.Parse("{\"prices\":{\"gems\":240,\"single\":70,\"bags\":50},\"cheap\":[\"single\"]}"))
            {
                Assert.IsNull(PeddlerEncounterType.ParseConfig(bad.RootElement.Clone(), prize, out var error));
                StringAssert.Contains(error, "config.prices.gems: prize entry gems lists 2 wcids");
            }

            // Control: the same table without the multi-wcid item entry drawable parses (salvage with two wcids is fine,
            // and the weight-0 multi-wcid entry needs no price).
            const string prizeJsonNoGems = "{\"entries\":["
                + "{\"id\":\"single\",\"wcids\":[7299],\"qty\":5,\"weight\":1},"
                + "{\"id\":\"bags\",\"weight\":1,\"salvage\":{\"wcids\":[21036,21051],\"bags\":{\"min\":2,\"max\":4}}},"
                + "{\"id\":\"spare\",\"wcids\":[1,2],\"qty\":1,\"weight\":0}"
                + "]}";

            using var okDoc = JsonDocument.Parse(prizeJsonNoGems);
            prize = ThreadEncounterPrizeTable.Parse(okDoc.RootElement.Clone(), "test", diagnostics);
            Assert.IsNotNull(ParseWith("{\"single\":70,\"bags\":50}"));
        }

        private static Dictionary<string, int> Prices(Action<Dictionary<string, int>> edit)
        {
            var prices = new Dictionary<string, int>(ShippedConfig(out _).Prices);
            edit(prices);
            return prices;
        }

        // ======================================================================================
        // stock and prices (pure)
        // ======================================================================================

        private static readonly IReadOnlyList<PinnacleDef> Pool = new[] { Pinnacle };

        private static List<PeddlerOffer> Stock(PeddlerConfig config, ThreadEncounterPrizeTable prize, int seed, bool knotsOpen, out double mult)
            => PeddlerEncounterType.BuildStock(config, prize.Entries, _ => true, new Random(seed), knotsOpen ? 120 : (int?)null, knotsOpen ? Pool : Array.Empty<PinnacleDef>(), out mult);

        [TestMethod]
        public void Stock_Invariants_HoldOverManySeeds()
        {
            var config = ShippedConfig(out var prize);
            var counts = new HashSet<int>();

            for (var seed = 0; seed < 2000; seed++)
            {
                var stock = Stock(config, prize, seed, knotsOpen: seed % 2 == 0, out var mult);

                Assert.IsTrue(stock.Count >= 3 && stock.Count <= 5, $"seed {seed}: {stock.Count} offers");
                Assert.AreEqual(stock.Count, stock.Select(o => o.EntryId).Distinct().Count(), $"seed {seed}: offers are distinct");
                Assert.IsTrue(stock.Any(o => config.Cheap.Contains(o.EntryId)), $"seed {seed}: at least one cheap offer ({string.Join(",", stock.Select(o => o.EntryId))})");
                Assert.IsTrue(stock.All(o => o.Cheap == config.Cheap.Contains(o.EntryId)));
                Assert.IsTrue(mult >= 0.85 && mult <= 1.6, $"seed {seed}: multiplier {mult}");

                foreach (var offer in stock)
                {
                    var b = config.Prices[offer.EntryId];
                    var lo = Math.Max(1, (int)Math.Round(b * mult * 0.9, MidpointRounding.AwayFromZero));
                    var hi = (int)Math.Round(b * mult * 1.1, MidpointRounding.AwayFromZero);

                    Assert.IsTrue(offer.Price >= lo && offer.Price <= hi, $"seed {seed} {offer.EntryId}: {offer.Price} outside {lo}..{hi}");
                    Assert.IsTrue(offer.Price >= config.ReachablePriceRange().Min && offer.Price <= config.ReachablePriceRange().Max);
                }

                if (seed % 2 == 1)
                    Assert.IsFalse(stock.Any(o => o.Pick.Entry.IsFiber || o.Pick.Entry.IsPattern), $"seed {seed}: the Knots gate is closed, so no Fiber or Pattern");

                counts.Add(stock.Count);
            }

            CollectionAssert.AreEquivalent(new[] { 3, 4, 5 }, counts.ToList(), "every count in range is reached");
        }

        [TestMethod]
        public void Stock_IsDeterministicPerSeed()
        {
            var config = ShippedConfig(out var prize);

            for (var seed = 0; seed < 50; seed++)
            {
                var a = Stock(config, prize, seed, true, out var ma);
                var b = Stock(config, prize, seed, true, out var mb);

                Assert.AreEqual(ma, mb);
                CollectionAssert.AreEqual(a.Select(o => $"{o.EntryId}:{o.Price}:{o.Pick.Pattern?.Key}").ToList(), b.Select(o => $"{o.EntryId}:{o.Price}:{o.Pick.Pattern?.Key}").ToList(), $"seed {seed}");
            }
        }

        /// <summary>The cheap redraw replaces the LAST slot when no drawn offer is cheap: forced here by a table whose only cheap entry has a tiny weight.</summary>
        [TestMethod]
        public void Stock_NoCheapDrawn_RedrawsTheLastSlotFromCheap()
        {
            var entries = new[]
            {
                new ThreadEncounterPrizeEntry("a", new[] { 1u }, 1, 1000),
                new ThreadEncounterPrizeEntry("b", new[] { 2u }, 1, 1000),
                new ThreadEncounterPrizeEntry("c", new[] { 3u }, 1, 1000),
                new ThreadEncounterPrizeEntry("cheap", new[] { 4u }, 1, 0.0001),
            };

            var config = new PeddlerConfig
            {
                OffersMin = 3,
                OffersMax = 3,
                Prices = new Dictionary<string, int> { { "a", 100 }, { "b", 100 }, { "c", 100 }, { "cheap", 10 } },
                Cheap = new[] { "cheap" },
            };

            for (var seed = 0; seed < 100; seed++)
            {
                var stock = PeddlerEncounterType.BuildStock(config, entries, _ => true, new Random(seed), null, null, out _);

                Assert.AreEqual(3, stock.Count);
                Assert.AreEqual("cheap", stock[2].EntryId, $"seed {seed}: the last slot is the cheap redraw");
                Assert.AreEqual(2, stock.Take(2).Count(o => o.EntryId != "cheap"));
            }
        }

        [TestMethod]
        public void Stock_StopsEarly_WhenFewerEntriesAreAvailable_AndIsEmptyWhenNone()
        {
            var config = ShippedConfig(out var prize);
            var onlyTwo = new HashSet<uint> { 20630, 7299 };

            var stock = PeddlerEncounterType.BuildStock(config, prize.Entries, w => onlyTwo.Contains(w), new Random(3), null, null, out _);
            Assert.AreEqual(2, stock.Count, "only two entries have weenies");

            Assert.AreEqual(0, PeddlerEncounterType.BuildStock(config, prize.Entries, _ => false, new Random(3), null, null, out _).Count);
        }

        [TestMethod]
        public void FinalPrice_RoundsHalfAwayFromZero_FloorsAtOne()
        {
            Assert.AreEqual(1, PeddlerEncounterType.FinalPrice(1, 0.1, -0.5));
            Assert.AreEqual(563, PeddlerEncounterType.FinalPrice(320, 1.6, 0.1));
            Assert.AreEqual((38, 563), ShippedConfig(out _).ReachablePriceRange());
        }

        // ======================================================================================
        // displays
        // ======================================================================================

        [TestMethod]
        public void BundleNames_AreTheOwnerWording()
        {
            var prize = ShippedPrize();
            ThreadEncounterPrizeEntry Entry(string id) => prize.Entries.Single(e => e.Id == id);

            Assert.AreEqual("Trade Note (250,000) x5", PeddlerEncounterType.BundleName(Entry("trade_note_250k"), "Trade Note (250,000)"));
            Assert.AreEqual("Diamond Scarab x5", PeddlerEncounterType.BundleName(Entry("diamond_scarab"), "Diamond Scarab"));
            Assert.AreEqual("Promissory Note x5", PeddlerEncounterType.BundleName(Entry("promissory_note"), "Promissory Note"));
            Assert.AreEqual("Salvage Bundle (2-4 bags)", PeddlerEncounterType.BundleName(Entry("salvage_bags"), "Salvaged Amethyst"));
            Assert.AreEqual("Sold by the Wandering Peddler. Goes straight into your pack when you buy it.", PeddlerEncounterType.BundleDescription);
        }

        private static Weenie ItemWeenie(uint wcid, string name, ItemType type, uint icon)
            => new Weenie
            {
                WeenieClassId = wcid,
                WeenieType = WeenieType.Generic,
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.ItemType, (int)type } },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, name } },
                PropertiesDID = new Dictionary<PropertyDataId, uint> { { PropertyDataId.Icon, icon } },
            };

        private static WorldObject Typed(uint wcid, ItemType type)
        {
            var o = Generic(wcid);
            o.ItemType = type;
            return o;
        }

        /// <summary>Runs <paramref name="body"/> with the display seams swapped to in-memory builders; restores them in a finally.</summary>
        internal static void WithDisplaySeams(Dictionary<uint, Weenie> weenies, Action<List<uint>> body)
        {
            var created = new List<uint>();
            var oCreate = PeddlerEncounterType.CreateObject;
            var oLookup = PeddlerEncounterType.WeenieLookup;
            var oFiber = KnotDrops.FiberFactory;
            var oPattern = KnotDrops.PatternFactory;

            try
            {
                PeddlerEncounterType.CreateObject = w =>
                {
                    created.Add(w);
                    var type = weenies.TryGetValue(w, out var wn) ? (ItemType)wn.PropertiesInt[PropertyInt.ItemType] : ItemType.Misc;
                    return Typed(w, type);
                };
                PeddlerEncounterType.WeenieLookup = w => weenies.TryGetValue(w, out var wn) ? wn : null;
                KnotDrops.FiberFactory = level => { created.Add(KnotItems.FiberWcid); var o = Generic(KnotItems.FiberWcid, KnotItemKind.Fiber); o.Name = $"Fiber (Level {level})"; return o; };
                KnotDrops.PatternFactory = def => { created.Add(KnotItems.PatternWcid); var o = Generic(KnotItems.PatternWcid, KnotItemKind.Pattern); o.Name = $"Pattern: {def.Name}"; return o; };

                body(created);
            }
            finally
            {
                PeddlerEncounterType.CreateObject = oCreate;
                PeddlerEncounterType.WeenieLookup = oLookup;
                KnotDrops.FiberFactory = oFiber;
                KnotDrops.PatternFactory = oPattern;
            }
        }

        private static readonly Dictionary<uint, Weenie> RetailItems = new Dictionary<uint, Weenie>
        {
            { 20630, ItemWeenie(20630, "Trade Note (250,000)", ItemType.PromissoryNote, 0x06002761) },
            { 7299, ItemWeenie(7299, "Diamond Scarab", ItemType.SpellComponents, 0x06001CE9) },
            { 43901, ItemWeenie(43901, "Promissory Note", ItemType.Misc, 0x06006F64) },
            { 21036, ItemWeenie(21036, "Salvaged Amethyst", ItemType.TinkeringMaterial, 0x0600102C) },
            { 1007900, ItemWeenie(1007900, "Rear Guard Mantle", ItemType.Gem, 0x06001234) },
            { 555, ItemWeenie(555, "Single Note", ItemType.PromissoryNote, 0x06005555) },
            { 556, ItemWeenie(556, "Coin", ItemType.Money, 0x06005556) },
        };

        [TestMethod]
        public void BuildDisplay_ShellsBundles_ListsRealSingles_AndPricesEveryDisplay()
        {
            var config = ShippedConfig(out var prize);
            ThreadEncounterPrizeEntry Entry(string id) => prize.Entries.Single(e => e.Id == id);

            WithDisplaySeams(RetailItems, created =>
            {
                var note = PeddlerEncounterType.BuildDisplay(new PeddlerOffer(new ThreadEncounterPrizePick(Entry("trade_note_250k"), new[] { 20630u }), 52, true), config);
                Assert.AreEqual(config.BundleWcid, note.WeenieClassId, "a quantity-5 stack is a shell");
                Assert.AreEqual("Trade Note (250,000) x5", note.Name);
                Assert.AreEqual(0x06002761u, note.IconId, "the real item's icon");
                Assert.AreEqual(PeddlerEncounterType.BundleDescription, note.LongDesc);
                Assert.AreEqual(52, note.Value);
                Assert.AreEqual(ItemType.Misc, note.ItemType, "the shell's type, never PromissoryNote");

                var salvage = PeddlerEncounterType.BuildDisplay(new PeddlerOffer(new ThreadEncounterPrizePick(Entry("salvage_bags"), new[] { 21036u, 21036u }), 61, true), config);
                Assert.AreEqual(config.BundleWcid, salvage.WeenieClassId);
                Assert.AreEqual("Salvage Bundle (2-4 bags)", salvage.Name);
                Assert.AreEqual(0x0600102Cu, salvage.IconId);

                var gem = PeddlerEncounterType.BuildDisplay(new PeddlerOffer(new ThreadEncounterPrizePick(Entry("mantle_gem"), new[] { 1007900u }), 300, false), config);
                Assert.AreEqual(1007900u, gem.WeenieClassId, "a single gem is the real gem");
                Assert.AreEqual(300, gem.Value);

                var pattern = PeddlerEncounterType.BuildDisplay(new PeddlerOffer(new ThreadEncounterPrizePick(Entry("pattern"), new[] { KnotItems.PatternWcid }, 0, Pinnacle), 400, false), config);
                Assert.AreEqual("Pattern: P One", pattern.Name, "the real Pattern for the rolled pinnacle");
                Assert.AreEqual(400, pattern.Value);

                var fiber = PeddlerEncounterType.BuildDisplay(new PeddlerOffer(new ThreadEncounterPrizePick(Entry("fiber"), new[] { KnotItems.FiberWcid }, 120), 150, false), config);
                Assert.AreEqual("Fiber (Level 120)", fiber.Name);
                Assert.AreEqual(150, fiber.Value);

                // A qty-1 PromissoryNote- or Money-typed item would be priced at 1.15 (or read as change): always a shell.
                var single = new ThreadEncounterPrizeEntry("single_note", new[] { 555u }, 1, 1);
                var coin = new ThreadEncounterPrizeEntry("coin", new[] { 556u }, 1, 1);
                Assert.AreEqual(config.BundleWcid, PeddlerEncounterType.BuildDisplay(new PeddlerOffer(new ThreadEncounterPrizePick(single, new[] { 555u }), 10, false), config).WeenieClassId);
                Assert.AreEqual(config.BundleWcid, PeddlerEncounterType.BuildDisplay(new PeddlerOffer(new ThreadEncounterPrizePick(coin, new[] { 556u }), 10, false), config).WeenieClassId);
                Assert.AreEqual("Single Note", PeddlerEncounterType.BuildDisplay(new PeddlerOffer(new ThreadEncounterPrizePick(single, new[] { 555u }), 10, false), config).Name);
            });
        }

        [TestMethod]
        public void BuildDisplay_RefusesARealObjectThatIsPromissoryNoteTyped()
        {
            var config = ShippedConfig(out _);
            var entry = new ThreadEncounterPrizeEntry("odd", new[] { 777u }, 1, 1);
            var weenies = new Dictionary<uint, Weenie> { { 777, ItemWeenie(777, "Odd", ItemType.Misc, 1) } };

            WithDisplaySeams(weenies, _ =>
            {
                var saved = PeddlerEncounterType.CreateObject;
                PeddlerEncounterType.CreateObject = w => Typed(w, ItemType.PromissoryNote);

                Assert.IsNull(PeddlerEncounterType.BuildDisplay(new PeddlerOffer(new ThreadEncounterPrizePick(entry, new[] { 777u }), 10, false), config),
                    "an object whose weenie said Misc but which built PromissoryNote-typed is never listed");
                PeddlerEncounterType.CreateObject = saved;
            });
        }

        // ======================================================================================
        // RejectRun, Place, and a second peddler
        // ======================================================================================

        [TestMethod]
        public void RejectRun_PooledLootOff_NeverSpawns()
        {
            var type = new PeddlerEncounterType();

            StringAssert.Contains(type.RejectRun(null, ActiveSoloRun(pooled: false)), "pooled loot is off");
            Assert.IsNull(type.RejectRun(null, ActiveSoloRun(pooled: true)));
            Assert.AreEqual(1, typeof(PeddlerEncounterType).GetMethods().Count(m => m.Name == nameof(IThreadEncounterType.RejectRun) && m.DeclaringType == typeof(PeddlerEncounterType)),
                "the type overrides the framework's allow-all default");
        }

        /// <summary>A pooled, Active dungeon run with one point, and a peddler pick at it.</summary>
        private static (ThreadDungeonRun Run, ThreadEncounterPlaceRequest Request) PlaceRequest(PeddlerEncounterType type, bool pooled = true)
        {
            var point = Point(DungeonCell(), 10, 10);
            var run = DungeonRun(new[] { point }, max: 1);
            run.MarkPooledLoot(pooled);

            var def = Type("peddler", kind: PeddlerEncounterType.KindName);
            def.Config = ShippedRow().GetProperty("config");

            var instance = run.NewEncounterInstance(new ThreadEncounterPick(1, def, Candidate(point), 7), type);
            return (run, new ThreadEncounterPlaceRequest(run, null, instance, null));
        }

        private static void WithPlaceSeams(Func<WorldObject, bool> enterWorld, Action<List<WorldObject>> body)
        {
            var built = new List<WorldObject>();
            var oPrize = PeddlerEncounterType.PrizeTable;
            var oKnots = PeddlerEncounterType.KnotsInputs;
            var oExists = PeddlerEncounterType.WeenieExists;
            var oEnter = PeddlerEncounterType.EnterWorld;

            try
            {
                var prize = ShippedPrize();
                PeddlerEncounterType.PrizeTable = () => prize;
                PeddlerEncounterType.KnotsInputs = r => (null, Array.Empty<PinnacleDef>());
                PeddlerEncounterType.WeenieExists = _ => true;
                PeddlerEncounterType.EnterWorld = enterWorld;

                WithDisplaySeams(RetailItems, _ =>
                {
                    var inner = PeddlerEncounterType.CreateObject;
                    PeddlerEncounterType.CreateObject = w =>
                    {
                        var o = w == PeddlerConfig.DefaultNpcWcid ? ThreadPeddlerVendorTests.NewPeddler() : inner(w);
                        built.Add(o);
                        return o;
                    };

                    body(built);
                });
            }
            finally
            {
                PeddlerEncounterType.PrizeTable = oPrize;
                PeddlerEncounterType.KnotsInputs = oKnots;
                PeddlerEncounterType.WeenieExists = oExists;
                PeddlerEncounterType.EnterWorld = oEnter;
            }
        }

        [TestMethod]
        public void Place_RecordsThePlan_CreatesNothing_AndLogsTheOffers()
        {
            var type = new PeddlerEncounterType();
            var (run, request) = PlaceRequest(type);

            WithPlaceSeams(_ => true, built =>
            {
                Assert.IsTrue(type.Place(request, out var failure), failure);

                Assert.AreEqual(0, built.Count, "clear only: nothing exists mid-run, not even a display");
                var plan = run.PeddlerPlan;
                Assert.IsNotNull(plan, "the run HAS a peddler");
                Assert.AreSame(plan, request.Instance.State);
                Assert.AreSame(request.Instance, plan.Instance);
                Assert.IsTrue(plan.Offers.Count >= 3 && plan.Offers.Count <= 5);
                Assert.IsFalse(plan.Offers.Any(o => o.Pick.Entry.IsFiber || o.Pick.Entry.IsPattern), "the Knots gate is closed here");
                Assert.AreEqual(120, plan.Config.IdleSeconds);
                Assert.IsTrue(run.PeddlerClaimed);
                Assert.IsNull(run.PeddlerNpc, "no NPC until the clear");
                Assert.IsTrue(run.EncounterOutcomes.Any(l => l.Contains("peddler for the clear with")), string.Join("\n", run.EncounterOutcomes));
            });
        }

        private static Position SomePosition(ThreadDungeonRun run) => new Position(DungeonCell(), 10, 10, 0, 0, 0, 0, 1, run.Instance);

        [TestMethod]
        public void CreateNpcAt_BindsTheStall_TracksThePeddler()
        {
            var type = new PeddlerEncounterType();
            var (run, request) = PlaceRequest(type);

            WithPlaceSeams(_ => true, built =>
            {
                Assert.IsTrue(type.Place(request, out var failure), failure);
                var plan = run.PeddlerPlan;

                var peddler = PeddlerEncounterType.CreateNpcAt(run, plan, SomePosition(run), out failure);
                Assert.IsNotNull(peddler, failure);

                Assert.AreSame(run, peddler.Run);
                Assert.AreSame(request.Instance, peddler.P_ThreadEncounter, "tracked before EnterWorld");
                Assert.AreEqual(plan.Offers.Count, peddler.DefaultItemsForSale.Count);
                Assert.AreEqual((120.0, 5.0), peddler.Timings, "the row's idle and walk-away seconds");

                foreach (var (guid, purchase) in peddler.Listings)
                {
                    var offer = plan.Offers.Single(o => o.EntryId == purchase.EntryId);
                    Assert.AreEqual(offer.Price, purchase.Price, "the listing's record carries the offer's price");
                    Assert.AreEqual(offer.Price, peddler.DefaultItemsForSale[guid].Value, "and the display shows it");
                }
            });
        }

        /// <summary>
        /// The plan's idleSeconds and walkAwaySeconds reach the NPC (review of #2237: binding the defaults passed every test).
        /// Mutation-proved: CreateNpcAt binding PeddlerConfig's defaults fails this.
        /// </summary>
        [TestMethod]
        public void CreateNpcAt_BindsThePlansIdleAndWalkAwaySeconds()
        {
            var type = new PeddlerEncounterType();
            var (run, request) = PlaceRequest(type);

            WithPlaceSeams(_ => true, built =>
            {
                Assert.IsTrue(type.Place(request, out var failure), failure);
                var shipped = run.PeddlerPlan;
                var c = shipped.Config;
                var config = new PeddlerConfig
                {
                    NpcWcid = c.NpcWcid, CurrencyWcid = c.CurrencyWcid, BundleWcid = c.BundleWcid,
                    IdleSeconds = 37, WalkAwaySeconds = 3,
                    OffersMin = c.OffersMin, OffersMax = c.OffersMax, PriceMultiplierMin = c.PriceMultiplierMin, PriceMultiplierMax = c.PriceMultiplierMax,
                    ItemJitter = c.ItemJitter, Prices = c.Prices, Cheap = c.Cheap,
                };
                var plan = new PeddlerPlan(request.Instance, config, shipped.Offers, shipped.SpawnMultiplier);

                var peddler = PeddlerEncounterType.CreateNpcAt(run, plan, SomePosition(run), out failure);
                Assert.IsNotNull(peddler, failure);
                Assert.AreEqual((37.0, 3.0), peddler.Timings);
            });
        }

        [TestMethod]
        public void CreateNpcAt_EnterWorldFails_LeavesNothingBehind()
        {
            var type = new PeddlerEncounterType();
            var (run, request) = PlaceRequest(type);

            WithPlaceSeams(_ => false, built =>
            {
                Assert.IsTrue(type.Place(request, out var failure), failure);

                Assert.IsNull(PeddlerEncounterType.CreateNpcAt(run, run.PeddlerPlan, SomePosition(run), out failure));
                StringAssert.Contains(failure, "failed to enter the world");
                Assert.IsTrue(built.Count >= 4, "the peddler and its displays were built");
                Assert.IsTrue(built.All(o => o.IsDestroyed), "the peddler and every display are destroyed");
                Assert.IsNull(built.OfType<ThreadPeddlerVendor>().Single().P_ThreadEncounter);
                Assert.IsTrue(run.PeddlerClaimed, "the plan keeps the claim: a failed appearance never frees a second peddler");
            });
        }

        [TestMethod]
        public void ShippedRow_IsCappedAtOnePerRun()
        {
            using var doc = ShippedFile();
            var row = doc.RootElement.GetProperty("types").EnumerateArray().Single(r => r.GetProperty("id").GetString() == "peddler");
            Assert.AreEqual(1, row.GetProperty("maxPerRun").GetInt32());

            var problems = new List<string>();
            var store = ThreadEncounterStore.Parse(File.ReadAllText(SalvageRollerVendorTests.FindInSourceTree("Content/dungeons/dynamic/encounters.json")), null, problems);
            Assert.AreEqual(1, store.Types.Single(t => t.Id == "peddler").MaxPerRun, string.Join("\n", problems));
        }

        /// <summary>A second pick on an existing run (a fresh instance, the same run).</summary>
        private static ThreadEncounterPlaceRequest SecondRequest(PeddlerEncounterType type, ThreadDungeonRun run)
        {
            var point = Point(DungeonCell(), 20, 20);
            var def = Type("peddler", kind: PeddlerEncounterType.KindName);
            def.Config = ShippedRow().GetProperty("config");
            var instance = run.NewEncounterInstance(new ThreadEncounterPick(2, def, Candidate(point), 9), type);
            return new ThreadEncounterPlaceRequest(run, null, instance, null);
        }

        [TestMethod]
        public void Place_refuses_a_second_peddler_for_the_same_run()
        {
            var type = new PeddlerEncounterType();
            var (run, first) = PlaceRequest(type);
            var second = SecondRequest(type, run);

            WithPlaceSeams(_ => true, built =>
            {
                Assert.IsTrue(type.Place(first, out var failure), failure);
                Assert.IsTrue(run.PeddlerClaimed);
                var builtBefore = built.Count;

                Assert.IsFalse(type.Place(second, out failure));
                Assert.AreEqual("the run already had its peddler", failure);
                Assert.AreEqual(builtBefore, built.Count, "the refusal is decided before anything is created");
                Assert.IsNull(second.Instance.State);
                Assert.IsTrue(run.PeddlerClaimed, "the recorded plan keeps the claim");
                Assert.AreSame(first.Instance.State, run.PeddlerPlan, "the first plan stands");
            });
        }

        [TestMethod]
        public void Place_failure_before_the_plan_releases_the_peddler_claim()
        {
            var type = new PeddlerEncounterType();
            var (run, first) = PlaceRequest(type);
            var second = SecondRequest(type, run);

            WithPlaceSeams(_ => true, built =>
            {
                var empty = PeddlerEncounterType.PrizeTable;
                PeddlerEncounterType.PrizeTable = () => ThreadEncounterPrizeTable.Empty;

                try
                {
                    Assert.IsFalse(type.Place(first, out var failure));
                    Assert.IsNotNull(failure);
                }
                finally
                {
                    PeddlerEncounterType.PrizeTable = empty;
                }

                Assert.IsFalse(run.PeddlerClaimed, "a failed Place leaves the slot free");
                Assert.IsNull(run.PeddlerPlan);

                Assert.IsTrue(type.Place(second, out var ok), ok);
                Assert.IsTrue(run.PeddlerClaimed);
            });
        }

        [TestMethod]
        public void Place_refuses_when_run_not_active()
        {
            var type = new PeddlerEncounterType();
            var (run, request) = PlaceRequest(type);

            WithPlaceSeams(_ => true, built =>
            {
                Assert.IsTrue(run.MarkEnded("test", out _));
                Assert.IsFalse(type.Place(request, out var failure));
                StringAssert.Contains(failure, "not Active");
                Assert.AreEqual(0, built.Count, "decided before anything is created");
                Assert.IsFalse(run.PeddlerClaimed);
                Assert.IsNull(run.PeddlerPlan);
            });
        }

        [TestMethod]
        public void Place_refuses_when_run_is_cleared()
        {
            var type = new PeddlerEncounterType();
            var (run, request) = PlaceRequest(type);

            WithPlaceSeams(_ => true, built =>
            {
                run.RecordKill(false);
                Assert.AreEqual(ThreadDungeonRunState.Cleared, run.State, "fixture: Cleared");

                Assert.IsFalse(type.Place(request, out var failure));
                StringAssert.Contains(failure, "not Active");
                Assert.AreEqual(0, built.Count);
                Assert.IsFalse(run.PeddlerClaimed);
                Assert.IsNull(run.PeddlerPlan, "a run that has already cleared gets no peddler");
            });
        }

        [TestMethod]
        public void Place_refuses_when_run_is_starting()
        {
            var type = new PeddlerEncounterType();
            var (_, request) = PlaceRequest(type);
            var starting = ThreadLootTestFixtures.NewRun(true);
            Assert.AreEqual(ThreadDungeonRunState.Starting, starting.State, "fixture: Starting (never populated)");
            var startingRequest = new ThreadEncounterPlaceRequest(starting, null, request.Instance, null);

            WithPlaceSeams(_ => true, built =>
            {
                Assert.IsFalse(type.Place(startingRequest, out var failure));
                StringAssert.Contains(failure, "not Active");
                Assert.AreEqual(0, built.Count);
                Assert.IsFalse(starting.PeddlerClaimed);
            });
        }

        /// <summary>
        /// #2234's guarantee, clear-only form: once the plan is recorded the claim is never given back, so neither a second
        /// pick nor an admin force-place can give the run another peddler, and the NPC the plan later creates is the only one.
        /// </summary>
        [TestMethod]
        public void Place_keeps_the_claim_once_the_plan_is_recorded()
        {
            var type = new PeddlerEncounterType();
            var (run, first) = PlaceRequest(type);
            var second = SecondRequest(type, run);

            WithPlaceSeams(_ => true, built =>
            {
                Assert.IsTrue(type.Place(first, out var failure), failure);

                run.ReleasePeddlerClaim();
                Assert.IsTrue(run.PeddlerClaimed, "a recorded plan holds the claim: a stray release is refused");

                Assert.IsFalse(type.Place(second, out failure));
                Assert.AreEqual("the run already had its peddler", failure);
                Assert.AreEqual(0, built.Count);
            });
        }

        [TestMethod]
        public void Place_RefusesWithoutPooledLoot_AndOnAConfigError()
        {
            var type = new PeddlerEncounterType();

            WithPlaceSeams(_ => true, built =>
            {
                var (_, unpooled) = PlaceRequest(type, pooled: false);
                Assert.IsFalse(type.Place(unpooled, out var failure));
                StringAssert.Contains(failure, "pooled loot is off");

                var (_, bad) = PlaceRequest(type);
                bad.Instance.Pick.Def.Config = JsonDocument.Parse("{\"colour\":1}").RootElement.Clone();
                Assert.IsFalse(type.Place(bad, out failure));
                StringAssert.Contains(failure, "not a peddler key");

                Assert.AreEqual(0, built.Count, "both refusals are decided before anything is created");
            });
        }

        // ======================================================================================
        // source pins
        // ======================================================================================

        /// <summary>Source pin (mutation-proved): the type is registered with the others in ThreadDungeonManager.Initialize, before the store is published.</summary>
        [TestMethod]
        public void Source_TypeIsRegisteredAtInitialize()
        {
            var code = AccountVaultPurgeTests.StripComments(File.ReadAllText(SalvageRollerVendorTests.FindInSourceTree("Source/ACE.Server/ThreadDungeons/ThreadDungeonManager.cs")));

            var register = code.IndexOf("ThreadEncounterTypes.Register(new PeddlerEncounterType());", StringComparison.Ordinal);
            var store = code.IndexOf("var store = ThreadDungeonStore.LoadFromServerConfig();", StringComparison.Ordinal);

            Assert.IsTrue(register > 0 && register < store, $"registered before the store is published (register={register}, store={store})");
        }

        /// <summary>Source pin (mutation-proved): the shipped peddler weenie carries the vendor rows the window and price rely on.</summary>
        [TestMethod]
        public void Source_PeddlerWeenie_CarriesTheVendorRows()
        {
            var sql = File.ReadAllText(SalvageRollerVendorTests.FindInSourceTree("Content/sql/weenies/1007981 Wandering Peddler.sql"));

            StringAssert.Contains(sql, "VALUES (1007981, 'ace1007981-wanderingpeddler', 10 ", "type 10 Creature, never a retail Vendor");
            StringAssert.Contains(sql, "(1007981, 9096, True )", "the factory opt-in");
            StringAssert.Contains(sql, "(1007981,  38,       1) /* SellPrice", "SellPrice 1.0, so Value is the price");
            StringAssert.Contains(sql, "(1007981,  57,    1007982) /* AlternateCurrency", "the Cache Pledge");
            StringAssert.Contains(sql, "(1007981,  74,          0) /* MerchandiseItemTypes", "buys nothing");
            StringAssert.Contains(sql, "(1007981,  19, False) /* Attackable", "not attackable");
        }
    }
}
