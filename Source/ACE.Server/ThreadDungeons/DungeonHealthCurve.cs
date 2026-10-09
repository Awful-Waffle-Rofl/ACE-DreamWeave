using System;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// The band-standard health curve: what a creature at level L in a Thread run OUGHT to carry, as one
    /// smooth monotone function of the level rather than as whatever the authored content happens to hold
    /// there.
    ///
    /// <code>
    ///   T(L) = AnchorLow * g^(L - AnchorLowLevel),   g = (AnchorHigh / AnchorLow) ^ (1 / (AnchorHighLevel - AnchorLowLevel))
    /// </code>
    ///
    /// g IS DERIVED FROM THE TWO ANCHORS AND IS NEVER A SECOND LITERAL, and that is the whole point of the
    /// shape: a per-level ratio written out as its own constant alongside the anchors is a value that CAN
    /// disagree with them, and the first time an admin moves an anchor the curve silently stops passing
    /// through the endpoint it is documented to pass through. Derived, the two anchors are the only inputs
    /// and the endpoints are exact by construction. With the compiled defaults g is about 1.01425, i.e.
    /// +1.43% per level and +15.2% over a 10-level Raw Fragment rung.
    ///
    /// WHY THIS EXISTS. Before it, a run's trash health was whatever the drawn weenie authored, floored at
    /// <see cref="DungeonSpawnPlan.TrashHealthFloor"/> = the cross-family band median times
    /// <see cref="DungeonPopulationLimits.TrashHealthFloorRatio"/>. That median is a live measurement over
    /// the species tables, so the effective difficulty curve inherited the authored content's lumpiness:
    /// measured against the local ace_world on 2026-09-12 at the Raw Fragment rungs it climbed 890 -> 10100
    /// over levels 185 to 245 (an 11x climb over six rungs) and then sat nearly flat from 245 to 375 (9570,
    /// 8250, 8200, 9400, 11250, 13100, 13100 - falling for three rungs before resuming), because the
    /// high-level bands are drawn from the same small set of top-level creatures. The owner's ruling is that
    /// the ladder be smooth and monotone, so the curve becomes the STANDARD and the measured median becomes
    /// the DENOMINATOR a drawn creature is normalized against
    /// (<see cref="DungeonSpawnPlan.HealthNormalizeRatio"/>).
    ///
    /// ANCHORS ARE THE TWO ENDPOINTS OF THE MEASURED CURVE, not invented numbers: 890 is the measured
    /// cross-family band median at level 185 (the lowest Raw Fragment rung) and 13100 is the measured median
    /// at level 375 (the gem ceiling, <see cref="DungeonGemSpec.MaxGemLevel"/>). So T(185) and T(375) reproduce
    /// today's two endpoints exactly and everything between them is interpolated rather than sampled.
    ///
    /// THE CURVE OWNS THE WHOLE LADDER 1 to its top level, not just the Raw Fragment rungs, and below 185 it extends
    /// downward on the same ratio rather than deferring to the sample. It does NOT track the sampled medians
    /// closely down there and is not meant to: measured on the local ace_world on 2026-09-12 it runs BELOW
    /// them over most of the sub-185 range (level 150: curve 542 against a sampled median of 1230; level 100:
    /// 267 against 475; level 50: 132 against 157) and ABOVE them only at the very bottom, where the bands
    /// hold a handful of near-zero-health weenies (level 10: 75 against 50). What it buys is monotonicity,
    /// which the sample does not have - the sampled medians step DOWN from 1230 hp at level 160 to 810 at 170
    /// where the curve gives 625 then 720. Sub-185 gems are admin-issued only (the shipped Raw Fragment rungs
    /// start at 185), so this range is a correctness property rather than a tuning one.
    ///
    /// ABOVE <see cref="AnchorHighLevel"/> IT EXTRAPOLATES ON THE SAME RATIO, up to a configurable top level
    /// (dynamic_dungeons_health_curve_top_level, default <see cref="DefaultTopLevel"/> = 500), and clamps there.
    /// Until 2026-10-08 it clamped AT 375, on the reasoning that no authored content above 375 existed to
    /// measure against and no gem could be rolled above it. The second half stopped being true with the
    /// owner's run ceiling split (DungeonGemSpec.MaxRunLevel: a 375 fragment pressed with a Mana Scarab plays at
    /// 410), and the owner ruled the first half acceptable: every monster stat follows its natural curve past
    /// the authored data, and very hard to impossible for a solo player at the top is intended. A 375 clamp
    /// would have made every pressed run above 375 field exactly the 375 pack's health, so a +35 scarab bought
    /// nothing but a reward bump. The extrapolation is the measured per-level ratio g carried forward, not a
    /// new fit: T(400) 18661, T(425) 26583, T(450) 37868, T(475) 53944, T(500) 76845. A top level of 375
    /// restores the old clamp exactly. <see cref="DungeonStatCurve"/> carries the same decision for every
    /// other stat axis.
    ///
    /// A level of 0 or below returns 0, which every caller reads as "no curve" - the same no-op convention
    /// <see cref="DungeonSpawnPlan.TrashHealthFloor"/> and <see cref="DungeonBandStandard.Empty"/> already
    /// use.
    ///
    /// PURE, and deliberately so: nothing here reads PropertyManager. The two anchors and the master switch
    /// arrive on a <see cref="DungeonPopulationLimits"/> that the caller (ThreadDungeonSpawner) resolved and
    /// sanitized, exactly as every other Threads dial does, because a PropertyManager read throws under the
    /// unit-test harness. The anchors are nonetheless re-sanitized HERE as well, on the same defensive
    /// contract as <see cref="DungeonRewardMath.RewardScaleRatio"/>: a limits struct built directly in code
    /// (every unit test) has no such caller, and a NaN must not propagate into every creature's health.
    /// </summary>
    public static class DungeonHealthCurve
    {
        /// <summary>
        /// The measured cross-family band median health at <see cref="AnchorLowLevel"/>, and the compiled
        /// default for dynamic_dungeons_health_curve_anchor_low. Shared with the PropertyManager
        /// registration so the two cannot drift.
        /// </summary>
        public const double DefaultAnchorLow = 890.0;

        /// <summary>
        /// The measured cross-family band median health at <see cref="AnchorHighLevel"/>, and the compiled
        /// default for dynamic_dungeons_health_curve_anchor_high.
        /// </summary>
        public const double DefaultAnchorHigh = 13100.0;

        /// <summary>
        /// The level <see cref="DefaultAnchorLow"/> was measured at - the lowest Raw Fragment rung, and the
        /// level the curve passes through exactly. NOT a tunable: moving it would change which measurement
        /// the anchor value is a measurement OF, and the anchor would then be a number with no provenance.
        /// </summary>
        public const int AnchorLowLevel = 185;

        /// <summary>
        /// The level <see cref="DefaultAnchorHigh"/> was measured at - the top of the measured data, and the
        /// LOWEST value the top level (<see cref="SanitizeTopLevel"/>) may take. Equal to
        /// <see cref="DungeonGemSpec.MaxGemLevel"/>, and that is not a coincidence worth encoding as a
        /// reference: the two are the same number for different reasons (the gem ceiling is a design choice,
        /// this is where 13100 was measured), and it stays 375 through the 2026-10-08 run ceiling raise for
        /// exactly that reason - moving it would change which measurement the high anchor is a measurement OF.
        /// Since that raise the curve no longer clamps here; it extrapolates to the top level instead.
        /// </summary>
        public const int AnchorHighLevel = 375;

        /// <summary>
        /// The compiled default of dynamic_dungeons_health_curve_top_level: the level at and above which the
        /// curve clamps. The RUN ceiling (<see cref="DungeonGemSpec.MaxRunLevel"/>), so by default every playable
        /// run level sits on the extrapolated curve and nothing above it is reachable anyway.
        /// </summary>
        public const int DefaultTopLevel = DungeonGemSpec.MaxRunLevel;

        /// <summary>
        /// Typo ceilings for the two anchors, in the style of
        /// <see cref="DungeonPopulationLimits.MaxTrashHealthFloorRatio"/> and for the same reason: both are
        /// LIVE, editable with one /pm command, with no content-lint gate in front of them, and they sit in
        /// front of every creature's health in every run opened afterwards. A fat-fingered extra zero on the
        /// high anchor would not merely make runs hard - it would multiply every drawn creature's health by
        /// the resulting ratio until an admin noticed.
        ///
        /// These are NOT statements about what a good anchor is. The shipped values are 890 and 13100;
        /// anything up to these ceilings is a legitimate tuning choice the ceiling deliberately does not
        /// judge. Raise them if a real design ever wants more; do not remove them. Note that
        /// <see cref="DungeonPopulationBuilder.MaxHealthNormalizeRatio"/> is the second, independent guard
        /// on the same failure: the ceilings bound the anchors, that clamp bounds the ratio they produce.
        /// </summary>
        public const double MaxAnchorLow = 100000.0;

        /// <summary>Typo ceiling for the high anchor - see <see cref="MaxAnchorLow"/>.</summary>
        public const double MaxAnchorHigh = 1000000.0;

        /// <summary>
        /// The per-level ratio g, DERIVED from the two anchors - see this class's own summary for why it is
        /// never a literal. Returns 1.0 (a flat curve) when the sanitized high anchor is at or below the
        /// sanitized low one, matching <see cref="Target(int, double, double)"/>'s own choice.
        /// </summary>
        public static double RatioPerLevel(double anchorLow, double anchorHigh)
        {
            var low = SanitizeAnchorLow(anchorLow);
            var high = SanitizeAnchorHigh(anchorHigh);

            if (high <= low)
                return 1.0;

            return Math.Pow(high / low, 1.0 / (AnchorHighLevel - AnchorLowLevel));
        }

        /// <summary>
        /// The band-standard health at <paramref name="level"/>, or 0 for a level of 0 or below ("no curve").
        ///
        /// Returns a DOUBLE rather than a rounded integer on purpose. It has two consumers with different
        /// rounding needs - a ratio (T / band median) and an integer floor (round(T * ratio)) - and rounding
        /// here first would make the ratio carry a rounding error it has no reason to, and would break strict
        /// monotonicity at the bottom of the ladder where consecutive targets are less than 1 hp apart.
        ///
        /// FLAT AT <paramref name="anchorLow"/>, NEVER INVERTED, when the sanitized high anchor is at or
        /// below the sanitized low one. An admin who swaps the two, or types a high anchor under the low one,
        /// has expressed no coherent curve; a falling ladder (high-level runs easier than low-level ones) is
        /// the one outcome that is definitely not what they meant, and refusing to build the run at all is
        /// worse than ignoring the dial. A flat curve is legible in the log and trivially recognised as
        /// misconfiguration.
        /// </summary>
        public static double Target(int level, double anchorLow, double anchorHigh, long topLevel = DefaultTopLevel)
        {
            if (level <= 0)
                return 0.0;

            var low = SanitizeAnchorLow(anchorLow);
            var high = SanitizeAnchorHigh(anchorHigh);

            if (high <= low)
                return low;

            // Clamped at the top level, so every level above it reads the top level's value.
            var at = Math.Min(level, SanitizeTopLevel(topLevel));

            // EXACTLY the high anchor at its own level, rather than low * g^190, which floating point can land a
            // hair off: a top level of 375 must reproduce the pre-2026-10-08 clamp bit for bit, and T(375) has
            // always been the anchor value itself.
            if (at == AnchorHighLevel)
                return high;

            // Math.Pow(g, 0) is exactly 1, so level == AnchorLowLevel already returns `low` exactly and
            // needs no special case. Above AnchorHighLevel the same expression IS the extrapolation - g carried
            // forward past the high anchor - so there is deliberately no second formula for it.
            return low * Math.Pow(RatioPerLevel(low, high), at - AnchorLowLevel);
        }

        /// <summary>
        /// The band-standard health at <paramref name="level"/> for one run's resolved tunables, or 0 when
        /// the master switch (dynamic_dungeons_health_curve) is off - read by every caller as "no curve", the
        /// same answer a non-positive level gives.
        /// </summary>
        public static double Target(int level, DungeonPopulationLimits limits)
            => limits.HealthCurveEnabled ? Target(level, limits.HealthCurveAnchorLow, limits.HealthCurveAnchorHigh, limits.HealthCurveTopLevel) : 0.0;

        /// <summary>
        /// Sanitizes dynamic_dungeons_health_curve_top_level into [<see cref="AnchorHighLevel"/>,
        /// <see cref="DungeonGemSpec.MaxRunLevel"/>]. Below the high anchor would clamp the curve INSIDE its own
        /// measured range, flattening data it actually has; above the run ceiling describes levels no run can
        /// reach. Both read as the nearer bound rather than as the default, so an admin who types 300 gets the
        /// most conservative legal curve (the old 375 clamp) and one who types 9999 gets the whole run range -
        /// each the closest honest reading of what they typed. A default(DungeonPopulationLimits) carries 0 here,
        /// which therefore reads as 375: today's clamp, the safe reading of a limits struct nobody filled in.
        ///
        /// Pure, on the same contract as <see cref="SanitizeAnchor"/>: the WARN lives in the spawner's reader.
        /// </summary>
        public static int SanitizeTopLevel(long value)
            => (int)Math.Clamp(value, AnchorHighLevel, DungeonGemSpec.MaxRunLevel);

        /// <summary>
        /// Sanitizes the low anchor: NaN, Infinity or a non-positive value reads as
        /// <see cref="DefaultAnchorLow"/>, and anything above <see cref="MaxAnchorLow"/> reads as the
        /// ceiling.
        ///
        /// Deliberately NOT ThreadDungeonSpawner.SanitizeDoubleDial's rule, which keeps a zero as an explicit
        /// "disable this axis". An anchor has no disabled state - a zero anchor makes g undefined and the
        /// whole curve meaningless - so zero falls back like any other garbled value. The master switch is
        /// how the feature is turned off. This is SanitizeBandDial's reasoning applied to an anchor rather
        /// than to a band edge.
        /// </summary>
        internal static double SanitizeAnchorLow(double value) => SanitizeAnchor(value, DefaultAnchorLow, MaxAnchorLow);

        /// <summary>Sanitizes the high anchor, same rule - see <see cref="SanitizeAnchorLow"/>.</summary>
        internal static double SanitizeAnchorHigh(double value) => SanitizeAnchor(value, DefaultAnchorHigh, MaxAnchorHigh);

        /// <summary>
        /// The shared anchor rule, internal rather than private so ThreadDungeonSpawner.ReadAnchorDial can warn
        /// against the SAME arithmetic the curve applies rather than a restatement of it - the contract
        /// ReadBandDial/SanitizeBandDial already follow.
        ///
        /// Pure and side-effect free on purpose, on the same contract as
        /// ThreadDungeonSpawner.SanitizeDoubleDial: the WARN lives in the reader, so a unit test can assert
        /// this arithmetic without a configured log4net or a live PropertyManager.
        /// </summary>
        internal static double SanitizeAnchor(double value, double fallback, double ceiling)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
                return fallback;

            return Math.Min(value, ceiling);
        }
    }
}
