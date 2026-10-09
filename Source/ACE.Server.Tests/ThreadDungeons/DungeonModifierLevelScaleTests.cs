using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;
using ACE.Server.WorldEvents.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The modifier magnitude curve below level 185 (DungeonModifierLevelScale): s(L) = min(1, (L / 185)^k),
    /// k = dynamic_dungeons_modifier_level_exponent (1.76). Pins the s table, every axis transform, the wiring
    /// into DungeonRewardMath / DungeonPopulationBuilder / the gem's description, and - the discriminating
    /// half - that a level-185 gem builds EXACTLY the plan it built before the curve existed.
    /// </summary>
    [TestClass]
    public class DungeonModifierLevelScaleTests
    {
        private const double K = DungeonModifierLevelScale.DefaultExponent;

        /// <summary>The formula, restated independently of the production code.</summary>
        private static double Formula(int level, double k) => Math.Min(1.0, Math.Pow(level / 185.0, k));

        private static double S(int level) => DungeonModifierLevelScale.Factor(level, K);

        // ---- the factor -------------------------------------------------------------------------------------

        [TestMethod]
        public void The_anchor_is_185()
        {
            // The shipped exponent (1.76) is pinned against the live registration in
            // The_tunable_is_registered_at_the_compiled_default, not as a constant-vs-constant compare here.
            Assert.AreEqual(185, DungeonModifierLevelScale.AnchorLevel,
                "the anchor reads DungeonGemFactory.RungBaseLevel; a rung added below 185 must fail here, not move the curve");
        }

        /// <summary>
        /// The owner-approved table, to 3 decimals, asserted twice: against the formula computed here and
        /// against the literal values the design was approved with.
        /// </summary>
        [TestMethod]
        public void The_s_table_matches_the_formula_and_the_approved_literals()
        {
            var table = new (int Level, double S)[]
            {
                (50, 0.100), (75, 0.204), (100, 0.339), (125, 0.502), (150, 0.691), (175, 0.907), (185, 1.000),
            };

            foreach (var (level, literal) in table)
            {
                Assert.AreEqual(Formula(level, K), S(level), 1e-12, $"formula at {level}");
                Assert.AreEqual(literal, Math.Round(S(level), 3), 1e-12, $"approved literal at {level}");
            }
        }

        [TestMethod]
        public void S_is_exactly_one_at_and_above_the_anchor()
        {
            foreach (var level in new[] { 185, 186, 200, 275, 300, 375 })
                Assert.AreEqual(1.0, S(level), $"level {level}");
        }

        [TestMethod]
        public void A_non_positive_or_garbled_exponent_turns_the_curve_off()
        {
            foreach (var k in new[] { 0.0, -1.0, -0.001, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            {
                Assert.AreEqual(1.0, DungeonModifierLevelScale.Factor(50, k), $"k = {k}");
                Assert.AreEqual(1.0, DungeonModifierLevelScale.Factor(1, k), $"k = {k}");
            }
        }

        [TestMethod]
        public void S_is_monotone_below_the_anchor_and_zero_for_a_non_positive_level()
        {
            var previous = 0.0;
            for (var level = 1; level <= 185; level++)
            {
                var s = S(level);
                Assert.IsTrue(s > previous && s <= 1.0, $"level {level}: {s}");
                previous = s;
            }

            Assert.AreEqual(0.0, S(0));
            Assert.AreEqual(0.0, S(-5));
        }

        // ---- the axis transforms ----------------------------------------------------------------------------

        [TestMethod]
        public void Additive_axes_are_magnitude_times_s()
        {
            var s = S(50);

            foreach (var kind in new[] { DungeonRewardMath.DamageRating, DungeonRewardMath.CritRating, DungeonRewardMath.CritDamageRating,
                DungeonRewardMath.DamageResistRating, DungeonRewardMath.IgnoreShield, DungeonRewardMath.Hollow })
            {
                Assert.AreEqual(40 * s, DungeonModifierLevelScale.ScaleMagnitude(kind, 40, s), 1e-12, kind);
                Assert.AreEqual(0.6 * s, DungeonModifierLevelScale.ScaleMagnitude(kind, 0.6, s), 1e-12, kind);
            }

            Assert.AreEqual(40 * s, DungeonModifierLevelScale.ScaleAdditive(40, s), 1e-12);
        }

        [TestMethod]
        public void Multiplier_axes_are_pulled_toward_one()
        {
            var s = S(50);

            foreach (var kind in new[] { DungeonRewardMath.HealthMult, DungeonRewardMath.RunSpeedMult, DungeonRewardMath.CountMult })
            {
                Assert.AreEqual(1 + s * (2.0 - 1), DungeonModifierLevelScale.ScaleMagnitude(kind, 2.0, s), 1e-12, kind);
                Assert.AreEqual(1 + s * (1.3 - 1), DungeonModifierLevelScale.ScaleMagnitude(kind, 1.3, s), 1e-12, kind);
            }

            // An invalid (non-positive or NaN) multiplier stays invalid, so its consumer still skips/resets it.
            Assert.AreEqual(0.0, DungeonModifierLevelScale.ScaleMagnitude(DungeonRewardMath.HealthMult, 0.0, s));
            Assert.AreEqual(-1.0, DungeonModifierLevelScale.ScaleMagnitude(DungeonRewardMath.CountMult, -1.0, s));
            Assert.IsTrue(double.IsNaN(DungeonModifierLevelScale.ScaleMagnitude(DungeonRewardMath.RunSpeedMult, double.NaN, s)));
        }

        [TestMethod]
        public void Elite_share_is_pulled_toward_the_default_share()
        {
            var s = S(50);
            var neutral = DungeonPopulationBuilder.DefaultEliteShare;

            Assert.AreEqual(0.15, neutral, "the neutral is the existing default elite share");
            Assert.AreEqual(neutral + s * (0.5 - neutral), DungeonModifierLevelScale.ScaleMagnitude(DungeonRewardMath.EliteShare, 0.5, s), 1e-12);
            Assert.AreEqual(neutral + s * (0.25 - neutral), DungeonModifierLevelScale.ScaleEliteShare(0.25, s), 1e-12);
            Assert.AreEqual(neutral, DungeonModifierLevelScale.ScaleEliteShare(0.5, 0.0), 1e-12, "s = 0 is exactly the default share");
        }

        [TestMethod]
        public void Reward_factors_are_pulled_toward_one()
        {
            var s = S(50);

            Assert.AreEqual(1 + s * 0.25, DungeonModifierLevelScale.ScaleRewardFactor(1.25, s), 1e-12);
            Assert.AreEqual(1 + s * 0.5, DungeonModifierLevelScale.ScaleRewardFactor(1.5, s), 1e-12);
            Assert.AreEqual(1 - s * 0.5, DungeonModifierLevelScale.ScaleRewardFactor(0.5, s), 1e-12, "a factor below 1 rises toward 1, never past it");
        }

        [TestMethod]
        public void Loot_quantity_salvage_affinity_and_unknown_kinds_are_never_scaled()
        {
            var s = S(50);

            Assert.AreEqual(1.4, DungeonModifierLevelScale.ScaleMagnitude(DungeonRewardMath.LootQuantity, 1.4, s));
            Assert.AreEqual(20.0, DungeonModifierLevelScale.ScaleMagnitude(DungeonRewardMath.SalvageAffinity, 20.0, s));
            Assert.AreEqual(1.5, DungeonModifierLevelScale.ScaleMagnitude("none", 1.5, s));
            Assert.AreEqual(7.0, DungeonModifierLevelScale.ScaleMagnitude("not_a_kind", 7.0, s));
            Assert.AreEqual(7.0, DungeonModifierLevelScale.ScaleMagnitude(null, 7.0, s));
        }

        /// <summary>
        /// At s = 1 every transform returns its input bit for bit - 1 + 1 x (m - 1) is not guaranteed to, so
        /// the transforms short-circuit. This is what makes "185 and above behave exactly as today" hold in
        /// floating point, not just to a tolerance.
        /// </summary>
        [TestMethod]
        public void Every_transform_is_bit_identical_at_s_one()
        {
            foreach (var m in new[] { 1.1, 1.3, 1.72, 0.26, 0.15000000000000002, 37.24, 0.1 + 0.2 })
            {
                Assert.AreEqual(m, DungeonModifierLevelScale.ScaleMultiplier(m, 1.0));
                Assert.AreEqual(m, DungeonModifierLevelScale.ScaleEliteShare(m, 1.0));
                Assert.AreEqual(m, DungeonModifierLevelScale.ScaleRewardFactor(m, 1.0));
                Assert.AreEqual(m, DungeonModifierLevelScale.ScaleAdditive(m, 1.0));
                Assert.AreEqual(m, DungeonModifierLevelScale.ScaleMagnitude(DungeonRewardMath.HealthMult, m, 1.0));
                Assert.AreEqual(m, DungeonModifierLevelScale.ScaleMagnitude(DungeonRewardMath.EliteShare, m, 1.0));
            }

            Assert.AreEqual(1.3, DungeonModifierLevelScale.ScaleMultiplier(1.3, double.NaN), "a garbled s never shrinks a modifier");
        }

        // ---- DungeonRewardMath with a level scale --------------------------------------------------------------

        private static Dictionary<string, ModifierDef> Defs() => new Dictionary<string, ModifierDef>
        {
            ["hardy"] = new ModifierDef { Id = "hardy", MonsterEffectKind = DungeonRewardMath.HealthMult, RewardXpKind = "linear", RewardXpBase = 1.0, RewardXpSlope = 0.25 },
            ["savage"] = new ModifierDef { Id = "savage", MonsterEffectKind = DungeonRewardMath.DamageRating },
            ["savage2"] = new ModifierDef { Id = "savage2", MonsterEffectKind = DungeonRewardMath.DamageRating },
            ["enlightened"] = new ModifierDef { Id = "enlightened", MonsterEffectKind = "none", RewardXpKind = "linear", RewardXpBase = 0.0, RewardXpSlope = 1.0 },
            ["champions"] = new ModifierDef { Id = "champions", Target = "run", MonsterEffectKind = DungeonRewardMath.EliteShare },
        };

        private static DungeonGemSpec Spec(int level, params (string, double)[] mods) => new DungeonGemSpec("filos_doom", level, 6, "any", 1, mods, 0, 0);

        /// <summary>The design's example table: Savage 10..40, Hardy 1.3..2.0, Enlightened 1.25..1.5.</summary>
        [TestMethod]
        public void The_approved_examples_at_level_50_and_185()
        {
            var defs = Defs();

            Assert.AreEqual(1, DungeonRewardMath.RatingTotal(Spec(50, ("savage", 10)), defs, null, DungeonRewardMath.DamageRating, levelScale: S(50)));
            Assert.AreEqual(4, DungeonRewardMath.RatingTotal(Spec(50, ("savage", 40)), defs, null, DungeonRewardMath.DamageRating, levelScale: S(50)));
            Assert.AreEqual(10, DungeonRewardMath.RatingTotal(Spec(185, ("savage", 10)), defs, null, DungeonRewardMath.DamageRating, levelScale: S(185)));
            Assert.AreEqual(40, DungeonRewardMath.RatingTotal(Spec(185, ("savage", 40)), defs, null, DungeonRewardMath.DamageRating, levelScale: S(185)));

            Assert.AreEqual(1.03, DungeonRewardMath.HealthMultiplier(Spec(50, ("hardy", 1.3)), defs, null, levelScale: S(50)), 1e-4);
            Assert.AreEqual(1.10, DungeonRewardMath.HealthMultiplier(Spec(50, ("hardy", 2.0)), defs, null, levelScale: S(50)), 1e-4);
            Assert.AreEqual(1.3, DungeonRewardMath.HealthMultiplier(Spec(185, ("hardy", 1.3)), defs, null, levelScale: S(185)));
            Assert.AreEqual(2.0, DungeonRewardMath.HealthMultiplier(Spec(185, ("hardy", 2.0)), defs, null, levelScale: S(185)));

            Assert.AreEqual(1.025, DungeonRewardMath.XpMultiplier(Spec(50, ("enlightened", 1.25)), defs, 100, S(50)), 1e-4);
            Assert.AreEqual(1.05, DungeonRewardMath.XpMultiplier(Spec(50, ("enlightened", 1.5)), defs, 100, S(50)), 1e-4);
            Assert.AreEqual(1.25, DungeonRewardMath.XpMultiplier(Spec(185, ("enlightened", 1.25)), defs, 100, S(185)));
            Assert.AreEqual(1.5, DungeonRewardMath.XpMultiplier(Spec(185, ("enlightened", 1.5)), defs, 100, S(185)));
        }

        /// <summary>
        /// Ratings are scaled per modifier, SUMMED, then rounded once - never rounded per modifier. Two
        /// savages of 14 at level 50 are 1.39989 each: summed then rounded is 3, rounded first would be 2.
        /// </summary>
        [TestMethod]
        public void Ratings_are_summed_before_they_are_rounded()
        {
            var s = S(50);
            var total = DungeonRewardMath.RatingTotal(Spec(50, ("savage", 14), ("savage2", 14)), Defs(), null, DungeonRewardMath.DamageRating, levelScale: s);

            Assert.AreEqual((int)Math.Round(28 * s), total);
            Assert.AreEqual(3, total);
            Assert.AreNotEqual(2 * (int)Math.Round(14 * s), total, "guard: per-modifier rounding gives a different number");
        }

        [TestMethod]
        public void Each_xp_factor_is_scaled_before_the_product_and_the_cap()
        {
            var s = S(50);
            var spec = Spec(50, ("hardy", 2.0), ("enlightened", 1.5));

            var expected = (1 + s * 0.5) * (1 + s * 0.5);
            Assert.AreEqual(expected, DungeonRewardMath.XpMultiplier(spec, Defs(), 100, s), 1e-12);
            Assert.AreEqual(1.02, DungeonRewardMath.XpMultiplier(spec, Defs(), 1.02, s), 1e-12, "the cap applies to the scaled product");
        }

        [TestMethod]
        public void RunValue_scales_a_found_magnitude_and_never_the_fallback()
        {
            var s = S(50);
            var defs = Defs();

            Assert.AreEqual(0.15 + s * 0.35, DungeonRewardMath.RunValue(Spec(50, ("champions", 0.5)), defs, DungeonRewardMath.EliteShare, DungeonPopulationBuilder.DefaultEliteShare, s), 1e-12);
            Assert.AreEqual(0.42, DungeonRewardMath.RunValue(Spec(50), defs, DungeonRewardMath.EliteShare, 0.42, s), "the fallback is the no-modifier value already");
            Assert.AreEqual(0.5, DungeonRewardMath.RunValue(Spec(50, ("champions", 0.5)), defs, DungeonRewardMath.EliteShare, DungeonPopulationBuilder.DefaultEliteShare),
                "the default levelScale (1.0) is the pre-curve behaviour");
        }

        // ---- the builder: discrimination ----------------------------------------------------------------------

        private static readonly Dictionary<uint, int> Levels = new Dictionary<uint, int>
        {
            [100] = 50, [102] = 52, [110] = 54, [200] = 185, [202] = 190, [210] = 195, [10981] = 110,
        };

        private static int LevelOf(uint w) => Levels.TryGetValue(w, out var l) ? l : 0;

        private static uint HealthOf(uint w) => 1000;

        private static DungeonSpawnPointDef Pt(int i) => new DungeonSpawnPointDef { Cell = 0x01500100u + (uint)i, X = i, Y = 0, Z = 0, Clearance = 2, Curated = true };

        private static DungeonEntryDef Dungeon() => new DungeonEntryDef
        {
            Id = "filos_doom", Landblock = 0x0150, Name = "Filos' Doom", ExitPortalWcid = 1003601,
            Points = Enumerable.Range(1, 20).Select(Pt).ToList(),
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
                    new SpeciesMemberDef { Wcid = 100, Role = 0 }, new SpeciesMemberDef { Wcid = 102, Role = 0 }, new SpeciesMemberDef { Wcid = 110, Role = 1 },
                    new SpeciesMemberDef { Wcid = 200, Role = 0 }, new SpeciesMemberDef { Wcid = 202, Role = 0 }, new SpeciesMemberDef { Wcid = 210, Role = 1 },
                }
            }
        };

        /// <summary>The shipped modifier shapes (Content/dungeons/dynamic/modifiers.json), one per axis.</summary>
        private static ThreadDungeonStore Store() => ThreadDungeonStore.Parse(
            "{\"dungeons\":[]}",
            "{\"bosses\":[{\"wcid\":10981,\"name\":\"Aun Tanua\",\"level\":110,\"families\":[],\"modifiers\":[\"boss_guarded\"]}]}",
            "{\"modifiers\":[" +
            "{\"id\":\"hardy\",\"target\":\"monster\",\"minMagnitude\":1.3,\"maxMagnitude\":2.0,\"monsterEffectKind\":\"health_mult\",\"rewardXpKind\":\"linear\",\"rewardXpBase\":1.0,\"rewardXpSlope\":0.25}," +
            "{\"id\":\"savage\",\"target\":\"monster\",\"minMagnitude\":10,\"maxMagnitude\":40,\"monsterEffectKind\":\"damage_rating\",\"rewardXpKind\":\"linear\",\"rewardXpBase\":1.0,\"rewardXpSlope\":0.01}," +
            "{\"id\":\"precise\",\"target\":\"monster\",\"minMagnitude\":10,\"maxMagnitude\":30,\"monsterEffectKind\":\"crit_rating\"}," +
            "{\"id\":\"vicious\",\"target\":\"monster\",\"minMagnitude\":5,\"maxMagnitude\":20,\"monsterEffectKind\":\"crit_damage_rating\"}," +
            "{\"id\":\"stalwart\",\"target\":\"monster\",\"minMagnitude\":10,\"maxMagnitude\":40,\"monsterEffectKind\":\"damage_resist_rating\",\"rewardXpKind\":\"linear\",\"rewardXpBase\":1.0,\"rewardXpSlope\":0.005}," +
            "{\"id\":\"swift\",\"target\":\"monster\",\"minMagnitude\":1.15,\"maxMagnitude\":1.35,\"monsterEffectKind\":\"run_speed_mult\"}," +
            "{\"id\":\"teeming\",\"target\":\"run\",\"minMagnitude\":1.25,\"maxMagnitude\":1.75,\"monsterEffectKind\":\"count_mult\"}," +
            "{\"id\":\"champions\",\"target\":\"run\",\"minMagnitude\":0.25,\"maxMagnitude\":0.5,\"monsterEffectKind\":\"elite_share\"}," +
            "{\"id\":\"shield_hollow\",\"target\":\"monster\",\"minMagnitude\":0.25,\"maxMagnitude\":0.6,\"monsterEffectKind\":\"ignore_shield\"}," +
            "{\"id\":\"hollow\",\"target\":\"monster\",\"minMagnitude\":0.01,\"maxMagnitude\":1.0,\"monsterEffectKind\":\"hollow\",\"rewardXpKind\":\"linear\",\"rewardXpBase\":1.0,\"rewardXpSlope\":0.75,\"rewardLumBase\":1.0,\"rewardLumSlope\":0.75}," +
            "{\"id\":\"enlightened\",\"target\":\"run\",\"minMagnitude\":1.25,\"maxMagnitude\":1.5,\"monsterEffectKind\":\"none\",\"rewardXpKind\":\"linear\",\"rewardXpBase\":0.0,\"rewardXpSlope\":1.0}," +
            "{\"id\":\"radiant\",\"target\":\"run\",\"minMagnitude\":1.25,\"maxMagnitude\":2.0,\"monsterEffectKind\":\"none\",\"rewardLumBase\":0.0,\"rewardLumSlope\":1.0}," +
            "{\"id\":\"boss_guarded\",\"target\":\"boss\",\"minMagnitude\":1.5,\"maxMagnitude\":3.0,\"monsterEffectKind\":\"health_mult\",\"rewardXpKind\":\"linear\",\"rewardXpBase\":1.0,\"rewardXpSlope\":0.15}," +
            "{\"id\":\"boss_enraged\",\"target\":\"boss\",\"minMagnitude\":20,\"maxMagnitude\":60,\"monsterEffectKind\":\"damage_rating\"}," +
            "{\"id\":\"bounteous\",\"target\":\"run\",\"minMagnitude\":1.15,\"maxMagnitude\":1.6,\"monsterEffectKind\":\"loot_quantity\",\"lootQualityBonus\":0.1}," +
            "{\"id\":\"affinity_x\",\"target\":\"monster\",\"minMagnitude\":5,\"maxMagnitude\":25,\"monsterEffectKind\":\"salvage_affinity\",\"salvageMaterial\":43,\"salvageBaseWcid\":2398}]," +
            "\"xpLadder\":[{\"level\":20,\"xp\":3500},{\"level\":100,\"xp\":80000},{\"level\":200,\"xp\":1100000}]}",
            new Dictionary<string, string>());

        private static readonly (string, double)[] FixedMods =
        {
            ("hardy", 1.6), ("savage", 30), ("precise", 20), ("vicious", 12), ("stalwart", 20), ("swift", 1.3),
            ("teeming", 1.5), ("champions", 0.5), ("shield_hollow", 0.5), ("hollow", 0.6), ("enlightened", 1.5),
            ("radiant", 2.0), ("boss_guarded", 2.0), ("boss_enraged", 40), ("bounteous", 1.4), ("affinity_x", 20),
        };

        /// <summary>
        /// Caps high enough never to bind on this fixture, and the boss rating floors at 0, so every number
        /// below is the modifier arithmetic alone. Reward scaling stays ON (its default), so the loot and
        /// salvage assertions also prove the curve did not touch axes that already carry that ratio.
        /// </summary>
        private static DungeonPopulationLimits Limits(double exponent)
            => new DungeonPopulationLimits(120, 100.0, 100.0, 8, 0.5, lootQuantityCap: 3.0,
                bossDamageRatingFloor: 0, bossDamageResistFloor: 0, minMonsters: 0, modifierLevelExponent: exponent);

        private static DungeonSpawnPlan Build(int level, double exponent)
            => DungeonPopulationBuilder.Build(Spec(level, FixedMods), Dungeon(), Store(), Species(), LevelOf, HealthOf, Limits(exponent), new Random(7));

        private static int Elites(DungeonSpawnPlan plan) => plan.Entries.Count(e => e.Role == DungeonRole.Elite);

        private static int NonBoss(DungeonSpawnPlan plan) => plan.Entries.Count(e => e.Role != DungeonRole.Boss);

        [TestMethod]
        public void A_level_50_gem_builds_scaled_monster_stats_and_multipliers()
        {
            var s = S(50);
            var plan = Build(50, K);
            var full = Build(50, 0.0); // the curve off: the pre-curve plan for the same spec

            Assert.AreEqual(s, plan.ModifierLevelScale, 1e-15);
            Assert.AreEqual(1.0, full.ModifierLevelScale);

            Assert.AreEqual(1 + s * 0.6, plan.HealthMultiplier, 1e-12);
            Assert.AreEqual(1.6, full.HealthMultiplier, 1e-12, "guard: off is full strength");

            Assert.AreEqual(3, plan.DamageRating, "round(30 x 0.099992)");
            Assert.AreEqual(2, plan.CritRating, "round(20 x s)");
            Assert.AreEqual(1, plan.CritDamageRating, "round(12 x s)");
            Assert.AreEqual(2, plan.DamageResistRating, "round(20 x s)");
            Assert.AreEqual(30, full.DamageRating);

            Assert.AreEqual(1 + s * 0.3, plan.RunSpeedMult, 1e-12);
            Assert.AreEqual(0.5 * s, plan.IgnoreShield, 1e-12);
            Assert.AreEqual(0.6 * s, plan.HollowIntensity, 1e-12);

            // count_mult: round(20 x (1 + s x 0.5)) = 21, against round(20 x 1.5) = 30 at full strength.
            Assert.AreEqual(21, NonBoss(plan));
            Assert.AreEqual(30, NonBoss(full));

            // elite_share: floor(21 x (0.15 + s x 0.35)) = 3, against floor(30 x 0.5) = 15.
            Assert.AreEqual(3, Elites(plan));
            Assert.AreEqual(15, Elites(full));

            // XP: every non-boss factor scaled, then multiplied. hardy 1.4, savage 1.3, stalwart 1.1,
            // hollow 1.45, enlightened 1.5; radiant has no XP factor.
            double F(double f) => 1 + s * (f - 1);
            Assert.AreEqual(F(1.4) * F(1.3) * F(1.1) * F(1.45) * F(1.5), plan.XpMultiplier, 1e-12);
            Assert.AreEqual(F(1.3), plan.BossXpMultiplier, 1e-12, "boss_guarded 1 + 0.15 x 2.0");
            Assert.AreEqual(F(1.45) * F(2.0), plan.LumMultiplier, 1e-12, "hollow 1.45, radiant 2.0");

            // The boss: the gem's hardy and boss_guarded AND the curated row's own boss_guarded (at its
            // MinMagnitude 1.5) are all scaled.
            Assert.AreEqual(10981u, plan.BossWcid);
            Assert.AreEqual(F(1.6) * F(2.0) * F(1.5), plan.BossHealthMultiplier, 1e-12);
            Assert.AreEqual((int)Math.Round((30 + 40) * s), plan.BossDamageRating, "savage + boss_enraged, scaled");
            Assert.AreEqual(2, plan.BossDamageResistRating);
        }

        [TestMethod]
        public void A_level_50_gem_leaves_loot_quantity_quality_and_salvage_affinity_to_the_reward_ratio()
        {
            var plan = Build(50, K);
            var full = Build(50, 0.0);

            Assert.AreEqual(full.LootQuantityMult, plan.LootQuantityMult);
            Assert.AreEqual(full.Profile.LootQualityMod, plan.Profile.LootQualityMod);
            Assert.AreEqual(full.Profile.ItemMaxAmount, plan.Profile.ItemMaxAmount);
            Assert.AreEqual(1, plan.SalvageAffinities.Count);
            Assert.AreEqual(full.SalvageAffinities[0].Chance, plan.SalvageAffinities[0].Chance);
            Assert.AreEqual(full.XpScale, plan.XpScale, "the server-wide xp scale is not a modifier");
        }

        /// <summary>
        /// The other half: the SAME spec at 185 builds exactly today's plan - bit-identical to the curve
        /// switched off, and equal to the full-strength literals.
        /// </summary>
        [TestMethod]
        public void A_level_185_gem_builds_exactly_the_pre_curve_plan()
        {
            var plan = Build(185, K);
            var off = Build(185, 0.0);

            Assert.AreEqual(1.0, plan.ModifierLevelScale);

            Assert.AreEqual(off.HealthMultiplier, plan.HealthMultiplier);
            Assert.AreEqual(off.BossHealthMultiplier, plan.BossHealthMultiplier);
            Assert.AreEqual(off.DamageRating, plan.DamageRating);
            Assert.AreEqual(off.CritRating, plan.CritRating);
            Assert.AreEqual(off.CritDamageRating, plan.CritDamageRating);
            Assert.AreEqual(off.DamageResistRating, plan.DamageResistRating);
            Assert.AreEqual(off.BossDamageRating, plan.BossDamageRating);
            Assert.AreEqual(off.BossDamageResistRating, plan.BossDamageResistRating);
            Assert.AreEqual(off.RunSpeedMult, plan.RunSpeedMult);
            Assert.AreEqual(off.IgnoreShield, plan.IgnoreShield);
            Assert.AreEqual(off.HollowIntensity, plan.HollowIntensity);
            Assert.AreEqual(off.XpMultiplier, plan.XpMultiplier);
            Assert.AreEqual(off.BossXpMultiplier, plan.BossXpMultiplier);
            Assert.AreEqual(off.LumMultiplier, plan.LumMultiplier);
            Assert.AreEqual(off.LootQuantityMult, plan.LootQuantityMult);
            CollectionAssert.AreEqual(off.Entries.Select(e => (e.Wcid, e.Role, e.Point.Cell, e.UpliftLevel)).ToList(),
                plan.Entries.Select(e => (e.Wcid, e.Role, e.Point.Cell, e.UpliftLevel)).ToList());

            Assert.AreEqual(1.6, plan.HealthMultiplier);
            Assert.AreEqual(30, plan.DamageRating);
            Assert.AreEqual(20, plan.CritRating);
            Assert.AreEqual(12, plan.CritDamageRating);
            Assert.AreEqual(20, plan.DamageResistRating);
            Assert.AreEqual(70, plan.BossDamageRating);
            Assert.AreEqual(1.3, plan.RunSpeedMult);
            Assert.AreEqual(0.5, plan.IgnoreShield);
            Assert.AreEqual(0.6, plan.HollowIntensity);
            Assert.AreEqual(30, NonBoss(plan));
            Assert.AreEqual(15, Elites(plan));
            Assert.AreEqual(1.4 * 1.3 * 1.1 * 1.45 * 1.5, plan.XpMultiplier, 1e-12);
            Assert.AreEqual(1.6 * 2.0 * 1.5, plan.BossHealthMultiplier, 1e-12);
        }

        [TestMethod]
        public void A_default_limits_struct_has_the_curve_off()
        {
            Assert.AreEqual(0.0, default(DungeonPopulationLimits).ModifierLevelExponent);
            Assert.AreEqual(1.0, DungeonModifierLevelScale.Factor(50, default(DungeonPopulationLimits).ModifierLevelExponent));
            Assert.AreEqual(K, new DungeonPopulationLimits(120, 3.0, 3.0, 8, 0.5).ModifierLevelExponent, "the constructor defaults to the shipped exponent");
        }

        // ---- the gem's description and the press summary -----------------------------------------------------

        private static ModifierDef Lookup(string id) => Store().Modifiers.TryGetValue(id, out var def) ? def : null;

        private static string Compose(int level, params (string, double)[] mods)
            => ThreadDungeonGemHandler.ComposeLongDesc(new DungeonGemSpec("filos_doom", level, 6, "any", 4242, mods, 0, 0, 1, null, null),
                "Filo's Doom", 2, 3, Lookup, w => "", xpScale: 1.0, lumScale: 1.0);

        [TestMethod]
        public void The_item_panel_prints_the_scaled_modifier_and_reward_at_level_50()
        {
            var low = Compose(50, ("savage", 40), ("hardy", 2.0), ("enlightened", 1.5));

            // The fixture rows carry no display text, so each line is labelled with the modifier id.
            StringAssert.Contains(low, "\nsavage: monsters attack with +4 damage rating, hitting harder\n");
            StringAssert.Contains(low, "monsters have x1.10 health");
            StringAssert.Contains(low, "kills pay x1.05 experience");
            // XP product: hardy 1 + s x 0.5, savage 1 + s x 0.4, enlightened 1 + s x 0.5.
            var s = S(50);
            var xp = (1 + s * 0.5) * (1 + s * 0.4) * (1 + s * 0.5);
            StringAssert.Contains(low, "\nExperience: x" + xp.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + "\n");
        }

        [TestMethod]
        public void The_item_panel_at_185_is_unchanged_by_the_curve()
        {
            var mods = new (string, double)[] { ("savage", 40), ("hardy", 2.0), ("enlightened", 1.5), ("champions", 0.26) };
            var spec = new DungeonGemSpec("filos_doom", 185, 6, "any", 4242, mods, 0, 0, 1, null, null);

            var on = ThreadDungeonGemHandler.ComposeLongDesc(spec, "Filo's Doom", 2, 3, Lookup, w => "");
            var off = ThreadDungeonGemHandler.ComposeLongDesc(spec, "Filo's Doom", 2, 3, Lookup, w => "", modifierLevelExponent: 0.0);

            Assert.AreEqual(off, on);
            StringAssert.Contains(on, "monsters attack with +40 damage rating");
            StringAssert.Contains(on, "26% of the run's monsters are elites");
        }

        [TestMethod]
        public void The_press_summary_prints_the_scaled_magnitude()
        {
            var mods = Store().Modifiers;
            var low = DungeonGemNarrator.ComposeSummary(Spec(50, ("savage", 40), ("champions", 0.5)), 3, mods);
            var at185 = DungeonGemNarrator.ComposeSummary(Spec(185, ("savage", 40), ("champions", 0.5)), 3, mods);

            Assert.IsTrue(low.Any(l => l.Contains("+4 damage rating")), string.Join("\n", low));
            Assert.IsTrue(low.Any(l => l.Contains("18% of the run's monsters are elites")), "0.15 + s x 0.35 = 0.184997: " + string.Join("\n", low));
            Assert.IsTrue(at185.Any(l => l.Contains("+40 damage rating")), string.Join("\n", at185));
            Assert.IsTrue(at185.Any(l => l.Contains("50% of the run's monsters are elites")), string.Join("\n", at185));
        }

        [TestMethod]
        public void A_none_row_prints_the_magnitude_whose_factor_is_the_scaled_factor()
        {
            var s = S(50);

            // Shipped shape: base 0, slope 1 - the magnitude IS the factor.
            var enlightened = new ModifierDef { MonsterEffectKind = "none", RewardXpKind = "linear", RewardXpBase = 0.0, RewardXpSlope = 1.0 };
            Assert.AreEqual(1 + s * 0.5, DungeonModifierLevelScale.ScaleDisplayMagnitude(enlightened, 1.5, s), 1e-12);

            // A non-identity linear row: base 1, slope 0.02, magnitude 20 is factor 1.4; scaled factor
            // 1 + s x 0.4, which is magnitude (scaled - 1) / 0.02 = 20 s.
            var veined = new ModifierDef { MonsterEffectKind = "none", RewardXpKind = "linear", RewardXpBase = 1.0, RewardXpSlope = 0.02 };
            Assert.AreEqual(20 * s, DungeonModifierLevelScale.ScaleDisplayMagnitude(veined, 20, s), 1e-9);

            // Luminance takes precedence, as in RenderEffect.
            var radiant = new ModifierDef { MonsterEffectKind = "none", RewardLumBase = 0.0, RewardLumSlope = 1.0 };
            Assert.AreEqual(1 + s * 1.0, DungeonModifierLevelScale.ScaleDisplayMagnitude(radiant, 2.0, s), 1e-12);

            // A monster-knob kind prints the same value the run applies; a loot row is untouched.
            Assert.AreEqual(40 * s, DungeonModifierLevelScale.ScaleDisplayMagnitude(new ModifierDef { MonsterEffectKind = DungeonRewardMath.DamageRating }, 40, s), 1e-12);
            Assert.AreEqual(1.4, DungeonModifierLevelScale.ScaleDisplayMagnitude(new ModifierDef { MonsterEffectKind = DungeonRewardMath.LootQuantity }, 1.4, s));
            Assert.AreEqual(9.0, DungeonModifierLevelScale.ScaleDisplayMagnitude(null, 9.0, s), "no def, no transform");
        }

        // ---- the tunable -------------------------------------------------------------------------------------

        [TestMethod]
        public void The_tunable_is_registered_at_the_compiled_default()
        {
            Assert.IsTrue(ACE.Server.Managers.DefaultPropertyManager.DefaultDoubleProperties.TryGetValue("dynamic_dungeons_modifier_level_exponent", out var p),
                "dynamic_dungeons_modifier_level_exponent is not registered");
            Assert.AreEqual(1.76, p.Item, "the shipped exponent, as a literal");
        }

        [TestMethod]
        public void The_sanitizer_keeps_zero_and_negatives_and_defaults_only_garbage()
        {
            Assert.AreEqual(K, ThreadDungeonSpawner.SanitizeModifierLevelExponent(double.NaN));
            Assert.AreEqual(K, ThreadDungeonSpawner.SanitizeModifierLevelExponent(double.PositiveInfinity));
            Assert.AreEqual(K, ThreadDungeonSpawner.SanitizeModifierLevelExponent(double.NegativeInfinity));
            Assert.AreEqual(0.0, ThreadDungeonSpawner.SanitizeModifierLevelExponent(0.0), "0 is the documented off switch");
            Assert.AreEqual(-1.0, ThreadDungeonSpawner.SanitizeModifierLevelExponent(-1.0));
            Assert.AreEqual(2.5, ThreadDungeonSpawner.SanitizeModifierLevelExponent(2.5));
        }

        /// <summary>
        /// The live read, through the same PropertyManager cache seeding DungeonGemLongDescTests uses (no DB
        /// round trip). Restored in a finally block because the cache is static and shared assembly-wide.
        /// </summary>
        [TestMethod]
        public void The_live_read_returns_the_tunable_value()
        {
            try
            {
                Assert.IsTrue(ACE.Server.Managers.PropertyManager.ModifyDouble("dynamic_dungeons_modifier_level_exponent", 2.25), "key not registered");
                Assert.AreEqual(2.25, ThreadDungeonSpawner.ResolveModifierLevelExponent());

                ACE.Server.Managers.PropertyManager.ModifyDouble("dynamic_dungeons_modifier_level_exponent", 0.0);
                Assert.AreEqual(0.0, ThreadDungeonSpawner.ResolveModifierLevelExponent(), "an admin's 0 is kept: the curve is off");
            }
            finally
            {
                ACE.Server.Managers.PropertyManager.ModifyDouble("dynamic_dungeons_modifier_level_exponent", DungeonPopulationLimits.DefaultModifierLevelExponent);
            }
        }

        // ---- wiring pins (review F2) ----------------------------------------------------------------------------
        //
        // Every live caller passes the resolved exponent explicitly. Dropping the argument still COMPILES and
        // still PASSES every arithmetic test, because each parameter defaults to the shipped 1.76 - the run
        // would then silently ignore an admin's /pm change. These pins are what fail instead.

        [TestMethod]
        public void TryPopulate_passes_the_live_exponent_into_the_limits()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadDungeonSpawner.cs");
            var body = PooledLootSourceText.MethodBody(src, "public static void TryPopulate(ThreadDungeonRun run, Landblock landblock)");

            StringAssert.Contains(body, "modifierLevelExponent: ResolveModifierLevelExponent()");
        }

        [TestMethod]
        public void Press_resolves_the_live_exponent_once_and_passes_it_to_both_narrated_surfaces()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/FragmentPressStation.cs");
            var body = PooledLootSourceText.MethodBody(src, "public static void Press(Player player, Gem fragment, WorldObject press)");

            StringAssert.Contains(body, "ThreadDungeonSpawner.ResolveModifierLevelExponent()");
            StringAssert.Contains(body, "DungeonGemNarrator.ComposeDoseLines(doses, store.Modifiers, modifierLevelExponent)");
            StringAssert.Contains(body, "DungeonGemNarrator.ComposeSummary(result.Spec, result.Entries, store.Modifiers, modifierLevelExponent)");
        }

        [TestMethod]
        public void The_store_bound_long_description_passes_the_live_exponent()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadDungeonGemHandler.cs");
            var body = PooledLootSourceText.ExpressionBody(src,
                "public static string ComposeLongDesc(DungeonGemSpec spec, string dungeonName, int entries, int maxEntries, bool groupRun)");

            StringAssert.Contains(body, "ThreadDungeonSpawner.ResolveModifierLevelExponent())");
        }

        // ---- panel / builder agreement over the SHIPPED modifiers (review F3) --------------------------------

        private static DungeonEntryDef DungeonWithPoints(int points) => new DungeonEntryDef
        {
            Id = "filos_doom", Landblock = 0x0150, Name = "Filos' Doom", ExitPortalWcid = 1003601,
            Points = Enumerable.Range(1, points).Select(Pt).ToList(),
            BossAnchor = Pt(999),
            CreatureTypes = new List<string> { "Banderling" },
        };

        /// <summary>A banderling roster with trash and an elite inside each of the 50, 100 and 150 bands.</summary>
        private static readonly Dictionary<uint, int> AgreementLevels = new Dictionary<uint, int>
        {
            [100] = 50, [102] = 52, [110] = 54, [120] = 100, [122] = 105, [130] = 108, [140] = 150, [142] = 155, [150] = 160,
        };

        private static int AgreementLevelOf(uint w) => AgreementLevels.TryGetValue(w, out var l) ? l : 0;

        private static Dictionary<string, SpeciesTableDef> AgreementSpecies() => new Dictionary<string, SpeciesTableDef>
        {
            ["banderling"] = new SpeciesTableDef
            {
                Id = "banderling", CreatureType = "Banderling",
                Members = AgreementLevels.Keys.Select(w => new SpeciesMemberDef { Wcid = w, Role = w == 110 || w == 130 || w == 150 ? 1 : 0 }).ToList(),
            }
        };

        /// <summary>The first number RenderEffect prints: "x1.10", "+4", "17%".</summary>
        private static string Token(string rendered)
        {
            var m = System.Text.RegularExpressions.Regex.Match(rendered, @"[x+]?\d+(?:\.\d+)?%?");
            Assert.IsTrue(m.Success, "no number in: " + rendered);
            return m.Value;
        }

        private static string Mult(double v) => "x" + v.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

        private static string Pct(double fraction) => Math.Round(fraction * 100).ToString(System.Globalization.CultureInfo.InvariantCulture) + "%";

        /// <summary>
        /// For every shipped modifier the panel words as a number the builder also applies, the number
        /// RenderScaledEffect prints must be the number the plan carries, formatted the same way, at 50, 100 and
        /// 150. Each modifier is rolled at its shipped MaxMagnitude. Monster knobs and the two reward-only rows
        /// are built separately, because hollow and the difficulty rows also move XP/luminance, which would
        /// contaminate the radiant/enlightened comparison.
        ///
        /// 100 curated points, not 20: count_mult is printed as a whole percent, and only with 100 points does
        /// round(points x m) - points equal that percent exactly.
        /// </summary>
        [TestMethod]
        public void The_panel_prints_what_the_builder_applies_for_every_shipped_kind()
        {
            var store = ThreadDungeonStoreTests.LoadShipped();
            var mods = store.Modifiers;
            var limits = new DungeonPopulationLimits(1000, 100.0, 100.0, 8, 0.5, lootQuantityCap: 3.0, minMonsters: 0, modifierLevelExponent: K);

            string[] knobs = { "hardy", "savage", "precise", "stalwart", "swift", "teeming", "shield_hollow", "hollow" };
            string[] rewardOnly = { "radiant", "enlightened" };

            foreach (var id in knobs.Concat(rewardOnly))
                Assert.IsTrue(mods.ContainsKey(id), $"shipped modifiers.json no longer has {id}");

            foreach (var level in new[] { 50, 100, 150 })
            {
                var s = S(level);
                string Shown(string id) => Token(ThreadDungeonGemHandler.RenderScaledEffect(mods[id], mods[id].MaxMagnitude, s));

                var knobSpec = Spec(level, knobs.Select(id => (id, mods[id].MaxMagnitude)).ToArray());
                var plan = DungeonPopulationBuilder.Build(knobSpec, DungeonWithPoints(100), store, AgreementSpecies(), AgreementLevelOf, HealthOf, limits, new Random(7));

                Assert.AreEqual(Mult(plan.HealthMultiplier), Shown("hardy"), $"hardy at {level}");
                Assert.AreEqual("+" + plan.DamageRating, Shown("savage"), $"savage at {level}");
                Assert.AreEqual("+" + plan.CritRating, Shown("precise"), $"precise at {level}");
                Assert.AreEqual("+" + plan.DamageResistRating, Shown("stalwart"), $"stalwart at {level}");
                Assert.AreEqual(Mult(plan.RunSpeedMult), Shown("swift"), $"swift at {level}");
                Assert.AreEqual(Pct(NonBoss(plan) / 100.0 - 1.0), Shown("teeming"), $"teeming at {level}");
                Assert.AreEqual(Pct(plan.IgnoreShield), Shown("shield_hollow"), $"shield_hollow at {level}");
                Assert.AreEqual(Pct(plan.HollowIntensity), Shown("hollow"), $"hollow at {level}");

                var rewardSpec = Spec(level, rewardOnly.Select(id => (id, mods[id].MaxMagnitude)).ToArray());
                var rewardPlan = DungeonPopulationBuilder.Build(rewardSpec, DungeonWithPoints(100), store, AgreementSpecies(), AgreementLevelOf, HealthOf, limits, new Random(7));

                Assert.AreEqual(Mult(rewardPlan.LumMultiplier), Shown("radiant"), $"radiant at {level}");
                Assert.AreEqual(Mult(rewardPlan.XpMultiplier), Shown("enlightened"), $"enlightened at {level}");

                Assert.IsTrue(plan.ModifierLevelScale < 1.0, $"guard: {level} is below the anchor, so the comparison is of SCALED values");
                Assert.IsTrue(NonBoss(plan) >= 100, $"guard: the roster fielded a pack at {level}");
            }
        }

        // ---- press dose lines (review F4) ----------------------------------------------------------------------

        private static DoseLogEntry Dose(string before, string after, int levelBefore, int levelAfter)
            => new DoseLogEntry { Name = "Red Taper", Op = AttunementOps.AddOrRaise, Before = before, After = after, LevelBefore = levelBefore, LevelAfter = levelAfter };

        [TestMethod]
        public void Press_dose_lines_print_the_scaled_magnitude_below_185()
        {
            var mods = Store().Modifiers;

            var gained = DungeonGemNarrator.ComposeDoseLine(Dose("mods=", "mods=savage:40", 50, 50), mods);
            Assert.AreEqual("  Red Taper: gained savage 4", gained, "40 x 0.099992 = 3.9997, printed to 2 decimals");

            var raised = DungeonGemNarrator.ComposeDoseLine(Dose("mods=hardy:1.3", "mods=hardy:2", 50, 50), mods);
            Assert.AreEqual("  Red Taper: hardy 1.03 -> hardy 1.1", raised);
        }

        [TestMethod]
        public void Press_dose_lines_are_unchanged_at_185()
        {
            var mods = Store().Modifiers;

            Assert.AreEqual("  Red Taper: gained savage 40", DungeonGemNarrator.ComposeDoseLine(Dose("mods=", "mods=savage:40", 185, 185), mods));
            Assert.AreEqual("  Red Taper: hardy 1.3 -> hardy 2", DungeonGemNarrator.ComposeDoseLine(Dose("mods=hardy:1.3", "mods=hardy:2", 185, 185), mods));
            Assert.AreEqual(DungeonGemNarrator.ComposeDoseLine(Dose("mods=", "mods=savage:40", 185, 185), mods, modifierLevelExponent: 0.0),
                DungeonGemNarrator.ComposeDoseLine(Dose("mods=", "mods=savage:40", 185, 185), mods), "identical to the curve switched off");
        }

        [TestMethod]
        public void A_dose_with_no_level_prints_the_raw_magnitude()
        {
            // A hand-built entry (levels left at 0) must not be scaled to nothing.
            Assert.AreEqual("  Red Taper: gained savage 40", DungeonGemNarrator.ComposeDoseLine(Dose("mods=", "mods=savage:40", 0, 0), Store().Modifiers));
        }
    }
}
