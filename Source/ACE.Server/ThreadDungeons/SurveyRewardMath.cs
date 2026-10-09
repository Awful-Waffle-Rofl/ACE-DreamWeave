using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using ACE.Server.Entity;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// One reward tier of the daily survey: how many surveys inside the window it needs, what share of the
    /// player's own next-level XP it pays, and its unscaled luminance.
    /// </summary>
    public readonly struct SurveyTier
    {
        /// <summary>Surveys in the current window this tier needs, and also the N of its rolling average.</summary>
        public int Surveys { get; }

        /// <summary>Share of the player's OWN next-level XP the award is worth before the cap applies.</summary>
        public double XpPercent { get; }

        /// <summary>Flat luminance at ratio 1.0. Owner ruling R4 scales this by the ratio (luminance only; XP is measured at a level, not scaled by the ratio).</summary>
        public long Luminance { get; }

        public SurveyTier(int surveys, double xpPercent, long luminance)
        {
            Surveys = surveys;
            XpPercent = xpPercent;
            Luminance = luminance;
        }

        /// <summary>The quest row that latches this tier as paid for the current window.</summary>
        public string Quest => DungeonSurveyRules.SurveyQuest(Surveys);

        public override string ToString()
            => $"tier {Surveys.ToString(CultureInfo.InvariantCulture)} ({XpPercent.ToString("0.##", CultureInfo.InvariantCulture)}, {Luminance.ToString(CultureInfo.InvariantCulture)} lum)";
    }

    /// <summary>
    /// Everything the reward math needs from outside itself, resolved ONCE per turn-in by
    /// SurveyArchivistStation.BuildLimits and handed in. The same object feeds every tier paid in one visit,
    /// which is what makes the displayed figure and the paid figure provably the same number.
    ///
    /// Sanitizing lives in this constructor rather than at each call site, so a NaN, a negative, or an absurd
    /// tunable value cannot reach the math from ANY caller (owner ruling R7's exponent is live-tunable, and a
    /// live tunable is player-visible input to an XP award).
    /// </summary>
    public sealed class SurveyRewardLimits
    {
        /// <summary>Owner ruling R7. Also the default of dynamic_dungeons_survey_ratio_exponent.</summary>
        public const double DefaultExponent = 2.0;

        /// <summary>Default of dynamic_dungeons_survey_ratio_floor. A cleared run is never worth nothing.</summary>
        public const double DefaultFloor = 0.10;

        /// <summary>
        /// Anything past this reads as garbage rather than intent: at k = 50 even a gem one level under the
        /// cap pays under 84% of the ratio, so no legitimate tuning lives above it.
        /// </summary>
        public const double MaxExponent = 50.0;

        /// <summary>The reference level the LUMINANCE ratio is measured against. DungeonGemSpec.MaxGemLevel in production (the gem ceiling, never the run one).</summary>
        public int MaxLevel { get; }

        /// <summary>
        /// The cumulative character XP chart (index = level) the XP cap is read off. Handed in by the station
        /// (EnlightenmentXpCurve.ExtendedTotals in production) so the math stays pure and a test can drive it with
        /// a synthetic chart. Null or shorter than three entries means no XP can be computed: the cap is 0 and the
        /// station pays luminance only.
        /// </summary>
        public IReadOnlyList<ulong> XpTotals { get; }

        /// <summary>The k of ratio = (avg / MaxLevel)^k.</summary>
        public double Exponent { get; }

        /// <summary>Lower bound on the ratio, so a low-level run still pays something.</summary>
        public double Floor { get; }

        /// <summary>False pins the ratio to 1.0 - a live rollback to the pre-scaling numbers, no redeploy.</summary>
        public bool ScalingEnabled { get; }

        public SurveyRewardLimits(int maxLevel, IReadOnlyList<ulong> xpTotals, double exponent, double floor, bool scalingEnabled)
        {
            MaxLevel = Math.Max(1, maxLevel);
            XpTotals = xpTotals;
            Exponent = double.IsFinite(exponent) && exponent >= 0.0 ? Math.Min(exponent, MaxExponent) : DefaultExponent;
            Floor = double.IsFinite(floor) ? Math.Clamp(floor, 0.0, 1.0) : DefaultFloor;
            ScalingEnabled = scalingEnabled;
        }
    }

    /// <summary>What one tier pays this visit, with every intermediate value the log line reports.</summary>
    public readonly struct SurveyAward
    {
        public SurveyTier Tier { get; }

        /// <summary>How many ring entries the average actually covered - min(Tier.Surveys, ring depth held).</summary>
        public int Sampled { get; }

        /// <summary>Rounded mean gem level over those entries; 0 when the ring is empty.</summary>
        public int AverageLevel { get; }

        /// <summary>clamp((AverageLevel / MaxLevel)^k, floor, 1.0), or 1.0 for an empty ring (ruling R8).</summary>
        public double Ratio { get; }

        /// <summary>
        /// The level whose next-level XP the award is a share of: min(player level, AverageLevel), or just the
        /// player level for an empty ring or with scaling switched off.
        /// </summary>
        public int XpLevel { get; }

        /// <summary>The cap handed to Player.GrantLevelProportionalXp. Zero means pay no XP at all.</summary>
        public long XpCap { get; }

        /// <summary>Scaled luminance (ruling R4).</summary>
        public long Luminance { get; }

        public double XpPercent => Tier.XpPercent;

        public SurveyAward(SurveyTier tier, int sampled, int averageLevel, double ratio, int xpLevel, long xpCap, long luminance)
        {
            Tier = tier;
            Sampled = sampled;
            AverageLevel = averageLevel;
            Ratio = ratio;
            XpLevel = xpLevel;
            XpCap = xpCap;
            Luminance = luminance;
        }
    }

    /// <summary>
    /// The daily-survey reward, scaled to the difficulty actually run.
    ///
    ///   avgLevel(N) = round(mean of the newest min(N, ring.Count) gem levels)
    ///   ratio       = clamp((avgLevel / MaxLevel)^k, floor, 1.0)
    ///   luminance   = round(baseLum * ratio)
    ///   xpLevel     = min(playerLevel, avgLevel)
    ///   xpCap       = round(percent * delta(xpLevel)),  delta(L) = XP to go from L to L + 1
    ///
    /// The XP is "x% of a level-up, measured at whichever is lower: the player's level or the ring's average
    /// gem level". The cap is that figure and GrantLevelProportionalXp(percent, 0, cap) pays
    /// min(percent * delta(playerLevel), cap), which is the same number whichever side is lower. The ratio
    /// does NOT touch XP at all (it would double-penalise); it scales luminance only.
    ///
    /// Owner ruling R6: the ratio is a FORMULA over MaxLevel, deliberately not the hand-authored xpLadder in
    /// Content/dungeons/dynamic/modifiers.json. The ladder's top rung is written at the current cap, so it
    /// would need a new rung on every cap raise and would read 1.0 for everything above it until someone
    /// noticed. A formula over MaxLevel rescales itself.
    ///
    /// Owner ruling R8: an EMPTY ring pays ratio 1.0 and measures XP at the player's own level. A character
    /// who filed surveys before this shipped has no ring, and must not take a silent cut on a turn-in they
    /// had already earned. Switching scaling off (the live rollback) does the same.
    ///
    /// A ring average ABOVE the player's own level changes nothing for XP: min() picks the player's level,
    /// so a level-100 character who somehow ran a level-275 gem is paid percent * delta(100).
    ///
    /// PURE: every tunable arrives in a <see cref="SurveyRewardLimits"/>. No PropertyManager, no DatManager,
    /// no Player, no store - all four are unavailable or throw under the unit-test harness.
    /// </summary>
    public static class SurveyRewardMath
    {
        /// <summary>
        /// THE tier table, ascending. This is the ONLY declaration of the (surveys, percent, luminance)
        /// triple in the fork - the station, the turn-in planner and the tests all read it from here, so the
        /// three cannot drift. The numbers are unchanged from the emote rig this replaced: ruling R4 scales
        /// them by the ratio, it does not renumber them.
        /// </summary>
        public static readonly IReadOnlyList<SurveyTier> Tiers = new[]
        {
            new SurveyTier(1, 0.15, 50000),
            new SurveyTier(5, 0.40, 250000),
            new SurveyTier(10, 0.75, 500000),
        };

        /// <summary>Tiers highest first - the order a turn-in pays them in.</summary>
        public static IEnumerable<SurveyTier> Descending => Tiers.Reverse();

        public static bool TryGetTier(int surveys, out SurveyTier tier)
        {
            for (var i = 0; i < Tiers.Count; i++)
            {
                if (Tiers[i].Surveys != surveys)
                    continue;

                tier = Tiers[i];
                return true;
            }

            tier = default;
            return false;
        }

        /// <summary>
        /// Rounded mean of the newest min(window, ring.Count) entries. Returns 0 for an empty ring, which
        /// <see cref="Ratio"/> reads as "no data" rather than as "level zero".
        /// </summary>
        public static int AverageLevel(SurveyLevelRing ring, int window)
        {
            var sampled = SampleSize(ring, window);
            if (sampled == 0)
                return 0;

            long sum = 0;
            for (var i = 0; i < sampled; i++)
                sum += ring.Levels[i];

            return (int)Math.Round(sum / (double)sampled, MidpointRounding.AwayFromZero);
        }

        /// <summary>How many ring entries a tier's average actually covers. A short ring averages what exists.</summary>
        public static int SampleSize(SurveyLevelRing ring, int window)
        {
            if (ring == null || window <= 0)
                return 0;

            return Math.Min(window, ring.Count);
        }

        /// <summary>
        /// clamp((averageLevel / MaxLevel)^k, floor, 1.0). Returns 1.0 unscaled when scaling is switched off
        /// (the live rollback) or when nothing was sampled (ruling R8, the grandfather clause).
        /// </summary>
        public static double Ratio(int averageLevel, int sampled, SurveyRewardLimits limits)
        {
            if (limits == null || !limits.ScalingEnabled || sampled <= 0)
                return 1.0;

            var raw = Math.Pow(Math.Max(0, averageLevel) / (double)limits.MaxLevel, limits.Exponent);

            // A non-finite result can only come from a pathological tunable that survived sanitizing; pay the
            // unscaled award rather than a NaN cap, which would silently become a zero award.
            if (!double.IsFinite(raw))
                return 1.0;

            return Math.Clamp(raw, limits.Floor, 1.0);
        }

        /// <summary>
        /// The level the XP is measured at: min(playerLevel, averageLevel), or playerLevel alone when nothing was
        /// sampled (ruling R8) or scaling is switched off (the live rollback).
        /// </summary>
        public static int XpLevel(int playerLevel, int averageLevel, int sampled, SurveyRewardLimits limits)
        {
            if (limits == null || !limits.ScalingEnabled || sampled <= 0)
                return playerLevel;

            return Math.Min(playerLevel, averageLevel);
        }

        /// <summary>
        /// The XP to go from <paramref name="level"/> to level + 1 on the chart: the same
        /// EnlightenmentXpCurve.GetXPBetweenLevels call GrantLevelProportionalXp pays its percent of, so the
        /// level is clamped into the chart's index range identically. Returns 0 for an unusable chart, which
        /// the station reads as "pay no XP" (a cap of 0 means UNCAPPED to the grant, so it must never reach it).
        /// </summary>
        public static long LevelXp(IReadOnlyList<ulong> totals, int level)
        {
            if (totals == null || totals.Count < 3)
                return 0;

            // Clamp the level into [1, last index - 1] first, so a level at either edge still spans a real
            // one-level step. GetXPBetweenLevels clamps each end on its own, and a level below 1 would
            // otherwise collapse to a zero-width step (a zero cap).
            level = Math.Clamp(level, 1, totals.Count - 2);

            var delta = EnlightenmentXpCurve.GetXPBetweenLevels(totals, level, level + 1);

            return delta > long.MaxValue ? long.MaxValue : (long)delta;
        }

        /// <summary>
        /// The whole reward for one tier. Rounding is AwayFromZero so a half-XP cap rounds up rather than to
        /// even, and a plain cast would pay one XP less. <paramref name="playerLevel"/> is the level the
        /// player turns in at; see <see cref="XpLevel"/> for how it combines with the ring average.
        /// </summary>
        public static SurveyAward Compute(SurveyTier tier, SurveyLevelRing ring, SurveyRewardLimits limits, int playerLevel)
        {
            if (limits == null) throw new ArgumentNullException(nameof(limits));

            var sampled = SampleSize(ring, tier.Surveys);
            var averageLevel = AverageLevel(ring, tier.Surveys);
            var ratio = Ratio(averageLevel, sampled, limits);

            var xpLevel = XpLevel(playerLevel, averageLevel, sampled, limits);
            var xpCap = (long)Math.Round(tier.XpPercent * LevelXp(limits.XpTotals, xpLevel), MidpointRounding.AwayFromZero);
            var luminance = (long)Math.Round(tier.Luminance * ratio, MidpointRounding.AwayFromZero);

            return new SurveyAward(tier, sampled, averageLevel, ratio, xpLevel, Math.Max(0, xpCap), Math.Max(0, luminance));
        }
    }
}
