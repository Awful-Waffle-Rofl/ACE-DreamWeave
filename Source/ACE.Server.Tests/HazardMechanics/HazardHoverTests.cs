using System;
using System.Linq;
using System.Numerics;

using ACE.Server.HazardMechanics;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.HazardMechanics
{
    /// <summary>
    /// The two 2026-10-07 owner rulings after gate 2 (nothing moves through a solid): BALLS HOVER (a Ground leg's
    /// end, sub-leg ends and spawn point sit GroundOffset above GroundZ on plane AND terrain regions) and WISPS
    /// NEVER ENTER SOLIDS (HazardGround.PlaceWisp).
    /// </summary>
    [TestClass]
    public class HazardHoverTests
    {
        private static HazardRegion Pinnacle()
        {
            HazardRegionPresets.TryGet("pinnacle", 0, out var r);
            return r;
        }

        private static HazardRegion TerrainCircle() => HazardRegion.Circle("t", 0xA9B4, 0, true,
            96f, 96f, 40f, HazardGroundMode.Terrain, 0f, null, 2f, null, 2f, 6f);

        private static float Slope(float x, float y) => 10f + 0.1f * x;

        private static HazardMoveOptions Ball(float hover) => new HazardMoveOptions { Follow = HazardFollow.Ground, GroundOffset = hover, RollRadius = 1.8f };

        // ---- balls hover ---------------------------------------------------------------------------------------

        [TestMethod]
        public void PlaneRegion_GroundLeg_EndsHoverAboveTheGround_WhateverZTheCallerPassed()
        {
            var r = Pinnacle(); // ground plane -47.75

            // the gate-2 bug: the caller passes the ground z; the offset must still reach the leg end
            var legs = HazardContext.PlanLeg(r, null, new Vector3(1.5f, -20f, -45.95f), new Vector3(18.5f, -20f, -47.75f), 0.0, 4.25, Ball(1.8f));

            Assert.AreEqual(1, legs.Count, "a plane leg is one straight piece");
            Assert.AreEqual(-45.95f, legs[0].End.Z, 1e-4, "end = ground -47.75 + hover 1.8");
            Assert.AreEqual(18.5f, legs[0].End.X, 1e-4);
            Assert.AreEqual(0f, legs[0].Velocity.Z, 1e-4, "a hovering ball rolls level");

            // ball_z=-5 (the owner's other retest) must reach the end too
            var low = HazardContext.PlanLeg(r, null, new Vector3(1.5f, -20f, -52.75f), new Vector3(18.5f, -20f, -47.75f), 0.0, 4.25, Ball(-5f));
            Assert.AreEqual(-52.75f, low[0].End.Z, 1e-4);
        }

        [TestMethod]
        public void PlaneRegion_SpawnAndMarkerPoint_HoversByTheSameRule()
        {
            var r = Pinnacle();

            var spawn = HazardGround.FollowPoint(r, HazardFollow.Ground, 1.8f, new Vector3(1.5f, -20f, -47.75f), null);
            Assert.AreEqual(new Vector3(1.5f, -20f, -45.95f), spawn);

            // the spawn point and the leg end agree, so the first leg does not climb or sink
            var legs = HazardContext.PlanLeg(r, null, spawn, new Vector3(18.5f, -20f, 0f), 0.0, 4.0, Ball(1.8f));
            Assert.AreEqual(spawn.Z, legs[0].End.Z, 1e-4);

            Assert.AreEqual(-47.25f, HazardGround.FollowZ(r, HazardFollow.HazardHeight, 1.8f, 0f, -30f, 99f, null), 1e-4, "a wisp flies at the region's hazard height (ground + its own offset 0.5); the 1.8 offset is a ball's");
            Assert.AreEqual(99f, HazardGround.FollowZ(r, HazardFollow.Straight, 1.8f, 0f, -30f, 99f, null), "Straight keeps the caller's z");
        }

        [TestMethod]
        public void PlaneRegion_NonGroundLegs_KeepTheCallersEnd_AndAreNotSplit()
        {
            var r = Pinnacle();
            var end = new Vector3(18.5f, -20f, -49.0f);

            foreach (var follow in new[] { HazardFollow.HazardHeight, HazardFollow.Straight })
            {
                var legs = HazardContext.PlanLeg(r, null, new Vector3(1.5f, -20f, -49.0f), end, 0.0, 4.0, new HazardMoveOptions { Follow = follow });

                Assert.AreEqual(1, legs.Count, follow.ToString());
                Assert.AreEqual(end, legs[0].End, follow.ToString());
            }

            Assert.IsFalse(HazardGround.SplitsLegs(r, HazardFollow.Ground));
            Assert.IsNull(HazardGround.HeightAt(r, HazardFollow.Straight, 0f, null));
        }

        [TestMethod]
        public void TerrainRegion_GroundLeg_EverySubLegEndHoversAboveTheTerrain()
        {
            var r = TerrainCircle();
            var start = new Vector3(70f, 96f, Slope(70f, 96f) + 1.5f);

            var legs = HazardContext.PlanLeg(r, Slope, start, new Vector3(122f, 96f, 0f), 0.0, 10.0, Ball(1.5f));

            Assert.IsTrue(legs.Count > 1, "terrain legs follow the ground in sub-legs");
            foreach (var leg in legs)
                Assert.AreEqual(Slope(leg.End.X, leg.End.Y) + 1.5f, leg.End.Z, 1e-3, $"sub-leg end {leg.End}");
        }

        // ---- wisps never enter solids --------------------------------------------------------------------------

        /// <summary>The Pinnacle west block (x 5..15, y -25..-15, top -48.00, recon 2026-10-06) as a 3D cell probe.</summary>
        private static bool HasCell(Vector3 p) => !(p.X >= 5f && p.X <= 15f && p.Y >= -25f && p.Y <= -15f && p.Z < -48f);

        [TestMethod]
        public void PlaceWisp_TargetInsideASolid_StandsAtTheEdge_PillarOnTheSolidsTop()
        {
            var placed = HazardGround.PlaceWisp(new Vector3(1.5f, -20f, -51f), new Vector3(10f, -20f, -51f), HasCell, clearance: 0.5f);

            Assert.IsTrue(placed.Blocked);
            Assert.AreEqual(4.25f, placed.Stand.X, 1e-3, "last open sample 4.75 (x 5 is solid), backed off 0.5");
            Assert.AreEqual(-20f, placed.Stand.Y, 1e-4);
            Assert.AreEqual(-51f, placed.Stand.Z, 1e-4, "the body stays at the hazard plane");
            Assert.IsTrue(HasCell(placed.Stand));

            Assert.IsTrue(placed.PillarFound);
            Assert.AreEqual(new Vector3(10f, -20f, -48f), placed.Pillar, "true target (x, y), raised 3 m onto the block top - no safe spots");
        }

        [TestMethod]
        public void PlaceWisp_OpenTargetBehindASolid_IsStillBlocked_AndAnOpenPathIsNot()
        {
            // the bridge hollow at x 18.5 is open, but the west block lies between: a wisp may not pass through it
            var behind = HazardGround.PlaceWisp(new Vector3(1.5f, -20f, -51f), new Vector3(18.5f, -20f, -51f), HasCell, clearance: 0.5f);

            Assert.IsTrue(behind.Blocked);
            Assert.AreEqual(4.25f, behind.Stand.X, 1e-3);
            Assert.AreEqual(new Vector3(18.5f, -20f, -51f), behind.Pillar, "an open target fires where it is");

            // control: an open line south of the blocks is not blocked and the wisp reaches its target
            var clear = HazardGround.PlaceWisp(new Vector3(5f, -30f, -51f), new Vector3(25f, -30f, -51f), HasCell);

            Assert.IsFalse(clear.Blocked);
            Assert.AreEqual(new Vector3(25f, -30f, -51f), clear.Stand);
            Assert.AreEqual(clear.Stand, clear.Pillar);
        }

        [TestMethod]
        public void PlaceWisp_EdgeCases()
        {
            // starting right at the solid: stays where it is, never steps in
            var atEdge = HazardGround.PlaceWisp(new Vector3(4.9f, -20f, -51f), new Vector3(10f, -20f, -51f), HasCell, clearance: 0.5f);
            Assert.IsTrue(atEdge.Blocked);
            Assert.AreEqual(4.9f, atEdge.Stand.X, 1e-4, "clearance never backs it past its start");

            // no open z within maxRise: pillar falls back to the target, flagged
            var none = HazardGround.PlaceWisp(new Vector3(1.5f, -20f, -51f), new Vector3(10f, -20f, -51f), HasCell, maxRise: 1f);
            Assert.IsFalse(none.PillarFound);
            Assert.AreEqual(new Vector3(10f, -20f, -51f), none.Pillar);

            // a zero-length move onto an open point
            var stay = HazardGround.PlaceWisp(new Vector3(0f, -30f, -51f), new Vector3(0f, -30f, -51f), HasCell);
            Assert.IsFalse(stay.Blocked);
            Assert.AreEqual(new Vector3(0f, -30f, -51f), stay.Stand);
        }
    }
}
