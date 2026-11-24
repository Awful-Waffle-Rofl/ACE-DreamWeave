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
    /// Picker / placement agreement and the dispatch fallback (prod 2026-11-21: 46 picked encounters were refused at Place
    /// and their slots never refilled). The picker's run-aware prefilter applies Place's portal walk-ratio rule, and a slot
    /// whose Place still refuses moves to its planned alternates. Seam-driven, no world, no PropertyManager read.
    /// </summary>
    [TestClass]
    public class ThreadEncounterFallbackTests
    {
        // Two cells of the 0x0150 test dungeon: the pillar's and its only neighbour's.
        private static readonly uint CellA = DungeonCell(0x0101);
        private static readonly uint CellB = DungeonCell(0x0102);

        /// <summary>A walk graph joining A and B by one doorway at (<paramref name="x"/>, <paramref name="y"/>).</summary>
        private static DungeonWalkGraph Doorway(float x, float y)
            => new DungeonWalkGraph(new[] { CellA, CellB }, new[]
            {
                new DungeonWalkGraph.Portal(CellA, CellB, new Vector3(x, y, 0)),
                new DungeonWalkGraph.Portal(CellB, CellA, new Vector3(x, y, 0)),
            });

        // ---- picker / placement agreement ------------------------------------------------------------------------

        [TestMethod]
        public void The_picker_rejects_a_defense_point_whose_only_portal_point_fails_Place_walk_ratio()
        {
            // The pillar at (60, 10) in A, its neighbour 15 m east in B. Straight line: in reach (8 to 25 m), so the old
            // straight-line prefilter accepted it. Through a doorway at (60, 40) the walk is 30 + 33.5 = 63.5 m, over
            // Place's 1.5 x 15 = 22.5 m: Place refuses ("0 portal point(s) ... 1 needed"), so the picker must too.
            var defense = new DefenseEncounterType();
            var other = new FakeType("other");
            var store = Store(Type("defense", kind: DefenseEncounterType.KindName, weight: 1000), Type("other", kind: "other"));
            using var seams = new Seams(store, defense, other);

            var points = new[] { Point(CellA, 60f, 10f), Point(CellB, 75f, 10f) };

            ThreadEncounterPass.WalkGraphSource = _ => Doorway(60f, 40f);

            var run = DungeonRun(points, max: 1);
            var candidates = ThreadEncounterPass.BuildCandidates(run);
            var pillar = candidates.Single(c => c.Point.Cell == CellA);
            var def = store.Types.First(t => t.Kind == DefenseEncounterType.KindName);

            Assert.IsNull(defense.RejectPointAmong(def, pillar, candidates, false), "control: the straight-line count alone accepts the point");
            Assert.AreEqual("defense: 0 portal point(s) between 8 and 25 m on the pillar's landblock; 1 needed", defense.RejectPointInRun(def, pillar, candidates, run));

            for (var s = 0; s < 30; s++)
            {
                var plan = ThreadEncounterPass.Plan(DungeonRun(points, seed: s, max: 1), store);
                Assert.IsFalse(plan.Picks.Any(p => p.Def.Kind == DefenseEncounterType.KindName), $"seed {s}: no defense where Place must refuse");
            }

            // Control: the doorway on the straight line (walk 15 m) qualifies, and the heavy defense row is picked.
            ThreadEncounterPass.WalkGraphSource = _ => Doorway(67f, 10f);
            Assert.IsNull(defense.RejectPointInRun(def, pillar, candidates, run));
            Assert.IsTrue(Enumerable.Range(0, 30).Any(s => ThreadEncounterPass.Plan(DungeonRun(points, seed: s, max: 1), store).Picks.Any(p => p.Def.Kind == DefenseEncounterType.KindName)));
        }

        [TestMethod]
        public void The_gamble_prefilter_also_refuses_a_portal_point_with_no_known_walk()
        {
            // Gamble's Place requires a walked distance (requireWalkDistance) at its tighter 1.25 ratio: with a graph that
            // does not know the neighbour's cell, Place refuses, so the prefilter must too. Control: a graph that knows it.
            var gamble = new GambleEncounterType();
            var store = Store(Type("gamble", kind: GambleEncounterType.KindName));
            using var seams = new Seams(store, gamble);

            var cache = Point(CellA, 60f, 10f);
            var points = new[] { cache, Point(CellB, 75f, 10f), Point(CellB, 60f, 25f) };
            var run = DungeonRun(points);
            var def = store.Types.Single();

            ThreadEncounterPass.WalkGraphSource = _ => new DungeonWalkGraph(new[] { CellA }, Array.Empty<DungeonWalkGraph.Portal>());
            var candidates = ThreadEncounterPass.BuildCandidates(run);
            var at = candidates.Single(c => ReferenceEquals(c.Point, cache));

            Assert.IsNull(gamble.RejectPointAmong(def, at, candidates, false), "control: the straight-line count accepts");
            StringAssert.StartsWith(gamble.RejectPointInRun(def, at, candidates, run), "gamble: 0 portal point(s) between");

            // Two doorways on the straight lines: each walk is 15 m, inside 1.25 x 15.
            ThreadEncounterPass.WalkGraphSource = _ => new DungeonWalkGraph(new[] { CellA, CellB }, new[]
            {
                new DungeonWalkGraph.Portal(CellA, CellB, new Vector3(70f, 10f, 0)),
                new DungeonWalkGraph.Portal(CellA, CellB, new Vector3(60f, 20f, 0)),
            });
            Assert.IsNull(gamble.RejectPointInRun(def, at, candidates, run));
        }

        [TestMethod]
        public void A_type_without_its_own_run_filter_is_judged_by_RejectPointAmong()
        {
            var fake = new FakeType { Reject = (d, c) => c.Point.X > 50 ? "far" : null };
            IThreadEncounterType type = fake;
            var run = DungeonRun(new[] { Point(DungeonCell(), 0, 0), Point(DungeonCell(), 80, 0) });
            var candidates = ThreadEncounterPass.BuildCandidates(run);

            Assert.IsNull(type.RejectPointInRun(Type("a"), candidates[0], candidates, run));
            Assert.AreEqual("far", type.RejectPointInRun(Type("a"), candidates[1], candidates, run));
        }

        // ---- fallback plan ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void Every_pick_gets_alternates_clear_of_the_other_slots_and_the_plan_is_deterministic()
        {
            var types = new[] { Type("a", placement: p => p.MinSpacing = 50f) };
            var candidates = Enumerable.Range(0, 8).Select(i => Candidate(Point(DungeonCell(), i * 30f, 0))).ToList();

            for (var seed = 0; seed < 40; seed++)
            {
                var plan = ThreadEncounterPicker.Pick(Request(types, candidates, seed: seed, max: 2));
                var again = ThreadEncounterPicker.Pick(Request(types, candidates, seed: seed, max: 2));

                CollectionAssert.AreEqual(plan.Picks.ToList(), again.Picks.ToList(), $"seed {seed}");
                Assert.AreEqual(plan.Fallbacks.Count, again.Fallbacks.Count);

                foreach (var (slot, list) in plan.Fallbacks)
                    CollectionAssert.AreEqual(list.ToList(), again.Fallbacks[slot].ToList(), $"seed {seed} slot {slot}: same alternates");

                foreach (var pick in plan.Picks)
                {
                    var mine = plan.Fallbacks.TryGetValue(pick.Slot, out var l) ? l : Array.Empty<ThreadEncounterPick>();
                    Assert.IsTrue(mine.Count <= ThreadEncounterPicker.MaxSameRowFallbacks + ThreadEncounterPicker.MaxOtherKindFallbacks);
                    Assert.IsTrue(mine.All(a => a.Slot == pick.Slot && a.Seed > 0));
                    Assert.IsFalse(mine.Any(a => ReferenceEquals(a.Point, pick.Point)), "never the failed point again");

                    // Any combination of this slot's options with the other slot's options stays 50 m apart.
                    foreach (var other in plan.Picks.Where(p => p.Slot != pick.Slot))
                    {
                        var theirs = new[] { other }.Concat(plan.Fallbacks.TryGetValue(other.Slot, out var t) ? t : Array.Empty<ThreadEncounterPick>());

                        foreach (var a in new[] { pick }.Concat(mine))
                            foreach (var b in theirs)
                                Assert.IsTrue(Vector3.Distance(a.Point.Global, b.Point.Global) >= 50f, $"seed {seed}: {a.Point} vs {b.Point}");
                    }
                }
            }
        }

        [TestMethod]
        public void A_kind_at_its_cap_in_another_slot_is_never_offered_as_an_alternate()
        {
            var capped = Type("capped", kind: "capped", weight: 1000);
            capped.MaxPerRun = 1;
            var types = new[] { capped, Type("a"), Type("b", kind: "b") };
            var candidates = Enumerable.Range(0, 8).Select(i => Candidate(Point(DungeonCell(), i * 30f, 0))).ToList();

            var checkedOne = false;

            for (var seed = 0; seed < 60; seed++)
            {
                var plan = ThreadEncounterPicker.Pick(Request(types, candidates, seed: seed, max: 2));

                if (!plan.Picks.Any(p => p.Def.Kind == "capped"))
                    continue;

                checkedOne = true;

                foreach (var pick in plan.Picks.Where(p => p.Def.Kind != "capped"))
                {
                    var mine = plan.Fallbacks.TryGetValue(pick.Slot, out var l) ? l : Array.Empty<ThreadEncounterPick>();
                    Assert.IsFalse(mine.Any(a => a.Def.Kind == "capped"), $"seed {seed}: the capped kind is taken by the other slot");
                }

                foreach (var pick in plan.Picks.Where(p => p.Def.Kind == "capped"))
                {
                    var mine = plan.Fallbacks.TryGetValue(pick.Slot, out var l) ? l : Array.Empty<ThreadEncounterPick>();
                    Assert.IsTrue(mine.Any(a => a.Def.Kind != "capped"), $"seed {seed}: control, other kinds are offered");
                }
            }

            Assert.IsTrue(checkedOne, "some seed picked the capped kind");
        }

        [TestMethod]
        public void A_throw_while_planning_alternates_leaves_the_primary_picks_intact()
        {
            // The TypeFilter verdicts are cached per (row, point), so the fallback planner never re-asks them; the per-run
            // filter (TypeUnavailable -> RunFilter) it does re-ask. With one slot, the main loop asks each row once, so a
            // filter that throws on its SECOND call per row throws only inside the fallback planner.
            var types = new[] { Type("a", weight: 1000), Type("b", kind: "b", weight: 0.001) };
            var candidates = Enumerable.Range(0, 4).Select(i => Candidate(Point(DungeonCell(), i * 40f, 0))).ToList();

            var control = ThreadEncounterPicker.Pick(Request(types, candidates, seed: 3, max: 1));
            Assert.AreEqual(1, control.Picks.Count);
            Assert.IsTrue(control.Fallbacks.Count > 0, "control: alternates are planned when nothing throws");

            var calls = new Dictionary<string, int>(StringComparer.Ordinal);
            var request = Request(types, candidates, seed: 3, max: 1);
            request.RunFilter = def =>
            {
                calls[def.Id] = calls.TryGetValue(def.Id, out var n) ? n + 1 : 1;

                if (calls[def.Id] == 2)
                    throw new InvalidOperationException("second call");

                return null;
            };

            var plan = ThreadEncounterPicker.Pick(request);

            Assert.IsTrue(calls.Values.Any(n => n >= 2), "the filter was asked a second time (the throw happened)");
            CollectionAssert.AreEqual(control.Picks.ToList(), plan.Picks.ToList(), "the primary picks are exactly the control's");
            Assert.AreEqual(0, plan.Fallbacks.Count, "no alternates after the planner threw");
            Assert.AreEqual(control.Dropped.Count, plan.Dropped.Count);
        }

        // ---- dispatch fallback ---------------------------------------------------------------------------------------

        [TestMethod]
        public void A_throwing_place_moves_the_slot_to_an_alternate_where_it_places()
        {
            var calls = 0;
            var fake = new FakeType
            {
                OnPlace = r =>
                {
                    if (++calls == 1)
                        throw new InvalidOperationException("boom");

                    return (true, null);
                },
            };

            using var seams = new Seams(Store(Type("a")), fake);
            var run = FourPointRun();

            ThreadEncounterPass.OnRunPopulated(run);
            var plan = run.EncounterPlan;

            seams.Drain();
            Assert.AreEqual(0, run.PlacedEncounters.Count, "the first attempt threw");
            Assert.IsTrue(fake.Placed[0].Instance.Objects.Count == 0, "and was released");
            Assert.AreEqual(1, seams.Queued.Count, "its first alternate is queued");

            seams.Drain();

            Assert.AreEqual(2, fake.Placed.Count);
            Assert.AreEqual(1, run.PlacedEncounters.Count);
            Assert.AreSame(plan.Fallbacks[plan.Picks[0].Slot][0].Point, run.PlacedEncounters[0].Pick.Point);
            Assert.IsTrue(run.EncounterOutcomes.Any(l => l.Contains("not placed: threw InvalidOperationException; trying alternate 1 of")), string.Join("\n", run.EncounterOutcomes));
        }

        [TestMethod]
        public void A_failed_pick_falls_back_to_another_kind_placed_by_that_kinds_own_type()
        {
            // The pick's own row refuses everywhere; after its two same-row alternates, the other-kind alternate is tried
            // through ITS implementation and places.
            var refusing = new FakeType { OnPlace = r => (false, "no room") };
            var other = new FakeType("other");

            using var seams = new Seams(Store(Type("a", weight: 1000), Type("o", kind: "other", weight: 0.001)), refusing, other);
            var run = FourPointRun();

            ThreadEncounterPass.OnRunPopulated(run);

            var plan = run.EncounterPlan;
            Assert.AreEqual("a", plan.Picks.Single().Def.Id, "the heavy row is the pick");

            var alternates = plan.Fallbacks[plan.Picks[0].Slot];
            Assert.AreEqual(ThreadEncounterPicker.MaxSameRowFallbacks + 1, alternates.Count, "two same-row points, then the one other kind");
            Assert.AreEqual("o", alternates.Last().Def.Id);

            DrainAll(seams);

            Assert.AreEqual(1 + ThreadEncounterPicker.MaxSameRowFallbacks, refusing.Placed.Count, "the pick and both same-row alternates refused");
            Assert.AreEqual(1, other.Placed.Count, "the other kind's own type placed it");
            Assert.AreEqual(1, run.PlacedEncounters.Count);

            var placed = run.PlacedEncounters[0];
            Assert.AreEqual("other", placed.Kind);
            Assert.AreSame(other, placed.Type);
            Assert.AreEqual(plan.Picks[0].Slot, placed.Pick.Slot, "still the same slot");
        }

        private static ThreadDungeonRun FourPointRun(int max = 1)
            => DungeonRun(new[] { Point(DungeonCell(), 0, 0), Point(DungeonCell(), 40, 0), Point(DungeonCell(), 80, 0), Point(DungeonCell(), 120, 0) }, max: max);

        /// <summary>Drains until nothing new is queued (a fallback queues its next attempt from inside the drained action).</summary>
        private static int DrainAll(Seams seams)
        {
            var rounds = 0;

            while (seams.Queued.Count > 0 && rounds < 20)
            {
                seams.Drain();
                rounds++;
            }

            return rounds;
        }

        [TestMethod]
        public void A_refused_place_moves_the_slot_to_a_second_point_where_it_places()
        {
            DungeonSpawnPointDef refusedAt = null;

            var fake = new FakeType
            {
                OnPlace = r =>
                {
                    if (refusedAt == null)
                    {
                        refusedAt = r.Point.Point;
                        return (false, "no room");
                    }

                    return (true, null);
                },
            };

            using var seams = new Seams(Store(Type("a")), fake);
            var run = FourPointRun();

            ThreadEncounterPass.OnRunPopulated(run);

            var plan = run.EncounterPlan;
            Assert.AreEqual(1, plan.Picks.Count);
            Assert.IsTrue(plan.Fallbacks[plan.Picks[0].Slot].Count > 0, "the pick has alternates");

            seams.Drain();
            Assert.AreEqual(1, fake.Placed.Count);
            Assert.AreEqual(0, run.PlacedEncounters.Count, "the first attempt refused");
            Assert.AreEqual(1, seams.Queued.Count, "and queued its first alternate");

            seams.Drain();

            Assert.AreEqual(2, fake.Placed.Count);
            Assert.AreEqual(1, run.PlacedEncounters.Count, "placed at the alternate");
            Assert.AreNotSame(refusedAt, run.PlacedEncounters[0].Pick.Point.Point, "at a different point");
            Assert.AreSame(plan.Fallbacks[plan.Picks[0].Slot][0].Point, run.PlacedEncounters[0].Pick.Point, "the first planned alternate");
            Assert.IsTrue(run.EncounterOutcomes.Any(l => l.Contains("not placed: no room; trying alternate 1 of")), string.Join("\n", run.EncounterOutcomes));
            Assert.IsFalse(run.EncounterOutcomes.Any(l => l.Contains("no alternate left")));
        }

        [TestMethod]
        public void When_every_attempt_refuses_the_drop_is_still_recorded_and_nothing_is_placed()
        {
            var fake = new FakeType { OnPlace = r => (false, "no room") };

            using var seams = new Seams(Store(Type("a")), fake);
            var run = FourPointRun();
            var placedBefore = run.PlacedEncounters.Count;

            ThreadEncounterPass.OnRunPopulated(run);

            var alternates = run.EncounterPlan.Fallbacks[run.EncounterPlan.Picks[0].Slot];
            Assert.AreEqual(ThreadEncounterPicker.MaxSameRowFallbacks, alternates.Count, "three other points, two same-row alternates, no other kind");

            DrainAll(seams);

            Assert.AreEqual(1 + alternates.Count, fake.Placed.Count, "the pick and every alternate were tried, once each");
            Assert.AreEqual(placedBefore, run.PlacedEncounters.Count);
            Assert.AreEqual(1 + alternates.Count, fake.Placed.Select(r => r.Point.Point).Distinct().Count(), "each at its own point");
            Assert.IsTrue(fake.Placed.All(r => r.Instance.Objects.Count == 0), "every failed attempt released");
            Assert.IsTrue(run.EncounterOutcomes.Any(l => l.EndsWith($"not placed: no room; no alternate left ({alternates.Count} planned)", StringComparison.Ordinal)), string.Join("\n", run.EncounterOutcomes));

            ThreadEncounterPass.OnRunEnded(run, "test");
            Assert.AreEqual(0, fake.Ended.Count, "a failed place is owed no OnRunEnded");
        }

        [TestMethod]
        public void A_slot_with_no_alternate_keeps_the_old_drop_line_exactly()
        {
            // Spacing 1000 m: one pick, no other point is clear of it, so no alternate. The outcome is the pre-fallback line.
            var fake = new FakeType { OnPlace = r => (false, "no room") };
            using var seams = new Seams(Store(Type("a", placement: p => p.MinSpacing = 1000f)), fake);
            var run = DungeonRun(new[] { Point(DungeonCell(), 0, 0) }, max: 1);

            ThreadEncounterPass.OnRunPopulated(run);
            DrainAll(seams);

            Assert.AreEqual(1, fake.Placed.Count);
            Assert.IsTrue(run.EncounterOutcomes.Any(l => l.EndsWith(": not placed: no room", StringComparison.Ordinal)), string.Join("\n", run.EncounterOutcomes));
        }

        [TestMethod]
        public void A_pick_that_cannot_be_queued_falls_back_too_and_drops_with_the_old_line_when_nothing_queues()
        {
            var fake = new FakeType();

            using var seams = new Seams(Store(Type("a")), fake);
            seams.UseProductionEnqueue(); // the run copy holds no landblock: neither the pick nor any alternate can queue
            var run = FourPointRun();

            ThreadEncounterPass.OnRunPopulated(run);

            Assert.AreEqual(0, fake.Placed.Count);
            Assert.IsTrue(run.EncounterOutcomes.Any(l => l.Contains("; skipped")), "each alternate was tried and skipped");
            Assert.IsTrue(run.EncounterOutcomes.Any(l => l.EndsWith("landblock 0x0150 is not loaded in the run copy", StringComparison.Ordinal)), string.Join("\n", run.EncounterOutcomes));
        }

    }
}
