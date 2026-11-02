using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.WorldEvents.Defs;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// The archetype-weighted family draw (dynamic_dungeons_family_archetype_weighting, 2026-10-27), pure.
    ///
    /// THE LEAK IT CLOSES. When a run's family-eligibility band widens (too few species tables field a trash member in
    /// the natural band), the legacy draw picks among every family the WIDER band admits by preference weight alone. Each
    /// family is one species, and a widened-in family is often a single archetype (all casters, all tanks), so the more
    /// families the widening admits, the further a run's expected archetype mix drifts from what the natural band fields.
    /// The archetype uplift then normalizes each pick against its OWN archetype's standard, so the drift survives the
    /// uplift as a different fight.
    ///
    /// THE RULE. q(a) is the archetype mix an UN-widened run fields - the legacy draw's expected trash mix over the families
    /// eligible in the natural band (DungeonPopulationBuilder.UnwidenedMix) - p_f(a) each eligible family's
    /// trash-pool mix (<see cref="FamilyMix"/>) and pref_f its legacy preference weight. The matched weight
    /// <code>
    ///   W_f = sum over a of  q(a) x pref_f x p_f(a) / sum over g of (pref_g x p_g(a))
    /// </code>
    /// shares each archetype's mass among the families that carry it in proportion to their legacy weight (an archetype
    /// none offers drops out and W is renormalized). When every family's pool is a single archetype that closed form
    /// makes the expected mix sum_f W_f p_f(a) equal q(a) exactly; a family that mixes archetypes drags mass along with
    /// it, so the closed form only approximates q. It is therefore the START of <see cref="EmIterations"/> deterministic
    /// EM steps, w_f &lt;- w_f x sum over a of q'(a) p_f(a) / m(a) with m the current expected mix and q' the q over
    /// offered archetypes, each of which moves the expected mix toward q (it lowers KL(q' || m)) and leaves an exact
    /// single-archetype answer where it is. Every family keeps at least <see cref="LegacyFloorFraction"/> of its legacy
    /// share, and the final share is (1 - s) x legacy + s x matched, s the tunable.
    ///
    /// Every sum walks families in the order given (the caller's Id-ordinal list) and archetypes in enum order
    /// (<see cref="Order"/>); nothing iterates a dictionary.
    /// </summary>
    public static class FamilyArchetypeWeighting
    {
        /// <summary>Every archetype, Unclassified included, in enum order: the index order of every mix vector here.</summary>
        public static readonly CreatureArchetype[] Order =
        {
            CreatureArchetype.Unclassified, CreatureArchetype.Caster, CreatureArchetype.Ranged,
            CreatureArchetype.MeleeTank, CreatureArchetype.MeleeDps, CreatureArchetype.Hybrid,
        };

        /// <summary>The share of its legacy share every eligible family keeps under the matched weights.</summary>
        public const double LegacyFloorFraction = 0.1;

        private static int IndexOf(CreatureArchetype a)
        {
            var i = Array.IndexOf(Order, a);
            return i < 0 ? 0 : i;
        }

        /// <summary>p_f (pure): one family's trash-pool mix, as shares in <see cref="Order"/> (each pool entry weighs 1, the uniform draw).</summary>
        public static double[] FamilyMix(IEnumerable<uint> pool, Func<uint, CreatureArchetype> archetypeOf)
        {
            var counts = new double[Order.Length];

            if (pool != null && archetypeOf != null)
                foreach (var wcid in pool)
                    counts[IndexOf(archetypeOf(wcid))]++;

            return Normalize(counts);
        }

        private static double[] Normalize(double[] counts)
        {
            var total = counts.Sum();

            if (total > 0)
                for (var i = 0; i < counts.Length; i++)
                    counts[i] /= total;

            return counts;
        }

        /// <summary>The legacy shares (pure): each preference weight over their sum; uniform when every weight is non-positive.</summary>
        public static double[] LegacyShares(IReadOnlyList<double> preference)
        {
            var n = preference?.Count ?? 0;
            var shares = new double[n];
            var total = 0.0;

            for (var i = 0; i < n; i++)
                total += Positive(preference[i]);

            for (var i = 0; i < n; i++)
                shares[i] = total > 0 ? Positive(preference[i]) / total : 1.0 / n;

            return shares;
        }

        private static double Positive(double v) => double.IsNaN(v) || double.IsInfinity(v) || v < 0 ? 0.0 : v;

        /// <summary>The fixed number of EM steps <see cref="Matched"/> runs from the closed form (deterministic, no convergence test).</summary>
        // 1000, not 50: when the best fit needs a family at weight 0 (q on the boundary) EM closes in slowly - on the five-family
        // fixture in FamilyArchetypeWeightingTests 50 steps left a 0.009 miss and 1000 land within 0.001. Each step is O(families x 6).
        public const int EmIterations = 1000;

        /// <summary>
        /// The matched weights W_f (pure, before the floor and the blend), normalized to sum to 1: the closed form
        /// (<see cref="MatchedClosedForm"/>) refined by <see cref="EmIterations"/> EM steps toward q. Families walk in the
        /// order given, archetypes in <see cref="Order"/>. Null when no family offers any archetype q carries.
        /// </summary>
        public static double[] Matched(IReadOnlyList<double> preference, IReadOnlyList<double[]> familyMixes, IReadOnlyList<double> q)
        {
            var w = MatchedClosedForm(preference, familyMixes, q);

            if (w == null)
                return null;

            var n = w.Length;

            // q' : q over the archetypes some family offers, renormalized (the closed form drops the rest the same way)
            var offered = new double[Order.Length];
            var offeredTotal = 0.0;

            for (var a = 0; a < Order.Length; a++)
            {
                var any = false;

                for (var f = 0; f < n && !any; f++)
                    any = familyMixes[f][a] > 0;

                if (any && q[a] > 0)
                {
                    offered[a] = q[a];
                    offeredTotal += q[a];
                }
            }

            if (!(offeredTotal > 0))
                return w;

            for (var a = 0; a < Order.Length; a++)
                offered[a] /= offeredTotal;

            var m = new double[Order.Length];
            var next = new double[n];

            for (var step = 0; step < EmIterations; step++)
            {
                for (var a = 0; a < Order.Length; a++)
                {
                    m[a] = 0.0;

                    for (var f = 0; f < n; f++)
                        m[a] += w[f] * familyMixes[f][a];
                }

                var total = 0.0;

                for (var f = 0; f < n; f++)
                {
                    var factor = 0.0;

                    for (var a = 0; a < Order.Length; a++)
                        if (offered[a] > 0 && m[a] > 0)
                            factor += offered[a] * familyMixes[f][a] / m[a];

                    next[f] = w[f] * factor;
                    total += next[f];
                }

                if (!(total > 0))
                    break;

                for (var f = 0; f < n; f++)
                    w[f] = next[f] / total;
            }

            return w;
        }

        /// <summary>
        /// The closed-form matched weights (pure): W_f = sum over a of q(a) x pref_f x p_f(a) / sum over g of (pref_g x
        /// p_g(a)), normalized to sum to 1; null when no family offers any archetype q carries. Exact only for
        /// single-archetype pools; <see cref="Matched"/> refines it.
        /// </summary>
        public static double[] MatchedClosedForm(IReadOnlyList<double> preference, IReadOnlyList<double[]> familyMixes, IReadOnlyList<double> q)
        {
            var n = preference?.Count ?? 0;

            if (n == 0 || familyMixes == null || familyMixes.Count != n || q == null || q.Count != Order.Length)
                return null;

            var w = new double[n];

            for (var a = 0; a < Order.Length; a++)
            {
                if (!(q[a] > 0))
                    continue;

                var denom = 0.0;

                for (var g = 0; g < n; g++)
                    denom += Positive(preference[g]) * familyMixes[g][a];

                if (!(denom > 0))
                    continue;

                for (var f = 0; f < n; f++)
                    w[f] += q[a] * Positive(preference[f]) * familyMixes[f][a] / denom;
            }

            var total = w.Sum();

            if (!(total > 0))
                return null;

            for (var f = 0; f < n; f++)
                w[f] /= total;

            return w;
        }

        /// <summary>
        /// The final draw shares (pure): (1 - s) x legacy + s x floored matched, where the matched weights are floored at
        /// <see cref="LegacyFloorFraction"/> x each family's legacy share and renormalized. s is clamped to [0, 1] (NaN
        /// reads 0). With nothing to match, or s = 0, the legacy shares. Every share is positive when every preference is.
        /// </summary>
        public static double[] Weights(IReadOnlyList<double> preference, IReadOnlyList<double[]> familyMixes, IReadOnlyList<double> q, double s)
        {
            var legacy = LegacyShares(preference);
            var blend = double.IsNaN(s) ? 0.0 : Math.Clamp(s, 0.0, 1.0);
            var matched = blend > 0 ? Matched(preference, familyMixes, q) : null;

            if (matched == null)
                return legacy;

            var floored = new double[legacy.Length];

            for (var f = 0; f < legacy.Length; f++)
                floored[f] = Math.Max(matched[f], LegacyFloorFraction * legacy[f]);

            var total = floored.Sum();
            var result = new double[legacy.Length];

            for (var f = 0; f < legacy.Length; f++)
                result[f] = (1.0 - blend) * legacy[f] + blend * floored[f] / total;

            return result;
        }

        /// <summary>The expected archetype mix of a family draw (pure): sum over f of share_f x p_f(a), in <see cref="Order"/>.</summary>
        public static double[] ExpectedMix(IReadOnlyList<double> shares, IReadOnlyList<double[]> familyMixes)
        {
            var mix = new double[Order.Length];

            for (var f = 0; f < (shares?.Count ?? 0); f++)
                for (var a = 0; a < Order.Length; a++)
                    mix[a] += shares[f] * familyMixes[f][a];

            return mix;
        }
    }
}
