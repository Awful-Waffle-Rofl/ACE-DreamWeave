using System.Text.Json.Serialization;

namespace ACE.Server.WorldEvents.Defs
{
    /// <summary>
    /// Objective kind. Parsed case-insensitively from GoalDef.Type. No Hold entry ships in v1 (C6 defers
    /// Hold to P2), but the enum value exists so WP-12 can add it without a rename.
    /// </summary>
    public enum GoalType
    {
        KillCount,
        DestroySource,
        KillBoss,
        Hold
    }

    /// <summary>
    /// MVP attribution rule. Parsed case-insensitively from GoalDef.MvpRule.
    /// </summary>
    public enum MvpRule
    {
        MostKills,
        KillingBlow,
        KillingBlowAndTopDamage
    }

    /// <summary>
    /// One entry from Content/events/axes/goals.json (TECH-DESIGN 5.4).
    /// </summary>
    public class GoalDef
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("displayName")]
        public string DisplayName { get; set; }

        [JsonPropertyName("type")]
        public string Type { get; set; }

        [JsonPropertyName("count")]
        public ScaledCount Count { get; set; }

        [JsonPropertyName("holdSeconds")]
        public int HoldSeconds { get; set; }

        [JsonPropertyName("mvpRule")]
        public string MvpRule { get; set; }

        [JsonPropertyName("progressTemplate")]
        public string ProgressTemplate { get; set; }

        /// <summary>
        /// Optional outcome-line template for a Success announcement (2026-08-19). Tokens: {goal}, {anchor},
        /// {boss}, {noun}, {nounPlural}. Absent (the default, and every goal shipped before this field
        /// existed) keeps the legacy "{goal} at {anchor} succeeded!" shape. Overridden by the SOURCE's own
        /// successTemplate, but ONLY when this goal's <see cref="TypeKind"/> is <see cref="GoalType.DestroySource"/>
        /// - see <see cref="ACE.Server.WorldEvents.WorldEventComposition.SuccessTemplate"/>. A blank value is
        /// repaired to null with a diagnostic at load.
        /// </summary>
        [JsonPropertyName("successTemplate")]
        public string SuccessTemplate { get; set; }

        /// <summary>
        /// Optional outcome-line template for every Failed* announcement (2026-08-19). Tokens: {goal},
        /// {anchor}, {boss}, {noun}, {nounPlural}, {reason}. Absent keeps the legacy
        /// "{goal} at {anchor} failed - {reason}." shape. Same SOURCE-override and DestroySource-only rule as
        /// <see cref="SuccessTemplate"/>.
        /// </summary>
        [JsonPropertyName("failTemplate")]
        public string FailTemplate { get; set; }

        /// <summary>
        /// Computed at load time from <see cref="Type"/> (case-insensitive). Never read from JSON.
        /// </summary>
        [JsonIgnore]
        public GoalType TypeKind { get; set; }

        /// <summary>
        /// Computed at load time from <see cref="MvpRule"/> (case-insensitive). Never read from JSON.
        /// </summary>
        [JsonIgnore]
        public MvpRule RuleKind { get; set; }
    }
}
