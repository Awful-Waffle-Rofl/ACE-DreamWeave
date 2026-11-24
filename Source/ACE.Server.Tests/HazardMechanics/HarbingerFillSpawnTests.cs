using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.DatLoader;
using ACE.Server.HazardMechanics;
using ACE.Server.Pinnacles.Harbinger;
using ACE.Server.Physics;
using ACE.Server.Physics.Util;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests.HazardMechanics
{
    /// <summary>
    /// Dat-backed proofs that the Harbinger's acid fill covers the chamber octagon on landblock 0x00BC, through the REAL
    /// FillDirector over HarbingerSettings.AcidArgs and the ported physics the server runs (the cell lookup and spawn path of
    /// MatronFillSpawnTests, whose helpers this reuses). Skips (Inconclusive) when the dats are absent.
    /// </summary>
    [TestClass]
    public class HarbingerFillSpawnTests
    {
        private const uint Landblock = 0x00BC;

        private static readonly object initLock = new object();
        private static bool initialized;
        private static string skipReason;

        /// <summary>
        /// DIAGNOSTIC (round 2 A1, the owner's "some floor squares show no acid"): prints every tile of the director's sheet, its
        /// centre, whether that centre has a cell at the panel height, and every refusal the director met.
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void HarbingerFill_Diagnostic_PrintsEveryTileItsCellAndEveryRefusal()
        {
            RequireDats();

            var lookup = MatronFillSpawnTests.Lookup(Landblock);
            var region = HazardRegionPresets.Harbinger(0);
            var settings = HarbingerSettings.Defaults;
            var args = settings.AcidArgs(HarbingerSettings.AcidFloorZ, settings.AcidRate, 1.0, false);

            Assert.IsNull(FillRules.Validate(region, args), "the Harbinger's fill args are accepted");

            var fill = FillSettings.From(args, region);
            var (edge, tiles) = FillRules.Sheet(region, fill.PanelSize, fill.SheetMargin);

            Console.WriteLine($"HARBINGER FILL panel_size={fill.PanelSize} sheet_margin={fill.SheetMargin} edge={edge:0.###} tiles={tiles.Count} doubled={fill.Doubled} floor_z={fill.FloorZ}");

            foreach (var (centre, _) in tiles)
            {
                var cell = lookup(new Vector3(centre.X, centre.Y, (float)fill.FloorZ));
                var dx = centre.X - 20f;
                var dy = centre.Y + 20f;
                Console.WriteLine($"  tile ({centre.X:0.###}, {centre.Y:0.###}) |dx|+|dy|={Math.Abs(dx) + Math.Abs(dy):0.###} cell={(cell.HasValue ? "0x" + cell.Value.ToString("X8") : "none")}");
            }

            var port = new MatronFillSpawnTests.DatFillPort(lookup, fill);
            var director = new FillDirector<WorldObject, string>(region, fill, port);

            try
            {
                director.Start(0);

                Console.WriteLine($"DIRECTOR TilesMissed={director.TilesMissed} placed={port.Placed.Count} describe: {director.Describe()}");

                foreach (var r in port.Refusals)
                    Console.WriteLine($"  refusal: {r}");

                foreach (var l in port.Logs)
                    Console.WriteLine($"  log: {l}");
            }
            finally
            {
                port.RemoveAll();
            }
        }

        /// <summary>
        /// THE A1 PIN. The chamber octagon is |dx| &lt;= 15, |dy| &lt;= 15, |dx| + |dy| &lt;= 20 about (20, -20), and the cut corner
        /// triangles of its rect have no cell (HarbingerRoomGeometryTests). Under the round-1 args (panel_size 10.4: a 3x3 sheet of
        /// 10.2 m tiles) the four diagonal tile centres sat at |dx| + |dy| = 20.4, outside the room, so they were refused and about
        /// 49 m2 of floor per corner showed no acid (the diagnostic above prints it: tiles=5/9). The fix is a 5x5 sheet of 6.12 m
        /// tiles: only the four extreme corner tiles fall outside (centre |dx| + |dy| = 24.48), and what they would have covered
        /// inside the octagon is the CORNER SLIVER, the triangle |dx|, |dy| &gt;= 1.5 x edge under the diagonal (legs
        /// 20 - 3 x edge = 1.64 m, about 1.3 m2 per corner, one point of the 1 m grid each). Every other octagon grid point must
        /// lie under a placed tile, and the director's TilesMissed must equal the number of tile centres outside the room.
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void HarbingerFill_EveryOctagonMetreOutsideTheCornerSlivers_LiesUnderAPlacedTile()
        {
            RequireDats();

            var lookup = MatronFillSpawnTests.Lookup(Landblock);
            var region = HazardRegionPresets.Harbinger(0);
            var settings = HarbingerSettings.Defaults;
            var args = settings.AcidArgs(HarbingerSettings.AcidFloorZ, settings.AcidRate, 1.0, false);

            Assert.IsNull(FillRules.Validate(region, args), "the Harbinger's fill args are accepted (the 64 surface panel cap included)");

            var fill = FillSettings.From(args, region);
            var (edge, tiles) = FillRules.Sheet(region, fill.PanelSize, fill.SheetMargin);

            Assert.IsTrue(fill.Doubled, "the sheet is doubled");

            static bool OnOctagon(float x, float y) => Math.Abs(x - 20f) <= 15f && Math.Abs(y + 20f) <= 15f && Math.Abs(x - 20f) + Math.Abs(y + 20f) <= 20f;

            // control: the octagon walk is the 741 points HarbingerRoomGeometryTests measured, and its cut corners have no cell
            var octagon = new List<Vector2>();
            for (var x = 5f; x <= 35f; x += 1f)
                for (var y = -35f; y <= -5f; y += 1f)
                    if (OnOctagon(x, y))
                        octagon.Add(new Vector2(x, y));

            Assert.AreEqual(741, octagon.Count, "the octagon's 1 m grid");
            Assert.IsFalse(lookup(new Vector3(6f, -6f, (float)fill.FloorZ)).HasValue, "control: a cut corner point has no cell at the panel height");

            var outOfRoom = tiles.Count(t => !lookup(new Vector3(t.Centre.X, t.Centre.Y, (float)fill.FloorZ)).HasValue);

            var port = new MatronFillSpawnTests.DatFillPort(lookup, fill);
            var director = new FillDirector<WorldObject, string>(region, fill, port);

            try
            {
                director.Start(0);

                Assert.AreEqual(outOfRoom, director.TilesMissed, "only the tiles whose centre is outside the room are missed: " + string.Join(" | ", port.Refusals));
                Assert.IsTrue(port.Refusals.All(r => r.Contains("no cell")), "every refusal is an out-of-room centre, none failed to enter the world: " + string.Join(" | ", port.Refusals));
                Assert.AreEqual(2 * (tiles.Count - outOfRoom), port.Placed.Count, "both faces of every in-room tile entered the world");

                var half = edge / 2f + 1e-3f;
                var placedUp = port.Placed.Where(p => !p.FaceDown).Select(p => new Vector2(p.Asked.X, p.Asked.Y)).ToList();

                bool InSliver(Vector2 p) => Math.Abs(p.X - 20f) >= 1.5f * edge && Math.Abs(p.Y + 20f) >= 1.5f * edge;

                var sliver = octagon.Where(InSliver).ToList();
                var uncovered = octagon.Where(p => !InSliver(p) && !placedUp.Any(c => Math.Abs(p.X - c.X) <= half && Math.Abs(p.Y - c.Y) <= half)).ToList();

                Console.WriteLine($"COVERAGE edge={edge:0.###} tiles={tiles.Count} missed={director.TilesMissed} sliver points={sliver.Count} ({string.Join(" ", sliver.Select(p => $"({p.X},{p.Y})"))}) uncovered={uncovered.Count}");

                Assert.AreEqual(0, uncovered.Count, "every octagon grid point outside the corner slivers lies under a placed tile: " + string.Join(" ", uncovered.Select(p => $"({p.X},{p.Y})")));
                Assert.AreEqual(25, tiles.Count, "a 5x5 sheet");
                Assert.AreEqual(50, 2 * tiles.Count, "50 surface panels, under the 64 cap");
                Assert.AreEqual(6.12f, edge, 1e-3f, "30.6 m (the rect plus the 0.3 margin) / 5");
                Assert.AreEqual(4, outOfRoom, "only the four extreme corner tiles fall outside the room");
                Assert.AreEqual(4, sliver.Count, "the slivers hold one grid point per corner, (+-10, +-10) about the centre");

                var leg = 20f - 3f * edge;
                Assert.IsTrue(leg * leg / 2f < 1.5f, $"each corner sliver is about {leg * leg / 2f:0.##} m2 (legs {leg:0.##} m)");

                StringAssert.Contains(director.Describe(), $"tiles={tiles.Count - outOfRoom}/{tiles.Count}");
            }
            finally
            {
                port.RemoveAll();
            }
        }

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
