using System;
using System.Collections.Generic;

using ACE.Server.Pvp.Rating;

namespace ACE.Server.Pvp
{
    /// <summary>
    /// The three v1 arena mode definitions (Docs/Pvp/DESIGN.md "Modes" table). Every mode's map pool is
    /// <see cref="ArenaMapCatalog.All"/> (0x0066 and 0x0067); each map carries every mode's spawn set, keyed by
    /// ModeKey. Still inert: nothing reads these until the coordinator lands.
    /// </summary>
    public static class PvpModes
    {
        public static PvpModeDefinition OneVOne(PvpArenaDials dials)
        {
            return new PvpModeDefinition(
                modeKey: ArenaMapCatalog.OneVOneKey,
                ladderKey: "arena_1v1",
                enabledTunableKey: "pvp_arena_1v1_enabled",
                teamCountRange: (2, 2),
                teamSize: 1,
                matchmaker: () => new PairMatchmaker(),
                winCondition: () => new EliminationWinCondition(),
                tickHandler: () => new NoOpMatchTickHandler(),
                respawn: new NoRespawnPolicy(),
                accept: AcceptPolicy.AllOrNothing,
                timeoutRated: false,
                rating: new TeamEloModel(),
                timeLimitTunableKey: "pvp_arena_time_limit_seconds_1v1",
                mapPool: ArenaMapCatalog.All,
                fellowship: TeamFellowshipPolicy.Ignored);
        }

        public static PvpModeDefinition TwoVTwo(PvpArenaDials dials)
        {
            return new PvpModeDefinition(
                modeKey: ArenaMapCatalog.TwoVTwoKey,
                ladderKey: "arena_2v2",
                enabledTunableKey: "pvp_arena_2v2_enabled",
                teamCountRange: (2, 2),
                teamSize: 2,
                matchmaker: () => new DuoMatchmaker(),
                winCondition: () => new EliminationWinCondition(),
                tickHandler: () => new NoOpMatchTickHandler(),
                respawn: new NoRespawnPolicy(),
                accept: AcceptPolicy.AllOrNothing,
                timeoutRated: false,
                rating: new TeamEloModel(),
                timeLimitTunableKey: "pvp_arena_time_limit_seconds_2v2",
                mapPool: ArenaMapCatalog.All,
                fellowship: TeamFellowshipPolicy.PremadeAllowed);
        }

        public static PvpModeDefinition Ffa(PvpArenaDials dials)
        {
            return new PvpModeDefinition(
                modeKey: ArenaMapCatalog.FfaKey,
                ladderKey: "arena_ffa",
                enabledTunableKey: "pvp_arena_ffa_enabled",
                teamCountRange: (dials.FfaMinPlayers, dials.FfaMaxPlayers),
                teamSize: 1,
                matchmaker: () => new LobbyMatchmaker(),
                winCondition: () => new EliminationWinCondition(),
                tickHandler: () => new NoOpMatchTickHandler(),
                respawn: new NoRespawnPolicy(),
                accept: AcceptPolicy.ProceedIfAtLeast(dials.FfaMinPlayers),
                timeoutRated: true,
                rating: new PairwisePlacementEloModel(dials.RatingKFfa),
                timeLimitTunableKey: "pvp_arena_time_limit_seconds_ffa",
                mapPool: ArenaMapCatalog.All,
                fellowship: TeamFellowshipPolicy.Ignored);
        }

        public static IReadOnlyList<PvpModeDefinition> All(PvpArenaDials dials)
        {
            return new List<PvpModeDefinition> { OneVOne(dials), TwoVTwo(dials), Ffa(dials) };
        }
    }
}
