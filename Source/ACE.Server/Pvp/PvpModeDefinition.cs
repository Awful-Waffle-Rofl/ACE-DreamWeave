using System;
using System.Collections.Generic;

namespace ACE.Server.Pvp
{
    /// <summary>
    /// Everything that can differ between arena (and later, battleground) modes: team size, team count
    /// range, matchmaker, accept policy, rating model, ladder, time limit, map pool and TimeoutRated.
    /// Everything else is shared - see Docs/Pvp/DESIGN.md "Must stay identical".
    /// </summary>
    public sealed class PvpModeDefinition
    {
        public string ModeKey { get; }

        public string LadderKey { get; }

        public string EnabledTunableKey { get; }

        public (int Min, int Max) TeamCountRange { get; }

        public int TeamSize { get; }

        public Func<IMatchmaker> Matchmaker { get; }

        public Func<IWinCondition> WinCondition { get; }

        public Func<IMatchTickHandler> TickHandler { get; }

        public IRespawnPolicy Respawn { get; }

        public AcceptPolicy Accept { get; }

        /// <summary>Whether a timeout at the mode's time limit still counts toward rating (false = unrated draw).</summary>
        public bool TimeoutRated { get; }

        public IRatingModel Rating { get; }

        public string TimeLimitTunableKey { get; }

        public IReadOnlyList<ArenaMap> MapPool { get; }

        public TeamFellowshipPolicy Fellowship { get; }

        private readonly string roomKey;

        /// <summary>
        /// The queue room this mode is joined through. Defaults to <see cref="ModeKey"/> (every arena mode has its own
        /// room); a battleground mode shares the one "bg" room. Docs/Pvp/BATTLEGROUNDS.md "Matchmaking".
        /// </summary>
        public string RoomKey => roomKey ?? ModeKey;

        /// <summary>True for an objective (score) mode: the coordinator skips overtime and the elimination timeout cast.</summary>
        public bool IsObjective { get; }

        /// <summary>Whether the mode goes to overtime at its time limit. Arena modes do; objective modes ignore it.</summary>
        public bool UsesOvertime { get; }

        /// <summary>Whether a finished match of this mode pays arena Blood (PayArenaBlood). Objective (battleground) modes are false and pay Marks through PayBattlegroundMarks instead.</summary>
        public bool PaysBlood { get; }

        /// <summary>
        /// PvP Template Facets (Docs/Pvp/TEMPLATES.md "Scope"): whether a match of this mode is fought on templates.
        /// TRUE BY DEFAULT for every mode, arena and battleground alike (owner ruling 2026-10-03): a new mode is
        /// templated unless its definition explicitly passes templated: false. A templated mode needs a template at
        /// join, re-checks it at accept and dispatch, freezes it onto the bindings, applies it before the teleport,
        /// forces the arena masks on and does not require the PK facet; an opted-out mode is placed with the
        /// player's own build, the pvp_arena_suppress_* dials and the PK facet requirement.
        /// </summary>
        public bool Templated { get; }

        public PvpModeDefinition(
            string modeKey,
            string ladderKey,
            string enabledTunableKey,
            (int Min, int Max) teamCountRange,
            int teamSize,
            Func<IMatchmaker> matchmaker,
            Func<IWinCondition> winCondition,
            Func<IMatchTickHandler> tickHandler,
            IRespawnPolicy respawn,
            AcceptPolicy accept,
            bool timeoutRated,
            IRatingModel rating,
            string timeLimitTunableKey,
            IReadOnlyList<ArenaMap> mapPool,
            TeamFellowshipPolicy fellowship,
            string roomKey = null,
            bool isObjective = false,
            bool usesOvertime = true,
            bool paysBlood = true,
            bool templated = true)
        {
            ModeKey = modeKey;
            LadderKey = ladderKey;
            EnabledTunableKey = enabledTunableKey;
            TeamCountRange = teamCountRange;
            TeamSize = teamSize;
            Matchmaker = matchmaker;
            WinCondition = winCondition;
            TickHandler = tickHandler;
            Respawn = respawn;
            Accept = accept;
            TimeoutRated = timeoutRated;
            Rating = rating;
            TimeLimitTunableKey = timeLimitTunableKey;
            MapPool = mapPool;
            Fellowship = fellowship;
            this.roomKey = roomKey;
            IsObjective = isObjective;
            UsesOvertime = usesOvertime;
            PaysBlood = paysBlood;
            Templated = templated;
        }
    }
}
