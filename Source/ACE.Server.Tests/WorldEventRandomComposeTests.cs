using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Unit coverage for "/worldevent start random" (WE-S4, TECH-DESIGN 5.3): WorldEventComposer's
    /// TryComposeRandom search over the (source, goal) x family cross product. Everything here is pure
    /// (TECH-DESIGN D6) - no live landblock, no Player/Session.
    /// </summary>
    [TestClass]
    public class WorldEventRandomComposeTests
    {
        // ---- fixture: two sources (one multi-goal, one single-goal), two families, one unbound boss ----

        private const string RandomSources = @"{ ""sources"": [ {
            ""id"": ""element_portal_fire"", ""displayName"": ""Fire Portal"", ""geometry"": ""disc"",
            ""geometryRadius"": 10.0, ""geometryPoints"": 4, ""waveIntervalSeconds"": 40.0,
            ""maxAlive"": 20, ""waveCount"": { ""base"": 3, ""perParticipant"": 1.5, ""cap"": 16 },
            ""rewardRadius"": 60.0,
            ""objectiveHealth"": { ""base"": 8000, ""perParticipant"": 2500, ""cap"": 80000 },
            ""objectives"": [
                { ""wcid"": 1002660, ""dx"": 6.5, ""dy"": 0.0, ""dz"": 0.0, ""yaw"": 0.0 },
                { ""wcid"": 1002660, ""dx"": -6.5, ""dy"": 0.0, ""dz"": 0.0, ""yaw"": 0.0 } ],
            ""compatibleGoals"": [ ""destroy_source"", ""kill_count"", ""kill_boss"" ],
            ""goalDisplayName"": ""Shatter the Pillars"",
            ""objectiveNoun"": ""pillar"", ""objectiveNounPlural"": ""pillars"",
            ""startFlavour"": ""x"", ""waveFlavour"": ""y"" }, {
            ""id"": ""ambush"", ""displayName"": ""Ambush"", ""geometry"": ""edges"",
            ""geometryRadius"": 45.0, ""geometryPoints"": 3, ""waveIntervalSeconds"": 45.0,
            ""maxAlive"": 24, ""waveCount"": { ""base"": 4, ""perParticipant"": 1.5, ""cap"": 18 },
            ""rewardRadius"": 60.0, ""compatibleGoals"": [ ""kill_count"" ],
            ""startFlavour"": ""x"", ""waveFlavour"": ""y"" } ] }";

        private const string RandomFamilies =
            @"{ ""families"": [ { ""id"": ""emberwrought"", ""displayName"": ""the Emberwrought"" }, " +
            @"{ ""id"": ""frostbound"", ""displayName"": ""the Frostbound"" } ] }";

        private const string RandomGoals = @"{ ""goals"": [
            { ""id"": ""kill_count"", ""displayName"": ""Kill Count"", ""type"": ""KillCount"",
              ""count"": { ""base"": 20, ""perParticipant"": 6, ""cap"": 150 }, ""holdSeconds"": 0,
              ""mvpRule"": ""mostKills"", ""progressTemplate"": ""{killed} of {target} slain."" },
            { ""id"": ""destroy_source"", ""displayName"": ""Destroy the Source"", ""type"": ""DestroySource"",
              ""holdSeconds"": 0, ""mvpRule"": ""killingBlowAndTopDamage"",
              ""progressTemplate"": ""{remaining} {nounPlural} remain."" },
            { ""id"": ""kill_boss"", ""displayName"": ""Slay the Champion"", ""type"": ""KillBoss"",
              ""holdSeconds"": 0, ""mvpRule"": ""killingBlowAndTopDamage"",
              ""progressTemplate"": ""The champion still stands."" } ] }";

        private const string RandomRewards = @"{ ""rewards"": [ {
            ""id"": ""standard"", ""displayName"": ""Hammer Crate"",
            ""successCrateWcid"": 1002600, ""consolationCrateWcid"": 1002601, ""cacheWcid"": 1002602,
            ""participantsPerCache"": 8, ""claimWindowSeconds"": 300 } ] }";

        private const string RandomBosses =
            @"{ ""bosses"": [ { ""id"": ""ember_boss"", ""displayName"": ""Ember Boss"", ""wcid"": 1002619 } ] }";

        private static WorldEventAxisStore RandomStore()
        {
            return WorldEventAxisStore.Parse(RandomSources, RandomFamilies, RandomGoals, RandomRewards,
                null, RandomBosses);
        }

        private static Weenie FamilyMemberWeenie(uint wcid, string family, int level = 20, bool caster = false)
        {
            var weenie = new Weenie
            {
                WeenieClassId = wcid,
                ClassName = "testcreature" + wcid,
                WeenieType = WeenieType.Creature,
                PropertiesBool = new Dictionary<PropertyBool, bool>
                {
                    [PropertyBool.WorldEventCreature] = true,
                    [PropertyBool.Attackable] = true
                },
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    [PropertyInt.Level] = level,
                    [PropertyInt.WorldEventRole] = 0
                },
                PropertiesString = new Dictionary<PropertyString, string>
                {
                    [PropertyString.WorldEventFamily] = family,
                    [PropertyString.Name] = "Test Creature " + wcid
                }
            };

            if (caster)
                weenie.PropertiesSpellBook = new Dictionary<int, float> { [157] = 1.0f };

            return weenie;
        }

        private static WorldEventCatalog RandomCatalog()
        {
            var weenies = new List<Weenie>
            {
                FamilyMemberWeenie(1002604, "emberwrought"),
                FamilyMemberWeenie(1002605, "frostbound")
            };

            return WorldEventCatalogBuilder.Build(weenies, out _);
        }

        /// <summary>
        /// Three families with the shapes the pairing rule actually discriminates on (two-family
        /// composition, 2026-08-29): "emberwrought" reaches the floor AND casts, "frostbound" reaches the
        /// floor and does not, "margul" is the level-135 family the whole rule exists because of - it can
        /// only ever be the SECOND half of a pair.
        /// </summary>
        private static WorldEventCatalog PairingCatalog()
        {
            var weenies = new List<Weenie>
            {
                FamilyMemberWeenie(1002604, "emberwrought", level: 20, caster: true),
                FamilyMemberWeenie(1002605, "frostbound", level: 30),
                FamilyMemberWeenie(1002606, "margul", level: 135)
            };

            return WorldEventCatalogBuilder.Build(weenies, out _);
        }

        private static WorldEventAxisStore PairingStore()
        {
            const string families =
                @"{ ""families"": [ { ""id"": ""emberwrought"", ""displayName"": ""the Emberwrought"" }, " +
                @"{ ""id"": ""frostbound"", ""displayName"": ""the Frostbound"" }, " +
                @"{ ""id"": ""margul"", ""displayName"": ""the Margul"" } ] }";

            return WorldEventAxisStore.Parse(RandomSources, families, RandomGoals, RandomRewards, null, RandomBosses);
        }

        private static WorldEventRequest RandomRequest(string sourceId = null, string goalId = null,
            string familyId = null, string bossId = null)
        {
            return new WorldEventRequest
            {
                Random = true,
                SourceId = sourceId,
                GoalId = goalId,
                FamilyId = familyId,
                BossId = bossId,
                AnchorPosition = new Position(0x016C019E, 10f, 10f, 0f, 0f, 0f, 0f, 1f, 0),
                AnchorLabel = "the test anchor",
                Invoker = "test"
            };
        }

        // ---- (a) reproducibility -------------------------------------------------------------------

        [TestMethod]
        public void Random_SeededRngIsReproducible()
        {
            var store = RandomStore();
            var catalog = RandomCatalog();

            Assert.IsTrue(WorldEventComposer.TryCompose(store, catalog, RandomRequest(), out var first, out var e1, new Random(12345)), e1);
            Assert.IsTrue(WorldEventComposer.TryCompose(store, catalog, RandomRequest(), out var second, out var e2, new Random(12345)), e2);

            Assert.AreEqual(first.Source.Id, second.Source.Id);
            Assert.AreEqual(first.Goal.Id, second.Goal.Id);
            Assert.AreEqual(first.Boss.Id, second.Boss.Id);

            // BOTH composed family ids, in slot order - a seed that reproduced only slot A would hide half
            // the draw (two-family composition, 2026-08-29).
            CollectionAssert.AreEqual(
                first.Families.Select(f => f.Id).ToList(),
                second.Families.Select(f => f.Id).ToList());
        }

        // ---- (a2) the family pair --------------------------------------------------------------------

        [TestMethod]
        public void Random_AlwaysDrawsTwoFamilies()
        {
            var store = PairingStore();
            var catalog = PairingCatalog();

            for (var seed = 0; seed < 100; seed++)
            {
                Assert.IsTrue(WorldEventComposer.TryCompose(store, catalog, RandomRequest(), out var composition, out var error, new Random(seed)), error);

                Assert.AreEqual(2, composition.Families.Count, $"seed {seed} composed {composition.Families.Count} families");
                Assert.AreNotEqual(composition.Families[0].Id, composition.Families[1].Id);
            }
        }

        [TestMethod]
        public void Random_FamilyAAlwaysHasAMemberAtOrBelowTheFloor()
        {
            var store = PairingStore();
            var catalog = PairingCatalog();

            var floor = WorldEventFamilyPairing.FloorLevel();

            for (var seed = 0; seed < 100; seed++)
            {
                Assert.IsTrue(WorldEventComposer.TryCompose(store, catalog, RandomRequest(), out var composition, out var error, new Random(seed)), error);

                var slotA = composition.Families[0];

                Assert.IsTrue(slotA.Members.Any(m => m.Level <= floor),
                    $"seed {seed} put '{slotA.Id}' in slot A with nothing at or below level {floor}");

                // margul is the level-135 family: it may ride along as B, never as A.
                Assert.AreNotEqual("margul", slotA.Id, $"seed {seed} put margul in slot A");
            }
        }

        [TestMethod]
        public void Random_AtLeastOneComposedFamilyHasACaster()
        {
            var store = PairingStore();
            var catalog = PairingCatalog();

            for (var seed = 0; seed < 100; seed++)
            {
                Assert.IsTrue(WorldEventComposer.TryCompose(store, catalog, RandomRequest(), out var composition, out var error, new Random(seed)), error);

                Assert.IsTrue(composition.Families.Any(f => f.Members.Any(m => m.Caster)),
                    $"seed {seed} composed {string.Join("+", composition.Families.Select(f => f.Id))} with no caster on either side");
            }
        }

        [TestMethod]
        public void Random_TheRosterIsTheUnionOfBothFamilies()
        {
            var store = PairingStore();
            var catalog = PairingCatalog();

            Assert.IsTrue(WorldEventComposer.TryCompose(store, catalog, RandomRequest(), out var composition, out var error, new Random(7)), error);

            var expected = composition.Families.SelectMany(f => f.Members).Select(m => m.Wcid).OrderBy(w => w).ToList();
            var actual = composition.Roster.Members.Select(m => m.Wcid).OrderBy(w => w).ToList();

            CollectionAssert.AreEqual(expected, actual);
            Assert.AreEqual(string.Join("+", composition.Families.Select(f => f.Id)), composition.Roster.Id);
        }

        [TestMethod]
        public void Random_PinnedSingleFamily_ComposesThatFamilyAlone()
        {
            var store = PairingStore();
            var catalog = PairingCatalog();

            for (var seed = 0; seed < 50; seed++)
            {
                Assert.IsTrue(WorldEventComposer.TryCompose(store, catalog, RandomRequest(familyId: "margul"), out var composition, out var error, new Random(seed)), error);

                // Pinned verbatim: no rolled partner, and no floor check - margul would never have been a
                // legal slot A on its own.
                Assert.AreEqual(1, composition.Families.Count);
                Assert.AreEqual("margul", composition.Family.Id);
                Assert.AreEqual("margul", composition.Roster.Id);
                Assert.IsNull(composition.PairingTier, "a pinned family bypasses the pairing rule, so there is no tier");
            }
        }

        [TestMethod]
        public void Random_PinnedTwoFamilies_ComposesExactlyThose()
        {
            var store = PairingStore();
            var catalog = PairingCatalog();

            var request = RandomRequest();
            request.FamilyIds = new List<string> { "margul", "frostbound" };

            Assert.IsTrue(WorldEventComposer.TryCompose(store, catalog, request, out var composition, out var error, new Random(3)), error);

            CollectionAssert.AreEqual(new[] { "margul", "frostbound" }, composition.Families.Select(f => f.Id).ToList());
            Assert.AreEqual("margul+frostbound", composition.Roster.Id);
            Assert.IsNull(composition.PairingTier);
        }

        [TestMethod]
        public void Random_RolledPairCarriesItsTierAndFloor()
        {
            var store = PairingStore();
            var catalog = PairingCatalog();

            Assert.IsTrue(WorldEventComposer.TryCompose(store, catalog, RandomRequest(), out var composition, out var error, new Random(11)), error);

            Assert.AreEqual(WorldEventFamilyPairing.PairTier.FloorAndCaster, composition.PairingTier);
            Assert.AreEqual(WorldEventFamilyPairing.FloorLevel(), composition.PairingFloorLevel);
        }

        [TestMethod]
        public void Random_TwoFamilyAxisSummaryIsOneTokenInTheSameField()
        {
            var store = PairingStore();
            var catalog = PairingCatalog();

            Assert.IsTrue(WorldEventComposer.TryCompose(store, catalog, RandomRequest(), out var composition, out var error, new Random(5)), error);

            var summary = composition.ToAxisSummary();
            var fields = summary.Split(' ');

            Assert.AreEqual(6, fields.Length, $"the compose line's field count is fixed (TECH-DESIGN 5.2): {summary}");
            Assert.AreEqual($"family={composition.Families[0].Id}+{composition.Families[1].Id}", fields[1]);
        }

        // ---- (b) every unpinned roll stays inside the legal surface --------------------------------

        [TestMethod]
        public void Random_NeverProducesASourceGoalPairOutsideCompatibleGoals()
        {
            var store = RandomStore();
            var catalog = RandomCatalog();

            for (var seed = 0; seed < 200; seed++)
            {
                Assert.IsTrue(WorldEventComposer.TryCompose(store, catalog, RandomRequest(), out var composition, out var error, new Random(seed)), error);

                CollectionAssert.Contains(composition.Source.CompatibleGoals.ToList(), composition.Goal.Id);
            }
        }

        [TestMethod]
        public void Random_KillBossNeverComesBackWithBossKindNone()
        {
            var store = RandomStore();
            var catalog = RandomCatalog();

            for (var seed = 0; seed < 200; seed++)
            {
                Assert.IsTrue(WorldEventComposer.TryCompose(store, catalog, RandomRequest(goalId: "kill_boss"), out var composition, out var error, new Random(seed)), error);

                Assert.AreNotEqual(BossKind.None, composition.Boss.Kind);
            }
        }

        [TestMethod]
        public void Random_PinnedGoalAlwaysYieldsThatGoal()
        {
            var store = RandomStore();
            var catalog = RandomCatalog();

            for (var seed = 0; seed < 50; seed++)
            {
                Assert.IsTrue(WorldEventComposer.TryCompose(store, catalog, RandomRequest(goalId: "kill_boss"), out var composition, out var error, new Random(seed)), error);

                Assert.AreEqual("kill_boss", composition.Goal.Id);
                Assert.AreNotEqual(BossKind.None, composition.Boss.Kind);
            }
        }

        [TestMethod]
        public void Random_PinnedSourceAmbushOnlyEverYieldsItsOwnCompatibleGoal()
        {
            var store = RandomStore();
            var catalog = RandomCatalog();

            var ambushCompatibleGoals = store.Sources["ambush"].CompatibleGoals;

            for (var seed = 0; seed < 50; seed++)
            {
                Assert.IsTrue(WorldEventComposer.TryCompose(store, catalog, RandomRequest(sourceId: "ambush"), out var composition, out var error, new Random(seed)), error);

                Assert.AreEqual("ambush", composition.Source.Id);
                CollectionAssert.Contains(ambushCompatibleGoals.ToList(), composition.Goal.Id);
            }
        }

        [TestMethod]
        public void Random_PinnedFamilyAlwaysYieldsThatFamily()
        {
            var store = RandomStore();
            var catalog = RandomCatalog();

            for (var seed = 0; seed < 50; seed++)
            {
                Assert.IsTrue(WorldEventComposer.TryCompose(store, catalog, RandomRequest(familyId: "frostbound"), out var composition, out var error, new Random(seed)), error);

                Assert.AreEqual("frostbound", composition.Family.Id);
            }
        }

        // ---- (c) refusals stay honest instead of throwing ------------------------------------------

        [TestMethod]
        public void Random_NoFamiliesInCatalog_ReturnsARefusal_NotAnException()
        {
            var store = RandomStore();

            var ok = WorldEventComposer.TryCompose(store, WorldEventCatalog.Empty, RandomRequest(), out var composition, out var error);

            Assert.IsFalse(ok);
            Assert.IsNull(composition);
            Assert.AreEqual("random composition: no family in the catalog has any members", error);
        }

        [TestMethod]
        public void Random_PinnedIncompatiblePair_ReturnsTheOrdinaryRefusal()
        {
            var store = RandomStore();
            var catalog = RandomCatalog();

            var ok = WorldEventComposer.TryCompose(store, catalog,
                RandomRequest(sourceId: "ambush", goalId: "destroy_source"), out var composition, out var error);

            Assert.IsFalse(ok);
            Assert.IsNull(composition);
            Assert.AreEqual("goal 'destroy_source' is not compatible with source 'ambush' (compatible: kill_count)", error);
        }

        // ---- (b2) the attempt budget scales with the content -----------------------------------------

        [TestMethod]
        public void ResolveMaxAttempts_GivesEveryTriedPairAFullCross()
        {
            // The guarantee: whatever the content grows to, the budget is at least
            // (pairs actually tried) x (source, goal combinations), so MaxPairsTried stays the only thing
            // deciding how many pairs get attempted.
            for (var pairsTried = 1; pairsTried <= WorldEventComposer.MaxPairsTried; pairsTried++)
            {
                foreach (var combos in new[] { 1, 21, 26, 40, 120 })
                {
                    var budget = WorldEventComposer.ResolveMaxAttempts(pairsTried, combos);

                    Assert.IsTrue(budget >= pairsTried * combos,
                        $"{pairsTried} pairs x {combos} combos needs {pairsTried * combos} attempts, budget was {budget}");
                }
            }
        }

        [TestMethod]
        public void ResolveMaxAttempts_IsFlooredAndCeilinged()
        {
            // Floor: small content keeps the historic budget rather than shrinking below it.
            Assert.AreEqual(WorldEventComposer.MinRandomAttempts, WorldEventComposer.ResolveMaxAttempts(1, 1));
            Assert.AreEqual(WorldEventComposer.MinRandomAttempts, WorldEventComposer.ResolveMaxAttempts(8, 21));
            Assert.AreEqual(WorldEventComposer.MinRandomAttempts, WorldEventComposer.ResolveMaxAttempts(0, 0));

            // Between the two it is exactly the full cross.
            Assert.AreEqual(8 * 40, WorldEventComposer.ResolveMaxAttempts(8, 40));

            // Ceiling: a pathological store cannot turn one refusal into an unbounded walk. int.MaxValue
            // here also pins that the multiply is done wide enough not to overflow.
            Assert.AreEqual(WorldEventComposer.MaxRandomAttempts, WorldEventComposer.ResolveMaxAttempts(8, 100000));
            Assert.AreEqual(WorldEventComposer.MaxRandomAttempts, WorldEventComposer.ResolveMaxAttempts(8, int.MaxValue));
        }

        /// <summary>
        /// A store with <paramref name="sourceCount"/> sources, exactly ONE of which offers a goal that can
        /// compose against a "--boss none" request; every other source offers only kill_boss, which that
        /// request refuses. That makes the position of the single composable (source, goal) combination in
        /// the shuffled list the thing the attempt budget has to be big enough to reach.
        /// </summary>
        private static WorldEventAxisStore ManySourceStore(int sourceCount)
        {
            var sources = new List<string>();

            for (var i = 0; i < sourceCount; i++)
            {
                // Source 0 is the only one that can compose under "--boss none".
                var goal = i == 0 ? "kill_count" : "kill_boss";

                sources.Add($@"{{ ""id"": ""source_{i:D3}"", ""displayName"": ""Source {i}"", ""geometry"": ""edges"",
                    ""geometryRadius"": 45.0, ""geometryPoints"": 3, ""waveIntervalSeconds"": 45.0,
                    ""maxAlive"": 24, ""waveCount"": {{ ""base"": 4, ""perParticipant"": 1.5, ""cap"": 18 }},
                    ""rewardRadius"": 60.0, ""compatibleGoals"": [ ""{goal}"" ],
                    ""startFlavour"": ""x"", ""waveFlavour"": ""y"" }}");
            }

            var sourcesJson = @"{ ""sources"": [ " + string.Join(", ", sources) + " ] }";

            const string families =
                @"{ ""families"": [ { ""id"": ""low_caster"", ""displayName"": ""the Low Casters"" }, " +
                @"{ ""id"": ""low_plain"", ""displayName"": ""the Low Plain"" }, " +
                @"{ ""id"": ""high"", ""displayName"": ""the High"" } ] }";

            return WorldEventAxisStore.Parse(sourcesJson, families, RandomGoals, RandomRewards, null, RandomBosses);
        }

        /// <summary>
        /// Three families producing exactly three candidate pairs at the FloorAndCaster tier: "low_caster"
        /// reaches the floor and casts, "low_plain" reaches the floor and does not, "high" does neither.
        /// </summary>
        private static WorldEventCatalog ManyPairCatalog()
        {
            var weenies = new List<Weenie>
            {
                FamilyMemberWeenie(1002607, "low_caster", level: 20, caster: true),
                FamilyMemberWeenie(1002608, "low_plain", level: 30),
                FamilyMemberWeenie(1002609, "high", level: 200)
            };

            return WorldEventCatalogBuilder.Build(weenies, out _);
        }

        [TestMethod]
        [TestCategory("Slow")]
        public void Random_ManySourceGoalCombos_StillComposesForEverySeed()
        {
            // 260 (source, goal) combinations, of which exactly one composes, against 3 candidate family
            // pairs. A single family pair's own full cross (260) already exceeds the historic flat cap of
            // 200, so under that cap every seed that shuffled the one composable combination past position
            // 200 came back "no compatible source/family/goal combination was found" - a refusal caused by
            // the budget, not by the content. The budget now scales, so every seed composes.
            const int sourceCount = 260;

            var store = ManySourceStore(sourceCount);
            var catalog = ManyPairCatalog();

            Assert.AreEqual(sourceCount, store.Sources.Count);

            var candidatePairs = WorldEventFamilyPairing.CandidatePairs(catalog, WorldEventFamilyPairing.FloorLevel(), out var tier);

            Assert.AreEqual(WorldEventFamilyPairing.PairTier.FloorAndCaster, tier);
            Assert.IsTrue(candidatePairs.Count > 1, $"this test needs more than one candidate pair, got {candidatePairs.Count}");

            var composedPairs = new HashSet<string>();

            for (var seed = 0; seed < 200; seed++)
            {
                var request = RandomRequest(bossId: "none");

                Assert.IsTrue(WorldEventComposer.TryCompose(store, catalog, request, out var composition, out var error, new Random(seed)),
                    $"seed {seed}: {error}");

                Assert.AreEqual("source_000", composition.Source.Id);
                Assert.AreEqual("kill_count", composition.Goal.Id);

                composedPairs.Add(string.Join("+", composition.Families.Select(f => f.Id)));
            }

            // Every candidate pair is reachable across seeds - none is structurally starved by the search
            // order or the budget.
            var expected = candidatePairs.Select(p => $"{p.a}+{p.b}").OrderBy(id => id, StringComparer.Ordinal).ToList();

            CollectionAssert.AreEquivalent(expected, composedPairs.OrderBy(id => id, StringComparer.Ordinal).ToList());
        }

        // ---- (c2) the composer's own family-list refusals --------------------------------------------

        private static WorldEventRequest ExplicitRequest(params string[] familyIds)
        {
            return new WorldEventRequest
            {
                SourceId = "ambush",
                GoalId = "kill_count",
                FamilyIds = familyIds.ToList(),
                AnchorPosition = new Position(0x016C019E, 10f, 10f, 0f, 0f, 0f, 0f, 1f, 0),
                AnchorLabel = "the test anchor",
                Invoker = "test"
            };
        }

        [TestMethod]
        public void Compose_NoFamilyId_StillSaysMissingFamily()
        {
            var ok = WorldEventComposer.TryCompose(PairingStore(), PairingCatalog(), ExplicitRequest(),
                out var composition, out var error);

            Assert.IsFalse(ok);
            Assert.IsNull(composition);
            Assert.AreEqual("missing --family", error);
        }

        [TestMethod]
        public void Compose_ThreeFamilies_IsRefused()
        {
            var ok = WorldEventComposer.TryCompose(PairingStore(), PairingCatalog(),
                ExplicitRequest("emberwrought", "frostbound", "margul"), out var composition, out var error);

            Assert.IsFalse(ok);
            Assert.IsNull(composition);
            Assert.AreEqual("a run composes at most 2 families, got 3", error);
        }

        [TestMethod]
        public void Compose_DuplicateFamily_IsRefused()
        {
            var ok = WorldEventComposer.TryCompose(PairingStore(), PairingCatalog(),
                ExplicitRequest("emberwrought", "Emberwrought"), out var composition, out var error);

            Assert.IsFalse(ok);
            Assert.IsNull(composition);
            Assert.AreEqual("family 'emberwrought' is listed twice - a run's two families must be different", error);
        }

        [TestMethod]
        public void Compose_TwoExplicitFamilies_AreNeverConstrained()
        {
            // margul first, no caster on either side: illegal for a ROLLED pair, accepted verbatim here.
            var ok = WorldEventComposer.TryCompose(PairingStore(), PairingCatalog(),
                ExplicitRequest("margul", "frostbound"), out var composition, out var error);

            Assert.IsTrue(ok, error);
            CollectionAssert.AreEqual(new[] { "margul", "frostbound" }, composition.Families.Select(f => f.Id).ToList());
            Assert.IsNull(composition.PairingTier);
        }

        [TestMethod]
        public void Random_PinnedUnknownSource_SurfacesTheUnknownSourceRefusal()
        {
            var store = RandomStore();
            var catalog = RandomCatalog();

            var ok = WorldEventComposer.TryCompose(store, catalog, RandomRequest(sourceId: "not_a_source"), out var composition, out var error);

            Assert.IsFalse(ok);
            Assert.IsNull(composition);
            StringAssert.StartsWith(error, "unknown source 'not_a_source'");
        }

        // ---- weighted roll (TECH-DESIGN 5.3): bucket -> source -> goal ----

        private static WorldEventAxisStore RealAxesStore()
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "Content", "events", "axes", "goals.json")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find Content/events/axes/goals.json by walking up from {AppContext.BaseDirectory}");

            return WorldEventAxisStore.Load(System.IO.Path.Combine(dir.FullName, "Content", "events", "axes"));
        }

        private static string BucketOf(string sourceId) => sourceId.StartsWith("element_portal_", StringComparison.Ordinal) ? "portal" : sourceId;

        private const int WeightedRolls = 8000;

        /// <summary>Fixed seed for the one rng a frequency test shares across all its rolls (consecutive seeds would stratify the first draw).</summary>
        private const int FrequencySeed = 20261116;

        /// <summary>Rolls <paramref name="rolls"/> seeded random compositions against the shipped axes with Hold seeded as given.</summary>
        private static List<(string sourceId, string goalId)> RollReal(bool holdEnabled, int rolls, IReadOnlyList<string> allowed = null)
        {
            var store = RealAxesStore();
            var family = store.Families.Keys.OrderBy(k => k, StringComparer.Ordinal).First();
            var catalog = WorldEventHoldComposerGateTests.CatalogFor(family);
            var saved = WorldEventComposer.HoldEnabledReader;
            var results = new List<(string, string)>();
            var rng = new Random(FrequencySeed);

            try
            {
                WorldEventComposer.HoldEnabledReader = _ => holdEnabled;

                for (var i = 0; i < rolls; i++)
                {
                    var request = RandomRequest();
                    request.AllowedSourceIds = allowed;

                    Assert.IsTrue(WorldEventComposer.TryCompose(store, catalog, request, out var composition, out var error, rng),
                        $"roll {i}: {error}");

                    results.Add((composition.Source.Id, composition.Goal.Id));
                }
            }
            finally
            {
                WorldEventComposer.HoldEnabledReader = saved;
            }

            return results;
        }

        private static double Share(List<(string sourceId, string goalId)> rolls, Func<(string sourceId, string goalId), bool> pick) =>
            rolls.Count(pick) / (double)rolls.Count;

        [TestMethod]
        public void Weighted_HoldOn_FourBucketsRollAQuarterEach()
        {
            var rolls = RollReal(holdEnabled: true, WeightedRolls);
            var shares = new[] { "portal", "sky_rift", "weave_spiral", "ambush" }
                .ToDictionary(b => b, b => Share(rolls, r => BucketOf(r.sourceId) == b));

            Console.WriteLine("HoldOn bucket shares: " + string.Join(", ", shares.Select(kv => $"{kv.Key}={kv.Value:P1}")));

            foreach (var kv in shares)
                Assert.AreEqual(0.25, kv.Value, 0.02, $"bucket {kv.Key}");

            Assert.AreEqual(0, rolls.Count(r => r.sourceId == "rift"), "rift has weight 0");
        }

        [TestMethod]
        public void Weighted_HoldOff_ThreeBucketsRollAThirdEach()
        {
            var rolls = RollReal(holdEnabled: false, WeightedRolls);
            var shares = new[] { "portal", "sky_rift", "weave_spiral" }
                .ToDictionary(b => b, b => Share(rolls, r => BucketOf(r.sourceId) == b));

            Console.WriteLine("HoldOff bucket shares: " + string.Join(", ", shares.Select(kv => $"{kv.Key}={kv.Value:P1}")));

            foreach (var kv in shares)
                Assert.AreEqual(1.0 / 3.0, kv.Value, 0.02, $"bucket {kv.Key}");

            Assert.AreEqual(0, rolls.Count(r => r.sourceId == "ambush" || r.sourceId == "rift"));
        }

        [TestMethod]
        public void Weighted_PortalElementIsUniformWithinTheBucket()
        {
            var rolls = RollReal(holdEnabled: false, WeightedRolls).Where(r => BucketOf(r.sourceId) == "portal").ToList();

            foreach (var element in new[] { "fire", "acid", "frost", "lightning", "earth" })
                Assert.AreEqual(0.2, rolls.Count(r => r.sourceId == "element_portal_" + element) / (double)rolls.Count, 0.04, element);
        }

        [TestMethod]
        public void Weighted_KillCountNeverRolled_ButComposesWhenPinned()
        {
            foreach (var hold in new[] { true, false })
            {
                var rolls = RollReal(hold, 1500);

                Assert.AreEqual(0, rolls.Count(r => r.goalId == "kill_count"), $"hold={hold}");
                Assert.IsTrue(rolls.Any(r => r.goalId == "kill_boss"), "control: kill_boss (weight 1) does roll");
            }

            var store = RealAxesStore();
            var catalog = WorldEventHoldComposerGateTests.CatalogFor(store.Families.Keys.OrderBy(k => k, StringComparer.Ordinal).First());

            Assert.IsTrue(WorldEventComposer.TryCompose(store, catalog, RandomRequest(sourceId: "sky_rift", goalId: "kill_count"),
                out var composition, out var error, new Random(1)), error);
            Assert.AreEqual("kill_count", composition.Goal.Id);
        }

        [TestMethod]
        public void Weighted_RiftNeverRolled_ButComposesWhenPinned()
        {
            var rolls = RollReal(holdEnabled: true, 1500);

            Assert.AreEqual(0, rolls.Count(r => r.sourceId == "rift"));
            Assert.IsTrue(rolls.Any(r => r.sourceId == "sky_rift"), "control: a weight-1 source does roll");

            var store = RealAxesStore();
            var catalog = WorldEventHoldComposerGateTests.CatalogFor(store.Families.Keys.OrderBy(k => k, StringComparer.Ordinal).First());

            Assert.IsTrue(WorldEventComposer.TryCompose(store, catalog, RandomRequest(sourceId: "rift"),
                out var composition, out var error, new Random(1)), error);
            Assert.AreEqual("rift", composition.Source.Id);
        }

        [TestMethod]
        public void Weighted_PortalBucketStaysOneShare_WhenTheFilterRemovesSomePortals()
        {
            var allowed = new List<string> { "element_portal_fire", "element_portal_acid", "sky_rift", "weave_spiral", "ambush" };
            var rolls = RollReal(holdEnabled: true, WeightedRolls, allowed);
            var portal = Share(rolls, r => BucketOf(r.sourceId) == "portal");

            Console.WriteLine($"filtered portal share: {portal:P1}");

            Assert.AreEqual(0.25, portal, 0.02);
            Assert.AreEqual(0, rolls.Count(r => r.sourceId == "element_portal_frost" || r.sourceId == "element_portal_earth"));
            Assert.AreEqual(0.125, Share(rolls, r => r.sourceId == "element_portal_fire"), 0.02, "the surviving portals split the bucket");
        }

        [TestMethod]
        public void Weighted_FilterRemovingABucket_RenormalizesTheRest()
        {
            var allowed = new List<string> { "element_portal_fire", "sky_rift" };
            var rolls = RollReal(holdEnabled: true, WeightedRolls, allowed);

            Assert.AreEqual(0.5, Share(rolls, r => r.sourceId == "sky_rift"), 0.03);
            Assert.AreEqual(0.5, Share(rolls, r => r.sourceId == "element_portal_fire"), 0.03);
        }

        [TestMethod]
        public void Weighted_AmbushOnlyEverYieldsDefendEnvoys()
        {
            var ambush = RollReal(holdEnabled: true, WeightedRolls).Where(r => r.sourceId == "ambush").ToList();

            Assert.IsTrue(ambush.Count > 500, $"control: ambush must actually roll ({ambush.Count})");
            Assert.IsTrue(ambush.All(r => r.goalId == "defend_envoys"), string.Join(",", ambush.Select(r => r.goalId).Distinct()));
        }

        // ---- the ordering primitive, on a synthetic store ----

        private static WorldEventAxisStore WeightedFixture(string sourceExtraA = "", string sourceExtraB = "", string goalExtra = "")
        {
            string Src(string id, string extra) => @"{ ""id"": """ + id + @""", ""displayName"": """ + id + @""", ""geometry"": ""disc"",
                ""geometryRadius"": 10.0, ""geometryPoints"": 4, ""waveIntervalSeconds"": 40.0, ""maxAlive"": 20,
                ""waveCount"": { ""base"": 3, ""perParticipant"": 1.5, ""cap"": 16 }, ""rewardRadius"": 60.0," + extra + @"
                ""compatibleGoals"": [ ""kill_count"", ""kill_boss"" ], ""startFlavour"": ""x"", ""waveFlavour"": ""y"" }";

            var sources = @"{ ""randomGroups"": { ""pair"": 2.0 }, ""sources"": [ " + Src("a", sourceExtraA) + ", " + Src("b", sourceExtraB) + " ] }";

            var goals = @"{ ""goals"": [
                { ""id"": ""kill_count"", ""displayName"": ""Kill Count"", " + goalExtra + @" ""type"": ""KillCount"",
                  ""count"": { ""base"": 20, ""perParticipant"": 6, ""cap"": 150 }, ""holdSeconds"": 0,
                  ""mvpRule"": ""mostKills"", ""progressTemplate"": ""x"" },
                { ""id"": ""kill_boss"", ""displayName"": ""Slay the Champion"", ""type"": ""KillBoss"",
                  ""holdSeconds"": 0, ""mvpRule"": ""killingBlowAndTopDamage"", ""progressTemplate"": ""x"" } ] }";

            return WorldEventAxisStore.Parse(sources, RandomFamilies, goals, RandomRewards, null, RandomBosses);
        }

        private static List<(string sourceId, string goalId)> AllPairs(WorldEventAxisStore store) =>
            store.Sources.Keys.OrderBy(k => k, StringComparer.Ordinal)
                .SelectMany(s => store.Goals.Keys.OrderBy(k => k, StringComparer.Ordinal).Select(g => (s, g))).ToList();

        [TestMethod]
        public void WeightedPairOrder_IsAPermutationOfTheEligiblePairs_AndSeedReproducible()
        {
            var store = WeightedFixture();
            var pairs = AllPairs(store);

            var one = WorldEventComposer.WeightedPairOrder(store, pairs, false, false, new Random(9));
            var two = WorldEventComposer.WeightedPairOrder(store, pairs, false, false, new Random(9));

            CollectionAssert.AreEqual(one, two);
            CollectionAssert.AreEquivalent(pairs, one);
        }

        [TestMethod]
        public void WeightedPairOrder_WeightZero_IsDropped_UnlessThatAxisIsPinned()
        {
            var store = WeightedFixture(sourceExtraA: @" ""randomWeight"": 0,", goalExtra: @"""randomWeight"": 0,");
            var pairs = AllPairs(store);

            var rolled = WorldEventComposer.WeightedPairOrder(store, pairs, false, false, new Random(1));
            CollectionAssert.AreEqual(new[] { ("b", "kill_boss") }, rolled);

            // Pinned source ignores source weights; pinned goal ignores goal weights.
            var pinnedSource = WorldEventComposer.WeightedPairOrder(store, pairs.Where(p => p.sourceId == "a").ToList(), true, false, new Random(1));
            CollectionAssert.AreEqual(new[] { ("a", "kill_boss") }, pinnedSource);

            var pinnedGoal = WorldEventComposer.WeightedPairOrder(store, pairs.Where(p => p.goalId == "kill_count").ToList(), false, true, new Random(1));
            CollectionAssert.AreEqual(new[] { ("b", "kill_count") }, pinnedGoal);
        }

        [TestMethod]
        public void WeightedPairOrder_GroupedSourcesSplitTheirBucketByTheirOwnWeights()
        {
            // a (weight 3) and b (weight 1) share group "pair": the bucket is always drawn, then a takes 3/4 of first picks.
            var store = WeightedFixture(sourceExtraA: @" ""randomGroup"": ""pair"", ""randomWeight"": 3.0,", sourceExtraB: @" ""randomGroup"": ""pair"",");
            var pairs = AllPairs(store);
            const int n = 6000;
            var rng = new Random(FrequencySeed);

            var a = Enumerable.Range(0, n).Count(_ => WorldEventComposer.WeightedPairOrder(store, pairs, false, false, rng)[0].sourceId == "a");

            Assert.AreEqual(0.75, a / (double)n, 0.03);
        }

        [TestMethod]
        public void Random_PinnedAmbush_HoldOff_RefusalNamesTheGoalAxis()
        {
            var store = RealAxesStore();
            var catalog = WorldEventHoldComposerGateTests.CatalogFor(store.Families.Keys.OrderBy(k => k, StringComparer.Ordinal).First());
            var saved = WorldEventComposer.HoldEnabledReader;

            try
            {
                WorldEventComposer.HoldEnabledReader = _ => false;

                Assert.IsFalse(WorldEventComposer.TryCompose(store, catalog, RandomRequest(sourceId: "ambush"), out var composition, out var error, new Random(1)));
                Assert.IsNull(composition);
                Assert.AreEqual("random composition: every candidate goal for source 'ambush' has random weight 0 " +
                                "(pin --goal to start one explicitly; defend_envoys also needs world_events_hold_enabled)", error);

                // Pinning the named axis is the way out: kill_count (weight 0) composes when pinned.
                Assert.IsTrue(WorldEventComposer.TryCompose(store, catalog, RandomRequest(sourceId: "ambush", goalId: "kill_count"),
                    out var pinned, out error, new Random(1)), error);
                Assert.AreEqual("kill_count", pinned.Goal.Id);
            }
            finally
            {
                WorldEventComposer.HoldEnabledReader = saved;
            }
        }

        [TestMethod]
        public void Random_PinnedWeightZeroGoal_RefusalNamesTheSourceAxis()
        {
            var store = WeightedFixture(sourceExtraA: @" ""randomWeight"": 0,", sourceExtraB: @" ""randomWeight"": 0,");

            Assert.IsFalse(WorldEventComposer.TryCompose(store, RandomCatalog(), RandomRequest(goalId: "kill_boss"), out _, out var error, new Random(1)));
            Assert.AreEqual("random composition: every candidate source for goal 'kill_boss' has random weight 0 (pin --source to start one explicitly)", error);
        }

        // ---- scripted rng: exact bucket boundaries ----

        /// <summary>Overrides NextDouble ONLY and returns queued values (then 0.0). Never touches Sample or base.</summary>
        private sealed class ScriptedRandom : Random
        {
            private readonly Queue<double> values;

            public ScriptedRandom(params double[] values) { this.values = new Queue<double>(values); }

            public override double NextDouble() => values.Count > 0 ? values.Dequeue() : 0.0;
        }

        /// <summary>One ungrouped source per weight (ids s0, s1, ...), each with the single goal kill_boss.</summary>
        private static WorldEventAxisStore ScriptedStore(params double[] weights)
        {
            string Src(int i, double w) => @"{ ""id"": ""s" + i + @""", ""displayName"": ""s" + i + @""", ""geometry"": ""disc"",
                ""geometryRadius"": 10.0, ""geometryPoints"": 4, ""waveIntervalSeconds"": 40.0, ""maxAlive"": 20,
                ""waveCount"": { ""base"": 3, ""perParticipant"": 1.5, ""cap"": 16 }, ""rewardRadius"": 60.0,
                ""randomWeight"": " + w.ToString(System.Globalization.CultureInfo.InvariantCulture) + @",
                ""compatibleGoals"": [ ""kill_boss"" ], ""startFlavour"": ""x"", ""waveFlavour"": ""y"" }";

            var sources = @"{ ""sources"": [ " + string.Join(", ", weights.Select((w, i) => Src(i, w))) + " ] }";
            var goals = @"{ ""goals"": [ { ""id"": ""kill_boss"", ""displayName"": ""Slay the Champion"", ""type"": ""KillBoss"",
                ""holdSeconds"": 0, ""mvpRule"": ""killingBlowAndTopDamage"", ""progressTemplate"": ""x"" } ] }";

            return WorldEventAxisStore.Parse(sources, RandomFamilies, goals, RandomRewards, null, RandomBosses);
        }

        /// <summary>One pick consumes three draws: bucket, source, goal.</summary>
        private static double[] Picks(params double[] bucketRolls) => bucketRolls.SelectMany(r => new[] { r, 0.0, 0.0 }).ToArray();

        [TestMethod]
        public void WeightedPairOrder_ScriptedRng_FourEqualBuckets_SwitchExactlyAtTheQuarters()
        {
            var store = ScriptedStore(1, 1, 1, 1);
            var pairs = AllPairs(store);

            string First(double roll) => WorldEventComposer.WeightedPairOrder(store, pairs, false, false, new ScriptedRandom(Picks(roll)))[0].sourceId;

            Assert.AreEqual("s0", First(0.0));
            Assert.AreEqual("s0", First(0.2499999));
            Assert.AreEqual("s1", First(0.25));
            Assert.AreEqual("s1", First(0.4999999));
            Assert.AreEqual("s2", First(0.5));
            Assert.AreEqual("s3", First(0.75));
            Assert.AreEqual("s3", First(0.9999999));
        }

        [TestMethod]
        public void WeightedPairOrder_ScriptedRng_WeightTwoVsOne_SwitchesAtTwoThirds()
        {
            var store = ScriptedStore(2, 1);
            var pairs = AllPairs(store);

            string First(double roll) => WorldEventComposer.WeightedPairOrder(store, pairs, false, false, new ScriptedRandom(Picks(roll)))[0].sourceId;

            Assert.AreEqual("s0", First(0.0));
            Assert.AreEqual("s0", First(0.6666));
            Assert.AreEqual("s1", First(0.6667));
            Assert.AreEqual("s1", First(0.9999999));
        }

        [TestMethod]
        public void WeightedPairOrder_ScriptedRng_SecondPickRenormalizesOverTheRemainingPool()
        {
            // Weights 1,1,2. First roll 0.0 takes s0. The pool is then s1 (1) + s2 (2) = 3, so roll 0.3 lands at 0.9 = s1.
            // Over the ORIGINAL total of 4 the same roll would land at 1.2 = s2, so this fails if the share is not renormalized.
            var store = ScriptedStore(1, 1, 2);
            var order = WorldEventComposer.WeightedPairOrder(store, AllPairs(store), false, false, new ScriptedRandom(Picks(0.0, 0.3)));

            CollectionAssert.AreEqual(new[] { ("s0", "kill_boss"), ("s1", "kill_boss"), ("s2", "kill_boss") }, order);

            var other = WorldEventComposer.WeightedPairOrder(store, AllPairs(store), false, false, new ScriptedRandom(Picks(0.0, 0.34)));
            CollectionAssert.AreEqual(new[] { ("s0", "kill_boss"), ("s2", "kill_boss"), ("s1", "kill_boss") }, other, "0.34 x 3 = 1.02 is past s1's share of 1");
        }

        [TestMethod]
        public void Random_AllCandidatesWeightZero_IsRefusedWithAClearMessage()
        {
            var store = WeightedFixture(sourceExtraA: @" ""randomWeight"": 0,", sourceExtraB: @" ""randomWeight"": 0,");

            Assert.IsFalse(WorldEventComposer.TryCompose(store, RandomCatalog(), RandomRequest(), out var composition, out var error, new Random(1)));
            Assert.IsNull(composition);
            StringAssert.Contains(error, "every candidate source/goal has random weight 0");
            StringAssert.Contains(error, "pin --source or --goal");

            // A pinned source still composes: the pin ignores that axis's weight.
            Assert.IsTrue(WorldEventComposer.TryCompose(store, RandomCatalog(), RandomRequest(sourceId: "a"), out _, out error, new Random(1)), error);
        }
    }
}
