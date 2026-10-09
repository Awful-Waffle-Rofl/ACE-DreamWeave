using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Pvp;
using ACE.Server.Pvp.Battlegrounds;

namespace ACE.Server.Tests.Pvp.Battlegrounds
{
    /// <summary>
    /// BattlegroundMatchmaker (Docs/Pvp/BATTLEGROUNDS.md "Matchmaking"): fill window, team size, premade
    /// handling, same-IP blocking, clan splitting and rating balance. Dials are built explicitly here.
    /// </summary>
    [TestClass]
    public class BattlegroundMatchmakerTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private static BattlegroundDials Dials(bool splitClanmates = true, int fillWindow = 120) => BattlegroundTunables.Defaults with
        {
            MinPlayers = 4,
            MaxPlayers = 12,
            FillWindowSeconds = fillWindow,
            MaxPremadeSize = 6,
            PremadeImbalanceTolerance = 2,
            PremadeVsSoloAfterSeconds = 120,
            SplitClanmates = splitClanmates,
            // These tests pin the pre-snake preference order; BattlegroundMatchmakerSnakeTests covers pvp_bg_snake_draft on.
            SnakeDraft = false
        };

        private static MatchmakingContext Ctx(DateTime now, bool blockSameIp = true, bool splitClanmates = true, int fillWindow = 120) => new MatchmakingContext(
            UtcNow: now,
            MmWindowInitial: 0, MmWindowGrowthPerMinute: 0, MmWindowMax: 0,
            DuoVsSoloAfterSeconds: 0,
            FfaTargetPlayers: 0, FfaMinPlayers: 0, FfaMaxPlayers: 0, FfaMinDecaySeconds: 0,
            BlockSameIp: blockSameIp,
            Bg: Dials(splitClanmates, fillWindow));

        // ================= FillWindowCloseUtc (the helper the matchmaker and the Arena Crier share) =================

        [TestMethod]
        public void FillWindowCloseUtc_NullBelowMinPlayers()
        {
            var units = new[] { (2, T0), (1, T0.AddSeconds(1)) };

            Assert.IsNull(BattlegroundMatchmaker.FillWindowCloseUtc(units, Dials()), "3 of 4 players: no window yet");
        }

        [TestMethod]
        public void FillWindowCloseUtc_T4IsTheUnitThatCrossesMinPlayers_NotTheFirstOrLast()
        {
            // Running totals 2, 4 (crosses MinPlayers = 4 here), 7: t4 is the SECOND unit's time.
            var units = new[] { (2, T0), (2, T0.AddSeconds(10)), (3, T0.AddSeconds(50)) };

            Assert.AreEqual(T0.AddSeconds(10 + 120), BattlegroundMatchmaker.FillWindowCloseUtc(units, Dials(fillWindow: 120)));
        }

        [TestMethod]
        public void FillWindowCloseUtc_UnitsSharingAQueueTime_GiveTheSameT4WhicheverEntrantIdSortsFirst()
        {
            // Two units queued in the same instant (distinct EntrantIds) are ordered by EntrantId, but equal queue times
            // mean either order puts t4 at that same instant, so the tie-break cannot move the close time.
            var shared = T0.AddSeconds(5);

            var a = new[] { (3, shared), (1, shared), (4, T0.AddSeconds(30)) };
            var b = new[] { (1, shared), (3, shared), (4, T0.AddSeconds(30)) };

            Assert.AreEqual(shared.AddSeconds(120), BattlegroundMatchmaker.FillWindowCloseUtc(a, Dials(fillWindow: 120)));
            Assert.AreEqual(shared.AddSeconds(120), BattlegroundMatchmaker.FillWindowCloseUtc(b, Dials(fillWindow: 120)));
        }
        private static Guid IdFor(uint id) => new Guid((int)id, 0, 0, new byte[8]);

        private static QueueEntrant Solo(uint id, DateTime queuedAt, int rating = 1500, string ip = null, uint monarch = 0) =>
            new QueueEntrant(IdFor(id), new[] { id }, new[] { rating }, ip, queuedAt, new[] { monarch });

        private static QueueEntrant Premade(uint firstId, int size, DateTime queuedAt, string ip = null, IReadOnlyList<string> ipKeys = null)
        {
            var ids = Enumerable.Range(0, size).Select(i => firstId + (uint)i).ToArray();
            return new QueueEntrant(IdFor(firstId), ids, ids.Select(_ => 1500).ToArray(), ip, queuedAt, null, ipKeys);
        }

        private static List<QueueEntrant> Solos(uint firstId, int count, DateTime queuedAt) =>
            Enumerable.Range(0, count).Select(i => Solo(firstId + (uint)i, queuedAt.AddSeconds(i))).ToList();

        private static IEnumerable<uint> Ids(PvpTeam t) => t.Members.Select(m => m.CharacterId);

        private static IEnumerable<uint> AllIds(MatchProposal p) => p.Teams.SelectMany(Ids);

        private static string Describe(MatchProposal p) =>
            p == null ? "null" : string.Join(" vs ", p.Teams.Select(t => "[" + string.Join(",", Ids(t)) + "]"));

        [TestMethod]
        public void ThreeSolos_NeverForm_EvenLong()
        {
            var proposal = new BattlegroundMatchmaker().TryForm(Solos(1, 3, T0), Ctx(T0.AddHours(1)));

            Assert.IsNull(proposal);
        }

        [TestMethod]
        public void FourSolos_BeforeWindow_Null_AfterWindow_TwoVsTwo()
        {
            var waiting = Solos(1, 4, T0);
            var t4 = T0.AddSeconds(3);

            Assert.IsNull(new BattlegroundMatchmaker().TryForm(waiting, Ctx(t4.AddSeconds(119))));

            var proposal = new BattlegroundMatchmaker().TryForm(waiting, Ctx(t4.AddSeconds(120)));

            Assert.IsNotNull(proposal);
            Assert.IsTrue(proposal.Rated);
            Assert.AreEqual(2, proposal.Teams.Count);
            Assert.AreEqual(0, proposal.Teams[0].TeamIndex);
            Assert.AreEqual(1, proposal.Teams[1].TeamIndex);
            Assert.IsTrue(proposal.Teams.All(t => t.Members.Count == 2));
        }

        /// <summary>pvp_bg_max_players is a real cap: 12 solos with MaxPlayers 8 form 4v4 of the 8 oldest; the other 4 wait.</summary>
        [TestMethod]
        public void TwelveSolos_MaxPlayersEight_FormFourVsFour_RestWait()
        {
            var ctx = Ctx(T0.AddSeconds(11)) with { Bg = Dials() with { MaxPlayers = 8 } };
            var proposal = new BattlegroundMatchmaker().TryForm(Solos(1, 12, T0), ctx);

            Assert.IsNotNull(proposal, "8 or more waiting forms at once");
            Assert.IsTrue(proposal.Teams.All(t => t.Members.Count == 4), Describe(proposal));
            CollectionAssert.AreEquivalent(Enumerable.Range(1, 8).Select(i => (uint)i).ToArray(), AllIds(proposal).ToArray(), "the 8 oldest play; 9-12 wait");
        }

        [TestMethod]
        public void TwelveSolos_FormSixVsSix_AtOnce()
        {
            var proposal = new BattlegroundMatchmaker().TryForm(Solos(1, 12, T0), Ctx(T0));

            Assert.IsNotNull(proposal);
            Assert.IsTrue(proposal.Teams.All(t => t.Members.Count == 6));
            CollectionAssert.AreEquivalent(Enumerable.Range(1, 12).Select(i => (uint)i).ToList(), AllIds(proposal).ToList());
        }

        [TestMethod]
        public void FiveSolos_AfterWindow_TwoVsTwo_NewestWaits()
        {
            var waiting = Solos(1, 5, T0);

            var proposal = new BattlegroundMatchmaker().TryForm(waiting, Ctx(T0.AddSeconds(4 + 120)));

            Assert.IsNotNull(proposal);
            Assert.IsTrue(proposal.Teams.All(t => t.Members.Count == 2));
            CollectionAssert.AreEquivalent(new uint[] { 1, 2, 3, 4 }, AllIds(proposal).ToList());
        }

        [TestMethod]
        public void FullPremadeOfSix_VersusSixSolos_ExcludedBeforePremadeWait_JoinsAfter()
        {
            var waiting = new List<QueueEntrant> { Premade(100, 6, T0) };
            waiting.AddRange(Solos(1, 6, T0.AddSeconds(10)));

            // Window is anchored on the premade (6 players >= min at T0); form at max (12) regardless.
            var before = new BattlegroundMatchmaker().TryForm(waiting, Ctx(T0.AddSeconds(60)));

            Assert.IsNotNull(before, "the six solos alone still form a match");
            CollectionAssert.AreEquivalent(new uint[] { 1, 2, 3, 4, 5, 6 }, AllIds(before).ToList(), Describe(before));

            var after = new BattlegroundMatchmaker().TryForm(waiting, Ctx(T0.AddSeconds(121)));

            Assert.IsNotNull(after);
            Assert.IsTrue(after.Teams.All(t => t.Members.Count == 6));
            var premadeTeam = after.Teams.Single(t => Ids(t).Contains(100u));
            CollectionAssert.AreEquivalent(Enumerable.Range(100, 6).Select(i => (uint)i).ToList(), Ids(premadeTeam).ToList());
        }

        [TestMethod]
        public void PremadeOfThree_VersusSolos_WaitsForPremadeWait()
        {
            var waiting = new List<QueueEntrant> { Premade(100, 3, T0) };
            waiting.AddRange(Solos(1, 3, T0.AddSeconds(1)));

            // A short fill window keeps the window closed so only the premade wait is under test.
            Assert.IsNull(new BattlegroundMatchmaker().TryForm(waiting, Ctx(T0.AddSeconds(119), fillWindow: 30)), "imbalance 3 exceeds tolerance 2 before the wait");

            var after = new BattlegroundMatchmaker().TryForm(waiting, Ctx(T0.AddSeconds(120), fillWindow: 30));

            Assert.IsNotNull(after);
            Assert.IsTrue(after.Teams.All(t => t.Members.Count == 3));
        }

        [TestMethod]
        public void TwoPremadesOfThree_LandOnOppositeTeams()
        {
            var waiting = new List<QueueEntrant> { Premade(100, 3, T0), Premade(200, 3, T0.AddSeconds(1)) };
            waiting.AddRange(Solos(1, 6, T0.AddSeconds(2)));

            var proposal = new BattlegroundMatchmaker().TryForm(waiting, Ctx(T0.AddSeconds(5)));

            Assert.IsNotNull(proposal);
            Assert.AreEqual(6, proposal.Teams[0].Members.Count);
            var a = proposal.Teams.Single(t => Ids(t).Contains(100u));
            var b = proposal.Teams.Single(t => Ids(t).Contains(200u));
            Assert.AreNotSame(a, b, Describe(proposal));
        }

        [TestMethod]
        public void SameIpPair_NeverSplitAcrossTeams_WhenBlocked_AndMaySplitWhenOff()
        {
            // 1 and 2 are the strong pair on one IP; a rating-balanced split would separate them.
            var waiting = new List<QueueEntrant>
            {
                Solo(1, T0, rating: 1800, ip: "A"),
                Solo(2, T0.AddSeconds(1), rating: 1800, ip: "A"),
                Solo(3, T0.AddSeconds(2), rating: 1200),
                Solo(4, T0.AddSeconds(3), rating: 1200)
            };
            var now = T0.AddSeconds(3 + 120);

            var blocked = new BattlegroundMatchmaker().TryForm(waiting, Ctx(now, blockSameIp: true));

            Assert.IsNotNull(blocked);
            var withOne = blocked.Teams.Single(t => Ids(t).Contains(1u));
            Assert.IsTrue(Ids(withOne).Contains(2u), Describe(blocked));

            var open = new BattlegroundMatchmaker().TryForm(waiting, Ctx(now, blockSameIp: false));

            Assert.IsNotNull(open);
            var openWithOne = open.Teams.Single(t => Ids(t).Contains(1u));
            Assert.IsFalse(Ids(openWithOne).Contains(2u), Describe(open));
        }

        [TestMethod]
        public void SameIpPair_WithNoLegalSplit_YieldsNoMatch()
        {
            var waiting = new List<QueueEntrant>
            {
                Solo(1, T0, ip: "A"),
                Solo(2, T0, ip: "A"),
                Solo(3, T0, ip: "A"),
                Solo(4, T0, ip: "B")
            };

            // 3 on A cannot share a team of 2, so every split puts an A across; blocked = null.
            Assert.IsNull(new BattlegroundMatchmaker().TryForm(waiting, Ctx(T0.AddSeconds(120), blockSameIp: true)));
            Assert.IsNotNull(new BattlegroundMatchmaker().TryForm(waiting, Ctx(T0.AddSeconds(120), blockSameIp: false)));
        }

        [TestMethod]
        public void PremadeMembersOnDifferentIps_UsePerMemberKeys()
        {
            var premade = Premade(100, 2, T0, ip: "X", ipKeys: new[] { "X", "B" });
            var withKeys = new List<QueueEntrant> { premade, Solo(1, T0, ip: "B"), Solo(2, T0, ip: "C") };
            var withoutKeys = new List<QueueEntrant> { Premade(100, 2, T0, ip: "X"), Solo(1, T0, ip: "B"), Solo(2, T0, ip: "C") };
            var now = T0.AddSeconds(1 + 120);
            // Fourth player so the queue reaches the minimum.
            withKeys.Add(Solo(3, T0, ip: "D"));
            withoutKeys.Add(Solo(3, T0, ip: "D"));

            // Teams of 2: the premade is one team, so the opposing solos include B (member 2's IP) or not.
            // With per-member keys the premade clashes with solo 1 whichever pair of solos plays; here solos
            // 1,2 are the two oldest and form the opposing team.
            Assert.IsNull(new BattlegroundMatchmaker().TryForm(withKeys, Ctx(now)), "member 2 shares IP B with solo 1");
            Assert.IsNotNull(new BattlegroundMatchmaker().TryForm(withoutKeys, Ctx(now)), "the unit-wide key X alone cannot see the clash");
        }

        [TestMethod]
        public void SameMonarchSolos_AreSplit_WhenEnabled()
        {
            var waiting = new List<QueueEntrant>
            {
                Solo(1, T0, monarch: 7),
                Solo(2, T0.AddSeconds(1), monarch: 7),
                Solo(3, T0.AddSeconds(2)),
                Solo(4, T0.AddSeconds(3))
            };
            var now = T0.AddSeconds(3 + 120);

            var split = new BattlegroundMatchmaker().TryForm(waiting, Ctx(now));

            Assert.IsNotNull(split);
            Assert.IsFalse(Ids(split.Teams.Single(t => Ids(t).Contains(1u))).Contains(2u), Describe(split));

            var together = new BattlegroundMatchmaker().TryForm(waiting, Ctx(now, splitClanmates: false));

            Assert.IsNotNull(together);
            Assert.IsTrue(Ids(together.Teams.Single(t => Ids(t).Contains(1u))).Contains(2u), "with splitting off, ties keep queue order");
        }

        [TestMethod]
        public void RatingBalance_PicksTheCloserSplit()
        {
            var waiting = new List<QueueEntrant>
            {
                Solo(1, T0, rating: 2000),
                Solo(2, T0.AddSeconds(1), rating: 1900),
                Solo(3, T0.AddSeconds(2), rating: 1000),
                Solo(4, T0.AddSeconds(3), rating: 1100)
            };

            var proposal = new BattlegroundMatchmaker().TryForm(waiting, Ctx(T0.AddSeconds(3 + 120)));

            Assert.IsNotNull(proposal);
            var sums = proposal.Teams.Select(t => t.Members.Sum(m => m.RatingAtStart)).ToList();
            Assert.AreEqual(sums[0], sums[1], Describe(proposal));
        }

        [TestMethod]
        public void SameInput_TwiceYieldsTheSameProposal()
        {
            var waiting = Solos(1, 9, T0);
            waiting.Add(Premade(100, 3, T0.AddSeconds(2)));
            var ctx = Ctx(T0.AddSeconds(200));

            var first = new BattlegroundMatchmaker().TryForm(waiting, ctx);
            var second = new BattlegroundMatchmaker().TryForm(waiting.AsEnumerable().Reverse().ToList(), ctx);

            Assert.IsNotNull(first);
            Assert.AreEqual(Describe(first), Describe(second));
        }

        [TestMethod]
        public void NoBgDials_ReturnsNull()
        {
            var ctx = Ctx(T0.AddHours(1)) with { Bg = null };

            Assert.IsNull(new BattlegroundMatchmaker().TryForm(Solos(1, 12, T0), ctx));
        }
    }
}
