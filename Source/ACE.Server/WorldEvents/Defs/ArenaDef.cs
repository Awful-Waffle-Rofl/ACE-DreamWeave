using System.Text.Json.Serialization;

namespace ACE.Server.WorldEvents.Defs
{
    /// <summary>
    /// The nominal circular "arena" a source theme declares (presence payout, and later hazards): the area
    /// whose occupancy time decides a player's reward multiplier. Optional on <see cref="SourceThemeDef"/>;
    /// a theme with no arena block still tracks presence on a circle of its rewardRadius, with no ring
    /// (WorldEvent.BuildArena).
    ///
    /// Every field has a safe default, so an arena block of just <c>{}</c> is legal and means "the source's
    /// rewardRadius, no ring".
    /// </summary>
    public class ArenaDef
    {
        /// <summary>Arena radius in metres. 0 (the default) means use the source's rewardRadius.</summary>
        [JsonPropertyName("radius")]
        public float Radius { get; set; }

        /// <summary>Half the arena's vertical extent in metres. 0 (the default) means radius / 2.</summary>
        [JsonPropertyName("halfHeight")]
        public float HalfHeight { get; set; }

        /// <summary>Wcid of the marker placed around the rim. 0 (the default) means no ring.</summary>
        [JsonPropertyName("ringWcid")]
        public uint RingWcid { get; set; }

        /// <summary>Target metres between adjacent ring markers.</summary>
        [JsonPropertyName("ringSpacing")]
        public float RingSpacing { get; set; } = 10f;

        /// <summary>Lower bound on the ring marker count.</summary>
        [JsonPropertyName("ringMin")]
        public int RingMin { get; set; } = 12;

        /// <summary>Upper bound on the ring marker count.</summary>
        [JsonPropertyName("ringMax")]
        public int RingMax { get; set; } = 40;

        /// <summary>Scale applied to each ring marker.</summary>
        [JsonPropertyName("ringScale")]
        public float RingScale { get; set; } = 1.0f;

        /// <summary>Vertical offset added to each ring marker after it is snapped to terrain.</summary>
        [JsonPropertyName("ringDz")]
        public float RingDz { get; set; }

        /// <summary>Height above the terrain at each ring point from which the downward ground probe starts (WorldEventGroundProbe).</summary>
        [JsonPropertyName("probeHeight")]
        public float ProbeHeight { get; set; } = 60f;
    }
}