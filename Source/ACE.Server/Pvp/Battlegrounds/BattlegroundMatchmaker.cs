using System;
using System.Collections.Generic;
using System.Linq;

namespace ACE.Server.Pvp.Battlegrounds
{
    /// <summary>
    /// The battleground matchmaker (Docs/Pvp/BATTLEGROUNDS.md "Matchmaking"): one queue, a fill window
    /// derived from the queue alone, the largest even team size the queue supports, and an exhaustive split
    /// into two teams of exactly that size. Pure: the clock is ctx.UtcNow and every value comes from ctx.Bg.
    /// Deterministic: ties break by queue time, then EntrantId, then split enumeration order.
    /// </summary>
    public sealed class BattlegroundMatchmaker : IMatchmaker
    {
        private const int MinTeamSize = 2;

        // The largest team a battleground holds (a full premade). Equal to the pvp_bg_max_premade_size ceiling.
        private const int MaxTeamSize = BattlegroundTunables.AbsoluteMaxPremadeSize;

        public MatchProposal TryForm(IReadOnlyList<QueueEntrant> waiting, MatchmakingContext ctx)
        {
            var dials = ctx.Bg;

            if (dials == null || waiting == null || waiting.Count == 0)
                return null;

            var units = waiting.OrderBy(u => u.QueuedAtUtc).ThenBy(u => u.EntrantId).ToList();
            var total = units.Sum(u => u.CharacterIds.Count);

            if (!FillWindowClosed(units, total, ctx.UtcNow, dials))
                return null;

            // Premades are units of 2 or more that fit a team; anything larger can never be placed.
            var premades = units.Where(u => u.CharacterIds.Count >= 2 && u.CharacterIds.Count <= Math.Min(dials.MaxPremadeSize, MaxTeamSize)).ToList();
            var solos = units.Where(u => u.CharacterIds.Count == 1).ToList();

            // pvp_bg_max_players caps the match (not only the form-now trigger): the team size starts at half of it,
            // never below the minimum team, and the players beyond 2s keep waiting for the next match.
            var largest = Math.Min(Math.Min(MaxTeamSize, Math.Max(MinTeamSize, dials.MaxPlayers / 2)), total / 2);

            for (var s = largest; s >= MinTeamSize; s--)
            {
                var chosen = ChooseUnits(premades, solos, s);

                if (chosen == null)
                    continue;

                var proposal = BestSplit(chosen, s, ctx, dials);

                if (proposal != null)
                    return proposal;
            }

            return null;
        }

        /// <summary>
        /// t4 is the queue time of the unit that brings the player count to MinPlayers. Form at MaxPlayers
        /// or once the window after t4 has closed.
        /// </summary>
        private static bool FillWindowClosed(List<QueueEntrant> orderedUnits, int total, DateTime now, BattlegroundDials dials)
        {
            if (total >= dials.MaxPlayers)
                return true;

            var close = FillWindowCloseUtc(orderedUnits.Select(u => (u.CharacterIds.Count, u.QueuedAtUtc)), dials);

            return close.HasValue && now >= close.Value;
        }

        /// <summary>
        /// When the fill window closes: t4 + pvp_bg_fill_window_seconds, where t4 is the queue time of the unit that
        /// brings the running player count to MinPlayers. Null while the queue is below MinPlayers (no window yet).
        /// <paramref name="orderedUnits"/> MUST already be ordered by queue time, then EntrantId - the matchmaker and the
        /// Arena Crier both feed it that order so they can never disagree on the window.
        /// </summary>
        public static DateTime? FillWindowCloseUtc(IEnumerable<(int Players, DateTime QueuedAtUtc)> orderedUnits, BattlegroundDials dials)
        {
            var running = 0;

            foreach (var u in orderedUnits)
            {
                running += u.Players;

                if (running >= dials.MinPlayers)
                    return u.QueuedAtUtc.AddSeconds(dials.FillWindowSeconds);
            }

            return null;
        }

        /// <summary>
        /// Premades oldest first while they can still be packed into two teams of s, then the oldest solos.
        /// If the solos run short, the newest chosen premade is dropped and the pick retried. Null when no
        /// 2s players can be assembled. Result is ordered oldest first (queue time, then EntrantId).
        /// </summary>
        private static List<QueueEntrant> ChooseUnits(List<QueueEntrant> premades, List<QueueEntrant> solos, int s)
        {
            var candidates = new List<QueueEntrant>(premades);

            while (true)
            {
                var chosen = new List<QueueEntrant>();

                foreach (var p in candidates)
                {
                    chosen.Add(p);

                    if (!Packable(chosen, s))
                        chosen.RemoveAt(chosen.Count - 1);
                }

                var need = 2 * s - chosen.Sum(p => p.CharacterIds.Count);

                if (solos.Count >= need)
                {
                    chosen.AddRange(solos.Take(need));
                    return chosen.OrderBy(u => u.QueuedAtUtc).ThenBy(u => u.EntrantId).ToList();
                }

                if (chosen.Count == 0)
                    return null;

                candidates.Remove(chosen[chosen.Count - 1]);
            }
        }

        /// <summary>True when the premades can be divided into two bins of at most s players each.</summary>
        private static bool Packable(List<QueueEntrant> premades, int s)
        {
            var total = premades.Sum(p => p.CharacterIds.Count);

            for (var mask = 0; mask < (1 << premades.Count); mask++)
            {
                var bin0 = 0;

                for (var i = 0; i < premades.Count; i++)
                    if ((mask & (1 << i)) != 0)
                        bin0 += premades[i].CharacterIds.Count;

                if (bin0 <= s && total - bin0 <= s)
                    return true;
            }

            return false;
        }

        private static MatchProposal BestSplit(List<QueueEntrant> units, int s, MatchmakingContext ctx, BattlegroundDials dials)
        {
            var n = units.Count;
            var bestKey = (int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue);
            var bestMask = -1;

            // Solo unit indices in snake order (rating desc, queue time, EntrantId); null when the snake draft is off.
            var soloOrder = dials.SnakeDraft ? SnakeSoloOrder(units) : null;

            // Unit 0 (the oldest) always sits on team 0, so a split and its mirror are one candidate.
            for (var mask = 1; mask < (1 << n); mask += 2)
            {
                if (!TryScore(units, mask, s, ctx, dials, soloOrder, out var key))
                    continue;

                if (bestMask < 0 || key.CompareTo(bestKey) < 0)
                {
                    bestKey = key;
                    bestMask = mask;
                }
            }

            if (bestMask < 0)
                return null;

            var west = new List<PvpParticipant>();
            var east = new List<PvpParticipant>();

            for (var i = 0; i < n; i++)
            {
                var target = (bestMask & (1 << i)) != 0 ? west : east;

                for (var m = 0; m < units[i].CharacterIds.Count; m++)
                    target.Add(new PvpParticipant(units[i].CharacterIds[m], units[i].Ratings[m], units[i].IpKeyOf(m)));
            }

            return new MatchProposal(new List<PvpTeam> { new PvpTeam(0, west), new PvpTeam(1, east) }, Rated: true);
        }

        /// <summary>
        /// Applies the hard constraints; on success yields the (premade imbalance, snake deviation, clan pairs, rating sum difference)
        /// preference key. The snake deviation is always 0 when <paramref name="soloOrder"/> is null (pvp_bg_snake_draft off), so the
        /// key then orders splits exactly as the three-part key it extends.
        /// </summary>
        private static bool TryScore(List<QueueEntrant> units, int mask, int s, MatchmakingContext ctx, BattlegroundDials dials, List<int> soloOrder, out (int, int, int, int) key)
        {
            key = default;

            var size0 = 0;

            for (var i = 0; i < units.Count; i++)
                if ((mask & (1 << i)) != 0)
                    size0 += units[i].CharacterIds.Count;

            if (size0 != s)
                return false;

            if (ctx.BlockSameIp && CrossTeamIpClash(units, mask))
                return false;

            var premade0 = 0;
            var premade1 = 0;
            QueueEntrant heaviest = null;
            var rating0 = 0;
            var rating1 = 0;

            for (var i = 0; i < units.Count; i++)
            {
                var onWest = (mask & (1 << i)) != 0;
                var u = units[i];
                var ratingSum = u.Ratings.Take(u.CharacterIds.Count).Sum();

                if (onWest)
                    rating0 += ratingSum;
                else
                    rating1 += ratingSum;

                if (u.CharacterIds.Count >= 2)
                {
                    if (onWest)
                        premade0 += u.CharacterIds.Count;
                    else
                        premade1 += u.CharacterIds.Count;
                }
            }

            var imbalance = Math.Abs(premade0 - premade1);

            if (imbalance > dials.PremadeImbalanceTolerance)
            {
                // The most unbalanced premade: the largest one on the heavier side, oldest on a tie.
                var heavierIsWest = premade0 > premade1;

                for (var i = 0; i < units.Count; i++)
                {
                    var onWest = (mask & (1 << i)) != 0;

                    if (onWest != heavierIsWest || units[i].CharacterIds.Count < 2)
                        continue;

                    if (heaviest == null || units[i].CharacterIds.Count > heaviest.CharacterIds.Count)
                        heaviest = units[i];
                }

                if (heaviest == null || (ctx.UtcNow - heaviest.QueuedAtUtc).TotalSeconds < dials.PremadeVsSoloAfterSeconds)
                    return false;
            }

            key = (imbalance, soloOrder != null ? SnakeDeviation(units, soloOrder, mask, s) : 0, dials.SplitClanmates ? ClanPairs(units, mask) : 0, Math.Abs(rating0 - rating1));
            return true;
        }

        /// <summary>Indices of the solo units, best rating first, then oldest queue time, then EntrantId.</summary>
        private static List<int> SnakeSoloOrder(List<QueueEntrant> units)
        {
            return Enumerable.Range(0, units.Count)
                .Where(i => units[i].CharacterIds.Count == 1)
                .OrderByDescending(i => units[i].Ratings[0])
                .ThenBy(i => units[i].QueuedAtUtc)
                .ThenBy(i => units[i].EntrantId)
                .ToList();
        }

        /// <summary>
        /// How many solos sit on a different team than the Elo snake draft would put them. Premades keep the side the split gave
        /// them; each team's spare places are s minus its premade players. The solos, best first, are dealt A, B, B, A, A, B, B, A ...
        /// skipping a team that is full. A is the team with the lower premade rating sum; only on an exact tie (no premades
        /// included) are both labellings (A = team 0, A = team 1) scored and the smaller count kept. Swapping the sides swaps the
        /// sums, so a split and its mirror score the same. Pure.
        /// </summary>
        private static int SnakeDeviation(List<QueueEntrant> units, List<int> soloOrder, int mask, int s)
        {
            var spare0 = s;
            var spare1 = s;
            var premadeRating0 = 0;
            var premadeRating1 = 0;

            for (var i = 0; i < units.Count; i++)
            {
                var u = units[i];

                if (u.CharacterIds.Count < 2)
                    continue;

                var ratingSum = u.Ratings.Take(u.CharacterIds.Count).Sum();

                if ((mask & (1 << i)) != 0)
                {
                    spare0 -= u.CharacterIds.Count;
                    premadeRating0 += ratingSum;
                }
                else
                {
                    spare1 -= u.CharacterIds.Count;
                    premadeRating1 += ratingSum;
                }
            }

            // A is the team with the lower premade rating sum. Only an exact tie (which includes no premades) tries both labellings.
            var best = int.MaxValue;

            for (var aIsTeam0 = 0; aIsTeam0 < 2; aIsTeam0++)
            {
                if (premadeRating0 != premadeRating1 && (aIsTeam0 == 1) != (premadeRating0 < premadeRating1))
                    continue;

                var spare = new[] { spare0, spare1 };
                var deviation = 0;

                for (var k = 0; k < soloOrder.Count; k++)
                {
                    // A, B, B, A, A, B, B, A ...: slot k is A when k mod 4 is 0 or 3.
                    var wantsA = (k & 3) == 0 || (k & 3) == 3;
                    var team = wantsA == (aIsTeam0 == 1) ? 0 : 1;

                    if (spare[team] <= 0)
                        team = 1 - team;

                    spare[team]--;

                    var actual = (mask & (1 << soloOrder[k])) != 0 ? 0 : 1;

                    if (actual != team)
                        deviation++;
                }

                best = Math.Min(best, deviation);
            }

            return best;
        }

        private static bool CrossTeamIpClash(List<QueueEntrant> units, int mask)
        {
            var west = new HashSet<string>();
            var east = new HashSet<string>();

            for (var i = 0; i < units.Count; i++)
            {
                var side = (mask & (1 << i)) != 0 ? west : east;

                for (var m = 0; m < units[i].CharacterIds.Count; m++)
                {
                    var ip = units[i].IpKeyOf(m);

                    if (ip != null)
                        side.Add(ip);
                }
            }

            return west.Overlaps(east);
        }

        /// <summary>Same-MonarchId solo pairs that share a team (MonarchId 0 means no allegiance).</summary>
        private static int ClanPairs(List<QueueEntrant> units, int mask)
        {
            var west = new Dictionary<uint, int>();
            var east = new Dictionary<uint, int>();

            for (var i = 0; i < units.Count; i++)
            {
                var u = units[i];

                if (u.CharacterIds.Count != 1 || u.MonarchIds == null || u.MonarchIds.Count == 0 || u.MonarchIds[0] == 0)
                    continue;

                var side = (mask & (1 << i)) != 0 ? west : east;
                side[u.MonarchIds[0]] = side.TryGetValue(u.MonarchIds[0], out var c) ? c + 1 : 1;
            }

            return west.Values.Concat(east.Values).Sum(c => c * (c - 1) / 2);
        }
    }
}
