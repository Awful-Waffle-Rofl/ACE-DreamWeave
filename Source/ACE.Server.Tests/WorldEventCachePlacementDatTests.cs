using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using ACE.DatLoader;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Physics;
using ACE.Server.WorldEvents;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Position = ACE.Entity.Position;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The Weave Cache placement against REAL outdoor geometry, at the prod failure anchor of 2026-11-23 run 4:
    /// 0x934B001A [76.300003 38.099998 14.000000], landblock loaded from client_cell_1.dat. Drives the real spawn
    /// path (WorldObject.InitPhysicsObj then AddPhysicsObj: LScape.get_landcell, PhysicsObj.enter_world with
    /// Placement | Slide) on every cache weenie rewards.json can draw, built from its committed SQL (Setup,
    /// PhysicsState, DefaultScale), with human-sized bodies standing in for the crowd.
    ///
    /// What it establishes: the empty spot takes every cache (so terrain, Z and statics are not the cause); a
    /// crowd around the centre refuses a solid cache there, which is what the old plan tried twice; the new plan's
    /// first accepted attempt then lands; and an ethereal cache enters inside a crowd that refuses every solid one.
    /// It characterises the engine and the plan, not SpawnOne's wiring (no LandblockManager or WorldEvent here).
    ///
    /// Same dat bootstrap as WorldEventGroundProbeDatTests: PhysicsEngine.Server is false so LScape builds the
    /// landblock from the cell dat, and the cell dat is swapped into DatManager only for each test.
    /// </summary>
    [TestClass]
    public class WorldEventCachePlacementDatTests
    {
        private const uint Cell = 0x934B001A;
        private const float AX = 76.300003f, AY = 38.099998f, AZ = 14.0f;

        /// <summary>A crowd radius that refuses a solid cache at the centre (measured: 3 m does not, 4.5 m does).</summary>
        private const float BlockingCrowdRadius = 4.5f;

        private const float CrowdSpacing = 1.2f;

        private static uint nextGuid = 0x7D1E0001;

        private static Position Anchor() => new Position(Cell, AX, AY, AZ, 0f, 0f, 0f, 1f, 0);

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void EmptyAnchor_TakesEveryCacheShape()
        {
            RequireDats();

            var report = new StringBuilder();

            var ground = Anchor();
            ground.AdjustMapCoords();
            report.AppendLine($"terrain z under the anchor = {ground.PositionZ}");
            Assert.AreEqual(AZ, ground.PositionZ, 0.01f, "the web anchor's Z is the terrain height there, so an un-snapped anchor is not what failed");

            foreach (var shape in CacheShapes())
            {
                var ok = TryEnter(shape, Anchor(), false, out var landed, keep: false);
                report.AppendLine($"{shape.Wcid}: ok={ok} landed={landed?.ToLOCString()}");
                Assert.IsTrue(ok, $"cache {shape.Wcid} must enter the empty anchor\n{report}");
            }

            Console.WriteLine(report);
        }

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void CrowdedCentre_RefusesTheOldPlan_AndTheNewPlanPlacesEveryShape()
        {
            RequireDats();

            var report = new StringBuilder();
            var crowd = SpawnCrowd(BlockingCrowdRadius);
            report.AppendLine($"crowd of {crowd.Count} bodies within {BlockingCrowdRadius} m");

            try
            {
                var refusedSomewhere = false;

                foreach (var shape in CacheShapes())
                {
                    // the old plan for a single cache: the anchor, then "the anchor" again
                    var oldFirst = TryEnter(shape, Anchor(), false, out _, keep: false);
                    var oldRetry = TryEnter(shape, Anchor(), false, out _, keep: false);

                    if (!oldFirst && !oldRetry)
                        refusedSomewhere = true;

                    var plan = WorldEventRewardDelivery.BuildPlacementPlan(Anchor(), Anchor(), p => p.AdjustMapCoords());
                    Position landed = null;

                    var index = WorldEventRewardDelivery.PlaceFirstAccepted(plan,
                        a => TryEnter(shape, a.Position, a.Ethereal, out landed, keep: false)
                            ? WorldEventRewardDelivery.CachePlaceResult.Placed
                            : WorldEventRewardDelivery.CachePlaceResult.Refused,
                        () => false).Index;

                    report.AppendLine($"{shape.Wcid} scale={shape.Scale}: old={oldFirst}/{oldRetry} new index={index} kind={(index >= 0 ? plan[index].Kind : "-")} eth={(index >= 0 && plan[index].Ethereal)} landed={landed?.ToLOCString()}");

                    Assert.IsTrue(index >= 0, $"the new plan must place cache {shape.Wcid}\n{report}");
                    Assert.IsTrue(landed.Cell >> 16 == Cell >> 16, $"and on the anchor's landblock\n{report}");
                }

                Assert.IsTrue(refusedSomewhere, $"positive control: the crowd must refuse the old plan for at least one cache shape, or this test proves nothing\n{report}");
            }
            finally
            {
                foreach (var body in crowd)
                    body.PhysicsObj?.leave_world();
            }

            Console.WriteLine(report);
        }

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void EtherealCache_EntersWhereTheCrowdRefusesASolidOne()
        {
            RequireDats();

            var report = new StringBuilder();
            var crowd = SpawnCrowd(BlockingCrowdRadius + 1.5f);

            try
            {
                foreach (var shape in CacheShapes())
                {
                    var solid = TryEnter(shape, Anchor(), false, out _, keep: false);
                    var ethereal = TryEnter(shape, Anchor(), true, out var landed, keep: false);

                    report.AppendLine($"{shape.Wcid}: solid={solid} ethereal={ethereal} landed={landed?.ToLOCString()}");

                    Assert.IsTrue(ethereal, $"an ethereal cache {shape.Wcid} must enter inside the crowd\n{report}");
                }

                Assert.IsTrue(report.ToString().Contains("solid=False"), $"positive control: the crowd refuses a solid cache\n{report}");
            }
            finally
            {
                foreach (var body in crowd)
                    body.PhysicsObj?.leave_world();
            }

            Console.WriteLine(report);
        }

        // ================= helpers =================

        private sealed class CacheShape
        {
            public uint Wcid;
            public uint Setup;
            public int PhysicsState;
            public float Scale;
        }

        private static List<WorldObject> SpawnCrowd(float radius)
        {
            var crowd = new List<WorldObject>();

            for (var gx = -radius; gx <= radius; gx += CrowdSpacing)
            {
                for (var gy = -radius; gy <= radius; gy += CrowdSpacing)
                {
                    if (gx * gx + gy * gy > radius * radius)
                        continue;

                    var at = new Position(Cell, AX + gx, AY + gy, AZ, 0f, 0f, 0f, 1f, 0);
                    at.AdjustMapCoords();

                    var body = MakeBody(at);
                    body.InitPhysicsObj();

                    if (body.AddPhysicsObj())
                        crowd.Add(body);
                }
            }

            Assert.IsTrue(crowd.Count > 10, $"setup: the crowd must stand ({crowd.Count} bodies entered)");

            return crowd;
        }

        private static Creature MakeBody(Position at)
        {
            var weenie = new Weenie
            {
                WeenieClassId = 999851,
                ClassName = "cacheplacementcrowdbody",
                WeenieType = WeenieType.Creature,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Cache Placement Crowd Body" } },
                PropertiesDID = new Dictionary<PropertyDataId, uint> { { PropertyDataId.Setup, 0x02000001 } },   // human male, 0.48 m sphere
            };

            TestGameTables.EnsureInitialized();

            return new Creature(weenie, new ObjectGuid(nextGuid++)) { Location = at };
        }

        private static bool TryEnter(CacheShape shape, Position at, bool ethereal, out Position landed, bool keep)
        {
            var weenie = new Weenie
            {
                WeenieClassId = 999850,
                ClassName = "cacheplacementshape" + shape.Wcid,
                WeenieType = WeenieType.Generic,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Cache Placement Shape " + shape.Wcid } },
                PropertiesDID = new Dictionary<PropertyDataId, uint> { { PropertyDataId.Setup, shape.Setup } },
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.PhysicsState, shape.PhysicsState } },
                PropertiesBool = new Dictionary<PropertyBool, bool> { { PropertyBool.Stuck, true } },
                PropertiesFloat = new Dictionary<PropertyFloat, double> { { PropertyFloat.DefaultScale, shape.Scale } },
            };

            var wo = new GenericObject(weenie, new ObjectGuid(nextGuid++));

            // The production sequence (TryPlace calls the same helper with EnterWorld as the enter step); only the
            // enter step differs, because there is no LandblockManager here.
            var ok = WorldEventRewardDelivery.TryEnterCache(wo, at, ethereal, w =>
            {
                w.InitPhysicsObj();
                return w.AddPhysicsObj();
            });
            landed = ok ? new Position(wo.Location) : null;

            if (ok && !keep)
                wo.PhysicsObj?.leave_world();

            return ok;
        }

        /// <summary>
        /// Every cache rewards.json can draw (each reward's cacheWcid and cacheWcids), shaped from its committed SQL,
        /// so a new chest added to the pool is covered without editing this test.
        /// </summary>
        private static List<CacheShape> CacheShapes()
        {
            var root = RepoRoot();
            var rewards = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "Content", "events", "axes", "rewards.json")));
            var wcids = new SortedSet<uint>();

            foreach (var reward in rewards.RootElement.GetProperty("rewards").EnumerateArray())
            {
                if (reward.TryGetProperty("cacheWcid", out var single))
                    wcids.Add(single.GetUInt32());

                if (reward.TryGetProperty("cacheWcids", out var pool))
                    foreach (var w in pool.EnumerateArray())
                        wcids.Add(w.GetUInt32());
            }

            Assert.IsTrue(wcids.Count >= 7, $"rewards.json lists {wcids.Count} cache wcids");

            var weenieDir = Path.Combine(root, "Content", "sql", "weenies");
            var shapes = new List<CacheShape>();

            foreach (var wcid in wcids)
            {
                var file = Directory.GetFiles(weenieDir, wcid + " *.sql").SingleOrDefault();
                Assert.IsNotNull(file, $"no weenie unit for cache wcid {wcid}");

                var sql = File.ReadAllText(file);

                var setup = Regex.Match(sql, $@"\({wcid},\s*1,\s*0x([0-9A-Fa-f]{{8}})\)\s*/\*\s*Setup");
                var state = Regex.Match(sql, $@"\({wcid},\s*93,\s*(\d+)\)");
                var scale = Regex.Match(sql, $@"\({wcid},\s*39,\s*([0-9.]+)\)");

                Assert.IsTrue(setup.Success && state.Success, $"cache {wcid}: Setup and PhysicsState rows must be readable from {file}");

                shapes.Add(new CacheShape
                {
                    Wcid = wcid,
                    Setup = uint.Parse(setup.Groups[1].Value, NumberStyles.HexNumber),
                    PhysicsState = int.Parse(state.Groups[1].Value),
                    Scale = scale.Success ? float.Parse(scale.Groups[1].Value, CultureInfo.InvariantCulture) : 1f,
                });
            }

            return shapes;
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            // Both, because the build copies Content/events into the test output directory but not the weenie SQL.
            while (dir != null && !(File.Exists(Path.Combine(dir.FullName, "Content", "events", "axes", "rewards.json"))
                && Directory.Exists(Path.Combine(dir.FullName, "Content", "sql", "weenies"))))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find Content/events/axes/rewards.json and Content/sql/weenies by walking up from {AppContext.BaseDirectory}");

            return dir.FullName;
        }

        // ================= dat handling (as WorldEventGroundProbeDatTests) =================

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
                ACE.Server.Physics.Common.LScape.unload_landblock(Cell | 0xFFFF, 0);
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
