using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Models.Shard;
using ACE.Server.Entity.AccountVault;
using ACE.Server.Managers;
using ACE.Server.Managers.Market;
using ACE.Server.WorldObjects;

using ShardAccountVault = ACE.Database.Models.Shard.AccountVault;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The vault half of the "a listing over a group lists every member" premise. The suit inventory marks
    /// every member of a group listed because ANY withdraw from a group is reported to the market under the
    /// group's REPRESENTATIVE guid (the pre-withdraw hook call in AccountVaultStore.TryWithdraw), and that
    /// closes the whole listing. SuitRoutesTests pins the market half by calling OnVaultWithdraw directly;
    /// this drives a real withdraw through a real AccountVaultStore with the production hook wired, so if the
    /// store ever stopped passing the representative guid this test fails.
    /// </summary>
    [TestClass]
    public class SuitGroupWithdrawTests
    {
        private const uint Account = 8901;
        private const uint Character = 0x50000891;
        private const uint BagWcid = 20980;

        private static readonly VaultActor Actor = new VaultActor(Account, Character, "Groupowner");

        private FakeVaultBackend backend;
        private FakeVaultWorld world;
        private AccountVaultStore vault;
        private FakeMarketRepository repo;

        private Action<uint, uint?, uint, int, string> savedPreWithdrawHook;
        private Func<uint, uint?, uint, string, bool> savedIsListedHook;
        private Func<uint, ACE.Entity.Models.Weenie> savedWeenieLookup;

        [TestInitialize]
        public void Setup()
        {
            MarketManagerTests.SeedMarketTunables();

            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_entry_cap", 100000));
            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_landblock",
                DefaultPropertyManager.DefaultLongProperties["account_vault_landblock"].Item));

            savedPreWithdrawHook = AccountVaultStore.PreWithdrawHook;
            savedIsListedHook = AccountVaultStore.IsListedHook;
            savedWeenieLookup = MarketManager.WeenieLookup;
            MarketManager.WeenieLookup = _ => null;

            backend = new FakeVaultBackend();
            world = new FakeVaultWorld();
            repo = new FakeMarketRepository();

            var container = FakeVaultWorld.MakeContainer(255);
            world.Containers[container.Guid.Full] = container;
            backend.Vaults.Add(new ShardAccountVault
            {
                Id = 9950,
                AccountId = Account,
                ContainerGuid = container.Guid.Full,
                CreatedAt = new DateTime(2026, 1, 1),
                Kind = AccountVaultStore.VaultContainerKind,
            });

            for (var i = 0; i < 3; i++)
                Assert.IsTrue(container.TryAddToInventory(FakeVaultWorld.MakeSalvageBag(BagWcid, 100, 77, 12, 640 + i)), "could not seed a vault item");

            vault = new AccountVaultStore(Account, backend, world);
        }

        [TestCleanup]
        public void Teardown()
        {
            MarketManager.Shutdown();
            AccountVaultStore.PreWithdrawHook = savedPreWithdrawHook;
            AccountVaultStore.IsListedHook = savedIsListedHook;
            MarketManager.WeenieLookup = savedWeenieLookup;
        }

        [TestMethod]
        public void GroupListing_ARealSingleMemberWithdraw_ClosesTheWholeListing()
        {
            var itemStore = new VaultMarketItemStore(account => account == Account ? vault : null);

            // Production wiring of the two vault hooks, as MarketClassListingTests.StartMarket does.
            MarketManager.Initialize(itemStore, new FakeMarketWallet(), repo);
            AccountVaultStore.PreWithdrawHook = MarketManager.OnVaultWithdraw;
            AccountVaultStore.IsListedHook = MarketManager.IsListed;

            var group = vault.GetEntries(0, -1).Single(e => e.Kind == VaultEntryKind.StoredItem);
            Assert.IsTrue(group.IsGroup, "precondition: three equivalent bags are one group row");
            Assert.AreEqual(3L, group.Count);

            var seller = new MarketActor(Account, Character, "Groupowner");
            var result = MarketManager.List(seller, group.Guid.Full, BagWcid, 2, 50, MarketChannel.Web);
            Assert.IsTrue(result.Ok, $"group listing refused: {result.Error}");
            var listing = result.Value;
            Assert.AreEqual(MarketListingStatus.Active, MarketManager.GetListing(listing.Id).Status, "precondition: the listing is live");

            // The vault owner takes ONE member out - fewer than the 2 the listing covers.
            var ok = false;
            List<WorldObject> got = null;
            string reason = null;
            vault.Enqueue(() => ok = vault.TryWithdraw(group, 1, Actor, out got, out reason, GroupTakeOrder.Front));

            Assert.IsTrue(ok, reason);
            Assert.AreEqual(1, got.Count, "exactly one member left the vault");
            Assert.AreEqual(MarketListingStatus.Delisted, MarketManager.GetListing(listing.Id).Status,
                "the vault reported the withdraw under the representative guid, which closes the WHOLE group listing");
        }
    }
}
