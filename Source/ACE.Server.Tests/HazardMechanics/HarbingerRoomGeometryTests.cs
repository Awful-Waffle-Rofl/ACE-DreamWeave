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
using ACE.Server.Pinnacles.Harbinger;
using ACE.Server.Physics;
using ACE.Server.Physics.Util;
using ACE.Server.WorldObjects;

using Position = ACE.Entity.Position;

namespace ACE.Server.Tests.HazardMechanics
{
    /// <summary>
    /// Dat-backed proofs of the Harbinger's Lair (landblock 0x00BC) through the ported physics the server runs: every
    /// HarbingerData point resolves its pinned cell, the real Harbinger body (setup 0x02001731 at scale 6.2, as 1008250)
    /// spawns at the head point and its radius is MEASURED here (the input to T8's melee reach), each crystal and
    /// guardian body spawns at its point, and the chamber's 1 m grid at the hail drop height has a cell everywhere the
    /// octagon is. Also reports whether the four sloped corner cells (011B, 011D, 0121, 0123) are walkable.
    /// The centre platform is blocked space (owner 2026-11-15): nothing here treats it as standable.
    /// Every value checked here is also pinned dat-free in Pinnacles.HarbingerLayoutTests.
    /// Same dat handling as MatronRoomGeometryTests. Skips (Inconclusive) when the dats are absent.
    /// </summary>
    [TestClass]
    public class HarbingerRoomGeometryTests
    {
        private const uint Landblock = 0x00BC;
        private const uint HeadMotionTable = 0x09000111;
        private const int HeadPhysicsState = 1032;          // ReportCollisions, Gravity (1008250 PropertyInt 93)
        private const int ElitePhysicsState = 3080;         // ReportCollisions, Gravity, LightingOn (crystals, guardians, summons)
        private const uint PlayerSetup = 0x02000001;
        private const uint PlayerMotionTable = 0x09000001;

        private static readonly object initLock = new object();
        private static bool initialized;
        private static string skipReason;
        private static uint nextGuid = 0x7F1B1001;

        // crystal / guardian / summon bodies by element, as the 1008251-1008262 content units carry them
        private static readonly Dictionary<HarbingerElement, (uint CrystalSetup, uint ChildSetup)> Setups = new()
        {
            { HarbingerElement.Acid, (0x020008FC, 0x0200154A) },
            { HarbingerElement.Lightning, (0x020008FC, 0x0200154C) },
            { HarbingerElement.Frost, (0x02000700, 0x02001549) },
            { HarbingerElement.Fire, (0x02000700, 0x0200154B) },
        };

        // guardians are the retail elemental knights (1008255-1008258 rebased on Caustic 31832, Galvanic 31829, Glacial 31828,
        // Incendiary 31827): human motion table, retail scale 1.3
        private const uint GuardianMotionTable = 0x09000001;
        private const float GuardianScale = 1.3f;
        private static readonly Dictionary<HarbingerElement, uint> GuardianSetups = new()
        {
            { HarbingerElement.Acid, 0x02001481 },
            { HarbingerElement.Lightning, 0x02001487 },
            { HarbingerElement.Frost, 0x02001482 },
            { HarbingerElement.Fire, 0x02001483 },
        };

        private static IEnumerable<(string Name, PinnaclePoint At)> RoomPoints()
        {
            yield return ("arrival", HarbingerData.ArrivalPoint);
            yield return ("fight entry", HarbingerData.FightEntryPoint);
            yield return ("head", HarbingerData.HeadPoint);

            for (var i = 0; i < HarbingerData.SummonPoints.Count; i++)
                yield return ($"summon ring {i}", HarbingerData.SummonPoints[i]);

            foreach (var a in HarbingerData.Alcoves)
            {
                yield return ($"{a.Name} stand", a.Stand);
                yield return ($"{a.Name} crystal", a.Crystal);
                yield return ($"{a.Name} guard 0", a.Guards[0]);
                yield return ($"{a.Name} guard 1", a.Guards[1]);
                yield return ($"{a.Name} floor portal", a.Portal);
            }
        }

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void EveryHarbingerDataPoint_ResolvesItsPinnedCell()
        {
            RequireDats();

            var adjust = AdjustCell.Get(Landblock, 0);

            Assert.IsFalse(adjust.GetCell(new Vector3(120f, 120f, -35f)).HasValue, "control: a point off the dungeon has no cell, so the check discriminates");

            var wrong = new List<string>();
            var count = 0;

            foreach (var (name, at) in RoomPoints())
            {
                count++;

                var cell = adjust.GetCell(new Vector3(at.X, at.Y, at.Z));

                if (cell != at.Cell)
                    wrong.Add($"{name} ({at.X}, {at.Y}, {at.Z}): pinned 0x{at.Cell:X8}, measured {(cell.HasValue ? "0x" + cell.Value.ToString("X8") : "none")}");
            }

            Assert.AreEqual(3 + 8 + 4 * 5, count, "the whole room was walked");
            Assert.AreEqual(0, wrong.Count, string.Join("; ", wrong));
        }

        /// <summary>
        /// The Harbinger body at its fight scale (6.2) spawns at the head through the ORDINARY placement, and its radius is the
        /// pinned reach input. Control (the measured ceiling): the same body at 6.4 is refused there (collision spheres 0.48 x
        /// scale), so 6.2 is a measurement, not a guess. No exact-placement opt-in is used.
        /// </summary>

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void HarbingerBody_AtHeadScale_SpawnsAtTheHead_AndItsRadiusIsThePinnedReachInput()
        {
            RequireDats();

            var at = HarbingerData.HeadPoint;

            var tooBig = MakeCreature(at, HarbingerLayout.HeadSetup, HeadMotionTable, 6.4f, HeadPhysicsState, stuck: true);
            var tooBigSpawned = TrySpawn(tooBig);
            if (tooBigSpawned)
                Remove(tooBig);
            Assert.IsFalse(tooBigSpawned, "control: the scale 6.4 body is refused by the ordinary placement at the head, so 6.2 is the measured ceiling");

            var body = MakeCreature(at, HarbingerLayout.HeadSetup, HeadMotionTable, HarbingerLayout.HeadScale, HeadPhysicsState, stuck: true);

            try
            {
                Assert.IsTrue(TrySpawn(body), $"the Harbinger must spawn at the head ({at.X}, {at.Y}, {at.Z}) in 0x{at.Cell:X8} through the plain placement");
                Assert.AreEqual(at.Cell, body.Location.Cell, "in the authored cell");
                Assert.AreEqual(at.X, body.Location.PositionX, 0.01f, "not slid");
                Assert.AreEqual(at.Y, body.Location.PositionY, 0.01f, "not slid");
                Assert.AreEqual(at.Z, body.Location.PositionZ, 0.05f, "rests on the floor");

                var radius = body.PhysicsObj.GetRadius();
                var height = body.PhysicsObj.GetHeight();

                Console.WriteLine($"HARBINGER BODY setup 0x{HarbingerLayout.HeadSetup:X8} scale {HarbingerLayout.HeadScale}: PhysicsObj.GetRadius() = {radius:0.0000}, GetHeight() = {height:0.0000}, rests at z {body.Location.PositionZ:0.000}");

                Assert.AreEqual(HarbingerLayout.HeadRadius, radius, 1e-3f, "HarbingerLayout.HeadRadius is the measured PhysicsObj.GetRadius() of the scale 6.2 body");
            }
            finally
            {
                Remove(body);
            }
        }

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void CrystalsAndGuardians_SpawnAtTheirPoints_InEveryAlcove()
        {
            RequireDats();

            // control: a body 10 m outside the dungeon keeps no cell, so the fit checks below prove something
            var outside = MakeCreature(HarbingerData.Alcoves[0].Crystal with { X = -30f }, 0x020008FC, 0x09000097, 1.5f, ElitePhysicsState, stuck: true);
            var outsideSpawned = TrySpawn(outside);
            var outsideMoved = outsideSpawned && (Math.Abs(outside.Location.PositionX + 30f) > 0.01f || outside.Location.Cell != HarbingerData.Alcoves[0].Cell);
            if (outsideSpawned)
                Remove(outside);
            Assert.IsTrue(!outsideSpawned || outsideMoved, "control: a body placed outside the room keeps neither its authored cell nor x");

            foreach (var a in HarbingerData.Alcoves)
            {
                var (crystalSetup, _) = Setups[a.Element];
                var guardianSetup = GuardianSetups[a.Element];

                var bodies = new List<(string Name, PinnaclePoint At, Creature Body)>
                {
                    ($"{a.Name} crystal", a.Crystal, MakeCreature(a.Crystal, crystalSetup, 0x09000097, 1.5f, ElitePhysicsState, stuck: true)),
                    ($"{a.Name} guard 0", a.Guards[0], MakeCreature(a.Guards[0], guardianSetup, GuardianMotionTable, GuardianScale, ElitePhysicsState, stuck: true)),
                    ($"{a.Name} guard 1", a.Guards[1], MakeCreature(a.Guards[1], guardianSetup, GuardianMotionTable, GuardianScale, ElitePhysicsState, stuck: true)),
                };

                try
                {
                    foreach (var (name, at, body) in bodies)
                    {
                        Assert.IsTrue(TrySpawn(body), $"{name} must spawn at ({at.X}, {at.Y}, {at.Z}) in 0x{at.Cell:X8}");
                        Assert.AreEqual(at.Cell, body.Location.Cell, $"{name}: in the authored cell");
                        Assert.AreEqual(at.X, body.Location.PositionX, 0.01f, $"{name}: not slid");
                        Assert.AreEqual(at.Y, body.Location.PositionY, 0.01f, $"{name}: not slid");
                        Assert.AreEqual(at.Z, body.Location.PositionZ, 0.05f, $"{name}: rests on the alcove floor");
                    }
                }
                finally
                {
                    foreach (var (_, _, body) in bodies)
                        Remove(body);
                }
            }
        }

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void SummonBodies_SpawnAtEveryRingPoint_AndAPlayerFitsEveryArrivalStandAndPortal()
        {
            RequireDats();

            for (var i = 0; i < HarbingerData.SummonPoints.Count; i++)
            {
                var at = HarbingerData.SummonPoints[i];

                foreach (var el in Setups.Keys)
                {
                    var body = MakeCreature(at, Setups[el].ChildSetup, 0x0900008F, 1.2f, ElitePhysicsState, stuck: true);

                    try
                    {
                        Assert.IsTrue(TrySpawn(body), $"{el} summon must spawn at ring {i} ({at.X}, {at.Y}, {at.Z}) in 0x{at.Cell:X8}");
                        Assert.AreEqual(at.Cell, body.Location.Cell, $"ring {i}: in the authored cell");
                        Assert.AreEqual(at.X, body.Location.PositionX, 0.01f, $"ring {i}: not slid");
                        Assert.AreEqual(at.Y, body.Location.PositionY, 0.01f, $"ring {i}: not slid");
                    }
                    finally
                    {
                        Remove(body);
                    }
                }
            }

            var players = new List<(string Name, PinnaclePoint At)> { ("arrival", HarbingerData.ArrivalPoint) };
            players.AddRange(HarbingerData.Alcoves.Select(a => ($"{a.Name} stand", a.Stand)));
            players.AddRange(HarbingerData.Alcoves.Select(a => ($"{a.Name} floor portal", a.Portal)));

            foreach (var (name, at) in players)
            {
                var body = MakeCreature(at, PlayerSetup, PlayerMotionTable, 1f, HeadPhysicsState, stuck: false);

                try
                {
                    Assert.IsTrue(TrySpawn(body), $"a player-sized body must spawn at {name} ({at.X}, {at.Y}, {at.Z}) in 0x{at.Cell:X8}");
                    Assert.AreEqual(at.Cell, body.Location.Cell, $"{name}: in the authored cell");
                    Assert.AreEqual(at.X, body.Location.PositionX, 0.01f, $"{name}: not slid");
                    Assert.AreEqual(at.Y, body.Location.PositionY, 0.01f, $"{name}: not slid");
                    Assert.AreEqual(at.Z, body.Location.PositionZ, 0.05f, $"{name}: rests at its z");
                }
                finally
                {
                    Remove(body);
                }
            }
        }

        /// <summary>
        /// MEASURED 2026-11-15: of the 961 1 m grid points over the floor rect (x 5..35, y -35..-5), 741 lie on the octagon (|x - 20|
        /// and |y + 20| at most 15, their sum at most 20); the other 220 are the four cut corner triangles, which have NO cell at any
        /// height (scanned to z -24). Per drop height, scanning down from -14.0 in 0.25 m steps (printed), the octagon points with a
        /// cell: 561 down to -17.0 (only the plus-shaped centre bands: the four corner stacks are capped at z -17.2, see the corner
        /// test) and all 741 from -17.25 down. The highest scanned height with all 741 is -17.25; HarbingerLayout.HailDropZ is -18.0 (owner ruling: a 0.4 m ball there tops at -17.6, inside the cells), so hail falls
        /// over the corners too. The brief's "all 961" cannot hold: the rect's cut corners are outside the room.
        /// </summary>

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void HailDropGrid_EveryMetreOverTheFloorRect_HasACellAtTheDropHeight_AndTheEdgeIsMeasured()
        {
            RequireDats();

            var adjust = AdjustCell.Get(Landblock, 0);

            // control: the same plane well outside the dungeon has none
            Assert.IsFalse(adjust.GetCell(new Vector3(120f, 120f, -14.0f)).HasValue, "control: a point off the dungeon has no cell");

            static bool OnOctagon(float x, float y) => Math.Abs(x - 20f) <= 15f && Math.Abs(y + 20f) <= 15f && Math.Abs(x - 20f) + Math.Abs(y + 20f) <= 20f;

            int Covered(float z, bool octagon = true)
            {
                var n = 0;

                for (var x = 5f; x <= 35f; x += 1f)
                    for (var y = -35f; y <= -5f; y += 1f)
                        if (OnOctagon(x, y) == octagon && adjust.GetCell(new Vector3(x, y, z)).HasValue)
                            n++;

                return n;
            }

            var table = new List<string>();
            float? highestFull = null;

            for (var z = -14.0f; z >= -24.0f; z -= 0.25f)
            {
                var n = Covered(z);
                table.Add($"{z:0.##}:{n}");

                if (n == 741 && highestFull == null)
                    highestFull = z;
            }

            Console.WriteLine($"HAIL COVERAGE (z:octagon points with a cell of 741): {string.Join(" ", table)}");
            Console.WriteLine($"HAIL highest full-coverage drop z = {(highestFull.HasValue ? highestFull.Value.ToString("0.##") : "none")}");

            Assert.AreEqual(741, Covered(HarbingerLayout.HailDropZ), $"every octagon grid point has a cell at the pinned drop z {HarbingerLayout.HailDropZ}");
            Assert.AreEqual(-17.25f, highestFull, "the measured edge: the highest full-coverage height on the 0.25 m scan");
            Assert.AreEqual(-18.0f, HarbingerLayout.HailDropZ, "pinned 0.75 m under the edge so a 0.4 m ball (top -17.6) stays inside the cells (owner ruling)");
            Assert.IsTrue(HarbingerLayout.HailDropZ + 0.4f < -17.25f, "the ball clears the corner caps' roof");
            Assert.AreEqual(561, Covered(-17.0f), "-17.0 is not full: the measured edge");
            Assert.AreEqual(561, Covered(-14.0f), "the -14.0 finding: only the plus-shaped centre bands have a cell; the four corner squares do not");
            Assert.AreEqual(0, Covered(HarbingerLayout.HailDropZ, octagon: false), "the cut corner triangles are outside the room at the drop height");
        }
        /// <summary>
        /// The four sloped corner cells (011B, 011D, 0121, 0123) are NOT standable. MEASURED 2026-11-15: over each corner square
        /// the cells stack from the chamber floor to a thin cap, 0x...0100 (floor) / 0109 / 0112 / 011B at the north-west corner,
        /// and the cap is the topmost cell: its volume spans z -17.75..-17.25 and there is no cell above z -17.2. The "floor" the
        /// cells tool reports at -17.2 for these cells is that cap's top face, with no cell over it for a body to stand in. A
        /// player-sized body put inside the cap is refused or does not come to rest OnWalkable; the control is the same body on
        /// the chamber floor, which must be OnWalkable.
        /// </summary>

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void CornerCells_AreAThinCapAtTheTopOfTheCornerStack_NotStandable()
        {
            RequireDats();

            var corners = new (string Name, uint Cell, float X, float Y)[]
            {
                ("011B", 0x00BC011B, 11.33f, -11.33f),
                ("011D", 0x00BC011D, 11.33f, -28.67f),
                ("0121", 0x00BC0121, 28.67f, -11.33f),
                ("0123", 0x00BC0123, 28.67f, -28.67f),
            };

            var control = MakeCreature(new PinnaclePoint(0x00BC0100, 12.9f, -12.9f, -35.95f), PlayerSetup, PlayerMotionTable, 1f, HeadPhysicsState, stuck: false);

            try
            {
                Assert.IsTrue(TrySpawn(control), "control body spawns on the chamber floor");
                Assert.IsTrue(control.PhysicsObj.TransientState.HasFlag(TransientStateFlags.OnWalkable), $"control: the chamber floor is walkable ({control.PhysicsObj.TransientState})");
            }
            finally
            {
                Remove(control);
            }

            var adjust = AdjustCell.Get(Landblock, 0);

            foreach (var (name, cell, x, y) in corners)
            {
                // the cap exists between -17.75 and -17.25 and nothing is above it
                Assert.AreEqual(cell, adjust.GetCell(new Vector3(x, y, -17.5f)), $"{name}: the cap cell at z -17.5");
                Assert.IsFalse(adjust.GetCell(new Vector3(x, y, -17.0f)).HasValue, $"{name}: no cell at z -17.0, nothing above the cap");
                Assert.IsFalse(adjust.GetCell(new Vector3(x, y, -14.0f)).HasValue, $"{name}: no cell at z -14.0");
                Assert.AreNotEqual(cell, adjust.GetCell(new Vector3(x, y, -18.5f)), $"{name}: below the cap is another cell of the stack");

                var body = MakeCreature(new PinnaclePoint(cell, x, y, -17.5f), PlayerSetup, PlayerMotionTable, 1f, HeadPhysicsState, stuck: false);
                var spawned = false;

                try
                {
                    spawned = TrySpawn(body);

                    var walkable = spawned && body.PhysicsObj.TransientState.HasFlag(TransientStateFlags.OnWalkable);

                    Console.WriteLine($"CORNER {name} ({x}, {y}, -17.5): spawned {spawned}, rests z {(spawned ? body.Location.PositionZ.ToString("0.000") : "-")}, OnWalkable {walkable}, state {(spawned ? body.PhysicsObj.TransientState.ToString() : "-")}");

                    Assert.IsFalse(walkable, $"{name}: a player-sized body must not come to rest walkable in the corner cap");
                }
                finally
                {
                    if (spawned)
                        Remove(body);
                }
            }
        }
        /// <summary>A creature body (gravity, PhysicsState as the weenie carries) at the given DefaultScale.</summary>
        private static Creature MakeCreature(PinnaclePoint at, uint setup, uint motionTable, float scale, int physicsState, bool stuck)
        {
            var weenie = new Weenie
            {
                WeenieClassId = 999971,
                ClassName = "harbingerroomtestcreature",
                WeenieType = WeenieType.Creature,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Harbinger Room Test Creature" } },
                PropertiesDID = new Dictionary<PropertyDataId, uint> { { PropertyDataId.Setup, setup }, { PropertyDataId.MotionTable, motionTable } },
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.PhysicsState, physicsState } },
                PropertiesBool = new Dictionary<PropertyBool, bool>
                {
                    { PropertyBool.Stuck, stuck }, { PropertyBool.ReportCollisions, true }, { PropertyBool.IgnoreCollisions, false },
                    { PropertyBool.Ethereal, false }, { PropertyBool.GravityStatus, true },
                },
                PropertiesFloat = new Dictionary<PropertyFloat, double> { { PropertyFloat.DefaultScale, scale } },
            };

            TestGameTables.EnsureInitialized();   // Creature's ctor reads the vital formula tables

            var wo = new Creature(weenie, new ObjectGuid(nextGuid++));
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

        // ================= dat handling (as MatronRoomGeometryTests) =================

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
