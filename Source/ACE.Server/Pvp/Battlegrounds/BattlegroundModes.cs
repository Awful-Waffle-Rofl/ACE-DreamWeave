using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.Pvp.Rating;

namespace ACE.Server.Pvp.Battlegrounds
{
    /// <summary>
    /// The battleground mode, room and ladder identifiers (Docs/Pvp/BATTLEGROUNDS.md "Invariants for the step 2
    /// fan-out"). Every reference elsewhere uses these constants; none retypes the string. Battleground modes are
    /// deliberately NOT in <see cref="PvpModes.All"/>, which PvpModesMapPoolTests pins to the arena maps.
    /// </summary>
    public static class BattlegroundModes
    {
        /// <summary>The King of the Hill mode key.</summary>
        public const string KothModeKey = "bg_koth";

        /// <summary>The Attack/Defend mode key (Docs/Pvp/ATTACK-DEFEND.md).</summary>
        public const string AttackDefendModeKey = "bg_ad";

        /// <summary>Every battleground mode key starts with this prefix (bg_koth); no arena mode key (1v1, 2v2, ffa) does.</summary>
        public const string ModeKeyPrefix = "bg_";

        /// <summary>
        /// TRUE for a battleground mode key (starts with <see cref="ModeKeyPrefix"/>), FALSE for an arena mode key or null.
        /// What <see cref="ACE.Server.Pvp.Rules.PvpClassifier"/> uses to tell a Battleground match from an Arena one.
        /// </summary>
        public static bool IsBattlegroundModeKey(string modeKey)
            => modeKey != null && modeKey.StartsWith(ModeKeyPrefix, StringComparison.Ordinal);

        /// <summary>The one queue room every battleground mode is joined through (/arena join bg).</summary>
        public const string RoomKey = "bg";

        /// <summary>The one team-Elo ladder shared by every battleground mode.</summary>
        public const string LadderKey = "battleground";

        /// <summary>The time-limit setting King of the Hill reads.</summary>
        public const string KothTimeLimitTunableKey = "pvp_bg_time_limit_seconds_koth";

        /// <summary>The per-mode switch King of the Hill reads.</summary>
        public const string KothEnabledTunableKey = "pvp_bg_koth_enabled";

        /// <summary>The time-limit setting Attack/Defend reads.</summary>
        public const string AdTimeLimitTunableKey = "pvp_bg_time_limit_seconds_ad";

        /// <summary>The per-mode switch Attack/Defend reads.</summary>
        public const string AdEnabledTunableKey = "pvp_bg_ad_enabled";

        /// <summary>
        /// King of the Hill (Docs/Pvp/BATTLEGROUNDS.md "King of the Hill", "Winning", "Respawn"), with every value read
        /// from <paramref name="d"/> at the moment the match is formed: the win condition's target and limit, the zone
        /// radius and height, and the pen time. Map bg_016c; the zone centre comes from its layout.
        /// </summary>
        public static PvpModeDefinition Koth(BattlegroundDials d)
        {
            var zone = KothZoneFor(BattlegroundMapCatalog.Bg016c, d);

            return new PvpModeDefinition(
                modeKey: KothModeKey,
                ladderKey: LadderKey,
                enabledTunableKey: KothEnabledTunableKey,
                teamCountRange: (2, 2),
                // The LARGEST team a battleground holds; the matchmaker chooses the actual size per match (2 to 6).
                teamSize: BattlegroundTunables.AbsoluteMaxPremadeSize,
                matchmaker: () => new BattlegroundMatchmaker(),
                winCondition: () => new ScoreWinCondition(d.KothScoreTarget, d.TimeLimitSecondsKoth),
                // The coordinator binds the match's resolved layout when it forms the match (KothTickHandler.BindLayout), so the scored
                // zone and the marker ring share one layout.
                tickHandler: () => new KothTickHandler(d, zone),
                respawn: new BattlegroundRespawnPolicy(d.RespawnSeconds),
                accept: AcceptPolicy.AllOrNothing,
                // Inert for an objective mode: only the arena timeout path (EliminationWinCondition.ResolveTimeout) reads
                // it; ScoreWinCondition rates every outcome, a timeout draw included.
                timeoutRated: true,
                rating: new TeamEloModel(),
                timeLimitTunableKey: KothTimeLimitTunableKey,
                mapPool: MapPoolFor(KothModeKey),
                // Premades queue as before, and each team is put in a server-built fellowship at Countdown
                // (Docs/Pvp/BATTLEGROUNDS.md "Team fellowships").
                fellowship: TeamFellowshipPolicy.TeamFellowship,
                roomKey: RoomKey,
                isObjective: true,
                // Inert for an objective mode: AdvanceLive's objective branch (AdvanceObjectiveLive) never reads UsesOvertime.
                usesOvertime: false,
                paysBlood: false);
        }

        /// <summary>
        /// Attack/Defend (Docs/Pvp/ATTACK-DEFEND.md "Match flow"), with every value read from <paramref name="d"/> at the moment the match is
        /// formed: the crystal win condition (every crystal against the time limit), the per-side respawn delays and the alert and status
        /// cadence. Team 0 is the attacker. The win condition and the placeholder tick handler are sized for the FIRST map in the mode's pool
        /// (its effective crystal count; one attacker); the coordinator rebinds the handler's plan to the formed match's own layout and
        /// attacker count (<see cref="AttackDefendTickHandler.BindPlan"/>), the way King of the Hill rebinds its layout.
        /// </summary>
        public static PvpModeDefinition AttackDefend(BattlegroundDials d)
        {
            var pool = MapPoolFor(AttackDefendModeKey);
            var layout = pool.Count > 0 ? BattlegroundMapCatalog.Find(pool[0].MapKey) : null;
            var crystalCount = AttackDefendPlan.EffectiveCount(layout, d);

            return new PvpModeDefinition(
                modeKey: AttackDefendModeKey,
                ladderKey: LadderKey,
                enabledTunableKey: AdEnabledTunableKey,
                teamCountRange: (2, 2),
                teamSize: BattlegroundTunables.AbsoluteMaxPremadeSize,
                matchmaker: () => new BattlegroundMatchmaker(),
                winCondition: () => new CrystalWinCondition(crystalCount, d.TimeLimitSecondsAd),
                tickHandler: () => new AttackDefendTickHandler(d, layout == null ? null : AttackDefendPlan.Build(layout, d, 1)),
                respawn: new SidedRespawnPolicy(d.AdRespawnSecondsAttackers, d.AdRespawnSecondsDefenders),
                accept: AcceptPolicy.AllOrNothing,
                // A timeout is a RATED defender win in this mode, never an unrated draw (owner ruling 3).
                timeoutRated: true,
                rating: new TeamEloModel(),
                timeLimitTunableKey: AdTimeLimitTunableKey,
                mapPool: pool,
                fellowship: TeamFellowshipPolicy.TeamFellowship,
                roomKey: RoomKey,
                isObjective: true,
                // Inert for an objective mode: AdvanceLive's objective branch never reads UsesOvertime.
                usesOvertime: false,
                paysBlood: false);
        }

        /// <summary>
        /// The space-seam maps a mode is played on, DERIVED from the catalogue: every battleground layout whose
        /// <see cref="BattlegroundLayout.Modes"/> names <paramref name="modeKey"/>, in <see cref="BattlegroundMapCatalog.All"/> order. Never
        /// hand-listed, so a map joins a mode by declaring it. Empty for a key no layout names.
        /// </summary>
        public static IReadOnlyList<ArenaMap> MapPoolFor(string modeKey)
        {
            return BattlegroundMapCatalog.All
                .Where(l => l.Modes.Contains(modeKey))
                .Select(l => BattlegroundMapCatalog.SpaceMapOf(l))
                .ToList()
                .AsReadOnly();
        }

        /// <summary>
        /// The King of the Hill control zone for a layout and a dials snapshot: the layout's centre, the snapshot's radius
        /// and height. The ONE place the zone is built, so the scoring cylinder (<see cref="Koth"/>, handed to
        /// <see cref="KothTickHandler"/>) and the zone-marker ring (<see cref="BattlegroundZoneMarkers.Plan"/>, planned at
        /// match formation) can never disagree when both are given the same snapshot. Pure.
        /// </summary>
        public static KothZone KothZoneFor(BattlegroundLayout layout, BattlegroundDials d)
            => new KothZone(layout.ZoneX, layout.ZoneY, layout.ZoneZ, d.KothZoneRadius, d.KothZoneHeight);

        /// <summary>The zone for one hill site of a layout (see <see cref="BattlegroundLayout.SiteFor"/>): that site's centre, the snapshot's radius and height.</summary>
        public static KothZone KothZoneFor(BattlegroundLayout layout, BattlegroundDials d, KothSiteChoice site)
        {
            var (x, y) = layout.SiteFor(site);

            return new KothZone(x, y, layout.ZoneZ, d.KothZoneRadius, d.KothZoneHeight);
        }

        /// <summary>
        /// Every battleground mode definition, built from one dials snapshot, the battleground counterpart of
        /// <see cref="PvpModes.All"/>: a definition snapshots its dials (score target, time limit, zone, respawn delay),
        /// so it is built per matchmaking pass, never cached. Includes disabled modes; the coordinator filters by
        /// <see cref="IsEnabled"/>.
        /// </summary>
        public static IReadOnlyList<PvpModeDefinition> All(BattlegroundDials dials) => new[] { Koth(dials), AttackDefend(dials) };

        /// <summary>
        /// Whether a battleground mode may form a match: pvp_bg_enabled and the mode's own switch. pvp_arena_enabled sits
        /// above both and is the caller's check. An unknown key is disabled.
        /// </summary>
        public static bool IsEnabled(string modeKey, BattlegroundDials d)
        {
            if (d == null || !d.Enabled)
                return false;

            switch (modeKey)
            {
                case KothModeKey: return d.KothEnabled;
                case AttackDefendModeKey: return d.AdEnabled;
                default: return false;
            }
        }

        /// <summary>
        /// The failure for a mode key no switch here names. The lookups below used to answer King of the Hill's value for ANY other key,
        /// so a new mode that forgot its case silently ran on KOTH's time limit and score target; an unknown key now throws instead.
        /// </summary>
        private static ArgumentOutOfRangeException UnknownMode(string modeKey) =>
            new ArgumentOutOfRangeException(nameof(modeKey), modeKey, "not a battleground mode key");

        /// <summary>A mode's own time limit in seconds, from the same snapshot its win condition was built from. Throws for an unknown key.</summary>
        public static int TimeLimitSeconds(string modeKey, BattlegroundDials d)
        {
            switch (modeKey)
            {
                case KothModeKey: return d.TimeLimitSecondsKoth;
                case AttackDefendModeKey: return d.TimeLimitSecondsAd;
                default: throw UnknownMode(modeKey);
            }
        }

        /// <summary>A mode's score target, from the same snapshot. Throws for an unknown key.</summary>
        public static int ScoreTarget(string modeKey, BattlegroundDials d)
        {
            switch (modeKey)
            {
                case KothModeKey: return d.KothScoreTarget;
                // Attack/Defend plays to its crystal count, which depends on the map: this is the effective count for the FIRST map in the
                // mode's pool (the same count the mode definition's placeholder win condition uses), never the raw setting, whose 0 means
                // "the map default". A formed match replaces it with its own plan's count (PvpMatchCoordinator.BindAttackDefend).
                case AttackDefendModeKey:
                {
                    var pool = MapPoolFor(AttackDefendModeKey);
                    return AttackDefendPlan.EffectiveCount(pool.Count > 0 ? BattlegroundMapCatalog.Find(pool[0].MapKey) : null, d);
                }
                default: throw UnknownMode(modeKey);
            }
        }
    }
}
