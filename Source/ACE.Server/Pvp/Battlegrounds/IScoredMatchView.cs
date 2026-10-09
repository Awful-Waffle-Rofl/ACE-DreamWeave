using System.Collections.Generic;

namespace ACE.Server.Pvp.Battlegrounds
{
    /// <summary>
    /// A match view that also exposes the per-team score and kill counters, the two things
    /// <see cref="ScoreWinCondition"/> reads. <see cref="PvpMatch"/> and <see cref="IBattlegroundMatchContext"/> implement it;
    /// a plain <see cref="IMatchView"/> that does not scores as all zeros.
    /// </summary>
    public interface IScoredMatchView : IMatchView
    {
        /// <summary>Per-team score, keyed by TeamIndex.</summary>
        Dictionary<int, int> ScoreBoard { get; }

        /// <summary>Per-team kill count, keyed by TeamIndex. The score tiebreak.</summary>
        Dictionary<int, int> TeamKills { get; }
    }
}