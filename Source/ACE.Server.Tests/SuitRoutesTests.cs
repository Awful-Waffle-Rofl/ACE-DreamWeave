using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.DatLoader;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Managers;
using ACE.Server.Managers.CharacterSheets;
using ACE.Server.Managers.Market;
using ACE.Server.Managers.Market.Suit;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// GET /v1/accounts/me/suit/inventory and GET /v1/accounts/me/characters/{guid}/suit-profile, over a
    /// real loopback Kestrel with fakes behind every seam (the MarketApiTests shape), plus the owner-view
    /// service path and the equipped filter.
    /// </summary>
    [TestClass]
    public class SuitRoutesTests
    {
        private const string Key = "suit-test-key";
        private const uint AccountId = 9301;
        private const uint CharacterGuid = 0x50000501;
        private const uint StrangerCharacter = 0x50000502;

        private const uint ArmorWcid = 93001;
        private const uint SwordWcid = 93002;
        private const uint ShieldWcid = 93003;
        private const uint RingWcid = 93004;
        private const uint SalvageWcid = 93005;
        private const uint ClassRingWcid = 93006;
        private const uint ClassSalvageWcid = 93007;

        private static int nextGuid = 0x7F700000;

        private FakeMarketRepository repo;
        private FakeMarketItemStore store;
        private FakeMarketWallet wallet;
        private FakeCharacterSheetService sheets;
        private HttpClient client;
        private bool suitEnabled;
        private bool characterMoves;

        private IClothingIconSource savedClothingIcons;
        private Func<uint, Weenie> savedWeenieLookup;

        [TestInitialize]
        public void Setup()
        {
            savedClothingIcons = MarketSnapshot.ClothingIcons;
            savedWeenieLookup = MarketManager.WeenieLookup;
            MarketSnapshot.ClothingIcons = null;

            repo = new FakeMarketRepository();
            store = new FakeMarketItemStore();
            wallet = new FakeMarketWallet();
            sheets = new FakeCharacterSheetService();
            suitEnabled = true;
            characterMoves = true;

            MarketManagerTests.SeedMarketTunables();

            MarketManager.WeenieLookup = wcid => wcid switch
            {
                RingWcid => NewWeenie(RingWcid, "Ledger Ring", ItemType.Jewelry, EquipMask.FingerWearLeft | EquipMask.FingerWearRight),
                ClassRingWcid => NewWeenie(ClassRingWcid, "Class Ring", ItemType.Jewelry, EquipMask.FingerWearLeft | EquipMask.FingerWearRight),
                SalvageWcid => NewWeenie(SalvageWcid, "Ledger Salvage", ItemType.TinkeringMaterial, EquipMask.None),
                ClassSalvageWcid => NewWeenie(ClassSalvageWcid, "Class Salvage", ItemType.TinkeringMaterial, EquipMask.None),
                _ => null,
            };

            MarketManager.Initialize(store, wallet, repo);

            MarketApiHost.Start(new MarketApiOptions
            {
                ListenUrl = "http://127.0.0.1:0",
                SharedKey = Key,
                MaxInFlight = 8,
                WriteRatePerMinute = 20,
                RequestTimeoutMs = 3000,
                Wallet = wallet,
                CharacterSheets = sheets,
                AuthenticateAccount = (name, password) => name == "tester" && password == "pw" ? AccountId : 0u,
                ListCharacters = accountId => accountId == AccountId
                    ? new List<(uint, string)> { (CharacterGuid, "Suitwearer") }
                    : new List<(uint, string)>(),
                SuitBuilderEnabled = () => suitEnabled,
                SuitCharacterMovesEnabled = () => characterMoves,
            });

            client = new HttpClient { BaseAddress = new Uri(MarketApiHost.ListeningUrl) };
        }

        [TestCleanup]
        public void Teardown()
        {
            client?.Dispose();
            MarketApiHost.Stop();
            MarketManager.Shutdown();
            MarketManager.WeenieLookup = savedWeenieLookup;
            MarketSnapshot.ClothingIcons = savedClothingIcons;
            PropertyManager.ModifyBool("market_enabled", true);
        }

        // ---- fixtures ----

        private static Weenie NewWeenie(uint wcid, string name, ItemType type, EquipMask locations)
        {
            var ints = new Dictionary<PropertyInt, int>
            {
                { PropertyInt.ItemType, (int)type },
                { PropertyInt.EncumbranceVal, 50 },
            };

            if (locations != EquipMask.None)
                ints[PropertyInt.ValidLocations] = unchecked((int)(uint)locations);

            return new Weenie
            {
                WeenieClassId = wcid,
                WeenieType = WeenieType.Clothing,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, name } },
                PropertiesInt = ints,
            };
        }

        private static WorldObject Item(uint wcid, string name, ItemType type, EquipMask locations)
            => new Clothing(NewWeenie(wcid, name, type, locations), new ObjectGuid((uint)nextGuid++));

        private static WorldObject Armor(string name = "Breastplate") => Item(ArmorWcid, name, ItemType.Armor, EquipMask.ChestArmor);

        private HttpRequestMessage Get(string path, string key = Key, string bearer = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, path);

            if (key != null)
                request.Headers.Add("X-Market-Key", key);

            if (bearer != null)
                request.Headers.Add("Authorization", $"Bearer {bearer}");

            return request;
        }

        private async Task<(HttpStatusCode status, JsonElement json)> Send(HttpRequestMessage request)
        {
            using var response = await client.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();

            if (string.IsNullOrWhiteSpace(text))
                return (response.StatusCode, default);

            using var doc = JsonDocument.Parse(text);
            return (response.StatusCode, doc.RootElement.Clone());
        }

        private async Task<string> LoginAsync()
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/v1/session") { Content = new StringContent("{\"account_name\":\"tester\",\"password\":\"pw\"}", Encoding.UTF8, "application/json") };
            request.Headers.Add("X-Market-Key", Key);

            var (status, json) = await Send(request);
            Assert.AreEqual(HttpStatusCode.OK, status);
            return json.GetProperty("session_token").GetString();
        }

        private async Task<(HttpStatusCode status, JsonElement json)> Inventory()
            => await Send(Get("/v1/accounts/me/suit/inventory", bearer: await LoginAsync()));

        private async Task<(HttpStatusCode status, JsonElement json)> Profile(uint guid)
            => await Send(Get($"/v1/accounts/me/characters/{guid}/suit-profile", bearer: await LoginAsync()));

        private static string[] Names(JsonElement json)
            => json.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        // ---- GET /v1/accounts/me/suit/inventory ----

        /// <summary>character_moves mirrors the suit_character_moves_enabled switch, so the client knows whether it may send holder_guid.</summary>
        [TestMethod]
        public async Task Inventory_ReportsCharacterMoves_FromTheKillSwitch()
        {
            store.SeedItem(AccountId, Armor());

            var (onStatus, on) = await Inventory();
            Assert.AreEqual(HttpStatusCode.OK, onStatus);
            Assert.IsTrue(on.GetProperty("character_moves").GetBoolean());

            characterMoves = false;

            var (offStatus, off) = await Inventory();
            Assert.AreEqual(HttpStatusCode.OK, offStatus);
            Assert.IsFalse(off.GetProperty("character_moves").GetBoolean(), "present and false, never omitted");
            Assert.AreEqual(1, off.GetProperty("items").GetArrayLength(), "the vault listing itself is unaffected");
        }

        [TestMethod]
        public async Task Inventory_NoKey_Is401Empty()
        {
            using var r = await client.SendAsync(Get("/v1/accounts/me/suit/inventory", key: null));
            Assert.AreEqual(HttpStatusCode.Unauthorized, r.StatusCode);
            Assert.AreEqual(0, (await r.Content.ReadAsByteArrayAsync()).Length);
        }

        [TestMethod]
        public async Task Inventory_NoSession_Is401BadCredentials()
        {
            var (status, json) = await Send(Get("/v1/accounts/me/suit/inventory"));
            Assert.AreEqual(HttpStatusCode.Unauthorized, status);
            Assert.AreEqual("bad_credentials", json.GetProperty("error").GetString());
        }

        [TestMethod]
        public async Task Inventory_ColdVault_AnswersVaultLoading503_NotAnEmptyList()
        {
            store.SeedItem(AccountId, Armor());
            store.NotReadyAccounts.Add(AccountId);

            var (status, json) = await Inventory();

            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
            Assert.AreEqual("vault_loading", json.GetProperty("error").GetString());
            Assert.IsFalse(json.TryGetProperty("items", out _), "a not-ready vault must never read as an empty one");
        }

        [TestMethod]
        public async Task Inventory_ProjectsStoredItemsAsSuitItems()
        {
            var armor = Armor("Breastplate of Testing");
            store.SeedItem(AccountId, armor);

            var (status, json) = await Inventory();

            Assert.AreEqual(HttpStatusCode.OK, status);
            var item = json.GetProperty("items").EnumerateArray().Single();
            Assert.AreEqual("vault", item.GetProperty("source").GetString());
            Assert.AreEqual(armor.Guid.Full, item.GetProperty("item_guid").GetUInt32());
            Assert.AreEqual(ArmorWcid, item.GetProperty("wcid").GetUInt32());
            Assert.AreEqual("Breastplate of Testing", item.GetProperty("name").GetString());
            Assert.AreEqual((int)EquipMask.ChestArmor, item.GetProperty("valid_locations").GetInt32());
            Assert.AreEqual(1, item.GetProperty("count").GetInt32());
            Assert.IsFalse(item.GetProperty("listed").GetBoolean());
            Assert.IsFalse(item.TryGetProperty("class_key", out _));
        }

        [TestMethod]
        public async Task Inventory_FiltersOutEntriesThatAreNotSuitRelevant()
        {
            store.SeedItem(AccountId, Armor("Breastplate"));
            store.SeedItem(AccountId, Item(SwordWcid, "Sword", ItemType.MeleeWeapon, EquipMask.MeleeWeapon));
            store.SeedItem(AccountId, Item(ShieldWcid, "Shield", ItemType.Armor, EquipMask.Shield));
            store.SeedLedger(AccountId, RingWcid, 4);
            store.SeedLedger(AccountId, SalvageWcid, 900);
            store.SeedClassLine(AccountId, "aaaa1111bbbb2222cccc3333dddd4444", ClassRingWcid, 7);
            store.SeedClassLine(AccountId, "eeee5555ffff6666aaaa7777bbbb8888", ClassSalvageWcid, 30);

            var (status, json) = await Inventory();

            Assert.AreEqual(HttpStatusCode.OK, status);
            CollectionAssert.AreEqual(new[] { "Breastplate", "Class Ring", "Ledger Ring" }, Names(json));
        }

        [TestMethod]
        public async Task Inventory_LedgerAndClassRows_CarryCountAndClassKey()
        {
            const string classId = "aaaa1111bbbb2222cccc3333dddd4444";
            store.SeedLedger(AccountId, RingWcid, 4);
            store.SeedClassLine(AccountId, classId, ClassRingWcid, 7);

            var (_, json) = await Inventory();
            var items = json.GetProperty("items").EnumerateArray().ToList();

            var ledger = items.Single(i => i.GetProperty("name").GetString() == "Ledger Ring");
            Assert.AreEqual(4, ledger.GetProperty("count").GetInt32());
            Assert.IsFalse(ledger.TryGetProperty("item_guid", out _), "a ledger row has no biota");
            Assert.IsFalse(ledger.TryGetProperty("class_key", out _));

            var line = items.Single(i => i.GetProperty("name").GetString() == "Class Ring");
            Assert.AreEqual(7, line.GetProperty("count").GetInt32());
            Assert.AreEqual(classId, line.GetProperty("class_key").GetString());
        }

        [TestMethod]
        public async Task Inventory_ListedFlag_FollowsTheMarketsListing()
        {
            var armor = Armor("Listed Breastplate");
            var other = Item(ArmorWcid, "Unlisted Helm", ItemType.Armor, EquipMask.HeadWear);
            store.SeedItem(AccountId, armor);
            store.SeedItem(AccountId, other);

            var seller = new MarketActor(AccountId, CharacterGuid, "Suitwearer");
            Assert.IsTrue(MarketManager.List(seller, armor.Guid.Full, ArmorWcid, 1, 500, MarketChannel.Web).Ok);

            var (_, json) = await Inventory();
            var items = json.GetProperty("items").EnumerateArray().ToList();

            Assert.IsTrue(items.Single(i => i.GetProperty("name").GetString() == "Listed Breastplate").GetProperty("listed").GetBoolean());
            Assert.IsFalse(items.Single(i => i.GetProperty("name").GetString() == "Unlisted Helm").GetProperty("listed").GetBoolean());
        }

        [TestMethod]
        public async Task Inventory_GroupListing_MarksEveryMemberListed()
        {
            var group = Enumerable.Range(0, 3).Select(_ => Armor("Grouped Breastplate")).ToList();
            store.Groups[AccountId] = new List<List<WorldObject>> { group };

            var seller = new MarketActor(AccountId, CharacterGuid, "Suitwearer");
            Assert.IsTrue(MarketManager.List(seller, group[0].Guid.Full, ArmorWcid, 2, 500, MarketChannel.Web).Ok);

            var (_, json) = await Inventory();
            var items = json.GetProperty("items").EnumerateArray().ToList();

            Assert.AreEqual(3, items.Count, "each member is its own line");
            CollectionAssert.AreEqual(new[] { true, true, true }, items.Select(i => i.GetProperty("listed").GetBoolean()).ToArray(),
                "a listing over a group is closed by ANY withdraw from it, so every member is listed");
        }

        [TestMethod]
        public void GroupListing_AnyWithdrawFromTheGroup_ClosesTheWholeListing()
        {
            // The fact the all-members-listed rule rests on: the vault names the REPRESENTATIVE guid on
            // every withdraw from a group (the pre-withdraw hook call in AccountVaultStore), and the real
            // hook delists the whole listing even though it covers only 2 of 3 members.
            var group = Enumerable.Range(0, 3).Select(_ => Armor("Grouped Breastplate")).ToList();
            store.Groups[AccountId] = new List<List<WorldObject>> { group };

            var seller = new MarketActor(AccountId, CharacterGuid, "Suitwearer");
            var listing = MarketManager.List(seller, group[0].Guid.Full, ArmorWcid, 2, 500, MarketChannel.Web).Value;
            Assert.AreEqual(MarketListingStatus.Active, MarketManager.GetListing(listing.Id).Status);

            MarketManager.OnVaultWithdraw(AccountId, group[0].Guid.Full, ArmorWcid, 1, null);

            Assert.AreEqual(MarketListingStatus.Delisted, MarketManager.GetListing(listing.Id).Status);
        }

        [TestMethod]
        public async Task Inventory_PartlyListedLedgerRow_SplitsIntoFreeAndListedParts()
        {
            store.SeedLedger(AccountId, RingWcid, 4);

            var seller = new MarketActor(AccountId, CharacterGuid, "Suitwearer");
            var listing = MarketManager.List(seller, null, RingWcid, 2, 500, MarketChannel.Web).Value;

            var (_, json) = await Inventory();
            var items = json.GetProperty("items").EnumerateArray().ToList();

            Assert.AreEqual(2, items.Count);
            var free = items.Single(i => !i.GetProperty("listed").GetBoolean());
            var listed = items.Single(i => i.GetProperty("listed").GetBoolean());
            Assert.AreEqual(2, free.GetProperty("count").GetInt32());
            Assert.AreEqual(2, listed.GetProperty("count").GetInt32());

            // The split mirrors the keep-alive rule of the hook: withdrawing the free 2 leaves the
            // listing up, withdrawing 3 would leave 1 < 2 and closes it.
            MarketManager.OnVaultWithdraw(AccountId, null, RingWcid, 2, null);
            Assert.AreEqual(MarketListingStatus.Active, MarketManager.GetListing(listing.Id).Status);
            MarketManager.OnVaultWithdraw(AccountId, null, RingWcid, 3, null);
            Assert.AreEqual(MarketListingStatus.Delisted, MarketManager.GetListing(listing.Id).Status);
        }

        [TestMethod]
        public async Task Inventory_FullyListedLedgerRow_IsOneListedItem()
        {
            store.SeedLedger(AccountId, RingWcid, 4);

            var seller = new MarketActor(AccountId, CharacterGuid, "Suitwearer");
            Assert.IsTrue(MarketManager.List(seller, null, RingWcid, 4, 500, MarketChannel.Web).Ok);

            var (_, json) = await Inventory();
            var item = json.GetProperty("items").EnumerateArray().Single();

            Assert.IsTrue(item.GetProperty("listed").GetBoolean());
            Assert.AreEqual(4, item.GetProperty("count").GetInt32());
        }

        [TestMethod]
        public async Task Inventory_PartlyListedClassLine_SplitsToo()
        {
            const string classId = "aaaa1111bbbb2222cccc3333dddd4444";
            store.SeedClassLine(AccountId, classId, ClassRingWcid, 7);

            var seller = new MarketActor(AccountId, CharacterGuid, "Suitwearer");
            Assert.IsTrue(MarketManager.List(seller, null, 0, 3, 500, MarketChannel.Web, classId).Ok);

            var (_, json) = await Inventory();
            var items = json.GetProperty("items").EnumerateArray().ToList();

            Assert.AreEqual(2, items.Count);
            Assert.AreEqual(4, items.Single(i => !i.GetProperty("listed").GetBoolean()).GetProperty("count").GetInt32());
            Assert.AreEqual(3, items.Single(i => i.GetProperty("listed").GetBoolean()).GetProperty("count").GetInt32());
            Assert.IsTrue(items.All(i => i.GetProperty("class_key").GetString() == classId));
        }

        [TestMethod]
        public async Task Inventory_MarketDisabled_SuitBuilderEnabled_StillServes()
        {
            store.SeedItem(AccountId, Armor());
            Assert.IsTrue(PropertyManager.ModifyBool("market_enabled", false));
            Assert.IsFalse(MarketManager.Enabled, "precondition: the trading switch is off");

            var (status, json) = await Inventory();

            Assert.AreEqual(HttpStatusCode.OK, status);
            Assert.AreEqual(1, json.GetProperty("items").GetArrayLength());
        }

        [TestMethod]
        public async Task Inventory_SuitBuilderOff_IsRefused503_AndNothingIsRead()
        {
            store.SeedItem(AccountId, Armor());
            suitEnabled = false;

            var (status, json) = await Inventory();

            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
            Assert.AreEqual("suit_builder_disabled", json.GetProperty("error").GetString());
        }

        [TestMethod]
        public async Task Inventory_MarketEnabled_SuitBuilderOff_StillRefused()
        {
            suitEnabled = false;
            Assert.IsTrue(MarketManager.Enabled);

            var (status, _) = await Inventory();
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
        }

        [TestMethod]
        public async Task Inventory_UnwiredSwitch_ReadsAsOff()
        {
            MarketApiHost.Stop();
            MarketApiHost.Start(new MarketApiOptions
            {
                ListenUrl = "http://127.0.0.1:0",
                SharedKey = Key,
                Wallet = wallet,
                AuthenticateAccount = (name, password) => AccountId,
                ListCharacters = _ => new List<(uint, string)>(),
            });
            client.Dispose();
            client = new HttpClient { BaseAddress = new Uri(MarketApiHost.ListeningUrl) };

            var (status, json) = await Inventory();

            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
            Assert.AreEqual("suit_builder_disabled", json.GetProperty("error").GetString());
        }

        // ---- GET /v1/accounts/me/characters/{guid}/suit/shared-inventory ----

        private const uint OwnerA = 9311;
        private const uint OwnerB = 9312;
        private const uint GranterA = 0x50000511;
        private const uint OwnerAOtherCharacter = 0x50000512;
        private const uint AltCharacter = 0x50000503;   // another character on the caller's own account

        private static uint nextGrantId = 600;

        private ACE.Database.Models.Shard.AccountVaultGrant Grant(uint owner, uint grantee = CharacterGuid, bool canWithdraw = true,
                                                                   uint? granterGuid = GranterA, string granterName = "Granter")
        {
            var g = new ACE.Database.Models.Shard.AccountVaultGrant
            {
                Id = nextGrantId++,
                OwnerAccountId = owner,
                GranteeCharacterGuid = grantee,
                CanWithdraw = canWithdraw,
                GrantedByCharacterGuid = granterGuid,
                GrantedByCharacterName = granterName,
            };

            store.Grants.Add(g);
            return g;
        }

        private async Task<(HttpStatusCode status, JsonElement json)> Shared(uint guid)
            => await Send(Get($"/v1/accounts/me/characters/{guid}/suit/shared-inventory", bearer: await LoginAsync()));

        private static List<JsonElement> Vaults(JsonElement json) => json.GetProperty("vaults").EnumerateArray().ToList();

        private static List<JsonElement> Items(JsonElement json) => json.GetProperty("items").EnumerateArray().ToList();

        [TestMethod]
        public async Task Shared_ACharacterNotOnTheAccount_IsNotOwner()
        {
            Grant(OwnerA, grantee: StrangerCharacter);
            store.SeedItem(OwnerA, Armor());

            foreach (var guid in new[] { StrangerCharacter, AltCharacter })
            {
                var (status, json) = await Shared(guid);

                Assert.AreEqual(HttpStatusCode.Forbidden, status, $"0x{guid:X8}");
                Assert.AreEqual("not_owner", json.GetProperty("error").GetString());
            }
        }

        [TestMethod]
        public async Task Shared_ADepositWithdrawGrant_ListsTheMule_AndStampsItsItems()
        {
            var g = Grant(OwnerA);
            var armor = Armor("Shared Breastplate");
            store.SeedItem(OwnerA, armor);
            store.SeedItem(AccountId, Armor("My Own Breastplate"));

            var (status, json) = await Shared(CharacterGuid);

            Assert.AreEqual(HttpStatusCode.OK, status);
            var vault = Vaults(json).Single();
            Assert.AreEqual($"s:{g.Id}", vault.GetProperty("vault_key").GetString());
            Assert.AreEqual("Granter", vault.GetProperty("label").GetString());
            Assert.AreEqual("ready", vault.GetProperty("state").GetString());

            var item = Items(json).Single();
            Assert.AreEqual("Shared Breastplate", item.GetProperty("name").GetString(), "only the shared mule's items, never the caller's own");
            Assert.AreEqual("vault", item.GetProperty("source").GetString());
            Assert.AreEqual($"s:{g.Id}", item.GetProperty("vault_key").GetString());
            Assert.AreEqual("Granter", item.GetProperty("vault_label").GetString());
            Assert.AreEqual(armor.Guid.Full, item.GetProperty("item_guid").GetUInt32());

            // The own-vault route is unchanged: no vault_key, no vault_label.
            var (_, own) = await Inventory();
            var mine = own.GetProperty("items").EnumerateArray().Single();
            Assert.IsFalse(mine.TryGetProperty("vault_key", out _));
            Assert.IsFalse(mine.TryGetProperty("vault_label", out _));
        }

        /// <summary>
        /// Owner ruling: a bonded (or sticky, or attuned) item is left out of a shared mule, while the very same item in
        /// the caller's own vault is still listed there.
        /// </summary>
        [TestMethod]
        public async Task Shared_ABondedItem_IsLeftOut_ButTheSameItemStaysInTheOwnVault()
        {
            Grant(OwnerA);

            var bonded = Armor("Bonded Breastplate");
            bonded.Bonded = BondedStatus.Bonded;
            var sticky = Armor("Sticky Breastplate");
            sticky.Bonded = BondedStatus.Sticky;
            var attuned = Armor("Attuned Breastplate");
            attuned.Attuned = AttunedStatus.Attuned;

            store.SeedItem(OwnerA, bonded);
            store.SeedItem(OwnerA, sticky);
            store.SeedItem(OwnerA, attuned);
            store.SeedItem(OwnerA, Armor("Free Breastplate"));
            store.SeedItem(AccountId, bonded);

            var (status, json) = await Shared(CharacterGuid);

            Assert.AreEqual(HttpStatusCode.OK, status);
            CollectionAssert.AreEqual(new[] { "Free Breastplate" }, Items(json).Select(i => i.GetProperty("name").GetString()).ToArray(), "bonded, sticky and attuned items never leave a shared mule");

            var (ownStatus, own) = await Inventory();

            Assert.AreEqual(HttpStatusCode.OK, ownStatus);
            CollectionAssert.Contains(Names(own), "Bonded Breastplate", "the caller's own vault keeps its bonded items");
        }

        /// <summary>
        /// A ledger row whose TEMPLATE weenie is bonded (or attuned) is left out of a shared mule, while the caller's own
        /// row of the same wcid is still listed.
        /// </summary>
        [TestMethod]
        public async Task Shared_ALedgerRowWithABondedTemplate_IsLeftOut_ButTheOwnRowStays()
        {
            var prior = MarketManager.WeenieLookup;

            MarketManager.WeenieLookup = wcid =>
            {
                var w = prior(wcid);

                if (w != null && wcid == RingWcid)
                    w.PropertiesInt[PropertyInt.Bonded] = (int)BondedStatus.Bonded;

                if (w != null && wcid == ClassRingWcid)
                    w.PropertiesInt[PropertyInt.Attuned] = (int)AttunedStatus.Attuned;

                return w;
            };

            Grant(OwnerA);
            store.SeedLedger(OwnerA, RingWcid, 4);
            store.SeedLedger(OwnerA, ClassRingWcid, 2);
            store.SeedItem(OwnerA, Armor("Free Breastplate"));
            store.SeedLedger(AccountId, RingWcid, 4);

            var (status, json) = await Shared(CharacterGuid);

            Assert.AreEqual(HttpStatusCode.OK, status);
            CollectionAssert.AreEqual(new[] { "Free Breastplate" }, Items(json).Select(i => i.GetProperty("name").GetString()).ToArray(), "a bonded or attuned template keeps the row in the mule");

            var (ownStatus, own) = await Inventory();

            Assert.AreEqual(HttpStatusCode.OK, ownStatus);
            CollectionAssert.Contains(Names(own), "Ledger Ring", "the caller's own vault keeps its bonded-template row");
        }

        [TestMethod]
        public async Task Shared_DepositOnlyShares_AreLeftOut()
        {
            Grant(OwnerA, canWithdraw: false);
            store.SeedItem(OwnerA, Armor());

            var (status, json) = await Shared(CharacterGuid);

            Assert.AreEqual(HttpStatusCode.OK, status);
            Assert.AreEqual(0, Vaults(json).Count);
            Assert.AreEqual(0, Items(json).Count);
        }

        /// <summary>The grant read still returns a grant that has since been revoked: the access re-check drops it.</summary>
        [TestMethod]
        public async Task Shared_ARevokedGrant_IsLeftOut()
        {
            var stale = Grant(OwnerA);
            store.SeedItem(OwnerA, Armor());
            store.Grants.Remove(stale);
            store.GranteeReadOverride = _ => new List<ACE.Database.Models.Shard.AccountVaultGrant> { stale };

            var (status, json) = await Shared(CharacterGuid);

            Assert.AreEqual(HttpStatusCode.OK, status);
            Assert.AreEqual(0, Vaults(json).Count);
            Assert.AreEqual(0, Items(json).Count);
        }

        /// <summary>
        /// A mule shared with the caller's ALT is not shared with this character, even when the grant read hands
        /// every row back unfiltered; and a grant owned by the caller's own account is never a shared mule.
        /// </summary>
        [TestMethod]
        public async Task Shared_AGrantToTheCallersAlt_IsNotCounted_NorOneOwnedByTheCallersAccount()
        {
            Grant(OwnerA, grantee: AltCharacter);
            Grant(AccountId);
            store.SeedItem(OwnerA, Armor());
            store.SeedItem(AccountId, Armor("Own"));
            store.GranteeReadOverride = _ => store.Grants.ToList();

            var (status, json) = await Shared(CharacterGuid);

            Assert.AreEqual(HttpStatusCode.OK, status);
            Assert.AreEqual(0, Vaults(json).Count);
            Assert.AreEqual(0, Items(json).Count);
        }

        [TestMethod]
        public async Task Shared_AFailedGrantRead_IsVaultUnavailable_NotAnEmptyList()
        {
            Grant(OwnerA);
            store.SeedItem(OwnerA, Armor());
            store.FailGranteeRead = true;

            var (status, json) = await Shared(CharacterGuid);

            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
            Assert.AreEqual("vault_unavailable", json.GetProperty("error").GetString());
            Assert.IsFalse(json.TryGetProperty("items", out _));
            Assert.IsFalse(json.TryGetProperty("vaults", out _));
        }

        [TestMethod]
        public async Task Shared_ListedParts_AreLeftOut_APartlyListedRowSendsItsFreeRemainder()
        {
            Grant(OwnerA);
            var listedArmor = Armor("Listed Breastplate");
            store.SeedItem(OwnerA, listedArmor);
            store.SeedItem(OwnerA, Item(ArmorWcid, "Unlisted Helm", ItemType.Armor, EquipMask.HeadWear));
            store.SeedLedger(OwnerA, RingWcid, 4);

            var owner = new MarketActor(OwnerA, GranterA, "Granter");
            Assert.IsTrue(MarketManager.List(owner, listedArmor.Guid.Full, ArmorWcid, 1, 500, MarketChannel.Web).Ok);
            Assert.IsTrue(MarketManager.List(owner, null, RingWcid, 3, 500, MarketChannel.Web).Ok);

            var (status, json) = await Shared(CharacterGuid);
            var items = Items(json);

            Assert.AreEqual(HttpStatusCode.OK, status);
            CollectionAssert.AreEquivalent(new[] { "Ledger Ring", "Unlisted Helm" }, items.Select(i => i.GetProperty("name").GetString()).ToArray());
            Assert.AreEqual(1, items.Single(i => i.GetProperty("name").GetString() == "Ledger Ring").GetProperty("count").GetInt32(), "4 held, 3 listed: the free 1 only");
            Assert.IsTrue(items.All(i => !i.GetProperty("listed").GetBoolean()));
        }

        [TestMethod]
        public async Task Shared_ACompletelyListedRow_ContributesNothing()
        {
            Grant(OwnerA);
            store.SeedLedger(OwnerA, RingWcid, 4);
            Assert.IsTrue(MarketManager.List(new MarketActor(OwnerA, GranterA, "Granter"), null, RingWcid, 4, 500, MarketChannel.Web).Ok);

            var (_, json) = await Shared(CharacterGuid);

            Assert.AreEqual("ready", Vaults(json).Single().GetProperty("state").GetString());
            Assert.AreEqual(0, Items(json).Count);
        }

        [TestMethod]
        public async Task Shared_AtMostTenMules_InOwnerOrder()
        {
            for (uint owner = 9400; owner < 9412; owner++)
            {
                Grant(owner, granterName: $"Granter{owner}", granterGuid: null);
                store.SeedItem(owner, Armor($"Armor {owner}"));
            }

            var (_, json) = await Shared(CharacterGuid);
            var vaults = Vaults(json);

            Assert.AreEqual(10, vaults.Count);
            CollectionAssert.AreEqual(Enumerable.Range(9400, 10).Select(o => $"Granter{o}").ToArray(), vaults.Select(v => v.GetProperty("label").GetString()).ToArray());
            Assert.AreEqual(10, Items(json).Count);
            Assert.IsFalse(Items(json).Any(i => i.GetProperty("name").GetString() == "Armor 9410"), "the 11th owner is not read");
        }

        [TestMethod]
        public async Task Shared_AColdStore_IsLoading_AndTheOthersStillServe()
        {
            Grant(OwnerA);
            Grant(OwnerB, granterGuid: null, granterName: "Bee");
            store.SeedItem(OwnerA, Armor("Cold Armor"));
            store.SeedItem(OwnerB, Armor("Warm Armor"));
            store.NotReadyAccounts.Add(OwnerA);

            var (status, json) = await Shared(CharacterGuid);

            Assert.AreEqual(HttpStatusCode.OK, status);
            var vaults = Vaults(json);
            Assert.AreEqual("loading", vaults.Single(v => v.GetProperty("label").GetString() == "Granter").GetProperty("state").GetString());
            Assert.AreEqual("ready", vaults.Single(v => v.GetProperty("label").GetString() == "Bee").GetProperty("state").GetString());
            CollectionAssert.AreEqual(new[] { "Warm Armor" }, Items(json).Select(i => i.GetProperty("name").GetString()).ToArray());

            // Never a cold load from the request thread: the cold mule is queued for warming, and IsReady (which is
            // TryCheckReady, the call that loads) is never asked about it.
            CollectionAssert.AreEqual(new[] { OwnerA }, store.WarmRequests.ToArray());
            CollectionAssert.DoesNotContain(store.IsReadyAccounts, OwnerA);
        }

        /// <summary>
        /// Through the REAL VaultMarketItemStore: a cold owner whose store is not live is never built (GetStore builds
        /// one and refreshes its capacity bonuses) - it is queued for warming and listed as loading.
        /// </summary>
        [TestMethod]
        public void Shared_RealItemStore_AColdOwner_BuildsNoStore_AndQueuesAWarmUp()
        {
            var constructions = 0;
            var warmed = new List<uint>();
            var grant = new ACE.Database.Models.Shard.AccountVaultGrant { Id = 4242, OwnerAccountId = OwnerA, GranteeCharacterGuid = CharacterGuid, CanWithdraw = true, GrantedByCharacterName = "Granter" };

            var real = new VaultMarketItemStore(
                storeResolver: id => { constructions++; return null; },
                grantsResolver: _ => new List<ACE.Database.Models.Shard.AccountVaultGrant> { grant },
                existingStoreResolver: _ => null,
                requestWarm: id => { warmed.Add(id); return true; });

            MarketManager.Shutdown();
            MarketManager.Initialize(real, wallet, repo);

            SuitCharacterResolver roster = (uint account, uint character, out string name) =>
            {
                name = "Suitwearer";
                return account == AccountId && character == CharacterGuid;
            };

            Assert.IsTrue(MarketManager.TryGetSharedSuitInventory(AccountId, CharacterGuid, roster, out var vaults, out var items, out var error), error.ToString());

            Assert.AreEqual(SharedSuitVault.StateLoading, vaults.Single().State);
            Assert.AreEqual("s:4242", vaults.Single().VaultKey);
            Assert.AreEqual(0, items.Count);
            Assert.AreEqual(0, constructions, "no shared lookup builds a store");
            CollectionAssert.AreEqual(new[] { OwnerA }, warmed);
        }

        /// <summary>Deposit-only shares must not use up the ten-mule cap ahead of a withdraw share.</summary>
        [TestMethod]
        public async Task Shared_TenDepositOnlyOwners_DoNotCrowdOutAWithdrawOwner()
        {
            for (uint owner = 9400; owner < 9410; owner++)
                Grant(owner, canWithdraw: false, granterGuid: null, granterName: $"Deposit{owner}");

            Grant(9420, granterGuid: null, granterName: "Withdrawer");
            store.SeedItem(9420, Armor("Withdrawable Armor"));

            var (status, json) = await Shared(CharacterGuid);

            Assert.AreEqual(HttpStatusCode.OK, status);
            Assert.AreEqual("Withdrawer", Vaults(json).Single().GetProperty("label").GetString());
            CollectionAssert.AreEqual(new[] { "Withdrawable Armor" }, Items(json).Select(i => i.GetProperty("name").GetString()).ToArray());
        }

        [TestMethod]
        public async Task Shared_AnAccessReadFailure_IsLoading_NeverDropped()
        {
            Grant(OwnerA);
            store.SeedItem(OwnerA, Armor());
            store.FailAccessOwners.Add(OwnerA);

            var (_, json) = await Shared(CharacterGuid);

            Assert.AreEqual("loading", Vaults(json).Single().GetProperty("state").GetString());
            Assert.AreEqual(0, Items(json).Count);
        }

        /// <summary>
        /// The label is the GRANTING character's current name (else its stored name, else the numbered fallback),
        /// and nothing in the response names another character on the owner's account or the owner account.
        /// </summary>
        [TestMethod]
        public async Task Shared_TheLabel_NeverNamesANonGranterCharacter()
        {
            var a = Grant(OwnerA, granterGuid: GranterA, granterName: "OldName");
            Grant(OwnerB, granterGuid: null, granterName: null);
            Grant(9313, granterGuid: null, granterName: "<unknown>");
            store.CurrentNames[GranterA] = "NewName";
            store.CurrentNames[OwnerAOtherCharacter] = "OwnersOtherAlt";
            store.SeedItem(OwnerA, Armor());

            var (status, json) = await Shared(CharacterGuid);
            var text = json.GetRawText();

            Assert.AreEqual(HttpStatusCode.OK, status);
            CollectionAssert.AreEqual(new[] { "NewName", "A shared mule", "A shared mule (2)" }, Vaults(json).Select(v => v.GetProperty("label").GetString()).ToArray());
            Assert.IsFalse(text.Contains("OwnersOtherAlt"), text);
            Assert.IsFalse(text.Contains("OldName"), "the current name wins over the stored one");
            Assert.IsFalse(Vaults(json).Any(v => v.GetRawText().Contains(OwnerA.ToString())), "the owner account id is never on a vault entry");
            Assert.AreEqual($"s:{a.Id}", Vaults(json)[0].GetProperty("vault_key").GetString());
        }

        [TestMethod]
        public async Task Shared_SuitBuilderOff_IsRefused()
        {
            Grant(OwnerA);
            suitEnabled = false;

            var (status, json) = await Shared(CharacterGuid);

            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
            Assert.AreEqual("suit_builder_disabled", json.GetProperty("error").GetString());
        }
        // ---- GET /v1/accounts/me/characters/{guid}/suit-profile ----

        [TestMethod]
        public async Task Profile_CharacterOnAnotherAccount_IsNotOwner403_AndTheServiceIsNotCalled()
        {
            var (status, json) = await Profile(StrangerCharacter);

            Assert.AreEqual(HttpStatusCode.Forbidden, status);
            Assert.AreEqual("not_owner", json.GetProperty("error").GetString());
            Assert.AreEqual(0, sheets.GetOwnerProfileCalls);
        }

        [TestMethod]
        public async Task Profile_GuidOutOfRange_IsNotOwner()
        {
            var token = await LoginAsync();

            foreach (var guid in new[] { "0", "-5", "99999999999" })
            {
                var (status, json) = await Send(Get($"/v1/accounts/me/characters/{guid}/suit-profile", bearer: token));
                Assert.AreEqual(HttpStatusCode.Forbidden, status, guid);
                Assert.AreEqual("not_owner", json.GetProperty("error").GetString(), guid);
            }

            Assert.AreEqual(0, sheets.GetOwnerProfileCalls);
        }

        [TestMethod]
        public async Task Profile_NonNumericGuid_Is404AtRouting_AndTheServiceIsNotCalled()
        {
            var token = await LoginAsync();

            var (status, _) = await Send(Get("/v1/accounts/me/characters/abc/suit-profile", bearer: token));

            Assert.AreEqual(HttpStatusCode.NotFound, status);
            Assert.AreEqual(0, sheets.GetOwnerProfileCalls);
        }

        [TestMethod]
        public async Task Profile_OwnCharacter_ReturnsTheDocumentedShape()
        {
            var armorObject = Armor("Worn Breastplate");
            armorObject.CurrentWieldedLocation = EquipMask.ChestArmor;
            var armor = CharacterSheetProjector.ProjectEquippedForSuit(new[] { armorObject }, "Tester").Single();

            sheets.NextOwnerProfile = new OwnerProfileResult(SheetOutcome.Ok, new OwnerProfile
            {
                CharacterGuid = CharacterGuid,
                Name = "Suitwearer",
                Level = 275,
                Heritage = "Gharu'ndim",
                Online = false,
                Attributes = new List<SheetStat> { new SheetStat { Name = "Strength", Base = 200, Current = 250 } },
                Vitals = new List<SheetStat> { new SheetStat { Name = "Health", Base = 300, Current = 420 } },
                Skills = new List<SheetSkill> { new SheetSkill { Name = "War Magic", Advancement = "specialized", Base = 400, Current = 480 } },
                Equipped = new List<SuitItem> { armor },
                EquippedAppraisal = new List<SheetItem>
                {
                    new SheetItem { ItemGuid = armor.ItemGuid.Value, Name = "Worn Breastplate", Slot = "ChestArmor", Snapshot = MarketSnapshot.FromItem(armorObject) },
                },
            });

            var (status, json) = await Profile(CharacterGuid);

            Assert.AreEqual(HttpStatusCode.OK, status);
            Assert.AreEqual(CharacterGuid, sheets.LastOwnerProfileGuid);
            Assert.AreEqual(CharacterGuid, json.GetProperty("character_guid").GetUInt32());
            Assert.AreEqual("Suitwearer", json.GetProperty("name").GetString());
            Assert.AreEqual(275, json.GetProperty("level").GetInt32());
            Assert.AreEqual("Gharu'ndim", json.GetProperty("heritage").GetString());
            Assert.IsFalse(json.GetProperty("online").GetBoolean());

            var attribute = json.GetProperty("attributes")[0];
            Assert.AreEqual(200u, attribute.GetProperty("base").GetUInt32());
            Assert.AreEqual(250u, attribute.GetProperty("current").GetUInt32());
            Assert.AreEqual(420u, json.GetProperty("vitals")[0].GetProperty("current").GetUInt32());

            var skill = json.GetProperty("skills")[0];
            Assert.AreEqual("specialized", skill.GetProperty("advancement").GetString());
            Assert.AreEqual(400u, skill.GetProperty("base").GetUInt32());
            Assert.AreEqual(480u, skill.GetProperty("current").GetUInt32());

            var worn = json.GetProperty("equipped")[0];
            Assert.AreEqual("equipped", worn.GetProperty("source").GetString());
            Assert.AreEqual("Worn Breastplate", worn.GetProperty("name").GetString());
            Assert.AreEqual((int)EquipMask.ChestArmor, worn.GetProperty("current_wielded_location").GetInt32());

            var appraisal = json.GetProperty("equipped_appraisal");
            Assert.AreEqual(1, appraisal.GetArrayLength());
            Assert.AreEqual(armor.ItemGuid.Value, appraisal[0].GetProperty("item_guid").GetUInt32());
            Assert.AreEqual("Worn Breastplate", appraisal[0].GetProperty("name").GetString());
            Assert.AreEqual("ChestArmor", appraisal[0].GetProperty("slot").GetString());
            Assert.AreEqual(JsonValueKind.Object, appraisal[0].GetProperty("snapshot").ValueKind);
        }

        [TestMethod]
        public void JoinEquippedAppraisal_KeepsOnlyGuidsInTheSuitEquippedList()
        {
            var armor = Armor("Breastplate");
            var sword = Item(SwordWcid, "Sword", ItemType.MeleeWeapon, EquipMask.MeleeWeapon);
            var worn = new List<WorldObject> { armor, sword };

            var suit = CharacterSheetProjector.ProjectEquippedForSuit(worn, "Tester");
            var sheet = worn.Select(w => new SheetItem { ItemGuid = w.Guid.Full, Name = w.Name, Slot = "x" }).ToList();

            var joined = CharacterSheetProjector.JoinEquippedAppraisal(sheet, suit);

            Assert.AreEqual(1, suit.Count, "the weapon has no suit slot");
            CollectionAssert.AreEqual(new[] { "Breastplate" }, joined.Select(j => j.Name).ToArray());
            Assert.IsFalse(joined.Any(j => j.ItemGuid == sword.Guid.Full), "the weapon fixture is absent");
        }

        [TestMethod]
        public void ProjectOwnerEquipment_AppraisalGuidsEqualSuitGuids_SwordAbsent_RingSidesDiffer()
        {
            var armor = Armor("Breastplate");
            var sword = Item(SwordWcid, "Sword", ItemType.MeleeWeapon, EquipMask.MeleeWeapon);
            var left = Item(RingWcid, "Left Ring", ItemType.Jewelry, EquipMask.FingerWearLeft | EquipMask.FingerWearRight);
            var right = Item(RingWcid, "Right Ring", ItemType.Jewelry, EquipMask.FingerWearLeft | EquipMask.FingerWearRight);
            armor.CurrentWieldedLocation = EquipMask.ChestArmor;
            sword.CurrentWieldedLocation = EquipMask.MeleeWeapon;
            left.CurrentWieldedLocation = EquipMask.FingerWearLeft;
            right.CurrentWieldedLocation = EquipMask.FingerWearRight;

            var worn = new List<WorldObject> { armor, sword, left, right };
            var sheet = worn.Select(w => new SheetItem { ItemGuid = w.Guid.Full, Name = w.Name, Slot = "x" }).ToList();

            var (equipped, appraisal) = CharacterSheetProjector.ProjectOwnerEquipment(worn, "Tester", sheet);

            Assert.AreEqual(3, equipped.Count);
            CollectionAssert.AreEquivalent(equipped.Select(e => e.ItemGuid.Value).ToArray(), appraisal.Select(a => a.ItemGuid).ToArray());
            Assert.IsFalse(appraisal.Any(a => a.ItemGuid == sword.Guid.Full), "the sword is absent");
            Assert.AreNotEqual(
                equipped.Single(e => e.Name == "Left Ring").CurrentWieldedLocation,
                equipped.Single(e => e.Name == "Right Ring").CurrentWieldedLocation);
        }

        [TestMethod]
        public void CurrentWieldedLocation_VaultFromItemHasNone_EquippedProjectionHasIt()
        {
            var item = Item(RingWcid, "Ring", ItemType.Jewelry, EquipMask.FingerWearLeft | EquipMask.FingerWearRight);
            item.CurrentWieldedLocation = EquipMask.FingerWearLeft;

            var vault = SuitItemProjector.FromItem(item, SuitItem.SourceVault);
            var worn = CharacterSheetProjector.ProjectEquippedForSuit(new[] { item }, "Tester").Single();

            Assert.IsNull(vault.CurrentWieldedLocation);
            Assert.AreEqual((int)EquipMask.FingerWearLeft, worn.CurrentWieldedLocation);
            Assert.IsFalse(JsonSerializer.Serialize(vault, MarketApiHost.Json).Contains("current_wielded_location"), "omitted, not null");
        }

        [TestMethod]
        public void CurrentWieldedLocation_LeftAndRightRing_AreDifferentValues()
        {
            var left = Item(RingWcid, "Left Ring", ItemType.Jewelry, EquipMask.FingerWearLeft | EquipMask.FingerWearRight);
            var right = Item(RingWcid, "Right Ring", ItemType.Jewelry, EquipMask.FingerWearLeft | EquipMask.FingerWearRight);
            left.CurrentWieldedLocation = EquipMask.FingerWearLeft;
            right.CurrentWieldedLocation = EquipMask.FingerWearRight;

            var worn = CharacterSheetProjector.ProjectEquippedForSuit(new[] { left, right }, "Tester");
            var byName = worn.ToDictionary(w => w.Name);

            Assert.AreEqual((int)EquipMask.FingerWearLeft, byName["Left Ring"].CurrentWieldedLocation);
            Assert.AreEqual((int)EquipMask.FingerWearRight, byName["Right Ring"].CurrentWieldedLocation);
            Assert.AreNotEqual(byName["Left Ring"].CurrentWieldedLocation, byName["Right Ring"].CurrentWieldedLocation);
        }

        /// <summary>
        /// The owner projection's heritage fields. Olthoi and OlthoiAcid share the display name "Olthoi"
        /// (HeritageGroupExtensions.ToSentence), so only heritage_id tells acid-Olthoi armor
        /// (HeritageSpecificArmor 13) apart; the display name passes through unchanged.
        /// </summary>
        [TestMethod]
        public void ProjectHeritage_OlthoiAcidIs13_PlainOlthoiIs12_NameUnchanged()
        {
            var acid = CharacterSheetProjector.ProjectHeritage("Olthoi", HeritageGroup.OlthoiAcid);
            var plain = CharacterSheetProjector.ProjectHeritage("Olthoi", HeritageGroup.Olthoi);

            Assert.AreEqual(13, acid.Id);
            Assert.AreEqual(12, plain.Id);
            Assert.AreEqual("Olthoi", acid.Name, "the display string is not changed by this field");
            Assert.AreEqual("Olthoi", plain.Name);

            Assert.AreEqual("OlthoiAcid", CharacterSheetProjector.ProjectHeritage(null, HeritageGroup.OlthoiAcid).Name, "no stored name falls back to the enum name, as before");
        }

        [TestMethod]
        [DataRow(13)]
        [DataRow(12)]
        public async Task Profile_EmitsHeritageId_NextToTheUnchangedHeritageString(int heritageId)
        {
            sheets.NextOwnerProfile = new OwnerProfileResult(SheetOutcome.Ok, new OwnerProfile
            {
                CharacterGuid = CharacterGuid,
                Name = "Acidwalker",
                Heritage = "Olthoi",
                HeritageId = heritageId,
            });

            var (status, json) = await Profile(CharacterGuid);

            Assert.AreEqual(HttpStatusCode.OK, status);
            Assert.AreEqual(heritageId, json.GetProperty("heritage_id").GetInt32());
            Assert.AreEqual("Olthoi", json.GetProperty("heritage").GetString());
        }

        [TestMethod]
        public void PublicSheetJson_CarriesNoHeritageId()
        {
            var sheet = new CharacterSheet { Name = "x", Owner = new OwnerProfile { Name = "x", Heritage = "Olthoi", HeritageId = 13 } };

            var text = JsonSerializer.Serialize(sheet, MarketApiHost.Json);

            Assert.IsFalse(text.Contains("heritage", StringComparison.Ordinal), $"the public sheet must not carry owner fields: {text}");
        }

        [TestMethod]
        public async Task Profile_MarketDisabled_SuitBuilderEnabled_StillServes()
        {
            Assert.IsTrue(PropertyManager.ModifyBool("market_enabled", false));

            var (status, _) = await Profile(CharacterGuid);

            Assert.AreEqual(HttpStatusCode.OK, status);
            Assert.AreEqual(1, sheets.GetOwnerProfileCalls);
        }

        [TestMethod]
        public async Task Profile_SuitBuilderOff_IsRefused503_AndTheServiceIsNotCalled()
        {
            suitEnabled = false;

            var (status, json) = await Profile(CharacterGuid);

            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
            Assert.AreEqual("suit_builder_disabled", json.GetProperty("error").GetString());
            Assert.AreEqual(0, sheets.GetOwnerProfileCalls);
        }

        [TestMethod]
        public async Task Profile_ServiceOutcomes_MapToPublishedCodes()
        {
            sheets.NextOwnerProfile = new OwnerProfileResult(SheetOutcome.Busy, null);
            var (busyStatus, busy) = await Profile(CharacterGuid);
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, busyStatus);
            Assert.AreEqual("sheet_busy", busy.GetProperty("error").GetString());

            sheets.NextOwnerProfile = new OwnerProfileResult(SheetOutcome.NotFound, null);
            var (missingStatus, missing) = await Profile(CharacterGuid);
            Assert.AreEqual(HttpStatusCode.NotFound, missingStatus);
            Assert.AreEqual("sheet_not_found", missing.GetProperty("error").GetString());

            sheets.NextOwnerProfile = new OwnerProfileResult(SheetOutcome.Failed, null);
            var (failedStatus, failed) = await Profile(CharacterGuid);
            Assert.AreEqual(HttpStatusCode.InternalServerError, failedStatus);
            Assert.AreEqual("server_error", failed.GetProperty("error").GetString());
        }

        // ---- equipped filter ----

        [TestMethod]
        public void EquippedForSuit_KeepsOnlySuitRelevantItems_AsEquippedSource()
        {
            var worn = new List<WorldObject>
            {
                Armor("Breastplate"),
                Item(RingWcid, "Ring", ItemType.Jewelry, EquipMask.FingerWearLeft | EquipMask.FingerWearRight),
                Item(ArmorWcid, "Cloak", ItemType.Armor, EquipMask.Cloak),
                Item(ArmorWcid, "Shirt", ItemType.Clothing, EquipMask.ChestWear | EquipMask.AbdomenWear),
                Item(SwordWcid, "Sword", ItemType.MeleeWeapon, EquipMask.MeleeWeapon),
                Item(ShieldWcid, "Shield", ItemType.Armor, EquipMask.Shield),
                Item(ArmorWcid, "Aetheria", ItemType.Misc, EquipMask.SigilOne),
            };

            var equipped = CharacterSheetProjector.ProjectEquippedForSuit(worn, "Tester");

            CollectionAssert.AreEquivalent(new[] { "Breastplate", "Cloak", "Ring", "Shirt" }, equipped.Select(e => e.Name).ToArray());
            Assert.IsTrue(equipped.All(e => e.Source == "equipped"));
        }

        // ---- pack projection ----

        private static Container SidePack(params WorldObject[] contents)
        {
            var weenie = new Weenie
            {
                WeenieClassId = 93100,
                WeenieType = WeenieType.Container,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Side Pack" } },
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemsCapacity, 24 },
                    { PropertyInt.ContainersCapacity, 4 },
                },
            };

            var pack = new Container(weenie, new ObjectGuid((uint)nextGuid++));

            foreach (var item in contents)
                Assert.IsTrue(pack.TryAddToInventory(item), "could not seed a side-pack item");

            return pack;
        }

        private static HashSet<uint> Guids(params WorldObject[] items) => new HashSet<uint>(items.Select(i => i.Guid.Full));

        [TestMethod]
        public void PackForSuit_ExcludesAWornItem_EvenWhenItAlsoSitsInTheInventoryCollection()
        {
            var worn = Armor("Worn Breastplate");
            var carried = Armor("Spare Breastplate");

            // corrupt-data shape: the worn item is ALSO listed in the pack; the equipped guid set must win
            var pack = CharacterSheetProjector.ProjectPackForSuit(new[] { worn, carried }, Guids(worn), CharacterGuid, "Tester");

            CollectionAssert.AreEqual(new[] { "Spare Breastplate" }, pack.Select(p => p.Name).ToArray());
        }

        [TestMethod]
        public void PackForSuit_IncludesASidePackItem_FoundOnlyByWalkingTheSideContainer()
        {
            var inMain = Armor("Main Pack Plate");
            var inSide = Item(RingWcid, "Side Pack Ring", ItemType.Jewelry, EquipMask.FingerWearLeft | EquipMask.FingerWearRight);
            var side = SidePack(inSide);

            // the ring is reachable ONLY through the side container
            var pack = CharacterSheetProjector.ProjectPackForSuit(new WorldObject[] { inMain, side }, new HashSet<uint>(), CharacterGuid, "Tester");

            CollectionAssert.AreEquivalent(new[] { "Main Pack Plate", "Side Pack Ring" }, pack.Select(p => p.Name).ToArray());
        }

        [TestMethod]
        public void PackForSuit_ListsAnItemOnceEvenWhenTwoCollectionsHoldIt()
        {
            var ring = Item(RingWcid, "Twice Listed Ring", ItemType.Jewelry, EquipMask.FingerWearLeft | EquipMask.FingerWearRight);
            var side = SidePack(ring);

            // deliberately corrupt: the ring is in the main list AND inside the side pack
            var pack = CharacterSheetProjector.ProjectPackForSuit(new WorldObject[] { side, ring }, new HashSet<uint>(), CharacterGuid, "Tester");

            Assert.AreEqual(1, pack.Count);
            Assert.AreEqual("Twice Listed Ring", pack[0].Name);
        }

        [TestMethod]
        public void PackForSuit_IsDepthOne_ANestedContainersContentsAreNotProjected()
        {
            var deep = Armor("Deep Plate");
            var nested = SidePack(deep);
            var side = SidePack();
            Assert.IsTrue(side.TryAddToInventory(nested), "fixture: nested container accepted");
            var shallow = Armor("Shallow Plate");
            Assert.IsTrue(side.TryAddToInventory(shallow));

            var pack = CharacterSheetProjector.ProjectPackForSuit(new WorldObject[] { side }, new HashSet<uint>(), CharacterGuid, "Tester");

            CollectionAssert.AreEqual(new[] { "Shallow Plate" }, pack.Select(p => p.Name).ToArray());
        }

        [TestMethod]
        public void PackForSuit_Cap_KeepsTheLowestGuidsInAFixedOrder_NotTheInputOrder()
        {
            var plates = Enumerable.Range(0, 12).Select(i => Armor($"Plate {i}")).ToList();   // guids ascend with i
            var expected = plates.Take(5).Select(p => p.Name).ToArray();

            var forward = CharacterSheetProjector.ProjectPackForSuit(plates, new HashSet<uint>(), CharacterGuid, "Tester", 5, out var truncatedForward);
            var shuffled = CharacterSheetProjector.ProjectPackForSuit(plates.AsEnumerable().Reverse().OrderBy(p => p.Guid.Full % 3).ToList(), new HashSet<uint>(), CharacterGuid, "Tester", 5, out var truncatedShuffled);

            CollectionAssert.AreEqual(expected, forward.Select(p => p.Name).ToArray());
            CollectionAssert.AreEqual(expected, shuffled.Select(p => p.Name).ToArray());
            Assert.IsTrue(truncatedForward);
            Assert.IsTrue(truncatedShuffled);
        }

        [TestMethod]
        public void PackForSuit_Cap_TakesMainPackBeforeSidePacks_AndSidePacksByPlacementThenGuid()
        {
            var mainPlate = Armor("Main");
            var lateSideItem = Armor("Late Side");
            var earlySideItem = Armor("Early Side");
            var lateSide = SidePack(lateSideItem);
            var earlySide = SidePack(earlySideItem);
            lateSide.PlacementPosition = 2;
            earlySide.PlacementPosition = 1;

            var pack = CharacterSheetProjector.ProjectPackForSuit(new WorldObject[] { lateSide, earlySide, mainPlate }, new HashSet<uint>(), CharacterGuid, "Tester", 2, out var truncated);

            CollectionAssert.AreEqual(new[] { "Main", "Early Side" }, pack.Select(p => p.Name).ToArray());
            Assert.IsTrue(truncated);
        }

        [TestMethod]
        public void PackForSuit_NotTruncated_WhenEverythingFits()
        {
            var pack = CharacterSheetProjector.ProjectPackForSuit(new[] { Armor("A"), Armor("B") }, new HashSet<uint>(), CharacterGuid, "Tester", 2, out var truncated);

            Assert.AreEqual(2, pack.Count);
            Assert.IsFalse(truncated, "exactly at the cap is not truncation");
        }

        [TestMethod]
        public async Task Profile_PackTruncated_IsEmittedOnlyWhenTrue()
        {
            sheets.NextOwnerProfile = new OwnerProfileResult(SheetOutcome.Ok, new OwnerProfile { CharacterGuid = CharacterGuid, Name = "x" });
            var (_, plain) = await Profile(CharacterGuid);
            Assert.IsFalse(plain.TryGetProperty("pack_truncated", out _), "omitted when false");

            sheets.NextOwnerProfile = new OwnerProfileResult(SheetOutcome.Ok, new OwnerProfile { CharacterGuid = CharacterGuid, Name = "x", PackTruncated = true });
            var (_, cut) = await Profile(CharacterGuid);
            Assert.IsTrue(cut.GetProperty("pack_truncated").GetBoolean());
        }

        [TestMethod]
        public void OwnerGear_PackComesFromInventory_NotEquippedObjects_AndHolderGuidPassesThrough()
        {
            var worn = Armor("Worn Plate");
            var carried = Armor("Carried Plate");
            var sheet = new List<SheetItem> { new SheetItem { ItemGuid = worn.Guid.Full, Name = "Worn Plate", Slot = "x" } };

            // the worn item is in the inventory collection too (corrupt shape): worn wins, so it must not be in pack
            var gear = CharacterSheetProjector.ProjectOwnerGear(new[] { worn }, new[] { carried, worn }, "Tester", sheet, CharacterGuid);

            CollectionAssert.AreEqual(new[] { "Worn Plate" }, gear.Equipped.Select(e => e.Name).ToArray());
            CollectionAssert.AreEqual(new[] { "Carried Plate" }, gear.Pack.Select(e => e.Name).ToArray());
            Assert.AreEqual(CharacterGuid, gear.Equipped.Single().HolderGuid);
            Assert.AreEqual(CharacterGuid, gear.Pack.Single().HolderGuid);
            Assert.IsFalse(gear.PackTruncated);
        }

        [TestMethod]
        public void AttunedBonded_FlagsAgreeWithIsBoundForSharing_ForEveryCombination()
        {
            foreach (var attuned in new int?[] { null, -1, 0, 1, 2 })
            {
                foreach (var bonded in new int?[] { null, -2, -1, 0, 1, 2 })
                {
                    var item = Armor("Flagged");
                    if (attuned.HasValue) item.SetProperty(PropertyInt.Attuned, attuned.Value);
                    if (bonded.HasValue) item.SetProperty(PropertyInt.Bonded, bonded.Value);

                    var vaultRow = SuitItemProjector.FromItem(item, SuitItem.SourceVault);
                    var flagged = vaultRow.Attuned == true || vaultRow.Bonded == true;

                    Assert.AreEqual(MarketManager.IsBoundForSharing(item), flagged, $"attuned={attuned} bonded={bonded}: the shared route's exclusion and the wire flags must agree");

                    var weenie = NewWeenie(ArmorWcid, "Flagged", ItemType.Armor, EquipMask.ChestArmor);
                    if (attuned.HasValue) weenie.PropertiesInt[PropertyInt.Attuned] = attuned.Value;
                    if (bonded.HasValue) weenie.PropertiesInt[PropertyInt.Bonded] = bonded.Value;

                    var ledgerRow = SuitItemProjector.FromWeenie(weenie);
                    Assert.AreEqual(MarketManager.IsBoundForSharing(weenie), ledgerRow.Attuned == true || ledgerRow.Bonded == true, $"weenie attuned={attuned} bonded={bonded}");
                }
            }
        }
        [TestMethod]
        public void PackForSuit_DropsNonSuitItems_AndTheSidePackItself()
        {
            var sword = Item(SwordWcid, "Sword", ItemType.MeleeWeapon, EquipMask.MeleeWeapon);
            var shield = Item(ShieldWcid, "Shield", ItemType.Armor, EquipMask.Shield);
            var salvage = Item(SalvageWcid, "Salvage", ItemType.TinkeringMaterial, EquipMask.None);
            var keep = Armor("Keeper");
            var side = SidePack(sword, shield, salvage);

            var pack = CharacterSheetProjector.ProjectPackForSuit(new WorldObject[] { side, keep }, new HashSet<uint>(), CharacterGuid, "Tester");

            CollectionAssert.AreEqual(new[] { "Keeper" }, pack.Select(p => p.Name).ToArray());
        }

        [TestMethod]
        public void HolderGuid_IsSetOnPackAndEquippedRows_AndSourcesAreRight()
        {
            var worn = Armor("Worn");
            var carried = Armor("Carried");

            var equipped = CharacterSheetProjector.ProjectEquippedForSuit(new[] { worn }, "Tester", CharacterGuid).Single();
            var pack = CharacterSheetProjector.ProjectPackForSuit(new[] { carried }, Guids(worn), CharacterGuid, "Tester").Single();

            Assert.AreEqual(CharacterGuid, equipped.HolderGuid);
            Assert.AreEqual(CharacterGuid, pack.HolderGuid);
            Assert.AreEqual("equipped", equipped.Source);
            Assert.AreEqual("pack", pack.Source);
            Assert.IsNull(SuitItemProjector.FromItem(carried).HolderGuid, "a vault projection has no holder");
        }

        [TestMethod]
        [DataRow(0, false)]
        [DataRow(1, true)]
        [DataRow(2, true)]
        public void AttunedAndBonded_AreTrueOnlyWhenAtLeastOne_OnEverySource(int value, bool expectedSet)
        {
            foreach (var property in new[] { PropertyInt.Attuned, PropertyInt.Bonded })
            {
                var item = Armor("Flagged");
                item.SetProperty(property, value);

                var projections = new[]
                {
                    SuitItemProjector.FromItem(item, SuitItem.SourceVault),
                    CharacterSheetProjector.ProjectEquippedForSuit(new[] { item }, "Tester").Single(),
                    CharacterSheetProjector.ProjectPackForSuit(new[] { item }, new HashSet<uint>(), CharacterGuid, "Tester").Single(),
                };

                foreach (var s in projections)
                {
                    var flag = property == PropertyInt.Attuned ? s.Attuned : s.Bonded;
                    var wireName = property == PropertyInt.Attuned ? "attuned" : "bonded";
                    var root = JsonDocument.Parse(JsonSerializer.Serialize(s, MarketApiHost.Json)).RootElement;

                    if (expectedSet)
                    {
                        Assert.AreEqual(true, flag, $"{property} {value} on {s.Source}");
                        Assert.IsTrue(root.GetProperty(wireName).GetBoolean());
                    }
                    else
                    {
                        Assert.IsNull(flag, $"{property} {value} on {s.Source}");
                        Assert.IsFalse(root.TryGetProperty(wireName, out _), "omitted, not false");
                    }
                }
            }
        }

        [TestMethod]
        public void AttunedAndBonded_AbsentProperty_IsOmitted()
        {
            var s = SuitItemProjector.FromItem(Armor("Plain"));

            Assert.IsNull(s.Attuned);
            Assert.IsNull(s.Bonded);
        }

        [TestMethod]
        public void PackForSuit_WireNames_AreHolderGuidAttunedBondedAndPackSource()
        {
            var item = Armor("Wire");
            item.SetProperty(PropertyInt.Attuned, 1);
            item.SetProperty(PropertyInt.Bonded, 1);

            var pack = CharacterSheetProjector.ProjectPackForSuit(new[] { item }, new HashSet<uint>(), CharacterGuid, "Tester");
            var root = JsonDocument.Parse(JsonSerializer.Serialize(pack, MarketApiHost.Json)).RootElement[0];

            Assert.AreEqual("pack", root.GetProperty("source").GetString());
            Assert.AreEqual(CharacterGuid, root.GetProperty("holder_guid").GetUInt32());
            Assert.IsTrue(root.GetProperty("attuned").GetBoolean());
            Assert.IsTrue(root.GetProperty("bonded").GetBoolean());
        }

        [TestMethod]
        public async Task Profile_EmitsPackBesideEquipped()
        {
            var carried = Armor("Carried Plate");
            var pack = CharacterSheetProjector.ProjectPackForSuit(new[] { carried }, new HashSet<uint>(), CharacterGuid, "Tester");

            sheets.NextOwnerProfile = new OwnerProfileResult(SheetOutcome.Ok, new OwnerProfile
            {
                CharacterGuid = CharacterGuid,
                Name = "Suitwearer",
                Pack = pack,
            });

            var (status, json) = await Profile(CharacterGuid);

            Assert.AreEqual(HttpStatusCode.OK, status);
            Assert.AreEqual(1, json.GetProperty("pack").GetArrayLength());
            Assert.AreEqual("pack", json.GetProperty("pack")[0].GetProperty("source").GetString());
            Assert.AreEqual(CharacterGuid, json.GetProperty("pack")[0].GetProperty("holder_guid").GetUInt32());
        }

        // ---- the owner view through the service ----

        private long ms;

        private CharacterSheetService Service(FakeCharacterSheetWorld world, FakeCharacterSheetLinkRepository links, bool enabled = false, int maxOffline = 2)
            => new CharacterSheetService(world, links, () => enabled, () => false, () => "char.example.com", () => 120,
                () => new DateTime(2026, 10, 7, 0, 0, 0, DateTimeKind.Utc), maxOffline, 2500, () => ms);

        [TestMethod]
        public void Service_OwnerProfile_IgnoresTheOptInFlagAndTheCache_AndAsksForTheOwnerView()
        {
            var world = new FakeCharacterSheetWorld();
            world.PresenceOf[CharacterGuid] = CharacterPresence.Offline;
            var links = new FakeCharacterSheetLinkRepository();
            var service = Service(world, links, enabled: false);

            var result = service.GetOwnerProfile(CharacterGuid);

            Assert.AreEqual(SheetOutcome.Ok, result.Outcome);
            Assert.AreEqual(CharacterGuid, result.Profile.CharacterGuid);
            Assert.IsFalse(result.Profile.Online);
            Assert.AreEqual(true, world.LastOwnerView);
            Assert.AreEqual(0, world.RankBuildCount, "no rank snapshot for the owner view");
            Assert.AreEqual(0, service.CachedCount, "the public sheet cache is never touched");
        }

        [TestMethod]
        public void Service_OwnerProfile_OnlineCharacter_IsMarkedOnline()
        {
            var world = new FakeCharacterSheetWorld();
            world.PresenceOf[CharacterGuid] = CharacterPresence.Online;

            var result = Service(world, new FakeCharacterSheetLinkRepository()).GetOwnerProfile(CharacterGuid);

            Assert.AreEqual(SheetOutcome.Ok, result.Outcome);
            Assert.IsTrue(result.Profile.Online);
            Assert.AreEqual(1, world.OnlineBuildCount);
        }

        [TestMethod]
        public void Service_OwnerProfile_MissingCharacter_IsNotFound_AndBuildsNothing()
        {
            var world = new FakeCharacterSheetWorld();

            var result = Service(world, new FakeCharacterSheetLinkRepository()).GetOwnerProfile(CharacterGuid);

            Assert.AreEqual(SheetOutcome.NotFound, result.Outcome);
            Assert.AreEqual(0, world.OnlineBuildCount + world.OfflineBuildCount);
        }

        [TestMethod]
        public void Service_OwnerProfile_NullBuild_IsBusy()
        {
            var world = new FakeCharacterSheetWorld { OfflineReturnsNull = true };
            world.PresenceOf[CharacterGuid] = CharacterPresence.Offline;

            Assert.AreEqual(SheetOutcome.Busy, Service(world, new FakeCharacterSheetLinkRepository()).GetOwnerProfile(CharacterGuid).Outcome);
        }

        [TestMethod]
        public void Service_OwnerProfile_ThrowingWorld_IsFailed()
        {
            var world = new FakeCharacterSheetWorld { ThrowOnPresence = true };

            Assert.AreEqual(SheetOutcome.Failed, Service(world, new FakeCharacterSheetLinkRepository()).GetOwnerProfile(CharacterGuid).Outcome);
        }

        [TestMethod]
        public void Service_OwnerProfile_RespectsTheOfflineBuildCap()
        {
            var world = new FakeCharacterSheetWorld { BlockOfflineBuilds = true };
            world.PresenceOf[CharacterGuid] = CharacterPresence.Offline;
            var service = Service(world, new FakeCharacterSheetLinkRepository(), maxOffline: 1);

            var first = Task.Run(() => service.GetOwnerProfile(CharacterGuid));
            Assert.IsTrue(world.OfflineBuildStarted.Wait(TimeSpan.FromSeconds(10)), "the first offline build never started");

            try
            {
                Assert.AreEqual(SheetOutcome.Busy, service.GetOwnerProfile(CharacterGuid).Outcome, "the cap is shared with the public sheet path");
                Assert.AreEqual(1, world.OfflineBuildCount, "the refused request must not reach the world");
            }
            finally
            {
                world.ReleaseOfflineBuilds();
            }

            Assert.AreEqual(SheetOutcome.Ok, first.Result.Outcome);
        }

        // ---- the owner view through the live world: a public request never joins an owner-view load ----

        [TestMethod]
        public void LiveWorld_OwnerViewAndPublicLoads_AreSeparateInFlightEntries()
        {
            var started = new List<(uint guid, Action<CharacterSheet> callback)>();

            var world = new LiveCharacterSheetWorld(
                _ => { },
                () => new Dictionary<uint, List<SheetRank>>(),
                (guid, project, callback) => started.Add((guid, callback)),
                () => 0L);

            var publicLoad = Task.Run(() => world.BuildOffline(CharacterGuid, false, TimeSpan.FromSeconds(5)));
            SpinWait.SpinUntil(() => started.Count >= 1, TimeSpan.FromSeconds(5));
            var ownerLoad = Task.Run(() => world.BuildOffline(CharacterGuid, false, TimeSpan.FromSeconds(5), ownerView: true));
            SpinWait.SpinUntil(() => started.Count >= 2, TimeSpan.FromSeconds(5));

            Assert.AreEqual(2, started.Count, "an owner-view request must start its own load, not join the public one");
            Assert.AreEqual(2, world.OutstandingOfflineLoads);

            started[0].callback(new CharacterSheet { Name = "public" });
            started[1].callback(new CharacterSheet { Name = "owner", Owner = new OwnerProfile { Name = "owner" } });

            Assert.AreEqual("public", publicLoad.Result.Name);
            Assert.IsNull(publicLoad.Result.Owner);
            Assert.AreEqual("owner", ownerLoad.Result.Owner.Name);
        }

        // ---- the public sheet JSON must not grow the owner extras ----

        [TestMethod]
        public void PublicSheetJson_NeverCarriesTheOwnerExtras()
        {
            var sheet = new CharacterSheet { Name = "x", Owner = new OwnerProfile { Name = "x", Heritage = "Aluvian" } };

            var text = JsonSerializer.Serialize(sheet, MarketApiHost.Json);

            Assert.IsFalse(text.Contains("owner", StringComparison.OrdinalIgnoreCase), text);
            Assert.IsFalse(text.Contains("heritage", StringComparison.OrdinalIgnoreCase), text);
        }
    }
}
