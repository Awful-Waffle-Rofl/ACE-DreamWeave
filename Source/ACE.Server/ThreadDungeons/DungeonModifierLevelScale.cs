using System;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// The modifier magnitude curve below level 185 (owner ruling, 2026-10-04): a gem's modifiers bite only a
    /// little at low levels and ramp up to exactly today's strength at the anchor, so a level-50 gem handed to
    /// a new player does not carry full-strength modifiers.
    ///
    ///     s(L) = min(1, (L / anchor) ^ k)
    ///
    /// L is the gem's level (spec.Level, after any scarab shift), the anchor is
    /// <see cref="DungeonGemFactory.RungBaseLevel"/> (185, the lowest Raw Fragment rung), and k is
    /// dynamic_dungeons_modifier_level_exponent (<see cref="DefaultExponent"/> = 1.76, chosen so s(50) = 0.100).
    /// k &lt;= 0 turns the scaler off (s = 1 everywhere).
    ///
    /// s pulls each modifier value toward its "no effect" value, never past it:
    ///   - ADDITIVE axes (neutral 0) - the damage, crit, crit-damage and damage-resist ratings, ignore-shield
    ///     and hollow: m x s;
    ///   - MULTIPLIER axes (neutral 1.0) - health, run speed and monster count: 1 + s x (m - 1);
    ///   - ELITE SHARE (neutral <see cref="DungeonPopulationBuilder.DefaultEliteShare"/>):
    ///     neutral + s x (m - neutral);
    ///   - a modifier's XP/luminance reward FACTOR (neutral 1.0): 1 + s x (factor - 1).
    ///
    /// DELIBERATELY NOT SCALED: loot quantity, loot quality and salvage affinity. Those already carry the
    /// gem-level reward-scale ratio (<see cref="DungeonRewardMath.RewardScaleRatio"/>), and scaling them a
    /// second time here would double-discount the same level.
    ///
    /// EXACTNESS AT AND ABOVE THE ANCHOR. Every transform returns its input UNCHANGED when s &gt;= 1, rather
    /// than evaluating 1 + 1 x (m - 1), which is not guaranteed to be bit-identical to m in floating point.
    /// That is what makes "levels 185 and above behave exactly as today" a property of the code rather than
    /// of the rounding.
    ///
    /// Pure: nothing here reads PropertyManager (a read throws under the unit-test harness). The live
    /// exponent is resolved by ThreadDungeonSpawner.ResolveModifierLevelExponent and carried in
    /// <see cref="DungeonPopulationLimits.ModifierLevelExponent"/>.
    /// </summary>
    public static class DungeonModifierLevelScale
    {
        /// <summary>The compiled default for dynamic_dungeons_modifier_level_exponent.</summary>
        public const double DefaultExponent = 1.76;

        /// <summary>
        /// The level at which s reaches 1.0 - the lowest Raw Fragment rung. Read through, never restated, so
        /// the curve and the rung ladder cannot disagree; DungeonModifierLevelScaleTests pins it to 185 so a
        /// rung added BELOW today's first one fails loudly instead of silently moving this anchor.
        /// </summary>
        public static int AnchorLevel => DungeonGemFactory.RungBaseLevel;

        /// <summary>
        /// s for a gem at <paramref name="level"/>. 1.0 when the exponent is non-positive, NaN or infinite
        /// (the scaler is off - a garbled value must degrade to today's behaviour, never to zero-strength
        /// modifiers), 1.0 at or above <see cref="AnchorLevel"/>, and 0.0 for a non-positive level.
        /// </summary>
        public static double Factor(int level, double exponent)
        {
            if (double.IsNaN(exponent) || double.IsInfinity(exponent) || exponent <= 0)
                return 1.0;

            var anchor = AnchorLevel;

            if (anchor <= 0 || level >= anchor)
                return 1.0;

            if (level <= 0)
                return 0.0;

            var s = Math.Pow((double)level / anchor, exponent);

            return double.IsNaN(s) ? 1.0 : Math.Clamp(s, 0.0, 1.0);
        }

        /// <summary>An additive axis (neutral 0): m x s.</summary>
        public static double ScaleAdditive(double magnitude, double s)
            => IsNeutralScale(s) ? magnitude : magnitude * s;

        /// <summary>A multiplier axis (neutral 1.0): 1 + s x (m - 1).</summary>
        public static double ScaleMultiplier(double magnitude, double s)
            => ScaleToward(magnitude, 1.0, s);

        /// <summary>Elite share (neutral <see cref="DungeonPopulationBuilder.DefaultEliteShare"/>).</summary>
        public static double ScaleEliteShare(double magnitude, double s)
            => ScaleToward(magnitude, DungeonPopulationBuilder.DefaultEliteShare, s);

        /// <summary>A modifier's XP or luminance reward factor (neutral 1.0): 1 + s x (factor - 1).</summary>
        public static double ScaleRewardFactor(double factor, double s)
            => ScaleToward(factor, 1.0, s);

        /// <summary>neutral + s x (m - neutral); m unchanged when s &gt;= 1.</summary>
        public static double ScaleToward(double magnitude, double neutral, double s)
            => IsNeutralScale(s) ? magnitude : neutral + s * (magnitude - neutral);

        /// <summary>
        /// The ONE place a monsterEffectKind is mapped to its transform. Every consumer of a modifier
        /// magnitude that becomes a monster stat (DungeonRewardMath.Effects and RunValue) goes through here,
        /// and the item-panel/chat wording reaches it through <see cref="ScaleDisplayMagnitude"/>, so the number
        /// a player reads is the number the run applies.
        ///
        /// loot_quantity, salvage_affinity, "none" and any kind this switch does not know are returned
        /// UNCHANGED: the first two by ruling (they already carry the reward-scale ratio), and the rest because
        /// no creature knob reads them (every Effects/RunValue consumer matches on a known kind). A "none"
        /// row's REWARD is scaled where it is paid, in DungeonRewardMath.Product, and its printed wording by
        /// <see cref="ScaleDisplayMagnitude"/>.
        /// </summary>
        public static double ScaleMagnitude(string kind, double magnitude, double s)
        {
            if (IsNeutralScale(s))
                return magnitude;

            switch (kind)
            {
                case DungeonRewardMath.DamageRating:
                case DungeonRewardMath.CritRating:
                case DungeonRewardMath.CritDamageRating:
                case DungeonRewardMath.DamageResistRating:
                case DungeonRewardMath.IgnoreShield:
                case DungeonRewardMath.Hollow:
                    return ScaleAdditive(magnitude, s);

                case DungeonRewardMath.HealthMult:
                case DungeonRewardMath.RunSpeedMult:
                case DungeonRewardMath.CountMult:
                    // A non-positive or NaN multiplier is invalid content that every consumer already skips
                    // or resets (HealthMultiplier's "magnitude > 0", count_mult's "< 0 reads as 1.0"). It is
                    // returned unchanged so scaling cannot turn an invalid row into a valid-looking one.
                    return magnitude > 0 ? ScaleMultiplier(magnitude, s) : magnitude;

                case DungeonRewardMath.EliteShare:
                    return ScaleEliteShare(magnitude, s);

                default:
                    return magnitude;
            }
        }

        /// <summary>
        /// The magnitude to PRINT for <paramref name="def"/> on a gem whose curve value is <paramref name="s"/> -
        /// what ThreadDungeonGemHandler.RenderScaledEffect hands RenderEffect.
        ///
        /// For every monster-knob kind this is <see cref="ScaleMagnitude"/>, the same value the run applies.
        /// A "none" row (radiant, enlightened) writes no creature knob; RenderEffect words its magnitude as
        /// the reward it pays ("kills pay x1.25 experience"), using the luminance fields when RewardLumSlope is
        /// non-zero and the XP fields otherwise - the same precedence TryDescribeEffect uses. Its printed value
        /// must therefore be the magnitude whose factor is the SCALED factor DungeonRewardMath.Product
        /// multiplies in: with f = base + slope x m and f' = <see cref="ScaleRewardFactor"/>(f, s), that is
        /// m' = (f' - base) / slope. For the shipped rows (base 0, slope 1) this is simply 1 + s x (m - 1).
        /// A "none" row whose factor does not depend on its magnitude (a constant XP kind) falls back to
        /// <see cref="ScaleRewardFactor"/> applied to the magnitude itself.
        /// </summary>
        public static double ScaleDisplayMagnitude(ACE.Server.ThreadDungeons.Defs.ModifierDef def, double magnitude, double s)
        {
            if (def == null || IsNeutralScale(s))
                return magnitude;

            var kind = def.MonsterEffectKind ?? "none";

            if (kind != "none")
                return ScaleMagnitude(kind, magnitude, s);

            double factorBase, slope;

            if (def.RewardLumSlope != 0)
            {
                factorBase = def.RewardLumBase;
                slope = def.RewardLumSlope;
            }
            else if (def.RewardXpSlope != 0 && def.RewardXpKind == "linear")
            {
                factorBase = def.RewardXpBase;
                slope = def.RewardXpSlope;
            }
            else
            {
                return ScaleRewardFactor(magnitude, s);
            }

            var scaledFactor = ScaleRewardFactor(factorBase + slope * magnitude, s);

            return (scaledFactor - factorBase) / slope;
        }

        /// <summary>
        /// True when s leaves values untouched: at or above 1.0, and for NaN (a garbled s must never shrink a
        /// modifier). Below 0 is clamped by <see cref="Factor"/> and cannot reach here from production.
        /// </summary>
        private static bool IsNeutralScale(double s) => double.IsNaN(s) || s >= 1.0;
    }
}
