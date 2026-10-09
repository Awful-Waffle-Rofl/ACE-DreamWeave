using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Pvp;
using ACE.Server.ThreadDungeons;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// The pure half of the arena Blood payout (Docs/Pvp/DESIGN.md "Rewards"): the per-seat amount, the daily-cap
    /// decision the player's action chain applies at grant time, the arena day, and the stack layout. The coordinator
    /// half (who is paid, exactly once) is in PvpMatchCoordinatorTests.
    /// </summary>
    [TestClass]
    public class PvpArenaRewardsTests
    {
        private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

        private static uint ToUnix(int year, int month, int day, int hour, int minute, int second)
            => (uint)new DateTimeOffset(year, month, day, hour, minute, second, TimeSpan.Zero).ToUnixTimeSeconds();

        // ---------------- amount ----------------

        [TestMethod]
        public void BloodFor_MapsEachOutcome_ToItsDial()
        {
            var d = PvpTunables.Defaults with { BloodEnabled = true, BloodWin = 7, BloodLoss = 3, BloodDraw = 5 };

            Assert.AreEqual(7, PvpArenaRewards.BloodFor(PvpBloodResult.Win, false, true, false, d));
            Assert.AreEqual(3, PvpArenaRewards.BloodFor(PvpBloodResult.Loss, false, true, false, d));
            Assert.AreEqual(5, PvpArenaRewards.BloodFor(PvpBloodResult.Draw, false, true, false, d));
        }

        [TestMethod]
        public void BloodFor_ShippedDefaults_Are2_1_1()
        {
            var d = PvpTunables.Defaults with { BloodEnabled = true };

            Assert.AreEqual(2, PvpArenaRewards.BloodFor(PvpBloodResult.Win, false, true, false, d));
            Assert.AreEqual(1, PvpArenaRewards.BloodFor(PvpBloodResult.Loss, false, true, false, d));
            Assert.AreEqual(1, PvpArenaRewards.BloodFor(PvpBloodResult.Draw, false, true, false, d));
        }

        [TestMethod]
        public void BloodFor_ForfeitNotLiveSameIpOrDisabled_PaysZero()
        {
            var d = PvpTunables.Defaults with { BloodEnabled = true };

            Assert.AreEqual(0, PvpArenaRewards.BloodFor(PvpBloodResult.Win, forfeited: true, wentLive: true, sameIp: false, d), "a forfeiter is never paid");
            Assert.AreEqual(0, PvpArenaRewards.BloodFor(PvpBloodResult.Win, false, wentLive: false, sameIp: false, d), "a match that never went Live pays nothing");
            Assert.AreEqual(0, PvpArenaRewards.BloodFor(PvpBloodResult.Win, false, true, sameIp: true, d), "same-IP opponents pay nothing");
            Assert.AreEqual(0, PvpArenaRewards.BloodFor(PvpBloodResult.Win, false, true, false, d with { BloodEnabled = false }));
            Assert.AreEqual(0, PvpArenaRewards.BloodFor(PvpBloodResult.Loss, false, true, false, d with { BloodLoss = -4 }), "a negative dial pays 0, never takes Blood away");
        }

        [TestMethod]
        public void ResultFor_FfaFirstOrSharedFirstWins_EveryoneElseLoses()
        {
            Assert.AreEqual(PvpBloodResult.Win, PvpArenaRewards.ResultFor(isDraw: false, twoTeam: false, won: false, placement: 1));
            Assert.AreEqual(PvpBloodResult.Loss, PvpArenaRewards.ResultFor(false, false, won: true, placement: 2), "FFA reads the placement, not the team flag");
            Assert.AreEqual(PvpBloodResult.Win, PvpArenaRewards.ResultFor(false, true, won: true, placement: 1));
            Assert.AreEqual(PvpBloodResult.Loss, PvpArenaRewards.ResultFor(false, true, won: false, placement: 1), "two-team: a winning team's forfeiter is not won");
            Assert.AreEqual(PvpBloodResult.Draw, PvpArenaRewards.ResultFor(true, true, false, 1));
            Assert.AreEqual(PvpBloodResult.Draw, PvpArenaRewards.ResultFor(true, false, false, 1));
        }

        // ---------------- daily cap ----------------

        /// <summary>Plays <paramref name="matches"/> matches on one day through DecideCap, storing back exactly what a grant stores.</summary>
        private static (int Paid, int Count, int Day) Play(int matches, int startCount, int startDay, int today, int cap)
        {
            var count = startCount;
            var day = startDay;
            var paid = 0;

            for (var i = 0; i < matches; i++)
            {
                var d = PvpArenaRewards.DecideCap(count, day, today, cap);

                if (!d.Pay)
                    continue;

                paid++;
                count = d.NewCount;
                day = d.NewDay;
            }

            return (paid, count, day);
        }

        [TestMethod]
        public void Cap_TenthPaidMatchPays_EleventhPaysZero()
        {
            var nine = Play(9, 0, 0, 500, 10);
            Assert.AreEqual(9, nine.Paid);

            var tenth = PvpArenaRewards.DecideCap(nine.Count, nine.Day, 500, 10);
            Assert.IsTrue(tenth.Pay, "the 10th paid match pays");
            Assert.AreEqual(10, tenth.NewCount);

            var eleventh = PvpArenaRewards.DecideCap(tenth.NewCount, tenth.NewDay, 500, 10);
            Assert.IsFalse(eleventh.Pay, "the 11th pays 0");
        }

        [TestMethod]
        public void Cap_FollowsTheDial_NotALiteral()
        {
            var r = Play(8, 0, 0, 500, 3);

            Assert.AreEqual(3, r.Paid);
            Assert.AreEqual(3, r.Count);
        }

        [TestMethod]
        public void Cap_MatchWhileCapped_DoesNotIncrement()
        {
            var capped = PvpArenaRewards.DecideCap(10, 500, 500, 10);

            Assert.IsFalse(capped.Pay);
            Assert.AreEqual(10, capped.NewCount, "a capped match leaves the count where it was");

            var r = Play(5, 10, 500, 500, 10);
            Assert.AreEqual(0, r.Paid);
            Assert.AreEqual(10, r.Count);
        }

        [TestMethod]
        public void Cap_DayRollover_ResetsTheCount()
        {
            var next = PvpArenaRewards.DecideCap(10, 500, 501, 10);

            Assert.IsTrue(next.Pay, "a new day pays again");
            Assert.AreEqual(1, next.NewCount);
            Assert.AreEqual(501, next.NewDay);

            var r = Play(12, 10, 500, 501, 10);
            Assert.AreEqual(10, r.Paid, "the new day has its own full cap");
        }

        [TestMethod]
        public void Cap_StoredDayInTheFuture_ReadsAsToday_NeverAsAFreshDay()
        {
            // The clock stepped back: the survey reset's rule - a future stamp is already counted.
            var d = PvpArenaRewards.DecideCap(10, 505, 500, 10);

            Assert.IsFalse(d.Pay);
        }

        [TestMethod]
        public void Cap_ZeroOrNegative_IsNoCap()
        {
            Assert.AreEqual(50, Play(50, 0, 0, 500, 0).Paid);
            Assert.AreEqual(50, Play(50, 0, 0, 500, -1).Paid);
        }

        [TestMethod]
        public void DayIndex_IsTheSurveyDay_AtTheBloodResetHour()
        {
            var before = ToUnix(2026, 10, 1, 9, 59, 59); // 05:59:59 EDT
            var after = ToUnix(2026, 10, 1, 10, 0, 0);   // 06:00:00 EDT

            Assert.AreEqual((int)SurveyDay.DayIndex(before, Eastern, 6), PvpArenaRewards.DayIndex(before, Eastern, 6));
            Assert.AreEqual(PvpArenaRewards.DayIndex(before, Eastern, 6) + 1, PvpArenaRewards.DayIndex(after, Eastern, 6), "the arena day turns over at the reset hour");
            Assert.AreEqual(PvpArenaRewards.DayIndex(before, Eastern, 0), PvpArenaRewards.DayIndex(after, Eastern, 0), "with the reset at midnight both are the same day");
            Assert.AreEqual(PvpArenaRewards.DayIndex(after, Eastern, 23), PvpArenaRewards.DayIndex(after, Eastern, 99), "the hour is clamped to 23");
        }

        // ---------------- earn / owed ledger ----------------

        /// <summary>A deliverer that lands <paramref name="lands"/> Blood (capped at the amount asked) and then optionally throws.</summary>
        private static PvpBloodDeliverer Deliver(int lands, bool thenThrow = false, List<int> asked = null)
        {
            return (int amount, ref int delivered) =>
            {
                asked?.Add(amount);
                delivered += Math.Min(lands, amount);

                if (thenThrow)
                    throw new InvalidOperationException("simulated delivery failure");
            };
        }

        [TestMethod]
        public void Earn_PartialDelivery_BanksTheRestAsOwed_AndCountsTheMatchOnce()
        {
            var r = PvpArenaRewards.Earn(new PvpBloodLedger(3, 500, 2), 10, 500, 10, Deliver(4));

            Assert.IsFalse(r.Capped);
            Assert.AreEqual(4, r.Delivered);
            Assert.AreEqual(new PvpBloodLedger(4, 500, 8), r.Ledger, "count +1, owed 2 + (10 - 4)");
            Assert.IsNull(r.DeliveryError);
        }

        [TestMethod]
        public void Earn_DeliveryThrowsPartWay_BanksAmountMinusDelivered_AndKeepsTheError()
        {
            var r = PvpArenaRewards.Earn(new PvpBloodLedger(0, 500, 0), 10, 500, 10, Deliver(3, thenThrow: true));

            Assert.AreEqual(3, r.Delivered);
            Assert.AreEqual(new PvpBloodLedger(1, 500, 7), r.Ledger, "the 7 not delivered are owed; the cap is consumed exactly once");
            Assert.IsInstanceOfType(r.DeliveryError, typeof(InvalidOperationException));
        }

        /// <summary>
        /// The R1 case: the stack reached the pack, then the create call threw before the deliverer could count it. With
        /// the pack measured, owed is amount minus what ACTUALLY arrived, so the stack is never paid twice.
        /// </summary>
        [TestMethod]
        public void Earn_StackAddedThenThrowBeforeCounting_OwesOnlyWhatDidNotArrive()
        {
            var pack = 995;
            PvpBloodDeliverer addThenThrow = (int amount, ref int delivered) =>
            {
                delivered += 2;  // a top-up, counted
                pack += 2;
                pack += 5;       // a new stack lands in the pack ...
                throw new InvalidOperationException("send failed after the add"); // ... and is never counted
            };

            var r = PvpArenaRewards.Earn(new PvpBloodLedger(0, 500, 0), 10, 500, 10, addThenThrow, () => pack);

            Assert.AreEqual(7, r.Delivered, "measured: 7 actually arrived");
            Assert.AreEqual(new PvpBloodLedger(1, 500, 3), r.Ledger, "owed == 10 - 7");
            Assert.IsNotNull(r.DeliveryError);
        }

        [TestMethod]
        public void DeliverOwed_StackAddedThenThrowBeforeCounting_KeepsOnlyTheUndeliveredOwed()
        {
            var pack = 0;
            PvpBloodDeliverer addThenThrow = (int amount, ref int delivered) =>
            {
                pack += 6;
                throw new InvalidOperationException("save failed after the add");
            };

            var r = PvpArenaRewards.DeliverOwed(new PvpBloodLedger(4, 500, 10), addThenThrow, () => pack);

            Assert.AreEqual(new PvpBloodLedger(4, 500, 4), r.Ledger);
        }

        [TestMethod]
        public void Earn_MeasurementBelowTheCount_KeepsTheCount_AndAFailingMeasurementFallsBackToIt()
        {
            var r1 = PvpArenaRewards.Earn(new PvpBloodLedger(0, 500, 0), 10, 500, 10, Deliver(6), () => 0);
            Assert.AreEqual(6, r1.Delivered, "max(count, measured): never under the count");

            var r2 = PvpArenaRewards.Earn(new PvpBloodLedger(0, 500, 0), 10, 500, 10, Deliver(6), () => throw new InvalidOperationException());
            Assert.AreEqual(6, r2.Delivered);
            Assert.IsNull(r2.DeliveryError, "a measurement failure is not a delivery failure");
        }

        [TestMethod]
        public void Earn_FullPack_BanksEverything()
        {
            var r = PvpArenaRewards.Earn(new PvpBloodLedger(0, 0, 0), 2, 500, 10, Deliver(0));

            Assert.AreEqual(new PvpBloodLedger(1, 500, 2), r.Ledger);
        }

        [TestMethod]
        public void Earn_Offline_NullDeliverer_BanksEverything()
        {
            var r = PvpArenaRewards.Earn(new PvpBloodLedger(9, 500, 1), 2, 500, 10, null);

            Assert.AreEqual(new PvpBloodLedger(10, 500, 3), r.Ledger);
        }

        [TestMethod]
        public void Earn_WhileCapped_DeliversNothing_AndLeavesTheLedgerUntouched()
        {
            var asked = new List<int>();
            var start = new PvpBloodLedger(10, 500, 4);
            var r = PvpArenaRewards.Earn(start, 2, 500, 10, Deliver(2, asked: asked));

            Assert.IsTrue(r.Capped);
            Assert.AreEqual(start, r.Ledger);
            Assert.AreEqual(0, asked.Count, "a capped match never reaches delivery");
        }

        [TestMethod]
        public void DeliverOwed_Partial_LeavesTheRemainderOwed()
        {
            var r = PvpArenaRewards.DeliverOwed(new PvpBloodLedger(2, 500, 10), Deliver(4));

            Assert.AreEqual(4, r.Delivered);
            Assert.AreEqual(new PvpBloodLedger(2, 500, 6), r.Ledger);
        }

        [TestMethod]
        public void DeliverOwed_FullPack_ReBanksEverything()
        {
            var r = PvpArenaRewards.DeliverOwed(new PvpBloodLedger(2, 500, 10), Deliver(0));

            Assert.AreEqual(new PvpBloodLedger(2, 500, 10), r.Ledger);
        }

        [TestMethod]
        public void DeliverOwed_Throws_KeepsTheUndeliveredOwed()
        {
            var r = PvpArenaRewards.DeliverOwed(new PvpBloodLedger(2, 500, 10), Deliver(6, thenThrow: true));

            Assert.AreEqual(new PvpBloodLedger(2, 500, 4), r.Ledger);
            Assert.IsNotNull(r.DeliveryError);
        }

        /// <summary>
        /// The cap is applied on EARN, never on delivery: a character at the cap still receives every owed Blood, and the
        /// paid count and day are not touched by the delivery.
        /// </summary>
        [TestMethod]
        public void DeliverOwed_AtTheCap_StillDelivers_AndNeverTouchesTheCount()
        {
            var r = PvpArenaRewards.DeliverOwed(new PvpBloodLedger(10, 500, 5), Deliver(5));

            Assert.AreEqual(5, r.Delivered);
            Assert.AreEqual(new PvpBloodLedger(10, 500, 0), r.Ledger);
        }

        [TestMethod]
        public void SettleOwedDelivery_OverDelivery_NeverGoesNegative()
        {
            Assert.AreEqual(0, PvpArenaRewards.SettleOwedDelivery(new PvpBloodLedger(1, 1, 3), 9).Owed);
            Assert.AreEqual(int.MaxValue, PvpArenaRewards.AddOwed(int.MaxValue - 1, 5), "owed saturates");
        }

        // ---------------- stack layout ----------------

        [TestMethod]
        public void PlanStacks_995Plus10_FillsTheStack_ThenANewStackOf5()
        {
            var plan = PvpArenaRewards.PlanStacks(new[] { 995 }, 1000, 10);

            CollectionAssert.AreEqual(new[] { (0, 5) }, plan.TopUps.ToArray());
            CollectionAssert.AreEqual(new[] { 5 }, plan.NewStacks.ToArray());
        }

        [TestMethod]
        public void PlanStacks_SkipsFullStacks_AndSplitsTheRemainderAtTheMax()
        {
            var plan = PvpArenaRewards.PlanStacks(new[] { 1000, 400, 999 }, 1000, 2500);

            CollectionAssert.AreEqual(new[] { (1, 600), (2, 1) }, plan.TopUps.ToArray());
            CollectionAssert.AreEqual(new[] { 1000, 899 }, plan.NewStacks.ToArray());
            Assert.AreEqual(2500, plan.TopUps.Sum(t => t.Add) + plan.NewStacks.Sum());
        }

        [TestMethod]
        public void PlanStacks_NoExistingStacks_OrNothingToPay()
        {
            CollectionAssert.AreEqual(new[] { 2 }, PvpArenaRewards.PlanStacks(Array.Empty<int>(), 1000, 2).NewStacks.ToArray());
            Assert.AreEqual(0, PvpArenaRewards.PlanStacks(new[] { 5 }, 1000, 0).TopUps.Count);
            Assert.AreEqual(0, PvpArenaRewards.PlanStacks(new[] { 5 }, 1000, 0).NewStacks.Count);
        }
    }
}
