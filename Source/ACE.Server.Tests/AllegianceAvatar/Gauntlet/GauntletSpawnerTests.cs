using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

using TreasureDeath = ACE.Database.Models.World.TreasureDeath;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Avatars.Gauntlet;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldEvents;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.Avatars.Gauntlet
{
    /// <summary>
    /// GauntletSpawner's off-world half (Prepare), its health clamp and XP scale, its ephemeral refusal, the wake's target
    /// choice, and the death report. Fixtures are in-memory weenies built the TrialSpawnerTests way (no spell book, so no
    /// projectile cache seed is needed), widened with loot, a rare flag and kill tasks so every strip has something to strip.
    ///
    /// PropertyManager: ClassInitialize loads the compiled defaults (creature construction reads monster-effect keys);
    /// <see cref="WithPrepareKeys"/> seeds the keys PrepareForPlacement reads and restores them in a finally block.
    /// </summary>
    [TestClass]
    public class GauntletSpawnerTests
    {
        private const uint TrashWcid = 0x7F1D8001;
        private const uint BossWcid = 0x7F1D8003;

        private static uint nextGuid = 0x7F1D0001;

        private static readonly OutdoorPerimeterSettings Settings = new OutdoorPerimeterSettings(10, 40, true, 5, 5, false, 3, true, 5, 20);

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        private static Creature Make(uint wcid, int level = 300, int xp = 2_000_000, uint health = 1500)
        {
            TestGameTables.EnsureInitialized();

            var weenie = new Weenie
            {
                WeenieClassId = wcid,
                ClassName = $"gauntletprobe{wcid:X}",
                WeenieType = WeenieType.Creature,
                PropertiesInt = new Dictionary<PropertyInt, int> { [PropertyInt.Level] = level, [PropertyInt.XpOverride] = xp },
                PropertiesBool = new Dictionary<PropertyBool, bool> { [PropertyBool.CanGenerateRare] = true, [PropertyBool.Stuck] = true },
                PropertiesDID = new Dictionary<PropertyDataId, uint> { [PropertyDataId.DeathTreasureType] = 453 },
                PropertiesString = new Dictionary<PropertyString, string>
                {
                    [PropertyString.Name] = $"Gauntlet Probe {wcid:X}",
                    [PropertyString.KillQuest] = "gauntletprobekill1",
                    [PropertyString.KillQuest2] = "gauntletprobekill2",
                    [PropertyString.KillQuest3] = "gauntletprobekill3",
                },
                PropertiesAttribute = new Dictionary<PropertyAttribute, PropertiesAttribute>
                {
                    [PropertyAttribute.Strength] = new PropertiesAttribute { InitLevel = 250 },
                    [PropertyAttribute.Endurance] = new PropertiesAttribute { InitLevel = 200 },
                    [PropertyAttribute.Coordination] = new PropertiesAttribute { InitLevel = 200 },
                    [PropertyAttribute.Quickness] = new PropertiesAttribute { InitLevel = 200 },
                    [PropertyAttribute.Focus] = new PropertiesAttribute { InitLevel = 200 },
                    [PropertyAttribute.Self] = new PropertiesAttribute { InitLevel = 200 },
                },
                PropertiesAttribute2nd = new Dictionary<PropertyAttribute2nd, PropertiesAttribute2nd>
                {
                    [PropertyAttribute2nd.MaxHealth] = new PropertiesAttribute2nd { InitLevel = health },
                    [PropertyAttribute2nd.MaxStamina] = new PropertiesAttribute2nd { InitLevel = 500 },
                    [PropertyAttribute2nd.MaxMana] = new PropertiesAttribute2nd { InitLevel = 500 },
                },
                PropertiesSkill = new Dictionary<Skill, PropertiesSkill>
                {
                    [Skill.HeavyWeapons] = new PropertiesSkill { InitLevel = 300, SAC = SkillAdvancementClass.Trained },
                    [Skill.MeleeDefense] = new PropertiesSkill { InitLevel = 280, SAC = SkillAdvancementClass.Trained },
                    [Skill.Run] = new PropertiesSkill { InitLevel = 150, SAC = SkillAdvancementClass.Trained },
                },
                PropertiesBodyPart = new Dictionary<CombatBodyPart, PropertiesBodyPart>
                {
                    [CombatBodyPart.Head] = new PropertiesBodyPart { DType = DamageType.Slash, DVal = 120, DVar = 0.5f, BaseArmor = 300 },
                },
            };

            return new Creature(weenie, new ObjectGuid(nextGuid++));
        }

        private static GauntletWavePlan Wave(double healthMultiplier = 1.5)
        {
            var plan = new DungeonSpawnPlan
            {
                BossWcid = BossWcid,
                FamilyId = "gauntletprobe",
                HealthMultiplier = healthMultiplier,
                BossHealthMultiplier = 2.0,
                BossLevel = 310,
                XpMultiplier = 1.5,
                BossXpMultiplier = 2.0,
                LumMultiplier = 1.0,
                XpScale = 1.0,
                LumScale = 1.0,
                Profile = new TreasureDeath { Tier = 8 },
                SalvageAffinities = new List<(int MaterialId, uint BaseWcid, double Chance)> { (20, 3000, 0.5) },
            };

            plan.Entries.Add(new DungeonSpawnPlanEntry(TrashWcid, DungeonRole.Trash, GauntletFixtureSites.Ring(30, 0)));
            plan.Entries.Add(new DungeonSpawnPlanEntry(BossWcid, DungeonRole.Boss, GauntletFixtureSites.Ring(30, 180)));

            return new GauntletWavePlan { Wave = 3, Level = 250, Plan = plan, Group = GroupScaling.Solo };
        }

        private static FakeGauntletOwner Owner() => new FakeGauntletOwner { CentreGx = 1234.5, CentreGy = 678.25, Radius = 55, CurrentWave = 3 };

        private static void WithPrepareKeys(Action body)
        {
            try
            {
                Assert.IsTrue(PropertyManager.ModifyBool("dynamic_dungeons_uplift_attributes", DungeonPopulationLimits.DefaultUpliftAttributes));
                Assert.IsTrue(PropertyManager.ModifyBool("dynamic_dungeons_strip_aoe_spells", DungeonSpellFilter.DefaultStripAoeSpellsEnabled));
                Assert.IsTrue(PropertyManager.ModifyString("dynamic_dungeons_banned_spell_ids", DungeonSpellFilter.DefaultBannedSpellIds));

                body();
            }
            finally
            {
                PropertyManager.ModifyBool("dynamic_dungeons_uplift_attributes", DungeonPopulationLimits.DefaultUpliftAttributes);
                PropertyManager.ModifyBool("dynamic_dungeons_strip_aoe_spells", DungeonSpellFilter.DefaultStripAoeSpellsEnabled);
                PropertyManager.ModifyString("dynamic_dungeons_banned_spell_ids", DungeonSpellFilter.DefaultBannedSpellIds);
            }
        }

        // ---- Prepare -----------------------------------------------------------------------------------------------

        [TestMethod]
        public void Prepare_StripsLootAndKillQuests_StampsBackRefAndPerimeter()
        {
            WithPrepareKeys(() =>
            {
                var wave = Wave();
                var owner = Owner();

                foreach (var e in wave.Plan.Entries)
                {
                    var label = $"{e.Role}";
                    var live = Make(e.Wcid);
                    var g = Make(e.Wcid);

                    ThreadDungeonSpawner.ShapeCreature(live, wave.Plan, e, 3000, GauntletSpawner.ShapeOptions(wave.Group, "live"));
                    GauntletSpawner.Prepare(g, wave, e, 3000, owner, GauntletTuning.Defaults, Settings, "test");

                    // Positive controls: the live twin drops loot and carries kill tasks, so every strip below is non-vacuous.
                    Assert.IsNotNull(live.DeathTreasureOverride, $"{label}: live loot profile");
                    Assert.IsNotNull(live.DeathTreasureType, $"{label}: live treasure DID");
                    Assert.IsNotNull(live.P_DungeonSalvageAffinities, $"{label}: live salvage");
                    Assert.IsTrue(live.CanGenerateRare, $"{label}: live rare flag");
                    Assert.IsNotNull(live.GetProperty(PropertyString.KillQuest), $"{label}: live kill task");
                    Assert.IsTrue(live.XpOverride > 0);

                    // Loot.
                    Assert.IsNull(g.DeathTreasureOverride, $"{label}: DeathTreasureOverride");
                    Assert.IsNull(g.DeathTreasureType, $"{label}: DeathTreasureType");
                    Assert.IsNull(g.P_DungeonSalvageAffinities, $"{label}: salvage");
                    Assert.AreEqual(0, g.LootGateLevel);
                    Assert.IsFalse(g.CanGenerateRare, $"{label}: rare");

                    // Kill tasks.
                    Assert.IsNull(g.GetProperty(PropertyString.KillQuest));
                    Assert.IsNull(g.GetProperty(PropertyString.KillQuest2));
                    Assert.IsNull(g.GetProperty(PropertyString.KillQuest3));

                    // The live fight is kept: same health, XP and luminance as the Thread twin (scale 1.0).
                    Assert.AreEqual(live.Health.MaxValue, g.Health.MaxValue, $"{label}: health");
                    Assert.AreEqual(live.XpOverride, g.XpOverride, $"{label}: XP at scale 1");
                    Assert.AreEqual(live.LuminanceAward, g.LuminanceAward, $"{label}: luminance");
                    Assert.AreEqual(e.Role, g.DungeonRole);

                    // Back-reference and wave.
                    Assert.AreSame(owner, g.P_Gauntlet, $"{label}: P_Gauntlet");
                    Assert.AreEqual(3, g.P_GauntletWave);
                    Assert.AreEqual(g.Health.MaxValue, g.P_GauntletSpawnMaxHealth);
                    Assert.IsNull(live.P_Gauntlet, "a Thread creature carries no Gauntlet owner");
                    Assert.IsNull(g.GetProperty(PropertyInt.ThreadDungeonRunId), "no Thread run stamp");
                    Assert.IsNull(g.P_DungeonRun);

                    // Perimeter: the owner's circle, never interior, HomeRadius 2R.
                    var perimeter = g.P_OutdoorPerimeter;
                    Assert.IsNotNull(perimeter, $"{label}: perimeter");
                    Assert.AreEqual(1234.5, perimeter.CentreGx);
                    Assert.AreEqual(678.25, perimeter.CentreGy);
                    Assert.AreEqual(55, perimeter.Radius);
                    Assert.IsFalse(perimeter.Interior);
                    Assert.AreSame(Settings, perimeter.Settings);
                    Assert.AreEqual(110.0, g.HomeRadius);
                }
            });
        }

        [TestMethod]
        public void Prepare_HealthClampedToMax()
        {
            WithPrepareKeys(() =>
            {
                var wave = Wave(healthMultiplier: 4.0);
                var e = wave.Plan.Entries[0];

                var unclamped = Make(e.Wcid);
                GauntletSpawner.Prepare(unclamped, wave, e, 0, Owner(), GauntletTuning.Defaults, Settings, "test");
                Assert.IsTrue(unclamped.Health.MaxValue > 2000, $"positive control: shaped health {unclamped.Health.MaxValue} is above the test cap");

                var clamped = Make(e.Wcid);
                GauntletSpawner.Prepare(clamped, wave, e, 0, Owner(), GauntletTuning.Defaults with { MaxCreatureHealth = 2000 }, Settings, "test");
                Assert.AreEqual(2000u, clamped.Health.MaxValue, "clamped after shaping");
                Assert.AreEqual(clamped.Health.MaxValue, clamped.Health.Current, "refilled to the new max");
                Assert.AreEqual(2000u, clamped.P_GauntletSpawnMaxHealth, "the spawn max is the clamped one");

                // At the shipped clamp: a saturated int.MaxValue creature lands at or under 2e9 (float steps of 128 there).
                var saturated = Make(e.Wcid);
                WorldEventSpawner.ApplyHealth(saturated, int.MaxValue);
                Assert.IsTrue(saturated.Health.MaxValue > 2_000_000_000u, "positive control: saturated above the clamp");
                Assert.IsTrue(GauntletSpawner.ClampHealth(saturated, 2_000_000_000));
                Assert.IsTrue(saturated.Health.MaxValue <= 2_000_000_000u && saturated.Health.MaxValue >= 2_000_000_000u - 256, $"landed at {saturated.Health.MaxValue}");
                Assert.AreEqual(saturated.Health.MaxValue, saturated.Health.Current);

                // Under the cap nothing changes.
                var small = Make(e.Wcid, health: 900);
                var before = small.Health.MaxValue;
                Assert.IsFalse(GauntletSpawner.ClampHealth(small, 2_000_000_000));
                Assert.AreEqual(before, small.Health.MaxValue);
            });
        }

        [TestMethod]
        public void XpScaleZero_ZeroesXpOverride()
        {
            WithPrepareKeys(() =>
            {
                var wave = Wave();
                var e = wave.Plan.Entries[0];

                int? XpAt(double scale)
                {
                    var c = Make(e.Wcid);
                    GauntletSpawner.Prepare(c, wave, e, 0, Owner(), GauntletTuning.Defaults with { KillXpScale = scale }, Settings, "test");
                    return c.XpOverride;
                }

                var full = XpAt(1.0);
                Assert.IsTrue(full > 0, "positive control: kill XP at scale 1");
                Assert.AreEqual(0, XpAt(0.0), "scale 0 turns kill XP off");
                Assert.AreEqual((int)Math.Round(full.Value * 0.5, MidpointRounding.AwayFromZero), XpAt(0.5), "scale 0.5 halves it");

                var c2 = Make(e.Wcid, xp: int.MaxValue / 2);
                Assert.AreEqual(int.MaxValue, GauntletSpawner.ScaleXp(c2, 10.0), "saturates");
            });
        }

        // ---- Spawn -------------------------------------------------------------------------------------------------

        [TestMethod]
        public void Spawn_RefusesPersistentLandblock()
        {
            // An uninitialized Landblock reads Instance 0: a persistent (non-ephemeral) copy.
            var lb = (Landblock)RuntimeHelpers.GetUninitializedObject(typeof(Landblock));
            Assert.IsFalse(lb.IsEphemeral, "guard: the fixture landblock is persistent");

            var wave = Wave();
            var creates = 0;
            WorldObject Create(uint wcid) { creates++; return Make(wcid); }

            var spawn = new PortalWaveSpawn(0, 2.0, wave.Plan.Entries[0]);
            var result = GauntletSpawner.Spawn(wave, spawn, 0, Owner(), lb, 0, GauntletTuning.Defaults, Settings, "test", Create);

            Assert.IsNull(result, "nothing spawns on a persistent landblock");
            Assert.AreEqual(0, creates, "the refusal comes before any create");
        }

        // ---- wake --------------------------------------------------------------------------------------------------

        [TestMethod]
        public void EngageEnemy_DeadRunWakesNothing()
        {
            var c = Make(TrashWcid);
            var owner = Owner();
            owner.IsLive = false;

            Assert.IsFalse(GauntletSpawner.EngageEnemy(c, owner, out _), "a run that is not live wakes nothing");
            Assert.IsFalse(GauntletSpawner.EngageEnemy(c, null, out _), "no owner wakes nothing");
        }

        [TestMethod]
        public void NearestTarget_NoMembers_IsNoMember()
        {
            var c = Make(TrashWcid);
            var (best, members) = GauntletSpawner.NearestTarget(c, Array.Empty<Player>(), _ => 0);

            Assert.IsNull(best);
            Assert.AreEqual(0, members);
            Assert.AreEqual(ThreadEncounterPortalWaves.WakeResult.NoMember, ThreadEncounterPortalWaves.ClassifyWake(members, false));
        }

        // ---- death -------------------------------------------------------------------------------------------------

        [TestMethod]
        public void OnCreatureDied_ReportsOnlyWhileLive_AndNeverThrows()
        {
            var owner = Owner();
            var c = Make(TrashWcid);
            c.P_Gauntlet = owner;

            GauntletSpawner.OnCreatureDied(c);
            CollectionAssert.AreEqual(new[] { c }, owner.Deaths, "a live run hears the death");

            owner.IsLive = false;
            GauntletSpawner.OnCreatureDied(c);
            Assert.AreEqual(1, owner.Deaths.Count, "a finished run hears nothing");

            owner.IsLive = true;
            owner.ThrowOnDeath = true;
            GauntletSpawner.OnCreatureDied(c);

            GauntletSpawner.OnCreatureDied(Make(TrashWcid));
            GauntletSpawner.OnCreatureDied(null);
        }

        [TestMethod]
        public void ObservedMaxNonBoss_MeasuresDistinctNonBossEntries_AndDestroysEach()
        {
            WithPrepareKeys(() =>
            {
                var wave = Wave();
                wave.Plan.Entries.Add(new DungeonSpawnPlanEntry(TrashWcid, DungeonRole.Trash, GauntletFixtureSites.Ring(30, 90)));

                var created = new List<Creature>();
                Creature Create(uint wcid) { var c = Make(wcid); created.Add(c); return c; }

                var max = GauntletSpawner.ObservedMaxNonBoss(wave, Create, "test");

                Assert.AreEqual(1, created.Count, "two identical trash entries measured once, the boss never");
                Assert.IsTrue(created.All(c => c.IsDestroyed));

                var twin = Make(TrashWcid);
                ThreadDungeonSpawner.ShapeCreature(twin, wave.Plan, wave.Plan.Entries[0], 0, GauntletSpawner.ShapeOptions(wave.Group, "twin"));
                Assert.AreEqual(twin.Health.MaxValue, max);
                Assert.IsTrue(max > 1500, "the shaped max, not the authored one");

                Assert.ThrowsExactly<InvalidOperationException>(() => GauntletSpawner.ObservedMaxNonBoss(wave, _ => null, "test"));
            });
        }

        [TestMethod]
        public void CorpselessPredicate_IsTheMembershipFlag()
        {
            Assert.IsTrue(Creature.IsGauntletCorpselessDeath(true));
            Assert.IsFalse(Creature.IsGauntletCorpselessDeath(false));
        }
    }
}
