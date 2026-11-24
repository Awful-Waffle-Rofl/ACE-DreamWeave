using System;

using ACE.Entity.Enum;
using ACE.Server.Physics.Animation;

namespace ACE.Server.Pinnacles
{
    /// <summary>
    /// The pure math behind a hazard hit slow (PinnacleHitSlow, applied by Player.ApplyPinnacleHitSlow). Reads no
    /// PropertyManager and no world state.
    ///
    /// The run half is a Run-skill debuff, not a speed multiplier: the client and the server both derive run speed from
    /// the Run skill through MovementSystem.GetRunRate, so a skill debuff is the one lever both sides agree on. The
    /// curve is rate(s) = (11 s / (s + 200) + 4) / 4 below 800 and a flat 4.5 at 800 and above, so the points that
    /// cut the RATE by a fraction are found by inverting it: s' = 200 (4t - 4) / (15 - 4t).
    /// </summary>
    public static class PinnacleHitSlowMath
    {
        /// <summary>The highest Run skill still on the curve; 800 and above is the flat 4.5 cap.</summary>
        public const int HighestCurveSkill = 799;

        /// <summary>The stat-mod type a retail Leaden Feet VI (spell 1005) carries: Skill | SingleStat | Additive (36880).</summary>
        public const EnchantmentTypeFlags RunSlowStatModType = EnchantmentTypeFlags.Skill | EnchantmentTypeFlags.SingleStat | EnchantmentTypeFlags.Additive;

        /// <summary>The smallest attack-speed factor a slow can apply.</summary>
        public const double MinAttackFactor = 0.1;

        /// <summary>
        /// The Run-skill points to take from <paramref name="runUnslowed"/> so the run rate falls by
        /// <paramref name="fraction"/> (0.25 = a quarter slower). Never negative and never more than the skill. At 800+ the
        /// cut rate can sit above the curve's top (3.2 at 799), so the result never lands back above 799.
        /// </summary>
        public static int RunPointsFor(int runUnslowed, double fraction)
        {
            if (runUnslowed <= 0 || !(fraction > 0))
                return 0;

            if (fraction >= 1)
                return runUnslowed;

            var target = MovementSystem.GetRunRate(0f, runUnslowed, 1f) * (1 - fraction);

            var denominator = 15 - 4 * target;

            // at or above the curve's asymptote (3.75) the inverse has no finite answer: the nearest reachable skill below
            // the 800 jump is 799
            var slowed = denominator <= 0 ? HighestCurveSkill : 200 * (4 * target - 4) / denominator;

            if (runUnslowed >= 800 && slowed > HighestCurveSkill)
                slowed = HighestCurveSkill;

            var points = (int)Math.Round(runUnslowed - slowed);

            return Math.Clamp(points, 0, runUnslowed);
        }

        /// <summary>
        /// The attack-speed factor at <paramref name="now"/>: <paramref name="factor"/> clamped to [0.1, 1] while
        /// <paramref name="now"/> is before <paramref name="until"/>, else exactly 1. A NaN or infinite factor is 1.
        /// </summary>
        public static double AttackFactorAt(double factor, double until, double now)
        {
            if (!(now < until) || !double.IsFinite(factor))
                return 1.0;

            return Math.Clamp(factor, MinAttackFactor, 1.0);
        }
    }
}
