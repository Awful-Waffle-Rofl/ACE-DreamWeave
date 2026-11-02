using System;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Tests.ThreadDungeons;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Source-text pins on the live boss guard (precedent: ThreadDungeonShapeSeamTests): LiveSpeedCourseWorld cannot run
    /// under this harness, so these tie its writes to the behaviour SpeedBossPhaseTests and CreatureHealthFloorTests
    /// prove on the fake world and on a bare Creature. Spawn must attach the immune record and write the floor and the
    /// immunity AFTER ShapeCreature and BEFORE EnterWorld (the first CreateObject carries them, and nothing lands
    /// unguarded); SetBossGuard must write both fields. Reads files only: no PropertyManager key to seed.
    /// </summary>
    [TestClass]
    public class SpeedBossGuardSourceTests
    {
        private const string LivePath = "Source/ACE.Server/SpeedCourses/LiveSpeedCourseWorld.cs";

        private static string Body(string signature) => PooledLootSourceText.MethodBody(PooledLootSourceText.Read(LivePath), signature);

        private static string SpawnBody()
            => Body("public SpeedCreatureHandle Spawn(DungeonSpawnPlan plan, DungeonSpawnPlanEntry entry, SpeedCreatureStamp stamp, out uint maxHealth)");

        private static int At(string body, string needle)
        {
            var i = body.IndexOf(needle, StringComparison.Ordinal);
            Assert.IsTrue(i >= 0, $"LiveSpeedCourseWorld.Spawn no longer contains `{needle}`");
            return i;
        }

        [TestMethod]
        public void Spawn_guards_the_boss_after_shaping_and_before_EnterWorld()
        {
            var body = SpawnBody();

            var shape = At(body, "ThreadDungeonSpawner.ShapeCreature(");
            var enter = At(body, "creature.EnterWorld()");

            foreach (var write in new[]
            {
                "creature.ApplyRuntimeScaleMultiplier(stamp.Scale)",
                "creature.ApplyMonsterEffectOverlay(BossImmuneRecord)",
                "creature.P_HealthFloor = SpeedBossPhaseRules.FloorFor(creature.Health.MaxValue, stamp.GuardFloorFraction.Value);",
                "creature.P_DigsiteImmune = stamp.GuardFloorFraction.Value >= 1.0;",
            })
            {
                var i = At(body, write);
                Assert.IsTrue(i > shape, $"`{write}` must come after ShapeCreature (which rewrites health and effects)");
                Assert.IsTrue(i < enter, $"`{write}` must come before EnterWorld");
            }
        }

        [TestMethod]
        public void SetBossGuard_writes_the_floor_and_the_immunity()
        {
            var body = Body("public void SetBossGuard(SpeedCreatureHandle h, bool immune, uint floorHp)");

            StringAssert.Contains(body, "c.P_HealthFloor = floorHp;");
            StringAssert.Contains(body, "c.P_DigsiteImmune = immune;");
        }

        [TestMethod]
        public void The_immune_record_is_the_one_ImmuneEffect_reads()
        {
            Assert.AreEqual("immune", ACE.Server.SpeedCourses.LiveSpeedCourseWorld.BossImmuneRecord);
        }
    }
}