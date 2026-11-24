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
using ACE.Server.Physics;
using ACE.Server.Physics.Util;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldObjects;

using Position = ACE.Entity.Position;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The Jump course over REAL terrain: Holtburg (landblock 0xAAB3, the ow_holtburg outdoor row, client_cell_1.dat),
    /// through the ground probe production uses (ThreadDungeonSpawner.ProbeOutdoorGround) and the landblock spawn path
    /// (InitPhysicsObj, AddPhysicsObj). Proves a generated course exists on that landblock and that a Skyward Platform
    /// built from 1007961's physics rows (Setup 0x02000DB3, PhysicsState 24, Stuck, GravityStatus False, no bool 9069)
    /// enters the world where the layout put it, within JumpEncounterType.PlacementTolerance. Local only: skipped
    /// (Inconclusive) without the dats. The dat-free pin, The_platform_weenie_matches_the_fixture, keeps the fixture
    /// and the shipped SQL in step for CI.
    ///
    /// Dat handling as PinnacleArenaGeometryTests: the cell dat is opened privately and swapped into DatManager only for
    /// each test, then the physics landblock is unloaded and the dat swapped back out.
    ///
    /// LIMIT: the spawns run with PhysicsEngine.Server = false (WithClientPhysics, the PinnacleArenaGeometryTests
    /// harness), not in server mode, so a landing here is strong evidence but not the production proof. The production
    /// proof is the owner's in-game Holtburg check: the course forms there and never reports "could not form".
    /// </summary>
    [TestClass]
    public class JumpCourseHoltburgGeometryTests
    {
        private const uint Landblock = 0xAAB3;
        private const uint PadSetup = 0x02000DB3;   // 1007961 Skyward Platform (and 1007551 Matron Platform), PropertyDataId 1
        private const int PadPhysicsState = 24;     // ReportCollisions, IgnoreCollisions

        private static readonly object initLock = new object();
        private static bool initialized;
        private static string skipReason;
        private static uint nextGuid = 0x7F0E2001;

        /// <summary>Open-ground NPC spots around Holtburg to try, landblock-local (inside RejectPoint's [14, 178]).</summary>
        private static readonly Vector2[] Spots =
        {
            new Vector2(40, 40), new Vector2(150, 40), new Vector2(40, 150), new Vector2(150, 150),
            new Vector2(96, 30), new Vector2(30, 96), new Vector2(160, 96), new Vector2(96, 160),
        };

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void A_course_generates_on_Holtburg_and_every_platform_lands_where_the_layout_put_it()
        {
            RequireDats();

            var config = new JumpConfig();
            var spawned = new List<WorldObject>();
            JumpLayout layout = null;
            Vector3 npc = default;
            var tried = new List<string>();

            try
            {
                foreach (var spot in Spots)
                {
                    var probe = new Position(Landblock << 16 | 0x0001, spot.X, spot.Y, 0f, 0f, 0f, 0f, 1f, 0);
                    probe.SetLandCell();

                    if (!ThreadDungeonSpawner.ProbeOutdoorGround(probe, 0f, 1000f, out var groundZ))
                    {
                        tried.Add($"{spot}: no open ground");
                        continue;
                    }

                    npc = new Vector3(spot.X, spot.Y, groundZ);
                    layout = JumpCourse.Generate(config, new Random(7), npc, Ground(groundZ), (x, y) => JumpEncounterType.Allowed((ushort)Landblock, x, y, null, 0, 0), out var why);

                    if (layout != null)
                        break;

                    tried.Add($"{spot}: {why}");
                }

                Assert.IsNotNull(layout, "no Holtburg spot took a course: " + string.Join("; ", tried));

                foreach (var pad in layout.Pads)
                {
                    var at = new Position(Landblock << 16 | 0x0001, pad.X, pad.Y, pad.Bottom, 0f, 0f, 0f, 1f, 0);
                    at.SetLandCell();

                    var wo = MakePad(at);
                    Assert.IsTrue(TrySpawn(wo), $"pad at {at.ToLOCString()} did not spawn");
                    spawned.Add(wo);

                    Assert.AreEqual(at.LandblockId.Landblock, wo.Location.LandblockId.Landblock);
                    Assert.IsTrue(Vector3.Distance(at.Pos, wo.Location.Pos) <= JumpEncounterType.PlacementTolerance,
                        $"pad intended {at.Pos} entered at {wo.Location.Pos}: the placement moved it, so production would refuse the course");
                }

                var top = layout.TopPad;
                Assert.IsTrue(top.Top - npc.Z >= 7 * config.RiseMin, $"the top is {top.Top - npc.Z:0.0} m above the NPC");

                // Control: the same slab sunk 0.2 m into the terrain at the NPC's spot. The harness must see THAT moved
                // (or refused), or the landing check above proves nothing.
                var sunk = new Position(Landblock << 16 | 0x0001, npc.X, npc.Y, npc.Z - 0.2f, 0f, 0f, 0f, 1f, 0);
                sunk.SetLandCell();
                var control = MakePad(sunk);
                var controlSpawned = TrySpawn(control);

                if (controlSpawned)
                    spawned.Add(control);

                Assert.IsTrue(!controlSpawned || Vector3.Distance(sunk.Pos, control.Location.Pos) > JumpEncounterType.PlacementTolerance,
                    $"control: a slab cutting the terrain entered unmoved at {control.Location.Pos}; the placement check would not catch a bad pad");
            }
            finally
            {
                foreach (var wo in spawned)
                    Remove(wo);
            }
        }

        /// <summary>
        /// The dat-free pin for CI: the fixture above is exactly what 1007961 Skyward Platform ships (setup, physics
        /// state, Stuck, GravityStatus False, Ethereal False, and NOT bool 9069), so the local proof covers the real weenie.
        /// </summary>
        [TestMethod]
        public void The_platform_weenie_matches_the_fixture()
        {
            var sql = PooledLootSourceText.Read("Content/sql/weenies/1007961 Skyward Platform.sql");

            StringAssert.Contains(sql, "(1007961,   1, 0x02000DB3)");
            StringAssert.Contains(sql, "(1007961,  93,         24)");
            StringAssert.Contains(sql, "(1007961,   1, True )");
            StringAssert.Contains(sql, "(1007961,  13, False)");
            StringAssert.Contains(sql, "(1007961,  14, False)");
            Assert.IsFalse(sql.Contains("(1007961, 9069"), "no SpawnAtExactPosition: the encounter verifies each landing instead");
            StringAssert.Contains(sql, $"0x{PadSetup:X8}");
            StringAssert.Contains(sql, $"(1007961,  93, {PadPhysicsState,10})");

            // The slab dimensions the generator assumes (2 x 2 x 0.4 m, origin at the bottom face) are the Setup's.
            Assert.AreEqual(1.0f, JumpCourse.PadHalf);
            Assert.AreEqual(0.4f, JumpCourse.PadThickness);
        }

        private static Func<float, float, float?> Ground(float sourceZ)
            => (x, y) =>
            {
                var p = new Position(Landblock << 16 | 0x0001, x, y, sourceZ, 0f, 0f, 0f, 1f, 0);
                p.SetLandCell();
                return ThreadDungeonSpawner.ProbeOutdoorGround(p, sourceZ, ThreadDungeonSpawner.ScatterMaxDropZ, out var z) ? z : (float?)null;
            };

        private static GenericObject MakePad(Position at)
        {
            var weenie = new Weenie
            {
                WeenieClassId = 999961,
                ClassName = "jumpcoursetestpad",
                WeenieType = WeenieType.Generic,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Jump Course Test Pad" } },
                PropertiesDID = new Dictionary<PropertyDataId, uint> { { PropertyDataId.Setup, PadSetup } },
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.PhysicsState, PadPhysicsState } },
                PropertiesBool = new Dictionary<PropertyBool, bool>
                {
                    { PropertyBool.Stuck, true },
                    { PropertyBool.ReportCollisions, true },
                    { PropertyBool.Ethereal, false },
                    { PropertyBool.GravityStatus, false },
                },
            };

            var wo = new GenericObject(weenie, new ObjectGuid(nextGuid++));
            wo.Location = new Position(at);
            return wo;
        }

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
