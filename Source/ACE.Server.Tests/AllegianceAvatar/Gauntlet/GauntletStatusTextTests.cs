using System;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Avatars.Gauntlet;

namespace ACE.Server.Tests.Avatars.Gauntlet
{
    /// <summary>The lines /avatar gauntlet shows (plan Task 14): state, wave, level, time left, headcount, own hold, cooldown, best. Pure.</summary>
    [TestClass]
    public class GauntletStatusTextTests
    {
        private static GauntletRunInfo Run(GauntletState state, int secondsLeft = 90) =>
            new GauntletRunInfo(1, 2, 3, "ow_arwic", 0x10064, state, 4, 275, 3, secondsLeft, 12, 9, null);

        private static string Joined(params string[] lines) => string.Join("\n", lines);

        [TestMethod]
        public void Active_ShowsWaveLevelTimeHeadcountHoldCooldownAndBest()
        {
            var lines = GauntletStatusText.Lines(true, Run(GauntletState.Active), holdSecondsLeft: 12.2, cooldownLeft: TimeSpan.FromMinutes(125), bestCenti: 1347);
            var text = Joined(lines.ToArray());

            StringAssert.Contains(text, "under way");
            StringAssert.Contains(text, "Wave 4 (level 275), 3 cleared. 90 s left.");
            StringAssert.Contains(text, "12 fighting, 9 fought so far.");
            StringAssert.Contains(text, "You are held for 13 more seconds.");
            StringAssert.Contains(text, "Cooldown: 2h 5m left.");
            StringAssert.Contains(text, "Best score: Wave 13.47.");
        }

        [TestMethod]
        public void NoRun_ShowsReadyAndNoneYet()
        {
            var lines = GauntletStatusText.Lines(true, null, 0, null, 0);

            Assert.AreEqual(3, lines.Count);
            StringAssert.Contains(lines[0], "no run under way");
            Assert.AreEqual("Cooldown: ready.", lines[1]);
            Assert.AreEqual("Best score: none yet.", lines[2]);
        }

        [TestMethod]
        public void NoHoldLine_WhenNotHeld_AndStagingPointsAtTheGate()
        {
            var text = Joined(GauntletStatusText.Lines(true, Run(GauntletState.Staging, 100), 0, null, 0).ToArray());

            Assert.IsFalse(text.Contains("held for"));
            StringAssert.Contains(text, "gathering");
            StringAssert.Contains(text, "100 s left. Enter through the Gauntlet Gate.");
        }

        [TestMethod]
        public void Disabled_SaysNotAvailable_AndNothingElse()
        {
            var lines = GauntletStatusText.Lines(false, Run(GauntletState.Active), 5, TimeSpan.FromHours(1), 500);

            CollectionAssert.AreEqual(new[] { GauntletStatusText.DisabledLine }, lines.ToArray());
        }

        [TestMethod]
        public void ZeroOrNegativeCooldown_IsReady()
        {
            Assert.AreEqual("Cooldown: ready.", GauntletStatusText.Lines(true, null, 0, TimeSpan.Zero, 0)[1]);
            Assert.AreEqual("Cooldown: ready.", GauntletStatusText.Lines(true, null, 0, TimeSpan.FromSeconds(-5), 0)[1]);
        }

        [TestMethod]
        public void EveryState_HasAWordedLine()
        {
            foreach (GauntletState s in Enum.GetValues(typeof(GauntletState)))
                Assert.IsFalse(string.IsNullOrWhiteSpace(GauntletStatusText.StateText(s)), s.ToString());
        }
    }
}
