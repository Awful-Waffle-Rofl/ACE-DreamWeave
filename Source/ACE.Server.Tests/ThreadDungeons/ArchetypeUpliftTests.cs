using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// ArchetypeUplift (design steps 8-9): the pure per-axis rule (raise, never lower, the own x k term, missing data),
    /// the rating, spell-output and health cores, the defense cap and ceiling decisions, the attribute ratio split, and
    /// the creature glue on a real Creature built from a hand-made weenie (missing defense row ADDED, an attack school
    /// never added, body parts cloned before scaling). No database, no dat: TestGameTables supplies the skill and vital
    /// formulas, and no PropertyManager key is read by anything driven here.
    /// </summary>
    [TestClass]
    public class ArchetypeUpliftTests
    {
        private static uint nextGuid = 0x7E300000;  // static guid range, clear of GuidManager and the simulator tests
        private static uint nextWcid = 997000;

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
            TestGameTables.EnsureInitialized();
        }

        // ---- the rule -------------------------------------------------------------------------------------

        [TestMethod]
        public void AxisTarget_raises_a_value_below_the_run_standard_to_rho_times_it()
        {
            // own 100 vs S(L) 400, S(l) 200: max(100, 100 x 400/200 = 200, 400) = 400.
            Assert.AreEqual(400.0, ArchetypeUplift.AxisTarget(100, 400, 200, 1.0), 1e-9);

            // rho scales the standard term: 0.5 x 400 = 200 = the k term, so 200.
            Assert.AreEqual(200.0, ArchetypeUplift.AxisTarget(100, 400, 200, 0.5), 1e-9);
            Assert.AreEqual(600.0, ArchetypeUplift.AxisTarget(100, 400, 200, 1.5), 1e-9);
        }

        [TestMethod]
        public void AxisTarget_keeps_a_creature_that_was_above_its_own_standard_that_far_above_the_run_standard()
        {
            // own 300 at S(l) 200 is 1.5x its own level; at S(L) 400 that is 600, above rho x S(L).
            Assert.AreEqual(600.0, ArchetypeUplift.AxisTarget(300, 400, 200, 1.0), 1e-9);
        }

        [TestMethod]
        public void AxisTarget_never_lowers()
        {
            // own 900 is above both terms (900 x 100/200 = 450, 100): stays 900.
            Assert.AreEqual(900.0, ArchetypeUplift.AxisTarget(900, 100, 200, 1.0), 1e-9);

            // A run standard BELOW the own-level one (k < 1) cannot pull a value down either.
            Assert.AreEqual(500.0, ArchetypeUplift.AxisTarget(500, 300, 600, 1.0), 1e-9);
        }

        [TestMethod]
        public void AxisTarget_drops_terms_with_no_data_and_a_zero_rho_leaves_the_axis_authored()
        {
            Assert.AreEqual(100.0, ArchetypeUplift.AxisTarget(100, 0, 200, 1.0), 1e-9, "S(L) of 0: no standard, own stands");
            Assert.AreEqual(400.0, ArchetypeUplift.AxisTarget(100, 400, 0, 1.0), 1e-9, "S(l) of 0: the k term drops, rho x S(L) stays");
            Assert.AreEqual(100.0, ArchetypeUplift.AxisTarget(100, 400, 200, 0.0), 1e-9, "rho 0 disables the axis");
            Assert.AreEqual(100.0, ArchetypeUplift.AxisTarget(100, 400, 200, double.NaN), 1e-9);
            Assert.AreEqual(400.0, ArchetypeUplift.AxisTarget(-5, 400, 200, 1.0), 1e-9, "a negative own reads 0 and is raised to the standard");
            Assert.AreEqual(400.0, ArchetypeUplift.AxisTarget(0, 400, 200, 1.0), 1e-9, "a zero own is raised by the standard term, never by a ratio");
        }

        [TestMethod]
        public void Multiplier_is_target_over_own_upward_and_one_otherwise()
        {
            Assert.AreEqual(4.0, ArchetypeUplift.Multiplier(10, 40), 1e-9);
            Assert.AreEqual(1.0, ArchetypeUplift.Multiplier(10, 5), 1e-9);
            Assert.AreEqual(1.0, ArchetypeUplift.Multiplier(0, 40), 1e-9, "a zero is never lifted by a ratio");
        }

        // ---- ratings, spell output, health ---------------------------------------------------------------

        [TestMethod]
        public void RatingTarget_floors_to_the_mean_standard_and_never_lowers()
        {
            Assert.AreEqual(12, ArchetypeUplift.RatingTarget(0, 12.4, 6, 1.0), "a creature with no rating is floored to the standard, rounded");
            Assert.AreEqual(13, ArchetypeUplift.RatingTarget(0, 12.5, 6, 1.0), "half away from zero");
            Assert.AreEqual(20, ArchetypeUplift.RatingTarget(10, 12, 6, 1.0), "the k term: 10 x 12/6");
            Assert.AreEqual(100, ArchetypeUplift.RatingTarget(50, 12, 6, 1.0), "above its own level's standard: carried that far above, 50 x 12/6");
            Assert.AreEqual(50, ArchetypeUplift.RatingTarget(50, 6, 12, 1.0), "a run standard below the own-level one never lowers");
            Assert.AreEqual(-5, ArchetypeUplift.RatingTarget(-5, 0, 0, 1.0), "no standard: an authored negative stays as authored");
            Assert.AreEqual(12, ArchetypeUplift.RatingTarget(-5, 12, 6, 1.0), "a negative own is floored from 0");
        }

        [TestMethod]
        public void SpellDamageRating_is_the_smallest_rating_that_lifts_spell_output_to_its_target()
        {
            // mean 100, DR 0: output 100 vs run 150 (own std 100): target max(100, 150, 150) = 150 -> DR 50.
            Assert.AreEqual(50, ArchetypeUplift.SpellDamageRating(100, 0, 150, 100, 1.0));

            // Already carrying DR 20 (output 120): target max(120, 180, 150) = 180 -> DR 80.
            Assert.AreEqual(80, ArchetypeUplift.SpellDamageRating(100, 20, 150, 100, 1.0));

            // Output already above the target: the current rating stands.
            Assert.AreEqual(90, ArchetypeUplift.SpellDamageRating(100, 90, 150, 200, 1.0));

            // No war/void projectile: nothing to lift.
            Assert.AreEqual(7, ArchetypeUplift.SpellDamageRating(0, 7, 150, 100, 1.0));

            // A fractional need rounds UP so the output reaches the target.
            Assert.AreEqual(34, ArchetypeUplift.SpellDamageRating(150, 0, 200.1, 0, 1.0), "200.1 / 150 = 1.334 -> 34");
        }

        [TestMethod]
        public void HealthBase_floors_at_the_full_archetype_median_and_is_capped()
        {
            // Rule 5: the floor is rho_def x H(L) itself (no trash_health_floor_ratio). authored 1000, H(L) 8000, H(l) 2000.
            Assert.AreEqual(8000.0, ArchetypeUplift.HealthBase(1000, 8000, 2000, 1.0, 0.0), 1e-9, "k term 4000 < the median floor 8000");

            // A creature tanky for its level keeps that shape, but only up to the cap: 3000 x 4 = 12000, capped at 1.25 x 8000.
            Assert.AreEqual(12000.0, ArchetypeUplift.HealthBase(3000, 8000, 2000, 1.0, 0.0), 1e-9, "no cap");
            Assert.AreEqual(10000.0, ArchetypeUplift.HealthBase(3000, 8000, 2000, 1.0, 1.25), 1e-9);

            // A frail one lands on the median.
            Assert.AreEqual(8000.0, ArchetypeUplift.HealthBase(100, 8000, 2000, 1.0, 1.25), 1e-9);

            // rho_def scales the floor and the cap together.
            Assert.AreEqual(16000.0, ArchetypeUplift.HealthBase(100, 8000, 2000, 2.0, 1.25), 1e-9);

            // Upward only beats the cap: an authored value already above it stays.
            Assert.AreEqual(50000.0, ArchetypeUplift.HealthBase(50000, 8000, 20000, 1.0, 1.25), 1e-9);

            // No data at either end: authored.
            Assert.AreEqual(1000.0, ArchetypeUplift.HealthBase(1000, 0, 0, 1.0, 1.25), 1e-9);
        }

        [TestMethod]
        public void HealthTarget_applies_the_gem_multiplier_after_the_floor_and_the_cap()
        {
            var info = Info();
            info.Run = info.Run.With(health: 8000);
            info.Own = info.Own.With(health: 2000);
            info.CapRatio = 1.25;
            info.DefenseCapRatio = 1.25;  // health rides the DEFENSE cap; 1.25 here so the k term (12000) is visibly capped

            Assert.AreEqual(12000, ArchetypeUplift.HealthTarget(1000, info, 1.5), "median 8000, then x1.5");
            Assert.AreEqual(8000, ArchetypeUplift.HealthTarget(1000, info, double.NaN), "a garbled multiplier reads 1");
            Assert.AreEqual(15000, ArchetypeUplift.HealthTarget(3000, info, 1.5), "the cap (10000) is applied BEFORE the gem multiplier");

            // At the shipped defense cap (1.0) the base is the median itself, then the gem multiplier.
            info.DefenseCapRatio = DungeonPopulationLimits.DefaultUpliftDefenseCap;
            Assert.AreEqual(12000, ArchetypeUplift.HealthTarget(3000, info, 1.5), "median 8000 (not the k term 12000), then x1.5");
        }

        // ---- the band cap (rule 1) ----------------------------------------------------------------------------

        [TestMethod]
        public void The_cap_holds_every_target_inside_median_to_cap_times_median_but_never_lowers()
        {
            Assert.AreEqual(500.0, ArchetypeUplift.AxisTarget(300, 400, 200, 1.0, 1.25), 1e-9, "the k term (600) is capped at 1.25 x 400");
            Assert.AreEqual(900.0, ArchetypeUplift.AxisTarget(900, 400, 200, 1.0, 1.25), 1e-9, "already above the cap: never lowered");
            Assert.AreEqual(400.0, ArchetypeUplift.AxisTarget(100, 400, 200, 1.0, 1.25), 1e-9, "below the median: the median");
            Assert.AreEqual(250.0, ArchetypeUplift.AxisTarget(200, 400, 100, 0.5, 1.25), 1e-9, "the cap rides rho: the k term 400 is held at 1.25 x 0.5 x 400");
            Assert.AreEqual(400.0, ArchetypeUplift.AxisTarget(300, 400, 200, 1.0, 1.0), 1e-9, "cap 1 lands everyone below it on the median");
            Assert.AreEqual(600.0, ArchetypeUplift.AxisTarget(300, 400, 200, 1.0, 0.0), 1e-9, "cap 0 is no cap");
            Assert.AreEqual(600.0, ArchetypeUplift.AxisTarget(300, 400, 200, 1.0, double.NaN), 1e-9);
        }

        [TestMethod]
        public void The_cap_reaches_skills_ratings_and_spell_output()
        {
            var info = Info();
            info.CapRatio = 1.25;
            info.DefenseCapRatio = 1.25;  // the defense skill below rides the DEFENSE cap

            Assert.AreEqual(500u, ArchetypeUplift.SkillTarget(info, Skill.UnarmedCombat, 300, 0), "attack: 300 x 2 capped at 1.25 x 400");
            Assert.AreEqual(750u, ArchetypeUplift.SkillTarget(info, Skill.MeleeDefense, 500, 0), "defense: 500 x 2 capped at 1.25 x 600");
            Assert.AreEqual(15, ArchetypeUplift.RatingTarget(10, 12, 6, 1.0, 1.25), "rating: 10 x 2 capped at 1.25 x 12");

            // Spell output: mean 100, DR 20 (output 120); k term 180 is capped at 1.25 x 150 = 187.5 (not binding), and at
            // cap 1 the target is 150 -> DR 50.
            Assert.AreEqual(80, ArchetypeUplift.SpellDamageRating(100, 20, 150, 100, 1.0, 1.25));
            Assert.AreEqual(50, ArchetypeUplift.SpellDamageRating(100, 20, 150, 100, 1.0, 1.0));

            var targets = ArchetypeUplift.AttributeTargets(info, a => a == PropertyAttribute.Strength ? 150u : (uint?)null);
            Assert.AreEqual(250u, targets[PropertyAttribute.Strength], "attribute: 150 x 2 = 300 capped at 1.25 x 200");
        }

        // ---- the DamageRating offset (rule 3) -----------------------------------------------------------------

        [TestMethod]
        public void The_rating_mod_is_the_engines_and_the_offset_undoes_exactly_the_added_rating()
        {
            Assert.AreEqual(1.0, ArchetypeUplift.RatingModOf(0), 1e-12);
            Assert.AreEqual(1.5, ArchetypeUplift.RatingModOf(50), 1e-12);
            Assert.AreEqual(100.0 / 150.0, ArchetypeUplift.RatingModOf(-50), 1e-12, "Creature.GetNegativeRatingMod below zero");
            Assert.AreEqual(Creature.GetPositiveRatingMod(37), ArchetypeUplift.RatingModOf(37), 1e-6);
            Assert.AreEqual(Creature.GetPositiveRatingMod(-20), ArchetypeUplift.RatingModOf(-20), 1e-6);

            // Total 150 with 100 of it added for spells: physical damage must lose exactly mod(50) / mod(150).
            Assert.AreEqual(1.5 / 2.5, ArchetypeUplift.DamageRatingOffset(150, 100), 1e-12);
            Assert.AreEqual(1.0, ArchetypeUplift.DamageRatingOffset(150, 0), 1e-12);
            Assert.AreEqual(1.0, ArchetypeUplift.DamageRatingOffset(150, -5), 1e-12);

            // The engine's composition: damage x mod(total); after the offset, damage x offset x mod(total) = damage x mod(total - added).
            Assert.AreEqual(ArchetypeUplift.RatingModOf(50), ArchetypeUplift.DamageRatingOffset(150, 100) * ArchetypeUplift.RatingModOf(150), 1e-12);

            Assert.AreEqual(2.4, ArchetypeUplift.PhysicalMultiplier(4.0, 0.6), 1e-12);
            Assert.AreEqual(1.0, ArchetypeUplift.PhysicalMultiplier(1.2, 0.5), 1e-12, "never below the authored damage");
        }

        // ---- skills -----------------------------------------------------------------------------------------

        /// <summary>
        /// A run-level and an own-level source standard, plus distinct cross-archetype standards so a test can tell which
        /// one magic defense read: archetype melee/missile defense 600 at L, 300 at l; cross-archetype magic defense 500 at
        /// L, 250 at l; archetype magic defense deliberately 9999 so reading it would be visible.
        /// </summary>
        private static ArchetypeUpliftInfo Info(double offense = 1.0, double defense = 1.0)
        {
            DungeonBandStandard Std(uint scale, uint magic) => DungeonBandStandard.ForTest(
                new Dictionary<Skill, uint> { [Skill.Run] = 100 * scale, [Skill.Deception] = 50 * scale },
                maxBodyDamage: 20 * scale, maxBaseArmor: 100 * scale, spellTier: 6,
                attributeMedians: new Dictionary<PropertyAttribute, uint>
                {
                    [PropertyAttribute.Strength] = 100 * scale, [PropertyAttribute.Endurance] = 100 * scale,
                    [PropertyAttribute.Coordination] = 100 * scale, [PropertyAttribute.Quickness] = 100 * scale,
                },
                effectiveDefenseMedians: new Dictionary<Skill, uint>
                {
                    [Skill.MeleeDefense] = 300 * scale, [Skill.MissileDefense] = 300 * scale, [Skill.MagicDefense] = magic,
                },
                effectiveSkillMedians: new Dictionary<Skill, uint> { [Skill.UnarmedCombat] = 200 * scale });

            return new ArchetypeUpliftInfo
            {
                Wcid = 1,
                Archetype = CreatureArchetype.MeleeTank,
                PrimaryArchetype = CreatureArchetype.MeleeTank,
                Source = CreatureArchetype.MeleeTank,
                RunLevel = 400,
                OwnLevel = 200,
                Run = new ArchetypeAxes(Std(2, 9999), 40, 0, 12, 10, 4, 6, 0, 8, 1.0),
                Own = new ArchetypeAxes(Std(1, 9999), 20, 0, 6, 5, 2, 3, 0, 8, 1.0),
                AllRun = DungeonBandStandard.ForTest(null, 0, 0, effectiveDefenseMedians: new Dictionary<Skill, uint> { [Skill.MagicDefense] = 500 }),
                AllOwn = DungeonBandStandard.ForTest(null, 0, 0, effectiveDefenseMedians: new Dictionary<Skill, uint> { [Skill.MagicDefense] = 250 }),
                OwnEffectiveDamage = 10,
                OffenseRatio = offense,
                DefenseRatio = defense,
            };
        }

        /// <summary><see cref="Info"/> with the two ends swapped: the run-level standard is BELOW the own-level one (k = 0.5).</summary>
        private static ArchetypeUpliftInfo Downhill()
        {
            var info = Info();
            (info.Run, info.Own) = (info.Own, info.Run);
            (info.AllRun, info.AllOwn) = (info.AllOwn, info.AllRun);
            return info;
        }

        [TestMethod]
        public void Melee_and_missile_defense_read_the_source_standard_and_magic_defense_the_cross_archetype_one()
        {
            var info = Info();

            Assert.AreEqual((600.0, 300.0), ArchetypeUplift.SkillStandards(info, Skill.MeleeDefense));
            Assert.AreEqual((600.0, 300.0), ArchetypeUplift.SkillStandards(info, Skill.MissileDefense));
            Assert.AreEqual((500.0, 250.0), ArchetypeUplift.SkillStandards(info, Skill.MagicDefense), "no archetype factor on magic defense");
            Assert.AreEqual((400.0, 200.0), ArchetypeUplift.SkillStandards(info, Skill.UnarmedCombat));
        }

        [TestMethod]
        public void SkillTarget_raises_an_attack_skill_on_the_offense_ratio_and_never_lowers_it()
        {
            Assert.AreEqual(400u, ArchetypeUplift.SkillTarget(Info(), Skill.UnarmedCombat, 150, 0));
            Assert.AreEqual(600u, ArchetypeUplift.SkillTarget(Info(), Skill.UnarmedCombat, 300, 0), "300 x 400/200");
            Assert.AreEqual(2000u, ArchetypeUplift.SkillTarget(Info(), Skill.UnarmedCombat, 1000, 0), "far above its own standard: carried, 1000 x 400/200");
            Assert.AreEqual(1000u, ArchetypeUplift.SkillTarget(Downhill(), Skill.UnarmedCombat, 1000, 0), "k below 1 never lowers");
            Assert.AreEqual(200u, ArchetypeUplift.SkillTarget(Info(offense: 0.5), Skill.UnarmedCombat, 150, 0));
            Assert.AreEqual(400u, ArchetypeUplift.SkillTarget(Info(defense: 0.1), Skill.UnarmedCombat, 150, 0), "the defense ratio does not touch attack");
        }

        [TestMethod]
        public void SkillTarget_caps_a_defense_skill_but_never_below_its_own_value()
        {
            var info = Info();

            Assert.AreEqual(600u, ArchetypeUplift.SkillTarget(info, Skill.MeleeDefense, 100, 0), "no cap: the standard");
            Assert.AreEqual(550u, ArchetypeUplift.SkillTarget(info, Skill.MeleeDefense, 100, 550), "capped");
            Assert.AreEqual(700u, ArchetypeUplift.SkillTarget(info, Skill.MeleeDefense, 700, 550), "an authored value above the cap is not cut by the uplift (its k term, 1400, is)");
            Assert.AreEqual(1200u, ArchetypeUplift.SkillTarget(Info(defense: 2.0), Skill.MeleeDefense, 100, 0), "defense ratio");
        }

        [TestMethod]
        public void DefenseCapFor_reads_the_matching_standard_and_is_off_with_the_trait_strip()
        {
            var info = Info();

            Assert.AreEqual(650u, ArchetypeUplift.DefenseCapFor(info, Skill.MeleeDefense, 50, true), "source median 600 + offset 50");
            Assert.AreEqual(550u, ArchetypeUplift.DefenseCapFor(info, Skill.MagicDefense, 50, true), "cross-archetype magic median 500 + 50");
            Assert.AreEqual(0u, ArchetypeUplift.DefenseCapFor(info, Skill.MeleeDefense, 50, false));
        }

        [TestMethod]
        public void CeilingFor_writes_a_ceiling_only_when_the_value_overshoots_or_an_older_one_sits_under_it()
        {
            Assert.IsNull(ArchetypeUplift.CeilingFor(0, 900, null, true), "no cap: never");
            Assert.AreEqual(500u, ArchetypeUplift.CeilingFor(500, 700, null, false), "an overshoot is clamped");
            Assert.IsNull(ArchetypeUplift.CeilingFor(500, 700, 500, false), "already at the cap");
            Assert.IsNull(ArchetypeUplift.CeilingFor(500, 450, null, true), "raised within the cap, no older ceiling");
            Assert.AreEqual(500u, ArchetypeUplift.CeilingFor(500, 450, 300, true), "the trait strip's ceiling would hide the raise");
            Assert.IsNull(ArchetypeUplift.CeilingFor(500, 450, 300, false), "not raised: the older ceiling is the strip's business");
        }

        [TestMethod]
        public void Attributes_split_Endurance_and_Quickness_onto_defense_and_the_rest_onto_offense()
        {
            var info = Info(offense: 0.5, defense: 2.0);

            Assert.AreEqual(2.0, ArchetypeUplift.AttributeRatio(PropertyAttribute.Endurance, info));
            Assert.AreEqual(2.0, ArchetypeUplift.AttributeRatio(PropertyAttribute.Quickness, info));
            Assert.AreEqual(0.5, ArchetypeUplift.AttributeRatio(PropertyAttribute.Strength, info));
            Assert.AreEqual(0.5, ArchetypeUplift.AttributeRatio(PropertyAttribute.Coordination, info));
            Assert.AreEqual(0.5, ArchetypeUplift.AttributeRatio(PropertyAttribute.Focus, info));
            Assert.AreEqual(0.5, ArchetypeUplift.AttributeRatio(PropertyAttribute.Self, info));

            var base100 = new Dictionary<PropertyAttribute, uint?>
            {
                [PropertyAttribute.Strength] = 100, [PropertyAttribute.Endurance] = 100, [PropertyAttribute.Coordination] = 500,
                [PropertyAttribute.Quickness] = 0, [PropertyAttribute.Focus] = null,
            };

            var targets = ArchetypeUplift.AttributeTargets(info, a => base100.TryGetValue(a, out var v) ? v : null);

            // Str: max(100, 100 x 0.5 x 200/100 = 100, 0.5 x 200 = 100) = 100 -> not raised. End: 2 x 200 = 400 (k term 400 too).
            Assert.IsFalse(targets.ContainsKey(PropertyAttribute.Strength));
            Assert.AreEqual(400u, targets[PropertyAttribute.Endurance]);
            Assert.IsFalse(targets.ContainsKey(PropertyAttribute.Coordination), "500 is above 0.5 x 200 and 500 x 0.5 x 2");
            Assert.IsFalse(targets.ContainsKey(PropertyAttribute.Quickness), "an attribute authored at 0 is not added");
            Assert.IsFalse(targets.ContainsKey(PropertyAttribute.Focus), "a missing attribute is not added");
        }

        [TestMethod]
        public void BestProjectileMean_takes_the_largest_mean_in_the_final_book()
        {
            Assert.AreEqual(300.0, ArchetypeUplift.BestProjectileMean(new[] { 1, 2, 3 }, id => id * 100.0), 1e-9);
            Assert.AreEqual(0.0, ArchetypeUplift.BestProjectileMean(null, id => 1.0), 1e-9);
            Assert.AreEqual(0.0, ArchetypeUplift.BestProjectileMean(new[] { 1 }, null), 1e-9);
        }

        // ---- creature glue -----------------------------------------------------------------------------------

        private static Creature MakeCreature(bool withMissileDefense)
        {
            PropertiesBodyPart Part(int dval, int armor) => new PropertiesBodyPart
            {
                DType = DamageType.Bludgeon, DVal = dval, DVar = 0.5f, BaseArmor = armor,
                MLF = 1, MRF = 1, MLB = 1, MRB = 1,
            };

            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Creature,
                PropertiesInt = new Dictionary<PropertyInt, int> { [PropertyInt.Level] = 200 },
                PropertiesAttribute = new Dictionary<PropertyAttribute, PropertiesAttribute>
                {
                    [PropertyAttribute.Strength] = new PropertiesAttribute { InitLevel = 60 },
                    [PropertyAttribute.Endurance] = new PropertiesAttribute { InitLevel = 60 },
                    [PropertyAttribute.Coordination] = new PropertiesAttribute { InitLevel = 60 },
                    [PropertyAttribute.Quickness] = new PropertiesAttribute { InitLevel = 60 },
                },
                PropertiesAttribute2nd = new Dictionary<PropertyAttribute2nd, PropertiesAttribute2nd>
                {
                    [PropertyAttribute2nd.MaxHealth] = new PropertiesAttribute2nd { InitLevel = 500 },
                },
                PropertiesSkill = new Dictionary<Skill, PropertiesSkill>
                {
                    [Skill.UnarmedCombat] = new PropertiesSkill { InitLevel = 100, SAC = SkillAdvancementClass.Trained },
                    [Skill.MeleeDefense] = new PropertiesSkill { InitLevel = 100, SAC = SkillAdvancementClass.Trained },
                    [Skill.Run] = new PropertiesSkill { InitLevel = 10, SAC = SkillAdvancementClass.Trained },
                },
                PropertiesBodyPart = new Dictionary<CombatBodyPart, PropertiesBodyPart>
                {
                    [CombatBodyPart.Chest] = Part(10, 50),
                    [CombatBodyPart.Abdomen] = Part(5, 100),
                },
            };

            if (withMissileDefense)
                weenie.PropertiesSkill[Skill.MissileDefense] = new PropertiesSkill { InitLevel = 100, SAC = SkillAdvancementClass.Trained };

            return new Creature(weenie, new ObjectGuid(nextGuid++));
        }

        private static uint Effective(Creature creature, Skill skill)
        {
            var s = creature.GetCreatureSkill(skill, false);
            return s == null ? 0u : (s.IsUsable ? AttributeFormula.GetFormula(creature, skill, false) : 0u) + s.InitLevel + s.Ranks;
        }

        [TestMethod]
        public void Apply_adds_a_missing_defense_row_and_lands_it_on_the_standard()
        {
            var creature = MakeCreature(withMissileDefense: false);
            Assert.IsNull(creature.GetCreatureSkill(Skill.MissileDefense, false), "fixture: the weenie authors no missile defense");

            var session = ArchetypeUplift.Begin(creature, Info(), 1.0);
            var result = ArchetypeUplift.Apply(creature, session, 400, 0, applyCap: false);

            var row = creature.GetCreatureSkill(Skill.MissileDefense, false);

            Assert.IsNotNull(row, "the missing defense row is ADDED");
            Assert.IsTrue(row.IsUsable);
            Assert.AreEqual(600u, Effective(creature, Skill.MissileDefense), "the added row's effective value is the source standard");

            // The weenie authors no magic defense either: added at the CROSS-archetype standard (500, not the archetype's 9999).
            Assert.AreEqual(500u, Effective(creature, Skill.MagicDefense));
            Assert.AreEqual(2, result.DefenseRowsAdded);
            Assert.AreEqual(400, creature.Level, "the level is floored at the uplift level");
        }

        [TestMethod]
        public void Apply_caps_an_added_defense_and_writes_no_ceiling_when_the_value_sits_inside_the_cap()
        {
            var creature = MakeCreature(withMissileDefense: false);
            var session = ArchetypeUplift.Begin(creature, Info(defense: 2.0), 1.0);

            // defense 2.0 would target 1200; the cap is source median 600 + offset 50.
            ArchetypeUplift.Apply(creature, session, 400, 50, applyCap: true);

            Assert.AreEqual(650u, Effective(creature, Skill.MissileDefense));
            Assert.IsNull(creature.GetDefenseSkillCeiling(Skill.MissileDefense), "the written value is at the cap, so nothing overshoots");
        }

        [TestMethod]
        public void Apply_raises_a_carried_defense_skill_and_does_not_lower_one_above_the_standard()
        {
            var creature = MakeCreature(withMissileDefense: true);
            creature.GetCreatureSkill(Skill.MissileDefense, false).InitLevel = 5000;
            var high = Effective(creature, Skill.MissileDefense);

            var uncapped = Info();
            uncapped.DefenseCapRatio = 0.0;  // no defense cap: the k term stands
            var session = ArchetypeUplift.Begin(creature, uncapped, 1.0);
            ArchetypeUplift.Apply(creature, session, 400, 0, applyCap: false);

            Assert.AreEqual(600u, Effective(creature, Skill.MeleeDefense), "melee defense (authored 140) raised to the standard");
            Assert.AreEqual(high * 2, Effective(creature, Skill.MissileDefense), "defense cap off: far above its own standard, carried that far above the run's (k = 2)");

            // At the shipped defense cap (1.0) the k term cannot lift it; authored (above the median 600) is kept.
            var shipped = MakeCreature(withMissileDefense: true);
            shipped.GetCreatureSkill(Skill.MissileDefense, false).InitLevel = 5000;
            ArchetypeUplift.Apply(shipped, ArchetypeUplift.Begin(shipped, Info(), 1.0), 400, 0, applyCap: false);
            Assert.AreEqual(high, Effective(shipped, Skill.MissileDefense), "defense cap 1: authored kept, not carried");

            // k below 1: nothing is lowered.
            var downhill = MakeCreature(withMissileDefense: true);
            downhill.GetCreatureSkill(Skill.MissileDefense, false).InitLevel = 5000;
            var before = Effective(downhill, Skill.MissileDefense);

            ArchetypeUplift.Apply(downhill, ArchetypeUplift.Begin(downhill, Downhill(), 1.0), 400, 0, applyCap: false);

            Assert.AreEqual(before, Effective(downhill, Skill.MissileDefense), "a defense above the standard is never lowered");
            Assert.AreEqual(300u, Effective(downhill, Skill.MeleeDefense), "and one below it still rises to rho x S(L)");
        }

        [TestMethod]
        public void Apply_retargets_a_carried_attack_skill_and_never_adds_an_attack_school()
        {
            var creature = MakeCreature(withMissileDefense: true);
            var session = ArchetypeUplift.Begin(creature, Info(), 1.0);

            ArchetypeUplift.Apply(creature, session, 400, 0, applyCap: false);

            Assert.AreEqual(400u, Effective(creature, Skill.UnarmedCombat));
            Assert.IsNull(creature.GetCreatureSkill(Skill.Bow, false), "attack skills are carried only");
            Assert.IsNull(creature.GetCreatureSkill(Skill.WarMagic, false));
            Assert.AreEqual(200u, creature.GetCreatureSkill(Skill.Run, false).InitLevel, "another carried skill is floored to the source's InitLevel median");
            Assert.IsNull(creature.GetCreatureSkill(Skill.Deception, false), "a skill the creature does not carry is not added");
        }

        [TestMethod]
        public void Apply_scales_damage_and_armour_on_a_clone_of_the_shared_body_parts()
        {
            var creature = MakeCreature(withMissileDefense: true);
            var shared = creature.Biota.PropertiesBodyPart;
            var weenieChest = shared[CombatBodyPart.Chest];

            var session = ArchetypeUplift.Begin(creature, Info(), 1.0);
            var result = ArchetypeUplift.Apply(creature, session, 400, 0, applyCap: false);

            // Damage: own effective 10 vs run 40 (own std 20) -> x4. Armour: own best 100 vs run 200 (own std 100) -> x2.
            Assert.AreEqual(4.0, result.DamageMultiplier, 1e-9);
            Assert.AreEqual(2.0, result.ArmorMultiplier, 1e-9);
            Assert.AreEqual(40, creature.Biota.PropertiesBodyPart[CombatBodyPart.Chest].DVal);
            Assert.AreEqual(20, creature.Biota.PropertiesBodyPart[CombatBodyPart.Abdomen].DVal, "every part rides the ONE multiplier");
            Assert.AreEqual(200, creature.Biota.PropertiesBodyPart[CombatBodyPart.Abdomen].BaseArmor);

            Assert.AreEqual(10, weenieChest.DVal, "the shared (cached weenie) part is never written");
            Assert.AreEqual(50, weenieChest.BaseArmor);
        }

        // ---- round 3: the cap on armour and physical damage, the attack-skill ceiling ---------------------------

        /// <summary><see cref="Info"/> with a LOW own-level standard on damage (5) and armour (50), so the k terms overshoot the cap.</summary>
        private static ArchetypeUpliftInfo SteepInfo(double cap)
        {
            var info = Info();
            info.Own = new ArchetypeAxes(DungeonBandStandard.ForTest(info.Own.Core.SkillMedians.ToDictionary(k => k.Key, k => k.Value), 5, 50, spellTier: 6,
                    attributeMedians: info.Own.Core.AttributeMedians.ToDictionary(k => k.Key, k => k.Value),
                    effectiveDefenseMedians: new Dictionary<Skill, uint> { [Skill.MeleeDefense] = 300, [Skill.MissileDefense] = 300 },
                    effectiveSkillMedians: new Dictionary<Skill, uint> { [Skill.UnarmedCombat] = 200 }),
                5, 0, 6, 5, 2, 3, 0, 8, 1.0);
            info.CapRatio = cap;
            info.DefenseCapRatio = cap;  // armour rides the DEFENSE cap; both set so one argument steers the fixture
            return info;
        }

        [TestMethod]
        public void Apply_caps_armour_and_physical_damage_at_cap_times_the_run_standard()
        {
            // Damage: own 10, k = 40 / 5 = 8 -> 80, capped at 1.25 x 40 = 50 -> x5. Armour: own best 100, k = 200 / 50 = 4 -> 400,
            // capped at 1.25 x 200 = 250 -> x2.5. Both reach every body part through the one multiplier each.
            var creature = MakeCreature(withMissileDefense: true);
            var result = ArchetypeUplift.Apply(creature, ArchetypeUplift.Begin(creature, SteepInfo(1.25), 1.0), 400, 0, applyCap: false);

            Assert.AreEqual(5.0, result.DamageMultiplier, 1e-9, "the damage k term (x8) is held at the cap");
            Assert.AreEqual(5.0, result.PhysicalMultiplier, 1e-9, "no DamageRating was added for spells: physical = axis");
            Assert.AreEqual(50, creature.Biota.PropertiesBodyPart[CombatBodyPart.Chest].DVal);
            Assert.AreEqual(25, creature.Biota.PropertiesBodyPart[CombatBodyPart.Abdomen].DVal);
            Assert.AreEqual(2.5, result.ArmorMultiplier, 1e-9, "the armour k term (x4) is held at the cap");
            Assert.AreEqual(250, creature.Biota.PropertiesBodyPart[CombatBodyPart.Abdomen].BaseArmor);
            Assert.AreEqual(125, creature.Biota.PropertiesBodyPart[CombatBodyPart.Chest].BaseArmor);

            // Cap off: the k terms stand.
            var uncapped = MakeCreature(withMissileDefense: true);
            var free = ArchetypeUplift.Apply(uncapped, ArchetypeUplift.Begin(uncapped, SteepInfo(0.0), 1.0), 400, 0, applyCap: false);
            Assert.AreEqual(8.0, free.DamageMultiplier, 1e-9);
            Assert.AreEqual(4.0, free.ArmorMultiplier, 1e-9);
        }

        [TestMethod]
        public void Apply_never_lowers_armour_or_physical_damage_authored_above_the_cap()
        {
            var creature = MakeCreature(withMissileDefense: true);
            creature.Biota.PropertiesBodyPart = creature.Biota.PropertiesBodyPart.ToDictionary(k => k.Key, k => new PropertiesBodyPart
            {
                DType = k.Value.DType, DVal = k.Key == CombatBodyPart.Chest ? 60 : k.Value.DVal, DVar = k.Value.DVar, BaseArmor = 900, MLF = 1, MRF = 1, MLB = 1, MRB = 1,
            });

            var info = SteepInfo(1.25);  // live own damage is the chest's 60, above the cap 50

            var result = ArchetypeUplift.Apply(creature, ArchetypeUplift.Begin(creature, info, 1.0), 400, 0, applyCap: false);

            Assert.AreEqual(1.0, result.DamageMultiplier, 1e-12);
            Assert.AreEqual(1.0, result.ArmorMultiplier, 1e-12);
            Assert.AreEqual(60, creature.Biota.PropertiesBodyPart[CombatBodyPart.Chest].DVal, "authored is the floor");
            Assert.AreEqual(5, creature.Biota.PropertiesBodyPart[CombatBodyPart.Abdomen].DVal, "the body rides ONE multiplier off its best part: x1");
            Assert.AreEqual(900, creature.Biota.PropertiesBodyPart[CombatBodyPart.Chest].BaseArmor);
        }

        // ---- code review F3: own damage from the LIVE instance, each source onto the one target ----------------

        private static WorldObject AddItem<T>(Creature creature, WeenieType type, int damage, EquipMask? slot, double? damageMod = null) where T : WorldObject
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = type,
                PropertiesInt = new Dictionary<PropertyInt, int> { [PropertyInt.Damage] = damage },
                PropertiesFloat = damageMod.HasValue ? new Dictionary<PropertyFloat, double> { [PropertyFloat.DamageMod] = damageMod.Value } : new Dictionary<PropertyFloat, double>(),
            };

            var item = (T)Activator.CreateInstance(typeof(T), weenie, new ObjectGuid(nextGuid++));

            if (slot.HasValue)
            {
                item.CurrentWieldedLocation = slot;
                item.Wielder = creature;
                creature.EquippedObjects[item.Guid] = item;
            }
            else
                creature.Inventory[item.Guid] = item;

            return item;
        }

        /// <summary>A steep damage standard so the rule's target is the cap: run 2700 (cap 1.25 -> 3375), own-level 20.</summary>
        private static ArchetypeUpliftInfo DamageInfo()
        {
            var info = Info();
            info.Run = new ArchetypeAxes(info.Run.Core, 2700, 0, 12, 10, 4, 6, 0, 8, 1.0);
            info.Own = new ArchetypeAxes(info.Own.Core, 20, 0, 6, 5, 2, 3, 0, 8, 1.0);
            info.CapRatio = 1.25;
            info.OwnEffectiveDamage = 25;  // the weenie facts: body only, blind to the wielded weapon
            return info;
        }

        [TestMethod]
        public void Apply_reads_own_damage_off_the_live_instance_and_lands_every_source_on_the_target()
        {
            var creature = MakeCreature(withMissileDefense: true);
            creature.Biota.PropertiesBodyPart = creature.Biota.PropertiesBodyPart.ToDictionary(k => k.Key, k => new PropertiesBodyPart
            {
                DType = k.Value.DType, DVal = k.Key == CombatBodyPart.Chest ? 25 : 10, DVar = k.Value.DVar, BaseArmor = k.Value.BaseArmor, MLF = 1, MRF = 1, MLB = 1, MRB = 1,
            });
            var sword = AddItem<MeleeWeapon>(creature, WeenieType.MeleeWeapon, 60, EquipMask.MeleeWeapon);

            Assert.AreEqual(60.0, ArchetypeUplift.LiveEffectiveDamageOf(creature), 1e-9, "the live read before Apply is the own value Apply uses");

            var session = ArchetypeUplift.Begin(creature, DamageInfo(), 1.0);

            // A DamageRating top-up for spells of 50 on a final total of 70: physical damage must lose mod(20) / mod(70).
            creature.DamageRating = 70;
            session.DamageRatingAddedForSpells = 50;
            var offset = ArchetypeUplift.RatingModOf(20) / ArchetypeUplift.RatingModOf(70);

            var result = ArchetypeUplift.Apply(creature, session, 400, 0, applyCap: false);

            Assert.AreEqual(60.0, result.OwnLiveDamage, 1e-9, "the wielded weapon (60) beats the best body part (25), and the facts' 25 is not used");
            Assert.AreEqual(3375.0, result.DamageTarget, 1e-9, "k = 60 x 2700 / 20 = 8100, held at the cap 1.25 x 2700");
            Assert.AreEqual(offset, result.DamageRatingOffset, 1e-12);

            var expected = 3375.0 * offset;
            var weapon = sword.GetProperty(PropertyInt.Damage) ?? 0;
            var chest = creature.Biota.PropertiesBodyPart[CombatBodyPart.Chest].DVal;
            var maxMelee = Math.Max(weapon, chest);

            Assert.AreEqual(expected, maxMelee, 1.0, "the max melee damage lands on the target after the DR offset, within rounding");
            Assert.AreEqual(expected, weapon, 1.0, "the weapon by its own ratio (3375 / 60)");
            Assert.AreEqual(expected, chest, 1.0, "the best body part by its own ratio (3375 / 25)");
            Assert.AreEqual(ReachUpStamp.ScaleInt(10, 3375.0 / 25 * offset), creature.Biota.PropertiesBodyPart[CombatBodyPart.Abdomen].DVal, "the other parts ride the body's ratio");
            Assert.AreEqual(1, result.WeaponsScaled);
            Assert.AreEqual(maxMelee, ArchetypeUplift.LiveEffectiveDamageOf(creature), 1e-9, "the live read after Apply is what the creature fights with");
        }

        [TestMethod]
        public void A_pack_weapon_never_sets_own_damage_but_is_scaled_onto_the_same_target()
        {
            // Own-level standard 22 puts the k term between rho x S (2700) and the cap (3375): own 25 -> 25 x 2700 / 22 =
            // 3068.2, while own 60 would cap at 3375 - so the target says which weapon own was read from.
            var creature = MakeCreature(withMissileDefense: true);
            var equipped = AddItem<MeleeWeapon>(creature, WeenieType.MeleeWeapon, 25, EquipMask.MeleeWeapon);
            var pack = AddItem<MeleeWeapon>(creature, WeenieType.MeleeWeapon, 60, null);

            var info = DamageInfo();
            info.Own = new ArchetypeAxes(info.Own.Core, 22, 0, 6, 5, 2, 3, 0, 8, 1.0);

            Assert.AreEqual(25.0, ArchetypeUplift.LiveEffectiveDamageOf(creature), 1e-9, "the pack weapon (60) is not what it fights with");

            var session = ArchetypeUplift.Begin(creature, info, 1.0);
            var result = ArchetypeUplift.Apply(creature, session, 400, 0, applyCap: false);
            var target = 25.0 * 2700 / 22;

            Assert.AreEqual(25.0, result.OwnLiveDamage, 1e-9, "own comes from the equipped 25, never the pack 60");
            Assert.AreEqual(target, result.DamageTarget, 1e-9);
            Assert.AreEqual(target, equipped.GetProperty(PropertyInt.Damage) ?? 0, 1.0, "the equipped weapon lands on the target");
            Assert.AreEqual(target, pack.GetProperty(PropertyInt.Damage) ?? 0, 1.0, "a swap-in pack weapon is scaled onto the same target by its own ratio");
            Assert.AreEqual(2, result.WeaponsScaled);
        }

        [TestMethod]
        public void Apply_scales_ammunition_by_its_launcher_damage_mod_and_leaves_the_launcher_alone()
        {
            var creature = MakeCreature(withMissileDefense: true);
            var bow = AddItem<MissileLauncher>(creature, WeenieType.MissileLauncher, 7, EquipMask.MissileWeapon, damageMod: 2.0);
            var arrows = AddItem<Ammunition>(creature, WeenieType.Ammunition, 50, EquipMask.MissileAmmo);

            var result = ArchetypeUplift.Apply(creature, ArchetypeUplift.Begin(creature, DamageInfo(), 1.0), 400, 0, applyCap: false);

            Assert.AreEqual(100.0, result.OwnLiveDamage, 1e-9, "arrows 50 x DamageMod 2 beats the body's 10");
            Assert.AreEqual(3375.0, result.DamageTarget, 1e-9);
            Assert.AreEqual(3375.0, (arrows.GetProperty(PropertyInt.Damage) ?? 0) * 2.0, 2.0, "the arrows land the launched shot on the target");
            Assert.AreEqual(7, bow.GetProperty(PropertyInt.Damage), "a launcher's own Damage is not a damage source here");

            Assert.AreEqual(1.0, ArchetypeUplift.LauncherDamageMod(null), 1e-12);
            Assert.AreEqual(1.0, ArchetypeUplift.LauncherDamageMod(0.0), 1e-12);
            Assert.AreEqual(1.0, ArchetypeUplift.SourceMultiplier(500, 400, 1.0), 1e-12, "a source above the target is never lowered");
            Assert.AreEqual(1.0, ArchetypeUplift.SourceMultiplier(0, 400, 1.0), 1e-12);
        }

        [TestMethod]
        public void SkillTarget_holds_an_attack_skill_under_the_attack_skill_ceiling_but_never_lowers()
        {
            var info = Info();
            info.AttackSkillCeiling = 450;

            Assert.AreEqual(450u, ArchetypeUplift.SkillTarget(info, Skill.UnarmedCombat, 300, 0), "the k term 600 is held at the ceiling");
            Assert.IsTrue(ArchetypeUplift.AttackCeilingBinds(info, Skill.UnarmedCombat, 300));
            Assert.AreEqual(400u, ArchetypeUplift.SkillTarget(info, Skill.UnarmedCombat, 150, 0), "the standard 400 sits under it");
            Assert.IsFalse(ArchetypeUplift.AttackCeilingBinds(info, Skill.UnarmedCombat, 150));
            Assert.IsFalse(ArchetypeUplift.AttackCeilingLowers(info, Skill.UnarmedCombat, 300), "authored under the ceiling: held, not lowered");

            // Round 4: the ONE axis allowed to go down. Authored above the ceiling: lowered to it.
            Assert.AreEqual(450u, ArchetypeUplift.SkillTarget(info, Skill.UnarmedCombat, 500, 0), "authored 500 above the ceiling 450: LOWERED");
            Assert.IsTrue(ArchetypeUplift.AttackCeilingBinds(info, Skill.UnarmedCombat, 500));
            Assert.IsTrue(ArchetypeUplift.AttackCeilingLowers(info, Skill.UnarmedCombat, 500));

            var downhill = Downhill();
            downhill.AttackSkillCeiling = 450;
            Assert.AreEqual(450u, ArchetypeUplift.SkillTarget(downhill, Skill.UnarmedCombat, 500, 0), "lowered even where the rule alone would leave it authored (k below 1)");
            Assert.IsTrue(ArchetypeUplift.AttackCeilingLowers(downhill, Skill.UnarmedCombat, 500));
            Assert.AreEqual(450u, ArchetypeUplift.SkillTarget(downhill, Skill.UnarmedCombat, 450, 0), "at the ceiling: unchanged");
            Assert.IsFalse(ArchetypeUplift.AttackCeilingLowers(downhill, Skill.UnarmedCombat, 450));
            Assert.AreEqual(700u, ArchetypeUplift.SkillTarget(downhill, Skill.MeleeDefense, 700, 0), "a defense skill is still never lowered");

            Assert.AreEqual(600u, ArchetypeUplift.SkillTarget(info, Skill.MeleeDefense, 100, 0), "defense skills are not attack skills");
            Assert.IsFalse(ArchetypeUplift.AttackCeilingBinds(info, Skill.MeleeDefense, 100));

            info.AttackSkillCeiling = 0;
            Assert.AreEqual(600u, ArchetypeUplift.SkillTarget(info, Skill.UnarmedCombat, 300, 0), "0 = no ceiling");

            // On top of the band cap: cap 1.25 alone holds 600 at 500; the ceiling 450 is lower and wins.
            info.CapRatio = 1.25;
            Assert.AreEqual(500u, ArchetypeUplift.SkillTarget(info, Skill.UnarmedCombat, 300, 0));
            info.AttackSkillCeiling = 450;
            Assert.AreEqual(450u, ArchetypeUplift.SkillTarget(info, Skill.UnarmedCombat, 300, 0));
        }

        [TestMethod]
        public void Apply_writes_the_ceiling_and_reports_where_it_bound()
        {
            var creature = MakeCreature(withMissileDefense: true);
            var info = Info();
            info.AttackSkillCeiling = 300;

            var session = ArchetypeUplift.Begin(creature, info, 1.0);
            var result = ArchetypeUplift.Apply(creature, session, 400, 0, applyCap: false);

            Assert.AreEqual(300u, Effective(creature, Skill.UnarmedCombat), "the standard 400 is held at the ceiling");
            CollectionAssert.AreEqual(new[] { Skill.UnarmedCombat }, result.AttackCeilingBound);

            var line = ArchetypeUplift.LogLine(42, 400, 200, session, result, default, 0, session.HealthTarget);
            StringAssert.Contains(line, "attack-skill ceiling 300 (P75 of natural at L) BOUND on UnarmedCombat");

            Assert.AreEqual(0, result.AttackCeilingLowered.Count, "authored 140, under the ceiling: held, not lowered");

            var free = MakeCreature(withMissileDefense: true);
            var freeSession = ArchetypeUplift.Begin(free, Info(), 1.0);
            var freeResult = ArchetypeUplift.Apply(free, freeSession, 400, 0, applyCap: false);
            Assert.AreEqual(0, freeResult.AttackCeilingBound.Count);
            StringAssert.Contains(ArchetypeUplift.LogLine(42, 400, 200, freeSession, freeResult, default, 0, freeSession.HealthTarget), "attack-skill ceiling 0 not binding");
        }

        [TestMethod]
        public void A_zero_offense_ratio_leaves_attack_skill_authored_even_above_the_ceiling()
        {
            var info = Info(offense: 0.0);
            info.AttackSkillCeiling = 450;

            Assert.IsFalse(ArchetypeUplift.CeilingApplies(info));
            Assert.AreEqual(500u, ArchetypeUplift.SkillTarget(info, Skill.UnarmedCombat, 500, 0), "rho_off 0: authored, not lowered");
            Assert.AreEqual(150u, ArchetypeUplift.SkillTarget(info, Skill.UnarmedCombat, 150, 0), "rho_off 0: authored, not raised");
            Assert.IsFalse(ArchetypeUplift.AttackCeilingBinds(info, Skill.UnarmedCombat, 500));
            Assert.IsFalse(ArchetypeUplift.AttackCeilingLowers(info, Skill.UnarmedCombat, 500));

            info.OffenseRatio = double.NaN;
            Assert.AreEqual(500u, ArchetypeUplift.SkillTarget(info, Skill.UnarmedCombat, 500, 0), "a garbled rho reads as leave-authored too");

            // Through Apply: an attack skill authored far above the ceiling stays exactly as authored.
            var creature = MakeCreature(withMissileDefense: true);
            creature.GetCreatureSkill(Skill.UnarmedCombat, false).InitLevel = 1000;
            var authored = Effective(creature, Skill.UnarmedCombat);
            var zero = Info(offense: 0.0);
            zero.AttackSkillCeiling = 300;

            var result = ArchetypeUplift.Apply(creature, ArchetypeUplift.Begin(creature, zero, 1.0), 400, 0, applyCap: false);

            Assert.AreEqual(authored, Effective(creature, Skill.UnarmedCombat));
            Assert.AreEqual(0, result.AttackCeilingLowered.Count);
            Assert.AreEqual(0, result.AttackCeilingBound.Count);
        }

        [TestMethod]
        public void Apply_lowers_an_authored_attack_skill_above_the_ceiling_and_reports_it()
        {
            // Authored UnarmedCombat InitLevel 1000 (effective 1040): far above the ceiling 300, which it lands on.
            var creature = MakeCreature(withMissileDefense: true);
            creature.GetCreatureSkill(Skill.UnarmedCombat, false).InitLevel = 1000;
            creature.GetCreatureSkill(Skill.MeleeDefense, false).InitLevel = 5000;
            var authored = Effective(creature, Skill.UnarmedCombat);
            var defenseBefore = Effective(creature, Skill.MeleeDefense);

            var info = Info();
            info.AttackSkillCeiling = 300;

            var session = ArchetypeUplift.Begin(creature, info, 1.0);
            var result = ArchetypeUplift.Apply(creature, session, 400, 0, applyCap: false);

            Assert.IsTrue(authored > 300, "fixture");
            Assert.AreEqual(300u, Effective(creature, Skill.UnarmedCombat), "lowered to the ceiling");
            Assert.AreEqual((authored, 300u), result.Skills[Skill.UnarmedCombat]);
            CollectionAssert.AreEqual(new[] { Skill.UnarmedCombat }, result.AttackCeilingLowered);
            CollectionAssert.AreEqual(new[] { Skill.UnarmedCombat }, result.AttackCeilingBound);
            Assert.IsTrue(Effective(creature, Skill.MeleeDefense) >= defenseBefore, "defense is never lowered");

            var line = ArchetypeUplift.LogLine(42, 400, 200, session, result, default, 0, session.HealthTarget);
            StringAssert.Contains(line, "LOWERED authored UnarmedCombat");
            StringAssert.Contains(line, $"UnarmedCombat:{authored}->300");

            // An attribute term alone above the ceiling cannot be cut: InitLevel floors at 0, the term stands.
            var strong = MakeCreature(withMissileDefense: true);
            strong.GetCreatureSkill(Skill.UnarmedCombat, false).InitLevel = 1000;
            var low = Info();
            low.AttackSkillCeiling = 10;
            ArchetypeUplift.Apply(strong, ArchetypeUplift.Begin(strong, low, 1.0), 400, 0, applyCap: false);
            Assert.AreEqual(0u, strong.GetCreatureSkill(Skill.UnarmedCombat, false).InitLevel);
            Assert.AreEqual(AttributeFormula.GetFormula(strong, Skill.UnarmedCombat, false), Effective(strong, Skill.UnarmedCombat));
        }

        [TestMethod]
        public void FloorRatings_floors_each_rating_and_tops_up_damage_rating_for_spell_output()
        {
            var creature = MakeCreature(withMissileDefense: true);
            creature.Biota.PropertiesSpellBook = new Dictionary<int, float> { [77] = 2.1f };

            var info = Info();
            info.Run = new ArchetypeAxes(info.Run.Core, 40, 300, 12, 10, 4, 6, 0, 8, 1.0);
            info.Own = new ArchetypeAxes(info.Own.Core, 20, 100, 6, 5, 2, 3, 0, 8, 1.0);

            // Spell mean 100 vs output 300 at L (100 at l): target max(100, 300, 300) = 300 -> DR 200, above the rating floor 12.
            var session = ArchetypeUplift.Begin(creature, info, 1.0);
            var r = ArchetypeUplift.FloorRatings(creature, session, id => id == 77 ? 100.0 : 0.0);

            Assert.AreEqual(200, creature.DamageRating);
            Assert.AreEqual(200, r.DrSpell);
            Assert.AreEqual(12, r.DrRating, "what the rating axis alone floors to");
            Assert.AreEqual(188, session.DamageRatingAddedForSpells, "the spell top-up above the rating floor");
            Assert.AreEqual(10, creature.DamageResistRating);
            Assert.AreEqual(4, creature.CritRating);
            Assert.AreEqual(6, creature.CritDamageRating);

            // No projectile: DR is just floored.
            var melee = MakeCreature(withMissileDefense: true);
            var meleeSession = ArchetypeUplift.Begin(melee, info, 1.0);
            ArchetypeUplift.FloorRatings(melee, meleeSession, id => 0.0);
            Assert.AreEqual(12, melee.DamageRating);
            Assert.AreEqual(0, meleeSession.DamageRatingAddedForSpells);
        }

        [TestMethod]
        public void Apply_divides_physical_damage_by_the_damage_rating_added_for_spells()
        {
            var creature = MakeCreature(withMissileDefense: true);
            creature.Biota.PropertiesSpellBook = new Dictionary<int, float> { [77] = 2.1f };

            var info = Info();
            info.Run = new ArchetypeAxes(info.Run.Core, 40, 300, 12, 10, 4, 6, 0, 8, 1.0);
            info.Own = new ArchetypeAxes(info.Own.Core, 20, 100, 6, 5, 2, 3, 0, 8, 1.0);

            var session = ArchetypeUplift.Begin(creature, info, 1.0);
            ArchetypeUplift.FloorRatings(creature, session, id => id == 77 ? 100.0 : 0.0);

            // The gem then adds its own +20 on top (the spawner's order), so the total is 220 when Apply runs.
            creature.DamageRating += 20;

            var result = ArchetypeUplift.Apply(creature, session, 400, 0, applyCap: false);

            // Axis x4 (effective damage 10 -> 40); offset mod(220 - 188) / mod(220) = 1.32 / 3.2.
            var offset = 1.32 / 3.2;
            Assert.AreEqual(4.0, result.DamageMultiplier, 1e-9);
            Assert.AreEqual(offset, result.DamageRatingOffset, 1e-9);
            Assert.AreEqual(4.0 * offset, result.PhysicalMultiplier, 1e-9);
            Assert.AreEqual(ReachUpStamp.ScaleInt(10, 4.0 * offset), creature.Biota.PropertiesBodyPart[CombatBodyPart.Chest].DVal);

            // The composed physical damage is what the axis alone would deliver at the rating floor's DR: DVal x mod(total).
            var physical = 10 * result.PhysicalMultiplier * ArchetypeUplift.RatingModOf(220);
            Assert.AreEqual(10 * 4.0 * ArchetypeUplift.RatingModOf(32), physical, 1e-9);

            // A large offset never takes physical damage below the authored value.
            var weak = MakeCreature(withMissileDefense: true);
            weak.Biota.PropertiesSpellBook = new Dictionary<int, float> { [77] = 2.1f };
            weak.Biota.PropertiesBodyPart = weak.Biota.PropertiesBodyPart.ToDictionary(k => k.Key, k => new PropertiesBodyPart
            {
                DType = k.Value.DType, DVal = k.Key == CombatBodyPart.Chest ? 40 : k.Value.DVal, DVar = k.Value.DVar, BaseArmor = k.Value.BaseArmor, MLF = 1, MRF = 1, MLB = 1, MRB = 1,
            });
            var weakInfo = Info();  // live own damage: the chest's 40, already at the run standard, and cap 1 holds it there: axis x1
            weakInfo.CapRatio = 1.0;
            weakInfo.Run = info.Run;
            weakInfo.Own = info.Own;
            var weakSession = ArchetypeUplift.Begin(weak, weakInfo, 1.0);
            ArchetypeUplift.FloorRatings(weak, weakSession, id => id == 77 ? 100.0 : 0.0);
            var weakResult = ArchetypeUplift.Apply(weak, weakSession, 400, 0, applyCap: false);

            Assert.AreEqual(1.0, weakResult.PhysicalMultiplier, 1e-12);
            Assert.AreEqual(40, weak.Biota.PropertiesBodyPart[CombatBodyPart.Chest].DVal, "authored is the floor");
        }

        // ---- the defense cap (owner ruling 2026-10-23: defensive axes aim at the archetype median) ------------
        //
        // Every case below gives the creature an own value ABOVE its own-level standard, so the k term would carry it above
        // the run median: at the shipped defense cap (1.0) it lands exactly on the median, at 1.1 it keeps 10% headroom,
        // and a value authored above the median is kept. The offense cap is set to 1.25 throughout to show it does not leak.

        private static ArchetypeUpliftInfo DefenseCapInfo(double defenseCap)
        {
            var info = Info();
            info.CapRatio = 1.25;
            info.DefenseCapRatio = defenseCap;
            return info;
        }

        [TestMethod]
        public void The_shipped_defense_cap_is_one()
        {
            Assert.AreEqual(1.0, DungeonPopulationLimits.DefaultUpliftDefenseCap);
            Assert.AreEqual(1.0, new ArchetypeUpliftInfo().DefenseCapRatio, "a hand-built info gets the shipped defense cap");
        }

        [TestMethod]
        public void Defense_cap_health_lands_on_the_median_keeps_authored_above_and_rides_the_cap()
        {
            ArchetypeUpliftInfo HealthInfo(double cap)
            {
                var info = DefenseCapInfo(cap);
                info.Run = info.Run.With(health: 8000);
                info.Own = info.Own.With(health: 2000);
                return info;
            }

            // authored 3000 vs own-level 2000: k term 12000.
            Assert.AreEqual(8000, ArchetypeUplift.HealthTarget(3000, HealthInfo(1.0), 1.0), "cap 1: exactly the median");
            Assert.AreEqual(8800, ArchetypeUplift.HealthTarget(3000, HealthInfo(1.1), 1.0), "cap 1.1: 10% headroom");
            Assert.AreEqual(12000, ArchetypeUplift.HealthTarget(3000, HealthInfo(0.0), 1.0), "cap 0: no cap, the k term");
            Assert.AreEqual(9000, ArchetypeUplift.HealthTarget(9000, HealthInfo(1.0), 1.0), "authored above the median is kept");
            Assert.AreEqual(12000, ArchetypeUplift.HealthTarget(3000, HealthInfo(1.0), 1.5), "the gem multiplier still applies after the median");
        }

        [TestMethod]
        public void Defense_cap_defense_skills_land_on_the_median_keep_authored_above_and_ride_the_cap()
        {
            // Melee and missile: source median 600 at L, 300 at l; own 500 -> k term 1000.
            foreach (var skill in new[] { Skill.MeleeDefense, Skill.MissileDefense })
            {
                Assert.AreEqual(600u, ArchetypeUplift.SkillTarget(DefenseCapInfo(1.0), skill, 500, 0), $"{skill}: cap 1, exactly the median");
                Assert.AreEqual(660u, ArchetypeUplift.SkillTarget(DefenseCapInfo(1.1), skill, 500, 0), $"{skill}: cap 1.1");
                Assert.AreEqual(1000u, ArchetypeUplift.SkillTarget(DefenseCapInfo(0.0), skill, 500, 0), $"{skill}: cap 0, the k term");
                Assert.AreEqual(700u, ArchetypeUplift.SkillTarget(DefenseCapInfo(1.0), skill, 700, 0), $"{skill}: authored above the median kept");
            }

            // Magic: the cross-archetype median 500 at L, 250 at l; own 400 -> k term 800.
            Assert.AreEqual(500u, ArchetypeUplift.SkillTarget(DefenseCapInfo(1.0), Skill.MagicDefense, 400, 0), "magic: cap 1, exactly the median");
            Assert.AreEqual(550u, ArchetypeUplift.SkillTarget(DefenseCapInfo(1.1), Skill.MagicDefense, 400, 0), "magic: cap 1.1");
            Assert.AreEqual(600u, ArchetypeUplift.SkillTarget(DefenseCapInfo(1.0), Skill.MagicDefense, 600, 0), "magic: authored above kept");

            // The offense cap (1.25) still gives an attack skill its headroom on the same info: 300 x 2 capped at 1.25 x 400.
            Assert.AreEqual(500u, ArchetypeUplift.SkillTarget(DefenseCapInfo(1.0), Skill.UnarmedCombat, 300, 0), "attack rides the offense cap");
        }

        [TestMethod]
        public void Defense_cap_armour_lands_on_the_median_keeps_authored_above_and_rides_the_cap()
        {
            // Armour: own best 100 vs own-level 50 and run 200 -> k term 400. Damage (offense) keeps its 1.25 cap: x5.
            ArchetypeUpliftInfo Steep(double defenseCap)
            {
                var info = SteepInfo(1.25);
                info.DefenseCapRatio = defenseCap;
                return info;
            }

            var atMedian = MakeCreature(withMissileDefense: true);
            var r1 = ArchetypeUplift.Apply(atMedian, ArchetypeUplift.Begin(atMedian, Steep(1.0), 1.0), 400, 0, applyCap: false);
            Assert.AreEqual(2.0, r1.ArmorMultiplier, 1e-9, "cap 1: 100 -> the median 200");
            Assert.AreEqual(200, atMedian.Biota.PropertiesBodyPart[CombatBodyPart.Abdomen].BaseArmor);
            Assert.AreEqual(5.0, r1.DamageMultiplier, 1e-9, "damage still rides the offense cap 1.25");

            var headroom = MakeCreature(withMissileDefense: true);
            var r2 = ArchetypeUplift.Apply(headroom, ArchetypeUplift.Begin(headroom, Steep(1.1), 1.0), 400, 0, applyCap: false);
            Assert.AreEqual(2.2, r2.ArmorMultiplier, 1e-9, "cap 1.1: 220");

            var authored = MakeCreature(withMissileDefense: true);
            authored.Biota.PropertiesBodyPart = authored.Biota.PropertiesBodyPart.ToDictionary(k => k.Key, k => new PropertiesBodyPart
            {
                DType = k.Value.DType, DVal = k.Value.DVal, DVar = k.Value.DVar, BaseArmor = 300, MLF = 1, MRF = 1, MLB = 1, MRB = 1,
            });
            var r3 = ArchetypeUplift.Apply(authored, ArchetypeUplift.Begin(authored, Steep(1.0), 1.0), 400, 0, applyCap: false);
            Assert.AreEqual(1.0, r3.ArmorMultiplier, 1e-12, "authored 300 above the median 200 is kept");
            Assert.AreEqual(300, authored.Biota.PropertiesBodyPart[CombatBodyPart.Abdomen].BaseArmor);
        }

        [TestMethod]
        public void Defense_cap_damage_resist_rating_lands_on_the_median_keeps_authored_above_and_rides_the_cap()
        {
            // DRR: run 10, own-level 5. Authored 8 -> k term 16. DamageRating (offense, run 12 own 6): authored 10 -> k 20,
            // held at the offense cap 1.25 x 12 = 15 on every info, so the two caps are visibly independent.
            int Drr(double defenseCap, int authored)
            {
                var creature = MakeCreature(withMissileDefense: true);
                creature.DamageResistRating = authored;
                creature.DamageRating = 10;
                ArchetypeUplift.FloorRatings(creature, ArchetypeUplift.Begin(creature, DefenseCapInfo(defenseCap), 1.0), id => 0.0);
                Assert.AreEqual(15, creature.DamageRating, "DamageRating rides the offense cap");
                return creature.DamageResistRating ?? 0;
            }

            Assert.AreEqual(10, Drr(1.0, 8), "cap 1: exactly the median");
            Assert.AreEqual(11, Drr(1.1, 8), "cap 1.1");
            Assert.AreEqual(16, Drr(0.0, 8), "cap 0: the k term");
            Assert.AreEqual(14, Drr(1.0, 14), "authored above the median kept");
        }

        [TestMethod]
        public void Defense_cap_endurance_and_quickness_land_on_the_median_keep_authored_above_and_ride_the_cap()
        {
            // Attribute medians 200 at L, 100 at l. Authored 150 -> k term 300. Strength (offense) keeps the 1.25 cap: 250.
            Dictionary<PropertyAttribute, uint> Targets(double defenseCap, uint authored)
                => ArchetypeUplift.AttributeTargets(DefenseCapInfo(defenseCap), a => a == PropertyAttribute.Strength ? 150u : authored);

            foreach (var attribute in new[] { PropertyAttribute.Endurance, PropertyAttribute.Quickness })
            {
                Assert.AreEqual(DungeonPopulationLimits.DefaultUpliftDefenseCap, ArchetypeUplift.AttributeCap(attribute, DefenseCapInfo(DungeonPopulationLimits.DefaultUpliftDefenseCap)));
                Assert.AreEqual(200u, Targets(1.0, 150)[attribute], $"{attribute}: cap 1, exactly the median");
                Assert.AreEqual(220u, Targets(1.1, 150)[attribute], $"{attribute}: cap 1.1");
                Assert.AreEqual(300u, Targets(0.0, 150)[attribute], $"{attribute}: cap 0, the k term");
                Assert.IsFalse(Targets(1.0, 250).ContainsKey(attribute), $"{attribute}: authored 250 above the median is kept (not raised)");
            }

            Assert.AreEqual(250u, Targets(1.0, 150)[PropertyAttribute.Strength], "Strength rides the offense cap");
            Assert.AreEqual(1.25, ArchetypeUplift.AttributeCap(PropertyAttribute.Strength, DefenseCapInfo(1.0)));
        }

        [TestMethod]
        public void LogLine_names_the_archetype_source_and_every_axis_multiplier()
        {
            var creature = MakeCreature(withMissileDefense: false);
            var info = Info();
            info.Source = CreatureArchetype.Unclassified;

            var session = ArchetypeUplift.Begin(creature, info, 1.0);
            var result = ArchetypeUplift.Apply(creature, session, 400, 0, applyCap: false);
            var line = ArchetypeUplift.LogLine(42, 400, 200, session, result, default, 0, session.HealthTarget);

            StringAssert.Contains(line, "as MeleeTank against All");
            StringAssert.Contains(line, "dmg own(live) 10 -> 40 (x4)");
            StringAssert.Contains(line, "arm x2");
            StringAssert.Contains(line, "MissileDefense:0->600(+600)");
            StringAssert.Contains(line, "MagicDefense:0->500(+500)");
            StringAssert.Contains(line, "2 defense row(s) added");
        }
    }
}
