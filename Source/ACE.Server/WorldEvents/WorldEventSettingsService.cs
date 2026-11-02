using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

using ACE.Server.Managers;
using ACE.Server.Network;

namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// The seams the settings service reads and writes through. <see cref="CreateDefault"/> wires them to PropertyManager and
    /// PropertyAdminService; a unit test builds an instance with its own delegates, because PropertyManager reads throw there.
    /// A bare <c>new</c> instance is inert: every read is 0 / false / empty, there is no registered default, and every write fails.
    /// </summary>
    public sealed class WorldEventSettingsIo
    {
        public Func<string, double> ReadDouble { get; init; } = _ => 0;
        public Func<string, long> ReadLong { get; init; } = _ => 0;
        public Func<string, bool> ReadBool { get; init; } = _ => false;
        public Func<string, string> ReadString { get; init; } = _ => "";

        /// <summary>The property's registered default as text, or null when it has none.</summary>
        public Func<string, string> DefaultText { get; init; } = _ => null;

        /// <summary>The JSON axis value at a row's JsonPath as text, or null when it is not available.</summary>
        public Func<string, string> JsonValue { get; init; } = _ => null;

        /// <summary>Writes one property (type, name, text, session); returns null on success, else why not.</summary>
        public Func<string, string, string, Session, string> Write { get; init; } = (_, _, _, _) => "no writer is wired";

        /// <summary>The folder presets live in; null when there is none.</summary>
        public Func<string> PresetFolder { get; init; } = () => null;

        /// <summary>One audit line for a bulk change.</summary>
        public Action<Session, string> Audit { get; init; } = (_, _) => { };

        public static WorldEventSettingsIo CreateDefault() => new WorldEventSettingsIo
        {
            ReadDouble = key => PropertyManager.GetDouble(key).Item,
            ReadLong = key => PropertyManager.GetLong(key).Item,
            ReadBool = key => PropertyManager.GetBool(key).Item,
            ReadString = key => PropertyManager.GetString(key).Item,
            DefaultText = RegisteredDefault,
            Write = WriteProperty,
            PresetFolder = DefaultPresetFolder,
            Audit = AuditLine
        };

        private static string RegisteredDefault(string key)
        {
            if (DefaultPropertyManager.DefaultDoubleProperties.TryGetValue(key, out var d))
                return WorldEventSettingsService.FormatNumber(d.Item);

            if (DefaultPropertyManager.DefaultLongProperties.TryGetValue(key, out var l))
                return l.Item.ToString(CultureInfo.InvariantCulture);

            if (DefaultPropertyManager.DefaultBooleanProperties.TryGetValue(key, out var b))
                return b.Item ? "True" : "False";

            if (DefaultPropertyManager.DefaultStringProperties.TryGetValue(key, out var s))
                return s.Item ?? "";

            return null;
        }

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

        private static string DefaultPresetFolder()
        {
            var axes = WorldEventManager.Store?.ResolvedFolder;

            if (string.IsNullOrWhiteSpace(axes))
                return null;

            var events = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(axes).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));

            return events == null ? null : System.IO.Path.Combine(events, "settings-presets");
        }

        private static void AuditLine(Session session, string text)
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

    /// <summary>One row of a settings listing: the row, its value now and where that value came from.</summary>
    public sealed class WorldEventSettingDescription
    {
        public WorldEventSettingRow Row { get; init; }

        /// <summary>The effective value as shown (bool rows as on/off).</summary>
        public string Value { get; init; }

        public SettingSource Source { get; init; }
        public string SourceLabel { get; init; }

        /// <summary>True when the property itself supplies the value (it is set, or differs from its registered default).</summary>
        public bool Overridden { get; init; }

        /// <summary>True when the stored number is outside the row's range (a write through this service would clamp it).</summary>
        public bool Clamped { get; init; }

        public string Warning { get; init; }
    }

    /// <summary>The verdict on a proposed value for a row.</summary>
    public sealed class WorldEventSettingCheck
    {
        public WorldEventSettingRow Row { get; init; }

        /// <summary>True when the value cannot be written at all (unparsable, not finite, not a choice).</summary>
        public bool Refused { get; init; }

        /// <summary>The text that would be written (clamped, normalized); null when refused.</summary>
        public string Normalized { get; init; }

        public bool Clamped { get; init; }

        /// <summary>Why it was refused, or what was changed (clamped, treated as unset); null when the value is fine as given.</summary>
        public string Warning { get; init; }
    }

    /// <summary>
    /// Describe, check and (see WorldEventSettingsService.Admin.cs) write the World Events settings rows. Values live in typed
    /// PropertyManager properties; this class only adds the rule for what "not set" means, the clamp, and the scope guard.
    /// </summary>
    public sealed partial class WorldEventSettingsService
    {
        private static readonly Lazy<WorldEventSettingsService> defaultService = new Lazy<WorldEventSettingsService>(
            () => new WorldEventSettingsService(WorldEventSettingsRegistry.Default, WorldEventSettingsIo.CreateDefault()));

        /// <summary>The shipped registry over PropertyManager.</summary>
        public static WorldEventSettingsService Default => defaultService.Value;

        public WorldEventSettingsService(WorldEventSettingsRegistry registry, WorldEventSettingsIo io)
        {
            Registry = registry ?? throw new ArgumentNullException(nameof(registry));
            Io = io ?? throw new ArgumentNullException(nameof(io));
        }

        public WorldEventSettingsRegistry Registry { get; }
        public WorldEventSettingsIo Io { get; }

        // ---- text helpers ----------------------------------------------------------------------------------------

        public static string FormatNumber(double v)
        {
            if (double.IsNegativeInfinity(v))
                return "-inf";

            if (double.IsPositiveInfinity(v))
                return "inf";

            return v.ToString("R", CultureInfo.InvariantCulture);
        }

        private static string ShowBool(bool v) => v ? "on" : "off";

        private static bool TryParseBool(string text, out bool value)
        {
            switch ((text ?? "").Trim().ToLowerInvariant())
            {
                case "on": case "true": case "1": case "yes":
                    value = true;
                    return true;
                case "off": case "false": case "0": case "no":
                    value = false;
                    return true;
                default:
                    value = false;
                    return false;
            }
        }

        // ---- describe --------------------------------------------------------------------------------------------

        /// <summary>The effective value of <paramref name="row"/> and where it came from: the property, else the JSON value, else the code default.</summary>
        public WorldEventSettingDescription Describe(WorldEventSettingRow row)
        {
            switch (row.Kind)
            {
                case SettingKind.Double:
                {
                    var d = Io.ReadDouble(row.PropertyName);
                    return DescribeNumber(row, d, double.IsFinite(d), FormatNumber(d));
                }

                case SettingKind.Long:
                {
                    var l = Io.ReadLong(row.PropertyName);
                    return DescribeNumber(row, l, true, l.ToString(CultureInfo.InvariantCulture));
                }

                case SettingKind.Bool:
                {
                    var b = Io.ReadBool(row.PropertyName);
                    var defaultText = Io.DefaultText(row.PropertyName) ?? row.CodeDefault;
                    var isDefault = TryParseBool(defaultText, out var def) && def == b;

                    return Make(row, ShowBool(b), isDefault ? SettingSource.Default : SettingSource.Property, null, false);
                }

                default:
                {
                    var s = Io.ReadString(row.PropertyName) ?? "";

                    if (s.Length == 0)
                        return Fallback(row, null, false);

                    string warning = null;

                    if (row.Kind == SettingKind.Choice && !row.Choices.Contains(s.Trim().ToLowerInvariant()))
                        warning = $"'{s}' is not one of {row.RangeText}";

                    if (warning == null && row.ParseValue != null)
                    {
                        var (_, parseError) = row.ParseValue(s.Trim());

                        if (parseError != null)
                            warning = $"stored value is invalid: {parseError}; the default is used";
                    }

                    return Make(row, s, SettingSource.Property, warning, false);
                }
            }
        }

        private WorldEventSettingDescription DescribeNumber(WorldEventSettingRow row, double stored, bool finite, string storedText)
        {
            if (!finite)
                return Fallback(row, $"stored value '{storedText}' is not a finite number; the default is used", false);

            if (IsUnsetNumber(row, stored))
                return Fallback(row, null, false);

            var clamped = stored < row.Min || stored > row.Max;
            var warning = clamped ? $"{storedText} is outside {row.RangeText}; a write here would clamp it to {FormatNumber(Math.Min(Math.Max(stored, row.Min), row.Max))}" : null;

            // A stored value equal to the property's registered default is "not touched", whatever the unset rule: a
            // NonPositiveIsUnset row whose property defaults above 0 (the built-in value) must not read as set out of the box.
            var defaultText = Io.DefaultText(row.PropertyName) ?? row.CodeDefault;
            var isDefault = double.TryParse(defaultText, NumberStyles.Float, CultureInfo.InvariantCulture, out var def) && def == stored;

            return Make(row, storedText, isDefault ? SettingSource.Default : SettingSource.Property, warning, clamped);
        }

        /// <summary>True when a finite stored number means "not set" under the row's rule (NonPositiveIsUnset: at or below 0; NegativeIsUnset: below 0).</summary>
        public static bool IsUnsetNumber(WorldEventSettingRow row, double stored)
        {
            switch (row.Unset)
            {
                case SettingUnsetRule.NonPositiveIsUnset:
                    return stored <= 0;
                case SettingUnsetRule.NegativeIsUnset:
                    return stored < 0;
                default:
                    return false;
            }
        }

        private WorldEventSettingDescription Fallback(WorldEventSettingRow row, string warning, bool clamped)
        {
            var json = row.JsonPath == null ? null : Io.JsonValue(row.JsonPath);

            if (!string.IsNullOrWhiteSpace(json))
                return Make(row, json, SettingSource.Json, warning, clamped);

            var def = row.Kind == SettingKind.Bool && TryParseBool(row.CodeDefault, out var b) ? ShowBool(b) : row.CodeDefault;

            return Make(row, def, SettingSource.Default, warning, clamped);
        }

        private static WorldEventSettingDescription Make(WorldEventSettingRow row, string value, SettingSource source, string warning, bool clamped) =>
            new WorldEventSettingDescription
            {
                Row = row,
                Value = value,
                Source = source,
                SourceLabel = source == SettingSource.Property ? "property " + row.PropertyName : source == SettingSource.Json ? "json " + row.JsonPath : "default",
                Overridden = source == SettingSource.Property,
                Clamped = clamped,
                Warning = warning
            };

        /// <summary>
        /// The value as a listing shows it: "(none)" for an empty one, and "not set" for a NegativeIsUnset row that falls through
        /// to its own -1 default (no JSON value). Describe's Value keeps the raw text, so an export of it round-trips as unset.
        /// </summary>
        private static string ShownValue(WorldEventSettingRow row, WorldEventSettingDescription d)
        {
            if (string.IsNullOrEmpty(d.Value))
                return "(none)";

            if (row.Unset == SettingUnsetRule.NegativeIsUnset && d.Source == SettingSource.Default &&
                double.TryParse(d.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v < 0)
                return "not set";

            return d.Value;
        }

        /// <summary>One line per row: key = value (source) [range] applies, with flags.</summary>
        public List<string> Lines(IEnumerable<WorldEventSettingRow> rows)
        {
            var lines = new List<string>();

            foreach (var row in rows)
            {
                var d = Describe(row);
                var sb = new StringBuilder();

                sb.Append(row.Key).Append(" = ").Append(ShownValue(row, d));
                sb.Append("  (").Append(d.SourceLabel).Append(')');
                sb.Append("  [").Append(row.RangeText).Append(']');
                sb.Append("  ").Append(WorldEventSettingRow.AppliesText(row.Applies));
                sb.Append("  <").Append(row.Scope).Append('>');

                if (d.Overridden)
                    sb.Append("  *set");

                if (d.Clamped)
                    sb.Append("  !clamped");

                if (d.Warning != null)
                    sb.Append("  warning: ").Append(d.Warning);

                lines.Add(sb.ToString());
            }

            return lines;
        }

        // ---- check -----------------------------------------------------------------------------------------------

        private static WorldEventSettingCheck Refuse(WorldEventSettingRow row, string why) =>
            new WorldEventSettingCheck { Row = row, Refused = true, Warning = why };

        /// <summary>
        /// Validates a proposed value the way a write would store it: unparsable, non-finite and non-choice values are refused;
        /// a number outside the row's range is clamped with a warning. For a NonPositiveIsUnset row a value at or below 0 is the
        /// "not set" marker and is stored as 0 without clamping; for a NegativeIsUnset row a value below 0 is, stored as -1.
        /// </summary>
        public WorldEventSettingCheck Check(WorldEventSettingRow row, string input)
        {
            var text = (input ?? "").Trim();

            if (text.Length == 0)
                return Refuse(row, $"{row.Key}: give a value, or reset it");

            if (row.ParseValue != null)
            {
                var (normalized, parseError) = row.ParseValue(text);

                if (parseError != null)
                    return Refuse(row, $"{row.Key}: {parseError}");

                text = normalized;
            }

            switch (row.Kind)
            {
                case SettingKind.Bool:
                    return TryParseBool(text, out var b)
                        ? new WorldEventSettingCheck { Row = row, Normalized = b ? "True" : "False" }
                        : Refuse(row, $"{row.Key}='{input}' is not on or off");

                case SettingKind.Choice:
                    var pick = text.ToLowerInvariant();
                    return row.Choices.Contains(pick)
                        ? new WorldEventSettingCheck { Row = row, Normalized = pick }
                        : Refuse(row, $"{row.Key}='{input}' is not one of {row.RangeText}");

                case SettingKind.Text:
                    if (text.Any(char.IsControl))
                        return Refuse(row, $"{row.Key}: text may not contain control characters");

                    return text.Length > 200
                        ? Refuse(row, $"{row.Key}: text is longer than 200 characters")
                        : new WorldEventSettingCheck { Row = row, Normalized = text };

                case SettingKind.Long:
                    if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
                        return IsWholeNumberText(text)
                            ? Refuse(row, $"{row.Key}='{input}' is out of range")
                            : Refuse(row, $"{row.Key}='{input}' is not a whole number");

                    return CheckNumber(row, l, input);

                default:
                    if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || !double.IsFinite(d))
                        return Refuse(row, $"{row.Key}='{input}' is not a finite number");

                    return CheckNumber(row, d, input);
            }
        }

        /// <summary>True for an optional sign and digits only: a long that failed to parse for that shape overflowed.</summary>
        private static bool IsWholeNumberText(string text)
        {
            var digits = text.Length > 0 && (text[0] == '-' || text[0] == '+') ? text.Substring(1) : text;

            return digits.Length > 0 && digits.All(c => c >= '0' && c <= '9');
        }

        private static WorldEventSettingCheck CheckNumber(WorldEventSettingRow row, double v, string input)
        {
            string Show(double x) => row.Kind == SettingKind.Long ? ((long)x).ToString(CultureInfo.InvariantCulture) : FormatNumber(x);

            if (row.Unset == SettingUnsetRule.NonPositiveIsUnset && v <= 0)
                return new WorldEventSettingCheck
                {
                    Row = row,
                    Normalized = "0",
                    Warning = v < 0 ? $"{row.Key}={Show(v)} is stored as 0 (not set: the JSON or code default is used)" : null
                };

            // NegativeIsUnset: any negative number is the "not set" marker, stored as -1 (the shipped default) and never clamped;
            // 0 and above are real values and clamp to the row's range like any other.
            if (row.Unset == SettingUnsetRule.NegativeIsUnset && v < 0)
                return new WorldEventSettingCheck
                {
                    Row = row,
                    Normalized = "-1",
                    Warning = v != -1 ? $"{row.Key}={Show(v)} is stored as -1 (not set: the JSON or code default is used)" : null
                };

            var clamped = Math.Min(Math.Max(v, row.Min), row.Max);

            if (clamped != v)
                return new WorldEventSettingCheck
                {
                    Row = row,
                    Normalized = Show(clamped),
                    Clamped = true,
                    Warning = $"{row.Key}={Show(v)} is outside {row.RangeText}; clamped to {Show(clamped)}"
                };

            return new WorldEventSettingCheck { Row = row, Normalized = Show(v) };
        }

        internal static string TypeFor(WorldEventSettingRow row)
        {
            switch (row.Kind)
            {
                case SettingKind.Double: return PropertyAdminService.TypeDouble;
                case SettingKind.Long: return PropertyAdminService.TypeLong;
                case SettingKind.Bool: return PropertyAdminService.TypeBool;
                default: return PropertyAdminService.TypeString;
            }
        }
    }
}
