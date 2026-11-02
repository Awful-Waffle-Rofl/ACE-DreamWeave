using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Guards Player.IsWaveDeadlineLive, the pure rule behind the wave time-limit segments. Regression: wave 15
    /// was cleared, the 10 second inter-wave gap began, and the wave-15 deadline segment still fired because
    /// the live wave number does not change until the next wave spawns.
    /// </summary>
    [TestClass]
    public class WaveChallengeDeadlineTests
    {
        [TestMethod]
        public void LiveUnclearedWave_DeadlineActs()
        {
            Assert.IsTrue(Player.IsWaveDeadlineLive(liveWave: 15, clearedWaves: 14, segmentWave: 15));
        }

        [TestMethod]
        public void ClearedWaveDuringInterWaveGap_DeadlineDoesNotAct()
        {
            // wave 15 cleared: live wave is still 15, cleared count is 15
            Assert.IsFalse(Player.IsWaveDeadlineLive(liveWave: 15, clearedWaves: 15, segmentWave: 15));
        }

        [TestMethod]
        public void StaleSegmentFromEarlierWave_DeadlineDoesNotAct()
        {
            Assert.IsFalse(Player.IsWaveDeadlineLive(liveWave: 16, clearedWaves: 15, segmentWave: 15));
        }

        [TestMethod]
        public void NewWaveAfterGap_ItsOwnDeadlineActs()
        {
            Assert.IsTrue(Player.IsWaveDeadlineLive(liveWave: 16, clearedWaves: 15, segmentWave: 16));
        }
    }
}
