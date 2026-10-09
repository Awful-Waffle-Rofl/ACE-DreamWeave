using System;
using System.Linq;

namespace ACE.Server.Pvp.Battlegrounds
{
    /// <summary>
    /// The Attack/Defend win condition (Docs/Pvp/ATTACK-DEFEND.md "Match flow"). Team 0 is the attacker, team 1 the defender. Checked
    /// in this order each tick: (1) an inner <see cref="EliminationWinCondition"/> (a team with no active members loses, forfeit
    /// placement unchanged); (2) the destroyed-crystal count, kept only in <c>ScoreBoard[0]</c>, reaching the crystal count is an
    /// attacker win, <see cref="EndReason.Score"/>; (3) the time limit reached is a defender win, <see cref="EndReason.Timeout"/>,
    /// RATED, never a draw (the owner ruling that overrides the KOTH timeout for this mode). The score check runs before the clock,
    /// so a crystal that falls on the timeout tick is still an attacker win. Before the match is Live only (1) applies. Pure: reads
    /// no clock and no random.
    /// </summary>
    public sealed class CrystalWinCondition : IWinCondition
    {
        /// <summary>The attacking team's index. Also the only <c>ScoreBoard</c> slot this condition reads.</summary>
        public const int AttackerTeam = 0;

        /// <summary>The defending team's index.</summary>
        public const int DefenderTeam = 1;

        private readonly EliminationWinCondition _elimination = new();
        private readonly int _crystalCount;
        private readonly int _timeLimitSeconds;

        public CrystalWinCondition(int crystalCount, int timeLimitSeconds)
        {
            _crystalCount = crystalCount;
            _timeLimitSeconds = timeLimitSeconds;
        }

        public void OnParticipantOut(IMatchView m, PvpParticipant p, ParticipantExit how)
        {
            _elimination.OnParticipantOut(m, p, how);
        }

        public MatchOutcome Evaluate(IMatchView m, DateTime utcNow)
        {
            var eliminated = _elimination.Evaluate(m, utcNow);

            if (eliminated != null)
                return eliminated;

            if (m.LiveSinceUtc == null)
                return null;

            // A crystal count of 0 or less is a misconfigured match, never an instant attacker win.
            if (_crystalCount > 0 && DestroyedOf(m) >= _crystalCount)
                return Win(m, AttackerTeam, EndReason.Score);

            if ((utcNow - m.LiveSinceUtc.Value).TotalSeconds >= _timeLimitSeconds)
                return Win(m, DefenderTeam, EndReason.Timeout);

            return null;
        }

        private static int DestroyedOf(IMatchView m)
        {
            return m is IScoredMatchView s && s.ScoreBoard.TryGetValue(AttackerTeam, out var v) ? v : 0;
        }

        /// <summary>One winner (placement 1), every other team placement 2, rated.</summary>
        private static MatchOutcome Win(IMatchView m, int winner, EndReason reason)
        {
            var placements = m.Teams.Select(t => t.TeamIndex == winner ? 1 : 2).ToArray();

            return new MatchOutcome(new System.Collections.Generic.HashSet<int> { winner }, placements, IsDraw: false, Rated: true, Reason: reason);
        }
    }
}
