using System.Collections.Generic;

using ACE.Server.Managers;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldEvents.Objectives;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// DestroySourceObjective.OnSourceSpawning against a real Creature built through the monster-effect test seam
    /// (TestCreatures.CreateEffectCarrier): the sealed state a phased source spawns in, applied once.
    /// </summary>
    [TestClass]
    public class DestroySourceSpawnGuardTests
    {
        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        private sealed class QuietPort : IDestroySourcePhasePort
        {
            public readonly List<(uint Guid, bool Immune, uint Floor)> Guards = new List<(uint, bool, uint)>();

            public void SetGuard(uint guid, bool immune, uint floorHp) => Guards.Add((guid, immune, floorHp));

            public bool TryGetHealth(uint guid, out uint current, out uint max)
            {
                current = 0;
                max = 0;
                return false;
            }

            public void Announce(string line)
            {
            }
        }

        private static GoalDef Goal() => new GoalDef { Id = "destroy_source", TypeKind = GoalType.DestroySource };

        [TestMethod]
        public void OnSourceSpawning_AppliesOverlayFloorAndImmune_AndASecondCallIsANoOp()
        {
            var source = TestCreatures.CreateEffectCarrier();
            var max = source.Health.MaxValue;
            Assert.IsTrue(max > 0, "positive control: the fixture has health, so floor = max can differ from 0");
            Assert.IsNull(source.MonsterEffects);
            Assert.IsFalse(source.P_DigsiteImmune);
            Assert.AreEqual(0u, source.P_HealthFloor);

            var machine = new DestroySourcePhases(DestroySourcePhaseSettings.Defaults, new QuietPort(), () => 4);
            var objective = new DestroySourceObjective(Goal(), () => new List<uint> { source.Guid.Full });
            objective.AttachPhases(machine, () => 0);

            objective.OnSourceSpawning(source);

            Assert.IsNotNull(source.MonsterEffects, "the immune overlay is attached");
            Assert.AreEqual(1, source.MonsterEffects.Count);
            Assert.AreEqual(max, source.P_HealthFloor, "floor = max health");
            Assert.IsTrue(source.P_DigsiteImmune);

            // Second call: nothing is redone (the effect set and its state array are the same objects; a changed flag stays changed).
            var set = source.MonsterEffects;
            var states = source.MonsterEffectStates;
            source.P_HealthFloor = 5;
            source.P_DigsiteImmune = false;

            objective.OnSourceSpawning(source);

            Assert.AreSame(set, source.MonsterEffects);
            Assert.AreSame(states, source.MonsterEffectStates, "no fresh effect-state array");
            Assert.AreEqual(5u, source.P_HealthFloor);
            Assert.IsFalse(source.P_DigsiteImmune);
        }

        [TestMethod]
        public void OnSourceSpawning_WithPhasesOff_TouchesNothing()
        {
            var source = TestCreatures.CreateEffectCarrier();
            var objective = new DestroySourceObjective(Goal(), () => new List<uint> { source.Guid.Full });

            objective.OnSourceSpawning(source);

            Assert.IsNull(source.MonsterEffects);
            Assert.AreEqual(0u, source.P_HealthFloor);
            Assert.IsFalse(source.P_DigsiteImmune);
        }
    }
}
