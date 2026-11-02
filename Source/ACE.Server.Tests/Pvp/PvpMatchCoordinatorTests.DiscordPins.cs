using System;
using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Pvp;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// Byte-for-byte pins of the Discord/in-match result line (BuildMatchResultDiscordMessage), whose winner/loser grouping is now shared with the
    /// Arena Crier line. The expected strings were produced by running these same tests against the pre-refactor coordinator (master acfb40e34).
    /// </summary>
    public partial class PvpMatchCoordinatorTests
    {
        private static List<string> CaptureDiscord(Action run)
        {
            var lines = new List<string>();
            PvpMatchCoordinator.QueuePvpForTest = lines.Add;

            try
            {
                run();
            }
            finally
            {
                PvpMatchCoordinator.QueuePvpForTest = null;
            }

            return lines;
        }

        /// <summary>A 2v2 where a winning-team member forfeits: the forfeiter is listed first among the losers, exactly as before.</summary>
        [TestMethod]
        public void DiscordLine_TwoVTwoWinningTeamForfeit_IsUnchanged()
        {
            var lines = CaptureDiscord(() =>
            {
                var rig = new Rig().Build();
                rig.Gateway.Add(A, "Alpha", fellowship: new[] { A, B });
                rig.Gateway.Add(B, "Bravo", fellowship: new[] { A, B });
                rig.Gateway.Add(C, "Charlie");
                rig.Gateway.Add(D, "Delta");
                rig.Coordinator.Join(A, "2v2", true);
                rig.Coordinator.Join(C, "2v2", false);
                rig.Coordinator.Join(D, "2v2", false);
                rig.Clock.Advance(rig.Dials.DuoVsSoloAfterSeconds);
                rig.Tick();
                rig.AcceptAll(A, B, C, D);
                var match = rig.DriveAcceptedToLive(A);

                rig.Logout(B, match.MatchId);
                rig.Tick();
                rig.Death(C, match.MatchId, killer: A);
                rig.Death(D, match.MatchId, killer: A);
                rig.Tick();
            });

            Assert.AreEqual(1, lines.Count);
            Assert.AreEqual("[Arena] 2v2 match: Alpha (Duelist) defeated Bravo (Duelist), Charlie (Duelist), Delta (Duelist).", lines[0]);
        }

        /// <summary>A Tugak Brawl timeout: four survivors share first, one fighter fell. Winners then losers, each in seat order.</summary>
        [TestMethod]
        public void DiscordLine_FfaTimeout_IsUnchanged()
        {
            var lines = CaptureDiscord(() =>
            {
                var rig = FfaRig(5, out var ids);
                rig.Dials = rig.Dials with { FfaTargetPlayers = 5, OvertimeEnabled = false };

                foreach (var id in ids)
                    rig.Coordinator.Join(id, "ffa", false);

                rig.Tick();
                rig.AcceptAll(ids);
                var match = rig.DriveAcceptedToLive(A);

                rig.Death(E, match.MatchId, killer: A);
                rig.Tick();
                rig.Clock.Advance(rig.Dials.TimeLimitSecondsFfa);
                rig.Tick();
            });

            Assert.AreEqual(1, lines.Count);
            Assert.AreEqual("[Arena] Tugak Brawl match: Alpha (Duelist), Bravo (Duelist), Charlie (Duelist), Delta (Duelist) defeated Echo (Duelist).", lines[0]);
        }
    }
}