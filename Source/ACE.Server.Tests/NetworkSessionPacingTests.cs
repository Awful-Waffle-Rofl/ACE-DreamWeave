using System.Collections.Concurrent;
using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Network;
using ACE.Server.Network.Packets;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The NetworkSession side of send pacing: which real ServerPackets bypass the pacer, the bucket size of a packet,
    /// the in-flight arithmetic (including the uint.MaxValue "nothing sent yet" sentinel), and the order packets reach
    /// the wire when pacing is switched off on a session that still holds some.
    /// </summary>
    [TestClass]
    public class NetworkSessionPacingTests
    {
        // ---- packets shaped exactly as the server builds them ----

        private static ServerPacket Nak()
        {
            // DoRequestForRetransmission: flags RequestRetransmit only, sequence list in Data
            var p = new ServerPacket();
            p.InitializeDataWriter();
            p.DataWriter.Write(1u);
            p.DataWriter.Write(42u);
            p.Header.Flags = PacketHeaderFlags.RequestRetransmit;
            return p;
        }

        private static ServerPacket AckOnly()
        {
            // SendBundle with SendAck and no messages: bundle.EncryptedChecksum false, so flags == AckSequence
            var p = new ServerPacket();
            p.Header.Flags = PacketHeaderFlags.AckSequence;
            p.InitializeDataWriter();
            p.DataWriter.Write(17u);
            return p;
        }

        private static ServerPacket Echo()
        {
            // FlagEcho sets bundle.EncryptedChecksum; WriteOptionalHeaders writes two floats, no fragments
            var p = new ServerPacket();
            p.Header.Flags = PacketHeaderFlags.EncryptedChecksum | PacketHeaderFlags.EchoResponse;
            p.InitializeDataWriter();
            p.DataWriter.Write(1.5f);
            p.DataWriter.Write(0.25f);
            return p;
        }

        private static ServerPacket TimeSync()
        {
            // Update sets TimeSync and EncryptedChecksum; WriteOptionalHeaders writes PortalYearTicks (a double)
            var p = new ServerPacket();
            p.Header.Flags = PacketHeaderFlags.EncryptedChecksum | PacketHeaderFlags.TimeSync;
            p.InitializeDataWriter();
            p.DataWriter.Write(123456.789);
            return p;
        }

        private static ServerPacket Data(int fragmentBytes = 100)
        {
            var p = new ServerPacket();
            p.Header.Flags = PacketHeaderFlags.BlobFragments | PacketHeaderFlags.EncryptedChecksum;
            p.Fragments.Add(new ServerPacketFragment(new byte[fragmentBytes]));
            return p;
        }

        // ---- BypassesPacer ----

        [TestMethod]
        public void Bypass_ControlPackets_AllBypass()
        {
            Assert.IsTrue(NetworkSession.BypassesPacer(new PacketOutboundConnectRequest(0, 0, 0, new byte[4], new byte[4])), "connect request");
            Assert.IsTrue(NetworkSession.BypassesPacer(new PacketRejectRetransmit(new List<uint> { 5, 6 })), "RejectRetransmit");
            Assert.IsTrue(NetworkSession.BypassesPacer(Nak()), "NAK");
            Assert.IsTrue(NetworkSession.BypassesPacer(AckOnly()), "bare ack");
        }

        [TestMethod]
        public void Bypass_EncryptedButFragmentless_EchoAndTimeSync_Bypass()
        {
            // their payload carries PortalYearTicks, which would go stale while held
            Assert.IsTrue(NetworkSession.BypassesPacer(Echo()), "EchoResponse");
            Assert.IsTrue(NetworkSession.BypassesPacer(TimeSync()), "TimeSync");
        }

        [TestMethod]
        public void Bypass_EncryptedDataPacket_IsPaced()
        {
            Assert.IsFalse(NetworkSession.BypassesPacer(Data()), "BlobFragments|EncryptedChecksum is the data the pacer exists for");

            // an echo riding in the first packet of a bundle with fragments is paced with it
            var echoWithData = Echo();
            echoWithData.Header.Flags |= PacketHeaderFlags.BlobFragments;
            echoWithData.Fragments.Add(new ServerPacketFragment(new byte[10]));
            Assert.IsFalse(NetworkSession.BypassesPacer(echoWithData));
        }

        [TestMethod]
        public void Bypass_UnencryptedPacketWithFragments_StillBypasses()
        {
            // the "no encrypted checksum" clause on its own: nothing that skips ISAAC is ever held
            var p = new ServerPacket();
            p.Header.Flags = PacketHeaderFlags.BlobFragments;
            p.Fragments.Add(new ServerPacketFragment(new byte[10]));
            Assert.IsTrue(NetworkSession.BypassesPacer(p));
        }

        // ---- WireSize ----

        [TestMethod]
        public void WireSize_IsHeaderPlusDataPlusEveryFragment()
        {
            Assert.AreEqual(20, PacketHeader.HeaderSize, "constant this test is written against");
            Assert.AreEqual(16, PacketFragmentHeader.HeaderSize, "constant this test is written against");

            Assert.AreEqual(20, NetworkSession.WireSize(new ServerPacket()), "header only");
            Assert.AreEqual(20 + 8, NetworkSession.WireSize(Echo()), "header + two floats");

            var p = Data(100);
            p.Fragments.Add(new ServerPacketFragment(new byte[300]));
            p.InitializeDataWriter();
            p.DataWriter.Write(7u);
            Assert.AreEqual(20 + 4 + (16 + 100) + (16 + 300), NetworkSession.WireSize(p));
        }

        // ---- Inflight ----

        [TestMethod]
        public void Inflight_SentMinusAcked_NeverNegative_AndTheSentinelIsZero()
        {
            Assert.AreEqual(0, NetworkSession.Inflight(uint.MaxValue, 0), "UIntSequence(clientPrimed: false) starts at uint.MaxValue: nothing sent");
            Assert.AreEqual(0, NetworkSession.Inflight(uint.MaxValue, 12), "sentinel with a stale ack");
            Assert.AreEqual(uint.MaxValue - 1L, NetworkSession.Inflight(uint.MaxValue - 1, 0), "only the exact sentinel is special");
            Assert.AreEqual(6, NetworkSession.Inflight(10, 4));
            Assert.AreEqual(0, NetworkSession.Inflight(10, 10));
            Assert.AreEqual(0, NetworkSession.Inflight(4, 10), "an ack past the sequence never underflows");
            Assert.AreEqual(0, NetworkSession.Inflight(0, 0));
        }

        // ---- FlushThroughPacer ----

        private sealed class Wire
        {
            public readonly List<ServerPacket> Sent = new List<ServerPacket>();
            public long Acked;
            public long Inflight() => Sent.Count - Acked;
            public void Send(ServerPacket p) => Sent.Add(p);
        }

        private static SendPacerConfig CapOne => new SendPacerConfig { RateBytesPerSecond = 0, BurstBytes = 1, MaxInflightPackets = 1, TrickleMs = 1000 };

        [TestMethod]
        public void Flush_Paced_SendsBypassAtOnce_AndHoldsData()
        {
            var wire = new Wire();
            var pacer = new SendPacer<ServerPacket>(() => 0);
            var queue = new ConcurrentQueue<ServerPacket>();

            var a = Data(); var b = Data(); var echo = Echo(); var c = Data();
            queue.Enqueue(a); queue.Enqueue(b); queue.Enqueue(echo); queue.Enqueue(c);

            // cap 2: the echo takes a sequence too, so it occupies one in-flight slot
            var capTwo = CapOne;
            capTwo.MaxInflightPackets = 2;
            NetworkSession.FlushThroughPacer(queue, pacer, true, capTwo, wire.Inflight, wire.Send);

            CollectionAssert.AreEqual(new[] { echo, a }, wire.Sent, "the echo goes at offer time, ahead of a; then the cap lets exactly one data packet out");
            Assert.AreEqual(2, pacer.HeldCount);
            Assert.IsTrue(queue.IsEmpty);
        }

        [TestMethod]
        public void Flush_FlipOff_DrainsHeldOldestFirst_ThenNewer()
        {
            var wire = new Wire();
            var pacer = new SendPacer<ServerPacket>(() => 0);
            var queue = new ConcurrentQueue<ServerPacket>();

            var a = Data(); var b = Data(); var c = Data();
            queue.Enqueue(a); queue.Enqueue(b); queue.Enqueue(c);
            NetworkSession.FlushThroughPacer(queue, pacer, true, CapOne, wire.Inflight, wire.Send);
            CollectionAssert.AreEqual(new[] { a }, wire.Sent);

            // pacing switched off with b and c still held; d and a time sync arrive in the same update
            var d = Data(); var ts = TimeSync();
            queue.Enqueue(d); queue.Enqueue(ts);
            // the cap config is passed on purpose: a terminating session still has live tunables, and an unpaced flush
            // must ignore them (the unacked count is well past 1 here)
            NetworkSession.FlushThroughPacer(queue, pacer, false, CapOne, wire.Inflight, wire.Send);

            CollectionAssert.AreEqual(new[] { a, ts, b, c, d }, wire.Sent, "held b, c go before the newer d; the time sync never waits");
            Assert.AreEqual(0, pacer.HeldCount);
        }
    }
}
