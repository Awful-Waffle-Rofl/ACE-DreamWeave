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
    /// The "any" gem's dungeon pick (<c>OutdoorRotation.Pick</c>, first value of Random(seed)) and the trash-family draw used
    /// to consume the SAME first value of the SAME seed, so dungeon k of N only ever saw family rolls in [k/N,(k+1)/N).
    /// The family now draws from its own salted stream (DungeonRosterSelector.FamilyRngFor) and burns exactly the one value
    /// it used to take from the main stream, so everything drawn after it is unchanged.
    /// </summary>
    [TestClass]
    public class FamilyRngSaltTests
    {
        private const int FamilyCount = 8;
        private const int DungeonCount = 5;

        private static readonly Func<uint, int> LevelOf = _ => 102;


        private static Dictionary<string, SpeciesTableDef> Species()
        {
            var d = new Dictionary<string, SpeciesTableDef>();

            for (var i = 0; i < FamilyCount; i++)
            {
                var id = "fam" + i;
                var baseWcid = (uint)(1000 + i * 10);

                d[id] = new SpeciesTableDef
                {
                    Id = id, CreatureType = "Type" + i,
                    Members = new List<SpeciesMemberDef>
                    {
                        new SpeciesMemberDef { Wcid = baseWcid, Role = 0 }, new SpeciesMemberDef { Wcid = baseWcid + 1, Role = 0 },
                        new SpeciesMemberDef { Wcid = baseWcid + 2, Role = 0 }, new SpeciesMemberDef { Wcid = baseWcid + 3, Role = 1 },
                    }
                };
            }

            return d;
        }

        private static DungeonSpawnPointDef Pt(int i) => new DungeonSpawnPointDef { Cell = 0x01500100u + (uint)i, X = i, Y = 0, Z = 0, Clearance = 2, Curated = true };

        private static DungeonEntryDef Dungeon(string id = "filos_doom") => new DungeonEntryDef
        {
            Id = id, Landblock = 0x0150, Name = id, ExitPortalWcid = 1003601,
            Points = Enumerable.Range(1, 20).Select(Pt).ToList(), BossAnchor = Pt(99),
        };

        private static ThreadDungeonStore Store() => ThreadDungeonStore.Parse("{\"dungeons\":[]}", "{\"bosses\":[]}",
            "{\"modifiers\":[],\"xpLadder\":[{\"level\":20,\"xp\":3500},{\"level\":100,\"xp\":80000},{\"level\":200,\"xp\":1100000}]}",
            new Dictionary<string, string>());

        private static DungeonPopulationLimits Limits() => new DungeonPopulationLimits(120, 2.0, 3.0, 8, 0.5, rewardScalingEnabled: false, reachUp: false, minMonsters: 0);

        private static DungeonGemSpec Spec(int seed, string family = "any") => new DungeonGemSpec("filos_doom", 100, 6, family, seed, null, 0, 0);

        private static DungeonSpawnPlan Build(DungeonGemSpec spec, Random rng)
            => DungeonPopulationBuilder.Build(spec, Dungeon(), Store(), Species(), LevelOf, _ => 300u, Limits(), rng);

        private static List<DungeonEntryDef> Candidates() => Enumerable.Range(0, DungeonCount).Select(i => Dungeon("d" + i)).ToList();

        [TestMethod]
        public void Family_is_decoupled_from_the_dungeon_pick_of_the_same_seed()
        {
            var candidates = Candidates();
            var drawn = new HashSet<string>();
            var landed = 0;

            for (var seed = 1; seed < 20000 && landed < 500; seed++)
            {
                var pick = OutdoorRotation.Pick(candidates, new Random(seed));

                if (!ReferenceEquals(pick, candidates[0]))
                    continue;

                landed++;
                drawn.Add(Build(Spec(seed), new Random(seed)).FamilyId);
            }

            Assert.AreEqual(500, landed, "enough seeds landed on the first dungeon");
            Assert.IsTrue(drawn.Count >= 6, $"seeds all landing on one dungeon drew only {drawn.Count} of {FamilyCount} families: {string.Join(",", drawn.OrderBy(x => x))}");
        }

        [TestMethod]
        public void FamilyRngFor_is_a_pure_function_of_the_seed()
        {
            foreach (var seed in new[] { 0, 1, 7, -5, int.MaxValue, int.MinValue })
            {
                var a = DungeonRosterSelector.FamilyRngFor(seed);
                var b = DungeonRosterSelector.FamilyRngFor(seed);

                for (var i = 0; i < 5; i++)
                    Assert.AreEqual(a.Next(), b.Next());
            }

            Assert.AreNotEqual(DungeonRosterSelector.FamilyRngFor(1).Next(), DungeonRosterSelector.FamilyRngFor(2).Next());
        }

        [TestMethod]
        public void PickFamily_burns_one_Next_from_the_main_stream_and_matches_the_old_stream_after_it()
        {
            var tables = Species();

            for (var seed = 0; seed < 200; seed++)
            {
                var main = new Random(seed);
                var reference = new Random(seed);

                var family = DungeonRosterSelector.PickFamily(tables, "any", new List<string>(), new List<string>(), 100, LevelOf, DungeonRosterSelector.FamilyRngFor(seed),
                    burnRng: main);

                Assert.IsNotNull(family);

                reference.Next(FamilyCount);

                for (var i = 0; i < 10; i++)
                    Assert.AreEqual(reference.Next(), main.Next(), $"seed {seed} draw {i} after the family draw");
            }
        }

        [TestMethod]
        public void PickFamilyWeighted_burns_one_NextDouble_from_the_main_stream()
        {
            var tables = Species().Values.OrderBy(t => t.Id, StringComparer.Ordinal).ToList();
            var weights = tables.Select(_ => 1.0).ToList();

            for (var seed = 0; seed < 200; seed++)
            {
                var main = new Random(seed);
                var reference = new Random(seed);

                Assert.IsNotNull(DungeonRosterSelector.PickFamilyWeighted(tables, weights, DungeonRosterSelector.FamilyRngFor(seed), main));

                reference.NextDouble();

                for (var i = 0; i < 10; i++)
                    Assert.AreEqual(reference.Next(), main.Next(), $"seed {seed} draw {i} after the weighted family draw");
            }
        }

        [TestMethod]
        public void Nothing_is_burned_when_the_family_is_forced_or_none_is_eligible()
        {
            var tables = Species();
            var main = new Random(5);

            Assert.AreEqual("fam3", DungeonRosterSelector.PickFamily(tables, "fam3", new List<string>(), new List<string>(), 100, LevelOf, new Random(1), burnRng: main).Id);
            Assert.IsNull(DungeonRosterSelector.PickFamily(tables, "any", new List<string>(), new List<string>(), 100, _ => 9999, new Random(1), burnRng: main));
            Assert.IsNull(DungeonRosterSelector.PickFamilyWeighted(new List<SpeciesTableDef>(), new List<double>(), new Random(1), main));

            Assert.AreEqual(new Random(5).Next(), main.Next());
        }

        [TestMethod]
        public void Everything_after_the_family_draw_is_unchanged()
        {
            // The population drawn for an "any" gem must equal the one drawn for the SAME family forced, on a main stream
            // that was pre-advanced by exactly the one Next(total) the old code spent on the family: forced draws consume
            // nothing, and the old any-draw consumed exactly that one value, so this is the pre-change stream.
            for (var seed = 1; seed <= 60; seed++)
            {
                var any = Build(Spec(seed), new Random(seed));
                Assert.IsNotNull(any.FamilyId);

                var preAdvanced = new Random(seed);
                preAdvanced.Next(FamilyCount);
                var forced = Build(Spec(seed, any.FamilyId), preAdvanced);

                Assert.AreEqual(any.FamilyId, forced.FamilyId);
                CollectionAssert.AreEqual(any.Entries.Select(e => (e.Wcid, e.Role, e.Point.Cell)).ToList(), forced.Entries.Select(e => (e.Wcid, e.Role, e.Point.Cell)).ToList(),
                    $"seed {seed}: population differs");
                Assert.AreEqual(any.BossWcid, forced.BossWcid);
            }
        }

        [TestMethod]
        public void A_forced_family_build_is_independent_of_the_family_salt()
        {
            // Speed courses force their family: no family draw, so the plan is a function of the main stream alone. Two specs that
            // differ ONLY in seed (hence in the salted family stream) must build the identical plan on the same main stream.
            var a = Build(Spec(42, "fam2"), new Random(42));
            var b = Build(Spec(43, "fam2"), new Random(42));

            Assert.AreEqual("fam2", a.FamilyId);
            Assert.AreEqual("fam2", b.FamilyId);
            Assert.IsTrue(a.Entries.Count > 0);
            CollectionAssert.AreEqual(a.Entries.Select(e => (e.Wcid, e.Role, e.Point.Cell)).ToList(), b.Entries.Select(e => (e.Wcid, e.Role, e.Point.Cell)).ToList());
            Assert.AreEqual(a.BossWcid, b.BossWcid);
        }
    }
}
