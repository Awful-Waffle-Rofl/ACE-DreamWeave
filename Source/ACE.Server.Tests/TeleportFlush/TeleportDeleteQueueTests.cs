using System.Linq;

using ACE.Server.Entity;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.TeleportFlush
{
    /// <summary>
    /// TeleportDeleteQueue: the paced flush's waiting deletes (teleport_flush_scope 1, teleport_flush_delete_pacing).
    /// Payloads here are the guid as a string, so every assertion is on exactly what would be sent, in order.
    /// </summary>
    [TestClass]
    public class TeleportDeleteQueueTests
    {
        private const uint Realm0 = 0x00000000;
        private const uint Realm1 = 0x00010000;

        private static TeleportDeleteQueue<string> QueueOf(params uint[] guids)
        {
            var queue = new TeleportDeleteQueue<string>();

            foreach (var guid in guids)
                queue.Defer(guid, 0x0016, Realm1, guid.ToString("X"), 1000);

            return queue;
        }

        [TestMethod]
        public void Take_ReleasesAtMostRateInFifoOrder()
        {
            var queue = QueueOf(1, 2, 3, 4, 5);

            CollectionAssert.AreEqual(new[] { "1", "2" }, queue.Take(2));
            CollectionAssert.AreEqual(new[] { "3", "4" }, queue.Take(2));
            CollectionAssert.AreEqual(new[] { "5" }, queue.Take(2));
            Assert.AreEqual(0, queue.Take(2).Count);
            Assert.AreEqual(0, queue.Count);
        }

        [TestMethod]
        public void Take_ZeroRateStillReleasesOne()
        {
            var queue = QueueOf(1, 2);

            CollectionAssert.AreEqual(new[] { "1" }, queue.Take(0));
        }

        [TestMethod]
        public void CancelOnCreate_ThatGuidIsNeverDeletedAfter()
        {
            var queue = QueueOf(1, 2, 3);

            Assert.IsTrue(queue.Cancel(2), "a create for a waiting guid cancels its delete");
            Assert.IsFalse(queue.Cancel(2));
            Assert.IsFalse(queue.Cancel(99), "nothing waiting: nothing cancelled");

            CollectionAssert.AreEqual(new[] { "1", "3" }, queue.TakeAll());
            Assert.AreEqual(1, queue.Cycle.Cancelled);
        }

        [TestMethod]
        public void ReDefer_ReplacesTheEntry_AtTheNewPosition()
        {
            var queue = QueueOf(1, 2, 3);

            queue.Defer(1, 0x0016, Realm1, "1-again", 2000);

            Assert.AreEqual(3, queue.Count);
            CollectionAssert.AreEqual(new[] { "2", "3", "1-again" }, queue.TakeAll());
        }

        [TestMethod]
        public void PacingSwitchedOffMidDrain_TakeAllReleasesEverythingLeft()
        {
            var queue = QueueOf(1, 2, 3, 4, 5);

            CollectionAssert.AreEqual(new[] { "1", "2" }, queue.Take(2));

            // the drain reads pacing off on the next tick and releases the rest at once
            CollectionAssert.AreEqual(new[] { "3", "4", "5" }, queue.TakeAll());
            Assert.AreEqual(0, queue.Count);
            Assert.AreEqual(5, queue.Cycle.Sent);
        }

        [TestMethod]
        public void Logout_DiscardDropsEverythingUnsent()
        {
            var queue = QueueOf(1, 2, 3);

            Assert.AreEqual(3, queue.Discard());
            Assert.AreEqual(0, queue.Count);
            Assert.AreEqual(0, queue.TakeAll().Count, "nothing discarded is ever released");
        }

        [TestMethod]
        public void TakeColliding_PullsOnlyOtherCopiesInTheDestination3x3_KeepingFifo()
        {
            var queue = new TeleportDeleteQueue<string>();

            queue.Defer(1, 0x0007, Realm1, "a", 1000);   // other copy of the destination: collides
            queue.Defer(2, 0x0016, Realm1, "b", 1000);   // elsewhere: stays
            queue.Defer(3, 0x0108, Realm1, "c", 1000);   // neighbour, other copy: collides
            queue.Defer(4, 0x0007, Realm0, "d", 1000);   // the destination's own copy: stays

            var pulled = queue.TakeColliding(TeleportFlushPolicy.Neighbourhood(0x0007), Realm0);

            CollectionAssert.AreEqual(new[] { "a", "c" }, pulled);
            CollectionAssert.AreEqual(new[] { "b", "d" }, queue.TakeAll());
        }

        [TestMethod]
        public void Cycle_CountsEverything_AndStaysOpenUntilEnded()
        {
            var queue = new TeleportDeleteQueue<string>();

            Assert.IsFalse(queue.CycleOpen);

            queue.NoteTeleport(5000);
            queue.Defer(1, 0x0016, Realm1, "1", 6000);
            queue.Defer(2, 0x0016, Realm1, "2", 6000);
            queue.NoteTeleport(7000);
            queue.Defer(3, 0x0016, Realm1, "3", 7000);

            queue.Cancel(2);
            queue.Take(10);

            Assert.AreEqual(0, queue.Count);
            Assert.IsTrue(queue.CycleOpen, "an emptied queue still has a cycle to end and log");

            var cycle = queue.EndCycle();

            Assert.AreEqual(2, cycle.Sent);
            Assert.AreEqual(1, cycle.Cancelled);
            Assert.AreEqual(3, cycle.Deferred);
            Assert.AreEqual(2, cycle.Teleports);
            Assert.AreEqual(5000, cycle.StartTicks, "the cycle starts at its first teleport");
            Assert.IsFalse(queue.CycleOpen);
            Assert.AreEqual(0, queue.Cycle.Sent);
        }

        [TestMethod]
        public void AllCancelled_CycleStillOpenWithNothingWaiting()
        {
            var queue = QueueOf(1);

            queue.Cancel(1);

            Assert.AreEqual(0, queue.Count);
            Assert.IsTrue(queue.CycleOpen);
            Assert.AreEqual(0, queue.Take(10).Count);
        }

        [TestMethod]
        public void IsPending_TracksLiveEntriesOnly()
        {
            var queue = QueueOf(1, 2);

            Assert.IsTrue(queue.IsPending(1));
            queue.Cancel(1);
            Assert.IsFalse(queue.IsPending(1));
            Assert.IsTrue(queue.IsPending(2));
            Assert.IsFalse(queue.TakeAll().Contains("1"));
        }
    }
}
