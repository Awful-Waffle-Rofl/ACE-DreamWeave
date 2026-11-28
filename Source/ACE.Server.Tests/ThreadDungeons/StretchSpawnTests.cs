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
    /// The spawner side of the Thread family stretch (Task 11): a stretched pick is normalized to the standard at its MAPPED
    /// level (plan.Stretch), not the run's; the family overscale multiplies its health and damage (damage BEFORE the damage
    /// ceiling) and its XP, never its luminance; the boss is never stretched or overscaled; the plan log line names the
    /// stretch.
    ///
    /// The behaviour tests drive PrepareForPlacement on LIVE creatures built from hand-made weenies and compare a stretched
    /// entry with a native uplift entry at the SAME uplift level, so every difference is the stretch's. The source pins cover
    /// what a behaviour test cannot see (the log line, call order inside the method) and are exact-line matches.
    ///
    /// PropertyManager: the PrepareForPlacement tests seed and restore the three keys it reads (the WeakPointSpawnTests set)
    /// in a finally block. Every other test reads no key.
    /// </summary>
    [TestClass]
    public class StretchSpawnTests
    {
        private const string Spawner = "Source/ACE.Server/ThreadDungeons/ThreadDungeonSpawner.cs";
        private const string Signature = "internal static void PrepareForPlacement(ThreadDungeonRun run, Creature creature, DungeonSpawnPlanEntry entry, DungeonSpawnPlan plan,";

        private const int L = 220;
        private const int M = 250;
        private const double Overscale = 1.25;
        private const uint TrashWcid = 0x7F0E0001;
        private const uint BossWcid = 0x7F0E0002;
        private const int AuthoredXp = 1000001;

        private static uint nextGuid = 0x7F0E1001;

        // ---- fixture ---------------------------------------------------------------------------------------

        private static Weenie MakeWeenie(uint wcid, int level) => new Weenie
        {
            WeenieClassId = wcid,
            ClassName = $"stretch{wcid:X}",
            WeenieType = WeenieType.Creature,
            PropertiesInt = new Dictionary<PropertyInt, int> { [PropertyInt.Level] = level, [PropertyInt.XpOverride] = AuthoredXp },
            PropertiesString = new Dictionary<PropertyString, string> { [PropertyString.Name] = $"Stretch {wcid:X}" },
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
            PropertiesBodyPart = new Dictionary<CombatBodyPart, PropertiesBodyPart>
            {
                [CombatBodyPart.Head] = Part(40, 400),
                [CombatBodyPart.Hand] = Part(20, 200),
            },
        };

        private static PropertiesBodyPart Part(int dval, int armor) => new PropertiesBodyPart
        {
            DType = DamageType.Slash, DVal = dval, DVar = 0.4f, BaseArmor = armor,
            ArmorVsSlash = armor, ArmorVsPierce = armor, ArmorVsBludgeon = armor, ArmorVsCold = armor, ArmorVsFire = armor,
            ArmorVsAcid = armor, ArmorVsElectric = armor, ArmorVsNether = armor,
            HLF = 0.1f, HRF = 0.1f, MLF = 0.1f, MRF = 0.1f, LLF = 0.1f, LRF = 0.1f,
        };

        private static Creature NewCreature(Weenie weenie) => new Creature(weenie, new ObjectGuid(nextGuid++));

        /// <summary>The run's standard at L.</summary>
        private static DungeonBandStandard StandardL() => DungeonBandStandard.ForTest(
            new Dictionary<Skill, uint> { [Skill.HeavyWeapons] = 300, [Skill.MeleeDefense] = 280, [Skill.MissileDefense] = 260, [Skill.MagicDefense] = 240, [Skill.Run] = 150 },
            maxBodyDamage: 90, maxBaseArmor: 400, healthRate: 2.0,
            attributeMedians: Attributes(250, 240, 230, 220, 210, 200),
            effectiveDefenseMedians: new Dictionary<Skill, uint> { [Skill.MeleeDefense] = 500, [Skill.MissileDefense] = 470, [Skill.MagicDefense] = 440 },
            effectiveSkillMedians: new Dictionary<Skill, uint> { [Skill.HeavyWeapons] = 560 });

        /// <summary>The standard at the mapped level M: higher on every axis, so a site that reads the wrong one shows.</summary>
        private static DungeonBandStandard StandardM() => DungeonBandStandard.ForTest(
            new Dictionary<Skill, uint> { [Skill.HeavyWeapons] = 350, [Skill.MeleeDefense] = 330, [Skill.MissileDefense] = 310, [Skill.MagicDefense] = 290, [Skill.Run] = 170 },
            maxBodyDamage: 120, maxBaseArmor: 500, healthRate: 2.5,
            attributeMedians: Attributes(300, 260, 280, 270, 260, 250),
            effectiveDefenseMedians: new Dictionary<Skill, uint> { [Skill.MeleeDefense] = 600, [Skill.MissileDefense] = 570, [Skill.MagicDefense] = 540 },
            effectiveSkillMedians: new Dictionary<Skill, uint> { [Skill.HeavyWeapons] = 660 });

        private static Dictionary<PropertyAttribute, uint> Attributes(uint str, uint end, uint coo, uint qui, uint foc, uint self) => new Dictionary<PropertyAttribute, uint>
        {
            [PropertyAttribute.Strength] = str, [PropertyAttribute.Endurance] = end, [PropertyAttribute.Coordination] = coo,
            [PropertyAttribute.Quickness] = qui, [PropertyAttribute.Focus] = foc, [PropertyAttribute.Self] = self,
        };

        private static StretchPlan Stretch(DungeonBandStandard atM, double overscale = Overscale, double healthAtM = 3000.4)
            => new StretchPlan("stretchfam", L, 253, L, M, 6, overscale,
                new Dictionary<int, DungeonBandStandard> { [M] = atM }, new Dictionary<int, double> { [M] = healthAtM });

        private static DungeonSpawnPlan MakePlan(bool stretch)
        {
            var plan = new DungeonSpawnPlan
            {
                BossWcid = BossWcid,
                FamilyId = "stretchfam",
                HealthMultiplier = 1.5,
                BossHealthMultiplier = 2.0,
                TrashHealthFloor = 1200,
                HealthNormalizeRatio = 1.25,
                BandStandard = StandardL(),
                NaturalBandLow = L,
                EffectiveBandLow = L,
                DamageRating = 12,
                BossLevel = 260,
                BossHealthFloorRatio = 4.0,
                XpMultiplier = 1.5,
                BossXpMultiplier = 2.0,
                LumMultiplier = 1.0,
                XpScale = 1.0,
                LumScale = 1.0,
                StripCombatTraits = true,
                CategoryResistFloor = 0.5,
                CategoryArmorModCeiling = 2.0,
                DefenseSkillCapOffset = 50,
                Stretch = stretch ? Stretch(StandardM()) : null,
            };

            plan.Entries.Add(StretchedEntry());
            plan.Entries.Add(NativeEntry());
            plan.Entries.Add(new DungeonSpawnPlanEntry(BossWcid, DungeonRole.Boss, null));
            return plan;
        }

        private static DungeonSpawnPlanEntry StretchedEntry() => new DungeonSpawnPlanEntry(TrashWcid, DungeonRole.Trash, null, M, 0, true);
        private static DungeonSpawnPlanEntry NativeEntry() => new DungeonSpawnPlanEntry(TrashWcid, DungeonRole.Trash, null, M);
        private static DungeonSpawnPlanEntry BossEntry() => new DungeonSpawnPlanEntry(BossWcid, DungeonRole.Boss, null);

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

        private static uint Attribute(Creature c, PropertyAttribute a) => c.Attributes[a].Base;

        // ---- 1. pure helpers -------------------------------------------------------------------------------

        [TestMethod]
        public void UpliftStandardFor_selects_the_mapped_standard()
        {
            var s423 = StandardM();
            var plan = new DungeonSpawnPlan
            {
                BandStandard = StandardL(),
                Stretch = new StretchPlan("fam", 400, 460, 400, 460, 6, 1.0,
                    new Dictionary<int, DungeonBandStandard> { [423] = s423 }, new Dictionary<int, double>()),
            };

            Assert.AreSame(s423, ThreadDungeonSpawner.UpliftStandardFor(new DungeonSpawnPlanEntry(1, DungeonRole.Trash, null, 423, 0, true), plan), "stretched at 423");
            Assert.AreSame(plan.BandStandard, ThreadDungeonSpawner.UpliftStandardFor(new DungeonSpawnPlanEntry(1, DungeonRole.Trash, null, 423), plan), "a native uplift reads the run's");
            Assert.AreSame(plan.BandStandard, ThreadDungeonSpawner.UpliftStandardFor(new DungeonSpawnPlanEntry(1, DungeonRole.Trash, null, 999, 0, true), plan), "no standard at 999: the run's");

            plan.Stretch = null;
            Assert.AreSame(plan.BandStandard, ThreadDungeonSpawner.UpliftStandardFor(new DungeonSpawnPlanEntry(1, DungeonRole.Trash, null, 423, 0, true), plan), "no stretch plan: the run's");
        }

        [TestMethod]
        public void OverscaleFor_is_the_family_overscale_for_stretched_entries_only()
        {
            var plan = new DungeonSpawnPlan { Stretch = Stretch(StandardM()) };

            Assert.AreEqual(Overscale, ThreadDungeonSpawner.OverscaleFor(StretchedEntry(), plan));
            Assert.AreEqual(1.0, ThreadDungeonSpawner.OverscaleFor(NativeEntry(), plan), "a native uplift");
            Assert.AreEqual(1.0, ThreadDungeonSpawner.OverscaleFor(BossEntry(), plan), "the boss is never overscaled");
            Assert.AreEqual(1.0, ThreadDungeonSpawner.OverscaleFor(StretchedEntry(), new DungeonSpawnPlan()), "no stretch plan");
            Assert.AreEqual(1.0, ThreadDungeonSpawner.OverscaleFor(StretchedEntry(), new DungeonSpawnPlan { Stretch = Stretch(StandardM(), double.NaN) }), "NaN reads as none");
            Assert.AreEqual(1.0, ThreadDungeonSpawner.OverscaleFor(StretchedEntry(), new DungeonSpawnPlan { Stretch = Stretch(StandardM(), 0.5) }), "below 1 reads as none");
        }

        [TestMethod]
        public void NonBossHealthTerms_floor_and_overscale()
        {
            var plan = new DungeonSpawnPlan
            {
                TrashHealthFloor = 1000,
                Stretch = new StretchPlan("fam", 400, 460, 400, 460, 6, 1.25,
                    new Dictionary<int, DungeonBandStandard>(), new Dictionary<int, double> { [423] = 5000.4 }),
            };

            Assert.AreEqual((2.5, 5000u), ThreadDungeonSpawner.NonBossHealthTerms(new DungeonSpawnPlanEntry(1, DungeonRole.Trash, null, 423, 0, true), plan, 2.0));
            Assert.AreEqual((2.0, 1000u), ThreadDungeonSpawner.NonBossHealthTerms(new DungeonSpawnPlanEntry(1, DungeonRole.Trash, null, 423), plan, 2.0));
            Assert.AreEqual((2.0, 1000u), ThreadDungeonSpawner.NonBossHealthTerms(new DungeonSpawnPlanEntry(1, DungeonRole.Trash, null), plan, 2.0));
        }

        [TestMethod]
        public void OverscaledBaseXp()
        {
            var plan = new DungeonSpawnPlan { Stretch = Stretch(StandardM()) };

            Assert.AreEqual(1250L, ThreadDungeonSpawner.OverscaledBaseXp(1000, StretchedEntry(), plan));
            Assert.AreEqual(1000L, ThreadDungeonSpawner.OverscaledBaseXp(1000, NativeEntry(), plan));
            Assert.AreEqual(long.MaxValue, ThreadDungeonSpawner.OverscaledBaseXp(long.MaxValue, StretchedEntry(), plan));
            Assert.AreEqual(long.MaxValue - 1, ThreadDungeonSpawner.OverscaledBaseXp(long.MaxValue - 1, NativeEntry(), plan), "native is returned exactly, never through a double");
            Assert.AreEqual(1250001L, ThreadDungeonSpawner.OverscaledBaseXp(1000001, StretchedEntry(), plan), "1250001.25 rounds to 1250001");
        }

        [TestMethod]
        public void StretchLogSegment_text()
        {
            StretchPlan P(int n, double x) => new StretchPlan("fam", 400, 460, 400, 460, n, x, new Dictionary<int, DungeonBandStandard>(), new Dictionary<int, double>());

            Assert.AreEqual("stretch=no", ThreadDungeonSpawner.StretchLogSegment(new DungeonSpawnPlan()));
            Assert.AreEqual("stretch=yes 400-460 n6 x1", ThreadDungeonSpawner.StretchLogSegment(new DungeonSpawnPlan { Stretch = P(6, 1.0) }));
            Assert.AreEqual("stretch=yes 400-460 n5 x1.25", ThreadDungeonSpawner.StretchLogSegment(new DungeonSpawnPlan { Stretch = P(5, 1.25) }));
        }

        [TestMethod]
        public void DefenseCapStandard_routes_a_stretched_entry_to_its_mapped_standard()
        {
            var plan = new DungeonSpawnPlan { BandStandard = StandardL(), Stretch = Stretch(StandardM()) };

            Assert.AreSame(plan.Stretch.StandardAt(M), ThreadDungeonSpawner.DefenseCapStandard(StretchedEntry(), plan));
            Assert.IsNull(ThreadDungeonSpawner.DefenseCapStandard(NativeEntry(), plan), "native: null falls back to plan.BandStandard");
            Assert.IsNull(ThreadDungeonSpawner.DefenseCapStandard(BossEntry(), plan), "the boss reads the run's");
            Assert.IsNull(ThreadDungeonSpawner.DefenseCapStandard(new DungeonSpawnPlanEntry(TrashWcid, DungeonRole.Trash, null, 999, 0, true), plan),
                "no standard at the mapped level: the run's");
        }

        [TestMethod]
        public void ArchetypeInfoFor_routes_a_stretched_entry_through_ForAt()
        {
            TestGameTables.EnsureInitialized();

            var tables = ShippedRosterFixture.Tables;
            Assert.IsNotNull(tables, "Content/events/axes/species not found from the test output directory");

            const int runLevel = 400;
            const int mapped = 460;
            var limits = new DungeonPopulationLimits(60, 3.0, 3.0, 8, 0.5, archetypeUplift: true, statCurve: true);
            var ctx = DungeonPopulationBuilder.BuildArchetypeContext(FamilyStretchFixture.Spec(runLevel), tables, limits, null, FamilyStretchFixture.LevelProfile);
            Assert.IsTrue(ctx.CurvePivot > 0, "positive control: the curve is on, so ForAt carries");

            var w = tables["drudge"].Members.Where(m => m.Role == 0).OrderByDescending(m => m.Level).ThenBy(m => m.Wcid)
                .Select(m => m.Wcid).FirstOrDefault(x => ctx.For(x) != null);
            Assert.AreNotEqual(0u, w, "no drudge wcid resolves an archetype uplift info");

            var plan = new DungeonSpawnPlan { ArchetypeUplift = ctx };
            var stretched = ThreadDungeonSpawner.ArchetypeInfoFor(new DungeonSpawnPlanEntry(w, DungeonRole.Trash, null, mapped, 0, true), plan);
            var native = ThreadDungeonSpawner.ArchetypeInfoFor(new DungeonSpawnPlanEntry(w, DungeonRole.Trash, null, mapped), plan);

            Assert.AreSame(ctx.ForAt(w, mapped), stretched, "a stretched entry gets the info carried to its mapped level");
            Assert.AreEqual(mapped, stretched.RunLevel);
            Assert.AreSame(ctx.For(w), native, "a native uplift keeps the run-level info");
            Assert.AreNotSame(native, stretched, "positive control: the two differ");
            Assert.IsNull(ThreadDungeonSpawner.ArchetypeInfoFor(new DungeonSpawnPlanEntry(w, DungeonRole.Boss, null, mapped), plan), "never the boss");
        }

        // ---- 2. ReachUpStamp.ScaleDamage -----------------------------------------------------------------------

        [TestMethod]
        public void ScaleDamage_clones_the_parts_scales_wielded_weapons_and_leaves_the_weenies_alone()
        {
            TestGameTables.EnsureInitialized();

            var weenie = MakeWeenie(TrashWcid, 150);
            var creature = NewCreature(weenie);
            Assert.AreSame(weenie.PropertiesBodyPart, creature.Biota.PropertiesBodyPart, "positive control: the creature shares the weenie's parts");

            var swordWeenie = new Weenie
            {
                WeenieClassId = 0x7F0E00F1,
                ClassName = "stretchsword",
                WeenieType = WeenieType.MeleeWeapon,
                PropertiesInt = new Dictionary<PropertyInt, int> { [PropertyInt.Damage] = 100 },
                PropertiesString = new Dictionary<PropertyString, string> { [PropertyString.Name] = "Stretch Sword" },
            };
            var sword = new MeleeWeapon(swordWeenie, new ObjectGuid(nextGuid++));
            creature.EquippedObjects[sword.Guid] = sword;
            sword.Wielder = creature;

            var (parts, weapons) = ReachUpStamp.ScaleDamage(creature, Overscale, 1.0);

            Assert.AreEqual(2, parts);
            Assert.AreEqual(1, weapons);
            Assert.AreEqual(50, creature.Biota.PropertiesBodyPart[CombatBodyPart.Head].DVal, "40 x 1.25");
            Assert.AreEqual(25, creature.Biota.PropertiesBodyPart[CombatBodyPart.Hand].DVal, "20 x 1.25");
            Assert.AreEqual(400, creature.Biota.PropertiesBodyPart[CombatBodyPart.Head].BaseArmor, "armour ratio 1.0");
            Assert.AreEqual(125, sword.GetProperty(PropertyInt.Damage));
            Assert.AreEqual(40, weenie.PropertiesBodyPart[CombatBodyPart.Head].DVal, "the cached creature weenie is untouched");
            Assert.AreEqual(100, swordWeenie.PropertiesInt[PropertyInt.Damage], "the cached weapon weenie is untouched");
        }

        // ---- 3. PrepareForPlacement on live creatures ---------------------------------------------------------

        [TestMethod]
        public void A_stretched_entry_is_normalized_at_its_mapped_level_and_overscaled()
        {
            WithSeededKeys(() =>
            {
                var weenie = MakeWeenie(TrashWcid, 150);
                var plan = MakePlan(stretch: true);
                var stretched = NewCreature(weenie);
                var native = NewCreature(weenie);

                Prepare(stretched, StretchedEntry(), plan);
                Prepare(native, NativeEntry(), plan);

                // Health: native max(floor 1200, normalized authored) x 1.5; stretched max(1200, round(3000.4)) x (1.5 x 1.25).
                Assert.AreEqual(1800u, native.Health.MaxValue, "native: 1200 x 1.5");
                Assert.AreEqual(5625u, stretched.Health.MaxValue, "stretched: 3000 x 1.875");

                // Attributes: the uplift raise reads the mapped standard.
                Assert.AreEqual(250u, Attribute(native, PropertyAttribute.Strength), "native: the run's median");
                Assert.AreEqual(300u, Attribute(stretched, PropertyAttribute.Strength), "stretched: the mapped median");
                Assert.AreEqual(260u, Attribute(stretched, PropertyAttribute.Endurance));

                // Damage: BandUplift scales the best part to the standard's MaxBodyDamage (40 -> 90 or 120), then the
                // overscale multiplies the stretched creature's parts by 1.25 (120 -> 150, 60 -> 75).
                Assert.AreEqual(90, native.Biota.PropertiesBodyPart[CombatBodyPart.Head].DVal);
                Assert.AreEqual(45, native.Biota.PropertiesBodyPart[CombatBodyPart.Hand].DVal);
                Assert.AreEqual(150, stretched.Biota.PropertiesBodyPart[CombatBodyPart.Head].DVal);
                Assert.AreEqual(75, stretched.Biota.PropertiesBodyPart[CombatBodyPart.Hand].DVal);

                // Armour reads the mapped standard and the overscale leaves it alone (400 -> 500, not x 1.25).
                Assert.AreEqual(400, native.Biota.PropertiesBodyPart[CombatBodyPart.Head].BaseArmor);
                Assert.AreEqual(500, stretched.Biota.PropertiesBodyPart[CombatBodyPart.Head].BaseArmor);

                // The cached weenie is never written.
                Assert.AreEqual(40, weenie.PropertiesBodyPart[CombatBodyPart.Head].DVal);
                Assert.AreEqual(400, weenie.PropertiesBodyPart[CombatBodyPart.Head].BaseArmor);

                // Level: both are stamped to the mapped level.
                Assert.AreEqual(M, stretched.Level);
                Assert.AreEqual(M, native.Level);
            });
        }

        [TestMethod]
        public void A_stretched_entry_pays_overscaled_xp_and_unscaled_luminance()
        {
            WithSeededKeys(() =>
            {
                var weenie = MakeWeenie(TrashWcid, 150);
                var plan = MakePlan(stretch: true);
                var stretched = NewCreature(weenie);
                var native = NewCreature(weenie);

                Prepare(stretched, StretchedEntry(), plan);
                Prepare(native, NativeEntry(), plan);

                var baseXp = DungeonPopulationBuilder.BaseXp(ThreadDungeonManager.Store.XpLadder, AuthoredXp, 150, DungeonRole.Trash, plan.BossLevel, M);
                Assert.IsTrue(baseXp >= AuthoredXp, "positive control: the anchor is at least the authored XpOverride");

                Assert.AreEqual((int)DungeonPopulationBuilder.XpFor(plan, baseXp, DungeonRole.Trash), native.XpOverride, "native: the unscaled anchor");
                Assert.AreEqual((int)DungeonPopulationBuilder.XpFor(plan, (long)Math.Round(baseXp * Overscale, MidpointRounding.AwayFromZero), DungeonRole.Trash),
                    stretched.XpOverride, "stretched: the anchor x 1.25");
                Assert.IsTrue(stretched.XpOverride > native.XpOverride);

                Assert.AreEqual(native.LuminanceAward, stretched.LuminanceAward, "luminance is not overscaled");
                Assert.IsTrue((native.LuminanceAward ?? 0) > 0, "positive control: the fixture pays luminance");
            });
        }

        [TestMethod]
        public void The_damage_ceiling_still_binds_on_an_overscaled_entry()
        {
            WithSeededKeys(() =>
            {
                var weenie = MakeWeenie(TrashWcid, 150);

                // No ceiling: the overscaled creature's melee hit.
                var free = NewCreature(weenie);
                Prepare(free, StretchedEntry(), MakePlan(stretch: true));
                var overscaled = DamageCeiling.Measure(free).Melee;
                Assert.AreEqual(150, free.Biota.PropertiesBodyPart[CombatBodyPart.Head].DVal, "positive control: the free creature is overscaled (120 x 1.25)");

                // A median whose cap sits under the overscaled hit but above the un-overscaled one: if the overscale ran
                // after the ceiling, the creature would end above the cap.
                var plan = MakePlan(stretch: true);
                plan.PerHitMedian = overscaled / (DamageCeiling.DefaultCap * 1.1);
                plan.DamageCeilingCap = DamageCeiling.DefaultCap;
                plan.DamageCeilingTop = 8.0;

                var capped = NewCreature(weenie);
                Prepare(capped, StretchedEntry(), plan);

                var cap = DamageCeiling.DefaultCap * plan.PerHitMedian;
                Assert.IsTrue(overscaled > cap, "positive control: the overscaled hit is above the cap");
                Assert.IsTrue(overscaled / Overscale < cap, "positive control: the un-overscaled hit is under it");
                Assert.IsTrue(DamageCeiling.Measure(capped).Melee <= cap * (1 + 1e-9), $"melee {DamageCeiling.Measure(capped).Melee:0.##} above the cap {cap:0.##}");

                // The discriminating check: the ceiling is a soft curve, so "under the cap" alone also holds when the ceiling
                // runs first and the overscale after it. The capped creature must equal the ceiling applied to the
                // OVERSCALED creature (free is exactly the pipeline up to the ceiling).
                var ceilingOnFree = ThreadDungeonSpawner.ApplyDamageCeiling("test", free, StretchedEntry(), plan);
                Assert.IsNotNull(ceilingOnFree);
                Assert.IsTrue(ceilingOnFree.BodyFactor < 1.0, "positive control: the ceiling binds on the overscaled creature");
                Assert.AreEqual(free.Biota.PropertiesBodyPart[CombatBodyPart.Head].DVal, capped.Biota.PropertiesBodyPart[CombatBodyPart.Head].DVal,
                    "the ceiling measured the overscaled damage");
                Assert.AreEqual(free.Biota.PropertiesBodyPart[CombatBodyPart.Hand].DVal, capped.Biota.PropertiesBodyPart[CombatBodyPart.Hand].DVal);
            });
        }

        [TestMethod]
        public void The_attribute_raise_gate_prices_a_stretched_entry_with_its_own_health_terms()
        {
            WithSeededKeys(() =>
            {
                // No trash floor and a 0.2 gem multiplier: the native target is round(588 x 0.2) = 118, which is not above
                // the raised Endurance's health term (260 / 2 = 130), so the raise is skipped. The stretched target is
                // round(3000 x 0.25) = 750, which is, so the raise runs.
                var plan = MakePlan(stretch: true);
                plan.TrashHealthFloor = 0;
                plan.HealthMultiplier = 0.2;

                var weenie = MakeWeenie(TrashWcid, 150);
                var stretched = NewCreature(weenie);
                var native = NewCreature(weenie);

                Prepare(stretched, StretchedEntry(), plan);
                Prepare(native, NativeEntry(), plan);

                Assert.AreEqual(150u, Attribute(native, PropertyAttribute.Strength), "positive control: the native raise is skipped");
                Assert.AreEqual(300u, Attribute(stretched, PropertyAttribute.Strength), "the stretched raise runs on its own health terms");
                Assert.AreEqual(750u, stretched.Health.MaxValue);
            });
        }

        [TestMethod]
        public void The_boss_of_a_stretched_plan_is_neither_restandardized_nor_overscaled()
        {
            WithSeededKeys(() =>
            {
                var weenie = MakeWeenie(BossWcid, 200);
                var withStretch = NewCreature(weenie);
                var without = NewCreature(weenie);

                Prepare(withStretch, BossEntry(), MakePlan(stretch: true));
                Prepare(without, BossEntry(), MakePlan(stretch: false));

                Assert.AreEqual(without.Health.MaxValue, withStretch.Health.MaxValue);
                Assert.AreEqual(without.XpOverride, withStretch.XpOverride);
                Assert.AreEqual(without.Biota.PropertiesBodyPart[CombatBodyPart.Head].DVal, withStretch.Biota.PropertiesBodyPart[CombatBodyPart.Head].DVal);
                Assert.AreEqual(Attribute(without, PropertyAttribute.Strength), Attribute(withStretch, PropertyAttribute.Strength));
            });
        }

        // ---- 3b. the archetype branch (dynamic_dungeons_archetype_uplift ships ON) --------------------------------------

        /// <summary>
        /// A hand-built archetype info for <see cref="TrashWcid"/> (the ArchetypeUpliftTests.Info shape): the run end twice
        /// the own end on every axis, so ArchetypeUplift.Apply raises the creature's damage and skills.
        /// </summary>
        private static ArchetypeUpliftInfo ArchetypeInfo()
        {
            DungeonBandStandard Std(uint scale) => DungeonBandStandard.ForTest(
                new Dictionary<Skill, uint> { [Skill.Run] = 100 * scale },
                maxBodyDamage: 40 * scale, maxBaseArmor: 200 * scale, spellTier: 0,
                attributeMedians: new Dictionary<PropertyAttribute, uint>
                {
                    [PropertyAttribute.Strength] = 120 * scale, [PropertyAttribute.Endurance] = 100 * scale,
                    [PropertyAttribute.Coordination] = 120 * scale, [PropertyAttribute.Quickness] = 120 * scale,
                },
                effectiveDefenseMedians: new Dictionary<Skill, uint> { [Skill.MeleeDefense] = 300 * scale, [Skill.MissileDefense] = 300 * scale, [Skill.MagicDefense] = 9999 },
                effectiveSkillMedians: new Dictionary<Skill, uint> { [Skill.HeavyWeapons] = 200 * scale });

            return new ArchetypeUpliftInfo
            {
                Wcid = TrashWcid,
                Archetype = CreatureArchetype.MeleeTank,
                PrimaryArchetype = CreatureArchetype.MeleeTank,
                Source = CreatureArchetype.MeleeTank,
                RunLevel = M,
                OwnLevel = 150,
                Run = new ArchetypeAxes(Std(2), 80, 0, 12, 10, 4, 6, 0, 8, 1.0),
                Own = new ArchetypeAxes(Std(1), 40, 0, 6, 5, 2, 3, 0, 8, 1.0),
                AllRun = DungeonBandStandard.ForTest(null, 0, 0),
                AllOwn = DungeonBandStandard.ForTest(null, 0, 0),
                OwnEffectiveDamage = 40,
                OffenseRatio = 1.0,
                DefenseRatio = 1.0,
            };
        }

        private static DungeonSpawnPlan ArchetypePlan()
        {
            var plan = MakePlan(stretch: true);
            plan.ArchetypeUplift = ArchetypeUpliftContext.ForTest(M, ArchetypeInfo());
            return plan;
        }

        [TestMethod]
        public void The_archetype_branch_carries_the_overscale_on_health_and_damage()
        {
            WithSeededKeys(() =>
            {
                var plan = ArchetypePlan();
                Assert.IsNotNull(ThreadDungeonSpawner.ArchetypeInfoFor(StretchedEntry(), plan), "positive control: the stretched entry takes the archetype branch");
                Assert.IsNotNull(ThreadDungeonSpawner.ArchetypeInfoFor(NativeEntry(), plan), "positive control: so does the native one");

                var weenie = MakeWeenie(TrashWcid, 150);
                var stretched = NewCreature(weenie);
                var native = NewCreature(weenie);

                Prepare(stretched, StretchedEntry(), plan);
                Prepare(native, NativeEntry(), plan);

                var nativeHp = native.Health.MaxValue;
                var stretchedHp = stretched.Health.MaxValue;
                Assert.IsTrue(nativeHp > 0, "positive control: native health is priced");
                Assert.IsTrue(Math.Abs(stretchedHp - nativeHp * Overscale) <= 1.0, $"health carries x1.25: stretched {stretchedHp}, native {nativeHp}");

                var nativeHead = native.Biota.PropertiesBodyPart[CombatBodyPart.Head].DVal;
                var stretchedHead = stretched.Biota.PropertiesBodyPart[CombatBodyPart.Head].DVal;
                Assert.IsTrue(nativeHead > 40, $"positive control: ArchetypeUplift.Apply raised the authored 40 (got {nativeHead})");
                Assert.AreEqual(ReachUpStamp.ScaleInt(nativeHead, Overscale), stretchedHead, "damage carries x1.25 after ArchetypeUplift.Apply");
                Assert.AreEqual(ReachUpStamp.ScaleInt(native.Biota.PropertiesBodyPart[CombatBodyPart.Hand].DVal, Overscale),
                    stretched.Biota.PropertiesBodyPart[CombatBodyPart.Hand].DVal);

                Assert.AreEqual(40, weenie.PropertiesBodyPart[CombatBodyPart.Head].DVal, "the cached weenie is untouched");
            });
        }

        [TestMethod]
        public void PredictEntry_archetype_branch_keeps_ByArchetype_through_the_overscale()
        {
            var plan = ArchetypePlan();
            var raw = new CreaturePerHit(150, new PerHitSource(40, PropertyAttribute.Strength, 150, false, isBody: true), default);

            var native = FamilyAbilityPlanner.PredictEntry(plan, NativeEntry(), raw, upliftAttributes: false);
            var stretched = FamilyAbilityPlanner.PredictEntry(plan, StretchedEntry(), raw, upliftAttributes: false);

            Assert.IsTrue(native.ByArchetype, "positive control: the native prediction is the archetype one");
            Assert.IsTrue(stretched.ByArchetype, "ByArchetype survives DamageScaledBy");
            Assert.IsTrue(native.Melee.BaseDamage > 0);
            Assert.AreEqual(native.Melee.BaseDamage * Overscale, stretched.Melee.BaseDamage, 1e-9);
            Assert.AreEqual(native.Melee.AttributeValue, stretched.Melee.AttributeValue, "the overscale leaves the attribute alone");
        }

        [TestMethod]
        public void DamageScaledBy_scales_damage_only_above_one()
        {
            var raw = new CreaturePerHit(150, new PerHitSource(40, PropertyAttribute.Strength, 150, false, isBody: true),
                new PerHitSource(30, PropertyAttribute.Coordination, 120, true), byArchetype: true);

            Assert.AreSame(raw, raw.DamageScaledBy(1.0));
            Assert.AreSame(raw, raw.DamageScaledBy(double.NaN));
            Assert.AreSame(raw, raw.DamageScaledBy(double.PositiveInfinity));
            Assert.AreSame(raw, raw.DamageScaledBy(0.5));

            var scaled = raw.DamageScaledBy(1.25);
            Assert.AreEqual(50.0, scaled.Melee.BaseDamage, 1e-9);
            Assert.AreEqual(37.5, scaled.Missile.BaseDamage, 1e-9);
            Assert.AreEqual(150u, scaled.Melee.AttributeValue);
            Assert.AreEqual(120u, scaled.Missile.AttributeValue);
            Assert.IsTrue(scaled.Missile.IsBow && scaled.Melee.IsBody);
            Assert.AreEqual(150, scaled.Level);
            Assert.IsTrue(scaled.ByArchetype);
        }

        [TestMethod]
        public void NonBossHealthTerms_tolerates_a_null_plan()
        {
            Assert.AreEqual((2.0, 0u), ThreadDungeonSpawner.NonBossHealthTerms(StretchedEntry(), null, 2.0));
            Assert.AreEqual((2.0, 0u), ThreadDungeonSpawner.NonBossHealthTerms(null, null, 2.0));
        }

        // ---- 4. the per-hit predictor mirrors the spawner -------------------------------------------------------

        [TestMethod]
        public void PredictEntry_reads_the_mapped_standard_and_the_overscale()
        {
            var plan = MakePlan(stretch: true);
            var raw = new CreaturePerHit(150, new PerHitSource(40, PropertyAttribute.Strength, 150, false, isBody: true), default);

            var native = FamilyAbilityPlanner.PredictEntry(plan, NativeEntry(), raw, upliftAttributes: false);
            var stretched = FamilyAbilityPlanner.PredictEntry(plan, StretchedEntry(), raw, upliftAttributes: false);
            var boss = FamilyAbilityPlanner.PredictEntry(plan, BossEntry(), raw, upliftAttributes: false);

            Assert.AreEqual(90.0, native.Melee.BaseDamage, 1e-9, "native: the run standard's MaxBodyDamage");
            Assert.AreEqual(150.0, stretched.Melee.BaseDamage, 1e-9, "stretched: the mapped standard's 120 x 1.25");
            Assert.AreEqual(M, stretched.Level);
            Assert.AreEqual(40.0, boss.Melee.BaseDamage, 1e-9, "the boss (not normalizing here) is untouched");

            var withAttributes = FamilyAbilityPlanner.PredictEntry(plan, StretchedEntry(), raw, upliftAttributes: true);
            Assert.AreEqual(300u, withAttributes.Melee.AttributeValue, "the attribute raise reads the mapped median");
        }

        // ---- 5. source pins (exact lines) -----------------------------------------------------------------------

        private static string[] Lines() => PooledLootSourceText.Read(Spawner).Split('\n');

        private static string[] BodyLines() => PooledLootSourceText.MethodBody(PooledLootSourceText.Read(Spawner), Signature).Split('\n');

        private static bool IsComment(string line) => line.TrimStart().StartsWith("//", StringComparison.Ordinal);

        /// <summary>The single line index whose trimmed text is exactly <paramref name="code"/>; fails (without dumping the file) unless there is exactly one.</summary>
        private static int ExactLine(string[] lines, string code)
        {
            var hits = Enumerable.Range(0, lines.Length).Where(i => lines[i].Trim() == code).ToList();
            Assert.AreEqual(1, hits.Count, $"expected exactly one line '{code}', found {hits.Count}");
            return hits[0];
        }

        [TestMethod]
        public void Pin_no_non_boss_uplift_site_reads_the_run_standard()
        {
            // The two section markers are comments, so they are located in the raw lines; the readers are scanned in
            // the comment-stripped lines (same line count, stripping keeps every line break).
            var lines = Lines();
            var rawLines = PooledLootSourceText.ReadRaw(Spawner).Split('\n');
            Assert.AreEqual(rawLines.Length, lines.Length);
            var from = Enumerable.Range(0, rawLines.Length).First(i => rawLines[i].TrimStart().StartsWith("// 4a.", StringComparison.Ordinal));
            var to = ExactLine(rawLines, "// ---- boss normalization: offense and defense (spec B) ----");
            Assert.IsTrue(from < to, "the 4a marker precedes the boss normalization marker");

            var readers = Enumerable.Range(from, to - from).Where(i => !IsComment(lines[i]) && lines[i].Contains("plan.BandStandard")).ToList();

            Assert.AreEqual(3, readers.Count,
                $"only the two normalizingBoss sites may read plan.BandStandard between 4a and the boss normalization; offending lines (1-based): {string.Join(", ", readers.Select(i => i + 1))}");
            Assert.IsTrue(readers.All(i => lines[i].Contains("normalizingBoss") || lines[i].Contains("DungeonSpellTier.SetTier(")),
                $"a non-boss site reads plan.BandStandard at line(s) {string.Join(", ", readers.Where(i => !lines[i].Contains("normalizingBoss") && !lines[i].Contains("DungeonSpellTier.SetTier(")).Select(i => i + 1))}");
        }

        [TestMethod]
        public void Pin_ApplyUplift_reads_the_uplift_standard()
        {
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(Spawner),
                "private static void ApplyUplift(object logTag, Creature creature, DungeonSpawnPlanEntry entry, DungeonSpawnPlan plan,").Split('\n');

            ExactLine(body, "var standard = UpliftStandardFor(entry, plan);");
        }

        [TestMethod]
        public void Pin_xp_is_priced_from_the_overscaled_anchor()
        {
            ExactLine(BodyLines(), "creature.XpOverride = (int)Math.Clamp(DungeonPopulationBuilder.XpFor(plan, OverscaledBaseXp(baseXp, entry, plan), entry.Role), 0, int.MaxValue);");
            ExactLine(BodyLines(), "var lum = DungeonPopulationBuilder.LuminanceFor(plan, baseXp, rewardLevel, entry.Role);");
        }

        [TestMethod]
        public void Pin_the_archetype_rating_floor_receives_the_strip_switch_as_noCritDamage()
        {
            // Dropping the argument lets FloorRatings write a band-standard CritDamageRating back onto a stripped creature.
            ExactLine(BodyLines(), "archetypeRatings = ArchetypeUplift.FloorRatings(creature, archetypeSession, ArchetypeFactsResolver.LiveProjectileMeanOf, plan.StripCombatTraits);");
        }

        [TestMethod]
        public void Pin_the_overscale_runs_after_the_uplift_and_before_run_speed_and_the_ceiling()
        {
            var body = BodyLines();
            var overscale = ExactLine(body, "ApplyStretchOverscale(logTag, creature, entry, plan);");
            var uplift = ExactLine(body, "ApplyUplift(logTag, creature, entry, plan, ownLevel, spellsRaised, attributesRaised, upliftAttributes);");
            var archetype = ExactLine(body, "var applied = ArchetypeUplift.Apply(creature, archetypeSession, entry.UpliftLevel, plan.DefenseSkillCapOffset, plan.StripCombatTraits);");
            var runSpeed = ExactLine(body, "if (Math.Abs(plan.RunSpeedMult - 1.0) > 1e-9 && plan.RunSpeedMult > 0)");
            var ceiling = ExactLine(body, "ApplyDamageCeiling(logTag, creature, entry, plan);");

            Assert.IsTrue(overscale > uplift && overscale > archetype, "the overscale runs after both uplift branches");
            Assert.IsTrue(overscale < runSpeed, "the overscale runs before the run-speed block");
            Assert.IsTrue(overscale < ceiling, "the overscale runs before the damage ceiling");

            // Not inside the braceless `else if (entry.UpliftLevel > 0)`: the line before it is the ApplyUplift statement,
            // so the overscale is the next statement at the outer level and runs for the archetype branch too.
            var previous = Enumerable.Range(0, overscale).Reverse().First(i => body[i].Trim().Length > 0 && !IsComment(body[i]));
            Assert.AreEqual(uplift, previous, "the overscale immediately follows the uplift block");
            Assert.AreEqual(body[uplift].Length - body[uplift].TrimStart().Length - 4, body[overscale].Length - body[overscale].TrimStart().Length,
                "the overscale sits one indent level outside the braceless else-if");
        }

        [TestMethod]
        public void Pin_the_archetype_session_carries_the_overscale()
        {
            ExactLine(BodyLines(), "archetypeSession = ArchetypeUplift.Begin(creature, archetypeInfo, plan.HealthMultiplier * OverscaleFor(entry, plan));");
        }

        [TestMethod]
        public void Pin_the_plan_log_line_carries_the_stretch_segment()
        {
            ExactLine(Lines(), "log.Info($\"[DYNDUNGEON] {run} plan: family={plan.FamilyId ?? \"none\"} {StretchLogSegment(plan)} weak={WeakPointLabel(plan.WeakPoint)} \" +");
        }
    }
}
