using System.Linq;

using ACE.Server.WorldEvents;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>The pure value parsers of the Hold settings rows (WorldEventHoldRules.Settings.cs). No PropertyManager, no world.</summary>
    [TestClass]
    public class WorldEventHoldRulesSettingsTests
    {
        // ---- knight_max_health_by_wcid -------------------------------------------------------------------------------

        [TestMethod]
        public void KnightHealthByWcid_Valid_ParsesEveryPair()
        {
            Assert.IsTrue(WorldEventHoldRules.TryParseKnightHealthByWcid("1007650=12000, 1007652 = 8000", out var map, out var error), error);
            Assert.AreEqual(2, map.Count);
            Assert.AreEqual(12000, map[1007650]);
            Assert.AreEqual(8000, map[1007652]);

            Assert.IsTrue(WorldEventHoldRules.TryParseKnightHealthByWcid("1007651=1000", out map, out _), "the lower bound is allowed");
            Assert.IsTrue(WorldEventHoldRules.TryParseKnightHealthByWcid("1007651=100000", out map, out _), "the upper bound is allowed");
        }

        [TestMethod]
        public void KnightHealthByWcid_Check_StoresCanonicalTextSortedByWcid()
        {
            var (normalized, error) = WorldEventHoldRules.CheckKnightHealthByWcid(" 1007652=8000 ,1007650 = 12000");

            Assert.IsNull(error);
            Assert.AreEqual("1007650=12000,1007652=8000", normalized);
        }

        [DataTestMethod]
        [DataRow("1007650", "not wcid=health")]
        [DataRow("=5000", "not wcid=health")]
        [DataRow("1007650=", "not wcid=health")]
        [DataRow("abc=5000", "not a wcid")]
        [DataRow("0=5000", "not a wcid")]
        [DataRow("-5=5000", "not a wcid")]
        [DataRow("1007650=abc", "not a whole-number health")]
        [DataRow("1007650=12.5", "not a whole-number health")]
        [DataRow("1007650=-3000", "not a whole-number health")]
        [DataRow("4294967296=5000", "out of range")]
        [DataRow("1007650=99999999999999999999", "outside 1000..100000")]
        [DataRow("1007650=999", "outside 1000..100000")]
        [DataRow("1007650=100001", "outside 1000..100000")]
        [DataRow("1007650=12000,,1007651=3000", "empty entry")]
        [DataRow("1007650=12000,1007650=9000", "listed twice")]
        [DataRow("1007650=12000,1007651", "not wcid=health")]
        [DataRow("", "give wcid=health")]
        [DataRow("   ", "give wcid=health")]
        public void KnightHealthByWcid_Malformed_IsRefusedWithAReason(string raw, string reasonFragment)
        {
            Assert.IsFalse(WorldEventHoldRules.TryParseKnightHealthByWcid(raw, out var map, out var error), raw);
            Assert.IsNull(map);
            StringAssert.Contains(error, reasonFragment);

            var (normalized, checkError) = WorldEventHoldRules.CheckKnightHealthByWcid(raw);
            Assert.IsNull(normalized);
            Assert.AreEqual(error, checkError);
        }

        // ---- milestone_percents --------------------------------------------------------------------------------------

        [TestMethod]
        public void MilestonePercents_Valid_IsDeduplicatedAndDescending()
        {
            Assert.IsTrue(WorldEventHoldRules.TryParseMilestonePercents("40, 80,80,60,1,99", out var p, out var error), error);
            CollectionAssert.AreEqual(new[] { 99, 80, 60, 40, 1 }, p.ToArray());

            Assert.AreEqual("80,60,40", WorldEventHoldRules.CheckMilestonePercents("60,80,40,80").Normalized);
            Assert.AreEqual("50", WorldEventHoldRules.CheckMilestonePercents("50").Normalized);
        }

        [TestMethod]
        public void MilestonePercents_None_IsAnEmptyList()
        {
            Assert.IsTrue(WorldEventHoldRules.TryParseMilestonePercents("NONE", out var p, out _));
            Assert.AreEqual(0, p.Count);
            Assert.AreEqual("none", WorldEventHoldRules.CheckMilestonePercents(" None ").Normalized);
        }

        [DataTestMethod]
        [DataRow("0", "1 to 99")]
        [DataRow("100", "1 to 99")]
        [DataRow("-5", "1 to 99")]
        [DataRow("50%", "1 to 99")]
        [DataRow("7.5", "1 to 99")]
        [DataRow("abc", "1 to 99")]
        [DataRow("75;50", "1 to 99")]
        [DataRow("75 50", "1 to 99")]
        [DataRow("75,,50", "empty entry")]
        [DataRow("75,", "empty entry")]
        [DataRow("75,0,25", "1 to 99")]
        [DataRow("", "give percents")]
        public void MilestonePercents_Malformed_IsRefusedWithAReason(string raw, string reasonFragment)
        {
            Assert.IsFalse(WorldEventHoldRules.TryParseMilestonePercents(raw, out var p, out var error), raw);
            Assert.IsNull(p);
            StringAssert.Contains(error, reasonFragment);
            Assert.IsNull(WorldEventHoldRules.CheckMilestonePercents(raw).Normalized);
        }

        // ---- corrupted_tint ------------------------------------------------------------------------------------------

        [TestMethod]
        public void CorruptedTint_Valid_ParsesAndStoresCanonicalHex()
        {
            Assert.IsTrue(WorldEventHoldRules.TryParseCorruptedTint("0x04001381", out var palette, out var error), error);
            Assert.AreEqual(0x04001381u, palette);

            Assert.IsTrue(WorldEventHoldRules.TryParseCorruptedTint("67113857", out palette, out _), "decimal is the same palette");
            Assert.AreEqual(0x04001381u, palette);

            Assert.AreEqual("0x0400137D", WorldEventHoldRules.CheckCorruptedTint("0x0400137d").Normalized);
            Assert.AreEqual("0x04001381", WorldEventHoldRules.CheckCorruptedTint("67113857").Normalized);
            Assert.AreEqual("0x04000000", WorldEventHoldRules.CheckCorruptedTint("0x04000000").Normalized, "the range's first id is a palette");
            Assert.AreEqual("0x0400FFFF", WorldEventHoldRules.CheckCorruptedTint("0x0400FFFF").Normalized, "the range's last id is a palette");
        }

        [TestMethod]
        public void CorruptedTint_NoneAndZero_MeanNoTint()
        {
            foreach (var text in new[] { "none", "NONE", "0" })
            {
                Assert.IsTrue(WorldEventHoldRules.TryParseCorruptedTint(text, out var palette, out _), text);
                Assert.AreEqual(0u, palette, text);
                Assert.AreEqual("none", WorldEventHoldRules.CheckCorruptedTint(text).Normalized, text);
            }
        }

        [DataTestMethod]
        [DataRow("red")]
        [DataRow("0x03FFFFFF")]
        [DataRow("0x04010000")]
        [DataRow("0x1")]
        [DataRow("1234")]
        [DataRow("-5")]
        [DataRow("0x04001381,0x0400137D")]
        public void CorruptedTint_Malformed_IsRefused(string raw)
        {
            Assert.IsFalse(WorldEventHoldRules.TryParseCorruptedTint(raw, out _, out var error), raw);
            StringAssert.Contains(error, "not a palette id");
            Assert.IsNull(WorldEventHoldRules.CheckCorruptedTint(raw).Normalized);
        }

        // ---- min_survivors aliases ---------------------------------------------------------------------------------------

        [TestMethod]
        public void MinSurvivors_AllAndAny_ResolveToTheirNumbers()
        {
            Assert.AreEqual("1", WorldEventHoldRules.ResolveMinSurvivorsAlias("all"));
            Assert.AreEqual("1", WorldEventHoldRules.ResolveMinSurvivorsAlias(" ALL "));
            Assert.AreEqual("3", WorldEventHoldRules.ResolveMinSurvivorsAlias("any"));
            Assert.AreEqual("3", WorldEventHoldRules.ResolveMinSurvivorsAlias("Any"));

            Assert.AreEqual("1", WorldEventHoldRules.CheckMinSurvivors("all").Normalized);
            Assert.AreEqual("3", WorldEventHoldRules.CheckMinSurvivors("any").Normalized);
        }

        [TestMethod]
        public void MinSurvivors_NonAliases_PassThroughForTheOrdinaryRangeCheck()
        {
            Assert.IsNull(WorldEventHoldRules.ResolveMinSurvivorsAlias("2"));
            Assert.IsNull(WorldEventHoldRules.ResolveMinSurvivorsAlias("some"));
            Assert.IsNull(WorldEventHoldRules.ResolveMinSurvivorsAlias(null));

            Assert.AreEqual(("2", (string)null), WorldEventHoldRules.CheckMinSurvivors("2"));
            Assert.AreEqual("some", WorldEventHoldRules.CheckMinSurvivors("some").Normalized, "not an alias, so the Long check refuses it");
        }
    }
}
