using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.DatLoader;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Physics.Animation;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Combat pet movement-speed floor (PetSpeedFloor, CombatPet_SpeedFloor.cs). The arithmetic is pure and needs
    /// no dats; one test reads the reference motion table from client_portal.dat and is Inconclusive without it.
    /// No PropertyManager key is read: the tunable's default is checked against DefaultBooleanProperties directly.
    /// </summary>
    [TestClass]
    public class PetSpeedFloorTests
    {
        [TestMethod]
        public void GetFactor_PetSlowerThanTarget_RaisesToTarget()
        {
            Assert.AreEqual(2.0f, PetSpeedFloor.GetFactor(true, 10.0f, 5.0f), 1e-6f);
            Assert.AreEqual(1.25f, PetSpeedFloor.GetFactor(true, 5.0f, 4.0f), 1e-6f);

            // the product is exactly the target speed
            var pet = 3.7f;
            Assert.AreEqual(11.3f, pet * PetSpeedFloor.GetFactor(true, 11.3f, pet), 1e-4f);
        }

        [TestMethod]
        public void GetFactor_PetAlreadyAsFastOrFaster_IsUnchanged()
        {
            Assert.AreEqual(1.0f, PetSpeedFloor.GetFactor(true, 10.0f, 10.0f), 0.0f, "equal speed: no change");
            Assert.AreEqual(1.0f, PetSpeedFloor.GetFactor(true, 10.0f, 12.0f), 0.0f, "a floor never slows a faster pet");
            Assert.AreEqual(1.0f, PetSpeedFloor.GetFactor(true, 10.0f, 1000.0f), 0.0f);
        }

        [TestMethod]
        public void GetFactor_DisabledOrUnknownSpeeds_IsNeutral()
        {
            Assert.AreEqual(1.0f, PetSpeedFloor.GetFactor(false, 10.0f, 2.0f), 0.0f, "tunable off");
            Assert.AreEqual(1.0f, PetSpeedFloor.GetFactor(true, 0.0f, 2.0f), 0.0f, "target 0 = dat missing");
            Assert.AreEqual(1.0f, PetSpeedFloor.GetFactor(true, -1.0f, 2.0f), 0.0f);
            Assert.AreEqual(1.0f, PetSpeedFloor.GetFactor(true, float.NaN, 2.0f), 0.0f);
            Assert.AreEqual(1.0f, PetSpeedFloor.GetFactor(true, float.PositiveInfinity, 2.0f), 0.0f);
            Assert.AreEqual(1.0f, PetSpeedFloor.GetFactor(true, 10.0f, 0.0f), 0.0f, "pet speed unknown");
            Assert.AreEqual(1.0f, PetSpeedFloor.GetFactor(true, 10.0f, -3.0f), 0.0f);
            Assert.AreEqual(1.0f, PetSpeedFloor.GetFactor(true, 10.0f, float.NaN), 0.0f);
            Assert.AreEqual(1.0f, PetSpeedFloor.GetFactor(true, 10.0f, float.Epsilon), 0.0f, "an overflowing ratio is refused, not sent");
        }

        [TestMethod]
        public void ComputeReferenceSpeed_MirrorsGetMovementSpeedFormula()
        {
            // GetRunRate(0 burden, 160 run skill, 1) = ((160 / 360) * 11 + 4) / 4
            var expectedRunRate = (160.0f / 360.0f * 11.0f + 4.0f) / 4.0f;

            Assert.AreEqual(expectedRunRate, (float)MovementSystem.GetRunRate(0.0f, PetSpeedFloor.ReferenceRunSkill, 1.0f), 1e-5f);
            Assert.AreEqual(4.0f * expectedRunRate * 0.8f, PetSpeedFloor.ComputeReferenceSpeed(4.0f), 1e-5f);

            Assert.AreEqual(0.0f, PetSpeedFloor.ComputeReferenceSpeed(0.0f), 0.0f, "no RunForward cycle disables the floor");
            Assert.AreEqual(0.0f, PetSpeedFloor.ComputeReferenceSpeed(float.NaN), 0.0f);
        }

        [TestMethod]
        public void ReferenceConstants_HoldTheLiteralsCopiedFromWcid49135()
        {
            // a literal check only: it pins the copied values, it cannot see the world DB row they came from
            Assert.AreEqual(150995304u, PetSpeedFloor.ReferenceMotionTableId);
            Assert.AreEqual(0x09000168u, PetSpeedFloor.ReferenceMotionTableId);
            Assert.AreEqual(0.8f, PetSpeedFloor.ReferenceObjScale, 0.0f);
            Assert.AreEqual(160, PetSpeedFloor.ReferenceRunSkill);
        }

        [TestMethod]
        public void Tunable_DefaultsOn()
        {
            Assert.IsTrue(DefaultPropertyManager.DefaultBooleanProperties.TryGetValue(PetSpeedFloor.EnabledKey, out var property),
                "pet_speed_floor must be registered as a bool tunable");
            Assert.IsTrue(property.Item, "pet_speed_floor defaults to TRUE");
        }

        [TestMethod]
        public void IdleFollow_CarriesTheFactorOnBothHalves()
        {
            var worldObjects = Path.Combine(ClassAbilityAffinityDeclarationTests.RepoRoot(), "Source", "ACE.Server", "WorldObjects");
            var follow = File.ReadAllText(Path.Combine(worldObjects, "CombatPet_Follow.cs"));
            var floor = File.ReadAllText(Path.Combine(worldObjects, "CombatPet_SpeedFloor.cs"));

            Assert.IsTrue(follow.Contains("var speed = MovementSpeedFactor;"), "IssueFollowMove must read the factor once");
            Assert.IsTrue(follow.Contains("motion.MoveToParameters.Speed = speed;"), "the WIRE half of the follow");
            Assert.IsTrue(follow.Contains("mvp.Speed = speed;"), "the SERVER half of the follow");

            Assert.IsTrue(floor.Contains("public override float MovementSpeedFactor"), "CombatPet must override the factor");
            Assert.IsTrue(floor.Contains("return CurrentMovementSlowFactor * speedFloorFactor;"),
                "the pet factor multiplies the slow, it does not replace it");
        }

        // ---- behavioural: a real CombatPet ----------------------------------------------------------------

        private static int nextGuid = 0x7F2A0000;

        /// <summary>
        /// A fresh owner-less CombatPet with MoveSpeed preset, so GetMovementSpeed (which needs dats) is never
        /// called. Fresh per case because the pet caches its factor on the first read.
        /// </summary>
        private static CombatPet NewPet(float moveSpeed)
        {
            // the Creature constructor reads the vital formulas, as in DamageHistoryInfoTests
            TestGameTables.EnsureInitialized();

            var guid = (uint)System.Threading.Interlocked.Increment(ref nextGuid);
            var pet = new CombatPet(new ACE.Entity.Models.Weenie { WeenieClassId = 42, WeenieType = ACE.Entity.Enum.WeenieType.Creature }, new ACE.Entity.ObjectGuid(guid));
            pet.MoveSpeed = moveSpeed;
            return pet;
        }

        /// <summary>Runs <paramref name="body"/> with the reference speed and the tunable seeded, restoring both.</summary>
        private static void WithFloor(float targetSpeed, bool enabled, Action body)
        {
            var savedSource = PetSpeedFloor.ReferenceSpeedSource;
            try
            {
                PetSpeedFloor.ReferenceSpeedSource = () => targetSpeed;
                PropertyManager.ModifyBool(PetSpeedFloor.EnabledKey, enabled);
                body();
            }
            finally
            {
                PetSpeedFloor.ReferenceSpeedSource = savedSource;
                PropertyManager.ModifyBool(PetSpeedFloor.EnabledKey, DefaultPropertyManager.DefaultBooleanProperties[PetSpeedFloor.EnabledKey].Item);
            }
        }

        [TestMethod]
        public void MovementSpeedFactor_PetAtOrAboveTarget_IsExactlyOne()
        {
            WithFloor(16.0f, true, () =>
            {
                Assert.AreEqual(1.0f, NewPet(16.0f).MovementSpeedFactor, 0.0f, "equal to the target");
                Assert.AreEqual(1.0f, NewPet(20.0f).MovementSpeedFactor, 0.0f, "faster than the target");
            });
        }

        [TestMethod]
        public void MovementSpeedFactor_PetBelowTarget_IsTargetOverMoveSpeed()
        {
            WithFloor(16.0f, true, () =>
            {
                Assert.AreEqual(2.0f, NewPet(8.0f).MovementSpeedFactor, 1e-6f);
                Assert.AreEqual(16.0f / 1.4f, NewPet(1.4f).MovementSpeedFactor, 1e-5f);

                // resolved once: a later MoveSpeed change does not re-resolve the cached factor
                var pet = NewPet(4.0f);
                Assert.AreEqual(4.0f, pet.MovementSpeedFactor, 1e-6f);
                pet.MoveSpeed = 16.0f;
                Assert.AreEqual(4.0f, pet.MovementSpeedFactor, 1e-6f);
            });
        }

        [TestMethod]
        public void MovementSpeedFactor_DisabledOrNoReference_IsExactlyOne()
        {
            WithFloor(16.0f, false, () => Assert.AreEqual(1.0f, NewPet(4.0f).MovementSpeedFactor, 0.0f, "tunable off"));
            WithFloor(0.0f, true, () => Assert.AreEqual(1.0f, NewPet(4.0f).MovementSpeedFactor, 0.0f, "reference 0 = dat missing"));
        }

        // ---- dat-backed --------------------------------------------------------------------------------

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void ReferenceSpeed_FromDat_IsPositive()
        {
            var skip = InitializeDats();
            if (skip != null)
                Assert.Inconclusive(skip);

            var runSpeed = MotionTable.GetRunSpeed(PetSpeedFloor.ReferenceMotionTableId);
            Assert.IsTrue(runSpeed > 0.0f, $"motion table 0x{PetSpeedFloor.ReferenceMotionTableId:X8} has no RunForward cycle");

            var target = PetSpeedFloor.ComputeReferenceSpeed(runSpeed);

            Console.WriteLine($"PetSpeedFloor reference: GetRunSpeed(0x{PetSpeedFloor.ReferenceMotionTableId:X8}) = {runSpeed:0.####} m/s, target = {target:0.####} m/s");

            Assert.IsTrue(target > 0.0f);

            // a 0 resolved earlier in this process (before dats loaded) is not cached, so the property agrees now
            Assert.AreEqual(target, PetSpeedFloor.ReferenceSpeed, 1e-6f);
        }

        private static string InitializeDats()
        {
            if (DatManager.PortalDat != null)
                return null;

            var candidates = new List<string>();

            var fromEnv = Environment.GetEnvironmentVariable("ACE_DAT_PATH");
            if (!string.IsNullOrWhiteSpace(fromEnv))
                candidates.Add(fromEnv);

            candidates.Add(@"C:\ACE\Dats\");
            candidates.Add(@"C:\Turbine\Asheron's Call\");

            var datDir = candidates.FirstOrDefault(c => File.Exists(Path.Combine(c, "client_portal.dat")));

            if (datDir == null)
                return $"Skipped: client_portal.dat not found in any of [{string.Join(", ", candidates)}]. Set ACE_DAT_PATH.";

            try
            {
                System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
                DatManager.Initialize(datDir, true, false);
            }
            catch (Exception ex)
            {
                return $"Skipped: DatManager.Initialize({datDir}) failed: {ex.Message}";
            }

            return DatManager.PortalDat == null ? $"Skipped: DatManager.Initialize({datDir}) did not produce a PortalDat." : null;
        }
    }
}
