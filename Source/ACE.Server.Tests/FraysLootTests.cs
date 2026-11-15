using ACE.Server.Frays;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Frays loot: a brute pack drops as much as a whole swarm pack, and Frays beat every tier 8 spot (tier 8 loot,
    /// and more items a creature than retail tier 8 mobs).
    /// </summary>
    [TestClass]
    public class FraysLootTests
    {
        [TestInitialize]
        public void Init() => FraysSettings.ResetCacheForTest();

        // The most-used retail tier 8 table (treasure_death 243) drops 4 to 5 magic items; the best ordinary 2, mundane 4.
        private const double RetailMagic = 4.5, RetailOrdinary = 2;

        // No common retail tier 8 table drops more than about 5 items in all (243: 4 to 5 magic; 240 and 245: about 4.5).
        private const double RetailTotal = 5;

        [TestMethod]
        public void ASwarmCreature_DropsMoreThanARetailTierEightMob()
        {
            var swarm = FraysSettings.Number(FraysSettings.SwarmHealthShare) * FraysRules.PayPerCreature(0);
            Assert.IsTrue(swarm * FraysSettings.Number(FraysSettings.LootItemsPerStandard) > RetailMagic, "magic");
            Assert.IsTrue(swarm * FraysSettings.Number(FraysSettings.LootOrdinaryPerStandard) > RetailOrdinary, "ordinary");
            Assert.IsTrue(swarm * (FraysSettings.Number(FraysSettings.LootItemsPerStandard) + FraysSettings.Number(FraysSettings.LootOrdinaryPerStandard)
                + FraysSettings.Number(FraysSettings.LootMundanePerStandard)) > RetailTotal, "all items");
        }

        [TestMethod]
        public void OneBrutePack_DropsWhatOneSwarmPackDoes()
        {
            foreach (var heat in new[] { 0, 7, 10, 20 })
            {
                var swarm = FraysRules.TypedPack(FraysPackType.Swarm, heat, 0, out var swarmEach);
                var brute = FraysRules.TypedPack(FraysPackType.Brute, heat, 0, out var bruteEach);
                var share = FraysSettings.Number(FraysSettings.SwarmHealthShare);
                Assert.AreEqual(swarm.Count * share * swarmEach /* swarmEach is 1 */, brute.Count * bruteEach, 1e-9, $"Heat {heat}: loot share of the whole pack");
            }
        }

        [TestMethod]
        public void ACorpse_NeverHoldsMoreThanItsCap_AndKeepsItsMix()
        {
            Assert.AreEqual((15, 6, 5), FraysRules.FitCorpse(15, 6, 5, 110), "fits: unchanged");
            var (m, o, u) = FraysRules.FitCorpse(150, 60, 50, 110);
            Assert.IsTrue(m + o + u <= 110);
            Assert.IsTrue(m > o && o > u, "the mix stays");
            Assert.AreEqual(1, FraysRules.FitCorpse(1, 300, 0, 110).Magic, "a rolled single item stays");
        }

        [TestMethod]
        public void ABrutePack_IsWorthASwarmPack_WhenItsCorpsesOverflow()
        {
            // A corpse holds at most its cap; what it can't hold goes to the key bar as keys to a village chest.
            var cap = (int)FraysSettings.Number(FraysSettings.LootCorpseCap);
            var perKey = FraysSettings.Number(FraysSettings.LootOverflowPerKey);
            double Magic(double s) => s * FraysSettings.Number(FraysSettings.LootItemsPerStandard);
            double Ordinary(double s) => s * FraysSettings.Number(FraysSettings.LootOrdinaryPerStandard);
            double Mundane(double s) => s * FraysSettings.Number(FraysSettings.LootMundanePerStandard);

            // items in the corpse plus Vault Keys at perKey items each
            double Worth(double share)
            {
                var (m, o, u, over) = FraysRules.SplitCorpse(Magic(share), Ordinary(share), Mundane(share), cap);
                Assert.IsTrue(m + o + u <= cap + 1e-9, "the corpse holds no more than its cap");
                return m + o + u + FraysRules.OverflowKeys(over, perKey) * perKey;
            }

            foreach (var heat in new[] { 0, 7, 10, 14, 20 })
                foreach (var heatHealth in new[] { 1.0, 1.83, 3.01, 7.03 })
                    foreach (var gilded in new[] { 1.0, FraysSettings.Number(FraysSettings.RichLootMult) })
                    {
                        var swarm = FraysRules.TypedPack(FraysPackType.Swarm, heat, 0, out _);
                        var brute = FraysRules.TypedPack(FraysPackType.Brute, heat, 0, out var each);
                        var k = heatHealth * gilded;
                        var swarmPack = swarm.Count * Worth(FraysSettings.Number(FraysSettings.SwarmHealthShare) * k);
                        var brutePack = brute.Count * Worth(each * k);
                        Assert.AreEqual(swarmPack, brutePack, 1e-6, $"Heat {heat}, health x{heatHealth}, loot x{gilded}");
                    }

            var (_, _, _, none) = FraysRules.SplitCorpse(10, 4, 3, cap);
            Assert.AreEqual(0, none, "a corpse under its cap sends nothing to the key bar");
            Assert.AreEqual(0, FraysRules.OverflowKeys(50, 0), "loot_overflow_per_key 0: the overflow is lost");
        }

        /// <summary>Average pay share over an hour for a camper clearing <paramref name="perHour"/> standard creatures an hour, one at a time.</summary>
        private static double HourShare(double perHour)
        {
            var c = new FraysCamper();
            var gap = 3600 / perHour;
            double sum = 0;
            var kills = (int)perHour;

            for (var k = 0; k < kills; k++)
                sum += FraysFire.EarnFactor(c, 1, 1000 + k * gap);

            return sum / kills;
        }

        [TestMethod]
        public void EarnKnee_LeavesEverySimBuildAlone_AndThinsOnlyARunawayPace()
        {
            // Single-target and AoE builds earn the same, and the ceiling is hardly noticed. The fastest build
            // tested clears about 356 standard creatures an hour, the best AoE build about 274.
            Assert.AreEqual(1, HourShare(274), 1e-9, "the best AoE build");
            Assert.AreEqual(1, HourShare(356), 1e-9, "the fastest build");
            Assert.AreEqual(1, HourShare(390), 1e-9, "a little faster than any build");

            var runaway = HourShare(1000);
            Assert.IsTrue(runaway < 0.7, $"a pace past every player is thinned ({runaway:P0} of full pay)");
            Assert.IsTrue(1000 * runaway > 400 * 1.5, "but speed still pays: about 1.6 times a camper at the ceiling");
        }

        [TestMethod]
        public void Durations_ReadAsPlayersSayThem()
        {
            Assert.AreEqual("45 seconds", FraysRules.Duration(44.2));
            Assert.AreEqual("1 minute", FraysRules.Duration(60));
            Assert.AreEqual("3 minutes 30 seconds", FraysRules.Duration(210));
            Assert.AreEqual("The Gilded pack falls. It returns in 3 minutes 30 seconds.", FraysFire.GildedClearedText(210));
        }
    }
}
