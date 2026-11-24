using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

using ACE.Server.ThreadDungeons.Defs;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// The validated contents of Content/dungeons/dynamic/encounters.json (Docs/Threads/ENCOUNTERS.md). Immutable,
    /// built by <see cref="Parse"/> inside ThreadDungeonStore.Parse, so it is re-read by every /dd reload with the
    /// rest of the Thread data.
    ///
    /// Fail-open on the puzzle-gates.json rule: a missing file is silently empty (the file is optional), a malformed
    /// or unsupported-version file is one diagnostic and empty, and a bad type or dungeon row costs only itself.
    /// </summary>
    public sealed class ThreadEncounterStore
    {
        public const string FileName = "encounters.json";

        public const int SupportedVersion = 1;

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        public static readonly ThreadEncounterStore Empty = new ThreadEncounterStore(new List<ThreadEncounterTypeDef>(), new Dictionary<string, string>(StringComparer.Ordinal), ThreadEncounterPrizeTable.Empty, null);

        /// <summary>Every valid type row, disabled ones included, in file order.</summary>
        public IReadOnlyList<ThreadEncounterTypeDef> Types { get; }

        /// <summary>Dungeon ids whose row says disabled, with the authored reason (may be empty).</summary>
        public IReadOnlyDictionary<string, string> DisabledDungeons { get; }

        /// <summary>The shared encounter prize table (the file's "prize" section; ThreadEncounterPrize). Empty when the section is absent or unusable.</summary>
        public ThreadEncounterPrizeTable Prize { get; }

        /// <summary>
        /// The file's "countWeights" (index = encounters a run tries to place), or null when absent or unusable, in which
        /// case the per-slot slot_chance roll is used. See <see cref="ParseCountWeights"/>.
        /// </summary>
        public IReadOnlyList<double> CountWeights { get; }

        private ThreadEncounterStore(List<ThreadEncounterTypeDef> types, Dictionary<string, string> disabled, ThreadEncounterPrizeTable prize, IReadOnlyList<double> countWeights)
        {
            Types = types;
            DisabledDungeons = disabled;
            Prize = prize ?? ThreadEncounterPrizeTable.Empty;
            CountWeights = countWeights;
        }

        /// <summary>
        /// encounters.json "countWeights" -> the table, or null. Absent (undefined or null) is null with no diagnostic. A
        /// present value must be an array of 1 to MaxPerRunCeiling + 1 finite numbers, each &gt;= 0, at least one &gt; 0;
        /// anything else is one diagnostic and null, so the run falls back to the slot_chance roll. Pure.
        /// </summary>
        public static IReadOnlyList<double> ParseCountWeights(JsonElement value, List<string> diagnostics)
        {
            diagnostics ??= new List<string>();

            if (value.ValueKind == JsonValueKind.Undefined || value.ValueKind == JsonValueKind.Null)
                return null;

            IReadOnlyList<double> Refuse(string why)
            {
                diagnostics.Add($"{FileName}: countWeights {why}; ignored, the slot_chance roll is used");
                return null;
            }

            if (value.ValueKind != JsonValueKind.Array)
                return Refuse($"must be an array of numbers, not {value.ValueKind}");

            var weights = new List<double>();

            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Number || !item.TryGetDouble(out var w))
                {
                    return Refuse($"[{weights.Count}] must be a number");
                }

                if (double.IsNaN(w) || double.IsInfinity(w) || w < 0)
                {
                    return Refuse($"[{weights.Count}] {w.ToString(System.Globalization.CultureInfo.InvariantCulture)} must be a finite number >= 0");
                }

                weights.Add(w);
            }

            if (weights.Count == 0 || weights.Count > ThreadEncounterPicker.MaxPerRunCeiling + 1)
            {
                return Refuse($"must list 1 to {ThreadEncounterPicker.MaxPerRunCeiling + 1} weights (counts 0 to {ThreadEncounterPicker.MaxPerRunCeiling}), not {weights.Count}");
            }

            if (!weights.Any(w => w > 0))
            {
                return Refuse("must have at least one weight > 0");
            }

            return weights;
        }

        /// <summary>Is the dungeon switched off for encounters? <paramref name="reason"/> is the authored reason.</summary>
        public bool IsDisabledFor(string dungeonId, out string reason)
        {
            reason = null;
            return dungeonId != null && DisabledDungeons.TryGetValue(dungeonId, out reason);
        }

        /// <summary>One type row by id, or null.</summary>
        public ThreadEncounterTypeDef FindType(string id)
            => id == null ? null : Types.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.Ordinal));

        /// <summary>
        /// encounters.json -> the store. <paramref name="dungeons"/> is the parsed index (a dungeon row naming an
        /// unknown dungeon is skipped with a diagnostic). Null or whitespace text is "no file": empty, no diagnostic.
        /// </summary>
        public static ThreadEncounterStore Parse(string json, IReadOnlyDictionary<string, DungeonEntryDef> dungeons, List<string> diagnostics)
        {
            diagnostics ??= new List<string>();

            if (string.IsNullOrWhiteSpace(json))
                return Empty;

            ThreadEncounterFileDef file;

            try
            {
                file = JsonSerializer.Deserialize<ThreadEncounterFileDef>(json, JsonOptions);
            }
            catch (JsonException ex)
            {
                diagnostics.Add($"{FileName}: {ex.Message}; file ignored");
                return Empty;
            }

            if (file == null)
            {
                diagnostics.Add($"{FileName}: null document; file ignored");
                return Empty;
            }

            if (file.Version != SupportedVersion)
            {
                diagnostics.Add($"{FileName}: unsupported version {file.Version} (this server reads version {SupportedVersion}); file ignored");
                return Empty;
            }

            var types = new List<ThreadEncounterTypeDef>();
            var ids = new HashSet<string>(StringComparer.Ordinal);

            var rows = file.Types ?? new List<JsonElement>();

            for (var i = 0; i < rows.Count; i++)
            {
                var label = $"type[{i}]";
                ThreadEncounterTypeDef def;

                try
                {
                    def = rows[i].ValueKind == JsonValueKind.Object ? rows[i].Deserialize<ThreadEncounterTypeDef>(JsonOptions) : null;
                }
                catch (Exception ex) when (ex is JsonException || ex is InvalidOperationException || ex is FormatException)
                {
                    diagnostics.Add($"{FileName}: {label} is malformed - {ex.Message}; skipped");
                    continue;
                }

                if (def == null)
                {
                    diagnostics.Add($"{FileName}: {label} is not an object; skipped");
                    continue;
                }

                if (def.Id != null)
                    label = $"type '{def.Id}'";

                var problem = Validate(def);

                if (problem != null)
                {
                    diagnostics.Add($"{FileName}: {label}: {problem}; skipped");
                    continue;
                }

                if (!ids.Add(def.Id))
                {
                    diagnostics.Add($"{FileName}: duplicate type id '{def.Id}'; the first stands");
                    continue;
                }

                // A detached copy: the element above belongs to the deserialized file and the store outlives it.
                def.Config = def.Config.ValueKind == JsonValueKind.Undefined ? default : def.Config.Clone();
                types.Add(def);
            }

            var disabled = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var kv in file.Dungeons ?? new Dictionary<string, JsonElement>())
            {
                if (dungeons != null && !dungeons.ContainsKey(kv.Key))
                {
                    diagnostics.Add($"{FileName}: dungeon '{kv.Key}' is not in index.json; its row is skipped");
                    continue;
                }

                ThreadEncounterDungeonDef row;

                try
                {
                    row = kv.Value.ValueKind == JsonValueKind.Object ? kv.Value.Deserialize<ThreadEncounterDungeonDef>(JsonOptions) : null;
                }
                catch (Exception ex) when (ex is JsonException || ex is InvalidOperationException || ex is FormatException)
                {
                    diagnostics.Add($"{FileName}: dungeon '{kv.Key}' is malformed - {ex.Message}; skipped");
                    continue;
                }

                if (row == null)
                {
                    diagnostics.Add($"{FileName}: dungeon '{kv.Key}' is not an object; skipped");
                    continue;
                }

                if (row.Disabled)
                    disabled[kv.Key] = row.Reason ?? "";
            }

            var prize = ThreadEncounterPrizeTable.Parse(file.Prize, FileName, diagnostics);
            var countWeights = ParseCountWeights(file.CountWeights, diagnostics);

            return new ThreadEncounterStore(types, disabled, prize, countWeights);
        }

        /// <summary>Why a type row is unusable, or null when it is valid. Pure.</summary>
        internal static string Validate(ThreadEncounterTypeDef def)
        {
            if (string.IsNullOrEmpty(def.Id) || !def.Id.All(c => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_'))
                return $"id '{def.Id}' must match [a-z0-9_]+";

            if (string.IsNullOrWhiteSpace(def.Kind))
                return "kind is missing";

            if (double.IsNaN(def.Weight) || double.IsInfinity(def.Weight) || def.Weight < 0)
                return $"weight {def.Weight.ToString(CultureInfo.InvariantCulture)} must be a finite number >= 0";

            if (def.MaxPerRun < 1)
                return $"maxPerRun {def.MaxPerRun.Value.ToString(CultureInfo.InvariantCulture)} must be at least 1 (leave it out for no cap)";

            var p = def.Placement ??= new ThreadEncounterPlacementDef();

            foreach (var (name, value) in new[] { ("minClearance", p.MinClearance), ("minEntryWalk", p.MinEntryWalk), ("minBossDist", p.MinBossDist), ("minSpacing", p.MinSpacing) })
            {
                if (float.IsNaN(value) || float.IsInfinity(value) || value < 0)
                    return $"placement.{name} {value.ToString(CultureInfo.InvariantCulture)} must be a finite number >= 0";
            }

            if (p.MinDepth < 0 || p.MaxDepth < 0)
                return "placement depth bounds must be >= 0";

            if (p.MinDepth.HasValue && p.MaxDepth.HasValue && p.MinDepth.Value > p.MaxDepth.Value)
                return $"placement.minDepth {p.MinDepth} exceeds maxDepth {p.MaxDepth}";

            p.RunKinds ??= new List<string>();

            foreach (var kind in p.RunKinds)
            {
                if (kind != DungeonEntryDef.KindDungeon && kind != DungeonEntryDef.KindOutdoor)
                    return $"placement.runKinds entry '{kind}' must be dungeon or outdoor";
            }

            return null;
        }
    }
}
