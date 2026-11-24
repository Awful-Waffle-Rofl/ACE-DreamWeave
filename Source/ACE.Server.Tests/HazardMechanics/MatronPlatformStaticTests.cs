using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;

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
    /// 2026-11-15 (Discord bug report 1570812629348393113): player summons did not stand on the Matron Knot's jump platforms.
    /// 1007551 Matron Platform copies the Ward Plane's PhysicsState 24, a non-Static body, and an ethereal mover (every pet:
    /// Pet sets Ethereal) passes through a non-Static object. The fix is the Grael one (#1986): ForcePhysicsStatic (9091) on
    /// the weenie. These tests pin that row to the fixture, show an ethereal pet resting on every Static platform (and
    /// falling through without the bool), and prove the #1986 caveat does not bite here: the acid fill's panels, which are
    /// ethereal moving bodies and so now meet the slabs, spawn and snap exactly as they did with non-Static platforms at
    /// every surface height the fill passes through. Dat handling as MatronRoomGeometryTests. Skips (Inconclusive) without dats.
    /// </summary>
    [TestClass]
    public class MatronPlatformStaticTests
    {
        private const uint Landblock = 0x586B;
        private const uint PlatformWcid = 1007551;
        private const uint PlatformSetup = 0x02000DB3;     // 1007551 PropertyDataId 1
        private const int PlatformPhysicsState = 24;       // 1007551 PropertyInt 93
        private const uint PetSetup = 0x02001C26;          // 1007703 Ossuary Adept (a Custom Summons CombatPet), as PinnacleArenaGeometryTests
        private const uint PetMotionTable = 0x09000001;
        private const int PetPhysicsState = 1036;          // ReportCollisions, Ethereal, Gravity

        private static readonly object initLock = new object();
        private static bool initialized;
        private static string skipReason;
        private static uint nextGuid = 0x7F0F5001;

        /// <summary>The bools of the shipped weenie (Content/sql/weenies/1007551 Matron Platform.sql); <paramref name="forceStatic"/> false is the pre-2026-11-15 slab.</summary>
        private static Dictionary<PropertyBool, bool> PlatformBools(bool forceStatic)
        {
            var bools = new Dictionary<PropertyBool, bool>
            {
                { PropertyBool.Stuck, true }, { PropertyBool.ReportCollisions, true }, { PropertyBool.Ethereal, false },
                { PropertyBool.GravityStatus, false }, { PropertyBool.Visibility, false }, { PropertyBool.UiHidden, true },
                { PropertyBool.SpawnAtExactPosition, true },
            };

            if (forceStatic)
                bools[PropertyBool.ForcePhysicsStatic] = true;

            return bools;
        }

        /// <summary>
        /// The dat tests below build platforms with <see cref="PlatformPhysicsState"/> and <see cref="PlatformBools"/>; this pins both
        /// to the shipped weenie: its PhysicsState (int 93) and every bool, ForcePhysicsStatic (9091) included. No dats needed.
        /// </summary>
        [TestMethod]
        public void MatronPlatformWeenie_MatchesTheFixturePlatforms()
        {
            var sql = ACE.Server.Tests.ThreadDungeons.PooledLootSourceText.Read("Content/sql/weenies/1007551 Matron Platform.sql");
            var ints = new Regex(@"\(\s*1007551\s*,\s*93\s*,\s*(?<v>\d+)\s*\)").Matches(sql);

            Assert.AreEqual(1, ints.Count, "exactly one PhysicsState (int 93) row in the Matron Platform weenie");

            var state = int.Parse(ints[0].Groups["v"].Value, System.Globalization.CultureInfo.InvariantCulture);

            Assert.AreEqual(PlatformPhysicsState, state, "the fixture's PlatformPhysicsState is the weenie's PhysicsState");
            Assert.IsFalse(((PhysicsState)state).HasFlag(PhysicsState.Ethereal), "never Ethereal, or nothing collides with it");

            var bools = new Dictionary<int, bool>();
            foreach (Match m in new Regex(@"\(\s*1007551\s*,\s*(?<k>\d+)\s*,\s*(?<v>True|False)\s*\)").Matches(sql))
                bools.Add(int.Parse(m.Groups["k"].Value, System.Globalization.CultureInfo.InvariantCulture), m.Groups["v"].Value == "True");

            Assert.IsTrue(bools.TryGetValue((int)PropertyBool.ForcePhysicsStatic, out var forced) && forced,
                "the weenie carries ForcePhysicsStatic True, or ethereal bodies (player summons) fall through the platforms");

            var fixture = PlatformBools(true);
            Assert.AreEqual(fixture.Count, bools.Count, "the fixture sets every bool the weenie ships, and no other: " + string.Join(", ", bools.Keys));

            foreach (var (key, value) in fixture)
            {
                Assert.IsTrue(bools.TryGetValue((int)key, out var shipped), $"the weenie has a {key} ({(int)key}) row, as the fixture does");
                Assert.AreEqual(value, shipped, $"{key}: the fixture platform matches the weenie");
            }

            Assert.AreEqual(PlatformWcid, MatronData.PlatformWcid, "the wcid the script spawns as its platforms");
        }

        /// <summary>
        /// A real CombatPet body (1007703's setup, motion table and PhysicsState; the Pet ctor makes it ethereal, as live)
        /// dropped 1 m over the middle of every one of the 20 platforms comes to rest on the slab top when the platforms carry
        /// the bool. CONTROL: with the same platforms WITHOUT the bool (the shipped pre-fix slab) the same drop passes through
        /// the slab and ends well under its top, so the test can see the bug.
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void EtherealPet_DroppedOnEveryPlatform_RestsOnTheStaticTop_AndFallsThroughWithoutTheBool()
        {
            RequireDats();

            foreach (var forceStatic in new[] { false, true })
            {
                var platforms = SpawnAllPlatforms(forceStatic);

                try
                {
                    Assert.AreEqual(20, platforms.Count);
                    Assert.AreEqual(forceStatic, platforms.All(p => p.Body.PhysicsObj.State.HasFlag(PhysicsState.Static)), $"ForcePhysicsStatic {forceStatic}: the slabs' bodies carry Static exactly when the bool is set");

                    foreach (var (name, at, _) in platforms)
                    {
                        var top = at.Z + MatronLayout.PlatformThickness;
                        var drop = new Vector3(at.X, at.Y, top + 1.0f);
                        var cell = MatronData.HallCell(drop.X, drop.Y, drop.Z);
                        Assert.AreNotEqual(0u, cell, $"{name}: the drop point lies in a hall cell");

                        var pet = MakeCombatPet(cell, drop);

                        try
                        {
                            Assert.IsTrue(TrySpawn(pet), $"{name}: the pet spawns 1 m over the slab");
                            Assert.IsTrue(pet.PhysicsObj.State.HasFlag(PhysicsState.Ethereal), "the pet's body is ethereal, as Pet makes every pet");
                            Assert.IsTrue(pet.PhysicsObj.State.HasFlag(PhysicsState.Gravity), "and has gravity");

                            Settle(pet);

                            var z = pet.Location.PositionZ;

                            if (forceStatic)
                            {
                                Assert.AreEqual(top, z, 0.02f, $"{name}: an ethereal pet rests on the Static slab top {top:0.000}; z {z:0.000}");
                                Assert.AreEqual(drop.X, pet.Location.PositionX, 0.02f, $"{name}: not slid");
                                Assert.AreEqual(drop.Y, pet.Location.PositionY, 0.02f, $"{name}: not slid");
                            }
                            else
                            {
                                Assert.IsTrue(z < at.Z - 0.5f, $"control {name}: without the bool the ethereal pet passes through the slab (top {top:0.000}) and ends at z {z:0.000}; if it does not, this test cannot see the bug");
                            }
                        }
                        finally
                        {
                            Remove(pet);
                        }
                    }
                }
                finally
                {
                    foreach (var p in platforms)
                        Remove(p.Body);
                }
            }
        }

        /// <summary>
        /// The #1986 caveat, checked for this room: Static plates refused the Grael fill's face-down panels. Here every one of
        /// the 20 platforms is spawned (a superset of what the script has up at once, so no live layout can obstruct more), and
        /// the Matron's real fill sheet (MatronSettings.FillArgs, FillRules.SheetTiles: the in-cross tile centres, both faces,
        /// the moving-body prep HazardContext.Spawn applies) is walked from the face-down floor to max_z every 0.1 m: at each
        /// height every panel is SPAWNED there (a StartFill at that level, as a restart does) and the moving panel is SNAPPED
        /// there with HazardMotion.SnapFlags (every leg end of the rise). With Static platforms each outcome is identical to
        /// the same walk with non-Static platforms, and every panel that has a cell is placed at its exact point.
        /// POSITIVE CONTROL: a face-down panel centred over a platform, half way through the slab, is kept there through the
        /// non-Static slab and NOT through the Static one, so the walk can see an obstruction if a tile centre ever met a slab.
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void MatronFill_RisingThroughTheStaticPlatforms_EveryPanelSpawnsAndSnapsAsWithoutTheBool()
        {
            RequireDats();

            var lookup = MatronFillSpawnTests.Lookup(Landblock);
            var region = HazardRegionPresets.Matron(0);
            var settings = MatronSettings.Defaults;
            var fill = FillSettings.From(settings.FillArgs(settings.FillFloorZ, settings.FlowRate, 1.0), region);
            var tiles = FillRules.SheetTiles(region, fill.PanelSize, fill.SheetMargin);
            var edge = FillRules.SheetEdge(region, fill.PanelSize, fill.SheetMargin);

            var lowZ = (float)fill.FloorZ + HazardPanels.FaceClearance;   // the doubled sheet's face-down floor over the hall (MatronFillSpawnTests)
            var highZ = (float)fill.MaxZ;
            Assert.IsTrue(highZ > MatronLayout.PlatformTop(MatronLayout.PlatformCount - 1), $"the walk ({lowZ:0.00} .. {highZ:0.00}) passes every platform top");

            var levels = new List<float>();
            for (var z = lowZ; z <= highZ + 1e-4f; z += 0.1f)
                levels.Add(z);

            // the geometric reason, reported: the nearest any tile centre comes to a platform's footprint
            var nearest = float.MaxValue;
            foreach (var o in MatronData.Openings)
                foreach (var p in MatronLayout.Platforms(o))
                    foreach (var (c, _) in tiles)
                        nearest = Math.Min(nearest, Math.Max(Math.Abs(c.X - p.X), Math.Abs(c.Y - p.Y)) - MatronLayout.PlatformHalfSize);
            Console.WriteLine($"fill walk: {tiles.Count} tiles, edge {edge:0.00}, {levels.Count} levels {lowZ:0.00} .. {highZ:0.00}; nearest tile centre to a platform footprint (Chebyshev) {nearest:0.00} m");

            var outcomes = new Dictionary<bool, List<string>>();

            foreach (var forceStatic in new[] { false, true })
            {
                var platforms = SpawnAllPlatforms(forceStatic);
                var log = new List<string>();
                var exact = 0;

                try
                {
                    foreach (var (centre, _) in tiles)
                    {
                        foreach (var faceDown in new[] { false, true })
                        {
                            var rotation = faceDown ? HazardPanels.FaceDown : HazardPanels.FaceUp;
                            GenericObject mover = null;

                            try
                            {
                                foreach (var z in levels)
                                {
                                    var at = new Vector3(centre.X, centre.Y, z);
                                    var cell = lookup(at);

                                    if (!cell.HasValue || cell.Value == 0)
                                    {
                                        log.Add($"{centre} {faceDown} {z:0.00}: no cell");
                                        continue;
                                    }

                                    // 1. spawned at this height (a fill start or restart at this level)
                                    var spec = HazardPanels.Spec(fill.PanelWcid, at, rotation, edge, fill.Opacity, true, "fill");
                                    var body = MatronFillSpawnTests.PanelBody(cell.Value, spec);
                                    var spawned = MatronFillSpawnTests.TrySpawn(body);
                                    var spawnKept = spawned && Vector3.Distance(Rest(body), at) < 1e-3f;
                                    if (spawned)
                                        MatronFillSpawnTests.Remove(body);

                                    // 2. the moving panel snapped up to this height (a leg end of the rise)
                                    if (mover == null)
                                    {
                                        var moverSpec = HazardPanels.Spec(fill.PanelWcid, at, rotation, edge, fill.Opacity, true, "fill");
                                        mover = MatronFillSpawnTests.PanelBody(cell.Value, moverSpec);
                                        if (!MatronFillSpawnTests.TrySpawn(mover))
                                            mover = null;
                                    }

                                    var snapKept = false;
                                    var result = ACE.Server.Physics.Common.SetPositionError.GeneralFailure;
                                    if (mover != null)
                                    {
                                        result = Snap(mover, cell.Value, at, rotation);
                                        snapKept = result == ACE.Server.Physics.Common.SetPositionError.OK && Vector3.Distance(Rest(mover), at) < 1e-3f;
                                    }

                                    if (spawnKept && snapKept)
                                        exact++;

                                    log.Add($"{centre} {faceDown} {z:0.00}: spawn {(spawnKept ? "exact" : spawned ? "moved" : "refused")} snap {(snapKept ? "exact" : result.ToString())}");
                                }
                            }
                            finally
                            {
                                if (mover != null)
                                    MatronFillSpawnTests.Remove(mover);
                            }
                        }
                    }
                }
                finally
                {
                    foreach (var p in platforms)
                        Remove(p.Body);
                }

                Console.WriteLine($"ForcePhysicsStatic {forceStatic}: {exact} exact spawn+snap pairs of {log.Count} probes");
                outcomes[forceStatic] = log;
            }

            var diffs = outcomes[false].Zip(outcomes[true], (a, b) => (a, b)).Where(p => p.a != p.b).Select(p => $"{p.a}  =>  {p.b}").ToList();
            Assert.AreEqual(outcomes[false].Count, outcomes[true].Count, "the same walk both times");
            Assert.AreEqual(0, diffs.Count, "Static platforms change no fill panel outcome: " + string.Join(" | ", diffs.Take(10)));

            var withCell = outcomes[true].Where(l => !l.EndsWith("no cell")).ToList();
            Assert.IsTrue(withCell.Count > 1000, $"control: the walk really placed panels ({withCell.Count} probes with a cell)");
            Assert.IsTrue(withCell.All(l => l.EndsWith("spawn exact snap exact")), "every panel with a cell spawns and snaps exactly: " + string.Join(" | ", withCell.Where(l => !l.EndsWith("spawn exact snap exact")).Take(10)));

            // POSITIVE CONTROL: a panel centred on a slab, at the slab's mid-height, is obstructed only by the Static one
            var opening = MatronData.Openings[0];
            var pad = MatronLayout.Platforms(opening)[0];
            var inSlab = new Vector3(pad.X, pad.Y, pad.Z + MatronLayout.PlatformThickness / 2f);
            var slabCell = lookup(inSlab);
            Assert.IsTrue(slabCell.HasValue, "the in-slab probe lies in a hall cell");

            foreach (var forceStatic in new[] { false, true })
            {
                var slab = MakePlatform(pad, forceStatic);
                Assert.IsTrue(TrySpawn(slab), "the control slab spawns");

                try
                {
                    var spec = HazardPanels.Spec(fill.PanelWcid, inSlab, HazardPanels.FaceDown, edge, fill.Opacity, true, "fill");
                    var body = MatronFillSpawnTests.PanelBody(slabCell.Value, spec);
                    var spawned = MatronFillSpawnTests.TrySpawn(body);
                    var kept = spawned && Vector3.Distance(Rest(body), inSlab) < 1e-3f;
                    if (spawned)
                        MatronFillSpawnTests.Remove(body);

                    if (forceStatic)
                        Assert.IsFalse(kept, "control: a face-down panel inside a Static slab is refused or moved; if it is kept, the walk above cannot see an obstruction");
                    else
                        Assert.IsTrue(kept, "control: through a non-Static slab the ethereal panel is kept, so the difference is the bool");
                }
                finally
                {
                    Remove(slab);
                }
            }
        }

        // ================= builders =================

        private static Vector3 Rest(WorldObject wo) => wo.PhysicsObj.Position.Frame.Origin;

        private static List<(string Name, PinnaclePoint At, GenericObject Body)> SpawnAllPlatforms(bool forceStatic)
        {
            var list = new List<(string, PinnaclePoint, GenericObject)>();

            foreach (var o in MatronData.Openings)
            {
                var points = MatronLayout.Platforms(o);

                for (var k = 0; k < points.Count; k++)
                {
                    var slab = MakePlatform(points[k], forceStatic);
                    Assert.IsTrue(TrySpawn(slab), $"{o.Name} platform {k} must spawn (ForcePhysicsStatic {forceStatic}), or the test proves nothing");
                    Assert.AreEqual(points[k].Z, slab.Location.PositionZ, 1e-3f, $"{o.Name} platform {k}: z exact with ForcePhysicsStatic {forceStatic}");
                    list.Add(($"{o.Name} platform {k}", points[k], slab));
                }
            }

            return list;
        }

        private static GenericObject MakePlatform(PinnaclePoint at, bool forceStatic)
        {
            var weenie = new Weenie
            {
                WeenieClassId = 999970,
                ClassName = "matronplatformstatictestplatform",
                WeenieType = WeenieType.Generic,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Matron Platform Static Test Platform" } },
                PropertiesDID = new Dictionary<PropertyDataId, uint> { { PropertyDataId.Setup, PlatformSetup } },
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.PhysicsState, PlatformPhysicsState } },
                PropertiesBool = PlatformBools(forceStatic),
            };

            var wo = new GenericObject(weenie, new ObjectGuid(nextGuid++));
            wo.Location = new Position(at.Cell, at.X, at.Y, at.Z, at.QX, at.QY, at.QZ, at.QW, 0);
            return wo;
        }

        private static CombatPet MakeCombatPet(uint cell, Vector3 at)
        {
            var weenie = new Weenie
            {
                WeenieClassId = 999969,
                ClassName = "matronplatformstatictestpet",
                WeenieType = WeenieType.CombatPet,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Matron Platform Static Test Pet" } },
                PropertiesDID = new Dictionary<PropertyDataId, uint> { { PropertyDataId.Setup, PetSetup }, { PropertyDataId.MotionTable, PetMotionTable } },
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.PhysicsState, PetPhysicsState } },
                PropertiesBool = new Dictionary<PropertyBool, bool> { { PropertyBool.Stuck, true }, { PropertyBool.ReportCollisions, true }, { PropertyBool.GravityStatus, true } },
                PropertiesFloat = new Dictionary<PropertyFloat, double> { { PropertyFloat.DefaultScale, 1.0 } },
            };

            TestGameTables.EnsureInitialized();   // Creature's ctor reads the vital formula tables

            var wo = new CombatPet(weenie, new ObjectGuid(nextGuid++));   // Pet's ctor sets Ethereal, as live
            wo.Location = new Position(cell, at.X, at.Y, at.Z, 0f, 0f, 0f, 1f, 0);
            return wo;
        }

        /// <summary>A leg-end snap with HazardMotion.Snap's exact SetPosition flags, keeping the body's rotation as HazardMotion.Snap does.</summary>
        private static ACE.Server.Physics.Common.SetPositionError Snap(WorldObject wo, uint cell, Vector3 target, Quaternion rotation)
        {
            var result = ACE.Server.Physics.Common.SetPositionError.GeneralFailure;

            WithClientPhysics(() =>
            {
                var setPosition = new ACE.Server.Physics.Common.SetPosition(0);
                setPosition.Pos = new ACE.Server.Physics.Common.Position(new Position(cell, target.X, target.Y, target.Z, rotation.X, rotation.Y, rotation.Z, rotation.W, 0));
                setPosition.Flags = HazardMotion.SnapFlags;

                result = wo.PhysicsObj.SetPosition(setPosition);
                wo.SyncLocation();
            });

            return result;
        }

        private static void Settle(WorldObject wo)
        {
            WithClientPhysics(() =>
            {
                wo.PhysicsObj.set_active(true);

                for (var i = 0; i < 40; i++)
                    wo.PhysicsObj.UpdateObjectInternal(0.1, 0);

                wo.SyncLocation();
            });
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
