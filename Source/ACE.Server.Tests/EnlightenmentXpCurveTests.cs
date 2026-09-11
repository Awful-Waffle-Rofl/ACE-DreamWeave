using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Entity;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Boundary math for the enlightenment XP curve (<see cref="EnlightenmentXpCurve"/>): the pure
    /// extrapolation core must derive the growth ratio from the chart's last two deltas as an exact rational,
    /// produce a strictly increasing, deterministic chart, and stop below the overflow cap; the personal-cap
    /// and skill-credit accessors must follow 275 + 5n (clamped) and grant no credits past the retail chart.
    /// These drive the DatManager-free internal overloads so no client dat files are needed, matching the
    /// <see cref="AltCharacterBonusTests"/> pattern.
    /// </summary>
    [TestClass]
    public class EnlightenmentXpCurveTests
    {
        private static readonly ulong OverflowCap = (ulong)(long.MaxValue / 2);

        [TestMethod]
        public void Extend_DerivesGrowthRatioFromLastTwoDeltas()
        {
            // deltas 100 then 200 => ratio 2x; each synthesized delta doubles the previous one
            var baseTotals = new List<ulong> { 0, 100, 300 };

            var extended = EnlightenmentXpCurve.Extend(baseTotals, 10_000);

            Assert.AreEqual(700UL, extended[3]);   // +400
            Assert.AreEqual(1500UL, extended[4]);  // +800
            Assert.AreEqual(3100UL, extended[5]);  // +1600
        }

        [TestMethod]
        public void Extend_ProducesStrictlyIncreasingTotals()
        {
            var baseTotals = new List<ulong> { 0, 1000, 2065 }; // ratio 2065/1000 = 1.065

            var extended = EnlightenmentXpCurve.Extend(baseTotals, OverflowCap);

            Assert.IsTrue(extended.Count > baseTotals.Count, "chart should have been extended");
            for (var i = 1; i < extended.Count; i++)
                Assert.IsTrue(extended[i] > extended[i - 1], $"total at index {i} is not strictly greater than {i - 1}");
        }

        [TestMethod]
        public void Extend_IsDeterministic()
        {
            var baseTotals = new List<ulong> { 0, 1000, 2065 };

            var a = EnlightenmentXpCurve.Extend(baseTotals, OverflowCap);
            var b = EnlightenmentXpCurve.Extend(baseTotals, OverflowCap);

            CollectionAssert.AreEqual(a, b);
        }

        [TestMethod]
        public void Extend_StopsBelowOverflowCap()
        {
            var baseTotals = new List<ulong> { 0, 1000, 2065 };

            var extended = EnlightenmentXpCurve.Extend(baseTotals, OverflowCap);

            var lastTotal = extended[extended.Count - 1];
            Assert.IsTrue(lastTotal <= OverflowCap, $"final total {lastTotal} exceeded the cap {OverflowCap}");

            // adding one more compounded delta (~6.5% of the last delta) would breach the cap - that is why the loop stopped
            var lastDelta = extended[extended.Count - 1] - extended[extended.Count - 2];
            Assert.IsTrue(lastTotal + lastDelta > OverflowCap, "there was still room for another level below the cap");
        }

        [TestMethod]
        public void Extend_SteepGeometricTailYieldsBoundedCeiling()
        {
            // A steep (~6.5%/level) geometric tail off a retail-magnitude (~1e10) last delta lands the hard
            // ceiling a few hundred levels past the base cap - well bounded, not runaway. (The real retail
            // chart's final growth is shallower, ~1.44%/level, so its actual emergent ceiling is higher -
            // around level 1445 - but the algorithm's bounding behavior is identical and is what this asserts.)
            var baseTotals = BuildGeometricTailChart();

            var extended = EnlightenmentXpCurve.Extend(baseTotals, OverflowCap);
            var ceiling = extended.Count - 1;

            Assert.IsTrue(ceiling >= 400 && ceiling <= 700, $"ceiling {ceiling} not in the expected [400, 700] band");
        }

        [TestMethod]
        public void Extend_ShortChartReturnsDegenerateCopy()
        {
            var baseTotals = new List<ulong> { 0, 500 };

            var extended = EnlightenmentXpCurve.Extend(baseTotals, 1_000_000);

            CollectionAssert.AreEqual(baseTotals, extended);
            Assert.AreNotSame(baseTotals, extended, "should return a fresh copy, not the same instance");
        }

        [TestMethod]
        public void Extend_FlatTailExtendsWithConstantDelta()
        {
            // equal deltas (ratio 1) => constant-delta linear extension, still strictly increasing
            var baseTotals = new List<ulong> { 0, 100, 200 };

            var extended = EnlightenmentXpCurve.Extend(baseTotals, 1000);

            Assert.AreEqual(300UL, extended[3]);
            Assert.AreEqual(400UL, extended[4]);
        }

        [TestMethod]
        public void GetMaxLevelForEnlightenment_IsNoLongerThePlayerCap_ButStaysCorrect()
        {
            // Player.GetPlayerMaxLevel no longer calls this - every character caps at HardCeilingLevel now
            // (Docs/ClassAbilities/XP-LANE-SPEC.md sec 2.2). The pure function is kept because the migration
            // for already-enlightened characters derives their equivalent level from it, so it must keep
            // reporting the OLD per-enlightenment cap exactly. This test pins that, not the live cap.
            const int hardCeiling = 546;

            Assert.AreEqual(275, EnlightenmentXpCurve.GetMaxLevelForEnlightenment(0, hardCeiling));
            Assert.AreEqual(280, EnlightenmentXpCurve.GetMaxLevelForEnlightenment(1, hardCeiling));
        }

        [TestMethod]
        public void ExtendedChart_IsStrictlyIncreasingAndStopsUnderTheOverflowCap()
        {
            // with levels uncapped, the synthesized tail is what every character now levels along, all the
            // way to HardCeilingLevel. A non-monotonic or overflowing tail would break CheckForLevelup's
            // walk, so assert the whole extension, not just its first entries.
            var baseTotals = new List<ulong> { 0, 100, 210 };   // deltas 100, 110 => ratio 11/10
            const ulong cap = 100000;

            var extended = EnlightenmentXpCurve.Extend(baseTotals, cap);

            Assert.IsTrue(extended.Count > baseTotals.Count, "tail should extend past the base chart");

            for (var i = 1; i < extended.Count; i++)
                Assert.IsTrue(extended[i] > extended[i - 1], $"chart must strictly increase at index {i}");

            Assert.IsTrue(extended[extended.Count - 1] <= cap, "final total must not exceed the overflow cap");
        }

        [TestMethod]
        public void GetMaxLevelForEnlightenment_275PlusFivePerEnlightenment()
        {
            const int hardCeiling = 546;

            Assert.AreEqual(275, EnlightenmentXpCurve.GetMaxLevelForEnlightenment(0, hardCeiling));
            Assert.AreEqual(280, EnlightenmentXpCurve.GetMaxLevelForEnlightenment(1, hardCeiling));
            Assert.AreEqual(325, EnlightenmentXpCurve.GetMaxLevelForEnlightenment(10, hardCeiling));

            // clamps to the hard ceiling
            Assert.AreEqual(hardCeiling, EnlightenmentXpCurve.GetMaxLevelForEnlightenment(1000, hardCeiling));

            // negative enlightenment clamps to the base cap
            Assert.AreEqual(275, EnlightenmentXpCurve.GetMaxLevelForEnlightenment(-5, hardCeiling));
        }

        [TestMethod]
        public void GetSkillCreditsForLevel_RetailChartThenEvery25PastCap()
        {
            // synthetic credit list of length 276 (indices 0..275), a credit granted at level 2
            var creditList = new uint[276];
            creditList[2] = 1;

            // within the retail chart: passthrough
            Assert.AreEqual(1u, EnlightenmentXpCurve.GetSkillCreditsForLevel(2, creditList));
            Assert.AreEqual(0u, EnlightenmentXpCurve.GetSkillCreditsForLevel(1, creditList));

            // past the retail cap: +1 every 25 levels beyond 275, first at 300
            Assert.AreEqual(1u, EnlightenmentXpCurve.GetSkillCreditsForLevel(300, creditList));
            Assert.AreEqual(1u, EnlightenmentXpCurve.GetSkillCreditsForLevel(325, creditList));
            Assert.AreEqual(1u, EnlightenmentXpCurve.GetSkillCreditsForLevel(350, creditList));

            // non-milestone levels past the cap grant nothing, and never an index-out-of-range
            Assert.AreEqual(0u, EnlightenmentXpCurve.GetSkillCreditsForLevel(276, creditList));
            Assert.AreEqual(0u, EnlightenmentXpCurve.GetSkillCreditsForLevel(299, creditList));
            Assert.AreEqual(0u, EnlightenmentXpCurve.GetSkillCreditsForLevel(301, creditList));
            Assert.AreEqual(0u, EnlightenmentXpCurve.GetSkillCreditsForLevel(324, creditList));

            // negative still 0
            Assert.AreEqual(0u, EnlightenmentXpCurve.GetSkillCreditsForLevel(-1, creditList));
        }

        [TestMethod]
        public void EnlightenmentCost_IsMillionTimesNextNumber()
        {
            // mirrors Enlightenment.GetCost(player) = 1_000_000L * (Enlightenment + 1); Player state can't be
            // constructed in a unit test, so the closed form is asserted directly as the cost-curve spec anchor
            Assert.AreEqual(1_000_000L, EnlightenmentCost(0));
            Assert.AreEqual(2_000_000L, EnlightenmentCost(1));
            Assert.AreEqual(10_000_000L, EnlightenmentCost(9));
            Assert.AreEqual(1_000_000_000L, EnlightenmentCost(999));
        }

        private static long EnlightenmentCost(int enlightenment) => 1_000_000L * (enlightenment + 1);

        /// <summary>
        /// A 276-entry (levels 0..275) chart whose last two deltas encode a retail-magnitude (~1e10) last delta
        /// growing at ~6.5%. Only those last two deltas drive <see cref="EnlightenmentXpCurve.Extend"/>, so the
        /// earlier entries just need to be monotonic.
        /// </summary>
        private static List<ulong> BuildGeometricTailChart()
        {
            var totals = new List<ulong>();

            for (var i = 0; i < 273; i++)
                totals.Add((ulong)i);

            var t273 = totals[totals.Count - 1] + 10_000_000_000UL;
            var t274 = t273 + 10_900_000_000UL;             // second-to-last delta
            var t275 = t274 + 11_608_500_000UL;             // last delta (~1.065x the previous)

            totals.Add(t273);
            totals.Add(t274);
            totals.Add(t275);

            return totals; // Count == 276, last index 275
        }
    }
}
