using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Models.Shard;
using ACE.Server.Avatars;
using ACE.Server.ThreadDungeons;

namespace ACE.Server.Tests.Avatars
{
    /// <summary>
    /// AvatarManager's daily claims: TryClaimDaily / CommitDaily / ReleaseDaily / DailyCount, through a fake store so the
    /// persisted half (the flushed row) and the memory-only half (pending claims lost on a restart) are both observed.
    /// Reads no PropertyManager value: the manager's tuning is a literal and every day clock is built from it.
    /// </summary>
    [TestClass]
    public class AvatarDailyClaimTests
    {
        private const uint AvatarId = 7;
        private const uint Monarch = 1000;
        private const double Start = 1_800_000_000.0;

        private static readonly AvatarTuning T = AvatarTuning.Defaults;

        // 2027-01-15 12:00Z = 7:00 AM EST. The Avatar day is Jan 15 Eastern, from 05:00Z to 05:00Z on Jan 16.
        private static readonly DateTime Now = new DateTime(2027, 1, 15, 12, 0, 0, DateTimeKind.Utc);

        private Store store;

        private static SurveyDayClock Day(DateTime utc) => AvatarDay.BuildClock(T, utc);

        [TestInitialize]
        public void Setup()
        {
            store = new Store();
            store.Avatars.Add(new AllegianceAvatar { AvatarId = AvatarId, MonarchGuid = Monarch, State = 0, Favor = 5000 });
            Boot();
        }

        [TestCleanup]
        public void Cleanup() =>
            AvatarManager.Initialize(new Store { LoadAllReturnsNull = true }, () => T, () => Start, () => new AccountActivityIndex(), () => new List<AvatarRoster>());

        private void Boot() =>
            AvatarManager.Initialize(store, () => T, () => Start, () => new AccountActivityIndex(), () => new List<AvatarRoster>());

        /// <summary>Forces a flush, then makes the store's rows the ones a restart loads.</summary>
        private AllegianceAvatar FlushAndPersist()
        {
            AvatarManager.OnShutdown();
            var row = store.Saved.LastOrDefault(a => a.AvatarId == AvatarId);
            Assert.IsNotNull(row, "the commit marked the Avatar dirty, so the flush carried it");
            store.Avatars.Clear();
            store.Avatars.Add(row);
            return row;
        }

        [TestMethod]
        public void Claim_Commit_FlushesTheStamp_AndAReloadIsRefused()
        {
            var day = Day(Now);

            Assert.IsTrue(AvatarManager.TryClaimDaily(AvatarId, AvatarDailyKind.Event, 1, day));
            Assert.AreEqual((0, 1), AvatarManager.DailyCount(AvatarId, AvatarDailyKind.Event, day), "held, not used yet");
            Assert.IsFalse(AvatarManager.TryClaimDaily(AvatarId, AvatarDailyKind.Event, 1, day), "a pending claim holds the only slot");

            var versionBefore = AvatarManager.Get(AvatarId).Version;
            AvatarManager.CommitDaily(AvatarId, AvatarDailyKind.Event, day);

            Assert.AreEqual((1, 0), AvatarManager.DailyCount(AvatarId, AvatarDailyKind.Event, day));
            Assert.AreEqual(versionBefore, AvatarManager.Get(AvatarId).Version, "a daily commit is a meter: it never cancels an open popup");

            var row = FlushAndPersist();
            Assert.AreEqual(Now, row.LastEventAt, "the flushed row carries the Active instant");
            Assert.AreEqual((byte)1, row.EventsOnDay, "and the day's count");
            Assert.IsNull(row.BlessingBoughtAt, "an event never stamps the Blessing");

            Boot();
            Assert.IsFalse(AvatarManager.TryClaimDaily(AvatarId, AvatarDailyKind.Event, 1, Day(Now.AddHours(3))), "the used slot survives a restart");
        }

        [TestMethod]
        public void Release_GivesTheSlotBack()
        {
            var day = Day(Now);

            Assert.IsTrue(AvatarManager.TryClaimDaily(AvatarId, AvatarDailyKind.Event, 1, day));
            AvatarManager.ReleaseDaily(AvatarId, AvatarDailyKind.Event);

            Assert.AreEqual((0, 0), AvatarManager.DailyCount(AvatarId, AvatarDailyKind.Event, day));
            Assert.IsTrue(AvatarManager.TryClaimDaily(AvatarId, AvatarDailyKind.Event, 1, day), "released, so free again");

            AvatarManager.ReleaseDaily(AvatarId, AvatarDailyKind.Event);
            AvatarManager.ReleaseDaily(AvatarId, AvatarDailyKind.Event);
            Assert.AreEqual((0, 0), AvatarManager.DailyCount(AvatarId, AvatarDailyKind.Event, day), "an extra release saturates at 0");
            Assert.IsTrue(AvatarManager.TryClaimDaily(AvatarId, AvatarDailyKind.Event, 1, day));
            Assert.IsFalse(AvatarManager.TryClaimDaily(AvatarId, AvatarDailyKind.Event, 1, day), "the saturated release left no credit behind");
        }

        [TestMethod]
        public void Restart_WithAPendingClaim_FreesIt()
        {
            var day = Day(Now);
            Assert.IsTrue(AvatarManager.TryClaimDaily(AvatarId, AvatarDailyKind.Event, 1, day));

            AvatarManager.OnShutdown();
            Assert.IsFalse(store.Saved.Any(a => a.AvatarId == AvatarId && a.LastEventAt != null), "a pending claim is never written");

            Boot();
            Assert.AreEqual((0, 0), AvatarManager.DailyCount(AvatarId, AvatarDailyKind.Event, day), "pending claims are memory only");
            Assert.IsTrue(AvatarManager.TryClaimDaily(AvatarId, AvatarDailyKind.Event, 1, day));
        }

        [TestMethod]
        public void NextEasternDay_IsFree_AndAStaleCountReadsAsZero()
        {
            var day = Day(Now);
            Assert.IsTrue(AvatarManager.TryClaimDaily(AvatarId, AvatarDailyKind.Event, 1, day));
            AvatarManager.CommitDaily(AvatarId, AvatarDailyKind.Event, day);

            Assert.IsFalse(AvatarManager.TryClaimDaily(AvatarId, AvatarDailyKind.Event, 1, Day(new DateTime(2027, 1, 16, 4, 59, 59, DateTimeKind.Utc))),
                "11:59:59 PM Eastern is still the same day");

            var tomorrow = Day(new DateTime(2027, 1, 16, 5, 0, 0, DateTimeKind.Utc));
            Assert.AreEqual((0, 0), AvatarManager.DailyCount(AvatarId, AvatarDailyKind.Event, tomorrow), "yesterday's count is not today's");
            Assert.IsTrue(AvatarManager.TryClaimDaily(AvatarId, AvatarDailyKind.Event, 1, tomorrow), "12:00 AM Eastern frees the slot");

            AvatarManager.CommitDaily(AvatarId, AvatarDailyKind.Event, tomorrow);
            Assert.AreEqual((byte)1, AvatarManager.Get(AvatarId).EventsOnDay, "a new day restarts the count at 1, not 2");
        }

        [TestMethod]
        public void StaleRowFromAnEarlierDay_DoesNotBlock()
        {
            store.Avatars[0].LastEventAt = Now.AddDays(-1);
            store.Avatars[0].EventsOnDay = 5;
            Boot();

            Assert.IsTrue(AvatarManager.TryClaimDaily(AvatarId, AvatarDailyKind.Event, 1, Day(Now)));
            AvatarManager.CommitDaily(AvatarId, AvatarDailyKind.Event, Day(Now));
            Assert.AreEqual((byte)1, AvatarManager.Get(AvatarId).EventsOnDay);
        }

        [TestMethod]
        public void FutureStamp_CountsAsUsed_AndIsKeptOnCommit()
        {
            var future = Now.AddDays(2);
            store.Avatars[0].LastEventAt = future;
            store.Avatars[0].EventsOnDay = 1;
            Boot();

            Assert.IsFalse(AvatarManager.TryClaimDaily(AvatarId, AvatarDailyKind.Event, 1, Day(Now)), "a stamp from the future never frees a slot");
            Assert.IsTrue(AvatarManager.TryClaimDaily(AvatarId, AvatarDailyKind.Event, 2, Day(Now)), "control: it is counted as one, not as all");

            AvatarManager.CommitDaily(AvatarId, AvatarDailyKind.Event, Day(Now));
            Assert.AreEqual(future, AvatarManager.Get(AvatarId).LastEventUtc, "the later stamp is kept, so it goes on counting");
            Assert.AreEqual((byte)2, AvatarManager.Get(AvatarId).EventsOnDay);
        }

        [TestMethod]
        public void LimitTwo_AllowsTwo_ThenRefuses()
        {
            var day = Day(Now);

            for (var i = 0; i < 2; i++)
            {
                Assert.IsTrue(AvatarManager.TryClaimDaily(AvatarId, AvatarDailyKind.Event, 2, day), $"claim {i + 1}");
                AvatarManager.CommitDaily(AvatarId, AvatarDailyKind.Event, day);
            }

            Assert.AreEqual((2, 0), AvatarManager.DailyCount(AvatarId, AvatarDailyKind.Event, day));
            Assert.IsFalse(AvatarManager.TryClaimDaily(AvatarId, AvatarDailyKind.Event, 2, day));
            Assert.AreEqual((byte)2, FlushAndPersist().EventsOnDay);
        }

        [TestMethod]
        public void LimitZero_IsUnlimited()
        {
            var day = Day(Now);

            for (var i = 0; i < 5; i++)
            {
                Assert.IsTrue(AvatarManager.TryClaimDaily(AvatarId, AvatarDailyKind.Event, 0, day), $"claim {i + 1}");
                AvatarManager.CommitDaily(AvatarId, AvatarDailyKind.Event, day);
            }

            Assert.AreEqual((5, 0), AvatarManager.DailyCount(AvatarId, AvatarDailyKind.Event, day), "still counted, for the status line");
        }

        [TestMethod]
        public void Blessing_IsAStampOnly_OncePerDay_AndIndependentOfEvents()
        {
            var day = Day(Now);

            Assert.IsTrue(AvatarManager.TryClaimDaily(AvatarId, AvatarDailyKind.Blessing, 1, day));
            Assert.IsFalse(AvatarManager.TryClaimDaily(AvatarId, AvatarDailyKind.Blessing, 5, day), "a Blessing limit is capped at 1");
            AvatarManager.CommitDaily(AvatarId, AvatarDailyKind.Blessing, day);

            Assert.IsTrue(AvatarManager.TryClaimDaily(AvatarId, AvatarDailyKind.Event, 1, day), "the Blessing never uses the event slot");

            var row = FlushAndPersist();
            Assert.AreEqual(Now, row.BlessingBoughtAt);
            Assert.IsNull(row.LastEventAt, "the event claim was only pending");

            Boot();
            Assert.IsFalse(AvatarManager.TryClaimDaily(AvatarId, AvatarDailyKind.Blessing, 1, Day(Now.AddHours(1))), "the stamp survives a restart");
            Assert.IsTrue(AvatarManager.TryClaimDaily(AvatarId, AvatarDailyKind.Blessing, 1, Day(Now.AddDays(1))), "and expires with the day");
        }

        [TestMethod]
        public void UnknownAvatar_OrDisabledManager_IsRefused()
        {
            Assert.IsFalse(AvatarManager.TryClaimDaily(999, AvatarDailyKind.Event, 1, Day(Now)));

            AvatarManager.Initialize(new Store { LoadAllReturnsNull = true }, () => T, () => Start, () => new AccountActivityIndex(), () => new List<AvatarRoster>());
            Assert.IsFalse(AvatarManager.TryClaimDaily(AvatarId, AvatarDailyKind.Event, 0, Day(Now)), "a failed load takes no claims");
        }

        /// <summary>
        /// Sixteen buyers released together against a limit of 3: exactly three are accepted. Runs 20 times inside the test
        /// (and the class should still be run alone repeatedly) because a race passes most of the time.
        /// </summary>
        [TestMethod]
        public void ParallelClaims_AcceptExactlyTheLimit()
        {
            const int Buyers = 16;
            const int Limit = 3;

            for (var round = 0; round < 20; round++)
            {
                Boot();
                var day = Day(Now);
                var accepted = 0;

                using (var barrier = new Barrier(Buyers))
                {
                    var threads = Enumerable.Range(0, Buyers).Select(_ => new Thread(() =>
                    {
                        barrier.SignalAndWait();
                        if (AvatarManager.TryClaimDaily(AvatarId, AvatarDailyKind.Event, Limit, day))
                            Interlocked.Increment(ref accepted);
                    })).ToList();

                    threads.ForEach(t => t.Start());
                    threads.ForEach(t => t.Join());
                }

                Assert.AreEqual(Limit, accepted, $"round {round}");
                Assert.AreEqual((0, Limit), AvatarManager.DailyCount(AvatarId, AvatarDailyKind.Event, day), $"round {round}");
            }
        }

        private sealed class Store : IAvatarStore
        {
            private readonly object sync = new object();

            public readonly List<AllegianceAvatar> Avatars = new List<AllegianceAvatar>();
            public readonly List<AllegianceAvatar> Saved = new List<AllegianceAvatar>();
            public bool LoadAllReturnsNull;

            public List<AllegianceAvatar> LoadAll() => LoadAllReturnsNull ? null : Avatars.Select(Copy).ToList();
            public List<AllegianceAvatarContribution> LoadContributionsSince(DateTime dayUtc) => new List<AllegianceAvatarContribution>();

            public void SaveBatch(IReadOnlyList<AllegianceAvatar> a, IReadOnlyList<AllegianceAvatarContribution> r, Action<bool> done)
            {
                lock (sync)
                    Saved.AddRange(a.Select(Copy));
                done(true);
            }

            public void Insert(AllegianceAvatar row, Action<bool, uint> done) => done(true, 0);

            private static AllegianceAvatar Copy(AllegianceAvatar e) => new AllegianceAvatar
            {
                AvatarId = e.AvatarId, MonarchGuid = e.MonarchGuid, State = e.State, Favor = e.Favor, LifetimeXp = e.LifetimeXp,
                LevelXp = e.LevelXp, BlessingBoughtAt = e.BlessingBoughtAt, LastEventAt = e.LastEventAt, EventsOnDay = e.EventsOnDay,
            };
        }
    }
}
