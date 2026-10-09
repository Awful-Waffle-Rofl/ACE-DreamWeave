using System.Collections.Generic;

namespace ACE.Server.Pvp
{
    /// <summary>
    /// The result of a finished match, as returned by <see cref="IWinCondition.Evaluate"/> (null means the
    /// match continues). Placements and WinningTeams are indexed by <see cref="PvpTeam.TeamIndex"/>, i.e.
    /// Placements[i] is the finishing place of the team at Teams[i] (1 = best; ties share a value).
    /// </summary>
    public sealed record MatchOutcome(
        IReadOnlySet<int> WinningTeams,
        IReadOnlyList<int> Placements,
        bool IsDraw,
        bool Rated,
        EndReason Reason);
}
