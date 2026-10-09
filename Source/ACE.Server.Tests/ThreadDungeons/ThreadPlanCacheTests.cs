using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Managers;
using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;
using ACE.Server.WorldEvents.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using static ACE.Server.Tests.ThreadDungeons.ThreadLootTestFixtures;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The population-plan cache (<see cref="ThreadPlanCache"/>).
    ///
    /// The ONE thing every test here exists to protect is that the cache is a cost saving and nothing else: a
    /// plan built from a warm cache must equal, member for member, the plan the same seed and inputs produce
    /// with no cache at all. Everything below is either that equality under some condition, or a proof that a
    /// changed input is not served the old answer.
    ///
    /// NO PropertyManager KEY IS READ by anything driven here. ThreadPlanCache.ForRun takes the kill switch as
    /// an argument by design (see its doc comment), so the tunable's registration is checked against
    /// DefaultPropertyManager's compiled table rather than through a live read - that read throws in this
    /// harness and would also make the class order-dependent on whoever seeded the key first.
    ///
    /// The cache is process-wide static state, so every test drops it in TestInitialize. Nothing else in the
    /// suite touches it: every other Build call site passes no memo.
    /// </summary>
    [TestClass]
    public class ThreadPlanCacheTests
    {
        [TestInitialize]
        public void Reset()
        {
            // The band standard's EffectiveDefenseMedians axis resolves a SkillFormula off GameTables, which
            // throws until the synthetic portal tables are injected. Without this the profiles below would have
            // to carry no attributes, and that axis - one of the cached standard's dictionaries - would never
            // be built, so the determinism assertions would silently be comparing a thinner object.
            TestGameTables.EnsureInitialized();

            // Both halves matter. Invalidate clears the maps; the ForRun(true, ...) normalises the kill
            // switch's last-seen value, so a test that left it false cannot make the NEXT test's first ForRun
            // invalidate underneath it.
            ThreadPlanCache.Invalidate();
            ThreadPlanCache.ForRun(true, null, Levels, HealthOf, ProfileOf);
            ThreadPlanCache.Invalidate();
        }

        // ---- fixtures ------------------------------------------------------------------------------------
        // Five families, a band that widens, a curated any-family boss row and profiles with real data, so a
        // single Build exercises all five structural memos rather than short-circuiting past them.

        /// <summary>
        /// Levels chosen so a level-200 gem's NATURAL band [200, 230] fields exactly ONE family ("deep") and the
        /// eligibility ladder has to widen to x0.85 - band [170, 230] - to reach the shipped threshold of four.
        /// That makes the band low floor, the eligibility threshold and the roster band edges all discriminating
        /// inputs here rather than inert ones, which is what the keying tests below need in order to prove
        /// anything at all.
        /// </summary>
        private static readonly Dictionary<uint, int> LevelTable = new Dictionary<uint, int>
        {
            // "wide": entirely below the natural band, in reach only from x0.85 down.
            [900] = 170, [901] = 175, [902] = 172, [903] = 168, [904] = 165, [905] = 162, [906] = 160,
            [910] = 160,
            // "deep": the only family inside the natural band.
            [930] = 201, [931] = 202, [932] = 203, [933] = 204, [934] = 206, [935] = 207, [940] = 215,
            // The three single-member families, each reachable at a different rung.
            [920] = 170, [921] = 175, [922] = 180,
            [10981] = 110,
        };

        /// <summary>The live level read, against a MUTABLE table so a test can simulate an edited weenie.</summary>
        private static int Levels(uint wcid) => LevelTable.TryGetValue(wcid, out var level) ? level : 0;

        private static uint HealthOf(uint wcid) => (uint)(Levels(wcid) * 10);

        /// <summary>
        /// A profile carrying every axis the band standard reads - skills, body-part damage and armour, a spell
        /// tier, a health rate AND attributes - so the cached DungeonBandStandard is a full one and the
        /// determinism assertions are comparing all of it rather than a stub.
        /// </summary>
        private static DungeonStatProfile ProfileOf(uint wcid)
        {
            var level = Levels(wcid);

            return new DungeonStatProfile(level, HealthOf(wcid),
                new Dictionary<Skill, uint> { [Skill.MeleeDefense] = (uint)(level * 2), [Skill.MagicDefense] = (uint)(level + 10) },
                (uint)level, (uint)(level * 3), 3, 1.5,
                new Dictionary<PropertyAttribute, uint>
                {
                    [PropertyAttribute.Strength] = (uint)(level * 3),
                    [PropertyAttribute.Coordination] = (uint)(level * 2),
                    [PropertyAttribute.Quickness] = (uint)(level * 2),
                    [PropertyAttribute.Self] = (uint)(level + 5),
                });
        }

        private static SpeciesMemberDef Trash(uint w) => new SpeciesMemberDef { Wcid = w, Role = 0 };

        /// <summary>A FRESH dictionary each call - callers that want one stable roster reference hold onto it.</summary>
        private static Dictionary<string, SpeciesTableDef> Species()
        {
            var tables = new Dictionary<string, SpeciesTableDef>
            {
                ["wide"] = new SpeciesTableDef
                {
                    Id = "wide",
                    Members = new List<SpeciesMemberDef>
                    {
                        Trash(900), Trash(901), Trash(902), Trash(903), Trash(904), Trash(905), Trash(906),
                        new SpeciesMemberDef { Wcid = 910, Role = 1 },
                    }
                },
                ["deep"] = new SpeciesTableDef
                {
                    Id = "deep",
                    Members = new List<SpeciesMemberDef>
                    {
                        Trash(930), Trash(931), Trash(932), Trash(933), Trash(934), Trash(935),
                        new SpeciesMemberDef { Wcid = 940, Role = 1 },
                    }
                },
            };

            foreach (var (id, wcid) in new[] { ("f2", 920u), ("f3", 921u), ("f4", 922u) })
                tables[id] = new SpeciesTableDef { Id = id, Members = new List<SpeciesMemberDef> { Trash(wcid) } };

            return tables;
        }

        private static DungeonSpawnPointDef Pt(int i) => new DungeonSpawnPointDef { Cell = 0x01500100u + (uint)i, X = i, Y = 0, Z = 0, Clearance = 2, Curated = true };

        private static DungeonEntryDef Dungeon(int points = 8) => new DungeonEntryDef
        {
            Id = "filos_doom", Landblock = 0x0150, Name = "Filos' Doom", ExitPortalWcid = 1003601,
            Points = Enumerable.Range(1, points).Select(Pt).ToList(),
            BossAnchor = Pt(99),
        };

        private static ThreadDungeonStore Store() => ThreadDungeonStore.Parse(
            "{\"dungeons\":[]}",
            "{\"bosses\":[{\"wcid\":10981,\"name\":\"Aun Tanua\",\"level\":110,\"families\":[],\"modifiers\":[\"boss_guarded\"]}]}",
            "{\"modifiers\":[{\"id\":\"boss_guarded\",\"target\":\"boss\",\"minMagnitude\":1.5,\"maxMagnitude\":3.0,\"monsterEffectKind\":\"health_mult\"}]," +
            "\"xpLadder\":[{\"level\":20,\"xp\":3500},{\"level\":100,\"xp\":80000},{\"level\":200,\"xp\":1100000}]}",
            new Dictionary<string, string>());

        private static DungeonGemSpec Spec(string family = "any", int level = 200)
            => new DungeonGemSpec("filos_doom", level, 6, family, 1, new (string, double)[0], 0, 0);

        private static DungeonPopulationLimits Limits(double bandLowFloorRatio = 0.6,
            int minEligibleFamilies = DungeonPopulationLimits.DefaultMinEligibleFamilies,
            double rosterBandLow = DungeonPopulationLimits.DefaultRosterBandLow)
            => new DungeonPopulationLimits(120, 2.0, 3.0, 8, 0.5,
                rewardScalingEnabled: false,
                rosterBandLow: rosterBandLow,
                bandLowFloorRatio: bandLowFloorRatio,
                minEligibleFamilies: minEligibleFamilies,
                minMonsters: 0);

        /// <summary>
        /// A predicate with a REAL effect on the roster: it empties the whole "wide" family, dropping the
        /// eligible count at x0.85 from five families to four. Nothing here asserts a physical truth - the fit
        /// filter's own arithmetic is DungeonFitFilterTests' subject - it just has to change the answer.
        /// </summary>
        private static Func<uint, bool> Fits() => wcid => wcid < 900 || wcid > 910;

        private const string FitKey = "filos_doom|2.95|0";

        private static DungeonSpawnPlan Plan(IReadOnlyDictionary<string, SpeciesTableDef> species, DungeonEntryDef dungeon,
            DungeonPopulationLimits limits, ThreadPlanMemo memo, Func<uint, bool> fits = null, string family = "any", int seed = 7)
        {
            Func<uint, int> levelOf = Levels;
            Func<uint, uint> healthOf = HealthOf;
            Func<uint, DungeonStatProfile> profileOf = ProfileOf;

            if (memo != null)
            {
                levelOf = memo.LevelOf;
                healthOf = memo.HealthOf;
                profileOf = memo.ProfileOf;
            }

            return DungeonPopulationBuilder.Build(Spec(family), dungeon, Store(), species, levelOf, healthOf,
                limits, new Random(seed), profileOf, fits, memo);
        }

        private static ThreadPlanMemo Memo(string fitKey = null)
            => ThreadPlanCache.ForRun(true, fitKey, Levels, HealthOf, ProfileOf);

        /// <summary>Everything about a plan a cache bug could plausibly move, in one comparable string.</summary>
        private static string Shape(DungeonSpawnPlan plan)
            => string.Join("|", plan.Entries.Select(e => $"{e.Wcid}:{e.Role}:{e.Point?.X}:{e.UpliftLevel}"))
               + $"||family={plan.FamilyId} boss={plan.BossWcid} bossLevel={plan.BossLevel} naturalLow={plan.NaturalBandLow} "
               + $"effectiveLow={plan.EffectiveBandLow} trashFloor={plan.TrashHealthFloor} normalizeRatio={plan.HealthNormalizeRatio:R} "
               + $"poolMax={plan.PoolMaxBase} bossBase={plan.BossHealthBase:R} bossBand={plan.BossHealthBandTerm:R} bossPack={plan.BossHealthPackTerm:R} "
               + $"standardSample={plan.BandStandard.SampleCount} standardLow={plan.BandStandard.SampleLowRatio:R} "
               + $"standardDmg={plan.BandStandard.MaxBodyDamage} standardArmor={plan.BandStandard.MaxBaseArmor} "
               + $"standardSkills={string.Join(",", plan.BandStandard.SkillMedians.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}"))} "
               + $"standardAttrs={string.Join(",", plan.BandStandard.AttributeMedians.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}"))} "
               + $"standardDefense={string.Join(",", plan.BandStandard.EffectiveDefenseMedians.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}"))} "
               + $"standardRate={plan.BandStandard.HealthRate:R} standardTier={plan.BandStandard.SpellTier} "
               + $"notes={string.Join(";", plan.Notes)}";

        // ---- determinism ---------------------------------------------------------------------------------

        /// <summary>
        /// THE contract. Three builds of the same spec against the same inputs: one with no cache at all, one
        /// filling a cold cache, one served from the warm cache. All three plans must be the same plan.
        /// </summary>
        [TestMethod]
        public void A_warm_cache_reproduces_the_cold_plan_and_the_no_cache_plan_member_for_member()
        {
            var species = Species();
            var dungeon = Dungeon();

            var uncached = Shape(Plan(species, dungeon, Limits(), memo: null));

            var cold = Memo();
            var coldShape = Shape(Plan(species, dungeon, Limits(), cold));

            var warm = Memo();
            var warmShape = Shape(Plan(species, dungeon, Limits(), warm));

            Assert.AreEqual(uncached, coldShape, "a cold cache must not change the plan");
            Assert.AreEqual(uncached, warmShape, "a warm cache must not change the plan");

            Assert.AreEqual(0, cold.Hits, "nothing to hit on the first build");
            Assert.IsTrue(cold.Lookups >= 3, $"the build should take at least the three no-fit-filter structural lookups, took {cold.Lookups}");
            Assert.IsTrue(warm.AllCached, $"the second build should hit every structural lookup, hit {warm.Hits} of {warm.Lookups}");
            Assert.AreEqual(cold.Lookups, warm.Lookups, "the same build takes the same lookups warm or cold");
        }

        /// <summary>The same equality with the fit filter on, which adds the projection and its two band reads.</summary>
        [TestMethod]
        public void A_warm_cache_reproduces_the_plan_with_the_fit_filter_on_too()
        {
            var species = Species();
            var dungeon = Dungeon();

            var uncached = Shape(Plan(species, dungeon, Limits(), memo: null, fits: Fits()));

            var cold = Memo(FitKey);
            var coldShape = Shape(Plan(species, dungeon, Limits(), cold, Fits()));

            var warm = Memo(FitKey);
            var warmShape = Shape(Plan(species, dungeon, Limits(), warm, Fits()));

            Assert.AreEqual(uncached, coldShape);
            Assert.AreEqual(uncached, warmShape);
            Assert.IsTrue(warm.AllCached, $"hit {warm.Hits} of {warm.Lookups}");
            Assert.IsTrue(warm.Lookups > cold.Lookups - 1 && warm.Lookups >= 4,
                $"the fit filter adds the projection and its eligibility reads; took {warm.Lookups}");
            Assert.AreEqual(1, ThreadPlanCache.Counts.Projections, "one projection, shared by both builds");
        }

        /// <summary>Warm or cold, a DIFFERENT seed still produces that seed's own plan and not the first one's.</summary>
        [TestMethod]
        public void The_cache_never_pins_a_plan_to_one_seed()
        {
            var species = Species();
            var dungeon = Dungeon();

            var uncachedSeven = Shape(Plan(species, dungeon, Limits(), memo: null, seed: 7));
            var uncachedEleven = Shape(Plan(species, dungeon, Limits(), memo: null, seed: 11));

            Assert.AreNotEqual(uncachedSeven, uncachedEleven, "the fixture must actually be seed-sensitive, or this test proves nothing");

            Plan(species, dungeon, Limits(), Memo(), seed: 7);

            Assert.AreEqual(uncachedEleven, Shape(Plan(species, dungeon, Limits(), Memo(), seed: 11)));
            Assert.AreEqual(uncachedSeven, Shape(Plan(species, dungeon, Limits(), Memo(), seed: 7)));
        }

        // ---- keying: a changed input is never served the old answer --------------------------------------

        [TestMethod]
        public void A_changed_roster_band_dial_keys_separately()
        {
            var species = Species();
            var dungeon = Dungeon();

            var wide = Limits(rosterBandLow: 0.75);
            var uncachedWide = Shape(Plan(species, dungeon, wide, memo: null));

            Assert.AreNotEqual(Shape(Plan(species, dungeon, Limits(), memo: null)), uncachedWide,
                "the two band settings must actually differ, or this test proves nothing");

            // Warm the cache at the DEFAULT band first, then ask for the wide one.
            Plan(species, dungeon, Limits(), Memo());

            var second = Memo();
            Assert.AreEqual(uncachedWide, Shape(Plan(species, dungeon, wide, second)));
            Assert.AreEqual(0, second.Hits, "a different band is a different key, so nothing may hit");
        }

        [TestMethod]
        public void A_changed_band_low_floor_keys_separately()
        {
            var species = Species();
            var dungeon = Dungeon();

            var pinned = Limits(bandLowFloorRatio: 1.0);
            var uncachedPinned = Shape(Plan(species, dungeon, pinned, memo: null));

            Assert.AreNotEqual(Shape(Plan(species, dungeon, Limits(), memo: null)), uncachedPinned,
                "the two floors must actually differ, or this test proves nothing");

            Plan(species, dungeon, Limits(), Memo());

            var second = Memo();
            Assert.AreEqual(uncachedPinned, Shape(Plan(species, dungeon, pinned, second)));

            // NOT "nothing hits". The band MEDIAN has no floor term - BandMedianHealth does not take one - so
            // its entry is legitimately shared across floors and hits here. What must miss is everything the
            // floor really feeds: the eligibility band and the band standard.
            Assert.IsFalse(second.AllCached, $"the floor-dependent lookups must miss; hit {second.Hits} of {second.Lookups}");
            Assert.IsTrue(second.Hits < second.Lookups);
        }

        [TestMethod]
        public void A_changed_min_eligible_families_threshold_keys_separately()
        {
            var species = Species();
            var dungeon = Dungeon();

            // One family is enough: the ladder never runs and the natural band stands.
            var relaxed = Limits(minEligibleFamilies: 1);
            var uncachedRelaxed = Shape(Plan(species, dungeon, relaxed, memo: null));

            Assert.AreNotEqual(Shape(Plan(species, dungeon, Limits(), memo: null)), uncachedRelaxed,
                "the two thresholds must actually differ, or this test proves nothing");

            Plan(species, dungeon, Limits(), Memo());

            var second = Memo();
            Assert.AreEqual(uncachedRelaxed, Shape(Plan(species, dungeon, relaxed, second)));
            Assert.IsFalse(second.AllCached, $"the threshold-dependent lookups must miss; hit {second.Hits} of {second.Lookups}");
        }

        [TestMethod]
        public void A_changed_gem_level_keys_separately()
        {
            var species = Species();
            var dungeon = Dungeon();

            Func<uint, int> levelOf = Levels;
            var memo = Memo();

            var atTwoHundred = DungeonPopulationBuilder.Build(Spec(level: 200), dungeon, Store(), species, memo.LevelOf,
                memo.HealthOf, Limits(), new Random(7), memo.ProfileOf, null, memo);

            var memo2 = Memo();
            var atOneEighty = DungeonPopulationBuilder.Build(Spec(level: 180), dungeon, Store(), species, memo2.LevelOf,
                memo2.HealthOf, Limits(), new Random(7), memo2.ProfileOf, null, memo2);

            var uncached180 = DungeonPopulationBuilder.Build(Spec(level: 180), dungeon, Store(), species, levelOf,
                HealthOf, Limits(), new Random(7), ProfileOf, null, null);

            Assert.AreNotEqual(Shape(atTwoHundred), Shape(atOneEighty), "the two levels must actually differ");
            Assert.AreEqual(Shape(uncached180), Shape(atOneEighty));
        }

        /// <summary>
        /// A different fit key (here: the same dungeon after an admin edits the headroom margin) must build its
        /// own projection rather than reuse the one keyed on the old margin.
        /// </summary>
        [TestMethod]
        public void A_changed_fit_key_builds_its_own_projection()
        {
            var species = Species();
            var dungeon = Dungeon();

            // Fits() empties "wide"; this also empties "f4", which drops the count at x0.85 below the
            // threshold and makes the ladder run further. Two predicates, two different rosters.
            Func<uint, bool> strict = wcid => (wcid < 900 || wcid > 910) && wcid != 922;

            var uncachedStrict = Shape(Plan(species, dungeon, Limits(), memo: null, fits: strict));

            Assert.AreNotEqual(Shape(Plan(species, dungeon, Limits(), memo: null, fits: Fits())), uncachedStrict,
                "the two predicates must actually differ, or this test proves nothing");

            Plan(species, dungeon, Limits(), Memo(FitKey), Fits());

            Assert.AreEqual(uncachedStrict, Shape(Plan(species, dungeon, Limits(), Memo("filos_doom|2.95|0.5"), strict)));
            Assert.AreEqual(2, ThreadPlanCache.Counts.Projections, "one projection per fit key");
        }

        /// <summary>
        /// A predicate with NO stable identity (null fit key) is never stored, because nothing could key it.
        /// The plan must still be correct; it just costs what it always cost.
        /// </summary>
        [TestMethod]
        public void A_fit_predicate_with_no_key_is_computed_and_never_stored()
        {
            var species = Species();
            var dungeon = Dungeon();

            var uncached = Shape(Plan(species, dungeon, Limits(), memo: null, fits: Fits()));

            Assert.AreEqual(uncached, Shape(Plan(species, dungeon, Limits(), Memo(fitKey: null), Fits())));
            Assert.AreEqual(uncached, Shape(Plan(species, dungeon, Limits(), Memo(fitKey: null), Fits())));
            Assert.AreEqual(0, ThreadPlanCache.Counts.Projections, "an unkeyable projection must not be stored");
        }

        // ---- invalidation --------------------------------------------------------------------------------

        /// <summary>
        /// The content-reload contract, in both directions. An edited weenie is served from cache until
        /// something invalidates - that is the cache working, and it is what makes the second half meaningful -
        /// and after Invalidate the plan matches a fresh uncached build against the edited data.
        /// </summary>
        [TestMethod]
        public void Invalidate_drops_every_entry_so_edited_weenie_data_is_re_read()
        {
            var species = Species();
            var dungeon = Dungeon();
            var originalF3 = LevelTable[921];
            var originalF4 = LevelTable[922];

            try
            {
                Plan(species, dungeon, Limits(), Memo());

                // An admin raises two single-member families' creatures clean out of the band's reach and
                // clears the weenie cache. The eligibility ladder can no longer field four families at x0.85,
                // so it runs on to the floor and the run draws from a different band.
                LevelTable[921] = 400;
                LevelTable[922] = 400;

                var uncachedAfterEdit = Shape(Plan(species, dungeon, Limits(), memo: null));
                var stale = Shape(Plan(species, dungeon, Limits(), Memo()));

                Assert.AreNotEqual(uncachedAfterEdit, stale,
                    "the cache must really be serving the pre-edit reads, or the invalidation half of this test proves nothing");

                ThreadPlanCache.Invalidate();

                Assert.AreEqual(uncachedAfterEdit, Shape(Plan(species, dungeon, Limits(), Memo())),
                    "after Invalidate the plan must be built from the edited data");
            }
            finally
            {
                LevelTable[921] = originalF3;
                LevelTable[922] = originalF4;
                ThreadPlanCache.Invalidate();
            }
        }

        /// <summary>
        /// A species-table RELOAD needs no explicit invalidation to be correct: the store publishes by swapping
        /// the dictionary, and every band key holds the old dictionary by reference, so the new roster cannot
        /// match an old entry. (The reload paths call Invalidate anyway, to drop the dead entries.)
        /// </summary>
        [TestMethod]
        public void A_reloaded_species_dictionary_is_a_different_key_on_its_own()
        {
            var dungeon = Dungeon();

            var before = Species();
            Plan(before, dungeon, Limits(), Memo());

            // The reloaded roster: two families deleted from content, so the eligibility ladder runs further.
            var after = Species();
            after.Remove("f3");
            after.Remove("f4");

            var uncachedAfter = Shape(Plan(after, dungeon, Limits(), memo: null));

            Assert.AreNotEqual(Shape(Plan(before, dungeon, Limits(), memo: null)), uncachedAfter,
                "the two rosters must actually differ, or this test proves nothing");

            var memo = Memo();
            Assert.AreEqual(uncachedAfter, Shape(Plan(after, dungeon, Limits(), memo)));
            Assert.AreEqual(0, memo.Hits, "a swapped roster reference can hit nothing");
        }

        /// <summary>
        /// SOURCE PIN. Every weenie-cache clear in ACE.Server must sit next to a ThreadPlanCache.Invalidate().
        ///
        /// WHY THIS IS PINNED ON SOURCE RATHER THAN EXERCISED. The hook cannot live where it belongs.
        /// ACE.Database.WorldDatabaseWithEntityCache is what actually clears the weenie cache, and ACE.Database
        /// does not reference ACE.Server, so the invalidation has to be repeated at each ACE.Server call site
        /// instead of sitting once inside the clear itself. Three sites carry it today. A FOURTH added without
        /// it would leave the population-plan cache serving pre-edit weenie levels, healths and stat profiles
        /// for the rest of the process - and silently, because every plan it produced would still be internally
        /// consistent and every test would still pass. Nothing else in the suite can catch that: it needs a live
        /// world database and an admin command to reproduce.
        ///
        /// The scan is deliberately loud about finding NOTHING (see the floor assertion): a renamed method or a
        /// changed call spelling would otherwise turn this pin into a test that passes by examining zero sites.
        /// </summary>
        [TestMethod]
        public void Every_weenie_cache_clear_in_ACE_Server_sits_next_to_a_plan_cache_invalidate()
        {
            var serverRoot = Path.Combine(RepoRoot(), "Source", "ACE.Server");
            var sites = 0;
            var offenders = new List<string>();

            foreach (var path in Directory.EnumerateFiles(serverRoot, "*.cs", SearchOption.AllDirectories))
            {
                // Generated and build output, never hand-written call sites.
                if (path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    || path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    continue;

                var lines = File.ReadAllLines(path);

                for (var i = 0; i < lines.Length; i++)
                {
                    var trimmed = lines[i].TrimStart();

                    // A doc comment or a prose note that merely NAMES the method is not a call site.
                    if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("*", StringComparison.Ordinal))
                        continue;

                    // The call forms, qualified, so the method's own declaration and any bare mention are skipped.
                    if (!trimmed.Contains(".ClearWeenieCache()", StringComparison.Ordinal)
                        && !trimmed.Contains(".ClearCachedWeenie(", StringComparison.Ordinal))
                        continue;

                    sites++;

                    // "Adjacent" is the next 15 source lines: enough room for the explanatory comment each of
                    // the three existing hooks carries, and far too little to reach an unrelated method.
                    var window = string.Join("\n", lines.Skip(i + 1).Take(15));

                    if (window.Contains("ThreadPlanCache.Invalidate()", StringComparison.Ordinal))
                        continue;

                    offenders.Add(
                        $"{Path.GetRelativePath(RepoRoot(), path).Replace('\\', '/')}:{i + 1}: {trimmed}");
                }
            }

            Assert.IsTrue(offenders.Count == 0,
                "A weenie-cache clear in ACE.Server is not followed by ThreadPlanCache.Invalidate() within 15 lines.\n"
                + "ThreadPlanCache memoises live weenie reads (level, health, stat profile, movement height) for the\n"
                + "Threads population plan, so clearing the weenie cache without clearing it leaves every later run\n"
                + "planned from pre-edit data, silently and for the rest of the process.\n"
                + "FIX: add this line immediately after each call listed below, with a comment pointing at\n"
                + "DeveloperContentCommands.HandleClearCache for why the hook lives on the ACE.Server side:\n"
                + "    ThreadPlanCache.Invalidate();\n"
                + "(and 'using ACE.Server.ThreadDungeons;' if the file does not already have it)\n"
                + "Offending call site(s):\n  " + string.Join("\n  ", offenders));

            // The floor. If a rename or a respelling makes the scan match nothing, this pin must fail rather
            // than report a clean sweep of an empty set.
            Assert.IsTrue(sites >= 3,
                $"the weenie-cache-clear scan found only {sites} call site(s) under {serverRoot}, and there were 3 when "
                + "this pin was written (DeveloperContentCommands: HandleClearCache, ImportJsonWeenie, ImportSQLWeenie). "
                + "Either the method was renamed or the call spelling changed - fix the patterns above, do not lower "
                + "this floor, because a scan that matches nothing passes without examining anything.");
        }

        /// <summary>
        /// The repo root, found by walking up from the test output directory for a file only the repo has - the
        /// same shape PooledLootSourceText.Read uses, and for the same reason: an --artifacts-path run moves the
        /// assembly out of the tree, and this must then fail loudly rather than quietly scan nothing.
        /// </summary>
        private static string RepoRoot()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Source", "ACE.Server", "ThreadDungeons", "ThreadPlanCache.cs")))
                    return dir.FullName;
            }

            Assert.Fail($"Could not find the repo root by walking up from {AppContext.BaseDirectory}. "
                + "Run the tests in-tree; --artifacts-path breaks every source pin in this suite.");
            return null;
        }

        /// <summary>Turning the switch off and back on drops whatever the cache held while nobody was reading it.</summary>
        [TestMethod]
        public void Turning_the_cache_back_on_drops_what_it_held_while_it_was_off()
        {
            var species = Species();
            var dungeon = Dungeon();

            Plan(species, dungeon, Limits(), Memo());
            Assert.IsTrue(ThreadPlanCache.Counts.BandStandards > 0, "the warm-up must have stored something");

            Assert.IsNull(ThreadPlanCache.ForRun(false, null, Levels, HealthOf, ProfileOf), "the kill switch yields no memo");
            Assert.IsTrue(ThreadPlanCache.Counts.BandStandards > 0, "switching off does not itself clear");

            var backOn = ThreadPlanCache.ForRun(true, null, Levels, HealthOf, ProfileOf);

            Assert.IsNotNull(backOn);
            Assert.AreEqual(0, ThreadPlanCache.Counts.BandStandards, "coming back on drops the generation that went unread");
            Assert.AreEqual(0, ThreadPlanCache.Counts.Levels);
        }

        // ---- kill switch ---------------------------------------------------------------------------------

        [TestMethod]
        public void The_kill_switch_leaves_the_cache_untouched_and_the_plan_unchanged()
        {
            var species = Species();
            var dungeon = Dungeon();

            var uncached = Shape(Plan(species, dungeon, Limits(), memo: null));

            var memo = ThreadPlanCache.ForRun(false, FitKey, Levels, HealthOf, ProfileOf);

            Assert.IsNull(memo, "switched off, there is no memo and the builder takes its pre-cache path");
            Assert.AreEqual(uncached, Shape(Plan(species, dungeon, Limits(), memo)));

            var counts = ThreadPlanCache.Counts;

            Assert.AreEqual(0, counts.Levels + counts.Healths + counts.Profiles + counts.Projections
                + counts.EligibilityBands + counts.FamilyCounts + counts.BandMedians + counts.BandStandards,
                $"nothing may be read or written while the switch is off; got {counts}");
        }

        [TestMethod]
        public void The_switch_is_registered_and_ships_on()
        {
            Assert.IsTrue(DefaultPropertyManager.DefaultBooleanProperties.ContainsKey("dynamic_dungeons_plan_cache"),
                "dynamic_dungeons_plan_cache must be registered or no admin can turn the cache off");

            Assert.IsTrue(DefaultPropertyManager.DefaultBooleanProperties["dynamic_dungeons_plan_cache"].Item,
                "a new server setting ships ON (fork convention)");

            Assert.AreEqual(ThreadPlanCache.DefaultPlanCacheEnabled,
                DefaultPropertyManager.DefaultBooleanProperties["dynamic_dungeons_plan_cache"].Item,
                "the registration and the compiled default must not drift");
        }

        // ---- bounds --------------------------------------------------------------------------------------

        /// <summary>
        /// Each map stops growing at its cap, and past the cap it keeps returning the RIGHT value - it just
        /// recomputes it. Driven through the band-median memo, which is the cheapest of the five to call
        /// directly and shares its BoundedMap implementation with every other map.
        /// </summary>
        [TestMethod]
        public void A_band_map_stops_at_its_cap_and_still_answers_correctly()
        {
            var species = Species();
            var memo = Memo();
            var overflow = ThreadPlanCache.MaxBandEntries + 40;

            for (var level = 1; level <= overflow; level++)
            {
                var band = DungeonRosterBand.Default;
                var expected = DungeonRosterSelector.BandMedianHealth(species, level, Levels, HealthOf, band);

                Assert.AreEqual(expected, memo.BandMedianHealth(species, level, Levels, HealthOf, band),
                    $"level {level} must answer correctly whether or not it fitted in the cache");
            }

            Assert.AreEqual(ThreadPlanCache.MaxBandEntries, ThreadPlanCache.Counts.BandMedians,
                "the map must stop exactly at its cap and never evict");

            // And the entries that DID fit are still served from cache.
            var warm = Memo();
            warm.BandMedianHealth(species, 1, Levels, HealthOf, DungeonRosterBand.Default);

            Assert.AreEqual(1, warm.Hits, "an entry stored before the cap was reached still hits");
        }

        [TestMethod]
        public void The_per_wcid_memos_return_exactly_what_the_inner_delegate_returned()
        {
            var memo = Memo();

            foreach (var wcid in LevelTable.Keys)
            {
                Assert.AreEqual(Levels(wcid), memo.LevelOf(wcid));
                Assert.AreEqual(HealthOf(wcid), memo.HealthOf(wcid));
                Assert.AreEqual(ProfileOf(wcid).Level, memo.ProfileOf(wcid).Level);
                Assert.AreEqual(ProfileOf(wcid).MaxBodyDamage, memo.ProfileOf(wcid).MaxBodyDamage);
            }

            // Second pass, now entirely from cache, must agree with the first.
            foreach (var wcid in LevelTable.Keys)
            {
                Assert.AreEqual(Levels(wcid), memo.LevelOf(wcid));
                Assert.AreEqual(HealthOf(wcid), memo.HealthOf(wcid));
            }

            Assert.AreEqual(LevelTable.Count, ThreadPlanCache.Counts.Levels);
            Assert.AreEqual(0, memo.Lookups, "per-wcid reads are not structural lookups and must not move the counters");

            // A wcid the table does not carry reads 0 and is memoised as 0, exactly as the inner delegate said.
            Assert.AreEqual(0, memo.LevelOf(4242u));
            Assert.AreEqual(0u, memo.HealthOf(4242u));
        }

        [TestMethod]
        public void The_bounds_are_ordered_so_the_expensive_map_is_the_tightest()
        {
            Assert.IsTrue(ThreadPlanCache.MaxProfileEntries < ThreadPlanCache.MaxScalarWcidEntries,
                "a stat profile costs far more per entry than a level or a health, so its cap must be the tighter one");

            Assert.IsTrue(ThreadPlanCache.MaxProjectionEntries < ThreadPlanCache.MaxBandEntries,
                "a projection is a copy of the whole roster's shape; there are 13 curated dungeons, not hundreds");
        }

        // ---- telemetry -----------------------------------------------------------------------------------

        [TestMethod]
        public void The_run_summary_line_carries_planCached_and_the_memo_split()
        {
            var cold = PooledRun();
            cold.Perf.RecordPlan(12, 180, 0, 6);
            Assert.IsTrue(cold.MarkEnded("test"));

            var coldLine = ThreadDungeonManager.RunSummaryLine(cold);

            StringAssert.Contains(coldLine, "planMs=12");
            StringAssert.Contains(coldLine, "planCached=False");
            StringAssert.Contains(coldLine, "planMemo=0/6");

            var warm = PooledRun();
            warm.Perf.RecordPlan(2, 180, 6, 6);
            Assert.IsTrue(warm.MarkEnded("test"));

            var warmLine = ThreadDungeonManager.RunSummaryLine(warm);

            StringAssert.Contains(warmLine, "planCached=True");
            StringAssert.Contains(warmLine, "planMemo=6/6");
        }

        [TestMethod]
        public void PlanCached_is_false_when_the_cache_is_off_and_when_any_lookup_missed()
        {
            var perf = new ThreadRunPerfStats();

            perf.RecordPlan(9, 180);
            Assert.IsFalse(perf.PlanCached, "no lookups at all (the switch is off) is not 'cached'");
            Assert.AreEqual(0, perf.PlanMemoLookups);

            perf.RecordPlan(9, 180, 5, 6);
            Assert.IsFalse(perf.PlanCached, "one miss is enough to make a build not fully warm");

            perf.RecordPlan(9, 180, 6, 6);
            Assert.IsTrue(perf.PlanCached);

            perf.RecordPlan(9, 180, -3, -3);
            Assert.AreEqual(0, perf.PlanMemoHits, "a negative count is never recorded");
            Assert.AreEqual(0, perf.PlanMemoLookups);
        }
    }
}
