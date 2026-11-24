using System.Collections.Generic;

using ACE.Server.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// Character Details shows no offense or defense rows when the baseline is null, so the shipped roster must yield a
    /// baseline at every level a player can be, and a higher level must never be easier to hit than a lower one.
    /// Covers MELEE and MISSILE defense only: the unit-test GameTables carries formulas for just those two skills
    /// (TestGameTables.cs:30-31), so magic defense and every attack skill read 0 here by construction. The Character
    /// Details sample test covers them with stand-in formulas.
    /// </summary>
    [TestClass]
    public class ThreadBaselineRosterCoverageTests
    {
        private static readonly int[] Levels = { 1, 5, 10, 25, 50, 100, 150, 200, 275, 300, 375, 500, 1000, 5000 };

        /// <summary>From here up the stat curve takes over and defense keeps climbing with no level clamp.</summary>
        private const int StrictFromLevel = 375;

        [TestInitialize]
        public void Setup()
        {
            TestGameTables.EnsureInitialized();
            ThreadPlanCache.Invalidate();
        }

        [TestCleanup]
        public void Cleanup() => ThreadPlanCache.Invalidate();

        [TestMethod]
        public void ShippedRoster_YieldsABaselineAtEveryLevel_AndMeleeDefenseOnlyClimbs()
        {
            var limits = ThreadBaselineTests.Limits();
            uint previous = 0;
            var nulls = new List<int>();
            var zeros = new List<int>();
            var notIncreasing = new List<string>();
            var decreasing = new List<string>();

            foreach (var level in Levels)
            {
                var baseline = ThreadBaseline.For(level, limits, ShippedRosterFixture.Tables, ShippedRosterFixture.LevelOf, ThreadBaselineTests.Profile);

                if (baseline == null)
                {
                    nulls.Add(level);
                    continue;
                }

                if (baseline.MeleeDefense == 0)
                    zeros.Add(level);

                if (baseline.MeleeDefense < previous)
                    decreasing.Add($"{level}: {baseline.MeleeDefense} < {previous}");

                if (level >= StrictFromLevel && baseline.MeleeDefense <= previous)
                    notIncreasing.Add($"{level}: {baseline.MeleeDefense} <= {previous}");

                previous = baseline.MeleeDefense;
            }

            Assert.AreEqual(0, nulls.Count, "levels with no baseline: " + string.Join(", ", nulls));
            Assert.AreEqual(0, zeros.Count, "levels with zero melee defense: " + string.Join(", ", zeros));
            Assert.AreEqual(0, decreasing.Count, "melee defense decreased at: " + string.Join("; ", decreasing));
            Assert.AreEqual(0, notIncreasing.Count, "no level clamp: melee defense must strictly increase from level " + StrictFromLevel + ", but did not at: " + string.Join("; ", notIncreasing));
        }
    }
}
