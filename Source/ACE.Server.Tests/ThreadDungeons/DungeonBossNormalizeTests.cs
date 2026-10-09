using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;
using ACE.Server.WorldEvents.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// Boss normalization (dynamic_dungeons_boss_normalize) and the boss-row selection changes that ship with
    /// it: health base and target, level stamp, XP, the any-family window, the weighted row draw, and the
    /// two-way spell tier. Everything here is pure; the spawner half is covered by DungeonCombatNormalizerTests
    /// and DungeonPlacementFallbackTests.
    /// </summary>
    [TestClass]
    public class DungeonBossNormalizeTests
    {
        // ---- fixture (the DungeonPopulationBuilderTests shape, with health data) -------------------------

        private static readonly Dictionary<uint, int> Levels = new Dictionary<uint, int>
        {
            [100] = 100, [102] = 105, [103] = 112, [110] = 110, [111] = 114, [10981] = 110, [50000] = 500, [60000] = 150,
        };

        private static int LevelOf(uint w) => Levels.TryGetValue(w, out var l) ? l : 0;

        // Deliberately lumpy: 103 is far tougher than the rest, so the pool maximum is a real term.
        private static readonly Dictionary<uint, uint> Health = new Dictionary<uint, uint>
        {
            [100] = 300, [102] = 250, [103] = 2400, [110] = 900, [111] = 700, [10981] = 800, [50000] = 90000, [60000] = 5000,
        };

        private static uint HealthOf(uint w) => Health.TryGetValue(w, out var h) ? h : 0;

        private static DungeonSpawnPointDef Pt(int i) => new DungeonSpawnPointDef { Cell = 0x01500100u + (uint)i, X = i, Y = 0, Z = 0, Clearance = 2, Curated = true };

        private static DungeonEntryDef Dungeon(int points = 12) => new DungeonEntryDef
        {
            Id = "filos_doom", Landblock = 0x0150, Name = "Filos' Doom", ExitPortalWcid = 1003601,
            Points = Enumerable.Range(1, points).Select(Pt).ToList(),
            BossAnchor = Pt(99),
            CreatureTypes = new List<string> { "Banderling" },
        };

        private static Dictionary<string, SpeciesTableDef> Species() => new Dictionary<string, SpeciesTableDef>
        {
            ["banderling"] = new SpeciesTableDef
            {
                Id = "banderling", CreatureType = "Banderling",
                Members = new List<SpeciesMemberDef>
                {
                    new SpeciesMemberDef { Wcid = 100, Role = 0 }, new SpeciesMemberDef { Wcid = 102, Role = 0 }, new SpeciesMemberDef { Wcid = 103, Role = 0 },
                    new SpeciesMemberDef { Wcid = 110, Role = 1 }, new SpeciesMemberDef { Wcid = 111, Role = 1 },
                }
            }
        };

        private const string Modifiers =
            "{\"modifiers\":[" +
            "{\"id\":\"hardy\",\"target\":\"monster\",\"minMagnitude\":1.3,\"maxMagnitude\":2.0,\"monsterEffectKind\":\"health_mult\"}," +
            "{\"id\":\"boss_guarded\",\"target\":\"boss\",\"minMagnitude\":1.5,\"maxMagnitude\":3.0,\"monsterEffectKind\":\"health_mult\"}]," +
            "\"xpLadder\":[{\"level\":20,\"xp\":3500},{\"level\":100,\"xp\":80000},{\"level\":200,\"xp\":1100000},{\"level\":500,\"xp\":9000000}]}";

        private static ThreadDungeonStore Store(string bossesJson = null) => ThreadDungeonStore.Parse(
            "{\"dungeons\":[]}",
            bossesJson ?? "{\"bosses\":[{\"wcid\":10981,\"name\":\"Aun Tanua\",\"level\":110,\"families\":[],\"modifiers\":[\"boss_guarded\"]}]}",
            Modifiers,
            new Dictionary<string, string>());

        // minMonsters defaults to 0, NOT DungeonPopulationLimits.DefaultMinMonsters (40): this file's fixtures
        // (Dungeon()'s 12 points) predate the min-monsters floor and are written one creature per point.
        private static DungeonPopulationLimits Limits(bool normalize = true, bool anyAllBands = true, double r = DungeonPopulationLimits.DefaultBossHealthBandRatio,
            double m = DungeonPopulationLimits.DefaultBossHealthPackMargin, double weight = DungeonPopulationLimits.DefaultBossFamilyRowWeight, int minMonsters = 0)
            => new DungeonPopulationLimits(120, 2.0, 3.0, 8, 0.5, rewardScalingEnabled: false,
                bossNormalize: normalize, bossHealthBandRatio: r, bossHealthPackMargin: m, bossAnyAllBands: anyAllBands, bossFamilyRowWeight: weight,
                minMonsters: minMonsters);

        private static DungeonGemSpec Spec(int level = 100, params (string, double)[] mods) => new DungeonGemSpec("filos_doom", level, 6, "any", 1, mods, 0, 0);

        private static DungeonSpawnPlan Build(DungeonPopulationLimits limits, ThreadDungeonStore store = null, int seed = 3, DungeonGemSpec spec = null)
            => DungeonPopulationBuilder.Build(spec ?? Spec(), Dungeon(), store ?? Store(), Species(), LevelOf, HealthOf, limits, new Random(seed));

        // ---- health --------------------------------------------------------------------------------------

        [TestMethod]
        public void Boss_base_is_the_larger_of_the_band_term_and_the_pack_term()
        {
            var plan = Build(Limits());
            var t = DungeonHealthCurve.Target(100, Limits());

            Assert.AreNotEqual(0u, plan.BossWcid);
            Assert.AreEqual(3.0 * t, plan.BossHealthBandTerm, 1e-9);
            Assert.AreEqual(1.25 * plan.PoolMaxBase, plan.BossHealthPackTerm, 1e-9);
            Assert.AreEqual(Math.Max(plan.BossHealthBandTerm, plan.BossHealthPackTerm), plan.BossHealthBase, 1e-9);
        }

        [TestMethod]
        public void Pool_max_base_covers_the_whole_pools_not_just_the_draw()
        {
            // One point: the draw fields a single creature, but PoolMaxBase is over every in-band pool member.
            var one = new DungeonEntryDef { Id = "filos_doom", Landblock = 0x0150, Points = new List<DungeonSpawnPointDef> { Pt(1) }, BossAnchor = Pt(99), CreatureTypes = new List<string> { "Banderling" } };
            var limits = Limits();

            var small = DungeonPopulationBuilder.Build(Spec(), one, Store(), Species(), LevelOf, HealthOf, limits, new Random(3));
            var full = Build(limits);

            Assert.AreEqual(full.PoolMaxBase, small.PoolMaxBase, "the boss must not depend on how many creatures were drawn");

            var expected = new uint[] { 100, 102, 103, 110, 111 }
                .Where(w => LevelOf(w) >= 100 && LevelOf(w) <= 115)
                .Select(w => (uint)ThreadDungeonSpawner.NonBossHealthTarget(HealthOf(w), full.HealthNormalizeRatio, 1.0, full.TrashHealthFloor))
                .Max();

            Assert.AreEqual(expected, full.PoolMaxBase);
        }

        [TestMethod]
        public void Hard_invariant_the_boss_strictly_outranks_every_non_boss_in_the_plan()
        {
            foreach (var mods in new[] { new (string, double)[0], new[] { ("hardy", 2.0) }, new[] { ("hardy", 1.3), ("boss_guarded", 3.0) } })
            {
                foreach (var r in new[] { 0.0, 1.0, 3.0 })
                {
                    for (var seed = 0; seed < 20; seed++)
                    {
                        var plan = Build(Limits(r: r), seed: seed, spec: Spec(100, mods));
                        var because = $"mods [{string.Join(",", mods.Select(x => x.Item1))}] R {r} seed {seed}";

                        Assert.IsTrue(plan.BossHealthMultiplier >= plan.HealthMultiplier - 1e-12, $"{because}: boss multiplier must not undercut the pack's");

                        var packMax = plan.Entries.Where(e => e.Role != DungeonRole.Boss)
                            .Select(e => ThreadDungeonSpawner.NonBossHealthTarget(HealthOf(e.Wcid), plan.HealthNormalizeRatio, plan.HealthMultiplier, plan.TrashHealthFloor))
                            .DefaultIfEmpty(0).Max();

                        // The plan-time figure alone, WITHOUT the spawner's observed-pack guard: the margin is what
                        // must carry the invariant on ordinary data.
                        var boss = (int)Math.Round(plan.BossHealthBase * plan.BossHealthMultiplier);

                        Assert.IsTrue(boss > packMax, $"{because}: boss {boss} must exceed every non-boss ({packMax})");
                    }
                }
            }
        }

        [TestMethod]
        public void Switch_off_reproduces_the_legacy_boss()
        {
            var plan = Build(Limits(normalize: false));

            Assert.IsFalse(plan.BossNormalize);
            Assert.AreEqual(0u, plan.PoolMaxBase);
            Assert.AreEqual(0.0, plan.BossHealthBase);
            Assert.AreEqual(3.0 * 1.0, Math.Max(3.0, 1.0), "sanity");
            Assert.IsTrue(plan.BossLevel >= LevelOf(plan.BossWcid), "legacy stamp keeps the authored-level floor");
        }

        [TestMethod]
        public void Normalized_target_is_the_base_times_the_multiplier_guarded_by_the_observed_pack()
        {
            Assert.AreEqual(15000, DungeonRewardMath.NormalizedBossHealthTarget(10000, 1.5, 4000));
            Assert.AreEqual(20001, DungeonRewardMath.NormalizedBossHealthTarget(10000, 1.5, 20000), "a CP-parked or odd-Endurance pack member above the plan still loses by 1");
            Assert.AreEqual(10000, DungeonRewardMath.NormalizedBossHealthTarget(10000, double.NaN, 0), "garbled multiplier reads as 1");
            Assert.AreEqual(1, DungeonRewardMath.NormalizedBossHealthTarget(0, 1.0, 0));
            Assert.AreEqual(int.MaxValue, DungeonRewardMath.NormalizedBossHealthTarget(1e12, 10, 0));
        }

        [TestMethod]
        public void Band_and_pack_terms_sanitize_their_dials()
        {
            Assert.AreEqual(0.0, DungeonRewardMath.BossHealthBandTerm(1000, 0), "R = 0 uses the pack term only");
            Assert.AreEqual(0.0, DungeonRewardMath.BossHealthBandTerm(0, 3), "no curve, no band term");
            Assert.AreEqual(3000.0, DungeonRewardMath.BossHealthBandTerm(1000, 3), 1e-9);

            Assert.AreEqual(0.0, DungeonRewardMath.SanitizePackMargin(0), "M = 0 uses the band term only");
            Assert.AreEqual(1.0, DungeonRewardMath.SanitizePackMargin(0.4), "a positive margin under 1 reads as 1 - the boss must outrank");
            Assert.AreEqual(1.25, DungeonRewardMath.SanitizePackMargin(1.25));
            Assert.AreEqual(0.0, DungeonRewardMath.SanitizePackMargin(double.NaN));
            Assert.AreEqual(2000.0, DungeonRewardMath.BossHealthPackTerm(2000, 0.4), 1e-9);
        }

        [TestMethod]
        public void Shipped_boss_rows_never_lower_the_health_multiplier()
        {
            var dir = FindDynamicContentDir();
            if (dir == null)
                Assert.Inconclusive("Content/dungeons/dynamic not found above the test output directory");

            var store = ThreadDungeonStore.Parse("{\"dungeons\":[]}", File.ReadAllText(Path.Combine(dir, "bosses.json")),
                File.ReadAllText(Path.Combine(dir, "modifiers.json")), new Dictionary<string, string>());

            var spec = new DungeonGemSpec("x", 300, 8, "any", 1, new[] { ("hardy", 2.0) }, 0, 0);
            var pack = DungeonRewardMath.HealthMultiplier(spec, store.Modifiers, Array.Empty<string>());

            foreach (var row in store.Bosses)
                Assert.IsTrue(DungeonRewardMath.HealthMultiplier(spec, store.Modifiers, row.Modifiers) >= pack,
                    $"boss row {row.Wcid} lowers the health multiplier below the pack's");

            foreach (var id in DungeonPopulationBuilder.PromotedBossModifiers)
                Assert.IsTrue(DungeonRewardMath.HealthMultiplier(spec, store.Modifiers, new[] { id }) >= pack, $"promoted modifier {id}");
        }

        // ---- level and XP ------------------------------------------------------------------------------

        [TestMethod]
        public void Normalizing_drops_the_authored_level_term_from_the_stamp()
        {
            var store = Store("{\"bosses\":[{\"wcid\":50000,\"name\":\"Huge\",\"level\":500,\"families\":[],\"modifiers\":[]}]}");

            var on = Build(Limits(), store);
            var off = Build(Limits(normalize: false, anyAllBands: false), store, spec: Spec(320));

            Assert.AreEqual(50000u, on.BossWcid, "any-family row is eligible at gem 100 while normalizing");
            // The pack term reads the PROJECTED level since the 2026-10-08 reach-up (this fixture's roster tops out
            // low enough that a level-100 gem sits above its own pivot), so the expectation projects too. The
            // authored-500 assertion below is unaffected: 500 is far above any projected pack level here.
            var packMax = on.Entries.Where(e => e.Role != DungeonRole.Boss)
                .Select(e => DungeonPopulationBuilder.ProjectLevel(LevelOf(e.Wcid), on.ReachUpScale)).DefaultIfEmpty(0).Max();
            Assert.IsTrue(on.BossLevel < 500, $"the authored 500 is gone (boss level {on.BossLevel})");
            Assert.AreEqual(Math.Max(packMax + 1, Math.Max(101, (int)Math.Ceiling(100 * DungeonPopulationLimits.DefaultBossLevelMargin - 1e-9))), on.BossLevel,
                "max(pack + 1, gem + 1, ceil(gem x margin)) - the authored 500 is gone");

            Assert.AreEqual(50000u, off.BossWcid, "control: a 320 gem's legacy window [320, 512] holds the row");
            Assert.AreEqual(500, off.BossLevel, "legacy stamp keeps the authored level");
        }

        [TestMethod]
        public void Boss_xp_prices_the_ladder_at_the_stamped_level_only_when_normalizing()
        {
            var ladder = Store().XpLadder;

            Assert.AreEqual(DungeonRewardMath.LadderXp(ladder, 150),
                DungeonPopulationBuilder.BaseXp(ladder, 9_000_000, 500, DungeonRole.Boss, 150, 0, bossLadderOnly: true),
                "the authored XpOverride is ignored");
            Assert.AreEqual(9_000_000L, DungeonPopulationBuilder.BaseXp(ladder, 9_000_000, 500, DungeonRole.Boss, 150), "legacy keeps the max");
            Assert.AreEqual(500_000L, DungeonPopulationBuilder.BaseXp(ladder, 500_000, 130, DungeonRole.Trash, 150, 0, bossLadderOnly: true),
                "non-boss roles ignore the flag");
        }

        // ---- any-family rows and the weighted draw ------------------------------------------------------

        [TestMethod]
        public void Any_family_rows_skip_the_window_only_with_both_switches_on_and_family_rows_keep_it()
        {
            var store = Store("{\"bosses\":[" +
                "{\"wcid\":50000,\"name\":\"Huge\",\"level\":500,\"families\":[],\"modifiers\":[]}," +
                "{\"wcid\":60000,\"name\":\"Fam\",\"level\":500,\"families\":[\"banderling\"],\"modifiers\":[]}]}");

            Assert.AreEqual(50000u, Build(Limits(), store).BossWcid, "only the any-family row may skip the window");
            Assert.AreNotEqual(50000u, Build(Limits(anyAllBands: false), store).BossWcid, "any_all_bands off: window applies, the family promotes");
            Assert.AreNotEqual(50000u, Build(Limits(normalize: false), store).BossWcid, "normalize off: window applies");
        }

        [TestMethod]
        public void Family_rows_are_weighted_up_and_a_seed_reproduces_its_draw()
        {
            var rows = new List<BossEntryDef>
            {
                new BossEntryDef { Wcid = 3, Level = 100, Families = new List<string>(), Modifiers = new List<string>() },
                new BossEntryDef { Wcid = 1, Level = 100, Families = new List<string> { "banderling" }, Modifiers = new List<string>() },
                new BossEntryDef { Wcid = 2, Level = 100, Families = null, Modifiers = new List<string>() },
            };

            var familyHits = 0;
            const int draws = 6000;

            for (var seed = 0; seed < draws; seed++)
            {
                var pick = DungeonPopulationBuilder.PickBossRow(rows, "banderling", 5.0, new Random(seed));
                if (pick.Wcid == 1) familyHits++;

                Assert.AreSame(pick, DungeonPopulationBuilder.PickBossRow(rows, "banderling", 5.0, new Random(seed)), "same seed, same row");
            }

            // Expected share 5 / (5 + 1 + 1) = 0.714.
            Assert.IsTrue(familyHits > draws * 0.68 && familyHits < draws * 0.75, $"family row share {familyHits}/{draws}");
        }

        [TestMethod]
        public void A_weight_of_one_is_the_legacy_uniform_draw_byte_for_byte()
        {
            var rows = Enumerable.Range(0, 5).Select(i => new BossEntryDef { Wcid = (uint)(50 - i), Families = new List<string> { "banderling" } }).ToList();

            for (var seed = 0; seed < 50; seed++)
            {
                var legacy = rows[new Random(seed).Next(rows.Count)];

                Assert.AreSame(legacy, DungeonPopulationBuilder.PickBossRow(rows, "banderling", 1.0, new Random(seed)));
                Assert.AreSame(legacy, DungeonPopulationBuilder.PickBossRow(rows, "banderling", 0.3, new Random(seed)), "below 1 reads as 1");
                Assert.AreSame(legacy, DungeonPopulationBuilder.PickBossRow(rows, "banderling", double.NaN, new Random(seed)));
            }

            Assert.IsNull(DungeonPopulationBuilder.PickBossRow(new List<BossEntryDef>(), "x", 5, new Random(1)));
        }

        // ---- two-way spell tier ------------------------------------------------------------------------

        [TestMethod]
        public void SetTier_lowers_a_higher_tier_and_raises_a_lower_one()
        {
            var book = new Dictionary<int, float>
            {
                [(int)SpellId.ImperilOther8] = 0.4f,
                [(int)SpellId.CantripHermeticLink1] = 0.1f,
            };

            Assert.AreEqual(0, DungeonSpellTier.Raise(new Dictionary<int, float>(book), 5), "control: Raise never lowers");

            Assert.AreEqual(1, DungeonSpellTier.SetTier(book, 5));
            Assert.AreEqual(0.4f, book[(int)SpellId.ImperilOther5]);
            Assert.IsFalse(book.ContainsKey((int)SpellId.ImperilOther8));
            Assert.AreEqual(0.1f, book[(int)SpellId.CantripHermeticLink1], "non-tierable id untouched");

            var low = new Dictionary<int, float> { [(int)SpellId.ImperilOther5] = 0.2f };
            Assert.AreEqual(1, DungeonSpellTier.SetTier(low, 8));
            Assert.AreEqual(0.2f, low[(int)SpellId.ImperilOther8]);
        }

        [TestMethod]
        public void SetTier_collisions_collapse_keeping_the_first_position_and_the_highest_probability()
        {
            var book = new Dictionary<int, float>
            {
                [(int)SpellId.ImperilOther8] = 0.1f,
                [(int)SpellId.CantripHermeticLink1] = 0.5f,
                [(int)SpellId.ImperilOther5] = 0.3f,
            };

            DungeonSpellTier.SetTier(book, 5);

            CollectionAssert.AreEqual(new[] { (int)SpellId.ImperilOther5, (int)SpellId.CantripHermeticLink1 }, book.Keys.ToArray());
            Assert.AreEqual(0.3f, book[(int)SpellId.ImperilOther5]);
            Assert.AreEqual(0, DungeonSpellTier.SetTier(book, 0), "tier 0 is a no-op");
        }

        // ---- tunable registrations ---------------------------------------------------------------------

        /// <summary>
        /// Owner ruling 2026-09-13: every new dial SHIPS ON at the spec value, and the compiled const, the
        /// PropertyManager registration and the spawner's read fallback must carry that one value. The spawner's
        /// reads pass the const itself as the fallback, so this pins the remaining pair.
        /// </summary>
        [TestMethod]
        public void Every_new_registration_default_equals_its_shipped_const()
        {
            var bools = new Dictionary<string, bool>
            {
                ["dynamic_dungeons_boss_normalize"] = DungeonPopulationLimits.DefaultBossNormalize,
                ["dynamic_dungeons_strip_combat_traits"] = DungeonPopulationLimits.DefaultStripCombatTraits,
                ["dynamic_dungeons_boss_any_all_bands"] = DungeonPopulationLimits.DefaultBossAnyAllBands,
            };

            var doubles = new Dictionary<string, double>
            {
                ["dynamic_dungeons_boss_health_band_ratio"] = DungeonPopulationLimits.DefaultBossHealthBandRatio,
                ["dynamic_dungeons_boss_health_pack_margin"] = DungeonPopulationLimits.DefaultBossHealthPackMargin,
                ["dynamic_dungeons_boss_offense_band_ratio"] = DungeonPopulationLimits.DefaultBossOffenseBandRatio,
                ["dynamic_dungeons_boss_defense_band_ratio"] = DungeonPopulationLimits.DefaultBossDefenseBandRatio,
                ["dynamic_dungeons_category_resist_floor"] = DungeonPopulationLimits.DefaultCategoryResistFloor,
                ["dynamic_dungeons_category_armor_mod_ceiling"] = DungeonPopulationLimits.DefaultCategoryArmorModCeiling,
                ["dynamic_dungeons_defense_skill_cap_offset"] = DungeonPopulationLimits.DefaultDefenseSkillCapOffset,
                ["dynamic_dungeons_boss_family_row_weight"] = DungeonPopulationLimits.DefaultBossFamilyRowWeight,
            };

            foreach (var kvp in bools)
            {
                Assert.IsTrue(ACE.Server.Managers.DefaultPropertyManager.DefaultBooleanProperties.TryGetValue(kvp.Key, out var p), $"{kvp.Key} is not registered");
                Assert.AreEqual(kvp.Value, p.Item, kvp.Key);
                Assert.IsTrue(p.Item, $"{kvp.Key} must ship ON");
            }

            foreach (var kvp in doubles)
            {
                Assert.IsTrue(ACE.Server.Managers.DefaultPropertyManager.DefaultDoubleProperties.TryGetValue(kvp.Key, out var p), $"{kvp.Key} is not registered");
                Assert.AreEqual(kvp.Value, p.Item, 1e-12, kvp.Key);
                Assert.IsTrue(p.Item > 0, $"{kvp.Key} must not ship at 0 (off)");
            }

            Assert.IsTrue(ACE.Server.Managers.DefaultPropertyManager.DefaultLongProperties.TryGetValue("dynamic_dungeons_placement_fallback_attempts", out var attempts));
            Assert.AreEqual((long)DungeonPopulationLimits.DefaultPlacementFallbackAttempts, attempts.Item);
            Assert.AreEqual(5, DungeonPopulationLimits.DefaultPlacementFallbackAttempts, "spec value");

            // The spec values themselves, so a const edit cannot silently move the shipped behaviour.
            Assert.AreEqual(3.0, DungeonPopulationLimits.DefaultBossHealthBandRatio);
            Assert.AreEqual(1.25, DungeonPopulationLimits.DefaultBossHealthPackMargin);
            Assert.AreEqual(1.0, DungeonPopulationLimits.DefaultBossOffenseBandRatio);
            Assert.AreEqual(1.0, DungeonPopulationLimits.DefaultBossDefenseBandRatio);
            Assert.AreEqual(0.5, DungeonPopulationLimits.DefaultCategoryResistFloor);
            Assert.AreEqual(2.0, DungeonPopulationLimits.DefaultCategoryArmorModCeiling);
            Assert.AreEqual(100.0, DungeonPopulationLimits.DefaultDefenseSkillCapOffset);
            Assert.AreEqual(5.0, DungeonPopulationLimits.DefaultBossFamilyRowWeight);
        }

        [TestMethod]
        public void Limits_built_with_defaults_ship_every_new_feature_on()
        {
            var limits = new DungeonPopulationLimits(120, 2.0, 3.0, 8, 0.5);

            Assert.IsTrue(limits.BossNormalize);
            Assert.IsTrue(limits.StripCombatTraits);
            Assert.IsTrue(limits.BossAnyAllBands);
            Assert.AreEqual(DungeonPopulationLimits.DefaultBossHealthBandRatio, limits.BossHealthBandRatio);
            Assert.AreEqual(DungeonPopulationLimits.DefaultBossHealthPackMargin, limits.BossHealthPackMargin);
            Assert.AreEqual(DungeonPopulationLimits.DefaultBossFamilyRowWeight, limits.BossFamilyRowWeight);
            Assert.AreEqual(DungeonPopulationLimits.DefaultDefenseSkillCapOffset, limits.DefenseSkillCapOffset);

            // The 2026-10-08 run ceiling raise: all four ship on (literals, not the Default* constants, so a
            // changed default fails here rather than constant-folding to true).
            Assert.IsTrue(limits.ReachUp);
            Assert.IsTrue(limits.StatCurve);
            Assert.AreEqual(500, limits.HealthCurveTopLevel);
            Assert.AreEqual(0.5, limits.DefenseCurveRateAbove375);
        }

        /// <summary>
        /// Code review finding (eb14fab27): the plan's own sanitize step mapped a garbled (NaN/Infinity)
        /// DefenseSkillCapOffset to 0.0, which is a VALID explicit cap ("exactly at the band's effective
        /// median") for this axis, not "disabled" or "garbage" - it silently turned a garbled dial into the
        /// harshest possible cap instead of the documented "reads as the default" fallback. Only a genuinely
        /// negative value is this axis's own disable.
        /// </summary>
        [TestMethod]
        public void A_garbled_defense_skill_cap_offset_reads_as_the_default_not_zero()
        {
            var nanLimits = new DungeonPopulationLimits(120, 2.0, 3.0, 8, 0.5, defenseSkillCapOffset: double.NaN);
            var infLimits = new DungeonPopulationLimits(120, 2.0, 3.0, 8, 0.5, defenseSkillCapOffset: double.PositiveInfinity);

            var nanPlan = DungeonPopulationBuilder.Build(Spec(), Dungeon(), Store(), Species(), LevelOf, HealthOf, nanLimits, new Random(3));
            var infPlan = DungeonPopulationBuilder.Build(Spec(), Dungeon(), Store(), Species(), LevelOf, HealthOf, infLimits, new Random(3));

            Assert.AreEqual(DungeonPopulationLimits.DefaultDefenseSkillCapOffset, nanPlan.DefenseSkillCapOffset, "NaN reads as the default");
            Assert.AreEqual(DungeonPopulationLimits.DefaultDefenseSkillCapOffset, infPlan.DefenseSkillCapOffset, "Infinity reads as the default");

            // A genuinely negative value is this axis's own disable and must NOT be pulled up to the default.
            var negativeLimits = new DungeonPopulationLimits(120, 2.0, 3.0, 8, 0.5, defenseSkillCapOffset: -1.0);
            var negativePlan = DungeonPopulationBuilder.Build(Spec(), Dungeon(), Store(), Species(), LevelOf, HealthOf, negativeLimits, new Random(3));
            Assert.AreEqual(-1.0, negativePlan.DefenseSkillCapOffset, "a negative offset disables the axis and passes through unchanged");
        }

        // ---- helpers -----------------------------------------------------------------------------------

        private static string FindDynamicContentDir()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "Content", "dungeons", "dynamic");

                if (File.Exists(Path.Combine(candidate, "bosses.json")))
                    return candidate;

                dir = dir.Parent;
            }

            return null;
        }
    }
}
