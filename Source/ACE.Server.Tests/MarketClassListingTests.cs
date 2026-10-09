using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database;
using ACE.Database.Models.Shard;
using ACE.Entity.Enum;
using ACE.Server.Entity.AccountVault;
using ACE.Server.Managers;
using ACE.Server.Managers.Market;
using ACE.Server.WorldObjects;

using ShardAccountVault = ACE.Database.Models.Shard.AccountVault;
using ShardMarketListing = ACE.Database.Models.Shard.MarketListing;
using MarketListing = ACE.Server.Managers.Market.MarketListing;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Listing and barreling a counted CLASS line, driven end to end through the REAL custody code:
    /// MarketManager over VaultMarketItemStore over two real AccountVaultStores (seller and buyer),
    /// each on its own FakeVaultBackend and FakeVaultWorld, with the market's two vault hooks wired
    /// exactly as production wires them. Nothing here needs a database.
    ///
    /// THE LINES. Every seller holds up to two display lines of the SAME wcid, and optionally a stack
    /// LEDGER row of that wcid too, because that is the shape that makes the three null-guid
    /// identities collide:
    ///   line A - Structure 100, two members (workmanship 80/num 10 and 88/num 11), two bags each;
    ///   line B - Structure 50, one member, two bags.
    /// A line is named by its display id (VaultEntry.ClassDisplayId), which is what a listing and a
    /// barrel carry; a member is one account_vault_class row.
    ///
    /// Values are deliberately NOT divisible by the count, so every conservation assertion here
    /// also covers the round-half-up pooled share.
    /// </summary>
    [TestClass]
    public class MarketClassListingTests
    {
        private const uint SellerAccount = 8701;
        private const uint SellerCharacter = 0x50000871;
        private const uint BuyerAccount = 8702;
        private const uint BuyerCharacter = 0x50000872;

        private const uint BagWcid = 21113;

        private const long Price = 10;
        private const long BuyerStart = 1000;

        private static readonly MarketActor Seller = new MarketActor(SellerAccount, SellerCharacter, "Classseller");
        private static readonly MarketActor Buyer = new MarketActor(BuyerAccount, BuyerCharacter, "Classbuyer");
        private static readonly VaultActor SellerVaultActor = new VaultActor(SellerAccount, SellerCharacter, "Classseller");

        private FakeVaultBackend sellerBackend;
        private FakeVaultBackend buyerBackend;
        private FakeVaultWorld sellerWorld;
        private FakeVaultWorld buyerWorld;

        private AccountVaultStore sellerStore;
        private AccountVaultStore buyerStore;
        private VaultMarketItemStore itemStore;

        private FakeMarketRepository repo;
        private FakeMarketWallet wallet;

        private uint nextVaultRow = 1;

        private string lineA;
        private string lineB;

        private Action<uint, uint?, uint, int, string> savedPreWithdrawHook;
        private Func<uint, uint?, uint, string, bool> savedIsListedHook;
        private Func<uint, ACE.Entity.Models.Weenie> savedWeenieLookup;

        [TestInitialize]
        public void Setup()
        {
            // Seeds market_allow_class_listings and account_vault_class_storage among the rest.
            MarketManagerTests.SeedMarketTunables();

            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_entry_cap", 100000));
            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_landblock",
                DefaultPropertyManager.DefaultLongProperties["account_vault_landblock"].Item));

            savedPreWithdrawHook = AccountVaultStore.PreWithdrawHook;
            savedIsListedHook = AccountVaultStore.IsListedHook;
            AccountVaultStore.PreWithdrawHook = null;
            AccountVaultStore.IsListedHook = null;

            // The default lookup reads DatabaseManager.World, which no unit test has.
            savedWeenieLookup = MarketManager.WeenieLookup;
            MarketManager.WeenieLookup = _ => null;

            sellerBackend = new FakeVaultBackend();
            buyerBackend = new FakeVaultBackend();

            // Both sides classify: the seller's bags live as class rows, and what the buyer receives
            // lands in the buyer's own class rows.
            sellerWorld = new FakeVaultWorld { PristineResult = false, ClassifyResult = true };
            buyerWorld = new FakeVaultWorld { PristineResult = false, ClassifyResult = true };

            repo = new FakeMarketRepository();
            wallet = new FakeMarketWallet();

            wallet.Seed(BuyerCharacter, BuyerStart);
            wallet.Seed(SellerCharacter, 0);

            SeedContainer(sellerBackend, sellerWorld, SellerAccount);
            SeedContainer(buyerBackend, buyerWorld, BuyerAccount);

            sellerStore = new AccountVaultStore(SellerAccount, sellerBackend, sellerWorld);
            buyerStore = new AccountVaultStore(BuyerAccount, buyerBackend, buyerWorld);

            Assert.IsTrue(sellerStore.IsLoaded, "the seller's store must load");
            Assert.IsTrue(buyerStore.IsLoaded, "the buyer's store must load");

            itemStore = new VaultMarketItemStore(account =>
                account == SellerAccount ? sellerStore : account == BuyerAccount ? buyerStore : null);
        }

        [TestCleanup]
        public void Teardown()
        {
            MarketManager.Shutdown();

            AccountVaultStore.PreWithdrawHook = savedPreWithdrawHook;
            AccountVaultStore.IsListedHook = savedIsListedHook;
            MarketManager.WeenieLookup = savedWeenieLookup;

            PropertyManager.ModifyLong("account_vault_entry_cap", DefaultPropertyManager.DefaultLongProperties["account_vault_entry_cap"].Item);
            PropertyManager.ModifyBool("account_vault_class_storage", DefaultPropertyManager.DefaultBooleanProperties["account_vault_class_storage"].Item);
            PropertyManager.ModifyBool("market_allow_class_listings", DefaultPropertyManager.DefaultBooleanProperties["market_allow_class_listings"].Item);
        }

        // ---- fixture ----

        private void SeedContainer(FakeVaultBackend backend, FakeVaultWorld world, uint accountId)
        {
            var container = FakeVaultWorld.MakeContainer(AccountVaultStore.VaultItemCapacity);

            world.Containers[container.Guid.Full] = container;

            backend.Vaults.Add(new ShardAccountVault
            {
                Id = 9800 + nextVaultRow,
                AccountId = accountId,
                ContainerGuid = container.Guid.Full,
                CreatedAt = new DateTime(2026, 1, 1).AddMinutes(nextVaultRow),
                Kind = AccountVaultStore.VaultContainerKind,
            });

            nextVaultRow++;
        }

        private static WorldObject Bag(int structure, int workmanship, int numItems, int value)
            => FakeVaultWorld.MakeSalvageBag(BagWcid, structure, workmanship, numItems, value, $"Salvage ({structure})");

        private static void Deposit(AccountVaultStore store, VaultActor actor, WorldObject item)
        {
            var ok = false;
            string reason = null;

            store.Enqueue(() => ok = store.TryDeposit(item, actor, out reason));

            Assert.IsTrue(ok, $"could not seed a deposit: {reason}");
        }

        private static bool Withdraw(AccountVaultStore store, VaultActor actor, VaultEntry entry, int amount, out List<WorldObject> withdrawn)
        {
            var ok = false;
            List<WorldObject> got = null;

            store.Enqueue(() => ok = store.TryWithdraw(entry, amount, actor, out got, out _, GroupTakeOrder.Front));

            withdrawn = got ?? new List<WorldObject>();
            return ok;
        }

        /// <summary>The display id of the one class line not already in <paramref name="known"/>.</summary>
        private string NewLineId(params string[] known)
        {
            var ids = sellerStore.GetEntries(0, -1).Where(e => e.Kind == VaultEntryKind.Class)
                .Select(e => e.ClassDisplayId).Where(id => !known.Contains(id)).ToList();

            Assert.AreEqual(1, ids.Count, $"expected exactly one new class line, found {ids.Count}");
            return ids[0];
        }

        /// <summary>Line A: two members of two bags each (values 100+111 and 150+157).</summary>
        private void SeedLineA()
        {
            Deposit(sellerStore, SellerVaultActor, Bag(100, 80, 10, 100));
            Deposit(sellerStore, SellerVaultActor, Bag(100, 80, 10, 111));
            Deposit(sellerStore, SellerVaultActor, Bag(100, 88, 11, 150));
            Deposit(sellerStore, SellerVaultActor, Bag(100, 88, 11, 157));

            lineA = NewLineId();

            Assert.AreEqual(2, Line(lineA).ClassMembers.Count, "control: line A must span TWO class rows, or this is not a multi-member line");
            Assert.AreEqual(4, Line(lineA).Count);
        }

        /// <summary>Line B: one member of two bags (values 60+61), same wcid as line A.</summary>
        private void SeedLineB()
        {
            Deposit(sellerStore, SellerVaultActor, Bag(50, 80, 10, 60));
            Deposit(sellerStore, SellerVaultActor, Bag(50, 80, 10, 61));

            lineB = lineA != null ? NewLineId(lineA) : NewLineId();

            Assert.AreEqual(2, Line(lineB).Count);
            Assert.AreNotEqual(lineA, lineB);
        }

        /// <summary>
        /// A stack LEDGER row of the lines' own wcid. The store reads its ledger once, at load, so the
        /// seller's store is rebuilt over the same backend (the resolver reads the field, so the market
        /// sees the new one). Call it before StartMarket.
        /// </summary>
        private void SeedSellerLedger(long count)
        {
            sellerBackend.Stacks.Add(new AccountVaultStack { Id = 7101, AccountId = SellerAccount, Wcid = BagWcid, Count = count });

            sellerStore = new AccountVaultStore(SellerAccount, sellerBackend, sellerWorld);
            Assert.IsTrue(sellerStore.IsLoaded, "the rebuilt seller store must load");

            Assert.IsTrue(sellerStore.GetEntries(0, -1).Any(e => e.Kind == VaultEntryKind.Ledger && e.Wcid == BagWcid && e.Count == count),
                "control: the ledger row of the lines' wcid must be visible, or this is not the collision under test");
        }

        private VaultEntry Line(string displayId)
            => sellerStore.GetEntries(0, -1).SingleOrDefault(e => e.Kind == VaultEntryKind.Class && e.ClassDisplayId == displayId);

        private void StartMarket()
        {
            MarketManager.Initialize(itemStore, wallet, repo);

            // Production wiring (MarketManager.Initialize()), which the three-seam overload leaves out.
            AccountVaultStore.PreWithdrawHook = MarketManager.OnVaultWithdraw;
            AccountVaultStore.IsListedHook = MarketManager.IsListed;
        }

        private static Dictionary<string, (long count, long value)> ClassRows(FakeVaultBackend backend, uint accountId)
            => backend.Classes.Where(c => c.AccountId == accountId && c.Count > 0)
                .ToDictionary(c => c.ClassKey, c => (c.Count, c.TotalValue), StringComparer.Ordinal);

        private static long TotalCount(FakeVaultBackend backend, uint accountId) => backend.Classes.Where(c => c.AccountId == accountId).Sum(c => c.Count);

        private static long TotalValue(FakeVaultBackend backend, uint accountId) => backend.Classes.Where(c => c.AccountId == accountId).Sum(c => c.TotalValue);

        private long SellerLedger() => sellerBackend.Stacks.Where(s => s.AccountId == SellerAccount && s.Wcid == BagWcid).Sum(s => s.Count);

        private MarketListing ListClass(string displayId, int count)
        {
            var result = MarketManager.List(Seller, null, 0, count, Price, MarketChannel.Web, displayId);

            Assert.IsTrue(result.Ok, $"class listing of {displayId} x{count} refused: {result.Error}");
            return result.Value;
        }

        private bool Barrel(VaultEntry entry, int amount, out string failReason)
        {
            var ok = false;
            string reason = null;

            sellerStore.Enqueue(() => ok = sellerStore.TryBarrel(entry, amount, SellerVaultActor, out reason));

            failReason = reason;
            return ok;
        }

        // ---- T1: a two-member sale conserves value ----

        [TestMethod]
        public void T1_Buy_TwoMemberLine_SellerLossEqualsBuyerGainEqualsTheDepositLogs()
        {
            SeedLineA();
            StartMarket();

            var listing = ListClass(lineA, 3);

            Assert.IsTrue(listing.IsClass);
            Assert.IsFalse(listing.IsLedger);
            Assert.AreEqual(BagWcid, listing.Wcid, "the listing takes the line's wcid, not the 0 the caller sent");

            var sellerValueBefore = TotalValue(sellerBackend, SellerAccount);

            var result = MarketManager.Buy(Buyer, listing.Id, 3, Price, MarketChannel.Web);

            var sellerDelta = sellerValueBefore - TotalValue(sellerBackend, SellerAccount);
            var buyerGain = TotalValue(buyerBackend, BuyerAccount);
            var depositLogValue = buyerBackend.Logs.Where(l => l.Action == (int)AccountVaultAction.Deposit).Sum(l => l.Value ?? 0);

            Assert.AreEqual(1, TotalCount(sellerBackend, SellerAccount), "three of four items left the line");
            Assert.AreEqual(3, TotalCount(buyerBackend, BuyerAccount), "and all three reached the buyer");
            Assert.IsTrue(sellerDelta > 0, "control: value really left the seller");
            Assert.AreEqual(sellerDelta, buyerGain, "CONSERVATION: the value the seller lost is exactly what the buyer gained");
            Assert.AreEqual(buyerGain, depositLogValue, "and exactly what the buyer's deposit rows record");
            Assert.AreEqual(0, SellerLedger(), "nothing ever touches the stack ledger");

            Assert.IsTrue(result.Ok, $"got {result.Error}");
            Assert.AreEqual(3, result.Value.Count);
            Assert.AreEqual(BuyerStart - 3 * Price, wallet.GetBalanceMmd(BuyerCharacter));
            Assert.AreEqual(3 * Price, wallet.GetBalanceMmd(SellerCharacter));
            Assert.AreEqual(MarketListingStatus.Sold, MarketManager.GetListing(listing.Id).Status);
        }

        // ---- T2: a failed delivery puts the seller's rows back exactly ----

        [TestMethod]
        public void T2_Buy_BuyerDepositRefused_SellerClassRowsAreRestoredExactly_AndTheLedgerIsUntouched()
        {
            SeedLineA();
            StartMarket();

            var listing = ListClass(lineA, 3);

            var before = ClassRows(sellerBackend, SellerAccount);

            buyerBackend.ClassAdjustOverride = (key, delta) => AccountVaultStackAdjustResult.Refused;
            buyerBackend.FailAddVault = true;

            var result = MarketManager.Buy(Buyer, listing.Id, 3, Price, MarketChannel.Web);

            var after = ClassRows(sellerBackend, SellerAccount);

            Assert.AreEqual(0, TotalCount(buyerBackend, BuyerAccount), "control: nothing was delivered");
            Assert.AreEqual(before.Count, after.Count, "every member row is back");

            foreach (var kvp in before)
            {
                Assert.IsTrue(after.ContainsKey(kvp.Key), $"member {kvp.Key} must be back on its own class row");
                Assert.AreEqual(kvp.Value, after[kvp.Key], $"member {kvp.Key} must carry exactly its old count AND total value");
            }

            Assert.AreEqual(0, SellerLedger(), "the undelivered items must go back to the CLASS rows, never to the stack ledger");
            Assert.IsFalse(result.Ok, "nothing delivered is no sale");
            Assert.AreEqual(BuyerStart, wallet.GetBalanceMmd(BuyerCharacter));
            Assert.AreEqual(MarketListingStatus.Active, MarketManager.GetListing(listing.Id).Status);
        }

        // ---- T3: a partial delivery sells what arrived ----

        [TestMethod]
        public void T3_Buy_RefusedOnItem2Of3_SellsOne_ReturnsTwo_AndConservesCountAndValue()
        {
            SeedLineA();
            StartMarket();

            var listing = ListClass(lineA, 3);

            var countBefore = TotalCount(sellerBackend, SellerAccount);
            var valueBefore = TotalValue(sellerBackend, SellerAccount);

            var calls = 0;
            buyerBackend.ClassAdjustOverride = (key, delta) => ++calls == 2 ? AccountVaultStackAdjustResult.Refused : (AccountVaultStackAdjustResult?)null;
            buyerBackend.FailAddVault = true;

            var result = MarketManager.Buy(Buyer, listing.Id, 3, Price, MarketChannel.Web);

            Assert.AreEqual(1, TotalCount(buyerBackend, BuyerAccount), "one item reached the buyer");
            Assert.AreEqual(countBefore, TotalCount(sellerBackend, SellerAccount) + TotalCount(buyerBackend, BuyerAccount),
                "CONSERVATION of items: never more than the seller started with");
            Assert.AreEqual(valueBefore, TotalValue(sellerBackend, SellerAccount) + TotalValue(buyerBackend, BuyerAccount),
                "CONSERVATION of pooled value");
            Assert.AreEqual(0, SellerLedger());

            Assert.IsTrue(result.Ok, $"a delivery that landed one item is a partial SALE, got {result.Error}");
            Assert.AreEqual(1, result.Value.Count);
            Assert.AreEqual(BuyerStart - Price, wallet.GetBalanceMmd(BuyerCharacter), "the buyer pays for what arrived");
            Assert.AreEqual(Price, wallet.GetBalanceMmd(SellerCharacter));

            var after = MarketManager.GetListing(listing.Id);
            Assert.AreEqual(MarketListingStatus.Active, after.Status);
            Assert.AreEqual(2, after.Count);
        }

        // ---- T4: ledger W, class line A and class line B of W coexist ----

        [TestMethod]
        public void T4_LedgerListingAndClassListingOfTheSameWcidCoexist_AndBuyingTheClassLineMovesOnlyThatLine()
        {
            SeedLineA();
            SeedLineB();
            SeedSellerLedger(5);
            StartMarket();

            // The CLASS listing first: a ledger identity that matched it would then refuse the ledger
            // listing as already listed, which is the collision under test.
            var classListing = ListClass(lineB, 2);

            var ledgerResult = MarketManager.List(Seller, null, BagWcid, 3, Price, MarketChannel.Web);
            Assert.IsTrue(ledgerResult.Ok, $"a ledger listing of the same wcid must coexist with the class listing, got {ledgerResult.Error}");
            Assert.IsTrue(ledgerResult.Value.IsLedger);

            var aBefore = Line(lineA).ClassMembers.ToDictionary(m => m.ClassKey, m => (m.Count, m.TotalValue));

            var result = MarketManager.Buy(Buyer, classListing.Id, 2, Price, MarketChannel.Web);
            Assert.IsTrue(result.Ok, $"got {result.Error}");

            Assert.IsNull(Line(lineB), "line B was bought out entirely");
            Assert.AreEqual(5, SellerLedger(), "the ledger of the same wcid is untouched");

            var aAfter = Line(lineA).ClassMembers.ToDictionary(m => m.ClassKey, m => (m.Count, m.TotalValue));
            CollectionAssert.AreEquivalent(aBefore.ToList(), aAfter.ToList(), "line A is untouched");

            Assert.AreEqual(2, TotalCount(buyerBackend, BuyerAccount));
            Assert.AreEqual(121, TotalValue(buyerBackend, BuyerAccount), "the buyer got line B's bags (60+61), not line A's");

            var ledgerListing = MarketManager.GetListing(ledgerResult.Value.Id);
            Assert.AreEqual(MarketListingStatus.Active, ledgerListing.Status, "the ledger listing survives a sale of the class line");
            Assert.AreEqual(3, ledgerListing.Count);
        }

        // ---- T5: the pre-withdraw hook is per line ----

        [TestMethod]
        public void T5_WithdrawFromLineA_LeavesLineBListed_WithdrawBelowBsListedCount_DelistsB()
        {
            SeedLineA();
            SeedLineB();
            StartMarket();

            var listingA = ListClass(lineA, 1);
            var listingB = ListClass(lineB, 2);

            Assert.IsTrue(Withdraw(sellerStore, SellerVaultActor, Line(lineA), 1, out _), "control: the withdraw from line A must happen");

            Assert.AreEqual(MarketListingStatus.Active, MarketManager.GetListing(listingB.Id).Status, "a withdraw from line A must not touch line B's listing");
            Assert.AreEqual(MarketListingStatus.Active, MarketManager.GetListing(listingA.Id).Status,
                "line A still holds 3, at least its listed 1, so the ledger rule keeps it listed");

            Assert.IsTrue(Withdraw(sellerStore, SellerVaultActor, Line(lineB), 1, out _), "control: the withdraw from line B must happen");

            Assert.AreEqual(MarketListingStatus.Delisted, MarketManager.GetListing(listingB.Id).Status,
                "line B now holds 1, below its listed 2, so its listing goes");
            Assert.AreEqual(MarketListingStatus.Active, MarketManager.GetListing(listingA.Id).Status, "and line A's does not");
        }

        // ---- T6: invalidation sees a class listing's real backing ----

        [TestMethod]
        public void T6_FindInvalidationCandidates_ABackedClassListingIsNotACandidate_AnUnbackedOneIs()
        {
            SeedLineA();
            SeedLineB();
            StartMarket();

            var listingA = ListClass(lineA, 4);
            var listingB = ListClass(lineB, 2);

            Assert.AreEqual(0, MarketManager.FindInvalidationCandidates(SellerAccount).Listings.Count,
                "both lines hold their listed counts, so neither is a candidate");

            // Drain line B with the hook OFF, so the listing is left stale exactly as a missed hook would.
            AccountVaultStore.PreWithdrawHook = null;
            Assert.IsTrue(Withdraw(sellerStore, SellerVaultActor, Line(lineB), 2, out _));
            AccountVaultStore.PreWithdrawHook = MarketManager.OnVaultWithdraw;

            var candidates = MarketManager.FindInvalidationCandidates(SellerAccount).Listings;

            Assert.AreEqual(1, candidates.Count, "control: an unbacked class listing IS a candidate");
            Assert.AreEqual(listingB.Id, candidates[0].Id);
            Assert.AreNotEqual(listingA.Id, candidates[0].Id);
        }

        // ---- T7: a class barrel debits exactly the planned shares ----

        [TestMethod]
        public void T7_BarrelThreeOfLineA_DebitsThePlannedShares_WithOneBarrelRowPerMember()
        {
            SeedLineA();
            StartMarket();

            var members = Line(lineA).ClassMembers.ToList();
            var first = members[0];
            var second = members[1];

            var expectedSecondShare = AccountVaultStore.PooledShare(second.TotalValue, second.Count);
            var valueBefore = TotalValue(sellerBackend, SellerAccount);

            var result = MarketManager.Barrel(Seller, null, 0, 3, lineA);
            Assert.IsTrue(result.Ok, $"got {result.Error}");

            var rows = sellerBackend.Barrels.Where(b => b.AccountId == SellerAccount).OrderBy(b => b.Id).ToList();

            Assert.AreEqual(2, rows.Count, "one barrel row per member touched");

            Assert.AreEqual(first.ClassKey, rows[0].ClassKey);
            Assert.AreEqual(first.CanonicalForm, rows[0].CanonicalForm);
            Assert.AreEqual(first.Count, rows[0].Count, "the front member is drained whole");
            Assert.AreEqual(first.TotalValue, rows[0].Value, "carrying its whole pool");

            Assert.AreEqual(second.ClassKey, rows[1].ClassKey);
            Assert.AreEqual(1, rows[1].Count);
            Assert.AreEqual(expectedSecondShare, rows[1].Value, "the second member gives up exactly one pooled share");

            foreach (var row in rows)
            {
                Assert.IsNull(row.ItemGuid, "a class barreling names no biota");
                Assert.AreEqual(BagWcid, row.Wcid);
            }

            Assert.AreEqual(valueBefore - rows.Sum(r => r.Value ?? 0), TotalValue(sellerBackend, SellerAccount),
                "the seller lost exactly what the barrel rows record");
            Assert.AreEqual(1, TotalCount(sellerBackend, SellerAccount));
            Assert.IsFalse(ClassRows(sellerBackend, SellerAccount).ContainsKey(first.ClassKey), "the drained member is reaped");
            Assert.AreEqual(2, sellerBackend.Logs.Count(l => l.Action == (int)AccountVaultAction.Barrel));
            Assert.AreEqual(0, SellerLedger());
        }

        // ---- T8: restore rebuilds a reaped member ----

        [TestMethod]
        public void T8_RestoreAfterTheMemberWasReaped_RebuildsItFromTheCanonicalForm_AndNeverTouchesTheLedger()
        {
            SeedLineA();
            StartMarket();

            var first = Line(lineA).ClassMembers[0];

            Assert.IsTrue(MarketManager.Barrel(Seller, null, 0, (int)first.Count, lineA).Ok);
            Assert.IsFalse(ClassRows(sellerBackend, SellerAccount).ContainsKey(first.ClassKey), "control: the member row was reaped");

            var row = sellerBackend.Barrels.Single(b => b.AccountId == SellerAccount);

            var ok = false;
            string reason = null;
            sellerStore.Enqueue(() => ok = sellerStore.TryRestoreFromBarrel(row.Id, SellerVaultActor, out reason));

            Assert.AreEqual(0, SellerLedger(), "a class restore must never credit the stack ledger of its wcid");
            Assert.IsTrue(ok, reason);

            var rebuilt = sellerBackend.Classes.Single(c => c.AccountId == SellerAccount && c.ClassKey == first.ClassKey);

            Assert.AreEqual(first.Count, rebuilt.Count);
            Assert.AreEqual(first.TotalValue, rebuilt.TotalValue);
            Assert.AreEqual(first.CanonicalForm, rebuilt.CanonicalForm);
            Assert.IsNotNull(row.RestoredAt, "the row is stamped restored");

            Assert.AreEqual(4, Line(lineA).Count, "the line is whole again");
            Assert.AreEqual(1, sellerBackend.Logs.Count(l => l.Action == (int)AccountVaultAction.Restore));
        }

        // ---- T9: uniqueness is per line ----

        [TestMethod]
        public void T9_TwoLinesOfOneWcidBothList_ASecondListingOfOneLineIsAlreadyListed_AndTheRepoKeyHoldsIt()
        {
            SeedLineA();
            SeedLineB();
            StartMarket();

            var listingA = ListClass(lineA, 1);
            var listingB = ListClass(lineB, 1);

            Assert.AreNotEqual(listingA.Id, listingB.Id);

            var again = MarketManager.List(Seller, null, 0, 1, Price, MarketChannel.Web, lineA);
            Assert.IsFalse(again.Ok);
            Assert.AreEqual(MarketError.AlreadyListed, again.Error);

            // The index is not the authority; the unique key is. A row that got past the index must still be refused.
            var duplicate = new ShardMarketListing
            {
                SellerAccountId = SellerAccount,
                SellerCharacterGuid = SellerCharacter,
                SellerCharacterName = "Classseller",
                Wcid = BagWcid,
                Count = 1,
                PriceMmd = Price,
                Status = (int)MarketListingStatus.Active,
                ClassKey = lineA,
                ActiveClassKey = lineA,
            };

            Assert.IsFalse(repo.AddListing(duplicate), "(seller, active_Class_Key) is unique");

            var stored = repo.GetAllListings().Where(r => r.SellerAccountId == SellerAccount).ToList();
            Assert.IsTrue(stored.All(r => r.ActiveLedgerWcid == null && r.ActiveItemGuid == null),
                "a class row occupies neither older unique key");
        }

        // ---- T10: a restart keeps the class listing binding ----

        [TestMethod]
        public void T10_AfterReinitializeFromRows_TheClassListingStillResolves_AndStillBlocksABarrel()
        {
            SeedLineA();
            StartMarket();

            var listing = ListClass(lineA, 2);

            MarketManager.Shutdown();
            StartMarket();

            var reloaded = MarketManager.GetListing(listing.Id);

            Assert.IsNotNull(reloaded);
            Assert.AreEqual(lineA, reloaded.ClassKey);
            Assert.IsTrue(reloaded.IsClass);
            Assert.IsFalse(reloaded.IsLedger);

            var viaMarket = MarketManager.Barrel(Seller, null, 0, 1, lineA);
            Assert.IsFalse(viaMarket.Ok);
            Assert.AreEqual(MarketError.ItemListed, viaMarket.Error);

            // And the vault's own check, through the hook, which is the guarantee.
            Assert.IsFalse(Barrel(Line(lineA), 1, out var reason));
            Assert.AreEqual(AccountVaultStore.ItemListedMessage, reason);
            Assert.AreEqual(0, sellerBackend.Barrels.Count);
            Assert.AreEqual(4, Line(lineA).Count);
        }

        // ---- T11: purge is stamp-only ----

        [TestMethod]
        public void T11_PurgeOfAClassBarrelRow_IsStampOnly_AndDestroysNothing()
        {
            SeedLineA();
            StartMarket();

            Assert.IsTrue(MarketManager.Barrel(Seller, null, 0, 1, lineA).Ok);

            var row = sellerBackend.Barrels.Single(b => b.AccountId == SellerAccount);
            var rowsBefore = ClassRows(sellerBackend, SellerAccount);
            var destroyedBefore = sellerWorld.Destroyed.Count;

            var ok = false;
            string reason = null;
            sellerStore.Enqueue(() => ok = sellerStore.TryPurgeFromBarrel(row, out reason));

            Assert.IsTrue(ok, reason);
            Assert.AreEqual(destroyedBefore, sellerWorld.Destroyed.Count, "there is no object behind a class barreling to destroy");
            CollectionAssert.AreEquivalent(rowsBefore.ToList(), ClassRows(sellerBackend, SellerAccount).ToList(), "and no class row moves");
            Assert.AreEqual(0, SellerLedger());
        }

        // ---- in-game /market list, and the kill switch ----

        [TestMethod]
        public void ListByEntryNumber_OfAClassLine_ListsTheWholeLineByItsDisplayId()
        {
            SeedLineA();
            SeedSellerLedger(5);
            StartMarket();

            var entries = sellerStore.GetEntries(0, -1).ToList();
            var number = entries.FindIndex(e => e.Kind == VaultEntryKind.Class && e.ClassDisplayId == lineA) + 1;

            Assert.IsTrue(number > 0);

            var result = MarketManager.ListByEntryNumber(Seller, number, Price, 0, MarketChannel.InGame);

            Assert.IsTrue(result.Ok, $"got {result.Error}");
            Assert.AreEqual(lineA, result.Value.ClassKey, "the numbered line, never the ledger row of its wcid");
            Assert.AreEqual(4, result.Value.Count, "count 0 means the whole line");

            Assert.IsTrue(MarketManager.TryGetVaultEntries(SellerAccount, out var lines, out _));
            Assert.IsTrue(lines[number - 1].Contains($"[listed #{result.Value.Id}"), "the vault rendering attaches the listing to the class line");
            Assert.IsFalse(lines.Where((l, i) => i != number - 1).Any(l => l.Contains("[listed")), "and to nothing else");
        }

        [TestMethod]
        public void KillSwitch_Off_RefusesAClassListingAsItemNotFound_AndNeverFallsThroughToTheLedger()
        {
            SeedLineA();
            SeedSellerLedger(5);
            StartMarket();

            Assert.IsTrue(PropertyManager.ModifyBool("market_allow_class_listings", false));

            var result = MarketManager.List(Seller, null, BagWcid, 1, Price, MarketChannel.Web, lineA);

            Assert.IsFalse(result.Ok);
            Assert.AreEqual(MarketError.ItemNotFound, result.Error);
            Assert.AreEqual(0, MarketManager.GetActiveListingsForAccount(SellerAccount).Count, "no listing of any kind");
        }
    }
}
