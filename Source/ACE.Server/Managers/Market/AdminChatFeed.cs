using System;
using System.Collections.Generic;
using System.Threading;

using ACE.Entity.Enum;
using ACE.Server.Entity;

namespace ACE.Server.Managers.Market
{
    /// <summary>One captured public TurbineChat line (PLAN-P3.md section 2).</summary>
    public sealed record AdminChatLine(long Seq, DateTime At, string Channel, string Sender, string Text, bool Truncated);

    /// <summary>One page returned by <see cref="AdminChatFeed.Read"/> (PLAN-P3.md section 1.1).</summary>
    public readonly record struct AdminChatPage(string Generation, long HeadSeq, long OldestSeq, bool Gap, bool HasMore, long NextSince, IReadOnlyList<AdminChatLine> Lines);

    /// <summary>
    /// An in-memory-only ring buffer of the newest public TurbineChat lines (General, Trade, LFG,
    /// Roleplay), fed by <see cref="ACE.Server.Network.Handlers.TurbineChatHandler.LogTurbineChat"/>
    /// (PLAN-P3.md section 2, invariant 7). Never persisted: a restart empties the buffer and mints a
    /// new <see cref="Generation"/>, so a stale web cursor from a prior process is always answered
    /// with a gap rather than silently skipping lines.
    ///
    /// Capture MUST NEVER throw into the chat path, read PropertyManager, or log - the whole body of
    /// <see cref="Capture"/> is wrapped so a bug here can never break chat delivery (PLAN-P3.md trap
    /// 10, invariant 7).
    /// </summary>
    public sealed class AdminChatFeed
    {
        public const int Capacity = 500;
        public const int MaxPageLines = 200;
        public const int MaxTextChars = 1000;
        public const int MaxSenderChars = 64;

        public static AdminChatFeed Shared { get; } = new AdminChatFeed();

        /// <summary>Random per process, minted once. A restart changes it (see class remarks).</summary>
        public string Generation { get; }

        private readonly Func<DateTime> utcNow;
        private readonly AdminChatLine[] ring;
        private readonly object sync = new object();

        private int start;
        private int count;
        private long headSeq;
        private long dropped;

        public long DroppedCount => Interlocked.Read(ref dropped);

        public AdminChatFeed(int capacity = Capacity, Func<DateTime> utcNow = null)
        {
            this.utcNow = utcNow ?? (() => DateTime.UtcNow);
            ring = new AdminChatLine[capacity <= 0 ? 1 : capacity];
            Generation = Guid.NewGuid().ToString("N");
        }

        /// <summary>
        /// Maps a captured ChatType to its wire label. An explicit allowlist - Society,
        /// SocietyCelHan, SocietyEldWeb and SocietyRadBlo are FOUR separate members, and a default
        /// arm that produced a non-null label for any of them (or for Allegiance/Olthoi/Undef) would
        /// silently widen what the feed captures (PLAN-P3.md trap 9).
        /// </summary>
        internal static string ChannelLabel(ChatType t)
        {
            switch (t)
            {
                case ChatType.General:  return "general";
                case ChatType.Trade:    return "trade";
                case ChatType.LFG:      return "lfg";
                case ChatType.Roleplay: return "roleplay";
                default:                return null;
            }
        }

        /// <summary>The TurbineChatChannel id that must accompany each label - the privacy gate (PLAN-P3.md section 2, S1).</summary>
        private static bool ChannelIdMatches(ChatType t, uint channelID)
        {
            switch (t)
            {
                case ChatType.General:  return channelID == TurbineChatChannel.General;
                case ChatType.Trade:    return channelID == TurbineChatChannel.Trade;
                case ChatType.LFG:      return channelID == TurbineChatChannel.LFG;
                case ChatType.Roleplay: return channelID == TurbineChatChannel.Roleplay;
                default:                return false;
            }
        }

        public void Capture(ChatType chatType, uint channelID, string sender, string text)
        {
            try
            {
                var label = ChannelLabel(chatType);
                if (label == null)
                    return;

                if (!ChannelIdMatches(chatType, channelID))
                    return;

                if (string.IsNullOrEmpty(text))
                    return;

                var capturedAt = utcNow();

                var truncated = false;
                if (text.Length > MaxTextChars)
                {
                    var cut = MaxTextChars;
                    if (char.IsHighSurrogate(text[cut - 1]))
                        cut--;
                    text = text.Substring(0, cut);
                    truncated = true;
                }

                sender ??= string.Empty;
                if (sender.Length > MaxSenderChars)
                    sender = sender.Substring(0, MaxSenderChars);

                lock (sync)
                {
                    var seq = ++headSeq;
                    var line = new AdminChatLine(seq, capturedAt, label, sender, text, truncated);

                    var index = (start + count) % ring.Length;
                    ring[index] = line;

                    if (count < ring.Length)
                        count++;
                    else
                        start = (start + 1) % ring.Length;
                }
            }
            catch (Exception)
            {
                Interlocked.Increment(ref dropped);
            }
        }

        public AdminChatPage Read(long? since, string generation, int? limit)
        {
            var lim = limit ?? MaxPageLines;
            if (lim < 1 || lim > MaxPageLines)
                lim = MaxPageLines;

            lock (sync)
            {
                var oldestSeq = count == 0 ? 0 : headSeq - count + 1;

                if (since == null || since.Value < 0)
                {
                    var tailFrom = Math.Max(oldestSeq, headSeq - lim + 1);
                    var tailLines = CopySeqRange(tailFrom, headSeq, oldestSeq, lim);
                    return new AdminChatPage(Generation, headSeq, oldestSeq, false, false, headSeq, tailLines);
                }

                var sinceValue = since.Value;
                var generationOk = !string.IsNullOrEmpty(generation) && string.Equals(generation, Generation, StringComparison.Ordinal);

                bool gap;
                long fromSeq;

                if (!generationOk)
                {
                    gap = true;
                    fromSeq = oldestSeq;
                }
                else if (sinceValue > headSeq)
                {
                    gap = true;
                    fromSeq = oldestSeq;
                }
                else if (sinceValue < oldestSeq - 1)
                {
                    gap = true;
                    fromSeq = oldestSeq;
                }
                else
                {
                    gap = false;
                    fromSeq = sinceValue + 1;
                }

                var lines = CopySeqRange(fromSeq, headSeq, oldestSeq, lim);

                long nextSince;
                if (lines.Count > 0)
                    nextSince = lines[lines.Count - 1].Seq;
                else if (gap)
                    nextSince = count == 0 ? 0 : oldestSeq - 1;
                else
                    nextSince = sinceValue;

                var hasMore = lines.Count > 0 && nextSince < headSeq;

                return new AdminChatPage(Generation, headSeq, oldestSeq, gap, hasMore, nextSince, lines);
            }
        }

        /// <summary>Copies at most <paramref name="limit"/> lines starting at <paramref name="fromSeq"/> (clamped to <paramref name="oldestSeq"/>) up to <paramref name="headSeq"/>. Must be called under <see cref="sync"/>.</summary>
        private List<AdminChatLine> CopySeqRange(long fromSeq, long headSeq, long oldestSeq, int limit)
        {
            if (headSeq <= 0 || fromSeq > headSeq)
                return new List<AdminChatLine>();

            if (fromSeq < oldestSeq)
                fromSeq = oldestSeq;

            var available = headSeq - fromSeq + 1;
            var take = (int)Math.Min(available, limit);

            var result = new List<AdminChatLine>(take);
            for (var i = 0; i < take; i++)
            {
                var seq = fromSeq + i;
                var index = (int)((start + (seq - oldestSeq)) % ring.Length);
                result.Add(ring[index]);
            }

            return result;
        }
    }
}
