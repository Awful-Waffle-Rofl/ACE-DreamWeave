using System;

using ACE.Server.WorldEvents.Defs;

using K = ACE.Server.WorldEvents.HoldKeys;

namespace ACE.Server.WorldEvents.Hold
{
    /// <summary>One damage role's tuning (world_events_hold_&lt;role&gt;_*). Immutable.</summary>
    public sealed record WorldEventHoldRoleTuning
    {
        /// <summary>The identity: every multiplier 1, no health or swing change, no level move.</summary>
        public static WorldEventHoldRoleTuning Identity { get; } = new WorldEventHoldRoleTuning();

        /// <summary>Multiplies the role's damage to players.</summary>
        public double PlayerDamageMult { get; init; } = 1.0;

        /// <summary>Multiplies the role's damage to the knights, before the min_death_seconds cap.</summary>
        public double NpcDamageMult { get; init; } = 1.0;

        /// <summary>The role's health multiplier, or null for no change.</summary>
        public double? HealthMult { get; init; }

        /// <summary>The role's melee swing speed multiplier, or null for no change.</summary>
        public double? AttackSpeedMult { get; init; }

        /// <summary>Levels the role's slots of a mixed wave are moved by (0 = no move).</summary>
        public int LevelOffset { get; init; }
    }

    /// <summary>
    /// Every per-hit value a Hold run's engine hooks read, resolved once into an immutable snapshot (<see cref="From"/>).
    /// WorldEventHoldRun builds one at OnStaged and rebuilds it about once a second from the Live rows (the NextRun rows stay
    /// as they were at OnStaged), swapping the reference whole, so a hook reading Run.Tuning never sees half of one snapshot
    /// and half of another.
    ///
    /// Each value is the resolved setting (WorldEventHoldSettingsResolver: a set property, else the goals.json value, else the
    /// row's code default). A row that resolves to "no preference" (null) takes the fallback named on its member, which is the
    /// goals.json def's own default where the def has one.
    ///
    /// THREADING: immutable (init-only members, and the role table is a private array exposed read-only), so safe from any
    /// thread.
    /// </summary>
    public sealed record WorldEventHoldTuning
    {
        /// <summary>The tuning with no goal and no property set: every row's code default, every fallback. Used before OnStaged.</summary>
        public static WorldEventHoldTuning Defaults { get; } = From(WorldEventHoldSettingsResolver.Resolve(null, _ => null), null, 1);

        /// <summary>The audience the snapshot was sized for (at least 1).</summary>
        public int Participants { get; init; } = 1;

        // ---- knights ----

        /// <summary>knight_max_health (fallback ProtectDef.TimeScaledMaxHealth).</summary>
        public long KnightMaxHealth { get; init; }

        /// <summary>knight_damage_taken_mult (fallback 1).</summary>
        public double KnightDamageTakenMult { get; init; }

        /// <summary>undefended_seconds as set or in goals.json, before the per-participant reduction (0 = monster damage unscaled).</summary>
        public double UndefendedSecondsBase { get; init; }

        /// <summary>undefended_seconds_per_participant (fallback 0).</summary>
        public double UndefendedSecondsPerParticipant { get; init; }

        /// <summary>undefended_seconds_floor (fallback 0).</summary>
        public double UndefendedSecondsFloor { get; init; }

        /// <summary>undefended_seconds for <see cref="Participants"/>, by ProtectDef.EffectiveUndefendedSeconds's rule.</summary>
        public double UndefendedSeconds { get; init; }

        /// <summary>min_death_seconds (fallback 0 = no cap).</summary>
        public double MinDeathSeconds { get; init; }

        /// <summary>min_survivors (fallback ProtectDef.DefaultMinSurvivors).</summary>
        public int MinSurvivors { get; init; }

        /// <summary>hold_seconds (fallback 0).</summary>
        public double HoldSeconds { get; init; }

        // ---- internals ----

        /// <summary>cap_burst_seconds (fallback 2).</summary>
        public double CapBurstSeconds { get; init; }

        /// <summary>hit_interval_seconds (fallback 2).</summary>
        public double HitIntervalSeconds { get; init; }

        /// <summary>hit_interval_max_seconds (fallback 4).</summary>
        public double HitIntervalMaxSeconds { get; init; }

        /// <summary>healer_memory_seconds (fallback 20).</summary>
        public double HealerMemorySeconds { get; init; }

        /// <summary>boss_damage_keep_seconds (fallback 30).</summary>
        public double BossDamageKeepSeconds { get; init; }

        /// <summary>wave_resolve_grace_seconds (fallback 15).</summary>
        public double WaveResolveGraceSeconds { get; init; }

        // ---- healing ----

        /// <summary>heal_reduction_after_hit (fallback 0).</summary>
        public double HealReductionAfterHit { get; init; }

        /// <summary>heal_reduction_seconds (fallback 5, ProtectDef's default).</summary>
        public double HealReductionSeconds { get; init; }

        /// <summary>heal_budget_per_wave (fallback 0 = no budget).</summary>
        public double HealBudgetPerWave { get; init; }

        /// <summary>rescue_heal (fallback 0).</summary>
        public double RescueHeal { get; init; }

        /// <summary>rescue_cap_per_wave (fallback 0.1, ProtectDef's default).</summary>
        public double RescueCapPerWave { get; init; }

        /// <summary>corrupted_kill_heal (fallback 0).</summary>
        public double CorruptedKillHeal { get; init; }

        /// <summary>heal_between_waves (fallback 0).</summary>
        public double HealBetweenWaves { get; init; }

        /// <summary>regen_per_second (fallback 0 = off).</summary>
        public double RegenPerSecond { get; init; }

        /// <summary>regen_clear_radius (fallback 10, ProtectDef's default).</summary>
        public double RegenClearRadius { get; init; }

        /// <summary>regen_interval_seconds (fallback 1).</summary>
        public double RegenIntervalSeconds { get; init; }

        /// <summary>player_heal: players' heals restore knights at all.</summary>
        public bool PlayerHealEnabled { get; init; }

        /// <summary>player_heal_cap (fallback ProtectDef.DefaultMaxHealPerTenSeconds).</summary>
        public double PlayerHealCap { get; init; }

        /// <summary>player_heal_mult (fallback 1).</summary>
        public double PlayerHealMult { get; init; }

        /// <summary>shrine: on when set on, or (not set) when goals.json has a shrine block.</summary>
        public bool ShrineEnabled { get; init; }

        /// <summary>shrine_heal (fallback 0.15, ProtectDef's default).</summary>
        public double ShrineHeal { get; init; }

        /// <summary>shrine_cooldown_seconds (fallback 45, ProtectDef's default).</summary>
        public double ShrineCooldownSeconds { get; init; }

        /// <summary>shrine_heals_all.</summary>
        public bool ShrineHealsAll { get; init; }

        /// <summary>shrine_player_cooldown_seconds (fallback 0 = off).</summary>
        public double ShrinePlayerCooldownSeconds { get; init; }

        /// <summary>shrine_bind_range (fallback ProtectDef.DefaultShrineBindRange).</summary>
        public double ShrineBindRange { get; init; }

        /// <summary>protect.shrine.usedEffect (goals.json only): the effect a knight shows when a shrine restores it; null for none.</summary>
        public string ShrineUsedEffect { get; init; }

        // ---- the knights' cosmetic defense (protect.defendPose) ----

        /// <summary>defend_pose: on when set on, or (not set) when goals.json has a defendPose block.</summary>
        public bool DefendPoseEnabled { get; init; }

        /// <summary>protect.defendPose.reach (goals.json only; DefendPoseDef's default without one).</summary>
        public double DefendReach { get; init; }

        /// <summary>defend_swing_seconds (fallback protect.defendPose.swingSeconds).</summary>
        public double DefendSwingSeconds { get; init; }

        /// <summary>defend_swing_max_seconds (fallback protect.defendPose.swingMaxSeconds), never below <see cref="DefendSwingSeconds"/>.</summary>
        public double DefendSwingMaxSeconds { get; init; }

        /// <summary>protect.defendPose.turnBackSeconds (goals.json only).</summary>
        public double DefendTurnBackSeconds { get; init; }

        /// <summary>protect.defendPose.faceDegrees (goals.json only).</summary>
        public double DefendFaceDegrees { get; init; }

        // ---- monsters ----

        /// <summary>runner_speed_mult (fallback SpecialWaveDef's default).</summary>
        public double RunnerSpeedMult { get; init; }

        /// <summary>runner_attack_speed_mult (fallback SpecialWaveDef's default).</summary>
        public double RunnerAttackSpeedMult { get; init; }

        /// <summary>runner_health_mult (fallback SpecialWaveDef's default).</summary>
        public double RunnerHealthMult { get; init; }

        /// <summary>ranged_keep_distance (fallback SpecialWaveDef's default holdDistance).</summary>
        public double RangedKeepDistance { get; init; }

        /// <summary>ranged_slack (fallback 1.5).</summary>
        public double RangedSlack { get; init; }

        /// <summary>corrupted_self_mult (fallback CorruptedDef's default).</summary>
        public double CorruptedSelfMult { get; init; }

        /// <summary>corrupted_empower_mult (fallback CorruptedDef's default).</summary>
        public double CorruptedEmpowerMult { get; init; }

        /// <summary>protect.corrupted.empowerRadius (goals.json only; CorruptedDef's default without one).</summary>
        public double CorruptedEmpowerRadius { get; init; }

        /// <summary>corrupted_tint as a client palette, or 0 for none (also 0 for text that is not a palette).</summary>
        public uint CorruptedTintPalette { get; init; }

        private readonly WorldEventHoldRoleTuning[] roles = new WorldEventHoldRoleTuning[RoleCount];

        private const int RoleCount = (int)HoldDamageRole.Corrupted + 1;

        /// <summary>The tuning of <paramref name="role"/>; <see cref="WorldEventHoldRoleTuning.Identity"/> for None or an unknown role.</summary>
        public WorldEventHoldRoleTuning Role(HoldDamageRole role)
        {
            var i = (int)role;

            return i > 0 && i < RoleCount ? roles[i] ?? WorldEventHoldRoleTuning.Identity : WorldEventHoldRoleTuning.Identity;
        }

        // ---- the ranged ward (goals.json protect.ward only) ----

        /// <summary>The goal carries a ward block.</summary>
        public bool WardEnabled { get; init; }

        public double WardReduction { get; init; }

        public double WardShatterSeconds { get; init; }

        public WorldEventCombatKinds WardReduces { get; init; }

        public WorldEventCombatKinds WardBreakOn { get; init; }

        public string WardBreakEffect { get; init; }

        public string WardRestoreEffect { get; init; }

        // ---- the finale boss ----

        /// <summary>finale_min_kill_seconds (fallback ProtectDef.DefaultBossMinKillSeconds).</summary>
        public double FinaleMinKillSeconds { get; init; }

        /// <summary>finale_player_damage_mult (fallback 1).</summary>
        public double FinalePlayerDamageMult { get; init; }

        /// <summary>finale_enrage_seconds (fallback 0 = never).</summary>
        public double FinaleEnrageSeconds { get; init; }

        /// <summary>finale_enrage_mult (fallback 1.5).</summary>
        public double FinaleEnrageMult { get; init; }

        /// <summary>finale_swap_seconds (fallback 15).</summary>
        public double FinaleSwapSeconds { get; init; }

        /// <summary>finale_swap_max_seconds (fallback 20).</summary>
        public double FinaleSwapMaxSeconds { get; init; }

        /// <summary>finale_pull_share (fallback 0.06).</summary>
        public double FinalePullShare { get; init; }

        /// <summary>finale_pull_window_seconds (fallback 8).</summary>
        public double FinalePullWindowSeconds { get; init; }

        /// <summary>finale_pull_seconds (fallback 10).</summary>
        public double FinalePullSeconds { get; init; }

        /// <summary>finale_target: npcs, players or both; null when nothing decides it (the boss's own seeking).</summary>
        public string FinaleTarget { get; init; }

        /// <summary>
        /// The finale boss goes for the knights (finale_target npcs or both), so its hits on them take its own
        /// finale_min_kill_seconds slice and bucket. Derived from <see cref="FinaleTarget"/>; no row of its own.
        /// </summary>
        public bool FinaleTargetsKnights =>
            string.Equals(FinaleTarget, ProtectDef.FinaleTargetNpcs, StringComparison.Ordinal)
            || string.Equals(FinaleTarget, ProtectDef.FinaleTargetBoth, StringComparison.Ordinal);

        // ---- waves and participation (what the objective reads) ----

        /// <summary>max_waves (fallback ProtectDef.MaxHoldWaves).</summary>
        public int MaxWaves { get; init; }

        /// <summary>participant_radius, or null for the source's own radius.</summary>
        public double? ParticipantRadius { get; init; }

        /// <summary>afk_seconds (fallback 0 = off).</summary>
        public double AfkSeconds { get; init; }

        /// <summary>broadcast is "local": the run's lines go to the players within the participant radius only.</summary>
        public bool AnnouncesLocally { get; init; }

        // ---- H5: the spawner and wave planner's values (a separate sub-record; see WorldEventHoldSpawnerTuning) ----

        /// <summary>Knight placement, the wave mix, the corrupted assault, the finale boss and the spawn ring. Never null.</summary>
        public WorldEventHoldSpawnerTuning Spawner { get; init; } = WorldEventHoldSpawnerTuning.Defaults;

        // ---- H6/H7: the run's cadence, lines, participation and rewards (a separate sub-record; see WorldEventHoldRuntimeTuning) ----

        /// <summary>Wave count and size, wave timing, the milestone lines, the claim policy values. Never null.</summary>
        public WorldEventHoldRuntimeTuning Runtime { get; init; } = WorldEventHoldRuntimeTuning.Defaults;

        /// <summary>
        /// Builds the snapshot from resolved <paramref name="settings"/> for <paramref name="goal"/> (null allowed: no goal
        /// values) and an audience of <paramref name="participants"/> (below 1 counts as 1). Pure: no property reads.
        /// </summary>
        public static WorldEventHoldTuning From(WorldEventHoldSettings settings, GoalDef goal, int participants)
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));

            participants = Math.Max(1, participants);

            double N(string key, double fallback) => settings.Number(key) is double d && double.IsFinite(d) ? d : fallback;

            var protect = goal?.Protect;
            var runnerDef = protect?.SpecialWave(HoldWaveType.Runners) ?? new SpecialWaveDef();
            var rangedDef = protect?.SpecialWave(HoldWaveType.Ranged) ?? new SpecialWaveDef();
            var corruptedDef = protect?.EffectiveCorrupted ?? new CorruptedDef();
            var ward = protect?.Ward;
            var wardDef = ward ?? new WardDef();
            var poseDef = protect?.DefendPose ?? new DefendPoseDef();
            var defendSwing = N(K.DefendSwingSeconds, poseDef.SwingSeconds);

            var undefendedBase = N(K.UndefendedSeconds, 0);
            var undefendedPer = N(K.UndefendedSecondsPerParticipant, 0);
            var undefendedFloor = N(K.UndefendedSecondsFloor, 0);

            // The goals.json def's own rule, so the setting and the def can never disagree on the reduction.
            var undefended = new ProtectDef
            {
                UndefendedSeconds = undefendedBase,
                UndefendedSecondsPerParticipant = undefendedPer,
                UndefendedSecondsFloor = undefendedFloor
            }.EffectiveUndefendedSeconds(participants);

            WorldEventCombatKinds wardReduces = wardDef.ReducesKinds, wardBreakOn = wardDef.BreakOnKinds;

            var tuning = new WorldEventHoldTuning
            {
                Participants = participants,

                KnightMaxHealth = settings.Whole(K.KnightMaxHealth) ?? ProtectDef.TimeScaledMaxHealth,
                KnightDamageTakenMult = N(K.KnightDamageTakenMult, 1.0),
                UndefendedSecondsBase = undefendedBase,
                UndefendedSecondsPerParticipant = undefendedPer,
                UndefendedSecondsFloor = undefendedFloor,
                UndefendedSeconds = undefended,
                MinDeathSeconds = N(K.MinDeathSeconds, 0),
                MinSurvivors = (int)(settings.Whole(K.MinSurvivors) ?? ProtectDef.DefaultMinSurvivors),
                HoldSeconds = N(K.HoldSeconds, 0),

                CapBurstSeconds = N(K.CapBurstSeconds, 2),
                HitIntervalSeconds = N(K.HitIntervalSeconds, 2),
                HitIntervalMaxSeconds = N(K.HitIntervalMaxSeconds, 4),
                HealerMemorySeconds = N(K.HealerMemorySeconds, 20),
                BossDamageKeepSeconds = N(K.BossDamageKeepSeconds, 30),
                WaveResolveGraceSeconds = N(K.WaveResolveGraceSeconds, 15),

                HealReductionAfterHit = N(K.HealReductionAfterHit, 0),
                HealReductionSeconds = N(K.HealReductionSeconds, 5),
                HealBudgetPerWave = N(K.HealBudgetPerWave, 0),
                RescueHeal = N(K.RescueHeal, 0),
                RescueCapPerWave = N(K.RescueCapPerWave, 0.10),
                CorruptedKillHeal = N(K.CorruptedKillHeal, 0),
                HealBetweenWaves = N(K.HealBetweenWaves, 0),
                RegenPerSecond = N(K.RegenPerSecond, 0),
                RegenClearRadius = N(K.RegenClearRadius, 10),
                RegenIntervalSeconds = N(K.RegenIntervalSeconds, 1),
                PlayerHealEnabled = settings.Flag(K.PlayerHeal),
                PlayerHealCap = N(K.PlayerHealCap, ProtectDef.DefaultMaxHealPerTenSeconds),
                PlayerHealMult = N(K.PlayerHealMult, 1.0),
                ShrineEnabled = settings.OnOff(K.Shrine) ?? false,
                ShrineHeal = N(K.ShrineHeal, 0.15),
                ShrineCooldownSeconds = N(K.ShrineCooldownSeconds, 45),
                ShrineHealsAll = settings.Flag(K.ShrineHealsAll),
                ShrinePlayerCooldownSeconds = N(K.ShrinePlayerCooldownSeconds, 0),
                ShrineBindRange = N(K.ShrineBindRange, ProtectDef.DefaultShrineBindRange),
                ShrineUsedEffect = protect?.Shrine?.UsedEffect,

                DefendPoseEnabled = settings.OnOff(K.DefendPose) ?? protect?.DefendPose != null,
                DefendReach = poseDef.Reach,
                DefendSwingSeconds = defendSwing,
                DefendSwingMaxSeconds = Math.Max(defendSwing, N(K.DefendSwingMaxSeconds, poseDef.SwingMaxSeconds)),
                DefendTurnBackSeconds = poseDef.TurnBackSeconds,
                DefendFaceDegrees = poseDef.FaceDegrees,

                RunnerSpeedMult = N(K.RunnerSpeedMult, runnerDef.SpeedMult),
                RunnerAttackSpeedMult = N(K.RunnerAttackSpeedMult, runnerDef.AttackSpeedMult),
                RunnerHealthMult = N(K.RunnerHealthMult, runnerDef.HealthMult),
                RangedKeepDistance = N(K.RangedKeepDistance, rangedDef.HoldDistance),
                RangedSlack = N(K.RangedSlack, 1.5),
                CorruptedSelfMult = N(K.CorruptedSelfMult, corruptedDef.SelfMult),
                CorruptedEmpowerMult = N(K.CorruptedEmpowerMult, corruptedDef.EmpowerMult),
                CorruptedEmpowerRadius = corruptedDef.EmpowerRadius,
                CorruptedTintPalette = WorldEventTint.TryParsePalette(settings.Text(K.CorruptedTint), out var tint) ? tint : 0,

                WardEnabled = ward != null,
                WardReduction = wardDef.Reduction,
                WardShatterSeconds = wardDef.ShatterSeconds,
                WardReduces = wardReduces,
                WardBreakOn = wardBreakOn,
                WardBreakEffect = wardDef.BreakEffect,
                WardRestoreEffect = wardDef.RestoreEffect,

                FinaleMinKillSeconds = N(K.FinaleMinKillSeconds, ProtectDef.DefaultBossMinKillSeconds),
                FinalePlayerDamageMult = N(K.FinalePlayerDamageMult, 1.0),
                FinaleEnrageSeconds = N(K.FinaleEnrageSeconds, 0),
                FinaleEnrageMult = N(K.FinaleEnrageMult, 1.5),
                FinaleSwapSeconds = N(K.FinaleSwapSeconds, 15),
                FinaleSwapMaxSeconds = N(K.FinaleSwapMaxSeconds, 20),
                FinalePullShare = N(K.FinalePullShare, 0.06),
                FinalePullWindowSeconds = N(K.FinalePullWindowSeconds, 8),
                FinalePullSeconds = N(K.FinalePullSeconds, 10),
                FinaleTarget = settings.Text(K.FinaleTarget),

                MaxWaves = (int)(settings.Whole(K.MaxWaves) ?? ProtectDef.MaxHoldWaves),
                ParticipantRadius = settings.Number(K.ParticipantRadius) is double r && double.IsFinite(r) && r > 0 ? r : (double?)null,
                AfkSeconds = N(K.AfkSeconds, 0),
                AnnouncesLocally = string.Equals(settings.Text(K.Broadcast), "local", StringComparison.Ordinal),

                // H5 (spawner): its own sub-record, built from the same settings snapshot.
                Spawner = WorldEventHoldSpawnerTuning.From(settings, goal),

                // H6/H7 (runtime): its own sub-record, built from the same settings snapshot.
                Runtime = WorldEventHoldRuntimeTuning.From(settings, goal),
            };

            tuning.roles[(int)HoldDamageRole.Melee] = RoleFrom(settings, "melee", null, null);
            tuning.roles[(int)HoldDamageRole.Runner] = RoleFrom(settings, "runner", tuning.RunnerHealthMult, tuning.RunnerAttackSpeedMult);
            tuning.roles[(int)HoldDamageRole.Ranged] = RoleFrom(settings, "ranged", null, null);
            tuning.roles[(int)HoldDamageRole.Caster] = RoleFrom(settings, "caster", null, null);
            tuning.roles[(int)HoldDamageRole.Corrupted] = RoleFrom(settings, "corrupted", N(K.CorruptedHealthMult, corruptedDef.HealthMult), null);

            return tuning;
        }

        /// <summary>
        /// One role's row set. <paramref name="healthMult"/> / <paramref name="attackSpeedMult"/> stand in for the role's own
        /// rows where it has none (runner and corrupted health come from their goals.json rows, runner swing speed too).
        /// </summary>
        private static WorldEventHoldRoleTuning RoleFrom(WorldEventHoldSettings settings, string role, double? healthMult, double? attackSpeedMult)
        {
            var offset = Optional(settings, K.Role(role, K.RoleLevelOffset));

            return new WorldEventHoldRoleTuning
            {
                PlayerDamageMult = Optional(settings, K.Role(role, K.RolePlayerDamageMult)) ?? 1.0,
                NpcDamageMult = Optional(settings, K.Role(role, K.RoleNpcDamageMult)) ?? 1.0,
                HealthMult = healthMult ?? Optional(settings, K.Role(role, K.RoleHealthMult)),
                AttackSpeedMult = attackSpeedMult ?? Optional(settings, K.Role(role, K.RoleAttackSpeedMult)),
                LevelOffset = offset is double o ? (int)Math.Round(o, MidpointRounding.AwayFromZero) : 0
            };
        }

        /// <summary>A number row's finite value, or null when it has none - or when the snapshot has no such row (runner and corrupted have no health/swing row of their own).</summary>
        private static double? Optional(WorldEventHoldSettings settings, string key)
        {
            foreach (var k in settings.Keys)
                if (string.Equals(k, key, StringComparison.Ordinal))
                    return settings.Number(key) is double d && double.IsFinite(d) ? d : (double?)null;

            return null;
        }
    }
}
