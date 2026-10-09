using System;
using System.Collections.Generic;

using ACE.DatLoader.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>One stat axis the curve extrapolates, each with its own fitted per-level slope.</summary>
    public enum DungeonStatAxis
    {
        /// <summary>Best body-part DVal (melee max damage), and a wielded weapon's Damage.</summary>
        Damage,

        /// <summary>Best body-part BaseArmor.</summary>
        Armor,

        Strength,
        Endurance,
        Quickness,
        Coordination,
        Focus,
        Self,

        /// <summary>EFFECTIVE weapon attack skill (attribute term + InitLevel), every non-magic attack skill.</summary>
        WeaponAttack,

        /// <summary>EFFECTIVE magic attack skill: War, Life, Void and Creature Enchantment.</summary>
        MagicAttack,

        /// <summary>EFFECTIVE melee defense. Softened above <see cref="DungeonStatCurve.DefenseSoftenLevel"/>.</summary>
        MeleeDefense,

        /// <summary>EFFECTIVE missile defense. Softened above <see cref="DungeonStatCurve.DefenseSoftenLevel"/>.</summary>
        MissileDefense,

        /// <summary>EFFECTIVE magic defense. Softened above <see cref="DungeonStatCurve.DefenseSoftenLevel"/>.</summary>
        MagicDefense,
    }

    /// <summary>
    /// The natural stat curve above the top of the authored roster (owner ruling 2026-10-08, final): every
    /// monster stat - attributes, attack skills, defense skills, magic skills, melee damage and body armour -
    /// follows its own log-linear curve past the authored data instead of going flat, and very hard to
    /// impossible for a solo player at the top is intended. Health has its own curve
    /// (<see cref="DungeonHealthCurve"/>) and is deliberately not an axis here.
    ///
    /// <code>
    ///   Standard(M) = Standard(P) * e^(b * (M - P))        for every axis, M above the pivot P
    ///   E(M)        = E(375) * e^(r * b * (M - 375))        EFFECTIVE defense above monster level 375, r = 0.5
    /// </code>
    ///
    /// WHY IT EXISTS. <see cref="DungeonBandStandard"/> measures a band's medians off the creatures it can
    /// really draw, and records its own known limitation: near the top of the ladder the sample is drawn from
    /// a small, fixed set of top-level creatures, so the standard FLATTENS. That was acceptable while 375 was
    /// both the gem ceiling and the top of the authored data. The 2026-10-08 run ceiling (DungeonGemSpec.
    /// MaxRunLevel, 500) put playable runs above any authored creature, and a flat standard there would make a
    /// +35 Mana Scarab buy nothing but health and reward. So the owner ruled that the stats extrapolate.
    ///
    /// THE PIVOT. P = floor(authored top / band high) - floor(375 / 1.15) = 326 on the shipped roster - is the
    /// highest gem level whose natural band [P, 1.15P] is still entirely inside the authored data. Its band
    /// standard is the last fully measured one, so it is the ANCHOR, and it is derived in code from the real
    /// band standard at P (DungeonPopulationBuilder.Build) - never hardcoded here. At or below P nothing in this
    /// type applies; the measured standard is used exactly as before.
    ///
    /// THE SLOPES ARE FITTED, NOT CHOSEN. Each b below is the per-level slope of an ordinary least-squares fit of
    /// ln(stat) against level over the fork's high-band bestiary - the 80 creatures wcid 1004000-1004079 (both
    /// roles) authored at levels 300 to 375, read from Content/sql and the local ace_world - in the 2026-10-08
    /// analysis (scratchpad curve/fit.py and final.py). A per-creature fit rather than a fit over the band
    /// medians, because the medians flatten in exactly the region the curve has to describe. Effective skills
    /// are fitted as attribute term + InitLevel, the quantity CreatureSkill.Base actually yields. Cross-check
    /// the same analysis recorded: from the measured level-326 standard (dmg 844, attack 810, melee D 820,
    /// missile D 745, magic D 630, war 680, armour 540, Str 410, End 410, Quick 360, Coord 410, Focus 580,
    /// Self 580, a 20-member sample) these slopes give, at 375, dmg 1386, attack 831, melee D 859, missile D
    /// 782, magic D 680, war 726, armour 592, Str 456, End 459, Quick 386, Coord 443, Focus 638, Self 641 -
    /// pinned by ThreadsRunCeilingTests.
    ///
    /// THE DEFENSE SOFTENING. Above MONSTER level 375 the effective defense standard grows at
    /// dynamic_dungeons_defense_curve_rate_above_375 (default 0.5) of its fitted rate, continuously at 375.
    /// Effective defense is what decides whether an attack lands, so its rate is what makes the top of the
    /// ladder impossible rather than merely hard; halving it was the owner's call. Because the softening is on
    /// the EFFECTIVE value, a consumer that writes InitLevel must derive it as target effective minus the
    /// attribute term over the curve-raised attributes - otherwise the attributes' own slope would silently
    /// restore the full rate. <see cref="Extrapolate"/> does exactly that for the InitLevel medians.
    ///
    /// SCOPE. Threads only. World Events never construct one of these, and DungeonBandStandard.Compute,
    /// BandUplift.Apply and the DungeonRosterSelector code they share are unchanged.
    ///
    /// PURE: no PropertyManager, no Creature, no logging. The one engine read is the skill formula table, and
    /// it arrives through a delegate (production: DungeonBandStandard.DefaultFormulaOf) so a test can supply
    /// its own.
    /// </summary>
    public static class DungeonStatCurve
    {
        // ---- fitted slopes (ln units per level) ----------------------------------------------------------
        // Provenance for every constant in this block: per-creature log-linear OLS over the 80 fork high-band
        // bestiary weenies (wcid 1004000-1004079, both roles, levels 300-375; Content/sql plus the local
        // ace_world), 2026-10-08 analysis. "Effective" = AttributeFormula term + authored InitLevel.

        /// <summary>Best body-part DVal: +1.017% per level.</summary>
        public const double DamageSlope = 0.01012;

        /// <summary>Best body-part BaseArmor: +0.188% per level.</summary>
        public const double ArmorSlope = 0.00188;

        public const double StrengthSlope = 0.00217;
        public const double EnduranceSlope = 0.00231;
        public const double QuicknessSlope = 0.00142;
        public const double CoordinationSlope = 0.00160;
        public const double FocusSlope = 0.00193;
        public const double SelfSlope = 0.00205;

        /// <summary>
        /// Effective weapon attack. FITTED ON HEAVY WEAPONS ONLY (the bestiary's dominant attack skill) and
        /// applied to every non-magic attack skill, the same "one attack standard for the band" reading
        /// DungeonCombatNormalizer.AttackSkillTarget already takes when it borrows the highest attack median.
        /// </summary>
        public const double WeaponAttackSlope = 0.00053;

        /// <summary>
        /// Effective magic attack. Fitted on War Magic and Life Magic, which came out identical; applied to Void
        /// Magic and Creature Enchantment as well, which the bestiary authors too rarely to fit on their own.
        /// </summary>
        public const double MagicAttackSlope = 0.00133;

        public const double MeleeDefenseSlope = 0.00094;
        public const double MissileDefenseSlope = 0.00099;
        public const double MagicDefenseSlope = 0.00155;

        /// <summary>
        /// The MONSTER level above which effective defense grows at the softened rate. The top of the authored
        /// data (DungeonGemSpec.MaxGemLevel and DungeonHealthCurve.AnchorHighLevel are the same 375 for the
        /// same reason), not the run's level: a stamped creature at 420 in a 375 run is softened from 375 up.
        /// </summary>
        public const int DefenseSoftenLevel = 375;

        /// <summary>The compiled default of dynamic_dungeons_defense_curve_rate_above_375 (owner ruling 2026-10-08).</summary>
        public const double DefaultDefenseRateAbove375 = 0.5;

        /// <summary>The fitted slope for <paramref name="axis"/>, before any softening.</summary>
        public static double Slope(DungeonStatAxis axis)
        {
            switch (axis)
            {
                case DungeonStatAxis.Damage: return DamageSlope;
                case DungeonStatAxis.Armor: return ArmorSlope;
                case DungeonStatAxis.Strength: return StrengthSlope;
                case DungeonStatAxis.Endurance: return EnduranceSlope;
                case DungeonStatAxis.Quickness: return QuicknessSlope;
                case DungeonStatAxis.Coordination: return CoordinationSlope;
                case DungeonStatAxis.Focus: return FocusSlope;
                case DungeonStatAxis.Self: return SelfSlope;
                case DungeonStatAxis.WeaponAttack: return WeaponAttackSlope;
                case DungeonStatAxis.MagicAttack: return MagicAttackSlope;
                case DungeonStatAxis.MeleeDefense: return MeleeDefenseSlope;
                case DungeonStatAxis.MissileDefense: return MissileDefenseSlope;
                case DungeonStatAxis.MagicDefense: return MagicDefenseSlope;
                default: return 0.0;
            }
        }

        /// <summary>True for the three EFFECTIVE defense axes, the only ones the softening touches.</summary>
        public static bool IsDefense(DungeonStatAxis axis)
            => axis == DungeonStatAxis.MeleeDefense || axis == DungeonStatAxis.MissileDefense || axis == DungeonStatAxis.MagicDefense;

        /// <summary>
        /// Sanitizes the softening factor into [0, 1]: NaN or Infinity reads as the default; a negative value
        /// reads as 0 (defense frozen at its 375 value - a falling defense curve is the one reading an admin
        /// cannot have meant); anything above 1 reads as 1 (the unsoftened curve - "softening" that STEEPENS is
        /// not softening). Pure, so the spawner's reader can warn against the same arithmetic.
        /// </summary>
        public static double SanitizeDefenseRate(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return DefaultDefenseRateAbove375;

            return Math.Clamp(value, 0.0, 1.0);
        }

        /// <summary>
        /// The curve's multiplier between two levels on one axis: Standard(<paramref name="toLevel"/>) /
        /// Standard(<paramref name="fromLevel"/>). Anchor-free, so it is also how a stamped creature is scaled
        /// from its authored level to its stamp ("preserving authored spread": every creature at the same raw
        /// and stamp levels is multiplied by the same factor, so the order within a pack survives).
        ///
        /// The exponent is the integral of the axis slope from one level to the other, with a defense axis's
        /// slope multiplied by <paramref name="defenseRate"/> wherever the path lies above
        /// <see cref="DefenseSoftenLevel"/>. That makes it continuous at 375 by construction, and makes
        /// Ratio(a, c) == Ratio(a, b) * Ratio(b, c) for any b. Below the pivot the same log-linear line is simply
        /// extended backward; that only matters for a creature authored below the pivot, and only as a ratio.
        /// </summary>
        public static double Ratio(DungeonStatAxis axis, int fromLevel, int toLevel, double defenseRate)
        {
            if (fromLevel == toLevel)
                return 1.0;

            var lo = Math.Min(fromLevel, toLevel);
            var hi = Math.Max(fromLevel, toLevel);
            var slope = Slope(axis);
            double exponent;

            if (IsDefense(axis))
            {
                var rate = SanitizeDefenseRate(defenseRate);
                var below = Math.Max(0, Math.Min(hi, DefenseSoftenLevel) - lo);
                var above = Math.Max(0, hi - Math.Max(lo, DefenseSoftenLevel));
                exponent = slope * (below + rate * above);
            }
            else
            {
                exponent = slope * (hi - lo);
            }

            return Math.Exp(toLevel > fromLevel ? exponent : -exponent);
        }

        /// <summary>The axis an attack or defense skill's EFFECTIVE value follows, or null for a skill the curve does not move.</summary>
        public static DungeonStatAxis? AxisOf(Skill skill)
        {
            switch (skill)
            {
                case Skill.MeleeDefense: return DungeonStatAxis.MeleeDefense;
                case Skill.MissileDefense: return DungeonStatAxis.MissileDefense;
                case Skill.MagicDefense: return DungeonStatAxis.MagicDefense;
                case Skill.WarMagic:
                case Skill.LifeMagic:
                case Skill.VoidMagic:
                case Skill.CreatureEnchantment:
                    return DungeonStatAxis.MagicAttack;
            }

            return Array.IndexOf(DungeonCombatNormalizer.AttackSkills, skill) >= 0 ? DungeonStatAxis.WeaponAttack : (DungeonStatAxis?)null;
        }

        /// <summary>The axis a primary attribute follows, or null for Undef.</summary>
        public static DungeonStatAxis? AxisOf(PropertyAttribute attribute)
        {
            switch (attribute)
            {
                case PropertyAttribute.Strength: return DungeonStatAxis.Strength;
                case PropertyAttribute.Endurance: return DungeonStatAxis.Endurance;
                case PropertyAttribute.Quickness: return DungeonStatAxis.Quickness;
                case PropertyAttribute.Coordination: return DungeonStatAxis.Coordination;
                case PropertyAttribute.Focus: return DungeonStatAxis.Focus;
                case PropertyAttribute.Self: return DungeonStatAxis.Self;
                default: return null;
            }
        }

        /// <summary>
        /// <paramref name="value"/> times <paramref name="ratio"/>, rounded half away from zero and saturated
        /// into uint. A non-zero value never rounds to 0, so "no authored data" (0) stays distinguishable from
        /// a tiny one - the convention every DungeonStatProfile axis relies on.
        /// </summary>
        public static uint Scale(uint value, double ratio)
        {
            if (value == 0 || double.IsNaN(ratio) || double.IsInfinity(ratio) || ratio <= 0)
                return value;

            return (uint)Math.Clamp(Math.Round(value * ratio, MidpointRounding.AwayFromZero), 1, uint.MaxValue);
        }

        /// <summary>
        /// The InitLevel that puts a skill's effective value on <paramref name="targetEffective"/> given the
        /// (curve-raised) attribute term <paramref name="fixedTerm"/>, floored at 0. This is the line that keeps
        /// the defense softening honest: the attributes climb on their OWN slopes, and an InitLevel left alone
        /// (or scaled by the defense ratio) would let that climb carry the effective value past its softened
        /// target. It may therefore LOWER an InitLevel, which is correct - the effective value is the standard.
        /// </summary>
        public static uint InitFor(uint targetEffective, uint fixedTerm)
            => targetEffective > fixedTerm ? targetEffective - fixedTerm : 0u;

        /// <summary>
        /// The band standard at <paramref name="level"/>, extrapolated from <paramref name="anchor"/> (the
        /// measured standard at <paramref name="pivot"/>). Returns the anchor itself at or below the pivot, and
        /// for an empty or null anchor (the "normalize nothing" no-op stays a no-op).
        ///
        /// Per field:
        ///   - MaxBodyDamage, MaxBaseArmor, every attribute median and every EFFECTIVE skill median: the anchor
        ///     value times <see cref="Ratio"/> on its axis (defense softened above 375);
        ///   - the InitLevel medians (SkillMedians) of every attack and defense skill: re-derived so a creature
        ///     SET to this standard - attributes to the attribute medians, InitLevel to this median, which is what
        ///     boss normalization does - lands on the anchor's attribute-term-plus-median carried up the same
        ///     curve: InitLevel(M) = round(effective(P) * ratio) - attributeTerm(attributes(M)), floored at 0;
        ///   - every other skill's InitLevel median (Run, Deception, ...), the spell tier, the health regen rate,
        ///     the sample count and the sample low ratio: unchanged. None of them has a fitted slope, and the
        ///     spell tier is already capped by the spell table.
        ///
        /// <paramref name="formulaOf"/> is consulted only when the anchor carries attribute medians, so a
        /// standard with none never touches the dat-backed formula table. A skill with no formula row has no
        /// attribute term and is scaled as InitLevel alone.
        /// </summary>
        public static DungeonBandStandard Extrapolate(DungeonBandStandard anchor, int pivot, int level, double defenseRate,
            Func<Skill, SkillFormula> formulaOf = null)
        {
            if (anchor == null || anchor.IsEmpty || pivot <= 0 || level <= pivot)
                return anchor ?? DungeonBandStandard.Empty;

            double R(DungeonStatAxis axis) => Ratio(axis, pivot, level, defenseRate);

            var attributes = new Dictionary<PropertyAttribute, uint>(anchor.AttributeMedians.Count);

            foreach (var kvp in anchor.AttributeMedians)
            {
                var axis = AxisOf(kvp.Key);
                attributes[kvp.Key] = axis.HasValue ? Scale(kvp.Value, R(axis.Value)) : kvp.Value;
            }

            var effective = new Dictionary<Skill, uint>(anchor.EffectiveSkillMedians.Count);

            foreach (var kvp in anchor.EffectiveSkillMedians)
            {
                var axis = AxisOf(kvp.Key);
                effective[kvp.Key] = axis.HasValue ? Scale(kvp.Value, R(axis.Value)) : kvp.Value;
            }

            var hasAttributes = anchor.AttributeMedians.Count > 0;
            formulaOf ??= DungeonBandStandard.DefaultFormulaOf;

            uint AttributeTerm(Skill skill, IReadOnlyDictionary<PropertyAttribute, uint> attrs)
                => hasAttributes ? AttributeFormula.Compute(formulaOf(skill), a => attrs.TryGetValue(a, out var v) ? v : 0u) : 0u;

            var inits = new Dictionary<Skill, uint>(anchor.SkillMedians.Count);

            foreach (var kvp in anchor.SkillMedians)
            {
                var axis = AxisOf(kvp.Key);

                if (!axis.HasValue || kvp.Value == 0)
                {
                    inits[kvp.Key] = kvp.Value;
                    continue;
                }

                var anchorEffective = AttributeTerm(kvp.Key, anchor.AttributeMedians) + kvp.Value;
                var target = Scale(anchorEffective, R(axis.Value));

                inits[kvp.Key] = InitFor(target, AttributeTerm(kvp.Key, attributes));
            }

            return DungeonBandStandard.Create(inits,
                Scale(anchor.MaxBodyDamage, R(DungeonStatAxis.Damage)),
                Scale(anchor.MaxBaseArmor, R(DungeonStatAxis.Armor)),
                anchor.SpellTier, anchor.SampleCount, anchor.SampleLowRatio, anchor.HealthRate, attributes, effective);
        }

        /// <summary>
        /// One run's curve: the measured anchor standard, its pivot, and the resolved softening factor. Carried
        /// on the plan (DungeonSpawnPlan.StatCurve) so the spawner can ask for the standard at ANY level - a
        /// stamped creature's own level, for its defense cap - and for the scaling ratio between two levels,
        /// without re-deriving any of it.
        ///
        /// Immutable apart from a memo of the standards already extrapolated, which is a pure function of the
        /// level and is guarded by a lock: a run's placement steps run one at a time on its landblock, but the
        /// plan object is not otherwise promised to one thread, and the memo is cheap to guard.
        /// </summary>
        public sealed class Anchored
        {
            private readonly Func<Skill, SkillFormula> formulaOf;
            private readonly Dictionary<int, DungeonBandStandard> memo = new Dictionary<int, DungeonBandStandard>();

            public Anchored(DungeonBandStandard anchor, int pivot, double defenseRate, Func<Skill, SkillFormula> formulaOf = null)
            {
                Anchor = anchor ?? DungeonBandStandard.Empty;
                Pivot = pivot;
                DefenseRate = SanitizeDefenseRate(defenseRate);
                this.formulaOf = formulaOf;
            }

            /// <summary>The measured band standard at <see cref="Pivot"/>.</summary>
            public DungeonBandStandard Anchor { get; }

            public int Pivot { get; }

            /// <summary>The sanitized softening factor in force for this run.</summary>
            public double DefenseRate { get; }

            /// <summary>The curve standard at <paramref name="level"/>; the anchor itself at or below the pivot.</summary>
            public DungeonBandStandard StandardAt(int level)
            {
                lock (memo)
                {
                    if (memo.TryGetValue(level, out var cached))
                        return cached;

                    var value = Extrapolate(Anchor, Pivot, level, DefenseRate, formulaOf);
                    memo[level] = value;
                    return value;
                }
            }

            /// <summary><see cref="DungeonStatCurve.Ratio"/> at this run's softening factor.</summary>
            public double Ratio(DungeonStatAxis axis, int fromLevel, int toLevel)
                => DungeonStatCurve.Ratio(axis, fromLevel, toLevel, DefenseRate);
        }
    }
}
