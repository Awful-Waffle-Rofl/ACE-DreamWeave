using System.Collections.Generic;

namespace ACE.Server.Pvp
{
    /// <summary>
    /// One side of a <see cref="PvpMatch"/>. TeamIndex is the position in <see cref="PvpMatch.Teams"/>
    /// and is also the index used by <see cref="MatchOutcome.Placements"/> and <see cref="MatchOutcome.WinningTeams"/>.
    /// </summary>
    public sealed class PvpTeam
    {
        public int TeamIndex { get; }

        public List<PvpParticipant> Members { get; }

        public PvpTeam(int teamIndex, List<PvpParticipant> members)
        {
            TeamIndex = teamIndex;
            Members = members;
        }
    }
}
