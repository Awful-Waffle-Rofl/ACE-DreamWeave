using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldEvents.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// Family stretch wired into DungeonPopulationBuilder.Build (task 10): stretched families join the draw, a drawn one
    /// fields its rank-mapped pool tagged Stretched at the mapped level, plan.Stretch carries the per-level standards, and
    /// every native sample and native plan is exactly what it was. Reads no PropertyManager key.
    /// </summary>
    [TestClass]
    public class FamilyStretchBuilderTests
    {
        private static readonly DungeonPopulationLimits On = new DungeonPopulationLimits(60, 3.0, 3.0, 8, 0.5, familyStretch: true);
        private static readonly DungeonPopulationLimits Off = new DungeonPopulationLimits(60, 3.0, 3.0, 8, 0.5, familyStretch: false);

        private static readonly uint[] DrudgeSix = { 73227u, 22899u, 25563u, 30680u, 23480u, 24278u };

        private static readonly string[] NativeHigh = { "anekshay", "ghost", "golem", "gromnie", "mukkir", "olthoi", "shadow", "undead", "virindi", "zefir" };

        [TestInitialize]
        public void Init() => TestGameTables.EnsureInitialized();

        private static DungeonSpawnPlan Build(int level, int seed, DungeonPopulationLimits limits, string family = "any",
            Func<uint, bool> fits = null, ThreadPlanMemo memo = null)
            => FamilyStretchFixture.Build(level, seed, limits, family, fits, memo);

        private static string Fp(DungeonSpawnPlan plan) => ShippedRosterFixture.Fingerprint(plan);

        private static IEnumerable<DungeonSpawnPlanEntry> NonBoss(DungeonSpawnPlan plan) => plan.Entries.Where(e => e.Role != DungeonRole.Boss);

        // ---- switch off and natives -----------------------------------------------------------------------------------

        [TestMethod]
        public void Switch_off_plans_still_match_origin_master_and_on_moves_them()
        {
            foreach (var kvp in FamilyStretchGoldenTests.MasterHashes)
            {
                var (l, s) = kvp.Key;
                var off = Build(l, s, Off);
                Assert.AreEqual(kvp.Value, ShippedRosterFixture.Hash(off), $"explicit off, level {l} seed {s}");
                Assert.IsNull(off.Stretch, $"off never stretches, level {l} seed {s}");
            }

            var stretchedPlans = 0;

            for (var seed = 1; seed <= 10; seed++)
            {
                var on = Build(400, seed, On);

                if (on.Stretch == null)
                    continue;

                stretchedPlans++;
                Assert.AreNotEqual(ShippedRosterFixture.Hash(Build(400, seed, Off)), ShippedRosterFixture.Hash(on), $"seed {seed}: a stretched plan must hash differently from off");
            }

            Assert.IsTrue(stretchedPlans > 0, "at least one of seeds 1..10 at 400 draws a stretched family");
        }

        [TestMethod]
        [TestCategory("Slow")]
        public void Native_high_families_plan_identically_with_the_switch_on()
        {
            foreach (var fam in NativeHigh)
            foreach (var l in new[] { 185, 300, 400, 480 })
            foreach (var s in new[] { 1, 2 })
            {
                var on = Build(l, s, On, fam);
                Assert.AreEqual(Fp(Build(l, s, Off, fam)), Fp(on), $"{fam} at {l} seed {s}");
                Assert.IsNull(on.Stretch, $"{fam} at {l} seed {s}");
            }
        }

        [TestMethod]
        public void A_family_natively_in_band_is_unchanged()
        {
            foreach (var (fam, l) in new[] { ("drudge", 180), ("snowman", 170) })
            {
                var on = Build(l, 1, On, fam);
                Assert.AreEqual(fam, on.FamilyId);
                Assert.AreEqual(Fp(Build(l, 1, Off, fam)), Fp(on), $"{fam} at {l}");
                Assert.IsNull(on.Stretch, $"{fam} at {l}");
            }
        }

        // ---- a stretched draw -----------------------------------------------------------------------------------------

        [TestMethod]
        public void Stretched_drudge_at_400_draws_the_mapped_pool()
        {
            var plan = Build(400, 1, On, "drudge");

            Assert.AreEqual("drudge", plan.FamilyId);
            Assert.IsNotNull(plan.Stretch);
            Assert.AreEqual(400, plan.Stretch.LowEdge);
            Assert.AreEqual(460, plan.Stretch.HighEdge);
            Assert.AreEqual(400, plan.EffectiveBandLow);

            var nonBoss = NonBoss(plan).ToList();
            Assert.IsTrue(nonBoss.Count > 0);

            foreach (var e in nonBoss)
            {
                Assert.IsTrue(e.Stretched, $"wcid {e.Wcid}");
                Assert.AreEqual(0, e.StampLevel, $"wcid {e.Wcid}");
                CollectionAssert.Contains(new[] { 400, 423, 460 }, e.UpliftLevel, $"wcid {e.Wcid} level {e.UpliftLevel}");
                CollectionAssert.Contains(DrudgeSix, e.Wcid, $"wcid {e.Wcid}");
            }

            var boss = plan.Entries.Single(e => e.Role == DungeonRole.Boss);
            Assert.IsFalse(boss.Stretched);
            Assert.IsTrue(plan.BossLevel > nonBoss.Max(e => e.UpliftLevel), $"boss {plan.BossLevel}");

            Assert.IsFalse(plan.Notes.Any(n => n.StartsWith("band:", StringComparison.Ordinal)), string.Join(" / ", plan.Notes));
            Assert.IsFalse(plan.Notes.Any(n => n.StartsWith("uplift:", StringComparison.Ordinal) && boss.UpliftLevel == 0),
                "no uplift note when only stretched entries carry an uplift level: " + string.Join(" / ", plan.Notes));
        }

        [TestMethod]
        public void Snowman_pool_has_five_members_and_the_boss_is_unique_when_possible()
        {
            var plan = Build(400, 1, On, "snowman");

            Assert.AreEqual("snowman", plan.FamilyId);
            Assert.AreEqual(5, plan.Stretch.PoolCount);
            Assert.AreEqual(1.25, plan.Stretch.Overscale);

            var distinct = NonBoss(plan).Select(e => e.Wcid).Distinct().Count();

            if (distinct > 1)
                Assert.IsFalse(NonBoss(plan).Any(e => e.Wcid == plan.BossWcid), $"boss {plan.BossWcid} also in the pack");
        }

        [TestMethod]
        public void Stretched_standards_are_carried_to_the_mapped_level()
        {
            var high = Build(400, 1, On, "drudge");
            Assert.IsNotNull(high.StatCurve, "400 is above the pivot: the run has a stat curve");
            Assert.IsNotNull(high.Stretch.StandardAt(460), "the top mapped level carries a standard");
            Assert.AreEqual(FamilyStretchFixture.StandardText(high.StatCurve.StandardAt(460)), FamilyStretchFixture.StandardText(high.Stretch.StandardAt(460)));
            Assert.AreEqual(DungeonHealthCurve.Target(460, On), high.Stretch.HealthStandardAt(460));

            // Every stretched entry's level has a standard; nothing else does.
            CollectionAssert.AreEquivalent(NonBoss(high).Select(e => e.UpliftLevel).Distinct().ToList(), high.Stretch.Standards.Keys.ToList());
            CollectionAssert.AreEquivalent(high.Stretch.Standards.Keys.ToList(), high.Stretch.HealthStandards.Keys.ToList());

            var low = Build(200, 1, On, "snowman");
            Assert.AreEqual("snowman", low.FamilyId);
            Assert.IsNotNull(low.Stretch, "snowman stretches at 200");
            Assert.IsNull(low.StatCurve, "200 is below the pivot");
            Assert.IsTrue(low.ReachUpPivot > 0);

            var carried = DungeonStatCurve.Carry(low.BandStandard, 200, 230, low.ReachUpPivot,
                DungeonStatCurve.SanitizeDefenseRate(On.DefenseCurveRateAbove375),
                DungeonStatCurve.SanitizeMagicDefenseRate(On.MagicDefenseCurveRateAbove300),
                DungeonStatCurve.SanitizeDamageRate(On.DamageCurveRateAbovePivot),
                DungeonStatCurve.SanitizeWeaponAttackRate(On.WeaponAttackCurveRateAbove400),
                DungeonStatCurve.SanitizeMeleeDefenseRate(On.MeleeDefenseCurveRateAbove400));

            Assert.IsNotNull(low.Stretch.StandardAt(230), "the top mapped level 230 carries a standard");
            Assert.AreEqual(carried.MaxBodyDamage, low.Stretch.StandardAt(230).MaxBodyDamage);
            Assert.IsTrue(low.Stretch.StandardAt(230).MaxBodyDamage > low.BandStandard.MaxBodyDamage,
                $"carried {low.Stretch.StandardAt(230).MaxBodyDamage} vs band {low.BandStandard.MaxBodyDamage}");
            Assert.AreEqual(FamilyStretchFixture.StandardText(carried), FamilyStretchFixture.StandardText(low.Stretch.StandardAt(230)));
        }

        [TestMethod]
        public void PoolMaxBase_prices_the_stretched_pool_at_its_mapped_health()
        {
            var plan = Build(400, 1, On, "drudge");
            Assert.IsTrue(plan.BossNormalize);

            var floor460 = DungeonPopulationBuilder.StretchHealthFloor(plan.TrashHealthFloor, DungeonHealthCurve.Target(460, On));
            Assert.IsTrue(floor460 > plan.TrashHealthFloor, "fixture: the mapped-level floor must rise above the run floor, or the pin is vacuous");
            Assert.AreEqual(floor460, plan.PoolMaxBase);

            // Off at the same level never draws drudge, so its PoolMaxBase is priced on the native pool only.
            Assert.AreNotEqual(Fp(Build(400, 1, Off)), Fp(plan));
        }

        [TestMethod]
        public void PoolMaxBase_carries_the_overscale()
        {
            var plan = Build(400, 1, On, "snowman");
            var floor460 = DungeonPopulationBuilder.StretchHealthFloor(plan.TrashHealthFloor, DungeonHealthCurve.Target(460, On));
            Assert.AreEqual((uint)Math.Round(floor460 * 1.25), plan.PoolMaxBase);
        }

        [TestMethod]
        public void StretchHealthFloor_is_the_higher_of_the_two_rounded_away_from_zero()
        {
            Assert.AreEqual(100u, DungeonPopulationBuilder.StretchHealthFloor(100, 50.0));
            Assert.AreEqual(151u, DungeonPopulationBuilder.StretchHealthFloor(100, 150.5));
            Assert.AreEqual(150u, DungeonPopulationBuilder.StretchHealthFloor(100, 150.4));
            Assert.AreEqual(100u, DungeonPopulationBuilder.StretchHealthFloor(100, -5.0));
            Assert.AreEqual(100u, DungeonPopulationBuilder.StretchHealthFloor(100, double.NaN));
            Assert.AreEqual(uint.MaxValue, DungeonPopulationBuilder.StretchHealthFloor(100, double.PositiveInfinity));
        }

        private static CreaturePerHit SyntheticPerHit(uint wcid)
        {
            var level = ShippedRosterFixture.LevelOf(wcid);

            if (level <= 0)
                return CreaturePerHit.Empty;

            var melee = new PerHitSource(10 + wcid % 97 + level, PropertyAttribute.Strength, 100 + wcid % 50, false, isBody: true);
            return new CreaturePerHit(level, melee, default);
        }

        private static DungeonSpawnPlan BuildWithPerHit(int level, int seed, DungeonPopulationLimits limits)
            => DungeonPopulationBuilder.Build(FamilyStretchFixture.Spec(level, seed), ShippedRosterFixture.Dungeon(30), ThreadDungeonStoreTests.LoadShipped(),
                ShippedRosterFixture.Tables, ShippedRosterFixture.LevelOf, ShippedRosterFixture.HealthOf, limits, new Random(seed),
                FamilyStretchFixture.LevelProfile, perHitOf: SyntheticPerHit);

        [TestMethod]
        public void Native_samples_ignore_stretch()
        {
            var sawStretch = false;

            foreach (var l in new[] { 400, 200 })
            for (var s = 1; s <= 5; s++)
            {
                var on = BuildWithPerHit(l, s, On);
                var off = BuildWithPerHit(l, s, Off);
                sawStretch |= on.Stretch != null;

                Assert.AreEqual(off.HealthNormalizeRatio, on.HealthNormalizeRatio, $"{l}/{s}");
                Assert.AreEqual(off.TrashHealthFloor, on.TrashHealthFloor, $"{l}/{s}");
                Assert.AreEqual(off.HealthCurveTarget, on.HealthCurveTarget, $"{l}/{s}");
                Assert.AreEqual(FamilyStretchFixture.StandardText(off.BandStandard), FamilyStretchFixture.StandardText(on.BandStandard), $"{l}/{s}");
                Assert.IsTrue(off.PerHitMedian > 0, "fixture: the damage ceiling sample is non-empty");
                Assert.AreEqual(off.PerHitMedian, on.PerHitMedian, $"{l}/{s}");
                Assert.AreEqual(off.PerHitSample, on.PerHitSample, $"{l}/{s}");
                Assert.AreEqual(off.DamageCeilingTop, on.DamageCeilingTop, $"{l}/{s}");

                var spec = FamilyStretchFixture.Spec(l, s);
                var bOn = DungeonPopulationBuilder.DamageCeilingBaselineFor(spec, ShippedRosterFixture.Tables, ShippedRosterFixture.LevelOf, On, SyntheticPerHit, FamilyStretchFixture.LevelProfile, null, 0);
                var bOff = DungeonPopulationBuilder.DamageCeilingBaselineFor(spec, ShippedRosterFixture.Tables, ShippedRosterFixture.LevelOf, Off, SyntheticPerHit, FamilyStretchFixture.LevelProfile, null, 0);
                Assert.AreEqual(bOff.Median, bOn.Median, $"{l}/{s}");
                Assert.AreEqual(bOff.SampleSize, bOn.SampleSize, $"{l}/{s}");
            }

            Assert.IsTrue(sawStretch, "fixture: at least one free draw stretched, or this test proves nothing");
        }

        // ---- the accepted side effect: eligibility widening and weighting ----------------------------------------------

        private static readonly Dictionary<uint, int> SynthLevels = new Dictionary<uint, int>
        {
            [9001] = 310, [9002] = 320, // alpha, native
            [9011] = 305, [9012] = 330, // beta, native
            [9021] = 100, [9022] = 120, // gamma, stretches
            [9031] = 140, [9032] = 150, // delta, stretches
            [9041] = 90, [9042] = 110,  // epsilon, stretches
        };

        private static SpeciesTableDef Table(string id, params uint[] wcids) => new SpeciesTableDef
        {
            Id = id,
            Members = wcids.Select(w => new SpeciesMemberDef { Wcid = w, Level = SynthLevels[w], Role = 0 }).ToList(),
        };

        private static IReadOnlyDictionary<string, SpeciesTableDef> SynthTables() => new Dictionary<string, SpeciesTableDef>
        {
            ["alpha"] = Table("alpha", 9001, 9002), ["beta"] = Table("beta", 9011, 9012),
            ["gamma"] = Table("gamma", 9021, 9022), ["delta"] = Table("delta", 9031, 9032), ["epsilon"] = Table("epsilon", 9041, 9042),
        };

        private static int SynthLevelOf(uint w) => SynthLevels.TryGetValue(w, out var l) ? l : 0;

        private static DungeonStatProfile SynthProfile(uint w)
        {
            var level = SynthLevelOf(w);

            if (level <= 0)
                return DungeonStatProfile.Empty;

            var l = (uint)level;
            return new DungeonStatProfile(level, 10 * l, new Dictionary<Skill, uint> { [Skill.MeleeDefense] = 2 * l, [Skill.HeavyWeapons] = 2 * l }, l, 2 * l);
        }

        private static DungeonSpawnPlan BuildSynth(int seed, bool stretch, int minEligible = 4)
        {
            var limits = new DungeonPopulationLimits(60, 3.0, 3.0, 8, 0.5, minEligibleFamilies: minEligible, reachUp: false, statCurve: false, familyStretch: stretch);

            return DungeonPopulationBuilder.Build(FamilyStretchFixture.Spec(300, seed), ShippedRosterFixture.Dungeon(30), ThreadDungeonStoreTests.LoadShipped(),
                SynthTables(), SynthLevelOf, w => (uint)(10 * SynthLevelOf(w)), limits, new Random(seed), SynthProfile, archetypeProfileOf: SynthProfile);
        }

        [TestMethod]
        public void Stretch_stops_eligibility_widening_and_weighting()
        {
            var offWeighted = 0;
            var onDrewStretched = false;

            for (var seed = 1; seed <= 10; seed++)
            {
                var off = BuildSynth(seed, false);
                var on = BuildSynth(seed, true);

                Assert.IsTrue(off.Notes.Any(n => n.StartsWith("band: only 2 family/families", StringComparison.Ordinal)), $"off seed {seed}: " + string.Join(" / ", off.Notes));
                Assert.IsFalse(on.Notes.Any(n => n.StartsWith("band: only", StringComparison.Ordinal)), $"on seed {seed}: " + string.Join(" / ", on.Notes));
                Assert.IsFalse(on.Notes.Any(n => n.Contains("archetype-weighted")), $"on seed {seed}: " + string.Join(" / ", on.Notes));

                if (off.Notes.Any(n => n.Contains("archetype-weighted")))
                    offWeighted++;

                onDrewStretched |= on.Stretch != null;
            }

            Assert.IsTrue(offWeighted > 0, "off widens and so takes the archetype-weighted draw");
            Assert.IsTrue(onDrewStretched, "on draws a stretched family at least once over seeds 1..10");
        }

        [TestMethod]
        public void Weighted_draw_is_skipped_even_when_the_band_still_widens()
        {
            // 2 natives + 3 stretched < 10, so the ladder still widens with the switch on (decision 6's "otherwise" arm);
            // the archetype-weighted draw must still be skipped because the stretched set is non-empty.
            var offWeighted = 0;

            for (var seed = 1; seed <= 10; seed++)
            {
                var off = BuildSynth(seed, false, 10);
                var on = BuildSynth(seed, true, 10);

                Assert.IsTrue(on.Notes.Any(n => n.StartsWith("band: only 2 family/families", StringComparison.Ordinal)), $"on seed {seed} widens: " + string.Join(" / ", on.Notes));
                Assert.IsFalse(on.Notes.Any(n => n.Contains("archetype-weighted")), $"on seed {seed}: " + string.Join(" / ", on.Notes));

                if (off.Notes.Any(n => n.Contains("archetype-weighted")))
                    offWeighted++;
            }

            Assert.IsTrue(offWeighted > 0, "off at the same threshold takes the archetype-weighted draw");
        }

        [TestMethod]
        public void Promoted_boss_from_a_single_elite_stretched_pool_is_unique_in_its_pack()
        {
            // Curated boss rows excluded through fits, so the boss is promoted from armoredillo's ONE stretched elite.
            var bossRows = new HashSet<uint>(ThreadDungeonStoreTests.LoadShipped().Bosses.Select(b => b.Wcid));
            var armoredillo = new HashSet<uint>(ShippedRosterFixture.Tables["armoredillo"].Members.Select(m => m.Wcid));
            bool Fits(uint w) => !bossRows.Contains(w) || armoredillo.Contains(w);

            for (var seed = 1; seed <= 10; seed++)
            {
                var plan = Build(400, seed, On, "armoredillo", Fits);
                var off = Build(400, seed, Off, "any", Fits);

                Assert.AreEqual("armoredillo", plan.FamilyId, $"seed {seed}");
                Assert.IsNotNull(plan.Stretch, $"seed {seed}");
                Assert.IsTrue(plan.Notes.Any(n => n.StartsWith("boss: no curated row", StringComparison.Ordinal)), $"seed {seed} promotes: " + string.Join(" / ", plan.Notes));

                var boss = plan.Entries.Single(e => e.Role == DungeonRole.Boss);
                Assert.IsFalse(boss.Stretched, $"seed {seed}");
                Assert.AreEqual(400, boss.UpliftLevel, $"seed {seed}");

                var nonBoss = NonBoss(plan).ToList();
                Assert.IsTrue(nonBoss.All(e => e.Stretched), $"seed {seed}: every re-drawn slot stays stretched");
                Assert.IsTrue(plan.BossLevel > nonBoss.Max(e => e.UpliftLevel), $"seed {seed}: boss {plan.BossLevel}");
                Assert.IsFalse(nonBoss.Any(e => e.Wcid == plan.BossWcid), $"seed {seed}: boss {plan.BossWcid} duplicated in its pack");

                Assert.AreEqual(off.HealthNormalizeRatio, plan.HealthNormalizeRatio, $"seed {seed}");
                Assert.AreEqual(off.TrashHealthFloor, plan.TrashHealthFloor, $"seed {seed}");
                Assert.AreEqual(FamilyStretchFixture.StandardText(off.BandStandard), FamilyStretchFixture.StandardText(plan.BandStandard), $"seed {seed}");
            }
        }

        // ---- speed courses, fit filter, forced families ----------------------------------------------------------------

        [TestMethod]
        public void Speed_course_limits_never_stretch()
        {
            var on = Build(400, 1, On.WithForcedFamilyWidening(), "drudge");
            Assert.AreEqual(Fp(Build(400, 1, Off.WithForcedFamilyWidening(), "drudge")), Fp(on));
            Assert.IsNull(on.Stretch);
        }

        [TestMethod]
        public void Fit_filter_thins_and_empties_a_stretched_pool()
        {
            var thinned = Build(400, 1, On, "drudge", w => w != 73227);
            Assert.AreEqual("drudge", thinned.FamilyId);
            Assert.IsNotNull(thinned.Stretch);
            Assert.IsFalse(thinned.Entries.Any(e => e.Wcid == 73227), "73227 does not fit");
            Assert.AreEqual(460, thinned.Stretch.MappedHigh);

            var top = NonBoss(thinned).Where(e => e.Wcid == 22899).ToList();
            Assert.IsTrue(top.Count > 0, "fixture: 22899 drawn");
            Assert.IsTrue(top.All(e => e.UpliftLevel == 460), "22899 is the top fitting member and maps to 460");

            var drudgeWcids = new HashSet<uint>(ShippedRosterFixture.Tables["drudge"].Members.Select(m => m.Wcid));
            bool NoDrudge(uint w) => !drudgeWcids.Contains(w);

            var emptied = Build(400, 1, On, "drudge", NoDrudge);
            Assert.IsNull(emptied.FamilyId);
            Assert.IsTrue(emptied.Notes.Any(n => n.StartsWith("family:", StringComparison.Ordinal)), string.Join(" / ", emptied.Notes));

            for (var seed = 1; seed <= 20; seed++)
                Assert.AreNotEqual("drudge", Build(400, seed, On, "any", NoDrudge).FamilyId, $"seed {seed}");
        }

        [TestMethod]
        public void Forced_stretched_and_forced_excluded_families()
        {
            var moss = Build(480, 1, On, "mosswart");
            Assert.AreEqual("mosswart", moss.FamilyId);
            Assert.IsNotNull(moss.Stretch);

            var chicken = Build(400, 1, On, "chicken");
            Assert.IsNull(chicken.FamilyId);
            Assert.IsTrue(chicken.Notes.Any(n => n.Contains("has no trash member")), string.Join(" / ", chicken.Notes));
        }

        [TestMethod]
        public void Stretched_pack_never_outranks_the_band_or_its_boss()
        {
            var stretched = 0;

            for (var seed = 1; seed <= 10; seed++)
            {
                var plan = Build(201, seed, On, "wisp");

                if (plan.Stretch == null)
                    continue;

                stretched++;

                var pack = NonBoss(plan).ToList();

                foreach (var e in pack)
                    Assert.IsTrue(ShippedRosterFixture.LevelOf(e.Wcid) <= plan.Stretch.HighEdge,
                        $"seed {seed}: wcid {e.Wcid} authored level {ShippedRosterFixture.LevelOf(e.Wcid)} above band top {plan.Stretch.HighEdge}");

                var packMax = pack.Count == 0 ? 0 : pack.Max(e => Math.Max(ShippedRosterFixture.LevelOf(e.Wcid), e.UpliftLevel));
                Assert.IsTrue(plan.BossLevel > packMax, $"seed {seed}: boss {plan.BossLevel} vs pack max {packMax}");
            }

            Assert.IsTrue(stretched > 0, "forced wisp at 201 must stretch");
        }

        // ---- the plan cache ---------------------------------------------------------------------------------------------

        private static ThreadPlanMemo FreshMemo()
        {
            ThreadPlanCache.Invalidate();
            return ThreadPlanCache.ForRun(true, null, ShippedRosterFixture.LevelOf, ShippedRosterFixture.HealthOf, FamilyStretchFixture.LevelProfile);
        }

        [TestMethod]
        public void Warm_plan_cache_does_not_leak_across_switch_states()
        {
            try
            {
                var coldOn = Fp(Build(400, 1, On));
                var coldOff = Fp(Build(400, 1, Off));
                Assert.AreNotEqual(coldOff, coldOn, "fixture: seed 1 at 400 must differ on vs off, or a leak could not show");

                var memo = FreshMemo();
                Assert.IsNotNull(memo);
                Assert.AreEqual(coldOn, Fp(Build(400, 1, On, memo: memo)), "on, cold memo");
                Assert.AreEqual(coldOff, Fp(Build(400, 1, Off, memo: memo)), "off after on, same memo");

                memo = FreshMemo();
                Assert.AreEqual(coldOff, Fp(Build(400, 1, Off, memo: memo)), "off, cold memo");
                Assert.AreEqual(coldOn, Fp(Build(400, 1, On, memo: memo)), "on after off, same memo");
            }
            finally
            {
                ThreadPlanCache.Invalidate();
            }
        }
    }
}
