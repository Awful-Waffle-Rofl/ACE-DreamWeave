using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ACE.Server.Managers.Market;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    [TestClass]
    public class WebLaunchCodeStoreTests
    {
        private DateTime now;
        private WebLaunchCodeStore store;

        [TestInitialize]
        public void Setup()
        {
            now = new DateTime(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);
            store = new WebLaunchCodeStore(() => now);
        }

        [TestMethod]
        public void TryIssue_ReturnsA43CharBase64UrlCode()
        {
            Assert.IsTrue(store.TryIssue(7, 0x50000001, out var c));
            StringAssert.Matches(c, new Regex("^[A-Za-z0-9_-]{43}$"));
        }

        [TestMethod]
        public void TryRedeem_ValidCode_ReturnsAccountExactlyOnce()
        {
            Assert.IsTrue(store.TryIssue(7, 0x50000001, out var c));
            Assert.IsTrue(store.TryRedeem(c, out var a, out var g));
            Assert.AreEqual(7u, a);
            Assert.AreEqual(0x50000001u, g);
            Assert.IsFalse(store.TryRedeem(c, out _, out _));
        }

        [TestMethod]
        public void TryRedeem_ConcurrentRedeemsOfOneCode_ExactlyOneWins()
        {
            Assert.IsTrue(store.TryIssue(7, 1, out var c));

            var wins = 0;
            Parallel.For(0, 64, new ParallelOptions { MaxDegreeOfParallelism = 64 }, _ =>
            {
                if (store.TryRedeem(c, out uint _, out uint _))
                    Interlocked.Increment(ref wins);
            });

            Assert.AreEqual(1, wins);
            Assert.AreEqual(0, store.Count);
        }

        [TestMethod]
        public void TryRedeem_At60Seconds_FailsAndConsumes()
        {
            Assert.IsTrue(store.TryIssue(7, 1, out var c));
            now += TimeSpan.FromSeconds(60);
            Assert.IsFalse(store.TryRedeem(c, out _, out _));
            now -= TimeSpan.FromSeconds(60);
            Assert.IsFalse(store.TryRedeem(c, out _, out _));
        }

        [TestMethod]
        public void TryRedeem_At59Seconds_Succeeds()
        {
            Assert.IsTrue(store.TryIssue(7, 1, out var c));
            now += TimeSpan.FromSeconds(59);
            Assert.IsTrue(store.TryRedeem(c, out var a, out _));
            Assert.AreEqual(7u, a);
        }

        [TestMethod]
        public void TryRedeem_UnknownNullEmptyOversized_False()
        {
            Assert.IsTrue(store.TryIssue(7, 1, out _));
            Assert.IsFalse(store.TryRedeem(null, out _, out _));
            Assert.IsFalse(store.TryRedeem("", out _, out _));
            Assert.IsFalse(store.TryRedeem(new string('A', 43), out _, out _));
            Assert.IsFalse(store.TryRedeem(new string('A', 5000), out _, out _));
            Assert.AreEqual(1, store.Count);
        }

        [TestMethod]
        public void TryIssue_KeepsThreeOutstanding_FourthRevokesOldest()
        {
            Assert.IsTrue(store.TryIssue(7, 1, out var c1));
            now += TimeSpan.FromSeconds(1);
            Assert.IsTrue(store.TryIssue(7, 1, out var c2));
            now += TimeSpan.FromSeconds(1);
            Assert.IsTrue(store.TryIssue(7, 1, out var c3));
            now += TimeSpan.FromSeconds(1);
            Assert.IsTrue(store.TryIssue(7, 1, out var c4));

            Assert.IsFalse(store.TryRedeem(c1, out _, out _));
            Assert.IsTrue(store.TryRedeem(c2, out _, out _));
            Assert.IsTrue(store.TryRedeem(c3, out _, out _));
            Assert.IsTrue(store.TryRedeem(c4, out _, out _));
        }

        [TestMethod]
        public void TryIssue_RevocationNeverCrossesAccounts()
        {
            Assert.IsTrue(store.TryIssue(7, 1, out var mine));
            for (var i = 0; i < 4; i++)
            {
                now += TimeSpan.FromSeconds(1);
                Assert.IsTrue(store.TryIssue(8, 2, out _));
            }

            Assert.IsTrue(store.TryRedeem(mine, out var a, out _));
            Assert.AreEqual(7u, a);
        }

        [TestMethod]
        public void TryIssue_EleventhInAMinute_Refused_ThenAllowedAfter60s()
        {
            for (var i = 0; i < 10; i++)
                Assert.IsTrue(store.TryIssue(7, 1, out _));

            Assert.IsFalse(store.TryIssue(7, 1, out var code));
            Assert.IsNull(code);

            now += TimeSpan.FromSeconds(61);
            Assert.IsTrue(store.TryIssue(7, 1, out _));
        }

        [TestMethod]
        public void TryIssue_AccountZero_Refused()
        {
            Assert.IsFalse(store.TryIssue(0, 1, out var code));
            Assert.IsNull(code);
            Assert.AreEqual(0, store.Count);
        }

        [TestMethod]
        public void TryIssue_SweepsExpiredEntries()
        {
            for (uint i = 1; i <= 500; i++)
                Assert.IsTrue(store.TryIssue(i, 1, out _));

            Assert.AreEqual(500, store.Count);
            now += TimeSpan.FromSeconds(61);
            Assert.IsTrue(store.TryIssue(9999, 1, out _));
            Assert.AreEqual(1, store.Count);
        }
    }
}
