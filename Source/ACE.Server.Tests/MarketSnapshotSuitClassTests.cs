using System;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.DatLoader;
using ACE.Database.Models.Shard;
using ACE.Entity.Enum;
using ACE.Server.Entity.AccountVault;
using ACE.Server.Managers;
using ACE.Server.Managers.Market;
using ACE.Server.WorldObjects;

using ShardAccountVault = ACE.Database.Models.Shard.AccountVault;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The suit projection on a CLASS line's listing snapshot, through the production
    /// VaultMarketItemStore.DescribeClass and MarketManager.List over a real AccountVaultStore on the
    /// fake backend/world (the SuitClassLineTests fixture). The fake world materializes a salvage bag;
    /// MaterializeTransform dresses it as a ring so it is suit-relevant.
    ///
    /// THE LEAK UNDER TEST: the class snapshot is cached by class key and pooled share (not by account or
    /// count) and handed to every viewer, so stamping the seller's whole pool count into suit.count
    /// would publish it and carry it across accounts.
    /// </summary>
    [TestClass]
    public class MarketSnapshotSuitClassTests
    {
        private const uint Account = 8811;
        private const uint Character = 0x50000891;
        private const uint BagWcid = 21113;
        private const string LineName = "Ring of the Leak Test";

        private static readonly VaultActor Actor = new VaultActor(Account, Character, "Classowner");
        private static readonly MarketActor Seller = new MarketActor(Account, Character, "Classowner");

        private FakeVaultBackend backend;
        private FakeVaultWorld world;
        private AccountVaultStore vault;
        private VaultMarketItemStore itemStore;

        private IClothingIconSource savedClothingIcons;
        private Func<uint, ACE.Entity.Models.Weenie> savedWeenieLookup;
        private Action<uint, uint?, uint, int, string> savedPreWithdrawHook;
        private Func<uint, uint?, uint, string, bool> savedIsListedHook;

        [TestInitialize]
        public void Setup()
        {
            MarketManagerTests.SeedMarketTunables();

            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_entry_cap", 100000));
            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_landblock",
                DefaultPropertyManager.DefaultLongProperties["account_vault_landblock"].Item));

            savedClothingIcons = MarketSnapshot.ClothingIcons;
            savedWeenieLookup = MarketManager.WeenieLookup;
            savedPreWithdrawHook = AccountVaultStore.PreWithdrawHook;
            savedIsListedHook = AccountVaultStore.IsListedHook;
            MarketSnapshot.ClothingIcons = null;
            AccountVaultStore.PreWithdrawHook = null;
            AccountVaultStore.IsListedHook = null;
            MarketManager.WeenieLookup = _ => null;

            backend = new FakeVaultBackend();
            world = new FakeVaultWorld
            {
                PristineResult = false,
                ClassifyResult = true,
                MaterializeTransform = built =>
                {
                    built.ValidLocations = EquipMask.FingerWearLeft | EquipMask.FingerWearRight;
                    return built;
                },
            };

            var container = FakeVaultWorld.MakeContainer(AccountVaultStore.VaultItemCapacity);
            world.Containers[container.Guid.Full] = container;
            backend.Vaults.Add(new ShardAccountVault
            {
                Id = 9910,
                AccountId = Account,
                ContainerGuid = container.Guid.Full,
                CreatedAt = new DateTime(2026, 1, 1),
                Kind = AccountVaultStore.VaultContainerKind,
            });

            vault = new AccountVaultStore(Account, backend, world);
            Assert.IsTrue(vault.IsLoaded);

            itemStore = new VaultMarketItemStore(account => account == Account ? vault : null);
        }

        [TestCleanup]
        public void Teardown()
        {
            MarketManager.Shutdown();
            MarketSnapshot.ClothingIcons = savedClothingIcons;
            MarketManager.WeenieLookup = savedWeenieLookup;
            AccountVaultStore.PreWithdrawHook = savedPreWithdrawHook;
            AccountVaultStore.IsListedHook = savedIsListedHook;
        }

        private void DepositBag(int value)
        {
            var bag = FakeVaultWorld.MakeSalvageBag(BagWcid, 100, 80, 10, value, LineName);
            var ok = false;
            string reason = null;
            vault.Enqueue(() => ok = vault.TryDeposit(bag, Actor, out reason));
            Assert.IsTrue(ok, reason);
        }

        private VaultEntry Line(int expectedCount)
        {
            var line = vault.GetEntries(0, -1).Single(e => e.Kind == VaultEntryKind.Class);
            Assert.AreEqual(expectedCount, line.Count, "control: the class line must hold the bags deposited so far");
            return line;
        }

        [TestMethod]
        public void ClassSuit_NeverCarriesThePoolCount_WhateverTheLineHolds()
        {
            // Values 100 and 101 pool to a share of 101 (round half up), and adding a third 101 keeps it
            // at 101: the SAME class key and share at counts 2 and 3, which is exactly the key the
            // class cache uses, so a stamped count would be served to a viewer of the other pool.
            DepositBag(100);
            DepositBag(101);
            var two = Line(2);
            var snapshotTwo = itemStore.DescribeClass(Account, two);

            DepositBag(101);
            var three = Line(3);
            var snapshotThree = itemStore.DescribeClass(Account, three);

            Assert.AreEqual(two.ClassKey, three.ClassKey, "control: same class key");
            Assert.AreEqual(AccountVaultStore.PooledShare(two.TotalValue, two.Count), AccountVaultStore.PooledShare(three.TotalValue, three.Count),
                "control: same pooled share, so the cache would treat both as one entry");

            Assert.IsNotNull(snapshotTwo.Suit, "control: the dressed ring must be suit-relevant");
            Assert.IsNotNull(snapshotThree.Suit);

            Assert.AreEqual(snapshotTwo.Suit.Count, snapshotThree.Suit.Count, "the suit count must not move with the pool");
            Assert.AreEqual(1, snapshotTwo.Suit.Count);
            Assert.AreEqual(1, snapshotThree.Suit.Count);
            Assert.IsNull(snapshotThree.Suit.ItemGuid);
            Assert.AreEqual(three.ClassDisplayId, snapshotThree.Suit.ClassKey);
            Assert.IsFalse(MarketSnapshot.Serialize(snapshotThree).Contains("\"count\":3"), "the pool count must not appear anywhere in the snapshot JSON");
        }

        [TestMethod]
        public void ListingAClassLine_SnapshotSuitCountIsNotThePool()
        {
            DepositBag(100);
            DepositBag(101);
            DepositBag(101);
            var line = Line(3);

            MarketManager.Initialize(itemStore, new FakeMarketWallet(), new FakeMarketRepository());

            var result = MarketManager.List(Seller, null, 0, 1, 10, MarketChannel.Web, line.ClassDisplayId);

            Assert.IsTrue(result.Ok, $"class listing refused: {result.Error}");

            var suit = result.Value.Snapshot.Suit;

            Assert.IsNotNull(suit);
            Assert.AreEqual(1, suit.Count, "the pool of 3 must not be stamped into the listing's suit");
            Assert.IsNull(suit.ItemGuid);
            Assert.AreEqual(line.ClassDisplayId, suit.ClassKey);
        }
    }
}
