using System;
using System.IO;

using ACE.Common.Cryptography;
using ACE.Server.Network;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.Network
{
    /// <summary>
    /// The inbound gap decisions NetworkSession delegates to InboundGapTracker (reorder bound, stall and queue-cap
    /// verdicts, recovery logging, lost key stream), plus source scans pinning that NetworkSession and NetworkManager
    /// actually call them, since no test here can run a live session.
    /// </summary>
    [TestClass]
    public class InboundGapTrackerTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 10, 22, 17, 26, 29, DateTimeKind.Utc);
        private static readonly TimeSpan Stall = TimeSpan.FromSeconds(30);

        [TestMethod]
        public void ReorderBound_IsTheCryptoWindow()
        {
            // the original rule: bottom = last + 2, abnormal when rcvd - bottom > window
            Assert.IsFalse(InboundGapTracker.ExceedsReorderBound(12 + 4096, 10, 4096));
            Assert.IsTrue(InboundGapTracker.ExceedsReorderBound(12 + 4097, 10, 4096));
            Assert.IsFalse(InboundGapTracker.ExceedsReorderBound(12 + 256, 10, 256));
            Assert.IsTrue(InboundGapTracker.ExceedsReorderBound(12 + 257, 10, 256));
            Assert.IsTrue(InboundGapTracker.ExceedsReorderBound(11, 10, 4096), "below bottom is abnormal, as before");

            // the 2026-10-22 shape: a gap of a few hundred ended the session at 256 and does not at 4096
            Assert.IsTrue(InboundGapTracker.ExceedsReorderBound(1000 + 600, 1000, CryptoSystem.LegacyEffortLevel));
            Assert.IsFalse(InboundGapTracker.ExceedsReorderBound(1000 + 600, 1000, CryptoSystem.StandardEffortLevel));
        }

        [TestMethod]
        public void Stall_FiresAfterTheLimit_WithNoAdvance()
        {
            var t = new InboundGapTracker();
            t.OnQueued(100, 150, T0, true);

            Assert.AreEqual(InboundGapVerdict.None, t.Check(10, 8192, T0.AddSeconds(29), Stall));
            Assert.AreEqual(InboundGapVerdict.Stalled, t.Check(10, 8192, T0.AddSeconds(30), Stall));
        }

        [TestMethod]
        public void Stall_ClockRestartsOnEveryAdvance_SoASlowRecoveryIsNotEnded()
        {
            var t = new InboundGapTracker();
            t.OnQueued(100, 2000, T0, true);

            // NAK rounds drain the gap 115 at a time; each advance restarts the clock
            for (var s = 20; s <= 200; s += 20)
            {
                Assert.AreEqual(InboundGapVerdict.None, t.Check(500, 8192, T0.AddSeconds(s), Stall), $"at {s}s");
                Assert.IsNull(t.OnAdvanced(false, T0.AddSeconds(s)));
            }

            Assert.IsTrue(t.InGap);
            Assert.AreEqual(InboundGapVerdict.Stalled, t.Check(500, 8192, T0.AddSeconds(230), Stall));
        }

        [TestMethod]
        public void Stall_OpeningAGapStartsTheClock_EvenAfterALongIdle()
        {
            // an idle client sends only ack-only packets, which never advance the sequence; its first gap must not
            // be judged stalled by the idle time before it
            var t = new InboundGapTracker();
            t.OnAdvanced(true, T0);
            t.OnQueued(100, 102, T0.AddMinutes(10), true);

            Assert.AreEqual(InboundGapVerdict.None, t.Check(1, 8192, T0.AddMinutes(10).AddSeconds(1), Stall));
        }

        [TestMethod]
        public void Stall_OffWithAZeroLimit_AndNeverWithAnEmptyQueue()
        {
            var t = new InboundGapTracker();
            t.OnQueued(100, 150, T0, true);

            Assert.AreEqual(InboundGapVerdict.None, t.Check(10, 8192, T0.AddHours(1), TimeSpan.Zero));
            Assert.AreEqual(InboundGapVerdict.None, t.Check(0, 8192, T0.AddHours(1), Stall));
            Assert.AreEqual(InboundGapVerdict.None, new InboundGapTracker().Check(0, 8192, T0.AddHours(1), Stall), "no gap open");
        }

        [TestMethod]
        public void QueueOverCap_IsOverflow_EvenWithTheStallRuleOff()
        {
            var t = new InboundGapTracker();
            t.OnQueued(100, 150, T0, true);

            Assert.AreEqual(8192, InboundGapTracker.QueueCap(4096));
            Assert.AreEqual(InboundGapVerdict.None, t.Check(8192, 8192, T0, TimeSpan.Zero));
            Assert.AreEqual(InboundGapVerdict.QueueOverflow, t.Check(8193, 8192, T0, TimeSpan.Zero));
        }

        [TestMethod]
        public void Recovery_WiderThanTheLegacyLimit_IsLogged_WithSpanTimeAndNaks()
        {
            var t = new InboundGapTracker();
            t.OnQueued(1000, 1300, T0, true);
            t.OnQueued(1000, 1600, T0.AddSeconds(1), false);
            t.OnQueued(1000, 1500, T0.AddSeconds(1), false);
            t.OnNak();
            t.OnNak();
            t.OnNak();

            Assert.AreEqual(600u, t.GapSpan);
            Assert.IsNull(t.OnAdvanced(false, T0.AddSeconds(2)), "still queued: the gap stays open");

            var r = t.OnAdvanced(true, T0.AddSeconds(4.5));
            Assert.IsNotNull(r);
            Assert.IsTrue(r.Value.Log);
            Assert.AreEqual(600u, r.Value.Span);
            Assert.AreEqual(3, r.Value.Naks);
            Assert.AreEqual(4500, (long)r.Value.Elapsed.TotalMilliseconds);
            Assert.IsFalse(t.InGap);
        }

        [TestMethod]
        public void Recovery_WithinTheLegacyLimit_IsNotLogged()
        {
            var t = new InboundGapTracker();
            t.OnQueued(1000, 1256, T0, true);

            var r = t.OnAdvanced(true, T0.AddSeconds(1));
            Assert.IsNotNull(r);
            Assert.IsFalse(r.Value.Log);
            Assert.AreEqual(256u, r.Value.Span);
        }

        [TestMethod]
        public void Recovery_Logging_IsRateLimitedPerSession_AndCountsWhatItSkipped()
        {
            var t = new InboundGapTracker();
            var now = T0;

            InboundGapRecovery? Gap(uint start)
            {
                t.OnQueued(start, start + 1000, now, true);
                now = now.AddSeconds(1);
                return t.OnAdvanced(true, now);
            }

            Assert.IsTrue(Gap(1000).Value.Log);
            Assert.IsFalse(Gap(3000).Value.Log);
            Assert.IsFalse(Gap(5000).Value.Log);

            now = now.AddSeconds(10);
            var r = Gap(7000).Value;
            Assert.IsTrue(r.Log);
            Assert.AreEqual(2, r.Suppressed);
        }

        [TestMethod]
        public void KeyStreamLost_NeedsThreeFailuresInARow_OverTheLimit()
        {
            var t = new InboundGapTracker();

            // a client that keeps sending and keeps failing: one failure every 2 s
            for (var s = 0; s <= 28; s += 2)
            {
                t.OnKeyFailure(T0.AddSeconds(s));
                Assert.IsFalse(t.KeyStreamLost(T0.AddSeconds(s), Stall), $"under the limit at {s}s");
            }

            t.OnKeyFailure(T0.AddSeconds(30));
            Assert.IsTrue(t.KeyStreamLost(T0.AddSeconds(30), Stall));
            Assert.IsFalse(t.KeyStreamLost(T0.AddSeconds(30), TimeSpan.Zero), "0 is off");

            // a success restarts the run
            t.OnKeyVerified();
            t.OnKeyFailure(T0.AddSeconds(31));
            Assert.IsFalse(t.KeyStreamLost(T0.AddSeconds(31), Stall));
        }

        [TestMethod]
        public void KeyStreamLost_TwoFailuresAreNotEnough_HoweverLong()
        {
            var t = new InboundGapTracker();
            t.OnKeyFailure(T0);
            t.OnKeyFailure(T0.AddSeconds(4));
            Assert.IsFalse(t.KeyStreamLost(T0.AddSeconds(40), Stall));
        }

        [TestMethod]
        public void KeyStreamLost_StrayFailureAfterSilence_StartsANewRun()
        {
            // 3 failures, 40 s of silence, then one stray failure: not a lost key stream
            var t = new InboundGapTracker();
            t.OnKeyFailure(T0);
            t.OnKeyFailure(T0.AddSeconds(1));
            t.OnKeyFailure(T0.AddSeconds(2));

            t.OnKeyFailure(T0.AddSeconds(42));
            Assert.AreEqual(1, t.KeyFailuresInRow);
            Assert.IsFalse(t.KeyStreamLost(T0.AddSeconds(42), Stall));

            // and the lazy read with no new failure is not lost either
            var u = new InboundGapTracker();
            u.OnKeyFailure(T0);
            u.OnKeyFailure(T0.AddSeconds(1));
            u.OnKeyFailure(T0.AddSeconds(2));
            Assert.IsFalse(u.KeyStreamLost(T0.AddSeconds(42), Stall));
        }

        [TestMethod]
        public void Stall_QueueDrainedWithoutAdvance_DoesNotLeaveAStaleClock()
        {
            // a queued ack-only packet is drained without the sequence moving; NetworkSession closes the gap
            var t = new InboundGapTracker();
            t.OnQueued(100, 102, T0, true);
            t.OnDrainedWithoutAdvance();
            Assert.IsFalse(t.InGap);

            // 31 s idle, then a new out-of-order packet
            t.OnQueued(100, 103, T0.AddSeconds(31), true);
            Assert.AreEqual(InboundGapVerdict.None, t.Check(1, 8192, T0.AddSeconds(31), Stall));
        }

        [TestMethod]
        public void Stall_QueuingIntoAnEmptyQueue_RestartsTheClock_EvenIfAGapLooksOpen()
        {
            // the second guard: had the close been missed, a queue event into an empty queue still opens a fresh gap
            var t = new InboundGapTracker();
            t.OnQueued(100, 102, T0, true);

            t.OnQueued(100, 103, T0.AddSeconds(31), true);
            Assert.AreEqual(InboundGapVerdict.None, t.Check(1, 8192, T0.AddSeconds(31), Stall));
            Assert.AreEqual(3u, t.GapSpan, "a fresh gap from the new packet");

            // control: the same events with the queue not empty keep the old clock and fire
            var c = new InboundGapTracker();
            c.OnQueued(100, 102, T0, true);
            c.OnQueued(100, 103, T0.AddSeconds(31), false);
            Assert.AreEqual(InboundGapVerdict.Stalled, c.Check(2, 8192, T0.AddSeconds(31), Stall));
        }

        [TestMethod]
        public void Settings_Clamp()
        {
            Assert.AreEqual(0, InboundRecoverySettings.ClampStallSeconds(0));
            Assert.AreEqual(0, InboundRecoverySettings.ClampStallSeconds(-3));
            Assert.AreEqual(30, InboundRecoverySettings.ClampStallSeconds(30));
            Assert.AreEqual(600, InboundRecoverySettings.ClampStallSeconds(100000));
            Assert.AreEqual(4096L, InboundRecoverySettings.DefaultCryptoLookahead);
            Assert.AreEqual(30L, InboundRecoverySettings.DefaultStallSeconds);
        }

        // ---- wiring, by source scan ----

        private static string ReadServerSource(string relativePath)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Source", "ACE.Server", "WorldObjects")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, "repo root (a directory containing Source/ACE.Server/WorldObjects) not found above the test output dir");

            var path = Path.Combine(dir.FullName, "Source", relativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.IsTrue(File.Exists(path), $"expected source file not found: {path}");

            return File.ReadAllText(path).Replace("\r\n", "\n");
        }

        private static string MethodBody(string source, string signature)
        {
            var at = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.IsTrue(at >= 0, $"method not found: {signature}");

            var open = source.IndexOf('{', at);
            var depth = 0;
            for (var i = open; i < source.Length; i++)
            {
                if (source[i] == '{')
                    depth++;
                else if (source[i] == '}' && --depth == 0)
                    return source.Substring(open, i - open + 1);
            }

            Assert.Fail($"unbalanced braces after {signature}");
            return null;
        }

        private static bool InOrder(string body, params string[] needles)
        {
            var at = 0;
            foreach (var needle in needles)
            {
                at = body.IndexOf(needle, at, StringComparison.Ordinal);
                if (at < 0)
                    return false;
                at += needle.Length;
            }
            return true;
        }

        [TestMethod]
        public void NetworkSession_ReorderBound_UsesTheSessionCryptoWindow()
        {
            var body = MethodBody(ReadServerSource("ACE.Server/Network/NetworkSession.cs"), "private void DoRequestForRetransmission(uint rcvdSeq)");

            Assert.IsTrue(body.Contains("InboundGapTracker.ExceedsReorderBound(rcvdSeq, lastReceivedPacketSequence, ConnectionData.CryptoClient.EffortLevel)"));
            Assert.IsFalse(body.Contains("MaximumEffortLevel"), "the fixed 256 bound is gone");
            Assert.IsTrue(InOrder(body, "EnqueueSend(reqPacket);", "gapTracker.OnNak();"));
        }

        [TestMethod]
        public void NetworkSession_ProcessPacket_FeedsTheTracker()
        {
            var body = MethodBody(ReadServerSource("ACE.Server/Network/NetworkSession.cs"), "public void ProcessPacket(ClientPacket packet, int dataSize)");

            Assert.IsTrue(InOrder(body,
                "if (!packet.VerifyCRC(ConnectionData.CryptoClient))",
                "NoteKeyFailure();",
                "return;",
                "gapTracker.OnKeyVerified();"));

            Assert.IsTrue(InOrder(body,
                "outOfOrderPackets.TryAdd(packet.Header.Sequence, packet);",
                "gapTracker.OnQueued(lastReceivedPacketSequence, packet.Header.Sequence, now, queueWasEmpty);",
                "DoRequestForRetransmission(packet.Header.Sequence);",
                "CheckInboundGap(now);",
                "return;",
                "HandleOrderedPacket(packet);",
                "CheckOutOfOrderPackets();",
                "NoteSequenceAdvanced();",
                "else if (gapTracker.InGap && outOfOrderPackets.IsEmpty)",
                "gapTracker.OnDrainedWithoutAdvance();"));

            Assert.IsTrue(InOrder(body, "var queueWasEmpty = outOfOrderPackets.IsEmpty;", "outOfOrderPackets.TryAdd(packet.Header.Sequence, packet);"));

            // discrimination
            Assert.IsFalse(InOrder(body.Replace("CheckInboundGap(now);", ""), "DoRequestForRetransmission(packet.Header.Sequence);", "CheckInboundGap(now);", "return;", "HandleOrderedPacket(packet);"));
        }

        [TestMethod]
        public void NetworkManager_RefreshesTheSettingsOncePerSessionWork()
        {
            var work = MethodBody(ReadServerSource("ACE.Server/Network/Managers/NetworkManager.cs"), "public static int DoSessionWork()");
            Assert.IsTrue(InOrder(work, "InboundRecoverySettings.Refresh();", "Parallel.ForEach(sessionMap,"));
        }
    }
}
