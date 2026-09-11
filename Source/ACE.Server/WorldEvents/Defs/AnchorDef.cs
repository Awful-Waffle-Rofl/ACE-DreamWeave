using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ACE.Server.WorldEvents.Defs
{
    /// <summary>
    /// One entry from Content/events/axes/anchors.json (TECH-DESIGN 5.4). P2 - v1 ships an empty list;
    /// "--here" is the P0/P1 anchor path.
    /// </summary>
    public class AnchorDef
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("displayName")]
        public string DisplayName { get; set; }

        [JsonPropertyName("cellId")]
        public uint CellId { get; set; }

        [JsonPropertyName("originX")]
        public float OriginX { get; set; }

        [JsonPropertyName("originY")]
        public float OriginY { get; set; }

        [JsonPropertyName("originZ")]
        public float OriginZ { get; set; }

        [JsonPropertyName("anglesW")]
        public float AnglesW { get; set; }

        [JsonPropertyName("anglesX")]
        public float AnglesX { get; set; }

        [JsonPropertyName("anglesY")]
        public float AnglesY { get; set; }

        [JsonPropertyName("anglesZ")]
        public float AnglesZ { get; set; }

        [JsonPropertyName("biomeTags")]
        public List<string> BiomeTags { get; set; } = new List<string>();

        [JsonPropertyName("cooldownSeconds")]
        public int CooldownSeconds { get; set; }
    }
}
