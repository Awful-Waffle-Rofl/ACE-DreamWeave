using System.Text.Json.Serialization;

namespace ACE.Server.WorldEvents.Defs
{
    /// <summary>
    /// ProtectDef.HealthWisps: the wisp row that reads a Hold target's health. One wisp weenie, <see cref="Count"/> of them in a
    /// row centred over the target's start spot, perpendicular to its facing. Validated at load by WorldEventAxisStore.
    /// </summary>
    public class HealthWispsDef
    {
        public const int DefaultCount = 4;
        public const int MaxCount = 8;
        public const double DefaultSpacing = 0.5;
        public const double DefaultDz = 2.4;
        public const double DefaultScale = 0.35;

        /// <summary>The wisp weenie (a plain Generic scenery object). Must be greater than 0.</summary>
        [JsonPropertyName("wcid")]
        public uint Wcid { get; set; }

        /// <summary>How many wisps at full health, 1 to <see cref="MaxCount"/>.</summary>
        [JsonPropertyName("count")]
        public int Count { get; set; } = DefaultCount;

        /// <summary>Metres between two neighbouring wisps. Greater than 0.</summary>
        [JsonPropertyName("spacing")]
        public double Spacing { get; set; } = DefaultSpacing;

        /// <summary>Metres above the target's ground spot (clear of its head).</summary>
        [JsonPropertyName("dz")]
        public double Dz { get; set; } = DefaultDz;

        /// <summary>The wisps' DefaultScale. Greater than 0.</summary>
        [JsonPropertyName("scale")]
        public double Scale { get; set; } = DefaultScale;
    }
}
