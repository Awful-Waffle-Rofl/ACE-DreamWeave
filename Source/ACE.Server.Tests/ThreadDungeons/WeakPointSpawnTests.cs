using System;
using System.Collections.Generic;
using System.Linq;

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
    /// The Threads weak point as <see cref="ThreadDungeonSpawner.PrepareForPlacement"/> applies it
    /// (<see cref="ThreadDungeonSpawner.ApplyWeakPoint"/>).
    ///
    /// Tests 1 and 2 are the shared-reference trap: a plain trash spawn reaches the weak-point step still holding the
    /// SAME PropertiesBodyPart dictionary as the cached weenie, so an in-place cut would re-cut every instance server-wide.
    /// Both open with a positive control (the sharing really is there before the step runs) so they cannot pass vacuously.
    /// Tests 3 and 4 pin the step's position: behaviourally against the writers that rewrite BaseArmor, and as source text.
    /// </summary>
    [TestClass]
    public class WeakPointSpawnTests
    {
        private const string Spawner = "Source/ACE.Server/ThreadDungeons/ThreadDungeonSpawner.cs";
        private const string Signature = "internal static void PrepareForPlacement(ThreadDungeonRun run, Creature creature, DungeonSpawnPlanEntry entry, DungeonSpawnPlan plan,";
        private const string CallStatement = "ApplyWeakPoint(logTag, creature, entry, plan);";
        private const double Cut = 0.4;

        private const uint TrashWcid = 0x7F0001A1;
        private const uint BossWcid = 0x7F0001A3;
        private static uint nextGuid = 0x7F0001A1;

        // ---- fixture ---------------------------------------------------------------------------------------

        /// <summary>Head and Chest hit high, Abdomen medium, UpperLeg low. Armour differs per part so a mixed-up part shows.</summary>
        private static Dictionary<CombatBodyPart, PropertiesBodyPart> Parts(int head, int chest, int abdomen, int upperLeg)
        {
            PropertiesBodyPart P(int armor, float h, float m, float l) => new PropertiesBodyPart
            {
                DType = DamageType.Slash, DVal = 40, DVar = 0.4f, BaseArmor = armor,
                ArmorVsSlash = armor, ArmorVsPierce = armor, ArmorVsBludgeon = armor, ArmorVsCold = armor, ArmorVsFire = armor,
                ArmorVsAcid = armor, ArmorVsElectric = armor, ArmorVsNether = armor,
                HLF = h, HRF = h, HLB = h, HRB = h, MLF = m, MRF = m, MLB = m, MRB = m, LLF = l, LRF = l, LLB = l, LRB = l,
            };

            return new Dictionary<CombatBodyPart, PropertiesBodyPart>
            {
                [CombatBodyPart.Head] = P(head, 0.1f, 0, 0),
                [CombatBodyPart.Chest] = P(chest, 0.1f, 0.01f, 0),
                [CombatBodyPart.Abdomen] = P(abdomen, 0, 0.1f, 0),
                [CombatBodyPart.UpperLeg] = P(upperLeg, 0, 0.01f, 0.1f),
            };
        }

        private static Weenie MakeWeenie(uint wcid, int level, Dictionary<CombatBodyPart, PropertiesBodyPart> parts) => new Weenie
        {
            WeenieClassId = wcid,
            ClassName = $"weakpoint{wcid:X}",
            WeenieType = WeenieType.Creature,
            PropertiesInt = new Dictionary<PropertyInt, int> { [PropertyInt.Level] = level, [PropertyInt.XpOverride] = 1000 + level },
            PropertiesBool = new Dictionary<PropertyBool, bool> { [PropertyBool.NoCorpse] = true },
            PropertiesString = new Dictionary<PropertyString, string> { [PropertyString.Name] = $"Weak {wcid:X}" },
            PropertiesFloat = new Dictionary<PropertyFloat, double> { [PropertyFloat.HealthRate] = 0.5 },
            PropertiesAttribute = new Dictionary<PropertyAttribute, PropertiesAttribute>
            {
                [PropertyAttribute.Strength] = new PropertiesAttribute { InitLevel = 150 },
                [PropertyAttribute.Endurance] = new PropertiesAttribute { InitLevel = 140 },
                [PropertyAttribute.Coordination] = new PropertiesAttribute { InitLevel = 150 },
                [PropertyAttribute.Quickness] = new PropertiesAttribute { InitLevel = 150 },
                [PropertyAttribute.Focus] = new PropertiesAttribute { InitLevel = 140 },
                [PropertyAttribute.Self] = new PropertiesAttribute { InitLevel = 140 },
            },
            PropertiesAttribute2nd = new Dictionary<PropertyAttribute2nd, PropertiesAttribute2nd>
            {
                [PropertyAttribute2nd.MaxHealth] = new PropertiesAttribute2nd { InitLevel = 400 },
                [PropertyAttribute2nd.MaxStamina] = new PropertiesAttribute2nd { InitLevel = 200 },
                [PropertyAttribute2nd.MaxMana] = new PropertiesAttribute2nd { InitLevel = 200 },
            },
            PropertiesSkill = new Dictionary<Skill, PropertiesSkill>
            {
                [Skill.HeavyWeapons] = new PropertiesSkill { InitLevel = 150, SAC = SkillAdvancementClass.Trained },
                [Skill.MeleeDefense] = new PropertiesSkill { InitLevel = 140, SAC = SkillAdvancementClass.Trained },
                [Skill.MissileDefense] = new PropertiesSkill { InitLevel = 140, SAC = SkillAdvancementClass.Trained },
                [Skill.MagicDefense] = new PropertiesSkill { InitLevel = 140, SAC = SkillAdvancementClass.Trained },
                [Skill.Run] = new PropertiesSkill { InitLevel = 100, SAC = SkillAdvancementClass.Trained },
            },
            PropertiesBodyPart = parts,
            PropertiesEmote = new List<PropertiesEmote> { new PropertiesEmote { Category = EmoteCategory.Death, Probability = 1.0f } },
        };

        private static Creature NewCreature(Weenie weenie) => new Creature(weenie, new ObjectGuid(nextGuid++));

        private static DungeonSpawnPlan MakePlan(bool bossNormalize, WeakPointPlan weak)
        {
            var standard = DungeonBandStandard.ForTest(
                new Dictionary<Skill, uint> { [Skill.HeavyWeapons] = 300, [Skill.MeleeDefense] = 280, [Skill.MissileDefense] = 260, [Skill.MagicDefense] = 240, [Skill.Run] = 150 },
                maxBodyDamage: 90, maxBaseArmor: 400, spellTier: 0, healthRate: 2.0,
                attributeMedians: new Dictionary<PropertyAttribute, uint>
                {
                    [PropertyAttribute.Strength] = 250, [PropertyAttribute.Endurance] = 240, [PropertyAttribute.Coordination] = 230,
                    [PropertyAttribute.Quickness] = 220, [PropertyAttribute.Focus] = 210, [PropertyAttribute.Self] = 200,
                },
                effectiveDefenseMedians: new Dictionary<Skill, uint> { [Skill.MeleeDefense] = 500, [Skill.MissileDefense] = 470, [Skill.MagicDefense] = 440 },
                effectiveSkillMedians: new Dictionary<Skill, uint> { [Skill.HeavyWeapons] = 560 });

            var plan = new DungeonSpawnPlan
            {
                BossWcid = BossWcid,
                FamilyId = "weakfamily",
                WeakPoint = weak,
                HealthMultiplier = 1.5,
                BossHealthMultiplier = 2.0,
                TrashHealthFloor = 1200,
                HealthNormalizeRatio = 1.25,
                BandStandard = standard,
                UpliftRename = true,
                NaturalBandLow = 220,
                EffectiveBandLow = 150,
                DamageRating = 12,
                CritRating = 3,
                CritDamageRating = 4,
                DamageResistRating = 6,
                BossDamageRating = 30,
                BossDamageResistRating = 20,
                BossLevel = 260,
                BossHealthFloorRatio = 4.0,
                RunSpeedMult = 1.2,
                IgnoreShield = 0.25,
                HollowIntensity = 0.5,
                XpMultiplier = 1.5,
                BossXpMultiplier = 2.0,
                LumMultiplier = 1.0,
                XpScale = 1.0,
                LumScale = 1.0,
                StripCombatTraits = true,
                CategoryResistFloor = 0.5,
                CategoryArmorModCeiling = 2.0,
                DefenseSkillCapOffset = 50,
                BossNormalize = bossNormalize,
            };

            plan.Entries.Add(new DungeonSpawnPlanEntry(TrashWcid, DungeonRole.Trash, null));
            plan.Entries.Add(new DungeonSpawnPlanEntry(TrashWcid, DungeonRole.Trash, null, upliftLevel: 250));
            plan.Entries.Add(new DungeonSpawnPlanEntry(BossWcid, DungeonRole.Boss, null));
            return plan;
        }

        private static DungeonSpawnPlanEntry TrashEntry(DungeonSpawnPlan plan) => plan.Entries[0];
        private static DungeonSpawnPlanEntry UpliftedEntry(DungeonSpawnPlan plan) => plan.Entries[1];
        private static DungeonSpawnPlanEntry BossEntry(DungeonSpawnPlan plan) => plan.Entries[2];

        /// <summary>Seeds the three PropertyManager keys PrepareForPlacement reads, runs the body, restores them.</summary>
        private static void WithSeededKeys(Action body)
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

        private static void Prepare(Creature creature, DungeonSpawnPlanEntry entry, DungeonSpawnPlan plan)
            => ThreadDungeonSpawner.PrepareForPlacement(null, creature, entry, plan, observedMaxNonBoss: 2500);

        private static WeakPointPlan HighWeak() => new WeakPointPlan { TrashHeight = AttackHeight.High, BossHeight = AttackHeight.Medium, ArmorCut = Cut };

        private static Dictionary<CombatBodyPart, int> Armor(Creature c) => c.Biota.PropertiesBodyPart.ToDictionary(kv => kv.Key, kv => kv.Value.BaseArmor);

        // ---- 1. the cached weenie is untouched ---------------------------------------------------------------

        [TestMethod]
        public void The_cached_weenie_is_untouched_and_the_creature_gets_its_own_parts()
        {
            WithSeededKeys(() =>
            {
                var weenie = MakeWeenie(TrashWcid, 150, Parts(300, 400, 200, 250));
                var armorBefore = weenie.PropertiesBodyPart.ToDictionary(kv => kv.Key, kv => kv.Value.BaseArmor);
                var partsBefore = weenie.PropertiesBodyPart.ToDictionary(kv => kv.Key, kv => kv.Value);
                var c1 = NewCreature(weenie);
                var c2 = NewCreature(weenie);
                var plan = MakePlan(bossNormalize: false, HighWeak());

                // POSITIVE CONTROL: before the step runs, the creature really does share the weenie's dictionary.
                Assert.AreSame(weenie.PropertiesBodyPart, c1.Biota.PropertiesBodyPart, "positive control: the trap exists");
                Assert.AreSame(weenie.PropertiesBodyPart, c2.Biota.PropertiesBodyPart, "positive control: the trap exists");

                Prepare(c1, TrashEntry(plan), plan);

                Assert.AreNotSame(weenie.PropertiesBodyPart, c1.Biota.PropertiesBodyPart);

                foreach (var kv in partsBefore)
                {
                    Assert.AreNotSame(kv.Value, c1.Biota.PropertiesBodyPart[kv.Key], $"{kv.Key} must be a clone");
                    Assert.AreSame(kv.Value, weenie.PropertiesBodyPart[kv.Key], $"{kv.Key}: the weenie keeps its own part object");
                    Assert.AreEqual(armorBefore[kv.Key], weenie.PropertiesBodyPart[kv.Key].BaseArmor, $"{kv.Key}: the weenie's armour must be unchanged");
                }

                // The cut itself landed on the high parts, so the control above is not a no-op run.
                Assert.AreEqual(180, c1.Biota.PropertiesBodyPart[CombatBodyPart.Head].BaseArmor);
                Assert.AreEqual(240, c1.Biota.PropertiesBodyPart[CombatBodyPart.Chest].BaseArmor);
                Assert.AreEqual(200, c1.Biota.PropertiesBodyPart[CombatBodyPart.Abdomen].BaseArmor);
                Assert.AreEqual(250, c1.Biota.PropertiesBodyPart[CombatBodyPart.UpperLeg].BaseArmor);

                // A second spawn from the same weenie starts from the AUTHORED armour (no geometric decay).
                Prepare(c2, TrashEntry(plan), plan);
                CollectionAssert.AreEqual(Armor(c1).OrderBy(k => (int)k.Key).ToList(), Armor(c2).OrderBy(k => (int)k.Key).ToList());
            });
        }

        // ---- 2. two instances do not share parts -------------------------------------------------------------

        [TestMethod]
        public void Two_instances_do_not_share_parts()
        {
            WithSeededKeys(() =>
            {
                var weenie = MakeWeenie(TrashWcid, 150, Parts(300, 400, 200, 250));
                var c1 = NewCreature(weenie);
                var c2 = NewCreature(weenie);
                var plan = MakePlan(bossNormalize: false, HighWeak());

                Assert.AreSame(c1.Biota.PropertiesBodyPart, c2.Biota.PropertiesBodyPart, "positive control: before preparation both share one dictionary");

                Prepare(c1, TrashEntry(plan), plan);
                Prepare(c2, TrashEntry(plan), plan);

                Assert.AreNotSame(c1.Biota.PropertiesBodyPart, c2.Biota.PropertiesBodyPart);

                foreach (var key in c1.Biota.PropertiesBodyPart.Keys)
                    Assert.AreNotSame(c1.Biota.PropertiesBodyPart[key], c2.Biota.PropertiesBodyPart[key], $"{key}");

                var c2Before = Armor(c2);
                var weenieBefore = weenie.PropertiesBodyPart.ToDictionary(kv => kv.Key, kv => kv.Value.BaseArmor);

                foreach (var part in c1.Biota.PropertiesBodyPart.Values)
                    part.BaseArmor += 1000;

                CollectionAssert.AreEqual(c2Before.OrderBy(k => (int)k.Key).ToList(), Armor(c2).OrderBy(k => (int)k.Key).ToList(), "mutating c1 changed c2");
                CollectionAssert.AreEqual(weenieBefore.OrderBy(k => (int)k.Key).ToList(),
                    weenie.PropertiesBodyPart.ToDictionary(kv => kv.Key, kv => kv.Value.BaseArmor).OrderBy(k => (int)k.Key).ToList(), "mutating c1 changed the weenie");
            });
        }

        // ---- 3. ordering, behavioural ------------------------------------------------------------------------

        [TestMethod]
        public void A_boss_is_cut_from_its_FINAL_normalized_armour_and_uses_the_boss_height()
        {
            WithSeededKeys(() =>
            {
                var weenie = MakeWeenie(BossWcid, 240, Parts(100, 200, 150, 120));
                var plainPlan = MakePlan(bossNormalize: true, null);
                var weakPlan = MakePlan(bossNormalize: true, HighWeak());

                var plain = NewCreature(weenie);
                var weak = NewCreature(weenie);

                Prepare(plain, BossEntry(plainPlan), plainPlan);
                Prepare(weak, BossEntry(weakPlan), weakPlan);

                // What SetBossBodyParts alone produces, computed on an independent clone of the authored biota.
                var expectedBiota = new ACE.Entity.Models.Biota { PropertiesBodyPart = Parts(100, 200, 150, 120) };
                var changed = DungeonCombatNormalizer.SetBossBodyParts(expectedBiota, weakPlan.BandStandard.MaxBodyDamage, weakPlan.BandStandard.MaxBaseArmor,
                    weakPlan.BossOffenseBandRatio, weakPlan.BossDefenseBandRatio);

                Assert.IsTrue(changed > 0, "positive control: the boss writer moves armour for this fixture");
                Assert.AreNotEqual(100, expectedBiota.PropertiesBodyPart[CombatBodyPart.Head].BaseArmor, "positive control: the authored head armour is not already the target");

                // The plain boss ends exactly on the writer's values, so the weak boss differs from it only by the cut.
                foreach (var kv in expectedBiota.PropertiesBodyPart)
                    Assert.AreEqual(kv.Value.BaseArmor, plain.Biota.PropertiesBodyPart[kv.Key].BaseArmor, $"{kv.Key}: plain boss");

                // The boss height is Medium (TrashHeight is High): only the abdomen is cut, from the NORMALIZED value.
                foreach (var kv in expectedBiota.PropertiesBodyPart)
                {
                    var expected = kv.Key == CombatBodyPart.Abdomen ? DungeonWeakPoint.CutArmor(kv.Value.BaseArmor, Cut) : kv.Value.BaseArmor;

                    Assert.AreEqual(expected, weak.Biota.PropertiesBodyPart[kv.Key].BaseArmor, $"{kv.Key}: weak boss");
                }

                Assert.AreNotEqual(plain.Biota.PropertiesBodyPart[CombatBodyPart.Abdomen].BaseArmor, weak.Biota.PropertiesBodyPart[CombatBodyPart.Abdomen].BaseArmor, "the cut changed something");
            });
        }

        [TestMethod]
        public void An_uplifted_trash_creature_is_cut_from_its_FINAL_uplifted_armour()
        {
            WithSeededKeys(() =>
            {
                var weenie = MakeWeenie(TrashWcid, 150, Parts(120, 240, 120, 120));
                var plainPlan = MakePlan(bossNormalize: false, null);
                var weakPlan = MakePlan(bossNormalize: false, HighWeak());

                var plain = NewCreature(weenie);
                var weak = NewCreature(weenie);

                Prepare(plain, UpliftedEntry(plainPlan), plainPlan);
                Prepare(weak, UpliftedEntry(weakPlan), weakPlan);

                // Positive control: the band uplift is what rewrote this armour (the authored head armour was 120).
                Assert.IsTrue(plain.Biota.PropertiesBodyPart[CombatBodyPart.Head].BaseArmor > 120, "positive control: the uplift raised the head armour");

                foreach (var kv in plain.Biota.PropertiesBodyPart)
                {
                    var expected = kv.Key == CombatBodyPart.Head || kv.Key == CombatBodyPart.Chest ? DungeonWeakPoint.CutArmor(kv.Value.BaseArmor, Cut) : kv.Value.BaseArmor;

                    Assert.AreEqual(expected, weak.Biota.PropertiesBodyPart[kv.Key].BaseArmor, $"{kv.Key}");
                }
            });
        }

        // ---- 4. ordering, structural -------------------------------------------------------------------------

        [TestMethod]
        public void The_weak_point_call_is_the_last_statement_of_PrepareForPlacement_and_appears_once()
        {
            var source = PooledLootSourceText.Read(Spawner);
            var occurrences = 0;

            for (var at = source.IndexOf(CallStatement, StringComparison.Ordinal); at >= 0; at = source.IndexOf(CallStatement, at + 1, StringComparison.Ordinal))
                occurrences++;

            Assert.AreEqual(1, occurrences, $"'{CallStatement}' must appear exactly once in {Spawner}");

            var body = PooledLootSourceText.MethodBody(source, Signature);
            var call = body.IndexOf(CallStatement, StringComparison.Ordinal);

            Assert.IsTrue(call >= 0, "the call must be inside PrepareForPlacement");

            foreach (var writer in new[] { "DungeonCreatureNormalizer.NormalizeBoss(creature, plan)", "ArchetypeUplift.Apply(creature", "ApplyUplift(logTag, creature", "ReachUpStamp.Apply(creature" })
            {
                var at = body.IndexOf(writer, StringComparison.Ordinal);

                Assert.IsTrue(at >= 0, $"PrepareForPlacement no longer contains: {writer}");
                Assert.IsTrue(at < call, $"'{writer}' must come before the weak point");
            }

            var tail = body.Substring(call + CallStatement.Length);

            Assert.AreEqual("}", tail.Trim(), "nothing may follow the weak point in PrepareForPlacement");

            foreach (var token in new[] { "PropertiesBodyPart", "NormalizeBoss", "ApplyBodyPartUplift", "ScaleBodyParts", "SetBossBodyParts", "BandUplift.Apply" })
                Assert.IsFalse(tail.Contains(token), $"'{token}' must not follow the weak point");
        }

        // ---- 6. no weak point on the plan --------------------------------------------------------------------

        [TestMethod]
        public void A_plan_without_a_weak_point_leaves_the_biota_alone()
        {
            WithSeededKeys(() =>
            {
                var weenie = MakeWeenie(TrashWcid, 150, Parts(300, 400, 200, 250));
                var c = NewCreature(weenie);
                var plan = MakePlan(bossNormalize: false, null);

                Prepare(c, TrashEntry(plan), plan);

                Assert.AreSame(weenie.PropertiesBodyPart, c.Biota.PropertiesBodyPart, "no writer fires for a plain trash spawn, so nothing clones");
                Assert.AreEqual(300, c.Biota.PropertiesBodyPart[CombatBodyPart.Head].BaseArmor);
                Assert.AreEqual(400, c.Biota.PropertiesBodyPart[CombatBodyPart.Chest].BaseArmor);
            });
        }
    }
}
