using System;

using ACE.Entity;
using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The Thread hold for a member pulled into a PvP match (Docs/Pvp/BATTLEGROUNDS.md "Pulled from a Thread"), as pure rules: the one
    /// idle-window table both holds share (ThreadDungeonRun.ResolveUnloadInterval and its single clamp), the reap's stand-down
    /// (ThreadDungeonManager.ShouldEnd), the hold-until arithmetic, and the away registry's by-reference validity (a re-issued
    /// instance id naming a different run object is never honoured).
    ///
    /// <para/>
    /// No PropertyManager key is read: the hold minutes arrive here already clamped (ThreadPvpAway.ClampHold), as the live gateway hands
    /// them over. The registry is process-static, so every test starts and ends with it empty.
    /// </summary>
    [TestClass]
    public class ThreadPvpHoldTests
    {
        private const uint Instance = 0x80001234u;
        private const uint Guid1 = 0x50000101u, Guid2 = 0x50000102u;

        private static readonly DateTime T0 = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);

        private static readonly Position Inside = new Position(0x01500100, 10f, 20f, 0f, 0f, 0f, 0f, 1f, Instance);

        [TestInitialize]
        public void Setup() => ThreadPvpAway.ClearForTests();

        [TestCleanup]
        public void Cleanup() => ThreadPvpAway.ClearForTests();

        private static ThreadDungeonRun NewRun(uint instance = Instance)
        {
            var spec = new DungeonGemSpec("filos_doom", 100, 6, "any", 1, new (string, double)[0], 0, 0);
            var dungeon = new DungeonEntryDef { Id = "filos_doom", Landblock = 0x0150, ExitPortalWcid = 1003601 };
            return new ThreadDungeonRun(instance, 0x50000001u, "Tester", 7, 0x80000099u, spec, dungeon, DateTime.UtcNow, TimeSpan.FromMinutes(180));
        }

        private static TimeSpan M(double minutes) => TimeSpan.FromMinutes(minutes);

        // ================= the one interval table =================

        /// <summary>
        /// ResolveUnloadInterval with both holds: the PvP hold replaces the window only when it is longer (or there is none), for every
        /// state but Ended, and every hold (loot or PvP) is raised to the one floor.
        /// </summary>
        [DataTestMethod]
        // state, grace, lootHeld, lootHold, pvpHeld, pvpHold, expected (-1 = null)
        [DataRow(ThreadDungeonRunState.Active, 10, false, 0, false, 0, 10, DisplayName = "Active, no hold: the grace")]
        [DataRow(ThreadDungeonRunState.Active, 10, false, 0, true, 30, 30, DisplayName = "Active, PvP hold longer than the grace: the hold")]
        [DataRow(ThreadDungeonRunState.Active, 60, false, 0, true, 30, 60, DisplayName = "Active, grace longer than the PvP hold: the grace")]
        [DataRow(ThreadDungeonRunState.Starting, 10, false, 0, true, 30, 30, DisplayName = "Starting, PvP hold: the hold")]
        [DataRow(ThreadDungeonRunState.Cleared, 10, false, 0, false, 0, -1, DisplayName = "Cleared, nothing held: the landblock default")]
        [DataRow(ThreadDungeonRunState.Cleared, 10, false, 0, true, 30, 30, DisplayName = "Cleared, PvP hold only: the hold")]
        [DataRow(ThreadDungeonRunState.Cleared, 10, true, 45, true, 30, 45, DisplayName = "Cleared, loot hold longer: the loot hold")]
        [DataRow(ThreadDungeonRunState.Cleared, 10, true, 20, true, 30, 30, DisplayName = "Cleared, PvP hold longer: the PvP hold")]
        [DataRow(ThreadDungeonRunState.Cleared, 10, false, 0, true, 1, 5, DisplayName = "a short PvP hold is raised to the floor")]
        [DataRow(ThreadDungeonRunState.Cleared, 10, false, 0, true, 0, -1, DisplayName = "PvP held with a zero hold: as if not held")]
        [DataRow(ThreadDungeonRunState.Ended, 10, true, 45, true, 30, -1, DisplayName = "Ended: never held")]
        public void ResolveUnloadInterval_FoldsThePvpHoldIn(ThreadDungeonRunState state, int grace, bool lootHeld, int lootHold, bool pvpHeld, int pvpHold, int expected)
        {
            Assert.AreEqual(5, (int)ThreadDungeonRun.EmptyWindowFloor.TotalMinutes, "fixture: the floor the table assumes");

            var window = ThreadDungeonRun.ResolveUnloadInterval(state, M(grace), lootHeld, M(lootHold), pvpHeld, M(pvpHold));

            if (expected < 0)
                Assert.IsNull(window);
            else
                Assert.AreEqual(M(expected), window);
        }

        /// <summary>The older forms are exactly the six-argument form with no PvP hold, for every state and loot hold.</summary>
        [TestMethod]
        public void TheShorterForms_AreTheSixArgumentFormWithoutAPvpHold()
        {
            foreach (ThreadDungeonRunState state in Enum.GetValues(typeof(ThreadDungeonRunState)))
            {
                foreach (var lootHeld in new[] { false, true })
                {
                    Assert.AreEqual(ThreadDungeonRun.ResolveUnloadInterval(state, M(10), lootHeld, M(20), false, TimeSpan.Zero),
                        ThreadDungeonRun.ResolveUnloadInterval(state, M(10), lootHeld, M(20)), $"{state} loot {lootHeld}");
                }

                Assert.AreEqual(ThreadDungeonRun.ResolveUnloadInterval(state, M(10), false, TimeSpan.Zero, false, TimeSpan.Zero),
                    ThreadDungeonRun.ResolveUnloadInterval(state, M(10)), state.ToString());
            }
        }

        /// <summary>The run's own window (the heartbeat override) reads the PvP hold the reap stored.</summary>
        [TestMethod]
        public void UnloadIntervalOverride_ReadsTheStoredPvpHold()
        {
            var run = NewRun();
            run.EmptyGrace = M(10);
            run.SetPvpHeld(M(30));

            Assert.IsTrue(run.IsPvpHeld);
            Assert.AreEqual(ThreadDungeonRun.ResolveUnloadInterval(run.State, M(10), false, TimeSpan.Zero, true, M(30)), run.UnloadIntervalOverride);

            run.SetPvpHeld(null);
            Assert.IsFalse(run.IsPvpHeld);
            Assert.AreEqual(ThreadDungeonRun.ResolveUnloadInterval(run.State, M(10)), run.UnloadIntervalOverride);
        }

        // ================= the reap stands down =================

        /// <summary>A cleared, empty copy is not reaped while a member is away in a PvP match; without the hold it is.</summary>
        [TestMethod]
        public void ShouldEnd_ClearedAndEmpty_StandsDownForAPvpHold()
        {
            var now = T0;

            Assert.IsTrue(ThreadDungeonManager.ShouldEnd(ThreadDungeonRunState.Cleared, true, true, true, false, false, false, M(10), M(5), now, now.AddHours(1), out var why));
            Assert.AreEqual(DungeonRunTelemetry.EndReasons.ClearedAndEmpty, why, "fixture: without the hold the copy is reaped");

            Assert.IsFalse(ThreadDungeonManager.ShouldEnd(ThreadDungeonRunState.Cleared, true, true, true, false, false, true, M(10), M(5), now, now.AddHours(1), out why));
            Assert.IsNull(why);
        }

        /// <summary>The PvP hold never outlives the run's own expiry, nor a copy that has unloaded.</summary>
        [TestMethod]
        public void ShouldEnd_APvpHold_DoesNotStopExpiryOrUnload()
        {
            var now = T0;

            Assert.IsTrue(ThreadDungeonManager.ShouldEnd(ThreadDungeonRunState.Active, true, true, true, false, false, true, M(10), M(0), now, now, out var why));
            Assert.AreEqual(DungeonRunTelemetry.EndReasons.Expired, why);

            Assert.IsTrue(ThreadDungeonManager.ShouldEnd(ThreadDungeonRunState.Cleared, false, true, true, false, false, true, M(10), M(5), now, now.AddHours(1), out why));
            Assert.AreEqual(DungeonRunTelemetry.EndReasons.LandblockUnloaded, why);
        }

        /// <summary>The eleven-argument form is the twelve with no PvP hold.</summary>
        [TestMethod]
        public void ShouldEnd_ElevenArgumentForm_IsTheTwelveWithoutAPvpHold()
        {
            var now = T0;

            foreach (var lootHeld in new[] { false, true })
            {
                var eleven = ThreadDungeonManager.ShouldEnd(ThreadDungeonRunState.Cleared, true, true, true, false, lootHeld, M(10), M(5), now, now.AddHours(1), out var a);
                var twelve = ThreadDungeonManager.ShouldEnd(ThreadDungeonRunState.Cleared, true, true, true, false, lootHeld, false, M(10), M(5), now, now.AddHours(1), out var b);

                Assert.AreEqual(twelve, eleven);
                Assert.AreEqual(b, a);
            }
        }

        // ================= hold-until arithmetic =================

        [DataTestMethod]
        [DataRow(0L, 5)]
        [DataRow(4L, 5)]
        [DataRow(30L, 30)]
        [DataRow(180L, 180)]
        [DataRow(999L, 180)]
        [DataRow(-10L, 5)]
        public void ClampHold_ClampsTo5Through180Minutes(long raw, int expected)
        {
            Assert.AreEqual(M(expected), ThreadPvpAway.ClampHold(raw));
        }

        /// <summary>Registering holds the copy from now for the window; participation ending shrinks it to the empty grace, never lengthens it.</summary>
        [TestMethod]
        public void HoldUntil_RegistersThenShrinksOnParticipationEnd()
        {
            var run = NewRun();
            run.EmptyGrace = M(10);

            ThreadPvpAway.Register(Guid1, run, Inside, T0, M(30));
            Assert.IsTrue(ThreadPvpAway.TryGet(Guid1, out var e));
            Assert.AreEqual(T0 + M(30), e.HoldUntilUtc);
            Assert.AreEqual(M(30), e.HoldWindow);

            ThreadPvpAway.OnParticipationEnded(Guid1, T0 + M(5));
            ThreadPvpAway.TryGet(Guid1, out e);
            Assert.AreEqual(T0 + M(15), e.HoldUntilUtc, "five minutes in, plus the ten-minute grace");

            ThreadPvpAway.OnParticipationEnded(Guid1, T0 + M(12));
            ThreadPvpAway.TryGet(Guid1, out e);
            Assert.AreEqual(T0 + M(15), e.HoldUntilUtc, "a later end never lengthens the hold");
        }

        [TestMethod]
        public void ShortenedHoldUntil_IsTheEarlierOfTheTwo()
        {
            Assert.AreEqual(T0 + M(15), ThreadPvpAway.ShortenedHoldUntil(T0 + M(30), T0 + M(5), M(10)));
            Assert.AreEqual(T0 + M(30), ThreadPvpAway.ShortenedHoldUntil(T0 + M(30), T0 + M(25), M(10)));
        }

        /// <summary>HeldWindow is the longest unexpired window among this run's entries; an expired hold no longer holds the copy.</summary>
        [TestMethod]
        public void HeldWindow_LongestUnexpired_ThenNone()
        {
            var run = NewRun();

            ThreadPvpAway.Register(Guid1, run, Inside, T0, M(30));
            ThreadPvpAway.Register(Guid2, run, Inside, T0, M(60));

            Assert.AreEqual(M(60), ThreadPvpAway.HeldWindow(run, T0 + M(1)));
            Assert.AreEqual(M(60), ThreadPvpAway.HeldWindow(run, T0 + M(45)), "the 30-minute entry has run out; the 60-minute one holds");
            Assert.IsNull(ThreadPvpAway.HeldWindow(run, T0 + M(60)), "every hold has run out");
        }

        // ================= validity is by run reference =================

        /// <summary>
        /// The instance id is re-issued to a NEW run object: the old entry neither holds the new run, nor counts as away from it, nor is
        /// released by it, and the prune drops it because the registered run is no longer the one it recorded.
        /// </summary>
        [TestMethod]
        public void AReissuedInstanceId_WithADifferentRun_IsRejected()
        {
            var left = NewRun();
            var reissued = NewRun();
            Assert.AreEqual(left.Instance, reissued.Instance, "fixture: the same instance id");

            ThreadPvpAway.Register(Guid1, left, Inside, T0, M(30));

            Assert.IsNull(ThreadPvpAway.HeldWindow(reissued, T0 + M(1)), "the new run is not held by the old entry");
            Assert.IsFalse(ThreadPvpAway.IsAwayFrom(reissued, Guid1));
            Assert.IsTrue(ThreadPvpAway.IsAwayFrom(left, Guid1));

            ThreadPvpAway.Release(Guid1, reissued);
            Assert.IsTrue(ThreadPvpAway.TryGet(Guid1, out _), "releasing by the new run leaves the old entry");

            ThreadPvpAway.Prune(_ => left);
            Assert.IsTrue(ThreadPvpAway.TryGet(Guid1, out _), "still the registered run: kept");

            ThreadPvpAway.Prune(_ => reissued);
            Assert.IsFalse(ThreadPvpAway.TryGet(Guid1, out _), "the registered run is a different object: pruned");
        }

        /// <summary>An ended run's entries are pruned; an entry whose hold merely ran out is not (the copy may be alive for other reasons).</summary>
        [TestMethod]
        public void Prune_DropsEndedRuns_KeepsExpiredHolds()
        {
            var run = NewRun();
            ThreadPvpAway.Register(Guid1, run, Inside, T0, M(5));

            Assert.IsNull(ThreadPvpAway.HeldWindow(run, T0 + M(10)), "fixture: the hold ran out");
            ThreadPvpAway.Prune(_ => run);
            Assert.IsTrue(ThreadPvpAway.TryGet(Guid1, out _), "an expired hold is not pruned");

            run.MarkEnded("test");
            ThreadPvpAway.Prune(_ => run);
            Assert.IsFalse(ThreadPvpAway.TryGet(Guid1, out _), "an ended run is pruned");
        }

        // ================= a new pull supersedes =================

        /// <summary>A new pull from a position that is not inside a Thread drops the stale entry and registers nothing.</summary>
        [TestMethod]
        public void BeginPull_FromANonThreadPosition_DropsTheOldEntry()
        {
            var run = NewRun();
            ThreadPvpAway.Register(Guid1, run, Inside, T0, M(30));

            var dropped = ThreadPvpAway.BeginPull(Guid1, null, new Position(0xA9B40019, 84f, 7f, 94f, 0f, 0f, 0f, 1f, 0), T0 + M(60), TimeSpan.Zero);

            Assert.AreSame(run, dropped?.Run, "the stale entry is handed back");
            Assert.IsFalse(ThreadPvpAway.TryGet(Guid1, out _), "nothing left for a later match to consume");
            Assert.IsNull(ThreadPvpAway.HeldWindow(run, T0 + M(61)), "the old run is no longer held");
        }

        /// <summary>A new pull from inside a Thread replaces the old entry with the new run and spot.</summary>
        [TestMethod]
        public void BeginPull_FromAThread_ReplacesTheEntry()
        {
            var old = NewRun();
            var fresh = NewRun(0x80001240u);
            ThreadPvpAway.Register(Guid1, old, Inside, T0, M(30));

            var spot = new Position(0x01600100, 5f, 6f, 0f, 0f, 0f, 0f, 1f, 0x80001240u);
            ThreadPvpAway.BeginPull(Guid1, fresh, spot, T0 + M(1), M(45));

            Assert.IsTrue(ThreadPvpAway.TryGet(Guid1, out var e));
            Assert.AreSame(fresh, e.Run);
            Assert.AreEqual(spot.Instance, e.Inside.Instance);
            Assert.AreEqual(T0 + M(46), e.HoldUntilUtc);
            Assert.IsFalse(ThreadPvpAway.IsAwayFrom(old, Guid1));
        }

        /// <summary>Only an online player who is out of the match copy and not teleporting has left without the resolver: released.</summary>
        [DataTestMethod]
        [DataRow(true, false, false, ThreadPvpAway.EndAction.Release, DisplayName = "online, out, not teleporting: a walk-out")]
        [DataRow(true, false, true, ThreadPvpAway.EndAction.Shrink, DisplayName = "still in the match: the return comes")]
        [DataRow(true, true, false, ThreadPvpAway.EndAction.Shrink, DisplayName = "teleporting: a return or death trip in progress")]
        [DataRow(false, false, false, ThreadPvpAway.EndAction.Shrink, DisplayName = "offline: the login relocation returns them")]
        public void ParticipationEndedAction_Table(bool online, bool teleporting, bool inMatch, ThreadPvpAway.EndAction expected)
        {
            Assert.AreEqual(expected, ThreadPvpAway.ParticipationEndedAction(online, teleporting, inMatch));
        }

        [TestMethod]
        public void ApplyParticipationEnded_ReleasesOrShrinks()
        {
            var run = NewRun();
            run.EmptyGrace = M(10);

            Assert.IsNull(ThreadPvpAway.ApplyParticipationEnded(Guid1, true, false, false, T0), "no entry: nothing to do");

            ThreadPvpAway.Register(Guid1, run, Inside, T0, M(30));
            Assert.AreEqual(ThreadPvpAway.EndAction.Shrink, ThreadPvpAway.ApplyParticipationEnded(Guid1, false, false, false, T0 + M(5)));
            Assert.IsTrue(ThreadPvpAway.TryGet(Guid1, out var e));
            Assert.AreEqual(T0 + M(15), e.HoldUntilUtc);

            Assert.AreEqual(ThreadPvpAway.EndAction.Release, ThreadPvpAway.ApplyParticipationEnded(Guid1, true, false, false, T0 + M(6)));
            Assert.IsFalse(ThreadPvpAway.TryGet(Guid1, out _));
        }

        /// <summary>The return rule: every one of the four conditions is needed.</summary>
        [DataTestMethod]
        [DataRow(true, false, true, true, true)]
        [DataRow(false, false, true, true, false)]
        [DataRow(true, true, true, true, false)]
        [DataRow(true, false, false, true, false)]
        [DataRow(true, false, true, false, false)]
        public void IsReturnValid_NeedsAllFour(bool sameRun, bool ended, bool loaded, bool accepts, bool expected)
        {
            Assert.AreEqual(expected, ThreadPvpAway.IsReturnValid(sameRun, ended, loaded, accepts));
        }
    }
}
