using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ACE.Server.HazardMechanics
{
    /// <summary>
    /// One run's numbers: a case-insensitive key=value map built by laying the caller's args (the /hazard
    /// command line, or a world event / boss script) OVER the mechanic's settings string (hazard_mech_wisps and
    /// friends). Args win; within one string the FIRST occurrence of a key wins.
    ///
    /// TOTAL ON PURPOSE, the same contract MlDigsiteMechanicArgs states: the accessors never throw and never hand a
    /// mechanic a non-finite or out-of-range value, so a bad value costs the designer a wrong number, never the tick.
    /// Every typed accessor takes a documented default AND a clamp. But a typo is not silently defaulted at START:
    /// each mechanic's Validate first asks <see cref="FirstNonNumeric"/> and REFUSES the run with one line. A typo in
    /// a hazard_mech_&lt;kind&gt; settings row therefore refuses every run, so HazardManager.Start appends
    /// " (from setting hazard_mech_&lt;kind&gt;)" to a refusal that names a key the caller did not give.
    ///
    /// Immutable; safe to share across threads.
    /// </summary>
    public sealed class HazardArgs
    {
        public static readonly HazardArgs Empty = new HazardArgs(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

        private readonly Dictionary<string, string> map;

        private HazardArgs(Dictionary<string, string> map)
        {
            this.map = map;
        }

        public int Count => map.Count;

        public IEnumerable<string> Keys => map.Keys;

        public bool Has(string key) => key != null && map.ContainsKey(key);

        public string GetString(string key, string defaultValue = null)
            => key != null && map.TryGetValue(key, out var value) ? value : defaultValue;

        /// <summary>A finite double clamped to [min, max]; missing or unparsable or non-finite -> <paramref name="defaultValue"/> (also clamped).</summary>
        public double GetDouble(string key, double defaultValue, double min, double max)
        {
            var raw = GetString(key);

            var value = raw != null
                && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                && double.IsFinite(parsed)
                    ? parsed
                    : defaultValue;

            return Math.Clamp(value, min, max);
        }

        /// <summary>An int clamped to [min, max]; missing or unparsable -> <paramref name="defaultValue"/> (also clamped).</summary>
        public int GetInt(string key, int defaultValue, int min, int max)
        {
            var raw = GetString(key);

            var value = raw != null && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : defaultValue;

            return Math.Clamp(value, min, max);
        }

        /// <summary>true/false/1/0/on/off/yes/no; anything else -> <paramref name="defaultValue"/>.</summary>
        public bool GetBool(string key, bool defaultValue)
        {
            var raw = GetString(key);

            if (raw == null)
                return defaultValue;

            switch (raw.Trim().ToLowerInvariant())
            {
                case "1": case "true": case "on": case "yes": return true;
                case "0": case "false": case "off": case "no": return false;
                default: return defaultValue;
            }
        }

        /// <summary>One of <paramref name="allowed"/> (case-insensitive, returned lower-case); anything else -> <paramref name="defaultValue"/>.</summary>
        public string GetChoice(string key, string defaultValue, params string[] allowed)
        {
            var raw = GetString(key);

            if (raw == null || allowed == null)
                return defaultValue;

            foreach (var a in allowed)
                if (string.Equals(a, raw, StringComparison.OrdinalIgnoreCase))
                    return a.ToLowerInvariant();

            return defaultValue;
        }

        /// <summary>
        /// THE ONE NUMERIC REFUSAL every mechanic's Validate uses (shared invariant 8): the accessors above are total
        /// and silently fall back to the default, which would turn a typo ("speed=fast") into a run at the default
        /// speed. Validate asks this first instead. Returns ONE line naming the first GIVEN key that is not a finite
        /// number (<paramref name="numberKeys"/>) or not a whole number (<paramref name="wholeKeys"/>), else null.
        /// A key that is not given is fine (it takes its default).
        /// </summary>
        public string FirstNonNumeric(IEnumerable<string> numberKeys, IEnumerable<string> wholeKeys = null)
        {
            if (numberKeys != null)
            {
                foreach (var key in numberKeys)
                {
                    if (Has(key) && !(double.TryParse(GetString(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v)))
                        return $"{key}='{GetString(key)}' is not a number";
                }
            }

            if (wholeKeys != null)
            {
                foreach (var key in wholeKeys)
                {
                    if (Has(key) && !int.TryParse(GetString(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                        return $"{key}='{GetString(key)}' is not a whole number";
                }
            }

            return null;
        }

        /// <summary>A copy with <paramref name="key"/> set to <paramref name="value"/> (replacing any existing value).</summary>
        public HazardArgs With(string key, string value)
        {
            var copy = new Dictionary<string, string>(map, StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(key) && value != null)
                copy[key.Trim()] = value.Trim();

            return new HazardArgs(copy);
        }

        /// <summary>Canonical "k=v k=v" in key order. Logged with every run so a run is reproducible from its log line.</summary>
        public override string ToString()
            => string.Join(" ", map.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Select(kv => $"{kv.Key}={kv.Value}"));

        // ---- parsing ---------------------------------------------------------------------------------------------

        /// <summary>
        /// Reads "key=value key=value". A token with no '=', an empty key or value, or a repeated key is dropped
        /// with a line in <paramref name="errors"/> (when supplied); the FIRST occurrence of a key wins. Never null.
        /// </summary>
        public static HazardArgs Parse(string raw, List<string> errors = null)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return Empty;

            return FromTokens(raw.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries), errors);
        }

        /// <summary>The same rules as <see cref="Parse"/> over already-split tokens (a command line).</summary>
        public static HazardArgs FromTokens(IEnumerable<string> tokens, List<string> errors = null)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (tokens == null)
                return Empty;

            foreach (var token in tokens)
            {
                if (string.IsNullOrWhiteSpace(token))
                    continue;

                var equals = token.IndexOf('=');

                if (equals <= 0 || equals == token.Length - 1)
                {
                    errors?.Add($"token '{token}' is not key=value");
                    continue;
                }

                var key = token.Substring(0, equals).Trim();
                var value = token.Substring(equals + 1).Trim();

                if (key.Length == 0 || value.Length == 0)
                {
                    errors?.Add($"token '{token}' is not key=value");
                    continue;
                }

                if (map.ContainsKey(key))
                {
                    errors?.Add($"token '{token}' repeats key '{key}'; the first one is kept");
                    continue;
                }

                map[key] = value;
            }

            return map.Count == 0 ? Empty : new HazardArgs(map);
        }

        /// <summary>
        /// <paramref name="overrides"/> laid over <paramref name="baseArgs"/>: every key of the overrides wins,
        /// every other key of the base survives. Either side may be null.
        /// </summary>
        public static HazardArgs Merge(HazardArgs overrides, HazardArgs baseArgs)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (baseArgs != null)
                foreach (var kv in baseArgs.map)
                    map[kv.Key] = kv.Value;

            if (overrides != null)
                foreach (var kv in overrides.map)
                    map[kv.Key] = kv.Value;

            return map.Count == 0 ? Empty : new HazardArgs(map);
        }
    }
}
