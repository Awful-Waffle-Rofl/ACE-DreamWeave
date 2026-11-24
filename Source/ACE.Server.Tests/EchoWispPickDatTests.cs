using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

using ACE.DatLoader;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.ClassAbilities;
using ACE.Server.Managers;
using ACE.Server.Physics;
using ACE.Server.WorldObjects;
using ACE.Server.WorldObjects.Entity;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Position = ACE.Entity.Position;
using Spell = ACE.Server.Entity.Spell;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Echo Cast wisp target pick on real geometry (stage bug 2026-11-23: wisps spawned and expired beside a World Event
    /// fight and never cast, while a wisp held on a boss did). Drives the REAL EchoWisp.PickTarget, with a real wisp body
    /// (the committed 1008602 shape), real creatures and the real IsDirectVisible sight test, on Holtburg terrain
    /// (landblock 0xA9B4, the World Event's own landblock in that log), through the CasterPetCastGateTests dat bootstrap.
    ///
    /// It shows the two halves side by side: a held starting target is kept with no engagement test (design rule 1),
    /// and once it is dead the wisp picks the next creature only when that creature is engaged with the caster - a
    /// monster attacking a player outside the caster's fellowship, and never damaged by the caster, is turned away as
    /// NotInCastersFight (design rule 3), and the pick tally says so. That is the line the per-cast DEBUG log prints.
    ///
    /// The stage case (owner ruling 2026-11-23) has its own test: the starting target dead and the hostiles left
    /// attacking a fellow, or damaged by the caster, which the pre-ruling rule (caster and own pets only) turned away.
    ///
    /// The owner is an uninitialized Player (no live Player is constructible here) holding the biota, skills and
    /// physics object PickTarget reads; its physics object is a placed stand-in creature's, whose ObjMaint is given
    /// the creatures as visible objects. RequiresDatFiles: Inconclusive without ACE_DAT_PATH, skipped in CI; the
    /// dat-free pins on the same rules live in EchoWispControllerTests.
    /// </summary>
    [TestClass]
    public class EchoWispPickDatTests
    {
        private const uint Landblock = 0xA9B40000;
        private static uint nextGuid = 0x7E6E0001;

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "<Biota>k__BackingField")]
        private static extern ref ACE.Entity.Models.Biota BiotaField(WorldObject wo);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "BiotaDatabaseLock")]
        private static extern ref ReaderWriterLockSlim BiotaLockField(WorldObject wo);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "<PhysicsObj>k__BackingField")]
        private static extern ref PhysicsObj PhysicsObjField(WorldObject wo);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "<Guid>k__BackingField")]
        private static extern ref ObjectGuid GuidField(WorldObject wo);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "Skills")]
        private static extern ref Dictionary<Skill, CreatureSkill> SkillsField(Creature creature);

        private static readonly MethodInfo pickTarget = typeof(EchoWisp).GetMethod("PickTarget", BindingFlags.NonPublic | BindingFlags.Instance);

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();

            // before anything here touches a Player-declared member (its static initializer reads the world database)
            ThornsCloseRangeTests.EnsurePlayerTypeInitializable();
        }

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void Pick_KeepsTheHeldTarget_ThenTakesOnlyAnEngagedHostile_AndTalliesWhy()
        {
            RequireDats();

            var lb = ACE.Server.Physics.Common.LScape.get_landblock(Landblock | 0xFFFF, 0);
            float Z(float x, float y) => lb.GetZ(new Vector3(x, y, 0f));

            Assert.IsNotNull(pickTarget, "EchoWisp.PickTarget(EchoWispPickTally)");

            var placed = new List<WorldObject>();

            Creature Mob(float x, float y, string name)
            {
                var m = Creature(x, y, Z(x, y), name);
                placed.Add(m);
                return m;
            }

            try
            {
                // the caster, a wisp 1.5 m to its side at its height (the spawn rule), and the other player's stand-in
                var stand = Mob(85f, 4f, "caster stand-in");
                var owner = Owner(stand);
                var otherPlayer = Mob(70f, 4f, "another player stand-in");

                var body = Wisp(86.5f, 4f, stand.PhysicsObj.Position.Frame.Origin.Z);
                placed.Add(body);

                var spell = new Spell(4439u, false);    // Incantation of Flame Bolt, one of the stage log's wisps
                Assert.AreEqual(MagicSchool.WarMagic, spell.School, "control: the spell came from the portal dat");

                var start = Mob(85f, 9f, "starting target");
                var engaged = Mob(87f, 10f, "engaged hostile");
                var elsewhere = Mob(84f, 8f, "hostile fighting someone else");

                foreach (var m in new[] { start, engaged, elsewhere })
                    stand.PhysicsObj.ObjMaint.AddVisibleObject(m.PhysicsObj);

                // the starting target is idle (it need not be engaged: design rule 1); the others are awake
                engaged.AttackTarget = owner;
                elsewhere.AttackTarget = otherPlayer;

                Assert.IsTrue(body.IsDirectVisible(engaged) && body.IsDirectVisible(elsewhere),
                    "POSITIVE CONTROL: the wisp body has line of sight to both hostiles, so only the rules below can turn one away");

                var wisp = new EchoWisp(owner, body, spell, start, 0, false, 1.0f, null, null, 0.0, 9.0, 2.0, 3.0, 0.5);

                // 1. the held starting target, alive: kept, with no engagement test
                var tally = new EchoWispPickTally();
                Assert.AreSame(start, Pick(wisp, tally), "the held starting target is kept while it lives");
                Assert.IsTrue(tally.HadHeld);
                Assert.AreEqual(EchoWispIneligible.None, tally.HeldIneligible);
                StringAssert.StartsWith(tally.Describe(), "held: kept; candidates 3:");

                // 2. it dies: the engaged hostile is the next target, the one fighting someone else is turned away
                start.Health.Current = 0;
                tally = new EchoWispPickTally();
                Assert.AreSame(engaged, Pick(wisp, tally), "with the starting target dead, the wisp moves to the hostile engaged with its caster");
                Assert.AreEqual(EchoWispIneligible.Dead, tally.HeldIneligible);
                Assert.AreEqual(1, tally.Ineligible(EchoWispIneligible.NotInCastersFight), "the hostile fighting another player is not the caster's fight");
                Assert.AreEqual(1, tally.InRange);
                Assert.AreEqual(1, tally.SightTested);
                Assert.AreEqual(0, tally.SightFailed);

                // 3. that one dies too: the only hostile left fights someone else, so the wisp idles and the tally says why
                engaged.Health.Current = 0;
                tally = new EchoWispPickTally();
                Assert.IsNull(Pick(wisp, tally), "no live hostile is engaged with the caster: the wisp idles (design rule 4)");
                Assert.AreEqual(EchoWispIneligible.Dead, tally.HeldIneligible);
                Assert.AreEqual(1, tally.Ineligible(EchoWispIneligible.NotInCastersFight));
                Assert.AreEqual(0, tally.SightTested, "no legal candidate reached a sight test");
                Assert.AreEqual("held: dropped (Dead); candidates 3: ineligible 2 (Dead 1, NotInCastersFight 1), out of range 0 (range 75.0 m), in range 0, sight failed 0 of 0 tested, 0 untested",
                    tally.Describe(), "the idle line the per-cast DEBUG log prints");

                // 4. /summonaggro on lifts the engagement rule (design rule 3): the nearest hostile is taken
                owner.SetProperty(PropertyBool.SummonAggro, true);
                tally = new EchoWispPickTally();
                Assert.AreSame(elsewhere, Pick(wisp, tally), "with /summonaggro on, any attackable creature in range and sight, nearest first");
                Assert.AreEqual(0, tally.Ineligible(EchoWispIneligible.NotInCastersFight));
            }
            finally
            {
                foreach (var wo in placed)
                    wo.PhysicsObj?.leave_world();
            }
        }

        /// <summary>
        /// The stage case (2026-11-23, owner ruling): the starting target is dead and the hostiles left are fighting the
        /// caster's FELLOW, or are idle but were damaged by the caster. The wisp moves on to them, nearest first; a nearer
        /// hostile fighting a player outside the fellowship, never damaged by the caster, stays off-limits. Under the
        /// pre-ruling rule (the caster and its own pets only) every pick here returned null, which is the bug.
        /// Asserts on the picked creature only, so the same body ran against the pre-fix branch head.
        /// </summary>
        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void Pick_StartTargetDead_MovesOnToHostilesFightingAFellowOrDamagedByTheCaster()
        {
            RequireDats();

            var lb = ACE.Server.Physics.Common.LScape.get_landblock(Landblock | 0xFFFF, 0);
            float Z(float x, float y) => lb.GetZ(new Vector3(x, y, 0f));

            var placed = new List<WorldObject>();

            Creature Mob(float x, float y, string name)
            {
                var m = Creature(x, y, Z(x, y), name);
                placed.Add(m);
                return m;
            }

            try
            {
                var stand = Mob(85f, 4f, "caster stand-in");
                var owner = Owner(stand);
                GuidField(owner) = new ObjectGuid(0x50000A01);

                var fellowship = (ACE.Server.Entity.Fellowship)RuntimeHelpers.GetUninitializedObject(typeof(ACE.Server.Entity.Fellowship));
                owner.Fellowship = fellowship;

                var fellow = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));
                GuidField(fellow) = new ObjectGuid(0x50000A02);
                fellow.Fellowship = fellowship;

                var stranger = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));
                GuidField(stranger) = new ObjectGuid(0x50000A03);

                var body = Wisp(86.5f, 4f, stand.PhysicsObj.Position.Frame.Origin.Z);
                placed.Add(body);

                var spell = new Spell(4439u, false);    // Incantation of Flame Bolt

                var start = Mob(85f, 9f, "starting target");
                var strangers = Mob(86.5f, 6.5f, "hostile fighting a stranger");     // nearest to the wisp
                var fellows = Mob(87f, 10f, "hostile fighting a fellow");
                var damaged = Mob(83f, 12f, "idle hostile the caster damaged");

                foreach (var m in new[] { start, strangers, fellows, damaged })
                    stand.PhysicsObj.ObjMaint.AddVisibleObject(m.PhysicsObj);

                strangers.AttackTarget = stranger;
                fellows.AttackTarget = fellow;
                damaged.DamageHistory.TotalDamage.Add(owner.Guid, new ACE.Server.Entity.DamageHistoryInfo(owner, 120f));

                foreach (var m in new[] { strangers, fellows, damaged })
                    Assert.IsTrue(body.IsDirectVisible(m), $"POSITIVE CONTROL: the wisp sees {m.Name}");

                var wisp = new EchoWisp(owner, body, spell, start, 0, false, 1.0f, null, null, 0.0, 9.0, 2.0, 3.0, 0.5);

                Assert.AreSame(start, Pick(wisp, null), "control: the live starting target is held");

                start.Health.Current = 0;
                Assert.AreSame(fellows, Pick(wisp, null),
                    "starting target dead: the wisp moves on to the hostile fighting the caster's fellow, skipping the nearer one fighting a stranger");

                fellows.Health.Current = 0;
                Assert.AreSame(damaged, Pick(wisp, null), "then to the idle hostile the caster has damaged");

                damaged.Health.Current = 0;
                Assert.IsNull(Pick(wisp, null), "a hostile fighting a player outside the fellowship, never damaged by the caster, stays off-limits");
            }
            finally
            {
                foreach (var wo in placed)
                    wo.PhysicsObj?.leave_world();
            }
        }

        private static Creature Pick(EchoWisp wisp, EchoWispPickTally tally)
        {
            var picked = (Creature)pickTarget.Invoke(wisp, new object[] { tally });

            // the controller's own bookkeeping (CastOnOwnerThread): the pick becomes the held target, or none
            typeof(EchoWisp).GetField("currentTarget", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(wisp, picked);

            return picked;
        }

        /// <summary>A bare Player with a specialized War Magic skill and the stand-in's physics object (its ObjMaint is the candidate source).</summary>
        private static Player Owner(Creature stand)
        {
            var owner = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));
            BiotaField(owner) = new ACE.Entity.Models.Biota
            {
                PropertiesBool = new Dictionary<PropertyBool, bool>(),
                PropertiesSkill = new Dictionary<Skill, PropertiesSkill>
                {
                    [Skill.WarMagic] = new PropertiesSkill { SAC = SkillAdvancementClass.Specialized, InitLevel = 10, LevelFromPP = 300 },
                },
            };
            BiotaLockField(owner) = new ReaderWriterLockSlim();
            SkillsField(owner) = new Dictionary<Skill, CreatureSkill>();
            PhysicsObjField(owner) = stand.PhysicsObj;
            return owner;
        }

        /// <summary>A wisp body of the committed 1008602 shape (Ethereal, IgnoreCollisions, no gravity, SpawnAtExactPosition).</summary>
        private static GenericObject Wisp(float x, float y, float z)
        {
            var weenie = new Weenie
            {
                WeenieClassId = 1008602,
                ClassName = "echowisppickbody",
                WeenieType = WeenieType.Generic,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Echo Wisp" } },
                PropertiesBool = new Dictionary<PropertyBool, bool>
                {
                    { PropertyBool.Stuck, true },
                    { PropertyBool.IgnoreCollisions, true },
                    { PropertyBool.Ethereal, true },
                    { PropertyBool.GravityStatus, false },
                    { PropertyBool.Attackable, false },
                    { PropertyBool.UiHidden, true },
                    { (PropertyBool)9069, true },   // SpawnAtExactPosition
                },
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.PhysicsState, 0x814 } },
                PropertiesDID = new Dictionary<PropertyDataId, uint> { { PropertyDataId.Setup, 0x020009F3u } },
            };

            var wo = new GenericObject(weenie, new ObjectGuid(nextGuid++));
            wo.Location = new Position(CellOf(x, y), x, y, z, 0f, 0f, 0f, 1f, 0);
            wo.InitPhysicsObj();
            Assert.IsTrue(wo.AddPhysicsObj(), "the wisp body entered the world");
            return wo;
        }

        /// <summary>A human-bodied, attackable, living creature (the World Event trash's Setup 0x02000001 and PhysicsState 1032).</summary>
        private static Creature Creature(float x, float y, float z, string name)
        {
            var weenie = new Weenie
            {
                WeenieClassId = 999876u,
                ClassName = "echowisppickmob",
                WeenieType = WeenieType.Creature,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, name } },
                PropertiesDID = new Dictionary<PropertyDataId, uint> { { PropertyDataId.Setup, 0x02000001u }, { PropertyDataId.MotionTable, 0x09000001u } },
                PropertiesBool = new Dictionary<PropertyBool, bool> { { PropertyBool.Attackable, true } },
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.PhysicsState, 1032 } },
                PropertiesAttribute2nd = new Dictionary<PropertyAttribute2nd, PropertiesAttribute2nd>
                {
                    { PropertyAttribute2nd.MaxHealth, new PropertiesAttribute2nd { InitLevel = 500, CurrentLevel = 500 } },
                },
            };

            TestGameTables.EnsureInitialized();

            var wo = new Creature(weenie, new ObjectGuid(nextGuid++));
            wo.Location = new Position(CellOf(x, y), x, y, z, 0f, 0f, 0f, 1f, 0);
            wo.InitPhysicsObj();
            Assert.IsTrue(wo.AddPhysicsObj(), $"{name} entered the world");
            Assert.IsTrue(wo.IsAlive, $"control: {name} is alive");
            return wo;
        }

        private static uint CellOf(float x, float y) => Landblock | (uint)((int)(x / 24f) * 8 + (int)(y / 24f) + 1);

        // ===================== dat handling (as CasterPetCastGateTests) =====================

        private static readonly object initLock = new object();
        private static bool initialized;
        private static string skipReason;
        private static CellDatDatabase ownCellDat;
        private static bool cellDatSwapped;
        private static CellDatDatabase previousCellDat;
        private static bool wasServer;

        private static readonly PropertyInfo cellDatProperty = typeof(DatManager).GetProperty(nameof(DatManager.CellDat));

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
