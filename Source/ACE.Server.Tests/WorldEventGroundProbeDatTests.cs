using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;

using ACE.DatLoader;
using ACE.Server.Physics;
using ACE.Server.WorldEvents;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The presence-ring ground probe against REAL outdoor geometry (Holtburg, landblock 0xA9B4, from
    /// client_cell_1.dat): one downward Transition must stop on a building's roof where there is one, and on
    /// the terrain where there is not. Same dat bootstrap as PuzzleWalkProbeDatTests: PhysicsEngine.Server is
    /// false so LScape builds the landblock from the cell dat, and the cell dat is swapped into DatManager only
    /// for each test. Inconclusive when the dats are not on this machine.
    /// </summary>
    [TestClass]
    public class WorldEventGroundProbeDatTests
    {
        private const uint Landblock = 0xA9B40000;

        /// <summary>
        /// The spike: at least one building origin in Holtburg is under a roof, and the sweep from 60 m above
        /// the terrain stops on that roof (well above the terrain) rather than falling through to the ground.
        /// The control, in the same test: a grid point clear of every building lands on the terrain itself, so
        /// the roof result is not a constant offset.
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void A_downward_sweep_lands_on_a_roof_and_on_bare_terrain_elsewhere()
        {
            RequireDats();

            var lb = ACE.Server.Physics.Common.LScape.get_landblock(Landblock | 0xFFFF, 0);
            Assert.IsNotNull(lb, "Holtburg loaded from the cell dat");
            Assert.IsTrue(lb.Buildings.Count > 0, "Holtburg has buildings");

            var radius = WorldEventGroundProbe.BaseSphereRadius;
            var roofs = new List<string>();
            var report = new List<string>();

            foreach (var building in lb.Buildings)
            {
                var o = building.Position.Frame.Origin;
                var terrainZ = lb.GetZ(new Vector3(o.X, o.Y, 0f));
                var r = WorldEventGroundProbe.ProbeDown(Landblock, 0, o.X, o.Y, terrainZ, 60f, radius, out var attempts);

                report.Add($"({o.X:F1},{o.Y:F1}) terrain={terrainZ:F2} landed={r.Landed} blocked={r.StartBlocked} rest={r.Rest.Z:F2} cell=0x{r.Cell:X8} nz={r.NormalZ:F2} attempts={attempts}");

                if (r.Landed && r.Rest.Z - terrainZ > 2.5f)
                    roofs.Add(report[report.Count - 1]);
            }

            Assert.IsTrue(roofs.Count > 0, "no sweep stopped on a roof:\n" + string.Join("\n", report));

            // Control: a point on bare terrain lands within 10 cm of the terrain height.
            var bare = 0;
            var controlReport = new List<string>();

            for (var gx = 6f; gx < 192f; gx += 12f)
            {
                for (var gy = 6f; gy < 192f; gy += 12f)
                {
                    if (lb.Buildings.Any(b => Vector2.Distance(new Vector2(b.Position.Frame.Origin.X, b.Position.Frame.Origin.Y), new Vector2(gx, gy)) < 25f))
                        continue;

                    var terrainZ = lb.GetZ(new Vector3(gx, gy, 0f));
                    var r = WorldEventGroundProbe.ProbeDown(Landblock, 0, gx, gy, terrainZ, 60f, radius, out _);

                    controlReport.Add($"({gx},{gy}) terrain={terrainZ:F2} landed={r.Landed} rest={r.Rest.Z:F2}");

                    if (r.Landed && Math.Abs(r.Rest.Z - terrainZ) < 0.1f)
                        bare++;
                }
            }

            Assert.IsTrue(bare > 0, "no control point landed on the terrain:\n" + string.Join("\n", controlReport.Take(40)));

            // Every control point must land SOMEWHERE (terrain, scenery or a static object): a sweep that falls
            // through to its end point would mean a hole in the probe, not in the world.
            var unlanded = controlReport.Where(l => l.Contains("landed=False")).ToList();
            Assert.AreEqual(0, unlanded.Count, "control points that never landed:\n" + string.Join("\n", unlanded.Take(40)));

            Console.WriteLine($"roof hits {roofs.Count}/{lb.Buildings.Count}; bare-terrain control hits {bare}/{controlReport.Count}");

            foreach (var line in controlReport.Where(l => !l.Contains("landed=False")).Take(12))
                Console.WriteLine("CONTROL " + line);
            foreach (var line in roofs)
                Console.WriteLine("ROOF " + line);
        }

        /// <summary>A sweep that starts below the terrain cannot land anywhere meaningful: it is reported, never a height.</summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void A_sweep_starting_under_the_terrain_does_not_report_a_landing()
        {
            RequireDats();

            var lb = ACE.Server.Physics.Common.LScape.get_landblock(Landblock | 0xFFFF, 0);
            var terrainZ = lb.GetZ(new Vector3(100f, 100f, 0f));

            var r = WorldEventGroundProbe.Sweep(Landblock, 0, 100f, 100f, terrainZ - 5f, terrainZ - 20f, WorldEventGroundProbe.BaseSphereRadius);

            Assert.IsFalse(r.Landed && r.Rest.Z > terrainZ - 5f, $"rest={r.Rest.Z:F2} terrain={terrainZ:F2}");
        }

        private static readonly object initLock = new object();
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
                ACE.Server.Physics.Common.LScape.unload_landblock(Landblock | 0xFFFF, 0);
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
            var fromEnv = Environment.GetEnvironmentVariable("ACE_DAT_PATH");

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
