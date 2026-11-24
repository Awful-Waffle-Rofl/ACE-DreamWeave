using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.BalanceTrials;
using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.BalanceTrials
{
    [TestClass]
    public class TrialSessionTests
    {
        private sealed class FakeWorld : ITrialWorld
        {
            public bool RoomLoaded { get; set; } = true;
            public int Foreign { get; set; }
            public bool PlayerOnline { get; set; } = true;
            public bool PlayerInRoom { get; set; } = true;
            public int DestroyCalls;
            public int ReturnCalls;
            public int SpawnCalls;
            public int PerWave = 2;
            public HashSet<uint> Dead = new HashSet<uint>();
            private uint _next = 100;
            public List<uint> LastSpawned = new List<uint>();

            public int LiveForeignCreatures() => Foreign;
            public bool IsAlive(uint guid) => !Dead.Contains(guid);
            public void DestroyAll() => DestroyCalls++;
            public void ReturnPlayerToStart() => ReturnCalls++;

            public IReadOnlyList<uint> SpawnWave(IReadOnlyList<DungeonSpawnPlanEntry> wave, TrialRep rep)
            {
                SpawnCalls++;
                LastSpawned = Enumerable.Range(0, PerWave).Select(_ => _next++).ToList();
                return LastSpawned;
            }
        }

        private static TrialScenario Scenario(int reps = 2, int timeout = 60)
            => new TrialScenario("s", "r", null, 1, null, null, reps, timeout, 0);

        private static TrialRep Rep(int waves = 2, IReadOnlyList<string> shortfalls = null, string refusal = null, bool rebuild = false)
            => new TrialRep
            {
                Waves = Enumerable.Range(0, waves).Select(_ => (IReadOnlyList<DungeonSpawnPlanEntry>)new List<DungeonSpawnPlanEntry>()).ToList(),
                Shortfalls = shortfalls ?? Array.Empty<string>(),
                Refusal = refusal,
                RebuildRequired = rebuild,
            };

        private static TrialSession Session(FakeWorld world, TrialScenario scenario = null, Func<int, TrialRep> prepare = null)
            => new TrialSession("u", scenario ?? Scenario(), "sha", null, "asha", null, 1u, world, prepare ?? (_ => Rep()));

        private static TrialSession Ready(FakeWorld world, TrialScenario scenario = null, Func<int, TrialRep> prepare = null)
        {
            var s = Session(world, scenario, prepare);
            s.Tick(0);
            Assert.AreEqual(TrialPhase.Ready, s.Phase);
            return s;
        }

        [TestMethod]
        public void Preparing_becomes_Ready_when_the_room_loads()
        {
            var w = new FakeWorld { RoomLoaded = false };
            var s = Session(w);
            s.Tick(0);
            Assert.AreEqual(TrialPhase.Preparing, s.Phase);
            w.RoomLoaded = true;
            s.Tick(1);
            Assert.AreEqual(TrialPhase.Ready, s.Phase);
        }

        [TestMethod]
        public void Next_refuses_a_room_a_generator_filled_after_Ready()
        {
            var w = new FakeWorld();
            var prepared = 0;
            var s = Ready(w, prepare: _ => { prepared++; return Rep(); });

            // A generator spawns its children after the landblock loads: the room was empty at Ready, not now.
            w.Foreign = 1;
            Assert.IsFalse(s.TryNext(1, out var refusal));
            Assert.AreEqual("room not empty", refusal);
            Assert.AreEqual(0, prepared, "nothing is prepared (no soft reset, no roll) in a room that is not empty");
            Assert.AreEqual(0, w.SpawnCalls);
            Assert.AreEqual(TrialPhase.Ready, s.Phase, "the session stays Ready; the operator clears the room and retries");
            Assert.AreEqual(0, s.RepsDone);

            // Control: the room cleared, the same session runs the rep.
            w.Foreign = 0;
            Assert.IsTrue(s.TryNext(2, out refusal), refusal);
            Assert.AreEqual(1, prepared);

            // And again between reps: rep 1 cleared, then a straggler appears before rep 2.
            foreach (var g in w.LastSpawned) w.Dead.Add(g);
            s.OnCreatureDied(0, 3);
            foreach (var g in w.LastSpawned) w.Dead.Add(g);
            s.OnCreatureDied(0, 4);
            Assert.AreEqual(TrialPhase.Ready, s.Phase);
            w.Foreign = 2;
            Assert.IsFalse(s.TryNext(5, out refusal));
            Assert.AreEqual("room not empty", refusal);
            Assert.AreEqual(1, prepared);
        }

        [TestMethod]
        public void A_room_with_foreign_creatures_aborts()
        {
            var w = new FakeWorld { Foreign = 3 };
            var s = Session(w);
            s.Tick(0);
            Assert.AreEqual(TrialPhase.Aborted, s.Phase);
            Assert.AreEqual("room not empty", s.LastRefusal);
        }

        [TestMethod]
        public void Next_refused_outside_Ready_when_rebuild_required_and_after_last_rep()
        {
            var w = new FakeWorld { RoomLoaded = false };
            var s = Session(w, Scenario(reps: 1));
            Assert.IsFalse(s.TryNext(0, out var r1));
            Assert.IsNotNull(r1);

            w.RoomLoaded = true;
            s.Tick(0);
            s.RebuildRequired = true;
            Assert.IsFalse(s.TryNext(0, out _));
            Assert.AreEqual(0, w.SpawnCalls);
            s.RebuildRequired = false;

            Assert.IsTrue(s.TryNext(0, out _));
            w.Dead.UnionWith(w.LastSpawned);
            s.OnCreatureDied(100, 1);
            w.Dead.UnionWith(w.LastSpawned);
            s.OnCreatureDied(100, 2);
            Assert.AreEqual(TrialPhase.Done, s.Phase);
            Assert.IsFalse(s.TryNext(3, out var r2));
            Assert.IsNotNull(r2);
        }

        [TestMethod]
        public void Waves_advance_on_clear_and_the_last_clear_ends_cleared()
        {
            var w = new FakeWorld();
            var s = Ready(w);
            Assert.IsTrue(s.TryNext(0, out _));
            Assert.AreEqual(1, w.SpawnCalls);

            w.Dead.Add(w.LastSpawned[0]);
            s.OnCreatureDied(w.LastSpawned[0], 1);
            Assert.AreEqual(1, w.SpawnCalls, "one of two still alive");

            w.Dead.Add(w.LastSpawned[1]);
            s.OnCreatureDied(w.LastSpawned[1], 2);
            Assert.AreEqual(2, w.SpawnCalls);
            Assert.AreEqual(1, s.CurrentRep.WaveIndex);
            Assert.AreEqual(TrialPhase.Running, s.Phase);

            w.Dead.UnionWith(w.LastSpawned);
            s.OnCreatureDied(w.LastSpawned[0], 3);
            Assert.AreEqual(TrialEventKinds.EndCleared, s.LastEndState);
            Assert.AreEqual(TrialPhase.Ready, s.Phase);
            Assert.AreEqual(1, s.RepsDone);
            Assert.AreEqual(1, w.ReturnCalls);
        }

        [TestMethod]
        public void Death_ends_died_without_ReturnPlayerToStart()
        {
            var w = new FakeWorld();
            var s = Ready(w);
            s.TryNext(0, out _);
            s.OnPlayerDied(5);
            Assert.AreEqual(TrialEventKinds.EndDied, s.LastEndState);
            Assert.AreEqual(0, w.ReturnCalls);
            Assert.AreEqual(TrialPhase.Ready, s.Phase);
        }

        [TestMethod]
        public void Timeout_ends_timeout()
        {
            var w = new FakeWorld();
            var s = Ready(w, Scenario(timeout: 60));
            s.TryNext(10, out _);
            s.Tick(70);
            Assert.AreEqual(TrialPhase.Running, s.Phase, "exactly at the limit is not past it");
            s.Tick(70.5);
            Assert.AreEqual(TrialEventKinds.EndTimeout, s.LastEndState);
            Assert.AreEqual(1, w.ReturnCalls);
            Assert.AreEqual(TrialPhase.Ready, s.Phase);
        }

        [TestMethod]
        public void Logout_or_leaving_the_room_forfeits_and_aborts()
        {
            foreach (var logout in new[] { true, false })
            {
                var w = new FakeWorld();
                var s = Ready(w);
                s.TryNext(0, out _);
                if (logout) w.PlayerOnline = false; else w.PlayerInRoom = false;
                s.Tick(1);
                Assert.AreEqual(TrialEventKinds.EndForfeit, s.LastEndState);
                Assert.AreEqual(TrialPhase.Aborted, s.Phase);
                Assert.AreEqual(0, w.ReturnCalls);
                Assert.AreEqual(1, w.DestroyCalls);
            }
        }

        [TestMethod]
        public void Every_end_destroys_tracked_creatures_once()
        {
            var w = new FakeWorld();
            var s = Ready(w, Scenario(reps: 3));
            s.TryNext(0, out _);
            s.OnPlayerDied(1);
            Assert.AreEqual(1, w.DestroyCalls);
            s.Tick(2);
            Assert.AreEqual(1, w.DestroyCalls, "a tick after the end does not destroy again");
            s.OnPlayerDied(3);
            Assert.AreEqual(1, w.DestroyCalls);

            s.TryNext(4, out _);
            s.Tick(1000);
            Assert.AreEqual(2, w.DestroyCalls);
        }

        [TestMethod]
        public void A_shortfall_aborts_the_rep_before_any_spawn()
        {
            var w = new FakeWorld();
            var ended = 0;
            var s = Ready(w, prepare: _ => Rep(shortfalls: new[] { "Boss: wanted 1, got 0" }));
            s.RepEnded += (_, _) => ended++;
            Assert.IsFalse(s.TryNext(0, out var refusal));
            Assert.AreEqual(0, w.SpawnCalls);
            Assert.AreEqual(TrialPhase.Ready, s.Phase);
            Assert.IsNull(s.LastEndState);
            StringAssert.Contains(refusal, "Boss: wanted 1, got 0");
            StringAssert.Contains(s.LastRefusal, "Boss: wanted 1, got 0");
            Assert.AreEqual(0, ended);
            Assert.AreEqual(0, s.RepsDone);
        }

        [TestMethod]
        public void A_prepared_rep_can_refuse_or_flag_a_rebuild_before_any_spawn()
        {
            var w = new FakeWorld();
            var calls = 0;
            var s = Ready(w, prepare: _ => ++calls == 1 ? Rep(refusal: "digest mismatch") : Rep(rebuild: true));
            Assert.IsFalse(s.TryNext(0, out var r));
            Assert.AreEqual("digest mismatch", r);
            Assert.AreEqual(TrialPhase.Ready, s.Phase);
            Assert.IsFalse(s.RebuildRequired);
            Assert.IsFalse(s.TryNext(0, out _));
            Assert.IsTrue(s.RebuildRequired);
            Assert.AreEqual(0, w.SpawnCalls);
        }

        [TestMethod]
        public void A_wave_that_spawns_nothing_never_stays_Running()
        {
            var w = new FakeWorld { PerWave = 0 };
            var s = Ready(w);
            Assert.IsFalse(s.TryNext(0, out var refusal));
            Assert.AreNotEqual(TrialPhase.Running, s.Phase);
            Assert.AreEqual(TrialEventKinds.EndForfeit, s.LastEndState);
            Assert.IsNotNull(refusal);
            Assert.AreEqual(refusal, s.LastRefusal);
            Assert.AreEqual(1, w.DestroyCalls);

            // And a later wave that spawns nothing.
            var w2 = new FakeWorld();
            var s2 = Ready(w2);
            s2.TryNext(0, out _);
            w2.PerWave = 0;
            w2.Dead.UnionWith(w2.LastSpawned);
            s2.OnCreatureDied(100, 1);
            Assert.AreNotEqual(TrialPhase.Running, s2.Phase);
            Assert.AreEqual(TrialEventKinds.EndForfeit, s2.LastEndState);
            Assert.IsNotNull(s2.LastRefusal);
        }

        [TestMethod]
        public void Abort_while_Running_ends_forfeit_once()
        {
            var w = new FakeWorld();
            var s = Ready(w);
            var states = new List<string>();
            s.RepEnded += (_, state) => states.Add(state);
            s.TryNext(0, out _);
            s.Abort("admin stop", 1);
            s.Abort("again", 2);
            CollectionAssert.AreEqual(new[] { TrialEventKinds.EndForfeit }, states);
            Assert.IsTrue(TrialEventKinds.AllEndStates.Contains(states[0]));
            Assert.AreEqual(TrialPhase.Aborted, s.Phase);
            Assert.AreEqual("admin stop", s.LastRefusal);
            Assert.AreEqual(1, w.DestroyCalls);
            Assert.AreEqual(1, w.ReturnCalls, "an admin abort returns the player");
        }

        [TestMethod]
        public void Abort_on_an_Aborted_or_Done_session_is_a_no_op()
        {
            var w = new FakeWorld { Foreign = 1 };
            var s = Session(w);
            s.Tick(0);
            Assert.AreEqual("room not empty", s.LastRefusal);
            s.Abort("late", 1);
            Assert.AreEqual("room not empty", s.LastRefusal);

            var w2 = new FakeWorld();
            var s2 = Ready(w2, Scenario(reps: 1));
            s2.TryNext(0, out _);
            s2.OnPlayerDied(1);
            Assert.AreEqual(TrialPhase.Done, s2.Phase);
            s2.Abort("late", 2);
            Assert.AreEqual(TrialPhase.Done, s2.Phase);
            Assert.AreNotEqual("late", s2.LastRefusal);
        }

        [TestMethod]
        public void A_null_rep_from_prepareRep_is_a_refusal()
        {
            var w = new FakeWorld();
            var s = Ready(w, prepare: _ => null);
            Assert.IsFalse(s.TryNext(0, out var r));
            Assert.IsNotNull(r);
            Assert.AreEqual(TrialPhase.Ready, s.Phase);
            Assert.AreEqual(0, w.SpawnCalls);
        }

        [TestMethod]
        public void RepEnded_fires_once_per_rep_with_its_end_state()
        {
            var w = new FakeWorld();
            var s = Ready(w, Scenario(reps: 2));
            var log = new List<(int Number, string State, int DestroyAtFire)>();
            s.RepEnded += (rep, state) => log.Add((rep.Number, state, w.DestroyCalls));

            s.TryNext(0, out _);
            s.OnPlayerDied(1);
            s.OnPlayerDied(2);
            s.TryNext(3, out _);
            s.Tick(9999);
            s.Tick(10000);

            Assert.AreEqual(2, log.Count);
            Assert.IsTrue(log.All(l => TrialEventKinds.AllEndStates.Contains(l.State)));
            Assert.AreEqual((1, TrialEventKinds.EndDied, 1), log[0]);
            Assert.AreEqual((2, TrialEventKinds.EndTimeout, 2), log[1]);
            Assert.AreEqual(TrialPhase.Done, s.Phase);
        }
    }
}
