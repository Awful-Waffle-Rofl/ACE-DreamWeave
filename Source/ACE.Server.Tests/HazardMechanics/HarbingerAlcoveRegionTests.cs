using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.DatLoader;
using ACE.Server.HazardMechanics;
using ACE.Server.Physics;
using ACE.Server.Physics.Util;

namespace ACE.Server.Tests.HazardMechanics
{
    /// <summary>
    /// Dat-backed proof of the four Harbinger alcove hazard presets (HazardRegionPresets.HarbingerAlcove*, landblock 0x00BC)
    /// through the ported physics the server runs (AdjustCell.GetCell, a point-in-cell test): every point of each preset's
    /// circle, on a 0.25 m grid, lies inside the alcove's own two cells at the floor, at the hazard height and just under
    /// the pinned ceiling face, and the controls show the probe discriminates (no cell just above the ceiling, none just
    /// under the floor, none outside the walls). Same dat handling as HarbingerRoomGeometryTests. Skips (Inconclusive)
    /// when the dats are absent.
    /// </summary>
    [TestClass]
    public class HarbingerAlcoveRegionTests
    {
        private const uint Landblock = 0x00BC;

        private static readonly object initLock = new object();
        private static bool initialized;
        private static string skipReason;

        /// <summary>
        /// Each preset, its alcove's two cells (the floor cell and the cell above it, `cells --room=0xCELL --cells`) and its
        /// doorway sill cell (the floor cell's one opening, `cells --openings=0xCELL`).
        /// </summary>
        private static readonly (string Name, uint FloorCell, uint UpperCell, uint SillCell)[] Alcoves =
        {
            (HazardRegionPresets.HarbingerAlcoveWestName, 0x00BC012D, 0x00BC0139, 0x00BC012E),
            (HazardRegionPresets.HarbingerAlcoveNorthName, 0x00BC0130, 0x00BC013B, 0x00BC0131),
            (HazardRegionPresets.HarbingerAlcoveSouthName, 0x00BC0134, 0x00BC013E, 0x00BC0135),
            (HazardRegionPresets.HarbingerAlcoveEastName, 0x00BC0137, 0x00BC0140, 0x00BC0138),
        };

        private static IEnumerable<Vector2> Grid(HazardRegion r, float step = 0.25f)
        {
            for (var x = r.CenterX - r.Radius; x <= r.CenterX + r.Radius + 1e-4f; x += step)
                for (var y = r.CenterY - r.Radius; y <= r.CenterY + r.Radius + 1e-4f; y += step)
                    if (r.Contains(x, y))
                        yield return new Vector2(x, y);
        }

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void EveryAlcovePresetPoint_HasItsAlcovesCell_FromTheFloorToTheCeiling()
        {
            RequireDats();

            var adjust = AdjustCell.Get(Landblock, 0);
            var wrong = new List<string>();
            var checkedPoints = 0;

            foreach (var (name, floorCell, upperCell, _) in Alcoves)
            {
                Assert.IsTrue(HazardRegionPresets.TryGet(name, 0, out var r), name);

                var heights = new[]
                {
                    r.GroundPlaneZ + 0.05f,                 // on the floor
                    r.HazardZ(r.CenterX, r.CenterY),        // where wisps fly and balls roll
                    r.CeilingZ.Value - 0.05f,               // just under the ceiling face
                };

                var points = Grid(r).ToList();
                Assert.IsTrue(points.Count > 700, $"{name}: {points.Count} grid points (a 4 m disc at 0.25 m is about 800)");

                foreach (var p in points)
                    foreach (var z in heights)
                    {
                        checkedPoints++;
                        var cell = adjust.GetCell(new Vector3(p.X, p.Y, z));

                        if (cell != floorCell && cell != upperCell)
                            wrong.Add($"{name} ({p.X:0.00}, {p.Y:0.00}, {z:0.00}): {(cell.HasValue ? "0x" + cell.Value.ToString("X8") : "no cell")}");
                    }
            }

            Assert.AreEqual(0, wrong.Count, $"{wrong.Count} of {checkedPoints} points: " + string.Join("; ", wrong.Take(20)));
        }

        /// <summary>
        /// Review F2 on 19d358426: an acid drip's ball spawns at HarbingerSettings.DripDropZ (8.0) over a point of its 0.5 m
        /// area round a pool slot, and RainDirector only launches where a cell holds that point. Every area (its centre, its
        /// four corners and its edge midpoints) of every slot of every alcove has a cell at the drop height, so a drip is
        /// never left with no place to fall (the residual on DripDuration: such a run would still end Completed).
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void EveryDripArea_HasACellAtTheDropHeight_InEveryAlcove()
        {
            RequireDats();

            var adjust = AdjustCell.Get(Landblock, 0);
            var dropZ = (float)ACE.Server.Pinnacles.Harbinger.HarbingerSettings.DripDropZ;
            var missing = new List<string>();
            var checkedPoints = 0;

            foreach (var alcove in ACE.Server.Pinnacles.Harbinger.HarbingerData.Alcoves)
            {
                for (var i = 0; i < ACE.Server.Pinnacles.Harbinger.HarbingerLayout.PoolSlotsUV.Count; i++)
                {
                    var a = ACE.Server.Pinnacles.Harbinger.HarbingerLayout.PoolArea(alcove, i, ACE.Server.Pinnacles.Harbinger.HarbingerSettings.DripAreaSize);
                    var mx = (a.MinX + a.MaxX) / 2f;
                    var my = (a.MinY + a.MaxY) / 2f;

                    foreach (var x in new[] { a.MinX, mx, a.MaxX })
                        foreach (var y in new[] { a.MinY, my, a.MaxY })
                        {
                            checkedPoints++;

                            if (adjust.GetCell(new Vector3(x, y, dropZ)) == null)
                                missing.Add($"{alcove.Name} slot {i} ({x:0.00}, {y:0.00}, {dropZ:0.00})");
                        }
                }
            }

            Assert.AreEqual(4 * 18 * 9, checkedPoints);
            Assert.AreEqual(0, missing.Count, $"{missing.Count} of {checkedPoints} drip points have no cell: " + string.Join("; ", missing.Take(20)));

            // control: the same probe finds no cell over the ceiling face at a slot
            var west = ACE.Server.Pinnacles.Harbinger.HarbingerData.Alcoves[0];
            var c = ACE.Server.Pinnacles.Harbinger.HarbingerLayout.PoolSlot(west, 0);
            Assert.IsNull(adjust.GetCell(new Vector3(c.X, c.Y, HazardRegionPresets.HarbingerAlcoveCeilingZ + 0.1f)));
        }

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void Controls_TheProbeSeesTheCeilingTheFloorAndTheWalls()
        {
            RequireDats();

            var adjust = AdjustCell.Get(Landblock, 0);

            foreach (var (name, floorCell, upperCell, _) in Alcoves)
            {
                HazardRegionPresets.TryGet(name, 0, out var r);

                var c = new Vector2(r.CenterX, r.CenterY);

                Assert.AreEqual(upperCell, adjust.GetCell(new Vector3(c, r.CeilingZ.Value - 0.05f)), $"{name}: inside, under the ceiling");
                Assert.IsNull(adjust.GetCell(new Vector3(c, r.CeilingZ.Value + 0.1f)), $"{name}: nothing above the pinned ceiling face");
                Assert.AreEqual(floorCell, adjust.GetCell(new Vector3(c, r.GroundPlaneZ + 0.05f)), $"{name}: on the floor");
                Assert.IsNull(adjust.GetCell(new Vector3(c, r.GroundPlaneZ - 0.05f)), $"{name}: nothing under the floor");

                // past the walls (4.5 m out) on the two sides with no doorway: no cell
                var outward = Vector2.Normalize(c - new Vector2(20f, -20f));
                var side = new Vector2(-outward.Y, outward.X);
                Assert.IsNull(adjust.GetCell(new Vector3(c + 4.8f * side, 1f)), $"{name}: beyond the side wall");
                Assert.IsNull(adjust.GetCell(new Vector3(c - 4.8f * side, 1f)), $"{name}: beyond the other side wall");
                Assert.IsNull(adjust.GetCell(new Vector3(c + 4.8f * outward, 1f)), $"{name}: beyond the far wall");
            }
        }

        /// <summary>
        /// No safe ring (review F2 on #2190): every point that is floor in the alcove or on its doorway sill, on a 0.1 m grid,
        /// counts as a player inside the region (HazardRegion.ContainsPlayer, the test PlayersInRegion and the HitsRequireInside
        /// gate use), while hazards are still only PLACED inside the 4.0 m circle. The counts prove the grid reaches past the
        /// placement circle, so a region without the player margin would fail here.
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void EveryFloorPoint_OfEachAlcoveAndItsSill_CountsAsAPlayerInside()
        {
            RequireDats();

            var adjust = AdjustCell.Get(Landblock, 0);
            var outside = new List<string>();

            foreach (var (name, floorCell, _, sillCell) in Alcoves)
            {
                HazardRegionPresets.TryGet(name, 0, out var r);

                int floor = 0, sill = 0, pastPlacement = 0;
                var farthest = 0f;

                for (var dx = -6f; dx <= 6f + 1e-4f; dx += 0.1f)
                    for (var dy = -6f; dy <= 6f + 1e-4f; dy += 0.1f)
                    {
                        var x = r.CenterX + dx;
                        var y = r.CenterY + dy;
                        var cell = adjust.GetCell(new Vector3(x, y, r.GroundPlaneZ + 0.05f));

                        if (cell != floorCell && cell != sillCell)
                            continue;

                        if (cell == floorCell) floor++; else sill++;

                        var d = new Vector2(dx, dy).Length();
                        farthest = Math.Max(farthest, d);

                        if (!r.Contains(x, y))
                            pastPlacement++;

                        if (!r.ContainsPlayer(new Vector3(x, y, r.GroundPlaneZ)))
                            outside.Add($"{name} ({x:0.00}, {y:0.00}) {d:0.00} m out, cell 0x{cell:X8}");
                    }

                Console.WriteLine($"{name}: floor {floor} sill {sill} past placement {pastPlacement} farthest {farthest:0.00} m");

                Assert.IsTrue(floor > 5000, $"{name}: {floor} alcove floor points (68.6 m2 at 0.01 m2 each)");
                Assert.IsTrue(sill > 50, $"{name}: {sill} sill points");
                Assert.IsTrue(pastPlacement > 1000, $"{name}: {pastPlacement} floor points lie past the 4.0 m placement circle, so the margin is load-bearing");
                Assert.IsTrue(farthest > HazardRegionPresets.HarbingerAlcoveRadius + 1.0f && farthest <= HazardRegionPresets.HarbingerAlcovePlayerRadius,
                    $"{name}: the farthest floor point is {farthest:0.00} m out (player radius {HazardRegionPresets.HarbingerAlcovePlayerRadius})");
            }

            Assert.AreEqual(0, outside.Count, $"{outside.Count} floor points are outside the player region: " + string.Join("; ", outside.Take(20)));
        }

        /// <summary>
        /// Round 4: the fire (north) alcove has no exclusion. The strip that was the doorway lane is real floor and is placeable
        /// like the rest of the room (Ember roll rolls balls in every direction, with no safe line from the door to the crystal).
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void FireDoorwayLane_IsFloor_AndPlaceable_NoExclusion()
        {
            RequireDats();

            var adjust = AdjustCell.Get(Landblock, 0);
            HazardRegionPresets.TryGet(HazardRegionPresets.HarbingerAlcoveNorthName, 0, out var r);

            Assert.AreEqual(0, r.Exclusions.Count);

            var lanePoints = 0;

            for (var x = 18.4f; x <= 21.6f + 1e-4f; x += 0.1f)
                for (var y = -4.0f; y <= 4.0f + 1e-4f; y += 0.1f)
                {
                    if (!r.Contains(x, y))
                        continue;

                    lanePoints++;
                    Assert.IsTrue(r.IsPlaceable(x, y), $"lane ({x:0.00}, {y:0.00}) is not placeable");
                    Assert.AreEqual(0x00BC0130u, adjust.GetCell(new Vector3(x, y, 0.05f)), $"lane ({x:0.00}, {y:0.00}) is alcove floor");
                }

            Assert.IsTrue(lanePoints > 2000, $"{lanePoints} lane points");
            Assert.IsFalse(r.InExclusion(20f - 1.5f, -4.0f) || r.InExclusion(20f + 1.5f, -4.0f));
        }

        /// <summary>
        /// The alcove signatures (round 2 B2) on the REAL floor, no safe ring: every pool slot's panel point is in its alcove's
        /// floor cell; in the acid alcove every floor and sill point lies under some pool slot; in the frost alcove every floor
        /// and sill point is within reach of a Whiteout burst; in the fire alcove every point where a player can stand (it and
        /// its 16-point ring at the 0.48 m player body radius are floor or sill) is within reach of some Ember roll lane (round 4: no safe doorway lane). The args are the ones the script hands the runtime.
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void Signatures_ReachEveryRealFloorPoint_AndEveryPoolSlotIsOnTheFloor()
        {
            RequireDats();

            var adjust = AdjustCell.Get(Landblock, 0);
            var settings = ACE.Server.Pinnacles.Harbinger.HarbingerSettings.Defaults;

            bool OnFloor(uint floorCell, uint sillCell, float x, float y)
            {
                var c = adjust.GetCell(new Vector3(x, y, 0.05f));
                return c == floorCell || c == sillCell;
            }

            List<Vector2> FloorPoints(string name, uint floorCell, uint sillCell)
            {
                HazardRegionPresets.TryGet(name, 0, out var r);
                var pts = new List<Vector2>();

                for (var dx = -6f; dx <= 6f + 1e-4f; dx += 0.1f)
                    for (var dy = -6f; dy <= 6f + 1e-4f; dy += 0.1f)
                        if (OnFloor(floorCell, sillCell, r.CenterX + dx, r.CenterY + dy))
                            pts.Add(new Vector2(r.CenterX + dx, r.CenterY + dy));

                return pts;
            }

            var failures = new List<string>();

            // every pool slot of every alcove: its panel point (the pool surface z) is in the alcove's floor cell
            foreach (var alcove in ACE.Server.Pinnacles.Harbinger.HarbingerData.Alcoves)
            {
                var row = Alcoves.Single(a => a.Name == ACE.Server.Pinnacles.Harbinger.HarbingerLayout.RegionPreset(alcove));

                for (var i = 0; i < ACE.Server.Pinnacles.Harbinger.HarbingerLayout.PoolSlotsUV.Count; i++)
                {
                    var c = ACE.Server.Pinnacles.Harbinger.HarbingerLayout.PoolSlot(alcove, i);
                    var cell = adjust.GetCell(new Vector3(c.X, c.Y, (float)ACE.Server.Pinnacles.Harbinger.HarbingerSettings.PoolSurfaceZ));

                    if (cell != row.FloorCell)
                        failures.Add($"{alcove.Name} pool slot {i} ({c.X:0.00}, {c.Y:0.00}) is in cell 0x{cell:X8}, not the floor 0x{row.FloorCell:X8}");
                }
            }

            // acid: every floor and sill point under some pool slot
            {
                var alcove = ACE.Server.Pinnacles.Harbinger.HarbingerData.AlcoveOf(ACE.Server.Pinnacles.Harbinger.HarbingerElement.Acid);
                var (name, floorCell, _, sillCell) = Alcoves.Single(a => a.Name == HazardRegionPresets.HarbingerAlcoveWestName);
                var pts = FloorPoints(name, floorCell, sillCell);
                var missed = pts.Where(pt => !ACE.Server.Tests.Pinnacles.HarbingerSignatureReach.PoolCovers(alcove, settings.PoolSize, pt)).ToList();

                Assert.IsTrue(pts.Count > 5000, $"acid {pts.Count} floor points");
                failures.AddRange(missed.Take(10).Select(pt => $"acid: no pool slot covers ({pt.X:0.00}, {pt.Y:0.00})"));
            }

            // frost: every floor and sill point within reach of a burst
            {
                HazardRegionPresets.TryGet(HazardRegionPresets.HarbingerAlcoveSouthName, 0, out var r);
                var (name, floorCell, _, sillCell) = Alcoves.Single(a => a.Name == HazardRegionPresets.HarbingerAlcoveSouthName);
                var rs = RainSettings.From(settings.WhiteoutArgs(0.04, false));
                var pts = FloorPoints(name, floorCell, sillCell);
                var missed = pts.Where(pt => !ACE.Server.Tests.Pinnacles.HarbingerSignatureReach.WhiteoutHits(r, rs, pt)).ToList();

                Assert.IsTrue(pts.Count > 5000, $"frost {pts.Count} floor points");
                failures.AddRange(missed.Take(10).Select(pt => $"frost: no burst reaches ({pt.X:0.00}, {pt.Y:0.00})"));
            }

            // fire (round 4: no doorway lane): every standing point within reach of some lane
            {
                HazardRegionPresets.TryGet(HazardRegionPresets.HarbingerAlcoveNorthName, 0, out var r);
                var (name, floorCell, _, sillCell) = Alcoves.Single(a => a.Name == HazardRegionPresets.HarbingerAlcoveNorthName);
                var bp = BallRules.Read(settings.EmberArgs(0.2, false), BallPatterns.Random);
                var lanes = ACE.Server.Tests.Pinnacles.HarbingerSignatureReach.EmberLanes(r, bp);
                var disc = ACE.Server.Tests.Pinnacles.HarbingerSignatureReach.PlayerDisc;

                bool Standing(Vector2 pt)
                    => Enumerable.Range(0, 16).All(k =>
                    {
                        var a = k * Math.PI / 8.0;
                        return OnFloor(floorCell, sillCell, pt.X + disc * (float)Math.Cos(a), pt.Y + disc * (float)Math.Sin(a));
                    });

                var standing = FloorPoints(name, floorCell, sillCell).Where(Standing).ToList();
                var missed = standing.Where(pt => !ACE.Server.Tests.Pinnacles.HarbingerSignatureReach.EmberHits(lanes, bp.Reach, pt)).ToList();

                Console.WriteLine($"fire: {standing.Count} standing points, {missed.Count} out of reach");
                Assert.IsTrue(standing.Count > 2500, $"fire {standing.Count} standing points");
                failures.AddRange(missed.Take(10).Select(pt => $"fire: no lane reaches ({pt.X:0.00}, {pt.Y:0.00})"));
            }

            Assert.AreEqual(0, failures.Count, string.Join("; ", failures));
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
