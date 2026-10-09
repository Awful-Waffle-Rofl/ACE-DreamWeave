using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using log4net;

using ACE.Common.Extensions;
using ACE.Database;
using ACE.Database.Models.Shard;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>Audit-only context recorded beside a ledger row. Nothing reads it back for a decision.</summary>
    public readonly struct ThreadPuzzleAudit
    {
        public uint? AccountId { get; init; }
        public uint? CharacterId { get; init; }

        /// <summary>The run instance id (ThreadDungeonRun.RunId). Recycles across restarts.</summary>
        public uint? RunId { get; init; }

        /// <summary>ThreadDungeonRun.StartGroup: the per-gem-use GUID, stable across restarts.</summary>
        public string RunStartGroup { get; init; }

        /// <summary>The raw remote address, as opposed to the policy key derived from it.</summary>
        public string IpAddress { get; init; }
    }

    /// <summary>The thread_puzzle_ip_ledger persistence seam, so the ledger can be tested without MySQL. Every method may throw.</summary>
    public interface IThreadPuzzleIpLedgerStore
    {
        void InsertFail(ThreadPuzzleIpLedgerRow row);

        void InsertLockout(ThreadPuzzleIpLedgerRow row);

        /// <summary>Fail rows with at_Utc inside the window ending at nowUtc, and lockout rows with until_Utc after nowUtc.</summary>
        List<ThreadPuzzleIpLedgerRow> LoadActive(DateTime nowUtc, TimeSpan failWindow);

        /// <summary>Deletes fail rows older than the window and ended lockouts. Returns the count deleted.</summary>
        int PruneExpired(DateTime nowUtc, TimeSpan failWindow);

        /// <summary>Deletes the key's fail rows at or before <paramref name="atUtc"/> (a lockout consumed them). Returns the count deleted.</summary>
        int ClearFails(string key, DateTime atUtc);
    }

    /// <summary>What <see cref="ThreadPuzzleIpLedger.RecordFailUnlessLocked"/> did, decided under the key's lock.</summary>
    public readonly struct ThreadPuzzleLedgerFailResult
    {
        public ThreadPuzzleLedgerFailResult(DateTime? lockedUntilBefore, IReadOnlyList<DateTime> fails, DateTime? lockoutApplied)
        {
            LockedUntilBefore = lockedUntilBefore;
            Fails = fails ?? Array.Empty<DateTime>();
            LockoutApplied = lockoutApplied;
        }

        /// <summary>Set when the key was already locked out: nothing was recorded.</summary>
        public DateTime? LockedUntilBefore { get; }

        /// <summary>The key's fails inside the window, this one included, as they stood before any lockout cleared them.</summary>
        public IReadOnlyList<DateTime> Fails { get; }

        /// <summary>Set when this call created the lockout (rounded up to the second). Exactly one caller per lockout sees it.</summary>
        public DateTime? LockoutApplied { get; }

        public bool Recorded => !LockedUntilBefore.HasValue;
    }

    /// <summary>Production store: the shard DAO, reached through the base database so it bypasses SerializedShardDatabase.</summary>
    public sealed class ShardThreadPuzzleIpLedgerStore : IThreadPuzzleIpLedgerStore
    {
        public void InsertFail(ThreadPuzzleIpLedgerRow row)
            => DatabaseManager.Shard.BaseDatabase.InsertThreadPuzzleIpFail(row);

        public void InsertLockout(ThreadPuzzleIpLedgerRow row)
            => DatabaseManager.Shard.BaseDatabase.InsertThreadPuzzleIpLockout(row);

        public List<ThreadPuzzleIpLedgerRow> LoadActive(DateTime nowUtc, TimeSpan failWindow)
            => DatabaseManager.Shard.BaseDatabase.LoadActiveThreadPuzzleIpLedger(nowUtc, failWindow);

        public int PruneExpired(DateTime nowUtc, TimeSpan failWindow)
            => DatabaseManager.Shard.BaseDatabase.PruneExpiredThreadPuzzleIpLedger(nowUtc, failWindow);

        public int ClearFails(string key, DateTime atUtc)
            => DatabaseManager.Shard.BaseDatabase.ClearThreadPuzzleIpFails(key, atUtc);
    }

    /// <summary>
    /// Durable per-key store of Thread puzzle-gate fails and lockouts. STORAGE ONLY: it never decides
    /// whether a fail count trips a lockout, how long a lockout lasts, or how a key is derived from a
    /// session - the policy code owns all of that and passes its own window into every read.
    ///
    /// Memory is the source of truth while the server runs. <see cref="Load"/> fills it once at startup
    /// from the shard table; after that every check (<see cref="GetFails"/>, <see cref="GetLockoutUntil"/>)
    /// is answered from memory and never touches the database. Every write lands in memory first, under
    /// that key's own lock, and is then persisted on a single background chain (one write at a time, in
    /// the order they were recorded). A database failure - an outage, or the table not existing because
    /// the migration has not been applied - leaves the in-memory policy fully working; it is logged as
    /// ONE error, and further failures are only counted until a write succeeds again.
    ///
    /// Timestamps are truncated to whole seconds before they are stored (the column is datetime), so the
    /// state rebuilt after a restart matches what was in memory: at-times round down, lockout ends round
    /// up so a lockout is never shorter than asked.
    ///
    /// <see cref="Retention"/> bounds both what <see cref="Load"/> reads and how long a fail is kept. A
    /// read window longer than it behaves as <see cref="Retention"/>, so the policy's fail window must not
    /// exceed it.
    /// </summary>
    public sealed class ThreadPuzzleIpLedger
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>How long fail rows are kept in memory and in the table. Must be at least the policy's fail window.</summary>
        public static readonly TimeSpan DefaultRetention = TimeSpan.FromHours(24);

        /// <summary>
        /// The server's ledger. Until <see cref="Initialize"/> replaces it this is a memory-only, never-loaded
        /// instance, so a caller that runs without a database (a unit test) still gets working storage.
        /// </summary>
        public static ThreadPuzzleIpLedger Shared { get; private set; } = new ThreadPuzzleIpLedger(null, DefaultRetention);

        /// <summary>
        /// Startup: builds the shard-backed ledger, loads it SYNCHRONOUSLY, prunes the table in the
        /// background, and publishes it as <see cref="Shared"/>. Program.Main calls this before
        /// SocketManager.Initialize, so the load has finished before any client can connect, let alone use
        /// a gem. A load failure is logged and the server starts with empty storage.
        /// </summary>
        public static void Initialize()
        {
            var nowUtc = DateTime.UtcNow;
            var ledger = new ThreadPuzzleIpLedger(new ShardThreadPuzzleIpLedgerStore(), DefaultRetention);

            var loaded = ledger.Load(nowUtc);

            if (ledger.IsLoaded)
            {
                log.Info($"[PUZZLEIP] Loaded {loaded} active fail/lockout row(s) across {ledger.KeyCount} key(s); retention {ledger.Retention.TotalHours:0.#} h.");
                ledger.PruneInBackground(nowUtc);
            }

            Shared = ledger;
        }

        /// <summary>Shutdown: waits up to <paramref name="cap"/> for queued writes. Never throws.</summary>
        public static void Shutdown(TimeSpan cap)
        {
            try
            {
                if (!Shared.Flush(cap))
                    log.Warn($"[PUZZLEIP] Pending ledger writes did not finish within {cap.TotalSeconds:0.#} s of shutdown; they are lost.");
            }
            catch (Exception ex)
            {
                log.Error($"[PUZZLEIP] Flushing the ledger at shutdown failed: {ex.GetFullMessage()}");
            }
        }

        private sealed class Entry
        {
            public readonly object Gate = new object();
            public readonly List<DateTime> Fails = new List<DateTime>();
            public DateTime? LockoutUntil;

            /// <summary>Set under Gate when Sweep drops this entry; a writer that sees it retries with a fresh entry.</summary>
            public bool Removed;
        }

        private readonly ConcurrentDictionary<string, Entry> entries = new ConcurrentDictionary<string, Entry>(StringComparer.Ordinal);

        private readonly IThreadPuzzleIpLedgerStore store;
        private readonly Action<string> errorLog;
        private readonly Action<string> infoLog;

        private readonly object chainLock = new object();
        private Task chain = Task.CompletedTask;

        private int failing;
        private int persistFailureCount;
        private volatile bool isLoaded;
        private volatile bool loadFailed;

        /// <param name="store">The table. NULL makes a memory-only ledger: nothing is loaded or persisted.</param>
        /// <param name="retention">How long fails are kept, and the window <see cref="Load"/> reads. Must be positive.</param>
        /// <param name="errorLog">Error sink; defaults to log4net ERROR. Injectable so tests can count calls.</param>
        /// <param name="infoLog">Info sink; defaults to log4net INFO.</param>
        public ThreadPuzzleIpLedger(IThreadPuzzleIpLedgerStore store, TimeSpan retention, Action<string> errorLog = null, Action<string> infoLog = null)
        {
            if (retention <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(retention), "Retention must be positive.");

            this.store = store;
            Retention = retention;
            this.errorLog = errorLog ?? (m => log.Error(m));
            this.infoLog = infoLog ?? (m => log.Info(m));
        }

        public TimeSpan Retention { get; }

        /// <summary>True once <see cref="Load"/> has read the table successfully.</summary>
        public bool IsLoaded => isLoaded;

        /// <summary>True when <see cref="Load"/> ran and the read failed (storage started empty).</summary>
        public bool LoadFailed => loadFailed;

        /// <summary>Background writes that have failed since construction. Diagnostic.</summary>
        public int PersistFailureCount => Volatile.Read(ref persistFailureCount);

        /// <summary>Keys currently held in memory. Diagnostic.</summary>
        public int KeyCount => entries.Count;

        /// <summary>
        /// Reads every row that can still matter (fails inside <see cref="Retention"/>, lockouts not yet
        /// ended) into memory. Call ONCE, before anything is recorded: a second call adds the same fail rows
        /// again. Synchronous: the caller decides when it runs. Returns the number of rows read; on failure logs one error and returns 0. A memory-only
        /// ledger returns 0 and stays not-loaded.
        /// </summary>
        public int Load(DateTime nowUtc)
        {
            if (store == null)
                return 0;

            List<ThreadPuzzleIpLedgerRow> rows;

            try
            {
                rows = store.LoadActive(nowUtc, Retention);
            }
            catch (Exception ex)
            {
                loadFailed = true;
                ReportFailure("load", ex);
                return 0;
            }

            var count = 0;

            foreach (var row in rows ?? new List<ThreadPuzzleIpLedgerRow>())
            {
                if (string.IsNullOrEmpty(row?.IpKey))
                    continue;

                if (row.Kind == ThreadPuzzleIpLedgerKind.Fail)
                {
                    var at = AsUtc(row.AtUtc);

                    if (at <= nowUtc - Retention)
                        continue;

                    WithEntry(row.IpKey, e => { e.Fails.Add(at); return 0; });
                    count++;
                }
                else if (row.Kind == ThreadPuzzleIpLedgerKind.Lockout && row.UntilUtc.HasValue)
                {
                    var until = AsUtc(row.UntilUtc.Value);

                    if (until <= nowUtc)
                        continue;

                    WithEntry(row.IpKey, e => { e.LockoutUntil = Max(e.LockoutUntil, until); return 0; });
                    count++;
                }
            }

            isLoaded = true;
            loadFailed = false;
            return count;
        }

        /// <summary>
        /// Records one fail for <paramref name="key"/> at <paramref name="nowUtc"/> (truncated to the
        /// second) and returns, oldest first, every fail for the key inside <paramref name="window"/>,
        /// this one included. Atomic per key: concurrent calls on one key each see their own fail and
        /// none is lost. Persisted in the background.
        /// </summary>
        public IReadOnlyList<DateTime> RecordFail(string key, DateTime nowUtc, TimeSpan window, ThreadPuzzleAudit audit = default)
        {
            RequireKey(key);

            var at = FloorToSecond(nowUtc);

            var result = WithEntry(key, e =>
            {
                Trim(e, nowUtc);
                e.Fails.Add(at);
                return InWindow(e, nowUtc, window);
            });

            Persist(s => s.InsertFail(ToRow(key, ThreadPuzzleIpLedgerKind.Fail, at, null, audit)));

            return result;
        }

        /// <summary>
        /// Locks <paramref name="key"/> out until <paramref name="untilUtc"/> (rounded up to the second).
        /// An existing later lockout is kept: this never shortens one. The key's fails at or before
        /// <paramref name="nowUtc"/> are cleared (a lockout consumes them, so a lockout shorter than the fail window
        /// cannot re-trigger the moment it ends), in memory and in the table. Persisted in the background, queued
        /// under the key's lock so the table sees this key's writes in the order memory did. Returns true when the
        /// key had no running lockout before this call.
        /// </summary>
        public bool ApplyLockout(string key, DateTime nowUtc, DateTime untilUtc, ThreadPuzzleAudit audit = default)
        {
            RequireKey(key);

            var at = FloorToSecond(nowUtc);
            var until = CeilToSecond(untilUtc);

            return WithEntry(key, e =>
            {
                var wasLocked = e.LockoutUntil.HasValue && e.LockoutUntil.Value > nowUtc;
                LockLocked(e, key, at, until, audit);
                return !wasLocked;
            });
        }

        /// <summary>
        /// The policy's one atomic step, under the key's lock: if the key is locked out, records nothing and says
        /// so; otherwise records one fail, hands the fails inside <paramref name="window"/> (this one included) to
        /// <paramref name="lockoutFor"/>, and if that returns an end time, locks the key out until then and clears
        /// its fails exactly as <see cref="ApplyLockout"/> does. The ledger still decides nothing: the policy's
        /// delegate does. Two concurrent pulls on one key therefore can never both create the lockout, and a pull
        /// can never record a fail after the lockout that consumed its predecessors.
        /// </summary>
        public ThreadPuzzleLedgerFailResult RecordFailUnlessLocked(string key, DateTime nowUtc, TimeSpan window,
            Func<IReadOnlyList<DateTime>, DateTime?> lockoutFor, ThreadPuzzleAudit audit = default)
        {
            RequireKey(key);

            var at = FloorToSecond(nowUtc);

            return WithEntry(key, e =>
            {
                Trim(e, nowUtc);

                if (e.LockoutUntil.HasValue && e.LockoutUntil.Value > nowUtc)
                    return new ThreadPuzzleLedgerFailResult(e.LockoutUntil, null, null);

                e.Fails.Add(at);
                Persist(s => s.InsertFail(ToRow(key, ThreadPuzzleIpLedgerKind.Fail, at, null, audit)));

                var fails = InWindow(e, nowUtc, window);
                var wanted = lockoutFor?.Invoke(fails);

                if (!wanted.HasValue)
                    return new ThreadPuzzleLedgerFailResult(null, fails, null);

                var until = CeilToSecond(wanted.Value);
                LockLocked(e, key, at, until, audit);

                return new ThreadPuzzleLedgerFailResult(null, fails, e.LockoutUntil);
            });
        }

        /// <summary>Under the key's lock: extends the lockout (never shortens), clears fails at or before <paramref name="at"/>, queues both writes.</summary>
        private void LockLocked(Entry e, string key, DateTime at, DateTime until, ThreadPuzzleAudit audit)
        {
            e.LockoutUntil = Max(e.LockoutUntil, until);
            e.Fails.RemoveAll(t => t <= at);

            Persist(s => s.InsertLockout(ToRow(key, ThreadPuzzleIpLedgerKind.Lockout, at, until, audit)));
            Persist(s => s.ClearFails(key, at));
        }

        /// <summary>The key's lockout end if it is after <paramref name="nowUtc"/>, else null. Memory only.</summary>
        public DateTime? GetLockoutUntil(string key, DateTime nowUtc)
        {
            if (string.IsNullOrEmpty(key) || !entries.TryGetValue(key, out var e))
                return null;

            lock (e.Gate)
            {
                return e.LockoutUntil.HasValue && e.LockoutUntil.Value > nowUtc ? e.LockoutUntil : null;
            }
        }

        /// <summary>The key's fails inside <paramref name="window"/> ending at <paramref name="nowUtc"/>, oldest first. Memory only.</summary>
        public IReadOnlyList<DateTime> GetFails(string key, DateTime nowUtc, TimeSpan window)
        {
            if (string.IsNullOrEmpty(key) || !entries.TryGetValue(key, out var e))
                return Array.Empty<DateTime>();

            lock (e.Gate)
            {
                return InWindow(e, nowUtc, window);
            }
        }

        /// <summary>
        /// Drops in-memory keys with no fail inside <see cref="Retention"/> and no running lockout, and
        /// prunes the table the same way in the background. Returns the number of keys dropped.
        /// </summary>
        public int Sweep(DateTime nowUtc)
        {
            var dropped = 0;

            foreach (var kv in entries)
            {
                var e = kv.Value;

                lock (e.Gate)
                {
                    Trim(e, nowUtc);

                    if (e.Fails.Count > 0 || (e.LockoutUntil.HasValue && e.LockoutUntil.Value > nowUtc))
                        continue;

                    e.Removed = true;
                    entries.TryRemove(new KeyValuePair<string, Entry>(kv.Key, e));
                    dropped++;
                }
            }

            PruneInBackground(nowUtc);

            return dropped;
        }

        /// <summary>Waits up to <paramref name="timeout"/> for every queued write to finish. True if they did.</summary>
        public bool Flush(TimeSpan timeout)
        {
            Task pending;

            lock (chainLock)
                pending = chain;

            return pending.Wait(timeout);
        }

        // ---- internals ---------------------------------------------------------------------------------

        private void PruneInBackground(DateTime nowUtc)
        {
            var retention = Retention;
            Persist(s => s.PruneExpired(nowUtc, retention));
        }

        /// <summary>
        /// Runs <paramref name="action"/> under the key's lock on a live entry. A concurrent Sweep may mark
        /// the entry it is about to return as Removed; the loop then retries on a fresh one, so a write can
        /// never land on an entry that is no longer in the dictionary.
        /// </summary>
        private T WithEntry<T>(string key, Func<Entry, T> action)
        {
            while (true)
            {
                var e = entries.GetOrAdd(key, _ => new Entry());

                lock (e.Gate)
                {
                    if (e.Removed)
                        continue;

                    return action(e);
                }
            }
        }

        private void Trim(Entry e, DateTime nowUtc)
        {
            var cutoff = nowUtc - Retention;
            e.Fails.RemoveAll(t => t <= cutoff);

            if (e.LockoutUntil.HasValue && e.LockoutUntil.Value <= nowUtc)
                e.LockoutUntil = null;
        }

        private IReadOnlyList<DateTime> InWindow(Entry e, DateTime nowUtc, TimeSpan window)
        {
            if (window > Retention)
                window = Retention;

            var cutoff = nowUtc - window;
            var list = e.Fails.Where(t => t > cutoff && t <= nowUtc).ToList();
            list.Sort();
            return list;
        }

        private void Persist(Action<IThreadPuzzleIpLedgerStore> write)
        {
            if (store == null)
                return;

            lock (chainLock)
            {
                chain = chain.ContinueWith(_ => RunWrite(write), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            }
        }

        private void RunWrite(Action<IThreadPuzzleIpLedgerStore> write)
        {
            try
            {
                write(store);

                if (Interlocked.Exchange(ref failing, 0) == 1)
                    infoLog($"[PUZZLEIP] Ledger writes are succeeding again after {PersistFailureCount} failure(s) in total. Rows recorded during the outage were kept in memory only.");
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref persistFailureCount);
                ReportFailure("write", ex);
            }
        }

        /// <summary>Logs the FIRST failure of an outage as one error; later ones are only counted until a write succeeds.</summary>
        private void ReportFailure(string what, Exception ex)
        {
            if (Interlocked.Exchange(ref failing, 1) == 1)
                return;

            errorLog($"[PUZZLEIP] thread_puzzle_ip_ledger {what} failed; fails and lockouts keep working from memory but will not survive a restart until the table is reachable (is Database/Updates/Shard/2026-10-06-00-Add-Thread-Puzzle-Ip-Ledger.sql applied?). Further failures are counted, not logged, until a write succeeds. {ex.GetFullMessage()}");
        }

        private static ThreadPuzzleIpLedgerRow ToRow(string key, ThreadPuzzleIpLedgerKind kind, DateTime at, DateTime? until, ThreadPuzzleAudit audit) => new ThreadPuzzleIpLedgerRow
        {
            IpKey = key,
            Kind = kind,
            AtUtc = at,
            UntilUtc = until,
            AccountId = audit.AccountId,
            CharacterId = audit.CharacterId,
            RunId = audit.RunId,
            RunStartGroup = Clip(audit.RunStartGroup, 32),
            IpAddress = Clip(audit.IpAddress, 45),
        };

        private static void RequireKey(string key)
        {
            if (string.IsNullOrEmpty(key))
                throw new ArgumentException("A ledger key must be non-empty.", nameof(key));

            if (key.Length > 45)
                throw new ArgumentException("A ledger key is at most 45 characters (the ip_Key column).", nameof(key));
        }

        private static string Clip(string s, int max) => s == null || s.Length <= max ? s : s.Substring(0, max);

        private static DateTime? Max(DateTime? a, DateTime b) => a.HasValue && a.Value >= b ? a : b;

        private static DateTime AsUtc(DateTime t) => t.Kind == DateTimeKind.Utc ? t : DateTime.SpecifyKind(t, DateTimeKind.Utc);

        internal static DateTime FloorToSecond(DateTime t)
            => new DateTime(t.Ticks - t.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);

        internal static DateTime CeilToSecond(DateTime t)
        {
            var rem = t.Ticks % TimeSpan.TicksPerSecond;
            return new DateTime(rem == 0 ? t.Ticks : t.Ticks - rem + TimeSpan.TicksPerSecond, DateTimeKind.Utc);
        }
    }
}
