using System;
using System.Linq;
using System.Numerics;

using ACE.Server.HazardMechanics;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.HazardMechanics
{
    [TestClass]
    public class HazardRegionTests
    {
        private static HazardRegion DungeonRect(params HazardExclusion[] exclusions) => HazardRegion.Rect("t", 0x0069, 0, false,
            0f, 20f, -10f, 10f, HazardGroundMode.Plane, -5f, null, 1f, null, 2f, 6f, exclusions);

        private static HazardRegion OutdoorCircle(float cx, float cy, float r) => HazardRegion.Circle("t", 0xA9B4, 0, true,
            cx, cy, r, HazardGroundMode.Terrain, 0f, null, 0f, null, 2f, 6f);

        [TestMethod]
        public void Rect_Containment_IsEdgeInclusive()
        {
            var r = DungeonRect();

            Assert.IsTrue(r.Contains(0f, -10f));
            Assert.IsTrue(r.Contains(20f, 10f));
            Assert.IsTrue(r.Contains(10f, 0f));
            Assert.IsFalse(r.Contains(-0.01f, 0f));
            Assert.IsFalse(r.Contains(10f, 10.01f));
        }

        [TestMethod]
        public void Circle_Containment()
        {
            var c = OutdoorCircle(100f, 100f, 10f);

            Assert.IsTrue(c.Contains(100f, 100f));
            Assert.IsTrue(c.Contains(110f, 100f));
            Assert.IsFalse(c.Contains(107.1f, 107.1f)); // sqrt(2) * 7.1 > 10
            Assert.IsTrue(c.Contains(107f, 107f));
        }

        [TestMethod]
        public void Exclusions_BlockPlacement_ButNeverExemptAPlayer()
        {
            var r = DungeonRect(HazardExclusion.Circle(10f, 0f, 3f), HazardExclusion.Rect(15f, 18f, 5f, 8f));

            Assert.IsTrue(r.Contains(10f, 0f));
            Assert.IsFalse(r.IsPlaceable(10f, 0f), "inside the circle exclusion");
            Assert.IsFalse(r.IsPlaceable(16f, 6f), "inside the rect exclusion");
            Assert.IsTrue(r.IsPlaceable(2f, 2f));

            // no safe spots: a player standing in an exclusion is still in the region
            Assert.IsTrue(r.ContainsPlayer(new Vector3(10f, 0f, -5f)));
        }

        [TestMethod]
        public void TrySamplePoint_NeverReturnsAnExcludedPoint()
        {
            var r = DungeonRect(HazardExclusion.Rect(0f, 15f, -10f, 10f));
            var rng = new Random(7);

            for (var i = 0; i < 200; i++)
            {
                Assert.IsTrue(r.TrySamplePoint(rng, out var p));
                Assert.IsTrue(p.X > 15f && r.Contains(p.X, p.Y), $"sample {p} is excluded or outside");
            }
        }

        [TestMethod]
        public void OutdoorRegion_LeavingItsLandblock_IsRefusedNotClipped()
        {
            Assert.IsNull(OutdoorCircle(96f, 96f, 20f).Validate());

            StringAssert.Contains(OutdoorCircle(185f, 96f, 10f).Validate(), "leaves landblock");
            StringAssert.Contains(OutdoorCircle(5f, 96f, 10f).Validate(), "leaves landblock");

            // a dungeon has no 0..192 bound: negative local coordinates are normal there
            var dungeon = HazardRegion.Rect("d", 0x0069, 0, false, -4.6f, 34.6f, -34.6f, -5.4f, HazardGroundMode.Plane, -47.75f, null, 0f, null, 1f, 1f);
            Assert.IsNull(dungeon.Validate());
        }

        [TestMethod]
        public void Validate_RefusesTerrainInADungeon_ZeroSize_AndALowCeiling()
        {
            StringAssert.Contains(HazardRegion.Rect("d", 1, 0, false, 0, 10, 0, 10, HazardGroundMode.Terrain, 0, null, 0, null, 1, 1).Validate(), "terrain");
            StringAssert.Contains(HazardRegion.Rect("d", 1, 0, false, 0, 0, 0, 10, HazardGroundMode.Plane, 0, null, 0, null, 1, 1).Validate(), "width");
            StringAssert.Contains(HazardRegion.Circle("d", 1, 0, false, 0, 0, 0, HazardGroundMode.Plane, 0, null, 0, null, 1, 1).Validate(), "radius");
            StringAssert.Contains(HazardRegion.Rect("d", 1, 0, false, 0, 10, 0, 10, HazardGroundMode.Plane, 0, null, 0, -1f, 1, 1).Validate(), "ceiling");
        }

        [TestMethod]
        public void AdHoc_FromCallerPosition_Dungeon_IsAPlaneAtTheCallersZ()
        {
            var caller = new Vector3(12f, -20f, -47.75f);
            var r = HazardRegion.AdHoc(0x0069, 0x80010001, false, caller, HazardShapeSpec.Rect(10f, 6f), 2f, out var error);

            Assert.IsNull(error);
            Assert.AreEqual(HazardShape.Rect, r.Shape);
            Assert.AreEqual(HazardGroundMode.Plane, r.GroundMode);
            Assert.AreEqual(7f, r.MinX, 1e-4);
            Assert.AreEqual(17f, r.MaxX, 1e-4);
            Assert.AreEqual(-23f, r.MinY, 1e-4);
            Assert.AreEqual(-17f, r.MaxY, 1e-4);
            Assert.AreEqual(-47.75f, r.GroundZ(0f, 0f), 1e-4);
            Assert.AreEqual(-45.75f, r.HazardZ(12f, -20f), 1e-4, "hazard height = ground + hz");
            Assert.AreEqual(0x80010001u, r.Instance);
            Assert.IsNull(r.Validate());
        }

        [TestMethod]
        public void AdHoc_FromCallerPosition_Outdoors_IsTerrainAndRefusedNearTheEdge()
        {
            var r = HazardRegion.AdHoc(0xA9B4, 0, true, new Vector3(50f, 60f, 10f), HazardShapeSpec.Circle(15f), 0f, out _);

            Assert.AreEqual(HazardGroundMode.Terrain, r.GroundMode);
            Assert.AreEqual(50f, r.CenterX, 1e-4);
            Assert.AreEqual(15f, r.Radius, 1e-4);
            Assert.IsNull(r.Validate());

            var edge = HazardRegion.AdHoc(0xA9B4, 0, true, new Vector3(10f, 60f, 10f), HazardShapeSpec.Circle(15f), 0f, out _);
            Assert.IsNotNull(edge.Validate(), "a 15 m circle 10 m from the edge overflows the landblock");
        }

        [TestMethod]
        public void ShapeSpec_Parse()
        {
            Assert.IsTrue(HazardShapeSpec.TryParse("circle:12", out var c, out _));
            Assert.AreEqual(HazardShape.Circle, c.Shape);
            Assert.AreEqual(12f, c.Radius);

            Assert.IsTrue(HazardShapeSpec.TryParse("rect:20x30", out var r, out _));
            Assert.AreEqual(20f, r.Width);
            Assert.AreEqual(30f, r.Height);

            Assert.IsFalse(HazardShapeSpec.TryParse("circle:0", out _, out _));
            Assert.IsFalse(HazardShapeSpec.TryParse("rect:20", out _, out _));
            Assert.IsFalse(HazardShapeSpec.TryParse("square:5", out _, out _));
            Assert.IsFalse(HazardShapeSpec.TryParse("circle:500", out _, out _));
        }

        [TestMethod]
        public void EdgePoint_LiesOnTheBoundary_WithAnInwardNormal()
        {
            foreach (var region in new[] { DungeonRect(), OutdoorCircle(96f, 96f, 20f) })
            {
                for (var i = 0; i < 16; i++)
                {
                    // half-step offsets keep off the rect corners, where the inward step would touch another side
                    var t = (i + 0.5) / 16.0;
                    var p = region.EdgePoint(t, out var n);

                    Assert.AreEqual(0f, region.DistanceToEdge(p.X, p.Y), 1e-3, $"{region.Shape} t={t} not on the edge");

                    var inward = p + n * 0.5f;
                    Assert.IsTrue(region.DistanceToEdge(inward.X, inward.Y) > 0f, $"{region.Shape} normal at {p} points out");
                }
            }
        }

        [TestMethod]
        public void PinnaclePreset_IsValid_AndOnLandblock0069()
        {
            Assert.IsTrue(HazardRegionPresets.TryGet("PINNACLE", 0x80010005, out var r));
            Assert.AreEqual((ushort)0x0069, r.Landblock);
            Assert.AreEqual(0x80010005u, r.Instance);
            Assert.IsFalse(r.Outdoor);
            // owner ruling 2026-10-18: wisps glide just above the floor (ground -47.75 + 0.5), no longer in the pit at -51
            Assert.IsNull(r.HazardPlaneZ);
            Assert.AreEqual(-47.25f, r.HazardZ(15f, -20f), 1e-4);
            Assert.AreEqual(-47.25f, r.HazardZ(30f, -10f), 1e-4, "the same height over a block and over the plated pit");
            Assert.AreEqual(-42.05f, r.CeilingZ.Value, 1e-4);
            Assert.IsNull(r.Validate());

            // no safe spots: the band reaches the pit floor (-54.00) and the block tops (-48.00)
            Assert.IsTrue(r.InBand(new Vector3(0f, -30f, -54.0f)));
            Assert.IsTrue(r.InBand(new Vector3(30f, -20f, -48.0f)));

            Assert.AreEqual(HazardRegionPresets.PinnacleName, HazardRegionPresets.ForLandblock(0x0069));
            Assert.IsNull(HazardRegionPresets.ForLandblock(0x0070));
        }

        // ---- edge inset (wall clearance) ------------------------------------------------------------------------

        [TestMethod]
        public void ClampInside_Rect_PullsCornersAndOutsidePointsInside_AndLeavesInnerPointsAlone()
        {
            var r = DungeonRect(); // x 0..20, y -10..10

            Assert.AreEqual(new Vector2(2f, -8f), r.ClampInside(new Vector2(0f, -10f), 2f), "corner");
            Assert.AreEqual(new Vector2(18f, 8f), r.ClampInside(new Vector2(50f, 50f), 2f), "outside");
            Assert.AreEqual(new Vector2(10f, 3f), r.ClampInside(new Vector2(10f, 3f), 2f), "already inside");
            Assert.AreEqual(new Vector2(0f, -10f), r.ClampInside(new Vector2(0f, -10f), 0f), "zero inset is a no-op");
            Assert.AreEqual(new Vector2(0f, 0f), r.ClampInside(new Vector2(-1f, 0f), -3f), "a negative inset counts as 0 but still clamps inside");
        }

        [TestMethod]
        public void ClampInside_NarrowRect_UsesTheCentreLine_AndNonFiniteGoesToTheCentre()
        {
            var narrow = HazardRegion.Rect("n", 0x0069, 0, false, 0f, 3f, 0f, 40f, HazardGroundMode.Plane, 0f, null, 0f, null, 1f, 1f);

            Assert.AreEqual(new Vector2(1.5f, 2f), narrow.ClampInside(new Vector2(0f, 0f), 2f), "3 m wide < 2 x 2 m: x falls to the centre line");
            Assert.AreEqual(narrow.Center, narrow.ClampInside(new Vector2(float.NaN, 5f), 2f));
            Assert.AreEqual(narrow.Center, narrow.ClampInside(new Vector2(1f, float.PositiveInfinity), 2f));
        }

        [TestMethod]
        public void ClampInside_Circle_PullsOntoTheInsetRim()
        {
            var c = OutdoorCircle(100f, 100f, 10f);

            var p = c.ClampInside(new Vector2(110f, 100f), 2f);
            Assert.AreEqual(108f, p.X, 1e-4);
            Assert.AreEqual(100f, p.Y, 1e-4);
            Assert.AreEqual(2f, c.DistanceToEdge(p.X, p.Y), 1e-4);

            Assert.AreEqual(new Vector2(103f, 104f), c.ClampInside(new Vector2(103f, 104f), 2f), "5 m from the centre is inside 8");
            Assert.AreEqual(c.Center, c.ClampInside(new Vector2(130f, 100f), 12f), "inset >= radius: the centre");
        }

        [TestMethod]
        public void TrySamplePoint_WithEdgeInset_NeverReturnsAPointNearTheEdge()
        {
            var r = DungeonRect();
            var rng = new Random(11);
            var nearEdgeWithoutInset = 0;

            for (var i = 0; i < 300; i++)
            {
                Assert.IsTrue(r.TrySamplePoint(rng, out var p, edgeInset: 3f));
                Assert.IsTrue(r.DistanceToEdge(p.X, p.Y) >= 3f, $"{p} is within 3 m of the edge");
            }

            // control: without the inset, points within 3 m of the edge do come back (the inset is what removes them)
            var control = new Random(11);

            for (var i = 0; i < 300; i++)
                if (r.TrySamplePoint(control, out var p) && r.DistanceToEdge(p.X, p.Y) < 3f)
                    nearEdgeWithoutInset++;

            Assert.IsTrue(nearEdgeWithoutInset > 0);
        }

        [TestMethod]
        public void ShowOutline_PutsEdgeMarkersTheInsetInsideTheEdge()
        {
            var rect = DungeonRect();
            var outline = HazardToolRoutes.Outline(rect, 2f);

            Assert.AreEqual(5, outline.Count, "4 corners + centre");
            CollectionAssert.AreEqual(new[] { new Vector2(2f, -8f), new Vector2(18f, -8f), new Vector2(18f, 8f), new Vector2(2f, 8f), rect.Center }, outline.ToArray());

            var circle = OutdoorCircle(96f, 96f, 20f);
            var rim = HazardToolRoutes.Outline(circle, 2f);

            Assert.AreEqual(9, rim.Count);
            for (var i = 0; i < 8; i++)
                Assert.AreEqual(2f, circle.DistanceToEdge(rim[i].X, rim[i].Y), 1e-3, $"rim marker {i}");

            Assert.AreEqual(0f, rect.DistanceToEdge(HazardToolRoutes.Outline(rect)[0].X, HazardToolRoutes.Outline(rect)[0].Y), 1e-4, "inset 0 keeps the raw corner");
        }

        // ---- /hazard move v2 planner ---------------------------------------------------------------------------

        /// <summary>The Pinnacle at the wisp plane, from the 2026-10-06 recon: the two blocks are solid, the bridge hollow is open, outside the room is solid.</summary>
        private static bool PinnacleSolid(Vector2 p)
            => (p.X >= 5f && p.X <= 15f && p.Y >= -25f && p.Y <= -15f)
            || (p.X >= 25f && p.X <= 35f && p.Y >= -25f && p.Y <= -15f)
            || p.X < -4.75f || p.X > 34.75f || p.Y < -34.75f || p.Y > -5.25f;

        [TestMethod]
        public void MoveGate_OnPinnacle_OpenLineRunsThroughTheSouthPit_ClearOfTheBlocks()
        {
            HazardRegionPresets.TryGet("pinnacle", 0, out var r);

            var open = HazardMoveGate.FindOpenSegment(r, 2f, PinnacleSolid);

            Assert.IsNotNull(open);
            Assert.AreEqual(new Vector2(5f, -30f), open.From);
            Assert.AreEqual(new Vector2(25f, -30f), open.To);
            Assert.IsFalse(open.Crossing);

            for (var t = 0f; t <= 1f; t += 0.05f)
            {
                var p = Vector2.Lerp(open.From, open.To, t);
                Assert.IsFalse(PinnacleSolid(p) || PinnacleSolid(p + new Vector2(0f, 2.5f)) || PinnacleSolid(p - new Vector2(0f, 2.5f)), $"{p} is not 2.5 m clear");
            }
        }

        [TestMethod]
        public void MoveGate_OnPinnacle_CrossingLineGoesThroughTheWestBlockOnTheCentreLine()
        {
            HazardRegionPresets.TryGet("pinnacle", 0, out var r);

            var cross = HazardMoveGate.FindCrossingSegment(r, 2f, PinnacleSolid);

            Assert.IsNotNull(cross);
            Assert.AreEqual(new Vector2(1.5f, -20f), cross.From);
            Assert.AreEqual(new Vector2(18.5f, -20f), cross.To);
            Assert.AreEqual(new Vector2(4.75f, -20f), cross.SolidEnter.Value);
            Assert.AreEqual(new Vector2(15.25f, -20f), cross.SolidExit.Value);
            Assert.IsFalse(PinnacleSolid(cross.From) || PinnacleSolid(cross.To), "both ends must be open (a moving body is never raised)");
        }

        [TestMethod]
        public void MoveGate_Plan_HasFiveCases_AndSkipsCrossingCasesWhenNothingIsSolid()
        {
            HazardRegionPresets.TryGet("pinnacle", 0, out var r);

            var plan = HazardMoveGate.Plan(r, 2f, PinnacleSolid);

            Assert.AreEqual(HazardMoveGate.CaseCount, plan.Count);
            CollectionAssert.AreEqual(new[] { 1, 2, 3, 4, 5 }, plan.Select(c => c.Number).ToArray());
            CollectionAssert.AreEqual(new[] { "wisp", "ball", "ball", "wisp", "ball" }, plan.Select(c => c.Body).ToArray());
            Assert.IsTrue(plan.All(c => c.Skip == null));
            Assert.AreEqual(2, plan[4].LegCount, "case 5 is out and back");
            Assert.AreEqual(plan[4].Points[0], plan[4].Points[2]);

            // case 3 crosses the solid; case 4 aims a wisp at the solid's MIDDLE (it must stop at the edge live)
            CollectionAssert.AreEqual(new[] { new Vector2(1.5f, -20f), new Vector2(18.5f, -20f) }, plan[2].Points.ToArray());
            CollectionAssert.AreEqual(new[] { new Vector2(1.5f, -20f), new Vector2(10f, -20f) }, plan[3].Points.ToArray());
            Assert.IsTrue(PinnacleSolid(plan[3].Points[1]));
            Assert.IsTrue(plan.SelectMany(c => c.Points).All(p => r.DistanceToEdge(p.X, p.Y) >= 2f), "every point keeps the edge inset");

            // discriminating control: nothing solid -> the two crossing cases are skipped, the open ones still run
            var flat = HazardMoveGate.Plan(r, 2f, p => false);

            Assert.AreEqual(HazardMoveGate.NoCrossingLine, flat[2].Skip);
            Assert.AreEqual(HazardMoveGate.NoCrossingLine, flat[3].Skip);
            Assert.IsNull(flat[0].Skip);
            Assert.AreEqual(0, flat[2].LegCount);

            // everything solid -> nothing to run
            Assert.IsTrue(HazardMoveGate.Plan(r, 2f, p => true).All(c => c.Skip != null));
        }

        [TestMethod]
        public void MoveGate_Announce_SaysBodyDirectionPointsSpeedAndExpectation()
        {
            HazardRegionPresets.TryGet("pinnacle", 0, out var r);

            var c2 = HazardMoveGate.Plan(r, 2f, PinnacleSolid)[1];
            var line = HazardMoveGate.Announce(c2, new[] { new Vector3(5f, -30f, -47.75f), new Vector3(25f, -30f, -47.75f) }, 4f, new[] { 5.0 });

            StringAssert.StartsWith(line, "Case 2/5 - BALL rolls EAST");
            StringAssert.Contains(line, "from (5.0, -30.0) to (25.0, -30.0) at z -47.75, 4.0 m/s, 5.0 s");
            StringAssert.Contains(line, "EXPECT");
            Assert.IsFalse(line.Any(ch => ch > 127), "ASCII only");

            Assert.AreEqual("WEST", HazardMoveGate.Heading(new Vector2(5f, 0f), new Vector2(1f, 1f)));
            Assert.AreEqual("NORTH", HazardMoveGate.Heading(new Vector2(0f, 0f), new Vector2(1f, 3f)));
            Assert.AreEqual(5.0, HazardMoveTestMechanic.LegSeconds(new Vector2(5f, -30f), new Vector2(25f, -30f), 4f), 1e-9);
            Assert.AreEqual(0.5, HazardMoveTestMechanic.LegSeconds(Vector2.Zero, Vector2.Zero, 4f), 1e-9, "never under 0.5 s");
        }

        [TestMethod]
        public void MoveGate_Grid_IsMultiplesOfTheStep()
        {
            CollectionAssert.AreEqual(new[] { -2.5f, -2f, -1.5f }, HazardMoveGate.Grid(-2.6f, -1.4f, 0.5f).ToArray());
            Assert.AreEqual(0, HazardMoveGate.Grid(1f, 0f, 0.5f).Count);
            Assert.AreEqual(0, HazardMoveGate.Grid(0f, 1f, 0f).Count);
        }
    }
}
