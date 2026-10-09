using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ACE.Server.MonsterEffects
{
    /// <summary>
    /// One authored effect record: its kind plus the key=value args that came with it. Immutable, produced
    /// by <see cref="MonsterEffectParser"/> and shared by every monster of the wcid it was parsed for, so
    /// nothing here may ever be mutated - all per-monster mutable state lives in
    /// <see cref="MonsterEffectState"/>.
    ///
    /// Args are stored verbatim and converted on read. The typed accessors are total: a missing key, an
    /// unparseable value or an out-of-range enum all return the caller's default rather than throwing,
    /// because a handler reading its own args in a combat hot path must never be able to kill a tick. That
    /// is not a licence for silence - a value the handler actually cares about is checked once at build time
    /// in <see cref="IMonsterEffect.Validate"/>, where a bad one is a logged warning naming the wcid.
    /// </summary>
    public sealed class MonsterEffectSpec
    {
        /// <summary>
        /// The authored kind token, lowercased. Matches <see cref="IMonsterEffect.Kind"/>.
        /// </summary>
        public string Kind { get; }

        private readonly IReadOnlyDictionary<string, string> args;

        public MonsterEffectSpec(string kind, IReadOnlyDictionary<string, string> args)
        {
            Kind = kind ?? throw new ArgumentNullException(nameof(kind));
            this.args = args ?? throw new ArgumentNullException(nameof(args));
        }

        /// <summary>
        /// True when the key was authored at all, whatever its value. The distinction matters for args whose
        /// absence and whose zero mean different things.
        /// </summary>
        public bool Has(string key) => key != null && args.ContainsKey(key);

        /// <summary>
        /// The raw authored text for a key, or null. Handlers should prefer the typed accessors; this exists
        /// for args that are neither a number nor an enum (a spell name, a quest key).
        /// </summary>
        public string GetString(string key, string defaultValue = null) =>
            key != null && args.TryGetValue(key, out var value) ? value : defaultValue;

        public double GetDouble(string key, double defaultValue)
        {
            var raw = GetString(key);

            // invariant culture on purpose: the string is authored in SQL, not localised
            return raw != null && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? value
                : defaultValue;
        }

        public int GetInt(string key, int defaultValue)
        {
            var raw = GetString(key);

            return raw != null && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : defaultValue;
        }

        /// <summary>
        /// A single enum value, matched by NAME case-insensitively (never by number - an authored "3" is a
        /// content bug waiting for the enum to be renumbered).
        /// </summary>
        public T GetEnum<T>(string key, T defaultValue) where T : struct, Enum
        {
            var raw = GetString(key);
            if (raw == null)
                return defaultValue;

            return Enum.TryParse<T>(raw, ignoreCase: true, out var value) && Enum.IsDefined(typeof(T), value)
                ? value
                : defaultValue;
        }

        /// <summary>
        /// A comma-separated multi-value arg folded into one [Flags] enum - "on=hit,avoid" becomes
        /// Hit | Avoid. Unrecognised members are dropped; if NOTHING in the list resolves the default is
        /// returned, so a wholly mis-authored value behaves as if it were absent rather than as None.
        /// </summary>
        public T GetFlags<T>(string key, T defaultValue) where T : struct, Enum
        {
            var raw = GetString(key);
            if (raw == null)
                return defaultValue;

            long combined = 0;
            var matched = false;

            foreach (var token in raw.Split(','))
            {
                var name = token.Trim();
                if (name.Length == 0)
                    continue;

                if (!Enum.TryParse<T>(name, ignoreCase: true, out var value) || !Enum.IsDefined(typeof(T), value))
                    continue;

                combined |= Convert.ToInt64(value, CultureInfo.InvariantCulture);
                matched = true;
            }

            return matched ? (T)Enum.ToObject(typeof(T), combined) : defaultValue;
        }

        /// <summary>
        /// A boolean arg. Accepts true/false, 1/0 and yes/no; anything else falls back to the default.
        /// There is no bare-token form - a flag arg is authored as "silent=true", because a token with no
        /// '=' is a malformed token and the parser reports it (see MonsterEffectParser).
        /// </summary>
        public bool GetBool(string key, bool defaultValue)
        {
            var raw = GetString(key);
            if (raw == null)
                return defaultValue;

            switch (raw.Trim().ToLowerInvariant())
            {
                case "true":
                case "1":
                case "yes":
                    return true;

                case "false":
                case "0":
                case "no":
                    return false;

                default:
                    return defaultValue;
            }
        }

        /// <summary>
        /// The record as it would be authored, kind first then args in authored order. Used in warnings so a
        /// content author can find the offending record in the weenie by eye.
        /// </summary>
        public override string ToString() =>
            args.Count == 0 ? Kind : Kind + " " + string.Join(" ", args.Select(kvp => kvp.Key + "=" + kvp.Value));
    }
}
