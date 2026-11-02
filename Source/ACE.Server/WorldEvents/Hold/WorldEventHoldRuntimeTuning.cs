using System;
using System.Collections.Generic;

using ACE.Server.WorldEvents.Defs;

using K = ACE.Server.WorldEvents.HoldKeys;

namespace ACE.Server.WorldEvents.Hold
{
    /// <summary>
    /// World Events Hold H6/H7: the values the run's own cadence, its lines and its claim policy read - wave count and size,
    /// the wave timing, the milestone lines, participation and the rewards - resolved into one immutable snapshot that rides
    /// on <see cref="WorldEventHoldTuning.Runtime"/>. Kept apart from the per-hit fields (H4) and the spawner's
    /// (<see cref="WorldEventHoldSpawnerTuning"/>, H5) so no slice edits another's lines.
    ///
    /// Each value is the resolved setting (a set property, else the goals.json value, else the row's code default). A count
    /// whose row resolves to "no preference" stays null here, and the objective falls back to the goal's own value.
    /// THREADING: immutable, so safe from any thread.
    /// </summary>
    public sealed record WorldEventHoldRuntimeTuning
    {
        /// <summary>The snapshot with no goal and no property set.</summary>
        public static WorldEventHoldRuntimeTuning Defaults { get; } = From(WorldEventHoldSettingsResolver.Resolve(null, _ => null), null);

        /// <summary>The milestone percents when milestone_percents is not readable: 75, 50, 25.</summary>
        public static readonly IReadOnlyList<int> DefaultMilestonePercents = new[] { 75, 50, 25 };

        // ---- waves ----

        /// <summary>wave_count_base (fallback protect.waves.base), or null when neither says.</summary>
        public int? WaveCountBase { get; init; }

        /// <summary>wave_count_per_player (fallback protect.waves.perParticipant), or null.</summary>
        public double? WaveCountPerPlayer { get; init; }

        /// <summary>wave_count_cap (fallback protect.waves.cap), or null.</summary>
        public int? WaveCountCap { get; init; }

        /// <summary>wave_size_base (fallback protect.waveSize.base), or null.</summary>
        public int? WaveSizeBase { get; init; }

        /// <summary>wave_size_per_player (fallback protect.waveSize.perParticipant), or null.</summary>
        public double? WaveSizePerPlayer { get; init; }

        /// <summary>wave_size_cap (fallback protect.waveSize.cap), or null.</summary>
        public int? WaveSizeCap { get; init; }

        /// <summary>wave_size_base_players (0 = growth from the first player).</summary>
        public int WaveSizeBasePlayers { get; init; }

        /// <summary>wave_size_max_players (0 = no limit).</summary>
        public int WaveSizeMaxPlayers { get; init; }

        /// <summary>final_wave_bonus (default 0.25; 0 = no bonus).</summary>
        public double FinalWaveBonus { get; init; } = 0.25;

        /// <summary>first_wave_delay_seconds (default 5): the first wave goes once the knights stand, or this long after the start.</summary>
        public double FirstWaveDelaySeconds { get; init; } = 5;

        /// <summary>wave_interval_seconds (fallback protect.waveIntervalSeconds; 0 = the source's own interval).</summary>
        public double WaveIntervalSeconds { get; init; }

        /// <summary>The interval the cadence uses (pure): <see cref="WaveIntervalSeconds"/>, or the source's own interval when it is 0.</summary>
        public double EffectiveWaveIntervalSeconds(double sourceIntervalSeconds) =>
            WaveIntervalSeconds > 0 ? WaveIntervalSeconds : (double.IsFinite(sourceIntervalSeconds) ? Math.Max(0, sourceIntervalSeconds) : 0);

        /// <summary>breather_seconds (fallback protect.minWaveGapSeconds).</summary>
        public double BreatherSeconds { get; init; } = ProtectDef.DefaultMinWaveGapSeconds;

        /// <summary>max_wave_interval_seconds (fallback protect.maxWaveIntervalSeconds; 0 = no ceiling).</summary>
        public double MaxWaveIntervalSeconds { get; init; }

        /// <summary>next_wave_alive_share (fallback protect.nextWaveAliveShare).</summary>
        public double NextWaveAliveShare { get; init; } = ProtectDef.DefaultNextWaveAliveShare;

        /// <summary>protect.nextWaveRequiresBoth (goals.json only).</summary>
        public bool NextWaveRequiresBoth { get; init; }

        // ---- messages ----

        /// <summary>health_milestones (fallback protect.healthMilestones): the world milestone lines are on.</summary>
        public bool HealthMilestones { get; init; } = true;

        /// <summary>milestone_percents parsed (descending); empty for "none".</summary>
        public IReadOnlyList<int> MilestonePercents { get; init; } = DefaultMilestonePercents;

        // ---- rewards and participation (H7) ----

        /// <summary>participants_per_cache, or null for the reward profile's own participantsPerCache.</summary>
        public int? ParticipantsPerCache { get; init; }

        /// <summary>min_contribution: the damage share a claimant needs for the crate (0 = off).</summary>
        public double MinContribution { get; init; }

        /// <summary>failure_reward: what a failed run's cache pays.</summary>
        public FailureRewardMode FailureReward { get; init; } = FailureRewardMode.Consolation;

        /// <summary>min_presence_seconds: the presence a claimant needs for the crate (0 = off).</summary>
        public double MinPresenceSeconds { get; init; }

        /// <summary>admins_count: staff count toward the audience scaling.</summary>
        public bool AdminsCount { get; init; }

        /// <summary>Builds the snapshot (pure: no property reads). <paramref name="goal"/> may be null.</summary>
        public static WorldEventHoldRuntimeTuning From(WorldEventHoldSettings settings, GoalDef goal)
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));

            double N(string key, double fallback) => settings.Number(key) is double d && double.IsFinite(d) ? d : fallback;
            int? I(string key) => settings.Whole(key) is long l ? (int)Math.Clamp(l, int.MinValue, int.MaxValue) : (int?)null;
            double? D(string key) => settings.Number(key) is double d && double.IsFinite(d) ? d : (double?)null;

            var protect = goal?.Protect;

            var milestoneText = settings.Text(K.MilestonePercents);
            IReadOnlyList<int> milestones = DefaultMilestonePercents;

            if (milestoneText != null && WorldEventHoldRules.TryParseMilestonePercents(milestoneText, out var parsed, out _))
                milestones = parsed;

            return new WorldEventHoldRuntimeTuning
            {
                WaveCountBase = I(K.WaveCountBase) ?? protect?.Waves?.Base,
                WaveCountPerPlayer = D(K.WaveCountPerPlayer) ?? protect?.Waves?.PerParticipant,
                WaveCountCap = I(K.WaveCountCap) ?? protect?.Waves?.Cap,
                WaveSizeBase = I(K.WaveSizeBase) ?? protect?.WaveSize?.Base,
                WaveSizePerPlayer = D(K.WaveSizePerPlayer) ?? protect?.WaveSize?.PerParticipant,
                WaveSizeCap = I(K.WaveSizeCap) ?? protect?.WaveSize?.Cap,
                WaveSizeBasePlayers = Math.Max(0, I(K.WaveSizeBasePlayers) ?? 0),
                WaveSizeMaxPlayers = Math.Max(0, I(K.WaveSizeMaxPlayers) ?? 0),
                FinalWaveBonus = Math.Max(0, N(K.FinalWaveBonus, 0.25)),
                FirstWaveDelaySeconds = Math.Max(0, N(K.FirstWaveDelaySeconds, 5)),
                WaveIntervalSeconds = Math.Max(0, N(K.WaveIntervalSeconds, protect?.WaveIntervalSeconds ?? 0)),
                BreatherSeconds = Math.Max(0, N(K.BreatherSeconds, protect?.MinWaveGapSeconds ?? ProtectDef.DefaultMinWaveGapSeconds)),
                MaxWaveIntervalSeconds = Math.Max(0, N(K.MaxWaveIntervalSeconds, protect?.MaxWaveIntervalSeconds ?? 0)),
                NextWaveAliveShare = Math.Clamp(N(K.NextWaveAliveShare, protect?.NextWaveAliveShare ?? ProtectDef.DefaultNextWaveAliveShare), 0, 1),
                NextWaveRequiresBoth = protect?.NextWaveRequiresBoth ?? false,

                HealthMilestones = settings.OnOff(K.HealthMilestones) ?? protect?.HealthMilestones ?? true,
                MilestonePercents = milestones,

                ParticipantsPerCache = I(K.ParticipantsPerCache) is int ppc && ppc > 0 ? ppc : (int?)null,
                MinContribution = Math.Clamp(N(K.MinContribution, 0), 0, 1),
                FailureReward = ParseFailureReward(settings.Text(K.FailureReward)),
                MinPresenceSeconds = Math.Max(0, N(K.MinPresenceSeconds, 0)),
                AdminsCount = settings.Flag(K.AdminsCount),
            };
        }

        /// <summary>failure_reward text to its mode (pure): "partial", "none", anything else (consolation, unset, unknown) Consolation.</summary>
        public static FailureRewardMode ParseFailureReward(string text)
        {
            switch (text?.Trim().ToLowerInvariant())
            {
                case "partial":
                    return FailureRewardMode.Partial;

                case "none":
                    return FailureRewardMode.None;

                default:
                    return FailureRewardMode.Consolation;
            }
        }

        /// <summary>
        /// The wave target's scaled count (pure): this snapshot's rows, each falling back to <paramref name="goalWaves"/>
        /// (protect.waves) when it has none; null when neither says anything (no wave-based hold).
        /// </summary>
        public ScaledCount WaveCount(ScaledCount goalWaves)
        {
            if (WaveCountBase == null && goalWaves == null)
                return null;

            return new ScaledCount
            {
                Base = WaveCountBase ?? goalWaves?.Base ?? 1,
                PerParticipant = WaveCountPerPlayer ?? goalWaves?.PerParticipant ?? 0,
                Cap = WaveCountCap ?? goalWaves?.Cap ?? WaveCountBase ?? goalWaves?.Base ?? 1,
            };
        }

        /// <summary>
        /// The wave size the run's pick is sized with (pure): the wave_size_* rows over protect.waveSize, owning every dial
        /// so the global world_events_wave_count_* override never replaces it. Null when neither sets a size.
        /// </summary>
        public ScaledCount WaveSize(ScaledCount goalSize)
        {
            if (WaveSizeBase == null && goalSize == null)
                return null;

            return new ScaledCount
            {
                Base = WaveSizeBase ?? goalSize?.Base ?? 1,
                PerParticipant = WaveSizePerPlayer ?? goalSize?.PerParticipant ?? 0,
                Cap = WaveSizeCap ?? goalSize?.Cap ?? WaveSizeBase ?? goalSize?.Base ?? 1,
                PlayersFrom = WaveSizeBasePlayers,
                PlayersUpTo = WaveSizeMaxPlayers,
                OwnsBase = true,
                OwnsPerParticipant = true,
                OwnsCap = true,
            };
        }
    }
}
