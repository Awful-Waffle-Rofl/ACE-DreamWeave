using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using ACE.Server.Network;

namespace ACE.Server.WorldEvents
{
    /// <summary>What a write, reset or import did: which keys changed, which were refused (scope or value), which failed, and the reply text.</summary>
    public sealed class WorldEventSettingsOutcome
    {
        /// <summary>Keys written (or reset).</summary>
        public List<string> Written { get; } = new List<string>();

        /// <summary>"key (why)" for every row refused: outside the caller's scope, or a value that cannot be written.</summary>
        public List<string> Refused { get; } = new List<string>();

        /// <summary>"key (why)" for every write the property layer failed.</summary>
        public List<string> Failed { get; } = new List<string>();

        /// <summary>The text a reset wrote for each key in <see cref="Written"/> (the default it pinned).</summary>
        public Dictionary<string, string> WrittenValues { get; } = new Dictionary<string, string>();

        /// <summary>Names a preset carried that match no row.</summary>
        public List<string> Unknown { get; } = new List<string>();

        public List<string> Warnings { get; } = new List<string>();

        public string Reply { get; set; }
    }

    public sealed partial class WorldEventSettingsService
    {
        /// <summary>A preset name: 1 to 40 of a-z, 0-9, _ and -, never starting with - (so it cannot be mistaken for a flag).</summary>
        public static readonly Regex PresetName = new Regex("^[a-z0-9][a-z0-9_-]{0,39}$", RegexOptions.Compiled);

        /// <summary>The most entries an import reply lists per category before "and N more".</summary>
        private const int ReplyListMax = 10;

        private static string Bounded(IReadOnlyList<string> items)
        {
            var shown = string.Join("; ", items.Take(ReplyListMax));
            return items.Count > ReplyListMax ? $"{shown} and {items.Count - ReplyListMax} more" : shown;
        }

        // ---- the one write choke point -------------------------------------------------------------------------

        /// <summary>
        /// EVERY property write in this service goes through here. A row whose scope is not exactly the caller's scope is refused
        /// before the writer is reached, so no loop (reset-all, replace-import) can write a key its caller does not own.
        /// Returns null on success, else why not; <paramref name="refusedByScope"/> tells the two failure kinds apart.
        /// </summary>
        private string GuardedWrite(SettingScope caller, WorldEventSettingRow row, string text, Session session, out bool refusedByScope)
        {
            refusedByScope = !row.Scope.Equals(caller);

            if (refusedByScope)
                return $"it is {row.Scope} scoped and this is a {caller} caller";

            return Io.Write(TypeFor(row), row.PropertyName, text, session);
        }

        private string UnsetText(WorldEventSettingRow row, out string error)
        {
            error = null;
            var text = Io.DefaultText(row.PropertyName);

            if (text != null)
                return text;

            if (row.Kind == SettingKind.Choice || row.Kind == SettingKind.Text)
                return "";

            error = $"{row.PropertyName} has no registered default to reset to";
            return null;
        }

        // ---- describe (reply text) -----------------------------------------------------------------------------

        /// <summary>The reply of "/worldevent settings [describe] [name|group|all|set]", over every row.</summary>
        public List<string> DescribeLines(string which) => DescribeLines(null, which);

        /// <summary>
        /// The same reply limited to the rows of <paramref name="scope"/> (a goal's own command lists only that goal's rows and
        /// groups; a name outside the scope is "no such setting" there). A null scope is every row, as the global command shows.
        /// </summary>
        public List<string> DescribeLines(SettingScope scope, string which)
        {
            var w = (which ?? "").Trim().ToLowerInvariant();
            IReadOnlyList<WorldEventSettingRow> rows = scope == null ? Registry.Rows : Registry.Rows.Where(r => r.Scope.Equals(scope)).ToList();
            var groups = rows.Select(r => r.Group).Distinct().ToList();
            var title = scope == null || scope.IsGlobal ? "World Events settings" : $"World Events {scope.GoalType} settings";
            var command = scope == null || scope.IsGlobal ? "/worldevent settings" : $"/worldevent {scope.GoalType}";
            var browse = scope == null || scope.IsGlobal ? "describe" : "settings";
            IEnumerable<WorldEventSettingRow> chosen;

            if (w.Length == 0)
            {
                var set = rows.Where(r => Describe(r).Overridden).ToList();
                var head = new List<string>
                {
                    title + ". Groups: " + string.Join(", ", groups.Select(g => $"{g} ({rows.Count(r => r.Group == g)})")),
                    set.Count == 0 ? "Nothing is set: every value is a JSON or code default." : $"{set.Count} set:"
                };

                head.AddRange(Lines(set).Select(l => "  " + l));
                head.Add($"Use: {command} {browse} <group|all|set|name>, check <name> <value>, set <name> <value>, reset <name|group|all>, export <name> [--all], import <name> [--merge]");
                return head;
            }

            if (w == "all")
                chosen = rows;
            else if (w == "set")
                chosen = rows.Where(r => Describe(r).Overridden);
            else if (groups.Contains(w, StringComparer.OrdinalIgnoreCase))
                chosen = rows.Where(r => string.Equals(r.Group, w, StringComparison.OrdinalIgnoreCase));
            else if (Registry.TryFind(w, out var one) && rows.Contains(one))
                chosen = new[] { one };
            else
                return new List<string> { $"No setting group or setting '{which}'. Groups: {string.Join(", ", groups)}, all, set." };

            var lines = new List<string> { $"{title}: {w}" };
            lines.AddRange(Lines(chosen).Select(l => "  " + l));
            return lines;
        }

        /// <summary>"/worldevent settings check &lt;name&gt; &lt;value&gt;": what a set would do, without writing.</summary>
        public string CheckReply(string name, string value) => CheckReply(null, name, value);

        /// <summary>The same for a caller of <paramref name="caller"/> scope: a row outside it would be refused (a null caller does not check scope).</summary>
        public string CheckReply(SettingScope caller, string name, string value)
        {
            if (!Registry.TryFind(name, out var row))
                return caller == null || caller.IsGlobal
                    ? $"No setting '{name}'. See /worldevent settings describe all."
                    : $"No setting '{name}'. See /worldevent {caller.GoalType} settings all.";

            if (caller != null && !row.Scope.Equals(caller))
                return $"Would be refused: {row.Key} is {row.Scope} scoped and this is a {caller} caller.";

            var c = Check(row, value);

            if (c.Refused)
                return $"Would be refused: {c.Warning}.";

            return $"{row.Key}: {c.Normalized} would be stored" + (c.Warning == null ? "." : $" (warning: {c.Warning}).");
        }

        // ---- set / reset ---------------------------------------------------------------------------------------

        /// <summary>"/worldevent settings set &lt;name&gt; &lt;value&gt;" for a caller of <paramref name="caller"/> scope.</summary>
        public WorldEventSettingsOutcome Set(SettingScope caller, string name, string value, Session session)
        {
            var o = new WorldEventSettingsOutcome();

            if (!Registry.TryFind(name, out var row))
            {
                o.Reply = $"No setting '{name}'. See /worldevent settings describe all.";
                return o;
            }

            var c = Check(row, value);

            if (c.Refused)
            {
                o.Refused.Add($"{row.Key} ({c.Warning})");
                o.Reply = $"Not set: {c.Warning}.";
                return o;
            }

            var error = GuardedWrite(caller, row, c.Normalized, session, out var scopeRefused);

            if (error != null)
            {
                (scopeRefused ? o.Refused : o.Failed).Add($"{row.Key} ({error})");
                o.Reply = $"Not set: {row.Key}: {error}.";
                return o;
            }

            o.Written.Add(row.Key);

            if (c.Warning != null)
                o.Warnings.Add(c.Warning);

            var d = Describe(row);
            o.Reply = $"{row.Key} = {d.Value} ({row.PropertyName}; applies {WorldEventSettingRow.AppliesText(row.Applies)})." + (c.Warning == null ? "" : $" Warning: {c.Warning}.");
            return o;
        }

        /// <summary>"/worldevent settings reset &lt;name|group|all&gt;": WRITES each set row's current registered default (the property store has no removal, so the row is pinned to that value, not unset). A row named outside <paramref name="caller"/>'s scope is refused; a group or "all" loop skips such rows silently.</summary>
        public WorldEventSettingsOutcome Reset(SettingScope caller, string which, Session session)
        {
            var o = new WorldEventSettingsOutcome();

            if (string.IsNullOrWhiteSpace(which))
            {
                o.Reply = "Use: /worldevent settings reset <name|group|all>";
                return o;
            }

            var w = which.Trim().ToLowerInvariant();
            List<WorldEventSettingRow> chosen;
            WorldEventSettingRow named = null;

            if (w == "all")
                chosen = Registry.Rows.ToList();
            else if (Registry.IsGroup(w) && (caller.IsGlobal || Registry.InGroup(w).Any(r => r.Scope.Equals(caller))))
                chosen = Registry.InGroup(w).ToList();
            else if (Registry.TryFind(w, out var one))
            {
                named = one;
                chosen = new List<WorldEventSettingRow> { one };
            }
            else
            {
                o.Reply = $"No setting or group '{which}'.";
                return o;
            }

            // Only a row named explicitly is refused loudly when it is outside the caller's scope. A group or "all" loop skips
            // other scopes' rows silently (they are not the caller's to reset); GuardedWrite stays the backstop either way.
            var bulk = named == null;

            foreach (var row in chosen)
            {
                if (bulk && !row.Scope.Equals(caller))
                    continue;

                var overridden = Describe(row).Overridden;

                // A named row is always checked; in a loop only the rows that would actually change are visited.
                if (!overridden && bulk)
                    continue;

                if (!overridden && row.Scope.Equals(caller))
                    continue;

                var text = UnsetText(row, out var noDefault);

                if (noDefault != null)
                {
                    o.Failed.Add($"{row.Key} ({noDefault})");
                    continue;
                }

                var error = GuardedWrite(caller, row, text, bulk ? null : session, out var scopeRefused);

                if (error == null)
                {
                    o.Written.Add(row.Key);
                    o.WrittenValues[row.Key] = text;
                }
                else
                    (scopeRefused ? o.Refused : o.Failed).Add($"{row.Key} ({error})");
            }

            var written = o.Written.Select(k => $"{k}={o.WrittenValues[k]}").ToList();

            if (bulk && o.Written.Count > 0)
                Io.Audit(session, $"reset {o.Written.Count} World Events setting(s) to their current default ({w}, {caller}): {string.Join(", ", written)}");

            // A reset WRITES the property's current default; the property store has no removal, so nothing is ever unset.
            var sb = new StringBuilder(o.Written.Count == 0
                ? $"Nothing to reset in {w}: every value is already its default."
                : $"Reset writes the current default: {Bounded(written)}.");

            if (o.Refused.Count > 0)
                sb.Append($" Refused: {string.Join("; ", o.Refused)}.");

            if (o.Failed.Count > 0)
                sb.Append($" Failed: {string.Join("; ", o.Failed)}.");

            o.Reply = sb.ToString();
            return o;
        }

        // ---- presets -------------------------------------------------------------------------------------------

        public sealed class PresetFile
        {
            public string Preset { get; set; }
            public string Exported { get; set; }
            public string Scope { get; set; }
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

        private const long MaxPresetBytes = 256 * 1024;

        private bool TryPresetPath(string name, out string path, out string error)
        {
            path = null;
            error = null;
            var n = (name ?? "").Trim().ToLowerInvariant();

            if (n.EndsWith(".json", StringComparison.Ordinal))
                n = n.Substring(0, n.Length - 5);

            if (!PresetName.IsMatch(n))
            {
                error = "a preset name is 1 to 40 of a-z, 0-9, _ and -, not starting with - (no folders)";
                return false;
            }

            var folder = Io.PresetFolder();

            if (string.IsNullOrWhiteSpace(folder))
            {
                error = "no axis folder is loaded, so there is no presets folder";
                return false;
            }

            var full = Path.GetFullPath(Path.Combine(folder, n + ".json"));

            if (!string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            {
                error = "the preset path leaves the presets folder";
                return false;
            }

            path = full;
            return true;
        }

        /// <summary>The preset content for <paramref name="caller"/>: only rows of that scope; the set ones, or every effective value with <paramref name="all"/>.</summary>
        public PresetFile BuildPreset(SettingScope caller, string name, bool all)
        {
            var file = new PresetFile
            {
                Preset = name,
                Exported = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                Scope = caller.ToString(),
                Note = all ? "every effective value" : "the settings that were set"
            };

            foreach (var row in Registry.Rows.Where(r => r.Scope.Equals(caller)))
            {
                var d = Describe(row);

                if (!all && !d.Overridden)
                    continue;

                // An unset row is exported as its unset marker, never as the JSON or code default it falls back to: writing
                // that fallback would pin it on import, so a later goals.json change would no longer reach the row.
                file.Settings[row.Key] = d.Overridden ? d.Value ?? "" : ExportText(row, d);
            }

            return file;
        }

        /// <summary>The text "not set" is stored as under a row's rule: -1, 0 or empty; null for an AlwaysSet row, which is never unset.</summary>
        public static string UnsetMarker(WorldEventSettingRow row)
        {
            switch (row.Unset)
            {
                case SettingUnsetRule.NegativeIsUnset: return "-1";
                case SettingUnsetRule.NonPositiveIsUnset: return "0";
                case SettingUnsetRule.EmptyIsUnset: return "";
                default: return null;
            }
        }

        private static string ExportText(WorldEventSettingRow row, WorldEventSettingDescription d) => UnsetMarker(row) ?? d.Value ?? "";

        /// <summary>"/worldevent settings export &lt;name&gt; [--all]".</summary>
        public WorldEventSettingsOutcome Export(SettingScope caller, string name, bool all)
        {
            var o = new WorldEventSettingsOutcome();

            if (!TryPresetPath(name, out var path, out var error))
            {
                o.Reply = $"Not exported: {error}.";
                return o;
            }

            // Presets share one folder: never overwrite another scope's preset.
            if (File.Exists(path))
            {
                try
                {
                    var existing = JsonSerializer.Deserialize<PresetFile>(File.ReadAllText(path), PresetJson);

                    if (existing != null && !string.IsNullOrEmpty(existing.Scope) && !string.Equals(existing.Scope, caller.ToString(), StringComparison.Ordinal))
                    {
                        o.Reply = $"Not exported: {Path.GetFileNameWithoutExtension(path)} is a {existing.Scope} preset; this command is {caller}.";
                        return o;
                    }
                }
                catch (Exception)
                {
                    // An unreadable file is not another scope's preset; it is overwritten as before.
                }
            }

            var file = BuildPreset(caller, Path.GetFileNameWithoutExtension(path), all);

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonSerializer.Serialize(file, PresetJson), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                o.Reply = $"Not exported: {ex.Message}.";
                return o;
            }

            o.Written.AddRange(file.Settings.Keys);
            o.Reply = $"Exported {file.Settings.Count} setting(s) to {path}.";
            return o;
        }

        /// <summary>
        /// "/worldevent settings import &lt;name&gt; [--merge]": without --merge every set row of the caller's scope that the file
        /// does not mention is written back to its current default first (pinned, not unset), then the file's values are applied.
        /// Rows the FILE names outside the caller's scope are refused and listed; other scopes' rows are skipped silently.
        /// Unknown names are skipped and listed. A row named twice in the file is applied once, the last entry winning.
        /// </summary>
        public WorldEventSettingsOutcome Import(SettingScope caller, string name, bool merge, Session session)
        {
            var o = new WorldEventSettingsOutcome();

            if (!TryPresetPath(name, out var path, out var error))
            {
                o.Reply = $"Not imported: {error}.";
                return o;
            }

            PresetFile file;

            try
            {
                var info = new FileInfo(path);

                if (!info.Exists)
                {
                    o.Reply = $"Not imported: no preset at {path}.";
                    return o;
                }

                if (info.Length > MaxPresetBytes)
                {
                    o.Reply = $"Not imported: {path} is larger than {MaxPresetBytes / 1024} KB.";
                    return o;
                }

                file = JsonSerializer.Deserialize<PresetFile>(File.ReadAllText(path), PresetJson) ?? new PresetFile();
            }
            catch (Exception ex)
            {
                o.Reply = $"Not imported: {path} is not a preset ({ex.Message}).";
                return o;
            }

            if (!string.IsNullOrEmpty(file.Scope) && !string.Equals(file.Scope, caller.ToString(), StringComparison.Ordinal))
            {
                o.Reply = $"Not imported: {Path.GetFileNameWithoutExtension(path)} is a {file.Scope} preset; this command is {caller}.";
                return o;
            }

            // Key lookup is case-insensitive and also accepts a property name, so a file can name one row twice. Resolve every
            // entry to its row first; the LAST entry for a row wins, and each row is checked, written and reported once.
            var byRow = new Dictionary<WorldEventSettingRow, string>();
            var rowOrder = new List<WorldEventSettingRow>();

            foreach (var (key, value) in file.Settings ?? new Dictionary<string, string>())
            {
                if (!Registry.TryFind(key, out var found))
                {
                    if (!o.Unknown.Contains(key, StringComparer.OrdinalIgnoreCase))
                        o.Unknown.Add(key);

                    continue;
                }

                if (!byRow.ContainsKey(found))
                    rowOrder.Add(found);

                byRow[found] = value;
            }

            var plan = new List<(WorldEventSettingRow Row, string Value)>();

            foreach (var row in rowOrder)
            {
                var value = byRow[row];

                if (string.IsNullOrWhiteSpace(value))
                    continue;

                var c = Check(row, value);

                if (c.Refused)
                {
                    o.Refused.Add(c.Warning);
                    continue;
                }

                // A row the file names as not set, and that is not set now, needs no write: skipping it keeps the property
                // untouched instead of pinning the marker over its shipped default.
                var marker = UnsetMarker(row);

                if (marker != null && c.Normalized == marker && !Describe(row).Overridden)
                    continue;

                if (c.Warning != null)
                    o.Warnings.Add(c.Warning);

                plan.Add((row, c.Normalized));
            }

            var resetCount = 0;

            if (!merge)
            {
                var inFile = new HashSet<WorldEventSettingRow>(plan.Select(p => p.Row));

                // Rows of other scopes are skipped silently (not the caller's to reset); GuardedWrite stays the backstop.
                foreach (var row in Registry.Rows.Where(r => r.Scope.Equals(caller) && !inFile.Contains(r) && Describe(r).Overridden))
                {
                    var text = UnsetText(row, out var noDefault);

                    if (noDefault != null)
                    {
                        o.Failed.Add($"{row.Key} ({noDefault})");
                        continue;
                    }

                    var err = GuardedWrite(caller, row, text, null, out var scopeRefused);

                    if (err == null)
                        resetCount++;
                    else
                        (scopeRefused ? o.Refused : o.Failed).Add($"{row.Key} ({err})");
                }
            }

            foreach (var (row, text) in plan)
            {
                var err = GuardedWrite(caller, row, text, null, out var scopeRefused);

                if (err == null)
                    o.Written.Add(row.Key);
                else
                    (scopeRefused ? o.Refused : o.Failed).Add($"{row.Key} ({err})");
            }

            // The preset's self-declared name is file content: echo (and audit) it only when it is a valid preset name,
            // else fall back to the name of the file it was read from.
            var shownName = file.Preset != null && PresetName.IsMatch(file.Preset) ? file.Preset : Path.GetFileNameWithoutExtension(path);

            Io.Audit(session, $"imported World Events settings preset '{shownName}' ({caller}) from {path}: {o.Written.Count} applied, {resetCount} reset to default{(merge ? " (merge)" : "")}");

            var sb = new StringBuilder($"Imported '{shownName}': {o.Written.Count} setting(s) applied");

            sb.Append(merge ? " (merged; others kept)." : $", {resetCount} other setting(s) reset to their default.");

            if (o.Written.Count > 0)
                sb.Append($" Applied: {Bounded(o.Written)}.");

            if (o.Unknown.Count > 0)
                sb.Append($" Unknown, skipped: {Bounded(o.Unknown)}.");

            if (o.Refused.Count > 0)
                sb.Append($" Refused: {Bounded(o.Refused)}.");

            if (o.Failed.Count > 0)
                sb.Append($" Failed: {Bounded(o.Failed)}.");

            if (o.Warnings.Count > 0)
                sb.Append($" Warnings: {Bounded(o.Warnings)}.");

            o.Reply = sb.ToString();
            return o;
        }
    }
}
