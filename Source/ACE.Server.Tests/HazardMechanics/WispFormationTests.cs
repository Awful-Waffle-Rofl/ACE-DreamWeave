using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using ACE.Server.HazardMechanics;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.HazardMechanics
{
    /// <summary>
    /// The wisp patterns' geometry (WispFormations): pure, no world, no PropertyManager. Every pattern is run on a
    /// rect (the Pinnacle), a circle, an ad-hoc rect and an outdoor terrain circle.
    /// </summary>
    [TestClass]
    public class WispFormationTests
    {
        private static readonly string[] AllPatterns = { "scatter", "row", "column", "diagonal", "ring", "sweep", "mixed" };

        private static HazardRegion Pinnacle() => HazardRegionPresets.Pinnacle(0);

        private static HazardRegion CircleRegion() => HazardRegion.Circle("circle", 0x0069, 0, false, 15f, -20f, 14f,
            HazardGroundMode.Plane, -47.75f, -51f, 0f, null, 6.5f, 5.7f);

        private static HazardRegion AdHocRect()
        {
            var r = HazardRegion.AdHoc(0x0123, 0, false, new Vector3(100f, -60f, -47.75f), HazardShapeSpec.Rect(30f, 22f), -3f, out var error);
            Assert.IsNull(error);
            return r;
        }

        private static HazardRegion OutdoorCircle()
        {
            var r = HazardRegion.AdHoc(0xA9B4, 0, true, new Vector3(96f, 96f, 10f), HazardShapeSpec.Circle(18f), 2f, out var error);
            Assert.IsNull(error);
            Assert.AreEqual(HazardGroundMode.Terrain, r.GroundMode);
            return r;
        }

        private static IEnumerable<(string Name, HazardRegion Region)> Regions()
        {
            yield return ("pinnacle rect", Pinnacle());
            yield return ("circle", CircleRegion());
            yield return ("ad-hoc rect", AdHocRect());
            yield return ("outdoor terrain circle", OutdoorCircle());
        }

        private static WispSettings S(string args = "") => WispSettings.From(HazardArgs.Parse(args));

        private static WispFormation FirstStep(HazardRegion region, WispSettings s, string pattern, int seed)
            => WispFormations.Plan(region, s, pattern, new Random(seed)).Steps[0];

        private static void AssertUsable(string label, HazardRegion region, WispSettings s, WispFormation f)
        {
            foreach (var p in f.Points)
            {
                Assert.IsTrue(region.Contains(p.X, p.Y), $"{label}: ({p.X:0.00},{p.Y:0.00}) is outside the region");
                Assert.IsTrue(region.DistanceToEdge(p.X, p.Y) >= s.EdgeInset - 1e-3f, $"{label}: ({p.X:0.00},{p.Y:0.00}) is closer than edge_inset to the edge");
                Assert.IsFalse(region.InExclusion(p.X, p.Y), $"{label}: ({p.X:0.00},{p.Y:0.00}) is inside an exclusion");
            }
        }

        // ---- every pattern, every region ------------------------------------------------------------------------

        [TestMethod]
        public void EveryPattern_OnEveryRegion_StaysInsideTheInsetRegion_AndKeepsNeighboursAGapApart()
        {
            foreach (var (name, region) in Regions())
            {
                var s = S();

                foreach (var pattern in AllPatterns)
                {
                    Assert.IsNull(WispRules.Validate(region, HazardArgs.Empty, pattern), $"{name}/{pattern} should validate with the defaults");

                    for (var seed = 1; seed <= 30; seed++)
                    {
                        var plan = WispFormations.Plan(region, s, pattern, new Random(seed));

                        Assert.AreNotEqual("mixed", plan.Pattern, "a plan is never 'mixed'");

                        foreach (var step in plan.Steps)
                        {
                            var label = $"{name}/{pattern}->{plan.Pattern}/seed {seed}";
                            AssertUsable(label, region, s, step);
                            Assert.IsTrue(step.Points.Count <= s.Count, label + ": more wisps than n");

                            for (var i = 1; i < step.Points.Count; i++)
                            {
                                var d = Vector2.Distance(step.Points[i - 1], step.Points[i]);
                                Assert.IsTrue(d >= s.MinSpacing - 1e-3f, $"{label}: neighbours {d:0.00} m apart, need {s.MinSpacing:0.00}");
                            }
                        }
                    }
                }
            }
        }

        [TestMethod]
        public void TooManyWisps_AreCutToWhatFitsWithGaps_NotCrowded()
        {
            var region = Pinnacle();
            var s = S("n=32");

            foreach (var pattern in new[] { "row", "column", "diagonal", "ring", "scatter" })
            {
                var f = FirstStep(region, s, pattern, 5);

                Assert.IsTrue(f.Points.Count < 32, $"{pattern}: 32 wisps cannot fit with gaps, got {f.Points.Count}");

                for (var i = 0; i < f.Points.Count; i++)
                    for (var j = i + 1; j < f.Points.Count; j++)
                        if (pattern == "scatter" || j == i + 1)
                            Assert.IsTrue(Vector2.Distance(f.Points[i], f.Points[j]) >= s.MinSpacing - 1e-3f, $"{pattern}: points {i} and {j} too close");
            }
        }

        [TestMethod]
        public void Plan_IsReproduciblePerSeed_AndDiffersAcrossSeeds()
        {
            var region = Pinnacle();
            var s = S();

            var a = FirstStep(region, s, "scatter", 7).Points;
            var b = FirstStep(region, s, "scatter", 7).Points;
            var c = FirstStep(region, s, "scatter", 8).Points;

            CollectionAssert.AreEqual(a.ToArray(), b.ToArray());
            CollectionAssert.AreNotEqual(a.ToArray(), c.ToArray());
        }

        // ---- row and column -------------------------------------------------------------------------------------

        [TestMethod]
        public void Row_RunsWestEast_ForDirNAndS_WithARandomOffsetEachCycle()
        {
            var region = Pinnacle();
            var arena = WispArena.Of(region, 2f);

            foreach (var dir in new[] { "n", "s" })
            {
                var s = S("dir=" + dir);
                var horizontal = dir == "n" || dir == "s"; // a row is perpendicular to its dir
                var offsets = new HashSet<int>();

                for (var seed = 1; seed <= 20; seed++)
                {
                    var f = FirstStep(region, s, "row", seed);

                    Assert.IsTrue(f.Points.Count >= 2, dir);

                    var fixedAxis = f.Points.Select(p => horizontal ? p.Y : p.X).ToList();
                    Assert.IsTrue(fixedAxis.Max() - fixedAxis.Min() < 1e-3f, $"row dir={dir} is not a straight {(horizontal ? "west-east" : "south-north")} line");

                    var lo = horizontal ? arena.MinY : arena.MinX;
                    var hi = horizontal ? arena.MaxY : arena.MaxX;
                    Assert.IsTrue(fixedAxis[0] >= lo - 1e-3f && fixedAxis[0] <= hi + 1e-3f);

                    offsets.Add((int)Math.Round(fixedAxis[0] * 10f));
                }

                Assert.IsTrue(offsets.Count >= 8, $"row dir={dir}: the offset must be random each cycle, saw {offsets.Count} distinct");
            }
        }

        [TestMethod]
        public void PatternByDirTable_RowIsWestEast_ColumnIsNorthSouth_SweepTakesAllFour_AndRandomStaysOnItsAxis()
        {
            var region = Pinnacle();

            var table = new[]
            {
                ("row", "n", true), ("row", "s", true), ("row", "e", false), ("row", "w", false), ("row", "random", true),
                ("column", "n", false), ("column", "s", false), ("column", "e", true), ("column", "w", true), ("column", "random", true),
                ("sweep", "n", true), ("sweep", "s", true), ("sweep", "e", true), ("sweep", "w", true), ("sweep", "random", true),
            };

            foreach (var (pattern, dir, accepted) in table)
            {
                var refusal = WispRules.Validate(region, HazardArgs.Parse("dir=" + dir), pattern);

                Assert.AreEqual(accepted, refusal == null, $"{pattern} dir={dir}: {refusal}");

                if (!accepted)
                {
                    Assert.IsFalse(refusal.Contains('\n'));
                    continue;
                }

                var s = S("dir=" + dir);

                for (var seed = 1; seed <= 20; seed++)
                {
                    foreach (var step in WispFormations.Plan(region, s, pattern, new Random(seed)).Steps)
                    {
                        if (step.Points.Count < 2)
                            continue;

                        var xs = step.Points.Select(p => p.X).ToList();
                        var ys = step.Points.Select(p => p.Y).ToList();
                        var flatY = ys.Max() - ys.Min() < 1e-3f; // west-east line
                        var flatX = xs.Max() - xs.Min() < 1e-3f; // north-south line

                        if (pattern == "row")
                            Assert.IsTrue(flatY && !flatX, $"row dir={dir} must run west-east");
                        else if (pattern == "column")
                            Assert.IsTrue(flatX && !flatY, $"column dir={dir} must run north-south");
                        else
                            Assert.IsTrue(flatX ^ flatY, "a sweep row is straight");
                    }
                }
            }

            // random picks within the axis: a row only n or s, a column only e or w, a sweep any of the four
            var rowSides = new HashSet<WispCompass>();
            var columnSides = new HashSet<WispCompass>();
            var sweepSides = new HashSet<WispCompass>();
            var any = S("dir=random");

            for (var seed = 1; seed <= 60; seed++)
            {
                rowSides.Add(WispFormations.PickSide(any, new Random(seed), "row"));
                columnSides.Add(WispFormations.PickSide(any, new Random(seed), "column"));
                sweepSides.Add(WispFormations.PickSide(any, new Random(seed), "sweep"));
            }

            CollectionAssert.AreEquivalent(new[] { WispCompass.N, WispCompass.S }, rowSides.ToArray());
            CollectionAssert.AreEquivalent(new[] { WispCompass.E, WispCompass.W }, columnSides.ToArray());
            Assert.AreEqual(4, sweepSides.Count);

            // a dir off the pattern's axis that got past Validate is still kept on the axis, never rotated
            Assert.AreEqual(WispCompass.N, WispFormations.PickSide(S("dir=n"), new Random(1), "row"));

            var offAxisRow = WispFormations.PickSide(S("dir=e"), new Random(1), "row");
            Assert.IsTrue(offAxisRow == WispCompass.N || offAxisRow == WispCompass.S);

            var offAxisColumn = WispFormations.PickSide(S("dir=n"), new Random(1), "column");
            Assert.IsTrue(offAxisColumn == WispCompass.E || offAxisColumn == WispCompass.W);

            Assert.AreEqual(WispCompass.W, WispFormations.PickSide(S("dir=w"), new Random(1), "sweep"));
        }

        [TestMethod]
        public void RowAndColumn_ShareOneLineHelper()
        {
            var region = Pinnacle();
            var s = S();
            var arena = WispArena.Of(region, s.EdgeInset);

            foreach (var side in new[] { WispCompass.N, WispCompass.S })
                CollectionAssert.AreEqual(WispFormations.Line(region, arena, s, side, 6.0, s.Count, false, null, "x").Points.ToArray(),
                    WispFormations.Row(region, arena, s, side, 6.0).Points.ToArray());

            foreach (var side in new[] { WispCompass.E, WispCompass.W })
                CollectionAssert.AreEqual(WispFormations.Line(region, arena, s, side, 6.0, s.Count, false, null, "x").Points.ToArray(),
                    WispFormations.Column(region, arena, s, side, 6.0).Points.ToArray());
        }

        [TestMethod]
        public void Row_OffsetIsMeasuredFromTheNamedSide()
        {
            var region = Pinnacle();
            var s = S();
            var arena = WispArena.Of(region, s.EdgeInset);

            Assert.AreEqual(arena.MaxY, WispFormations.Row(region, arena, s, WispCompass.N, 0.0).Points[0].Y, 1e-3);
            Assert.AreEqual(arena.MinY, WispFormations.Row(region, arena, s, WispCompass.N, 1000.0).Points[0].Y, 1e-3, "an offset past the depth is clamped to the far side");
            Assert.AreEqual(arena.MinY, WispFormations.Row(region, arena, s, WispCompass.S, 0.0).Points[0].Y, 1e-3);
            Assert.AreEqual(arena.MaxX, WispFormations.Column(region, arena, s, WispCompass.E, 0.0).Points[0].X, 1e-3);
            Assert.AreEqual(arena.MinX, WispFormations.Column(region, arena, s, WispCompass.W, 0.0).Points[0].X, 1e-3);
            Assert.AreEqual(arena.MaxY - 5.0, WispFormations.Row(region, arena, s, WispCompass.N, 5.0).Points[0].Y, 1e-3);
        }

        [TestMethod]
        public void OnACircle_RowsAndColumnsSpanChords()
        {
            var region = CircleRegion();
            var s = S();
            var arena = WispArena.Of(region, s.EdgeInset);
            var r = arena.Radius; // 12

            var middle = WispFormations.Row(region, arena, s, WispCompass.N, r); // through the centre: the diameter
            var nearRim = WispFormations.Row(region, arena, s, WispCompass.N, 1.0); // a short chord

            Assert.AreEqual(4, middle.Points.Count, "24 m chord holds floor(24 / 5.5) wisps");
            Assert.AreEqual(1, nearRim.Points.Count, "a 9.6 m chord holds one");

            // the middle row spans the whole diameter: its end slots are within one slot of the arena rim
            var span = middle.Points.Max(p => p.X) - middle.Points.Min(p => p.X);
            Assert.IsTrue(span > 24f * 0.6f, $"span {span:0.0}");

            foreach (var p in middle.Points.Concat(nearRim.Points))
                Assert.IsTrue(arena.Contains(p.X, p.Y), "chord points stay inside the circle");

            // a chord is shorter than the same line on a rect of the same extent: more wisps fit across the Pinnacle
            var rect = Pinnacle();
            var rectRow = WispFormations.Row(rect, WispArena.Of(rect, 2f), s, WispCompass.N, 10.0);
            Assert.IsTrue(rectRow.Points.Count > middle.Points.Count);
        }

        // ---- diagonal ----------------------------------------------------------------------------------------

        [TestMethod]
        public void Diagonal_OnARect_RunsCornerToCorner_BothWays()
        {
            var region = Pinnacle();
            var s = S();
            var slope = (region.MaxY - region.MinY) / (region.MaxX - region.MinX);
            var seen = new HashSet<int>();

            for (var seed = 1; seed <= 20; seed++)
            {
                var f = FirstStep(region, s, "diagonal", seed);
                Assert.IsTrue(f.Points.Count >= 3);

                var signs = new HashSet<int>();

                foreach (var p in f.Points)
                {
                    var dx = p.X - region.CenterX;
                    var dy = p.Y - region.CenterY;

                    if (Math.Abs(dx) < 0.5f)
                        continue;

                    Assert.AreEqual(slope, Math.Abs(dy / dx), 0.01, "the rect's own diagonal slope, not 45 degrees");
                    signs.Add(Math.Sign(dy / dx));
                }

                Assert.AreEqual(1, signs.Count, "one straight line");
                seen.Add(signs.First());
            }

            CollectionAssert.AreEquivalent(new[] { -1, 1 }, seen.ToArray(), "both diagonals are used");
        }

        [TestMethod]
        public void Diagonal_OnACircle_IsAFortyFiveDegreeChordThroughTheCentre()
        {
            var region = CircleRegion();
            var s = S();

            for (var seed = 1; seed <= 10; seed++)
            {
                foreach (var p in FirstStep(region, s, "diagonal", seed).Points)
                    Assert.AreEqual(Math.Abs(p.X - region.CenterX), Math.Abs(p.Y - region.CenterY), 0.01);
            }
        }

        [TestMethod]
        public void Diagonal_PassesThroughAConfiguredCentre()
        {
            var region = Pinnacle();
            var s = S("cx=10 cy=-15");
            var slope = (region.MaxY - region.MinY) / (region.MaxX - region.MinX);

            foreach (var p in FirstStep(region, s, "diagonal", 2).Points)
            {
                var dx = p.X - 10f;

                if (Math.Abs(dx) > 0.5f)
                    Assert.AreEqual(slope, Math.Abs((p.Y + 15f) / dx), 0.01);
            }
        }

        // ---- ring --------------------------------------------------------------------------------------------

        [TestMethod]
        public void Ring_IsEvenlySpacedOnRingRadius_DefaultHalfTheInscribedRadius()
        {
            var region = Pinnacle();
            var s = S();
            var f = FirstStep(region, s, "ring", 4);
            var rr = region.InscribedRadius * 0.5f;

            Assert.AreEqual(8, f.Points.Count);

            foreach (var p in f.Points)
                Assert.AreEqual(rr, Vector2.Distance(p, region.Center), 1e-3);

            var angles = f.Points.Select(p => Math.Atan2(p.Y - region.CenterY, p.X - region.CenterX)).ToList();

            for (var i = 0; i < angles.Count; i++)
            {
                var diff = angles[(i + 1) % angles.Count] - angles[i];
                diff = ((diff % (2 * Math.PI)) + 2 * Math.PI) % (2 * Math.PI);
                Assert.AreEqual(2 * Math.PI / 8, diff, 1e-3, "even spacing");
            }
        }

        [TestMethod]
        public void Ring_HonoursACentreAndARadius_AndTurnsEachCycle()
        {
            var region = Pinnacle();
            var s = S("cx=10 cy=-15 ring_radius=6 n=6");
            var starts = new HashSet<int>();

            for (var seed = 1; seed <= 12; seed++)
            {
                var f = FirstStep(region, s, "ring", seed);

                Assert.AreEqual(6, f.Points.Count);

                foreach (var p in f.Points)
                    Assert.AreEqual(6f, Vector2.Distance(p, new Vector2(10f, -15f)), 1e-3);

                starts.Add((int)Math.Round(Math.Atan2(f.Points[0].Y + 15f, f.Points[0].X - 10f) * 100.0));
            }

            Assert.IsTrue(starts.Count >= 6, "the ring turns by a random phase each cycle");
        }

        [TestMethod]
        public void Ring_CountIsCutToWhatKeepsTheCorridor()
        {
            var region = Pinnacle();
            var s = S("ring_radius=6.5 n=8"); // 8 wisps would be 2 x 6.5 x sin(pi/8) = 4.98 m apart, under the 5.5 needed
            var f = FirstStep(region, s, "ring", 1);

            Assert.AreEqual(WispRules.RingCapacity(6.5f, s.MinSpacing), f.Points.Count);
            Assert.AreEqual(7, f.Points.Count);

            for (var i = 0; i < f.Points.Count; i++)
                Assert.IsTrue(Vector2.Distance(f.Points[i], f.Points[(i + 1) % f.Points.Count]) >= s.MinSpacing - 1e-3f);
        }

        // ---- scatter -----------------------------------------------------------------------------------------

        [TestMethod]
        public void Scatter_SpotsAreAtLeastTwoPillarRadiiApart_AndCoverAtMostTheCap()
        {
            var region = Pinnacle();
            var s = S("pillar_radius=6"); // 3 pillars cover 0.45 of the usable area; a 4th would pass 0.5
            var limit = WispRules.ScatterLimit(region, s);

            Assert.AreEqual(3, limit);

            for (var seed = 1; seed <= 20; seed++)
            {
                var f = FirstStep(region, s, "scatter", seed);

                Assert.IsTrue(f.Points.Count <= limit, $"seed {seed}: {f.Points.Count} pillars, cap {limit}");
                Assert.IsTrue(f.Points.Count >= 1);

                var covered = f.Points.Count * Math.PI * Math.Pow(s.PillarRadius + s.PlayerRadius, 2.0);
                Assert.IsTrue(covered <= s.ScatterCover * WispArena.Of(region, s.EdgeInset).Area + 1e-3, "coverage over the cap");

                for (var i = 0; i < f.Points.Count; i++)
                    for (var j = i + 1; j < f.Points.Count; j++)
                        Assert.IsTrue(Vector2.Distance(f.Points[i], f.Points[j]) >= 2f * s.PillarRadius, "closer than 2 x pillar_radius");
            }
        }

        [TestMethod]
        public void Scatter_CoverIsConfigurable_AndClamped()
        {
            var region = Pinnacle();

            Assert.AreEqual(0.5, S().ScatterCover, 1e-9);
            Assert.AreEqual(0.8, S("scatter_cover=5").ScatterCover, 1e-9);
            Assert.AreEqual(0.1, S("scatter_cover=0").ScatterCover, 1e-9);

            var loose = WispRules.ScatterLimit(region, S("pillar_radius=6 scatter_cover=0.8"));
            var tight = WispRules.ScatterLimit(region, S("pillar_radius=6 scatter_cover=0.2"));

            Assert.IsTrue(loose > tight);
        }

        // ---- sweep -------------------------------------------------------------------------------------------

        [TestMethod]
        public void Sweep_StepsAcrossTheRegionFromDir_ForAllFourDirs_WithOneGapSlotPerStep()
        {
            var region = Pinnacle();

            foreach (var dir in new[] { "n", "s", "e", "w" })
            {
                var s = S("dir=" + dir);
                var arena = WispArena.Of(region, s.EdgeInset);
                var horizontal = dir == "n" || dir == "s";
                var gaps = new HashSet<int>();

                for (var seed = 1; seed <= 20; seed++)
                {
                    var plan = WispFormations.Plan(region, s, "sweep", new Random(seed));
                    var depth = arena.Depth(WispFormations.ParseSide(dir));
                    var expectedSteps = (int)Math.Ceiling(depth / (2f * s.PillarRadius));

                    Assert.AreEqual("sweep", plan.Pattern);
                    Assert.AreEqual(expectedSteps, plan.Steps.Count, dir);

                    var previousLine = double.NaN;
                    var increasing = dir == "s" || dir == "w"; // moving away from the dir side

                    foreach (var step in plan.Steps)
                    {
                        var line = step.Points.Select(p => horizontal ? p.Y : p.X).ToList();
                        Assert.IsTrue(line.Max() - line.Min() < 1e-3f, $"sweep dir={dir}: a step is a straight row");

                        if (!double.IsNaN(previousLine))
                            Assert.IsTrue(increasing ? line[0] > previousLine + 1f : line[0] < previousLine - 1f, $"sweep dir={dir} must step away from that side");

                        previousLine = line[0];

                        // slots: 6 equal slots along the 35.2 m (or 25.2 m) line, one of them empty
                        var length = horizontal ? arena.MaxX - arena.MinX : arena.MaxY - arena.MinY;
                        var slots = Math.Min(s.Count + 1, (int)Math.Floor(length / s.MinSpacing + 1e-4));
                        var width = length / slots;
                        var start = horizontal ? arena.MinX : arena.MinY;
                        var taken = step.Points.Select(p => (int)Math.Round(((horizontal ? p.X : p.Y) - start) / width - 0.5)).ToList();

                        Assert.AreEqual(slots - 1, step.Points.Count, "exactly one gap slot");
                        Assert.AreEqual(taken.Count, taken.Distinct().Count());

                        var missing = Enumerable.Range(0, slots).Single(k => !taken.Contains(k));
                        gaps.Add(missing);
                    }
                }

                Assert.IsTrue(gaps.Count >= 4, $"sweep dir={dir}: the gap slot must be random per step, saw {gaps.Count} distinct");
            }
        }

        [TestMethod]
        public void Sweep_StepsArgSetsTheRowCount_AndACircleSweepShrinksItsChords()
        {
            var region = Pinnacle();

            Assert.AreEqual(3, WispFormations.Plan(region, S("dir=n steps=3"), "sweep", new Random(1)).Steps.Count);

            var circle = CircleRegion();
            var plan = WispFormations.Plan(circle, S("dir=n"), "sweep", new Random(1));
            var counts = plan.Steps.Select(st => st.Points.Count).ToList();

            Assert.IsTrue(counts.Max() > counts.First(), "the middle rows of a circle are longer than the rim rows: " + string.Join(",", counts));

            foreach (var st in plan.Steps)
                AssertUsable("circle sweep", circle, S(), st);
        }

        // ---- exclusions, mixed, spawn --------------------------------------------------------------------------

        [TestMethod]
        public void NoPatternPutsASpotInAnExclusion_AndARowThroughOneLosesTheSpotsInIt()
        {
            var exclusion = HazardExclusion.Circle(15f, -20f, 6f);
            var plain = Pinnacle();
            var region = HazardRegion.Rect("excl", 0x0069, 0, false, -4.6f, 34.6f, -34.6f, -5.4f, HazardGroundMode.Plane, -47.75f, -51f, 0f, null, 6.5f, 5.7f,
                new[] { exclusion });
            var s = S();

            foreach (var pattern in AllPatterns)
            {
                for (var seed = 1; seed <= 25; seed++)
                    foreach (var step in WispFormations.Plan(region, s, pattern, new Random(seed)).Steps)
                        AssertUsable($"{pattern}/{seed}", region, s, step);
            }

            var arenaPlain = WispArena.Of(plain, s.EdgeInset);
            var through = WispFormations.Row(plain, arenaPlain, s, WispCompass.N, arenaPlain.Depth(WispCompass.N) * 0.5);
            var cut = WispFormations.Row(region, WispArena.Of(region, s.EdgeInset), s, WispCompass.N, arenaPlain.Depth(WispCompass.N) * 0.5);

            Assert.IsTrue(cut.Points.Count < through.Points.Count, $"{cut.Points.Count} vs {through.Points.Count}");
        }

        [TestMethod]
        public void Mixed_PicksEverySixPatternsOverTime_AndDropsARingThatDoesNotFit()
        {
            var region = Pinnacle();
            var s = S();
            var seen = new HashSet<string>();

            for (var seed = 1; seed <= 80; seed++)
                seen.Add(WispFormations.Plan(region, s, "mixed", new Random(seed)).Pattern);

            CollectionAssert.AreEquivalent(new[] { "scatter", "row", "column", "diagonal", "ring", "sweep" }, seen.ToArray());

            var small = S("ring_radius=2");
            CollectionAssert.DoesNotContain(WispRules.Pool(region, small).ToArray(), "ring");
            CollectionAssert.Contains(WispRules.Pool(region, s).ToArray(), "ring");

            for (var seed = 1; seed <= 40; seed++)
                Assert.AreNotEqual("ring", WispFormations.Plan(region, small, "mixed", new Random(seed)).Pattern);

            // dir leaves out the pattern whose axis it does not name
            CollectionAssert.DoesNotContain(WispRules.Pool(region, S("dir=n")).ToArray(), "column");
            CollectionAssert.Contains(WispRules.Pool(region, S("dir=n")).ToArray(), "row");
            CollectionAssert.DoesNotContain(WispRules.Pool(region, S("dir=e")).ToArray(), "row");
            CollectionAssert.Contains(WispRules.Pool(region, S("dir=e")).ToArray(), "column");
        }

        [TestMethod]
        public void ASmallExclusion_IsNeverAPermanentSafeSpot_ABigOneIs()
        {
            var s = S();
            var reach = s.PillarRadius + s.PlayerRadius; // 2.5

            HazardRegion WithExclusion(float r) => HazardRegion.Rect("excl", 0x0069, 0, false, -4.6f, 34.6f, -34.6f, -5.4f, HazardGroundMode.Plane, -47.75f, -51f, 0f, null, 6.5f, 5.7f,
                new[] { HazardExclusion.Circle(15f, -20f, r) });

            List<Vector2> AllSpots(HazardRegion region)
            {
                var spots = new List<Vector2>();

                foreach (var pattern in AllPatterns)
                    for (var seed = 1; seed <= 200; seed++)
                        foreach (var step in WispFormations.Plan(region, s, pattern, new Random(seed)).Steps)
                            spots.AddRange(step.Points);

                return spots;
            }

            var probes = new List<Vector2>();

            for (var dx = -2f; dx <= 2f; dx += 0.5f)
                for (var dy = -2f; dy <= 2f; dy += 0.5f)
                    if (dx * dx + dy * dy <= 4f)
                        probes.Add(new Vector2(15f + dx, -20f + dy));

            var small = WithExclusion(2f);
            Assert.IsNull(WispRules.Validate(small, HazardArgs.Empty, "mixed"), "an exclusion inside a pillar's reach is allowed");

            var smallSpots = AllSpots(small);

            foreach (var probe in probes)
                Assert.IsTrue(smallSpots.Any(p => Vector2.Distance(p, probe) <= reach), $"probe ({probe.X:0.0},{probe.Y:0.0}) inside the exclusion is never reached by any pillar");

            // the control: a 6 m exclusion leaves its middle unreachable, which is exactly why Validate refuses it
            var big = WithExclusion(6f);
            StringAssert.Contains(WispRules.Validate(big, HazardArgs.Empty, "mixed"), "exclusion");

            var bigSpots = AllSpots(big);
            Assert.IsFalse(bigSpots.Any(p => Vector2.Distance(p, new Vector2(15f, -20f)) <= reach), "no pillar can reach the middle of a 6 m exclusion");
        }

        [TestMethod]
        public void SpawnPoints_AreOpenPlaceableAndInsideTheRegion()
        {
            var region = Pinnacle();
            var rng = new Random(9);

            // only the east half has a cell at wisp height: every west edge spot is refused and must be moved
            Func<Vector2, bool> open = p => p.X >= 10f;

            var points = WispFormations.SpawnPoints(region, 8, 2f, rng, open);

            Assert.AreEqual(8, points.Count);

            foreach (var p in points)
            {
                Assert.IsTrue(open(p), "a wisp is never spawned in a solid");
                Assert.IsTrue(region.IsPlaceable(p.X, p.Y));
                Assert.IsTrue(region.DistanceToEdge(p.X, p.Y) >= 2f - 1e-3f);
            }

            Assert.AreEqual(0, WispFormations.SpawnPoints(region, 8, 2f, new Random(1), _ => false).Count, "no open spot, no wisps");
        }

        [TestMethod]
        public void TheCommandPath_AdHocRegionAroundTheCaller_ThenPlan()
        {
            Assert.IsTrue(HazardCommandRequest.TryParse(new[] { "wisps", "ring", "region=circle:14", "ring_radius=6", "n=6", "fire=sequence" }, out var request, out var error), error);
            Assert.AreEqual("ring", request.Pattern);

            Assert.IsTrue(request.TryResolveRegion(0x0123, 0, false, new Vector3(50f, -50f, -47.75f), out var region, out error), error);
            Assert.IsNull(WispRules.Validate(region, request.Args, request.Pattern));

            var s = WispSettings.From(request.Args);

            foreach (var p in WispFormations.Plan(region, s, request.Pattern, new Random(3)).Steps[0].Points)
                Assert.AreEqual(6f, Vector2.Distance(p, new Vector2(50f, -50f)), 1e-3, "the ring is round the caller");
        }
    }
}
