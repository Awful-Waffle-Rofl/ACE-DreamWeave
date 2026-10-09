using System;
using System.Collections.Generic;

using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    [TestClass]
    public class DungeonRewardMathTests
    {
        private static List<XpLadderRungDef> Ladder() => new List<XpLadderRungDef>
        {
            new XpLadderRungDef { Level = 20, Xp = 3500 }, new XpLadderRungDef { Level = 50, Xp = 10000 },
            new XpLadderRungDef { Level = 80, Xp = 30000 }, new XpLadderRungDef { Level = 100, Xp = 80000 },
            new XpLadderRungDef { Level = 200, Xp = 1100000 }, new XpLadderRungDef { Level = 240, Xp = 1850000 },
        };

        private static Dictionary<string, ModifierDef> Mods() => new Dictionary<string, ModifierDef>
        {
            ["hardy"] = new ModifierDef { Id = "hardy", Target = "monster", MonsterEffectKind = "health_mult", RewardXpKind = "linear", RewardXpBase = 1.0, RewardXpSlope = 0.25, LootQuantityMult = 1.4 },
            ["savage"] = new ModifierDef { Id = "savage", Target = "monster", MonsterEffectKind = "damage_rating", RewardXpKind = "linear", RewardXpBase = 1.0, RewardXpSlope = 0.01, LootQuantityMult = 1.35 },
            ["enlightened"] = new ModifierDef { Id = "enlightened", Target = "run", MonsterEffectKind = "none", RewardXpKind = "linear", RewardXpBase = 0.0, RewardXpSlope = 1.0 },
            ["radiant"] = new ModifierDef { Id = "radiant", Target = "run", MonsterEffectKind = "none", RewardXpKind = "constant", RewardXpBase = 1.0, RewardLumBase = 0.0, RewardLumSlope = 1.0 },
            ["teeming"] = new ModifierDef { Id = "teeming", Target = "run", MonsterEffectKind = "count_mult", RewardXpKind = "constant", RewardXpBase = 1.0 },
            ["boss_guarded"] = new ModifierDef { Id = "boss_guarded", Target = "boss", MonsterEffectKind = "health_mult", MinMagnitude = 1.5, MaxMagnitude = 3.0, RewardXpKind = "linear", RewardXpBase = 1.0, RewardXpSlope = 0.15 },
            ["boss_enraged"] = new ModifierDef { Id = "boss_enraged", Target = "boss", MonsterEffectKind = "damage_rating", MinMagnitude = 20, MaxMagnitude = 60, RewardXpKind = "linear", RewardXpBase = 1.0, RewardXpSlope = 0.005 },
            ["hollow"] = new ModifierDef { Id = "hollow", Target = "monster", MonsterEffectKind = "hollow", MinMagnitude = 0.01, MaxMagnitude = 1.0, RewardXpKind = "linear", RewardXpBase = 1.0, RewardXpSlope = 0.75, RewardLumBase = 1.0, RewardLumSlope = 0.75 },
        };

        private static DungeonGemSpec Spec(params (string, double)[] mods) => new DungeonGemSpec("any", 100, 6, "any", 1, mods, 0, 0);

        [TestMethod]
        public void Loot_quantity_multiplies_across_modifiers_and_clamps_to_the_cap()
        {
            // A row that omits lootQuantityMult defaults to 1.0, so an unmodified gem is a no-op.
            Assert.AreEqual(1.0, DungeonRewardMath.LootQuantityMultiplier(Spec(), Mods(), 3.0), 1e-9);
            Assert.AreEqual(1.0, DungeonRewardMath.LootQuantityMultiplier(Spec(("teeming", 1.5)), Mods(), 3.0), 1e-9,
                "teeming carries no quantity factor - it gives more kills, not richer ones");

            Assert.AreEqual(1.4, DungeonRewardMath.LootQuantityMultiplier(Spec(("hardy", 2.0)), Mods(), 3.0), 1e-9);
            Assert.AreEqual(1.4 * 1.35, DungeonRewardMath.LootQuantityMultiplier(Spec(("hardy", 2.0), ("savage", 40)), Mods(), 3.0), 1e-9);

            Assert.AreEqual(1.5, DungeonRewardMath.LootQuantityMultiplier(Spec(("hardy", 2.0), ("savage", 40)), Mods(), 1.5), 1e-9,
                "1.4 * 1.35 = 1.89 clamped to the cap");
            Assert.AreEqual(1.0, DungeonRewardMath.LootQuantityMultiplier(Spec(("unknown", 9)), Mods(), 3.0), 1e-9,
                "unknown ids are ignored");
        }

        [TestMethod]
        public void Loot_quantity_never_shrinks_a_run()
        {
            var mods = Mods();
            mods["broken"] = new ModifierDef { Id = "broken", Target = "run", MonsterEffectKind = "none", LootQuantityMult = 0.0 };
            Assert.AreEqual(1.0, DungeonRewardMath.LootQuantityMultiplier(Spec(("broken", 1)), mods, 3.0), 1e-9,
                "a zero factor is skipped, not applied");

            mods["negative"] = new ModifierDef { Id = "negative", Target = "run", MonsterEffectKind = "none", LootQuantityMult = -2.0 };
            Assert.AreEqual(1.4, DungeonRewardMath.LootQuantityMultiplier(Spec(("hardy", 2.0), ("negative", 1)), mods, 3.0), 1e-9);

            Assert.AreEqual(1.0, DungeonRewardMath.LootQuantityMultiplier(Spec(("hardy", 2.0)), Mods(), 0.5), 1e-9,
                "a cap below 1 cannot drive the multiplier below 1");
        }

        [TestMethod]
        public void BuildProfile_scales_max_amounts_only_and_rounds_up()
        {
            var baseline = DungeonRewardMath.BuildProfile(6, 0.2, 0.5, 8);
            Assert.AreEqual(2, baseline.ItemMaxAmount);
            Assert.AreEqual(2, baseline.MagicItemMaxAmount);
            Assert.AreEqual(1, baseline.MundaneItemMaxAmount);

            var tripled = DungeonRewardMath.BuildProfile(6, 0.2, 0.5, 8, 3.0);
            Assert.AreEqual(6, tripled.ItemMaxAmount);
            Assert.AreEqual(6, tripled.MagicItemMaxAmount);
            Assert.AreEqual(3, tripled.MundaneItemMaxAmount);

            Assert.AreEqual(baseline.ItemMinAmount, tripled.ItemMinAmount, "minimums never move");
            Assert.AreEqual(baseline.MagicItemMinAmount, tripled.MagicItemMinAmount);
            Assert.AreEqual(baseline.MundaneItemMinAmount, tripled.MundaneItemMinAmount);

            // Rounds UP, so a fractional multiplier still buys a whole extra item off the mundane roll.
            var partial = DungeonRewardMath.BuildProfile(6, 0.2, 0.5, 8, 1.4);
            Assert.AreEqual(3, partial.ItemMaxAmount, "ceil(2 * 1.4) = 3");
            Assert.AreEqual(2, partial.MundaneItemMaxAmount, "ceil(1 * 1.4) = 2");

            // Below 1 and NaN both leave the profile alone rather than shrinking it.
            Assert.AreEqual(2, DungeonRewardMath.BuildProfile(6, 0.2, 0.5, 8, 0.25).ItemMaxAmount);
            Assert.AreEqual(2, DungeonRewardMath.BuildProfile(6, 0.2, 0.5, 8, double.NaN).ItemMaxAmount);
        }

        [TestMethod]
        public void Ladder_hits_rungs_exactly_and_interpolates_between()
        {
            Assert.AreEqual(3500, DungeonRewardMath.LadderXp(Ladder(), 20));
            Assert.AreEqual(80000, DungeonRewardMath.LadderXp(Ladder(), 100));
            var mid = DungeonRewardMath.LadderXp(Ladder(), 150);
            Assert.IsTrue(mid > 80000 && mid < 1100000, $"mid={mid}");
            Assert.AreEqual(3500, DungeonRewardMath.LadderXp(Ladder(), 5), "flat below the first rung");
            Assert.AreEqual(1850000, DungeonRewardMath.LadderXp(Ladder(), 275), "flat above the last rung");
        }

        [TestMethod]
        public void Interpolation_is_log_linear_not_linear()
        {
            // log-linear between (100, 80000) and (200, 1100000): at 150 the value is sqrt(80000*1100000) = 296648
            var mid = DungeonRewardMath.LadderXp(Ladder(), 150);
            Assert.IsTrue(mid > 290000 && mid < 303000, $"mid={mid}");
        }

        [TestMethod]
        public void Role_rates()
        {
            Assert.AreEqual(1.0, DungeonRewardMath.RoleRate(DungeonRole.Trash));
            Assert.AreEqual(1.5, DungeonRewardMath.RoleRate(DungeonRole.Elite));
            Assert.AreEqual(2.0, DungeonRewardMath.RoleRate(DungeonRole.Boss));
        }

        [TestMethod]
        public void Xp_multiplier_is_a_capped_product()
        {
            Assert.AreEqual(1.0, DungeonRewardMath.XpMultiplier(Spec(), Mods(), 2.0), 1e-9);
            Assert.AreEqual(1.4, DungeonRewardMath.XpMultiplier(Spec(("hardy", 1.6)), Mods(), 2.0), 1e-9);
            Assert.AreEqual(1.4 * 1.2, DungeonRewardMath.XpMultiplier(Spec(("hardy", 1.6), ("savage", 20)), Mods(), 2.0), 1e-9);
            Assert.AreEqual(2.0, DungeonRewardMath.XpMultiplier(Spec(("hardy", 2.0), ("enlightened", 1.5)), Mods(), 2.0), 1e-9, "1.5 * 1.5 = 2.25 capped to 2.0");
            Assert.AreEqual(1.0, DungeonRewardMath.XpMultiplier(Spec(("unknown", 9)), Mods(), 2.0), 1e-9, "unknown ids are ignored");
        }

        [TestMethod]
        public void Lum_multiplier_reads_the_lum_fields()
        {
            Assert.AreEqual(1.0, DungeonRewardMath.LumMultiplier(Spec(("hardy", 1.6)), Mods(), 3.0), 1e-9);
            Assert.AreEqual(1.75, DungeonRewardMath.LumMultiplier(Spec(("radiant", 1.75)), Mods(), 3.0), 1e-9);
        }

        [TestMethod]
        public void Xp_for_kill_combines_ladder_role_and_multiplier()
        {
            Assert.AreEqual(80000, DungeonRewardMath.XpForKill(Ladder(), 100, DungeonRole.Trash, 1.0, 1.0, 1.0));
            Assert.AreEqual(120000, DungeonRewardMath.XpForKill(Ladder(), 100, DungeonRole.Elite, 1.0, 1.0, 1.0));
            Assert.AreEqual(224000, DungeonRewardMath.XpForKill(Ladder(), 100, DungeonRole.Boss, 1.0, 1.4, 1.0));
            Assert.AreEqual(1, DungeonRewardMath.XpForKill(new List<XpLadderRungDef>(), 100, DungeonRole.Trash, 1.0, 1.0, 1.0), "empty ladder still awards 1");
        }

        /// <summary>
        /// The owner's headline requirement: a dungeon kill pays 2.0x what killing that creature outside the
        /// dungeon pays. Asserted against the SHIPPED default rather than a literal, so retuning the dial
        /// retunes this test with it, and against a base value handed in directly - the anchor is the drawn
        /// creature's own retail worth, not a ladder rung at the gem's level.
        /// </summary>
        [TestMethod]
        public void A_plain_gem_pays_the_reward_scale_times_retail()
        {
            const long baseXp = 800000;
            var scale = DungeonPopulationLimits.DefaultXpScale;

            Assert.AreEqual(2.0, scale, 1e-9, "guard: the shipped scale this test's expectations are written against");

            Assert.AreEqual((long)Math.Round(baseXp * scale),
                DungeonRewardMath.XpForKill(baseXp, DungeonRole.Trash, scale, 1.0, 1.0));
            Assert.AreEqual((long)Math.Round(baseXp * 1.5 * scale),
                DungeonRewardMath.XpForKill(baseXp, DungeonRole.Elite, scale, 1.0, 1.0), "elite rate is 1.5x on top");
            Assert.AreEqual((long)Math.Round(baseXp * 2.0 * scale),
                DungeonRewardMath.XpForKill(baseXp, DungeonRole.Boss, scale, 1.0, 1.0), "boss rate is 2.0x on top");
        }

        /// <summary>
        /// THE guard against the most likely way to get this change wrong: folding the reward scale into the
        /// XP modifier product as a synthetic factor. Product ends in Math.Min(product, cap), so a scale
        /// folded inside would be clipped by dynamic_dungeons_xp_mult_cap and a plain gem would pay exactly
        /// what a four-modifier gem pays.
        ///
        /// The cap here is 2.0 and the scale is 2.7, so a scale living inside the product could not survive.
        /// The plain-gem case is what discriminates: with the scale inside, it would land on the cap (2.0x)
        /// instead of the scale (2.7x).
        /// </summary>
        [TestMethod]
        public void The_reward_scale_is_not_clipped_by_the_xp_modifier_cap()
        {
            const long baseXp = 100000;
            const double cap = 2.0;
            const double scale = 2.7;

            var plain = Spec();
            var plainMult = DungeonRewardMath.XpMultiplier(plain, Mods(), cap);
            Assert.AreEqual(1.0, plainMult, 1e-9, "guard: a plain gem's modifier product is 1.0");

            Assert.AreEqual(270000L, DungeonRewardMath.XpForKill(baseXp, DungeonRole.Trash, scale, plainMult, 1.0),
                "a plain gem pays the full 2.7x; a scale folded inside Product would have been clipped to the 2.0 cap");

            // And the modifiers still add on top of it rather than being absorbed by it.
            var modded = Spec(("hardy", 2.0), ("enlightened", 1.5));
            var moddedMult = DungeonRewardMath.XpMultiplier(modded, Mods(), cap);
            Assert.AreEqual(cap, moddedMult, 1e-9, "guard: this gem's product saturates the cap");

            Assert.AreEqual(540000L, DungeonRewardMath.XpForKill(baseXp, DungeonRole.Trash, scale, moddedMult, 1.0),
                "2.0 capped product x 2.7 scale; difficulty must still add on top of the scale");
        }

        /// <summary>An explicit scale of 0 is the documented way to disable run XP, and must pay nothing rather than the Math.Max floor of 1.</summary>
        [TestMethod]
        public void A_zero_reward_scale_pays_nothing()
        {
            Assert.AreEqual(0L, DungeonRewardMath.XpForKill(800000, DungeonRole.Boss, 0.0, 2.0, 2.0));
        }

        /// <summary>
        /// An absurd product must saturate on a bound the return type can actually hold, and must never come
        /// back negative.
        ///
        /// The trap is the upper clamp bound. long.MaxValue widened to double rounds UP to 2^63, one past the
        /// largest representable long, so clamping a double against it enforces a maximum the destination
        /// type cannot represent. MaxRepresentableLong (2^63 - 1024, the next double below) round-trips
        /// exactly, which is what the third assertion below pins.
        ///
        /// Note what this test does NOT claim, because the control run measured it: with the bound reverted
        /// to long.MaxValue this method returns long.MaxValue, not long.MinValue. .NET Core 3.0 and later
        /// specify float-to-integer conversion as SATURATING, so the classic negative wrap does not occur on
        /// this runtime - the second assertion is what discriminates, not the first. The first stays because
        /// it is the property that actually matters to a player, and because the saturating-conversion rule is
        /// a runtime guarantee this code should not silently depend on.
        ///
        /// Unreachable with the shipped dials, so this pins arithmetic rather than a live path. The inputs are
        /// far past anything the sanitizers allow, which is the point.
        /// </summary>
        [TestMethod]
        public void An_absurd_product_saturates_on_a_representable_bound()
        {
            var xp = DungeonRewardMath.XpForKill(long.MaxValue, DungeonRole.Boss, double.MaxValue, double.MaxValue, double.MaxValue);

            Assert.IsTrue(xp > 0, $"XP came back negative instead of saturating: {xp}");
            Assert.AreEqual((long)DungeonRewardMath.MaxRepresentableLong, xp, "it must land on the stated saturation bound");

            // The bound itself round-trips, which is the whole reason it is not long.MaxValue.
            Assert.AreEqual(DungeonRewardMath.MaxRepresentableLong, (double)(long)DungeonRewardMath.MaxRepresentableLong, 0.0,
                "the clamp bound must be a double that casts back to the same long");
            Assert.AreNotEqual(long.MaxValue, (long)DungeonRewardMath.MaxRepresentableLong,
                "guard: the bound is genuinely below long.MaxValue, so the assertion above is not vacuous");

            // Infinity is the other shape the same overflow arrives in, and NaN is the third.
            Assert.AreEqual((long)DungeonRewardMath.MaxRepresentableLong,
                DungeonRewardMath.XpForKill(long.MaxValue, DungeonRole.Boss, double.PositiveInfinity, 1.0, 1.0),
                "an infinite scale must still saturate on the same bound");
            Assert.AreEqual(1L, DungeonRewardMath.XpForKill(0, DungeonRole.Trash, double.PositiveInfinity, 1.0, 1.0),
                "0 x Infinity is NaN, which survives Math.Clamp and would make the following cast unspecified");
        }

        [TestMethod]
        public void Boss_modifiers_do_not_change_a_trash_kill()
        {
            var withBoss = Spec(("boss_enraged", 40));
            var withoutBoss = Spec();

            var trashXpWithBoss = DungeonRewardMath.XpForKill(Ladder(), 100, DungeonRole.Trash, 1.0,
                DungeonRewardMath.XpMultiplier(withBoss, Mods(), 2.0), DungeonRewardMath.BossXpMultiplier(withBoss, Mods(), 2.0));
            var trashXpWithoutBoss = DungeonRewardMath.XpForKill(Ladder(), 100, DungeonRole.Trash, 1.0,
                DungeonRewardMath.XpMultiplier(withoutBoss, Mods(), 2.0), DungeonRewardMath.BossXpMultiplier(withoutBoss, Mods(), 2.0));

            Assert.AreEqual(trashXpWithoutBoss, trashXpWithBoss, "a boss modifier must not change a trash kill's XP");
        }

        [TestMethod]
        public void Boss_modifiers_multiply_a_boss_kill()
        {
            var spec = Spec(("boss_enraged", 40));
            var mods = Mods();
            var xpMultiplier = DungeonRewardMath.XpMultiplier(spec, mods, 2.0);
            var bossXpMultiplier = DungeonRewardMath.BossXpMultiplier(spec, mods, 2.0);

            var bossXp = DungeonRewardMath.XpForKill(Ladder(), 100, DungeonRole.Boss, 1.0, xpMultiplier, bossXpMultiplier);
            var expected = (long)Math.Round(DungeonRewardMath.LadderXp(Ladder(), 100) * DungeonRewardMath.RoleRate(DungeonRole.Boss) * xpMultiplier * bossXpMultiplier);

            Assert.AreEqual(expected, bossXp);
        }

        [TestMethod]
        public void Boss_xp_multiplier_is_capped()
        {
            var spec = Spec(("boss_enraged", 60), ("boss_guarded", 3.0));
            var uncapped = DungeonRewardMath.BossXpMultiplier(spec, Mods(), 100.0);
            Assert.IsTrue(uncapped > 1.5, $"uncapped={uncapped}");

            var capped = DungeonRewardMath.BossXpMultiplier(spec, Mods(), 1.5);
            Assert.AreEqual(1.5, capped, 1e-9);
        }

        [TestMethod]
        public void Existing_gems_are_unchanged()
        {
            var spec = Spec(("hardy", 1.6), ("savage", 22));
            var mods = Mods();

            var xpMultiplier = DungeonRewardMath.XpMultiplier(spec, mods, 2.0);
            var bossXpMultiplier = DungeonRewardMath.BossXpMultiplier(spec, mods, 2.0);
            Assert.AreEqual(1.0, bossXpMultiplier, 1e-9, "no boss modifier on this gem");

            var xp = DungeonRewardMath.XpForKill(Ladder(), 100, DungeonRole.Trash, 1.0, xpMultiplier, bossXpMultiplier);
            Assert.AreEqual(136640, xp, "byte-identical to the pre-D6 result for this spec");
        }

        // ---- hollow intensity (owner ruling, 2026-09-17) -----------------------------------------------

        [TestMethod]
        public void Hollow_intensity_is_zero_with_no_hollow_modifier()
        {
            Assert.AreEqual(0.0, DungeonRewardMath.HollowIntensity(Spec(), Mods(), new string[0]), 1e-9);
            Assert.AreEqual(0.0, DungeonRewardMath.HollowIntensity(Spec(("hardy", 1.6)), Mods(), new string[0]), 1e-9,
                "an ordinary modifier is not a hollow modifier");
        }

        [TestMethod]
        public void Hollow_intensity_is_the_max_over_hollow_modifiers_and_clamps_to_0_1()
        {
            Assert.AreEqual(0.4, DungeonRewardMath.HollowIntensity(Spec(("hollow", 0.4)), Mods(), new string[0]), 1e-9);

            var mods = Mods();
            mods["hollow2"] = new ModifierDef { Id = "hollow2", Target = "monster", MonsterEffectKind = "hollow", MinMagnitude = 0.01, MaxMagnitude = 1.0 };
            Assert.AreEqual(0.7, DungeonRewardMath.HollowIntensity(Spec(("hollow", 0.4), ("hollow2", 0.7)), mods, new string[0]), 1e-9,
                "the MAX across hollow modifiers, never a sum");

            Assert.AreEqual(1.0, DungeonRewardMath.HollowIntensity(Spec(("hollow", 1.5)), Mods(), new string[0]), 1e-9, "clamped above 1");
            Assert.AreEqual(0.0, DungeonRewardMath.HollowIntensity(Spec(("hollow", -0.2)), Mods(), new string[0]), 1e-9, "a non-positive magnitude contributes nothing");
            Assert.AreEqual(0.0, DungeonRewardMath.HollowIntensity(Spec(("hollow", double.NaN)), Mods(), new string[0]), 1e-9);

            var extra = DungeonRewardMath.HollowIntensity(Spec(), Mods(), new[] { "hollow" });
            Assert.AreEqual(0.01, extra, 1e-9, "an extra modifier id uses its MinMagnitude");
        }

        /// <summary>
        /// THE COMPATIBILITY ANCHOR (owner ruling 2026-09-17): a full-intensity hollow gem pays exactly the
        /// pre-intensity constant of x1.75, and the shipped floor of 0.01 pays x1.0075 - both linear off the
        /// shipped base 1.0 / slope 0.75.
        /// </summary>
        [TestMethod]
        public void Hollow_reward_multiplier_is_linear_and_matches_the_pre_intensity_constant_at_1_0()
        {
            Assert.AreEqual(1.75, DungeonRewardMath.XpMultiplier(Spec(("hollow", 1.0)), Mods(), 2.0), 1e-9);
            Assert.AreEqual(1.75, DungeonRewardMath.LumMultiplier(Spec(("hollow", 1.0)), Mods(), 2.0), 1e-9);

            Assert.AreEqual(1.0075, DungeonRewardMath.XpMultiplier(Spec(("hollow", 0.01)), Mods(), 2.0), 1e-9);
            Assert.AreEqual(1.0075, DungeonRewardMath.LumMultiplier(Spec(("hollow", 0.01)), Mods(), 2.0), 1e-9);
        }

        // ---- salvage affinity -------------------------------------------------------------------------

        private static Dictionary<string, ModifierDef> AffinityMods()
        {
            var mods = Mods();
            mods["affinity_tourmaline"] = new ModifierDef { Id = "affinity_tourmaline", Target = "monster", MonsterEffectKind = "salvage_affinity", SalvageMaterial = 43, SalvageBaseWcid = 2398, MinMagnitude = 5, MaxMagnitude = 25 };
            mods["affinity_obsidian"] = new ModifierDef { Id = "affinity_obsidian", Target = "monster", MonsterEffectKind = "salvage_affinity", SalvageMaterial = 69, SalvageBaseWcid = 243, MinMagnitude = 5, MaxMagnitude = 25 };
            return mods;
        }

        [TestMethod]
        public void A_gem_with_no_affinity_modifier_has_no_salvage_affinities()
        {
            Assert.AreEqual(0, DungeonRewardMath.SalvageAffinities(Spec(), AffinityMods()).Count);
            Assert.AreEqual(0, DungeonRewardMath.SalvageAffinities(Spec(("hardy", 1.6)), AffinityMods()).Count,
                "an ordinary modifier is not an affinity");
        }

        /// <summary>
        /// The material and the base wcid both come off the ROW, so a fourth material is one JSON row and
        /// nothing downstream has to infer whether a material is a gem or a stone.
        /// </summary>
        [TestMethod]
        public void One_affinity_yields_its_material_base_wcid_and_chance()
        {
            var one = DungeonRewardMath.SalvageAffinities(Spec(("affinity_tourmaline", 20)), AffinityMods());

            Assert.AreEqual(1, one.Count);
            Assert.AreEqual(43, one[0].MaterialId);
            Assert.AreEqual(2398u, one[0].BaseWcid);
            Assert.AreEqual(0.20, one[0].Chance, 1e-9, "magnitude is a PERCENT and is converted to a probability exactly here, once");
        }

        [TestMethod]
        public void Two_affinities_both_come_back_in_gem_order()
        {
            var both = DungeonRewardMath.SalvageAffinities(Spec(("affinity_obsidian", 5), ("affinity_tourmaline", 25)), AffinityMods());

            Assert.AreEqual(2, both.Count);
            Assert.AreEqual((69, 243u), (both[0].MaterialId, both[0].BaseWcid));
            Assert.AreEqual(0.05, both[0].Chance, 1e-9);
            Assert.AreEqual((43, 2398u), (both[1].MaterialId, both[1].BaseWcid));
            Assert.AreEqual(0.25, both[1].Chance, 1e-9);
        }

        /// <summary>
        /// The percent-to-probability conversion happens exactly once, in this method - never at the JSON
        /// boundary and never twice. 100 is certainty, 0 drops the row, and a nonsense magnitude clamps
        /// rather than producing a probability outside [0, 1].
        /// </summary>
        [TestMethod]
        public void Affinity_magnitude_is_a_percent_converted_once()
        {
            Assert.AreEqual(1.0, DungeonRewardMath.SalvageAffinities(Spec(("affinity_tourmaline", 100)), AffinityMods())[0].Chance, 1e-9);
            Assert.AreEqual(1.0, DungeonRewardMath.SalvageAffinities(Spec(("affinity_tourmaline", 400)), AffinityMods())[0].Chance, 1e-9, "clamped, not 4.0");
            Assert.AreEqual(0, DungeonRewardMath.SalvageAffinities(Spec(("affinity_tourmaline", 0)), AffinityMods()).Count, "a zero chance is dropped, not carried");
            Assert.AreEqual(0, DungeonRewardMath.SalvageAffinities(Spec(("affinity_tourmaline", -5)), AffinityMods()).Count);

            var broken = AffinityMods();
            broken["affinity_broken"] = new ModifierDef { Id = "affinity_broken", Target = "monster", MonsterEffectKind = "salvage_affinity", SalvageMaterial = 43, SalvageBaseWcid = 0 };
            Assert.AreEqual(0, DungeonRewardMath.SalvageAffinities(Spec(("affinity_broken", 25)), broken).Count,
                "a row with no base wcid can only ever produce a null at the death path");
        }

        [TestMethod]
        public void Loot_quality_bonus_is_zero_for_shipped_data()
        {
            var store = ThreadDungeonStoreTests.LoadShipped();
            foreach (var def in store.Modifiers.Values)
                Assert.AreEqual(0.0, def.LootQualityBonus, 1e-9, $"{def.Id} carries a loot quality bonus");
        }

        [TestMethod]
        public void Shipped_ladder_reaches_275()
        {
            var store = ThreadDungeonStoreTests.LoadShipped();
            Assert.AreEqual(2500000, DungeonRewardMath.LadderXp(store.XpLadder, 265));
            Assert.AreEqual(2750000, DungeonRewardMath.LadderXp(store.XpLadder, 275));
        }

        [TestMethod]
        public void Health_multiplier_folds_gem_and_extra_modifiers()
        {
            Assert.AreEqual(1.0, DungeonRewardMath.HealthMultiplier(Spec(), Mods(), new string[0]), 1e-9);
            Assert.AreEqual(1.6, DungeonRewardMath.HealthMultiplier(Spec(("hardy", 1.6)), Mods(), new string[0]), 1e-9);
            Assert.AreEqual(1.6 * 1.5, DungeonRewardMath.HealthMultiplier(Spec(("hardy", 1.6)), Mods(), new[] { "boss_guarded" }), 1e-9, "an extra uses its MinMagnitude");
        }

        [TestMethod]
        public void Rating_total_sums_one_kind()
        {
            Assert.AreEqual(20, DungeonRewardMath.RatingTotal(Spec(("savage", 20), ("hardy", 1.6)), Mods(), new string[0], "damage_rating"));
            Assert.AreEqual(0, DungeonRewardMath.RatingTotal(Spec(("savage", 20)), Mods(), new string[0], "crit_rating"));
        }

        // ---- a boss-targeted gem modifier must not leak onto the pack (player report, 2026-09-24) --------

        [TestMethod]
        public void Boss_targeted_rating_modifier_is_excluded_from_the_pack_but_included_for_the_boss()
        {
            var spec = Spec(("boss_enraged", 32), ("savage", 10));

            Assert.AreEqual(10, DungeonRewardMath.RatingTotal(spec, Mods(), Array.Empty<string>(), DungeonRewardMath.DamageRating),
                "boss_enraged (Target: boss) must not stamp its damage rating onto every non-boss creature");
            Assert.AreEqual(42, DungeonRewardMath.RatingTotal(spec, Mods(), Array.Empty<string>(), DungeonRewardMath.DamageRating, forBoss: true),
                "the boss view is a superset: its own rating plus the pack's");
        }

        [TestMethod]
        public void Boss_targeted_health_modifier_is_excluded_from_the_pack_but_included_for_the_boss()
        {
            var spec = Spec(("boss_guarded", 2.0), ("hardy", 1.6));

            Assert.AreEqual(1.6, DungeonRewardMath.HealthMultiplier(spec, Mods(), Array.Empty<string>()), 1e-9,
                "boss_guarded (Target: boss) must not multiply the pack's health");
            Assert.AreEqual(3.2, DungeonRewardMath.HealthMultiplier(spec, Mods(), Array.Empty<string>(), forBoss: true), 1e-9,
                "the boss view folds its own boss_guarded on top of the pack's hardy");
        }

        [TestMethod]
        public void Run_value_returns_magnitude_or_fallback()
        {
            Assert.AreEqual(1.5, DungeonRewardMath.RunValue(Spec(("teeming", 1.5)), Mods(), "count_mult", 1.0), 1e-9);
            Assert.AreEqual(1.0, DungeonRewardMath.RunValue(Spec(), Mods(), "count_mult", 1.0), 1e-9);
        }

        [TestMethod]
        public void Profile_is_capped_and_retail_shaped()
        {
            var p = DungeonRewardMath.BuildProfile(9, 0.9, 0.5, 8);
            Assert.AreEqual(8, p.Tier);
            Assert.AreEqual(0.5, p.LootQualityMod, 1e-9);
            Assert.AreEqual(0u, p.Id);
            Assert.AreEqual(100, p.ItemChance);
            Assert.AreEqual(100, p.MagicItemChance);
            Assert.AreEqual(2, p.MagicItemMaxAmount);
            var low = DungeonRewardMath.BuildProfile(3, 0.1, 0.5, 8);
            Assert.AreEqual(3, low.Tier);
            Assert.AreEqual(0.1, low.LootQualityMod, 1e-6, "LootQualityMod is float; 1e-9 is tighter than float round-trip precision");
        }

        // ---- the boss must ride its pack's health normalize ratio -----------------------------------------

        /// <summary>
        /// Why ThreadDungeonSpawner normalizes the boss's own authored base before handing it to
        /// <see cref="DungeonRewardMath.BossHealthTarget"/>, rather than leaving BossHealthTarget to sort it out.
        ///
        /// The boss floor is term 3: the OBSERVED pack maximum times dynamic_dungeons_boss_health_floor_ratio,
        /// and the whole function is a MAX - it only ever raises. So when the health curve scales a pack DOWN
        /// (every measured ratio at the shipped rungs is at or below 1.0) while the boss keeps its raw authored
        /// health, term 1 wins outright and the designed "boss is 4x its pack" relationship silently becomes
        /// whatever the authored gap happened to be. Normalizing the base by the SAME ratio the pack got is what
        /// preserves it, and BossHealthTarget itself needs no change.
        ///
        /// Worked case: a 10000 hp trash weenie and a 30000 hp boss weenie, a normalize ratio of 0.2, no gem
        /// health multiplier, and the shipped 4.0 floor.
        /// </summary>
        [TestMethod]
        public void A_normalized_boss_base_keeps_the_pack_ratio_where_an_un_normalized_one_does_not()
        {
            const double ratio = 0.2;
            const double floorRatio = DungeonPopulationLimits.DefaultBossHealthFloorRatio;

            // What the pack actually entered the world with, after the same normalization.
            var packMax = ThreadDungeonSpawner.NormalizedBase(10000, ratio);
            Assert.AreEqual(2000u, packMax);

            var normalizedBase = ThreadDungeonSpawner.NormalizedBase(30000, ratio);
            Assert.AreEqual(6000u, normalizedBase);

            var normalized = DungeonRewardMath.BossHealthTarget(normalizedBase, 1.0, packMax, floorRatio);
            Assert.AreEqual(8000, normalized, "term 3 (4x the pack) now binds, which is the designed relationship");
            Assert.AreEqual(floorRatio, (double)normalized / packMax, 1e-9, "exactly 4x its pack");

            var unNormalized = DungeonRewardMath.BossHealthTarget(30000, 1.0, packMax, floorRatio);
            Assert.AreEqual(30000, unNormalized, "term 1 (the raw authored base) wins instead");
            Assert.AreEqual(15.0, (double)unNormalized / packMax, 1e-9, "15x its pack - the bug this avoids");
        }

        // ---- dynamic_dungeons_modifier_reward_scale (ApplyModifierRewardScale) -----------------------------

        /// <summary>s = 1.0 is the neutral no-op: the raw capped product passes through untouched.</summary>
        [TestMethod]
        public void ModifierRewardScale_at_one_is_identity()
        {
            Assert.AreEqual(1.75, DungeonRewardMath.ApplyModifierRewardScale(1.0, 1.75), 1e-9);
            Assert.AreEqual(1.5, DungeonRewardMath.ApplyModifierRewardScale(1.0, 1.5), 1e-9);
            Assert.AreEqual(1.0, DungeonRewardMath.ApplyModifierRewardScale(1.0, 1.0), 1e-9);
        }

        /// <summary>
        /// The two worked examples from the spec: a Hollow-style 1.75 product at s=0.8 becomes 1.60, and an
        /// Enlightened-style 1.5 product at s=0.8 becomes 1.40. effective = 1 + s * (product - 1).
        /// </summary>
        [TestMethod]
        public void ModifierRewardScale_at_point_eight_matches_the_worked_examples()
        {
            Assert.AreEqual(1.60, DungeonRewardMath.ApplyModifierRewardScale(0.8, 1.75), 1e-9);
            Assert.AreEqual(1.40, DungeonRewardMath.ApplyModifierRewardScale(0.8, 1.50), 1e-9);
        }

        /// <summary>s = 0.0 makes every modifier pay exactly retail (1.0) on the four axes it touches.</summary>
        [TestMethod]
        public void ModifierRewardScale_at_zero_is_always_neutral()
        {
            Assert.AreEqual(1.0, DungeonRewardMath.ApplyModifierRewardScale(0.0, 1.75), 1e-9);
            Assert.AreEqual(1.0, DungeonRewardMath.ApplyModifierRewardScale(0.0, 4.0), 1e-9);
            Assert.AreEqual(1.0, DungeonRewardMath.ApplyModifierRewardScale(0.0, 1.0), 1e-9);
        }

        /// <summary>
        /// Applied AFTER the axis's own cap - a product of 2.5 capped to 2.0 (the caller's job, e.g.
        /// DungeonRewardMath.XpMultiplier(spec, mods, cap)) at s=0.8 becomes 1.8, never a scaling of the
        /// UN-capped 2.5 (which would give 2.2).
        /// </summary>
        [TestMethod]
        public void ModifierRewardScale_applies_after_the_axis_cap()
        {
            const double uncapped = 2.5;
            const double cap = 2.0;
            var capped = Math.Min(uncapped, cap);

            Assert.AreEqual(2.0, capped, 1e-9, "sanity: the cap binds before the scale is ever applied");
            Assert.AreEqual(1.8, DungeonRewardMath.ApplyModifierRewardScale(0.8, capped), 1e-9);
            Assert.AreNotEqual(1.8, DungeonRewardMath.ApplyModifierRewardScale(0.8, uncapped), 1e-9,
                "scaling the UN-capped product would give a different (wrong) answer");
        }

        /// <summary>
        /// A product below 1.0 is NOT floored - it is deliberately left to the same weighted-average formula
        /// as everything else (see ApplyModifierRewardScale's doc comment): at s=1.0 (neutral) it passes
        /// through unchanged, which is exactly the existing "a cap below 1.0 is shown because the run really
        /// pays it" behaviour (DungeonGemRewardLinesTests.A_cap_below_one_is_shown_because_the_run_really_pays_it)
        /// that a flooring implementation would have silently broken. NaN is the one input that IS guarded, to
        /// 1.0 (neutral), so a garbled upstream product cannot propagate.
        /// </summary>
        [TestMethod]
        public void ModifierRewardScale_does_not_floor_a_product_below_one_but_guards_nan()
        {
            // s=1.0 (neutral, the default) must be a true identity even for a sub-1.0 product, or the shipped
            // "cap below 1.0" gem text (Experience: x0.50 etc.) would change the moment this dial merges.
            Assert.AreEqual(0.5, DungeonRewardMath.ApplyModifierRewardScale(1.0, 0.5), 1e-9);

            // s=0.8 on a product of 0.5: 1 + 0.8 * (0.5 - 1) = 0.6 - a weighted average of 0.5 and 1.0, never
            // below the product itself.
            Assert.AreEqual(0.6, DungeonRewardMath.ApplyModifierRewardScale(0.8, 0.5), 1e-9);

            // s=0.0 is still always-neutral, even starting from a sub-1.0 product.
            Assert.AreEqual(1.0, DungeonRewardMath.ApplyModifierRewardScale(0.0, 0.5), 1e-9);

            Assert.AreEqual(1.0, DungeonRewardMath.ApplyModifierRewardScale(0.8, double.NaN), 1e-9);
        }

        /// <summary>Garbage scale input (NaN/negative/out of range) sanitizes the same way the neighbouring
        /// reward dials do, via ThreadDungeonSpawner.SanitizeDoubleDial - NaN/Infinity/negative reads as the
        /// compiled default (1.0, neutral); anything above the domain ceiling of 1.0 reads as 1.0.</summary>
        [TestMethod]
        public void ModifierRewardScale_garbage_dial_input_sanitizes_via_SanitizeDoubleDial()
        {
            const double fallback = DungeonPopulationLimits.DefaultModifierRewardScale;
            const double ceiling = DungeonPopulationLimits.MaxModifierRewardScale;

            Assert.AreEqual(fallback, ThreadDungeonSpawner.SanitizeDoubleDial(double.NaN, fallback, ceiling), 1e-9);
            Assert.AreEqual(fallback, ThreadDungeonSpawner.SanitizeDoubleDial(double.PositiveInfinity, fallback, ceiling), 1e-9);
            Assert.AreEqual(fallback, ThreadDungeonSpawner.SanitizeDoubleDial(-0.5, fallback, ceiling), 1e-9);
            Assert.AreEqual(0.0, ThreadDungeonSpawner.SanitizeDoubleDial(0.0, fallback, ceiling), 1e-9, "0 is a valid explicit value, not garbage");
            Assert.AreEqual(1.0, ThreadDungeonSpawner.SanitizeDoubleDial(5.0, fallback, ceiling), 1e-9, "above the domain ceiling clamps to 1.0");
            Assert.AreEqual(0.8, ThreadDungeonSpawner.SanitizeDoubleDial(0.8, fallback, ceiling), 1e-9);
        }

        // ---- dynamic_dungeons_salvage_affinity_chance_cap ------------------------------------------------

        /// <summary>Same sanitize contract as dynamic_dungeons_modifier_reward_scale: NaN/Infinity/negative
        /// reads as the default (1.0, neutral); above the domain ceiling of 1.0 reads as 1.0.</summary>
        [TestMethod]
        public void SalvageAffinityChanceCap_garbage_dial_input_sanitizes_via_SanitizeDoubleDial()
        {
            const double fallback = DungeonPopulationLimits.DefaultSalvageAffinityChanceCap;
            const double ceiling = DungeonPopulationLimits.MaxSalvageAffinityChanceCap;

            Assert.AreEqual(fallback, ThreadDungeonSpawner.SanitizeDoubleDial(double.NaN, fallback, ceiling), 1e-9);
            Assert.AreEqual(fallback, ThreadDungeonSpawner.SanitizeDoubleDial(double.PositiveInfinity, fallback, ceiling), 1e-9);
            Assert.AreEqual(fallback, ThreadDungeonSpawner.SanitizeDoubleDial(-0.5, fallback, ceiling), 1e-9);
            Assert.AreEqual(0.0, ThreadDungeonSpawner.SanitizeDoubleDial(0.0, fallback, ceiling), 1e-9, "0 is a valid explicit value, not garbage");
            Assert.AreEqual(1.0, ThreadDungeonSpawner.SanitizeDoubleDial(5.0, fallback, ceiling), 1e-9, "above the domain ceiling clamps to 1.0");
            Assert.AreEqual(0.25, ThreadDungeonSpawner.SanitizeDoubleDial(0.25, fallback, ceiling), 1e-9, "the owner's planned launch value");
        }
    }
}
