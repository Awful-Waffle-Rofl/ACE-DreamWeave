using ACE.Server.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The sanitizers the four boss tunables are read through. They are separated from the PropertyManager
    /// read for exactly this reason: a PropertyManager read throws under the test harness, so the arithmetic
    /// can only be asserted if it lives in a pure function.
    ///
    /// One rule, four dials: NaN, Infinity or a negative value falls back to the compiled default, zero is a
    /// valid explicit "disable this axis", and the two doubles additionally clamp at a typo ceiling.
    ///
    /// The internals are visible via InternalsVisibleTo in ACE.Server.csproj:15.
    /// </summary>
    [TestClass]
    public class ThreadDungeonSpawnerTunableTests
    {
        private const double Ratio = DungeonPopulationLimits.DefaultBossHealthFloorRatio;
        private const double RatioMax = DungeonPopulationLimits.MaxBossHealthFloorRatio;
        private const double Margin = DungeonPopulationLimits.DefaultBossLevelMargin;
        private const double MarginMax = DungeonPopulationLimits.MaxBossLevelMargin;

        [TestMethod]
        public void A_fat_fingered_ratio_is_capped_at_the_ceiling()
        {
            // The case that motivated the ceiling: BossHealthTarget saturates its RESULT at int.MaxValue, so
            // without this the boss of every dungeon opened afterwards has about two billion health.
            Assert.AreEqual(RatioMax, ThreadDungeonSpawner.SanitizeDoubleDial(100000.0, Ratio, RatioMax), 1e-9);
            Assert.AreEqual(RatioMax, ThreadDungeonSpawner.SanitizeDoubleDial(RatioMax + 0.5, Ratio, RatioMax), 1e-9);
            Assert.AreEqual(RatioMax, ThreadDungeonSpawner.SanitizeDoubleDial(double.MaxValue, Ratio, RatioMax), 1e-9);
        }

        [TestMethod]
        public void A_fat_fingered_level_margin_is_capped_at_the_ceiling()
        {
            Assert.AreEqual(MarginMax, ThreadDungeonSpawner.SanitizeDoubleDial(110.0, Margin, MarginMax), 1e-9);
            Assert.AreEqual(MarginMax, ThreadDungeonSpawner.SanitizeDoubleDial(MarginMax + 0.001, Margin, MarginMax), 1e-9);
        }

        [TestMethod]
        public void The_ceiling_is_the_last_admissible_value_not_the_first_rejected_one()
        {
            Assert.AreEqual(RatioMax, ThreadDungeonSpawner.SanitizeDoubleDial(RatioMax, Ratio, RatioMax), 1e-9);
            Assert.AreEqual(MarginMax, ThreadDungeonSpawner.SanitizeDoubleDial(MarginMax, Margin, MarginMax), 1e-9);
        }

        [TestMethod]
        public void A_dial_inside_the_range_is_passed_through_untouched()
        {
            Assert.AreEqual(6.5, ThreadDungeonSpawner.SanitizeDoubleDial(6.5, Ratio, RatioMax), 1e-9);
            Assert.AreEqual(1.25, ThreadDungeonSpawner.SanitizeDoubleDial(1.25, Margin, MarginMax), 1e-9);
        }

        [TestMethod]
        public void A_garbled_dial_falls_back_to_the_compiled_default()
        {
            Assert.AreEqual(Ratio, ThreadDungeonSpawner.SanitizeDoubleDial(double.NaN, Ratio, RatioMax), 1e-9);
            Assert.AreEqual(Ratio, ThreadDungeonSpawner.SanitizeDoubleDial(double.PositiveInfinity, Ratio, RatioMax), 1e-9);
            Assert.AreEqual(Ratio, ThreadDungeonSpawner.SanitizeDoubleDial(double.NegativeInfinity, Ratio, RatioMax), 1e-9);
            Assert.AreEqual(Ratio, ThreadDungeonSpawner.SanitizeDoubleDial(-0.5, Ratio, RatioMax), 1e-9);
            Assert.AreEqual(Margin, ThreadDungeonSpawner.SanitizeDoubleDial(-2.0, Margin, MarginMax), 1e-9);
        }

        [TestMethod]
        public void Zero_is_a_valid_explicit_disable_on_a_dial()
        {
            // Zero must survive: it is the documented way to turn one axis off, and BossHealthTarget reads
            // any ratio <= 0 as "no floor". Folding it into the fallback would make the axis unturnoffable.
            Assert.AreEqual(0.0, ThreadDungeonSpawner.SanitizeDoubleDial(0.0, Ratio, RatioMax), 1e-9);
            Assert.AreEqual(0.0, ThreadDungeonSpawner.SanitizeDoubleDial(0.0, Margin, MarginMax), 1e-9);
        }

        [TestMethod]
        public void A_negative_floor_falls_back_to_the_compiled_default_rather_than_to_zero()
        {
            // The bug this pins: clamping to 0 instead would silently REMOVE the damage floor, which is the
            // opposite of what an admin typing a negative number could possibly have meant.
            Assert.AreEqual(DungeonPopulationLimits.DefaultBossDamageRatingFloor,
                ThreadDungeonSpawner.SanitizeBossFloor(-10, DungeonPopulationLimits.DefaultBossDamageRatingFloor));
            Assert.AreEqual(DungeonPopulationLimits.DefaultBossDamageResistFloor,
                ThreadDungeonSpawner.SanitizeBossFloor(long.MinValue, DungeonPopulationLimits.DefaultBossDamageResistFloor));
        }

        [TestMethod]
        public void Zero_is_a_valid_explicit_disable_on_a_floor()
        {
            Assert.AreEqual(0, ThreadDungeonSpawner.SanitizeBossFloor(0, DungeonPopulationLimits.DefaultBossDamageRatingFloor));
            Assert.AreEqual(0, ThreadDungeonSpawner.SanitizeBossFloor(0, DungeonPopulationLimits.DefaultBossDamageResistFloor));
        }

        [TestMethod]
        public void A_floor_inside_the_range_is_passed_through_and_an_oversized_one_saturates()
        {
            Assert.AreEqual(45, ThreadDungeonSpawner.SanitizeBossFloor(45, DungeonPopulationLimits.DefaultBossDamageRatingFloor));
            Assert.AreEqual(int.MaxValue, ThreadDungeonSpawner.SanitizeBossFloor(long.MaxValue, DungeonPopulationLimits.DefaultBossDamageRatingFloor),
                "a bare cast would wrap negative and disable the floor it was meant to raise");
        }

        /// <summary>
        /// ResolveRewardScaleRatio is internal (visible here via the same InternalsVisibleTo this class's own
        /// summary already documents), so it is cheap to hit directly rather than only indirectly through
        /// ComposeLongDesc or DungeonPopulationBuilder.Build. Unlike SanitizeDoubleDial/SanitizeBossFloor
        /// above, this method reads PropertyManager, so both cases seed the cache directly (ModifyBool/
        /// ModifyDouble - no DB round trip, same pattern as DungeonGemLongDescTests.SeedRewardScaleProperties)
        /// and restore the switch to its compiled default afterwards, since PropertyManager's cache is static
        /// and shared with every other test in the assembly.
        /// </summary>
        [TestMethod]
        public void Resolve_reward_scale_ratio_reads_the_live_master_switch()
        {
            ACE.Server.Managers.PropertyManager.ModifyDouble("dynamic_dungeons_reward_scale_anchor", DungeonPopulationLimits.DefaultRewardScaleAnchor);
            ACE.Server.Managers.PropertyManager.ModifyDouble("dynamic_dungeons_reward_scale_exponent", DungeonPopulationLimits.DefaultRewardScaleExponent);
            ACE.Server.Managers.PropertyManager.ModifyDouble("dynamic_dungeons_reward_scale_floor", DungeonPopulationLimits.DefaultRewardScaleFloor);
            ACE.Server.Managers.PropertyManager.ModifyDouble("dynamic_dungeons_reward_scale_cap", DungeonPopulationLimits.DefaultRewardScaleCap);

            var switchOnOk = ACE.Server.Managers.PropertyManager.ModifyBool("dynamic_dungeons_reward_scaling", true);
            Assert.IsTrue(switchOnOk, "ModifyBool(dynamic_dungeons_reward_scaling, true) returned false - key not registered in DefaultBooleanProperties");

            try
            {
                // level 185 with the switch on: ratio(185) = (185/300)^2.87, clamped to the 0.10 floor - the
                // same value DungeonPopulationBuilderTests pins for the pure DungeonRewardMath.RewardScaleRatio
                // call, confirming the live PropertyManager path agrees with the pure math it wraps.
                Assert.AreEqual(DungeonRewardMath.RewardScaleRatio(185, DungeonPopulationLimits.DefaultRewardScaleAnchor,
                    DungeonPopulationLimits.DefaultRewardScaleExponent, DungeonPopulationLimits.DefaultRewardScaleFloor,
                    DungeonPopulationLimits.DefaultRewardScaleCap), ThreadDungeonSpawner.ResolveRewardScaleRatio(185), 1e-9);

                ACE.Server.Managers.PropertyManager.ModifyBool("dynamic_dungeons_reward_scaling", false);

                // level 300 (the anchor, where ratio(level)=1.0 with the switch on) makes the switch-off case
                // unambiguous: only "always exactly 1.0" proves the switch bypassed the curve, since 1.0 is
                // also this level's ON-state answer.
                Assert.AreEqual(1.0, ThreadDungeonSpawner.ResolveRewardScaleRatio(185), 1e-9);
                Assert.AreEqual(1.0, ThreadDungeonSpawner.ResolveRewardScaleRatio(300), 1e-9);
            }
            finally
            {
                ACE.Server.Managers.PropertyManager.ModifyBool("dynamic_dungeons_reward_scaling", DungeonPopulationLimits.DefaultRewardScalingEnabled);
            }
        }

        // ---- dynamic_dungeons_trash_health_floor_ratio (SanitizeDoubleDial reused, own constants) --------

        private const double TrashRatio = DungeonPopulationLimits.DefaultTrashHealthFloorRatio;
        private const double TrashRatioMax = DungeonPopulationLimits.MaxTrashHealthFloorRatio;

        [TestMethod]
        public void A_negative_trash_health_floor_ratio_falls_back_to_the_compiled_default()
        {
            Assert.AreEqual(TrashRatio, ThreadDungeonSpawner.SanitizeDoubleDial(-1.0, TrashRatio, TrashRatioMax), 1e-9);
            Assert.AreEqual(TrashRatio, ThreadDungeonSpawner.SanitizeDoubleDial(double.NaN, TrashRatio, TrashRatioMax), 1e-9);
        }

        [TestMethod]
        public void A_fat_fingered_trash_health_floor_ratio_is_capped_at_the_ceiling()
        {
            Assert.AreEqual(TrashRatioMax, ThreadDungeonSpawner.SanitizeDoubleDial(1000.0, TrashRatio, TrashRatioMax), 1e-9);
        }

        [TestMethod]
        public void Zero_disables_the_trash_health_floor_ratio()
        {
            Assert.AreEqual(0.0, ThreadDungeonSpawner.SanitizeDoubleDial(0.0, TrashRatio, TrashRatioMax), 1e-9);
        }

        // ---- NonBossHealthTarget: floor-then-multiply -----------------------------------------------------

        [TestMethod]
        public void Floor_is_applied_before_the_health_multiplier_not_after()
        {
            // The regression case from the bug report: an authored 285 hp creature, a band floor of 4000, and
            // a 1.5x gem multiplier must land at 4000 * 1.5 = 6000, NOT at max(285 * 1.5, 4000) = max(428, 4000).
            var target = ThreadDungeonSpawner.NonBossHealthTarget(285, 1.5, 4000);
            Assert.AreEqual(6000, target);
            Assert.AreNotEqual(4000, target, "a post-multiply floor would have landed here instead");
        }

        [TestMethod]
        public void The_floor_never_lowers_a_creature_already_above_it()
        {
            var target = ThreadDungeonSpawner.NonBossHealthTarget(20000, 1.0, 4000);
            Assert.AreEqual(20000, target);
        }

        [TestMethod]
        public void A_zero_floor_is_a_no_op()
        {
            Assert.AreEqual(428, ThreadDungeonSpawner.NonBossHealthTarget(285, 1.5, 0));
        }

        // ---- HealthFromAttributes: LevelFromCP ("Ranks") must be included ---------------------------------

        [TestMethod]
        public void HealthFromAttributes_includes_InitLevel_plus_half_endurance()
        {
            // The non-CP case CreatureCombatTests.MaxHealth_AddsHalfEnduranceToInitLevel already verifies
            // against live appraisal: InitLevel 100, Endurance 500 -> 100 + 250 = 350.
            Assert.AreEqual(350u, ThreadDungeonSpawner.HealthFromAttributes(100, 0, 500));
        }

        [TestMethod]
        public void HealthFromAttributes_includes_LevelFromCP_for_CP_loaded_weenies()
        {
            // Reviewer's worked case: wcid 35268 Spectral Dread, InitLevel 0, LevelFromCP 20000, Endurance
            // InitLevel 500. Omitting LevelFromCP (the pre-fix formula) returns 0 + 250 = 250, an 81x
            // undercount; the fix must return 0 + 20000 + 250 = 20250.
            var health = ThreadDungeonSpawner.HealthFromAttributes(0, 20000, 500);
            Assert.AreEqual(20250u, health);
            Assert.AreNotEqual(250u, health, "LevelFromCP was dropped -- this is the pre-fix undercount");
        }
    }
}
