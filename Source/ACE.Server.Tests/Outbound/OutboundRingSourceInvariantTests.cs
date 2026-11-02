using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.Outbound
{
    /// <summary>
    /// Where the outbound ring hooks into the network code, pinned by source scan because no test here can run a live
    /// session: the record call sits in SendBundle's dequeue loop and nowhere in EnqueueSend, the inbound anchor sits
    /// beside lastPacketReceivedTicks, the record is isolated in try/catch, the ring core reads no setting, and the
    /// dropped line only gains an appended ring= tail taken before the line and before ReleaseResources. Each matcher is
    /// also run against a copy with the guarded text removed, to prove it discriminates.
    /// </summary>
    [TestClass]
    public class OutboundRingSourceInvariantTests
    {
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
            Assert.AreEqual(-1, source.IndexOf(signature, at + signature.Length, StringComparison.Ordinal), $"signature is ambiguous: {signature}");

            var open = source.IndexOf('{', at);
            var depth = 0;

            for (var i = open; i < source.Length; i++)
            {
                if (source[i] == '{')
                    depth++;
                else if (source[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                        return source.Substring(open, i - open + 1);
                }
            }

            Assert.Fail($"unbalanced braces after {signature}");
            return null;
        }

        private static bool InOrder(string body, params string[] needles)
        {
            var from = 0;

            foreach (var needle in needles)
            {
                var at = body.IndexOf(needle, from, StringComparison.Ordinal);
                if (at < 0)
                    return false;

                from = at + needle.Length;
            }

            return true;
        }

        private static readonly string[] RingTokens = { "RecordOutbound", "outboundRing", "OutboundRing" };

        private static readonly string[] DequeueRecord =
        {
            "var ringStamp = ringOn ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;",
            "while (bundle.HasMoreMessages)",
            "var message = bundle.Dequeue();",
            "var fragment = new MessageFragment(message, ConnectionData.FragmentSequence++);",
            "if (ringOn)",
            "fragment.RingIndex = RecordOutbound(message, ringStamp);",
            "fragments.Add(fragment);",
        };

        [TestMethod]
        public void RecordCall_IsInSendBundlesDequeueLoop_AndNeverInEnqueueSend()
        {
            var source = ReadServerSource("ACE.Server/Network/NetworkSession.cs");
            var sendBundle = MethodBody(source, "private void SendBundle(NetworkBundle bundle, GameMessageGroup group)");

            Assert.IsTrue(InOrder(sendBundle, DequeueRecord), "the record must sit in the dequeue loop, one timestamp per bundle before it");
            Assert.AreEqual(1, Regex.Matches(sendBundle, Regex.Escape("Stopwatch.GetTimestamp()")).Count, "one timestamp per bundle, not per message");
            Assert.AreEqual(3, Regex.Matches(sendBundle, Regex.Escape("TagFinalPiece(")).Count, "every piece placement is tagged");

            foreach (var signature in new[]
            {
                "public void EnqueueSend(params GameMessage[] messages)",
                "public void EnqueueSend(IEnumerable<GameMessage> messages)",
                "public void EnqueueSend(params ServerPacket[] packets)",
                "private void EnqueueIntoBundle(GameMessage message)",
            })
            {
                var body = MethodBody(source, signature);
                foreach (var token in RingTokens)
                    Assert.IsFalse(body.Contains(token), $"{signature} must not touch the ring ({token})");
            }

            // discrimination
            Assert.IsFalse(InOrder(sendBundle.Replace("fragment.RingIndex = RecordOutbound(message, ringStamp);", ""), DequeueRecord));
        }

        [TestMethod]
        public void RecordAndSequenceNote_AreIsolatedInTryCatch_AndTheStampNotesTheSequence()
        {
            var source = ReadServerSource("ACE.Server/Network/NetworkSession.cs");

            var record = MethodBody(source, "private long RecordOutbound(GameMessage message, long stamp)");
            var recordOrder = new[] { "try", "OutboundMessageDecode.Decode(message,", "outboundRing.Record(", "catch (Exception ex)", "Interlocked.Exchange(ref ringFailureLogged, 1) == 0", "return 0;" };
            Assert.IsTrue(InOrder(record, recordOrder));
            Assert.IsFalse(record.Contains("PropertyManager"));

            var note = MethodBody(source, "private void NoteStampedRingEntries(ServerPacket packet)");
            Assert.IsTrue(InOrder(note, "try", "outboundRing.NotePacketSeq(", "catch (Exception ex)"));

            var stamp = MethodBody(source, "private void StampAndSend(ServerPacket packet)");
            Assert.IsTrue(InOrder(stamp, "packet.Header.Sequence = ConnectionData.PacketSequence.NextValue;", "NoteStampedRingEntries(packet);", "SendPacket(packet);"));

            Assert.IsFalse(InOrder(record.Replace("catch (Exception ex)", ""), recordOrder));
        }

        [TestMethod]
        public void InboundAnchor_SitsBesideLastPacketReceivedTicks()
        {
            var body = MethodBody(ReadServerSource("ACE.Server/Network/NetworkSession.cs"), "public void ProcessPacket(ClientPacket packet, int dataSize)");
            var order = new[]
            {
                "Interlocked.Exchange(ref lastPacketReceivedTicks, DateTime.UtcNow.Ticks);",
                "outboundRing.NoteInbound(System.Diagnostics.Stopwatch.GetTimestamp());",
                "if (!packet.VerifyCRC(ConnectionData.CryptoClient))",
            };

            Assert.IsTrue(InOrder(body, order));
            Assert.IsFalse(InOrder(body.Replace("outboundRing.NoteInbound(", ""), order));
        }

        [TestMethod]
        public void RingCoreAndDecoder_ReadNoSettings()
        {
            foreach (var file in new[] { "ACE.Server/Network/OutboundRing.cs", "ACE.Server/Network/OutboundMessageDecode.cs" })
            {
                var text = ReadServerSource(file);
                Assert.IsFalse(text.Contains("PropertyManager"), file);
                Assert.IsFalse(text.Contains("OutboundRingSettings"), file);
            }

            // settings are read once per DoSessionWork, before the outbound tick
            var work = MethodBody(ReadServerSource("ACE.Server/Network/Managers/NetworkManager.cs"), "public static int DoSessionWork()");
            Assert.IsTrue(InOrder(work, "OutboundRingSettings.Refresh();", "Parallel.ForEach(sessionMap,", "session.DropSession();", "OutboundRingMonitor.OnSessionWork(sessionMap);"));
        }

        [TestMethod]
        public void DroppedLine_GainsOnlyAnAppendedRingTail_TakenBeforeTheLine()
        {
            var source = ReadServerSource("ACE.Server/Network/Session.cs");
            var drop = MethodBody(source, "public void DropSession()");

            var order = new[]
            {
                "dropRingTail = TakeOutboundRingDump(reason);",
                "log.Info($\"Session {Network?.ClientId}\\\\{EndPointC2S} dropped. Account: {Account}, Player: {Player?.Name}{reas}{DropDiagnostics()}\");",
                "Network.ReleaseResources();",
            };

            Assert.IsTrue(InOrder(drop, order), "the dump is snapshotted before the line, and resources are released after it");

            var diagnostics = MethodBody(source, "private string DropDiagnostics()");
            Assert.IsTrue(InOrder(diagnostics, "ledgerEvictions={ledgerEvictions}{dropRingTail}\";", "\" | diagnostics-unavailable\" + dropRingTail;"),
                "the ring tail is the LAST thing on the line");

            Assert.IsFalse(InOrder(drop.Replace("dropRingTail = TakeOutboundRingDump(reason);", ""), order));
        }

        [TestMethod]
        public void NoRingFileReadsTheRawClientAddress()
        {
            foreach (var file in new[] { "ACE.Server/Network/OutboundRing.cs", "ACE.Server/Network/OutboundMessageDecode.cs", "ACE.Server/Network/OutboundRingMonitor.cs", "ACE.Server/Network/OutboundRingSettings.cs" })
            {
                var text = ReadServerSource(file);
                Assert.IsFalse(text.Contains("EndPoint"), file);
                Assert.IsFalse(text.Contains("IPAddress"), file);
            }
        }
    }
}
