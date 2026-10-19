using System.Linq;

using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>The healing group of the Hold settings.</summary>
    public partial class WorldEventHoldSettingsTests
    {
        [TestMethod]
        public void Healing_WithNothingSet_ReadExactlyTheGoalsJson()
        {
            var goal = EnvoysGoal();
            var p = goal.Protect;

            using var scope = new HoldSettingsScope();

            Assert.AreEqual(0.15, p.ShrineHeal);
            Assert.AreEqual(45, p.EffectiveShrineCooldownSeconds);
            Assert.IsNull(p.ShrineCount, "every knight gets a shrine");
            Assert.IsFalse(p.ShrineHealsAll);
            Assert.AreEqual(0, p.ShrinePlayerCooldownSeconds);
            Assert.AreEqual(8, p.ShrineBindRange);
            Assert.AreEqual(1, p.RegenIntervalSeconds);
            Assert.IsTrue(p.PlayerHealAllowed);
            Assert.AreEqual(1, p.PlayerHealMult);

            foreach (var s in HoldSettings.InGroup(HoldSettings.GroupHealing).Where(s => !s.IsReused))
                Assert.IsFalse(HoldSettings.Describe(s, goal).Overridden, s.Key);
        }

        [TestMethod]
        public void Healing_ShrineSettingsAreLive_AndZeroIsAllowed()
        {
            var goal = EnvoysGoal();
            var p = goal.Protect;

            using var scope = new HoldSettingsScope();

            scope.Set(HoldKeys.ShrineHeal, "0.4");
            scope.Set(HoldKeys.ShrineCooldownSeconds, "20");
            Assert.AreEqual(0.4, p.ShrineHeal);
            Assert.AreEqual(20, p.EffectiveShrineCooldownSeconds);

            scope.Set(HoldKeys.ShrineHeal, "0");
            scope.Set(HoldKeys.ShrineCooldownSeconds, "0");
            Assert.AreEqual(0, p.ShrineHeal, "a 0 heal on purpose");
            Assert.AreEqual(0, p.EffectiveShrineCooldownSeconds, "a 0 rest on purpose (goals.json itself may not go under 1)");

            scope.Set(HoldKeys.Shrine, "off");
            Assert.IsNull(p.Shrine, "shrines off live");

            scope.Set(HoldKeys.Shrine, "on");
            p.Shrine = null;
            Assert.IsNotNull(p.Shrine, "on with no block in goals.json: the default shrine");

            scope.Set(HoldKeys.ShrineUseRadius, "7");
            Assert.AreEqual(7, p.Shrine.UseRadius);

            scope.Set(HoldKeys.ShrineCount, "1");
            Assert.AreEqual(1, p.ShrineCount);
        }

        [TestMethod]
        public void Healing_ReloadValidatesTheFileNotTheLiveValue()
        {
            // A shrine rest of 0 is fine as a live setting but below the file's own 1 s minimum: a reload must keep the goal.
            using var scope = new HoldSettingsScope();
            scope.Set(HoldKeys.ShrineCooldownSeconds, "0");
            scope.Set(HoldKeys.DefendSwingSeconds, "9");

            var goal = EnvoysGoal();

            Assert.IsNotNull(goal, "the goal still loads");
            Assert.AreEqual(45, goal.Protect.ShrineCooldownSecondsJson, "the file's value is kept as is");
            Assert.AreEqual(0, goal.Protect.EffectiveShrineCooldownSeconds, "and the live value still applies");
        }

        [TestMethod]
        public void Healing_PlayerHealToggleAndMultiplier()
        {
            var goal = EnvoysGoal();
            var p = goal.Protect;

            using var scope = new HoldSettingsScope();

            scope.Set(HoldKeys.PlayerHeal, "off");
            Assert.IsFalse(p.PlayerHealAllowed);

            scope.Set(HoldKeys.PlayerHeal, "on");
            scope.Set(HoldKeys.PlayerHealCap, "0");
            Assert.IsFalse(p.PlayerHealAllowed, "a 0 cap is off as before");

            scope.Set(HoldKeys.PlayerHealCap, "");
            scope.Set(HoldKeys.PlayerHealMult, "2");
            Assert.IsTrue(p.PlayerHealAllowed);
            Assert.AreEqual(2, p.PlayerHealMult);
        }

        [TestMethod]
        public void Healing_HealValuesAreLive()
        {
            var goal = EnvoysGoal();
            var p = goal.Protect;

            using var scope = new HoldSettingsScope();

            scope.Set(HoldKeys.CorruptedKillHeal, "0.25");
            scope.Set(HoldKeys.RescueHeal, "0");
            scope.Set(HoldKeys.RescueCapPerWave, "0.3");
            scope.Set(HoldKeys.RegenPerSecond, "0.02");
            scope.Set(HoldKeys.RegenClearRadius, "15");
            scope.Set(HoldKeys.RegenIntervalSeconds, "5");
            scope.Set(HoldKeys.HealBetweenWaves, "0.2");
            scope.Set(HoldKeys.HealBudgetPerWave, "0.5");
            scope.Set(HoldKeys.HealReductionAfterHit, "0");
            scope.Set(HoldKeys.HealReductionSeconds, "10");

            Assert.AreEqual(0.25, p.CorruptedKillHeal);
            Assert.AreEqual(0, p.RescueHeal);
            Assert.AreEqual(0.3, p.RescueCapPerWave);
            Assert.AreEqual(0.02, p.ClearRegenPerSecond);
            Assert.AreEqual(15, p.ClearRadius);
            Assert.AreEqual(5, p.RegenIntervalSeconds);
            Assert.AreEqual(0.2, p.HealBetweenWaves);
            Assert.AreEqual(0.5, p.HealBudgetPerWave);
            Assert.AreEqual(0, p.HealReductionAfterHit);
            Assert.AreEqual(10, p.HealReductionSeconds);

            scope.Set(HoldKeys.RegenPerSecond, "1");
            Assert.AreEqual(0.1, p.ClearRegenPerSecond, "clamped to 10% a second");
        }

        [TestMethod]
        public void Healing_ShrineUseCheck_ShrineRestFirstThenThePlayersOwn()
        {
            const double now = 1000;

            Assert.AreEqual(WorldEventHoldRules.ShrineUseState.Ready, WorldEventHoldRules.ShrineUseCheck(now, 0, 0, 0), "never used");
            Assert.AreEqual(WorldEventHoldRules.ShrineUseState.ShrineResting, WorldEventHoldRules.ShrineUseCheck(now, now + 5, 0, 0));
            Assert.AreEqual(WorldEventHoldRules.ShrineUseState.Ready, WorldEventHoldRules.ShrineUseCheck(now, 0, now + 30, 0), "player cooldown off (0): ignored");
            Assert.AreEqual(WorldEventHoldRules.ShrineUseState.PlayerResting, WorldEventHoldRules.ShrineUseCheck(now, 0, now + 30, 60));
            Assert.AreEqual(WorldEventHoldRules.ShrineUseState.ShrineResting, WorldEventHoldRules.ShrineUseCheck(now, now + 5, now + 30, 60), "the shrine first");
            Assert.AreEqual(WorldEventHoldRules.ShrineUseState.Ready, WorldEventHoldRules.ShrineUseCheck(now, now, now, 60), "both just over");
        }

        [TestMethod]
        public void Healing_PlayerCooldownLineIsAscii()
        {
            var line = WorldEventHoldRules.ShrinePlayerCooldownLine(12.2);
            StringAssert.Contains(line, "13 seconds");
            Assert.IsTrue(line.All(c => c < 128), line);
        }
    }
}
