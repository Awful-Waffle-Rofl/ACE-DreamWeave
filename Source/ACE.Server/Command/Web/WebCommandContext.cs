using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

using ACE.Server.Network;
using ACE.Server.WorldObjects;

namespace ACE.Server.Command.Web
{
    /// <summary>One captured output line (AdminCommandOutputLine in market-api-v1.yaml).</summary>
    public sealed class WebCommandOutputLine
    {
        public string Level { get; init; }
        public string Text { get; init; }
    }

    /// <summary>
    /// The per-run context of one web command (PLAN-P4.md section 3.1): who is acting, which session
    /// the run's output is addressed to (null for the web bucket, the character's session for the
    /// in-game character bucket), and the capped line buffer.
    ///
    /// It lives in an AsyncLocal, and AsyncLocal values flow into every thread, task and timer a
    /// handler starts while the value is set. Two things make that safe. The scope's Dispose seals the
    /// context, and every consumer reads it through <see cref="Current"/>, which answers null for a
    /// sealed context - so a thread the handler started keeps no attribution and captures nothing once
    /// the run has ended, even though it still holds the value. And each run owns its own buffer, so a
    /// timed-out run's late output can only ever land in its own (already answered) buffer, never in
    /// another run's.
    /// </summary>
    internal sealed class WebCommandContext
    {
        public const int MaxLines = 500;
        public const int MaxBytes = 64 * 1024;

        public const string LevelInfo = "info";
        public const string LevelDebug = "debug";
        public const string LevelError = "error";
        public const string LevelChat = "chat";

        private static readonly AsyncLocal<WebCommandContext> current = new AsyncLocal<WebCommandContext>();
        private static int activeCount;

        private readonly object sync = new object();
        private readonly List<WebCommandOutputLine> lines = new List<WebCommandOutputLine>();
        private int bytes;
        private bool truncated;
        private int sealedFlag;
        private int entered;

        public WebCommandContext(uint accountId, string accountName, string actorLabel, Session session, Player player)
        {
            AccountId = accountId;
            AccountName = accountName ?? string.Empty;
            ActorLabel = actorLabel ?? AccountName;
            Session = session;
            Player = player;
        }

        public uint AccountId { get; }
        public string AccountName { get; }

        /// <summary>The Audit channel sender for this run's lines.</summary>
        public string ActorLabel { get; }

        /// <summary>Null for the web bucket.</summary>
        public Session Session { get; }

        /// <summary>Null for the web bucket.</summary>
        public Player Player { get; }

        public bool IsSealed => Volatile.Read(ref sealedFlag) != 0;

        /// <summary>Contexts entered and not yet disposed, process-wide. Test seam.</summary>
        internal static int ActiveCount => Volatile.Read(ref activeCount);

        /// <summary>The hot-path gate for the send chokepoint: one int read, no allocation.</summary>
        public static bool IsCapturing => Volatile.Read(ref activeCount) != 0;

        /// <summary>The live context on this execution flow, or null when there is none or it is sealed.</summary>
        public static WebCommandContext Current
        {
            get
            {
                var ctx = current.Value;
                return ctx == null || ctx.IsSealed ? null : ctx;
            }
        }

        /// <summary>
        /// Sets this execution flow's context. Dispose the returned scope in a finally ON THE SAME THREAD:
        /// an AsyncLocal set inside a synchronous method stays set on that thread after the method
        /// returns, so only the scope's Dispose clears it. A context can be entered once.
        /// </summary>
        public static IDisposable Enter(WebCommandContext ctx)
        {
            if (ctx == null)
                throw new ArgumentNullException(nameof(ctx));

            if (Interlocked.Exchange(ref ctx.entered, 1) != 0 || ctx.IsSealed)
                throw new InvalidOperationException("A web command context can be entered only once.");

            var previous = current.Value;
            current.Value = ctx;
            Interlocked.Increment(ref activeCount);

            return new Scope(ctx, previous);
        }

        /// <summary>
        /// Captures one line written to <paramref name="session"/> (null for the null-session WriteOutput*
        /// branch) when this flow's live context is addressed to exactly that session. Never captures a
        /// line addressed to any other session.
        /// </summary>
        public static bool TryCapture(Session session, string level, string text)
        {
            if (Volatile.Read(ref activeCount) == 0)
                return false;

            var ctx = Current;

            if (ctx == null || !ReferenceEquals(ctx.Session, session))
                return false;

            return ctx.Append(level, text, ignoreSeal: false);
        }

        /// <summary>A line the dispatcher itself produces (usage text). Respects the cap, not the seal.</summary>
        internal void AddOwnLine(string level, string text) => Append(level, text, ignoreSeal: true);

        /// <summary>
        /// The last line of an exception result. It must end the output even when the buffer is full, so
        /// when it would not fit it replaces the last buffered line and the caps still hold.
        /// </summary>
        internal void AddFinalLine(string level, string text)
        {
            var line = new WebCommandOutputLine { Level = level, Text = text ?? string.Empty };
            var size = Encoding.UTF8.GetByteCount(line.Text);

            lock (sync)
            {
                while (lines.Count > 0 && (lines.Count + 1 > MaxLines || bytes + size > MaxBytes))
                {
                    bytes -= Encoding.UTF8.GetByteCount(lines[lines.Count - 1].Text);
                    lines.RemoveAt(lines.Count - 1);
                    truncated = true;
                }

                lines.Add(line);
                bytes += size;
            }
        }

        public (List<WebCommandOutputLine> Lines, bool Truncated) Snapshot()
        {
            lock (sync)
                return (new List<WebCommandOutputLine>(lines), truncated);
        }

        private bool Append(string level, string text, bool ignoreSeal)
        {
            text ??= string.Empty;
            var size = Encoding.UTF8.GetByteCount(text);

            lock (sync)
            {
                if (!ignoreSeal && sealedFlag != 0)
                    return false;

                if (truncated || lines.Count >= MaxLines || bytes + size > MaxBytes)
                {
                    truncated = true;
                    return false;
                }

                lines.Add(new WebCommandOutputLine { Level = level, Text = text });
                bytes += size;
                return true;
            }
        }

        private void Seal()
        {
            lock (sync)
                sealedFlag = 1;
        }

        private sealed class Scope : IDisposable
        {
            private readonly WebCommandContext ctx;
            private readonly WebCommandContext previous;
            private int disposed;

            public Scope(WebCommandContext ctx, WebCommandContext previous)
            {
                this.ctx = ctx;
                this.previous = previous;
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0)
                    return;

                // Seal FIRST: from here on every flow still holding this value (threads the handler
                // started) reads Current as null.
                ctx.Seal();
                current.Value = previous;
                Interlocked.Decrement(ref activeCount);
            }
        }
    }
}
