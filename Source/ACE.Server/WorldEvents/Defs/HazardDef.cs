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

        /// <summary>
        /// Optional list of kinds (wisps | balls | rain): each run picks one uniformly. Overrides <see cref="Kind"/> when the
        /// caller names no hazard; absent or empty = <see cref="Kind"/> alone. A picked balls keeps its balls/rain alternation, so
        /// ["wisps","balls"] emits wisps 50%, rolling balls 25%, rain 25% of runs. Ignored (with a load diagnostic) when <see cref="Kind"/> is none.
        /// </summary>
        [JsonPropertyName("kinds")]
        public List<string> Kinds { get; set; }

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
