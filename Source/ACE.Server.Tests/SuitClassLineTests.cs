using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.DatLoader;
using ACE.Database.Models.Shard;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity.AccountVault;
using ACE.Server.Managers;
using ACE.Server.Managers.Market;
using ACE.Server.Managers.Market.Suit;
using ACE.Server.WorldObjects;

using ShardAccountVault = ACE.Database.Models.Shard.AccountVault;

namespace ACE.Server.Tests
{
    /// <summary>
    /// VaultMarketItemStore.DescribeClassForSuit, the PRODUCTION class-line path of the suit inventory,
    /// against a real VaultMarketItemStore over a real AccountVaultStore on FakeVaultBackend/FakeVaultWorld
    /// (the MarketClassListingTests fixture). MarketApiTests-style fakes cannot reach it: they never
    /// materialize a display object.
    ///
    /// The fake world always materializes a salvage bag; FakeVaultWorld.MaterializeTransform is how a
    /// test makes that display object look like a ring (or like something whose projection throws).
    /// </summary>
    [TestClass]
    public class SuitClassLineTests
    {
        private const uint Account = 8801;
        private const uint Character = 0x50000881;
        private const uint BagWcid = 21113;
        private const string LineName = "Ring of the Test";

        private static readonly VaultActor Actor = new VaultActor(Account, Character, "Classowner");

        private FakeVaultBackend backend;
        private FakeVaultWorld world;
        private AccountVaultStore vault;
        private VaultMarketItemStore itemStore;

        private IClothingIconSource savedClothingIcons;
        private Func<uint, Weenie> savedWeenieLookup;
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
            world = new FakeVaultWorld { PristineResult = false, ClassifyResult = true };

            var container = FakeVaultWorld.MakeContainer(AccountVaultStore.VaultItemCapacity);
            world.Containers[container.Guid.Full] = container;
            backend.Vaults.Add(new ShardAccountVault
            {
                Id = 9900,
                AccountId = Account,
                ContainerGuid = container.Guid.Full,
                CreatedAt = new DateTime(2026, 1, 1),
                Kind = AccountVaultStore.VaultContainerKind,
            });

            vault = new AccountVaultStore(Account, backend, world);
            Assert.IsTrue(vault.IsLoaded);

            itemStore = new VaultMarketItemStore(account => account == Account ? vault : null);

            // Two bags with a name override form one class line of count 2.
            for (var i = 0; i < 2; i++)
            {
                var bag = FakeVaultWorld.MakeSalvageBag(BagWcid, 100, 80, 10, 100 + i, LineName);
                var ok = false;
                string reason = null;
                vault.Enqueue(() => ok = vault.TryDeposit(bag, Actor, out reason));
                Assert.IsTrue(ok, reason);
            }
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

        private VaultEntry Line()
        {
            var line = vault.GetEntries(0, -1).Single(e => e.Kind == VaultEntryKind.Class);
            Assert.AreEqual(2, line.Count, "control: the seeded class line must hold both bags");
            return line;
        }

        private static WorldObject AsRing(WorldObject built)
        {
            built.ValidLocations = EquipMask.FingerWearLeft | EquipMask.FingerWearRight;
            return built;
        }

        private static Weenie RingWeenie(string name) => new Weenie
        {
            WeenieClassId = BagWcid,
            WeenieType = WeenieType.Clothing,
            PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, name } },
            PropertiesInt = new Dictionary<PropertyInt, int>
            {
                { PropertyInt.ItemType, (int)ItemType.Jewelry },
                { PropertyInt.ValidLocations, (int)(EquipMask.FingerWearLeft | EquipMask.FingerWearRight) },
            },
        };

        [TestMethod]
        public void SuitClass_OverriddenName_AppearsInTheSuitItem_AndTheDisplayObjectIsDestroyed()
        {
            world.MaterializeTransform = AsRing;
            var line = Line();
            var destroyedBefore = world.Destroyed.Count;

            var item = itemStore.DescribeClassForSuit(Account, line, SuitItem.SourceVault);

            Assert.IsNotNull(item);
            Assert.AreEqual(LineName, item.Name, "the class name override must reach the projection");
            Assert.AreEqual(line.ClassDisplayId, item.ClassKey);
            Assert.AreEqual(2, item.Count);
            Assert.AreEqual(BagWcid, item.Wcid);
            Assert.IsNull(item.ItemGuid, "a class line has no biota to name");
            Assert.AreEqual("vault", item.Source);
            Assert.AreEqual(destroyedBefore + 1, world.Destroyed.Count, "the materialized display object must be destroyed");
        }

        [TestMethod]
        public void SuitClass_ProjectionThrows_ReturnsNull_AndStillDestroysTheDisplayObject()
        {
            // A ClothingBase on the display object plus a clothing source that throws makes the icon
            // resolution inside the projection throw AFTER the object exists.
            world.MaterializeTransform = built =>
            {
                AsRing(built);
                built.SetProperty(PropertyDataId.ClothingBase, 0x10000123u);
                return built;
            };
            MarketSnapshot.ClothingIcons = new ThrowingClothingSource();

            var line = Line();
            var destroyedBefore = world.Destroyed.Count;

            var item = itemStore.DescribeClassForSuit(Account, line, SuitItem.SourceVault);

            Assert.IsNull(item, "a projection failure is swallowed into null so the caller falls back");
            Assert.AreEqual(destroyedBefore + 1, world.Destroyed.Count, "the display object must be destroyed on the throwing path too");
        }

        [TestMethod]
        public void SuitClass_NotSuitRelevant_ReturnsNull_DestroysTheDisplayObject_AndTheCallerFallsBack()
        {
            // No transform: the display object is a salvage bag with no ValidLocations.
            var line = Line();
            var destroyedBefore = world.Destroyed.Count;

            Assert.IsNull(itemStore.DescribeClassForSuit(Account, line, SuitItem.SourceVault));
            Assert.AreEqual(destroyedBefore + 1, world.Destroyed.Count);

            // The caller (TryGetSuitInventory) then falls back to the template weenie, which here IS
            // suit-relevant, so the line still appears - named from the weenie, not the override.
            MarketManager.WeenieLookup = _ => RingWeenie("Template Ring");
            MarketManager.Initialize(itemStore, new FakeMarketWallet(), new FakeMarketRepository());

            Assert.IsTrue(MarketManager.TryGetSuitInventory(Account, out var items, out _));

            var fallback = items.Single();
            Assert.AreEqual("Template Ring", fallback.Name);
            Assert.AreEqual(line.ClassDisplayId, fallback.ClassKey);
            Assert.AreEqual(2, fallback.Count);
        }

        [TestMethod]
        public void SuitClass_ProductionPath_UsesTheMaterializedDisplayObject()
        {
            world.MaterializeTransform = AsRing;
            MarketManager.WeenieLookup = _ => RingWeenie("Template Ring");
            MarketManager.Initialize(itemStore, new FakeMarketWallet(), new FakeMarketRepository());

            Assert.IsTrue(MarketManager.TryGetSuitInventory(Account, out var items, out _));

            Assert.AreEqual(LineName, items.Single().Name, "with the production store the override name wins over the template");
        }

        private sealed class ThrowingClothingSource : IClothingIconSource
        {
            public ClothingIconTable TryGetTable(uint clothingBase) => throw new InvalidOperationException("clothing table unavailable");
        }
    }
}
