using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

using ACE.Server.Managers;
using ACE.Server.WorldEvents.Defs;

using log4net;

namespace ACE.Server.WorldEvents
{
    /// <summary>How a Hold setting's text value is read.</summary>
    public enum HoldSettingKind { Number, Integer, Toggle, Choice, Text }

    /// <summary>When a change to a Hold setting is first used.</summary>
    public enum HoldApplies
    {
        /// <summary>The next tick or hit (within a second).</summary>
        Live,

        /// <summary>The next wave spawned.</summary>
        NextWave,

        /// <summary>The next time the thing is spawned (the finale boss, the champion, a corrupted monster).</summary>
        NextSpawn,

        /// <summary>The next event started.</summary>
        NextEvent,

        /// <summary>Read when a stage ends, so it applies to a run already going.</summary>
        StageEnd
    }

    /// <summary>Where a Hold setting's effective value came from.</summary>
    public enum HoldSettingSource { Property, GoalsJson, OtherFile, Default }

    /// <summary>
    /// One admin-tunable Hold, chain or champion value (<see cref="HoldSettings"/>). A new setting is a STRING server
    /// property whose empty value means "not set", so 0, a negative number or "off" can be set on purpose. A reused
    /// setting (<see cref="ExistingType"/>) is an older typed property that keeps its own rule (set when above 0).
    /// </summary>
    public sealed class HoldSetting
    {
        /// <summary>The short name used by /worldevent hold (e.g. "shrine_heal").</summary>
        public string Key { get; init; }

        /// <summary>The server property (e.g. "world_events_hold_shrine_heal").</summary>
        public string PropertyName { get; init; }

        public string Group { get; init; }
        public HoldSettingKind Kind { get; init; }

        /// <summary>Today's value when nothing else sets it, as text.</summary>
        public string CodeDefault { get; init; }

        public double Min { get; init; } = double.NegativeInfinity;
        public double Max { get; init; } = double.PositiveInfinity;

        /// <summary>The allowed values of a Choice setting, lower case.</summary>
        public IReadOnlyList<string> Choices { get; init; }

        /// <summary>Words an admin may type for a value (lower case word to stored value), e.g. fail condition "any" to 3.</summary>
        public IReadOnlyDictionary<string, string> Aliases { get; init; }

        public HoldApplies Applies { get; init; }

        /// <summary>True for a setting added by the live-tuning change (not a goals.json value or an older property).</summary>
        public bool IsNew { get; init; }

        public string Description { get; init; }

        /// <summary>The goals.json path of the value this overrides (e.g. "protect.shrineHeal"), or null.</summary>
        public string GoalsPath { get; init; }

        /// <summary>The other axis file the value comes from when not goals.json (e.g. "rewards.json"), or null.</summary>
        public string OtherFile { get; init; }

        /// <summary>For a reused older property: its type ("long", "double" or "bool"). Null for a new string setting.</summary>
        public string ExistingType { get; init; }

        /// <summary>True when the property affects every World Event, not only Hold runs and chains.</summary>
        public bool Global { get; init; }

        /// <summary>The value the code uses for <c>goal</c> right now, formatted; null uses the setting's own resolution.</summary>
        public Func<GoalDef, string> Effective { get; init; }

        public bool IsReused => ExistingType != null;

        /// <summary>"0 to 1", "on/off", "a, b or c" - for help text and warnings.</summary>
        public string RangeText
        {
            get
            {
                switch (Kind)
                {
                    case HoldSettingKind.Toggle:
                        return "on/off";
                    case HoldSettingKind.Choice:
                        return string.Join("|", Choices ?? Array.Empty<string>());
                    case HoldSettingKind.Text:
                        return "text";
                    default:
                        return $"{HoldSettings.Format(Min)}..{HoldSettings.Format(Max)}";
                }
            }
        }
    }

    /// <summary>One line of "/worldevent hold settings": a setting, its value now and where that value came from.</summary>
    public sealed class HoldSettingRow
    {
        public HoldSetting Setting { get; init; }
        public string Value { get; init; }
        public HoldSettingSource Source { get; init; }
        public string SourceLabel { get; init; }
        public string RawProperty { get; init; }
        public bool Overridden { get; init; }
        public bool Clamped { get; init; }
        public string Warning { get; init; }
    }

    /// <summary>
    /// The live admin tuning of Hold goals (defend_envoys), their chain and the chained champion stage.
    ///
    /// Every value resolves in this order: the server property, then goals.json (or the other axis file it lives in),
    /// then the code default. The defs' accessors (ProtectDef.ShrineHeal and so on) call in here on every read, so a
    /// change is seen by a run already going - the run keeps a reference to its GoalDef, but never a copy of a number.
    ///
    /// A new setting is a string property; "" means not set. A property value is clamped to the setting's range, and a
    /// value that is out of range or does not parse is reported (<see cref="Check"/>): by /worldevent hold, and in the
    /// log for @modifystring and the web panel (<see cref="OnPropertyModified"/>). The goals.json value is not clamped
    /// here - the loader already refused the goal if it was out of range.
    ///
    /// Reads go through <see cref="StringSource"/>, which only looks at the property cache: at runtime it holds every
    /// registered key from boot on, and a unit test has none, so nothing is ever overridden there unless a test sets it.
    /// </summary>
    public static partial class HoldSettings
    {
        private static readonly ILog log = LogManager.GetLogger(typeof(HoldSettings));

        /// <summary>The master toggle. Off (the default) refuses Hold goals and keeps random rolls off them.</summary>
        public const string EnabledProperty = "world_events_hold_enabled";

        public const string HoldPrefix = "world_events_hold_";
        public const string ChainPrefix = "world_events_";

        /// <summary>Test seam: the raw text of a string property, or null when it is not in the cache.</summary>
        internal static Func<string, string> StringSource = key => PropertyManager.TryGetCachedString(key, out var value) ? value : null;

        /// <summary>Test seams for the reused older typed properties (display only; their own readers are unchanged).</summary>
        internal static Func<string, long> ReusedLongSource = key => PropertyManager.GetLong(key, DefaultPropertyManager.DefaultLongProperties.TryGetValue(key, out var d) ? d.Item : 0).Item;
        internal static Func<string, double> ReusedDoubleSource = key => PropertyManager.GetDouble(key, DefaultPropertyManager.DefaultDoubleProperties.TryGetValue(key, out var d) ? d.Item : 0).Item;
        internal static Func<string, bool> ReusedBoolSource = key => PropertyManager.GetBool(key, DefaultPropertyManager.DefaultBooleanProperties.TryGetValue(key, out var d) && d.Item).Item;

        /// <summary>Test seam: the master toggle.</summary>
        internal static Func<bool> EnabledSource = () => PropertyManager.TryGetCachedBool(EnabledProperty, out var value) && value;

        /// <summary>True when Hold goals may start (<see cref="EnabledProperty"/>).</summary>
        public static bool HoldEnabled
        {
            get
            {
                try
                {
                    return EnabledSource();
                }
                catch (Exception ex)
                {
                    log.Error($"[WORLDEVENT] could not read {EnabledProperty}; Hold goals stay off", ex);
                    return false;
                }
            }
        }

        private static readonly Lazy<Registry> registry = new Lazy<Registry>(BuildRegistry);

        /// <summary>Every setting, in group order.</summary>
        public static IReadOnlyList<HoldSetting> All => registry.Value.Settings;

        /// <summary>The groups, in display order.</summary>
        public static IReadOnlyList<string> Groups => registry.Value.Groups;

        private sealed class Registry
        {
            public List<HoldSetting> Settings = new List<HoldSetting>();
            public List<string> Groups = new List<string>();
            public Dictionary<string, HoldSetting> ByKey = new Dictionary<string, HoldSetting>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, HoldSetting> ByProperty = new Dictionary<string, HoldSetting>(StringComparer.OrdinalIgnoreCase);
        }

        private static Registry BuildRegistry()
        {
            var list = new List<HoldSetting>();
            Register(list);

            var reg = new Registry();

            foreach (var s in list)
            {
                if (reg.ByKey.ContainsKey(s.Key))
                    throw new InvalidOperationException($"Hold setting '{s.Key}' is registered twice");

                if (reg.ByProperty.ContainsKey(s.PropertyName))
                    throw new InvalidOperationException($"Hold setting property '{s.PropertyName}' is registered twice");

                reg.Settings.Add(s);
                reg.ByKey[s.Key] = s;
                reg.ByProperty[s.PropertyName] = s;

                if (!reg.Groups.Contains(s.Group))
                    reg.Groups.Add(s.Group);
            }

            return reg;
        }

        /// <summary>The property name for a new setting's short key: "chain..." under world_events_, the rest under world_events_hold_.</summary>
        public static string PropertyFor(string key) => key.StartsWith("chain", StringComparison.Ordinal) ? ChainPrefix + key : HoldPrefix + key;

        /// <summary>A setting by short name or full property name.</summary>
        public static bool TryFind(string name, out HoldSetting setting)
        {
            setting = null;

            if (string.IsNullOrWhiteSpace(name))
                return false;

            name = name.Trim();

            return registry.Value.ByKey.TryGetValue(name, out setting) || registry.Value.ByProperty.TryGetValue(name, out setting);
        }

        /// <summary>The settings of one group (case-insensitive), or empty.</summary>
        public static IReadOnlyList<HoldSetting> InGroup(string group) =>
            All.Where(s => string.Equals(s.Group, group, StringComparison.OrdinalIgnoreCase)).ToList();

        public static bool IsGroup(string name) => Groups.Any(g => string.Equals(g, name, StringComparison.OrdinalIgnoreCase));

        /// <summary>Every new (string) setting's property and description, for DefaultPropertyManager's string defaults ("" = not set).</summary>
        internal static IEnumerable<(string Key, string Description)> RegisteredStringDefaults()
        {
            foreach (var s in All.Where(s => !s.IsReused))
                yield return (s.PropertyName, DescribeForProperty(s));
        }

        private static string DescribeForProperty(HoldSetting s)
        {
            var defaultText = s.CodeDefault == null || s.CodeDefault.Length == 0 ? "none" : s.CodeDefault;
            var from = s.GoalsPath != null ? $"goals.json {s.GoalsPath}, else {defaultText}" : s.OtherFile != null ? $"{s.OtherFile}, else {defaultText}" : defaultText;

            return $"(non-retail function) World Events Hold ({s.Group}): {s.Description} Empty = not set (uses {from}). Range {s.RangeText}. Applies: {AppliesText(s.Applies)}. See /worldevent hold settings";
        }

        public static string AppliesText(HoldApplies applies)
        {
            switch (applies)
            {
                case HoldApplies.Live: return "live";
                case HoldApplies.NextWave: return "next wave";
                case HoldApplies.NextSpawn: return "next spawn";
                case HoldApplies.NextEvent: return "next event";
                case HoldApplies.StageEnd: return "at stage end";
                default: return applies.ToString();
            }
        }

        // ---- reading -------------------------------------------------------------------------------------------

        private readonly struct Parsed
        {
            public readonly string Raw;
            public readonly bool Ok;
            public readonly double Number;
            public readonly string Text;
            public readonly string Warning;

            public Parsed(string raw, bool ok, double number, string text, string warning)
            {
                Raw = raw;
                Ok = ok;
                Number = number;
                Text = text;
                Warning = warning;
            }
        }

        private static readonly ConcurrentDictionary<string, Parsed> parseCache = new ConcurrentDictionary<string, Parsed>(StringComparer.Ordinal);

        /// <summary>The raw property text ("" when not set).</summary>
        public static string RawValue(HoldSetting s)
        {
            if (s == null || s.IsReused)
                return "";

            try
            {
                return StringSource(s.PropertyName) ?? "";
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] could not read {s.PropertyName}; treating it as not set", ex);
                return "";
            }
        }

        [ThreadStatic]
        private static int fileValuesOnlyDepth;

        /// <summary>
        /// While the returned scope is open, on this thread every setting reads as not set, so the defs show goals.json's own
        /// values. The axis loader validates under it: a live override never makes a reload drop a goal, and the file is
        /// checked for what it says.
        /// </summary>
        public static IDisposable FileValuesOnly()
        {
            fileValuesOnlyDepth++;
            return new FileValuesScope();
        }

        private sealed class FileValuesScope : IDisposable
        {
            private bool disposed;

            public void Dispose()
            {
                if (disposed)
                    return;

                disposed = true;
                fileValuesOnlyDepth--;
            }
        }

        private static Parsed Read(HoldSetting s)
        {
            if (fileValuesOnlyDepth > 0)
                return default;

            var raw = RawValue(s);

            if (raw.Length == 0 || raw.Trim().Length == 0)
                return default;

            if (parseCache.TryGetValue(s.PropertyName, out var cached) && ReferenceEquals(cached.Raw, raw))
                return cached;

            var parsed = Parse(s, raw);
            parseCache[s.PropertyName] = parsed;

            return parsed;
        }

        private static Parsed Parse(HoldSetting s, string raw)
        {
            var text = raw.Trim();
            var lower = text.ToLowerInvariant();

            if (s.Aliases != null && s.Aliases.TryGetValue(lower, out var aliased))
            {
                text = aliased;
                lower = aliased.ToLowerInvariant();
            }

            switch (s.Kind)
            {
                case HoldSettingKind.Number:
                case HoldSettingKind.Integer:
                {
                    if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
                        return new Parsed(raw, false, 0, null, $"{s.Key}='{raw}' is not a number; it is ignored (not set)");

                    string warning = null;

                    if (s.Kind == HoldSettingKind.Integer && Math.Abs(value - Math.Round(value)) > 1e-9)
                    {
                        value = Math.Round(value, MidpointRounding.AwayFromZero);
                        warning = $"{s.Key}='{raw}' is not a whole number; using {Format(value)}";
                    }

                    if (value < s.Min || value > s.Max)
                    {
                        var clamped = Math.Clamp(value, s.Min, s.Max);
                        warning = $"{s.Key}={Format(value)} is out of range {s.RangeText}; using {Format(clamped)}";
                        value = clamped;
                    }

                    return new Parsed(raw, true, value, null, warning);
                }

                case HoldSettingKind.Toggle:
                {
                    switch (lower)
                    {
                        case "on": case "true": case "yes": case "1":
                            return new Parsed(raw, true, 1, "on", null);
                        case "off": case "false": case "no": case "0":
                            return new Parsed(raw, true, 0, "off", null);
                        default:
                            return new Parsed(raw, false, 0, null, $"{s.Key}='{raw}' is not on or off; it is ignored (not set)");
                    }
                }

                case HoldSettingKind.Choice:
                {
                    var match = s.Choices?.FirstOrDefault(c => string.Equals(c, lower, StringComparison.Ordinal));

                    return match != null
                        ? new Parsed(raw, true, 0, match, null)
                        : new Parsed(raw, false, 0, null, $"{s.Key}='{raw}' is not one of {s.RangeText}; it is ignored (not set)");
                }

                default:
                    return new Parsed(raw, true, 0, text, null);
            }
        }

        private static HoldSetting Get(string key)
        {
            if (!registry.Value.ByKey.TryGetValue(key, out var s))
                throw new ArgumentException($"unknown Hold setting '{key}'", nameof(key));

            return s;
        }

        /// <summary>True when the setting's property holds a value that parses.</summary>
        public static bool IsSet(string key) => Read(Get(key)).Ok;

        /// <summary>The property's number, clamped, when set; else <paramref name="fallback"/> (goals.json or the code default).</summary>
        public static double Number(string key, double fallback)
        {
            var p = Read(Get(key));
            return p.Ok ? p.Number : fallback;
        }

        /// <summary>The property's whole number, clamped, when set; else <paramref name="fallback"/>.</summary>
        public static int Integer(string key, int fallback)
        {
            var p = Read(Get(key));
            return p.Ok ? (int)Math.Round(p.Number, MidpointRounding.AwayFromZero) : fallback;
        }

        /// <summary>The property's on/off when set; else <paramref name="fallback"/>.</summary>
        public static bool Toggle(string key, bool fallback)
        {
            var p = Read(Get(key));
            return p.Ok ? p.Number != 0 : fallback;
        }

        /// <summary>The property's choice (lower case) when set; else <paramref name="fallback"/>.</summary>
        public static string Choice(string key, string fallback)
        {
            var p = Read(Get(key));
            return p.Ok ? p.Text : fallback;
        }

        /// <summary>The property's text when set; else <paramref name="fallback"/>.</summary>
        public static string Text(string key, string fallback)
        {
            var p = Read(Get(key));
            return p.Ok ? p.Text : fallback;
        }

        // ---- checking and reporting ---------------------------------------------------------------------------

        /// <summary>
        /// What happens to <paramref name="raw"/> if it is set: null when it is used as is, else a warning (clamped,
        /// rounded, or ignored because it does not parse). An empty value is "not set" and never warns.
        /// </summary>
        public static string Check(HoldSetting s, string raw)
        {
            if (s == null || s.IsReused || string.IsNullOrWhiteSpace(raw))
                return null;

            return Parse(s, raw).Warning ?? TextCheck(s.Key, raw);
        }

        /// <summary>The extra check of a Text setting that has a format (a list, a wcid map), or null when it is fine.</summary>
        private static string TextCheck(string key, string raw)
        {
            switch (key)
            {
                case HoldKeys.KnightMaxHealthByWcid:
                    return CheckKnightHealthByWcid(raw);
                case HoldKeys.ChainNextSource:
                case HoldKeys.ChainNextGoal:
                case HoldKeys.ChainNextBoss:
                case HoldKeys.ChainNextFamily:
                    return CheckChainAxis(key, raw);
                case HoldKeys.ChainRewardStage1:
                case HoldKeys.ChainRewardStage2:
                case HoldKeys.ChainRewardFinal:
                    return CheckRewardId(key, raw);
                case HoldKeys.ChainFullBonus:
                    return CheckWcidList(key, raw);
                case HoldKeys.CorruptedTint:
                    return WorldEventTint.TryParsePalette(raw, out _) ? null
                        : $"{HoldKeys.CorruptedTint}='{raw}' is not a palette id (0x04000000..0x0400FFFF) or none; goals.json's color stays";
                default:
                    return null;
            }
        }

        /// <summary>True when <paramref name="raw"/> parses for <paramref name="s"/> (out of range still parses; it is clamped).</summary>
        public static bool Parses(HoldSetting s, string raw) => s != null && !string.IsNullOrWhiteSpace(raw) && Parse(s, raw).Ok;

        /// <summary>
        /// The value to store for what an admin typed: aliases resolved (fail condition "any" stores 3), toggles stored as
        /// on/off. Unchanged when it does not parse (Check then reports it).
        /// </summary>
        public static string Normalize(HoldSetting s, string input)
        {
            if (s == null || string.IsNullOrWhiteSpace(input))
                return "";

            var text = input.Trim();

            if (s.Aliases != null && s.Aliases.TryGetValue(text.ToLowerInvariant(), out var aliased))
                text = aliased;

            var p = Parse(s, text);

            if (!p.Ok)
                return text;

            switch (s.Kind)
            {
                case HoldSettingKind.Toggle:
                case HoldSettingKind.Choice:
                    return p.Text;
                default:
                    return text;
            }
        }

        /// <summary>
        /// After-change hook for every property write (PropertyAdminService.SettingUpdated): a Hold setting set out of
        /// range or to something that does not parse is logged, so @modifystring and the web panel warn too.
        /// </summary>
        internal static void OnPropertyModified(string key)
        {
            if (string.IsNullOrEmpty(key))
                return;

            if (string.Equals(key, EnabledProperty, StringComparison.Ordinal))
            {
                log.Info($"[WORLDEVENT] {EnabledProperty} is now {(HoldEnabled ? "on" : "off")}: Hold goals {(HoldEnabled ? "may start" : "are refused and skipped by random rolls")}");
                return;
            }

            if (!registry.Value.ByProperty.TryGetValue(key, out var s) || s.IsReused)
                return;

            var raw = RawValue(s);
            var warning = Check(s, raw);

            if (warning != null)
                log.Warn($"[WORLDEVENT] hold setting {warning}");
            else
                log.Info($"[WORLDEVENT] hold setting {s.Key} is now {(raw.Length == 0 ? "not set" : raw)} (applies {AppliesText(s.Applies)})");
        }

        /// <summary>The warning a web edit of <paramref name="key"/> to <paramref name="raw"/> earns, or null.</summary>
        public static string WarningForProperty(string key, string raw)
        {
            if (string.IsNullOrEmpty(key) || !registry.Value.ByProperty.TryGetValue(key, out var s))
                return null;

            return Check(s, raw);
        }

        /// <summary>The value, source and flags of <paramref name="s"/> for <paramref name="goal"/> (null: the code default).</summary>
        public static HoldSettingRow Describe(HoldSetting s, GoalDef goal)
        {
            if (s.IsReused)
                return DescribeReused(s, goal);

            var raw = RawValue(s);
            var parsed = raw.Trim().Length == 0 ? default : Parse(s, raw);
            var set = parsed.Ok;

            HoldSettingSource source;
            string label;

            if (set)
            {
                source = HoldSettingSource.Property;
                label = "property";
            }
            else if (s.GoalsPath != null && IsPresent(goal, s.GoalsPath))
            {
                source = HoldSettingSource.GoalsJson;
                label = "goals.json";
            }
            else if (s.OtherFile != null)
            {
                source = HoldSettingSource.OtherFile;
                label = s.OtherFile;
            }
            else
            {
                source = HoldSettingSource.Default;
                label = "default";
            }

            string value;

            if (s.Effective != null && goal != null)
                value = SafeEffective(s, goal);
            else if (set)
                value = s.Kind == HoldSettingKind.Number || s.Kind == HoldSettingKind.Integer ? Format(parsed.Number) : parsed.Text;
            else
                value = s.CodeDefault ?? "";

            return new HoldSettingRow
            {
                Setting = s,
                Value = value,
                Source = source,
                SourceLabel = label,
                RawProperty = raw,
                Overridden = set,
                Clamped = set && parsed.Warning != null,
                Warning = raw.Trim().Length == 0 ? null : parsed.Warning ?? TextCheck(s.Key, raw)
            };
        }

        private static HoldSettingRow DescribeReused(HoldSetting s, GoalDef goal)
        {
            string raw;
            bool set;

            // A reused property counts as overridden when it differs from its own registered default (0 for the
            // "0 = use the JSON" dials, 60 for the teaser, and so on).
            try
            {
                switch (s.ExistingType)
                {
                    case "long":
                    {
                        var v = ReusedLongSource(s.PropertyName);
                        raw = v.ToString(CultureInfo.InvariantCulture);
                        set = DefaultPropertyManager.DefaultLongProperties.TryGetValue(s.PropertyName, out var d) ? v != d.Item : v > 0;
                        break;
                    }
                    case "double":
                    {
                        var v = ReusedDoubleSource(s.PropertyName);
                        raw = Format(v);
                        set = DefaultPropertyManager.DefaultDoubleProperties.TryGetValue(s.PropertyName, out var d) ? Math.Abs(v - d.Item) > 1e-12 : v > 0;
                        break;
                    }
                    case "bool":
                    {
                        var v = ReusedBoolSource(s.PropertyName);
                        raw = Format(v);
                        set = DefaultPropertyManager.DefaultBooleanProperties.TryGetValue(s.PropertyName, out var d) && v != d.Item;
                        break;
                    }
                    default:
                        raw = "";
                        set = false;
                        break;
                }
            }
            catch
            {
                raw = "";
                set = false;
            }

            var source = set ? HoldSettingSource.Property : s.GoalsPath != null && IsPresent(goal, s.GoalsPath) ? HoldSettingSource.GoalsJson : s.OtherFile != null ? HoldSettingSource.OtherFile : HoldSettingSource.Default;
            var label = source == HoldSettingSource.Property ? "property" : source == HoldSettingSource.GoalsJson ? "goals.json" : source == HoldSettingSource.OtherFile ? s.OtherFile : "default";
            var value = s.Effective != null && goal != null ? SafeEffective(s, goal) : set ? raw : s.CodeDefault;

            return new HoldSettingRow { Setting = s, Value = value, Source = source, SourceLabel = label, RawProperty = raw, Overridden = set };
        }

        private static string SafeEffective(HoldSetting s, GoalDef goal)
        {
            try
            {
                return s.Effective(goal) ?? "";
            }
            catch (Exception ex)
            {
                return $"(error: {ex.Message})";
            }
        }

        /// <summary>
        /// True when goals.json sets <paramref name="path"/> for this goal: dot-separated object keys, where a segment
        /// "name[key=value]" picks the array element whose "key" equals "value" (e.g. "protect.specialWaves[type=runners].healthMult").
        /// </summary>
        public static bool IsPresent(GoalDef goal, string path)
        {
            if (goal?.RawJson is not JsonElement root || string.IsNullOrEmpty(path))
                return false;

            var current = root;

            foreach (var segment in path.Split('.'))
            {
                var name = segment;
                string matchKey = null, matchValue = null;
                var open = segment.IndexOf('[');

                if (open > 0 && segment.EndsWith("]", StringComparison.Ordinal))
                {
                    name = segment.Substring(0, open);
                    var inner = segment.Substring(open + 1, segment.Length - open - 2);
                    var eq = inner.IndexOf('=');

                    if (eq > 0)
                    {
                        matchKey = inner.Substring(0, eq);
                        matchValue = inner.Substring(eq + 1);
                    }
                }

                if (current.ValueKind != JsonValueKind.Object || !TryGetPropertyIgnoreCase(current, name, out var next))
                    return false;

                if (matchKey != null)
                {
                    if (next.ValueKind != JsonValueKind.Array)
                        return false;

                    var found = false;

                    foreach (var item in next.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.Object && TryGetPropertyIgnoreCase(item, matchKey, out var keyValue) &&
                            keyValue.ValueKind == JsonValueKind.String && string.Equals(keyValue.GetString(), matchValue, StringComparison.OrdinalIgnoreCase))
                        {
                            next = item;
                            found = true;
                            break;
                        }
                    }

                    if (!found)
                        return false;
                }

                current = next;
            }

            return current.ValueKind != JsonValueKind.Null && current.ValueKind != JsonValueKind.Undefined;
        }

        private static bool TryGetPropertyIgnoreCase(JsonElement obj, string name, out JsonElement value)
        {
            foreach (var p in obj.EnumerateObject())
            {
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = p.Value;
                    return true;
                }
            }

            value = default;
            return false;
        }

        /// <summary>Invariant, short: 0.15, 45, 1.5.</summary>
        public static string Format(double value)
        {
            if (double.IsPositiveInfinity(value))
                return "inf";

            if (double.IsNegativeInfinity(value))
                return "-inf";

            return value.ToString("0.####", CultureInfo.InvariantCulture);
        }

        public static string Format(bool value) => value ? "on" : "off";

        /// <summary>Test helper: forget parsed values (a test swaps <see cref="StringSource"/>).</summary>
        internal static void ClearParseCache() => parseCache.Clear();
    }
}
