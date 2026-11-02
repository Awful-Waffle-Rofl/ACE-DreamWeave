using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

using ACE.Server.ThreadDungeons;

namespace ACE.Server.Knots
{
    /// <summary>
    /// Parses and lints Content/dungeons/dynamic/pinnacles.json for ThreadDungeonStore. Fail-open exactly like
    /// the store's clearance.json and puzzle-gates.json readers: a missing file, malformed JSON, an unsupported
    /// version or an invalid row costs pinnacles, never the store, and nothing here throws. A row that fails
    /// lint is dropped with one diagnostic naming it; a disabled but valid row is kept (see
    /// <see cref="ThreadDungeonStore.EnabledPinnacles"/>).
    /// </summary>
    public static class PinnacleRegistry
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        public static List<PinnacleDef> Parse(string json, List<string> diagnostics)
        {
            var result = new List<PinnacleDef>();

            if (string.IsNullOrWhiteSpace(json))
                return result;

            PinnaclesFileDef file;
            try { file = JsonSerializer.Deserialize<PinnaclesFileDef>(json, JsonOptions); }
            catch (JsonException ex) { diagnostics.Add($"pinnacles.json: {ex.Message}"); return result; }

            if (file == null)
                return result;

            if (file.Version != ThreadDungeonStore.SupportedVersion)
            {
                diagnostics.Add($"pinnacles.json: unsupported version {file.Version} (this server reads version {ThreadDungeonStore.SupportedVersion}); file ignored");
                return result;
            }

            if (file.Pinnacles == null)
                return result;

            var seen = new HashSet<string>(StringComparer.Ordinal);

            for (var i = 0; i < file.Pinnacles.Count; i++)
            {
                PinnacleDef def;
                try { def = file.Pinnacles[i].Deserialize<PinnacleDef>(JsonOptions); }
                catch (Exception ex) when (ex is JsonException || ex is InvalidOperationException)
                { diagnostics.Add($"pinnacles.json: row {i} is malformed - {ex.Message}; skipped"); continue; }

                if (def == null)
                { diagnostics.Add($"pinnacles.json: row {i} is null; skipped"); continue; }

                var label = string.IsNullOrWhiteSpace(def.Key) ? $"row {i}" : $"pinnacle '{def.Key}'";
                var problem = Lint(def);

                if (problem != null)
                { diagnostics.Add($"pinnacles.json: {label}: {problem}; skipped"); continue; }

                if (!seen.Add(def.Key))
                { diagnostics.Add($"pinnacles.json: duplicate pinnacle key '{def.Key}'; later row skipped"); continue; }

                result.Add(def);
            }

            return result;
        }

        /// <summary>The first lint problem with one row, or null when it is valid.</summary>
        public static string Lint(PinnacleDef d)
        {
            if (string.IsNullOrWhiteSpace(d.Key)) return "empty key";
            if (d.Key != d.Key.Trim()) return "key has leading or trailing whitespace";
            if (string.IsNullOrWhiteSpace(d.Name)) return "empty name";
            if (d.Destination == null || string.IsNullOrWhiteSpace(d.Destination.Kind)) return "destination.kind is missing";
            if (d.Entries < 1) return $"entries {d.Entries} must be at least 1";
            if (d.PartyMax != 1) return $"partyMax {d.PartyMax} must be 1 (group Knots are not built yet)";

            if (d.Phases == null || d.Phases.Count == 0) return "phases is empty";
            if (d.Phases[0] != 1.0) return $"phases must start at 1.0, starts at {d.Phases[0]}";
            for (var i = 0; i < d.Phases.Count; i++)
            {
                var p = d.Phases[i];
                if (double.IsNaN(p) || p <= 0.0 || p > 1.0) return $"phase {i} value {p} is outside (0, 1]";
                if (i > 0 && p >= d.Phases[i - 1]) return $"phases must strictly descend, but phase {i} ({p}) is not below phase {i - 1} ({d.Phases[i - 1]})";
            }

            return LintBand(d.RollBand);
        }

        private static string LintBand(RollBandDef b)
        {
            if (b == null) return "rollBand is null";
            if (b.LowBand == null || b.LowBand.Length != 2) return "rollBand.lowBand must have exactly two numbers";
            if (b.HighBand == null || b.HighBand.Length != 2) return "rollBand.highBand must have exactly two numbers";
            if (b.LowLevel < 1 || b.HighLevel <= b.LowLevel) return $"rollBand levels must satisfy 1 <= lowLevel < highLevel (got {b.LowLevel}, {b.HighLevel})";

            foreach (var v in b.LowBand.Concat(b.HighBand).Concat(new[] { b.ClampLow }))
                if (v < 0 || v > 100) return $"rollBand number {v} is outside 0-100";

            if (b.LowBand[0] > b.LowBand[1]) return "rollBand.lowBand low exceeds high";
            if (b.HighBand[0] > b.HighBand[1]) return "rollBand.highBand low exceeds high";
            if (b.HighBand[0] < b.LowBand[0] || b.HighBand[1] < b.LowBand[1]) return "rollBand.highBand must not sit below lowBand";
            if (b.ClampLow < b.LowBand[0]) return $"rollBand.clampLow {b.ClampLow} is below lowBand's low edge {b.LowBand[0]}";

            return null;
        }
    }
}
