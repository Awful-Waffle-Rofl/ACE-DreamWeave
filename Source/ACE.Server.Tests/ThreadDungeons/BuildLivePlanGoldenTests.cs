using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

using ACE.Database;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Managers;
using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;
using ACE.Server.WorldEvents.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// Golden plans for the BuildLivePlan extraction (Balance Trials plan A). Every row was captured from origin/master
    /// a4321a50e on 2026-11-18, BEFORE the extraction, by a scratch test that called master's unchanged pieces in
    /// TryPopulate's own order (ReadLimits, ResolveFitPredicate, ThreadPlanCache.ForRun, the memo delegate swap,
    /// DungeonPopulationBuilder.Build, ResolveCreatureAbilities) with the fixtures below. The extracted helper must
    /// reproduce every column for every row from the same inputs; a comparison of the helper with itself proves nothing.
    /// The rows were first captured at 58827ad0b and recaptured at a4321a50e because #2184 (family stretch to level 500)
    /// legitimately moved the plans; the same scratch test reproduced every 58827ad0b row exactly before the recapture.
    ///
    /// Fixtures, none of which touch the world database:
    /// - Roster: <see cref="ShippedRosterFixture"/> (the shipped species tables, levels and health from the files) and the
    ///   shipped store (<see cref="ThreadDungeonStoreTests.LoadShipped"/>).
    /// - Profile and per-hit: <see cref="SyntheticProfile"/> and <see cref="SyntheticPerHit"/>, copied verbatim from
    ///   DamageCeilingTests (private there). They stand in for LiveProfileOf, LiveArchetypeProfileOf and LivePerHitOf;
    ///   unlike the live readers they ignore the spell strip. Hand them to the build through <see cref="FixtureCalls"/>,
    ///   whose counts are the InnerCalls column.
    /// - Movement height (outdoor rows only): a flat 1.0 m, as OutdoorPopulationTests uses.
    /// - The fit filter: dynamic_dungeons_fit_filter is seeded per row. The indoor fixture (filos_doom) has a shipped
    ///   clearance row, so a fit-on indoor row would build a predicate over LiveMovementHeightOf, a world read; every
    ///   indoor row therefore runs fit OFF. Outdoor rows run fit ON (the shipped default), which resolves no predicate
    ///   for this site. fitKey is null on every row (the capture asserted it), so the goldens do not cover a live fit
    ///   predicate.
    /// - Creature-ability traits: ResolveCreatureAbilities calls LiveAbilityTraitsOf, which reads
    ///   DatabaseManager.World.GetCachedWeenie with no seam. <see cref="SeedWeenieCache"/> puts a bare creature weenie
    ///   (no spell book, no PropertyString 9015) in the world cache for every roster and boss wcid, overwriting whatever
    ///   was cached, so that read never falls through to the database; <see cref="UnseedWeenieCache"/> puts back exactly
    ///   what was there before. The capture asserted WeenieCacheMissTotal did not move, and so does the golden test.
    ///
    /// PropertyManager: <see cref="SeedProperties"/> loads every compiled default, then sets the two per-row keys;
    /// <see cref="RestoreProperties"/> loads the compiled defaults again. ThreadPlanCache is invalidated before and after
    /// every row, so memo hit and lookup counts are cold-cache counts.
    /// </summary>
    [TestClass]
    public class BuildLivePlanGoldenTests
    {
        internal const string FixedFamily = "olthoi";
        internal const string OutdoorSiteId = "ow_arwic";
        internal const float OutdoorMovementHeight = 1.0f;

        /// <summary>
        /// One golden row: the inputs (level, tier, family, seed, group size, plan cache, outdoor, fit filter) and what
        /// master produced. PlanHash is <see cref="ShippedRosterFixture.Hash"/>. AbilitiesFingerprint is
        /// <see cref="AbilitiesHash"/> ("null" when ResolveCreatureAbilities returned null). PlanExtras is
        /// <see cref="PlanExtras"/> (the ceiling and group fields ShippedRosterFixture.Fingerprint does not cover).
        /// MemoHits and MemoLookups are the run's memo counters on a cold cache (0 and 0 with the cache off). InnerCalls is
        /// <see cref="FixtureCalls.ToString"/>: how many times each fixture delegate was invoked. The memo is value-preserving
        /// by design, so a build that skips the memo delegate swap produces the same plan; only the inner call counts show it.
        /// </summary>
        internal sealed record GoldenCase(string CaseId, int Level, int Tier, string Family, int Seed, int GroupSize, bool PlanCache,
            bool Outdoor, bool FitFilter, string PlanHash, string AbilitiesFingerprint, string PlanExtras, int MemoHits, int MemoLookups,
            string InnerCalls);

        /// <summary>
        /// Captured from origin/master a4321a50e on 2026-11-18. Do not edit by hand, with ONE recorded exception (2026-11-21):
        /// species/slithis.json went "enabled": false (the Tendrils of T'thuun are never a family), which takes it out of the eligible-family
        /// count that sizes the L200 eligibility band, so rows L200-any-s1234-solo-cache-in, L200-fam-s98765-solo-nocache-in and
        /// L200-any-s98765-g3-nocache-out moved their band median/sample/top (PlanExtras) and their ph/lv call counts, and
        /// L400-any-s1234-g3-nocache-in and L500-any-s98765-g3-nocache-in moved their lv count (fewer members iterated). PlanHash,
        /// AbilitiesFingerprint and the memo counters did not move on any row. Proof the delta is the roster alone: with the
        /// "enabled": false line removed from slithis.json, every row passes against the ORIGINAL values.
        /// </summary>
        internal static readonly GoldenCase[] Golden =
        {
            new GoldenCase("L200-any-s1234-solo-cache-in", 200, 7, "any", 1234, 1, true, false, false,
                "99766044a5e3f7919b97f5dd", "null",
                "median=511.430002450943|sample=224|cap=2|top=1.515124213233086|G=0|hpMul=1|bossHpMul=1|modScale=1|curve=1100.5028232569207|pivot=326|weak=set|arch=null|entries=41", 0, 14, "lv=1651|hp=117|pf=1461|ap=1475|ph=224|mh=0"),
            new GoldenCase("L200-fam-s98765-solo-nocache-in", 200, 7, FixedFamily, 98765, 1, false, false, false,
                "4451ca99c5f5514625e99cde", "null",
                "median=511.430002450943|sample=224|cap=2|top=1.515124213233086|G=0|hpMul=1|bossHpMul=1|modScale=1|curve=1100.5028232569207|pivot=326|weak=set|arch=set|entries=41", 0, 0, "lv=18118|hp=127|pf=1461|ap=21929|ph=224|mh=0"),
            new GoldenCase("L400-any-s1234-solo-cache-in", 400, 8, "any", 1234, 1, true, false, false,
                "1166eaf4d6dd445c3239c9ff", "null",
                "median=1338.3719844818115|sample=120|cap=2|top=1.5969656325239687|G=0|hpMul=1|bossHpMul=1.5|modScale=1|curve=18661.213702691013|pivot=326|weak=set|arch=set|entries=41", 5, 17, "lv=1661|hp=39|pf=1461|ap=1461|ph=120|mh=0"),
            new GoldenCase("L400-any-s1234-g3-cache-in", 400, 8, "any", 1234, 3, true, false, false,
                "c168868fa0b93e7a6d8fc328", "7c37bb41aa330be47126e692",
                "median=1338.3719844818115|sample=120|cap=2|top=1.5969656325239687|G=30|hpMul=1.5|bossHpMul=4.5|modScale=1|curve=18661.213702691013|pivot=326|weak=set|arch=set|entries=81", 5, 17, "lv=1661|hp=39|pf=1461|ap=1464|ph=121|mh=0"),
            new GoldenCase("L400-any-s1234-g3-nocache-in", 400, 8, "any", 1234, 3, false, false, false,
                "c168868fa0b93e7a6d8fc328", "7c37bb41aa330be47126e692",
                "median=1338.3719844818115|sample=120|cap=2|top=1.5969656325239687|G=30|hpMul=1.5|bossHpMul=4.5|modScale=1|curve=18661.213702691013|pivot=326|weak=set|arch=set|entries=81", 0, 0, "lv=18038|hp=41|pf=1461|ap=19014|ph=141|mh=0"),
            new GoldenCase("L400-fam-s98765-g3-cache-in", 400, 8, FixedFamily, 98765, 3, true, false, false,
                "4160a16411501f654002370e", "3a471439cb9edf9c77f7d43b",
                "median=1338.3719844818115|sample=120|cap=2|top=1.5969656325239687|G=30|hpMul=1.5|bossHpMul=3|modScale=1|curve=18661.213702691013|pivot=326|weak=set|arch=set|entries=81", 5, 17, "lv=1661|hp=26|pf=1461|ap=1464|ph=121|mh=0"),
            new GoldenCase("L500-any-s98765-solo-cache-in", 500, 8, "any", 98765, 1, true, false, false,
                "fefa6b76ca6af131e99e0b89", "null",
                "median=1721.376064300537|sample=120|cap=2|top=3.659402829163247|G=0|hpMul=1|bossHpMul=1.5|modScale=1|curve=76844.75830100212|pivot=326|weak=set|arch=set|entries=41", 4, 17, "lv=1661|hp=26|pf=1461|ap=1461|ph=120|mh=0"),
            new GoldenCase("L500-any-s98765-g3-nocache-in", 500, 8, "any", 98765, 3, false, false, false,
                "96f4f450f97f6f2ba8fb3fad", "1f83cdf0dda8b893c08dd55d",
                "median=1721.376064300537|sample=120|cap=2|top=3.659402829163247|G=30|hpMul=1.5|bossHpMul=4.5|modScale=1|curve=76844.75830100212|pivot=326|weak=set|arch=set|entries=81", 0, 0, "lv=16949|hp=26|pf=1461|ap=19000|ph=127|mh=0"),
            new GoldenCase("L500-fam-s1234-g3-cache-in", 500, 8, FixedFamily, 1234, 3, true, false, false,
                "1997e04e8d82ea102909daad", "0c9ee5104f7bb6c824287fcc",
                "median=1721.376064300537|sample=120|cap=2|top=3.659402829163247|G=30|hpMul=1.5|bossHpMul=3|modScale=1|curve=76844.75830100212|pivot=326|weak=set|arch=set|entries=81", 5, 17, "lv=1661|hp=26|pf=1461|ap=1464|ph=121|mh=0"),
            new GoldenCase("L400-any-s1234-solo-cache-out", 400, 8, "any", 1234, 1, true, true, true,
                "a6abb9b6c21a93a0d53c02b1", "null",
                "median=1338.3719844818115|sample=120|cap=2|top=1.5969656325239687|G=0|hpMul=1|bossHpMul=1|modScale=1|curve=18661.213702691013|pivot=326|weak=set|arch=set|entries=51", 3, 17, "lv=1661|hp=26|pf=1461|ap=1461|ph=120|mh=64"),
            new GoldenCase("L200-any-s98765-g3-nocache-out", 200, 7, "any", 98765, 3, false, true, true,
                "af29bf532109e0898d27748a", "554ee29f12d0027b06e879c7",
                "median=511.430002450943|sample=224|cap=2|top=1.515124213233086|G=30|hpMul=1.5|bossHpMul=3|modScale=1|curve=1100.5028232569207|pivot=326|weak=set|arch=set|entries=101", 0, 0, "lv=19443|hp=127|pf=1461|ap=20481|ph=237|mh=112"),
            new GoldenCase("L500-any-s1234-g3-cache-out", 500, 8, "any", 1234, 3, true, true, true,
                "c8bcf27b56817c8f216ab7df", "494367a8f5648e28fbf6afbb",
                "median=1721.376064300537|sample=120|cap=2|top=3.659402829163247|G=30|hpMul=1.5|bossHpMul=3|modScale=1|curve=76844.75830100212|pivot=326|weak=set|arch=set|entries=101", 3, 17, "lv=1661|hp=26|pf=1461|ap=1462|ph=127|mh=109"),
        };

        // ---- tests ----------------------------------------------------------------------------------------------

        private const string Spawner = "Source/ACE.Server/ThreadDungeons/ThreadDungeonSpawner.cs";
        private const string BuildLivePlanSignature = "internal static DungeonSpawnPlan BuildLivePlan(";

        /// <summary>
        /// The fit the goldens were captured under: no predicate and no key. Every indoor row ran with the fit filter off and
        /// the outdoor site has no clearance row, so master's ResolveFitPredicate returned (null, null) on every row.
        /// </summary>
        internal static (Func<uint, bool> Fits, string Key) GoldenFitOf(DungeonEntryDef dungeon) => (null, null);

        /// <summary>
        /// The fixture readers behind <see cref="FixtureCalls"/>, in the shape BuildLivePlan takes. Ignores the spell strip, as
        /// the capture did. <paramref name="fitOf"/> defaults to <see cref="GoldenFitOf"/>.
        /// </summary>
        internal static ThreadDungeonSpawner.LivePlanSources FixtureSources(FixtureCalls calls, ThreadDungeonStore store,
            Func<DungeonEntryDef, (Func<uint, bool> Fits, string Key)> fitOf = null)
        {
            var archetypeProfileOf = calls.ArchetypeProfileOf;
            var perHitOf = calls.PerHitOf;

            return new ThreadDungeonSpawner.LivePlanSources(calls.LevelOf, calls.HealthOf, calls.ProfileOf, _ => archetypeProfileOf, _ => perHitOf,
                calls.MovementHeightOf, store, ShippedRosterFixture.Tables, fitOf ?? GoldenFitOf);
        }

        /// <summary>
        /// The extraction is behaviour-preserving: BuildLivePlan, fed the capture's fixtures, reproduces every column master
        /// produced for every row. Every mismatch is collected before failing, so a regression names all the rows it moves.
        /// </summary>
        [TestMethod]
        public void BuildLivePlan_matches_master_for_every_golden_case()
        {
            TestGameTables.EnsureInitialized();
            Assert.IsNotNull(ShippedRosterFixture.Tables, "species tables not found");

            var store = ThreadDungeonStoreTests.LoadShipped();
            var seeded = new List<SeededWeenie>();
            var failures = new List<string>();

            void Check<T>(GoldenCase c, string column, T expected, T actual)
            {
                if (!EqualityComparer<T>.Default.Equals(expected, actual))
                    failures.Add($"{c.CaseId}: {column} expected <{expected}> actual <{actual}>");
            }

            try
            {
                SeedWeenieCache(store, seeded);

                foreach (var c in Golden)
                {
                    try
                    {
                        SeedProperties(c);
                        ThreadPlanCache.Invalidate();
                        AssertPlaceholdersResolve(seeded, c.CaseId);

                        var missesBefore = WorldDatabaseWithEntityCache.WeenieCacheMissTotal;
                        var calls = new FixtureCalls();
                        var dungeon = DungeonFor(c, store);

                        var labelCalls = 0;
                        var plan = ThreadDungeonSpawner.BuildLivePlan(SpecFor(c, dungeon), dungeon, GroupFor(c), FixtureSources(calls, store),
                            () => { labelCalls++; return c.CaseId; }, out var stats);

                        Assert.AreEqual(missesBefore, WorldDatabaseWithEntityCache.WeenieCacheMissTotal, $"{c.CaseId}: a world-cache miss means a database read");
                        Assert.IsNotNull(plan, $"{c.CaseId}: no plan");
                        Assert.IsTrue(plan.Entries.Count > 0, $"{c.CaseId}: positive control, the plan populated");
                        Assert.AreEqual(stats.Limits.WeakPoint && stats.Limits.WeakPointArmorCut > 0, stats.WeakPointOn, $"{c.CaseId}: WeakPointOn");
                        Assert.IsTrue(stats.ElapsedTotalMs >= 0 && stats.ElapsedMs >= 0, $"{c.CaseId}: negative elapsed time");

                        Check(c, "plan", c.PlanHash, ShippedRosterFixture.Hash(plan));
                        Check(c, "abilities", c.AbilitiesFingerprint, AbilitiesHash(plan.CreatureAbilities));
                        Check(c, "extras", c.PlanExtras, PlanExtras(plan));
                        Check(c, "memo hits", c.MemoHits, stats.MemoHits);
                        Check(c, "memo lookups", c.MemoLookups, stats.MemoLookups);
                        Check(c, "inner delegate calls", c.InnerCalls, calls.ToString());
                        Check(c, "log label calls (every golden row has a median, so no warning)", 0, labelCalls);
                    }
                    finally
                    {
                        ThreadPlanCache.Invalidate();
                        RestoreProperties();
                    }
                }
            }
            finally
            {
                UnseedWeenieCache(seeded);
            }

            Assert.AreEqual(0, failures.Count, $"{failures.Count} golden mismatch(es):\n" + string.Join("\n", failures));
        }

        /// <summary>
        /// The seeding the goldens rely on: an entry another test class left in the world cache (a real weenie, or a null
        /// cached from a database miss) is overwritten by the placeholder while the rows run, and put back exactly
        /// afterwards; a wcid that was absent is absent again.
        /// </summary>
        [TestMethod]
        public void SeedWeenieCache_overwrites_prior_entries_and_restores_them_exactly()
        {
            var store = ThreadDungeonStoreTests.LoadShipped();
            var cache = WeenieCache();
            var wcids = RosterWcids(store);
            Assert.IsTrue(wcids.Count >= 3, "roster too small for this test");

            uint withWeenie = wcids[0], withNull = wcids[1], absent = wcids[2];
            var before = wcids.Take(3).ToDictionary(w => w, w => (Had: cache.TryGetValue(w, out var v), Value: v));
            var sentinel = new Weenie { WeenieClassId = withWeenie, ClassName = "goldenplan-sentinel", WeenieType = WeenieType.Creature };
            var seeded = new List<SeededWeenie>();

            try
            {
                cache[withWeenie] = sentinel;
                cache[withNull] = null;
                cache.TryRemove(absent, out _);

                try
                {
                    SeedWeenieCache(store, seeded);
                    AssertPlaceholdersResolve(seeded, "seeded");
                    Assert.AreEqual(wcids.Count, seeded.Count, "every roster wcid is recorded");
                }
                finally
                {
                    UnseedWeenieCache(seeded);
                }

                Assert.IsTrue(cache.TryGetValue(withWeenie, out var w1) && ReferenceEquals(w1, sentinel), "a prior weenie must be restored");
                Assert.IsTrue(cache.TryGetValue(withNull, out var w2) && w2 == null, "a prior cached null must be restored");
                Assert.IsFalse(cache.ContainsKey(absent), "a wcid absent before seeding must be absent after");
            }
            finally
            {
                foreach (var (wcid, prior) in before)
                {
                    if (prior.Had)
                        cache[wcid] = prior.Value;
                    else
                        cache.TryRemove(wcid, out _);
                }
            }
        }

        /// <summary>
        /// BuildLivePlan builds with the fit predicate its sources supply, and hands that predicate's key to the plan memo.
        /// A golden row is built once with the golden fit (no predicate) to find a non-boss wcid the plan places; it is then
        /// rebuilt with a FitOf that rejects exactly that wcid. The wcid must be gone from the plan, and the memo must have
        /// stored a fit projection, which ThreadPlanMemo.Project does only under a non-null fit key (a null key computes and
        /// discards). A control build with the same predicate and a NULL key stores none, so the count is the key's doing.
        /// </summary>
        [TestMethod]
        public void BuildLivePlan_uses_the_sources_fit_predicate()
        {
            TestGameTables.EnsureInitialized();
            Assert.IsNotNull(ShippedRosterFixture.Tables, "species tables not found");

            var store = ThreadDungeonStoreTests.LoadShipped();
            var c = Golden.Single(g => g.CaseId == "L400-any-s1234-solo-cache-in");
            Assert.IsTrue(c.PlanCache && !c.Outdoor, "the row must run with the plan cache on, indoors");

            var seeded = new List<SeededWeenie>();

            DungeonSpawnPlan Build(Func<DungeonEntryDef, (Func<uint, bool> Fits, string Key)> fitOf)
            {
                ThreadPlanCache.Invalidate();
                AssertPlaceholdersResolve(seeded, c.CaseId);

                var missesBefore = WorldDatabaseWithEntityCache.WeenieCacheMissTotal;
                var dungeon = DungeonFor(c, store);
                var plan = ThreadDungeonSpawner.BuildLivePlan(SpecFor(c, dungeon), dungeon, GroupFor(c), FixtureSources(new FixtureCalls(), store, fitOf),
                    () => c.CaseId, out _);

                Assert.AreEqual(missesBefore, WorldDatabaseWithEntityCache.WeenieCacheMissTotal, $"{c.CaseId}: a world-cache miss means a database read");
                Assert.IsTrue(plan.Entries.Count > 0, $"{c.CaseId}: positive control, the plan populated");
                return plan;
            }

            try
            {
                SeedWeenieCache(store, seeded);
                SeedProperties(c);

                var baseline = Build(null);
                Assert.AreEqual(c.PlanHash, ShippedRosterFixture.Hash(baseline), "baseline build must match the golden row");
                Assert.AreEqual(0, ThreadPlanCache.Counts.Projections, "no fit key, no stored projection");

                var rejected = baseline.Entries.Where(e => e.Role != DungeonRole.Boss).Select(e => e.Wcid).Where(w => w != baseline.BossWcid).First();
                Func<uint, bool> fits = w => w != rejected;

                var keyed = Build(_ => (fits, "golden-fit-test"));
                Assert.IsFalse(keyed.Entries.Any(e => e.Wcid == rejected), $"wcid {rejected} was rejected by the sources' fit predicate but is in the plan");
                Assert.AreNotEqual(rejected, keyed.BossWcid, $"wcid {rejected} was rejected but is the boss");
                Assert.IsTrue(ThreadPlanCache.Counts.Projections > 0, "the sources' fit key did not reach ThreadPlanCache.ForRun (no projection stored)");

                var unkeyed = Build(_ => (fits, null));
                Assert.IsFalse(unkeyed.Entries.Any(e => e.Wcid == rejected), $"control: wcid {rejected} is in the plan");
                Assert.AreEqual(0, ThreadPlanCache.Counts.Projections, "control: a null fit key must store no projection");
            }
            finally
            {
                ThreadPlanCache.Invalidate();
                RestoreProperties();
                UnseedWeenieCache(seeded);
            }
        }

        /// <summary>
        /// The log label is lazy: TryPopulate's label is ThreadDungeonRun.ToString, which takes the run's lock, and before the
        /// extraction it was formatted only when the zero-median warning fired. A build whose per-hit read returns nothing
        /// (median 0, ceiling on) fires the warning and invokes the label exactly once; the golden rows, which all have a
        /// median, invoke it zero times (asserted in <see cref="BuildLivePlan_matches_master_for_every_golden_case"/>).
        /// </summary>
        [TestMethod]
        public void BuildLivePlan_invokes_the_log_label_only_for_the_zero_median_warning()
        {
            TestGameTables.EnsureInitialized();
            Assert.IsNotNull(ShippedRosterFixture.Tables, "species tables not found");

            var store = ThreadDungeonStoreTests.LoadShipped();
            var c = Golden.Single(g => g.CaseId == "L400-any-s1234-solo-cache-in");
            var seeded = new List<SeededWeenie>();

            try
            {
                SeedWeenieCache(store, seeded);
                SeedProperties(c);
                ThreadPlanCache.Invalidate();
                AssertPlaceholdersResolve(seeded, c.CaseId);

                var dungeon = DungeonFor(c, store);
                var sources = FixtureSources(new FixtureCalls(), store) with { PerHitOf = _ => _ => CreaturePerHit.Empty };
                var labelCalls = 0;

                var plan = ThreadDungeonSpawner.BuildLivePlan(SpecFor(c, dungeon), dungeon, GroupFor(c), sources,
                    () => { labelCalls++; return c.CaseId; }, out var stats);

                Assert.IsTrue(stats.Limits.DamageCeiling, "the damage ceiling must be on for the warning to fire");
                Assert.IsFalse(plan.PerHitMedian > 0, "positive control: an empty per-hit read must leave the median at 0");
                Assert.AreEqual(1, labelCalls, "the label must be formatted exactly once, by the zero-median warning");
            }
            finally
            {
                ThreadPlanCache.Invalidate();
                RestoreProperties();
                UnseedWeenieCache(seeded);
            }
        }

        /// <summary>
        /// The fit predicate and its key are the one input no golden row covers: every row runs with fitKey null, because a
        /// live predicate closes over LiveMovementHeightOf (a world read). Behaviour is covered by
        /// <see cref="BuildLivePlan_uses_the_sources_fit_predicate"/>; this pins the slots: the pair comes from
        /// sources.FitOf, the predicate goes to DungeonPopulationBuilder.Build in its fitsDungeon slot, and the key to
        /// ThreadPlanCache.ForRun in its fitKey slot.
        /// </summary>
        [TestMethod]
        public void BuildLivePlan_passes_the_fit_predicate_to_Build_and_its_key_to_ForRun()
        {
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(Spawner), BuildLivePlanSignature);

            StringAssert.Contains(body, "var (fitsDungeon, fitKey) = sources.FitOf(dungeon);");
            Assert.IsFalse(body.Contains("ResolveFitPredicate("), "BuildLivePlan must take its fit predicate from its sources, not resolve one itself");

            StringAssert.Matches(body, new Regex(@"ThreadPlanCache\.ForRun\(\s*PropertyManager\.GetBool\(""dynamic_dungeons_plan_cache"", ThreadPlanCache\.DefaultPlanCacheEnabled\)\.Item,\s*fitKey,"),
                "BuildLivePlan must pass fitKey to ThreadPlanCache.ForRun as its second argument");

            StringAssert.Matches(body, new Regex(@"DungeonPopulationBuilder\.Build\([^;]*?,\s*profileOf,\s*fitsDungeon,\s*memo,"),
                "BuildLivePlan must pass fitsDungeon to DungeonPopulationBuilder.Build in its fitsDungeon slot (after profileOf, before memo)");
        }

        /// <summary>
        /// The goldens drive BuildLivePlan with fixture sources, so they cannot see what the live path hands it. Pinned here:
        /// <see cref="ThreadDungeonSpawner.LivePlanSources.Live"/> maps each member to the live reader it replaced, and reads
        /// both stores at call time (a property getter building a fresh instance, not a cached field), the thread store only
        /// once so its Store member and its fit filter are the same snapshot.
        /// TryPopulate's call through BuildLivePlan with Live is Task 3's wiring pin.
        /// </summary>
        [TestMethod]
        public void LivePlanSources_Live_maps_each_member_to_its_live_reader()
        {
            var source = PooledLootSourceText.Read(Spawner);

            var live = PooledLootSourceText.MethodBody(source, "public static LivePlanSources Live");

            StringAssert.Matches(live, new Regex(@"var store = ThreadDungeonManager\.Store;\s*" +
                @"return new LivePlanSources\(\s*" +
                @"LiveLevelOf, LiveHealthOf, LiveProfileOf,\s*" +
                @"strip => wcid => LiveArchetypeProfileOf\(wcid, strip\), strip => wcid => LivePerHitOf\(wcid, strip\),\s*" +
                @"LiveMovementHeightOf, store, WorldEventHooks\.Store\.SpeciesTables,\s*" +
                @"dungeon => \(ResolveFitPredicate\(store, dungeon, out var key\), key\)\);"),
                "LivePlanSources.Live must map each member to the live reader it replaced, read at call time");

            // One read of the live store: the Store member and the fit filter must be the same snapshot.
            Assert.AreEqual(1, Regex.Matches(live, @"ThreadDungeonManager\.Store").Count, "Live must read ThreadDungeonManager.Store exactly once");
        }

        private const string TryPopulateSignature = "public static void TryPopulate(ThreadDungeonRun run, Landblock landblock)";

        /// <summary>
        /// The goldens drive BuildLivePlan with fixture sources, so they cannot see which sources TryPopulate hands it or
        /// whether it still builds a plan some other way. Pinned: the one call, with the live sources, the run's own spec,
        /// dungeon and group, a lazy label, and no direct DungeonPopulationBuilder.Build left behind in TryPopulate.
        /// </summary>
        [TestMethod]
        public void TryPopulate_builds_its_plan_through_BuildLivePlan_with_the_live_sources()
        {
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(Spawner), TryPopulateSignature);

            StringAssert.Contains(body, "BuildLivePlan(run.Spec, run.Dungeon, run.Group, LivePlanSources.Live, () => run.ToString(), out var stats)");
            Assert.AreEqual(1, body.Split("BuildLivePlan(").Length - 1, "TryPopulate builds its plan exactly once");
            Assert.IsFalse(body.Contains("DungeonPopulationBuilder.Build("), "TryPopulate must not build a plan outside BuildLivePlan");
        }

        /// <summary>
        /// TryPopulate wraps the call in its own try/catch (an empty run is playable, a stuck one is not). BuildLivePlan must
        /// not swallow anything itself, or a failed build would hand back a half-built plan instead of reaching that catch.
        /// </summary>
        [TestMethod]
        public void BuildLivePlan_does_not_catch()
        {
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(Spawner), BuildLivePlanSignature);

            Assert.IsFalse(body.Contains("catch"), "BuildLivePlan must let a failure reach TryPopulate's catch");
        }

        [TestMethod]
        public void BuildLivePlan_zero_median_warning_names_the_run()
        {
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(Spawner), BuildLivePlanSignature);

            StringAssert.Contains(body, "{logLabel?.Invoke()} damage ceiling: the per-hit median");
        }

        // ---- inputs ---------------------------------------------------------------------------------------------

        internal static GroupScaling GroupFor(GoldenCase c)
            => c.GroupSize <= 1 ? GroupScaling.Solo
                : GroupScaling.Compute(c.GroupSize, GroupScaling.DefaultEffortPerMember, GroupScaling.DefaultCountShare, GroupScaling.DefaultGroupMaxMonsters,
                    GroupScaling.DefaultDamageRatingPerMember, GroupScaling.DefaultDamageRatingCap, GroupScaling.DefaultRewardBonusPerMember,
                    GroupScaling.DefaultRewardBonusCap, 2.7);

        internal static DungeonEntryDef DungeonFor(GoldenCase c, ThreadDungeonStore store)
        {
            if (!c.Outdoor)
                return ShippedRosterFixture.Dungeon(30);

            Assert.IsTrue(store.Dungeons.TryGetValue(OutdoorSiteId, out var site), $"{OutdoorSiteId} is not in the shipped store");
            Assert.IsTrue(site.IsOutdoor, $"{OutdoorSiteId} is not an outdoor site");
            return site;
        }

        internal static DungeonGemSpec SpecFor(GoldenCase c, DungeonEntryDef dungeon)
            => new DungeonGemSpec(dungeon.Id, c.Level, c.Tier, c.Family, c.Seed, Array.Empty<(string, double)>(), 0, 0);

        /// <summary>Copied verbatim from DamageCeilingTests.SyntheticPerHit.</summary>
        internal static CreaturePerHit SyntheticPerHit(uint wcid)
        {
            var level = ShippedRosterFixture.LevelOf(wcid);

            if (level <= 0)
                return CreaturePerHit.Empty;

            var melee = new PerHitSource(10 + wcid % 97 + level, PropertyAttribute.Strength, 100 + wcid % 50, false, isBody: true);
            var missile = wcid % 3 == 0 ? new PerHitSource(5 + wcid % 41 + level / 2.0, PropertyAttribute.Coordination, 150, true) : default;

            return new CreaturePerHit(level, melee, missile);
        }

        /// <summary>Copied verbatim from DamageCeilingTests.SyntheticProfile.</summary>
        internal static DungeonStatProfile SyntheticProfile(uint wcid)
        {
            var level = ShippedRosterFixture.LevelOf(wcid);

            if (level <= 0)
                return null;

            return new DungeonStatProfile(level, ShippedRosterFixture.HealthOf(wcid),
                new Dictionary<Skill, uint> { [Skill.HeavyWeapons] = (uint)(level * 2), [Skill.MeleeDefense] = (uint)(level * 2) },
                (uint)(level + wcid % 13), (uint)(level * 3), 0, 0.0,
                new Dictionary<PropertyAttribute, uint> { [PropertyAttribute.Strength] = (uint)(100 + level / 2), [PropertyAttribute.Endurance] = (uint)(100 + level / 3) });
        }

        internal static float MovementHeightOf(uint wcid) => OutdoorMovementHeight;

        /// <summary>
        /// Call counters around the fixture delegates, one per live reader they stand in for (LiveLevelOf, LiveHealthOf,
        /// LiveProfileOf, LiveArchetypeProfileOf, LivePerHitOf, LiveMovementHeightOf). Hand the wrapped delegates to the
        /// build exactly where the Live* readers go; the counts then record how often the build reached past the memo.
        /// </summary>
        internal sealed class FixtureCalls
        {
            private int level, health, profile, archetype, perHit, height;

            public Func<uint, int> LevelOf => w => { Interlocked.Increment(ref level); return ShippedRosterFixture.LevelOf(w); };
            public Func<uint, uint> HealthOf => w => { Interlocked.Increment(ref health); return ShippedRosterFixture.HealthOf(w); };
            public Func<uint, DungeonStatProfile> ProfileOf => w => { Interlocked.Increment(ref profile); return SyntheticProfile(w); };
            public Func<uint, DungeonStatProfile> ArchetypeProfileOf => w => { Interlocked.Increment(ref archetype); return SyntheticProfile(w); };
            public Func<uint, CreaturePerHit> PerHitOf => w => { Interlocked.Increment(ref perHit); return SyntheticPerHit(w); };
            public Func<uint, float> MovementHeightOf => w => { Interlocked.Increment(ref height); return OutdoorMovementHeight; };

            public override string ToString()
                => string.Create(CultureInfo.InvariantCulture, $"lv={level}|hp={health}|pf={profile}|ap={archetype}|ph={perHit}|mh={height}");
        }

        // ---- process-static state -------------------------------------------------------------------------------

        internal static void SeedProperties(GoldenCase c)
        {
            DefaultPropertyManager.LoadDefaultProperties();
            Assert.IsTrue(PropertyManager.ModifyBool("dynamic_dungeons_plan_cache", c.PlanCache), "dynamic_dungeons_plan_cache not registered");
            Assert.IsTrue(PropertyManager.ModifyBool("dynamic_dungeons_fit_filter", c.FitFilter), "dynamic_dungeons_fit_filter not registered");
        }

        internal static void RestoreProperties() => DefaultPropertyManager.LoadDefaultProperties();

        private static ConcurrentDictionary<uint, Weenie> WeenieCache()
        {
            var field = typeof(WorldDatabaseWithEntityCache).GetField("weenieCache", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(field, "WorldDatabaseWithEntityCache.weenieCache was not found by reflection - has it been renamed?");
            return (ConcurrentDictionary<uint, Weenie>)field.GetValue(DatabaseManager.World);
        }

        /// <summary>One wcid's world-cache entry as it was before <see cref="SeedWeenieCache"/> overwrote it.</summary>
        internal readonly record struct SeededWeenie(uint Wcid, bool Had, Weenie Prior, Weenie Placeholder);

        /// <summary>Every species member and boss wcid: the reads a build can make through the world cache.</summary>
        internal static List<uint> RosterWcids(ThreadDungeonStore store)
            => ShippedRosterFixture.Tables.Values.SelectMany(t => t.Members ?? Enumerable.Empty<SpeciesMemberDef>()).Select(m => m.Wcid)
                .Concat(store.Bosses.Select(b => b.Wcid))
                .Distinct()
                .OrderBy(w => w)
                .ToList();

        /// <summary>
        /// Puts a bare creature weenie (no spell book, no PropertyString 9015) in the world cache for every roster wcid, so no
        /// read during a build can fall through to the database. It OVERWRITES with the indexer: an entry an earlier test
        /// class cached, or a null cached from a database miss, must not silently win over the placeholder the goldens were
        /// captured with. Each wcid's prior state (present with its value, or absent) is appended to the caller-owned
        /// <paramref name="seeded"/> BEFORE the write, so a throw partway leaves the caller's finally able to restore
        /// exactly what was touched (<see cref="UnseedWeenieCache"/>).
        /// </summary>
        internal static void SeedWeenieCache(ThreadDungeonStore store, List<SeededWeenie> seeded)
        {
            var cache = WeenieCache();

            foreach (var wcid in RosterWcids(store))
            {
                var had = cache.TryGetValue(wcid, out var prior);
                var placeholder = new Weenie { WeenieClassId = wcid, ClassName = $"goldenplan{wcid}", WeenieType = WeenieType.Creature };

                seeded.Add(new SeededWeenie(wcid, had, prior, placeholder));
                cache[wcid] = placeholder;
            }
        }

        /// <summary>Puts back exactly what <see cref="SeedWeenieCache"/> found: the prior entry (null included), or no entry.</summary>
        internal static void UnseedWeenieCache(List<SeededWeenie> seeded)
        {
            if (seeded == null || seeded.Count == 0)
                return;

            var cache = WeenieCache();

            for (var i = seeded.Count - 1; i >= 0; i--)
            {
                var s = seeded[i];

                if (s.Had)
                    cache[s.Wcid] = s.Prior;
                else
                    cache.TryRemove(s.Wcid, out _);
            }

            seeded.Clear();
        }

        /// <summary>Every seeded wcid resolves, through the same call the build makes, to the placeholder seeded for it.</summary>
        internal static void AssertPlaceholdersResolve(List<SeededWeenie> seeded, string caseId)
        {
            Assert.IsTrue(seeded.Count > 0, "nothing was seeded");

            foreach (var s in seeded)
                Assert.AreSame(s.Placeholder, DatabaseManager.World.GetCachedWeenie(s.Wcid), $"{caseId}: wcid {s.Wcid} does not resolve to the seeded placeholder");
        }

        // ---- fingerprints ---------------------------------------------------------------------------------------

        private static string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);

        private static string Hash12(string text)
        {
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
            return string.Concat(bytes.Take(12).Select(b => b.ToString("x2")));
        }

        /// <summary>The plan fields ShippedRosterFixture.Fingerprint leaves out: the damage ceiling, the group and the scales.</summary>
        internal static string PlanExtras(DungeonSpawnPlan plan)
            => string.Concat(
                "median=", R(plan.PerHitMedian),
                "|sample=", plan.PerHitSample.ToString(CultureInfo.InvariantCulture),
                "|cap=", R(plan.DamageCeilingCap),
                "|top=", R(plan.DamageCeilingTop),
                "|G=", plan.GroupDamageRatingBonus.ToString(CultureInfo.InvariantCulture),
                "|hpMul=", R(plan.HealthMultiplier),
                "|bossHpMul=", R(plan.BossHealthMultiplier),
                "|modScale=", R(plan.ModifierLevelScale),
                "|curve=", R(plan.HealthCurveTarget),
                "|pivot=", plan.ReachUpPivot.ToString(CultureInfo.InvariantCulture),
                "|weak=", plan.WeakPoint == null ? "null" : "set",
                "|arch=", plan.ArchetypeUplift == null ? "null" : "set",
                "|entries=", plan.Entries.Count.ToString(CultureInfo.InvariantCulture));

        /// <summary>A canonical text form of a FamilyAbilityPlan: ordered fields, invariant culture, "null" for null.</summary>
        internal static string AbilitiesFingerprint(FamilyAbilityPlan a)
        {
            if (a == null)
                return "null";

            var sb = new StringBuilder();
            sb.Append("fam=").Append(a.FamilyKey ?? "null");
            sb.Append("|budget=").Append(a.Budget.ToString(CultureInfo.InvariantCulture));
            sb.Append("|gearUnknown=").Append(a.GearUnknownCount.ToString(CultureInfo.InvariantCulture));
            sb.Append("|spec=").Append(a.Result.Spec ?? "null");
            sb.Append("|kinds=").Append(string.Join(",", a.Result.Kinds.Select(k => k.ToString())));

            var f = a.Facts;
            if (f == null)
                sb.Append("|facts=null");
            else
                sb.Append("|facts=").Append(f.Melee ? 1 : 0).Append(f.Missile ? 1 : 0).Append(f.Caster ? 1 : 0).Append(f.Hybrid ? 1 : 0)
                  .Append(":tier").Append(f.BandSpellTier.ToString(CultureInfo.InvariantCulture))
                  .Append(":rec").Append(f.AuthoredRecordCount.ToString(CultureInfo.InvariantCulture))
                  .Append(":auth").Append(f.AuthoredEffectKinds == null ? "null" : string.Join(",", f.AuthoredEffectKinds));

            var s = a.BudgetSetterMember;
            sb.Append("|setter=").Append(s == null ? "null" : $"{s.Wcid}:{(int)s.Role}");

            foreach (var m in a.Members)
            {
                sb.Append("|m").Append(m.Wcid).Append(':').Append((int)m.Role)
                  .Append(":r").Append(m.OwnRating.ToString(CultureInfo.InvariantCulture))
                  .Append(":mp").Append(R(m.MeleePerHit)).Append(":xp").Append(R(m.MissilePerHit))
                  .Append(":wm").Append(m.WithheldMelee.ToString(CultureInfo.InvariantCulture)).Append(":wx").Append(m.WithheldMissile.ToString(CultureInfo.InvariantCulture))
                  .Append(":c").Append(m.Casts ? 1 : 0).Append(":cs").Append(R(m.CastShare)).Append(":ms").Append(R(m.MissileShare))
                  .Append(":g").Append(m.GearUnknown ? 1 : 0);
            }

            return sb.ToString();
        }

        /// <summary><see cref="AbilitiesFingerprint"/> hashed to 24 hex characters, or "null" when the plan is null.</summary>
        internal static string AbilitiesHash(FamilyAbilityPlan a) => a == null ? "null" : Hash12(AbilitiesFingerprint(a));
    }
}
