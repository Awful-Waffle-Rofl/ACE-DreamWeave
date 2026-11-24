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
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;
using ACE.Server.WorldEvents;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.Avatars.Gauntlet
{
    /// <summary>
    /// Allegiance Gauntlet spike 1B: can the Thread live plan builder build sane plans above the run ceiling (500)?
    ///
    /// Every case builds through <see cref="ThreadDungeonSpawner.BuildLivePlan"/> on a real outdoor site from the
    /// shipped store (<see cref="BuildLivePlanGoldenTests.OutdoorSiteId"/>), with the golden test's dat-free fixture
    /// sources (shipped species roster, synthetic profiles and per-hit, a seeded world cache), so nothing here needs
    /// client dats or a database. Each plan entry is then shaped through <see cref="ThreadDungeonSpawner.ShapeCreature"/>
    /// on a synthetic creature carrying the roster row's own level and health, exactly as TrialSpawner.Prepare shapes
    /// (Run null, Rewards true), and its final Health.MaxValue is recorded. Non-boss entries shape first so the boss sees
    /// the observed pack maximum, as the live populate does.
    ///
    /// Two passes: the THREAD curve (no limits adjustment; the health curve is flat above 500), and the GAUNTLET curve,
    /// built through BuildLivePlan's limits adjustment with the top level and the ceiling both at 1000
    /// (<see cref="GauntletAdjust"/>, Task 3's real ceiling). The spike's throwaway in-test patch of the plan fields is
    /// gone: the real ceiling also moves the archetype uplift's per-level health standards and the family stretch's
    /// standards, which the patch could not reach.
    ///
    /// PropertyManager: every compiled default is loaded before and after (the golden test's convention), which
    /// seeds and restores every key BuildLivePlan, ShapeCreature and the group dials read.
    /// </summary>
    [TestClass]
    public class GauntletPlanProbeTests
    {
        private static readonly int[] Levels = { 200, 500, 525, 750, 1000, 1500 };
        private static readonly int[] Headcounts = { 1, 20, 100 };

        private const int Tier = 8;
        private const int Seed = 424242;
        private const long MaxCreatureHealth = 2_000_000_000;
        private const int GauntletCurveTop = 1000;
        private const int TimingRepeats = 5;

        internal sealed record ProbeRow(int Level, int Headcount, bool Extended, string Threw, int Entries, bool AllFinite,
            long MaxShapedHealth, long MinShapedHealth, double MaxPlanHealthDouble, bool BossPresent, bool BossNoCandidate,
            long BossShapedHealth, double ElapsedTotalMs, double HealthCurveTarget, double NormalizeRatio, uint TrashFloor,
            double HpMul, double BossHpMul, int Stretched, int Uplifted, int Stamped, string Family)
        {
            public string Line() => string.Create(CultureInfo.InvariantCulture,
                $"L={Level,5} H={Headcount,3} ext={(Extended ? "Y" : "N")} threw={Threw ?? "no"} entries={Entries,3} finite={AllFinite} " +
                $"maxHp={MaxShapedHealth,13} minHp={MinShapedHealth,10} planMaxDbl={MaxPlanHealthDouble,16:0} boss={(BossPresent ? "yes" : "no")} bossNoCand={BossNoCandidate} " +
                $"bossHp={BossShapedHealth,13} ms={ElapsedTotalMs,8:0.00} curve={HealthCurveTarget:0} norm={NormalizeRatio:0.###} floor={TrashFloor} hpMul={HpMul:0.##} bossHpMul={BossHpMul:0.##} " +
                $"stretched={Stretched} uplift={Uplifted} stamp={Stamped} fam={Family}");
        }

        // ---- the 18-case probes ---------------------------------------------------------------------------------

        [TestMethod]
        [TestCategory("Slow")]
        public void Probe_thread_curve_all_levels_and_headcounts()
        {
            var rows = RunAll(extended: false);

            AssertGo(rows);

            // The current curve is flat above its top level (500): every case above 500 prices the same band-standard target
            // as level 500. Task 3 lifts that only through the Gauntlet limits adjustment, never on this path.
            foreach (var h in Headcounts)
            {
                var at500 = rows.Single(r => r.Level == 500 && r.Headcount == h).HealthCurveTarget;

                foreach (var r in rows.Where(r => r.Headcount == h && r.Level > 500))
                    Assert.AreEqual(at500, r.HealthCurveTarget, 1e-6, $"L{r.Level} H{h}: the Thread curve target must read the level-500 value");
            }
        }

        [TestMethod]
        [TestCategory("Slow")]
        public void Probe_gauntlet_curve_all_levels_and_headcounts()
        {
            var current = RunAll(extended: false);
            var rows = RunAll(extended: true);

            AssertGo(rows);

            // Saturate, never wrap. The patch only RAISES health inputs, so for the same case (same family, same seed) the
            // extended max can never land below the current one; a wrap through int or float would land far below it. A
            // plan-level target past int.MaxValue must shape to exactly int.MaxValue (saturated), never to something smaller.
            foreach (var r in rows.Where(r => r.Level > DungeonGemSpec.MaxRunLevel))
            {
                var cur = current.Single(c => c.Level == r.Level && c.Headcount == r.Headcount);

                Assert.AreEqual(cur.Family, r.Family, $"L{r.Level} H{r.Headcount}: the two passes must draw the same family");
                Assert.IsTrue(r.HealthCurveTarget > cur.HealthCurveTarget, $"L{r.Level} H{r.Headcount}: the Gauntlet ceiling must raise the curve target");
                Assert.IsTrue(r.MaxShapedHealth >= cur.MaxShapedHealth, $"L{r.Level} H{r.Headcount}: extended max {r.MaxShapedHealth} below current {cur.MaxShapedHealth} (wrap?)");
                Assert.IsTrue(r.MinShapedHealth >= cur.MinShapedHealth, $"L{r.Level} H{r.Headcount}: extended min {r.MinShapedHealth} below current {cur.MinShapedHealth} (wrap?)");

                if (r.MaxPlanHealthDouble > int.MaxValue)
                    Assert.AreEqual((long)int.MaxValue, r.MaxShapedHealth, $"L{r.Level} H{r.Headcount}: a target past int.MaxValue must saturate there");
            }

            // Positive control: the extreme case really does drive past int.MaxValue, so the saturation branch above ran.
            Assert.IsTrue(rows.Single(r => r.Level == 1500 && r.Headcount == 100).MaxPlanHealthDouble > int.MaxValue, "the extreme case must exceed int.MaxValue before the clamp");

            // The Gauntlet curve is flat above its own top level (1000), as the Thread curve is above 500.
            foreach (var h in Headcounts)
                Assert.AreEqual(rows.Single(r => r.Level == 1000 && r.Headcount == h).HealthCurveTarget,
                    rows.Single(r => r.Level == 1500 && r.Headcount == h).HealthCurveTarget, $"H{h}: the Gauntlet curve target above 1000 reads T(1000)");
        }

        private static void AssertGo(List<ProbeRow> rows)
        {
            Assert.AreEqual(Levels.Length * Headcounts.Length, rows.Count);

            var failures = new List<string>();

            foreach (var r in rows)
            {
                if (r.Threw != null) failures.Add($"L{r.Level} H{r.Headcount}: threw {r.Threw}");
                if (!r.AllFinite) failures.Add($"L{r.Level} H{r.Headcount}: a non-finite plan health");
                if (r.Entries <= 0) failures.Add($"L{r.Level} H{r.Headcount}: empty plan");
                if (r.MinShapedHealth < 1) failures.Add($"L{r.Level} H{r.Headcount}: a shaped health below 1 ({r.MinShapedHealth})");
                if (r.MaxShapedHealth > int.MaxValue) failures.Add($"L{r.Level} H{r.Headcount}: a shaped health above int.MaxValue ({r.MaxShapedHealth})");
                if (Math.Min(r.MaxShapedHealth, MaxCreatureHealth) > MaxCreatureHealth) failures.Add($"L{r.Level} H{r.Headcount}: above the clamp");
                if (!r.BossPresent && !r.BossNoCandidate) failures.Add($"L{r.Level} H{r.Headcount}: no boss entry and BossNoCandidate not set");
            }

            Assert.AreEqual(0, failures.Count, string.Join("\n", failures));
        }

        private static List<ProbeRow> RunAll(bool extended)
        {
            TestGameTables.EnsureInitialized();
            Assert.IsNotNull(ShippedRosterFixture.Tables, "species tables not found");

            var store = ThreadDungeonStoreTests.LoadShipped();
            Assert.IsTrue(store.Dungeons.TryGetValue(BuildLivePlanGoldenTests.OutdoorSiteId, out var site), "outdoor site missing from the shipped store");
            Assert.IsTrue(site.IsOutdoor, "the probe site must be an outdoor site");

            var seeded = new List<BuildLivePlanGoldenTests.SeededWeenie>();
            var rows = new List<ProbeRow>();

            try
            {
                DefaultPropertyManager.LoadDefaultProperties();
                BuildLivePlanGoldenTests.SeedWeenieCache(store, seeded);

                // JIT warm-up, discarded, so the first case's ElapsedTotalMs measures the build and not the JIT.
                ThreadPlanCache.Invalidate();
                Build(site, store, 500, GroupScaling.Solo, extended: false, out _);

                foreach (var level in Levels)
                    foreach (var h in Headcounts)
                        rows.Add(RunCase(site, store, level, h, extended));
            }
            finally
            {
                ThreadPlanCache.Invalidate();
                DefaultPropertyManager.LoadDefaultProperties();
                BuildLivePlanGoldenTests.UnseedWeenieCache(seeded);
            }

            Console.WriteLine($"---- Gauntlet plan probe ({(extended ? "Gauntlet curve top " + GauntletCurveTop : "Thread curve")}, site {site.Id}, tier {Tier}, seed {Seed}) ----");
            foreach (var r in rows)
                Console.WriteLine(r.Line());

            return rows;
        }

        private static DungeonSpawnPlan Build(DungeonEntryDef site, ThreadDungeonStore store, int level, GroupScaling group, bool extended, out ThreadDungeonSpawner.LivePlanBuildStats stats)
        {
            var spec = new DungeonGemSpec(site.Id, level, Tier, "any", Seed ^ level, Array.Empty<(string, double)>(), 0, 0);
            var sources = BuildLivePlanGoldenTests.FixtureSources(new BuildLivePlanGoldenTests.FixtureCalls(), store);

            return ThreadDungeonSpawner.BuildLivePlan(spec, site, group, sources, () => $"gauntlet probe L{level}", out stats,
                extended ? GauntletAdjust(GauntletCurveTop) : null);
        }

        /// <summary>The Gauntlet's limits adjustment as the spec states it: the health curve's top level and ceiling both set to <paramref name="top"/>.</summary>
        internal static Func<DungeonPopulationLimits, DungeonPopulationLimits> GauntletAdjust(int top) => limits => limits.WithHealthCurveTopLevel(top, top);

        /// <summary>The live Thread group dials, read exactly as ThreadDungeonManager.GroupTunables.Read reads them (that struct is private).</summary>
        internal static GroupScaling LiveGroupFor(int headcount) => GroupScaling.Compute(headcount,
            PropertyManager.GetDouble("dynamic_dungeons_group_effort_per_member", GroupScaling.DefaultEffortPerMember).Item,
            PropertyManager.GetDouble("dynamic_dungeons_group_count_share", GroupScaling.DefaultCountShare).Item,
            PropertyManager.GetLong("dynamic_dungeons_group_max_monsters_per_run", GroupScaling.DefaultGroupMaxMonsters).Item,
            PropertyManager.GetLong("dynamic_dungeons_group_damage_rating_per_member", GroupScaling.DefaultDamageRatingPerMember).Item,
            PropertyManager.GetLong("dynamic_dungeons_group_damage_rating_cap", GroupScaling.DefaultDamageRatingCap).Item,
            PropertyManager.GetDouble("dynamic_dungeons_group_reward_bonus_per_member", GroupScaling.DefaultRewardBonusPerMember).Item,
            PropertyManager.GetDouble("dynamic_dungeons_group_reward_bonus_cap", GroupScaling.DefaultRewardBonusCap).Item,
            PropertyManager.GetDouble("fellowship_share_group_plateau").Item,
            PropertyManager.GetLong("dynamic_dungeons_group_loot_hold_minutes", GroupScaling.DefaultLootHoldMinutes).Item);

        private static ProbeRow RunCase(DungeonEntryDef site, ThreadDungeonStore store, int level, int headcount, bool extended)
        {
            var group = LiveGroupFor(headcount);

            DungeonSpawnPlan plan = null;
            ThreadDungeonSpawner.LivePlanBuildStats stats = default;
            var timings = new List<double>();

            try
            {
                // TimingRepeats cold-memo builds of the same spec; the plan is deterministic per spec, the elapsed time is
                // the median (one noisy GC or JIT pause does not decide the row).
                for (var i = 0; i < TimingRepeats; i++)
                {
                    ThreadPlanCache.Invalidate();
                    plan = Build(site, store, level, group, extended, out stats);
                    timings.Add(stats.ElapsedTotalMs);
                }

                timings.Sort();
                stats = stats with { ElapsedTotalMs = timings[timings.Count / 2] };
            }
            catch (Exception ex)
            {
                return new ProbeRow(level, headcount, extended, $"{ex.GetType().Name}: {ex.Message}", 0, false, 0, 0, double.NaN, false, false, 0, 0,
                    double.NaN, double.NaN, 0, double.NaN, double.NaN, 0, 0, 0, null);
            }

            Assert.AreEqual(extended ? GauntletCurveTop : DungeonGemSpec.MaxRunLevel, stats.Limits.HealthCurveCeilingLevel, $"L{level} H{headcount}: the limits the build saw");

            string threw = null;
            var shaped = new List<(DungeonSpawnPlanEntry Entry, long Max)>();

            try
            {
                uint observedMaxNonBoss = 0;
                var options = new CreatureShapeOptions { Rewards = true, GroupEffort = group.Effort, LogTag = $"gauntlet probe L{level} H{headcount}" };

                foreach (var e in plan.Entries.Where(e => e.Role != DungeonRole.Boss).Concat(plan.Entries.Where(e => e.Role == DungeonRole.Boss)))
                {
                    var creature = MakeCreature(e.Wcid, level);
                    ThreadDungeonSpawner.ShapeCreature(creature, plan, e, observedMaxNonBoss, options);

                    var max = creature.Health.MaxValue;
                    shaped.Add((e, max));

                    if (e.Role != DungeonRole.Boss && max > observedMaxNonBoss)
                        observedMaxNonBoss = max;
                }
            }
            catch (Exception ex)
            {
                threw = $"ShapeCreature {ex.GetType().Name}: {ex.Message}";
            }

            var planDoubles = new[]
            {
                plan.HealthCurveTarget, plan.HealthNormalizeRatio, plan.HealthMultiplier, plan.BossHealthMultiplier, plan.BossHealthBase,
                plan.HealthCurveTarget * plan.HealthMultiplier, plan.BossHealthBase * plan.BossHealthMultiplier, (double)plan.TrashHealthFloor * plan.HealthMultiplier,
            };

            var boss = shaped.Where(s => s.Entry.Role == DungeonRole.Boss).Select(s => s.Max).DefaultIfEmpty(0).Max();

            return new ProbeRow(level, headcount, extended, threw, plan.Entries.Count,
                planDoubles.All(d => !double.IsNaN(d) && !double.IsInfinity(d)),
                shaped.Count == 0 ? 0 : shaped.Max(s => s.Max),
                shaped.Count == 0 ? 0 : shaped.Min(s => s.Max),
                planDoubles.Skip(5).Max(),
                plan.Entries.Any(e => e.Role == DungeonRole.Boss), plan.BossNoCandidate, boss, stats.ElapsedTotalMs,
                plan.HealthCurveTarget, plan.HealthNormalizeRatio, plan.TrashHealthFloor, plan.HealthMultiplier, plan.BossHealthMultiplier,
                plan.Entries.Count(e => e.Stretched), plan.Entries.Count(e => e.UpliftLevel > 0), plan.Entries.Count(e => e.StampLevel > 0), plan.FamilyId);
        }

        private static uint nextGuid = 0x7F1B0001;

        /// <summary>A bare creature with the roster row's level and health (Endurance 100 + level/3, so health = StartingValue + Endurance/2).</summary>
        internal static Creature MakeCreature(uint wcid, int fallbackLevel)
        {
            var level = ShippedRosterFixture.LevelOf(wcid);
            var health = ShippedRosterFixture.HealthOf(wcid);

            if (level <= 0) level = fallbackLevel;
            if (health == 0) health = 5000;

            var endurance = (uint)(100 + level / 3);
            var starting = health > endurance / 2 ? health - endurance / 2 : 1u;

            PropertiesBodyPart Part(int d, int a) => new PropertiesBodyPart
            {
                DType = DamageType.Slash, DVal = d, DVar = 0.4f, BaseArmor = a,
                ArmorVsSlash = a, ArmorVsPierce = a, ArmorVsBludgeon = a, ArmorVsCold = a, ArmorVsFire = a, ArmorVsAcid = a, ArmorVsElectric = a, ArmorVsNether = a,
            };

            var weenie = new Weenie
            {
                WeenieClassId = wcid,
                ClassName = $"gauntletprobe{wcid}",
                WeenieType = WeenieType.Creature,
                PropertiesInt = new Dictionary<PropertyInt, int> { [PropertyInt.Level] = level, [PropertyInt.XpOverride] = 1000 + level },
                PropertiesString = new Dictionary<PropertyString, string> { [PropertyString.Name] = $"Probe {wcid}" },
                PropertiesAttribute = new Dictionary<PropertyAttribute, PropertiesAttribute>
                {
                    [PropertyAttribute.Strength] = new PropertiesAttribute { InitLevel = (uint)(100 + level / 2) },
                    [PropertyAttribute.Endurance] = new PropertiesAttribute { InitLevel = endurance },
                    [PropertyAttribute.Coordination] = new PropertiesAttribute { InitLevel = (uint)(100 + level / 2) },
                    [PropertyAttribute.Quickness] = new PropertiesAttribute { InitLevel = (uint)(100 + level / 2) },
                    [PropertyAttribute.Focus] = new PropertiesAttribute { InitLevel = (uint)(100 + level / 3) },
                    [PropertyAttribute.Self] = new PropertiesAttribute { InitLevel = (uint)(100 + level / 3) },
                },
                PropertiesAttribute2nd = new Dictionary<PropertyAttribute2nd, PropertiesAttribute2nd>
                {
                    [PropertyAttribute2nd.MaxHealth] = new PropertiesAttribute2nd { InitLevel = starting },
                    [PropertyAttribute2nd.MaxStamina] = new PropertiesAttribute2nd { InitLevel = 200 },
                    [PropertyAttribute2nd.MaxMana] = new PropertiesAttribute2nd { InitLevel = 200 },
                },
                PropertiesSkill = new Dictionary<Skill, PropertiesSkill>
                {
                    [Skill.HeavyWeapons] = new PropertiesSkill { InitLevel = (uint)(level * 2), SAC = SkillAdvancementClass.Trained },
                    [Skill.MeleeDefense] = new PropertiesSkill { InitLevel = (uint)(level * 2), SAC = SkillAdvancementClass.Trained },
                    [Skill.MissileDefense] = new PropertiesSkill { InitLevel = (uint)(level * 2), SAC = SkillAdvancementClass.Trained },
                    [Skill.MagicDefense] = new PropertiesSkill { InitLevel = (uint)(level * 2), SAC = SkillAdvancementClass.Trained },
                    [Skill.Run] = new PropertiesSkill { InitLevel = 100, SAC = SkillAdvancementClass.Trained },
                },
                PropertiesBodyPart = new Dictionary<CombatBodyPart, PropertiesBodyPart>
                {
                    [CombatBodyPart.Head] = Part(10 + level / 4, 100 + level),
                    [CombatBodyPart.Chest] = Part(5 + level / 8, 200 + level),
                },
            };

            return new Creature(weenie, new ObjectGuid(nextGuid++));
        }

        // ---- dat-free pins on the saturate-or-wrap question -------------------------------------------------------

        /// <summary>
        /// The health write saturates at int.MaxValue rather than wrapping: ApplyHealth at int.MaxValue lands a MaxValue of
        /// int.MaxValue. CreatureVital.GetMaxValue sums in uint, multiplies in float and converts with (int)Math.Round, and
        /// float(2^31 - 1) rounds up to 2^31, one past int.MaxValue; on net10 that conversion saturates. On a runtime with
        /// the old x64 truncating conversion it would read int.MinValue and the min-vital floor would return 5.
        /// </summary>
        [TestMethod]
        public void ApplyHealth_at_int_max_saturates_rather_than_wraps()
        {
            TestGameTables.EnsureInitialized();

            var creature = MakeCreature(0x7F1BFFFF, 1500);
            WorldEventSpawner.ApplyHealth(creature, int.MaxValue);

            Assert.AreEqual((uint)int.MaxValue, creature.Health.MaxValue, "MaxValue at an int.MaxValue target");
            Assert.AreEqual(creature.Health.MaxValue, creature.Health.Current, "Current is refilled to MaxValue");

            // The Gauntlet clamp value is exactly reachable? Only to float precision: at 2e9 a float step is 128.
            var c2 = MakeCreature(0x7F1BFFFE, 1500);
            WorldEventSpawner.ApplyHealth(c2, (int)MaxCreatureHealth);
            Assert.IsTrue(Math.Abs((long)c2.Health.MaxValue - MaxCreatureHealth) <= 128, $"2e9 target landed at {c2.Health.MaxValue}");
        }

        /// <summary>The three health-target functions clamp into [1, int.MaxValue] for any extreme input (no wrap).</summary>
        [TestMethod]
        public void Health_targets_clamp_to_int_max_at_extreme_inputs()
        {
            Assert.AreEqual(int.MaxValue, ThreadDungeonSpawner.NonBossHealthTarget(uint.MaxValue, DungeonPopulationBuilder.MaxHealthNormalizeRatio, 1e6, uint.MaxValue));
            Assert.AreEqual(int.MaxValue, DungeonRewardMath.NormalizedBossHealthTarget(1e12, 1e6, uint.MaxValue));
            Assert.AreEqual(int.MaxValue, DungeonRewardMath.BossHealthTarget(uint.MaxValue, 1e6, uint.MaxValue, 1e6));
            Assert.AreEqual(uint.MaxValue, ThreadDungeonSpawner.NormalizedBase(uint.MaxValue, 25.0));
        }

        /// <summary>
        /// The clamps that key on 500 on every Thread path, which Task 3 leaves exactly as they were: the single-argument
        /// top-level sanitizer clamps into [375, 500] and the four-argument curve is flat above it; the gem constructor
        /// takes any level, the codec refuses above 500. Only the explicit ceiling forms lift the 500.
        /// </summary>
        [TestMethod]
        public void Level_500_clamps_as_they_stand_today()
        {
            Assert.AreEqual(500, DungeonGemSpec.MaxRunLevel);
            Assert.AreEqual(500, DungeonHealthCurve.SanitizeTopLevel(1000));
            Assert.AreEqual(DungeonHealthCurve.Target(500, DungeonHealthCurve.DefaultAnchorLow, DungeonHealthCurve.DefaultAnchorHigh),
                DungeonHealthCurve.Target(1500, DungeonHealthCurve.DefaultAnchorLow, DungeonHealthCurve.DefaultAnchorHigh, 1000));

            var spec = new DungeonGemSpec("ow_arwic", 1500, Tier, "any", 1, Array.Empty<(string, double)>(), 0, 0);
            Assert.AreEqual(1500, spec.Level, "the constructor does not clamp");
            Assert.IsFalse(DungeonGemSpec.TryParse(spec.Serialize(), out _, out var error), "the codec refuses a level above MaxRunLevel");
            StringAssert.Contains(error, "lvl");
        }
    }
}
