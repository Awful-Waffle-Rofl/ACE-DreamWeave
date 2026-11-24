using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ACE.Server.WorldEvents.Defs
{
    /// <summary>
    /// One entry from Content/events/axes/anchors.json (TECH-DESIGN 5.4). Holds the owner-walked city anchors the
    /// daily scheduler picks from (mirrors the market admin tool list); "--here" is the in-game anchor path.
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

        /// <summary>
        /// The realm the anchor lives in (0 = retail). Read by the scheduler, which builds a web-style location from it.
        /// </summary>
        [JsonPropertyName("realmId")]
        public ushort RealmId { get; set; }

        /// <summary>
        /// The largest source ClearanceRadius (metres) this city can host; 0 means it hosts nothing. Same rule as the
        /// market admin World Event tool: a source is offered only when its clearance is at most this and this is above 0.
        /// </summary>
        [JsonPropertyName("maxRadius")]
        public float MaxRadius { get; set; }
    }
}
