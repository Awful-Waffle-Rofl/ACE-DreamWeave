using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// QuestManager.BuildKillTaskMessage: the player-facing progress line sent after a kill-task
    /// stamp. Covers the three shapes the quest's MaxSolves/IsMaxSolves state can put it in - a
    /// normal in-progress count, a finite quest that just hit its cap, and an unlimited
    /// (MaxSolves -1, e.g. a daily-repeatable clear) quest, which is never IsMaxSolves and so
    /// must not fall into the "You must kill -1" branch.
    /// </summary>
    [TestClass]
    public class QuestManagerKillTaskMessageTests
    {
        [TestMethod]
        public void InProgress_FiniteQuest_ReportsRemainingCount()
        {
            var msg = QuestManager.BuildKillTaskMessage(numTimesCompleted: 3, pluralName: "Sclavii", maxSolves: 10, isMaxSolves: false);

            Assert.AreEqual("You have killed 3 Sclavii! You must kill 10 to complete your task.", msg);
        }

        [TestMethod]
        public void MaxSolvesReached_FiniteQuest_ReportsComplete()
        {
            var msg = QuestManager.BuildKillTaskMessage(numTimesCompleted: 10, pluralName: "Sclavii", maxSolves: 10, isMaxSolves: true);

            Assert.AreEqual("You have killed 10 Sclavii! Your task is complete!", msg);
        }

        [TestMethod]
        public void UnlimitedSolves_NeverMaxSolves_ReportsComplete_NotNegativeOne()
        {
            // Bluespire ladder D4 boss (BluespireLadderD4Cleared, max_Solves -1): IsMaxSolves is
            // always false for an unlimited quest, so without the maxSolves < 0 branch this would
            // fall through to "You must kill -1 to complete your task."
            var msg = QuestManager.BuildKillTaskMessage(numTimesCompleted: 1, pluralName: "The Hollow Queens", maxSolves: -1, isMaxSolves: false);

            Assert.AreEqual("You have killed 1 The Hollow Queens! Task complete!", msg);
            StringAssert.DoesNotMatch(msg, new System.Text.RegularExpressions.Regex("-1"));
        }
    }
}
