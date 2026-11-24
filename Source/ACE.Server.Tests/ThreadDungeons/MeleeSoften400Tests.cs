using System;
using System.Collections.Generic;
using System.Linq;

using ACE.DatLoader.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldEvents;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The 400 knee (owner ruling 2026-11-17): above monster level 400 the WeaponAttack and MeleeDefense axes take their
    /// own flat rates (default 0.33 and 0.19 of the fitted slope), about +15 points each from 400 to 500. Pure curve math: no
    /// PropertyManager read, so nothing here depends on another test class having seeded a key.
    /// </summary>
    [TestClass]
    public class MeleeSoften400Tests
    {
        private const double A = DungeonStatCurve.WeaponAttackSlope;
        private const double D = DungeonStatCurve.MeleeDefenseSlope;
        private const double Half = DungeonStatCurve.DefaultDefenseRateAbove375;
        private const double W = DungeonStatCurve.DefaultWeaponAttackRateAbove400;
        private const double M = DungeonStatCurve.DefaultMeleeDefenseRateAbove400;

        private static double Atk(int from, int to, double rate400)
            => DungeonStatCurve.Ratio(DungeonStatAxis.WeaponAttack, from, to, Half, 0.0, 0.75, 326, rate400, M);

        private static double Mel(int from, int to, double rate400)
            => DungeonStatCurve.Ratio(DungeonStatAxis.MeleeDefense, from, to, Half, 0.0, 0.75, 326, W, rate400);

        [TestMethod]
        public void Below_400_both_axes_are_the_pre_knee_curve_whatever_the_400_rates_are()
        {
            foreach (var (from, to) in new[] { (100, 400), (326, 400), (250, 380), (400, 326), (390, 300) })
                foreach (var rate in new[] { 0.0, 0.33, 1.0 })
                {
                    Assert.AreEqual(Math.Exp(A * (to - from)), Atk(from, to, rate), 1e-12, $"attack {from}->{to} at {rate}");
                }

            // Melee defense below 400: full slope under 300, the 0.5 dial between 300 and 400, as on origin/master.
            Assert.AreEqual(Math.Exp(D * 4 + Half * D * 100), Mel(296, 400, 0.0), 1e-12);
            Assert.AreEqual(Math.Exp(Half * D * 74), Mel(326, 400, 0.77), 1e-12);
            Assert.AreEqual(Math.Exp(D * 50), Mel(250, 300, 0.77), 1e-12);
        }

        [TestMethod]
        public void Above_400_the_slope_is_the_fitted_slope_times_the_rate_and_replaces_the_defense_rate()
        {
            Assert.AreEqual(Math.Exp(A * W * 100), Atk(400, 500, W), 1e-12);
            Assert.AreEqual(Math.Exp(D * M * 100), Mel(400, 500, M), 1e-12, "a fraction of the FULL slope, not of the 0.5-softened one");
            Assert.AreEqual(Math.Exp(A * 74 + A * W * 100), Atk(326, 500, W), 1e-12);
            Assert.AreEqual(Math.Exp(Half * D * 74 + D * M * 100), Mel(326, 500, M), 1e-12);
            Assert.AreEqual(Math.Exp(D * 4 + Half * D * 100 + D * M * 10), Mel(296, 410, M), 1e-12, "a span straddling 300 and 400");
            Assert.AreEqual(Math.Exp(A * 90 + A * W * 10), Atk(310, 410, W), 1e-12, "a span straddling 326 and 400");

            // Downward is the inverse, and the ratio composes through 400 (continuity).
            Assert.AreEqual(1.0 / Atk(326, 500, W), Atk(500, 326, W), 1e-12);
            Assert.AreEqual(1.0 / Mel(326, 500, M), Mel(500, 326, M), 1e-12);
            Assert.AreEqual(Atk(326, 400, W) * Atk(400, 500, W), Atk(326, 500, W), 1e-12);
            Assert.AreEqual(Mel(250, 400, M) * Mel(400, 500, M), Mel(250, 500, M), 1e-12);

            // Continuous at 400: one level either side differs only by that level's own slope.
            Assert.AreEqual(Math.Exp(A * W), Atk(400, 401, W), 1e-12);
            Assert.AreEqual(Math.Exp(A), Atk(399, 400, W), 1e-12);
            Assert.AreEqual(Math.Exp(D * M), Mel(400, 401, M), 1e-12);
            Assert.AreEqual(Math.Exp(D * Half), Mel(399, 400, M), 1e-12);
        }

        [TestMethod]
        public void Rate_zero_is_flat_above_400_and_rate_one_on_attack_is_the_old_unsoftened_curve()
        {
            Assert.AreEqual(1.0, Atk(400, 500, 0.0));
            Assert.AreEqual(1.0, Mel(400, 500, 0.0));
            Assert.IsTrue(Atk(400, 500, 0.01) > 1.0 && Mel(400, 500, 0.01) > 1.0, "monotone: any positive rate rises");

            // The pre-knee WeaponAttack curve was exp(slope * levels) everywhere.
            foreach (var (from, to) in new[] { (326, 500), (300, 410), (400, 500), (410, 326) })
                Assert.AreEqual(Math.Exp(A * (to - from)), Atk(from, to, 1.0), 1e-12, $"{from}->{to}");

            // Rate 0.5 on melee defense reproduces the pre-knee melee curve exactly (it is the same dial value).
            Assert.AreEqual(Math.Exp(Half * D * 174), Mel(326, 500, Half), 1e-12);
        }

        [TestMethod]
        public void Only_weapon_attack_and_melee_defense_see_the_knee()
        {
            foreach (DungeonStatAxis axis in Enum.GetValues(typeof(DungeonStatAxis)))
            {
                if (axis == DungeonStatAxis.WeaponAttack || axis == DungeonStatAxis.MeleeDefense)
                    continue;

                var reference = DungeonStatCurve.Ratio(axis, 326, 500, Half, 0.0, 0.75, 326, 1.0, 1.0);
                Assert.AreEqual(reference, DungeonStatCurve.Ratio(axis, 326, 500, Half, 0.0, 0.75, 326, 0.0, 0.0), 1e-15, $"{axis}");
                Assert.AreEqual(reference, DungeonStatCurve.Ratio(axis, 326, 500, Half, 0.0, 0.75, 326, DungeonStatCurve.DefaultWeaponAttackRateAbove400, DungeonStatCurve.DefaultMeleeDefenseRateAbove400), 1e-15, $"{axis} (defaults)");
            }

            // Missile defense keeps the 0.5 dial above 400.
            Assert.AreEqual(Math.Exp(Half * DungeonStatCurve.MissileDefenseSlope * 174),
                DungeonStatCurve.Ratio(DungeonStatAxis.MissileDefense, 326, 500, Half, 0.0, 0.75, 326, 0.0, 0.0), 1e-12);
        }

        [TestMethod]
        public void The_400_rates_are_sanitized_into_zero_to_one_and_nan_reads_as_the_default()
        {
            Assert.AreEqual(W, DungeonStatCurve.SanitizeWeaponAttackRate(double.NaN));
            Assert.AreEqual(W, DungeonStatCurve.SanitizeWeaponAttackRate(double.PositiveInfinity));
            Assert.AreEqual(W, DungeonStatCurve.SanitizeWeaponAttackRate(double.NegativeInfinity));
            Assert.AreEqual(0.0, DungeonStatCurve.SanitizeWeaponAttackRate(-3));
            Assert.AreEqual(1.0, DungeonStatCurve.SanitizeWeaponAttackRate(7));
            Assert.AreEqual(0.25, DungeonStatCurve.SanitizeWeaponAttackRate(0.25));

            Assert.AreEqual(M, DungeonStatCurve.SanitizeMeleeDefenseRate(double.NaN));
            Assert.AreEqual(M, DungeonStatCurve.SanitizeMeleeDefenseRate(double.PositiveInfinity));
            Assert.AreEqual(0.0, DungeonStatCurve.SanitizeMeleeDefenseRate(-3));
            Assert.AreEqual(1.0, DungeonStatCurve.SanitizeMeleeDefenseRate(7));
            Assert.AreEqual(0.25, DungeonStatCurve.SanitizeMeleeDefenseRate(0.25));

            // Ratio sanitizes too: an out-of-range rate acts as its clamp, NaN as the default.
            Assert.AreEqual(Atk(400, 500, 1.0), Atk(400, 500, 9.0), 1e-15);
            Assert.AreEqual(Atk(400, 500, 0.0), Atk(400, 500, -1.0), 1e-15);
            Assert.AreEqual(Atk(400, 500, W), Atk(400, 500, double.NaN), 1e-15);
            Assert.AreEqual(Mel(400, 500, M), Mel(400, 500, double.NaN), 1e-15);

            Assert.AreEqual(0.33, W);
            Assert.AreEqual(0.19, M);
            Assert.AreEqual(W, DungeonPopulationLimits.DefaultWeaponAttackCurveRateAbove400);
            Assert.AreEqual(M, DungeonPopulationLimits.DefaultMeleeDefenseCurveRateAbove400);
        }

        // ---- the calibration, on the real level-326 standards ---------------------------------------------

        private static SkillFormula Formula(PropertyAttribute a, PropertyAttribute b, uint divisor) => new SkillFormula(a, b, divisor);

        private static SkillFormula FormulaOf(Skill skill)
        {
            switch (skill)
            {
                case Skill.HeavyWeapons: return Formula(PropertyAttribute.Strength, PropertyAttribute.Coordination, 3);
                case Skill.MeleeDefense: return Formula(PropertyAttribute.Quickness, PropertyAttribute.Coordination, 3);
                case Skill.MissileDefense: return Formula(PropertyAttribute.Quickness, PropertyAttribute.Coordination, 5);
                case Skill.MagicDefense: return Formula(PropertyAttribute.Self, PropertyAttribute.Focus, 7);
                case Skill.WarMagic:
                case Skill.LifeMagic: return Formula(PropertyAttribute.Focus, PropertyAttribute.Self, 4);
                default: return null;
            }
        }

        /// <summary>The measured level-326 standard (attack 810, melee defense 820), the same anchor ThreadsRunCeilingTests uses.</summary>
        private static DungeonBandStandard Anchor()
        {
            var attrs = new Dictionary<PropertyAttribute, uint>
            {
                [PropertyAttribute.Strength] = 410, [PropertyAttribute.Endurance] = 410, [PropertyAttribute.Quickness] = 360,
                [PropertyAttribute.Coordination] = 410, [PropertyAttribute.Focus] = 580, [PropertyAttribute.Self] = 580,
            };
            var effective = new Dictionary<Skill, uint>
            {
                [Skill.HeavyWeapons] = 810, [Skill.MeleeDefense] = 820, [Skill.MissileDefense] = 745,
                [Skill.MagicDefense] = 630, [Skill.WarMagic] = 680, [Skill.LifeMagic] = 680,
            };
            var inits = effective.ToDictionary(kvp => kvp.Key, kvp => kvp.Value - AttributeFormula.Compute(FormulaOf(kvp.Key), a => attrs[a]));

            return DungeonBandStandard.ForTest(inits, 844, 540, sampleCount: 20, attributeMedians: attrs, effectiveSkillMedians: effective);
        }

        private static DungeonStatCurve.Anchored Curve(double weaponAttack400, double meleeDefense400)
            => new DungeonStatCurve.Anchored(Anchor(), 326, DungeonStatCurve.DefaultDefenseRateAbove375, DungeonStatCurve.DefaultMagicDefenseRateAbove300,
                DungeonStatCurve.DefaultDamageRateAbovePivot, weaponAttack400, meleeDefense400, FormulaOf);

        [TestMethod]
        public void The_shipped_defaults_raise_attack_and_melee_defense_about_15_points_from_400_to_500()
        {
            var curve = Curve(W, M);
            var s400 = curve.StandardAt(400);
            var s500 = curve.StandardAt(500);

            // Level 400 is unchanged by the knee (it is the knee).
            Assert.AreEqual(842u, s400.EffectiveSkillMedianFor(Skill.HeavyWeapons), "attack at 400");
            Assert.AreEqual(849u, s400.EffectiveSkillMedianFor(Skill.MeleeDefense), "melee D at 400");

            var attackGain = (int)s500.EffectiveSkillMedianFor(Skill.HeavyWeapons) - (int)s400.EffectiveSkillMedianFor(Skill.HeavyWeapons);
            var meleeGain = (int)s500.EffectiveSkillMedianFor(Skill.MeleeDefense) - (int)s400.EffectiveSkillMedianFor(Skill.MeleeDefense);

            Assert.AreEqual(15, attackGain, 3, $"attack 400->500 gained {attackGain}");
            Assert.AreEqual(15, meleeGain, 3, $"melee D 400->500 gained {meleeGain}");

            // The 450 midpoint sits between, still rising.
            var s450 = curve.StandardAt(450);
            Assert.IsTrue(s450.EffectiveSkillMedianFor(Skill.HeavyWeapons) > s400.EffectiveSkillMedianFor(Skill.HeavyWeapons));
            Assert.IsTrue(s450.EffectiveSkillMedianFor(Skill.HeavyWeapons) < s500.EffectiveSkillMedianFor(Skill.HeavyWeapons));
            Assert.IsTrue(s450.EffectiveSkillMedianFor(Skill.MeleeDefense) > s400.EffectiveSkillMedianFor(Skill.MeleeDefense));
            Assert.IsTrue(s450.EffectiveSkillMedianFor(Skill.MeleeDefense) < s500.EffectiveSkillMedianFor(Skill.MeleeDefense));

            // Missile defense and magic defense are the unchanged curve.
            var old = Curve(1.0, DungeonStatCurve.DefaultDefenseRateAbove375);
            Assert.AreEqual(old.StandardAt(500).EffectiveSkillMedianFor(Skill.MissileDefense), s500.EffectiveSkillMedianFor(Skill.MissileDefense));
            Assert.AreEqual(old.StandardAt(500).EffectiveSkillMedianFor(Skill.MagicDefense), s500.EffectiveSkillMedianFor(Skill.MagicDefense));

            // The old curve gained about 46 and 41 over the same stretch: the knee is what removes the difference.
            Assert.AreEqual(46, (int)old.StandardAt(500).EffectiveSkillMedianFor(Skill.HeavyWeapons) - (int)old.StandardAt(400).EffectiveSkillMedianFor(Skill.HeavyWeapons), 1);
            Assert.AreEqual(41, (int)old.StandardAt(500).EffectiveSkillMedianFor(Skill.MeleeDefense) - (int)old.StandardAt(400).EffectiveSkillMedianFor(Skill.MeleeDefense), 1);
        }

        [TestMethod]
        public void The_anchored_curve_carries_both_rates_to_the_standard_and_to_stamp_ratios()
        {
            var curve = Curve(0.6, 0.4);
            Assert.AreEqual(0.6, curve.WeaponAttackRateAbove400);
            Assert.AreEqual(0.4, curve.MeleeDefenseRateAbove400);

            Assert.AreEqual(DungeonStatCurve.Ratio(DungeonStatAxis.WeaponAttack, 326, 450, Half, 0.0, 0.75, 326, 0.6, 0.4), curve.Ratio(DungeonStatAxis.WeaponAttack, 326, 450), 1e-15);
            Assert.AreEqual(DungeonStatCurve.Ratio(DungeonStatAxis.MeleeDefense, 326, 450, Half, 0.0, 0.75, 326, 0.6, 0.4), curve.Ratio(DungeonStatAxis.MeleeDefense, 326, 450), 1e-15);

            // Swapped rates move the standard: a discriminating pin that each rate reaches ITS axis.
            var swapped = Curve(0.4, 0.6);
            Assert.AreNotEqual(curve.StandardAt(500).EffectiveSkillMedianFor(Skill.HeavyWeapons), swapped.StandardAt(500).EffectiveSkillMedianFor(Skill.HeavyWeapons));
            Assert.AreNotEqual(curve.StandardAt(500).EffectiveSkillMedianFor(Skill.MeleeDefense), swapped.StandardAt(500).EffectiveSkillMedianFor(Skill.MeleeDefense));

            // Sanitized on construction.
            Assert.AreEqual(1.0, Curve(5.0, 5.0).WeaponAttackRateAbove400);
            Assert.AreEqual(W, Curve(double.NaN, double.NaN).WeaponAttackRateAbove400);
            Assert.AreEqual(M, Curve(double.NaN, double.NaN).MeleeDefenseRateAbove400);
        }

        // ---- World Events keeps its old curve ------------------------------------------------------------

        // Golden values computed by a temporary probe running origin/master e65241d0e (before this change): the level-326
        // anchor above, Anchored(anchor, 326, 0.5, 0.0, 0.75).StandardAt(level) and Ratio(axis, 326, level), the dials World
        // Events passes. They are literals so this pin cannot compare the new code with itself.
        private static readonly Dictionary<int, (uint Dmg, uint Armor, uint Atk, uint MelD, uint MisD, uint MagD, uint War, uint AtkInit, uint MelInit, uint Str, uint Quick)> MasterStandards =
            new Dictionary<int, (uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint)>
            {
                [410] = (1597, 632, 847, 853, 777, 520, 760, 527, 561, 492, 406),
                [500] = (3162, 749, 888, 890, 812, 520, 857, 508, 556, 598, 461),
            };

        private static readonly Dictionary<(int Level, DungeonStatAxis Axis), double> MasterRatios =
            new Dictionary<(int, DungeonStatAxis), double>
            {
                [(410, DungeonStatAxis.WeaponAttack)] = 1.0455258870190922,
                [(410, DungeonStatAxis.MeleeDefense)] = 1.0402696932830369,
                [(410, DungeonStatAxis.MissileDefense)] = 1.0424565550401046,
                [(500, DungeonStatAxis.WeaponAttack)] = 1.0966060488756493,
                [(500, DungeonStatAxis.MeleeDefense)] = 1.0852170358174908,
                [(500, DungeonStatAxis.MissileDefense)] = 1.0899480123370708,
            };

        private static DungeonStatCurve.Anchored WorldEventCurve()
            => new DungeonStatCurve.Anchored(Anchor(), 326, WorldEventBandMemo.CurveDefenseRate, WorldEventBandMemo.CurveMagicDefenseRate,
                WorldEventBandMemo.CurveDamageRate, WorldEventBandMemo.CurveWeaponAttackRateAbove400, WorldEventBandMemo.CurveMeleeDefenseRateAbove400, FormulaOf);

        [TestMethod]
        public void World_events_curve_at_410_and_500_equals_origin_master()
        {
            var curve = WorldEventCurve();

            foreach (var kvp in MasterStandards)
            {
                var s = curve.StandardAt(kvp.Key);
                var e = kvp.Value;
                var ctx = $"level {kvp.Key}";
                Assert.AreEqual(e.Dmg, s.MaxBodyDamage, ctx + " dmg");
                Assert.AreEqual(e.Armor, s.MaxBaseArmor, ctx + " armor");
                Assert.AreEqual(e.Atk, s.EffectiveSkillMedianFor(Skill.HeavyWeapons), ctx + " attack");
                Assert.AreEqual(e.MelD, s.EffectiveSkillMedianFor(Skill.MeleeDefense), ctx + " melee D");
                Assert.AreEqual(e.MisD, s.EffectiveSkillMedianFor(Skill.MissileDefense), ctx + " missile D");
                Assert.AreEqual(e.MagD, s.EffectiveSkillMedianFor(Skill.MagicDefense), ctx + " magic D");
                Assert.AreEqual(e.War, s.EffectiveSkillMedianFor(Skill.WarMagic), ctx + " war");
                Assert.AreEqual(e.AtkInit, s.MedianFor(Skill.HeavyWeapons), ctx + " attack InitLevel");
                Assert.AreEqual(e.MelInit, s.MedianFor(Skill.MeleeDefense), ctx + " melee D InitLevel");
                Assert.AreEqual(e.Str, s.AttributeMedianFor(PropertyAttribute.Strength), ctx + " Str");
                Assert.AreEqual(e.Quick, s.AttributeMedianFor(PropertyAttribute.Quickness), ctx + " Quick");
            }

            foreach (var kvp in MasterRatios)
                Assert.AreEqual(kvp.Value, curve.Ratio(kvp.Key.Axis, 326, kvp.Key.Level), 1e-12, $"{kvp.Key.Axis} 326->{kvp.Key.Level}");

            // Positive control: the Threads defaults DO differ at 500, so the pin above is not vacuous.
            Assert.AreNotEqual(MasterStandards[500].Atk, Curve(W, M).StandardAt(500).EffectiveSkillMedianFor(Skill.HeavyWeapons));
            Assert.AreNotEqual(MasterStandards[500].MelD, Curve(W, M).StandardAt(500).EffectiveSkillMedianFor(Skill.MeleeDefense));
        }

        [TestMethod]
        public void World_events_hands_the_pre_knee_rates_to_both_curve_consumers()
        {
            Assert.AreEqual(1.0, WorldEventBandMemo.CurveWeaponAttackRateAbove400);
            Assert.AreEqual(0.5, WorldEventBandMemo.CurveMeleeDefenseRateAbove400);

            // The damage ceiling's Threads baseline limits (the other World Events reader of these dials).
            Assert.AreEqual(WorldEventBandMemo.CurveWeaponAttackRateAbove400, WorldEventDamageCeiling.ThreadsLimits.WeaponAttackCurveRateAbove400);
            Assert.AreEqual(WorldEventBandMemo.CurveMeleeDefenseRateAbove400, WorldEventDamageCeiling.ThreadsLimits.MeleeDefenseCurveRateAbove400);
        }

        // ---- the baseline memo key ---------------------------------------------------------------------------

        [TestMethod]
        public void A_changed_400_dial_is_a_different_baseline_key()
        {
            var shipped = new DungeonPopulationLimits(60, 3.0, 3.0, 8, 0.5);
            var attack = new DungeonPopulationLimits(60, 3.0, 3.0, 8, 0.5, weaponAttackCurveRateAbove400: 0.5);
            var melee = new DungeonPopulationLimits(60, 3.0, 3.0, 8, 0.5, meleeDefenseCurveRateAbove400: 0.5);

            Assert.AreEqual(W, shipped.WeaponAttackCurveRateAbove400);
            Assert.AreEqual(M, shipped.MeleeDefenseCurveRateAbove400);
            Assert.AreNotEqual(ThreadBaseline.DialsKey(shipped), ThreadBaseline.DialsKey(attack));
            Assert.AreNotEqual(ThreadBaseline.DialsKey(shipped), ThreadBaseline.DialsKey(melee));
            Assert.AreNotEqual(ThreadBaseline.DialsKey(attack), ThreadBaseline.DialsKey(melee));
        }
    }
}
