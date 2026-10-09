using System;
using System.Collections.Generic;

using ACE.Server.Entity;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// One rung of the Thread-Guide ladder. See <see cref="ThreadGuideLadder.Rungs"/>.
    /// </summary>
    public sealed class GuideRung
    {
        /// <summary>The rung's level, which is also the guide fragment's lvl=. 50..375 in steps of 25.</summary>
        public int Level { get; }

        /// <summary>
        /// The guide fragment's loot tier. Rungs 50..175 carry the owner-ruled tiers and 200..375 follow the
        /// shipped Raw Fragment weenies: see the TIER note on <see cref="ThreadGuideLadder"/>. Nullable only
        /// because the type predates the ruling; every shipped rung has a value.
        /// </summary>
        public int? Tier { get; }

        /// <summary>
        /// The press component type this rung teaches (one of RawFragmentRules' type constants), or null
        /// for a plain guide rung. On a special rung that type's aim roll is guaranteed
        /// (PressLimits.GuaranteedType).
        /// </summary>
        public string RequiredType { get; }

        /// <summary>
        /// A short, plain, factual line about what the required type does, or null on a plain rung. The text
        /// itself lives in ThreadGuideText (T-EXPLAIN), so the Press and this table cannot disagree.
        /// </summary>
        public string Explanation { get; }

        /// <summary>
        /// Every type the Press demands a dose of before it takes this rung's fragment, in the order the
        /// refusals are checked, and every type it guarantees (PressLimits.GuaranteedTypes). Normally just
        /// <see cref="RequiredType"/>; rung 150 also demands a Taper, because a Talisman sharpens a modifier
        /// that must already be there (owner ruling on the chunk 3 review). Empty on a plain rung.
        /// </summary>
        public IReadOnlyList<string> RequiredTypes { get; }

        public GuideRung(int level, int? tier, string requiredType, string explanation, IReadOnlyList<string> alsoRequired = null)
        {
            Level = level;
            Tier = tier;
            RequiredType = requiredType;
            Explanation = explanation;

            var types = new List<string>();
            if (alsoRequired != null)
                types.AddRange(alsoRequired);
            if (requiredType != null && !types.Contains(requiredType))
                types.Add(requiredType);
            RequiredTypes = types;
        }
    }

    /// <summary>
    /// THE Thread-Guide ladder: a compiled table of the fourteen solo guide rungs a Thread-Guide hands out,
    /// 50, 75, ..., 375, one rung up per win and never reset. Pure, static, no engine state; follows the
    /// DungeonGemFactory.Rungs compiled-table pattern.
    ///
    /// The first six rungs each TEACH one press component type, in the order Scarab, Taper, Herb, Powder,
    /// Talisman, Potion (50..175); that type's aim roll is guaranteed on its rung. Rung 150 also demands and
    /// guarantees a Taper, since its Talisman needs a modifier to sharpen. Rungs 200..375 are plain.
    ///
    /// TIER. No code rule maps a gem level to a loot tier. The only source for Thread rungs is the tier=
    /// hand-authored into each shipped Raw Fragment weenie's DungeonGemSpec (Content/sql/weenies/1003615..
    /// 1003629): levels 185..235 carry tier 7, levels 245..375 carry tier 8. The guide rungs at 200..375 take
    /// their tier from that table, and none of them is ambiguous: 200 sits between the tier-7 rungs 195 and
    /// 205, 250 between the tier-8 rungs 245 and 255, and 225/275/300/325/350/375 are shipped rung levels
    /// themselves. Below 185 there is no shipped rung and no Thread rule at all, so rungs 50..175 take the
    /// tiers the owner ruled for the guide ladder (Thread-Guide chunk 3 spec): 50 = 3, 75 = 4, 100 = 5, 125 = 5, 150 = 6,
    /// 175 = 6. (The only level-to-tier function in the server, LootGenerationFactory_OlthoiPlay.GetTierHeuristic,
    /// is private to Olthoi play and disagrees with the shipped rungs at 245..265, so it is not treated as the
    /// rule.)
    /// </summary>
    public static class ThreadGuideLadder
    {
        public const int FirstRung = 50;
        public const int LastRung = 375;
        public const int RungStep = 25;

        public static readonly IReadOnlyList<GuideRung> Rungs = new[]
        {
            new GuideRung(50, 3, RawFragmentRules.ScarabType, ThreadGuideText.ExplainScarab),
            new GuideRung(75, 4, RawFragmentRules.TaperType, ThreadGuideText.ExplainTaper),
            new GuideRung(100, 5, RawFragmentRules.HerbType, ThreadGuideText.ExplainHerb),
            new GuideRung(125, 5, RawFragmentRules.PowderType, ThreadGuideText.ExplainPowder),
            new GuideRung(150, 6, RawFragmentRules.TalismanType, ThreadGuideText.ExplainTalisman, new[] { RawFragmentRules.TaperType }),
            new GuideRung(175, 6, RawFragmentRules.PotionType, ThreadGuideText.ExplainPotion),
            new GuideRung(200, 7, null, null),
            new GuideRung(225, 7, null, null),
            new GuideRung(250, 8, null, null),
            new GuideRung(275, 8, null, null),
            new GuideRung(300, 8, null, null),
            new GuideRung(325, 8, null, null),
            new GuideRung(350, 8, null, null),
            new GuideRung(375, 8, null, null),
        };

        /// <summary>True when <paramref name="level"/> is exactly one of the ladder's rungs.</summary>
        public static bool IsRungLevel(int level)
            => level >= FirstRung && level <= LastRung && (level - FirstRung) % RungStep == 0;

        /// <summary>The rung at exactly <paramref name="level"/>, or null when it is not a rung.</summary>
        public static GuideRung Get(int level)
        {
            if (!IsRungLevel(level)) return null;
            return Rungs[(level - FirstRung) / RungStep];
        }

        /// <summary>
        /// The rung to issue next, given the highest rung the player has WON (0 when none): the smallest rung
        /// strictly above <paramref name="highestWon"/>. So 0 -> 50, n -> n + 25 for any rung n, and 375 ->
        /// null (the ladder is complete). An off-ladder value is tolerated rather than thrown on - a negative
        /// reads as 0, 60 reads as "next is 75", anything at or above 375 is complete - because the value
        /// comes from persisted player state and a corrupt one must not wedge the NPC.
        /// </summary>
        public static int? NextRung(int highestWon)
        {
            if (highestWon < FirstRung) return FirstRung;
            if (highestWon >= LastRung) return null;

            return FirstRung + ((highestWon - FirstRung) / RungStep + 1) * RungStep;
        }

        /// <summary>The component type the rung teaches, or null for a plain rung or a non-rung level.</summary>
        public static string RequiredType(int rungLevel) => Get(rungLevel)?.RequiredType;

        /// <summary>Every type the rung demands and guarantees (<see cref="GuideRung.RequiredTypes"/>); empty for a plain rung or a non-rung level.</summary>
        public static IReadOnlyList<string> RequiredTypes(int rungLevel) => Get(rungLevel)?.RequiredTypes ?? Array.Empty<string>();

        /// <summary>
        /// The XP cap to pass as GrantLevelProportionalXp's max so that GrantLevelProportionalXp(1.0, 0, cap)
        /// pays exactly min(the player's next-level XP, the next-level XP of a player AT
        /// <paramref name="rungLevel"/>), before EarnXP applies its own modifiers.
        ///
        /// SAME DELTA, BY CONSTRUCTION. GrantLevelProportionalXp pays percent of
        /// EnlightenmentXpCurve.GetXPBetweenLevels(chart, level, level + 1) and clamps it to max when max > 0
        /// (EnlightenmentXpCurve.LevelProportionalXp, which the player method delegates to). This returns
        /// the very same function evaluated at the rung: GetXPBetweenLevels(chart, rungLevel, rungLevel + 1).
        /// The clamp then pays min(player's delta, rung's delta) whatever the chart's shape; on a chart whose
        /// deltas grow with level, that is the player's own delta below the rung and the rung's at or above it.
        ///
        /// NEVER 0. A max of 0 means UNCAPPED to GrantLevelProportionalXp, which is the dangerous direction,
        /// so a degenerate zero delta is returned as 1 rather than as 0. Unreachable on the shipped chart,
        /// whose every delta is positive. Throws ArgumentException for a non-rung level.
        /// </summary>
        public static long RewardCap(int rungLevel, IReadOnlyList<ulong> xpTotals)
        {
            if (!IsRungLevel(rungLevel))
                throw new ArgumentException($"{rungLevel} is not a Thread-Guide rung", nameof(rungLevel));

            var delta = EnlightenmentXpCurve.GetXPBetweenLevels(xpTotals, rungLevel, rungLevel + 1);

            // The chart is capped at long.MaxValue / 2 (EnlightenmentXpCurve.TotalCap), so a delta always fits.
            return Math.Max(1L, (long)Math.Min(delta, (ulong)long.MaxValue));
        }

        /// <summary>
        /// <see cref="RewardCap(int, IReadOnlyList{ulong})"/> on the live chart (EnlightenmentXpCurve.ExtendedTotals,
        /// which reads the portal dat on first use, so not callable from a unit test).
        /// </summary>
        public static long RewardCap(int rungLevel) => RewardCap(rungLevel, EnlightenmentXpCurve.ExtendedTotals);
    }
}
