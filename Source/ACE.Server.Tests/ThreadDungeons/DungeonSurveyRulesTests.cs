using System;
using System.Collections.Generic;

using ACE.Server.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// Covers DungeonSurveyRules.Record and Counts against a fake ISurveyLedger (PHASE-2-DESIGN.md
    /// section 4.1, rewritten for the daily reset). Nothing here touches a Player or PropertyManager (whose
    /// reads throw under the test harness).
    /// </summary>
    [TestClass]
    public class DungeonSurveyRulesTests
    {
        private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

        /// <summary>2026-09-17T18:00:00Z = 14:00 EDT, a fixed "now" for tests that do not care about DST edges.</summary>
        private static readonly uint FixedNow = ToUnix(2026, 9, 17, 18, 0, 0);

        private static uint ToUnix(int year, int month, int day, int hour, int minute, int second)
            => (uint)new DateTimeOffset(year, month, day, hour, minute, second, TimeSpan.Zero).ToUnixTimeSeconds();

        private static SurveyDayClock Clock(uint now, int resetHour = 0) => new SurveyDayClock(now, Eastern, resetHour);

        /// <summary>
        /// Fake ledger: LastTimeCompleted per quest (null = never stamped), and an ordered op log so tests
        /// can assert the exact call sequence Record makes.
        /// </summary>
        private sealed class FakeLedger : ISurveyLedger
        {
            public readonly Dictionary<string, uint?> Stamps = new Dictionary<string, uint?>();
            public readonly Dictionary<string, int> SolveCounts = new Dictionary<string, int>();
            public readonly List<string> Ops = new List<string>();

            public uint? LastCompleted(string quest)
            {
                Ops.Add($"LastCompleted({quest})");
                return Stamps.TryGetValue(quest, out var stamp) ? stamp : null;
            }

            public void Erase(string quest)
            {
                Ops.Add($"Erase({quest})");
                SolveCounts[quest] = 0;
            }

            public void Stamp(string quest)
            {
                Ops.Add($"Stamp({quest})");
                Stamps[quest] = null; // the fake does not need a real timestamp for Record's own writes
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
            var ledger = new FakeLedger();

            var result = DungeonSurveyRules.Record(ledger, Clock(FixedNow));

            CollectionAssert.AreEqual(
                new[]
                {
                    $"LastCompleted({DungeonSurveyRules.Window})",
                    $"Erase({DungeonSurveyRules.Count})",
                    $"Stamp({DungeonSurveyRules.Window})",
                    $"Increment({DungeonSurveyRules.Count})",
                    $"Increment({DungeonSurveyRules.Total})",
                },
                ledger.Ops);
            Assert.AreEqual(1, result);
        }

        [TestMethod]
        public void Clear_on_the_same_day_only_increments()
        {
            var ledger = new FakeLedger();
            ledger.Stamps[DungeonSurveyRules.Window] = FixedNow - 3600; // stamped 1h earlier, same day
            ledger.SolveCounts[DungeonSurveyRules.Count] = 3;

            var result = DungeonSurveyRules.Record(ledger, Clock(FixedNow));

            CollectionAssert.AreEqual(
                new[]
                {
                    $"LastCompleted({DungeonSurveyRules.Window})",
                    $"Increment({DungeonSurveyRules.Count})",
                    $"Increment({DungeonSurveyRules.Total})",
                },
                ledger.Ops);
            Assert.AreEqual(4, result);
        }

        [TestMethod]
        public void A_new_day_resets_the_count_but_not_the_total()
        {
            var ledger = new FakeLedger();
            ledger.Stamps[DungeonSurveyRules.Window] = FixedNow - 86400; // stamped yesterday
            ledger.SolveCounts[DungeonSurveyRules.Count] = 7;
            ledger.SolveCounts[DungeonSurveyRules.Total] = 7;

            DungeonSurveyRules.Record(ledger, Clock(FixedNow));

            Assert.AreEqual(1, ledger.SolveCounts[DungeonSurveyRules.Count]);
            Assert.AreEqual(8, ledger.SolveCounts[DungeonSurveyRules.Total]);
        }

        [TestMethod]
        public void A_stamp_from_the_future_reads_as_the_same_day_and_does_not_reset()
        {
            // Clock stepped back (or the two simply disagree): a future stamp must read as already-today,
            // never as free to re-pay. See SurveyDayClock.IsToday's doc comment.
            var ledger = new FakeLedger();
            ledger.Stamps[DungeonSurveyRules.Window] = FixedNow + 3600;
            ledger.SolveCounts[DungeonSurveyRules.Count] = 5;

            DungeonSurveyRules.Record(ledger, Clock(FixedNow));

            Assert.AreEqual(6, ledger.SolveCounts[DungeonSurveyRules.Count], "a future stamp must not be treated as a previous day");
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

        [TestMethod]
        public void Counts_with_a_zero_floor_admits_every_gem_level()
        {
            Assert.IsTrue(DungeonSurveyRules.Counts(175, 0));
            Assert.IsTrue(DungeonSurveyRules.Counts(180, 0));
            Assert.IsTrue(DungeonSurveyRules.Counts(1, 0));
            Assert.IsFalse(DungeonSurveyRules.Counts(175, 0, 0));
            Assert.IsTrue(DungeonSurveyRules.Counts(175, 0, 1));
        }
    }
}
