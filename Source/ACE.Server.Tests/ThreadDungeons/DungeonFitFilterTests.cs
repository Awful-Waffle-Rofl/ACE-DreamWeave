using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using ACE.DatLoader.Entity;
using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;
using ACE.Server.WorldEvents.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// Pure-function coverage for <see cref="DungeonFitFilter"/> and its wiring into
    /// <see cref="DungeonPopulationBuilder"/>.
    ///
    /// NO DAT AND NO DATABASE. ACE.Server.Tests can never construct a live Player, and PropertyManager reads
    /// throw unless the manager was initialized, so every test here works over hand-built Sphere lists and a
    /// fake fit predicate rather than over real setups or real tunables. The dat read and the tunable reads
    /// both live in ThreadDungeonSpawner, which is exactly why they are not in the filter.
    /// </summary>
    [TestClass]
    public class DungeonFitFilterTests
    {
        // ---- fixtures ----

        /// <summary>
        /// ACE.DatLoader.Entity.Sphere has private setters and is only ever populated by Unpack, so a test
        /// sphere is built the way the dat builds one: four little-endian singles, x/y/z then radius.
        /// </summary>
        private static Sphere Sph(float z, float radius, float x = 0f, float y = 0f)
        {
            var ms = new MemoryStream();

            using (var writer = new BinaryWriter(ms, System.Text.Encoding.UTF8, true))
            {
                writer.Write(x);
                writer.Write(y);
                writer.Write(z);
                writer.Write(radius);
            }

            ms.Position = 0;

            using var reader = new BinaryReader(ms);
            var sphere = new Sphere();
            sphere.Unpack(reader);
            return sphere;
        }

        private static readonly Dictionary<uint, int> Levels = new Dictionary<uint, int>
        {
            [100] = 100, [101] = 120, [102] = 105, [103] = 112, [104] = 118, [105] = 101,
            [110] = 130, [111] = 128,
            [200] = 100, [201] = 118, [210] = 129,
            [10981] = 110,
            // Deliberately BELOW the natural band's low edge (1.0x of a level-100 gem), so only the
            // adaptive widening can reach them.
            [300] = 70, [310] = 72,
        };

        private static int LevelOf(uint w) => Levels.TryGetValue(w, out var lvl) ? lvl : 0;

        private static uint HealthOf(uint w) => 0;

        private static DungeonSpawnPointDef Pt(int i, bool curated = true)
            => new DungeonSpawnPointDef { Cell = 0x01500100u + (uint)i, X = i, Y = 0, Z = 0, Clearance = 2, Curated = curated };

        private static DungeonEntryDef Dungeon(int points) => new DungeonEntryDef
        {
            Id = "filos_doom", Landblock = 0x0150, Name = "Filos' Doom", ExitPortalWcid = 1003601,
            Points = Enumerable.Range(1, points).Select(i => Pt(i)).ToList(),
            BossAnchor = Pt(99),
            CreatureTypes = new List<string> { "Banderling" },
        };

        /// <summary>
        /// Two families. "banderling" carries wcids 100-105 as trash and 110/111 as elite; "grievver" carries
        /// 200/201 trash and 210 elite. Both are eligible at the natural band for a level-100 gem.
        /// </summary>
        private static Dictionary<string, SpeciesTableDef> Species() => new Dictionary<string, SpeciesTableDef>
        {
            ["banderling"] = new SpeciesTableDef
            {
                Id = "banderling", DisplayName = "Banderlings", HueKey = "brown", CreatureType = "Banderling", Source = "retail",
                BiomeTags = new List<string> { "cave" },
                Members = new List<SpeciesMemberDef>
                {
                    new SpeciesMemberDef { Wcid = 100, Role = 0 },
                    new SpeciesMemberDef { Wcid = 101, Role = 0 },
                    new SpeciesMemberDef { Wcid = 102, Role = 0 },
                    new SpeciesMemberDef { Wcid = 103, Role = 0 },
                    new SpeciesMemberDef { Wcid = 104, Role = 0 },
                    new SpeciesMemberDef { Wcid = 105, Role = 0 },
                    new SpeciesMemberDef { Wcid = 110, Role = 1 },
                    new SpeciesMemberDef { Wcid = 111, Role = 1 },
                }
            },
            ["grievver"] = new SpeciesTableDef
            {
                Id = "grievver", DisplayName = "Grievvers", CreatureType = "Grievver", Source = "retail",
                Members = new List<SpeciesMemberDef>
                {
                    new SpeciesMemberDef { Wcid = 200, Role = 0 },
                    new SpeciesMemberDef { Wcid = 201, Role = 0 },
                    new SpeciesMemberDef { Wcid = 210, Role = 1 },
                }
            },
        };

        private static ThreadDungeonStore Store() => ThreadDungeonStore.Parse(
            "{\"dungeons\":[]}",
            "{\"bosses\":[{\"wcid\":10981,\"name\":\"Aun Tanua\",\"level\":110,\"families\":[],\"modifiers\":[]}]}",
            "{\"modifiers\":[],\"xpLadder\":[{\"level\":20,\"xp\":3500},{\"level\":100,\"xp\":80000}]}",
            new Dictionary<string, string>());

        private static DungeonPopulationLimits Limits()
            => new DungeonPopulationLimits(120, 2.0, 3.0, 8, 0.5, rewardScalingEnabled: false);

        private static DungeonGemSpec Spec(string family = "any")
            => new DungeonGemSpec("filos_doom", 100, 6, family, 1, Array.Empty<(string, double)>(), 0, 0);

        /// <summary>A predicate that rejects exactly the named wcids.</summary>
        private static Func<uint, bool> Rejecting(params uint[] wcids)
        {
            var banned = new HashSet<uint>(wcids);
            return w => !banned.Contains(w);
        }

        // ---- MovementHeight ----

        [TestMethod]
        public void MovementHeight_is_the_top_of_the_tallest_of_the_first_two_spheres()
        {
            // Lower sphere tops out at 0.6 + 0.5 = 1.1; upper at 1.4 + 0.7 = 2.1.
            var height = DungeonFitFilter.MovementHeight(new List<Sphere> { Sph(0.6f, 0.5f), Sph(1.4f, 0.7f) }, 1f);

            Assert.AreEqual(2.1f, height, 0.0001f);
        }

        [TestMethod]
        public void MovementHeight_takes_the_taller_of_the_two_even_when_it_is_declared_first()
        {
            var height = DungeonFitFilter.MovementHeight(new List<Sphere> { Sph(1.4f, 0.7f), Sph(0.6f, 0.5f) }, 1f);

            Assert.AreEqual(2.1f, height, 0.0001f);
        }

        [TestMethod]
        public void MovementHeight_ignores_every_sphere_past_the_first_two()
        {
            // The third sphere is by far the tallest (10.0 + 1.0). SpherePath.InitSphere clamps NumSphere to
            // 2 and indexes 0..1 WITHOUT sorting, so a third sphere never takes part in a transition and must
            // not raise the measured height.
            var spheres = new List<Sphere> { Sph(0.6f, 0.5f), Sph(1.4f, 0.7f), Sph(10.0f, 1.0f) };

            Assert.AreEqual(2.1f, DungeonFitFilter.MovementHeight(spheres, 1f), 0.0001f);
        }

        [TestMethod]
        public void MovementHeight_of_an_empty_or_null_list_is_the_unscaled_dummy()
        {
            Assert.AreEqual(0.20f, DungeonFitFilter.MovementHeight(new List<Sphere>(), 1f), 0.0001f);
            Assert.AreEqual(0.20f, DungeonFitFilter.MovementHeight(null, 1f), 0.0001f);

            // The dummy is UNSCALED at both of the engine's install sites (PhysicsObj.cs:1186 and :4100 pass
            // scale 1.0f, not the object's own Scale), so a scale argument must not move it.
            Assert.AreEqual(0.20f, DungeonFitFilter.MovementHeight(new List<Sphere>(), 4f), 0.0001f);
        }

        [TestMethod]
        public void MovementHeight_scales_both_the_origin_and_the_radius()
        {
            // (1.4 + 0.7) * 1.75 = 3.675. Scaling only the origin would give 1.4*1.75 + 0.7 = 3.15, and
            // scaling only the radius 1.4 + 0.7*1.75 = 2.625 - so this pins that InitSphere's
            // "Center * scale, Radius * scale" is reproduced and not half of it.
            var spheres = new List<Sphere> { Sph(0.6f, 0.5f), Sph(1.4f, 0.7f) };

            Assert.AreEqual(3.675f, DungeonFitFilter.MovementHeight(spheres, 1.75f), 0.0001f);
        }

        // ---- Fits ----

        [TestMethod]
        public void Fits_treats_an_unmeasured_dungeon_as_no_constraint()
        {
            Assert.IsTrue(DungeonFitFilter.Fits(99f, 0.0, 0.0), "height 0 is 'not measured', not 'nothing fits'");
            Assert.IsTrue(DungeonFitFilter.Fits(99f, -1.0, 0.0));
        }

        [TestMethod]
        public void Fits_is_inclusive_at_the_boundary()
        {
            // 2.95f widens to 2.950000047683716, strictly GREATER than the 2.95 clearance.json parses. Without
            // DungeonFitFilter.FitEpsilon this exact-boundary case is excluded by a rounding artefact.
            Assert.IsTrue(DungeonFitFilter.Fits(2.95f, 2.95, 0.0), "exact equality fits");
            Assert.IsFalse(DungeonFitFilter.Fits(2.951f, 2.95, 0.0), "a millimetre over does not fit");
        }

        [TestMethod]
        public void The_boundary_epsilon_is_far_smaller_than_any_real_clearance_difference()
        {
            // The epsilon absorbs float widening and NOTHING else, so it must not be acting as a second
            // headroom margin. Pinned BEHAVIOURALLY rather than by comparing the constant, which the compiler
            // folds and MSTEST0032 then flags as an always-true assertion: a hundredth of a millimetre over
            // the ceiling is still a miss, which bounds the epsilon below 1e-5 m without naming it.
            Assert.IsFalse(DungeonFitFilter.Fits(2.9501f, 2.95, 0.0), "0.1 mm over the ceiling is a miss");
            Assert.IsFalse(DungeonFitFilter.Fits(2.95001f, 2.95, 0.0), "0.01 mm over the ceiling is a miss");
        }

        [TestMethod]
        public void Fits_applies_the_headroom_margin()
        {
            Assert.IsTrue(DungeonFitFilter.Fits(2.5f, 3.0, 0.0));
            Assert.IsTrue(DungeonFitFilter.Fits(2.5f, 3.0, 0.5), "2.5 + 0.5 == 3.0 still fits");
            Assert.IsFalse(DungeonFitFilter.Fits(2.5f, 3.0, 0.6));
        }

        [TestMethod]
        public void Fits_is_total_and_fails_open_for_NaN()
        {
            Assert.IsTrue(DungeonFitFilter.Fits(float.NaN, 2.95, 0.0), "a NaN height must never empty a roster");
            Assert.IsTrue(DungeonFitFilter.Fits(2.0f, double.NaN, 0.0), "a NaN dungeon height reads as unmeasured");
            Assert.IsTrue(DungeonFitFilter.Fits(2.0f, 2.95, double.NaN), "a NaN margin degrades to the default");
        }

        // ---- Project ----

        [TestMethod]
        public void Project_never_mutates_its_input()
        {
            var species = Species();
            var originalLists = species.ToDictionary(kv => kv.Key, kv => kv.Value.Members);
            var originalCounts = species.ToDictionary(kv => kv.Key, kv => kv.Value.Members.Count);
            var originalTables = species.ToDictionary(kv => kv.Key, kv => kv.Value);

            var projection = DungeonFitFilter.Project(species, Rejecting(100, 101, 110, 200, 201, 210));

            Assert.IsTrue(projection.RemovedWcids > 0, "the fixture must actually remove something for this to prove anything");

            foreach (var kv in species)
            {
                Assert.AreSame(originalTables[kv.Key], kv.Value, $"{kv.Key}: the table instance itself was replaced");
                Assert.AreSame(originalLists[kv.Key], kv.Value.Members, $"{kv.Key}: the member LIST instance was replaced");
                Assert.AreEqual(originalCounts[kv.Key], kv.Value.Members.Count, $"{kv.Key}: members were removed from the input");
                Assert.AreNotSame(kv.Value.Members, projection.Species[kv.Key].Members, $"{kv.Key}: the projection shares the input's list");
            }
        }

        [TestMethod]
        public void Project_carries_Id_and_CreatureType_onto_the_copy()
        {
            var projection = DungeonFitFilter.Project(Species(), Rejecting(100));

            var banderling = projection.Species["banderling"];

            // Id keys PickFamily's ordering and becomes plan.FamilyId; CreatureType is half of the 3:1
            // dungeon family preference. Losing either is silent.
            Assert.AreEqual("banderling", banderling.Id);
            Assert.AreEqual("Banderling", banderling.CreatureType);
            Assert.AreEqual("Banderlings", banderling.DisplayName);
            Assert.AreEqual("brown", banderling.HueKey);
            Assert.AreEqual("retail", banderling.Source);
            CollectionAssert.AreEqual(new List<string> { "cave" }, banderling.BiomeTags);
        }

        [TestMethod]
        public void Project_counts_removed_wcids_and_emptied_families()
        {
            var projection = DungeonFitFilter.Project(Species(), Rejecting(200, 201, 210, 100));

            Assert.AreEqual(4, projection.RemovedWcids);
            Assert.AreEqual(1, projection.EmptiedFamilies, "grievver lost all three members");
            Assert.IsTrue(projection.Species.ContainsKey("grievver"), "an emptied family is KEPT, with an empty member list");
            Assert.AreEqual(0, projection.Species["grievver"].Members.Count);
            Assert.AreEqual(7, projection.Species["banderling"].Members.Count);
        }

        [TestMethod]
        public void Project_with_a_null_predicate_copies_everything_through()
        {
            var projection = DungeonFitFilter.Project(Species(), null);

            Assert.AreEqual(0, projection.RemovedWcids);
            Assert.AreEqual(0, projection.EmptiedFamilies);
            Assert.AreEqual(8, projection.Species["banderling"].Members.Count);
        }

        // ---- builder wiring ----

        /// <summary>
        /// INVARIANT 6: the master switch off is BIT-IDENTICAL to today, not "usually the same". A null
        /// predicate is what the spawner hands the builder when the switch is off, so the plan it produces for
        /// a fixed seed must match the plan the pre-filter overload produces for that same seed, entry for
        /// entry and note for note.
        /// </summary>
        [TestMethod]
        public void Filter_off_is_bit_identical_to_the_pre_filter_plan_for_a_fixed_seed()
        {
            for (var seed = 0; seed < 25; seed++)
            {
                var before = DungeonPopulationBuilder.Build(Spec(), Dungeon(12), Store(), Species(), LevelOf, HealthOf,
                    Limits(), new Random(seed));

                var after = DungeonPopulationBuilder.Build(Spec(), Dungeon(12), Store(), Species(), LevelOf, HealthOf,
                    Limits(), new Random(seed), null, fitsDungeon: null);

                Assert.AreEqual(before.FamilyId, after.FamilyId, $"seed {seed}: family");
                Assert.AreEqual(before.BossWcid, after.BossWcid, $"seed {seed}: boss");
                Assert.AreEqual(before.BossLevel, after.BossLevel, $"seed {seed}: boss level");
                Assert.AreEqual(before.Entries.Count, after.Entries.Count, $"seed {seed}: entry count");

                for (var i = 0; i < before.Entries.Count; i++)
                {
                    Assert.AreEqual(before.Entries[i].Wcid, after.Entries[i].Wcid, $"seed {seed}: entry {i} wcid");
                    Assert.AreEqual(before.Entries[i].Role, after.Entries[i].Role, $"seed {seed}: entry {i} role");
                    Assert.AreEqual(before.Entries[i].Point.X, after.Entries[i].Point.X, $"seed {seed}: entry {i} point");
                    Assert.AreEqual(before.Entries[i].UpliftLevel, after.Entries[i].UpliftLevel, $"seed {seed}: entry {i} uplift");
                }

                CollectionAssert.AreEqual(before.Notes.ToList(), after.Notes.ToList(), $"seed {seed}: notes");
            }
        }

        [TestMethod]
        public void An_excluded_creature_never_reaches_the_population()
        {
            // Exclude the whole banderling ELITE bench and two of its trash, plus the curated boss. Nothing
            // excluded may appear in any drawn slot.
            var excluded = new uint[] { 100, 101, 110, 111, 10981 };

            var plan = DungeonPopulationBuilder.Build(Spec(), Dungeon(12), Store(), Species(), LevelOf, HealthOf,
                Limits(), new Random(7), null, Rejecting(excluded));

            Assert.IsTrue(plan.Entries.Count > 0, "the run must still be populated");

            foreach (var entry in plan.Entries)
                CollectionAssert.DoesNotContain(excluded, entry.Wcid, $"wcid {entry.Wcid} was excluded but was placed anyway");

            Assert.AreNotEqual(10981u, plan.BossWcid, "an excluded curated boss must not headline");
            Assert.IsTrue(plan.Notes.Any(n => n.StartsWith("fit:")), "the exclusion must be reported on the plan");
        }

        [TestMethod]
        public void A_family_whose_whole_band_trash_is_excluded_is_not_returned_by_the_family_pick()
        {
            // grievver's entire membership is excluded, so no seed may ever draw it. banderling survives.
            for (var seed = 0; seed < 40; seed++)
            {
                var plan = DungeonPopulationBuilder.Build(Spec(), Dungeon(8), Store(), Species(), LevelOf, HealthOf,
                    Limits(), new Random(seed), null, Rejecting(200, 201, 210));

                Assert.AreEqual("banderling", plan.FamilyId, $"seed {seed}: an emptied family was picked anyway");
                Assert.IsTrue(plan.Entries.Any(e => e.Role != DungeonRole.Boss), $"seed {seed}: the run opened with no trash");
            }
        }

        /// <summary>
        /// The reason the filter is applied at ROSTER CONSTRUCTION rather than to the pools afterwards.
        /// ResolveFamilyEliteBand widens only while the elite pool is empty; if it saw the UNFILTERED members
        /// it would stop at the natural band, and the post-filter pool would then be empty and the elite slots
        /// would silently fall back. Filtering first makes the widening search the filtered members and reach
        /// the below-band elite, which the plan then tags for uplift.
        /// </summary>
        [TestMethod]
        public void An_elite_pool_emptied_at_the_natural_band_is_rescued_by_the_widening()
        {
            var species = Species();

            // A below-band elite (level 72 against a level-100 gem) that only the widening can reach.
            species["banderling"].Members.Add(new SpeciesMemberDef { Wcid = 310, Role = 1 });

            // The floor must actually allow widening for this test to measure anything.
            var limits = new DungeonPopulationLimits(120, 2.0, 3.0, 8, 0.5, rewardScalingEnabled: false,
                bandLowFloorRatio: DungeonRosterSelector.BandLowFloor);

            Assert.IsTrue(limits.BandLowFloorRatio < 1.0, "the fixture must leave band widening ENABLED, or this test measures nothing");

            var plan = DungeonPopulationBuilder.Build(Spec(), Dungeon(12), Store(), species, LevelOf, HealthOf,
                limits, new Random(11), null, Rejecting(110, 111, 200, 201, 210));

            Assert.AreEqual("banderling", plan.FamilyId);
            Assert.IsTrue(plan.Entries.Any(e => e.Role == DungeonRole.Elite),
                "the elite slots must still be filled from the widened, filtered pool");
            Assert.IsTrue(plan.Entries.Where(e => e.Role == DungeonRole.Elite).All(e => e.Wcid == 310u),
                "the only surviving elite is the below-band one the widening reached");
        }

        [TestMethod]
        public void The_zero_eligible_families_guard_restores_the_raw_roster_and_says_so()
        {
            var species = Species();
            var everyWcid = species.Values.SelectMany(t => t.Members).Select(m => m.Wcid).ToArray();

            var plan = DungeonPopulationBuilder.Build(Spec(), Dungeon(12), Store(), species, LevelOf, HealthOf,
                Limits(), new Random(5), null, Rejecting(everyWcid));

            Assert.IsNotNull(plan.FamilyId, "the guard must fall back to the raw roster rather than open the run empty");
            Assert.IsTrue(plan.Entries.Any(e => e.Role != DungeonRole.Boss), "the run must have trash");
            Assert.IsTrue(plan.Notes.Any(n => n.Contains("fit filter bypassed")), "the bypass must be reported on the plan");
        }

        [TestMethod]
        public void A_forced_family_that_does_not_fit_still_produces_a_populated_run()
        {
            // The /dd give path: the gem NAMES grievver, and every grievver is excluded. PickFamily refuses a
            // forced family it cannot satisfy rather than substituting, so without the guard this run opens
            // with no trash and no boss.
            var plan = DungeonPopulationBuilder.Build(Spec("grievver"), Dungeon(12), Store(), Species(), LevelOf, HealthOf,
                Limits(), new Random(9), null, Rejecting(200, 201, 210));

            Assert.AreEqual("grievver", plan.FamilyId, "the forced family must still be honoured");
            Assert.IsTrue(plan.Entries.Any(e => e.Role != DungeonRole.Boss), "the forced run must still be populated");
            Assert.IsTrue(plan.Notes.Any(n => n.Contains("fit filter bypassed")));
        }

        [TestMethod]
        public void A_forced_family_that_survives_the_filter_is_still_filtered()
        {
            // The complement of the test above: the guard must not fire merely because a family was forced.
            var plan = DungeonPopulationBuilder.Build(Spec("banderling"), Dungeon(12), Store(), Species(), LevelOf, HealthOf,
                Limits(), new Random(9), null, Rejecting(100, 101, 200, 201, 210));

            Assert.AreEqual("banderling", plan.FamilyId);
            Assert.IsFalse(plan.Notes.Any(n => n.Contains("fit filter bypassed")));

            foreach (var entry in plan.Entries.Where(e => e.Role != DungeonRole.Boss))
                Assert.IsTrue(entry.Wcid != 100u && entry.Wcid != 101u, $"excluded wcid {entry.Wcid} was placed");
        }

        // ---- store ----

        [TestMethod]
        public void A_dungeon_with_no_clearance_row_reads_as_no_constraint()
        {
            var store = ThreadDungeonStore.Parse("{\"dungeons\":[]}", "{\"bosses\":[]}", "{\"modifiers\":[]}",
                new Dictionary<string, string>());

            Assert.IsNotNull(store.Clearance, "Clearance is empty, never null");
            Assert.AreEqual(0, store.Clearance.Count);
            Assert.IsFalse(store.Clearance.TryGetValue("filos_doom", out _));
        }

        [TestMethod]
        public void Clearance_rows_are_keyed_by_dungeon_id_and_carry_the_envelope()
        {
            var store = ThreadDungeonStore.Parse(
                "{\"version\":1,\"dungeons\":[{\"id\":\"tiny_hive\",\"landblock\":\"0x0289\",\"name\":\"Tiny Hive\",\"minLevel\":60,\"maxLevel\":375," +
                "\"exitPortalWcid\":1003601,\"enabled\":true}]}",
                "{\"bosses\":[]}", "{\"modifiers\":[]}",
                new Dictionary<string, string>
                {
                    ["0x0289"] = "{\"version\":1,\"landblock\":\"0x0289\",\"entry\":\"0x02890100\"," +
                                 "\"points\":[{\"cell\":42533120,\"x\":1,\"y\":1,\"z\":0}]," +
                                 "\"bossAnchor\":{\"cell\":42533120,\"x\":2,\"y\":2,\"z\":0}}"
                },
                preDiagnostics: null,
                attunementJson: null,
                clearanceJson: "{\"version\":1,\"dungeons\":[{\"id\":\"tiny_hive\",\"landblock\":\"0x0289\",\"enabled\":true," +
                               "\"maxCollisionHeightFullAccess\":2.95," +
                               "\"envelope\":[{\"collisionHeight\":2.5,\"maxCollisionRadius\":1.2,\"collisionRadius99\":1.2," +
                               "\"collisionRadius95\":1.1,\"collisionRadius90\":1.0}]}]}");

            Assert.IsTrue(store.Clearance.TryGetValue("tiny_hive", out var row));
            Assert.AreEqual(2.95, row.MaxCollisionHeightFullAccess, 0.0001);
            Assert.AreEqual(1, row.Envelope.Count);
            Assert.AreEqual(1.2, row.Envelope[0].MaxCollisionRadius, 0.0001);
            Assert.AreEqual(1.0, row.Envelope[0].CollisionRadius90, 0.0001);
        }

        [TestMethod]
        public void An_unknown_clearance_id_and_a_landblock_disagreement_are_non_fatal_diagnostics()
        {
            var store = ThreadDungeonStore.Parse(
                "{\"version\":1,\"dungeons\":[{\"id\":\"tiny_hive\",\"landblock\":\"0x0289\",\"name\":\"Tiny Hive\",\"minLevel\":60,\"maxLevel\":375," +
                "\"exitPortalWcid\":1003601,\"enabled\":true}]}",
                "{\"bosses\":[]}", "{\"modifiers\":[]}",
                new Dictionary<string, string>
                {
                    ["0x0289"] = "{\"version\":1,\"landblock\":\"0x0289\",\"entry\":\"0x02890100\"," +
                                 "\"points\":[{\"cell\":42533120,\"x\":1,\"y\":1,\"z\":0}]," +
                                 "\"bossAnchor\":{\"cell\":42533120,\"x\":2,\"y\":2,\"z\":0}}"
                },
                preDiagnostics: null,
                attunementJson: null,
                clearanceJson: "{\"version\":1,\"dungeons\":[" +
                               "{\"id\":\"tiny_hive\",\"landblock\":\"0x9999\",\"maxCollisionHeightFullAccess\":2.95}," +
                               "{\"id\":\"no_such_dungeon\",\"landblock\":\"0x0111\",\"maxCollisionHeightFullAccess\":3.0}]}");

            // Both rows are still loaded - neither problem is a reason to drop a measurement.
            Assert.AreEqual(2, store.Clearance.Count);
            Assert.IsTrue(store.Diagnostics.Any(d => d.Contains("no_such_dungeon") && d.Contains("no dungeon in index.json")));
            Assert.IsTrue(store.Diagnostics.Any(d => d.Contains("tiny_hive") && d.Contains("disagrees")));

            // And the dungeon itself survived: a clearance fault must never take a dungeon offline.
            Assert.IsTrue(store.Dungeons.ContainsKey("tiny_hive"));
        }

        [TestMethod]
        public void An_unsupported_clearance_version_loads_nothing_and_constrains_nothing()
        {
            var store = ThreadDungeonStore.Parse("{\"dungeons\":[]}", "{\"bosses\":[]}", "{\"modifiers\":[]}",
                new Dictionary<string, string>(), preDiagnostics: null, attunementJson: null,
                clearanceJson: "{\"version\":99,\"dungeons\":[{\"id\":\"tiny_hive\",\"maxCollisionHeightFullAccess\":2.95}]}");

            Assert.AreEqual(0, store.Clearance.Count);
            Assert.IsTrue(store.Diagnostics.Any(d => d.Contains("clearance.json") && d.Contains("unsupported version 99")));
        }

        // ---- the shipped exclusion, end to end over the pure layer ----

        /// <summary>
        /// The shipped verdict for wcid 1004046 (Olthoi Deepmother), pinned against the two dungeons that
        /// bracket it. Its rig is 0x02000AAD at DefaultScale 1.0 (no DefaultScale row on the weenie, which IS
        /// exactly 1.0), and setup 0x02000AAD declares exactly two collision spheres - (z 1.0, r 1.0) and
        /// (z 2.675, r 1.0) - so its movement height is 3.675 m. That is above Tiny Hive's
        /// maxCollisionHeightFullAccess of 2.95 and below Artifex Collegium's 5.00.
        ///
        /// The sphere values are the real ones, transcribed from the dat rather than read from it, because
        /// ACE.Server.Tests has no dat; the dungeon heights are the real ones from
        /// Content/dungeons/dynamic/clearance.json. ACE.Content.Tools "cells --modelbounds=0x02000AAD" prints
        /// the same stack, rounded for display to "2.68" and "MOVEMENT height = 3.68".
        /// </summary>
        [TestMethod]
        public void The_deepmother_is_excluded_from_tiny_hive_and_kept_in_artifex_collegium()
        {
            const uint deepmother = 1004046;
            const double tinyHive = 2.95;
            const double artifexCollegium = 5.0;

            var height = DungeonFitFilter.MovementHeight(new List<Sphere> { Sph(1.0f, 1.0f), Sph(2.675f, 1.0f) }, 1.0f);

            Assert.AreEqual(3.675f, height, 0.0001f, "fixture drift: setup 0x02000AAD measures 3.675 m at scale 1.0");

            Assert.IsFalse(DungeonFitFilter.Fits(height, tinyHive, 0.0), "3.675 m must not fit a 2.95 m dungeon");
            Assert.IsTrue(DungeonFitFilter.Fits(height, artifexCollegium, 0.0), "3.675 m must fit a 5.00 m dungeon");

            // Burun Fortress and Matron Hive South are the two 3.75 m dungeons, and 3.675 clears both by
            // 0.075 m - the tightest real margin in the shipped table, and the reason the boundary epsilon
            // has to be micrometres rather than centimetres.
            Assert.IsTrue(DungeonFitFilter.Fits(height, 3.75, 0.0), "3.675 m must still fit the 3.75 m dungeons");

            var species = new Dictionary<string, SpeciesTableDef>
            {
                ["olthoi"] = new SpeciesTableDef
                {
                    Id = "olthoi", CreatureType = "Olthoi",
                    Members = new List<SpeciesMemberDef>
                    {
                        new SpeciesMemberDef { Wcid = deepmother, Role = 0 },
                        new SpeciesMemberDef { Wcid = 100, Role = 0 },
                    }
                }
            };

            var tight = DungeonFitFilter.Project(species, w => DungeonFitFilter.Fits(w == deepmother ? height : 1.8f, tinyHive, 0.0));
            var tall = DungeonFitFilter.Project(species, w => DungeonFitFilter.Fits(w == deepmother ? height : 1.8f, artifexCollegium, 0.0));

            CollectionAssert.DoesNotContain(tight.Species["olthoi"].Members.Select(m => m.Wcid).ToList(), deepmother);
            CollectionAssert.Contains(tall.Species["olthoi"].Members.Select(m => m.Wcid).ToList(), deepmother);
        }
    }
}
