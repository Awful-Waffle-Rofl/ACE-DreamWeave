using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;

using log4net;

using MaxMind.Db;

namespace ACE.Server.Managers.Analytics
{
    // Session capture for the IP integrity dashboard (Docs/Monitoring/IP-INTEGRITY-DESIGN.md section 3).
    //
    // Everything in this file runs on the AnalyticsManager writer thread, never on a login or world thread.
    // The hot-path half (plain-value enqueue) lives in AnalyticsManager.RecordSessionLogin/Logout. The logic
    // here is split behind small interfaces (ISessionStore, IAsnReader) so the open/close/boot-cleanup
    // bookkeeping and the ASN classifier can be unit tested without a MySQL server or a real .mmdb.

    internal enum SessionEventKind
    {
        Open,
        Close
    }

    /// <summary>
    /// One queued login or logout, captured as plain values on the sim thread. Holds no Session, Player or
    /// WorldObject, so the writer can process it long after the session is gone.
    /// </summary>
    internal sealed class SessionEventRow
    {
        public SessionEventKind Kind;
        public DateTime Ts;
        public uint CharacterId;

        // Open only. A close carries just the character id and the time.
        public uint AccountId;
        public string AccountName;
        public string CharacterName;

        /// <summary>Raw address bytes: 4 for IPv4, 16 for IPv6. An IPv4-mapped IPv6 address is stored as IPv4.</summary>
        public byte[] Ip;
    }

    /// <summary>The ASN lookup outcome for one address. NULL in every member means "unavailable", never "no".</summary>
    internal readonly struct AsnResult
    {
        public readonly uint? Asn;
        public readonly string Org;

        /// <summary>True: ASN is on the hosting/VPN list. False: ASN known, not listed. Null: unknown.</summary>
        public readonly bool? IsHosting;

        public AsnResult(uint? asn, string org, bool? isHosting)
        {
            Asn = asn;
            Org = org;
            IsHosting = isHosting;
        }

        public static readonly AsnResult Unknown = new AsnResult(null, null, null);
    }

    /// <summary>Reads an ASN database. Abstracted so the classifier is testable without a real .mmdb.</summary>
    internal interface IAsnReader : IDisposable
    {
        /// <summary>False when the address is not in the database.</summary>
        bool TryLookup(IPAddress ip, out uint asn, out string org);
    }

    /// <summary>
    /// DB-IP "IP to ASN Lite" / MaxMind GeoLite2 ASN reader (same .mmdb layout). The field names
    /// autonomous_system_number and autonomous_system_organization were checked against a real
    /// dbip-asn-lite .mmdb (DatabaseType "DBIP-ASN-Lite (compat=GeoLite2-ASN)"); the number decodes as a
    /// 64-bit integer.
    /// </summary>
    internal sealed class MmdbAsnReader : IAsnReader
    {
        private readonly Reader reader;

        public MmdbAsnReader(string path)
        {
            // FileAccessMode.Memory reads the file into memory instead of memory-mapping it. A mapped file
            // stays locked on Windows, which would make "refresh monthly by replacing the file" fail.
            reader = new Reader(path, FileAccessMode.Memory);
        }

        public bool TryLookup(IPAddress ip, out uint asn, out string org)
        {
            asn = 0;
            org = null;

            var data = reader.Find<Dictionary<string, object>>(ip);
            if (data == null || !data.TryGetValue("autonomous_system_number", out var number) || number == null)
                return false;

            var value = Convert.ToInt64(number);
            if (value <= 0 || value > uint.MaxValue)
                return false;

            asn = (uint)value;

            if (data.TryGetValue("autonomous_system_organization", out var name))
                org = name as string;

            return true;
        }

        public void Dispose() => reader.Dispose();
    }

    /// <summary>
    /// Classifies an address into (ASN, org, hosting flag) from two optional host files. Not thread safe:
    /// owned by the writer thread.
    ///
    /// Both files are re-read when their modified time changes. A missing or unreadable file leaves the
    /// affected columns NULL and logs ONE warning per state change (not one per login); nothing here ever
    /// throws out to the writer.
    /// </summary>
    internal sealed class AsnClassifier : IDisposable
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private readonly string dbPath;
        private readonly string listPath;
        private readonly Func<string, IAsnReader> readerFactory;

        private IAsnReader reader;
        private bool dbAttempted;
        private DateTime? dbStamp;

        private HashSet<uint> hosting;
        private bool listAttempted;
        private DateTime? listStamp;

        private bool lookupWarned;

        public AsnClassifier(string asnDatabasePath, string hostingListPath, Func<string, IAsnReader> readerFactory = null)
        {
            dbPath = asnDatabasePath;
            listPath = hostingListPath;
            this.readerFactory = readerFactory ?? (path => new MmdbAsnReader(path));
        }

        public AsnResult Classify(byte[] ipBytes)
        {
            RefreshDatabase();
            RefreshList();

            uint? asn = null;
            string org = null;

            if (reader != null && ipBytes != null)
            {
                try
                {
                    if (reader.TryLookup(new IPAddress(ipBytes), out var a, out var o))
                    {
                        asn = a;
                        org = o;
                    }
                }
                catch (Exception ex)
                {
                    if (!lookupWarned)
                    {
                        lookupWarned = true;
                        log.Warn("AsnClassifier: ASN lookup threw; affected rows get NULL asn (warning logged once until the database file changes).", ex);
                    }
                }
            }

            // Hosting is only knowable with BOTH an ASN for this address and a loaded list. Anything less
            // is NULL (unknown), never 0, so "no list installed" cannot read as "nobody is on a VPN".
            bool? isHosting = asn.HasValue && hosting != null ? hosting.Contains(asn.Value) : (bool?)null;

            return new AsnResult(asn, org, isHosting);
        }

        private static DateTime? StampOf(string path)
        {
            try
            {
                return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : (DateTime?)null;
            }
            catch
            {
                return null;
            }
        }

        private void RefreshDatabase()
        {
            if (string.IsNullOrWhiteSpace(dbPath))
                return;

            var stamp = StampOf(dbPath);
            if (dbAttempted && stamp == dbStamp)
                return;

            dbAttempted = true;
            dbStamp = stamp;
            lookupWarned = false;

            var old = reader;
            reader = null;

            try
            {
                old?.Dispose();
            }
            catch
            {
                // a failing Dispose on the stale reader must not block loading the new file
            }

            if (stamp == null)
            {
                log.Warn($"AsnClassifier: ASN database '{dbPath}' not found; asn, asn_org and is_hosting will be NULL until it appears.");
                return;
            }

            try
            {
                reader = readerFactory(dbPath);
                log.Info($"AsnClassifier: loaded ASN database '{dbPath}'.");
            }
            catch (Exception ex)
            {
                log.Warn($"AsnClassifier: could not read ASN database '{dbPath}'; asn, asn_org and is_hosting will be NULL until the file changes.", ex);
            }
        }

        private void RefreshList()
        {
            if (string.IsNullOrWhiteSpace(listPath))
                return;

            var stamp = StampOf(listPath);
            if (listAttempted && stamp == listStamp)
                return;

            listAttempted = true;
            listStamp = stamp;
            hosting = null;

            if (stamp == null)
            {
                log.Warn($"AsnClassifier: hosting ASN list '{listPath}' not found; is_hosting will be NULL until it appears.");
                return;
            }

            try
            {
                hosting = ParseHostingList(File.ReadAllLines(listPath), out var skipped);
                log.Info($"AsnClassifier: loaded {hosting.Count} hosting ASNs from '{listPath}'{(skipped > 0 ? $" ({skipped} unparseable lines skipped)" : "")}.");
            }
            catch (Exception ex)
            {
                log.Warn($"AsnClassifier: could not read hosting ASN list '{listPath}'; is_hosting will be NULL until the file changes.", ex);
            }
        }

        /// <summary>
        /// One ASN per line. '#' starts a comment (whole line or trailing), blank lines are ignored, and an
        /// optional "AS" prefix is accepted ("AS13335" and "13335" are the same entry). Anything else on a
        /// line is skipped and counted rather than failing the whole file.
        /// </summary>
        internal static HashSet<uint> ParseHostingList(IEnumerable<string> lines, out int skipped)
        {
            var result = new HashSet<uint>();
            skipped = 0;

            foreach (var raw in lines)
            {
                var line = raw;
                var hash = line.IndexOf('#');
                if (hash >= 0)
                    line = line.Substring(0, hash);

                line = line.Trim();
                if (line.Length == 0)
                    continue;

                if (line.StartsWith("AS", StringComparison.OrdinalIgnoreCase))
                    line = line.Substring(2);

                if (uint.TryParse(line, out var asn) && asn != 0)
                    result.Add(asn);
                else
                    skipped++;
            }

            return result;
        }

        public void Dispose()
        {
            try
            {
                reader?.Dispose();
            }
            catch
            {
            }

            reader = null;
        }
    }

    /// <summary>
    /// The exact values session_event.close_reason can hold (VARCHAR(8), NULL while a session is open). The
    /// dashboard reads these strings, so they are a contract: do not rename or add one casually.
    /// </summary>
    internal static class SessionCloseReason
    {
        /// <summary>A FinalizeLogout event was seen.</summary>
        public const string Logout = "logout";

        /// <summary>The per-flush reconcile found the character no longer online with no logout event.</summary>
        public const string Orphan = "orphan";

        /// <summary>Boot cleanup: a previous process died or stopped without closing it.</summary>
        public const string Boot = "boot";

        /// <summary>Closed because the same character logged in again while this row was still open.</summary>
        public const string Relogin = "relogin";

        /// <summary>Closed by the graceful-shutdown final pass.</summary>
        public const string Shutdown = "shutdown";

        public static readonly string[] All = { Logout, Orphan, Boot, Relogin, Shutdown };
    }

    /// <summary>The persistence the session writer needs. MySqlSessionStore is the real one.</summary>
    internal interface ISessionStore
    {
        /// <summary>Boot cleanup: closes every session a previous process left open (logout_utc = last_seen_utc).</summary>
        void CloseAbandonedSessions();

        /// <summary>Inserts an open session row and returns its id.</summary>
        long InsertOpen(SessionEventRow row, AsnResult asn);

        /// <summary>Closes a session at <paramref name="ts"/> with a <see cref="SessionCloseReason"/>.</summary>
        void Close(long id, DateTime ts, string reason);

        /// <summary>Closes a session whose logout was never seen, at the last time it was known alive.</summary>
        void CloseAtLastSeen(long id, string reason);

        /// <summary>Advances last_seen_utc for every id in one statement.</summary>
        void Touch(IReadOnlyCollection<long> ids, DateTime ts);
    }

    /// <summary>The production store: ace_analytics through AnalyticsDatabase.</summary>
    internal sealed class MySqlSessionStore : ISessionStore
    {
        public void CloseAbandonedSessions() => AnalyticsDatabase.CloseAbandonedSessions();

        public long InsertOpen(SessionEventRow row, AsnResult asn) => AnalyticsDatabase.InsertSessionOpen(row, asn);

        public void Close(long id, DateTime ts, string reason) => AnalyticsDatabase.CloseSession(id, ts, reason);

        public void CloseAtLastSeen(long id, string reason) => AnalyticsDatabase.CloseSessionAtLastSeen(id, reason);

        public void Touch(IReadOnlyCollection<long> ids, DateTime ts) => AnalyticsDatabase.TouchSessions(ids, ts);
    }

    /// <summary>
    /// The writer-side bookkeeping for sessions: which character has which open row. Owned by the writer
    /// thread. The queue itself lives in AnalyticsManager; this only consumes drained batches.
    /// </summary>
    internal sealed class SessionEventWriter
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private readonly ISessionStore store;
        private readonly AsnClassifier classifier;

        // character id -> open row id. Events for one character arrive in order (one FIFO queue), so a
        // close always finds the open that preceded it.
        private readonly Dictionary<uint, long> open = new Dictionary<uint, long>();

        public SessionEventWriter(ISessionStore store, AsnClassifier classifier)
        {
            this.store = store;
            this.classifier = classifier;
        }

        public int OpenCount => open.Count;

        /// <summary>Runs once when the writer starts: closes sessions left open by a previous process.</summary>
        public void StartUp()
        {
            try
            {
                store.CloseAbandonedSessions();
            }
            catch (Exception ex)
            {
                log.Error("SessionEventWriter: boot cleanup of abandoned sessions failed.", ex);
            }
        }

        public void Process(IReadOnlyList<SessionEventRow> rows)
        {
            foreach (var row in rows)
            {
                try
                {
                    if (row.Kind == SessionEventKind.Open)
                        ProcessOpen(row);
                    else
                        ProcessClose(row);
                }
                catch (Exception ex)
                {
                    // One bad row must not discard the rest of the batch (the caller has already dequeued it).
                    log.Error($"SessionEventWriter: failed to write a session {row.Kind} for character 0x{row.CharacterId:X8}.", ex);
                }
            }
        }

        private void ProcessOpen(SessionEventRow row)
        {
            // A character that logs in while we still hold an open row for it means its close was never seen
            // (a logout path that bypassed the hook, or a dropped event). End the old row where it was last
            // known alive instead of leaving two overlapping sessions for one character.
            if (open.TryGetValue(row.CharacterId, out var stale))
            {
                open.Remove(row.CharacterId);

                // Its own try/catch: if closing the stale row fails, the NEW login must still get a row. The
                // stale row is then left for the boot cleanup to close.
                try
                {
                    store.CloseAtLastSeen(stale, SessionCloseReason.Relogin);
                }
                catch (Exception ex)
                {
                    log.Error($"SessionEventWriter: failed to close stale session row {stale} on re-login; opening the new session anyway.", ex);
                }
            }

            var asn = classifier != null ? classifier.Classify(row.Ip) : AsnResult.Unknown;

            var id = store.InsertOpen(row, asn);
            open[row.CharacterId] = id;
        }

        private void ProcessClose(SessionEventRow row)
        {
            // No open row: the open was dropped (queue full), failed to write, or this is the boot-cleanup
            // case where the previous process's rows were already closed. Nothing to do.
            if (!open.TryGetValue(row.CharacterId, out var id))
                return;

            open.Remove(row.CharacterId);
            store.Close(id, row.Ts, SessionCloseReason.Logout);
        }

        /// <summary>
        /// Runs on each analytics flush. Advances last_seen_utc for every open session whose character is
        /// still online (one statement for the whole set, bounded by the online count). A session whose
        /// character is no longer online but never produced a close is closed at its last known-alive time,
        /// so it cannot stay open for the life of the process.
        /// </summary>
        public void Advance(Func<uint, bool> isOnline, DateTime now)
        {
            if (open.Count == 0)
                return;

            var alive = new List<long>(open.Count);
            List<uint> gone = null;

            foreach (var kvp in open)
            {
                if (isOnline(kvp.Key))
                    alive.Add(kvp.Value);
                else
                    (gone ?? (gone = new List<uint>())).Add(kvp.Key);
            }

            if (gone != null)
            {
                foreach (var characterId in gone)
                {
                    var id = open[characterId];
                    open.Remove(characterId);

                    try
                    {
                        store.CloseAtLastSeen(id, SessionCloseReason.Orphan);
                    }
                    catch (Exception ex)
                    {
                        log.Error($"SessionEventWriter: failed to close orphaned session row {id}.", ex);
                    }
                }
            }

            if (alive.Count > 0)
                store.Touch(alive, now);
        }

        /// <summary>
        /// Graceful shutdown: closes every session still tracked as open at <paramref name="ts"/>. Runs after the
        /// final queue drain, so every logout the shutdown itself produced has already been applied and only
        /// sessions that were genuinely still open (fast shutdown, hard deadline) are closed as 'shutdown'.
        /// </summary>
        public void CloseAll(DateTime ts)
        {
            foreach (var kvp in open.ToList())
            {
                open.Remove(kvp.Key);

                try
                {
                    store.Close(kvp.Value, ts, SessionCloseReason.Shutdown);
                }
                catch (Exception ex)
                {
                    log.Error($"SessionEventWriter: failed to close session row {kvp.Value} at shutdown.", ex);
                }
            }
        }
    }
}
