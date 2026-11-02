using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>Guards Player.FormatMmdEquivalent, the "(N MMD)" figure on the /bank pyreal line.</summary>
    [TestClass]
    public class BankMmdEquivalentTests
    {
        [DataTestMethod]
        [DataRow(0L, "0")]
        [DataRow(24999L, "0")]
        [DataRow(25000L, "0.1")]
        [DataRow(250000L, "1")]
        [DataRow(374999L, "1.4")]
        [DataRow(375000L, "1.5")]
        [DataRow(2500000L, "10")]
        [DataRow(2625000000L, "10,500")]
        public void FormatMmdEquivalent_TruncatesToOneDecimal(long pyreals, string expected)
        {
            Assert.AreEqual(expected, Player.FormatMmdEquivalent(pyreals));
        }

        [TestMethod]
        public void FormatMmdEquivalent_UsesTheMmdValueConstant_NotAHardcodedRate()
        {
            Assert.AreEqual("1", Player.FormatMmdEquivalent(Player.MmdValue));
        }

        [TestMethod]
        public void FormatMmdEquivalent_NegativeBalanceShowsZero()
        {
            Assert.AreEqual("0", Player.FormatMmdEquivalent(-5));
        }
    }
}
