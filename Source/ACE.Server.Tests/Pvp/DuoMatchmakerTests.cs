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

        private static MatchmakingContext Ctx(DateTime now) => new MatchmakingContext(
            UtcNow: now,
            MmWindowInitial: 100, MmWindowGrowthPerMinute: 50, MmWindowMax: 400,
            DuoVsSoloAfterSeconds: 120,
            FfaTargetPlayers: 10, FfaMinPlayers: 5, FfaMaxPlayers: 15, FfaMinDecaySeconds: 60,
            BlockSameIp: true);

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
    }
}
