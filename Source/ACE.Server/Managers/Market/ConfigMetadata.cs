using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace ACE.Server.Managers.Market
{
    /// <summary>One config-metadata.tsv row: which tab/group a PropertyManager key is shown under, where it came from, and whether its value may ever reach the wire.</summary>
    public sealed class ConfigMetadataRow
    {
        public string Key;
        public string Tab;
        public string Group;
        public string Origin;
        public bool Sensitive;
    }

    /// <summary>
    /// The parsed Source/ACE.Server/config-metadata.tsv - the single source of truth for how the web
    /// admin panel's Settings tab groups and labels every PropertyManager key (PLAN-P1.md section 3).
    /// The generating logic (the `configmetadata` ACE.Content.Tools command) lives in a separate
    /// project so ACE.Server never depends on ACE.Content.Tools; this class only parses and serves the
    /// checked-in file.
    /// </summary>
    public sealed class ConfigMetadata
    {
        public const string EmbeddedResourceName = "ACE.Server.config-metadata.tsv";
        public const string HeaderLine = "key\ttab\tgroup\torigin\tsensitive";

        /// <summary>Every tab, id and label, in DISPLAY order (PLAN-P1.md section 3's "Display order").</summary>
        public static readonly IReadOnlyList<(string Id, string Label)> Tabs = new List<(string, string)>
        {
            ("server", "Server"),
            ("social", "Social"),
            ("combat", "Combat"),
            ("pvp", "PvP"),
            ("items", "Loot and Items"),
            ("economy", "Economy"),
            ("progression", "Progression"),
            ("class_abilities", "Class Abilities"),
            ("threads", "Threads"),
            ("world_events", "World Events"),
        };

        private readonly Dictionary<string, ConfigMetadataRow> rowsByKey;

        private ConfigMetadata(Dictionary<string, ConfigMetadataRow> rowsByKey)
        {
            this.rowsByKey = rowsByKey;
        }

        public bool TryGet(string key, out ConfigMetadataRow row) => rowsByKey.TryGetValue(key, out row);

        public IReadOnlyDictionary<string, ConfigMetadataRow> RowsByKey => rowsByKey;

        /// <summary>Loads Source/ACE.Server/config-metadata.tsv from the assembly's embedded resources (ACE.Server.csproj: LogicalName="ACE.Server.config-metadata.tsv").</summary>
        public static ConfigMetadata LoadEmbedded()
        {
            var assembly = typeof(ConfigMetadata).Assembly;

            using var stream = assembly.GetManifestResourceStream(EmbeddedResourceName);

            if (stream == null)
                throw new InvalidOperationException($"ConfigMetadata.LoadEmbedded: embedded resource '{EmbeddedResourceName}' was not found in {assembly.FullName}.");

            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
            return Parse(reader.ReadToEnd());
        }

        /// <summary>Parses a config-metadata.tsv document. Tolerates CRLF line endings on read (the working tree may normalize them) even though the checked-in file is written LF-only.</summary>
        public static ConfigMetadata Parse(string text)
        {
            var rows = new Dictionary<string, ConfigMetadataRow>(StringComparer.Ordinal);

            if (string.IsNullOrEmpty(text))
                return new ConfigMetadata(rows);

            var normalized = text.Replace("\r\n", "\n");
            var lines = normalized.Split('\n');

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];

                if (i == 0 || line.Length == 0)
                    continue;

                var fields = line.Split('\t');
                if (fields.Length < 5)
                    throw new FormatException($"config-metadata.tsv: line {i + 1} has {fields.Length} field(s), expected 5.");

                rows[fields[0]] = new ConfigMetadataRow
                {
                    Key = fields[0],
                    Tab = fields[1],
                    Group = fields[2],
                    Origin = fields[3],
                    Sensitive = fields[4] == "yes",
                };
            }

            return new ConfigMetadata(rows);
        }
    }
}
