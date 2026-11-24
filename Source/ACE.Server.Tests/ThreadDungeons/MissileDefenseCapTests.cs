using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

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
    /// A Thread run creature's pre-enchantment Missile Defense is held at dynamic_dungeons_missile_defense_skill_cap
    /// (default 720, sized so a rank-3 Fast Aim archer with spec Run 580 hits about 90% at mid-bar against a 410 Thread).
    /// Mirrors MagicAttackCapTests: the clamp arithmetic, the per-creature ceiling on Missile Defense only, the min with the
    /// #1173 defense-skill ceiling, the dial's sanitizer and ReadLimits wiring, the plan field and the PrepareForPlacement hook.
    /// </summary>
    [TestClass]
    public class MissileDefenseCapTests
    {
        private const string CapKey = "dynamic_dungeons_missile_defense_skill_cap";
        private const string Spawner = "Source/ACE.Server/ThreadDungeons/ThreadDungeonSpawner.cs";
        private const string Signature = "internal static void PrepareForPlacement(ThreadDungeonRun run, Creature creature, DungeonSpawnPlanEntry entry, DungeonSpawnPlan plan,";

        private static uint nextGuid = 0x7F0003A1;

        private static Creature NewCreature(uint defenseInit)
        {
            var skills = new Dictionary<Skill, PropertiesSkill>
            {
                [Skill.MissileDefense] = new PropertiesSkill { InitLevel = defenseInit, SAC = SkillAdvancementClass.Trained },
                [Skill.MeleeDefense] = new PropertiesSkill { InitLevel = defenseInit, SAC = SkillAdvancementClass.Trained },
                [Skill.MagicDefense] = new PropertiesSkill { InitLevel = defenseInit, SAC = SkillAdvancementClass.Trained },
                [Skill.HeavyWeapons] = new PropertiesSkill { InitLevel = defenseInit, SAC = SkillAdvancementClass.Trained },
            };

            var weenie = new Weenie
            {
                WeenieClassId = nextGuid,
                ClassName = $"missiledefcap{nextGuid:X}",
                WeenieType = WeenieType.Creature,
                PropertiesInt = new Dictionary<PropertyInt, int> { [PropertyInt.Level] = 300 },
                PropertiesString = new Dictionary<PropertyString, string> { [PropertyString.Name] = "Missile Cap Test" },
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

        private static DungeonSpawnPlan PlanWithCap(double cap) => new DungeonSpawnPlan { MissileDefenseSkillCap = cap };

        // ---- 1. pure arithmetic ---------------------------------------------------------------------------

        [TestMethod]
        public void The_default_is_720()
        {
            Assert.AreEqual(720.0, DungeonPopulationLimits.DefaultMissileDefenseSkillCap);
        }

        [TestMethod]
        public void The_ceiling_helper_stands_for_the_sanitized_cap()
        {
            Assert.IsNull(DungeonCombatNormalizer.MissileDefenseCeilingFor(0));
            Assert.IsNull(DungeonCombatNormalizer.MissileDefenseCeilingFor(-3));
            Assert.AreEqual(720u, DungeonCombatNormalizer.MissileDefenseCeilingFor(720));
            Assert.AreEqual(1u, DungeonCombatNormalizer.MissileDefenseCeilingFor(1));
        }

        // ---- 2. the per-creature ceiling ------------------------------------------------------------------

        [TestMethod]
        public void A_creature_above_the_cap_reads_the_cap_on_Missile_Defense_Base_and_Current_only()
        {
            TestGameTables.EnsureInitialized();

            var creature = NewCreature(defenseInit: 900);

            // Positive control: uncapped, every authored skill under test exceeds 720.
            foreach (var skill in new[] { Skill.MissileDefense, Skill.MeleeDefense, Skill.MagicDefense, Skill.HeavyWeapons })
            {
                Assert.IsTrue(creature.GetCreatureSkill(skill).Current > 720u, $"{skill} uncapped Current");
                Assert.IsTrue(creature.GetCreatureSkill(skill).Base > 720u, $"{skill} uncapped Base");
            }

            var meleeBefore = creature.GetCreatureSkill(Skill.MeleeDefense).Current;
            var meleeBaseBefore = creature.GetCreatureSkill(Skill.MeleeDefense).Base;
            var magicBefore = creature.GetCreatureSkill(Skill.MagicDefense).Current;
            var magicBaseBefore = creature.GetCreatureSkill(Skill.MagicDefense).Base;
            var weaponBefore = creature.GetCreatureSkill(Skill.HeavyWeapons).Current;

            ThreadDungeonSpawner.ApplyMissileDefenseCeiling(creature, PlanWithCap(720));

            Assert.AreEqual(720u, creature.MissileDefenseSkillCeiling);
            Assert.AreEqual(720u, creature.GetCreatureSkill(Skill.MissileDefense).Current, "Current");
            Assert.AreEqual(720u, creature.GetCreatureSkill(Skill.MissileDefense).Base, "Base");

            Assert.AreEqual(meleeBefore, creature.GetCreatureSkill(Skill.MeleeDefense).Current, "melee defense untouched");
            Assert.AreEqual(meleeBaseBefore, creature.GetCreatureSkill(Skill.MeleeDefense).Base);
            Assert.AreEqual(magicBefore, creature.GetCreatureSkill(Skill.MagicDefense).Current, "magic defense untouched");
            Assert.AreEqual(magicBaseBefore, creature.GetCreatureSkill(Skill.MagicDefense).Base);
            Assert.AreEqual(weaponBefore, creature.GetCreatureSkill(Skill.HeavyWeapons).Current, "a weapon skill untouched");
        }

        [TestMethod]
        public void A_creature_at_or_below_the_cap_is_unchanged()
        {
            TestGameTables.EnsureInitialized();

            var creature = NewCreature(defenseInit: 100);
            var baseBefore = creature.GetCreatureSkill(Skill.MissileDefense).Base;
            var currentBefore = creature.GetCreatureSkill(Skill.MissileDefense).Current;
            Assert.IsTrue(currentBefore < 720u, "positive control: the fixture sits below the cap");

            ThreadDungeonSpawner.ApplyMissileDefenseCeiling(creature, PlanWithCap(720));
            Assert.AreEqual(720u, creature.MissileDefenseSkillCeiling, "the ceiling is set, but it only lowers");
            Assert.AreEqual(baseBefore, creature.GetCreatureSkill(Skill.MissileDefense).Base);
            Assert.AreEqual(currentBefore, creature.GetCreatureSkill(Skill.MissileDefense).Current);

            creature.MissileDefenseSkillCeiling = currentBefore;
            Assert.AreEqual(currentBefore, creature.GetCreatureSkill(Skill.MissileDefense).Current, "at the cap stays put");
        }

        [TestMethod]
        public void The_effective_ceiling_is_the_lower_of_the_cap_and_an_existing_defense_ceiling()
        {
            TestGameTables.EnsureInitialized();

            // A defense-skill ceiling BELOW the cap wins.
            var lower = NewCreature(defenseInit: 900);
            lower.DefenseSkillCeilings = new Dictionary<Skill, uint> { [Skill.MissileDefense] = 600 };
            ThreadDungeonSpawner.ApplyMissileDefenseCeiling(lower, PlanWithCap(720));
            Assert.AreEqual(600u, lower.GetCreatureSkill(Skill.MissileDefense).Current, "Current: the lower defense ceiling wins");
            Assert.AreEqual(600u, lower.GetCreatureSkill(Skill.MissileDefense).Base, "Base: the lower defense ceiling wins");

            // A defense-skill ceiling ABOVE the cap loses to it.
            var higher = NewCreature(defenseInit: 900);
            higher.DefenseSkillCeilings = new Dictionary<Skill, uint> { [Skill.MissileDefense] = 800 };
            ThreadDungeonSpawner.ApplyMissileDefenseCeiling(higher, PlanWithCap(720));
            Assert.AreEqual(720u, higher.GetCreatureSkill(Skill.MissileDefense).Current, "Current: the cap wins");
            Assert.AreEqual(720u, higher.GetCreatureSkill(Skill.MissileDefense).Base, "Base: the cap wins");
        }

        [TestMethod]
        public void A_cap_of_zero_sets_no_ceiling_and_a_creature_outside_a_run_has_none()
        {
            TestGameTables.EnsureInitialized();

            var creature = NewCreature(defenseInit: 900);
            Assert.IsNull(creature.MissileDefenseSkillCeiling, "a creature nobody touched has no ceiling (World Events, Pinnacles, the open world)");

            var uncapped = creature.GetCreatureSkill(Skill.MissileDefense).Current;
            ThreadDungeonSpawner.ApplyMissileDefenseCeiling(creature, PlanWithCap(0));

            Assert.IsNull(creature.MissileDefenseSkillCeiling);
            Assert.AreEqual(uncapped, creature.GetCreatureSkill(Skill.MissileDefense).Current);

            ThreadDungeonSpawner.ApplyMissileDefenseCeiling(creature, null);
            Assert.IsNull(creature.MissileDefenseSkillCeiling, "a null plan is a no-op");
        }

        [TestMethod]
        public void An_enchantment_on_the_monster_still_lands_on_top_of_the_clamp()
        {
            TestGameTables.EnsureInitialized();

            var creature = NewCreature(defenseInit: 900);
            ThreadDungeonSpawner.ApplyMissileDefenseCeiling(creature, PlanWithCap(720));

            creature.Biota.PropertiesEnchantmentRegistry.AddEnchantment(new PropertiesEnchantmentRegistry
            {
                SpellId = 1,
                SpellCategory = SpellCategory.MissileDefenseRaising,
                LayerId = 1,
                PowerLevel = 100,
                StartTime = 0,
                Duration = -1,
                StatModType = EnchantmentTypeFlags.Skill | EnchantmentTypeFlags.Additive | EnchantmentTypeFlags.SingleStat,
                StatModKey = (uint)Skill.MissileDefense,
                StatModValue = -50f,
                EnchantmentCategory = (uint)SpellType.Enchantment,
            }, creature.BiotaDatabaseLock);

            Assert.AreEqual(-50, creature.EnchantmentManager.GetSkillMod_Additives(Skill.MissileDefense), "positive control: the debuff is read");
            Assert.AreEqual(670u, creature.GetCreatureSkill(Skill.MissileDefense).Current, "720 clamped, then the -50 debuff applied on top");
            Assert.AreEqual(720u, creature.GetCreatureSkill(Skill.MissileDefense).Base, "Base carries no enchantments");
        }

        [TestMethod]
        public void The_ceiling_sits_before_enchantments_and_never_applies_to_a_player()
        {
            // A Player cannot be constructed in this harness, so these guarantees are pinned as source text.
            var setter = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(Spawner),
                "internal static void ApplyMissileDefenseCeiling(Creature creature, DungeonSpawnPlan plan)");
            StringAssert.Contains(setter, "creature is Player");

            var skill = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Entity/CreatureSkill.cs");
            var calls = Regex.Matches(skill, @"ClampToDefenseCeiling\(total, creature\.GetMissileDefenseSkillCeiling\(Skill\)\)");
            Assert.AreEqual(2, calls.Count, "Base and Current both clamp");

            var multiplier = skill.IndexOf("GetSkillMod_Multiplier(Skill)", StringComparison.Ordinal);
            Assert.IsTrue(multiplier >= 0, "positive control: the enchantment multiplier is in the file");

            foreach (Match call in calls)
            {
                Assert.IsTrue(call.Index < multiplier, "each clamp precedes the enchantment multiplier, so a player debuff lands on top");
                Assert.IsTrue(skill.Substring(call.Index - 900, 900).Contains("creature != null"), "each clamp sits in a non-player creature branch");
            }
        }

        // ---- 3. the dial ----------------------------------------------------------------------------------

        [TestMethod]
        public void The_sanitizer_reads_garbage_as_the_default_and_negative_as_off()
        {
            Assert.AreEqual(720.0, DungeonCombatNormalizer.SanitizeMissileDefenseSkillCap(double.NaN));
            Assert.AreEqual(720.0, DungeonCombatNormalizer.SanitizeMissileDefenseSkillCap(double.PositiveInfinity));
            Assert.AreEqual(720.0, DungeonCombatNormalizer.SanitizeMissileDefenseSkillCap(double.NegativeInfinity));
            Assert.AreEqual(0.0, DungeonCombatNormalizer.SanitizeMissileDefenseSkillCap(-5));
            Assert.AreEqual(0.0, DungeonCombatNormalizer.SanitizeMissileDefenseSkillCap(0));
            Assert.AreEqual(800.0, DungeonCombatNormalizer.SanitizeMissileDefenseSkillCap(800), "positive control: a legal value passes through");
            Assert.AreEqual(721.0, DungeonCombatNormalizer.SanitizeMissileDefenseSkillCap(720.6), "rounded to a whole point");
            Assert.AreEqual(DungeonCombatNormalizer.MaxMissileDefenseSkillCap, DungeonCombatNormalizer.SanitizeMissileDefenseSkillCap(1e12));
        }

        [TestMethod]
        public void The_warning_predicate_ignores_legal_fractions_and_flags_out_of_range_values()
        {
            Assert.IsFalse(DungeonCombatNormalizer.MissileDefenseSkillCapNeedsWarning(720.4), "a legal fraction is rounded silently");
            Assert.IsFalse(DungeonCombatNormalizer.MissileDefenseSkillCapNeedsWarning(720));
            Assert.IsFalse(DungeonCombatNormalizer.MissileDefenseSkillCapNeedsWarning(0));
            Assert.IsTrue(DungeonCombatNormalizer.MissileDefenseSkillCapNeedsWarning(-1));
            Assert.IsTrue(DungeonCombatNormalizer.MissileDefenseSkillCapNeedsWarning(double.NaN));
            Assert.IsTrue(DungeonCombatNormalizer.MissileDefenseSkillCapNeedsWarning(double.PositiveInfinity));
            Assert.IsTrue(DungeonCombatNormalizer.MissileDefenseSkillCapNeedsWarning(DungeonCombatNormalizer.MaxMissileDefenseSkillCap + 1));
        }

        [TestMethod]
        public void ReadLimits_reads_and_sanitizes_the_live_dial()
        {
            DefaultPropertyManager.LoadDefaultProperties();

            try
            {
                Assert.IsTrue(PropertyManager.ModifyDouble(CapKey, DungeonPopulationLimits.DefaultMissileDefenseSkillCap), "key not registered");
                Assert.AreEqual(720.0, ThreadDungeonSpawner.ReadLimits(GroupScaling.Solo).MissileDefenseSkillCap, 1e-12, "shipped default");

                Assert.IsTrue(PropertyManager.ModifyDouble(CapKey, double.NaN));
                Assert.AreEqual(720.0, ThreadDungeonSpawner.ReadLimits(GroupScaling.Solo).MissileDefenseSkillCap, 1e-12, "NaN reads as the default");

                Assert.IsTrue(PropertyManager.ModifyDouble(CapKey, -3.0));
                Assert.AreEqual(0.0, ThreadDungeonSpawner.ReadLimits(GroupScaling.Solo).MissileDefenseSkillCap, 1e-12, "negative reads as off");

                Assert.IsTrue(PropertyManager.ModifyDouble(CapKey, 640.0));
                Assert.AreEqual(640.0, ThreadDungeonSpawner.ReadLimits(GroupScaling.Solo).MissileDefenseSkillCap, 1e-12, "a legal value passes through");
            }
            finally
            {
                PropertyManager.ModifyDouble(CapKey, DungeonPopulationLimits.DefaultMissileDefenseSkillCap);
            }
        }

        [TestMethod]
        public void The_plan_carries_the_sanitized_cap_and_a_default_struct_reads_off()
        {
            Assert.AreEqual(0.0, default(DungeonPopulationLimits).MissileDefenseSkillCap, "a default struct is off");
            Assert.AreEqual(0.0, new DungeonSpawnPlan().MissileDefenseSkillCap, "a hand-built plan carries no cap");
            Assert.AreEqual(720.0, new DungeonPopulationLimits(120, 2.0, 3.0, 8, 0.5).MissileDefenseSkillCap, "the constructor defaults to the shipped value");

            var source = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/DungeonPopulationBuilder.cs");
            StringAssert.Contains(source, "plan.MissileDefenseSkillCap = DungeonCombatNormalizer.SanitizeMissileDefenseSkillCap(limits.MissileDefenseSkillCap);");

            var spawner = PooledLootSourceText.Read(Spawner);
            var limits = PooledLootSourceText.MethodBody(spawner, "internal static DungeonPopulationLimits ReadLimits(GroupScaling group)");
            StringAssert.Contains(limits, "missileDefenseSkillCap: ReadMissileDefenseSkillCapDial()");

            var reader = PooledLootSourceText.MethodBody(spawner, "private static double ReadMissileDefenseSkillCapDial()");
            StringAssert.Contains(reader, "\"" + CapKey + "\"");
            StringAssert.Contains(reader, "DungeonCombatNormalizer.SanitizeMissileDefenseSkillCap(configured)");
            StringAssert.Contains(reader, "DungeonCombatNormalizer.MissileDefenseSkillCapNeedsWarning(configured)");
        }

        [TestMethod]
        public void Speed_courses_keep_the_cap_through_CourseLimits()
        {
            DefaultPropertyManager.LoadDefaultProperties();

            try
            {
                Assert.IsTrue(PropertyManager.ModifyDouble(CapKey, 640.0), "key not registered");
                var limits = ThreadDungeonSpawner.ReadLimits(GroupScaling.Solo);
                Assert.AreEqual(640.0, limits.MissileDefenseSkillCap, 1e-12, "positive control: the dial is read");

                var course = ACE.Server.SpeedCourses.SpeedCourseRules.CourseLimits(limits, 6);
                Assert.AreEqual(limits.MissileDefenseSkillCap, course.MissileDefenseSkillCap, 1e-12, "a speed course shares the dial");
            }
            finally
            {
                PropertyManager.ModifyDouble(CapKey, DungeonPopulationLimits.DefaultMissileDefenseSkillCap);
            }
        }

        // ---- 4. through PrepareForPlacement ---------------------------------------------------------------

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
        public void PrepareForPlacement_caps_a_trash_creature_and_leaves_it_alone_at_cap_zero()
        {
            WithSeededPrepareKeys(() =>
            {
                var entry = new DungeonSpawnPlanEntry(1, DungeonRole.Trash, null);
                var capped = NewCreature(defenseInit: 900);
                var off = NewCreature(defenseInit: 900);
                var uncapped = capped.GetCreatureSkill(Skill.MissileDefense).Current;
                Assert.IsTrue(uncapped > 720u, "positive control: above the cap before the step");

                ThreadDungeonSpawner.PrepareForPlacement(null, capped, entry, new DungeonSpawnPlan { MissileDefenseSkillCap = 720 }, observedMaxNonBoss: 2500);
                ThreadDungeonSpawner.PrepareForPlacement(null, off, entry, new DungeonSpawnPlan { MissileDefenseSkillCap = 0 }, observedMaxNonBoss: 2500);

                Assert.AreEqual(720u, capped.MissileDefenseSkillCeiling);
                Assert.AreEqual(720u, capped.GetCreatureSkill(Skill.MissileDefense).Current);

                Assert.IsNull(off.MissileDefenseSkillCeiling, "cap 0 sets no ceiling");
                Assert.AreEqual(uncapped, off.GetCreatureSkill(Skill.MissileDefense).Current, "cap 0 leaves the skill authored");
            });
        }

        [TestMethod]
        public void PrepareForPlacement_caps_a_boss_too()
        {
            WithSeededPrepareKeys(() =>
            {
                var boss = NewCreature(defenseInit: 900);
                Assert.IsTrue(boss.GetCreatureSkill(Skill.MissileDefense).Current > 720u, "positive control");

                var plan = new DungeonSpawnPlan { MissileDefenseSkillCap = 720 };
                ThreadDungeonSpawner.PrepareForPlacement(null, boss, new DungeonSpawnPlanEntry(1, DungeonRole.Boss, null), plan, observedMaxNonBoss: 2500);

                Assert.AreEqual(720u, boss.GetCreatureSkill(Skill.MissileDefense).Current);
            });
        }

        [TestMethod]
        public void The_hook_runs_after_every_defense_writer_and_before_the_damage_ceiling()
        {
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(Spawner), Signature);

            var hook = body.IndexOf("ApplyMissileDefenseCeiling(creature, plan);", StringComparison.Ordinal);
            Assert.IsTrue(hook >= 0, "PrepareForPlacement no longer sets the missile defense ceiling");
            Assert.AreEqual(hook, body.LastIndexOf("ApplyMissileDefenseCeiling(creature, plan);", StringComparison.Ordinal), "set exactly once");

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
        public void World_Events_Pinnacles_and_map_events_never_set_the_ceiling()
        {
            var root = "Source/ACE.Server/";

            foreach (var file in new[] { "Pinnacles/PinnacleScaling.cs", "MlDigsite/MlMapEventScaling.cs", "WorldEvents/WorldEventSpawner.cs", "WorldEvents/WorldEventUplift.cs" })
            {
                var text = PooledLootSourceText.Read(root + file);
                Assert.IsTrue(text.IndexOf("MissileDefenseSkillCeiling", StringComparison.Ordinal) < 0, file);
                Assert.IsTrue(text.IndexOf("ApplyMissileDefenseCeiling", StringComparison.Ordinal) < 0, file);
            }
        }
    }
}
