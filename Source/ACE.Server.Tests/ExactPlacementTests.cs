using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.DatLoader;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Physics;
using ACE.Server.WorldObjects;

using Position = ACE.Entity.Position;

namespace ACE.Server.Tests
{
    /// <summary>
    /// PropertyBool.SpawnAtExactPosition (9069): a Stuck object that opts in spawns at exactly its authored
    /// position instead of being pushed out of baked geometry by the spawn placement insert.
    ///
    /// The dat-backed tests drive the REAL server spawn path - WorldObject.InitPhysicsObj then
    /// WorldObject.AddPhysicsObj (AdjustDungeon, LScape.get_landcell, PhysicsObj.enter_world,
    /// SetPositionInternal, SyncLocation) - on the Marketplace Salvage Forge's own setup at its own authored
    /// row, which sits 0.86 m off the centre of a baked table static (0x020002FB at 15.89,-72.45 in cell
    /// 0x01F501B3, `cells --statics`). The unopted control proves the slide is really reachable here; the
    /// opted-in case fails on the pre-9069 code, which has no branch that skips the placement insert.
    ///
    /// PhysicsEngine.Server is flipped to false for the dat-backed tests so LScape builds the landblock from
    /// client_cell.dat instead of routing through LandblockManager and a world database (the same bootstrap
    /// the offline `cells --spawncheck` uses), and restored afterwards.
    /// </summary>
    [TestClass]
    public class ExactPlacementTests
    {
        private const uint ForgeSetup = 0x0200033C;       // weenies/1002652 Salvage Forge, PropertyDataId 1
        private const int ForgePhysicsState = 6292508;    // Ethereal, ReportCollisions, IgnoreCollisions, Gravity, ...
        private const uint ForgeCell = 0x01F501B3;
        private const float ForgeX = 16.75f, ForgeY = -72.45f, ForgeZ = -17.995f;
        private const float ForgeQw = 0.92388f, ForgeQz = -0.38268f;

        private static readonly object initLock = new object();
        private static bool initialized;
        private static string skipReason;

        private static uint nextGuid = 0x7F0E0001;

        // ================= pure: the opt-in gate =================

        [TestMethod]
        public void ExactPlacementStatic_RequiresBothOptInAndStuck()
        {
            Assert.IsFalse(MakeForge(optIn: false, stuck: false).ExactPlacementStatic, "neither");
            Assert.IsFalse(MakeForge(optIn: false, stuck: true).ExactPlacementStatic, "Stuck alone (every retail static) must not opt in");
            Assert.IsFalse(MakeForge(optIn: true, stuck: false).ExactPlacementStatic, "the opt-in is ignored on a non-Stuck object");
            Assert.IsTrue(MakeForge(optIn: true, stuck: true).ExactPlacementStatic, "opt-in plus Stuck");
        }

        // ================= pure: the creature rule =================

        /// <summary>An ordinary Stuck creature (a monster or NPC) must not honour the opt-in.</summary>
        [TestMethod]
        public void Creature_StuckWithOptIn_IsRefused()
        {
            var c = MakeCreature(wcid: 999967, optIn: true, stuck: true, aiImmobile: false, looksLikeObject: false);
            Assert.IsFalse(c.ExactPlacementStatic);
            StringAssert.Contains(c.ExactPlacementRefusal, "Creature");

            // each half of the pair alone is not enough
            Assert.IsFalse(MakeCreature(999967, true, true, aiImmobile: true, looksLikeObject: false).ExactPlacementStatic, "AiImmobile alone");
            Assert.IsFalse(MakeCreature(999967, true, true, aiImmobile: false, looksLikeObject: true).ExactPlacementStatic, "NpcLooksLikeObject alone");
            Assert.IsFalse(MakeCreature(999967, true, stuck: false, aiImmobile: true, looksLikeObject: true).ExactPlacementStatic, "not Stuck");
        }

        /// <summary>A creature-typed station prop (Stuck + AiImmobile + NpcLooksLikeObject) honours it.</summary>
        [TestMethod]
        public void Creature_StationPropWithOptIn_IsHonoured()
        {
            var c = MakeCreature(wcid: 999966, optIn: true, stuck: true, aiImmobile: true, looksLikeObject: true);
            Assert.IsNull(c.ExactPlacementRefusal);
            Assert.IsTrue(c.ExactPlacementStatic);
        }

        /// <summary>
        /// The two real weenies, built from their committed SQL (weenie type and every bool row), honour the
        /// opt-in. Fails if either stops being Creature-with-both-flags-and-Stuck, or drops the 9069 row.
        /// </summary>
        [TestMethod]
        [DataRow(1002652u, "1002652 Salvage Forge.sql")]
        [DataRow(1002751u, "1002751 Arcane Alignment Table.sql")]
        public void RealStationWeenies_HonourTheOptIn(uint wcid, string file)
        {
            var wo = LoadWeenieShape(wcid, file);
            Assert.IsInstanceOfType(wo, typeof(Creature), "the stations are Creature weenies (the give intercept needs that)");
            Assert.IsNull(wo.ExactPlacementRefusal, wo.ExactPlacementRefusal);
            Assert.IsTrue(wo.ExactPlacementStatic);
        }

        private static Creature MakeCreature(uint wcid, bool optIn, bool stuck, bool aiImmobile, bool looksLikeObject)
        {
            var bools = new Dictionary<PropertyBool, bool>();
            if (optIn) bools[PropertyBool.SpawnAtExactPosition] = true;
            if (stuck) bools[PropertyBool.Stuck] = true;
            if (aiImmobile) bools[PropertyBool.AiImmobile] = true;
            if (looksLikeObject) bools[PropertyBool.NpcLooksLikeObject] = true;

            var weenie = new Weenie
            {
                WeenieClassId = wcid,
                ClassName = "exactplacementtestcreature",
                WeenieType = WeenieType.Creature,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Exact Placement Test Creature" } },
                PropertiesBool = bools,
            };
            TestGameTables.EnsureInitialized();   // Creature's ctor reads the vital formula tables
            return new Creature(weenie, new ObjectGuid(nextGuid++));
        }

        /// <summary>
        /// Weenie type and bool rows only, read from Content/sql/weenies/&lt;file&gt; (found by walking up from the
        /// test output directory). Bool rows are the only rows in a weenie unit whose value is True/False.
        /// </summary>
        private static WorldObject LoadWeenieShape(uint wcid, string file)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            string path = null;
            while (dir != null && path == null)
            {
                var candidate = Path.Combine(dir.FullName, "Content", "sql", "weenies", file);
                if (File.Exists(candidate)) path = candidate;
                dir = dir.Parent;
            }
            Assert.IsNotNull(path, $"Could not find Content/sql/weenies/{file} by walking up from {AppContext.BaseDirectory}");

            var sql = File.ReadAllText(path);

            var typeMatch = System.Text.RegularExpressions.Regex.Match(sql, $@"VALUES \({wcid}, '[^']*', (\d+),");
            Assert.IsTrue(typeMatch.Success, "weenie row not found");
            var weenieType = (WeenieType)int.Parse(typeMatch.Groups[1].Value);

            var bools = new Dictionary<PropertyBool, bool>();
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(sql, $@"\({wcid},\s*(\d+),\s*(True|False)\s*\)"))
                bools[(PropertyBool)ushort.Parse(m.Groups[1].Value)] = m.Groups[2].Value == "True";

            var weenie = new Weenie
            {
                WeenieClassId = wcid,
                ClassName = "exactplacementshape" + wcid,
                WeenieType = weenieType,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, file } },
                PropertiesBool = bools,
            };

            TestGameTables.EnsureInitialized();
            return weenieType == WeenieType.Creature
                ? new Creature(weenie, new ObjectGuid(nextGuid++))
                : new GenericObject(weenie, new ObjectGuid(nextGuid++));
        }
        [TestMethod]
        public void IgnoredOptIn_WarnsOncePerWcid()
        {
            const uint wcid = 999968;
            Assert.IsTrue(WorldObject.WarnExactPlacementIgnoredOnce(wcid, "first", "it is not Stuck"), "first sighting logs");
            Assert.IsFalse(WorldObject.WarnExactPlacementIgnoredOnce(wcid, "second", "it is not Stuck"), "a repeat for the same wcid stays quiet");
        }

        [TestMethod]
        public void SpawnAtExactPosition_IsForkId9069()
        {
            Assert.AreEqual(9069, (int)PropertyBool.SpawnAtExactPosition);
        }

        // ================= dat-backed: the real spawn path =================

        /// <summary>Positive control: without the opt-in the forge's authored row is pushed well off its spot.</summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void Spawn_WithoutOptIn_IsPushedOffAuthoredSpot()
        {
            RequireDats();

            var moved = SpawnAndMeasure(MakeForge(optIn: false, stuck: true), out _);

            Assert.IsTrue(moved > 1.0f, $"control: the unopted forge should slide more than 1 m off its authored spot (the baked table pushes it); it moved {moved:0.###} m. If this fails the harness no longer reaches the placement slide and the opted-in test below proves nothing.");
        }

        /// <summary>The opt-in (on a Stuck object) spawns at exactly the authored cell and frame.</summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void Spawn_WithOptInAndStuck_StaysExactlyAtAuthoredPosition()
        {
            RequireDats();

            var wo = MakeForge(optIn: true, stuck: true);
            var moved = SpawnAndMeasure(wo, out var final, keep: true);

            try
            {
                Assert.AreEqual(0f, moved, 1e-4f, "horizontal displacement");
                Assert.AreEqual(ForgeZ, final.PositionZ, 1e-4f, "no step-down either");
                Assert.AreEqual(ForgeCell, final.Cell, "authored cell");
                Assert.AreEqual(ForgeQw, final.RotationW, 1e-4f);
                Assert.AreEqual(ForgeQz, final.RotationZ, 1e-4f);

                // Resting on a walkable plane, as a normal floor-level spawn ends (see ForceIntoCellExact).
                var ts = wo.PhysicsObj.TransientState;
                Assert.IsTrue(ts.HasFlag(TransientStateFlags.Contact) && ts.HasFlag(TransientStateFlags.OnWalkable), $"TransientState {ts}");

                // Sanity: physics ticks leave it where it was placed.
                var before = wo.PhysicsObj.Position.Frame.Origin;
                for (var i = 0; i < 20; i++)
                    wo.PhysicsObj.UpdateObjectInternal(0.1, 0);
                var after = wo.PhysicsObj.Position.Frame.Origin;
                Assert.AreEqual(0f, (after - before).Length(), 1e-4f, $"physics ticks moved the exact-placed object from {before} to {after}");
            }
            finally
            {
                Remove(wo);
            }
        }

        /// <summary>The Stuck gate is real: the same opt-in on a non-Stuck object takes the normal (sliding) path.</summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void Spawn_WithOptInButNotStuck_IsStillPushed()
        {
            RequireDats();

            var moved = SpawnAndMeasure(MakeForge(optIn: true, stuck: false), out _);

            Assert.IsTrue(moved > 1.0f, $"a non-Stuck object must ignore the opt-in; it moved {moved:0.###} m");
        }

        // ================= helpers =================

        private static GenericObject MakeForge(bool optIn, bool stuck)
        {
            var weenie = new Weenie
            {
                WeenieClassId = 999969,
                ClassName = "exactplacementtestforge",
                WeenieType = WeenieType.Generic,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Exact Placement Test Forge" } },
                PropertiesDID = new Dictionary<PropertyDataId, uint> { { PropertyDataId.Setup, ForgeSetup } },
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.PhysicsState, ForgePhysicsState } },
                PropertiesBool = new Dictionary<PropertyBool, bool>(),
            };

            if (stuck)
                weenie.PropertiesBool[PropertyBool.Stuck] = true;
            if (optIn)
                weenie.PropertiesBool[PropertyBool.SpawnAtExactPosition] = true;

            var wo = new GenericObject(weenie, new ObjectGuid(nextGuid++));
            wo.Location = new Position(ForgeCell, ForgeX, ForgeY, ForgeZ, 0f, 0f, ForgeQz, ForgeQw, 0);
            return wo;
        }

        /// <summary>
        /// Runs the landblock spawn path and returns the horizontal distance between the authored and the final
        /// position. The object is removed from the world afterwards unless <paramref name="keep"/> is set, so a
        /// later test does not collide with a leftover forge at the same spot.
        /// </summary>
        private static float SpawnAndMeasure(WorldObject wo, out Position final, bool keep = false)
        {
            var authored = new Position(wo.Location);
            var engine = PhysicsEngine.Instance;
            var wasServer = engine.Server;
            engine.Server = false;

            try
            {
                wo.InitPhysicsObj();
                Assert.IsTrue(wo.AddPhysicsObj(), "AddPhysicsObj failed - the authored row did not spawn at all");

                final = new Position(wo.Location);
                var dx = final.PositionX - authored.PositionX;
                var dy = final.PositionY - authored.PositionY;
                var moved = MathF.Sqrt(dx * dx + dy * dy);

                if (!keep)
                    Remove(wo);

                return moved;
            }
            finally
            {
                engine.Server = wasServer;
            }
        }

        private static void Remove(WorldObject wo)
        {
            var engine = PhysicsEngine.Instance;
            var wasServer = engine.Server;
            engine.Server = false;
            try
            {
                wo.PhysicsObj?.leave_world();
            }
            finally
            {
                engine.Server = wasServer;
            }
        }

        // ================= dat handling =================
        //
        // This class needs client_cell_1.dat (the landblock's cells and baked statics), but other classes in
        // this project DEPEND on DatManager.CellDat being null - MuleSummonTests asserts that Landblock
        // construction throws, precisely because no test here loads the cell dat. So the cell dat is opened
        // ONCE into a private instance and swapped into DatManager.CellDat only for the duration of each test
        // (SwapInCellDat), then swapped back out in TestCleanup, together with the physics landblock it built.

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

            SwapInCellDat();
        }

        private static void SwapInCellDat()
        {
            previousCellDat = DatManager.CellDat;
            cellDatProperty.SetValue(null, ownCellDat);
            cellDatSwapped = true;
        }

        [TestCleanup]
        public void RestoreGlobalState()
        {
            if (!cellDatSwapped)
                return;

            var engine = PhysicsEngine.Instance;
            var wasServer = engine.Server;
            engine.Server = false;
            try
            {
                // drop the physics landblock (and its AdjustCell) this test built from the cell dat
                ACE.Server.Physics.Common.LScape.unload_landblock(ForgeCell | 0xFFFF, 0);
            }
            finally
            {
                engine.Server = wasServer;
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

                // portal dat through DatManager exactly as the other dat-backed classes do (loadCell: false)
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
        }    }
}
