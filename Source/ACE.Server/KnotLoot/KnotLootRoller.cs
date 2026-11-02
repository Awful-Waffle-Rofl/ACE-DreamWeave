using System;
using System.Collections.Generic;

namespace ACE.Server.KnotLoot
{
    /// <summary>The settings the roller reads. Production is <see cref="LiveKnotLootSettings"/>; tests hand in a fake.</summary>
    public interface IKnotLootSettings
    {
        bool TableEnabled(string table);

        long Weight(string table, string entryKey, long fallback);

        /// <summary>Drop chance of a pick group, in [0,1]. Only consulted for a Gated group (see KnotLootPickGroup.Gated).</summary>
        double Chance(string table, string groupKey, double fallback);

        (long Min, long Max) Currency(string table, string currencyKey, (long, long) fallback);

        KnotWindowSettings Window(string table);
    }

    /// <summary>
    /// One rolled drop. <see cref="U"/> is the roll position: a trait drop stores it on the item as its trait position,
    /// a currency drop carries the u its count was rolled from, and a trait-less pick carries 0. The materializer
    /// consumes it only when <see cref="Trait"/> is set. <see cref="Mutation"/> is copied from the entry with no draw.
    /// </summary>
    public sealed record KnotLootDrop(uint Wcid, int StackSize, KnotTraitId? Trait, double U, KnotLootMutation Mutation = KnotLootMutation.None);

    /// <summary>
    /// Pure table roller. Draw order is pinned: per pick group in order, a Gated group first
    /// draws one gate u and is skipped (no further draws) when u >= its chance; then draw r, and if the chosen entry
    /// has a trait draw its u; then one u per currency in order. Output is picks then currencies. Gating is decided by
    /// the group static Gated flag, never by the live setting value, so the draw count does not depend on tuning
    /// (a live chance of 0 or 1 still consumes the gate draw). An ungated group makes no gate draw.
    /// </summary>
    public static class KnotLootRoller
    {
        public static List<KnotLootDrop> Roll(KnotLootTable table, int knotLevel, IKnotLootSettings s, Func<double> next)
        {
            var drops = new List<KnotLootDrop>();

            if (table == null || !s.TableEnabled(table.Key))
                return drops;

            var window = s.Window(table.Key);

            foreach (var group in table.Picks)
            {
                if (group.Gated)
                {
                    var gate = next();

                    if (!(gate < s.Chance(table.Key, group.Key, group.DefaultChance)))
                        continue;
                }

                var weights = new List<long>(group.Entries.Count);

                foreach (var entry in group.Entries)
                    weights.Add(s.Weight(table.Key, entry.Key, entry.DefaultWeight));

                var index = Choose(weights, next());

                if (index < 0)
                    continue;

                var chosen = group.Entries[index];

                if (chosen.Trait != null)
                    drops.Add(new KnotLootDrop(chosen.Wcid, 1, chosen.Trait, next(), chosen.Mutation));
                else
                    drops.Add(new KnotLootDrop(chosen.Wcid, 1, null, 0.0, chosen.Mutation));
            }

            foreach (var currency in table.Currency)
            {
                var u = next();
                var (min, max) = s.Currency(table.Key, currency.Key, (currency.DefaultMin, currency.DefaultMax));
                var count = Count(min, max, KnotLootWindow.Position(knotLevel, u, window));

                if (count > 0)
                    drops.Add(new KnotLootDrop(currency.Wcid, count, null, u));
            }

            return drops;
        }

        /// <summary>
        /// Weighted index for r in [0,1). Negative weights count as 0; all zero returns -1; r at or above 1 returns the
        /// last positive-weight entry.
        /// </summary>
        public static int Choose(IReadOnlyList<long> weights, double r)
        {
            double total = 0;
            var lastPositive = -1;

            for (var i = 0; i < weights.Count; i++)
            {
                if (weights[i] > 0)
                {
                    total += weights[i];
                    lastPositive = i;
                }
            }

            if (lastPositive < 0)
                return -1;

            if (double.IsNaN(r) || r < 0)
                r = 0;

            double cumulative = 0;

            for (var i = 0; i < weights.Count; i++)
            {
                if (weights[i] <= 0)
                    continue;

                cumulative += weights[i];

                if (r < cumulative / total)
                    return i;
            }

            return lastPositive;
        }

        /// <summary>Round-away-from-zero of min + p * (max - min), clamped to a non-negative int.</summary>
        public static int Count(long min, long max, double p)
        {
            var value = Math.Round(KnotLootWindow.Lerp(min, max, p), MidpointRounding.AwayFromZero);

            return (int)Math.Clamp(value, 0, int.MaxValue);
        }
    }
}
