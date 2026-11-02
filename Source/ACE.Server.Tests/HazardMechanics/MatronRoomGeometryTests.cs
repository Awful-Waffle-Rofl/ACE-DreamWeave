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
using ACE.Server.Pinnacles;
using ACE.Server.Pinnacles.Matron;
using ACE.Server.Physics;
using ACE.Server.Physics.Util;
using ACE.Server.WorldObjects;

using Position = ACE.Entity.Position;

namespace ACE.Server.Tests.HazardMechanics
{
    /// <summary>
    /// Dat-backed proofs of the Matron room (landblock 0x586B, the Queen's Quarters) through the ported physics the
    /// server runs: every MatronData point and every MatronLayout point resolves its pinned cell, the real Matron body
    /// (setup 0x02000AAD, scale 1.5, PhysicsState 1032, as 72428) fits where she is placed, the platform slab fits
    /// exactly at every platform point, and her motion table carries the two motions the script plays.
    /// Same dat handling as PinnacleArenaGeometryTests: the cell dat is opened privately and swapped into DatManager
    /// for each test. Skips (Inconclusive) when the dats are absent.
    /// </summary>
    [TestClass]
    public class MatronRoomGeometryTests
    {
        private const uint Landblock = 0x586B;
        private const uint MatronSetup = 0x02000AAD;
        private const uint MatronMotionTable = 0x090000BF;
        private const float MatronScale = 1.5f;
        private const int MatronPhysicsState = 1032;       // ReportCollisions, Gravity (72428 PropertyInt 93)
        private const uint PlatformSetup = 0x02000DB3;     // 1007551 Matron Platform, the slab x -1..1, y -1..1, z 0..0.4
        private const int PlatformPhysicsState = 24;       // as the Ward Plane

        private static readonly object initLock = new object();
        private static bool initialized;
        private static string skipReason;
        private static uint nextGuid = 0x7F0F1001;

        private static IEnumerable<(string Name, PinnaclePoint At)> RoomPoints()
        {
            yield return ("arrival", MatronData.ArrivalPoint);
            yield return ("head", MatronData.HeadPoint);
            yield return ("finale", MatronData.FinalePoint);
            yield return ("cache", MatronData.CachePoint);
            yield return ("return portal", MatronData.ReturnPortalPoint);
            yield return ("drain", MatronData.DrainPoint);
            yield return ("drain floor", MatronLayout.DrainFloor().At);
            var tiles = MatronLayout.DrainGrate();
            for (var i = 0; i < tiles.Count; i++)
                yield return ($"drain grate tile {i}", tiles[i].At);
            yield return ("drain iris", MatronLayout.DrainIris(false).At);

            yield return ("pillar host", MatronData.PillarHostPoint);

            for (var i = 0; i < MatronData.WaveSpawnPoints.Count; i++)
                yield return ($"wave spawn {i}", MatronData.WaveSpawnPoints[i]);

            for (var i = 0; i < MatronData.LavaPoolPoints.Count; i++)
                yield return ($"lava pool {i}", MatronData.LavaPoolPoints[i]);

            foreach (var o in MatronData.Openings)
                yield return ($"stand {o.Name}", o.Stand);
        }

        private static IEnumerable<(string Name, PinnaclePoint At)> LayoutPoints(MatronOpening o)
        {
            yield return ($"{o.Name} door", MatronLayout.Door(o));
            yield return ($"{o.Name} back panel", MatronLayout.BackPanel(o).At);

            var strip = MatronLayout.WallStrip(o, lava: false, MatronLayout.WallFxBoth);
            for (var i = 0; i < strip.Count; i++)
                yield return ($"{o.Name} strip {i}", strip[i].At);


            var platforms = MatronLayout.Platforms(o);
            for (var i = 0; i < platforms.Count; i++)
                yield return ($"{o.Name} platform {i}", platforms[i]);

            var floor = MatronLayout.NicheFloor(o);
            for (var i = 0; i < floor.Count; i++)
                yield return ($"{o.Name} niche floor {i}", floor[i].At);

            yield return ($"{o.Name} rock", MatronLayout.Rock(o));
        }

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void EveryMatronDataPoint_ResolvesItsPinnedCell()
        {
            RequireDats();

            var adjust = AdjustCell.Get(Landblock, 0);

            Assert.IsFalse(adjust.GetCell(new Vector3(120f, 120f, -59f)).HasValue, "control: a point off the dungeon has no cell, so the check discriminates");

            var wrong = new List<string>();

            foreach (var (name, at) in RoomPoints())
            {
                var cell = adjust.GetCell(new Vector3(at.X, at.Y, at.Z));

                if (cell != at.Cell)
                    wrong.Add($"{name} ({at.X}, {at.Y}, {at.Z}): pinned 0x{at.Cell:X8}, measured {(cell.HasValue ? "0x" + cell.Value.ToString("X8") : "none")}");
            }

            Assert.AreEqual(0, wrong.Count, string.Join("; ", wrong));
        }

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void EveryLayoutPoint_ForAllFiveOpenings_ResolvesItsPinnedCell()
        {
            RequireDats();

            var adjust = AdjustCell.Get(Landblock, 0);
            var wrong = new List<string>();
            var count = 0;

            foreach (var o in MatronData.Openings)
            {
                foreach (var (name, at) in LayoutPoints(o))
                {
                    count++;

                    var cell = adjust.GetCell(new Vector3(at.X, at.Y, at.Z));

                    if (cell != at.Cell)
                        wrong.Add($"{name} ({at.X:0.###}, {at.Y:0.###}, {at.Z:0.###}): pinned 0x{at.Cell:X8}, measured {(cell.HasValue ? "0x" + cell.Value.ToString("X8") : "none")}");
                }
            }

            Assert.AreEqual(5 * (1 + 1 + 6 + 4 + 2 + 1), count, "the whole layout was walked");
            Assert.AreEqual(0, wrong.Count, string.Join("; ", wrong));
        }

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void HallCellTable_MatchesTheDatEverywhereOnTheFloorPlan()
        {
            RequireDats();

            var adjust = AdjustCell.Get(Landblock, 0);
            var wrong = new List<string>();
            var tested = 0;

            // every 2.5 m grid point of the cross at three heights, one per stacked layer the layout uses
            for (var x = 196.25f; x < 245f; x += 2.5f)
                for (var y = -163.75f; y < -105f; y += 2.5f)
                    foreach (var z in new[] { -59.0f, -50.0f, -46.0f })
                    {
                        if (!MatronData.InFloorPlan(x, y))
                            continue;

                        tested++;

                        var table = MatronData.HallCell(x, y, z);
                        var cell = adjust.GetCell(new Vector3(x, y, z));

                        if (table != (cell ?? 0u))
                            wrong.Add($"({x}, {y}, {z}): table 0x{table:X8}, dat {(cell.HasValue ? "0x" + cell.Value.ToString("X8") : "none")}");
                    }

            Assert.IsTrue(tested > 300, $"control: {tested} points tested");
            Assert.AreEqual(0, wrong.Count, string.Join("; ", wrong.Take(10)));
        }

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void MatronBody_FitsAtHead_EveryStandPoint_AndFallen()
        {
            RequireDats();

            var points = new List<(string Name, PinnaclePoint At)> { ("head", MatronData.HeadPoint), ("finale", MatronData.FinalePoint) };
            points.AddRange(MatronData.Openings.Select(o => ($"stand {o.Name}", o.Stand)));

            // control: 5 m into the west wing's wall the same body is refused, or comes to rest somewhere else
            var inWall = MakeMatron(MatronData.Point(MatronData.Opening(1).Stand.Cell, 190f, -118.5f, MatronData.FloorZ, 0f));
            var wallSpawned = TrySpawn(inWall);
            var wallMoved = wallSpawned && (Math.Abs(inWall.Location.PositionX - 190f) > 0.01f || inWall.Location.Cell != MatronData.Opening(1).Stand.Cell);
            if (wallSpawned)
                Remove(inWall);
            Assert.IsTrue(!wallSpawned || wallMoved, "control: a body placed outside the room keeps its authored cell and x; if it does, the fit checks below prove nothing");

            foreach (var (name, at) in points)
            {
                var body = MakeMatron(at);

                try
                {
                    Assert.IsTrue(TrySpawn(body), $"the Matron must spawn at {name} ({at.X}, {at.Y}, {at.Z}) in 0x{at.Cell:X8}");
                    Assert.AreEqual(at.Cell, body.Location.Cell, $"{name}: in the authored cell");
                    Assert.AreEqual(at.X, body.Location.PositionX, 0.01f, $"{name}: not slid");
                    Assert.AreEqual(at.Y, body.Location.PositionY, 0.01f, $"{name}: not slid");
                    Assert.AreEqual(at.Z, body.Location.PositionZ, 0.05f, $"{name}: rests on the floor");
                }
                finally
                {
                    Remove(body);
                }
            }
        }

        private const uint SentinelSetup = 0x02000FB3;          // 1007558 Hive Sentinel (as 25341), PropertyDataId 1
        private const uint SentinelMotionTable = 0x09000135;    // PropertyDataId 2
        private const float SentinelScale = 1.6f;               // PropertyFloat 39: twice 25341's 0.8

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void HiveSentinelBody_AtTwiceRetailScale_FitsAtEveryWaveSpawnPoint()
        {
            RequireDats();

            Assert.AreEqual(1007558u, MatronData.SentinelWcid);

            // control: the same body 5 m into the west wing's wall is refused, or comes to rest somewhere else
            var inWall = MakeCreature(MatronData.Point(MatronData.Opening(1).Stand.Cell, 190f, -118.5f, MatronData.FloorZ, 0f), SentinelSetup, SentinelMotionTable, SentinelScale);
            var wallSpawned = TrySpawn(inWall);
            var wallMoved = wallSpawned && (Math.Abs(inWall.Location.PositionX - 190f) > 0.01f || inWall.Location.Cell != MatronData.Opening(1).Stand.Cell);
            if (wallSpawned)
                Remove(inWall);
            Assert.IsTrue(!wallSpawned || wallMoved, "control: a body placed outside the room keeps its authored cell and x; if it does, the fit checks below prove nothing");

            for (var i = 0; i < MatronData.WaveSpawnPoints.Count; i++)
            {
                var at = MatronData.WaveSpawnPoints[i];
                var body = MakeCreature(at, SentinelSetup, SentinelMotionTable, SentinelScale);

                try
                {
                    Assert.IsTrue(TrySpawn(body), $"the Hive Sentinel must spawn at wave spawn {i} ({at.X}, {at.Y}, {at.Z}) in 0x{at.Cell:X8}");
                    Assert.AreEqual(at.Cell, body.Location.Cell, $"wave spawn {i}: in the authored cell");
                    Assert.AreEqual(at.X, body.Location.PositionX, 0.01f, $"wave spawn {i}: not slid");
                    Assert.AreEqual(at.Y, body.Location.PositionY, 0.01f, $"wave spawn {i}: not slid");
                }
                finally
                {
                    Remove(body);
                }
            }
        }

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void EveryPlatform_ForAllFiveOpenings_FitsExactlyAtItsPoint()
        {
            RequireDats();

            var placed = new List<WorldObject>();

            try
            {
                // control: a platform authored into a cell it is not in does not keep that cell
                var misplaced = MakePlatform(MatronData.Point(0x586B0103, 240f, -140f, -50f, 0f));
                var misplacedSpawned = TrySpawn(misplaced);
                if (misplacedSpawned)
                    placed.Add(misplaced);
                Assert.IsTrue(!misplacedSpawned || misplaced.Location.Cell != 0x586B0103u || Math.Abs(misplaced.Location.PositionX - 240f) > 1e-3f,
                    "control: a platform authored into a far-away cell is refused or re-cellled; if it is kept as authored the exact checks below prove nothing");
                if (misplacedSpawned)
                {
                    Remove(misplaced);
                    placed.Remove(misplaced);
                }

                foreach (var o in MatronData.Openings)
                {
                    var platforms = MatronLayout.Platforms(o);

                    for (var k = 0; k < platforms.Count; k++)
                    {
                        var at = platforms[k];
                        var slab = MakePlatform(at);
                        var spawned = TrySpawn(slab);

                        if (spawned)
                            placed.Add(slab);

                        Assert.IsTrue(spawned, $"{o.Name} platform {k} at ({at.X:0.###}, {at.Y:0.###}, {at.Z:0.###}) in 0x{at.Cell:X8} must spawn");
                        Assert.AreEqual(at.Cell, slab.Location.Cell, $"{o.Name} platform {k}: in the authored cell");
                        Assert.AreEqual(at.X, slab.Location.PositionX, 1e-3f, $"{o.Name} platform {k}: x exact");
                        Assert.AreEqual(at.Y, slab.Location.PositionY, 1e-3f, $"{o.Name} platform {k}: y exact");
                        Assert.AreEqual(at.Z, slab.Location.PositionZ, 1e-3f, $"{o.Name} platform {k}: z exact (no step-down, no raise)");
                    }
                }

                Assert.AreEqual(20, placed.Count);
            }
            finally
            {
                foreach (var wo in placed)
                    Remove(wo);
            }
        }

        /// <summary>
        /// Flush means touching, never inside: the wall face lies on the opening's centre plane along every pad's wall-side
        /// edge (both corners, bottom and top of the slab), so a slab whose edge is PlatformWallGap (0) off that plane sits
        /// against the wall and not in it. Placement alone cannot show this: it also places a slab 0.01 m into the wall exactly.
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void EveryPlatform_WallSideEdge_TouchesTheWallFace_NeverInsideIt()
        {
            RequireDats();

            var adjust = AdjustCell.Get(Landblock, 0);
            var checkedPoints = 0;

            foreach (var o in MatronData.Openings)
            {
                var along = MatronLayout.Along(o);

                foreach (var p in MatronLayout.Platforms(o))
                {
                    var centre = new Vector2(p.X, p.Y);
                    var edge = centre + o.Outward * MatronLayout.PlatformHalfSize;     // the wall-side edge's midpoint

                    foreach (var t in new[] { -0.999f, 0.999f })
                    {
                        foreach (var z in new[] { p.Z + 0.01f, p.Z + MatronLayout.PlatformThickness - 0.01f })
                        {
                            var corner = edge + along * (t * MatronLayout.PlatformHalfSize);
                            var inFront = corner - o.Outward * 0.005f;
                            var behind = corner + o.Outward * 0.005f;
                            var name = $"{o.Name} pad at ({p.X}, {p.Y}) corner t {t} z {z:0.000}";

                            Assert.AreEqual(0f, Vector2.Dot(o.Centre - corner, o.Outward) - MatronLayout.PlatformWallGap, 1e-4f, $"{name}: the edge is the wall gap off the centre plane");
                            Assert.IsTrue(adjust.GetCell(new Vector3(inFront.X, inFront.Y, z)).HasValue, $"{name}: 5 mm in front of the edge is room");
                            Assert.IsFalse(adjust.GetCell(new Vector3(behind.X, behind.Y, z)).HasValue, $"{name}: 5 mm behind the edge is solid wall");
                            checkedPoints++;
                        }
                    }
                }
            }

            Assert.AreEqual(5 * 4 * 2 * 2, checkedPoints);
        }

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void MatronMotionTable_CarriesSpecialAttack1UnderHandCombat_AndAFallenCycle()
        {
            RequireDats();

            var mt = DatManager.PortalDat.ReadFromDat<ACE.DatLoader.FileTypes.MotionTable>(MatronMotionTable);

            uint Key(MotionCommand style, MotionCommand cmd) => ((uint)style << 16) | ((uint)cmd & 0xFFFFFF);

            var attack = Key(MotionCommand.HandCombat, MotionCommand.SpecialAttack1);
            var inCycles = mt.Cycles.ContainsKey(attack);
            var inModifiers = mt.Modifiers.ContainsKey(attack);
            var inLinks = mt.Links.Any(kv => (kv.Key >> 16) == ((uint)MotionCommand.HandCombat & 0xFFFF) && kv.Value.ContainsKey((uint)MotionCommand.SpecialAttack1));

            // measured 2026-10-27: it is a LINK from Ready (not a cycle or modifier), as every attack motion of a creature table is
            Assert.IsTrue(inLinks, $"SpecialAttack1 under HandCombat as a link from the style's substates (cycle {inCycles}, modifier {inModifiers}, link {inLinks}); style/command key 0x{attack:X8}");

            var fallen = Key(MotionCommand.NonCombat, MotionCommand.Fallen);

            Assert.IsTrue(mt.Cycles.TryGetValue(fallen, out var cycle), $"a NonCombat Fallen cycle, key 0x{fallen:X8}");
            Assert.IsTrue(cycle.Anims.Count > 0, "the Fallen cycle plays an animation");

            // the server's own reader agrees on the length the script waits for
            var length = ACE.Server.Physics.Animation.MotionTable.GetAnimationLength(MatronMotionTable, MotionStance.HandCombat, MotionCommand.Ready, MotionCommand.SpecialAttack1);
            Assert.AreEqual(1.25f, length, 0.05f, $"SpecialAttack1 plays about 1.25 s (the design's open timer); measured {length}");
        }

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void DrainLayers_SpawnExactlyWhereLayoutPutsThem_EachWithAControl()
        {
            RequireDats();

            // (name, prop, setup, whether the flag is load-bearing). The black panel at 3 cm settles 2 mm without SpawnAtExactPosition
            // (-59.972, measured 2026-10-28), so its row is a real control. The grate tiles at 5.5 cm do NOT move without it (measured the
            // same day: -59.945 exact), so for them the flag is defensive and the plain spawn only pins that they land exactly too.
            var layers = new List<(string, PinnacleProp, uint, bool)> { ("black", MatronLayout.DrainFloor(), GrateSetup, true) };
            layers.AddRange(MatronLayout.DrainGrate().Select((t, i) => ($"grate tile {i}", t, GrateSetup, false)));
            var placed = new List<WorldObject>();

            try
            {
                foreach (var (name, layer, setup, flagNeeded) in layers)
                {
                    // control: the same body WITHOUT SpawnAtExactPosition is not left at the pinned z (the engine settles or refuses it)
                    var plain = MakeDrainPanelBody(layer.At, setup, layer.Scale);

                    if (TrySpawn(plain))
                    {
                        placed.Add(plain);
                        Console.WriteLine($"without SpawnAtExactPosition the {name} rests at z {plain.Location.PositionZ:0.000} (authored {layer.At.Z:0.000})");
                        if (flagNeeded)
                            Assert.IsTrue(Math.Abs(plain.Location.PositionZ - layer.At.Z) > 1e-3f, $"control: without SpawnAtExactPosition the {name} must not stay at {layer.At.Z}; measured {plain.Location.PositionZ}");
                        else
                            Assert.AreEqual(layer.At.Z, plain.Location.PositionZ, 1e-3f, $"{name}: lands exactly without the flag too");
                        Remove(plain);
                        placed.Remove(plain);
                    }
                    else
                    {
                        Console.WriteLine($"without SpawnAtExactPosition the {name} spawn is refused");
                        Assert.IsTrue(flagNeeded, $"{name}: refused without the flag, so the flag is load-bearing here: update the expectation");
                    }

                    var body = MakeDrainPanelBody(layer.At, setup, layer.Scale, exactPosition: true);
                    Assert.IsTrue(TrySpawn(body), $"the {name} must spawn at ({layer.At.X}, {layer.At.Y}, {layer.At.Z}) in 0x{layer.At.Cell:X8}");
                    placed.Add(body);
                    Assert.AreEqual(MatronData.DrainPoint.Cell, body.Location.Cell, $"{name}: in the authored cell");
                    Assert.AreEqual(layer.At.X, body.Location.PositionX, 1e-3f, $"{name}: x exact");
                    Assert.AreEqual(layer.At.Y, body.Location.PositionY, 1e-3f, $"{name}: y exact");
                    Assert.AreEqual(layer.At.Z, body.Location.PositionZ, 1e-3f, $"{name}: z exact");
                    Console.WriteLine($"{name} rests at z {body.Location.PositionZ:0.0000} (authored {layer.At.Z:0.0000})");
                }

                // the iris (a rotated Door, pitched face up): the same two-sided check
                var iris = MatronLayout.DrainIris(false);
                // NOT a control: the iris's SpawnAtExactPosition (9069) is defensive, not proven necessary. Measured 2026-10-28, without
                // the flag the iris still lands exactly at its authored position, so this only pins that it does.
                var irisPlain = MakeIrisBody(iris.At, iris.Scale, exactPosition: false);
                Assert.IsTrue(TrySpawn(irisPlain), "the iris spawns without the flag too");
                placed.Add(irisPlain);
                Assert.AreEqual((iris.At.X, iris.At.Y, iris.At.Z), (irisPlain.Location.PositionX, irisPlain.Location.PositionY, irisPlain.Location.PositionZ), "without 9069 the iris equals its authored position");
                Remove(irisPlain);
                placed.Remove(irisPlain);
                var irisBody = MakeIrisBody(iris.At, iris.Scale, exactPosition: true);
                Assert.IsTrue(TrySpawn(irisBody), $"the iris must spawn at ({iris.At.X}, {iris.At.Y}, {iris.At.Z}) in 0x{iris.At.Cell:X8}");
                placed.Add(irisBody);
                Assert.AreEqual(iris.At.Cell, irisBody.Location.Cell, "iris: in the authored cell");
                Assert.AreEqual(iris.At.X, irisBody.Location.PositionX, 1e-3f, "iris: x exact");
                Assert.AreEqual(iris.At.Y, irisBody.Location.PositionY, 1e-3f, "iris: y exact");
                Assert.AreEqual(iris.At.Z, irisBody.Location.PositionZ, 1e-3f, "iris: z exact");
                Assert.AreEqual(iris.At.QX, irisBody.Location.RotationX, 1e-4f, "iris: pitched face up");
                Assert.AreEqual(iris.At.QW, irisBody.Location.RotationW, 1e-4f, "iris: pitched face up");
            }
            finally
            {
                foreach (var wo in placed)
                    Remove(wo);
            }
        }

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void DrainIris_IsInUseRangeFromEveryEdge_OfTheClosedDisc()
        {
            RequireDats();

            var iris = MatronLayout.DrainIris(false);
            var body = MakeIrisBody(iris.At, iris.Scale, exactPosition: true);

            try
            {
                Assert.IsTrue(TrySpawn(body), "the iris must spawn");

                // The server's use check (Player_Use.cs CreateMoveToChain -> Player_Move.cs WithinUseRadius -> Landblock.cs
                // IsWithinUseRadiusOf -> WorldObject_Use.cs GetCylinderDistance -> Physics Position.CylinderDistance):
                // origin to origin in 3D minus both radii (the door's = Setup.Radius x scale, whatever its rotation), at most
                // UseRadius. Player radius 0 is the worst case: a real body only shortens it.
                var r = body.PhysicsObj.GetRadius();
                var h = body.PhysicsObj.GetHeight();
                var c = MatronData.DrainPoint;
                var halfWidth = IrisDiscHalfWidth;
                var halfDepth = IrisDiscHalfDepth;
                const float step = 0.5f;

                float Dist(float x, float y)
                {
                    var feet = new ACE.Server.Physics.Common.Position(new Position(MatronData.HallCell(x, y, c.Z), x, y, c.Z, 0, 0, 0, 1, 0));
                    return (float)ACE.Server.Physics.Common.Position.CylinderDistance(0f, 1.8f, feet, r, h, body.PhysicsObj.Position);
                }

                var sides = new[]
                {
                    ("east", c.X + halfWidth + step, c.Y),
                    ("west", c.X - halfWidth - step, c.Y),
                    ("north", c.X, c.Y + halfDepth + step),
                    ("south", c.X, c.Y - halfDepth - step),
                };

                var report = string.Join(", ", sides.Select(s => $"{s.Item1} {Dist(s.Item2, s.Item3):0.00}"));
                Console.WriteLine($"drain iris use distances (radius {r:0.000}, height {h:0.000}, UseRadius {DrainUseRadius}): {report}");

                foreach (var (side, x, y) in sides)
                    Assert.IsTrue(Dist(x, y) <= DrainUseRadius, $"{side} edge + 0.5 m ({x}, {y}): use distance {Dist(x, y):0.00} > {DrainUseRadius}; all: {report}");

                // control: 4 m out from the east edge is out of range, so the check discriminates
                Assert.IsTrue(Dist(c.X + halfWidth + 4f, c.Y) > DrainUseRadius, "control: a far point is out of use range");
            }
            finally
            {
                Remove(body);
            }
        }

        /// <summary>
        /// The dat behind MatronLayout's drain constants (MatronLayoutTests asserts the geometric relations on the constants, dat-free):
        /// the quad's face drop, sphere and selection sphere, the door's selection sphere, and the closed iris's extents and its
        /// unbroken covered radius, re-derived the way the 2026-10-28 probe measured them.
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void DrainIrisClosedPose_AndTheQuadsAndDoorsSelectionSpheres_MatchTheLayoutConstants()
        {
            RequireDats();

            var quad = DatManager.PortalDat.ReadFromDat<ACE.DatLoader.FileTypes.SetupModel>(GrateSetup);
            Assert.AreEqual(MatronLayout.QuadSelectionRadius, quad.SelectionSphere.Radius, 1e-4f);
            Assert.AreEqual(Vector3.Zero, quad.SelectionSphere.Origin, "the quad's selection sphere is centred on its origin");
            Assert.AreEqual(1, quad.Spheres.Count);
            Assert.AreEqual(0.1f, quad.Spheres[0].Radius, 1e-4f, "the quad's one physics sphere (MatronLayoutTests.QuadSphereRadius)");
            var quadGfx = DatManager.PortalDat.ReadFromDat<ACE.DatLoader.FileTypes.GfxObj>(quad.Parts[0]);
            Assert.IsTrue(quadGfx.VertexArray.Vertices.Values.All(v => Math.Abs(v.Origin.Z + 0.0104f) < 1e-4f), "the quad's face lies 0.0104 under its origin (MatronLayoutTests.QuadFaceDrop)");
            Assert.AreEqual(MatronLayout.GrateNativeSize, quadGfx.VertexArray.Vertices.Values.Max(v => v.Origin.X) - quadGfx.VertexArray.Vertices.Values.Min(v => v.Origin.X), 1e-4f);

            var door = DatManager.PortalDat.ReadFromDat<ACE.DatLoader.FileTypes.SetupModel>(DrainDoorSetup);
            Assert.AreEqual(MatronLayout.DoorSelectionRadius, door.SelectionSphere.Radius, 1e-4f);
            Assert.AreEqual(new Vector3(0f, 0f, MatronLayout.DoorSelectionCentreZ), door.SelectionSphere.Origin);

            // the closed iris: parts 3 to 12 in frame 0 of the closed-pose animation, pitched +90 about x, at scale 1
            var anim = DatManager.PortalDat.ReadFromDat<ACE.DatLoader.FileTypes.Animation>(IrisClosedAnim);
            var pitch = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2f);
            var tris = new List<(Vector2 A, Vector2 B, Vector2 C)>();
            var mn = new Vector3(float.MaxValue);
            var mx = new Vector3(float.MinValue);

            for (var part = 3; part < door.Parts.Count; part++)
            {
                var gfx = DatManager.PortalDat.ReadFromDat<ACE.DatLoader.FileTypes.GfxObj>(door.Parts[part]);
                var frame = anim.PartFrames[0].Frames[part];
                Vector3 W(short id) => Vector3.Transform(Vector3.Transform(gfx.VertexArray.Vertices[(ushort)id].Origin, frame.Orientation) + frame.Origin, pitch);

                foreach (var poly in gfx.Polygons.Values)
                {
                    var ids = poly.VertexIds;
                    foreach (var id in ids) { mn = Vector3.Min(mn, W(id)); mx = Vector3.Max(mx, W(id)); }
                    for (var k = 1; k + 1 < ids.Count; k++)
                    {
                        Vector2 P(short id) { var w = W(id); return new Vector2(w.X, w.Y); }
                        tris.Add((P(ids[0]), P(ids[k]), P(ids[k + 1])));
                    }
                }
            }

            Assert.AreEqual(-MatronLayout.IrisDiscCentreY, (mn.Y + mx.Y) / 2f, 1e-3f, $"the disc's centre y about the origin; extents {mn} .. {mx}");
            Assert.AreEqual(MatronLayout.IrisDiscBelow, -mn.Z, 1e-3f);
            Assert.AreEqual(MatronLayout.IrisDiscAbove, mx.Z, 1e-3f);
            Assert.AreEqual(IrisDiscHalfWidth1, Math.Max(-mn.X, mx.X), 1e-3f);
            Assert.AreEqual(IrisDiscHalfDepth1, (mx.Y - mn.Y) / 2f, 1e-3f);

            bool Covered(Vector2 q)
            {
                foreach (var (a, b, c) in tris)
                {
                    float d1 = (q.X - b.X) * (a.Y - b.Y) - (a.X - b.X) * (q.Y - b.Y);
                    float d2 = (q.X - c.X) * (b.Y - c.Y) - (b.X - c.X) * (q.Y - c.Y);
                    float d3 = (q.X - a.X) * (c.Y - a.Y) - (c.X - a.X) * (q.Y - a.Y);
                    if (!((d1 < 0 || d2 < 0 || d3 < 0) && (d1 > 0 || d2 > 0 || d3 > 0)))
                        return true;
                }

                return false;
            }

            // the petals meet edge to edge, so a hairline crack lies along every shared edge: a point counts as covered within 5 mm
            const float crack = 0.005f;
            bool Near(Vector2 q) => Covered(q) || Covered(q + new Vector2(crack, 0)) || Covered(q - new Vector2(crack, 0)) || Covered(q + new Vector2(0, crack)) || Covered(q - new Vector2(0, crack));

            var centre = new Vector2((mn.X + mx.X) / 2f, (mn.Y + mx.Y) / 2f);
            var shortest = float.MaxValue;

            for (var deg = 0; deg < 360; deg++)
            {
                var dir = new Vector2(MathF.Cos(deg * MathF.PI / 180f), MathF.Sin(deg * MathF.PI / 180f));
                var r = 0f;
                while (r < 4f && Near(centre + dir * (r + 0.001f)))
                    r += 0.001f;
                shortest = Math.Min(shortest, r);
            }

            // the layout's figure is the measured run with the crack tolerance taken back off: it must not claim more than the dat covers
            Console.WriteLine($"closed iris at scale 1: unbroken covered radius {shortest:0.0000} (with the {crack} m crack tolerance)");
            Assert.IsTrue(MatronLayout.IrisCoveredRadius <= shortest, $"IrisCoveredRadius {MatronLayout.IrisCoveredRadius} over the measured {shortest}");
            Assert.AreEqual(shortest - crack, MatronLayout.IrisCoveredRadius, 0.002f, "and is not far under it either");

            // control: a point just past the bounding box is uncovered, so the coverage test discriminates
            Assert.IsFalse(Near(new Vector2(mx.X + 0.05f, centre.Y)), "control: outside the disc is not covered");
        }

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void PitDoors_AtDoorScale_SpawnInEveryNiche_InTheirCell_XYExact()
        {
            RequireDats();

            foreach (var o in MatronData.Openings)
            {
                var at = MatronLayout.Door(o);
                var door = MakeDrainDoorBody(at, MatronLayout.DoorScale);

                try
                {
                    Assert.IsTrue(TrySpawn(door), $"{o.Name} door at scale {MatronLayout.DoorScale} must spawn at ({at.X}, {at.Y}, {at.Z}) in 0x{at.Cell:X8}");
                    Assert.AreEqual(at.Cell, door.Location.Cell, $"{o.Name} door: in the niche");
                    Assert.AreEqual(at.X, door.Location.PositionX, 1e-3f, $"{o.Name} door: x exact");
                    Assert.AreEqual(at.Y, door.Location.PositionY, 1e-3f, $"{o.Name} door: y exact");
                    Console.WriteLine($"{o.Name} door rests at z {door.Location.PositionZ:0.000} (authored {at.Z})");
                    Assert.AreEqual(at.Z, door.Location.PositionZ, 0.11f, $"{o.Name} door: on the sill (at most the 0.1 m rest lift)");
                }
                finally
                {
                    Remove(door);
                }
            }
        }

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void NicheAcidFloor_EnterExactlyInEveryNiche()
        {
            RequireDats();

            foreach (var o in MatronData.Openings)
            {
                foreach (var panel in MatronLayout.NicheFloor(o))
                {
                    var body = MakeDrainPanelBody(panel.At, DrainPanelSetup, panel.Scale);

                    try
                    {
                        Assert.IsTrue(TrySpawn(body), $"{o.Name} niche floor at ({panel.At.X}, {panel.At.Y}, {panel.At.Z}) must spawn");
                        Console.WriteLine($"{o.Name} niche floor rests at ({body.Location.PositionX:0.000}, {body.Location.PositionY:0.000}, {body.Location.PositionZ:0.000}) cell 0x{body.Location.Cell:X8}");
                        Assert.AreEqual(panel.At.Cell, body.Location.Cell, $"{o.Name} niche floor: in the niche");
                        Assert.AreEqual(panel.At.X, body.Location.PositionX, 1e-3f, $"{o.Name} niche floor: x exact");
                        Assert.AreEqual(panel.At.Y, body.Location.PositionY, 1e-3f, $"{o.Name} niche floor: y exact");
                        Assert.AreEqual(panel.At.Z, body.Location.PositionZ, 1e-3f, $"{o.Name} niche floor: z exact (face up)");
                    }
                    finally
                    {
                        Remove(body);
                    }
                }
            }
        }

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void FinaleRock_SpawnsOnTheNorthSill_InTheNiche_XYExact()
        {
            RequireDats();

            var at = MatronLayout.Rock(MatronData.Opening(MatronData.NorthId));
            var rock = MakeRock(at);

            try
            {
                Assert.IsTrue(TrySpawn(rock), $"the rock (setup 0x{RockSetup:X8} at DefaultScale {RockScale}) must spawn at ({at.X}, {at.Y}, {at.Z}) in 0x{at.Cell:X8}");
                Assert.AreEqual(at.Cell, rock.Location.Cell, "rock: in the north niche");
                Assert.AreEqual(at.X, rock.Location.PositionX, 0.01f, "rock: x exact");
                Assert.AreEqual(at.Y, rock.Location.PositionY, 0.01f, "rock: y exact");
                Console.WriteLine($"rock rests at z {rock.Location.PositionZ:0.000} (authored {at.Z}), radius {rock.PhysicsObj.GetRadius():0.00}");
            }
            finally
            {
                Remove(rock);
            }
        }

        /// <summary>
        /// The drain beacon hosts (turned 180 degrees so a hanging beam rises): each lifted by BeaconLift spawns and stays in the drain's
        /// cell. Positive control: the same turned host at plain DrainZ is refused (its sphere falls under the floor).
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void DrainBeacon_TurnedHostLiftedByBeaconLift_Spawns_AtPlainDrainZ_IsRefused()
        {
            RequireDats();

            foreach (var script in MatronLayout.DrainBeacons)
            {
                var lifted = MatronLayout.DrainBeacon(script).At;
                var plain = lifted with { Z = MatronLayout.DrainZ };
                var refused = MakeEffectHost(plain);
                var control = TrySpawn(refused);
                Remove(refused);
                Assert.IsFalse(control, $"control 0x{script:X8}: the turned host at plain DrainZ must be refused");

                var body = MakeEffectHost(lifted);

                try
                {
                    Assert.IsTrue(TrySpawn(body), $"the beacon host 0x{script:X8} must spawn at ({lifted.X}, {lifted.Y}, {lifted.Z})");
                    Assert.AreEqual(0x586B0111u, body.Location.Cell, $"0x{script:X8}: in the drain cell");
                    Assert.AreEqual(MatronData.DrainPoint.Cell, body.Location.Cell);
                }
                finally
                {
                    Remove(body);
                }
            }
        }

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void WallStrip_ForAllFiveOpenings_BothElements_EveryPanelAndFogHostEntersExactly_InFrontOfTheWall()
        {
            RequireDats();

            var adjust = AdjustCell.Get(Landblock, 0);
            var panels = 0;
            var hosts = 0;

            foreach (var o in MatronData.Openings)
            {
                // control: the wall plane is solid rock right behind the strip
                var behind = o.Centre + o.Outward * 0.05f;
                Assert.IsFalse(adjust.GetCell(new Vector3(behind.X, behind.Y, -58.5f)).HasValue, $"control {o.Name}: 0.05 m behind the wall plane is no cell (solid rock)");

                foreach (var lava in new[] { false, true })
                {
                    foreach (var h in MatronLayout.WallStrip(o, lava, MatronLayout.WallFxBoth))
                    {
                        var isPanel = h.Wcid != MatronData.EffectHostWcid;
                        var body = isPanel ? MakeFlowPanel(h.At, lava ? LavaFlowSetup : AcidFlowSetup, h.Scale) : MakeEffectHost(h.At);
                        var name = $"{o.Name} {(lava ? "lava" : "acid")} {(isPanel ? "panel" : "fog")} z {h.At.Z}";

                        try
                        {
                            Assert.IsTrue(TrySpawn(body), $"{name} at ({h.At.X}, {h.At.Y}, {h.At.Z}) in 0x{h.At.Cell:X8} must spawn");
                            Assert.AreEqual(h.At.Cell, body.Location.Cell, $"{name}: in its hall cell, in front of the wall");
                            Assert.AreEqual(h.At.X, body.Location.PositionX, 1e-3f, $"{name}: x exact");
                            Assert.AreEqual(h.At.Y, body.Location.PositionY, 1e-3f, $"{name}: y exact");
                            Assert.AreEqual(h.At.Z, body.Location.PositionZ, 1e-3f, $"{name}: z exact");

                            if (isPanel) panels++; else hosts++;
                        }
                        finally
                        {
                            Remove(body);
                        }
                    }
                }
            }

            Assert.AreEqual(5 * 2 * 4, panels, "every flow panel");
            Assert.AreEqual(5 * 2 * 2, hosts, "every fog host (two per strip)");
        }

        /// <summary>
        /// The stand-in ball rule for the flow setups, measured: a room-facing panel's 0.1 m ball rides 0.1 m up its normal
        /// (into the room), so its origin can sit right on the wall plane; a WALL-facing one puts the ball into the wall and is
        /// refused, as the old curtain fronts were. Control for the strip test above.
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void FlowPanel_StandInBall_RoomFacingEntersAtTheWallOffset_WallFacingIsRefused()
        {
            RequireDats();

            var refusedWallFacing = 0;

            foreach (var o in MatronData.Openings)
            {
                var plane = o.Centre - o.Outward * MatronLayout.WallPanelOffset;
                var z = MatronLayout.WallPanelRowZ[1];
                var cell = MatronData.HallCell(plane.X, plane.Y, z);
                var wallFacing = MatronData.Point(cell, plane.X, plane.Y, z, MatronLayout.WallPanelFacing(o.Outward));
                var body = MakeFlowPanel(wallFacing, LavaFlowSetup, MatronLayout.PanelScale);

                if (TrySpawn(body))
                {
                    var exact = body.Location.Cell == cell && Math.Abs(body.Location.PositionX - plane.X) < 1e-3f && Math.Abs(body.Location.PositionY - plane.Y) < 1e-3f;
                    Remove(body);
                    if (!exact)
                        refusedWallFacing++;
                }
                else
                {
                    refusedWallFacing++;
                }
            }

            Assert.AreEqual(5, refusedWallFacing, "control: a wall-facing flow panel at the strip's origin offset never enters exactly, so the room-facing pass above is the ball rule at work");
        }
        // ================= builders =================
        private const uint LavaFlowSetup = 0x02000DA8;       // 1007555 Matron Lava Flow
        private const uint AcidFlowSetup = 0x02000DA9;       // 1007556 Matron Acid Flow
        private const int FlowPanelPhysicsState = 20;        // Ethereal, IgnoreCollisions (1007555 / 1007556 PropertyInt 93)

        /// <summary>A body like the flow panels 1007555 / 1007556 (Stuck, ethereal, ignores collisions, no gravity, no exact-position flag) at the prop's ObjScale.</summary>
        private static GenericObject MakeFlowPanel(PinnaclePoint at, uint setup, float scale)
        {
            var weenie = new Weenie
            {
                WeenieClassId = 999976,
                ClassName = "matronroomtestflowpanel",
                WeenieType = WeenieType.Generic,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Matron Room Test Flow Panel" } },
                PropertiesDID = new Dictionary<PropertyDataId, uint> { { PropertyDataId.Setup, setup } },
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.PhysicsState, FlowPanelPhysicsState } },
                PropertiesBool = new Dictionary<PropertyBool, bool>
                {
                    { PropertyBool.Stuck, true }, { PropertyBool.Ethereal, true }, { PropertyBool.IgnoreCollisions, true },
                    { PropertyBool.ReportCollisions, false }, { PropertyBool.GravityStatus, false },
                },
            };

            var wo = new GenericObject(weenie, new ObjectGuid(nextGuid++));
            wo.ObjScale = scale;   // as LivePinnacleWorld.SpawnProp stamps PinnacleProp.Scale
            wo.Location = new Position(at.Cell, at.X, at.Y, at.Z, at.QX, at.QY, at.QZ, at.QW, 0);
            return wo;
        }

        private const uint EffectHostSetup = 0x02000176;     // 1006952 Hazard Effect Host
        private const int EffectHostPhysicsState = 53;       // 0x35: Static, Ethereal, IgnoreCollisions, NoDraw

        /// <summary>A body like the hazard effect host 1006952 (Stuck, ethereal, no gravity, SpawnAtExactPosition), scale 1.</summary>
        private static GenericObject MakeEffectHost(PinnaclePoint at)
        {
            var weenie = new Weenie
            {
                WeenieClassId = 999979,
                ClassName = "matronroomtesteffecthost",
                WeenieType = WeenieType.Generic,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Matron Room Test Effect Host" } },
                PropertiesDID = new Dictionary<PropertyDataId, uint> { { PropertyDataId.Setup, EffectHostSetup } },
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.PhysicsState, EffectHostPhysicsState } },
                PropertiesBool = new Dictionary<PropertyBool, bool>
                {
                    { PropertyBool.Stuck, true }, { PropertyBool.Ethereal, true }, { PropertyBool.IgnoreCollisions, true },
                    { PropertyBool.GravityStatus, false }, { PropertyBool.SpawnAtExactPosition, true },
                },
            };

            var wo = new GenericObject(weenie, new ObjectGuid(nextGuid++));
            wo.Location = new Position(at.Cell, at.X, at.Y, at.Z, at.QX, at.QY, at.QZ, at.QW, 0);
            return wo;
        }
        private const uint RockSetup = 0x02000597;           // 1007554 Hardened Hive Rock
        private const float RockScale = 4f;                  // 1007554 PropertyFloat 39
        private const int RockPhysicsState = 1032;           // 1007554 PropertyInt 93 (ReportCollisions, Gravity)

        /// <summary>A body like 1007554 (Stuck, solid, gravity, DefaultScale 4), spawned at add scale 1 so the weenie's scale applies.</summary>
        private static Creature MakeRock(PinnaclePoint at)
        {
            var weenie = new Weenie
            {
                WeenieClassId = 999978,
                ClassName = "matronroomtestrock",
                WeenieType = WeenieType.Creature,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Matron Room Test Rock" } },
                PropertiesDID = new Dictionary<PropertyDataId, uint> { { PropertyDataId.Setup, RockSetup } },
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.PhysicsState, RockPhysicsState } },
                PropertiesBool = new Dictionary<PropertyBool, bool>
                {
                    { PropertyBool.Stuck, true }, { PropertyBool.ReportCollisions, true }, { PropertyBool.IgnoreCollisions, false },
                    { PropertyBool.Ethereal, false }, { PropertyBool.GravityStatus, true },
                },
                PropertiesFloat = new Dictionary<PropertyFloat, double> { { PropertyFloat.DefaultScale, RockScale } },
            };

            TestGameTables.EnsureInitialized();

            var wo = new Creature(weenie, new ObjectGuid(nextGuid++));
            wo.Location = new Position(at.Cell, at.X, at.Y, at.Z, at.QX, at.QY, at.QZ, at.QW, 0);
            return wo;
        }

        private const uint DrainDoorSetup = 0x020005F2;      // 4451 Olthoi door, PropertyDataId 1
        private const int DrainDoorPhysicsState = 8;          // 4451 PropertyInt 93 (ReportCollisions)
        private const float DrainDoorUseRadius = 2f;          // 4451 PropertyFloat 54
        // The closed iris disc about its centre at scale 1, from the 2026-10-28 DatLoader probe (true vertex extents after a +90 pitch):
        // x -1.9109..1.9066 (half width 1.9109 taken as the wider side), y -3.3636..0.0028 (half depth (3.3636 + 0.0028) / 2 = 1.6832),
        // scaled by the layout's IrisScale. DrainIrisClosedPose_MatchesTheLayoutConstants re-derives them from the dat.
        private const float IrisDiscHalfWidth1 = 1.9109f;
        private const float IrisDiscHalfDepth1 = 1.6832f;
        private static readonly float IrisDiscHalfWidth = IrisDiscHalfWidth1 * MatronLayout.IrisScale;
        private static readonly float IrisDiscHalfDepth = IrisDiscHalfDepth1 * MatronLayout.IrisScale;
        private const uint IrisClosedAnim = 0x03000769;     // frame 0 is the Olthoi door's CLOSED pose (placement 0 is half open)
        private const float DrainUseRadius = 2f;              // 1007557 PropertyFloat 54 (UseRadius)
        private const uint DrainPanelSetup = 0x02000E6E;      // the acid panel 1006967 (the 10 m panel); the drain's black panel until playtest 6
        private const uint GrateSetup = 0x02000E15;           // 1007552 Matron Drain Grate and 1007553 Matron Drain Floor (the 8.075 m ClipMap quad)
        private const int DrainPanelPhysicsState = 20;        // Ethereal, IgnoreCollisions

        /// <summary>A body with the Olthoi door's setup and physics (4451: Stuck, solid, no gravity, no exact-position flag) at the live prop path's ObjScale.</summary>
        private static GenericObject MakeDrainDoorBody(PinnaclePoint at, float scale)
        {
            var weenie = new Weenie
            {
                WeenieClassId = 999975,
                ClassName = "matronroomtestdraindoor",
                WeenieType = WeenieType.Generic,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Matron Room Test Drain Door" } },
                PropertiesDID = new Dictionary<PropertyDataId, uint> { { PropertyDataId.Setup, DrainDoorSetup } },
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.PhysicsState, DrainDoorPhysicsState } },
                PropertiesBool = new Dictionary<PropertyBool, bool>
                {
                    { PropertyBool.Stuck, true }, { PropertyBool.Ethereal, false }, { PropertyBool.GravityStatus, false },
                    { PropertyBool.ReportCollisions, true },
                },
                PropertiesFloat = new Dictionary<PropertyFloat, double> { { PropertyFloat.UseRadius, DrainDoorUseRadius } },
            };

            var wo = new GenericObject(weenie, new ObjectGuid(nextGuid++));
            wo.ObjScale = scale;   // as LivePinnacleWorld.SpawnProp stamps PinnacleProp.Scale
            wo.Location = new Position(at.Cell, at.X, at.Y, at.Z, at.QX, at.QY, at.QZ, at.QW, 0);
            return wo;
        }

        /// <summary>
        /// A body like the drain iris 1007557 (the Olthoi door's setup, Stuck, ethereal, ignores collisions, no gravity, UseRadius 2, with
        /// <paramref name="exactPosition"/> SpawnAtExactPosition) at the prop's ObjScale and the layout's rotation.
        /// </summary>
        private static GenericObject MakeIrisBody(PinnaclePoint at, float scale, bool exactPosition)
        {
            var weenie = new Weenie
            {
                WeenieClassId = 999972,
                ClassName = "matronroomtestiris",
                WeenieType = WeenieType.Generic,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Matron Room Test Iris" } },
                PropertiesDID = new Dictionary<PropertyDataId, uint> { { PropertyDataId.Setup, DrainDoorSetup } },
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.PhysicsState, DrainPanelPhysicsState } },
                PropertiesBool = new Dictionary<PropertyBool, bool>
                {
                    { PropertyBool.Stuck, true }, { PropertyBool.Ethereal, true }, { PropertyBool.IgnoreCollisions, true },
                    { PropertyBool.ReportCollisions, false }, { PropertyBool.GravityStatus, false },
                    { PropertyBool.SpawnAtExactPosition, exactPosition },
                },
                PropertiesFloat = new Dictionary<PropertyFloat, double> { { PropertyFloat.UseRadius, DrainUseRadius } },
            };

            var wo = new GenericObject(weenie, new ObjectGuid(nextGuid++));
            wo.ObjScale = scale;   // as LivePinnacleWorld.SpawnProp stamps PinnacleProp.Scale
            wo.Location = new Position(at.Cell, at.X, at.Y, at.Z, at.QX, at.QY, at.QZ, at.QW, 0);
            return wo;
        }

        /// <summary>A drain layer body (the acid panels; the drain's black panel and grate with <paramref name="exactPosition"/>: Stuck, ethereal, ignores collisions, no gravity) at the prop's ObjScale.</summary>
        private static GenericObject MakeDrainPanelBody(PinnaclePoint at, uint setup, float scale, bool exactPosition = false)
        {
            var weenie = new Weenie
            {
                WeenieClassId = 999977,
                ClassName = "matronroomtestdrainpanel",
                WeenieType = WeenieType.Generic,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Matron Room Test Drain Panel" } },
                PropertiesDID = new Dictionary<PropertyDataId, uint> { { PropertyDataId.Setup, setup } },
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.PhysicsState, DrainPanelPhysicsState } },
                PropertiesBool = new Dictionary<PropertyBool, bool>
                {
                    { PropertyBool.Stuck, true }, { PropertyBool.Ethereal, true }, { PropertyBool.IgnoreCollisions, true },
                    { PropertyBool.GravityStatus, false },
                    { PropertyBool.SpawnAtExactPosition, exactPosition },
                },
            };

            var wo = new GenericObject(weenie, new ObjectGuid(nextGuid++));
            wo.ObjScale = scale;   // as LivePinnacleWorld.SpawnProp stamps PinnacleProp.Scale
            wo.Location = new Position(at.Cell, at.X, at.Y, at.Z, at.QX, at.QY, at.QZ, at.QW, 0);
            return wo;
        }

        private static Creature MakeMatron(PinnaclePoint at) => MakeCreature(at, MatronSetup, MatronMotionTable, MatronScale);

        /// <summary>A creature body (Stuck, solid, gravity, PhysicsState 1032 as 72428 and 25341 both carry) at the weenie's DefaultScale.</summary>
        private static Creature MakeCreature(PinnaclePoint at, uint setup, uint motionTable, float scale)
        {
            var weenie = new Weenie
            {
                WeenieClassId = 999973,
                ClassName = "matronroomtestcreature",
                WeenieType = WeenieType.Creature,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Matron Room Test Creature" } },
                PropertiesDID = new Dictionary<PropertyDataId, uint> { { PropertyDataId.Setup, setup }, { PropertyDataId.MotionTable, motionTable } },
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.PhysicsState, MatronPhysicsState } },
                PropertiesBool = new Dictionary<PropertyBool, bool>
                {
                    { PropertyBool.Stuck, true }, { PropertyBool.ReportCollisions, true }, { PropertyBool.IgnoreCollisions, false },
                    { PropertyBool.Ethereal, false }, { PropertyBool.GravityStatus, true },
                },
                PropertiesFloat = new Dictionary<PropertyFloat, double> { { PropertyFloat.DefaultScale, scale } },
            };

            TestGameTables.EnsureInitialized();   // Creature's ctor reads the vital formula tables

            var wo = new Creature(weenie, new ObjectGuid(nextGuid++));
            wo.Location = new Position(at.Cell, at.X, at.Y, at.Z, at.QX, at.QY, at.QZ, at.QW, 0);
            return wo;
        }

        private static GenericObject MakePlatform(PinnaclePoint at)
        {
            var weenie = new Weenie
            {
                WeenieClassId = 999974,
                ClassName = "matronroomtestplatform",
                WeenieType = WeenieType.Generic,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Matron Room Test Platform" } },
                PropertiesDID = new Dictionary<PropertyDataId, uint> { { PropertyDataId.Setup, PlatformSetup } },
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.PhysicsState, PlatformPhysicsState } },
                PropertiesBool = new Dictionary<PropertyBool, bool>
                {
                    { PropertyBool.Stuck, true }, { PropertyBool.Ethereal, false }, { PropertyBool.GravityStatus, false },
                    { PropertyBool.SpawnAtExactPosition, true },
                },
                PropertiesFloat = new Dictionary<PropertyFloat, double> { { PropertyFloat.DefaultScale, 1.0 } },
            };

            var wo = new GenericObject(weenie, new ObjectGuid(nextGuid++));
            wo.Location = new Position(at.Cell, at.X, at.Y, at.Z, at.QX, at.QY, at.QZ, at.QW, 0);
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

        // ================= dat handling (as PinnacleArenaGeometryTests) =================

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
                    AdjustCell.TryRemove((ushort)Landblock, 0);
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
