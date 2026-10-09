using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using log4net;

using ACE.Server.Network.GameMessages;

namespace ACE.Server.Network
{
    /// <summary>
    /// One captured outbound record: a whole GameMessage (as enqueued), one wire fragment (as produced by
    /// the bundling path), or a session boundary marker. The byte array is a private copy.
    /// </summary>
    public sealed class NetCaptureEntry
    {
        public DateTime TimeUtc { get; init; }
        public bool IsFragment { get; init; }
        public bool IsSessionMarker { get; init; }
        /// <summary>Endpoint text, session markers only.</summary>
        public string Note { get; init; }
        public uint Opcode { get; init; }
        /// <summary>Message length for a msg entry, payload length for a frag entry.</summary>
        public int Length => Data.Length;
        public uint Sequence { get; init; }
        public ushort Index { get; init; }
        public ushort Count { get; init; }
        public ushort Group { get; init; }
        public byte[] Data { get; init; }
    }

    /// <summary>
    /// Bounded, thread-safe ring of captured entries for one character. Oldest entries are evicted first
    /// once the charged total would exceed the budget. Each entry is charged its payload length plus
    /// <see cref="EntryOverhead"/>, so the cap bounds real memory (object + array headers), not just
    /// payload, and zero-length entries are still evictable.
    /// </summary>
    public sealed class NetCaptureRing
    {
        public const long DefaultMaxBytes = 64L * 1024 * 1024;

        /// <summary>Estimated per-entry cost beyond the payload: NetCaptureEntry object, array header, queue slot.</summary>
        public const int EntryOverhead = 96;

        private readonly object sync = new object();
        private readonly Queue<NetCaptureEntry> entries = new Queue<NetCaptureEntry>();
        private long bytes;
        private object owner;

        public long MaxBytes { get; }

        public NetCaptureRing(long maxBytes = DefaultMaxBytes)
        {
            MaxBytes = maxBytes;
        }

        public int Count { get { lock (sync) return entries.Count; } }

        /// <summary>Charged bytes (payload plus per-entry overhead), the quantity the cap bounds.</summary>
        public long Bytes { get { lock (sync) return bytes; } }

        private static long Cost(NetCaptureEntry e) => e.Data.Length + EntryOverhead + (e.Note?.Length ?? 0);

        /// <summary>
        /// Copies buffer[0..length) into a private array. Never touches a shared stream position.
        /// </summary>
        public void AddMessage(DateTime timeUtc, uint opcode, byte[] buffer, int length, ushort group = 0)
        {
            var copy = new byte[length];
            Buffer.BlockCopy(buffer, 0, copy, 0, length);
            Add(new NetCaptureEntry { TimeUtc = timeUtc, IsFragment = false, Opcode = opcode, Group = group, Data = copy });
        }

        public void AddFragment(DateTime timeUtc, uint opcode, uint sequence, ushort index, ushort count, ushort group, byte[] payload)
        {
            var copy = new byte[payload.Length];
            Buffer.BlockCopy(payload, 0, copy, 0, payload.Length);
            Add(new NetCaptureEntry { TimeUtc = timeUtc, IsFragment = true, Opcode = opcode, Sequence = sequence, Index = index, Count = count, Group = group, Data = copy });
        }

        /// <summary>
        /// Records a session boundary the first time a new owner (a NetworkSession) writes into this ring,
        /// so a dump that spans a relog or post-terminate traffic can be split.
        /// </summary>
        public void BindOwner(object newOwner, string endpoint)
        {
            lock (sync)
            {
                if (ReferenceEquals(owner, newOwner))
                    return;
                owner = newOwner;
            }
            Add(new NetCaptureEntry { TimeUtc = DateTime.UtcNow, IsSessionMarker = true, Note = endpoint ?? string.Empty, Data = Array.Empty<byte>() });
        }

        private void Add(NetCaptureEntry entry)
        {
            var cost = Cost(entry);
            if (cost > MaxBytes)
                return;

            lock (sync)
            {
                entries.Enqueue(entry);
                bytes += cost;
                while (bytes > MaxBytes && entries.Count > 0)
                    bytes -= Cost(entries.Dequeue());
            }
        }

        /// <summary>Oldest-first copy of the current entries.</summary>
        public NetCaptureEntry[] Snapshot()
        {
            lock (sync)
                return entries.ToArray();
        }

        /// <summary>Returns the current entries oldest-first and empties the ring.</summary>
        public NetCaptureEntry[] TakeAndClear()
        {
            lock (sync)
            {
                var result = entries.ToArray();
                entries.Clear();
                bytes = 0;
                return result;
            }
        }

        public void Clear()
        {
            lock (sync)
            {
                entries.Clear();
                bytes = 0;
            }
        }
    }

    /// <summary>
    /// Per-session cache of "which ring, if any, is this session feeding". Immutable holder swapped
    /// atomically because EnqueueSend runs on many threads. Only consulted when NetCapture.AnyFlagged.
    /// </summary>
    public sealed class NetCaptureBinder
    {
        private sealed class Binding
        {
            public int Version;
            public uint Guid;
            public NetCaptureRing Ring;
        }

        private volatile Binding binding;

        /// <summary>
        /// Returns the ring for <paramref name="guid"/> or null. Re-resolves only when the flag set or the
        /// session's player changed; the first bind of a ring by this owner writes a session marker.
        /// </summary>
        public NetCaptureRing Resolve(uint guid, object owner, Func<string> endpoint)
        {
            var ver = NetCapture.Version;
            var b = binding;
            if (b == null || b.Version != ver || b.Guid != guid)
            {
                var ring = guid != 0 ? NetCapture.GetRing(guid) : null;
                b = new Binding { Version = ver, Guid = guid, Ring = ring };
                binding = b;
                ring?.BindOwner(owner, endpoint?.Invoke());
            }
            return b.Ring;
        }
    }

    /// <summary>Snapshot row for /netcapture status.</summary>
    public readonly record struct NetCaptureStatusRow(uint Guid, string Name, int Count, long Bytes);

    /// <summary>
    /// Admin diagnostic: per-character capture of outbound GameMessages and wire fragments.
    /// State is process memory only, never persisted, so every restart starts with capture off.
    /// </summary>
    public static class NetCapture
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private const int MaxReasonInFileName = 40;

        private sealed class Flagged
        {
            public string Name;
            public NetCaptureRing Ring;
        }

        private static readonly ConcurrentDictionary<uint, Flagged> flagged = new ConcurrentDictionary<uint, Flagged>();

        // Serializes add/remove together with the flaggedCount publish, so AnyFlagged cannot be left stale.
        private static readonly object flagLock = new object();

        // Hot-path gate: one volatile read. Zero means nobody is flagged and no other work happens.
        private static volatile int flaggedCount;

        // Bumped on every flag change so a session's cached binding knows to refresh.
        private static int version;

        private static readonly object pendingLock = new object();
        private static readonly List<Task> pendingDumps = new List<Task>();

        /// <summary>Hot-path check: false when no character is flagged.</summary>
        public static bool AnyFlagged => flaggedCount != 0;

        public static int Version => Volatile.Read(ref version);

        /// <summary>Ring size used for newly flagged characters. Tests may lower it.</summary>
        public static long RingBytes { get; set; } = NetCaptureRing.DefaultMaxBytes;

        /// <summary>Test seam: overrides the dump directory. Null means resolve from log4net.</summary>
        public static string DumpDirectoryOverride { get; set; }

        /// <returns>False if the character was already flagged.</returns>
        public static bool Enable(uint guid, string name)
        {
            lock (flagLock)
            {
                var added = flagged.TryAdd(guid, new Flagged { Name = name, Ring = new NetCaptureRing(RingBytes) });
                if (added)
                {
                    flaggedCount = flagged.Count;
                    Interlocked.Increment(ref version);
                }
                return added;
            }
        }

        /// <returns>False if the character was not flagged. Discards the ring.</returns>
        public static bool Disable(uint guid)
        {
            lock (flagLock)
            {
                if (!flagged.TryRemove(guid, out var f))
                    return false;
                f.Ring.Clear();
                flaggedCount = flagged.Count;
                Interlocked.Increment(ref version);
                return true;
            }
        }

        public static void DisableAll()
        {
            lock (flagLock)
            {
                foreach (var kv in flagged.ToArray())
                {
                    if (flagged.TryRemove(kv.Key, out var f))
                        f.Ring.Clear();
                }
                flaggedCount = flagged.Count;
                Interlocked.Increment(ref version);
            }
        }

        public static NetCaptureRing GetRing(uint guid)
        {
            return flagged.TryGetValue(guid, out var f) ? f.Ring : null;
        }

        public static string GetName(uint guid)
        {
            return flagged.TryGetValue(guid, out var f) ? f.Name : null;
        }

        public static List<NetCaptureStatusRow> Status()
        {
            return flagged
                .Select(kv => new NetCaptureStatusRow(kv.Key, kv.Value.Name, kv.Value.Ring.Count, kv.Value.Ring.Bytes))
                .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Copies the message bytes out of the backing array. Never Seeks/Reads the shared stream: one
        /// GameMessage is shared across sessions (see the GameMessage.Data remarks), so Position and Length
        /// are left untouched.
        /// </summary>
        public static void RecordMessage(NetCaptureRing ring, GameMessage message, DateTime timeUtc)
        {
            ring.AddMessage(timeUtc, (uint)message.Opcode, message.Data.GetBuffer(), (int)message.Data.Length, (ushort)message.Group);
        }

        // ---- dump ----

        public static string ResolveDumpDirectory()
        {
            if (!string.IsNullOrEmpty(DumpDirectoryOverride))
                return DumpDirectoryOverride;

            try
            {
                var repo = LogManager.GetRepository(System.Reflection.Assembly.GetEntryAssembly());
                foreach (var appender in repo.GetAppenders())
                {
                    if (appender is log4net.Appender.FileAppender fa && !string.IsNullOrEmpty(fa.File))
                    {
                        var dir = Path.GetDirectoryName(Path.GetFullPath(fa.File));
                        if (!string.IsNullOrEmpty(dir))
                            return dir;
                    }
                }
            }
            catch
            {
                // fall through to the fallback
            }

            return Path.Combine(AppContext.BaseDirectory, "NetCapture");
        }

        public static string SanitizeForFileName(string s, int maxLength = int.MaxValue)
        {
            if (string.IsNullOrEmpty(s))
                return "unknown";
            var sb = new StringBuilder(Math.Min(s.Length, maxLength));
            foreach (var c in s)
            {
                if (sb.Length >= maxLength)
                    break;
                sb.Append(char.IsAsciiLetterOrDigit(c) || c == '-' ? c : '_');
            }
            return sb.ToString();
        }

        private static string Q(string s) => JsonSerializer.Serialize(s ?? string.Empty);

        private const string TimeFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";

        /// <summary>
        /// Writes the header line and one JSON line per entry. Hex is uppercase.
        /// </summary>
        public static void WriteJsonLines(TextWriter w, string charName, uint guid, string endpoint, string reason, IReadOnlyList<NetCaptureEntry> entries)
        {
            int msgs = 0, frags = 0, sessions = 0;
            foreach (var e in entries)
            {
                if (e.IsSessionMarker) sessions++;
                else if (e.IsFragment) frags++;
                else msgs++;
            }

            w.Write("{\"kind\":\"header\",\"character\":" + Q(charName));
            w.Write(",\"guid\":\"0x" + guid.ToString("X8", CultureInfo.InvariantCulture) + "\"");
            w.Write(",\"endpoint\":" + Q(endpoint));
            w.Write(",\"reason\":" + Q(reason));
            w.Write(",\"written\":\"" + DateTime.UtcNow.ToString(TimeFormat, CultureInfo.InvariantCulture) + "\"");
            w.Write(",\"msgCount\":" + msgs.ToString(CultureInfo.InvariantCulture));
            w.Write(",\"fragCount\":" + frags.ToString(CultureInfo.InvariantCulture));
            w.Write(",\"sessionCount\":" + sessions.ToString(CultureInfo.InvariantCulture));
            w.Write("}\n");

            foreach (var e in entries)
            {
                w.Write("{\"t\":\"" + e.TimeUtc.ToString(TimeFormat, CultureInfo.InvariantCulture) + "\"");
                if (e.IsSessionMarker)
                {
                    w.Write(",\"kind\":\"session\",\"endpoint\":" + Q(e.Note) + "}\n");
                    continue;
                }
                w.Write(e.IsFragment ? ",\"kind\":\"frag\"" : ",\"kind\":\"msg\"");
                w.Write(",\"opcode\":\"0x" + e.Opcode.ToString("X4", CultureInfo.InvariantCulture) + "\"");
                w.Write(",\"len\":" + e.Length.ToString(CultureInfo.InvariantCulture));
                if (e.IsFragment)
                {
                    w.Write(",\"seq\":" + e.Sequence.ToString(CultureInfo.InvariantCulture));
                    w.Write(",\"idx\":" + e.Index.ToString(CultureInfo.InvariantCulture));
                    w.Write(",\"count\":" + e.Count.ToString(CultureInfo.InvariantCulture));
                }
                w.Write(",\"group\":" + e.Group.ToString(CultureInfo.InvariantCulture));
                w.Write(",\"hex\":\"" + Convert.ToHexString(e.Data) + "\"}\n");
            }
        }

        /// <summary>The path a dump of this character/reason would be written to right now (before collision suffixing).</summary>
        public static string PlanDumpPath(string charName, string reason)
        {
            var file = "netcapture_" + SanitizeForFileName(charName) + "_" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "_" + SanitizeForFileName(reason, MaxReasonInFileName) + ".jsonl";
            return Path.Combine(ResolveDumpDirectory(), file);
        }

        private static string WriteFile(string path, string charName, uint guid, string endpoint, string reason, IReadOnlyList<NetCaptureEntry> entries)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            int n = 1;
            var stem = Path.Combine(Path.GetDirectoryName(path), Path.GetFileNameWithoutExtension(path));
            while (File.Exists(path))
                path = stem + "_" + (n++) + ".jsonl";

            using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            using (var w = new StreamWriter(fs, new UTF8Encoding(false)) { NewLine = "\n" })
                WriteJsonLines(w, charName, guid, endpoint, reason, entries);
            return path;
        }

        /// <summary>
        /// Writes the entries to a new file in the dump directory and returns its full path. If the first
        /// attempt fails it retries once under a short fallback name; on final failure it logs the entry count
        /// and rethrows.
        /// </summary>
        public static string WriteDumpFile(string charName, uint guid, string endpoint, string reason, IReadOnlyList<NetCaptureEntry> entries)
        {
            string path = null;
            try
            {
                path = WriteFile(PlanDumpPath(charName, reason), charName, guid, endpoint, reason, entries);
            }
            catch (Exception ex)
            {
                log.Warn("NetCapture: dump write failed for " + charName + ", retrying with fallback name", ex);
                try
                {
                    var fallback = Path.Combine(ResolveDumpDirectory(), "netcapture_" + guid.ToString("X8", CultureInfo.InvariantCulture) + "_" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".jsonl");
                    path = WriteFile(fallback, charName, guid, endpoint, reason, entries);
                }
                catch (Exception ex2)
                {
                    log.Warn("NetCapture: dump FAILED, " + entries.Count + " entries for " + charName + " were lost", ex2);
                    throw;
                }
            }

            log.Info("NetCapture: wrote " + entries.Count + " entries to " + path);
            return path;
        }

        private static void Track(Task t)
        {
            lock (pendingLock)
                pendingDumps.Add(t);
            t.ContinueWith(done =>
            {
                lock (pendingLock)
                    pendingDumps.Remove(done);
            }, TaskScheduler.Default);
        }

        /// <summary>Waits for in-flight dump writes; used by the server shutdown path. Never throws.</summary>
        public static void WaitForPendingDumps(TimeSpan timeout)
        {
            try
            {
                Task[] tasks;
                lock (pendingLock)
                    tasks = pendingDumps.ToArray();
                if (tasks.Length == 0)
                    return;
                if (!Task.WaitAll(tasks, timeout))
                    log.Warn("NetCapture: " + tasks.Count(t => !t.IsCompleted) + " dump write(s) still running after " + timeout.TotalSeconds + "s");
            }
            catch (Exception ex)
            {
                log.Warn("NetCapture: waiting for pending dumps failed", ex);
            }
        }

        /// <summary>
        /// Dumps the character's ring on the thread pool and keeps it filled. Returns the intended path
        /// immediately, or null if the character is not flagged. The returned task never faults.
        /// </summary>
        public static string DumpNowAsync(uint guid, string endpoint, string reason, out Task completion)
        {
            completion = Task.CompletedTask;
            if (!flagged.TryGetValue(guid, out var f))
                return null;

            var name = f.Name;
            var ring = f.Ring;
            var planned = PlanDumpPath(name, reason);
            completion = Task.Run(() =>
            {
                try
                {
                    WriteDumpFile(name, guid, endpoint, reason, ring.Snapshot());
                }
                catch (Exception ex)
                {
                    log.Warn("NetCapture: manual dump failed for " + name, ex);
                }
            });
            Track(completion);
            return planned;
        }

        /// <summary>
        /// Called from Session.Terminate. Hands the ring to a pool task, which snapshots, clears and writes it,
        /// so the Terminate caller does no copying or IO. Never throws; the flag stays on.
        /// </summary>
        public static void OnSessionTerminate(uint guid, string endpoint, string reason, out Task completion)
        {
            completion = Task.CompletedTask;
            try
            {
                if (!flagged.TryGetValue(guid, out var f))
                    return;

                var name = f.Name;
                var ring = f.Ring;
                completion = Task.Run(() =>
                {
                    try
                    {
                        var entries = ring.TakeAndClear();
                        if (entries.Length == 0)
                            return;
                        WriteDumpFile(name, guid, endpoint, reason, entries);
                    }
                    catch (Exception ex)
                    {
                        log.Warn("NetCapture: auto-dump failed for " + name, ex);
                    }
                });
                Track(completion);
            }
            catch (Exception ex)
            {
                log.Warn("NetCapture: OnSessionTerminate failed", ex);
            }
        }

        public static void OnSessionTerminate(uint guid, string endpoint, string reason)
        {
            OnSessionTerminate(guid, endpoint, reason, out _);
        }
    }
}
