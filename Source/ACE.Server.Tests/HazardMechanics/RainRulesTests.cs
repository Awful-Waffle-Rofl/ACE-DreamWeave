using System;
using System.Linq;

using ACE.Server.HazardMechanics;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.HazardMechanics
{
    /// <summary>The rain's pure rules: the numbers, the pre-flight, the fair-warning floor, the cap and the launch gaps. No world.</summary>
    [TestClass]
    public class RainRulesTests
    {
        /// <summary>An open-sky circle on a plane at z 0, 40 m radius.</summary>
        internal static HazardRegion Open(float radius = 40f, float? ceiling = null) => HazardRegion.Circle(
            "rain-test", 0x1234, 0, true, 96f, 96f, radius,
            HazardGroundMode.Plane, 0f, null, 0.5f, ceiling, 2f, 6f);

        private static RainSettings S(string args = "") => RainSettings.From(HazardArgs.Parse(args));

        // ---- defaults and clamps ---------------------------------------------------------------------------------

        [TestMethod]
        public void Defaults_AreTheDocumentedOnes()
        {
            var s = S();

            Assert.AreEqual(10.0, s.Speed);
            Assert.AreEqual(0.2, s.Lead);
            Assert.AreEqual(4.0, s.BurstRadius);
            Assert.AreEqual(60.0, s.Duration);
            Assert.IsNull(s.DropZ);
            Assert.AreEqual(8, s.MaxFalling);
            Assert.AreEqual(2.0f, s.EdgeInset);
            Assert.AreEqual(HazardDefaults.WispDestMarkScript, s.MarkerScript);
            Assert.AreEqual(HazardDefaults.PillarScript, s.BurstScript);
            Assert.AreEqual(HazardDefaults.RollingDeathRedWcid, s.BallWcid);
            Assert.AreEqual(3.0, s.EscapeSpeed);
            Assert.AreEqual(0.5, s.PlayerRadius);
            Assert.AreEqual(0.6, s.TargetShare);
            Assert.AreEqual(5.0, s.TargetSpread);
        }

        [TestMethod]
        public void TargetKeys_AreClamped()
        {
            Assert.AreEqual(1.0, S("target_share=7").TargetShare);
            Assert.AreEqual(0.0, S("target_share=-1").TargetShare);
            Assert.AreEqual(30.0, S("target_spread=99").TargetSpread);
            Assert.AreEqual(0.0, S("target_spread=-4").TargetSpread);
            Assert.AreEqual(0.25, S("target_share=0.25").TargetShare);
            Assert.AreEqual(12.0, S("target_spread=12").TargetSpread);
        }

        [TestMethod]
        public void Validate_RefusesNonNumericTargetKeys()
        {
            Assert.AreEqual("target_share='lots' is not a number", RainRules.Validate(Open(), HazardArgs.Parse("target_share=lots"), null));
            Assert.AreEqual("target_spread='wide' is not a number", RainRules.Validate(Open(), HazardArgs.Parse("target_spread=wide"), null));
            Assert.IsNull(RainRules.Validate(Open(), HazardArgs.Parse("target_share=0.5 target_spread=8"), null));
        }

        [TestMethod]
        public void Values_AreReadAndClamped()
        {
            var s = S("speed=99 lead=0.1 burst_radius=99 duration=0 max_falling=500 drop_z=-12.5 marker_script=0x33000791 burst_script=855 ball_wcid=1006966");

            Assert.AreEqual(RainRules.MaxSpeed, s.Speed);
            Assert.AreEqual(RainRules.MinLead, s.Lead);
            Assert.AreEqual(RainRules.MaxBurstRadius, s.BurstRadius);
            Assert.AreEqual(RainRules.MinDuration, s.Duration);
            Assert.AreEqual(64, s.MaxFalling);
            Assert.AreEqual(-12.5, s.DropZ);
            Assert.AreEqual(0x33000791u, s.MarkerScript);
            Assert.AreEqual(855u, s.BurstScript);
            Assert.AreEqual(1006966u, s.BallWcid);
        }

        // ---- Validate --------------------------------------------------------------------------------------------

        [TestMethod]
        public void Validate_AcceptsTheDefaults_AndTheKeysTheWorldEventEmits()
        {
            Assert.IsNull(RainRules.Validate(Open(), HazardArgs.Empty, null));

            var args = HazardArgs.Parse("speed=10 lead=0.6 burst_radius=4 duration=60 drop_z=30 max_falling=8 edge_inset=2 marker_script=0x33000791 burst_script=0x33001028 ball_wcid=1006966 pct=0.6 damage_type=fire hit_verb=sears escape_speed=3");

            Assert.IsNull(RainRules.Validate(Open(), args, null));
        }

        [TestMethod]
        public void Validate_RefusesANonNumber_NamingTheKey()
        {
            Assert.AreEqual("speed='fast' is not a number", RainRules.Validate(Open(), HazardArgs.Parse("speed=fast"), null));
            Assert.AreEqual("max_falling='many' is not a whole number", RainRules.Validate(Open(), HazardArgs.Parse("max_falling=many"), null));
            Assert.AreEqual("drop_z='sky' is not a number", RainRules.Validate(Open(), HazardArgs.Parse("drop_z=sky"), null));
        }

        [TestMethod]
        public void Validate_RefusesAnUnknownDamageType_WithTheExactLine()
        {
            Assert.AreEqual("damage_type='x' is not one of fire|acid|cold|electric|bludgeon",
                RainRules.Validate(Open(), HazardArgs.Parse("damage_type=x"), null));

            foreach (var ok in new[] { "fire", "acid", "cold", "electric", "bludgeon", "FIRE" })
                Assert.IsNull(RainRules.Validate(Open(), HazardArgs.Parse("damage_type=" + ok), null), ok);
        }

        [TestMethod]
        public void Validate_RefusesABadScriptId()
        {
            Assert.AreEqual("marker_script='nope' is not a script id (decimal or 0x hex)", RainRules.Validate(Open(), HazardArgs.Parse("marker_script=nope"), null));
            Assert.AreEqual("burst_script='0xZZ' is not a script id (decimal or 0x hex)", RainRules.Validate(Open(), HazardArgs.Parse("burst_script=0xZZ"), null));
        }

        [TestMethod]
        public void Validate_RefusesARegionTooSmallForItsEdgeInset()
        {
            var line = RainRules.Validate(Open(1.5f), HazardArgs.Empty, null);

            Assert.IsNotNull(line);
            StringAssert.Contains(line, "nowhere to fall");
        }

        [TestMethod]
        public void Validate_RefusesADropThatIsNotAboveTheGround()
        {
            var line = RainRules.Validate(Open(), HazardArgs.Parse("drop_z=1"), null);

            Assert.IsNotNull(line);
            StringAssert.Contains(line, "drop_z=1");
            StringAssert.Contains(line, "nothing to fall");
        }

        [TestMethod]
        public void Validate_RefusesARegionWithACeilingBelowTheDrop()
        {
            var line = RainRules.Validate(Open(40f, ceiling: 10f), HazardArgs.Empty, null);

            Assert.IsNotNull(line);
            StringAssert.Contains(line, "needs open sky");
        }

        [TestMethod]
        public void Validate_RefusesAWarningShorterThanTheFloor_AndAcceptsOneAtTheFloor()
        {
            // floor = 0.5 + (4 + 0.5) / 3 = 2.0 s. drop 9 m above the ball centre line... fall = (dropZ - 1.8) / speed
            // speed 10, drop_z 11 -> fall 0.92; lead 0.6 -> 1.52 s: too short
            var line = RainRules.Validate(Open(), HazardArgs.Parse("speed=10 drop_z=11 lead=0.6"), null);

            Assert.IsNotNull(line);
            StringAssert.Contains(line, "too short a warning");
            StringAssert.Contains(line, "2");

            // lead raised to 1.2: 1.2 + 0.92 = 2.12 >= 2.0
            Assert.IsNull(RainRules.Validate(Open(), HazardArgs.Parse("speed=10 drop_z=11 lead=1.2"), null));

            // a bigger burst raises the floor (0.5 + 5.5 / 3 = 2.33)
            Assert.IsNotNull(RainRules.Validate(Open(), HazardArgs.Parse("speed=10 drop_z=11 lead=1.2 burst_radius=5"), null));

            // a faster runner shortens it (0.5 + 4.5 / 6 = 1.25)
            Assert.IsNull(RainRules.Validate(Open(), HazardArgs.Parse("speed=10 drop_z=11 lead=0.6 escape_speed=6"), null));
        }

        // ---- the floor, the lead, the cap ---------------------------------------------------------------------------

        [TestMethod]
        public void WarningFloor_IsHalfASecondPlusTheRunOutTime()
        {
            Assert.AreEqual(0.5 + 4.5 / 3.0, RainRules.WarningFloor(S()), 1e-9);
            Assert.AreEqual(0.5 + (6.0 + 1.0) / 4.0, RainRules.WarningFloor(S("burst_radius=6 player_radius=1 escape_speed=4")), 1e-9);
        }

        [TestMethod]
        public void LeadFor_RaisesTheLeadToTheFloor_AndNeverLowersIt()
        {
            var s = S("lead=0.6");

            Assert.AreEqual(0.6, RainRules.LeadFor(s, 5.0), "a long fall already meets the floor");
            Assert.AreEqual(2.0 - 0.9, RainRules.LeadFor(s, 0.9), 1e-9, "a short fall is topped up");
            Assert.AreEqual(3.0, RainRules.LeadFor(S("lead=3"), 0.9), "an explicit longer lead wins");
        }

        [TestMethod]
        public void FallSeconds_RestsTheBallOnTheGround()
        {
            Assert.AreEqual((31.8 - 1.8) / 10.0, RainRules.FallSeconds(31.8, 0.0, 10.0), 1e-6);
            Assert.AreEqual((50.0 - (20.0 + RainRules.BallRadius)) / 5.0, RainRules.FallSeconds(50.0, 20.0, 5.0), 1e-6);
            Assert.AreEqual(0.0, RainRules.FallSeconds(0.0, 10.0, 5.0), "never negative");
        }

        [TestMethod]
        public void CapFor_PrefersTheLiveCount_AndClampsBothWays()
        {
            var s = S("max_falling=8");

            Assert.AreEqual(8, RainRules.CapFor(null, s));
            Assert.AreEqual(20, RainRules.CapFor(20, s));
            Assert.AreEqual(1, RainRules.CapFor(0, s));
            Assert.AreEqual(1, RainRules.CapFor(-5, s));
            Assert.AreEqual(64, RainRules.CapFor(1000, s));
            Assert.AreEqual(64, RainRules.CapFor(null, S("max_falling=500")));
        }

        // ---- the launch gaps ------------------------------------------------------------------------------------------

        [TestMethod]
        public void NextGap_IsDeterministicUnderASeed_AndStaysInItsBand()
        {
            var a = new Random(42);
            var b = new Random(42);

            for (var i = 0; i < 200; i++)
            {
                var ga = RainRules.NextGap(a, 0.5);
                var gb = RainRules.NextGap(b, 0.5);

                Assert.AreEqual(ga, gb);
                Assert.IsTrue(ga >= 0.5 * RainRules.MinGapFactor - 1e-12 && ga <= 0.5 * RainRules.MaxGapFactor + 1e-12, $"gap {ga}");
            }
        }

        [TestMethod]
        public void NextGap_AveragesAboutTheMean_AndIsNotAMetronome()
        {
            var rng = new Random(7);
            var gaps = Enumerable.Range(0, 4000).Select(_ => RainRules.NextGap(rng, 1.0)).ToList();
            var mean = gaps.Average();

            Assert.IsTrue(mean > 0.85 && mean < 1.1, $"mean {mean:0.000}");
            Assert.IsTrue(gaps.Distinct().Count() > 1000, "random gaps, not one fixed period");
            Assert.IsTrue(gaps.Max() > 2.0 * gaps.Min(), "spread");
        }

        [TestMethod]
        public void MeanGap_ShrinksAsTheCapGrows()
        {
            Assert.AreEqual(0.5, RainRules.MeanGap(4.0, 8), 1e-9);
            Assert.AreEqual(2.0, RainRules.MeanGap(4.0, 2), 1e-9);
            Assert.AreEqual(4.0, RainRules.MeanGap(4.0, 0), 1e-9, "a zero cap reads as one");
        }

        [TestMethod]
        public void NominalLifetime_IsLeadPlusFall_FromTheReferenceGround()
        {
            var life = RainRules.NominalLifetime(Open(), S("speed=10 lead=0.6"));

            // drop = ground + 30, ball rests at ground + 1.8: fall 2.82 s; lead 0.6 (floor 2.0 already met)
            Assert.AreEqual(0.6 + (30.0 - RainRules.BallRadius) / 10.0, life, 1e-6);
        }
    }
}
