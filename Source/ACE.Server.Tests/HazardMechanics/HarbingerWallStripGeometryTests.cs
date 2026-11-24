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
using ACE.Server.Pinnacles;
using ACE.Server.Pinnacles.Harbinger;
using ACE.Server.Pinnacles.Matron;
using ACE.Server.Physics;
using ACE.Server.Physics.Util;
using ACE.Server.WorldObjects;

using Position = ACE.Entity.Position;

namespace ACE.Server.Tests.HazardMechanics
{
    /// <summary>
    /// Dat-backed proofs of the Harbinger's wall strips (round 2 A2) on landblock 0x00BC, through the ported physics the server
    /// runs: the wall plane under each alcove's doorway is MEASURED here (HarbingerLayout.WallDistance, the cells
    /// HarbingerAlcove.WallCells), and every strip prop (HarbingerLayout.WallStrip) enters the world exactly where it is put.
    /// Modelled on MatronRoomGeometryTests' wall strip tests. Skips (Inconclusive) when the dats are absent.
    /// </summary>
    [TestClass]
    public class HarbingerWallStripGeometryTests
    {
        private const uint Landblock = 0x00BC;

        private static readonly object initLock = new object();
        private static bool initialized;
        private static string skipReason;
        private static uint nextGuid = 0x7F1C1001;

        /// <summary>The scan's resolution along the outward vector (m).</summary>
        private const float Step = 0.005f;

        /// <summary>The strip is one panel wide (10 m x PanelScale = 3.33 m); the scan checks 1.7 m to either side of the centre line.</summary>
        private const float HalfWidth = 1.7f;

        /// <summary>
        /// MEASURED: for every height a strip prop uses, on the doorway's centre line and 1.7 m to either side, the point
        /// <see cref="HarbingerLayout.WallDistance"/> - 0.001 m out along the alcove's outward vector is in that height's wall cell and the
        /// point a scan step past the plane is in no cell. The scan from 12 m outward prints where the cells end. Control: above the
        /// sill the centre line runs on through the doorway (a cell past the plane), so the scan does see an opening when there is one.
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void WallPlane_UnderEveryDoorway_IsMeasured_AtEveryStripHeight()
        {
            RequireDats();

            var adjust = AdjustCell.Get(Landblock, 0);
            var heights = HarbingerLayout.WallPanelRowZ
                .Concat(MatronLayout.AcidFogs.SelectMany(f => f.Lifts).Select(l => HarbingerLayout.WallFogZ + l))
                .Distinct().OrderBy(z => z).ToList();

            foreach (var a in HarbingerData.Alcoves)
            {
                var o = a.Outward;
                var p = HarbingerLayout.Perp(o);

                // control: through the doorway, 1.5 m over the sill, the centre line has a cell past the wall plane
                var door = HarbingerLayout.ChamberCentre + (HarbingerLayout.WallDistance + 0.25f) * o;
                Assert.IsTrue(adjust.GetCell(new Vector3(door.X, door.Y, 1.5f)).HasValue, $"control {a.Name}: the doorway is open past the wall plane");

                var edges = new List<string>();

                foreach (var z in heights)
                {
                    foreach (var side in new[] { -HalfWidth, 0f, HalfWidth })
                    {
                        float? edge = null;

                        for (var d = 12f; d <= 18f; d += Step)
                        {
                            var at = HarbingerLayout.ChamberCentre + d * o + side * p;

                            if (!adjust.GetCell(new Vector3(at.X, at.Y, z)).HasValue)
                            {
                                edge = d;
                                break;
                            }
                        }

                        edges.Add($"z {z:0.###} side {side:0.0}: first no-cell {(edge.HasValue ? edge.Value.ToString("0.000") : "none")}");

                        var inside = HarbingerLayout.ChamberCentre + (HarbingerLayout.WallDistance - 0.001f) * o + side * p;
                        var past = HarbingerLayout.ChamberCentre + (HarbingerLayout.WallDistance + Step) * o + side * p;

                        Assert.AreEqual(HarbingerLayout.WallCell(a, z), adjust.GetCell(new Vector3(inside.X, inside.Y, z)), $"{a.Name} z {z} side {side}: just inside the wall plane is the pinned wall cell");
                        Assert.IsFalse(adjust.GetCell(new Vector3(past.X, past.Y, z)).HasValue, $"{a.Name} z {z} side {side}: {Step} m past the wall plane is solid");
                    }
                }

                Console.WriteLine($"WALL {a.Name} (outward {o}):{System.Environment.NewLine}  " + string.Join(System.Environment.NewLine + "  ", edges));
            }
        }

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void WallStrip_ForAllFourAlcoves_EveryFlowPanelAndFogHostEntersExactly_InFrontOfTheWall()
        {
            RequireDats();

            var panels = 0;
            var hosts = 0;

            foreach (var a in HarbingerData.Alcoves)
            {
                for (var fogPick = 0; fogPick < MatronLayout.AcidFogs.Count; fogPick++)
                {
                    // the panels are the same for every fog pick: spawn them with pick 0 only
                    var mode = fogPick == 0 ? MatronLayout.WallFxBoth : MatronLayout.WallFxFog;

                    foreach (var h in HarbingerLayout.WallStrip(a, mode, fogPick))
                    {
                        var isPanel = h.Wcid == MatronData.AcidFlowWcid;
                        var body = isPanel ? MakeFlowPanel(h.At, h.Scale) : MakeEffectHost(h.At);
                        var name = $"{a.Name} {(isPanel ? "panel" : $"fog {fogPick}")} z {h.At.Z}";

                        try
                        {
                            Assert.IsTrue(TrySpawn(body), $"{name} at ({h.At.X}, {h.At.Y}, {h.At.Z}) in 0x{h.At.Cell:X8} must spawn");
                            Assert.AreEqual(h.At.Cell, body.Location.Cell, $"{name}: in its wall cell, in front of the wall");
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

            Assert.AreEqual(4 * 11, panels, "every flow panel, 11 rows per wall");
            Assert.AreEqual(4 * MatronLayout.AcidFogs.Sum(f => f.Lifts.Length), hosts, "every fog host of every fog pick");
        }

        /// <summary>
        /// Control for the strip test above (as MatronRoomGeometryTests' flow panel control): a WALL-facing flow panel at the strip's
        /// origin puts its 0.1 m stand-in ball into the wall and never enters exactly, so the room-facing pass is the ball rule at work.
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void FlowPanel_WallFacingAtTheStripOrigin_NeverEntersExactly()
        {
            RequireDats();

            var refused = 0;

            foreach (var a in HarbingerData.Alcoves)
            {
                var room = HarbingerLayout.WallStrip(a, MatronLayout.WallFxPanels)[5].At;
                var wallFacing = MatronData.Point(room.Cell, room.X, room.Y, room.Z, MatronLayout.WallPanelFacing(a.Outward));
                var body = MakeFlowPanel(wallFacing, MatronLayout.PanelScale);

                if (TrySpawn(body))
                {
                    var exact = body.Location.Cell == room.Cell && Math.Abs(body.Location.PositionX - room.X) < 1e-3f && Math.Abs(body.Location.PositionY - room.Y) < 1e-3f;
                    Remove(body);
                    if (!exact)
                        refused++;
                }
                else
                {
                    refused++;
                }
            }

            Assert.AreEqual(4, refused, "a wall-facing panel at the strip's origin offset never enters exactly");
        }

        // ================= builders (as MatronRoomGeometryTests) =================

        private const uint AcidFlowSetup = 0x02000DA9;       // 1007556 Matron Acid Flow
        private const int FlowPanelPhysicsState = 20;        // Ethereal, IgnoreCollisions (1007556 PropertyInt 93)
        private const uint EffectHostSetup = 0x02000176;     // 1006952 Hazard Effect Host
        private const int EffectHostPhysicsState = 53;       // 0x35: Static, Ethereal, IgnoreCollisions, NoDraw

        /// <summary>A body like the flow panel 1007556 (Stuck, ethereal, ignores collisions, no gravity) at the prop's ObjScale.</summary>
        private static GenericObject MakeFlowPanel(PinnaclePoint at, float scale)
        {
            var weenie = new Weenie
            {
                WeenieClassId = 999966,
                ClassName = "harbingerstriptestflowpanel",
                WeenieType = WeenieType.Generic,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Harbinger Strip Test Flow Panel" } },
                PropertiesDID = new Dictionary<PropertyDataId, uint> { { PropertyDataId.Setup, AcidFlowSetup } },
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

        /// <summary>A body like the hazard effect host 1006952 (Stuck, ethereal, no gravity, SpawnAtExactPosition), scale 1.</summary>
        private static GenericObject MakeEffectHost(PinnaclePoint at)
        {
            var weenie = new Weenie
            {
                WeenieClassId = 999965,
                ClassName = "harbingerstriptesteffecthost",
                WeenieType = WeenieType.Generic,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Harbinger Strip Test Effect Host" } },
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

        /// <summary>The landblock spawn path: InitPhysicsObj (if not already) then AddPhysicsObj.</summary>
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

        // ================= dat handling (as MatronFillSpawnTests, for landblock 0x00BC) =================

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
