using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    [TestClass]
    public class FocusSpellDamageTests
    {
        [TestMethod]
        public void ZeroFocus_IsUnchanged() => Assert.AreEqual(1.0f, Player.GetFocusSpellDamageMod(0, 0.001), 1e-6f);

        [TestMethod]
        public void TypicalFocus_Is_PointOnePercentPerPoint() => Assert.AreEqual(1.3f, Player.GetFocusSpellDamageMod(300, 0.001), 1e-5f);

        [TestMethod]
        public void ZeroSetting_Disables() => Assert.AreEqual(1.0f, Player.GetFocusSpellDamageMod(300, 0.0), 1e-6f);

        [TestMethod]
        public void NegativeNaNOrInfinite_MeansNoBonus()
        {
            Assert.AreEqual(1.0f, Player.GetFocusSpellDamageMod(300, -0.001), 1e-6f);
            Assert.AreEqual(1.0f, Player.GetFocusSpellDamageMod(300, double.NaN), 1e-6f);
            Assert.AreEqual(1.0f, Player.GetFocusSpellDamageMod(300, double.PositiveInfinity), 1e-6f);
            Assert.AreEqual(1.0f, Player.GetFocusSpellDamageMod(300, double.NegativeInfinity), 1e-6f);
        }

        [TestMethod]
        public void OverflowingProduct_MeansNoBonus() => Assert.AreEqual(1.0f, Player.GetFocusSpellDamageMod(300, 1e37), 0f);

        [TestMethod]
        public void Gate_FullTruthTable()
        {
            for (var i = 0; i < 16; i++)
            {
                var direct = (i & 1) != 0;
                var targetPlayer = (i & 2) != 0;
                var proc = (i & 4) != 0;
                var caProc = (i & 8) != 0;

                var expected = direct && !targetPlayer && !proc && !caProc;

                Assert.AreEqual(expected, Player.QualifiesForFocusSpellDamage(direct, targetPlayer, proc, caProc), $"case {i}");
            }
        }

        [TestMethod]
        public void LargerSetting_Scales() => Assert.AreEqual(2.5f, Player.GetFocusSpellDamageMod(300, 0.005), 1e-5f);
    }
}