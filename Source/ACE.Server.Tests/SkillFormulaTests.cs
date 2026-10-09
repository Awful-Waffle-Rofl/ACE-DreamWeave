using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    [TestClass]
    public class SkillFormulaTests
    {
        [TestMethod]
        public void FiftyFiftyIsAccurate()
        {
            var result = SkillCheck.GetSkillChance(100, 100);
            Assert.AreEqual(0.5d, result);
        }

        [TestMethod]
        public void KnownValuesMatchTheRetailFormula()
        {
            // 1 - 1 / (1 + e^(0.03 * (skill - difficulty)))
            Assert.AreEqual(0.574443, SkillCheck.GetSkillChance(110, 100), 0.000001);
            Assert.AreEqual(0.425557, SkillCheck.GetSkillChance(100, 110), 0.000001);
            Assert.AreEqual(0.952574, SkillCheck.GetSkillChance(200, 100), 0.000001);

            // magic uses a steeper curve: factor 0.07 instead of 0.03
            Assert.AreEqual(0.668188, SkillCheck.GetMagicSkillChance(110, 100), 0.000001);
        }

        [TestMethod]
        public void ChanceIsSymmetricAroundParity()
        {
            // the defender's escape chance is the attacker's failure chance
            foreach (var (skill, difficulty) in new[] { (0, 0), (50, 150), (100, 110), (123, 45), (500, 20) })
            {
                var forward = SkillCheck.GetSkillChance(skill, difficulty);
                var backward = SkillCheck.GetSkillChance(difficulty, skill);

                Assert.AreEqual(1.0, forward + backward, 0.000000001, $"skill {skill} vs difficulty {difficulty}");
            }
        }

        [TestMethod]
        public void ChanceRisesWithSkillAndFallsWithDifficulty()
        {
            var previous = 0.0;
            foreach (var skill in new[] { 0, 50, 100, 150, 250, 400 })
            {
                var chance = SkillCheck.GetSkillChance(skill, 200);
                Assert.IsTrue(chance > previous, $"chance must rise with skill (skill {skill} vs difficulty 200)");
                previous = chance;
            }

            previous = 1.0;
            foreach (var difficulty in new[] { 0, 50, 100, 150, 250, 400 })
            {
                var chance = SkillCheck.GetSkillChance(200, difficulty);
                Assert.IsTrue(chance < previous, $"chance must fall with difficulty (skill 200 vs difficulty {difficulty})");
                previous = chance;
            }
        }

        [TestMethod]
        public void ChanceIsAlwaysAValidProbability()
        {
            foreach (var (skill, difficulty) in new[] { (0, 10000), (10000, 0), (0, 0), (1, 999999), (999999, 1) })
            {
                var chance = SkillCheck.GetSkillChance(skill, difficulty);

                Assert.IsTrue(chance >= 0.0 && chance <= 1.0, $"chance {chance} out of range for skill {skill} vs difficulty {difficulty}");
            }

            // extreme gaps saturate toward certain failure/success
            Assert.IsTrue(SkillCheck.GetSkillChance(1000, 0) > 0.999999);
            Assert.IsTrue(SkillCheck.GetSkillChance(0, 1000) < 0.000001);
        }

        [TestMethod]
        public void UnsignedOverloadMatchesSignedOverload()
        {
            Assert.AreEqual(SkillCheck.GetSkillChance(110, 100), SkillCheck.GetSkillChance(110u, 100u));
            Assert.AreEqual(SkillCheck.GetSkillChance(45, 320), SkillCheck.GetSkillChance(45u, 320u));
        }
    }
}
