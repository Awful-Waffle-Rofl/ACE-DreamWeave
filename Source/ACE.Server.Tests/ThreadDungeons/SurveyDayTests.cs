using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// Covers SurveyDay's pure day-boundary arithmetic and SurveyDayClock, plus the cross-tier / cross-day
    /// boundary behaviour of DungeonSurveyRules that arithmetic exists to drive (owner design 2026-09-17: one
    /// server-wide daily reset, replacing four independent 20 h cooldowns).
    ///
    /// America/New_York is used throughout since it is the shipped default (dynamic_dungeons_survey_reset_timezone),
    /// so the DST-edge tests are exercising the real default zone rather than a synthetic one.
    /// </summary>
    [TestClass]
    public class SurveyDayTests
    {
        private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

        private static uint ToUnix(int year, int month, int day, int hour, int minute, int second)
            => (uint)new DateTimeOffset(year, month, day, hour, minute, second, TimeSpan.Zero).ToUnixTimeSeconds();

        // ---------------- (a) EDT day boundary ----------------

        [TestMethod]
        public void EDT_boundary_at_0400Z_splits_the_day()
        {
            var before = ToUnix(2026, 9, 18, 3, 59, 59);
            var at = ToUnix(2026, 9, 18, 4, 0, 0);

            Assert.AreNotEqual(
                SurveyDay.DayIndex(before, Eastern, 0),
                SurveyDay.DayIndex(at, Eastern, 0),
                "03:59:59Z and 04:00:00Z must fall on different survey days in EDT");
        }

        [TestMethod]
        public void EDT_instants_either_side_of_midnight_share_a_day()
        {
            var early = ToUnix(2026, 9, 18, 3, 0, 0);
            var late = ToUnix(2026, 9, 18, 3, 59, 59);

            Assert.AreEqual(
                SurveyDay.DayIndex(early, Eastern, 0),
                SurveyDay.DayIndex(late, Eastern, 0),
                "03:00:00Z and 03:59:59Z must fall on the same survey day in EDT");
        }

        // ---------------- (b) EST day boundary ----------------

        [TestMethod]
        public void EST_boundary_at_0500Z_splits_the_day()
        {
            var before = ToUnix(2026, 12, 1, 4, 59, 59);
            var at = ToUnix(2026, 12, 1, 5, 0, 0);

            Assert.AreNotEqual(
                SurveyDay.DayIndex(before, Eastern, 0),
                SurveyDay.DayIndex(at, Eastern, 0),
                "04:59:59Z and 05:00:00Z must fall on different survey days in EST");
        }

        // ---------------- (c) NextReset across the fall-back transition ----------------

        [TestMethod]
        public void NextReset_before_the_fallback_transition_lands_at_0400Z()
        {
            var now = ToUnix(2026, 10, 31, 16, 0, 0); // 12:00 EDT, before the Nov 1 fallback

            var next = SurveyDay.NextReset(now, Eastern, 0);

            Assert.AreEqual(ToUnix(2026, 11, 1, 4, 0, 0), next);
        }

        [TestMethod]
        public void NextReset_after_the_fallback_transition_lands_at_0500Z()
        {
            var now = ToUnix(2026, 11, 1, 17, 0, 0); // 12:00 EST, after the Nov 1 fallback

            var next = SurveyDay.NextReset(now, Eastern, 0);

            Assert.AreEqual(ToUnix(2026, 11, 2, 5, 0, 0), next);
        }

        /// <summary>
        /// March 8, 2026 is the spring-forward date (2nd Sunday of March): local clocks jump from 01:59:59
        /// EST straight to 03:00:00 EDT, so 02:00-02:59 local does not exist that day. resetHour=2 is used
        /// deliberately (not the shipped default resetHour=0) because a boundary at 02:00 local is the only
        /// way to land the candidate reset inside the gap and exercise the IsInvalidTime step-forward loop
        /// in SurveyDay.cs. "now" is 01:00 EST (06:00Z), before the gap; the candidate boundary of 02:00
        /// local is invalid, so it steps forward to 03:00 EDT, the first valid instant after the gap, which
        /// converts back to 07:00Z (03:00 EDT = UTC-4).
        /// </summary>
        [TestMethod]
        public void NextReset_across_the_springforward_gap_lands_at_0700Z()
        {
            var now = ToUnix(2026, 3, 8, 6, 0, 0); // 01:00 EST, before the Mar 8 springforward gap

            var next = SurveyDay.NextReset(now, Eastern, 2);

            Assert.AreEqual(ToUnix(2026, 3, 8, 7, 0, 0), next);
        }

        // ---------------- (d)/(e) the key cross-tier case ----------------

        /// <summary>Minimal ISurveyLedger for the Plan-level boundary tests below.</summary>
        private sealed class DayLedger : ISurveyLedger
        {
            public int Count;
            public readonly Dictionary<string, uint?> Stamps = new Dictionary<string, uint?>();

            public uint? LastCompleted(string quest) => Stamps.TryGetValue(quest, out var stamp) ? stamp : null;
            public int Solves(string quest) => quest == DungeonSurveyRules.Count ? Count : 0;
            public void Erase(string quest) { }
            public void Stamp(string quest) { }
            public void Increment(string quest) { }
        }

        /// <summary>
        /// Tier 1 paid 2026-09-17T14:00Z, tier 5 paid 2026-09-17T20:00Z - two different times, the whole
        /// point being that a per-tier 20 h cooldown would come off cooldown at two different moments. Under
        /// the shared daily reset both instead turn over together at the SAME boundary: past
        /// 2026-09-18T04:00:01Z (just after the shared EDT midnight), with a fresh window and count 5, both
        /// pay again.
        /// </summary>
        [TestMethod]
        public void Tiers_paid_at_different_times_yesterday_both_repay_together_after_the_shared_reset()
        {
            var tier1Paid = ToUnix(2026, 9, 17, 14, 0, 0);
            var tier5Paid = ToUnix(2026, 9, 17, 20, 0, 0);
            var now = ToUnix(2026, 9, 18, 4, 0, 1);

            var ledger = new DayLedger { Count = 5 };
            ledger.Stamps[DungeonSurveyRules.Window] = now; // a fresh window, reopened by this same visit's Record
            ledger.Stamps[DungeonSurveyRules.SurveyQuest(1)] = tier1Paid;
            ledger.Stamps[DungeonSurveyRules.SurveyQuest(5)] = tier5Paid;

            var plan = DungeonSurveyRules.Plan(ledger, new SurveyDayClock(now, Eastern, 0));

            CollectionAssert.AreEqual(new[] { 5, 1 }, plan.TiersToPay.ToArray());
            Assert.IsFalse(plan.Held);
        }

        /// <summary>The same two tiers, one minute before the shared reset: still held, not yet re-payable.</summary>
        [TestMethod]
        public void Tiers_paid_at_different_times_yesterday_are_still_held_one_minute_before_the_shared_reset()
        {
            var tier1Paid = ToUnix(2026, 9, 17, 14, 0, 0);
            var tier5Paid = ToUnix(2026, 9, 17, 20, 0, 0);
            var now = ToUnix(2026, 9, 18, 3, 59, 0);

            var ledger = new DayLedger { Count = 5 };
            ledger.Stamps[DungeonSurveyRules.Window] = tier1Paid;
            ledger.Stamps[DungeonSurveyRules.SurveyQuest(1)] = tier1Paid;
            ledger.Stamps[DungeonSurveyRules.SurveyQuest(5)] = tier5Paid;

            var plan = DungeonSurveyRules.Plan(ledger, new SurveyDayClock(now, Eastern, 0));

            Assert.IsTrue(plan.WindowLive);
            Assert.AreEqual(0, plan.TiersToPay.Count);
            Assert.IsTrue(plan.Held);
        }

        // ---------------- (f) a stamp from the future reads as today ----------------

        [TestMethod]
        public void IsToday_reads_a_future_stamp_as_today()
        {
            var now = ToUnix(2026, 9, 17, 18, 0, 0);
            var future = ToUnix(2026, 9, 18, 18, 0, 0); // clock stepped back, or a future stamp for any reason

            var clock = new SurveyDayClock(now, Eastern, 0);

            Assert.IsTrue(clock.IsToday(future), "a stamp from the future must never read as payable again");
        }

        // ---------------- (g) Record at 23:59 local then 00:01 local erases the count ----------------

        private sealed class RecordLedger : ISurveyLedger
        {
            public readonly Dictionary<string, uint?> Stamps = new Dictionary<string, uint?>();
            public readonly Dictionary<string, int> SolveCounts = new Dictionary<string, int>();

            public uint? LastCompleted(string quest) => Stamps.TryGetValue(quest, out var stamp) ? stamp : null;

            public void Erase(string quest) => SolveCounts[quest] = 0;

            public void Stamp(string quest) => Stamps[quest] = lastStampedAt;

            public void Increment(string quest)
            {
                SolveCounts.TryGetValue(quest, out var current);
                SolveCounts[quest] = current + 1;
            }

            public int Solves(string quest)
            {
                SolveCounts.TryGetValue(quest, out var current);
                return current;
            }

            /// <summary>Test seam: the instant Stamp should record, set by the test before each Record call.</summary>
            public uint lastStampedAt;
        }

        [TestMethod]
        public void Record_at_2359_local_then_0001_local_erases_the_count_with_no_carry_over()
        {
            // 2026-09-17 23:59 EDT = 2026-09-18T03:59:00Z; 2026-09-18 00:01 EDT = 2026-09-18T04:01:00Z.
            var lateTonight = ToUnix(2026, 9, 18, 3, 59, 0);
            var justAfterMidnight = ToUnix(2026, 9, 18, 4, 1, 0);

            var ledger = new RecordLedger();

            ledger.lastStampedAt = lateTonight;
            DungeonSurveyRules.Record(ledger, new SurveyDayClock(lateTonight, Eastern, 0));
            DungeonSurveyRules.Record(ledger, new SurveyDayClock(lateTonight, Eastern, 0));
            Assert.AreEqual(2, ledger.SolveCounts[DungeonSurveyRules.Count], "two surveys the same local day must both count");

            ledger.lastStampedAt = justAfterMidnight;
            DungeonSurveyRules.Record(ledger, new SurveyDayClock(justAfterMidnight, Eastern, 0));

            Assert.AreEqual(1, ledger.SolveCounts[DungeonSurveyRules.Count], "a survey filed after the local reset must not carry the previous day's count");
        }

        // ---------------- (h) duration formatter ----------------

        [TestMethod]
        public void FormatDuration_renders_hours_and_minutes()
        {
            Assert.AreEqual("3h 12m", SurveyDay.FormatDuration(TimeSpan.FromMinutes(192)));
            Assert.AreEqual("1h 0m", SurveyDay.FormatDuration(TimeSpan.FromHours(1)));
        }

        [TestMethod]
        public void FormatDuration_under_an_hour_omits_the_hour_part()
        {
            Assert.AreEqual("12m", SurveyDay.FormatDuration(TimeSpan.FromMinutes(12)));
        }

        [TestMethod]
        public void FormatDuration_floors_at_one_minute()
        {
            Assert.AreEqual("1m", SurveyDay.FormatDuration(TimeSpan.FromSeconds(5)));
            Assert.AreEqual("1m", SurveyDay.FormatDuration(TimeSpan.Zero));
            Assert.AreEqual("1m", SurveyDay.FormatDuration(TimeSpan.FromSeconds(-30)));
        }

        [TestMethod]
        public void FormatDuration_rounds_seconds_up_to_the_next_minute()
        {
            // 90 seconds must not truncate down to "1m"; the player must never be told to come back before
            // the boundary has actually passed.
            Assert.AreEqual("2m", SurveyDay.FormatDuration(TimeSpan.FromSeconds(90)));
        }

        // ---------------- (i) a non-zero reset hour shifts the boundary ----------------

        [TestMethod]
        public void A_reset_hour_of_six_shifts_the_day_boundary()
        {
            // With resetHour 6, the survey day runs 06:00-to-06:00 local. 06:00 EDT = 10:00Z.
            var justBefore = ToUnix(2026, 9, 18, 9, 59, 59);
            var justAfter = ToUnix(2026, 9, 18, 10, 0, 0);

            Assert.AreNotEqual(
                SurveyDay.DayIndex(justBefore, Eastern, 6),
                SurveyDay.DayIndex(justAfter, Eastern, 6));

            // The same instants read as the SAME day at resetHour 0 (the 04:00Z EDT boundary already passed).
            Assert.AreEqual(
                SurveyDay.DayIndex(justBefore, Eastern, 0),
                SurveyDay.DayIndex(justAfter, Eastern, 0));
        }

        [TestMethod]
        public void NextReset_with_a_reset_hour_of_six_lands_at_the_shifted_boundary()
        {
            var now = ToUnix(2026, 9, 18, 12, 0, 0); // 08:00 EDT, already past this day's 06:00 boundary

            var next = SurveyDay.NextReset(now, Eastern, 6);

            Assert.AreEqual(ToUnix(2026, 9, 19, 10, 0, 0), next); // tomorrow 06:00 EDT = 10:00Z
        }
    }
}
