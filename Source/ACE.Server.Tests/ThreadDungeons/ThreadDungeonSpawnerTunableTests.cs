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

        // ---- NonBossHealthTarget: normalize-then-floor-then-multiply --------------------------------------

        /// <summary>
        /// A normalize ratio of 1.0 is the PRE-CURVE three-term formula, so every case in this block passes it
        /// and reads as the assertion it has always been. The curve's own arithmetic is asserted separately
        /// below and in DungeonHealthCurveTests.
        /// </summary>
        private const double NoNormalize = 1.0;

        [TestMethod]
        public void Floor_is_applied_before_the_health_multiplier_not_after()
        {
            // The regression case from the bug report: an authored 285 hp creature, a band floor of 4000, and
            // a 1.5x gem multiplier must land at 4000 * 1.5 = 6000, NOT at max(285 * 1.5, 4000) = max(428, 4000).
            var target = ThreadDungeonSpawner.NonBossHealthTarget(285, NoNormalize, 1.5, 4000);
            Assert.AreEqual(6000, target);
            Assert.AreNotEqual(4000, target, "a post-multiply floor would have landed here instead");
        }

        [TestMethod]
        public void The_floor_never_lowers_a_creature_already_above_it()
        {
            var target = ThreadDungeonSpawner.NonBossHealthTarget(20000, NoNormalize, 1.0, 4000);
            Assert.AreEqual(20000, target);
        }

        [TestMethod]
        public void A_zero_floor_is_a_no_op()
        {
            Assert.AreEqual(428, ThreadDungeonSpawner.NonBossHealthTarget(285, NoNormalize, 1.5, 0));
        }

        // ---- NormalizedBase and the normalize term in NonBossHealthTarget ---------------------------------

        /// <summary>
        /// The contract that makes a switched-off run byte-for-byte the run it was before the curve existed:
        /// a ratio of 0 (switch off, or no usable band sample) and a ratio of exactly 1.0 must both be
        /// arithmetically identical to the pre-change three-argument call, for every combination of floor and
        /// multiplier - not merely close to it.
        /// </summary>
        [TestMethod]
        public void A_ratio_of_zero_or_one_reproduces_the_pre_curve_formula_exactly()
        {
            foreach (var (authored, hpMult, floor) in new[]
            {
                (285u, 1.5, 4000u), (20000u, 1.0, 4000u), (285u, 1.5, 0u), (8045u, 2.0, 0u), (1u, 1.0, 0u),
            })
            {
                // The pre-change formula, written out here rather than called, so this test pins the OLD
                // arithmetic independently of the new implementation it is checking.
                var expected = (int)System.Math.Clamp(System.Math.Round(System.Math.Max(authored, floor) * hpMult), 1, int.MaxValue);

                Assert.AreEqual(expected, ThreadDungeonSpawner.NonBossHealthTarget(authored, 0.0, hpMult, floor),
                    $"ratio 0 must be a no-op (authored {authored}, mult {hpMult}, floor {floor})");
                Assert.AreEqual(expected, ThreadDungeonSpawner.NonBossHealthTarget(authored, 1.0, hpMult, floor),
                    $"ratio 1.0 must be a no-op (authored {authored}, mult {hpMult}, floor {floor})");
            }
        }

        [TestMethod]
        public void Normalized_base_scales_and_rounds_to_nearest()
        {
            Assert.AreEqual(2000u, ThreadDungeonSpawner.NormalizedBase(10000, 0.2));
            Assert.AreEqual(1500u, ThreadDungeonSpawner.NormalizedBase(1000, 1.5));

            // Math.Round's banker's rounding is the ONE rounding rule both the trash and boss paths share;
            // 1001 * 0.5 = 500.5 rounds to the even 500, not up to 501.
            Assert.AreEqual(500u, ThreadDungeonSpawner.NormalizedBase(1001, 0.5));
        }

        [TestMethod]
        public void Normalized_base_is_a_no_op_for_every_garbled_or_neutral_ratio()
        {
            foreach (var ratio in new[] { 0.0, 1.0, -0.5, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
                Assert.AreEqual(8045u, ThreadDungeonSpawner.NormalizedBase(8045, ratio), $"ratio {ratio} must leave the authored value alone");
        }

        [TestMethod]
        public void Normalized_base_never_returns_zero()
        {
            // A vital of 0 is degenerate (WorldEventSpawner.ApplyHealth clamps StartingValue to 1 for the same
            // reason), so a tiny creature under a tiny ratio floors at 1 rather than vanishing.
            Assert.AreEqual(1u, ThreadDungeonSpawner.NormalizedBase(3, 0.01));
        }

        [TestMethod]
        public void The_floor_is_measured_against_the_normalized_base_not_the_authored_one()
        {
            // Authored 10000 normalized at 0.2 is 2000, which a floor of 3000 then raises; the 1.5x multiplier
            // lands it at 4500. Normalizing AFTER the floor would have given max(10000, 3000) * 0.2 * 1.5 =
            // 3000, and skipping the normalization entirely would have given 15000.
            Assert.AreEqual(4500, ThreadDungeonSpawner.NonBossHealthTarget(10000, 0.2, 1.5, 3000));
            Assert.AreEqual(15000, ThreadDungeonSpawner.NonBossHealthTarget(10000, 1.0, 1.5, 3000), "the un-normalized answer, for contrast");
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

        // ---- hollow intensity: the plan's roll never lowers an authored hollow (owner ruling 2026-09-17) ----

        [TestMethod]
        public void ResolveSpawnedHollowIntensity_writes_the_plan_intensity_on_an_unauthored_creature()
        {
            Assert.AreEqual(0.4, ThreadDungeonSpawner.ResolveSpawnedHollowIntensity(0.4, false, null), 1e-9);
            Assert.AreEqual(0.4, ThreadDungeonSpawner.ResolveSpawnedHollowIntensity(0.4, false, 0.9), 1e-9,
                "a stray HollowIntensity with no flag is not 'already hollow'");
        }

        [TestMethod]
        public void ResolveSpawnedHollowIntensity_never_lowers_an_authored_hollow()
        {
            // Flag-only authoring (no PropertyFloat) reads as full 1.0, same as HollowMath.ClampOrOne.
            Assert.AreEqual(1.0, ThreadDungeonSpawner.ResolveSpawnedHollowIntensity(0.4, true, null), 1e-9,
                "an author-set flag with no roll is full hollow and must not be lowered to the plan's 0.4");

            Assert.AreEqual(0.9, ThreadDungeonSpawner.ResolveSpawnedHollowIntensity(0.4, true, 0.9), 1e-9,
                "an authored 0.9 outranks the plan's 0.4");

            Assert.AreEqual(0.4, ThreadDungeonSpawner.ResolveSpawnedHollowIntensity(0.4, true, 0.1), 1e-9,
                "the plan may still RAISE an authored intensity below its own");
        }

        [TestMethod]
        public void ResolveSpawnedHollowIntensity_clamps_a_garbled_authored_intensity()
        {
            Assert.AreEqual(1.0, ThreadDungeonSpawner.ResolveSpawnedHollowIntensity(0.4, true, double.NaN), 1e-9);
            Assert.AreEqual(1.0, ThreadDungeonSpawner.ResolveSpawnedHollowIntensity(0.4, true, 1.5), 1e-9, "clamped, not left above 1");
        }

        // ---- dynamic_dungeons_uplift_attributes (owner ruling 2026-10-06) -----------------------------------

        private static DungeonBandStandard UsableStandard()
            => DungeonBandStandard.ForTest(new System.Collections.Generic.Dictionary<ACE.Entity.Enum.Skill, uint>(), 10, 10);

        [TestMethod]
        public void The_attribute_uplift_gate_needs_an_uplift_a_non_boss_a_standard_and_the_switch()
        {
            Assert.IsTrue(ThreadDungeonSpawner.UpliftsAttributes(200, DungeonRole.Trash, UsableStandard(), true));
            Assert.IsTrue(ThreadDungeonSpawner.UpliftsAttributes(200, DungeonRole.Elite, UsableStandard(), true));

            Assert.IsFalse(ThreadDungeonSpawner.UpliftsAttributes(200, DungeonRole.Trash, UsableStandard(), false), "kill switch off");
            Assert.IsFalse(ThreadDungeonSpawner.UpliftsAttributes(0, DungeonRole.Trash, UsableStandard(), true), "not uplifted");
            Assert.IsFalse(ThreadDungeonSpawner.UpliftsAttributes(200, DungeonRole.Boss, UsableStandard(), true), "a boss has its own path");
            Assert.IsFalse(ThreadDungeonSpawner.UpliftsAttributes(200, DungeonRole.Trash, DungeonBandStandard.Empty, true));
            Assert.IsFalse(ThreadDungeonSpawner.UpliftsAttributes(200, DungeonRole.Trash, null, true));
        }

        [TestMethod]
        public void The_attribute_uplift_defaults_on()
        {
            Assert.IsTrue(DungeonPopulationLimits.DefaultUpliftAttributes);
        }

        [TestMethod]
        public void The_uplift_log_line_reports_the_attributes_raised()
        {
            var line = ThreadDungeonSpawner.UpliftLogLine(910u, 200, 160, 3, 4, 2, UsableStandard(), attributesRaised: 5);

            StringAssert.Contains(line, "5 attribute(s) raised");
            StringAssert.Contains(line, "3 skill(s) raised, 4 body part(s) scaled toward", "the pre-existing wording is unchanged");
            Assert.IsFalse(line.Contains("topped up"), "the top-up clause only appears when it did something");

            var topped = ThreadDungeonSpawner.UpliftLogLine(910u, 200, 160, 3, 4, 2, UsableStandard(), 5, 2, 1);
            StringAssert.Contains(topped, "2 skill(s) topped up to the effective median, 1 defense ceiling(s) set");
        }

        [TestMethod]
        public void Health_is_written_from_the_authored_max_and_skipped_only_against_the_live_max()
        {
            // The Endurance raise moved the live max from 1000 to 1150. The target is still computed from the
            // AUTHORED 1000, so health ends exactly where it lands without the raise...
            var write = ThreadDungeonSpawner.NonBossHealthWrite(authoredMax: 1000, liveMax: 1150, normalizeRatio: 1.0, hpMult: 1.0, floor: 0);

            Assert.AreEqual(1000, write, "target from the authored max");
            Assert.IsNotNull(write, "the raised live max (1150) differs from the target, so a write is REQUIRED to bring it back; a skip check against authoredMax would wrongly skip");

            // ...and with no raise (live == authored == target) nothing is written, exactly as before.
            Assert.IsNull(ThreadDungeonSpawner.NonBossHealthWrite(1000, 1000, 1.0, 1.0, 0));

            // A real health target (normalize + floor + multiplier) is still taken from the authored value.
            Assert.AreEqual(ThreadDungeonSpawner.NonBossHealthTarget(1000, 2.0, 1.5, 0), ThreadDungeonSpawner.NonBossHealthWrite(1000, 1150, 2.0, 1.5, 0));
        }

        private static readonly string SpawnerPath = "Source/ACE.Server/ThreadDungeons/ThreadDungeonSpawner.cs";

        private static string PlaceBody()
        {
            var src = PooledLootSourceText.Read(SpawnerPath);
            var start = src.IndexOf("var upliftAttributes = UpliftsAttributes(", System.StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, "the spawner must compute upliftAttributes through UpliftsAttributes");
            return src.Substring(start);
        }

        [TestMethod]
        public void Spawner_order_captures_authored_health_then_raises_attributes_then_writes_health()
        {
            var body = PlaceBody();

            var capture = body.IndexOf("healthMaxBeforeUplift = preRaiseMax;", System.StringComparison.Ordinal);
            var raise = body.IndexOf("BandUplift.RaiseAttributes(creature, plan.BandStandard)", System.StringComparison.Ordinal);
            // 2026-10-08: the base also falls back to the reach-up stamp's capture (see the next test).
            var baseRead = body.IndexOf("var authoredMax = AuthoredHealthMax(healthMaxBeforeUplift, healthMaxBeforeStamp, creature.Health.MaxValue);", System.StringComparison.Ordinal);
            var write = body.IndexOf("NonBossHealthWrite(authoredMax, creature.Health.MaxValue", System.StringComparison.Ordinal);

            Assert.IsTrue(capture >= 0 && raise >= 0 && baseRead >= 0 && write >= 0, "all four anchors must exist");
            Assert.IsTrue(body.IndexOf("var preRaiseMax = creature.Health.MaxValue;", System.StringComparison.Ordinal) is var pre && pre >= 0 && pre < raise && capture < raise, "the authored max is read and captured BEFORE the attribute raise");
            Assert.IsTrue(raise < baseRead, "the attribute raise runs BEFORE the health block");
            Assert.IsTrue(baseRead < write, "the health write comes last and skips against the LIVE max");
        }

        /// <summary>
        /// The reach-up stamp (2026-10-08) scales Endurance like the uplift's attribute raise does, so it must
        /// obey the same order: read the authored max, scale, capture, and only then let the health block price
        /// the creature off that captured AUTHORED max - or the scaled Endurance would leak into health and the
        /// run's health terms would be applied on top of it.
        /// </summary>
        [TestMethod]
        public void Spawner_order_captures_authored_health_before_the_reach_up_stamp_scales_attributes()
        {
            var src = PooledLootSourceText.Read(SpawnerPath);

            var pre = src.IndexOf("var preStampMax = creature.Health.MaxValue;", System.StringComparison.Ordinal);
            var apply = src.IndexOf("ReachUpStamp.Apply(creature, authoredLevel, entry.StampLevel, stampCurve, stampHealthTarget)", System.StringComparison.Ordinal);
            var capture = src.IndexOf("healthMaxBeforeStamp = HealthMaxBeforeStamp(stamped, preStampMax);", System.StringComparison.Ordinal);
            var strip = src.IndexOf("DungeonCreatureNormalizer.StripCombatTraits(creature, plan, entry.Role == DungeonRole.Boss, capStandard)", System.StringComparison.Ordinal);
            var baseRead = src.IndexOf("var authoredMax = AuthoredHealthMax(healthMaxBeforeUplift, healthMaxBeforeStamp, creature.Health.MaxValue);", System.StringComparison.Ordinal);

            Assert.IsTrue(pre >= 0 && apply >= 0 && capture >= 0 && strip >= 0 && baseRead >= 0, "all five anchors must exist");
            Assert.IsTrue(pre < apply && apply < capture, "the authored max is read BEFORE the stamp and captured after it");
            Assert.IsTrue(apply < strip, "the stamp runs BEFORE the defense ceiling, so the ceiling measures the scaled skill");
            Assert.IsTrue(capture < baseRead, "the health block reads the capture AFTER it is set");
        }

        [TestMethod]
        public void Run_speed_multiplier_is_applied_after_the_uplift_floor()
        {
            var body = PlaceBody();

            var uplift = body.IndexOf("ApplyUplift(run, creature, entry, plan, ownLevel, spellsRaised, attributesRaised, upliftAttributes);", System.StringComparison.Ordinal);
            var runMult = body.IndexOf("runSkill.InitLevel = (uint)Math.Clamp(Math.Round(runSkill.InitLevel * plan.RunSpeedMult)", System.StringComparison.Ordinal);

            Assert.IsTrue(uplift >= 0 && runMult >= 0, "both anchors must exist");
            Assert.IsTrue(uplift < runMult,
                "RunSpeedMult must multiply the InitLevel AFTER the uplift's Run floor, or the floor overwrites part of the gem's modifier");
            Assert.AreEqual(runMult, body.LastIndexOf("runSkill.InitLevel = (uint)Math.Clamp(Math.Round(runSkill.InitLevel * plan.RunSpeedMult)", System.StringComparison.Ordinal),
                "exactly one run-speed write");
        }

        [TestMethod]
        public void An_endurance_raise_that_makes_the_health_target_unreachable_is_refused()
        {
            // Authored max 100 with End raised to 400: the raised Endurance alone contributes 200, so ApplyHealth
            // would clamp StartingValue to 1 and max health would land at 201, not 100.
            Assert.IsFalse(ThreadDungeonSpawner.UpliftAttributeRaiseKeepsHealthReachable(100, 400));
            Assert.IsFalse(ThreadDungeonSpawner.UpliftAttributeRaiseKeepsHealthReachable(200, 400), "a target equal to the term is still unreachable");
            Assert.IsTrue(ThreadDungeonSpawner.UpliftAttributeRaiseKeepsHealthReachable(201, 400));
            Assert.IsTrue(ThreadDungeonSpawner.UpliftAttributeRaiseKeepsHealthReachable(5000, 400));
        }

        [TestMethod]
        public void The_uplift_log_line_omits_the_attribute_field_when_none_were_raised()
        {
            var none = ThreadDungeonSpawner.UpliftLogLine(910u, 200, 160, 3, 4, 2, UsableStandard(), attributesRaised: 0);

            Assert.IsFalse(none.Contains("attribute(s) raised"));
            StringAssert.Contains(none, "spell(s) raised to tier");
        }

        [TestMethod]
        public void The_run_speed_write_never_adds_a_run_skill()
        {
            var body = PlaceBody();

            StringAssert.Contains(body, "creature.GetCreatureSkill(Skill.Run, false)");
            Assert.IsFalse(body.Contains("creature.GetCreatureSkill(Skill.Run)"),
                "the default overload ADDS an Untrained Run row at InitLevel 0 (and a Run-less creature must not get one)");
        }
    }
}
