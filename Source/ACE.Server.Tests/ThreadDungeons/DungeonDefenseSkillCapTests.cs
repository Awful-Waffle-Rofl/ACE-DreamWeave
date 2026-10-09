using System.Collections.Generic;
using System.Linq;

using ACE.DatLoader.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The Threads effective-defense-skill cap (dynamic_dungeons_defense_skill_cap_offset), which replaced an
    /// InitLevel-only ratio cap that could not touch an attribute-heavy retail weenie (wcid 46700 Crazed
    /// Olthoi: melee defense InitLevel 320 but ~1000 in every primary attribute, for an EFFECTIVE melee defense
    /// near 987 that a 2.0x InitLevel cap of 640 never saw). Covers three layers, each a pure static helper so
    /// none of it needs a live Creature or a loaded portal.dat:
    ///   1. AttributeFormula.Compute - the attribute-formula arithmetic itself, taken off an attribute
    ///      dictionary instead of a live Creature.
    ///   2. DungeonBandStandard.Compute's effective-median derivation - which sample members contribute, and
    ///      which are excluded.
    ///   3. DungeonCombatNormalizer.DefenseSkillCap / ClampToDefenseCeiling - the cap arithmetic and the
    ///      runtime-ceiling clamp CreatureSkill.Base/Current applies.
    /// </summary>
    [TestClass]
    public class DungeonDefenseSkillCapTests
    {
        // ---- 1. AttributeFormula.Compute ------------------------------------------------------------------

        private static SkillFormula TwoAttrFormula(PropertyAttribute attr1, PropertyAttribute attr2, uint divisor)
            => new SkillFormula(attr1, attr2, divisor);

        private static SkillFormula SingleAttrFormula(PropertyAttribute attr1, uint divisor)
            => new SkillFormula(attr1, PropertyAttribute.Undef, divisor);

        [TestMethod]
        public void Compute_sums_two_attributes_then_divides()
        {
            // MeleeDefense-shaped: (Coordination + Quickness) / 3, matching Creature_Combat's own divisor family.
            var formula = TwoAttrFormula(PropertyAttribute.Coordination, PropertyAttribute.Quickness, 3);

            var attributes = new Dictionary<PropertyAttribute, uint> { [PropertyAttribute.Coordination] = 1000, [PropertyAttribute.Quickness] = 1000 };

            // (1000 + 1000) / 3 = 666.67, rounds to 667 (FloatExtensions.Round uses MidpointRounding.AwayFromZero-equivalent banker-free rounding).
            Assert.AreEqual(667u, AttributeFormula.Compute(formula, attr => attributes.TryGetValue(attr, out var v) ? v : 0u));
        }

        [TestMethod]
        public void Compute_single_attribute_formula_ignores_attr2_when_undef()
        {
            // MagicDefense-shaped: (Focus + Self) / 7.
            var formula = TwoAttrFormula(PropertyAttribute.Focus, PropertyAttribute.Self, 7);
            var attributes = new Dictionary<PropertyAttribute, uint> { [PropertyAttribute.Focus] = 700, [PropertyAttribute.Self] = 700 };

            Assert.AreEqual(200u, AttributeFormula.Compute(formula, attr => attributes.TryGetValue(attr, out var v) ? v : 0u));

            var single = SingleAttrFormula(PropertyAttribute.Focus, 7);
            Assert.AreEqual(100u, AttributeFormula.Compute(single, attr => attributes.TryGetValue(attr, out var v) ? v : 0u),
                "attr2 is Undef - only attr1 contributes");
        }

        [TestMethod]
        public void Compute_missing_attribute_reads_as_zero_and_zero_formula_is_a_noop()
        {
            var formula = TwoAttrFormula(PropertyAttribute.Coordination, PropertyAttribute.Quickness, 3);

            Assert.AreEqual(0u, AttributeFormula.Compute(formula, attr => 0u), "no attribute data - zero contribution");
            Assert.AreEqual(0u, AttributeFormula.Compute(null, attr => 1000u), "null formula");

            var zeroFormula = new SkillFormula(PropertyAttribute.Coordination, PropertyAttribute.Undef, 1) { X = 0 };
            Assert.AreEqual(0u, AttributeFormula.Compute(zeroFormula, attr => throw new System.Exception("must not be called when X == 0")));
        }

        // ---- 2. DungeonBandStandard's effective-median derivation -----------------------------------------

        private static readonly Skill DefenseSkill = Skill.MeleeDefense;
        private static SkillFormula MeleeDefenseFormula => TwoAttrFormula(PropertyAttribute.Coordination, PropertyAttribute.Quickness, 3);

        private static DungeonStatProfile ProfileWith(uint initLevel, uint coordination, uint quickness)
            => new DungeonStatProfile(200, 1000, new Dictionary<Skill, uint> { [DefenseSkill] = initLevel }, 0, 0,
                attributes: new Dictionary<PropertyAttribute, uint> { [PropertyAttribute.Coordination] = coordination, [PropertyAttribute.Quickness] = quickness });

        private static DungeonStatProfile ProfileNoAttributes(uint initLevel)
            => new DungeonStatProfile(200, 1000, new Dictionary<Skill, uint> { [DefenseSkill] = initLevel }, 0, 0);

        private static DungeonBandStandard ComputeFromSample(IReadOnlyDictionary<string, ACE.Server.WorldEvents.Defs.SpeciesTableDef> tables,
            IReadOnlyDictionary<uint, DungeonStatProfile> profiles)
        {
            return DungeonBandStandard.Compute(tables, 200, wcid => profiles.TryGetValue(wcid, out var p) ? p : DungeonStatProfile.Empty,
                new DungeonRosterBand(1.0, 1.15), 1.0, formulaOf: skill => skill == DefenseSkill ? MeleeDefenseFormula : null);
        }

        private static (IReadOnlyDictionary<string, ACE.Server.WorldEvents.Defs.SpeciesTableDef> Tables, Dictionary<uint, DungeonStatProfile> Profiles)
            BuildSample(params (uint Wcid, DungeonStatProfile Profile)[] members)
        {
            var table = new ACE.Server.WorldEvents.Defs.SpeciesTableDef
            {
                Members = members.Select(m => new ACE.Server.WorldEvents.Defs.SpeciesMemberDef { Wcid = m.Wcid, Role = 0 }).ToList(),
            };

            var tables = new Dictionary<string, ACE.Server.WorldEvents.Defs.SpeciesTableDef> { ["family"] = table };
            var profiles = members.ToDictionary(m => m.Wcid, m => m.Profile);

            return (tables, profiles);
        }

        [TestMethod]
        public void Effective_median_is_the_attribute_term_plus_initlevel_over_odd_and_even_samples()
        {
            // Odd sample of 5: effective = InitLevel + (Coord+Quick)/3.
            var odd = BuildSample(
                (1, ProfileWith(100, 300, 300)),  // 100 + 200 = 300
                (2, ProfileWith(200, 300, 300)),  // 200 + 200 = 400
                (3, ProfileWith(300, 300, 300)),  // 300 + 200 = 500
                (4, ProfileWith(400, 300, 300)),  // 400 + 200 = 600
                (5, ProfileWith(500, 300, 300))); // 500 + 200 = 700

            var std = ComputeFromSample(odd.Tables, odd.Profiles);
            Assert.AreEqual(500u, std.EffectiveDefenseMedianFor(DefenseSkill), "lower median of 5 distinct values is the middle one");

            // Even sample of 6: LowerMedian takes the LOWER of the two middles, never an average.
            var even = BuildSample(
                (1, ProfileWith(100, 300, 300)),
                (2, ProfileWith(200, 300, 300)),
                (3, ProfileWith(300, 300, 300)),
                (4, ProfileWith(400, 300, 300)),
                (5, ProfileWith(500, 300, 300)),
                (6, ProfileWith(600, 300, 300)));

            var stdEven = ComputeFromSample(even.Tables, even.Profiles);
            Assert.AreEqual(500u, stdEven.EffectiveDefenseMedianFor(DefenseSkill), "6 values: lower of the two middles (500, 600) is 500");
        }

        [TestMethod]
        public void A_member_without_the_skill_is_excluded_from_the_effective_sample()
        {
            var sample = BuildSample(
                (1, ProfileWith(100, 300, 300)),
                (2, ProfileWith(200, 300, 300)),
                (3, ProfileWith(300, 300, 300)),
                (4, ProfileWith(0, 300, 300)),   // InitLevel 0 for the skill - excluded, same rule SkillMediansOf uses
                (5, ProfileNoAttributes(400)));  // no attribute rows at all - excluded, never read as zero attributes

            var std = ComputeFromSample(sample.Tables, sample.Profiles);

            // Only members 1, 2, 3 contribute: effective values 300, 400, 500. Lower median of 3 is the middle: 400.
            Assert.AreEqual(400u, std.EffectiveDefenseMedianFor(DefenseSkill));
        }

        [TestMethod]
        public void A_skill_with_no_effective_data_reads_as_zero()
        {
            var sample = BuildSample((1, ProfileNoAttributes(100)), (2, ProfileNoAttributes(200)));

            var std = ComputeFromSample(sample.Tables, sample.Profiles);

            Assert.AreEqual(0u, std.EffectiveDefenseMedianFor(DefenseSkill), "every member excluded (no attributes) - no standard");
            Assert.AreEqual(0u, std.EffectiveDefenseMedianFor(Skill.MagicDefense), "skill never present at all");
        }

        // ---- 3. Cap arithmetic and the ceiling clamp ------------------------------------------------------

        [TestMethod]
        public void Cap_is_the_effective_median_plus_the_offset()
        {
            var std = DungeonBandStandard.ForTest(new Dictionary<Skill, uint>(), 0, 0,
                effectiveDefenseMedians: new Dictionary<Skill, uint> { [Skill.MeleeDefense] = 987 });

            Assert.AreEqual(1087u, DungeonCombatNormalizer.DefenseSkillCap(std, Skill.MeleeDefense, 100.0));
            Assert.AreEqual(987u, DungeonCombatNormalizer.DefenseSkillCap(std, Skill.MeleeDefense, 0.0), "offset 0 caps exactly at the median");
            Assert.AreEqual(0u, DungeonCombatNormalizer.DefenseSkillCap(std, Skill.MeleeDefense, -0.01), "any negative offset disables");
            Assert.AreEqual(0u, DungeonCombatNormalizer.DefenseSkillCap(std, Skill.MissileDefense, 100.0), "no median for this skill");
            Assert.AreEqual(0u, DungeonCombatNormalizer.DefenseSkillCap(DungeonBandStandard.Empty, Skill.MeleeDefense, 100.0), "empty standard");
        }

        [TestMethod]
        public void Ceiling_clamp_never_raises_a_value_and_a_null_ceiling_is_a_noop()
        {
            Assert.AreEqual(600u, DungeonCombatNormalizer.ClampToDefenseCeiling(987, 600));
            Assert.AreEqual(600u, DungeonCombatNormalizer.ClampToDefenseCeiling(600, 600));
            Assert.AreEqual(300u, DungeonCombatNormalizer.ClampToDefenseCeiling(300, 600));
            Assert.AreEqual(987u, DungeonCombatNormalizer.ClampToDefenseCeiling(987, null));
        }

        // ---- config-dial sanitizing ----------------------------------------------------------------------

        [TestMethod]
        public void Offset_dial_treats_negative_as_disabled_not_as_the_default()
        {
            var fallback = DungeonPopulationLimits.DefaultDefenseSkillCapOffset;
            var ceiling = DungeonPopulationLimits.MaxDefenseSkillCapOffset;

            Assert.AreEqual(-5.0, ThreadDungeonSpawner.SanitizeDefenseSkillCapOffset(-5.0, fallback, ceiling), 1e-9,
                "negative passes through unchanged - it is this axis's own disable, not garbage");
            Assert.AreEqual(0.0, ThreadDungeonSpawner.SanitizeDefenseSkillCapOffset(0.0, fallback, ceiling), 1e-9, "0 is a valid explicit cap");
            Assert.AreEqual(fallback, ThreadDungeonSpawner.SanitizeDefenseSkillCapOffset(double.NaN, fallback, ceiling), 1e-9);
            Assert.AreEqual(fallback, ThreadDungeonSpawner.SanitizeDefenseSkillCapOffset(double.PositiveInfinity, fallback, ceiling), 1e-9);
            Assert.AreEqual(ceiling, ThreadDungeonSpawner.SanitizeDefenseSkillCapOffset(ceiling + 1000.0, fallback, ceiling), 1e-9);
            Assert.AreEqual(250.0, ThreadDungeonSpawner.SanitizeDefenseSkillCapOffset(250.0, fallback, ceiling), 1e-9, "in range, passed through");
        }
    }
}
