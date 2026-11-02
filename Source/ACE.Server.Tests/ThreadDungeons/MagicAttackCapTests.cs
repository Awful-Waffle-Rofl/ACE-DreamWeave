using System;
using System.Collections.Generic;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Managers;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// Owner ruling 2026-10-30: a Thread run creature's pre-enchantment War, Life, Void and Creature Enchantment skill is held
    /// at dynamic_dungeons_magic_attack_skill_cap (default 508) so a top player (about 530 magic defense) resists about 66%
    /// of monster spells. Pins the clamp arithmetic, the per-creature ceiling on the four schools only, the dial's sanitizer
    /// and ReadLimits wiring, the plan field, the PrepareForPlacement hook position, and the resist arithmetic the 508 comes from.
    /// </summary>
    [TestClass]
    public class MagicAttackCapTests
    {
        private const string CapKey = "dynamic_dungeons_magic_attack_skill_cap";
        private const string Spawner = "Source/ACE.Server/ThreadDungeons/ThreadDungeonSpawner.cs";
        private const string Signature = "internal static void PrepareForPlacement(ThreadDungeonRun run, Creature creature, DungeonSpawnPlanEntry entry, DungeonSpawnPlan plan,";

        private static readonly Skill[] MagicSchools = { Skill.WarMagic, Skill.LifeMagic, Skill.VoidMagic, Skill.CreatureEnchantment };
        private static uint nextGuid = 0x7F0002A1;

        private static Creature NewCreature(uint magicInit, uint weaponInit)
        {
            var skills = new Dictionary<Skill, PropertiesSkill>
            {
                [Skill.HeavyWeapons] = new PropertiesSkill { InitLevel = weaponInit, SAC = SkillAdvancementClass.Trained },
                [Skill.MagicDefense] = new PropertiesSkill { InitLevel = magicInit, SAC = SkillAdvancementClass.Trained },
            };

            foreach (var school in MagicSchools)
                skills[school] = new PropertiesSkill { InitLevel = magicInit, SAC = SkillAdvancementClass.Trained };

            var weenie = new Weenie
            {
                WeenieClassId = nextGuid,
                ClassName = $"magicattackcap{nextGuid:X}",
                WeenieType = WeenieType.Creature,
                PropertiesInt = new Dictionary<PropertyInt, int> { [PropertyInt.Level] = 300 },
                PropertiesString = new Dictionary<PropertyString, string> { [PropertyString.Name] = "Cap Test" },
                PropertiesAttribute = new Dictionary<PropertyAttribute, PropertiesAttribute>
                {
                    [PropertyAttribute.Strength] = new PropertiesAttribute { InitLevel = 100 },
                    [PropertyAttribute.Endurance] = new PropertiesAttribute { InitLevel = 100 },
                    [PropertyAttribute.Coordination] = new PropertiesAttribute { InitLevel = 100 },
                    [PropertyAttribute.Quickness] = new PropertiesAttribute { InitLevel = 100 },
                    [PropertyAttribute.Focus] = new PropertiesAttribute { InitLevel = 100 },
                    [PropertyAttribute.Self] = new PropertiesAttribute { InitLevel = 100 },
                },
                PropertiesAttribute2nd = new Dictionary<PropertyAttribute2nd, PropertiesAttribute2nd>
                {
                    [PropertyAttribute2nd.MaxHealth] = new PropertiesAttribute2nd { InitLevel = 400 },
                    [PropertyAttribute2nd.MaxStamina] = new PropertiesAttribute2nd { InitLevel = 200 },
                    [PropertyAttribute2nd.MaxMana] = new PropertiesAttribute2nd { InitLevel = 200 },
                },
                PropertiesSkill = skills,
            };

            return new Creature(weenie, new ObjectGuid(nextGuid++));
        }

        private static DungeonSpawnPlan PlanWithCap(double cap) => new DungeonSpawnPlan { MagicAttackSkillCap = cap };

        // ---- 1. pure arithmetic ---------------------------------------------------------------------------

        [TestMethod]
        public void The_clamp_lowers_a_value_above_the_ceiling_and_leaves_the_rest_alone()
        {
            Assert.AreEqual(508u, DungeonCombatNormalizer.ClampToDefenseCeiling(857, 508));
            Assert.AreEqual(508u, DungeonCombatNormalizer.ClampToDefenseCeiling(508, 508));
            Assert.AreEqual(400u, DungeonCombatNormalizer.ClampToDefenseCeiling(400, 508));
            Assert.AreEqual(857u, DungeonCombatNormalizer.ClampToDefenseCeiling(857, null));
        }

        [TestMethod]
        public void Only_the_four_magic_attack_schools_count_as_magic_attack_skills()
        {
            foreach (var school in MagicSchools)
                Assert.IsTrue(DungeonCombatNormalizer.IsMagicAttackSkill(school), school.ToString());

            foreach (var other in new[] { Skill.MagicDefense, Skill.HeavyWeapons, Skill.MeleeDefense, Skill.MissileDefense, Skill.Run, Skill.ItemEnchantment })
                Assert.IsFalse(DungeonCombatNormalizer.IsMagicAttackSkill(other), other.ToString());
        }

        [TestMethod]
        public void The_default_is_508_and_the_resist_at_530_magic_defense_is_about_66_percent()
        {
            Assert.AreEqual(508.0, DungeonPopulationLimits.DefaultMagicAttackSkillCap);

            // resist = 1 - GetSkillChance(A, D); WorldObject.MagicDefenseCheck reports exactly that as resistChance.
            // The roll is random, so read the reported chance and ignore the outcome.
            WorldObject.MagicDefenseCheck(508, 530, out var capped);
            Assert.AreEqual(0.66, capped, 0.005, "A = 508 against D = 530 resists about 66%");

            // Discriminating: an uncapped 857 (the stat curve at level 500) barely resists.
            WorldObject.MagicDefenseCheck(857, 530, out var uncapped);
            Assert.IsTrue(uncapped < 0.001, $"uncapped resist {uncapped}");

            // And the analytic solution the default comes from.
            Assert.AreEqual(507.9, 530 + Math.Log(0.34 / 0.66) / 0.03, 0.05);
        }

        // ---- 2. the per-creature ceiling ------------------------------------------------------------------

        [TestMethod]
        public void A_creature_above_the_cap_reads_the_cap_for_the_four_schools_only()
        {
            TestGameTables.EnsureInitialized();

            var creature = NewCreature(magicInit: 700, weaponInit: 700);

            // Positive control: uncapped, the authored skills exceed 508 on every skill under test.
            foreach (var school in MagicSchools)
            {
                Assert.IsTrue(creature.GetCreatureSkill(school).Current > 508u, $"{school} uncapped Current");
                Assert.IsTrue(creature.GetCreatureSkill(school).Base > 508u, $"{school} uncapped Base");
            }

            var heavyBefore = creature.GetCreatureSkill(Skill.HeavyWeapons).Current;
            var heavyBaseBefore = creature.GetCreatureSkill(Skill.HeavyWeapons).Base;
            var defenseBefore = creature.GetCreatureSkill(Skill.MagicDefense).Current;
            Assert.IsTrue(heavyBefore > 508u && defenseBefore > 508u, "positive control: non-magic-attack skills are above the cap too");

            ThreadDungeonSpawner.ApplyMagicAttackCeiling(creature, PlanWithCap(508));

            foreach (var school in MagicSchools)
            {
                Assert.AreEqual(508u, creature.GetCreatureSkill(school).Current, $"{school} Current");
                Assert.AreEqual(508u, creature.GetCreatureSkill(school).Base, $"{school} Base");
            }

            Assert.AreEqual(heavyBefore, creature.GetCreatureSkill(Skill.HeavyWeapons).Current, "a weapon skill is not a magic attack skill");
            Assert.AreEqual(heavyBaseBefore, creature.GetCreatureSkill(Skill.HeavyWeapons).Base);
            Assert.AreEqual(defenseBefore, creature.GetCreatureSkill(Skill.MagicDefense).Current, "magic DEFENSE is not clamped by the attack cap");
        }

        [TestMethod]
        public void A_creature_at_or_below_the_cap_is_unchanged()
        {
            TestGameTables.EnsureInitialized();

            var creature = NewCreature(magicInit: 100, weaponInit: 100);
            var before = new Dictionary<Skill, (uint Base, uint Current)>();

            foreach (var school in MagicSchools)
                before[school] = (creature.GetCreatureSkill(school).Base, creature.GetCreatureSkill(school).Current);

            Assert.IsTrue(before[Skill.WarMagic].Current < 508u, "positive control: the fixture sits below the cap");

            ThreadDungeonSpawner.ApplyMagicAttackCeiling(creature, PlanWithCap(508));
            Assert.AreEqual(508u, creature.MagicAttackSkillCeiling, "the ceiling is set, but it only lowers");

            foreach (var school in MagicSchools)
            {
                Assert.AreEqual(before[school].Base, creature.GetCreatureSkill(school).Base, $"{school} Base");
                Assert.AreEqual(before[school].Current, creature.GetCreatureSkill(school).Current, $"{school} Current");
            }

            // Exactly at the cap: a creature whose skill equals 508 stays 508.
            creature.MagicAttackSkillCeiling = creature.GetCreatureSkill(Skill.WarMagic).Current;
            Assert.AreEqual(before[Skill.WarMagic].Current, creature.GetCreatureSkill(Skill.WarMagic).Current, "at the cap stays put");
        }

        [TestMethod]
        public void A_cap_of_zero_sets_no_ceiling_and_a_creature_outside_a_run_has_none()
        {
            TestGameTables.EnsureInitialized();

            var creature = NewCreature(magicInit: 700, weaponInit: 700);
            Assert.IsNull(creature.MagicAttackSkillCeiling, "a creature nobody touched has no ceiling (World Events, Pinnacles, the open world)");

            var uncapped = creature.GetCreatureSkill(Skill.WarMagic).Current;
            ThreadDungeonSpawner.ApplyMagicAttackCeiling(creature, PlanWithCap(0));

            Assert.IsNull(creature.MagicAttackSkillCeiling);
            Assert.AreEqual(uncapped, creature.GetCreatureSkill(Skill.WarMagic).Current);

            // A null plan is a no-op rather than a throw.
            ThreadDungeonSpawner.ApplyMagicAttackCeiling(creature, null);
            Assert.IsNull(creature.MagicAttackSkillCeiling);
        }

        [TestMethod]
        public void The_ceiling_sits_before_enchantments_and_never_applies_to_a_player()
        {
            // A Player cannot be constructed in this harness, so these two guarantees are pinned as source text.
            var setter = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(Spawner),
                "internal static void ApplyMagicAttackCeiling(Creature creature, DungeonSpawnPlan plan)");
            StringAssert.Contains(setter, "creature is Player");

            var skill = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Entity/CreatureSkill.cs");
            var calls = System.Text.RegularExpressions.Regex.Matches(skill, @"ClampToDefenseCeiling\(total, creature\.GetMagicAttackSkillCeiling\(Skill\)\)");
            Assert.AreEqual(2, calls.Count, "Base and Current both clamp");

            var multiplier = skill.IndexOf("GetSkillMod_Multiplier(Skill)", StringComparison.Ordinal);
            Assert.IsTrue(multiplier >= 0, "positive control: the enchantment multiplier is in the file");

            foreach (System.Text.RegularExpressions.Match call in calls)
            {
                Assert.IsTrue(call.Index < multiplier, "each clamp precedes the enchantment multiplier, so a player debuff lands on top");
                Assert.IsTrue(skill.Substring(call.Index - 700, 700).Contains("creature != null"), "each clamp sits in a non-player creature branch");
            }
        }
        // ---- 3. the dial ----------------------------------------------------------------------------------

        [TestMethod]
        public void The_sanitizer_reads_garbage_as_the_default_and_negative_as_off()
        {
            Assert.AreEqual(508.0, DungeonCombatNormalizer.SanitizeMagicAttackSkillCap(double.NaN));
            Assert.AreEqual(508.0, DungeonCombatNormalizer.SanitizeMagicAttackSkillCap(double.PositiveInfinity));
            Assert.AreEqual(508.0, DungeonCombatNormalizer.SanitizeMagicAttackSkillCap(double.NegativeInfinity));
            Assert.AreEqual(0.0, DungeonCombatNormalizer.SanitizeMagicAttackSkillCap(-5));
            Assert.AreEqual(0.0, DungeonCombatNormalizer.SanitizeMagicAttackSkillCap(0));
            Assert.AreEqual(600.0, DungeonCombatNormalizer.SanitizeMagicAttackSkillCap(600), "positive control: a legal value passes through");
            Assert.AreEqual(DungeonCombatNormalizer.MaxMagicAttackSkillCap, DungeonCombatNormalizer.SanitizeMagicAttackSkillCap(1e12));

            Assert.IsNull(DungeonCombatNormalizer.MagicAttackCeilingFor(0));
            Assert.AreEqual(508u, DungeonCombatNormalizer.MagicAttackCeilingFor(508));
        }

        [TestMethod]
        public void ReadLimits_reads_and_sanitizes_the_live_dial()
        {
            DefaultPropertyManager.LoadDefaultProperties();

            try
            {
                Assert.IsTrue(PropertyManager.ModifyDouble(CapKey, DungeonPopulationLimits.DefaultMagicAttackSkillCap), "key not registered");
                Assert.AreEqual(508.0, ThreadDungeonSpawner.ReadLimits(GroupScaling.Solo).MagicAttackSkillCap, 1e-12, "shipped default");

                Assert.IsTrue(PropertyManager.ModifyDouble(CapKey, double.NaN));
                Assert.AreEqual(508.0, ThreadDungeonSpawner.ReadLimits(GroupScaling.Solo).MagicAttackSkillCap, 1e-12, "NaN reads as the default");

                Assert.IsTrue(PropertyManager.ModifyDouble(CapKey, -3.0));
                Assert.AreEqual(0.0, ThreadDungeonSpawner.ReadLimits(GroupScaling.Solo).MagicAttackSkillCap, 1e-12, "negative reads as off");

                Assert.IsTrue(PropertyManager.ModifyDouble(CapKey, 640.0));
                Assert.AreEqual(640.0, ThreadDungeonSpawner.ReadLimits(GroupScaling.Solo).MagicAttackSkillCap, 1e-12, "a legal value passes through");
            }
            finally
            {
                PropertyManager.ModifyDouble(CapKey, DungeonPopulationLimits.DefaultMagicAttackSkillCap);
            }
        }

        [TestMethod]
        public void The_plan_carries_the_sanitized_cap_and_a_default_struct_reads_off()
        {
            Assert.AreEqual(0.0, default(DungeonPopulationLimits).MagicAttackSkillCap, "a default struct is off");
            Assert.AreEqual(0.0, new DungeonSpawnPlan().MagicAttackSkillCap, "a hand-built plan carries no cap");

            var source = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/DungeonPopulationBuilder.cs");
            StringAssert.Contains(source, "plan.MagicAttackSkillCap = DungeonCombatNormalizer.SanitizeMagicAttackSkillCap(limits.MagicAttackSkillCap);");

            var spawner = PooledLootSourceText.Read(Spawner);
            var limits = PooledLootSourceText.MethodBody(spawner, "internal static DungeonPopulationLimits ReadLimits(GroupScaling group)");
            StringAssert.Contains(limits, "magicAttackSkillCap: ReadMagicAttackSkillCapDial()");

            var reader = PooledLootSourceText.MethodBody(spawner, "private static double ReadMagicAttackSkillCapDial()");
            StringAssert.Contains(reader, "\"" + CapKey + "\"");
            StringAssert.Contains(reader, "DungeonCombatNormalizer.SanitizeMagicAttackSkillCap(configured)");
            StringAssert.Contains(reader, "DungeonCombatNormalizer.MagicAttackSkillCapNeedsWarning(configured)");
        }

        [TestMethod]
        public void The_warning_predicate_ignores_legal_fractions_and_flags_out_of_range_values()
        {
            Assert.IsFalse(DungeonCombatNormalizer.MagicAttackSkillCapNeedsWarning(600.4), "a legal fraction is rounded silently");
            Assert.IsFalse(DungeonCombatNormalizer.MagicAttackSkillCapNeedsWarning(508));
            Assert.IsFalse(DungeonCombatNormalizer.MagicAttackSkillCapNeedsWarning(0));
            Assert.IsTrue(DungeonCombatNormalizer.MagicAttackSkillCapNeedsWarning(-1));
            Assert.IsTrue(DungeonCombatNormalizer.MagicAttackSkillCapNeedsWarning(double.NaN));
            Assert.IsTrue(DungeonCombatNormalizer.MagicAttackSkillCapNeedsWarning(double.PositiveInfinity));
            Assert.IsTrue(DungeonCombatNormalizer.MagicAttackSkillCapNeedsWarning(DungeonCombatNormalizer.MaxMagicAttackSkillCap + 1));
        }

        [TestMethod]
        public void Speed_courses_keep_the_cap_through_CourseLimits()
        {
            DefaultPropertyManager.LoadDefaultProperties();

            try
            {
                Assert.IsTrue(PropertyManager.ModifyDouble(CapKey, 640.0), "key not registered");
                var limits = ThreadDungeonSpawner.ReadLimits(GroupScaling.Solo);
                Assert.AreEqual(640.0, limits.MagicAttackSkillCap, 1e-12, "positive control: the dial is read");

                var course = ACE.Server.SpeedCourses.SpeedCourseRules.CourseLimits(limits, 6);
                Assert.AreEqual(limits.MagicAttackSkillCap, course.MagicAttackSkillCap, 1e-12, "a speed course shares the dial");
            }
            finally
            {
                PropertyManager.ModifyDouble(CapKey, DungeonPopulationLimits.DefaultMagicAttackSkillCap);
            }
        }

        // ---- 5. through PrepareForPlacement ---------------------------------------------------------------

        private static void WithSeededPrepareKeys(Action body)
        {
            TestGameTables.EnsureInitialized();

            var keys = new (string Key, bool Value)[]
            {
                ("dynamic_dungeons_uplift_attributes", DungeonPopulationLimits.DefaultUpliftAttributes),
                ("dynamic_dungeons_strip_aoe_spells", DungeonSpellFilter.DefaultStripAoeSpellsEnabled),
            };

            try
            {
                foreach (var (key, value) in keys)
                    Assert.IsTrue(PropertyManager.ModifyBool(key, value), $"ModifyBool({key}) returned false: key not registered");

                Assert.IsTrue(PropertyManager.ModifyString("dynamic_dungeons_banned_spell_ids", DungeonSpellFilter.DefaultBannedSpellIds));

                body();
            }
            finally
            {
                foreach (var (key, value) in keys)
                    PropertyManager.ModifyBool(key, value);

                PropertyManager.ModifyString("dynamic_dungeons_banned_spell_ids", DungeonSpellFilter.DefaultBannedSpellIds);
            }
        }

        [TestMethod]
        public void PrepareForPlacement_caps_a_war_heavy_trash_creature_and_leaves_it_alone_at_cap_zero()
        {
            WithSeededPrepareKeys(() =>
            {
                var entry = new DungeonSpawnPlanEntry(1, DungeonRole.Trash, null);
                var capped = NewCreature(magicInit: 700, weaponInit: 700);
                var off = NewCreature(magicInit: 700, weaponInit: 700);
                var uncapped = capped.GetCreatureSkill(Skill.WarMagic).Current;
                Assert.IsTrue(uncapped > 508u, "positive control: war-heavy before the step");

                ThreadDungeonSpawner.PrepareForPlacement(null, capped, entry, new DungeonSpawnPlan { MagicAttackSkillCap = 508 }, observedMaxNonBoss: 2500);
                ThreadDungeonSpawner.PrepareForPlacement(null, off, entry, new DungeonSpawnPlan { MagicAttackSkillCap = 0 }, observedMaxNonBoss: 2500);

                Assert.AreEqual(508u, capped.MagicAttackSkillCeiling);
                foreach (var school in MagicSchools)
                    Assert.AreEqual(508u, capped.GetCreatureSkill(school).Current, school.ToString());

                Assert.IsNull(off.MagicAttackSkillCeiling, "cap 0 sets no ceiling");
                Assert.AreEqual(uncapped, off.GetCreatureSkill(Skill.WarMagic).Current, "cap 0 leaves the skill authored");
            });
        }

        [TestMethod]
        public void PrepareForPlacement_caps_a_boss_too()
        {
            WithSeededPrepareKeys(() =>
            {
                var boss = NewCreature(magicInit: 700, weaponInit: 700);
                Assert.IsTrue(boss.GetCreatureSkill(Skill.VoidMagic).Current > 508u, "positive control");

                var plan = new DungeonSpawnPlan { MagicAttackSkillCap = 508 };
                ThreadDungeonSpawner.PrepareForPlacement(null, boss, new DungeonSpawnPlanEntry(1, DungeonRole.Boss, null), plan, observedMaxNonBoss: 2500);

                foreach (var school in MagicSchools)
                    Assert.AreEqual(508u, boss.GetCreatureSkill(school).Current, school.ToString());
            });
        }
        // ---- 4. the hook ----------------------------------------------------------------------------------

        [TestMethod]
        public void The_hook_runs_after_every_magic_skill_writer_and_before_the_damage_ceiling()
        {
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(Spawner), Signature);

            var hook = body.IndexOf("ApplyMagicAttackCeiling(creature, plan);", StringComparison.Ordinal);
            Assert.IsTrue(hook >= 0, "PrepareForPlacement no longer sets the magic attack ceiling");
            Assert.AreEqual(hook, body.LastIndexOf("ApplyMagicAttackCeiling(creature, plan);", StringComparison.Ordinal), "set exactly once");

            foreach (var writer in new[]
            {
                "ReachUpStamp.Apply(",
                "DungeonCreatureNormalizer.StripCombatTraits(",
                "ArchetypeUplift.Apply(",
                "DungeonCreatureNormalizer.NormalizeBoss(",
            })
            {
                var at = body.IndexOf(writer, StringComparison.Ordinal);
                Assert.IsTrue(at >= 0, $"PrepareForPlacement no longer contains: {writer}");
                Assert.IsTrue(at < hook, $"the ceiling must come after {writer}");
            }

            Assert.IsTrue(body.IndexOf("ApplyUplift(", StringComparison.Ordinal) < hook, "after the band uplift");
            Assert.IsTrue(hook < body.IndexOf("ApplyDamageCeiling(logTag, creature, entry, plan);", StringComparison.Ordinal), "before the damage ceiling");
        }

        [TestMethod]
        public void Pinnacles_and_World_Events_never_set_the_ceiling()
        {
            var root = "Source/ACE.Server/";

            foreach (var file in new[] { "Pinnacles/PinnacleScaling.cs", "WorldEvents/WorldEventSpawner.cs", "MlDigsite/MlMapEventScaling.cs" })
            {
                var text = PooledLootSourceText.Read(root + file);
                Assert.IsTrue(text.IndexOf("MagicAttackSkillCeiling", StringComparison.Ordinal) < 0, file);
                Assert.IsTrue(text.IndexOf("ApplyMagicAttackCeiling", StringComparison.Ordinal) < 0, file);
            }
        }
    }
}