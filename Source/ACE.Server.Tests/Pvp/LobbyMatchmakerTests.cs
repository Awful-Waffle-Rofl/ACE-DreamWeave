using System;
using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Pvp;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// FFA lobby sizing (Docs/Pvp/DESIGN.md "LobbyMatchmaker (FFA)"): needed size decays per minute waited
    /// down to the minimum, and at most FfaMaxPlayers are ever taken.
    /// </summary>
    [TestClass]
    public class LobbyMatchmakerTests
    {
        private static readonly DateTime Now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private static QueueEntrant Solo(int id, DateTime queuedAt) =>
            new QueueEntrant(Guid.NewGuid(), new uint[] { (uint)id }, new[] { 1500 }, null, queuedAt);

        private static MatchmakingContext Ctx(DateTime now) => new MatchmakingContext(
            UtcNow: now,
            MmWindowInitial: 100, MmWindowGrowthPerMinute: 50, MmWindowMax: 400,
            DuoVsSoloAfterSeconds: 120,
            FfaTargetPlayers: 10, FfaMinPlayers: 5, FfaMaxPlayers: 15, FfaMinDecaySeconds: 60,
            BlockSameIp: true);

        [TestMethod]
        public void FewerThanTargetAndNoWait_DoesNotForm()
        {
            var waiting = new List<QueueEntrant>();
            for (var i = 0; i < 9; i++)
                waiting.Add(Solo(i, Now));

            var proposal = new LobbyMatchmaker().TryForm(waiting, Ctx(Now));

            Assert.IsNull(proposal);
        }

        [TestMethod]
        public void ExactlyTargetPlayersWaitingImmediately_Forms()
        {
            var waiting = new List<QueueEntrant>();
            for (var i = 0; i < 10; i++)
                waiting.Add(Solo(i, Now));

            var proposal = new LobbyMatchmaker().TryForm(waiting, Ctx(Now));

            Assert.IsNotNull(proposal);
            Assert.AreEqual(10, proposal.Teams.Count);
        }

        [TestMethod]
        public void NeededSizeDecaysOneStepPerMinuteWaited()
        {
            // Only 8 waiting, but the oldest has waited 2 full decay steps (2 minutes at 60s/step),
            // so needed size = max(5, 10 - 2) = 8, which is exactly met.
            var oldestQueuedAt = Now.AddMinutes(-2);

            var waiting = new List<QueueEntrant> { Solo(0, oldestQueuedAt) };
            for (var i = 1; i < 8; i++)
                waiting.Add(Solo(i, Now));

            var proposal = new LobbyMatchmaker().TryForm(waiting, Ctx(Now));

            Assert.IsNotNull(proposal);
            Assert.AreEqual(8, proposal.Teams.Count);
        }

        [TestMethod]
        public void NeededSizeNeverDecaysBelowMinimum()
        {
            // Oldest has waited 100 minutes - decay would drive needed size far below zero without the floor.
            var oldestQueuedAt = Now.AddMinutes(-100);

            var waiting = new List<QueueEntrant> { Solo(0, oldestQueuedAt) };
            for (var i = 1; i < 5; i++)
                waiting.Add(Solo(i, Now));

            var proposal = new LobbyMatchmaker().TryForm(waiting, Ctx(Now));

            Assert.IsNotNull(proposal);
            Assert.AreEqual(5, proposal.Teams.Count);
        }

        [TestMethod]
        public void NeverTakesMoreThanTheMaximum()
        {
            var waiting = new List<QueueEntrant>();
            for (var i = 0; i < 20; i++)
                waiting.Add(Solo(i, Now));

            var proposal = new LobbyMatchmaker().TryForm(waiting, Ctx(Now));

            Assert.IsNotNull(proposal);
            Assert.AreEqual(15, proposal.Teams.Count);
        }

        /// <summary>
        /// Pins LobbyMatchmaker's needed size to ArenaMapCatalog.FfaDecayedTargetSize - the ONE shared FFA decay
        /// formula (Docs/Pvp/DESIGN.md "Arena Crier") - across several wait times, so a future edit to either side
        /// cannot silently diverge from the other. Exactly enough waiting solos are supplied each time so the
        /// match forms iff the helper's own needed size is met, pinning TryForm's actual formed size to the helper.
        /// </summary>
        [TestMethod]
        public void NeededSize_MatchesTheSharedHelper_AcrossSeveralWaitTimes()
        {
            var ctx = Ctx(Now);
            var waitTimes = new[] { TimeSpan.Zero, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(100) };

            foreach (var waited in waitTimes)
            {
                var expectedNeeded = ArenaMapCatalog.FfaDecayedTargetSize(waited, ctx.FfaMinPlayers, ctx.FfaTargetPlayers, ctx.FfaMaxPlayers, ctx.FfaMinDecaySeconds);

                var waiting = new List<QueueEntrant> { Solo(0, Now - waited) };
                for (var i = 1; i < expectedNeeded - 1; i++)
                    waiting.Add(Solo(i, Now));

                var oneShort = new LobbyMatchmaker().TryForm(waiting, ctx);
                Assert.IsNull(oneShort, $"waited={waited}: {expectedNeeded - 1} of {expectedNeeded} needed must not form yet");

                waiting.Add(Solo(expectedNeeded, Now));
                var exact = new LobbyMatchmaker().TryForm(waiting, ctx);

                Assert.IsNotNull(exact, $"waited={waited}: exactly the helper's needed size ({expectedNeeded}) must form");
                Assert.AreEqual(expectedNeeded, exact.Teams.Count, $"waited={waited}: formed size must equal the helper's needed size");
            }
        }
    }
}
