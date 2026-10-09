using System;
using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Pvp;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>1v1 matchmaking (Docs/Pvp/DESIGN.md "Modes" table): the widening rating window, and same-IP blocking.</summary>
    [TestClass]
    public class PairMatchmakerTests
    {
        private static readonly DateTime Now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private static QueueEntrant Solo(int id, int rating, DateTime queuedAt, string ip = null) =>
            new QueueEntrant(Guid.NewGuid(), new uint[] { (uint)id }, new[] { rating }, ip, queuedAt);

        private static MatchmakingContext Ctx(DateTime now) => new MatchmakingContext(
            UtcNow: now,
            MmWindowInitial: 100, MmWindowGrowthPerMinute: 50, MmWindowMax: 400,
            DuoVsSoloAfterSeconds: 120,
            FfaTargetPlayers: 10, FfaMinPlayers: 5, FfaMaxPlayers: 15, FfaMinDecaySeconds: 60,
            BlockSameIp: true);

        [TestMethod]
        public void WithinInitialWindow_Matches()
        {
            var waiting = new List<QueueEntrant>
            {
                Solo(1, 1500, Now),
                Solo(2, 1580, Now) // within the initial 100-point window
            };

            var proposal = new PairMatchmaker().TryForm(waiting, Ctx(Now));

            Assert.IsNotNull(proposal);
        }

        [TestMethod]
        public void OutsideInitialWindow_NoWait_DoesNotMatch()
        {
            var waiting = new List<QueueEntrant>
            {
                Solo(1, 1500, Now),
                Solo(2, 1650, Now) // 150 points apart, outside the initial 100-point window
            };

            var proposal = new PairMatchmaker().TryForm(waiting, Ctx(Now));

            Assert.IsNull(proposal);
        }

        [TestMethod]
        public void WindowWidensWithWaitTime_EventuallyMatches()
        {
            // 150 points apart: not matchable at t=0 (window 100), but is once the oldest has waited
            // one minute (window 100 + 50 = 150).
            var oldestQueuedAt = Now.AddMinutes(-1);

            var waiting = new List<QueueEntrant>
            {
                Solo(1, 1500, oldestQueuedAt),
                Solo(2, 1650, Now)
            };

            var proposal = new PairMatchmaker().TryForm(waiting, Ctx(Now));

            Assert.IsNotNull(proposal);
        }

        [TestMethod]
        public void SameIp_NeverMatchedRegardlessOfRating()
        {
            var waiting = new List<QueueEntrant>
            {
                Solo(1, 1500, Now, ip: "1.2.3.4"),
                Solo(2, 1500, Now, ip: "1.2.3.4")
            };

            var proposal = new PairMatchmaker().TryForm(waiting, Ctx(Now));

            Assert.IsNull(proposal);
        }

        [TestMethod]
        public void DifferentIp_EqualRating_Matches()
        {
            var waiting = new List<QueueEntrant>
            {
                Solo(1, 1500, Now, ip: "1.2.3.4"),
                Solo(2, 1500, Now, ip: "5.6.7.8")
            };

            var proposal = new PairMatchmaker().TryForm(waiting, Ctx(Now));

            Assert.IsNotNull(proposal);
        }
    }
}
