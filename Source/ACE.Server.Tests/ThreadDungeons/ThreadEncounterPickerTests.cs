using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using static ACE.Server.Tests.ThreadDungeons.ThreadEncounterTestFixtures;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The pure encounter picker (ThreadEncounterPicker): determinism, the switch and the slot count, every requirement
    /// filter, spacing, the outdoor rules (point kind, straight-line global distance, the ground-probe seam), the slot
    /// distribution, and dropped slots. No world, no PropertyManager key.
    /// </summary>
    [TestClass]
    public class ThreadEncounterPickerTests
    {
        private static List<ThreadEncounterCandidate> Line(int count, float step = 30f)
            => Enumerable.Range(0, count).Select(i => Candidate(Point(DungeonCell(), i * step, 0f))).ToList();

        private static string Fingerprint(ThreadEncounterPlan plan)
            => string.Join("|", plan.Picks.Select(p => $"{p.Slot}:{p.Def.Id}:{p.Point.Point.X}:{p.Seed}")) + "/" + string.Join("|", plan.Dropped);

        // ---- determinism ----------------------------------------------------------------------------------------

        [TestMethod]
        public void The_same_request_always_gives_the_same_plan_and_the_seed_matters()
        {
            var types = new[] { Type("a"), Type("b", weight: 3) };
            var candidates = Line(10);

            var first = ThreadEncounterPicker.Pick(Request(types, candidates, seed: 1234, max: 4));
            var again = ThreadEncounterPicker.Pick(Request(types, candidates, seed: 1234, max: 4));

            Assert.AreEqual(4, first.Picks.Count);
            Assert.AreEqual(Fingerprint(first), Fingerprint(again));

            // Discriminating control: some other seed produces a different plan.
            Assert.IsTrue(Enumerable.Range(0, 20).Any(s => Fingerprint(ThreadEncounterPicker.Pick(Request(types, candidates, seed: s, max: 4))) != Fingerprint(first)));
        }

        [TestMethod]
        public void The_stream_is_salted_apart_from_the_puzzle_picker()
        {
            Assert.AreNotEqual(0x5A17E5EDu, ThreadEncounterPicker.StreamSalt);

            var mine = ThreadEncounterPicker.NewStream(99);
            var puzzle = new ACE.Server.Entity.SkyDecorRandom(unchecked(99u * 2654435761u ^ 0x5A17E5EDu));

            for (var i = 0; i < 4; i++)
                puzzle.NextUInt();

            Assert.AreNotEqual(puzzle.NextUInt(), mine.NextUInt());
        }

        // ---- the switch and the slot count ----------------------------------------------------------------------

        [TestMethod]
        public void Switched_off_or_zero_max_or_zero_chance_places_nothing()
        {
            var types = new[] { Type("a") };
            var candidates = Line(5);

            var off = Request(types, candidates);
            off.Enabled = false;
            Assert.AreEqual(0, ThreadEncounterPicker.Pick(off).Picks.Count);
            Assert.IsNotNull(ThreadEncounterPicker.Pick(off).Skipped);

            var zero = ThreadEncounterPicker.Pick(Request(types, candidates, max: 0));
            Assert.AreEqual(0, zero.Picks.Count);
            Assert.AreEqual(0, zero.SlotsRolled);

            var never = ThreadEncounterPicker.Pick(Request(types, candidates, max: 4, chance: 0.0));
            Assert.AreEqual(4, never.SlotsRolled);
            Assert.AreEqual(0, never.SlotsActive);
            Assert.AreEqual(0, never.Picks.Count);

            // Control: the same request switched on, max 2, chance 1 places two.
            Assert.AreEqual(2, ThreadEncounterPicker.Pick(Request(types, candidates, max: 2)).Picks.Count);
        }

        [TestMethod]
        public void Max_per_run_is_capped_at_the_ceiling()
        {
            var plan = ThreadEncounterPicker.Pick(Request(new[] { Type("a") }, Line(20), max: 99));

            Assert.AreEqual(ThreadEncounterPicker.MaxPerRunCeiling, plan.SlotsRolled);
            Assert.AreEqual(ThreadEncounterPicker.MaxPerRunCeiling, plan.Picks.Count);
        }

        // ---- countWeights (owner ruling 2026-11-11) -------------------------------------------------------------

        private static readonly double[] ShippedWeights = { 20, 40, 25, 10, 4, 1 };

        private static int[] CountHistogram(IReadOnlyList<double> weights, int max, int seeds, double chance = 1.0)
        {
            var histogram = new int[ThreadEncounterPicker.MaxPerRunCeiling + 1];

            for (var s = 0; s < seeds; s++)
            {
                var req = Request(new[] { Type("a") }, Line(10), seed: s, max: max, chance: chance);
                req.CountWeights = weights;
                histogram[ThreadEncounterPicker.Pick(req).SlotsActive]++;
            }

            return histogram;
        }

        [TestMethod]
        public void The_shipped_count_weights_and_defaults_give_about_one_and_a_half_encounters_and_rarely_five()
        {
            // Owner ruling 2026-11-11: P(0..5) = 20/40/25/10/4/1 percent, mean 1.41, max_per_run 5, slot_chance stays 0.5.
            var store = ThreadDungeonStoreTests.LoadShipped();
            CollectionAssert.AreEqual(ShippedWeights, store.Encounters.CountWeights.ToArray(), "encounters.json ships the ruled table");
            Assert.AreEqual(5, ThreadDungeonRun.DefaultEncountersMaxPerRun);
            Assert.AreEqual(0.5, ThreadDungeonRun.DefaultEncounterSlotChance, "slot_chance is only the fallback");
            Assert.AreEqual(8, ThreadEncounterPicker.MaxPerRunCeiling);
            Assert.AreEqual(5, ThreadDungeonRun.ClampEncountersMaxPerRun(ThreadDungeonRun.DefaultEncountersMaxPerRun), "the default sits inside the clamp");

            const int seeds = 20000;
            var histogram = CountHistogram(ShippedWeights, ThreadDungeonRun.DefaultEncountersMaxPerRun, seeds);

            var expected = new[] { 0.20, 0.40, 0.25, 0.10, 0.04, 0.01 };
            for (var k = 0; k < expected.Length; k++)
                Assert.AreEqual(expected[k], histogram[k] / (double)seeds, 0.012, $"P({k})");

            for (var k = expected.Length; k < histogram.Length; k++)
                Assert.AreEqual(0, histogram[k], $"no run draws {k}");

            var mean = Enumerable.Range(0, histogram.Length).Sum(k => k * histogram[k]) / (double)seeds;
            Assert.AreEqual(1.41, mean, 0.03);
        }

        [TestMethod]
        public void Count_weights_activate_the_first_N_slots_and_ignore_the_slot_chance()
        {
            for (var s = 0; s < 200; s++)
            {
                var req = Request(new[] { Type("a") }, Line(10), seed: s, max: 5, chance: 0.0);
                req.CountWeights = new double[] { 0, 0, 0, 1 };
                var plan = ThreadEncounterPicker.Pick(req);

                Assert.AreEqual(ThreadEncounterPlan.MechanismCountWeights, plan.Mechanism);
                Assert.AreEqual(3, plan.SlotsActive, "chance 0 would give none; the weights say exactly 3");
                CollectionAssert.AreEqual(new[] { 0, 1, 2 }, plan.Picks.Select(p => p.Slot).ToArray());
            }
        }

        [TestMethod]
        public void Count_weights_above_the_cap_fold_into_the_cap()
        {
            CollectionAssert.AreEqual(new[] { 20.0, 40.0, 40.0 }, ThreadEncounterPicker.FoldCountWeights(ShippedWeights, 2));
            CollectionAssert.AreEqual(new[] { 100.0 }, ThreadEncounterPicker.FoldCountWeights(ShippedWeights, 0));
            CollectionAssert.AreEqual(new[] { 20.0, 40.0, 25.0, 10.0, 4.0, 1.0, 0.0, 0.0 }, ThreadEncounterPicker.FoldCountWeights(ShippedWeights, 7), "a short table weighs the unlisted counts 0");

            // Through the picker: max 2 keeps P(0) at 20 percent and P(2) takes 25 + 10 + 4 + 1.
            const int seeds = 20000;
            var histogram = CountHistogram(ShippedWeights, 2, seeds);
            Assert.AreEqual(0.20, histogram[0] / (double)seeds, 0.012);
            Assert.AreEqual(0.40, histogram[1] / (double)seeds, 0.012);
            Assert.AreEqual(0.40, histogram[2] / (double)seeds, 0.012);
            Assert.AreEqual(seeds, histogram[0] + histogram[1] + histogram[2]);
        }

        [TestMethod]
        public void Draw_count_reads_cumulative_bands_and_skips_zero_weights()
        {
            var folded = new[] { 1.0, 0.0, 3.0 };
            Assert.AreEqual(0, ThreadEncounterPicker.DrawCount(folded, 0.0));
            Assert.AreEqual(0, ThreadEncounterPicker.DrawCount(folded, 0.2499));
            Assert.AreEqual(2, ThreadEncounterPicker.DrawCount(folded, 0.25));
            Assert.AreEqual(2, ThreadEncounterPicker.DrawCount(folded, 0.9999999));
            Assert.AreEqual(0, ThreadEncounterPicker.DrawCount(new[] { 0.0, 0.0 }, 0.5), "all-zero weights draw 0");
        }

        [TestMethod]
        public void Without_count_weights_the_slot_chance_roll_is_unchanged()
        {
            for (var s = 0; s < 50; s++)
            {
                var plain = ThreadEncounterPicker.Pick(Request(new[] { Type("a") }, Line(10), seed: s, max: 5, chance: 0.5));
                var nulled = Request(new[] { Type("a") }, Line(10), seed: s, max: 5, chance: 0.5);
                nulled.CountWeights = null;
                var empty = Request(new[] { Type("a") }, Line(10), seed: s, max: 5, chance: 0.5);
                empty.CountWeights = Array.Empty<double>();

                Assert.AreEqual(ThreadEncounterPlan.MechanismSlotChance, plain.Mechanism);
                Assert.AreEqual(Fingerprint(plain), Fingerprint(ThreadEncounterPicker.Pick(nulled)));
                Assert.AreEqual(Fingerprint(plain), Fingerprint(ThreadEncounterPicker.Pick(empty)));
            }
        }

        [TestMethod]
        public void The_count_draw_comes_from_the_picker_stream_alone()
        {
            // Same seed, same weights: same plan. The draw is the stream's first NextDouble, so it is reproducible from the
            // run seed with no other input; the count never reads System.Random or the population builder's stream.
            for (var s = 0; s < 50; s++)
            {
                var a = Request(new[] { Type("a") }, Line(10), seed: s, max: 5);
                a.CountWeights = ShippedWeights;
                var b = Request(new[] { Type("a") }, Line(10), seed: s, max: 5);
                b.CountWeights = ShippedWeights;

                var plan = ThreadEncounterPicker.Pick(a);
                Assert.AreEqual(Fingerprint(plan), Fingerprint(ThreadEncounterPicker.Pick(b)));

                var expected = ThreadEncounterPicker.DrawCount(ThreadEncounterPicker.FoldCountWeights(ShippedWeights, 5), ThreadEncounterPicker.NewStream(s).NextDouble());
                Assert.AreEqual(expected, plan.SlotsActive, $"seed {s}");
            }
        }

        // ---- the Defense portal prefilter (review R1, 2026-11-11) -----------------------------------------------

        [TestMethod]
        public void Portal_points_in_reach_count_same_landblock_curated_points_from_min_to_max_metres()
        {
            var config = new DefenseConfig();
            Assert.AreEqual(8f, config.PortalMinDistance);
            Assert.AreEqual(25f, config.PortalMaxDistance);

            var pillar = Candidate(Point(DungeonCell(), 100f, 100f));
            var points = new List<ThreadEncounterCandidate>
            {
                pillar,                                                                  // itself: never counted
                Candidate(Point(DungeonCell(), 108f, 100f)),                             // 8 m: in (inclusive)
                Candidate(Point(DungeonCell(), 100f, 125f)),                             // 25 m: in (inclusive)
                Candidate(Point(DungeonCell(), 107.9f, 100f)),                           // under 8 m: out
                Candidate(Point(DungeonCell(), 100f, 125.1f)),                           // over 25 m: out
                Candidate(Point(DungeonCell(), 115f, 100f, curated: false)),             // not curated: out
                Candidate(Point(((uint)0x0151 << 16) | 0x018A, 115f, 100f)),             // other landblock: out
            };

            Assert.AreEqual(2, DefenseEncounterType.PortalPointsInReach(pillar, points, false, config));
            Assert.AreEqual(0, DefenseEncounterType.PortalPointsInReach(pillar, null, false, config));

            // portalMinDistance may be 0 (ParseConfig allows 0 <= min): the pillar's own point still never counts.
            var fromZero = new DefenseConfig { PortalMinDistance = 0f };
            Assert.AreEqual(0, DefenseEncounterType.PortalPointsInReach(pillar, new[] { pillar }, false, fromZero));
            Assert.AreEqual(1, DefenseEncounterType.PortalPointsInReach(pillar, new[] { pillar, Candidate(Point(DungeonCell(), 100f, 100f)) }, false, fromZero), "control: another point on the same spot counts");
        }

        [TestMethod]
        public void Outdoors_only_open_ground_points_count_toward_the_portals()
        {
            const ushort lb = 0x5456;
            var pillar = Candidate(Point(OutdoorCell(lb, 100, 100), 100f, 100f, kind: "outdoor"), outdoor: true);
            var open = Candidate(Point(OutdoorCell(lb, 115, 100), 115f, 100f, kind: "outdoor"), outdoor: true);
            var interior = Candidate(Point(OutdoorCell(lb, 100, 115), 100f, 115f, kind: "interior"), outdoor: true);

            Assert.AreEqual(1, DefenseEncounterType.PortalPointsInReach(pillar, new[] { pillar, open, interior }, true, new DefenseConfig()));
        }

        [TestMethod]
        public void The_prefilter_rejects_exactly_where_Place_would_refuse_its_portals()
        {
            // With no walk graph the prefilter and PickPortals read the same points by the same rule, so they agree.
            var defense = new DefenseEncounterType();
            var def = Type("defense", kind: DefenseEncounterType.KindName);
            var config = new DefenseConfig();

            var lonely = Candidate(Point(DungeonCell(), 10f, 10f));
            var near = Candidate(Point(DungeonCell(), 60f, 10f));
            var neighbour = Candidate(Point(DungeonCell(), 75f, 10f));
            var all = new List<ThreadEncounterCandidate> { lonely, near, neighbour };

            foreach (var c in all)
            {
                var portals = DefenseEncounterType.PickPortals(c.Global, c.Landblock,
                    all.Where(o => !ReferenceEquals(o, c)).Select(o => new PortalWaveCandidate(o.Point, o.Global, null)).ToList(),
                    false, config, new Random(1), out _);

                Assert.AreEqual(portals == null, defense.RejectPointAmong(def, c, all, false) != null, c.ToString());
            }

            StringAssert.StartsWith(defense.RejectPointAmong(def, lonely, all, false), "defense: fewer than 1 portal point(s)");
            Assert.IsNull(defense.RejectPointAmong(def, near, all, false));
        }

        [TestMethod]
        public void A_defense_slot_at_a_point_without_portal_neighbours_goes_to_another_type()
        {
            var defense = new DefenseEncounterType();
            var types = new[] { Type("defense", kind: DefenseEncounterType.KindName, weight: 1000), Type("other") };

            // One point, nothing 8 to 25 m from it: defense cannot open a portal there, so the slot is never spent on it.
            var lonely = new List<ThreadEncounterCandidate> { Candidate(Point(DungeonCell(), 10f, 10f)) };

            for (var s = 0; s < 50; s++)
            {
                var req = Request(types, lonely, seed: s, max: 1);
                req.TypeFilter = (d, c) => d.Kind == DefenseEncounterType.KindName ? defense.RejectPointAmong(d, c, lonely, false) : null;
                var plan = ThreadEncounterPicker.Pick(req);

                Assert.AreEqual("other", plan.Picks.Single().Def.Id, $"seed {s}");
                Assert.AreEqual(0, plan.Dropped.Count);
            }

            // Control: give the point a neighbour 15 m away and the heavy defense row wins the slot.
            var paired = new List<ThreadEncounterCandidate> { lonely[0], Candidate(Point(DungeonCell(), 25f, 10f)) };
            var control = Request(types, paired, seed: 3, max: 1);
            control.TypeFilter = (d, c) => d.Kind == DefenseEncounterType.KindName ? defense.RejectPointAmong(d, c, paired, false) : null;
            Assert.AreEqual("defense", ThreadEncounterPicker.Pick(control).Picks.Single().Def.Id);
        }

        [TestMethod]
        public void The_count_summary_names_the_mechanism_and_the_drawn_count()
        {
            var req = Request(new[] { Type("a") }, Line(10), seed: 3, max: 5);
            req.CountWeights = new double[] { 0, 0, 1 };
            Assert.AreEqual("countWeights drew 2 of max 5", ThreadEncounterPicker.Pick(req).CountSummary(0.5));

            var roll = ThreadEncounterPicker.Pick(Request(new[] { Type("a") }, Line(10), seed: 3, max: 4, chance: 1.0));
            Assert.AreEqual("slot_chance 1 x 4 slot(s), 4 active", roll.CountSummary(1.0));
        }

        [TestMethod]
        public void Slot_count_over_many_seeds_matches_the_slot_chance()
        {
            const int seeds = 4000;
            const int max = 4;

            foreach (var chance in new[] { 0.25, 0.5, 0.8 })
            {
                var total = 0;

                for (var s = 0; s < seeds; s++)
                    total += ThreadEncounterPicker.Pick(Request(new[] { Type("a") }, Line(1), seed: s, max: max, chance: chance)).SlotsActive;

                var mean = (double)total / seeds;
                Assert.AreEqual(max * chance, mean, 0.08, $"mean active slots at chance {chance}");
            }
        }

        // ---- requirement filters --------------------------------------------------------------------------------

        [TestMethod]
        public void Clearance_depth_entry_and_boss_distance_each_reject_a_point()
        {
            var def = Type("a", placement: p => { p.MinClearance = 3f; p.MinDepth = 2; p.MaxDepth = 6; p.MinEntryWalk = 20f; p.MinBossDist = 15f; });
            var good = Candidate(Point(DungeonCell(), 0, 0, clearance: 3f, depth: 2), entry: 20f, boss: 15f);

            Assert.IsNull(ThreadEncounterPicker.RejectReason(def, good, false, null), "every bound is inclusive");

            Assert.AreEqual("clearance", ThreadEncounterPicker.RejectReason(def, Candidate(Point(DungeonCell(), 0, 0, clearance: 2.9f, depth: 3), 50, 50), false, null));
            Assert.AreEqual("too shallow", ThreadEncounterPicker.RejectReason(def, Candidate(Point(DungeonCell(), 0, 0, depth: 1), 50, 50), false, null));
            Assert.AreEqual("too deep", ThreadEncounterPicker.RejectReason(def, Candidate(Point(DungeonCell(), 0, 0, depth: 7), 50, 50), false, null));
            Assert.AreEqual("near entry", ThreadEncounterPicker.RejectReason(def, Candidate(Point(DungeonCell(), 0, 0, depth: 3), 19.9f, 50), false, null));
            Assert.AreEqual("near boss", ThreadEncounterPicker.RejectReason(def, Candidate(Point(DungeonCell(), 0, 0, depth: 3), 50, 14.9f), false, null));
            Assert.AreEqual("not curated", ThreadEncounterPicker.RejectReason(def, Candidate(Point(DungeonCell(), 0, 0, depth: 3, curated: false), 50, 50), false, null));
        }

        [TestMethod]
        public void Only_points_meeting_the_requirements_are_ever_picked()
        {
            var def = Type("a", placement: p => p.MinClearance = 3f);
            var tight = Candidate(Point(DungeonCell(), 0, 0, clearance: 1.5f));
            var open = Candidate(Point(DungeonCell(), 60, 0, clearance: 5f));

            for (var s = 0; s < 50; s++)
            {
                var plan = ThreadEncounterPicker.Pick(Request(new[] { def }, new[] { tight, open }, seed: s, max: 1));
                Assert.AreSame(open, plan.Picks.Single().Point);
            }
        }

        [TestMethod]
        public void A_type_filter_from_the_implementation_rejects_a_point()
        {
            var def = Type("a");
            var a = Candidate(Point(DungeonCell(), 0, 0));
            var b = Candidate(Point(DungeonCell(), 60, 0));

            for (var s = 0; s < 30; s++)
            {
                var req = Request(new[] { def }, new[] { a, b }, seed: s, max: 1);
                req.TypeFilter = (d, c) => ReferenceEquals(c, a) ? "type says no" : null;
                Assert.AreSame(b, ThreadEncounterPicker.Pick(req).Picks.Single().Point);
            }
        }

        [TestMethod]
        public void Disabled_zero_weight_unregistered_and_wrong_run_kind_types_are_never_drawn()
        {
            var candidates = Line(6);
            var types = new[]
            {
                Type("off", enabled: false),
                Type("zero", weight: 0),
                Type("unreg", kind: "ghost"),
                Type("outdoor_only", placement: p => p.RunKinds = new List<string> { "outdoor" }),
                Type("ok"),
            };

            for (var s = 0; s < 40; s++)
            {
                var req = Request(types, candidates, seed: s, max: 2);
                req.KindRegistered = kind => kind != "ghost";

                foreach (var pick in ThreadEncounterPicker.Pick(req).Picks)
                    Assert.AreEqual("ok", pick.Def.Id);
            }
        }

        [TestMethod]
        public void Weight_decides_how_often_a_type_wins()
        {
            var types = new[] { Type("light", weight: 1), Type("heavy", weight: 3) };
            var heavy = 0;
            const int n = 4000;

            for (var s = 0; s < n; s++)
            {
                if (ThreadEncounterPicker.Pick(Request(types, Line(3), seed: s, max: 1)).Picks.Single().Def.Id == "heavy")
                    heavy++;
            }

            Assert.AreEqual(0.75, (double)heavy / n, 0.03);
        }

        // ---- spacing --------------------------------------------------------------------------------------------

        [TestMethod]
        public void Spacing_holds_against_puzzle_sites_and_against_each_other()
        {
            var def = Type("a", placement: p => p.MinSpacing = 10f);
            var a = Candidate(Point(DungeonCell(), 0, 0));
            var b = Candidate(Point(DungeonCell(), 3, 0));
            var c = Candidate(Point(DungeonCell(), 50, 0));

            // C is next to a puzzle site; A and B are next to each other: only one of A/B can be used, and slot 2 drops.
            var req = Request(new[] { def }, new[] { a, b, c }, max: 2);
            req.PuzzleSites = new[] { new Vector3(52, 0, 0) };

            var plan = ThreadEncounterPicker.Pick(req);

            Assert.AreEqual(1, plan.Picks.Count);
            Assert.IsTrue(ReferenceEquals(plan.Picks[0].Point, a) || ReferenceEquals(plan.Picks[0].Point, b));
            StringAssert.Contains(plan.Dropped.Single(), "clear of the other encounters and puzzle sites");

            // Control: without the puzzle site, both slots fill, 10 m or more apart.
            var free = ThreadEncounterPicker.Pick(Request(new[] { def }, new[] { a, b, c }, max: 2));
            Assert.AreEqual(2, free.Picks.Count);
            Assert.IsTrue(Vector3.Distance(free.Picks[0].Point.Global, free.Picks[1].Point.Global) >= 10f);
        }

        [TestMethod]
        public void Spacing_between_two_encounters_is_the_larger_of_their_two_rules()
        {
            var spaced = Type("spaced", placement: p => p.MinSpacing = 30f);
            var loose = Type("loose");
            var a = Candidate(Point(DungeonCell(), 0, 0));
            var b = Candidate(Point(DungeonCell(), 5, 0));

            var bothSeen = false;

            for (var s = 0; s < 200; s++)
            {
                var plan = ThreadEncounterPicker.Pick(Request(new[] { spaced, loose }, new[] { a, b }, seed: s, max: 2));

                if (plan.Picks.Count == 2)
                {
                    // Two picks 5 m apart are allowed only when neither of them is the 30 m type.
                    Assert.IsFalse(plan.Picks.Any(p => p.Def.Id == "spaced"), $"seed {s}: a spacing-0 type landed inside a 30 m type's radius");
                }

                if (plan.Picks.Any(p => p.Def.Id == "spaced"))
                    bothSeen = true;
            }

            Assert.IsTrue(bothSeen, "the spaced type must win some slots, or this proves nothing");

            // The rule directly, in both orders.
            var spacedPick = new ThreadEncounterPick(0, spaced, a, 1);
            var loosePick = new ThreadEncounterPick(0, loose, a, 1);
            Assert.IsFalse(ThreadEncounterPicker.Spaced(b, 0f, new[] { spacedPick }, null, false), "loose candidate vs a spaced pick");
            Assert.IsFalse(ThreadEncounterPicker.Spaced(b, 30f, new[] { loosePick }, null, false), "spaced candidate vs a loose pick");
            Assert.IsTrue(ThreadEncounterPicker.Spaced(Candidate(Point(DungeonCell(), 40, 0)), 0f, new[] { spacedPick }, null, false), "control: 40 m clears 30 m");
        }

        [TestMethod]
        public void The_tunable_clamps_hold_their_ranges_and_NaN_reads_as_the_default()
        {
            Assert.AreEqual(0, ThreadDungeonRun.ClampEncountersMaxPerRun(-5));
            Assert.AreEqual(2, ThreadDungeonRun.ClampEncountersMaxPerRun(2));
            Assert.AreEqual(ThreadEncounterPicker.MaxPerRunCeiling, ThreadDungeonRun.ClampEncountersMaxPerRun(long.MaxValue));

            Assert.AreEqual(0.0, ThreadDungeonRun.ClampEncounterSlotChance(-1));
            Assert.AreEqual(0.25, ThreadDungeonRun.ClampEncounterSlotChance(0.25));
            Assert.AreEqual(1.0, ThreadDungeonRun.ClampEncounterSlotChance(7));
            Assert.AreEqual(1.0, ThreadDungeonRun.ClampEncounterSlotChance(double.PositiveInfinity));
            Assert.AreEqual(ThreadDungeonRun.DefaultEncounterSlotChance, ThreadDungeonRun.ClampEncounterSlotChance(double.NaN));
        }

        [TestMethod]
        public void Two_encounters_never_share_a_point_even_with_no_spacing_rule()
        {
            var only = Candidate(Point(DungeonCell(), 0, 0));
            var plan = ThreadEncounterPicker.Pick(Request(new[] { Type("a") }, new[] { only }, max: 3));

            Assert.AreEqual(1, plan.Picks.Count);
            Assert.AreEqual(2, plan.Dropped.Count);
        }

        [TestMethod]
        public void A_slot_with_no_fit_is_dropped_with_its_reason()
        {
            var def = Type("a", placement: p => p.MinClearance = 10f);
            var plan = ThreadEncounterPicker.Pick(Request(new[] { def }, Line(4), max: 2));

            Assert.AreEqual(0, plan.Picks.Count);
            Assert.AreEqual(2, plan.SlotsActive);
            Assert.AreEqual(2, plan.Dropped.Count);
            StringAssert.Contains(plan.Dropped[0], "slot 0");
            StringAssert.Contains(plan.Dropped[0], "clearance x4");
        }

        // ---- outdoor --------------------------------------------------------------------------------------------

        [TestMethod]
        public void Outdoor_runs_use_open_ground_points_only_and_ignore_depth_and_curated_clearance()
        {
            var def = Type("a", placement: p => { p.MinDepth = 50; p.MinClearance = 3f; });
            var open = Candidate(Point(OutdoorCell(0x5456, 50, 50), 50, 50, clearance: 0f, depth: 0, kind: "outdoor"), outdoor: true);
            var interior = Candidate(Point(OutdoorCell(0x5456, 60, 60), 60, 60, clearance: 9f, depth: 99, kind: "interior"), outdoor: true);

            Assert.IsNull(ThreadEncounterPicker.RejectReason(def, open, true, null), "depth and curated clearance do not apply outdoors");
            Assert.AreEqual("not an outdoor point", ThreadEncounterPicker.RejectReason(def, interior, true, null));

            // Control: the same open point is rejected by its curated clearance (0 m) in a dungeon run.
            Assert.AreEqual("clearance", ThreadEncounterPicker.RejectReason(def, open, false, null));
        }

        [TestMethod]
        public void Outdoor_distance_is_straight_line_in_global_metres_across_landblocks()
        {
            // 0x5456 x=190 and 0x5556 x=8 are 10 m apart globally (192 m landblocks), whatever the local x says.
            var a = ThreadEncounterGeometry.Global(OutdoorCell(0x5456, 190, 40), 190, 40, 0, true);
            var b = ThreadEncounterGeometry.Global(OutdoorCell(0x5556, 8, 40), 8, 40, 30, true);

            Assert.AreEqual(10f, ThreadEncounterGeometry.Distance(a, b, true), 1e-3f, "horizontal only");
            Assert.AreEqual(Vector3.Distance(new Vector3(190, 40, 0), new Vector3(8, 40, 30)), ThreadEncounterGeometry.Distance(new Vector3(190, 40, 0), new Vector3(8, 40, 30), false), 1e-3f,
                "a dungeon compares local 3D");

            var def = Type("a", placement: p => p.MinSpacing = 12f);
            var ca = new ThreadEncounterCandidate(Point(OutdoorCell(0x5456, 190, 40), 190, 40, kind: "outdoor"), a, 100, 100);
            var cb = new ThreadEncounterCandidate(Point(OutdoorCell(0x5556, 8, 40), 8, 40, kind: "outdoor"), b, 100, 100);

            var plan = ThreadEncounterPicker.Pick(Request(new[] { def }, new[] { ca, cb }, max: 2, outdoor: true));
            Assert.AreEqual(1, plan.Picks.Count, "10 m apart across the seam is inside a 12 m spacing");
        }

        [TestMethod]
        public void The_ground_probe_seam_rejects_a_point_and_is_asked_at_the_type_clearance()
        {
            var def = Type("a", placement: p => p.MinClearance = 4f);
            var bad = Candidate(Point(OutdoorCell(0x5456, 50, 50), 50, 50, kind: "outdoor"), outdoor: true);
            var good = Candidate(Point(OutdoorCell(0x5456, 120, 50), 120, 50, kind: "outdoor"), outdoor: true);
            var radii = new HashSet<float>();

            for (var s = 0; s < 40; s++)
            {
                var req = Request(new[] { def }, new[] { bad, good }, seed: s, max: 1, outdoor: true);
                req.GroundProbe = (c, r) => { radii.Add(r); return !ReferenceEquals(c, bad); };
                Assert.AreSame(good, ThreadEncounterPicker.Pick(req).Picks.Single().Point);
            }

            CollectionAssert.AreEqual(new[] { 4f }, radii.ToList());

            // Control: with an accept-all probe the rejected point is reachable for some seed.
            Assert.IsTrue(Enumerable.Range(0, 40).Any(s =>
            {
                var req = Request(new[] { def }, new[] { bad, good }, seed: s, max: 1, outdoor: true);
                req.GroundProbe = (c, r) => true;
                return ReferenceEquals(ThreadEncounterPicker.Pick(req).Picks.Single().Point, bad);
            }));
        }

        [TestMethod]
        public void A_dungeon_run_never_calls_the_ground_probe()
        {
            var called = false;
            var req = Request(new[] { Type("a") }, Line(3), max: 2);
            req.GroundProbe = (c, r) => { called = true; return false; };

            Assert.AreEqual(2, ThreadEncounterPicker.Pick(req).Picks.Count);
            Assert.IsFalse(called);
        }
    }
}
