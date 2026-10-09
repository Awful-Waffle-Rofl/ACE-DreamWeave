using System;

using log4net;

using ACE.Server.Entity;
using ACE.Server.Managers;

namespace ACE.Server.Pvp.Battlegrounds
{
    /// <summary>
    /// One resolved snapshot of every pvp_bg_* setting (Docs/Pvp/BATTLEGROUNDS.md "Settings"). Inert until
    /// the battleground matchmaker, win condition and tick handler that read it land.
    /// </summary>
    public sealed record BattlegroundDials(
        // pvp_bg_enabled - BG master switch, underneath pvp_arena_enabled.
        bool Enabled,
        // pvp_bg_koth_enabled - per-mode switch for King of the Hill.
        bool KothEnabled,
        // pvp_bg_min_players / pvp_bg_max_players - queue count that opens the fill window, and the count that forms at once.
        int MinPlayers,
        int MaxPlayers,
        // pvp_bg_fill_window_seconds - wait after the queue reaches MinPlayers before a match forms.
        int FillWindowSeconds,
        // pvp_bg_max_premade_size - clamped to [1, 6] and to the fellowship member cap (see ClampMaxPremadeSize).
        int MaxPremadeSize,
        // pvp_bg_premade_imbalance_tolerance - premade players by which the teams may differ.
        int PremadeImbalanceTolerance,
        // pvp_bg_premade_vs_solo_after_seconds - wait after which the tolerance is waived.
        int PremadeVsSoloAfterSeconds,
        // pvp_bg_split_clanmates - prefer to split same-allegiance solos across teams.
        bool SplitClanmates,
        // pvp_bg_max_concurrent_matches - counted inside the global arena cap.
        int MaxConcurrentMatches,
        // pvp_bg_respawn_seconds - pen time before a dead player returns.
        int RespawnSeconds,
        // pvp_bg_time_limit_seconds_koth - KOTH time limit.
        int TimeLimitSecondsKoth,
        // pvp_bg_koth_score_target - score at which a team wins.
        int KothScoreTarget,
        // pvp_bg_koth_tick_seconds - seconds between score and drain ticks.
        int KothTickSeconds,
        // pvp_bg_koth_hold_points / pvp_bg_koth_decay_points - per-tick gain for the holder, loss for the other team.
        int KothHoldPoints,
        int KothDecayPoints,
        // pvp_bg_koth_zone_radius / pvp_bg_koth_zone_height - metres.
        double KothZoneRadius,
        double KothZoneHeight,
        // pvp_bg_koth_min_holders - players a team needs on the point to hold it.
        int KothMinHolders,
        // pvp_bg_koth_dedupe_ip - each IP counts once per team on the point.
        bool KothDedupeIp,
        // pvp_bg_koth_drain_health / _stamina / _mana - per-tick drain on every counted player.
        int KothDrainHealth,
        int KothDrainStamina,
        int KothDrainMana,
        // pvp_bg_koth_drain_lethal - whether the drain can kill.
        bool KothDrainLethal,
        // pvp_bg_koth_score_announce_seconds - seconds between score announcements.
        int KothScoreAnnounceSeconds,
        // pvp_bg_koth_markers_enabled - place the ring of zone markers on the King of the Hill zone edge.
        bool KothMarkersEnabled,
        // pvp_bg_koth_marker_spacing - target metres between neighbouring zone markers; clamped to at least 0.5.
        double KothMarkerSpacing,
        // pvp_bg_koth_marker_z_offset - metres added to each marker's z; clamped to MinMarkerZOffset..MaxMarkerZOffset.
        double KothMarkerZOffset,
        // pvp_bg_koth_hill_moves - how many times the hill moves over a match; clamped to 0..KothHillSchedule.MaxMoves (3).
        int KothHillMoves,
        // pvp_bg_team_fellowship - put each team in a server-built fellowship at Countdown (read once per match, at formation).
        bool TeamFellowship = true,
        // pvp_bg_team_fellowship_restore - put a released player back into the fellowship they arrived in, where possible.
        bool TeamFellowshipRestore = true,
        // pvp_bg_ad_enabled - per-mode switch for Attack/Defend.
        bool AdEnabled = true,
        // pvp_bg_time_limit_seconds_ad - Attack/Defend time limit; at the limit the defenders win.
        int TimeLimitSecondsAd = 600,
        // pvp_bg_ad_crystal_count - crystals per match; 0 means the map's default, clamped to 0..MaxCrystalCount here and to the map's site count when planned.
        int AdCrystalCount = 0,
        // pvp_bg_ad_crystal_health_per_attacker - each crystal's health per attacker on the attacking team at formation.
        int AdCrystalHealthPerAttacker = 5000,
        // pvp_bg_ad_respawn_seconds_attackers / pvp_bg_ad_respawn_seconds_defenders - pen time per side.
        int AdRespawnSecondsAttackers = 30,
        int AdRespawnSecondsDefenders = 30,
        // pvp_bg_ad_alert_step_percent - a defender alert fires each time a crystal's health drops through another step of this many percent.
        int AdAlertStepPercent = 25,
        // pvp_bg_ad_alert_min_interval_seconds - fewest seconds between two defender alerts.
        int AdAlertMinIntervalSeconds = 10,
        // pvp_bg_ad_status_announce_seconds - seconds between the periodic status lines.
        int AdStatusAnnounceSeconds = 60,
        // pvp_bg_spawn_protection_seconds - seconds a respawned battleground player cannot be hurt by other players; 0 disables it. Read at formation.
        int SpawnProtectionSeconds = 3,
        // pvp_bg_ad_sequential_crystals - the attackers must destroy the crystals in site order; only the lowest standing one is vulnerable. Read at formation.
        bool AdSequentialCrystals = true,
        // pvp_bg_dmg_mod - multiplies melee, missile, war and void projectile damage between two players bound to the same Live
        // battleground match (any mode), at choke point AM1 before the damage cap. Read on every hit, not snapshotted. NaN, infinite
        // or negative = unchanged (sanitized where it is applied, like pvp_arena_dmg_mod_ffa).
        // Read per hit through BattlegroundTunables.DialSource; m.BgDials.DmgMod is a stale formation snapshot and must not be read.
        double DmgMod = 1.0,
        // pvp_bg_ad_kill_chip_pct - percent of the vulnerable crystal's MAXIMUM health an attacker's kill of a defender near it takes off.
        // Snapshotted at formation. 0, NaN, infinite or negative = off; above 100 counts as 100 (CrystalKillRules).
        double AdKillChipPercent = 1.0,
        // pvp_bg_ad_kill_heal_pct - percent of the vulnerable crystal's MAXIMUM health a defender's kill of an attacker near it restores.
        double AdKillHealPercent = 0.5,
        // pvp_bg_ad_kill_range - metres from the vulnerable crystal within which the VICTIM must have died for a kill to count.
        double AdKillRange = 25.0,
        // pvp_bg_ad_defender_dr_per / _cap / _radius / _window_s - engaged-defender reduction of an attacker's hit on a crystal
        // (CrystalDefenderReduction): each defender within the radius who dealt or took player damage within the window takes this
        // fraction off, up to the cap. Snapshotted at formation onto PvpMatch.DefenderReduction. NaN, infinite or not positive = off.
        double AdDefenderDrPer = 0.125,
        double AdDefenderDrCap = 0.5,
        double AdDefenderDrRadius = 5.0,
        double AdDefenderDrWindowSeconds = 10.0,
        // pvp_bg_snake_draft - the matchmaker prefers the split whose solos follow an Elo snake draft (A, B, B, A, A, B, B, A ...),
        // ahead of the clanmate split and the rating-sum difference. The hard constraints (same IP, premade tolerance) always win.
        bool SnakeDraft = true,
        // pvp_bg_marks_win / _loss / _draw - Marks of the Hopeslayer paid to each battleground winner / loser who did not
        // forfeit / participant of a drawn match (Docs/Pvp/BATTLEGROUNDS.md "Rewards"). Paid only while the arena's master switch
        // pvp_arena_blood_enabled is on (it lives in PvpArenaDials). 0 or negative pays nothing. Read when the match resolves.
        int MarksWin = 2,
        int MarksLoss = 1,
        int MarksDraw = 1,
        // pvp_bg_marks_scale - multiplies the win / loss / draw amount: paid = max(0, round(base x scale)). NaN or infinite
        // = 1.0 (unchanged), negative = 0 (pays nothing); sanitized where it is applied (PvpArenaRewards.SanitizeMarksScale).
        double MarksScale = 1.0,
        // pvp_bg_marks_daily_cap - at most this many PAID battleground matches per character per day (its own ledger,
        // independent of pvp_arena_blood_daily_cap; same reset zone and hour). 0 or negative = no cap.
        int MarksDailyCap = 10);

    /// <summary>
    /// Test seam for the pvp_bg_* settings, mirroring <see cref="PvpTunables"/>: PropertyManager.Get* throws
    /// in ACE.Server.Tests for any uncached key, so every read is wrapped and falls back to
    /// <see cref="Defaults"/>, the SAME defaults the PropertyManager entries ship with.
    /// </summary>
    public static class BattlegroundTunables
    {
        private static readonly ILog log = LogManager.GetLogger(typeof(BattlegroundTunables));

        /// <summary>The most players a premade team can hold (a full BG team).</summary>
        public const int AbsoluteMaxPremadeSize = 6;

        public static Func<BattlegroundDials> DialSource = ReadFromProperties;

        public static readonly BattlegroundDials Defaults = new BattlegroundDials(
            Enabled: true,
            KothEnabled: true,
            MinPlayers: 4,
            MaxPlayers: 12,
            FillWindowSeconds: 120,
            MaxPremadeSize: 6,
            PremadeImbalanceTolerance: 2,
            PremadeVsSoloAfterSeconds: 120,
            SplitClanmates: true,
            MaxConcurrentMatches: 2,
            RespawnSeconds: 30,
            TimeLimitSecondsKoth: 900,
            KothScoreTarget: 300,
            KothTickSeconds: 5,
            KothHoldPoints: 5,
            KothDecayPoints: 2,
            KothZoneRadius: 6.0,
            KothZoneHeight: 3.0,
            KothMinHolders: 1,
            KothDedupeIp: true,
            KothDrainHealth: 50,
            KothDrainStamina: 50,
            KothDrainMana: 50,
            KothDrainLethal: true,
            KothScoreAnnounceSeconds: 10,
            KothMarkersEnabled: true,
            KothMarkerSpacing: 2.5,
            KothMarkerZOffset: -0.15,
            KothHillMoves: 2,
            TeamFellowship: true,
            TeamFellowshipRestore: true,
            AdEnabled: true,
            TimeLimitSecondsAd: 600,
            AdCrystalCount: 0,
            AdCrystalHealthPerAttacker: 5000,
            AdRespawnSecondsAttackers: 30,
            AdRespawnSecondsDefenders: 30,
            AdAlertStepPercent: 25,
            AdAlertMinIntervalSeconds: 10,
            AdStatusAnnounceSeconds: 60,
            SpawnProtectionSeconds: 3,
            AdSequentialCrystals: true,
            DmgMod: 1.0,
            AdKillChipPercent: 1.0,
            AdKillHealPercent: 0.5,
            AdKillRange: 25.0,
            AdDefenderDrPer: 0.125,
            AdDefenderDrCap: 0.5,
            AdDefenderDrRadius: 5.0,
            AdDefenderDrWindowSeconds: 10.0,
            SnakeDraft: true,
            MarksWin: 2,
            MarksLoss: 1,
            MarksDraw: 1,
            MarksScale: 1.0,
            MarksDailyCap: 10);

        private static BattlegroundDials ReadFromProperties()
        {
            try
            {
                var d = Defaults;

                return Clamp(new BattlegroundDials(
                    Enabled: PropertyManager.GetBool("pvp_bg_enabled", d.Enabled).Item,
                    KothEnabled: PropertyManager.GetBool("pvp_bg_koth_enabled", d.KothEnabled).Item,
                    MinPlayers: (int)PropertyManager.GetLong("pvp_bg_min_players", d.MinPlayers).Item,
                    MaxPlayers: (int)PropertyManager.GetLong("pvp_bg_max_players", d.MaxPlayers).Item,
                    FillWindowSeconds: (int)PropertyManager.GetLong("pvp_bg_fill_window_seconds", d.FillWindowSeconds).Item,
                    MaxPremadeSize: ClampMaxPremadeSize((int)PropertyManager.GetLong("pvp_bg_max_premade_size", d.MaxPremadeSize).Item, Fellowship.MaxFellows),
                    PremadeImbalanceTolerance: (int)PropertyManager.GetLong("pvp_bg_premade_imbalance_tolerance", d.PremadeImbalanceTolerance).Item,
                    PremadeVsSoloAfterSeconds: (int)PropertyManager.GetLong("pvp_bg_premade_vs_solo_after_seconds", d.PremadeVsSoloAfterSeconds).Item,
                    SplitClanmates: PropertyManager.GetBool("pvp_bg_split_clanmates", d.SplitClanmates).Item,
                    MaxConcurrentMatches: (int)PropertyManager.GetLong("pvp_bg_max_concurrent_matches", d.MaxConcurrentMatches).Item,
                    RespawnSeconds: (int)PropertyManager.GetLong("pvp_bg_respawn_seconds", d.RespawnSeconds).Item,
                    TimeLimitSecondsKoth: (int)PropertyManager.GetLong("pvp_bg_time_limit_seconds_koth", d.TimeLimitSecondsKoth).Item,
                    KothScoreTarget: (int)PropertyManager.GetLong("pvp_bg_koth_score_target", d.KothScoreTarget).Item,
                    KothTickSeconds: (int)PropertyManager.GetLong("pvp_bg_koth_tick_seconds", d.KothTickSeconds).Item,
                    KothHoldPoints: (int)PropertyManager.GetLong("pvp_bg_koth_hold_points", d.KothHoldPoints).Item,
                    KothDecayPoints: (int)PropertyManager.GetLong("pvp_bg_koth_decay_points", d.KothDecayPoints).Item,
                    KothZoneRadius: PropertyManager.GetDouble("pvp_bg_koth_zone_radius", d.KothZoneRadius).Item,
                    KothZoneHeight: PropertyManager.GetDouble("pvp_bg_koth_zone_height", d.KothZoneHeight).Item,
                    KothMinHolders: (int)PropertyManager.GetLong("pvp_bg_koth_min_holders", d.KothMinHolders).Item,
                    KothDedupeIp: PropertyManager.GetBool("pvp_bg_koth_dedupe_ip", d.KothDedupeIp).Item,
                    KothDrainHealth: (int)PropertyManager.GetLong("pvp_bg_koth_drain_health", d.KothDrainHealth).Item,
                    KothDrainStamina: (int)PropertyManager.GetLong("pvp_bg_koth_drain_stamina", d.KothDrainStamina).Item,
                    KothDrainMana: (int)PropertyManager.GetLong("pvp_bg_koth_drain_mana", d.KothDrainMana).Item,
                    KothDrainLethal: PropertyManager.GetBool("pvp_bg_koth_drain_lethal", d.KothDrainLethal).Item,
                    KothScoreAnnounceSeconds: (int)PropertyManager.GetLong("pvp_bg_koth_score_announce_seconds", d.KothScoreAnnounceSeconds).Item,
                    KothMarkersEnabled: PropertyManager.GetBool("pvp_bg_koth_markers_enabled", d.KothMarkersEnabled).Item,
                    KothMarkerSpacing: PropertyManager.GetDouble("pvp_bg_koth_marker_spacing", d.KothMarkerSpacing).Item,
                    KothMarkerZOffset: PropertyManager.GetDouble("pvp_bg_koth_marker_z_offset", d.KothMarkerZOffset).Item,
                    KothHillMoves: (int)PropertyManager.GetLong("pvp_bg_koth_hill_moves", d.KothHillMoves).Item,
                    TeamFellowship: PropertyManager.GetBool("pvp_bg_team_fellowship", d.TeamFellowship).Item,
                    TeamFellowshipRestore: PropertyManager.GetBool("pvp_bg_team_fellowship_restore", d.TeamFellowshipRestore).Item,
                    AdEnabled: PropertyManager.GetBool("pvp_bg_ad_enabled", d.AdEnabled).Item,
                    TimeLimitSecondsAd: (int)PropertyManager.GetLong("pvp_bg_time_limit_seconds_ad", d.TimeLimitSecondsAd).Item,
                    AdCrystalCount: (int)PropertyManager.GetLong("pvp_bg_ad_crystal_count", d.AdCrystalCount).Item,
                    AdCrystalHealthPerAttacker: (int)PropertyManager.GetLong("pvp_bg_ad_crystal_health_per_attacker", d.AdCrystalHealthPerAttacker).Item,
                    AdRespawnSecondsAttackers: (int)PropertyManager.GetLong("pvp_bg_ad_respawn_seconds_attackers", d.AdRespawnSecondsAttackers).Item,
                    AdRespawnSecondsDefenders: (int)PropertyManager.GetLong("pvp_bg_ad_respawn_seconds_defenders", d.AdRespawnSecondsDefenders).Item,
                    AdAlertStepPercent: (int)PropertyManager.GetLong("pvp_bg_ad_alert_step_percent", d.AdAlertStepPercent).Item,
                    AdAlertMinIntervalSeconds: (int)PropertyManager.GetLong("pvp_bg_ad_alert_min_interval_seconds", d.AdAlertMinIntervalSeconds).Item,
                    AdStatusAnnounceSeconds: (int)PropertyManager.GetLong("pvp_bg_ad_status_announce_seconds", d.AdStatusAnnounceSeconds).Item,
                    SpawnProtectionSeconds: ClampSpawnProtectionSeconds(PropertyManager.GetLong("pvp_bg_spawn_protection_seconds", d.SpawnProtectionSeconds).Item),
                    AdSequentialCrystals: PropertyManager.GetBool("pvp_bg_ad_sequential_crystals", d.AdSequentialCrystals).Item,
                    DmgMod: PropertyManager.GetDouble("pvp_bg_dmg_mod", d.DmgMod).Item,
                    AdKillChipPercent: PropertyManager.GetDouble("pvp_bg_ad_kill_chip_pct", d.AdKillChipPercent).Item,
                    AdKillHealPercent: PropertyManager.GetDouble("pvp_bg_ad_kill_heal_pct", d.AdKillHealPercent).Item,
                    AdKillRange: PropertyManager.GetDouble("pvp_bg_ad_kill_range", d.AdKillRange).Item,
                    AdDefenderDrPer: PropertyManager.GetDouble("pvp_bg_ad_defender_dr_per", d.AdDefenderDrPer).Item,
                    AdDefenderDrCap: PropertyManager.GetDouble("pvp_bg_ad_defender_dr_cap", d.AdDefenderDrCap).Item,
                    AdDefenderDrRadius: PropertyManager.GetDouble("pvp_bg_ad_defender_dr_radius", d.AdDefenderDrRadius).Item,
                    AdDefenderDrWindowSeconds: PropertyManager.GetDouble("pvp_bg_ad_defender_dr_window_s", d.AdDefenderDrWindowSeconds).Item,
                    SnakeDraft: PropertyManager.GetBool("pvp_bg_snake_draft", d.SnakeDraft).Item,
                    MarksWin: (int)PropertyManager.GetLong("pvp_bg_marks_win", d.MarksWin).Item,
                    MarksLoss: (int)PropertyManager.GetLong("pvp_bg_marks_loss", d.MarksLoss).Item,
                    MarksDraw: (int)PropertyManager.GetLong("pvp_bg_marks_draw", d.MarksDraw).Item,
                    MarksScale: PropertyManager.GetDouble("pvp_bg_marks_scale", d.MarksScale).Item,
                    MarksDailyCap: (int)PropertyManager.GetLong("pvp_bg_marks_daily_cap", d.MarksDailyCap).Item));
            }
            catch (Exception ex)
            {
                log.Error("[PVP] could not read the pvp_bg_* tunables; falling back to built-in defaults", ex);
                return Defaults;
            }
        }

        /// <summary>The fewest players a battleground forms with: two teams of two.</summary>
        public const int MinPlayersFloor = 4;

        /// <summary>The most players a battleground holds: two full teams.</summary>
        public const int MaxPlayersCeiling = 2 * AbsoluteMaxPremadeSize;

        /// <summary>
        /// Input clamps for the dials (the counterpart of <see cref="PvpTunables.ClampAcceptSeconds"/>), so a
        /// misconfigured value cannot divide by zero, never end a match, or ask for an impossible match size:
        /// score target, tick and time limit at least 1; respawn, hold and decay points, fill window at least 0;
        /// min holders at least 1; the Attack/Defend limits (time limit at least 60, crystal count 0..4, per-attacker health at least 1, respawns at least 0, alert step 5..100, alert interval at least 1, status interval at least 5); min players at least 4; max players between min players and 12; marker spacing at least 0.5; marker z offset within MinMarkerZOffset..MaxMarkerZOffset; hill moves within 0..KothHillSchedule.MaxMoves. Every other
        /// field is returned as given (the premade size has its own clamp at read time). Pure.
        /// </summary>
        public static BattlegroundDials Clamp(BattlegroundDials d)
        {
            if (d == null)
                return null;

            var minPlayers = Math.Clamp(d.MinPlayers, MinPlayersFloor, MaxPlayersCeiling);

            return d with
            {
                KothScoreTarget = Math.Max(1, d.KothScoreTarget),
                KothTickSeconds = Math.Max(1, d.KothTickSeconds),
                TimeLimitSecondsKoth = Math.Max(1, d.TimeLimitSecondsKoth),
                RespawnSeconds = Math.Max(0, d.RespawnSeconds),
                KothMinHolders = Math.Max(1, d.KothMinHolders),
                KothHoldPoints = Math.Max(0, d.KothHoldPoints),
                KothDecayPoints = Math.Max(0, d.KothDecayPoints),
                MinPlayers = minPlayers,
                MaxPlayers = Math.Clamp(d.MaxPlayers, minPlayers, MaxPlayersCeiling),
                FillWindowSeconds = Math.Max(0, d.FillWindowSeconds),
                KothMarkerSpacing = ClampMarkerSpacing(d.KothMarkerSpacing),
                KothMarkerZOffset = ClampMarkerZOffset(d.KothMarkerZOffset),
                KothHillMoves = Math.Clamp(d.KothHillMoves, 0, KothHillSchedule.MaxMoves),
                TimeLimitSecondsAd = Math.Max(MinTimeLimitSecondsAd, d.TimeLimitSecondsAd),
                AdCrystalCount = Math.Clamp(d.AdCrystalCount, 0, MaxCrystalCount),
                AdCrystalHealthPerAttacker = Math.Max(1, d.AdCrystalHealthPerAttacker),
                AdRespawnSecondsAttackers = Math.Max(0, d.AdRespawnSecondsAttackers),
                AdRespawnSecondsDefenders = Math.Max(0, d.AdRespawnSecondsDefenders),
                AdAlertStepPercent = Math.Clamp(d.AdAlertStepPercent, MinAlertStepPercent, MaxAlertStepPercent),
                AdAlertMinIntervalSeconds = Math.Max(1, d.AdAlertMinIntervalSeconds),
                AdStatusAnnounceSeconds = Math.Max(MinStatusAnnounceSeconds, d.AdStatusAnnounceSeconds),
                SpawnProtectionSeconds = ClampSpawnProtectionSeconds(d.SpawnProtectionSeconds),
            };
        }

        /// <summary>The longest spawn-protection window in seconds (pvp_bg_spawn_protection_seconds); the dial clamps to 0..this.</summary>
        public const int MaxSpawnProtectionSeconds = 30;

        /// <summary>
        /// Clamps a raw spawn-protection value to 0..<see cref="MaxSpawnProtectionSeconds"/> BEFORE narrowing to int, so a huge setting
        /// (a long that would wrap through an int cast to a small or negative number) lands on the cap, not on a wrapped value. Pure.
        /// </summary>
        public static int ClampSpawnProtectionSeconds(long raw) => (int)Math.Clamp(raw, 0L, (long)MaxSpawnProtectionSeconds);

        /// <summary>The shortest Attack/Defend time limit in seconds (pvp_bg_time_limit_seconds_ad).</summary>
        public const int MinTimeLimitSecondsAd = 60;

        /// <summary>The most crystals pvp_bg_ad_crystal_count may ask for; phase B also clamps to the map's site count.</summary>
        public const int MaxCrystalCount = 4;

        /// <summary>The smallest and largest pvp_bg_ad_alert_step_percent.</summary>
        public const int MinAlertStepPercent = 5;

        public const int MaxAlertStepPercent = 100;

        /// <summary>The shortest gap in seconds between periodic Attack/Defend status lines (pvp_bg_ad_status_announce_seconds).</summary>
        public const int MinStatusAnnounceSeconds = 5;

        /// <summary>The smallest zone-marker spacing in metres; a smaller (or NaN) value is raised to it.</summary>
        public const double MinMarkerSpacing = 0.5;

        /// <summary>pvp_bg_koth_marker_spacing clamped to at least <see cref="MinMarkerSpacing"/>; NaN becomes the floor. Pure.</summary>
        public static double ClampMarkerSpacing(double spacing)
        {
            return double.IsNaN(spacing) || spacing < MinMarkerSpacing ? MinMarkerSpacing : spacing;
        }

        /// <summary>
        /// The lowest marker z offset in metres. The marker weenie's (Setup 0x020009F3, DefaultScale 0.35) first collision
        /// sphere is centred 0.48 x 0.35 = 0.168 m above its origin, and the server's placement test needs that centre inside
        /// a cell, so on a floor at z 0.005 an origin below about -0.16 is refused (`cells --spawncheck` flipped from SPAWNS
        /// at z -0.160 to WILL NOT SPAWN at z -0.168). -0.15 keeps the whole range placeable. Change it with the weenie scale.
        /// </summary>
        public const double MinMarkerZOffset = -0.15;

        /// <summary>The highest marker z offset in metres.</summary>
        public const double MaxMarkerZOffset = 5.0;

        /// <summary>pvp_bg_koth_marker_z_offset clamped to <see cref="MinMarkerZOffset"/>..<see cref="MaxMarkerZOffset"/>; NaN becomes the default. Pure.</summary>
        public static double ClampMarkerZOffset(double offset)
        {
            return double.IsNaN(offset) ? Defaults.KothMarkerZOffset : Math.Clamp(offset, MinMarkerZOffset, MaxMarkerZOffset);
        }

        /// <summary>
        /// pvp_bg_max_premade_size "clamped to 6 and to the fellowship cap" (Docs/Pvp/BATTLEGROUNDS.md
        /// "Settings"). The floor of 1 keeps a misconfigured 0 or negative from making every queue unit
        /// unadmittable. Pure and separately testable, since ACE.Server.Tests cannot set a live
        /// PropertyManager value to exercise this through ReadFromProperties.
        /// </summary>
        public static int ClampMaxPremadeSize(int rawSize, int fellowshipCap)
        {
            var ceiling = Math.Min(AbsoluteMaxPremadeSize, Math.Max(1, fellowshipCap));
            return Math.Clamp(rawSize, 1, ceiling);
        }
    }
}