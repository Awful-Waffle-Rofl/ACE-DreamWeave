using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

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
    /// Pins of <see cref="ThreadDungeonSpawner.PrepareForPlacement"/> (code review 2026-10-23, F6).
    ///
    /// The ORDER pins are source-text pins in the ThreadDungeonSpawnerGeneratorStripTests style: each one is a
    /// sequencing contract documented in the method body that no behaviour test would notice being broken by a
    /// reorder.
    ///
    /// The GOLDEN pins the switch-off output. With dynamic_dungeons_archetype_uplift off the plan carries no
    /// archetype state, and every site must then run exactly as it did before the archetype uplift existed. The
    /// golden strings were produced by this same fixture run at commit 28ca97d09, the commit that extracted
    /// PrepareForPlacement out of TryPlaceOnce and the last one before any archetype-uplift change to the method.
    /// origin/master has no PrepareForPlacement to call (the body is inline in TryPlaceOnce, which needs a live
    /// landblock), and 28ca97d09's extraction was checked to be a move of master's code apart from the run
    /// null-guards and the call site (git diff --color-moved over bf3079fae..28ca97d09).
    /// </summary>
    [TestClass]
    public class PrepareForPlacementGoldenTests
    {
        private const string Spawner = "Source/ACE.Server/ThreadDungeons/ThreadDungeonSpawner.cs";
        private const string Signature = "internal static void PrepareForPlacement(ThreadDungeonRun run, Creature creature, DungeonSpawnPlanEntry entry, DungeonSpawnPlan plan,";

        private static string Body() => PooledLootSourceText.MethodBody(PooledLootSourceText.Read(Spawner), Signature);

        private static void AssertBefore(string body, string first, string second)
        {
            var a = body.IndexOf(first, StringComparison.Ordinal);
            var b = body.IndexOf(second, StringComparison.Ordinal);

            Assert.IsTrue(a >= 0, $"PrepareForPlacement no longer contains: {first}");
            Assert.IsTrue(b >= 0, $"PrepareForPlacement no longer contains: {second}");
            Assert.IsTrue(a < b, $"PrepareForPlacement must run '{first}' before '{second}'");
        }

        [TestMethod]
        public void The_archetype_spell_retier_runs_before_the_aoe_strip()
        {
            // A raised id can land on a banned id or gain projectiles; the strip has to see the final book.
            AssertBefore(Body(), "DungeonSpellTier.Raise(creature.Biota.PropertiesSpellBook, archetypeInfo.Run.Core.SpellTier)", "DungeonSpellFilter.Strip(book");
        }

        [TestMethod]
        public void Begin_captures_the_authored_values_before_the_attribute_raise_and_the_raise_precedes_health()
        {
            // Begin reads the AUTHORED max health and effective skills; the raise moves Endurance, which feeds max health.
            var body = Body();
            AssertBefore(body, "ArchetypeUplift.Begin(creature", "ArchetypeUplift.RaiseAttributes(creature");
            AssertBefore(body, "ArchetypeUplift.RaiseAttributes(creature", "var target = archetypeSession.HealthTarget;");
        }

        [TestMethod]
        public void The_archetype_rating_floors_run_before_the_gem_ratings()
        {
            // The floors are floors under the AUTHORED ratings; the gem's additive ratings stack on top of them.
            AssertBefore(Body(), "ArchetypeUplift.FloorRatings(creature", "var dr = StampedDamageRating(plan, isBoss);");
        }

        [TestMethod]
        public void The_archetype_apply_runs_before_the_run_speed_multiplier()
        {
            // Apply floors Run's InitLevel; the gem's run-speed multiplier must scale the FINAL value.
            AssertBefore(Body(), "ArchetypeUplift.Apply(creature", "runSkill.InitLevel =");
        }

        [TestMethod]
        public void The_resist_floor_runs_after_the_archetype_apply_gated_on_the_plan_and_before_the_weak_point()
        {
            // It reads the FINAL body armour Apply leaves (the armour-mod floor needs BaseArmor above 0), and it is not a
            // body-part writer, so it has no business after the weak point; gated on the plan's switch.
            var body = Body();
            AssertBefore(body, "ArchetypeUplift.Apply(creature", "plan.UpliftResistFloor ? ArchetypeUplift.FloorResists(creature, archetypeSession, plan) : null");
            AssertBefore(body, "ArchetypeUplift.FloorResists(creature", "ApplyWeakPoint(logTag, creature, entry, plan);");
        }

        [TestMethod]
        public void A_ceiling_lowering_is_counted_only_after_EnterWorld_succeeds()
        {
            // A refused EnterWorld destroys the creature and the retry prepares a fresh one, so counting inside
            // PrepareForPlacement would count a retried placement twice (code review 2026-10-23, R2).
            var src = PooledLootSourceText.Read(Spawner);
            var place = PooledLootSourceText.MethodBody(src, "private static PlaceOutcome TryPlaceOnce(ThreadDungeonRun run, Landblock landblock, DungeonSpawnPlan plan, DungeonSpawnPlanEntry entry,");

            Assert.IsTrue(Body().IndexOf("RecordAttackCeilingLowered(", StringComparison.Ordinal) < 0, "PrepareForPlacement must not count lowerings itself");
            AssertBefore(place, "PrepareForPlacement(run, creature, entry, plan, observedMaxNonBoss, out var ceilingLowered);", "creature.EnterWorld()");
            AssertBefore(place, "EnterWorld refused wcid", "run.RecordAttackCeilingLowered(ceilingLowered.Skills, ceilingLowered.MaxDelta);");
            AssertBefore(place, "DungeonRunTelemetry.Reasons.EnterWorldThrew", "run.RecordAttackCeilingLowered(");
            AssertBefore(place, "run.RecordAttackCeilingLowered(", "return PlaceOutcome.Placed;");
        }

        // ---- the switch-off golden ---------------------------------------------------------------------------

        private const uint TrashWcid = 0x7F0000A1;
        private const uint EliteWcid = 0x7F0000A2;
        private const uint BossWcid = 0x7F0000A3;
        private static uint nextGuid = 0x7F0000A1;

        private static Creature MakeCreature(uint wcid, int level, uint str, uint end, uint maxHealth, uint attack, uint defense, int dval, int armor)
        {
            PropertiesBodyPart Part(int d, int a) => new PropertiesBodyPart
            {
                DType = DamageType.Slash, DVal = d, DVar = 0.4f, BaseArmor = a,
                ArmorVsSlash = a, ArmorVsPierce = a, ArmorVsBludgeon = a, ArmorVsCold = a, ArmorVsFire = a, ArmorVsAcid = a, ArmorVsElectric = a, ArmorVsNether = a,
                MLF = 1, MRF = 1, MLB = 1, MRB = 1,
            };

            var weenie = new Weenie
            {
                WeenieClassId = wcid,
                ClassName = $"golden{wcid:X}",
                WeenieType = WeenieType.Creature,
                PropertiesInt = new Dictionary<PropertyInt, int> { [PropertyInt.Level] = level, [PropertyInt.XpOverride] = 1000 + level },
                PropertiesBool = new Dictionary<PropertyBool, bool> { [PropertyBool.NoCorpse] = true },
                PropertiesString = new Dictionary<PropertyString, string> { [PropertyString.Name] = $"Golden {wcid:X}" },
                PropertiesFloat = new Dictionary<PropertyFloat, double> { [PropertyFloat.HealthRate] = 0.5 },
                PropertiesAttribute = new Dictionary<PropertyAttribute, PropertiesAttribute>
                {
                    [PropertyAttribute.Strength] = new PropertiesAttribute { InitLevel = str },
                    [PropertyAttribute.Endurance] = new PropertiesAttribute { InitLevel = end },
                    [PropertyAttribute.Coordination] = new PropertiesAttribute { InitLevel = str },
                    [PropertyAttribute.Quickness] = new PropertiesAttribute { InitLevel = str },
                    [PropertyAttribute.Focus] = new PropertiesAttribute { InitLevel = end },
                    [PropertyAttribute.Self] = new PropertiesAttribute { InitLevel = end },
                },
                PropertiesAttribute2nd = new Dictionary<PropertyAttribute2nd, PropertiesAttribute2nd>
                {
                    [PropertyAttribute2nd.MaxHealth] = new PropertiesAttribute2nd { InitLevel = maxHealth },
                    [PropertyAttribute2nd.MaxStamina] = new PropertiesAttribute2nd { InitLevel = 200 },
                    [PropertyAttribute2nd.MaxMana] = new PropertiesAttribute2nd { InitLevel = 200 },
                },
                PropertiesSkill = new Dictionary<Skill, PropertiesSkill>
                {
                    [Skill.HeavyWeapons] = new PropertiesSkill { InitLevel = attack, SAC = SkillAdvancementClass.Trained },
                    [Skill.MeleeDefense] = new PropertiesSkill { InitLevel = defense, SAC = SkillAdvancementClass.Trained },
                    [Skill.MissileDefense] = new PropertiesSkill { InitLevel = defense, SAC = SkillAdvancementClass.Trained },
                    [Skill.MagicDefense] = new PropertiesSkill { InitLevel = defense, SAC = SkillAdvancementClass.Trained },
                    [Skill.Run] = new PropertiesSkill { InitLevel = 100, SAC = SkillAdvancementClass.Trained },
                },
                PropertiesBodyPart = new Dictionary<CombatBodyPart, PropertiesBodyPart>
                {
                    [CombatBodyPart.Head] = Part(dval, armor),
                    [CombatBodyPart.Chest] = Part(dval / 2, armor * 2),
                    [CombatBodyPart.Abdomen] = Part(0, armor),
                },
                PropertiesEmote = new List<PropertiesEmote> { new PropertiesEmote { Category = EmoteCategory.Death, Probability = 1.0f } },
            };

            return new Creature(weenie, new ObjectGuid(nextGuid++));
        }

        private static DungeonSpawnPlan MakePlan()
        {
            var standard = DungeonBandStandard.ForTest(
                new Dictionary<Skill, uint>
                {
                    [Skill.HeavyWeapons] = 300, [Skill.MeleeDefense] = 280, [Skill.MissileDefense] = 260, [Skill.MagicDefense] = 240, [Skill.Run] = 150,
                },
                maxBodyDamage: 90, maxBaseArmor: 400, spellTier: 0, healthRate: 2.0,
                attributeMedians: new Dictionary<PropertyAttribute, uint>
                {
                    [PropertyAttribute.Strength] = 250, [PropertyAttribute.Endurance] = 240, [PropertyAttribute.Coordination] = 230,
                    [PropertyAttribute.Quickness] = 220, [PropertyAttribute.Focus] = 210, [PropertyAttribute.Self] = 200,
                },
                effectiveDefenseMedians: new Dictionary<Skill, uint>
                {
                    [Skill.MeleeDefense] = 500, [Skill.MissileDefense] = 470, [Skill.MagicDefense] = 440,
                },
                effectiveSkillMedians: new Dictionary<Skill, uint> { [Skill.HeavyWeapons] = 560 });

            var plan = new DungeonSpawnPlan
            {
                BossWcid = BossWcid,
                FamilyId = "golden",
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
            };

            plan.Entries.Add(new DungeonSpawnPlanEntry(TrashWcid, DungeonRole.Trash, null, upliftLevel: 250));
            plan.Entries.Add(new DungeonSpawnPlanEntry(EliteWcid, DungeonRole.Elite, null));
            plan.Entries.Add(new DungeonSpawnPlanEntry(BossWcid, DungeonRole.Boss, null));
            return plan;
        }

        private static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);

        private static string Snapshot(Creature c)
        {
            var b = c.Biota;
            var sb = new StringBuilder();

            // CreationTimestamp is the wall clock at construction, so it is the one property left out.
            foreach (var kv in (b.PropertiesInt ?? new Dictionary<PropertyInt, int>()).Where(k => k.Key != PropertyInt.CreationTimestamp).OrderBy(k => (int)k.Key)) sb.Append($"i{(int)kv.Key}={kv.Value};");
            foreach (var kv in (b.PropertiesInt64 ?? new Dictionary<PropertyInt64, long>()).OrderBy(k => (int)k.Key)) sb.Append($"l{(int)kv.Key}={kv.Value};");
            foreach (var kv in (b.PropertiesBool ?? new Dictionary<PropertyBool, bool>()).OrderBy(k => (int)k.Key)) sb.Append($"b{(int)kv.Key}={kv.Value};");
            foreach (var kv in (b.PropertiesFloat ?? new Dictionary<PropertyFloat, double>()).OrderBy(k => (int)k.Key)) sb.Append($"f{(int)kv.Key}={F(kv.Value)};");
            foreach (var kv in (b.PropertiesString ?? new Dictionary<PropertyString, string>()).OrderBy(k => (int)k.Key)) sb.Append($"s{(int)kv.Key}={kv.Value};");
            foreach (var kv in (b.PropertiesDID ?? new Dictionary<PropertyDataId, uint>()).OrderBy(k => (int)k.Key)) sb.Append($"d{(int)kv.Key}={kv.Value};");
            foreach (var kv in (b.PropertiesAttribute ?? new Dictionary<PropertyAttribute, PropertiesAttribute>()).OrderBy(k => (int)k.Key)) sb.Append($"a{(int)kv.Key}={kv.Value.InitLevel}+{kv.Value.LevelFromCP};");
            foreach (var kv in (b.PropertiesAttribute2nd ?? new Dictionary<PropertyAttribute2nd, PropertiesAttribute2nd>()).OrderBy(k => (int)k.Key)) sb.Append($"v{(int)kv.Key}={kv.Value.InitLevel}/{kv.Value.CurrentLevel};");
            foreach (var kv in (b.PropertiesSkill ?? new Dictionary<Skill, PropertiesSkill>()).OrderBy(k => (int)k.Key)) sb.Append($"k{(int)kv.Key}={kv.Value.InitLevel}+{kv.Value.LevelFromPP}:{(int)kv.Value.SAC};");
            foreach (var kv in (b.PropertiesBodyPart ?? new Dictionary<CombatBodyPart, PropertiesBodyPart>()).OrderBy(k => (int)k.Key))
            {
                var p = kv.Value;
                sb.Append($"p{(int)kv.Key}={p.DVal},{F(p.DVar)},{p.BaseArmor},{p.ArmorVsSlash},{p.ArmorVsPierce},{p.ArmorVsBludgeon},{p.ArmorVsCold},{p.ArmorVsFire},{p.ArmorVsAcid},{p.ArmorVsElectric},{p.ArmorVsNether};");
            }
            foreach (var kv in (b.PropertiesSpellBook ?? new Dictionary<int, float>()).OrderBy(k => k.Key)) sb.Append($"sp{kv.Key}={F(kv.Value)};");

            sb.Append($"emotes={b.PropertiesEmote?.Count ?? 0};gen={b.PropertiesGenerator?.Count ?? 0};create={b.PropertiesCreateList?.Count ?? 0};");
            sb.Append($"maxhp={c.Health.MaxValue};name={c.Name};level={c.Level};");
            return sb.ToString();
        }

        private static string[] Produce()
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

                var plan = MakePlan();
                var creatures = new[]
                {
                    MakeCreature(TrashWcid, 150, 120, 110, 400, 150, 140, 40, 120),
                    MakeCreature(EliteWcid, 230, 260, 250, 900, 320, 300, 80, 300),
                    MakeCreature(BossWcid, 240, 280, 270, 1500, 340, 320, 100, 350),
                };

                var output = new string[creatures.Length];

                for (var i = 0; i < creatures.Length; i++)
                {
                    ThreadDungeonSpawner.PrepareForPlacement(null, creatures[i], plan.Entries[i], plan, observedMaxNonBoss: 2500);
                    output[i] = Snapshot(creatures[i]);
                }

                return output;
            }
            finally
            {
                foreach (var (key, value) in keys)
                    PropertyManager.ModifyBool(key, value);
                PropertyManager.ModifyString("dynamic_dungeons_banned_spell_ids", DungeonSpellFilter.DefaultBannedSpellIds);
            }
        }

        // Produced at 28ca97d09 by this fixture (see the class summary). Regenerate ONLY for a change that is meant to
        // alter the switch-off path, and say so in the commit.
        private static readonly string[] Golden =
        {
            "i7=0;i25=250;i65=101;i146=1725;i307=12;i308=6;i313=3;i314=4;b65=True;b66=True;f1=5;f3=0.5;f54=0.5;f151=0.25;f9012=0.5;s1=Threadbound Golden 7F0000A1;a1=250+0;a2=240+0;a3=220+0;a4=230+0;a5=210+0;a6=200+0;v1=1680/1800;v3=200/440;v5=200/400;k6=350+0:2;k7=380+0:2;k15=440+0:2;k24=180+0:2;k44=560+0:2;p0=90,0.4000000059604645,200,120,120,120,120,120,120,120,120;p1=45,0.4000000059604645,400,240,240,240,240,240,240,240,240;p2=0,0.4000000059604645,200,120,120,120,120,120,120,120,120;emotes=0;gen=0;create=0;maxhp=1800;name=Threadbound Golden 7F0000A1;level=250;",
            "i7=0;i25=230;i65=101;i146=2768;i307=12;i308=6;i313=3;i314=4;b65=True;b66=True;f1=5;f3=0.5;f54=0.5;f151=0.25;f9012=0.5;s1=Elite Golden 7F0000A2;a1=260+0;a2=250+0;a3=260+0;a4=260+0;a5=250+0;a6=250+0;v1=1797/1922;v3=200/450;v5=200/450;k6=300+0:2;k7=300+0:2;k15=300+0:2;k24=120+0:2;k44=320+0:2;p0=80,0.4000000059604645,300,300,300,300,300,300,300,300,300;p1=40,0.4000000059604645,600,600,600,600,600,600,600,600,600;p2=0,0.4000000059604645,300,300,300,300,300,300,300,300,300;emotes=0;gen=0;create=0;maxhp=1922;name=Elite Golden 7F0000A2;level=230;",
            "i7=0;i25=260;i65=101;i146=7440;i307=30;i308=20;i313=3;i314=4;b65=True;b66=True;f1=5;f3=0.5;f54=0.5;f151=0.25;f9012=0.5;s1=Dread Golden 7F0000A3;a1=280+0;a2=270+0;a3=280+0;a4=280+0;a5=270+0;a6=270+0;v1=9865/10000;v3=200/470;v5=200/470;k6=320+0:2;k7=320+0:2;k15=320+0:2;k24=120+0:2;k44=340+0:2;p0=100,0.4000000059604645,350,350,350,350,350,350,350,350,350;p1=50,0.4000000059604645,700,700,700,700,700,700,700,700,700;p2=0,0.4000000059604645,350,350,350,350,350,350,350,350,350;emotes=0;gen=0;create=0;maxhp=10000;name=Dread Golden 7F0000A3;level=260;",
        };

        [TestMethod]
        public void With_the_archetype_switch_off_the_output_equals_the_pre_archetype_golden()
        {
            Assert.IsNull(MakePlan().ArchetypeUplift, "a plan built without archetype state is the switch-off plan");

            var actual = Produce();

            for (var i = 0; i < Golden.Length; i++)
                Assert.AreEqual(Golden[i], actual[i], $"entry {i} ({MakePlan().Entries[i].Role}) differs from the pre-archetype golden");
        }
    }
}
