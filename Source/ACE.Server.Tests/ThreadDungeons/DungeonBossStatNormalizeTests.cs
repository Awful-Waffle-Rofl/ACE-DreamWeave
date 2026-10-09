using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldEvents.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// Code review follow-ups on boss normalization (2026-09-13): the boss's attributes and its wielded weapon
    /// damage. Pure arithmetic only - ACE.Server.Tests cannot build a live Creature, so the spawner's application
    /// of these numbers (DungeonCreatureNormalizer) is not exercised here.
    /// </summary>
    [TestClass]
    public class DungeonBossStatNormalizeTests
    {
        private static readonly PropertyAttribute[] Six =
        {
            PropertyAttribute.Strength, PropertyAttribute.Endurance, PropertyAttribute.Coordination,
            PropertyAttribute.Quickness, PropertyAttribute.Focus, PropertyAttribute.Self,
        };

        private static Dictionary<PropertyAttribute, uint> Attrs(uint str, uint end, uint coord, uint quick, uint focus, uint self)
            => new Dictionary<PropertyAttribute, uint>
            {
                [PropertyAttribute.Strength] = str,
                [PropertyAttribute.Endurance] = end,
                [PropertyAttribute.Coordination] = coord,
                [PropertyAttribute.Quickness] = quick,
                [PropertyAttribute.Focus] = focus,
                [PropertyAttribute.Self] = self,
            };

        private static DungeonBandStandard Standard() => DungeonBandStandard.ForTest(
            new Dictionary<Skill, uint>
            {
                [Skill.MeleeDefense] = 300, [Skill.MissileDefense] = 280, [Skill.MagicDefense] = 250,
                [Skill.HeavyWeapons] = 350, [Skill.WarMagic] = 320,
            },
            220, 400, attributeMedians: Attrs(200, 180, 220, 210, 150, 160));

        /// <summary>
        /// Mirrors AttributeFormula.GetFormula (AttributeFormula.cs:52-67): attr1 + attr2, divided and rounded away
        /// from zero when the divisor is not 1. The attribute pairs and divisors used below are FIXTURE values in
        /// the shape of the portal.dat skill table, not assertions about it; the property under test holds for
        /// any pair and divisor.
        /// </summary>
        private static uint Formula(IReadOnlyDictionary<PropertyAttribute, uint> attrs, PropertyAttribute a1, PropertyAttribute a2, uint divisor)
        {
            var total = attrs[a1] + attrs[a2];

            return divisor == 1 ? total : (uint)Math.Round((float)total / divisor, MidpointRounding.AwayFromZero);
        }

        // ---- attributes ---------------------------------------------------------------------------------

        [TestMethod]
        public void Offense_ratio_scales_strength_coordination_focus_self_and_defense_ratio_scales_endurance_quickness()
        {
            var std = Standard();

            Assert.AreEqual(300u, DungeonCombatNormalizer.AttributeTarget(std, PropertyAttribute.Strength, 1.5, 1.0));
            Assert.AreEqual(330u, DungeonCombatNormalizer.AttributeTarget(std, PropertyAttribute.Coordination, 1.5, 1.0));
            Assert.AreEqual(225u, DungeonCombatNormalizer.AttributeTarget(std, PropertyAttribute.Focus, 1.5, 1.0));
            Assert.AreEqual(240u, DungeonCombatNormalizer.AttributeTarget(std, PropertyAttribute.Self, 1.5, 1.0));
            Assert.AreEqual(180u, DungeonCombatNormalizer.AttributeTarget(std, PropertyAttribute.Endurance, 1.5, 1.0), "Endurance ignores the offense ratio");
            Assert.AreEqual(210u, DungeonCombatNormalizer.AttributeTarget(std, PropertyAttribute.Quickness, 1.5, 1.0), "Quickness ignores the offense ratio");
            Assert.AreEqual(90u, DungeonCombatNormalizer.AttributeTarget(std, PropertyAttribute.Endurance, 1.0, 0.5));
            Assert.AreEqual(105u, DungeonCombatNormalizer.AttributeTarget(std, PropertyAttribute.Quickness, 1.0, 0.5));
        }

        [TestMethod]
        public void Attribute_target_is_zero_for_an_empty_standard_a_missing_median_or_a_zero_ratio()
        {
            var std = Standard();
            var noAttributes = DungeonBandStandard.ForTest(new Dictionary<Skill, uint> { [Skill.MeleeDefense] = 300 }, 220, 400);

            Assert.AreEqual(0u, DungeonCombatNormalizer.AttributeTarget(DungeonBandStandard.Empty, PropertyAttribute.Strength, 1.0, 1.0));
            Assert.AreEqual(0u, DungeonCombatNormalizer.AttributeTarget(noAttributes, PropertyAttribute.Strength, 1.0, 1.0));
            Assert.AreEqual(0u, DungeonCombatNormalizer.AttributeTarget(std, PropertyAttribute.Strength, 0.0, 1.0), "0 = leave offense authored");
            Assert.AreEqual(0u, DungeonCombatNormalizer.AttributeTarget(std, PropertyAttribute.Endurance, 1.0, double.NaN));
        }

        /// <summary>
        /// The review's finding, as a property: 43270 (600/400/400/400/350/500) and 51617 (500/500/300/300/480/480),
        /// given Str/End/Coord/Quick/Focus/Self order, keep every skill Current well above the band even with
        /// InitLevel already set; with the attributes set too, each skill's Current equals the band median member's
        /// (median attributes + median InitLevel) within formula rounding.
        /// </summary>
        [TestMethod]
        public void A_boss_with_extreme_attributes_ends_on_the_band_median_members_skill_current()
        {
            var std = Standard();
            var formulas = new (Skill Skill, PropertyAttribute A1, PropertyAttribute A2, uint Divisor, bool Attack)[]
            {
                (Skill.MeleeDefense, PropertyAttribute.Coordination, PropertyAttribute.Quickness, 3, false),
                (Skill.MissileDefense, PropertyAttribute.Coordination, PropertyAttribute.Quickness, 5, false),
                (Skill.MagicDefense, PropertyAttribute.Focus, PropertyAttribute.Self, 7, false),
                (Skill.HeavyWeapons, PropertyAttribute.Strength, PropertyAttribute.Coordination, 3, true),
                (Skill.WarMagic, PropertyAttribute.Focus, PropertyAttribute.Self, 4, true),
            };

            var bosses = new[]
            {
                (Wcid: 43270u, Authored: Attrs(600, 400, 400, 400, 350, 500)),
                (Wcid: 51617u, Authored: Attrs(500, 500, 300, 300, 480, 480)),
            };

            var medianMember = Six.ToDictionary(a => a, a => std.AttributeMedianFor(a));

            foreach (var boss in bosses)
            {
                var normalized = Six.ToDictionary(a => a, a =>
                {
                    var target = DungeonCombatNormalizer.AttributeTarget(std, a, 1.0, 1.0);
                    return target == 0 ? boss.Authored[a] : target;
                });

                foreach (var f in formulas)
                {
                    var init = f.Attack
                        ? DungeonCombatNormalizer.AttackSkillTarget(std, f.Skill, 1.0)
                        : DungeonCombatNormalizer.DefenseSkillTarget(std, f.Skill, 1.0);

                    var bandCurrent = Formula(medianMember, f.A1, f.A2, f.Divisor) + std.MedianFor(f.Skill);
                    var initOnly = Formula(boss.Authored, f.A1, f.A2, f.Divisor) + init;
                    var withAttributes = Formula(normalized, f.A1, f.A2, f.Divisor) + init;

                    Assert.IsTrue(initOnly > bandCurrent, $"{boss.Wcid} {f.Skill}: the fixture must reproduce the finding ({initOnly} vs band {bandCurrent})");
                    Assert.IsTrue(Math.Abs((long)withAttributes - bandCurrent) <= 1,
                        $"{boss.Wcid} {f.Skill}: normalized Current {withAttributes} must equal the band median member's {bandCurrent}");
                }
            }
        }

        [TestMethod]
        public void Band_standard_takes_the_lower_median_of_each_attribute_and_excludes_zero()
        {
            var tables = new Dictionary<string, SpeciesTableDef>
            {
                ["f"] = new SpeciesTableDef
                {
                    Id = "f",
                    Members = Enumerable.Range(1, 5).Select(i => new SpeciesMemberDef { Wcid = (uint)i, Role = 0 }).ToList(),
                },
            };

            var strengths = new uint[] { 100, 200, 300, 0, 400 };

            DungeonStatProfile ProfileOf(uint w) => new DungeonStatProfile(200, 0, new Dictionary<Skill, uint>(), 50, 0, 0, 0.0,
                new Dictionary<PropertyAttribute, uint> { [PropertyAttribute.Strength] = strengths[w - 1], [PropertyAttribute.Self] = 90 });

            var std = DungeonBandStandard.Compute(tables, 200, ProfileOf, new DungeonRosterBand(1.0, 1.15), 1.0);

            Assert.AreEqual(5, std.SampleCount);
            Assert.AreEqual(200u, std.AttributeMedianFor(PropertyAttribute.Strength), "lower middle of 100/200/300/400; the 0 is no data");
            Assert.AreEqual(90u, std.AttributeMedianFor(PropertyAttribute.Self));
            Assert.AreEqual(0u, std.AttributeMedianFor(PropertyAttribute.Focus), "no member authored Focus");
        }

        [TestMethod]
        public void ProfileOfWeenie_reads_each_attribute_base_and_attributes_alone_are_not_data()
        {
            var weenie = new Weenie
            {
                WeenieClassId = 12345,
                PropertiesInt = new Dictionary<PropertyInt, int> { [PropertyInt.Level] = 200 },
                PropertiesAttribute = new Dictionary<PropertyAttribute, PropertiesAttribute>
                {
                    [PropertyAttribute.Strength] = new PropertiesAttribute { InitLevel = 250 },
                    [PropertyAttribute.Endurance] = new PropertiesAttribute { InitLevel = 180, LevelFromCP = 7 },
                },
            };

            var profile = ThreadDungeonSpawner.ProfileOfWeenie(weenie);

            Assert.AreEqual(250u, profile.Attributes[PropertyAttribute.Strength]);
            Assert.AreEqual(187u, profile.Attributes[PropertyAttribute.Endurance], "InitLevel + LevelFromCP, CreatureAttribute.Base for a non-player");
            Assert.IsFalse(profile.Attributes.ContainsKey(PropertyAttribute.Focus));
            Assert.IsFalse(profile.HasData, "attributes do not count toward MinSample");
            Assert.AreEqual(0, DungeonStatProfile.Empty.Attributes.Count);
        }

        // ---- weapons ------------------------------------------------------------------------------------

        [TestMethod]
        public void Weapon_damage_is_set_so_its_effective_max_lands_on_the_band_body_damage()
        {
            Assert.AreEqual(220, DungeonCombatNormalizer.WeaponDamageTarget(65, 1.0, 220, 1.0), "General Garsh's axe 24567 authors Damage 65 in local ace_world");
            Assert.AreEqual(220, DungeonCombatNormalizer.WeaponDamageTarget(400, 1.0, 220, 1.0), "an over-tuned blade comes down");
            Assert.AreEqual(110, DungeonCombatNormalizer.WeaponDamageTarget(120, 2.0, 220, 1.0), "ammunition under a x2 launcher: 110 x 2 = 220");
            Assert.AreEqual(275, DungeonCombatNormalizer.WeaponDamageTarget(65, 1.0, 220, 1.25));
            Assert.AreEqual(220, DungeonCombatNormalizer.WeaponDamageTarget(65, double.NaN, 220, 1.0), "a garbled DamageMod reads as 1");
            Assert.AreEqual(220, DungeonCombatNormalizer.WeaponDamageTarget(65, 0.0, 220, 1.0), "so does a zero one");
        }

        [TestMethod]
        public void Weapon_damage_is_left_authored_for_no_damage_no_standard_or_no_ratio_and_never_rounds_to_zero()
        {
            Assert.AreEqual(0, DungeonCombatNormalizer.WeaponDamageTarget(0, 1.0, 220, 1.0), "a Damage 0 item is not a weapon");
            Assert.AreEqual(65, DungeonCombatNormalizer.WeaponDamageTarget(65, 1.0, 0, 1.0));
            Assert.AreEqual(65, DungeonCombatNormalizer.WeaponDamageTarget(65, 1.0, 220, 0.0), "0 = leave offense authored");
            Assert.AreEqual(65, DungeonCombatNormalizer.WeaponDamageTarget(65, 1.0, 220, double.NaN));
            Assert.AreEqual(1, DungeonCombatNormalizer.WeaponDamageTarget(65, 1000.0, 220, 1.0), "0.22 rounds to 0, floored at 1");
        }
    }
}
