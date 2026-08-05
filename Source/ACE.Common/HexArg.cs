using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ACE.Common
{
    /// <summary>
    /// Shared parsing for comma-separated hex id lists (landblock ids, cell ids) used by the map/catalog
    /// commands - dungeonmap, surfacemap and anything else taking a "--landblocks=0x02A3,0x0111,..." list.
    ///
    /// Every entry is HEX, with an OPTIONAL "0x"/"0X" prefix, and the prefix is stripped per-entry BEFORE
    /// the hex parse so a whole list parses uniformly. This exists as one helper precisely because the two
    /// map commands each inlined their own split+parse loop; a per-entry inconsistency (first entry hex,
    /// later entries drifting) is exactly the class of bug that surfaced during the Proving Grounds build,
    /// so both commands now go through the same code.
    ///
    /// MIXED-PREFIX GUARD: under Windows PowerShell 5.1, an unquoted argument of this shape gets silently
    /// corrupted before it ever reaches this parser - the comma is PowerShell's array operator, so every
    /// token after the first is evaluated as a numeric literal and restringified in DECIMAL before being
    /// rejoined. Measured 2026-08-01: unquoted "--landblocks=0x0032,0x017D,0x0021,0x01A8,0x01BA,0x0043,0x00AE"
    /// arrived as "--landblocks=0x0032,381,33,424,442,67,174" - the first token keeps its "0x" (it is glued
    /// to the "--landblocks=" prefix and always survives verbatim) while every later token has lost it, and
    /// each of those decimal numbers gets misread as hex ("381" -> 0x0381). The tool rendered seven wrong
    /// dungeons, printed a normal-looking table, and returned exit code 0. Refusing that specific shape -
    /// first token prefixed, a later token not - turns the silent wrong render into an error naming the fix
    /// (quote the whole argument). A list where every token is prefixed, or where none are, stays legal.
    /// </summary>
    public static class HexArg
    {
        /// <summary>Parse a single token as hex, tolerating a leading 0x/0X and surrounding whitespace.</summary>
        public static bool TryParseUInt(string token, out uint value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(token))
                return false;

            var s = token.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                s = s.Substring(2);

            return uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>
        /// Parse a comma-separated hex list. Every entry uses the same 0x-optional hex rule. Before parsing,
        /// checks for the PowerShell mixed-prefix corruption signature (see class doc comment): more than
        /// one token, the first prefixed, a later one not. On that shape, or on the first unparseable entry,
        /// returns false with a complete, ready-to-print message in <paramref name="error"/>.
        /// </summary>
        public static bool TryParseUIntList(string arg, out List<uint> values, out string error)
        {
            values = new List<uint>();
            error = null;

            var tokens = (arg ?? string.Empty)
                .Split(new[] { ',' })
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToList();

            if (tokens.Count > 1 && tokens[0].StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                var badToken = tokens.Skip(1).FirstOrDefault(t => !t.StartsWith("0x", StringComparison.OrdinalIgnoreCase));
                if (badToken != null)
                {
                    error = $"'{badToken}' is missing its 0x prefix while '{tokens[0]}' has one - this is the signature of " +
                        "Windows PowerShell 5.1 corrupting an unquoted comma list: every token after the first is " +
                        "evaluated as a number and re-emitted in decimal, so the original hex ids are LOST (they cannot " +
                        "be recovered from this argument). Re-type the ids from their original source and quote the " +
                        "whole argument, e.g. \"--landblocks=0x0032,0x017D\".";
                    return false;
                }
            }

            foreach (var part in tokens)
            {
                if (!TryParseUInt(part, out var v))
                {
                    error = $"'{part}' is not a hex id (e.g. 0x0066). Every comma-separated entry must be hex, with an optional 0x prefix.";
                    return false;
                }
                values.Add(v);
            }

            return true;
        }
    }
}
