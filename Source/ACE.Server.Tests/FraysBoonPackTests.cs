using System;
using System.Collections.Generic;
using System.Linq;
using ACE.Server.Frays;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>Golden Cinders without its loot step, Packmaster as Bellows, bigger affix packs not resting longer.</summary>
    [TestClass]
    public class FraysBoonPackTests
    {
        private Func<string, string> previousSource;

        [TestInitialize]
        public void Init()
        {
            previousSource = FraysSettings.StringSource;
            FraysSettings.StringSource = _ => null;
            FraysSettings.ResetCacheForTest();
        }

        [TestCleanup]
        public void Cleanup()
        {
            FraysSettings.StringSource = previousSource;
            FraysSettings.ResetCacheForTest();
        }

        private static FraysBoonDef Boon(string key) => FraysBoons.All.First(b => b.Key == key);

        [TestMethod]
        public void GoldenCinders_IsEmbersOnly()
        {
            var m = new FraysCampMods();
            Boon("goldencinders").Grow(m, 0.15);
            Assert.AreEqual(1.15, m.Embers, 1e-9);
            Assert.AreEqual("+{0} Embers", Boon("goldencinders").Effect);
        }

        [TestMethod]
        public void Packmaster_IsTenPercentMoreCreatures_NotABiggerPack()
        {
            var m = new FraysCampMods();
            Boon("packmaster").Grow(m, 0.03);
            Assert.AreEqual(0, m.PackDelta, "not in the pack size, so not in the rest scaling");
            Assert.AreEqual(0.10, m.PackShare, 1e-9);
            Assert.AreEqual(1.03, m.Embers, 1e-9);
            StringAssert.Contains(Boon("packmaster").Effect, "10% more creatures an hour");
            Assert.IsTrue(Boon("packmaster").Effect.All(c => c < 128));
        }

        [TestMethod]
        public void BiggerAffixPacks_RestLonger_SoAWeekNeverPaysMore()
        {
            // Teeming (+2) and Unspooled Swarm (+1) make every typed pack bigger, and the rests stretch to match (weekly
            // affixes only make a week harder), so the worth fielded an hour is unchanged.
            foreach (var heat in new[] { 0, 10, 20 })
            {
                var plain = FraysRules.TypedWorth(heat, 0);
                Assert.AreEqual(plain + 2 * 0.4, FraysRules.TypedWorth(heat, 2), 1e-9, "Teeming: two more swarm creatures' worth");
                FraysRules.TypedPack(FraysPackType.Brute, heat, 2, out var each);
                Assert.AreEqual(FraysRules.TypedWorth(heat, 2), FraysRules.BruteCount(heat) * each, 1e-9, "a brute pack takes it as health");

                var target = FraysSettings.Number(FraysSettings.SpawnTargetPerHour);
                var restRatio = FraysRules.TypedRestScale(12, heat, 2, false, target) / FraysRules.TypedRestScale(12, heat, 0, false, target);
                Assert.AreEqual(FraysRules.TypedWorth(heat, 2) / plain, restRatio, 1e-6, "rests stretch with the pack");
            }

            // An affix that lowers health lowers the work (and so the pay) with it; one that raises health pays as before.
            Assert.AreEqual(0.7, FraysRules.AffixWorkMult(0.7), 1e-9, "Brittle Dream");
            Assert.AreEqual(0.8, FraysRules.AffixWorkMult(0.8), 1e-9, "Frayed Masses packs");
            Assert.AreEqual(1.0, FraysRules.AffixWorkMult(1.4), 1e-9, "Heavy Thread");
            Assert.AreEqual(1.0, FraysRules.AffixWorkMult(0), 1e-9);
        }
    }
}
