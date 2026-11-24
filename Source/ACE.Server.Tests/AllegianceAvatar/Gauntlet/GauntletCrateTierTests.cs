using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Avatars.Gauntlet;

namespace ACE.Server.Tests.Avatars.Gauntlet
{
    [TestClass]
    public class GauntletCrateTierTests
    {
        [TestMethod]
        public void Tier_FloorOfClearedOver5_CappedAtMax()
        {
            Assert.AreEqual("gauntlet_t0", GauntletCrateTier.For(1, 0, 6).Profile);
            Assert.AreEqual("gauntlet_t0", GauntletCrateTier.For(4, 0, 6).Profile);
            Assert.AreEqual("gauntlet_t1", GauntletCrateTier.For(5, 0, 6).Profile);
            Assert.AreEqual("gauntlet_t1", GauntletCrateTier.For(9, 0, 6).Profile);
            Assert.AreEqual("gauntlet_t2", GauntletCrateTier.For(10, 0, 6).Profile);
            Assert.AreEqual("gauntlet_t5", GauntletCrateTier.For(29, 0, 6).Profile);
            Assert.AreEqual("gauntlet_t6", GauntletCrateTier.For(30, 0, 6).Profile);
            Assert.AreEqual("gauntlet_t6", GauntletCrateTier.For(500, 0, 6).Profile, "capped at the max tier");
            Assert.AreEqual("gauntlet_t3", GauntletCrateTier.For(500, 0, 3).Profile, "the cap is the tunable, not a constant");
            Assert.AreEqual("gauntlet_t0", GauntletCrateTier.For(500, 0, 0).Profile);
            Assert.AreEqual("gauntlet_t0", GauntletCrateTier.For(500, 0, -4).Profile, "a negative max tier is read as 0");
        }

        [TestMethod]
        public void Multiplier_BelowTheTopTier_IsOnePlusTheProgressThroughTheTier()
        {
            // cleared 7, partial 50: tier 1, 2 waves in plus half of the third, so m = 1 + (2 + 0.5) / 5.
            Assert.AreEqual(1.5, GauntletCrateTier.For(7, 50, 6).Multiplier, 1e-12);
            Assert.AreEqual(1.0, GauntletCrateTier.For(5, 0, 6).Multiplier, 1e-12);
            Assert.AreEqual(1.0 + 4.99 / 5, GauntletCrateTier.For(9, 99, 6).Multiplier, 1e-12);
            Assert.AreEqual(1.2, GauntletCrateTier.For(1, 0, 6).Multiplier, 1e-12);
        }

        [TestMethod]
        public void Multiplier_AtTheTopTier_GrowsTenthPerWave_UpToTwo()
        {
            Assert.AreEqual(1.0, GauntletCrateTier.For(30, 0, 6).Multiplier, 1e-12);
            Assert.AreEqual(1.5, GauntletCrateTier.For(35, 0, 6).Multiplier, 1e-12);
            Assert.AreEqual(2.0, GauntletCrateTier.For(40, 0, 6).Multiplier, 1e-12);
            Assert.AreEqual(2.0, GauntletCrateTier.For(400, 0, 6).Multiplier, "capped at 2");
            Assert.AreEqual(1.3, GauntletCrateTier.For(33, 99, 6).Multiplier, 1e-12, "the partial does not count at the top tier");
        }

        [TestMethod]
        public void Multiplier_InOneToTwo_NeverAbove2()
        {
            for (var max = 0; max <= 6; max++)
                for (var cleared = 0; cleared <= 600; cleared++)
                    foreach (var partial in new[] { -5, 0, 1, 49, 99, 100, 5000 })
                    {
                        var (profile, m) = GauntletCrateTier.For(cleared, partial, max);
                        Assert.IsTrue(m >= 1.0 && m <= 2.0, $"cleared {cleared} partial {partial} max {max}: {profile} m={m}");
                    }
        }

        [TestMethod]
        public void ZeroCleared_IsT0AtOne()
        {
            foreach (var partial in new[] { 0, 50, 99 })
            {
                var (profile, m) = GauntletCrateTier.For(0, partial, 6);
                Assert.AreEqual("gauntlet_t0", profile);
                Assert.AreEqual(1.0, m, "a run that cleared nothing pays t0 at 1, whatever its partial");
            }

            Assert.AreEqual(1.0, GauntletCrateTier.For(-3, 50, 6).Multiplier);
            Assert.AreEqual("gauntlet_t0", GauntletCrateTier.For(-3, 50, 6).Profile);
        }

        private static int TierOf(string profile) => int.Parse(profile.Substring(GauntletCrateTier.ProfilePrefix.Length), System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>
        /// Playability review P6 guard, the part that can be defined today: across scores 0..3500 (centi-waves) the tier never
        /// falls, and within one tier the multiplier never falls. P6 itself (the value across a tier boundary) is an open call.
        /// </summary>
        [TestMethod]
        public void TierAndMultiplier_NeverFallAsTheScoreRises()
        {
            var lastTier = -1;
            var lastM = 0.0;

            for (long score = 0; score <= 3500; score++)
            {
                var (profile, m) = GauntletRewards.TierFor(score, 6);
                var tier = TierOf(profile);

                Assert.IsTrue(tier >= lastTier, $"score {score}: tier {tier} below {lastTier}");

                if (tier == lastTier)
                    Assert.IsTrue(m >= lastM, $"score {score}: multiplier {m} below {lastM} within tier {tier}");

                lastTier = tier;
                lastM = m;
            }
        }

        /// <summary>
        /// Playability review P6: a crate's value should never fall as the score rises. Value here is a stand-in, (tier + 1) x
        /// multiplier; Task 16 replaces it with each profile's real pool value and decides the tier-boundary rule, then enables
        /// this test. Today the boundary fails it (t0 at 1.998 is worth more than t1 at 1.0 under the stand-in).
        /// </summary>
        [TestMethod]
        [Ignore("enable at Task 16 tuning")]
        public void CrateValue_NeverFallsAsTheScoreRises()
        {
            var last = 0.0;

            for (long score = 0; score <= 3500; score++)
            {
                var (profile, m) = GauntletRewards.TierFor(score, 6);
                var value = (TierOf(profile) + 1) * m;

                Assert.IsTrue(value >= last, $"score {score}: value {value} below {last}");
                last = value;
            }
        }

        [TestMethod]
        public void ProfileNames_MatchTheShippedRowIds()
        {
            // Task 12 ships gauntlet_t0 .. gauntlet_t6 in rewards.json; the ceiling must not name a row that does not exist.
            Assert.AreEqual(6, GauntletTuning.CrateMaxTierCeiling);
            Assert.AreEqual("gauntlet_t6", GauntletCrateTier.For(int.MaxValue, 0, int.MaxValue).Profile, "an unclamped max is held to the ceiling");
        }
    }
}
