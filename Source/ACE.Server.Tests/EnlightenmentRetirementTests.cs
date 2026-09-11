using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Entity;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Tests for the enlightenment retirement credit's pure math (Docs/ClassAbilities/XP-LANE-SPEC.md sec 5.2).
    ///
    /// Everything here runs against an INJECTED chart rather than the real one, because the shipped chart is
    /// built from the client dat files and <c>EnlightenmentXpCurve.ExtendedTotals</c> throws under test. The
    /// synthetic chart below is deliberately simple enough to verify the sums by hand.
    /// </summary>
    [TestClass]
    public class EnlightenmentRetirementTests
    {
        /// <summary>
        /// A chart where the total for level L is L * 100, long enough to cover several enlightenment caps
        /// (275, 280, 285, ...). Hand-checkable: the cost of enlightenment cycle k is (275 + 5k) * 100.
        /// </summary>
        private static IReadOnlyList<ulong> LinearChart(int levels = 1200)
        {
            var totals = new List<ulong>(levels + 1);
            for (var i = 0; i <= levels; i++)
                totals.Add((ulong)i * 100);
            return totals;
        }

        [TestMethod]
        public void Unenlightened_CostsNothingAndCreditsNothing()
        {
            var chart = LinearChart();

            Assert.AreEqual(0UL, EnlightenmentRetirement.CumulativeXpSpent(0, chart));
            Assert.AreEqual(0, EnlightenmentRetirement.EquivalentLevel(0, chart));

            // negative is treated as zero, not as an error
            Assert.AreEqual(0UL, EnlightenmentRetirement.CumulativeXpSpent(-3, chart));
            Assert.AreEqual(0, EnlightenmentRetirement.EquivalentLevel(-3, chart));
        }

        [TestMethod]
        public void FirstEnlightenmentCostsTheBaseCapClimb()
        {
            var chart = LinearChart();

            // one cycle = the full climb to level 275
            Assert.AreEqual(275UL * 100, EnlightenmentRetirement.CumulativeXpSpent(1, chart));
            Assert.AreEqual(275, EnlightenmentRetirement.EquivalentLevel(1, chart));
        }

        [TestMethod]
        public void EachCycleCostsItsOwnRaisedCap()
        {
            var chart = LinearChart();

            // cycles are 275, 280, 285 - each the FULL climb, because the total was zeroed every time
            Assert.AreEqual((275UL + 280) * 100, EnlightenmentRetirement.CumulativeXpSpent(2, chart));
            Assert.AreEqual((275UL + 280 + 285) * 100, EnlightenmentRetirement.CumulativeXpSpent(3, chart));

            // on a linear chart the equivalent level is just the summed levels
            Assert.AreEqual(555, EnlightenmentRetirement.EquivalentLevel(2, chart));
            Assert.AreEqual(840, EnlightenmentRetirement.EquivalentLevel(3, chart));
        }

        [TestMethod]
        public void EquivalentLevelRoundsUpToTheLevelThatCoversTheSpend()
        {
            // A ONE-cycle spend can never demonstrate rounding: it is totals[275] read from the same chart,
            // so it always lands exactly on a level. Two cycles on a chart whose step CHANGES is what puts
            // the sum between two levels.
            var totals = new List<ulong>();
            for (var i = 0; i <= 300; i++)
                totals.Add((ulong)i * 100);              // 275 -> 27,500   280 -> 28,000
            for (var i = 301; i <= 400; i++)
                totals.Add(30_000 + (ulong)(i - 300) * 1000);   // coarser steps past 300

            // two cycles cost 27,500 + 28,000 = 55,500
            Assert.AreEqual(55_500UL, EnlightenmentRetirement.CumulativeXpSpent(2, totals));

            // 55,500 sits between level 325 (55,000) and level 326 (56,000) - it must round UP to the
            // level that actually covers the spend, never down to one that does not.
            Assert.AreEqual(55_000UL, totals[325]);
            Assert.AreEqual(56_000UL, totals[326]);
            Assert.AreEqual(326, EnlightenmentRetirement.EquivalentLevel(2, totals));
        }

        [TestMethod]
        public void SaturatesInsteadOfWrappingOnAnAbsurdCount()
        {
            var chart = LinearChart();
            var ceiling = chart[chart.Count - 1];

            // enough cycles to blow past the chart entirely
            var spent = EnlightenmentRetirement.CumulativeXpSpent(100000, chart);

            Assert.AreEqual(ceiling, spent, "must saturate at the chart ceiling rather than overflow");
            Assert.AreEqual(chart.Count - 1, EnlightenmentRetirement.EquivalentLevel(100000, chart));
        }

        [TestMethod]
        public void EmptyOrNullChartIsHandled()
        {
            Assert.AreEqual(0UL, EnlightenmentRetirement.CumulativeXpSpent(5, null));
            Assert.AreEqual(0, EnlightenmentRetirement.EquivalentLevel(5, null));
            Assert.AreEqual(0UL, EnlightenmentRetirement.CumulativeXpSpent(5, new List<ulong>()));
            Assert.AreEqual(0, EnlightenmentRetirement.EquivalentLevel(5, new List<ulong>()));
        }

        /// <summary>
        /// The credit only ever RAISES a character (Player.GrantEnlightenmentRetirementCredit compares before
        /// assigning), so the computed target must grow monotonically with the enlightenment count - otherwise
        /// a higher count could credit a lower level than a lower one.
        /// </summary>
        [TestMethod]
        public void EquivalentLevelIsMonotonicInEnlightenment()
        {
            // must be long enough that 60 cycles do NOT saturate it - cumulative(60) is 2,535,000 on this
            // chart, and a 1200-level chart tops out at 120,000, which is why a shorter one shows equal
            // levels rather than increasing ones.
            var chart = LinearChart(30000);
            var previous = 0;

            for (var enl = 1; enl <= 60; enl++)
            {
                var level = EnlightenmentRetirement.EquivalentLevel(enl, chart);
                Assert.IsTrue(level > previous, $"ENL {enl} credited level {level}, not above ENL {enl - 1}'s {previous}");
                previous = level;
            }
        }
    }
}
