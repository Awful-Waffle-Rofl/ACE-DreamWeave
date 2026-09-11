using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Managers.Market;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Covers FakeMarketItemStore.TryTakeMatching's own accounting, independent of the manager. The
    /// fake is the ONLY thing that will ever exercise the order-fill path in this test project - there
    /// is no live vault here - so a fake more permissive than the real store (VaultMarketItemStore,
    /// Task 8) would let a broken feature pass a green suite.
    /// </summary>
    [TestClass]
    public class MarketFakeMatchingTakeTests
    {
        private const uint SellerAccount = 9401;
        private static readonly MarketActor Seller = new MarketActor(SellerAccount, 0x50000401, "Marketseller");

        /// <summary>
        /// A group of [fullBag, fullBag, nonFullBag] (representative first, matching the SeedGroupOf
        /// contract) asked for ONE full bag. A slice-from-the-back-of-the-group-unchecked bug takes the
        /// non-full bag at the tail and reports success; the real store's fix takes it too but never
        /// counts it as progress, so the whole take must be refused and the seller left whole.
        /// </summary>
        [TestMethod]
        public void TryTakeMatching_NonMatchingMemberAtBack_RefusesAndReturnsSellerWhole()
        {
            var store = new FakeMarketItemStore();

            var full1 = FakeVaultWorld.MakeSalvageBag(20980, 100, 10, 100, 1000);
            var full2 = FakeVaultWorld.MakeSalvageBag(20980, 100, 10, 100, 1000);
            var notFull = FakeVaultWorld.MakeSalvageBag(20980, 40, 10, 40, 400);

            store.SeedGroupOf(SellerAccount, new System.Collections.Generic.List<ACE.Server.WorldObjects.WorldObject> { full1, full2, notFull });

            bool IsFullBag(ACE.Server.WorldObjects.WorldObject o) => o.Structure == 100;

            var ok = store.TryTakeMatching(SellerAccount, IsFullBag, 1, Seller, out var taken, out _, out var error);

            Assert.IsFalse(ok, "a slice containing a non-matching member must never report success");
            Assert.AreEqual(MarketError.NoMatchingItems, error);
            Assert.AreEqual(0, taken.Count, "a refused take must hand back nothing");

            // Seller left whole: all three original objects are still held by the account somewhere -
            // whether the unwind put them back in the group or (since the group emptied) as loose
            // items is an implementation detail of TryReturnToSeller, not what this test is checking.
            var held = store.Items.TryGetValue(SellerAccount, out var loose) ? loose.ToList() : new System.Collections.Generic.List<ACE.Server.WorldObjects.WorldObject>();

            if (store.Groups.TryGetValue(SellerAccount, out var groups))
                foreach (var g in groups)
                    held.AddRange(g);

            Assert.AreEqual(3, held.Count, "all three members must come back - nothing destroyed, nothing duplicated");
            Assert.IsTrue(held.Any(m => ReferenceEquals(m, full1)));
            Assert.IsTrue(held.Any(m => ReferenceEquals(m, full2)));
            Assert.IsTrue(held.Any(m => ReferenceEquals(m, notFull)));
            Assert.AreEqual(2, held.Count(IsFullBag), "exactly the two originally-full bags remain full");
        }

        /// <summary>
        /// A throw from the SECOND withdraw, with the first one's bag already out of the vault. This is
        /// the only market take that withdraws more than once, so it is the only one that can strand a
        /// partial take - and if the throw escapes, nothing above catches it usefully: RunSerialized
        /// rethrows by design, SafeSerialized swallows it into a bare false, and the manager's "the
        /// region never ran" branch does not unwind. The bag is then in no vault at all, referenced by
        /// nothing, named in no log.
        ///
        /// So the unwind has to happen INSIDE the take, while the region is still open. The error is
        /// VaultUnavailable rather than NoMatchingItems: after a throw the seller's holdings are
        /// unknown, and telling them they hold nothing would be a guess.
        /// </summary>
        [TestMethod]
        public void TryTakeMatching_WhenTheSecondWithdrawThrows_TakesNothing_AndLeavesTheSellerWhole()
        {
            var store = new FakeMarketItemStore();

            var full1 = FakeVaultWorld.MakeSalvageBag(20980, 100, 10, 100, 1000);
            var full2 = FakeVaultWorld.MakeSalvageBag(20980, 100, 10, 100, 1000);

            // Two separate rows, so filling a count of 2 needs two withdraws.
            store.SeedGroupOf(SellerAccount, new System.Collections.Generic.List<ACE.Server.WorldObjects.WorldObject> { full1 });
            store.SeedGroupOf(SellerAccount, new System.Collections.Generic.List<ACE.Server.WorldObjects.WorldObject> { full2 });

            bool IsFullBag(ACE.Server.WorldObjects.WorldObject o) => o.Structure == 100;

            Assert.AreEqual(2, store.CountMatching(SellerAccount, IsFullBag), "both bags start in the seller's vault");

            store.ThrowOnTakeMatchingWithdraw = 2;

            var ok = store.TryTakeMatching(SellerAccount, IsFullBag, 2, Seller, out var taken, out _, out var error);

            Assert.IsFalse(ok, "a take that threw part way through must never report success");
            Assert.AreEqual(MarketError.VaultUnavailable, error);
            Assert.AreEqual(0, taken.Count, "a refused take must hand back nothing for the caller to strand");
            Assert.AreEqual(2, store.CountMatching(SellerAccount, IsFullBag),
                "the bag taken by the first withdraw must be back in the seller's vault");
        }

        /// <summary>
        /// The CORRELATED failure: the withdraw throws AND the unwind that answers it throws too. That
        /// is the most likely shape this path will ever see, not the least - the trigger is a shard
        /// write error, and the unwind is a shard write to the same store, in the same region,
        /// microseconds later. TryReturnToSeller calls AccountVaultStore.TryReturnWithdrawn with no
        /// catch of its own, and that throws; Player_Facets.TryReturnWithdrawnToVault
        /// (Player_Facets.cs:909) wraps the identical pair of calls for exactly this reason.
        ///
        /// An escaping throw here re-creates, inside the unwind, precisely what the unwind exists to
        /// prevent: it propagates through RunSerialized, SafeSerialized swallows it into a bare false,
        /// and the manager takes its "the seller's region never ran" branch, which does not unwind.
        /// Worse, `taken` is an out parameter written through a byref, so the caller is left holding
        /// the partially withdrawn items rather than an empty list.
        ///
        /// The bags really are lost in this case - nothing can put them back once the vault refuses -
        /// so what is asserted is the CONTAINMENT: a return rather than a throw, an honest error, and
        /// an empty `taken` so no caller can act on items it does not have.
        /// </summary>
        [TestMethod]
        public void TryTakeMatching_WhenTheUnwindItselfThrows_StillReturnsFalse_AndHandsBackNothing()
        {
            var store = new FakeMarketItemStore();

            var full1 = FakeVaultWorld.MakeSalvageBag(20980, 100, 10, 100, 1000);
            var full2 = FakeVaultWorld.MakeSalvageBag(20980, 100, 10, 100, 1000);

            store.SeedGroupOf(SellerAccount, new System.Collections.Generic.List<ACE.Server.WorldObjects.WorldObject> { full1 });
            store.SeedGroupOf(SellerAccount, new System.Collections.Generic.List<ACE.Server.WorldObjects.WorldObject> { full2 });

            bool IsFullBag(ACE.Server.WorldObjects.WorldObject o) => o.Structure == 100;

            store.ThrowOnTakeMatchingWithdraw = 2;
            store.ThrowOnReturnToSeller = true;

            var ok = store.TryTakeMatching(SellerAccount, IsFullBag, 2, Seller, out var taken, out _, out var error);

            Assert.IsFalse(ok, "the take must report failure rather than throwing it at the caller");
            Assert.AreEqual(MarketError.VaultUnavailable, error);
            Assert.AreEqual(0, taken.Count,
                "taken is written through a byref, so leaving items in it hands the caller custody of bags it cannot see");
            Assert.AreEqual(1, store.ReturnCalls, "the unwind was attempted, which is what makes the loss reportable");
        }
    }
}
