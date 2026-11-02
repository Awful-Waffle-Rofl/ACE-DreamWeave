using System;
using System.Collections.Generic;
using System.Linq;

namespace ACE.Server.Pvp
{
    /// <summary>What a resolved match looks like to the Arena Crier's result line.</summary>
    public enum PvpResultKind
    {
        /// <summary>A two-team win: <c>sides[0]</c> is the winning side, <c>sides[1]</c> the losing one.</summary>
        Win,

        /// <summary>A draw: two sides (two-team modes) or none (Tugak Brawl).</summary>
        Draw,

        /// <summary>Tugak Brawl: <c>sides[0]</c> holds the winners; <c>ffaOthers</c> is everyone else.</summary>
        FfaWin,

        /// <summary>Nobody won and it was not a draw (every fighter forfeited).</summary>
        NoWinner,
    }

    /// <summary>One side of a result line. A null <paramref name="Label"/> prints the bare names (1v1).</summary>
    public sealed record PvpResultSide(string Label, IReadOnlyList<string> Names);

    /// <summary>
    /// The one-line match result the Arena Crier posts on /lfg (Docs/Pvp/DESIGN.md "Arena Crier"). Pure: no
    /// PropertyManager, no match state. Character names only; ASCII only; at most <see cref="MaxLength"/>
    /// characters, shortening the longest name list to "first, second, and k more" and never cutting a name.
    /// </summary>
    public static class PvpMatchResultLine
    {
        /// <summary>The AC chat line is cut near 255 characters; stay clear of it.</summary>
        public const int MaxLength = 250;

        public static string Build(string modeLabel, PvpResultKind kind, IReadOnlyList<PvpResultSide> sides = null, int ffaOthers = 0)
        {
            sides ??= Array.Empty<PvpResultSide>();

            switch (kind)
            {
                case PvpResultKind.NoWinner:
                    return $"{modeLabel} ended with no winner.";

                case PvpResultKind.Draw when sides.Count < 2:
                    return $"{modeLabel} ended in a draw.";

                case PvpResultKind.FfaWin when sides.Count < 1 || sides[0].Names.Count == 0:
                    return $"{modeLabel} ended with no winner.";

                case PvpResultKind.Win when sides.Count < 2:
                    return $"{modeLabel} ended with no winner.";
            }

            var visible = sides.Select(s => s.Names.Count).ToArray();

            string Render()
            {
                switch (kind)
                {
                    case PvpResultKind.FfaWin:
                        return $"{modeLabel}: {Names(sides[0], visible[0], " and ")} won against {ffaOthers} other fighter{(ffaOthers == 1 ? "" : "s")}.";
                    case PvpResultKind.Draw:
                        return $"{modeLabel} ended in a draw: {Side(sides[0], visible[0])} vs {Side(sides[1], visible[1])}.";
                    default:
                        return $"{modeLabel}: {Side(sides[0], visible[0])} defeated {Side(sides[1], visible[1])}.";
                }
            }

            var line = Render();

            while (line.Length > MaxLength)
            {
                // Shorten the side whose rendered text is longest, one name at a time, but never below one name.
                var pick = -1;
                var pickLength = -1;

                for (var i = 0; i < sides.Count; i++)
                {
                    if (visible[i] <= 1)
                        continue;

                    var length = Side(sides[i], visible[i]).Length;

                    if (length > pickLength)
                    {
                        pick = i;
                        pickLength = length;
                    }
                }

                if (pick < 0)
                    break;

                visible[pick]--;
                line = Render();
            }

            return line;
        }

        private static string Side(PvpResultSide side, int visible)
        {
            var names = Names(side, visible, ", ");

            return side.Label == null ? names : $"{side.Label} ({names})";
        }

        private static string Names(PvpResultSide side, int visible, string separator)
        {
            if (visible >= side.Names.Count)
                return string.Join(separator, side.Names);

            return string.Join(", ", side.Names.Take(visible)) + ", and " + (side.Names.Count - visible) + " more";
        }
    }
}
