using System;
using System.Collections.Generic;

using ACE.DatLoader;

namespace ACE.Server.Entity
{
    /// <summary>
    /// Synthesizes the character XP chart past the retail cap (level 275) so an enlightened character can keep
    /// leveling toward its raised personal cap. The retail chart (portal.dat XpTable.CharacterLevelXPList) ends
    /// at level 275; this class extrapolates the chart's tail geometrically from its last two deltas, using
    /// exact rational arithmetic (no floating point) so the extended chart is deterministic across restarts.
    ///
    /// Kept free of <see cref="WorldObjects.Player"/> state, and the extrapolation core (<see cref="Extend"/>)
    /// takes its input list explicitly, so the boundary behavior (ratio derivation, monotonicity, overflow
    /// guard) is unit-testable in isolation without loading the client dat files. See Player_Xp.cs for the
    /// per-player-cap wiring and Entity.Enlightenment for the reset flow.
    /// </summary>
    public static class EnlightenmentXpCurve
    {
        /// <summary>The retail character level cap (portal.dat chart length - 1).</summary>
        public const int BaseMaxLevel = 275;

        /// <summary>Levels the personal cap rises by per enlightenment.</summary>
        public const int LevelsPerEnlightenment = 5;

        /// <summary>
        /// Cumulative-total overflow cap for the extended chart. Half of long.MaxValue leaves ample headroom
        /// for the signed 64-bit XP math elsewhere in the server (TotalExperience is a long, and several call
        /// sites cast the chart's ulong totals to long and subtract), so the synthesized tail can never push a
        /// legitimate total into overflow territory.
        /// </summary>
        internal const ulong TotalCap = (ulong)(long.MaxValue / 2);

        private static readonly Lazy<ExtendedChart> chart = new Lazy<ExtendedChart>(BuildChart, true);

        /// <summary>
        /// The extended cumulative-XP chart, indexed by level (index 0 = 0 XP). Levels 0..275 are the retail
        /// chart verbatim; higher indices are the synthesized tail up to <see cref="HardCeilingLevel"/>.
        /// </summary>
        public static IReadOnlyList<ulong> ExtendedTotals => chart.Value.Totals;

        /// <summary>
        /// The highest level the synthesized chart supports before the overflow cap - the absolute ceiling no
        /// amount of enlightenment can raise the personal cap past.
        /// </summary>
        public static int HardCeilingLevel => chart.Value.HardCeiling;

        private sealed class ExtendedChart
        {
            public IReadOnlyList<ulong> Totals;
            public int HardCeiling;
        }

        private static ExtendedChart BuildChart()
        {
            var baseTotals = DatManager.PortalDat.XpTable.CharacterLevelXPList;
            var extended = Extend(baseTotals, TotalCap);
            return new ExtendedChart { Totals = extended, HardCeiling = extended.Count - 1 };
        }

        /// <summary>
        /// Extends a cumulative-total XP chart past its end by extrapolating the growth ratio of its last two
        /// deltas as an exact rational (num/den), compounding it per level until adding the next delta would
        /// push the cumulative total past <paramref name="totalCap"/>. A ratio &lt;= 1 (flat or shrinking tail)
        /// degenerates to a constant-delta (linear) extension. Returns a fresh list = the original entries
        /// followed by the synthesized tail; a chart with fewer than 3 entries (no derivable ratio) is copied
        /// unchanged. Pure and deterministic - no floating point, no shared state.
        /// </summary>
        internal static List<ulong> Extend(IReadOnlyList<ulong> baseTotals, ulong totalCap)
        {
            var result = new List<ulong>(baseTotals);

            // need at least two deltas (three entries) to derive a growth ratio
            if (result.Count < 3)
                return result;

            var last = result.Count - 1;
            var prevDelta = result[last] - result[last - 1];
            var prevPrevDelta = result[last - 1] - result[last - 2];

            // growth ratio as an exact rational: nextDelta = currentDelta * num / den
            ulong num = prevDelta;
            ulong den = prevPrevDelta;

            // flat/shrinking tail -> extend with a constant delta rather than decaying to zero growth
            if (den == 0 || num <= den)
            {
                num = 1;
                den = 1;
            }

            var currentTotal = result[last];
            var currentDelta = prevDelta;

            // a degenerate chart could sit at a flat total (delta 0); nothing to extend then
            if (currentDelta == 0)
                return result;

            while (true)
            {
                // 128-bit intermediate avoids overflow while multiplying before the divide
                var nextDelta = (ulong)((UInt128)currentDelta * num / den);

                // guard against rational rounding stalling growth below the previous step
                if (nextDelta < currentDelta)
                    nextDelta = currentDelta;

                // stop before the cumulative total would exceed the overflow cap
                if (nextDelta > totalCap - currentTotal)
                    break;

                currentTotal += nextDelta;
                currentDelta = nextDelta;
                result.Add(currentTotal);
            }

            return result;
        }

        /// <summary>
        /// The personal maximum level for a character with the given <paramref name="enlightenment"/> count:
        /// <see cref="BaseMaxLevel"/> + <see cref="LevelsPerEnlightenment"/> per enlightenment, clamped to
        /// <see cref="HardCeilingLevel"/>.
        /// </summary>
        public static int GetMaxLevelForEnlightenment(int enlightenment)
        {
            return GetMaxLevelForEnlightenment(enlightenment, HardCeilingLevel);
        }

        /// <summary>Pure ceiling math for <see cref="GetMaxLevelForEnlightenment(int)"/>, unit-testable.</summary>
        internal static int GetMaxLevelForEnlightenment(int enlightenment, int hardCeiling)
        {
            if (enlightenment < 0)
                enlightenment = 0;

            var target = (long)BaseMaxLevel + (long)LevelsPerEnlightenment * enlightenment;

            return (int)Math.Min(target, hardCeiling);
        }

        /// <summary>
        /// The total cumulative XP required to reach <paramref name="level"/>, clamped to the chart bounds.
        /// </summary>
        public static ulong GetTotalXPRequiredForLevel(int level)
        {
            var totals = ExtendedTotals;

            if (level < 0)
                return 0;
            if (level >= totals.Count)
                level = totals.Count - 1;

            return totals[level];
        }

        /// <summary>
        /// The number of skill credits granted for reaching <paramref name="level"/>. Levels 0..275 use the
        /// retail skill-credit chart verbatim. Past 275, an enlightened character earns +1 skill credit every
        /// 25 levels indefinitely - first at 300, then 325, 350, ... - matching how the retail chart's credit
        /// levels are re-earned across each enlightenment reset + re-level cycle. Out-of-range (negative) input
        /// returns 0. This is also the guarded accessor that keeps CheckForLevelup from indexing past
        /// CharacterLevelSkillCreditList.
        /// </summary>
        public static uint GetSkillCreditsForLevel(int level)
        {
            return GetSkillCreditsForLevel(level, DatManager.PortalDat.XpTable.CharacterLevelSkillCreditList);
        }

        /// <summary>Pure skill-credit lookup for <see cref="GetSkillCreditsForLevel(int)"/>, unit-testable.</summary>
        internal static uint GetSkillCreditsForLevel(int level, IReadOnlyList<uint> creditList)
        {
            if (level < 0)
                return 0;

            // within the retail chart: use its per-level credit value verbatim
            if (level < creditList.Count)
                return creditList[level];

            // past the retail cap: +1 skill credit every 25 levels beyond the base cap, indefinitely (300, 325, ...)
            return (level - BaseMaxLevel) % 25 == 0 ? 1u : 0u;
        }
    }
}
