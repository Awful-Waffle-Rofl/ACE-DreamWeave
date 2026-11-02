using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

using ACE.Server.Network;
using ACE.Server.Network.Enum;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.Outbound
{
    /// <summary>
    /// The outbound ring core (OutboundRing), its entry selection, its log format, and the pure pieces of the dump
    /// triggers (OutboundRingMonitor.DropKind, the rate limiter, the rid).
    /// </summary>
    [TestClass]
    public class OutboundRingTests
    {
        private static long Record(OutboundRing ring, uint guid, int capacity = 64, long stamp = 0)
        {
            return ring.Record(stamp, 0xF745, 0, guid, guid ^ 0xA5A5A5A5u, (int)(guid & 0xFFFF), 0, capacity);
        }

        [TestMethod]
        public void Wraparound_KeepsTheNewestCapacityEntries_InIndexOrder()
        {
            var ring = new OutboundRing();

            for (uint i = 1; i <= 200; i++)
            {
                Record(ring, i);
                ring.NoteInbound(i); // keep the post-anchor cap out of the way
            }

            var snapshot = ring.Snapshot();

            Assert.AreEqual(64, snapshot.Capacity);
            Assert.AreEqual(200, snapshot.Written);
            Assert.AreEqual(64, snapshot.Entries.Count);
            CollectionAssert.AreEqual(Enumerable.Range(137, 64).Select(i => (long)i).ToList(), snapshot.Entries.Select(e => e.Index).ToList());
            Assert.AreEqual(0, snapshot.Suppressed);
        }

        [TestMethod]
        public void Record_StoresEveryField_AndEmptyRingSnapshotsEmpty()
        {
            var ring = new OutboundRing();
            Assert.IsFalse(ring.Allocated);
            Assert.AreEqual(0, ring.Snapshot().Entries.Count);

            var index = ring.Record(12345, 0xF7B0, 0x02C9, 0x80001234, 0x2A, 96, 77, 64);
            Assert.AreEqual(1L, index);
            Assert.IsTrue(ring.Allocated);

            var e = ring.Snapshot().Entries.Single();
            Assert.AreEqual(1L, e.Index);
            Assert.AreEqual(12345L, e.Stamp);
            Assert.AreEqual((ushort)0xF7B0, e.Opcode);
            Assert.AreEqual((ushort)0x02C9, e.EventType);
            Assert.AreEqual(0x80001234u, e.Guid);
            Assert.AreEqual(0x2Au, e.Aux);
            Assert.AreEqual(96, e.Size);
            Assert.AreEqual(77u, e.PacketSeq);
        }

        [TestMethod]
        public void Capacity_MustBeAPowerOfTwo()
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new OutboundRing().Record(0, 1, 0, 0, 0, 0, 0, 100));
        }

        [TestMethod]
        public void PostAnchorCap_FreezesAtHalf_CountsSuppressedAndCaps_AndThawsOnInbound()
        {
            var ring = new OutboundRing();

            // no inbound yet: the anchor is 0, so only 32 of 100 go in
            for (uint i = 1; i <= 100; i++)
                Record(ring, i);

            Assert.AreEqual(32, ring.Written);
            Assert.AreEqual(68, ring.Suppressed);
            Assert.AreEqual(1, ring.Caps, "one capping episode");
            Assert.AreEqual(0L, Record(ring, 101), "suppressed records return 0");

            // a new inbound packet thaws it: 32 more fit after the new anchor, then it caps again (a second episode)
            ring.NoteInbound(1);
            for (uint i = 200; i < 240; i++)
                Record(ring, i);

            Assert.AreEqual(64, ring.Written);
            Assert.AreEqual(69 + 8, ring.Suppressed);
            Assert.AreEqual(2, ring.Caps);
        }

        [TestMethod]
        public void PostAnchorCap_ThePreAnchorHalfSurvivesAnyAmountOfSending()
        {
            var ring = new OutboundRing();

            for (uint i = 1; i <= 100; i++)
            {
                Record(ring, i);
                ring.NoteInbound(i);
            }

            // the client goes quiet at index 100; the server keeps sending
            for (uint i = 1000; i < 11000; i++)
                Record(ring, i);

            var snapshot = ring.Snapshot();
            var anchor = snapshot.Anchor.Index;

            Assert.AreEqual(100L, anchor);
            Assert.AreEqual(32, snapshot.Entries.Count(e => e.Index <= anchor), "the 32 entries before the anchor are intact");
            Assert.AreEqual(32, snapshot.Entries.Count(e => e.Index > anchor));
            Assert.AreEqual(10000 - 32, snapshot.Suppressed);
            Assert.AreEqual(1, snapshot.Caps);
        }

        [TestMethod]
        public void NoteInbound_AnchorIsTheCurrentEndOfTheStream()
        {
            var ring = new OutboundRing();
            Assert.AreEqual(0L, ring.ReadAnchor().Index);

            Record(ring, 1);
            Record(ring, 2);
            ring.NoteInbound(555);

            var anchor = ring.ReadAnchor();
            Assert.AreEqual(2L, anchor.Index);
            Assert.AreEqual(555L, anchor.Stamp);
        }

        [TestMethod]
        public void NotePacketSeq_SetsOnlyTheEntryStillInItsSlot()
        {
            var ring = new OutboundRing();
            var first = Record(ring, 1);
            ring.NoteInbound(1);

            ring.NotePacketSeq(first, 900);
            Assert.AreEqual(900u, ring.Snapshot().Entries.Single().PacketSeq);

            // overwrite slot 1 (index 65), then a stale note for index 1 must not touch it
            for (uint i = 2; i <= 65; i++)
            {
                Record(ring, i);
                ring.NoteInbound(i);
            }

            ring.NotePacketSeq(first, 901);
            Assert.AreEqual(0u, ring.Snapshot().Entries.Single(e => e.Index == 65).PacketSeq);

            ring.NotePacketSeq(0, 5);           // no-ops
            new OutboundRing().NotePacketSeq(1, 5);
        }

        [TestMethod]
        public void LappedWriter_NeverPublishesOverTheNewerEntryInItsSlot()
        {
            var ring = new OutboundRing();

            for (uint i = 1; i <= 100; i++)
            {
                Record(ring, i);
                ring.NoteInbound(i);
            }

            // stand in for a writer that claimed index 36 a whole lap ago and stalled before taking its slot: slot 36
            // now holds index 100, so the late writer must drop its record rather than publish 36 over 100
            var cursor = typeof(OutboundRing).GetField("cursor", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.IsNotNull(cursor, "OutboundRing.cursor renamed: update this test");
            cursor.SetValue(ring, 35L);

            Assert.AreEqual(36L, Record(ring, 0xDEAD));

            cursor.SetValue(ring, 100L);

            var snapshot = ring.Snapshot();
            Assert.AreEqual(64, snapshot.Entries.Count, "the newer entry survives the late writer");
            var held = snapshot.Entries.Single(e => e.Index == 100);
            Assert.AreEqual(100u, held.Guid);
            Assert.IsFalse(snapshot.Entries.Any(e => e.Guid == 0xDEAD));
            Assert.AreEqual(1, snapshot.Dropped, "the lapped record is counted as dropped");
        }

        private static T Field<T>(OutboundRing ring, string name)
        {
            var field = typeof(OutboundRing).GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.IsNotNull(field, $"OutboundRing.{name} renamed: update this test");
            return (T)field.GetValue(ring);
        }

        private static void SetField(OutboundRing ring, string name, object value)
        {
            var field = typeof(OutboundRing).GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.IsNotNull(field, $"OutboundRing.{name} renamed: update this test");
            field.SetValue(ring, value);
        }

        /// <summary>A 64-slot ring whose next claim is index 69 (slot 5), with the post-anchor cap out of the way.</summary>
        private static OutboundRingEntry[] RingAboutToClaim69(OutboundRing ring)
        {
            Record(ring, 1);
            SetField(ring, "cursor", 68L);
            ring.NoteInbound(0);
            return Field<OutboundRingEntry[]>(ring, "entries");
        }

        [TestMethod]
        public void SlotHeldByANewerWriter_TheOlderRecordIsDroppedAndCounted_AndTheMarkIsUntouched()
        {
            var ring = new OutboundRing();
            var e = RingAboutToClaim69(ring);
            e[5].Index = -200;

            Assert.AreEqual(69L, Record(ring, 0xBEEF));

            Assert.AreEqual(-200L, Volatile.Read(ref e[5].Index));
            Assert.AreNotEqual(0xBEEFu, e[5].Guid);
            Assert.AreEqual(1, ring.Dropped);
            Assert.AreEqual(0, ring.Suppressed, "a drop is not a suppression: the index was claimed");
            Assert.AreEqual(1, ring.Snapshot().Dropped);
        }

        [TestMethod]
        public void SlotHeldByAnOlderWriter_TheNewerRecordWaitsForItsPublish_ThenPublishes()
        {
            var ring = new OutboundRing();
            var e = RingAboutToClaim69(ring);
            SetField(ring, "spinBudget", int.MaxValue); // observe the wait itself, not the budget
            e[5].Index = -5;

            var result = 0L;
            var started = 0;
            var writer = new Thread(() =>
            {
                Volatile.Write(ref started, 1);
                Interlocked.Exchange(ref result, Record(ring, 0xBEEF));
            }) { IsBackground = true }; // an unbounded budget must never outlive the test run

            try
            {
                writer.Start();

                // the dwell starts only once the writer has claimed index 69: from there, until it returns, the only
                // place it can be is the wait for the slot (it has not taken the slot, dropped, or returned)
                var deadline = Stopwatch.StartNew();
                while ((Volatile.Read(ref started) == 0 || ring.Written != 69) && deadline.ElapsedMilliseconds < 5000)
                    Thread.SpinWait(100);

                Assert.AreEqual(69L, ring.Written, "the writer claimed index 69 before the dwell");

                Assert.IsFalse(writer.Join(50), "the newer writer waits while the older one holds the slot");
                Assert.AreEqual(0L, Interlocked.Read(ref result), "Record has not returned");
                Assert.AreEqual(0, ring.Dropped, "it did not give up");
                Assert.AreEqual(69L, ring.Written, "nothing else claimed");
                Assert.AreEqual(-5L, Volatile.Read(ref e[5].Index), "it did not take the held slot");

                Volatile.Write(ref e[5].Index, 5);

                Assert.IsTrue(writer.Join(5000), "the newer writer proceeds once the older one publishes");
                Assert.AreEqual(69L, Interlocked.Read(ref result));
                Assert.AreEqual(69L, Volatile.Read(ref e[5].Index));
                Assert.AreEqual(0xBEEFu, e[5].Guid);
                Assert.AreEqual(0, ring.Dropped);
            }
            finally
            {
                // free the writer however the body exited
                if (Volatile.Read(ref e[5].Index) == -5)
                    Volatile.Write(ref e[5].Index, 5);
                writer.Join(5000);
            }
        }

        [TestMethod]
        public void SlotHeldByAStalledOlderWriter_TheNewerRecordGivesUpWithinItsBudget_AndCountsTheDrop()
        {
            var ring = new OutboundRing();
            var e = RingAboutToClaim69(ring);
            Assert.AreEqual(64, Field<int>(ring, "spinBudget"));
            e[5].Index = -5; // never released

            var timer = Stopwatch.StartNew();
            Assert.AreEqual(69L, Record(ring, 0xBEEF));
            timer.Stop();

            Assert.IsTrue(timer.ElapsedMilliseconds < 1000, $"the budget bounds the wait (took {timer.ElapsedMilliseconds} ms)");
            Assert.AreEqual(-5L, Volatile.Read(ref e[5].Index), "the older holder keeps its slot");
            Assert.AreNotEqual(0xBEEFu, e[5].Guid);
            Assert.AreEqual(1, ring.Dropped);
            Assert.AreEqual(0, ring.Suppressed);
        }

        [TestMethod]
        public void NotePacketSeq_RacingAWriterThatTakesTheSlot_NeverStampsTheNewerEntry()
        {
            // a two-slot ring lapped on every record, and a noter stamping each entry with its own index as the
            // packet sequence: an entry carrying any other non-zero sequence was stamped by a note meant for the
            // entry its slot held before. No seam exists between the note's check and its write, so this races for
            // a bounded time and stops at the first stale stamp.
            var ring = new OutboundRing();
            ring.NoteInbound(0);
            Record(ring, 1, 2);

            var stop = 0;
            var stale = 0L;
            var staleSeq = 0u;

            var writer = new Thread(() =>
            {
                uint guid = 2;
                while (Volatile.Read(ref stop) == 0)
                {
                    ring.NoteInbound(guid);
                    Record(ring, guid++, 2);
                }
            });

            var noter = new Thread(() =>
            {
                var deadline = Stopwatch.StartNew();
                while (deadline.ElapsedMilliseconds < 2000 && Volatile.Read(ref stop) == 0)
                {
                    var index = ring.Written;
                    ring.NotePacketSeq(index, (uint)index);
                    ring.NotePacketSeq(index - 1, (uint)(index - 1));

                    foreach (var e in ring.Snapshot().Entries)
                    {
                        if (e.PacketSeq != 0 && e.PacketSeq != (uint)e.Index)
                        {
                            Interlocked.Exchange(ref stale, e.Index);
                            staleSeq = e.PacketSeq;
                            Volatile.Write(ref stop, 1);
                        }
                    }
                }

                Volatile.Write(ref stop, 1);
            });

            writer.Start();
            noter.Start();
            noter.Join();
            writer.Join();

            Assert.AreEqual(0L, Interlocked.Read(ref stale), $"entry {stale} carries packet sequence {staleSeq}, a note meant for an older entry");
            Assert.IsTrue(ring.Written > 1000, $"the writer lapped the ring during the race (written={ring.Written})");
        }

        private static uint AuxOf(uint guid) => guid * 2654435761u ^ 0x9E3779B9u;

        [TestMethod]
        public void ConcurrentWriters_AndReader_NoTornEntries_AndEachWritersOrderIsKept()
        {
            const int writers = 4;
            const int perWriter = 50000;

            var ring = new OutboundRing();
            var stop = 0;
            var failures = new List<string>();
            var snapshotsTaken = 0;

            void Check(OutboundRingSnapshot snapshot)
            {
                var lastSeq = new long[writers];
                for (var w = 0; w < writers; w++)
                    lastSeq[w] = -1;

                foreach (var e in snapshot.Entries)
                {
                    var writer = (int)(e.Guid >> 24);
                    var seq = e.Guid & 0xFFFFFF;

                    if (writer >= writers || e.Aux != AuxOf(e.Guid) || e.Size != (int)(e.Guid ^ 0x5555) || e.EventType != writer || e.Opcode != 0xF7B0 || e.Stamp != e.Guid)
                    {
                        lock (failures)
                            failures.Add($"torn entry at index {e.Index}: guid={e.Guid:X8} aux={e.Aux:X8} size={e.Size} ev={e.EventType}");
                        continue;
                    }

                    if (seq <= lastSeq[writer])
                    {
                        lock (failures)
                            failures.Add($"writer {writer} out of order at index {e.Index}: {seq} after {lastSeq[writer]}");
                    }

                    lastSeq[writer] = seq;
                }
            }

            var threads = new List<Thread>();

            for (var w = 0; w < writers; w++)
            {
                var writer = (uint)w;
                threads.Add(new Thread(() =>
                {
                    for (uint seq = 0; seq < perWriter; seq++)
                    {
                        var guid = (writer << 24) | seq;
                        ring.Record(guid, 0xF7B0, (ushort)writer, guid, AuxOf(guid), (int)(guid ^ 0x5555), 0, 4096);

                        // concurrent inbound notes too, so the post-anchor cap keeps thawing and the ring wraps many times
                        if ((seq & 63) == 0)
                            ring.NoteInbound(seq);
                    }
                }));
            }

            var inbound = new Thread(() =>
            {
                long stamp = 0;
                while (Volatile.Read(ref stop) == 0)
                    ring.NoteInbound(++stamp);
            });

            var reader = new Thread(() =>
            {
                while (Volatile.Read(ref stop) == 0)
                {
                    Check(ring.Snapshot());
                    ring.ReadAnchor();
                    Interlocked.Increment(ref snapshotsTaken);
                }
            });

            inbound.Start();
            reader.Start();
            foreach (var t in threads)
                t.Start();
            foreach (var t in threads)
                t.Join();

            Volatile.Write(ref stop, 1);
            inbound.Join();
            reader.Join();

            var final = ring.Snapshot();
            Check(final);

            Assert.AreEqual(0, failures.Count, string.Join("\n", failures.Take(10)));
            Assert.IsTrue(snapshotsTaken > 0, "the reader ran concurrently");
            Assert.AreEqual(writers * perWriter, final.Written + final.Suppressed, "every record was either written or counted as suppressed");
            Assert.IsTrue(final.Written > 10 * 4096, $"the ring wrapped many times during the race (written={final.Written})");
            // a writer that runs out of spin budget behind an older holder leaves that holder's older entry in the slot
            // until the next lap, so each drop can cost one slot; with no drop, every slot is held
            if (final.Dropped == 0)
                Assert.AreEqual(4096, final.Entries.Count, "a quiescent ring snapshots every slot");
            else
                Assert.IsTrue(final.Entries.Count >= 4096 - final.Dropped, $"a quiescent ring misses at most one slot per drop (entries={final.Entries.Count} dropped={final.Dropped})");
        }

        private static OutboundRingEntry E(long index, long stamp) => new OutboundRingEntry { Index = index, Stamp = stamp };

        [TestMethod]
        public void Select_TakesAQuarterAfterTheAnchor_ThenFillsFromJustBefore()
        {
            var f = Stopwatch.Frequency;
            var entries = Enumerable.Range(1, 400).Select(i => E(i, i * f / 1000)).ToList(); // 1 ms apart
            var anchor = new OutboundRingAnchor(200, 200 * f / 1000);

            var selected = OutboundRing.Select(entries, anchor, 128, OutboundRing.DefaultPreWindowTicks);

            Assert.AreEqual(128, selected.Count);
            CollectionAssert.AreEqual(Enumerable.Range(105, 128).Select(i => (long)i).ToList(), selected.Select(e => e.Index).ToList(),
                "96 before the anchor (105-200), 32 after (201-232), in index order");
        }

        [TestMethod]
        public void Select_FewPostEntries_GiveTheirRoomToPreEntries()
        {
            var f = Stopwatch.Frequency;
            var entries = Enumerable.Range(1, 300).Select(i => E(i, i * f / 1000)).ToList();
            var anchor = new OutboundRingAnchor(295, 295 * f / 1000);

            var selected = OutboundRing.Select(entries, anchor, 128, OutboundRing.DefaultPreWindowTicks);

            Assert.AreEqual(128, selected.Count);
            Assert.AreEqual(5, selected.Count(e => e.Index > 295));
            Assert.AreEqual(173L, selected.First().Index);
            Assert.AreEqual(300L, selected.Last().Index);
        }

        [TestMethod]
        public void Select_PreEntriesOutsideThirtySecondsAreDropped()
        {
            var f = Stopwatch.Frequency;
            var anchorStamp = 100 * f;

            var entries = new List<OutboundRingEntry>
            {
                E(1, anchorStamp - 31 * f),     // too old
                E(2, anchorStamp - 30 * f),     // exactly 30 s: kept
                E(3, anchorStamp - 1 * f),
                E(4, anchorStamp),
                E(5, anchorStamp + 70 * f),     // after the anchor: no window applies
            };

            var selected = OutboundRing.Select(entries, new OutboundRingAnchor(4, anchorStamp), 16, OutboundRing.DefaultPreWindowTicks);

            CollectionAssert.AreEqual(new long[] { 2, 3, 4, 5 }, selected.Select(e => e.Index).ToList());

            // empty input, and no room at all
            Assert.AreEqual(0, OutboundRing.Select(new List<OutboundRingEntry>(), new OutboundRingAnchor(0, 0), 16, f).Count);
            Assert.AreEqual(0, OutboundRing.Select(entries, new OutboundRingAnchor(4, anchorStamp), 0, f).Count);
        }

        [TestMethod]
        public void Select_PostEntriesAreCappedAtAQuarterEvenWithNothingBefore()
        {
            var entries = Enumerable.Range(1, 100).Select(i => E(i, i)).ToList();

            var selected = OutboundRing.Select(entries, new OutboundRingAnchor(0, 0), 16, OutboundRing.DefaultPreWindowTicks);

            CollectionAssert.AreEqual(new long[] { 1, 2, 3, 4 }, selected.Select(e => e.Index).ToList());
        }

        private static readonly Regex HeaderLine = new Regex(
            @"^\[OUTBOUND_RING\] rid=[0-9a-f]{8} k=[tsc] hdr=1 player=""[ -~]*"" acct=\S+ cell=0x[0-9A-F]{8} teleporting=[01] " +
            @"sinceInMs=-?\d+ connS=\d+ ring=\d+ written=\d+ suppressed=\d+ caps=\d+ seq=\d+ ack=\d+ ackAgoMs=-?\d+ " +
            @"resendMiss=\d+ crcFail=\d+ shown=\d+( skipped=rate)? evicted=[01]$");

        private static readonly Regex EntryLine = new Regex(
            @"^\[OUTBOUND_RING\] rid=[0-9a-f]{8} k=[tsc] i=\d+ rel=-?\d+ op=[0-9A-F]{4} sz=\d+( ev=[0-9A-F]{4})?( g=[0-9A-F]{8})?( a=[0-9A-F]{8})?( w=\d+)? ackd=[01]$");

        private static OutboundRingDumpHeader SampleHeader(bool skipped) => new OutboundRingDumpHeader
        {
            Rid = "5f3a9c01",
            Kind = 't',
            Player = "Foo \"Bar\" " + (char)0xE9,
            Account = "foo bar",
            Cell = 0x8A0201AB,
            Teleporting = false,
            SinceInboundMs = 61012,
            ConnectionSeconds = 5403,
            Capacity = 512,
            Written = 48211,
            Suppressed = 1840,
            Caps = 1,
            ServerSequence = 88123,
            ClientAck = 88101,
            AckAgoMs = 61200,
            ResendMisses = 0,
            CrcFailures = 0,
            Shown = skipped ? 0 : 128,
            RateSkipped = skipped,
        };

        [TestMethod]
        public void Header_HasTheExactFieldOrder()
        {
            var line = OutboundRingFormat.Header(SampleHeader(false));

            Assert.AreEqual("[OUTBOUND_RING] rid=5f3a9c01 k=t hdr=1 player=\"Foo _Bar_ ?\" acct=foo_bar cell=0x8A0201AB teleporting=0 sinceInMs=61012 " +
                            "connS=5403 ring=512 written=48211 suppressed=1840 caps=1 seq=88123 ack=88101 ackAgoMs=61200 resendMiss=0 crcFail=0 shown=128 evicted=0", line);
            Assert.IsTrue(HeaderLine.IsMatch(line));

            var skippedHeader = SampleHeader(true);
            skippedHeader.Evicted = true;
            var skipped = OutboundRingFormat.Header(skippedHeader);
            StringAssert.EndsWith(skipped, " shown=0 skipped=rate evicted=1", "evicted is the last field");
            Assert.IsTrue(HeaderLine.IsMatch(skipped));

            var blank = SampleHeader(false);
            blank.Player = null;
            blank.Account = null;
            blank.SinceInboundMs = -1;
            blank.AckAgoMs = -1;
            Assert.IsTrue(HeaderLine.IsMatch(OutboundRingFormat.Header(blank)), OutboundRingFormat.Header(blank));
        }

        [TestMethod]
        public void Entry_HasTheExactFieldOrder_AndOmitsZeroFields()
        {
            var f = Stopwatch.Frequency;
            var anchorStamp = 1000 * f;

            var gameEvent = new OutboundRingEntry { Index = 95, Stamp = anchorStamp - 412 * f / 1000, Opcode = 0xF7B0, Size = 96, EventType = 0x02C9, Aux = 0x2A, PacketSeq = 88100 };
            var create = new OutboundRingEntry { Index = 97, Stamp = anchorStamp + 18 * f / 1000, Opcode = 0xF745, Size = 412, Guid = 0x8000ABCD, Aux = 0x02000A1C, PacketSeq = 88102 };
            var unstamped = new OutboundRingEntry { Index = 98, Stamp = anchorStamp, Opcode = 0xF748, Size = 40 };

            var first = OutboundRingFormat.Entry("5f3a9c01", 't', gameEvent, anchorStamp, 88101, 0);
            var second = OutboundRingFormat.Entry("5f3a9c01", 't', create, anchorStamp, 88101, 1234567);
            var third = OutboundRingFormat.Entry("5f3a9c01", 'c', unstamped, anchorStamp, 88101, 0);

            Assert.AreEqual("[OUTBOUND_RING] rid=5f3a9c01 k=t i=95 rel=-412 op=F7B0 sz=96 ev=02C9 a=0000002A ackd=1", first);
            Assert.AreEqual("[OUTBOUND_RING] rid=5f3a9c01 k=t i=97 rel=18 op=F745 sz=412 g=8000ABCD a=02000A1C w=1234567 ackd=0", second);
            Assert.AreEqual("[OUTBOUND_RING] rid=5f3a9c01 k=c i=98 rel=0 op=F748 sz=40 ackd=0", third, "packet seq 0 (never stamped) is never acked");

            foreach (var line in new[] { first, second, third })
                Assert.IsTrue(EntryLine.IsMatch(line), line);
        }

        [TestMethod]
        public void FormattedOutput_IsPrintableAscii()
        {
            var lines = new List<string>
            {
                OutboundRingFormat.Header(SampleHeader(false)),
                OutboundRingFormat.Header(SampleHeader(true)),
                OutboundRingFormat.Entry("00000001", 's', new OutboundRingEntry { Index = 1, Opcode = 0xFFFF, Size = int.MaxValue, Guid = uint.MaxValue, Aux = uint.MaxValue, EventType = 0xFFFF }, long.MaxValue / 2000, 0, uint.MaxValue),
            };

            foreach (var line in lines)
            {
                foreach (var b in Encoding.UTF8.GetBytes(line))
                    Assert.IsTrue(b >= 0x20 && b <= 0x7E, $"byte 0x{b:X2} in: {line}");
            }
        }

        private static readonly SessionTerminationReason[] ConnectionFaults =
        {
            SessionTerminationReason.ClientSentNetworkErrorDisconnect,
            SessionTerminationReason.ClientConnectionFailure,
            SessionTerminationReason.SendToSocketException,
            SessionTerminationReason.AbnormalSequenceReceived,
            SessionTerminationReason.BadHandshake,
        };

        [TestMethod]
        public void DropKind_TimeoutAlways_ConnectionFaultsOnlyAfterTenSecondsOfSilence()
        {
            Assert.AreEqual('t', OutboundRingMonitor.DropKind(SessionTerminationReason.NetworkTimeout, 0));
            Assert.AreEqual('t', OutboundRingMonitor.DropKind(SessionTerminationReason.NetworkTimeout, -1));

            foreach (var reason in ConnectionFaults)
            {
                Assert.AreEqual('s', OutboundRingMonitor.DropKind(reason, 10_000), reason.ToString());
                Assert.AreEqual('\0', OutboundRingMonitor.DropKind(reason, 9_999), reason + " before 10 s of silence");
                Assert.AreEqual('\0', OutboundRingMonitor.DropKind(reason, -1), reason + " with no inbound ever");
            }
        }

        [TestMethod]
        public void DropKind_AdministrativeAndAccountDrops_NeverDump()
        {
            foreach (var reason in new[]
            {
                SessionTerminationReason.ServerShuttingDown,
                SessionTerminationReason.WorldClosed,
                SessionTerminationReason.AccountBooted,
                SessionTerminationReason.AccountInUse,
                SessionTerminationReason.AccountLoggedIn,
                SessionTerminationReason.ForcedLogOffRequested,
                SessionTerminationReason.AutoForcedLogOff,
                SessionTerminationReason.AccountBanned,
                SessionTerminationReason.ClientVersionIncorrect,
                SessionTerminationReason.PacketHeaderDisconnect,
                SessionTerminationReason.PongSentClosingConnection,
                SessionTerminationReason.None,
            })
                Assert.AreEqual('\0', OutboundRingMonitor.DropKind(reason, 600_000), reason.ToString());

            // the whitelist is exactly the five fault reasons, out of every reason that exists
            var whitelisted = System.Enum.GetValues(typeof(SessionTerminationReason)).Cast<SessionTerminationReason>()
                .Where(OutboundRingMonitor.IsConnectionFault).ToList();
            CollectionAssert.AreEquivalent(ConnectionFaults, whitelisted);
        }

        [TestMethod]
        public void Evicted_ControlAnchorOverwrittenAfterThePick_IsFlagged()
        {
            const int dumpEntries = 128;
            var ring = new OutboundRing();

            for (uint i = 1; i <= 300; i++)
            {
                Record(ring, i, 512);
                ring.NoteInbound(i);
            }

            // the control pick: anchor at 300
            var picked = ring.ReadAnchor();
            var atPick = ring.Snapshot();
            Assert.IsFalse(OutboundRing.Evicted(atPick.Written, atPick.Capacity, picked, dumpEntries), "nothing evicted at the pick");

            // the session stays healthy (inbound keeps moving the live anchor, so the cap never protects the picked
            // one) and writes past the ring's capacity before the dump
            for (uint i = 301; i <= 1000; i++)
            {
                Record(ring, i, 512);
                ring.NoteInbound(i);
            }

            var atDump = ring.Snapshot();
            Assert.IsTrue(OutboundRing.Evicted(atDump.Written, atDump.Capacity, picked, dumpEntries));

            // still dumpable: selection returns what is left after the picked anchor
            var selected = OutboundRing.Select(atDump.Entries, picked, dumpEntries, long.MaxValue);
            Assert.IsTrue(selected.All(e => e.Index > picked.Index));

            // boundary: the oldest wanted entry is anchor - 96 + 1 = 205; held while oldest held (written - 511) <= 205
            Assert.IsFalse(OutboundRing.Evicted(716, 512, picked, dumpEntries));
            Assert.IsTrue(OutboundRing.Evicted(717, 512, picked, dumpEntries));

            // a window larger than the ring is reported, an empty ring is not
            Assert.IsTrue(OutboundRing.Evicted(1000, 64, new OutboundRingAnchor(1000, 0), 512));
            Assert.IsFalse(OutboundRing.Evicted(0, 0, new OutboundRingAnchor(0, 0), 128));
        }

        [TestMethod]
        public void ControlClock_SchedulesOneIntervalOut_AndALoweredIntervalTakesEffectPromptly()
        {
            long next = 0;

            Assert.IsFalse(OutboundRingMonitor.AdvanceControlClock(ref next, 1000, 300), "the first call only schedules");
            Assert.AreEqual(1300, next);

            Assert.IsFalse(OutboundRingMonitor.AdvanceControlClock(ref next, 1299, 300));
            Assert.IsTrue(OutboundRingMonitor.AdvanceControlClock(ref next, 1300, 300));
            Assert.AreEqual(1600, next);

            // the interval drops from 300 to 10: the deadline comes in to now + 10, not 1600
            Assert.IsFalse(OutboundRingMonitor.AdvanceControlClock(ref next, 1305, 10));
            Assert.AreEqual(1315, next);
            Assert.IsTrue(OutboundRingMonitor.AdvanceControlClock(ref next, 1315, 10));
            Assert.AreEqual(1325, next);

            // raising it again does not push an existing deadline out
            Assert.IsFalse(OutboundRingMonitor.AdvanceControlClock(ref next, 1320, 300));
            Assert.AreEqual(1325, next);

            // off unschedules
            Assert.IsFalse(OutboundRingMonitor.AdvanceControlClock(ref next, 1400, 0));
            Assert.AreEqual(0, next);
        }

        [TestMethod]
        public void DumpLimiter_AllowsThirtyPerWindow_ThenResets()
        {
            var limiter = new OutboundRingDumpLimiter(30, 1000);

            for (var i = 0; i < 30; i++)
                Assert.IsTrue(limiter.TryTake(500 + i));

            Assert.IsFalse(limiter.TryTake(600));
            Assert.IsFalse(limiter.TryTake(1499));
            Assert.IsTrue(limiter.TryTake(1500), "a new window");
        }

        [TestMethod]
        public void Rid_IsEightLowercaseHexDigits_AndUnique()
        {
            var a = OutboundRingMonitor.NextRid();
            var b = OutboundRingMonitor.NextRid();

            Assert.IsTrue(Regex.IsMatch(a, "^[0-9a-f]{8}$"), a);
            Assert.AreNotEqual(a, b);
        }
    }
}
