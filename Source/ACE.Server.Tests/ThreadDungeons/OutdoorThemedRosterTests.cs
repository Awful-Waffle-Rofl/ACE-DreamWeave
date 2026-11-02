using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;
using ACE.Server.WorldEvents.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// Open World Threads WP3b review F11: an outdoor site fields its themed creatures. When any eligible species table
    /// matches the site's families or creature types, the family draw is over the matching tables alone; with none
    /// eligible it falls back to the ordinary weighted draw. A dungeon's draw is pinned byte-for-byte against a verbatim
    /// copy of the pre-F11 algorithm, including how many random numbers it consumes.
    /// </summary>
    [TestClass]
    public class OutdoorThemedRosterTests
    {
        private static readonly Dictionary<uint, int> Levels = new Dictionary<uint, int>
        {
            [100] = 100, [101] = 120, [102] = 105, [110] = 130,
            [200] = 100, [201] = 118, [210] = 129,
            [300] = 101, [301] = 110, [310] = 125,
            [10981] = 110,
        };

        private static int LevelOf(uint w) => Levels.TryGetValue(w, out var l) ? l : 0;

        private static SpeciesTableDef Table(string id, string creatureType, uint trash1, uint trash2, uint elite) => new SpeciesTableDef
        {
            Id = id, DisplayName = id, CreatureType = creatureType, Source = "retail",
            Members = new List<SpeciesMemberDef>
            {
                new SpeciesMemberDef { Wcid = trash1, Role = 0 }, new SpeciesMemberDef { Wcid = trash2, Role = 0 }, new SpeciesMemberDef { Wcid = elite, Role = 1 },
            }
        };

        private static Dictionary<string, SpeciesTableDef> Tables() => new Dictionary<string, SpeciesTableDef>
        {
            ["banderling"] = Table("banderling", "Banderling", 100, 102, 110),
            ["drudge"] = Table("drudge", "Drudge", 300, 301, 310),
            ["mukkir"] = Table("mukkir", "Mukkir", 200, 201, 210),
        };

        private static readonly IReadOnlyList<string> NoFamilies = new List<string>();

        /// <summary>The PickFamily any-family branch exactly as it was before F11, verbatim apart from its name.</summary>
        private static SpeciesTableDef PreF11PickFamily(IReadOnlyDictionary<string, SpeciesTableDef> tables, IReadOnlyList<string> dungeonFamilies,
            IReadOnlyList<string> dungeonCreatureTypes, int level, Func<uint, int> levelOf, Random rng)
        {
            var eligible = tables.Values.Where(t => DungeonRosterSelector.IsEligible(t, level, levelOf, null)).OrderBy(t => t.Id, StringComparer.Ordinal).ToList();
            if (eligible.Count == 0)
                return null;

            int WeightOf(SpeciesTableDef t)
            {
                var preferred = dungeonFamilies.Contains(t.Id)
                    || (!string.IsNullOrEmpty(t.CreatureType) && dungeonCreatureTypes.Any(c => string.Equals(c, t.CreatureType, StringComparison.OrdinalIgnoreCase)));
                return preferred ? DungeonRosterSelector.PreferredFamilyWeight : 1;
            }

            var total = eligible.Sum(WeightOf);
            var roll = rng.Next(total);
            foreach (var t in eligible)
            {
                roll -= WeightOf(t);
                if (roll < 0) return t;
            }
            return eligible[eligible.Count - 1];
        }

        [TestMethod]
        public void Fixture_every_table_is_eligible_at_level_100()
        {
            foreach (var t in Tables().Values)
                Assert.IsTrue(DungeonRosterSelector.IsEligible(t, 100, LevelOf, null), t.Id);
        }

        [TestMethod]
        public void An_outdoor_site_with_a_matching_table_draws_only_its_theme()
        {
            var types = new List<string> { "Mukkir" };

            for (var seed = 0; seed < 300; seed++)
            {
                var picked = DungeonRosterSelector.PickFamily(Tables(), DungeonGemSpec.Any, NoFamilies, types, 100, LevelOf, new Random(seed), themedOnly: true);
                Assert.AreEqual("mukkir", picked.Id, $"seed {seed}");
            }

            // Two themed tables: both appear, and nothing else does.
            var two = new List<string> { "Mukkir", "Drudge" };
            var seen = Enumerable.Range(0, 300)
                .Select(seed => DungeonRosterSelector.PickFamily(Tables(), DungeonGemSpec.Any, NoFamilies, two, 100, LevelOf, new Random(seed), themedOnly: true).Id)
                .Distinct().OrderBy(x => x).ToList();
            CollectionAssert.AreEqual(new[] { "drudge", "mukkir" }, seen);
        }

        [TestMethod]
        public void A_family_id_match_counts_as_theme_too()
        {
            for (var seed = 0; seed < 100; seed++)
                Assert.AreEqual("drudge", DungeonRosterSelector.PickFamily(Tables(), DungeonGemSpec.Any, new List<string> { "drudge" }, new List<string>(), 100, LevelOf,
                    new Random(seed), themedOnly: true).Id);
        }

        [TestMethod]
        public void An_outdoor_site_with_no_eligible_match_falls_back_to_the_weighted_draw()
        {
            var types = new List<string> { "Olthoi" };   // no table carries it
            var seen = new HashSet<string>();

            for (var seed = 0; seed < 300; seed++)
            {
                var themedRng = new Random(seed);
                var plainRng = new Random(seed);
                var themed = DungeonRosterSelector.PickFamily(Tables(), DungeonGemSpec.Any, NoFamilies, types, 100, LevelOf, themedRng, themedOnly: true);
                var plain = PreF11PickFamily(Tables(), NoFamilies, types, 100, LevelOf, plainRng);

                Assert.AreSame(plain.Id, themed.Id, $"seed {seed}: the fallback is the old draw");
                Assert.AreEqual(plainRng.Next(), themedRng.Next(), $"seed {seed}: and consumes the same random stream");
                seen.Add(themed.Id);
            }

            Assert.AreEqual(3, seen.Count, "the fallback can still field every eligible family");
        }

        [TestMethod]
        public void A_dungeon_draw_is_byte_for_byte_the_pre_F11_draw()
        {
            var cases = new[]
            {
                new List<string> { "Mukkir" }, new List<string> { "Mukkir", "Drudge" }, new List<string>(), new List<string> { "Olthoi" },
            };

            foreach (var types in cases)
            {
                for (var seed = 0; seed < 500; seed++)
                {
                    var newRng = new Random(seed);
                    var oldRng = new Random(seed);

                    var now = DungeonRosterSelector.PickFamily(Tables(), DungeonGemSpec.Any, NoFamilies, types, 100, LevelOf, newRng);
                    var then = PreF11PickFamily(Tables(), NoFamilies, types, 100, LevelOf, oldRng);

                    Assert.AreEqual(then.Id, now.Id, $"types [{string.Join(",", types)}] seed {seed}");
                    Assert.AreEqual(oldRng.Next(), newRng.Next(), $"types [{string.Join(",", types)}] seed {seed}: same draws consumed");
                }
            }
        }

        // ---- through the builder ----

        private static ThreadDungeonStore Store() => ThreadDungeonStore.Parse(
            "{\"dungeons\":[]}",
            "{\"bosses\":[{\"wcid\":10981,\"name\":\"Aun Tanua\",\"level\":110,\"families\":[],\"modifiers\":[]}]}",
            "{\"modifiers\":[],\"xpLadder\":[{\"level\":20,\"xp\":3500},{\"level\":100,\"xp\":80000}]}",
            new Dictionary<string, string>());

        private static DungeonPopulationLimits Limits() => new DungeonPopulationLimits(120, 2.0, 3.0, 8, 0.5, rewardScalingEnabled: false, minMonsters: 0);

        private static DungeonSpawnPointDef Pt(int i, uint landblock) => new DungeonSpawnPointDef
        {
            Cell = (landblock << 16) | 0x0001u, X = 20 + i * 10, Y = 20 + i * 10, Z = 10, Clearance = 2, Curated = true, Kind = OutdoorPopulation.KindOutdoor,
        };

        private static DungeonEntryDef Row(bool outdoor)
        {
            const ushort lb = 0x5456;
            var row = new DungeonEntryDef
            {
                Id = "ow_mukkir_test", Landblock = lb, Name = "Mukkir Encampment", ExitPortalWcid = 1003604,
                Entry = "0x54560001 100 96 10",
                Points = Enumerable.Range(1, 8).Select(i => Pt(i, lb)).ToList(),
                BossAnchor = Pt(12, lb),
                CreatureTypes = new List<string> { "Mukkir" },
            };

            if (outdoor)
            {
                row.Kind = DungeonEntryDef.KindOutdoor;
                row.Outdoor = new OutdoorSiteDef
                {
                    Primary = lb, Footprint = new List<ushort> { lb },
                    Circle = new OutdoorCircleDef { Landblock = lb, X = 96, Y = 96, Radius = 90 },
                    Exit = "0x54560001 101 96 10",
                };
            }

            return row;
        }

        private static DungeonGemSpec Spec() => new DungeonGemSpec("ow_mukkir_test", 100, 6, "any", 1, Array.Empty<(string, double)>(), 0, 0);

        [TestMethod]
        public void The_builder_themes_an_outdoor_site_and_leaves_a_dungeon_weighted()
        {
            var outdoorFamilies = new HashSet<string>();
            var dungeonFamilies = new HashSet<string>();

            for (var seed = 0; seed < 60; seed++)
            {
                outdoorFamilies.Add(DungeonPopulationBuilder.Build(Spec(), Row(outdoor: true), Store(), Tables(), LevelOf, w => 0, Limits(), new Random(seed),
                    movementHeightOf: w => 1.0f).FamilyId);
                dungeonFamilies.Add(DungeonPopulationBuilder.Build(Spec(), Row(outdoor: false), Store(), Tables(), LevelOf, w => 0, Limits(), new Random(seed)).FamilyId);
            }

            CollectionAssert.AreEqual(new[] { "mukkir" }, outdoorFamilies.ToList(), "the outdoor site always fields its theme");
            Assert.IsTrue(dungeonFamilies.Count > 1, "control: the same row as a dungeon keeps the weighted 3:1 draw: " + string.Join(",", dungeonFamilies));
        }
    }
}
