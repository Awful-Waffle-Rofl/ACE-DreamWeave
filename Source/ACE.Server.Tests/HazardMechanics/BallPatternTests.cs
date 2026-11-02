using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using ACE.Server.HazardMechanics;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.HazardMechanics
{
    /// <summary>
    /// The pure geometry of the balls patterns (BallPatterns): every pattern on a Rect, a Circle and ad-hoc regions
    /// that are nothing like the Pinnacle, all four row sides, the cross intersection, and positive controls that
    /// prove each safety check can fail. No world, no PropertyManager, no clock.
    /// </summary>
    [TestClass]
    public class BallPatternTests
    {
        private const float Eps = 1e-3f;

        private static HazardRegion Pinnacle() => HazardRegionPresets.Pinnacle(0);

        private static HazardRegion Circle() => HazardRegion.Circle("circle", 0x0069, 0, false, 15f, -20f, 14f,
            HazardGroundMode.Plane, -47.75f, null, 2f, null, 2f, 6f);

        /// <summary>An ad-hoc OUTDOOR terrain rect around (96, 96): the shape /hazard region=rect:34x24 builds anywhere.</summary>
        private static HazardRegion AdHocRect()
        {
            var r = HazardRegion.AdHoc(0xA9B4, 0, true, new Vector3(96f, 96f, 30f), HazardShapeSpec.Rect(34f, 24f), 0f, out var error);
            Assert.IsNull(error);
            return r;
        }

        private static HazardRegion AdHocCircle()
        {
            var r = HazardRegion.AdHoc(0xA9B4, 0, true, new Vector3(60f, 120f, 30f), HazardShapeSpec.Circle(18f), 0f, out var error);
            Assert.IsNull(error);
            return r;
        }

        private static IEnumerable<(string Name, HazardRegion Region)> Regions()
        {
            yield return ("pinnacle rect", Pinnacle());
            yield return ("circle", Circle());
            yield return ("adhoc terrain rect", AdHocRect());
            yield return ("adhoc terrain circle", AdHocCircle());
        }

        private static BallParams P(string args, string pattern) => BallRules.Read(HazardArgs.Parse(args), pattern);

        private static BallWave Plan(HazardRegion region, string pattern, string args = "", int seed = 1)
            => BallPatterns.Plan(region, pattern, P(args, pattern), new Random(seed));

        private static float DistToSegment(Vector2 pt, BallRoute r)
        {
            var ab = r.To - r.From;
            var len2 = ab.LengthSquared();
            var t = len2 <= 0f ? 0f : Math.Clamp(Vector2.Dot(pt - r.From, ab) / len2, 0f, 1f);
            return Vector2.Distance(pt, r.From + ab * t);
        }

        private static float MinDistToAny(Vector2 pt, IEnumerable<BallRoute> routes)
            => routes.Select(r => DistToSegment(pt, r)).DefaultIfEmpty(float.PositiveInfinity).Min();

        // ---- every route is inside the region, whatever the pattern -------------------------------------------------

        [TestMethod]
        public void EveryPattern_OnEveryRegion_KeepsEveryPointInsideAndOffExclusions()
        {
            foreach (var (name, region) in Regions())
            {
                foreach (var pattern in BallPatterns.All)
                {
                    for (var seed = 1; seed <= 12; seed++)
                    {
                        var p = P("", pattern);
                        var wave = BallPatterns.Plan(region, pattern, p, new Random(seed));
                        var floor = Math.Min(p.Inset, p.Reach * 0.5f) - Eps;

                        Assert.IsTrue(wave.Routes.Count > 0, $"{name}/{pattern}/seed {seed}: no routes");

                        foreach (var r in wave.Routes)
                        {
                            foreach (var pt in new[] { r.From, r.To })
                            {
                                Assert.IsTrue(region.Contains(pt.X, pt.Y), $"{name}/{pattern}/seed {seed}: ({pt.X},{pt.Y}) leaves the region");
                                Assert.IsTrue(region.DistanceToEdge(pt.X, pt.Y) >= floor, $"{name}/{pattern}/seed {seed}: ({pt.X},{pt.Y}) is {region.DistanceToEdge(pt.X, pt.Y)} from the edge");
                            }

                            Assert.IsTrue(r.Length >= HazardDefaults.BallsMinLane - Eps, $"{name}/{pattern}: a {r.Length} m path");
                        }
                    }
                }
            }
        }

        [TestMethod]
        public void ExclusionZone_NeverHoldsAnEndpoint_AndAShortenedPathStillMoves()
        {
            // a pad across the middle of the Pinnacle-sized room: no start or end may land in it
            var pad = HazardExclusion.Rect(10f, 20f, -30f, -10f);

            var region = HazardRegion.Rect("padded", 0x0069, 0, false, -4.6f, 34.6f, -34.6f, -5.4f,
                HazardGroundMode.Plane, -47.75f, null, 0f, null, 2f, 6f, new[] { pad });

            foreach (var pattern in new[] { BallPatterns.RingIn, BallPatterns.Row, BallPatterns.Cross, BallPatterns.Random })
            {
                for (var seed = 1; seed <= 10; seed++)
                {
                    var wave = Plan(region, pattern, "", seed);

                    foreach (var r in wave.Routes)
                    {
                        Assert.IsFalse(region.InExclusion(r.From.X, r.From.Y), $"{pattern}/seed {seed}: start in the pad");
                        Assert.IsFalse(region.InExclusion(r.To.X, r.To.Y), $"{pattern}/seed {seed}: end in the pad");
                    }
                }
            }

            // positive control: without the pad a ring-in ends at the centre (15, -20), which IS inside it
            var open = Plan(Pinnacle(), BallPatterns.RingIn, "", 3);
            Assert.IsTrue(open.Routes.All(r => pad.Contains(r.To.X, r.To.Y)), "the control must land in the pad, or the test proves nothing");

            // and with the pad the ring-in balls still roll, stopping at the pad's edge
            var padded = Plan(region, BallPatterns.RingIn, "", 3);
            Assert.IsTrue(padded.Routes.Count > 0 && padded.Routes.All(r => r.Length >= HazardDefaults.BallsMinLane));
        }

        // ---- rings ---------------------------------------------------------------------------------------------------

        [TestMethod]
        public void RingOut_IsEvenlySpacedFromTheCentreToTheInsetEdge_OnEveryRegion()
        {
            foreach (var (name, region) in Regions())
            {
                var p = P("count=8", BallPatterns.RingOut);
                var wave = BallPatterns.Plan(region, BallPatterns.RingOut, p, new Random(5));

                Assert.AreEqual(8, wave.Routes.Count, name);

                var angles = wave.Routes.Select(r =>
                {
                    Assert.AreEqual(region.CenterX, r.From.X, Eps, name + " starts at the centre");
                    Assert.AreEqual(region.CenterY, r.From.Y, Eps, name + " starts at the centre");
                    Assert.AreEqual(p.Inset, region.DistanceToEdge(r.To.X, r.To.Y), 0.02f, name + " stops at the edge, inset");
                    return Math.Atan2(r.To.Y - r.From.Y, r.To.X - r.From.X);
                }).OrderBy(a => a).ToList();

                for (var i = 0; i < angles.Count; i++)
                {
                    var next = i + 1 < angles.Count ? angles[i + 1] : angles[0] + 2 * Math.PI;
                    Assert.AreEqual(2 * Math.PI / 8, next - angles[i], 1e-3, $"{name}: uneven spacing");
                }
            }
        }

        [TestMethod]
        public void RingIn_IsTheReverseOfRingOut_ConvergingOnTheCentre()
        {
            foreach (var (name, region) in Regions())
            {
                var outWave = BallPatterns.Plan(region, BallPatterns.RingOut, P("count=6", BallPatterns.RingOut), new Random(9));
                var inWave = BallPatterns.Plan(region, BallPatterns.RingIn, P("count=6", BallPatterns.RingIn), new Random(9));

                Assert.AreEqual(outWave.Routes.Count, inWave.Routes.Count, name);

                for (var i = 0; i < outWave.Routes.Count; i++)
                {
                    Assert.AreEqual(outWave.Routes[i].To.X, inWave.Routes[i].From.X, Eps, name);
                    Assert.AreEqual(outWave.Routes[i].To.Y, inWave.Routes[i].From.Y, Eps, name);
                    Assert.AreEqual(region.CenterX, inWave.Routes[i].To.X, Eps, name);
                    Assert.AreEqual(region.CenterY, inWave.Routes[i].To.Y, Eps, name);
                }
            }
        }

        /// <summary>
        /// PIN (pinnacle-encounter M1.4, plan section 7 Q5): a ring's rotation is already random per wave. The run's one
        /// Random is drawn again for each wave, so two waves planned from it start at different angles; the phase is
        /// always under one ray step (2 pi / n), so the rotation never skips a whole spoke. Existing behaviour, pinned here
        /// so a later change that fixes the rotation fails a test.
        /// </summary>
        [TestMethod]
        public void Ring_TwoWaves_SameSeed_DifferentPhase()
        {
            foreach (var pattern in new[] { BallPatterns.RingOut, BallPatterns.RingIn })
            {
                const int n = 8;
                var region = Pinnacle();
                var p = P("count=8", pattern);
                var rng = new Random(21);

                var first = BallPatterns.Plan(region, pattern, p, rng);
                var second = BallPatterns.Plan(region, pattern, p, rng);

                // the ray angle of the first spoke, measured at the centre (a ring-in route runs edge -> centre, so flip it)
                float Phase(BallWave w)
                {
                    var r = w.Routes[0];
                    var d = pattern == BallPatterns.RingOut ? r.To - r.From : r.From - r.To;
                    var a = (float)Math.Atan2(d.Y, d.X);
                    return a < 0f ? a + 2f * (float)Math.PI : a;
                }

                Assert.AreEqual(n, first.Routes.Count, pattern);
                Assert.AreEqual(n, second.Routes.Count, pattern);
                Assert.AreNotEqual(Phase(first), Phase(second), 1e-3f, pattern + ": the second wave must start at a different angle");

                var step = 2f * (float)Math.PI / n;
                Assert.IsTrue(Phase(first) >= 0f && Phase(first) < step + Eps, pattern + ": phase " + Phase(first));
                Assert.IsTrue(Phase(second) >= 0f && Phase(second) < step + Eps, pattern + ": phase " + Phase(second));

                // the same seed from the start gives the same first wave (the rotation is seeded, not free-running)
                var again = BallPatterns.Plan(region, pattern, p, new Random(21));
                Assert.AreEqual(Phase(first), Phase(again), 1e-5f, pattern);
            }
        }

        [TestMethod]
        public void Ring_UsesAnExplicitCentre_AndStaysInsideTheRegionFromThere()
        {
            var region = Pinnacle();
            var wave = Plan(region, BallPatterns.RingOut, "count=6 cx=5 cy=-12", 4);

            Assert.IsTrue(wave.Routes.All(r => Math.Abs(r.From.X - 5f) < Eps && Math.Abs(r.From.Y + 12f) < Eps), "every ball starts at cx/cy");
            Assert.IsTrue(wave.Routes.All(r => region.DistanceToEdge(r.To.X, r.To.Y) >= 2f - 0.02f), "every ray stops inside the inset edge");

            // the rays are NOT all the same length from an off-centre point (the control: the centred ring has 4 equal-ish pairs)
            Assert.IsTrue(wave.Routes.Select(r => r.Length).Distinct().Count() > 1);
        }

        [TestMethod]
        public void Ring_SafeLaneOpensAtTheDocumentedRadius_AndNotBefore()
        {
            foreach (var (name, region) in Regions())
            {
                const int n = 6;
                var p = P("count=6", BallPatterns.RingOut);
                var wave = BallPatterns.Plan(region, BallPatterns.RingOut, p, new Random(2));
                var w = BallRules.MinGap(p);
                var rLane = (float)BallRules.LaneRadius(w, n);

                var angles = wave.Routes.Select(r => (float)Math.Atan2(r.To.Y - r.From.Y, r.To.X - r.From.X)).OrderBy(a => a).ToList();
                var mid = (angles[0] + angles[1]) * 0.5f;
                var centre = region.Center;

                var open = centre + new Vector2((float)Math.Cos(mid), (float)Math.Sin(mid)) * (rLane + 0.01f);
                var tooClose = centre + new Vector2((float)Math.Cos(mid), (float)Math.Sin(mid)) * (rLane * 0.5f);

                Assert.IsTrue(MinDistToAny(open, wave.Routes) > p.Reach, $"{name}: no lane at r={rLane}");
                Assert.IsTrue(MinDistToAny(tooClose, wave.Routes) < p.Reach, $"{name}: control, half that radius must be in reach");
            }
        }

        // ---- rows: all four sides ----------------------------------------------------------------------------------

        [TestMethod]
        public void Row_FromEachSide_StartsOnThatSide_AndRollsStraightAcrossToTheOpposite()
        {
            foreach (var (name, region) in Regions())
            {
                foreach (var letter in new[] { "n", "s", "e", "w" })
                {
                    var wave = Plan(region, BallPatterns.Row, "dir=" + letter, 7);
                    var side = BallPatterns.ParseSide(letter[0]);

                    Assert.IsTrue(wave.Routes.Count >= 3, $"{name}/{letter}: a row needs a wall");

                    foreach (var r in wave.Routes)
                    {
                        switch (side)
                        {
                            case BallSide.North: Assert.AreEqual(r.From.X, r.To.X, Eps); Assert.IsTrue(r.From.Y > r.To.Y, $"{name}: north row must roll south"); break;
                            case BallSide.South: Assert.AreEqual(r.From.X, r.To.X, Eps); Assert.IsTrue(r.From.Y < r.To.Y, $"{name}: south row must roll north"); break;
                            case BallSide.East: Assert.AreEqual(r.From.Y, r.To.Y, Eps); Assert.IsTrue(r.From.X > r.To.X, $"{name}: east row must roll west"); break;
                            default: Assert.AreEqual(r.From.Y, r.To.Y, Eps); Assert.IsTrue(r.From.X < r.To.X, $"{name}: west row must roll east"); break;
                        }
                    }

                    // on a rect every ball starts exactly edge_inset from the starting wall and ends edge_inset from the far wall
                    if (region.Shape == HazardShape.Rect)
                    {
                        var inset = 2f;
                        var expectFrom = side switch
                        {
                            BallSide.North => region.MaxY - inset,
                            BallSide.South => region.MinY + inset,
                            BallSide.East => region.MaxX - inset,
                            _ => region.MinX + inset,
                        };
                        var fromValue = BallPatterns.LateralIsX(side) ? wave.Routes[0].From.Y : wave.Routes[0].From.X;
                        Assert.AreEqual(expectFrom, fromValue, Eps, $"{name}/{letter}");
                    }
                }
            }
        }

        [TestMethod]
        public void Row_RandomDir_PicksAllFourSidesAcrossWaves()
        {
            var seen = new HashSet<string>();

            for (var seed = 1; seed <= 60; seed++)
            {
                var wave = Plan(Pinnacle(), BallPatterns.Row, "", seed);
                seen.Add(wave.Routes[0].Tag);
            }

            CollectionAssert.AreEquivalent(new[] { "row-north", "row-south", "row-east", "row-west" }, seen.ToArray());
        }

        [TestMethod]
        public void Row_HasExactlyOneGap_AtTheWidthAsked_AndTheGapMovesBetweenWaves()
        {
            foreach (var (name, region) in Regions())
            {
                foreach (var side in new[] { BallSide.North, BallSide.East })
                {
                    var p = P("dir=" + BallPatterns.Name(side)[0] + " gap=7", BallPatterns.Row);
                    var gaps = new HashSet<int>();

                    for (var seed = 1; seed <= 15; seed++)
                    {
                        var wave = BallPatterns.Plan(region, BallPatterns.Row, p, new Random(seed));
                        var (lo, hi) = BallPatterns.LateralRange(region, side, p);
                        var lateral = wave.Routes.Select(r => BallPatterns.LateralIsX(side) ? r.From.X : r.From.Y).ToList();

                        var free = FreeIntervals(lateral, lo, hi, p.Reach);

                        Assert.AreEqual(1, free.Count, $"{name}/{side}/seed {seed}: expected ONE gap, found {free.Count}");

                        var gc = wave.Gaps.Single().Lateral;
                        Assert.IsTrue(free[0].Lo <= gc && gc <= free[0].Hi, $"{name}/{side}: the free interval holds the gap centre");

                        // the flank balls are exactly `gap` apart centre to centre (or sit on the lateral limit)
                        var flankLo = lateral.Where(q => q <= gc).DefaultIfEmpty(lo).Max();
                        var flankHi = lateral.Where(q => q >= gc).DefaultIfEmpty(hi).Min();
                        Assert.IsTrue(flankHi - flankLo >= p.Gap - Eps, $"{name}/{side}: flanks {flankHi - flankLo} m apart");

                        gaps.Add((int)Math.Round(gc));
                    }

                    Assert.IsTrue(gaps.Count > 3, $"{name}/{side}: the gap must move wave to wave (saw {gaps.Count} positions)");
                }
            }
        }

        [TestMethod]
        public void Row_TheGapLane_IsClearOfEveryBall_OnEveryRegionAndSide()
        {
            foreach (var (name, region) in Regions())
            {
                foreach (var letter in new[] { "n", "s", "e", "w" })
                {
                    for (var seed = 1; seed <= 8; seed++)
                    {
                        var p = P("dir=" + letter, BallPatterns.Row);
                        var wave = BallPatterns.Plan(region, BallPatterns.Row, p, new Random(seed));
                        var (side, gc) = wave.Gaps.Single();

                        Assert.IsTrue(BallPatterns.TryLane(region, side, gc, p, out var start, out var end), name);

                        // walk the whole gap lane: a player standing anywhere on it is out of every ball's reach
                        for (var t = 0f; t <= 1f; t += 0.1f)
                        {
                            var pt = Vector2.Lerp(start, end, t);
                            Assert.IsTrue(MinDistToAny(pt, wave.Routes) > p.Reach, $"{name}/{letter}/seed {seed}: gap lane is hit at t={t}");
                        }
                    }
                }
            }
        }

        [TestMethod]
        public void Row_Wall_PositiveControl_ASparserPitchWouldLeaveHoles()
        {
            var reach = 2.3f;

            // the shipped wall: one gap
            var good = BallPatterns.WallPositions(0f, 36f, 18f, 6f, reach);
            Assert.AreEqual(1, FreeIntervals(good, 0f, 36f, reach).Count);

            // the same wall built as if the pitch were 1.2 x wider (reach x 1.2 -> pitch 2.16 reach, over the 2 reach a player needs): holes appear
            var sparse = BallPatterns.WallPositions(0f, 36f, 18f, 6f, reach * 1.2f);
            Assert.IsTrue(FreeIntervals(sparse, 0f, 36f, reach).Count > 1, "the check must be able to see a hole");
        }

        // ---- cross ---------------------------------------------------------------------------------------------------

        [TestMethod]
        public void Cross_TwoAdjacentRows_AndTheGapLanesIntersectAtAReachableSafePoint()
        {
            foreach (var (name, region) in Regions())
            {
                foreach (var dirs in new[] { "ne", "nw", "se", "sw", "random" })
                {
                    for (var seed = 1; seed <= 10; seed++)
                    {
                        var p = P("dirs=" + dirs, BallPatterns.Cross);
                        var wave = BallPatterns.Plan(region, BallPatterns.Cross, p, new Random(seed));

                        Assert.AreEqual(2, wave.Gaps.Count, name);
                        Assert.AreNotEqual(BallPatterns.LateralIsX(wave.Gaps[0].Side), BallPatterns.LateralIsX(wave.Gaps[1].Side), $"{name}/{dirs}: the sides must be adjacent");

                        if (dirs != "random")
                        {
                            var want = dirs.Select(BallPatterns.ParseSide).OrderBy(s => s).ToArray();
                            CollectionAssert.AreEqual(want, wave.Gaps.Select(g => g.Side).OrderBy(s => s).ToArray());
                        }

                        var safe = wave.SafePoint.Value;

                        Assert.IsTrue(region.IsPlaceable(safe.X, safe.Y), $"{name}/{dirs}/seed {seed}: the safe point is not standable");
                        Assert.IsTrue(MinDistToAny(safe, wave.Routes) > p.Reach, $"{name}/{dirs}/seed {seed}: the intersection is in reach of a ball ({MinDistToAny(safe, wave.Routes)})");

                        // the safe point IS where the two gap lanes cross
                        foreach (var (side, lateral) in wave.Gaps)
                            Assert.AreEqual(lateral, BallPatterns.LateralIsX(side) ? safe.X : safe.Y, Eps);
                    }
                }
            }
        }

        [TestMethod]
        public void Cross_PositiveControl_AGapLaneAloneIsNotSafe_OnlyTheIntersectionIs()
        {
            var region = Pinnacle();
            var p = P("dirs=ne", BallPatterns.Cross);
            var wave = BallPatterns.Plan(region, BallPatterns.Cross, p, new Random(3));
            var safe = wave.SafePoint.Value;

            // down the north row's gap lane (x = safe.X) but at a y outside the east row's gap: the east row hits it
            var offLane = new Vector2(safe.X, safe.Y + p.Gap + p.Reach * 2f);

            if (!region.Contains(offLane.X, offLane.Y))
                offLane = new Vector2(safe.X, safe.Y - p.Gap - p.Reach * 2f);

            Assert.IsTrue(region.Contains(offLane.X, offLane.Y));
            Assert.IsTrue(MinDistToAny(offLane, wave.Routes) < p.Reach, "a point in one gap lane only must be hit by the other row");
        }

        // ---- random --------------------------------------------------------------------------------------------------

        [TestMethod]
        public void Random_LaunchesStaggeredOnePerSlot_EachPerpendicularToItsSide()
        {
            foreach (var (name, region) in Regions())
            {
                var p = P("count=8 window=24", BallPatterns.Random);
                var wave = BallPatterns.Plan(region, BallPatterns.Random, p, new Random(11));
                var slot = 24.0 / 8;

                Assert.AreEqual(8, wave.Routes.Count, name);

                for (var i = 0; i < wave.Routes.Count; i++)
                {
                    var r = wave.Routes[i];

                    Assert.IsTrue(r.Delay >= i * slot && r.Delay <= (i + 1) * slot, $"{name}: ball {i} delay {r.Delay} outside slot {i}");

                    var side = r.Tag.Substring("random-".Length);

                    switch (side)
                    {
                        case "north": Assert.AreEqual(r.From.X, r.To.X, Eps); Assert.IsTrue(r.From.Y > r.To.Y); break;
                        case "south": Assert.AreEqual(r.From.X, r.To.X, Eps); Assert.IsTrue(r.From.Y < r.To.Y); break;
                        case "east": Assert.AreEqual(r.From.Y, r.To.Y, Eps); Assert.IsTrue(r.From.X > r.To.X); break;
                        default: Assert.AreEqual(r.From.Y, r.To.Y, Eps); Assert.IsTrue(r.From.X < r.To.X); break;
                    }
                }
            }
        }

        [TestMethod]
        public void Random_UsesAllFourSides_IsReproduciblePerSeed_AndDiffersBetweenSeeds()
        {
            var region = Pinnacle();
            var a = Plan(region, BallPatterns.Random, "count=40 window=60 speed=8", 21);
            var b = Plan(region, BallPatterns.Random, "count=40 window=60 speed=8", 21);
            var c = Plan(region, BallPatterns.Random, "count=40 window=60 speed=8", 22);

            CollectionAssert.AreEquivalent(new[] { "random-north", "random-south", "random-east", "random-west" }, a.Routes.Select(r => r.Tag).Distinct().ToArray());
            CollectionAssert.AreEqual(a.Routes.Select(r => r.From).ToArray(), b.Routes.Select(r => r.From).ToArray(), "same seed, same plan");
            CollectionAssert.AreNotEqual(a.Routes.Select(r => r.From).ToArray(), c.Routes.Select(r => r.From).ToArray(), "control: a different seed differs");
        }

        // ---- helpers -------------------------------------------------------------------------------------------------

        /// <summary>The lateral stretches of [lo, hi] no ball (centre at each of <paramref name="walls"/>) reaches: a player there is safe.</summary>
        private static List<(float Lo, float Hi)> FreeIntervals(IEnumerable<float> walls, float lo, float hi, float reach)
        {
            var list = walls.ToList();
            var result = new List<(float, float)>();
            float? start = null;
            var last = lo;

            for (var x = lo; x <= hi + 1e-4f; x += 0.02f)
            {
                var free = list.All(q => Math.Abs(x - q) > reach);

                if (free && start == null)
                    start = x;

                if (!free && start != null)
                {
                    result.Add((start.Value, last));
                    start = null;
                }

                last = x;
            }

            if (start != null)
                result.Add((start.Value, last));

            return result;
        }
    }
}
