using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// Covers DungeonSurveyRules.Plan, the pure rewrite of the Survey-Archivist's deleted 19-set Use emote
    /// cascade, against the daily-reset ledger shape (owner design 2026-09-17). The first five tests are the
    /// five hand-written paper traces the weenie's header carried, turned one-for-one into assertions with
    /// explicit timestamps in place of the old stamped/cooldown booleans: those traces WERE the specification
    /// of the shipped behaviour, and preserving it exactly is the point of the rewrite.
    ///
    /// Pure - no Player, no PropertyManager, no emote engine. The cross-day / cross-tier boundary cases live
    /// in SurveyDayTests, next to the day-index arithmetic they exercise.
    /// </summary>
    [TestClass]
    public class DungeonSurveyTurnInTests
    {
        private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

        /// <summary>2026-09-17T18:00:00Z = 14:00 EDT. Fixed "now" for every trace in this file.</summary>
        private static readonly uint Now = ToUnix(2026, 9, 17, 18, 0, 0);

        private static uint ToUnix(int year, int month, int day, int hour, int minute, int second)
            => (uint)new DateTimeOffset(year, month, day, hour, minute, second, TimeSpan.Zero).ToUnixTimeSeconds();

        private static SurveyDayClock Clock() => new SurveyDayClock(Now, Eastern, 0);

        /// <summary>
        /// Ledger fake shaped for Plan: LastTimeCompleted per quest (null = never stamped). Mirrors
        /// QuestManagerSurveyLedger's own shape rather than modelling anything about QuestManager itself.
        /// </summary>
        private sealed class PlanLedger : ISurveyLedger
        {
            public int Count;

            public readonly Dictionary<string, uint?> Stamps = new Dictionary<string, uint?>();

            public readonly List<string> Writes = new List<string>();

            public uint? LastCompleted(string quest) => Stamps.TryGetValue(quest, out var stamp) ? stamp : null;

            public int Solves(string quest) => quest == DungeonSurveyRules.Count ? Count : 0;

            public void Erase(string quest) => Writes.Add($"Erase({quest})");

            public void Stamp(string quest) => Writes.Add($"Stamp({quest})");

            public void Increment(string quest) => Writes.Add($"Increment({quest})");
        }

        /// <summary>
        /// A window stamped 1h before Now (same survey day) and, for each tier in <paramref name="paidTiersToday"/>,
        /// that tier's row stamped 30 minutes before Now (also today).
        /// </summary>
        private static PlanLedger Ledger(int count, params int[] paidTiersToday)
        {
            var ledger = new PlanLedger { Count = count };
            ledger.Stamps[DungeonSurveyRules.Window] = Now - 3600;

            foreach (var tier in paidTiersToday)
                ledger.Stamps[DungeonSurveyRules.SurveyQuest(tier)] = Now - 1800;

            return ledger;
        }

        // ---------------- the five paper traces from the deleted weenie header ----------------

        /// <summary>Trace 1: "count 3, nothing paid" -> BAND_LT5 + tier 1 paid.</summary>
        [TestMethod]
        public void Trace_count_three_nothing_paid_pays_tier_one_in_the_under_five_band()
        {
            var plan = DungeonSurveyRules.Plan(Ledger(3), Clock());

            Assert.IsTrue(plan.WindowLive);
            Assert.AreEqual(3, plan.Count);
            Assert.AreEqual(SurveyBand.Lt5, plan.Band);
            CollectionAssert.AreEqual(new[] { 1 }, plan.TiersToPay.ToArray());
            Assert.IsFalse(plan.Held);
        }

        /// <summary>Trace 2: "count 6, tier 1 already paid" -> BAND_5 + tier 5 paid; tier 1 NOT re-paid.</summary>
        [TestMethod]
        public void Trace_count_six_with_tier_one_paid_pays_only_tier_five()
        {
            var plan = DungeonSurveyRules.Plan(Ledger(6, 1), Clock());

            Assert.AreEqual(SurveyBand.Five, plan.Band);
            CollectionAssert.AreEqual(new[] { 5 }, plan.TiersToPay.ToArray());
            Assert.IsFalse(plan.Held);
        }

        /// <summary>Trace 3: "count 12, nothing paid" -> BAND_10 + all three tiers paid, highest first.</summary>
        [TestMethod]
        public void Trace_count_twelve_nothing_paid_pays_all_three_tiers_highest_first()
        {
            var plan = DungeonSurveyRules.Plan(Ledger(12), Clock());

            Assert.AreEqual(SurveyBand.Ten, plan.Band);
            CollectionAssert.AreEqual(new[] { 10, 5, 1 }, plan.TiersToPay.ToArray());
            Assert.IsFalse(plan.Held);
        }

        /// <summary>Trace 4: "count 12, everything already paid" -> BAND_10 + HELD; nothing paid.</summary>
        [TestMethod]
        public void Trace_count_twelve_all_paid_is_held()
        {
            var plan = DungeonSurveyRules.Plan(Ledger(12, 1, 5, 10), Clock());

            Assert.AreEqual(SurveyBand.Ten, plan.Band);
            Assert.AreEqual(0, plan.TiersToPay.Count);
            Assert.IsTrue(plan.Held);
        }

        /// <summary>
        /// Trace 5: "count 10, all paid, window from a previous day" -> the stale count is never read, so no
        /// tier is re-paid even though it still reads 10. This is the branch the whole window gate exists
        /// for: DynDungeonSurveyCount is erased only when Record opens a NEW survey day, never merely by the
        /// day turning over on its own - Plan itself never erases anything.
        /// </summary>
        [TestMethod]
        public void Trace_a_window_from_a_previous_day_reads_no_count_at_all()
        {
            var ledger = new PlanLedger { Count = 10 };
            ledger.Stamps[DungeonSurveyRules.Window] = Now - 86400;
            ledger.Stamps[DungeonSurveyRules.SurveyQuest(1)] = Now - 86400;
            ledger.Stamps[DungeonSurveyRules.SurveyQuest(5)] = Now - 86400;
            ledger.Stamps[DungeonSurveyRules.SurveyQuest(10)] = Now - 86400;

            var plan = DungeonSurveyRules.Plan(ledger, Clock());

            Assert.IsFalse(plan.WindowLive);
            Assert.AreEqual(0, plan.Count, "a window from a previous day must not report the stale count");
            Assert.AreEqual(SurveyBand.None, plan.Band);
            Assert.AreEqual(0, plan.TiersToPay.Count);
            Assert.IsFalse(plan.Held, "a dead window is not 'held', it is 'nothing filed'");
        }

        // ---------------- the rest of the cascade ----------------

        [TestMethod]
        public void A_window_that_was_never_stamped_pays_nothing()
        {
            var ledger = Ledger(4);
            ledger.Stamps.Remove(DungeonSurveyRules.Window);

            var plan = DungeonSurveyRules.Plan(ledger, Clock());

            Assert.IsFalse(plan.WindowLive);
            Assert.AreEqual(SurveyBand.None, plan.Band);
            Assert.IsFalse(plan.Held);
        }

        [TestMethod]
        public void A_live_window_with_nothing_filed_pays_nothing_and_is_not_held()
        {
            var plan = DungeonSurveyRules.Plan(Ledger(0), Clock());

            Assert.IsTrue(plan.WindowLive);
            Assert.AreEqual(SurveyBand.None, plan.Band);
            Assert.AreEqual(0, plan.TiersToPay.Count);
            Assert.IsFalse(plan.Held);
        }

        [TestMethod]
        public void Exact_tier_boundaries_open_their_band()
        {
            Assert.AreEqual(SurveyBand.Lt5, DungeonSurveyRules.Plan(Ledger(1), Clock()).Band);
            Assert.AreEqual(SurveyBand.Lt5, DungeonSurveyRules.Plan(Ledger(4), Clock()).Band);
            Assert.AreEqual(SurveyBand.Five, DungeonSurveyRules.Plan(Ledger(5), Clock()).Band);
            Assert.AreEqual(SurveyBand.Five, DungeonSurveyRules.Plan(Ledger(9), Clock()).Band);
            Assert.AreEqual(SurveyBand.Ten, DungeonSurveyRules.Plan(Ledger(10), Clock()).Band);
        }

        [TestMethod]
        public void A_middle_tier_paid_alone_still_pays_the_ones_around_it()
        {
            var plan = DungeonSurveyRules.Plan(Ledger(11, 5), Clock());

            CollectionAssert.AreEqual(new[] { 10, 1 }, plan.TiersToPay.ToArray());
            Assert.IsFalse(plan.Held);
        }

        [TestMethod]
        public void A_tier_stamped_on_a_previous_day_counts_as_unpaid()
        {
            // A stamp from a previous survey day is present (Has would say true) but not "today", so its
            // tier reads as unpaid - the daily-reset replacement for the old aged-out-cooldown case.
            var ledger = Ledger(7);
            ledger.Stamps[DungeonSurveyRules.SurveyQuest(5)] = Now - 90000; // ~25h ago: yesterday
            ledger.Stamps[DungeonSurveyRules.SurveyQuest(1)] = Now - 90000;

            var plan = DungeonSurveyRules.Plan(ledger, Clock());

            CollectionAssert.AreEqual(new[] { 5, 1 }, plan.TiersToPay.ToArray());
        }

        [TestMethod]
        public void Held_is_only_ever_true_inside_a_band()
        {
            // Held means "you reached a band and every tier under it is already paid". It must never fire on
            // the nothing-filed branch, which speaks a different line entirely.
            Assert.IsTrue(DungeonSurveyRules.Plan(Ledger(5, 1, 5), Clock()).Held);
            Assert.IsFalse(DungeonSurveyRules.Plan(Ledger(0), Clock()).Held);
        }

        [TestMethod]
        public void Plan_writes_nothing_to_the_ledger()
        {
            // The latch is the station's job and has to happen in one synchronous pass there; a planner that
            // stamped would make the same reward payable twice from two different code paths.
            var ledger = Ledger(12, 5);

            DungeonSurveyRules.Plan(ledger, Clock());

            Assert.AreEqual(0, ledger.Writes.Count, string.Join(", ", ledger.Writes));
        }

        [TestMethod]
        public void Tiers_to_pay_only_ever_names_tiers_that_exist_in_the_reward_table()
        {
            var plan = DungeonSurveyRules.Plan(Ledger(100), Clock());

            foreach (var tier in plan.TiersToPay)
                Assert.IsTrue(SurveyRewardMath.TryGetTier(tier, out _), $"tier {tier} is not in SurveyRewardMath.Tiers");
        }
    }
}
