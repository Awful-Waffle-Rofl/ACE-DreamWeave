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
    /// nothing, one unit is given so a present player is never paid zero. Duplicate (wcid, itemMaxLevel) rows in
    /// the output are merged by summing quantity. An entry with a wcids list resolves to one uniformly chosen
    /// member per pick.
    /// </summary>
    public static class WorldEventRewardRoller
    {
        public static List<RolledReward> Roll(IReadOnlyList<RewardPoolEntryDef> pool, double m, Random rng)
        {
            var output = new List<RolledReward>();

            if (pool == null || pool.Count == 0 || double.IsNaN(m) || m <= 0)
                return output;

            rng = rng ?? new Random();

            var n = (int)Math.Floor(m);
            var f = m - n;

            for (var i = 0; i < n; i++)
            {
                var pick = WeightedPick(pool, rng, includeChance: true);

                if (pick != null)
                    Give(output, pick, pick.Qty, rng);
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
                            Give(output, pick, pick.Qty, rng);
                        }
                        else
                        {
                            var redraw = WeightedPick(pool, rng, includeChance: false);

                            if (redraw != null)
                                Scaled(output, redraw, f, onlyRoll, rng);
                            else if (onlyRoll)
                                Give(output, pick, pick.Qty, rng);   // a pool of nothing but chance entries: never pay zero
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
        private static void Scaled(List<RolledReward> output, RewardPoolEntryDef e, double f, bool onlyRoll, Random rng)
        {
            if (e.Scale == RewardPoolEntryDef.ScaleBags && e.BagWcid.HasValue && e.BagsPerUnit >= 1)
            {
                var b = StochasticRound(e.BagsPerUnit * f, rng);

                if (b >= e.BagsPerUnit)
                    Give(output, e, 1, rng);
                else if (b >= 1)
                    GiveRaw(output, e.BagWcid.Value, b, null);
                else if (onlyRoll)
                    GiveRaw(output, e.BagWcid.Value, 1, null);

                return;
            }

            // "stack", and the defensive fallback for a bags entry with no bag wcid
            var q = StochasticRound(e.Qty * f, rng);

            if (q < 1 && onlyRoll)
                q = 1;

            if (q >= 1)
                Give(output, e, q, rng);
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

        /// <summary>
        /// Adds qty of the entry's item. An entry with a wcids list resolves to one member, uniformly with rng,
        /// on every call (so two picks of one entry can differ). The entry's itemMaxLevel rides along.
        /// </summary>
        private static void Give(List<RolledReward> output, RewardPoolEntryDef e, int qty, Random rng)
        {
            var wcid = e.Wcid;

            if (e.Wcids != null && e.Wcids.Count > 0)
                wcid = e.Wcids[rng.Next(e.Wcids.Count)];

            GiveRaw(output, wcid, qty, e.ItemMaxLevel, e.KnotItem);
        }

        private static void GiveRaw(List<RolledReward> output, uint wcid, int qty, int? itemMaxLevel, string knotItem = null)
        {
            if (qty > 0)
                output.Add(new RolledReward(wcid, qty, itemMaxLevel, knotItem));
        }

        private static List<RolledReward> Merge(List<RolledReward> items)
        {
            var merged = new List<RolledReward>();
            var index = new Dictionary<(uint, int?, string), int>();

            foreach (var r in items)
            {
                var key = (r.Wcid, r.ItemMaxLevel, r.KnotItem);

                if (index.TryGetValue(key, out var at))
                    merged[at] = merged[at] with { Qty = merged[at].Qty + r.Qty };
                else
                {
                    index[key] = merged.Count;
                    merged.Add(r);
                }
            }

            return merged;
        }
    }

    /// <summary>One rolled output row. ItemMaxLevel and KnotItem, when set, are stamped on every object created for the row.</summary>
    public record RolledReward(uint Wcid, int Qty, int? ItemMaxLevel = null, string KnotItem = null);
}
