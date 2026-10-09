using System;
using System.Collections.Generic;

namespace ACE.Server.Entity
{
    /// <summary>
    /// Pure damage-attribution arithmetic for one damage-over-time tick, extracted from
    /// EnchantmentManager.ApplyDamageTick so it can be tested without a live Creature, Player or landblock.
    ///
    /// WHAT THIS EXISTS TO GUARANTEE. A DoT tick is accumulated RAW and uncapped across every contributing
    /// enchantment, the victim's absorbers (Sanguine Ward, then Mana Barrier) run against that whole total,
    /// and only then is the result capped to the health actually available. The vital write is deliberately
    /// left uncapped - UpdateVitalDelta floors Health at zero by itself, which is what lets an overkill tick
    /// kill. But the per-damager CREDITS must not be: damage history and the per-damager combat lines feed
    /// kill attribution, so crediting anyone for damage that was never dealt hands out kill credit sized off
    /// the nominal roll rather than the hit.
    ///
    /// So the credits are split by RUNNING CUMULATIVE PROPORTION rather than by rounding each damager's
    /// share independently. Rounding shares independently lets the rounding error accumulate, so N damagers
    /// can collectively be credited with up to N/2 points more (or less) than was dealt; taking the
    /// difference of successive rounded cumulative totals cannot, because every point credited to one
    /// damager is a point the next one does not get. The result sums EXACTLY to the applied total and no
    /// single entry can exceed it.
    ///
    /// This class is the reason the accumulation order is fixed by the caller before either pass runs: it
    /// iterates the same list twice and relies on <see cref="Total"/> and the running sum inside
    /// <see cref="Split"/> performing the identical float additions in the identical order.
    ///
    /// <see cref="GroupByLastContribution"/> fixes that order, and its ordering is load-bearing for a
    /// second, unrelated reason - it decides who is recorded as landing the killing blow. See its own doc
    /// comment before changing it.
    /// </summary>
    public static class DotTickCredits
    {
        /// <summary>
        /// Totals each damager's contributions to one tick and returns them in LAST-CONTRIBUTION order:
        /// the damager whose most recent contribution came latest sorts last.
        ///
        /// THE ORDER IS THE POINT, NOT A CONVENIENCE. EnchantmentManager.ApplyDamageTick replays
        /// DamageHistory.Add over this result, and DamageHistory.LastDamager resolves to the attacker on
        /// the LAST negative-amount log entry - so this order decides who landed the killing blow on a
        /// finishing tick. The original code called Add once per enchantment as it walked the list, which
        /// is last-contribution order by construction; grouping the contributions first and replaying them
        /// in first-insertion order (what a plain Dictionary enumeration gives, since updating a value does
        /// not move its key) silently reassigns the kill. For three top-layer DoTs in list order
        /// [A, B, A] - one caster holding two spell categories on the target, an ordinary stacked-DoT raid
        /// shape - first-insertion order yields [A, B] and hands the kill to B, where the original yields
        /// [B, A] and hands it to A.
        ///
        /// Downstream consumers of LastDamager that this would break: Player_KillFillVessel and
        /// Player_MuleFormToken (both credit ONLY the killing blow, deliberately, over a fellow who did
        /// more damage), the death and kill messages in Monster_Combat / Player_Combat, and the
        /// TargetingTactic.LastDamager aggro tactic.
        ///
        /// Reordering here is safe for <see cref="Total"/> and <see cref="Split"/> as long as the amounts
        /// travel with their keys: both are order-sensitive only in which entry absorbs the rounding
        /// remainder, which was already unspecified.
        /// </summary>
        public static List<KeyValuePair<T, float>> GroupByLastContribution<T>(IReadOnlyList<KeyValuePair<T, float>> contributions)
        {
            var order = new List<T>();
            var totals = new Dictionary<T, float>();

            if (contributions == null)
                return new List<KeyValuePair<T, float>>();

            for (var i = 0; i < contributions.Count; i++)
            {
                var key = contributions[i].Key;

                if (totals.ContainsKey(key))
                {
                    totals[key] += contributions[i].Value;

                    // move to the end: this contribution is now the damager's most recent one
                    order.Remove(key);
                }
                else
                {
                    totals.Add(key, contributions[i].Value);
                }

                order.Add(key);
            }

            var grouped = new List<KeyValuePair<T, float>>(order.Count);

            foreach (var key in order)
                grouped.Add(new KeyValuePair<T, float>(key, totals[key]));

            return grouped;
        }

        /// <summary>
        /// The raw, uncapped tick total: the sum of every damager's contribution, in list order. This is
        /// the number the victim's absorbers must be fed - never a health-capped share of it, which is the
        /// defect that let a warded or barriered victim survive a DoT of any size.
        /// </summary>
        public static float Total(IReadOnlyList<float> rawAmounts)
        {
            var total = 0.0f;

            if (rawAmounts == null)
                return total;

            for (var i = 0; i < rawAmounts.Count; i++)
                total += rawAmounts[i];

            return total;
        }

        /// <summary>
        /// Splits <paramref name="appliedTotal"/> - the damage actually removed from the victim, after the
        /// absorbers and after the health cap - across the damagers in proportion to what each of them
        /// actually contributed to the raw tick.
        ///
        /// Returns one credit per entry of <paramref name="rawAmounts"/>, in the same order. The credits
        /// always sum to exactly <paramref name="appliedTotal"/>, and no single credit exceeds it. Returns
        /// all zeroes when there is nothing to apply or nothing was dealt.
        /// </summary>
        public static uint[] Split(IReadOnlyList<float> rawAmounts, uint appliedTotal)
        {
            var credits = new uint[rawAmounts?.Count ?? 0];

            if (credits.Length == 0 || appliedTotal == 0)
                return credits;

            var total = Total(rawAmounts);

            if (!(total > 0.0f))
                return credits;

            var running = 0.0f;
            uint assigned = 0;

            for (var i = 0; i < rawAmounts.Count; i++)
            {
                running += rawAmounts[i];

                var cumulative = (uint)Math.Round(running / total * appliedTotal);

                if (cumulative > appliedTotal)
                    cumulative = appliedTotal;

                // the running sum is nondecreasing for any real DoT term; the guard stops a pathological
                // negative contribution from underflowing the unsigned subtraction below
                if (cumulative < assigned)
                    cumulative = assigned;

                credits[i] = cumulative - assigned;
                assigned = cumulative;
            }

            // By construction the final cumulative is Round(1.0 * appliedTotal) == appliedTotal, because
            // `running` performs the same float additions in the same order as Total. This sweep-up is a
            // guard against a pathological term (a negative contribution clamped above, a denormal) leaving
            // the split short, not an expected path - without it such a case would silently under-credit.
            if (assigned < appliedTotal)
                credits[credits.Length - 1] += appliedTotal - assigned;

            return credits;
        }
    }
}
