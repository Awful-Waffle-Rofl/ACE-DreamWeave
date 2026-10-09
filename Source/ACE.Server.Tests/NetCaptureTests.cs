using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Network;
using ACE.Server.Network.GameMessages;

namespace ACE.Server.Tests
{
    [TestClass]
    public class NetCaptureTests
    {
        private const int OH = NetCaptureRing.EntryOverhead;

        private sealed class TestMessage : GameMessage
        {
            public TestMessage(byte[] payload) : base((GameMessageOpcode)0xF745, GameMessageGroup.UIQueue)
            {
                Writer.Write(payload);
            }
        }

        [TestInitialize]
        public void Init()
        {
            NetCapture.DisableAll();
            NetCapture.RingBytes = NetCaptureRing.DefaultMaxBytes;
            NetCapture.DumpDirectoryOverride = null;
        }

        [TestCleanup]
        public void Cleanup()
        {
            NetCapture.DisableAll();
            NetCapture.RingBytes = NetCaptureRing.DefaultMaxBytes;
            NetCapture.DumpDirectoryOverride = null;
        }

        private static byte[] Bytes(int n, byte fill) => Enumerable.Repeat(fill, n).ToArray();

        private static string TempDir() => Path.Combine(Path.GetTempPath(), "netcapture-test-" + Guid.NewGuid().ToString("N"));

        [TestMethod]
        public void Ring_EvictsOldestFirstByChargedByteBudget()
        {
            // Each 30-byte entry is charged 30 + overhead. Budget fits exactly three.
            var ring = new NetCaptureRing(3 * (30 + OH) + 10);
            var t = DateTime.UtcNow;
            for (byte i = 1; i <= 5; i++)
                ring.AddMessage(t, i, Bytes(30, i), 30);

            var snap = ring.Snapshot();
            Assert.AreEqual(3, snap.Length);
            Assert.AreEqual(3 * (30 + OH), ring.Bytes);
            CollectionAssert.AreEqual(new uint[] { 3, 4, 5 }, snap.Select(e => e.Opcode).ToArray());
        }

        [TestMethod]
        public void Ring_MessagesAndFragmentsShareOneBudgetAndKeepOrder()
        {
            var ring = new NetCaptureRing(2 * (40 + OH) + 10);
            var t = DateTime.UtcNow;
            ring.AddMessage(t, 1, Bytes(40, 1), 40);
            ring.AddFragment(t, 1, 7, 0, 1, 3, Bytes(40, 2));
            ring.AddMessage(t, 2, Bytes(40, 3), 40);

            var snap = ring.Snapshot();
            Assert.AreEqual(2, snap.Length);
            Assert.IsTrue(snap[0].IsFragment);
            Assert.IsFalse(snap[1].IsFragment);
            Assert.AreEqual(2 * (40 + OH), ring.Bytes);
        }

        [TestMethod]
        public void Ring_ZeroLengthEntriesAreChargedAndEvictable()
        {
            var ring = new NetCaptureRing(5 * OH);
            for (int i = 0; i < 50; i++)
                ring.AddMessage(DateTime.UtcNow, (uint)i, Array.Empty<byte>(), 0);
            Assert.AreEqual(5, ring.Count);
            Assert.AreEqual(5 * OH, ring.Bytes);
            Assert.AreEqual(45u, ring.Snapshot()[0].Opcode);
        }

        [TestMethod]
        public void Ring_OneMillionTinyAddsStayBounded()
        {
            var ring = new NetCaptureRing(1_000_000);
            var one = new byte[1];
            for (int i = 0; i < 1_000_000; i++)
                ring.AddMessage(DateTime.UtcNow, 1, one, 1);
            Assert.IsTrue(ring.Bytes <= 1_000_000);
            Assert.IsTrue(ring.Count <= 1_000_000 / (1 + OH));
            Assert.IsTrue(ring.Count > 0);
        }

        [TestMethod]
        public void Ring_CopiesInputAndHonoursLength()
        {
            var ring = new NetCaptureRing(10_000);
            var src = new byte[] { 1, 2, 3, 4, 5, 6 };
            ring.AddMessage(DateTime.UtcNow, 9, src, 4);
            src[0] = 99;
            var e = ring.Snapshot().Single();
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, e.Data);
            Assert.AreEqual(4, e.Length);
        }

        [TestMethod]
        public void Ring_TakeAndClearEmpties()
        {
            var ring = new NetCaptureRing(10_000);
            ring.AddMessage(DateTime.UtcNow, 1, Bytes(10, 1), 10);
            Assert.AreEqual(1, ring.TakeAndClear().Length);
            Assert.AreEqual(0, ring.Count);
            Assert.AreEqual(0, ring.Bytes);
        }

        [TestMethod]
        public void Ring_ThreadSafetySmoke()
        {
            var ring = new NetCaptureRing(20_000);
            Parallel.For(0, 8, w =>
            {
                for (int i = 0; i < 2000; i++)
                {
                    ring.AddMessage(DateTime.UtcNow, (uint)w, Bytes(50, (byte)w), 50);
                    if (i % 100 == 0) ring.Snapshot();
                }
            });
            Assert.IsTrue(ring.Bytes <= 20_000);
            Assert.AreEqual(ring.Snapshot().Sum(e => (long)e.Length + OH), ring.Bytes);
        }

        [TestMethod]
        public void Ring_BindOwnerWritesMarkerOnlyOnOwnerChange()
        {
            var ring = new NetCaptureRing(10_000);
            var a = new object();
            var b = new object();
            ring.BindOwner(a, "1.1.1.1:1");
            ring.BindOwner(a, "1.1.1.1:1");
            ring.BindOwner(b, "2.2.2.2:2");
            var markers = ring.Snapshot().Where(e => e.IsSessionMarker).ToArray();
            Assert.AreEqual(2, markers.Length);
            Assert.AreEqual("2.2.2.2:2", markers[1].Note);
        }

        [TestMethod]
        public void FlagMap_OnOffStatus()
        {
            Assert.IsFalse(NetCapture.AnyFlagged);
            Assert.IsTrue(NetCapture.Enable(0x50000001, "Alice"));
            Assert.IsFalse(NetCapture.Enable(0x50000001, "Alice"));
            Assert.IsTrue(NetCapture.AnyFlagged);
            NetCapture.GetRing(0x50000001).AddMessage(DateTime.UtcNow, 1, Bytes(10, 1), 10);

            var st = NetCapture.Status();
            Assert.AreEqual(1, st.Count);
            Assert.AreEqual("Alice", st[0].Name);
            Assert.AreEqual(1, st[0].Count);
            Assert.AreEqual(10 + OH, st[0].Bytes);

            var v = NetCapture.Version;
            Assert.IsTrue(NetCapture.Disable(0x50000001));
            Assert.AreNotEqual(v, NetCapture.Version);
            Assert.IsFalse(NetCapture.Disable(0x50000001));
            Assert.IsFalse(NetCapture.AnyFlagged);
            Assert.IsNull(NetCapture.GetRing(0x50000001));
            Assert.AreEqual(0, NetCapture.Status().Count);
        }

        [TestMethod]
        public void FlagMap_ParallelEnableDisableKeepsAnyFlaggedConsistent()
        {
            for (int round = 0; round < 200; round++)
            {
                Parallel.For(0, 16, i =>
                {
                    var guid = 0x50001000u + (uint)(i % 4);
                    if ((i & 1) == 0) NetCapture.Enable(guid, "P" + i);
                    else NetCapture.Disable(guid);
                });
                Assert.AreEqual(NetCapture.Status().Count > 0, NetCapture.AnyFlagged, "round " + round);
            }
            NetCapture.DisableAll();
            Assert.IsFalse(NetCapture.AnyFlagged);
        }

        [TestMethod]
        public void Binder_RefreshesAfterEnableDisable()
        {
            var binder = new NetCaptureBinder();
            var owner = new object();
            const uint guid = 0x50000010;

            Assert.IsNull(binder.Resolve(guid, owner, () => "e"));

            NetCapture.Enable(guid, "Carol");
            var ring1 = binder.Resolve(guid, owner, () => "e");
            Assert.IsNotNull(ring1);
            Assert.AreSame(NetCapture.GetRing(guid), ring1);
            Assert.AreEqual(1, ring1.Snapshot().Count(e => e.IsSessionMarker));

            NetCapture.Disable(guid);
            Assert.IsNull(binder.Resolve(guid, owner, () => "e"));

            NetCapture.Enable(guid, "Carol");
            var ring2 = binder.Resolve(guid, owner, () => "e");
            Assert.IsNotNull(ring2);
            Assert.AreNotSame(ring1, ring2);

            // A different player guid on the same binder re-resolves too.
            Assert.IsNull(binder.Resolve(0x50000011, owner, () => "e"));
        }

        [TestMethod]
        public void RecordMessage_DoesNotDisturbSharedStream()
        {
            var payload = Bytes(20, 0x5A);
            var msg = new TestMessage(payload);
            msg.Data.Position = 7; // sentinel a Seek/Read based capture would clobber
            var lengthBefore = msg.Data.Length;

            var ring = new NetCaptureRing(10_000);
            NetCapture.RecordMessage(ring, msg, DateTime.UtcNow);

            Assert.AreEqual(7, msg.Data.Position);
            Assert.AreEqual(lengthBefore, msg.Data.Length);
            var e = ring.Snapshot().Single();
            Assert.IsFalse(e.IsFragment);
            Assert.AreEqual(0xF745u, e.Opcode);
            Assert.AreEqual((int)lengthBefore, e.Length);
            CollectionAssert.AreEqual(msg.Data.ToArray(), e.Data);
        }

        [TestMethod]
        public void UnflaggedPath_RecordsNothing()
        {
            Assert.IsFalse(NetCapture.AnyFlagged);
            Assert.IsNull(NetCapture.GetRing(0x50000002));
            Assert.AreEqual(0, NetCapture.Status().Count);
            var dir = TempDir();
            NetCapture.DumpDirectoryOverride = dir;
            NetCapture.OnSessionTerminate(0x50000002, "1.2.3.4:5", "x", out var t);
            t.Wait();
            Assert.IsFalse(Directory.Exists(dir));
        }

        [TestMethod]
        public void Dump_FormatParsesAndHexRoundTrips()
        {
            var msg = new byte[] { 0x45, 0xF7, 0x00, 0x00, 0xAB, 0xCD };
            var frag = new byte[] { 0x01, 0x02, 0xFE };
            var ring = new NetCaptureRing(10_000);
            ring.BindOwner(new object(), "1.2.3.4:9000");
            ring.AddMessage(new DateTime(2026, 9, 29, 1, 2, 3, 456, DateTimeKind.Utc), 0xF745, msg, msg.Length, 4);
            ring.AddFragment(new DateTime(2026, 9, 29, 1, 2, 4, 7, DateTimeKind.Utc), 0xF745, 12, 1, 2, 4, frag);

            var sw = new StringWriter();
            NetCapture.WriteJsonLines(sw, "Al \"Q\"", 0x50000003, "1.2.3.4:9000", "NetworkTimeout", ring.Snapshot());
            var lines = sw.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.AreEqual(4, lines.Length);

            using (var h = JsonDocument.Parse(lines[0]))
            {
                var r = h.RootElement;
                Assert.AreEqual("Al \"Q\"", r.GetProperty("character").GetString());
                Assert.AreEqual("0x50000003", r.GetProperty("guid").GetString());
                Assert.AreEqual("1.2.3.4:9000", r.GetProperty("endpoint").GetString());
                Assert.AreEqual("NetworkTimeout", r.GetProperty("reason").GetString());
                Assert.AreEqual(1, r.GetProperty("msgCount").GetInt32());
                Assert.AreEqual(1, r.GetProperty("fragCount").GetInt32());
                Assert.AreEqual(1, r.GetProperty("sessionCount").GetInt32());
            }

            using (var s = JsonDocument.Parse(lines[1]))
            {
                Assert.AreEqual("session", s.RootElement.GetProperty("kind").GetString());
                Assert.AreEqual("1.2.3.4:9000", s.RootElement.GetProperty("endpoint").GetString());
            }

            using (var m = JsonDocument.Parse(lines[2]))
            {
                var r = m.RootElement;
                Assert.AreEqual("msg", r.GetProperty("kind").GetString());
                Assert.AreEqual("2026-09-29T01:02:03.456Z", r.GetProperty("t").GetString());
                Assert.AreEqual("0xF745", r.GetProperty("opcode").GetString());
                Assert.AreEqual(6, r.GetProperty("len").GetInt32());
                CollectionAssert.AreEqual(msg, Convert.FromHexString(r.GetProperty("hex").GetString()));
                Assert.AreEqual("45F70000ABCD", r.GetProperty("hex").GetString());
            }

            using (var f = JsonDocument.Parse(lines[3]))
            {
                var r = f.RootElement;
                Assert.AreEqual("frag", r.GetProperty("kind").GetString());
                Assert.AreEqual(12, r.GetProperty("seq").GetInt32());
                Assert.AreEqual(1, r.GetProperty("idx").GetInt32());
                Assert.AreEqual(2, r.GetProperty("count").GetInt32());
                Assert.AreEqual(4, r.GetProperty("group").GetInt32());
                Assert.AreEqual(3, r.GetProperty("len").GetInt32());
                CollectionAssert.AreEqual(frag, Convert.FromHexString(r.GetProperty("hex").GetString()));
            }
        }

        [TestMethod]
        public void FileName_ReasonIsTruncated()
        {
            NetCapture.DumpDirectoryOverride = TempDir();
            var path = NetCapture.PlanDumpPath("Bob", new string('x', 500));
            Assert.IsTrue(Path.GetFileName(path).Length < 120, path);
            StringAssert.Contains(Path.GetFileName(path), new string('x', 40) + ".jsonl");
        }

        [TestMethod]
        public void AutoDump_WritesFileClearsRingKeepsFlag()
        {
            var dir = TempDir();
            NetCapture.DumpDirectoryOverride = dir;
            try
            {
                NetCapture.Enable(0x50000004, "Bob");
                NetCapture.GetRing(0x50000004).AddMessage(DateTime.UtcNow, 1, Bytes(8, 7), 8);

                NetCapture.OnSessionTerminate(0x50000004, "1.2.3.4:9000", "NetworkTimeout", out var task);
                task.Wait(TimeSpan.FromSeconds(10));

                Assert.AreEqual(0, NetCapture.GetRing(0x50000004).Count);
                Assert.IsTrue(NetCapture.AnyFlagged);
                Assert.AreEqual(1, Directory.GetFiles(dir, "netcapture_Bob_*_NetworkTimeout.jsonl").Length);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        [TestMethod]
        public void ManualDump_ReturnsPathImmediatelyWritesAsyncKeepsRing()
        {
            var dir = TempDir();
            NetCapture.DumpDirectoryOverride = dir;
            try
            {
                NetCapture.Enable(0x50000005, "Dee");
                NetCapture.GetRing(0x50000005).AddMessage(DateTime.UtcNow, 1, Bytes(8, 7), 8);

                var planned = NetCapture.DumpNowAsync(0x50000005, "1.2.3.4:9000", "manual", out var task);
                Assert.IsNotNull(planned);
                task.Wait(TimeSpan.FromSeconds(10));
                NetCapture.WaitForPendingDumps(TimeSpan.FromSeconds(5));

                Assert.AreEqual(1, Directory.GetFiles(dir, "netcapture_Dee_*_manual.jsonl").Length);
                Assert.AreEqual(1, NetCapture.GetRing(0x50000005).Count);
                Assert.IsNull(NetCapture.DumpNowAsync(0x5000FFFF, "x", "manual", out _));
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }
    }
}
