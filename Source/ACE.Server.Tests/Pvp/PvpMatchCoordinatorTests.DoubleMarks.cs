using System;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Pvp;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>The arena path is untouched by the Double Marks windows (Docs/Pvp/BATTLEGROUNDS.md "Double Marks windows"): Marks of the Hopeslayer from a battleground are the only thing a window doubles.</summary>
    public partial class PvpMatchCoordinatorTests
    {
        /// <summary>A 1v1 that goes Live at 06:30 Pacific (inside the morning window) pays the ordinary 2 / 1 and its grants carry no Double Marks flag.</summary>
        [TestMethod]
        public void DoubleMarks_ArenaOneVOne_InsideTheWindow_PaysTheOrdinaryAmounts()
        {
            // 2026-09-26 is PDT (UTC-7): 06:30 local is 13:30Z, and the countdown runs before Live.
            var rig = new Rig();
            rig.Clock.Now = new DateTime(2026, 9, 26, 13, 30, 0, DateTimeKind.Utc).AddSeconds(-rig.Dials.CountdownSeconds);
            rig.Build();
            rig.Gateway.Add(A, "Alpha");
            rig.Gateway.Add(B, "Bravo");

            Assert.IsTrue(rig.Coordinator.Join(A, "1v1", false).Joined);
            Assert.IsTrue(rig.Coordinator.Join(B, "1v1", false).Joined);
            rig.Tick();
            rig.AcceptAll(A, B);

            var match = rig.DriveAcceptedToLive(A);

            Assert.AreEqual(new DateTime(2026, 9, 26, 13, 30, 0, DateTimeKind.Utc), match.LiveSinceUtc, "fixture: Live inside the morning window");

            rig.Death(B, match.MatchId, killer: A);
            rig.Tick();

            Assert.AreEqual(PvpMatchState.Resolving, match.State);
            Assert.AreEqual((2, 1), rig.Gateway.Blood(A), "the arena win is the ordinary 2, not doubled");
            Assert.AreEqual((1, 1), rig.Gateway.Blood(B), "the arena loss is the ordinary 1, not doubled");
            Assert.IsTrue(rig.Gateway.BloodGrants.TrueForAll(g => !g.Grant.DoubleMarks));
            Assert.IsTrue(rig.Gateway.BloodGrants.TrueForAll(g => g.Grant.Ledger == PvpBloodLedgerKind.Arena));
        }
    }
}
