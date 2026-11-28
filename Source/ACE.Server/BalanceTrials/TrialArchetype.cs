using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using ACE.Entity.Enum;
using ACE.Server.ClassAbilities;
using ACE.Server.Entity;

namespace ACE.Server.BalanceTrials
{
    /// <summary>One trial master: which class it stands for, its slot, the level it ends at, how its anchor is chosen and how it is lifted.</summary>
    public sealed record TrialArchetype(string Id, int Slot, ClassAbilityClass Class, int TargetLevel, TrialAnchorRule Anchor, TrialLiftUp LiftUp, string BuffProfile);

    /// <summary>How the anchor character is picked from the prod snapshot.</summary>
    public sealed record TrialAnchorRule(string Rule, ClassAbilityClass Class, int MinLevel, int MaxLevel, int Rank, double MinShare);

    /// <summary>The lift from the anchor up to the archetype: CAP, donor gear, augmentations, facets, consumables.</summary>
    public sealed record TrialLiftUp(
        IReadOnlyList<ClassAbilityClass> Reference,
        TrialCapLift Cap,
        IReadOnlyList<TrialGearSlot> Gear,
        IReadOnlyDictionary<string, int> Augs,
        string Facets,
        IReadOnlyList<TrialConsumable> Consumables);

    /// <summary>CAP lift: spend up to a named percentile of the reference classes, with explicit ranks per ability.</summary>
    public sealed record TrialCapLift(string Percentile, IReadOnlyDictionary<string, int> Ranks, double Tolerance);

    /// <summary>One donor gear slot. Slot is an EquipMask member; Source is "any", "class:&lt;Class&gt;" or "class:&lt;Class&gt;|any" (the pick may fall back to any donor when the class pool is empty), kept as written; Pick is "pNN metric".</summary>
    public sealed record TrialGearSlot(EquipMask Slot, string Source, string Pick);

    public sealed record TrialConsumable(uint Wcid, int Stack);

    /// <summary>Thrown for any archetype file that is malformed or breaks a schema rule.</summary>
    public sealed class TrialArchetypeException : Exception
    {
        public TrialArchetypeException(string message) : base(message) { }
    }

    /// <summary>
    /// The one archetype schema and loader, used by trialseed, verify and Plan C. Closed key set: an unknown key
    /// is an error, so a typo cannot silently drop a rule.
    /// </summary>
    public static class TrialArchetypeLoader
    {
        /// <summary>An anchor window may sit at most this many levels under the target (the lift covers the rest).</summary>
        public const int MaxLevelLift = 50;

        /// <summary>Percentiles the calibration file carries.</summary>
        public static readonly IReadOnlyList<string> Percentiles = new[] { "p25", "p50", "p75", "p90" };

        /// <summary>Per-item gear metrics a donor pick may order by.</summary>
        public static readonly IReadOnlyList<string> GearMetrics = new[] { "armorLevel", "gearRatingSum", "modCount", "aspectSum" };

        private static readonly string[] AnchorRules = { "topByClassSpend" };
        private static readonly string[] FacetModes = { "keep", "drop" };

        public static TrialArchetype Parse(string json)
        {
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false });
            }
            catch (JsonException ex)
            {
                throw new TrialArchetypeException($"not valid JSON: {ex.Message}");
            }

            using (doc)
            {
                var root = Obj(doc.RootElement, "archetype", "id", "slot", "class", "targetLevel", "anchor", "liftUp", "buffProfile");

                var id = Str(root, "id");
                if (id.Length == 0 || !id.All(c => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-'))
                    throw new TrialArchetypeException($"id '{id}' must be lower-case letters, digits and hyphens");

                var slot = Int(root, "slot");
                if (slot < 0 || slot >= TrialRoster.MasterCount)
                    throw new TrialArchetypeException($"slot {slot} is outside 0..{TrialRoster.MasterCount - 1}");

                var cls = Class(Str(root, "class"), "class");
                var target = Int(root, "targetLevel");
                if (target < EnlightenmentXpCurve.BaseMaxLevel)
                    throw new TrialArchetypeException($"targetLevel {target} is below {EnlightenmentXpCurve.BaseMaxLevel}");

                var anchor = ParseAnchor(Get(root, "anchor"), target);
                var liftUp = ParseLiftUp(Get(root, "liftUp"));

                var buff = Str(root, "buffProfile");
                if (!TrialBuffs.IsValidProfile(buff))
                    throw new TrialArchetypeException($"buffProfile '{buff}' must be ownSpellbook or adminAll:<level>");

                return new TrialArchetype(id, slot, cls, target, anchor, liftUp, buff);
            }
        }

        /// <summary>
        /// Loads every *.json in the directory, in ordinal file-name order. Each archetype comes with the sha256 of
        /// the exact file text that was parsed (see <see cref="Sha256(string)"/>), so a caller never hashes a
        /// different read than the one it parsed. The file name must equal the id, which also makes ids unique.
        /// </summary>
        public static IReadOnlyList<(TrialArchetype Archetype, string Sha256)> LoadDirectory(string dir)
        {
            if (!Directory.Exists(dir))
                throw new TrialArchetypeException($"archetype directory '{dir}' does not exist");

            var result = new List<(TrialArchetype Archetype, string Sha256)>();
            foreach (var file in Directory.GetFiles(dir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
            {
                var text = File.ReadAllText(file);
                TrialArchetype a;
                string sha;
                try
                {
                    a = Parse(text);
                    sha = Sha256(text);
                }
                catch (TrialArchetypeException ex)
                {
                    throw new TrialArchetypeException($"{Path.GetFileName(file)}: {ex.Message}");
                }

                if (!string.Equals(Path.GetFileNameWithoutExtension(file), a.Id, StringComparison.Ordinal))
                    throw new TrialArchetypeException($"{Path.GetFileName(file)}: file name must equal id '{a.Id}'");

                var dupSlot = result.FirstOrDefault(r => r.Archetype.Slot == a.Slot);
                if (dupSlot.Archetype != null)
                    throw new TrialArchetypeException($"slot {a.Slot} is used by both '{dupSlot.Archetype.Id}' and '{a.Id}'");

                result.Add((a, sha));
            }

            return result;
        }

        /// <summary>
        /// Lower-case hex sha256 of the canonical form of the JSON: object keys sorted ordinally, no whitespace,
        /// values untouched. Whitespace and key order do not change it; any value does. Numbers hash as written, so 0.5 and 0.50 differ.
        /// The input is the file text itself: nothing is re-serialized from a parsed TrialArchetype.
        /// </summary>
        public static string Sha256(string json)
        {
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(json);
            }
            catch (JsonException ex)
            {
                throw new TrialArchetypeException($"not valid JSON: {ex.Message}");
            }

            using (doc)
            {
                var sb = new StringBuilder();
                Canonical(doc.RootElement, sb);
                var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
                return Convert.ToHexString(hash).ToLowerInvariant();
            }
        }

        private static void Canonical(JsonElement e, StringBuilder sb)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                    sb.Append('{');
                    var first = true;
                    foreach (var p in e.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        sb.Append(JsonSerializer.Serialize(p.Name)).Append(':');
                        Canonical(p.Value, sb);
                    }
                    sb.Append('}');
                    break;
                case JsonValueKind.Array:
                    sb.Append('[');
                    var firstItem = true;
                    foreach (var item in e.EnumerateArray())
                    {
                        if (!firstItem) sb.Append(',');
                        firstItem = false;
                        Canonical(item, sb);
                    }
                    sb.Append(']');
                    break;
                case JsonValueKind.String:
                    sb.Append(JsonSerializer.Serialize(e.GetString()));
                    break;
                default:
                    sb.Append(e.GetRawText());
                    break;
            }
        }

        private static TrialAnchorRule ParseAnchor(JsonElement e, int target)
        {
            var o = Obj(e, "anchor", "rule", "class", "minLevel", "maxLevel", "rank", "minShare");

            var rule = Str(o, "rule");
            if (!AnchorRules.Contains(rule))
                throw new TrialArchetypeException($"anchor.rule '{rule}' must be one of {string.Join(", ", AnchorRules)}");

            var cls = Class(Str(o, "class"), "anchor.class");
            var min = Int(o, "minLevel");
            var max = Int(o, "maxLevel");
            var rank = Int(o, "rank");
            var share = Dbl(o, "minShare");

            if (max > target)
                throw new TrialArchetypeException($"anchor.maxLevel {max} is above targetLevel {target}: an anchor is never levelled down");
            var floor = Math.Max(EnlightenmentXpCurve.BaseMaxLevel, target - MaxLevelLift);
            if (min < floor)
                throw new TrialArchetypeException($"anchor.minLevel {min} is below {floor} (the larger of {EnlightenmentXpCurve.BaseMaxLevel} and targetLevel - {MaxLevelLift})");
            if (min > max)
                throw new TrialArchetypeException($"anchor.minLevel {min} is above anchor.maxLevel {max}");
            if (rank < 1)
                throw new TrialArchetypeException($"anchor.rank {rank} must be at least 1");
            if (share < 0 || share > 1)
                throw new TrialArchetypeException($"anchor.minShare {share} must be within 0..1");

            return new TrialAnchorRule(rule, cls, min, max, rank, share);
        }

        private static TrialLiftUp ParseLiftUp(JsonElement e)
        {
            var o = Obj(e, "liftUp", "reference", "cap", "gear", "augs", "facets", "consumables");

            var reference = Arr(o, "reference").Select((x, i) => Class(StrOf(x, $"liftUp.reference[{i}]"), $"liftUp.reference[{i}]")).ToArray();

            var capObj = Obj(Get(o, "cap"), "liftUp.cap", "percentile", "ranks", "tolerance");
            var percentile = Str(capObj, "percentile");
            if (!Percentiles.Contains(percentile))
                throw new TrialArchetypeException($"liftUp.cap.percentile '{percentile}' must be one of {string.Join(", ", Percentiles)}");
            var ranks = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var p in ObjProps(Get(capObj, "ranks"), "liftUp.cap.ranks"))
            {
                if (!ClassAbilityRegistry.TryGetByName(p.Name, out var def))
                    throw new TrialArchetypeException($"liftUp.cap.ranks names unknown ability '{p.Name}'");
                var rank = IntOf(p.Value, $"liftUp.cap.ranks.{p.Name}");
                if (rank < 0 || rank > def.MaxRank)
                    throw new TrialArchetypeException($"liftUp.cap.ranks.{p.Name} {rank} is outside 0..{def.MaxRank}");
                ranks[def.Name] = rank;
            }
            var tolerance = Dbl(capObj, "tolerance");
            if (tolerance < 0 || tolerance > 1)
                throw new TrialArchetypeException($"liftUp.cap.tolerance {tolerance} must be within 0..1");

            var gear = new List<TrialGearSlot>();
            var seenSlots = new HashSet<EquipMask>();
            foreach (var (g, i) in Arr(o, "gear").Select((g, i) => (g, i)))
            {
                var go = Obj(g, $"liftUp.gear[{i}]", "slot", "source", "pick");
                var slotName = Str(go, "slot");
                if (slotName.Length == 0 || !Regex.IsMatch(slotName, @"^[A-Za-z]+\z") || !Enum.TryParse<EquipMask>(slotName, false, out var slot) || !Enum.IsDefined(typeof(EquipMask), slot)
                    || slot == EquipMask.None || System.Numerics.BitOperations.PopCount((uint)slot) != 1)
                    throw new TrialArchetypeException($"liftUp.gear[{i}].slot '{slotName}' is not a single EquipMask member");
                if (!seenSlots.Add(slot))
                    throw new TrialArchetypeException($"liftUp.gear[{i}].slot '{slotName}' appears twice");

                var source = Str(go, "source");
                if (source != "any")
                {
                    // Grammar: any | class:<Class> | class:<Class>|any (the last lets the pick fall back to any donor).
                    var classPart = source.EndsWith("|any", StringComparison.Ordinal) ? source.Substring(0, source.Length - "|any".Length) : source;
                    if (!classPart.StartsWith("class:", StringComparison.Ordinal))
                        throw new TrialArchetypeException($"liftUp.gear[{i}].source '{source}' must be any, class:<Class> or class:<Class>|any");
                    Class(classPart.Substring("class:".Length), $"liftUp.gear[{i}].source");
                }

                var pick = Str(go, "pick");
                var parts = pick.Split(' ');
                if (parts.Length != 2 || !Percentiles.Contains(parts[0]) || !GearMetrics.Contains(parts[1]))
                    throw new TrialArchetypeException($"liftUp.gear[{i}].pick '{pick}' must be '<{string.Join("|", Percentiles)}> <{string.Join("|", GearMetrics)}>'");

                gear.Add(new TrialGearSlot(slot, source, pick));
            }

            var augs = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var p in ObjProps(Get(o, "augs"), "liftUp.augs"))
            {
                if (!p.Name.StartsWith("Augmentation", StringComparison.Ordinal) && !p.Name.StartsWith("LumAug", StringComparison.Ordinal))
                    throw new TrialArchetypeException($"liftUp.augs.{p.Name} must start with Augmentation or LumAug");
                var v = IntOf(p.Value, $"liftUp.augs.{p.Name}");
                if (v < 0)
                    throw new TrialArchetypeException($"liftUp.augs.{p.Name} {v} must not be negative");
                augs[p.Name] = v;
            }

            var facets = Str(o, "facets");
            if (!FacetModes.Contains(facets))
                throw new TrialArchetypeException($"liftUp.facets '{facets}' must be keep or drop");

            var consumables = new List<TrialConsumable>();
            foreach (var (c, i) in Arr(o, "consumables").Select((c, i) => (c, i)))
            {
                var co = Obj(c, $"liftUp.consumables[{i}]", "wcid", "stack");
                var wcid = Int(co, "wcid");
                var stack = Int(co, "stack");
                if (wcid <= 0)
                    throw new TrialArchetypeException($"liftUp.consumables[{i}].wcid {wcid} must be positive");
                if (stack < 1)
                    throw new TrialArchetypeException($"liftUp.consumables[{i}].stack {stack} must be at least 1");
                consumables.Add(new TrialConsumable((uint)wcid, stack));
            }

            return new TrialLiftUp(reference, new TrialCapLift(percentile, ranks, tolerance), gear, augs, facets, consumables);
        }

        private static ClassAbilityClass Class(string name, string field)
        {
            if (name.Length == 0 || !Regex.IsMatch(name, @"^[A-Za-z]+\z") || !Enum.TryParse<ClassAbilityClass>(name, false, out var c) || !Enum.IsDefined(typeof(ClassAbilityClass), c) || c == ClassAbilityClass.None)
                throw new TrialArchetypeException($"{field} '{name}' is not a class");
            return c;
        }

        // ---- closed-key-set JSON helpers ----

        // Thin key-based adapters over TrialJson, the one copy of the closed-key-set JSON helpers.

        private static Dictionary<string, JsonElement> Obj(JsonElement e, string what, params string[] keys) => TrialJson.Obj(e, what, keys);

        private static IEnumerable<JsonProperty> ObjProps(JsonElement e, string what) => TrialJson.Props(e, what);

        private static JsonElement Get(Dictionary<string, JsonElement> o, string key) => o[key];

        private static IEnumerable<JsonElement> Arr(Dictionary<string, JsonElement> o, string key) => TrialJson.Arr(o[key], key);

        private static string Str(Dictionary<string, JsonElement> o, string key) => TrialJson.Str(o[key], key);

        private static string StrOf(JsonElement e, string what) => TrialJson.Str(e, what);

        private static int Int(Dictionary<string, JsonElement> o, string key) => TrialJson.Int(o[key], key);

        private static int IntOf(JsonElement e, string what) => TrialJson.Int(e, what);

        private static double Dbl(Dictionary<string, JsonElement> o, string key) => TrialJson.Finite(o[key], key);
    }
}
