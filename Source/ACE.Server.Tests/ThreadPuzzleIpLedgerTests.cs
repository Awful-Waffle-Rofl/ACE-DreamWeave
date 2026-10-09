using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Models.Shard;
using ACE.Server.ThreadDungeons;

namespace ACE.Server.Tests
{
    [TestClass]
    public class ThreadPuzzleIpLedgerTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);
        private static readonly TimeSpan Hour = TimeSpan.FromHours(1);
        private static readonly TimeSpan Retention = TimeSpan.FromHours(24);
        private static readonly TimeSpan FlushCap = TimeSpan.FromSeconds(10);

        /// <summary>
        /// In-memory stand-in for the shard table. LoadActive applies the same filter as the DAO's SELECT,
        /// so a second ledger loading from it models a restart. Each operation can be made to throw.
        /// </summary>
        private sealed class FakeStore : IThreadPuzzleIpLedgerStore
        {
            private readonly object gate = new object();
            public readonly List<ThreadPuzzleIpLedgerRow> Rows = new List<ThreadPuzzleIpLedgerRow>();

            public volatile bool FailWrites;
            public volatile bool FailLoad;
            public List<ThreadPuzzleIpLedgerRow> LoadOverride;

            public int Inserts;
            public int Loads;
            public int Prunes;
            public (DateTime now, TimeSpan window) LastLoadArgs;

            public void InsertFail(ThreadPuzzleIpLedgerRow row) => Insert(row);

            public void InsertLockout(ThreadPuzzleIpLedgerRow row) => Insert(row);

            private void Insert(ThreadPuzzleIpLedgerRow row)
            {
                Interlocked.Increment(ref Inserts);

                if (FailWrites)
                    throw new InvalidOperationException("Table 'ace_shard.thread_puzzle_ip_ledger' doesn't exist");

                lock (gate)
                    Rows.Add(row);
            }

            public List<ThreadPuzzleIpLedgerRow> LoadActive(DateTime nowUtc, TimeSpan failWindow)
            {
                Interlocked.Increment(ref Loads);
                LastLoadArgs = (nowUtc, failWindow);

                if (FailLoad)
                    throw new InvalidOperationException("Table 'ace_shard.thread_puzzle_ip_ledger' doesn't exist");

                if (LoadOverride != null)
                    return LoadOverride;

                lock (gate)
                {
                    return Rows.Where(r => (r.Kind == ThreadPuzzleIpLedgerKind.Fail && r.AtUtc > nowUtc - failWindow)
                                        || (r.Kind == ThreadPuzzleIpLedgerKind.Lockout && r.UntilUtc > nowUtc)).ToList();
                }
            }

            public int Clears;

            public int ClearFails(string key, DateTime atUtc)
            {
                Interlocked.Increment(ref Clears);

                if (FailWrites)
                    throw new InvalidOperationException("clear failed");

                lock (gate)
                    return Rows.RemoveAll(r => r.IpKey == key && r.Kind == ThreadPuzzleIpLedgerKind.Fail && r.AtUtc <= atUtc);
            }

            public int PruneExpired(DateTime nowUtc, TimeSpan failWindow)
            {
                Interlocked.Increment(ref Prunes);

                if (FailWrites)
                    throw new InvalidOperationException("prune failed");

                lock (gate)
                {
                    return Rows.RemoveAll(r => (r.Kind == ThreadPuzzleIpLedgerKind.Fail && r.AtUtc < nowUtc - failWindow)
                                            || (r.Kind == ThreadPuzzleIpLedgerKind.Lockout && r.UntilUtc <= nowUtc));
                }
            }
        }

        private sealed class LogSink
        {
            public readonly List<string> Errors = new List<string>();
            public readonly List<string> Infos = new List<string>();

            public void Error(string m) { lock (Errors) Errors.Add(m); }
            public void Info(string m) { lock (Infos) Infos.Add(m); }
        }

        private static ThreadPuzzleIpLedger NewLedger(FakeStore store, LogSink logs = null)
        {
            logs ??= new LogSink();
            return new ThreadPuzzleIpLedger(store, Retention, logs.Error, logs.Info);
        }

        /// <summary>
        /// A lockout consumes the fails that earned it, in memory AND in the table (one DELETE, queued after the
        /// lockout row), so a restart cannot resurrect them and a lockout shorter than the window cannot re-trigger.
        /// </summary>
        [TestMethod]
        public void Lockout_ClearsTheKeysFails_InMemoryAndPersisted()
        {
            var store = new FakeStore();
            var ledger = NewLedger(store);

            ledger.RecordFail("k", T0, Hour);
            ledger.RecordFail("k", T0.AddSeconds(1), Hour);
            ledger.RecordFail("other", T0.AddSeconds(1), Hour);

            var result = ledger.RecordFailUnlessLocked("k", T0.AddSeconds(2), Hour, fails => fails.Count >= 3 ? T0.AddMinutes(5) : (DateTime?)null);

            Assert.IsTrue(result.Recorded);
            Assert.AreEqual(3, result.Fails.Count, "the decision saw all three");
            Assert.AreEqual(T0.AddMinutes(5), result.LockoutApplied);
            Assert.AreEqual(0, ledger.GetFails("k", T0.AddSeconds(2), Hour).Count, "consumed in memory");
            Assert.AreEqual(1, ledger.GetFails("other", T0.AddSeconds(2), Hour).Count, "other keys untouched");

            Assert.IsTrue(ledger.Flush(FlushCap));
            Assert.AreEqual(1, store.Clears);
            Assert.AreEqual(0, store.Rows.Count(r => r.IpKey == "k" && r.Kind == ThreadPuzzleIpLedgerKind.Fail), "consumed in the table");
            Assert.AreEqual(1, store.Rows.Count(r => r.IpKey == "k" && r.Kind == ThreadPuzzleIpLedgerKind.Lockout));

            var restarted = NewLedger(store);
            restarted.Load(T0.AddMinutes(1));
            Assert.AreEqual(0, restarted.GetFails("k", T0.AddMinutes(1), Hour).Count, "a restart does not resurrect them");
            Assert.IsTrue(restarted.GetLockoutUntil("k", T0.AddMinutes(1)).HasValue);

            var locked = ledger.RecordFailUnlessLocked("k", T0.AddMinutes(1), Hour, _ => T0.AddHours(9));
            Assert.IsFalse(locked.Recorded, "nothing is recorded while locked");
            Assert.IsFalse(ledger.ApplyLockout("k", T0.AddMinutes(1), T0.AddMinutes(10)), "extending is not creating");
            Assert.IsTrue(ledger.ApplyLockout("fresh", T0, T0.AddMinutes(10)), "a key with no running lockout is newly locked");
        }
        [TestMethod]
        public void ConcurrentRecordFail_OnOneKey_LosesNothing()
        {
            const int threads = 16;
            const int perThread = 250;

            var store = new FakeStore();
            var ledger = NewLedger(store);
            var start = new Barrier(threads);
            var maxSeen = new int[threads];

            var tasks = Enumerable.Range(0, threads).Select(t => Task.Factory.StartNew(() =>
            {
                start.SignalAndWait();

                for (var i = 0; i < perThread; i++)
                {
                    var seen = ledger.RecordFail("203.0.113.7", T0.AddMilliseconds(i), Retention).Count;
                    maxSeen[t] = Math.Max(maxSeen[t], seen);
                }
            }, TaskCreationOptions.LongRunning)).ToArray();

            Task.WaitAll(tasks);

            Assert.AreEqual(threads * perThread, ledger.GetFails("203.0.113.7", T0.AddSeconds(1), Retention).Count,
                "every concurrent RecordFail must land in memory");
            Assert.AreEqual(threads * perThread, maxSeen.Max(),
                "the last RecordFail to run must see every fail, its own included");

            Assert.IsTrue(ledger.Flush(FlushCap));
            Assert.AreEqual(threads * perThread, store.Rows.Count, "every fail must be persisted exactly once");
        }

        [TestMethod]
        public void RecordFail_ReturnsOnlyFailsInsideTheCallersWindow_AndChecksNeverTouchTheStore()
        {
            var store = new FakeStore();
            var ledger = NewLedger(store);

            ledger.RecordFail("k", T0, Hour);
            ledger.RecordFail("k", T0.AddMinutes(30), Hour);
            var fails = ledger.RecordFail("k", T0.AddMinutes(61), Hour);

            CollectionAssert.AreEqual(new[] { T0.AddMinutes(30), T0.AddMinutes(61) }, fails.ToArray(),
                "the fail at T0 is outside the 60 min window ending at T0+61");

            Assert.IsTrue(ledger.Flush(FlushCap));
            var inserts = store.Inserts;
            var loads = store.Loads;

            Assert.AreEqual(3, ledger.GetFails("k", T0.AddMinutes(61), TimeSpan.FromHours(2)).Count);
            Assert.AreEqual(0, ledger.GetFails("other", T0, Hour).Count);
            Assert.IsNull(ledger.GetLockoutUntil("k", T0));

            Assert.AreEqual(inserts, store.Inserts, "a check must not write");
            Assert.AreEqual(loads, store.Loads, "a check must not read the store");
        }

        [TestMethod]
        public void Load_KeepsFailsInsideRetentionAndRunningLockouts_Only()
        {
            var now = T0;
            var store = new FakeStore
            {
                // Deliberately returns rows the SELECT would have excluded, so the ledger's own filter is under test.
                LoadOverride = new List<ThreadPuzzleIpLedgerRow>
                {
                    new ThreadPuzzleIpLedgerRow { IpKey = "a", Kind = ThreadPuzzleIpLedgerKind.Fail, AtUtc = now.AddMinutes(-10) },
                    new ThreadPuzzleIpLedgerRow { IpKey = "a", Kind = ThreadPuzzleIpLedgerKind.Fail, AtUtc = now - Retention - TimeSpan.FromSeconds(1) },
                    new ThreadPuzzleIpLedgerRow { IpKey = "b", Kind = ThreadPuzzleIpLedgerKind.Lockout, AtUtc = now.AddMinutes(-5), UntilUtc = now.AddMinutes(115) },
                    new ThreadPuzzleIpLedgerRow { IpKey = "b", Kind = ThreadPuzzleIpLedgerKind.Lockout, AtUtc = now.AddMinutes(-1), UntilUtc = now.AddMinutes(30) },
                    new ThreadPuzzleIpLedgerRow { IpKey = "c", Kind = ThreadPuzzleIpLedgerKind.Lockout, AtUtc = now.AddHours(-3), UntilUtc = now.AddMinutes(-1) },
                    new ThreadPuzzleIpLedgerRow { IpKey = "d", Kind = ThreadPuzzleIpLedgerKind.Lockout, AtUtc = now, UntilUtc = null },
                    new ThreadPuzzleIpLedgerRow { IpKey = "", Kind = ThreadPuzzleIpLedgerKind.Fail, AtUtc = now },
                }
            };

            var ledger = NewLedger(store);

            Assert.AreEqual(3, ledger.Load(now), "one live fail for a, two live lockouts for b");
            Assert.IsTrue(ledger.IsLoaded);
            Assert.AreEqual((now, Retention), store.LastLoadArgs, "Load reads the retention window");

            CollectionAssert.AreEqual(new[] { now.AddMinutes(-10) }, ledger.GetFails("a", now, Retention).ToArray());
            Assert.AreEqual(now.AddMinutes(115), ledger.GetLockoutUntil("b", now), "the later of two lockouts wins");
            Assert.IsNull(ledger.GetLockoutUntil("c", now), "an ended lockout is not loaded");
            Assert.IsNull(ledger.GetLockoutUntil("d", now), "a lockout row without an end is ignored");
            Assert.AreEqual(0, store.Inserts, "loading must not write anything back");
        }

        [TestMethod]
        public void StateSurvivesARestart_ThroughTheStore()
        {
            var store = new FakeStore();
            var first = NewLedger(store);

            first.RecordFail("198.51.100.0/64", T0.AddSeconds(0.4), Hour,
                new ThreadPuzzleAudit { AccountId = 7, CharacterId = 0x50000001, RunId = 0x8000_0001, RunStartGroup = new string('a', 40), IpAddress = "198.51.100.9" });
            first.RecordFail("198.51.100.0/64", T0.AddMinutes(1), Hour);
            // Another key: a lockout consumes its own key's fails (pinned in Lockout_ClearsTheKeysFails_InMemoryAndPersisted).
            first.ApplyLockout("203.0.113.0", T0.AddMinutes(1), T0.AddMinutes(121).AddMilliseconds(1));
            Assert.IsTrue(first.Flush(FlushCap));

            var audited = store.Rows.First();
            Assert.AreEqual(T0, audited.AtUtc, "at-times are floored to the second before they are stored");
            Assert.AreEqual(32, audited.RunStartGroup.Length, "audit strings are clipped to their column");
            Assert.AreEqual(7u, audited.AccountId);
            Assert.IsNull(audited.UntilUtc);

            var second = NewLedger(store);
            second.Load(T0.AddMinutes(2));

            CollectionAssert.AreEqual(new[] { T0, T0.AddMinutes(1) }, second.GetFails("198.51.100.0/64", T0.AddMinutes(2), Hour).ToArray());
            Assert.AreEqual(T0.AddMinutes(121).AddSeconds(1), second.GetLockoutUntil("203.0.113.0", T0.AddMinutes(2)),
                "lockout ends are rounded UP to the second, so a restart never shortens one");
            Assert.AreEqual(first.GetLockoutUntil("203.0.113.0", T0.AddMinutes(2)), second.GetLockoutUntil("203.0.113.0", T0.AddMinutes(2)));
        }

        [TestMethod]
        public void ApplyLockout_NeverShortensOne_AndExpires()
        {
            var ledger = NewLedger(new FakeStore());

            ledger.ApplyLockout("k", T0, T0.AddHours(2));
            ledger.ApplyLockout("k", T0, T0.AddMinutes(5));

            Assert.AreEqual(T0.AddHours(2), ledger.GetLockoutUntil("k", T0.AddHours(1)));
            Assert.IsNull(ledger.GetLockoutUntil("k", T0.AddHours(2)), "a lockout ending exactly now has ended");
        }

        [TestMethod]
        public void WriteFailure_KeepsMemoryState_AndLogsOnePerOutage()
        {
            var store = new FakeStore { FailWrites = true };
            var logs = new LogSink();
            var ledger = NewLedger(store, logs);

            for (var i = 0; i < 5; i++)
                ledger.RecordFail("k", T0.AddSeconds(i), Hour);

            ledger.ApplyLockout("k2", T0, T0.AddHours(2));

            Assert.IsTrue(ledger.Flush(FlushCap));

            Assert.AreEqual(5, ledger.GetFails("k", T0.AddSeconds(5), Hour).Count, "memory keeps every fail the store refused");
            Assert.AreEqual(T0.AddHours(2), ledger.GetLockoutUntil("k2", T0.AddSeconds(5)), "memory keeps the lockout the store refused");
            Assert.AreEqual(7, ledger.PersistFailureCount, "five fails, the lockout row and its fail clear");
            Assert.AreEqual(1, logs.Errors.Count, "one outage, one error");
            StringAssert.Contains(logs.Errors[0], "2026-10-06-00-Add-Thread-Puzzle-Ip-Ledger.sql");

            // Recovery is announced once, and a SECOND outage gets its own single error.
            store.FailWrites = false;
            ledger.RecordFail("k", T0.AddSeconds(6), Hour);
            Assert.IsTrue(ledger.Flush(FlushCap));
            Assert.AreEqual(1, logs.Infos.Count);

            store.FailWrites = true;
            ledger.RecordFail("k", T0.AddSeconds(7), Hour);
            ledger.RecordFail("k", T0.AddSeconds(8), Hour);
            Assert.IsTrue(ledger.Flush(FlushCap));
            Assert.AreEqual(2, logs.Errors.Count);
        }

        [TestMethod]
        public void MissingTableAtLoad_LogsOnce_ForTheLoadAndEveryLaterWrite()
        {
            var store = new FakeStore { FailLoad = true, FailWrites = true };
            var logs = new LogSink();
            var ledger = NewLedger(store, logs);

            Assert.AreEqual(0, ledger.Load(T0));
            Assert.IsFalse(ledger.IsLoaded);
            Assert.IsTrue(ledger.LoadFailed);

            ledger.RecordFail("k", T0, Hour);
            ledger.RecordFail("k", T0.AddSeconds(1), Hour);
            Assert.IsTrue(ledger.Flush(FlushCap));

            Assert.AreEqual(2, ledger.GetFails("k", T0.AddSeconds(1), Hour).Count, "the policy still works from memory");
            Assert.AreEqual(1, logs.Errors.Count);
        }

        [TestMethod]
        public void Sweep_DropsIdleKeys_KeepsLiveOnes_AndPrunesTheStore()
        {
            var store = new FakeStore();
            var ledger = NewLedger(store);

            ledger.RecordFail("old", T0, Hour);
            ledger.RecordFail("recent", T0 + Retention, Hour);
            ledger.ApplyLockout("locked", T0, T0 + Retention + Hour);
            Assert.IsTrue(ledger.Flush(FlushCap));

            var now = T0 + Retention + TimeSpan.FromMinutes(1);
            Assert.AreEqual(1, ledger.Sweep(now));
            Assert.AreEqual(2, ledger.KeyCount);
            Assert.IsTrue(ledger.Flush(FlushCap));

            Assert.AreEqual(1, store.Prunes);
            Assert.IsFalse(store.Rows.Any(r => r.IpKey == "old"), "the expired fail row is pruned from the store too");

            // A swept key is usable again.
            Assert.AreEqual(1, ledger.RecordFail("old", now, Hour).Count);
        }

        [TestMethod]
        public void MemoryOnlyLedger_WorksWithoutAStore()
        {
            var ledger = new ThreadPuzzleIpLedger(null, Retention);

            Assert.AreEqual(0, ledger.Load(T0));
            Assert.IsFalse(ledger.IsLoaded);
            Assert.AreEqual(1, ledger.RecordFail("acct:12345", T0, Hour).Count);
            Assert.IsTrue(ledger.Flush(FlushCap));
        }

        [TestMethod]
        public void RecordFail_RejectsKeysTheColumnCannotHold()
        {
            var ledger = new ThreadPuzzleIpLedger(null, Retention);

            Assert.ThrowsExactly<ArgumentException>(() => ledger.RecordFail("", T0, Hour));
            Assert.ThrowsExactly<ArgumentException>(() => ledger.RecordFail(new string('x', 46), T0, Hour));
        }
    }
}
