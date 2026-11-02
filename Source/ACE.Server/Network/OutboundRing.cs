using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;

namespace ACE.Server.Network
{
    /// <summary>
    /// One recorded outbound message. <see cref="Index"/> is both the slot's seqlock and the entry's position in the
    /// session's outbound stream (1-based; 0 means the slot was never written, and minus an index means that entry is
    /// being written). <see cref="Stamp"/> is a
    /// <see cref="Stopwatch.GetTimestamp"/> value. <see cref="PacketSeq"/> is the sequence of the packet that carried
    /// the message's last piece, 0 until that packet is stamped (a message still held by the pacer has none).
    /// </summary>
    public struct OutboundRingEntry
    {
        public long Index;
        public long Stamp;
        public uint Guid;
        public uint Aux;
        public int Size;
        public uint PacketSeq;
        public ushort Opcode;
        public ushort EventType;
    }

    /// <summary>The client's last inbound packet, as a position in the outbound stream and a Stopwatch timestamp.</summary>
    public readonly struct OutboundRingAnchor
    {
        public readonly long Index;
        public readonly long Stamp;

        public OutboundRingAnchor(long index, long stamp)
        {
            Index = index;
            Stamp = stamp;
        }
    }

    /// <summary>A consistent copy of a ring: the entries still held, in index order, plus its counters.</summary>
    public sealed class OutboundRingSnapshot
    {
        public List<OutboundRingEntry> Entries;
        public OutboundRingAnchor Anchor;
        public int Capacity;
        public long Written;
        public long Suppressed;
        public long Dropped;
        public long Caps;
    }

    /// <summary>
    /// Per-session record of recently sent messages (diagnostics only: nothing reads it to decide anything).
    /// <para>Writes are lock-free and allocation-free after the first one, which allocates the entry array at the
    /// capacity the caller passes. The writer is normally the session's single outbound thread, but the connection
    /// listener thread can overlap it at login, so a slot is claimed with Interlocked and published through a seqlock:
    /// the slot's Index is set to minus the claimed index (the writing mark), the fields are written, then the Index is
    /// published. A writer never publishes over a newer entry in its slot. A reader keeps a slot only when the Index
    /// it saw before and after copying is the index it expected.</para>
    /// <para>Post-anchor cap: once half the capacity has been written since the client's last inbound packet, further
    /// records are dropped and counted (<see cref="Suppressed"/>, and <see cref="Caps"/> once per capping episode)
    /// until the next inbound packet moves the anchor. So the half of the ring before the anchor survives however much
    /// the server keeps sending to a client that has gone quiet.</para>
    /// <para>This class reads no settings; everything it needs is passed in.</para>
    /// </summary>
    public sealed class OutboundRing
    {
        private OutboundRingEntry[] entries;

        private long cursor;            // index of the last claimed slot
        private long suppressed;
        private long caps;
        private long lastCapAnchor = -1;
        private long dropped;

        // SpinOnce calls a writer spends waiting for an older holder of its slot before it drops its record. An
        // instance field (not a constant) so tests can raise it to observe the wait itself.
        private int spinBudget = 64;

        // anchor seqlock: odd version = being written
        private long anchorVersion;
        private long anchorIndex;
        private long anchorStamp;

        /// <summary>True once the first record allocated the entry array.</summary>
        public bool Allocated => Volatile.Read(ref entries) != null;

        public int Capacity => Volatile.Read(ref entries)?.Length ?? 0;

        public long Written => Interlocked.Read(ref cursor);

        public long Suppressed => Interlocked.Read(ref suppressed);

        public long Caps => Interlocked.Read(ref caps);

        /// <summary>
        /// Records that claimed an index but were not stored: lapped by a newer entry in their slot, or out of spin
        /// budget waiting for an older holder of it. Counted within <see cref="Written"/>, separately from
        /// <see cref="Suppressed"/>.
        /// </summary>
        public long Dropped => Interlocked.Read(ref dropped);

        /// <summary>
        /// Records one message. Returns its index, or 0 when the post-anchor cap suppressed it. A dropped record
        /// (<see cref="Dropped"/>) still returns its index; NotePacketSeq for it is a no-op.
        /// <paramref name="capacity"/> is used only by the first record (it must be a power of two, at least 2).
        /// </summary>
        public long Record(long stamp, ushort opcode, ushort eventType, uint guid, uint aux, int size, uint packetSeq, int capacity)
        {
            var e = Volatile.Read(ref entries) ?? Allocate(capacity);

            var half = e.Length >> 1;
            var anchor = Volatile.Read(ref anchorIndex);

            if (Volatile.Read(ref cursor) - anchor >= half)
            {
                Interlocked.Increment(ref suppressed);

                if (Interlocked.Exchange(ref lastCapAnchor, anchor) != anchor)
                    Interlocked.Increment(ref caps);

                return 0;
            }

            var index = Interlocked.Increment(ref cursor);

            ref var slot = ref e[index & (e.Length - 1)];

            // Take the slot by moving its Index from a published older entry (or 0, never written) to -index, the
            // writing mark. The CAS is a full fence: the field writes below cannot move ahead of it. A writer stalled
            // between its claim and here can be lapped by writers a whole capacity ahead; it must never publish over
            // their newer entry, so it drops its own record instead (that entry is already overwritten, and
            // NotePacketSeq for its index is a no-op). A newer writer that finds an older one (or a packet-sequence
            // note) mid-write waits for it to publish, so two writers never write the same slot's fields at once. The
            // wait is bounded by a spin budget, then dropped and counted, so a stalled holder never parks the
            // outbound thread. Both drops count in Dropped, not Suppressed: the index was claimed, so it is Written.
            var spins = new SpinWait();
            var budget = spinBudget;
            while (true)
            {
                var held = Volatile.Read(ref slot.Index);

                if (held > index || -held > index)
                {
                    Interlocked.Increment(ref dropped);
                    return index;
                }

                if (held < 0)
                {
                    if (budget-- <= 0)
                    {
                        Interlocked.Increment(ref dropped);
                        return index;
                    }

                    spins.SpinOnce(-1);
                    continue;
                }

                if (Interlocked.CompareExchange(ref slot.Index, -index, held) == held)
                    break;
            }

            slot.Stamp = stamp;
            slot.Guid = guid;
            slot.Aux = aux;
            slot.Size = size;
            slot.PacketSeq = packetSeq;
            slot.Opcode = opcode;
            slot.EventType = eventType;
            Volatile.Write(ref slot.Index, index);

            return index;
        }

        private OutboundRingEntry[] Allocate(int capacity)
        {
            if (capacity < 2 || (capacity & (capacity - 1)) != 0)
                throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "must be a power of two, at least 2");

            return Interlocked.CompareExchange(ref entries, new OutboundRingEntry[capacity], null) ?? Volatile.Read(ref entries);
        }

        /// <summary>
        /// Sets the packet sequence of an entry still held in the ring. A no-op when the slot has moved on or is being
        /// written. The slot is held under the writing mark for the one write, with a single CAS and no wait, so a
        /// writer taking the slot for a newer entry waits for the note instead of having its entry stamped with this
        /// stale sequence.
        /// </summary>
        public void NotePacketSeq(long index, uint packetSeq)
        {
            var e = Volatile.Read(ref entries);
            if (e == null || index <= 0)
                return;

            ref var slot = ref e[index & (e.Length - 1)];
            if (Interlocked.CompareExchange(ref slot.Index, -index, index) != index)
                return;

            slot.PacketSeq = packetSeq;
            Volatile.Write(ref slot.Index, index);
        }

        /// <summary>Called for every inbound datagram: the anchor becomes the current end of the outbound stream.</summary>
        public void NoteInbound(long stamp)
        {
            // take the version from even to odd (full fence); a second concurrent caller waits for the first, so the
            // version can never be left odd
            long version;
            var spins = new SpinWait();
            while (((version = Volatile.Read(ref anchorVersion)) & 1) != 0 || Interlocked.CompareExchange(ref anchorVersion, version + 1, version) != version)
                spins.SpinOnce();

            Volatile.Write(ref anchorStamp, stamp);
            Volatile.Write(ref anchorIndex, Volatile.Read(ref cursor));
            Volatile.Write(ref anchorVersion, version + 2);
        }

        public OutboundRingAnchor ReadAnchor()
        {
            var spins = new SpinWait();

            while (true)
            {
                var before = Volatile.Read(ref anchorVersion);
                var index = Volatile.Read(ref anchorIndex);
                var stamp = Volatile.Read(ref anchorStamp);
                Interlocked.MemoryBarrier();
                var after = Volatile.Read(ref anchorVersion);

                if ((before & 1) == 0 && before == after)
                    return new OutboundRingAnchor(index, stamp);

                spins.SpinOnce();
            }
        }

        /// <summary>Copies every slot still holding a complete entry, in index order, plus the counters. Off the hot path.</summary>
        public OutboundRingSnapshot Snapshot()
        {
            var snapshot = new OutboundRingSnapshot
            {
                Anchor = ReadAnchor(),
                Suppressed = Suppressed,
                Dropped = Dropped,
                Caps = Caps,
            };

            var e = Volatile.Read(ref entries);
            var end = Interlocked.Read(ref cursor);

            snapshot.Written = end;
            snapshot.Capacity = e?.Length ?? 0;
            snapshot.Entries = new List<OutboundRingEntry>(e == null ? 0 : (int)Math.Min(end, e.Length));

            if (e == null)
                return snapshot;

            var start = Math.Max(1, end - e.Length + 1);

            for (var index = start; index <= end; index++)
            {
                ref var slot = ref e[index & (e.Length - 1)];

                // a slot marked -index holds this very entry under a brief packet-sequence note (or its own write
                // finishing): re-read a few times before skipping it, so a complete entry does not vanish
                for (var attempt = 0; ; attempt++)
                {
                    var before = Volatile.Read(ref slot.Index);
                    var copy = slot;
                    Interlocked.MemoryBarrier();
                    var after = Volatile.Read(ref slot.Index);

                    if (before == index && after == index)
                    {
                        copy.Index = index;
                        snapshot.Entries.Add(copy);
                        break;
                    }

                    if (attempt >= 4 || (before != -index && after != -index))
                        break;

                    Thread.SpinWait(20);
                }
            }

            return snapshot;
        }

        /// <summary>
        /// The entries a dump shows: up to <paramref name="dumpEntries"/>/4 entries just after the anchor, then, up to
        /// <paramref name="dumpEntries"/> in all, the entries just before it that fall within
        /// <paramref name="preWindowTicks"/> (Stopwatch ticks) of the anchor. Output in index order.
        /// <paramref name="snapshot"/> must be in index order.
        /// </summary>
        public static List<OutboundRingEntry> Select(IReadOnlyList<OutboundRingEntry> snapshot, OutboundRingAnchor anchor, int dumpEntries, long preWindowTicks)
        {
            var result = new List<OutboundRingEntry>();
            if (snapshot == null || dumpEntries <= 0)
                return result;

            // first entry after the anchor
            var split = 0;
            while (split < snapshot.Count && snapshot[split].Index <= anchor.Index)
                split++;

            var postCount = Math.Min(dumpEntries / 4, snapshot.Count - split);
            var preRoom = dumpEntries - postCount;

            var preFirst = split;
            while (preFirst > 0 && split - preFirst < preRoom && anchor.Stamp - snapshot[preFirst - 1].Stamp <= preWindowTicks)
                preFirst--;

            for (var i = preFirst; i < split + postCount; i++)
                result.Add(snapshot[i]);

            return result;
        }

        /// <summary>
        /// True when the ring no longer holds every entry <see cref="Select"/> could want for this anchor: the
        /// <paramref name="dumpEntries"/> - <paramref name="dumpEntries"/>/4 entries up to the anchor and the
        /// <paramref name="dumpEntries"/>/4 after it, of those that were written. The post-anchor cap protects the
        /// LIVE anchor only, so a control dump taken against an anchor recorded earlier can find its window overwritten
        /// once more than capacity minus that window has been written since; the dump is still taken, flagged
        /// evicted=1. Also true when the wanted window is larger than the ring could ever hold.
        /// </summary>
        public static bool Evicted(long written, int capacity, OutboundRingAnchor anchor, int dumpEntries)
        {
            if (capacity <= 0 || written <= 0)
                return false;

            var preRoom = dumpEntries - dumpEntries / 4;
            var firstWanted = Math.Max(1, anchor.Index - preRoom + 1);
            var oldestHeld = Math.Max(1, written - capacity + 1);

            return oldestHeld > firstWanted;
        }

        /// <summary>The 30 second pre-anchor window, in Stopwatch ticks.</summary>
        public static long DefaultPreWindowTicks => 30 * Stopwatch.Frequency;
    }

    /// <summary>Everything a dump header shows, captured synchronously when the dump is taken.</summary>
    public struct OutboundRingDumpHeader
    {
        public string Rid;
        public char Kind;
        public string Player;
        public string Account;
        public uint Cell;
        public bool Teleporting;
        public long SinceInboundMs;     // -1: no inbound packet yet
        public long ConnectionSeconds;
        public int Capacity;
        public long Written;
        public long Suppressed;
        public long Caps;
        public uint ServerSequence;
        public uint ClientAck;
        public long AckAgoMs;           // -1: no ack yet
        public long ResendMisses;
        public long CrcFailures;
        public int Shown;
        public bool RateSkipped;
        public bool Evicted;            // some entries the selection wanted were already overwritten (OutboundRing.Evicted)
    }

    /// <summary>The [OUTBOUND_RING] log lines. ASCII only, fixed field order.</summary>
    public static class OutboundRingFormat
    {
        public const string Tag = "[OUTBOUND_RING]";

        public static string Header(in OutboundRingDumpHeader h)
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder(256);

            sb.Append(Tag).Append(" rid=").Append(h.Rid).Append(" k=").Append(h.Kind).Append(" hdr=1");
            sb.Append(" player=\"").Append(Ascii(h.Player, true)).Append('"');
            sb.Append(" acct=").Append(Ascii(h.Account, false));
            sb.Append(" cell=0x").Append(h.Cell.ToString("X8", inv));
            sb.Append(" teleporting=").Append(h.Teleporting ? '1' : '0');
            sb.Append(" sinceInMs=").Append(h.SinceInboundMs.ToString(inv));
            sb.Append(" connS=").Append(h.ConnectionSeconds.ToString(inv));
            sb.Append(" ring=").Append(h.Capacity.ToString(inv));
            sb.Append(" written=").Append(h.Written.ToString(inv));
            sb.Append(" suppressed=").Append(h.Suppressed.ToString(inv));
            sb.Append(" caps=").Append(h.Caps.ToString(inv));
            sb.Append(" seq=").Append(h.ServerSequence.ToString(inv));
            sb.Append(" ack=").Append(h.ClientAck.ToString(inv));
            sb.Append(" ackAgoMs=").Append(h.AckAgoMs.ToString(inv));
            sb.Append(" resendMiss=").Append(h.ResendMisses.ToString(inv));
            sb.Append(" crcFail=").Append(h.CrcFailures.ToString(inv));
            sb.Append(" shown=").Append(h.Shown.ToString(inv));

            if (h.RateSkipped)
                sb.Append(" skipped=rate");

            sb.Append(" evicted=").Append(h.Evicted ? '1' : '0');

            return sb.ToString();
        }

        /// <summary>
        /// One entry line. <paramref name="ack"/> is the client's cumulative ack; the entry counts as acked when its
        /// packet was stamped and the ack has reached it. <paramref name="wcid"/> 0 omits w=.
        /// </summary>
        public static string Entry(string rid, char kind, in OutboundRingEntry entry, long anchorStamp, uint ack, uint wcid)
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder(128);

            var relMs = (long)Math.Round((entry.Stamp - anchorStamp) * 1000.0 / Stopwatch.Frequency, MidpointRounding.AwayFromZero);

            sb.Append(Tag).Append(" rid=").Append(rid).Append(" k=").Append(kind);
            sb.Append(" i=").Append(entry.Index.ToString(inv));
            sb.Append(" rel=").Append(relMs.ToString(inv));
            sb.Append(" op=").Append(entry.Opcode.ToString("X4", inv));
            sb.Append(" sz=").Append(entry.Size.ToString(inv));

            if (entry.EventType != 0)
                sb.Append(" ev=").Append(entry.EventType.ToString("X4", inv));
            if (entry.Guid != 0)
                sb.Append(" g=").Append(entry.Guid.ToString("X8", inv));
            if (entry.Aux != 0)
                sb.Append(" a=").Append(entry.Aux.ToString("X8", inv));
            if (wcid != 0)
                sb.Append(" w=").Append(wcid.ToString(inv));

            sb.Append(" ackd=").Append(entry.PacketSeq != 0 && ack >= entry.PacketSeq ? '1' : '0');

            return sb.ToString();
        }

        /// <summary>Printable ASCII only: anything else becomes '?'. Quotes always, and spaces outside a quoted field, become '_'.</summary>
        internal static string Ascii(string text, bool quoted)
        {
            if (string.IsNullOrEmpty(text))
                return "-";

            var chars = new char[text.Length];
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (c < 0x20 || c > 0x7E)
                    c = '?';
                else if (c == '"' || (c == ' ' && !quoted))
                    c = '_';
                chars[i] = c;
            }

            return new string(chars);
        }
    }
}
