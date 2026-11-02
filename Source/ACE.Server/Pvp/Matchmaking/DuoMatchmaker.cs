using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Common;

namespace ACE.Server.Pvp
{
    /// <summary>
    /// 2v2 matchmaker: "duo vs duo first, then duo vs two solos after duo_vs_solo_after_seconds, or four solos in
    /// random teams" (Docs/Pvp/DESIGN.md "Modes" table). Premade duos keep priority because of their coordination
    /// advantage: phase 1 is duo vs duo, phase 2 is duo vs two solos once a duo has waited, and phase 3 (every call,
    /// after phases 1 and 2, no wait timer) pairs four solos into two randomly assigned teams of two. No rating
    /// window - 2v2 pairs whichever combination is available, since a rating-window search across duo averages was
    /// not specified.
    /// </summary>
    public sealed class DuoMatchmaker : IMatchmaker
    {
        public MatchProposal TryForm(IReadOnlyList<QueueEntrant> waiting, MatchmakingContext ctx)
        {
            var duos = waiting.Where(w => w.CharacterIds.Count == 2).OrderBy(w => w.QueuedAtUtc).ToList();
            var solos = waiting.Where(w => w.CharacterIds.Count == 1).OrderBy(w => w.QueuedAtUtc).ToList();

            for (var i = 0; i < duos.Count; i++)
            {
                // Fresh-opponent tiers (see PairingTiers): best tier wins, first in queue order within a tier; no Probe = first valid.
                var bestDuo = -1;
                var bestTier = int.MaxValue;

                for (var j = i + 1; j < duos.Count; j++)
                {
                    if (ctx.BlockSameIp && SharesIp(duos[i], duos[j]))
                        continue;

                    var tier = PairingTiers.Rate(ctx, new[] { duos[i] }, new[] { duos[j] });

                    if (tier == 0 || tier >= bestTier)
                        continue;

                    bestDuo = j;
                    bestTier = tier;

                    if (tier == PairingTiers.Best)
                        break;
                }

                if (bestDuo >= 0)
                {
                    var teamA = new PvpTeam(0, DuoParticipants(duos[i]));
                    var teamB = new PvpTeam(1, DuoParticipants(duos[bestDuo]));

                    return new MatchProposal(new List<PvpTeam> { teamA, teamB }, Rated: true);
                }
            }

            foreach (var duo in duos)
            {
                var waitedSeconds = (ctx.UtcNow - duo.QueuedAtUtc).TotalSeconds;

                if (waitedSeconds < ctx.DuoVsSoloAfterSeconds)
                    continue;

                QueueEntrant bestS1 = null;
                QueueEntrant bestS2 = null;
                var bestTier = int.MaxValue;

                for (var i = 0; i < solos.Count && bestTier != PairingTiers.Best; i++)
                {
                    for (var j = i + 1; j < solos.Count; j++)
                    {
                        var s1 = solos[i];
                        var s2 = solos[j];

                        if (ctx.BlockSameIp && (SharesIp(duo, s1) || SharesIp(duo, s2) || s1.IpKey != null && s1.IpKey == s2.IpKey))
                            continue;

                        var tier = PairingTiers.Rate(ctx, new[] { duo }, new[] { s1, s2 });

                        if (tier == 0 || tier >= bestTier)
                            continue;

                        bestS1 = s1;
                        bestS2 = s2;
                        bestTier = tier;

                        if (tier == PairingTiers.Best)
                            break;
                    }
                }

                if (bestS1 != null)
                {
                    var teamDuo = new PvpTeam(0, DuoParticipants(duo));
                    var teamSolos = new PvpTeam(1, new List<PvpParticipant>
                    {
                        new PvpParticipant(bestS1.CharacterIds[0], bestS1.Ratings[0], bestS1.IpKey),
                        new PvpParticipant(bestS2.CharacterIds[0], bestS2.Ratings[0], bestS2.IpKey)
                    });

                    return new MatchProposal(new List<PvpTeam> { teamDuo, teamSolos }, Rated: true);
                }
            }

            return TryFormSolos(solos, ctx);
        }

        /// <summary>
        /// Phase 3 scans at most this many of the oldest solos (C(16,4) = 1820 subsets, 3 splits each), so a long solo queue with no
        /// valid split cannot cost O(n^4) on the world thread. Earliest-subset-wins holds inside the window.
        /// </summary>
        internal const int MaxSolosScanned = 16;

        /// <summary>The two subset positions opposing position 0 and position k+1, for split k.</summary>
        private static readonly int[,] SplitOthers = { { 2, 3 }, { 1, 3 }, { 1, 2 } };

        /// <summary>
        /// Phase 3: four solos into two teams of two. Subsets of four from the oldest <see cref="MaxSolosScanned"/> solos are scanned
        /// in queue order; the best fresh-opponent tier wins, earliest subset on a tie (stopping at <see cref="PairingTiers.Best"/>).
        /// Within the chosen subset the split is drawn uniformly at random among the valid splits of that best tier, and one more
        /// draw decides which team is team 0 - those draws are the random team assignment.
        /// A split is valid when (with BlockSameIp) no player shares an IP with someone on the other team (teammates may share),
        /// and its tier is not 0 (an unpaid rematch refused).
        /// </summary>
        private static MatchProposal TryFormSolos(List<QueueEntrant> solos, MatchmakingContext ctx)
        {
            var w = Math.Min(solos.Count, MaxSolosScanned);

            if (w < 4)
                return null;

            var window = solos.GetRange(0, w);

            // Same-IP conflicts as a matrix, computed once; the probe is cached per ordered entrant pair for this call.
            var conflict = new bool[w, w];

            if (ctx.BlockSameIp)
                for (var i = 0; i < w; i++)
                    for (var j = 0; j < w; j++)
                        conflict[i, j] = i != j && SharesIp(window[i], window[j]);

            if (ctx.Probe != null)
            {
                var probe = ctx.Probe;
                var cache = new Dictionary<(Guid, Guid), (bool Recent, IReadOnlyList<bool> UnpaidA, IReadOnlyList<bool> UnpaidB)>();

                ctx = ctx with
                {
                    Probe = (x, y) =>
                    {
                        if (!cache.TryGetValue((x.EntrantId, y.EntrantId), out var hit))
                            cache[(x.EntrantId, y.EntrantId)] = hit = probe(x, y);

                        return hit;
                    }
                };
            }

            var bestTier = int.MaxValue;
            var bestIdx = new int[4];
            var bestTiers = new int[3];
            var idx = new int[4];
            var tiers = new int[3];
            var sideA = new QueueEntrant[2];
            var sideB = new QueueEntrant[2];

            for (var a = 0; a < w - 3 && bestTier != PairingTiers.Best; a++)
            {
                for (var b = a + 1; b < w - 2 && bestTier != PairingTiers.Best; b++)
                {
                    for (var c = b + 1; c < w - 1 && bestTier != PairingTiers.Best; c++)
                    {
                        for (var d = c + 1; d < w; d++)
                        {
                            idx[0] = a;
                            idx[1] = b;
                            idx[2] = c;
                            idx[3] = d;

                            var subsetBest = int.MaxValue;

                            // Split k: position 0 with position k+1 against the other two.
                            for (var k = 0; k < 3; k++)
                            {
                                tiers[k] = RateSplit(window, idx, k, ctx, conflict, sideA, sideB);

                                if (tiers[k] != 0 && tiers[k] < subsetBest)
                                    subsetBest = tiers[k];
                            }

                            if (subsetBest >= bestTier)
                                continue;

                            bestTier = subsetBest;
                            idx.CopyTo(bestIdx, 0);
                            tiers.CopyTo(bestTiers, 0);

                            if (bestTier == PairingTiers.Best)
                                break;
                        }
                    }
                }
            }

            if (bestTier == int.MaxValue)
                return null;

            var candidates = new List<int>(3);

            for (var k = 0; k < 3; k++)
                if (bestTiers[k] == bestTier)
                    candidates.Add(k);

            var pickIndex = ctx.PickIndex ?? (n => ThreadSafeRandom.Next(0, n - 1));
            var split = candidates[pickIndex(candidates.Count)];
            var swapSides = pickIndex(2) == 1;

            var first = new List<PvpParticipant>(2);
            var second = new List<PvpParticipant>(2);

            for (var m = 0; m < 4; m++)
            {
                var s = window[bestIdx[m]];
                var participant = new PvpParticipant(s.CharacterIds[0], s.Ratings[0], s.IpKey);

                if (m == 0 || m == split + 1)
                    first.Add(participant);
                else
                    second.Add(participant);
            }

            var teamZero = swapSides ? second : first;
            var teamOne = swapSides ? first : second;

            return new MatchProposal(new List<PvpTeam> { new PvpTeam(0, teamZero), new PvpTeam(1, teamOne) }, Rated: true);
        }

        /// <summary>The tier of split <paramref name="k"/> of the four solos at <paramref name="idx"/>, or 0 when the split is invalid.</summary>
        private static int RateSplit(List<QueueEntrant> window, int[] idx, int k, MatchmakingContext ctx, bool[,] conflict, QueueEntrant[] sideA, QueueEntrant[] sideB)
        {
            // Team A is position 0 with position k+1; team B is the remaining two.
            var a1 = k + 1;
            var b0 = SplitOthers[k, 0];
            var b1 = SplitOthers[k, 1];

            if (ctx.BlockSameIp
                && (conflict[idx[0], idx[b0]] || conflict[idx[0], idx[b1]] || conflict[idx[a1], idx[b0]] || conflict[idx[a1], idx[b1]]))
                return 0;

            sideA[0] = window[idx[0]];
            sideA[1] = window[idx[a1]];
            sideB[0] = window[idx[b0]];
            sideB[1] = window[idx[b1]];
            return PairingTiers.Rate(ctx, sideA, sideB);
        }
        private static List<PvpParticipant> DuoParticipants(QueueEntrant duo)
        {
            return new List<PvpParticipant>
            {
                new PvpParticipant(duo.CharacterIds[0], duo.Ratings[0], duo.IpKey),
                new PvpParticipant(duo.CharacterIds[1], duo.Ratings[1], duo.IpKey)
            };
        }

        private static bool SharesIp(QueueEntrant a, QueueEntrant b)
        {
            return a.IpKey != null && a.IpKey == b.IpKey;
        }
    }
}
