using System.Collections.Generic;
using System.Text.Json.Serialization;

using ACE.Server.WorldEvents;

namespace ACE.Server.WorldEvents.Defs
{
    /// <summary>
    /// Kind of boss a WorldEventComposition may resolve to. The two built-in entries - see
    /// <see cref="BossDef.None"/> and <see cref="BossDef.FamilyChampion"/> - are always present in the
    /// store; every Named entry comes from Content/events/axes/bosses.json.
    /// </summary>
    public enum BossKind
    {
        None,
        FamilyChampion,
        Named
    }

    /// <summary>
    /// A boss axis entry. The store always seeds the two built-in static instances below, and adds one
    /// <see cref="BossKind.Named"/> entry per bosses.json row (BOSS-STANDARD.md section 5).
    ///
    /// Every JSON-backed field has a C# default identical to the shipped JSON value, so an entry that
    /// omits a dial behaves exactly as the shipped ones do rather than reading as "multiplier 0".
    /// </summary>
    public class BossDef
    {
        /// <summary>Added to the health multiplier per unit of audience power (see <see cref="ResolveHealthMult"/>).</summary>
        public const double DefaultPerPlayer = 0.15;

        /// <summary>The ceiling on the health multiplier, however large or strong the crowd gets.</summary>
        public const double DefaultHealthCap = 8.0;

        /// <summary>
        /// The C# default for <see cref="ThroughputFloorHealth"/> - and the ONE field here whose default is
        /// deliberately NOT the shipped JSON value (the shipped bosses carry 100000). 0 means "this boss has
        /// no throughput floor", which turns throughput scaling off for it and leaves the legacy power curve
        /// in charge even while the flag is on. An entry that omits the dial therefore behaves exactly as it
        /// did before the feature existed, which is the only safe reading of a missing key here: a floor
        /// invented from a default would silently re-base a boss's health.
        /// </summary>
        public const uint DefaultThroughputFloorHealth = 0;

        /// <summary>
        /// The C# and shipped default for <see cref="ThroughputCapHealth"/>: 6400000 = 800000 x the legacy
        /// <see cref="DefaultHealthCap"/> of 8, so the throughput path's ceiling is the same absolute health
        /// the power curve could already reach on a big authored boss.
        /// </summary>
        public const uint DefaultThroughputCapHealth = 6400000;

        /// <summary>The C# and shipped default for <see cref="TargetKillSeconds"/> (owner decision C16, 2026-08-18).</summary>
        public const double DefaultTargetKillSeconds = 300.0;

        /// <summary>
        /// The C# and shipped default for <see cref="ThroughputCalibration"/>. 1.0 because the wave-phase rate
        /// is treated as a FLOOR on what the group can do to one target: focus fire and stacked debuffs on a
        /// single boss out-damage the same group spread over a wave (decision C16).
        /// </summary>
        public const double DefaultThroughputCalibration = 1.0;

        /// <summary>The C# and shipped default for <see cref="MinSampleSeconds"/>.</summary>
        public const double DefaultMinSampleSeconds = 120.0;

        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("displayName")]
        public string DisplayName { get; set; }

        /// <summary>Never read from JSON - the store sets it (Named for every bosses.json row).</summary>
        [JsonIgnore]
        public BossKind Kind { get; set; }

        /// <summary>
        /// The specific named-boss wcid. 0 unless Kind == Named.
        /// </summary>
        [JsonPropertyName("wcid")]
        public uint NamedWcid { get; set; }

        /// <summary>
        /// Optional family gate. When non-empty, this boss may only be composed with that family and any
        /// other family refuses composition; when empty (the v1 shipped state), any family is allowed.
        /// </summary>
        [JsonPropertyName("familyId")]
        public string FamilyId { get; set; }

        /// <summary>
        /// The pool WorldEventAnnouncer.BossFailLine draws from when the run FAILS - the
        /// boss's own "I won" lines (BOSS-LINES.md "Win"). Never used on a success and never on an abort.
        /// Null in JSON is normalised to an empty list by the store.
        /// </summary>
        [JsonPropertyName("failLines")]
        public List<string> FailLines { get; set; } = new List<string>();

        /// <summary>
        /// OPTIONAL base-health override, read by WorldEventSpawner.ApplyBaseHealthOverride. 0 (the
        /// shipped state, and the absent-key state) means "use the health the weenie authored" - the
        /// weenie is the authority, this is only an escape hatch.
        ///
        /// When set, it is the boss's maximum health BEFORE <see cref="ResolveHealthMult"/> is applied, so
        /// the effective ceiling is baseHealth x <see cref="HealthCap"/>. Keep that product inside
        /// int.MaxValue (2147483647): a single ratchet raise is handed to Creature.UpdateVitalDelta, whose
        /// delta is an int, and WorldEventSpawner.ClampVitalDelta pins anything larger rather than letting
        /// it wrap negative - a clamp is a correctness backstop, not a supported configuration.
        /// </summary>
        [JsonPropertyName("baseHealth")]
        public uint BaseHealth { get; set; }

        /// <summary>Added to the multiplier per unit of audience power sum (BOSS-STANDARD.md section 3).</summary>
        [JsonPropertyName("perPlayer")]
        public double PerPlayer { get; set; } = DefaultPerPlayer;

        /// <summary>
        /// The ceiling on the multiplier. With <see cref="BaseHealth"/> this bounds the boss's maximum
        /// health at baseHealth x cap, which must stay inside int.MaxValue (2147483647) - see
        /// <see cref="BaseHealth"/> for why.
        /// </summary>
        [JsonPropertyName("cap")]
        public double HealthCap { get; set; } = DefaultHealthCap;

        /// <summary>
        /// The health this boss is RE-BASED to before the throughput multiplier is applied, and the switch
        /// that turns throughput scaling on for this boss at all (flag
        /// world_events_boss_throughput_scaling_enabled; WorldEventThroughput.ResolveBossMult).
        ///
        /// 0 - the C# default, see <see cref="DefaultThroughputFloorHealth"/> - means this boss never takes
        /// the throughput path, whatever the flag says. Non-zero is both the rebase target (handed to
        /// WorldEventSpawner.ApplyBaseHealthOverride through WorldEvent.BossRebaseHealth, in place of
        /// <see cref="BaseHealth"/>) and the denominator the multiplier is expressed against, so the boss can
        /// never end up below it: multiplier 1.0 IS the floor.
        /// </summary>
        [JsonPropertyName("throughputFloorHealth")]
        public uint ThroughputFloorHealth { get; set; } = DefaultThroughputFloorHealth;

        /// <summary>
        /// The absolute health ceiling on the throughput path - the counterpart of <see cref="HealthCap"/> on
        /// the power path, expressed in health rather than as a multiplier because the throughput path's base
        /// is <see cref="ThroughputFloorHealth"/> rather than the weenie's own number. A cap at or below the
        /// floor means "no headroom", i.e. the boss is always exactly the floor.
        ///
        /// Bounded by int.MaxValue (2147483647) for the reason <see cref="BaseHealth"/> gives - a ratchet
        /// raise is handed to Creature.UpdateVitalDelta as an int - and the store repairs anything above it
        /// down to that bound with a diagnostic.
        /// </summary>
        [JsonPropertyName("throughputCapHealth")]
        public uint ThroughputCapHealth { get; set; } = DefaultThroughputCapHealth;

        /// <summary>
        /// How long the boss should take to kill, in seconds, if the group keeps clearing health at the rate
        /// it demonstrated during the wave phase (owner decision C16: 300 s).
        /// </summary>
        [JsonPropertyName("targetKillSeconds")]
        public double TargetKillSeconds { get; set; } = DefaultTargetKillSeconds;

        /// <summary>
        /// Multiplied into the measured rate before it is turned into health. 1.0 ships because the wave rate
        /// is treated as a floor on single-target throughput, not an estimate of it (see
        /// <see cref="DefaultThroughputCalibration"/>). Below 1 makes the boss softer than the measurement
        /// implies, above 1 harder.
        /// </summary>
        [JsonPropertyName("throughputCalibration")]
        public double ThroughputCalibration { get; set; } = DefaultThroughputCalibration;

        /// <summary>
        /// The shortest wave phase that produces a usable measurement. Below it the run falls back to the
        /// power curve, because a rate drawn from a handful of seconds is one pull's luck rather than a
        /// group's pace.
        /// </summary>
        [JsonPropertyName("minSampleSeconds")]
        public double MinSampleSeconds { get; set; } = DefaultMinSampleSeconds;

        /// <summary>
        /// <see cref="PerPlayer"/>, or world_events_boss_per_player when that property is non-zero (live
        /// global override, TECH-DESIGN "Live overrides"). Read fresh on every access - see
        /// <see cref="WorldEventOverrides"/>.
        /// </summary>
        [JsonIgnore]
        public double EffectivePerPlayer => WorldEventOverrides.Double("world_events_boss_per_player", PerPlayer);

        /// <summary><see cref="HealthCap"/>, or world_events_boss_health_cap when non-zero.</summary>
        [JsonIgnore]
        public double EffectiveHealthCap => WorldEventOverrides.Double("world_events_boss_health_cap", HealthCap);

        /// <summary><see cref="ThroughputFloorHealth"/>, or world_events_boss_throughput_floor_health when non-zero.</summary>
        [JsonIgnore]
        public uint EffectiveThroughputFloorHealth => (uint)WorldEventOverrides.Long("world_events_boss_throughput_floor_health", ThroughputFloorHealth);

        /// <summary><see cref="ThroughputCapHealth"/>, or world_events_boss_throughput_cap_health when non-zero.</summary>
        [JsonIgnore]
        public uint EffectiveThroughputCapHealth => (uint)WorldEventOverrides.Long("world_events_boss_throughput_cap_health", ThroughputCapHealth);

        /// <summary><see cref="TargetKillSeconds"/>, or world_events_boss_target_kill_seconds when non-zero.</summary>
        [JsonIgnore]
        public double EffectiveTargetKillSeconds => WorldEventOverrides.Double("world_events_boss_target_kill_seconds", TargetKillSeconds);

        /// <summary><see cref="ThroughputCalibration"/>, or world_events_boss_throughput_calibration when non-zero.</summary>
        [JsonIgnore]
        public double EffectiveThroughputCalibration => WorldEventOverrides.Double("world_events_boss_throughput_calibration", ThroughputCalibration);

        /// <summary><see cref="MinSampleSeconds"/>, or world_events_boss_min_sample_seconds when non-zero.</summary>
        [JsonIgnore]
        public double EffectiveMinSampleSeconds => WorldEventOverrides.Double("world_events_boss_min_sample_seconds", MinSampleSeconds);

        /// <summary>
        /// clamp(1 + perPlayer * powerSum, 1.0, cap) - the power-weighted boss health curve
        /// (BOSS-STANDARD.md section 3, D6 - pure), resolved against the EFFECTIVE perPlayer/cap
        /// (<see cref="EffectivePerPlayer"/>/<see cref="EffectiveHealthCap"/>) so a live override property
        /// takes effect on the next resolve without a reload.
        ///
        /// A Named boss's health multiplier is THIS AND NOTHING ELSE: it deliberately ignores the
        /// count-based crowdHealth and the pace controller's health step, both of which are tuned for
        /// waves. Family champions keep crowd x pace.
        ///
        /// The cap is itself floored at 1.0 before it is applied, so a mis-authored cap below 1 turns the
        /// feature OFF rather than making the boss WEAKER than the weenie authored it. See
        /// <see cref="ResolveHealthMult(double, double, double)"/> for the dial-free pure form - this
        /// instance method is the only place PropertyManager is consulted; the pure form never is.
        /// </summary>
        public double ResolveHealthMult(double powerSum)
        {
            return ResolveHealthMult(EffectivePerPlayer, EffectiveHealthCap, powerSum);
        }

        /// <summary>The dial-free form of <see cref="ResolveHealthMult(double)"/>, for tests and callers holding raw values.</summary>
        public static double ResolveHealthMult(double perPlayer, double cap, double powerSum)
        {
            if (!IsFinite(perPlayer) || perPlayer < 0)
                perPlayer = DefaultPerPlayer;

            if (!IsFinite(powerSum) || powerSum < 0)
                powerSum = 0;

            var ceiling = !IsFinite(cap) || cap < 1.0 ? 1.0 : cap;

            var mult = 1.0 + (perPlayer * powerSum);

            if (!IsFinite(mult))
                return ceiling;

            if (mult > ceiling)
                mult = ceiling;

            return mult < 1.0 ? 1.0 : mult;
        }

        private static bool IsFinite(double value)
        {
            return double.IsFinite(value);
        }

        public static readonly BossDef None = new BossDef
        {
            Id = "none",
            DisplayName = "None",
            Kind = BossKind.None,
            NamedWcid = 0
        };

        public static readonly BossDef FamilyChampion = new BossDef
        {
            Id = "family_champion",
            DisplayName = "Family Champion",
            Kind = BossKind.FamilyChampion,
            NamedWcid = 0
        };
    }
}
