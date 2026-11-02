using System;
using System.Linq;
using System.Reflection;

using ACE.Server.HazardMechanics;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.HazardMechanics
{
    /// <summary>
    /// The balls' per-wave ramp (BallRules.ForWave over HazardRamp.At): "speed", "lead" and "wave_gap" run towards their
    /// "_end" keys across the waves, a run with no end key is exactly today's, and Validate checks the hardest end.
    /// Pure: no PropertyManager is touched.
    /// </summary>
    [TestClass]
    public class BallRampTests
    {
        private static HazardRegion Pinnacle() => HazardRegionPresets.Pinnacle(0);

        private static HazardArgs Merged(string args) => HazardArgs.Merge(HazardArgs.Parse(args), HazardArgs.Parse(HazardDefaults.Balls));

        private static BallParams P(string args, string pattern) => BallRules.Read(Merged(args), pattern);

        private static string Validate(string args, string pattern) => BallRules.Validate(Pinnacle(), Merged(args), pattern);

        [TestMethod]
        public void ForWave_SpeedRamps3To9Over6Waves()
        {
            var p = P("speed=3 speed_end=9 waves=6", BallPatterns.Row);
            var expected = new[] { 3.0, 4.2, 5.4, 6.6, 7.8, 9.0 };

            for (var w = 1; w <= 6; w++)
                Assert.AreEqual(expected[w - 1], BallRules.ForWave(p, w).Speed, 1e-4, "wave " + w);
        }

        [TestMethod]
        public void ForWave_LeadAndWaveGapRamp_LikeSpeed()
        {
            var p = P("lead=2 lead_end=6 wave_gap=3 wave_gap_end=1 waves=5", BallPatterns.Row);

            Assert.AreEqual(2.0, BallRules.ForWave(p, 1).Lead, 1e-9);
            Assert.AreEqual(4.0, BallRules.ForWave(p, 3).Lead, 1e-9);
            Assert.AreEqual(6.0, BallRules.ForWave(p, 5).Lead, 1e-9);

            // 5 waves have 4 gaps (after waves 1-4); the ramp spans those, so the last USED gap is wave_gap_end
            Assert.AreEqual(3.0, BallRules.ForWave(p, 1).WaveGap, 1e-9);
            Assert.AreEqual(1.0, BallRules.ForWave(p, 4).WaveGap, 1e-9, "the gap after the second-to-last wave is the last one used");
            Assert.AreEqual(3.0 + (1.0 - 3.0) * (2.0 / 3.0), BallRules.ForWave(p, 3).WaveGap, 1e-9);
        }

        [TestMethod]
        public void ForWave_WaveGap_LastUsedGapIsTheEndValue()
        {
            var p = P("wave_gap=3 wave_gap_end=1 waves=6", BallPatterns.Row);
            var used = Enumerable.Range(1, 5).Select(w => BallRules.ForWave(p, w).WaveGap).ToArray();

            Assert.AreEqual(3.0, used[0], 1e-9);
            Assert.AreEqual(1.0, used[4], 1e-9);

            // a single wave has no gap at all; it must not divide by zero
            Assert.AreEqual(3.0, BallRules.ForWave(P("wave_gap=3 wave_gap_end=1 waves=1", BallPatterns.Row), 1).WaveGap, 1e-9);
        }

        [TestMethod]
        public void EndKeys_TakeTheirStartKeysClamp()
        {
            var p = P("speed=3 speed_end=99 lead=2 lead_end=0.01 wave_gap=3 wave_gap_end=9999", BallPatterns.Row);

            Assert.AreEqual((float)HazardDefaults.BallsMaxSpeed, p.SpeedEnd.Value);
            Assert.AreEqual(HazardDefaults.BallsMinLead, p.LeadEnd.Value);
            Assert.AreEqual(HazardDefaults.BallsMaxWaveGap, p.WaveGapEnd.Value);
        }

        [TestMethod]
        public void ForWave_LeadNeverBelowFairLead()
        {
            var region = Pinnacle();

            foreach (var pattern in new[] { BallPatterns.Row, BallPatterns.Cross, BallPatterns.RingOut, BallPatterns.RingIn, BallPatterns.Random })
            {
                var p = P("lead=0.5 lead_end=0.5 waves=4", pattern);
                var fair = BallRules.FairLead(region, pattern, p);

                Assert.IsTrue(fair > 0.5, pattern + ": the test needs a fair-lead floor above the asked lead");

                for (var w = 1; w <= 4; w++)
                {
                    var wp = BallRules.ForWave(p, w);
                    var lead = BallRules.LeadFor(region, pattern, wp, null);

                    Assert.IsTrue(lead >= fair - 1e-9, $"{pattern} wave {w}: lead {lead} is under the fair lead {fair}");
                    Assert.AreEqual(fair, lead, 1e-9, $"{pattern} wave {w}: a raised lead is exactly the floor");
                }
            }
        }

        [TestMethod]
        public void ForWave_CopiesEveryOtherProperty()
        {
            // every property gets a non-default value, so a property ForWave forgets to copy shows up as a difference
            var rich = P("ball_radius=1.2 escape_speed=4 player_radius=0.7 edge_inset=3 gap=7 count=5 window=10 marker=false dir=n dirs=ne cx=5 cy=-12 spread=45 target=nearest speed=4 speed_end=8 lead=3 lead_end=5 wave_gap=2 wave_gap_end=1 stream=30 waves=6 ray_markers=1 wave_every=4 wave_every_end=2 gaps=3 max_in_flight=2 max_in_flight_end=4 max_balls=24 marker_script=0x33001030 fair_lead=0 no_overlap=1 origin_clear=2 distinct_dirs=1 hit_lag=0.3 occupancy=1.5", BallPatterns.Random);
            var fresh = new BallParams();
            var ramped = new[] { nameof(BallParams.Speed), nameof(BallParams.Lead), nameof(BallParams.WaveGap), nameof(BallParams.WaveEvery), nameof(BallParams.MaxInFlight) };
            var copy = BallRules.ForWave(rich, 1);

            foreach (var prop in typeof(BallParams).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (ramped.Contains(prop.Name) || prop.Name == nameof(BallParams.Reach) || prop.Name == nameof(BallParams.Problem))
                    continue;

                Assert.AreNotEqual(prop.GetValue(fresh), prop.GetValue(rich), prop.Name + ": the test args must set it to a non-default value");
                Assert.AreEqual(prop.GetValue(rich), prop.GetValue(copy), prop.Name + " was not copied by ForWave");
            }

            // Problem rides along too (a refused run never reaches ForWave, but a copy must not hide why)
            var bad = P("dir=zz", BallPatterns.Row);
            Assert.IsNotNull(bad.Problem);
            Assert.AreEqual(bad.Problem, BallRules.ForWave(bad, 1).Problem);
        }

        [TestMethod]
        public void Validate_RandomOccupancy_UsesSlowestSpeed()
        {
            // 8 launches over 10 s: fine at 9 m/s, too many balls in flight at 3 m/s
            Assert.IsNull(Validate("count=8 window=10 speed=9", BallPatterns.Random));
            StringAssert.Contains(Validate("count=8 window=10 speed=3", BallPatterns.Random), "random launch rate");

            // the ramp's slow end is the one that counts, whichever key holds it
            StringAssert.Contains(Validate("count=8 window=10 speed=9 speed_end=3", BallPatterns.Random), "random launch rate");
            StringAssert.Contains(Validate("count=8 window=10 speed=3 speed_end=9", BallPatterns.Random), "random launch rate");
            Assert.IsNull(Validate("count=8 window=10 speed=9 speed_end=8", BallPatterns.Random));
        }

        [TestMethod]
        public void Occupancy_DefaultsTo075InCode_IsClamped_AndANonNumericValueIsRefused()
        {
            Assert.AreEqual(0.75, P("", BallPatterns.Random).Occupancy, 1e-12);
            Assert.AreEqual(0.75, new BallParams().Occupancy, 1e-12);
            Assert.AreEqual(0.75, BallRules.Read(HazardArgs.Parse(HazardDefaults.Balls), BallPatterns.Random).Occupancy, 1e-12, "the default row does not set it");
            Assert.AreEqual(1.25, P("occupancy=1.25", BallPatterns.Random).Occupancy, 1e-12);
            Assert.AreEqual(0.25, P("occupancy=0", BallPatterns.Random).Occupancy, 1e-12, "clamped up to 0.25");
            Assert.AreEqual(2.0, P("occupancy=9", BallPatterns.Random).Occupancy, 1e-12, "clamped down to 2");
            StringAssert.Contains(Validate("occupancy=x", BallPatterns.Random), "occupancy");
        }

        [TestMethod]
        public void Occupancy_RelaxesTheRandomBoundOnly()
        {
            const string dense = "count=8 window=10 speed=3";

            StringAssert.Contains(Validate(dense, BallPatterns.Random), "random launch rate");
            StringAssert.Contains(Validate(dense, BallPatterns.Random), "occupancy", "the refusal names the lever");
            Assert.IsNull(Validate(dense + " occupancy=2", BallPatterns.Random), "a higher occupancy admits the denser wave");
            StringAssert.Contains(Validate(dense + " occupancy=0.25", BallPatterns.Random), "random launch rate", "a lower one is stricter");

            // p.Occupancy is read only in ValidateRandom: a wave the other patterns refuse stays refused at occupancy=2
            foreach (var (pattern, args, why) in new[] { (BallPatterns.Row, "gap=30 dir=e", "does not fit the east side"), (BallPatterns.Cross, "gap=25 dirs=ne", "does not fit the east side"), (BallPatterns.Fan, "fair_lead=0 lead=0 cx=5000 cy=5000", "fan origin") })
            {
                var refused = Validate(args, pattern);

                Assert.IsNotNull(refused, pattern + ": the control must be refused at the default");
                StringAssert.Contains(refused, why, pattern);
                Assert.AreEqual(refused, Validate(args + " occupancy=2", pattern), pattern + ": occupancy changes nothing outside the random pattern");
            }
        }

        [TestMethod]
        public void Validate_RefusesANonNumericEndKey()
        {
            foreach (var key in new[] { "speed_end", "lead_end", "wave_gap_end", "stream" })
                StringAssert.Contains(Validate(key + "=fast", BallPatterns.Random), key);
        }

        [TestMethod]
        public void NoEndKeys_IsTodaysBehaviour()
        {
            var region = Pinnacle();

            foreach (var pattern in BallPatterns.All)
            {
                var p = P("", pattern);

                Assert.IsNull(p.SpeedEnd, pattern);
                Assert.IsNull(p.LeadEnd, pattern);
                Assert.IsNull(p.WaveGapEnd, pattern);
                Assert.AreEqual(0.0, p.Stream, pattern);

                for (var w = 1; w <= p.Waves; w++)
                {
                    var wp = BallRules.ForWave(p, w);

                    Assert.AreEqual(p.Speed, wp.Speed, pattern);
                    Assert.AreEqual(p.Lead, wp.Lead, pattern);
                    Assert.AreEqual(p.WaveGap, wp.WaveGap, pattern);
                    Assert.AreEqual(BallRules.LeadFor(region, pattern, p, null), BallRules.LeadFor(region, pattern, wp, null), pattern);

                    // the same seed plans the same routes, and no route carries a speed of its own
                    var now = BallPatterns.Plan(region, pattern, p, new Random(7));
                    var then = BallPatterns.Plan(region, pattern, wp, new Random(7));

                    Assert.AreEqual(now.Routes.Count, then.Routes.Count, pattern);

                    for (var i = 0; i < now.Routes.Count; i++)
                    {
                        Assert.AreEqual(now.Routes[i].From, then.Routes[i].From, pattern);
                        Assert.AreEqual(now.Routes[i].To, then.Routes[i].To, pattern);
                        Assert.AreEqual(now.Routes[i].Delay, then.Routes[i].Delay, pattern);
                        Assert.AreEqual(0f, then.Routes[i].Speed, pattern);
                    }
                }
            }
        }
    }
}
