using ACE.Entity.Enum;
using ACE.Server.WorldObjects.Managers;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The ledger ConfirmationManager uses to drop, once and quietly, the client's automatic reply to a dialog
    /// TryCancel closed - and nothing else.
    /// </summary>
    [TestClass]
    public class CancelledConfirmationIdsTests
    {
        [TestMethod]
        public void RememberedReply_IsConsumedExactlyOnce()
        {
            var ids = new CancelledConfirmationIds();
            ids.Remember(ConfirmationType.Yes_No, 41);

            Assert.IsTrue(ids.TryConsume(ConfirmationType.Yes_No, 41));
            Assert.IsFalse(ids.TryConsume(ConfirmationType.Yes_No, 41), "a second reply with the same id is not swallowed");
        }

        [TestMethod]
        public void OtherReplies_AreNeverConsumed()
        {
            var ids = new CancelledConfirmationIds();
            ids.Remember(ConfirmationType.Yes_No, 41);

            Assert.IsFalse(ids.TryConsume(ConfirmationType.Yes_No, 42), "a newer dialog of the same type");
            Assert.IsFalse(ids.TryConsume(ConfirmationType.Fellowship, 41), "same id, another type");
            Assert.IsFalse(new CancelledConfirmationIds().TryConsume(ConfirmationType.Yes_No, 41), "nothing cancelled");

            Assert.IsTrue(ids.TryConsume(ConfirmationType.Yes_No, 41), "the real one is still there");
        }

        [TestMethod]
        public void Ledger_IsBounded_OldestForgottenFirst()
        {
            var ids = new CancelledConfirmationIds(capacity: 3);
            for (uint i = 1; i <= 5; i++)
                ids.Remember(ConfirmationType.Yes_No, i);

            Assert.AreEqual(3, ids.Count);
            Assert.IsFalse(ids.TryConsume(ConfirmationType.Yes_No, 1));
            Assert.IsFalse(ids.TryConsume(ConfirmationType.Yes_No, 2));
            Assert.IsTrue(ids.TryConsume(ConfirmationType.Yes_No, 3));
            Assert.IsTrue(ids.TryConsume(ConfirmationType.Yes_No, 5));
            Assert.AreEqual(1, ids.Count);
        }

        [TestMethod]
        public void DefaultCapacity_IsSmall()
        {
            var ids = new CancelledConfirmationIds();
            for (uint i = 0; i < 100; i++)
                ids.Remember(ConfirmationType.Yes_No, i);

            Assert.AreEqual(CancelledConfirmationIds.DefaultCapacity, ids.Count);
        }
    }
}
