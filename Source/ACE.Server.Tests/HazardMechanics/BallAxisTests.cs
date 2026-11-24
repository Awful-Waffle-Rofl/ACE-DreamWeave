using System;
using System.Linq;
using System.Numerics;

using ACE.Server.HazardMechanics;
using ACE.Server.Pinnacles.Harbinger;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.HazardMechanics
{
    /// <summary>
    /// The random pattern's "axis" arg (BallParams.Axis), built for the Harbinger's Ember roll in round 2: an axis run rolls only
    /// along its axis and only on lanes whose whole path stays out of every exclusion. Round 4 dropped the fire alcove's
    /// exclusion and Ember roll rolls axis=any, so the generic axis tests run on their own fixture region (a circle with a
    /// doorway-wide strip excluded), and the Harbinger controls pin the new truth: no exclusion, balls from all four sides.
    /// Pure BallPatterns: no world.
    /// </summary>
    [TestClass]
    public class BallAxisTests
    {
        /// <summary>A fixture circle shaped like the old fire alcove, radius 4 at (20, 0), with a 3.33 m wide strip running through it in y excluded.</summary>
        private static HazardRegion StripRegion()
        {
            var r = HazardRegion.Circle("axis-fixture", 0x00BC, 0, false, 20f, 0f, 4.0f, HazardGroundMode.Plane,
                groundPlaneZ: 0f, hazardPlaneZ: null, hazardOffset: 0f, ceilingZ: 9f, bandBelow: 0.5f, bandAbove: 9f,
                exclusions: new[] { HazardExclusion.Rect(20f - 1.665f, 20f + 1.665f, -4.5f, 4.5f) }, playerMargin: 1.3f);

            Assert.AreEqual(1, r.Exclusions.Count, "the strip");
            return r;
        }

        private static HazardRegion FireAlcove()
        {
            Assert.IsTrue(HazardRegionPresets.TryGet(HazardRegionPresets.HarbingerAlcoveNorthName, 0, out var r));
            return r;
        }

        private static BallParams Ember(bool pressured = false)
            => BallRules.Read(HarbingerSettings.Defaults.EmberArgs(0.2, pressured), BallPatterns.Random);

        private static BallParams Axis(string axis, double radius = 0.5)
            => BallRules.Read(HazardArgs.Parse($"axis={axis} ball_radius={radius.ToString(System.Globalization.CultureInfo.InvariantCulture)} edge_inset=0.5 count=2 waves=1 wave_every=3 window=3 speed=3"), BallPatterns.Random);

        /// <summary>Every sampled point of the route, its ends and every 0.02 m between.</summary>
        private static bool EntersExclusion(HazardRegion r, BallRoute route)
        {
            var steps = (int)Math.Ceiling(route.Length / 0.02f) + 1;

            for (var i = 0; i <= steps; i++)
            {
                var pt = Vector2.Lerp(route.From, route.To, (float)i / steps);

                if (r.InExclusion(pt.X, pt.Y))
                    return true;
            }

            return false;
        }

        [TestMethod]
        public void AxisNs_OnAStripRegion_NoLaneEverEntersTheStrip_AndEveryLaneRunsAlongY()
        {
            var r = StripRegion();
            var strip = r.Exclusions.Single();
            var p = Axis("ns");
            var rng = new Random(20261118);
            int routes = 0, north = 0, south = 0, west = 0, east = 0;

            for (var wave = 1; wave <= 2000; wave++)
            {
                var plan = BallPatterns.Plan(r, BallPatterns.Random, p, rng, wave);

                Assert.AreEqual(p.Count, plan.Routes.Count, $"wave {wave}: every wave rolls all its balls");

                foreach (var route in plan.Routes)
                {
                    routes++;
                    Assert.AreEqual(route.From.X, route.To.X, 1e-5, $"wave {wave}: a lane along y");
                    Assert.IsFalse(EntersExclusion(r, route), $"wave {wave}: lane x {route.From.X:0.000} enters the strip");
                    Assert.IsTrue(route.From.X <= strip.MinX - p.BallRadius + 1e-3f || route.From.X >= strip.MaxX + p.BallRadius - 1e-3f,
                        $"wave {wave}: the ball body at x {route.From.X:0.000} overhangs the strip");

                    if (route.From.Y > route.To.Y) north++; else south++;
                    if (route.From.X < strip.MinX) west++; else east++;
                }
            }

            Assert.AreEqual(2000 * p.Count, routes);
            Assert.IsTrue(north > 400 * p.Count && south > 400 * p.Count, $"both ends of the axis: n {north} s {south}");
            Assert.IsTrue(west > 400 * p.Count && east > 400 * p.Count, $"both sides of the strip: w {west} e {east}");
        }

        [TestMethod]
        public void Control_AxisAny_CrossesAStrip_OrPlansNothing_BecauseExclusionsOnlySlideEnds()
        {
            var r = StripRegion();
            var any = Axis("any");
            var rng = new Random(7);
            int crossing = 0, empty = 0;

            for (var wave = 1; wave <= 500; wave++)
            {
                var plan = BallPatterns.Plan(r, BallPatterns.Random, any, rng, wave);

                if (plan.Routes.Count == 0)
                    empty++;

                crossing += plan.Routes.Count(route => EntersExclusion(r, route));
            }

            Assert.IsTrue(crossing > 50, $"{crossing} east-west lanes run through the strip");

            // an explicit axis whose every lane crosses the strip is refused, naming the axis
            StringAssert.Contains(BallRules.Validate(r, HazardArgs.Parse("axis=ew ball_radius=0.5 edge_inset=0.5 count=2 waves=1 wave_every=3 window=3 speed=3"), BallPatterns.Random), "axis=ew");
        }

        [TestMethod]
        public void Validate_RefusesBadAxisValues_AndAxisOnANonRandomPattern()
        {
            var r = StripRegion();

            StringAssert.Contains(BallRules.Validate(r, HazardArgs.Parse("axis=up"), BallPatterns.Random), "axis");
            StringAssert.Contains(BallRules.Validate(HazardRegionPresets.Pinnacle(0), HazardArgs.Parse("axis=ns"), BallPatterns.Row), "only works with the random pattern");
        }

        [TestMethod]
        public void EmberRoll_OnTheFireAlcove_HasNoExclusion_AndValidatesAtKnotLevels200And400_PressedOrNot()
        {
            var r = FireAlcove();

            Assert.AreEqual(0, r.Exclusions.Count, "round 4: no doorway safe lane");

            foreach (var level in new[] { 200, 400 })
                foreach (var pressured in new[] { false, true })
                {
                    var args = HarbingerSettings.Defaults.EmberArgs(HarbingerSettings.Defaults.EmberPctAt(level), pressured);

                    Assert.AreEqual(BallPatterns.AxisAny, args.GetString("axis"));
                    Assert.IsNull(BallRules.Validate(r, args, BallPatterns.Random), $"level {level} pressured {pressured}");
                }
        }

        [TestMethod]
        public void EmberRoll_PlansBallsFromAllFourSides_AndOnBothAxes()
        {
            var r = FireAlcove();

            foreach (var pressured in new[] { false, true })
            {
                var p = Ember(pressured);
                var rng = new Random(20261119);
                int north = 0, south = 0, west = 0, east = 0, routes = 0;

                for (var wave = 1; wave <= 400; wave++)
                {
                    var plan = BallPatterns.Plan(r, BallPatterns.Random, p, rng, wave);

                    Assert.AreEqual(p.Count, plan.Routes.Count, $"wave {wave}: every wave rolls all its balls");

                    foreach (var route in plan.Routes)
                    {
                        routes++;

                        if (Math.Abs(route.From.X - route.To.X) < 1e-4f)
                        {
                            if (route.From.Y > route.To.Y) north++; else south++;
                        }
                        else
                        {
                            Assert.AreEqual(route.From.Y, route.To.Y, 1e-4f, "an axis-aligned lane");
                            if (route.From.X < route.To.X) west++; else east++;
                        }
                    }
                }

                Assert.AreEqual(400 * p.Count, routes);

                // sides here name where a ball starts: (north: starts at +y, rolls south), and so on; all four are drawn, each about a quarter
                foreach (var (name, n) in new[] { ("from-north", north), ("from-south", south), ("from-west", west), ("from-east", east) })
                    Assert.IsTrue(n > routes / 8, $"pressured {pressured}: {name} drew only {n} of {routes} balls");
            }
        }

        [TestMethod]
        public void ClearLanes_WithoutExclusions_IsTheWholeLateralRange()
        {
            var r = HazardRegionPresets.Pinnacle(0);
            var p = BallRules.Read(HazardArgs.Parse("axis=ew"), BallPatterns.Random);

            foreach (var side in BallPatterns.AxisSides(BallPatterns.AxisEastWest))
            {
                var runs = BallPatterns.ClearLanes(r, side, p);
                var (lo, hi) = BallPatterns.LateralRange(r, side, p);

                Assert.AreEqual(1, runs.Count, side.ToString());
                Assert.AreEqual(lo, runs[0].Lo, 1e-4);
                Assert.AreEqual(hi, runs[0].Hi, 1e-4);
            }
        }

        [TestMethod]
        public void AxisAny_ExplicitlyGiven_MatchesTheMasterGoldens()
        {
            // axis=any is the old random draw: the random goldens captured from master hold with it given explicitly
            foreach (var (pattern, set, seed, golden) in BallPatternsMasterFingerprints.Plans.Where(x => x.Pattern == BallPatterns.Random))
            {
                var args = (BallPatternsMasterFingerprints.ArgSets[set] + " axis=any").Trim();
                var merged = HazardArgs.Merge(HazardArgs.Parse(args), HazardArgs.Parse(HazardDefaults.Balls));
                var wave = BallPatterns.Plan(HazardRegionPresets.Pinnacle(0), pattern, BallRules.Read(merged, pattern), new Random(seed));
                var print = string.Join(";", wave.Routes.Select(x => FormattableString.Invariant($"{x.From.X:0.###},{x.From.Y:0.###}>{x.To.X:0.###},{x.To.Y:0.###}")))
                    + "|" + string.Join(";", wave.Gaps.Select(g => FormattableString.Invariant($"{g.Side}:{g.Lateral:0.###}")))
                    + "|" + string.Join(";", wave.Markers.Select(m => FormattableString.Invariant($"{m.X:0.###},{m.Y:0.###}")))
                    + "|" + wave.Summary;

                Assert.AreEqual(golden, print, $"set {set} seed {seed}");
            }

            Assert.IsTrue(BallPatternsMasterFingerprints.Plans.Count(x => x.Pattern == BallPatterns.Random) >= 10, "the goldens include random plans");
        }
    }
}
