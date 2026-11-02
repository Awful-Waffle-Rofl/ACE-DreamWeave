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
using ACE.Server.HazardMechanics;
using ACE.Server.Pinnacles;
using ACE.Server.Pinnacles.Matron;
using ACE.Server.Physics;
using ACE.Server.Physics.Util;
using ACE.Server.WorldObjects;

using Position = ACE.Entity.Position;

namespace ACE.Server.Tests.HazardMechanics
{
    /// <summary>
    /// Dat-backed proofs that the Matron's acid fill enters the world on landblock 0x586B, through the ported physics the
    /// server runs (the 2026-10-27 playtest: every in-room fill panel was refused, the fill aborted at t=0.1 s; the panel
    /// curtains that failed the same way are retired; the wall strip took their place, MatronRoomGeometryTests). The panel weenies 1006967 / 1006968 / 1006969 declare Setup 0x02000E6E,
    /// which has no collision spheres, so the engine places them by its 0.1 m DummySphere on the panel's front face
    /// (HazardPanels.FaceClearance). The fill runs through the REAL FillDirector and the Matron's own FillArgs.
    /// Same dat handling as MatronRoomGeometryTests. Skips (Inconclusive) when
    /// the dats are absent.
    /// </summary>
    [TestClass]
    public class MatronFillSpawnTests
    {
        private const uint Landblock = 0x586B;

        /// <summary>The cell floor of the hall (HazardRegionPresets.Matron's ground plane).</summary>
        private const float HallFloor = -60.0f;

        /// <summary>A tile centre in the middle of the hall (cell 0x586B0111).</summary>
        private static readonly Vector2 HallMiddle = new Vector2(220f, -130.14f);

        private static readonly object initLock = new object();
        private static bool initialized;
        private static string skipReason;

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void PanelPlacementLaw_OnTheHallFloor_FaceDownNeedsTheWholeDummySphereClear_FaceUpDoesNot()
        {
            RequireDats();

            var lookup = Lookup(Landblock);

            // the numbers HazardPanels.FaceClearance documents
            Assert.IsNull(PanelRestZ(lookup, HallMiddle, -59.9f, HazardPanels.FaceDown), "face down, probe centre on the floor: refused");
            Assert.IsNull(PanelRestZ(lookup, HallMiddle, -59.95f, HazardPanels.FaceDown), "face down at the playtest's floor_z -59.95: refused (the abort)");
            Assert.AreEqual(-59.8f, PanelRestZ(lookup, HallMiddle, -59.85f, HazardPanels.FaceDown).Value, 1e-3f, "face down, sphere in the floor: pushed up to -59.8");
            Assert.AreEqual(-59.8f, PanelRestZ(lookup, HallMiddle, -59.8f, HazardPanels.FaceDown).Value, 1e-3f, "face down, sphere just clear: exact");
            Assert.AreEqual(HallFloor + HazardPanels.FaceClearance, PanelRestZ(lookup, HallMiddle, HallFloor + HazardPanels.FaceClearance, HazardPanels.FaceDown).Value, 1e-3f, "face down at FaceClearance: exact");
            Assert.AreEqual(-59.95f, PanelRestZ(lookup, HallMiddle, -59.95f, HazardPanels.FaceUp).Value, 1e-3f, "face up at -59.95: exact (its sphere is above it)");
        }

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void MatronFill_TheDirectorsWholeSheet_EveryInCrossTileEntersTheWorld_BothFacesExactlyWhereTheyWerePut()
        {
            RequireDats();

            var lookup = Lookup(Landblock);
            var region = HazardRegionPresets.Matron(0);
            var settings = MatronSettings.Defaults;
            var args = settings.FillArgs(settings.FillFloorZ, settings.FlowRate, 1.0);

            Assert.IsNull(FillRules.Validate(region, args), "the Matron's fill args are accepted");

            var fill = FillSettings.From(args, region);

            Assert.IsTrue(fill.Doubled, "the Matron's sheet is doubled: every tile has a face-down partner");
            Assert.AreEqual(-59.95, fill.FloorZ, 1e-6, "the playtest's floor_z (0.05 m over the hall floor)");

            var tiles = FillRules.SheetTiles(region, fill.PanelSize, fill.SheetMargin);
            var inCross = tiles.Count(t => lookup(new Vector3(t.Centre.X, t.Centre.Y, (float)fill.FloorZ)).HasValue);

            Assert.AreEqual(30, tiles.Count, "30 tiles (FillArgs' own doc)");
            Assert.AreEqual(18, inCross, "18 tile centres inside the cross under the edge-to-edge plan, as under the old one (the playtest logged the other 12 as 'no cell of this landblock holds it')");

            // control: the director's old placement (the face-down partner at floor_z itself) is refused at an in-cross centre
            Assert.IsNull(PanelRestZ(lookup, HallMiddle, (float)fill.FloorZ, HazardPanels.FaceDown), "control: a face-down panel at floor_z is refused, the playtest abort");

            var port = new DatFillPort(lookup, fill);
            var director = new FillDirector<WorldObject, string>(region, fill, port);

            try
            {
                director.Start(0);

                Assert.AreEqual(tiles.Count - inCross, director.TilesMissed, "only the out-of-cross tiles are missed: " + string.Join(" | ", port.Refusals));
                Assert.AreEqual(2 * inCross, port.Placed.Count, "both faces of every in-cross tile entered the world");

                // the seam lines of playtest 3: every face of the sheet RESTS on one plane, up and down alike
                var rested = port.Placed.Select(p => p.Body.Location.PositionZ).ToList();
                Assert.IsTrue(rested.Max() - rested.Min() < 1e-4f, $"one plane: the faces rest between z {rested.Min()} and {rested.Max()}");

                var edge = FillRules.SheetEdge(region, fill.PanelSize, fill.SheetMargin);
                Assert.AreEqual(10.12f, edge, 1e-3f, "edge to edge: 50.6 m / 5 tiles (FillRules.Sheet), not panel_size 12");
                Assert.IsTrue(port.Edges.All(e => e == edge), "every panel at the plan's edge: " + string.Join(",", port.Edges.Distinct()));
                Assert.IsTrue(port.Placed.All(p => Math.Abs(p.Body.ObjScale.Value - HazardPanels.ScaleFor(edge)) < 1e-5f), "and the scale that edge needs");
                Assert.AreEqual(inCross, port.Placed.Count(p => p.FaceDown), "one face-down partner per in-cross tile");
                Assert.IsTrue(port.Refusals.All(r => r.Contains("no cell")), "every refusal is an out-of-cross centre: " + string.Join(" | ", port.Refusals));

                foreach (var (body, asked, faceDown) in port.Placed)
                {
                    var at = new Vector3(body.Location.PositionX, body.Location.PositionY, body.Location.PositionZ);
                    Assert.IsTrue(Vector3.Distance(at, asked) < 1e-3f, $"{(faceDown ? "down" : "up")} panel asked at {asked} rests at {at}");
                    Assert.IsTrue(asked.Z - HazardPanels.FaceClearance > HallFloor - 1e-4f, $"and is drawn at least FaceClearance over the floor: {asked.Z}");
                }

                Assert.IsTrue(port.Logs.Any(l => l.Contains("draws its panels no lower than")), "the lift is logged: " + string.Join(" | ", port.Logs));
                Assert.AreEqual(fill.FloorZ, director.SurfaceAt(0), 1e-9, "the surface itself (damage, the script's level) is still floor_z");
                StringAssert.Contains(director.Describe(), $"tiles={inCross}/{tiles.Count}");
            }
            finally
            {
                port.RemoveAll();
            }
        }

        // ================= helpers (shared with PinnacleArenaGeometryTests' Grael fill control) =================

        private static bool Rests(WorldObject wo, PinnaclePoint at)
            => wo.Location.Cell == at.Cell
               && Vector3.Distance(new Vector3(wo.Location.PositionX, wo.Location.PositionY, wo.Location.PositionZ), new Vector3(at.X, at.Y, at.Z)) < 1e-3f;

        /// <summary>The cell lookup HazardGround.TryResolveCell uses for a dungeon (AdjustCell.GetCell).</summary>
        internal static Func<Vector3, uint?> Lookup(uint landblock)
        {
            var adjust = AdjustCell.Get(landblock, 0);
            return p => adjust.GetCell(p);
        }

        /// <summary>Where a moving panel spawned at (xy, z) with <paramref name="rotation"/> comes to rest; null when refused.</summary>
        internal static float? PanelRestZ(Func<Vector3, uint?> lookup, Vector2 xy, float z, Quaternion rotation)
        {
            var p = new Vector3(xy.X, xy.Y, z);
            var cell = lookup(p);

            if (!cell.HasValue)
                return null;

            var spec = HazardPanels.Spec(HazardDefaults.PanelAcidWcid, p, rotation, 12f, 0.5, true, "law");
            var body = PanelBody(cell.Value, spec);

            if (!TrySpawn(body))
                return null;

            var rest = body.Location.PositionZ;
            Remove(body);
            return rest;
        }

        /// <summary>
        /// A body with the panel weenie's physics (1006967 / 1006968, ace_world read 2026-10-27 with ace_ro: Setup
        /// 0x02000E6E, PhysicsState 20 = Ethereal | IgnoreCollisions, Stuck, no gravity, DefaultScale 1), stamped the way
        /// HazardContext.Spawn stamps a panel spec (rotation, ObjScale, translucency, then PrepareMovingBody when moving).
        /// </summary>
        internal static GenericObject PanelBody(uint cell, HazardSpawnSpec spec)
        {
            var q = spec.Rotation ?? Quaternion.Identity;
            var wo = NewPanel(new Position(cell, spec.Position.X, spec.Position.Y, spec.Position.Z, q.X, q.Y, q.Z, q.W, 0));

            if (float.IsFinite(spec.Scale) && spec.Scale > 0f && Math.Abs(spec.Scale - 1.0f) > 1e-6f)
                wo.ObjScale = spec.Scale;

            if (spec.Translucency.HasValue)
                wo.Translucency = spec.Translucency.Value;

            if (spec.Moving)
                HazardMotion.PrepareMovingBody(wo);

            return wo;
        }

        /// <summary>A prop panel the way LivePinnacleWorld.SpawnProp stamps one (location, scale; not moving).</summary>
        private static GenericObject PanelBody(PinnaclePoint at, float scale)
        {
            var wo = NewPanel(new Position(at.Cell, at.X, at.Y, at.Z, at.QX, at.QY, at.QZ, at.QW, 0));

            if (float.IsFinite(scale) && scale > 0f && Math.Abs(scale - 1.0f) > 1e-6f)
                wo.ObjScale = scale;

            return wo;
        }

        private static uint nextGuid = 0x7F0F3001;

        private static GenericObject NewPanel(Position at)
        {
            var weenie = new Weenie
            {
                WeenieClassId = 999981,
                ClassName = "matronfilltestpanel",
                WeenieType = WeenieType.Generic,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Matron Fill Test Panel" } },
                PropertiesDID = new Dictionary<PropertyDataId, uint> { { PropertyDataId.Setup, 0x02000E6E } },
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.PhysicsState, 20 } },
                PropertiesBool = new Dictionary<PropertyBool, bool>
                {
                    { PropertyBool.Stuck, true }, { PropertyBool.Ethereal, true }, { PropertyBool.IgnoreCollisions, true },
                    { PropertyBool.GravityStatus, false }, { PropertyBool.ReportCollisions, false },
                },
                PropertiesFloat = new Dictionary<PropertyFloat, double> { { PropertyFloat.DefaultScale, 1.0 } },
            };

            return new GenericObject(weenie, new ObjectGuid(nextGuid++)) { Location = at };
        }

        /// <summary>The landblock spawn path: InitPhysicsObj (if not already) then AddPhysicsObj. False = "failed to enter the world".</summary>
        internal static bool TrySpawn(WorldObject wo)
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

        internal static void Remove(WorldObject wo) => WithClientPhysics(() => wo.PhysicsObj?.leave_world());

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

        /// <summary>
        /// The fill director's world against the real dats: panels are spawned as HazardContext.Spawn would (a centre
        /// in no cell is refused with "no cell", as the playtest logged; otherwise the stamped body is put through the
        /// landblock spawn path). Wisps, motion, players and damage are inert - only placement is under test.
        /// </summary>
        internal sealed class DatFillPort : IFillPort<WorldObject, string>
        {
            private readonly Func<Vector3, uint?> lookup;
            private readonly FillSettings s;

            public DatFillPort(Func<Vector3, uint?> lookup, FillSettings settings)
            {
                this.lookup = lookup;
                s = settings;
            }

            public readonly List<(WorldObject Body, Vector3 Asked, bool FaceDown)> Placed = new List<(WorldObject, Vector3, bool)>();
            public readonly List<string> Refusals = new List<string>();
            public readonly List<string> Logs = new List<string>();

            public Random Rng { get; } = new Random(1);

            public readonly List<float> Edges = new List<float>();

            public WorldObject SpawnPanel(Vector3 centre, float edge, bool faceDown, bool moving)
            {
                var cell = lookup(centre);

                if (!cell.HasValue || cell.Value == 0)
                {
                    Refusals.Add($"{(faceDown ? "down" : "up")} at {centre}: no cell");
                    return null;
                }

                Edges.Add(edge);
                var spec = HazardPanels.Spec(s.PanelWcid, centre, faceDown ? HazardPanels.FaceDown : HazardPanels.FaceUp, edge, s.Opacity, moving, "fill");
                var body = PanelBody(cell.Value, spec);

                if (!TrySpawn(body))
                {
                    Refusals.Add($"{(faceDown ? "down" : "up")} at {centre}: failed to enter the world");
                    return null;
                }

                Placed.Add((body, centre, faceDown));
                return body;
            }

            public WorldObject SpawnWisp(Vector3 at) => null;

            public bool HasCell(Vector3 point) => lookup(point) is uint c && c != 0;

            public bool Alive(WorldObject handle) => handle?.PhysicsObj?.CurCell != null;

            public int Move(IReadOnlyList<(WorldObject Handle, Vector3 End)> group, double seconds) => 0;

            public void Halt(WorldObject handle) { }

            public void Destroy(WorldObject handle)
            {
                if (handle == null)
                    return;

                Remove(handle);
                Placed.RemoveAll(p => ReferenceEquals(p.Body, handle));
            }

            public IReadOnlyList<(string Player, Vector3 Feet)> Players() => Array.Empty<(string, Vector3)>();

            public int HitFlat(WorldObject source, string player, double flat) => 0;

            public IReadOnlyList<HazardControl> TakeControls() => Array.Empty<HazardControl>();

            public void Say(string line) { }

            public void Log(string line) => Logs.Add(line);

            public void End(HazardOutcome outcome, string reason) => Logs.Add($"end {outcome} {reason}");

            public void RemoveAll()
            {
                foreach (var (body, _, _) in Placed)
                    Remove(body);

                Placed.Clear();
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
