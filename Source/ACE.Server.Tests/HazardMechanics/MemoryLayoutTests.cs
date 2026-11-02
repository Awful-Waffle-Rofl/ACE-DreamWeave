using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using ACE.Server.HazardMechanics;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.HazardMechanics
{
    /// <summary>Pure tests of the memory game's geometry: pad layout on every region kind, the marker ring, the beam points.</summary>
    [TestClass]
    public class MemoryLayoutTests
    {
        private const float Radius = 1f;
        private const float Gap = 6f;
        private const float Inset = 2f;

        private static HazardRegion Rect(float size = 40f, params HazardExclusion[] exclusions)
            => HazardRegion.Rect("rect", 0x0069, 0, false, 0, size, 0, size, HazardGroundMode.Plane, 0, null, 0, null, 2, 6, exclusions);

        private static HazardRegion Circle(float r = 20f)
            => HazardRegion.Circle("circle", 0x0069, 0, false, 100, 100, r, HazardGroundMode.Plane, 0, null, 0, null, 2, 6);

        private static HazardRegion AdHocCircle()
        {
            var region = HazardRegion.AdHoc(0x0069, 0, false, new Vector3(50, 50, -10), HazardShapeSpec.Circle(15), 0, out var error);
            Assert.IsNull(error);
            return region;
        }

        private static HazardRegion AdHocRect()
        {
            var region = HazardRegion.AdHoc(0x0069, 0, false, new Vector3(50, 50, -10), HazardShapeSpec.Rect(24, 24), 0, out var error);
            Assert.IsNull(error);
            return region;
        }

        private static HazardRegion Outdoor()
        {
            var region = HazardRegion.AdHoc(0x0069, 0, true, new Vector3(96, 96, 40), HazardShapeSpec.Circle(15), 0, out var error);
            Assert.IsNull(error);
            Assert.AreEqual(HazardGroundMode.Terrain, region.GroundMode);
            return region;
        }

        private static void AssertGoodLayout(HazardRegion region, IReadOnlyList<Vector2> pads, string what)
        {
            Assert.AreEqual(4, pads.Count, what);
            Assert.IsNull(MemoryLayout.CheckPads(region, pads, Radius, Gap, Inset), what);

            for (var i = 0; i < 4; i++)
            {
                Assert.IsTrue(region.IsPlaceable(pads[i].X, pads[i].Y), $"{what}: pad {i} placeable");
                Assert.IsTrue(region.DistanceToEdge(pads[i].X, pads[i].Y) >= Radius + Inset - 1e-3f, $"{what}: pad {i} inset from the edge");

                for (var j = 0; j < i; j++)
                    Assert.IsTrue(Vector2.Distance(pads[i], pads[j]) >= Gap - 1e-3f, $"{what}: pads {i} and {j} keep the gap");
            }
        }

        // ---- default quadrant layout ------------------------------------------------------------------------------

        [TestMethod]
        public void Rect_QuadrantCentres_AreTheCentresOfTheFourQuarters()
        {
            var region = HazardRegion.Rect("r", 0x0069, 0, false, 10, 50, 20, 100, HazardGroundMode.Plane, 0, null, 0, null, 2, 6);

            var pads = MemoryLayout.QuadrantCentres(region);

            CollectionAssert.AreEqual(
                new[] { new Vector2(20, 40), new Vector2(40, 40), new Vector2(40, 80), new Vector2(20, 80) },
                pads.ToArray());
        }

        [TestMethod]
        public void Circle_QuadrantCentres_AreFourPointsAtHalfTheRadius_NinetyDegreesApart()
        {
            var region = Circle(20f);
            var pads = MemoryLayout.QuadrantCentres(region);

            Assert.AreEqual(4, pads.Count);

            foreach (var p in pads)
                Assert.AreEqual(10f, Vector2.Distance(p, region.Center), 1e-3f, "half the radius from the centre");

            for (var i = 0; i < 4; i++)
            {
                var a = Math.Atan2(pads[i].Y - 100, pads[i].X - 100) * 180 / Math.PI;
                var expected = 45 + 90 * i;
                var diff = ((a - expected) % 360 + 540) % 360 - 180;
                Assert.AreEqual(0.0, diff, 0.01, $"pad {i} at {expected} degrees");
            }
        }

        [TestMethod]
        public void QuadrantLayout_FitsRect_Circle_AdHocAndOutdoorRegions()
        {
            foreach (var (name, region) in new[]
            {
                ("rect", Rect()), ("circle", Circle()), ("adhoc circle", AdHocCircle()), ("adhoc rect", AdHocRect()), ("outdoor", Outdoor()),
            })
            {
                Assert.IsNull(MemoryLayout.Feasible(region, false, Radius, Gap, Inset), name);

                var pads = MemoryLayout.Plan(region, false, new Random(1), Radius, Gap, Inset);

                Assert.IsNotNull(pads, name);
                AssertGoodLayout(region, pads, name);
                CollectionAssert.AreEqual(MemoryLayout.QuadrantCentres(region).ToArray(), pads.ToArray(), $"{name}: the default is the quadrant centres");
            }
        }

        [TestMethod]
        public void QuadrantLayout_IsTheSameForAnyRng_PadsStayPut()
        {
            var a = MemoryLayout.Plan(Rect(), false, new Random(1), Radius, Gap, Inset);
            var b = MemoryLayout.Plan(Rect(), false, new Random(999), Radius, Gap, Inset);

            CollectionAssert.AreEqual(a.ToArray(), b.ToArray());
        }

        // ---- refusals --------------------------------------------------------------------------------------------

        [TestMethod]
        public void ARegionTooSmallForFourPads_IsRefused_ByTheEdgeInset()
        {
            // an 8 m square: quarter centres are 2 m from the edge, a pad needs radius 1 + inset 2 = 3 m
            StringAssert.Contains(MemoryLayout.Feasible(Rect(8f), false, Radius, Gap, Inset), "too small");

            // the same region with no inset fits the pads (the gap is 4 m, so shrink pad_gap too)
            Assert.IsNull(MemoryLayout.Feasible(Rect(8f), false, Radius, 4f, 0f));
        }

        [TestMethod]
        public void PadGap_IsEnforced_BetweenPadCentres()
        {
            // a 20 m square puts the quarter centres 10 m apart
            Assert.IsNull(MemoryLayout.Feasible(Rect(20f), false, Radius, 10f, Inset));
            StringAssert.Contains(MemoryLayout.CheckPads(Rect(20f), MemoryLayout.QuadrantCentres(Rect(20f)), Radius, 10.5f, Inset), "pad_gap");
            Assert.IsNotNull(MemoryLayout.Feasible(Rect(20f), false, Radius, 10.5f, Inset));
            // random can use the corners of the 14 m usable square (14 m apart), but nothing gets 14.5 m apart
            Assert.IsNotNull(MemoryLayout.Plan(Rect(20f), true, new Random(3), Radius, 10.5f, Inset), "random packs the corners");
            Assert.IsNull(MemoryLayout.Plan(Rect(20f), true, new Random(3), Radius, 14.5f, Inset), "no layout beats the gap in a region this small");
            Assert.IsNotNull(MemoryLayout.Feasible(Rect(20f), true, Radius, 14.5f, Inset));
        }

        [TestMethod]
        public void AnExclusionOnAPad_RefusesTheQuadrantLayout_ButRandomWorksAround()
        {
            var region = Rect(40f, HazardExclusion.Circle(10, 10, 3));

            Assert.IsFalse(region.IsPlaceable(10, 10));
            StringAssert.Contains(MemoryLayout.Feasible(region, false, Radius, Gap, Inset), "does not fit");
            Assert.IsNull(MemoryLayout.Plan(region, false, new Random(1), Radius, Gap, Inset), "the default layout is refused, never silently moved");

            Assert.IsNull(MemoryLayout.Feasible(region, true, Radius, Gap, Inset));

            var pads = MemoryLayout.Plan(region, true, new Random(1), Radius, Gap, Inset);
            AssertGoodLayout(region, pads, "random around the exclusion");
            Assert.IsTrue(pads.All(p => Vector2.Distance(p, new Vector2(10, 10)) > 3f + 0.9f), "no pad touches the exclusion");
        }

        [TestMethod]
        public void AnExclusionClippingOnlyTheRim_AlsoRefusesThePad()
        {
            // the centre (10,10) is clear but the exclusion covers the rim point at (11, 10)
            var region = Rect(40f, HazardExclusion.Circle(11.5f, 10, 0.6f));

            Assert.IsTrue(region.IsPlaceable(10, 10));
            Assert.IsFalse(MemoryLayout.PadFits(region, new Vector2(10, 10), Radius, Inset));
        }

        // ---- random layout ---------------------------------------------------------------------------------------

        [TestMethod]
        public void RandomLayout_IsSeededReproducible_ValidEverywhere_AndVaries()
        {
            foreach (var (name, region) in new[] { ("rect", Rect()), ("circle", Circle()), ("adhoc", AdHocCircle()), ("outdoor", Outdoor()) })
            {
                var a = MemoryLayout.Plan(region, true, new Random(21), Radius, Gap, Inset);
                var b = MemoryLayout.Plan(region, true, new Random(21), Radius, Gap, Inset);
                var c = MemoryLayout.Plan(region, true, new Random(22), Radius, Gap, Inset);

                AssertGoodLayout(region, a, name + " seed 21");
                AssertGoodLayout(region, c, name + " seed 22");
                CollectionAssert.AreEqual(a.ToArray(), b.ToArray(), name + ": same seed, same pads");
                CollectionAssert.AreNotEqual(a.ToArray(), c.ToArray(), name + ": another seed, other pads");
                CollectionAssert.AreNotEqual(MemoryLayout.QuadrantCentres(region).ToArray(), a.ToArray(), name + ": random is not the quadrant layout");
            }
        }

        [TestMethod]
        public void RandomLayout_ManySeeds_AlwaysHonourTheGapAndTheInset()
        {
            var region = Circle(20f);

            for (var seed = 0; seed < 60; seed++)
                AssertGoodLayout(region, MemoryLayout.Plan(region, true, new Random(seed), Radius, Gap, Inset), $"seed {seed}");
        }

        [TestMethod]
        public void FirstFit_IsDeterministic_AndFindsPadsWhereTheQuadrantsFail()
        {
            var region = Rect(40f, HazardExclusion.Circle(10, 10, 3), HazardExclusion.Circle(30, 30, 3));

            var a = MemoryLayout.FirstFitPads(region, Radius, Gap, Inset);
            var b = MemoryLayout.FirstFitPads(region, Radius, Gap, Inset);

            AssertGoodLayout(region, a, "first fit");
            CollectionAssert.AreEqual(a.ToArray(), b.ToArray());
            Assert.IsNull(MemoryLayout.FirstFitPads(Rect(8f), Radius, Gap, Inset), "nothing fits an 8 m square at this inset");
        }

        // ---- the marker ring --------------------------------------------------------------------------------------

        [TestMethod]
        public void RingCount_IsCeilOfCircumferenceOverSpacing_ClampedTo8And32()
        {
            Assert.AreEqual(11, MemoryLayout.RingCount(1f, 0.6f), "ceil(2 pi / 0.6) = ceil(10.47)");
            Assert.AreEqual(13, MemoryLayout.RingCount(1f, 0.5f), "ceil(12.57)");
            Assert.AreEqual(8, MemoryLayout.RingCount(1f, 10f), "wide spacing still gets the minimum ring");
            Assert.AreEqual(32, MemoryLayout.RingCount(3f, 0.2f), "ceil(94.2) clamps to 32");
            Assert.AreEqual(8, MemoryLayout.RingCount(0f, 0.6f));
            Assert.AreEqual(8, MemoryLayout.RingCount(1f, 0f));
            Assert.AreEqual(8, MemoryLayout.RingCount(float.NaN, 0.6f));
        }

        [TestMethod]
        public void RingPoints_AreEvenlySpaced_OnTheRim_StartingAtPlusX()
        {
            var centre = new Vector2(30, 40);
            var points = MemoryLayout.RingPoints(centre, 2f, 12);

            Assert.AreEqual(12, points.Count);
            Assert.AreEqual(32f, points[0].X, 1e-4f);
            Assert.AreEqual(40f, points[0].Y, 1e-4f);

            for (var i = 0; i < 12; i++)
                Assert.AreEqual(2f, Vector2.Distance(points[i], centre), 1e-4f, $"marker {i} on the rim");

            var chord = Vector2.Distance(points[0], points[1]);

            for (var i = 0; i < 12; i++)
                Assert.AreEqual(chord, Vector2.Distance(points[i], points[(i + 1) % 12]), 1e-4f, "even spacing");

            Assert.AreEqual(30f, points[3].X, 1e-4f, "a quarter turn is straight up in y");
            Assert.AreEqual(42f, points[3].Y, 1e-4f);
        }

        // ---- the playback beams ------------------------------------------------------------------------------------

        [TestMethod]
        public void BeamPoints_AreInTheRegion_InsetFromTheEdge_AndNeverOnAPad()
        {
            var region = Circle(20f);
            var pads = MemoryLayout.QuadrantCentres(region);
            var rng = new Random(5);

            for (var i = 0; i < 300; i++)
            {
                Assert.IsTrue(MemoryLayout.TryBeamPoint(region, rng, pads, Radius, Inset, out var p));
                Assert.IsTrue(region.IsPlaceable(p.X, p.Y));
                Assert.IsTrue(region.DistanceToEdge(p.X, p.Y) >= Inset - 1e-3f);

                foreach (var pad in pads)
                    Assert.IsTrue(Vector2.Distance(p, pad) > Radius + MemoryLayout.BeamClearance, "outside every pad plus its clearance");
            }
        }

        [TestMethod]
        public void BeamPoints_AvoidExclusions()
        {
            var region = Rect(40f, HazardExclusion.Rect(0, 40, 0, 20));
            var pads = MemoryLayout.QuadrantCentres(region);
            var rng = new Random(9);

            for (var i = 0; i < 200; i++)
            {
                Assert.IsTrue(MemoryLayout.TryBeamPoint(region, rng, pads, Radius, Inset, out var p));
                Assert.IsTrue(p.Y > 20f, "the lower half is an exclusion");
            }
        }

        [TestMethod]
        public void BeamPoints_TheClearanceIsWhatKeepsThemOffThePads()
        {
            // a 4 m square with pads of radius 2 at its centre: the farthest corner is 2.83 m away. Without the 1 m
            // clearance the corners are free; with it the whole square is covered and no beam fits.
            var region = Rect(4f);
            var pads = new[] { new Vector2(2, 2), new Vector2(2, 2), new Vector2(2, 2), new Vector2(2, 2) };

            Assert.IsFalse(MemoryLayout.TryBeamPoint(region, new Random(1), pads, 2f, 0f, out _));
            Assert.IsTrue(MemoryLayout.TryBeamPoint(region, new Random(1), pads, 1.0f, 0f, out _), "a smaller pad leaves the corners");
        }

        [TestMethod]
        public void BeamPoints_AreSeededReproducible()
        {
            var region = Rect();
            var pads = MemoryLayout.QuadrantCentres(region);

            MemoryLayout.TryBeamPoint(region, new Random(77), pads, Radius, Inset, out var a);
            MemoryLayout.TryBeamPoint(region, new Random(77), pads, Radius, Inset, out var b);
            MemoryLayout.TryBeamPoint(region, new Random(78), pads, Radius, Inset, out var c);

            Assert.AreEqual(a, b);
            Assert.AreNotEqual(a, c);
        }

        // ---- review fixes ------------------------------------------------------------------------------------------

        [TestMethod]
        public void RandomLayout_FallsBackToTheQuadrants_WhenOnlyTheyFit()
        {
            // a 12.2 m square, gap 6.1: the usable square (rim + inset 3 m from the edge) is 6.2 m wide, so the
            // quadrant centres (6.1 m apart) fit exactly, random sampling essentially cannot hit that, and the 0.5 m
            // first-fit grid tops out at 6.0 m of spread. Validate said yes; Plan used to say no.
            var region = Rect(12.2f);

            Assert.IsNull(MemoryLayout.Feasible(region, true, Radius, 6.1f, Inset));
            Assert.IsNull(MemoryLayout.FirstFitPads(region, Radius, 6.1f, Inset), "first fit alone cannot do it, so the quadrant step is what saves it");

            var pads = MemoryLayout.Plan(region, true, new Random(1), Radius, 6.1f, Inset);

            Assert.IsNotNull(pads);
            Assert.IsNull(MemoryLayout.CheckPads(region, pads, Radius, 6.1f, Inset));
        }

        [TestMethod]
        public void WhatFeasibleAccepts_PlanAlwaysDelivers_AcrossRegionsSeedsAndGaps()
        {
            var regions = new List<HazardRegion>
            {
                Rect(12.2f), Rect(13f), Rect(20f), Rect(40f), Circle(8f), Circle(10f), Circle(20f), AdHocCircle(), AdHocRect(), Outdoor(),
                Rect(40f, HazardExclusion.Circle(10, 10, 3)),
            };

            var accepted = 0;

            foreach (var region in regions)
            {
                foreach (var gap in new[] { 4f, 6f, 6.1f, 8f, 10f, 12f })
                {
                    foreach (var random in new[] { false, true })
                    {
                        if (MemoryLayout.Feasible(region, random, Radius, gap, Inset) != null)
                            continue;

                        accepted++;

                        for (var seed = 0; seed < 8; seed++)
                        {
                            var pads = MemoryLayout.Plan(region, random, new Random(seed), Radius, gap, Inset);

                            Assert.IsNotNull(pads, $"{region.Describe()} gap={gap} random={random} seed={seed}: Validate accepted it but Plan found no pads");
                            Assert.IsNull(MemoryLayout.CheckPads(region, pads, Radius, gap, Inset));
                        }
                    }
                }
            }

            Assert.IsTrue(accepted > 20, "the property was exercised, not vacuous");
        }

        [TestMethod]
        public void PadFits_AsksTheWorldAboutTheCentreAndMostOfTheRim()
        {
            var region = Rect();
            var c = new Vector2(10, 10);

            Assert.IsTrue(MemoryLayout.PadFits(region, c, Radius, Inset, _ => true));
            Assert.IsFalse(MemoryLayout.PadFits(region, c, Radius, Inset, _ => false), "no cell anywhere");
            Assert.IsFalse(MemoryLayout.PadFits(region, c, Radius, Inset, p => p != c), "no cell under the centre");
            Assert.IsFalse(MemoryLayout.PadFits(region, c, Radius, Inset, p => p == c || p.X > 10.001f), "under half the rim has a cell");
            Assert.IsTrue(MemoryLayout.PadFits(region, c, Radius, Inset, p => p.X > 9.999f), "half the rim and the centre are enough");
        }

        [TestMethod]
        public void Plan_RefusesPadsTheWorldHasNoCellFor_NotJustTheRegion()
        {
            var region = Rect();
            var quadrants = MemoryLayout.QuadrantCentres(region);

            // no cell within 3 m of the first quadrant pad
            Func<Vector2, bool> cells = p => Vector2.Distance(p, quadrants[0]) > 3f;

            Assert.IsNull(MemoryLayout.Plan(region, false, new Random(1), Radius, Gap, Inset, cells), "the default layout is refused");

            var random = MemoryLayout.Plan(region, true, new Random(1), Radius, Gap, Inset, cells);

            Assert.IsNotNull(random);
            Assert.IsTrue(random.All(p => Vector2.Distance(p, quadrants[0]) > 3f - Radius), "random pads avoid the cell-less spot");
            Assert.IsNotNull(MemoryLayout.Plan(region, false, new Random(1), Radius, Gap, Inset, null), "without the predicate the quadrants fit");
        }

        [TestMethod]
        public void PickBeamPoint_OnlyReturnsPointsTheWorldAccepts_InAtMostEightTries()
        {
            var region = Rect();
            var pads = MemoryLayout.QuadrantCentres(region);
            var rng = new Random(3);

            for (var i = 0; i < 100; i++)
            {
                Assert.IsTrue(MemoryLayout.PickBeamPoint(region, rng, pads, Radius, Inset, p => p.X > 20f, out var p2, 64));
                Assert.IsTrue(p2.X > 20f);
            }

            var asked = 0;

            Assert.IsFalse(MemoryLayout.PickBeamPoint(region, rng, pads, Radius, Inset, p => { asked++; return false; }, out _));
            Assert.AreEqual(8, asked, "up to 8 candidates per beam, no more");

            asked = 0;
            Assert.IsTrue(MemoryLayout.PickBeamPoint(region, rng, pads, Radius, Inset, p => { asked++; return true; }, out _));
            Assert.AreEqual(1, asked, "the first accepted candidate wins");
        }

        [TestMethod]
        public void HasBeamRoom_IsFalseForARegionWhoseOnlyPlaceableGroundIsThePads()
        {
            // the reviewer's pocket region: everything is an exclusion except a small square round each quadrant pad
            const float a = 8.9f, b = 11.1f, c = 28.9f, d = 31.1f;

            var pocket = Rect(40f,
                HazardExclusion.Rect(0, 40, 0, a), HazardExclusion.Rect(0, 40, b, c), HazardExclusion.Rect(0, 40, d, 40),
                HazardExclusion.Rect(0, a, a, b), HazardExclusion.Rect(b, c, a, b), HazardExclusion.Rect(d, 40, a, b),
                HazardExclusion.Rect(0, a, c, d), HazardExclusion.Rect(b, c, c, d), HazardExclusion.Rect(d, 40, c, d));

            var pads = MemoryLayout.QuadrantCentres(pocket);

            Assert.IsNull(MemoryLayout.CheckPads(pocket, pads, Radius, Gap, Inset), "the four pads themselves fit in their pockets");
            Assert.IsFalse(MemoryLayout.HasBeamRoom(pocket, pads, Radius, Inset));

            var error = MemoryLayout.Feasible(pocket, false, Radius, Gap, Inset);
            StringAssert.Contains(error, "no room for the playback beams");

            Assert.IsTrue(MemoryLayout.HasBeamRoom(Rect(), MemoryLayout.QuadrantCentres(Rect()), Radius, Inset));
            Assert.IsNull(MemoryLayout.Feasible(Rect(), false, Radius, Gap, Inset));
        }

        [TestMethod]
        public void HasBeamRoom_HonoursTheEdgeInsetAndTheClearanceFromPads()
        {
            // a 4 m square, pad radius 2 in the middle: covered by the 3 m clearance
            var small = Rect(4f);
            var pads = new[] { new Vector2(2, 2), new Vector2(2, 2), new Vector2(2, 2), new Vector2(2, 2) };

            Assert.IsFalse(MemoryLayout.HasBeamRoom(small, pads, 2f, 0f));
            Assert.IsTrue(MemoryLayout.HasBeamRoom(small, pads, 1f, 0f));
            Assert.IsFalse(MemoryLayout.HasBeamRoom(small, pads, 1f, 2.5f), "the inset leaves no placeable point");
        }
    }
}
