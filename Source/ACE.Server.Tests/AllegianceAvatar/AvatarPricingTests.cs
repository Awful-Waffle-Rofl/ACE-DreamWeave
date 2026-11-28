using System;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Avatars;
using ACE.Server.Avatars.Gauntlet;

namespace ACE.Server.Tests.Avatars
{
    /// <summary>
    /// The roster-scaled Favor prices and the per-Avatar Favor cap. Expected values are worked by hand from the formula:
    /// E = R up to T = max(2, 0.2 x S), else T + sqrt(R - T); price = max(floor, ceil(perAccount x E)); cap = max(20000, ceil(2 x the largest price)).
    /// With S = 230, T = 46 and sqrt(27) = 5.196152..., so R = 73 gives E = 51.196152...
    /// </summary>
    [TestClass]
    public class AvatarPricingTests
    {
        private static readonly AvatarTuning T = AvatarTuning.Defaults;
        private static readonly GauntletTuning G = GauntletTuning.Defaults;

        [TestMethod]
        public void Defaults_AreTheOwnerValues()
        {
            Assert.AreEqual(700L, T.EventPriceQuiet);
            Assert.AreEqual(200L, T.EventPriceQuietPerAccount);
            Assert.AreEqual(1000L, T.EventPriceAnnounced);
            Assert.AreEqual(300L, T.EventPriceAnnouncedPerAccount);
            Assert.AreEqual(1000L, G.Price);
            Assert.AreEqual(300L, G.PricePerAccount);
            Assert.AreEqual(20000L, T.FavorCap);
            Assert.AreEqual(2.0, T.FavorCapPriceMultiple);
            Assert.AreEqual(0.00000001, T.BaseRate);
        }

        // R, S, quiet, announced, gauntlet, cap
        [DataTestMethod]
        [DataRow(2, 230, 700UL, 1000UL, 1000UL, 20000UL)]      // E = 2: 400 / 600 / 600 are under their floors
        [DataRow(20, 230, 4000UL, 6000UL, 6000UL, 20000UL)]    // E = 20 (under T = 46): 200 x 20, 300 x 20; cap 2 x 6000 = 12000 < 20000
        [DataRow(46, 230, 9200UL, 13800UL, 13800UL, 27600UL)]  // E = 46: exactly at the threshold
        [DataRow(73, 230, 10240UL, 15359UL, 15359UL, 30718UL)] // E = 51.196152: 10239.23 -> 10240, 15358.85 -> 15359, cap 2 x 15359
        public void Prices_AndCap_MatchTheWorkedTable(int r, int s, ulong quiet, ulong announced, ulong gauntlet, ulong cap)
        {
            Assert.AreEqual(quiet, AvatarPricing.Quiet(T, r, s), "quiet");
            Assert.AreEqual(announced, AvatarPricing.Announced(T, r, s), "announced");
            Assert.AreEqual(gauntlet, AvatarPricing.Gauntlet(T, G, r, s), "gauntlet");
            Assert.AreEqual(cap, AvatarPricing.FavorCap(T, G, r, s), "cap");
        }

        [TestMethod]
        public void EffectiveAccounts_IsTheNumeratorOfRosterScale()
        {
            foreach (var (r, s) in new[] { (1, 0), (2, 230), (10, 0), (20, 230), (46, 230), (73, 230), (200, 230) })
            {
                var effective = AvatarCurves.EffectiveAccounts(r, s, T);
                Assert.AreEqual(Math.Min(1.0, effective / r), AvatarCurves.RosterScale(r, s, T), 0.0, $"R={r} S={s}");
            }

            Assert.AreEqual(46.0 + Math.Sqrt(27.0), AvatarCurves.EffectiveAccounts(73, 230, T), 1e-12);
            Assert.AreEqual(0.0, AvatarCurves.EffectiveAccounts(0, 230, T));
            Assert.AreEqual(0.0, AvatarCurves.EffectiveAccounts(-5, 230, T));
        }

        [TestMethod]
        public void ScaledPrice_FloorsAtTheFloor_NeverNegative_AndSaturates()
        {
            Assert.AreEqual(700UL, AvatarCurves.ScaledPrice(700, 200, 0, 230, T), "no accounts: the floor");
            Assert.AreEqual(0UL, AvatarCurves.ScaledPrice(-5, -5, 20, 230, T), "negative tunables are 0");
            Assert.AreEqual(4000UL, AvatarCurves.ScaledPrice(-5, 200, 20, 230, T), "a negative floor does not lower the scaled part");
            Assert.AreEqual(ulong.MaxValue, AvatarCurves.ScaledPrice(0, long.MaxValue, int.MaxValue, 0, T), "saturates, never wraps");
            Assert.AreEqual((ulong)long.MaxValue, AvatarCurves.ScaledPrice(long.MaxValue, 0, 20, 230, T), "a huge floor is the price");
        }

        [TestMethod]
        public void FavorCapFor_FloorsAtTheFlatCap_AndSaturates()
        {
            Assert.AreEqual(20000UL, AvatarCurves.FavorCapFor(0, T));
            Assert.AreEqual(20000UL, AvatarCurves.FavorCapFor(10_000, T), "2 x 10000 = 20000: not above the flat cap");
            Assert.AreEqual(20002UL, AvatarCurves.FavorCapFor(10_001, T));
            Assert.AreEqual(50_000UL, AvatarCurves.FavorCapFor(50_000, T with { FavorCapPriceMultiple = 0.0 }), "multiple below 1 reads as 1.0: never under the largest price");
            Assert.AreEqual(50_000UL, AvatarCurves.FavorCapFor(50_000, T with { FavorCapPriceMultiple = -3.0 }), "negative reads as 1.0");
            Assert.AreEqual(50_000UL, AvatarCurves.FavorCapFor(50_000, T with { FavorCapPriceMultiple = double.NaN }), "NaN reads as 1.0");
            Assert.AreEqual(50_000UL, AvatarCurves.FavorCapFor(50_000, T with { FavorCapPriceMultiple = 0.5 }), "0.5 reads as 1.0");
            Assert.AreEqual(100_000UL, AvatarCurves.FavorCapFor(50_000, T with { FavorCapPriceMultiple = 2.0 }));
            Assert.AreEqual(1_000_000UL, AvatarCurves.FavorCapFor(50_000, T with { FavorCap = 1_000_000 }), "the flat cap still floors a large favor_cap");
            Assert.AreEqual(ulong.MaxValue, AvatarCurves.FavorCapFor(ulong.MaxValue, T));
            Assert.AreEqual(25_000UL, AvatarCurves.FavorCapFor(10_000, T with { FavorCap = 1, FavorCapPriceMultiple = 2.5 }), "the multiple is a tunable");
        }

        [TestMethod]
        public void AddFavor_AppliesThePassedCap_NotTheFlatOne()
        {
            Assert.AreEqual(30_718UL, AvatarCurves.AddFavor(30_000, 5_000, 30_718UL, T), "the per-Avatar cap");
            Assert.AreEqual(20_000UL, AvatarCurves.AddFavor(19_000, 5_000, 20_000UL, T), "the flat cap clamps the same input lower");
            Assert.AreEqual(24_000UL, AvatarCurves.AddFavor(19_000, 5_000, 40_000UL, T), "under the cap: plain addition");
            Assert.AreEqual(30_000UL, AvatarCurves.AddFavor(30_000, 5_000, 20_000UL, T), "above a shrunken cap: kept, not lowered");
        }
    }
}
