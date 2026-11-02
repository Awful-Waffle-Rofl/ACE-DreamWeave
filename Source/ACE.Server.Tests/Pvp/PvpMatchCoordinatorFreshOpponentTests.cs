using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Pvp;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// Fresh-opponent matchmaking (Docs/Pvp/DESIGN.md "Fresh-opponent matchmaking") and the /arena norematch choice driven through the
    /// coordinator rig: the recent-opponent ring fed at Resolve, the tier order at Tick, the refusal of an unpaid rematch, the toggle on an
    /// already-queued unit, and the once-only waiting notice. Every round uses FRESH characters, so only the ACCOUNT carries history.
    /// </summary>
    public partial class PvpMatchCoordinatorTests
    {
        private const uint AcctThree = 1003, AcctFour = 1004, AcctFive = 1005, AcctSix = 1006, AcctSeven = 1007, AcctEight = 1008;

        private static uint FreshChar(int n) => 0x50000400u + (uint)n;

        /// <summary>A character on <paramref name="account"/> joins the 1v1 queue, optionally with the norematch choice on.</summary>
        private static uint QueueOn(Rig rig, int n, uint account, bool refuse = false)
        {
            var id = FreshChar(n);

            AddOnAccount(rig, id, account);

            var player = rig.Gateway.Players[id];
            player.Facts = player.Facts with { RefuseUnpaidRematch = refuse };

            Assert.IsTrue(rig.Coordinator.Join(id, "1v1", false).Joined, $"char {n} joins");

            return id;
        }

        /// <summary>One played 1v1 round between two accounts on fresh characters: both queue, accept, go Live, one dies, post-match.</summary>
        private static void PlayBetween(Rig rig, int n, uint accountOne, uint accountTwo)
        {
            var one = QueueOn(rig, 2 * n, accountOne);
            var two = QueueOn(rig, 2 * n + 1, accountTwo);

            rig.Tick();
            Assert.AreEqual(PvpMatchState.AwaitingAccept, rig.MatchOf(one).State);
            rig.AcceptAll(one, two);

            var match = rig.DriveAcceptedToLive(one);

            rig.Death(two, match.MatchId, killer: one);
            rig.Tick();
            Assert.AreEqual(PvpMatchState.Resolving, match.State);
            rig.PostMatch();
        }

        private static bool Paired(Rig rig, uint x, uint y)
        {
            var mx = rig.MatchOf(x);

            return mx != null && rig.MatchOf(y) != null && mx.MatchId == rig.MatchOf(y).MatchId;
        }

        /// <summary>Drives: one played round (accounts 1/2), then queue 1, 2, 3 in that order. Account 1 skips the recent opponent (2) for the fresh one (3).</summary>
        [TestMethod]
        public void FreshOpponent_PrefersAnAccountNotPlayedRecently_OverAnEarlierRecentOne()
        {
            var rig = new Rig().Build();
            PlayBetween(rig, 1, AcctOne, AcctTwo);

            var a = QueueOn(rig, 10, AcctOne);
            var b = QueueOn(rig, 11, AcctTwo);
            var c = QueueOn(rig, 12, AcctThree);

            rig.Tick();

            Assert.IsTrue(Paired(rig, a, c), "the fresh account is picked");
            Assert.IsNull(rig.MatchOf(b), "the recent opponent keeps waiting");
        }

        /// <summary>Same queue with pvp_arena_mm_recent_opponents = 0 (off): no ring, so the earlier-queued opponent wins - proves the ring is what changed the pick above.</summary>
        [TestMethod]
        public void FreshOpponent_RecentOpponentsDialOff_KeepsQueueOrder()
        {
            var rig = new Rig();
            rig.Pairing = PvpPairingTunables.Defaults with { MmRecentOpponents = 0 };
            rig.Build();
            PlayBetween(rig, 1, AcctOne, AcctTwo);

            var a = QueueOn(rig, 10, AcctOne);
            var b = QueueOn(rig, 11, AcctTwo);
            QueueOn(rig, 12, AcctThree);

            rig.Tick();

            Assert.IsTrue(Paired(rig, a, b), "with the ring off the first queued opponent is taken");
        }

        /// <summary>A quiet queue never starves: the only opponent is a recent one and the pair still forms.</summary>
        [TestMethod]
        public void FreshOpponent_RecentOnly_StillPairs()
        {
            var rig = new Rig().Build();
            PlayBetween(rig, 1, AcctOne, AcctTwo);

            var a = QueueOn(rig, 10, AcctOne);
            var b = QueueOn(rig, 11, AcctTwo);

            rig.Tick();

            Assert.IsTrue(Paired(rig, a, b));
        }

        /// <summary>The ring keeps only the newest K: with K = 1 an older opponent is fresh again; with K = 3 both are still recent.</summary>
        [TestMethod]
        public void FreshOpponent_RingKeepsTheNewestK()
        {
            foreach (var (k, expectFresh) in new[] { (1L, true), (3L, false) })
            {
                var rig = new Rig();
                rig.Pairing = PvpPairingTunables.Defaults with { MmRecentOpponents = k };
                rig.Build();
                PlayBetween(rig, 1, AcctOne, AcctTwo);
                PlayBetween(rig, 2, AcctOne, AcctThree);

                // Recency is symmetric (either side's ring naming the other makes the pair recent), so account 2 must also have
                // moved on from account 1 for K = 1 to forget the pair: it plays account 3 as well.
                PlayBetween(rig, 3, AcctTwo, AcctThree);

                // Queue order: account 1, then 3 (the NEWEST opponent), then 2 (the older one).
                var a = QueueOn(rig, 10, AcctOne);
                var three = QueueOn(rig, 11, AcctThree);
                var two = QueueOn(rig, 12, AcctTwo);

                rig.Tick();

                if (expectFresh)
                    Assert.IsTrue(Paired(rig, a, two), "K=1: both rings now name only account 3, so account 2 is fresh again");
                else
                    Assert.IsTrue(Paired(rig, a, three), "K=3: both are recent, so queue order decides");
            }
        }

        /// <summary>Drives: cap 1, one played round, then account 1 (norematch on) and account 2 queue. No match, the waiting notice goes out exactly once across several ticks, and turning the choice off pairs them.</summary>
        [TestMethod]
        public void NoRematch_RefusesAnUnpaidPairing_NotifiesOnce_AndTheToggleFreesIt()
        {
            var rig = new Rig();
            rig.Pairing = PvpPairingTunables.Defaults with { PaidCap = 1 };
            rig.Build();
            PlayBetween(rig, 1, AcctOne, AcctTwo);

            var a = QueueOn(rig, 10, AcctOne, refuse: true);
            var b = QueueOn(rig, 11, AcctTwo);

            for (var i = 0; i < 4; i++)
            {
                rig.Clock.Advance(1);
                rig.Tick();
            }

            Assert.IsNull(rig.MatchOf(a), "the unpaid rematch is refused");
            Assert.IsNull(rig.MatchOf(b));
            Assert.AreEqual(1, rig.Gateway.Count(a, PvpArenaText.WaitingForFreshOpponent), "told once, however many ticks pass");
            Assert.IsFalse(rig.Gateway.Got(b, PvpArenaText.WaitingForFreshOpponent), "the opponent did not refuse and is not told");

            Assert.IsTrue(rig.Coordinator.SetRefuseUnpaidRematch(a, false), "a queued character's choice is found");

            rig.Tick();

            Assert.IsTrue(Paired(rig, a, b), "once the choice is off the unpaid rematch is accepted");
        }

        /// <summary>The toggle reaches a unit that is already queued: on before the first tick, nothing forms; an unknown character is not found.</summary>
        [TestMethod]
        public void NoRematch_SetOnAQueuedUnit_TakesEffectAtTheNextTick()
        {
            var rig = new Rig();
            rig.Pairing = PvpPairingTunables.Defaults with { PaidCap = 1 };
            rig.Build();
            PlayBetween(rig, 1, AcctOne, AcctTwo);

            var a = QueueOn(rig, 10, AcctOne);
            var b = QueueOn(rig, 11, AcctTwo);

            Assert.IsTrue(rig.Coordinator.SetRefuseUnpaidRematch(b, true));
            Assert.IsFalse(rig.Coordinator.SetRefuseUnpaidRematch(0x7FFFFFFF, true), "not queued");

            rig.Tick();

            Assert.IsNull(rig.MatchOf(a));
            Assert.AreEqual(1, rig.Gateway.Count(b, PvpArenaText.WaitingForFreshOpponent));
        }

        /// <summary>A refusing player who is matched anyway (a fresh opponent was behind the unpaid one) gets no waiting notice.</summary>
        [TestMethod]
        public void NoRematch_MatchedWithAFreshOpponent_GetsNoWaitingNotice()
        {
            var rig = new Rig();
            rig.Pairing = PvpPairingTunables.Defaults with { PaidCap = 1 };
            rig.Build();
            PlayBetween(rig, 1, AcctOne, AcctTwo);

            var a = QueueOn(rig, 10, AcctOne, refuse: true);
            QueueOn(rig, 11, AcctTwo);
            var c = QueueOn(rig, 12, AcctThree);

            rig.Tick();

            Assert.IsTrue(Paired(rig, a, c));
            Assert.IsFalse(rig.Gateway.Got(a, PvpArenaText.WaitingForFreshOpponent));
        }

        /// <summary>With the cap off nothing is ever unpaid, so a refusing player is matched as usual.</summary>
        [TestMethod]
        public void NoRematch_CapOff_NeverRefuses()
        {
            var rig = new Rig();
            rig.Pairing = PvpPairingTunables.Defaults with { PaidCap = 0 };
            rig.Build();
            PlayBetween(rig, 1, AcctOne, AcctTwo);

            var a = QueueOn(rig, 10, AcctOne, refuse: true);
            var b = QueueOn(rig, 11, AcctTwo);

            rig.Tick();

            Assert.IsTrue(Paired(rig, a, b));
        }

        /// <summary>The default is OFF: a player whose facts say nothing about it is matched into an unpaid rematch.</summary>
        [TestMethod]
        public void NoRematch_DefaultsToOff()
        {
            Assert.IsFalse(new PvpPlayerFacts(1, "x", null, false, false, false, false, false, false, false, null).RefuseUnpaidRematch);
            Assert.IsFalse(new QueueEntrant(System.Guid.NewGuid(), new uint[] { 1 }, new[] { 1500 }, null, System.DateTime.UtcNow).RefuseUnpaidRematch);
        }

        // ---------------- review follow-ups ----------------

        /// <summary>Drives: cap 1, one played round; account 1 and 3 form a match; account 1 accepts and then turns norematch ON while inside it; account 3 declines, so account 1 is requeued; account 2 (an unpaid rematch for account 1) joins. The choice made inside the forming match survives the requeue: no pairing.</summary>
        [TestMethod]
        public void NoRematch_ToggledInsideAFormingMatch_SurvivesTheRequeue()
        {
            var rig = new Rig();
            rig.Pairing = PvpPairingTunables.Defaults with { PaidCap = 1 };
            rig.Build();
            PlayBetween(rig, 1, AcctOne, AcctTwo);

            var a = QueueOn(rig, 10, AcctOne);
            var c = QueueOn(rig, 11, AcctThree);

            rig.Tick();
            Assert.IsTrue(Paired(rig, a, c));
            Assert.AreEqual(PvpAnswerOutcome.Accepted, rig.Coordinator.Accept(a).Outcome);

            rig.Coordinator.SetRefuseUnpaidRematch(a, true);

            Assert.AreEqual(PvpAnswerOutcome.Declined, rig.Coordinator.Decline(c).Outcome);
            rig.Tick();
            Assert.IsNull(rig.MatchOf(a), "the match is canceled and account 1 is back in the queue");

            var b = QueueOn(rig, 12, AcctTwo);

            rig.Tick();

            Assert.IsNull(rig.MatchOf(a), "the requeued unit kept the choice made inside the match, so the unpaid rematch is not formed");
            Assert.IsNull(rig.MatchOf(b));
        }

        /// <summary>Drives: cap 1; queue X (account 2), A (account 1, norematch on, unpaid against X), Y, D. X takes Y, then A takes D in the same pass: A was refused once but is NOT waiting, so gets no notice.</summary>
        [TestMethod]
        public void NoRematch_UnitMatchedLaterInTheSamePass_GetsNoNotice()
        {
            var rig = new Rig();
            rig.Pairing = PvpPairingTunables.Defaults with { PaidCap = 1 };
            rig.Build();
            PlayBetween(rig, 1, AcctOne, AcctTwo);

            var x = QueueOn(rig, 10, AcctTwo);
            var a = QueueOn(rig, 11, AcctOne, refuse: true);
            var y = QueueOn(rig, 12, AcctThree);
            var d = QueueOn(rig, 13, AcctFour);

            rig.Tick();

            Assert.IsTrue(Paired(rig, x, y), "X skips the refusing unpaid A and takes the fresh Y");
            Assert.IsTrue(Paired(rig, a, d), "A is matched later in the pass");
            Assert.IsFalse(rig.Gateway.Got(a, PvpArenaText.WaitingForFreshOpponent));
        }

        /// <summary>With Marks off (pvp_arena_blood_enabled false) nothing is paid, so no rematch is unpaid and a refuser pairs.</summary>
        [TestMethod]
        public void NoRematch_BloodDisabled_NothingIsUnpaid()
        {
            var rig = new Rig();
            rig.Pairing = PvpPairingTunables.Defaults with { PaidCap = 1 };
            rig.Build();
            PlayBetween(rig, 1, AcctOne, AcctTwo);

            rig.Dials = rig.Dials with { BloodEnabled = false };

            var a = QueueOn(rig, 10, AcctOne, refuse: true);
            var b = QueueOn(rig, 11, AcctTwo);

            rig.Tick();

            Assert.IsTrue(Paired(rig, a, b));
        }

        /// <summary>The once-only notice re-arms only on a real off-to-on change: setting ON again does not repeat it, off then on does.</summary>
        [TestMethod]
        public void NoRematch_NoticeRearmsOnlyOnARealOffToOnChange()
        {
            var rig = new Rig();
            rig.Pairing = PvpPairingTunables.Defaults with { PaidCap = 1 };
            rig.Build();
            PlayBetween(rig, 1, AcctOne, AcctTwo);

            var a = QueueOn(rig, 10, AcctOne, refuse: true);
            QueueOn(rig, 11, AcctTwo);

            rig.Tick();
            Assert.AreEqual(1, rig.Gateway.Count(a, PvpArenaText.WaitingForFreshOpponent));

            rig.Coordinator.SetRefuseUnpaidRematch(a, true);
            rig.Clock.Advance(1);
            rig.Tick();
            Assert.AreEqual(1, rig.Gateway.Count(a, PvpArenaText.WaitingForFreshOpponent), "on -> on is not a change");

            rig.Coordinator.SetRefuseUnpaidRematch(a, false);
            rig.Coordinator.SetRefuseUnpaidRematch(a, true);
            rig.Clock.Advance(1);
            rig.Tick();
            Assert.AreEqual(2, rig.Gateway.Count(a, PvpArenaText.WaitingForFreshOpponent), "off -> on re-arms it");
        }

        // ---- 2v2 duos ----

        /// <summary>A premade duo (two accounts) joins 2v2, optionally with each member's norematch choice.</summary>
        private static (uint First, uint Second) QueueDuo(Rig rig, int n, uint accountFirst, uint accountSecond, bool refuseFirst = false, bool refuseSecond = false)
        {
            var first = FreshChar(2 * n);
            var second = FreshChar(2 * n + 1);

            AddOnAccount(rig, first, accountFirst);
            AddOnAccount(rig, second, accountSecond);

            var fellowship = new uint[] { first, second };

            rig.Gateway.Players[first].Facts = rig.Gateway.Players[first].Facts with { FellowshipMemberIds = fellowship, RefuseUnpaidRematch = refuseFirst };
            rig.Gateway.Players[second].Facts = rig.Gateway.Players[second].Facts with { FellowshipMemberIds = fellowship, RefuseUnpaidRematch = refuseSecond };

            Assert.IsTrue(rig.Coordinator.Join(first, "2v2", true).Joined);

            return (first, second);
        }

        /// <summary>
        /// Drives: a 1v1 round puts the SECOND member's account of duo X in a recent pairing with account 5; then duos X, R (holds account 5)
        /// and F (fresh) queue 2v2 in that order. X's recent set is the union over its members, so X takes F, not R.
        /// </summary>
        [TestMethod]
        public void FreshOpponent_Duo_UsesTheUnionOfBothMembersAccounts()
        {
            var rig = new Rig().Build();
            PlayBetween(rig, 1, AcctTwo, AcctFive);

            var x = QueueDuo(rig, 10, AcctOne, AcctTwo);
            var r = QueueDuo(rig, 11, AcctSix, AcctFive);
            var f = QueueDuo(rig, 12, AcctSeven, AcctEight);

            rig.Tick();

            Assert.IsTrue(Paired(rig, x.First, f.First), "the fresh duo is taken");
            Assert.IsNull(rig.MatchOf(r.First), "the duo holding the recent opponent's account keeps waiting");
        }

        /// <summary>Drives: cap 1, a 1v1 round between account 1 and account 5; duo X (accounts 1, 2) vs duo R (accounts 5, 6), alone. Refusal is per member: it blocks only when the refusing member is itself unpaid.</summary>
        [TestMethod]
        public void NoRematch_Duo_IsDecidedPerMember()
        {
            // Member 2 (account 2) refuses but is paid (never met account 5): the duo pairs.
            var rig = new Rig();
            rig.Pairing = PvpPairingTunables.Defaults with { PaidCap = 1 };
            rig.Build();
            PlayBetween(rig, 1, AcctOne, AcctFive);

            var x = QueueDuo(rig, 10, AcctOne, AcctTwo, refuseFirst: false, refuseSecond: true);
            var r = QueueDuo(rig, 11, AcctFive, AcctSix);

            rig.Tick();
            Assert.IsTrue(Paired(rig, x.First, r.First), "the refusing member is paid, so nothing blocks");

            // Member 1 (account 1) refuses and IS unpaid against account 5: blocked, and only that member is told.
            var rig2 = new Rig();
            rig2.Pairing = PvpPairingTunables.Defaults with { PaidCap = 1 };
            rig2.Build();
            PlayBetween(rig2, 1, AcctOne, AcctFive);

            var x2 = QueueDuo(rig2, 10, AcctOne, AcctTwo, refuseFirst: true);
            var r2 = QueueDuo(rig2, 11, AcctFive, AcctSix);

            rig2.Tick();

            Assert.IsNull(rig2.MatchOf(x2.First), "the unpaid refusing member blocks the pairing");
            Assert.IsTrue(rig2.Gateway.Got(x2.First, PvpArenaText.WaitingForFreshOpponent), "the refusing member is told");
            Assert.IsFalse(rig2.Gateway.Got(x2.Second, PvpArenaText.WaitingForFreshOpponent), "the partner who did not refuse is not");
            Assert.IsFalse(rig2.Gateway.Got(r2.First, PvpArenaText.WaitingForFreshOpponent));
        }

        /// <summary>
        /// Drives: one 2v2, duo (accounts 21, 22) against solos (23, 23), played to a timeout draw, cap 10 so saturation cannot hide a
        /// double count. Each opposing account pair (21-23 and 22-23) sits on TWO seat pairs, yet the pair ledger counts each ONCE
        /// (the repeat-pairing cap's per-match dedupe), and the same deduped pass still feeds the recent-opponent ring both ways.
        /// </summary>
        [TestMethod]
        public void RecordPairings_SameAccountPairOnTwoSeatPairs_CountsOnce_AndStillFeedsTheRecentOpponentRing()
        {
            var rig = new Rig();
            rig.Dials = rig.Dials with { OvertimeEnabled = false };
            rig.Pairing = PvpPairingTunables.Defaults with { PaidCap = 10 };
            rig.Build();

            var match = FourToLive(rig, new[] { 21u, 22u, 23u, 23u }, out _);
            DrawAndClose(rig, match);

            Assert.AreEqual(1, rig.Coordinator.PairCountForTest(21, 23), "21-23 is on two seat pairs but counts once per match");
            Assert.AreEqual(1, rig.Coordinator.PairCountForTest(22, 23), "22-23 likewise");
            Assert.AreEqual(0, rig.Coordinator.PairCountForTest(21, 22), "teammates are never a pair");

            CollectionAssert.AreEqual(new[] { 23u }, rig.Coordinator.RecentOpponentsForTest(21).ToArray(), "account 21's ring names account 23");
            CollectionAssert.AreEqual(new[] { 23u }, rig.Coordinator.RecentOpponentsForTest(22).ToArray(), "account 22's ring names account 23");
            CollectionAssert.AreEquivalent(new[] { 21u, 22u }, rig.Coordinator.RecentOpponentsForTest(23).ToArray(), "account 23's ring names both opponents, each once");
        }
    }
}