using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Pvp;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// The repeat-pairing cap (Docs/Pvp/DESIGN.md "Repeat-pairing cap") driven through the coordinator rig: Join x2, Tick, Accept x2,
    /// Tick through Staging / Countdown / Live, then a Death (or a walk-out, or Cancel), Tick, and the post-match Tick. Every round uses
    /// FRESH characters, so only the ACCOUNT can carry the count from one round to the next.
    /// </summary>
    public partial class PvpMatchCoordinatorTests
    {
        private const uint AcctOne = 1001, AcctTwo = 1002;

        private static uint Char(int n) => 0x50000100u + (uint)n;

        private static void AddOnAccount(Rig rig, uint id, uint account, string ip = null)
        {
            var player = rig.Gateway.Add(id, $"Char{id & 0xFFF}", ip);

            player.Facts = player.Facts with { AccountId = account };
        }

        /// <summary>Two fresh characters, one on each account, queue 1v1 and drive to Live.</summary>
        private static PvpMatch RoundToLive(Rig rig, int n, out uint one, out uint two, string ipOne = null, string ipTwo = null)
        {
            one = Char(2 * n);
            two = Char(2 * n + 1);
            AddOnAccount(rig, one, AcctOne, ipOne);
            AddOnAccount(rig, two, AcctTwo, ipTwo);

            Assert.IsTrue(rig.Coordinator.Join(one, "1v1", false).Joined);
            Assert.IsTrue(rig.Coordinator.Join(two, "1v1", false).Joined);

            rig.Tick();
            Assert.AreEqual(PvpMatchState.AwaitingAccept, rig.MatchOf(one).State);
            rig.AcceptAll(one, two);

            return rig.DriveAcceptedToLive(one);
        }

        /// <summary>A played-out round: <paramref name="one"/> kills <paramref name="two"/>, then the post-match close.</summary>
        private static (uint One, uint Two, PvpMatch Match) PlayRound(Rig rig, int n)
        {
            var match = RoundToLive(rig, n, out var one, out var two);

            rig.Death(two, match.MatchId, killer: one);
            rig.Tick();
            Assert.AreEqual(PvpMatchState.Resolving, match.State);
            rig.PostMatch();

            return (one, two, match);
        }

        private static bool LineupSaysUnpaid(Rig rig, uint id) => rig.Gateway.Messages.Any(m => m.Id == id && m.Text.StartsWith("[Arena] 1v1:") && m.Text.Contains("unrewarded rematch"));

        /// <summary>
        /// Drives: three 1v1 rounds between the same two ACCOUNTS on a fresh pair of characters each time. Rounds 1 and 2 pay and carry no tag;
        /// round 3 is announced as an unpaid rematch to both seats, makes NO grant call at all (so the characters' daily cap is not touched),
        /// and still moves the ratings.
        /// </summary>
        [TestMethod]
        public void PairCap_ThirdMatchBetweenTheSameAccounts_PaysZero_ButStillChangesRating()
        {
            var rig = new Rig().Build();

            var r1 = PlayRound(rig, 1);
            var r2 = PlayRound(rig, 2);

            Assert.AreEqual((2, 1), rig.Gateway.Blood(r1.One));
            Assert.AreEqual((1, 1), rig.Gateway.Blood(r1.Two));
            Assert.AreEqual((2, 1), rig.Gateway.Blood(r2.One), "round 2 is still within the cap");
            Assert.IsFalse(LineupSaysUnpaid(rig, r1.One) || LineupSaysUnpaid(rig, r2.One), "no tag while the pair is under the cap");

            var grantsBefore = rig.Gateway.BloodGrants.Count;

            var r3 = PlayRound(rig, 3);

            Assert.IsTrue(LineupSaysUnpaid(rig, r3.One), "the lineup line carries the tag");
            Assert.IsTrue(LineupSaysUnpaid(rig, r3.Two));
            Assert.IsTrue(rig.Gateway.Got(r3.One, PvpArenaText.UnpaidRematchNotice), "and each flagged seat gets the notice");
            Assert.IsTrue(rig.Gateway.Got(r3.Two, PvpArenaText.UnpaidRematchNotice));

            Assert.AreEqual(grantsBefore, rig.Gateway.BloodGrants.Count, "a withheld match makes no grant call, so it cannot consume the daily cap");
            Assert.AreEqual((0, 0), rig.Gateway.Blood(r3.One));
            Assert.AreEqual((0, 0), rig.Gateway.Blood(r3.Two));

            var save = rig.Sink.Saves.Last();
            var winner = save.Parts.Single(p => p.CharacterId == r3.One);
            var loser = save.Parts.Single(p => p.CharacterId == r3.Two);

            Assert.IsTrue(winner.RatingAfter > winner.RatingBefore, "rating still applies to the winner");
            Assert.IsTrue(loser.RatingAfter < loser.RatingBefore, "and to the loser");
        }

        /// <summary>
        /// Drives: three 1v1 rounds between the same two ACCOUNTS with the arena amounts seeded to 0 explicitly (the shipped amounts are 2/1/1 since 2026-10-18). The pair cap stays live at 0 amounts
        /// (owner design 2026-10-15: it now means "rewarded matches", Marks AND daily quests), so round 3 still carries the reworded lineup tag and
        /// notice, and rounds 1 and 2 do not. Nothing is granted in any round.
        /// </summary>
        [TestMethod]
        public void PairCap_StaysLive_OnShippedZeroDials_ThirdRoundGetsTheRewordedNotice()
        {
            var rig = new Rig { Dials = PvpTunables.Defaults with { CrierEnabled = false, BloodWin = 0, BloodLoss = 0, BloodDraw = 0 } }.Build();
            Assert.AreEqual(0, rig.Dials.BloodWin, "control: the amounts are seeded to zero");

            var r1 = PlayRound(rig, 1);
            var r2 = PlayRound(rig, 2);
            Assert.IsFalse(LineupSaysUnpaid(rig, r1.One) || LineupSaysUnpaid(rig, r2.One), "no tag while the pair is under the cap");
            Assert.IsFalse(rig.Gateway.Got(r2.One, PvpArenaText.UnpaidRematchNotice));

            var r3 = PlayRound(rig, 3);

            Assert.IsTrue(LineupSaysUnpaid(rig, r3.One), "round 3 still carries the unrewarded-rematch tag at 0 amounts");
            Assert.IsTrue(rig.Gateway.Got(r3.One, PvpArenaText.UnpaidRematchNotice));
            Assert.IsTrue(rig.Gateway.Got(r3.Two, PvpArenaText.UnpaidRematchNotice));
            Assert.AreEqual("[Arena] You have already played this opponent the maximum number of rewarded matches today. This match still counts for rating, but not for Marks or daily quests.", PvpArenaText.UnpaidRematchNotice);
            Assert.AreEqual(0, rig.Gateway.BloodGrants.Count, "and zero amounts grant nothing in any round");
            Assert.IsTrue(rig.Gateway.DailyFacts.Any(f => f.MatchId == r2.Match.MatchId), "control: a rewarded round dispatches daily facts");
            Assert.IsFalse(rig.Gateway.DailyFacts.Any(f => f.MatchId == r3.Match.MatchId), "the capped round earns no daily-quest progress");
        }

        /// <summary>Drives: three rounds on one arena day (the third unpaid), the clock a day later, a fourth round. The next arena day pays again.</summary>
        [TestMethod]
        public void PairCap_NextArenaDay_PaysAgain()
        {
            var rig = new Rig().Build();

            PlayRound(rig, 1);
            PlayRound(rig, 2);
            var unpaid = PlayRound(rig, 3);
            Assert.AreEqual((0, 0), rig.Gateway.Blood(unpaid.One));

            rig.Clock.Advance(24 * 3600);

            var next = PlayRound(rig, 4);

            Assert.AreEqual((2, 1), rig.Gateway.Blood(next.One), "a fresh arena day pays the pair again");
            Assert.IsFalse(LineupSaysUnpaid(rig, next.One));
        }

        /// <summary>A cap of 0 is off: the same pairing keeps paying however often it repeats.</summary>
        [TestMethod]
        public void PairCap_Off_PaysEveryMatch()
        {
            var rig = new Rig();
            rig.Pairing = PvpPairingTunables.Defaults with { PaidCap = 0 };
            rig.Build();

            for (var n = 1; n <= 4; n++)
            {
                var r = PlayRound(rig, n);
                Assert.AreEqual((2, 1), rig.Gateway.Blood(r.One), $"round {n}");
            }
        }

        /// <summary>
        /// Drives: two rounds that never go Live (a walk-out during Countdown resolves, but is not a played match) and two Live matches ended
        /// by an admin Cancel (Resolve never runs). Neither kind counts toward the cap, so the next real match is paid and untagged.
        /// </summary>
        [TestMethod]
        public void PairCap_NeverLiveOrCanceledMatches_DoNotCount()
        {
            var rig = new Rig().Build();
            var n = 1;

            for (var i = 0; i < 2; i++, n++)
            {
                var one = Char(2 * n);
                var two = Char(2 * n + 1);
                AddOnAccount(rig, one, AcctOne);
                AddOnAccount(rig, two, AcctTwo);
                rig.Coordinator.Join(one, "1v1", false);
                rig.Coordinator.Join(two, "1v1", false);
                rig.Tick();
                var match = rig.MatchOf(one);
                rig.AcceptAll(one, two);
                rig.Tick();
                rig.Tick();
                rig.Tick();
                Assert.AreEqual(PvpMatchState.Countdown, match.State);

                rig.Gateway.Players[two].Instance = 0;
                rig.Tick();
                Assert.AreEqual(PvpMatchState.Resolving, match.State, "the walk-out resolved");
                Assert.IsFalse(match.LiveSinceUtc.HasValue, "but it never went Live");
                rig.PostMatch();
            }

            for (var i = 0; i < 2; i++, n++)
            {
                var live = RoundToLive(rig, n, out _, out _);
                rig.Coordinator.Cancel(live.MatchId);
                rig.PostMatch();
            }

            Assert.AreEqual(0, rig.Gateway.BloodGrants.Count, "fixture: none of those four matches paid");

            var real = PlayRound(rig, n);

            Assert.AreEqual((2, 1), rig.Gateway.Blood(real.One));
            Assert.IsFalse(LineupSaysUnpaid(rig, real.One));
        }

        /// <summary>Drives: two Live matches that end in a forfeit, then a third. Forfeits count toward the cap, so the third is an unpaid rematch.</summary>
        [TestMethod]
        public void PairCap_Forfeits_CountTowardTheCap()
        {
            var rig = new Rig().Build();

            for (var n = 1; n <= 2; n++)
            {
                var match = RoundToLive(rig, n, out var one, out var two);

                rig.Gateway.Players[two].Instance = 0;
                rig.Tick();
                Assert.AreEqual(PvpMatchState.Resolving, match.State);
                Assert.AreEqual((0, 0), rig.Gateway.Blood(two), "a forfeiter is never granted");
                rig.PostMatch();
            }

            var third = PlayRound(rig, 3);

            Assert.IsTrue(LineupSaysUnpaid(rig, third.One), "two forfeited matches already used up the pair");
            Assert.AreEqual((0, 0), rig.Gateway.Blood(third.One));
        }

        /// <summary>
        /// Same-IP opponents (pvp_arena_block_same_ip off) are an unrated, unpaid match and do NOT count: after two of them the next match,
        /// on different addresses, is paid.
        /// </summary>
        [TestMethod]
        public void PairCap_SameIpMatches_DoNotCount()
        {
            var rig = new Rig();
            rig.Dials = rig.Dials with { BlockSameIp = false };
            rig.Build();

            for (var n = 1; n <= 2; n++)
            {
                var match = RoundToLive(rig, n, out var one, out var two, ipOne: "10.9.9.9", ipTwo: "10.9.9.9");

                rig.Death(two, match.MatchId, killer: one);
                rig.Tick();
                rig.PostMatch();
            }

            Assert.AreEqual(0, rig.Gateway.BloodGrants.Count, "fixture: same-IP matches pay nothing");

            var real = PlayRound(rig, 3);

            Assert.AreEqual((2, 1), rig.Gateway.Blood(real.One));
            Assert.IsFalse(LineupSaysUnpaid(rig, real.One));
        }

        /// <summary>The pure amount: a withheld seat pays 0 whatever its result, and the same arguments unwithheld pay the dial.</summary>
        [TestMethod]
        public void BloodFor_Withheld_PaysZero()
        {
            var dials = PvpTunables.Defaults with { BloodEnabled = true, BloodWin = 2, BloodLoss = 1, BloodDraw = 1 };

            Assert.AreEqual(2, PvpArenaRewards.BloodFor(PvpBloodResult.Win, false, true, false, dials));
            Assert.AreEqual(0, PvpArenaRewards.BloodFor(PvpBloodResult.Win, false, true, false, dials, withheld: true));
            Assert.AreEqual(0, PvpArenaRewards.BloodFor(PvpBloodResult.Draw, false, true, false, dials, withheld: true));
        }

        // ================= review follow-ups: Subset, day straddle, 2v2, Blood off =================

        private static int fourPlayerSerial;

        /// <summary>
        /// Four fresh characters on <paramref name="accounts"/> queued for 2v2 and driven to Live: the first two are a premade duo
        /// (fellowship) and, after the duo-vs-solo wait, face the other two as team 0 against team 1 (the 2v2 matchmaker forms nothing
        /// else from one duo and two solos).
        /// </summary>
        private static PvpMatch FourToLive(Rig rig, uint[] accounts, out uint[] ids)
        {
            var baseId = 0x50000400u + (uint)(++fourPlayerSerial * 4);
            ids = new[] { baseId, baseId + 1, baseId + 2, baseId + 3 };

            for (var i = 0; i < 4; i++)
            {
                if (i < 2)
                    rig.Gateway.Add(ids[i], $"Duo{ids[i] & 0xFFF}", null, true, ids[0], ids[1]);
                else
                    rig.Gateway.Add(ids[i], $"Solo{ids[i] & 0xFFF}");

                rig.Gateway.Players[ids[i]].Facts = rig.Gateway.Players[ids[i]].Facts with { AccountId = accounts[i] };
            }

            Assert.IsTrue(rig.Coordinator.Join(ids[0], "2v2", true).Joined);
            Assert.IsTrue(rig.Coordinator.Join(ids[2], "2v2", false).Joined);
            Assert.IsTrue(rig.Coordinator.Join(ids[3], "2v2", false).Joined);

            rig.Clock.Advance(rig.Dials.DuoVsSoloAfterSeconds);
            rig.Tick();
            Assert.IsNotNull(rig.MatchOf(ids[0]), "the 2v2 formed");
            rig.AcceptAll(ids);

            return rig.DriveAcceptedToLive(ids[0]);
        }

        /// <summary>Ends a Live match as a timeout draw (overtime must be off) and runs the post-match close.</summary>
        private static void DrawAndClose(Rig rig, PvpMatch match)
        {
            rig.Clock.Advance(rig.Dials.TimeLimitSeconds2v2);
            rig.Tick();
            Assert.AreEqual(PvpMatchState.Resolving, match.State);
            rig.PostMatch();
        }

        private static bool Notified(Rig rig, uint id) => rig.Gateway.Got(id, PvpArenaText.UnpaidRematchNotice);

        /// <summary>
        /// A premade duo (accounts one and two) is matched against a second duo (account 3 twice); one member of the first duo declines,
        /// the match is called off, and the survivor goes back to the front of the queue as a SUBSET of the duo's queue unit. That unit
        /// must keep the survivor's account, or the survivor's pairings are counted under a bare character id. The survivor then plays as a
        /// solo against the account-3 duo, and twice more as a fresh account-1 character. Facing two account-3 opponents counts that pair once
        /// per match (default cap 2): the third round must be flagged.
        /// </summary>
        [TestMethod]
        public void PairCap_SubsetOfADuo_KeepsTheAccount()
        {
            var rig = new Rig();
            rig.Dials = rig.Dials with { OvertimeEnabled = false };
            rig.Build();

            rig.Gateway.Add(A, "Alpha", fellowship: new[] { A, B });
            rig.Gateway.Add(B, "Bravo", fellowship: new[] { A, B });
            rig.Gateway.Add(C, "Charlie", fellowship: new[] { C, D });
            rig.Gateway.Add(D, "Delta", fellowship: new[] { C, D });
            rig.Gateway.Add(E, "Echo");

            foreach (var (id, account) in new[] { (A, 1u), (B, 2u), (C, 3u), (D, 3u), (E, 5u) })
                rig.Gateway.Players[id].Facts = rig.Gateway.Players[id].Facts with { AccountId = account };

            Assert.IsTrue(rig.Coordinator.Join(A, "2v2", true).Joined);
            Assert.IsTrue(rig.Coordinator.Join(C, "2v2", true).Joined);
            rig.Tick();
            Assert.AreEqual(PvpMatchState.AwaitingAccept, rig.MatchOf(A).State, "fixture: duo against duo formed");

            Assert.AreEqual(PvpAnswerOutcome.Declined, rig.Coordinator.Decline(B).Outcome);
            rig.Tick();
            Assert.IsNull(rig.MatchOf(A), "fixture: the match was called off; Alpha is back in the queue as a subset unit");

            Assert.IsTrue(rig.Coordinator.Join(E, "2v2", false).Joined);

            // Alpha (solo, from the subset) and Echo against the account-3 duo.
            rig.Clock.Advance(rig.Dials.DuoVsSoloAfterSeconds);
            rig.Tick();

            var first = rig.MatchOf(A);
            Assert.IsNotNull(first, "the account-3 duo, the survivor and Echo formed a match");
            rig.AcceptAll(A, C, D, E);
            rig.DriveAcceptedToLive(A);
            DrawAndClose(rig, first);

            var second = FourToLive(rig, new[] { 3u, 3u, 1u, 5u }, out var round2);
            Assert.IsFalse(Notified(rig, round2[2]), "the second round is still under the cap");
            DrawAndClose(rig, second);

            var thirdRound = FourToLive(rig, new[] { 3u, 3u, 1u, 5u }, out var round3);
            Assert.IsTrue(Notified(rig, round3[2]), "the third round is an unpaid rematch: the subset round was counted under account 1");
            DrawAndClose(rig, thirdRound);
        }

        /// <summary>
        /// A match forms and goes Live at 23:59:40 on one arena day (UTC reset at hour 0), the clock crosses midnight, and the match
        /// resolves on the next day. Its pairing is counted on the FORMATION day: both 1v1 rounds on the new day are paid (cap 2). Counted
        /// on the resolve-time day it would use up one of them.
        /// </summary>
        [TestMethod]
        public void PairCap_MatchStraddlingTheReset_CountsOnTheFormationDay()
        {
            var rig = new Rig();
            rig.Dials = rig.Dials with { BloodResetTimezone = "UTC", BloodResetHour = 0 };
            rig.Clock.Now = new System.DateTime(2026, 9, 26, 23, 59, 30, System.DateTimeKind.Utc);
            rig.Build();

            var match = RoundToLive(rig, 1, out var one, out var two);
            Assert.AreEqual(26, rig.Clock.Now.Day, "fixture: Live before midnight");

            rig.Clock.Advance(60);
            Assert.AreEqual(27, rig.Clock.Now.Day, "fixture: the clock crossed the reset");

            rig.Death(two, match.MatchId, killer: one);
            rig.Tick();
            Assert.AreEqual(PvpMatchState.Resolving, match.State);
            Assert.AreEqual((2, 1), rig.Gateway.Blood(one), "the straddling match itself was paid");
            rig.PostMatch();

            var next1 = PlayRound(rig, 2);
            var next2 = PlayRound(rig, 3);

            Assert.AreEqual((2, 1), rig.Gateway.Blood(next1.One), "first round of the new day is paid");
            Assert.AreEqual((2, 1), rig.Gateway.Blood(next2.One), "and so is the second: the straddling match counted on the day before");
            Assert.IsFalse(LineupSaysUnpaid(rig, next2.One));
        }

        /// <summary>The cap is counted even while pvp_arena_blood_enabled is off: two rounds with Marks off, then on, and the third round is flagged.</summary>
        [TestMethod]
        public void PairCap_BloodDisabledMatches_StillCount()
        {
            var rig = new Rig();
            rig.Dials = rig.Dials with { BloodEnabled = false };
            rig.Build();

            PlayRound(rig, 1);
            PlayRound(rig, 2);
            Assert.AreEqual(0, rig.Gateway.BloodGrants.Count, "fixture: Marks were off");

            rig.Dials = rig.Dials with { BloodEnabled = true };

            var third = PlayRound(rig, 3);

            Assert.IsTrue(LineupSaysUnpaid(rig, third.One), "the two earlier matches counted");
            Assert.AreEqual((0, 0), rig.Gateway.Blood(third.One));
        }

        /// <summary>
        /// Two opposing seats share one account: duo (accounts 21, 22) against solos (23, 23). Each account pair advances by exactly ONE
        /// per match, not once per seat pair. At the default cap of 2 the second match is therefore still paid (nobody flagged) and the
        /// third is flagged. Without the per-match dedupe the first match alone would put 21-23 at 2 and flag the second.
        /// </summary>
        [TestMethod]
        public void PairCap_OpposingSeatsSharingAnAccount_AdvanceThePairByOne()
        {
            var rig = new Rig();
            rig.Dials = rig.Dials with { OvertimeEnabled = false };
            rig.Build();

            var first = FourToLive(rig, new[] { 21u, 22u, 23u, 23u }, out var round1);
            DrawAndClose(rig, first);

            var second = FourToLive(rig, new[] { 21u, 22u, 23u, 23u }, out var round2);
            Assert.IsFalse(round2.Any(id => Notified(rig, id)), "one match counted once: the pair is at 1 of 2");
            DrawAndClose(rig, second);

            FourToLive(rig, new[] { 21u, 22u, 23u, 23u }, out var round3);
            Assert.IsTrue(round3.All(id => Notified(rig, id)), "two counted matches: the pair is at the cap");
        }

        /// <summary>
        /// 2v2, duo AB against solos CD twice, then duo AC against solos BD (accounts 11..14, fresh characters each round). Every seat in
        /// round three has a cross-team pair at 2 (A-D, C-B, B-C, D-A), so all four are flagged; the first two rounds flag nobody.
        /// </summary>
        [TestMethod]
        public void PairCap_TwoVTwo_CrossPairsDecideEverySeat()
        {
            var rig = new Rig();
            rig.Dials = rig.Dials with { OvertimeEnabled = false };
            rig.Build();

            for (var i = 0; i < 2; i++)
            {
                var m = FourToLive(rig, new[] { 11u, 12u, 13u, 14u }, out var ids);
                Assert.IsFalse(ids.Any(id => Notified(rig, id)), $"round {i + 1} flags nobody");
                DrawAndClose(rig, m);
            }

            var third = FourToLive(rig, new[] { 11u, 13u, 12u, 14u }, out var round3);

            Assert.IsTrue(round3.All(id => Notified(rig, id)), "AC vs BD: each seat faces an opponent account it has already met twice");
            DrawAndClose(rig, third);
        }

        /// <summary>
        /// Counts are per cross-team pair, not per team: AB vs CD, AC vs BD, then AD vs BC. After two rounds A-D and B-C are at 2, but
        /// in AD vs BC they are teammates, so the cross pairs (A-B, A-C, D-B, D-C) are all at 1 and nobody is flagged. A fourth round AB vs CD
        /// puts A-D and B-C across the net again: everyone is flagged.
        /// </summary>
        [TestMethod]
        public void PairCap_TwoVTwo_TeammatesAtTheCapAreNotFlaggedUntilTheyFaceEachOther()
        {
            var rig = new Rig();
            rig.Dials = rig.Dials with { OvertimeEnabled = false };
            rig.Build();

            // accounts: A=11 B=12 C=13 D=14
            foreach (var teams in new[] { new[] { 11u, 12u, 13u, 14u }, new[] { 11u, 13u, 12u, 14u } })
            {
                var m = FourToLive(rig, teams, out _);
                DrawAndClose(rig, m);
            }

            var third = FourToLive(rig, new[] { 11u, 14u, 12u, 13u }, out var round3);
            Assert.IsFalse(round3.Any(id => Notified(rig, id)), "AD vs BC: the pairs at the cap are teammates");
            DrawAndClose(rig, third);

            var fourth = FourToLive(rig, new[] { 11u, 12u, 13u, 14u }, out var round4);
            Assert.IsTrue(round4.All(id => Notified(rig, id)), "AB vs CD: A-D and B-C are across the net and at the cap");
            DrawAndClose(rig, fourth);
        }
    }
}
