using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.DatLoader;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.HazardMechanics;
using ACE.Server.Physics;
using ACE.Server.Physics.Util;
using ACE.Server.WorldObjects;

using Position = ACE.Entity.Position;

namespace ACE.Server.Tests.HazardMechanics
{
    /// <summary>
    /// Dat-backed proofs on the REAL Pinnacle arena geometry (dungeon landblock 0x0069, client_cell_1.dat) through
    /// the ported physics the server runs (and which is a port of the client's own).
    ///
    /// BUG 2026-10-18 (balls stuck over a platform): a Rolling Death (setup 0x02000DDD, scale 2.25, sphere r 1.8 at
    /// its origin, PhysicsState 2068) hovering ball_z over a platform top is STEPPED DOWN onto it by the placement
    /// (non-Missile objects step down), comes to rest OnWalkable, and calc_friction then bleeds its velocity away:
    /// it spins in place. HazardMotion.PrepareMovingBody makes moving hazards Missiles, which never step down.
    /// The control (no Missile) proves the stall is reachable here; the fixed case fails on the pre-fix code.
    ///
    /// ITEM 3 2026-10-18 (wisps over a block): at the new Pinnacle hazard height every point of the region lies in an
    /// EnvCell, so HazardGround.PlaceWisp never reports a solid on the way and Stand equals Pillar; the old -51 pit
    /// plane is the control that does block.
    ///
    /// Same dat handling as ExactPlacementTests: the cell dat is opened privately and swapped into DatManager only for
    /// each test, then the physics landblock is unloaded and the dat swapped back out.
    /// </summary>
    [TestClass]
    public class PinnacleArenaGeometryTests
    {
        private const uint Landblock = 0x0069;
        private const uint BallSetup = 0x02000DDD;   // 1006951 Rolling Death, PropertyDataId 1
        private const int BallPhysicsState = 2068;   // Ethereal, IgnoreCollisions, LightingOn (1006951 Rolling Death.sql)
        private const float BallScale = 2.25f;       // DefaultScale, sphere 0.8 x 2.25 = 1.8 m
        private const uint EastBlockCell = 0x00690119; // the cell over the east block (top -48.00)
        private const uint PitCell = 0x00690118;       // a pit cell north of it (floor -54.00)
        private const float HoverZ = -45.75f;          // ground plane -47.75 + ball_z 2

        private static readonly object initLock = new object();
        private static bool initialized;
        private static string skipReason;
        private static uint nextGuid = 0x7F0E1001;

        // ================= bug 2: the ball over a platform =================

        /// <summary>Positive control: a plain (non-Missile) ball over the east block is dropped onto it and stalls.</summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void Control_PlainBallOverAPlatform_IsSteppedDownOntoIt_AndFrictionStallsIt()
        {
            RequireDats();

            var wo = MakeBall(EastBlockCell, new Vector3(29.6f, -20f, HoverZ));

            try
            {
                Spawn(wo, missile: false);

                Assert.AreEqual(-46.2f, wo.Location.PositionZ, 0.02f, "control: the placement steps the hover ball down onto the block top (-48 + 1.8). If this fails the harness no longer reaches the step-down and the fixed test proves nothing.");
                Assert.IsTrue(wo.PhysicsObj.TransientState.HasFlag(TransientStateFlags.OnWalkable), $"control: resting on the platform ({wo.PhysicsObj.TransientState})");

                var moved = Roll(wo);
                Assert.IsTrue(moved < 1.0f, $"control: friction should stall a 3 m/s ball resting on the platform; it rolled {moved:0.00} m in 1 s");
            }
            finally
            {
                Remove(wo);
            }
        }

        /// <summary>The same ball prepared as a hazard (Missile) keeps its hover z, is not OnWalkable, and rolls.</summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void HazardBallOverAPlatform_KeepsItsHoverZ_AndRolls()
        {
            RequireDats();

            foreach (var z in new[] { HoverZ, HoverZ + 1f }) // ball_z 2 and 3, the owner's two retests
            {
                var wo = MakeBall(EastBlockCell, new Vector3(29.6f, -20f, z));

                try
                {
                    Spawn(wo, missile: true);

                    Assert.AreEqual(z, wo.Location.PositionZ, 1e-3f, $"ball_z hover kept (asked {z})");
                    Assert.IsFalse(wo.PhysicsObj.TransientState.HasFlag(TransientStateFlags.OnWalkable), $"not resting on the platform ({wo.PhysicsObj.TransientState})");

                    var moved = Roll(wo);
                    Assert.AreEqual(3.0f, moved, 0.2f, $"a 3 m/s hazard ball rolls ~3 m in 1 s over the platform at z {z}");

                    Assert.IsTrue(wo.PhysicsObj.State.HasFlag(PhysicsState.Missile), "PrepareMovingBody must reach PhysicsObj.State (what CreateObject serializes)");
                }
                finally
                {
                    Remove(wo);
                }
            }
        }

        /// <summary>
        /// A leg-end snap (HazardMotion.Snap's exact SetPosition flags, which include Placement) of a prepared hazard
        /// ball to a hover point over the east block and over the bridge keeps the hover z: a Missile is not stepped
        /// down by a snap either. Park afterwards leaves it inactive and still a Missile.
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void HazardBallSnap_OverAPlatform_KeepsItsHoverZ_AndParkLeavesItAnInactiveMissile()
        {
            RequireDats();

            var wo = MakeBall(EastBlockCell, new Vector3(29.6f, -20f, HoverZ));

            try
            {
                Spawn(wo, missile: true);

                foreach (var (cell, target) in new[] { (EastBlockCell, new Vector3(26.0f, -20f, HoverZ)), (0x00690116u, new Vector3(20f, -20f, HoverZ)) })
                {
                    WithClientPhysics(() =>
                    {
                        var setPosition = new ACE.Server.Physics.Common.SetPosition(0);
                        setPosition.Pos = new ACE.Server.Physics.Common.Position(new Position(cell, target.X, target.Y, target.Z, 0f, 0f, 0f, 1f, 0));
                        setPosition.Flags = HazardMotion.SnapFlags;

                        var result = wo.PhysicsObj.SetPosition(setPosition);
                        wo.SyncLocation();

                        Assert.AreEqual(ACE.Server.Physics.Common.SetPositionError.OK, result, $"snap to {target} in 0x{cell:X8}");
                    });

                    Assert.AreEqual(target.Z, wo.PhysicsObj.Position.Frame.Origin.Z, 1e-3f, $"snap to {target}: hover z kept");
                    Assert.AreEqual(target.X, wo.PhysicsObj.Position.Frame.Origin.X, 1e-3f, $"snap to {target}: not slid");
                    Assert.AreEqual(cell, wo.PhysicsObj.Position.ObjCellID, $"snap to {target}: cell");

                    HazardMotion.Park(wo);

                    Assert.IsFalse(wo.PhysicsObj.is_active(), "parked after the snap");
                    Assert.IsTrue(wo.PhysicsObj.State.HasFlag(PhysicsState.Missile), "a snap does not strip the Missile bit");
                }
            }
            finally
            {
                Remove(wo);
            }
        }

        /// <summary>
        /// Review 2026-11-06: the floor plates became Static (ForcePhysicsStatic), and a Static target is collided with even
        /// by an ethereal mover, so the hazard bodies that move over the plates must still clear them. With the shipped plates
        /// present, a prepared ball (Rolling Death, radius 1.8 at the default ball_z 2: centre -45.75, bottom -47.55) and a
        /// prepared wisp (Ember Wisp 1006950, setup 0x020009DB, origin at its feet, at ground + 0.5 = -47.25) are spawned and
        /// snapped (HazardMotion.SnapFlags) over the pit and over the east block: each snap returns OK and keeps its z.
        /// CONTROL: a ball snapped low enough to cut into a plate (centre -46.60, bottom -48.40, plate z -47.90 to -47.75)
        /// over the pit keeps that z through plates WITHOUT the bool, and is NOT kept through the Static plates, so the
        /// clearance assertions can fail.
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void HazardBallAndWispSnaps_OverTheStaticPlates_KeepTheirZ_AndALowBallIsObstructed()
        {
            RequireDats();

            const float WispZ = -47.25f;   // ground -47.75 + 0.5
            const float LowBallZ = -46.60f;
            var pit = new Vector3(29.6f, -10f, 0f);
            var block = new Vector3(29.6f, -20f, 0f);

            var plates = SpawnFloorPlates();

            try
            {
                foreach (var (name, body, z) in new[]
                {
                    ("ball", MakeBall(PitCell, new Vector3(pit.X, pit.Y, HoverZ)), HoverZ),
                    ("wisp", MakeWisp(PitCell, new Vector3(pit.X, pit.Y, WispZ)), WispZ),
                })
                {
                    try
                    {
                        SpawnPrepared(body);
                        Assert.AreEqual(z, body.Location.PositionZ, 1e-3f, $"{name}: spawned over the Static plates at its z");

                        foreach (var (cell, xy) in new[] { (EastBlockCell, block), (PitCell, pit) })
                        {
                            var target = new Vector3(xy.X, xy.Y, z);
                            var result = Snap(body, cell, target);

                            Assert.AreEqual(ACE.Server.Physics.Common.SetPositionError.OK, result, $"{name}: snap to {target} in 0x{cell:X8}");
                            Assert.AreEqual(z, body.PhysicsObj.Position.Frame.Origin.Z, 1e-3f, $"{name}: snap to {target} keeps its z over the Static plates");
                            Assert.AreEqual(target.X, body.PhysicsObj.Position.Frame.Origin.X, 1e-3f, $"{name}: snap to {target}: not slid");
                        }
                    }
                    finally
                    {
                        Remove(body);
                    }
                }
            }
            finally
            {
                foreach (var wo in plates)
                    Remove(wo);
            }

            // CONTROL: the same low snap, through plates without the bool and then through the Static ones
            foreach (var forceStatic in new[] { false, true })
            {
                var controlPlates = SpawnFloorPlates(forceStatic);
                var ball = MakeBall(PitCell, new Vector3(pit.X, pit.Y, HoverZ));

                try
                {
                    SpawnPrepared(ball);

                    var target = new Vector3(pit.X, pit.Y, LowBallZ);
                    var result = Snap(ball, PitCell, target);
                    var kept = result == ACE.Server.Physics.Common.SetPositionError.OK && Math.Abs(ball.PhysicsObj.Position.Frame.Origin.Z - LowBallZ) < 1e-3f;

                    if (forceStatic)
                        Assert.IsFalse(kept, $"control: a ball cutting into a Static plate is obstructed (result {result}, z {ball.PhysicsObj.Position.Frame.Origin.Z:0.000}); if it is kept, the clearance checks above prove nothing");
                    else
                        Assert.IsTrue(kept, $"control: through plates without the bool the ethereal ball keeps the low z (result {result}, z {ball.PhysicsObj.Position.Frame.Origin.Z:0.000}), so the difference is the bool");
                }
                finally
                {
                    Remove(ball);

                    foreach (var wo in controlPlates)
                        Remove(wo);
                }
            }
        }

        private const uint WispSetup = 0x020009DB;     // 1006950 Ember Wisp, Setup
        private const int WispPhysicsState = 2068;    // 1006950: Ethereal, IgnoreCollisions, LightingOn

        private static GenericObject MakeWisp(uint cell, Vector3 at)
            => MakeGeneric(cell, at, Quaternion.Identity, WispSetup, WispPhysicsState, 1f, new Dictionary<PropertyBool, bool> { { PropertyBool.Stuck, true } });

        /// <summary>The hazard spawn path for any moving body (HazardContext.Spawn: PrepareMovingBody, then AddPhysicsObj).</summary>
        private static void SpawnPrepared(WorldObject wo)
        {
            WithClientPhysics(() =>
            {
                HazardMotion.PrepareMovingBody(wo);
                Assert.IsTrue(wo.AddPhysicsObj(), $"{wo.Name}: AddPhysicsObj failed - it did not spawn at all");
            });
        }

        /// <summary>A leg-end snap with HazardMotion.Snap's exact SetPosition flags, as HazardBallSnap_* does.</summary>
        private static ACE.Server.Physics.Common.SetPositionError Snap(WorldObject wo, uint cell, Vector3 target)
        {
            var result = ACE.Server.Physics.Common.SetPositionError.GeneralFailure;

            WithClientPhysics(() =>
            {
                var setPosition = new ACE.Server.Physics.Common.SetPosition(0);
                setPosition.Pos = new ACE.Server.Physics.Common.Position(new Position(cell, target.X, target.Y, target.Z, 0f, 0f, 0f, 1f, 0));
                setPosition.Flags = HazardMotion.SnapFlags;

                result = wo.PhysicsObj.SetPosition(setPosition);
                wo.SyncLocation();
            });

            return result;
        }

        /// <summary>Why only the platform lanes stuck: over the pit the floor is out of step-down reach, Missile or not.</summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void Control_PlainBallOverThePit_KeepsItsZ_AndRolls()
        {
            RequireDats();

            var wo = MakeBall(PitCell, new Vector3(29.6f, -10f, HoverZ));

            try
            {
                Spawn(wo, missile: false);

                Assert.AreEqual(HoverZ, wo.Location.PositionZ, 1e-3f);
                Assert.AreEqual(3.0f, Roll(wo), 0.2f);
            }
            finally
            {
                Remove(wo);
            }
        }

        // ================= item 3: wisps over the blocks =================

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void PinnacleWispHeight_EveryPointOfTheRegionHasACell_SoPlaceWispIsNeverBlocked()
        {
            RequireDats();

            var region = HazardRegionPresets.Pinnacle(0);
            var adjust = AdjustCell.Get(Landblock, 0);
            bool HasCell(Vector3 p) => HazardGround.TryRaiseToCell(v => adjust.GetCell(v), p, 0, out _, out _); // HazardContext.HasCell, raise false

            var missing = new List<Vector2>();

            for (var x = region.MinX; x <= region.MaxX + 1e-3f; x += 0.5f)
                for (var y = region.MinY; y <= region.MaxY + 1e-3f; y += 0.5f)
                    if (!HasCell(new Vector3(x, y, region.HazardZ(x, y))))
                        missing.Add(new Vector2(x, y));

            Assert.AreEqual(0, missing.Count, $"points of the region with no cell at wisp height {region.HazardZ(0f, 0f):0.00}: {string.Join(" ", missing.Take(10))}");

            // paths that cross every block and the bridge, corner to corner
            var paths = new[]
            {
                (new Vector2(-2f, -20f), new Vector2(33f, -20f)),
                (new Vector2(-2f, -33f), new Vector2(33f, -7f)),
                (new Vector2(30f, -33f), new Vector2(10f, -7f)),
            };

            foreach (var (a, b) in paths)
            {
                var placed = HazardGround.PlaceWisp(new Vector3(a, region.HazardZ(a.X, a.Y)), new Vector3(b, region.HazardZ(b.X, b.Y)), HasCell);

                Assert.IsFalse(placed.Blocked, $"{a} -> {b} must not meet a solid at wisp height");
                Assert.AreEqual(placed.Stand, placed.Pillar, $"{a} -> {b}: the telegraph (the wisp) stands where the pillar fires");
            }

            // control: the old pit plane -51 runs into the west block on the same first path
            var old = HazardGround.PlaceWisp(new Vector3(-2f, -20f, -51f), new Vector3(33f, -20f, -51f), HasCell);
            Assert.IsTrue(old.Blocked, "control: at -51 the path is blocked by the west block; if this fails the probe proves nothing");
            Assert.AreNotEqual(old.Stand, old.Pillar);
        }

        // ================= 2026-10-19 playtest: the encounter's spawns =================

        private const uint WardPlaneSetup = 0x02000C2E;   // 1006900 Pinnacle Ward Plane, PropertyDataId 1
        private const int WardPlaneState = 24;            // ReportCollisions, IgnoreCollisions (1006900.sql; pinned by WardPlaneWeenie_MatchesTheFixturePlates)
        private const bool WardPlaneForcesStatic = true;  // ForcePhysicsStatic (bool 9091) on 1006900.sql, same pin; false = the controls' pre-2026-11-06 plates
        private const uint PetSetup = 0x02001C26;         // 1007703 Ossuary Adept (a Custom Summons CombatPet), Setup
        private const uint PetMotionTable = 0x09000001;   // 1007703, MotionTable
        private const int PetPhysicsState = 1036;         // 1007703: ReportCollisions, Ethereal, Gravity
        private const float WardPlaneScale = 0.997f;
        private const uint GraelBodySetup = 0x020015A2;   // 1006904-1006907 all carry it (Setup, MotionTable 0x09000007, state 1032, scale 1)
        private const uint GraelMotionTable = 0x09000007;
        private const int GraelPhysicsState = 1032;       // ReportCollisions, Gravity
        private const uint HeadSetup = 0x0200036A;        // 1006909 Head of Grael (content/pinnacle-grael-encounter)
        private const uint OldHeadSetup = 0x020000E4;     // the setup that drew the head 6.1 m too high
        private const float HeadScale = 15.5404f;
        private const int HeadPhysicsState = 8;           // ReportCollisions

        /// <summary>The 20 boss-room floor plates exactly as Content/dungeons/pinnacle_arena.sql places them (rows 0x7006900B-0x7006901E).</summary>
        private static readonly (uint Cell, float X, float Y)[] FloorPlates =
        {
            (0x0069010F, 0.042f, -5.041f), (0x0069010F, 0.042f, -11.025f), (0x00690110, 0.042f, -17.009f), (0x00690110, 0.042f, -22.993f), (0x00690111, 0.042f, -28.977f),
            (0x00690112, 10.014f, -5.041f), (0x00690112, 10.014f, -11.025f), (0x00690113, 10.014f, -17.009f), (0x00690113, 10.014f, -22.993f), (0x00690114, 10.014f, -28.977f),
            (0x00690115, 19.986f, -5.041f), (0x00690115, 19.986f, -11.025f), (0x00690116, 19.986f, -17.009f), (0x00690116, 19.986f, -22.993f), (0x00690117, 19.986f, -28.977f),
            (0x00690118, 29.958f, -5.041f), (0x00690118, 29.958f, -11.025f), (0x00690119, 29.958f, -17.009f), (0x00690119, 29.958f, -22.993f), (0x0069011A, 29.958f, -28.977f),
        };

        private const float FloorPlateZ = -47.85f;

        /// <summary>
        /// Every encounter creature failed to spawn in the playtest (AddPhysicsObj false). Cause: the spawn origins sat at
        /// z -47.75, exactly the top of the floor Ward Planes, so each body started inside a plate. With the 20 plates
        /// placed through the real spawn path, every GraelData point spawns the real body (setup, motion table, physics
        /// state, scale of 1006904-1006907), and the old z -47.75 is refused at the same x, y (the control: without it
        /// this test would not prove the plates are what refuses).
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void GraelSpawnPoints_SpawnTheRealBody_OnTopOfTheFloorWardPlanes()
        {
            RequireDats();

            var plates = new List<WorldObject>();
            var placed = new List<WorldObject>();

            try
            {
                foreach (var (cell, x, y) in FloorPlates)
                {
                    var plate = MakeGeneric(cell, new Vector3(x, y, FloorPlateZ), new Quaternion(0.707107f, 0f, 0f, 0.707107f), WardPlaneSetup, WardPlaneState, WardPlaneScale,
                        WardPlaneBools(WardPlaneForcesStatic));
                    Assert.IsTrue(TrySpawn(plate), $"floor plate at ({x}, {y}) must spawn, or the test proves nothing");
                    plates.Add(plate);
                }

                var points = new List<(string Name, ACE.Server.Pinnacles.PinnaclePoint At)>
                {
                    ("grael", ACE.Server.Pinnacles.Grael.GraelData.GraelSpawn),
                };
                foreach (var v in ACE.Server.Pinnacles.Grael.GraelData.Variants)
                    points.Add((v.Tag, v.At));

                // the nine P3 variant points (wave 3: rounds 2-4 take the first five, round 5 all nine)
                for (var i = 0; i < ACE.Server.Pinnacles.Grael.GraelData.VariantPoints.Count; i++)
                    points.Add(($"variant point {i}", ACE.Server.Pinnacles.Grael.GraelData.VariantPoints[i]));

                foreach (var (name, at) in points)
                {
                    // control first: the playtest's z, same x and y
                    var old = MakeGraelBody(at.Cell, new Vector3(at.X, at.Y, -47.75f));
                    var oldSpawned = TrySpawn(old);
                    if (oldSpawned)
                        placed.Add(old);
                    Assert.IsFalse(oldSpawned, $"control: '{name}' at the playtest's z -47.75 must be refused by the floor plates; if it spawns, the plates are not what this test exercises");

                    var body = MakeGraelBody(at.Cell, new Vector3(at.X, at.Y, at.Z));
                    var spawned = TrySpawn(body);
                    if (spawned)
                        placed.Add(body);

                    Assert.IsTrue(spawned, $"'{name}' at ({at.X}, {at.Y}, {at.Z}) in 0x{at.Cell:X8} must spawn");
                    Assert.AreEqual(-47.745f, body.Location.PositionZ, 0.01f, $"'{name}' settles on the plate top");
                    Assert.AreEqual(at.X, body.Location.PositionX, 0.01f, $"'{name}' is not slid sideways");
                    Assert.AreEqual(at.Y, body.Location.PositionY, 0.01f, $"'{name}' is not slid sideways");

                    // take it out again so the next point (and the next control) meets only the plates
                    Remove(body);
                    placed.Remove(body);
                }
            }
            finally
            {
                foreach (var wo in placed)
                    Remove(wo);
                foreach (var wo in plates)
                    Remove(wo);
            }
        }

        private const uint MarkerSetup = 0x020009DB;      // 1006800-1006803 Hill Marker, Setup (the red wisp model)
        private const float MarkerScale = 0.35f;          // 1006800-1006803, DefaultScale
        private const int MarkerPhysicsState = 2069;      // 0x815: Static, Ethereal, IgnoreCollisions, LightingOn (1006800 Hill Marker Red.sql)

        /// <summary>The Hill Marker weenie's bools (1006800.sql); <paramref name="exact"/> adds SpawnAtExactPosition (9069), which only the SERVER honours.</summary>
        private static Dictionary<PropertyBool, bool> MarkerBools(bool exact)
        {
            var bools = new Dictionary<PropertyBool, bool>
            {
                { PropertyBool.Stuck, true }, { PropertyBool.IgnoreCollisions, true }, { PropertyBool.Ethereal, true }, { PropertyBool.GravityStatus, false },
                { PropertyBool.Attackable, false }, { PropertyBool.UiHidden, true },
            };

            if (exact)
                bools[PropertyBool.SpawnAtExactPosition] = true;

            return bools;
        }

        /// <summary>Spawns a real-shape Hill Marker at <paramref name="at"/> (its cell resolved from the dat) and reports whether it kept that exact spot.</summary>
        private static (bool Spawned, bool Kept, Vector3 Got) SpawnMarkerAt(Vector3 at, bool exact)
        {
            var cell = AdjustCell.Get(Landblock, 0).GetCell(at);
            Assert.IsTrue(cell.HasValue, $"the marker point {at} resolves to a cell of 0x{Landblock:X4}");

            var marker = MakeGeneric(cell.Value, at, Quaternion.Identity, MarkerSetup, MarkerPhysicsState, MarkerScale, MarkerBools(exact));

            try
            {
                var spawned = TrySpawn(marker);
                var got = new Vector3(marker.Location.PositionX, marker.Location.PositionY, marker.Location.PositionZ);
                var kept = spawned && Vector3.Distance(got, at) < 1e-3f;

                return (spawned, kept, got);
            }
            finally
            {
                Remove(marker);
            }
        }

        /// <summary>A recording <see cref="IMemoryWorld"/> over the real Pinnacle region: the ground point is PointFor(Ground) as HazardMemoryWorld computes it, every cell exists.</summary>
        private sealed class MarkerRecordingWorld : IMemoryWorld
        {
            private sealed class Piece : IMemoryPiece
            {
                public bool Alive => true;
            }

            public double Now { get; set; }
            public Random Rng { get; } = new Random(1);
            public HazardRegion Region { get; } = HazardRegionPresets.Pinnacle(0);
            public HazardArgs Args { get; } = HazardArgs.Parse(HazardDefaults.Memory);
            public float EdgeInset => (float)HazardDefaults.EdgeInset;

            public List<Vector3> Markers { get; } = new List<Vector3>();
            public List<string> Logged { get; } = new List<string>();
            public List<string> Ended { get; } = new List<string>();

            public Vector3 GroundPoint(float x, float y) => HazardGround.FollowPoint(Region, HazardFollow.Ground, 0f, new Vector3(x, y, Region.GroundZ(x, y)), null);
            public bool HasCell(Vector3 point, bool raise) => true;

            public IMemoryPiece SpawnMarker(uint wcid, Vector3 position, string tag)
            {
                Markers.Add(position);
                return new Piece();
            }

            public bool SpawnBeam(Vector3 point, uint script, double seconds, string tag, Quaternion orientation, bool raise) => true;
            public IMemoryPiece SpawnPillar(Vector3 point, double seconds, string tag) => null;
            public int Strike(IMemoryPiece pillar, Vector3 centre, float radius) => 0;
            public IReadOnlyList<(uint Id, Vector2 Position)> Players() => Array.Empty<(uint, Vector2)>();
            public string NameOf(uint id) => "P" + id;
            public void Say(string line) { }
            public void Log(string line) => Logged.Add(line);
            public void End(HazardOutcome outcome, string reason) => Ended.Add($"{outcome}: {reason}");
        }

        /// <summary>
        /// Playtest 2026-11-08: after the floor Ward Planes became Static (ForcePhysicsStatic, PR #1986) the colour memory
        /// pad rings stopped showing on clients, while the server still logged all 44 markers spawned. The markers are
        /// SpawnAtExactPosition (bool 9069), which only the SERVER honours; the client runs the normal placement, which is
        /// what the server's physics is ported from, so a marker built WITHOUT the bool models what the client does with
        /// it. The marker setup's lower sphere (r 0.48 centred 0.475 above the origin at scale 1) has its bottom 1.75 mm UNDER
        /// the origin at scale 0.35, so at the ground plane -47.75 it cuts into the plate top, and a Static plate is collided with even by an ethereal body.
        ///
        /// CONTROLS on the same pad ring point over the pit at the ground plane: kept through plates WITHOUT the bool
        /// (the difference is the bool), kept through the Static plates WITH SpawnAtExactPosition (why the server logs
        /// every marker), NOT kept through the Static plates without it (the client's view; if this is kept the test
        /// cannot see the bug). THE FIX: every marker point the mechanic actually spawns (MemoryMechanic.StartCore over
        /// the real Pinnacle region) is PLACED through the Static plates by the client-model placement, not slid, and rests
        /// on the plate top (measured 2026-11-09: asked -47.70, settled -47.74855, as the Grael bodies settle at -47.745).
        /// Before MemoryMechanic.MarkerLift the mechanic asked -47.75 and every marker was refused.
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void MemoryPadMarkers_OverTheStaticPlates_KeepTheirSpot_InTheClientModelPlacement()
        {
            RequireDats();

            // the setup numbers the hypothesis rests on
            var setup = DatManager.PortalDat.ReadFromDat<ACE.DatLoader.FileTypes.SetupModel>(MarkerSetup);
            Assert.IsTrue(setup.Spheres.Count > 0, "the marker setup has collision spheres");
            var lowest = setup.Spheres.Min(s => (s.Origin.Z - s.Radius) * MarkerScale);
            Assert.IsTrue(lowest <= 0f && lowest > -0.01f, $"the marker's lowest sphere bottom sits at (a hair under) its origin at scale {MarkerScale}: {lowest:0.00000} (spheres: {string.Join(" ", setup.Spheres.Select(s => $"r{s.Radius:0.###}@z{s.Origin.Z:0.###}"))})");

            var world = new MarkerRecordingWorld();
            var mechanic = new MemoryMechanic();
            mechanic.StartCore(world);

            Assert.AreEqual(0, world.Ended.Count, "the run starts: " + string.Join(" | ", world.Ended));
            Assert.AreEqual(44, world.Markers.Count, "4 pads x 11 markers, as the prod log's markers=44 per-pad=11");

            var ground = world.Region.GroundZ(0f, 0f);
            Assert.AreEqual(-47.75f, ground, 1e-4f, "the Pinnacle ground plane");

            var probe = new Vector3(world.Markers[0].X, world.Markers[0].Y, ground);

            // CONTROL 1: plates without the bool keep the marker at the ground plane
            var plates = SpawnFloorPlates(forceStatic: false);
            try
            {
                var r = SpawnMarkerAt(probe, exact: false);
                Assert.IsTrue(r.Kept, $"control: through plates without the bool a marker at the ground plane {probe} keeps its spot (spawned {r.Spawned}, got {r.Got}), so the difference is the bool");
            }
            finally
            {
                foreach (var wo in plates)
                    Remove(wo);
            }

            plates = SpawnFloorPlates();
            try
            {
                // CONTROL 2: the server's own path (SpawnAtExactPosition) keeps it: why the server logs all 44
                var server = SpawnMarkerAt(probe, exact: true);
                Assert.IsTrue(server.Kept, $"control: with SpawnAtExactPosition the server keeps the marker at {probe} (spawned {server.Spawned}, got {server.Got})");

                // CONTROL 3: the client-model placement at the ground plane is disturbed by the Static plates
                var client = SpawnMarkerAt(probe, exact: false);
                Assert.IsFalse(client.Kept, $"control: the client-model placement of a marker at the ground plane {probe} through the Static plates is refused or moved (spawned {client.Spawned}, got {client.Got}); if it is kept, the bug is not this");

                // THE FIX: every point the mechanic spawns is placed (not refused), not slid, and rests on the plate top
                // (stepped down the few mm from the lifted z, as the Grael bodies settle at -47.745 from GraelData.SpawnZ)
                foreach (var at in world.Markers)
                {
                    var r = SpawnMarkerAt(at, exact: false);
                    var what = $"the mechanic's marker at {at} (ground {ground}, offset {at.Z - ground:0.000}) in the client-model placement through the Static plates (spawned {r.Spawned}, got {r.Got.X:0.0000}, {r.Got.Y:0.0000}, {r.Got.Z:0.00000})";

                    Assert.IsTrue(at.Z > ground, $"the marker at {at} is asked above the plate top {ground}");
                    Assert.IsTrue(r.Spawned, what + ": placed, not refused");
                    Assert.AreEqual(at.X, r.Got.X, 1e-3f, what + ": not slid");
                    Assert.AreEqual(at.Y, r.Got.Y, 1e-3f, what + ": not slid");
                    Assert.IsTrue(r.Got.Z >= ground - 0.01f && r.Got.Z <= at.Z + 1e-3f, what + ": rests between the plate top and its asked z");
                }
            }
            finally
            {
                foreach (var wo in plates)
                    Remove(wo);
            }
        }

        /// <summary>
        /// Dat-free pin for the RequiresDatFiles test above (which skips in CI): the pad markers are lifted, and a marker at
        /// the Pinnacle ground point plus the lift sits above the floor plate top (-47.75), with its lower sphere's bottom
        /// (1.75 mm under the origin at scale 0.35, measured from setup 0x020009DB) clear of it.
        /// </summary>
        [TestMethod]
        public void MemoryMarkerLift_PutsThePinnacleMarkersAboveThePlateTop()
        {
            const float PlateTop = -47.75f;           // FloorPlateZ -47.85 + the plate's 0.10 thickness; the Pinnacle ground plane
            const float SphereBottomUnderOrigin = 0.00175f;

            Assert.IsTrue(MemoryMechanic.MarkerLift > 0f, "the markers are lifted off the ground point");

            var ground = HazardRegionPresets.Pinnacle(0).GroundZ(0f, 0f);
            Assert.AreEqual(PlateTop, ground, 1e-4f, "the Pinnacle ground plane is the plate top");
            Assert.IsTrue(ground + MemoryMechanic.MarkerLift - SphereBottomUnderOrigin > PlateTop + 0.01f,
                $"a marker at ground + {MemoryMechanic.MarkerLift} keeps its lowest sphere more than 1 cm above the plate top {PlateTop}");
            Assert.IsTrue(ground + MemoryMechanic.MarkerLift <= ACE.Server.Pinnacles.Grael.GraelData.SpawnZ + 1e-4f,
                "no higher than the arena's proven spawn height (GraelData.SpawnZ)");
        }

        private const uint CacheSetup = 0x02000F7A;       // 1007005 Pinnacle Cache.sql, Setup (retail Ornate Chest)
        private const float CacheScale = 2.0f;            // 1007005 Pinnacle Cache.sql, DefaultScale
        private const int CacheState = 1052;              // 1007005's 1048 (ReportCollisions, IgnoreCollisions, Gravity) + Ethereal, set by PinnacleCache
        private const uint ExitSetup = 0x020001B3;        // 1003604 Thread Exit (Summoned).sql, Setup
        private const int ExitState = 3076;               // Ethereal, Gravity, LightingOn (1003604.sql)

        /// <summary>
        /// Playtest 2026-10-22: the Knot's cache and Knot Exit fell through the floor. Both are ethereal with gravity, and
        /// while the floor Ward Planes' physics bodies were not Static an ethereal body passed through them; they were stood
        /// at the owner, who is usually over the pit. CONTROL (plates without ForcePhysicsStatic): the real setup over the
        /// pit falls past the plates, and at GraelData's fixed point (the head's west block) it passes through the plate and
        /// rests on the block top (-48). WITH the shipped plates (ForcePhysicsStatic, 2026-11-06) both points rest on the
        /// plate top instead.
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void KnotCacheAndExitPoints_RestOnTheStaticPlates_AndFallThroughNonStaticOnes()
        {
            RequireDats();

            var data = ACE.Server.Pinnacles.Grael.GraelData.Script();
            Assert.AreEqual(ACE.Server.Pinnacles.Grael.GraelData.CachePoint, data.CachePoint, "the script data carries the cache point");
            Assert.AreEqual(ACE.Server.Pinnacles.Grael.GraelData.ReturnPortalPoint, data.ReturnPortalPoint, "the script data carries the exit point");

            var bodies = new[]
            {
                ("cache", CacheSetup, CacheScale, CacheState, ACE.Server.Pinnacles.Grael.GraelData.CachePoint),
                ("exit", ExitSetup, 1f, ExitState, ACE.Server.Pinnacles.Grael.GraelData.ReturnPortalPoint),
            };

            // CONTROL: the pre-fix, non-Static plates
            var plates = SpawnFloorPlates(forceStatic: false);

            try
            {
                foreach (var (name, setup, scale, state, at) in bodies)
                {
                    var (pit, _) = DropAndSettle(MakeDropped(PitCell, new Vector3(29.6f, -10f, at.Z), setup, scale, state));
                    Assert.IsTrue(pit < -49f, $"control: with non-Static plates the {name} over the pit falls past them (z {pit:0.00}); if it does not, this test cannot see the bug");

                    var (block, _) = DropAndSettle(MakeDropped(at.Cell, new Vector3(at.X, at.Y, at.Z), setup, scale, state));
                    Assert.IsTrue(block > -48.2f && block < -47.9f, $"control: with non-Static plates the {name} passes through the plate onto the west block top (-48); z {block:0.0000}");
                }
            }
            finally
            {
                foreach (var wo in plates)
                    Remove(wo);
            }

            plates = SpawnFloorPlates();

            try
            {
                foreach (var (name, setup, scale, state, at) in bodies)
                {
                    var (pit, _) = DropAndSettle(MakeDropped(PitCell, new Vector3(29.6f, -10f, at.Z), setup, scale, state));
                    Assert.IsTrue(pit > -47.9f && pit < -47.5f, $"the {name} over the pit rests on the Static plate top, not the pit floor (-54); z {pit:0.0000}");

                    var (z, cell) = DropAndSettle(MakeDropped(at.Cell, new Vector3(at.X, at.Y, at.Z), setup, scale, state));
                    Assert.IsTrue(z > -47.9f && z < -47.5f, $"{name} at ({at.X}, {at.Y}) rests on the Static plate top over the west block; z {z:0.0000}");
                    Assert.AreEqual(at.Cell, cell, $"{name}: the authored cell is the one it rests in");
                }
            }
            finally
            {
                foreach (var wo in plates)
                    Remove(wo);
            }
        }

        /// <summary>
        /// 2026-11-06: player summons fell through the Grael floor. Pet.SetEphemeralValues sets Ethereal on every pet, and
        /// a non-Static Ward Plane is an ethereal obstruction to an ethereal mover (PhysicsObj.FindObjCollisions), so the
        /// body passed through. A real CombatPet (1007703 Ossuary Adept's setup, motion table and PhysicsState) dropped
        /// from SpawnZ with the shipped plates (ForcePhysicsStatic, so their bodies are Static) comes to rest on the plate
        /// top over the pit AND over a block. CONTROL: the same drop against plates without the bool (the same PhysicsState
        /// 24, a non-Static body) falls to the pit floor and to the block top.
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void EtherealPet_DroppedOnTheStaticPlates_RestsOnThePlateTop_OverThePitAndABlock()
        {
            RequireDats();

            var points = new[]
            {
                ("over the pit", PitCell, new Vector3(29.6f, -10f, ACE.Server.Pinnacles.Grael.GraelData.SpawnZ), -54.0f),
                ("over the east block", EastBlockCell, new Vector3(30f, -20f, ACE.Server.Pinnacles.Grael.GraelData.SpawnZ), -48.0f),
            };

            foreach (var isStatic in new[] { false, true })
            {
                var plates = SpawnFloorPlates(forceStatic: isStatic);

                try
                {
                    Assert.AreEqual(isStatic, plates.All(p => p.PhysicsObj.State.HasFlag(PhysicsState.Static)), $"ForcePhysicsStatic {isStatic}: the plates' physics body carries Static exactly when the bool is set");

                    foreach (var (name, cell, at, floorZ) in points)
                    {
                        var pet = MakeCombatPet(cell, at);

                        try
                        {
                            Assert.IsTrue(TrySpawn(pet), $"{name}: the pet spawns at SpawnZ");
                            Assert.IsTrue(pet.PhysicsObj.State.HasFlag(PhysicsState.Ethereal), "the pet's physics body is ethereal, as Pet makes every pet");
                            Assert.IsTrue(pet.PhysicsObj.State.HasFlag(PhysicsState.Gravity), "and has gravity");

                            Settle(pet);

                            if (isStatic)
                            {
                                Assert.AreEqual(-47.745f, pet.Location.PositionZ, 0.01f, $"{name}: an ethereal pet rests on the Static plate top");
                                Assert.AreEqual(at.X, pet.Location.PositionX, 0.01f, $"{name}: not slid");
                                Assert.AreEqual(at.Y, pet.Location.PositionY, 0.01f, $"{name}: not slid");
                            }
                            else
                                Assert.AreEqual(floorZ, pet.Location.PositionZ, 0.02f, $"control {name}: with non-Static plates the ethereal pet passes through them to the floor below; if it does not, this test cannot see the bug");
                        }
                        finally
                        {
                            Remove(pet);
                        }
                    }
                }
                finally
                {
                    foreach (var wo in plates)
                        Remove(wo);
                }
            }
        }

        /// <summary>
        /// The dat tests above place hand-built plates with <see cref="WardPlaneState"/> and <see cref="WardPlaneBools"/>;
        /// this pins both to the shipped weenie (Content/sql/weenies/1006900 Pinnacle Ward Plane.sql): its PhysicsState
        /// (int 93) and every bool the fixture sets, ForcePhysicsStatic (9091) included. The content and the fixture can
        /// then never drift apart again. No dats needed.
        /// </summary>
        [TestMethod]
        public void WardPlaneWeenie_MatchesTheFixturePlates()
        {
            var sql = ACE.Server.Tests.ThreadDungeons.PooledLootSourceText.Read("Content/sql/weenies/1006900 Pinnacle Ward Plane.sql");
            var ints = new System.Text.RegularExpressions.Regex(@"\(\s*1006900\s*,\s*93\s*,\s*(?<v>\d+)\s*\)").Matches(sql);

            Assert.AreEqual(1, ints.Count, "exactly one PhysicsState (int 93) row in the Ward Plane weenie");

            var state = int.Parse(ints[0].Groups["v"].Value, System.Globalization.CultureInfo.InvariantCulture);

            Assert.AreEqual(WardPlaneState, state, "the fixture's WardPlaneState is the weenie's PhysicsState");
            Assert.IsFalse(((PhysicsState)state).HasFlag(PhysicsState.Ethereal), "never Ethereal, or nothing collides with it");

            var bools = new Dictionary<int, bool>();
            foreach (System.Text.RegularExpressions.Match m in new System.Text.RegularExpressions.Regex(@"\(\s*1006900\s*,\s*(?<k>\d+)\s*,\s*(?<v>True|False)\s*\)").Matches(sql))
                bools.Add(int.Parse(m.Groups["k"].Value, System.Globalization.CultureInfo.InvariantCulture), m.Groups["v"].Value == "True");

            Assert.IsTrue(bools.TryGetValue((int)PropertyBool.ForcePhysicsStatic, out var forced) && forced,
                "the weenie carries ForcePhysicsStatic True, or ethereal bodies (pets, the cache, the exit) fall through the floor");

            foreach (var (key, value) in WardPlaneBools(WardPlaneForcesStatic))
            {
                Assert.IsTrue(bools.TryGetValue((int)key, out var shipped), $"the weenie has a {key} ({(int)key}) row, as the fixture does");
                Assert.AreEqual(value, shipped, $"{key}: the fixture plate matches the weenie");
            }
        }
        /// <summary>
        /// The east-door seal is a runtime spawn of 1006900 (PinnacleFight seals at GraelData.SealPoint), so since
        /// 2026-11-06 its body is Static too. With the floor plates present: CONTROL, no seal, a human placed in the
        /// doorway at the seal's x keeps that x. With the Static seal spawned there, the same human is not let into the
        /// seal's slab (refused, or put clear of it), and neither is an ethereal pet, which a non-Static seal would have
        /// let through. After the seal leaves the world (the despawn path) its body is out of every cell and the human
        /// spawns in the doorway again. Placement only: walking into the seal is not simulated here.
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void DoorSeal_IsStatic_BlocksAHumanAndAPetInTheDoorway_AndLeavesCleanly()
        {
            RequireDats();

            var seal = ACE.Server.Pinnacles.Grael.GraelData.SealPoint;
            var plates = SpawnFloorPlates();
            var doorway = new Vector3(seal.X, seal.Y, ACE.Server.Pinnacles.Grael.GraelData.SpawnZ);
            const float SlabMinX = 34.75f, SlabMaxX = 34.90f;   // pinnacle_arena.sql WARD east rows: x 34.75 to 34.90

            bool InsideSlab(WorldObject wo, float radius) => wo.Location.PositionX + radius > SlabMinX && wo.Location.PositionX - radius < SlabMaxX;

            try
            {
                var control = MakeHuman(seal.Cell, doorway);
                Assert.IsTrue(TrySpawn(control), "control: with no seal a human spawns in the doorway");
                Assert.AreEqual(seal.X, control.Location.PositionX, 0.01f, "control: and keeps the seal's x, so the doorway itself does not push it");
                Remove(control);

                var ward = MakeGeneric(seal.Cell, new Vector3(seal.X, seal.Y, seal.Z), new Quaternion(seal.QX, seal.QY, seal.QZ, seal.QW), WardPlaneSetup, WardPlaneState, WardPlaneScale,
                    WardPlaneBools(WardPlaneForcesStatic));
                Assert.IsTrue(TrySpawn(ward), "the seal spawns at SealPoint");
                Assert.IsTrue(ward.PhysicsObj.State.HasFlag(PhysicsState.Static), "the seal's body is Static");

                try
                {
                    var human = MakeHuman(seal.Cell, doorway);
                    var humanIn = TrySpawn(human);
                    if (humanIn)
                    {
                        Assert.IsFalse(InsideSlab(human, human.PhysicsObj.GetPhysicsRadius()), $"the human is not let into the seal's slab (x {human.Location.PositionX:0.000}, r {human.PhysicsObj.GetPhysicsRadius():0.00})");
                        Remove(human);
                    }

                    var pet = MakeCombatPet(seal.Cell, doorway);
                    var petIn = TrySpawn(pet);
                    if (petIn)
                    {
                        Assert.IsFalse(InsideSlab(pet, pet.PhysicsObj.GetPhysicsRadius()), $"the ethereal pet is not let into the Static seal's slab (x {pet.Location.PositionX:0.000})");
                        Remove(pet);
                    }
                }
                finally
                {
                    Remove(ward);
                }

                Assert.IsNull(ward.PhysicsObj.CurCell, "the seal's body has left every cell");

                var after = MakeHuman(seal.Cell, doorway);
                Assert.IsTrue(TrySpawn(after), "after the seal leaves, a human spawns in the doorway again");
                Assert.AreEqual(seal.X, after.Location.PositionX, 0.01f, "at the seal's x");
                Remove(after);

                // CONTROL for the pet half: a seal WITHOUT the bool lets the ethereal pet straight into its slab
                var oldWard = MakeGeneric(seal.Cell, new Vector3(seal.X, seal.Y, seal.Z), new Quaternion(seal.QX, seal.QY, seal.QZ, seal.QW), WardPlaneSetup, WardPlaneState, WardPlaneScale,
                    WardPlaneBools(forceStatic: false));
                Assert.IsTrue(TrySpawn(oldWard), "control: the pre-2026-11-06 seal spawns");

                try
                {
                    var oldPet = MakeCombatPet(seal.Cell, doorway);
                    Assert.IsTrue(TrySpawn(oldPet), "control: the pet spawns");
                    Assert.AreEqual(seal.X, oldPet.Location.PositionX, 0.01f, "control: a non-Static seal lets the ethereal pet into its slab; if this fails the pet half above proves nothing");
                    Remove(oldPet);
                }
                finally
                {
                    Remove(oldWard);
                }
            }
            finally
            {
                foreach (var wo in plates)
                    Remove(wo);
            }
        }

        private static CombatPet MakeCombatPet(uint cell, Vector3 at)
        {
            var weenie = new Weenie
            {
                WeenieClassId = 999973,
                ClassName = "pinnaclearenatestpet",
                WeenieType = WeenieType.CombatPet,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Pinnacle Arena Test Pet" } },
                PropertiesDID = new Dictionary<PropertyDataId, uint> { { PropertyDataId.Setup, PetSetup }, { PropertyDataId.MotionTable, PetMotionTable } },
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.PhysicsState, PetPhysicsState } },
                PropertiesBool = new Dictionary<PropertyBool, bool> { { PropertyBool.Stuck, true }, { PropertyBool.ReportCollisions, true }, { PropertyBool.GravityStatus, true } },
                PropertiesFloat = new Dictionary<PropertyFloat, double> { { PropertyFloat.DefaultScale, 1.0 } },
            };

            TestGameTables.EnsureInitialized();   // Creature's ctor reads the vital formula tables

            var wo = new CombatPet(weenie, new ObjectGuid(nextGuid++));   // Pet's ctor sets Ethereal, as live
            wo.Location = new Position(cell, at.X, at.Y, at.Z, 0f, 0f, 0f, 1f, 0);
            return wo;
        }

        private static GenericObject MakeDropped(uint cell, Vector3 at, uint setup, float scale, int state)
            => MakeGeneric(cell, at, Quaternion.Identity, setup, state, scale, new Dictionary<PropertyBool, bool>
            {
                { PropertyBool.Stuck, true }, { PropertyBool.Ethereal, true }, { PropertyBool.GravityStatus, true },
            });

        /// <summary>Spawns the body, runs 4 s of physics (40 x 0.1 s) and removes it; returns its final z and cell.</summary>
        private static (float Z, uint Cell) DropAndSettle(WorldObject wo)
        {
            Assert.IsTrue(TrySpawn(wo), $"{wo.Name} at {wo.Location} did not spawn at all");

            try
            {
                WithClientPhysics(() =>
                {
                    wo.PhysicsObj.set_active(true);

                    for (var i = 0; i < 40; i++)
                        wo.PhysicsObj.UpdateObjectInternal(0.1, 0);
                });

                return (wo.PhysicsObj.Position.Frame.Origin.Z, wo.PhysicsObj.Position.ObjCellID);
            }
            finally
            {
                Remove(wo);
            }
        }

        private const uint EffectHostSetup = 0x02000176;  // 1006952 Hazard Effect Host, PropertyDataId 1
        private const int EffectHostState = 53;           // Static, Ethereal, IgnoreCollisions, NoDraw (1006952.sql)

        /// <summary>
        /// WHY GRAEL NEVER HIT THE MATRON FILL ABORT (2026-10-27). The default doubled fill on the Pinnacle preset puts
        /// floor_z at ground -47.75 + 0.05 = -47.70, but this arena's cell floor under the plates is the block tops
        /// (-48.00) or the pit (-54.00): 0.30 m or more under every face-down panel, past HazardPanels.FaceClearance.
        /// So the real director lifts nothing here. MatronFillSpawnTests is the room where floor_z is 0.05 m over the cell
        /// floor.
        ///
        /// 2026-11-06: the floor plates' bodies became Static (ForcePhysicsStatic on 1006900). The fill panels are
        /// ethereal, and an ethereal body is no longer let through a Static plate, so a face-down panel at -47.70 (whose
        /// geometry reaches into the plate top at -47.75) is refused by the placement, and the director then takes the tile's
        /// face-up partner out again: every tile is missed and no fill sheet stands over the floor. ACCEPTED by the owner 2026-11-06:
        /// Grael's script does not run the fill, and no hazard code was changed for it. CONTROL: with plates WITHOUT the
        /// bool (the pre-2026-11-06 floor) every tile still enters both faces exactly, so the difference is the bool.
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void DefaultDoubledFill_OverTheFloorPlates_FaceDownPanelsAreRefusedOnlyByStaticPlates()
        {
            RequireDats();

            var region = HazardRegionPresets.Pinnacle(0);
            var fill = FillSettings.From(HazardArgs.Empty, region);
            var tiles = FillRules.SheetTiles(region, fill.PanelSize, fill.SheetMargin);

            Assert.IsTrue(fill.Doubled, "the default sheet is doubled");
            Assert.AreEqual(-47.70, fill.FloorZ, 1e-6, "the default floor_z");

            foreach (var forceStatic in new[] { false, true })
            {
                var plates = SpawnFloorPlates(forceStatic);
                var port = new MatronFillSpawnTests.DatFillPort(MatronFillSpawnTests.Lookup(Landblock), fill);

                try
                {
                    var director = new FillDirector<WorldObject, string>(region, fill, port);

                    director.Start(0);

                    Assert.AreEqual(0, port.Logs.Count(l => l.Contains("draws its panels")), $"ForcePhysicsStatic {forceStatic}: nothing is lifted: " + string.Join(" | ", port.Logs));

                    foreach (var (body, asked, faceDown) in port.Placed)
                    {
                        var at = new Vector3(body.Location.PositionX, body.Location.PositionY, body.Location.PositionZ);
                        Assert.IsTrue(Vector3.Distance(at, asked) < 1e-3f, $"{(faceDown ? "down" : "up")} panel asked at {asked} rests at {at}");
                        Assert.AreEqual(-47.70f, asked.Z, 1e-4f, "every face at floor_z, one plane");
                    }

                    if (!forceStatic)
                    {
                        Assert.AreEqual(0, director.TilesMissed, "control: without the bool every tile enters: " + string.Join(" | ", port.Refusals));
                        Assert.AreEqual(2 * tiles.Count, port.Placed.Count, "control: both faces of every tile");
                    }
                    else
                    {
                        Assert.AreEqual(tiles.Count, director.TilesMissed, "every tile misses its face-down panel over the Static plates: " + string.Join(" | ", port.Refusals));
                        Assert.AreEqual(tiles.Count, port.Refusals.Count, "one refusal per tile: " + string.Join(" | ", port.Refusals));
                        Assert.IsTrue(port.Refusals.All(r => r.StartsWith("down ")), "only face-down panels are refused: " + string.Join(" | ", port.Refusals));
                        Assert.AreEqual(0, port.Placed.Count, "the director takes each missed tile's face-up partner out again, so no fill panel stands over the plates");
                    }
                }
                finally
                {
                    port.RemoveAll();

                    foreach (var p in plates)
                        Remove(p);
                }
            }
        }
        /// <summary>
        /// Review 2026-10-20: the four P4 corner pillars (zones / corners) at exactly the points ZonesMechanic.Start uses
        /// (ZonesMechanic.PillarPoints: each Pinnacle corner clamped edge_inset inside, at the ground plane + 0.05 =
        /// -47.70), each spawned as the real effect host (1006952's setup and state) with the 20 floor plates present.
        /// A corner that fails here is a safe spot in the live fight.
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void CornerPillarPoints_SpawnTheEffectHost_AtAllFourCorners()
        {
            RequireDats();

            var region = HazardRegionPresets.Pinnacle(0);
            var points = ZonesMechanic.PillarPoints(region, (float)HazardDefaults.EdgeInset, (x, y) => region.GroundZ(x, y));
            var adjust = AdjustCell.Get(Landblock, 0);
            var plates = new List<WorldObject>();
            var placed = new List<WorldObject>();

            Assert.AreEqual(4, points.Count, "one pillar per corner");
            Assert.IsFalse(adjust.GetCell(new Vector3(120f, 120f, -47.70f)).HasValue, "control: a point off the dungeon has no cell, so the cell check below discriminates");
            Assert.IsTrue(points.All(p => Math.Abs(p.Z - (-47.70f)) < 1e-4f), "every pillar at -47.70");

            try
            {
                foreach (var (cell, x, y) in FloorPlates)
                {
                    var plate = MakeGeneric(cell, new Vector3(x, y, FloorPlateZ), new Quaternion(0.707107f, 0f, 0f, 0.707107f), WardPlaneSetup, WardPlaneState, WardPlaneScale,
                        WardPlaneBools(WardPlaneForcesStatic));
                    Assert.IsTrue(TrySpawn(plate), $"floor plate at ({x}, {y}) must spawn, or the test proves nothing");
                    plates.Add(plate);
                }

                foreach (var p in points)
                {
                    var cell = adjust.GetCell(p);
                    Assert.IsTrue(cell.HasValue, $"corner pillar ({p.X}, {p.Y}, {p.Z}) lies in a cell");

                    var host = MakeGeneric(cell.Value, p, Quaternion.Identity, EffectHostSetup, EffectHostState, 1f,
                        new Dictionary<PropertyBool, bool> { { PropertyBool.Stuck, true }, { PropertyBool.IgnoreCollisions, true }, { PropertyBool.Ethereal, true }, { PropertyBool.GravityStatus, false } });
                    var spawned = TrySpawn(host);

                    if (spawned)
                        placed.Add(host);

                    Assert.IsTrue(spawned, $"corner pillar host at ({p.X}, {p.Y}, {p.Z}) in 0x{cell.Value:X8} must spawn");
                    Assert.AreEqual(p.X, host.Location.PositionX, 0.01f, "not moved");
                    Assert.AreEqual(p.Y, host.Location.PositionY, 0.01f, "not moved");
                }

                Assert.AreEqual(4, placed.Count);
            }
            finally
            {
                foreach (var wo in placed)
                    Remove(wo);
                foreach (var wo in plates)
                    Remove(wo);
            }
        }

        /// <summary>
        /// Round 4 playtest: the memory game's corner beams (beam_corners=1, MemoryLayout.CornerBeamPoints with the live
        /// quadrant pads). Each corner is the zone-pillar corner (edge_inset inside), over the floor (ground -47.75, the
        /// -47.70 floor point in a cell), and its CEILING host (MemoryLayout.CeilingBeamPoint, checked and spawned with no
        /// raise exactly as MemoryMechanic.FireBeams does) lies in a cell and spawns as the real effect host, unturned.
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void MemoryCornerBeams_StandOverTheFloor_AndTheirCeilingHostsSpawn_AtAllFourCorners()
        {
            RequireDats();

            var region = HazardRegionPresets.Pinnacle(0);
            var pads = MemoryLayout.QuadrantCentres(region);
            var corners = MemoryLayout.CornerBeamPoints(region, pads, (float)MemorySettings.DefaultPadRadius, (float)HazardDefaults.EdgeInset);
            var pillars = ZonesMechanic.PillarPoints(region, (float)HazardDefaults.EdgeInset, (x, y) => region.GroundZ(x, y));
            var adjust = AdjustCell.Get(Landblock, 0);
            bool HasCell(Vector3 p) => HazardGround.TryRaiseToCell(v => adjust.GetCell(v), p, 0, out _, out _); // HazardContext.HasCell, raise false

            Assert.AreEqual(4, corners.Count, "a beam in every corner");
            Assert.IsTrue(region.CeilingZ.HasValue, "the memory game fires from the ceiling here");
            Assert.IsFalse(HasCell(new Vector3(120f, 120f, region.CeilingZ.Value - MemoryLayout.CeilingBeamOffset)), "control: off the dungeon there is no cell, so the check discriminates");

            var placed = new List<WorldObject>();

            try
            {
                for (var i = 0; i < 4; i++)
                {
                    var c = corners[i];

                    Assert.AreEqual(pillars[i].X, c.X, 1e-4f, "the zone pillar corner");
                    Assert.AreEqual(pillars[i].Y, c.Y, 1e-4f, "the zone pillar corner");
                    Assert.AreEqual(-47.75f, region.GroundZ(c.X, c.Y), 1e-3f, "over the floor");
                    Assert.IsTrue(HasCell(new Vector3(c.X, c.Y, -47.70f)), $"corner ({c.X}, {c.Y}): the floor point -47.70 lies in a cell");

                    var host = MemoryLayout.CeilingBeamPoint(c, region.CeilingZ.Value);
                    Assert.IsTrue(HasCell(host), $"corner ({c.X}, {c.Y}): the ceiling host ({host.Z}) lies in a cell");

                    var cell = adjust.GetCell(host);
                    var wo = MakeGeneric(cell.Value, host, MemoryLayout.BeamOrientation(MemoryLayout.BeamTurned(true, false)), EffectHostSetup, EffectHostState, 1f,
                        new Dictionary<PropertyBool, bool> { { PropertyBool.Stuck, true }, { PropertyBool.IgnoreCollisions, true }, { PropertyBool.Ethereal, true }, { PropertyBool.GravityStatus, false } });
                    var spawned = TrySpawn(wo);

                    if (spawned)
                        placed.Add(wo);

                    Assert.IsTrue(spawned, $"corner beam host at ({host.X}, {host.Y}, {host.Z}) in 0x{cell.Value:X8} must spawn");
                    Assert.AreEqual(host.X, wo.Location.PositionX, 0.01f, "not moved");
                    Assert.AreEqual(host.Y, wo.Location.PositionY, 0.01f, "not moved");
                    Assert.AreEqual(host.Z, wo.Location.PositionZ, 0.01f, "not raised");
                }
            }
            finally
            {
                foreach (var wo in placed)
                    Remove(wo);
            }
        }

        private const uint HumanSetup = 0x02000001;
        private const uint HumanMotionTable = 0x09000001;

        private static Creature MakeHuman(uint cell, Vector3 at)
            => MakeCreature(cell, at, Quaternion.Identity, HumanSetup, HumanMotionTable, GraelPhysicsState, 1f);

        /// <summary>Runs 4 s of physics (40 x 0.1 s) on a spawned body, then syncs its Location.</summary>
        private static void Settle(WorldObject wo)
        {
            WithClientPhysics(() =>
            {
                wo.PhysicsObj.set_active(true);

                for (var i = 0; i < 40; i++)
                    wo.PhysicsObj.UpdateObjectInternal(0.1, 0);

                wo.SyncLocation();
            });
        }

        /// <summary>
        /// The player's fight entry point (2026-10-22: z -46.20, one and a half metres above SpawnZ) lies in its authored cell, a human's
        /// height under the room's ceiling, and a human body spawned there with the 20 floor plates present comes to rest on
        /// the plate top in that cell after 4 s of physics, at the same x and y.
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void FightEntryPoint_IsInItsCellUnderTheCeiling_AndAHumanDroppedFromItSettlesOnThePlates()
        {
            RequireDats();

            var at = ACE.Server.Pinnacles.Grael.GraelData.FightEntryPoint;
            var adjust = AdjustCell.Get(Landblock, 0);

            Assert.AreEqual(-46.20f, at.Z, 1e-4f, "the raised entry z");
            Assert.AreEqual(at.Cell, adjust.GetCell(new Vector3(at.X, at.Y, at.Z)), "the entry point lies in its authored cell");
            Assert.IsFalse(adjust.GetCell(new Vector3(at.X, at.Y, -41.0f)).HasValue, "control: above the room (and its thin -41.2 caps) there is no cell, so the check discriminates");

            var plates = SpawnFloorPlates();
            WorldObject body = null;

            try
            {
                body = MakeHuman(at.Cell, new Vector3(at.X, at.Y, at.Z));

                Assert.IsTrue(TrySpawn(body), "the fight entry point must spawn a human body");
                Assert.IsTrue(at.Z + body.PhysicsObj.GetHeight() < RoomCeilingZ, $"the body's top ({at.Z + body.PhysicsObj.GetHeight():0.00}) is under the ceiling {RoomCeilingZ}");

                Settle(body);

                Assert.AreEqual(-47.745f, body.Location.PositionZ, 0.01f, "settles on the plate top");
                Assert.AreEqual(at.Cell, body.Location.Cell, "in the authored cell (at -47.75 it resolved to 0x00690127 instead)");
                Assert.AreEqual(at.X, body.Location.PositionX, 0.01f, "not slid");
                Assert.AreEqual(at.Y, body.Location.PositionY, 0.01f, "not slid");
            }
            finally
            {
                if (body != null)
                    Remove(body);
                foreach (var wo in plates)
                    Remove(wo);
            }
        }

        /// <summary>
        /// The 2026-10-22 stuck position. WITHOUT the plates (a client that has not loaded them yet: the unproven
        /// hypothesis), a human placed at the old entry z (SpawnZ) is stepped DOWN by the placement onto the east block's top,
        /// -47.995, the exact z prod logged. WITH the plates, that stuck z is inside a plate: a body there is refused, which is
        /// what a stuck client overlapping one looks like. The raised entry z (-46.20) is past the placement's step-down reach
        /// with no plates: it keeps its z (measured 2026-10-22: -46.70 is still stepped down, -46.45 is the first kept z).
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void StuckZ_IsTheBlockTopUnderAPlate_AndTheOldEntryStepsDownToIt_WithoutPlates()
        {
            RequireDats();

            var at = ACE.Server.Pinnacles.Grael.GraelData.FightEntryPoint;

            var old = MakeHuman(at.Cell, new Vector3(at.X, at.Y, ACE.Server.Pinnacles.Grael.GraelData.SpawnZ));
            Assert.IsTrue(TrySpawn(old), "control: the old entry spawns without plates");

            try
            {
                Assert.AreEqual(-47.995f, old.Location.PositionZ, 0.01f, "no plates: the placement steps the old entry down onto the block top");
            }
            finally
            {
                Remove(old);
            }

            // the step-down boundary, both sides: the earlier -46.70 (one metre up) is still in reach, the shipped entry z is not
            var inReach = MakeHuman(at.Cell, new Vector3(at.X, at.Y, -46.70f));
            Assert.IsTrue(TrySpawn(inReach));

            try
            {
                Assert.AreEqual(-47.995f, inReach.Location.PositionZ, 0.01f, "control: no plates, -46.70 is still stepped down onto the block top");
            }
            finally
            {
                Remove(inReach);
            }

            var raised = MakeHuman(at.Cell, new Vector3(at.X, at.Y, at.Z));
            Assert.IsTrue(TrySpawn(raised), "the shipped entry spawns without plates");

            try
            {
                Assert.AreEqual(at.Z, raised.Location.PositionZ, 0.01f, "no plates: the shipped entry z is out of step-down reach and keeps its height");
            }
            finally
            {
                Remove(raised);
            }

            var plates = SpawnFloorPlates();

            try
            {
                var stuck = MakeHuman(at.Cell, new Vector3(at.X, at.Y, -47.995f));
                var spawned = TrySpawn(stuck);

                if (spawned)
                    Remove(stuck);

                Assert.IsFalse(spawned, "with the plates, the stuck z overlaps plate 0x7006901C");
            }
            finally
            {
                foreach (var wo in plates)
                    Remove(wo);
            }
        }

        /// <summary>
        /// GraelData.PlateFloor's rectangle is the union of the 20 boss FLOOR plates exactly as Content/dungeons/pinnacle_arena.sql
        /// places them (rows 0x7006900B-0x7006901E, parsed from the file so content drift fails here; each flat plate spans x
        /// origin +-4.985 and y origin down to origin - 5.982), and its bottom is the plates' (origin z - 0.05). No dats needed.
        /// </summary>
        [TestMethod]
        public void PlateFloorRectangle_IsTheUnionOfTheArenaFloorPlates()
        {
            const float HalfWidth = 4.985f;
            const float Depth = 5.982f;

            var sql = ACE.Server.Tests.ThreadDungeons.PooledLootSourceText.Read("Content/dungeons/pinnacle_arena.sql");
            var row = new System.Text.RegularExpressions.Regex(
                @"\(\s*0x(?<guid>[0-9A-Fa-f]{8})\s*,\s*(?<wcid>\d+)\s*,\s*0x[0-9A-Fa-f]{8}\s*,\s*(?<x>-?[\d.]+)\s*,\s*(?<y>-?[\d.]+)\s*,\s*(?<z>-?[\d.]+)\s*,");

            var plates = new List<(float X, float Y, float Z)>();

            foreach (System.Text.RegularExpressions.Match m in row.Matches(sql))
            {
                var guid = Convert.ToUInt32(m.Groups["guid"].Value, 16);

                if (guid < 0x7006900B || guid > 0x7006901E)
                    continue;

                Assert.AreEqual("1006900", m.Groups["wcid"].Value, $"row 0x{guid:X8} is a Ward Plane");
                plates.Add((float.Parse(m.Groups["x"].Value, System.Globalization.CultureInfo.InvariantCulture),
                    float.Parse(m.Groups["y"].Value, System.Globalization.CultureInfo.InvariantCulture),
                    float.Parse(m.Groups["z"].Value, System.Globalization.CultureInfo.InvariantCulture)));
            }

            Assert.AreEqual(20, plates.Count, "the 20 boss FLOOR rows 0x7006900B-0x7006901E were all parsed");
            Assert.AreEqual(1, plates.Select(p => p.Z).Distinct().Count(), "every floor plate at one z");

            var floor = ACE.Server.Pinnacles.Grael.GraelData.PlateFloor;

            Assert.AreEqual(plates.Min(p => p.X - HalfWidth), floor.MinX, 1e-4f, "west edge");
            Assert.AreEqual(plates.Max(p => p.X + HalfWidth), floor.MaxX, 1e-4f, "east edge");
            Assert.AreEqual(plates.Min(p => p.Y - Depth), floor.MinY, 1e-4f, "south edge");
            Assert.AreEqual(plates.Max(p => p.Y), floor.MaxY, 1e-4f, "north edge");
            Assert.AreEqual(plates[0].Z - 0.05f, floor.BottomZ, 1e-4f, "plate bottom");

            // the hand-copied geometry array the dat tests place must match the content too
            Assert.AreEqual(plates.Count, FloorPlates.Length);
            for (var i = 0; i < plates.Count; i++)
            {
                Assert.AreEqual(plates[i].X, FloorPlates[i].X, 1e-4f, $"FloorPlates[{i}].X");
                Assert.AreEqual(plates[i].Y, FloorPlates[i].Y, 1e-4f, $"FloorPlates[{i}].Y");
            }
            Assert.AreEqual(plates[0].Z, FloorPlateZ, 1e-4f, "FloorPlateZ");
        }
        /// <summary>
        /// The plate-floor lift's destination (LivePinnacleWorld.LiftTarget, the same x and y at PlateLiftZ, its cell
        /// re-resolved) from the prod stuck point and from the pit floor: each lies in a cell, and a human spawned there with
        /// the plates present rests on the plate top.
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void PlateLiftTarget_FromTheStuckPointAndThePit_LandsAHumanOnThePlates()
        {
            RequireDats();

            var floor = ACE.Server.Pinnacles.Grael.GraelData.PlateFloor;
            var plates = SpawnFloorPlates();

            try
            {
                foreach (var (name, from) in new[]
                {
                    ("stuck on the east block", new Position(0x00690119, 32.5f, -20f, -47.995f, 0f, 0f, 0f, 1f, 0)),
                    ("on the pit floor", new Position(PitCell, 29.6f, -10f, -53.995f, 0f, 0f, 0f, 1f, 0)),
                })
                {
                    Assert.IsTrue(ACE.Server.Pinnacles.PinnaclePlateLift.IsUnderPlates(floor, from.PositionX, from.PositionY, from.PositionZ), $"{name}: the decision lifts it");

                    Position to = null;
                    WithClientPhysics(() => to = ACE.Server.Pinnacles.LivePinnacleWorld.LiftTarget(from, floor.LiftZ));

                    Assert.AreEqual(floor.LiftZ, to.PositionZ, 1e-4f, name);
                    Assert.AreEqual(from.PositionX, to.PositionX, 1e-4f, name);
                    Assert.AreEqual(from.PositionY, to.PositionY, 1e-4f, name);
                    Assert.AreEqual(AdjustCell.Get(Landblock, 0).GetCell(to.Pos), to.Cell, $"{name}: the cell is the lifted point's");

                    var body = MakeHuman(to.Cell, to.Pos);
                    Assert.IsTrue(TrySpawn(body), $"{name}: a human at the lift target spawns");

                    try
                    {
                        Assert.AreEqual(-47.745f, body.Location.PositionZ, 0.01f, $"{name}: rests on the plate top");
                        Assert.IsFalse(ACE.Server.Pinnacles.PinnaclePlateLift.IsUnderPlates(floor, body.Location.PositionX, body.Location.PositionY, body.Location.PositionZ), $"{name}: and is not lifted again");
                    }
                    finally
                    {
                        Remove(body);
                    }
                }
            }
            finally
            {
                foreach (var wo in plates)
                    Remove(wo);
            }
        }

        /// <summary>
        /// The Head of Grael was selectable but NOT VISIBLE in the playtest. Every body starts in placement frame 0x65
        /// (Resting; PartArray.CreateSetup / PhysicsObj.InitObjectEnd), and the previous head Setup 0x020000E4 (a
        /// retail item) defines a Resting frame that lifts its one part 0.395 along z: at the head's DefaultScale 15.5404
        /// the drawn head stood 6.1 m above its origin, wholly above the -42 ceiling and the black ceiling Shroud. The
        /// collision body stayed at the origin, which is why the head could still be targeted. 1006908's Setup
        /// 0x02000FB6 has only a Default frame (identity), which is why that prop was visible. The replacement
        /// 0x0200036A has only an identity Default frame and one cylsphere at the origin. The control shows the
        /// probe reads the frame the old Setup really applies.
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void HeadSetup_DrawsItsPartAtTheOrigin_AndSpawnsAtTheHeadPoint()
        {
            RequireDats();

            var at = ACE.Server.Pinnacles.Grael.GraelData.HeadPoint;
            var rot = new Quaternion(at.QX, at.QY, at.QZ, at.QW);

            var oldHead = MakeCreature(at.Cell, new Vector3(at.X, at.Y, at.Z), rot, OldHeadSetup, 0, HeadPhysicsState, HeadScale);
            var head = MakeCreature(at.Cell, new Vector3(at.X, at.Y, at.Z), rot, HeadSetup, HeadMotionTable, HeadPhysicsState, HeadScale);

            try
            {
                WithClientPhysics(() => { oldHead.InitPhysicsObj(); head.InitPhysicsObj(); });

                var oldLift = DrawnPartLift(oldHead);
                Assert.AreEqual(0.395f * HeadScale, oldLift, 0.05f, "control: 0x020000E4's Resting frame lifts the drawn part about 6.1 m; if this fails the probe is not reading the applied frame");

                Assert.AreEqual(0f, DrawnPartLift(head), 1e-4f, "the head's drawn part sits at the object origin");

                var cyl = head.PhysicsObj.PartArray.Setup.CylSphere;
                Assert.AreEqual(1, cyl.Count, "one collision cylsphere");
                Assert.AreEqual(0f, cyl[0].LowPoint.Length(), 1e-4f, "at the origin");

                Assert.IsTrue(TrySpawn(head), "the head spawns at GraelData.HeadPoint");
                Assert.AreEqual(at.Cell, head.Location.Cell);
                Assert.AreEqual(at.Z, head.Location.PositionZ, 0.01f, "no gravity: it floats where placed");

                // with its motion table (0x0900016A) the drawn part follows the Ready animation's frame, not the placement
                // frame: that frame is identity too, so the part still sits on the body
                Assert.IsNotNull(head.PhysicsObj.PartArray.MotionTableManager, "the motion table reached the body");
                Assert.AreEqual(0f, Vector3.Distance(head.PhysicsObj.PartArray.Parts[0].Pos.Frame.Origin, head.PhysicsObj.Position.Frame.Origin), 1e-4f, "the drawn part sits at the body origin");
                Assert.IsTrue(Math.Abs(Quaternion.Dot(head.PhysicsObj.PartArray.Parts[0].Pos.Frame.Orientation, head.PhysicsObj.Position.Frame.Orientation)) > 0.99999f, "and is not rotated off it");
                Assert.AreEqual(0.442f * HeadScale, head.PhysicsObj.GetHeight(), 0.01f, "the name label height (Setup.Height x scale) is unchanged");
            }
            finally
            {
                Remove(head);
                Remove(oldHead);
            }
        }

        // ================= round 3 playtest: the head's casts and its turn =================

        /// <summary>The ceiling of the boss room above the head (HazardRegionPresets.Pinnacle: ceiling -42.05).</summary>
        private const float RoomCeilingZ = -42.05f;

        /// <summary>
        /// The head's spells exactly as ace_world.spell holds them (queried 2026-10-20: wcid, projectile count, spread;
        /// every one has padding 1,1,1 and dims count,1,1), the projectile Setup of each wcid, and the type
        /// SpellProjectile.GetProjectileSpellType gives it (4423 is non-tracking: an arc).
        /// </summary>
        private static readonly (uint Id, uint Wcid, int Count, float Spread, ProjectileSpellType Type, uint ProjectileSetup)[] HeadSpells =
        {
            (4439, 1499, 1, 0f, ProjectileSpellType.Bolt, 0x0200050D),
            (4423, 20974, 1, 0f, ProjectileSpellType.Arc, 0x0200050D),
            (4440, 7263, 1, 0f, ProjectileSpellType.Streak, 0x0200050D),
            (6191, 7270, 9, 360f, ProjectileSpellType.Ring, 0x02000881),
        };

        private const int SpellProjectileState = 166728;   // 1499's PhysicsState (Missile, ReportCollisions, ...)

        /// <summary>
        /// Spells cast by the head hit the head itself (2026-10-20 playtest). The launch puts a bolt at 2/3 of the
        /// caster's collision Height and an arc at all of it (WorldObject.CalculatePreOffset); the head's column is 6.87 m
        /// in a room whose ceiling is 5.05 m above it. Then LaunchSpellProjectiles asks IsProjectileVisible, a line of
        /// sight from the projectile to the caster's "eye" at its Height, which is above the ceiling: the ceiling blocks
        /// it, the projectile is declared not visible and OnCollideEnvironment explodes it at once, beside the head. An
        /// arc's spawn point lies above the ceiling, in no cell at all. CONTROL (no override): exactly that. FIX
        /// (GraelData.HeadCastHeight on WorldObject.ProjectileCastHeight): every head spell spawns in a cell below the
        /// ceiling, clear of the head's column, and is seen by the head.
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void HeadCasts_LaunchInsideTheRoom_AndAreSeenByTheHead_OnlyWithTheCastHeight()
        {
            RequireDats();

            CollectionAssert.AreEquivalent(ACE.Server.Pinnacles.Grael.GraelData.P4Spells.ToArray(), HeadSpells.Select(s => s.Id).ToArray(), "every head projectile spell is checked here");

            var plates = SpawnFloorPlates();
            var placed = new List<WorldObject>();

            try
            {
                var at = ACE.Server.Pinnacles.Grael.GraelData.HeadPoint;
                var head = MakeCreature(at.Cell, new Vector3(at.X, at.Y, at.Z), new Quaternion(at.QX, at.QY, at.QZ, at.QW), HeadSetup, HeadMotionTable, HeadPhysicsState, HeadScale);
                Assert.IsTrue(TrySpawn(head));
                placed.Add(head);

                // the player, on the plates east of the head
                var target = MakeCreature(0x00690116, new Vector3(20f, -20f, ACE.Server.Pinnacles.Grael.GraelData.SpawnZ), Quaternion.Identity, 0x02000001, 0x09000001, GraelPhysicsState, 1f);
                Assert.IsTrue(TrySpawn(target));
                placed.Add(target);

                var column = head.PhysicsObj.GetPhysicsRadius();
                Assert.AreEqual(0.777f, column, 0.01f, "the head's column radius");

                // CONTROL: the retail launch height (no ProjectileCastHeight)
                Assert.IsNull(head.ProjectileCastHeight);
                var bolt = Launch(head, target, HeadSpells[0], placed);
                Assert.IsTrue(bolt.Spawned, "control: the bolt spawns (just under the ceiling)");
                Assert.IsFalse(bolt.Visible, $"control: the head cannot see its own bolt at {bolt.Spawn} (its eye point is above the ceiling), so the launch explodes it at the head; if this passes the control no longer reaches the playtest failure");
                var arc = Launch(head, target, HeadSpells[1], placed);
                Assert.IsTrue(arc.Spawn.Z > RoomCeilingZ, $"control: the arc spawns above the ceiling ({arc.Spawn.Z})");
                Assert.IsFalse(arc.HasCell, "control: in no cell at all");

                // FIX
                head.ProjectileCastHeight = ACE.Server.Pinnacles.Grael.GraelData.HeadCastHeight;

                foreach (var spell in HeadSpells)
                {
                    var r = Launch(head, target, spell, placed);
                    var name = $"{spell.Id} {spell.Type}";

                    Assert.IsTrue(r.HasCell, $"{name}: the spawn point {r.Spawn} lies in a cell");
                    Assert.IsTrue(r.Spawn.Z + r.Radius < RoomCeilingZ, $"{name}: below the ceiling ({r.Spawn.Z} + {r.Radius})");
                    Assert.IsTrue(r.FromAxis > column + r.Radius, $"{name}: clear of the head's column ({r.FromAxis:0.00} m from its axis, column {column:0.00} + projectile {r.Radius:0.00})");
                    Assert.IsTrue(r.Spawned, $"{name}: spawns");
                    Assert.IsTrue(r.Visible, $"{name}: the head sees it, so the launch keeps it");
                }
            }
            finally
            {
                foreach (var wo in placed)
                    Remove(wo);
                foreach (var wo in plates)
                    Remove(wo);
            }
        }

        private readonly struct LaunchResult
        {
            public Vector3 Spawn { get; init; }
            public float Radius { get; init; }
            public float FromAxis { get; init; }
            public bool HasCell { get; init; }
            public bool Spawned { get; init; }
            public bool Visible { get; init; }
        }

        /// <summary>
        /// The first projectile of <paramref name="s"/> exactly as WorldObject.LaunchSpellProjectiles places it (the real
        /// CalculateProjectileOrigins and the same turn toward the target), spawned as a body of the projectile's Setup,
        /// and the real IsProjectileVisible the launch asks. GetProjectileRadius reads the world DB, so its cache is filled
        /// from the dat (the projectile Setup's first sphere; none of these weenies carries a DefaultScale).
        /// </summary>
        private static LaunchResult Launch(Creature caster, Creature target, (uint Id, uint Wcid, int Count, float Spread, ProjectileSpellType Type, uint ProjectileSetup) s, List<WorldObject> placed)
        {
            var setup = DatManager.PortalDat.ReadFromDat<ACE.DatLoader.FileTypes.SetupModel>(s.ProjectileSetup);
            var radius = setup.Spheres[0].Radius;
            WorldObject.ProjectileRadiusCache[s.Wcid] = radius;

            var spell = new Spell(s.Id, false);
            spell._spell = new ACE.Database.Models.World.Spell
            {
                Id = s.Id, Wcid = s.Wcid, NumProjectiles = s.Count, SpreadAngle = s.Spread,
                PaddingOriginX = 1, PaddingOriginY = 1, PaddingOriginZ = 1, DimsOriginX = s.Count, DimsOriginY = 1, DimsOriginZ = 1,
            };

            List<Vector3> origins = null;
            WithClientPhysics(() => origins = caster.CalculateProjectileOrigins(spell, s.Type, target));

            var offset = caster.PhysicsObj.Position.GetOffset(target.PhysicsObj.Position);
            var rotate = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (float)Math.Atan2(-offset.X, offset.Y));
            var spawn = caster.Location.Pos + Vector3.Transform(origins[0], rotate);
            var fromAxis = new Vector2(spawn.X - caster.Location.PositionX, spawn.Y - caster.Location.PositionY).Length();

            var cell = AdjustCell.Get(Landblock, 0).GetCell(spawn);
            var spawned = false;
            var visible = false;

            if (cell.HasValue)
            {
                var proj = MakeGeneric(cell.Value, spawn, Quaternion.Identity, s.ProjectileSetup, SpellProjectileState, 1f, new Dictionary<PropertyBool, bool> { { PropertyBool.Stuck, true } });
                spawned = TrySpawn(proj);

                if (spawned)
                {
                    placed.Add(proj);
                    WithClientPhysics(() => visible = caster.IsProjectileVisible(proj));
                }
            }

            return new LaunchResult { Spawn = spawn, Radius = radius, FromAxis = fromAxis, HasCell = cell.HasValue, Spawned = spawned, Visible = visible };
        }

        /// <summary>
        /// The head never turned to face the player (2026-10-20, a fix that set the right yaw did not change that). The
        /// client applies a remote object's UpdatePosition through MoveOrTeleport (the port: PhysicsObj.MoveOrTeleport):
        /// a teleport sequence that is not newer is applied only for a pack flagged IsGrounded, and PositionPack sets that
        /// flag only for an OnWalkable body. The head floats (no gravity, 0.65 m above the block), so CONTROL: its plain
        /// update carries no IsGrounded and the client drops it, rotation unchanged. FIX: the teleport-sequenced update
        /// LivePinnacleWorld.ApplyRotation now sends (adminMove) is applied, whole frame.
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void HeadTurn_APlainUpdateIsDroppedByTheClient_ATeleportSequencedOneIsApplied()
        {
            RequireDats();

            var plates = SpawnFloorPlates();
            var at = ACE.Server.Pinnacles.Grael.GraelData.HeadPoint;
            var spawnRot = new Quaternion(at.QX, at.QY, at.QZ, at.QW);
            var head = MakeCreature(at.Cell, new Vector3(at.X, at.Y, at.Z), spawnRot, HeadSetup, HeadMotionTable, HeadPhysicsState, HeadScale);

            try
            {
                Assert.IsTrue(TrySpawn(head));
                Assert.IsFalse(head.PhysicsObj.TransientState.HasFlag(TransientStateFlags.OnWalkable), $"the head floats ({head.PhysicsObj.TransientState})");

                // turn it toward a player east of it, as FaceOwner does
                var q = ACE.Server.Pinnacles.PinnacleFacing.FaceToward(spawnRot, new Vector2(at.X, at.Y), new Vector2(20f, -30f), ACE.Server.Pinnacles.Grael.GraelData.HeadFaceYawOffset);
                Assert.IsTrue(Math.Abs(Quaternion.Dot(q, spawnRot)) < 0.999f, "the turn is a real change of rotation");

                head.Location.Rotation = q;

                // CONTROL: the plain update
                var plain = new ACE.Server.Network.Structure.PositionPack(head);
                Assert.IsFalse(plain.Flags.HasFlag(PositionFlags.IsGrounded), "a floating head's plain update is not grounded");
                Assert.IsFalse(ClientApplies(head, plain, q), "control: the client drops the plain update, so the head never turns; if this applies, the control no longer reaches the playtest failure");

                // FIX: the teleport-sequenced update
                var teleport = new ACE.Server.Network.Structure.PositionPack(head, adminMove: true);
                Assert.IsTrue(ClientApplies(head, teleport, q), "the teleport-sequenced update is applied, whole frame");
            }
            finally
            {
                Remove(head);
                foreach (var wo in plates)
                    Remove(wo);
            }
        }

        // ================= 2026-10-20 owner ruling: the client animates the head's turn =================

        private const uint HeadMotionTable = 0x0900016A;  // 1006909's MotionTable (King Toad Idol 0x0200112A's, a retail one-part statue)

        /// <summary>
        /// The head's motion table, read from the dat: (a) the default style is NonCombat, whose default substate is
        /// Ready, and it has a TurnRight cycle with a clockwise omega and no velocity; (b) EVERY animation any cycle, link
        /// or modifier of it can play holds part 0 at the identity frame on every frame, has no root motion (PosFrames)
        /// and no hooks, so nothing the table plays can displace the 15.5x-scaled head (a one-part Setup's part 0 IS the
        /// head); (c) its walk and run cycles carry no velocity, so even a walk would not translate the body (the only
        /// translating entry is the SideStepRight modifier, which the head's AI never issues: AiImmobile, NoAttack).
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void HeadMotionTable_TurnsInNonCombat_AndNoAnimationMovesItsOnePart()
        {
            RequireDats();

            var mt = DatManager.PortalDat.ReadFromDat<ACE.DatLoader.FileTypes.MotionTable>(HeadMotionTable);

            Assert.AreEqual((uint)MotionCommand.NonCombat, mt.DefaultStyle, "default style NonCombat");
            Assert.AreEqual((uint)MotionCommand.Ready, mt.StyleDefaults[mt.DefaultStyle], "NonCombat's default substate is Ready");

            var style = mt.DefaultStyle << 16;
            Assert.IsTrue(mt.Cycles.TryGetValue(style | ((uint)MotionCommand.TurnRight & 0xFFFFFF), out var turn), "(a) a NonCombat TurnRight cycle");
            Assert.AreEqual(-3.5f, turn.Omega.Z, 1e-4f, "(a) clockwise, 3.5 rad/s at speed 1");
            Assert.AreEqual(Vector3.Zero, turn.Velocity, "(a) the turn does not translate");
            Assert.AreEqual(-3.5f, ACE.Server.Physics.Animation.MotionTable.GetMotionData(HeadMotionTable, (uint)MotionCommand.TurnRight).Omega.Z, 1e-4f, "the server reads the same omega the live world turns by");

            foreach (var walk in new[] { MotionCommand.WalkForward, MotionCommand.RunForward })
                if (mt.Cycles.TryGetValue(style | ((uint)walk & 0xFFFFFF), out var w))
                    Assert.AreEqual(Vector3.Zero, w.Velocity, $"(c) {walk} carries no velocity");

            var all = new List<(string Where, ACE.DatLoader.Entity.MotionData Data)>();
            all.AddRange(mt.Cycles.Select(kv => ($"cycle 0x{kv.Key:X8}", kv.Value)));
            all.AddRange(mt.Modifiers.Select(kv => ($"modifier 0x{kv.Key:X8}", kv.Value)));
            all.AddRange(mt.Links.SelectMany(kv => kv.Value.Select(kv2 => ($"link 0x{kv.Key:X8} -> 0x{kv2.Key:X8}", kv2.Value))));

            var anims = 0;

            foreach (var (where, data) in all)
            {
                if (!where.StartsWith("modifier"))
                    Assert.AreEqual(Vector3.Zero, data.Velocity, $"{where}: no velocity");

                foreach (var ad in data.Anims)
                {
                    var anim = DatManager.PortalDat.ReadFromDat<ACE.DatLoader.FileTypes.Animation>(ad.AnimId);
                    anims++;

                    Assert.AreEqual(0, anim.PosFrames.Count, $"{where} anim 0x{ad.AnimId:X8}: no root motion");

                    for (var i = 0; i < anim.PartFrames.Count; i++)
                    {
                        var frame = anim.PartFrames[i];
                        Assert.AreEqual(0, frame.Hooks.Count, $"{where} anim 0x{ad.AnimId:X8} frame {i}: no hooks");
                        Assert.IsTrue(frame.Frames.Count >= 1, $"{where} anim 0x{ad.AnimId:X8} frame {i} animates part 0");
                        Assert.AreEqual(0f, frame.Frames[0].Origin.Length(), 1e-6f, $"{where} anim 0x{ad.AnimId:X8} frame {i}: part 0 origin");
                        Assert.IsTrue(Math.Abs(frame.Frames[0].Orientation.W) > 0.999999f, $"{where} anim 0x{ad.AnimId:X8} frame {i}: part 0 rotation {frame.Frames[0].Orientation}");
                    }
                }
            }

            Assert.IsTrue(anims > 10, $"the walk covered the table ({anims} animation references)");

            // the death: Creature.Die waits this long (ExecuteMotion's GetAnimationLength) before destroying the head;
            // with no motion table it was 0. The pose does not change (identity frames above), the head just stays drawn.
            Assert.AreEqual(59f / 30f, ACE.Server.Physics.Animation.MotionTable.GetAnimationLength(HeadMotionTable, MotionStance.NonCombat, MotionCommand.Ready, MotionCommand.Dead), 1e-3f, "Ready -> Dead: 59 frames at 30 a second");
        }

        /// <summary>
        /// The turn, played through the ported CLIENT physics exactly as a client plays the head's UpdateMotion (an
        /// interpreted state: Ready, TurnRight at the live world's signed speed), on the real arena geometry, with the
        /// head floating (not OnWalkable, no contact). The client turns it about its own up axis, in whole animation-frame
        /// steps, to exactly the rotation the server books (RotateAboutLocalUp by TurnLanding of LocalTurnToward), when the
        /// stop is sent at the time the server waits (TurnSeconds), without moving it and with its drawn part on the body
        /// frame throughout; the face then points at the owner within half a step. The Ready stop holds it there. The nod's teleport-sequenced rest frame then lands on that same rotation, does not set it turning
        /// again, and a second turn starts from it and lands on the second booked rotation. CONTROL: the same motion on
        /// the head without a motion table (the build before this one) does not turn it at all - which is why the head
        /// used to be turned by teleport.
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void HeadTurn_TheClientAnimatesTheMotionTurn_AndLandsWhereTheServerBooksIt()
        {
            RequireDats();

            var plates = SpawnFloorPlates();
            var at = ACE.Server.Pinnacles.Grael.GraelData.HeadPoint;
            var spawnRot = new Quaternion(at.QX, at.QY, at.QZ, at.QW);
            var headXY = new Vector2(at.X, at.Y);
            var offset = ACE.Server.Pinnacles.Grael.GraelData.HeadFaceYawOffset;

            var head = MakeCreature(at.Cell, new Vector3(at.X, at.Y, at.Z), spawnRot, HeadSetup, HeadMotionTable, HeadPhysicsState, HeadScale);
            var still = MakeCreature(at.Cell, new Vector3(at.X, at.Y, at.Z), spawnRot, HeadSetup, 0, HeadPhysicsState, HeadScale);

            try
            {
                Assert.IsTrue(TrySpawn(head));
                Assert.IsTrue(TrySpawn(still));
                Assert.IsFalse(head.PhysicsObj.TransientState.HasFlag(TransientStateFlags.OnWalkable), "the head floats");
                Assert.IsFalse(head.PhysicsObj.TransientState.HasFlag(TransientStateFlags.Contact), "and has no contact");

                var cycle = ACE.Server.Physics.Animation.MotionTable.GetMotionData(HeadMotionTable, (uint)MotionCommand.TurnRight);
                var omega = cycle.Omega.Z;
                var framerate = cycle.Anims[0].Framerate;
                var halfStep = (float)(ACE.Server.Pinnacles.PinnacleFacing.TurnStep(omega, framerate) / 2);

                // what LivePinnacleWorld.FaceOwner sends and books
                (float Angle, Quaternion Booked, float Speed, double Seconds) Plan(Quaternion from, Vector2 owner)
                {
                    var a = ACE.Server.Pinnacles.PinnacleFacing.LocalTurnToward(from, headXY, owner, offset);
                    var landing = ACE.Server.Pinnacles.PinnacleFacing.TurnLanding(a, omega, framerate);
                    return (a, ACE.Server.Pinnacles.PinnacleFacing.RotateAboutLocalUp(from, landing),
                        ACE.Server.Pinnacles.PinnacleFacing.TurnCommandSpeed(a, omega, ACE.Server.Pinnacles.LivePinnacleWorld.HeadTurnSpeed),
                        ACE.Server.Pinnacles.PinnacleFacing.TurnSeconds(a, omega, framerate, ACE.Server.Pinnacles.LivePinnacleWorld.HeadTurnSpeed));
                }

                // first turn: toward a player south-east of the head (a right turn)
                var (angle, booked, speed, seconds) = Plan(spawnRot, new Vector2(20f, -30f));

                Assert.IsTrue(Math.Abs(angle) > 0.5f, $"a real turn ({angle} rad)");

                // CONTROL: no motion table, no turn
                var stillBefore = still.PhysicsObj.Position.Frame.Orientation;
                PlayTurn(still, speed, seconds);
                Assert.IsTrue(Math.Abs(Quaternion.Dot(stillBefore, still.PhysicsObj.Position.Frame.Orientation)) > 0.99999f, "control: without a motion table the turn command does nothing; if this turns, the control no longer reaches the old behaviour");

                var origin = head.PhysicsObj.Position.Frame.Origin;
                var drift = PlayTurn(head, speed, seconds);

                AssertLanded(head, booked, new Vector2(20f, -30f), headXY, offset, halfStep, "first turn");
                Assert.AreEqual(0f, Vector3.Distance(origin, head.PhysicsObj.Position.Frame.Origin), 1e-4f, "the turn does not move the head");
                Assert.AreEqual(0f, drift, 1e-3f, "the drawn part stays on the body frame throughout the turn");

                // the nod's rest frame (teleport-sequenced, as LivePinnacleWorld.ApplyRotation sends it), built from the booked rotation
                WithClientPhysics(() =>
                {
                    var client = head.PhysicsObj;
                    client.UpdateTimes[(int)PhysicsTimeStamp.Teleport] = 0;
                    var pos = new ACE.Server.Physics.Common.Position(client.Position);
                    pos.Frame.Orientation = booked;
                    Assert.IsTrue(client.MoveOrTeleport(pos, 1, false, Vector3.Zero, 0), "the nod frame is applied");

                    for (var i = 0; i < 15; i++)
                        client.UpdateObjectInternal(1.0 / 30, 0);
                });
                AssertLanded(head, booked, new Vector2(20f, -30f), headXY, offset, halfStep, "after the nod frame (it must not set the head turning again)");

                // second turn, from there: toward a player north-east (a left turn)
                var (angle2, booked2, speed2, seconds2) = Plan(booked, new Vector2(20f, -10f));
                Assert.IsTrue(Math.Sign(angle2) != Math.Sign(angle), "the second turn goes the other way");

                drift = PlayTurn(head, speed2, seconds2);

                AssertLanded(head, booked2, new Vector2(20f, -10f), headXY, offset, halfStep, "second turn");
                Assert.AreEqual(0f, drift, 1e-3f, "second turn: the drawn part stays on the body frame");
            }
            finally
            {
                Remove(still);
                Remove(head);
                foreach (var wo in plates)
                    Remove(wo);
            }
        }

        /// <summary>
        /// The client's body rotation is the booked one (to float precision), and its face points horizontally at the
        /// owner within half a turn step (the client turns in whole steps; the server books where it lands).
        /// </summary>
        private static void AssertLanded(Creature head, Quaternion booked, Vector2 owner, Vector2 headXY, float offset, float halfStep, string what)
        {
            var q = head.PhysicsObj.Position.Frame.Orientation;
            Assert.IsTrue(Math.Abs(Quaternion.Dot(q, booked)) > 0.99999f, $"{what}: the client lands on the booked rotation ({q} vs {booked})");

            var face = Vector3.Transform(Vector3.Transform(Vector3.UnitY, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, offset)), q);
            var flat = Vector2.Normalize(new Vector2(face.X, face.Y));
            var toOwner = Vector2.Normalize(owner - headXY);
            Assert.IsTrue(Vector2.Dot(flat, toOwner) >= Math.Cos(halfStep) - 1e-5, $"{what}: the face points at the owner within half a step (cos {Vector2.Dot(flat, toOwner)}, half step {halfStep} rad)");
        }

        /// <summary>
        /// Plays the head's turn as a client does: the interpreted state the live world's start motion carries (Ready,
        /// TurnRight at <paramref name="speed"/>), the body updated for <paramref name="seconds"/> in 60 Hz-ish steps, then
        /// the Ready stop and half a second more. The body is marked Active, as a client's animated remote object is.
        /// Returns the largest distance seen between the drawn part's frame and the body frame (origin, metres).
        /// </summary>
        private static float PlayTurn(Creature wo, float speed, double seconds)
        {
            var drift = 0f;

            WithClientPhysics(() =>
            {
                var po = wo.PhysicsObj;
                po.get_minterp();

                var start = new ACE.Server.Physics.Animation.InterpretedMotionState();
                start.TurnCommand = (uint)MotionCommand.TurnRight;
                start.TurnSpeed = speed;
                po.MovementManager.move_to_interpreted_state(start);

                po.TransientState |= TransientStateFlags.Active;

                var steps = Math.Max(1, (int)Math.Ceiling(seconds * 60));
                var dt = seconds / steps;

                for (var i = 0; i < steps; i++)
                {
                    po.UpdateObjectInternal(dt, 0);

                    if (po.PartArray?.Parts.Count > 0)
                        drift = Math.Max(drift, Vector3.Distance(po.PartArray.Parts[0].Pos.Frame.Origin, po.Position.Frame.Origin));
                }

                po.MovementManager.move_to_interpreted_state(new ACE.Server.Physics.Animation.InterpretedMotionState());

                for (var i = 0; i < 30; i++)
                {
                    po.UpdateObjectInternal(1.0 / 60, 0);

                    if (po.PartArray?.Parts.Count > 0)
                        drift = Math.Max(drift, Vector3.Distance(po.PartArray.Parts[0].Pos.Frame.Origin, po.Position.Frame.Origin));
                }
            });

            return drift;
        }

        /// <summary>
        /// Plays <paramref name="pack"/> into a copy of the head's body through the ported client rule
        /// (PhysicsObj.MoveOrTeleport, contact = the pack's IsGrounded flag) and reports whether its rotation became
        /// <paramref name="q"/>. The copy's UpdateTimes are what a client stored at the CreateObject: the current sequences.
        /// </summary>
        private static bool ClientApplies(Creature head, ACE.Server.Network.Structure.PositionPack pack, Quaternion q)
        {
            var applied = false;

            WithClientPhysics(() =>
            {
                var client = head.PhysicsObj;
                var before = client.Position.Frame.Orientation;
                client.UpdateTimes[(int)PhysicsTimeStamp.Teleport] = 0;

                var pos = new ACE.Server.Physics.Common.Position(client.Position);
                pos.Frame.Orientation = q;

                var teleportStamp = BitConverter.ToUInt16(pack.TeleportSequence, 0);
                var contact = pack.Flags.HasFlag(PositionFlags.IsGrounded);

                client.MoveOrTeleport(pos, teleportStamp, contact, Vector3.Zero, 0);

                applied = Math.Abs(Quaternion.Dot(client.Position.Frame.Orientation, q)) > 0.9999f;

                // put the body back for the next call
                client.Position.Frame.Orientation = before;
                client.set_frame(client.Position.Frame);
            });

            return applied;
        }

        /// <summary>The Ward Plane weenie's bools (1006900.sql); <paramref name="forceStatic"/> false builds the pre-2026-11-06 plate.</summary>
        private static Dictionary<PropertyBool, bool> WardPlaneBools(bool forceStatic)
        {
            var bools = new Dictionary<PropertyBool, bool> { { PropertyBool.Stuck, true }, { PropertyBool.ReportCollisions, true }, { PropertyBool.Ethereal, false }, { PropertyBool.GravityStatus, false } };

            if (forceStatic)
                bools[PropertyBool.ForcePhysicsStatic] = true;

            return bools;
        }

        private static List<WorldObject> SpawnFloorPlates(bool forceStatic = WardPlaneForcesStatic)
        {
            var plates = new List<WorldObject>();

            foreach (var (cell, x, y) in FloorPlates)
            {
                var plate = MakeGeneric(cell, new Vector3(x, y, FloorPlateZ), new Quaternion(0.707107f, 0f, 0f, 0.707107f), WardPlaneSetup, WardPlaneState, WardPlaneScale,
                    WardPlaneBools(forceStatic));
                Assert.IsTrue(TrySpawn(plate), $"floor plate at ({x}, {y}) must spawn, or the test proves nothing");
                plates.Add(plate);
            }

            return plates;
        }

        /// <summary>The applied placement frame's z offset of part 0, in metres at the body's own scale.</summary>
        private static float DrawnPartLift(WorldObject wo)
        {
            var frame = wo.PhysicsObj.PartArray.Sequence.PlacementFrame;

            return frame == null || frame.Frames.Count == 0 ? 0f : frame.Frames[0].Origin.Z * wo.PhysicsObj.Scale;
        }

        private static Creature MakeGraelBody(uint cell, Vector3 at)
            => MakeCreature(cell, at, Quaternion.Identity, GraelBodySetup, GraelMotionTable, GraelPhysicsState, 1f);

        private static Creature MakeCreature(uint cell, Vector3 at, Quaternion rot, uint setup, uint motionTable, int state, float scale)
        {
            var did = new Dictionary<PropertyDataId, uint> { { PropertyDataId.Setup, setup } };
            if (motionTable != 0)
                did[PropertyDataId.MotionTable] = motionTable;

            var gravity = (state & (int)PhysicsState.Gravity) != 0;

            var weenie = new Weenie
            {
                WeenieClassId = 999971,
                ClassName = "pinnaclearenatestcreature",
                WeenieType = WeenieType.Creature,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Pinnacle Arena Test Creature" } },
                PropertiesDID = did,
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.PhysicsState, state } },
                PropertiesBool = new Dictionary<PropertyBool, bool>
                {
                    { PropertyBool.Stuck, true }, { PropertyBool.ReportCollisions, true }, { PropertyBool.IgnoreCollisions, false },
                    { PropertyBool.Ethereal, false }, { PropertyBool.GravityStatus, gravity },
                },
                PropertiesFloat = new Dictionary<PropertyFloat, double> { { PropertyFloat.DefaultScale, scale } },
            };

            TestGameTables.EnsureInitialized();   // Creature's ctor reads the vital formula tables

            var wo = new Creature(weenie, new ObjectGuid(nextGuid++));
            wo.Location = new Position(cell, at.X, at.Y, at.Z, rot.X, rot.Y, rot.Z, rot.W, 0);
            return wo;
        }

        /// <summary>A Generic object; <paramref name="rot"/> is a System.Numerics quaternion (X, Y, Z, W).</summary>
        private static GenericObject MakeGeneric(uint cell, Vector3 at, Quaternion rot, uint setup, int state, float scale, Dictionary<PropertyBool, bool> bools)
        {
            var weenie = new Weenie
            {
                WeenieClassId = 999972,
                ClassName = "pinnaclearenatestplate",
                WeenieType = WeenieType.Generic,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Pinnacle Arena Test Plate" } },
                PropertiesDID = new Dictionary<PropertyDataId, uint> { { PropertyDataId.Setup, setup } },
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.PhysicsState, state } },
                PropertiesBool = bools,
                PropertiesFloat = new Dictionary<PropertyFloat, double> { { PropertyFloat.DefaultScale, scale } },
            };

            var wo = new GenericObject(weenie, new ObjectGuid(nextGuid++));
            wo.Location = new Position(cell, at.X, at.Y, at.Z, rot.X, rot.Y, rot.Z, rot.W, 0);
            return wo;
        }

        /// <summary>The landblock spawn path: InitPhysicsObj (if not already) then AddPhysicsObj. False = "couldn't spawn".</summary>
        private static bool TrySpawn(WorldObject wo)
        {
            var ok = false;

            WithClientPhysics(() =>
            {
                if (wo.PhysicsObj == null)
                    wo.InitPhysicsObj();

                ok = wo.AddPhysicsObj();
            });

            return ok;
        }

        // ================= helpers =================

        private static GenericObject MakeBall(uint cell, Vector3 at)
        {
            var weenie = new Weenie
            {
                WeenieClassId = 999970,
                ClassName = "pinnaclearenatestball",
                WeenieType = WeenieType.Generic,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Pinnacle Arena Test Ball" } },
                PropertiesDID = new Dictionary<PropertyDataId, uint> { { PropertyDataId.Setup, BallSetup } },
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.PhysicsState, BallPhysicsState } },
                PropertiesBool = new Dictionary<PropertyBool, bool> { { PropertyBool.Stuck, true } },
                PropertiesFloat = new Dictionary<PropertyFloat, double> { { PropertyFloat.DefaultScale, BallScale } },
            };

            var wo = new GenericObject(weenie, new ObjectGuid(nextGuid++));
            wo.Location = new Position(cell, at.X, at.Y, at.Z, 0f, 0f, 0f, 1f, 0);
            return wo;
        }

        /// <summary>
        /// The landblock spawn path (InitPhysicsObj, AddPhysicsObj: enter_world, placement, SyncLocation). With
        /// <paramref name="missile"/> the hazard framework's own preparation runs first, as HazardContext.Spawn does.
        /// </summary>
        private static void Spawn(WorldObject wo, bool missile)
        {
            WithClientPhysics(() =>
            {
                if (missile)
                    HazardMotion.PrepareMovingBody(wo);
                else
                    wo.InitPhysicsObj();

                Assert.AreEqual(BallScale, wo.PhysicsObj.Scale, 1e-4f, "scale reached the physics body");
                Assert.IsTrue(wo.AddPhysicsObj(), "AddPhysicsObj failed - the ball did not spawn at all");
            });
        }

        /// <summary>Gives the body 3 m/s west and runs 1 s of physics (10 x 0.1 s); returns the horizontal distance covered.</summary>
        private static float Roll(WorldObject wo)
        {
            var before = wo.PhysicsObj.Position.Frame.Origin;

            WithClientPhysics(() =>
            {
                wo.PhysicsObj.Velocity = new Vector3(-3f, 0f, 0f);

                for (var i = 0; i < 10; i++)
                    wo.PhysicsObj.UpdateObjectInternal(0.1, 0);
            });

            var after = wo.PhysicsObj.Position.Frame.Origin;
            return new Vector2(after.X - before.X, after.Y - before.Y).Length();
        }

        private static void Remove(WorldObject wo) => WithClientPhysics(() => wo.PhysicsObj?.leave_world());

        private static void WithClientPhysics(Action body)
        {
            var engine = PhysicsEngine.Instance;
            var wasServer = engine.Server;
            engine.Server = false;

            try
            {
                body();
            }
            finally
            {
                engine.Server = wasServer;
            }
        }

        // ================= dat handling (as ExactPlacementTests) =================

        private static CellDatDatabase ownCellDat;
        private static bool cellDatSwapped;
        private static CellDatDatabase previousCellDat;

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
        }

        [TestCleanup]
        public void RestoreGlobalState()
        {
            if (!cellDatSwapped)
                return;

            try
            {
                WithClientPhysics(() =>
                {
                    ACE.Server.Physics.Common.LScape.unload_landblock(Landblock << 16 | 0xFFFF, 0);
                    AdjustCell.TryRemove(Landblock, 0);
                });
            }
            finally
            {
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
                return $"Skipped: client_portal.dat + client_cell_1.dat not found in any of [{string.Join(", ", candidates)}]. Set ACE_DAT_PATH to a directory containing the client .dat files.";

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

            if (DatManager.PortalDat == null || ownCellDat == null)
                return $"Skipped: {datDir} did not produce both a PortalDat and a CellDat.";

            return null;
        }
    }
}
