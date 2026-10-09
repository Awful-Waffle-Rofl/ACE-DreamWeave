using System;
using System.Collections.Generic;
using System.Linq;

using ACE.DatLoader.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The 2026-10-08 run ceiling raise (owner rulings, final): the gem ceiling stays 375, the run level can
    /// reach 500 (a 375 fragment plus a Mana Scarab presses to 410), reach-up applies from the pivot upward,
    /// every monster stat follows its natural curve past the authored data, and effective defense above monster
    /// level 375 grows at half its fitted rate.
    ///
    /// The plan-level tests run against the SHIPPED roster (ShippedRosterFixture), because the pivot is derived
    /// from the roster it is handed - a toy fixture would put its pivot wherever its own top happened to be.
    /// "Switch off reproduces today" is pinned against plan hashes captured from the unmodified builder
    /// (origin/master 2a56f4c31) with the same inputs, so it compares against the real pre-change behaviour
    /// rather than against this branch's idea of it.
    /// </summary>
    [TestClass]
    public class ThreadsRunCeilingTests
    {
        // ---- fixtures ------------------------------------------------------------------------------------

        private static DungeonGemSpec Spec(int level, int seed = 1) => new DungeonGemSpec("filos_doom", level, 6, "any", seed, null, 0, 0);

        /// <summary>The limits the master hashes were captured with: (60, 3.0, 3.0, 8, 0.5) and every other default.</summary>
        private static DungeonPopulationLimits MasterLimits(bool reachUp = DungeonPopulationLimits.DefaultReachUp,
            int healthCurveTopLevel = DungeonPopulationLimits.DefaultHealthCurveTopLevel,
            bool statCurve = DungeonPopulationLimits.DefaultStatCurve,
            double defenseRate = DungeonPopulationLimits.DefaultDefenseCurveRateAbove375)
            => new DungeonPopulationLimits(60, 3.0, 3.0, 8, 0.5,
                healthCurveTopLevel: healthCurveTopLevel, reachUp: reachUp, statCurve: statCurve, defenseCurveRateAbove375: defenseRate);

        private static DungeonSpawnPlan Build(int level, int seed, DungeonPopulationLimits limits, Func<uint, DungeonStatProfile> profileOf = null,
            ThreadPlanMemo memo = null)
        {
            var tables = ShippedRosterFixture.Tables;
            Assert.IsNotNull(tables, "Content/events/axes/species not found from the test output directory");

            Func<uint, int> levelOf = ShippedRosterFixture.LevelOf;
            Func<uint, uint> healthOf = ShippedRosterFixture.HealthOf;

            if (memo != null)
            {
                levelOf = memo.LevelOf;
                healthOf = memo.HealthOf;
                profileOf = profileOf == null ? null : memo.ProfileOf;
            }

            return DungeonPopulationBuilder.Build(Spec(level, seed), ShippedRosterFixture.Dungeon(30), ThreadDungeonStoreTests.LoadShipped(),
                tables, levelOf, healthOf, limits, new Random(seed), profileOf, null, memo);
        }

        /// <summary>
        /// Plan hashes from the UNMODIFIED builder (origin/master 2a56f4c31, captured 2026-10-08 by a temporary
        /// test over exactly these inputs: MasterLimits() defaults, the shipped store and roster, Dungeon(30),
        /// Spec(level, seed), new Random(seed), no profileOf).
        /// </summary>
        private static readonly Dictionary<(int Level, int Seed), string> MasterHashes = new Dictionary<(int, int), string>
        {
            [(300, 1)] = "7d9158731abe35504c5951a0", [(300, 2)] = "456c789634fb6d3954f2b00d",
            [(326, 1)] = "ccbe0013a55bb94772ec6e5f", [(326, 2)] = "7382db302a12058a05342dee",
            [(350, 1)] = "352fce95505a58fa9c97e79d", [(350, 2)] = "3c3f5d3ff6e2c5f82aaa40f6",
            [(375, 1)] = "90ab5a37f25723f9bfb89132", [(375, 2)] = "e0128eb233fe7803b1de322c",
            [(410, 1)] = "c1afa51065c099d00df60fb1", [(410, 2)] = "24f5e1939549bf3708a06b59",
            [(500, 1)] = "b58c25e646ee5265abcad726", [(500, 2)] = "204ed27ccadd70e0e0dd701c",
        };

        /// <summary>
        /// A synthetic stat profile over the shipped roster's levels, so the band standard and the curve anchor
        /// can be measured without a world database. Every axis is a plain function of the member's level, which
        /// is all the curve wiring needs: the measured standard at the pivot is then a known function of the
        /// pivot band's level median.
        /// </summary>
        private static DungeonStatProfile LevelProfile(uint wcid)
        {
            var level = ShippedRosterFixture.LevelOf(wcid);

            if (level <= 0)
                return DungeonStatProfile.Empty;

            var l = (uint)level;
            var attrs = new Dictionary<PropertyAttribute, uint>
            {
                [PropertyAttribute.Strength] = l, [PropertyAttribute.Endurance] = l, [PropertyAttribute.Quickness] = l,
                [PropertyAttribute.Coordination] = l, [PropertyAttribute.Focus] = l, [PropertyAttribute.Self] = l,
            };

            return new DungeonStatProfile(level, 0,
                new Dictionary<Skill, uint> { [Skill.MeleeDefense] = 2 * l, [Skill.MissileDefense] = 2 * l, [Skill.HeavyWeapons] = 2 * l },
                l, 2 * l, 0, 0.0, attrs);
        }

        [TestInitialize]
        public void Reset()
        {
            // The standard's effective-skill axis resolves SkillFormula off GameTables; LevelProfile carries
            // attributes, so the synthetic tables must be present (TestGameTables covers Melee and Missile D).
            TestGameTables.EnsureInitialized();
            ThreadPlanCache.Invalidate();
        }

        // ---- the ceiling split and the codec -------------------------------------------------------------

        [TestMethod]
        public void The_gem_ceiling_stays_375_and_the_run_ceiling_is_500()
        {
            // Reflective, not direct: a direct AreEqual on a const constant-folds (MSTEST0032) and can never fail.
            Assert.AreEqual(375, typeof(DungeonGemSpec).GetField(nameof(DungeonGemSpec.MaxGemLevel)).GetRawConstantValue());
            Assert.AreEqual(500, typeof(DungeonGemSpec).GetField(nameof(DungeonGemSpec.MaxRunLevel)).GetRawConstantValue());
        }

        [TestMethod]
        public void The_press_carries_a_375_fragment_plus_a_Mana_Scarab_to_a_410_run()
        {
            // The production press limits (FragmentPressStation.BuildLimits minus its two tunable reads), so
            // this is the ceiling the press really clamps at - not a test-local PressLimits.
            var limits = FragmentPressStation.ComposeLimits(4, 1.0);
            Assert.AreEqual(DungeonGemSpec.MaxRunLevel, limits.MaxLevel);

            var store = ThreadDungeonStoreTests.LoadShipped();
            Assert.IsTrue(store.Attunement.TryGet(37155, out var mana), "the shipped roster carries the Mana Scarab (37155)");

            var fragment = new DungeonGemSpec("any", DungeonGemSpec.MaxGemLevel, 3, "any", 1, null, 0, 0);
            var loaded = RawFragmentRules.Load(fragment, mana);
            var pressed = RawFragmentRules.Resolve(new PressState(loaded, 0), store.Attunement, store.Modifiers, limits, new Random(1), out _);

            Assert.AreEqual(410, pressed.Spec.Level, "375 + 35, no longer clamped back to the gem ceiling");

            // Discriminating control: the same press under the pre-split ceiling (375) discards the +35.
            var oldLimits = new PressLimits(limits.MaxMonsterMods, limits.MaxLocks, limits.MaxEntries, DungeonGemSpec.MaxGemLevel,
                limits.MaxBonusMods, limits.MaxSalvageMods, limits.AimChance);
            var old = RawFragmentRules.Resolve(new PressState(RawFragmentRules.Load(fragment, mana), 0), store.Attunement, store.Modifiers, oldLimits, new Random(1), out _);
            Assert.AreEqual(375, old.Spec.Level);
        }

        [TestMethod]
        public void The_codec_accepts_level_500_and_rejects_501()
        {
            Assert.IsTrue(DungeonGemSpec.TryParse("v2|dg=any|lvl=500|tier=3|fam=any|seed=1|mods=", out var spec, out var error), error);
            Assert.AreEqual(500, spec.Level);

            Assert.IsFalse(DungeonGemSpec.TryParse("v2|dg=any|lvl=501|tier=3|fam=any|seed=1|mods=", out _, out _), "501 is above the run ceiling");

            // Discriminating: 410 (the real player maximum today) round-trips, and so does the gem ceiling.
            Assert.IsTrue(DungeonGemSpec.TryParse("v2|dg=any|lvl=410|tier=3|fam=any|seed=1|mods=", out var at410, out error), error);
            Assert.AreEqual(410, at410.Level);
        }

        // ---- reach-up: the projection --------------------------------------------------------------------

        [TestMethod]
        public void The_shipped_pivot_is_326_and_k_is_1_at_and_below_it()
        {
            var top = DungeonPopulationBuilder.AuthoredTopLevel(ShippedRosterFixture.Tables, ShippedRosterFixture.LevelOf);
            Assert.AreEqual(375, top, "the authored roster tops out at the gem ceiling");

            var pivot = DungeonPopulationBuilder.ReachUpPivot(top, DungeonRosterSelector.BandHigh);
            Assert.AreEqual(326, pivot, "floor(375 / 1.15)");

            foreach (var level in new[] { 1, 185, 300, 325, 326 })
                Assert.AreEqual(1.0, DungeonPopulationBuilder.ReachUpScale(level, pivot), $"k at {level}");

            Assert.AreEqual(327.0 / 326.0, DungeonPopulationBuilder.ReachUpScale(327, pivot), 1e-12, "k = L / P one level above");
            Assert.AreEqual(500.0 / 326.0, DungeonPopulationBuilder.ReachUpScale(500, pivot), 1e-12);
        }

        [TestMethod]
        public void At_and_below_the_pivot_the_plan_is_the_masters_with_every_switch_on()
        {
            foreach (var level in new[] { 300, 326 })
                foreach (var seed in new[] { 1, 2 })
                {
                    var plan = Build(level, seed, MasterLimits());
                    Assert.AreEqual(1.0, plan.ReachUpScale, $"k at {level}");
                    Assert.AreEqual(MasterHashes[(level, seed)], ShippedRosterFixture.Hash(plan), $"level {level} seed {seed}: {ShippedRosterFixture.Fingerprint(plan)}");
                }
        }

        [TestMethod]
        public void Switching_reach_up_off_reproduces_the_masters_plan_at_375_and_410()
        {
            foreach (var seed in new[] { 1, 2 })
            {
                // 375: the health curve is exact at its high anchor whatever the top level, so only reach-up moves.
                Assert.AreEqual(MasterHashes[(375, seed)], ShippedRosterFixture.Hash(Build(375, seed, MasterLimits(reachUp: false))), $"375 seed {seed}");

                // 410: the master clamped the health curve at 375, so the top-level dial goes back too.
                Assert.AreEqual(MasterHashes[(410, seed)], ShippedRosterFixture.Hash(Build(410, seed, MasterLimits(reachUp: false, healthCurveTopLevel: 375))), $"410 seed {seed}");

                // Discriminating: with reach-up ON the same inputs do NOT reproduce the master at either level.
                Assert.AreNotEqual(MasterHashes[(375, seed)], ShippedRosterFixture.Hash(Build(375, seed, MasterLimits())), $"375 seed {seed} reach-up on");
                Assert.AreNotEqual(MasterHashes[(410, seed)], ShippedRosterFixture.Hash(Build(410, seed, MasterLimits())), $"410 seed {seed} reach-up on");
            }
        }

        [TestMethod]
        public void At_300_the_plan_is_identical_with_reach_up_on_and_off()
        {
            foreach (var seed in new[] { 1, 2, 3 })
                Assert.AreEqual(ShippedRosterFixture.Fingerprint(Build(300, seed, MasterLimits(reachUp: false))),
                    ShippedRosterFixture.Fingerprint(Build(300, seed, MasterLimits())), $"seed {seed}");
        }

        [TestMethod]
        public void Stamps_land_inside_the_run_band_and_above_the_authored_level()
        {
            foreach (var (level, upper) in new[] { (375, 432), (410, 472) })
            {
                Assert.AreEqual(upper, (int)Math.Ceiling(level * DungeonRosterSelector.BandHigh - 1e-9), "the upper bound is ceil(1.15L)");

                var stamped = 0;

                foreach (var seed in Enumerable.Range(1, 8))
                {
                    var plan = Build(level, seed, MasterLimits());

                    foreach (var e in plan.Entries.Where(e => e.StampLevel > 0))
                    {
                        stamped++;
                        Assert.IsTrue(e.StampLevel > ShippedRosterFixture.LevelOf(e.Wcid), $"stamp {e.StampLevel} above authored {ShippedRosterFixture.LevelOf(e.Wcid)}");
                        Assert.IsTrue(e.StampLevel > level && e.StampLevel <= upper, $"{level} gem: stamp {e.StampLevel} outside ({level}, {upper}]");
                        Assert.AreEqual(0, e.UpliftLevel, "an entry is stamped OR uplifted, never both");
                    }
                }

                Assert.IsTrue(stamped > 0, $"positive control: a {level} gem stamps something");
            }
        }

        [TestMethod]
        public void A_410_run_has_a_live_health_ratio_and_its_boss_outranks_every_stamp()
        {
            foreach (var seed in new[] { 1, 2, 3 })
            {
                var plan = Build(410, seed, MasterLimits());

                Assert.IsTrue(plan.HealthNormalizeRatio > 0,
                    $"seed {seed}: HealthNormalizeRatio {plan.HealthNormalizeRatio} - the master read 0 here (empty natural band), which disabled health normalization");

                var maxStamp = plan.Entries.Where(e => e.Role != DungeonRole.Boss).Select(e => Math.Max(e.StampLevel, ShippedRosterFixture.LevelOf(e.Wcid))).Max();
                Assert.IsTrue(plan.BossLevel > maxStamp, $"seed {seed}: boss {plan.BossLevel} vs top stamp {maxStamp}");
            }

            // Positive control for the ratio: with reach-up off the 410 run reproduces the master's 0.
            Assert.AreEqual(0.0, Build(410, 1, MasterLimits(reachUp: false, healthCurveTopLevel: 375)).HealthNormalizeRatio);
        }

        [TestMethod]
        public void Every_run_level_from_185_to_500_opens_with_trash_and_no_stamp_exceeds_the_band()
        {
            var failures = new List<string>();

            for (var level = 185; level <= DungeonGemSpec.MaxRunLevel; level += 5)
            {
                var plan = Build(level, level, MasterLimits());
                var trash = plan.Entries.Count(e => e.Role != DungeonRole.Boss);
                var upper = (int)Math.Ceiling(level * DungeonRosterSelector.BandHigh - 1e-9);

                if (trash == 0)
                    failures.Add($"{level}: no trash ({string.Join("; ", plan.Notes)})");

                foreach (var e in plan.Entries.Where(e => e.StampLevel > upper))
                    failures.Add($"{level}: wcid {e.Wcid} stamped {e.StampLevel} > {upper}");

                if (plan.Entries.Any(e => e.Role != DungeonRole.Boss && Math.Max(e.StampLevel, ShippedRosterFixture.LevelOf(e.Wcid)) >= plan.BossLevel))
                    failures.Add($"{level}: a pack member is at or above the boss level {plan.BossLevel}");
            }

            Assert.AreEqual(0, failures.Count, string.Join("\n", failures));
        }

        [TestMethod]
        public void The_plan_cache_serves_reach_up_runs_exactly_and_a_toggle_is_never_stale()
        {
            foreach (var level in new[] { 300, 375, 410, 500 })
            {
                var uncachedOn = ShippedRosterFixture.Fingerprint(Build(level, 4, MasterLimits(), LevelProfile));
                var uncachedOff = ShippedRosterFixture.Fingerprint(Build(level, 4, MasterLimits(reachUp: false), LevelProfile));

                ThreadPlanCache.Invalidate();

                // Warm with ON, then read OFF then ON again out of the same cache: a key without k would hand the
                // OFF run the ON run's eligibility band and median (or the reverse).
                Assert.AreEqual(uncachedOn, ShippedRosterFixture.Fingerprint(Build(level, 4, MasterLimits(), LevelProfile, Memo())), $"{level} on, cold");
                Assert.AreEqual(uncachedOff, ShippedRosterFixture.Fingerprint(Build(level, 4, MasterLimits(reachUp: false), LevelProfile, Memo())), $"{level} off after on");
                Assert.AreEqual(uncachedOn, ShippedRosterFixture.Fingerprint(Build(level, 4, MasterLimits(), LevelProfile, Memo())), $"{level} on after off");

                if (level > 326)
                    Assert.AreNotEqual(uncachedOn, uncachedOff, $"discriminating: at {level} the switch must change the plan");
            }
        }

        private static ThreadPlanMemo Memo()
            => ThreadPlanCache.ForRun(true, null, ShippedRosterFixture.LevelOf, ShippedRosterFixture.HealthOf, LevelProfile);

        // ---- the stat curve ------------------------------------------------------------------------------

        private static SkillFormula Formula(PropertyAttribute a, PropertyAttribute b, uint divisor) => new SkillFormula(a, b, divisor);

        /// <summary>The dat formulas band.py used (attack: Heavy Weapons; magic: War/Life).</summary>
        private static SkillFormula AnalysisFormulaOf(Skill skill)
        {
            switch (skill)
            {
                case Skill.HeavyWeapons: return Formula(PropertyAttribute.Strength, PropertyAttribute.Coordination, 3);
                case Skill.MeleeDefense: return Formula(PropertyAttribute.Quickness, PropertyAttribute.Coordination, 3);
                case Skill.MissileDefense: return Formula(PropertyAttribute.Quickness, PropertyAttribute.Coordination, 5);
                case Skill.MagicDefense: return Formula(PropertyAttribute.Self, PropertyAttribute.Focus, 7);
                case Skill.WarMagic:
                case Skill.LifeMagic: return Formula(PropertyAttribute.Focus, PropertyAttribute.Self, 4);
                default: return null;
            }
        }

        /// <summary>
        /// The measured level-326 standard the 2026-10-08 analysis recorded (final.py, a 20-member sample).
        /// InitLevel medians are effective minus the attribute term over the median attributes, so a creature set
        /// to this standard lands on the recorded effective values exactly.
        /// </summary>
        private static DungeonBandStandard AnalysisAnchor()
        {
            var attrs = new Dictionary<PropertyAttribute, uint>
            {
                [PropertyAttribute.Strength] = 410, [PropertyAttribute.Endurance] = 410, [PropertyAttribute.Quickness] = 360,
                [PropertyAttribute.Coordination] = 410, [PropertyAttribute.Focus] = 580, [PropertyAttribute.Self] = 580,
            };
            var effective = new Dictionary<Skill, uint>
            {
                [Skill.HeavyWeapons] = 810, [Skill.MeleeDefense] = 820, [Skill.MissileDefense] = 745,
                [Skill.MagicDefense] = 630, [Skill.WarMagic] = 680, [Skill.LifeMagic] = 680,
            };
            var inits = effective.ToDictionary(kvp => kvp.Key,
                kvp => kvp.Value - AttributeFormula.Compute(AnalysisFormulaOf(kvp.Key), a => attrs[a]));

            return DungeonBandStandard.ForTest(inits, 844, 540, sampleCount: 20, attributeMedians: attrs, effectiveSkillMedians: effective);
        }

        private static DungeonBandStandard CurveAt(int level, double rate = DungeonStatCurve.DefaultDefenseRateAbove375)
            => DungeonStatCurve.Extrapolate(AnalysisAnchor(), 326, level, rate, AnalysisFormulaOf);

        private static uint Landed(DungeonBandStandard s, Skill skill)
            => AttributeFormula.Compute(AnalysisFormulaOf(skill), a => s.AttributeMedianFor(a)) + s.MedianFor(skill);

        [TestMethod]
        public void The_curve_reproduces_the_analysis_values_at_375()
        {
            var s = CurveAt(375);

            Assert.AreEqual(1386u, s.MaxBodyDamage, "dmg");
            Assert.AreEqual(592u, s.MaxBaseArmor, "armour");
            Assert.AreEqual(831u, s.EffectiveSkillMedianFor(Skill.HeavyWeapons), "attack");
            Assert.AreEqual(859u, s.EffectiveSkillMedianFor(Skill.MeleeDefense), "melee D");
            Assert.AreEqual(782u, s.EffectiveSkillMedianFor(Skill.MissileDefense), "missile D");
            Assert.AreEqual(680u, s.EffectiveSkillMedianFor(Skill.MagicDefense), "magic D");
            Assert.AreEqual(726u, s.EffectiveSkillMedianFor(Skill.WarMagic), "war");
            Assert.AreEqual(456u, s.AttributeMedianFor(PropertyAttribute.Strength), "Str");
            Assert.AreEqual(459u, s.AttributeMedianFor(PropertyAttribute.Endurance), "End");
            Assert.AreEqual(386u, s.AttributeMedianFor(PropertyAttribute.Quickness), "Quick");
            Assert.AreEqual(443u, s.AttributeMedianFor(PropertyAttribute.Coordination), "Coord");
            Assert.AreEqual(638u, s.AttributeMedianFor(PropertyAttribute.Focus), "Focus");
            Assert.AreEqual(641u, s.AttributeMedianFor(PropertyAttribute.Self), "Self");

            // At or below the pivot the anchor is returned untouched.
            Assert.AreEqual(844u, CurveAt(326).MaxBodyDamage);
            Assert.AreEqual(844u, CurveAt(300).MaxBodyDamage);
        }

        [TestMethod]
        public void A_creature_set_to_the_curve_standard_lands_on_its_effective_target()
        {
            // The InitLevel medians are re-derived against the RAISED attributes, so attribute term + InitLevel
            // is exactly the extrapolated effective value - the attribute slope cannot sneak the full defense
            // rate back in. Independently computed (python, same rounding) at 410 and 500, rate 0.5.
            var expected = new Dictionary<int, (uint Atk, uint MelD, uint MisD, uint MagD, uint War)>
            {
                [410] = (847, 873, 796, 698, 760),
                [500] = (888, 911, 832, 749, 857),
            };

            foreach (var kvp in expected)
            {
                var s = CurveAt(kvp.Key);
                Assert.AreEqual(kvp.Value.Atk, Landed(s, Skill.HeavyWeapons), $"attack at {kvp.Key}");
                Assert.AreEqual(kvp.Value.MelD, Landed(s, Skill.MeleeDefense), $"melee D at {kvp.Key}");
                Assert.AreEqual(kvp.Value.MisD, Landed(s, Skill.MissileDefense), $"missile D at {kvp.Key}");
                Assert.AreEqual(kvp.Value.MagD, Landed(s, Skill.MagicDefense), $"magic D at {kvp.Key}");
                Assert.AreEqual(kvp.Value.War, Landed(s, Skill.WarMagic), $"war at {kvp.Key}");
                Assert.AreEqual(s.EffectiveSkillMedianFor(Skill.MeleeDefense), Landed(s, Skill.MeleeDefense), "landed == effective median");
            }
        }

        [TestMethod]
        public void Effective_defense_above_375_grows_at_half_the_slope_and_is_continuous_at_375()
        {
            foreach (var axis in new[] { DungeonStatAxis.MeleeDefense, DungeonStatAxis.MissileDefense, DungeonStatAxis.MagicDefense })
            {
                var b = DungeonStatCurve.Slope(axis);

                Assert.AreEqual(Math.Exp(b), DungeonStatCurve.Ratio(axis, 374, 375, 0.5), 1e-12, $"{axis}: full slope just below 375");
                Assert.AreEqual(Math.Exp(0.5 * b), DungeonStatCurve.Ratio(axis, 375, 376, 0.5), 1e-12, $"{axis}: half slope just above");
                Assert.AreEqual(Math.Exp(b * 49 + 0.5 * b * 35), DungeonStatCurve.Ratio(axis, 326, 410, 0.5), 1e-12, $"{axis}: split at 375");
                Assert.AreEqual(DungeonStatCurve.Ratio(axis, 326, 375, 0.5) * DungeonStatCurve.Ratio(axis, 375, 500, 0.5),
                    DungeonStatCurve.Ratio(axis, 326, 500, 0.5), 1e-12, $"{axis}: continuous (composes) through 375");

                // Discriminating: the unsoftened rate differs above 375 and is identical below it.
                Assert.AreNotEqual(DungeonStatCurve.Ratio(axis, 326, 410, 1.0), DungeonStatCurve.Ratio(axis, 326, 410, 0.5));
                Assert.AreEqual(DungeonStatCurve.Ratio(axis, 326, 375, 1.0), DungeonStatCurve.Ratio(axis, 326, 375, 0.5), 1e-15);
            }

            // Non-defense axes ignore the rate entirely.
            Assert.AreEqual(DungeonStatCurve.Ratio(DungeonStatAxis.Damage, 326, 500, 1.0), DungeonStatCurve.Ratio(DungeonStatAxis.Damage, 326, 500, 0.0), 1e-15);

            // And the standards: melee D at 410 is 873 softened against 887 unsoftened (python cross-check).
            Assert.AreEqual(873u, CurveAt(410, 0.5).EffectiveSkillMedianFor(Skill.MeleeDefense));
            Assert.AreEqual(887u, CurveAt(410, 1.0).EffectiveSkillMedianFor(Skill.MeleeDefense));
            Assert.AreEqual(CurveAt(375, 0.5).EffectiveSkillMedianFor(Skill.MeleeDefense), CurveAt(375, 1.0).EffectiveSkillMedianFor(Skill.MeleeDefense));
        }

        [TestMethod]
        public void The_defense_rate_is_sanitized_into_zero_to_one()
        {
            Assert.AreEqual(0.5, DungeonStatCurve.SanitizeDefenseRate(double.NaN));
            Assert.AreEqual(0.5, DungeonStatCurve.SanitizeDefenseRate(double.PositiveInfinity));
            Assert.AreEqual(0.0, DungeonStatCurve.SanitizeDefenseRate(-1));
            Assert.AreEqual(1.0, DungeonStatCurve.SanitizeDefenseRate(7));
            Assert.AreEqual(0.25, DungeonStatCurve.SanitizeDefenseRate(0.25));
            Assert.AreEqual(1.0, DungeonStatCurve.Ratio(DungeonStatAxis.MeleeDefense, 375, 500, 0.0), "0 freezes defense at its 375 value");
        }

        [TestMethod]
        public void The_plan_standard_follows_the_curve_above_the_pivot_and_switching_it_off_gives_the_measured_one()
        {
            var on = Build(410, 1, MasterLimits(), LevelProfile);
            var off = Build(410, 1, MasterLimits(statCurve: false), LevelProfile);

            Assert.IsNotNull(on.StatCurve, "a 410 run above the pivot carries the curve");
            Assert.AreEqual(326, on.StatCurve.Pivot);
            Assert.AreSame(on.StatCurve.StandardAt(410), on.BandStandard, "the plan standard IS the curve at the run level");

            // The anchor is the MEASURED standard at the pivot over its natural, unprojected band.
            var measuredAtPivot = DungeonBandStandard.Compute(ShippedRosterFixture.Tables, 326, LevelProfile,
                new DungeonRosterBand(DungeonRosterSelector.BandLow, DungeonRosterSelector.BandHigh), DungeonPopulationLimits.DefaultBandLowFloorRatio);
            Assert.AreEqual(measuredAtPivot.MaxBodyDamage, on.StatCurve.Anchor.MaxBodyDamage);
            Assert.AreEqual(measuredAtPivot.EffectiveSkillMedianFor(Skill.MeleeDefense), on.StatCurve.Anchor.EffectiveSkillMedianFor(Skill.MeleeDefense));

            Assert.IsNull(off.StatCurve, "switch off: no curve");
            Assert.IsFalse(off.BandStandard.IsEmpty);
            Assert.AreNotEqual(on.BandStandard.MaxBodyDamage, off.BandStandard.MaxBodyDamage, "discriminating: the curve moves the damage standard");

            // Below the pivot the curve never applies, switch on or not.
            Assert.IsNull(Build(300, 1, MasterLimits(), LevelProfile).StatCurve);
        }

        [TestMethod]
        public void The_defense_cap_for_a_stamped_creature_reads_the_curve_at_its_stamp()
        {
            var plan = Build(410, 1, MasterLimits(), LevelProfile);
            var stamp = plan.Entries.Where(e => e.StampLevel > 410).Select(e => e.StampLevel).DefaultIfEmpty(0).Max();
            Assert.IsTrue(stamp > 410, "positive control: something is stamped above the run level");

            var atRun = DungeonCombatNormalizer.DefenseSkillCap(plan.BandStandard, Skill.MeleeDefense, plan.DefenseSkillCapOffset);
            var atStamp = DungeonCombatNormalizer.DefenseSkillCap(plan.StatCurve.StandardAt(stamp), Skill.MeleeDefense, plan.DefenseSkillCapOffset);

            Assert.IsTrue(atRun > 0);
            Assert.IsTrue(atStamp > atRun, $"cap at the stamp {stamp} ({atStamp}) must exceed the run-level cap ({atRun}), or the stamped defense is clipped back down");
        }

        [TestMethod]
        public void Stamp_scaling_preserves_authored_order()
        {
            // Three creatures at the same authored level, stamped to the same level: one ratio for all of them.
            var ratio = DungeonStatCurve.Ratio(DungeonStatAxis.Damage, 375, 431, 0.5);
            var scaled = new[] { 300, 450, 600 }.Select(v => ReachUpStamp.ScaleInt(v, ratio)).ToArray();
            CollectionAssert.AreEqual(scaled.OrderBy(v => v).ToArray(), scaled, "same raw, same stamp: order kept");

            // Across raw levels the preserved quantity is each creature's position RELATIVE TO THE CURVE at its own
            // level (value / Standard(level)), because the ratio is Standard(stamp) / Standard(raw). Two creatures
            // each sitting exactly on the curve at their authored levels both land on the curve at their stamps,
            // and the higher stamp is the tougher one. (Equal RAW values at different levels are NOT ordered after
            // stamping on a defense axis: the lower one was relatively tougher for its level and stays so, and the
            // softening above 375 shortens the higher one's climb - measured 1060 vs 1048 for melee D at 340 vs
            // 370 in a 410 run. That is the authored spread being kept, not lost.)
            var k = DungeonPopulationBuilder.ReachUpScale(410, 326);
            var lowRaw = 340;
            var highRaw = 370;
            var lowStamp = DungeonPopulationBuilder.ProjectLevel(lowRaw, k);
            var highStamp = DungeonPopulationBuilder.ProjectLevel(highRaw, k);
            Assert.IsTrue(highStamp > lowStamp);

            foreach (var axis in Enum.GetValues(typeof(DungeonStatAxis)).Cast<DungeonStatAxis>())
            {
                int OnCurve(int level) => (int)Math.Round(10000 * DungeonStatCurve.Ratio(axis, 326, level, 0.5));

                var lowScaled = ReachUpStamp.ScaleInt(OnCurve(lowRaw), DungeonStatCurve.Ratio(axis, lowRaw, lowStamp, 0.5));
                var highScaled = ReachUpStamp.ScaleInt(OnCurve(highRaw), DungeonStatCurve.Ratio(axis, highRaw, highStamp, 0.5));

                Assert.AreEqual(OnCurve(lowStamp), lowScaled, 1, $"{axis}: on the curve at {lowRaw} -> on the curve at {lowStamp}");
                Assert.AreEqual(OnCurve(highStamp), highScaled, 1, $"{axis}: on the curve at {highRaw} -> on the curve at {highStamp}");
                Assert.IsTrue(highScaled > lowScaled, $"{axis}: {highScaled} vs {lowScaled}");
            }

            // Skills: the re-derived InitLevel lands effective on value x ratio for each creature.
            var meleeRatio = DungeonStatCurve.Ratio(DungeonStatAxis.MeleeDefense, 375, 431, 0.5);
            Assert.AreEqual(DungeonStatCurve.Scale(800, meleeRatio) - 300u, ReachUpStamp.SkillInitTarget(800, 300, meleeRatio));
            Assert.AreEqual(0u, ReachUpStamp.SkillInitTarget(100, 5000, meleeRatio), "floored at 0, never wraps");

            // A zero stays zero, and non-positive authored values are left alone.
            Assert.AreEqual(0, ReachUpStamp.ScaleInt(0, 3.0));
            Assert.AreEqual(-5, ReachUpStamp.ScaleInt(-5, 3.0));
        }

        [TestMethod]
        public void Body_part_scaling_clones_the_shared_collection()
        {
            var shared = new Dictionary<CombatBodyPart, PropertiesBodyPart>
            {
                [CombatBodyPart.Head] = new PropertiesBodyPart { DVal = 100, BaseArmor = 200 },
                [CombatBodyPart.Chest] = new PropertiesBodyPart { DVal = 0, BaseArmor = 300 },
            };
            var biota = new Biota { PropertiesBodyPart = shared };

            var changed = ReachUpStamp.ScaleBodyParts(biota, 2.0, 1.5);

            Assert.AreEqual(2, changed);
            Assert.AreNotSame(shared, biota.PropertiesBodyPart, "the cached weenie's collection is never written in place");
            Assert.AreEqual(100, shared[CombatBodyPart.Head].DVal, "the shared part is untouched");
            Assert.AreEqual(200, biota.PropertiesBodyPart[CombatBodyPart.Head].DVal);
            Assert.AreEqual(300, biota.PropertiesBodyPart[CombatBodyPart.Head].BaseArmor);
            Assert.AreEqual(0, biota.PropertiesBodyPart[CombatBodyPart.Chest].DVal, "no bite authored, none granted");
            Assert.AreEqual(450, biota.PropertiesBodyPart[CombatBodyPart.Chest].BaseArmor);
        }

        // ---- the spawner's pure seams -----------------------------------------------------------------------

        [TestMethod]
        public void The_placement_retry_keeps_the_stamp()
        {
            var original = new DungeonSpawnPlanEntry(42, DungeonRole.Trash, ShippedRosterFixture.Dungeon(2).Points[0], 0, 431);
            var moved = ShippedRosterFixture.Dungeon(2).Points[1];
            var retry = ThreadDungeonSpawner.RetryEntry(original, moved);

            Assert.AreEqual(431, retry.StampLevel);
            Assert.AreEqual(0, retry.UpliftLevel);
            Assert.AreSame(moved, retry.Point);
            Assert.AreEqual(42u, retry.Wcid);
            Assert.AreEqual(DungeonRole.Trash, retry.Role);

            var uplifted = ThreadDungeonSpawner.RetryEntry(new DungeonSpawnPlanEntry(43, DungeonRole.Elite, null, 375), moved);
            Assert.AreEqual(375, uplifted.UpliftLevel);
            Assert.AreEqual(0, uplifted.StampLevel);
        }

        [TestMethod]
        public void The_reward_level_is_the_highest_of_own_uplift_and_stamp()
        {
            Assert.AreEqual(431, ThreadDungeonSpawner.RewardLevel(new DungeonSpawnPlanEntry(1, DungeonRole.Trash, null, 0, 431), 375, 500));
            Assert.AreEqual(410, ThreadDungeonSpawner.RewardLevel(new DungeonSpawnPlanEntry(1, DungeonRole.Trash, null, 410, 0), 300, 500));
            Assert.AreEqual(380, ThreadDungeonSpawner.RewardLevel(new DungeonSpawnPlanEntry(1, DungeonRole.Trash, null, 0, 0), 380, 500));
            Assert.AreEqual(500, ThreadDungeonSpawner.RewardLevel(new DungeonSpawnPlanEntry(1, DungeonRole.Boss, null, 0, 431), 375, 500), "a boss reads its boss level");

            // The XP anchor reads the same raised level: a stamped trash pays at least its stamp's ladder XP.
            var ladder = ThreadDungeonStoreTests.LoadShipped().XpLadder;
            var stamped = new DungeonSpawnPlanEntry(1, DungeonRole.Trash, null, 0, 431);
            Assert.AreEqual(DungeonRewardMath.LadderXp(ladder, 431),
                DungeonPopulationBuilder.BaseXp(ladder, 1000, 375, DungeonRole.Trash, 500, stamped.RaisedLevel));
        }

        /// <summary>
        /// The XpOverride clamp (PropertyInt, so int by type) is left in place; this measures where it starts to
        /// bite at the shipped dials (solo, shipped roster), so the comment at the clamp quotes computed numbers.
        ///
        /// WORST CASE STACKS BOTH CAPS (code review 2026-10-08): XpForKill multiplies the plan's xpMultiplier AND
        /// its bossXpMultiplier, and DungeonPopulationBuilder caps EACH at limits.XpCap independently, so a boss
        /// can be priced at XpCap squared (9x at the shipped 3.0) - not XpCap once, as this test first assumed.
        /// The modifier reward scale (default 1.0) leaves both caps whole.
        ///
        /// The pin that matters: saturation starts strictly ABOVE 410, the highest run level a player can press
        /// today (a 375 fragment plus a Mana Scarab), so only admin-made gems ever reach the clamp.
        /// </summary>
        [TestMethod]
        public void Boss_xp_saturates_the_int_clamp_only_above_the_player_maximum()
        {
            var store = ThreadDungeonStoreTests.LoadShipped();
            var limits = new DungeonPopulationLimits(120, 3.0, 3.0, 8, 0.5);
            Assert.AreEqual(1.0, limits.ModifierRewardScale, "the worst case below assumes the caps are not scaled down");

            (long Plain, long Worst, int BossLevel) PriceAt(int level)
            {
                var plan = DungeonPopulationBuilder.Build(Spec(level), ShippedRosterFixture.Dungeon(30), store, ShippedRosterFixture.Tables,
                    ShippedRosterFixture.LevelOf, ShippedRosterFixture.HealthOf, limits, new Random(1));
                var baseXp = DungeonPopulationBuilder.BaseXp(store.XpLadder, null, 375, DungeonRole.Boss, plan.BossLevel, 0, bossLadderOnly: plan.BossNormalize);

                return (DungeonPopulationBuilder.XpFor(plan, baseXp, DungeonRole.Boss),
                        DungeonRewardMath.XpForKill(baseXp, DungeonRole.Boss, plan.XpScale, limits.XpCap, limits.XpCap),
                        plan.BossLevel);
            }

            var firstSaturating = 0;

            for (var level = DungeonGemSpec.MaxGemLevel; level <= DungeonGemSpec.MaxRunLevel; level++)
            {
                if (PriceAt(level).Worst >= int.MaxValue)
                {
                    firstSaturating = level;
                    break;
                }
            }

            var at410 = PriceAt(410);
            var at500 = PriceAt(DungeonGemSpec.MaxRunLevel);

            Console.WriteLine($"410: boss {at410.BossLevel} plain {at410.Plain} worst {at410.Worst}; " +
                              $"500: boss {at500.BossLevel} plain {at500.Plain} worst {at500.Worst}; first saturating run level {firstSaturating}");

            Assert.IsTrue(at500.Plain < int.MaxValue, $"a plain level-500 boss stays under the clamp ({at500.Plain})");
            Assert.IsTrue(at410.Worst < int.MaxValue, $"the player maximum never saturates, even with both caps ({at410.Worst})");
            Assert.IsTrue(at500.Worst >= int.MaxValue, $"positive control: both caps at 500 DO reach the clamp ({at500.Worst})");
            Assert.IsTrue(firstSaturating > 410, $"saturation starts at run level {firstSaturating}, which must be above 410");
        }

        // ---- the stamp glue's pure seams (code review 2026-10-08) -------------------------------------

        private static DungeonSpawnPlan CurvePlan()
        {
            var plan = Build(410, 1, MasterLimits(), LevelProfile);
            Assert.IsNotNull(plan.StatCurve, "fixture: a 410 run carries the curve");
            return plan;
        }

        [TestMethod]
        public void A_stamped_promoted_boss_reads_the_run_standard_for_its_defense_cap()
        {
            var plan = CurvePlan();

            var stampedTrash = new DungeonSpawnPlanEntry(1, DungeonRole.Trash, null, 0, 450);
            var stampedElite = new DungeonSpawnPlanEntry(1, DungeonRole.Elite, null, 0, 450);
            var stampedBoss = new DungeonSpawnPlanEntry(1, DungeonRole.Boss, null, 0, 450);
            var unstamped = new DungeonSpawnPlanEntry(1, DungeonRole.Trash, null);

            Assert.AreSame(plan.StatCurve.StandardAt(450), ThreadDungeonSpawner.DefenseCapStandard(stampedTrash, plan));
            Assert.AreSame(plan.StatCurve.StandardAt(450), ThreadDungeonSpawner.DefenseCapStandard(stampedElite, plan));
            Assert.IsNull(ThreadDungeonSpawner.DefenseCapStandard(stampedBoss, plan),
                "a promoted boss with a stamp falls back to plan.BandStandard, like every other boss");
            Assert.IsNull(ThreadDungeonSpawner.DefenseCapStandard(unstamped, plan));

            // Same exclusion on the stamp-scaling side, so the two choices cannot drift apart.
            Assert.AreSame(plan.StatCurve, ThreadDungeonSpawner.StampCurveFor(stampedTrash, plan));
            Assert.IsNull(ThreadDungeonSpawner.StampCurveFor(stampedBoss, plan));
            Assert.IsNull(ThreadDungeonSpawner.StampCurveFor(unstamped, plan));

            // And with no curve on the plan, nothing reads one.
            var flat = Build(410, 1, MasterLimits(statCurve: false), LevelProfile);
            Assert.IsNull(ThreadDungeonSpawner.DefenseCapStandard(stampedTrash, flat));
            Assert.IsNull(ThreadDungeonSpawner.StampCurveFor(stampedTrash, flat));
        }

        [TestMethod]
        public void The_stamp_hands_its_authored_health_to_the_health_block_only_when_attributes_moved()
        {
            var scaled = new ReachUpStamp.Result(attributesScaled: 6, skillsRetargeted: 3, partsScaled: 2, weaponsScaled: 0, attributesSkipped: false);
            var skipped = new ReachUpStamp.Result(0, 3, 2, 0, attributesSkipped: true);
            var nothing = default(ReachUpStamp.Result);

            Assert.AreEqual(1000u, ThreadDungeonSpawner.HealthMaxBeforeStamp(scaled, 1000));
            Assert.IsNull(ThreadDungeonSpawner.HealthMaxBeforeStamp(skipped, 1000), "attributes refused: the live max never moved");
            Assert.IsNull(ThreadDungeonSpawner.HealthMaxBeforeStamp(nothing, 1000));

            // The health block prices off the capture, never the Endurance-raised live max.
            Assert.AreEqual(1000u, ThreadDungeonSpawner.AuthoredHealthMax(null, ThreadDungeonSpawner.HealthMaxBeforeStamp(scaled, 1000), 1150));
            Assert.AreEqual(1150u, ThreadDungeonSpawner.AuthoredHealthMax(null, ThreadDungeonSpawner.HealthMaxBeforeStamp(skipped, 1150), 1150));
            Assert.AreEqual(900u, ThreadDungeonSpawner.AuthoredHealthMax(900, null, 1150), "the uplift's capture still wins");
            Assert.AreEqual(1150u, ThreadDungeonSpawner.AuthoredHealthMax(null, null, 1150));
        }
    }
}
