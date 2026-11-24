using System.Collections.Generic;

namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// The short names of the Destroy the Source phase rows (WorldEventSettingsRegistry.DestroySource.cs), one constant each so a typo
    /// is a compile error. Each row's property is "world_events_destroy_source_" + the key.
    /// </summary>
    public static class DestroySourceKeys
    {
        public const string PropertyPrefix = "world_events_destroy_source_";

        public const string PhasesEnabled = "phases_enabled";
        public const string PhaseCount = "phase_count";
        public const string QuotaFraction = "quota_fraction";
        public const string SoftTimeoutSeconds = "soft_timeout_seconds";
        public const string HardTimeoutSeconds = "hard_timeout_seconds";
        public const string BankMax = "bank_max";
    }

    /// <summary>
    /// The phased Destroy the Source settings rows: while on, a source starts shielded, killing the waves' adds earns phases, and each
    /// phase lets 10% of every source's max health be removed. Scoped to the destroy_source goal, so a global caller can never write one.
    /// The typed properties behind them come from <see cref="GoalScopedBoolDefaults"/> and friends (wired in DefaultPropertyManager), so
    /// the key list and the shipped defaults have one source. Every row is read once, when the objective is staged.
    /// </summary>
    public sealed partial class WorldEventSettingsRegistry
    {
        /// <summary>The goal type the phased Destroy the Source rows are scoped to.</summary>
        public const string DestroySourceGoalType = "destroy_source";

        public static readonly SettingScope DestroySourceScope = SettingScope.Goal(DestroySourceGoalType);

        public const string GroupPhases = "phases";

        private static WorldEventSettingRow Ds(string key, SettingKind kind, string def, double min, double max, string description) =>
            new WorldEventSettingRow
            {
                Key = key, PropertyName = DestroySourceKeys.PropertyPrefix + key, Group = GroupPhases, Kind = kind, Scope = DestroySourceScope,
                CodeDefault = def, Min = min, Max = max, Unset = SettingUnsetRule.AlwaysSet, Applies = SettingApplies.NextRun,
                Description = description
            };

        internal static IEnumerable<WorldEventSettingRow> DestroySourceRows()
        {
            yield return Ds(DestroySourceKeys.PhasesEnabled, SettingKind.Bool, "True", 0, 1,
                "Phased sources: a source starts shielded and its ward breaks 10% at a time as the waves' adds are slain. Off: sources take damage from the start, as before.");
            yield return Ds(DestroySourceKeys.PhaseCount, SettingKind.Long, "10", 1, 100,
                "How many phases it takes to break the ward. Each phase lets 1/phase_count of every source's max health be removed.");
            yield return Ds(DestroySourceKeys.QuotaFraction, SettingKind.Double, "0.75", 0.05, 3,
                "Adds to slay for one phase, as a share of the theme's wave count (rounded up, at least 1).");
            yield return Ds(DestroySourceKeys.SoftTimeoutSeconds, SettingKind.Double, "90", 5, 1800,
                "With no phase earned for this long, the quota falls to the adds already slain this phase (at least 1).");
            yield return Ds(DestroySourceKeys.HardTimeoutSeconds, SettingKind.Double, "180", 10, 3600,
                "With no phase earned for this long, the next phase opens regardless of kills.");
            yield return Ds(DestroySourceKeys.BankMax, SettingKind.Long, "1", 0, 5,
                "How many opened phases may sit unused ahead of the slowest source (0 = a phase opens only once the slowest source has used the last).");
        }
    }
}
