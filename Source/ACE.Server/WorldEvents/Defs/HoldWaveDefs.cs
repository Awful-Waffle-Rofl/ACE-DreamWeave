using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ACE.Server.WorldEvents.Defs
{
    /// <summary>The kinds of wave a wave-based Hold can roll (protect.specialWaves).</summary>
    public enum HoldWaveType
    {
        /// <summary>An ordinary wave.</summary>
        Normal,

        /// <summary>Every seeker in the wave goes for the SAME target, and that target warns which side.</summary>
        Breach,

        /// <summary>Fewer monsters, faster and with less health, so they reach the targets first.</summary>
        Runners,

        /// <summary>Monsters drawn from the family's ranged and caster members, attacking from a distance.</summary>
        Ranged,

        /// <summary>A few monsters ignore the targets and hunt players, healers first.</summary>
        Hunters,

        /// <summary>The last counted wave: a family champion leads it, going for the healthiest target.</summary>
        Finale
    }

    /// <summary>
    /// One protect.specialWaves entry. Which fields matter depends on <see cref="Type"/>; the rest are
    /// ignored. Player-facing text (announce, lines) is ASCII only, like every other line.
    /// </summary>
    public class SpecialWaveDef
    {
        /// <summary>breach, runners, ranged, hunters or finale (case-insensitive).</summary>
        [JsonPropertyName("type")]
        public string Type { get; set; }

        /// <summary>Relative chance among the special types when a wave rolls special. Finale ignores it.</summary>
        [JsonPropertyName("weight")]
        public double Weight { get; set; } = 1.0;

        /// <summary>Local messages, one picked at random, when this wave starts. Tokens: {family} {Family} {noun} {nounPlural} {anchor} {side} (breach) {champion} (finale).</summary>
        [JsonPropertyName("announce")]
        public List<string> Announce { get; set; } = new List<string>();

        /// <summary>Breach only: what the threatened target says (local speech). Same tokens as announce, {side} included.</summary>
        [JsonPropertyName("lines")]
        public List<string> Lines { get; set; } = new List<string>();

        /// <summary>Runners: the share of the wave that is kept (0 to 1). Default 0.6.</summary>
        [JsonPropertyName("countMult")]
        public double CountMult { get; set; } = 0.6;

        /// <summary>Runners: the runners' health as a share of normal (0 to 1). Default 0.6.</summary>
        [JsonPropertyName("healthMult")]
        public double HealthMult { get; set; } = 0.6;

        /// <summary>Runners: run speed multiplier (1 to 3). Default 1.5.</summary>
        [JsonPropertyName("speedMult")]
        public double SpeedMult { get; set; } = 1.5;

        /// <summary>Hunters: how many of the wave hunt players instead of the targets. Default 2.</summary>
        [JsonPropertyName("hunters")]
        public int Hunters { get; set; } = 2;

        /// <summary>Parsed from <see cref="Type"/> at load. Never read from JSON.</summary>
        [JsonIgnore]
        public HoldWaveType TypeKind { get; set; }
    }

    /// <summary>
    /// One protect.countByParticipants entry: from <see cref="MinPlayers"/> participants up, place
    /// <see cref="Count"/> targets on a ring of <see cref="Radius"/> metres. The entry with the highest
    /// MinPlayers not above the crowd wins.
    /// </summary>
    public class TargetLayoutDef
    {
        [JsonPropertyName("minPlayers")]
        public int MinPlayers { get; set; }

        [JsonPropertyName("count")]
        public int Count { get; set; }

        [JsonPropertyName("radius")]
        public double Radius { get; set; }
    }
}
