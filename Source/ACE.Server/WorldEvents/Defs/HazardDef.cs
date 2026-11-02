using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ACE.Server.WorldEvents.Defs
{
    /// <summary>
    /// The optional <c>hazard</c> block of a source in Content/events/axes/sources.json: what hazard a world
    /// event from this source runs by default (HAZARDS-PLAN). Pure data; the planner (WorldEventHazards) and the
    /// axis-store validation read it. Absent block = the source has no default hazard.
    /// </summary>
    public class HazardDef
    {
        /// <summary>wisps | balls | rain | random | none.</summary>
        [JsonPropertyName("kind")]
        public string Kind { get; set; }

        /// <summary>Optional fixed element (fire | acid | frost | lightning | earth). Absent: from <see cref="StyleElements"/>, else random.</summary>
        [JsonPropertyName("element")]
        public string Element { get; set; }

        /// <summary>Optional element per decor style, index-aligned with SourceThemeDef.DecorStyles.</summary>
        [JsonPropertyName("styleElements")]
        public List<string> StyleElements { get; set; }

        /// <summary>Optional rain drop height in metres above the anchor.</summary>
        [JsonPropertyName("rainHeight")]
        public float? RainHeight { get; set; }
    }
}
