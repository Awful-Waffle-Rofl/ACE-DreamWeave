using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using ACE.Server.ThreadDungeons;
using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// Covers DungeonRosterSelector's own trash/elite band arithmetic, which deliberately does NOT inherit
    /// WorldEventRosterSelector's MaxConsideredLevel (275) clamp, and the live-tree consequence of that:
    /// every Raw Fragment rung must be able to find a family in the shipped species tables.
    ///
    /// Nothing here touches a database or a landblock.
    /// </summary>
    [TestClass]
    public class DungeonRosterBandTests
    {
        /// <summary>
        /// The fourteen Raw Fragment rungs, wcids 1003615-1003624 and 1003626-1003629
        /// (Content/sql/weenies/). The top four arrived with the 2026-09-09 ceiling raise to 375.
        /// </summary>
        private static readonly int[] FragmentRungs =
        {
            185, 195, 205, 215, 225, 235, 245, 255, 265, 275,
            300, 325, 350, 375,
        };

        // ---- band arithmetic ---------------------------------------------------------------------------

        /// <summary>
        /// The regression this whole change exists for. WorldEventRosterSelector clamps a band's high edge to
        /// MaxConsideredLevel, which collapsed a level-275 gem's band onto the single level 275; once the only
        /// roster member sitting exactly there was removed, a 275 gem could match nothing at all and
        /// DungeonPopulationBuilder opened it with no trash. The dungeon band must not clamp.
        /// </summary>
        [TestMethod]
        public void Trash_band_high_edge_is_not_clamped_to_the_world_events_audience_ceiling()
        {
            var band = DungeonRosterSelector.TrashBand(275);

            Assert.AreEqual(275, band.Low);
            Assert.AreEqual(317, band.High, "275 * 1.15 = 316.25, ceiling 317, and NO clamp to 275");

            // The discriminating half: World Events still clamps, and must keep clamping. If this ever starts
            // reporting 317 then the audience ceiling has been changed out from under World Events, which is
            // exactly the side effect this split was made to avoid.
            var worldEvents = WorldEventRosterSelector.TrashBand(275, DungeonRosterSelector.BandLow, DungeonRosterSelector.BandHigh);
            Assert.AreEqual(WorldEventRosterSelector.MaxConsideredLevel, worldEvents.High,
                "WorldEventRosterSelector must still clamp; only the dungeon band was unclamped");
        }

        /// <summary>
        /// Below the ceiling the two agree exactly, which is what makes this a targeted change rather than a
        /// reworking of the band rule: same proportions, same floor/ceil edge rounding.
        /// </summary>
        [TestMethod]
        public void Below_the_ceiling_the_dungeon_band_matches_the_world_events_band()
        {
            foreach (var level in new[] { 1, 10, 50, 100, 150, 183 })
            {
                var dungeon = DungeonRosterSelector.TrashBand(level);
                var worldEvents = WorldEventRosterSelector.TrashBand(level, DungeonRosterSelector.BandLow, DungeonRosterSelector.BandHigh);

                Assert.AreEqual(worldEvents.Low, dungeon.Low, $"low edge at {level}");
                Assert.AreEqual(worldEvents.High, dungeon.High, $"high edge at {level}");
            }
        }

        [TestMethod]
        public void Band_edges_floor_the_low_and_ceiling_the_high()
        {
            var band = DungeonRosterSelector.TrashBand(101);

            Assert.AreEqual(101, band.Low);
            Assert.AreEqual(117, band.High, "101 * 1.15 = 116.15, ceiling 117");
        }

        /// <summary>
        /// The narrow-elite fallback is preserved from World Events: a band under
        /// NarrowEliteBandFallbackWidth levels wide is not worth treating as its own band. At level 6 the raw
        /// band is [6, 7], one wide, so the elite draw falls back to the trash band instead of a collapsed
        /// range; at level 100 it is [100, 115], fifteen wide, and stands on its own. (Recomputed from
        /// BandHigh's 2026-09-08 narrowing, 1.5 to 1.15 - level 20's old [20, 30] no longer clears the
        /// ten-wide threshold on its own: 20 * 1.15 = 23, only three wide.)
        /// </summary>
        [TestMethod]
        public void Elite_band_falls_back_to_the_trash_band_when_it_would_be_too_narrow()
        {
            Assert.AreEqual(WorldEventRosterSelector.NarrowEliteBandFallbackWidth, 10, "guards the two cases below");

            var narrow = DungeonRosterSelector.EliteBand(6);
            var trash = DungeonRosterSelector.TrashBand(6);
            Assert.AreEqual(trash.Low, narrow.Low);
            Assert.AreEqual(trash.High, narrow.High);

            var wide = DungeonRosterSelector.EliteBand(100);
            Assert.AreEqual(100, wide.Low);
            Assert.AreEqual(115, wide.High);
        }

        /// <summary>
        /// Before this change the band edges were the compiled constants 1.0 and 1.15, always low &lt; high, so
        /// an inverted band (low edge above high edge) was unreachable. Making both edges live /pm tunables --
        /// each sanitized independently, never compared to each other -- means an admin can now produce one
        /// with two commands. The guard that handles it is pre-existing (Band's highEdge &lt; lowEdge collapse,
        /// DungeonRosterSelector.cs:138-139) and untouched by this change; this test is what puts a caller in
        /// front of it for the first time.
        /// </summary>
        [TestMethod]
        public void Elite_band_collapses_to_the_trash_band_when_the_edges_are_inverted()
        {
            var inverted = new DungeonRosterBand(3.0, 1.0);

            var trash = DungeonRosterSelector.TrashBand(10, inverted);
            Assert.AreEqual(30, trash.Low, "10 * 3.0 = 30");
            Assert.AreEqual(30, trash.High, "high edge (10 * 1.0 = 10) collapses UP to the low edge, not down -- non-negative width");

            var elite = DungeonRosterSelector.EliteBand(10, inverted);
            Assert.AreEqual(trash.Low, elite.Low, "collapsed band is also under the narrow-fallback width, so elite matches trash either way");
            Assert.AreEqual(trash.High, elite.High);
        }

        /// <summary>
        /// The fallback comparison is `raw.High - raw.Low &lt; NarrowEliteBandFallbackWidth`
        /// (DungeonRosterSelector.cs:181), so a raw band exactly 10 wide is the first width that does NOT
        /// collapse to the trash band -- matching WorldEventRosterSelector's own `&lt; NarrowEliteBandFallbackWidth`
        /// semantics exactly. A custom band (1.0, 1.5) at level 20 lands on that boundary with no
        /// fractional-ceiling noise: low edge floor(20 * 1.0) = 20, high edge ceil(20 * 1.5) = 30, width 10.
        /// </summary>
        [TestMethod]
        public void Elite_band_stands_on_its_own_at_exactly_the_fallback_width()
        {
            Assert.AreEqual(10, WorldEventRosterSelector.NarrowEliteBandFallbackWidth, "guards the width engineered below");

            var band = new DungeonRosterBand(1.0, 1.5);
            var raw = DungeonRosterSelector.TrashBand(20, band);
            Assert.AreEqual(20, raw.Low);
            Assert.AreEqual(30, raw.High);
            Assert.AreEqual(10, raw.High - raw.Low, "engineered to land exactly on the fallback boundary");

            var elite = DungeonRosterSelector.EliteBand(20, band);
            Assert.AreEqual(raw.Low, elite.Low);
            Assert.AreEqual(raw.High, elite.High, "width-10 does not fall back -- only width < 10 does");
        }

        [TestMethod]
        public void A_zero_or_negative_level_yields_a_degenerate_band_rather_than_an_inverted_one()
        {
            foreach (var level in new[] { 0, -5 })
            {
                var band = DungeonRosterSelector.TrashBand(level);
                Assert.AreEqual(0, band.Low, $"level {level}");
                Assert.AreEqual(0, band.High, $"level {level}");
            }
        }

        // ---- the shipped default, and the live DungeonRosterBand tunable ------------------------------

        /// <summary>
        /// The number this whole change exists to ship: a level-185 gem's trash/elite band with the compiled
        /// defaults. Also pins <see cref="DungeonRosterSelector.BandHigh"/> itself, so a future edit to the
        /// constant fails loudly HERE - against the number this test's name promises - rather than silently
        /// changing every run's ceiling with no test naming the regression.
        /// </summary>
        [TestMethod]
        public void The_shipped_default_band_for_a_level_185_gem_is_185_to_213()
        {
            Assert.AreEqual(1.15, DungeonRosterSelector.BandHigh, 1e-9, "guard: the shipped BandHigh constant");

            var band = DungeonRosterSelector.TrashBand(185);
            Assert.AreEqual(185, band.Low);
            Assert.AreEqual(213, band.High, "185 * 1.15 = 212.75, ceiling 213");
        }

        /// <summary>
        /// Every public DungeonRosterSelector entry point honours an explicitly-passed DungeonRosterBand
        /// instead of silently falling back to DungeonRosterBand.Default. This is the discriminating test for
        /// the optional-parameter threading: a method that forgot to forward `band` into an internal call
        /// would still compile and would still pass every OTHER test in this file, because those tests never
        /// pass a non-default band at all.
        /// </summary>
        [TestMethod]
        public void A_custom_band_flows_through_every_public_entry_point()
        {
            // A deliberately WIDE custom band (low 0.5, high 2.0) so members outside the compiled default
            // [100, 115] band, but inside this custom one, are the ones actually drawn - proof the methods
            // used the custom band, not the default.
            var custom = new DungeonRosterBand(0.5, 2.0);

            var levels = new Dictionary<uint, int> { [700] = 50, [701] = 190, [710] = 195, [711] = 30 };
            int LevelOfLocal(uint w) => levels[w];

            var family = new SpeciesTableDef
            {
                Id = "custom-band-family",
                Members = new List<SpeciesMemberDef>
                {
                    new SpeciesMemberDef { Wcid = 700, Role = 0 }, // trash, level 50 -- outside default [100,115], inside custom [50,200]
                    new SpeciesMemberDef { Wcid = 701, Role = 0 }, // trash, level 190 -- outside default, inside custom
                    new SpeciesMemberDef { Wcid = 710, Role = 1 }, // elite, level 195 -- outside default, inside custom
                    new SpeciesMemberDef { Wcid = 711, Role = 1 }, // elite, level 30 -- outside custom [50,200] too
                }
            };
            var tables = new Dictionary<string, SpeciesTableDef> { [family.Id] = family };

            // TrashBand / EliteBand
            var trashBand = DungeonRosterSelector.TrashBand(100, custom);
            Assert.AreEqual(50, trashBand.Low);
            Assert.AreEqual(200, trashBand.High);
            var eliteBand = DungeonRosterSelector.EliteBand(100, custom);
            Assert.AreEqual(50, eliteBand.Low);
            Assert.AreEqual(200, eliteBand.High);

            // IsEligible: eligible under the custom band (wcid 700/701 in range), would be eligible under the
            // default too (level 50 and 190 are both outside default [100,115]) -- so this alone would not
            // discriminate; the PickFamily/PoolFor assertions below are what actually pin the custom band.
            Assert.IsTrue(DungeonRosterSelector.IsEligible(family, 100, LevelOfLocal, custom));

            // PickFamily: only eligible at all when the custom band is honoured (default band finds nothing).
            Assert.IsNull(DungeonRosterSelector.PickFamily(tables, DungeonGemSpec.Any, new List<string>(), new List<string>(), 100, LevelOfLocal, new Random(1)),
                "guard: under the DEFAULT band this family has no trash member in [100,115]");
            var picked = DungeonRosterSelector.PickFamily(tables, DungeonGemSpec.Any, new List<string>(), new List<string>(), 100, LevelOfLocal, new Random(1), custom);
            Assert.AreEqual(family.Id, picked?.Id, "the custom band makes the family eligible");

            // PoolFor: trash pool is exactly {700, 701}; elite pool is exactly {710} (711 is outside even the
            // custom band).
            var trashPool = DungeonRosterSelector.PoolFor(family, 100, DungeonRole.Trash, LevelOfLocal, custom);
            CollectionAssert.AreEquivalent(new List<uint> { 700, 701 }, trashPool);
            var elitePool = DungeonRosterSelector.PoolFor(family, 100, DungeonRole.Elite, LevelOfLocal, custom);
            CollectionAssert.AreEquivalent(new List<uint> { 710 }, elitePool);

            // PickPopulation: with the custom band, every drawn wcid is one of the custom-band-eligible ones.
            var picks = DungeonRosterSelector.PickPopulation(family, 100, 10, 0.3, LevelOfLocal, new Random(1), custom);
            Assert.IsTrue(picks.Count > 0);
            Assert.IsTrue(picks.All(p => p.Wcid == 700 || p.Wcid == 701 || p.Wcid == 710), "every pick came from the custom-band pools");

            // BandMedianHealth: only 700/701 (the custom-band trash pool) contribute.
            var health = new Dictionary<uint, uint> { [700] = 1000, [701] = 3000 };
            var median = DungeonRosterSelector.BandMedianHealth(tables, 100, LevelOfLocal, w => health.TryGetValue(w, out var h) ? h : 0, custom);
            Assert.AreEqual(1000u, median, "lower-of-two-middles over {1000, 3000}");
            Assert.AreEqual(0u, DungeonRosterSelector.BandMedianHealth(tables, 100, LevelOfLocal, w => health.TryGetValue(w, out var h) ? h : 0),
                "guard: under the DEFAULT band neither 700 nor 701 is in range, so the pool is empty");

            // PickBossCandidate: draws from the same pool PoolFor(..., Elite, ...) would (elite pool {710},
            // falling back to trash only if empty) -- here it is non-empty, so the candidate is 710.
            var candidate = DungeonRosterSelector.PickBossCandidate(family, 100, LevelOfLocal, new Random(1), new List<uint>(), custom);
            Assert.AreEqual(710u, candidate);
            Assert.AreEqual(0u, DungeonRosterSelector.PickBossCandidate(family, 100, LevelOfLocal, new Random(1), new List<uint>()),
                "guard: under the DEFAULT band this family can field no candidate at all");
        }

        // ---- SanitizeBandDial (ThreadDungeonSpawner) ---------------------------------------------------

        /// <summary>
        /// The discriminating test for the "0 is not a valid disable" rule: SanitizeDoubleDial (the OTHER
        /// dial sanitizer in this file's sibling class) treats 0 as a valid explicit value and only rejects
        /// NaN/Infinity/negative. A test that covered only those three would pass against that wrong
        /// implementation too - asserting 0 falls back to the compiled default is what actually pins
        /// SanitizeBandDial's own rule.
        /// </summary>
        [TestMethod]
        public void SanitizeBandDial_rejects_non_positive_and_non_finite_values_and_clamps_the_ceiling()
        {
            const double fallback = 1.15;
            const double ceiling = 5.0;

            Assert.AreEqual(fallback, ThreadDungeonSpawner.SanitizeBandDial(double.NaN, fallback, ceiling));
            Assert.AreEqual(fallback, ThreadDungeonSpawner.SanitizeBandDial(double.PositiveInfinity, fallback, ceiling));
            Assert.AreEqual(fallback, ThreadDungeonSpawner.SanitizeBandDial(-1, fallback, ceiling));
            Assert.AreEqual(fallback, ThreadDungeonSpawner.SanitizeBandDial(0, fallback, ceiling), "0 is a degenerate edge here, NOT a valid explicit disable");

            Assert.AreEqual(ceiling, ThreadDungeonSpawner.SanitizeBandDial(99, fallback, ceiling));

            // A legitimate in-range value passes through untouched.
            Assert.AreEqual(2.0, ThreadDungeonSpawner.SanitizeBandDial(2.0, fallback, ceiling));
        }

        // ---- live-tree tripwire ------------------------------------------------------------------------

        /// <summary>
        /// Every Raw Fragment rung must find at least one family in the REAL shipped species tables. This is
        /// the test that would have caught the regression: with the audience clamp in place and the level-275
        /// Frozen Gearknight removed, PickFamily returns null at 275 and the gem opens empty.
        ///
        /// Levels come from each member's own `level` field rather than from the world database, which the
        /// unit-test harness has none of. That is a faithful stand-in today and not an assumption: on
        /// 2026-09-06 a join of all 1544 roster wcids against ace_world.weenie_properties_int type 25 found
        /// ZERO members whose file level disagreed with the live weenie level, and zero missing from the
        /// database. Production still reads the live level (ThreadDungeonSpawner.LiveLevelOf); if the two
        /// ever drift, this test measures the file and says so here rather than pretending otherwise.
        /// </summary>
        [TestMethod]
        public void Every_raw_fragment_rung_finds_a_family_in_the_shipped_species_tables()
        {
            var tables = LoadShippedSpeciesTables();
            if (tables == null)
                Assert.Inconclusive("Could not locate Content/events/axes/species by walking up from the test assembly -- skipping the live-tree check.");

            Assert.IsTrue(tables.Count > 0, "the shipped species folder parsed to zero tables");

            var levels = new Dictionary<uint, int>();
            foreach (var member in tables.Values.SelectMany(t => t.Members))
                levels[member.Wcid] = member.Level;

            int LevelOf(uint wcid) => levels.TryGetValue(wcid, out var level) ? level : 0;

            var empty = new List<string>();

            foreach (var rung in FragmentRungs)
            {
                // Resolved band, not the natural one - this is what DungeonPopulationBuilder.cs:671-673
                // actually hands PickFamily. It matters from the 2026-09-09 ceiling raise onward: the highest
                // member level in the shipped species tables is 320 (measured 2026-09-09 across
                // Content/events/axes/species), so the natural band holds NOTHING at the 325, 350 and 375
                // rungs and only the low-edge widening reaches a family there.
                //
                // Nothing is weakened for the ten original rungs, because ResolveEligibilityBand returns the
                // natural band UNCHANGED whenever that band already fields MinEligibleFamilies tables, which
                // it does at every rung up to 275 - the sibling case below still pins the natural band's own
                // population at 275.
                var band = DungeonRosterSelector.ResolveEligibilityBand(tables, rung, LevelOf, DungeonRosterBand.Default,
                    DungeonRosterSelector.BandLowFloor, DungeonRosterSelector.MinEligibleFamilies);

                var family = DungeonRosterSelector.PickFamily(tables, DungeonGemSpec.Any, new List<string>(),
                    new List<string>(), rung, LevelOf, new Random(rung), band);

                var eligible = tables.Values.Count(t => DungeonRosterSelector.IsEligible(t, rung, LevelOf, band));

                if (family == null || eligible == 0)
                    empty.Add($"level {rung} (band {DungeonRosterSelector.TrashBand(rung, band)}, {eligible} eligible families)");
            }

            Assert.AreEqual(0, empty.Count,
                "Raw Fragment rungs that would open a dungeon with no trash at all: " + string.Join("; ", empty));
        }

        /// <summary>
        /// Names the exact number the fix restored, so a later roster prune that quietly re-empties the
        /// level-275 rung is caught here rather than in a play session. 275 was the top rung when this was
        /// written; the 2026-09-09 ceiling raise put four rungs above it, and this case stays pinned to 275
        /// deliberately, because the number below was measured against the NATURAL band and only 275 and
        /// below still fill one (the four new rungs reach their families through the low-edge widening, which
        /// the sibling rung tripwire above covers). Recomputed for BandHigh's 2026-09-08 narrowing
        /// (1.5 to 1.15, band [275, 413] to [275, 317]): 5 families and 16 trash members against the real
        /// shipped tables at the time of writing (was 6 and 17 under the wider band); asserted as a floor
        /// rather than an equality, since adding roster members must never fail a test.
        ///
        /// Floor moved 16 to 15 on 2026-09-08 with the removal of wcid 88161 (Vicious Remoran Sapper, level
        /// 280, in-band), which carried 1,760 authored health against wcid 52710 of the same name and level
        /// carrying 11,760 - one of the two was broken data, and 88161 was the low, misleading copy (owner
        /// ruling 2026-09-08). This is a deliberate, reviewed prune, not an accidental re-emptying, so the
        /// floor moves with it rather than the test being weakened generically; 52710 keeps the remoran
        /// family eligible at this band, so the 5-family floor is untouched. This is still a snapshot floor
        /// for catching future accidental shrinkage, not an inviolable design invariant - a thin band here no
        /// longer risks an empty run on its own, because DungeonRosterSelector.ResolveEligibilityBand and
        /// ResolveFamilyTrashBand step the band's low edge down automatically when a pool is thin, which is
        /// the real invariant the sibling zero-trash test below covers unmodified.
        /// </summary>
        [TestMethod]
        public void The_275_rung_draws_from_the_creatures_above_the_audience_ceiling()
        {
            var tables = LoadShippedSpeciesTables();
            if (tables == null)
                Assert.Inconclusive("Could not locate Content/events/axes/species by walking up from the test assembly -- skipping the live-tree check.");

            var levels = new Dictionary<uint, int>();
            foreach (var member in tables.Values.SelectMany(t => t.Members))
                levels[member.Wcid] = member.Level;

            int LevelOf(uint wcid) => levels.TryGetValue(wcid, out var level) ? level : 0;

            var band = DungeonRosterSelector.TrashBand(275);
            var trashInBand = tables.Values
                .SelectMany(t => t.Members)
                .Count(m => m.Role == 0 && band.Contains(m.Level));

            var eligible = tables.Values.Count(t => DungeonRosterSelector.IsEligible(t, 275, LevelOf));

            Assert.IsTrue(eligible >= 5, $"level-275 eligible families fell to {eligible}, expected at least 5");
            Assert.IsTrue(trashInBand >= 15, $"level-275 trash members fell to {trashInBand}, expected at least 15");

            // Every one of them sits ABOVE the World Events audience ceiling, which is the point: under the
            // clamped band this set is empty.
            Assert.IsTrue(band.Low >= WorldEventRosterSelector.MaxConsideredLevel);
        }

        // ---- adaptive band widening ----------------------------------------------------------------------

        /// <summary>
        /// The widening fixture. A level-200 gem's natural band is [200, 230]; the members below 200 are
        /// reachable only by stepping the LOW edge down, and wcid 999 (level 400) is above the high edge and
        /// must never become reachable at all.
        /// </summary>
        private static readonly Dictionary<uint, int> WideLevels = new Dictionary<uint, int>
        {
            [900] = 205, [901] = 175, [902] = 170, [903] = 165, [904] = 160, [905] = 155, [906] = 150, [907] = 130,
            [910] = 160, // elite, below the natural band
            [920] = 205, [921] = 205, [922] = 205, // one in-band trash member each, for three more families
            [999] = 400,
        };

        private static int WideLevelOf(uint wcid) => WideLevels[wcid];

        private static SpeciesTableDef WideFamily() => new SpeciesTableDef
        {
            Id = "wide",
            Members = new List<SpeciesMemberDef>
            {
                new SpeciesMemberDef { Wcid = 900, Role = 0 }, new SpeciesMemberDef { Wcid = 901, Role = 0 },
                new SpeciesMemberDef { Wcid = 902, Role = 0 }, new SpeciesMemberDef { Wcid = 903, Role = 0 },
                new SpeciesMemberDef { Wcid = 904, Role = 0 }, new SpeciesMemberDef { Wcid = 905, Role = 0 },
                new SpeciesMemberDef { Wcid = 906, Role = 0 }, new SpeciesMemberDef { Wcid = 907, Role = 0 },
                new SpeciesMemberDef { Wcid = 910, Role = 1 },
                new SpeciesMemberDef { Wcid = 999, Role = 0 },
            }
        };

        private static Dictionary<string, SpeciesTableDef> WideTables()
        {
            var tables = new Dictionary<string, SpeciesTableDef> { ["wide"] = WideFamily() };

            foreach (var (id, wcid) in new[] { ("f2", 920u), ("f3", 921u), ("f4", 922u) })
                tables[id] = new SpeciesTableDef { Id = id, Members = new List<SpeciesMemberDef> { new SpeciesMemberDef { Wcid = wcid, Role = 0 } } };

            return tables;
        }

        [TestMethod]
        public void The_low_edge_ladder_steps_down_and_lands_exactly_on_the_floor()
        {
            var ladder = DungeonRosterSelector.LowEdgeLadder(1.0, 0.60).ToList();

            CollectionAssert.AreEqual(new[] { 1.0, 0.95, 0.90, 0.85, 0.80, 0.75, 0.70, 0.65, 0.60 },
                ladder.Select(r => Math.Round(r, 6)).ToArray(),
                "eight steps of 0.05, ending exactly on the floor rather than a hair under it");

            // Floor at or above the start: the natural low edge and nothing else. This is what makes a floor
            // of 1.0 a total no-op against the shipped 1.0 low edge.
            CollectionAssert.AreEqual(new[] { 1.0 }, DungeonRosterSelector.LowEdgeLadder(1.0, 1.0).ToList().ToArray());
            CollectionAssert.AreEqual(new[] { 1.0 }, DungeonRosterSelector.LowEdgeLadder(1.0, 1.5).ToList().ToArray());
            CollectionAssert.AreEqual(new[] { 1.0 }, DungeonRosterSelector.LowEdgeLadder(1.0, double.NaN).ToList().ToArray());
        }

        /// <summary>
        /// I-B: the high edge never moves. Checked across a wide level sweep and against every widening entry
        /// point, at settings far more aggressive than anything shipped.
        /// </summary>
        [TestMethod]
        public void IB_the_high_edge_never_moves_at_any_level_under_any_threshold_setting()
        {
            var tables = WideTables();
            var family = WideFamily();

            foreach (var level in new[] { 1, 20, 100, 185, 200, 250, 275, 300, 1000 })
            {
                var natural = DungeonRosterBand.Default;
                var expectedHigh = DungeonRosterSelector.TrashBand(level, natural).High;

                foreach (var floor in new[] { 1.0, 0.9, 0.6, 0.25, 0.05 })
                {
                    foreach (var minFamilies in new[] { 0, 4, 999 })
                    {
                        var eligibility = DungeonRosterSelector.ResolveEligibilityBand(tables, level, WideLevelOf, natural, floor, minFamilies);
                        Assert.AreEqual(expectedHigh, DungeonRosterSelector.TrashBand(level, eligibility).High,
                            $"eligibility widening moved the high edge at level {level}, floor {floor}, minFamilies {minFamilies}");
                        Assert.AreEqual(natural.High, eligibility.High, 1e-9);

                        foreach (var minPool in new[] { 0, 6, 999 })
                        {
                            var trash = DungeonRosterSelector.ResolveFamilyTrashBand(family, level, WideLevelOf, eligibility, floor, minPool);
                            Assert.AreEqual(expectedHigh, DungeonRosterSelector.TrashBand(level, trash).High,
                                $"family widening moved the high edge at level {level}, floor {floor}, minPool {minPool}");
                        }

                        var elite = DungeonRosterSelector.ResolveFamilyEliteBand(family, level, WideLevelOf, eligibility, floor);
                        Assert.AreEqual(natural.High, elite.High, 1e-9,
                            $"elite widening moved the high edge at level {level}, floor {floor}");
                    }
                }
            }

            // The discriminating half: the level-400 member sits above every one of those high edges and is
            // never drawable, no matter how far the low edge steps down.
            var widest = DungeonRosterSelector.ResolveFamilyTrashBand(family, 200, WideLevelOf, DungeonRosterBand.Default, 0.05, 999);
            Assert.IsFalse(DungeonRosterSelector.PoolFor(family, 200, DungeonRole.Trash, WideLevelOf, widest).Contains(999u),
                "widening the low edge must never reach a member above the high edge");
        }

        [TestMethod]
        public void Family_eligibility_widens_only_until_the_threshold_is_met()
        {
            var tables = WideTables();

            // At the natural band four families each field at least one member, so nothing widens.
            Assert.AreEqual(4, DungeonRosterSelector.EligibleFamilyCount(tables, 200, WideLevelOf));
            Assert.AreEqual(DungeonRosterBand.Default.Low,
                DungeonRosterSelector.ResolveEligibilityBand(tables, 200, WideLevelOf, DungeonRosterBand.Default, 0.60, 4).Low, 1e-9);

            // Wanting five families is unsatisfiable here, so the search runs to the floor rather than
            // silently handing back the natural band.
            Assert.AreEqual(0.60,
                DungeonRosterSelector.ResolveEligibilityBand(tables, 200, WideLevelOf, DungeonRosterBand.Default, 0.60, 5).Low, 1e-9);

            // A threshold of 0 disables the widening outright.
            Assert.AreEqual(DungeonRosterBand.Default.Low,
                DungeonRosterSelector.ResolveEligibilityBand(tables, 200, WideLevelOf, DungeonRosterBand.Default, 0.60, 0).Low, 1e-9);
        }

        [TestMethod]
        public void A_thin_family_pool_widens_its_own_low_edge_and_stops_at_the_first_ratio_that_satisfies_it()
        {
            var family = WideFamily();
            var natural = DungeonRosterBand.Default;

            Assert.AreEqual(1, DungeonRosterSelector.PoolFor(family, 200, DungeonRole.Trash, WideLevelOf, natural).Count,
                "only wcid 900 (level 205) sits in the natural [200, 230] band");

            var widened = DungeonRosterSelector.ResolveFamilyTrashBand(family, 200, WideLevelOf, natural, 0.60, 6);

            // Members at 175, 170, 165, 160, 155 are picked up as the edge steps down; the sixth arrives at
            // x0.75 ([150, 230]), which is where the search stops - it does NOT run on to the floor.
            Assert.AreEqual(0.75, widened.Low, 1e-9);

            var pool = DungeonRosterSelector.PoolFor(family, 200, DungeonRole.Trash, WideLevelOf, widened);
            Assert.AreEqual(7, pool.Count);
            Assert.IsFalse(pool.Contains(907u), "the level-130 member is still out of reach at x0.75");
            Assert.IsFalse(pool.Contains(999u), "and the level-400 member is out of reach at every width");

            // A threshold of 0 disables per-family widening.
            Assert.AreEqual(natural.Low, DungeonRosterSelector.ResolveFamilyTrashBand(family, 200, WideLevelOf, natural, 0.60, 0).Low, 1e-9);
        }

        [TestMethod]
        public void The_elite_band_widens_only_while_the_elite_pool_is_empty()
        {
            var family = WideFamily();
            var natural = DungeonRosterBand.Default;

            Assert.AreEqual(0, DungeonRosterSelector.PoolFor(family, 200, DungeonRole.Elite, WideLevelOf, natural)
                .Count(w => w == 910u), "the family's only elite (level 160) is outside the natural band");

            var widened = DungeonRosterSelector.ResolveFamilyEliteBand(family, 200, WideLevelOf, natural, 0.60);

            Assert.AreEqual(0.80, widened.Low, 1e-9, "x0.80 gives [160, 230], the first width that reaches the elite");
            CollectionAssert.Contains(DungeonRosterSelector.PoolFor(family, 200, DungeonRole.Elite, WideLevelOf, widened), 910u);
        }

        [TestMethod]
        public void The_natural_low_edge_is_the_uplift_threshold_and_ignores_any_widening()
        {
            Assert.AreEqual(200, DungeonRosterSelector.NaturalLowEdge(200));
            Assert.AreEqual(275, DungeonRosterSelector.NaturalLowEdge(275));

            // It reads the band it is GIVEN, so an admin who moved dynamic_dungeons_roster_band_low also
            // moves the uplift threshold with it, which is the only self-consistent reading.
            Assert.AreEqual(180, DungeonRosterSelector.NaturalLowEdge(200, new DungeonRosterBand(0.9, 1.15)));
        }

        // ---- helpers -----------------------------------------------------------------------------------

        /// <summary>
        /// Parses the real Content/events/axes/species/*.json through the SAME parser production uses
        /// (WorldEventAxisStore.Parse's speciesFiles path), so a file this test accepts is one the server
        /// would accept. Returns null when the folder cannot be located.
        /// </summary>
        private static IReadOnlyDictionary<string, SpeciesTableDef> LoadShippedSpeciesTables()
        {
            var speciesDir = FindSpeciesDir();
            if (speciesDir == null)
                return null;

            var files = Directory.GetFiles(speciesDir, "*.json")
                .OrderBy(p => p, StringComparer.Ordinal)
                .Select(p => (fileName: Path.GetFileName(p), json: File.ReadAllText(p)))
                .ToList();

            var store = WorldEventAxisStore.Parse(null, null, null, null, null, null, null, files);
            return store.SpeciesTables;
        }

        /// <summary>
        /// Walks up from the test assembly for Content/events/axes/species, the same shape
        /// WorldContentPlannerTests.FindRepoContentDir uses. Kept private here rather than shared: the two
        /// look for different things and each stays readable on its own.
        /// </summary>
        private static string FindSpeciesDir()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "Content", "events", "axes", "species");

                if (Directory.Exists(candidate) && Directory.EnumerateFiles(candidate, "*.json").Any())
                    return candidate;

                dir = dir.Parent;
            }

            return null;
        }
    }
}
