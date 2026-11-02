using System;
using System.Collections.Generic;
using System.Linq;

using ACE.DatLoader.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;
using ACE.Server.WorldEvents.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The archetype-weighted family draw (dynamic_dungeons_family_archetype_weighting, 2026-10-27): the pure weights
    /// (single-archetype pools hit the natural mix exactly, every eligible family keeps a positive share, s = 0 is the
    /// legacy shares), the one-NextDouble draw, a 10k-seed Monte Carlo of the drawn archetype mix against the analytic one,
    /// q pinned to the legacy draw's expected mix over the natural-band families (and a non-widened run fielding it), and the builder wiring (s = 0
    /// and a non-widened run are the legacy draw and never read the archetype profile; a widened run draws on the shares).
    /// No database, no dat, no PropertyManager: TestGameTables supplies the skill formulas the classifier reads.
    /// </summary>
    [TestClass]
    public class FamilyArchetypeWeightingTests
    {
        [ClassInitialize]
        public static void TestSetup(TestContext context) => TestGameTables.EnsureInitialized();

        private static int I(CreatureArchetype a) => Array.IndexOf(FamilyArchetypeWeighting.Order, a);

        private static double[] MixOf(params (CreatureArchetype A, double Share)[] parts)
        {
            var mix = new double[FamilyArchetypeWeighting.Order.Length];

            foreach (var (a, share) in parts)
                mix[I(a)] = share;

            return mix;
        }

        // ---- the pure weights -------------------------------------------------------------------------------

        [TestMethod]
        public void Single_archetype_pools_hit_the_natural_mix_exactly()
        {
            // Three single-archetype families, legacy weights 3 (preferred), 1, 1. The legacy mix is Caster 60%; the natural
            // band fields Caster 30%, MeleeTank 50%, Ranged 20%.
            var preference = new double[] { 3, 1, 1 };
            var mixes = new[] { MixOf((CreatureArchetype.Caster, 1.0)), MixOf((CreatureArchetype.MeleeTank, 1.0)), MixOf((CreatureArchetype.Ranged, 1.0)) };
            var q = MixOf((CreatureArchetype.Caster, 0.3), (CreatureArchetype.MeleeTank, 0.5), (CreatureArchetype.Ranged, 0.2));

            var shares = FamilyArchetypeWeighting.Weights(preference, mixes, q, 1.0);
            var expected = FamilyArchetypeWeighting.ExpectedMix(shares, mixes);

            for (var a = 0; a < q.Length; a++)
                Assert.AreEqual(q[a], expected[a], 1e-12, $"{FamilyArchetypeWeighting.Order[a]}");

            Assert.AreEqual(0.6, FamilyArchetypeWeighting.ExpectedMix(FamilyArchetypeWeighting.LegacyShares(preference), mixes)[I(CreatureArchetype.Caster)], 1e-12,
                "control: the legacy draw is 60% casters");

            // s blends linearly between the two.
            var half = FamilyArchetypeWeighting.Weights(preference, mixes, q, 0.5);
            Assert.AreEqual(0.45, FamilyArchetypeWeighting.ExpectedMix(half, mixes)[I(CreatureArchetype.Caster)], 1e-12);
        }

        [TestMethod]
        public void An_archetype_shared_by_several_families_is_split_by_legacy_weight()
        {
            var preference = new double[] { 3, 1 };
            var mixes = new[] { MixOf((CreatureArchetype.MeleeDps, 1.0)), MixOf((CreatureArchetype.MeleeDps, 1.0)) };
            var q = MixOf((CreatureArchetype.MeleeDps, 1.0));

            var shares = FamilyArchetypeWeighting.Weights(preference, mixes, q, 1.0);

            Assert.AreEqual(0.75, shares[0], 1e-12, "nothing to rebalance: the legacy 3:1 stands");
            Assert.AreEqual(0.25, shares[1], 1e-12);
        }

        [TestMethod]
        public void S_zero_is_the_legacy_shares_and_every_eligible_family_keeps_a_positive_share()
        {
            var preference = new double[] { 1, 1, 1 };
            var mixes = new[] { MixOf((CreatureArchetype.Caster, 1.0)), MixOf((CreatureArchetype.MeleeTank, 1.0)), MixOf((CreatureArchetype.Ranged, 1.0)) };
            var q = MixOf((CreatureArchetype.Caster, 1.0));   // the natural band fields only casters

            CollectionAssert.AreEqual(FamilyArchetypeWeighting.LegacyShares(preference), FamilyArchetypeWeighting.Weights(preference, mixes, q, 0.0));
            CollectionAssert.AreEqual(FamilyArchetypeWeighting.LegacyShares(preference), FamilyArchetypeWeighting.Weights(preference, mixes, q, double.NaN));

            var full = FamilyArchetypeWeighting.Weights(preference, mixes, q, 1.0);

            Assert.IsTrue(full.All(w => w > 0), "the 0.1 x legacy floor keeps every eligible family drawable");
            Assert.AreEqual(1.0, full.Sum(), 1e-12);

            // matched (1, 0, 0) floored to (1, 1/30, 1/30), renormalized.
            var floor = FamilyArchetypeWeighting.LegacyFloorFraction / 3.0;
            Assert.AreEqual(floor / (1 + 2 * floor), full[1], 1e-12);
            Assert.AreEqual(1 / (1 + 2 * floor), full[0], 1e-12);

            // Nothing matchable (q carries only an archetype no family offers): the legacy shares.
            CollectionAssert.AreEqual(FamilyArchetypeWeighting.LegacyShares(preference),
                FamilyArchetypeWeighting.Weights(preference, mixes, MixOf((CreatureArchetype.Hybrid, 1.0)), 1.0));
        }

        [TestMethod]
        public void Mixed_archetype_pools_land_no_further_from_the_natural_mix_than_the_closed_form()
        {
            // The Monte Carlo fixture's pools: Caster 2 + MeleeTank 4, MeleeTank 6, Ranged 3 + MeleeDps 3, MeleeDps 6, legacy
            // 3:1:1:1, q a flat 25% over the four. The closed form misses q (a mixed family drags mass along); the EM steps
            // from it must not do worse, and here do strictly better.
            var preference = new double[] { 3, 1, 1, 1 };
            var mixes = new[]
            {
                MixOf((CreatureArchetype.Caster, 2 / 6.0), (CreatureArchetype.MeleeTank, 4 / 6.0)),
                MixOf((CreatureArchetype.MeleeTank, 1.0)),
                MixOf((CreatureArchetype.Ranged, 0.5), (CreatureArchetype.MeleeDps, 0.5)),
                MixOf((CreatureArchetype.MeleeDps, 1.0)),
            };
            var q = MixOf((CreatureArchetype.Caster, 0.25), (CreatureArchetype.MeleeTank, 0.25), (CreatureArchetype.Ranged, 0.25), (CreatureArchetype.MeleeDps, 0.25));

            double L1(double[] w) => FamilyArchetypeWeighting.ExpectedMix(w, mixes).Zip(q, (m, t) => Math.Abs(m - t)).Sum();

            var closed = FamilyArchetypeWeighting.MatchedClosedForm(preference, mixes, q);
            var refined = FamilyArchetypeWeighting.Matched(preference, mixes, q);
            var legacy = FamilyArchetypeWeighting.LegacyShares(preference);

            Assert.AreEqual(1.0, refined.Sum(), 1e-12);
            Assert.IsTrue(L1(refined) <= L1(closed) + 1e-12, $"EM {L1(refined):0.000} vs closed form {L1(closed):0.000}");
            Assert.IsTrue(L1(refined) < L1(closed) - 0.05, $"strictly better on this fixture: EM {L1(refined):0.000} vs closed form {L1(closed):0.000}");
            Assert.IsTrue(L1(closed) < L1(legacy), "control: the closed form already beats the legacy draw");

            // deterministic: the same inputs give the same weights bit for bit
            CollectionAssert.AreEqual(refined, FamilyArchetypeWeighting.Matched(preference, mixes, q));
        }

        // ---- the draw ---------------------------------------------------------------------------------------

        private static SpeciesTableDef T(string id) => new SpeciesTableDef { Id = id, Members = new List<SpeciesMemberDef>() };

        [TestMethod]
        public void The_weighted_draw_consumes_exactly_one_NextDouble_and_walks_in_the_order_given()
        {
            var families = new[] { T("a"), T("b"), T("c") };
            var weights = new[] { 0.2, 0.0, 0.8 };

            for (var seed = 0; seed < 50; seed++)
            {
                var rng = new Random(seed);
                var control = new Random(seed);
                var roll = control.NextDouble();

                var drawn = DungeonRosterSelector.PickFamilyWeighted(families, weights, rng);

                Assert.AreEqual(roll < 0.2 ? "a" : "c", drawn.Id, $"seed {seed}");
                Assert.AreEqual(control.Next(), rng.Next(), "one NextDouble consumed, nothing more");
            }

            Assert.IsNull(DungeonRosterSelector.PickFamilyWeighted(families, new[] { 0.0, 0.0, double.NaN }, new Random(1)));
        }

        [TestMethod]
        public void A_10k_seed_Monte_Carlo_of_the_drawn_archetype_mix_is_within_two_percent_of_the_analytic_mix()
        {
            var archetypes = new Dictionary<uint, CreatureArchetype>();
            var pools = new List<List<uint>>();
            uint next = 1;

            List<uint> Pool(params (CreatureArchetype A, int Count)[] parts)
            {
                var pool = new List<uint>();

                foreach (var (a, count) in parts)
                    for (var i = 0; i < count; i++)
                    {
                        archetypes[next] = a;
                        pool.Add(next++);
                    }

                pools.Add(pool);
                return pool;
            }

            Pool((CreatureArchetype.Caster, 2), (CreatureArchetype.MeleeTank, 4));
            Pool((CreatureArchetype.MeleeTank, 6));
            Pool((CreatureArchetype.Ranged, 3), (CreatureArchetype.MeleeDps, 3));
            Pool((CreatureArchetype.MeleeDps, 6));

            var families = pools.Select((_, i) => T("f" + i)).ToList();
            var preference = new double[] { 3, 1, 1, 1 };
            var mixes = pools.Select(p => FamilyArchetypeWeighting.FamilyMix(p, w => archetypes[w])).ToList();
            var q = MixOf((CreatureArchetype.Caster, 0.25), (CreatureArchetype.MeleeTank, 0.25), (CreatureArchetype.Ranged, 0.25), (CreatureArchetype.MeleeDps, 0.25));

            var shares = FamilyArchetypeWeighting.Weights(preference, mixes, q, 1.0);
            var analytic = FamilyArchetypeWeighting.ExpectedMix(shares, mixes);
            var tally = new double[FamilyArchetypeWeighting.Order.Length];
            const int Seeds = 10000;

            for (var seed = 0; seed < Seeds; seed++)
            {
                var rng = new Random(seed);
                var family = DungeonRosterSelector.PickFamilyWeighted(families, shares, rng);
                var pool = pools[families.IndexOf(family)];

                tally[I(archetypes[pool[rng.Next(pool.Count)]])]++;
            }

            for (var a = 0; a < tally.Length; a++)
                Assert.AreEqual(analytic[a], tally[a] / Seeds, 0.02, $"{FamilyArchetypeWeighting.Order[a]}");
        }

        // ---- q: the mix an un-widened run fields ------------------------------------------------------------

        private static readonly Dictionary<PropertyAttribute, uint> Attrs = new Dictionary<PropertyAttribute, uint>
        {
            [PropertyAttribute.Strength] = 300, [PropertyAttribute.Coordination] = 300, [PropertyAttribute.Focus] = 300, [PropertyAttribute.Self] = 300,
        };

        private static DungeonStatProfile Caster(int level)
            => new DungeonStatProfile(level, 2000, new Dictionary<Skill, uint> { [Skill.WarMagic] = (uint)level, [Skill.MeleeDefense] = (uint)level }, 20, 100, 6, 0.0, Attrs,
                new ArchetypeFacts(null, false, false, new Dictionary<Skill, int> { [Skill.WarMagic] = 1 }, new[] { new SpellRoll(0.6, Skill.WarMagic) },
                    effectiveMaxDamage: 20, bestProjectileMean: 60));

        private static DungeonStatProfile Melee(int level)
            => new DungeonStatProfile(level, 6000, new Dictionary<Skill, uint> { [Skill.Sword] = (uint)level, [Skill.MeleeDefense] = (uint)level }, 60, 1000, 0, 0.0, Attrs,
                new ArchetypeFacts(null, false, false, null, effectiveMaxDamage: 60));

        /// <summary>
        /// Level-200 run, natural band [200, 230]. "nat" is the only family with in-band trash (three casters, three melee),
        /// so the eligibility band widens to x0.85, where the three all-melee families at 170-175 become eligible. Also in
        /// "nat": an elite, an out-of-band trash member and an in-band member with no data, none of which q may count.
        /// </summary>
        private static (Dictionary<string, SpeciesTableDef> Tables, Dictionary<uint, DungeonStatProfile> Profiles) Fixture()
        {
            var profiles = new Dictionary<uint, DungeonStatProfile>
            {
                [1] = Caster(200), [2] = Caster(205), [3] = Caster(210), [4] = Melee(205), [5] = Melee(210), [6] = Melee(215),
                [7] = Melee(212),                                       // role 1
                [8] = Melee(150),                                       // trash, below the natural band
                [9] = new DungeonStatProfile(205, 0, null, 0, 0),        // trash, in band, no data
            };

            SpeciesMemberDef Trash(uint w) => new SpeciesMemberDef { Wcid = w, Role = 0 };

            var tables = new Dictionary<string, SpeciesTableDef>
            {
                ["nat"] = new SpeciesTableDef
                {
                    Id = "nat",
                    Members = new List<SpeciesMemberDef> { Trash(1), Trash(2), Trash(3), Trash(4), Trash(5), Trash(6), new SpeciesMemberDef { Wcid = 7, Role = 1 }, Trash(8), Trash(9) },
                },
            };

            uint next = 20;

            foreach (var id in new[] { "lowa", "lowb", "lowc" })
            {
                var members = new List<SpeciesMemberDef>();

                for (var i = 0; i < 6; i++)
                {
                    profiles[next] = Melee(170 + i);
                    members.Add(Trash(next++));
                }

                tables[id] = new SpeciesTableDef { Id = id, Members = members };
            }

            return (tables, profiles);
        }

        private static readonly DungeonRosterBand Natural = new DungeonRosterBand(DungeonPopulationLimits.DefaultRosterBandLow, DungeonPopulationLimits.DefaultRosterBandHigh);

        // ---- the builder wiring -----------------------------------------------------------------------------

        private static ThreadDungeonStore Store() => ThreadDungeonStore.Parse(
            "{\"dungeons\":[]}",
            "{\"bosses\":[]}",
            "{\"modifiers\":[],\"xpLadder\":[{\"level\":20,\"xp\":3500},{\"level\":100,\"xp\":80000},{\"level\":200,\"xp\":1100000}]}",
            new Dictionary<string, string>());

        private static DungeonEntryDef Dungeon() => new DungeonEntryDef
        {
            Id = "filos_doom", Landblock = 0x0150, Name = "Filos' Doom", ExitPortalWcid = 1003601,
            Points = Enumerable.Range(1, 12).Select(i => new DungeonSpawnPointDef { Cell = 0x01500100u + (uint)i, X = i, Curated = true, Clearance = 2 }).ToList(),
            BossAnchor = new DungeonSpawnPointDef { Cell = 0x01500199u, X = 99, Curated = true, Clearance = 2 },
        };

        private static DungeonGemSpec Spec(int seed) => new DungeonGemSpec("filos_doom", 200, 6, DungeonGemSpec.Any, seed, new (string, double)[0], 0, 0);

        private static DungeonPopulationLimits Limits(double weighting, int minEligibleFamilies = DungeonPopulationLimits.DefaultMinEligibleFamilies)
            => new DungeonPopulationLimits(120, 2.0, 3.0, 8, 0.5, rewardScalingEnabled: false, minMonsters: 0, reachUp: false, statCurve: false,
                minEligibleFamilies: minEligibleFamilies, archetypeUplift: false, familyArchetypeWeighting: weighting);

        private static DungeonStatProfile Throwing(uint wcid) => throw new InvalidOperationException("the archetype profile must not be read");

        private static DungeonSpawnPlan BuildPlan((Dictionary<string, SpeciesTableDef> Tables, Dictionary<uint, DungeonStatProfile> Profiles) f, int seed,
            DungeonPopulationLimits limits, Func<uint, DungeonStatProfile> archetypeProfileOf, Random rng = null)
            => DungeonPopulationBuilder.Build(Spec(seed), Dungeon(), Store(), f.Tables, w => f.Profiles[w].Level, w => 1000u, limits, rng ?? new Random(seed),
                w => f.Profiles[w], archetypeProfileOf: archetypeProfileOf);

        private static string Shape(DungeonSpawnPlan plan)
            => $"{plan.FamilyId}|{string.Join(",", plan.Entries.Select(e => $"{e.Wcid}:{e.Role}:{e.Point?.Cell}:{e.UpliftLevel}"))}|{plan.BossWcid}|{string.Join(";", plan.Notes)}";

        [TestMethod]
        public void S_zero_is_the_legacy_family_and_the_same_next_rng_value_and_never_reads_the_profile()
        {
            var f = Fixture();

            for (var seed = 1; seed <= 20; seed++)
            {
                var legacyRng = new Random(seed);
                var offRng = new Random(seed);

                var legacy = BuildPlan(f, seed, Limits(0.0), null, legacyRng);
                var off = BuildPlan(f, seed, Limits(0.0), Throwing, offRng);

                Assert.IsTrue(legacy.Notes.Any(n => n.StartsWith("band: only")), "fixture: the eligibility band widens");
                Assert.AreEqual(Shape(legacy), Shape(off), $"seed {seed}");
                Assert.AreEqual(legacyRng.Next(), offRng.Next(), "the same rng consumption");
            }
        }

        [TestMethod]
        public void A_run_that_does_not_widen_never_reads_the_archetype_profile()
        {
            var f = Fixture();

            // One eligible family is enough: the natural band ("nat") already meets it, so nothing widens.
            var plan = BuildPlan(f, 3, Limits(1.0, minEligibleFamilies: 1), Throwing);

            Assert.AreEqual("nat", plan.FamilyId);
            Assert.IsFalse(plan.Notes.Any(n => n.StartsWith("family: archetype-weighted")));
        }

        [TestMethod]
        public void A_widened_run_draws_on_the_weighted_shares_and_notes_it()
        {
            var f = Fixture();
            DungeonStatProfile ProfileOf(uint w) => f.Profiles[w];

            var natural = Natural;
            var eligibility = new DungeonRosterBand(0.85, natural.High);
            var roster = new RosterArchetypes(f.Tables, natural, DungeonPopulationLimits.DefaultBandLowFloorRatio, ProfileOf);

            var draw = DungeonPopulationBuilder.FamilyWeighting(Spec(1), Dungeon(), f.Tables, w => f.Profiles[w].Level, natural, eligibility,
                DungeonPopulationLimits.DefaultBandLowFloorRatio, DungeonPopulationLimits.DefaultMinFamilyPool, 1.0, roster);

            CollectionAssert.AreEqual(new[] { "lowa", "lowb", "lowc", "nat" }, draw.Families.Select(t => t.Id).ToList(), "Id-ordinal");
            Assert.AreEqual(0.25, draw.LegacyShares[3], 1e-12);
            Assert.IsTrue(draw.Shares[3] > 0.5, $"the only family carrying the un-widened run's casters is drawn far more often ({draw.Shares[3]})");
            Assert.IsTrue(draw.ExpectedMix[I(CreatureArchetype.Caster)] > draw.LegacyMix[I(CreatureArchetype.Caster)]);

            // The builder draws on exactly those shares: 2000 seeds' family frequencies track them.
            var counts = draw.Families.ToDictionary(t => t.Id, t => 0);
            const int Seeds = 2000;

            for (var seed = 1; seed <= Seeds; seed++)
            {
                var plan = BuildPlan(f, seed, Limits(1.0), ProfileOf);
                counts[plan.FamilyId]++;

                if (seed == 1)
                    Assert.AreEqual(1, plan.Notes.Count(n => n.StartsWith("family: archetype-weighted draw x1 over 4 eligible families")), string.Join(" / ", plan.Notes));
            }

            for (var i = 0; i < draw.Families.Count; i++)
                Assert.AreEqual(draw.Shares[i], counts[draw.Families[i].Id] / (double)Seeds, 0.04, draw.Families[i].Id);
        }

        // ---- q: the un-widened run's fielded mix -------------------------------------------------------------

        /// <summary>
        /// Level-200 run, natural band [200, 230]. Two families field trash in the natural band: "nat" (three casters, three
        /// melee) and "nat2" (four melee, PREFERRED by the dungeon, weight 3); three all-melee families at 170-175 are only
        /// eligible once the band widens.
        /// </summary>
        private static (Dictionary<string, SpeciesTableDef> Tables, Dictionary<uint, DungeonStatProfile> Profiles) TwoFamilyFixture()
        {
            var profiles = new Dictionary<uint, DungeonStatProfile>
            {
                [1] = Caster(200), [2] = Caster(205), [3] = Caster(210), [4] = Melee(205), [5] = Melee(210), [6] = Melee(215),
                [11] = Melee(201), [12] = Melee(206), [13] = Melee(211), [14] = Melee(216),
                [7] = Melee(222), [15] = Melee(222),                    // role 1: each family's elite, the boss candidate
            };

            SpeciesMemberDef Trash(uint w) => new SpeciesMemberDef { Wcid = w, Role = 0 };

            var tables = new Dictionary<string, SpeciesTableDef>
            {
                ["nat"] = new SpeciesTableDef { Id = "nat", Members = new List<SpeciesMemberDef> { Trash(1), Trash(2), Trash(3), Trash(4), Trash(5), Trash(6), new SpeciesMemberDef { Wcid = 7, Role = 1 } } },
                ["nat2"] = new SpeciesTableDef { Id = "nat2", Members = new List<SpeciesMemberDef> { Trash(11), Trash(12), Trash(13), Trash(14), new SpeciesMemberDef { Wcid = 15, Role = 1 } } },
            };

            uint next = 20;

            foreach (var id in new[] { "lowa", "lowb", "lowc" })
            {
                var members = new List<SpeciesMemberDef>();

                for (var i = 0; i < 6; i++)
                {
                    profiles[next] = Melee(170 + i);
                    members.Add(Trash(next++));
                }

                tables[id] = new SpeciesTableDef { Id = id, Members = members };
            }

            return (tables, profiles);
        }

        private static DungeonEntryDef PreferringNat2()
        {
            var dungeon = Dungeon();
            dungeon.Families = new List<string> { "nat2" };
            return dungeon;
        }

        [TestMethod]
        public void Q_is_the_legacy_draws_expected_mix_over_the_families_eligible_in_the_natural_band()
        {
            var f = TwoFamilyFixture();
            DungeonStatProfile ProfileOf(uint w) => f.Profiles[w];

            var roster = new RosterArchetypes(f.Tables, Natural, DungeonPopulationLimits.DefaultBandLowFloorRatio, ProfileOf);
            var caster = roster.Classify(1).Archetype;

            foreach (var w in new uint[] { 4, 5, 6, 11, 12, 13, 14 })
                Assert.AreNotEqual(caster, roster.Classify(w).Archetype, $"fixture: melee wcid {w} is not a caster");

            var draw = DungeonPopulationBuilder.FamilyWeighting(Spec(1), PreferringNat2(), f.Tables, w => f.Profiles[w].Level, Natural,
                new DungeonRosterBand(0.85, Natural.High), DungeonPopulationLimits.DefaultBandLowFloorRatio, DungeonPopulationLimits.DefaultMinFamilyPool, 1.0, roster);

            CollectionAssert.AreEqual(new[] { "nat", "nat2" }, draw.NaturalFamilies.Select(t => t.Id).ToList(), "only the natural-band families, Id-ordinal");

            // Analytic: legacy shares 1/4 (nat) and 3/4 (nat2, preferred), each pool drawn uniformly:
            // q(a) = 1/4 x (nat members of a) / 6 + 3/4 x (nat2 members of a) / 4.
            var analytic = new double[FamilyArchetypeWeighting.Order.Length];

            foreach (var w in new uint[] { 1, 2, 3, 4, 5, 6 })
                analytic[I(roster.Classify(w).Archetype)] += 0.25 / 6;

            foreach (var w in new uint[] { 11, 12, 13, 14 })
                analytic[I(roster.Classify(w).Archetype)] += 0.75 / 4;

            Assert.AreEqual(0.125, analytic[I(caster)], 1e-12, "fixture: q(caster) = 1/4 x 1/2");

            for (var a = 0; a < analytic.Length; a++)
                Assert.AreEqual(analytic[a], draw.NaturalMix[a], 1e-12, $"q({FamilyArchetypeWeighting.Order[a]})");

            // The widened draw over five families: the matched weights (before the floor) land on q; hitting it exactly needs
            // the three all-MeleeTank low families at 0, so the 0.1 x legacy floor (3 x 1/70 of the draw, all MeleeTank)
            // leaves the final mix up to that much MeleeTank-heavy - and still far nearer q than the legacy draw.
            double L1(double[] m) => m.Zip(draw.NaturalMix, (x, y) => Math.Abs(x - y)).Sum();

            var matchedMix = FamilyArchetypeWeighting.ExpectedMix(FamilyArchetypeWeighting.Matched(draw.LegacyShares, draw.FamilyMixes, draw.NaturalMix), draw.FamilyMixes);

            for (var a = 0; a < draw.NaturalMix.Length; a++)
            {
                Assert.AreEqual(draw.NaturalMix[a], matchedMix[a], 1e-3, $"matched {FamilyArchetypeWeighting.Order[a]}: q {draw.NaturalMix[a]:0.0000} matched {matchedMix[a]:0.0000}");
                Assert.AreEqual(draw.NaturalMix[a], draw.ExpectedMix[a], 3.0 / 70 + 1e-3,
                    $"{FamilyArchetypeWeighting.Order[a]}: q {draw.NaturalMix[a]:0.0000} weighted {draw.ExpectedMix[a]:0.0000} legacy {draw.LegacyMix[a]:0.0000}");
            }

            Assert.IsTrue(L1(draw.ExpectedMix) < L1(draw.LegacyMix), $"weighted L1 {L1(draw.ExpectedMix):0.0000} vs legacy {L1(draw.LegacyMix):0.0000}");

            // No natural-band family at all: nothing to target, the caller takes the legacy draw.
            var lowOnly = f.Tables.Where(kv => kv.Key.StartsWith("low")).ToDictionary(kv => kv.Key, kv => kv.Value);
            Assert.IsNull(DungeonPopulationBuilder.FamilyWeighting(Spec(1), PreferringNat2(), lowOnly, w => f.Profiles[w].Level, Natural,
                new DungeonRosterBand(0.85, Natural.High), DungeonPopulationLimits.DefaultBandLowFloorRatio, DungeonPopulationLimits.DefaultMinFamilyPool, 1.0, roster));
        }

        [TestMethod]
        public void A_run_that_does_not_widen_fields_q_within_two_percent()
        {
            // Positive control for the target: with two eligible families enough, nothing widens, the legacy draw runs, and
            // the trash it fields over 2000 seeds matches the q the weighting would aim a widened run at.
            var f = TwoFamilyFixture();
            DungeonStatProfile ProfileOf(uint w) => f.Profiles[w];

            var roster = new RosterArchetypes(f.Tables, Natural, DungeonPopulationLimits.DefaultBandLowFloorRatio, ProfileOf);
            var q = DungeonPopulationBuilder.UnwidenedMix(Spec(1), PreferringNat2(), f.Tables, w => f.Profiles[w].Level, Natural,
                DungeonPopulationLimits.DefaultBandLowFloorRatio, DungeonPopulationLimits.DefaultMinFamilyPool, w => roster.Classify(w).Archetype).Mix;

            var tally = new double[FamilyArchetypeWeighting.Order.Length];
            var picks = 0;
            const int Seeds = 2000;


            for (var seed = 1; seed <= Seeds; seed++)
            {
                var plan = DungeonPopulationBuilder.Build(Spec(seed), PreferringNat2(), Store(), f.Tables, w => f.Profiles[w].Level, w => 1000u, Limits(1.0, minEligibleFamilies: 2),
                    new Random(seed), w => f.Profiles[w], archetypeProfileOf: ProfileOf);

                if (seed == 1)
                    Assert.IsFalse(plan.Notes.Any(n => n.StartsWith("band: only") || n.StartsWith("family: archetype-weighted")), string.Join(" / ", plan.Notes));


                foreach (var entry in plan.Entries.Where(e => e.Role == DungeonRole.Trash))
                {

                    tally[I(roster.Classify(entry.Wcid).Archetype)]++;
                    picks++;
                }
            }

            Assert.IsTrue(picks > Seeds, "fixture: runs field trash");

            for (var a = 0; a < tally.Length; a++)
                Assert.AreEqual(q[a], tally[a] / picks, 0.02, $"{FamilyArchetypeWeighting.Order[a]}: q {q[a]:0.000} fielded {tally[a] / picks:0.000} over {picks} picks");
        }    }
}
