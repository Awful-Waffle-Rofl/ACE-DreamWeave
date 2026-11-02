using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Pvp;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// PvP dailies: the per-seat match facts the coordinator builds in Resolve and dispatches through the player gateway, driven through the
    /// arena rig. Facts go out exactly once per ELIGIBLE seat, by the same PvpRewardEligibility rule as the payouts. Method names start with
    /// DailyFacts_ so they can be run on their own.
    /// </summary>
    public partial class PvpMatchCoordinatorTests
    {
        /// <summary>
        /// Drives: a 1v1 between two accounts, A kills B, then extra ticks and the post-match close. Each seat gets exactly one record: A a
        /// win, B a loss, each carrying the OTHER seat's account (not its character), mode 1v1, and A's kill.
        /// </summary>
        [TestMethod]
        public void DailyFacts_OneVOne_OncePerSeat_CarriesTheOpponentAccount()
        {
            var rig = new Rig().Build();
            var match = RoundToLive(rig, 1, out var one, out var two);

            rig.Death(two, match.MatchId, killer: one);
            rig.Tick();
            Assert.AreEqual(PvpMatchState.Resolving, match.State);
            rig.Tick();
            rig.PostMatch();
            rig.Tick();

            var facts = rig.Gateway.DailyFacts;
            Assert.AreEqual(2, facts.Count, "one record per seat, once");

            var winner = facts.Single(f => f.CharacterId == one);
            var loser = facts.Single(f => f.CharacterId == two);

            Assert.AreEqual(PvpDailyMode.OneVOne, winner.Mode);
            Assert.AreEqual(PvpBloodResult.Win, winner.Result);
            Assert.AreEqual(PvpBloodResult.Loss, loser.Result);
            Assert.AreEqual(AcctTwo, winner.OpponentAccount, "the opponent's ACCOUNT");
            Assert.AreEqual(AcctOne, loser.OpponentAccount);
            Assert.AreEqual(1, winner.Kills);
            Assert.AreEqual(match.MatchId, winner.MatchId);
            Assert.IsTrue(winner.PeakRating > 1500, "the winner's peak already includes this match's rating");
        }

        /// <summary>
        /// The exactly-once guard itself: after Resolve dispatched one record per seat, the dispatch path is run twice more with Resolve's own
        /// arguments (the test seam). Still one record per seat. With the DailyFactsDispatched guard removed each extra run adds a record per seat.
        /// </summary>
        [TestMethod]
        public void DailyFacts_DispatchRunTwiceMore_StillOnePerSeat()
        {
            var rig = new Rig().Build();
            var match = RoundToLive(rig, 1, out var one, out var two);

            rig.Death(two, match.MatchId, killer: one);
            rig.Tick();
            Assert.AreEqual(PvpMatchState.Resolving, match.State);
            Assert.AreEqual(2, rig.Gateway.DailyFacts.Count, "fixture: Resolve dispatched once per seat");

            rig.Coordinator.RedispatchDailyFactsForTest(match.MatchId);
            rig.Coordinator.RedispatchDailyFactsForTest(match.MatchId);

            Assert.AreEqual(1, rig.Gateway.DailyFacts.Count(f => f.CharacterId == one));
            Assert.AreEqual(1, rig.Gateway.DailyFacts.Count(f => f.CharacterId == two));
        }

        /// <summary>A character holding the 1v1 dailies, for feeding dispatched facts to the real PvpDailyProgress.</summary>
        private sealed class DuelHolder : IPvpDailyTarget
        {
            public readonly System.Collections.Generic.Dictionary<string, int> Quests = new();
            public readonly System.Collections.Generic.Dictionary<PvpDailyStateSlot, string> State = new();
            public readonly System.Collections.Generic.Dictionary<PvpDailyStamp, int> Stamps = new();

            public bool HasQuest(string questName) => Quests.ContainsKey(questName);

            public int GetSolves(string questName) => Quests.TryGetValue(questName, out var v) ? v : 0;

            public void SetSolves(string questName, int completions) => Quests[questName] = completions;

            public string GetState(PvpDailyStateSlot slot) => State.TryGetValue(slot, out var v) ? v : null;

            public void SetState(PvpDailyStateSlot slot, string value)
            {
                if (value == null)
                    State.Remove(slot);
                else
                    State[slot] = value;
            }

            public int GetStamp(PvpDailyStamp stamp) => Stamps.TryGetValue(stamp, out var v) ? v : 0;

            public void SetStamp(PvpDailyStamp stamp, int value) => Stamps[stamp] = value;

            public int this[PvpDailyTask task] => Quests[PvpDailyCatalog.CounterNameOf(task)];
        }

        /// <summary>
        /// Drives: a Live 1v1 where B forfeits by leaving the instance. A gets its full facts (a win). B gets exactly one record, the minimal
        /// FORFEIT fact. Fed to the real PvpDailyProgress on a character holding a GladiatorsStreak at 1 plus the other 1v1 dailies, it resets
        /// the streak to 0 and advances nothing else. Without the forfeit dispatch B gets no record and the streak survives at 1.
        /// </summary>
        [TestMethod]
        public void DailyFacts_OneVOneForfeiter_GetsOnlyTheForfeitFact_WhichResetsTheStreak()
        {
            var rig = new Rig().Build();
            var match = RoundToLive(rig, 1, out var one, out var two);

            rig.Gateway.Players[two].Instance = 0;
            rig.Tick();
            Assert.AreEqual(PvpMatchState.Resolving, match.State);
            rig.Tick();
            rig.PostMatch();

            Assert.AreEqual(2, rig.Gateway.DailyFacts.Count, "the winner's facts and the forfeiter's forfeit fact, once each");

            var winner = rig.Gateway.DailyFacts.Single(f => f.CharacterId == one);
            Assert.IsFalse(winner.Forfeited);
            Assert.AreEqual(PvpBloodResult.Win, winner.Result);

            var forfeit = rig.Gateway.DailyFacts.Single(f => f.CharacterId == two);
            Assert.IsTrue(forfeit.Forfeited, "the forfeiter's record is the forfeit fact");
            Assert.AreEqual(PvpDailyMode.OneVOne, forfeit.Mode);

            var holder = new DuelHolder();
            foreach (var task in new[] { PvpDailyTask.BloodInTheSand, PvpDailyTask.FirstBlood, PvpDailyTask.ProvingGround })
                holder.Quests[PvpDailyCatalog.CounterNameOf(task)] = 0;
            holder.Quests[PvpDailyCatalog.CounterNameOf(PvpDailyTask.GladiatorsStreak)] = 1;
            holder.State[PvpDailyStateSlot.GladiatorsStreak] = new PvpDailyOpponentState(1, new[] { 0x77u }).Format();

            foreach (var f in rig.Gateway.DailyFacts.Where(f => f.CharacterId == two))
                PvpDailyProgress.Apply(holder, f);

            Assert.AreEqual(0, holder[PvpDailyTask.GladiatorsStreak], "a Live 1v1 forfeit resets the streak like a loss");
            Assert.AreEqual(0, holder[PvpDailyTask.BloodInTheSand], "but it is not a completed match");
            Assert.AreEqual(0, holder[PvpDailyTask.FirstBlood]);
            Assert.AreEqual(0, holder[PvpDailyTask.ProvingGround]);
            Assert.AreEqual(0, holder.Stamps.Count, "and it stamps nothing");
        }

        /// <summary>Drives: same-IP opponents (pvp_arena_block_same_ip off), a played 1v1. Nobody gets facts.</summary>
        [TestMethod]
        public void DailyFacts_SameIpOpponents_GetNone()
        {
            var rig = new Rig();
            rig.Dials = rig.Dials with { BlockSameIp = false };
            rig.Build();

            var match = RoundToLive(rig, 1, out var one, out var two, ipOne: "10.9.9.9", ipTwo: "10.9.9.9");
            rig.Death(two, match.MatchId, killer: one);
            rig.Tick();

            Assert.AreEqual(PvpMatchState.Resolving, match.State);
            Assert.AreEqual(0, rig.Gateway.DailyFacts.Count);
        }

        /// <summary>
        /// Drives: three 1v1 rounds between the same two accounts (pair cap 2). Rounds one and two give both seats facts; round three is an
        /// unpaid rematch for both seats and gives nobody facts.
        /// </summary>
        [TestMethod]
        public void DailyFacts_UnpaidRematch_GetsNone()
        {
            var rig = new Rig().Build();

            PlayRound(rig, 1);
            PlayRound(rig, 2);
            Assert.AreEqual(4, rig.Gateway.DailyFacts.Count, "control: the first two rounds give both seats facts");

            var third = PlayRound(rig, 3);

            Assert.IsTrue(LineupSaysUnpaid(rig, third.One), "fixture: round three is an unpaid rematch");
            Assert.AreEqual(4, rig.Gateway.DailyFacts.Count, "the unpaid rematch adds none");
        }

        /// <summary>A character with the FirstBlood daily accepted, for feeding the dispatched facts to the real PvpDailyProgress.</summary>
        private sealed class FirstBloodHolder : IPvpDailyTarget
        {
            public int Solves;

            public bool HasQuest(string questName) => questName == PvpDailyCatalog.CounterNameOf(PvpDailyTask.FirstBlood);

            public int GetSolves(string questName) => HasQuest(questName) ? Solves : 0;

            public void SetSolves(string questName, int completions) => Solves = completions;

            public string GetState(PvpDailyStateSlot slot) => null;

            public void SetState(PvpDailyStateSlot slot, string value) { }

            public int GetStamp(PvpDailyStamp stamp) => 0;

            public void SetStamp(PvpDailyStamp stamp, int value) { }
        }

        /// <summary>
        /// Every arena amount 0 (pvp_arena_blood_win / _loss / _draw, as PR #1665 ships), switch on, pair cap 2. Eligibility is the flags,
        /// never "was an amount paid": rounds one and two are normal 1v1 wins and each advances the winner's FirstBlood; round three is an
        /// unrewarded rematch and its win does NOT advance it. No grant is made in any round (fixture), so an amount-derived rule would
        /// either give all three rounds or none.
        /// </summary>
        [TestMethod]
        public void DailyFacts_ZeroArenaAmounts_UnpaidRematchWinDoesNotAdvanceFirstBlood()
        {
            var rig = new Rig();
            rig.Dials = rig.Dials with { BloodEnabled = true, BloodWin = 0, BloodLoss = 0, BloodDraw = 0 };
            rig.Build();

            var normal = PlayRound(rig, 1);
            var holder = new FirstBloodHolder();
            PvpDailyProgress.Apply(holder, rig.Gateway.DailyFacts.Single(f => f.CharacterId == normal.One));
            Assert.AreEqual(1, holder.Solves, "a normal 1v1 win advances FirstBlood with every amount at 0");

            PlayRound(rig, 2);
            var unpaid = PlayRound(rig, 3);

            Assert.IsTrue(LineupSaysUnpaid(rig, unpaid.One), "fixture: round three is an unrewarded rematch");
            Assert.AreEqual(0, rig.Gateway.BloodGrants.Count, "fixture: no round paid anything");
            Assert.IsFalse(rig.Gateway.DailyFacts.Any(f => f.MatchId == unpaid.Match.MatchId), "the unrewarded rematch dispatches no facts");

            var fresh = new FirstBloodHolder();
            foreach (var f in rig.Gateway.DailyFacts.Where(f => f.MatchId == unpaid.Match.MatchId))
                PvpDailyProgress.Apply(fresh, f);
            Assert.AreEqual(0, fresh.Solves, "so the rematch win does not advance FirstBlood");
        }

        /// <summary>Drives: a walk-out during the Countdown (resolves, never Live). Nobody gets facts.</summary>
        [TestMethod]
        public void DailyFacts_NeverLive_GetNone()
        {
            var rig = new Rig().Build();
            rig.Gateway.Add(A, "Alpha");
            rig.Gateway.Add(B, "Bravo");
            rig.Coordinator.Join(A, "1v1", false);
            rig.Coordinator.Join(B, "1v1", false);
            rig.Tick();
            var match = rig.MatchOf(A);
            rig.AcceptAll(A, B);
            rig.Tick();
            rig.Tick();
            rig.Tick();
            Assert.AreEqual(PvpMatchState.Countdown, match.State);

            rig.Gateway.Players[B].Instance = 0;
            rig.Tick();

            Assert.AreEqual(PvpMatchState.Resolving, match.State);
            Assert.IsFalse(match.LiveSinceUtc.HasValue, "fixture: never Live");
            Assert.AreEqual(0, rig.Gateway.DailyFacts.Count);
        }

        /// <summary>
        /// Drives: the Tugak floor scenario (A kills everyone; D and E deal nothing) with the contribution floor ON and OFF. Off: all five
        /// seats get facts. On: the two floor-withheld seats get none and the other three keep theirs. A, first place, is a Tugak win with
        /// four kills.
        /// </summary>
        [TestMethod]
        public void DailyFacts_Tugak_FloorWithheldSeats_GetNone()
        {
            var on = TugakFloorRun(true);
            var off = TugakFloorRun(false);

            Assert.AreEqual(5, off.Rig.Gateway.DailyFacts.Count, "control: the floor off gives every seat facts");
            CollectionAssert.AreEquivalent(new[] { A, B, C }, on.Rig.Gateway.DailyFacts.Select(f => f.CharacterId).ToArray());

            var a = on.Rig.Gateway.DailyFacts.Single(f => f.CharacterId == A);
            Assert.AreEqual(PvpDailyMode.Tugak, a.Mode);
            Assert.AreEqual(PvpBloodResult.Win, a.Result, "first place is a Tugak win");
            Assert.AreEqual(4, a.Kills);
            Assert.IsNull(a.OpponentAccount, "no opponent account outside 1v1");
            Assert.AreEqual(PvpBloodResult.Loss, on.Rig.Gateway.DailyFacts.Single(f => f.CharacterId == B).Result);
        }

        /// <summary>
        /// Drives: a played 1v1 with pvp_arena_blood_enabled OFF. No Blood is paid, but both seats still get facts: the master switch is the
        /// currency payout's, not part of the eligibility rule.
        /// </summary>
        [TestMethod]
        public void DailyFacts_BloodSwitchOff_StillGivesFacts()
        {
            var rig = new Rig();
            rig.Dials = rig.Dials with { BloodEnabled = false };
            rig.Build();

            PlayRound(rig, 1);

            Assert.AreEqual(0, rig.Gateway.BloodGrants.Count, "fixture: nothing paid");
            Assert.AreEqual(2, rig.Gateway.DailyFacts.Count);
        }
    }
}
