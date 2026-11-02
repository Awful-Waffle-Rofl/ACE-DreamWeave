using System.Linq;

using ACE.Server.Entity;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.TeleportFlush
{
    /// <summary>
    /// ClientObjectLedger: which landblock copy one client holds objects from (teleport_flush_scope 2).
    /// </summary>
    [TestClass]
    public class ClientObjectLedgerTests
    {
        private const uint Realm0 = 0x00000000;
        private const uint Realm1 = 0x00010000;

        private static byte[] Seq(byte value) => new byte[] { value, 0 };

        [TestMethod]
        public void Record_FilesGuidUnderItsLandblockCopy()
        {
            var ledger = new ClientObjectLedger();

            ledger.Record(0x80000001, 0x0007, Realm1, Seq(1));
            ledger.Record(0x80000002, 0x0007, Realm1, Seq(1));

            Assert.AreEqual(2, ledger.Count);
            Assert.AreEqual(1, ledger.LandblockCount);
            Assert.AreEqual(Realm1, ledger.HeldInstance(0x0007));
            Assert.IsNull(ledger.HeldInstance(0x0016));
            Assert.IsTrue(ledger.HoldsOtherCopy(0x0007, Realm0));
            Assert.IsFalse(ledger.HoldsOtherCopy(0x0007, Realm1));
        }

        [TestMethod]
        public void Record_IntoAnotherCopy_ReplacesTheWholeEntry()
        {
            var ledger = new ClientObjectLedger();

            ledger.Record(0x80000001, 0x0007, Realm1, Seq(1));
            ledger.Record(0x80000002, 0x0007, Realm1, Seq(1));

            // the client is now sent the realm-0 copy of the same landblock id
            ledger.Record(0x80000003, 0x0007, Realm0, Seq(1));

            Assert.AreEqual(Realm0, ledger.HeldInstance(0x0007));
            Assert.AreEqual(1, ledger.Count);
            Assert.IsFalse(ledger.Contains(0x80000001));
            Assert.IsTrue(ledger.Contains(0x80000003));
        }

        [TestMethod]
        public void Record_SameGuidElsewhere_MovesIt()
        {
            var ledger = new ClientObjectLedger();

            ledger.Record(0x80000001, 0x0007, Realm1, Seq(1));
            ledger.Record(0x80000001, 0x0016, Realm1, Seq(2));

            Assert.AreEqual(1, ledger.Count);
            Assert.IsNull(ledger.HeldInstance(0x0007), "the emptied entry is dropped");

            var taken = ledger.TakeOtherCopies(new ushort[] { 0x0016 }, Realm0);
            Assert.AreEqual(1, taken.Count);
            CollectionAssert.AreEqual(Seq(2), taken[0].InstanceSequence, "the latest create's sequence is kept");
        }

        [TestMethod]
        public void Remove_UnfilesTheGuid()
        {
            var ledger = new ClientObjectLedger();

            ledger.Record(0x80000001, 0x0007, Realm1, Seq(1));

            Assert.IsTrue(ledger.Remove(0x80000001));
            Assert.IsFalse(ledger.Remove(0x80000001));
            Assert.AreEqual(0, ledger.Count);
            Assert.AreEqual(0, ledger.LandblockCount);
        }

        [TestMethod]
        public void TakeOtherCopies_TakesOnlyOtherInstancesInTheGivenLandblocks()
        {
            var ledger = new ClientObjectLedger();

            ledger.Record(0x80000002, 0x0007, Realm1, Seq(2));
            ledger.Record(0x80000001, 0x0007, Realm1, Seq(1));
            ledger.Record(0x80000010, 0x0008, Realm0, Seq(3)); // same copy as the destination: kept
            ledger.Record(0x80000020, 0x0016, Realm1, Seq(4)); // outside the given landblocks: kept

            var neighbourhood = TeleportFlushPolicy.Neighbourhood(0x0007);

            Assert.AreEqual(2, ledger.CountOtherCopies(neighbourhood, Realm0), "the shadow count matches what a take returns");

            var taken = ledger.TakeOtherCopies(neighbourhood, Realm0);

            CollectionAssert.AreEqual(new uint[] { 0x80000001, 0x80000002 }, taken.Select(t => t.Guid).ToArray(), "sorted by guid within a landblock");
            CollectionAssert.AreEqual(Seq(1), taken[0].InstanceSequence);
            Assert.AreEqual(2, ledger.Count);
            Assert.IsTrue(ledger.Contains(0x80000010));
            Assert.IsTrue(ledger.Contains(0x80000020));
            Assert.AreEqual(0, ledger.TakeOtherCopies(neighbourhood, Realm0).Count, "a take empties what it took");
        }

        [TestMethod]
        public void Capacity_EvictsLeastRecentWholeLandblock_NeverTheOneBeingRecorded()
        {
            var ledger = new ClientObjectLedger(capacity: 4);

            ledger.Record(1, 0x0101, Realm0, Seq(1));
            ledger.Record(2, 0x0101, Realm0, Seq(1));
            ledger.Record(3, 0x0202, Realm0, Seq(1));
            ledger.Record(4, 0x0303, Realm0, Seq(1));

            Assert.AreEqual(0, ledger.Evictions);

            // touches 0x0202, so 0x0101 is now the oldest
            ledger.Record(5, 0x0202, Realm0, Seq(1));

            Assert.AreEqual(1, ledger.Evictions);
            Assert.IsNull(ledger.HeldInstance(0x0101), "the oldest whole landblock went");
            Assert.AreEqual(3, ledger.Count);
            Assert.IsFalse(ledger.Contains(1));
            Assert.IsFalse(ledger.Contains(2));

            // one landblock alone over capacity is never evicted from under itself
            var single = new ClientObjectLedger(capacity: 2);
            single.Record(1, 0x0101, Realm0, Seq(1));
            single.Record(2, 0x0101, Realm0, Seq(1));
            single.Record(3, 0x0101, Realm0, Seq(1));

            Assert.AreEqual(0, single.Evictions);
            Assert.AreEqual(3, single.Count);
        }
    }
}
