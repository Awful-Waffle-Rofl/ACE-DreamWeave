using System.Text.Json.Serialization;

namespace ACE.Server.WorldEvents.Defs
{
    /// <summary>
    /// Objective kind. Parsed case-insensitively from GoalDef.Type. Hold is the "protect the targets" goal:
    /// it spawns the friendly targets in its <see cref="GoalDef.Protect"/> block and succeeds when enough of
    /// them are still alive after <see cref="GoalDef.HoldSeconds"/>.
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

        /// <summary>Hold only: seconds from the run going Active until the hold succeeds.</summary>
        [JsonPropertyName("holdSeconds")]
        public int HoldSeconds { get; set; }

        /// <summary>Hold only: the targets to protect. Required for a Hold goal, ignored by every other type.</summary>
        [JsonPropertyName("protect")]
        public ProtectDef Protect { get; set; }

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
        /// Optional: when a run of this goal succeeds, a second World Event starts nearby (WorldEventChain). Absent: no
        /// chain. Any goal may carry it; the envoys do.
        /// </summary>
        [JsonPropertyName("chain")]
        public ChainDef Chain { get; set; }

        /// <summary>
        /// Optional: every location of this goal (and of the stages it chains into) must be a short walk from a Town
        /// Network portal drop (WorldEventTownReach). Absent: no check.
        /// </summary>
        [JsonPropertyName("townReach")]
        public TownReachDef TownReach { get; set; }

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
