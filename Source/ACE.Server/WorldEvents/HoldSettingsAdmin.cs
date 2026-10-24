using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.WorldEvents.Defs;

namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// "/worldevent hold ..." and the web panel's read-only listing: show, set, reset, export and import the Hold settings
    /// (HoldSettings). Every write goes through PropertyAdminService.TryModify (audited, and the after-change hook warns),
    /// so a change made here is exactly a change made with @modifystring or the web settings editor.
    /// </summary>
    public static class HoldSettingsAdmin
    {
        /// <summary>Test seam: writes one property (its type, name and text value) for the admin; returns null on success, else why not.</summary>
        internal static Func<string, string, string, Session, string> Writer = WriteProperty;

        /// <summary>Test seam: the folder presets live in.</summary>
        internal static Func<string> PresetFolder = DefaultPresetFolder;

        public static readonly Regex PresetName = new Regex("^[a-z0-9_-]{1,40}$", RegexOptions.Compiled);

        // ---- reading -------------------------------------------------------------------------------------------

        /// <summary>The goal the values are shown for: the live run's Hold goal (or the chain's first goal), else the store's first Hold goal.</summary>
        public static GoalDef ShownGoal()
        {
            var current = WorldEventManager.Current;

            if (current?.Composition?.Goal is GoalDef live && live.TypeKind == GoalType.Hold)
                return live;

            var store = WorldEventManager.Store;

            if (store?.Goals == null)
                return null;

            return store.Goals.TryGetValue("defend_envoys", out var envoys)
                ? envoys
                : store.Goals.Values.Where(g => g?.TypeKind == GoalType.Hold).OrderBy(g => g.Id, StringComparer.Ordinal).FirstOrDefault();
        }

        /// <summary>One line per setting of <paramref name="settings"/>.</summary>
        public static List<string> Lines(IEnumerable<HoldSetting> settings, GoalDef goal)
        {
            var lines = new List<string>();

            foreach (var s in settings)
            {
                var row = HoldSettings.Describe(s, goal);
                var sb = new StringBuilder();

                sb.Append(s.Key).Append(" = ").Append(string.IsNullOrEmpty(row.Value) ? "(none)" : row.Value);
                sb.Append("  (").Append(row.SourceLabel).Append(')');
                sb.Append("  [").Append(s.RangeText).Append(']');
                sb.Append("  ").Append(HoldSettings.AppliesText(s.Applies));

                if (row.Overridden)
                    sb.Append("  *set");

                if (row.Clamped)
                    sb.Append("  !clamped");

                if (s.Global)
                    sb.Append("  (global)");

                if (s.IsNew)
                    sb.Append("  new");

                if (row.Warning != null)
                    sb.Append("  warning: ").Append(row.Warning);

                lines.Add(sb.ToString());
            }

            return lines;
        }

        /// <summary>
        /// "/worldevent hold settings [group|all|set]": no argument lists the groups and every setting that is set; a group
        /// lists its settings; "all" lists every setting; "set" lists only those set.
        /// </summary>
        public static string Settings(string which)
        {
            var goal = ShownGoal();
            var sb = new StringBuilder();
            var shownFor = goal == null ? "no Hold goal loaded: code defaults" : $"values for {goal.Id}";

            if (string.IsNullOrWhiteSpace(which))
            {
                sb.AppendLine($"Hold settings ({shownFor}; Hold goals are {(HoldSettings.HoldEnabled ? "on" : "off")}: {HoldSettings.EnabledProperty}).");
                sb.AppendLine("Groups: " + string.Join(", ", HoldSettings.Groups.Select(g => $"{g} ({HoldSettings.InGroup(g).Count})")));

                var set = HoldSettings.All.Where(s => HoldSettings.Describe(s, goal).Overridden).ToList();

                sb.AppendLine(set.Count == 0 ? "Nothing is set: every value comes from goals.json or the code default." : $"{set.Count} set:");

                foreach (var line in Lines(set, goal))
                    sb.AppendLine("  " + line);

                sb.Append("Use: /worldevent hold settings <group|all|set>, hold set <name> <value>, hold reset <name|group|all>, hold export <name> [--all], hold import <name> [--merge]");
                return sb.ToString();
            }

            var w = which.Trim().ToLowerInvariant();
            IEnumerable<HoldSetting> chosen;

            if (w == "all")
                chosen = HoldSettings.All;
            else if (w == "set")
                chosen = HoldSettings.All.Where(s => HoldSettings.Describe(s, goal).Overridden);
            else if (HoldSettings.IsGroup(w))
                chosen = HoldSettings.InGroup(w);
            else if (HoldSettings.TryFind(w, out var one))
                chosen = new[] { one };
            else
                return $"No Hold setting group or setting '{which}'. Groups: {string.Join(", ", HoldSettings.Groups)}, all, set.";

            sb.AppendLine($"Hold settings: {w} ({shownFor})");

            foreach (var line in Lines(chosen, goal))
                sb.AppendLine("  " + line);

            return sb.ToString().TrimEnd();
        }

        // ---- writing -------------------------------------------------------------------------------------------

        /// <summary>The property type and text to write for <paramref name="input"/> on <paramref name="s"/>.</summary>
        private static (string Type, string Value) Encode(HoldSetting s, string input)
        {
            if (!s.IsReused)
                return (PropertyAdminService.TypeString, HoldSettings.Normalize(s, input));

            var text = (input ?? "").Trim();

            switch (s.ExistingType)
            {
                case "bool":
                    var on = text.Equals("on", StringComparison.OrdinalIgnoreCase) || text.Equals("true", StringComparison.OrdinalIgnoreCase)
                        || text == "1" || text.Equals("yes", StringComparison.OrdinalIgnoreCase);
                    return (PropertyAdminService.TypeBool, on ? "True" : "False");
                case "long":
                    return (PropertyAdminService.TypeLong, text);
                default:
                    return (PropertyAdminService.TypeDouble, text);
            }
        }

        /// <summary>The value that puts <paramref name="s"/> back to "not set" (new) or its registered default (reused).</summary>
        private static (string Type, string Value) Unset(HoldSetting s)
        {
            if (!s.IsReused)
                return (PropertyAdminService.TypeString, "");

            switch (s.ExistingType)
            {
                case "bool":
                    return (PropertyAdminService.TypeBool, DefaultPropertyManager.DefaultBooleanProperties[s.PropertyName].Item ? "True" : "False");
                case "long":
                    return (PropertyAdminService.TypeLong, DefaultPropertyManager.DefaultLongProperties[s.PropertyName].Item.ToString(CultureInfo.InvariantCulture));
                default:
                    return (PropertyAdminService.TypeDouble, DefaultPropertyManager.DefaultDoubleProperties[s.PropertyName].Item.ToString("R", CultureInfo.InvariantCulture));
            }
        }

        /// <summary>Checks a value the way the setting will read it: null when fine, else the warning (clamped, ignored, unknown id).</summary>
        public static string Validate(HoldSetting s, string input, out bool refuse)
        {
            refuse = false;

            if (s.IsReused)
            {
                var text = (input ?? "").Trim();

                switch (s.ExistingType)
                {
                    case "bool":
                        refuse = !new[] { "on", "off", "true", "false", "1", "0", "yes", "no" }.Contains(text.ToLowerInvariant());
                        return refuse ? $"{s.Key}='{input}' is not on or off" : null;
                    case "long":
                        refuse = !long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l);
                        return refuse ? $"{s.Key}='{input}' is not a whole number" : l < s.Min || l > s.Max ? $"{s.Key}={l} is outside the usual {s.RangeText}" : null;
                    default:
                        refuse = !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || !double.IsFinite(d);
                        return refuse ? $"{s.Key}='{input}' is not a number" : d < s.Min || d > s.Max ? $"{s.Key}={HoldSettings.Format(d)} is outside the usual {s.RangeText}" : null;
                }
            }

            return HoldSettings.Check(s, HoldSettings.Normalize(s, input));
        }

        /// <summary>"/worldevent hold set &lt;name&gt; &lt;value&gt;". Returns the reply.</summary>
        public static string Set(string name, string value, Session session)
        {
            if (!HoldSettings.TryFind(name, out var s))
                return $"No Hold setting '{name}'. See /worldevent hold settings.";

            if (string.IsNullOrWhiteSpace(value))
                return $"Give a value, or use /worldevent hold reset {s.Key}.";

            var warning = Validate(s, value, out var refuse);

            if (refuse)
                return $"Not set: {warning}.";

            var (type, encoded) = Encode(s, value);
            var error = Writer(type, s.PropertyName, encoded, session);

            if (error != null)
                return $"Not set: {error}.";

            var row = HoldSettings.Describe(s, ShownGoal());
            var reply = $"{s.Key} = {row.Value} ({s.PropertyName}; applies {HoldSettings.AppliesText(s.Applies)}).";

            return warning == null ? reply : $"{reply} Warning: {warning}.";
        }

        /// <summary>"/worldevent hold reset &lt;name|group|all&gt;". Returns the reply.</summary>
        public static string Reset(string which, Session session)
        {
            if (string.IsNullOrWhiteSpace(which))
                return "Use: /worldevent hold reset <name|group|all>";

            var w = which.Trim().ToLowerInvariant();
            List<HoldSetting> chosen;

            if (w == "all")
                chosen = HoldSettings.All.ToList();
            else if (HoldSettings.IsGroup(w))
                chosen = HoldSettings.InGroup(w).ToList();
            else if (HoldSettings.TryFind(w, out var one))
                chosen = new List<HoldSetting> { one };
            else
                return $"No Hold setting or group '{which}'.";

            var goal = ShownGoal();
            var bulk = chosen.Count > 1;
            var reset = new List<string>();
            var failed = new List<string>();

            foreach (var s in chosen.Where(s => HoldSettings.Describe(s, goal).Overridden))
            {
                var (type, value) = Unset(s);
                var error = Writer(type, s.PropertyName, value, bulk ? null : session);

                if (error == null)
                    reset.Add(s.Key);
                else
                    failed.Add($"{s.Key} ({error})");
            }

            if (bulk && reset.Count > 0)
                Audit(session, $"reset {reset.Count} World Events Hold setting(s) ({w}): {string.Join(", ", reset)}");

            var reply = reset.Count == 0 ? $"Nothing to reset in {w}: it was not set." : $"Reset {reset.Count}: {string.Join(", ", reset)}.";

            return failed.Count == 0 ? reply : $"{reply} Failed: {string.Join("; ", failed)}.";
        }

        // ---- presets -------------------------------------------------------------------------------------------

        public sealed class PresetFile
        {
            public string Preset { get; set; }
            public string Exported { get; set; }
            public string Note { get; set; }
            public Dictionary<string, string> Settings { get; set; } = new Dictionary<string, string>();
        }

        private static readonly JsonSerializerOptions PresetJson = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        private static string DefaultPresetFolder()
        {
            var axes = WorldEventManager.Store?.ResolvedFolder;

            if (string.IsNullOrWhiteSpace(axes))
                return null;

            var events = Path.GetDirectoryName(Path.GetFullPath(axes).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            return events == null ? null : Path.Combine(events, "hold-presets");
        }

        private static bool TryPresetPath(string name, out string path, out string error)
        {
            path = null;
            error = null;
            var n = (name ?? "").Trim().ToLowerInvariant();

            if (n.EndsWith(".json", StringComparison.Ordinal))
                n = n.Substring(0, n.Length - 5);

            if (!PresetName.IsMatch(n))
            {
                error = "a preset name is 1 to 40 of a-z, 0-9, _ and - (no folders)";
                return false;
            }

            var folder = PresetFolder();

            if (string.IsNullOrWhiteSpace(folder))
            {
                error = "no axis folder is loaded, so there is no hold-presets folder";
                return false;
            }

            path = Path.Combine(folder, n + ".json");
            return true;
        }

        /// <summary>The preset file's content: the settings that are set, or every effective value with <paramref name="all"/>.</summary>
        public static PresetFile BuildPreset(string name, bool all)
        {
            var goal = ShownGoal();
            var file = new PresetFile
            {
                Preset = name,
                Exported = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                Note = all ? "every Hold setting's effective value" : "the Hold settings that were set"
            };

            foreach (var s in HoldSettings.All)
            {
                var row = HoldSettings.Describe(s, goal);

                if (!all && !row.Overridden)
                    continue;

                file.Settings[s.Key] = row.Overridden && !s.IsReused ? row.RawProperty.Trim() : row.Value ?? "";
            }

            return file;
        }

        /// <summary>"/worldevent hold export &lt;name&gt; [--all]". Returns the reply.</summary>
        public static string Export(string name, bool all)
        {
            if (!TryPresetPath(name, out var path, out var error))
                return $"Not exported: {error}.";

            var file = BuildPreset(Path.GetFileNameWithoutExtension(path), all);

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonSerializer.Serialize(file, PresetJson));
            }
            catch (Exception ex)
            {
                return $"Not exported: {ex.Message}.";
            }

            return $"Exported {file.Settings.Count} setting(s) to {path}.";
        }

        /// <summary>
        /// "/worldevent hold import &lt;name&gt; [--merge]": every Hold setting is reset first (unless --merge), then the file's
        /// are applied. Unknown names are skipped and listed; values that will be clamped or ignored are listed. Returns the reply.
        /// </summary>
        public static string Import(string name, bool merge, Session session)
        {
            if (!TryPresetPath(name, out var path, out var error))
                return $"Not imported: {error}.";

            PresetFile file;

            try
            {
                if (!File.Exists(path))
                    return $"Not imported: no preset at {path}.";

                file = JsonSerializer.Deserialize<PresetFile>(File.ReadAllText(path), PresetJson) ?? new PresetFile();
            }
            catch (Exception ex)
            {
                return $"Not imported: {path} is not a preset ({ex.Message}).";
            }

            var unknown = new List<string>();
            var refused = new List<string>();
            var warnings = new List<string>();
            var plan = new List<(HoldSetting Setting, string Value)>();

            foreach (var (key, value) in file.Settings ?? new Dictionary<string, string>())
            {
                if (!HoldSettings.TryFind(key, out var s))
                {
                    unknown.Add(key);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(value))
                    continue;

                var warning = Validate(s, value, out var refuse);

                if (refuse)
                {
                    refused.Add(warning);
                    continue;
                }

                if (warning != null)
                    warnings.Add(warning);

                plan.Add((s, value));
            }

            var resetCount = 0;

            if (!merge)
            {
                var goal = ShownGoal();
                var inFile = new HashSet<HoldSetting>(plan.Select(p => p.Setting));

                foreach (var s in HoldSettings.All.Where(s => !inFile.Contains(s) && HoldSettings.Describe(s, goal).Overridden))
                {
                    var (type, value) = Unset(s);

                    if (Writer(type, s.PropertyName, value, null) == null)
                        resetCount++;
                }
            }

            var applied = 0;

            foreach (var (s, value) in plan)
            {
                var (type, encoded) = Encode(s, value);
                var writeError = Writer(type, s.PropertyName, encoded, null);

                if (writeError == null)
                    applied++;
                else
                    refused.Add($"{s.Key} ({writeError})");
            }

            Audit(session, $"imported World Events Hold preset '{file.Preset ?? name}' from {path}: {applied} applied, {resetCount} reset{(merge ? " (merge)" : "")}");

            var sb = new StringBuilder($"Imported '{file.Preset ?? name}': {applied} setting(s) applied");

            sb.Append(merge ? " (merged; others kept)." : $", {resetCount} other setting(s) reset.");

            if (unknown.Count > 0)
                sb.Append($" Unknown, skipped: {string.Join(", ", unknown)}.");

            if (refused.Count > 0)
                sb.Append($" Refused: {string.Join("; ", refused)}.");

            if (warnings.Count > 0)
                sb.Append($" Warnings: {string.Join("; ", warnings)}.");

            return sb.ToString();
        }

        /// <summary>The preset names in the folder.</summary>
        public static List<string> Presets()
        {
            try
            {
                var folder = PresetFolder();

                return string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)
                    ? new List<string>()
                    : Directory.GetFiles(folder, "*.json").Select(Path.GetFileNameWithoutExtension).OrderBy(n => n, StringComparer.Ordinal).ToList();
            }
            catch
            {
                return new List<string>();
            }
        }

        // ---- the web panel (read-only; editing is the generic settings editor) ---------------------------------

        /// <summary>GET /v1/admin/world-events/hold-settings: every setting with its value and where it came from.</summary>
        public static object WebBody()
        {
            var goal = ShownGoal();

            return new
            {
                hold_enabled = HoldSettings.HoldEnabled,
                goal = goal?.Id,
                presets = Presets(),
                settings = HoldSettings.All.Select(s =>
                {
                    var row = HoldSettings.Describe(s, goal);

                    return new
                    {
                        name = s.Key,
                        property = s.PropertyName,
                        group = s.Group,
                        value = row.Value,
                        source = row.SourceLabel,
                        raw = row.RawProperty,
                        overridden = row.Overridden,
                        clamped = row.Clamped,
                        warning = row.Warning,
                        range = s.RangeText,
                        code_default = s.CodeDefault,
                        applies = HoldSettings.AppliesText(s.Applies),
                        is_new = s.IsNew,
                        global = s.Global,
                        description = s.Description
                    };
                }).ToList()
            };
        }

        // ---- plumbing -----------------------------------------------------------------------------------------

        private static string WriteProperty(string type, string property, string value, Session session)
        {
            var result = PropertyAdminService.TryModify(type, property, value, PropertyAdminActor.InGame(session));

            switch (result?.Outcome)
            {
                case PropertyModifyOutcome.Updated:
                case PropertyModifyOutcome.Unchanged:
                    return null;
                case PropertyModifyOutcome.UnknownKey:
                    return $"{property} is not a server property";
                default:
                    return $"{property} was not written ({result?.Outcome})";
            }
        }

        private static void Audit(Session session, string text)
        {
            try
            {
                PropertyAdminService.AuditInGame(session?.Player, text);
            }
            catch
            {
                // An audit line must never undo the change it reports.
            }
        }
    }
}
