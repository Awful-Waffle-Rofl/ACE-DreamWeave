using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ACE.Server.Pvp
{
    /// <summary>One side of a finished 1v1 / 2v2 match, as the audit sees it.</summary>
    /// <param name="AccountKeys">The account keys (<see cref="PvpPairLedger.AccountKey"/>) of the team's players.</param>
    /// <param name="Names">The players' character names, for the staff line only (never part of the key).</param>
    /// <param name="Forfeited">Character names on this team that forfeited (left or logged out rather than died).</param>
    public sealed record AuditTeam(IReadOnlyList<uint> AccountKeys, IReadOnlyList<string> Names, IReadOnlyList<string> Forfeited);

    /// <summary>
    /// Win-trading audit (Docs/Pvp/DESIGN.md "Alternating-result audit"). Watches the results between the SAME exact
    /// lineup and, when the winner keeps alternating, produces one staff line. It ONLY reports: it never touches a payout, a
    /// rating, or the queue. Pure and in-memory (a restart forgets it, accepted).
    ///
    /// Key: the exact lineup - each team's account keys sorted, the teams sorted - so partner order and team order do not
    /// matter, and a 1v1 is "a|b". A different partner is a different lineup.
    ///
    /// Swaps: among the lineup's DECIDED results inside the window, the number of adjacent pairs whose winner differs.
    /// A draw is not a decided result: it is kept in the line for staff to read but it neither adds a swap nor breaks the
    /// run, so A, draw, B is one swap. The window is rolling and trimmed on every call; when swaps reach the threshold
    /// ONE line is produced and the key stays quiet until a full window has passed since that line.
    /// </summary>
    public sealed class PvpPairAudit
    {
        public const string Tag = "[PVP-AUDIT]";

        /// <summary>The most results the staff line lists (the newest); the header still carries the true count.</summary>
        public const int MaxListed = 10;

        private sealed class Entry
        {
            public DateTime Utc;
            public string WinnerKey;    // null = draw
            public string Summary;      // staff-readable result text
        }

        private sealed class History
        {
            public readonly List<Entry> Entries = new List<Entry>();
            public DateTime? FlaggedAtUtc;
            public string Lineup;
        }

        private readonly Dictionary<string, History> histories = new Dictionary<string, History>();

        /// <summary>Lineups currently holding history; for tests and memory checks.</summary>
        public int TrackedLineups => histories.Count;

        /// <summary>The canonical key for one team: its account keys sorted, joined with "+".</summary>
        public static string TeamKey(IEnumerable<uint> accountKeys) =>
            string.Join("+", accountKeys.OrderBy(a => a).Select(a => a.ToString(CultureInfo.InvariantCulture)));

        /// <summary>The canonical lineup key: team keys sorted ordinally, joined with "|". A 1v1 is "a|b".</summary>
        public static string LineupKey(IEnumerable<AuditTeam> teams) =>
            string.Join("|", teams.Select(t => TeamKey(t.AccountKeys)).OrderBy(k => k, StringComparer.Ordinal));

        /// <summary>
        /// Records one finished match and returns the staff line when this result tips the lineup over the threshold, else null.
        /// </summary>
        /// <param name="winningTeam">Index into <paramref name="teams"/> of the winner, or null for a draw.</param>
        /// <param name="duration">How long the match was Live.</param>
        public string Record(DateTime utc, IReadOnlyList<AuditTeam> teams, int? winningTeam, TimeSpan duration, bool forfeit, long windowHours, long minSwaps)
        {
            if (teams == null || teams.Count < 2 || windowHours <= 0)
                return null;

            var window = TimeSpan.FromHours(windowHours);

            Prune(utc, window);

            var key = LineupKey(teams);

            if (!histories.TryGetValue(key, out var history))
            {
                history = new History { Lineup = string.Join(" vs ", teams.OrderBy(t => TeamKey(t.AccountKeys), StringComparer.Ordinal).Select(t => string.Join(" + ", t.Names))) };
                histories[key] = history;
            }

            history.Entries.Add(new Entry
            {
                Utc = utc,
                WinnerKey = winningTeam.HasValue && winningTeam.Value >= 0 && winningTeam.Value < teams.Count ? TeamKey(teams[winningTeam.Value].AccountKeys) : null,
                Summary = Describe(teams, winningTeam, duration, forfeit, utc)
            });

            var swaps = CountSwaps(history.Entries);

            if (minSwaps <= 0 || swaps < minSwaps)
                return null;

            if (history.FlaggedAtUtc.HasValue && utc - history.FlaggedAtUtc.Value < window)
                return null;

            history.FlaggedAtUtc = utc;

            var earlier = Math.Max(0, history.Entries.Count - MaxListed);
            var listed = string.Join("; ", history.Entries.Skip(earlier).Select(e => e.Summary));

            return $"{Tag} alternating results between the same lineup ({history.Lineup}; accounts {key}): {swaps} winner swaps in {history.Entries.Count} matches within {windowHours}h: "
                + (earlier > 0 ? $"(+{earlier} earlier) " : "") + listed;
        }

        /// <summary>Adjacent decided results with different winners. Draws are skipped, not counted and not run-breaking.</summary>
        public static int CountSwaps(IEnumerable<string> winnerKeysOldestFirst)
        {
            var swaps = 0;
            string previous = null;

            foreach (var winner in winnerKeysOldestFirst)
            {
                if (winner == null)
                    continue;

                if (previous != null && !string.Equals(previous, winner, StringComparison.Ordinal))
                    swaps++;

                previous = winner;
            }

            return swaps;
        }

        private static int CountSwaps(List<Entry> entries) => CountSwaps(entries.Select(e => e.WinnerKey));

        /// <summary>Drops results older than the window and any lineup left empty, so the table cannot grow without bound.</summary>
        private void Prune(DateTime utc, TimeSpan window)
        {
            var cutoff = utc - window;

            foreach (var key in histories.Keys.ToList())
            {
                var h = histories[key];

                h.Entries.RemoveAll(e => e.Utc < cutoff);

                // A flag older than the window has expired with its entries; an empty lineup is forgotten entirely.
                if (h.Entries.Count == 0)
                    histories.Remove(key);
            }
        }

        private static string Describe(IReadOnlyList<AuditTeam> teams, int? winningTeam, TimeSpan duration, bool forfeit, DateTime utc)
        {
            var when = utc.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "Z";
            var secs = ((long)Math.Max(0, duration.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            var winner = winningTeam.HasValue && winningTeam.Value >= 0 && winningTeam.Value < teams.Count
                ? "winner " + string.Join(" + ", teams[winningTeam.Value].Names)
                : "draw";
            var forfeiters = teams.SelectMany(t => t.Forfeited).ToList();
            var forfeitText = forfeit || forfeiters.Count > 0
                ? ", forfeit" + (forfeiters.Count > 0 ? " by " + string.Join(" + ", forfeiters) : "")
                : "";

            return $"{when} {winner}{forfeitText}, {secs}s";
        }
    }
}
