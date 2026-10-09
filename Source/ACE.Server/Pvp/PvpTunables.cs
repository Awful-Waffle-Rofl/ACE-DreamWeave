using System;

using log4net;

using ACE.Server.Managers;

namespace ACE.Server.Pvp
{
    /// <summary>
    /// One resolved snapshot of every pvp_* / PvP-facing setting (Docs/Pvp/DESIGN.md "Tunables").
    /// Nothing reads this yet in PR A - the coordinator, hooks and commands that will are PR C/D.
    /// </summary>
    public sealed record PvpArenaDials(
        bool Enabled,
        bool OneVOneEnabled,
        bool TwoVTwoEnabled,
        bool FfaEnabled,
        int AcceptSeconds,
        int StagingTimeoutSeconds,
        int CountdownSeconds,
        int TimeLimitSeconds1v1,
        int TimeLimitSeconds2v2,
        int TimeLimitSecondsFfa,
        int PostMatchSeconds,
        int MaxConcurrentMatches,
        int MinLevel,
        bool BlockSameIp,
        bool FriendlyFire,
        bool DeathKeepsEnchantments,
        bool RetireCombatPets,
        bool SuppressClassAbilities,
        bool SuppressEquipmentMods,
        bool SuppressWeaponMods,
        bool SuppressPickupBoons,
        bool SuppressTurnSpeed,
        bool RestrictSpells,
        int DeclineLockoutSeconds,
        int MmWindowInitial,
        int MmWindowGrowthPerMinute,
        int MmWindowMax,
        int DuoVsSoloAfterSeconds,
        int FfaTargetPlayers,
        int FfaMinPlayers,
        int FfaMaxPlayers,
        int FfaMinDecaySeconds,
        int RatingInitial,
        int RatingKProvisional,
        int RatingKEstablished,
        int RatingKFfa,
        int RatingProvisionalGames,
        int RatingDecayGraceDays,
        int RatingDecayPointsPerWeek,
        int RatingDecayFloor,
        bool CrierEnabled,
        bool CrierAnnounceOnJoin,
        int CrierIntervalSeconds,
        int CrierLastCallDelaySeconds,
        int CrierLastCallCooldownSeconds,
        string CrierSenderName,
        // pvp_arena_dmg_mod_1v1 - multiplies melee, missile, war and void projectile damage between two players
        // bound to the same Live 1v1 match. Ported from Doctide `arenas_dmg_mod_1v1`. Defaults 1.0 (unchanged).
        double DmgMod1v1,
        // pvp_arena_healkit_skill_cap_1v1 - the healing-skill bonus cap applied to a healer or target in a Live
        // 1v1 match. Ported from Doctide `arena_1v1_healkit_skill_bonus_cap`. Defaults 150.
        int HealkitSkillCap1v1,
        // pvp_arena_healkit_restoration_cap_1v1 - the restoration-bonus multiplier cap applied to a healer or
        // target in a Live 1v1 match. Ported from Doctide `arena_1v1_healkit_restoration_bonus_cap`. Defaults 1.5.
        double HealkitRestorationCap1v1,
        // pvp_arena_overtime_enabled - when a match reaches its regulation time limit with no winner, it goes to
        // overtime instead of ending (Docs/Pvp/DESIGN.md "Overtime"). Defaults ON. Read at the regulation limit.
        bool OvertimeEnabled = true,
        // pvp_arena_overtime_seconds - how long overtime lasts. 0 or negative = overtime off. Defaults 180.
        int OvertimeSeconds = 180,
        // pvp_arena_overtime_healing_mod - multiplier on Health heals received during overtime. 0 = no healing, and
        // kits and Health potions are refused (item kept). NaN, infinite or negative = unchanged. Defaults 0.0.
        double OvertimeHealingMod = 0.0,
        // pvp_arena_overtime_damage_ramp_per_minute - damage between arena opponents is multiplied by
        // 1 + ramp x (whole minutes of overtime), after the PvP damage cap. 0 = off. Defaults 0.0.
        double OvertimeDamageRampPerMinute = 0.0,
        // pvp_arena_blood_enabled - arena matches pay the cosmetic Blood currency (Docs/Pvp/DESIGN.md "Rewards").
        // Off = no Blood is paid at all. Defaults ON (owner ruling 2026-10-08, with the next deploy; it was OFF from 2026-10-01). Read when the match resolves.
        bool BloodEnabled = true,
        // pvp_arena_blood_win - Blood paid to a winner (FFA: first place, shared first included). Defaults 2.
        int BloodWin = 2,
        // pvp_arena_blood_loss - Blood paid to a loser who did not forfeit (FFA: every other non-forfeiter). Defaults 1.
        int BloodLoss = 1,
        // pvp_arena_blood_draw - Blood paid to each side of a draw (timeout draw, double knockout). Defaults 1.
        int BloodDraw = 1,
        // pvp_arena_blood_daily_cap - at most this many PAID matches per character per arena day; further matches
        // pay 0. 0 or negative = no daily cap. Defaults 10.
        int BloodDailyCap = 10,
        // pvp_arena_blood_reset_timezone - IANA or Windows zone id whose wall clock defines the arena day for the
        // daily cap. Unknown id = fixed UTC-5 with a logged warning (the Threads survey reset's resolver). Defaults America/New_York.
        string BloodResetTimezone = "America/New_York",
        // pvp_arena_blood_reset_hour - the hour (0-23) in pvp_arena_blood_reset_timezone at which the arena day turns
        // over. Clamped to [0, 23]. Defaults 0.
        int BloodResetHour = 0,
        // pvp_arena_dmg_mod_ffa - the FFA counterpart of pvp_arena_dmg_mod_1v1: multiplies melee, missile, war and void
        // projectile damage between two players bound to the same Live FFA match. Defaults 1.0 (unchanged).
        double DmgModFfa = 1.0,
        // pvp_arena_ffa_ring_dmg - Tugak Brawl only: multiplies the damage of any-school ring-shape spell projectiles (today Curse
        // of Raven Fury) between two players bound to the same Live FFA match. Defaults 1.0 (unchanged).
        double FfaRingDmg = 1.0);

    /// <summary>
    /// Test seam for the pvp_* tunables, following <c>Player_Facets.FacetTunables</c> exactly:
    /// PropertyManager.Get* throws in ACE.Server.Tests for any uncached key, so every read here is wrapped
    /// and falls back to <see cref="Defaults"/> - the SAME defaults the tunables ship with, so a
    /// test-environment read and a fresh, unconfigured shard read agree.
    /// </summary>
    public static class PvpTunables
    {
        private static readonly ILog log = LogManager.GetLogger(typeof(PvpTunables));

        public static Func<PvpArenaDials> DialSource = ReadFromProperties;

        public static readonly PvpArenaDials Defaults = new PvpArenaDials(
            Enabled: true,
            OneVOneEnabled: true,
            TwoVTwoEnabled: true,
            FfaEnabled: true,
            AcceptSeconds: 20,
            StagingTimeoutSeconds: 30,
            CountdownSeconds: 10,
            TimeLimitSeconds1v1: 300,
            TimeLimitSeconds2v2: 300,
            TimeLimitSecondsFfa: 300,
            PostMatchSeconds: 10,
            MaxConcurrentMatches: 10,
            MinLevel: 50,
            BlockSameIp: true,
            FriendlyFire: false,
            DeathKeepsEnchantments: true,
            RetireCombatPets: true,
            SuppressClassAbilities: true,
            SuppressEquipmentMods: true,
            SuppressWeaponMods: true,
            SuppressPickupBoons: true,
            SuppressTurnSpeed: true,
            RestrictSpells: true,
            DeclineLockoutSeconds: 120,
            MmWindowInitial: 100,
            MmWindowGrowthPerMinute: 50,
            MmWindowMax: 400,
            DuoVsSoloAfterSeconds: 120,
            FfaTargetPlayers: 5,
            FfaMinPlayers: 5,
            FfaMaxPlayers: 15,
            FfaMinDecaySeconds: 60,
            RatingInitial: 1500,
            RatingKProvisional: 40,
            RatingKEstablished: 24,
            RatingKFfa: 32,
            RatingProvisionalGames: 10,
            RatingDecayGraceDays: 14,
            RatingDecayPointsPerWeek: 25,
            RatingDecayFloor: 1500,
            CrierEnabled: true,
            CrierAnnounceOnJoin: true,
            CrierIntervalSeconds: 900,
            CrierLastCallDelaySeconds: 15,
            CrierLastCallCooldownSeconds: 120,
            CrierSenderName: "Arena Crier",
            DmgMod1v1: 1.0,
            HealkitSkillCap1v1: 150,
            HealkitRestorationCap1v1: 1.5,
            OvertimeEnabled: true,
            OvertimeSeconds: 180,
            OvertimeHealingMod: 0.0,
            OvertimeDamageRampPerMinute: 0.0,
            BloodEnabled: true,
            BloodWin: 2,
            BloodLoss: 1,
            BloodDraw: 1,
            BloodDailyCap: 10,
            BloodResetTimezone: "America/New_York",
            BloodResetHour: 0);

        private static PvpArenaDials ReadFromProperties()
        {
            try
            {
                var d = Defaults;

                return new PvpArenaDials(
                    Enabled: PropertyManager.GetBool("pvp_arena_enabled", d.Enabled).Item,
                    OneVOneEnabled: PropertyManager.GetBool("pvp_arena_1v1_enabled", d.OneVOneEnabled).Item,
                    TwoVTwoEnabled: PropertyManager.GetBool("pvp_arena_2v2_enabled", d.TwoVTwoEnabled).Item,
                    FfaEnabled: PropertyManager.GetBool("pvp_arena_ffa_enabled", d.FfaEnabled).Item,
                    AcceptSeconds: ClampAcceptSeconds((int)PropertyManager.GetLong("pvp_arena_accept_seconds", d.AcceptSeconds).Item),
                    StagingTimeoutSeconds: (int)PropertyManager.GetLong("pvp_arena_staging_timeout_seconds", d.StagingTimeoutSeconds).Item,
                    CountdownSeconds: (int)PropertyManager.GetLong("pvp_arena_countdown_seconds", d.CountdownSeconds).Item,
                    TimeLimitSeconds1v1: (int)PropertyManager.GetLong("pvp_arena_time_limit_seconds_1v1", d.TimeLimitSeconds1v1).Item,
                    TimeLimitSeconds2v2: (int)PropertyManager.GetLong("pvp_arena_time_limit_seconds_2v2", d.TimeLimitSeconds2v2).Item,
                    TimeLimitSecondsFfa: (int)PropertyManager.GetLong("pvp_arena_time_limit_seconds_ffa", d.TimeLimitSecondsFfa).Item,
                    PostMatchSeconds: (int)PropertyManager.GetLong("pvp_arena_post_match_seconds", d.PostMatchSeconds).Item,
                    MaxConcurrentMatches: (int)PropertyManager.GetLong("pvp_arena_max_concurrent_matches", d.MaxConcurrentMatches).Item,
                    MinLevel: (int)PropertyManager.GetLong("pvp_arena_min_level", d.MinLevel).Item,
                    BlockSameIp: PropertyManager.GetBool("pvp_arena_block_same_ip", d.BlockSameIp).Item,
                    FriendlyFire: PropertyManager.GetBool("pvp_arena_friendly_fire", d.FriendlyFire).Item,
                    DeathKeepsEnchantments: PropertyManager.GetBool("pvp_arena_death_keeps_enchantments", d.DeathKeepsEnchantments).Item,
                    RetireCombatPets: PropertyManager.GetBool("pvp_arena_retire_combat_pets", d.RetireCombatPets).Item,
                    SuppressClassAbilities: PropertyManager.GetBool("pvp_arena_suppress_class_abilities", d.SuppressClassAbilities).Item,
                    SuppressEquipmentMods: PropertyManager.GetBool("pvp_arena_suppress_equipment_mods", d.SuppressEquipmentMods).Item,
                    SuppressWeaponMods: PropertyManager.GetBool("pvp_arena_suppress_weapon_mods", d.SuppressWeaponMods).Item,
                    SuppressPickupBoons: PropertyManager.GetBool("pvp_arena_suppress_pickup_boons", d.SuppressPickupBoons).Item,
                    SuppressTurnSpeed: PropertyManager.GetBool("pvp_arena_suppress_turn_speed", d.SuppressTurnSpeed).Item,
                    RestrictSpells: PropertyManager.GetBool("pvp_arena_restrict_spells", d.RestrictSpells).Item,
                    DeclineLockoutSeconds: (int)PropertyManager.GetLong("pvp_arena_decline_lockout_seconds", d.DeclineLockoutSeconds).Item,
                    MmWindowInitial: (int)PropertyManager.GetLong("pvp_arena_mm_window_initial", d.MmWindowInitial).Item,
                    MmWindowGrowthPerMinute: (int)PropertyManager.GetLong("pvp_arena_mm_window_growth_per_minute", d.MmWindowGrowthPerMinute).Item,
                    MmWindowMax: (int)PropertyManager.GetLong("pvp_arena_mm_window_max", d.MmWindowMax).Item,
                    DuoVsSoloAfterSeconds: (int)PropertyManager.GetLong("pvp_arena_duo_vs_solo_after_seconds", d.DuoVsSoloAfterSeconds).Item,
                    FfaTargetPlayers: (int)PropertyManager.GetLong("pvp_arena_ffa_target_players", d.FfaTargetPlayers).Item,
                    FfaMinPlayers: (int)PropertyManager.GetLong("pvp_arena_ffa_min_players", d.FfaMinPlayers).Item,
                    FfaMaxPlayers: (int)PropertyManager.GetLong("pvp_arena_ffa_max_players", d.FfaMaxPlayers).Item,
                    FfaMinDecaySeconds: (int)PropertyManager.GetLong("pvp_arena_ffa_min_decay_seconds", d.FfaMinDecaySeconds).Item,
                    RatingInitial: (int)PropertyManager.GetLong("pvp_rating_initial", d.RatingInitial).Item,
                    RatingKProvisional: (int)PropertyManager.GetLong("pvp_rating_k_provisional", d.RatingKProvisional).Item,
                    RatingKEstablished: (int)PropertyManager.GetLong("pvp_rating_k_established", d.RatingKEstablished).Item,
                    RatingKFfa: (int)PropertyManager.GetLong("pvp_rating_k_ffa", d.RatingKFfa).Item,
                    RatingProvisionalGames: (int)PropertyManager.GetLong("pvp_rating_provisional_games", d.RatingProvisionalGames).Item,
                    RatingDecayGraceDays: (int)PropertyManager.GetLong("pvp_rating_decay_grace_days", d.RatingDecayGraceDays).Item,
                    RatingDecayPointsPerWeek: (int)PropertyManager.GetLong("pvp_rating_decay_points_per_week", d.RatingDecayPointsPerWeek).Item,
                    RatingDecayFloor: (int)PropertyManager.GetLong("pvp_rating_decay_floor", d.RatingDecayFloor).Item,
                    CrierEnabled: PropertyManager.GetBool("pvp_arena_crier_enabled", d.CrierEnabled).Item,
                    CrierAnnounceOnJoin: PropertyManager.GetBool("pvp_arena_crier_announce_on_join", d.CrierAnnounceOnJoin).Item,
                    CrierIntervalSeconds: (int)PropertyManager.GetLong("pvp_arena_crier_interval_seconds", d.CrierIntervalSeconds).Item,
                    CrierLastCallDelaySeconds: (int)PropertyManager.GetLong("pvp_arena_crier_last_call_delay_seconds", d.CrierLastCallDelaySeconds).Item,
                    CrierLastCallCooldownSeconds: (int)PropertyManager.GetLong("pvp_arena_crier_last_call_cooldown_seconds", d.CrierLastCallCooldownSeconds).Item,
                    CrierSenderName: PropertyManager.GetString("pvp_arena_crier_sender_name", d.CrierSenderName).Item,
                    DmgMod1v1: PropertyManager.GetDouble("pvp_arena_dmg_mod_1v1", d.DmgMod1v1).Item,
                    HealkitSkillCap1v1: (int)PropertyManager.GetLong("pvp_arena_healkit_skill_cap_1v1", d.HealkitSkillCap1v1).Item,
                    HealkitRestorationCap1v1: PropertyManager.GetDouble("pvp_arena_healkit_restoration_cap_1v1", d.HealkitRestorationCap1v1).Item,
                    OvertimeEnabled: PropertyManager.GetBool("pvp_arena_overtime_enabled", d.OvertimeEnabled).Item,
                    OvertimeSeconds: (int)PropertyManager.GetLong("pvp_arena_overtime_seconds", d.OvertimeSeconds).Item,
                    OvertimeHealingMod: PropertyManager.GetDouble("pvp_arena_overtime_healing_mod", d.OvertimeHealingMod).Item,
                    OvertimeDamageRampPerMinute: PropertyManager.GetDouble("pvp_arena_overtime_damage_ramp_per_minute", d.OvertimeDamageRampPerMinute).Item,
                    BloodEnabled: PropertyManager.GetBool("pvp_arena_blood_enabled", d.BloodEnabled).Item,
                    BloodWin: (int)PropertyManager.GetLong("pvp_arena_blood_win", d.BloodWin).Item,
                    BloodLoss: (int)PropertyManager.GetLong("pvp_arena_blood_loss", d.BloodLoss).Item,
                    BloodDraw: (int)PropertyManager.GetLong("pvp_arena_blood_draw", d.BloodDraw).Item,
                    BloodDailyCap: (int)PropertyManager.GetLong("pvp_arena_blood_daily_cap", d.BloodDailyCap).Item,
                    BloodResetTimezone: PropertyManager.GetString("pvp_arena_blood_reset_timezone", d.BloodResetTimezone).Item,
                    BloodResetHour: (int)Math.Clamp(PropertyManager.GetLong("pvp_arena_blood_reset_hour", d.BloodResetHour).Item, 0L, 23L),
                    DmgModFfa: PropertyManager.GetDouble("pvp_arena_dmg_mod_ffa", d.DmgModFfa).Item,
                    FfaRingDmg: PropertyManager.GetDouble("pvp_arena_ffa_ring_dmg", d.FfaRingDmg).Item);
            }
            catch (Exception ex)
            {
                log.Error("[PVP] could not read the pvp_* tunables; falling back to built-in defaults", ex);
                return Defaults;
            }
        }

        /// <summary>
        /// pvp_arena_accept_seconds "must stay at or under 30, because the popup times out at 30 seconds"
        /// (Docs/Pvp/DESIGN.md "Tunables"). A misconfigured value above 30 would make the popup close
        /// before the accept window does; a value at or below 0 would give players no time to answer it
        /// at all, so the floor of 5 keeps the window meaningfully usable. Pure and separately testable,
        /// since ACE.Server.Tests cannot set a live PropertyManager value to exercise this through
        /// ReadFromProperties.
        /// </summary>
        public static int ClampAcceptSeconds(int rawSeconds)
        {
            return Math.Clamp(rawSeconds, 5, 30);
        }
    }
}
