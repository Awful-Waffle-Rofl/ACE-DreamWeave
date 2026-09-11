using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;
using ACE.Server.WorldEvents.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    [TestClass]
    public class DungeonPopulationBuilderTests
    {
        private static readonly Dictionary<uint, int> Levels = new Dictionary<uint, int> { [100] = 100, [101] = 120, [110] = 130, [10981] = 110 };
        private static int LevelOf(uint w) => Levels[w];

        /// <summary>
        /// No health data for any of these tests' fixtures: TrashHealthFloor resolves to 0 (an empty-pool
        /// no-op, DungeonRosterSelector.BandMedianHealth), so every test above that predates the trash health
        /// floor keeps its original, unfloored behaviour. Health-floor-specific coverage lives in
        /// DungeonRosterSelectorTests and DungeonRosterBandTests (BandMedianHealth itself) and in the
        /// dedicated tests below (the plan-time TrashHealthFloor computation).
        /// </summary>
        private static uint HealthOf(uint w) => 0;

        private static DungeonSpawnPointDef Pt(int i, bool curated = true) => new DungeonSpawnPointDef { Cell = 0x01500100u + (uint)i, X = i, Y = 0, Z = 0, Clearance = 2, Curated = curated };

        private static DungeonEntryDef Dungeon(int points) => new DungeonEntryDef
        {
            Id = "filos_doom", Landblock = 0x0150, Name = "Filos' Doom", ExitPortalWcid = 1003601,
            Points = Enumerable.Range(1, points).Select(i => Pt(i)).ToList(),
            BossAnchor = Pt(99),
            CreatureTypes = new List<string> { "Banderling" },
        };

        private static Dictionary<string, SpeciesTableDef> Species() => new Dictionary<string, SpeciesTableDef>
        {
            ["banderling"] = new SpeciesTableDef
            {
                Id = "banderling", CreatureType = "Banderling",
                Members = new List<SpeciesMemberDef> { new SpeciesMemberDef { Wcid = 100, Role = 0 }, new SpeciesMemberDef { Wcid = 101, Role = 0 }, new SpeciesMemberDef { Wcid = 110, Role = 1 } }
            }
        };

        private static ThreadDungeonStore Store() => ThreadDungeonStore.Parse(
            "{\"dungeons\":[]}",
            "{\"bosses\":[{\"wcid\":10981,\"name\":\"Aun Tanua\",\"level\":110,\"families\":[],\"modifiers\":[\"boss_guarded\"]}]}",
            "{\"modifiers\":[" +
            "{\"id\":\"hardy\",\"target\":\"monster\",\"minMagnitude\":1.3,\"maxMagnitude\":2.0,\"monsterEffectKind\":\"health_mult\",\"rewardXpKind\":\"linear\",\"rewardXpBase\":1.0,\"rewardXpSlope\":0.25}," +
            "{\"id\":\"teeming\",\"target\":\"run\",\"minMagnitude\":1.25,\"maxMagnitude\":1.75,\"monsterEffectKind\":\"count_mult\"}," +
            "{\"id\":\"boss_guarded\",\"target\":\"boss\",\"minMagnitude\":1.5,\"maxMagnitude\":3.0,\"monsterEffectKind\":\"health_mult\"}]," +
            "\"xpLadder\":[{\"level\":20,\"xp\":3500},{\"level\":100,\"xp\":80000},{\"level\":200,\"xp\":1100000}]}",
            new Dictionary<string, string>());

        // rewardScalingEnabled: false - this file's tests are written against the RAW xpScale/lumScale/loot
        // values, not the gem-level reward-scale curve (see DungeonRewardScaleTests for that). Most specs
        // here carry level 100, far below the curve's anchor (300), so leaving scaling on would silently
        // ratio every one of these pre-existing expectations down.
        private static DungeonPopulationLimits Limits(int max = 120, double trashHealthFloorRatio = DungeonPopulationLimits.DefaultTrashHealthFloorRatio)
            => new DungeonPopulationLimits(max, 2.0, 3.0, 8, 0.5,
                rewardScalingEnabled: false, trashHealthFloorRatio: trashHealthFloorRatio);

        private static DungeonGemSpec Spec(params (string, double)[] mods) => new DungeonGemSpec("filos_doom", 100, 6, "any", 1, mods, 0, 0);

        [TestMethod]
        public void Every_curated_point_gets_one_creature_and_the_boss_takes_the_anchor()
        {
            var plan = DungeonPopulationBuilder.Build(Spec(), Dungeon(12), Store(), Species(), LevelOf, HealthOf, Limits(), new Random(3));
            Assert.AreEqual(12, plan.Entries.Count(e => e.Role != DungeonRole.Boss));
            Assert.AreEqual(1, plan.Entries.Count(e => e.Role == DungeonRole.Boss));
            Assert.AreEqual(10981u, plan.BossWcid);
            Assert.AreEqual(12 + 1, plan.Entries.Select(e => e.Point).Distinct().Count(), "no point reused");
            Assert.AreEqual(99, (int)plan.Entries.Single(e => e.Role == DungeonRole.Boss).Point.X);
            Assert.AreEqual("banderling", plan.FamilyId);
        }

        [TestMethod]
        public void Uncurated_points_are_skipped()
        {
            var d = Dungeon(6);
            d.Points[0].Curated = false;
            var plan = DungeonPopulationBuilder.Build(Spec(), d, Store(), Species(), LevelOf, HealthOf, Limits(), new Random(3));
            Assert.AreEqual(5, plan.Entries.Count(e => e.Role != DungeonRole.Boss));
        }

        [TestMethod]
        public void Count_mult_never_exceeds_points_or_max()
        {
            var plan = DungeonPopulationBuilder.Build(Spec(("teeming", 1.75)), Dungeon(10), Store(), Species(), LevelOf, HealthOf, Limits(), new Random(3));
            Assert.AreEqual(10, plan.Entries.Count(e => e.Role != DungeonRole.Boss), "count_mult cannot invent points");
            var capped = DungeonPopulationBuilder.Build(Spec(), Dungeon(50), Store(), Species(), LevelOf, HealthOf, Limits(max: 8), new Random(3));
            Assert.AreEqual(8, capped.Entries.Count(e => e.Role != DungeonRole.Boss));
        }

        [TestMethod]
        public void Health_and_xp_come_from_the_math()
        {
            var plan = DungeonPopulationBuilder.Build(Spec(("hardy", 1.6)), Dungeon(4), Store(), Species(), LevelOf, HealthOf, Limits(), new Random(3));
            Assert.AreEqual(1.6, plan.HealthMultiplier, 1e-9);
            Assert.AreEqual(1.6 * 1.5, plan.BossHealthMultiplier, 1e-9, "boss folds boss_guarded at its MinMagnitude");
            Assert.AreEqual(1.4, plan.XpMultiplier, 1e-9);
            Assert.AreEqual(6, plan.Profile.Tier);
            Assert.AreEqual(0u, plan.Profile.Id);
        }

        // ---- TrashHealthFloor -----------------------------------------------------------------------------

        /// <summary>
        /// Species() has trash wcids 100 (level 100) and 101 (level 120). Spec()'s level-100 trash band is
        /// [100, 115] (DungeonRosterSelector.BandLow/BandHigh = 1.0/1.15, narrowed from 1.5 on 2026-09-08), so
        /// only wcid 100 is in band now - wcid 101 sits just outside it.
        /// </summary>
        private static Dictionary<uint, uint> TrashHealthByWcid => new Dictionary<uint, uint> { [100] = 4000, [101] = 6000 };
        private static uint TrashHealthOf(uint w) => TrashHealthByWcid.TryGetValue(w, out var h) ? h : 0;

        [TestMethod]
        public void Trash_health_floor_is_the_band_median_times_the_ratio()
        {
            // Pool: {4000} only (wcid 101's 6000 is excluded -- level 120 is outside the narrower [100, 115]
            // band). Single-element median is 4000. Ratio 0.5 -> floor 2000.
            var plan = DungeonPopulationBuilder.Build(Spec(), Dungeon(4), Store(), Species(), LevelOf, TrashHealthOf, Limits(trashHealthFloorRatio: 0.5), new Random(3));
            Assert.AreEqual(2000u, plan.TrashHealthFloor);
        }

        [TestMethod]
        public void A_ratio_of_zero_disables_the_trash_health_floor()
        {
            var plan = DungeonPopulationBuilder.Build(Spec(), Dungeon(4), Store(), Species(), LevelOf, TrashHealthOf, Limits(trashHealthFloorRatio: 0), new Random(3));
            Assert.AreEqual(0u, plan.TrashHealthFloor);
        }

        [TestMethod]
        public void A_negative_or_nan_ratio_reaching_the_builder_disables_the_floor_rather_than_throwing()
        {
            // Mirrors BossHealthFloorRatio's own contract: sanitizing a garbled dial is the READER's job
            // (ThreadDungeonSpawner.ReadDoubleDial); a negative/NaN value reaching a limits object built
            // directly in code (as every test here does) is treated as "disabled", never propagated.
            var negative = DungeonPopulationBuilder.Build(Spec(), Dungeon(4), Store(), Species(), LevelOf, TrashHealthOf, Limits(trashHealthFloorRatio: -1.0), new Random(3));
            Assert.AreEqual(0u, negative.TrashHealthFloor);

            var nan = DungeonPopulationBuilder.Build(Spec(), Dungeon(4), Store(), Species(), LevelOf, TrashHealthOf, Limits(trashHealthFloorRatio: double.NaN), new Random(3));
            Assert.AreEqual(0u, nan.TrashHealthFloor);
        }

        [TestMethod]
        public void An_empty_health_pool_yields_a_zero_floor()
        {
            var plan = DungeonPopulationBuilder.Build(Spec(), Dungeon(4), Store(), Species(), LevelOf, HealthOf, Limits(trashHealthFloorRatio: 0.5), new Random(3));
            Assert.AreEqual(0u, plan.TrashHealthFloor, "HealthOf here always returns 0, so the pool has no usable data");
        }

        [TestMethod]
        public void No_eligible_family_yields_an_empty_plan_with_a_note()
        {
            var spec = new DungeonGemSpec("filos_doom", 250, 6, "any", 1, new (string, double)[0], 0, 0);
            var plan = DungeonPopulationBuilder.Build(spec, Dungeon(4), Store(), Species(), LevelOf, HealthOf, Limits(), new Random(3));
            Assert.AreEqual(0, plan.Entries.Count(e => e.Role != DungeonRole.Boss));
            Assert.IsTrue(plan.Notes.Any(n => n.Contains("family")));
        }

        /// <summary>
        /// The CURATED row is still rejected when its level falls outside the gem's boss window - that is
        /// what this test has always guarded and still does. What changed on 2026-09-06 is what happens
        /// next: the run no longer goes bossless, it promotes the family's own best member and applies the
        /// same uplift, so the curated pool is a preference rather than the guarantee.
        /// </summary>
        [TestMethod]
        public void Boss_outside_the_level_window_is_not_chosen()
        {
            var spec = new DungeonGemSpec("filos_doom", 40, 3, "any", 1, new (string, double)[0], 0, 0);
            var species = Species();
            Levels[100] = 40; Levels[101] = 45; Levels[110] = 50;
            try
            {
                var plan = DungeonPopulationBuilder.Build(spec, Dungeon(4), Store(), species, LevelOf, HealthOf, Limits(), new Random(3));

                Assert.AreNotEqual(10981u, plan.BossWcid, "the curated Aun Tanua (level 110) is outside a level-40 gem's window");
                Assert.IsTrue(plan.Notes.Any(n => n.Contains("no curated row eligible")), "the refusal is recorded");

                Assert.AreNotEqual(0u, plan.BossWcid, "a promoted boss takes its place");
                Assert.IsTrue(species["banderling"].Members.Any(m => m.Wcid == plan.BossWcid), "promoted from the run's own family");
                Assert.IsTrue(plan.Notes.Any(n => n.Contains("promoted wcid " + plan.BossWcid)));
                AssertBossInvariants(plan, spec, Limits());
            }
            finally { Levels[100] = 100; Levels[101] = 120; Levels[110] = 130; }
        }

        // ---- additions beyond the brief's six ----

        /// <summary>
        /// The run's clear rule (owner ruling R28) derives TrashSpawned/TrashKilled by subtracting the boss
        /// slot back out of Spawned/Killed, and ThreadDungeonSpawner counts EVERY plan entry - boss included -
        /// into MarkPopulated's planned/spawned. So the boss must be one of Entries, not a field alongside
        /// them, or the ledger and the plan disagree by one.
        /// </summary>
        [TestMethod]
        public void The_boss_is_an_entry_so_the_kill_ledger_can_count_it()
        {
            var plan = DungeonPopulationBuilder.Build(Spec(), Dungeon(5), Store(), Species(), LevelOf, HealthOf, Limits(), new Random(7));
            Assert.AreEqual(6, plan.Entries.Count, "5 trash/elite + the boss");
            Assert.AreEqual(1, plan.Entries.Count(e => e.Wcid == plan.BossWcid && e.Role == DungeonRole.Boss));
        }

        /// <summary>
        /// A bosses.json entry that writes "families": null deserialises to a null list (ThreadDungeonStore
        /// normalises Modifiers but not Families), so the builder must read it null-safely: a null Families
        /// means "any family", exactly like an empty one.
        /// </summary>
        [TestMethod]
        public void A_boss_with_null_families_is_treated_as_any_family()
        {
            var store = ThreadDungeonStore.Parse(
                "{\"dungeons\":[]}",
                "{\"bosses\":[{\"wcid\":10981,\"name\":\"Aun Tanua\",\"level\":110,\"families\":null,\"modifiers\":[]}]}",
                "{\"modifiers\":[],\"xpLadder\":[{\"level\":20,\"xp\":3500},{\"level\":200,\"xp\":1100000}]}",
                new Dictionary<string, string>());

            Assert.IsNull(store.Bosses[0].Families, "guard: the store leaves Families null");

            var plan = DungeonPopulationBuilder.Build(Spec(), Dungeon(4), store, Species(), LevelOf, HealthOf, Limits(), new Random(3));
            Assert.AreEqual(10981u, plan.BossWcid);
        }

        /// <summary>
        /// A boss whose Families names a different family is not eligible - unchanged, and still the point of
        /// this test. Since 2026-09-06 the run then promotes from its own family rather than standing empty,
        /// so the assertion is "not the curated one, and a promoted one instead".
        /// </summary>
        [TestMethod]
        public void A_boss_bound_to_another_family_is_not_chosen()
        {
            var store = ThreadDungeonStore.Parse(
                "{\"dungeons\":[]}",
                "{\"bosses\":[{\"wcid\":10981,\"name\":\"Aun Tanua\",\"level\":110,\"families\":[\"olthoi\"],\"modifiers\":[]}]}",
                "{\"modifiers\":[],\"xpLadder\":[{\"level\":20,\"xp\":3500},{\"level\":200,\"xp\":1100000}]}",
                new Dictionary<string, string>());

            var plan = DungeonPopulationBuilder.Build(Spec(), Dungeon(4), store, Species(), LevelOf, HealthOf, Limits(), new Random(3));

            Assert.AreNotEqual(10981u, plan.BossWcid, "the curated row is bound to the olthoi family");
            Assert.IsTrue(plan.Notes.Any(n => n.Contains("no curated row eligible")));

            Assert.AreNotEqual(0u, plan.BossWcid, "a promoted boss takes its place");
            Assert.IsTrue(Species()["banderling"].Members.Any(m => m.Wcid == plan.BossWcid), "promoted from the run's own family");
            AssertBossInvariants(plan, Spec(), Limits());
        }

        /// <summary>
        /// A dungeon whose bossAnchor is also listed in points must not spawn two creatures on it. The store
        /// deserialises "points" and "bossAnchor" separately, so the duplicate arrives as a DIFFERENT object
        /// with the SAME cell and offset - a reference check would miss it and stack the boss on a trash slot.
        /// </summary>
        [TestMethod]
        public void An_anchor_that_is_also_a_listed_point_is_not_reused()
        {
            var d = Dungeon(6);
            d.BossAnchor = Pt(3);   // a separate object holding exactly the location of d.Points[2]

            var plan = DungeonPopulationBuilder.Build(Spec(), d, Store(), Species(), LevelOf, HealthOf, Limits(), new Random(3));

            Assert.AreEqual(5, plan.Entries.Count(e => e.Role != DungeonRole.Boss));
            Assert.AreEqual(plan.Entries.Count, plan.Entries.Select(e => (e.Point.Cell, e.Point.X, e.Point.Y, e.Point.Z)).Distinct().Count(), "no location reused");
        }

        /// <summary>Same gem seed, same plan - a re-roll of the same gem must place the same dungeon.</summary>
        [TestMethod]
        public void The_same_seed_produces_the_same_plan()
        {
            var a = DungeonPopulationBuilder.Build(Spec(), Dungeon(12), Store(), Species(), LevelOf, HealthOf, Limits(), new Random(11));
            var b = DungeonPopulationBuilder.Build(Spec(), Dungeon(12), Store(), Species(), LevelOf, HealthOf, Limits(), new Random(11));

            CollectionAssert.AreEqual(
                a.Entries.Select(e => e.Wcid + ":" + e.Role + ":" + e.Point.X).ToList(),
                b.Entries.Select(e => e.Wcid + ":" + e.Role + ":" + e.Point.X).ToList());
        }

        /// <summary>
        /// Luminance is derived from the drawn creature's own retail XP by the fork's luminance standard
        /// (monster_patterns.md, adopted 2026-08-17), not from a flat award of its own. Two things this pins:
        /// the gate is the DRAWN CREATURE's level at the standard's floor of 185, and a higher-level draw -
        /// which is a higher-XP draw - pays proportionally more.
        /// </summary>
        [TestMethod]
        public void Luminance_is_derived_from_the_creatures_own_xp_and_gated_at_185()
        {
            var plan = DungeonPopulationBuilder.Build(Spec(), Dungeon(4), Store(), Species(), LevelOf, HealthOf, Limits(), new Random(3));

            Assert.AreEqual(1.0, plan.LumMultiplier, 1e-9, "guard: no lum modifiers on this gem");
            Assert.AreEqual(DungeonPopulationLimits.DefaultLumScale, plan.LumScale, 1e-9, "guard: the plan carries the shipped scale");
            Assert.AreEqual(2.0, plan.LumScale, 1e-9, "guard: the shipped scale these expectations are written against");
            Assert.AreEqual(185, DungeonPopulationBuilder.LuminanceBaseLevel, "guard: the standard's floor");

            // Below the floor: nothing, however much XP the creature is worth.
            Assert.AreEqual(0, DungeonPopulationBuilder.LuminanceFor(plan, 800000, 184, DungeonRole.Trash));

            // At the floor: 800,000 / 4,000 = 200 lum base, then role and scale.
            Assert.AreEqual(400, DungeonPopulationBuilder.LuminanceFor(plan, 800000, 185, DungeonRole.Trash), "200 x 1.0 x 2.0");
            Assert.AreEqual(600, DungeonPopulationBuilder.LuminanceFor(plan, 800000, 185, DungeonRole.Elite), "200 x 1.5 x 2.0");
            Assert.AreEqual(800, DungeonPopulationBuilder.LuminanceFor(plan, 800000, 185, DungeonRole.Boss), "200 x 2.0 x 2.0");

            // A higher draw is worth more XP and so pays proportionally more luminance.
            Assert.AreEqual(600, DungeonPopulationBuilder.LuminanceFor(plan, 1100000, 200, DungeonRole.Trash), "300 x 1.0 x 2.0");
            Assert.IsTrue(DungeonPopulationBuilder.LuminanceFor(plan, 1100000, 200, DungeonRole.Trash)
                        > DungeonPopulationBuilder.LuminanceFor(plan, 800000, 185, DungeonRole.Trash),
                "a higher-level draw must pay more luminance, not the same flat award");
        }

        /// <summary>
        /// The standard's quotient is rounded HALF-UP to the nearest 50. .NET's default Math.Round is
        /// banker's rounding, which disagrees at every odd multiple of 25 in the quotient, and every other
        /// case in the standard's table agrees - so a bare Math.Round passes everything except this.
        ///
        /// L200 is the case monster_patterns.md itself calls out (1,100,000 / 4,000 = 275 becomes 300). Note
        /// that it does NOT actually discriminate the two modes: 275/50 = 5.5 and .NET rounds 5.5 to 6 under
        /// both, because 6 is even. L265 is the case that does: 2,500,000 / 4,000 / 50 = 12.5, which is 13
        /// half-up (650 lum, the value the standard's table publishes) and 12 to-even (600).
        ///
        /// LumScale is forced to 1.0 here so the assertions read as the standard's own published numbers.
        /// </summary>
        [TestMethod]
        public void Luminance_rounds_half_up_to_the_nearest_fifty()
        {
            var plan = DungeonPopulationBuilder.Build(Spec(), Dungeon(4), Store(), Species(), LevelOf, HealthOf, Limits(), new Random(3));
            plan.LumScale = 1.0;

            Assert.AreEqual(200, DungeonPopulationBuilder.LuminanceFor(plan, 800000, 185, DungeonRole.Trash), "L185 from the standard's table");
            Assert.AreEqual(300, DungeonPopulationBuilder.LuminanceFor(plan, 1100000, 200, DungeonRole.Trash), "L200: 275 rounds half-up to 300");
            Assert.AreEqual(650, DungeonPopulationBuilder.LuminanceFor(plan, 2500000, 265, DungeonRole.Trash), "L265: 12.5 rounds half-up to 13; banker's would give 600");
            Assert.AreEqual(3400, DungeonPopulationBuilder.LuminanceFor(plan, 13500000, 450, DungeonRole.Trash), "L450: 67.5 rounds half-up to 68; banker's would give 3400 too");
            Assert.AreEqual(291950, DungeonPopulationBuilder.LuminanceFor(plan, 1167700000, 1000, DungeonRole.Trash), "L1000: 5838.5 rounds half-up to 5839; banker's would give 291900");
        }

        /// <summary>
        /// The result is deliberately NOT re-snapped to the 50-grid after role and scale are applied: 200 x
        /// 1.5 x 2.0 is 600, which happens to already land on the grid at the shipped 2.0 default - LumScale
        /// is bumped to 2.7 here so the assertion still exercises a non-grid result (200 x 1.5 x 2.7 = 810).
        /// </summary>
        [TestMethod]
        public void Luminance_is_not_re_snapped_to_the_fifty_grid()
        {
            var plan = DungeonPopulationBuilder.Build(Spec(), Dungeon(4), Store(), Species(), LevelOf, HealthOf, Limits(), new Random(3));
            plan.LumScale = 2.7;

            var elite = DungeonPopulationBuilder.LuminanceFor(plan, 800000, 185, DungeonRole.Elite);
            Assert.AreEqual(810, elite);
            Assert.AreNotEqual(0, elite % DungeonPopulationBuilder.LuminanceRounding, "a re-snapped result would land on the grid and lose the role rate");
        }

        /// <summary>
        /// A salvage-affinity modifier changes the corpse, never the population. The roll it implies is taken
        /// per kill off ThreadSafeRandom at the death path, so the gem's seeded Random is never touched and a
        /// gem carrying only affinity modifiers must draw exactly the plan a bare gem draws.
        ///
        /// This is the shape a mistake here would take: resolving the affinity through the builder's own rng,
        /// which would advance it and silently re-roll every creature and every point in the run.
        /// </summary>
        [TestMethod]
        public void An_affinity_modifier_leaves_the_population_plan_untouched()
        {
            var store = ThreadDungeonStore.Parse(
                "{\"dungeons\":[]}",
                "{\"bosses\":[{\"wcid\":10981,\"name\":\"Aun Tanua\",\"level\":110,\"families\":[],\"modifiers\":[]}]}",
                "{\"modifiers\":[{\"id\":\"affinity_tourmaline\",\"target\":\"monster\",\"minMagnitude\":5,\"maxMagnitude\":25,\"monsterEffectKind\":\"salvage_affinity\",\"salvageMaterial\":43,\"salvageBaseWcid\":2398}]," +
                "\"xpLadder\":[{\"level\":20,\"xp\":3500},{\"level\":100,\"xp\":80000},{\"level\":200,\"xp\":1100000}]}",
                new Dictionary<string, string>());

            Assert.AreEqual(1, store.Modifiers.Count, "guard: the affinity row survived validation " + string.Join("\n", store.Diagnostics));

            var bare = DungeonPopulationBuilder.Build(Spec(), Dungeon(12), store, Species(), LevelOf, HealthOf, Limits(), new Random(11));
            var affinity = DungeonPopulationBuilder.Build(Spec(("affinity_tourmaline", 25)), Dungeon(12), store, Species(), LevelOf, HealthOf, Limits(), new Random(11));

            CollectionAssert.AreEqual(
                bare.Entries.Select(e => e.Wcid + ":" + e.Role + ":" + e.Point.X).ToList(),
                affinity.Entries.Select(e => e.Wcid + ":" + e.Role + ":" + e.Point.X).ToList(),
                "an affinity modifier must not perturb the seeded draw");

            Assert.AreEqual(bare.BossWcid, affinity.BossWcid);
            Assert.AreEqual(bare.BossLevel, affinity.BossLevel);
            Assert.AreEqual(bare.HealthMultiplier, affinity.HealthMultiplier, 1e-9);
            Assert.AreEqual(bare.XpMultiplier, affinity.XpMultiplier, 1e-9);
            Assert.AreEqual(bare.Profile.ItemMaxAmount, affinity.Profile.ItemMaxAmount, "and it carries no loot-quantity factor");

            // The only difference is the stamped list itself.
            Assert.AreEqual(0, bare.SalvageAffinities.Count);
            Assert.AreEqual(1, affinity.SalvageAffinities.Count);
            Assert.AreEqual((43, 2398u), (affinity.SalvageAffinities[0].MaterialId, affinity.SalvageAffinities[0].BaseWcid));
            Assert.AreEqual(0.25, affinity.SalvageAffinities[0].Chance, 1e-9);
        }

        /// <summary>
        /// A dungeon with no bossAnchor gets no boss and a note, and the trash population is unaffected -
        /// the empty-but-playable rule (PLAN 6).
        /// </summary>
        [TestMethod]
        public void A_dungeon_with_no_boss_anchor_still_populates()
        {
            var d = Dungeon(4);
            d.BossAnchor = null;

            var plan = DungeonPopulationBuilder.Build(Spec(), d, Store(), Species(), LevelOf, HealthOf, Limits(), new Random(3));

            Assert.AreEqual(0u, plan.BossWcid);
            Assert.AreEqual(4, plan.Entries.Count(e => e.Role != DungeonRole.Boss));
            Assert.IsTrue(plan.Notes.Any(n => n.Contains("boss")));
        }

        /// <summary>
        /// XP is priced off the DRAWN CREATURE's own retail worth times the reward scale - ruling R20, which
        /// used to price everything off the gem's level, was reversed by the owner on 2026-09-06. The
        /// baseline is therefore exactly 1.0x retail and the 2.0 is measurable against it, and a higher-level
        /// draw pays more rather than the gem's flat advertised rate.
        /// </summary>
        [TestMethod]
        public void Xp_per_kill_is_the_drawn_creatures_own_worth_times_the_scale()
        {
            var store = Store();
            var plan = DungeonPopulationBuilder.Build(Spec(), Dungeon(4), store, Species(), LevelOf, HealthOf, Limits(), new Random(3));

            Assert.AreEqual(1.0, plan.XpMultiplier, 1e-9, "guard: no XP modifiers on this gem");
            Assert.AreEqual(2.0, plan.XpScale, 1e-9, "guard: the shipped scale these expectations are written against");

            // The creature's own XpOverride is the anchor; the gem's level (100) plays no part.
            var baseXp = DungeonPopulationBuilder.BaseXp(store.XpLadder, 500000, 130, DungeonRole.Trash, plan.BossLevel);
            Assert.AreEqual(500000L, baseXp, "an authored XpOverride is used as-is");

            Assert.AreEqual(1000000L, DungeonPopulationBuilder.XpFor(plan, baseXp, DungeonRole.Trash), "500000 x 2.0");
            Assert.AreEqual(1500000L, DungeonPopulationBuilder.XpFor(plan, baseXp, DungeonRole.Elite), "elite rate is 1.5x on top");
            Assert.AreEqual(2000000L, DungeonPopulationBuilder.XpFor(plan, baseXp, DungeonRole.Boss), "boss rate is 2.0x on top");

            var richer = DungeonPopulationBuilder.BaseXp(store.XpLadder, 900000, 160, DungeonRole.Trash, plan.BossLevel);
            Assert.IsTrue(DungeonPopulationBuilder.XpFor(plan, richer, DungeonRole.Trash) > DungeonPopulationBuilder.XpFor(plan, baseXp, DungeonRole.Trash),
                "a higher-worth draw must pay more - that is the whole point of reversing R20");
        }

        /// <summary>
        /// The guard against the 54-member hole. Measured against ace_world on 2026-09-06, 1,490 of the 1,544
        /// roster members in Content/events/axes/species carry XpOverride greater than zero and 54 do not;
        /// without the ladder fallback every one of those 54 would pay literally nothing per kill.
        /// </summary>
        [TestMethod]
        public void A_creature_with_no_xp_override_falls_back_to_the_ladder_and_never_pays_zero()
        {
            var store = Store();
            var plan = DungeonPopulationBuilder.Build(Spec(), Dungeon(4), store, Species(), LevelOf, HealthOf, Limits(), new Random(3));

            // Both shapes the absence takes: the property missing entirely, and a stored zero.
            foreach (var absent in new int?[] { null, 0 })
            {
                var baseXp = DungeonPopulationBuilder.BaseXp(store.XpLadder, absent, 100, DungeonRole.Trash, 0);

                Assert.AreEqual(80000L, baseXp, $"the ladder rung at the creature's OWN level 100 (xpOverride {absent?.ToString() ?? "null"})");
                Assert.AreNotEqual(0L, DungeonPopulationBuilder.XpFor(plan, baseXp, DungeonRole.Trash), "such a creature must never be worth nothing");
                Assert.AreEqual(160000L, DungeonPopulationBuilder.XpFor(plan, baseXp, DungeonRole.Trash), "80000 x 2.0");
            }

            // The fallback reads the CREATURE's level, not the gem's: a level-200 draw off a level-100 gem
            // falls back to the level-200 rung.
            Assert.AreEqual(1100000L, DungeonPopulationBuilder.BaseXp(store.XpLadder, null, 200, DungeonRole.Trash, 0));
        }

        /// <summary>
        /// A PROMOTED boss is drawn from its family's own trash/elite roster, so it carries a trash-tier
        /// XpOverride while the spawner stamps it plan.BossLevel. Anchoring purely on its weenie would pay
        /// boss difficulty at trash rates, so the Boss role takes the max of the two.
        /// </summary>
        [TestMethod]
        public void A_promoted_boss_is_priced_off_its_stamped_level_not_its_trash_weenie()
        {
            var store = Store();

            // The same weenie, priced as trash and as a boss stamped to level 200.
            Assert.AreEqual(3500L, DungeonPopulationBuilder.BaseXp(store.XpLadder, 3500, 20, DungeonRole.Trash, 200),
                "bossLevel is ignored for a non-boss role");
            Assert.AreEqual(1100000L, DungeonPopulationBuilder.BaseXp(store.XpLadder, 3500, 20, DungeonRole.Boss, 200),
                "the ladder rung at the STAMPED boss level wins over the trash-tier weenie value");

            // And it is a max, not a substitution: a curated boss already worth more keeps its own value.
            Assert.AreEqual(5000000L, DungeonPopulationBuilder.BaseXp(store.XpLadder, 5000000, 200, DungeonRole.Boss, 200),
                "a boss already worth more than its rung is never lowered");

            // A plan with no boss (BossLevel 0) drops the term rather than reading rung zero.
            Assert.AreEqual(3500L, DungeonPopulationBuilder.BaseXp(store.XpLadder, 3500, 20, DungeonRole.Boss, 0));
        }

        /// <summary>A gem's XP modifiers multiply on top of the reward scale, never inside it.</summary>
        [TestMethod]
        public void Xp_modifiers_multiply_on_top_of_the_scale()
        {
            var store = Store();
            var plan = DungeonPopulationBuilder.Build(Spec(("hardy", 1.6)), Dungeon(4), store, Species(), LevelOf, HealthOf, Limits(), new Random(3));

            Assert.AreEqual(1.4, plan.XpMultiplier, 1e-9);
            Assert.AreEqual(224000L, DungeonPopulationBuilder.XpFor(plan, 80000, DungeonRole.Trash), "80000 x 1.4 x 2.0");
        }

        /// <summary>A gem naming a family that has no table at all is a note, not an exception.</summary>
        [TestMethod]
        public void An_unknown_forced_family_becomes_a_note()
        {
            var spec = new DungeonGemSpec("filos_doom", 100, 6, "no_such_family", 1, new (string, double)[0], 0, 0);
            var plan = DungeonPopulationBuilder.Build(spec, Dungeon(4), Store(), Species(), LevelOf, HealthOf, Limits(), new Random(3));

            Assert.AreEqual(0, plan.Entries.Count(e => e.Role != DungeonRole.Boss));
            Assert.IsNull(plan.FamilyId);
            Assert.IsTrue(plan.Notes.Any(n => n.Contains("family")));
        }

        // ---- the boss uplift (owner ruling 2026-09-06) ----------------------------------------------

        /// <summary>
        /// A roster wide enough that every gem level in the fuzz sweep has a populated band: trash and elite
        /// members every 5 levels from 50 to 425, which comfortably covers [L, ceil(1.15L)] for every L from
        /// 60 to 275 (the shipped BandHigh, narrowed from 1.5 on 2026-09-08) - and was originally sized to
        /// cover the wider [L, ceil(1.5L)] besides, so it needed no widening for the narrower band.
        /// Deliberately its own fixture rather than the shared <see cref="Levels"/> map, which one test
        /// mutates.
        /// </summary>
        private static readonly Dictionary<uint, int> FuzzLevels = BuildFuzzLevels();

        private static Dictionary<uint, int> BuildFuzzLevels()
        {
            var d = new Dictionary<uint, int>();

            for (var i = 0; i < 76; i++)
            {
                d[(uint)(1000 + i)] = 50 + 5 * i;   // trash
                d[(uint)(2000 + i)] = 55 + 5 * i;   // elite
            }

            d[10981] = 110;                         // the curated Aun Tanua from Store()
            return d;
        }

        private static int FuzzLevelOf(uint w) => FuzzLevels.TryGetValue(w, out var level) ? level : 0;

        private static Dictionary<string, SpeciesTableDef> FuzzSpecies() => new Dictionary<string, SpeciesTableDef>
        {
            ["fuzz"] = new SpeciesTableDef
            {
                Id = "fuzz",
                CreatureType = "Banderling",
                Members = Enumerable.Range(0, 76).Select(i => new SpeciesMemberDef { Wcid = (uint)(1000 + i), Role = 0 })
                    .Concat(Enumerable.Range(0, 76).Select(i => new SpeciesMemberDef { Wcid = (uint)(2000 + i), Role = 1 }))
                    .ToList()
            }
        };

        /// <summary>
        /// The invariants the owner's rule decomposes into, asserted on a finished plan. I2 is not here: it
        /// is enforced in ThreadDungeonSpawner.TryPlace against OBSERVED health, because a creature's real
        /// Health.MaxValue folds a dat-driven attribute formula a pure builder cannot evaluate. Its
        /// arithmetic is covered by the BossHealthTarget tests below. What that leaves genuinely uncovered is
        /// the WIRING in TryPlace, which no assertion here can reach: if it folded the boss's own health into
        /// observedMaxNonBoss, or stopped updating that accumulator at all, every test in this file would
        /// still pass and the floor would silently compare against the wrong number.
        /// </summary>
        private static void AssertBossInvariants(DungeonSpawnPlan plan, DungeonGemSpec spec, DungeonPopulationLimits limits,
            Func<uint, int> levelOf, IReadOnlyDictionary<string, SpeciesTableDef> species, string because = "")
        {
            if (plan.BossWcid == 0)
                return;

            var nonBoss = plan.Entries.Where(e => e.Role != DungeonRole.Boss).ToList();
            var packMax = nonBoss.Count == 0 ? 0 : nonBoss.Max(e => levelOf(e.Wcid));

            // I1 level.
            Assert.IsTrue(plan.BossLevel > packMax, $"I1 {because}: boss level {plan.BossLevel} must outrank the pack maximum {packMax}");
            Assert.IsTrue(plan.BossLevel > spec.Level, $"I1 {because}: boss level {plan.BossLevel} must exceed the gem level {spec.Level}");
            Assert.IsTrue(plan.BossLevel >= levelOf(plan.BossWcid), $"I1 {because}: boss level {plan.BossLevel} must never fall below its authored level {levelOf(plan.BossWcid)}");

            // I3 damage.
            Assert.IsTrue(plan.BossDamageRating >= plan.DamageRating + limits.BossDamageRatingFloor,
                $"I3 {because}: boss DR {plan.BossDamageRating} must be at least pack {plan.DamageRating} + floor {limits.BossDamageRatingFloor}");

            // I4 mitigation.
            Assert.IsTrue(plan.BossDamageResistRating >= plan.DamageResistRating + limits.BossDamageResistFloor,
                $"I4 {because}: boss DRR {plan.BossDamageResistRating} must be at least pack {plan.DamageResistRating} + floor {limits.BossDamageResistFloor}");

            // I5 identity. A duplicate is tolerated ONLY where the role's own pool offered no alternative,
            // which is the "more than one eligible member" escape - asserted rather than assumed.
            foreach (var dupe in nonBoss.Where(e => e.Wcid == plan.BossWcid))
            {
                var pool = plan.FamilyId != null && species.TryGetValue(plan.FamilyId, out var family)
                    ? DungeonRosterSelector.PoolFor(family, spec.Level, dupe.Role, levelOf)
                    : new List<uint>();

                Assert.AreEqual(0, pool.Count(w => w != plan.BossWcid),
                    $"I5 {because}: boss wcid {plan.BossWcid} also placed as {dupe.Role} while its pool offered alternatives");
            }

            // I6 ordering. Load-bearing for I2: it is what makes the pack's observed health maximum final by
            // the time the spawner places the boss.
            Assert.AreEqual(DungeonRole.Boss, plan.Entries[plan.Entries.Count - 1].Role, $"I6 {because}: the boss entry must be last");
            Assert.AreEqual(1, plan.Entries.Count(e => e.Role == DungeonRole.Boss), $"I6 {because}: exactly one boss entry");
        }

        private static void AssertBossInvariants(DungeonSpawnPlan plan, DungeonGemSpec spec, DungeonPopulationLimits limits)
            => AssertBossInvariants(plan, spec, limits, LevelOf, Species());

        /// <summary>
        /// The owner's rule, swept: "Boss should always be higher level and difficulty compared to standard
        /// monsters". 8 gem levels x 50 seeds, spanning the curated window (a level-100 gem reaches Aun Tanua
        /// at 110) and every level where it can field nobody and the family must promote.
        /// </summary>
        [TestMethod]
        public void Boss_always_outranks_the_pack()
        {
            var limits = Limits();
            var species = FuzzSpecies();
            var bossless = new List<string>();

            foreach (var level in new[] { 60, 100, 140, 185, 200, 240, 265, 275 })
            {
                foreach (var seed in Enumerable.Range(0, 50))
                {
                    var spec = new DungeonGemSpec("filos_doom", level, 6, "any", 1, new (string, double)[0], 0, 0);
                    var plan = DungeonPopulationBuilder.Build(spec, Dungeon(10), Store(), species, FuzzLevelOf, HealthOf, limits, new Random(seed));

                    if (plan.BossWcid == 0)
                        bossless.Add($"level {level} seed {seed}");

                    AssertBossInvariants(plan, spec, limits, FuzzLevelOf, species, $"level {level} seed {seed}");
                }
            }

            // Not part of the invariant, but the reason it is worth sweeping: with promotion in place, a
            // dungeon with a boss anchor and an eligible family ALWAYS gets a boss. If this ever starts
            // reporting entries, the invariants above are being satisfied vacuously.
            Assert.AreEqual(0, bossless.Count, "runs that produced no boss at all: " + string.Join(", ", bossless.Take(10)));
        }

        /// <summary>
        /// The gem's own modifiers still win when they are bigger than the floors - the uplift is a FLOOR,
        /// so it can only ever raise, and a boss_enraged run keeps its authored damage rating.
        /// </summary>
        [TestMethod]
        public void The_uplift_is_a_floor_and_never_lowers_a_modified_boss()
        {
            var store = ThreadDungeonStore.Parse(
                "{\"dungeons\":[]}",
                "{\"bosses\":[{\"wcid\":10981,\"name\":\"Aun Tanua\",\"level\":110,\"families\":[],\"modifiers\":[\"boss_enraged\"]}]}",
                "{\"modifiers\":[" +
                "{\"id\":\"boss_enraged\",\"target\":\"boss\",\"minMagnitude\":200,\"maxMagnitude\":200,\"monsterEffectKind\":\"damage_rating\"}," +
                "{\"id\":\"savage\",\"target\":\"monster\",\"minMagnitude\":10,\"maxMagnitude\":40,\"monsterEffectKind\":\"damage_rating\"}]," +
                "\"xpLadder\":[{\"level\":20,\"xp\":3500},{\"level\":200,\"xp\":1100000}]}",
                new Dictionary<string, string>());

            var spec = Spec(("savage", 25));
            var plan = DungeonPopulationBuilder.Build(spec, Dungeon(4), store, Species(), LevelOf, HealthOf, Limits(), new Random(3));

            Assert.AreEqual(10981u, plan.BossWcid);
            Assert.AreEqual(25, plan.DamageRating, "the pack carries the gem's savage");
            Assert.AreEqual(225, plan.BossDamageRating, "boss_enraged 200 + savage 25 beats the floor of 25 + 30, so the modifiers win");
            AssertBossInvariants(plan, spec, Limits());
        }

        /// <summary>
        /// The level margin is a FOURTH floor on top of the three I1 names, and it is the one that carries a
        /// run whose pack drew at the bottom of its band. The trash band starts at exactly the gem level, so
        /// "one above the pack" can be as little as gemLevel + 1; the margin is what still gives the boss
        /// visible headroom there.
        ///
        /// Its own fixture with every member pinned at the gem level, because in the shared banderling
        /// family the pack draws up to level 120 and the pack term dominates - which would let this test
        /// pass without the margin existing at all.
        /// </summary>
        [TestMethod]
        public void Boss_level_clears_the_gem_level_by_the_configured_margin()
        {
            var levels = new Dictionary<uint, int> { [200] = 100, [201] = 100, [210] = 100 };
            int LevelOfLocal(uint w) => levels.TryGetValue(w, out var level) ? level : 0;

            var species = new Dictionary<string, SpeciesTableDef>
            {
                ["flat"] = new SpeciesTableDef
                {
                    Id = "flat",
                    CreatureType = "Banderling",
                    Members = new List<SpeciesMemberDef>
                    {
                        new SpeciesMemberDef { Wcid = 200, Role = 0 },
                        new SpeciesMemberDef { Wcid = 201, Role = 0 },
                        new SpeciesMemberDef { Wcid = 210, Role = 1 },
                    }
                }
            };

            var store = ThreadDungeonStore.Parse(
                "{\"dungeons\":[]}",
                "{\"bosses\":[]}",
                "{\"modifiers\":[],\"xpLadder\":[{\"level\":20,\"xp\":3500},{\"level\":200,\"xp\":1100000}]}",
                new Dictionary<string, string>());

            var spec = new DungeonGemSpec("filos_doom", 100, 6, "any", 1, new (string, double)[0], 0, 0);
            var limits = Limits();
            var plan = DungeonPopulationBuilder.Build(spec, Dungeon(6), store, species, LevelOfLocal, HealthOf, limits, new Random(3));

            Assert.AreNotEqual(0u, plan.BossWcid, "guard: promotion fires, since bosses.json is empty here");
            Assert.AreEqual(100, plan.Entries.Where(e => e.Role != DungeonRole.Boss).Max(e => LevelOfLocal(e.Wcid)),
                "guard: the whole pack sits at the gem level, so the pack term alone would only reach 101");

            // The epsilon matters: 100 * 1.10 is 110.00000000000001 in binary, so a bare Math.Ceiling would
            // make the shipped margin quietly one level stronger than the number it is configured with. The
            // builder subtracts 1e-9 for exactly this reason, and so does this expectation.
            var expected = (int)Math.Ceiling(spec.Level * limits.BossLevelMargin - 1e-9);
            Assert.AreEqual(110, expected, "guard: the shipped margin of 1.10 at gem level 100");
            Assert.AreEqual(expected, plan.BossLevel, $"boss level must land exactly on the margin floor {expected} when the pack term is only 101");

            AssertBossInvariants(plan, spec, limits, LevelOfLocal, species);
        }

        /// <summary>The other side of the same rule: with no boss modifiers at all, the floors carry it.</summary>
        [TestMethod]
        public void The_floor_carries_an_unmodified_boss()
        {
            var store = ThreadDungeonStore.Parse(
                "{\"dungeons\":[]}",
                "{\"bosses\":[{\"wcid\":10981,\"name\":\"Aun Tanua\",\"level\":110,\"families\":[],\"modifiers\":[]}]}",
                "{\"modifiers\":[],\"xpLadder\":[{\"level\":20,\"xp\":3500},{\"level\":200,\"xp\":1100000}]}",
                new Dictionary<string, string>());

            var plan = DungeonPopulationBuilder.Build(Spec(), Dungeon(4), store, Species(), LevelOf, HealthOf, Limits(), new Random(3));

            Assert.AreEqual(DungeonPopulationLimits.DefaultBossDamageRatingFloor, plan.BossDamageRating);
            Assert.AreEqual(DungeonPopulationLimits.DefaultBossDamageResistFloor, plan.BossDamageResistRating);
            Assert.AreEqual(DungeonPopulationLimits.DefaultBossHealthFloorRatio, plan.BossHealthFloorRatio, 1e-9);
            AssertBossInvariants(plan, Spec(), Limits());
        }

        /// <summary>
        /// I5 for a CURATED row, which is the case that bit in shipped data: wcid 41229 is simultaneously a
        /// bosses.json row and a role-1 virindi member, so without the swap a run could stand it on the boss
        /// anchor and a byte-identical copy of it three rooms away.
        /// </summary>
        [TestMethod]
        public void A_curated_boss_that_is_also_a_family_member_is_not_drawn_twice()
        {
            // Its own fixture, because the shared banderling family has exactly ONE role-1 member and the
            // swap needs somewhere to swap TO. Two elites (110, 111) is the smallest roster where I5 binds
            // rather than falling through its "more than one eligible member" escape. 110 and 111 are pinned
            // inside the level-100 elite band [100, 115] (BandLow/BandHigh 1.0/1.15, narrowed from 1.5 on
            // 2026-09-08) so both remain elite-eligible; the curated boss row below still declares wcid 110's
            // level as 130, which is independent of this map and only has to clear the (unrelated,
            // untouched) boss level window [100, 160].
            var levels = new Dictionary<uint, int> { [100] = 100, [101] = 105, [110] = 108, [111] = 112 };
            int LevelOfLocal(uint w) => levels.TryGetValue(w, out var level) ? level : 0;

            var species = new Dictionary<string, SpeciesTableDef>
            {
                ["banderling"] = new SpeciesTableDef
                {
                    Id = "banderling",
                    CreatureType = "Banderling",
                    Members = new List<SpeciesMemberDef>
                    {
                        new SpeciesMemberDef { Wcid = 100, Role = 0 },
                        new SpeciesMemberDef { Wcid = 101, Role = 0 },
                        new SpeciesMemberDef { Wcid = 110, Role = 1 },
                        new SpeciesMemberDef { Wcid = 111, Role = 1 },
                    }
                }
            };

            // wcid 110 is BOTH the curated boss row and a role-1 family member. champions at 1.0 forces every
            // slot elite, so without the swap the pack would be nothing but copies of the boss.
            var store = ThreadDungeonStore.Parse(
                "{\"dungeons\":[]}",
                "{\"bosses\":[{\"wcid\":110,\"name\":\"Doubled\",\"level\":130,\"families\":[],\"modifiers\":[]}]}",
                "{\"modifiers\":[{\"id\":\"champions\",\"target\":\"run\",\"minMagnitude\":0.25,\"maxMagnitude\":1.0,\"monsterEffectKind\":\"elite_share\"}]," +
                "\"xpLadder\":[{\"level\":20,\"xp\":3500},{\"level\":200,\"xp\":1100000}]}",
                new Dictionary<string, string>());

            var spec = new DungeonGemSpec("filos_doom", 100, 6, "any", 1, new[] { ("champions", 1.0) }, 0, 0);
            var plan = DungeonPopulationBuilder.Build(spec, Dungeon(6), store, species, LevelOfLocal, HealthOf, Limits(), new Random(3));

            Assert.AreEqual(110u, plan.BossWcid, "guard: the curated row is the one that is also a family member");
            Assert.IsTrue(plan.Entries.Count(e => e.Role == DungeonRole.Elite) > 0, "guard: champions forced elite slots");
            Assert.AreEqual(0, plan.Entries.Count(e => e.Role != DungeonRole.Boss && e.Wcid == 110u),
                "the boss wcid must not also stand in the pack while the pool offers alternatives");
            Assert.IsTrue(plan.Notes.Any(n => n.Contains("re-drew")), "the swap is recorded");
            AssertBossInvariants(plan, spec, Limits(), LevelOfLocal, species);
        }

        // ---- BossHealthTarget (invariant I2's arithmetic) --------------------------------------------

        [TestMethod]
        public void Boss_health_floor_wins_when_the_pack_is_tougher_than_the_boss()
        {
            // A level-220 boss at 8045 hp leading a pack whose toughest member has 23160 - the shipped
            // inversion. 23160 * 4.0 = 92640 beats both the base and the 1.5x its modifiers would give.
            Assert.AreEqual(92640, DungeonRewardMath.BossHealthTarget(8045, 1.5, 23160, 4.0));
        }

        /// <summary>
        /// The shipped level-275 case, from real data. No curated bosses.json row is eligible at 275 (the
        /// three shipped rows are level 100, 200 and 220 against a window of [275, 440]), so the virindi
        /// family promotes the Rynthid Sorcerer, wcid 51760, and PromotedBossModifiers gives it boss_guarded
        /// at its 1.5 minimum. Its pack's toughest in-band trash is the Lothus Archmage, wcid 51978.
        ///
        /// Base health is hpInit + Endurance/2 per CreatureVital.MaxValue and AttributeFormula, computed
        /// from ace_world on 2026-09-06: 51760 is 7675 + 350/2 = 7850, 51978 is 7425 + 500/2 = 7675. (The
        /// species files' own health field says 7835 and 7575; it is an informational snapshot, and the
        /// runtime uses neither - the spawner measures the pack it actually placed.)
        /// </summary>
        [TestMethod]
        public void Boss_health_floor_wins_at_the_top_rung_too()
        {
            Assert.AreEqual(30700, DungeonRewardMath.BossHealthTarget(7850, 1.5, 7675, 4.0),
                "7675 x 4.0 = 30700 beats both the 7850 base and the 11775 its boss_guarded alone would give");
        }

        [TestMethod]
        public void Boss_health_modifiers_win_when_they_exceed_the_floor()
        {
            // 40000 * 3.0 = 120000 beats 20000 * 4.0 = 80000, so the gem's boss_guarded is what lands.
            Assert.AreEqual(120000, DungeonRewardMath.BossHealthTarget(40000, 3.0, 20000, 4.0));
        }

        [TestMethod]
        public void Boss_health_never_falls_below_the_weenie_base()
        {
            Assert.AreEqual(50000, DungeonRewardMath.BossHealthTarget(50000, 1.0, 100, 4.0), "a tiny pack cannot shrink a boss");
            Assert.AreEqual(50000, DungeonRewardMath.BossHealthTarget(50000, 0.25, 100, 4.0), "nor can a nonsense multiplier");
            Assert.AreEqual(50000, DungeonRewardMath.BossHealthTarget(50000, double.NaN, 100, double.NaN), "nor NaN dials");
        }

        [TestMethod]
        public void Boss_health_falls_back_to_the_modifier_term_when_nothing_non_boss_was_placed()
        {
            // observedMaxNonBoss 0: an empty or entirely-failed pack simply drops the floor term.
            Assert.AreEqual(12000, DungeonRewardMath.BossHealthTarget(8000, 1.5, 0, 4.0));
            Assert.AreEqual(8000, DungeonRewardMath.BossHealthTarget(8000, 1.0, 0, 4.0));
        }

        [TestMethod]
        public void Boss_health_saturates_rather_than_wrapping()
        {
            Assert.AreEqual(int.MaxValue, DungeonRewardMath.BossHealthTarget(uint.MaxValue, 1000.0, uint.MaxValue, 1000.0));
            Assert.IsTrue(DungeonRewardMath.BossHealthTarget(1, 1.0, 0, 0.0) >= 1, "never below 1");
        }

        // ---- gem-level reward scaling (owner requirement, 2026-09-07) ------------------------------

        private static DungeonGemSpec LeveledSpec(int level, params (string, double)[] mods) => new DungeonGemSpec("filos_doom", level, 6, "any", 1, mods, 0, 0);

        private static DungeonPopulationLimits ScaledLimits(double anchor = DungeonPopulationLimits.DefaultRewardScaleAnchor,
            double exponent = DungeonPopulationLimits.DefaultRewardScaleExponent,
            double floor = DungeonPopulationLimits.DefaultRewardScaleFloor,
            double cap = DungeonPopulationLimits.DefaultRewardScaleCap) =>
            new DungeonPopulationLimits(120, 2.0, 3.0, 8, 0.5, lootQuantityCap: 3.0,
                rewardScalingEnabled: true, rewardScaleAnchor: anchor, rewardScaleExponent: exponent, rewardScaleFloor: floor, rewardScaleCap: cap);

        /// <summary>
        /// A store carrying one modifier per axis this feature touches: "rich" (target run, no monster
        /// effect) gives a flat loot-quality bonus (0.4) and a loot-quantity factor (2.0, via the
        /// LootQuantityMult field rather than the "loot_quantity" magnitude-substitution kind), and
        /// "affinity_tourmaline" is a salvage-affinity row whose magnitude in percent becomes the raw per-kill
        /// chance.
        /// </summary>
        private static ThreadDungeonStore ScaledStore() => ThreadDungeonStore.Parse(
            "{\"dungeons\":[]}",
            "{\"bosses\":[]}",
            "{\"modifiers\":[" +
            "{\"id\":\"rich\",\"target\":\"run\",\"minMagnitude\":1.0,\"maxMagnitude\":1.0,\"monsterEffectKind\":\"none\",\"lootQualityBonus\":0.4,\"lootQuantityMult\":2.0}," +
            "{\"id\":\"affinity_tourmaline\",\"target\":\"monster\",\"minMagnitude\":5,\"maxMagnitude\":25,\"monsterEffectKind\":\"salvage_affinity\",\"salvageMaterial\":43,\"salvageBaseWcid\":2398}]," +
            "\"xpLadder\":[{\"level\":20,\"xp\":3500},{\"level\":100,\"xp\":80000},{\"level\":200,\"xp\":1100000}]}",
            new Dictionary<string, string>());

        /// <summary>
        /// The master switch off must reproduce today's (pre-feature) behaviour byte-for-byte: with scaling
        /// disabled, plan.XpScale/LumScale carry the raw tunables regardless of level, exactly as before this
        /// feature existed. Contrasted against the SAME gem/level with scaling ON, which must differ - the
        /// guard that this test is not vacuously true.
        /// </summary>
        [TestMethod]
        public void Master_switch_off_reproduces_the_raw_scale_regardless_of_level()
        {
            var store = Store();
            var lowLevelSpec = LeveledSpec(185);

            var disabled = new DungeonPopulationLimits(120, 2.0, 3.0, 8, 0.5, lootQuantityCap: 3.0, rewardScalingEnabled: false);
            var disabledPlan = DungeonPopulationBuilder.Build(lowLevelSpec, Dungeon(4), store, Species(), LevelOf, HealthOf, disabled, new Random(3));

            Assert.AreEqual(DungeonPopulationLimits.DefaultXpScale, disabledPlan.XpScale, 1e-9, "scaling disabled: XpScale is the raw tunable");
            Assert.AreEqual(DungeonPopulationLimits.DefaultLumScale, disabledPlan.LumScale, 1e-9, "scaling disabled: LumScale is the raw tunable");

            var enabledPlan = DungeonPopulationBuilder.Build(lowLevelSpec, Dungeon(4), store, Species(), LevelOf, HealthOf, ScaledLimits(), new Random(3));

            Assert.AreNotEqual(disabledPlan.XpScale, enabledPlan.XpScale, 1e-9,
                "guard: scaling ON must actually change something at level 185 (below the anchor), or this test cannot discriminate the two paths");
        }

        /// <summary>
        /// The XP/luminance scale are MULTIPLICATIVE axes (neutral value 1.0): effective = 1 + ratio *
        /// (raw - 1). At level 185 (ratio 0.25 exactly, per the spec's reference table) the shipped 2.0
        /// default becomes 1 + 0.25 * (2.0 - 1) = 1.25 - never the wrong form's 0.25 * 2.0 = 0.5, which would
        /// pay LESS than retail.
        /// </summary>
        [TestMethod]
        public void XpScale_and_LumScale_are_scaled_multiplicatively()
        {
            var plan = DungeonPopulationBuilder.Build(LeveledSpec(185), Dungeon(4), Store(), Species(), LevelOf, HealthOf, ScaledLimits(), new Random(3));

            Assert.AreEqual(1.25, plan.XpScale, 0.01);
            Assert.AreEqual(1.25, plan.LumScale, 0.01);
            Assert.IsTrue(plan.XpScale > 1.0, "must never drop below retail (1.0x) at any level");
        }

        /// <summary>
        /// Loot QUANTITY is a multiplicative axis (a count multiplier, neutral 1.0); loot QUALITY and the
        /// salvage-affinity per-kill chance are ADDITIVE axes (neutral 0.0, effective = ratio * raw). At
        /// level 185 (ratio 0.25): the "rich" modifier's raw 2.0x loot-quantity multiplier becomes
        /// 1 + 0.25 * (2.0 - 1) = 1.25x (observed via the profile's scaled max item counts), its raw 0.4
        /// loot-quality bonus becomes 0.25 * 0.4 = 0.10, and the salvage row's raw 25% chance becomes
        /// 0.25 * 0.25 = 0.0625.
        /// </summary>
        [TestMethod]
        public void Loot_quantity_scales_multiplicatively_and_quality_and_salvage_chance_scale_additively()
        {
            var store = ScaledStore();
            var spec = LeveledSpec(185, ("rich", 1.0), ("affinity_tourmaline", 25));

            var raw = DungeonPopulationBuilder.Build(spec, Dungeon(4), store, Species(), LevelOf, HealthOf,
                new DungeonPopulationLimits(120, 2.0, 3.0, 8, 0.5, lootQuantityCap: 3.0, rewardScalingEnabled: false), new Random(3));
            var scaled = DungeonPopulationBuilder.Build(spec, Dungeon(4), store, Species(), LevelOf, HealthOf, ScaledLimits(), new Random(3));

            // Loot quantity: raw 2.0x -> Scale(2) = ceil(2 * 2.0) = 4; scaled 1.25x -> Scale(2) = ceil(2 * 1.25) = 3.
            Assert.AreEqual(4, raw.Profile.ItemMaxAmount, "guard: the unscaled profile carries the raw 2.0x multiplier");
            Assert.AreEqual(3, scaled.Profile.ItemMaxAmount, "loot quantity scaled multiplicatively: 1 + 0.25 * (2.0 - 1) = 1.25x");

            // Loot quality: raw bonus 0.4 unchanged; scaled bonus 0.25 * 0.4 = 0.10.
            Assert.AreEqual(0.4f, raw.Profile.LootQualityMod, 0.001f, "guard: the unscaled profile carries the raw 0.4 bonus");
            Assert.AreEqual(0.10f, scaled.Profile.LootQualityMod, 0.001f, "loot quality scaled additively: 0.25 * 0.4 = 0.10");

            // Salvage-affinity chance: raw 25% -> 0.25; scaled -> ratio(185) * 0.25 (~0.0625, ratio itself only
            // pinned to 0.01 by the spec's reference table, so compare against the actual ratio rather than
            // the rounded 0.25 to avoid a spurious floating-point mismatch).
            var ratio185 = DungeonRewardMath.RewardScaleRatio(185, DungeonPopulationLimits.DefaultRewardScaleAnchor,
                DungeonPopulationLimits.DefaultRewardScaleExponent, DungeonPopulationLimits.DefaultRewardScaleFloor, DungeonPopulationLimits.DefaultRewardScaleCap);

            Assert.AreEqual(1, raw.SalvageAffinities.Count);
            Assert.AreEqual(0.25, raw.SalvageAffinities[0].Chance, 1e-9, "guard: the unscaled chance is the raw 25%");
            Assert.AreEqual(1, scaled.SalvageAffinities.Count);
            Assert.AreEqual(ratio185 * 0.25, scaled.SalvageAffinities[0].Chance, 1e-9, "salvage chance scaled additively: ratio(185) * 0.25 (~0.0625)");
        }

        /// <summary>A level at or above the anchor (300) is never worse off than the raw tunables, uncapped by default.</summary>
        [TestMethod]
        public void A_gem_at_or_above_the_anchor_pays_at_least_the_raw_scale()
        {
            var store = Store();

            var atAnchor = DungeonPopulationBuilder.Build(LeveledSpec(300), Dungeon(4), store, Species(), LevelOf, HealthOf, ScaledLimits(), new Random(3));
            Assert.AreEqual(DungeonPopulationLimits.DefaultXpScale, atAnchor.XpScale, 0.01, "ratio(300) = 1.00: the total equals the raw scale exactly");

            var aboveAnchor = DungeonPopulationBuilder.Build(LeveledSpec(400), Dungeon(4), store, Species(), LevelOf, HealthOf, ScaledLimits(), new Random(3));
            Assert.IsTrue(aboveAnchor.XpScale > atAnchor.XpScale, "above the anchor, uncapped, must pay more than at the anchor");
        }

        // ---- IsKillable (refuse a non-attackable / NPC boss weenie) ----

        [TestMethod]
        public void An_attackable_non_NPC_creature_is_killable()
        {
            Assert.IsTrue(DungeonPopulationBuilder.IsKillable(true, PlayerKillerStatus.NPK));
        }

        [TestMethod]
        public void A_non_attackable_creature_is_not_killable()
        {
            Assert.IsFalse(DungeonPopulationBuilder.IsKillable(false, PlayerKillerStatus.NPK));
        }

        [TestMethod]
        public void An_NPC_flagged_creature_is_not_killable()
        {
            Assert.IsFalse(DungeonPopulationBuilder.IsKillable(true, PlayerKillerStatus.NPC));
        }

        // ---- adaptive band + band-standard uplift (invariants I-A, I-D, I-E, I-F, I-G) ----------------------

        /// <summary>
        /// The widening fixture, at level 200 so the natural band is [200, 230].
        ///
        /// "wide" is thin at the natural band (one trash member, no elite) and deep below it, so it widens.
        /// "deep" already carries six in-band trash members and an in-band elite, so it never widens - that is
        /// the I-A control. "f2".."f4" exist only to put the eligible-family count at the natural band on the
        /// far side of MinEligibleFamilies, so the two widening halves can be exercised independently.
        /// </summary>
        private static readonly Dictionary<uint, int> WideLevels = new Dictionary<uint, int>
        {
            [900] = 205, [901] = 175, [902] = 170, [903] = 165, [904] = 160, [905] = 155, [906] = 150,
            [910] = 160,                                                        // "wide" elite, below the natural band
            [930] = 201, [931] = 202, [932] = 203, [933] = 204, [934] = 206, [935] = 207, [940] = 215, // "deep"
            [920] = 205, [921] = 205, [922] = 205,                              // f2, f3, f4
        };

        private static int WideLevelOf(uint w) => WideLevels[w];

        private static Dictionary<string, SpeciesTableDef> WideSpecies()
        {
            SpeciesMemberDef Trash(uint w) => new SpeciesMemberDef { Wcid = w, Role = 0 };

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

        /// <summary>A profile carrying one skill, some melee damage and some body armour, for the standard.</summary>
        private static DungeonStatProfile WideProfileOf(uint wcid)
        {
            var level = WideLevels[wcid];

            return new DungeonStatProfile(level, 0,
                new Dictionary<Skill, uint> { [Skill.MeleeDefense] = (uint)(level * 2) },
                (uint)level, (uint)(level * 3));
        }

        private static DungeonGemSpec WideSpec(string family)
            => new DungeonGemSpec("filos_doom", 200, 6, family, 1, new (string, double)[0], 0, 0);

        private static DungeonPopulationLimits WideLimits(double bandLowFloorRatio = DungeonPopulationLimits.DefaultBandLowFloorRatio,
            int minEligibleFamilies = DungeonPopulationLimits.DefaultMinEligibleFamilies,
            int minFamilyPool = DungeonPopulationLimits.DefaultMinFamilyPool)
            => new DungeonPopulationLimits(120, 2.0, 3.0, 8, 0.5,
                rewardScalingEnabled: false,
                bandLowFloorRatio: bandLowFloorRatio,
                minEligibleFamilies: minEligibleFamilies,
                minFamilyPool: minFamilyPool);

        /// <summary>
        /// I-A: a gem whose natural band already meets BOTH thresholds widens nothing, and its plan is
        /// identical to the same gem's plan with the feature switched off entirely.
        /// </summary>
        [TestMethod]
        public void IA_a_natural_band_that_meets_both_thresholds_widens_nothing()
        {
            var spec = WideSpec("deep");

            var adaptive = DungeonPopulationBuilder.Build(spec, Dungeon(6), Store(), WideSpecies(), WideLevelOf, HealthOf,
                WideLimits(), new Random(9), WideProfileOf);
            var disabled = DungeonPopulationBuilder.Build(spec, Dungeon(6), Store(), WideSpecies(), WideLevelOf, HealthOf,
                WideLimits(bandLowFloorRatio: 1.0), new Random(9), WideProfileOf);

            Assert.AreEqual(200, adaptive.NaturalBandLow);
            Assert.AreEqual(200, adaptive.EffectiveBandLow, "nothing widened");
            Assert.IsTrue(adaptive.BandStandard.IsEmpty, "nothing was tagged, so no standard was even computed");
            Assert.IsFalse(adaptive.Notes.Any(n => n.StartsWith("band:")), string.Join(" | ", adaptive.Notes));

            CollectionAssert.AreEqual(disabled.Entries.Select(e => e.Wcid).ToArray(), adaptive.Entries.Select(e => e.Wcid).ToArray());
            CollectionAssert.AreEqual(disabled.Entries.Select(e => (int)e.Role).ToArray(), adaptive.Entries.Select(e => (int)e.Role).ToArray());
            CollectionAssert.AreEqual(disabled.Entries.Select(e => e.Point.X).ToArray(), adaptive.Entries.Select(e => e.Point.X).ToArray());
            Assert.AreEqual(disabled.BossWcid, adaptive.BossWcid);
            Assert.AreEqual(disabled.BossLevel, adaptive.BossLevel);
        }

        /// <summary>
        /// I-D: only picks whose LIVE level is below floor(gemLevel) carry an uplift level, and the one they
        /// carry is the GEM's level. Everything at or above that edge carries 0.
        /// </summary>
        [TestMethod]
        public void ID_only_picks_below_the_natural_low_edge_carry_an_uplift_level()
        {
            var plan = DungeonPopulationBuilder.Build(WideSpec("wide"), Dungeon(20), Store(), WideSpecies(), WideLevelOf, HealthOf,
                WideLimits(), new Random(4), WideProfileOf);

            Assert.AreEqual(200, plan.NaturalBandLow);
            Assert.IsTrue(plan.EffectiveBandLow < 200, "the thin family widened its own low edge");
            Assert.IsTrue(plan.Entries.Count > 1);

            foreach (var e in plan.Entries)
            {
                var level = WideLevelOf(e.Wcid);

                if (level < 200)
                    Assert.AreEqual(200, e.UpliftLevel, $"wcid {e.Wcid} at level {level} is below the natural edge");
                else
                    Assert.AreEqual(0, e.UpliftLevel, $"wcid {e.Wcid} at level {level} is inside the natural band");
            }

            Assert.IsTrue(plan.Entries.Any(e => e.UpliftLevel > 0), "the fixture must actually exercise the tag");
            Assert.IsTrue(plan.Entries.Any(e => e.UpliftLevel == 0), "and must also exercise the untagged half");

            // A standard was derived, so the spawner has something to normalize toward.
            Assert.IsFalse(plan.BandStandard.IsEmpty);
            Assert.IsTrue(plan.BandStandard.MaxBodyDamage > 0);
            Assert.IsTrue(plan.BandStandard.MedianFor(Skill.MeleeDefense) > 0);
        }

        /// <summary>
        /// I-E: dynamic_dungeons_band_low_floor = 1.0 reproduces the pre-feature behaviour - no widening, no
        /// uplift tag, and so (since the spawner gates the prefix on the tag) no rename. The same fixture
        /// widens hard when the floor is left at its default, which is what makes this a discriminating test
        /// rather than one that would pass against a no-op implementation.
        /// </summary>
        [TestMethod]
        public void IE_a_band_low_floor_of_one_reproduces_todays_behaviour_exactly()
        {
            var spec = WideSpec("wide");

            var off = DungeonPopulationBuilder.Build(spec, Dungeon(8), Store(), WideSpecies(), WideLevelOf, HealthOf,
                WideLimits(bandLowFloorRatio: 1.0), new Random(6), WideProfileOf);

            Assert.AreEqual(200, off.EffectiveBandLow);
            Assert.AreEqual(off.NaturalBandLow, off.EffectiveBandLow);
            Assert.IsTrue(off.Entries.All(e => e.UpliftLevel == 0), "nothing is tagged, so the spawner renames nothing");
            Assert.IsTrue(off.BandStandard.IsEmpty, "and no standard is computed at all");
            Assert.IsFalse(off.Notes.Any(n => n.StartsWith("band:") || n.StartsWith("uplift:")), string.Join(" | ", off.Notes));

            Assert.IsTrue(off.Entries.Where(e => e.Role != DungeonRole.Boss).All(e => e.Wcid == 900u),
                "with the feature off the run can only draw the one member of the natural band");

            // The floor is the ONLY total off switch, and that is worth pinning rather than assuming. Zeroing
            // both count thresholds disables the two POOL-DEPTH widenings, but the elite-empty widening has
            // no threshold of its own - it is governed by the floor alone - so a family with no elite in the
            // natural band still reaches down for one and still tags it.
            var thresholdsOff = DungeonPopulationBuilder.Build(spec, Dungeon(8), Store(), WideSpecies(), WideLevelOf, HealthOf,
                WideLimits(minEligibleFamilies: 0, minFamilyPool: 0), new Random(6), WideProfileOf);

            Assert.AreEqual(200, thresholdsOff.EffectiveBandLow, "the TRASH pool is still the natural band's");
            Assert.IsTrue(thresholdsOff.Entries.Where(e => e.Role == DungeonRole.Trash).All(e => e.Wcid == 900u));
            Assert.IsTrue(thresholdsOff.Entries.Any(e => e.UpliftLevel == 200),
                "but the elite/promoted-boss slots still reached the level-160 elite, and are tagged for it");

            // The control: the SAME gem and seed widens hard with the shipped floor.
            var on = DungeonPopulationBuilder.Build(spec, Dungeon(8), Store(), WideSpecies(), WideLevelOf, HealthOf,
                WideLimits(), new Random(6), WideProfileOf);

            Assert.IsTrue(on.EffectiveBandLow < 200);
            Assert.IsTrue(on.Entries.Any(e => e.UpliftLevel == 200));
        }

        /// <summary>
        /// I-F: an uplifted kill pays at least the ladder XP for the gem's level. The ladder in this file's
        /// Store() tops out at {200, 1,100,000}, and the uplift target is the gem level, so a level-150
        /// creature drawn into a level-200 run anchors on 1,100,000 rather than on its own trash-tier value.
        /// </summary>
        [TestMethod]
        public void IF_an_uplifted_kill_pays_at_least_the_gem_levels_ladder_xp()
        {
            var store = Store();

            // Without the uplift term: the creature's own XpOverride, which is what it paid before this change.
            Assert.AreEqual(3500L, DungeonPopulationBuilder.BaseXp(store.XpLadder, 3500, 150, DungeonRole.Trash, 0));

            // With it: the level-200 ladder rung.
            Assert.AreEqual(1100000L, DungeonPopulationBuilder.BaseXp(store.XpLadder, 3500, 150, DungeonRole.Trash, 0, 200));

            // A FLOOR, never a replacement: a creature already worth more keeps its own value.
            Assert.AreEqual(5000000L, DungeonPopulationBuilder.BaseXp(store.XpLadder, 5000000, 150, DungeonRole.Trash, 0, 200));

            // An untagged entry is untouched by the new term (I-A/I-D again, at the reward end).
            Assert.AreEqual(3500L, DungeonPopulationBuilder.BaseXp(store.XpLadder, 3500, 150, DungeonRole.Trash, 0, 0));

            // And it composes with the boss term rather than replacing it: an uplifted PROMOTED boss takes
            // the larger of the two rungs.
            Assert.AreEqual(1100000L, DungeonPopulationBuilder.BaseXp(store.XpLadder, 3500, 150, DungeonRole.Boss, 220, 200));
        }

        /// <summary>
        /// I-G: the boss promotion path still works when the promoted candidate is itself uplifted. The "wide"
        /// family's only elite sits at level 160, below the natural low edge, so it is both the promotion
        /// candidate and an uplift-tagged entry; the boss level stamp's four floors must still hold on top.
        /// </summary>
        [TestMethod]
        public void IG_the_boss_promotion_path_works_when_the_promoted_candidate_is_itself_uplifted()
        {
            var plan = DungeonPopulationBuilder.Build(WideSpec("wide"), Dungeon(8), Store(), WideSpecies(), WideLevelOf, HealthOf,
                WideLimits(), new Random(2), WideProfileOf);

            Assert.AreNotEqual(0u, plan.BossWcid, "a boss was promoted");
            Assert.IsTrue(plan.Notes.Any(n => n.Contains("promoted wcid")), string.Join(" | ", plan.Notes));

            var boss = plan.Entries.Single(e => e.Role == DungeonRole.Boss);
            Assert.AreEqual(plan.BossWcid, boss.Wcid);
            Assert.IsTrue(WideLevelOf(boss.Wcid) < 200, "the fixture must promote a candidate that is itself below the natural edge");
            Assert.AreEqual(200, boss.UpliftLevel, "so it carries the uplift tag like any other extension pick");

            // The boss uplift sits ON TOP: level margin 1.10 x 200 = 220, and one above the gem is 201, so
            // the stamped level is 220 - above both the uplift target and every pack member.
            Assert.AreEqual(220, plan.BossLevel);
            Assert.IsTrue(plan.BossLevel > boss.UpliftLevel, "the boss stamp always wins, so the two compose in one direction only");

            var packMax = plan.Entries.Where(e => e.Role != DungeonRole.Boss).Max(e => WideLevelOf(e.Wcid));
            Assert.IsTrue(plan.BossLevel > packMax);
            Assert.IsTrue(plan.BossDamageRating >= plan.DamageRating + DungeonPopulationLimits.DefaultBossDamageRatingFloor);

            // The DIAGNOSTIC half of the same composition. ThreadDungeonSpawner applies the uplift after the
            // boss level stamp, so creature.Level is 220 by then; the log line must still report the boss
            // weenie's OWN level (160), which is why the spawner passes its pre-stamp ownLevel in rather than
            // re-reading the creature. These two numbers are only distinguishable because the assertions
            // above prove they differ.
            var authored = WideLevelOf(boss.Wcid);
            Assert.AreNotEqual(authored, plan.BossLevel, "the fixture must make the two values tell apart");

            var line = ThreadDungeonSpawner.UpliftLogLine(boss.Wcid, boss.UpliftLevel, authored, 1, 2, 0, plan.BandStandard);

            StringAssert.Contains(line, "(authored " + authored + ")");
            Assert.IsFalse(line.Contains("(authored " + plan.BossLevel + ")"),
                "reporting the STAMPED boss level as 'authored' reads backwards: 'uplifted to level 200 (authored 220)'");
        }

        [TestMethod]
        public void The_uplift_log_line_renders_both_forms_and_never_invents_a_standard()
        {
            var withStandard = DungeonPopulationBuilder.Build(WideSpec("wide"), Dungeon(6), Store(), WideSpecies(), WideLevelOf, HealthOf,
                WideLimits(), new Random(2), WideProfileOf).BandStandard;

            Assert.IsFalse(withStandard.IsEmpty);

            var full = ThreadDungeonSpawner.UpliftLogLine(910u, 200, 160, 3, 4, 2, withStandard);
            StringAssert.Contains(full, "wcid 910 uplifted to level 200 (authored 160)");
            StringAssert.Contains(full, "3 skill(s) raised, 4 body part(s) scaled toward");
            StringAssert.Contains(full, $"2 spell(s) raised to tier {withStandard.SpellTier}");

            // No standard: the level floor still happened, so the line still reports it, but nothing claims a
            // stat was touched.
            foreach (var none in new[] { null, DungeonBandStandard.Empty })
            {
                var bare = ThreadDungeonSpawner.UpliftLogLine(910u, 200, 160, 0, 0, 0, none);
                StringAssert.Contains(bare, "wcid 910 uplifted to level 200 (authored 160)");
                StringAssert.Contains(bare, "no band standard available, stats untouched");
                Assert.IsFalse(bare.Contains("skill(s) raised"));
                Assert.IsFalse(bare.Contains("spell(s) raised"));
            }
        }

        [TestMethod]
        public void A_garbled_band_floor_reaching_the_builder_disables_the_feature_rather_than_throwing()
        {
            foreach (var garbage in new[] { double.NaN, double.PositiveInfinity, 0.0, -1.0, 2.0 })
            {
                var plan = DungeonPopulationBuilder.Build(WideSpec("wide"), Dungeon(6), Store(), WideSpecies(), WideLevelOf, HealthOf,
                    WideLimits(bandLowFloorRatio: garbage), new Random(3), WideProfileOf);

                Assert.AreEqual(plan.NaturalBandLow, plan.EffectiveBandLow, $"floor={garbage}");
                Assert.IsTrue(plan.Entries.All(e => e.UpliftLevel == 0), $"floor={garbage}");
            }
        }

        [TestMethod]
        public void A_null_profile_delegate_still_tags_entries_but_leaves_the_standard_empty()
        {
            var plan = DungeonPopulationBuilder.Build(WideSpec("wide"), Dungeon(6), Store(), WideSpecies(), WideLevelOf, HealthOf,
                WideLimits(), new Random(3));

            Assert.IsTrue(plan.Entries.Any(e => e.UpliftLevel == 200), "the tag does not depend on the profile delegate");
            Assert.IsTrue(plan.BandStandard.IsEmpty, "but the standard does, and the spawner then normalizes only the level");
        }

        // ---- plan.LootQuantityMult, the boss cache's "never recomputed" invariant --------------------
        //
        // The boss cache (ThreadDungeonRewardSpawner) rolls ceil(lootCount * multiplier) items, and the
        // multiplier it uses is the RUN's - carried on plan.LootQuantityMult, stamped onto the run by
        // ThreadDungeonSpawner at populate time. The whole point of carrying it is that the cache cannot
        // drift from what the run's creatures were built with, so what has to be pinned is not that the
        // field holds a plausible number but that it holds the SAME scalar BuildProfile was handed.
        //
        // Both tests below assert that twice over: once against an independent recomputation through the
        // public DungeonRewardMath entry point, and once against plan.Profile's own scaled maximums, which
        // are the only externally visible trace of the scalar BuildProfile actually received. The second
        // assertion is the load-bearing one - a future edit that recomputed the field separately from the
        // profile would satisfy the first and fail the second.

        /// <summary>Store() with a loot-quantity factor on 'hardy'; the shared fixture carries none.</summary>
        private static ThreadDungeonStore LootQuantityStore() => ThreadDungeonStore.Parse(
            "{\"dungeons\":[]}",
            "{\"bosses\":[{\"wcid\":10981,\"name\":\"Aun Tanua\",\"level\":110,\"families\":[],\"modifiers\":[\"boss_guarded\"]}]}",
            "{\"modifiers\":[" +
            "{\"id\":\"hardy\",\"target\":\"monster\",\"minMagnitude\":1.3,\"maxMagnitude\":2.0,\"monsterEffectKind\":\"health_mult\",\"lootQuantityMult\":1.4,\"rewardXpKind\":\"linear\",\"rewardXpBase\":1.0,\"rewardXpSlope\":0.25}," +
            "{\"id\":\"teeming\",\"target\":\"run\",\"minMagnitude\":1.25,\"maxMagnitude\":1.75,\"monsterEffectKind\":\"count_mult\"}," +
            "{\"id\":\"boss_guarded\",\"target\":\"boss\",\"minMagnitude\":1.5,\"maxMagnitude\":3.0,\"monsterEffectKind\":\"health_mult\"}]," +
            "\"xpLadder\":[{\"level\":20,\"xp\":3500},{\"level\":100,\"xp\":80000},{\"level\":200,\"xp\":1100000}]}",
            new Dictionary<string, string>());

        /// <summary>
        /// Limits() with the loot-quantity axis actually ENABLED.
        ///
        /// This is not a stylistic variant: DungeonPopulationLimits' lootQuantityCap parameter defaults to
        /// 1.0 (DungeonPopulationBuilder.cs:243) and the shared Limits() helper above never passes one, so
        /// LootQuantityMultiplier clamps every product back to 1.0 under it. A loot-quantity test written
        /// against the shared helper would measure a no-op and pass for the wrong reason - which is exactly
        /// what the fixture-sanity assertion in the first test below exists to catch.
        /// </summary>
        private static DungeonPopulationLimits LootQuantityLimits()
            => new DungeonPopulationLimits(120, 2.0, 3.0, 8, 0.5, lootQuantityCap: 3.0, rewardScalingEnabled: false);

        /// <summary>
        /// BuildProfile's own scaling rule (DungeonRewardMath.BuildProfile): every MAXIMUM item count is
        /// ceil(base * q), floored at the unscaled base and capped at 100. Restated here rather than shared,
        /// so a change to that rule fails this test instead of being mirrored into it.
        /// </summary>
        private static int ExpectedProfileMax(int baseMax, double q) => (int)Math.Clamp(Math.Ceiling(baseMax * q), baseMax, 100);

        private static void AssertProfileWasBuiltFromPlanMultiplier(DungeonSpawnPlan plan)
        {
            var q = plan.LootQuantityMult;

            Assert.AreEqual(ExpectedProfileMax(2, q), plan.Profile.ItemMaxAmount, $"ItemMaxAmount for q={q}");
            Assert.AreEqual(ExpectedProfileMax(2, q), plan.Profile.MagicItemMaxAmount, $"MagicItemMaxAmount for q={q}");
            Assert.AreEqual(ExpectedProfileMax(1, q), plan.Profile.MundaneItemMaxAmount, $"MundaneItemMaxAmount for q={q}");

            Assert.AreEqual(1, plan.Profile.ItemMinAmount, "the multiplier must never move a minimum");
            Assert.AreEqual(1, plan.Profile.MagicItemMinAmount, "the multiplier must never move a minimum");
            Assert.AreEqual(1, plan.Profile.MundaneItemMinAmount, "the multiplier must never move a minimum");
        }

        [TestMethod]
        public void Plan_carries_the_same_loot_quantity_multiplier_it_gave_BuildProfile()
        {
            var store = LootQuantityStore();
            var limits = LootQuantityLimits();
            var spec = Spec(("hardy", 1.5));

            var plan = DungeonPopulationBuilder.Build(spec, Dungeon(12), store, Species(), LevelOf, HealthOf, limits, new Random(3));

            var expected = DungeonRewardMath.LootQuantityMultiplier(spec, store.Modifiers, limits.LootQuantityCap);

            Assert.AreEqual(1.4, expected, 1e-9, "fixture sanity: the modifier must actually carry a loot-quantity factor");
            Assert.AreEqual(expected, plan.LootQuantityMult, 1e-9, "the plan must carry the scalar, not recompute a different one");

            AssertProfileWasBuiltFromPlanMultiplier(plan);

            // 2 -> ceil(2.8) = 3 and 1 -> ceil(1.4) = 2, i.e. the profile visibly moved. Without this the
            // test would still pass if the multiplier were silently 1.0 in both places.
            Assert.AreEqual(3, plan.Profile.ItemMaxAmount);
            Assert.AreEqual(2, plan.Profile.MundaneItemMaxAmount);
        }

        [TestMethod]
        public void Plan_carries_the_neutral_multiplier_for_a_gem_with_no_loot_quantity_modifier()
        {
            // The SAME cap as the test above, so the only difference between the two is whether the gem's
            // modifier carries a loot-quantity factor. Sharing Limits() here instead would have made the cap
            // the real variable and the modifier incidental.
            var store = Store();
            var limits = LootQuantityLimits();
            var spec = Spec(("hardy", 1.5));

            var plan = DungeonPopulationBuilder.Build(spec, Dungeon(12), store, Species(), LevelOf, HealthOf, limits, new Random(3));

            var expected = DungeonRewardMath.LootQuantityMultiplier(spec, store.Modifiers, limits.LootQuantityCap);

            Assert.AreEqual(1.0, expected, 1e-9, "the shared fixture's modifiers carry no loot-quantity factor, and the cap is not what is holding it at 1.0");
            Assert.AreEqual(expected, plan.LootQuantityMult, 1e-9);

            AssertProfileWasBuiltFromPlanMultiplier(plan);

            // The neutral multiplier must leave BuildProfile's bases exactly as authored.
            Assert.AreEqual(2, plan.Profile.ItemMaxAmount);
            Assert.AreEqual(1, plan.Profile.MundaneItemMaxAmount);
        }

        /// <summary>
        /// A plan nobody set the field on reads 1.0 rather than 0. The default matters because
        /// ThreadDungeonSpawner stamps plan.LootQuantityMult straight onto the run, and a 0 default would
        /// make ScaledLootCount read a garbled multiplier on any future Build path that forgot to set it.
        /// </summary>
        [TestMethod]
        public void An_unset_plan_multiplier_is_neutral()
        {
            Assert.AreEqual(1.0, new DungeonSpawnPlan().LootQuantityMult, 1e-9);
        }
    }
}
