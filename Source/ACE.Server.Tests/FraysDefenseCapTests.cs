using System.Linq;

using ACE.Server.Frays;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>The Frays defense ceiling: every family can be hit at the camper's level.</summary>
    [TestClass]
    public class FraysDefenseCapTests
    {
        [TestMethod]
        public void Cap_IsSetAt300_AndMovesWithLevel()
        {
            Assert.AreEqual(450u, FraysLevelScale.DefenseCapFor(300, 450, 1.2));
            Assert.AreEqual(570u, FraysLevelScale.DefenseCapFor(400, 450, 1.2));
            Assert.AreEqual(210u, FraysLevelScale.DefenseCapFor(100, 450, 1.2));
            Assert.AreEqual(1u, FraysLevelScale.DefenseCapFor(1, 100, 5), "never below 1");
        }

        [TestMethod]
        public void Cap_Zero_IsOff()
        {
            Assert.AreEqual(0u, FraysLevelScale.DefenseCapFor(300, 0, 1.2));
        }

        [TestMethod]
        public void Cap_AtLevel300_LeavesAFairHitChance()
        {
            // A level 300 character's buffed attack is around 500-520; against the cap they land most swings.
            var chance = ACE.Server.WorldObjects.SkillCheck.GetSkillChance(500, (int)FraysLevelScale.DefenseCapFor(300, 450, 1.2));
            Assert.IsTrue(chance > 0.75, $"hit chance {chance:P0}");
        }

        [TestMethod]
        public void DefaultCap_AlmostNeverEvadesOrResists()
        {
            // Melee, missile and magic defense are all low, so creatures do not evade or resist.
            var cap = FraysSettings.All.First(d => d.Key == FraysSettings.DefenseCap).Default;
            var per = FraysSettings.All.First(d => d.Key == FraysSettings.DefenseCapPerLevel).Default;
            Assert.AreEqual(200.0, cap);

            // A level 300 attacker (about 500) and a level 441 one at Heat 10 (creatures at 466, attack about 670).
            Assert.IsTrue(ACE.Server.WorldObjects.SkillCheck.GetSkillChance(500, (int)FraysLevelScale.DefenseCapFor(300, cap, per)) > 0.99);
            Assert.IsTrue(ACE.Server.WorldObjects.SkillCheck.GetSkillChance(670, (int)FraysLevelScale.DefenseCapFor(466, cap, per)) > 0.99);
        }

        [TestMethod]
        public void AttackVsDefense_LandsAboutHalfAtHeat0_AndMostAtHeat20()
        {
            // Without the floor, a camper's 928 melee defense against a level 480 Monouga's 879 attack dodged 81% of swings.
            var off = FraysSettings.All.First(d => d.Key == FraysSettings.AttackVsDefense).Default;
            var per = FraysSettings.All.First(d => d.Key == FraysSettings.AttackVsDefensePerHeat).Default;

            double Lands(int heat) => ACE.Server.WorldObjects.SkillCheck.GetSkillChance((int)FraysRules.AttackVsDefense(879, 928, heat, off, per), 928);

            Assert.AreEqual(0.5, Lands(0), 0.01);
            Assert.IsTrue(Lands(13) > 0.7 && Lands(13) < 0.8, $"Heat 13: {Lands(13):P0}");
            Assert.IsTrue(Lands(20) > 0.83 && Lands(20) < 0.9, $"Heat 20: {Lands(20):P0}");
        }

        [TestMethod]
        public void AttackVsDefense_NeverLowers_AndCanBeTurnedOff()
        {
            Assert.AreEqual(900u, FraysRules.AttackVsDefense(900, 600, 10, 0, 3), "already above the floor");
            Assert.AreEqual(630u, FraysRules.AttackVsDefense(500, 600, 10, 0, 3));
            Assert.AreEqual(500u, FraysRules.AttackVsDefense(500, 900, 10, -200, 3), "off");
        }
    }
}
