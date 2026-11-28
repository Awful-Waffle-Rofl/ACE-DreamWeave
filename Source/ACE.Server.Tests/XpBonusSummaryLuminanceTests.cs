using System;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The /xpbonus summary must show Luminance the way EarnLuminance + GrantLuminance apply it on a kill:
    /// (1 + gear + aug) x luminance_modifier x (1 + offline). It used to claim Luminance got only the offline bonus.
    /// </summary>
    [TestClass]
    public class XpBonusSummaryLuminanceTests
    {
        private static string Summary() => PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_BonusSummary.cs");

        [TestMethod]
        public void KillLuminanceFactor_IncludesGearAndAug_ThenModifier_ThenOffline()
        {
            // gear +6%, aug 2 pt (+10%), luminance_modifier 1.0, offline +50%: 1.16 x 1.0 x 1.5 = 1.74
            Assert.AreEqual(1.74, Player.ComputeKillLuminanceFactor(0.06, 0.10, 1.0, 0.5), 1e-9);

            // the old text's value (offline only) would be 1.5; gear + offline must not collapse to it
            Assert.AreNotEqual(1.5, Player.ComputeKillLuminanceFactor(0.06, 0.0, 1.0, 0.5), 1e-9);

            // luminance_modifier scales it; xp_modifier is not an input
            Assert.AreEqual(2.0 * 1.06, Player.ComputeKillLuminanceFactor(0.06, 0.0, 2.0, 0.0), 1e-9);
            Assert.AreEqual(1.0, Player.ComputeKillLuminanceFactor(0.0, 0.0, 1.0, 0.0), 1e-9);
        }

        [TestMethod]
        public void Summary_NoLongerClaimsLuminanceIsOfflineOnly_AndFeedsGearAugAndModifier()
        {
            var text = Summary();
            Assert.IsFalse(text.Contains("Luminance receives only the offline bonus", StringComparison.Ordinal));
            Assert.IsFalse(text.Contains("var lumTotal = 1.0 + offline;", StringComparison.Ordinal));
            Assert.IsTrue(text.Contains("ComputeKillLuminanceFactor(gear, aug, lumModifier, offline)", StringComparison.Ordinal));
            Assert.IsTrue(text.Contains("GetDouble(\"luminance_modifier\")", StringComparison.Ordinal));
        }

        [TestMethod]
        public void Summary_ShowsBothThreadBlessings_AndFoldsThemIntoTheTotals()
        {
            var text = Summary();
            Assert.IsTrue(text.Contains("ThreadBoonKind.Xp", StringComparison.Ordinal));
            Assert.IsTrue(text.Contains("ThreadBoonKind.Luminance", StringComparison.Ordinal));
            Assert.IsTrue(text.Contains("(1.0 + alt) * xpBoon;", StringComparison.Ordinal));
            Assert.IsTrue(text.Contains("offline) * lumBoon;", StringComparison.Ordinal));
            Assert.IsTrue(text.Contains("Thread blessing, XP", StringComparison.Ordinal));
            Assert.IsTrue(text.Contains("Thread blessing, Luminance", StringComparison.Ordinal));
            Assert.IsTrue(text.Contains("(solo kill; fellowship splits apply first)", StringComparison.Ordinal));
        }

        [TestMethod]
        public void GetXPAndLuminanceModifier_StillGatesAugOnKill_AndSumsAdditively()
        {
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_Xp.cs"),
                "public float GetXPAndLuminanceModifier(XpType xpType)");
            Assert.IsTrue(body.Contains("xpType == XpType.Kill && AugmentationBonusXp > 0", StringComparison.Ordinal));
            Assert.IsTrue(body.Contains("1.0f + enchantmentBonus + augBonus", StringComparison.Ordinal));
        }

        [TestMethod]
        public void GrantLuminance_StillAppliesTheLuminanceThreadBlessing()
        {
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_Luminance.cs"),
                "public void GrantLuminance(long amount, XpType xpType, ShareType shareType, bool combatShare)");
            Assert.IsTrue(body.Contains("ThreadBoonKind.Luminance", StringComparison.Ordinal));
        }

        [TestMethod]
        public void EarnLuminance_StillAppliesTheGearAugModifier_TheSummaryDescribes()
        {
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_Luminance.cs"),
                "public void EarnLuminance(long amount, XpType xpType, ShareType shareType = ShareType.All)");
            Assert.IsTrue(body.Contains("GetXPAndLuminanceModifier(xpType)", StringComparison.Ordinal));
            Assert.IsTrue(body.Contains("amount * enchantment * modifier", StringComparison.Ordinal));
            Assert.IsTrue(body.Contains("\"luminance_modifier\"", StringComparison.Ordinal));
        }
    }
}
