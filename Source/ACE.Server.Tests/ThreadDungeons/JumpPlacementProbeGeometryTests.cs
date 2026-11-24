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
using ACE.Server.ThreadDungeons.Defs;
using ACE.Server.WorldObjects;

using Position = ACE.Entity.Position;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// JumpPlacementProbe over REAL terrain: Lonely Fort (landblock 0x1039, the ow_lonelyfort outdoor row,
    /// client_cell_1.dat), the prod case of 2026-11-12 (run 0x8000630B, encounter #1 at 0x10390035 seed 434432745, every
    /// use refused because pad 0x1039002D [138.316864 107.285568 43.595619] would not enter). Proves (1) the probe's
    /// verdict equals the real spawn's verdict (InitPhysicsObj + AddPhysicsObj, then Form's LandedAt) at the failing pad,
    /// a clear spot above the Trialmaster, a ring around the obstacle and a slab sunk into the terrain, in both physics
    /// modes; (2) the request seed's layout reproduces the prod failing pad, so the run inputs here are prod's; and (3)
    /// Place, with the production ground and probe, now refuses that layout and accepts a derived one, or reports the
    /// course obstructed. Local only: skipped (Inconclusive) without the dats.
    /// </summary>
    [TestClass]
    public class JumpPlacementProbeGeometryTests
    {
        private const ushort Landblock = 0x1039;
        private const uint RunId = 0x8000630Bu;
        private const int SwickSeed = 434432745;
        private const uint PadSetup = 0x02000DB3;   // 1007961 Skyward Platform
        private const int PadPhysicsState = 24;
        private const uint GoalSetup = 0x02000ADF;  // 1007962 Summit Bell
        private const int GoalPhysicsState = 20;

        private static readonly Vector3 FailingPad = new Vector3(138.316864f, 107.285568f, 43.595619f);
        private static readonly Vector3 Trialmaster = new Vector3(145.59f, 110.39f, 28.005f);

        private static readonly object initLock = new object();
        private static bool initialized;
        private static string skipReason;
        private static uint nextGuid = 0x7F0E4001;

        public TestContext TestContext { get; set; }

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void The_probe_agrees_with_the_real_spawn_and_rejects_the_prod_pad()
        {
            RequireDats();

            var spots = new List<(string Name, Vector3 At)>
            {
                ("prod failing pad", FailingPad),
                ("above the Trialmaster", Trialmaster + new Vector3(0, 0, 4f)),
                ("clear, high", new Vector3(150f, 150f, 60f)),
            };

            foreach (var dx in new[] { -3f, 0f, 3f })
                foreach (var dy in new[] { -3f, 0f, 3f })
                    spots.Add(($"ring {dx},{dy}", new Vector3(139.5f + dx, 106.5f + dy, 43.6f)));

            var lines = new List<string>();
            var sunkControlRan = false;

            foreach (var server in new[] { true, false })
            {
                WithPhysicsMode(server, () =>
                {
                    // Control: a slab sunk 0.2 m into the terrain at the Trialmaster must be moved or refused by BOTH.
                    var groundAt = Pos(Trialmaster.X, Trialmaster.Y, Trialmaster.Z);
                    Assert.IsTrue(ThreadDungeonSpawner.ProbeOutdoorGround(groundAt, Trialmaster.Z, 1000f, out var groundZ), "the Trialmaster stands on open ground");
                    var all = spots.Concat(new[] { ("sunk slab", new Vector3(Trialmaster.X, Trialmaster.Y, groundZ - 0.2f)) });

                    foreach (var (name, at) in all)
                    {
                        var intended = Pos(at.X, at.Y, at.Z);
                        var probe = ProbeLeavingNoTrace(intended, name);
                        var real = RealVerdict(intended, out var realAt);

                        lines.Add($"server={server} {name} {intended.ToLOCString()}: probe={(probe ?? "clear")} real={(real ? "clear" : "refused")} {realAt}");
                        Assert.AreEqual(real, probe == null, $"server={server} {name}: the probe ({probe ?? "clear"}) disagrees with the real spawn ({(real ? "clear" : "refused")} {realAt})");

                        if (name == "sunk slab")
                        {
                            Assert.IsFalse(real, "control: a slab cutting the terrain would be moved or refused, or the comparison proves nothing");
                            sunkControlRan = true;
                        }
                    }

                    Assert.IsNotNull(JumpPlacementProbe.Probe(_ => MakePad(), 1007961, Pos(FailingPad.X, FailingPad.Y, FailingPad.Z), true), "the prod pad is rejected");
                    Assert.IsNull(JumpPlacementProbe.Probe(_ => MakePad(), 1007961, Pos(Trialmaster.X, Trialmaster.Y, Trialmaster.Z + 4f), true), "a clear spot above the Trialmaster is accepted");
                });
            }

            Assert.IsTrue(sunkControlRan);
            TestContext.WriteLine(string.Join(System.Environment.NewLine, lines));
        }

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void Swicks_case_now_places_on_a_derived_layout_or_reports_the_course_obstructed()
        {
            RequireDats();

            var savedCreate = JumpEncounterType.CreateObject;
            var savedEnter = JumpEncounterType.EnterWorld;
            var savedGround = JumpEncounterType.GroundFor;
            var savedProbe = JumpEncounterType.ProbePlacement;

            try
            {
                var store = ThreadDungeonStore.Load(ThreadDungeonStoreTests.FindDynamicDir());
                Assert.IsTrue(store.Dungeons.TryGetValue("ow_lonelyfort", out var dungeon), "index.json has the ow_lonelyfort row");

                var point = dungeon.Points.Single(p => p.Cell == 0x10390035 && Math.Abs(p.X - 145.59f) < 0.01f && Math.Abs(p.Y - 110.39f) < 0.01f);

                var text = File.ReadAllText(Path.Combine(ThreadDungeonStoreTests.FindDynamicDir(), ThreadEncounterStore.FileName));
                var def = ThreadEncounterStore.Parse(text, null, new List<string>()).Types.Single(t => t.Kind == JumpEncounterType.KindName);

                var spec = new DungeonGemSpec(dungeon.Id, 200, 6, "any", 11, new (string, double)[0], 0, 0);
                var run = new ThreadDungeonRun(RunId, 0x50000001u, "Swick", 7, 0x80000099u, spec, dungeon, DateTime.UtcNow, TimeSpan.FromMinutes(180))
                {
                    EncountersEnabled = true,
                    EncountersMaxPerRun = 4,
                    EncounterSlotChance = 1.0,
                };
                run.MarkPopulated(planned: 1, spawned: 1, bossWcid: 0);

                var settings = new OutdoorPerimeterSettings(
                    OutdoorSiteTunables.DefaultWarningBandM, OutdoorSiteTunables.DefaultBackstopM, OutdoorSiteTunables.DefaultUnravelEnabled,
                    OutdoorSiteTunables.DefaultUnravelStartPct, OutdoorSiteTunables.DefaultUnravelStepPct, OutdoorSiteTunables.DefaultUnravelLethal,
                    OutdoorSiteTunables.DefaultUnravelGraceS, OutdoorSiteTunables.DefaultBackstopEnabled,
                    OutdoorSiteTunables.DefaultContainmentMarginM, OutdoorSiteTunables.DefaultInteriorAwarenessM);
                var perimeter = OutdoorRunPerimeter.For(dungeon, settings, 1.0);
                Assert.IsNotNull(perimeter, "the row has a perimeter");
                run.MarkEncounterContext(new ThreadEncounterRunContext(null, 0u, perimeter, new Dictionary<ushort, ACE.Server.Entity.Landblock>(), null));

                var type = new JumpEncounterType();
                var candidate = new ThreadEncounterCandidate(point, ThreadEncounterGeometry.Global(point, true), 100f, 100f);
                var instance = run.NewEncounterInstance(new ThreadEncounterPick(1, def, candidate, SwickSeed), type);
                var request = new ThreadEncounterPlaceRequest(run, null, instance, run.EncounterContext);
                var config = JumpEncounterType.ParseConfig(def.Config, out var configError);
                Assert.IsNull(configError);

                // The real ground (production GroundFor) and the real probe; only the factory and the NPC's EnterWorld are fakes.
                JumpEncounterType.CreateObject = wcid => wcid == config.PlatformWcid ? MakePad() : wcid == config.GoalWcid ? MakeGoal() : MakeNpc(wcid);
                JumpEncounterType.EnterWorld = _ => true;

                // Control: the request seed's layout is prod's (it holds the pad prod logged as not entered).
                var original = JumpCourse.Generate(config, new Random(SwickSeed), Trialmaster, JumpEncounterType.GroundFor(request), JumpEncounterType.AllowedFor(request), out var why);
                Assert.IsNotNull(original, "the request seed generates: " + why);
                Assert.IsTrue(original.Pads.Any(p => Vector3.Distance(new Vector3(p.X, p.Y, p.Bottom), FailingPad) < 0.001f),
                    "the request seed's layout holds the prod failing pad: " + string.Join("; ", original.Pads.Select(p => $"{p.X},{p.Y},{p.Bottom}")));
                Assert.IsNotNull(JumpEncounterType.Obstruction(request, config, original), "and the probe refuses that layout");

                var placed = WithServerPhysics(() => type.Place(request, out var f) ? null : f);

                TestContext.WriteLine(placed == null
                    ? $"PLACED on layout seed {((JumpState)instance.State).LayoutSeed} (request seed {SwickSeed}); outcomes: {string.Join(" | ", run.EncounterOutcomes)}"
                    : $"REFUSED: {placed}");

                if (placed == null)
                {
                    var state = (JumpState)instance.State;
                    Assert.AreNotEqual(SwickSeed, state.LayoutSeed, "a derived seed was accepted");
                    Assert.IsTrue(Enumerable.Range(1, JumpEncounterType.LayoutAttempts - 1).Any(k => JumpEncounterType.LayoutSeedFor(SwickSeed, k) == state.LayoutSeed));
                }
                else
                {
                    StringAssert.Contains(placed, $"the course is obstructed after {JumpEncounterType.LayoutAttempts} layouts");
                }
            }
            finally
            {
                JumpEncounterType.CreateObject = savedCreate;
                JumpEncounterType.EnterWorld = savedEnter;
                JumpEncounterType.GroundFor = savedGround;
                JumpEncounterType.ProbePlacement = savedProbe;
            }
        }

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void A_Setup_the_spawn_refuses_is_refused_and_torn_down()
        {
            RequireDats();

            GenericObject made = null;
            var why = WithServerPhysics(() => JumpPlacementProbe.Probe(_ => made = Make(1007961, "bad setup", 0x01000001, PadPhysicsState, ethereal: false), 1007961, Pos(Trialmaster.X, Trialmaster.Y, Trialmaster.Z + 4f), true));

            Assert.IsNotNull(why);
            StringAssert.Contains(why, "is not a valid setup model");
            Assert.IsTrue(made.IsDestroyed);
            Assert.IsNull(made.PhysicsObj, "refused before any physics body was built");
        }

        // ================= helpers =================

        /// <summary>
        /// Probe once, and assert the probe left nothing behind: the ServerObjectManager holds what it held before, the
        /// WorldObject is destroyed and never joined a landblock, and its physics body (if built) never joined a cell.
        /// </summary>
        private static string ProbeLeavingNoTrace(Position intended, string name)
        {
            GenericObject made = null;
            var before = ACE.Server.Physics.Managers.ServerObjectManager.ServerObjects.Count;

            var verdict = JumpPlacementProbe.Probe(_ => made = MakePad(), 1007961, intended, true);

            Assert.AreEqual(before, ACE.Server.Physics.Managers.ServerObjectManager.ServerObjects.Count, $"{name}: the probe left a ServerObjectManager entry");
            Assert.IsNotNull(made, $"{name}: the probe created its object");
            Assert.IsTrue(made.IsDestroyed, $"{name}: the probe object is destroyed");
            Assert.IsNull(made.CurrentLandblock, $"{name}: the probe object never joined a landblock");
            Assert.IsTrue(made.PhysicsObj == null || made.PhysicsObj.CurCell == null && made.PhysicsObj.ShadowObjects.Count == 0, $"{name}: the probe body never joined a cell");
            return verdict;
        }

        private static Position Pos(float x, float y, float z)
        {
            var p = new Position((uint)Landblock << 16 | 0x0001, x, y, z, 0f, 0f, 0f, 1f, RunId);
            p.SetLandCell();
            return p;
        }

        /// <summary>The real spawn path's verdict (Landblock.AddWorldObjectInternal's physics half), then Form's LandedAt.</summary>
        private static bool RealVerdict(Position intended, out string at)
        {
            var wo = MakePad();
            wo.Location = new Position(intended);
            wo.InitPhysicsObj();
            var ok = wo.AddPhysicsObj();
            at = ok ? wo.Location.ToLOCString() : "not entered";

            if (!ok)
                return false;

            var landed = wo.Location.LandblockId.Landblock == intended.LandblockId.Landblock
                         && Vector3.Distance(wo.Location.Pos, intended.Pos) <= JumpEncounterType.PlacementTolerance;

            wo.PhysicsObj.DestroyObject();
            return landed;
        }

        private static GenericObject MakePad() => Make(1007961, "Skyward Platform", PadSetup, PadPhysicsState, ethereal: false);

        private static GenericObject MakeGoal() => Make(1007962, "Summit Bell", GoalSetup, GoalPhysicsState, ethereal: true);

        private static GenericObject MakeNpc(uint wcid) => new GenericObject(new Weenie
        {
            WeenieClassId = wcid,
            WeenieType = WeenieType.Generic,
            PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "fake npc" } },
        }, new ObjectGuid(nextGuid++));

        private static GenericObject Make(uint wcid, string name, uint setup, int physicsState, bool ethereal)
            => new GenericObject(new Weenie
            {
                WeenieClassId = wcid,
                ClassName = "jumpprobetest" + wcid,
                WeenieType = WeenieType.Generic,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, name } },
                PropertiesDID = new Dictionary<PropertyDataId, uint> { { PropertyDataId.Setup, setup } },
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.PhysicsState, physicsState } },
                PropertiesBool = new Dictionary<PropertyBool, bool>
                {
                    { PropertyBool.Stuck, true },
                    { PropertyBool.Ethereal, ethereal },
                    { PropertyBool.GravityStatus, false },
                },
            }, new ObjectGuid(nextGuid++));

        private static T WithServerPhysics<T>(Func<T> body)
        {
            T result = default;
            WithPhysicsMode(true, () => result = body());
            return result;
        }

        private static void WithPhysicsMode(bool server, Action body)
        {
            var engine = PhysicsEngine.Instance;
            var was = engine.Server;
            engine.Server = server;

            try
            {
                body();
            }
            finally
            {
                engine.Server = was;
            }
        }

        // ================= dat handling (as JumpCourseHoltburgGeometryTests) =================

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
                WithPhysicsMode(false, () =>
                {
                    // The placement and ground probes may touch the neighbouring landblocks too.
                    foreach (var dx in new[] { -1, 0, 1 })
                        foreach (var dy in new[] { -1, 0, 1 })
                        {
                            var lb = (uint)(((Landblock >> 8) + dx) << 8 | ((Landblock & 0xFF) + dy));
                            ACE.Server.Physics.Common.LScape.unload_landblock(lb << 16 | 0xFFFF, RunId);
                            AdjustCell.TryRemove(lb, RunId);
                        }
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
