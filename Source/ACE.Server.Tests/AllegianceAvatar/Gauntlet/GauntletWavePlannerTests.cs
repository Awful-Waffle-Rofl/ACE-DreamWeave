using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.Avatars.Gauntlet;
using ACE.Server.Managers;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.Avatars.Gauntlet
{
    /// <summary>
    /// GauntletWavePlanner: what it hands the build (spec, group, the per-wave curve adjustment), how it delivers the wave
    /// (PickPortals over the site's non-interior points, PlanStream, the boss at the furthest portal) and the consumer pin
    /// that a level-500 Gauntlet wave shapes to exactly the Thread fixture's health.
    ///
    /// Most tests drive the planner through a capturing build seam and a fixed group seam, so they read no PropertyManager.
    /// The two that read live values (the default group seam, and the fixture build plus shaping) load every compiled default
    /// before and restore them after (DefaultPropertyManager.LoadDefaultProperties, the BuildLivePlanGoldenTests convention).
    /// </summary>
    [TestClass]
    public class GauntletWavePlannerTests
    {
        private const int RunSeed = 90210;

        private const uint TrashA = 0x7F1C8001;
        private const uint TrashB = 0x7F1C8002;
        private const uint EliteA = 0x7F1C8011;
        private const uint EliteB = 0x7F1C8012;
        private const uint BossW = 0x7F1C8021;

        private sealed class Capture
        {
            public readonly List<(DungeonGemSpec Spec, DungeonEntryDef Site, GroupScaling Group, Func<DungeonPopulationLimits, DungeonPopulationLimits> Adjust)> Calls = new();
            public DungeonSpawnPlan Plan;

            public DungeonSpawnPlan Build(DungeonGemSpec spec, DungeonEntryDef site, GroupScaling group, Func<DungeonPopulationLimits, DungeonPopulationLimits> adjust,
                out ThreadDungeonSpawner.LivePlanBuildStats stats)
            {
                Calls.Add((spec, site, group, adjust));
                stats = default;
                return Plan ?? FakePlan();
            }
        }

        private static GroupScaling FixedGroup(int h) => GroupScaling.Compute(h, GroupScaling.DefaultEffortPerMember, GroupScaling.DefaultCountShare,
            GroupScaling.DefaultGroupMaxMonsters, GroupScaling.DefaultDamageRatingPerMember, GroupScaling.DefaultDamageRatingCap,
            GroupScaling.DefaultRewardBonusPerMember, GroupScaling.DefaultRewardBonusCap, 1.0);

        private static DungeonSpawnPlan FakePlan(bool bossNoCandidate = false, int elites = 2)
        {
            var plan = new DungeonSpawnPlan { FamilyId = "fake", HealthCurveTarget = 50000, HealthMultiplier = 1.5, BossHealthMultiplier = 3.0, BossNoCandidate = bossNoCandidate };

            plan.Entries.Add(new DungeonSpawnPlanEntry(TrashA, DungeonRole.Trash, null));
            plan.Entries.Add(new DungeonSpawnPlanEntry(TrashB, DungeonRole.Trash, null));

            foreach (var w in new[] { EliteA, EliteB }.Take(elites))
                plan.Entries.Add(new DungeonSpawnPlanEntry(w, DungeonRole.Elite, null));

            if (!bossNoCandidate)
                plan.Entries.Add(new DungeonSpawnPlanEntry(BossW, DungeonRole.Boss, null, 0, 0, false));

            return plan;
        }

        private static (GauntletWavePlanner Planner, Capture Capture, List<int> Groups) Make(GauntletTuning tuning = null, DungeonSpawnPlan plan = null)
        {
            var capture = new Capture { Plan = plan };
            var groups = new List<int>();
            var planner = new GauntletWavePlanner(tuning ?? GauntletTuning.Defaults, capture.Build, h => { groups.Add(h); return FixedGroup(h); });
            return (planner, capture, groups);
        }

        private static GauntletSiteDef Site() => GauntletFixtureSites.Site(GauntletFixtureSites.SixPortalRow());

        // ---- the spec ----------------------------------------------------------------------------------------------

        [TestMethod]
        public void Spec_LevelSeedFamilyAny_PerWave()
        {
            var (planner, capture, _) = Make();
            var site = Site();

            foreach (var wave in new[] { 1, 2, 13, 33, 50 })
                planner.Plan(site, wave, 10, RunSeed, "spec test");

            Assert.AreEqual(5, capture.Calls.Count, "one build per wave");

            var levels = capture.Calls.Select(c => c.Spec.Level).ToArray();
            CollectionAssert.AreEqual(new[] { 200, 225, 500, 1000, 1425 }, levels, "L(n) = 200 + 25 (n - 1), no maximum");

            for (var i = 0; i < capture.Calls.Count; i++)
            {
                var (spec, row, _, _) = capture.Calls[i];
                var wave = new[] { 1, 2, 13, 33, 50 }[i];

                Assert.AreEqual(site.Id, spec.DungeonId, "the site, picked by id");
                Assert.AreSame(site.Outdoor, row, "the site's own outdoor row is handed to the build");
                Assert.AreEqual(8, spec.Tier, "tier 8");
                Assert.AreEqual("any", spec.Family, "family any");
                Assert.AreEqual(GauntletWavePlanner.WaveSeed(RunSeed, wave), spec.Seed, "seed = hash(runSeed, n)");
                Assert.AreEqual(0, spec.Modifiers.Count, "no modifiers");
                Assert.AreEqual(0u, spec.RunId);
                Assert.AreEqual(0u, spec.OwnerGuid);
            }

            Assert.AreEqual(capture.Calls.Count, capture.Calls.Select(c => c.Spec.Seed).Distinct().Count(), "every wave draws a different seed");
            Assert.AreEqual(GauntletWavePlanner.WaveSeed(RunSeed, 7), GauntletWavePlanner.WaveSeed(RunSeed, 7), "the same pair, the same seed");
            Assert.AreNotEqual(GauntletWavePlanner.WaveSeed(RunSeed, 7), GauntletWavePlanner.WaveSeed(RunSeed + 1, 7), "another run, another seed");
            Assert.IsTrue(Enumerable.Range(1, 200).All(n => GauntletWavePlanner.WaveSeed(-5, n) >= 0), "never negative");
        }

        [TestMethod]
        public void CurveTop_IsWaveLevel_CappedAtTunable()
        {
            void Check(GauntletTuning tuning, int wave, int expectedTop, int expectedCeiling)
            {
                var (planner, capture, _) = Make(tuning);
                planner.Plan(Site(), wave, 5, RunSeed);

                var limits = capture.Calls.Single().Adjust(default);
                Assert.AreEqual(expectedTop, limits.HealthCurveTopLevel, $"wave {wave}: the curve top");
                Assert.AreEqual(expectedCeiling, limits.HealthCurveCeilingLevel, $"wave {wave}: the ceiling");
            }

            var d = GauntletTuning.Defaults;

            // Below the anchor-high level (375) the sanitizer lifts the top to 375; the curve is the same there either way.
            Check(d, 1, 375, 1000);
            Check(d, 13, 500, 1000);
            Check(d, 21, 700, 1000);
            Check(d, 33, 1000, 1000);
            Check(d, 41, 1000, 1000);

            // The tunable caps it, as both the top and the ceiling.
            var t800 = d with { HealthCurveTopLevel = 800 };
            Check(t800, 21, 700, 800);
            Check(t800, 33, 800, 800);

            // The pure adjustment itself.
            Assert.AreEqual(600, GauntletWavePlanner.CurveAdjust(600, 1000)(default).HealthCurveTopLevel);
            Assert.AreEqual(1000, GauntletWavePlanner.CurveAdjust(5000, 1000)(default).HealthCurveTopLevel);
        }

        // ---- the group ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void Group_ComputedFromHeadcount()
        {
            var (planner, capture, groups) = Make();
            var site = Site();

            var w0 = planner.Plan(site, 1, 0, RunSeed);
            var w37 = planner.Plan(site, 1, 37, RunSeed);
            var w250 = planner.Plan(site, 1, 250, RunSeed);

            CollectionAssert.AreEqual(new[] { 1, 37, 100 }, groups, "the group is asked for the clamped headcount");
            Assert.AreEqual(1, w0.Headcount);
            Assert.AreEqual(37, w37.Headcount);
            Assert.AreEqual(100, w250.Headcount, "headcount_cap 100");
            Assert.AreSame(w37.Group, capture.Calls[1].Group, "the group the wave carries is the one handed to the build");
            Assert.AreEqual(37, capture.Calls[1].Group.RosterSize);

            // The default seam is the live Thread group reader, over the Thread dials.
            try
            {
                DefaultPropertyManager.LoadDefaultProperties();

                var live = new Capture();
                var livePlanner = new GauntletWavePlanner(GauntletTuning.Defaults, live.Build);
                var wave = livePlanner.Plan(site, 1, 37, RunSeed);
                var expected = ThreadDungeonManager.ReadGroupScaling(37);

                Assert.AreEqual(37, wave.Group.RosterSize);
                Assert.AreEqual(expected.Effort, wave.Group.Effort, "effort from the Thread dials");
                Assert.AreEqual(expected.DamageRatingBonus, wave.Group.DamageRatingBonus, "damage rating from the Thread dials");
                Assert.IsTrue(wave.Group.Effort > 1.0, "positive control: a 37-member group is not solo");
            }
            finally
            {
                DefaultPropertyManager.LoadDefaultProperties();
            }
        }

        // ---- delivery ----------------------------------------------------------------------------------------------

        [TestMethod]
        public void BossWave_AddsBossEntry_HalvesTrash()
        {
            var (planner, _, _) = Make();
            var site = Site();
            var rules = new GauntletRules(GauntletTuning.Defaults);

            var plain = planner.Plan(site, 4, 10, RunSeed);
            var boss = planner.Plan(site, 5, 10, RunSeed);

            Assert.IsFalse(plain.IsBoss);
            Assert.IsTrue(boss.IsBoss);
            Assert.AreEqual(28, plain.TrashTarget, "8 + 2 x 10");
            Assert.AreEqual(14, boss.TrashTarget, "the boss wave's trash is halved");
            Assert.AreEqual(rules.TrashCount(10, true), boss.TrashTarget);
            Assert.AreEqual(rules.TimeLimitSeconds(5), boss.TimeLimitSeconds, "the boss bonus seconds");
            Assert.AreEqual(360, boss.TimeLimitSeconds);

            Assert.AreEqual(0, plain.Spawns.Count(s => s.Entry.Role != DungeonRole.Trash), "a plain wave streams trash only");

            foreach (var w in new[] { plain, boss })
            {
                var trash = w.Spawns.Count(s => s.Entry.Role == DungeonRole.Trash);
                var p = w.Portals.Count;
                Assert.IsTrue(p >= 3 && p <= 5, $"wave {w.Wave}: {p} portals");
                Assert.IsTrue(trash >= p * (w.TrashTarget / p) && trash <= p * ((w.TrashTarget + p - 1) / p), $"wave {w.Wave}: {trash} trash around the target {w.TrashTarget}");
            }

            var heads = boss.Spawns.Where(s => s.Entry.Role == DungeonRole.Boss).ToList();
            Assert.AreEqual(1, heads.Count, "exactly one boss");
            Assert.AreEqual(BossW, heads[0].Entry.Wcid);
            Assert.AreEqual(boss.BossPortal, heads[0].Portal);
            Assert.AreSame(boss.Portals[boss.BossPortal], heads[0].Entry.Point, "the boss stands on its portal");

            // The boss portal is the furthest from the centre.
            var centre = ThreadEncounterGeometry.Global(site.Centre, true);
            var distances = boss.Portals.Select(pt => ThreadEncounterGeometry.Distance(centre, ThreadEncounterGeometry.Global(pt, true), true)).ToList();
            Assert.AreEqual(distances.Max(), distances[boss.BossPortal]);

            // In delay order.
            for (var i = 1; i < boss.Spawns.Count; i++)
                Assert.IsTrue(boss.Spawns[i - 1].Delay <= boss.Spawns[i].Delay);

            Assert.AreEqual(0, boss.Notes.Count);
        }

        [TestMethod]
        public void FurthestFrom_PicksTheFurthestPortal()
        {
            var anchor = ThreadEncounterGeometry.Global(GauntletFixtureSites.At(100, 96), true);
            var portals = new[] { GauntletFixtureSites.Ring(20, 0), GauntletFixtureSites.Ring(50, 90), GauntletFixtureSites.Ring(35, 180) };

            Assert.AreEqual(1, GauntletWavePlanner.FurthestFrom(anchor, portals));
        }

        [TestMethod]
        public void NoBossCandidate_TwoExtraElites_Logged()
        {
            var (planner, _, _) = Make(plan: FakePlan(bossNoCandidate: true));
            var wave = planner.Plan(Site(), 5, 10, RunSeed);

            Assert.AreEqual(0, wave.Spawns.Count(s => s.Entry.Role == DungeonRole.Boss), "no boss");
            var standIns = wave.Spawns.Where(s => s.Entry.Role == DungeonRole.Elite).ToList();
            Assert.AreEqual(2, standIns.Count, "two elites stand in");
            CollectionAssert.AreEquivalent(new[] { EliteA, EliteB }, standIns.Select(s => s.Entry.Wcid).ToList());
            Assert.IsTrue(standIns.All(s => s.Portal == wave.BossPortal), "at the boss portal");
            Assert.AreEqual(1, wave.Notes.Count);
            StringAssert.Contains(wave.Notes[0], "no boss candidate");
            StringAssert.Contains(wave.Notes[0], "2 elite(s) stand in");

            // One elite only: it stands in twice.
            var (one, _, _) = Make(plan: FakePlan(bossNoCandidate: true, elites: 1));
            var single = one.Plan(Site(), 5, 10, RunSeed);
            Assert.AreEqual(2, single.Spawns.Count(s => s.Entry.Role == DungeonRole.Elite && s.Entry.Wcid == EliteA));

            // A plain wave never adds stand-ins.
            var (plain, _, _) = Make(plan: FakePlan(bossNoCandidate: true));
            Assert.AreEqual(0, plain.Plan(Site(), 4, 10, RunSeed).Spawns.Count(s => s.Entry.Role != DungeonRole.Trash));
        }

        [TestMethod]
        public void Portals_FromPickPortals_InteriorExcluded()
        {
            // Three open-ground and three interior points 30 m out, alternating every 60 degrees, so any three-portal pick over
            // all six must take an interior one (the point opposite the first pick is always of the other kind).
            var points = new List<DungeonSpawnPointDef>();
            for (var i = 0; i < 6; i++)
                points.Add(GauntletFixtureSites.Ring(30, i * 60, i % 2 == 0 ? "outdoor" : "interior"));

            var site = GauntletFixtureSites.Site(GauntletFixtureSites.Row("ow_interiorfixture", points));
            var tuning = GauntletTuning.Defaults with { PortalsMin = 3, PortalsMax = 3 };
            var (planner, _, _) = Make(tuning);

            for (var wave = 1; wave <= 12; wave++)
            {
                var plan = planner.Plan(site, wave, 10, RunSeed);
                Assert.IsNull(plan.Refusal, plan.Refusal);
                Assert.AreEqual(3, plan.Portals.Count);
                Assert.IsTrue(plan.Portals.All(p => p.Kind == "outdoor"), $"wave {wave}: an interior point was picked");

                // The consumer: the portals are exactly PickPortals' answer over the filtered candidates with the wave's own rng.
                var expected = ThreadEncounterPortalWaves.PickPortals(ThreadEncounterGeometry.Global(site.Centre, true), GauntletFixtureSites.Primary,
                    GauntletSiteStore.PortalCandidates(site.Outdoor), true, planner.PickConfig(), GauntletWavePlanner.DeliveryRng(plan.Spec.Seed), "centre", out _);
                CollectionAssert.AreEqual(expected, plan.Portals.ToList(), $"wave {wave}: the portals are PickPortals'");

                // Positive control: the same pick over every point, interior included, takes an interior one.
                var unfiltered = ThreadEncounterPortalWaves.PickPortals(ThreadEncounterGeometry.Global(site.Centre, true), GauntletFixtureSites.Primary,
                    points.Select(p => new PortalWaveCandidate(p, ThreadEncounterGeometry.Global(p, true), null)).ToList(), true, planner.PickConfig(),
                    GauntletWavePlanner.DeliveryRng(plan.Spec.Seed), "centre", out _);
                Assert.IsTrue(unfiltered.Any(p => p.Kind == "interior"), $"wave {wave}: positive control");

                // Every spawn stands on one of the wave's portals.
                Assert.IsTrue(plan.Spawns.All(s => ReferenceEquals(plan.Portals[s.Portal], s.Entry.Point)));
            }
        }

        [TestMethod]
        public void TooFewPortalPoints_RefusedWithNoSpawns()
        {
            var site = GauntletFixtureSites.Site(GauntletFixtureSites.Row("ow_sparse", new[] { GauntletFixtureSites.Ring(30, 0), GauntletFixtureSites.Ring(30, 180) }));
            var (planner, capture, _) = Make();
            var wave = planner.Plan(site, 1, 10, RunSeed);

            Assert.IsNotNull(wave.Refusal);
            Assert.AreEqual(0, wave.Spawns.Count);
            Assert.AreEqual(0, wave.Portals.Count);
            Assert.IsNotNull(wave.Plan, "the plan is still built");
            Assert.AreEqual(1, capture.Calls.Count);
        }

        [TestMethod]
        public void UnspawnedEstimate_IsCurveTargetTimesRoleMultiplier_HeldToMax()
        {
            var plan = FakePlan();
            var e = GauntletWavePlanner.UnspawnedEstimates(plan, 2_000_000_000);

            Assert.AreEqual(75000, e[DungeonRole.Trash]);
            Assert.AreEqual(75000, e[DungeonRole.Elite]);
            Assert.AreEqual(150000, e[DungeonRole.Boss]);

            Assert.AreEqual(100000, GauntletWavePlanner.UnspawnedEstimates(plan, 100000)[DungeonRole.Boss], "held to max_creature_health");
        }

        // ---- the consumer pin: shaped health at level 500 equals the Thread fixture's ------------------------------

        /// <summary>
        /// A level-500 Gauntlet wave (wave 13 at the shipped values), built through the planner's own curve adjustment on the
        /// shipped outdoor site with the golden fixture sources, and every entry shaped through GauntletSpawner.Prepare, carries
        /// exactly the max health of the same spec built as a Thread (no adjustment) and shaped as a Thread creature with the same
        /// group. Then two controls prove the pin can fail: a fixed curve top of 1000 (the rejected design) moves at least one
        /// entry's health at level 500, and at level 750 the Gauntlet wave carries more health than the Thread's flat curve.
        /// </summary>
        [TestMethod]
        public void Level500Wave_ShapedHealth_EqualsThreadFixture()
        {
            TestGameTables.EnsureInitialized();
            var store = ThreadDungeonStoreTests.LoadShipped();
            Assert.IsTrue(store.Dungeons.TryGetValue(BuildLivePlanGoldenTests.OutdoorSiteId, out var row) && row.IsOutdoor);

            var centre = row.Points.First(p => p.Curated);
            var site = new GauntletSiteDef { Id = row.Id, Centre = centre, Radius = 60, Pen = centre, Entries = new List<DungeonSpawnPointDef> { centre }, Outdoor = row };
            var seeded = new List<BuildLivePlanGoldenTests.SeededWeenie>();

            try
            {
                DefaultPropertyManager.LoadDefaultProperties();
                BuildLivePlanGoldenTests.SeedWeenieCache(store, seeded);

                var sources = BuildLivePlanGoldenTests.FixtureSources(new BuildLivePlanGoldenTests.FixtureCalls(), store);

                DungeonSpawnPlan FixtureBuild(DungeonGemSpec spec, DungeonEntryDef s, GroupScaling g, Func<DungeonPopulationLimits, DungeonPopulationLimits> adjust,
                    out ThreadDungeonSpawner.LivePlanBuildStats stats)
                {
                    ThreadPlanCache.Invalidate();
                    return ThreadDungeonSpawner.BuildLivePlan(spec, s, g, sources, () => "gauntlet consumer pin", out stats, adjust);
                }

                var planner = new GauntletWavePlanner(GauntletTuning.Defaults, FixtureBuild);
                var owner = FakeGauntletOwner.At(centre);

                // Level 500: wave 13.
                var wave = planner.Plan(site, 13, 20, RunSeed, "consumer pin");
                Assert.AreEqual(500, wave.Level);
                Assert.AreEqual(500, wave.Stats.Limits.HealthCurveTopLevel, "the build saw the per-wave top");
                Assert.AreEqual(1000, wave.Stats.Limits.HealthCurveCeilingLevel, "and the Gauntlet ceiling");

                ThreadPlanCache.Invalidate();
                var thread = ThreadDungeonSpawner.BuildLivePlan(wave.Spec, row, wave.Group, sources, () => "thread twin", out var threadStats);
                Assert.AreEqual(DungeonGemSpec.MaxRunLevel, threadStats.Limits.HealthCurveCeilingLevel, "the twin is a Thread build");

                CollectionAssert.AreEqual(thread.Entries.Select(e => (e.Wcid, e.Role)).ToList(), wave.Plan.Entries.Select(e => (e.Wcid, e.Role)).ToList(), "the same picks");

                var gauntletHealth = ShapeGauntlet(wave, owner);
                var threadHealth = ShapeThread(thread, wave.Group, 500);

                Assert.IsTrue(gauntletHealth.Count > 0 && gauntletHealth.All(h => h > 0), "positive control: shaped health");
                CollectionAssert.AreEqual(threadHealth, gauntletHealth, "a level-500 Gauntlet wave's shaped health is the Thread fixture's");

                // Control 1: the rejected fixed top of 1000 prices the same wave differently (so the equality above can fail).
                var fixedTop = new GauntletWavePlanner(GauntletTuning.Defaults, (DungeonGemSpec spec, DungeonEntryDef s, GroupScaling g, Func<DungeonPopulationLimits, DungeonPopulationLimits> _,
                    out ThreadDungeonSpawner.LivePlanBuildStats st) => FixtureBuild(spec, s, g, l => l.WithHealthCurveTopLevel(1000, 1000), out st));
                var fixedWave = fixedTop.Plan(site, 13, 20, RunSeed);
                CollectionAssert.AreNotEqual(threadHealth, ShapeGauntlet(fixedWave, owner), "control: a fixed top of 1000 moves level-500 health");

                // Control 2: above 500 the Gauntlet curve keeps growing where the Thread's is flat.
                var deep = planner.Plan(site, 23, 20, RunSeed);
                Assert.AreEqual(750, deep.Level);
                ThreadPlanCache.Invalidate();
                var deepThread = ThreadDungeonSpawner.BuildLivePlan(deep.Spec, row, deep.Group, sources, () => "thread twin 750", out _);
                Assert.IsTrue(deep.Plan.HealthCurveTarget > deepThread.HealthCurveTarget, "control: the Gauntlet curve target grows past 500");
                Assert.IsTrue(ShapeGauntlet(deep, owner).Max() > ShapeThread(deepThread, deep.Group, 750).Max(), "control: and so does the shaped health");
            }
            finally
            {
                ThreadPlanCache.Invalidate();
                DefaultPropertyManager.LoadDefaultProperties();
                BuildLivePlanGoldenTests.UnseedWeenieCache(seeded);
            }
        }

        /// <summary>Every plan entry through GauntletSpawner.Prepare, non-boss first so the boss sees the pack max (the live order). Health in plan order.</summary>
        private static List<uint> ShapeGauntlet(GauntletWavePlan wave, FakeGauntletOwner owner)
        {
            var settings = OutdoorPerimeterSettings.FromTunables();
            return Shape(wave.Plan, wave.Level, (c, e, obs) => GauntletSpawner.Prepare(c, wave, e, obs, owner, GauntletTuning.Defaults, settings, "pin"));
        }

        /// <summary>Every plan entry shaped as a Thread creature with the group's effort (Run null, Rewards on), in the same order.</summary>
        private static List<uint> ShapeThread(DungeonSpawnPlan plan, GroupScaling group, int level)
            => Shape(plan, level, (c, e, obs) => ThreadDungeonSpawner.ShapeCreature(c, plan, e, obs, new CreatureShapeOptions { Rewards = true, GroupEffort = group.Effort }));

        private static List<uint> Shape(DungeonSpawnPlan plan, int level, Action<Creature, DungeonSpawnPlanEntry, uint> shape)
        {
            var health = new uint[plan.Entries.Count];
            uint observed = 0;

            var order = Enumerable.Range(0, plan.Entries.Count).OrderBy(i => plan.Entries[i].Role == DungeonRole.Boss ? 1 : 0).ThenBy(i => i);

            foreach (var i in order)
            {
                var e = plan.Entries[i];
                var c = GauntletPlanProbeTests.MakeCreature(e.Wcid, level);
                shape(c, e, observed);
                health[i] = c.Health.MaxValue;

                if (e.Role != DungeonRole.Boss && health[i] > observed)
                    observed = health[i];
            }

            return health.ToList();
        }
    }
}
