using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Entity;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The salvage forge's pure pricing rule (ACE.Server/Entity/SalvageForge.cs). Exercises the explicit-
    /// threshold overload of BagsRequired rather than the PropertyManager-backed one, because PropertyManager
    /// needs a live shard config table this project cannot provide - the same reason SalvageToolTests avoids
    /// touching TryConsume. The explicit overload is exactly what the PropertyManager-backed one calls after
    /// reading the three tunables, so this covers the real band logic byte for byte.
    /// </summary>
    [TestClass]
    public class SalvageForgeTests
    {
        // The shipped defaults - salvage_forge_skill_for_9/_8/_7.
        private const long SkillFor9 = 500;
        private const long SkillFor8 = 700;
        private const long SkillFor7 = 900;

        /// <summary>
        /// Every named edge from the spec, at the shipped default thresholds: skill &lt; 500 costs 10 bags;
        /// [500, 700) costs 9; [700, 900) costs 8; skill &gt;= 900 costs 7. The bands are half-open on the low
        /// end, so the threshold value itself always belongs to the CHEAPER band.
        /// </summary>
        [DataTestMethod]
        [DataRow(0, 10)]
        [DataRow(499, 10)]
        [DataRow(500, 9)]
        [DataRow(699, 9)]
        [DataRow(700, 8)]
        [DataRow(899, 8)]
        [DataRow(900, 7)]
        [DataRow(1200, 7)]
        public void BagsRequired_MatchesTheSkillBand(int skill, int expected)
        {
            Assert.AreEqual(expected, SalvageForge.BagsRequired(skill, SkillFor9, SkillFor8, SkillFor7));
        }

        /// <summary>
        /// The thresholds are read live, not baked in - the same band logic against a different set of
        /// tunables lands on different bag counts, which is what makes them tunables at all.
        /// </summary>
        [TestMethod]
        public void BagsRequired_HonoursCustomThresholds()
        {
            Assert.AreEqual(10, SalvageForge.BagsRequired(199, 200, 400, 600));
            Assert.AreEqual(9, SalvageForge.BagsRequired(200, 200, 400, 600));
            Assert.AreEqual(9, SalvageForge.BagsRequired(399, 200, 400, 600));
            Assert.AreEqual(8, SalvageForge.BagsRequired(400, 200, 400, 600));
            Assert.AreEqual(8, SalvageForge.BagsRequired(599, 200, 400, 600));
            Assert.AreEqual(7, SalvageForge.BagsRequired(600, 200, 400, 600));
        }
    }
}
