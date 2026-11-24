using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using static ACE.Server.Tests.ThreadDungeons.ThreadEncounterTestFixtures;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The Jump encounter's world-touching paths with fake objects (JumpEncounterType.Form, CouldNotForm, DestroyCourse,
    /// EndCourse, RingGoal), driven through the CreateObject / EnterWorld / DestroyObject / MembersInside / GroundFor seams:
    /// a pad that fails to enter or lands elsewhere tears the attempt down back to Idle, an end destroys every pad and the
    /// goal exactly once, a stale Form creates nothing, the finisher hook fires once per player, the fall zone cannot leak
    /// past the run's end, and a throw after the course formed ends it. Every seam and the fall-zone list are restored in
    /// a finally. Reads no PropertyManager key.
    /// </summary>
    [TestClass]
    public class JumpCourseLifecycleTests
    {
        private const uint A = 0x50000010u;
        private static uint nextGuid = 0x7F0E3001;

        private sealed class World : IDisposable
        {
            private readonly Func<uint, WorldObject> create = JumpEncounterType.CreateObject;
            private readonly Func<ThreadEncounterPlaceRequest, Func<float, float, float?>> ground = JumpEncounterType.GroundFor;
            private readonly Func<WorldObject, bool> enter = JumpEncounterType.EnterWorld;
            private readonly Action<WorldObject> destroy = JumpEncounterType.DestroyObject;
            private readonly Func<ThreadDungeonRun, List<Player>> members = JumpEncounterType.MembersInside;
            private readonly Action afterFormed = JumpEncounterType.AfterFormed;
            private readonly Action<ThreadEncounterInstance, uint> observed = JumpEncounterType.CompletionObserved;

            public List<WorldObject> Created { get; } = new List<WorldObject>();
            public List<WorldObject> Entered { get; } = new List<WorldObject>();
            public List<WorldObject> Destroyed { get; } = new List<WorldObject>();

            /// <summary>Decides each EnterWorld by its 1-based order; true by default.</summary>
            public Func<int, WorldObject, bool> Enter { get; set; } = (n, wo) => true;

            public World()
            {
                JumpEncounterFallReset();
                JumpEncounterType.CreateObject = wcid =>
                {
                    var wo = new GenericObject(new Weenie
                    {
                        WeenieClassId = wcid,
                        WeenieType = WeenieType.Generic,
                        PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, $"fake {wcid}" } },
                    }, new ObjectGuid(nextGuid++));
                    Created.Add(wo);
                    return wo;
                };
                JumpEncounterType.GroundFor = _ => (x, y) => 40f;
                JumpEncounterType.EnterWorld = wo => { Entered.Add(wo); return Enter(Entered.Count, wo); };
                JumpEncounterType.DestroyObject = wo => Destroyed.Add(wo);
                JumpEncounterType.MembersInside = _ => new List<Player>();
            }

            public void Dispose()
            {
                JumpEncounterType.CreateObject = create;
                JumpEncounterType.GroundFor = ground;
                JumpEncounterType.EnterWorld = enter;
                JumpEncounterType.DestroyObject = destroy;
                JumpEncounterType.MembersInside = members;
                JumpEncounterType.AfterFormed = afterFormed;
                JumpEncounterType.CompletionObserved = observed;
                JumpEncounterFallReset();
            }

            private static void JumpEncounterFallReset() => JumpFallGuard.Clear();
        }

        private static (ThreadEncounterInstance Instance, JumpState State) NewEncounter()
        {
            var point = Point(OutdoorCell(0x5456, 96, 96), 96, 96, 40f, kind: "outdoor");
            var run = OutdoorRun(new[] { point });
            var type = new JumpEncounterType();
            var instance = run.NewEncounterInstance(new ThreadEncounterPick(1, Type("jump", kind: JumpEncounterType.KindName), Candidate(point, outdoor: true), 7), type);
            var state = new JumpState(new ThreadEncounterPlaceRequest(run, null, instance, run.EncounterContext), new JumpConfig());
            instance.State = state;
            return (instance, state);
        }

        private static int Claim(JumpState state)
        {
            Assert.AreEqual(JumpStartOutcome.Claimed, state.TryClaim(false, true, out var gen));
            return gen;
        }

        [TestMethod]
        public void A_pad_failing_to_enter_destroys_the_pads_so_far_and_returns_to_idle()
        {
            using var world = new World { Enter = (n, wo) => n != 3 };
            var (instance, state) = NewEncounter();
            var gen = Claim(state);

            JumpEncounterType.Form(instance, state, gen, null);

            Assert.AreEqual(3, world.Created.Count, "pads 1-3 were made, then it stopped");
            CollectionAssert.AreEquivalent(world.Created, world.Destroyed, "pads 1-3 destroyed");
            Assert.AreEqual(3, world.Destroyed.Distinct().Count(), "each exactly once");
            Assert.AreEqual(JumpPhase.Idle, state.Phase);
            Assert.AreEqual(0u, state.GoalGuid, "no goal");
            Assert.IsNull(state.Zone, "no fall zone");
            Assert.AreEqual(0, JumpFallGuard.Zones.Count);
            Assert.IsTrue(instance.Run.EncounterOutcomes.Any(l => l.Contains("could not form")), string.Join("\n", instance.Run.EncounterOutcomes));
        }

        [TestMethod]
        public void A_pad_that_lands_elsewhere_refuses_the_course()
        {
            using var world = new World
            {
                Enter = (n, wo) =>
                {
                    if (n == 5)
                        wo.Location.PositionZ += 0.05f; // the placement nudged it
                    return true;
                },
            };
            var (instance, state) = NewEncounter();
            var gen = Claim(state);

            JumpEncounterType.Form(instance, state, gen, null);

            Assert.AreEqual(5, world.Created.Count);
            Assert.AreEqual(5, world.Destroyed.Distinct().Count());
            Assert.AreEqual(JumpPhase.Idle, state.Phase);
        }

        [TestMethod]
        public void A_formed_course_ends_once_destroying_every_pad_and_the_goal_exactly_once()
        {
            using var world = new World();
            var (instance, state) = NewEncounter();
            var gen = Claim(state);

            JumpEncounterType.Form(instance, state, gen, null);

            var config = new JumpConfig();
            Assert.AreEqual(config.Platforms + 1, world.Created.Count, "the pads and the goal");
            Assert.AreEqual(config.Platforms + 1, world.Entered.Count);
            Assert.AreEqual(0, world.Destroyed.Count);
            Assert.AreEqual(JumpPhase.Running, state.Phase);
            Assert.AreNotEqual(DateTime.MinValue, state.Deadline);
            Assert.AreEqual(world.Created.Last().Guid.Full, state.GoalGuid);
            Assert.AreSame(instance, world.Created.Last().P_ThreadEncounter, "the goal is tracked");
            Assert.IsTrue(world.Created.Take(config.Platforms).All(p => p.P_ThreadEncounter == null), "the pads are not tracked");
            Assert.AreEqual(long.MaxValue, state.Zone.ExpiresTicks, "the fall zone is open");

            JumpEncounterType.EndCourse(instance, state, gen, JumpEncounterType.TimeoutLine);

            CollectionAssert.AreEquivalent(world.Created, world.Destroyed, "every pad and the goal");
            Assert.AreEqual(world.Created.Count, world.Destroyed.Distinct().Count(), "each exactly once");
            Assert.AreEqual(JumpPhase.Done, state.Phase);
            Assert.AreNotEqual(long.MaxValue, state.Zone.ExpiresTicks, "the fall zone is closed (grace only)");

            JumpEncounterType.EndCourse(instance, state, gen, JumpEncounterType.TimeoutLine);
            JumpEncounterType.EndCourse(instance, state, gen, JumpEncounterType.EarlyEndLine);
            Assert.AreEqual(world.Created.Count, world.Destroyed.Count, "a second end does nothing");
        }

        [TestMethod]
        public void A_Form_queued_under_a_stale_generation_creates_nothing()
        {
            using var world = new World();
            var (instance, state) = NewEncounter();
            var first = Claim(state);
            Assert.IsTrue(state.FormFailed(first));
            Claim(state);

            JumpEncounterType.Form(instance, state, first, null);
            Assert.AreEqual(0, world.Created.Count, "the stale attempt");

            var (ended, endedState) = NewEncounter();
            var gen = Claim(endedState);
            ended.Run.MarkEnded("test");
            JumpEncounterType.Form(ended, endedState, gen, null);
            Assert.AreEqual(0, world.Created.Count, "the run ended");
        }

        [TestMethod]
        public void A_finisher_reaches_the_completion_hook_exactly_once()
        {
            using var world = new World();
            var (instance, state) = NewEncounter();
            var gen = Claim(state);
            JumpEncounterType.Form(instance, state, gen, null);

            var seen = new List<uint>();
            JumpEncounterType.CompletionObserved = (i, guid) => { Assert.AreSame(instance, i); seen.Add(guid); };

            var now = DateTime.UtcNow;
            var outcomes = new[]
            {
                JumpEncounterType.RingGoal(instance, state, A, "Alpha", true, now, state.TopGx, state.TopGy, state.TopZ - 5f, null),
                JumpEncounterType.RingGoal(instance, state, A, "Alpha", true, now, state.TopGx, state.TopGy, state.TopZ, null),
                JumpEncounterType.RingGoal(instance, state, A, "Alpha", true, now, state.TopGx, state.TopGy, state.TopZ, null),
                JumpEncounterType.RingGoal(instance, state, A, "Alpha", false, now, state.TopGx, state.TopGy, state.TopZ, null),
            };

            CollectionAssert.AreEqual(new[] { JumpGoalOutcome.NotOnTop, JumpGoalOutcome.Finished, JumpGoalOutcome.AlreadyFinished, JumpGoalOutcome.NotInside }, outcomes);
            CollectionAssert.AreEqual(new[] { A }, seen, "one completion");
        }

        [TestMethod]
        public void A_zone_attached_after_the_run_ended_is_closed_at_once()
        {
            using var world = new World();
            var (_, state) = NewEncounter();
            var now = DateTime.UtcNow;

            // OnRunEnded ran first (and saw no zone); the forming course attaches its zone afterwards.
            state.Request = null;
            state.AttachZone(JumpFallGuard.Open(0x80005678u, 1000, 1000, 12f, now), now);

            Assert.AreNotEqual(long.MaxValue, state.Zone.ExpiresTicks, "closed by AttachZone itself");
            Assert.IsFalse(JumpFallGuard.IsExempt(0x80005678u, 1000, 1000, now.AddSeconds(JumpFallGuard.GraceSeconds + 1)), "it lapses after the grace");

            // The other order: attached first, then Release closes it.
            var (_, live) = NewEncounter();
            live.AttachZone(JumpFallGuard.Open(0x80005678u, 2000, 2000, 12f, now), now);
            Assert.AreEqual(long.MaxValue, live.Zone.ExpiresTicks);
            live.Release(now);
            Assert.IsNull(live.Request);
            Assert.AreNotEqual(long.MaxValue, live.Zone.ExpiresTicks);
        }

        [TestMethod]
        public void A_throw_after_the_course_formed_ends_it_instead_of_leaving_it_running()
        {
            using var world = new World();
            JumpEncounterType.AfterFormed = () => throw new InvalidOperationException("late failure");
            var (instance, state) = NewEncounter();
            var gen = Claim(state);

            JumpEncounterType.Form(instance, state, gen, null);

            Assert.AreEqual(JumpPhase.Done, state.Phase, "ended and spent, not left Running");
            CollectionAssert.AreEquivalent(world.Created, world.Destroyed, "the course is gone");
            Assert.IsTrue(state.Zone == null || state.Zone.ExpiresTicks != long.MaxValue, "no fall zone left open");
            Assert.IsTrue(JumpFallGuard.Zones.All(z => z.ExpiresTicks != long.MaxValue));
        }

        [TestMethod]
        public void Every_exit_path_of_Form_tears_the_course_down()
        {
            // Source pins on the paths the fakes above cannot reach (a create returning null, the goal failing to enter).
            var src = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/JumpEncounterType.cs");
            var form = PooledLootSourceText.MethodBody(src, "internal static void Form(ThreadEncounterInstance instance, JumpState state, int generation, Player user)");
            var couldNot = PooledLootSourceText.MethodBody(src, "private static void CouldNotForm(ThreadEncounterInstance instance, JumpState state, int generation, Player user, string why)");

            Assert.AreEqual(6, CountOf(form, "CouldNotForm(instance, state, generation, user,"), "no course, a pad not created, a pad not landed, no goal, the goal not entered, and the catch");
            StringAssert.Contains(couldNot, "DestroyCourse(state);");
            StringAssert.Contains(form, "EndCourse(instance, state, generation, TimeoutLine);");
        }

        private static int CountOf(string text, string token)
        {
            var n = 0;

            for (var i = text.IndexOf(token, StringComparison.Ordinal); i >= 0; i = text.IndexOf(token, i + token.Length, StringComparison.Ordinal))
                n++;

            return n;
        }
    }
}
