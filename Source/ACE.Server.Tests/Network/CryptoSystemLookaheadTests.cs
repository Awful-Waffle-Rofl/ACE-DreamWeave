using System;
using System.Collections.Generic;

using ACE.Common.Cryptography;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.Network
{
    /// <summary>
    /// The C2S checksum key window (CryptoSystem). Every test drives the real class with a known seed and compares
    /// against an independent ISAAC stream from the same seed, so key ordinals are exact. Instances are built with an
    /// explicit window wherever the window matters, so no test depends on the static default another test may set.
    /// </summary>
    [TestClass]
    public class CryptoSystemLookaheadTests
    {
        private const uint Seed = 0x1234ABCDu;
        private const int Window = 4096;

        private static uint[] Keys(int count)
        {
            var isaac = new ISAAC(BitConverter.GetBytes(Seed));
            var keys = new uint[count];
            for (var i = 0; i < count; i++)
                keys[i] = isaac.Next();
            return keys;
        }

        private static CryptoSystem New(int window = Window) => new CryptoSystem(BitConverter.GetBytes(Seed), window);

        // what ClientPacket.VerifyCRC does with an encrypted checksum
        private static bool Verify(CryptoSystem cs, uint key)
        {
            if (!cs.Search(key))
                return false;

            cs.ConsumeKey(key);
            return true;
        }

        /// <summary>The pre-change algorithm, verbatim apart from the window being a field, for differential tests.</summary>
        private sealed class LegacyCryptoSystem : ISAAC
        {
            private readonly int max;
            private readonly HashSet<uint> xors = new HashSet<uint>();
            public uint CurrentKey;

            public LegacyCryptoSystem(int max) : base(BitConverter.GetBytes(Seed))
            {
                this.max = max;
                CurrentKey = Next();
            }

            public void ConsumeKey(uint x)
            {
                if (CurrentKey == x)
                    CurrentKey = Next();
                else
                    xors.Remove(x);
            }

            public bool Search(uint x)
            {
                if (CurrentKey == x)
                    return true;
                if (xors.Contains(x))
                    return true;
                int g = xors.Count;
                for (int i = 0; i < max - g; i++)
                {
                    xors.Add(CurrentKey);
                    ConsumeKey(CurrentKey);
                    if (CurrentKey == x)
                        return true;
                }
                return false;
            }

            public bool Verify(uint key)
            {
                if (!Search(key))
                    return false;
                ConsumeKey(key);
                return true;
            }
        }

        [TestMethod]
        public void InOrder_EveryKeyVerifies_AndNothingIsRemembered()
        {
            var keys = Keys(10000);
            var cs = New();

            for (var i = 0; i < keys.Length; i++)
                Assert.IsTrue(Verify(cs, keys[i]), $"key {i}");

            Assert.AreEqual(0, cs.OutstandingKeys);
            Assert.AreEqual(keys.Length - 1, cs.NewestVerifiedOrdinal);
        }

        [TestMethod]
        public void NoLoss_AcceptsExactlyWhatTheLegacyAlgorithmAccepted_AtTheLegacyWindow()
        {
            // no packet is lost for good: keys arrive in order, with adjacent pairs swapped now and then, plus garbage
            // checksums mixed in. At the legacy 256 window every accept/reject decision must match the old code.
            var keys = Keys(20000);
            var rng = new Random(7);
            var cs = New(CryptoSystem.LegacyEffortLevel);
            var legacy = new LegacyCryptoSystem(CryptoSystem.LegacyEffortLevel);

            var order = new List<int>();
            for (var i = 0; i < keys.Length; i++)
            {
                if (i + 1 < keys.Length && rng.Next(10) == 0)
                {
                    order.Add(i + 1);
                    order.Add(i);
                    i++;
                }
                else
                    order.Add(i);
            }

            var decisions = 0;
            foreach (var i in order)
            {
                if (rng.Next(25) == 0)
                {
                    var garbage = (uint)rng.Next() ^ ((uint)rng.Next() << 16);
                    Assert.AreEqual(legacy.Verify(garbage), Verify(cs, garbage), "garbage decision differs");
                    decisions++;
                }

                var expected = legacy.Verify(keys[i]);
                Assert.IsTrue(expected, $"legacy rejected key {i}: the fixture is not loss-free");
                Assert.AreEqual(expected, Verify(cs, keys[i]), $"key {i} decision differs");
                decisions++;
            }

            Assert.IsTrue(decisions > keys.Length);
        }

        [TestMethod]
        public void OutOfOrder_WithinTheWindow_AllVerify()
        {
            // blocks of 50 delivered newest first
            var keys = Keys(5000);
            var cs = New();

            for (var block = 0; block < keys.Length; block += 50)
            {
                for (var i = block + 49; i >= block; i--)
                    Assert.IsTrue(Verify(cs, keys[i]), $"key {i}");
            }

            Assert.AreEqual(0, cs.OutstandingKeys);
        }

        [TestMethod]
        public void GapOf1000_ThenLateRetransmitsOfTheSkippedKeys_AllVerify()
        {
            // the late arrivals here carry the skipped keys 100..1099, the case where a client retransmit reuses its
            // original packet's key; that the retail client does so is not established (the server's own S2C retransmit
            // does, via ServerPacket.IssacXor). If a retransmit took a fresh key instead, that is simply the next key
            // in the stream, and the never-used original ages out (LostKey_IsForgotten_OnceItIsMoreThanTheWindowBehind).
            var keys = Keys(1400);
            var cs = New();

            for (var i = 0; i < 100; i++)
                Assert.IsTrue(Verify(cs, keys[i]), $"key {i}");

            // 1000 lost; the client's next packets arrive
            for (var i = 1100; i < 1200; i++)
                Assert.IsTrue(Verify(cs, keys[i]), $"key {i} after the gap");

            Assert.AreEqual(1000, cs.OutstandingKeys, "the skipped keys are remembered");

            // NAKed retransmits, interleaved with new traffic
            for (var i = 100; i < 1100; i++)
            {
                Assert.IsTrue(Verify(cs, keys[i]), $"retransmit {i}");
                if (i % 10 == 0)
                    Assert.IsTrue(Verify(cs, keys[1200 + (i - 100) / 10]), "new traffic during recovery");
            }

            for (var i = 1300; i < 1400; i++)
                Assert.IsTrue(Verify(cs, keys[i]), $"key {i}");

            Assert.AreEqual(0, cs.OutstandingKeys);
        }

        [TestMethod]
        public void GapOfExactlyTheWindow_Verifies_OneMore_DoesNot()
        {
            var keys = Keys(20 + Window + 3);

            var cs = New();
            for (var i = 0; i <= 9; i++)
                Assert.IsTrue(Verify(cs, keys[i]));
            // keys 10 .. 10+Window-1 lost (Window of them)
            Assert.IsTrue(Verify(cs, keys[10 + Window]), "a gap of exactly the window is accepted");

            var cs2 = New();
            for (var i = 0; i <= 9; i++)
                Assert.IsTrue(Verify(cs2, keys[i]));
            Assert.IsFalse(Verify(cs2, keys[10 + Window + 1]), "a gap one wider than the window is rejected");
        }

        [TestMethod]
        public void GapLargerThanTheWindow_FailsForEveryLaterPacket()
        {
            var keys = Keys(3 * Window);
            var cs = New();

            for (var i = 0; i < 10; i++)
                Assert.IsTrue(Verify(cs, keys[i]));

            for (var i = 10 + Window + 500; i < 10 + Window + 600; i++)
                Assert.IsFalse(Verify(cs, keys[i]), $"key {i} is past the window");
        }

        [TestMethod]
        public void ManyIsolatedLosses_NeverShrinkTheLookahead()
        {
            // one packet in ten is lost and never retransmitted, for 60000 packets (6000 lost keys, past both the
            // legacy 256 budget and this window). Every delivered key must still verify. Fails on the pre-change
            // code at key 2566, the 257th loss.
            var keys = Keys(60000);
            var cs = New();

            for (var i = 0; i < keys.Length; i++)
            {
                if (i % 10 == 5)
                    continue;

                Assert.IsTrue(Verify(cs, keys[i]), $"key {i} rejected after {i / 10} isolated losses");
                Assert.IsTrue(cs.OutstandingKeys <= Window / 10 + 1, $"stale keys accumulate: {cs.OutstandingKeys} at key {i}");
            }

            // and the full window is still there: a gap of exactly the window after all that loss
            var more = Keys(60000 + Window + 1);
            Assert.IsTrue(Verify(cs, more[60000 + Window]), "full look-ahead restored");
        }

        [TestMethod]
        public void ManyIsolatedLosses_StarveTheLegacyAlgorithm()
        {
            // the control for the test above: the pre-change algorithm rejects the packet after the 257th loss
            var keys = Keys(6000);
            var legacy = new LegacyCryptoSystem(CryptoSystem.LegacyEffortLevel);

            var firstReject = -1;
            for (var i = 0; i < keys.Length && firstReject < 0; i++)
            {
                if (i % 10 == 5)
                    continue;

                if (!legacy.Verify(keys[i]))
                    firstReject = i;
            }

            Assert.AreEqual(2566, firstReject);
        }

        [TestMethod]
        public void Garbage_FillsTheWindowOnce_ThenEveryMissIsALookup()
        {
            var keys = Keys(200);
            var cs = New();

            for (var i = 0; i < 100; i++)
                Assert.IsTrue(Verify(cs, keys[i]));

            Assert.IsFalse(Verify(cs, 0xDEADBEEFu));
            var outstanding = cs.OutstandingKeys;
            Assert.AreEqual(0, cs.LookaheadRemaining, "the first miss generates the whole look-ahead");
            Assert.AreEqual(Window, outstanding, "ordinals 100 .. 99 + Window are remembered; CurrentKey is 100 + Window");

            for (uint g = 0; g < 1000; g++)
                Assert.IsFalse(Verify(cs, 0xA5A50000u + g));

            Assert.AreEqual(outstanding, cs.OutstandingKeys, "later misses generate nothing");
            Assert.AreEqual(99, cs.NewestVerifiedOrdinal, "garbage never moves the window");

            // the pre-generated keys still verify, in order
            for (var i = 100; i < 200; i++)
                Assert.IsTrue(Verify(cs, keys[i]), $"key {i}");
        }

        [TestMethod]
        public void Garbage_DoesNotEvictAPendingRetransmitKey()
        {
            var keys = Keys(100);
            var cs = New();

            for (var i = 0; i < 50; i++)
            {
                if (i != 5)
                    Assert.IsTrue(Verify(cs, keys[i]));
            }

            for (uint g = 0; g < 5000; g++)
                Assert.IsFalse(Verify(cs, 0x5A5A0000u + g));

            Assert.IsTrue(Verify(cs, keys[5]), "the lost key is still accepted when its retransmit arrives");
        }

        [TestMethod]
        public void LostKey_IsForgotten_OnceItIsMoreThanTheWindowBehind()
        {
            var keys = Keys(Window + 20);
            var cs = New();

            for (var i = 0; i < Window + 6; i++)
            {
                if (i != 5)
                    Assert.IsTrue(Verify(cs, keys[i]));
            }

            // newest verified is Window + 5; key 5 is exactly Window behind and still kept
            Assert.IsTrue(cs.Search(keys[5]), "exactly the window behind is kept");

            Assert.IsTrue(Verify(cs, keys[Window + 6]));
            Assert.IsFalse(Verify(cs, keys[5]), "one more key behind is forgotten");
        }

        [TestMethod]
        public void EffortLevel_IsClamped_AndTheStaticDefaultReachesNewInstances()
        {
            Assert.AreEqual(CryptoSystem.MinEffortLevel, CryptoSystem.ClampEffortLevel(0));
            Assert.AreEqual(CryptoSystem.MinEffortLevel, CryptoSystem.ClampEffortLevel(-5));
            Assert.AreEqual(CryptoSystem.MaxEffortLevel, CryptoSystem.ClampEffortLevel(1_000_000));
            Assert.AreEqual(5000, CryptoSystem.ClampEffortLevel(5000));
            Assert.AreEqual(256, CryptoSystem.MinEffortLevel);
            Assert.AreEqual(16384, CryptoSystem.MaxEffortLevel);
            Assert.AreEqual(4096, CryptoSystem.StandardEffortLevel);

            var saved = CryptoSystem.DefaultEffortLevel;
            try
            {
                CryptoSystem.DefaultEffortLevel = 1000;
                Assert.AreEqual(1000, new CryptoSystem(Seed).EffortLevel);

                CryptoSystem.DefaultEffortLevel = 50;
                Assert.AreEqual(256, new CryptoSystem(Seed).EffortLevel);

                var built = new CryptoSystem(Seed);
                CryptoSystem.DefaultEffortLevel = 9000;
                Assert.AreEqual(256, built.EffortLevel, "a live instance keeps its window");
            }
            finally
            {
                CryptoSystem.DefaultEffortLevel = saved;
            }
        }
    }
}
