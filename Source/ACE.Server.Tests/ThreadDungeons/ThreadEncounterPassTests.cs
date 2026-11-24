using System;
using System.Linq;

using ACE.Server.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using static ACE.Server.Tests.ThreadDungeons.ThreadEncounterTestFixtures;
using static ACE.Server.Tests.ThreadDungeons.ThreadLootTestFixtures;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The encounter pass (ThreadEncounterPass), seam-driven: one action per pick on the pick's own landblock, every
    /// failure leaving the run unchanged, OnRunEnded once per placed encounter, the once-per-run latch, and the three
    /// production wiring points (source scans). No world: the enqueue seam records actions and Drain runs them.
    /// </summary>
    [TestClass]
    public class ThreadEncounterPassTests
    {
        private static ThreadDungeonRun FourPointRun(int max = 2)
            => DungeonRun(new[]
            {
                Point(DungeonCell(), 0, 0), Point(DungeonCell(), 40, 0), Point(DungeonCell(), 80, 0), Point(DungeonCell(), 120, 0),
            }, max: max);

        private static (int Planned, int Spawned, ThreadDungeonRunState State, int Placed) Snapshot(ThreadDungeonRun run)
            => (run.Planned, run.Spawned, run.State, run.PlacedEncounters.Count);

        // ---- dispatch -------------------------------------------------------------------------------------------

        [TestMethod]
        public void One_action_per_encounter_on_the_landblock_of_its_point_and_each_places_once()
        {
            var fake = new FakeType();
            using var seams = new Seams(Store(Type("a")), fake);
            var run = FourPointRun(max: 3);

            ThreadEncounterPass.OnRunPopulated(run);

            var plan = run.EncounterPlan;
            Assert.AreEqual(3, plan.Picks.Count);
            Assert.AreEqual(3, seams.Queued.Count, "one action per pick");
            Assert.IsTrue(seams.Queued.All(q => q.Landblock == DungeonLb));
            Assert.AreEqual(0, fake.Placed.Count, "nothing placed until the landblock runs its queue");

            seams.Drain();

            Assert.AreEqual(3, fake.Placed.Count);
            Assert.AreEqual(3, run.PlacedEncounters.Count);
            CollectionAssert.AreEqual(plan.Picks.Select(p => p.Point).ToList(), fake.Placed.Select(r => r.Point).ToList());
            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, run.PlacedEncounters.Select(i => i.Id).ToList());
        }

        [TestMethod]
        public void The_pass_runs_once_per_run_however_often_OnRunPopulated_is_reached()
        {
            var fake = new FakeType();
            using var seams = new Seams(Store(Type("a")), fake);
            var run = FourPointRun();

            ThreadEncounterPass.OnRunPopulated(run);
            ThreadEncounterPass.OnRunPopulated(run); // a reward unseal reaches the manager's OnRunPopulated again

            Assert.AreEqual(2, seams.Queued.Count);
        }

        [TestMethod]
        public void An_early_call_before_the_context_exists_does_not_burn_the_latch()
        {
            var fake = new FakeType();
            using var seams = new Seams(Store(Type("a")), fake);
            var run = DungeonRun(new[] { Point(DungeonCell(), 0, 0), Point(DungeonCell(), 60, 0) }, context: false);

            // A reward unseal reaching OnRunPopulated before the spawner stamped the context.
            ThreadEncounterPass.OnRunPopulated(run);
            Assert.AreEqual(0, seams.Queued.Count);
            StringAssert.Contains(run.EncounterPlan.Skipped, "no populate context");

            run.MarkEncounterContext(new ThreadEncounterRunContext(null, 0u, null, null, null));
            ThreadEncounterPass.OnRunPopulated(run);

            Assert.AreEqual(2, seams.Queued.Count, "the real call still plans and queues");
            Assert.IsNull(run.EncounterPlan.Skipped, "the real plan replaced the provisional one");

            ThreadEncounterPass.OnRunPopulated(run);
            Assert.AreEqual(2, seams.Queued.Count, "and the latch holds after that");
        }

        [TestMethod]
        public void An_early_call_while_the_run_is_still_Starting_does_not_burn_the_latch()
        {
            var fake = new FakeType();
            using var seams = new Seams(Store(Type("a")), fake);

            var spec = new DungeonGemSpec("enc_test", 200, 6, "any", 5, new (string, double)[0], 0, 0);
            var dungeon = new ACE.Server.ThreadDungeons.Defs.DungeonEntryDef
            {
                Id = "enc_test", Landblock = DungeonLb, ExitPortalWcid = 1003601, Entry = "0x0150018A 0 0 0 1 0 0 0",
                Points = new System.Collections.Generic.List<ACE.Server.ThreadDungeons.Defs.DungeonSpawnPointDef> { Point(DungeonCell(), 0, 0) },
            };
            var run = new ThreadDungeonRun(0x80005679u, 0x50000001u, "Tester", 7, 0x80000099u, spec, dungeon, DateTime.UtcNow, TimeSpan.FromMinutes(180))
            {
                EncountersEnabled = true, EncountersMaxPerRun = 1, EncounterSlotChance = 1.0,
            };
            run.MarkEncounterContext(new ThreadEncounterRunContext(null, 0u, null, null, null));

            ThreadEncounterPass.OnRunPopulated(run); // Starting
            Assert.AreEqual(0, seams.Queued.Count);
            StringAssert.Contains(run.EncounterPlan.Skipped, "Starting");

            run.MarkPopulated(planned: 1, spawned: 1, bossWcid: 0);
            ThreadEncounterPass.OnRunPopulated(run);
            Assert.AreEqual(1, seams.Queued.Count);
        }

        [TestMethod]
        public void Nothing_is_rolled_when_switched_off_not_active_disabled_or_typeless()
        {
            var fake = new FakeType();

            void AssertNothing(ThreadDungeonRun run, ACE.Server.ThreadDungeons.ThreadEncounterStore store, string fragment)
            {
                using var seams = new Seams(store, fake);
                ThreadEncounterPass.OnRunPopulated(run);
                Assert.AreEqual(0, seams.Queued.Count);
                StringAssert.Contains(run.EncounterPlan.Skipped, fragment);
            }

            var off = FourPointRun();
            off.EncountersEnabled = false;
            AssertNothing(off, Store(Type("a")), "switched off");

            var ended = FourPointRun();
            ended.MarkEnded("test");
            AssertNothing(ended, Store(Type("a")), "Ended");

            AssertNothing(DungeonRun(new[] { Point(DungeonCell(), 0, 0) }, context: false), Store(Type("a")), "no populate context");

            AssertNothing(FourPointRun(), ThreadEncounterStore.Parse("{\"version\":1,\"types\":[{\"id\":\"a\",\"kind\":\"fake\"}],\"dungeons\":{\"enc_test\":{\"disabled\":true,\"reason\":\"why not\"}}}",
                null, new System.Collections.Generic.List<string>()), "why not");

            AssertNothing(FourPointRun(), ThreadEncounterStore.Empty, "no encounter types");

            // Control: the same run shape with a type and the switch on does roll.
            using (var seams = new Seams(Store(Type("a")), fake))
            {
                ThreadEncounterPass.OnRunPopulated(FourPointRun());
                Assert.AreEqual(2, seams.Queued.Count);
            }
        }

        // ---- failure leaves the run unchanged -------------------------------------------------------------------

        [TestMethod]
        public void A_refused_or_throwing_place_leaves_the_run_unchanged()
        {
            foreach (var mode in new[] { "refuse", "throw" })
            {
                var fake = new FakeType
                {
                    OnPlace = r =>
                    {
                        r.Instance.Track(Item()); // something tracked before the failure is released again
                        if (mode == "throw") throw new InvalidOperationException("boom");
                        return (false, "no room");
                    },
                };

                using var seams = new Seams(Store(Type("a")), fake);
                var run = FourPointRun();
                var before = Snapshot(run);

                ThreadEncounterPass.OnRunPopulated(run);
                seams.Drain();

                Assert.AreEqual(2, fake.Placed.Count, mode);
                Assert.AreEqual(before, Snapshot(run), mode);
                Assert.IsTrue(fake.Placed.All(r => r.Instance.Objects.Count == 0), "tracked objects released: " + mode);

                ThreadEncounterPass.OnRunEnded(run, "test");
                Assert.AreEqual(0, fake.Ended.Count, "a failed place is owed no OnRunEnded: " + mode);
            }
        }

        [TestMethod]
        public void An_unloaded_landblock_or_unregistered_kind_drops_the_pick_and_places_nothing()
        {
            var fake = new FakeType();

            using (var seams = new Seams(Store(Type("a")), fake))
            {
                seams.UseProductionEnqueue(); // the run's populate context holds no landblock at all
                var run = FourPointRun();
                var before = Snapshot(run);

                ThreadEncounterPass.OnRunPopulated(run);

                Assert.AreEqual(before, Snapshot(run));
                Assert.AreEqual(0, fake.Placed.Count);
                Assert.IsTrue(run.EncounterOutcomes.Any(l => l.Contains("landblock 0x0150 is not loaded")), string.Join("\n", run.EncounterOutcomes));
            }

            using (var seams = new Seams(Store(Type("a", kind: "ghost")), fake))
            {
                var run = FourPointRun();
                ThreadEncounterPass.OnRunPopulated(run);

                Assert.AreEqual(0, seams.Queued.Count);
                Assert.AreEqual(0, run.EncounterPlan.Picks.Count, "an unregistered kind is never even picked");
                StringAssert.Contains(run.EncounterPlan.Dropped.First(), "kind 'ghost' is not registered");
            }
        }

        [TestMethod]
        public void A_run_that_ends_before_its_action_runs_places_nothing()
        {
            var fake = new FakeType();
            using var seams = new Seams(Store(Type("a")), fake);
            var run = FourPointRun();

            ThreadEncounterPass.OnRunPopulated(run);
            run.MarkEnded("test");
            ThreadEncounterPass.OnRunEnded(run, "test");
            seams.Drain();

            Assert.AreEqual(0, fake.Placed.Count);
            Assert.AreEqual(0, run.PlacedEncounters.Count);
            Assert.IsNull(run.EncounterContext, "the end sweep released the populate context");
        }

        // ---- run end --------------------------------------------------------------------------------------------

        [TestMethod]
        public void OnRunEnded_is_called_once_per_placed_encounter_and_clears_the_back_references()
        {
            var tracked = Item();
            var fake = new FakeType { OnPlace = r => { if (r.Instance.Id == 1) r.Instance.Track(tracked); return (true, null); } };
            using var seams = new Seams(Store(Type("a")), fake);
            var run = FourPointRun();

            ThreadEncounterPass.OnRunPopulated(run);
            seams.Drain();

            Assert.AreSame(run.PlacedEncounters[0], tracked.P_ThreadEncounter);
            Assert.AreSame(run, tracked.P_ThreadEncounter.Run);

            run.MarkEnded("test");
            Assert.AreEqual(2, ThreadEncounterPass.OnRunEnded(run, "the why"));
            Assert.AreEqual(0, ThreadEncounterPass.OnRunEnded(run, "again"), "a second sweep finds nothing");

            Assert.AreEqual(2, fake.Ended.Count);
            Assert.AreEqual(2, fake.Ended.Select(e => e.Instance.Id).Distinct().Count());
            Assert.IsTrue(fake.Ended.All(e => e.Why == "the why"));
            Assert.IsNull(tracked.P_ThreadEncounter);

            // An ended instance tracks nothing more: no back-reference that nothing would ever clear.
            var late = Item();
            fake.Ended[0].Instance.Track(late);
            Assert.IsNull(late.P_ThreadEncounter);
            Assert.AreEqual(0, fake.Ended[0].Instance.Objects.Count);
        }

        [TestMethod]
        public void An_encounter_whose_run_ends_while_it_is_placing_is_ended_exactly_once()
        {
            ThreadDungeonRun run = null;
            var fake = new FakeType();
            fake.OnPlace = r =>
            {
                // EndRun lands on another thread while Place runs: its sweep cannot see this instance yet.
                run.MarkEnded("raced");
                ThreadEncounterPass.OnRunEnded(run, "raced");
                return (true, null);
            };

            using var seams = new Seams(Store(Type("a")), fake);
            run = FourPointRun(max: 1);

            ThreadEncounterPass.OnRunPopulated(run);
            seams.Drain();

            Assert.AreEqual(1, fake.Placed.Count);
            Assert.AreEqual(1, fake.Ended.Count);
            Assert.AreSame(fake.Placed[0].Instance, fake.Ended[0].Instance);
            Assert.AreEqual(0, run.PlacedEncounters.Count);
            Assert.AreEqual(0, ThreadEncounterPass.OnRunEnded(run, "later"));
            Assert.AreEqual(1, fake.Ended.Count);
        }

        [TestMethod]
        public void A_throwing_OnRunEnded_does_not_stop_the_others()
        {
            var calls = 0;
            var thrower = new ThrowingEndType(() => calls++);
            using var seams = new Seams(Store(Type("a", kind: thrower.Kind)), thrower);
            var run = FourPointRun();

            ThreadEncounterPass.OnRunPopulated(run);
            seams.Drain();
            run.MarkEnded("test");

            Assert.AreEqual(2, ThreadEncounterPass.OnRunEnded(run, "test"));
            Assert.AreEqual(2, calls);
        }

        private sealed class ThrowingEndType : IThreadEncounterType
        {
            private readonly Action onEnd;

            public ThrowingEndType(Action onEnd) { this.onEnd = onEnd; }

            public string Kind => "thrower";

            public bool Place(ThreadEncounterPlaceRequest request, out string failure) { failure = null; return true; }

            public void OnRunEnded(ThreadEncounterInstance instance, string why) { onEnd(); throw new InvalidOperationException("end boom"); }
        }

        // ---- admin ----------------------------------------------------------------------------------------------

        [TestMethod]
        public void Force_place_picks_the_fitting_point_nearest_the_admin_and_uses_the_same_queue()
        {
            var fake = new FakeType();
            using var seams = new Seams(Store(Type("a", weight: 0, placement: p => p.MinClearance = 3f)), fake);

            var near = Point(DungeonCell(), 10, 0, clearance: 1f);   // nearest, but too tight
            var next = Point(DungeonCell(), 30, 0, clearance: 5f);
            var far = Point(DungeonCell(), 90, 0, clearance: 5f);
            var run = DungeonRun(new[] { far, near, next });

            Assert.IsTrue(ThreadEncounterPass.TryForcePlace(run, "fake", DungeonCell(), new System.Numerics.Vector3(12, 0, 0), out var message), message);
            Assert.AreEqual(1, seams.Queued.Count);

            seams.Drain();
            Assert.AreSame(next, fake.Placed.Single().Point.Point);
            Assert.AreEqual(-1, fake.Placed.Single().Instance.Pick.Slot);

            Assert.IsFalse(ThreadEncounterPass.TryForcePlace(run, "nope", DungeonCell(), System.Numerics.Vector3.Zero, out var refusal));
            StringAssert.Contains(refusal, "is registered");
            Assert.IsFalse(message.Contains("default row"), "a real row was used");
        }

        [TestMethod]
        public void Force_place_refuses_a_peddler_when_the_run_already_had_one()
        {
            var fake = new FakeType();
            using var seams = new Seams(Store(Type("a", weight: 0)), fake);
            var run = FourPointRun();

            Assert.IsTrue(run.TryClaimPeddler(), "fixture: the run's peddler slot is taken");
            Assert.IsFalse(ThreadEncounterPass.TryForcePlace(run, PeddlerEncounterType.KindName, DungeonCell(), System.Numerics.Vector3.Zero, out var message));
            Assert.AreEqual("This run already had its peddler.", message);
            Assert.AreEqual(0, seams.Queued.Count, "refused before dispatch");

            // Control: another kind is not affected by the peddler claim.
            Assert.IsTrue(ThreadEncounterPass.TryForcePlace(run, "fake", DungeonCell(), System.Numerics.Vector3.Zero, out var ok), ok);
        }

        [TestMethod]
        public void Force_place_refuses_a_row_whose_run_kinds_exclude_this_run()
        {
            var fake = new FakeType();
            using var seams = new Seams(Store(Type("a", placement: p => p.RunKinds = new System.Collections.Generic.List<string> { "outdoor" })), fake);
            var run = FourPointRun();

            Assert.IsFalse(ThreadEncounterPass.TryForcePlace(run, "fake", DungeonCell(), System.Numerics.Vector3.Zero, out var message));
            StringAssert.Contains(message, "not allowed in dungeon runs");
            Assert.AreEqual(0, seams.Queued.Count);

            // Control: the same row allowing dungeons places.
            using var allowed = new Seams(Store(Type("a", placement: p => p.RunKinds = new System.Collections.Generic.List<string> { "dungeon" })), fake);
            Assert.IsTrue(ThreadEncounterPass.TryForcePlace(FourPointRun(), "fake", DungeonCell(), System.Numerics.Vector3.Zero, out var ok), ok);
        }

        [TestMethod]
        public void Force_place_with_no_row_uses_a_default_row_with_an_empty_config_and_says_so()
        {
            var fake = new FakeType();
            using var seams = new Seams(ThreadEncounterStore.Empty, fake);
            var run = FourPointRun();

            Assert.IsTrue(ThreadEncounterPass.TryForcePlace(run, "fake", DungeonCell(), System.Numerics.Vector3.Zero, out var message), message);
            StringAssert.Contains(message, "used a default row");

            seams.Drain();
            var config = fake.Placed.Single().Def.Config;
            Assert.AreEqual(System.Text.Json.JsonValueKind.Object, config.ValueKind);
            Assert.AreEqual(0, config.EnumerateObject().Count());
        }

        [TestMethod]
        public void The_list_text_names_the_roll_the_drops_and_the_placements()
        {
            var fake = new FakeType();
            using var seams = new Seams(Store(Type("a", placement: p => p.MinSpacing = 1000f)), fake);
            var run = FourPointRun();

            ThreadEncounterPass.OnRunPopulated(run);
            seams.Drain();

            var text = ACE.Server.Command.Handlers.ThreadDungeonCommands.EncounterListText(run, new[] { "fake" });

            StringAssert.Contains(text, "Rolled 2/2 slot(s); 1 picked.");
            StringAssert.Contains(text, "slot 1: no type fits");
            StringAssert.Contains(text, "Placed: 1.");
            StringAssert.Contains(text, "registered=fake");
        }

        // ---- production wiring (source scans) -------------------------------------------------------------------

        [TestMethod]
        public void The_manager_runs_the_pass_on_populate_and_ends_encounters_in_EndRun()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadDungeonManager.cs");

            var populated = PooledLootSourceText.MethodBody(src, "public static void OnRunPopulated(ThreadDungeonRun run)");
            StringAssert.Contains(populated, "ThreadEncounterPass.OnRunPopulated(run);");
            Assert.IsTrue(populated.IndexOf("AnnounceCleared(run);", StringComparison.Ordinal) < populated.IndexOf("ThreadEncounterPass.OnRunPopulated(run);", StringComparison.Ordinal));

            var end = PooledLootSourceText.MethodBody(src, "public static void EndRun(ThreadDungeonRun run, string why)");
            var marked = end.IndexOf("run.MarkEnded(why, out var priorState)", StringComparison.Ordinal);
            var swept = end.IndexOf("ThreadEncounterPass.OnRunEnded(run, why);", StringComparison.Ordinal);
            Assert.IsTrue(marked >= 0 && swept > marked, "the encounter sweep runs after the run is marked Ended");

            // TryStart's stamp (the object initializer of the one `new ThreadDungeonRun(` in the manager).
            StringAssert.Contains(src, "EncountersEnabled = encountersEnabled,");
            StringAssert.Contains(src, "EncountersMaxPerRun = encountersMaxPerRun,");
            StringAssert.Contains(src, "EncounterSlotChance = encounterSlotChance,");
        }

        [TestMethod]
        public void The_spawner_records_the_encounter_context_before_MarkPopulated()
        {
            // Scoped to PlaceBatch, the populate chain's terminal step (a local function of TryPopulate).
            var src = PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadDungeonSpawner.cs"), "void PlaceBatch()");

            var context = src.IndexOf("run.MarkEncounterContext(new ThreadEncounterRunContext(plan, observedMaxNonBoss, outdoorPerimeter,", StringComparison.Ordinal);
            var populated = src.IndexOf("run.MarkPopulated(plan.Entries.Count, spawned,", StringComparison.Ordinal);

            Assert.IsTrue(context >= 0 && populated > context);
        }

        [TestMethod]
        public void The_pass_hands_each_type_the_run_points_so_defense_skips_a_point_without_portal_neighbours()
        {
            // Review R1 (2026-11-11): the pass's type filter is RejectPointAmong over the run's candidates.
            var other = new FakeType("other");
            var store = Store(Type("defense", kind: DefenseEncounterType.KindName, weight: 1000), Type("other", kind: "other"));
            using var seams = new Seams(store, new DefenseEncounterType(), other);

            for (var s = 0; s < 30; s++)
            {
                var lonely = DungeonRun(new[] { Point(DungeonCell(), 60f, 10f) }, seed: s, max: 1);
                var plan = ThreadEncounterPass.Plan(lonely, store);
                Assert.AreEqual("other", plan.Picks.Single().Def.Id, $"seed {s}");
            }

            // Control: a neighbour 15 m away lets the heavy defense row win.
            var paired = DungeonRun(new[] { Point(DungeonCell(), 60f, 10f), Point(DungeonCell(), 75f, 10f) }, seed: 3, max: 1);
            Assert.AreEqual("defense", ThreadEncounterPass.Plan(paired, store).Picks.Single().Def.Id);
        }

        [TestMethod]
        public void Force_place_also_skips_a_defense_point_without_portal_neighbours()
        {
            var store = Store(Type("defense", kind: DefenseEncounterType.KindName));
            using var seams = new Seams(store, new DefenseEncounterType());

            var lonely = DungeonRun(new[] { Point(DungeonCell(), 60f, 10f) });
            Assert.IsFalse(ThreadEncounterPass.TryForcePlace(lonely, DefenseEncounterType.KindName, DungeonCell(), new System.Numerics.Vector3(60, 10, 0), out var refusal));
            StringAssert.Contains(refusal, "defense: fewer than 1 portal point(s)");
            Assert.AreEqual(0, seams.Queued.Count);

            // Control: with a neighbour 15 m away the same admin place is queued.
            var paired = DungeonRun(new[] { Point(DungeonCell(), 60f, 10f), Point(DungeonCell(), 75f, 10f) });
            Assert.IsTrue(ThreadEncounterPass.TryForcePlace(paired, DefenseEncounterType.KindName, DungeonCell(), new System.Numerics.Vector3(60, 10, 0), out var ok), ok);
            Assert.AreEqual(1, seams.Queued.Count);
        }
    }
}
