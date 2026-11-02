using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ACE.Server.WorldEvents
{
    /// <summary>How a World Events setting's value is typed. Each kind maps to ONE typed server property.</summary>
    public enum SettingKind { Double, Long, Bool, Choice, Text }

    /// <summary>When a change to a setting is first used.</summary>
    public enum SettingApplies
    {
        /// <summary>Read on every tick or hit, so a run already going sees it.</summary>
        Live,

        /// <summary>Read when a run is staged, so it applies from the next run.</summary>
        NextRun
    }

    /// <summary>How "not set" is spelled by a row's property. The fork idiom: a number at or below 0 means use the JSON or code default.</summary>
    public enum SettingUnsetRule
    {
        /// <summary>A number at or below 0 (or not finite) is "not set": the JSON value, else the code default, is used.</summary>
        NonPositiveIsUnset,

        /// <summary>Zero and false are real values; the property is always "set" and its source is Default only while it equals the registered default.</summary>
        AlwaysSet,

        /// <summary>An empty string is "not set" (Choice and Text rows).</summary>
        EmptyIsUnset,

        /// <summary>
        /// A number below 0 (or not finite) is "not set": the JSON value, else the code default, is used. Unlike
        /// <see cref="NonPositiveIsUnset"/>, 0 is a real value. Long and Double rows only; the property ships at -1, a set value
        /// (0 or above) is clamped to the row's Min and Max, and a negative value is never clamped.
        /// </summary>
        NegativeIsUnset
    }

    /// <summary>Where a setting's effective value came from.</summary>
    public enum SettingSource { Property, Json, Default }

    /// <summary>
    /// Who a setting belongs to: every World Event (<see cref="Global"/>) or the runs of one goal type (<see cref="Goal"/>).
    /// A writer is called with the caller's scope and refuses every row whose scope is not equal to it, so a goal-scoped
    /// reset or import can never touch a global key and a global admin command never reaches a goal's keys by accident.
    /// </summary>
    public sealed class SettingScope : IEquatable<SettingScope>
    {
        public static readonly SettingScope Global = new SettingScope(null);

        private static readonly Regex GoalTypeName = new Regex("^[a-z][a-z0-9_]{0,31}$", RegexOptions.Compiled);

        private SettingScope(string goalType)
        {
            GoalType = goalType;
        }

        /// <summary>The goal type of a goal scope (lower case), or null for Global.</summary>
        public string GoalType { get; }

        public bool IsGlobal => GoalType == null;

        public static SettingScope Goal(string goalType)
        {
            var g = (goalType ?? "").Trim().ToLowerInvariant();

            if (!GoalTypeName.IsMatch(g))
                throw new ArgumentException($"'{goalType}' is not a goal type name", nameof(goalType));

            return new SettingScope(g);
        }

        public bool Equals(SettingScope other) => other != null && string.Equals(GoalType, other.GoalType, StringComparison.Ordinal);

        public override bool Equals(object obj) => Equals(obj as SettingScope);

        public override int GetHashCode() => GoalType == null ? 0 : GoalType.GetHashCode();

        public override string ToString() => GoalType == null ? "global" : "goal:" + GoalType;
    }

    /// <summary>One admin-tunable World Events value: a typed server property plus the metadata the settings UX needs.</summary>
    public sealed class WorldEventSettingRow
    {
        /// <summary>The short name used by /worldevent settings (e.g. "max_alive").</summary>
        public string Key { get; init; }

        /// <summary>The typed server property that holds the value (e.g. "world_events_max_alive").</summary>
        public string PropertyName { get; init; }

        public SettingKind Kind { get; init; }
        public SettingScope Scope { get; init; } = SettingScope.Global;
        public string Group { get; init; }

        /// <summary>Smallest allowed value of a Double or Long row; a value below it is clamped with a warning.</summary>
        public double Min { get; init; } = double.NegativeInfinity;

        /// <summary>Largest allowed value of a Double or Long row.</summary>
        public double Max { get; init; } = double.PositiveInfinity;

        public SettingUnsetRule Unset { get; init; } = SettingUnsetRule.NonPositiveIsUnset;

        /// <summary>The value used when the property is not set and no JSON value is found, as text. Bool rows use "True" or "False".</summary>
        public string CodeDefault { get; init; }

        /// <summary>Where the JSON axis value this property overrides lives (e.g. "bosses.perPlayer"), or null when there is none.</summary>
        public string JsonPath { get; init; }

        /// <summary>The allowed values of a Choice row (lower case).</summary>
        public IReadOnlyList<string> Choices { get; init; }

        /// <summary>
        /// An optional write-side validator for a row whose text has a shape of its own (a list, a palette, an alias). It gets the
        /// trimmed non-empty input and returns the text to store (Normalized, Error null) or an Error (Normalized ignored). The
        /// value it returns then goes through the ordinary kind check, so a Long row still clamps. Null = no extra check.
        /// </summary>
        public Func<string, (string Normalized, string Error)> ParseValue { get; init; }

        public SettingApplies Applies { get; init; }
        public string Description { get; init; }

        public bool IsNumber => Kind == SettingKind.Double || Kind == SettingKind.Long;

        /// <summary>"0..1", "on/off", "a|b" - for listings and warnings.</summary>
        public string RangeText
        {
            get
            {
                switch (Kind)
                {
                    case SettingKind.Bool:
                        return "on/off";
                    case SettingKind.Choice:
                        return string.Join("|", Choices ?? Array.Empty<string>());
                    case SettingKind.Text:
                        return "text";
                    default:
                        return $"{WorldEventSettingsService.FormatNumber(Min)}..{WorldEventSettingsService.FormatNumber(Max)}";
                }
            }
        }

        public static string AppliesText(SettingApplies applies) => applies == SettingApplies.Live ? "live" : "next run";
    }

    /// <summary>
    /// The set of World Events settings rows, validated at construction (unique keys and properties, a consistent unset rule per kind).
    /// <see cref="Default"/> holds the shipped rows; tests build their own instances, including goal-scoped ones.
    /// </summary>
    public sealed partial class WorldEventSettingsRegistry
    {
        public const string GroupBosses = "bosses";
        public const string GroupWaves = "waves";
        public const string GroupMessages = "messages";
        public const string GroupRewards = "rewards";

        private static readonly Regex KeyName = new Regex("^[a-z][a-z0-9_]{0,63}$", RegexOptions.Compiled);

        private readonly Dictionary<string, WorldEventSettingRow> byName = new Dictionary<string, WorldEventSettingRow>(StringComparer.OrdinalIgnoreCase);

        public WorldEventSettingsRegistry(IEnumerable<WorldEventSettingRow> rows)
        {
            Rows = (rows ?? throw new ArgumentNullException(nameof(rows))).ToList();

            foreach (var r in Rows)
            {
                Validate(r);

                if (byName.ContainsKey(r.Key))
                    throw new InvalidOperationException($"setting key '{r.Key}' is registered twice");

                if (byName.ContainsKey(r.PropertyName))
                    throw new InvalidOperationException($"setting property '{r.PropertyName}' is registered twice");

                byName[r.Key] = r;
                byName[r.PropertyName] = r;

                if (!Groups.Contains(r.Group))
                    Groups.Add(r.Group);
            }

            // A key is a command argument next to group names and "all"/"set", so it may not shadow any of them.
            foreach (var r in Rows)
            {
                if (Groups.Contains(r.Key, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"setting key '{r.Key}' is also a group name");
            }
        }

        /// <summary>Words the describe and reset commands give a meaning of their own.</summary>
        private static readonly string[] ReservedWords = { "all", "set" };

        public IReadOnlyList<WorldEventSettingRow> Rows { get; }

        /// <summary>The groups, in first-use order.</summary>
        public List<string> Groups { get; } = new List<string>();

        private static void Validate(WorldEventSettingRow r)
        {
            if (r == null || r.Key == null || !KeyName.IsMatch(r.Key))
                throw new InvalidOperationException($"setting key '{r?.Key}' is not a lower-case name");

            if (ReservedWords.Contains(r.Key))
                throw new InvalidOperationException($"setting key '{r.Key}' is a reserved word");

            if (string.IsNullOrWhiteSpace(r.PropertyName) ||string.IsNullOrWhiteSpace(r.Group) || r.Scope == null)
                throw new InvalidOperationException($"setting '{r.Key}' needs a property, a group and a scope");

            if (r.IsNumber && !(double.IsFinite(r.Min) && double.IsFinite(r.Max) && r.Min <= r.Max))
                throw new InvalidOperationException($"setting '{r.Key}' needs a finite Min and Max, Min not above Max");

            if (r.Kind == SettingKind.Bool && r.Unset != SettingUnsetRule.AlwaysSet)
                throw new InvalidOperationException($"setting '{r.Key}': a Bool row is always set");

            if ((r.Kind == SettingKind.Choice || r.Kind == SettingKind.Text) != (r.Unset == SettingUnsetRule.EmptyIsUnset))
                throw new InvalidOperationException($"setting '{r.Key}': EmptyIsUnset is the rule of Choice and Text rows only");

            if (r.Unset == SettingUnsetRule.NegativeIsUnset && !r.IsNumber)
                throw new InvalidOperationException($"setting '{r.Key}': NegativeIsUnset is the rule of Long and Double rows only");

            if (r.Kind == SettingKind.Choice && (r.Choices == null || r.Choices.Count == 0))
                throw new InvalidOperationException($"setting '{r.Key}': a Choice row needs its choices");

            if (r.CodeDefault == null)
                throw new InvalidOperationException($"setting '{r.Key}' needs a CodeDefault (use an empty string for none)");
        }

        public bool TryFind(string name, out WorldEventSettingRow row)
        {
            row = null;
            return !string.IsNullOrWhiteSpace(name) && byName.TryGetValue(name.Trim(), out row);
        }

        public bool IsGroup(string name) => Groups.Any(g => string.Equals(g, name, StringComparison.OrdinalIgnoreCase));

        public IReadOnlyList<WorldEventSettingRow> InGroup(string group) =>
            Rows.Where(r => string.Equals(r.Group, group, StringComparison.OrdinalIgnoreCase)).ToList();

        // ---- the shipped rows ------------------------------------------------------------------------------------

        private static readonly Lazy<WorldEventSettingsRegistry> defaultRegistry = new Lazy<WorldEventSettingsRegistry>(() => new WorldEventSettingsRegistry(DefaultRows()));

        /// <summary>The shipped rows: the global World Events properties a goal's behavior depends on, then the Hold goal's own rows (WorldEventSettingsRegistry.Hold.cs).</summary>
        public static WorldEventSettingsRegistry Default => defaultRegistry.Value;

        private static WorldEventSettingRow Global(string group, string key, SettingKind kind, string codeDefault, double min, double max,
            SettingUnsetRule unset, SettingApplies applies, string description) =>
            new WorldEventSettingRow
            {
                Key = key, PropertyName = "world_events_" + key, Group = group, Kind = kind, Scope = SettingScope.Global, CodeDefault = codeDefault,
                Min = min, Max = max, Unset = unset, Applies = applies, Description = description
            };

        internal static IEnumerable<WorldEventSettingRow> DefaultRows()
        {
            var unset = SettingUnsetRule.NonPositiveIsUnset;
            var always = SettingUnsetRule.AlwaysSet;

            yield return Global(GroupWaves, "max_alive", SettingKind.Long, "0", 0, 200, unset, SettingApplies.Live,
                "Most wave monsters alive at once. 0 = use sources.json maxAlive.");

            yield return Global(GroupWaves, "teaser_lead_seconds", SettingKind.Long, "60", 0, 3600, always, SettingApplies.NextRun,
                "Teaser countdown before a run is staged; 0 = no teaser. --teaser wins for one run.");

            yield return Global(GroupMessages, "progress_interval_seconds", SettingKind.Long, "120", 0, 3600, always, SettingApplies.Live,
                "Seconds between the global progress callouts while a run is active; 0 = none.");

            yield return Global(GroupMessages, "announce_chat_type", SettingKind.Long, "5", 0, 100, always, SettingApplies.Live,
                "Chat channel (ChatMessageType) of the global World Event lines.");

            yield return Global(GroupBosses, "boss_scale_multiplier", SettingKind.Double, "0.67", 0.1, 5, always, SettingApplies.NextRun,
                "Size of every named boss: its ObjScale times this.");

            yield return Global(GroupBosses, "boss_hits_to_kill", SettingKind.Double, "3", 1, 50, unset, SettingApplies.Live,
                "A named boss's damage to players is steered so the toughest player dies in this many ordinary hits. 0 = the built-in default.");

            yield return Global(GroupBosses, "boss_damage_rating_min", SettingKind.Long, "-80", -200, 0, always, SettingApplies.Live,
                "Lowest DamageRating the named-boss damage steering may set.");

            yield return Global(GroupBosses, "boss_damage_rating_max", SettingKind.Long, "300", 0, 1000, always, SettingApplies.Live,
                "Highest DamageRating the named-boss damage steering may set.");

            yield return Global(GroupBosses, "boss_per_player", SettingKind.Double, "0", 0, 10, unset, SettingApplies.NextRun,
                "Named-boss health per player (power curve). 0 = use bosses.json.");

            yield return Global(GroupBosses, "boss_health_cap", SettingKind.Double, "0", 0, 1000, unset, SettingApplies.NextRun,
                "Named-boss health multiplier cap (power curve). 0 = use bosses.json.");

            yield return Global(GroupBosses, "boss_throughput_floor_health", SettingKind.Long, "0", 0, 100000000, unset, SettingApplies.NextRun,
                "Named-boss health floor when sized from the wave phase. 0 = use bosses.json.");

            yield return Global(GroupBosses, "boss_throughput_cap_health", SettingKind.Long, "0", 0, 100000000, unset, SettingApplies.NextRun,
                "Named-boss health cap when sized from the wave phase. 0 = use bosses.json.");

            yield return Global(GroupRewards, "participation_payout_enabled", SettingKind.Bool, "True", 0, 1, always, SettingApplies.Live,
                "A claimant with no damage or kill credit gets the coal prize instead of the crate.");

            foreach (var row in HoldRows())
                yield return row;
        }
    }
}
