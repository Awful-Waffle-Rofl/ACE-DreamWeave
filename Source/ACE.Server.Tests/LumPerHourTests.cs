using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Command.Handlers;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Boundary math for the /lph Luminance-per-hour rate (<see cref="Player.CalcLumPerHour"/>): a window
    /// shorter than <see cref="Player.MinLumRateWindowSeconds"/> must report null (too short to be
    /// meaningful) rather than a number divided by ~0, the boundary itself is inclusive, and a negative or
    /// non-finite elapsed time is treated the same as "too short" rather than producing a negative or
    /// nonsensical rate. Also pins <see cref="PlayerCommands.FormatLphElapsed"/>'s "1h 23m" / "23m" / "45s"
    /// rendering boundaries, including that hours are total hours (never a day component) and that a
    /// negative input is clamped to zero.
    /// </summary>
    [TestClass]
    public class LumPerHourTests
    {
        [TestMethod]
        public void CalcLumPerHour_PlainRate()
        {
            Assert.AreEqual(3600.0, Player.CalcLumPerHour(3600, 3600));
            Assert.AreEqual(7200.0, Player.CalcLumPerHour(1800, 900));
        }

        [TestMethod]
        public void CalcLumPerHour_ZeroEarnedIsZeroNotNull()
        {
            var result = Player.CalcLumPerHour(0, 3600);

            Assert.IsNotNull(result);
            Assert.AreEqual(0.0, result.Value);
        }

        [TestMethod]
        public void CalcLumPerHour_BelowMinWindowIsNull()
        {
            Assert.IsNull(Player.CalcLumPerHour(100, Player.MinLumRateWindowSeconds - 0.001));
            Assert.IsNull(Player.CalcLumPerHour(100, 0.0));
        }

        [TestMethod]
        public void CalcLumPerHour_AtMinWindowIsInclusive()
        {
            Assert.IsNotNull(Player.CalcLumPerHour(100, Player.MinLumRateWindowSeconds));
        }

        [TestMethod]
        public void CalcLumPerHour_NegativeElapsedIsNull()
        {
            Assert.IsNull(Player.CalcLumPerHour(100, -1.0));
        }

        [TestMethod]
        public void CalcLumPerHour_NonFiniteElapsedIsNull()
        {
            Assert.IsNull(Player.CalcLumPerHour(100, double.NaN));
            Assert.IsNull(Player.CalcLumPerHour(100, double.PositiveInfinity));
        }

        [TestMethod]
        public void CalcLumPerHour_LargeEarnedDoesNotThrow()
        {
            var result = Player.CalcLumPerHour(long.MaxValue / 4, 3600.0);

            Assert.IsNotNull(result);
            Assert.IsTrue(double.IsFinite(result.Value));
        }

        [TestMethod]
        public void FormatLphElapsed_Zero()
        {
            Assert.AreEqual("0s", PlayerCommands.FormatLphElapsed(0));
        }

        [TestMethod]
        public void FormatLphElapsed_JustUnderOneMinute()
        {
            Assert.AreEqual("59s", PlayerCommands.FormatLphElapsed(59));
        }

        [TestMethod]
        public void FormatLphElapsed_OneMinute()
        {
            Assert.AreEqual("1m", PlayerCommands.FormatLphElapsed(60));
        }

        [TestMethod]
        public void FormatLphElapsed_JustUnderOneHour()
        {
            Assert.AreEqual("59m", PlayerCommands.FormatLphElapsed(3599));
        }

        [TestMethod]
        public void FormatLphElapsed_OneHour()
        {
            Assert.AreEqual("1h 0m", PlayerCommands.FormatLphElapsed(3600));
        }

        [TestMethod]
        public void FormatLphElapsed_ThirtyHoursNeverRendersADayComponent()
        {
            Assert.AreEqual("30h 5m", PlayerCommands.FormatLphElapsed(108300));
        }

        [TestMethod]
        public void FormatLphElapsed_NegativeIsClampedToZero()
        {
            Assert.AreEqual("0s", PlayerCommands.FormatLphElapsed(-10));
        }
    }
}
