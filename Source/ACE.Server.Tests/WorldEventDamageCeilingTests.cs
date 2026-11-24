using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using ACE.Entity.Enum.Properties;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The World Events damage ceiling (world_events_damage_ceiling, _ratio, _reference_ratio): the baseline is the
    /// Threads construction at compiled defaults over the shared species tables, the memo, the pure gate and per-hit
    /// math, the dials through their test seam, and source-order pins for the spawn site (a live Creature cannot be
    /// built in this harness). Reads no PropertyManager key: every dial goes through WorldEventUplift.DialSource.
    /// </summary>
    [TestClass]
    public class WorldEventDamageCeilingTests
    {
        private Func<WorldEventUpliftDials> savedDials;

        [TestInitialize]
        public void Pin()
        {
            savedDials = WorldEventUplift.DialSource;
            WorldEventBandMemo.Clear();
        }

        [TestCleanup]
        public void Restore()
        {
            WorldEventUplift.DialSource = savedDials;
            WorldEventBandMemo.Clear();
        }

        // ---- fixtures: the shipped roster with a synthetic per-hit read (DamageCeilingTests' own) ----------------

        private static CreaturePerHit SyntheticPerHit(uint wcid)
        {
            var level = ShippedRosterFixture.LevelOf(wcid);

            if (level <= 0)
                return CreaturePerHit.Empty;

            var melee = new PerHitSource(10 + wcid % 97 + level, PropertyAttribute.Strength, 100 + wcid % 50, false, isBody: true);
            var missile = wcid % 3 == 0 ? new PerHitSource(5 + wcid % 41 + level / 2.0, PropertyAttribute.Coordination, 150, true) : default;

            return new CreaturePerHit(level, melee, missile);
        }

        /// <summary>A synthetic stat profile off the roster's own level and health (no skills), for the planning-cache path.</summary>
        private static DungeonStatProfile SyntheticProfile(uint wcid)
            => new DungeonStatProfile(ShippedRosterFixture.LevelOf(wcid), ShippedRosterFixture.HealthOf(wcid), new Dictionary<ACE.Entity.Enum.Skill, uint>(),
                100u + (uint)ShippedRosterFixture.LevelOf(wcid), 300u);

        /// <summary>Every seventh wcid hits six times as hard, so the pool spreads past the cap.</summary>
        private static CreaturePerHit HeavyTailPerHit(uint wcid)
        {
            var p = SyntheticPerHit(wcid);
            return wcid % 7 == 0 ? new CreaturePerHit(p.Level, p.Melee.With(p.Melee.BaseDamage * 6, p.Melee.AttributeValue), p.Missile) : p;
        }

        private static IReadOnlyDictionary<string, SpeciesTableDef> Tables()
        {
            Assert.IsNotNull(ShippedRosterFixture.Tables, "Content/events/axes/species not found from the test output directory");
            return ShippedRosterFixture.Tables;
        }

        private static DamageCeilingBaseline We(int level, Func<uint, CreaturePerHit> perHitOf = null)
            => WorldEventDamageCeiling.Compute(Tables(), level, ShippedRosterFixture.LevelOf, perHitOf ?? SyntheticPerHit, null, null);

        /// <summary>The Threads limits a test builds a plan with (the five positional arguments are not read by the baseline).</summary>
        private static DungeonPopulationLimits ThreadsShipped() => new DungeonPopulationLimits(60, 3.0, 3.0, 8, 0.5);

        private static DungeonGemSpec Spec(int level, int seed) => new DungeonGemSpec("filos_doom", level, 6, "any", seed, null, 0, 0);

        private static readonly int[] Levels = { 200, 300, 326, 375, 390, 400 };

        // ---- the baseline ---------------------------------------------------------------------------------------

        [TestMethod]
        public void Baseline_IsTheThreadsConstruction_AtEveryLevel()
        {
            foreach (var level in Levels)
            {
                var we = We(level);
                var threads = DungeonPopulationBuilder.DamageCeilingBaselineFor(Spec(level, 1), Tables(), ShippedRosterFixture.LevelOf, ThreadsShipped(),
                    SyntheticPerHit, null, null);

                Assert.IsTrue(we.Median > 0, $"level {level}: positive control, a median was measured");
                Assert.AreEqual(threads.Median, we.Median, 0.0, $"level {level} median");
                Assert.AreEqual(threads.PoolMaxRatio, we.PoolMaxRatio, 0.0, $"level {level} pool max ratio");
                Assert.AreEqual(threads.SampleSize, we.SampleSize, $"level {level} sample");
                CollectionAssert.AreEqual(threads.Members.Select(m => (m.Wcid, m.Placement, m.PerHit)).ToList(),
                    we.Members.Select(m => (m.Wcid, m.Placement, m.PerHit)).ToList(), $"level {level} members");
            }
        }

        [TestMethod]
        public void Baseline_EqualsWhatAThreadsPlanCarries_AtTheSameLevel()
        {
            foreach (var level in Levels)
            {
                var plan = DungeonPopulationBuilder.Build(Spec(level, 3), ShippedRosterFixture.Dungeon(30), ThreadDungeonStoreTests.LoadShipped(),
                    Tables(), ShippedRosterFixture.LevelOf, ShippedRosterFixture.HealthOf, ThreadsShipped(), new Random(3), perHitOf: SyntheticPerHit);
                var we = We(level);
                var (cap, top) = WorldEventDamageCeiling.CapAndTop(we, WorldEventUpliftDials.Defaults);

                Assert.IsTrue(plan.PerHitMedian > 0, $"level {level}: positive control");
                Assert.AreEqual(plan.PerHitMedian, we.Median, 0.0, $"level {level}: the WE median is the Threads plan's");
                Assert.AreEqual(plan.DamageCeilingCap, cap, 0.0, $"level {level} cap");
                Assert.AreEqual(plan.DamageCeilingTop, top, 0.0, $"level {level} top");
            }
        }

        [TestMethod]
        public void Baseline_AboveTheAuthoredTop_IsPricedByProjection_AndRises()
        {
            var at375 = We(375);
            var at390 = We(390);
            var at400 = We(400);

            Assert.IsTrue(at390.Median > 0 && at400.Median > 0, "reach-up projection fields a pool above the authored top");
            Assert.IsTrue(at390.Members.Any(m => m.Placement == DamageCeilingPlacement.Stamped), "positive control: some member is stamped above its authored level");
            Assert.IsTrue(at400.Median >= at390.Median && at390.Median >= at375.Median, $"median rises with level: 375 {at375.Median}, 390 {at390.Median}, 400 {at400.Median}");
        }

        [TestMethod]
        public void ThreadsLimits_AreTheCompiledDefaults()
        {
            var l = WorldEventDamageCeiling.ThreadsLimits;

            Assert.AreEqual(DungeonPopulationLimits.DefaultRosterBandLow, l.RosterBandLow);
            Assert.AreEqual(DungeonPopulationLimits.DefaultRosterBandHigh, l.RosterBandHigh);
            Assert.AreEqual(DungeonPopulationLimits.DefaultBandLowFloorRatio, l.BandLowFloorRatio);
            Assert.AreEqual(DungeonPopulationLimits.DefaultMinEligibleFamilies, l.MinEligibleFamilies);
            Assert.AreEqual(DungeonPopulationLimits.DefaultMinFamilyPool, l.MinFamilyPool);
            Assert.AreEqual(DungeonPopulationLimits.DefaultReachUp, l.ReachUp);
            Assert.AreEqual(DungeonPopulationLimits.DefaultStatCurve, l.StatCurve);
            Assert.AreEqual(DungeonPopulationLimits.DefaultDefenseCurveRateAbove375, l.DefenseCurveRateAbove375);
            Assert.AreEqual(DungeonPopulationLimits.DefaultMagicDefenseCurveRateAbove300, l.MagicDefenseCurveRateAbove300);
            Assert.AreEqual(DungeonPopulationLimits.DefaultDamageCurveRateAbovePivot, l.DamageCurveRateAbovePivot);
            Assert.AreEqual(DungeonPopulationLimits.DefaultArchetypeUplift, l.ArchetypeUplift);
            Assert.AreEqual(DungeonPopulationLimits.DefaultUpliftOffenseRatio, l.UpliftOffenseRatio);
            Assert.AreEqual(DungeonPopulationLimits.DefaultUpliftDefenseRatio, l.UpliftDefenseRatio);
            Assert.AreEqual(DungeonPopulationLimits.DefaultUpliftArchetypeCap, l.UpliftArchetypeCap);
            Assert.AreEqual(DungeonPopulationLimits.DefaultUpliftDefenseCap, l.UpliftDefenseCap);
            Assert.AreEqual(DungeonPopulationLimits.DefaultUpliftAttributes, l.UpliftAttributes);
            Assert.AreEqual(0, WorldEventDamageCeiling.GemDamageRating);
        }

        [TestMethod]
        public void Baseline_IsEmpty_ForNoTablesNoReadOrNoLevel()
        {
            Assert.AreSame(DamageCeilingBaseline.Empty, WorldEventDamageCeiling.Compute(null, 300, ShippedRosterFixture.LevelOf, SyntheticPerHit, null, null));
            Assert.AreSame(DamageCeilingBaseline.Empty, WorldEventDamageCeiling.Compute(Tables(), 0, ShippedRosterFixture.LevelOf, SyntheticPerHit, null, null));
            Assert.AreSame(DamageCeilingBaseline.Empty, WorldEventDamageCeiling.Compute(Tables(), 300, ShippedRosterFixture.LevelOf, null, null, null));
        }

        // ---- the memo -------------------------------------------------------------------------------------------

        [TestMethod]
        public void Memo_ComputesOncePerTablesAndLevel_AndStaysBounded()
        {
            var tables = Tables();
            var calls = 0;

            DamageCeilingBaseline Compute(int level)
            {
                calls++;
                return We(level);
            }

            var a = WorldEventBandMemo.DamageCeilingFor(tables, 390, () => Compute(390));
            var b = WorldEventBandMemo.DamageCeilingFor(tables, 390, () => Compute(390));

            Assert.AreSame(a, b);
            Assert.AreEqual(1, calls, "the second read is served from the memo");
            Assert.AreEqual(1, WorldEventBandMemo.DamageCeilingCount);

            // A reload publishes a new dictionary, which must not match the old one.
            var copy = new Dictionary<string, SpeciesTableDef>(tables);
            WorldEventBandMemo.DamageCeilingFor(copy, 390, () => Compute(390));
            Assert.AreEqual(2, calls, "keyed by table REFERENCE");

            // Nothing is computed or stored for unusable input.
            Assert.AreSame(DamageCeilingBaseline.Empty, WorldEventBandMemo.DamageCeilingFor(null, 390, () => Compute(390)));
            Assert.AreSame(DamageCeilingBaseline.Empty, WorldEventBandMemo.DamageCeilingFor(tables, 0, () => Compute(0)));
            Assert.AreEqual(2, calls);

            for (var level = 1; level <= WorldEventBandMemo.MaxEntries + 20; level++)
                WorldEventBandMemo.DamageCeilingFor(tables, level, () => DamageCeilingBaseline.Empty);

            Assert.IsTrue(WorldEventBandMemo.DamageCeilingCount <= WorldEventBandMemo.MaxEntries);

            WorldEventBandMemo.Clear();
            Assert.AreEqual(0, WorldEventBandMemo.DamageCeilingCount);
        }

        [TestMethod]
        public void PlanMemo_SharedAcrossLevels_EqualsTheMemoFreeConstruction()
        {
            var memo = WorldEventDamageCeiling.NewPlanMemo(ShippedRosterFixture.LevelOf, SyntheticPerHit, SyntheticProfile, null);

            Assert.IsNotNull(memo);

            // Out of order and repeated, through ONE memo, as a run's waves would reach it.
            foreach (var level in new[] { 390, 200, 400, 326, 375, 300, 390, 201 })
            {
                var cached = WorldEventDamageCeiling.Compute(Tables(), level, memo);
                var free = WorldEventDamageCeiling.Compute(Tables(), level, ShippedRosterFixture.LevelOf, SyntheticPerHit, SyntheticProfile, null);

                Assert.IsTrue(free.Median > 0, $"level {level}: positive control");
                Assert.AreEqual(free.Median, cached.Median, 0.0, $"level {level} median");
                Assert.AreEqual(free.PoolMaxRatio, cached.PoolMaxRatio, 0.0, $"level {level} top");
                CollectionAssert.AreEqual(free.Members.Select(m => (m.Wcid, m.Placement, m.PerHit)).ToList(),
                    cached.Members.Select(m => (m.Wcid, m.Placement, m.PerHit)).ToList(), $"level {level} members");
            }

            Assert.IsTrue(memo.Hits > 0, "positive control: the shared memo served structural reads across levels");
            Assert.AreSame(DamageCeilingBaseline.Empty, WorldEventDamageCeiling.Compute(Tables(), 390, (ThreadPlanMemo)null));
            Assert.IsNull(WorldEventDamageCeiling.NewPlanMemo(ShippedRosterFixture.LevelOf, null, SyntheticProfile, null), "no per-hit read: no memo");
            Assert.IsNull(WorldEventDamageCeiling.NewPlanMemo(ShippedRosterFixture.LevelOf, SyntheticPerHit, null, null), "no profile read: no memo");
        }

        [TestMethod]
        public void PlanMemo_IsOnePerTableLoad_AndClearedWithTheMemo()
        {
            var tables = Tables();
            var created = 0;

            ThreadPlanMemo Create()
            {
                created++;
                return WorldEventDamageCeiling.NewPlanMemo(ShippedRosterFixture.LevelOf, SyntheticPerHit, SyntheticProfile, null);
            }

            var a = WorldEventBandMemo.DamageCeilingPlanMemoFor(tables, Create);

            Assert.AreSame(a, WorldEventBandMemo.DamageCeilingPlanMemoFor(tables, Create));
            Assert.AreEqual(1, created);

            var reloaded = new Dictionary<string, SpeciesTableDef>(tables);
            var b = WorldEventBandMemo.DamageCeilingPlanMemoFor(reloaded, Create);

            Assert.AreNotSame(a, b, "a reload (new tables reference) gets a fresh planning cache");
            Assert.AreEqual(2, created);

            WorldEventBandMemo.Clear();
            Assert.AreNotSame(b, WorldEventBandMemo.DamageCeilingPlanMemoFor(reloaded, Create), "Clear drops it with the generation");
            Assert.AreEqual(3, created);

            Assert.IsNull(WorldEventBandMemo.DamageCeilingPlanMemoFor(null, Create));
            Assert.AreEqual(3, created);
        }

        /// <summary>The Threads tests' archetype-capable profile (a melee skill, a defense, body damage, armour, Strength, Endurance).</summary>
        private static DungeonStatProfile ArchetypeProfile(uint wcid)
        {
            var level = ShippedRosterFixture.LevelOf(wcid);

            if (level <= 0)
                return null;

            return new DungeonStatProfile(level, ShippedRosterFixture.HealthOf(wcid),
                new Dictionary<ACE.Entity.Enum.Skill, uint> { [ACE.Entity.Enum.Skill.HeavyWeapons] = (uint)(level * 2), [ACE.Entity.Enum.Skill.MeleeDefense] = (uint)(level * 2) },
                (uint)(level + wcid % 13), (uint)(level * 3), 0, 0.0,
                new Dictionary<PropertyAttribute, uint> { [PropertyAttribute.Strength] = (uint)(100 + level / 2), [PropertyAttribute.Endurance] = (uint)(100 + level / 3) });
        }

        private static string MemberShape(DamageCeilingBaseline b)
            => string.Join("|", b.Members.Select(m => $"{m.Wcid}:{m.Trash}:{m.Elite}:{m.Placement}:{m.Predicted.ByArchetype}:{m.PerHit:R}"));

        [TestMethod]
        public void PlanMemo_WithAnArchetypeProfile_EqualsTheMemoFreeConstruction_InMixedOrder()
        {
            TestGameTables.EnsureInitialized();

            var memo = WorldEventDamageCeiling.NewPlanMemo(ShippedRosterFixture.LevelOf, SyntheticPerHit, ArchetypeProfile, ArchetypeProfile);
            var archetypePredicted = 0;

            Assert.IsTrue(memo.HasArchetypeProfiles);

            foreach (var level in new[] { 375, 200, 400, 300, 326, 390, 210, 375 })
            {
                var cached = WorldEventDamageCeiling.Compute(Tables(), level, memo);
                var free = WorldEventDamageCeiling.Compute(Tables(), level, ShippedRosterFixture.LevelOf, SyntheticPerHit, ArchetypeProfile, ArchetypeProfile);

                archetypePredicted += free.Members.Count(m => m.Predicted.ByArchetype);

                Assert.IsTrue(free.Median > 0, $"level {level}: positive control");
                Assert.AreEqual(free.Median, cached.Median, 0.0, $"level {level} median");
                Assert.AreEqual(free.PoolMaxRatio, cached.PoolMaxRatio, 0.0, $"level {level} top");
                Assert.AreEqual(MemberShape(free), MemberShape(cached), $"level {level}: every member prediction");
            }

            Assert.IsTrue(archetypePredicted > 0, "guard: the archetype uplift prediction actually ran");
        }

        [TestMethod]
        public void TheWeComputes_LeaveThreadsSharedPlanCacheUntouched()
        {
            TestGameTables.EnsureInitialized();

            var before = ThreadPlanCache.Counts.ToString();
            var tables = Tables();
            var memo = WorldEventBandMemo.DamageCeilingPlanMemoFor(tables,
                () => WorldEventDamageCeiling.NewPlanMemo(ShippedRosterFixture.LevelOf, SyntheticPerHit, ArchetypeProfile, ArchetypeProfile));

            foreach (var level in new[] { 390, 250, 400 })
            {
                var b = WorldEventBandMemo.DamageCeilingFor(tables, level, () => WorldEventDamageCeiling.Compute(tables, level, memo));
                Assert.IsTrue(b.Median > 0, $"level {level}: positive control");
                WorldEventDamageCeiling.Compute(tables, level, ShippedRosterFixture.LevelOf, SyntheticPerHit, ArchetypeProfile, ArchetypeProfile);
            }

            Assert.IsTrue(memo.Lookups > 0, "positive control: the WE planning cache was used");
            Assert.AreEqual(before, ThreadPlanCache.Counts.ToString(), "Threads' shared plan cache generation is never written by WE");
        }

        [TestMethod]
        public void AThrowingCompute_IsTriedOncePerGeneration_NeverCachedAsEmpty_AndClearRetries()
        {
            var tables = Tables();
            var calls = 0;

            DamageCeilingBaseline Throw()
            {
                calls++;
                throw new InvalidOperationException("boom");
            }

            Assert.ThrowsExactly<InvalidOperationException>(() => WorldEventBandMemo.DamageCeilingFor(tables, 390, Throw), "the first failure propagates (the spawner logs it)");
            Assert.IsNull(WorldEventBandMemo.DamageCeilingFor(tables, 390, Throw), "a known failure returns no baseline, without computing");
            Assert.AreEqual(1, calls, "tried once");
            Assert.AreEqual(0, WorldEventBandMemo.DamageCeilingCount, "the failure is not stored as an Empty baseline");
            Assert.AreEqual(1, WorldEventBandMemo.FailedDamageCeilingCount);

            // Another level is independent.
            Assert.AreSame(DamageCeilingBaseline.Empty, WorldEventBandMemo.DamageCeilingFor(tables, 391, () => DamageCeilingBaseline.Empty));

            WorldEventBandMemo.Clear();
            Assert.AreEqual(0, WorldEventBandMemo.FailedDamageCeilingCount);
            Assert.ThrowsExactly<InvalidOperationException>(() => WorldEventBandMemo.DamageCeilingFor(tables, 390, Throw), "Clear allows a retry");
            Assert.AreEqual(2, calls);

            // A later success after Clear is cached normally.
            WorldEventBandMemo.Clear();
            var ok = WorldEventBandMemo.DamageCeilingFor(tables, 390, () => We(390));
            Assert.AreSame(ok, WorldEventBandMemo.DamageCeilingFor(tables, 390, Throw));
            Assert.AreEqual(2, calls);
        }

        [TestMethod]
        public void Logging_IsDebugPerCreature_AndOneInfoSummaryPerBatch()
        {
            var lines = Lines("WorldEvents", "WorldEventSpawner.cs");
            var method = Body(lines, "private DamageCeilingResult ApplyWorldEventDamageCeiling(");

            Assert.IsFalse(method.Contains("log.Info("), "no Info per creature");
            Assert.IsTrue(method.Contains("log.Debug($\"[WORLDEVENT] run={evt?.RunId} damage ceiling wcid="));
            Assert.IsTrue(method.Contains("if (log.IsDebugEnabled && Lowered(result))"));

            var spawn = Body(lines, "private void Spawn(WorldEvent evt, IReadOnlyList<SpawnEntry> entries,");
            Assert.AreEqual(1, System.Text.RegularExpressions.Regex.Matches(spawn, @"log\.Info\(\$""\[WORLDEVENT\] run=\{evt\.RunId\} \{kind\} damage ceiling lowered").Count,
                "exactly one Info summary per Spawn batch");
        }

        [TestMethod]
        public void Memo_DoesNotTouchTheBandOrCurveMaps()
        {
            WorldEventBandMemo.DamageCeilingFor(Tables(), 300, () => We(300));

            Assert.AreEqual((0, 0, 0), WorldEventBandMemo.Counts, "Pinnacles' StandardFor/MedianHealthFor maps untouched");
            Assert.AreEqual((0, 0, 0), WorldEventBandMemo.CurveCounts);
        }

        // ---- the gate and the per-hit math ----------------------------------------------------------------------

        [TestMethod]
        public void Gate_CoversTheGuardedCreatures_WithASlotLevel_AndTheDialOn()
        {
            var on = WorldEventUpliftDials.Defaults;
            var off = new WorldEventUpliftDials(true, true, 508, damageCeiling: false);

            Assert.IsTrue(WorldEventDamageCeiling.Applies(on, WorldEventSpawnKind.Wave, false, 390));
            Assert.IsTrue(WorldEventDamageCeiling.Applies(on, WorldEventSpawnKind.Boss, false, 390), "a non-named boss (a family champion) is covered");
            Assert.IsFalse(WorldEventDamageCeiling.Applies(on, WorldEventSpawnKind.Boss, true, 390), "the named boss is exempt");
            Assert.IsFalse(WorldEventDamageCeiling.Applies(off, WorldEventSpawnKind.Wave, false, 390), "dial off");
            Assert.IsFalse(WorldEventDamageCeiling.Applies(on, WorldEventSpawnKind.Wave, false, 0), "unknown slot level: no median to read");
            Assert.IsFalse(WorldEventDamageCeiling.Applies(on, WorldEventSpawnKind.Wave, false, WorldEventSpawner.NoSlotLevel));

            foreach (var kind in new[] { WorldEventSpawnKind.Source, WorldEventSpawnKind.Decor, WorldEventSpawnKind.Npc, WorldEventSpawnKind.InertObjective,
                         WorldEventSpawnKind.Add, WorldEventSpawnKind.Protected })
                Assert.IsFalse(WorldEventDamageCeiling.Applies(on, kind, false, 390), $"{kind} is not a roster monster");

            // The exact set the WE magic attack ceiling and combat guard cover.
            foreach (WorldEventSpawnKind kind in Enum.GetValues(typeof(WorldEventSpawnKind)))
                foreach (var named in new[] { false, true })
                    Assert.AreEqual(WorldEventCombatGuard.AppliesTo(kind, named), WorldEventDamageCeiling.Applies(on, kind, named, 390), $"{kind} named={named}");
        }

        [TestMethod]
        public void PerHit_AboveTheCap_IsCompressedToWhatThreadsProduces()
        {
            var baseline = We(390, HeavyTailPerHit);
            var dials = WorldEventUpliftDials.Defaults;
            var (cap, top) = WorldEventDamageCeiling.CapAndTop(baseline, dials);
            var m = baseline.Median;

            Assert.IsTrue(m > 0 && top > cap, $"positive control: median {m}, the heavy tail spreads past the cap (top {top})");

            foreach (var ratio in new[] { 0.5, 1.0, 1.5, 2.5, 4.0, top, top * 2 })
            {
                var perHit = m * ratio;
                var after = WorldEventDamageCeiling.PerHitAfter(perHit, baseline, dials);

                Assert.AreEqual(DamageCeiling.FinalPerHit(perHit, m, cap, top, 0, 0), after, 1e-9, $"ratio {ratio}: the Threads solo value");
                Assert.IsTrue(after <= cap * m + 1e-9, $"ratio {ratio}: never above cap x median");
                Assert.IsTrue(after <= perHit + 1e-9, $"ratio {ratio}: never raised");
            }

            Assert.AreEqual(m * 0.5, WorldEventDamageCeiling.PerHitAfter(m * 0.5, baseline, dials), 1e-9, "below the median: untouched");
            Assert.AreEqual(cap * m, WorldEventDamageCeiling.PerHitAfter(m * top * 2, baseline, dials), 1e-9, "above top: clamped to the cap");

            // A forced reference ratio replaces the automatic top.
            var forced = new WorldEventUpliftDials(true, true, 508, true, 2.0, 3.0);
            Assert.AreEqual(3.0, WorldEventDamageCeiling.CapAndTop(baseline, forced).Top);
            Assert.AreEqual(DamageCeiling.FinalPerHit(m * 2.5, m, 2.0, 3.0, 0, 0), WorldEventDamageCeiling.PerHitAfter(m * 2.5, baseline, forced), 1e-9);

            // No median: the hit is left alone.
            Assert.AreEqual(500.0, WorldEventDamageCeiling.PerHitAfter(500.0, DamageCeilingBaseline.Empty, dials));
            Assert.AreEqual(0.0, WorldEventDamageCeiling.CapAndTop(DamageCeilingBaseline.Empty, dials).Top);
        }

        // ---- the dials ------------------------------------------------------------------------------------------

        [TestMethod]
        public void Dials_DefaultOn_AndSanitizeByTheThreadsRules()
        {
            var d = WorldEventUpliftDials.Defaults;

            Assert.IsTrue(d.DamageCeiling, "new settings default on");
            Assert.AreEqual(DamageCeiling.DefaultCap, d.DamageCeilingRatio);
            Assert.AreEqual(DamageCeiling.DefaultReferenceRatio, d.DamageCeilingReferenceRatio);

            try
            {
                WorldEventUplift.DialSource = () => new WorldEventUpliftDials(true, true, 508, true, double.NaN, -1.0);
                var s = WorldEventUplift.Current();
                Assert.AreEqual(DamageCeiling.DefaultCap, s.DamageCeilingRatio, "NaN cap reads as the default");
                Assert.AreEqual(0.0, s.DamageCeilingReferenceRatio, "negative reference reads as auto");

                WorldEventUplift.DialSource = () => new WorldEventUpliftDials(true, true, 508, false, 0.5, double.PositiveInfinity);
                s = WorldEventUplift.Current();
                Assert.IsFalse(s.DamageCeiling);
                Assert.AreEqual(DamageCeiling.DefaultCap, s.DamageCeilingRatio, "a cap below 1 reads as the default");
                Assert.AreEqual(0.0, s.DamageCeilingReferenceRatio);

                WorldEventUplift.DialSource = () => new WorldEventUpliftDials(false, false, 400, true, 3.0, 5.0);
                s = WorldEventUplift.Current();
                Assert.AreEqual(3.0, s.DamageCeilingRatio);
                Assert.AreEqual(5.0, s.DamageCeilingReferenceRatio);
                Assert.IsFalse(s.StatCurve, "the existing dials pass through");
                Assert.AreEqual(400.0, s.MagicAttackSkillCap);
            }
            finally
            {
                WorldEventUplift.DialSource = savedDials;
            }
        }

        // ---- source pins ----------------------------------------------------------------------------------------

        private static string SourceRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ACE.Server", "WorldEvents", "WorldEventDamageCeiling.cs")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find ACE.Server/WorldEvents/WorldEventDamageCeiling.cs by walking up from {AppContext.BaseDirectory}");

            return Path.Combine(dir.FullName, "ACE.Server");
        }

        private static string[] Lines(params string[] path) => File.ReadAllLines(Path.Combine(new[] { SourceRoot() }.Concat(path).ToArray()));

        private static string Body(string[] lines, string signaturePrefix)
        {
            var start = Array.FindIndex(lines, l => l.Trim().StartsWith(signaturePrefix, StringComparison.Ordinal));
            Assert.IsTrue(start >= 0, $"missing {signaturePrefix}");

            var depth = 0;
            var opened = false;
            var body = new List<string>();

            for (var i = start; i < lines.Length; i++)
            {
                body.Add(lines[i]);
                depth += lines[i].Count(c => c == '{') - lines[i].Count(c => c == '}');
                opened |= lines[i].Contains('{');

                if (opened && depth == 0)
                    break;
            }

            return string.Join("\n", body);
        }

        [TestMethod]
        public void SpawnSite_RunsTheCeilingAfterEveryDamageWriter_AndBeforeHealth()
        {
            var lines = Lines("WorldEvents", "WorldEventSpawner.cs");

            int Find(string code)
            {
                var i = Array.FindIndex(lines, l => l.Trim() == code);
                Assert.IsTrue(i > 0, $"missing: {code}");
                Assert.AreEqual(i, Array.FindLastIndex(lines, l => l.Trim() == code), $"must appear once: {code}");
                return i;
            }

            var dials = Find("var upliftDials = WorldEventUplift.Current();");
            var uplift = Find("ApplyBandUplift(evt, creature, entry.UpliftLevel, kind, slotStandard, upliftDials);");
            var guard = Find("ApplyCombatGuard(evt, creature, entry.SlotLevel, kind, slotStandard);");
            var magic = Find("ApplyWorldEventMagicAttackCeiling(evt, creature, entry.SlotLevel, kind, upliftDials);");
            var ceiling = Find("if (Lowered(ApplyWorldEventDamageCeiling(evt, creature, entry.SlotLevel, kind, upliftDials)))");
            var multiplier = Find("ApplyHealthMultiplier(evt, creature, healthMult);");

            Assert.IsTrue(dials < uplift && uplift < guard && guard < magic && magic < ceiling && ceiling < multiplier,
                "uplift, guard, magic ceiling, damage ceiling, then the health multiplier (Threads' order)");
        }

        [TestMethod]
        public void SpawnerMethod_GatesBeforeItReadsTheMemoOrWrites()
        {
            var body = Body(Lines("WorldEvents", "WorldEventSpawner.cs"), "private DamageCeilingResult ApplyWorldEventDamageCeiling(");

            int At(string code)
            {
                var i = body.IndexOf(code, StringComparison.Ordinal);
                Assert.IsTrue(i >= 0, $"missing: {code}");
                return i;
            }

            var gate = At("if (!WorldEventDamageCeiling.Applies(upliftDials, kind, named, slotLevel))");
            var memo = At("WorldEventBandMemo.DamageCeilingFor(tables, slotLevel, () => WorldEventDamageCeiling.ComputeLiveCached(tables, slotLevel))");
            var apply = At("WorldEventDamageCeiling.Apply(creature, baseline, upliftDials)");

            Assert.IsTrue(gate < memo && memo < apply);
            Assert.IsTrue(body.Contains("BossKind.Named"), "the named boss is resolved for the gate");

            var applyBody = Body(Lines("WorldEvents", "WorldEventDamageCeiling.cs"), "internal static DamageCeilingResult Apply(");
            Assert.IsTrue(applyBody.IndexOf("!dials.DamageCeiling", StringComparison.Ordinal) < applyBody.IndexOf("DamageCeiling.Apply(creature", StringComparison.Ordinal),
                "dial off returns before any write");
        }

        [TestMethod]
        public void Simulate_ComputesTheCeilingWithoutTheMemo()
        {
            var simulate = Body(Lines("Command", "Handlers", "WorldEventCommands.cs"), "private static void HandleSimulate(");

            Assert.IsTrue(simulate.Contains("WorldEventDamageCeiling.ComputeLive(WorldEventManager.Store?.SpeciesTables, ceilingLevel)"));
            Assert.IsFalse(simulate.Contains("DamageCeilingFor("), "simulate must not write the world-thread memo");
            Assert.IsFalse(simulate.Contains("ComputeLiveCached("), "nor the world-thread planning cache");
            Assert.IsFalse(simulate.Contains("DamageCeilingPlanMemoFor("), "nor the world-thread planning cache");
            Assert.IsFalse(simulate.Contains("NewLivePlanMemo"), "nor a planning cache of its own");
        }

        [TestMethod]
        public void TheCeilingCode_ReadsNoThreadsTunable_AndPinnaclesDoNotApplyIt()
        {
            var lines = Lines("WorldEvents", "WorldEventDamageCeiling.cs");

            for (var i = 0; i < lines.Length; i++)
            {
                var code = lines[i];
                var commentAt = code.IndexOf("//", StringComparison.Ordinal);

                if (commentAt >= 0)
                    code = code.Substring(0, commentAt);

                Assert.IsFalse(code.Contains("\"dynamic_dungeons_"), $"WorldEventDamageCeiling.cs:{i + 1} reads a Threads tunable");
                Assert.IsFalse(code.Contains("PropertyManager"), $"WorldEventDamageCeiling.cs:{i + 1}: compiled defaults only");
            }

            var pinnacles = string.Join("\n", Lines("Pinnacles", "PinnacleScaling.cs"));
            Assert.IsFalse(pinnacles.Contains("DamageCeiling"), "Pinnacles stay byte-identical: no damage ceiling");
        }
    }
}
