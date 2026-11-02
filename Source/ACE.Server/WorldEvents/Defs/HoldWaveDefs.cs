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
        Finale,

        /// <summary>A mixed wave (protect.waveMix): every role at once. Never rolled or forced; set by the wave mix.</summary>
        Mixed
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

        /// <summary>
        /// Runners: attack speed multiplier (1 to 3). Default 1.5. Speeds up their swings, and their time-based
        /// damage to the targets by the same factor. Every runner is a seeker.
        /// </summary>
        [JsonPropertyName("attackSpeedMult")]
        public double AttackSpeedMult { get; set; } = 1.5;

        /// <summary>
        /// Ranged: the distance in metres (5 to 40) a ranged or caster monster stops at from its target and
        /// attacks from. It only closes in when it has no ranged attack or its spell cannot reach. Default 18.
        /// </summary>
        [JsonPropertyName("holdDistance")]
        public double HoldDistance { get; set; } = 18;

        /// <summary>Hunters: how many of the wave hunt players instead of the targets. Default 2.</summary>
        [JsonPropertyName("hunters")]
        public int Hunters { get; set; } = 2;

        /// <summary>Parsed from <see cref="Type"/> at load. Never read from JSON.</summary>
        [JsonIgnore]
        public HoldWaveType TypeKind { get; set; }
    }

    /// <summary>The job one monster has in a mixed Hold wave (protect.waveMix).</summary>
    public enum HoldRole
    {
        /// <summary>Goes for the targets, spread across the ones the breach squad is not on.</summary>
        Seeker,

        /// <summary>The breach squad: every one of them goes for the same target.</summary>
        Breach,

        /// <summary>Faster movement and attacks, lighter; always goes for the targets.</summary>
        Runner,

        /// <summary>A ranged or caster member that stops at the hold distance and attacks a target from there.</summary>
        Ranged,

        /// <summary>Ignores the targets and goes for players, a recent healer first.</summary>
        Hunter,

        /// <summary>A marked priority target: bigger, glowing, and the wave monsters near it hit the targets harder.</summary>
        Corrupted
    }

    /// <summary>
    /// protect.waveMix: every wave but the finale carries every role at once, each a share of the wave (they are
    /// normalised to sum to 1, and every role with a share above 0 gets at least one monster once the wave has
    /// room for all of them). The per-role settings (runner speeds and health, ranged hold distance, breach and
    /// hunter lines) come from the matching protect.specialWaves entries when present. Player-facing text is
    /// ASCII only.
    /// </summary>
    public class WaveMixDef
    {
        [JsonPropertyName("breach")]
        public double Breach { get; set; } = 0.15;

        [JsonPropertyName("runners")]
        public double Runners { get; set; } = 0.15;

        [JsonPropertyName("ranged")]
        public double Ranged { get; set; } = 0.3;

        [JsonPropertyName("hunters")]
        public double Hunters { get; set; } = 0.1;

        [JsonPropertyName("seekers")]
        public double Seekers { get; set; } = 0.2;

        /// <summary>Corrupted priority targets (protect.corrupted). Default 0.1, at least <see cref="MinCorrupted"/>, at most <see cref="MaxCorrupted"/>.</summary>
        [JsonPropertyName("corrupted")]
        public double Corrupted { get; set; } = 0.1;

        /// <summary>At least this many in the breach squad, so the knight it hits really feels it (0 to 10). Default 0.</summary>
        [JsonPropertyName("minBreach")]
        public int MinBreach { get; set; }

        /// <summary>At least this many ranged in every wave big enough to hold them (0 to 20). Default 3.</summary>
        [JsonPropertyName("minRanged")]
        public int MinRanged { get; set; } = 3;

        /// <summary>At least this many corrupted per wave when the share is above 0 (0 to 5). Default 1.</summary>
        [JsonPropertyName("minCorrupted")]
        public int MinCorrupted { get; set; } = 1;

        /// <summary>At most this many corrupted per wave (0 to 10); the rest go to seekers. Default 2.</summary>
        [JsonPropertyName("maxCorrupted")]
        public int MaxCorrupted { get; set; } = 2;

        /// <summary>
        /// The wave's World Event announcement, one picked at random, after "Wave n of N: ". Tokens: the usual
        /// ones plus {target} (the breach squad's target by name) and {side} (its side of the anchor).
        /// </summary>
        [JsonPropertyName("announce")]
        public List<string> Announce { get; set; } = new List<string>();

        /// <summary>The shares in role order: Seeker, Breach, Runner, Ranged, Hunter, Corrupted.</summary>
        [JsonIgnore]
        public double[] Shares => new[] { Seekers, Breach, Runners, Ranged, Hunters, Corrupted };

        /// <summary>The per-role minimums in role order (Breach, Ranged and Corrupted, each only while its share is above 0).</summary>
        [JsonIgnore]
        public int[] Minimums => new[] { 0, Breach > 0 ? MinBreach : 0, 0, Ranged > 0 ? MinRanged : 0, 0, Corrupted > 0 ? MinCorrupted : 0 };

        /// <summary>The per-role maximums in role order (-1 = none; only Corrupted has one).</summary>
        [JsonIgnore]
        public int[] Maximums => new[] { -1, -1, -1, -1, -1, MaxCorrupted };
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
