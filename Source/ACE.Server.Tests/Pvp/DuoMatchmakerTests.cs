using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Pvp;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// 2v2 matchmaking (Docs/Pvp/DESIGN.md "Modes" table): duo-vs-duo is preferred, duo-vs-two-solos only
    /// after the wait, and same-IP blocking.
    /// </summary>
    [TestClass]
    public class DuoMatchmakerTests
    {
        private static readonly DateTime Now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private static QueueEntrant Duo(uint a, uint b, DateTime queuedAt, string ip = null) =>
            new QueueEntrant(Guid.NewGuid(), new[] { a, b }, new[] { 1500, 1500 }, ip, queuedAt);

        private static QueueEntrant Solo(uint id, DateTime queuedAt, string ip = null) =>
            new QueueEntrant(Guid.NewGuid(), new[] { id }, new[] { 1500 }, ip, queuedAt);

        private static MatchmakingContext Ctx(DateTime now, bool blockSameIp = true, Func<int, int> pickIndex = null) => new MatchmakingContext(
            UtcNow: now,
            MmWindowInitial: 100, MmWindowGrowthPerMinute: 50, MmWindowMax: 400,
            DuoVsSoloAfterSeconds: 120,
            FfaTargetPlayers: 10, FfaMinPlayers: 5, FfaMaxPlayers: 15, FfaMinDecaySeconds: 60,
            BlockSameIp: blockSameIp,
            PickIndex: pickIndex);

        private static List<uint[]> TeamIds(MatchProposal p) =>
            p.Teams.Select(t => t.Members.Select(m => m.CharacterId).OrderBy(x => x).ToArray()).ToList();

        private static bool SameTeam(MatchProposal p, uint a, uint b) =>
            p.Teams.Any(t => t.Members.Any(m => m.CharacterId == a) && t.Members.Any(m => m.CharacterId == b));

        [TestMethod]
        public void TwoDuosWaiting_PrefersDuoVsDuo_EvenWithSolosAlsoWaiting()
        {
            var waiting = new List<QueueEntrant>
            {
                Duo(1, 2, Now),
                Duo(3, 4, Now),
                Solo(5, Now.AddMinutes(-10)),
                Solo(6, Now.AddMinutes(-10))
            };

            var proposal = new DuoMatchmaker().TryForm(waiting, Ctx(Now));

            Assert.IsNotNull(proposal);
            Assert.IsTrue(proposal.Teams.All(t => t.Members.Count == 2));
            var allIds = proposal.Teams.SelectMany(t => t.Members.Select(m => m.CharacterId)).ToList();
            CollectionAssert.AreEquivalent(new uint[] { 1, 2, 3, 4 }, allIds);
        }

        [TestMethod]
        public void OneDuoWaiting_NoOtherDuo_BeforeWait_DoesNotMatchSolos()
        {
            var waiting = new List<QueueEntrant>
            {
                Duo(1, 2, Now),
                Solo(3, Now),
                Solo(4, Now)
            };

            var proposal = new DuoMatchmaker().TryForm(waiting, Ctx(Now));

            Assert.IsNull(proposal);
        }

        [TestMethod]
        public void OneDuoWaiting_NoOtherDuo_AfterWait_MatchesTwoSolos()
        {
            var waiting = new List<QueueEntrant>
            {
                Duo(1, 2, Now.AddSeconds(-120)),
                Solo(3, Now),
                Solo(4, Now)
            };

            var proposal = new DuoMatchmaker().TryForm(waiting, Ctx(Now));

            Assert.IsNotNull(proposal);
            var soloTeam = proposal.Teams.Single(t => t.Members.All(m => m.CharacterId == 3 || m.CharacterId == 4));
            Assert.AreEqual(2, soloTeam.Members.Count);
        }

        [TestMethod]
        public void SameIpDuos_NotMatchedAgainstEachOther()
        {
            var waiting = new List<QueueEntrant>
            {
                Duo(1, 2, Now, ip: "1.2.3.4"),
                Duo(3, 4, Now, ip: "1.2.3.4")
            };

            var proposal = new DuoMatchmaker().TryForm(waiting, Ctx(Now));

            Assert.IsNull(proposal);
        }

        [TestMethod]
        public void FourSolos_NoDuo_FormTwoTeamsOfTwo_EachSoloOnce()
        {
            var waiting = new List<QueueEntrant> { Solo(1, Now), Solo(2, Now), Solo(3, Now), Solo(4, Now) };

            var proposal = new DuoMatchmaker().TryForm(waiting, Ctx(Now));

            Assert.IsNotNull(proposal);
            Assert.IsTrue(proposal.Rated);
            Assert.AreEqual(2, proposal.Teams.Count);
            Assert.IsTrue(proposal.Teams.All(t => t.Members.Count == 2));
            CollectionAssert.AreEquivalent(new uint[] { 1, 2, 3, 4 }, proposal.Teams.SelectMany(t => t.Members.Select(m => m.CharacterId)).ToList());
        }

        [TestMethod]
        public void ThreeSolos_NoMatch()
        {
            var waiting = new List<QueueEntrant> { Solo(1, Now), Solo(2, Now), Solo(3, Now) };

            Assert.IsNull(new DuoMatchmaker().TryForm(waiting, Ctx(Now)));
        }

        [TestMethod]
        public void DuoBeforeWait_PlusFourSolos_SolosMatchEachOther_DuoLeftOut()
        {
            var waiting = new List<QueueEntrant>
            {
                Duo(1, 2, Now),
                Solo(3, Now), Solo(4, Now), Solo(5, Now), Solo(6, Now)
            };

            var proposal = new DuoMatchmaker().TryForm(waiting, Ctx(Now));

            Assert.IsNotNull(proposal);
            var ids = proposal.Teams.SelectMany(t => t.Members.Select(m => m.CharacterId)).ToList();
            CollectionAssert.AreEquivalent(new uint[] { 3, 4, 5, 6 }, ids);
        }

        [TestMethod]
        public void DuoPastWait_PlusFourSolos_DuoVsTwoSolosWins()
        {
            var waiting = new List<QueueEntrant>
            {
                Duo(1, 2, Now.AddSeconds(-120)),
                Solo(3, Now.AddSeconds(-10)), Solo(4, Now.AddSeconds(-10)), Solo(5, Now), Solo(6, Now)
            };

            var proposal = new DuoMatchmaker().TryForm(waiting, Ctx(Now));

            Assert.IsNotNull(proposal);
            var duoTeam = proposal.Teams.Single(t => t.Members.Any(m => m.CharacterId == 1));
            CollectionAssert.AreEquivalent(new uint[] { 1, 2 }, duoTeam.Members.Select(m => m.CharacterId).ToList());
            var soloTeam = proposal.Teams.Single(t => t != duoTeam);
            CollectionAssert.AreEquivalent(new uint[] { 3, 4 }, soloTeam.Members.Select(m => m.CharacterId).ToList());
        }

        [TestMethod]
        public void TwoDuos_PlusFourSolos_DuoVsDuoWins()
        {
            var waiting = new List<QueueEntrant>
            {
                Solo(5, Now.AddMinutes(-10)), Solo(6, Now.AddMinutes(-10)), Solo(7, Now.AddMinutes(-10)), Solo(8, Now.AddMinutes(-10)),
                Duo(1, 2, Now),
                Duo(3, 4, Now)
            };

            var proposal = new DuoMatchmaker().TryForm(waiting, Ctx(Now));

            Assert.IsNotNull(proposal);
            CollectionAssert.AreEquivalent(new uint[] { 1, 2, 3, 4 }, proposal.Teams.SelectMany(t => t.Members.Select(m => m.CharacterId)).ToList());
        }

        [TestMethod]
        public void FourSolos_BlockSameIp_SameIpPairAreTeammates()
        {
            var waiting = new List<QueueEntrant>
            {
                Solo(1, Now, ip: "9.9.9.9"), Solo(2, Now, ip: "9.9.9.9"), Solo(3, Now, ip: "1.1.1.1"), Solo(4, Now, ip: "2.2.2.2")
            };

            // Every possible draw must keep 1 and 2 together: the only valid split is (1,2 | 3,4).
            for (var pick = 0; pick < 3; pick++)
            {
                var p = pick;
                var proposal = new DuoMatchmaker().TryForm(waiting, Ctx(Now, pickIndex: n => Math.Min(p, n - 1)));

                Assert.IsNotNull(proposal);
                Assert.IsTrue(SameTeam(proposal, 1, 2));
                Assert.IsTrue(SameTeam(proposal, 3, 4));
            }
        }

        [TestMethod]
        public void FourSolos_BlockSameIp_ThreeShareOneIp_NoValidSplit()
        {
            var waiting = new List<QueueEntrant>
            {
                Solo(1, Now, ip: "9.9.9.9"), Solo(2, Now, ip: "9.9.9.9"), Solo(3, Now, ip: "9.9.9.9"), Solo(4, Now, ip: "1.1.1.1")
            };

            Assert.IsNull(new DuoMatchmaker().TryForm(waiting, Ctx(Now)));
        }

        [TestMethod]
        public void FourSolos_BlockSameIpOff_SameIpSplitAllowed()
        {
            var waiting = new List<QueueEntrant>
            {
                Solo(1, Now, ip: "9.9.9.9"), Solo(2, Now, ip: "9.9.9.9"), Solo(3, Now, ip: "9.9.9.9"), Solo(4, Now, ip: "9.9.9.9")
            };

            Assert.IsNotNull(new DuoMatchmaker().TryForm(waiting, Ctx(Now, blockSameIp: false)));
        }

        [TestMethod]
        public void FourSolos_InjectedPick_SelectsExactSplit_AndDifferentPicksDiffer()
        {
            var waiting = new List<QueueEntrant> { Solo(1, Now), Solo(2, Now), Solo(3, Now), Solo(4, Now) };
            var asked = new List<int>();

            var first = new DuoMatchmaker().TryForm(waiting, Ctx(Now, pickIndex: n => { asked.Add(n); return 0; }));
            var second = new DuoMatchmaker().TryForm(waiting, Ctx(Now, pickIndex: n => n == 3 ? 1 : 0));
            var third = new DuoMatchmaker().TryForm(waiting, Ctx(Now, pickIndex: n => n == 3 ? 2 : 0));

            CollectionAssert.AreEqual(new[] { 3, 2 }, asked, "one draw for the split, one for the side");
            Assert.IsTrue(SameTeam(first, 1, 2) && SameTeam(first, 3, 4));
            Assert.IsTrue(SameTeam(second, 1, 3) && SameTeam(second, 2, 4));
            Assert.IsTrue(SameTeam(third, 1, 4) && SameTeam(third, 2, 3));
        }

        [TestMethod]
        public void FourSolos_DefaultRandom_ProducesMoreThanOneSplit()
        {
            var waiting = new List<QueueEntrant> { Solo(1, Now), Solo(2, Now), Solo(3, Now), Solo(4, Now) };
            var partners = new HashSet<uint>();

            for (var i = 0; i < 200; i++)
            {
                var proposal = new DuoMatchmaker().TryForm(waiting, Ctx(Now));
                var team = proposal.Teams.Single(t => t.Members.Any(m => m.CharacterId == 1));
                partners.Add(team.Members.Single(m => m.CharacterId != 1).CharacterId);
            }

            Assert.IsTrue(partners.SetEquals(new uint[] { 2, 3, 4 }), "solo 1 should be teamed with each of the others across 200 draws");
        }

        [TestMethod]
        public void FourSolos_SideDraw_DecidesWhichTeamIsTeamZero()
        {
            var waiting = new List<QueueEntrant> { Solo(1, Now), Solo(2, Now), Solo(3, Now), Solo(4, Now) };

            var keep = new DuoMatchmaker().TryForm(waiting, Ctx(Now, pickIndex: n => 0));
            var swap = new DuoMatchmaker().TryForm(waiting, Ctx(Now, pickIndex: n => n == 3 ? 0 : 1));

            CollectionAssert.AreEquivalent(new uint[] { 1, 2 }, keep.Teams[0].Members.Select(m => m.CharacterId).ToList());
            CollectionAssert.AreEquivalent(new uint[] { 3, 4 }, swap.Teams[0].Members.Select(m => m.CharacterId).ToList());
            Assert.AreEqual(0, swap.Teams[0].TeamIndex);
            Assert.AreEqual(1, swap.Teams[1].TeamIndex);
        }

        [TestMethod]
        public void FourSolos_DefaultRandom_BothSideOrderingsOccur()
        {
            var waiting = new List<QueueEntrant> { Solo(1, Now), Solo(2, Now), Solo(3, Now), Solo(4, Now) };
            var teamZeroHasOne = new HashSet<bool>();

            for (var i = 0; i < 200; i++)
            {
                var proposal = new DuoMatchmaker().TryForm(waiting, Ctx(Now));
                teamZeroHasOne.Add(proposal.Teams[0].Members.Any(m => m.CharacterId == 1));
            }

            Assert.AreEqual(2, teamZeroHasOne.Count, "solo 1 should land on team 0 sometimes and team 1 sometimes");
        }

        [TestMethod]
        public void ThirtySameIpSolos_BlockSameIp_NoMatch()
        {
            var waiting = Enumerable.Range(1, 30).Select(i => Solo((uint)i, Now.AddSeconds(i), ip: "9.9.9.9")).ToList();

            Assert.IsNull(new DuoMatchmaker().TryForm(waiting, Ctx(Now)));
        }

        [TestMethod]
        public void FourSolos_BeyondTheScanWindow_AreNotConsidered()
        {
            // 16 older solos share one IP (no valid split among them); four distinct-IP solos queue behind them, past the window.
            var waiting = Enumerable.Range(1, DuoMatchmaker.MaxSolosScanned).Select(i => Solo((uint)i, Now.AddSeconds(i), ip: "9.9.9.9")).ToList();
            waiting.AddRange(Enumerable.Range(101, 4).Select(i => Solo((uint)i, Now.AddMinutes(1).AddSeconds(i), ip: "10.0." + i)));

            Assert.IsNull(new DuoMatchmaker().TryForm(waiting, Ctx(Now)));
        }

        [TestMethod]
        public void FourSolos_InsideTheScanWindow_AreConsidered()
        {
            // Control: with 12 same-IP solos ahead, the four distinct-IP solos land at positions 13-16, inside the window.
            var waiting = Enumerable.Range(1, DuoMatchmaker.MaxSolosScanned - 4).Select(i => Solo((uint)i, Now.AddSeconds(i), ip: "9.9.9.9")).ToList();
            waiting.AddRange(Enumerable.Range(101, 4).Select(i => Solo((uint)i, Now.AddMinutes(1).AddSeconds(i), ip: "10.0." + i)));

            Assert.IsNotNull(new DuoMatchmaker().TryForm(waiting, Ctx(Now)));
        }
    }
}
