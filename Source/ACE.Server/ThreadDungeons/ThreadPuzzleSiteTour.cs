using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using ACE.Server.PuzzleGates;
using ACE.Server.ThreadDungeons.Defs;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>What a parsed /puzzlegate site line asks for.</summary>
    public enum PuzzleSiteAction
    {
        /// <summary>List a dungeon's sites (one page).</summary>
        List,

        /// <summary>Teleport to one site and place its puzzle.</summary>
        Place,

        /// <summary>Step the per-admin cursor and Place that site.</summary>
        Step,
    }

    /// <summary>What the arrival poll does on one pass.</summary>
    public enum SiteArrival
    {
        /// <summary>Wait and poll again.</summary>
        Wait,

        /// <summary>The admin is in the destination landblock and it has populated: place now.</summary>
        Place,

        /// <summary>Gave up waiting for the arrival.</summary>
        TimedOut,

        /// <summary>A newer site command replaced this one: do nothing.</summary>
        Superseded,
    }

    /// <summary>A parsed /puzzlegate site line.</summary>
    public sealed class PuzzleSiteArgs
    {
        public PuzzleSiteAction Action { get; init; }

        public string DungeonId { get; init; }

        public string SiteId { get; init; }

        /// <summary>Requested type; null = the site's first eligible type.</summary>
        public PuzzleGateType? Type { get; init; }

        /// <summary>Requested lever count; null = the site's maxN (clamped to the type's range).</summary>
        public int? N { get; init; }

        public int? Seed { get; init; }

        /// <summary>1-based page of a List.</summary>
        public int Page { get; init; } = 1;

        /// <summary>+1 for next, -1 for prev (Step only).</summary>
        public int Delta { get; init; }
    }

    /// <summary>
    /// The pure parts of /puzzlegate site (admin tour of the curated puzzle sites): argument parsing, the cursor
    /// arithmetic, the defaults a run's placement would have used, and the listing text. No world, no Player.
    /// </summary>
    public static class ThreadPuzzleSiteTour
    {
        /// <summary>Sites shown per list page, so a 56-site dungeon stays readable in chat.</summary>
        public const int PageSize = 12;

        public const string Usage =
            "  /puzzlegate site <dungeonId> [page=<n>]                         (list the dungeon's puzzle sites)\n" +
            "  /puzzlegate site <dungeonId> <siteId> [type=<sigil|beam|odd|shuffle>] [n=<levers>] [seed=<int>]\n" +
            "                                                                  (teleport to the site, place its puzzle as a run would)\n" +
            "  /puzzlegate site next|prev                                      (step through the last dungeon's sites, wraps)";

        /// <summary>Parses the tokens after "site". Pure.</summary>
        public static bool TryParse(IReadOnlyList<string> tokens, out PuzzleSiteArgs args, out string error)
        {
            args = null;
            error = null;

            if (tokens == null || tokens.Count == 0)
            {
                error = "expected a dungeon id, or next / prev.";
                return false;
            }

            var first = tokens[0].ToLowerInvariant();

            if (first == "next" || first == "prev" || first == "previous")
            {
                if (tokens.Count > 1)
                {
                    error = $"unexpected '{string.Join(" ", tokens.Skip(1))}' after {first}.";
                    return false;
                }

                args = new PuzzleSiteArgs { Action = PuzzleSiteAction.Step, Delta = first == "next" ? 1 : -1 };
                return true;
            }

            var dungeon = tokens[0];
            var rest = tokens.Skip(1).ToList();
            string site = null;

            if (rest.Count > 0 && !rest[0].Contains('='))
            {
                site = rest[0];
                rest.RemoveAt(0);
            }

            PuzzleGateType? type = null;
            int? n = null;
            int? seed = null;
            var page = 1;

            foreach (var token in rest)
            {
                var eq = token.IndexOf('=');

                if (eq <= 0)
                {
                    error = $"unknown token '{token}'.";
                    return false;
                }

                var key = token.Substring(0, eq).ToLowerInvariant();
                var value = token.Substring(eq + 1);

                switch (key)
                {
                    case "type":
                        if (type.HasValue || !PuzzleGateOptions.TryParseType(value, out var parsedType))
                        {
                            error = $"bad or repeated type '{token}'. Expected sigil, beam, odd or shuffle.";
                            return false;
                        }

                        type = parsedType;
                        break;

                    case "n":
                        if (n.HasValue || !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedN) || parsedN < 1)
                        {
                            error = $"bad or repeated n '{token}'.";
                            return false;
                        }

                        n = parsedN;
                        break;

                    case "seed":
                        if (seed.HasValue || !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedSeed))
                        {
                            error = $"bad or repeated seed '{token}'.";
                            return false;
                        }

                        seed = parsedSeed;
                        break;

                    case "page":
                        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out page) || page < 1)
                        {
                            error = $"bad page '{token}'.";
                            return false;
                        }

                        break;

                    default:
                        error = $"unknown option '{token}'.";
                        return false;
                }
            }

            if (site == null)
            {
                if (type.HasValue || n.HasValue || seed.HasValue)
                {
                    error = "type=, n= and seed= need a site id.";
                    return false;
                }

                args = new PuzzleSiteArgs { Action = PuzzleSiteAction.List, DungeonId = dungeon, Page = page };
                return true;
            }

            args = new PuzzleSiteArgs { Action = PuzzleSiteAction.Place, DungeonId = dungeon, SiteId = site, Type = type, N = n, Seed = seed };
            return true;
        }

        /// <summary>
        /// The cursor after a step: <paramref name="current"/> is the index last listed or used (-1 = none yet).
        /// Wraps both ways; a fresh cursor goes to the first site on next and the last on prev. -1 for an empty list.
        /// </summary>
        public static int Step(int current, int count, int delta)
        {
            if (count <= 0)
                return -1;

            if (current < 0 || current >= count)
                return delta >= 0 ? 0 : count - 1;

            return ((current + delta) % count + count) % count;
        }

        /// <summary>
        /// Pure: one pass of the arrival poll. Supersession wins over everything (an old poll must never place or
        /// even report once a newer command exists), then a ready arrival places, then the timeout, else wait.
        /// </summary>
        public static SiteArrival DecideArrival(bool superseded, bool arrived, bool landblockReady, double elapsedSeconds, double timeoutSeconds)
        {
            if (superseded)
                return SiteArrival.Superseded;

            if (arrived && landblockReady)
                return SiteArrival.Place;

            return elapsedSeconds > timeoutSeconds ? SiteArrival.TimedOut : SiteArrival.Wait;
        }

        /// <summary>Index of the site with this id (case-insensitive), or -1.</summary>
        public static int IndexOf(IReadOnlyList<PuzzleSiteDef> sites, string siteId)
        {
            if (sites == null || siteId == null)
                return -1;

            for (var i = 0; i < sites.Count; i++)
                if (sites[i] != null && string.Equals(sites[i].Id, siteId, StringComparison.OrdinalIgnoreCase))
                    return i;

            return -1;
        }

        /// <summary>The pick a run could have made for this site: first eligible type unless one was asked for, n = maxN clamped to the type's range.</summary>
        public static bool TryBuildPick(PuzzleSiteDef site, PuzzleSiteArgs args, int seed, out ThreadPuzzlePick pick, out string error)
        {
            pick = default;
            error = null;

            var eligible = ThreadPuzzleSitePicker.EligibleTypes(site);

            if (eligible.Count == 0)
            {
                error = $"site {site.Id} has no eligible puzzle type (listed: {string.Join(",", site.TypeNames ?? new List<string>())}, maxN={site.MaxN}, shuffle spots={site.ShuffleSpots?.Count ?? 0}).";
                return false;
            }

            var type = args?.Type ?? eligible[0];

            if (!eligible.Contains(type))
            {
                error = $"site {site.Id} cannot hold {type.ToString().ToLowerInvariant()}. Eligible: {string.Join(", ", eligible.Select(t => t.ToString().ToLowerInvariant()))}.";
                return false;
            }

            var n = DefaultN(type, site.MaxN, seed);

            if (args?.N is int asked)
            {
                if (type == PuzzleGateType.Shuffle)
                {
                    error = "shuffle takes no n.";
                    return false;
                }

                if (asked > site.MaxN)
                {
                    error = $"site {site.Id} fits at most {site.MaxN} levers.";
                    return false;
                }

                n = asked;
            }

            pick = new ThreadPuzzlePick(site, type, n, seed, site.Kind == PuzzleSiteKind.Reward);
            return true;
        }

        /// <summary>
        /// The lever count when none is asked for: the run picker's own rule (ThreadPuzzleSitePicker.LeverCount), so a
        /// site placed here has the count a run would give it. The picker's roll comes from the run's site stream,
        /// which a placement seed cannot recover, so the roll here is the placement seed itself: deterministic for a
        /// given seed= (sigil and odd draw 4 or 5 from it, capped at maxN), but not necessarily the run's draw.
        /// </summary>
        public static int DefaultN(PuzzleGateType type, int maxN, int seed)
            => ThreadPuzzleSitePicker.LeverCount(type, maxN, unchecked((uint)seed));

        /// <summary>One line describing a site: id, kind, types, maxN, gate model and doorway.</summary>
        public static string Describe(int index, PuzzleSiteDef site)
        {
            var m = site.GateModel;
            var model = m == null
                ? "none"
                : string.Format(CultureInfo.InvariantCulture, "{0} wcid={1} scale={2:0.##} panels={3}", (m.KindName ?? m.Kind.ToString()).ToLowerInvariant(), m.Wcid, m.Scale, m.Panels);
            var door = site.Doorway == null ? "?" : string.Format(CultureInfo.InvariantCulture, "{0:0.#}x{1:0.#}", site.Doorway.Width, site.Doorway.Height);
            var types = site.Types != null && site.Types.Count > 0
                ? string.Join("/", site.Types.Select(t => t.ToString().ToLowerInvariant()))
                : "-";

            return string.Format(CultureInfo.InvariantCulture, "  {0}. {1} [{2}] types={3} maxN={4} gate={5} doorway={6}{7}",
                index + 1, site.Id, (site.KindName ?? site.Kind.ToString()).ToLowerInvariant(), types, site.MaxN, model, door, DisabledTag(site));
        }

        /// <summary>
        /// " DISABLED (reason)" / " REWARD-DISABLED (reason)" for a site the kill switch keeps out of runs; "" otherwise.
        /// The tour still lists and places such a site, so it can be inspected.
        /// </summary>
        public static string DisabledTag(PuzzleSiteDef site)
        {
            if (site == null || (!site.Disabled && !site.RewardDisabled))
                return "";

            var reason = string.IsNullOrWhiteSpace(site.DisabledReason) ? "" : $" ({site.DisabledReason})";
            return (site.Disabled ? " DISABLED" : " REWARD-DISABLED") + reason;
        }

        /// <summary>The lines for one page of a dungeon's sites, with a header and a pointer to the next page.</summary>
        public static List<string> ListPage(string dungeonId, IReadOnlyList<PuzzleSiteDef> sites, int page)
        {
            var lines = new List<string>();
            var pages = Math.Max(1, (sites.Count + PageSize - 1) / PageSize);
            page = Math.Clamp(page, 1, pages);

            lines.Add($"{dungeonId}: {sites.Count} puzzle site(s), page {page}/{pages}.");

            for (var i = (page - 1) * PageSize; i < Math.Min(sites.Count, page * PageSize); i++)
                lines.Add(Describe(i, sites[i]));

            if (page < pages)
                lines.Add($"  (more: /puzzlegate site {dungeonId} page={page + 1})");

            return lines;
        }

        /// <summary>The valid dungeon ids (those with sites), sorted, for a "did you mean" line.</summary>
        public static string ValidDungeons(IEnumerable<string> dungeonIds)
            => string.Join(", ", dungeonIds.OrderBy(d => d, StringComparer.OrdinalIgnoreCase));

        /// <summary>The valid site ids of a dungeon, truncated to <paramref name="max"/> with a count of the rest.</summary>
        public static string ValidSites(IReadOnlyList<PuzzleSiteDef> sites, int max = 30)
        {
            var ids = sites.Where(s => s != null).Select(s => s.Id).ToList();
            var shown = string.Join(", ", ids.Take(max));
            return ids.Count > max ? $"{shown} (+{ids.Count - max} more; /puzzlegate site <dungeonId> lists them)" : shown;
        }
    }
}
