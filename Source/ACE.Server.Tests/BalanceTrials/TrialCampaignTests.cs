using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.BalanceTrials;
using ACE.Server.Managers.Analytics;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.BalanceTrials
{
    /// <summary>
    /// Campaigns (TrialCampaigns): the join and duplicate-join rules, the room-thread auto-next with its gaps, the deadline, the
    /// two breakers, the world-thread re-start and its cap, the close (logout through the seam, after the session ended), stop,
    /// and the report JSON. Every member session is a real TrialSession over a fake world, registered with TrialManager's test
    /// seam (no landblock: TrialManager.Enqueue answers false and every queued close runs inline). The room tick is
    /// TrialManager.TickSessionForTests (which calls TrialCampaigns.RoomTick); the world tick is TrialCampaigns.WorldTick.
    ///
    /// Static state: TrialManager, TrialRunLog and TrialCampaigns are reset in TestInitialize and TestCleanup. No PropertyManager
    /// key is read.
    /// </summary>
    [TestClass]
    public class TrialCampaignTests
    {
        private sealed class FakeWorld : ITrialWorld
        {
            public bool RoomLoaded { get; set; } = true;
            public int Foreign { get; set; }
            public bool PlayerOnline { get; set; } = true;
            public bool PlayerInRoom { get; set; } = true;
            public HashSet<uint> Dead = new HashSet<uint>();
            public List<uint> LastSpawned = new List<uint>();
            private static uint next = 0x7E000000;

            public int LiveForeignCreatures() => Foreign;
            public bool IsAlive(uint guid) => !Dead.Contains(guid);
            public void DestroyAll() { }
            public void ReturnPlayerToStart() { }

            public IReadOnlyList<uint> SpawnWave(IReadOnlyList<DungeonSpawnPlanEntry> wave, TrialRep rep)
            {
                LastSpawned = new List<uint> { next++, next++ };
                return LastSpawned;
            }
        }

        private sealed class Rep
        {
            public FakeWorld World = new FakeWorld();
            public TrialSession Session;
            public uint Instance;
            public int Starts;
        }

        private const string Scn = "camp-scn";
        private const int TimeoutSecs = 60;

        private static double T;
        private static uint nextInstance = 0x7FFE0000;
        private readonly Dictionary<uint, Rep> reps = new Dictionary<uint, Rep>();
        private readonly List<string> lines = new List<string>();
        private readonly List<string> order = new List<string>();
        private readonly List<AnalyticsDatabase.TrialRunRow> rows = new List<AnalyticsDatabase.TrialRunRow>();
        private readonly HashSet<uint> dead = new HashSet<uint>();
        private readonly HashSet<uint> loggingOut = new HashSet<uint>();
        private int hardCopyEvery;
        private bool refuseStarts;
        private readonly List<double> startAttempts = new List<double>();

        private static uint Guid(int slot) => TrialRoster.RepSlots[slot - 1].Guid;

        [TestInitialize]
        public void Init()
        {
            Reset();
            T = 1000;

            TrialManager.LandblockLookup = _ => null;
            TrialManager.OnlinePlayer = _ => null;

            TrialRunLog.EventSink = _ => { };
            TrialRunLog.RunSink = rows.Add;
            TrialRunLog.Defer = (_, write) => write();
            TrialRunLog.PlayerLevel = _ => 400;
            TrialRunLog.UtcNow = () => new DateTime(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);

            TrialCampaigns.Clock = () => T;
            TrialCampaigns.UtcNow = () => new DateTime(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);
            TrialCampaigns.Log = lines.Add;
            TrialCampaigns.PlayerAlive = g => !dead.Contains(g);
            TrialCampaigns.PlayerReady = g => reps.TryGetValue(g, out var r) && r.World.PlayerOnline && !dead.Contains(g) && !loggingOut.Contains(g);
            TrialCampaigns.HasLiveSession = g => TrialManager.TryGetByPlayer(g, out var s) && s.Phase != TrialPhase.Done && s.Phase != TrialPhase.Aborted;
            TrialCampaigns.StartMember = FakeStart;
            TrialCampaigns.LogOut = g => order.Add("logout " + TrialCampaigns.RepIndexOf(g));
        }

        [TestCleanup]
        public void Cleanup() => Reset();

        private static void Reset()
        {
            TrialCampaigns.ResetForTests();
            TrialManager.ResetForTests();
            TrialRunLog.ResetForTests();
        }

        /// <summary>The fake TryStart: the campaign precheck, a session built and configured the way TryStart does it, registered and Ready.</summary>
        private bool FakeStart(uint guid, string scenarioId, string archetypeId, TrialCampaignStart options, out string refusal)
        {
            var scenario = new TrialScenario(scenarioId, "r", null, 1, null, null, 3, TimeoutSecs, hardCopyEvery);

            startAttempts.Add(T);

            if (refuseStarts)
            {
                refusal = "the rep is not online";
                return false;
            }

            refusal = options.Precheck?.Invoke(scenario);
            if (refusal != null)
                return false;

            if (!reps.TryGetValue(guid, out var rep))
                reps[guid] = rep = new Rep();

            rep.World = new FakeWorld { PlayerOnline = rep.World.PlayerOnline };
            var world = rep.World;
            var session = new TrialSession("u" + guid.ToString("X8"), scenario, "sha", null, "asha", null, guid, world, _ => OneWaveRep());

            options.Configure(session);
            TrialRunLog.Attach(session);
            session.Ended += s => order.Add("ended " + TrialCampaigns.RepIndexOf(s.PlayerGuid));

            session.Tick(T);
            rep.Session = session;
            rep.Instance = nextInstance++;
            rep.Starts++;
            TrialManager.RegisterForTests(session, rep.Instance, seenLive: true);
            return true;
        }

        private static TrialRep OneWaveRep()
            => new TrialRep { Waves = new List<IReadOnlyList<DungeonSpawnPlanEntry>> { new List<DungeonSpawnPlanEntry>() } };

        private string JoinAs(int slot, string id = "c1", int minutes = 60, string scenario = Scn, string driver = "rynth-ui:1a2b3c4d")
            => TrialCampaigns.Join(Guid(slot), scenario, "archmage-400", minutes, id, driver);

        private void RoomTick(int slot) => TrialManager.TickSessionForTests(reps[Guid(slot)].Instance, T);

        private TrialSession S(int slot) => reps[Guid(slot)].Session;

        private TrialCampaignMember M(int slot, string id = "c1")
        {
            Assert.IsTrue(TrialCampaigns.TryGet(id, out var c));
            return c.Members.Single(m => m.PlayerGuid == Guid(slot));
        }

        private void Clear(int slot, int kills = 2)
        {
            var rep = reps[Guid(slot)];
            for (var i = 0; i < kills && i < rep.World.LastSpawned.Count; i++)
                S(slot).CurrentRep.Recorder?.OnKill(rep.World.LastSpawned[i], 1, DungeonRole.Trash, 10, 1, T);
            rep.World.Dead.UnionWith(rep.World.LastSpawned);
            S(slot).OnCreatureDied(0, T);
        }

        // ================================================================ auto-next

        [TestMethod]
        public void Auto_next_runs_after_a_clear_a_death_and_a_timeout_each_after_its_gap()
        {
            StringAssert.StartsWith(JoinAs(1), "trial started: scenario camp-scn, archetype archmage-400, campaign c1");
            Assert.AreEqual(TrialPhase.Ready, S(1).Phase);

            // The first rep waits the rep gap after the start.
            T += TrialCampaigns.RepGapSecs - 0.5;
            RoomTick(1);
            Assert.AreEqual(TrialPhase.Ready, S(1).Phase, "inside the gap nothing starts");
            T += 0.5;
            RoomTick(1);
            Assert.AreEqual(TrialPhase.Running, S(1).Phase, "the room tick starts rep 1 itself");

            // After a clear: the rep gap.
            T += 5;
            Clear(1);
            Assert.AreEqual(TrialPhase.Ready, S(1).Phase);
            Assert.AreEqual(TrialEventKinds.EndCleared, S(1).LastEndState);
            T += TrialCampaigns.RepGapSecs - 0.5;
            RoomTick(1);
            Assert.AreEqual(TrialPhase.Ready, S(1).Phase);
            T += 0.5;
            RoomTick(1);
            Assert.AreEqual(TrialPhase.Running, S(1).Phase, "rep 2 after the clear");

            // After a death: the death gap, and only once the rep is alive again.
            T += 5;
            dead.Add(Guid(1));
            S(1).OnPlayerDied(T);
            Assert.AreEqual(TrialEventKinds.EndDied, S(1).LastEndState);
            Assert.AreEqual(TrialPhase.Ready, S(1).Phase, "a death keeps today's branch: the session is Ready again");
            T += TrialCampaigns.RepGapSecs;
            RoomTick(1);
            Assert.AreEqual(TrialPhase.Ready, S(1).Phase, "the rep gap is not enough after a death");
            T += TrialCampaigns.DeathGapSecs - TrialCampaigns.RepGapSecs;
            RoomTick(1);
            Assert.AreEqual(TrialPhase.Ready, S(1).Phase, "a dead rep is not started");
            dead.Remove(Guid(1));
            RoomTick(1);
            Assert.AreEqual(TrialPhase.Running, S(1).Phase, "rep 3 once the rep is alive again, after the death gap");
            Assert.AreEqual(3, S(1).CurrentRep.Number);

            // A fresh member for the timeout leg (slot 2).
            JoinAs(2);
            T += TrialCampaigns.RepGapSecs;
            RoomTick(2);
            Assert.AreEqual(TrialPhase.Running, S(2).Phase);
            T += TimeoutSecs + 1;
            RoomTick(2);
            Assert.AreEqual(TrialEventKinds.EndTimeout, S(2).LastEndState, "the session's own tick times the rep out");
            Assert.AreEqual(TrialPhase.Ready, S(2).Phase);
            T += TrialCampaigns.RepGapSecs;
            RoomTick(2);
            Assert.AreEqual(TrialPhase.Running, S(2).Phase, "the next rep after a timeout");

            Assert.AreEqual(TrialScenarioLoader.MaxReps, S(2).Reps, "the reps cap is lifted to the scenario maximum");
        }

        [TestMethod]
        public void A_session_without_a_campaign_never_runs_a_rep_by_itself()
        {
            var world = new FakeWorld();
            var plain = new TrialSession("plain", new TrialScenario("s", "r", null, 1, null, null, 3, 60, 0), "sha", null, "asha", null, Guid(3), world, _ => OneWaveRep());
            TrialManager.RegisterForTests(plain, 0x7FFD0001, seenLive: true);

            for (var i = 0; i < 20; i++)
            {
                T += 5;
                TrialManager.TickSessionForTests(0x7FFD0001, T);
            }

            Assert.AreEqual(TrialPhase.Ready, plain.Phase, "only @trial next starts a plain session's rep");
            Assert.AreEqual(3, plain.Reps, "the plain session keeps the scenario's rep count");
            Assert.IsNull(plain.NoNewRepAfter);
            Assert.AreEqual("manual", TrialRunLog.Facts(plain, new TrialRep()).Driver, "a plain session's rows say manual");

            // Control: the same plain session runs a rep through TryNext with no deadline in the way.
            Assert.IsTrue(plain.TryNext(T + 1e6, out var refusal), refusal);
        }

        // ================================================================ deadline

        [TestMethod]
        public void The_rep_in_flight_at_the_deadline_finishes_and_no_rep_starts_after_it()
        {
            JoinAs(1, minutes: 1);
            Assert.IsTrue(TrialCampaigns.TryGet("c1", out var c));
            Assert.AreEqual(T + 60, c.DeadlineAt);

            // A rep starts just before the deadline.
            T = c.DeadlineAt - 2;
            RoomTick(1);
            Assert.AreEqual(TrialPhase.Running, S(1).Phase);

            // Past the deadline the rep runs on, and the campaign does not close while it is in flight.
            T = c.DeadlineAt + 5;
            RoomTick(1);
            Assert.AreEqual(TrialPhase.Running, S(1).Phase, "the in-flight rep is not cut off");
            TrialCampaigns.WorldTick(T);
            Assert.IsFalse(c.Closed, "no close while a rep is in flight");

            Clear(1);
            Assert.AreEqual(TrialEventKinds.EndCleared, S(1).LastEndState, "the in-flight rep ended normally");

            // No new rep, and the refusal is never even attempted (so it cannot feed the breaker).
            for (var i = 0; i < 10; i++)
            {
                T += 5;
                RoomTick(1);
            }

            Assert.AreEqual(TrialPhase.Ready, S(1).Phase, "no rep after the deadline");
            Assert.AreEqual(0, M(1).FailedAutoStartsInRow, "past the deadline the room tick does not try a rep");
            Assert.IsFalse(lines.Any(l => l.Contains("auto-start")), string.Join("\n", lines));

            // The session refuses one too (a typed @trial next).
            Assert.IsFalse(S(1).TryNext(T, out var refusal));
            Assert.AreEqual("the campaign deadline has passed", refusal);

            // The world tick then closes it.
            TrialCampaigns.WorldTick(T);
            Assert.IsTrue(c.Closed);
            Assert.AreEqual("deadline", c.StoppedBy);
        }

        [TestMethod]
        public void The_session_refuses_a_rep_at_its_deadline_even_when_asked_directly()
        {
            var world = new FakeWorld();
            var s = new TrialSession("d", new TrialScenario("s", "r", null, 1, null, null, 3, 60, 0), "sha", null, "asha", null, Guid(4), world, _ => OneWaveRep());
            s.NoNewRepAfter = 100;
            s.Tick(0);

            Assert.IsFalse(s.TryNext(100, out var refusal));
            Assert.AreEqual("the campaign deadline has passed", refusal);
            Assert.AreEqual(TrialPhase.Ready, s.Phase);

            Assert.IsTrue(s.TryNext(99.9, out refusal), "control: before the deadline it runs: " + refusal);
        }

        // ================================================================ join

        [TestMethod]
        public void A_duplicate_join_does_nothing_and_a_mismatched_scenario_is_refused()
        {
            JoinAs(1);
            Assert.IsTrue(TrialCampaigns.TryGet("c1", out var c));
            var deadline = c.DeadlineAt;
            var first = S(1);

            T += 30;
            Assert.AreEqual("campaign c1: already running", JoinAs(1, minutes: 240));
            Assert.AreEqual(1, reps[Guid(1)].Starts, "no second start");
            Assert.AreSame(first, S(1));
            Assert.AreEqual(deadline, c.DeadlineAt, "the deadline never moves");
            Assert.AreEqual(0, M(1).Resumes);
            Assert.AreEqual(1, c.Members.Count);

            // A later member must name the same scenario.
            StringAssert.StartsWith(JoinAs(2, scenario: "other"), "trial refused: campaign c1 runs scenario camp-scn");
            Assert.AreEqual(1, c.Members.Count);
            Assert.IsFalse(reps.ContainsKey(Guid(2)));

            // Control: with the same scenario it joins, and keeps the campaign's deadline.
            StringAssert.StartsWith(JoinAs(2, minutes: 5), "trial started:");
            Assert.AreEqual(2, c.Members.Count);
            Assert.AreEqual(deadline, S(2).NoNewRepAfter);
            Assert.AreEqual(60, c.Minutes);
        }

        [TestMethod]
        public void A_hard_copy_scenario_is_refused_in_campaign_mode_and_creates_nothing()
        {
            hardCopyEvery = 5;
            StringAssert.Contains(JoinAs(1), "hardCopyEvery 5");
            Assert.AreEqual(0, TrialCampaigns.Count, "a refused first start creates no campaign");

            Assert.IsNull(TrialCampaigns.ScenarioRefusal(new TrialScenario("s", "r", null, 1, null, null, 3, 60, 0)));
            Assert.IsNotNull(TrialCampaigns.ScenarioRefusal(new TrialScenario("s", "r", null, 1, null, null, 3, 60, 1)));

            hardCopyEvery = 0;
            StringAssert.StartsWith(JoinAs(1), "trial started:", "control");
        }

        [TestMethod]
        public void Run_rows_carry_the_driver_label()
        {
            JoinAs(1);
            T += TrialCampaigns.RepGapSecs;
            RoomTick(1);
            T += 4;
            Clear(1);

            Assert.AreEqual(1, rows.Count);
            Assert.AreEqual("rynth-ui:1a2b3c4d", rows[0].Driver);
            Assert.AreEqual(1, rows[0].ClientCount);

            JoinAs(2, driver: null);
            Assert.AreEqual("manual", TrialRunLog.Facts(S(2), new TrialRep()).Driver, "no label: manual");
        }

        // ================================================================ re-start

        [TestMethod]
        public void A_member_is_re_started_after_a_relog_up_to_the_resume_cap()
        {
            JoinAs(1);
            Assert.IsTrue(TrialCampaigns.TryGet("c1", out var c));
            var deadline = c.DeadlineAt;

            for (var resume = 1; resume <= TrialCampaigns.MaxResumes + 1; resume++)
            {
                // The rep relogs: the old Player reads offline, the manager sweep ends the session.
                reps[Guid(1)].World.PlayerOnline = false;
                RoomTick(1);
                Assert.AreEqual(TrialPhase.Aborted, S(1).Phase);
                Assert.IsFalse(TrialCampaigns.HasLiveSession(Guid(1)));

                // The new Player is online: offline or logging out, nothing; then the world tick re-starts it.
                var before = reps[Guid(1)].Starts;
                loggingOut.Add(Guid(1));
                reps[Guid(1)].World.PlayerOnline = true;
                T += TrialCampaigns.RestartScanSecs;
                TrialCampaigns.WorldTick(T);
                Assert.AreEqual(before, reps[Guid(1)].Starts, "a rep that is logging out is not re-started");
                loggingOut.Remove(Guid(1));

                T += TrialCampaigns.RestartScanSecs;
                TrialCampaigns.WorldTick(T);

                if (resume <= TrialCampaigns.MaxResumes)
                {
                    Assert.AreEqual(before + 1, reps[Guid(1)].Starts, $"resume {resume}");
                    Assert.AreEqual(resume, M(1).Resumes);
                    Assert.AreEqual(deadline, S(1).NoNewRepAfter, "a re-start never moves the deadline");
                    Assert.IsTrue(lines.Contains($"[TRIAL-CAMPAIGN] c1 slot=1: re-started, resume {resume}"), string.Join("\n", lines));
                }
                else
                {
                    Assert.AreEqual(before, reps[Guid(1)].Starts, "past the cap the world tick does not re-start");
                    Assert.AreEqual(TrialCampaigns.MaxResumes, M(1).Resumes);
                    Assert.AreEqual(TrialCampaigns.MaxResumes, M(1).TickResumes);
                }
            }

            // A re-join through the start line still re-starts a member whose session is not live (the cap is the world tick's).
            StringAssert.StartsWith(JoinAs(1), "campaign c1: re-started");
            Assert.AreEqual(TrialPhase.Ready, S(1).Phase);
            Assert.AreEqual("campaign c1: already running", JoinAs(1));
        }

        [TestMethod]
        public void No_re_start_after_the_deadline_or_after_the_close()
        {
            JoinAs(1, minutes: 1);
            Assert.IsTrue(TrialCampaigns.TryGet("c1", out var c));

            reps[Guid(1)].World.PlayerOnline = false;
            RoomTick(1);
            reps[Guid(1)].World.PlayerOnline = true;

            T = c.DeadlineAt + 1;
            TrialCampaigns.WorldTick(T);
            Assert.AreEqual(1, reps[Guid(1)].Starts, "no re-start once the deadline has passed");
            Assert.IsTrue(c.Closed, "deadline passed, nothing in flight: closed");

            T += TrialCampaigns.RestartScanSecs;
            TrialCampaigns.WorldTick(T);
            Assert.AreEqual(1, reps[Guid(1)].Starts);
            Assert.AreEqual("campaign c1 is closed; nothing started", JoinAs(1));
        }

        // ================================================================ breaker

        [TestMethod]
        public void Three_zero_kill_timeouts_in_a_row_trip_the_breaker_and_end_only_that_member()
        {
            JoinAs(1);
            JoinAs(2);

            void TimeOutRep(int slot, int kills)
            {
                T += TrialCampaigns.RepGapSecs;
                RoomTick(slot);
                Assert.AreEqual(TrialPhase.Running, S(slot).Phase);
                for (var i = 0; i < kills; i++)
                    S(slot).CurrentRep.Recorder.OnKill(reps[Guid(slot)].World.LastSpawned[0], 1, DungeonRole.Trash, 1, 0, T);
                T += TimeoutSecs + 1;
                RoomTick(slot);
                Assert.AreEqual(TrialEventKinds.EndTimeout, S(slot).LastEndState);
            }

            // Control: two zero-kill timeouts, one with a kill, two more: the run is broken, so no trip.
            TimeOutRep(2, 0);
            TimeOutRep(2, 0);
            TimeOutRep(2, 1);
            TimeOutRep(2, 0);
            TimeOutRep(2, 0);
            Assert.IsFalse(M(2).BreakerTripped, "a timeout with a kill breaks the run");

            TimeOutRep(1, 0);
            TimeOutRep(1, 0);
            Assert.IsFalse(M(1).BreakerTripped);
            TimeOutRep(1, 0);

            Assert.IsTrue(M(1).BreakerTripped);
            Assert.AreEqual(TrialPhase.Aborted, S(1).Phase, "the member's session ends");
            Assert.IsTrue(lines.Any(l => l == "[TRIAL-CAMPAIGN] c1 slot=1: breaker tripped (3 reps in a row timed out with no kills); the member's session ends and it is not re-started"), string.Join("\n", lines));
            Assert.IsTrue(TrialCampaigns.TryGet("c1", out var c));
            Assert.IsFalse(c.Closed, "the campaign runs on");
            Assert.AreNotEqual(TrialPhase.Aborted, S(2).Phase, "the other member runs on");

            // A tripped member is not re-started: not by the world tick, not by a relaunched client's start line.
            T += TrialCampaigns.RestartScanSecs;
            TrialCampaigns.WorldTick(T);
            Assert.AreEqual(1, reps[Guid(1)].Starts);
            Assert.AreEqual("campaign c1: this rep's breaker has tripped; nothing started", JoinAs(1));
            Assert.AreEqual(1, reps[Guid(1)].Starts);
            StringAssert.Contains(TrialCampaigns.Json(c), "\"breakerTripped\":true");
        }

        [TestMethod]
        public void Five_failed_auto_starts_in_a_row_trip_the_breaker()
        {
            JoinAs(1);
            reps[Guid(1)].World.Foreign = 1;

            for (var i = 1; i <= TrialCampaigns.BreakerFailedAutoStarts; i++)
            {
                Assert.IsFalse(M(1).BreakerTripped, $"not yet at failure {i - 1}");
                T += TrialCampaigns.RepGapSecs;
                RoomTick(1);
                Assert.AreEqual(i, M(1).FailedAutoStartsInRow);
            }

            Assert.IsTrue(M(1).BreakerTripped);
            Assert.AreEqual(TrialPhase.Aborted, S(1).Phase);
            Assert.IsTrue(lines.Contains("[TRIAL-CAMPAIGN] c1 slot=1: auto-start 1 of 5 in a row refused: room not empty"), string.Join("\n", lines));

            // Control: a success clears the count.
            JoinAs(2);
            reps[Guid(2)].World.Foreign = 1;
            for (var i = 0; i < 4; i++)
            {
                T += TrialCampaigns.RepGapSecs;
                RoomTick(2);
            }
            Assert.AreEqual(4, M(2).FailedAutoStartsInRow);
            reps[Guid(2)].World.Foreign = 0;
            T += TrialCampaigns.RepGapSecs;
            RoomTick(2);
            Assert.AreEqual(TrialPhase.Running, S(2).Phase);
            Assert.AreEqual(0, M(2).FailedAutoStartsInRow);
            Assert.IsFalse(M(2).BreakerTripped);
        }

        // ================================================================ close and stop

        [TestMethod]
        public void The_close_ends_each_session_before_logging_its_rep_out_and_writes_one_report()
        {
            JoinAs(1, minutes: 1);
            JoinAs(2, minutes: 1);
            Assert.IsTrue(TrialCampaigns.TryGet("c1", out var c));

            // Slot 2's rep relogs and is offline at the close: no session, still handed to the logout seam (which skips an offline rep).
            reps[Guid(2)].World.PlayerOnline = false;
            RoomTick(2);
            order.Clear();

            T = c.DeadlineAt;
            TrialCampaigns.WorldTick(T);

            Assert.IsTrue(c.Closed);
            CollectionAssert.AreEqual(new[] { "ended 1", "logout 1", "logout 2" }, order.ToArray(),
                "the session ends (its Ended fires the closing consumables reset) before the logout is requested");
            Assert.AreEqual(TrialPhase.Aborted, S(1).Phase);
            Assert.IsFalse(TrialManager.TryGetByPlayer(Guid(1), out _), "the session left the registry");
            Assert.IsTrue(lines.Contains("[TRIAL-CAMPAIGN] c1 closed: stoppedBy=deadline members=2 inFlightEnded=0"), string.Join("\n", lines));

            // The report: once, on the next world tick (nothing pending), never again.
            Assert.AreEqual(0, lines.Count(l => l.StartsWith("[TRIAL-CAMPAIGN] report ")));
            for (var i = 0; i < 5; i++)
            {
                T += 1;
                TrialCampaigns.WorldTick(T);
            }

            Assert.AreEqual(1, lines.Count(l => l.StartsWith("[TRIAL-CAMPAIGN] report ")));
            StringAssert.Contains(lines.Single(l => l.StartsWith("[TRIAL-CAMPAIGN] report ")), "\"closed\":true,\"stoppedBy\":\"deadline\"");
        }

        [TestMethod]
        public void Stop_ends_the_in_flight_reps_aborted_and_the_campaign_with_them()
        {
            JoinAs(1);
            JoinAs(2);
            T += TrialCampaigns.RepGapSecs;
            RoomTick(1);
            Assert.AreEqual(TrialPhase.Running, S(1).Phase);
            order.Clear();

            Assert.AreEqual("campaign c1 stopped: 1 in-flight reps ended aborted; its online reps are being logged out", TrialCampaigns.Stop("c1"));

            Assert.AreEqual(TrialEventKinds.EndForfeit, S(1).LastEndState, "the in-flight rep ends aborted (forfeit)");
            Assert.AreEqual(TrialPhase.Aborted, S(2).Phase);
            CollectionAssert.AreEqual(new[] { "ended 1", "logout 1", "ended 2", "logout 2" }, order.ToArray());
            Assert.AreEqual("campaign c1 is already closed", TrialCampaigns.Stop("c1"));
            Assert.AreEqual("unknown campaign 'nope'", TrialCampaigns.Stop("nope"));
            Assert.AreEqual("campaign c1 is closed; nothing started", JoinAs(3));

            TrialCampaigns.WorldTick(T);
            var report = lines.Single(l => l.StartsWith("[TRIAL-CAMPAIGN] report "));
            StringAssert.Contains(report, "\"stoppedBy\":\"stop\"");
            StringAssert.Contains(report, "\"repIndex\":1,\"reps\":1,\"cleared\":0,\"died\":0,\"timedOut\":0,\"aborted\":1");
        }

        [TestMethod]
        public void Status_lines_are_at_most_one_a_minute()
        {
            JoinAs(1);

            for (var i = 0; i < 240; i++)
            {
                T += 0.5;
                TrialCampaigns.WorldTick(T);
            }

            Assert.AreEqual(2, lines.Count(l => l.StartsWith("[TRIAL-CAMPAIGN] status {")), "120 s of half-second ticks: two status lines");
        }

        // ================================================================ JSON

        [TestMethod]
        public void Report_json_has_the_frozen_shape_and_no_guid_or_name()
        {
            JoinAs(2, id: "c202611241200-ab12", minutes: 60);
            JoinAs(1, id: "c202611241200-ab12", minutes: 60);

            // Rep 1 on slot 1: cleared with two kills and 1300 xp, 13 s after both joined. xpPerHour is over the member's wall time
            // (13 s, first start to now), not the rep's own 10 s.
            T += TrialCampaigns.RepGapSecs;
            RoomTick(1);
            T += 10;
            S(1).CurrentRep.Recorder.OnXpGranted(1300);
            Clear(1);

            Assert.IsTrue(TrialCampaigns.TryGet("c202611241200-ab12", out var c));
            var json = TrialCampaigns.Json(c);

            Assert.AreEqual(
                "{\"campaign\":\"c202611241200-ab12\",\"scenario\":\"camp-scn\",\"minutes\":60,\"startedUtc\":\"2026-11-24T12:00:00Z\",\"deadlineUtc\":\"2026-11-24T13:00:00Z\",\"closed\":false,\"stoppedBy\":null,\"members\":["
                + "{\"archetype\":\"archmage-400\",\"repIndex\":1,\"reps\":1,\"cleared\":1,\"died\":0,\"timedOut\":0,\"aborted\":0,\"killsTrash\":2,\"killsElite\":0,\"bosses\":0,\"deaths\":0,\"xp\":1300,\"lum\":0,\"engagedSecs\":0,\"wallSecs\":13,\"xpPerHour\":360000,\"lumPerHour\":0,\"xpPerEngagedHour\":null,\"lumPerEngagedHour\":null,\"driftReps\":0,\"driftUnknownReps\":1,\"resumes\":0,\"breakerTripped\":false},"
                + "{\"archetype\":\"archmage-400\",\"repIndex\":2,\"reps\":0,\"cleared\":0,\"died\":0,\"timedOut\":0,\"aborted\":0,\"killsTrash\":0,\"killsElite\":0,\"bosses\":0,\"deaths\":0,\"xp\":0,\"lum\":0,\"engagedSecs\":0,\"wallSecs\":13,\"xpPerHour\":0,\"lumPerHour\":0,\"xpPerEngagedHour\":null,\"lumPerEngagedHour\":null,\"driftReps\":0,\"driftUnknownReps\":0,\"resumes\":0,\"breakerTripped\":false}"
                + "]}",
                json);

            // No guid, no character name, no account name, on any line the campaign wrote.
            foreach (var text in lines.Append(json))
            {
                Assert.IsFalse(System.Text.RegularExpressions.Regex.IsMatch(text, "0x[0-9A-Fa-f]{8}"), text);
                foreach (var slot in TrialRoster.RepSlots)
                {
                    Assert.IsFalse(text.Contains(slot.Name, StringComparison.Ordinal), text);
                    Assert.IsFalse(text.Contains(slot.AccountName, StringComparison.Ordinal), text);
                    Assert.IsFalse(text.Contains(slot.Guid.ToString(), StringComparison.Ordinal), text);
                }
            }

            // The chat summary: every line under 200 characters.
            foreach (var report in new[] { false, true })
            {
                var summary = TrialCampaigns.SummaryLines(c, T, report);
                Assert.AreEqual(3, summary.Count);
                Assert.IsTrue(summary.All(l => l.Length < 200), string.Join("\n", summary));
            }

            Assert.AreEqual("campaign c202611241200-ab12: scenario camp-scn, open, 60 of 60 min left, 2 members", TrialCampaigns.SummaryLines(c, T, false)[0]);
        }

        [TestMethod]
        public void Scrub_masks_guids_and_trial_names()
        {
            Assert.AreEqual("buff profile 'x' needs the rep (0x????????) online", TrialCampaigns.Scrub("buff profile 'x' needs the rep (0x59000100) online"));
            Assert.AreEqual("<rep> and <master>", TrialCampaigns.Scrub("Trial Anvil and Trialmaster 3"));
            Assert.AreEqual("account <account> refused", TrialCampaigns.Scrub("account trial-07 refused"));
            Assert.AreEqual("room not empty", TrialCampaigns.Scrub("room not empty"));
        }

        // ================================================================ review fixes (F1-F5)

        [TestMethod]
        public void A_refused_re_start_spends_no_resume_and_backs_off_until_the_refusal_clears()
        {
            JoinAs(1);
            reps[Guid(1)].World.PlayerOnline = false;
            RoomTick(1);
            reps[Guid(1)].World.PlayerOnline = true;
            startAttempts.Clear();

            refuseStarts = true;
            for (var t = 1005; t <= 1200; t += 5)
            {
                T = t;
                TrialCampaigns.WorldTick(T);
            }

            CollectionAssert.AreEqual(new double[] { 1005, 1010, 1020, 1040, 1080, 1140, 1200 }, startAttempts.ToArray(),
                "refused re-starts back off 5, 10, 20, 40, then 60 s at most");
            Assert.AreEqual(0, M(1).Resumes, "a refused re-start spends no resume");
            Assert.AreEqual(0, M(1).TickResumes);

            // The refusal clears: the next try after the backoff re-starts it, once.
            refuseStarts = false;
            T = 1255;
            TrialCampaigns.WorldTick(T);
            Assert.AreEqual(1, reps[Guid(1)].Starts, "still inside the 60 s backoff");
            T = 1260;
            TrialCampaigns.WorldTick(T);
            Assert.AreEqual(2, reps[Guid(1)].Starts);
            Assert.AreEqual(1, M(1).Resumes);
            Assert.AreEqual(1, M(1).TickResumes);
            Assert.AreEqual(TrialPhase.Ready, S(1).Phase);
            T = 1265;
            TrialCampaigns.WorldTick(T);
            Assert.AreEqual(2, reps[Guid(1)].Starts, "a live session is not re-started again");
        }

        [TestMethod]
        public void A_start_line_re_join_does_not_count_toward_the_world_tick_cap()
        {
            JoinAs(1);

            for (var i = 1; i <= TrialCampaigns.MaxResumes; i++)
            {
                reps[Guid(1)].World.PlayerOnline = false;
                RoomTick(1);
                reps[Guid(1)].World.PlayerOnline = true;
                StringAssert.StartsWith(JoinAs(1), "campaign c1: re-started");
            }

            Assert.AreEqual(TrialCampaigns.MaxResumes, M(1).Resumes);
            Assert.AreEqual(0, M(1).TickResumes, "re-joins leave the world tick's cap untouched");

            // So the world tick can still resume it.
            reps[Guid(1)].World.PlayerOnline = false;
            RoomTick(1);
            reps[Guid(1)].World.PlayerOnline = true;
            T += TrialCampaigns.RestartScanSecs;
            TrialCampaigns.WorldTick(T);
            Assert.AreEqual(1, M(1).TickResumes);
        }

        [TestMethod]
        public void The_deadline_is_checked_against_a_fresh_clock_not_the_queued_tick_time()
        {
            JoinAs(1, minutes: 1);
            Assert.IsTrue(TrialCampaigns.TryGet("c1", out var c));

            // The tick was queued at 1003 (before the deadline) and runs on the room thread after it.
            var queuedAt = 1003.0;
            T = c.DeadlineAt + 1;
            TrialManager.TickSessionForTests(reps[Guid(1)].Instance, queuedAt);

            Assert.AreEqual(TrialPhase.Ready, S(1).Phase, "no rep: the fresh clock is past the deadline");
            Assert.AreEqual(0, M(1).FailedAutoStartsInRow, "and the room tick did not even try one");

            Assert.IsFalse(S(1).TryNext(queuedAt, out var refusal), "the session reads the fresh clock too");
            Assert.AreEqual("the campaign deadline has passed", refusal);

            // Control: with the fresh clock before the deadline, the same stale tick time starts the rep.
            T = queuedAt;
            TrialManager.TickSessionForTests(reps[Guid(1)].Instance, queuedAt);
            Assert.AreEqual(TrialPhase.Running, S(1).Phase);
        }

        [TestMethod]
        public void No_re_start_after_the_deadline_while_another_member_is_still_in_flight()
        {
            JoinAs(1, minutes: 1);
            JoinAs(2, minutes: 1);
            Assert.IsTrue(TrialCampaigns.TryGet("c1", out var c));

            T = c.DeadlineAt - 2;
            RoomTick(1);
            Assert.AreEqual(TrialPhase.Running, S(1).Phase);

            reps[Guid(2)].World.PlayerOnline = false;
            RoomTick(2);
            Assert.AreEqual(TrialPhase.Aborted, S(2).Phase);
            reps[Guid(2)].World.PlayerOnline = true;

            for (var i = 0; i < 8; i++)
            {
                T = c.DeadlineAt + 5 * (i + 1);
                TrialCampaigns.WorldTick(T);
            }

            Assert.IsFalse(c.Closed, "slot 1's rep is still in flight");
            Assert.AreEqual(1, reps[Guid(2)].Starts, "the relogged member is not re-started past the deadline");
            Assert.AreEqual(0, M(2).Resumes);
        }

        [TestMethod]
        public void The_report_drops_every_session_and_the_campaign_is_evicted_a_day_later()
        {
            JoinAs(1, minutes: 1);
            JoinAs(2, minutes: 1);
            Assert.IsTrue(TrialCampaigns.TryGet("c1", out var c));

            T = c.DeadlineAt;
            TrialCampaigns.WorldTick(T);
            Assert.IsTrue(c.Closed);
            T += 1;
            TrialCampaigns.WorldTick(T);
            Assert.IsTrue(c.ReportWritten);
            Assert.IsTrue(c.Members.All(m => m.Session == null), "the report drops every session reference");

            // Kept for a day for the verbs; then gone.
            T = c.ReportedAt + TrialCampaigns.EvictAfterSecs - 1;
            TrialCampaigns.WorldTick(T);
            Assert.AreEqual(1, TrialCampaigns.Count);
            Assert.AreEqual(3, TrialCampaigns.Describe("c1", report: true).Count);

            T = c.ReportedAt + TrialCampaigns.EvictAfterSecs;
            TrialCampaigns.WorldTick(T);
            Assert.AreEqual(0, TrialCampaigns.Count);
            CollectionAssert.AreEqual(new[] { "unknown campaign 'c1'" }, TrialCampaigns.Describe("c1", report: false).ToArray());
        }

        [TestMethod]
        public void The_production_player_ready_check_refuses_a_rep_that_is_logging_out()
        {
            TrialCampaigns.ResetForTests();
            var player = (ACE.Server.WorldObjects.Player)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ACE.Server.WorldObjects.Player));
            TrialManager.OnlinePlayer = _ => player;

            player.IsLoggingOut = true;
            Assert.IsFalse(TrialCampaigns.PlayerReady(Guid(1)), "logging out: not ready (and the guard is read before anything else)");

            TrialManager.OnlinePlayer = _ => null;
            Assert.IsFalse(TrialCampaigns.PlayerReady(Guid(1)), "offline: not ready");

            // Control: not logging out, the check goes on to read the rep's health (a bare Player has none, so the read throws):
            // the logout guard is what answered above, not something else.
            TrialManager.OnlinePlayer = _ => player;
            player.IsLoggingOut = false;
            var readHealth = false;
            try
            {
                TrialCampaigns.PlayerReady(Guid(1));
            }
            catch (Exception)
            {
                readHealth = true;
            }
            Assert.IsTrue(readHealth);
        }

        // ================================================================ wiring pins

        [TestMethod]
        public void The_manager_ticks_drive_the_campaigns()
        {
            const string ManagerPath = "Source/ACE.Server/BalanceTrials/TrialManager.cs";
            var tickAt = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(ManagerPath), "internal static void TickAt(double now)");
            var room = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(ManagerPath), "private static void TickSession(Entry entry, double now)");

            StringAssert.Contains(tickAt, "TrialCampaigns.WorldTick(now);");
            StringAssert.Contains(room, "TrialCampaigns.RoomTick(session, world, now);");
            Assert.IsTrue(room.IndexOf("session.Tick(now);", StringComparison.Ordinal) < room.IndexOf("TrialCampaigns.RoomTick(", StringComparison.Ordinal),
                "the auto-next runs after the session's own tick (which ends a timed-out rep first)");

            // The live logout: the client's own logoff path, queued on the rep's queue behind the closing reset, then the world thread.
            var logout = PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/BalanceTrials/TrialCampaign.cs"), "private static void LogOutOnline(uint guid)");
            StringAssert.Contains(logout, "TrialStarter.EnqueueOnPlayer(player, () => WorldManager.EnqueueAction(");
            StringAssert.Contains(logout, "player.Session?.LogOffPlayer();");
            Assert.IsFalse(logout.Contains("forceImmediate"), "never a forced logout");

            // TryStart: the campaign precheck before anything is created, the configure before anything subscribes.
            var start = PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/BalanceTrials/TrialStarter.cs"),
                "public static bool TryStart(Player player, string scenarioId, string archetypeId, out string refusal, TrialCampaignStart campaign = null)");
            var precheck = start.IndexOf("campaign?.Precheck?.Invoke(scenario)", StringComparison.Ordinal);
            var create = start.IndexOf("RealmManager.GetNewEphemeralLandblock(", StringComparison.Ordinal);
            var configure = start.IndexOf("campaign?.Configure?.Invoke(session);", StringComparison.Ordinal);
            var built = start.IndexOf("session = new TrialSession(", StringComparison.Ordinal);
            Assert.IsTrue(precheck > 0 && precheck < create, "the campaign refusal comes before the private copy");
            Assert.IsTrue(built > 0 && configure > built, "configured after the session is built");
            Assert.IsTrue(configure < start.IndexOf("session.RepEnded += (_, _) => repSetup.OnRepEnded(", StringComparison.Ordinal), "before the rep setup subscribes");
            Assert.IsTrue(configure < start.IndexOf("session.Ended += _ => ClosingReset(session, archetype, player);", StringComparison.Ordinal), "and before the closing reset subscribes");
            Assert.IsTrue(configure < start.IndexOf("TrialRunLog.Attach(session);", StringComparison.Ordinal), "and before the run log");
        }
    }
}
