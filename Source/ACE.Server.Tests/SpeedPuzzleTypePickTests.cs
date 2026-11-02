using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.SpeedCourses;

namespace ACE.Server.Tests
{
    /// <summary>
    /// SpeedCourseRules.PickPuzzleTypesPerSite: one type per site from that site's own allowed set, no type repeated
    /// in a run, backtracking when an early choice would starve a later site.
    /// </summary>
    [TestClass]
    public class SpeedPuzzleTypePickTests
    {
        private static readonly string[] All = { "Sigil", "Beam", "Odd", "Shuffle" };
        private static readonly string[] Corridor = { "Beam", "Shuffle" };

        private static IReadOnlyList<IReadOnlyList<string>> Sites(params string[][] sets) => sets.Select(s => (IReadOnlyList<string>)s).ToList();

        [TestMethod]
        public void BlackBookPlan_EverySeed_GivesThreeDistinctTypes_P2FromBeamOrShuffle()
        {
            var seen = new HashSet<string>();

            for (var seed = 0; seed < 500; seed++)
            {
                var picks = SpeedCourseRules.PickPuzzleTypesPerSite(new Random(seed), Sites(All, Corridor, All));

                Assert.IsNotNull(picks, $"seed {seed}");
                Assert.AreEqual(3, picks.Count);
                Assert.AreEqual(3, picks.Distinct().Count(), $"seed {seed}: a repeated type");
                CollectionAssert.Contains(Corridor, picks[1], $"seed {seed}: P2 drew {picks[1]}");
                CollectionAssert.Contains(All, picks[0]);
                CollectionAssert.Contains(All, picks[2]);

                seen.Add(string.Join(",", picks));
            }

            Assert.IsTrue(seen.Count > 5, $"the draw varies by seed ({seen.Count} distinct plans)");
        }

        [TestMethod]
        public void Backtracks_WhenAGreedyFirstChoiceWouldStarveALaterSite()
        {
            // Site 1 offers Beam or Sigil, site 2 only Beam. Whenever the shuffle puts Beam first at site 1, a greedy
            // pick would leave site 2 with nothing; the search must back up and give site 1 Sigil, on every seed.
            for (var seed = 0; seed < 100; seed++)
            {
                var picks = SpeedCourseRules.PickPuzzleTypesPerSite(new Random(seed), Sites(new[] { "Beam", "Sigil" }, new[] { "Beam" }));

                CollectionAssert.AreEqual(new[] { "Sigil", "Beam" }, picks, $"seed {seed}");
            }
        }

        [TestMethod]
        public void Infeasible_ReturnsNull()
        {
            Assert.IsNull(SpeedCourseRules.PickPuzzleTypesPerSite(new Random(1), Sites(Corridor, Corridor, Corridor)), "three corridor sites share only two types");
            Assert.IsNull(SpeedCourseRules.PickPuzzleTypesPerSite(new Random(1), Sites(All, new string[0])), "a site with nothing allowed");
        }

        [TestMethod]
        public void DuplicatesAndBlanks_InASiteSet_AreIgnored()
        {
            var picks = SpeedCourseRules.PickPuzzleTypesPerSite(new Random(3), Sites(new[] { "Beam", "Beam", " ", null }, new[] { "Shuffle" }));

            CollectionAssert.AreEqual(new[] { "Beam", "Shuffle" }, picks);
        }

        [TestMethod]
        public void SameSeed_SamePlan()
        {
            var a = SpeedCourseRules.PickPuzzleTypesPerSite(new Random(42), Sites(All, Corridor, All));
            var b = SpeedCourseRules.PickPuzzleTypesPerSite(new Random(42), Sites(All, Corridor, All));

            CollectionAssert.AreEqual(a, b);
        }

        [TestMethod]
        public void EmptySiteList_IsAnEmptyPlan_NullIsNull()
        {
            Assert.AreEqual(0, SpeedCourseRules.PickPuzzleTypesPerSite(new Random(1), Sites()).Count);
            Assert.IsNull(SpeedCourseRules.PickPuzzleTypesPerSite(new Random(1), null));
            Assert.ThrowsExactly<ArgumentNullException>(() => SpeedCourseRules.PickPuzzleTypesPerSite(null, Sites(All)));
        }

        // ---- the runner lifecycle rule ----

        [TestMethod]
        public void DecideRunnerEnd_CoversEveryWayARunStops()
        {
            Assert.AreEqual("runner offline", SpeedCourseRules.DecideRunnerEnd(false, false, true, 5, 120));
            Assert.IsNull(SpeedCourseRules.DecideRunnerEnd(true, true, false, 999, 120), "bound keeps it");
            Assert.AreEqual("run finished or forfeited", SpeedCourseRules.DecideRunnerEnd(true, false, true, 5, 120));
            Assert.IsNull(SpeedCourseRules.DecideRunnerEnd(true, false, false, 119, 120), "in transit");
            Assert.AreEqual("runner never armed in this instance", SpeedCourseRules.DecideRunnerEnd(true, false, false, 121, 120));
        }
    }
}
