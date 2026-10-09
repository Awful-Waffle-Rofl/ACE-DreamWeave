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

        [TestMethod]
        public void FormatWaveClearTime_Zero_IsZeroZeroPointZeroZero()
        {
            Assert.AreEqual("0:00.00", Player.FormatWaveClearTime(0));
        }

        [TestMethod]
        public void FormatWaveClearTime_SubMinute_OmitsMinutesDigitPadding()
        {
            // 4223 centiseconds = 42.23 seconds
            Assert.AreEqual("0:42.23", Player.FormatWaveClearTime(4223));
        }

        [TestMethod]
        public void FormatWaveClearTime_OverTenMinutes_IsMSsCc()
        {
            // 75423 centiseconds = 754.23 seconds = 12 minutes, 34.23 seconds
            Assert.AreEqual("12:34.23", Player.FormatWaveClearTime(75423));
        }

        [TestMethod]
        public void FormatWaveClearTime_TenMinutesExactly_DoesNotZeroPadMinutes()
        {
            // 60000 centiseconds = 600 seconds = exactly 10:00.00
            Assert.AreEqual("10:00.00", Player.FormatWaveClearTime(60000));
        }
    }
}
