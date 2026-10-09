using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Database;
using ACE.Server.Pvp.Battlegrounds;

namespace ACE.Server.Pvp.Templates
{
    /// <summary>One pvp_template row as the coordinator holds it: the stored fields plus its parsed definition.</summary>
    public sealed class PvpTemplateCatalogEntry
    {
        public string Key { get; init; }

        public string DisplayName { get; init; }

        public uint Version { get; init; }

        public bool Enabled { get; init; }

        /// <summary>The mode keys ("1v1", "2v2", "ffa", "bg_koth") the row offers the template in, in canonical order.</summary>
        public IReadOnlyList<string> Modes { get; init; } = Array.Empty<string>();

        /// <summary>The parsed definition, or null when the stored JSON did not parse (<see cref="ParseError"/> says why).</summary>
        public PvpTemplateDefinition Definition { get; init; }

        public string ParseError { get; init; }

        /// <summary>The name shown beside a player: the display name, falling back to the key.</summary>
        public string Label => string.IsNullOrWhiteSpace(DisplayName) ? Key : DisplayName;

        /// <summary>Enabled, parsed, and offered for <paramref name="modeKey"/>.</summary>
        public bool IsOfferedFor(string modeKey) => Enabled && Definition != null && modeKey != null && Modes.Contains(modeKey, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The coordinator's in-memory view of every pvp_template row (Docs/Pvp/TEMPLATES.md "Templates"). Pure: built
    /// from the plain records the shard read returns, parsed once with PvpTemplateSnapshotService.TryParse, and
    /// replaced wholesale on every reload, so a reader never sees a half-built catalog. Lookups are by key, ordinal and
    /// case-insensitive (keys are stored lowercase; a player typing "Duelist" still finds "duelist").
    /// </summary>
    public sealed class PvpTemplateCatalog
    {
        /// <summary>
        /// Every match mode a template can be offered in, in display order: DERIVED from the mode catalogs (the arena
        /// modes, then the battleground modes) and filtered to the templated ones, so a new mode is offerable the moment
        /// it is defined, unless it opts out (PvpModeDefinition.Templated false). Owner ruling 2026-10-03: templates are
        /// mandatory for every mode by default.
        /// </summary>
        public static readonly IReadOnlyList<string> ModeKeys =
            PvpModes.All(PvpTunables.Defaults).Concat(BattlegroundModes.All(BattlegroundTunables.Defaults))
                .Where(m => m.Templated).Select(m => m.ModeKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        /// <summary>
        /// A mode token as an admin or a stored column writes it, as its canonical mode key: a key itself, or the short
        /// form of a battleground key ("koth" for "bg_koth"). Null when it names no templated mode.
        /// </summary>
        public static string CanonicalModeKey(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                return null;

            var t = ArenaMapCatalog.CanonicalModeWord(token);

            return ModeKeys.FirstOrDefault(k => k == t) ?? ModeKeys.FirstOrDefault(k => k == "bg_" + t);
        }

        public static readonly PvpTemplateCatalog Empty = new PvpTemplateCatalog(new Dictionary<string, PvpTemplateCatalogEntry>(StringComparer.OrdinalIgnoreCase));

        private readonly Dictionary<string, PvpTemplateCatalogEntry> entries;

        private PvpTemplateCatalog(Dictionary<string, PvpTemplateCatalogEntry> entries)
        {
            this.entries = entries;
        }

        public int Count => entries.Count;

        /// <summary>Every row, enabled or not, ordered by key.</summary>
        public IReadOnlyList<PvpTemplateCatalogEntry> All => entries.Values.OrderBy(e => e.Key, StringComparer.Ordinal).ToList();

        /// <summary>
        /// Builds a catalog from the stored rows. A row whose JSON does not parse is kept (so /pvptemplate list can show
        /// it) but is never offered; <paramref name="warn"/> receives one line per such row.
        /// </summary>
        public static PvpTemplateCatalog Build(IEnumerable<PvpTemplateRecord> rows, Action<string> warn = null)
        {
            var map = new Dictionary<string, PvpTemplateCatalogEntry>(StringComparer.OrdinalIgnoreCase);

            foreach (var row in rows ?? Enumerable.Empty<PvpTemplateRecord>())
            {
                if (row == null || string.IsNullOrEmpty(row.TemplateKey))
                    continue;

                PvpTemplateDefinition definition = null;
                string error = null;

                try
                {
                    if (!PvpTemplateSnapshotService.TryParse(row, out definition, out error))
                        definition = null;
                }
                catch (Exception ex)
                {
                    definition = null;
                    error = ex.Message;
                }

                if (definition == null)
                    warn?.Invoke($"template {row.TemplateKey} v{row.Version} does not parse and is not offered: {error}");

                map[row.TemplateKey] = new PvpTemplateCatalogEntry
                {
                    Key = row.TemplateKey,
                    DisplayName = row.DisplayName,
                    Version = row.Version,
                    Enabled = row.Enabled,
                    Modes = ParseModes(row.Modes),
                    Definition = definition,
                    ParseError = definition == null ? (error ?? "unreadable") : null,
                };
            }

            return new PvpTemplateCatalog(map);
        }

        public PvpTemplateCatalogEntry Get(string key) => key != null && entries.TryGetValue(key.Trim(), out var e) ? e : null;

        /// <summary>The entry for <paramref name="key"/> when it is enabled, parsed and offered for <paramref name="modeKey"/>.</summary>
        public bool TryGetOffered(string key, string modeKey, out PvpTemplateCatalogEntry entry)
        {
            entry = Get(key);

            if (entry != null && entry.IsOfferedFor(modeKey))
                return true;

            entry = null;
            return false;
        }

        /// <summary>Every template offered in at least one mode (or in <paramref name="modeKey"/> when given), ordered by key.</summary>
        public IReadOnlyList<PvpTemplateCatalogEntry> Offered(string modeKey = null) =>
            All.Where(e => modeKey == null ? ModeKeys.Any(e.IsOfferedFor) : e.IsOfferedFor(modeKey)).ToList();

        /// <summary>The label for <paramref name="key"/>: its display name when known, else the key itself.</summary>
        public string LabelFor(string key) => Get(key)?.Label ?? key;

        /// <summary>
        /// The stored modes column ("1v1,2v2,ffa") as canonical mode keys. Unknown tokens are dropped, duplicates
        /// collapse, case and whitespace are ignored, and the result is in <see cref="ModeKeys"/> order.
        /// </summary>
        public static IReadOnlyList<string> ParseModes(string csv)
        {
            if (string.IsNullOrWhiteSpace(csv))
                return Array.Empty<string>();

            var tokens = csv.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(CanonicalModeKey).Where(k => k != null).ToHashSet();

            return ModeKeys.Where(tokens.Contains).ToList();
        }

        /// <summary>
        /// The admin form of the modes argument: every token must name a templated mode ("1v1", "2v2", "tugak" (or its alias "ffa"), "koth" or
        /// "bg_koth"), or "none" alone to offer the template nowhere. Returns the canonical column value (full mode keys),
        /// or null with <paramref name="error"/> set.
        /// </summary>
        public static string NormalizeModes(string input, out string error)
        {
            error = null;

            if (string.IsNullOrWhiteSpace(input))
            {
                error = $"Give the modes as a comma-separated list of {ModeList()}, or none.";
                return null;
            }

            var tokens = input.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim().ToLowerInvariant()).ToList();

            if (tokens.Count == 1 && tokens[0] == "none")
                return string.Empty;

            var unknown = tokens.Where(t => CanonicalModeKey(t) == null).ToList();

            if (unknown.Count > 0)
            {
                error = $"Unknown mode(s): {string.Join(", ", unknown)}. Use {ModeList()}, or none.";
                return null;
            }

            var canonical = tokens.Select(CanonicalModeKey).ToHashSet();

            return string.Join(",", ModeKeys.Where(canonical.Contains));
        }

        /// <summary>Mode keys (or a stored/canonical column such as "1v1,ffa") as an admin sees them in output: FFA as "tugak", every other key as is, comma-separated, or "none" when empty.</summary>
        public static string ModesDisplay(IEnumerable<string> keys)
        {
            var list = (keys ?? Enumerable.Empty<string>()).Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => ArenaMapCatalog.JoinWord(k.Trim())).ToList();

            return list.Count > 0 ? string.Join(",", list) : "none";
        }

        /// <summary><see cref="ModesDisplay(IEnumerable{string})"/> for a comma-separated column value.</summary>
        public static string ModesDisplay(string csv) => ModesDisplay((csv ?? string.Empty).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));

        /// <summary>The templated modes as an admin types them: arena keys as they are (FFA as "tugak"), battleground keys in their short form.</summary>
        public static string ModeList() => string.Join(", ", ModeKeys.Select(k => k.StartsWith("bg_", StringComparison.Ordinal) ? k.Substring(3) : ArenaMapCatalog.JoinWord(k)));
    }
}
