using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database;
using ACE.Database.Models.Shard;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity.AccountVault;
using ACE.Server.Managers;
using ACE.Server.Managers.Market;
using ACE.Server.WorldObjects;

using ShardAccountVault = ACE.Database.Models.Shard.AccountVault;
using MarketListing = ACE.Server.Managers.Market.MarketListing;
using MarketBuyOrder = ACE.Server.Managers.Market.MarketBuyOrder;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Wanted (buy order) fills drawn from counted item-CLASS lines, driven through the REAL custody
    /// code: VaultMarketItemStore over two real AccountVaultStores (seller and buyer), each on its own
    /// FakeVaultBackend and FakeVaultWorld, as MarketClassListingTests does. Nothing here needs a
    /// database.
    ///
    /// THE DEFECT. A player's own salvage bags are not identical to their weenie, so the vault folds
    /// equivalent ones into an account_vault_class row - deleting each item record and keeping a count
    /// plus a pooled Value. CountMatching and TryTakeMatching skipped class rows, so a seller holding
    /// four full bags of the wanted material held "none" and could not fill.
    ///
    /// THE FAKE WORLD. FakeVaultWorld builds every class object as a generic salvage bag with no
    /// MaterialType, MaxStructure 100 and no charges. <see cref="Dress"/> stamps what the real
    /// weenie would carry (MaterialType, and for the Hammer MaxStructure 10 plus SalvageToolCharges
    /// 10) onto every object either world builds, so the order predicate sees what production sees.
    ///
    /// Values are deliberately NOT divisible by the count, so every conservation assertion here also
    /// covers the round-half-up pooled share.
    /// </summary>
    [TestClass]
    public class MarketClassOrderFillTests
    {
        private const uint SellerAccount = 8801;
        private const uint SellerCharacter = 0x50000881;
        private const uint BuyerAccount = 8802;
        private const uint BuyerCharacter = 0x50000882;

        /// <summary>Tourmaline has both a bag and a Hammer (SalvageForge.MaterialTable), as MarketOrderFillTests uses.</summary>
        private static readonly int Tourmaline = (int)MaterialType.Tourmaline;

        private const uint TourmalineBagWcid = 21082;
        private const uint TourmalineHammerWcid = 1001910;
        private const uint GraniteBagWcid = 21013;

        private const long Price = 10;
        private const long BuyerStart = 1000;

        private static readonly MarketActor Seller = new MarketActor(SellerAccount, SellerCharacter, "Classfiller");
        private static readonly MarketActor Buyer = new MarketActor(BuyerAccount, BuyerCharacter, "Classwanter");
        private static readonly VaultActor SellerVaultActor = new VaultActor(SellerAccount, SellerCharacter, "Classfiller");

        private FakeVaultBackend sellerBackend;
        private FakeVaultBackend buyerBackend;
        private FakeVaultWorld sellerWorld;
        private FakeVaultWorld buyerWorld;

        private AccountVaultStore sellerStore;
        private AccountVaultStore buyerStore;
        private VaultMarketItemStore itemStore;

        private Container sellerVault;

        private FakeMarketRepository repo;
        private FakeMarketWallet wallet;

        private uint nextVaultRow = 1;

        private Action<uint, uint?, uint, int, string> savedPreWithdrawHook;
        private Func<uint, uint?, uint, string, bool> savedIsListedHook;
        private Func<uint, uint?, uint, int, string, int?> savedWouldDelistHook;
        private Func<uint, ACE.Entity.Models.Weenie> savedWeenieLookup;

        [TestInitialize]
        public void Setup()
        {
            // PlaceOrder and MarketSalvageMaterials reach Player's static initializer; see MarketBuyOrderTests.
            MarketOrderFillTests.EnsurePlayerStaticFieldsSeeded();

            MarketManagerTests.SeedMarketTunables();

            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_entry_cap", 100000));
            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_landblock",
                DefaultPropertyManager.DefaultLongProperties["account_vault_landblock"].Item));

            savedPreWithdrawHook = AccountVaultStore.PreWithdrawHook;
            savedIsListedHook = AccountVaultStore.IsListedHook;
            savedWouldDelistHook = AccountVaultStore.WithdrawWouldDelistHook;
            AccountVaultStore.PreWithdrawHook = null;
            AccountVaultStore.IsListedHook = null;
            AccountVaultStore.WithdrawWouldDelistHook = null;

            savedWeenieLookup = MarketManager.WeenieLookup;
            MarketManager.WeenieLookup = _ => null;

            sellerBackend = new FakeVaultBackend();
            buyerBackend = new FakeVaultBackend();

            sellerWorld = new FakeVaultWorld { PristineResult = false, ClassifyResult = true, ItemMaxStackSize = 1 };
            buyerWorld = new FakeVaultWorld { PristineResult = false, ClassifyResult = true, ItemMaxStackSize = 1 };

            foreach (var world in new[] { sellerWorld, buyerWorld })
            {
                world.MaterializeTransform = Dress;
                world.CreateTransform = DressLedgerUnit;
            }

            repo = new FakeMarketRepository();
            wallet = new FakeMarketWallet();

            wallet.Seed(BuyerCharacter, BuyerStart);
            wallet.Seed(SellerCharacter, 0);

            sellerVault = SeedContainer(sellerBackend, sellerWorld, SellerAccount);
            SeedContainer(buyerBackend, buyerWorld, BuyerAccount);

            BuildStores();
        }

        [TestCleanup]
        public void Teardown()
        {
            MarketManager.Shutdown();

            AccountVaultStore.PreWithdrawHook = savedPreWithdrawHook;
            AccountVaultStore.IsListedHook = savedIsListedHook;
            AccountVaultStore.WithdrawWouldDelistHook = savedWouldDelistHook;
            MarketManager.WeenieLookup = savedWeenieLookup;

            PropertyManager.ModifyLong("account_vault_entry_cap", DefaultPropertyManager.DefaultLongProperties["account_vault_entry_cap"].Item);
            PropertyManager.ModifyBool("account_vault_class_storage", DefaultPropertyManager.DefaultBooleanProperties["account_vault_class_storage"].Item);
            PropertyManager.ModifyBool("market_allow_class_listings", DefaultPropertyManager.DefaultBooleanProperties["market_allow_class_listings"].Item);
        }

        // ---- fixture ----

        private Container SeedContainer(FakeVaultBackend backend, FakeVaultWorld world, uint accountId)
        {
            var container = FakeVaultWorld.MakeContainer(AccountVaultStore.VaultItemCapacity);

            world.Containers[container.Guid.Full] = container;

            backend.Vaults.Add(new ShardAccountVault
            {
                Id = 9900 + nextVaultRow,
                AccountId = accountId,
                ContainerGuid = container.Guid.Full,
                CreatedAt = new DateTime(2026, 1, 1).AddMinutes(nextVaultRow),
                Kind = AccountVaultStore.VaultContainerKind,
            });

            nextVaultRow++;

            return container;
        }

        /// <summary>
        /// (Re)builds both stores over their backends. The store reads its ledger once, at load, so a test
        /// that seeds a ledger row calls this again; the resolver reads the fields, so the market sees the
        /// new stores.
        /// </summary>
        private void BuildStores()
        {
            sellerStore = new AccountVaultStore(SellerAccount, sellerBackend, sellerWorld);
            buyerStore = new AccountVaultStore(BuyerAccount, buyerBackend, buyerWorld);

            Assert.IsTrue(sellerStore.IsLoaded, "the seller's store must load");
            Assert.IsTrue(buyerStore.IsLoaded, "the buyer's store must load");

            itemStore = new VaultMarketItemStore(account =>
                account == SellerAccount ? sellerStore : account == BuyerAccount ? buyerStore : null);
        }

        /// <summary>What the real weenie carries and FakeVaultWorld.MakeSalvageBag does not.</summary>
        private static WorldObject Dress(WorldObject wo)
        {
            switch (wo.WeenieClassId)
            {
                case TourmalineBagWcid:
                    wo.MaterialType = MaterialType.Tourmaline;
                    break;

                case GraniteBagWcid:
                    wo.MaterialType = MaterialType.Granite;
                    break;

                case TourmalineHammerWcid:
                    wo.MaterialType = MaterialType.Tourmaline;
                    wo.MaxStructure = 10;
                    wo.SetProperty(PropertyInt.SalvageToolCharges, 10);
                    break;

                case OtherHammerWcid:
                    wo.MaterialType = MaterialType.Granite;
                    wo.MaxStructure = 10;
                    wo.SetProperty(PropertyInt.SalvageToolCharges, 10);
                    break;
            }

            return wo;
        }

        /// <summary>
        /// A test-only Hammer wcid of ANOTHER material (Granite). The order predicate never reads the wcid,
        /// only MaterialType and the tool fields, so this is a full-charge Hammer that differs from the
        /// Tourmaline one in material alone.
        /// </summary>
        private const uint OtherHammerWcid = 1001999;

        /// <summary>A collapsed ledger unit of a bag wcid: a forge-fresh FULL bag, which is the only bag shape that collapses.</summary>
        private static WorldObject DressLedgerUnit(WorldObject wo)
        {
            if (wo.WeenieClassId == TourmalineBagWcid || wo.WeenieClassId == GraniteBagWcid)
            {
                wo.ItemType = ItemType.TinkeringMaterial;
                wo.Structure = 100;
                wo.MaxStructure = 100;
            }

            return Dress(wo);
        }

        private static WorldObject Bag(uint wcid, int structure, int workmanship, int numItems, int value)
            => Dress(FakeVaultWorld.MakeSalvageBag(wcid, structure, workmanship, numItems, value, $"Salvage ({structure})"));

        private static WorldObject Hammer(int charges, int value)
            => Dress(FakeVaultWorld.MakeSalvageBag(TourmalineHammerWcid, charges, 40, 10, value, "Tourmaline Hammer"));

        private void Deposit(WorldObject item)
        {
            var ok = false;
            string reason = null;

            sellerStore.Enqueue(() => ok = sellerStore.TryDeposit(item, SellerVaultActor, out reason));

            Assert.IsTrue(ok, $"could not seed a deposit: {reason}");
        }

        private List<VaultEntry> SellerLines() => sellerStore.GetEntries(0, -1).Where(e => e.Kind == VaultEntryKind.Class).ToList();

        /// <summary>Four identical full Tourmaline bags: ONE line, ONE member. Values 100+111+150+157.</summary>
        private string SeedFullBagLine()
        {
            foreach (var value in new[] { 100, 111, 150, 157 })
                Deposit(Bag(TourmalineBagWcid, 100, 80, 10, value));

            var line = SellerLines().Single(e => e.Wcid == TourmalineBagWcid && e.Count == 4);
            Assert.AreEqual(1, line.ClassMembers.Count, "control: identical bags are one member row");
            return line.ClassDisplayId;
        }

        /// <summary>
        /// Four full Tourmaline bags that differ ONLY in workmanship and material count: one display line
        /// over TWO member rows, which is the shape a probe-per-line argument has to survive.
        /// </summary>
        private string SeedTwoMemberFullBagLine()
        {
            Deposit(Bag(TourmalineBagWcid, 100, 80, 10, 100));
            Deposit(Bag(TourmalineBagWcid, 100, 80, 10, 111));
            Deposit(Bag(TourmalineBagWcid, 100, 88, 11, 150));
            Deposit(Bag(TourmalineBagWcid, 100, 88, 11, 157));

            var line = SellerLines().Single(e => e.Wcid == TourmalineBagWcid && e.Structure() == 100);
            Assert.AreEqual(2, line.ClassMembers.Count, "control: the line must span TWO class rows, or this is not a multi-member line");
            Assert.AreEqual(4, line.Count);
            return line.ClassDisplayId;
        }

        private VaultEntry Line(string displayId) => SellerLines().SingleOrDefault(e => e.ClassDisplayId == displayId);

        private static Func<WorldObject, bool> Order(int material, MarketBuyOrderKind kind)
            => wo => MarketSalvageMaterials.MatchesOrder(wo, material, kind);

        private int Count(uint account, MarketBuyOrderKind kind, int material = -1)
            => itemStore.CountMatching(account, Order(material < 0 ? Tourmaline : material, kind));

        private bool Take(int count, MarketBuyOrderKind kind, out List<WorldObject> taken, out MarketTakeReceipt receipt, out MarketError error)
        {
            var ok = false;
            List<WorldObject> got = null;
            MarketTakeReceipt rec = null;
            var err = MarketError.None;

            Assert.IsTrue(itemStore.RunSerialized(SellerAccount,
                () => ok = itemStore.TryTakeMatching(SellerAccount, Order(Tourmaline, kind), count, Seller, out got, out rec, out err)),
                "the seller's region must run");

            taken = got ?? new List<WorldObject>();
            receipt = rec;
            error = err;
            return ok;
        }

        private static Dictionary<string, (long count, long value)> ClassRows(FakeVaultBackend backend, uint accountId)
            => backend.Classes.Where(c => c.AccountId == accountId && c.Count > 0)
                .ToDictionary(c => c.ClassKey, c => (c.Count, c.TotalValue), StringComparer.Ordinal);

        private static long TotalCount(FakeVaultBackend backend, uint accountId) => backend.Classes.Where(c => c.AccountId == accountId).Sum(c => c.Count);

        private static long TotalValue(FakeVaultBackend backend, uint accountId) => backend.Classes.Where(c => c.AccountId == accountId).Sum(c => c.TotalValue);

        private long SellerLedger(uint wcid) => sellerBackend.Stacks.Where(s => s.AccountId == SellerAccount && s.Wcid == wcid).Sum(s => s.Count);

        private void StartMarket()
        {
            MarketManager.Initialize(itemStore, wallet, repo);

            // Production wiring (MarketManager.Initialize()), which the three-seam overload leaves out.
            AccountVaultStore.PreWithdrawHook = MarketManager.OnVaultWithdraw;
            AccountVaultStore.IsListedHook = MarketManager.IsListed;
            AccountVaultStore.WithdrawWouldDelistHook = MarketManager.WithdrawWouldDelist;
        }

        private MarketBuyOrder PlaceOrder(MarketBuyOrderKind kind, int count)
        {
            var result = MarketManager.PlaceOrder(Buyer, Tourmaline, kind, count, Price, MarketChannel.Web);
            Assert.IsTrue(result.Ok, $"placing failed with {result.Error}");
            return result.Value;
        }

        // ---- 1-5: counting ----

        [TestMethod]
        public void C1_CountMatching_AFullBagClassLine_CountsEveryItemOnIt()
        {
            SeedFullBagLine();

            Assert.AreEqual(0, sellerVault.Inventory.Count, "control: the bags are a class line, not stored biotas");
            Assert.AreEqual(4, Count(SellerAccount, MarketBuyOrderKind.SalvageBag), "four full bags on a class line are four fillable bags");
        }

        [TestMethod]
        public void C2_CountMatching_APartialBagLine_CountsNothing_BesideAFullLineThatCountsFour()
        {
            SeedFullBagLine();

            Deposit(Bag(TourmalineBagWcid, 60, 80, 10, 40));
            Deposit(Bag(TourmalineBagWcid, 60, 80, 10, 41));

            Assert.AreEqual(2, SellerLines().Count, "control: the partial bags are their own line");
            Assert.AreEqual(4, Count(SellerAccount, MarketBuyOrderKind.SalvageBag),
                "exactly the full line: a partial bag never fills a bag order");
        }

        [TestMethod]
        public void C3_CountMatching_AMultiMemberLineDifferingOnlyInWorkmanship_CountsEveryMember()
        {
            SeedTwoMemberFullBagLine();

            Assert.AreEqual(4, Count(SellerAccount, MarketBuyOrderKind.SalvageBag));
        }

        [TestMethod]
        public void C4_CountMatching_DestroysEveryProbe_AndProbesEachLineOnce()
        {
            SeedFullBagLine();
            Deposit(Bag(TourmalineBagWcid, 60, 80, 10, 40));

            sellerWorld.ResetClassCalls();
            var destroyedBefore = sellerWorld.Destroyed.Count;

            var counted = Count(SellerAccount, MarketBuyOrderKind.SalvageBag);

            var materialized = sellerWorld.MaterializeClassCalls;
            var destroyed = sellerWorld.Destroyed.Count - destroyedBefore;

            Assert.AreEqual(4, counted, "control: the count really probed the full line");
            Assert.AreEqual(2, materialized, "one probe per display line, not one per item");
            Assert.AreEqual(materialized, destroyed, "every probe built is destroyed again");
        }

        [TestMethod]
        public void C5_CountMatching_ALineThatCannotBeMaterialized_CountsNothing()
        {
            SeedFullBagLine();

            Assert.AreEqual(4, Count(SellerAccount, MarketBuyOrderKind.SalvageBag), "control: the line counts while it can be built");

            sellerWorld.FailMaterializeWcids.Add(TourmalineBagWcid);

            Assert.AreEqual(0, Count(SellerAccount, MarketBuyOrderKind.SalvageBag),
                "a line the market cannot inspect is not offered to a fill");
        }

        // ---- 6-7: the take, and every way back ----

        [TestMethod]
        public void C6_MixedStoredLedgerAndClassTake_FallingShort_ReturnsEachObjectItsOwnWay_AndTheClassRowsExactly()
        {
            // One stored biota (kept out of the class tier), two ledger units and a 4-item class line.
            sellerBackend.Stacks.Add(new AccountVaultStack { Id = 7201, AccountId = SellerAccount, Wcid = TourmalineBagWcid, Count = 2 });
            BuildStores();

            var stored = Bag(TourmalineBagWcid, 100, 70, 10, 333);
            sellerWorld.UnclassifiableGuids.Add(stored.Guid.Full);
            Deposit(stored);

            var line = SeedTwoMemberFullBagLine();

            var rowsBefore = ClassRows(sellerBackend, SellerAccount);
            var lineTotal = Line(line).TotalValue;

            Assert.AreEqual(7, Count(SellerAccount, MarketBuyOrderKind.SalvageBag), "control: 1 stored + 2 ledger + 4 class");

            var ok = Take(8, MarketBuyOrderKind.SalvageBag, out var taken, out _, out var error);

            Assert.IsFalse(ok, "eight were asked for and seven are held");
            Assert.AreEqual(MarketError.NoMatchingItems, error);
            Assert.AreEqual(0, taken.Count);

            // The class line WAS drawn on and put back: a take that never touched it would pass the
            // row comparison below for free.
            var classWithdraws = sellerBackend.Logs.Where(l => l.Action == (int)AccountVaultAction.Withdraw && l.ClassKey != null).ToList();
            var classReturns = sellerBackend.Logs.Where(l => l.Action == (int)AccountVaultAction.Return && l.ClassKey != null).ToList();

            Assert.AreEqual(4, classWithdraws.Sum(l => l.Count), "all four class items were taken");
            Assert.AreEqual(4, classReturns.Sum(l => l.Count), "and all four went back INTO the class");
            Assert.AreEqual(lineTotal, classReturns.Sum(l => l.Value ?? 0), "carrying exactly the pooled value they left with");

            var rowsAfter = ClassRows(sellerBackend, SellerAccount);
            Assert.AreEqual(rowsBefore.Count, rowsAfter.Count, "every member row is back");

            foreach (var kvp in rowsBefore)
                Assert.AreEqual(kvp.Value, rowsAfter[kvp.Key], $"member {kvp.Key} must carry exactly its old count AND pooled total");

            Assert.AreEqual(2, SellerLedger(TourmalineBagWcid), "the ledger units went back to the ledger, and no class item joined them");
            Assert.AreEqual(1, sellerVault.Inventory.Count, "exactly one stored biota: no class or ledger object came back as one");
            Assert.IsTrue(sellerVault.Inventory.ContainsKey(stored.Guid), "and it is the original stored bag, as itself");
        }

        [TestMethod]
        public void C7_TakeOfExactlyCountMatching_Succeeds_OneMoreFails_AndLeavesTheVaultUnchanged()
        {
            var line = SeedTwoMemberFullBagLine();

            var held = Count(SellerAccount, MarketBuyOrderKind.SalvageBag);
            Assert.AreEqual(4, held, "control: the line is countable");

            var rowsBefore = ClassRows(sellerBackend, SellerAccount);
            var lineTotal = Line(line).TotalValue;

            Assert.IsFalse(Take(held + 1, MarketBuyOrderKind.SalvageBag, out var none, out _, out var error), "one more than is held must refuse");
            Assert.AreEqual(MarketError.NoMatchingItems, error);
            Assert.AreEqual(0, none.Count);

            var rowsAfterRefusal = ClassRows(sellerBackend, SellerAccount);
            Assert.AreEqual(rowsBefore.Count, rowsAfterRefusal.Count);
            foreach (var kvp in rowsBefore)
                Assert.AreEqual(kvp.Value, rowsAfterRefusal[kvp.Key], "a refused take leaves every member row exactly as it was");
            Assert.AreEqual(0, sellerVault.Inventory.Count, "and turns nothing into a stored biota");
            Assert.AreEqual(0, SellerLedger(TourmalineBagWcid), "or into ledger units");

            Assert.IsTrue(Take(held, MarketBuyOrderKind.SalvageBag, out var taken, out var receipt, out error), $"exactly what is held must succeed, got {error}");
            Assert.AreEqual(held, taken.Count);
            Assert.AreEqual(lineTotal, taken.Sum(i => (long)(i.Value ?? 0)), "the objects carry the line's whole pooled total between them");
            Assert.IsNull(Line(line), "the line is drained");

            // And the receipt puts them back exactly - which it can only do if it recorded every object
            // as class-sourced: a stored-biota return would leave the class rows short.
            var undone = false;
            Assert.IsTrue(itemStore.RunSerialized(SellerAccount, () => undone = itemStore.UndoTake(SellerAccount, taken, receipt, Seller)));
            Assert.IsTrue(undone);

            var rowsAfterUndo = ClassRows(sellerBackend, SellerAccount);
            foreach (var kvp in rowsBefore)
                Assert.AreEqual(kvp.Value, rowsAfterUndo[kvp.Key], "UndoTake restores every member's count and pooled total");
            Assert.AreEqual(0, sellerVault.Inventory.Count, "and files none of them as a stored biota");
        }

        private bool TakeWith(Func<WorldObject, bool> predicate, int count, out List<WorldObject> taken, out MarketError error)
        {
            var ok = false;
            List<WorldObject> got = null;
            var err = MarketError.None;

            Assert.IsTrue(itemStore.RunSerialized(SellerAccount,
                () => ok = itemStore.TryTakeMatching(SellerAccount, predicate, count, Seller, out got, out _, out err)),
                "the seller's region must run");

            taken = got ?? new List<WorldObject>();
            error = err;
            return ok;
        }

        private void AssertRowsExactlyAsBefore(Dictionary<string, (long count, long value)> before, string why)
        {
            var after = ClassRows(sellerBackend, SellerAccount);

            Assert.AreEqual(before.Count, after.Count, $"{why}: every member row is back");

            foreach (var kvp in before)
            {
                Assert.IsTrue(after.ContainsKey(kvp.Key), $"{why}: member {kvp.Key} must be back");
                Assert.AreEqual(kvp.Value, after[kvp.Key], $"{why}: member {kvp.Key} must carry exactly its old count AND pooled total");
            }

            Assert.AreEqual(0, sellerVault.Inventory.Count, $"{why}: no class item may come back as a stored biota");
            Assert.AreEqual(0, SellerLedger(TourmalineBagWcid), $"{why}: or onto the stack ledger");
        }

        /// <summary>
        /// REVIEW F1, and the CATCH-PATH unwind for class items. The predicate's 1st call is the line probe
        /// and its 2nd is the per-item re-check of the FIRST withdrawn object, so throwing there interrupts
        /// the walk over a batch of four objects that WithdrawFromClass has already debited. Every one of
        /// them must still be in `taken` with its class provenance when the catch unwinds; an object added
        /// only as the walk reached it was orphaned - debited from the row and returned nowhere.
        /// </summary>
        [TestMethod]
        public void C11_APredicateThrowMidBatch_UnwindsTheWholeWithdrawnBatch_BackToTheClassRowsExactly()
        {
            SeedTwoMemberFullBagLine();

            var before = ClassRows(sellerBackend, SellerAccount);
            var bag = Order(Tourmaline, MarketBuyOrderKind.SalvageBag);

            var calls = 0;
            Func<WorldObject, bool> throwsOnSecond = wo =>
            {
                if (++calls == 2)
                    throw new InvalidOperationException("simulated predicate failure on the first withdrawn object");

                return bag(wo);
            };

            var ok = TakeWith(throwsOnSecond, 4, out var taken, out var error);

            Assert.IsTrue(calls >= 2, "control: the predicate really threw inside the take");
            Assert.IsFalse(ok);
            Assert.AreEqual(MarketError.VaultUnavailable, error, "a throw is reported as unavailable, never as 'you hold nothing'");
            Assert.AreEqual(0, taken.Count);

            Assert.AreEqual(4, sellerBackend.Logs.Where(l => l.Action == (int)AccountVaultAction.Withdraw && l.ClassKey != null).Sum(l => l.Count),
                "control: the whole batch of four really was withdrawn before the throw");

            AssertRowsExactlyAsBefore(before, "after a predicate throw");
        }

        /// <summary>
        /// REVIEW F4: the probe and the real objects DISAGREE - the predicate passes the line's probe and
        /// refuses every withdrawn object. The per-item re-check is the authority, so this is a shortfall,
        /// and the objects come back by their recorded provenance rather than by what the predicate said.
        /// </summary>
        [TestMethod]
        public void C12_ProbeMatchesButTheRealObjectsDoNot_IsNoMatchingItems_AndTheRowsComeBackExactly()
        {
            SeedTwoMemberFullBagLine();

            var before = ClassRows(sellerBackend, SellerAccount);

            var calls = 0;
            Func<WorldObject, bool> trueOnFirstCallOnly = wo => ++calls == 1;

            var ok = TakeWith(trueOnFirstCallOnly, 4, out var taken, out var error);

            Assert.IsTrue(calls > 1, "control: the per-item re-check really ran after the probe");
            Assert.IsFalse(ok);
            Assert.AreEqual(MarketError.NoMatchingItems, error);
            Assert.AreEqual(0, taken.Count);

            Assert.AreEqual(4, sellerBackend.Logs.Where(l => l.Action == (int)AccountVaultAction.Withdraw && l.ClassKey != null).Sum(l => l.Count),
                "control: the probe let the take open the line");

            AssertRowsExactlyAsBefore(before, "after the re-check refused every object");
        }

        /// <summary>
        /// REVIEW F3: each row is clamped to int.MaxValue on its own, so two large rows must SATURATE rather
        /// than wrap into a negative count that would read as "holds nothing".
        /// </summary>
        [TestMethod]
        public void C13_CountMatchingEntries_TwoHugeLedgerRows_SaturateAtIntMaxValue()
        {
            var world = new FakeVaultWorld { ItemMaxStackSize = 1 };

            var entries = new List<VaultEntry>
            {
                VaultEntry.ForLedger(TourmalineBagWcid, 2_000_000_000L),
                VaultEntry.ForLedger(TourmalineBagWcid, 2_000_000_000L),
            };

            Assert.AreEqual(int.MaxValue, VaultMarketItemStore.CountMatchingEntries(entries, wo => true, world));
        }

        // ---- AccountVaultStore.WithdrawFromClass: a THROWING build mid-drain ----

        /// <summary>
        /// Every object the seller's world builds from here on, so a test can prove each one was
        /// destroyed again (no leaked object, no leaked guid).
        /// </summary>
        private List<WorldObject> RecordBuilds()
        {
            var built = new List<WorldObject>();

            sellerWorld.MaterializeTransform = wo =>
            {
                built.Add(wo);
                return Dress(wo);
            };

            return built;
        }

        /// <summary>
        /// Withdraws all four items of the two-member line A, with the seller's world set to THROW on the
        /// k-th MaterializeClass call. The drain's build order is: member 1 probe (1), member 1 tail (2),
        /// member 2 probe (3), member 2 tail (4).
        /// </summary>
        private void AssertAThrowingBuildUnwindsTheWholeDrain(int throwOnCall, string why)
        {
            var line = SeedTwoMemberFullBagLine();
            var before = ClassRows(sellerBackend, SellerAccount);
            var entry = Line(line);

            var built = RecordBuilds();
            sellerWorld.MaterializeThrowAfterCalls = throwOnCall - 1;

            var ok = true;
            string reason = null;
            List<WorldObject> withdrawn = null;

            var ran = sellerStore.Enqueue(() => ok = sellerStore.TryWithdraw(entry, 4, SellerVaultActor, out withdrawn, out reason, GroupTakeOrder.Front), out var thrown);

            Assert.IsNull(sellerWorld.MaterializeThrowAfterCalls, $"{why}: control: the materializer really threw");
            Assert.IsTrue(ran, $"{why}: the work ran");
            Assert.IsNull(thrown, $"{why}: a throwing build must become a refusal, never escape the withdraw: {thrown?.Message}");
            Assert.IsFalse(ok, $"{why}: TryWithdraw reports failure");
            Assert.AreEqual(AccountVaultStore.UnavailableMessage, reason, $"{why}: with the same reason the null-build path gives");
            Assert.AreEqual(0, withdrawn?.Count ?? 0, $"{why}: nothing is handed out");

            AssertRowsExactlyAsBefore(before, why);

            foreach (var wo in built)
                Assert.IsTrue(sellerWorld.Destroyed.Contains(wo), $"{why}: built object 0x{wo.Guid.Full:X8} must be destroyed again, or its guid leaks");

            Assert.AreEqual(4, Line(line).Count, $"{why}: the store's own view of the line is whole again");
        }

        [TestMethod]
        public void C14_WithdrawFromClass_ThrowOnTheFirstBuild_RefusesCleanly_AndNothingMoves()
            => AssertAThrowingBuildUnwindsTheWholeDrain(1, "throw on the first build");

        [TestMethod]
        public void C15_WithdrawFromClass_ThrowOnALaterMembersProbe_AfterAnEarlierDebitCommitted_RefundsItExactly()
            => AssertAThrowingBuildUnwindsTheWholeDrain(3, "throw on member 2's probe after member 1 was debited");

        [TestMethod]
        public void C16_WithdrawFromClass_ThrowInTheTailLoop_RefundsTheDebitedMemberExactly()
            => AssertAThrowingBuildUnwindsTheWholeDrain(2, "throw in member 1's tail build");

        /// <summary>
        /// The same failure seen from the market: a take over a class line whose withdraw throws part way
        /// must refuse and leave the seller's vault exact. Build order: the line probe CountMatching-style
        /// (1, destroyed), then the drain's member 1 probe (2) and member 1 tail (3), which throws after
        /// member 1 has been debited.
        /// </summary>
        [TestMethod]
        public void C17_TryTakeMatching_OverALineWhoseWithdrawThrowsMidDrain_RefusesAndLeavesTheVaultExact()
        {
            SeedTwoMemberFullBagLine();

            var before = ClassRows(sellerBackend, SellerAccount);
            var built = RecordBuilds();

            sellerWorld.MaterializeThrowAfterCalls = 2;

            var ok = Take(4, MarketBuyOrderKind.SalvageBag, out var taken, out _, out var error);

            Assert.IsNull(sellerWorld.MaterializeThrowAfterCalls, "control: the materializer really threw");
            Assert.IsFalse(ok, "the take must refuse");
            Assert.AreNotEqual(MarketError.None, error);
            Assert.AreEqual(0, taken.Count);

            AssertRowsExactlyAsBefore(before, "after a take whose class withdraw threw");

            foreach (var wo in built)
                Assert.IsTrue(sellerWorld.Destroyed.Contains(wo), $"built object 0x{wo.Guid.Full:X8} must be destroyed again");
        }

        // ---- WithdrawFromClass: the unwind itself must survive a throwing destroy or refund ----

        /// <summary>
        /// Seeds line A, runs <paramref name="arrange"/> with the line's member keys in DRAIN order (the
        /// plan walks VaultEntry.ClassMembers front first), then withdraws all four items through the real
        /// store, capturing any exception that escapes the withdraw rather than letting it fail the test
        /// from inside the queue.
        /// </summary>
        private (bool ok, string reason, Exception thrown, Dictionary<string, (long count, long value)> before, List<string> keys)
            WithdrawAllOfLineA(Action<List<string>> arrange)
        {
            var line = SeedTwoMemberFullBagLine();
            var before = ClassRows(sellerBackend, SellerAccount);
            var entry = Line(line);
            var keys = entry.ClassMembers.Select(m => m.ClassKey).ToList();

            arrange(keys);

            var ok = true;
            string reason = null;

            sellerStore.Enqueue(() => ok = sellerStore.TryWithdraw(entry, 4, SellerVaultActor, out _, out reason, GroupTakeOrder.Front), out var thrown);

            return (ok, reason, thrown, before, keys);
        }

        /// <summary>S2: member 2's debit is Refused, and destroying member 2's probe then THROWS.</summary>
        [TestMethod]
        public void C18_RefusedDebit_ThenTheProbeDestroyThrows_StillRefundsMember1Exactly()
        {
            var r = WithdrawAllOfLineA(keys =>
            {
                sellerBackend.ClassAdjustOverride = (key, delta) => key == keys[1] && delta < 0 ? AccountVaultStackAdjustResult.Refused : (AccountVaultStackAdjustResult?)null;
                sellerWorld.ThrowFromDestroyItemCount = 1;
            });

            Assert.AreEqual(0, sellerWorld.ThrowFromDestroyItemCount, "control: the destroy really threw");
            Assert.IsNull(r.thrown, $"no exception may escape the withdraw: {r.thrown?.Message}");
            Assert.IsFalse(r.ok);
            AssertRowsExactlyAsBefore(r.before, "S2");
            Assert.AreEqual("That is no longer in your vault.", r.reason, "the refused-debit path's own reason (AccountVaultStore.GoneMessage, private)");
        }

        /// <summary>S3: member 2's probe builds as NULL, and the unwind's first destroy THROWS.</summary>
        [TestMethod]
        public void C19_NullProbe_ThenTheUnwindsFirstDestroyThrows_StillRefundsMember1Exactly()
        {
            var r = WithdrawAllOfLineA(keys =>
            {
                sellerWorld.MaterializeFailAfterCalls = 2;   // member 1 probe and tail build; member 2's probe is null
                sellerWorld.ThrowFromDestroyItemCount = 1;
            });

            Assert.AreEqual(0, sellerWorld.ThrowFromDestroyItemCount, "control: the destroy really threw");
            Assert.IsNull(r.thrown, $"no exception may escape the withdraw: {r.thrown?.Message}");
            Assert.IsFalse(r.ok);
            Assert.AreEqual(AccountVaultStore.UnavailableMessage, r.reason);
            AssertRowsExactlyAsBefore(r.before, "S3");
        }

        /// <summary>S4: member 2's probe build THROWS, and the catch's unwind's first destroy THROWS too.</summary>
        [TestMethod]
        public void C20_MaterializeThrows_ThenTheCatchUnwindsFirstDestroyThrows_StillRefundsMember1Exactly()
        {
            var r = WithdrawAllOfLineA(keys =>
            {
                sellerWorld.MaterializeThrowAfterCalls = 2;  // the 3rd build, member 2's probe, throws
                sellerWorld.ThrowFromDestroyItemCount = 1;
            });

            Assert.IsNull(sellerWorld.MaterializeThrowAfterCalls, "control: the materializer really threw");
            Assert.AreEqual(0, sellerWorld.ThrowFromDestroyItemCount, "control: the destroy really threw");
            Assert.IsNull(r.thrown, $"no exception may escape the withdraw: {r.thrown?.Message}");
            Assert.IsFalse(r.ok);
            Assert.AreEqual(AccountVaultStore.UnavailableMessage, r.reason);
            AssertRowsExactlyAsBefore(r.before, "S4");
        }

        /// <summary>
        /// F2(a): member 2's DEBIT CALL ITSELF THROWS. Its outcome is unknown, and the standing ruling
        /// (never destroy property on an unknown outcome) refunds it as a Failed debit, marked with
        /// CLASS REFUND OVER UNKNOWN DEBIT. This fake's throw happens before anything is applied, so the
        /// refund OVER-CREDITS member 2 by exactly its planned take and value - the known, accepted cost of
        /// that ruling, pinned here so a change to it is a visible decision. Member 1 comes back exactly.
        /// </summary>
        [TestMethod]
        public void C21_ADebitThatThrows_IsRefundedAsAnUnknownDebit_WithTheMarker_AndMember1Exact()
        {
            List<log4net.Core.LoggingEvent> events;
            (bool ok, string reason, Exception thrown, Dictionary<string, (long count, long value)> before, List<string> keys) r;

            using (var capture = new MarketLogCapture())
            {
                r = WithdrawAllOfLineA(keys =>
                {
                    sellerBackend.ClassAdjustOverride = (key, delta) => key == keys[1] && delta < 0
                        ? throw new InvalidOperationException("simulated shard failure during member 2's debit")
                        : (AccountVaultStackAdjustResult?)null;
                });

                events = capture.Events;
            }

            Assert.IsNull(r.thrown, $"no exception may escape the withdraw: {r.thrown?.Message}");
            Assert.IsFalse(r.ok);
            Assert.AreEqual(AccountVaultStore.UnavailableMessage, r.reason);

            var after = ClassRows(sellerBackend, SellerAccount);
            var m1 = r.keys[0];
            var m2 = r.keys[1];

            Assert.AreEqual(r.before[m1], after[m1], "member 1, whose debit really applied, comes back exactly");
            Assert.AreEqual((r.before[m2].count * 2, r.before[m2].value * 2), after[m2],
                "member 2's debit never applied here, so the unknown-debit refund over-credits it by its whole planned take and value (the accepted ruling)");

            Assert.IsTrue(events.Any(e => e.RenderedMessage.Contains("CLASS REFUND OVER UNKNOWN DEBIT") && e.RenderedMessage.Contains(m2)),
                "the over-credit must carry the marker an operator reconciles from");
            Assert.AreEqual(0, sellerVault.Inventory.Count, "nothing comes back as a stored biota");
        }

        /// <summary>
        /// F2(b), no double refund. A REFUND that throws has an unknown outcome and must be attempted
        /// exactly ONCE - never retried by a second unwind pass, which is what the old `unwinding` guard in
        /// the catch existed to prevent. Member 2's tail builds null, so the shortfall site refunds member 2
        /// itself, and that refund THROWS. Member 1 must still be refunded exactly by the unwind after it;
        /// member 2's refund is attempted once and not again, and is left to the logged reconciliation.
        /// </summary>
        [TestMethod]
        public void C22_ARefundThatThrows_IsAttemptedOnce_NeverDoubled_AndTheOtherMemberIsStillRefunded()
        {
            var m2Refunds = 0;

            var r = WithdrawAllOfLineA(keys =>
            {
                sellerWorld.MaterializeFailAfterCalls = 3;   // member 2's tail build is null
                sellerBackend.ClassAdjustOverride = (key, delta) =>
                {
                    if (key == keys[1] && delta > 0 && ++m2Refunds == 1)
                        throw new InvalidOperationException("simulated shard failure during member 2's refund");

                    return null;
                };
            });

            Assert.IsNull(r.thrown, $"no exception may escape the withdraw: {r.thrown?.Message}");
            Assert.IsFalse(r.ok);
            Assert.AreEqual(1, m2Refunds, "member 2's refund is attempted exactly once: an unknown outcome is never retried");

            var after = ClassRows(sellerBackend, SellerAccount);
            var m1 = r.keys[0];
            var m2 = r.keys[1];

            Assert.IsTrue(after.TryGetValue(m1, out var m1After), "member 1's row must exist again after member 2's refund threw");
            Assert.AreEqual(r.before[m1], m1After, "member 1 is still refunded exactly after member 2's refund threw");
            Assert.IsFalse(after.ContainsKey(m2) && after[m2].count > r.before[m2].count, "and member 2 is never credited above where it started");
            Assert.AreEqual(0, sellerVault.Inventory.Count, "nothing comes back as a stored biota");
        }

        // ---- 8: end to end ----

        [TestMethod]
        public void C8_FillOrder_FromAClassLine_MovesItems_AndConservesThePool()
        {
            var line = SeedTwoMemberFullBagLine();
            StartMarket();

            var order = PlaceOrder(MarketBuyOrderKind.SalvageBag, 4);   // buyer 1000 -> 960

            var sellerValueBefore = TotalValue(sellerBackend, SellerAccount);

            var result = MarketManager.FillOrder(Seller, order.Id, 3, MarketChannel.Web);

            var sellerDrop = sellerValueBefore - TotalValue(sellerBackend, SellerAccount);
            var buyerGain = TotalValue(buyerBackend, BuyerAccount);

            Assert.IsTrue(result.Ok, $"fill failed with {result.Error}");
            Assert.AreEqual(MarketTransactionStatus.Completed, result.Value.Status);
            Assert.AreEqual(3, result.Value.Count);

            Assert.AreEqual(3, TotalCount(buyerBackend, BuyerAccount), "the buyer gained three items");
            Assert.AreEqual(1, Line(line).Count, "the seller's line lost three");

            var withdrawn = sellerBackend.Logs.Where(l => l.Action == (int)AccountVaultAction.Withdraw && l.ClassKey != null).Sum(l => l.Value ?? 0);
            Assert.IsTrue(sellerDrop > 0, "control: value really left the seller");
            Assert.AreEqual(withdrawn, sellerDrop, "the seller's pool fell by exactly the shares its Withdraw rows record");
            Assert.AreEqual(sellerDrop, buyerGain, "CONSERVATION: the buyer's pool rose by exactly that");

            Assert.AreEqual(3 * Price, wallet.GetBalanceMmd(SellerCharacter), "the seller is paid from escrow");
            Assert.AreEqual(BuyerStart - 4 * Price, wallet.GetBalanceMmd(BuyerCharacter), "the buyer paid at placement, not again");

            var live = MarketManager.GetBuyOrder(order.Id);
            Assert.AreEqual(1, live.CountRemaining);
            Assert.AreEqual(Price, live.EscrowMmd);
            Assert.AreEqual(MarketBuyOrderStatus.Active, live.Status);
        }

        // ---- 9: the kind still decides ----

        [TestMethod]
        public void C9_AFullChargeHammerLine_CountsForAHammerOrderNotABagOrder_APartChargeLineForNeither()
        {
            foreach (var value in new[] { 300, 301, 302 })
                Deposit(Hammer(10, value));

            Deposit(Hammer(7, 200));
            Deposit(Hammer(7, 201));

            Assert.AreEqual(2, SellerLines().Count, "control: full and part-charge Hammers are separate lines");

            Assert.AreEqual(3, Count(SellerAccount, MarketBuyOrderKind.SalvageHammer), "the three full-charge Hammers fill a Hammer order");
            Assert.AreEqual(0, Count(SellerAccount, MarketBuyOrderKind.SalvageBag), "and no Hammer ever fills a bag order");
        }

        /// <summary>
        /// THE PARITY TABLE. The web repo pins the same expected counts, so the two sides agree on what a
        /// seller can fill from. Each case is one class line alone in a fresh vault. The zero rows cannot
        /// fail against a server that skips class lines entirely - that is what the full-bag and
        /// full-charge rows, and C9, are for.
        /// </summary>
        [TestMethod]
        [DataRow("full-bag line x4", 4, 0)]
        [DataRow("partial-bag line", 0, 0)]
        [DataRow("full-charge hammer line", 0, 3)]
        [DataRow("part-charge hammer line", 0, 0)]
        [DataRow("other material", 0, 0)]
        [DataRow("other-material full-charge hammer line", 0, 0)]
        public void C9_ParityTable(string kase, int bagOrder, int hammerOrder)
        {
            switch (kase)
            {
                case "full-bag line x4":
                    SeedFullBagLine();
                    break;

                case "partial-bag line":
                    Deposit(Bag(TourmalineBagWcid, 60, 80, 10, 40));
                    Deposit(Bag(TourmalineBagWcid, 60, 80, 10, 41));
                    break;

                case "full-charge hammer line":
                    foreach (var value in new[] { 300, 301, 302 })
                        Deposit(Hammer(10, value));
                    break;

                case "part-charge hammer line":
                    Deposit(Hammer(7, 200));
                    Deposit(Hammer(7, 201));
                    break;

                case "other material":
                    Deposit(Bag(GraniteBagWcid, 100, 80, 10, 100));
                    Deposit(Bag(GraniteBagWcid, 100, 80, 10, 101));

                    Assert.AreEqual(2, Count(SellerAccount, MarketBuyOrderKind.SalvageBag, (int)MaterialType.Granite),
                        "control: the Granite line is fillable for a GRANITE order, so its 0 below is the material talking");
                    break;

                case "other-material full-charge hammer line":
                    foreach (var value in new[] { 300, 301, 302 })
                        Deposit(Dress(FakeVaultWorld.MakeSalvageBag(OtherHammerWcid, 10, 40, 10, value, "Granite Hammer")));

                    // The hammer column must discriminate on MATERIAL, not only on kind: this line IS a
                    // full-charge Hammer, so a Granite Hammer order would take all three.
                    Assert.AreEqual(3, Count(SellerAccount, MarketBuyOrderKind.SalvageHammer, (int)MaterialType.Granite),
                        "control: the line is a fillable full-charge Hammer line for its OWN material");
                    break;

                default:
                    Assert.Fail($"unknown case {kase}");
                    break;
            }

            Assert.AreEqual(1, SellerLines().Count, "control: the case is exactly one class line");
            Assert.AreEqual(bagOrder, Count(SellerAccount, MarketBuyOrderKind.SalvageBag), $"{kase}: Tourmaline bag order");
            Assert.AreEqual(hammerOrder, Count(SellerAccount, MarketBuyOrderKind.SalvageHammer), $"{kase}: Tourmaline Hammer order");
        }

        // ---- 10: a class listing on the same line ----

        [TestMethod]
        public void C10_AClassListingSurvivesAFillFromTheUnlistedSurplus_AndClosesWhenAFillEatsListedUnits()
        {
            var line = SeedFullBagLine();
            StartMarket();

            var listed = MarketManager.List(Seller, null, 0, 2, Price, MarketChannel.Web, line);
            Assert.IsTrue(listed.Ok, $"control: the class listing must be created, got {listed.Error}");
            var listing = listed.Value;

            var order = PlaceOrder(MarketBuyOrderKind.SalvageBag, 3);

            var first = MarketManager.FillOrder(Seller, order.Id, 2, MarketChannel.Web);
            Assert.IsTrue(first.Ok, $"the first fill failed with {first.Error}");
            Assert.AreEqual(2, Line(line).Count, "control: two of four left the line");
            Assert.AreEqual(MarketListingStatus.Active, MarketManager.GetListing(listing.Id).Status,
                "the fill took only the unlisted surplus, so the listing of 2 stands");

            var second = MarketManager.FillOrder(Seller, order.Id, 1, MarketChannel.Web);
            Assert.IsTrue(second.Ok, $"the second fill failed with {second.Error}");
            Assert.AreEqual(1, Line(line).Count);
            Assert.AreEqual(MarketListingStatus.Delisted, MarketManager.GetListing(listing.Id).Status,
                "this fill ate into the listed units, so the listing closes as a withdraw would close it");
        }
    }

    internal static class ClassLineTestExtensions
    {
        /// <summary>The Structure a class line was keyed on, read off its first member's canonical form.</summary>
        public static int? Structure(this VaultEntry entry)
            => VaultItemClass.TryParseCanonicalForm(entry.CanonicalForm, out _, out var overrides, out _)
                ? overrides.GetInt(PropertyInt.Structure)
                : null;
    }
}
