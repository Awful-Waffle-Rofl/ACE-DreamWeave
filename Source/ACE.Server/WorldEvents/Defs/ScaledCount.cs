using System;
using System.Text.Json.Serialization;

namespace ACE.Server.WorldEvents.Defs
{
    /// <summary>
    /// A count/quantity axis that scales with participant count, per TECH-DESIGN 5.4.
    /// Resolves to min(cap, base + ceil(perParticipant * participants)), floored at base.
    /// </summary>
    public class ScaledCount
    {
        [JsonPropertyName("base")]
        public int Base { get; set; }

        [JsonPropertyName("perParticipant")]
        public double PerParticipant { get; set; }

        [JsonPropertyName("cap")]
        public int Cap { get; set; }

        public int Resolve(int participants)
        {
            return Resolve(Base, PerParticipant, Cap, participants);
        }

        /// <summary>
        /// The UNCAPPED value, base + ceil(perParticipant * participants) (TECH-DESIGN 2.15). What
        /// <see cref="Resolve"/> would have returned had there been no <see cref="Cap"/>.
        ///
        /// The overflow-champion rule is built on the difference between this and the size a wave actually
        /// gets: the demand a big turnout generates does not vanish because the cap swallowed it, it is
        /// converted into champions instead.
        /// </summary>
        public int ResolveRaw(int participants)
        {
            return ResolveRaw(Base, PerParticipant, participants);
        }

        /// <summary>
        /// The dial-free pure form of <see cref="Resolve(int)"/>. This class is shared by SourceThemeDef's
        /// waveCount AND objectiveHealth, and only waveCount takes a live world_events_wave_count_* override
        /// (TECH-DESIGN "Live overrides"), so the override resolution happens at the SourceThemeDef call
        /// site (<see cref="SourceThemeDef.EffectiveWaveCountBase"/> and friends), never inside this class -
        /// an instance-level override here would silently also apply to objectiveHealth, which is never
        /// meant to move.
        /// </summary>
        public static int Resolve(int @base, double perParticipant, int cap, int participants)
        {
            return Math.Max(@base, Math.Min(cap, ResolveRaw(@base, perParticipant, participants)));
        }

        /// <summary>The dial-free pure form of <see cref="ResolveRaw(int)"/>. See <see cref="Resolve(int, double, int, int)"/>.</summary>
        public static int ResolveRaw(int @base, double perParticipant, int participants)
        {
            return @base + (int)Math.Ceiling(perParticipant * participants);
        }
    }
}
