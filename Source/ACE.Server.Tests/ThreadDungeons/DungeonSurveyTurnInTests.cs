using System.Collections.Generic;
using System.Linq;

using ACE.Server.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// Covers DungeonSurveyRules.Plan, the pure rewrite of the Survey-Archivist's deleted 19-set Use emote
    /// cascade. The first five tests are the five hand-written paper traces the weenie's header carried,
    /// turned one-for-one into assertions: those traces WERE the specification of the shipped behaviour, and
    /// preserving it exactly is the point of the rewrite.
    ///
    /// Pure - no Player, no PropertyManager, no emote engine.
    /// </summary>
    [TestClass]
    public class DungeonSurveyTurnInTests
    {
        /// <summary>
        /// Ledger fake shaped for Plan: a window that is stamped/expired independently, a solve count, and a
        /// set of tier latches that are either unpaid or paid-and-on-cooldown. Mirrors the emote rig's
        /// InqQuest success test (HasQuest AND NOT CanSolve) rather than modelling quest cooldown times.
        /// </summary>
        private sealed class PlanLedger : ISurveyLedger
        {
            public bool WindowStamped = true;
            public bool WindowExpired;
            public int Count;

            /// <summary>Tier quests stamped and still on cooldown, i.e. already paid this window.</summary>
            public readonly HashSet<string> Paid = new HashSet<string>();

            /// <summary>Tier quests stamped but whose stamp has aged out - present but NOT paid.</summary>
            public readonly HashSet<string> Stale = new HashSet<string>();

            public readonly List<string> Writes = new List<string>();

            public bool Has(string quest)
            {
                if (quest == DungeonSurveyRules.Window) return WindowStamped;
                return Paid.Contains(quest) || Stale.Contains(quest);
            }

            public bool CanSolve(string quest)
            {
                if (quest == DungeonSurveyRules.Window) return WindowExpired || !WindowStamped;
                return !Paid.Contains(quest);
            }

            public int Solves(string quest) => quest == DungeonSurveyRules.Count ? Count : 0;

            public void Erase(string quest) => Writes.Add($"Erase({quest})");

            public void Stamp(string quest) => Writes.Add($"Stamp({quest})");

            public void Increment(string quest) => Writes.Add($"Increment({quest})");
        }

        private static PlanLedger Ledger(int count, params int[] paidTiers)
        {
            var ledger = new PlanLedger { Count = count };

            foreach (var tier in paidTiers)
                ledger.Paid.Add(DungeonSurveyRules.SurveyQuest(tier));

            return ledger;
        }

        // ---------------- the five paper traces from the deleted weenie header ----------------

        /// <summary>Trace 1: "count 3, nothing paid" -> BAND_LT5 + tier 1 paid.</summary>
        [TestMethod]
        public void Trace_count_three_nothing_paid_pays_tier_one_in_the_under_five_band()
        {
            var plan = DungeonSurveyRules.Plan(Ledger(3));

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
            var plan = DungeonSurveyRules.Plan(Ledger(6, 1));

            Assert.AreEqual(SurveyBand.Five, plan.Band);
            CollectionAssert.AreEqual(new[] { 5 }, plan.TiersToPay.ToArray());
            Assert.IsFalse(plan.Held);
        }

        /// <summary>Trace 3: "count 12, nothing paid" -> BAND_10 + all three tiers paid, highest first.</summary>
        [TestMethod]
        public void Trace_count_twelve_nothing_paid_pays_all_three_tiers_highest_first()
        {
            var plan = DungeonSurveyRules.Plan(Ledger(12));

            Assert.AreEqual(SurveyBand.Ten, plan.Band);
            CollectionAssert.AreEqual(new[] { 10, 5, 1 }, plan.TiersToPay.ToArray());
            Assert.IsFalse(plan.Held);
        }

        /// <summary>Trace 4: "count 12, everything already paid" -> BAND_10 + HELD; nothing paid.</summary>
        [TestMethod]
        public void Trace_count_twelve_all_paid_is_held()
        {
            var plan = DungeonSurveyRules.Plan(Ledger(12, 1, 5, 10));

            Assert.AreEqual(SurveyBand.Ten, plan.Band);
            Assert.AreEqual(0, plan.TiersToPay.Count);
            Assert.IsTrue(plan.Held);
        }

        /// <summary>
        /// Trace 5: "count 10, all paid, window expired" -> the stale count is never read, so no tier is
        /// re-paid even though it still reads 10. This is the branch the whole window gate exists for:
        /// DynDungeonSurveyCount is erased only when a NEW window opens, never on expiry.
        /// </summary>
        [TestMethod]
        public void Trace_an_expired_window_reads_no_count_at_all()
        {
            var ledger = Ledger(10, 1, 5, 10);
            ledger.WindowExpired = true;

            var plan = DungeonSurveyRules.Plan(ledger);

            Assert.IsFalse(plan.WindowLive);
            Assert.AreEqual(0, plan.Count, "an expired window must not report the stale count");
            Assert.AreEqual(SurveyBand.None, plan.Band);
            Assert.AreEqual(0, plan.TiersToPay.Count);
            Assert.IsFalse(plan.Held, "an expired window is not 'held', it is 'nothing filed'");
        }

        // ---------------- the rest of the cascade ----------------

        [TestMethod]
        public void A_window_that_was_never_stamped_pays_nothing()
        {
            var ledger = Ledger(4);
            ledger.WindowStamped = false;

            var plan = DungeonSurveyRules.Plan(ledger);

            Assert.IsFalse(plan.WindowLive);
            Assert.AreEqual(SurveyBand.None, plan.Band);
            Assert.IsFalse(plan.Held);
        }

        [TestMethod]
        public void A_live_window_with_nothing_filed_pays_nothing_and_is_not_held()
        {
            var plan = DungeonSurveyRules.Plan(Ledger(0));

            Assert.IsTrue(plan.WindowLive);
            Assert.AreEqual(SurveyBand.None, plan.Band);
            Assert.AreEqual(0, plan.TiersToPay.Count);
            Assert.IsFalse(plan.Held);
        }

        [TestMethod]
        public void Exact_tier_boundaries_open_their_band()
        {
            Assert.AreEqual(SurveyBand.Lt5, DungeonSurveyRules.Plan(Ledger(1)).Band);
            Assert.AreEqual(SurveyBand.Lt5, DungeonSurveyRules.Plan(Ledger(4)).Band);
            Assert.AreEqual(SurveyBand.Five, DungeonSurveyRules.Plan(Ledger(5)).Band);
            Assert.AreEqual(SurveyBand.Five, DungeonSurveyRules.Plan(Ledger(9)).Band);
            Assert.AreEqual(SurveyBand.Ten, DungeonSurveyRules.Plan(Ledger(10)).Band);
        }

        [TestMethod]
        public void A_middle_tier_paid_alone_still_pays_the_ones_around_it()
        {
            var plan = DungeonSurveyRules.Plan(Ledger(11, 5));

            CollectionAssert.AreEqual(new[] { 10, 1 }, plan.TiersToPay.ToArray());
            Assert.IsFalse(plan.Held);
        }

        [TestMethod]
        public void A_tier_whose_stamp_has_aged_out_counts_as_unpaid()
        {
            // HasQuest alone is not enough: the emote rig's InqQuest success needed the stamp AND the
            // cooldown. A stamp from a previous window is present but solvable again, so its tier is unpaid.
            var ledger = Ledger(7);
            ledger.Stale.Add(DungeonSurveyRules.SurveyQuest(5));
            ledger.Stale.Add(DungeonSurveyRules.SurveyQuest(1));

            var plan = DungeonSurveyRules.Plan(ledger);

            CollectionAssert.AreEqual(new[] { 5, 1 }, plan.TiersToPay.ToArray());
        }

        [TestMethod]
        public void Held_is_only_ever_true_inside_a_band()
        {
            // Held means "you reached a band and every tier under it is already paid". It must never fire on
            // the nothing-filed branch, which speaks a different line entirely.
            Assert.IsTrue(DungeonSurveyRules.Plan(Ledger(5, 1, 5)).Held);
            Assert.IsFalse(DungeonSurveyRules.Plan(Ledger(0)).Held);
        }

        [TestMethod]
        public void Plan_writes_nothing_to_the_ledger()
        {
            // The latch is the station's job and has to happen in one synchronous pass there; a planner that
            // stamped would make the same reward payable twice from two different code paths.
            var ledger = Ledger(12, 5);

            DungeonSurveyRules.Plan(ledger);

            Assert.AreEqual(0, ledger.Writes.Count, string.Join(", ", ledger.Writes));
        }

        [TestMethod]
        public void Tiers_to_pay_only_ever_names_tiers_that_exist_in_the_reward_table()
        {
            var plan = DungeonSurveyRules.Plan(Ledger(100));

            foreach (var tier in plan.TiersToPay)
                Assert.IsTrue(SurveyRewardMath.TryGetTier(tier, out _), $"tier {tier} is not in SurveyRewardMath.Tiers");
        }
    }
}
