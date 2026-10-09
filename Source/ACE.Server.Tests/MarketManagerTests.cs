using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Entity.AccountVault;
using ACE.Server.Managers;
using ACE.Server.Managers.Market;

namespace ACE.Server.Tests
{
    /// <summary>Market manager unit tests, run against fakes for the three seams: no database, no network, no live Player.</summary>
    [TestClass]
    public class MarketManagerTests
    {
        [TestMethod]
        public void ErrorCodes_MatchThePublishedContract()
        {
            // These strings are the API's error envelope and the in-game message table's key.
            var expected = new Dictionary<MarketError, string>
            {
                { MarketError.ListingNotActive,  "listing_not_active" },
                { MarketError.InsufficientFunds, "insufficient_funds" },
                { MarketError.VaultUnavailable,  "vault_unavailable" },
                { MarketError.VaultFull,         "vault_full" },
                { MarketError.RateLimited,       "rate_limited" },
                { MarketError.PriceChanged,      "price_changed" },
                { MarketError.NotOwner,          "not_owner" },
                { MarketError.BadCredentials,    "bad_credentials" },
                { MarketError.CountUnavailable,  "count_unavailable" },
                { MarketError.ItemNotFound,      "item_not_found" },
                { MarketError.InvalidPrice,      "invalid_price" },
                { MarketError.AlreadyListed,     "already_listed" },
                { MarketError.Timeout,           "timeout" },
                { MarketError.Disabled,          "disabled" },
                { MarketError.ServerError,       "server_error" },
                { MarketError.LedgerUnknown,     "ledger_unknown" },
                { MarketError.ItemListed,        "item_listed" },
                { MarketError.BarrelUnavailable, "barrel_unavailable" },
                { MarketError.ListingLimit,      "listing_limit" },
                { MarketError.OrderExists,       "order_exists" },
                { MarketError.OrderNotActive,    "order_not_active" },
                { MarketError.NoMatchingItems,   "no_matching_items" },
                { MarketError.InvalidMaterial,   "invalid_material" },
                { MarketError.BuyOrdersDisabled, "buy_orders_disabled" },
                { MarketError.OrderBusy,         "order_busy" },
                { MarketError.NoHammerForMaterial, "no_hammer_for_material" },
                { MarketError.SheetNotFound,     "sheet_not_found" },
                { MarketError.SheetsDisabled,    "sheets_disabled" },
                { MarketError.SheetBusy,         "sheet_busy" },
                { MarketError.NotAdmin,          "not_admin" },
                { MarketError.AdminDisabled,     "admin_disabled" },
                { MarketError.SettingNotFound,   "setting_not_found" },
                { MarketError.SettingSensitive,  "setting_sensitive" },
                { MarketError.InvalidSettingValue, "invalid_setting_value" },
                { MarketError.SettingChanged,    "setting_changed" },
                { MarketError.InvalidAnnouncement, "invalid_announcement" },
                { MarketError.CommandsDisabled,  "commands_disabled" },
                { MarketError.InvalidCommandText, "invalid_command_text" },
                { MarketError.UnknownCommand,    "unknown_command" },
                { MarketError.CommandNotPermitted, "command_not_permitted" },
                { MarketError.CommandInGameOnly, "command_in_game_only" },
                { MarketError.CommandBusy,       "command_busy" },
                { MarketError.NoCharacterOnline, "no_character_online" },
                { MarketError.CommandNotStarted, "command_not_started" },
                { MarketError.WorldEventsDisabled, "world_events_disabled" },
                { MarketError.WorldEventRunning, "world_event_running" },
                { MarketError.WorldEventNotRunning, "world_event_not_running" },
                { MarketError.WorldEventRunChanged, "world_event_run_changed" },
                { MarketError.InvalidLocation,   "invalid_location" },
                { MarketError.SourceNotWebStartable, "source_not_web_startable" },
                { MarketError.WorldEventRefused, "world_event_refused" },
                { MarketError.VaultLoading, "vault_loading" },
                { MarketError.SuitBuilderDisabled, "suit_builder_disabled" },
                { MarketError.CharacterOffline, "character_offline" },
                { MarketError.NotInVaultArea, "not_in_vault_area" },
                { MarketError.PackFull, "pack_full" },
                { MarketError.TransferInProgress, "transfer_in_progress" },
                { MarketError.TooManyItems, "too_many_items" },
                { MarketError.CharacterBusy, "character_busy" },
                { MarketError.VaultPanelOpen, "vault_panel_open" },
                { MarketError.InvalidTransfer, "invalid_transfer" },
                { MarketError.TransferNotFound, "transfer_not_found" },
                { MarketError.InPvp, "in_pvp" },
            };

            foreach (var kvp in expected)
                Assert.AreEqual(kvp.Value, MarketErrorCodes.ToCode(kvp.Key), $"wrong wire code for {kvp.Key}");

            // Without this, a future member silently ships as "server_error" to the web app.
            foreach (MarketError value in Enum.GetValues(typeof(MarketError)))
            {
                if (value == MarketError.None)
                    continue;

                Assert.IsTrue(expected.ContainsKey(value), $"MarketError.{value} has no published wire code");
            }
        }

        [TestMethod]
        public void ErrorCodes_AreUnique()
        {
            var codes = Enum.GetValues(typeof(MarketError))
                .Cast<MarketError>()
                .Where(e => e != MarketError.None)
                .Select(MarketErrorCodes.ToCode)
                .ToList();

            CollectionAssert.AllItemsAreUnique(codes);
        }

        private const uint SellerAccount = 7001;
        private const uint SellerCharacter = 0x50000101;
        private const uint OtherAccount = 7002;
        private const uint OtherCharacter = 0x50000102;

        private static readonly MarketActor Seller = new MarketActor(SellerAccount, SellerCharacter, "Marketseller");
        private static readonly MarketActor Other = new MarketActor(OtherAccount, OtherCharacter, "Somebodyelse");

        private FakeMarketRepository repo;
        private FakeMarketItemStore store;
        private FakeMarketWallet wallet;

        [TestInitialize]
        public void Setup()
        {
            repo = new FakeMarketRepository();
            store = new FakeMarketItemStore();
            wallet = new FakeMarketWallet();

            SeedMarketTunables();

            MarketManager.Initialize(store, wallet, repo);
        }

        /// <summary>
        /// Seeds PropertyManager's cache so an uncached Get does not fall through to
        /// DatabaseManager.ShardConfig, and doubles as an assertion that each key is registered.
        /// The price cap is seeded to the hard bound so it does not mask MaxPriceMmd's own tests.
        /// </summary>
        internal static void SeedMarketTunables()
        {
            VaultClassTestConfig.Seed();

            Assert.IsTrue(PropertyManager.ModifyBool("market_enabled", true),
                "market_enabled is missing from DefaultBooleanProperties");
            Assert.IsTrue(PropertyManager.ModifyBool("market_allow_free_listings", true),
                "market_allow_free_listings is missing from DefaultBooleanProperties");
            Assert.IsTrue(PropertyManager.ModifyBool("market_allow_class_listings", true),
                "market_allow_class_listings is missing from DefaultBooleanProperties");
            Assert.IsTrue(PropertyManager.ModifyBool("market_snapshot_backfill_on_start", true),
                "market_snapshot_backfill_on_start is missing from DefaultBooleanProperties");
            Assert.IsTrue(PropertyManager.ModifyLong("market_max_listings_per_account", 100),
                "market_max_listings_per_account is missing from DefaultLongProperties");
            Assert.IsTrue(PropertyManager.ModifyLong("market_max_price_mmd", MarketManager.MaxPriceMmd),
                "market_max_price_mmd is missing from DefaultLongProperties");
            Assert.IsTrue(PropertyManager.ModifyBool("market_buy_orders_enabled", true),
                "market_buy_orders_enabled is missing from DefaultBooleanProperties");
            Assert.IsTrue(PropertyManager.ModifyLong("market_buy_order_max_days", 30),
                "market_buy_order_max_days is missing from DefaultLongProperties");
            Assert.IsTrue(PropertyManager.ModifyLong("market_buy_order_max_count", 100),
                "market_buy_order_max_count is missing from DefaultLongProperties");
        }

        [TestCleanup]
        public void Teardown()
        {
            MarketManager.Shutdown();
        }

        private WorldObjects.WorldObject SeedStoredItem(uint accountId, uint wcid = 9001)
        {
            var item = FakeVaultWorld.MakeStack(wcid, 1, 1);
            store.SeedItem(accountId, item);
            return item;
        }

        [TestMethod]
        public void List_StoredItem_CreatesAnActiveListingAndBumpsTheSequence()
        {
            var item = SeedStoredItem(SellerAccount);
            var before = MarketManager.ChangeSequence;

            var result = MarketManager.List(Seller, item.Guid.Full, item.WeenieClassId, 1, 5, MarketChannel.InGame);

            Assert.IsTrue(result.Ok, $"expected a listing, got {result.Error}");
            Assert.AreEqual(MarketListingStatus.Active, result.Value.Status);
            Assert.AreEqual(5, result.Value.PriceMmd);
            Assert.AreEqual(SellerCharacter, result.Value.SellerCharacterGuid);
            Assert.IsTrue(MarketManager.ChangeSequence > before, "a listing must advance the change sequence");

            // The row must carry the partial-unique emulation, or the database cannot refuse a
            // double listing.
            var row = repo.Listings.Single();
            Assert.AreEqual(item.Guid.Full, row.ActiveItemGuid);
            Assert.IsNull(row.ActiveLedgerWcid);
            Assert.IsFalse(string.IsNullOrWhiteSpace(row.SnapshotJson), "snapshot_json is what search runs against");
        }

        [TestMethod]
        public void List_SameItemTwice_IsRefused()
        {
            var item = SeedStoredItem(SellerAccount);

            Assert.IsTrue(MarketManager.List(Seller, item.Guid.Full, item.WeenieClassId, 1, 5, MarketChannel.InGame).Ok);

            var second = MarketManager.List(Seller, item.Guid.Full, item.WeenieClassId, 1, 9, MarketChannel.InGame);

            Assert.IsFalse(second.Ok);
            Assert.AreEqual(MarketError.AlreadyListed, second.Error);
            Assert.AreEqual(1, repo.Listings.Count, "the refused listing must not have been written");
        }

        [TestMethod]
        public void List_SecondActiveLedgerListingForOneWcid_IsRefused()
        {
            store.SeedLedger(SellerAccount, 9500, 100);

            Assert.IsTrue(MarketManager.List(Seller, null, 9500, 10, 2, MarketChannel.InGame).Ok);

            var second = MarketManager.List(Seller, null, 9500, 5, 3, MarketChannel.InGame);

            Assert.IsFalse(second.Ok);
            Assert.AreEqual(MarketError.AlreadyListed, second.Error);
        }

        /// <summary>
        /// THE DEFECT. A player holding 16 equivalent salvage bags sees ONE vault row, so listing
        /// them one at a time is impossible: the second attempt collides with the first listing's
        /// active_Item_Guid and comes back already_listed, and the web offers only "Delist". A group
        /// row is N separate WHOLE biotas, and WithdrawGroup takes exactly N of them, so nothing is
        /// split by listing more than one.
        /// </summary>
        [TestMethod]
        public void List_AGroupRow_AcceptsACountAboveOne()
        {
            var group = store.SeedGroup(SellerAccount, 20980, 16);

            var result = MarketManager.List(Seller, group[0].Guid.Full, 20980, 5, 25, MarketChannel.Web);

            Assert.IsTrue(result.Ok, $"expected a group listing of 5, got {result.Error}");
            Assert.AreEqual(5, result.Value.Count);
            Assert.AreEqual(group[0].Guid.Full, result.Value.ItemGuid,
                "a group listing anchors on the representative member, which is what the row's guid is");
        }

        [TestMethod]
        public void List_AGroupRow_StillRefusesMoreMembersThanTheRowHolds()
        {
            var group = store.SeedGroup(SellerAccount, 20980, 3);

            var result = MarketManager.List(Seller, group[0].Guid.Full, 20980, 4, 25, MarketChannel.Web);

            Assert.IsFalse(result.Ok, "a group is bounded by its member count, not unbounded");
            Assert.AreEqual(MarketError.CountUnavailable, result.Error);
        }

        /// <summary>
        /// THE DISCRIMINATING CONTROL, and the fail-safe direction. Widening the count rule must not
        /// widen it for a LONE stored biota, which is indivisible however many units it holds. This
        /// case is chosen so the entry-count guard cannot pass for it: the stack holds 200, so
        /// `entry.Count &lt; count` is false at a count of 2 and only the kind check can refuse it.
        /// Without this test the group test above would merely restate the change.
        /// </summary>
        [TestMethod]
        public void List_ALoneStoredStack_StillRefusesACountAboveOne()
        {
            var item = FakeVaultWorld.MakeStack(9001, 200, 250);
            store.SeedItem(SellerAccount, item);

            var result = MarketManager.List(Seller, item.Guid.Full, 9001, 2, 25, MarketChannel.Web);

            Assert.IsFalse(result.Ok, "a lone stored biota cannot be split, whatever its stack size");
            Assert.AreEqual(MarketError.CountUnavailable, result.Error);

            // Control on the control: the same row really does list at 1, so the refusal above is
            // about the COUNT and not about the row being unlistable.
            Assert.IsTrue(MarketManager.List(Seller, item.Guid.Full, 9001, 1, 25, MarketChannel.Web).Ok);
        }

        [TestMethod]
        public void ListByEntryNumber_ZeroCountOnAGroupRow_ListsEveryMember()
        {
            var group = store.SeedGroup(SellerAccount, 20980, 4);

            var result = MarketManager.ListByEntryNumber(Seller, 1, 25, 0, MarketChannel.InGame);

            Assert.IsTrue(result.Ok, $"expected the whole group, got {result.Error}");
            Assert.AreEqual(4, result.Value.Count, "0 means the whole row for a group, as it does for a ledger");
            Assert.AreEqual(group[0].Guid.Full, result.Value.ItemGuid);
        }

        /// <summary>The control for the above: 0 on a LONE stored biota still means 1, never its stack size.</summary>
        [TestMethod]
        public void ListByEntryNumber_ZeroCountOnALoneStoredStack_StillMeansOne()
        {
            store.SeedItem(SellerAccount, FakeVaultWorld.MakeStack(9001, 200, 250));

            var result = MarketManager.ListByEntryNumber(Seller, 1, 25, 0, MarketChannel.InGame);

            Assert.IsTrue(result.Ok, $"expected a listing, got {result.Error}");
            Assert.AreEqual(1, result.Value.Count, "a stored stack is listed and sold as ONE thing");
        }

        [TestMethod]
        public void List_ItemTheAccountDoesNotHold_IsRefused()
        {
            var result = MarketManager.List(Seller, 0x7FFFFFFF, 9001, 1, 5, MarketChannel.InGame);

            Assert.IsFalse(result.Ok);
            Assert.AreEqual(MarketError.ItemNotFound, result.Error);
        }

        [TestMethod]
        public void List_PriceOfZero_IsAGiveawayAndIsAccepted()
        {
            var item = SeedStoredItem(SellerAccount);

            var result = MarketManager.List(Seller, item.Guid.Full, item.WeenieClassId, 1, 0, MarketChannel.InGame);

            Assert.IsTrue(result.Ok, "a price of zero is a giveaway, not a refusal");
            Assert.AreEqual(0, result.Value.PriceMmd);
            Assert.AreEqual(1, repo.Listings.Count);
        }

        [TestMethod]
        public void List_NegativePrice_IsRefused()
        {
            var item = SeedStoredItem(SellerAccount);

            var result = MarketManager.List(Seller, item.Guid.Full, item.WeenieClassId, 1, -1, MarketChannel.InGame);

            Assert.IsFalse(result.Ok);
            Assert.AreEqual(MarketError.InvalidPrice, result.Error);
            Assert.AreEqual(0, repo.Listings.Count, "a refused price must never reach the table");
        }

        [TestMethod]
        public void List_PriceOfZero_IsRefusedWhenFreeListingsAreDisabled()
        {
            Assert.IsTrue(PropertyManager.ModifyBool("market_allow_free_listings", false));

            var item = SeedStoredItem(SellerAccount);

            var result = MarketManager.List(Seller, item.Guid.Full, item.WeenieClassId, 1, 0, MarketChannel.InGame);

            Assert.IsFalse(result.Ok);
            Assert.AreEqual(MarketError.InvalidPrice, result.Error);
            Assert.AreEqual(0, repo.Listings.Count);
        }

        [TestMethod]
        public void List_AboveTheTunablePriceCap_IsRefused()
        {
            Assert.IsTrue(PropertyManager.ModifyLong("market_max_price_mmd", 50));

            var item = SeedStoredItem(SellerAccount);

            var result = MarketManager.List(Seller, item.Guid.Full, item.WeenieClassId, 1, 51, MarketChannel.InGame);

            Assert.IsFalse(result.Ok);
            Assert.AreEqual(MarketError.InvalidPrice, result.Error);
            Assert.AreEqual(0, repo.Listings.Count, "a refused price must never reach the table");
        }

        [TestMethod]
        public void List_AtTheTunableListingCap_IsRefused()
        {
            Assert.IsTrue(PropertyManager.ModifyLong("market_max_listings_per_account", 1));

            var first = SeedStoredItem(SellerAccount, 9001);
            var second = SeedStoredItem(SellerAccount, 9002);

            Assert.IsTrue(MarketManager.List(Seller, first.Guid.Full, 9001, 1, 5, MarketChannel.InGame).Ok);

            var refused = MarketManager.List(Seller, second.Guid.Full, 9002, 1, 5, MarketChannel.InGame);

            Assert.IsFalse(refused.Ok);

            // Changed from AlreadyListed: the cap refusal now carries its own code, distinct from
            // "this item is already up" - see MarketError.ListingLimit.
            Assert.AreEqual(MarketError.ListingLimit, refused.Error);
            Assert.AreEqual(1, repo.Listings.Count);
        }

        /// <summary>
        /// EffectiveListingCap is the tunable base cap alone when the account has bought no /market
        /// upgrades - AccountCapacityUpgradeManager reports 0 for an account it has never loaded, and a
        /// test that resets neither leaves the manager's own cache untouched.
        /// </summary>
        [TestMethod]
        public void EffectiveListingCap_WithNoUpgrades_EqualsTheTunableBaseCap()
        {
            Assert.IsTrue(PropertyManager.ModifyLong("market_max_listings_per_account", 100));

            AccountCapacityUpgradeManager.ResetForTesting(null);

            Assert.AreEqual(100, MarketManager.EffectiveListingCap(SellerAccount));
        }

        /// <summary>
        /// Each /market upgrade the account owns adds 10 to the base cap - CapacityUpgradePricing.ListingsPerUpgrade.
        /// </summary>
        [TestMethod]
        public void EffectiveListingCap_WithUpgrades_AddsTenEach()
        {
            Assert.IsTrue(PropertyManager.ModifyLong("market_max_listings_per_account", 100));

            var bank = new FakeAccountBankBackend();
            var storage = new FakeCapacityUpgradeStorage(bank);
            storage.Counts[(SellerAccount, ACE.Database.CapacityUpgradeKind.MarketListings)] = 3;

            AccountBankManager.ResetForTesting(bank);
            AccountCapacityUpgradeManager.ResetForTesting(storage);

            try
            {
                Assert.AreEqual(130, MarketManager.EffectiveListingCap(SellerAccount));
            }
            finally
            {
                AccountCapacityUpgradeManager.ResetForTesting(null);
                AccountBankManager.ResetForTesting(null);
            }
        }

        /// <summary>
        /// An account at the base cap can list one more once it has bought a /market upgrade - the
        /// enforcement site (List) must read the SAME EffectiveListingCap the command surface quotes,
        /// not the raw market_max_listings_per_account setting.
        /// </summary>
        [TestMethod]
        public void List_AtTheBaseCap_WithAnUpgradeBought_AllowsOneMore()
        {
            Assert.IsTrue(PropertyManager.ModifyLong("market_max_listings_per_account", 1));

            var bank = new FakeAccountBankBackend();
            var storage = new FakeCapacityUpgradeStorage(bank);
            storage.Counts[(SellerAccount, ACE.Database.CapacityUpgradeKind.MarketListings)] = 1;

            AccountBankManager.ResetForTesting(bank);
            AccountCapacityUpgradeManager.ResetForTesting(storage);

            try
            {
                var first = SeedStoredItem(SellerAccount, 9001);
                var second = SeedStoredItem(SellerAccount, 9002);

                Assert.IsTrue(MarketManager.List(Seller, first.Guid.Full, 9001, 1, 5, MarketChannel.InGame).Ok);
                Assert.IsTrue(MarketManager.List(Seller, second.Guid.Full, 9002, 1, 5, MarketChannel.InGame).Ok,
                    "base cap 1 + one upgrade's 10 leaves room for a second listing");
            }
            finally
            {
                AccountCapacityUpgradeManager.ResetForTesting(null);
                AccountBankManager.ResetForTesting(null);
            }
        }

        [TestMethod]
        public void List_WhenTheRuntimeKillSwitchIsOff_IsRefusedAsDisabled()
        {
            // market_enabled is the live half of the gate; Config.js's Market.Enabled only decides
            // whether the host started. Both must hold, so this alone must stop a listing.
            Assert.IsTrue(PropertyManager.ModifyBool("market_enabled", false));

            var item = SeedStoredItem(SellerAccount);

            var result = MarketManager.List(Seller, item.Guid.Full, item.WeenieClassId, 1, 5, MarketChannel.InGame);

            Assert.IsFalse(MarketManager.Enabled);
            Assert.IsFalse(result.Ok);
            Assert.AreEqual(MarketError.Disabled, result.Error);
            Assert.AreEqual(0, repo.Listings.Count);
        }

        [TestMethod]
        public void List_WhenTheVaultIsNotReady_ReturnsVaultUnavailableAndNeverEmpty()
        {
            var item = SeedStoredItem(SellerAccount);
            store.NotReadyAccounts.Add(SellerAccount);

            var result = MarketManager.List(Seller, item.Guid.Full, item.WeenieClassId, 1, 5, MarketChannel.InGame);

            Assert.IsFalse(result.Ok);
            Assert.AreEqual(MarketError.VaultUnavailable, result.Error,
                "a store that cannot be read must never be reported as an empty one");
        }

        [TestMethod]
        public void Delist_ByTheOwner_ClosesTheListingAndClearsTheActiveKeys()
        {
            var item = SeedStoredItem(SellerAccount);
            var listing = MarketManager.List(Seller, item.Guid.Full, item.WeenieClassId, 1, 5, MarketChannel.InGame).Value;

            var result = MarketManager.Delist(Seller, listing.Id, MarketChannel.InGame);

            Assert.IsTrue(result.Ok);
            Assert.AreEqual(MarketListingStatus.Delisted, result.Value.Status);
            Assert.IsNotNull(result.Value.ClosedAt);

            var row = repo.Listings.Single(l => l.Id == listing.Id);
            Assert.IsNull(row.ActiveItemGuid, "a closed listing must free its unique key");
            Assert.AreEqual((int)MarketListingStatus.Delisted, row.Status);

            // Freeing the key is what lets the same item be listed again.
            Assert.IsTrue(MarketManager.List(Seller, item.Guid.Full, item.WeenieClassId, 1, 7, MarketChannel.InGame).Ok);
        }

        [TestMethod]
        public void Delist_ByAnyoneElse_IsRefused()
        {
            var item = SeedStoredItem(SellerAccount);
            var listing = MarketManager.List(Seller, item.Guid.Full, item.WeenieClassId, 1, 5, MarketChannel.InGame).Value;

            var result = MarketManager.Delist(Other, listing.Id, MarketChannel.InGame);

            Assert.IsFalse(result.Ok);
            Assert.AreEqual(MarketError.NotOwner, result.Error);
            Assert.AreEqual((int)MarketListingStatus.Active, repo.Listings.Single().Status);
        }

        [TestMethod]
        public void Feed_ReturnsOnlyChangesAfterTheCursorAndPagesAtMaxFeedRows()
        {
            var seq0 = MarketManager.ChangeSequence;

            var first = MarketManager.List(Seller, SeedStoredItem(SellerAccount, 9101).Guid.Full, 9101, 1, 1, MarketChannel.InGame).Value;
            var afterFirst = MarketManager.ChangeSequence;
            var second = MarketManager.List(Seller, SeedStoredItem(SellerAccount, 9102).Guid.Full, 9102, 1, 2, MarketChannel.InGame).Value;

            var all = MarketManager.GetListingChanges(seq0, MarketManager.MaxFeedRows, out var nextAll);
            Assert.AreEqual(2, all.Count);
            Assert.AreEqual(MarketManager.ChangeSequence, nextAll);

            var tail = MarketManager.GetListingChanges(afterFirst, MarketManager.MaxFeedRows, out _);
            Assert.AreEqual(1, tail.Count);
            Assert.AreEqual(second.Id, tail[0].Id);

            // A page smaller than the backlog must advance the cursor to the LAST ROW RETURNED, not
            // to the head, or the client silently skips everything it did not receive.
            var page = MarketManager.GetListingChanges(seq0, 1, out var nextPage);
            Assert.AreEqual(1, page.Count);
            Assert.AreEqual(first.Id, page[0].Id);
            Assert.AreEqual(page[0].Seq, nextPage);
        }

        [TestMethod]
        public void Feed_ReportsADelistAsAChange()
        {
            var item = SeedStoredItem(SellerAccount);
            var listing = MarketManager.List(Seller, item.Guid.Full, item.WeenieClassId, 1, 5, MarketChannel.InGame).Value;
            var afterList = MarketManager.ChangeSequence;

            MarketManager.Delist(Seller, listing.Id, MarketChannel.InGame);

            var changes = MarketManager.GetListingChanges(afterList, MarketManager.MaxFeedRows, out _);

            Assert.AreEqual(1, changes.Count);
            Assert.AreEqual(MarketListingStatus.Delisted, changes[0].Status,
                "the feed must carry the close, not just the create, or a web index never removes it");
        }

        [TestMethod]
        public void OnVaultWithdraw_AutoDelistsTheListingForThatItem()
        {
            var item = SeedStoredItem(SellerAccount);
            var listing = MarketManager.List(Seller, item.Guid.Full, item.WeenieClassId, 1, 5, MarketChannel.InGame).Value;

            MarketManager.OnVaultWithdraw(SellerAccount, item.Guid.Full, item.WeenieClassId, 1, null);

            Assert.AreEqual(MarketListingStatus.Delisted, MarketManager.GetListing(listing.Id).Status);
        }

        [TestMethod]
        public void OnVaultWithdraw_DrawingALedgerStackBelowItsListedCount_Delists()
        {
            store.SeedLedger(SellerAccount, 9500, 100);
            var listing = MarketManager.List(Seller, null, 9500, 60, 2, MarketChannel.InGame).Value;

            // 100 held, 60 listed. Taking 30 leaves 70, which still covers the listing.
            MarketManager.OnVaultWithdraw(SellerAccount, null, 9500, 30, null);
            Assert.AreEqual(MarketListingStatus.Active, MarketManager.GetListing(listing.Id).Status);

            // Taking 50 more would leave 20, below the listed 60.
            MarketManager.OnVaultWithdraw(SellerAccount, null, 9500, 50, null);
            Assert.AreEqual(MarketListingStatus.Delisted, MarketManager.GetListing(listing.Id).Status);
        }

        [TestMethod]
        public void Invalidate_MarksListingsWhoseItemLeftTheVault()
        {
            var item = SeedStoredItem(SellerAccount);
            var listing = MarketManager.List(Seller, item.Guid.Full, item.WeenieClassId, 1, 5, MarketChannel.InGame).Value;

            // The item disappears without the hook firing - the case a restart or an out-of-band
            // removal produces, and the reason invalidation exists at all.
            store.Items[SellerAccount].Clear();

            MarketManager.InvalidateForAccount(SellerAccount);

            Assert.AreEqual(MarketListingStatus.Invalidated, MarketManager.GetListing(listing.Id).Status);
        }

        [TestMethod]
        public void FindInvalidationCandidates_ListingStillHeld_IsNotACandidate()
        {
            var item = SeedStoredItem(SellerAccount);
            var listing = MarketManager.List(Seller, item.Guid.Full, item.WeenieClassId, 1, 5, MarketChannel.InGame).Value;

            var found = MarketManager.FindInvalidationCandidates(SellerAccount);

            Assert.IsFalse(found.StoreNotReady);
            Assert.AreEqual(0, found.Listings.Count, "the item is still in the vault, so nothing is a candidate");

            // Break the guarded logic to prove this test discriminates: with the backing gone, the same
            // listing becomes a candidate.
            store.Items[SellerAccount].Clear();
            found = MarketManager.FindInvalidationCandidates(SellerAccount);
            Assert.AreEqual(1, found.Listings.Count);
            Assert.AreEqual(listing.Id, found.Listings[0].Id);
        }

        [TestMethod]
        public void FindInvalidationCandidates_BackingGone_IsACandidate()
        {
            var item = SeedStoredItem(SellerAccount);
            var listing = MarketManager.List(Seller, item.Guid.Full, item.WeenieClassId, 1, 5, MarketChannel.InGame).Value;

            store.Items[SellerAccount].Clear();

            var found = MarketManager.FindInvalidationCandidates(SellerAccount);

            Assert.IsFalse(found.StoreNotReady);
            Assert.AreEqual(1, found.Listings.Count);
            Assert.AreEqual(listing.Id, found.Listings[0].Id);
        }

        [TestMethod]
        public void FindInvalidationCandidates_StoreNotReady_ReportsSkippedWithNoCandidates()
        {
            var item = SeedStoredItem(SellerAccount);
            MarketManager.List(Seller, item.Guid.Full, item.WeenieClassId, 1, 5, MarketChannel.InGame);

            // Not ready is a different fact from "holds nothing" - the item is gone from the store's
            // view too (NotReadyAccounts short-circuits GetEntries to empty), but the answer must be
            // "skipped", never "0 candidates found".
            store.NotReadyAccounts.Add(SellerAccount);

            var found = MarketManager.FindInvalidationCandidates(SellerAccount);

            Assert.IsTrue(found.StoreNotReady);
            Assert.AreEqual(0, found.Listings.Count);
        }

        [TestMethod]
        public void DryRun_NeverClosesAnything()
        {
            var item = SeedStoredItem(SellerAccount);
            var listing = MarketManager.List(Seller, item.Guid.Full, item.WeenieClassId, 1, 5, MarketChannel.InGame).Value;

            store.Items[SellerAccount].Clear();

            // The dry run: compute candidates and stop. Never call ApplyInvalidation.
            var found = MarketManager.FindInvalidationCandidates(SellerAccount);
            Assert.AreEqual(1, found.Listings.Count);

            Assert.AreEqual(MarketListingStatus.Active, MarketManager.GetListing(listing.Id).Status,
                "computing candidates must never close a listing");
        }

        [TestMethod]
        public void ApplyInvalidation_ClosesExactlyTheGivenCandidates()
        {
            var item = SeedStoredItem(SellerAccount);
            var listing = MarketManager.List(Seller, item.Guid.Full, item.WeenieClassId, 1, 5, MarketChannel.InGame).Value;

            var other = SeedStoredItem(SellerAccount, 9002);
            var otherListing = MarketManager.List(Seller, other.Guid.Full, other.WeenieClassId, 1, 5, MarketChannel.InGame).Value;

            store.Items[SellerAccount].RemoveAll(i => i.Guid.Full == item.Guid.Full);

            var found = MarketManager.FindInvalidationCandidates(SellerAccount);
            Assert.AreEqual(1, found.Listings.Count);

            MarketManager.ApplyInvalidation(SellerAccount, found.Listings);

            Assert.AreEqual(MarketListingStatus.Invalidated, MarketManager.GetListing(listing.Id).Status);
            Assert.AreEqual(MarketListingStatus.Active, MarketManager.GetListing(otherListing.Id).Status,
                "a listing not named in the candidate list must be left alone");
        }

        [TestMethod]
        public void ActiveListingSellerAccountIds_ReturnsDistinctSellersWithAnActiveListing()
        {
            var item1 = SeedStoredItem(SellerAccount, 9001);
            MarketManager.List(Seller, item1.Guid.Full, 9001, 1, 5, MarketChannel.InGame);

            var item2 = SeedStoredItem(SellerAccount, 9002);
            var second = MarketManager.List(Seller, item2.Guid.Full, 9002, 1, 5, MarketChannel.InGame).Value;

            var item3 = SeedStoredItem(OtherAccount, 9003);
            MarketManager.List(Other, item3.Guid.Full, 9003, 1, 5, MarketChannel.InGame);

            // Close one of SellerAccount's two listings; SellerAccount must still appear once, not twice.
            MarketManager.ApplyInvalidation(SellerAccount, new List<MarketListing> { second });

            var ids = MarketManager.ActiveListingSellerAccountIds();

            CollectionAssert.AreEquivalent(new[] { SellerAccount, OtherAccount }, ids.ToList());
        }

        [TestMethod]
        public void Initialize_RebuildsTheIndexFromTheDatabase()
        {
            var item = SeedStoredItem(SellerAccount);
            var listing = MarketManager.List(Seller, item.Guid.Full, item.WeenieClassId, 1, 5, MarketChannel.InGame).Value;

            // A fresh manager over the SAME repository is a restart.
            MarketManager.Shutdown();
            MarketManager.Initialize(store, wallet, repo);

            var rebuilt = MarketManager.GetListing(listing.Id);

            Assert.IsNotNull(rebuilt, "the index must be rebuilt from market_listing at boot");
            Assert.AreEqual(MarketListingStatus.Active, rebuilt.Status);
            Assert.AreEqual(5, rebuilt.PriceMmd);
            Assert.IsTrue(rebuilt.Seq > 0, "every rebuilt listing needs a feed sequence");
        }

        [TestMethod]
        public void Initialize_WhenTheListingReadFails_DisablesTheMarketRatherThanServingAnEmptyOne()
        {
            repo.FailListingRead = true;

            MarketManager.Shutdown();
            MarketManager.Initialize(store, wallet, repo);

            Assert.IsFalse(MarketManager.Enabled,
                "a failed read is not an empty market: serving one would let sellers re-list items that are already listed");
        }

        [TestMethod]
        public void Search_MatchesOnNameAndIsCappedAtMaxSearchRows()
        {
            for (var i = 0; i < MarketManager.MaxSearchRows + 5; i++)
            {
                var item = SeedStoredItem(SellerAccount, (uint)(9200 + i));
                MarketManager.List(Seller, item.Guid.Full, item.WeenieClassId, 1, 1, MarketChannel.InGame);
            }

            // FakeVaultWorld.MakeStack names every item "Test Item <wcid>".
            var hits = MarketManager.SearchByName("test item", MarketManager.MaxSearchRows);

            Assert.AreEqual(MarketManager.MaxSearchRows, hits.Count);
            Assert.AreEqual(0, MarketManager.SearchByName("no such thing anywhere", MarketManager.MaxSearchRows).Count);
        }

        [TestMethod]
        public void PreWithdrawHook_IsInvokedByTheVaultStoreItself()
        {
            // A static delegate, not a direct MarketManager call: AccountVaultStore must stay free of
            // every type outside its own namespace. This proves the seam carries the four values;
            // OnVaultWithdraw_* prove what the manager does with them.
            var seen = new List<(uint account, uint? guid, uint wcid, int amount)>();
            var previous = AccountVaultStore.PreWithdrawHook;

            try
            {
                AccountVaultStore.PreWithdrawHook = (account, guid, wcid, amount, classDisplayId)
                    => seen.Add((account, guid, wcid, amount));

                var backend = new FakeVaultBackend();
                var world = new FakeVaultWorld();
                var vault = new AccountVaultStore(SellerAccount, backend, world);
                var actor = new VaultActor(SellerAccount, SellerCharacter, "Marketseller");

                backend.Stacks.Add(new ACE.Database.Models.Shard.AccountVaultStack
                {
                    Id = 1,
                    AccountId = SellerAccount,
                    Wcid = 9500,
                    Count = 100,
                });

                var entry = VaultEntry.ForLedger(9500, 100);
                var ok = false;

                vault.Enqueue(() => ok = vault.TryWithdraw(entry, 40, actor, out _, out _));

                Assert.IsTrue(ok, "the withdraw itself must still succeed");
                Assert.AreEqual(1, seen.Count, "the vault store must call the pre-withdraw hook exactly once");
                Assert.AreEqual(SellerAccount, seen[0].account);
                Assert.IsNull(seen[0].guid, "a ledger withdraw carries no item guid");
                Assert.AreEqual(9500u, seen[0].wcid);
                Assert.AreEqual(40, seen[0].amount);
            }
            finally
            {
                AccountVaultStore.PreWithdrawHook = previous;
            }
        }
    }
}
