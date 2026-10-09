using System;
using System.Collections.Generic;

using ACE.Server.Pvp.Battlegrounds;

namespace ACE.Server.Pvp
{
    /// <summary>
    /// The live state of one match. Holds any number of teams (design: "PvpMatch holds List&lt;PvpTeam&gt;
    /// (any number of teams) and a ScoreBoard (per-team int) for future objective modes"). Pure - no live
    /// server types - so it can be built and driven entirely from unit tests.
    /// </summary>
    public sealed class PvpMatch : IMatchContext, IScoredMatchView
    {
        public Guid MatchId { get; }

        public string ModeKey { get; }

        public List<PvpTeam> Teams { get; }

        IReadOnlyList<PvpTeam> IMatchView.Teams => Teams;

        public PvpMatchState State { get; private set; }

        /// <summary>Per-team score, keyed by TeamIndex. Unused by the v1 elimination modes.</summary>
        public Dictionary<int, int> ScoreBoard { get; } = new();

        /// <summary>Per-team kill count, keyed by TeamIndex. Read only by objective (battleground) modes, as the score tiebreak.</summary>
        public Dictionary<int, int> TeamKills { get; } = new();

        private volatile CrystalSequence _crystalSequence;

        /// <summary>
        /// Attack/Defend sequential crystals (Docs/Pvp/ATTACK-DEFEND.md "Sequential crystals"): which crystal is the only vulnerable one, or
        /// null for any-order play and for every other mode. Set once by the coordinator when the match forms, before any player is bound;
        /// the crystal damage gate reads it from landblock threads through the player's binding, with no lock, like <see cref="State"/>.
        /// </summary>
        public CrystalSequence CrystalSequence
        {
            get => _crystalSequence;
            set => _crystalSequence = value;
        }

        private volatile CrystalDefenderReductionDials _defenderReduction;

        /// <summary>
        /// Attack/Defend engaged-defender reduction (Docs/Pvp/ATTACK-DEFEND.md "Engaged defenders"): the four pvp_bg_ad_defender_dr_* settings
        /// as they stood when the match formed, or null for every other mode. Set once by the coordinator with <see cref="CrystalSequence"/>,
        /// before any player is bound; the crystal damage sink reads it from the crystal's landblock thread through the attacker's binding,
        /// with no lock, like <see cref="CrystalSequence"/>.
        /// </summary>
        public CrystalDefenderReductionDials DefenderReduction
        {
            get => _defenderReduction;
            set => _defenderReduction = value;
        }

        public DateTime CreatedAtUtc { get; }

        public DateTime? LiveSinceUtc { get; private set; }

        public PvpMatch(Guid matchId, string modeKey, List<PvpTeam> teams, DateTime createdAtUtc)
        {
            MatchId = matchId;
            ModeKey = modeKey;
            Teams = teams;
            CreatedAtUtc = createdAtUtc;
            State = PvpMatchState.AwaitingAccept;
        }

        public void SetState(PvpMatchState state)
        {
            State = state;
        }

        /// <summary>Marks the match Live at a given time (caller supplies the clock; this type never reads it itself).</summary>
        public void GoLive(DateTime utcNow)
        {
            State = PvpMatchState.Live;
            LiveSinceUtc = utcNow;
        }
    }
}
