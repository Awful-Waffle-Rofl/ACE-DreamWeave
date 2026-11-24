// Local modification (not upstream's): the traffic log. Every frame the link carries, both ways,
// the link's own comings and goings, and the HTTPS calls `/derethnetwork verify` and the hub's challenge make,
// written one JSON object per line to a file per UTC day:
//
//   <log dir>/dereth-network/traffic-YYYY-MM-DD.jsonl
//
//   {"utc":"<ISO-8601 Z>","dir":"in|out|meta","type":"<frame type>","epoch":N,"frame":"<raw frame>"}
//
// A meta record (`dir` meta) has a `type` of its own (link.up, link.down, link.refused,
// frame.dropped, switch.on, switch.off, traffic.on, traffic.off, mod.stop, dropped), its `epoch`,
// and a `detail` and the `frame` where it has them; `dropped` has a `count` instead. An HTTPS
// record is
//
//   {"utc":"...","dir":"out","type":"https.verify|https.answer","url":"...","status":N,"request":"<body sent>","body":"<answer, at most 64 KiB>"}
//
// with status 0 when no answer came.
//
// The taps (the link's own tasks, an HTTP handler) only queue a record; a task of its own writes
// them, so neither the link nor the world thread ever waits on the disk. The queue is bounded (at
// most MaxQueuedRecords records and MaxQueuedChars characters); past it a record is dropped and
// counted, and the next write that succeeds says how many went (`"type":"dropped","count":N`). A
// write that fails closes the file, warns at most once a minute, drops and counts its batch, and the
// log tries again after RetryWait; nothing here throws into its callers. Every line is ASCII: any
// other character is written as a JSON \u escape.
//
// Redaction: the world's registry key is replaced with `(key)` anywhere it appears; the `token` of a
// `/derethnetwork verify` request is replaced with `(token)`; link codes (Wire.IsLinkCode's shape) in a
// `person.result` frame are replaced with `(code)`. Everything else is kept as it was carried.
//
// Nothing is ever deleted here: keeping or removing old days is the operator's.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace Dereth.Network;

public sealed class TrafficLog : IDisposable
{
    /// <summary>The most records that may wait to be written.</summary>
    public const int MaxQueuedRecords = 50_000;

    /// <summary>The most characters of text that may wait to be written (8 MiB).</summary>
    public const long MaxQueuedChars = 8L * 1024 * 1024;

    private static readonly TimeSpan WarnEvery = TimeSpan.FromMinutes(1);

    private abstract class Item;

    private sealed class Rec : Item
    {
        public required DateTimeOffset Utc { get; init; }
        public required string Dir { get; init; }
        /// <summary>The record's type; null for a frame, whose type is read from it when it is written.</summary>
        public string? Type { get; init; }
        public long Epoch { get; init; }
        public string? Frame { get; init; }
        public string? Detail { get; init; }
        public string? Url { get; init; }
        public int Status { get; init; }
        public string? Request { get; init; }
        public string? Body { get; init; }
        public long Chars { get; init; }
    }

    /// <summary>Close the file once everything before it is written.</summary>
    private sealed class Close : Item;

    /// <summary>Signal once everything before it is written and flushed.</summary>
    private sealed class FlushMark : Item
    {
        public readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly string directory;
    private readonly string? key;
    private readonly Logger log;
    private readonly Func<DateTimeOffset> utcNow;
    private readonly TimeSpan retryWait;
    private readonly Channel<Item> queue = Channel.CreateUnbounded<Item>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task writer;

    private int queuedRecords;
    private long queuedChars;
    private long tapDropped;
    private volatile bool enabled;

    // The writer task's own.
    private FileStream? file;
    private DateTime fileDay;
    private long dropped;
    private long retryAt;
    private long warnedAt;
    private bool warned;

    /// <param name="directory">The folder the day files go in (created when first written).</param>
    /// <param name="key">The world's registry key, never written: replaced with `(key)`.</param>
    /// <param name="log">Where a failing write is reported, at most once a minute.</param>
    /// <param name="utcNow">The clock records are stamped with (tests turn it).</param>
    /// <param name="retryWait">How long the log waits after a failed write before it writes again.</param>
    /// <param name="holdWriter">For tests: the writer starts once this ends, so the queue's bound can be reached.</param>
    public TrafficLog(string directory, string? key, Logger log, Func<DateTimeOffset>? utcNow = null, TimeSpan? retryWait = null,
        Task? holdWriter = null)
    {
        this.directory = directory;
        this.key = string.IsNullOrEmpty(key) ? null : key;
        this.log = log;
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        this.retryWait = retryWait ?? TimeSpan.FromSeconds(5);
        writer = Task.Run(() => WriteLoop(holdWriter));
    }

    public string Directory => directory;

    /// <summary>The day file a record stamped `utc` goes to.</summary>
    public string PathFor(DateTimeOffset utc) =>
        Path.Combine(directory, "traffic-" + utc.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".jsonl");

    /// <summary>
    /// Whether records are taken. Off, the taps take nothing, and what was taken before is written
    /// and the file closed.
    /// </summary>
    public bool Enabled
    {
        get => enabled;
        set
        {
            if (enabled == value)
                return;
            enabled = value;
            if (!value)
                queue.Writer.TryWrite(new Close());
        }
    }

    /// <summary>Records dropped and not yet reported in the file.</summary>
    public long Dropped => Interlocked.Read(ref tapDropped) + Interlocked.Read(ref dropped);

    /// <summary>A frame the link carried: `inbound` from the hub, else to it.</summary>
    public void Frame(bool inbound, long epoch, string frame)
    {
        if (!enabled)
            return;
        Take(new Rec { Utc = utcNow(), Dir = inbound ? "in" : "out", Epoch = epoch, Frame = frame, Chars = frame.Length });
    }

    /// <summary>Something of the link's own (up, down, refused, a frame dropped), or of the switch.</summary>
    public void Meta(string type, long epoch, string? detail = null)
    {
        if (!enabled)
            return;
        Take(new Rec { Utc = utcNow(), Dir = "meta", Type = type, Epoch = epoch, Detail = detail, Chars = type.Length + (detail?.Length ?? 0) });
    }

    /// <summary>A frame not sent: for a link that has gone, or too long.</summary>
    public void FrameDropped(long epoch, string reason, string frame)
    {
        if (!enabled)
            return;
        Take(new Rec { Utc = utcNow(), Dir = "meta", Type = "frame.dropped", Epoch = epoch, Detail = reason, Frame = frame, Chars = reason.Length + frame.Length });
    }

    /// <summary>One HTTPS call to the hub (`https.verify` or `https.answer`): what was sent, and the answer.</summary>
    public void Https(string type, string url, int status, string request, string body)
    {
        if (!enabled)
            return;
        Take(new Rec
        {
            Utc = utcNow(), Dir = "out", Type = type, Url = url, Status = status, Request = request, Body = body,
            Chars = type.Length + url.Length + request.Length + body.Length,
        });
    }

    private void Take(Rec r)
    {
        var records = Interlocked.Increment(ref queuedRecords);
        var chars = Interlocked.Add(ref queuedChars, r.Chars);
        if (records > MaxQueuedRecords || chars > MaxQueuedChars || !queue.Writer.TryWrite(r))
        {
            Interlocked.Decrement(ref queuedRecords);
            Interlocked.Add(ref queuedChars, -r.Chars);
            Interlocked.Increment(ref tapDropped);
        }
    }

    /// <summary>Waits up to `limit` for everything taken so far to be written and flushed.</summary>
    public bool Flush(TimeSpan limit)
    {
        var mark = new FlushMark();
        if (!queue.Writer.TryWrite(mark))
            return writer.Wait(limit);
        try
        {
            return mark.Done.Task.Wait(limit);
        }
        catch (AggregateException)
        {
            return true;
        }
    }

    /// <summary>Takes nothing more, writes what waits (up to `limit`) and closes the file.</summary>
    public bool Stop(TimeSpan limit)
    {
        enabled = false;
        queue.Writer.TryWrite(new Close());
        queue.Writer.TryComplete();
        try
        {
            return writer.Wait(limit);
        }
        catch (AggregateException)
        {
            return true;
        }
    }

    public void Dispose() => Stop(TimeSpan.FromSeconds(1));

    // ---- the writer's task -------------------------------------------------------------------

    private async Task WriteLoop(Task? hold)
    {
        var reader = queue.Reader;
        var batch = new List<Item>();
        try
        {
            if (hold is not null)
                await hold.ConfigureAwait(false);
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                batch.Clear();
                while (batch.Count < 4096 && reader.TryRead(out var item))
                    batch.Add(item);
                Handle(batch);
            }
        }
        catch (Exception e)
        {
            // Never reached by design; the log stops rather than throw anywhere.
            Warn(e);
        }
        CloseFile();
    }

    private void Handle(List<Item> batch)
    {
        var recs = new List<Rec>();
        foreach (var item in batch)
        {
            switch (item)
            {
                case Rec r:
                    Interlocked.Decrement(ref queuedRecords);
                    Interlocked.Add(ref queuedChars, -r.Chars);
                    recs.Add(r);
                    break;
                case Close:
                    Write(recs);
                    recs.Clear();
                    CloseFile();
                    break;
                case FlushMark f:
                    Write(recs);
                    recs.Clear();
                    f.Done.TrySetResult();
                    break;
            }
        }
        Write(recs);
    }

    private void Write(List<Rec> recs)
    {
        if (recs.Count == 0)
            return;
        if (Stopwatch.GetTimestamp() < Interlocked.Read(ref retryAt))
        {
            Interlocked.Add(ref dropped, recs.Count);
            return;
        }
        try
        {
            var lost = Interlocked.Read(ref dropped) + Interlocked.Exchange(ref tapDropped, 0);
            Interlocked.Exchange(ref dropped, lost);
            var text = new StringBuilder();
            var day = default(DateTime);
            foreach (var r in recs)
            {
                var d = r.Utc.UtcDateTime.Date;
                if (text.Length > 0 && d != day)
                {
                    WriteText(day, text);
                    text.Clear();
                }
                day = d;
                if (lost > 0)
                {
                    text.Append("{\"utc\":").Append(Json(Stamp(utcNow()))).Append(",\"dir\":\"meta\",\"type\":\"dropped\",\"count\":")
                        .Append(lost.ToString(CultureInfo.InvariantCulture)).Append("}\n");
                    lost = 0;
                }
                Line(text, r);
            }
            WriteText(day, text);
            file?.Flush();
            Interlocked.Exchange(ref dropped, 0);
        }
        catch (Exception e)
        {
            CloseFile();
            Interlocked.Add(ref dropped, recs.Count);
            Interlocked.Exchange(ref retryAt, Stopwatch.GetTimestamp() + (long)(retryWait.TotalSeconds * Stopwatch.Frequency));
            Warn(e);
        }
    }

    private void WriteText(DateTime day, StringBuilder text)
    {
        if (text.Length == 0)
            return;
        if (file is null || day != fileDay)
        {
            CloseFile();
            System.IO.Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "traffic-" + day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".jsonl");
            file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            fileDay = day;
        }
        var bytes = Encoding.ASCII.GetBytes(text.ToString());
        file.Write(bytes, 0, bytes.Length);
    }

    private void CloseFile()
    {
        var f = file;
        file = null;
        if (f is null)
            return;
        try
        {
            f.Dispose();
        }
        catch (Exception)
        {
            // Closing a file that could not be written; it is gone either way.
        }
    }

    private void Warn(Exception e)
    {
        var now = Stopwatch.GetTimestamp();
        if (warned && Stopwatch.GetElapsedTime(warnedAt, now) < WarnEvery)
            return;
        warned = true;
        warnedAt = now;
        try
        {
            log(LogLevel.Warn, $"the traffic log cannot be written to {directory} ({e.GetType().Name}: {Redact(e.Message)}); " +
                $"its records are dropped and counted, and it tries again in {retryWait.TotalSeconds:0.#} s");
        }
        catch (Exception)
        {
            // The log itself failing leaves nothing to tell.
        }
    }

    // ---- one line -----------------------------------------------------------------------------

    private void Line(StringBuilder text, Rec r)
    {
        text.Append("{\"utc\":").Append(Json(Stamp(r.Utc))).Append(",\"dir\":").Append(Json(r.Dir));
        if (r.Url is not null)
        {
            var request = Redact(r.Request ?? "");
            if (r.Type == "https.verify")
                request = RedactToken(request);
            text.Append(",\"type\":").Append(Json(r.Type ?? ""))
                .Append(",\"url\":").Append(Json(Redact(r.Url)))
                .Append(",\"status\":").Append(r.Status.ToString(CultureInfo.InvariantCulture))
                .Append(",\"request\":").Append(Json(request))
                .Append(",\"body\":").Append(Json(Redact(r.Body ?? "")))
                .Append("}\n");
            return;
        }
        var type = r.Type ?? FrameType(r.Frame ?? "");
        text.Append(",\"type\":").Append(Json(Redact(type))).Append(",\"epoch\":").Append(r.Epoch.ToString(CultureInfo.InvariantCulture));
        if (r.Detail is not null)
            text.Append(",\"detail\":").Append(Json(Redact(r.Detail)));
        if (r.Frame is not null)
        {
            var frame = Redact(r.Frame);
            if ((r.Type is null ? type : FrameType(r.Frame)) == "person.result")
                frame = RedactCodes(frame);
            text.Append(",\"frame\":").Append(Json(frame));
        }
        text.Append("}\n");
    }

    /// <summary>A record's time as ISO-8601 in UTC, to the millisecond.</summary>
    public static string Stamp(DateTimeOffset utc) =>
        utc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>A frame's `type`, read as JSON; `(unparsed)` when it is not an object with a string `type`.</summary>
    public static string FrameType(string frame)
    {
        try
        {
            using var doc = JsonDocument.Parse(frame);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("type", out var t)
                && t.ValueKind == JsonValueKind.String)
                return t.GetString() ?? "(unparsed)";
        }
        catch (Exception)
        {
            // Not JSON.
        }
        return "(unparsed)";
    }

    /// <summary>`s` as a JSON string, every character outside printable ASCII written as a \u escape.</summary>
    public static string Json(string s)
    {
        var b = new StringBuilder(s.Length + 2);
        b.Append('"');
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '"':
                    b.Append("\\\"");
                    break;
                case '\\':
                    b.Append("\\\\");
                    break;
                case '\n':
                    b.Append("\\n");
                    break;
                case '\r':
                    b.Append("\\r");
                    break;
                case '\t':
                    b.Append("\\t");
                    break;
                default:
                    if (ch < ' ' || ch > '~')
                        b.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        b.Append(ch);
                    break;
            }
        }
        b.Append('"');
        return b.ToString();
    }

    private string Redact(string s) => key is null ? s : s.Replace(key, "(key)", StringComparison.Ordinal);

    private static readonly Regex TokenField = new("(\"token\"\\s*:\\s*)\"(?:[^\"\\\\]|\\\\.)*\"", RegexOptions.CultureInvariant);

    /// <summary>A `/derethnetwork verify` request's body with its token replaced.</summary>
    public static string RedactToken(string body) => TokenField.Replace(body, "$1\"(token)\"");

    private const string CodeSymbol = "[0-9A-HJKMNP-TV-Z]";

    private static readonly Regex Code = new($"(?<![0-9A-Za-z]){CodeSymbol}{{4}}-{CodeSymbol}{{4}}(?![0-9A-Za-z])", RegexOptions.CultureInvariant);

    /// <summary>A frame with every link code (two groups of four of Crockford's base 32, hyphenated) replaced.</summary>
    public static string RedactCodes(string frame) => Code.Replace(frame, "(code)");
}
