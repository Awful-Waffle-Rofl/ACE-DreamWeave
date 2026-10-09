using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.ThreadDungeons;
using ACE.Server.WorldEvents.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    [TestClass]
    public class DungeonRosterSelectorTests
    {
        // Trash/elite levels at 101, 102, 110, 111 are pinned inside the level-100 trash/elite band
        // [100, 115] (DungeonRosterSelector.BandLow/BandHigh = 1.0/1.15). Recomputed from BandHigh's
        // 2026-09-08 narrowing (was 1.5, band [100, 150]) to keep this fixture's members eligible.
        private static readonly Dictionary<uint, int> Levels = new Dictionary<uint, int>
        {
            [100] = 100, [101] = 105, [102] = 110, [110] = 108, [111] = 112, [120] = 200,
            [200] = 20, [201] = 25, [210] = 30,
            [300] = 105, [310] = 400,
        };

        private static int LevelOf(uint wcid) => Levels[wcid];

        private static SpeciesTableDef Band() => new SpeciesTableDef
        {
            Id = "banderling", CreatureType = "Banderling",
            Members = new List<SpeciesMemberDef>
            {
                new SpeciesMemberDef { Wcid = 100, Role = 0 }, new SpeciesMemberDef { Wcid = 101, Role = 0 }, new SpeciesMemberDef { Wcid = 102, Role = 0 },
                new SpeciesMemberDef { Wcid = 110, Role = 1 }, new SpeciesMemberDef { Wcid = 111, Role = 2 },
                new SpeciesMemberDef { Wcid = 120, Role = 3 },
            }
        };

        private static SpeciesTableDef Drudge() => new SpeciesTableDef
        {
            Id = "drudge", CreatureType = "Drudge",
            Members = new List<SpeciesMemberDef> { new SpeciesMemberDef { Wcid = 200, Role = 0 }, new SpeciesMemberDef { Wcid = 201, Role = 0 }, new SpeciesMemberDef { Wcid = 210, Role = 1 } }
        };

        private static SpeciesTableDef Golem() => new SpeciesTableDef
        {
            Id = "golem", CreatureType = "Golem",
            Members = new List<SpeciesMemberDef> { new SpeciesMemberDef { Wcid = 300, Role = 0 }, new SpeciesMemberDef { Wcid = 310, Role = 1 } }
        };

        private static Dictionary<string, SpeciesTableDef> Tables() => new Dictionary<string, SpeciesTableDef>
        {
            ["banderling"] = Band(), ["drudge"] = Drudge(), ["golem"] = Golem(),
        };

        [TestMethod]
        public void Out_of_band_families_are_never_picked()
        {
            for (var seed = 0; seed < 100; seed++)
            {
                var fam = DungeonRosterSelector.PickFamily(Tables(), "any", new List<string>(), new List<string>(), 100, LevelOf, new Random(seed));
                Assert.AreNotEqual("drudge", fam.Id, "drudge is level 20-30, out of the level-100 trash band");
            }
        }

        [TestMethod]
        public void Original_creature_type_is_preferred_three_to_one()
        {
            int band = 0, golem = 0;
            for (var seed = 0; seed < 3000; seed++)
            {
                var fam = DungeonRosterSelector.PickFamily(Tables(), "any", new List<string>(), new List<string> { "Banderling" }, 100, LevelOf, new Random(seed));
                if (fam.Id == "banderling") band++; else if (fam.Id == "golem") golem++;
            }
            var ratio = (double)band / golem;
            Assert.IsTrue(ratio > 2.4 && ratio < 3.6, $"ratio={ratio}");
        }

        [TestMethod]
        public void Requested_family_is_forced_or_rejected()
        {
            Assert.AreEqual("golem", DungeonRosterSelector.PickFamily(Tables(), "golem", new List<string>(), new List<string>(), 100, LevelOf, new Random(1)).Id);
            Assert.ThrowsExactly<ArgumentException>(() => DungeonRosterSelector.PickFamily(Tables(), "drudge", new List<string>(), new List<string>(), 100, LevelOf, new Random(1)));
            Assert.ThrowsExactly<ArgumentException>(() => DungeonRosterSelector.PickFamily(Tables(), "nope", new List<string>(), new List<string>(), 100, LevelOf, new Random(1)));
        }

        [TestMethod]
        public void No_eligible_family_returns_null()
        {
            Assert.IsNull(DungeonRosterSelector.PickFamily(Tables(), "any", new List<string>(), new List<string>(), 260, LevelOf, new Random(1)));
        }

        [TestMethod]
        public void Population_honours_slot_count_and_elite_share()
        {
            var picks = DungeonRosterSelector.PickPopulation(Band(), 100, 20, 0.25, LevelOf, new Random(5));
            Assert.AreEqual(20, picks.Count);
            Assert.AreEqual(5, picks.Count(p => p.Role == DungeonRole.Elite));
            Assert.AreEqual(15, picks.Count(p => p.Role == DungeonRole.Trash));
            Assert.IsTrue(picks.Where(p => p.Role == DungeonRole.Elite).All(p => p.Wcid == 110 || p.Wcid == 111));
            Assert.IsTrue(picks.Where(p => p.Role == DungeonRole.Trash).All(p => p.Wcid >= 100 && p.Wcid <= 102));
            Assert.IsFalse(picks.Any(p => p.Wcid == 120), "named boss role is never drawn");
        }

        [TestMethod]
        public void Elite_falls_back_to_trash_pool_when_none_in_band()
        {
            var picks = DungeonRosterSelector.PickPopulation(Golem(), 100, 4, 0.5, LevelOf, new Random(5));
            Assert.AreEqual(4, picks.Count);
            Assert.IsTrue(picks.All(p => p.Wcid == 300));
            Assert.AreEqual(2, picks.Count(p => p.Role == DungeonRole.Elite), "role is still elite; only the wcid falls back");
        }

        [TestMethod]
        public void Single_slot_with_full_elite_share_is_an_elite()
        {
            var picks = DungeonRosterSelector.PickPopulation(Band(), 100, 1, 1.0, LevelOf, new Random(5));
            Assert.AreEqual(1, picks.Count);
            Assert.AreEqual(DungeonRole.Elite, picks[0].Role);
        }

        [TestMethod]
        public void Elite_count_never_consumes_every_slot_when_more_than_one()
        {
            var picks = DungeonRosterSelector.PickPopulation(Band(), 100, 3, 1.0, LevelOf, new Random(5));
            Assert.AreEqual(2, picks.Count(p => p.Role == DungeonRole.Elite));
        }

        // ---- BandMedianHealth ---------------------------------------------------------------------------

        [TestMethod]
        public void Median_health_is_cross_family_and_distinct_by_wcid()
        {
            var levels = new Dictionary<uint, int> { [400] = 100, [401] = 100, [402] = 100 };
            var health = new Dictionary<uint, uint> { [400] = 1000, [401] = 3000, [402] = 5000 };

            var tables = new Dictionary<string, SpeciesTableDef>
            {
                ["a"] = new SpeciesTableDef { Id = "a", Members = new List<SpeciesMemberDef> { new SpeciesMemberDef { Wcid = 400, Role = 0 } } },
                ["b"] = new SpeciesTableDef { Id = "b", Members = new List<SpeciesMemberDef> { new SpeciesMemberDef { Wcid = 401, Role = 0 } } },
                ["c"] = new SpeciesTableDef { Id = "c", Members = new List<SpeciesMemberDef> { new SpeciesMemberDef { Wcid = 402, Role = 0 } } },
            };

            int LevelOfLocal(uint w) => levels[w];
            uint HealthOfLocal(uint w) => health[w];

            // Odd count (3): the true median, 3000, drawn from a DIFFERENT family than either neighbour.
            Assert.AreEqual(3000u, DungeonRosterSelector.BandMedianHealth(tables, 100, LevelOfLocal, HealthOfLocal));
        }

        [TestMethod]
        public void Median_health_for_an_even_count_takes_the_lower_middle_not_the_average()
        {
            var levels = new Dictionary<uint, int> { [400] = 100, [401] = 100, [402] = 100, [403] = 100 };
            var health = new Dictionary<uint, uint> { [400] = 1000, [401] = 2000, [402] = 4000, [403] = 8000 };

            var tables = new Dictionary<string, SpeciesTableDef>
            {
                ["a"] = new SpeciesTableDef { Id = "a", Members = health.Keys.Select(w => new SpeciesMemberDef { Wcid = w, Role = 0 }).ToList() },
            };

            int LevelOfLocal(uint w) => levels[w];
            uint HealthOfLocal(uint w) => health[w];

            // Sorted: 1000, 2000, 4000, 8000. Average of the two middles would be 3000; the lower middle is 2000.
            Assert.AreEqual(2000u, DungeonRosterSelector.BandMedianHealth(tables, 100, LevelOfLocal, HealthOfLocal));
        }

        /// <summary>
        /// The ghost regression this whole function exists for: a family whose OWN in-band members are all
        /// far below the cross-family median must still be floored to the cross-family standard, not to its
        /// own family's (much lower) median.
        /// </summary>
        [TestMethod]
        public void A_family_far_below_the_cross_family_median_still_gets_the_cross_family_floor()
        {
            var levels = new Dictionary<uint, int> { [500] = 250, [501] = 250, [900] = 250, [901] = 250, [902] = 250 };
            var health = new Dictionary<uint, uint> { [500] = 250, [501] = 275, [900] = 8000, [901] = 8500, [902] = 9000 };

            var tables = new Dictionary<string, SpeciesTableDef>
            {
                ["ghost"] = new SpeciesTableDef { Id = "ghost", Members = new List<SpeciesMemberDef> { new SpeciesMemberDef { Wcid = 500, Role = 0 }, new SpeciesMemberDef { Wcid = 501, Role = 0 } } },
                ["golem"] = new SpeciesTableDef { Id = "golem", Members = new List<SpeciesMemberDef> { new SpeciesMemberDef { Wcid = 900, Role = 0 }, new SpeciesMemberDef { Wcid = 901, Role = 0 }, new SpeciesMemberDef { Wcid = 902, Role = 0 } } },
            };

            int LevelOfLocal(uint w) => levels[w];
            uint HealthOfLocal(uint w) => health[w];

            // Sorted pool: 250, 275, 8000, 8500, 9000 -- median (middle of 5) is 8000, nowhere near ghost's own
            // 250/275, which is exactly the point: the band standard is not the family standard.
            Assert.AreEqual(8000u, DungeonRosterSelector.BandMedianHealth(tables, 250, LevelOfLocal, HealthOfLocal));
        }

        [TestMethod]
        public void Zero_health_members_are_excluded_from_the_sample_not_counted_as_zeroes()
        {
            var levels = new Dictionary<uint, int> { [400] = 100, [401] = 100, [402] = 100 };
            // 401 reports 0 -- "no authored health data", per LiveHealthOf's contract -- and must not pull
            // the median down toward zero.
            var health = new Dictionary<uint, uint> { [400] = 4000, [401] = 0, [402] = 6000 };

            var tables = new Dictionary<string, SpeciesTableDef>
            {
                ["a"] = new SpeciesTableDef { Id = "a", Members = health.Keys.Select(w => new SpeciesMemberDef { Wcid = w, Role = 0 }).ToList() },
            };

            int LevelOfLocal(uint w) => levels[w];
            uint HealthOfLocal(uint w) => health[w];

            // With 401 excluded the sample is {4000, 6000}; lower middle is 4000. If 0 were counted the
            // sample would be {0, 4000, 6000} and the median would be 4000 too by coincidence, so also assert
            // the sample size indirectly via a case where inclusion WOULD change the answer.
            Assert.AreEqual(4000u, DungeonRosterSelector.BandMedianHealth(tables, 100, LevelOfLocal, HealthOfLocal));

            health[402] = 4001;
            // Now sorted-with-zero would be {0, 4000, 4001}, median 4000; sorted-without-zero is {4000, 4001},
            // lower middle 4000. Still coincidentally equal -- use three real values plus one zero instead.
            var levels2 = new Dictionary<uint, int> { [410] = 100, [411] = 100, [412] = 100, [413] = 100 };
            var health2 = new Dictionary<uint, uint> { [410] = 0, [411] = 1000, [412] = 2000, [413] = 3000 };
            var tables2 = new Dictionary<string, SpeciesTableDef>
            {
                ["b"] = new SpeciesTableDef { Id = "b", Members = health2.Keys.Select(w => new SpeciesMemberDef { Wcid = w, Role = 0 }).ToList() },
            };
            int LevelOf2(uint w) => levels2[w];
            uint HealthOf2(uint w) => health2[w];

            // Without the zero: {1000, 2000, 3000}, odd count, true median 2000.
            // If the zero were wrongly counted: {0, 1000, 2000, 3000}, even count, lower middle 1000.
            Assert.AreEqual(2000u, DungeonRosterSelector.BandMedianHealth(tables2, 100, LevelOf2, HealthOf2));
        }

        [TestMethod]
        public void Empty_pool_returns_zero()
        {
            Assert.AreEqual(0u, DungeonRosterSelector.BandMedianHealth(Tables(), 260, LevelOf, w => 1000));
        }

        [TestMethod]
        public void A_CP_loaded_member_with_zero_InitLevel_is_not_excluded_as_no_data()
        {
            // Reviewer's worked case: a CP-loaded weenie can carry InitLevel 0 with its entire authored health
            // parked in LevelFromCP (wcid 35268 Spectral Dread: InitLevel 0, LevelFromCP 20000, Endurance
            // InitLevel 500 -> true health 20250). A healthOf that (bug-for-bug) mirrors the pre-fix
            // LiveHealthOf -- InitLevel + Endurance/2 only, dropping LevelFromCP -- would compute 250, not 0,
            // for this member (0 + 500/2), so it would NOT be excluded either way; the defect was an
            // undercount, not an exclusion. What this test pins is that BandMedianHealth uses whatever
            // healthOf returns without re-deriving or discarding it, so once LiveHealthOf (fixed to include
            // LevelFromCP) reports the true 20250, that value -- not a truncated 250 -- reaches the median.
            var levels = new Dictionary<uint, int> { [500] = 250, [501] = 250, [502] = 250 };
            uint HealthFromAttributesLocal(uint initLevel, uint levelFromCP, uint enduranceInitLevel) =>
                ThreadDungeonSpawner.HealthFromAttributes(initLevel, levelFromCP, enduranceInitLevel);
            var health = new Dictionary<uint, uint>
            {
                [500] = HealthFromAttributesLocal(0, 20000, 500), // CP-loaded: InitLevel 0, LevelFromCP 20000 -> 20250
                [501] = 8000,
                [502] = 9000,
            };
            var tables = new Dictionary<string, SpeciesTableDef>
            {
                ["a"] = new SpeciesTableDef { Id = "a", Members = health.Keys.Select(w => new SpeciesMemberDef { Wcid = w, Role = 0 }).ToList() },
            };

            int LevelOfLocal(uint w) => levels[w];
            uint HealthOfLocal(uint w) => health[w];

            // Sorted: {8000, 9000, 20250}; median (odd count) is 9000 -- the CP-loaded member is IN the
            // sample and correctly pulls the median up, not excluded as if it reported 0.
            Assert.AreEqual(9000u, DungeonRosterSelector.BandMedianHealth(tables, 250, LevelOfLocal, HealthOfLocal));
        }

        [TestMethod]
        public void A_pool_with_only_zero_health_members_returns_zero()
        {
            var levels = new Dictionary<uint, int> { [400] = 100, [401] = 100 };
            var tables = new Dictionary<string, SpeciesTableDef>
            {
                ["a"] = new SpeciesTableDef { Id = "a", Members = new List<SpeciesMemberDef> { new SpeciesMemberDef { Wcid = 400, Role = 0 }, new SpeciesMemberDef { Wcid = 401, Role = 0 } } },
            };

            int LevelOfLocal(uint w) => levels[w];

            Assert.AreEqual(0u, DungeonRosterSelector.BandMedianHealth(tables, 100, LevelOfLocal, w => 0));
        }
    }
}
