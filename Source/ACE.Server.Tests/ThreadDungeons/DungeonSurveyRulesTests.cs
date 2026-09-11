using System.Collections.Generic;

using ACE.Server.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// Covers DungeonSurveyRules.Record and Counts against a fake ISurveyLedger (PHASE-2-DESIGN.md
    /// section 4.1). Nothing here touches a Player or PropertyManager (whose reads throw under the test
    /// harness).
    /// </summary>
    [TestClass]
    public class DungeonSurveyRulesTests
    {
        /// <summary>
        /// Fake ledger: a dictionary of solves per quest, a CanSolve(Window) flag the test controls, and an
        /// ordered op log so tests can assert the exact call sequence Record makes.
        /// </summary>
        private sealed class FakeLedger : ISurveyLedger
        {
            public bool CanSolveWindow;
            public readonly Dictionary<string, int> SolveCounts = new Dictionary<string, int>();
            public readonly List<string> Ops = new List<string>();

            public readonly HashSet<string> Stamps = new HashSet<string>();

            public bool CanSolve(string quest)
            {
                Ops.Add($"CanSolve({quest})");
                return quest == DungeonSurveyRules.Window && CanSolveWindow;
            }

            public bool Has(string quest) => Stamps.Contains(quest);

            public void Erase(string quest)
            {
                Ops.Add($"Erase({quest})");
                SolveCounts[quest] = 0;
            }

            public void Stamp(string quest)
            {
                Ops.Add($"Stamp({quest})");
                Stamps.Add(quest);
            }

            public void Increment(string quest)
            {
                Ops.Add($"Increment({quest})");
                SolveCounts.TryGetValue(quest, out var current);
                SolveCounts[quest] = current + 1;
            }

            public int Solves(string quest)
            {
                SolveCounts.TryGetValue(quest, out var current);
                return current;
            }
        }

        [TestMethod]
        public void First_clear_opens_a_window_and_counts_one()
        {
            var ledger = new FakeLedger { CanSolveWindow = true };

            var result = DungeonSurveyRules.Record(ledger);

            CollectionAssert.AreEqual(
                new[]
                {
                    $"CanSolve({DungeonSurveyRules.Window})",
                    $"Erase({DungeonSurveyRules.Count})",
                    $"Stamp({DungeonSurveyRules.Window})",
                    $"Increment({DungeonSurveyRules.Count})",
                    $"Increment({DungeonSurveyRules.Total})",
                },
                ledger.Ops);
            Assert.AreEqual(1, result);
        }

        [TestMethod]
        public void Clear_inside_the_window_only_increments()
        {
            var ledger = new FakeLedger { CanSolveWindow = false };
            ledger.SolveCounts[DungeonSurveyRules.Count] = 3;

            var result = DungeonSurveyRules.Record(ledger);

            CollectionAssert.AreEqual(
                new[]
                {
                    $"CanSolve({DungeonSurveyRules.Window})",
                    $"Increment({DungeonSurveyRules.Count})",
                    $"Increment({DungeonSurveyRules.Total})",
                },
                ledger.Ops);
            Assert.AreEqual(4, result);
        }

        [TestMethod]
        public void Window_expiry_resets_the_count_but_not_the_total()
        {
            var ledger = new FakeLedger { CanSolveWindow = true };
            ledger.SolveCounts[DungeonSurveyRules.Count] = 7;
            ledger.SolveCounts[DungeonSurveyRules.Total] = 7;

            DungeonSurveyRules.Record(ledger);

            Assert.AreEqual(1, ledger.SolveCounts[DungeonSurveyRules.Count]);
            Assert.AreEqual(8, ledger.SolveCounts[DungeonSurveyRules.Total]);
        }

        [TestMethod]
        public void Counts_requires_the_gem_level_floor()
        {
            Assert.IsTrue(DungeonSurveyRules.Counts(185, 185));
            Assert.IsFalse(DungeonSurveyRules.Counts(184, 185));
            Assert.IsTrue(DungeonSurveyRules.Counts(275, 185));
        }

        /// <summary>Ruling P2-R22: a run that spawned nothing must not count, whatever the gem level is.</summary>
        [TestMethod]
        public void Counts_requires_something_spawned()
        {
            Assert.IsFalse(DungeonSurveyRules.Counts(185, 185, 0));
            Assert.IsTrue(DungeonSurveyRules.Counts(185, 185, 1));
        }
    }
}
