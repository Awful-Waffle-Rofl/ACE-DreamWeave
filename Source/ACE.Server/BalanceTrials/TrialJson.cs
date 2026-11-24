using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ACE.Server.BalanceTrials
{
    /// <summary>Closed-key-set JSON helpers shared by the scenario and room loaders. Every refusal is a <see cref="TrialArchetypeException"/>.</summary>
    internal static class TrialJson
    {
        public static JsonDocument ParseDocument(string json)
        {
            try
            {
                return JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false });
            }
            catch (JsonException ex)
            {
                throw new TrialArchetypeException($"not valid JSON: {ex.Message}");
            }
        }

        /// <summary>Reads an object whose keys must all be in <paramref name="required"/> or <paramref name="optional"/>, with every required key present.</summary>
        public static Dictionary<string, JsonElement> Obj(JsonElement e, string what, string[] required, params string[] optional)
        {
            var d = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var p in Props(e, what))
            {
                if (!required.Contains(p.Name) && !optional.Contains(p.Name))
                    throw new TrialArchetypeException($"{what} has unknown key '{p.Name}'");
                d[p.Name] = p.Value;
            }

            foreach (var k in required)
                if (!d.ContainsKey(k))
                    throw new TrialArchetypeException($"{what} is missing '{k}'");
            return d;
        }

        /// <summary>Enumerates an object's properties, refusing a non-object and a repeated key.</summary>
        public static IEnumerable<JsonProperty> Props(JsonElement e, string what)
        {
            if (e.ValueKind != JsonValueKind.Object)
                throw new TrialArchetypeException($"{what} must be an object");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in e.EnumerateObject())
            {
                if (!seen.Add(p.Name))
                    throw new TrialArchetypeException($"{what} repeats key '{p.Name}'");
                yield return p;
            }
        }

        public static IReadOnlyList<JsonElement> Arr(JsonElement e, string what)
        {
            if (e.ValueKind != JsonValueKind.Array)
                throw new TrialArchetypeException($"'{what}' must be an array");
            return e.EnumerateArray().ToArray();
        }

        public static string Str(JsonElement e, string what)
        {
            if (e.ValueKind != JsonValueKind.String)
                throw new TrialArchetypeException($"'{what}' must be a string");
            return e.GetString();
        }

        public static int Int(JsonElement e, string what)
        {
            if (e.ValueKind != JsonValueKind.Number || !e.TryGetInt32(out var v))
                throw new TrialArchetypeException($"'{what}' must be an integer");
            return v;
        }

        /// <summary>A finite number (the message says only 'a number', as the archetype loader always did); a value that overflows a double is refused whether the parser reports false or Infinity.</summary>
        public static double Finite(JsonElement e, string what)
        {
            if (e.ValueKind != JsonValueKind.Number || !e.TryGetDouble(out var v) || double.IsNaN(v) || double.IsInfinity(v))
                throw new TrialArchetypeException($"'{what}' must be a number");
            return v;
        }

        public static bool IsId(string id)
            => !string.IsNullOrEmpty(id) && id.All(c => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-');
    }
}
