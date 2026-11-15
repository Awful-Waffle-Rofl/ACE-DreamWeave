using System;

using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;

namespace ACE.Server.Frays
{
    /// <summary>
    /// The Frays' /top boards, read from every character's record (online or not): highest Heat,
    /// fastest Snarl kill (the guardian board) and Heat rating, all-time or this week. Staff are left out by the
    /// existing leaderboard exemption in PlayerCommands.SendLeaderboard.
    /// </summary>
    public static class FraysLeaderboards
    {
        /// <summary>Fastest-kill scores are stored as this over the seconds, so the board's "higher is better" works.</summary>
        private const double GuardianScale = 36_000_000;

        public static (string Title, Func<IPlayer, long> Score, Func<long, string> Format)? Board(string which, bool weekly)
        {
            var week = FraysWeek.Current;
            var span = weekly ? " (this week)" : "";

            FraysPlayerRecord Rec(IPlayer p) => FraysPlayerRecord.Parse(p.GetProperty(PropertyString.FraysState));
            bool ThisWeek(FraysPlayerRecord r) => r.Week == week;

            switch (which)
            {
                case "heat":
                    return ($"Fray Heat{span}", p => { var r = Rec(p); return weekly ? (ThisWeek(r) ? r.WeekBestHeat : 0) : r.BestHeat; }, s => $"Heat {s}");

                case "guardian":
                case "snarl":
                    return ($"Fray Guardian (fastest Snarl){span}", p =>
                    {
                        var r = Rec(p);
                        var seconds = weekly ? (ThisWeek(r) ? r.WeekBestSnarlSeconds : 0) : r.BestSnarlSeconds;
                        return seconds > 0 ? (long)Math.Round(GuardianScale / seconds) : 0;
                    }, s => $"{GuardianScale / Math.Max(1, s):0} seconds");

                case "score":
                case "run":
                    return ($"Fray best run score{span}", p => { var r = Rec(p); return weekly ? (ThisWeek(r) ? r.WeekBestRunScore : 0) : r.BestRunScore; }, s => $"{s:N0}");

                // Group camps keep their own boards: a fellowship's shared Heat never lands on the solo boards.
                case "group":
                case "groupheat":
                    return ($"Fray group Heat{span}", p => { var r = Rec(p); return weekly ? (ThisWeek(r) ? r.WeekBestGroupHeat : 0) : r.BestGroupHeat; }, s => $"Heat {s}");

                case "groupscore":
                    return ($"Fray group run score{span}", p => { var r = Rec(p); return weekly ? (ThisWeek(r) ? r.WeekBestGroupRunScore : 0) : r.BestGroupRunScore; }, s => $"{s:N0}");

                case "grouprating":
                    return ("Fray group Heat rating", p => Rec(p).GroupRating, s => $"rating {s}");

                case "ascension":
                case "ascensions":
                case "ascend":
                    // Ascensions, highest first (the rating breaks no ties; the board's own order does).
                    return ("Fray Ascensions", p => Rec(p).Ascensions, s => $"Ascension {FraysLongTerm.Roman((int)s)}");

                case "rating":
                    return ("Fray Heat rating", p => Rec(p).Rating, s => $"rating {s}");

                default:
                    return null;
            }
        }

        /// <summary>
        /// The Ascensions column on every /top fray board: " - Ascension III, the Flamewarden", or
        /// nothing for a character who has not ascended.
        /// </summary>
        public static string Detail(IPlayer p)
        {
            var asc = FraysPlayerRecord.Parse(p.GetProperty(PropertyString.FraysState)).Ascensions;
            return DetailText(asc);
        }

        /// <summary>Pure: the column's text for <paramref name="ascensions"/>.</summary>
        public static string DetailText(int ascensions) =>
            ascensions > 0 ? $" - Ascension {FraysLongTerm.Roman(ascensions)}, {FraysLongTerm.AscensionTitle(ascensions)}" : "";
    }
}
