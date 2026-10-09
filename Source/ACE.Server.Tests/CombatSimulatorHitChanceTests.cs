using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.CombatSimulator;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    [TestClass]
    public class CombatSimulatorHitChanceTests
    {
        private const float Epsilon = 0.0001f;

        [TestMethod]
        public void HitChance_IsComplementOfEngineEvadeChance()
        {
            // the engine computes evade = 1 - GetSkillChance(attack, defense)
            var expected = 1.0f - (1.0f - (float)SkillCheck.GetSkillChance(400u, 300u));

            Assert.AreEqual(expected, HitChance.Calculate(400u, 300u), Epsilon);
        }

        [TestMethod]
        public void HitChance_EqualSkills_IsOneHalf()
        {
            Assert.AreEqual(0.5f, HitChance.Calculate(500u, 500u), Epsilon);
        }

        [TestMethod]
        public void HitChance_OverwhelmingDefense_ApproachesZero()
        {
            Assert.IsTrue(HitChance.Calculate(400u, 10000u) < 0.001f);
        }
    }
}
