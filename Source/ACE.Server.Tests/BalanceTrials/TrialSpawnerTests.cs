using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

using TreasureDeath = ACE.Database.Models.World.TreasureDeath;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.BalanceTrials;
using ACE.Server.Managers;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.BalanceTrials
{
    /// <summary>
    /// Trial creature shaping (<see cref="TrialSpawner"/>): a trial creature is shaped by the live Thread math with rewards ON,
    /// so it pays the live XP and luminance and carries the live health, damage rating and creature abilities, and only its
    /// loot is stripped afterwards (<see cref="Creature.StripLootRewards"/>). Plus the source pins that hold the shape in place:
    /// the kill-progress gate, the exact set of run reads Run = null drops, and the speed-course call order.
    ///
    /// Fixtures: live Creatures built from in-memory weenies the way DamageCeilingSpawnTests builds them (new Creature(weenie,
    /// guid) after TestGameTables.EnsureInitialized), widened with a level, an authored XpOverride, a death treasure DID, the
    /// rare flag and kill-task strings, so every equality below compares non-zero values and every strip has something to strip.
    ///
    /// PropertyManager: ClassInitialize loads the compiled defaults (creature construction and the overlay read
    /// monster_effects_enabled and monster_effect_max_per_monster, as in FamilyAbilityPlannerTests). The shaping tests seed the
    /// three keys PrepareForPlacement reads (the DamageCeilingSpawnTests seeds) and restore them in a finally block.
    /// </summary>
    [TestClass]
    public class TrialSpawnerTests
    {
        private const string SpawnerPath = "Source/ACE.Server/BalanceTrials/TrialSpawner.cs";
        private const string ThreadSpawnerPath = "Source/ACE.Server/ThreadDungeons/ThreadDungeonSpawner.cs";
        private const string DeathPath = "Source/ACE.Server/WorldObjects/Creature_Death.cs";
        private const string PrepareSignature = "internal static void PrepareForPlacement(ThreadDungeonRun run, Creature creature, DungeonSpawnPlanEntry entry, DungeonSpawnPlan plan,\n" +
            "            uint observedMaxNonBoss, out (int Skills, uint MaxDelta) ceilingLowered";

        private const uint TrashWcid = 0x7F1A8001;
        private const uint EliteWcid = 0x7F1A8002;
        private const uint BossWcid = 0x7F1A8003;

        private static uint nextGuid = 0x7F1A0001;

        /// <summary>
        /// A fixture-only spell id in every probe's spell book, so the spellbook comparison is not over two empty books. Its
        /// NumProjectiles (0) is seeded into ThreadDungeonSpawner's projectile cache for the duration of a shaping test, because
        /// the strip would otherwise ask the world database, which this harness does not have; the seed is removed in finally.
        /// </summary>
        private const int ProbeSpellId = 0x7F1A;

        /// <summary>An ability spec the overlay resolves (the speed-course boss guard record).</summary>
        private const string AbilitySpec = "immune";

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        // ---- fixture ---------------------------------------------------------------------------------------

        private static Weenie CreatureWeenie(uint wcid, int level, int xpOverride, uint maxHealth, int dval) => new Weenie
        {
            WeenieClassId = wcid,
            ClassName = $"trialprobe{wcid:X}",
            WeenieType = WeenieType.Creature,
            PropertiesInt = new Dictionary<PropertyInt, int> { [PropertyInt.Level] = level, [PropertyInt.XpOverride] = xpOverride },
            PropertiesBool = new Dictionary<PropertyBool, bool> { [PropertyBool.CanGenerateRare] = true, [PropertyBool.Stuck] = true },
            PropertiesDID = new Dictionary<PropertyDataId, uint> { [PropertyDataId.DeathTreasureType] = 453 },
            PropertiesString = new Dictionary<PropertyString, string>
            {
                [PropertyString.Name] = $"Trial Probe {wcid:X}",
                [PropertyString.KillQuest] = "trialprobekill1",
                [PropertyString.KillQuest2] = "trialprobekill2",
                [PropertyString.KillQuest3] = "trialprobekill3",
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
                [PropertyAttribute2nd.MaxHealth] = new PropertiesAttribute2nd { InitLevel = maxHealth },
                [PropertyAttribute2nd.MaxStamina] = new PropertiesAttribute2nd { InitLevel = 500 },
                [PropertyAttribute2nd.MaxMana] = new PropertiesAttribute2nd { InitLevel = 500 },
            },
            PropertiesSkill = new Dictionary<Skill, PropertiesSkill>
            {
                [Skill.HeavyWeapons] = new PropertiesSkill { InitLevel = 300, SAC = SkillAdvancementClass.Trained },
                [Skill.MeleeDefense] = new PropertiesSkill { InitLevel = 280, SAC = SkillAdvancementClass.Trained },
                [Skill.MissileDefense] = new PropertiesSkill { InitLevel = 260, SAC = SkillAdvancementClass.Trained },
                [Skill.MagicDefense] = new PropertiesSkill { InitLevel = 240, SAC = SkillAdvancementClass.Trained },
                [Skill.Run] = new PropertiesSkill { InitLevel = 150, SAC = SkillAdvancementClass.Trained },
            },
            PropertiesSpellBook = new Dictionary<int, float> { [ProbeSpellId] = 2.0f },
            PropertiesBodyPart = new Dictionary<CombatBodyPart, PropertiesBodyPart>
            {
                [CombatBodyPart.Head] = new PropertiesBodyPart
                {
                    DType = DamageType.Slash, DVal = dval, DVar = 0.5f, BaseArmor = 300,
                    HLF = 0.1f, HRF = 0.1f, MLF = 0.1f, MRF = 0.1f, LLF = 0.1f, LRF = 0.1f,
                },
            },
        };

        private static Creature NewCreature(Weenie weenie)
        {
            TestGameTables.EnsureInitialized();
            return new Creature(weenie, new ObjectGuid(nextGuid++));
        }

        private static readonly Dictionary<uint, (int Level, int Xp, uint Health, int DVal)> Shapes = new Dictionary<uint, (int, int, uint, int)>
        {
            // XpOverride large enough that the luminance base (xp / 4000, rounded to 50) is non-zero, and levels above 185.
            [TrashWcid] = (260, 2_000_000, 1500, 120),
            [EliteWcid] = (270, 3_000_000, 2400, 150),
            [BossWcid] = (280, 6_000_000, 5000, 200),
        };

        private static Creature Make(uint wcid)
        {
            var s = Shapes[wcid];
            return NewCreature(CreatureWeenie(wcid, s.Level, s.Xp, s.Health, s.DVal));
        }

        private static DungeonSpawnPlan Plan()
        {
            var plan = new DungeonSpawnPlan
            {
                BossWcid = BossWcid,
                FamilyId = "trialprobe",
                HealthMultiplier = 1.5,
                BossHealthMultiplier = 2.0,
                BossHealthFloorRatio = 4.0,
                BossLevel = 290,
                DamageRating = 12,
                DamageResistRating = 6,
                BossDamageRating = 30,
                BossDamageResistRating = 20,
                XpMultiplier = 1.5,
                BossXpMultiplier = 2.0,
                LumMultiplier = 1.0,
                XpScale = 1.0,
                LumScale = 1.0,
                Profile = new TreasureDeath { Tier = 8 },
                SalvageAffinities = new List<(int MaterialId, uint BaseWcid, double Chance)> { (20, 3000, 0.5) },
                CreatureAbilities = new FamilyAbilityPlan("trialprobe", 90, null, new CreatureAbilityResult(AbilitySpec, new CreatureAbilityKind[0]), new FamilyAbilityMember[0]),
            };

            plan.Entries.Add(new DungeonSpawnPlanEntry(TrashWcid, DungeonRole.Trash, null));
            plan.Entries.Add(new DungeonSpawnPlanEntry(EliteWcid, DungeonRole.Elite, null));
            plan.Entries.Add(new DungeonSpawnPlanEntry(TrashWcid, DungeonRole.Trash, null));
            plan.Entries.Add(new DungeonSpawnPlanEntry(BossWcid, DungeonRole.Boss, null));
            return plan;
        }

        /// <summary>ThreadDungeonSpawner's private static NumProjectiles memo (read, never reassigned).</summary>
        private static System.Collections.Concurrent.ConcurrentDictionary<int, int> ProjectileCache()
        {
            var field = typeof(ThreadDungeonSpawner).GetField("spellProjectilesCache", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(field, "ThreadDungeonSpawner.spellProjectilesCache moved; rebuild this fixture");
            return (System.Collections.Concurrent.ConcurrentDictionary<int, int>)field.GetValue(null);
        }

        private static string F(double v) => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>
        /// One line per property family of the creature's biota, minus the excluded keys: ints (CreationTimestamp, the wall clock
        /// at construction, is always left out), int64s, floats, bools, strings, DIDs, attributes, vitals, skills, body parts and
        /// the spell book.
        /// </summary>
        private static Dictionary<string, string> Families(Creature c, ISet<PropertyInt> ints, ISet<PropertyBool> bools, ISet<PropertyString> strings, ISet<PropertyDataId> dids)
        {
            var b = c.Biota;
            string Join<TK, TV>(IDictionary<TK, TV> d, Func<TK, bool> keep, Func<TK, TV, string> fmt) =>
                d == null ? "" : string.Join(";", d.Where(kv => keep(kv.Key)).OrderBy(kv => Convert.ToInt64(kv.Key)).Select(kv => fmt(kv.Key, kv.Value)));

            return new Dictionary<string, string>
            {
                ["int"] = Join(b.PropertiesInt, k => k != PropertyInt.CreationTimestamp && !ints.Contains(k), (k, v) => $"{(int)k}={v}"),
                ["int64"] = Join(b.PropertiesInt64, k => true, (k, v) => $"{(int)k}={v}"),
                ["float"] = Join(b.PropertiesFloat, k => true, (k, v) => $"{(int)k}={F(v)}"),
                ["bool"] = Join(b.PropertiesBool, k => !bools.Contains(k), (k, v) => $"{(int)k}={v}"),
                ["string"] = Join(b.PropertiesString, k => !strings.Contains(k), (k, v) => $"{(int)k}={v}"),
                ["did"] = Join(b.PropertiesDID, k => !dids.Contains(k), (k, v) => $"{(int)k}={v}"),
                ["attribute"] = Join(b.PropertiesAttribute, k => true, (k, v) => $"{(int)k}={v.InitLevel}+{v.LevelFromCP}+{v.CPSpent}"),
                ["vital"] = Join(b.PropertiesAttribute2nd, k => true, (k, v) => $"{(int)k}={v.InitLevel}/{v.CurrentLevel}+{v.LevelFromCP}"),
                ["skill"] = Join(b.PropertiesSkill, k => true, (k, v) => $"{(int)k}={v.InitLevel}+{v.LevelFromPP}:{(int)v.SAC}"),
                ["bodypart"] = Join(b.PropertiesBodyPart, k => true, (k, p) =>
                    $"{(int)k}={(int)p.DType},{p.DVal},{F(p.DVar)},{p.BaseArmor},{p.ArmorVsSlash},{p.ArmorVsPierce},{p.ArmorVsBludgeon},{p.ArmorVsCold},{p.ArmorVsFire},{p.ArmorVsAcid},{p.ArmorVsElectric},{p.ArmorVsNether}," +
                    $"{F(p.HLF)},{F(p.MLF)},{F(p.LLF)},{F(p.HRF)},{F(p.MRF)},{F(p.LRF)},{F(p.HLB)},{F(p.MLB)},{F(p.LLB)},{F(p.HRB)},{F(p.MRB)},{F(p.LRB)},{p.BH}"),
                ["spellbook"] = Join(b.PropertiesSpellBook, k => true, (k, v) => $"{k}={F(v)}"),
            };
        }

        private static void WithPrepareKeys(Action body)
        {
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

                Assert.IsTrue(ProjectileCache().TryAdd(ProbeSpellId, 0), $"guard: spell id {ProbeSpellId} was already in the projectile cache");

                body();
            }
            finally
            {
                ProjectileCache().TryRemove(ProbeSpellId, out _);

                foreach (var (key, value) in keys)
                    PropertyManager.ModifyBool(key, value);

                PropertyManager.ModifyString("dynamic_dungeons_banned_spell_ids", DungeonSpellFilter.DefaultBannedSpellIds);
            }
        }

        // ---- 1. live reward, live fight, no loot -----------------------------------------------------------

        [TestMethod]
        public void Trial_shaping_pays_the_live_reward_and_drops_nothing()
        {
            WithPrepareKeys(() =>
            {
                var plan = Plan();
                var rep = new TrialRep { Plan = plan };
                const uint Observed = 3000;

                foreach (var entry in new[] { plan.Entries[0], plan.Entries[1], plan.Entries[3] })
                {
                    var label = $"{entry.Role} {entry.Wcid:X}";

                    var live = Make(entry.Wcid);
                    var trial = Make(entry.Wcid);

                    // The live twin is placed the way TryPlaceOnce places a Thread creature, minus the run stamp: the role and the
                    // never-rot corpse, then PrepareForPlacement.
                    live.DungeonRole = entry.Role;
                    live.TimeToRot = -1;
                    ThreadDungeonSpawner.PrepareForPlacement(null, live, entry, plan, Observed, out _);
                    TrialSpawner.Prepare(trial, plan, entry, Observed, rep, "trialtest");

                    // Positive controls: the live twin pays, fights and drops, so every equality and strip below is non-vacuous.
                    Assert.IsTrue(live.XpOverride > 0, $"{label}: live XP is non-zero");
                    Assert.IsTrue(live.LuminanceAward > 0, $"{label}: live luminance is non-zero");
                    Assert.IsTrue(live.Health.MaxValue > 0, $"{label}: live health is non-zero");
                    Assert.IsTrue((live.DamageRating ?? 0) > 0, $"{label}: live damage rating is non-zero");
                    Assert.IsTrue(live.GetAttachedMonsterEffectKinds().Count > 0, $"{label}: the live twin carries the creature-ability overlay");
                    Assert.IsNotNull(live.DeathTreasureOverride, $"{label}: live loot profile");
                    Assert.IsNotNull(live.DeathTreasureType, $"{label}: live treasure DID");
                    Assert.IsNotNull(live.P_DungeonSalvageAffinities, $"{label}: live salvage affinities");
                    Assert.IsTrue(live.CanGenerateRare, $"{label}: live rare flag");
                    Assert.IsNotNull(live.GetProperty(PropertyString.KillQuest), $"{label}: live kill task");

                    // Parity.
                    Assert.AreEqual(live.XpOverride, trial.XpOverride, $"{label}: XpOverride");
                    Assert.AreEqual(live.LuminanceAward, trial.LuminanceAward, $"{label}: LuminanceAward");
                    Assert.AreEqual(live.Name, trial.Name, $"{label}: Name");
                    Assert.AreEqual(live.Health.MaxValue, trial.Health.MaxValue, $"{label}: Health.MaxValue");
                    Assert.AreEqual(live.DamageRating, trial.DamageRating, $"{label}: DamageRating");
                    Assert.AreEqual(live.GetAttachedMonsterEffectKinds().Count, trial.GetAttachedMonsterEffectKinds().Count, $"{label}: creature-ability overlay");
                    Assert.IsTrue(trial.GetAttachedMonsterEffectKinds().Count > 0, $"{label}: the trial twin carries the overlay");
                    Assert.AreEqual(live.DungeonRole, trial.DungeonRole, $"{label}: DungeonRole");

                    // Every property family, minus only the stripped loot and kill-task keys and the XP and luminance compared above.
                    var ints = new HashSet<PropertyInt> { PropertyInt.XpOverride, PropertyInt.LuminanceAward };
                    var bools = new HashSet<PropertyBool> { PropertyBool.CanGenerateRare };
                    var strings = new HashSet<PropertyString> { PropertyString.KillQuest, PropertyString.KillQuest2, PropertyString.KillQuest3 };
                    var dids = new HashSet<PropertyDataId> { PropertyDataId.DeathTreasureType };

                    var liveFamilies = Families(live, ints, bools, strings, dids);
                    var trialFamilies = Families(trial, ints, bools, strings, dids);
                    var authored = Families(Make(entry.Wcid), ints, bools, strings, dids);

                    foreach (var family in liveFamilies.Keys)
                        Assert.AreEqual(liveFamilies[family], trialFamilies[family], $"{label}: {family} properties");

                    // Positive controls: the families compared are populated, and shaping changed them, so the equality above is
                    // between two shaped creatures and not two copies of the weenie.
                    foreach (var family in new[] { "int", "float", "bool", "string", "attribute", "vital", "skill", "bodypart", "spellbook" })
                        Assert.IsTrue(liveFamilies[family].Length > 0, $"{label}: positive control, the live twin has {family} properties");
                    Assert.IsTrue(liveFamilies.Any(kv => kv.Value != authored[kv.Key]), $"{label}: positive control, shaping changed the creature");
                    Assert.AreNotEqual(authored["vital"], liveFamilies["vital"], $"{label}: positive control, shaping changed the vitals");

                    // No loot.
                    Assert.IsNull(trial.DeathTreasureOverride, $"{label}: DeathTreasureOverride");
                    Assert.IsNull(trial.DeathTreasureType, $"{label}: DeathTreasureType");
                    Assert.IsNull(trial.P_DungeonSalvageAffinities, $"{label}: P_DungeonSalvageAffinities");
                    Assert.AreEqual(0, trial.LootGateLevel, $"{label}: LootGateLevel");
                    Assert.IsFalse(trial.CanGenerateRare, $"{label}: CanGenerateRare");

                    // No kill tasks.
                    Assert.IsNull(trial.GetProperty(PropertyString.KillQuest), $"{label}: KillQuest");
                    Assert.IsNull(trial.GetProperty(PropertyString.KillQuest2), $"{label}: KillQuest2");
                    Assert.IsNull(trial.GetProperty(PropertyString.KillQuest3), $"{label}: KillQuest3");

                    // Membership.
                    Assert.AreSame(rep, trial.P_TrialRep, $"{label}: P_TrialRep");
                    Assert.IsNull(live.P_TrialRep, $"{label}: a live creature carries no trial rep");
                }
            });
        }

        [TestMethod]
        public void ShapeOptions_are_the_live_solo_shape_with_rewards_on()
        {
            var o = TrialSpawner.ShapeOptions("tag");

            Assert.IsNull(o.Run);
            Assert.IsTrue(o.Rewards, "Rewards = true; loot goes through StripLootRewards");
            Assert.AreEqual(GroupScaling.Solo.Effort, o.GroupEffort);
            Assert.AreEqual("tag", o.LogTag);
            Assert.IsFalse(o.ExcludedFromClear);
        }

        // ---- 2. StripDeathRewards keeps its effect -----------------------------------------------------------

        private static Creature BareCreature(ACE.Entity.Models.Biota biota)
        {
            // The ThreadDungeonShapeSeamTests fixture: an uninitialized Creature given a runtime biota and its lock.
            var creature = (Creature)RuntimeHelpers.GetUninitializedObject(typeof(Creature));

            var biotaField = typeof(WorldObject).GetField("<Biota>k__BackingField", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(biotaField, "WorldObject.Biota is no longer an auto-property; rebuild this fixture");
            biotaField.SetValue(creature, biota);

            var lockField = typeof(WorldObject).GetField("BiotaDatabaseLock", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
            Assert.IsNotNull(lockField, "WorldObject.BiotaDatabaseLock moved; rebuild this fixture");
            lockField.SetValue(creature, new System.Threading.ReaderWriterLockSlim());

            return creature;
        }

        private static Creature PayingCreature()
        {
            var creature = BareCreature(new ACE.Entity.Models.Biota
            {
                PropertiesInt = new Dictionary<PropertyInt, int> { [PropertyInt.XpOverride] = 125000, [PropertyInt.LuminanceAward] = 900 },
                PropertiesDID = new Dictionary<PropertyDataId, uint> { [PropertyDataId.DeathTreasureType] = 453 },
                PropertiesBool = new Dictionary<PropertyBool, bool> { [PropertyBool.CanGenerateRare] = true, [PropertyBool.Stuck] = true },
            });

            creature.DeathTreasureOverride = new TreasureDeath { Tier = 8 };
            creature.P_DungeonSalvageAffinities = new List<(int MaterialId, uint BaseWcid, double Chance)> { (20, 3000, 0.5) };
            creature.LootGateLevel = 300;

            return creature;
        }

        /// <summary>
        /// Golden values from origin/master's StripDeathRewards body (before the StripLootRewards split):
        /// XpOverride = 0; LuminanceAward = null; DeathTreasureOverride = null; DeathTreasureType = null;
        /// P_DungeonSalvageAffinities = null; LootGateLevel = 0; CanGenerateRare = false.
        /// </summary>
        [TestMethod]
        public void StripDeathRewards_is_unchanged()
        {
            var creature = PayingCreature();

            // Positive control.
            Assert.AreEqual(125000, creature.XpOverride);
            Assert.AreEqual(900, creature.LuminanceAward);
            Assert.AreEqual(453u, creature.DeathTreasureType);
            Assert.IsNotNull(creature.DeathTreasureOverride);
            Assert.IsNotNull(creature.P_DungeonSalvageAffinities);
            Assert.AreEqual(300, creature.LootGateLevel);
            Assert.IsTrue(creature.CanGenerateRare);

            creature.StripDeathRewards();

            Assert.AreEqual(0, creature.XpOverride);
            Assert.IsTrue(creature.Biota.PropertiesInt.ContainsKey(PropertyInt.XpOverride), "XpOverride is SET to 0, not removed");
            Assert.IsNull(creature.LuminanceAward);
            Assert.IsNull(creature.DeathTreasureOverride);
            Assert.IsNull(creature.DeathTreasureType);
            Assert.IsNull(creature.P_DungeonSalvageAffinities);
            Assert.AreEqual(0, creature.LootGateLevel);
            Assert.IsFalse(creature.CanGenerateRare);
        }

        [TestMethod]
        public void StripLootRewards_keeps_the_xp_and_luminance()
        {
            var creature = PayingCreature();

            creature.StripLootRewards();

            Assert.AreEqual(125000, creature.XpOverride);
            Assert.AreEqual(900, creature.LuminanceAward);
            Assert.IsNull(creature.DeathTreasureOverride);
            Assert.IsNull(creature.DeathTreasureType);
            Assert.IsNull(creature.P_DungeonSalvageAffinities);
            Assert.AreEqual(0, creature.LootGateLevel);
            Assert.IsFalse(creature.CanGenerateRare);
        }

        // ---- 3. kill progress -------------------------------------------------------------------------------

        [TestMethod]
        public void Trial_creatures_feed_no_kill_progress()
        {
            var death = PooledLootSourceText.Read(DeathPath);

            const string Call = "var feedsProgress = FeedsKillProgressHooks(P_SpeedCourseCreature || P_TrialRep != null);";
            StringAssert.Contains(death, Call, "OnDeath must gate kill-fill vessels and mule tokens on trial membership too");
            Assert.AreEqual(1, Regex.Matches(death, Regex.Escape("FeedsKillProgressHooks(")).Count, "one gate call");

            Assert.IsFalse(Creature.FeedsKillProgressHooks(true));
            Assert.IsTrue(Creature.FeedsKillProgressHooks(false));
        }

        // ---- 4. what Run = null drops ------------------------------------------------------------------------

        /// <summary>
        /// Every read of the <c>run</c> parameter in PrepareForPlacement's body, with comments removed, normalized to its form:
        /// <c>run?.Member</c>, <c>run.Member</c>, <c>(object)run</c>, <c>run == null</c>, or the raw token and what follows.
        /// </summary>
        internal static SortedDictionary<string, int> RunReads(string body)
        {
            var code = Regex.Replace(body, @"/\*.*?\*/", " ", RegexOptions.Singleline);
            code = Regex.Replace(code, @"//[^\n]*", " ");

            var reads = new SortedDictionary<string, int>(StringComparer.Ordinal);
            void Add(string read) => reads[read] = reads.TryGetValue(read, out var n) ? n + 1 : 1;

            foreach (Match m in Regex.Matches(code, @"(?<![\w.])run(?!\w)"))
            {
                var before = code.Substring(Math.Max(0, m.Index - 8), Math.Min(8, m.Index));
                var after = code.Substring(m.Index + m.Length);

                Match tail;

                if (before.EndsWith("(object)", StringComparison.Ordinal))
                    Add("(object)run");
                else if ((tail = Regex.Match(after, @"^\s*(\?\.|\.)\s*(\w+)")).Success)
                    Add("run" + tail.Groups[1].Value + tail.Groups[2].Value);
                else if (Regex.IsMatch(after, @"^\s*==\s*null"))
                    Add("run == null");
                else if (Regex.IsMatch(after, @"^\s*!=\s*null"))
                    Add("run != null");
                else
                    Add("run" + new string(after.Take(16).ToArray()).Replace('\n', ' '));
            }

            return reads;
        }

        [TestMethod]
        public void Run_reads_in_PrepareForPlacement_are_exactly_the_known_set()
        {
            var source = PooledLootSourceText.Read(ThreadSpawnerPath);
            Assert.AreEqual(1, Regex.Matches(source, Regex.Escape(PrepareSignature)).Count, "the signature names exactly one PrepareForPlacement overload (the full form)");

            var body = PooledLootSourceText.MethodBody(source, PrepareSignature);
            var reads = RunReads(body);

            // Run = null drops exactly these: the group (Solo for a trial, and GroupEffort carries Solo.Effort), the log tag
            // (LogTag stands in), the "no run" branch of the group effort, and the boss-health telemetry row. Modifier effects
            // reach the creature through the plan, and shrine boons are run-level (design 13, 14). "run != null" is the Threads-only
            // spell strip gate (ThreadEnemySpellStrip, owner ruling 2026-11-21): a trial deliberately keeps its spell book, so
            // losing that branch with Run = null is the intent, not a regression.
            var expected = new SortedDictionary<string, int>(StringComparer.Ordinal) { ["run?.Group"] = 1, ["(object)run"] = 1, ["run == null"] = 1, ["run != null"] = 1, ["run?.RecordBossHealth"] = 1 };

            string Show(IDictionary<string, int> d) => string.Join(", ", d.Select(kv => $"{kv.Key} x{kv.Value}"));

            CollectionAssert.AreEqual(expected.ToList(), reads.ToList(), $"PrepareForPlacement run reads changed. Expected [{Show(expected)}], found [{Show(reads)}]. " +
                "A new or repeated read is something a trial (Run = null) silently loses: decide whether the trial needs it before updating this pin.");
        }

        [TestMethod]
        public void RunReads_sees_a_new_member_read()
        {
            // Positive control for the scanner itself: a plain member read and a conditional one are both reported.
            var reads = RunReads("{ var a = run?.Group; var s = run.Spec; // run.Comment\n var t = (object)run; if (run == null) {} var b = run?.Group; }");

            CollectionAssert.AreEqual(new[] { "(object)run", "run == null", "run.Spec", "run?.Group" }, reads.Keys.ToArray());
            Assert.AreEqual(2, reads["run?.Group"], "a repeated read is counted");
            Assert.AreEqual(1, reads["run.Spec"]);
        }

        // ---- 5. call order ----------------------------------------------------------------------------------

        private static void AssertInOrder(string body, string where, params string[] calls)
        {
            var last = -1;

            foreach (var call in calls)
            {
                var at = body.IndexOf(call, StringComparison.Ordinal);
                Assert.IsTrue(at >= 0, $"{where} no longer contains: {call}");
                Assert.IsTrue(at > last, $"{where}: '{call}' is out of order");
                last = at;
            }
        }

        [TestMethod]
        public void Spawn_sequence_matches_the_speed_course_order()
        {
            var source = PooledLootSourceText.Read(SpawnerPath);

            var prepare = PooledLootSourceText.MethodBody(source, "internal static void Prepare(Creature c, DungeonSpawnPlan plan, DungeonSpawnPlanEntry e, uint observedMaxNonBoss, TrialRep rep, string tag)");
            AssertInOrder(prepare, "Prepare",
                "ThreadDungeonSpawner.ShapeCreature(c, plan, e, observedMaxNonBoss, ShapeOptions(tag));",
                "c.StripLootRewards();",
                "c.RemoveProperty(PropertyString.KillQuest);",
                "c.RemoveProperty(PropertyString.KillQuest2);",
                "c.RemoveProperty(PropertyString.KillQuest3);",
                "c.P_TrialRep = rep;");

            var spawn = PooledLootSourceText.MethodBody(source, "internal static Creature Spawn(DungeonSpawnPlan plan, DungeonSpawnPlanEntry e, uint observedMaxNonBoss, TrialRep rep, Landblock lb, uint instance, string tag)");
            AssertInOrder(spawn, "Spawn",
                "WorldObjectFactory.CreateNewWorldObject(e.Wcid)",
                "if (wo is not Creature creature)",
                "DungeonPopulationBuilder.IsKillable(creature.Attackable, creature.PlayerKillerStatus)",
                "creature.Location = new Position(p.Cell, p.X, p.Y, p.Z, p.QX, p.QY, p.QZ, p.QW, instance);",
                "creature.Location.LandblockId.Landblock != lb.Id.Landblock",
                "Prepare(creature, plan, e, observedMaxNonBoss, rep, tag);",
                "creature.EnterWorld()");

            // The speed course, for the shared steps: create, type check, killable, location, landblock, shape, kill-task strip, EnterWorld.
            var course = PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/SpeedCourses/LiveSpeedCourseWorld.cs"),
                "public SpeedCreatureHandle Spawn(DungeonSpawnPlan plan, DungeonSpawnPlanEntry entry, SpeedCreatureStamp stamp, out uint maxHealth)");
            AssertInOrder(course, "LiveSpeedCourseWorld.Spawn",
                "WorldObjectFactory.CreateNewWorldObject(entry.Wcid)",
                "if (wo is not Creature creature)",
                "DungeonPopulationBuilder.IsKillable(creature.Attackable, creature.PlayerKillerStatus)",
                "creature.Location = new Position(",
                "creature.Location.LandblockId.Landblock != landblock.Id.Landblock",
                "ThreadDungeonSpawner.ShapeCreature(creature, plan, entry,",
                "creature.RemoveProperty(PropertyString.KillQuest);",
                "creature.EnterWorld()");
        }

        private const string SpawnSignature = "internal static Creature Spawn(DungeonSpawnPlan plan, DungeonSpawnPlanEntry e, uint observedMaxNonBoss, TrialRep rep, Landblock lb, uint instance, string tag)";

        private static int IndexOnce(string body, string text, int from = 0)
        {
            var at = body.IndexOf(text, from, StringComparison.Ordinal);
            Assert.IsTrue(at >= 0, $"Spawn no longer contains: {text}");
            Assert.AreEqual(-1, body.IndexOf(text, at + text.Length, StringComparison.Ordinal), $"Spawn contains more than one: {text}");
            return at;
        }

        /// <summary>One refusal branch: from <paramref name="start"/> to the first "return null;" after it, inclusive.</summary>
        private static string Branch(string body, int start)
        {
            var end = body.IndexOf("return null;", start, StringComparison.Ordinal);
            Assert.IsTrue(end > start, "the branch does not end in return null");
            return body.Substring(start, end + "return null;".Length - start);
        }

        /// <summary>The first "catch (Exception ex)" after <paramref name="marker"/>, as a branch.</summary>
        private static string CatchAfter(string body, string marker)
        {
            var at = body.IndexOf("catch (Exception ex)", IndexOnce(body, marker), StringComparison.Ordinal);
            Assert.IsTrue(at >= 0, $"no catch after: {marker}");
            return Branch(body, at);
        }

        [TestMethod]
        public void Every_spawn_refusal_after_the_create_destroys_the_creature()
        {
            var spawn = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(SpawnerPath), SpawnSignature);

            // Each branch is sliced on its own (anchor to its return null), so a Destroy in a later branch cannot satisfy it.
            var branches = new (string Name, string Text)[]
            {
                ("not a Creature", Branch(spawn, IndexOnce(spawn, "if (wo is not Creature creature)"))),
                ("not killable", Branch(spawn, IndexOnce(spawn, "if (!DungeonPopulationBuilder.IsKillable(creature.Attackable, creature.PlayerKillerStatus))"))),
                ("off the landblock", Branch(spawn, IndexOnce(spawn, "if (creature.Location.LandblockId.Landblock != lb.Id.Landblock)"))),
                ("shaping threw", CatchAfter(spawn, "Prepare(creature, plan, e, observedMaxNonBoss, rep, tag);")),
                ("EnterWorld refused", Branch(spawn, IndexOnce(spawn, "if (!creature.EnterWorld())"))),
                ("EnterWorld threw", CatchAfter(spawn, "EnterWorld refused wcid")),
            };

            foreach (var (name, text) in branches)
            {
                Assert.IsTrue(text.Contains(".Destroy();"), $"{name}: the branch must destroy what it created:\n{text}");
                Assert.AreEqual(1, Regex.Matches(text, @"return null;").Count, $"{name}: the slice is one branch");
            }

            Assert.AreEqual(6, branches.Select(b => b.Text).Distinct().Count(), "six distinct branches");
        }

        [TestMethod]
        public void Spawn_refuses_a_non_ephemeral_landblock_before_creating_anything()
        {
            var spawn = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(SpawnerPath), SpawnSignature);

            var gate = IndexOnce(spawn, "if (!lb.IsEphemeral)");
            var create = IndexOnce(spawn, "WorldObjectFactory.CreateNewWorldObject(e.Wcid)");
            var branch = Branch(spawn, gate);

            Assert.IsTrue(gate < create, "the ephemeral refusal comes before the create");
            Assert.IsTrue(gate + branch.Length < create, "the refusal returns before the create");
            Assert.IsFalse(branch.Contains("Destroy"), "nothing exists yet to destroy");
        }

        // ---- 6. observed pack max ---------------------------------------------------------------------------

        [TestMethod]
        public void ObservedMaxNonBoss_covers_every_non_boss_entry_and_destroys_each()
        {
            WithPrepareKeys(() =>
            {
                var plan = Plan();
                var created = new List<(uint Wcid, Creature Creature)>();

                Creature Create(uint wcid)
                {
                    var c = Make(wcid);
                    created.Add((wcid, c));
                    return c;
                }

                var max = TrialSpawner.ObservedMaxNonBoss(plan, Create, "trialtest");

                var nonBoss = plan.Entries.Where(e => e.Role != DungeonRole.Boss).Select(e => e.Wcid).ToList();
                CollectionAssert.AreEqual(nonBoss, created.Select(c => c.Wcid).ToList(), "one create per non-boss entry of the whole plan, in plan order, and never the boss");
                Assert.AreEqual(3, created.Count, "guard: the plan has three non-boss entries");

                foreach (var (wcid, c) in created)
                {
                    Assert.IsTrue(c.IsDestroyed, $"wcid {wcid:X} was destroyed");
                    Assert.IsNull(c.P_TrialRep, "the observation creatures belong to no rep");
                }

                // The expected max, from twins shaped the same way and never destroyed.
                var expected = plan.Entries.Where(e => e.Role != DungeonRole.Boss).Select(e =>
                {
                    var twin = Make(e.Wcid);
                    TrialSpawner.Prepare(twin, plan, e, 0, null, "trialtest");
                    return twin.Health.MaxValue;
                }).Max();

                Assert.IsTrue(expected > 0, "positive control: shaped non-boss health is non-zero");
                Assert.AreEqual(expected, max);
                Assert.IsTrue(max > Shapes[TrashWcid].Health, "guard: the shaped max reflects the plan's health multiplier, not the authored value");
            });
        }

        [TestMethod]
        public void ObservedMaxNonBoss_throws_on_a_null_create()
        {
            WithPrepareKeys(() =>
            {
                var plan = Plan();
                var created = new List<Creature>();

                // The second non-boss entry cannot be created: the call throws, and the creature made before it is destroyed.
                var ex = Assert.ThrowsExactly<InvalidOperationException>(() => TrialSpawner.ObservedMaxNonBoss(plan, w =>
                {
                    if (created.Count == 1)
                        return null;

                    var c = Make(w);
                    created.Add(c);
                    return c;
                }, "trialtest"));

                StringAssert.Contains(ex.Message, "could not be created");
                Assert.AreEqual(1, created.Count);
                Assert.IsTrue(created[0].IsDestroyed, "the creature created before the null was destroyed");
            });
        }

        [TestMethod]
        public void ObservedMaxNonBoss_surfaces_a_throwing_create_and_destroys_what_it_created()
        {
            WithPrepareKeys(() =>
            {
                var plan = Plan();
                var created = new List<Creature>();

                var ex = Assert.ThrowsExactly<ApplicationException>(() => TrialSpawner.ObservedMaxNonBoss(plan, w =>
                {
                    if (created.Count == 2)
                        throw new ApplicationException("probe create failure");

                    var c = Make(w);
                    created.Add(c);
                    return c;
                }, "trialtest"));

                Assert.AreEqual("probe create failure", ex.Message, "the create's own exception, not a wrapper and not a swallowed 0");
                Assert.AreEqual(2, created.Count);
                Assert.IsTrue(created.All(c => c.IsDestroyed), "every creature created before the throw was destroyed");
            });
        }

        [TestMethod]
        public void ObservedMaxNonBoss_surfaces_a_throwing_shaping_and_destroys_that_creature()
        {
            WithPrepareKeys(() =>
            {
                var plan = Plan();
                var created = new List<Creature>();

                // The second creature's biota lock is disposed, so Prepare's first property read throws ObjectDisposedException.
                Assert.ThrowsExactly<ObjectDisposedException>(() => TrialSpawner.ObservedMaxNonBoss(plan, w =>
                {
                    var c = Make(w);
                    created.Add(c);

                    if (created.Count == 2)
                        c.BiotaDatabaseLock.Dispose();

                    return c;
                }, "trialtest"));

                Assert.AreEqual(2, created.Count, "it stopped at the throwing shaping");
                Assert.IsTrue(created[0].IsDestroyed, "the creature measured before it was destroyed");
                Assert.IsTrue(created[1].IsDestroyed, "the creature whose shaping threw was destroyed");
            });
        }

        [TestMethod]
        public void ObservedMaxNonBoss_has_no_catch_and_destroys_in_finally()
        {
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(SpawnerPath),
                "internal static uint ObservedMaxNonBoss(DungeonSpawnPlan plan, Func<uint, Creature> create, string tag)");

            Assert.AreEqual(1, Regex.Matches(body, @"\bcatch\b").Count, "exactly one catch: the Destroy guard in finally");
            Assert.IsTrue(Regex.IsMatch(body, @"finally\s*\{\s*if \(c != null\)\s*\{\s*try \{ c\.Destroy\(\); \} catch \{ \}"), "finally destroys the creature");
        }
    }
}
