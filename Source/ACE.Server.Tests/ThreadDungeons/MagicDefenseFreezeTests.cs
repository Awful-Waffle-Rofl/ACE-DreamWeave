using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

using ACE.Entity.Enum;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldEvents.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// Owner ruling 2026-10-28: monster MAGIC defense stops growing above monster level 300
    /// (dynamic_dungeons_magic_defense_curve_rate_above_300, default 0), while melee and missile defense keep the
    /// existing dynamic_dungeons_defense_curve_rate_above_375 softening. Pins the frozen curve, that 0.5 reproduces
    /// the pre-split curve exactly, that each dial moves only its own axes, that the damage ceiling's baseline memo
    /// keys on the new dial, and that ReadLimits reads it.
    /// </summary>
    [TestClass]
    public class MagicDefenseFreezeTests
    {
        private const string MagicKey = "dynamic_dungeons_magic_defense_curve_rate_above_300";

        private static readonly int[] AboveLevels = { 301, 350, 410, 500 };

        /// <summary>The pre-split formula, written out: 19.365 * 300^0.5769 * (L / 300)^(0.5769 * 0.5).</summary>
        private static double PreSplit(int level)
            => 19.365 * Math.Pow(300, 0.5769) * Math.Pow(level / 300.0, 0.5769 * 0.5);

        [TestMethod]
        public void Magic_rate_0_freezes_retail_magic_defense_at_its_level_300_value()
        {
            var at300 = DungeonStatCurve.RetailMagicDefense(300, 0.0);
            Assert.AreEqual(520.0, at300, 0.5, "positive control: the retail fit gives about 520 at 300");

            foreach (var level in AboveLevels)
                Assert.AreEqual(at300, DungeonStatCurve.RetailMagicDefense(level, 0.0), 1e-9, $"frozen at {level}");

            Assert.AreEqual(at300, DungeonStatCurve.RetailMagicDefense(410, DungeonStatCurve.DefaultMagicDefenseRateAbove300), 1e-9,
                "the shipped default is the frozen curve");
            Assert.AreEqual(0.0, DungeonStatCurve.DefaultMagicDefenseRateAbove300);
        }

        [TestMethod]
        public void Magic_rate_half_reproduces_the_pre_split_values_exactly()
        {
            foreach (var level in AboveLevels)
            {
                Assert.AreEqual(PreSplit(level), DungeonStatCurve.RetailMagicDefense(level, 0.5), 1e-9, $"level {level}");
                Assert.IsTrue(DungeonStatCurve.RetailMagicDefense(level, 0.5) > DungeonStatCurve.RetailMagicDefense(level, 0.0),
                    $"discriminating: 0.5 still grows at {level}");
            }

            // Below and at 300 the dial is irrelevant.
            Assert.AreEqual(DungeonStatCurve.RetailMagicDefense(300, 0.5), DungeonStatCurve.RetailMagicDefense(300, 0.0), 1e-12);
            Assert.AreEqual(DungeonStatCurve.RetailMagicDefense(250, 1.0), DungeonStatCurve.RetailMagicDefense(250, 0.0), 1e-12);
        }

        [TestMethod]
        public void Ratio_routes_each_dial_to_its_own_axes_only()
        {
            foreach (var axis in new[] { DungeonStatAxis.MeleeDefense, DungeonStatAxis.MissileDefense })
            {
                var reference = DungeonStatCurve.Ratio(axis, 326, 410, 0.5, 0.5, 1.0, 326);
                Assert.AreEqual(reference, DungeonStatCurve.Ratio(axis, 326, 410, 0.5, 0.0, 1.0, 326), 1e-15, $"{axis}: magic dial 0 does not move it");
                Assert.AreEqual(reference, DungeonStatCurve.Ratio(axis, 326, 410, 0.5, 1.0, 1.0, 326), 1e-15, $"{axis}: magic dial 1 does not move it");
                Assert.AreNotEqual(reference, DungeonStatCurve.Ratio(axis, 326, 410, 0.0, 0.5, 1.0, 326), $"{axis}: positive control, its own dial moves it");
            }

            var magic = DungeonStatCurve.Ratio(DungeonStatAxis.MagicDefense, 326, 410, 0.5, 0.0, 1.0, 326);
            Assert.AreEqual(1.0, magic, 1e-12, "magic frozen: 326 and 410 are both above 300, so the ratio is 1");
            Assert.AreEqual(magic, DungeonStatCurve.Ratio(DungeonStatAxis.MagicDefense, 326, 410, 1.0, 0.0, 1.0, 326), 1e-15, "the melee dial does not move magic");
            Assert.AreEqual(PreSplit(410) / PreSplit(326), DungeonStatCurve.Ratio(DungeonStatAxis.MagicDefense, 326, 410, 0.0, 0.5, 1.0, 326), 1e-12,
                "magic at 0.5 is the pre-split ratio whatever the melee dial is");
        }

        private static DungeonBandStandard DefenseAnchor()
        {
            var medians = new Dictionary<Skill, uint>
            {
                [Skill.MagicDefense] = 533, [Skill.MeleeDefense] = 820, [Skill.MissileDefense] = 745,
            };

            // No attribute medians: the attribute term is 0, so InitLevel and effective medians coincide.
            return DungeonBandStandard.ForTest(medians, 844, 540, sampleCount: 20, effectiveSkillMedians: new Dictionary<Skill, uint>(medians));
        }

        [TestMethod]
        public void Extrapolate_moves_only_magic_defense_with_the_magic_dial()
        {
            var anchor = DefenseAnchor();
            var frozen = DungeonStatCurve.Extrapolate(anchor, 326, 410, 0.5, 0.0, 1.0);
            var half = DungeonStatCurve.Extrapolate(anchor, 326, 410, 0.5, 0.5, 1.0);

            Assert.AreEqual(520u, frozen.EffectiveSkillMedianFor(Skill.MagicDefense), "frozen magic D at 410");
            Assert.AreEqual(520u, frozen.MedianFor(Skill.MagicDefense));
            Assert.AreEqual(569u, half.EffectiveSkillMedianFor(Skill.MagicDefense), "pre-split magic D at 410 (pinned in ThreadsRunCeilingTests)");

            foreach (var skill in new[] { Skill.MeleeDefense, Skill.MissileDefense })
            {
                Assert.AreEqual(half.EffectiveSkillMedianFor(skill), frozen.EffectiveSkillMedianFor(skill), $"{skill} effective unchanged");
                Assert.AreEqual(half.MedianFor(skill), frozen.MedianFor(skill), $"{skill} InitLevel unchanged");
                Assert.IsTrue(frozen.EffectiveSkillMedianFor(skill) > anchor.EffectiveSkillMedianFor(skill), $"{skill} still grows (softened)");
            }

            foreach (var level in AboveLevels)
            {
                var curve = new DungeonStatCurve.Anchored(anchor, 326, 0.5, 0.0, 1.0);
                Assert.AreEqual(520u, curve.StandardAt(level).EffectiveSkillMedianFor(Skill.MagicDefense), $"Anchored, frozen at {level}");
            }
        }

        [TestMethod]
        public void WithRetailMagicDefense_and_the_archetype_extrapolation_take_the_magic_dial()
        {
            var anchor = DefenseAnchor();
            Assert.AreEqual(520u, DungeonStatCurve.WithRetailMagicDefense(anchor, 500, 0.0).EffectiveSkillMedianFor(Skill.MagicDefense));
            Assert.AreEqual((uint)Math.Round(PreSplit(500), MidpointRounding.AwayFromZero),
                DungeonStatCurve.WithRetailMagicDefense(anchor, 500, 0.5).EffectiveSkillMedianFor(Skill.MagicDefense));

            var axes = new ArchetypeAxes(anchor, 1000, 0, 0, 0, 0, 0, 100000, 20, 1.0);
            var frozen = DungeonArchetypeStandards.Extrapolate(axes, 326, 500, 0.5, 0.0, 1.0);
            var half = DungeonArchetypeStandards.Extrapolate(axes, 326, 500, 0.5, 0.5, 1.0);
            Assert.AreEqual(520u, frozen.Core.EffectiveSkillMedianFor(Skill.MagicDefense));
            Assert.AreNotEqual(520u, half.Core.EffectiveSkillMedianFor(Skill.MagicDefense), "discriminating: 0.5 grows");
            Assert.AreEqual(half.Core.EffectiveSkillMedianFor(Skill.MeleeDefense), frozen.Core.EffectiveSkillMedianFor(Skill.MeleeDefense));
            Assert.AreEqual(half.EffectiveDamage, frozen.EffectiveDamage, 1e-9);
        }

        [TestMethod]
        public void The_magic_sanitizer_clamps_to_zero_to_one_and_reads_garbage_as_its_own_default()
        {
            Assert.AreEqual(0.0, DungeonStatCurve.SanitizeMagicDefenseRate(double.NaN));
            Assert.AreEqual(0.0, DungeonStatCurve.SanitizeMagicDefenseRate(double.PositiveInfinity));
            Assert.AreEqual(0.0, DungeonStatCurve.SanitizeMagicDefenseRate(-1));
            Assert.AreEqual(1.0, DungeonStatCurve.SanitizeMagicDefenseRate(7));
            Assert.AreEqual(0.25, DungeonStatCurve.SanitizeMagicDefenseRate(0.25));

            // The melee/missile dial's own sanitizer is unchanged.
            Assert.AreEqual(0.5, DungeonStatCurve.SanitizeDefenseRate(double.NaN));
        }

        [TestMethod]
        public void The_limits_carry_the_magic_dial_and_the_old_dial_keeps_its_default()
        {
            var shipped = new DungeonPopulationLimits(120, 2.0, 3.0, 8, 0.5);
            Assert.AreEqual(0.0, shipped.MagicDefenseCurveRateAbove300, 1e-12);
            Assert.AreEqual(0.5, shipped.DefenseCurveRateAbove375, 1e-12, "the existing dial keeps its default");
            Assert.AreEqual(0.0, default(DungeonPopulationLimits).MagicDefenseCurveRateAbove300, 1e-12, "default(...) reads the shipped value");

            var half = new DungeonPopulationLimits(120, 2.0, 3.0, 8, 0.5, magicDefenseCurveRateAbove300: 0.5)
                .WithMonsterCounts(5, 10).WithForcedFamilyWidening();
            Assert.AreEqual(0.5, half.MagicDefenseCurveRateAbove300, 1e-12, "the copy constructors carry it");
        }

        [TestMethod]
        public void The_damage_ceiling_baseline_memo_keys_on_the_magic_dial()
        {
            var tables = new Dictionary<string, SpeciesTableDef>();
            var frozen = new DungeonPopulationLimits(120, 2.0, 3.0, 8, 0.5);
            var half = new DungeonPopulationLimits(120, 2.0, 3.0, 8, 0.5, magicDefenseCurveRateAbove300: 0.5);

            Assert.AreEqual(new ThreadPlanCache.BaselineKey(tables, 410, frozen, 0, true, true), new ThreadPlanCache.BaselineKey(tables, 410, frozen, 0, true, true),
                "positive control: identical limits are one key");
            Assert.AreNotEqual(new ThreadPlanCache.BaselineKey(tables, 410, frozen, 0, true, true), new ThreadPlanCache.BaselineKey(tables, 410, half, 0, true, true),
                "a changed magic dial is a different key");

            // End to end through the memo: a live dial change recomputes, an unchanged one is served from cache.
            var memo = ThreadPlanMemo.Scratch(w => 0, null, null, w => null);
            var computes = 0;
            DamageCeilingBaseline Compute() { computes++; return DamageCeilingBaseline.Empty; }

            memo.DamageCeilingBaseline(tables, 410, frozen, 0, true, true, Compute);
            memo.DamageCeilingBaseline(tables, 410, frozen, 0, true, true, Compute);
            Assert.AreEqual(1, computes, "same limits: served from the memo");

            memo.DamageCeilingBaseline(tables, 410, half, 0, true, true, Compute);
            Assert.AreEqual(2, computes, "magic dial changed: recomputed");
        }

        [TestMethod]
        public void The_magic_dial_is_registered_at_its_compiled_default_and_takes_a_live_override()
        {
            Assert.IsTrue(ACE.Server.Managers.DefaultPropertyManager.DefaultDoubleProperties.ContainsKey(MagicKey));
            Assert.AreEqual(DungeonPopulationLimits.DefaultMagicDefenseCurveRateAbove300,
                ACE.Server.Managers.DefaultPropertyManager.DefaultDoubleProperties[MagicKey].Item, 1e-12);
            Assert.AreEqual(0.0, ACE.Server.Managers.DefaultPropertyManager.DefaultDoubleProperties[MagicKey].Item, 1e-12, "owner ruling 2026-10-28: frozen");
            Assert.AreEqual(0.5, ACE.Server.Managers.DefaultPropertyManager.DefaultDoubleProperties["dynamic_dungeons_defense_curve_rate_above_375"].Item, 1e-12,
                "the existing dial's default is unchanged");

            try
            {
                Assert.IsTrue(ACE.Server.Managers.PropertyManager.ModifyDouble(MagicKey, 0.5), "ModifyDouble returned false: key not registered");
                Assert.AreEqual(0.5, ACE.Server.Managers.PropertyManager.GetDouble(MagicKey, 0.0).Item, 1e-12);
            }
            finally
            {
                ACE.Server.Managers.PropertyManager.ModifyDouble(MagicKey, DungeonPopulationLimits.DefaultMagicDefenseCurveRateAbove300);
            }
        }

        [TestMethod]
        public void ReadLimits_reads_the_magic_dial_through_its_own_sanitizer()
        {
            var source = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadDungeonSpawner.cs");
            var body = PooledLootSourceText.MethodBody(source, "internal static DungeonPopulationLimits ReadLimits(GroupScaling group)");
            StringAssert.Contains(body, "magicDefenseCurveRateAbove300: ReadMagicDefenseCurveRateDial()");
            StringAssert.Contains(body, "defenseCurveRateAbove375: ReadDefenseCurveRateDial()");

            var reader = PooledLootSourceText.MethodBody(source, "private static double ReadMagicDefenseCurveRateDial()");
            StringAssert.Contains(reader, "\"" + MagicKey + "\"");
            StringAssert.Contains(reader, "DungeonPopulationLimits.DefaultMagicDefenseCurveRateAbove300");
            StringAssert.Contains(reader, "DungeonStatCurve.SanitizeMagicDefenseRate(configured)");
        }

        [TestMethod]
        public void ReadLimits_sanitizes_the_live_magic_dial()
        {
            // ReadLimits reads every Threads key; an uncached key falls through to the shard DB (none under this harness), so
            // seed every registered default into the cache first, the same way BloodMageScaffoldTests does.
            ACE.Server.Managers.DefaultPropertyManager.LoadDefaultProperties();

            try
            {
                Assert.IsTrue(ACE.Server.Managers.PropertyManager.ModifyDouble(MagicKey, 7.0), "ModifyDouble returned false: key not registered");
                Assert.AreEqual(1.0, ThreadDungeonSpawner.ReadLimits(GroupScaling.Solo).MagicDefenseCurveRateAbove300, 1e-12, "above 1 reads as 1");

                Assert.IsTrue(ACE.Server.Managers.PropertyManager.ModifyDouble(MagicKey, double.NaN));
                Assert.AreEqual(0.0, ThreadDungeonSpawner.ReadLimits(GroupScaling.Solo).MagicDefenseCurveRateAbove300, 1e-12, "NaN reads as the default, 0");

                Assert.IsTrue(ACE.Server.Managers.PropertyManager.ModifyDouble(MagicKey, 0.5));
                Assert.AreEqual(0.5, ThreadDungeonSpawner.ReadLimits(GroupScaling.Solo).MagicDefenseCurveRateAbove300, 1e-12, "positive control: a legal value passes through");
            }
            finally
            {
                ACE.Server.Managers.PropertyManager.ModifyDouble(MagicKey, DungeonPopulationLimits.DefaultMagicDefenseCurveRateAbove300);
            }
        }

        [TestMethod]
        public void Every_production_WithRetailMagicDefense_call_passes_a_magic_rate()
        {
            // The magic paths that are hard to drive end to end (the archetype context's non-extrapolated run end, its
            // own-level ends, the builder's below-pivot standard) all go through WithRetailMagicDefense. Its rate
            // argument must be the magic dial at every call, never the melee/missile DefenseRate.
            var calls = 0;

            foreach (var file in new[] { "DungeonArchetypeStandards.cs", "DungeonPopulationBuilder.cs", "DungeonStatCurve.cs" })
            {
                var source = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/" + file);

                foreach (Match m in Regex.Matches(source, @"(?<!static DungeonBandStandard )WithRetailMagicDefense\(([^,]+),([^,]+),([^,)]+)"))
                {
                    calls++;
                    StringAssert.Contains(m.Groups[3].Value, "Magic", $"{file}: {m.Value}");
                }
            }

            Assert.AreEqual(5, calls, "positive control: every known call site was scanned (update the count when one is added)");
        }
    }
}
