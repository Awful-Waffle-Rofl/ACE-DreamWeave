using System;
using System.Collections.Generic;
using System.Globalization;

namespace ACE.Server.PuzzleGates
{
    /// <summary>
    /// Parsed and validated options of <c>/puzzlegate place</c>. Pure: no exceptions for user input, errors
    /// come back as a string. Admin-facing text, so error lines may name keys and values.
    /// </summary>
    public sealed class PuzzleGateOptions
    {
        public PuzzleGateType Type { get; private set; }

        /// <summary>
        /// Lever count as parsed. 1 for shuffle. For sigil and odd with n= omitted this is only a placeholder:
        /// the generator draws 4 or 5 per placement (see <see cref="NExplicit"/> and PuzzleGateGenerator.N).
        /// </summary>
        public int N { get; private set; } = PuzzleGateTunables.DefaultN;

        /// <summary>True when n= was given explicitly.</summary>
        public bool NExplicit { get; private set; }

        /// <summary>Beam count (beam type only). 0 for the other types.</summary>
        public int Beams { get; private set; }

        public int Rounds { get; private set; } = PuzzleGateTunables.DefaultRounds;

        /// <summary>Explicit seed, or null when the caller should pick one.</summary>
        public int? Seed { get; private set; }

        public int LockoutSeconds { get; private set; } = PuzzleGateTunables.DefaultLockoutSeconds;

        /// <summary>Ambush creature wcid, 0 = none.</summary>
        public uint AmbushWcid { get; private set; }

        public int AmbushCount { get; private set; } = 1;

        public PuzzleOddChannel Diff { get; private set; } = PuzzleOddChannel.Auto;

        /// <summary>Shuffle spot count K.</summary>
        public int Spots { get; private set; } = PuzzleGateTunables.DefaultSpots;

        /// <summary>True when spots= was given explicitly.</summary>
        public bool SpotsExplicit { get; private set; }

        public float Radius { get; private set; } = PuzzleGateTunables.DefaultRadius;

        public PuzzleBeamAxis Axis { get; private set; } = PuzzleBeamAxis.Down;

        public static bool TryParseType(string token, out PuzzleGateType type)
        {
            switch ((token ?? "").Trim().ToLowerInvariant())
            {
                case "sigil": type = PuzzleGateType.Sigil; return true;
                case "beam": type = PuzzleGateType.Beam; return true;
                case "odd": type = PuzzleGateType.Odd; return true;
                case "shuffle": type = PuzzleGateType.Shuffle; return true;
                default: type = default; return false;
            }
        }

        /// <summary>
        /// Parses the tokens that follow <c>place</c>: <c>&lt;type&gt; [key=value ...]</c>. Keys are
        /// case-insensitive. Unknown keys, keys that do not apply to the type, duplicates and out-of-range
        /// values are rejected by name.
        /// </summary>
        public static bool TryParse(IReadOnlyList<string> args, out PuzzleGateOptions options, out string error)
        {
            options = null;
            error = null;

            if (args == null || args.Count == 0)
            {
                error = "Missing type. Expected one of: sigil, beam, odd, shuffle.";
                return false;
            }

            if (!TryParseType(args[0], out var type))
            {
                error = $"Unknown type '{args[0]}'. Expected one of: sigil, beam, odd, shuffle.";
                return false;
            }

            var o = new PuzzleGateOptions { Type = type };
            var seen = new HashSet<string>();
            var beamsGiven = false;

            for (var i = 1; i < args.Count; i++)
            {
                var token = args[i] ?? "";
                var eq = token.IndexOf('=');

                if (eq <= 0 || eq == token.Length - 1)
                {
                    error = $"Bad token '{token}'. Expected key=value.";
                    return false;
                }

                var key = token.Substring(0, eq).ToLowerInvariant();
                var value = token.Substring(eq + 1);

                if (!IsKnownKey(key))
                {
                    error = $"Unknown key '{key}'.";
                    return false;
                }

                if (!AppliesTo(key, type))
                {
                    error = $"Key '{key}' does not apply to type '{TypeName(type)}'.";
                    return false;
                }

                if (!seen.Add(key))
                {
                    error = $"Key '{key}' given more than once.";
                    return false;
                }

                switch (key)
                {
                    case "n":
                        if (IsPickType(type))
                        {
                            if (!TryInt(key, value, PuzzleGateTunables.MinPickN, PuzzleGateTunables.MaxPickN, out var pickN, out error))
                            {
                                error = $"n must be {PuzzleGateTunables.MinPickN}-{PuzzleGateTunables.MaxPickN} for {TypeName(type)} (omit n= to draw 4 or 5 per placement), got '{value}'.";
                                return false;
                            }

                            o.N = pickN;
                            o.NExplicit = true;
                            break;
                        }

                        if (!TryInt(key, value, PuzzleGateTunables.MinN, PuzzleGateTunables.MaxN, out var n, out error)) return false;
                        o.N = n;
                        o.NExplicit = true;
                        break;
                    case "beams":
                        if (!TryInt(key, value, PuzzleGateTunables.MinBeams, PuzzleGateTunables.MaxBeams, out var b, out error)) return false;
                        o.Beams = b;
                        beamsGiven = true;
                        break;
                    case "rounds":
                        if (!TryInt(key, value, PuzzleGateTunables.MinRounds, PuzzleGateTunables.MaxRounds, out var r, out error)) return false;
                        o.Rounds = r;
                        break;
                    case "seed":
                        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seed))
                        {
                            error = $"seed must be an integer, got '{value}'.";
                            return false;
                        }
                        o.Seed = seed;
                        break;
                    case "lockout":
                        if (!TryInt(key, value, PuzzleGateTunables.MinLockoutSeconds, PuzzleGateTunables.MaxLockoutSeconds, out var l, out error)) return false;
                        o.LockoutSeconds = l;
                        break;
                    case "ambush":
                        if (!TryAmbush(value, o, out error)) return false;
                        break;
                    case "diff":
                        switch (value.ToLowerInvariant())
                        {
                            case "scale": o.Diff = PuzzleOddChannel.Scale; break;
                            case "yaw": o.Diff = PuzzleOddChannel.Yaw; break;
                            case "glow": o.Diff = PuzzleOddChannel.Glow; break;
                            default:
                                error = $"diff must be scale, yaw or glow, got '{value}'.";
                                return false;
                        }
                        break;
                    case "spots":
                        if (!TryInt(key, value, PuzzleGateTunables.MinSpots, PuzzleGateTunables.MaxSpots, out var k, out error)) return false;
                        o.Spots = k;
                        o.SpotsExplicit = true;
                        break;
                    case "radius":
                        if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var rad)
                            || float.IsNaN(rad) || float.IsInfinity(rad)
                            || rad < PuzzleGateTunables.MinRadius || rad > PuzzleGateTunables.MaxRadius)
                        {
                            error = $"radius must be {PuzzleGateTunables.MinRadius.ToString(CultureInfo.InvariantCulture)}-{PuzzleGateTunables.MaxRadius.ToString(CultureInfo.InvariantCulture)} metres, got '{value}'.";
                            return false;
                        }
                        o.Radius = rad;
                        break;
                    case "axis":
                        switch (value.ToLowerInvariant())
                        {
                            case "down": o.Axis = PuzzleBeamAxis.Down; break;
                            case "up": o.Axis = PuzzleBeamAxis.Up; break;
                            default:
                                error = $"axis must be down or up, got '{value}'.";
                                return false;
                        }
                        break;
                }
            }

            // Pick types with n= omitted: the generator draws 4 or 5 from the placement seed; this is a placeholder.
            if (IsPickType(type) && !o.NExplicit)
                o.N = PuzzleGateTunables.MinPickN;

            if (type == PuzzleGateType.Shuffle)
                o.N = 1;

            if (type == PuzzleGateType.Beam)
            {
                if (!beamsGiven)
                    o.Beams = Math.Min(o.N, 2);
                else if (o.Beams > o.N)
                {
                    error = $"beams ({o.Beams}) cannot exceed n ({o.N}).";
                    return false;
                }
            }

            options = o;
            return true;
        }

        private static string TypeName(PuzzleGateType t) => t.ToString().ToLowerInvariant();

        /// <summary>The pick-one-lever types whose n is 4-5 and drawn per placement when omitted.</summary>
        public static bool IsPickType(PuzzleGateType t) => t == PuzzleGateType.Sigil || t == PuzzleGateType.Odd;

        private static bool IsKnownKey(string key)
        {
            switch (key)
            {
                case "n": case "beams": case "rounds": case "seed": case "lockout":
                case "ambush": case "diff": case "spots": case "radius": case "axis":
                    return true;
                default:
                    return false;
            }
        }

        private static bool AppliesTo(string key, PuzzleGateType type)
        {
            switch (key)
            {
                case "n": return type != PuzzleGateType.Shuffle;
                case "beams": return type == PuzzleGateType.Beam;
                // Sigil lights and the indicator hang from the same Puzzle Light host as the beams, so the
                // unverified beam-axis switch has to reach them too.
                case "axis": return type == PuzzleGateType.Beam || type == PuzzleGateType.Sigil;
                case "diff": return type == PuzzleGateType.Odd;
                case "spots": case "radius": return type == PuzzleGateType.Shuffle;
                default: return true;
            }
        }

        private static bool TryInt(string key, string value, int min, int max, out int result, out string error)
        {
            error = null;

            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result) || result < min || result > max)
            {
                error = $"{key} must be {min}-{max}, got '{value}'.";
                return false;
            }

            return true;
        }

        /// <summary>ambush=&lt;wcid&gt; or ambush=&lt;wcid&gt;x&lt;1-5&gt;.</summary>
        private static bool TryAmbush(string value, PuzzleGateOptions o, out string error)
        {
            error = null;

            var wcidText = value;
            var countText = (string)null;
            var x = value.IndexOfAny(new[] { 'x', 'X' });

            if (x >= 0)
            {
                wcidText = value.Substring(0, x);
                countText = value.Substring(x + 1);
            }

            if (!uint.TryParse(wcidText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var wcid) || wcid == 0)
            {
                error = $"ambush must be <wcid> or <wcid>x<{PuzzleGateTunables.MinAmbushCount}-{PuzzleGateTunables.MaxAmbushCount}>, got '{value}'.";
                return false;
            }

            var count = 1;

            if (countText != null
                && (!int.TryParse(countText, NumberStyles.Integer, CultureInfo.InvariantCulture, out count)
                    || count < PuzzleGateTunables.MinAmbushCount || count > PuzzleGateTunables.MaxAmbushCount))
            {
                error = $"ambush count must be {PuzzleGateTunables.MinAmbushCount}-{PuzzleGateTunables.MaxAmbushCount}, got '{countText}'.";
                return false;
            }

            o.AmbushWcid = wcid;
            o.AmbushCount = count;
            return true;
        }
    }
}