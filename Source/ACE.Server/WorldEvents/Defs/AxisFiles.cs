using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ACE.Server.WorldEvents.Defs
{
    // Wrapper roots for the six Content/events/axes/*.json files (TECH-DESIGN 5.4, BOSS-STANDARD 5).

    public class SourcesFile
    {
        /// <summary>
        /// Optional random-roll buckets: group name to bucket weight. A source joins one through its
        /// randomGroup key; the roll picks a bucket by this weight, then a source inside it by randomWeight.
        /// </summary>
        [JsonPropertyName("randomGroups")]
        public Dictionary<string, double> RandomGroups { get; set; } = new Dictionary<string, double>();

        [JsonPropertyName("sources")]
        public List<SourceThemeDef> Sources { get; set; } = new List<SourceThemeDef>();
    }

    public class FamiliesFile
    {
        [JsonPropertyName("families")]
        public List<FamilyDef> Families { get; set; } = new List<FamilyDef>();
    }

    public class GoalsFile
    {
        [JsonPropertyName("goals")]
        public List<GoalDef> Goals { get; set; } = new List<GoalDef>();
    }

    public class RewardsFile
    {
        [JsonPropertyName("rewards")]
        public List<RewardDef> Rewards { get; set; } = new List<RewardDef>();
    }

    public class BossesFile
    {
        [JsonPropertyName("bosses")]
        public List<BossDef> Bosses { get; set; } = new List<BossDef>();
    }

    public class AnchorsFile
    {
        [JsonPropertyName("anchors")]
        public List<AnchorDef> Anchors { get; set; } = new List<AnchorDef>();
    }
}
