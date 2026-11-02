using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Network;

namespace ACE.Server.Tests
{
    /// <summary>
    /// SendPacer: the per-session outbound FIFO behind network_pacing_enabled. A fake clock and a fake sent/acked pair
    /// stand in for the wall clock and the protocol sequence numbers.
    /// </summary>
    [TestClass]
    public class SendPacerTests
    {
        private sealed class Harness
        {
            public long NowMs;
            public long Sent;
            public long Acked;
            public readonly List<int> Released = new List<int>();
            public readonly SendPacer<int> Pacer;

            public Harness()
            {
                Pacer = new SendPacer<int>(() => NowMs);
            }

            public long Inflight() => Sent - Acked;

            public int Drain(SendPacerConfig config) => Pacer.Drain(config, Inflight, item => { Released.Add(item); Sent++; });

            public void OfferMany(int count, int size, int firstId = 0)
            {
                for (var i = 0; i < count; i++)
                    Assert.IsFalse(Pacer.Offer(firstId + i, size, false), "a data packet is queued, not sent inline");
            }
        }

        private static SendPacerConfig RateOnly(long rate, long burst) => new SendPacerConfig { RateBytesPerSecond = rate, BurstBytes = burst, MaxInflightPackets = 0, TrickleMs = 100 };

        private static SendPacerConfig CapOnly(long cap, long trickleMs) => new SendPacerConfig { RateBytesPerSecond = 0, BurstBytes = 1, MaxInflightPackets = cap, TrickleMs = trickleMs };

        [TestMethod]
        public void Fifo_IsPreservedUnderRateLimiting()
        {
            var h = new Harness();
            var config = RateOnly(1000, 1000);

            // 20 packets of 100 bytes against a 1000 byte bucket refilling at 1000 B/s
            h.OfferMany(20, 100);

            for (var step = 0; step < 30; step++)
            {
                h.Drain(config);
                h.NowMs += 100;
            }

            Assert.AreEqual(20, h.Released.Count, "everything drains eventually");
            for (var i = 0; i < 20; i++)
                Assert.AreEqual(i, h.Released[i], $"release #{i} is out of order");
        }

        [TestMethod]
        public void InflightCap_HoldsWhileAcksLag()
        {
            var h = new Harness();
            var config = CapOnly(8, 1000);

            h.OfferMany(50, 400);

            Assert.AreEqual(8, h.Drain(config), "the first drain fills the window and stops at the cap");

            // time passes but well inside the trickle interval, and the client acks nothing
            for (var step = 0; step < 9; step++)
            {
                h.NowMs += 100;
                Assert.AreEqual(0, h.Drain(config), $"step {step}: nothing more while 8 are unacked");
            }

            Assert.AreEqual(8, h.Inflight());
            Assert.AreEqual(42, h.Pacer.HeldCount);
        }

        [TestMethod]
        public void Acks_ReleaseTheNextPackets()
        {
            var h = new Harness();
            var config = CapOnly(8, 1000);

            h.OfferMany(50, 400);
            h.Drain(config);
            Assert.AreEqual(8, h.Released.Count);

            h.Acked = 5; // client acks five
            h.NowMs += 10;
            Assert.AreEqual(5, h.Drain(config), "five acked, five more may go");
            CollectionAssert.AreEqual(new[] { 8, 9, 10, 11, 12 }, h.Released.GetRange(8, 5), "and they are the next five in order");
            Assert.AreEqual(8, h.Inflight());
        }

        [TestMethod]
        public void TrickleFloor_FiresAfterTrickleMs_WithZeroAcks()
        {
            var h = new Harness();
            var config = CapOnly(4, 100);

            h.OfferMany(10, 400);
            Assert.AreEqual(4, h.Drain(config));

            h.NowMs += 99;
            Assert.AreEqual(0, h.Drain(config), "not before trickle_ms");

            h.NowMs += 1;
            Assert.AreEqual(1, h.Drain(config), "exactly one at trickle_ms, though nothing was acked");
            Assert.AreEqual(4, h.Released[4], "the trickle releases the head");

            h.NowMs += 50;
            Assert.AreEqual(0, h.Drain(config), "the floor measures from the last release, so not again yet");

            h.NowMs += 50;
            Assert.AreEqual(1, h.Drain(config), "one more after another trickle_ms");
            Assert.AreEqual(2, h.Pacer.TrickleReleases);
        }

        [TestMethod]
        public void BypassPackets_AreNeverHeld()
        {
            var h = new Harness();
            var config = CapOnly(2, 1000);

            h.OfferMany(10, 400);
            h.Drain(config);
            Assert.AreEqual(8, h.Pacer.HeldCount, "the queue is blocked at the cap");

            Assert.IsTrue(h.Pacer.Offer(999, 40, true), "a bypass packet must be sent now, even with a blocked queue");
            Assert.AreEqual(8, h.Pacer.HeldCount, "and it was not queued");
            Assert.IsFalse(h.Released.Contains(999), "nor released through the queue");
        }

        [TestMethod]
        public void BurstThenSteadyRate_Math()
        {
            var h = new Harness();
            var config = RateOnly(10000, 4000); // 10 kB/s, 4 kB burst

            h.OfferMany(100, 400);

            // t=0: a full bucket is exactly 10 packets of 400 bytes
            Assert.AreEqual(10, h.Drain(config), "the burst");
            Assert.AreEqual(0, h.Drain(config), "and nothing more at the same instant");

            // 10 kB/s refills 400 bytes every 40 ms
            h.NowMs = 39;
            Assert.AreEqual(0, h.Drain(config), "390 bytes of credit is not a 400 byte packet");
            h.NowMs = 40;
            Assert.AreEqual(1, h.Drain(config), "40 ms buys one packet");

            // one further second at the steady rate: 10000 / 400 = 25 packets, however finely it is sliced
            var released = 0;
            for (var t = 41; t <= 1040; t++)
            {
                h.NowMs = t;
                released += h.Drain(config);
            }
            Assert.AreEqual(25, released, "steady rate after the burst");

            // a long idle spell refills only up to the burst, never beyond
            h.NowMs += 60000;
            Assert.AreEqual(10, h.Drain(config), "idle credit is capped at the burst size");
        }

        [TestMethod]
        public void OversizedPacket_StillGoesOnceTheBucketIsFull()
        {
            var h = new Harness();
            var config = RateOnly(1000, 100);

            h.OfferMany(2, 464);
            Assert.AreEqual(1, h.Drain(config), "a packet bigger than the bucket goes when the bucket is full");
            Assert.IsTrue(h.Pacer.Tokens < 0, "and drives the level negative, so the average rate holds");

            h.NowMs = 300;
            Assert.AreEqual(0, h.Drain(config), "still in debt");
            h.NowMs = 464;
            Assert.AreEqual(1, h.Drain(config), "debt repaid and the bucket full again");
        }

        [TestMethod]
        public void PeakHeld_TracksTheDeepestQueue_AndResets()
        {
            var h = new Harness();
            h.OfferMany(30, 400);
            h.Pacer.Drain(CapOnly(10, 1000), h.Inflight, _ => h.Sent++);
            Assert.AreEqual(30, h.Pacer.PeakHeld);

            Assert.AreEqual(30, h.Pacer.TakePeak(), "take returns the peak");
            Assert.AreEqual(20, h.Pacer.PeakHeld, "and restarts it from what is held now");
        }

        [TestMethod]
        public void DrainAll_AndClear()
        {
            var h = new Harness();
            h.OfferMany(5, 400);
            h.Pacer.DrainAll(item => h.Released.Add(item));
            CollectionAssert.AreEqual(new[] { 0, 1, 2, 3, 4 }, h.Released);

            h.OfferMany(5, 400);
            h.Pacer.Clear();
            Assert.AreEqual(0, h.Pacer.HeldCount);
            Assert.AreEqual(0, h.Pacer.HeldBytes);
        }
    }
}
