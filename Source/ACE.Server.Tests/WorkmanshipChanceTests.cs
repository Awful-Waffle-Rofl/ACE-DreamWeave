using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Factories.Entity;
using ACE.Server.Factories.Tables;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Locks the workmanship ladder, including the T7/T8 tables added 2026-08-08.
    ///
    /// Before that change WorkmanshipChance.Roll clamped tier to 1-6 behind a "todo: add t7 / t8",
    /// so every tier 7 and tier 8 drop silently rolled the tier 6 distribution. That was invisible
    /// in play - the values it produced were all legal - and it stayed in the tree for years. The
    /// distribution assertions below exist because that failure mode does not throw, does not log,
    /// and does not show up in any other test.
    ///
    /// ChanceTable.VerifyTable only LOGS when a table does not sum to 1.0 (ChanceTable.cs:24), so a
    /// mistyped probability would sail through a normal Roll(). TablesSumToOne reflects over the
    /// private table list to assert it directly instead.
    /// </summary>
    [TestClass]
    public class WorkmanshipChanceTests
    {
        // Large enough that every assertion below clears its threshold by 100+ standard errors.
        private const int Rolls = 100_000;

        private const int MaxTier = 8;

        /// <summary>
        /// The declared tables, read straight off the static field the roller actually indexes.
        /// </summary>
        private static List<ChanceTable<int>> GetTables()
        {
            var field = typeof(WorkmanshipChance).GetField("workmanshipChances", BindingFlags.NonPublic | BindingFlags.Static);

            Assert.IsNotNull(field, "WorkmanshipChance.workmanshipChances not found - was the field renamed?");

            var tables = field.GetValue(null) as List<ChanceTable<int>>;

            Assert.IsNotNull(tables, "workmanshipChances was not a List<ChanceTable<int>>");

            return tables;
        }

        private static Dictionary<int, int> RollMany(int tier)
        {
            var counts = new Dictionary<int, int>();

            for (var i = 0; i < Rolls; i++)
            {
                var result = WorkmanshipChance.Roll(tier);

                counts.TryGetValue(result, out var prev);
                counts[result] = prev + 1;
            }

            return counts;
        }

        private static double Mean(Dictionary<int, int> counts) =>
            counts.Sum(kvp => (double)kvp.Key * kvp.Value) / counts.Values.Sum();

        private static double Probability(Dictionary<int, int> counts, int workmanship)
        {
            counts.TryGetValue(workmanship, out var hits);

            return (double)hits / counts.Values.Sum();
        }

        [TestMethod]
        public void EveryTierHasItsOwnTable()
        {
            Assert.AreEqual(MaxTier, GetTables().Count, "expected one workmanship table per tier 1-8");
        }

        [TestMethod]
        public void TablesSumToOne()
        {
            var tables = GetTables();

            for (var i = 0; i < tables.Count; i++)
            {
                // decimal, because summing floats accumulates error the assertion would then chase
                var total = tables[i].Sum(entry => (decimal)entry.chance);

                Assert.AreEqual(1.0m, total, 0.0000001m, $"T{i + 1} chances sum to {total}, expected 1.0");
            }
        }

        [TestMethod]
        public void DeclaredWorkmanshipStaysInRange()
        {
            var tables = GetTables();

            foreach (var entry in tables.SelectMany(table => table))
                Assert.IsTrue(entry.result >= 1 && entry.result <= 10, $"declared workmanship {entry.result} outside 1-10");
        }

        [TestMethod]
        public void RollRespectsTheOneToTenRange()
        {
            for (var tier = 1; tier <= MaxTier; tier++)
            {
                foreach (var result in RollMany(tier).Keys)
                    Assert.IsTrue(result >= 1 && result <= 10, $"tier {tier} rolled workmanship {result}, outside 1-10");
            }
        }

        [TestMethod]
        public void RollMatchesTheDeclaredDistribution()
        {
            var tables = GetTables();

            for (var tier = 1; tier <= MaxTier; tier++)
            {
                var counts = RollMany(tier);

                foreach (var entry in tables[tier - 1])
                {
                    var actual = Probability(counts, entry.result);

                    Assert.AreEqual((double)entry.chance, actual, 0.01, $"T{tier} workmanship {entry.result}: declared {entry.chance}, rolled {actual:F4}");
                }
            }
        }

        /// <summary>
        /// The regression that matters. If someone re-introduces a clamp at 6, T7 and T8 both collapse
        /// onto the T6 distribution and these two assertions are the only thing that notices.
        /// </summary>
        [TestMethod]
        public void HigherTiersRollHigherWorkmanship()
        {
            var t6 = Mean(RollMany(6));
            var t7 = Mean(RollMany(7));
            var t8 = Mean(RollMany(8));

            Assert.IsTrue(t7 > t6 + 0.25, $"T7 mean {t7:F3} is not meaningfully above T6 {t6:F3} - is tier still clamped to 6?");
            Assert.IsTrue(t8 > t7 + 0.25, $"T8 mean {t8:F3} is not meaningfully above T7 {t7:F3}");
        }

        [TestMethod]
        public void TopTiersImproveTheOddsOfWorkmanshipTen()
        {
            // Equipment and weapon mods scale linearly by workmanship / 10, so players only mod a 10.
            // Holding P(10) flat from T6 upward is what made the endgame hunt for a moddable piece
            // tier-independent; this asserts the easing is actually present.
            var t6 = Probability(RollMany(6), 10);
            var t7 = Probability(RollMany(7), 10);
            var t8 = Probability(RollMany(8), 10);

            Assert.IsTrue(t7 > t6, $"T7 P(wm 10) {t7:F4} did not improve on T6 {t6:F4}");
            Assert.IsTrue(t8 > t7, $"T8 P(wm 10) {t8:F4} did not improve on T7 {t7:F4}");
            Assert.IsTrue(t8 > 2.0 * t6, $"T8 P(wm 10) {t8:F4} is not the intended multiple of T6 {t6:F4}");
        }

        [TestMethod]
        public void TierAboveTheLadderClampsInsteadOfThrowing()
        {
            // Roll is reached from treasure profile data; an out-of-range tier must not crash loot gen.
            for (var tier = MaxTier + 1; tier <= MaxTier + 3; tier++)
            {
                var result = WorkmanshipChance.Roll(tier);

                Assert.IsTrue(result >= 1 && result <= 10, $"clamped tier {tier} rolled {result}");
            }

            for (var tier = 0; tier >= -3; tier--)
            {
                var result = WorkmanshipChance.Roll(tier);

                Assert.IsTrue(result >= 1 && result <= 10, $"clamped tier {tier} rolled {result}");
            }
        }
    }
}
