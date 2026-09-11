using System;
using System.Text.Json.Serialization;

using ACE.Server.WorldEvents;

namespace ACE.Server.WorldEvents.Defs
{
    /// <summary>
    /// The "crowd health" block of a source theme (TECH-DESIGN 2.15). A big turnout does not only mean more
    /// monsters, it means each one has to survive more incoming damage per second, so past
    /// <see cref="StartAt"/> participants every extra body raises every wave creature's maximum health.
    ///
    /// Every field has a C# default identical to the shipped JSON value, so a theme that omits the whole
    /// "crowdHealth" key behaves exactly as the shipped themes do rather than reading as "multiplier 0".
    /// </summary>
    public class CrowdHealthDef
    {
        public const int DefaultStartAt = 8;
        public const double DefaultPerParticipant = 0.1;
        public const double DefaultCap = 3.0;

        /// <summary>Participant count at which the multiplier starts to move. At or below this it is 1.0.</summary>
        [JsonPropertyName("startAt")]
        public int StartAt { get; set; } = DefaultStartAt;

        /// <summary>Added to the multiplier per participant above <see cref="StartAt"/>.</summary>
        [JsonPropertyName("perParticipant")]
        public double PerParticipant { get; set; } = DefaultPerParticipant;

        /// <summary>The ceiling on the multiplier, however large the crowd gets.</summary>
        [JsonPropertyName("cap")]
        public double Cap { get; set; } = DefaultCap;

        /// <summary>
        /// <see cref="StartAt"/>, or world_events_crowd_health_start_at when that property is non-zero (live
        /// global override, TECH-DESIGN "Live overrides"). Read fresh on every access - see
        /// <see cref="WorldEventOverrides"/>.
        /// </summary>
        [JsonIgnore]
        public int EffectiveStartAt => (int)WorldEventOverrides.Long("world_events_crowd_health_start_at", StartAt);

        /// <summary><see cref="PerParticipant"/>, or world_events_crowd_health_per_participant when non-zero.</summary>
        [JsonIgnore]
        public double EffectivePerParticipant => WorldEventOverrides.Double("world_events_crowd_health_per_participant", PerParticipant);

        /// <summary><see cref="Cap"/>, or world_events_crowd_health_cap when non-zero.</summary>
        [JsonIgnore]
        public double EffectiveCap => WorldEventOverrides.Double("world_events_crowd_health_cap", Cap);

        /// <summary>
        /// min(cap, 1 + perParticipant * max(0, count - startAt)), never below 1.0, resolved against the
        /// EFFECTIVE dials (<see cref="EffectiveStartAt"/>/<see cref="EffectivePerParticipant"/>/
        /// <see cref="EffectiveCap"/>) so a live override property takes effect on the next resolve without a
        /// reload. See <see cref="Resolve(int, double, int, double)"/> for the dial-free pure form - this
        /// instance method is the only place PropertyManager is consulted; the pure form never is.
        /// </summary>
        public double Resolve(int participantCount)
        {
            return Resolve(EffectiveStartAt, EffectivePerParticipant, participantCount, EffectiveCap);
        }

        /// <summary>
        /// The dial-free form of <see cref="Resolve(int)"/> (D6 - pure): min(cap, 1 + perParticipant *
        /// max(0, count - startAt)), never below 1.0.
        ///
        /// The cap is itself floored at 1.0 before it is applied, so a mis-authored cap below 1 turns the
        /// feature OFF rather than making wave creatures WEAKER than the weenie authored them.
        /// </summary>
        public static double Resolve(int startAt, double perParticipant, int participantCount, double cap)
        {
            var over = Math.Max(0, participantCount - startAt);

            var mult = 1.0 + (perParticipant * over);

            var ceiling = double.IsNaN(cap) || cap < 1.0 ? 1.0 : cap;

            if (mult > ceiling)
                mult = ceiling;

            return mult < 1.0 ? 1.0 : mult;
        }
    }
}
