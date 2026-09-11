using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Guards Player.FormatWaveScore, the centi-wave -> "Wave N.NN" display format shared by /top wave and the
    /// wave-gauntlet run-end messages. Pure static method - no database, no world.
    /// </summary>
    [TestClass]
    public class WaveChallengeScoreFormatTests
    {
        [TestMethod]
        public void FormatWaveScore_Zero_IsWaveZeroZeroZero()
        {
            Assert.AreEqual("Wave 0.00", Player.FormatWaveScore(0));
        }

        [TestMethod]
        public void FormatWaveScore_TwelveThirtySeven_IsWaveTwelvePointThirtySeven()
        {
            Assert.AreEqual("Wave 12.37", Player.FormatWaveScore(1237));
        }

        [TestMethod]
        public void FormatWaveScore_ExactClear_PadsToTwoZeroes()
        {
            Assert.AreEqual("Wave 12.00", Player.FormatWaveScore(1200));
        }

        [TestMethod]
        public void FormatWaveScore_SubOneWave_PadsWholePart()
        {
            Assert.AreEqual("Wave 0.99", Player.FormatWaveScore(99));
        }
    }
}
