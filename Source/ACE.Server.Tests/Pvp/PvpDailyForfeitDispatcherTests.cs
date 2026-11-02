using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Pvp;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// The forfeit fact's delivery (PvpDailyForfeitDispatcher), at the gateway level: online it is queued on the player; offline or logging
    /// out it pends a GladiatorsStreak reset on the stored character, which the next login applies through the normal forfeit path. Scripted
    /// targets stand in for PlayerManager, the OfflinePlayer and the world queue. Pure; touches no PropertyManager key.
    /// </summary>
    [TestClass]
    public class PvpDailyForfeitDispatcherTests
    {
        private const uint Id = 0x50000002;

        private sealed class FakeTargets : IPvpDailyForfeitTargets
        {
            /// <summary>Online() for the character: null (offline or logging out) unless set.</summary>
            public bool Online;

            /// <summary>Whether an offline record exists to write to (false while a logout is still finishing).</summary>
            public bool HasOfflineRecord = true;

            /// <summary>The stored character's GladiatorsStreak state property.</summary>
            public string StoredStreakState;

            public readonly List<PvpMatchFacts> AppliedOnline = new();
            public int Pends;
            public readonly Queue<Action> Scheduled = new();

            public bool TryApplyOnline(PvpMatchFacts facts)
            {
                if (!Online)
                    return false;

                AppliedOnline.Add(facts);
                return true;
            }

            public bool TryPendOffline(uint characterId)
            {
                if (!HasOfflineRecord)
                    return false;

                StoredStreakState = PvpDailyProgress.PendingStreakResetMarker;
                Pends++;
                return true;
            }

            public void ScheduleOnWorld(double seconds, Action action) => Scheduled.Enqueue(action);

            public void RunScheduled()
            {
                while (Scheduled.Count > 0)
                    Scheduled.Dequeue()();
            }
        }

        /// <summary>A character as the login sees it: the streak quest held at 1, the stored state copied from the offline record.</summary>
        private sealed class LoginTarget : IPvpDailyTarget
        {
            public readonly Dictionary<string, int> Quests = new();
            public readonly Dictionary<PvpDailyStateSlot, string> State = new();

            public bool HasQuest(string questName) => Quests.ContainsKey(questName);

            public int GetSolves(string questName) => Quests.TryGetValue(questName, out var v) ? v : 0;

            public void SetSolves(string questName, int completions) => Quests[questName] = completions;

            public string GetState(PvpDailyStateSlot slot) => State.TryGetValue(slot, out var v) ? v : null;

            public void SetState(PvpDailyStateSlot slot, string value)
            {
                if (value == null)
                    State.Remove(slot);
                else
                    State[slot] = value;
            }

            public int GetStamp(PvpDailyStamp stamp) => 0;

            public void SetStamp(PvpDailyStamp stamp, int value) { }
        }

        private static readonly string Streak = PvpDailyCatalog.CounterNameOf(PvpDailyTask.GladiatorsStreak);

        /// <summary>What Player.ApplyPendingPvpStreakReset does at login, against a target.</summary>
        private static void Login(IPvpDailyTarget t)
        {
            PvpDailyProgress.ApplyPendingStreakReset(t, Id, f => PvpDailyProgress.Apply(t, f));
        }

        /// <summary>
        /// THE F1 CASE: Online() returns null (a logout forfeit). The forfeit fact is not queued anywhere live; the reset is pended on the
        /// stored character, and the next login takes the marker and ends the streak at 0, with the marker gone. Without the offline path
        /// nothing is stored and the streak survives at 1.
        /// </summary>
        [TestMethod]
        public void Offline_PendsTheReset_AndTheNextLoginEndsTheStreakAtZero()
        {
            var targets = new FakeTargets { Online = false };

            new PvpDailyForfeitDispatcher(targets).Dispatch(PvpMatchFacts.ForfeitOf(Guid.NewGuid(), Id, 1));

            Assert.AreEqual(0, targets.AppliedOnline.Count, "nothing reached a live player");
            Assert.AreEqual(1, targets.Pends, "the reset was pended once on the stored character");

            var login = new LoginTarget();
            login.Quests[Streak] = 1;
            login.State[PvpDailyStateSlot.GladiatorsStreak] = targets.StoredStreakState;

            Login(login);

            Assert.AreEqual(0, login.Quests[Streak], "the streak ends at 0");
            Assert.AreNotEqual(PvpDailyProgress.PendingStreakResetMarker, login.GetState(PvpDailyStateSlot.GladiatorsStreak), "the marker is consumed");

            Login(login);
            Assert.AreEqual(0, login.Quests[Streak], "a second login has nothing pending");
        }

        /// <summary>A pending reset at login does not undo a streak already completed (2 of 2), just as an online forfeit would not.</summary>
        [TestMethod]
        public void PendingReset_DoesNotUndoACompletedStreak()
        {
            var login = new LoginTarget();
            login.Quests[Streak] = 2;
            login.State[PvpDailyStateSlot.GladiatorsStreak] = PvpDailyProgress.PendingStreakResetMarker;

            Login(login);

            Assert.AreEqual(2, login.Quests[Streak]);
            Assert.IsNull(login.GetState(PvpDailyStateSlot.GladiatorsStreak), "the marker is still consumed");
        }

        /// <summary>Online and not logging out: queued on the player once, and nothing is pended offline.</summary>
        [TestMethod]
        public void Online_QueuesOnThePlayer_AndPendsNothing()
        {
            var targets = new FakeTargets { Online = true };

            new PvpDailyForfeitDispatcher(targets).Dispatch(PvpMatchFacts.ForfeitOf(Guid.NewGuid(), Id, 1));

            Assert.AreEqual(1, targets.AppliedOnline.Count);
            Assert.AreEqual(0, targets.Pends);
            Assert.AreEqual(0, targets.Scheduled.Count);
        }

        /// <summary>
        /// Logging out (no live queue, no offline record yet): retried on the world thread until the offline record exists, then pended
        /// exactly once. If they log straight back in instead, the retry applies it online.
        /// </summary>
        [TestMethod]
        public void MidLogout_RetriesUntilTheOfflineRecordExists_ThenPendsOnce()
        {
            var targets = new FakeTargets { Online = false, HasOfflineRecord = false };
            var dispatcher = new PvpDailyForfeitDispatcher(targets);

            dispatcher.Dispatch(PvpMatchFacts.ForfeitOf(Guid.NewGuid(), Id, 1));
            Assert.AreEqual(1, targets.Scheduled.Count, "a retry is scheduled");
            Assert.AreEqual(0, targets.Pends);

            targets.HasOfflineRecord = true;
            targets.RunScheduled();

            Assert.AreEqual(1, targets.Pends, "pended once the record exists");
            Assert.AreEqual(0, targets.Scheduled.Count, "and no further retry");

            var relog = new FakeTargets { Online = false, HasOfflineRecord = false };
            new PvpDailyForfeitDispatcher(relog).Dispatch(PvpMatchFacts.ForfeitOf(Guid.NewGuid(), Id, 1));
            relog.Online = true;
            relog.RunScheduled();

            Assert.AreEqual(1, relog.AppliedOnline.Count, "back online: applied on the player");
            Assert.AreEqual(0, relog.Pends);
        }

        /// <summary>No record ever: it gives up after MaxAttempts attempts, never pending or applying anything.</summary>
        [TestMethod]
        public void NeverAnyRecord_GivesUpAfterMaxAttempts()
        {
            var targets = new FakeTargets { Online = false, HasOfflineRecord = false };
            var attempts = 1;

            new PvpDailyForfeitDispatcher(targets).Dispatch(PvpMatchFacts.ForfeitOf(Guid.NewGuid(), Id, 1));

            while (targets.Scheduled.Count > 0)
            {
                targets.Scheduled.Dequeue()();
                attempts++;
            }

            Assert.AreEqual(PvpDailyForfeitDispatcher.MaxAttempts, attempts);
            Assert.AreEqual(0, targets.Pends + targets.AppliedOnline.Count);
        }

        /// <summary>Only a forfeit fact takes this path: ordinary facts are ignored by the dispatcher.</summary>
        [TestMethod]
        public void NonForfeitFacts_AreIgnored()
        {
            var targets = new FakeTargets { Online = true };
            var facts = new PvpMatchFacts(Guid.NewGuid(), Id, PvpDailyMode.OneVOne, 0, PvpBloodResult.Win, 1, 0, 0, 0, 0, 0x7u, 0);

            new PvpDailyForfeitDispatcher(targets).Dispatch(facts);

            Assert.AreEqual(0, targets.AppliedOnline.Count + targets.Pends);
        }
    }
}
