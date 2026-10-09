using System.Numerics;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Physics.Animation;
using ACE.Server.WorldEvents;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Unit coverage for World Events boss confinement: the movement-flag opt-out
    /// (<see cref="Creature.GetMonsterMoveToFlags"/>), the tether distance test
    /// (<see cref="Creature.IsWithinTether"/>) and the spawner's stamp rules
    /// (<see cref="WorldEventSpawner.AppliesBossTether"/> and friends).
    ///
    /// All pure: no landblock, no physics object, no live creature, no PropertyManager read. The parts that
    /// cannot be covered here - that FindNextTarget actually consults the filtered list, that CheckMissHome
    /// runs on the monster tick - need a running world and are queued for live verification.
    /// </summary>
    [TestClass]
    public class WorldEventBossTetherTests
    {
        private const uint TestInstance = 0u;

        // An OUTDOOR cell in landblock 0x0102, matching AntiBlinkGeometryTests' convention.
        private const uint BlockA = 0x01020025;

        // The landblock immediately east of BlockA, so a position in it is one block (192 m) further east.
        private const uint BlockB = 0x02020025;

        private static Position At(uint cell, float x, float y, float z = 0f)
        {
            return new Position(cell, x, y, z, 0f, 0f, 0f, 1f, TestInstance);
        }

        // ---- GetMonsterMoveToFlags ----

        /// <summary>
        /// The pre-feature flag set, exactly as Monster_Navigation.GetMovementParameters hardcoded it. A
        /// monster with no DisableSticky property must still get precisely these four.
        /// </summary>
        private const MovementParamFlags MeleeFlags =
            MovementParamFlags.FailWalk | MovementParamFlags.UseFinalHeading
            | MovementParamFlags.Sticky | MovementParamFlags.MoveAway;

        [TestMethod]
        public void MoveToFlags_MeleeMonsterWithoutTheProperty_IsUnchanged()
        {
            Assert.AreEqual(MeleeFlags, Creature.GetMonsterMoveToFlags(turnTo: false, disableSticky: false));
        }

        [TestMethod]
        public void MoveToFlags_DisableSticky_DropsOnlySticky()
        {
            var flags = Creature.GetMonsterMoveToFlags(turnTo: false, disableSticky: true);

            Assert.IsFalse(flags.HasFlag(MovementParamFlags.Sticky), "Sticky must be omitted");
            Assert.IsTrue(flags.HasFlag(MovementParamFlags.FailWalk), "FailWalk must be kept");
            Assert.IsTrue(flags.HasFlag(MovementParamFlags.UseFinalHeading), "UseFinalHeading must be kept");
            Assert.IsTrue(flags.HasFlag(MovementParamFlags.MoveAway), "MoveAway must be kept");

            // and nothing else was added
            Assert.AreEqual(MeleeFlags & ~MovementParamFlags.Sticky, flags);
        }

        [TestMethod]
        public void MoveToFlags_TurnToMonster_AddsNothingEitherWay()
        {
            // ranged / immobile monsters never got the melee flags, and DisableSticky must not change that
            Assert.AreEqual(default(MovementParamFlags), Creature.GetMonsterMoveToFlags(turnTo: true, disableSticky: false));
            Assert.AreEqual(default(MovementParamFlags), Creature.GetMonsterMoveToFlags(turnTo: true, disableSticky: true));
        }

        // ---- IsWithinTether ----

        [TestMethod]
        public void Tether_NoRadius_AdmitsEverything()
        {
            var home = new Vector3(0f, 0f, 0f);
            var far = new Vector3(5000f, 5000f, 0f);

            Assert.IsTrue(Creature.IsWithinTether(home, far, null), "absent property must leave targeting alone");
            Assert.IsTrue(Creature.IsWithinTether(home, far, 0.0), "a zero radius must not confine the creature to a point");
            Assert.IsTrue(Creature.IsWithinTether(home, far, -1.0), "a negative radius reads as no tether");
        }

        [TestMethod]
        public void Tether_InsideTheRadius_IsAdmitted()
        {
            var home = new Vector3(100f, 100f, 0f);

            Assert.IsTrue(Creature.IsWithinTether(home, new Vector3(100f, 100f, 0f), 50.0), "the home point itself");
            Assert.IsTrue(Creature.IsWithinTether(home, new Vector3(130f, 100f, 0f), 50.0), "30 m out on one axis");
            Assert.IsTrue(Creature.IsWithinTether(home, new Vector3(130f, 140f, 0f), 50.0), "3-4-5 triangle, exactly on the boundary");
        }

        [TestMethod]
        public void Tether_OutsideTheRadius_IsRejected()
        {
            var home = new Vector3(100f, 100f, 0f);

            Assert.IsFalse(Creature.IsWithinTether(home, new Vector3(151f, 100f, 0f), 50.0));
            Assert.IsFalse(Creature.IsWithinTether(home, new Vector3(140f, 140f, 0f), 50.0), "56.6 m diagonal");
        }

        [TestMethod]
        public void Tether_MeasuresHeightToo()
        {
            // a boss on the ground and a player 60 m above it are 60 m apart, not 0
            var home = new Vector3(100f, 100f, 0f);

            Assert.IsFalse(Creature.IsWithinTether(home, new Vector3(100f, 100f, 60f), 50.0));
            Assert.IsTrue(Creature.IsWithinTether(home, new Vector3(100f, 100f, 40f), 50.0));
        }

        [TestMethod]
        public void Tether_CrossesLandblockBoundariesInGlobalCoords()
        {
            // The same reason CheckMissHome uses ToGlobal: a kiter one landblock east is 192 m away, not
            // the couple of metres a raw in-block coordinate comparison would report.
            var home = At(BlockA, 96f, 96f).ToGlobal();
            var nextBlock = At(BlockB, 96f, 96f).ToGlobal();

            Assert.IsFalse(Creature.IsWithinTether(home, nextBlock, 50.0));
            Assert.IsTrue(Creature.IsWithinTether(home, nextBlock, 200.0));
        }

        // ---- AppliesBossTether ----

        [TestMethod]
        public void AppliesBossTether_BossWithTheLeverOn_Stamps()
        {
            Assert.IsTrue(WorldEventSpawner.AppliesBossTether(WorldEventSpawnKind.Boss, true, 50.0));
        }

        [TestMethod]
        public void AppliesBossTether_OnlyTheBossKind()
        {
            Assert.IsFalse(WorldEventSpawner.AppliesBossTether(WorldEventSpawnKind.Wave, true, 50.0));
            Assert.IsFalse(WorldEventSpawner.AppliesBossTether(WorldEventSpawnKind.Source, true, 50.0));
            Assert.IsFalse(WorldEventSpawner.AppliesBossTether(WorldEventSpawnKind.InertObjective, true, 50.0));
            Assert.IsFalse(WorldEventSpawner.AppliesBossTether(WorldEventSpawnKind.Npc, true, 50.0));
            Assert.IsFalse(WorldEventSpawner.AppliesBossTether(WorldEventSpawnKind.Decor, true, 50.0));
        }

        [TestMethod]
        public void AppliesBossTether_LeverOff_StampsNothing()
        {
            Assert.IsFalse(WorldEventSpawner.AppliesBossTether(WorldEventSpawnKind.Boss, false, 50.0));
        }

        [TestMethod]
        public void AppliesBossTether_UnusableRadius_StampsNothing()
        {
            // a zero tether would confine the boss to a point and leave it nothing it may target
            Assert.IsFalse(WorldEventSpawner.AppliesBossTether(WorldEventSpawnKind.Boss, true, 0.0));
            Assert.IsFalse(WorldEventSpawner.AppliesBossTether(WorldEventSpawnKind.Boss, true, -50.0));
            Assert.IsFalse(WorldEventSpawner.AppliesBossTether(WorldEventSpawnKind.Boss, true, double.NaN));
            Assert.IsFalse(WorldEventSpawner.AppliesBossTether(WorldEventSpawnKind.Boss, true, double.PositiveInfinity));
        }

        // ---- BossHomeRadiusFor ----

        [TestMethod]
        public void BossHomeRadius_IsTwiceTheTether()
        {
            Assert.AreEqual(100.0, WorldEventSpawner.BossHomeRadiusFor(50.0), 0.0001);
            Assert.AreEqual(24.0, WorldEventSpawner.BossHomeRadiusFor(12.0), 0.0001);
        }

        [TestMethod]
        public void BossHomeRadius_LeavesTheLeashOutsideTheTether()
        {
            // the ordinary MoveToHome leash must only catch a boss the tether retarget could not pull back
            const double tether = 50.0;

            Assert.IsTrue(WorldEventSpawner.BossHomeRadiusFor(tether) > tether);
        }

        // ---- StampsBossTargetingTactic ----

        [TestMethod]
        public void StampsTargetingTactic_OnlyWhenTheWeenieAuthoredNone()
        {
            Assert.IsTrue(WorldEventSpawner.StampsBossTargetingTactic(null), "no PropertyInt row at all");
            Assert.IsTrue(WorldEventSpawner.StampsBossTargetingTactic(0), "an explicit TargetingTactic.None");
        }

        [TestMethod]
        public void StampsTargetingTactic_NeverOverwritesAnAuthoredTactic()
        {
            // the masks the ten shipped bosses actually carry: 13 (Random|LastDamager|TopDamager),
            // 9 (Random|TopDamager), 5 (Random|LastDamager), 3 (Random|Focused)
            Assert.IsFalse(WorldEventSpawner.StampsBossTargetingTactic(13));
            Assert.IsFalse(WorldEventSpawner.StampsBossTargetingTactic(9));
            Assert.IsFalse(WorldEventSpawner.StampsBossTargetingTactic(5));
            Assert.IsFalse(WorldEventSpawner.StampsBossTargetingTactic(3));
            Assert.IsFalse(WorldEventSpawner.StampsBossTargetingTactic((int)TargetingTactic.Nearest));
        }

        [TestMethod]
        public void NearestTacticValue_IsTheOneTheSpawnerWrites()
        {
            // Guards the value the spawner stamps against an upstream enum renumber. Read through a
            // variable so the assertion is a real comparison rather than a constant the compiler folds
            // away (MSTEST0032), matching WorldEventAudienceFloorTests' handling of the same warning.
            var nearest = (int)TargetingTactic.Nearest;

            Assert.AreEqual(0x40, nearest);
        }
    }
}
