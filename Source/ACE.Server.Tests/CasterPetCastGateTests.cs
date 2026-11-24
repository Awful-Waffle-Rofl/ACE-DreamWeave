using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;

using ACE.DatLoader;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Physics;
using ACE.Server.Physics.Animation;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Position = ACE.Entity.Position;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The caster pet's cast gate (CombatPet.CasterCastReady) must step the pet's physics before it reads
    /// IsAnimating, as AttackReady and MoveReady do. Nothing else steps an animating awake monster's physics (the
    /// landblock skips it, WorldObject.ShouldRunCreaturePhysics), and cutting a running follow MoveTo leaves stop
    /// motions pending. A gate that only read IsAnimating stalled forever after the pet's first cast: player
    /// reports "only ever casts the vuln" and "they just stop and stand there doing nothing" (2026-11-06).
    ///
    /// The dat-backed test drives the real PrepareExplicitCast on a pet whose physics MoveTo is running in real
    /// Holtburg terrain (client_cell_1.dat, the WorldEventGroundProbeDatTests bootstrap), with no other physics
    /// step between ticks. It is Inconclusive without the dats and skips in CI, so the source pin below holds the
    /// fix there.
    /// </summary>
    [TestClass]
    public class CasterPetCastGateTests
    {
        private const uint Landblock = 0xA9B40000;
        private static uint nextGuid = 0x7E5E0001;

        /// <summary>One monster AI tick.</summary>
        private const double TickSeconds = 0.2;

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void EndingAFollowBeforeACast_DoesNotStallTheNextCast()
        {
            RequireDats();

            var lb = ACE.Server.Physics.Common.LScape.get_landblock(Landblock | 0xFFFF, 0);
            float Z(float x, float y) => lb.GetZ(new Vector3(x, y, 0f));

            var flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            var prepare = typeof(CombatPet).GetMethod("PrepareExplicitCast", flags);
            var following = typeof(CombatPet).GetField("isFollowingOwner", flags);
            Assert.IsNotNull(prepare, "CombatPet.PrepareExplicitCast");
            Assert.IsNotNull(following, "CombatPet.isFollowingOwner");

            var savedTicks = Timers.PortalYearTicks;

            var runTarget = Body(false, 33f, 90f, Z(33f, 90f), "run target");
            var enemy = Body(false, 40f, 100f, Z(40f, 100f), "enemy");
            var pet = (CombatPet)Body(true, 18f, 90f, Z(18f, 90f), "pet");

            try
            {
                // CanPayCastMana answers true without a spell, and both cast cooldowns are long over, so the only
                // thing PrepareExplicitCast can still refuse on is IsAnimating
                pet.SetProperty(PropertyBool.AiUsesMana, false);
                pet.PrevAttackTime = -1000.0;
                pet.NextAttackTime = -1000.0;

                // a follow in flight: the follow's own MoveTo parameters, stepped until the pet is running
                var mvp = new MovementParameters { DistanceToObject = PetFollowRules.FollowArriveDistance, WalkRunThreshold = 0.0f, Speed = 1.0f };
                pet.PhysicsObj.UpdateTime = ACE.Server.Physics.Common.PhysicsTimer.CurrentTime;
                pet.PhysicsObj.MoveToObject(runTarget.PhysicsObj, mvp);
                pet.IsMoving = true;
                following.SetValue(pet, true);

                for (var i = 0; i < 50; i++)
                {
                    Timers.PortalYearTicks += 0.05;
                    pet.PhysicsObj.update_object(0);
                }

                var start = pet.PhysicsObj.Position.Frame.Origin;
                Assert.IsTrue(Vector3.Distance(start, new Vector3(18f, 90f, start.Z)) > 1f, "control: the pet is running when the cast is decided");

                // the first cast: the gate opens, the follow is cut
                Assert.IsTrue((bool)prepare.Invoke(pet, new object[] { null, enemy }), "the first cast goes out");
                Assert.IsFalse((bool)following.GetValue(pet), "and the follow is ended");
                Assert.IsTrue(pet.PhysicsObj.IsAnimating,
                    "POSITIVE CONTROL: cutting the running MoveTo leaves stop motions pending. If this stops holding, the harness no longer reaches the stall and the assertion below proves nothing");

                // the next caster ticks, with no other physics step in between (the landblock skips an animating
                // awake monster). The stop motions drain in about 0.35 s of stepped physics.
                var readyAt = -1;
                for (var tick = 1; tick <= 25 && readyAt < 0; tick++)
                {
                    Timers.PortalYearTicks += TickSeconds;

                    if ((bool)prepare.Invoke(pet, new object[] { null, enemy }))
                        readyAt = tick;
                }

                Assert.IsTrue(readyAt > 0 && readyAt <= 3,
                    $"the cast gate must open again within 3 ticks of ending a follow (opened at tick {readyAt}, -1 = never in 5 s). It reads IsAnimating ({pet.PhysicsObj.IsAnimating}) and must step physics first, or nothing ever drains the pending stop motions");
            }
            finally
            {
                pet.PhysicsObj?.leave_world();
                runTarget.PhysicsObj?.leave_world();
                enemy.PhysicsObj?.leave_world();
                Timers.PortalYearTicks = savedTicks;
            }
        }

        /// <summary>
        /// The dat-free half: CasterCastReady steps physics (update_object) before it reads IsAnimating. Comments are
        /// stripped first, so the doc comment that names both cannot satisfy the pin.
        /// </summary>
        [TestMethod]
        public void Pin_CasterCastReady_StepsPhysicsBeforeReadingIsAnimating()
        {
            var source = File.ReadAllText(Path.Combine(ClassAbilityAffinityDeclarationTests.RepoRoot(), "Source", "ACE.Server", "WorldObjects", "CombatPet_Caster.cs"));

            const string signature = "private bool CasterCastReady()";
            var start = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, $"CombatPet_Caster.cs no longer contains: {signature}");
            Assert.AreEqual(-1, source.IndexOf(signature, start + signature.Length, StringComparison.Ordinal), "more than one CasterCastReady");

            var open = source.IndexOf('{', start);
            var depth = 0;
            var end = open;
            for (; end < source.Length; end++)
            {
                if (source[end] == '{') depth++;
                else if (source[end] == '}' && --depth == 0) break;
            }

            var body = string.Join("\n", source.Substring(open, end - open + 1)
                .Split('\n')
                .Select(l => { var c = l.IndexOf("//", StringComparison.Ordinal); return c >= 0 ? l.Substring(0, c) : l; }));

            var step = body.IndexOf("PhysicsObj.update_object(", StringComparison.Ordinal);
            var read = body.IndexOf("IsAnimating", StringComparison.Ordinal);

            Assert.IsTrue(step >= 0, "CasterCastReady must step the pet's physics (PhysicsObj.update_object) - nothing else steps an animating awake monster, so a gate that only reads IsAnimating stalls forever");
            Assert.IsTrue(read > step, "and it must step BEFORE it reads IsAnimating");

            var gate = body.IndexOf("PrevAttackTime + CasterMagicDelay", StringComparison.Ordinal);
            Assert.IsTrue(gate >= 0 && gate < step, "timers must be checked before physics is stepped, as in AttackReady - a pet still on cooldown pays no physics step");
        }

        private static Creature Body(bool pet, float x, float y, float z, string name)
        {
            var weenie = new Weenie
            {
                WeenieClassId = pet ? 999873u : 999874u,
                ClassName = "castergatetestbody",
                WeenieType = pet ? WeenieType.CombatPet : WeenieType.Creature,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, name } },
                PropertiesDID = new Dictionary<PropertyDataId, uint>
                {
                    // the Ossuary casters' body and motion table (1007703-1007705)
                    { PropertyDataId.Setup, pet ? 0x02001C26u : 0x02000001u },
                    { PropertyDataId.MotionTable, 0x09000001u },
                },
                PropertiesBool = new Dictionary<PropertyBool, bool>
                {
                    { PropertyBool.Ethereal, pet },
                    { PropertyBool.Attackable, true },
                    { PropertyBool.SummonCasterBrain, pet },
                },
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.PhysicsState, 1036 } },
            };

            TestGameTables.EnsureInitialized();

            Creature wo = pet ? new CombatPet(weenie, new ObjectGuid(nextGuid++)) : new Creature(weenie, new ObjectGuid(nextGuid++));
            var cell = Landblock | (uint)((int)(x / 24f) * 8 + (int)(y / 24f) + 1);
            wo.Location = new Position(cell, x, y, z, 0f, 0f, 0f, 1f, 0);
            wo.InitPhysicsObj();
            Assert.IsTrue(wo.AddPhysicsObj(), $"{name} entered the world");
            return wo;
        }

        // ===================== dat handling (as WorldEventGroundProbeDatTests) =====================

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
