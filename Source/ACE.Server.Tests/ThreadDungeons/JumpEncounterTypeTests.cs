using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using static ACE.Server.Tests.ThreadDungeons.ThreadEncounterTestFixtures;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The Jump encounter type: its config, RejectPoint, Place's refusals, the locked lifecycle (JumpState: claim,
    /// could-not-form, formed, finish, end), the queued-action guard, the shipped encounters.json row, and source-scan pins
    /// for the registration and the tunable wiring. Player-free: the state is driven with guids and positions. Placement
    /// into a live world (EnterWorld, the pad landing check) needs a loaded landblock and is left to the in-game checks.
    /// Reads no PropertyManager key.
    /// </summary>
    [TestClass]
    public class JumpEncounterTypeTests
    {
        private const uint A = 0x50000010u;
        private const uint B = 0x50000020u;

        private static JsonElement Json(string text)
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.Clone();
        }

        private static JumpState NewState() => new JumpState(null, new JumpConfig());

        /// <summary>A state with a formed course whose top pad is at global (100, 200), top face z 30, deadline <paramref name="deadline"/>.</summary>
        private static (JumpState State, int Gen) Formed(DateTime deadline, params uint[] startMembers)
        {
            var state = NewState();
            Assert.AreEqual(JumpStartOutcome.Claimed, state.TryClaim(false, true, out var gen));
            state.TopGx = 100;
            state.TopGy = 200;
            state.TopZ = 30f;
            Assert.IsTrue(state.MarkFormed(gen, deadline, startMembers));
            return (state, gen);
        }

        private static JumpGoalOutcome Ring(JumpState state, uint guid, DateTime now, float z = 30f, double gx = 100, double gy = 200, bool inside = true)
            => state.TryFinish(guid, false, inside, now, gx, gy, z, out _, out _);

        // ---- config ------------------------------------------------------------------------------------------------

        [TestMethod]
        public void An_empty_or_absent_config_parses_to_the_shipped_defaults()
        {
            foreach (var element in new[] { Json("{}"), default(JsonElement) })
            {
                var config = JumpEncounterType.ParseConfig(element, out var error);

                Assert.IsNull(error);
                Assert.AreEqual(1007960u, config.NpcWcid);
                Assert.AreEqual(1007961u, config.PlatformWcid);
                Assert.AreEqual(1007962u, config.GoalWcid);
                Assert.AreEqual(8, config.Platforms);
                Assert.AreEqual((2.0f, 3.0f), (config.RiseMin, config.RiseMax));
                Assert.AreEqual((1.5f, 3.0f), (config.GapMin, config.GapMax));
                Assert.AreEqual(12f, config.Radius);
                Assert.AreEqual(2.5f, config.FirstDistance);
                Assert.AreEqual(300u, config.DesignJumpSkill);
                Assert.AreEqual(4.0f, config.DesignRunSpeed);
            }
        }

        [TestMethod]
        public void A_bad_config_is_refused_with_a_reason()
        {
            foreach (var (text, fragment) in new[]
            {
                ("[]", "must be an object"),
                ("{\"npcWcid\": 0}", "npcWcid"),
                ("{\"platforms\": 6}", "7 to 9"),
                ("{\"platforms\": 10}", "7 to 9"),
                ("{\"rise\": {\"min\": 3, \"max\": 2}}", "config.rise"),
                ("{\"rise\": {\"min\": 2, \"max\": 4}}", "not reachable"),
                ("{\"gap\": {\"min\": 1, \"max\": 7}}", "not reachable"),
                ("{\"radius\": 3}", "radius"),
                ("{\"designJumpSkill\": 100}", "not reachable"),
                ("{\"rise\": 2}", "must be numbers"),
            })
            {
                var config = JumpEncounterType.ParseConfig(Json(text), out var error);
                Assert.IsNull(config, text);
                StringAssert.Contains(error, fragment, text);
            }

            var nine = JumpEncounterType.ParseConfig(Json("{\"platforms\": 9}"), out var nineError);
            Assert.IsNull(nineError);
            Assert.AreEqual(9, nine.Platforms);
        }

        [TestMethod]
        public void RejectPoint_keeps_the_course_outdoors_and_on_the_points_landblock()
        {
            var type = new JumpEncounterType();
            var def = Type("jump", kind: JumpEncounterType.KindName);
            def.Config = Json("{}");

            var centre = Candidate(Point(OutdoorCell(0x5456, 96, 96), 96, 96, kind: "outdoor"), outdoor: true);
            Assert.IsNull(type.RejectPoint(def, centre, isOutdoor: true));
            StringAssert.Contains(type.RejectPoint(def, centre, isOutdoor: false), "outdoor runs only");

            // [R + 2, 190 - R] with R = 12: [14, 178].
            Assert.IsNull(type.RejectPoint(def, Candidate(Point(OutdoorCell(0x5456, 14, 178), 14, 178, kind: "outdoor"), outdoor: true), true));
            StringAssert.Contains(type.RejectPoint(def, Candidate(Point(OutdoorCell(0x5456, 13.9f, 96), 13.9f, 96, kind: "outdoor"), outdoor: true), true), "landblock");
            StringAssert.Contains(type.RejectPoint(def, Candidate(Point(OutdoorCell(0x5456, 96, 178.1f), 96, 178.1f, kind: "outdoor"), outdoor: true), true), "landblock");

            var bad = Type("jump", kind: JumpEncounterType.KindName);
            bad.Config = Json("{\"platforms\": 2}");
            StringAssert.Contains(type.RejectPoint(bad, centre, true), "7 to 9");
        }

        [TestMethod]
        public void Place_refuses_cleanly_on_a_dungeon_run_a_bad_config_no_room_or_a_missing_weenie()
        {
            var savedCreate = JumpEncounterType.CreateObject;
            var savedGround = JumpEncounterType.GroundFor;
            var savedProbe = JumpEncounterType.ProbePlacement;

            try
            {
                var created = 0;
                JumpEncounterType.ProbePlacement = (wcid, at, exact) => null;
                JumpEncounterType.CreateObject = _ => { created++; return null; };

                var fake = new JumpEncounterType();
                using var seams = new Seams(Store(), fake);

                // A dungeon run: refused before anything else.
                var dungeon = DungeonRun(new[] { Point(DungeonCell(), 0, 0) }, max: 1);
                var dungeonDef = Type("jump", kind: JumpEncounterType.KindName);
                dungeonDef.Config = Json("{}");
                Assert.IsNull(ThreadEncounterPass.PlaceOne(dungeon, null, new ThreadEncounterPick(1, dungeonDef, Candidate(Point(DungeonCell(), 0, 0)), 7), fake));
                Assert.IsTrue(dungeon.EncounterOutcomes.Any(l => l.Contains("outdoor runs only")), string.Join("\n", dungeon.EncounterOutcomes));

                var point = Point(OutdoorCell(0x5456, 96, 96), 96, 96, 40f, kind: "outdoor");

                foreach (var (config, ground, fragment) in new (string, Func<float, float, float?>, string)[]
                {
                    ("{\"platforms\": 2}", (x, y) => 40f, "7 to 9"),
                    ("{}", (x, y) => null, "no room for the course"),
                    ("{}", (x, y) => 40f, "did not create"),
                })
                {
                    JumpEncounterType.GroundFor = _ => ground;
                    var run = OutdoorRun(new[] { point }, max: 1);
                    var def = Type("jump", kind: JumpEncounterType.KindName);
                    def.Config = Json(config);

                    var placed = ThreadEncounterPass.PlaceOne(run, null, new ThreadEncounterPick(1, def, Candidate(point, outdoor: true), 7), fake);

                    Assert.IsNull(placed, config);
                    Assert.AreEqual(0, run.PlacedEncounters.Count, config);
                    Assert.IsTrue(run.EncounterOutcomes.Any(l => l.Contains(fragment)), config + ": " + string.Join("\n", run.EncounterOutcomes));
                }

                Assert.AreEqual(1, created, "only a config with room reaches the factory");
            }
            finally
            {
                JumpEncounterType.CreateObject = savedCreate;
                JumpEncounterType.GroundFor = savedGround;
                JumpEncounterType.ProbePlacement = savedProbe;
            }
        }

        // ---- lifecycle -------------------------------------------------------------------------------------------

        [TestMethod]
        public void A_non_member_is_refused_without_starting()
        {
            var state = NewState();

            Assert.AreEqual(JumpStartOutcome.NotInside, state.TryClaim(false, false, out var gen));
            Assert.AreEqual(0, gen);
            Assert.AreEqual(JumpPhase.Idle, state.Phase);
            Assert.AreEqual(0, state.Generation);

            Assert.AreEqual(JumpStartOutcome.RunOver, state.TryClaim(true, true, out _), "a use after the run ended is silent");
            Assert.AreEqual(JumpPhase.Idle, state.Phase);

            Assert.AreEqual(JumpStartOutcome.Claimed, state.TryClaim(false, true, out gen), "the encounter is still there for a member");
            Assert.AreEqual(1, gen);
        }

        [TestMethod]
        public void Two_uses_in_one_tick_give_one_start()
        {
            for (var round = 0; round < 20; round++)
            {
                var state = NewState();
                var outcomes = new ConcurrentBag<JumpStartOutcome>();
                using var go = new ManualResetEventSlim(false);

                var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
                {
                    go.Wait();
                    outcomes.Add(state.TryClaim(false, true, out _));
                })).ToArray();

                go.Set();
                Task.WaitAll(tasks);

                Assert.AreEqual(1, outcomes.Count(o => o == JumpStartOutcome.Claimed), $"round {round}");
                Assert.AreEqual(7, outcomes.Count(o => o == JumpStartOutcome.AlreadyRunning), $"round {round}");
                Assert.AreEqual(1, state.Generation);
            }
        }

        [TestMethod]
        public void A_stale_or_ended_run_action_is_a_no_op()
        {
            var run = OutdoorRun(new[] { Point(OutdoorCell(0x5456, 96, 96), 96, 96, kind: "outdoor") });
            var instance = run.NewEncounterInstance(new ThreadEncounterPick(1, Type("jump", kind: "jump"), Candidate(Point(OutdoorCell(0x5456, 96, 96), 96, 96, kind: "outdoor"), outdoor: true), 7), new JumpEncounterType());
            var request = new ThreadEncounterPlaceRequest(run, null, instance, run.EncounterContext);
            var state = new JumpState(request, new JumpConfig());
            var ran = 0;

            Assert.IsFalse(JumpEncounterType.RunIfLive(run, state, 1, () => ran++), "Idle: no attempt is live");

            Assert.AreEqual(JumpStartOutcome.Claimed, state.TryClaim(false, true, out var gen));
            Assert.IsTrue(JumpEncounterType.RunIfLive(run, state, gen, () => ran++), "the live attempt runs");
            Assert.AreEqual(1, ran);

            Assert.IsFalse(JumpEncounterType.RunIfLive(run, state, gen + 1, () => ran++), "another generation");

            // A could-not-form and a retry: the first attempt's queued actions are stale.
            Assert.IsTrue(state.FormFailed(gen));
            Assert.AreEqual(JumpStartOutcome.Claimed, state.TryClaim(false, true, out var retry));
            Assert.IsFalse(JumpEncounterType.RunIfLive(run, state, gen, () => ran++), "the failed attempt's action");
            Assert.IsTrue(JumpEncounterType.RunIfLive(run, state, retry, () => ran++));
            Assert.AreEqual(2, ran);

            // Done: nothing queued runs.
            Assert.IsTrue(state.MarkFormed(retry, DateTime.UtcNow.AddSeconds(60), new[] { A }));
            Assert.IsTrue(state.TryEnd(retry));
            Assert.IsFalse(JumpEncounterType.RunIfLive(run, state, retry, () => ran++), "the course ended");

            // The run's encounters ended / the request was released.
            var state2 = new JumpState(request, new JumpConfig());
            Assert.AreEqual(JumpStartOutcome.Claimed, state2.TryClaim(false, true, out var g2));
            state2.Request = null;
            Assert.IsFalse(JumpEncounterType.RunIfLive(run, state2, g2, () => ran++), "OnRunEnded released the request");

            var state3 = new JumpState(request, new JumpConfig());
            Assert.AreEqual(JumpStartOutcome.Claimed, state3.TryClaim(false, true, out var g3));
            run.TakeEncountersForEnd();
            Assert.IsFalse(JumpEncounterType.RunIfLive(run, state3, g3, () => ran++), "the run's encounters ended");

            var state4 = new JumpState(request, new JumpConfig());
            Assert.AreEqual(JumpStartOutcome.Claimed, state4.TryClaim(false, true, out var g4));
            var live = OutdoorRun(new[] { Point(OutdoorCell(0x5456, 96, 96), 96, 96, kind: "outdoor") });
            live.MarkEnded("test");
            Assert.IsFalse(JumpEncounterType.RunIfLive(live, state4, g4, () => ran++), "the run ended");

            Assert.AreEqual(2, ran);
        }

        [TestMethod]
        public void OnRunEnded_releases_the_request_and_closes_the_fall_zone()
        {
            try
            {
                var run = OutdoorRun(new[] { Point(OutdoorCell(0x5456, 96, 96), 96, 96, kind: "outdoor") });
                var type = new JumpEncounterType();
                var instance = run.NewEncounterInstance(new ThreadEncounterPick(1, Type("jump", kind: "jump"), Candidate(Point(OutdoorCell(0x5456, 96, 96), 96, 96, kind: "outdoor"), outdoor: true), 7), type);
                var state = new JumpState(new ThreadEncounterPlaceRequest(run, null, instance, run.EncounterContext), new JumpConfig());
                instance.State = state;
                var now = DateTime.UtcNow;
                state.AttachZone(JumpFallGuard.Open(run.Instance, 1000, 1000, 12f, now), now);
                Assert.AreEqual(long.MaxValue, state.Zone.ExpiresTicks, "open while the run lives");

                type.OnRunEnded(instance, "test");
                type.OnRunEnded(instance, "again");

                Assert.IsNull(state.Request);
                Assert.IsTrue(state.Zone.ExpiresTicks <= now.AddSeconds(JumpFallGuard.GraceSeconds + 5).Ticks, "the zone lapses after its grace");
                Assert.IsFalse(JumpFallGuard.IsExempt(run.Instance, 1000, 1000, DateTime.UtcNow.AddSeconds(JumpFallGuard.GraceSeconds + 5)));
            }
            finally
            {
                JumpFallGuard.Clear();
            }
        }

        [TestMethod]
        public void Could_not_form_returns_to_idle_and_the_encounter_is_not_spent()
        {
            var state = NewState();
            Assert.AreEqual(JumpStartOutcome.Claimed, state.TryClaim(false, true, out var gen));
            Assert.AreEqual(JumpStartOutcome.AlreadyRunning, state.TryClaim(false, true, out _), "while it forms");

            Assert.IsTrue(state.FormFailed(gen));
            Assert.AreEqual(JumpPhase.Idle, state.Phase);
            Assert.AreEqual(DateTime.MinValue, state.Deadline, "no timer started");
            Assert.IsFalse(state.FormFailed(gen), "idempotent");
            Assert.AreEqual(JumpGoalOutcome.NotRunning, Ring(state, A, DateTime.UtcNow), "no course to finish");

            Assert.AreEqual(JumpStartOutcome.Claimed, state.TryClaim(false, true, out var again));
            Assert.AreEqual(gen + 1, again);
            Assert.IsFalse(state.MarkFormed(gen, DateTime.UtcNow, new[] { A }), "the failed attempt cannot form late");
            Assert.IsTrue(state.MarkFormed(again, DateTime.UtcNow.AddSeconds(60), new[] { A }));
            Assert.IsFalse(state.FormFailed(again), "a formed course cannot fall back to Idle");
        }

        [TestMethod]
        public void A_goal_use_after_the_deadline_is_refused()
        {
            var now = DateTime.UtcNow;
            var (state, _) = Formed(now.AddSeconds(60), A);

            Assert.AreEqual(JumpGoalOutcome.TimeUp, Ring(state, A, now.AddSeconds(60.001)), "checked at use time, before the timeout runs");
            Assert.AreEqual(0, state.Finishers.Count);
            Assert.AreEqual(JumpGoalOutcome.Finished, Ring(state, A, now.AddSeconds(60)), "at the deadline still counts");
        }

        [TestMethod]
        public void A_goal_use_below_or_off_the_top_is_refused()
        {
            var now = DateTime.UtcNow;
            var (state, _) = Formed(now.AddSeconds(60), A, B);

            Assert.AreEqual(JumpGoalOutcome.NotOnTop, Ring(state, A, now, z: 30f - 0.31f), "just under the top face");
            Assert.AreEqual(JumpGoalOutcome.NotOnTop, Ring(state, A, now, z: 27f), "the pad below");
            Assert.AreEqual(JumpGoalOutcome.NotOnTop, Ring(state, A, now, z: 10f), "the ground");
            Assert.AreEqual(JumpGoalOutcome.NotOnTop, Ring(state, A, now, z: 32.51f), "jumping well over it");
            Assert.AreEqual(JumpGoalOutcome.NotOnTop, Ring(state, A, now, gx: 100 + 1.61), "beside it");
            Assert.AreEqual(JumpGoalOutcome.NotOnTop, Ring(state, A, now, z: float.NaN), "no position");
            Assert.AreEqual(0, state.Finishers.Count);

            Assert.AreEqual(JumpGoalOutcome.Finished, Ring(state, A, now, z: 30f - 0.3f, gx: 100 + 1.6, gy: 200 - 1.6), "the edge of the tolerance");
            Assert.AreEqual(JumpGoalOutcome.Finished, Ring(state, B, now, z: 32.5f));

            Assert.IsTrue(JumpEncounterType.IsOnTopPad(0, 0, 5f, 0, 0, 5f));
            Assert.IsFalse(JumpEncounterType.IsOnTopPad(0, 0, 5f, 0, 0, 4.69f));
        }

        [TestMethod]
        public void The_goal_checks_run_in_order()
        {
            var now = DateTime.UtcNow;
            var (state, _) = Formed(now.AddSeconds(60), A);

            Assert.AreEqual(JumpGoalOutcome.RunOver, state.TryFinish(A, true, true, now, 100, 200, 30f, out _, out _));
            Assert.AreEqual(JumpGoalOutcome.NotInside, Ring(state, A, now, inside: false), "a visitor on the top is still refused");
            Assert.AreEqual(JumpGoalOutcome.TimeUp, Ring(state, A, now.AddSeconds(61), z: 0f), "time before position");
            Assert.AreEqual(0, state.Finishers.Count);
        }

        [TestMethod]
        public void The_finisher_latch_fires_once_per_player()
        {
            var now = DateTime.UtcNow;
            var (state, _) = Formed(now.AddSeconds(60), A, B);

            Assert.AreEqual(JumpGoalOutcome.Finished, Ring(state, A, now));
            Assert.AreEqual(JumpGoalOutcome.AlreadyFinished, Ring(state, A, now));
            Assert.AreEqual(JumpGoalOutcome.AlreadyFinished, Ring(state, A, now.AddSeconds(1)));
            CollectionAssert.AreEquivalent(new[] { A }, state.Finishers.ToList());

            // Concurrent rings by one player: one Finished.
            var (racing, _) = Formed(now.AddSeconds(60), A, B);
            var outcomes = new ConcurrentBag<JumpGoalOutcome>();
            Parallel.For(0, 16, _ => outcomes.Add(Ring(racing, B, now)));
            Assert.AreEqual(1, outcomes.Count(o => o == JumpGoalOutcome.Finished));
        }

        [TestMethod]
        public void Everyone_inside_at_the_start_finishing_ends_early_and_Done_refuses_a_restart()
        {
            var now = DateTime.UtcNow;
            var (state, gen) = Formed(now.AddSeconds(60), A, B);

            Assert.AreEqual(JumpGoalOutcome.Finished, state.TryFinish(A, false, true, now, 100, 200, 30f, out var everyone, out var g1));
            Assert.IsFalse(everyone, "B has not finished");
            Assert.AreEqual(gen, g1);

            // A late arrival who was not inside at the start may still finish, and does not hold the early end back.
            Assert.AreEqual(JumpGoalOutcome.Finished, state.TryFinish(0x50000099u, false, true, now, 100, 200, 30f, out everyone, out _));
            Assert.IsFalse(everyone);

            Assert.AreEqual(JumpGoalOutcome.Finished, state.TryFinish(B, false, true, now, 100, 200, 30f, out everyone, out _));
            Assert.IsTrue(everyone, "every start member has finished");

            Assert.IsTrue(state.TryEnd(gen));
            Assert.IsFalse(state.TryEnd(gen), "the timeout that follows the early end is a no-op");
            Assert.AreEqual(JumpPhase.Done, state.Phase);

            Assert.AreEqual(JumpStartOutcome.Spent, state.TryClaim(false, true, out _), "one shot");
            Assert.AreEqual(JumpPhase.Done, state.Phase);
            Assert.AreEqual(JumpGoalOutcome.NotRunning, Ring(state, A, now));
        }

        [TestMethod]
        public void A_timeout_spends_the_encounter_with_nobody_finished()
        {
            var now = DateTime.UtcNow;
            var (state, gen) = Formed(now.AddSeconds(60), A);

            Assert.IsFalse(state.TryEnd(gen + 1), "a stale timeout");
            Assert.IsTrue(state.TryEnd(gen));
            Assert.AreEqual(JumpStartOutcome.Spent, state.TryClaim(false, true, out _));
            Assert.AreEqual(0, state.Finishers.Count);

            var idle = NewState();
            Assert.AreEqual(JumpStartOutcome.Claimed, idle.TryClaim(false, true, out var unformed));
            Assert.IsFalse(idle.TryEnd(unformed), "a course still forming cannot time out");
        }

        [TestMethod]
        public void The_countdown_marks_fit_the_timer()
        {
            CollectionAssert.AreEqual(new[] { (30, 30.0), (10, 50.0), (5, 55.0) }, JumpEncounterType.CountdownSchedule(60).ToList());
            CollectionAssert.AreEqual(new[] { (10, 5.0), (5, 10.0) }, JumpEncounterType.CountdownSchedule(15).ToList());
            Assert.AreEqual("30 seconds remain.", JumpEncounterType.CountdownLine(30));
            Assert.AreEqual("Pell reached the summit!", JumpEncounterType.OtherFinishedLine("Pell"));
        }

        [TestMethod]
        public void The_tunable_clamps_to_15_and_300()
        {
            Assert.AreEqual(60L, ThreadDungeonRun.DefaultJumpTimeSeconds);
            Assert.AreEqual(15L, ThreadDungeonRun.ClampJumpTimeSeconds(0));
            Assert.AreEqual(15L, ThreadDungeonRun.ClampJumpTimeSeconds(-5));
            Assert.AreEqual(90L, ThreadDungeonRun.ClampJumpTimeSeconds(90));
            Assert.AreEqual(300L, ThreadDungeonRun.ClampJumpTimeSeconds(1000));

            var run = OutdoorRun(new[] { Point(OutdoorCell(0x5456, 96, 96), 96, 96, kind: "outdoor") });
            Assert.AreEqual(60L, run.JumpTimeSeconds, "an unstamped run uses the shipped default");
        }

        [TestMethod]
        public void The_player_text_is_ascii_and_verbatim()
        {
            var lines = new[]
            {
                JumpEncounterType.RefusedLine, JumpEncounterType.RunningLine, JumpEncounterType.SpentLine, JumpEncounterType.CouldNotFormLine,
                JumpEncounterType.StartLine, JumpEncounterType.NotOnTopLine, JumpEncounterType.FinishedLine, JumpEncounterType.TimeoutLine,
                JumpEncounterType.EarlyEndLine,
            };

            Assert.AreEqual("Only members of this thread can begin the climb.", JumpEncounterType.RefusedLine);
            Assert.AreEqual("The climb is already under way.", JumpEncounterType.RunningLine);
            Assert.AreEqual("The platforms have already been raised here.", JumpEncounterType.SpentLine);
            Assert.AreEqual("The platforms cannot rise here. The Trialmaster's course is blocked.", JumpEncounterType.CouldNotFormLine);
            Assert.AreEqual("The platforms rise! Reach the Summit Bell at the top before time runs out.", JumpEncounterType.StartLine);
            Assert.AreEqual("You must stand atop the highest platform to ring the bell.", JumpEncounterType.NotOnTopLine);
            Assert.AreEqual("You reached the summit!", JumpEncounterType.FinishedLine);
            Assert.AreEqual("Time is up. The platforms fade away.", JumpEncounterType.TimeoutLine);
            Assert.AreEqual("Everyone reached the summit. The platforms fade away.", JumpEncounterType.EarlyEndLine);
            Assert.IsTrue(lines.All(l => l.All(c => c >= 0x20 && c < 0x7F)));
        }

        // ---- shipped data and wiring ----------------------------------------------------------------------------

        [TestMethod]
        public void The_shipped_row_is_outdoor_only_weight_one_and_parses_clean()
        {
            var text = File.ReadAllText(Path.Combine(ThreadDungeonStoreTests.FindDynamicDir(), ThreadEncounterStore.FileName));
            var diagnostics = new List<string>();
            var store = ThreadEncounterStore.Parse(text, null, diagnostics);

            Assert.AreEqual(0, diagnostics.Count, string.Join("\n", diagnostics));

            var row = store.Types.Single(t => t.Kind == JumpEncounterType.KindName);
            var shrine = store.Types.Single(t => t.Kind == ShrineEncounterType.KindName);

            Assert.IsTrue(row.Enabled);
            Assert.AreEqual(shrine.Weight, row.Weight, "the Shrine's weight");
            Assert.AreEqual(1.0, row.Weight);
            Assert.IsTrue(row.Placement.AllowsRunKind(true));
            Assert.IsFalse(row.Placement.AllowsRunKind(false), "outdoor only");

            var config = JumpEncounterType.ParseConfig(row.Config, out var error);
            Assert.IsNull(error);
            Assert.AreEqual(300u, config.DesignJumpSkill);
            Assert.AreEqual(8, config.Platforms);
            Assert.AreEqual(1007960u, config.NpcWcid);
        }

        [TestMethod]
        public void The_type_is_registered_and_the_tunable_is_read_and_stamped_by_TryStart()
        {
            var type = new JumpEncounterType();
            Assert.AreEqual("jump", type.Kind);
            Assert.IsInstanceOfType(type, typeof(IThreadEncounterUsable));

            var src = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadDungeonManager.cs");
            var init = PooledLootSourceText.MethodBody(src, "public static void Initialize()");
            var register = init.IndexOf("ThreadEncounterTypes.Register(new JumpEncounterType());", StringComparison.Ordinal);
            Assert.IsTrue(register >= 0 && register < init.IndexOf("Store = store;", StringComparison.Ordinal), "registered before the store is published");

            StringAssert.Contains(src, "var jumpTimeSeconds = ThreadDungeonRun.ClampJumpTimeSeconds(PropertyManager.GetLong(\"dynamic_dungeons_jump_time_seconds\", ThreadDungeonRun.DefaultJumpTimeSeconds).Item);");
            StringAssert.Contains(src, "JumpTimeSeconds = jumpTimeSeconds,");

            var pm = PooledLootSourceText.Read("Source/ACE.Server/Managers/PropertyManager.cs");
            StringAssert.Contains(pm, "(\"dynamic_dungeons_jump_time_seconds\", new Property<long>(ACE.Server.ThreadDungeons.ThreadDungeonRun.DefaultJumpTimeSeconds,");

            var metadata = PooledLootSourceText.Read("Source/ACE.Server/config-metadata.tsv").Replace("\r\n", "\n");
            StringAssert.Contains(metadata, "\ndynamic_dungeons_jump_time_seconds\tthreads\tjump\tfork\tno\n");

            var defaults = PooledLootSourceText.Read("Source/config-defaults.tsv").Replace("\r\n", "\n");
            StringAssert.Contains(defaults, "\ndynamic_dungeons_jump_time_seconds\tlong\t60\t60\t\n");

            // The course reads the stamped value, never PropertyManager, at use time.
            var jump = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/JumpEncounterType.cs");
            Assert.IsFalse(jump.Contains("PropertyManager"), "JumpEncounterType reads no PropertyManager key");
            StringAssert.Contains(jump, "ThreadDungeonRun.ClampJumpTimeSeconds(run.JumpTimeSeconds)");
        }
    }
}
