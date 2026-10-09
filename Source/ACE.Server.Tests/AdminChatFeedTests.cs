using System;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Managers.Market;

namespace ACE.Server.Tests
{
    /// <summary>
    /// AdminChatFeed: pure, no DB, no PropertyManager (PLAN-P3.md section 5). Every since-mode Read
    /// passes feed.Generation unless the test is specifically about generation.
    /// </summary>
    [TestClass]
    public class AdminChatFeedTests
    {
        private static AdminChatFeed NewFeed(int capacity = AdminChatFeed.Capacity, Func<DateTime> utcNow = null)
            => new AdminChatFeed(capacity, utcNow);

        [TestMethod]
        public void Capture_AssignsContiguousSeqFromOne()
        {
            var feed = NewFeed();

            feed.Capture(ChatType.General, TurbineChatChannel.General, "a", "one");
            feed.Capture(ChatType.General, TurbineChatChannel.General, "b", "two");
            feed.Capture(ChatType.General, TurbineChatChannel.General, "c", "three");

            var page = feed.Read(null, feed.Generation, null);

            CollectionAssert.AreEqual(new long[] { 1, 2, 3 }, page.Lines.Select(l => l.Seq).ToArray());
            Assert.AreEqual(3, page.HeadSeq);
        }

        // TurbineChatChannel's ids are `public static uint` fields, not const, so they cannot appear
        // as DataRow arguments (which must be compile-time constants) - the literals below are
        // asserted equal to those fields in Capture_NonPublicChannels_ChannelIdLiteralsMatchSource.
        [DataTestMethod]
        [DataRow((uint)ChatType.Allegiance, 1u)]
        [DataRow((uint)ChatType.Society, 6u)]
        [DataRow((uint)ChatType.SocietyCelHan, 7u)]
        [DataRow((uint)ChatType.SocietyEldWeb, 8u)]
        [DataRow((uint)ChatType.SocietyRadBlo, 9u)]
        [DataRow((uint)ChatType.Olthoi, 10u)]
        [DataRow((uint)ChatType.Undef, 0u)]
        public void Capture_NonPublicChannels_CaptureNothing(uint chatTypeValue, uint channelId)
        {
            var feed = NewFeed();
            feed.Capture((ChatType)chatTypeValue, channelId, "x", "hi");

            var page = feed.Read(null, feed.Generation, null);

            Assert.AreEqual(0, page.HeadSeq);
            Assert.AreEqual(0, page.Lines.Count);
        }

        [TestMethod]
        public void Capture_NonPublicChannels_ChannelIdLiteralsMatchSource()
        {
            Assert.AreEqual(1u, TurbineChatChannel.Allegiance);
            Assert.AreEqual(6u, TurbineChatChannel.Society);
            Assert.AreEqual(7u, TurbineChatChannel.SocietyCelestialHand);
            Assert.AreEqual(8u, TurbineChatChannel.SocietyEldrytchWeb);
            Assert.AreEqual(9u, TurbineChatChannel.SocietyRadiantBlood);
            Assert.AreEqual(10u, TurbineChatChannel.Olthoi);
        }

        [DataTestMethod]
        [DataRow((uint)ChatType.General, "general")]
        [DataRow((uint)ChatType.Trade, "trade")]
        [DataRow((uint)ChatType.LFG, "lfg")]
        [DataRow((uint)ChatType.Roleplay, "roleplay")]
        public void Capture_PublicChannelsOnly(uint chatTypeValue, string expectedLabel)
        {
            var chatType = (ChatType)chatTypeValue;

            var channelId = chatType switch
            {
                ChatType.General => TurbineChatChannel.General,
                ChatType.Trade => TurbineChatChannel.Trade,
                ChatType.LFG => TurbineChatChannel.LFG,
                ChatType.Roleplay => TurbineChatChannel.Roleplay,
                _ => throw new InvalidOperationException(),
            };

            var feed = NewFeed();
            feed.Capture(chatType, channelId, "Weftwalker", "hi");

            var page = feed.Read(null, feed.Generation, null);

            Assert.AreEqual(1, page.Lines.Count);
            Assert.AreEqual(expectedLabel, page.Lines[0].Channel);
        }

        [TestMethod]
        public void Capture_ChannelIdMismatch_Skipped()
        {
            var feed = NewFeed();

            // General chat type carrying the Allegiance channel id - the spoofed-channel-id case.
            feed.Capture(ChatType.General, TurbineChatChannel.Allegiance, "x", "hi");

            var page = feed.Read(null, feed.Generation, null);

            Assert.AreEqual(0, page.HeadSeq);
            Assert.AreEqual(0, page.Lines.Count);
        }

        [TestMethod]
        public void Buffer_WrapsAround_OldestEvicted()
        {
            var feed = NewFeed(500);

            for (var i = 0; i < 750; i++)
                feed.Capture(ChatType.General, TurbineChatChannel.General, "x", $"msg{i}");

            var page = feed.Read(null, feed.Generation, 500);

            Assert.AreEqual(251, page.OldestSeq);
            Assert.AreEqual(750, page.HeadSeq);

            var seqs = page.Lines.Select(l => l.Seq).ToArray();
            Assert.AreEqual(551, seqs.First());
            Assert.AreEqual(750, seqs.Last());
            CollectionAssert.AreEqual(Enumerable.Range(551, 200).Select(i => (long)i).ToArray(), seqs);
        }

        [TestMethod]
        public void Read_SinceIsExclusive()
        {
            var feed = NewFeed();
            for (var i = 0; i < 20; i++)
                feed.Capture(ChatType.General, TurbineChatChannel.General, "x", $"msg{i}");

            var page = feed.Read(10, feed.Generation, null);

            Assert.AreEqual(11, page.Lines.First().Seq);
            Assert.IsFalse(page.Gap);
        }

        [TestMethod]
        public void Read_SinceAtHead_NoLines_NoGap_CursorUnchanged()
        {
            var feed = NewFeed();
            for (var i = 0; i < 10; i++)
                feed.Capture(ChatType.General, TurbineChatChannel.General, "x", $"msg{i}");

            var page = feed.Read(10, feed.Generation, null);

            Assert.IsFalse(page.Gap);
            Assert.AreEqual(0, page.Lines.Count);
            Assert.AreEqual(10, page.NextSince);
            Assert.IsFalse(page.HasMore);
        }

        [TestMethod]
        public void Read_SinceEqualsOldestMinusOne_NoGap()
        {
            var feed = NewFeed(500);
            for (var i = 0; i < 750; i++)
                feed.Capture(ChatType.General, TurbineChatChannel.General, "x", $"msg{i}");

            // oldest_seq is 251, so 250 is the boundary.
            var page = feed.Read(250, feed.Generation, 10);

            Assert.IsFalse(page.Gap);
            Assert.AreEqual(251, page.Lines.First().Seq);
        }

        [TestMethod]
        public void Read_SinceOlderThanBuffer_GapTrue_ServedFromOldest()
        {
            var feed = NewFeed(500);
            for (var i = 0; i < 750; i++)
                feed.Capture(ChatType.General, TurbineChatChannel.General, "x", $"msg{i}");

            var page = feed.Read(5, feed.Generation, null);

            Assert.IsTrue(page.Gap);
            Assert.AreEqual(251, page.Lines.First().Seq);
        }

        [TestMethod]
        public void Read_SinceAheadOfHead_GapTrue_ServedFromOldest()
        {
            var feed = NewFeed();
            for (var i = 0; i < 10; i++)
                feed.Capture(ChatType.General, TurbineChatChannel.General, "x", $"msg{i}");

            var page = feed.Read(1000, feed.Generation, null);

            Assert.IsTrue(page.Gap);
            Assert.AreEqual(1, page.Lines.First().Seq);
        }

        [TestMethod]
        public void Read_GenerationMismatch_GapTrue()
        {
            var feed = NewFeed();
            for (var i = 0; i < 10; i++)
                feed.Capture(ChatType.General, TurbineChatChannel.General, "x", $"msg{i}");

            var page = feed.Read(5, "not-the-real-generation", null);

            Assert.IsTrue(page.Gap);
        }

        [TestMethod]
        public void Read_GenerationMatch_NoGap()
        {
            var feed = NewFeed();
            for (var i = 0; i < 10; i++)
                feed.Capture(ChatType.General, TurbineChatChannel.General, "x", $"msg{i}");

            var page = feed.Read(5, feed.Generation, null);

            Assert.IsFalse(page.Gap);
        }

        [DataTestMethod]
        [DataRow(null)]
        [DataRow("")]
        public void Read_SinceWithoutGeneration_GapTrue(string generation)
        {
            var feed = NewFeed();
            for (var i = 0; i < 10; i++)
                feed.Capture(ChatType.General, TurbineChatChannel.General, "x", $"msg{i}");

            var page = feed.Read(5, generation, null);

            Assert.IsTrue(page.Gap);
            Assert.AreEqual(1, page.Lines.First().Seq);
        }

        [TestMethod]
        public void Read_EmptyBuffer_SincePositive_GapTrue_NextSinceZero()
        {
            var feed = NewFeed();

            var page = feed.Read(5, feed.Generation, null);

            Assert.IsTrue(page.Gap);
            Assert.AreEqual(0, page.Lines.Count);
            Assert.AreEqual(0, page.NextSince);
        }

        [TestMethod]
        public void Read_NoSince_TailMode()
        {
            var feed = NewFeed();
            for (var i = 0; i < 10; i++)
                feed.Capture(ChatType.General, TurbineChatChannel.General, "x", $"msg{i}");

            var page = feed.Read(null, null, 5);

            Assert.IsFalse(page.Gap);
            Assert.AreEqual(5, page.Lines.Count);
            CollectionAssert.AreEqual(new long[] { 6, 7, 8, 9, 10 }, page.Lines.Select(l => l.Seq).ToArray());
            Assert.AreEqual(10, page.NextSince);
            Assert.IsFalse(page.HasMore);
        }

        [DataTestMethod]
        [DataRow(0, 200)]
        [DataRow(1000, 200)]
        [DataRow(5, 5)]
        public void Read_LimitClamped(int requestedLimit, int expectedCount)
        {
            var feed = NewFeed();
            for (var i = 0; i < 250; i++)
                feed.Capture(ChatType.General, TurbineChatChannel.General, "x", $"msg{i}");

            var page = feed.Read(0, feed.Generation, requestedLimit);

            Assert.AreEqual(expectedCount, page.Lines.Count);
            Assert.IsTrue(page.HasMore);
            Assert.AreEqual(page.Lines.Last().Seq, page.NextSince);
        }

        [TestMethod]
        public void Capture_TruncatesAt1000_FlagSet_NeverSplitsSurrogate()
        {
            var feed = NewFeed();

            var over = new string('a', 1500);
            feed.Capture(ChatType.General, TurbineChatChannel.General, "x", over);

            var page = feed.Read(null, feed.Generation, null);
            var line = page.Lines.Single();

            Assert.AreEqual(1000, line.Text.Length);
            Assert.IsTrue(line.Truncated);

            // A surrogate pair sitting exactly across the 1000-char boundary must not be split.
            var surrogatePair = "😀"; // one emoji, 2 UTF-16 chars
            var prefix = new string('a', 999);
            var withSurrogateAtBoundary = prefix + surrogatePair + new string('b', 100);

            feed.Capture(ChatType.General, TurbineChatChannel.General, "x", withSurrogateAtBoundary);
            var page2 = feed.Read(null, feed.Generation, null);
            var line2 = page2.Lines.Last();

            Assert.AreEqual(999, line2.Text.Length, "the boundary character was the high surrogate of a pair, so the cut must back off to 999");
            Assert.IsTrue(line2.Truncated);
            Assert.IsFalse(char.IsHighSurrogate(line2.Text[line2.Text.Length - 1]));
        }

        [TestMethod]
        public void Capture_SenderCappedAt64_NullSenderEmpty()
        {
            var feed = NewFeed();

            feed.Capture(ChatType.General, TurbineChatChannel.General, new string('n', 100), "hi");
            feed.Capture(ChatType.General, TurbineChatChannel.General, null, "hi2");

            var page = feed.Read(null, feed.Generation, null);

            Assert.AreEqual(64, page.Lines[0].Sender.Length);
            Assert.AreEqual(string.Empty, page.Lines[1].Sender);
        }

        [TestMethod]
        public void Capture_EmptyText_Skipped()
        {
            var feed = NewFeed();

            feed.Capture(ChatType.General, TurbineChatChannel.General, "x", "");
            feed.Capture(ChatType.General, TurbineChatChannel.General, "x", null);

            var page = feed.Read(null, feed.Generation, null);

            Assert.AreEqual(0, page.HeadSeq);
            Assert.AreEqual(0, page.Lines.Count);
        }

        [TestMethod]
        public void Capture_NeverThrows_WhenClockThrows()
        {
            var feed = NewFeed(AdminChatFeed.Capacity, () => throw new InvalidOperationException("clock broke"));

            feed.Capture(ChatType.General, TurbineChatChannel.General, "x", "hi");

            Assert.AreEqual(1, feed.DroppedCount);
            Assert.AreEqual(0, feed.Read(null, feed.Generation, null).HeadSeq);
        }

        [TestMethod]
        public async Task Capture_ConcurrentWithReads()
        {
            var feed = NewFeed();
            var tasks = new Task[8];
            var readerFailed = false;

            var cts = new System.Threading.CancellationTokenSource();

            var reader = Task.Run(() =>
            {
                try
                {
                    while (!cts.IsCancellationRequested)
                    {
                        var page = feed.Read(null, feed.Generation, null);

                        for (var i = 1; i < page.Lines.Count; i++)
                        {
                            if (page.Lines[i].Seq != page.Lines[i - 1].Seq + 1)
                                readerFailed = true;
                        }
                    }
                }
                catch (Exception)
                {
                    readerFailed = true;
                }
            });

            for (var t = 0; t < 8; t++)
            {
                tasks[t] = Task.Run(() =>
                {
                    for (var i = 0; i < 1000; i++)
                        feed.Capture(ChatType.General, TurbineChatChannel.General, "x", "hi");
                });
            }

            await Task.WhenAll(tasks);
            cts.Cancel();
            await reader;

            Assert.IsFalse(readerFailed, "the reader observed a non-contiguous page or an exception");
            Assert.AreEqual(8000, feed.Read(null, feed.Generation, null).HeadSeq);
        }

        [TestMethod]
        public void Buffer_MemoryBound()
        {
            var feed = NewFeed(500);

            // The packed-length maximum a TurbineChat message can carry.
            var bigMessage = new string('x', 32767);

            for (var i = 0; i < 2000; i++)
                feed.Capture(ChatType.General, TurbineChatChannel.General, "x", bigMessage);

            var page = feed.Read(null, feed.Generation, 200);

            Assert.IsTrue(page.Lines.Count <= 200);
            foreach (var line in page.Lines)
                Assert.IsTrue(line.Text.Length <= AdminChatFeed.MaxTextChars);
        }
    }
}
