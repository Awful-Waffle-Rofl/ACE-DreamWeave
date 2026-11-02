using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.WorldEvents.Defs;

namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// Turns a presence multiplier m (= 2p, so 0..2) into concrete items from a reward pool. Pure: the pool,
    /// the multiplier and the Random are the only inputs.
    ///
    /// floor(m) full rolls each pick a weighted entry and give it at full quantity (a "bags" entry at full
    /// gives its own wcid, the item the bags stand in for). The remainder f = m - floor(m), if any, is one
    /// fractional roll that scales the pick by f according to the entry's scale: "stack" scales the
    /// quantity with stochastic rounding, "bags" pays stochastically-rounded Full Bags (a full set of bags
    /// collapses back to the item), "chance" awards the whole quantity with probability f and otherwise
    /// redraws among the non-chance entries. When the fractional roll is the ONLY roll and rounds to
    /// nothing, one unit is given so a present player is never paid zero. Duplicate wcids in the output are
    /// merged by summing quantity.
    /// </summary>
    public static class WorldEventRewardRoller
    {
        public static List<(uint Wcid, int Qty)> Roll(IReadOnlyList<RewardPoolEntryDef> pool, double m, Random rng)
        {
            var output = new List<(uint Wcid, int Qty)>();

            if (pool == null || pool.Count == 0 || double.IsNaN(m) || m <= 0)
                return output;

            rng = rng ?? new Random();

            var n = (int)Math.Floor(m);
            var f = m - n;

            for (var i = 0; i < n; i++)
            {
                var pick = WeightedPick(pool, rng, includeChance: true);

                if (pick != null)
                    Give(output, pick.Wcid, pick.Qty);
            }

            if (f > 0)
            {
                var pick = WeightedPick(pool, rng, includeChance: true);

                if (pick != null)
                {
                    var onlyRoll = n == 0;

                    if (pick.Scale == RewardPoolEntryDef.ScaleChance)
                    {
                        if (rng.NextDouble() < f)
                        {
                            Give(output, pick.Wcid, pick.Qty);
                        }
                        else
                        {
                            var redraw = WeightedPick(pool, rng, includeChance: false);

                            if (redraw != null)
                                Scaled(output, redraw, f, onlyRoll, rng);
                            else if (onlyRoll)
                                Give(output, pick.Wcid, pick.Qty);   // a pool of nothing but chance entries: never pay zero
                        }
                    }
                    else
                    {
                        Scaled(output, pick, f, onlyRoll, rng);
                    }
                }
            }

            return Merge(output);
        }

        /// <summary>Applies a non-chance entry's scale at fraction f, with the minimum-1 rule for a lone roll.</summary>
        private static void Scaled(List<(uint Wcid, int Qty)> output, RewardPoolEntryDef e, double f, bool onlyRoll, Random rng)
        {
            if (e.Scale == RewardPoolEntryDef.ScaleBags && e.BagWcid.HasValue && e.BagsPerUnit >= 1)
            {
                var b = StochasticRound(e.BagsPerUnit * f, rng);

                if (b >= e.BagsPerUnit)
                    Give(output, e.Wcid, 1);
                else if (b >= 1)
                    Give(output, e.BagWcid.Value, b);
                else if (onlyRoll)
                    Give(output, e.BagWcid.Value, 1);

                return;
            }

            // "stack", and the defensive fallback for a bags entry with no bag wcid
            var q = StochasticRound(e.Qty * f, rng);

            if (q < 1 && onlyRoll)
                q = 1;

            if (q >= 1)
                Give(output, e.Wcid, q);
        }

        /// <summary>floor(x) plus 1 with probability equal to the remainder.</summary>
        private static int StochasticRound(double x, Random rng)
        {
            var fl = Math.Floor(x);
            var rem = x - fl;

            return (int)fl + (rem > 0 && rng.NextDouble() < rem ? 1 : 0);
        }

        private static RewardPoolEntryDef WeightedPick(IReadOnlyList<RewardPoolEntryDef> pool, Random rng, bool includeChance)
        {
            double sum = 0;

            foreach (var e in pool)
            {
                if (Eligible(e, includeChance))
                    sum += e.Weight;
            }

            if (sum <= 0)
                return null;

            var roll = rng.NextDouble() * sum;
            RewardPoolEntryDef last = null;

            foreach (var e in pool)
            {
                if (!Eligible(e, includeChance))
                    continue;

                last = e;
                roll -= e.Weight;

                if (roll < 0)
                    return e;
            }

            return last;
        }

        private static bool Eligible(RewardPoolEntryDef e, bool includeChance)
        {
            return e != null && e.Weight > 0 && (includeChance || e.Scale != RewardPoolEntryDef.ScaleChance);
        }

        private static void Give(List<(uint Wcid, int Qty)> output, uint wcid, int qty)
        {
            if (qty > 0)
                output.Add((wcid, qty));
        }

        private static List<(uint Wcid, int Qty)> Merge(List<(uint Wcid, int Qty)> items)
        {
            var merged = new List<(uint Wcid, int Qty)>();
            var index = new Dictionary<uint, int>();

            foreach (var (wcid, qty) in items)
            {
                if (index.TryGetValue(wcid, out var at))
                    merged[at] = (wcid, merged[at].Qty + qty);
                else
                {
                    index[wcid] = merged.Count;
                    merged.Add((wcid, qty));
                }
            }

            return merged;
        }
    }
}