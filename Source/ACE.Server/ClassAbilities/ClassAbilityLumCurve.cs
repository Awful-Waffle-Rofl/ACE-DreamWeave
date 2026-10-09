using System;

namespace ACE.Server.ClassAbilities
{
    /// <summary>
    /// The luminance-purchase lane of the class-ability-point economy (DESIGN.md sec 2b): a piecewise
    /// geometric price curve with two breakpoints. Each successive point bought with Luminance costs more
    /// than the last, and there is NO cap on how many can be bought - the escalating price is the only
    /// limiter (the "no hard caps, power limited by price curves" philosophy, DESIGN sec 1).
    ///
    /// cost(n) = base * r1^(min(n, bp1) - 1) * r2^(clamp(n - bp1, 0, bp2 - bp1)) * r3^(max(n - bp2, 0))
    ///
    /// where n is the 1-indexed point number (the character's 1st, 2nd, ... luminance-bought point). With
    /// the design defaults (base 1M, r1 1.25, r2 1.5, r3 1.75, bp1 10, bp2 20) this yields roughly 1M at
    /// point 1, 7.5M at point 10, 430M at point 20, and ~2.3B cumulative by point 23.
    ///
    /// This type is the PURE, side-effect-free core so it can be unit-tested without a live Player; the
    /// live purchase (reading the tunables, spending available-then-banked Luminance, granting the points)
    /// is <c>Player.TryBuyClassAbilityPoints</c> / <c>Player.LumCostForClassAbilityPoints</c>.
    /// </summary>
    public static class ClassAbilityLumCurve
    {
        public const long DefaultBaseCost = 1_000_000;
        public const double DefaultRatio1 = 1.25;
        public const double DefaultRatio2 = 1.5;
        public const double DefaultRatio3 = 1.75;
        public const int DefaultBreakpoint1 = 10;
        public const int DefaultBreakpoint2 = 20;

        /// <summary>
        /// The Luminance cost of the <paramref name="n"/>th class ability point bought with Luminance
        /// (1-indexed). Returns 0 for n &lt; 1. Overflow past <see cref="long.MaxValue"/> is clamped so an
        /// absurd point number can never wrap - it just becomes effectively unaffordable.
        /// </summary>
        public static long CostForPoint(int n, long baseCost, double ratio1, double ratio2, double ratio3, int breakpoint1, int breakpoint2)
        {
            if (n < 1)
                return 0;

            return ToLong(RawCost(n, baseCost, ratio1, ratio2, ratio3, breakpoint1, breakpoint2));
        }

        /// <summary>
        /// The total Luminance cost of buying <paramref name="count"/> more points when
        /// <paramref name="alreadyPurchased"/> have already been bought (i.e. the sum of the curve over
        /// points alreadyPurchased+1 .. alreadyPurchased+count). Returns 0 for count &lt; 1; clamps to
        /// <see cref="long.MaxValue"/> rather than overflowing.
        /// </summary>
        public static long CostForRange(int alreadyPurchased, int count, long baseCost, double ratio1, double ratio2, double ratio3, int breakpoint1, int breakpoint2)
        {
            if (count < 1)
                return 0;

            var total = 0.0;

            for (var i = 1; i <= count; i++)
            {
                total += RawCost(alreadyPurchased + i, baseCost, ratio1, ratio2, ratio3, breakpoint1, breakpoint2);

                if (total >= long.MaxValue)
                    return long.MaxValue;
            }

            return ToLong(total);
        }

        private static double RawCost(int n, long baseCost, double ratio1, double ratio2, double ratio3, int breakpoint1, int breakpoint2)
        {
            // guard against a misconfigured breakpoint2 < breakpoint1 so the middle segment width is never negative
            var segment2Width = Math.Max(0, breakpoint2 - breakpoint1);

            var exp1 = Math.Min(n, breakpoint1) - 1;                       // ratio1 applies up to breakpoint1
            var exp2 = Math.Clamp(n - breakpoint1, 0, segment2Width);      // ratio2 between the breakpoints
            var exp3 = Math.Max(n - breakpoint2, 0);                       // ratio3 on the uncapped tail

            return baseCost * Math.Pow(ratio1, exp1) * Math.Pow(ratio2, exp2) * Math.Pow(ratio3, exp3);
        }

        private static long ToLong(double cost)
        {
            if (double.IsNaN(cost) || cost < 0)
                return 0;

            if (cost >= long.MaxValue)
                return long.MaxValue;

            return (long)Math.Round(cost);
        }
    }
}
