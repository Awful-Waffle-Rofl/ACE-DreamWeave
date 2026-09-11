using System;
using System.Text.Json.Serialization;

using ACE.Server.WorldEvents;

namespace ACE.Server.WorldEvents.Defs
{
    /// <summary>
    /// The "pace" block of a source theme (TECH-DESIGN 2.15): the tunables of the real-time difficulty
    /// controller, which watches how long each wave actually takes to clear and pushes the next pick
    /// harder or easier.
    ///
    /// Every field has a C# default identical to the shipped JSON value, so a theme that omits the whole
    /// "pace" key still gets a working controller rather than one whose window is [0, 0].
    /// </summary>
    public class PaceDef
    {
        public const double DefaultMinWaveSeconds = 60.0;
        public const double DefaultMaxWaveSeconds = 120.0;
        public const int DefaultQuantityStep = 2;
        public const double DefaultHealthStep = 0.25;
        public const double DefaultHealthCap = 4.0;

        /// <summary>A wave cleared faster than this is evidence the run is too easy.</summary>
        [JsonPropertyName("minWaveSeconds")]
        public double MinWaveSeconds { get; set; } = DefaultMinWaveSeconds;

        /// <summary>A wave that takes longer than this - or is still standing this long - is too hard.</summary>
        [JsonPropertyName("maxWaveSeconds")]
        public double MaxWaveSeconds { get; set; } = DefaultMaxWaveSeconds;

        /// <summary>How many extra creatures one speed-up step adds to the next pick.</summary>
        [JsonPropertyName("quantityStep")]
        public int QuantityStep { get; set; } = DefaultQuantityStep;

        /// <summary>How much one step moves the health multiplier, in either direction.</summary>
        [JsonPropertyName("healthStep")]
        public double HealthStep { get; set; } = DefaultHealthStep;

        /// <summary>The ceiling on the pace half of the health multiplier.</summary>
        [JsonPropertyName("healthCap")]
        public double HealthCap { get; set; } = DefaultHealthCap;

        /// <summary>
        /// <see cref="MinWaveSeconds"/>, or world_events_pace_min_wave_seconds when that property is SET
        /// (positive and finite - see <see cref="WorldEventOverrides"/>; a zero or negative override is
        /// read as "unset", i.e. the JSON value stands). Read fresh on every access. Validates only its OWN
        /// dial - a min override and a max override can each be individually valid and still invert the
        /// PAIR when read together, which is what <see cref="EffectiveWindow"/> exists to catch.
        /// </summary>
        [JsonIgnore]
        public double EffectiveMinWaveSeconds => WorldEventOverrides.Double("world_events_pace_min_wave_seconds", MinWaveSeconds);

        /// <summary><see cref="MaxWaveSeconds"/>, or world_events_pace_max_wave_seconds when SET. See <see cref="EffectiveMinWaveSeconds"/>.</summary>
        [JsonIgnore]
        public double EffectiveMaxWaveSeconds => WorldEventOverrides.Double("world_events_pace_max_wave_seconds", MaxWaveSeconds);

        /// <summary><see cref="QuantityStep"/>, or world_events_pace_quantity_step when SET.</summary>
        [JsonIgnore]
        public int EffectiveQuantityStep => (int)WorldEventOverrides.Long("world_events_pace_quantity_step", QuantityStep);

        /// <summary><see cref="HealthStep"/>, or world_events_pace_health_step when SET.</summary>
        [JsonIgnore]
        public double EffectiveHealthStep => WorldEventOverrides.Double("world_events_pace_health_step", HealthStep);

        /// <summary><see cref="HealthCap"/>, or world_events_pace_health_cap when SET.</summary>
        [JsonIgnore]
        public double EffectiveHealthCap => WorldEventOverrides.Double("world_events_pace_health_cap", HealthCap);

        /// <summary>
        /// <see cref="EffectiveMinWaveSeconds"/>/<see cref="EffectiveMaxWaveSeconds"/> resolved TOGETHER as an
        /// ordered pair, min strictly less than max. Each Effective* accessor above only ever validates its
        /// own single dial, so two individually-valid overrides
        /// (world_events_pace_min_wave_seconds/world_events_pace_max_wave_seconds) can still invert the pair
        /// when read separately - a min above the max would flip PushHarder/EaseOff's sense entirely
        /// (a wave that cleared "too fast" would also read as "too slow"). This is the ONE place that pair
        /// is resolved, so <see cref="WorldEventPaceController"/> (the only production reader) never has to
        /// duplicate the check.
        ///
        /// Repair strategy mirrors WorldEventAxisStore.ValidateDifficulty's "never make it worse, always
        /// repair" rule for the JSON-authored pair, but REPAIRS BY CLAMPING (min pulled down to max - 1,
        /// floored at 0) rather than resetting both to the compiled-in defaults - unlike a JSON load, only
        /// ONE of the two dials here may actually be an intentional live override, and resetting both would
        /// silently discard the other one's still-valid value.
        /// </summary>
        public (double Min, double Max, bool WasInverted) EffectiveWindow()
        {
            var min = EffectiveMinWaveSeconds;
            var max = EffectiveMaxWaveSeconds;

            if (min < max)
                return (min, max, false);

            return (Math.Max(0, max - 1.0), max, true);
        }
    }
}
