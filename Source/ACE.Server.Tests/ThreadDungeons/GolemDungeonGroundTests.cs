using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;

using ACE.DatLoader;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Physics;
using ACE.Server.PuzzleGates;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The dungeon golem's ground (prod 2026-11-21: 21 of 21 indoor golems refused "no walkable ground under the golem").
    /// Root cause: the run's trash stands on most curated points when the encounter pass runs, and the ground probe's
    /// body settled onto that monster instead of the floor. The probe now passes through creatures. Dat-free pins.
    /// </summary>
    [TestClass]
    public class GolemDungeonGroundTests
    {
        [TestMethod]
        public void The_ground_probe_walks_through_creatures_and_nothing_else()
        {
            Assert.IsTrue(GolemEncounterType.GroundIgnores((Creature)RuntimeHelpers.GetUninitializedObject(typeof(Creature))), "a monster standing on the point");
            Assert.IsFalse(GolemEncounterType.GroundIgnores((Door)RuntimeHelpers.GetUninitializedObject(typeof(Door))), "a door still blocks");
            Assert.IsFalse(GolemEncounterType.GroundIgnores((GenericObject)RuntimeHelpers.GetUninitializedObject(typeof(GenericObject))), "scenery still blocks");
            Assert.IsFalse(GolemEncounterType.GroundIgnores(null), "a physics object with no world object (static geometry) blocks");
            Assert.IsFalse(GolemEncounterType.GroundIgnoresPhysics(null));
        }

        [TestMethod]
        public void Every_dungeon_probe_call_passes_the_creature_ignore()
        {
            // Mutation-proof form: each of the three Transitions the dungeon ground runs names the ignore predicate, and the
            // production ground's dungeon branch goes through DungeonGround (dat test GolemDungeonGroundDatTests runs it).
            var src = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/GolemEncounterType.cs");
            var ground = PooledLootSourceText.MethodBody(src, "internal static (GolemGroundHit? Golem, Func<float, float, GolemGroundHit?> At, Action Release) DungeonGround(uint instanceId, uint cell, Vector3 point)");

            StringAssert.Contains(ground, "Func<ACE.Server.Physics.PhysicsObj, bool> ignore = GroundIgnoresPhysics;");
            StringAssert.Contains(ground, "var settled = probe.Settle(cell, point, ignore);");
            StringAssert.Contains(ground, "var walked = probe.Step(start, offset, ignore, out _);");
            StringAssert.Contains(ground, "var floor = probe.Settle(end.Cell, end.Pos, ignore);");
            Assert.IsFalse(ground.Contains("Step(start, offset, null"), "no Transition without the ignore");
            Assert.IsFalse(System.Text.RegularExpressions.Regex.IsMatch(ground, @"\.Settle\([^,()]+,\s*[^,()]+\)"), "no two-argument Settle (no ignore)");

            var production = PooledLootSourceText.MethodBody(src, "private static (GolemGroundHit? Golem, Func<float, float, GolemGroundHit?> At, Action Release) ProductionGround(ThreadEncounterPlaceRequest request, GolemConfig config)");
            StringAssert.Contains(production, "return DungeonGround(instanceId, p.Cell, new Vector3(p.X, p.Y, p.Z));");
        }
    }

    /// <summary>
    /// The same against REAL dungeon geometry: tumerok_outpost's point 0x01BF018D (20.49, -30.4, 0), where prod refused a
    /// golem twice on 2026-11-21. With a creature standing on the point the old probe (no ignore) finds no floor, and
    /// DungeonGround finds the flat floor and a full ring. Local only (needs client_cell_1.dat; set ACE_DAT_PATH).
    /// </summary>
    [TestClass]
    public class GolemDungeonGroundDatTests
    {
        private const uint Cell = 0x01BF018D;
        private static readonly Vector3 At = new Vector3(20.49f, -30.4f, 0f);
        private const uint LandblockKey = 0x01BFFFFF;

        private static readonly object initLock = new object();
        private static bool initialized;
        private static string skipReason;
        private static CellDatDatabase ownCellDat;
        private static readonly System.Reflection.PropertyInfo cellDatProperty = typeof(DatManager).GetProperty(nameof(DatManager.CellDat));

        private CellDatDatabase previousCellDat;
        private bool swapped;
        private bool wasServer;
        private Creature monster;

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void A_monster_on_the_point_no_longer_hides_the_floor_from_the_golem()
        {
            RequireDats();

            // Control: the empty point has floor for the old probe too.
            using (var probe = new PuzzleWalkProbe(0))
            {
                var empty = probe.Settle(Cell, At);
                Assert.IsTrue(empty != null && empty.Value.OnWalkable, "control: the bare floor settles");
            }

            monster = SpawnMonster();

            // The prod failure, reproduced: with the run's trash on the point the un-ignoring settle finds nothing.
            using (var probe = new PuzzleWalkProbe(0))
                Assert.IsNull(probe.Settle(Cell, At), "a creature on the point stops the body above the floor");

            var ground = GolemEncounterType.DungeonGround(0, Cell, At);

            try
            {
                Assert.IsNotNull(ground.Golem, "the golem has ground under the creature");
                Assert.AreEqual(At.Z, ground.Golem.Value.Z, 0.05f);

                var slots = GolemEncounterType.BuildRing(At.X, At.Y, ground.Golem.Value.Z, 3.0f, 0.5f, ground.At, out var refusal);
                Assert.IsNotNull(slots, refusal);
                Assert.IsTrue(slots.Count >= GolemEncounterType.WispsPerRound, $"a full ring on this floor: {slots.Count}");
            }
            finally
            {
                ground.Release?.Invoke();
            }
        }

        private static Creature SpawnMonster()
        {
            TestGameTables.EnsureInitialized();

            var weenie = new Weenie
            {
                WeenieClassId = 0x7E200101,
                ClassName = "golemgroundtestmonster",
                WeenieType = WeenieType.Creature,
                PropertiesBool = new Dictionary<PropertyBool, bool> { { PropertyBool.Attackable, true } },
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.PhysicsState, (int)(PhysicsState.ReportCollisions | PhysicsState.Gravity | PhysicsState.EdgeSlide) } },
                PropertiesDID = new Dictionary<PropertyDataId, uint> { { PropertyDataId.Setup, 0x02000001 } },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Golem Ground Test Monster" } },
            };

            var creature = new Creature(weenie, new ObjectGuid(0x7F0E3001));
            creature.Location = new Position(Cell, At.X, At.Y, At.Z, 0, 0, 0, 1, 0);
            creature.InitPhysicsObj();
            Assert.IsTrue(creature.AddPhysicsObj(), "the test monster did not spawn");
            return creature;
        }

        private void RequireDats()
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
            wasServer = PhysicsEngine.Instance.Server;
            PhysicsEngine.Instance.Server = false;
            swapped = true;
        }

        [TestCleanup]
        public void Restore()
        {
            if (!swapped)
                return;

            try
            {
                monster?.PhysicsObj?.leave_world();
                monster = null;
                ACE.Server.Physics.Common.LScape.unload_landblock(LandblockKey, 0);
            }
            finally
            {
                PhysicsEngine.Instance.Server = wasServer;
                cellDatProperty.SetValue(null, previousCellDat);
                previousCellDat = null;
                swapped = false;
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
