using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldEvents.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// Owner ruling 2026-10-29: above the stat-curve pivot the Damage axis grows at
    /// dynamic_dungeons_damage_curve_rate_above_pivot (default 0.75) of its fitted slope. Pins that 1.0 is the old curve
    /// exactly, the softened ratio above the pivot and the identity below it, that the damage dial touches no other axis
    /// and the defense dials do not touch damage, that each of the three rates reaches its own axis through every
    /// consumer (Anchored, the archetype extrapolation and context, the damage ceiling's projection), the reader, the
    /// registration and the baseline memo key.
    /// </summary>
    [TestClass]
    public class DamageCurveRateTests
    {
        private const string DamageKey = "dynamic_dungeons_damage_curve_rate_above_pivot";
        private const int P = 326;
        private const double B = DungeonStatCurve.DamageSlope;

        private static readonly int[] Levels = { 350, 410, 450, 500 };

        [TestMethod]
        public void Rate_one_is_the_pre_dial_curve_exactly()
        {
            foreach (var (from, to) in new[] { (300, 410), (326, 500), (250, 300), (100, 326), (375, 431), (410, 326) })
            {
                var old = Math.Exp(B * (to - from));
                Assert.AreEqual(old, DungeonStatCurve.Ratio(DungeonStatAxis.Damage, from, to, 0.5, 0.0, 1.0, P), 1e-12, $"{from}->{to}");
                Assert.AreEqual(old, DungeonStatCurve.Ratio(DungeonStatAxis.Damage, from, to, 0.5, 0.0, 0.75, 0), 1e-12, $"{from}->{to}: no pivot, no softening");
            }
        }

        [TestMethod]
        public void Rate_three_quarters_softens_only_the_stretch_above_the_pivot()
        {
            foreach (var level in Levels)
                Assert.AreEqual(Math.Exp(0.75 * B * (level - P)), DungeonStatCurve.Ratio(DungeonStatAxis.Damage, P, level, 0.5, 0.0, 0.75, P), 1e-12, $"pivot->{level}");

            Assert.AreEqual(Math.Exp(B * 76), DungeonStatCurve.Ratio(DungeonStatAxis.Damage, 250, 326, 0.5, 0.0, 0.75, P), 1e-12, "at/below the pivot: full slope");
            Assert.AreEqual(Math.Exp(B * 26 + 0.75 * B * 84), DungeonStatCurve.Ratio(DungeonStatAxis.Damage, 300, 410, 0.5, 0.0, 0.75, P), 1e-12, "split at the pivot");
            Assert.AreEqual(DungeonStatCurve.Ratio(DungeonStatAxis.Damage, 300, 326, 0.5, 0.0, 0.75, P) * DungeonStatCurve.Ratio(DungeonStatAxis.Damage, 326, 500, 0.5, 0.0, 0.75, P),
                DungeonStatCurve.Ratio(DungeonStatAxis.Damage, 300, 500, 0.5, 0.0, 0.75, P), 1e-12, "continuous (composes) through the pivot");
            Assert.AreEqual(1.0 / DungeonStatCurve.Ratio(DungeonStatAxis.Damage, 326, 450, 0.5, 0.0, 0.75, P),
                DungeonStatCurve.Ratio(DungeonStatAxis.Damage, 450, 326, 0.5, 0.0, 0.75, P), 1e-12, "downward is the inverse");

            // The ruling's own numbers: about -19% at 410 and -36% at 500 against the full slope.
            Assert.AreEqual(0.809, Math.Exp(-0.25 * B * (410 - P)), 0.005);
            Assert.AreEqual(0.644, Math.Exp(-0.25 * B * (500 - P)), 0.005);
        }

        [TestMethod]
        public void The_damage_dial_touches_no_other_axis_and_the_defense_dials_do_not_touch_damage()
        {
            foreach (DungeonStatAxis axis in Enum.GetValues(typeof(DungeonStatAxis)))
            {
                if (axis == DungeonStatAxis.Damage)
                    continue;

                var reference = DungeonStatCurve.Ratio(axis, 300, 500, 0.5, 0.0, 1.0, P);
                Assert.AreEqual(reference, DungeonStatCurve.Ratio(axis, 300, 500, 0.5, 0.0, 0.75, P), 1e-15, $"{axis}: damage 0.75");
                Assert.AreEqual(reference, DungeonStatCurve.Ratio(axis, 300, 500, 0.5, 0.0, 0.0, P), 1e-15, $"{axis}: damage 0");
            }

            var damage = DungeonStatCurve.Ratio(DungeonStatAxis.Damage, 300, 500, 0.5, 0.0, 0.75, P);
            Assert.AreEqual(damage, DungeonStatCurve.Ratio(DungeonStatAxis.Damage, 300, 500, 0.0, 1.0, 0.75, P), 1e-15, "defense and magic dials leave damage alone");
        }

        [TestMethod]
        public void The_damage_sanitizer_clamps_and_reads_garbage_as_its_own_default()
        {
            Assert.AreEqual(0.75, DungeonStatCurve.SanitizeDamageRate(double.NaN));
            Assert.AreEqual(0.75, DungeonStatCurve.SanitizeDamageRate(double.NegativeInfinity));
            Assert.AreEqual(0.0, DungeonStatCurve.SanitizeDamageRate(-2));
            Assert.AreEqual(1.0, DungeonStatCurve.SanitizeDamageRate(7));
            Assert.AreEqual(0.4, DungeonStatCurve.SanitizeDamageRate(0.4));
            Assert.AreEqual(0.75, DungeonStatCurve.DefaultDamageRateAbovePivot);
        }

        private static DungeonBandStandard Anchor()
        {
            var medians = new Dictionary<Skill, uint> { [Skill.MagicDefense] = 533, [Skill.MeleeDefense] = 820, [Skill.MissileDefense] = 745 };
            return DungeonBandStandard.ForTest(medians, 844, 540, sampleCount: 20, effectiveSkillMedians: new Dictionary<Skill, uint>(medians));
        }

        [TestMethod]
        public void Each_of_the_three_rates_reaches_its_own_axis_through_the_anchored_curve()
        {
            // Three distinct rates: a swap of any two arguments moves at least one assertion.
            var curve = new DungeonStatCurve.Anchored(Anchor(), P, 0.25, 0.5, 0.75);
            Assert.AreEqual(0.25, curve.DefenseRate);
            Assert.AreEqual(0.5, curve.MagicDefenseRate);
            Assert.AreEqual(0.75, curve.DamageRate);

            var at410 = curve.StandardAt(410);
            Assert.AreEqual(DungeonStatCurve.Scale(844, Math.Exp(0.75 * B * 84)), at410.MaxBodyDamage, "damage at 0.75");
            Assert.AreEqual(DungeonStatCurve.Scale(820, Math.Exp(0.25 * DungeonStatCurve.MeleeDefenseSlope * 84)), at410.EffectiveSkillMedianFor(Skill.MeleeDefense), "melee at 0.25");
            Assert.AreEqual((uint)Math.Round(DungeonStatCurve.RetailMagicDefense(410, 0.5), MidpointRounding.AwayFromZero), at410.EffectiveSkillMedianFor(Skill.MagicDefense), "magic at 0.5");
            Assert.AreEqual(DungeonStatCurve.Scale(540, Math.Exp(DungeonStatCurve.ArmorSlope * 84)), at410.MaxBaseArmor, "armour is not damage: full slope");

            // The stamp ratio (ReachUpStamp reads curve.Ratio) splits damage at the curve's own pivot.
            Assert.AreEqual(Math.Exp(B * 26 + 0.75 * B * 84), curve.Ratio(DungeonStatAxis.Damage, 300, 410), 1e-12);
            Assert.AreEqual(Math.Exp(DungeonStatCurve.MeleeDefenseSlope * 0.25 * 110), curve.Ratio(DungeonStatAxis.MeleeDefense, 300, 410), 1e-12);
            Assert.AreEqual(Math.Exp(DungeonStatCurve.StrengthSlope * 110), curve.Ratio(DungeonStatAxis.Strength, 300, 410), 1e-12, "attributes: full slope");

            // 1.0 reproduces the pre-dial standard exactly.
            var old = new DungeonStatCurve.Anchored(Anchor(), P, 0.25, 0.5, 1.0).StandardAt(410);
            Assert.AreEqual(DungeonStatCurve.Scale(844, Math.Exp(B * 84)), old.MaxBodyDamage);
            Assert.AreEqual(old.EffectiveSkillMedianFor(Skill.MeleeDefense), at410.EffectiveSkillMedianFor(Skill.MeleeDefense));
        }

        [TestMethod]
        public void The_archetype_extrapolation_and_context_carry_the_damage_rate()
        {
            var axes = new ArchetypeAxes(Anchor(), 1000, 0, 0, 0, 0, 0, 100000, 20, 1.0);

            var soft = DungeonArchetypeStandards.Extrapolate(axes, P, 500, 0.25, 0.5, 0.75);
            var full = DungeonArchetypeStandards.Extrapolate(axes, P, 500, 0.25, 0.5, 1.0);
            Assert.AreEqual(1000 * Math.Exp(0.75 * B * 174), soft.EffectiveDamage, 1e-6, "effective damage at 0.75");
            Assert.AreEqual(1000 * Math.Exp(B * 174), full.EffectiveDamage, 1e-6, "1.0 is the old value");
            Assert.AreEqual(DungeonStatCurve.Scale(844, Math.Exp(0.75 * B * 174)), soft.Core.MaxBodyDamage);
            Assert.AreEqual(full.Core.EffectiveSkillMedianFor(Skill.MeleeDefense), soft.Core.EffectiveSkillMedianFor(Skill.MeleeDefense), "defense unchanged");
            Assert.AreEqual(full.SpellOutput, soft.SpellOutput, 1e-12, "spells stay flat");
            Assert.AreEqual(full.DamageRating, soft.DamageRating, 1e-12, "ratings stay flat");
        }

        [TestMethod]
        public void The_damage_ceiling_projection_takes_the_run_curve()
        {
            var raw = new CreaturePerHit(300, new PerHitSource(1000, PropertyAttribute.Strength, 400, false), new PerHitSource(800, PropertyAttribute.Coordination, 420, true));
            var soft = new DungeonStatCurve.Anchored(Anchor(), P, 0.5, 0.0, 0.75);
            var full = new DungeonStatCurve.Anchored(Anchor(), P, 0.5, 0.0, 1.0);

            var up = raw.ProjectedTo(410, soft.Ratio);
            Assert.AreEqual(1000 * Math.Exp(B * 26 + 0.75 * B * 84), up.Melee.BaseDamage, 1e-9, "damage on the run's softened curve");
            Assert.AreEqual(1000 * Math.Exp(B * 110), raw.ProjectedTo(410, full.Ratio).Melee.BaseDamage, 1e-9, "1.0: the old projection");
            Assert.AreEqual(raw.ProjectedTo(410, full.Ratio).Melee.AttributeValue, up.Melee.AttributeValue, "the attribute rides its own axis");

            // The baseline's prediction is the same projection the plan's curve gives.
            var (placement, predicted) = DamageCeilingStandard.Predict(1, raw, 410, 300, 400, 410, soft.Ratio, null);
            Assert.AreEqual(DamageCeilingPlacement.Stamped, placement);
            Assert.AreEqual(up.Melee.BaseDamage, predicted.Melee.BaseDamage, 1e-9);

            Assert.ThrowsExactly<ArgumentNullException>(() => raw.ProjectedTo(410, null), "the curve is required");
        }

        /// <summary>
        /// The family ability planner's per-entry prediction (FamilyAbilityPlanner.PredictEntry) stamps a STAMPED entry on
        /// the plan's own curve, so the damage dial reaches it: 0.75 and 1.0 predict different hits, by exactly the
        /// softened stretch above the pivot (code review 2026-10-29 F1c: a hardcoded 1.0 there broke no test).
        /// </summary>
        [TestMethod]
        public void The_family_planners_entry_prediction_stamps_on_the_plans_damage_rate()
        {
            const int From = 350, To = 450;
            var raw = new CreaturePerHit(From, new PerHitSource(1000, PropertyAttribute.Strength, 200, false, isBody: true), default);
            var entry = new DungeonSpawnPlanEntry(1, DungeonRole.Trash, null, stampLevel: To);

            double PredictedAt(double damageRate)
            {
                var plan = new DungeonSpawnPlan { FamilyId = "fam_stamp", StatCurve = new DungeonStatCurve.Anchored(Anchor(), P, 0.5, 0.0, damageRate) };
                return FamilyAbilityPlanner.PredictEntry(plan, entry, raw, upliftAttributes: false).Melee.BaseDamage;
            }

            var soft = PredictedAt(0.75);
            var full = PredictedAt(1.0);

            Assert.AreEqual(1000 * Math.Exp(0.75 * B * (To - From)), soft, 1e-9, "0.75: the whole stretch is above the pivot");
            Assert.AreEqual(1000 * Math.Exp(B * (To - From)), full, 1e-9, "1.0: the full slope");
            Assert.AreEqual(Math.Exp(0.25 * B * (To - From)), full / soft, 1e-12, "the two differ by exactly the softened slope");
        }

        [TestMethod]
        public void The_limits_carry_the_damage_dial_and_the_memo_keys_on_it()
        {
            var shipped = new DungeonPopulationLimits(120, 2.0, 3.0, 8, 0.5);
            Assert.AreEqual(0.75, shipped.DamageCurveRateAbovePivot, 1e-12);
            var full = new DungeonPopulationLimits(120, 2.0, 3.0, 8, 0.5, damageCurveRateAbovePivot: 1.0).WithMonsterCounts(5, 10).WithForcedFamilyWidening();
            Assert.AreEqual(1.0, full.DamageCurveRateAbovePivot, 1e-12, "the copy constructors carry it");

            var tables = new Dictionary<string, SpeciesTableDef>();
            Assert.AreEqual(new ThreadPlanCache.BaselineKey(tables, 410, shipped, 0, true, true), new ThreadPlanCache.BaselineKey(tables, 410, shipped, 0, true, true));
            Assert.AreNotEqual(new ThreadPlanCache.BaselineKey(tables, 410, shipped, 0, true, true),
                new ThreadPlanCache.BaselineKey(tables, 410, new DungeonPopulationLimits(120, 2.0, 3.0, 8, 0.5, damageCurveRateAbovePivot: 1.0), 0, true, true));

            var memo = ThreadPlanMemo.Scratch(w => 0, null, null, w => null);
            var computes = 0;
            DamageCeilingBaseline Compute() { computes++; return DamageCeilingBaseline.Empty; }

            memo.DamageCeilingBaseline(tables, 410, shipped, 0, true, true, Compute);
            memo.DamageCeilingBaseline(tables, 410, shipped, 0, true, true, Compute);
            memo.DamageCeilingBaseline(tables, 410, new DungeonPopulationLimits(120, 2.0, 3.0, 8, 0.5, damageCurveRateAbovePivot: 1.0), 0, true, true, Compute);
            Assert.AreEqual(2, computes, "a changed damage dial recomputes the baseline");
        }

        [TestMethod]
        public void The_damage_dial_is_registered_and_ReadLimits_sanitizes_it()
        {
            Assert.AreEqual(0.75, ACE.Server.Managers.DefaultPropertyManager.DefaultDoubleProperties[DamageKey].Item, 1e-12);
            Assert.AreEqual(DungeonPopulationLimits.DefaultDamageCurveRateAbovePivot, ACE.Server.Managers.DefaultPropertyManager.DefaultDoubleProperties[DamageKey].Item, 1e-12);

            // ReadLimits reads every Threads key; seed every registered default so none falls through to the shard DB.
            ACE.Server.Managers.DefaultPropertyManager.LoadDefaultProperties();

            try
            {
                Assert.IsTrue(ACE.Server.Managers.PropertyManager.ModifyDouble(DamageKey, 7.0), "ModifyDouble returned false: key not registered");
                Assert.AreEqual(1.0, ThreadDungeonSpawner.ReadLimits(GroupScaling.Solo).DamageCurveRateAbovePivot, 1e-12, "above 1 reads as 1");

                Assert.IsTrue(ACE.Server.Managers.PropertyManager.ModifyDouble(DamageKey, double.NaN));
                Assert.AreEqual(0.75, ThreadDungeonSpawner.ReadLimits(GroupScaling.Solo).DamageCurveRateAbovePivot, 1e-12, "NaN reads as the default");

                Assert.IsTrue(ACE.Server.Managers.PropertyManager.ModifyDouble(DamageKey, 0.4));
                Assert.AreEqual(0.4, ThreadDungeonSpawner.ReadLimits(GroupScaling.Solo).DamageCurveRateAbovePivot, 1e-12, "positive control: a legal value passes");
            }
            finally
            {
                ACE.Server.Managers.PropertyManager.ModifyDouble(DamageKey, DungeonPopulationLimits.DefaultDamageCurveRateAbovePivot);
            }

            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadDungeonSpawner.cs"),
                "internal static DungeonPopulationLimits ReadLimits(GroupScaling group)");
            StringAssert.Contains(body, "damageCurveRateAbovePivot: ReadDamageCurveRateDial()");
        }
    }
}
