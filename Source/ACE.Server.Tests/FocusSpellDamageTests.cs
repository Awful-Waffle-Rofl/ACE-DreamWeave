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

        [TestMethod]
        public void FocusLine_ProdSetting_ExactText() =>
            Assert.AreEqual("Your buffed Focus of 152 increases your spell damage against monsters by 22.8%.", Player.FormatFocusSpellDamageLine(152, 0.0015));

        [TestMethod]
        public void FocusLine_DefaultSetting_ExactText() =>
            Assert.AreEqual("Your buffed Focus of 300 increases your spell damage against monsters by 30%.", Player.FormatFocusSpellDamageLine(300, 0.001));

        [TestMethod]
        public void FocusLine_PinsTwoDecimalFormat() =>
            Assert.AreEqual("Your buffed Focus of 153 increases your spell damage against monsters by 22.95%.", Player.FormatFocusSpellDamageLine(153, 0.0015));

        [TestMethod]
        public void FocusLine_DisabledOrZeroFocus_IsNull()
        {
            Assert.IsNull(Player.FormatFocusSpellDamageLine(300, 0.0));
            Assert.IsNull(Player.FormatFocusSpellDamageLine(300, -0.001));
            Assert.IsNull(Player.FormatFocusSpellDamageLine(300, double.NaN));
            Assert.IsNull(Player.FormatFocusSpellDamageLine(300, double.PositiveInfinity));
            Assert.IsNull(Player.FormatFocusSpellDamageLine(0, 0.001));
        }
    }
}
