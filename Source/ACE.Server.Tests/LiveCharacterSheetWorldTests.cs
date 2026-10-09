using System;
using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Entity.Actions;
using ACE.Server.Managers.CharacterSheets;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The two pieces of LiveCharacterSheetWorld that need no live Player: the queued-work helper behind the
    /// online build (abandoned work is skipped) and the single in-flight rank build. The world queue is a
    /// list the test drains by hand, so nothing here depends on timing.
    /// </summary>
    [TestClass]
    public class LiveCharacterSheetWorldTests
    {
        private static readonly IReadOnlyDictionary<uint, List<SheetRank>> Empty = new Dictionary<uint, List<SheetRank>>();

        [TestMethod]
        public void RunQueued_TimedOut_TheActionSkipsTheWorkWhenItFinallyRuns()
        {
            IAction queued = null;
            var work = 0;

            var result = LiveCharacterSheetWorld.RunQueued<CharacterSheet>(a => queued = a, () => { work++; return new CharacterSheet(); }, TimeSpan.Zero, "test");

            Assert.IsNull(result);
            Assert.IsNotNull(queued);

            queued.Act();
            Assert.AreEqual(0, work, "a build its waiter abandoned must not project");
        }

        [TestMethod]
        public void RunQueued_RunsInTime_ReturnsTheWorkResult()
        {
            var sheet = new CharacterSheet();

            var result = LiveCharacterSheetWorld.RunQueued<CharacterSheet>(a => a.Act(), () => sheet, TimeSpan.FromSeconds(5), "test");

            Assert.AreSame(sheet, result);
        }

        [TestMethod]
        public void RunQueued_WorkThrows_ReturnsNull()
        {
            var result = LiveCharacterSheetWorld.RunQueued<CharacterSheet>(a => a.Act(), () => throw new InvalidOperationException("boom"), TimeSpan.FromSeconds(5), "test");

            Assert.IsNull(result);
        }

        [TestMethod]
        public void BuildRankSnapshot_WhileOneIsInFlight_DoesNotEnqueueAnother()
        {
            var queued = new List<IAction>();
            var computes = 0;
            var live = new LiveCharacterSheetWorld(a => queued.Add(a), () => { computes++; return Empty; });

            Assert.IsNull(live.BuildRankSnapshot(TimeSpan.Zero));
            Assert.IsNull(live.BuildRankSnapshot(TimeSpan.Zero));
            Assert.AreEqual(1, queued.Count, "the second waiter waits on the in-flight build instead of queueing another");

            queued[0].Act();
            Assert.AreEqual(1, computes);

            live.BuildRankSnapshot(TimeSpan.Zero);
            Assert.AreEqual(2, queued.Count, "once the in-flight build has completed, the next request queues a fresh one");
        }

        // ---- Offline loads: per-character single-flight, ceiling, 30s expiry (final review I1) --------

        private const uint G1 = 0x50000001;
        private static readonly TimeSpan OneMs = TimeSpan.FromMilliseconds(1);

        // the world's monotonic clock, in milliseconds; MSTest makes a fresh instance per test
        private long ms = 5_000_000;

        /// <summary>Records every load it is asked to start and never calls back on its own.</summary>
        private sealed class NeverCallsBackLoader
        {
            public readonly List<(uint guid, Action<CharacterSheet> callback)> Started = new List<(uint guid, Action<CharacterSheet> callback)>();

            public void Load(uint guid, Func<ACE.Server.WorldObjects.Player, CharacterSheet> project, Action<CharacterSheet> callback)
            {
                lock (Started)
                    Started.Add((guid, callback));
            }
        }

        private LiveCharacterSheetWorld OfflineWorld(NeverCallsBackLoader loader) =>
            new LiveCharacterSheetWorld(_ => { }, () => Empty, loader.Load, () => ms);

        [TestMethod]
        public void BuildOffline_TenTimedOutCallsForOneCharacter_StartExactlyOneLoad()
        {
            var loader = new NeverCallsBackLoader();
            var live = OfflineWorld(loader);

            for (var i = 0; i < 10; i++)
                Assert.IsNull(live.BuildOffline(G1, true, OneMs));

            Assert.AreEqual(1, loader.Started.Count, "repeat requests join the in-flight load instead of queueing another");
            Assert.AreEqual(1, live.OutstandingOfflineLoads);
        }

        [TestMethod]
        public void BuildOffline_TenDistinctCharacters_StartAtMostFourLoads()
        {
            var loader = new NeverCallsBackLoader();
            var live = OfflineWorld(loader);

            for (uint g = 1; g <= 10; g++)
                Assert.IsNull(live.BuildOffline(0x50000000 + g, true, OneMs));

            Assert.AreEqual(4, loader.Started.Count, "the outstanding-load ceiling refuses the rest without queueing");
            Assert.AreEqual(4, live.OutstandingOfflineLoads);
        }

        [TestMethod]
        public void BuildOffline_AfterTheCallbackFires_TheNextCallStartsAFreshLoad()
        {
            var loader = new NeverCallsBackLoader();
            var live = OfflineWorld(loader);

            live.BuildOffline(G1, true, OneMs);
            loader.Started[0].callback(new CharacterSheet());
            Assert.AreEqual(0, live.OutstandingOfflineLoads);

            live.BuildOffline(G1, true, OneMs);
            Assert.AreEqual(2, loader.Started.Count);
        }

        [TestMethod]
        public void BuildOffline_LoadCallsBackSynchronously_ReturnsItsSheet()
        {
            var sheet = new CharacterSheet();
            var live = new LiveCharacterSheetWorld(_ => { }, () => Empty, (g, p, cb) => cb(sheet), () => ms);

            Assert.AreSame(sheet, live.BuildOffline(G1, true, TimeSpan.FromSeconds(5)));
            Assert.AreEqual(0, live.OutstandingOfflineLoads);
        }

        [TestMethod]
        public void BuildOffline_EntryOlderThan30s_IsDead_AndALateCallbackIsNotCountedTwice()
        {
            var loader = new NeverCallsBackLoader();
            var live = OfflineWorld(loader);

            for (uint g = 1; g <= 4; g++)
                live.BuildOffline(0x50000000 + g, true, OneMs);
            Assert.AreEqual(4, loader.Started.Count);

            ms += 29_999;
            live.BuildOffline(G1, true, OneMs);           // still in flight: joins, starts nothing
            live.BuildOffline(0x50000005, true, OneMs);   // ceiling still full
            Assert.AreEqual(4, loader.Started.Count);

            ms += 1;                                      // 30s: all four are dead
            live.BuildOffline(0x50000005, true, OneMs);
            Assert.AreEqual(5, loader.Started.Count);
            Assert.AreEqual(1, live.OutstandingOfflineLoads);

            loader.Started[0].callback(new CharacterSheet());   // late callback from an expired load
            Assert.AreEqual(1, live.OutstandingOfflineLoads, "an expired load is decremented exactly once");

            live.BuildOffline(G1, true, OneMs);           // its dead entry no longer blocks a fresh load
            Assert.AreEqual(6, loader.Started.Count);
        }
    }
}
