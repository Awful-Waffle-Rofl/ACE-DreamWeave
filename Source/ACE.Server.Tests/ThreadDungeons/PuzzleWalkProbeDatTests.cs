using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;

using ACE.DatLoader;
using ACE.Server.Physics;
using ACE.Server.Physics.Util;
using ACE.Server.PuzzleGates;
using ACE.Server.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// PuzzleWalkProbe against REAL dungeon geometry (Filos Doom, landblock 0x0150, from client_cell_1.dat): the body
    /// walks the server's own Transition with no player, session or world object. Same dat bootstrap as
    /// ExactPlacementTests: PhysicsEngine.Server is false so LScape builds the landblock from the cell dat, and the cell
    /// dat is swapped into DatManager only for each test.
    /// </summary>
    [TestClass]
    public class PuzzleWalkProbeDatTests
    {
        private const uint Landblock = 0x0150;

        // puzzle-gates.json filos_doom s1: anchor, and its first shuffle spot (both curated standing points).
        private const uint AnchorCell = 0x01500199;
        private static readonly Vector3 Anchor = new Vector3(40f, -18f, 0f);
        private const uint SpotCell = 0x0150018B;
        private static readonly Vector3 Spot = new Vector3(34.226f, -12.463f, 0f);

        // (34, -18) lies 6 m west of the anchor, in cell 0x0150018C (AdjustCell resolves it): a real floor on the far
        // side of the corridor's west wall, which stands at x = 35.7 for the body (observed when this test was written).
        private const uint BehindWallCell = 0x0150018C;
        private static readonly Vector3 BehindWall = new Vector3(34f, -18f, 0f);

        /// <summary>Control: the curated shuffle spot is walkable from the anchor and back, straight.</summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void The_body_walks_from_the_anchor_to_a_curated_spot_and_back()
        {
            RequireDats();

            using var probe = new PuzzleWalkProbe(0);
            Assert.IsTrue(probe.Ready, "the human Setup loaded");
            Assert.IsTrue(probe.StepUpHeight > 0f, "the body has the human step-up height");

            var outLeg = probe.Walk("spot", "out", AnchorCell, Anchor, Spot, null, null);
            Assert.AreEqual(ProbeOutcome.Pass, outLeg.Outcome, outLeg.Describe());

            var backLeg = probe.Walk("spot", "back", SpotCell, Spot, Anchor, null, null);
            Assert.AreEqual(ProbeOutcome.Pass, backLeg.Outcome, backLeg.Describe());
        }

        /// <summary>A wall blocks the straight walk to a floor point behind it: the body stops at the wall, not at the target.</summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void A_wall_blocks_the_straight_walk_and_the_control_6_m_the_other_way_passes()
        {
            RequireDats();

            Assert.AreEqual(BehindWallCell, AdjustCell.Get(Landblock, 0).GetCell(BehindWall), "the target is a real cell, not void");

            using var probe = new PuzzleWalkProbe(0);

            var walled = probe.Walk("behind wall", "out", AnchorCell, Anchor, BehindWall, null, null);
            Assert.AreEqual(ProbeOutcome.Blocked, walled.Outcome, walled.Describe());
            Assert.IsTrue(walled.At.Value.X > BehindWall.X + 1f, $"stopped at the wall, short of the target: {walled.Describe()}");
            StringAssert.Contains(walled.Reason, "geometry");

            // Control: the same 6 m, north along the corridor (0x01500198), has no wall.
            var open = probe.Walk("north", "out", AnchorCell, Anchor, Anchor + new Vector3(0, 6, 0), null, null);
            Assert.AreEqual(ProbeOutcome.Pass, open.Outcome, open.Describe());
        }

        /// <summary>The walk graph's doorway route takes the body round the wall that blocked the straight walk.</summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void The_doorway_route_goes_round_the_wall()
        {
            RequireDats();

            var graph = DungeonWalkGraphSource.Build((ushort)Landblock, ownCellDat, DatManager.PortalDat, out var error);
            Assert.IsNotNull(graph, error);

            var route = graph.Route(AnchorCell, Anchor, BehindWallCell, BehindWall);
            Assert.IsNotNull(route, "the walk graph reaches the cell behind the wall");
            Assert.IsTrue(route.Count > 0);

            using var probe = new PuzzleWalkProbe(0);
            var leg = probe.Walk("behind wall", "out", AnchorCell, Anchor, BehindWall, route, null);

            Assert.AreEqual(ProbeOutcome.Pass, leg.Outcome, leg.Describe());
            StringAssert.StartsWith(leg.Path, "via ");
        }

        // ---- doors: a retail door (unlocked in a run, players open it) is passable; a puzzle gate part is not ----

        private const uint DoorWcid = 999970;
        private static uint nextDoorGuid = 0x7F0E1001;

        /// <summary>
        /// A closed Door across the corridor 6 m north of the anchor, on the Warded Gate's own Setup and PhysicsState,
        /// spawned through the real path (InitPhysicsObj + AddPhysicsObj) so its physics object sits in the cell.
        /// </summary>
        private static ACE.Server.WorldObjects.Door SpawnDoor(bool puzzleGateMarker, uint wcid = DoorWcid, float z = 0f, uint cell = 0x01500198, Vector3? at = null, System.Numerics.Quaternion? rotation = null, int physicsState = 24)
        {
            var weenie = new ACE.Entity.Models.Weenie
            {
                WeenieClassId = wcid,
                ClassName = "walkprobetestdoor",
                WeenieType = ACE.Entity.Enum.WeenieType.Door,
                PropertiesString = new Dictionary<ACE.Entity.Enum.Properties.PropertyString, string> { { ACE.Entity.Enum.Properties.PropertyString.Name, "Walk Probe Test Door" } },
                PropertiesDID = new Dictionary<ACE.Entity.Enum.Properties.PropertyDataId, uint> { { ACE.Entity.Enum.Properties.PropertyDataId.Setup, 0x0200024F } },
                PropertiesInt = new Dictionary<ACE.Entity.Enum.Properties.PropertyInt, int> { { ACE.Entity.Enum.Properties.PropertyInt.PhysicsState, physicsState } },
                PropertiesBool = new Dictionary<ACE.Entity.Enum.Properties.PropertyBool, bool>(),
            };

            var door = new ACE.Server.WorldObjects.Door(weenie, new ACE.Entity.ObjectGuid(nextDoorGuid++)) { IsPuzzleGateObject = puzzleGateMarker };
            var p = at ?? new Vector3(40f, -12f, z);
            var q = rotation ?? System.Numerics.Quaternion.Identity;
            door.Location = new ACE.Entity.Position(cell, p.X, p.Y, p.Z, q.X, q.Y, q.Z, q.W, 0);
            door.InitPhysicsObj();
            Assert.IsTrue(door.AddPhysicsObj(), "the test door did not spawn");
            return door;
        }

        private static void Despawn(ACE.Server.WorldObjects.Door door) => door?.PhysicsObj?.leave_world();

        /// <summary>
        /// Sight looks through the doors the walk passes (the probe's own passable predicate): with a closed retail door
        /// across the corridor between the body and a target 9 m north, the target is seen; with a puzzle gate part
        /// (IsPuzzleGateObject) in exactly the same spot it is not. Control: no door, seen.
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void Sight_looks_through_a_retail_door_but_not_a_puzzle_gate_part()
        {
            RequireDats();

            var target = Anchor + new Vector3(0f, 9f, 0f);

            bool Sees()
            {
                using var probe = new PuzzleWalkProbe(0, o => PuzzleWalkProbe.IsPassableDoor(o, new HashSet<uint>()));
                var start = probe.FindStart(AnchorCell, Anchor, null, out _).Value;
                return probe.CanSee(start.Cell, start.Pos, target, 1.0f);
            }

            Assert.IsTrue(Sees(), "control: no door");

            var retail = SpawnDoor(puzzleGateMarker: false);

            try
            {
                Assert.IsTrue(Sees(), "a closed retail door is opened on the way, so it does not block sight");
            }
            finally
            {
                Despawn(retail);
            }

            var gatePart = SpawnDoor(puzzleGateMarker: true);

            try
            {
                Assert.IsFalse(Sees(), "a puzzle gate part in the same spot blocks sight");
            }
            finally
            {
                Despawn(gatePart);
            }
        }

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void A_retail_door_is_walked_through_and_named_while_a_puzzle_gate_part_blocks()
        {
            RequireDats();

            var north = Anchor + new Vector3(0, 15, 0);

            using (var clear = new PuzzleWalkProbe(0))
            {
                var open = clear.Walk("north", "out", AnchorCell, Anchor, north, null, null);
                Assert.AreEqual(ProbeOutcome.Pass, open.Outcome, "control, no door: " + open.Describe());
            }

            var retail = SpawnDoor(puzzleGateMarker: false);

            try
            {
                // Control: with no passable rule the door is solid, so it really stands in the way.
                using (var solid = new PuzzleWalkProbe(0))
                {
                    var stopped = solid.Walk("north", "out", AnchorCell, Anchor, north, null, null);
                    Assert.AreEqual(ProbeOutcome.Blocked, stopped.Outcome, "control, door solid: " + stopped.Describe());
                }

                using var probe = new PuzzleWalkProbe(0, o => PuzzleWalkProbe.IsPassableDoor(o, new HashSet<uint>()));
                var through = probe.Walk("north", "out", AnchorCell, Anchor, north, null, null);

                Assert.AreEqual(ProbeOutcome.Pass, through.Outcome, through.Describe());
                CollectionAssert.AreEqual(new[] { $"wcid {DoorWcid}" }, through.DoorsPassed?.ToList(), through.Describe());
                StringAssert.Contains(PuzzleSiteSweep.DoorsPassedNote(new[] { through }), $"doors passed: wcid {DoorWcid} x1");

                // The same door listed among the placement's gate parts stays solid.
                using var asGatePart = new PuzzleWalkProbe(0, o => PuzzleWalkProbe.IsPassableDoor(o, new HashSet<uint> { retail.Guid.Full }));
                var byGuid = asGatePart.Walk("north", "out", AnchorCell, Anchor, north, null, null);
                Assert.AreEqual(ProbeOutcome.Blocked, byGuid.Outcome, "a gate part by guid: " + byGuid.Describe());
            }
            finally
            {
                Despawn(retail);
            }

            var gate = SpawnDoor(puzzleGateMarker: true);

            try
            {
                using var probe = new PuzzleWalkProbe(0, o => PuzzleWalkProbe.IsPassableDoor(o, new HashSet<uint>()));
                var blocked = probe.Walk("north", "out", AnchorCell, Anchor, north, null, null);

                Assert.AreEqual(ProbeOutcome.Blocked, blocked.Outcome, "a door marked IsPuzzleGateObject: " + blocked.Describe());
                Assert.IsNull(blocked.DoorsPassed);
            }
            finally
            {
                Despawn(gate);
            }
        }

        /// <summary>
        /// DoorsPassed names only the doors the retry NEEDED. A decoy door floats 2.2 m up over the blocking one, in the
        /// same cell, clear of the body's head: the retry is asked about it (ConsultedDoors, what the leg used to report)
        /// but walking needs only the blocking door, so only that one is reported.
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void Only_the_doors_a_retry_needed_are_reported_not_every_door_consulted()
        {
            RequireDats();

            const uint DecoyWcid = 999971;
            var north = Anchor + new Vector3(0, 15, 0);
            var blocker = SpawnDoor(puzzleGateMarker: false);
            ACE.Server.WorldObjects.Door decoy = null;

            try
            {
                decoy = SpawnDoor(puzzleGateMarker: false, wcid: DecoyWcid, z: 2.2f);

                using var probe = new PuzzleWalkProbe(0, o => PuzzleWalkProbe.IsPassableDoor(o, new HashSet<uint>()));
                var through = probe.Walk("north", "out", AnchorCell, Anchor, north, null, null);

                Assert.AreEqual(ProbeOutcome.Pass, through.Outcome, through.Describe());
                CollectionAssert.Contains(probe.ConsultedDoors, $"wcid {DecoyWcid}", "discriminator: the retry was asked about the decoy, so reporting every consulted door would name it. Consulted: " + string.Join(", ", probe.ConsultedDoors));
                CollectionAssert.AreEqual(new[] { $"wcid {DoorWcid}" }, through.DoorsPassed?.ToList(), through.Describe());
            }
            finally
            {
                Despawn(decoy);
                Despawn(blocker);
            }
        }

        /// <summary>
        /// The probe's Step is PhysicsObj.transition: for the same body, start (settled, in contact) and offset, the probe's
        /// own Transition and the body's PhysicsObj.transition (get_object_info and all) end at the same cell and point.
        /// Offsets: a diagonal into the corridor's west wall (a wall slide; no ledge was found in this landblock's tested
        /// area, so this is the geometry case) and a clear step north (control).
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void A_probe_step_ends_where_PhysicsObj_transition_ends_for_the_same_body_and_offset()
        {
            RequireDats();

            using var probe = new PuzzleWalkProbe(0);
            var body = probe.Body;
            Assert.AreEqual(PuzzleWalkProbe.PlayerBodyState, body.State, "the probe body carries a player's PhysicsState (EdgeSlide included)");

            var settled = probe.Settle(AnchorCell, Anchor);
            Assert.IsNotNull(settled, "the anchor has a floor");
            var s = settled.Value;
            Assert.IsTrue(s.Contact && s.OnWalkable);

            foreach (var offset in new[] { new Vector3(-6f, 1f, 0f), new Vector3(0f, 1f, 0f) })
            {
                var stepped = probe.Step(s, offset, null, out _);
                Assert.IsNotNull(stepped, $"probe step {offset}");

                try
                {
                    body.CurCell = ACE.Server.Physics.Common.LScape.get_landcell(s.Cell, 0);
                    body.ContactPlane = s.ContactPlane;
                    body.ContactPlaneCellID = s.ContactPlaneCell;
                    body.TransientState = TransientStateFlags.Contact | TransientStateFlags.OnWalkable;
                    body.Velocity = Vector3.Zero;

                    var from = new ACE.Server.Physics.Common.Position(s.Cell, new ACE.Server.Physics.Animation.AFrame(s.Pos, Quaternion.Identity));
                    var to = new ACE.Server.Physics.Common.Position(s.Cell, new ACE.Server.Physics.Animation.AFrame(s.Pos + offset, Quaternion.Identity));
                    var trans = body.transition(from, to, false);

                    Assert.IsNotNull(trans, $"PhysicsObj.transition {offset}");
                    var end = trans.SpherePath.CurPos;

                    Assert.AreEqual(end.ObjCellID, stepped.Value.Cell, $"cell for {offset}");
                    Assert.IsTrue(Vector3.Distance(end.Frame.Origin, stepped.Value.Pos) < 0.001f, $"{offset}: transition {end.Frame.Origin} vs probe {stepped.Value.Pos}");
                }
                finally
                {
                    body.CurCell = null;
                    body.TransientState = 0;
                }
            }

            // The wall case really is a slide: the body ends short of the full offset (the wall stopped its x).
            var walled = probe.Step(s, new Vector3(-6f, 1f, 0f), null, out _).Value;
            Assert.IsTrue(walled.Pos.X > s.Pos.X - 6f + 0.5f, $"the diagonal met the wall: {walled.Pos}");
        }

        // ---- what a player needs: standing room near the anchor, and the server's use range at the lever ----

        // puzzle-gates.json filos_doom s7: anchor (68, -80, -12) in 0x0150013F, yaw 270 (gate to +x, levers to -x).
        private const uint S7Cell = 0x0150013F;
        private static readonly Vector3 S7Anchor = new Vector3(68f, -80f, -12f);

        /// <summary>
        /// The 2026-10-07 sweep's "no floor within 12 m under the start point" at s7, as observed: the curated anchor lies
        /// in NO dungeon cell (inside the wall between 0x01500141 to the west and 0x0150013F to the east), and so does the
        /// point 0.6 m toward the levers (-x); the body cannot settle at either. With the anchor in no cell, the region is
        /// its DECLARED cell 0x0150013F when a candidate stands there (FindAnchorStart rule 2): the 1.0 m east standing
        /// room. The approach-side fallback (rule 3, AnchorRegion) would be 0x01500141; with the declared cell ignored
        /// (an outdoor-style declared cell) the start is found there, and the walk graph, with s7's gate doorway closed,
        /// reaches it (it is the region).
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void The_s7_anchor_is_inside_the_wall_and_the_start_is_found_in_its_declared_cell()
        {
            RequireDats();

            var adjust = AdjustCell.Get(Landblock, 0);
            using var probe = new PuzzleWalkProbe(0);

            Assert.IsNull(adjust.GetCell(S7Anchor + new Vector3(0, 0, 0.5f)), "the s7 anchor is in no cell");
            Assert.IsNull(probe.Settle(S7Cell, S7Anchor), "no standing on the anchor itself");

            var towardLevers = S7Anchor + new Vector3(-0.6f, 0f, 0f);
            Assert.IsNull(adjust.GetCell(towardLevers + new Vector3(0, 0, 0.5f)), "0.6 m toward the levers is in no cell either");

            // Control: 2 m east, inside 0x0150013F, the body stands.
            var east = S7Anchor + new Vector3(2f, 0f, 0f);
            Assert.AreEqual(S7Cell, adjust.GetCell(east + new Vector3(0, 0, 0.5f)));
            var standing = probe.Settle(S7Cell, east);
            Assert.IsTrue(standing.HasValue && standing.Value.OnWalkable, "control: 2 m east the body stands");

            var graph = DungeonWalkGraphSource.Build((ushort)Landblock, ownCellDat, DatManager.PortalDat, out var error);
            Assert.IsNotNull(graph, error);

            var approach = new Vector3(-1f, 0f, 0f);   // yaw 270: the gate is to +x, a player comes from -x

            // Rule 2: the declared cell holds standing room, so it is the region.
            var declared = probe.FindAnchorStart(S7Cell, S7Anchor, approach, (c, p) => PuzzleSiteSweep.StartReach(graph, c, p, null), out var declaredRegion, out _, out var declaredHow, out var declaredOffset);
            Assert.IsNotNull(declared);
            Assert.AreEqual(S7Cell, declaredRegion, declaredHow);
            Assert.AreEqual(S7Cell, declared.Value.Cell);
            StringAssert.Contains(declaredHow, "region = its declared cell 0x0150013F");
            Assert.IsTrue(declaredOffset <= PuzzleWalkProbe.StandRadius + 1e-3f, $"the nearest declared-cell standing room, 1.0 m east: {declaredOffset}");

            // Rule 3, the approach-side fallback, on its own:
            var region = probe.AnchorRegion(S7Cell, S7Anchor, approach, out var regionPoint, out var how);
            Assert.AreEqual(0x01500141u, region, how);
            StringAssert.Contains(how, "anchor in no cell; region = 0x01500141");
            Assert.IsTrue(regionPoint.X < S7Anchor.X, "the region point is on the approach side");

            var gatePoint = PuzzleGateGenerator.ToWorld(S7Anchor, 270f, new Vector3(0f, PuzzleLayoutParams.Default.GateDistance, 0f));
            var doorway = graph.NearestDoorway(gatePoint);
            var reach = PuzzleSiteSweep.StartReach(graph, region, regionPoint, doorway.HasValue ? new[] { doorway.Value } : null);
            Assert.IsNotNull(reach, "the walk graph holds the region cell, so a start elsewhere can be proven");

            // The gate doorway, built as the sweep builds it (NearestDoorway at the gate point): with it closed, the field
            // from the declared cell accepts the lever side (-x, 0x01500141) and rejects the cell beyond the doorway.
            Assert.IsTrue(doorway.HasValue, "a doorway at s7's gate point");
            var forward = PuzzleGateGenerator.ToWorld(S7Anchor, 270f, new Vector3(0f, 1f, 0f)) - S7Anchor;
            // Observed 2026-10-07: the doorway joins 0x01500140 and 0x01500145; 1 m past the gate point is 0x01500143.
            var pastGate = AdjustCell.Get(Landblock, 0).GetCell(gatePoint + forward * 1.0f + new Vector3(0, 0, 0.5f)) ?? 0u;
            Assert.AreNotEqual(0u, pastGate);
            var closedField = PuzzleSiteSweep.StartReach(graph, S7Cell, S7Anchor, new[] { doorway.Value });
            Assert.IsTrue(closedField(0x01500141u), "the lever side is reachable with the gate closed");
            Assert.IsTrue(closedField(doorway.Value.Item1) != closedField(doorway.Value.Item2), $"exactly one side of the doorway {doorway} is reachable with it closed");
            var beyond = closedField(doorway.Value.Item1) ? doorway.Value.Item2 : doorway.Value.Item1;
            Assert.IsFalse(closedField(beyond), "the cell beyond the gate doorway is not");
            Assert.IsFalse(closedField(pastGate), $"nor 1 m past the gate point (0x{pastGate:X8})");
            Assert.IsTrue(PuzzleSiteSweep.StartReach(graph, S7Cell, S7Anchor, null)(beyond), "control: with the doorway open it is");
            Assert.IsNull(PuzzleSiteSweep.RegionNotProven(graph, true, S7Cell, S7Anchor, 0x01500141u, new[] { doorway.Value }), "the approach-side region is proven from the declared cell");
            Assert.IsNotNull(PuzzleSiteSweep.RegionNotProven(graph, true, S7Cell, S7Anchor, beyond, new[] { doorway.Value }), "a region beyond the gate is not");

            var start = probe.FindStart(S7Cell, S7Anchor, approach, region, reach, out var offset);
            Assert.IsNotNull(start, "standing room within 1.5 m in the region");
            Assert.AreEqual(0x01500141u, start.Value.Cell);
            Assert.IsTrue(reach(start.Value.Cell), "graph-connected to the approach side");
            Assert.IsTrue(start.Value.Pos.X < S7Anchor.X && offset <= PuzzleWalkProbe.StartRadius + 1e-3f, $"west, within 1.5 m: {start.Value.Pos}, offset {offset}");

            // Control: from the east the region is 0x013F, and its standing room 1.0 m east is used.
            var eastRegion = probe.AnchorRegion(S7Cell, S7Anchor, new Vector3(1f, 0f, 0f), out var eastPoint, out _);
            Assert.AreEqual(S7Cell, eastRegion);
            var fromEast = probe.FindStart(S7Cell, S7Anchor, new Vector3(1f, 0f, 0f), eastRegion, PuzzleSiteSweep.StartReach(graph, eastRegion, eastPoint, null), out var eastOffset);
            Assert.IsNotNull(fromEast);
            Assert.AreEqual(S7Cell, fromEast.Value.Cell);
            Assert.IsTrue(fromEast.Value.Pos.X > S7Anchor.X && eastOffset <= PuzzleWalkProbe.StandRadius + 1e-3f, $"east side within 1.0 m: {fromEast.Value.Pos}, offset {eastOffset}");
        }

        /// <summary>
        /// One of the 27 gate anchors sweep_all6 refused (2026-10-07): artifex_collegium s7, anchor (209.477, -193.477, -6)
        /// inside the back wall of its lever room, declared cell 0x00ED0313. No approach-side point (away from the gate)
        /// within 1.5 m is in any cell, so the approach-side rule alone finds nothing (control); the declared-cell rule
        /// starts it in 0x00ED0313, in front of the wall.
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void A_back_wall_anchor_starts_in_its_declared_cell()
        {
            RequireDats();

            try
            {
                using var probe = new PuzzleWalkProbe(0);
                const uint declaredCell = 0x00ED0313;
                var anchor = new Vector3(209.477f, -193.477f, -6f);
                var forward = PuzzleGateGenerator.ToWorld(anchor, 355f, new Vector3(0f, 1f, 0f)) - anchor;
                var approach = new Vector3(-forward.X, -forward.Y, 0f);

                Assert.AreEqual(0u, probe.AnchorRegion(declaredCell, anchor, approach, out _, out var how), "control: the approach-side rule alone finds no cell: " + how);

                var start = probe.FindAnchorStart(declaredCell, anchor, approach, null, out var region, out _, out var startHow, out var offset);
                Assert.IsNotNull(start, startHow);
                Assert.AreEqual(declaredCell, region);
                Assert.AreEqual(declaredCell, start.Value.Cell);
                Assert.IsTrue(offset > 0f && offset <= PuzzleWalkProbe.StartRadius + 1e-3f, $"offset {offset}");
                Assert.IsTrue(Math.Abs(start.Value.Pos.Z - anchor.Z) <= probe.MaxStartRise, "within a step of the anchor's floor");
            }
            finally
            {
                ACE.Server.Physics.Common.LScape.unload_landblock(0x00EDFFFF, 0);
            }
        }

        /// <summary>
        /// A return leg that the greedy walk cannot make is retraced along the out leg's own footsteps: west to east
        /// through the 0x01500141 -> 0x0150013F doorway (routed), then back. Control: the same return with no route
        /// is blocked by the wall; retracing the out leg's trail passes.
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void A_blocked_return_passes_by_retracing_the_out_leg()
        {
            RequireDats();

            var graph = DungeonWalkGraphSource.Build((ushort)Landblock, ownCellDat, DatManager.PortalDat, out var error);
            Assert.IsNotNull(graph, error);

            using var probe = new PuzzleWalkProbe(0);
            var west = probe.FindStart(0x01500141, new Vector3(66.5f, -80f, -12f), new Vector3(-1, 0, 0), out _).Value;
            var east = probe.FindStart(S7Cell, S7Anchor, new Vector3(1, 0, 0), out _).Value;

            var outLeg = probe.WalkFrom("x", "out", west, east.Pos, graph.Route(west.Cell, west.Pos, east.Cell, east.Pos), null, probe.ReturnRule(east), out var outEnd, out var trail);
            Assert.AreEqual(ProbeOutcome.Pass, outLeg.Outcome, outLeg.Describe());
            Assert.IsTrue(trail.Count > 2, $"the out leg recorded its footsteps: {trail.Count}");

            var greedy = probe.WalkFrom("x", "back", outEnd, west.Pos, null, null, probe.ReturnRule(west), out _);
            Assert.AreEqual(ProbeOutcome.Blocked, greedy.Outcome, "control, no route back: " + greedy.Describe());

            var retraced = probe.Retrace("x", outEnd, trail, null, probe.ReturnRule(west), out var backEnd);
            Assert.AreEqual(ProbeOutcome.Pass, retraced.Outcome, retraced.Describe());
            StringAssert.StartsWith(retraced.Path, "retraced the out leg");
            Assert.IsTrue(Vector3.Distance(backEnd.Pos, west.Pos) <= PuzzleWalkProbe.StandRadius);
        }

        /// <summary>
        /// The region cell beats a nearer standing point in another cell: at s7 with 0x01500141 (west) as the region,
        /// the start is the 1.5 m west standing room although the east room in 0x0150013F is only 1.0 m off and every
        /// cell is acceptable. Control: with 0x013F as the region, the nearer east room is used.
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void The_region_cell_beats_a_nearer_standing_point_in_another_cell()
        {
            RequireDats();

            using var probe = new PuzzleWalkProbe(0);
            var approach = new Vector3(-1f, 0f, 0f);

            var west = probe.FindStart(S7Cell, S7Anchor, approach, 0x01500141u, _ => true, out var westOffset);
            Assert.IsNotNull(west);
            Assert.AreEqual(0x01500141u, west.Value.Cell);

            var east = probe.FindStart(S7Cell, S7Anchor, approach, S7Cell, _ => true, out var eastOffset);
            Assert.IsNotNull(east);
            Assert.AreEqual(S7Cell, east.Value.Cell);
            Assert.IsTrue(eastOffset < westOffset, $"the east room is nearer ({eastOffset} < {westOffset}), yet the region pass picked west first");
        }

        /// <summary>
        /// A start may sit above the point it serves by up to the step-up height: the s1 anchor point taken 0.3 m below
        /// the corridor floor settles onto the floor, 0.3 m above it. The cap itself (StartRiseOk) is unit-tested in both
        /// directions.
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void A_start_above_the_point_within_a_step_is_used()
        {
            RequireDats();

            using var probe = new PuzzleWalkProbe(0);
            Assert.IsTrue(probe.StepUpHeight > 0.35f, $"step-up {probe.StepUpHeight}");

            var below = Anchor - new Vector3(0f, 0f, 0.3f);
            var s = probe.FindStart(AnchorCell, below, null, out _);
            Assert.IsNotNull(s);
            Assert.AreEqual(0.3f, s.Value.Pos.Z - below.Z, 0.05f, "standing 0.3 m above the point");
        }

        /// <summary>
        /// A start on a ledge taller than a step is not returned to from the floor below it. The start is synthetic: the
        /// s1 anchor's standing state lifted by step-up + 0.2 m; the body walks back to it from 3 m up the corridor and
        /// ends within 1.0 m in 3D only for a low ledge. Blocked for the tall ledge (walk and retrace both), passed for a
        /// ledge 5 cm below the step-up height (control).
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void A_return_to_a_start_more_than_a_step_up_is_blocked()
        {
            RequireDats();

            using var probe = new PuzzleWalkProbe(0);
            var floor = probe.FindStart(AnchorCell, Anchor, null, out _).Value;
            var away = probe.FindStart(AnchorCell, Anchor + new Vector3(0f, 3f, 0f), null, out _).Value;

            var tall = floor with { Pos = floor.Pos + new Vector3(0f, 0f, probe.StepUpHeight + 0.2f) };
            var walked = probe.WalkFrom("x", "back", away, tall.Pos, null, null, probe.ReturnRule(tall), out _);
            Assert.AreEqual(ProbeOutcome.Blocked, walked.Outcome, walked.Describe());
            StringAssert.Contains(walked.Reason, "more than the");

            var retraced = probe.Retrace("x", away, new[] { tall, away }, null, probe.ReturnRule(tall), out _);
            Assert.AreEqual(ProbeOutcome.Blocked, retraced.Outcome, retraced.Describe());

            var low = floor with { Pos = floor.Pos + new Vector3(0f, 0f, probe.StepUpHeight - 0.05f) };
            var control = probe.WalkFrom("x", "back", away, low.Pos, null, null, probe.ReturnRule(low), out _);
            Assert.AreEqual(ProbeOutcome.Pass, control.Outcome, control.Describe());
        }

        /// <summary>
        /// A lever flush behind a wall is not seen: the sight cast aims at the lever's centre, not at a point short of
        /// its cylinder. Along y = -18 west from the s1 anchor, the last point in sight is found by stepping; a lever
        /// centred 0.1 m past it with a 0.3 m cylinder (which a cast stopped short by the radius would have reached
        /// in the open) is Blocked within its UseRadius; the same lever centred 0.3 m on the near side passes (control).
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void A_lever_flush_behind_a_wall_is_not_seen_and_one_on_the_near_side_is()
        {
            RequireDats();

            using var probe = new PuzzleWalkProbe(0);
            var start = probe.FindStart(AnchorCell, Anchor, null, out _).Value;
            const float height = 1.0f;

            var face = float.NaN;

            for (var x = start.Pos.X - 0.5f; x > 32f; x -= 0.05f)
            {
                if (!probe.CanSee(start.Cell, start.Pos, new Vector3(x, -18f, 0f), height))
                    break;

                face = x;
            }

            Assert.IsFalse(float.IsNaN(face), "something in sight to the west");
            Assert.IsTrue(face > 34.5f, $"the last point in sight lies at the corridor's west wall: {face}");

            var adjust = AdjustCell.Get(Landblock, 0);
            var behind = new Vector3(face - 0.1f, -18f, 0f);
            var behindCell = adjust.GetCell(behind + new Vector3(0, 0, 0.5f)) ?? AnchorCell;
            var lever = new UseReach(0.3f, height, 10f, "flush lever");

            var hidden = probe.WalkFrom("lever 1", "out", start, behind, null, null, probe.UseRange(behindCell, behind, lever), out _);
            Assert.AreEqual(ProbeOutcome.Blocked, hidden.Outcome, hidden.Describe());
            StringAssert.Contains(hidden.Reason, "not in sight from eye height");

            var near = new Vector3(face + 0.3f, -18f, 0f);
            var shown = probe.WalkFrom("lever 2", "out", start, near, null, null, probe.UseRange(AnchorCell, near, lever), out _);
            Assert.AreEqual(ProbeOutcome.Pass, shown.Outcome, shown.Describe());
        }

        /// <summary>
        /// A target object in the puzzle lever's state (PhysicsState 20 = Ethereal + IgnoreCollisions) standing on the
        /// sight point does not block its own sight: the cast reaches the centre touching nothing. The same object in a
        /// collidable state (24, the gate door's) at the same spot does block it (discriminating pair, same geometry).
        /// Both carry the puzzle gate marker, so it is the physics state, not the door predicate, that lets the cast
        /// through. Control: no object, seen.
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void A_target_in_the_lever_state_does_not_block_its_own_sight_and_a_collidable_one_does()
        {
            RequireDats();

            var target = Anchor + new Vector3(0f, 9f, 0f);
            var targetCell = AdjustCell.Get(Landblock, 0).GetCell(target + new Vector3(0f, 0f, 0.5f)) ?? 0u;
            Assert.AreNotEqual(0u, targetCell, "the target point is in a cell");

            bool Sees()
            {
                using var probe = new PuzzleWalkProbe(0, o => PuzzleWalkProbe.IsPassableDoor(o, new HashSet<uint>()));
                var start = probe.FindStart(AnchorCell, Anchor, null, out _).Value;
                return probe.CanSee(start.Cell, start.Pos, target, 1.0f);
            }

            Assert.IsTrue(Sees(), "control: no object at the target");

            var lever = SpawnDoor(puzzleGateMarker: true, cell: targetCell, at: target, physicsState: 20);

            try
            {
                Assert.AreEqual(ACE.Entity.Enum.PhysicsState.Ethereal | ACE.Entity.Enum.PhysicsState.IgnoreCollisions, lever.PhysicsObj.State & (ACE.Entity.Enum.PhysicsState.Ethereal | ACE.Entity.Enum.PhysicsState.IgnoreCollisions));
                Assert.IsTrue(Sees(), "a lever-state object on the target does not block the cast");
            }
            finally
            {
                Despawn(lever);
            }

            var solid = SpawnDoor(puzzleGateMarker: true, cell: targetCell, at: target, physicsState: 24);

            try
            {
                Assert.IsFalse(Sees(), "the same object, collidable, at the same spot blocks it");
            }
            finally
            {
                Despawn(solid);
            }
        }

        /// <summary>
        /// A start in a cell the anchor's region does not reach is refused: with only 0x0150018C (behind the corridor's
        /// west wall) acceptable, the standing room in the s1 corridor is not used and nothing is found within 1.5 m
        /// (the sweep reports that as a blocked "anchor start"). Control: the same search with the corridor as the
        /// region stands on the anchor.
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void A_start_in_a_cell_the_region_does_not_reach_is_refused()
        {
            RequireDats();

            using var probe = new PuzzleWalkProbe(0);

            Assert.IsNull(probe.FindStart(AnchorCell, Anchor, null, BehindWallCell, c => c == BehindWallCell, out _), "only the far side of the wall is acceptable");

            var control = probe.FindStart(AnchorCell, Anchor, null, AnchorCell, c => c == AnchorCell, out var offset);
            Assert.IsNotNull(control);
            Assert.AreEqual(0f, offset);
        }

        /// <summary>
        /// A start the player could not climb back up to from the floor below is refused: the s1 anchor lifted more than
        /// the body's step-up height + 0.1 m above the corridor floor has no start (every candidate settles onto the
        /// floor, too far below). Control: lifted 5 cm less than that limit, the floor below is used.
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void A_start_more_than_a_step_below_the_anchor_is_refused()
        {
            RequireDats();

            using var probe = new PuzzleWalkProbe(0);
            Assert.IsTrue(probe.MaxStartRise > PuzzleWalkProbe.StartRiseMargin, $"step-up height loaded: {probe.MaxStartRise}");

            var tooHigh = Anchor + new Vector3(0f, 0f, probe.MaxStartRise + 0.2f);
            Assert.IsNull(probe.FindStart(AnchorCell, tooHigh, null, out _), "the floor is out of a step's reach below the point");

            var withinStep = Anchor + new Vector3(0f, 0f, probe.MaxStartRise - 0.05f);
            var s = probe.FindStart(AnchorCell, withinStep, null, out _);
            Assert.IsNotNull(s, "control: within a step of the floor");
            Assert.AreEqual(Anchor.Z, s.Value.Pos.Z, 0.05f);
        }
        /// <summary>FindStart: a standable curated point is used as it is (offset 0); a point inside the rock below a floor has no standing room within 1.5 m.</summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void FindStart_keeps_a_standable_anchor_and_refuses_a_point_inside_solid_rock()
        {
            RequireDats();

            using var probe = new PuzzleWalkProbe(0);

            var onAnchor = probe.FindStart(AnchorCell, Anchor, new Vector3(0, -1, 0), out var offset);
            Assert.IsNotNull(onAnchor, "control: the s1 anchor is standable");
            Assert.AreEqual(0f, offset, "used as it is");

            var buried = Anchor + new Vector3(0f, 0f, -3f);   // 3 m under the s1 corridor floor
            Assert.IsNull(probe.FindStart(AnchorCell, buried, null, out _), "no standing room within 1.5 m inside the rock (a floor 3 m off is not within a step)");
        }

        /// <summary>
        /// Arrival needs the lever in sight, not just in range: a lever behind the corridor's west wall is Blocked even
        /// with a UseRadius that covers the whole distance from the start (the old through-wall pass with zero steps),
        /// while a lever 6 m up the open corridor with the same radius passes (control). CanSee itself agrees.
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void A_lever_behind_a_wall_within_use_range_is_blocked_and_a_visible_one_passes()
        {
            RequireDats();

            using var probe = new PuzzleWalkProbe(0);
            var start = probe.FindStart(AnchorCell, Anchor, null, out _).Value;
            var lever = new UseReach(0.1f, 1.0f, 10f, "test lever");
            var north = Anchor + new Vector3(0f, 6f, 0f);
            var northCell = AdjustCell.Get(Landblock, 0).GetCell(north + new Vector3(0, 0, 0.5f)) ?? 0u;
            Assert.AreNotEqual(0u, northCell);

            Assert.IsTrue(PuzzleSiteSweep.UseDistance(probe.BodyRadius, probe.BodyHeight, start.Cell, start.Pos, lever, BehindWallCell, BehindWall) < lever.UseRadius - PuzzleSiteSweep.UseMargin,
                "the walled lever is within range from the start");
            Assert.IsFalse(probe.CanSee(start.Cell, start.Pos, BehindWall, lever.Height), "but not in sight");
            Assert.IsTrue(probe.CanSee(start.Cell, start.Pos, north, lever.Height), "control: up the corridor is in sight");

            var walled = probe.WalkFrom("lever 1", "out", start, BehindWall, null, null, probe.UseRange(BehindWallCell, BehindWall, lever), out var wallEnd);
            Assert.AreEqual(ProbeOutcome.Blocked, walled.Outcome, walled.Describe());
            StringAssert.Contains(walled.Reason, "not in sight from eye height");

            var visible = probe.WalkFrom("lever 2", "out", start, north, null, null, probe.UseRange(northCell, north, lever), out _);
            Assert.AreEqual(ProbeOutcome.Pass, visible.Outcome, visible.Describe());
            StringAssert.Contains(visible.Measure, "server cylinder distance");

            // The return leg: back to within 1.0 m of the start passes.
            var back = probe.WalkFrom("lever 1", "back", wallEnd, start.Pos, null, null, probe.ReturnRule(start), out var backEnd);
            Assert.AreEqual(ProbeOutcome.Pass, back.Outcome, back.Describe());
            Assert.IsTrue(Vector3.Distance(backEnd.Pos, start.Pos) <= PuzzleWalkProbe.StandRadius);
        }

        /// <summary>UseDistance is the server's Position.CylinderDistance (dat-backed: it reaches LandDefs).</summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void UseDistance_is_the_server_cylinder_distance()
        {
            RequireDats();

            var lever = new UseReach(0.2f, 1.5f, 2.5f, "lever wcid 1006851");

            // UseDistance is Position.CylinderDistance: 3 m apart on one floor, radii 0.5 + 0.2 = 2.3 m.
            Assert.AreEqual(2.3f, PuzzleSiteSweep.UseDistance(0.5f, 1.8f, AnchorCell, new Vector3(10, 10, 0), lever, AnchorCell, new Vector3(13, 10, 0)), 1e-4f);

            // A lever 3 m below the body's feet: the server's reach is the 3D offset less both radii, and the gap above
            // the lever's 1.5 m top counts too: sqrt(reach^2 + 1.5^2).
            var below = PuzzleSiteSweep.UseDistance(0.5f, 1.8f, AnchorCell, new Vector3(10, 10, 3), lever, AnchorCell, new Vector3(13, 10, 0));
            var reach3d = MathF.Sqrt(18f) - 0.7f;
            Assert.AreEqual(MathF.Sqrt(reach3d * reach3d + 1.5f * 1.5f), below, 1e-3f);
        }

        // ---- doorways: the body walks THROUGH a routed doorway before it turns (2026-10-07 filos_doom s7/s8/r7) ----

        /// <summary>
        /// The 0x01500141 -> 0x0150013F doorway (centroid (68.3, -76.7)): west to east used to stop at (68.3, -77.8),
        /// because the body turned for the target as soon as it was within 0.75 m of the doorway centroid, still on the
        /// near side, and walked into the jamb. With the push-through goal it crosses both ways. Control: the straight
        /// walk with no route is blocked by the wall between the cells.
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void The_0141_to_013F_doorway_is_crossed_both_ways_and_the_straight_walk_is_walled()
        {
            RequireDats();

            var graph = DungeonWalkGraphSource.Build((ushort)Landblock, ownCellDat, DatManager.PortalDat, out var error);
            Assert.IsNotNull(graph, error);

            using var probe = new PuzzleWalkProbe(0);
            var west = probe.FindStart(0x01500141, new Vector3(66.5f, -80f, -12f), new Vector3(-1, 0, 0), out _).Value;
            var east = probe.FindStart(S7Cell, S7Anchor, new Vector3(1, 0, 0), out _).Value;   // the declared cell 0x013F is the region: its east standing room
            Assert.AreEqual(0x01500141u, west.Cell);
            Assert.AreEqual(S7Cell, east.Cell);

            var straight = probe.WalkFrom("x", "out", west, east.Pos, null, null, probe.ReturnRule(east), out _);
            Assert.AreEqual(ProbeOutcome.Blocked, straight.Outcome, "control, no route: " + straight.Describe());

            var toEast = graph.Route(west.Cell, west.Pos, east.Cell, east.Pos);
            Assert.IsTrue(toEast?.Count > 0, "the graph routes through a doorway");

            var we = probe.WalkFrom("x", "out", west, east.Pos, toEast, null, probe.ReturnRule(east), out var weEnd);
            Assert.AreEqual(ProbeOutcome.Pass, we.Outcome, "west to east: " + we.Describe());
            Assert.AreEqual(S7Cell, weEnd.Cell);

            var ew = probe.WalkFrom("x", "back", east, west.Pos, graph.Route(east.Cell, east.Pos, west.Cell, west.Pos), null, probe.ReturnRule(west), out _);
            Assert.AreEqual(ProbeOutcome.Pass, ew.Outcome, "east to west: " + ew.Describe());
        }

        /// <summary>
        /// r7's return through the doorway that holds retail door wcid 2180 (0x01500176, (55.25, -80, -6), the real
        /// Setup 0x0200024F and PhysicsState 24), from the focal approach (0x01500168) to the anchor (0x01500174):
        /// with no door it passes (the doorway is walkable); with the door solid it is blocked (the door really stands
        /// there); with the passable-door retry it passes and names the door.
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void The_r7_return_crosses_the_door_doorway_when_the_door_is_passable_and_not_when_it_is_solid()
        {
            RequireDats();

            var graph = DungeonWalkGraphSource.Build((ushort)Landblock, ownCellDat, DatManager.PortalDat, out var error);
            Assert.IsNotNull(graph, error);

            var anchor = new Vector3(56f, -76f, -6f);
            var focal = PuzzleSiteSweep.FocalApproach(anchor, 135f, 7f);

            ProbeLeg Walk(Func<PhysicsObj, bool> passable)
            {
                using var probe = new PuzzleWalkProbe(0, passable);
                var a = probe.FindStart(0x01500174, anchor, null, out _).Value;
                var f = probe.FindStart(0x01500168, focal, null, out _).Value;
                return probe.WalkFrom("focal approach", "back", f, a.Pos, graph.Route(f.Cell, f.Pos, a.Cell, a.Pos), null, probe.ReturnRule(a), out _);
            }

            var open = Walk(null);
            Assert.AreEqual(ProbeOutcome.Pass, open.Outcome, "no door: " + open.Describe());

            var door = SpawnDoor(false, 2180, 0f, 0x01500176, new Vector3(55.25f, -80f, -6f), new System.Numerics.Quaternion(0f, 0f, -0.707107f, -0.707107f));

            try
            {
                var solid = Walk(null);
                Assert.AreEqual(ProbeOutcome.Blocked, solid.Outcome, "door solid: " + solid.Describe());

                var passable = Walk(o => PuzzleWalkProbe.IsPassableDoor(o, new HashSet<uint>()));
                Assert.AreEqual(ProbeOutcome.Pass, passable.Outcome, "door passable: " + passable.Describe());
                CollectionAssert.AreEqual(new[] { "wcid 2180" }, passable.DoorsPassed?.ToList());
            }
            finally
            {
                Despawn(door);
            }
        }
        /// <summary>A leg that walks into a cell the gate should have closed fails, naming the cell; the control is the same walk unrestricted.</summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void A_walk_into_a_cell_beyond_the_gate_is_blocked()
        {
            RequireDats();

            using var probe = new PuzzleWalkProbe(0);
            var north = Anchor + new Vector3(0, 15, 0);   // 0x01500197, straight up the corridor

            var unrestricted = probe.Walk("north", "out", AnchorCell, Anchor, north, null, null);
            Assert.AreEqual(ProbeOutcome.Pass, unrestricted.Outcome, unrestricted.Describe());

            var gated = probe.Walk("north", "out", AnchorCell, Anchor, north, null, cell => cell != 0x01500197);
            Assert.AreEqual(ProbeOutcome.Blocked, gated.Outcome, gated.Describe());
            StringAssert.Contains(gated.Reason, "passed the gate into 0x01500197");
        }

        // ================= dat handling (as ExactPlacementTests) =================

        private static readonly object initLock = new object();
        /// <summary>
        /// artifex_collegium r4's second shuffle spot (138.436, -97.019, -12), in room template Environment 0x0D000196
        /// (2026-10-07 sweep_all2): AdjustCell resolves the floor point to 0x00ED0190, and the lever's physics cell is
        /// 0x00ED0191, a 6 x 2 m cell whose edge lies 2 cm away. 0x0191 does not hold the floor point but does hold the
        /// same point at body height, which is where physics files the lever. Control: the anchor 5 m away is in
        /// 0x0191 at neither height.
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void A_boundary_spot_is_in_the_neighbour_cell_at_body_height_but_not_at_the_floor()
        {
            RequireDats();

            try
            {
                var spot = new Vector3(138.436f, -97.019f, -12f);
                var anchor = new Vector3(138f, -102f, -12f);
                var lift = new Vector3(0f, 0f, PuzzleSiteSweep.BodyLift(0f));

                Assert.AreEqual(0x00ED0190u, AdjustCell.Get(0x00ED, 0).GetCell(spot));

                var neighbour = ACE.Server.Physics.Common.LScape.get_landcell(0x00ED0191, 0) as ACE.Server.Physics.Common.EnvCell;
                Assert.IsNotNull(neighbour);
                Assert.IsFalse(neighbour.point_in_cell(spot), "not at the floor");
                Assert.IsTrue(neighbour.point_in_cell(spot + lift), "at body height");

                Assert.IsFalse(neighbour.point_in_cell(anchor) || neighbour.point_in_cell(anchor + lift), "control: the anchor is in the neighbour at neither height");
            }
            finally
            {
                ACE.Server.Physics.Common.LScape.unload_landblock(0x00EDFFFF, 0);
            }
        }

        private static bool initialized;
        private static string skipReason;
        private static CellDatDatabase ownCellDat;
        private static bool cellDatSwapped;
        private static CellDatDatabase previousCellDat;
        private static bool wasServer;

        private static readonly System.Reflection.PropertyInfo cellDatProperty = typeof(DatManager).GetProperty(nameof(DatManager.CellDat));

        private static void RequireDats()
        {
            lock (initLock)
            {
                if (!initialized)
                {
                    initialized = true;
                    skipReason = InitializeDats();
                }
            }

            if (skipReason != null)
                Assert.Inconclusive(skipReason);

            previousCellDat = DatManager.CellDat;
            cellDatProperty.SetValue(null, ownCellDat);
            cellDatSwapped = true;

            wasServer = PhysicsEngine.Instance.Server;
            PhysicsEngine.Instance.Server = false;
        }

        [TestCleanup]
        public void RestoreGlobalState()
        {
            if (!cellDatSwapped)
                return;

            try
            {
                ACE.Server.Physics.Common.LScape.unload_landblock((Landblock << 16) | 0xFFFF, 0);
            }
            finally
            {
                PhysicsEngine.Instance.Server = wasServer;
                cellDatProperty.SetValue(null, previousCellDat);
                previousCellDat = null;
                cellDatSwapped = false;
            }
        }

        private static string InitializeDats()
        {
            if (PhysicsEngine.Instance == null)
            {
                var engine = new PhysicsEngine(new ACE.Server.Physics.Common.ObjectMaint(), new ACE.Server.Physics.Common.SmartBox());
                engine.Server = true;
            }

            var candidates = new List<string>();
            var fromEnv = System.Environment.GetEnvironmentVariable("ACE_DAT_PATH");

            if (!string.IsNullOrWhiteSpace(fromEnv))
                candidates.Add(fromEnv);

            candidates.Add(@"C:\ACE\Dats\");
            candidates.Add(@"C:\Turbine\Asheron's Call\");

            var datDir = candidates.FirstOrDefault(c => File.Exists(Path.Combine(c, "client_portal.dat")) && File.Exists(Path.Combine(c, "client_cell_1.dat")));

            if (datDir == null)
                return $"Skipped: client_portal.dat + client_cell_1.dat not found in any of [{string.Join(", ", candidates)}]. Set ACE_DAT_PATH.";

            try
            {
                System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

                if (DatManager.PortalDat == null)
                    DatManager.Initialize(datDir, true, false);

                ownCellDat = new CellDatDatabase(Path.Combine(datDir, "client_cell_1.dat"), true);
            }
            catch (Exception ex)
            {
                return $"Skipped: loading the dats from {datDir} failed: {ex.Message}";
            }

            return DatManager.PortalDat == null || ownCellDat == null ? $"Skipped: {datDir} did not produce both a PortalDat and a CellDat." : null;
        }
    }
}
