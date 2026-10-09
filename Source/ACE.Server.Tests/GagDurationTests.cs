using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Command.Handlers;
using ACE.Server.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Tests for the @gag duration token parser (AdminCommands.TryParseGagDuration) and the chat rendering of a
    /// gag length (PlayerManager.DescribeGagDuration). Both are pure; the live command path needs a session
    /// and is exercised in-game.
    /// </summary>
    [TestClass]
    public class GagDurationTests
    {
        private const double Permanent = PlayerManager.PermanentGagDurationSeconds;

        [DataTestMethod]
        [DataRow("5", 300.0)]
        [DataRow("30m", 1800.0)]
        [DataRow("30M", 1800.0)]
        [DataRow("2h", 7200.0)]
        [DataRow("1.5h", 5400.0)]
        [DataRow("7d", 604800.0)]
        [DataRow("0", Permanent)]
        [DataRow("0m", Permanent)]
        [DataRow("perm", Permanent)]
        [DataRow("PERM", Permanent)]
        [DataRow("permanent", Permanent)]
        public void TryParseGagDuration_ParsesMinutesSuffixesAndPermanent(string token, double expectedSeconds)
        {
            Assert.IsTrue(AdminCommands.TryParseGagDuration(token, out var seconds), $"'{token}' should parse");
            Assert.AreEqual(expectedSeconds, seconds, 1e-9, $"'{token}'");
        }

        [DataTestMethod]
        [DataRow("Bob")]
        [DataRow("perm2")]
        [DataRow("5x")]
        [DataRow("-5")]
        [DataRow("h")]
        [DataRow("")]
        [DataRow("5 m")]
        public void TryParseGagDuration_RejectsNonDurationTokens(string token)
        {
            Assert.IsFalse(AdminCommands.TryParseGagDuration(token, out _), $"'{token}' should not parse");
        }

        [TestMethod]
        public void TryParseGagDuration_CapsAtPermanentSentinel()
        {
            Assert.IsTrue(AdminCommands.TryParseGagDuration("99999999999d", out var seconds));
            Assert.AreEqual(Permanent, seconds);
        }

        [DataTestMethod]
        [DataRow(300.0, "for 5 minutes")]
        [DataRow(60.0, "for 1 minute")]
        [DataRow(90.0, "for 1.5 minutes")]
        [DataRow(3600.0, "for 1 hour")]
        [DataRow(7200.0, "for 2 hours")]
        [DataRow(43200.0, "for 12 hours")]
        [DataRow(86400.0, "for 1 day")]
        [DataRow(129600.0, "for 1.5 days")]
        [DataRow(604800.0, "for 7 days")]
        [DataRow(Permanent, "permanently")]
        public void DescribeGagDuration_PicksLargestUnit(double seconds, string expected)
        {
            Assert.AreEqual(expected, PlayerManager.DescribeGagDuration(seconds));
        }

        [TestMethod]
        public void DefaultGagDuration_IsRetailFiveMinutes()
        {
            Assert.AreEqual(300.0, PlayerManager.DefaultGagDurationSeconds);
            Assert.AreEqual("for 5 minutes", PlayerManager.DescribeGagDuration(PlayerManager.DefaultGagDurationSeconds));
        }
    }
}
