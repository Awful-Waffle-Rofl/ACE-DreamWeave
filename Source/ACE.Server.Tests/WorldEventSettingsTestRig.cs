using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

using ACE.Server.WorldEvents;

namespace ACE.Server.Tests
{
    /// <summary>
    /// An in-memory stand-in for PropertyManager behind a WorldEventSettingsService, plus a registry with global and goal-scoped
    /// rows of every kind. PropertyManager reads throw in a unit test, so every settings test goes through this rig.
    /// </summary>
    internal sealed class WorldEventSettingsTestRig : IDisposable
    {
        public static readonly SettingScope Hold = SettingScope.Goal("hold");

        public readonly Dictionary<string, string> Values = new Dictionary<string, string>();
        public readonly Dictionary<string, string> Json = new Dictionary<string, string>();
        public readonly List<(string Type, string Property, string Text, bool HadSession)> Writes = new List<(string, string, string, bool)>();
        public readonly List<string> Audits = new List<string>();
        public readonly Dictionary<string, string> Defaults = new Dictionary<string, string>();
        public readonly string Folder = Path.Combine(Path.GetTempPath(), "we-settings-" + Guid.NewGuid().ToString("N"));
        public string FolderOverride;
        public bool UseFolderOverride;
        public string FailWriteFor;

        public WorldEventSettingsRegistry Registry { get; }
        public WorldEventSettingsService Service { get; }

        public WorldEventSettingsTestRig(IEnumerable<WorldEventSettingRow> rows = null)
        {
            Registry = new WorldEventSettingsRegistry(rows ?? Rows());

            foreach (var r in Registry.Rows)
                Defaults[r.PropertyName] = r.Kind == SettingKind.Choice || r.Kind == SettingKind.Text ? "" : r.CodeDefault;

            Service = new WorldEventSettingsService(Registry, new WorldEventSettingsIo
            {
                ReadDouble = p => double.Parse(Text(p), NumberStyles.Float, CultureInfo.InvariantCulture),
                ReadLong = p => long.Parse(Text(p), CultureInfo.InvariantCulture),
                ReadBool = p => bool.Parse(Text(p)),
                ReadString = p => Text(p),
                DefaultText = p => Defaults.TryGetValue(p, out var d) ? d : null,
                JsonValue = path => Json.TryGetValue(path, out var j) ? j : null,
                Write = (type, p, text, session) =>
                {
                    if (FailWriteFor == p)
                        return "forced failure";

                    Writes.Add((type, p, text, session != null));
                    Values[p] = text;
                    return null;
                },
                PresetFolder = () => UseFolderOverride ? FolderOverride : Folder,
                Audit = (_, text) => Audits.Add(text)
            });
        }

        private string Text(string property) => Values.TryGetValue(property, out var v) ? v : Defaults[property];

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Folder))
                    Directory.Delete(Folder, true);
            }
            catch (IOException)
            {
                // A leftover temp folder is harmless.
            }
        }

        public bool WroteProperty(string property)
        {
            foreach (var w in Writes)
                if (w.Property == property)
                    return true;

            return false;
        }

        private static WorldEventSettingRow R(string key, SettingKind kind, SettingScope scope, string group, string def, double min, double max,
            SettingUnsetRule unset, string json = null, string[] choices = null) =>
            new WorldEventSettingRow
            {
                Key = key, PropertyName = "t_" + key, Kind = kind, Scope = scope, Group = group, CodeDefault = def, Min = min, Max = max,
                Unset = unset, JsonPath = json, Choices = choices, Applies = SettingApplies.Live, Description = key
            };

        /// <summary>Global rows g_* and Hold rows h_*; group "mixed" holds one of each scope.</summary>
        public static List<WorldEventSettingRow> Rows()
        {
            var np = SettingUnsetRule.NonPositiveIsUnset;
            var always = SettingUnsetRule.AlwaysSet;
            var empty = SettingUnsetRule.EmptyIsUnset;
            var g = SettingScope.Global;

            return new List<WorldEventSettingRow>
            {
                R("g_long", SettingKind.Long, g, "alpha", "0", 0, 200, np, json: "waves.maxAlive"),
                R("g_zero", SettingKind.Long, g, "alpha", "60", 0, 3600, always),
                R("g_dbl", SettingKind.Double, g, "alpha", "0", 0, 10, np),
                R("g_bool", SettingKind.Bool, g, "alpha", "True", 0, 1, always),
                R("g_mode", SettingKind.Choice, g, "mixed", "", 0, 0, empty, choices: new[] { "x", "y" }),
                R("h_dbl", SettingKind.Double, Hold, "beta", "0.5", 0, 1, np),
                R("h_long", SettingKind.Long, Hold, "beta", "3", -5, 100, always),
                R("h_bool", SettingKind.Bool, Hold, "beta", "False", 0, 1, always),
                R("h_pick", SettingKind.Choice, Hold, "mixed", "", 0, 0, empty, choices: new[] { "a", "b", "c" }),
                R("h_text", SettingKind.Text, Hold, "beta", "", 0, 0, empty)
            };
        }
    }
}
