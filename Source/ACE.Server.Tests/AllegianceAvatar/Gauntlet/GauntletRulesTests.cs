using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Avatars.Gauntlet;
using ACE.Server.ThreadDungeons;

namespace ACE.Server.Tests.Avatars.Gauntlet
{
    /// <summary>The pure Gauntlet wave and eligibility rules, driven only through <see cref="GauntletTuning"/>.</summary>
    [TestClass]
    public class GauntletRulesTests
    {
        private static GauntletRules Defaults() => new(GauntletTuning.Defaults);

        private static GauntletRules With(GauntletTuning t) => new(GauntletTuning.Sanitized(t));

        [TestMethod]
        public void Level_Wave1Is200_Wave13Is500_Wave33Is1000()
        {
            var r = Defaults();
            Assert.AreEqual(200, r.LevelOf(1));
            Assert.AreEqual(225, r.LevelOf(2));
            Assert.AreEqual(500, r.LevelOf(13));
            Assert.AreEqual(1000, r.LevelOf(33));
            Assert.AreEqual(1025, r.LevelOf(34), "there is no maximum level");
        }

        [TestMethod]
        public void Level_FollowsTheTunables()
        {
            var r = With(GauntletTuning.Defaults with { StartLevel = 100, LevelStep = 10 });
            Assert.AreEqual(100, r.LevelOf(1));
            Assert.AreEqual(150, r.LevelOf(6));
        }

        [TestMethod]
        public void Level_SaturatesInsteadOfWrapping()
        {
            var r = Defaults();
            Assert.AreEqual(int.MaxValue, r.LevelOf(int.MaxValue));
            Assert.AreEqual(200, r.LevelOf(0), "a wave below 1 is read as wave 1");
        }

        [TestMethod]
        public void Level_HugeStep_IsNonDecreasingAndEndsAtIntMax()
        {
            // Sanitized path, and the raw path too: LevelOf must not depend on the sanitizer to avoid a wrap.
            foreach (var r in new[]
            {
                With(GauntletTuning.Defaults with { LevelStep = long.MaxValue / 2 }),
                new GauntletRules(GauntletTuning.Defaults with { LevelStep = long.MaxValue / 2 }),
                new GauntletRules(GauntletTuning.Defaults with { StartLevel = long.MaxValue / 2, LevelStep = long.MaxValue / 2 }),
            })
            {
                var previous = r.LevelOf(1);
                for (var wave = 2; wave <= 40; wave++)
                {
                    var level = r.LevelOf(wave);
                    Assert.IsTrue(level >= previous, $"wave {wave}: {level} fell below {previous}");
                    previous = level;
                }
                Assert.AreEqual(int.MaxValue, previous);
            }
        }

        [TestMethod]
        public void BossWave_EveryFifth()
        {
            var r = Defaults();
            for (var wave = 1; wave <= 60; wave++)
                Assert.AreEqual(wave % 5 == 0, r.IsBossWave(wave), $"wave {wave}");
            Assert.IsFalse(r.IsBossWave(0));
        }

        [TestMethod]
        public void BossWave_FollowsTheInterval()
        {
            var r = With(GauntletTuning.Defaults with { BossEvery = 3 });
            Assert.IsTrue(r.IsBossWave(3));
            Assert.IsTrue(r.IsBossWave(6));
            Assert.IsFalse(r.IsBossWave(5));
        }

        [TestMethod]
        public void TimeLimit_BossWaveAddsBonus()
        {
            var r = Defaults();
            Assert.AreEqual(240, r.TimeLimitSeconds(1));
            Assert.AreEqual(240, r.TimeLimitSeconds(4));
            Assert.AreEqual(360, r.TimeLimitSeconds(5));
            Assert.AreEqual(360, r.TimeLimitSeconds(10));
            Assert.AreEqual(240, r.TimeLimitSeconds(11));
        }

        [TestMethod]
        public void TrashCount_ScalesWithHeadcount_CappedAtMaxAlive()
        {
            var r = Defaults();
            Assert.AreEqual(10, r.TrashCount(1, boss: false), "8 + 2.0 x 1");
            Assert.AreEqual(48, r.TrashCount(20, boss: false), "8 + 2.0 x 20");
            Assert.AreEqual(120, r.TrashCount(56, boss: false), "8 + 2.0 x 56 = 120, exactly the cap");
            Assert.AreEqual(120, r.TrashCount(100, boss: false), "208 capped at max_alive 120");
            Assert.AreEqual(120, r.TrashCount(5000, boss: false), "the headcount is clamped first, then the count is capped");
        }

        [TestMethod]
        public void TrashCount_RoundsHalfAwayFromZero()
        {
            var r = With(GauntletTuning.Defaults with { WaveBaseCount = 0, WaveCountPerPlayer = 0.5 });
            Assert.AreEqual(1, r.TrashCount(1, boss: false), "0.5 rounds up");
            Assert.AreEqual(2, r.TrashCount(3, boss: false), "1.5 rounds up");
            Assert.AreEqual(3, r.TrashCount(5, boss: false), "2.5 rounds up, not to the even 2");
        }

        [TestMethod]
        public void TrashCount_BossWaveHalved()
        {
            var r = Defaults();
            Assert.AreEqual(5, r.TrashCount(1, boss: true), "10 x 0.5");
            Assert.AreEqual(24, r.TrashCount(20, boss: true), "48 x 0.5");
            Assert.AreEqual(60, r.TrashCount(100, boss: true), "the cap applies before the boss fraction: 120 x 0.5");
        }

        [TestMethod]
        public void TrashCount_NeverBelowOneOnAPlainWave()
        {
            var r = With(GauntletTuning.Defaults with { WaveBaseCount = 0, WaveCountPerPlayer = 0.0 });
            Assert.AreEqual(1, r.TrashCount(1, boss: false), "a plain wave always has an enemy, or it would clear at once");
            Assert.AreEqual(0, r.TrashCount(1, boss: true), "a boss wave may have none; the boss entry is added by the planner");
        }

        [TestMethod]
        public void Headcount_ClampedTo100()
        {
            var r = Defaults();
            Assert.AreEqual(1, r.ClampHeadcount(0));
            Assert.AreEqual(1, r.ClampHeadcount(-7));
            Assert.AreEqual(1, r.ClampHeadcount(1));
            Assert.AreEqual(57, r.ClampHeadcount(57));
            Assert.AreEqual(100, r.ClampHeadcount(100));
            Assert.AreEqual(100, r.ClampHeadcount(101));
            Assert.AreEqual(100, r.ClampHeadcount(int.MaxValue));
            Assert.AreEqual(GroupScaling.MaxRosterSize, r.ClampHeadcount(int.MaxValue));
        }

        [TestMethod]
        public void Headcount_FollowsTheCapTunable()
        {
            var r = With(GauntletTuning.Defaults with { HeadcountCap = 12 });
            Assert.AreEqual(12, r.ClampHeadcount(50));
        }

        [TestMethod]
        public void Eligible_ThreeContactWaves_True_TwoFalse()
        {
            var r = Defaults();
            Assert.IsTrue(r.IsEligible(new HashSet<int> { 2, 5, 9 }, wavesReached: 9));
            Assert.IsTrue(r.IsEligible(new HashSet<int> { 1, 2, 3, 4 }, wavesReached: 9));
            Assert.IsFalse(r.IsEligible(new HashSet<int> { 2, 5 }, wavesReached: 9));
            Assert.IsFalse(r.IsEligible(new HashSet<int>(), wavesReached: 9));
        }

        [TestMethod]
        public void Eligible_RunShorterThanThreshold_AllWavesSuffice()
        {
            var r = Defaults();
            Assert.IsTrue(r.IsEligible(new HashSet<int> { 1, 2 }, wavesReached: 2), "a 2-wave run needs contact in both waves");
            Assert.IsFalse(r.IsEligible(new HashSet<int> { 2 }, wavesReached: 2), "contact in only one of two waves is not every wave");
            Assert.IsTrue(r.IsEligible(new HashSet<int> { 1 }, wavesReached: 1));
            Assert.IsFalse(r.IsEligible(new HashSet<int>(), wavesReached: 1));
            Assert.IsFalse(r.IsEligible(new HashSet<int> { 1 }, wavesReached: 0), "a run that reached no wave pays nobody");
        }

        [TestMethod]
        public void Eligible_IgnoresWavesOutsideTheRun_AndNull()
        {
            var r = Defaults();
            Assert.IsFalse(r.IsEligible(new HashSet<int> { 0, -1, 10, 11 }, wavesReached: 9), "waves the run never reached do not count");
            Assert.IsFalse(r.IsEligible(null, wavesReached: 9));
        }

        [TestMethod]
        public void Eligible_FollowsTheThresholdTunable()
        {
            var r = With(GauntletTuning.Defaults with { MinContactWaves = 5 });
            Assert.IsFalse(r.IsEligible(new HashSet<int> { 1, 2, 3, 4 }, wavesReached: 9));
            Assert.IsTrue(r.IsEligible(new HashSet<int> { 1, 2, 3, 4, 5 }, wavesReached: 9));
        }

        [TestMethod]
        public void PortalConfig_SplitsTheTargetAcrossThePortals_AndCarriesTheTunables()
        {
            var r = Defaults();
            var c = r.PortalConfig(target: 24, portals: 4);

            Assert.AreEqual(4, c.PortalsMin);
            Assert.AreEqual(4, c.PortalsMax);
            Assert.AreEqual(6, c.PerPortalMin);
            Assert.AreEqual(6, c.PerPortalMax);
            Assert.AreEqual(0.5, c.StaggerMin);
            Assert.AreEqual(2.0, c.StaggerMax);
            Assert.AreEqual(20f, c.PortalMinDistance);
            Assert.AreEqual(55f, c.PortalMaxDistance);
        }

        [TestMethod]
        public void PortalConfig_UnevenSplit_BracketsTheMean()
        {
            var c = Defaults().PortalConfig(target: 8, portals: 3);
            Assert.AreEqual(2, c.PerPortalMin, "floor(8 / 3)");
            Assert.AreEqual(3, c.PerPortalMax, "ceil(8 / 3)");
        }

        [TestMethod]
        public void PortalConfig_DegenerateInputs_NeverNegative()
        {
            var zero = Defaults().PortalConfig(target: 0, portals: 3);
            Assert.AreEqual(0, zero.PerPortalMin);
            Assert.AreEqual(0, zero.PerPortalMax);

            var noPortals = Defaults().PortalConfig(target: 10, portals: 0);
            Assert.AreEqual(1, noPortals.PortalsMin, "at least one portal");
            Assert.AreEqual(10, noPortals.PerPortalMax);

            var tiny = Defaults().PortalConfig(target: 2, portals: 5);
            Assert.AreEqual(2, tiny.PortalsMin, "never more portals than enemies: a portal would stream nothing");
            Assert.AreEqual(2, tiny.PortalsMax);
            Assert.AreEqual(1, tiny.PerPortalMin, "a positive target never rounds a portal to zero");
            Assert.AreEqual(1, tiny.PerPortalMax);
        }

        [TestMethod]
        public void PortalConfig_PortalScale_OutdoorLargerThanDungeon()
        {
            var c = Defaults().PortalConfig(8, 2);
            Assert.AreEqual(0.6, c.PortalScale(isOutdoor: true));
            Assert.AreEqual(0.3, c.PortalScale(isOutdoor: false));
        }
    }
}
