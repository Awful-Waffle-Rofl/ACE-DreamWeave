using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database;
using ACE.Database.Models.Auth;
using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.Command;
using ACE.Server.Command.Web;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Managers.CharacterSheets;
using ACE.Server.Managers.Market;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// MarketApiHost end to end over a real loopback Kestrel, with fakes behind every seam.
    /// Deliberately NOT WebApplicationFactory: binding port 0 exercises the real key check, gate
    /// and limiter for one fewer package.
    /// </summary>
    [TestClass]
    public class MarketApiTests
    {
        private const string Key = "test-shared-key";
        private const uint AccountId = 9101;
        private const uint SellerAccount = 9102;
        private const uint CharacterGuid = 0x50000301;
        private const uint SellerCharacter = 0x50000302;

        /// <summary>A character on a THIRD account: neither the session's nor the listing seller's.</summary>
        private const uint VictimCharacter = 0x50000303;

        private static readonly int Granite = (int)MaterialType.Granite;

        /// <summary>Any wcid: MarketSalvageMaterials.IsFullBagOf keys on MaterialType and Structure, never on wcid. Own range, clear of every other test class's.</summary>
        private const uint GraniteBagWcid = 91201;

        private FakeMarketRepository repo;
        private FakeMarketItemStore store;
        private FakeMarketWallet wallet;
        private FakeCharacterSheetService sheets;
        private HttpClient client;

        // ---- the Player static-initializer trap (see MarketSalvageMaterialsTests.cs) ----
        // Referencing MarketSalvageMaterials (via /v1/materials or PlaceOrder) forces Player's own
        // static type initializer to run, which unconditionally calls DatabaseManager.World for
        // "portalmarketplace" and 10 PK-arena portal names. With no live world database this NREs
        // unless those weenie names are seeded into the world cache first.
        private static uint nextWcid = 91200;
        private static bool playerStaticFieldsSeeded;

        private static void EnsurePlayerStaticFieldsSeeded()
        {
            if (playerStaticFieldsSeeded)
                return;

            var cacheField = typeof(WorldDatabaseWithEntityCache).GetField("weenieCache", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(cacheField, "WorldDatabaseWithEntityCache.weenieCache was not found by reflection - has it been renamed?");
            var nameField = typeof(WorldDatabaseWithEntityCache).GetField("weenieClassNameToClassIdCache", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(nameField, "WorldDatabaseWithEntityCache.weenieClassNameToClassIdCache was not found by reflection - has it been renamed?");

            var cache = (ConcurrentDictionary<uint, Weenie>)cacheField.GetValue(DatabaseManager.World);
            var nameCache = (ConcurrentDictionary<string, uint>)nameField.GetValue(DatabaseManager.World);

            foreach (var name in new[]
            {
                "portalmarketplace",
                "portalpkarenanew1", "portalpkarenanew2", "portalpkarenanew3", "portalpkarenanew4", "portalpkarenanew5",
                "portalpklarenanew1", "portalpklarenanew2", "portalpklarenanew3", "portalpklarenanew4", "portalpklarenanew5",
            })
            {
                var wcid = ++nextWcid;
                cache[wcid] = new Weenie { WeenieClassId = wcid, WeenieType = WeenieType.Generic };
                nameCache[name.ToLower()] = wcid;
            }

            playerStaticFieldsSeeded = true;
        }

        /// <summary>A full granite salvage bag, matching MarketOrderFillTests' Bag() shape.</summary>
        private static WorldObject GraniteBag(int workmanship = 4)
        {
            var bag = FakeVaultWorld.MakeSalvageBag(GraniteBagWcid, 100, workmanship * 10, 10, 100);
            bag.MaterialType = MaterialType.Granite;
            return bag;
        }

        private Func<uint, Weenie> savedWeenieLookup;

        [TestInitialize]
        public void Setup()
        {
            EnsurePlayerStaticFieldsSeeded();

            repo = new FakeMarketRepository();
            store = new FakeMarketItemStore();
            wallet = new FakeMarketWallet();
            sheets = new FakeCharacterSheetService();

            MarketManagerTests.SeedMarketTunables();

            // The default WeenieLookup calls DatabaseManager.World, which NREs with no live world
            // database; every material/order test resolves a weenie through it (MarketSalvageMaterials
            // and the ledger snapshot path both go through MarketManager.WeenieLookup). Same fix as
            // MarketOrderFillTests and MarketBuyOrderTests.
            savedWeenieLookup = MarketManager.WeenieLookup;
            MarketManager.WeenieLookup = _ => null;

            MarketManager.Initialize(store, wallet, repo);
            wallet.Seed(CharacterGuid, 1000);

            MarketApiHost.Start(new MarketApiOptions
            {
                ListenUrl = "http://127.0.0.1:0",
                SharedKey = Key,
                MaxInFlight = 8,
                WriteRatePerMinute = 20,
                RequestTimeoutMs = 3000,
                LoginRatePerMinutePerAccount = 5,
                LoginRatePerMinutePerIp = 30,
                Wallet = wallet,
                CharacterSheets = sheets,
                SheetMaxInFlight = 4,
                AuthenticateAccount = (name, password) =>
                    name == "tester" && password == "correct-horse" ? AccountId : 0u,
                ListCharacters = accountId => accountId == AccountId
                    ? new List<(uint, string)> { (CharacterGuid, "Marketbuyer") }
                    : new List<(uint, string)>(),
            });

            client = new HttpClient { BaseAddress = new Uri(MarketApiHost.ListeningUrl) };
        }

        [TestCleanup]
        public void Teardown()
        {
            foreach (var gate in runGates)
                gate.Set();

            foreach (var dispatcher in runDispatchers)
                dispatcher.Dispose();

            client?.Dispose();
            MarketApiHost.Stop();
            MarketManager.Shutdown();
            MarketManager.WeenieLookup = savedWeenieLookup;
        }

        private HttpRequestMessage Request(HttpMethod method, string path, string key = Key, string bearer = null, string body = null)
        {
            var request = new HttpRequestMessage(method, path);

            if (key != null)
                request.Headers.Add("X-Market-Key", key);

            if (bearer != null)
                request.Headers.Add("Authorization", $"Bearer {bearer}");

            if (body != null)
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");

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
            var (status, json) = await Send(Request(HttpMethod.Post, "/v1/session",
                body: "{\"account_name\":\"tester\",\"password\":\"correct-horse\"}"));

            Assert.AreEqual(HttpStatusCode.OK, status);
            return json.GetProperty("session_token").GetString();
        }

        /// <summary>The raw HttpResponseMessage, for a test that needs the response body's byte length or headers rather than the parsed JSON.</summary>
        private async Task<HttpResponseMessage> Raw(HttpMethod method, string path, string key = Key, string bearer = null, string json = null) =>
            await client.SendAsync(Request(method, path, key: key, bearer: bearer, body: json));

        private async Task AssertError(string path, HttpStatusCode status, string code)
        {
            var (actualStatus, json) = await Send(Request(HttpMethod.Get, path));
            Assert.AreEqual(status, actualStatus);
            Assert.AreEqual(code, json.GetProperty("error").GetString());
        }

        [TestMethod]
        public async Task Health_RequiresTheSharedKeyAndReturnsNoBodyWithoutIt()
        {
            var (withKey, _) = await Send(Request(HttpMethod.Get, "/v1/health"));
            Assert.AreEqual(HttpStatusCode.OK, withKey);

            using var noKey = await client.SendAsync(Request(HttpMethod.Get, "/v1/health", key: null));
            Assert.AreEqual(HttpStatusCode.Unauthorized, noKey.StatusCode);
            Assert.AreEqual(0, (await noKey.Content.ReadAsByteArrayAsync()).Length,
                "a wrong or missing key is dropped WITHOUT a body: an error envelope tells a scanner the endpoint is real");

            using var wrongKey = await client.SendAsync(Request(HttpMethod.Get, "/v1/health", key: "not-the-key"));
            Assert.AreEqual(HttpStatusCode.Unauthorized, wrongKey.StatusCode);
        }

        [TestMethod]
        public async Task Session_ReturnsATokenAndTheAccountCharactersWithBalances()
        {
            var (status, json) = await Send(Request(HttpMethod.Post, "/v1/session",
                body: "{\"account_name\":\"tester\",\"password\":\"correct-horse\"}"));

            Assert.AreEqual(HttpStatusCode.OK, status);
            Assert.AreEqual((int)AccountId, json.GetProperty("account_id").GetInt32());
            Assert.IsFalse(string.IsNullOrWhiteSpace(json.GetProperty("session_token").GetString()));

            var characters = json.GetProperty("characters").EnumerateArray().ToList();
            Assert.AreEqual(1, characters.Count);
            Assert.AreEqual("Marketbuyer", characters[0].GetProperty("name").GetString());
            Assert.AreEqual(1000, characters[0].GetProperty("balance_mmd").GetInt64());
        }

        [TestMethod]
        public async Task AccountsMe_IsTheSessionResponseMinusTheToken()
        {
            var token = await LoginAsync();

            var (status, json) = await Send(Request(HttpMethod.Get, "/v1/accounts/me", bearer: token));

            Assert.AreEqual(HttpStatusCode.OK, status);
            Assert.AreEqual((int)AccountId, json.GetProperty("account_id").GetInt32());

            Assert.IsFalse(json.TryGetProperty("session_token", out _),
                "the refresh must not mint or echo a token; it is the session response MINUS the token");

            var characters = json.GetProperty("characters").EnumerateArray().ToList();
            Assert.AreEqual(1, characters.Count);
            Assert.AreEqual((int)CharacterGuid, characters[0].GetProperty("guid").GetInt32());
            Assert.AreEqual("Marketbuyer", characters[0].GetProperty("name").GetString());
            Assert.AreEqual(1000, characters[0].GetProperty("balance_mmd").GetInt64());
        }

        [TestMethod]
        public async Task AccountsMe_RereadsTheBalanceEveryCall()
        {
            var token = await LoginAsync();

            wallet.Seed(CharacterGuid, 250);

            var (status, json) = await Send(Request(HttpMethod.Get, "/v1/accounts/me", bearer: token));

            Assert.AreEqual(HttpStatusCode.OK, status);
            Assert.AreEqual(250, json.GetProperty("characters").EnumerateArray().Single().GetProperty("balance_mmd").GetInt64(),
                "the whole point of the route is that it reflects the CURRENT balance, not the one login saw");
        }

        [TestMethod]
        public async Task AccountsMe_RequiresABearerToken()
        {
            using var noBearer = await client.SendAsync(Request(HttpMethod.Get, "/v1/accounts/me"));
            Assert.AreEqual(HttpStatusCode.Unauthorized, noBearer.StatusCode);

            using var noKey = await client.SendAsync(Request(HttpMethod.Get, "/v1/accounts/me", key: null));
            Assert.AreEqual(HttpStatusCode.Unauthorized, noKey.StatusCode);
            Assert.AreEqual(0, (await noKey.Content.ReadAsByteArrayAsync()).Length,
                "a wrong or missing key is dropped WITHOUT a body, like every other route");
        }

        [TestMethod]
        public async Task Session_WithABadPassword_IsBadCredentialsAndNeverSaysWhichHalfWasWrong()
        {
            var (status, json) = await Send(Request(HttpMethod.Post, "/v1/session",
                body: "{\"account_name\":\"tester\",\"password\":\"wrong\"}"));

            Assert.AreEqual(HttpStatusCode.Unauthorized, status);
            Assert.AreEqual("bad_credentials", json.GetProperty("error").GetString());

            var (unknownStatus, unknownJson) = await Send(Request(HttpMethod.Post, "/v1/session",
                body: "{\"account_name\":\"nobody\",\"password\":\"wrong\"}"));

            Assert.AreEqual(HttpStatusCode.Unauthorized, unknownStatus);
            Assert.AreEqual("bad_credentials", unknownJson.GetProperty("error").GetString(),
                "an unknown account and a wrong password must be indistinguishable, or the endpoint enumerates accounts");
        }

        [TestMethod]
        public async Task PlayerRoutes_RequireABearerToken()
        {
            using var noBearer = await client.SendAsync(Request(HttpMethod.Get, "/v1/accounts/me/vault"));
            Assert.AreEqual(HttpStatusCode.Unauthorized, noBearer.StatusCode);

            using var badBearer = await client.SendAsync(
                Request(HttpMethod.Get, "/v1/accounts/me/vault", bearer: "not-a-real-token"));
            Assert.AreEqual(HttpStatusCode.Unauthorized, badBearer.StatusCode);

            var token = await LoginAsync();
            var (status, _) = await Send(Request(HttpMethod.Get, "/v1/accounts/me/vault", bearer: token));
            Assert.AreEqual(HttpStatusCode.OK, status);
        }

        [TestMethod]
        public async Task Listings_CreateAndDelete_RoundTripThroughTheManager()
        {
            var item = FakeVaultWorld.MakeStack(9001, 1, 1);
            store.SeedItem(AccountId, item);

            var token = await LoginAsync();

            var (created, createdJson) = await Send(Request(HttpMethod.Post, "/v1/listings", bearer: token,
                body: $"{{\"item_guid\":{item.Guid.Full},\"price_mmd\":25,\"seller_character_guid\":{CharacterGuid}}}"));

            Assert.AreEqual(HttpStatusCode.OK, created);
            var listingId = createdJson.GetProperty("id").GetUInt32();
            Assert.AreEqual(25, createdJson.GetProperty("price_mmd").GetInt64());
            Assert.AreEqual("active", createdJson.GetProperty("status").GetString());

            var (deleted, deletedJson) = await Send(Request(HttpMethod.Delete, $"/v1/listings/{listingId}", bearer: token));

            Assert.AreEqual(HttpStatusCode.OK, deleted);
            Assert.AreEqual("delisted", deletedJson.GetProperty("status").GetString());
        }

        [TestMethod]
        public async Task Buy_AtAStalePrice_Returns409WithTheLivePrice()
        {
            var item = FakeVaultWorld.MakeStack(9001, 1, 1);
            store.SeedItem(SellerAccount, item);

            var seller = new MarketActor(SellerAccount, SellerCharacter, "Marketseller");
            var listing = MarketManager.List(seller, item.Guid.Full, 9001, 1, 30, MarketChannel.Web).Value;

            var token = await LoginAsync();

            var (status, json) = await Send(Request(HttpMethod.Post, $"/v1/listings/{listing.Id}/buy", bearer: token,
                body: $"{{\"buyer_character_guid\":{CharacterGuid},\"count\":1,\"expected_price_mmd\":10}}"));

            Assert.AreEqual(HttpStatusCode.Conflict, status);
            Assert.AreEqual("price_changed", json.GetProperty("error").GetString());
            Assert.AreEqual(30, json.GetProperty("current_price_mmd").GetInt64(),
                "the envelope must carry the live price so the web app can offer the real number");
        }

        [TestMethod]
        public async Task Feeds_AreCursorPagedAndCarryTheGeneration()
        {
            var item = FakeVaultWorld.MakeStack(9001, 1, 1);
            store.SeedItem(SellerAccount, item);

            var seller = new MarketActor(SellerAccount, SellerCharacter, "Marketseller");
            MarketManager.List(seller, item.Guid.Full, 9001, 1, 5, MarketChannel.Web);

            var (status, json) = await Send(Request(HttpMethod.Get, "/v1/listings/changes?since=0"));

            Assert.AreEqual(HttpStatusCode.OK, status);
            Assert.AreEqual(MarketManager.FeedGeneration, json.GetProperty("generation").GetString(),
                "a client whose generation differs must resync from 0, because sequence numbers do not survive a restart");
            Assert.AreEqual(1, json.GetProperty("listings").GetArrayLength());
            Assert.IsTrue(json.GetProperty("next_since").GetInt64() > 0);

            // Reading again from the returned cursor must be empty: the cursor is exclusive.
            var next = json.GetProperty("next_since").GetInt64();
            var (_, second) = await Send(Request(HttpMethod.Get, $"/v1/listings/changes?since={next}"));
            Assert.AreEqual(0, second.GetProperty("listings").GetArrayLength());

            var (historyStatus, historyJson) = await Send(Request(HttpMethod.Get, "/v1/history/changes?since=0"));
            Assert.AreEqual(HttpStatusCode.OK, historyStatus);
            Assert.AreEqual(MarketManager.FeedGeneration, historyJson.GetProperty("generation").GetString());
        }

        [TestMethod]
        public async Task Feeds_ClampTheirPageSizeToMaxFeedRows()
        {
            var (status, json) = await Send(Request(HttpMethod.Get, "/v1/listings/changes?since=0&limit=100000"));

            Assert.AreEqual(HttpStatusCode.OK, status);
            Assert.IsTrue(json.GetProperty("limit").GetInt32() <= MarketManager.MaxFeedRows);
        }

        [TestMethod]
        public async Task WriteRateLimit_Returns429AfterTheBucketEmpties()
        {
            MarketApiHost.Stop();
            MarketApiHost.Start(new MarketApiOptions
            {
                ListenUrl = "http://127.0.0.1:0",
                SharedKey = Key,
                MaxInFlight = 8,
                WriteRatePerMinute = 2,
                RequestTimeoutMs = 3000,
                LoginRatePerMinutePerAccount = 50,
                LoginRatePerMinutePerIp = 500,
                Wallet = wallet,
                AuthenticateAccount = (name, password) => name == "tester" && password == "correct-horse" ? AccountId : 0u,
                ListCharacters = _ => new List<(uint, string)> { (CharacterGuid, "Marketbuyer") },
            });

            client.Dispose();
            client = new HttpClient { BaseAddress = new Uri(MarketApiHost.ListeningUrl) };

            var token = await LoginAsync();

            HttpStatusCode last = HttpStatusCode.OK;

            for (var i = 0; i < 4; i++)
            {
                var (status, _) = await Send(Request(HttpMethod.Delete, "/v1/listings/999999", bearer: token));
                last = status;
            }

            Assert.AreEqual((HttpStatusCode)429, last,
                "the per-account write bucket must refuse rather than queue work into the world");
        }

        [TestMethod]
        public async Task LoginRateLimit_Returns429PerAccount()
        {
            MarketApiHost.Stop();
            MarketApiHost.Start(new MarketApiOptions
            {
                ListenUrl = "http://127.0.0.1:0",
                SharedKey = Key,
                MaxInFlight = 8,
                WriteRatePerMinute = 100,
                RequestTimeoutMs = 3000,
                LoginRatePerMinutePerAccount = 2,
                LoginRatePerMinutePerIp = 500,
                Wallet = wallet,
                AuthenticateAccount = (name, password) => name == "tester" && password == "correct-horse" ? AccountId : 0u,
                ListCharacters = _ => new List<(uint, string)> { (CharacterGuid, "Marketbuyer") },
            });

            client.Dispose();
            client = new HttpClient { BaseAddress = new Uri(MarketApiHost.ListeningUrl) };

            HttpStatusCode last = HttpStatusCode.OK;

            for (var i = 0; i < 5; i++)
            {
                var (status, _) = await Send(Request(HttpMethod.Post, "/v1/session",
                    body: "{\"account_name\":\"tester\",\"password\":\"wrong\"}"));
                last = status;
            }

            Assert.AreEqual((HttpStatusCode)429, last,
                "the login bucket must count FAILED attempts, or it does not blunt a password guess at all");
        }

        [TestMethod]
        public async Task Timeout_Returns503_AndTheEnqueuedWorkStillFinishes()
        {
            MarketApiHost.Stop();
            MarketApiHost.Start(new MarketApiOptions
            {
                ListenUrl = "http://127.0.0.1:0",
                SharedKey = Key,
                MaxInFlight = 8,
                WriteRatePerMinute = 100,
                RequestTimeoutMs = 50,
                LoginRatePerMinutePerAccount = 50,
                LoginRatePerMinutePerIp = 500,
                Wallet = wallet,
                AuthenticateAccount = (name, password) => name == "tester" && password == "correct-horse" ? AccountId : 0u,
                ListCharacters = _ => new List<(uint, string)> { (CharacterGuid, "Marketbuyer") },
            });

            client.Dispose();
            client = new HttpClient { BaseAddress = new Uri(MarketApiHost.ListeningUrl) };

            var item = FakeVaultWorld.MakeStack(9001, 1, 1);
            store.SeedItem(AccountId, item);

            // The fake store sleeps on every store touch, so the LIST call outruns the 50 ms budget.
            store.StoreDelayMs = 400;

            var token = await LoginAsync();

            var (status, json) = await Send(Request(HttpMethod.Post, "/v1/listings", bearer: token,
                body: $"{{\"item_guid\":{item.Guid.Full},\"price_mmd\":25,\"seller_character_guid\":{CharacterGuid}}}"));

            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
            Assert.AreEqual("timeout", json.GetProperty("error").GetString());

            // The point of the design: the client gave up, the WORK did not. It still lands.
            await Task.Delay(800);
            Assert.AreEqual(1, repo.Listings.Count,
                "on timeout the client gets 503 and the enqueued work still completes on its own");
        }

        [TestMethod]
        public async Task InFlightGate_Returns429Immediately_AndNeverQueuesIntoTheWorld()
        {
            MarketApiHost.Stop();
            MarketApiHost.Start(new MarketApiOptions
            {
                ListenUrl = "http://127.0.0.1:0",
                SharedKey = Key,
                MaxInFlight = 1,
                WriteRatePerMinute = 1000,
                // Generous on purpose: the slow request is HELD, not timed, and if this budget ran
                // out before the probe landed the 503 would release the slot and the probe would
                // see 200 for a reason that has nothing to do with the gate.
                RequestTimeoutMs = 30000,
                LoginRatePerMinutePerAccount = 1000,
                LoginRatePerMinutePerIp = 5000,
                Wallet = wallet,
                AuthenticateAccount = (name, password) => name == "tester" && password == "correct-horse" ? AccountId : 0u,
                ListCharacters = _ => new List<(uint, string)> { (CharacterGuid, "Marketbuyer") },
            });

            client.Dispose();
            client = new HttpClient { BaseAddress = new Uri(MarketApiHost.ListeningUrl) };

            var token = await LoginAsync();

            // Hold the store open rather than sleeping in it. A 400 ms sleep raced twice on CI: once
            // ahead of a cold Kestrel host, and once because the thread pool was starved and this
            // method's own continuation took longer than the sleep, so the "slow" request had already
            // finished when the probe was sent (expected TooManyRequests, actual OK). With a hold the
            // slot stays taken until this test says otherwise, however slow the runner is.
            using var hold = new System.Threading.ManualResetEventSlim(false);
            store.StoreHold = hold;

            var item = FakeVaultWorld.MakeStack(9002, 1, 1);
            store.SeedItem(AccountId, item);

            var slow = client.SendAsync(Request(HttpMethod.Post, "/v1/listings", bearer: token,
                body: $"{{\"item_guid\":{item.Guid.Full},\"price_mmd\":25,\"seller_character_guid\":{CharacterGuid}}}"));

            try
            {
                // Rendezvous with the in-flight request actually being inside the store (and so
                // holding the in-flight slot).
                await store.DelayEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

                // The probe is refused at the gate, before it ever reaches the store, so the hold
                // cannot block it.
                var (status, json) = await Send(Request(HttpMethod.Get, "/v1/accounts/me/vault", bearer: token));

                Assert.AreEqual((HttpStatusCode)429, status);
                Assert.AreEqual("rate_limited", json.GetProperty("error").GetString());
            }
            finally
            {
                hold.Set();
                store.StoreHold = null;
            }

            using var _ = await slow;
        }

        [TestMethod]
        public async Task Buy_WithAnotherAccountsCharacter_IsRefusedAndSpendsNobodysBank()
        {
            var item = FakeVaultWorld.MakeStack(9001, 1, 1);
            store.SeedItem(SellerAccount, item);
            wallet.Seed(VictimCharacter, 500);

            var seller = new MarketActor(SellerAccount, SellerCharacter, "Marketseller");
            var listing = MarketManager.List(seller, item.Guid.Full, 9001, 1, 30, MarketChannel.Web).Value;

            var token = await LoginAsync();

            // The session is account 9101; VictimCharacter belongs to neither it nor the seller. The
            // public history feed publishes buyer/seller guids, so this list is not secret.
            var (status, json) = await Send(Request(HttpMethod.Post, $"/v1/listings/{listing.Id}/buy", bearer: token,
                body: $"{{\"buyer_character_guid\":{VictimCharacter},\"count\":1,\"expected_price_mmd\":30}}"));

            Assert.AreEqual(HttpStatusCode.Forbidden, status);
            Assert.AreEqual("not_owner", json.GetProperty("error").GetString());

            Assert.AreEqual(500, wallet.GetBalanceMmd(VictimCharacter),
                "the refusal must land BEFORE the debit, or the market spends a stranger's bank");
            Assert.AreEqual(0, wallet.DebitCalls, "no debit may be attempted at all");
            Assert.AreEqual(0, repo.Transactions.Count, "no transaction row may be written");
            Assert.AreEqual(MarketListingStatus.Active, MarketManager.GetListing(listing.Id).Status);
            Assert.IsFalse(store.Items.TryGetValue(AccountId, out var delivered) && delivered.Count > 0,
                "nothing may be delivered to the caller's vault");
        }

        [TestMethod]
        public async Task List_WithAnotherAccountsCharacter_IsRefusedAndCreatesNothing()
        {
            var item = FakeVaultWorld.MakeStack(9001, 1, 1);
            store.SeedItem(AccountId, item);

            var token = await LoginAsync();

            // Own item, but published under a character the session does not hold: the feed would
            // carry that name and the sale would credit that stranger's bank.
            var (status, json) = await Send(Request(HttpMethod.Post, "/v1/listings", bearer: token,
                body: $"{{\"item_guid\":{item.Guid.Full},\"price_mmd\":25,\"seller_character_guid\":{VictimCharacter}}}"));

            Assert.AreEqual(HttpStatusCode.Forbidden, status);
            Assert.AreEqual("not_owner", json.GetProperty("error").GetString());

            Assert.AreEqual(0, repo.Listings.Count, "no listing row may be written");
            Assert.AreEqual(0, repo.AddListingCalls, "the manager must never be reached");
        }

        [TestMethod]
        public async Task CharacterOwnership_FailsClosedWhenTheRosterCannotBeResolved()
        {
            MarketApiHost.Stop();
            MarketApiHost.Start(new MarketApiOptions
            {
                ListenUrl = "http://127.0.0.1:0",
                SharedKey = Key,
                MaxInFlight = 8,
                WriteRatePerMinute = 100,
                RequestTimeoutMs = 3000,
                LoginRatePerMinutePerAccount = 50,
                LoginRatePerMinutePerIp = 500,
                Wallet = wallet,
                AuthenticateAccount = (name, password) => name == "tester" && password == "correct-horse" ? AccountId : 0u,
                ListCharacters = null,
            });

            client.Dispose();
            client = new HttpClient { BaseAddress = new Uri(MarketApiHost.ListeningUrl) };

            var item = FakeVaultWorld.MakeStack(9001, 1, 1);
            store.SeedItem(AccountId, item);

            var token = await LoginAsync();

            var (status, json) = await Send(Request(HttpMethod.Post, "/v1/listings", bearer: token,
                body: $"{{\"item_guid\":{item.Guid.Full},\"price_mmd\":25,\"seller_character_guid\":{CharacterGuid}}}"));

            Assert.AreEqual(HttpStatusCode.Forbidden, status);
            Assert.AreEqual("not_owner", json.GetProperty("error").GetString(),
                "an unresolvable roster cannot prove ownership, so it must refuse rather than wave the write through");
            Assert.AreEqual(0, repo.Listings.Count);
        }

        [TestMethod]
        public async Task CreateListing_WithNoPriceField_IsRefusedRatherThanTreatedAsFree()
        {
            // An omitted price and a chosen price of zero must never be the same request. Without
            // this guard a client that forgets the field gives the item away.
            var item = FakeVaultWorld.MakeStack(9001, 1, 1);
            store.SeedItem(AccountId, item);

            var token = await LoginAsync();

            var (status, json) = await Send(Request(HttpMethod.Post, "/v1/listings", bearer: token,
                body: $"{{\"item_guid\":{item.Guid.Full},\"wcid\":9001,\"count\":1,\"seller_character_guid\":{CharacterGuid}}}"));

            Assert.AreEqual(HttpStatusCode.BadRequest, status);
            Assert.AreEqual("invalid_price", json.GetProperty("error").GetString());
            Assert.AreEqual(0, repo.Listings.Count, "no listing row may be written for a body with no price");
        }

        [TestMethod]
        public async Task CreateListing_WithAnExplicitZeroPrice_IsAccepted()
        {
            var item = FakeVaultWorld.MakeStack(9001, 1, 1);
            store.SeedItem(AccountId, item);

            var token = await LoginAsync();

            var (status, json) = await Send(Request(HttpMethod.Post, "/v1/listings", bearer: token,
                body: $"{{\"item_guid\":{item.Guid.Full},\"wcid\":9001,\"count\":1,\"price_mmd\":0,\"seller_character_guid\":{CharacterGuid}}}"));

            Assert.AreEqual(HttpStatusCode.OK, status);
            Assert.AreEqual(0, json.GetProperty("price_mmd").GetInt64(),
                "an explicitly chosen zero is a giveaway and must survive the round trip as zero");
            Assert.AreEqual("active", json.GetProperty("status").GetString());
        }

        [TestMethod]
        public async Task Buy_WithNoExpectedPriceField_IsRefusedAgainstAFreeListing()
        {
            // The staleness check compares expected against live. An absent field deserializing to
            // zero would MATCH a free listing by accident, which is agreement the buyer never gave.
            var item = FakeVaultWorld.MakeStack(9001, 1, 1);
            store.SeedItem(SellerAccount, item);

            var seller = new MarketActor(SellerAccount, SellerCharacter, "Marketseller");
            var listing = MarketManager.List(seller, item.Guid.Full, 9001, 1, 0, MarketChannel.Web).Value;
            Assert.AreEqual(0, listing.PriceMmd, "the listing under test must genuinely be free, or the guard is not what is being measured");

            var token = await LoginAsync();

            var (status, json) = await Send(Request(HttpMethod.Post, $"/v1/listings/{listing.Id}/buy", bearer: token,
                body: $"{{\"buyer_character_guid\":{CharacterGuid},\"count\":1}}"));

            Assert.AreEqual(HttpStatusCode.BadRequest, status);
            Assert.AreEqual("invalid_price", json.GetProperty("error").GetString());
            Assert.AreEqual(MarketListingStatus.Active, MarketManager.GetListing(listing.Id).Status,
                "the free listing must still be there: nobody agreed to take it");
        }

        // ---- POST /v1/accounts/me/vault/barrel ----

        private string BarrelBody(uint itemGuid, int? count = 1, uint? characterGuid = CharacterGuid)
        {
            var parts = new List<string> { $"\"item_guid\":{itemGuid}", "\"wcid\":9001" };

            if (count != null)
                parts.Add($"\"count\":{count.Value}");

            if (characterGuid != null)
                parts.Add($"\"character_guid\":{characterGuid.Value}");

            return "{" + string.Join(",", parts) + "}";
        }

        [TestMethod]
        public async Task Barrel_DestroysAVaultItemAndRemovesItFromTheVaultView()
        {
            var item = FakeVaultWorld.MakeStack(9001, 1, 1);
            store.SeedItem(AccountId, item);

            var token = await LoginAsync();

            var (status, json) = await Send(Request(HttpMethod.Post, "/v1/accounts/me/vault/barrel", bearer: token,
                body: BarrelBody(item.Guid.Full)));

            Assert.AreEqual(HttpStatusCode.OK, status);

            // The response IS the updated vault, so a client re-renders from one answer rather than
            // a follow-up read that could interleave with another window's write.
            Assert.AreEqual(0, json.GetProperty("entries").GetArrayLength(), "the barreled item is gone from the response");

            var (vaultStatus, vaultJson) = await Send(Request(HttpMethod.Get, "/v1/accounts/me/vault", bearer: token));

            Assert.AreEqual(HttpStatusCode.OK, vaultStatus);
            Assert.AreEqual(0, vaultJson.GetProperty("entries").GetArrayLength(), "and gone from a fresh read too");

            Assert.AreEqual(1, store.Barreled.Count);
            Assert.AreEqual(item.Guid.Full, store.Barreled[0].itemGuid);
        }

        [TestMethod]
        public async Task Barrel_AListedItem_IsRefusedWith409()
        {
            var item = FakeVaultWorld.MakeStack(9001, 1, 1);
            store.SeedItem(AccountId, item);

            var owner = new MarketActor(AccountId, CharacterGuid, "Marketbuyer");
            var listing = MarketManager.List(owner, item.Guid.Full, 9001, 1, 25, MarketChannel.Web);
            Assert.IsTrue(listing.Ok, "the listing under test must exist, or the guard is not what is being measured");

            var token = await LoginAsync();

            var (status, json) = await Send(Request(HttpMethod.Post, "/v1/accounts/me/vault/barrel", bearer: token,
                body: BarrelBody(item.Guid.Full)));

            Assert.AreEqual(HttpStatusCode.Conflict, status);
            Assert.AreEqual("item_listed", json.GetProperty("error").GetString());
            Assert.AreEqual(0, store.BarrelCalls, "a listed row must not reach the store at all");
        }

        [TestMethod]
        public async Task Barrel_WithNoSessionToken_IsRefused()
        {
            var item = FakeVaultWorld.MakeStack(9001, 1, 1);
            store.SeedItem(AccountId, item);

            var (status, _) = await Send(Request(HttpMethod.Post, "/v1/accounts/me/vault/barrel",
                body: BarrelBody(item.Guid.Full)));

            Assert.AreEqual(HttpStatusCode.Unauthorized, status);
            Assert.AreEqual(0, store.BarrelCalls);
        }

        [TestMethod]
        public async Task Barrel_WithNoCountField_IsRefusedRatherThanTreatedAsOne()
        {
            // This endpoint DESTROYS things. "The client did not say how many" must not silently
            // become a number, for the same reason an absent price is not a price of zero.
            var item = FakeVaultWorld.MakeStack(9001, 1, 1);
            store.SeedItem(AccountId, item);

            var token = await LoginAsync();

            var (status, json) = await Send(Request(HttpMethod.Post, "/v1/accounts/me/vault/barrel", bearer: token,
                body: BarrelBody(item.Guid.Full, count: null)));

            Assert.AreEqual(HttpStatusCode.Conflict, status);
            Assert.AreEqual("count_unavailable", json.GetProperty("error").GetString());
            Assert.AreEqual(0, store.BarrelCalls, "nothing may be destroyed for a body with no count");
        }

        [TestMethod]
        public async Task Barrel_WithNoCharacterGuid_IsRefused()
        {
            // The audit row records WHO destroyed it. Filling that in from the roster would name an
            // arbitrary character in the record of a destruction, so an absent guid is refused.
            var item = FakeVaultWorld.MakeStack(9001, 1, 1);
            store.SeedItem(AccountId, item);

            var token = await LoginAsync();

            var (status, json) = await Send(Request(HttpMethod.Post, "/v1/accounts/me/vault/barrel", bearer: token,
                body: BarrelBody(item.Guid.Full, characterGuid: null)));

            Assert.AreEqual(HttpStatusCode.Forbidden, status);
            Assert.AreEqual("not_owner", json.GetProperty("error").GetString());
            Assert.AreEqual(0, store.BarrelCalls);
        }

        [TestMethod]
        public async Task Barrel_WithAnotherAccountsCharacter_IsRefused()
        {
            var item = FakeVaultWorld.MakeStack(9001, 1, 1);
            store.SeedItem(AccountId, item);

            var token = await LoginAsync();

            var (status, json) = await Send(Request(HttpMethod.Post, "/v1/accounts/me/vault/barrel", bearer: token,
                body: BarrelBody(item.Guid.Full, characterGuid: VictimCharacter)));

            Assert.AreEqual(HttpStatusCode.Forbidden, status);
            Assert.AreEqual("not_owner", json.GetProperty("error").GetString());
            Assert.AreEqual(0, store.BarrelCalls);
        }

        [TestMethod]
        public async Task Barrel_WhenTheBarrelIsUnreachable_Returns503()
        {
            var item = FakeVaultWorld.MakeStack(9001, 1, 1);
            store.SeedItem(AccountId, item);
            store.BarrelUnavailableAccounts.Add(AccountId);

            var token = await LoginAsync();

            var (status, json) = await Send(Request(HttpMethod.Post, "/v1/accounts/me/vault/barrel", bearer: token,
                body: BarrelBody(item.Guid.Full)));

            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
            Assert.AreEqual("barrel_unavailable", json.GetProperty("error").GetString());

            var (_, vaultJson) = await Send(Request(HttpMethod.Get, "/v1/accounts/me/vault", bearer: token));
            Assert.AreEqual(1, vaultJson.GetProperty("entries").GetArrayLength(), "a refusal destroys nothing");
        }

        [TestMethod]
        public async Task Barrel_ALedgerStack_TakesTheCountFromTheLedger()
        {
            store.SeedLedger(AccountId, 9001, 50);

            var token = await LoginAsync();

            var (status, _) = await Send(Request(HttpMethod.Post, "/v1/accounts/me/vault/barrel", bearer: token,
                body: $"{{\"wcid\":9001,\"count\":20,\"character_guid\":{CharacterGuid}}}"));

            Assert.AreEqual(HttpStatusCode.OK, status);
            Assert.AreEqual(30, store.LedgerCount(AccountId, 9001), "only the barreled units leave the ledger");
        }

        // ---- class_key over the wire (counted CLASS lines) ----

        private const string ClassLineA = "0123456789abcdef0123456789abcdef";
        private const string ClassLineB = "fedcba9876543210fedcba9876543210";

        [TestMethod]
        public async Task ClassLine_VaultViewCarriesItsKey_AndAListNamingItIsAClassListing()
        {
            store.SeedClassLine(AccountId, ClassLineA, 9001, 5);

            var token = await LoginAsync();

            var (vaultStatus, vaultJson) = await Send(Request(HttpMethod.Get, "/v1/accounts/me/vault", bearer: token));
            Assert.AreEqual(HttpStatusCode.OK, vaultStatus);

            var row = vaultJson.GetProperty("entries").EnumerateArray().Single();
            Assert.IsTrue(row.GetProperty("is_class").GetBoolean());
            Assert.AreEqual(ClassLineA, row.GetProperty("class_key").GetString(), "the vault view hands the client the id it lists by");

            // wcid omitted, exactly as the frozen contract has a class write send it.
            var (status, json) = await Send(Request(HttpMethod.Post, "/v1/listings", bearer: token,
                body: $"{{\"class_key\":\"{ClassLineA}\",\"count\":3,\"price_mmd\":25,\"seller_character_guid\":{CharacterGuid}}}"));

            Assert.AreEqual(HttpStatusCode.OK, status, json.ToString());
            Assert.AreEqual(ClassLineA, json.GetProperty("class_key").GetString());
            Assert.AreEqual(9001, json.GetProperty("wcid").GetInt32(), "the listing takes the LINE's wcid, not the absent one");
            Assert.AreEqual(3, json.GetProperty("count").GetInt32());
            Assert.IsFalse(json.TryGetProperty("item_guid", out var guid) && guid.ValueKind != JsonValueKind.Null, "a class listing names no biota");

            var listing = MarketManager.GetListing(json.GetProperty("id").GetUInt32());
            Assert.AreEqual(ClassLineA, listing.ClassKey);
            Assert.IsFalse(listing.IsLedger, "a class listing must never be taken for a ledger listing of its wcid");
        }

        [TestMethod]
        public async Task ClassLine_BarrelOverTheWire_DebitsTheLine_AndNeverTheLedgerOfItsWcid()
        {
            store.SeedClassLine(AccountId, ClassLineA, 9001, 5);
            store.SeedLedger(AccountId, 9001, 50);

            var token = await LoginAsync();

            var (status, json) = await Send(Request(HttpMethod.Post, "/v1/accounts/me/vault/barrel", bearer: token,
                body: $"{{\"class_key\":\"{ClassLineA}\",\"wcid\":0,\"count\":2,\"character_guid\":{CharacterGuid}}}"));

            Assert.AreEqual(HttpStatusCode.OK, status, json.ToString());
            Assert.AreEqual(1, store.ClassBarreled.Count);
            Assert.AreEqual((AccountId, ClassLineA, 2), store.ClassBarreled[0]);
            Assert.AreEqual(50, store.LedgerCount(AccountId, 9001), "the ledger of the same wcid is untouched");
            Assert.AreEqual(0, store.Barreled.Count, "nothing went down the guid or ledger arm");
        }

        [TestMethod]
        public async Task ClassLine_BarrelOfAListedLine_Is409ItemListed_AndTheOtherLineIsNotBlocked()
        {
            store.SeedClassLine(AccountId, ClassLineA, 9001, 5);
            store.SeedClassLine(AccountId, ClassLineB, 9001, 5);

            var owner = new MarketActor(AccountId, CharacterGuid, "Marketbuyer");
            Assert.IsTrue(MarketManager.List(owner, null, 0, 2, 25, MarketChannel.Web, ClassLineA).Ok, "the listing under test must exist");

            var token = await LoginAsync();

            var (listed, listedJson) = await Send(Request(HttpMethod.Post, "/v1/accounts/me/vault/barrel", bearer: token,
                body: $"{{\"class_key\":\"{ClassLineA}\",\"wcid\":0,\"count\":1,\"character_guid\":{CharacterGuid}}}"));

            Assert.AreEqual(HttpStatusCode.Conflict, listed);
            Assert.AreEqual("item_listed", listedJson.GetProperty("error").GetString());

            var (other, otherJson) = await Send(Request(HttpMethod.Post, "/v1/accounts/me/vault/barrel", bearer: token,
                body: $"{{\"class_key\":\"{ClassLineB}\",\"wcid\":0,\"count\":1,\"character_guid\":{CharacterGuid}}}"));

            Assert.AreEqual(HttpStatusCode.OK, other, otherJson.ToString());
            Assert.AreEqual(1, store.ClassBarreled.Count);
            Assert.AreEqual(ClassLineB, store.ClassBarreled[0].classKey);
        }

        [TestMethod]
        public async Task ClassKey_MalformedOrWithAGuid_IsItemNotFound_OnListAndBarrel_AndReachesNoStore()
        {
            store.SeedClassLine(AccountId, ClassLineA, 9001, 5);
            store.SeedLedger(AccountId, 9001, 50);

            var token = await LoginAsync();

            foreach (var key in new[] { "0123456789ABCDEF0123456789ABCDEF", "0123", "zz23456789abcdef0123456789abcdef", "" })
            {
                var (listStatus, listJson) = await Send(Request(HttpMethod.Post, "/v1/listings", bearer: token,
                    body: $"{{\"class_key\":\"{key}\",\"wcid\":9001,\"count\":1,\"price_mmd\":25,\"seller_character_guid\":{CharacterGuid}}}"));

                Assert.AreEqual("item_not_found", listJson.GetProperty("error").GetString(), $"list with key '{key}' ({listStatus})");

                var (barrelStatus, barrelJson) = await Send(Request(HttpMethod.Post, "/v1/accounts/me/vault/barrel", bearer: token,
                    body: $"{{\"class_key\":\"{key}\",\"wcid\":9001,\"count\":1,\"character_guid\":{CharacterGuid}}}"));

                Assert.AreEqual("item_not_found", barrelJson.GetProperty("error").GetString(), $"barrel with key '{key}' ({barrelStatus})");
            }

            var (withGuid, withGuidJson) = await Send(Request(HttpMethod.Post, "/v1/listings", bearer: token,
                body: $"{{\"class_key\":\"{ClassLineA}\",\"item_guid\":12345,\"count\":1,\"price_mmd\":25,\"seller_character_guid\":{CharacterGuid}}}"));

            Assert.AreEqual("item_not_found", withGuidJson.GetProperty("error").GetString(), $"a class key WITH a guid names nothing ({withGuid})");

            Assert.AreEqual(0, store.BarrelCalls, "a refused class key must not reach the store");
            Assert.AreEqual(50, store.LedgerCount(AccountId, 9001), "and above all must not fall through to the ledger of the wcid it sent");
            Assert.AreEqual(0, MarketManager.GetActiveListingsForAccount(AccountId).Count, "no listing of any kind was created");
        }

        [TestMethod]
        public async Task NoClassKey_ListingResponseOmitsIt_AndALedgerListingIsUnchanged()
        {
            store.SeedLedger(AccountId, 9001, 50);
            store.SeedClassLine(AccountId, ClassLineA, 9001, 5);

            var token = await LoginAsync();

            var (status, json) = await Send(Request(HttpMethod.Post, "/v1/listings", bearer: token,
                body: $"{{\"wcid\":9001,\"count\":10,\"price_mmd\":25,\"seller_character_guid\":{CharacterGuid}}}"));

            Assert.AreEqual(HttpStatusCode.OK, status, json.ToString());
            Assert.IsFalse(json.TryGetProperty("class_key", out _), "a non-class listing omits class_key (WhenWritingNull), as it omits item_guid");

            var listing = MarketManager.GetListing(json.GetProperty("id").GetUInt32());
            Assert.IsTrue(listing.IsLedger, "a body without class_key is exactly the pre-class ledger listing");
            Assert.IsNull(listing.ClassKey);
        }

        // ---- Wanted buy orders (WANTED-DESIGN section 6) ----

        [TestMethod]
        public async Task Materials_ListsSeventyTwo_WithoutASession()
        {
            var (status, json) = await Send(Request(HttpMethod.Get, "/v1/materials"));
            Assert.AreEqual(HttpStatusCode.OK, status);
            Assert.AreEqual(72, json.GetProperty("materials").GetArrayLength());
            Assert.IsTrue(json.GetProperty("materials")[0].TryGetProperty("material_type", out _));
            Assert.IsTrue(json.GetProperty("materials")[0].TryGetProperty("bag_name", out _));
        }

        /// <summary>
        /// has_hammer tells the web which materials a salvage_hammer order may name, without a second
        /// route. Asserted as an exact set against SalvageForge.MaterialTable, and with Granite pinned
        /// false: a flag that was always true would satisfy a bare "at least one is true" check.
        /// </summary>
        [TestMethod]
        public async Task Materials_CarryHasHammer_ForExactlyTheHammerMaterials()
        {
            var (status, json) = await Send(Request(HttpMethod.Get, "/v1/materials"));
            Assert.AreEqual(HttpStatusCode.OK, status);

            var flagged = json.GetProperty("materials").EnumerateArray()
                              .Where(m => m.GetProperty("has_hammer").GetBoolean())
                              .Select(m => m.GetProperty("material_type").GetInt32())
                              .ToList();

            CollectionAssert.AreEquivalent(SalvageForge.MaterialTable.Keys.Select(m => (int)m).ToList(), flagged);

            var granite = json.GetProperty("materials").EnumerateArray()
                              .Single(m => m.GetProperty("material_type").GetInt32() == (int)MaterialType.Granite);
            Assert.IsFalse(granite.GetProperty("has_hammer").GetBoolean(), "Granite has a bag but no Hammer");
        }

        /// <summary>
        /// The wire round trip for a hammer order: "salvage_hammer" in, "salvage_hammer" and the
        /// HAMMER's wcid back out. The web half matches this shape byte for byte.
        /// </summary>
        [TestMethod]
        public async Task PlaceOrder_HammerKind_RoundTripsOnTheWire()
        {
            var token = await LoginAsync();
            var (status, json) = await Send(Request(HttpMethod.Post, "/v1/orders", bearer: token,
                body: $"{{\"material_type\":{(int)MaterialType.Tourmaline},\"kind\":\"salvage_hammer\",\"count\":1,\"price_mmd\":300,\"buyer_character_guid\":{CharacterGuid}}}"));

            Assert.AreEqual(HttpStatusCode.OK, status);
            Assert.AreEqual("salvage_hammer", json.GetProperty("kind").GetString());
            Assert.AreEqual(1001910, json.GetProperty("wcid").GetInt64(), "the Tourmaline Hammer's wcid, not the bag's");
            Assert.AreEqual((int)MarketBuyOrderKind.SalvageHammer, repo.BuyOrders.Single().OrderKind);
        }

        /// <summary>
        /// An ABSENT kind is the one field on this request allowed to default, and it defaults to
        /// salvage_bag - the only thing an order could be before the field existed, so a client written
        /// against the previous contract keeps posting exactly the orders it always did.
        /// </summary>
        [TestMethod]
        public async Task PlaceOrder_OmittedKind_IsABagOrder()
        {
            var token = await LoginAsync();
            var (status, json) = await Send(Request(HttpMethod.Post, "/v1/orders", bearer: token,
                body: $"{{\"material_type\":{(int)MaterialType.Granite},\"count\":2,\"price_mmd\":10,\"buyer_character_guid\":{CharacterGuid}}}"));

            Assert.AreEqual(HttpStatusCode.OK, status);
            Assert.AreEqual("salvage_bag", json.GetProperty("kind").GetString());
            Assert.AreEqual(0, repo.BuyOrders.Single().OrderKind);
        }

        /// <summary>
        /// A kind the server does not know is a 400 the client can read, NOT a 500. That is why the
        /// field is a string parsed by hand rather than a MarketBuyOrderKind? on the DTO: System.Text.Json
        /// THROWS on an unrecognised enum name, ReadJson catches it into a null body, and the handler's
        /// null-body branch answers server_error. This test fails with 500/server_error if the field is
        /// ever changed to the enum type.
        /// </summary>
        [TestMethod]
        public async Task PlaceOrder_UnknownKind_IsA400_NotA500_AndCreatesNothing()
        {
            var token = await LoginAsync();
            var (status, json) = await Send(Request(HttpMethod.Post, "/v1/orders", bearer: token,
                body: $"{{\"material_type\":{(int)MaterialType.Granite},\"kind\":\"salvage_anvil\",\"count\":2,\"price_mmd\":10,\"buyer_character_guid\":{CharacterGuid}}}"));

            Assert.AreEqual(HttpStatusCode.BadRequest, status);
            Assert.AreEqual("invalid_material", json.GetProperty("error").GetString());
            Assert.AreEqual(0, repo.BuyOrders.Count, "no order row may be written");
        }

        /// <summary>
        /// The no-hammer refusal reaches the wire as its own code with a 400, distinct from
        /// invalid_material. Granite is a real orderable material, so the web can tell "pick another
        /// material" from "order this one as bags instead".
        /// </summary>
        [TestMethod]
        public async Task PlaceOrder_HammerKindForAMaterialWithNoHammer_IsRefusedNoHammerForMaterial()
        {
            var token = await LoginAsync();
            var (status, json) = await Send(Request(HttpMethod.Post, "/v1/orders", bearer: token,
                body: $"{{\"material_type\":{(int)MaterialType.Granite},\"kind\":\"salvage_hammer\",\"count\":2,\"price_mmd\":10,\"buyer_character_guid\":{CharacterGuid}}}"));

            Assert.AreEqual(HttpStatusCode.BadRequest, status);
            Assert.AreEqual("no_hammer_for_material", json.GetProperty("error").GetString());
            Assert.AreEqual(0, repo.BuyOrders.Count);
        }

        [TestMethod]
        public async Task PlaceOrder_OmittedPrice_IsRefusedAndCreatesNothing()
        {
            var token = await LoginAsync();
            var (status, json) = await Send(Request(HttpMethod.Post, "/v1/orders", bearer: token,
                body: $"{{\"material_type\":{(int)MaterialType.Granite},\"count\":2,\"buyer_character_guid\":{CharacterGuid}}}"));
            Assert.AreEqual(HttpStatusCode.BadRequest, status);
            Assert.AreEqual("invalid_price", json.GetProperty("error").GetString());
            Assert.AreEqual(0, repo.BuyOrders.Count);
        }

        /// <summary>
        /// Discriminates the `body.Count == null` guard specifically: with material and price both
        /// present and valid, an omitted count must still be refused rather than defaulting to 1 -
        /// exactly the same rule POST /v1/listings applies to price. If that guard were relaxed to
        /// `body.Count ?? 1` the request would succeed (200, a row written) and this test fails.
        /// </summary>
        [TestMethod]
        public async Task PlaceOrder_OmittedCount_IsRefused()
        {
            var token = await LoginAsync();
            var (status, json) = await Send(Request(HttpMethod.Post, "/v1/orders", bearer: token,
                body: $"{{\"material_type\":{(int)MaterialType.Granite},\"price_mmd\":10,\"buyer_character_guid\":{CharacterGuid}}}"));
            Assert.AreEqual(HttpStatusCode.Conflict, status);
            Assert.AreEqual("count_unavailable", json.GetProperty("error").GetString());
            Assert.AreEqual(0, repo.BuyOrders.Count, "no order row may be written");
        }

        /// <summary>
        /// Discriminates the `body.MaterialType == null` guard. Without it the null would flow into
        /// PlaceOrder as `body.MaterialType.Value`, either throwing (an InvalidOperationException on
        /// Nullable&lt;T&gt;.Value) or - if defaulted to 0 instead - refusing for the wrong reason
        /// (material 0 is a category header, not this code path). Either way this test's exact
        /// status/code pair fails if the guard is removed.
        /// </summary>
        [TestMethod]
        public async Task PlaceOrder_OmittedMaterial_IsRefused()
        {
            var token = await LoginAsync();
            var (status, json) = await Send(Request(HttpMethod.Post, "/v1/orders", bearer: token,
                body: $"{{\"count\":2,\"price_mmd\":10,\"buyer_character_guid\":{CharacterGuid}}}"));
            Assert.AreEqual(HttpStatusCode.BadRequest, status);
            Assert.AreEqual("invalid_material", json.GetProperty("error").GetString());
            Assert.AreEqual(0, repo.BuyOrders.Count, "no order row may be written");
        }

        /// <summary>
        /// An absent buyer_character_guid must fail closed to 403 not_owner rather than being read as
        /// "any character on this account". Two layers cooperate to make that true - the route's own
        /// `?? 0` plus TryResolveCharacter's explicit refusal of guid 0, and PlaceOrder's own
        /// `buyer.CharacterGuid == 0` guard behind it - so this test only catches BOTH being removed
        /// at once; it was verified NOT to discriminate the route's guard in isolation (bypassing just
        /// that guard still refuses via the manager's own zero check, confirmed by direct edit-and-run).
        /// It still pins the outward contract: no route may ever turn an absent character into a 200.
        /// </summary>
        [TestMethod]
        public async Task PlaceOrder_OmittedCharacter_IsRefused()
        {
            var token = await LoginAsync();
            var (status, json) = await Send(Request(HttpMethod.Post, "/v1/orders", bearer: token,
                body: $"{{\"material_type\":{(int)MaterialType.Granite},\"count\":2,\"price_mmd\":10}}"));
            Assert.AreEqual(HttpStatusCode.Forbidden, status);
            Assert.AreEqual("not_owner", json.GetProperty("error").GetString());
            Assert.AreEqual(0, repo.BuyOrders.Count, "no order row may be written");
        }

        [TestMethod]
        public async Task PlaceOrder_HappyPath_ReturnsTheOrder_AndTheFeedCarriesIt()
        {
            var token = await LoginAsync();
            var (status, json) = await Send(Request(HttpMethod.Post, "/v1/orders", bearer: token,
                body: $"{{\"material_type\":{(int)MaterialType.Granite},\"count\":2,\"price_mmd\":10,\"buyer_character_guid\":{CharacterGuid}}}"));
            Assert.AreEqual(HttpStatusCode.OK, status);
            Assert.AreEqual("active", json.GetProperty("status").GetString());
            Assert.AreEqual(2, json.GetProperty("count_remaining").GetInt32());
            Assert.IsFalse(json.TryGetProperty("escrow_mmd", out _), "escrow is not on the wire");
            Assert.IsFalse(json.TryGetProperty("buyer_account_id", out _));
            Assert.AreEqual((long)CharacterGuid, json.GetProperty("buyer_character_guid").GetInt64());

            var (feedStatus, feed) = await Send(Request(HttpMethod.Get, "/v1/orders/changes?since=0"));
            Assert.AreEqual(HttpStatusCode.OK, feedStatus);
            Assert.AreEqual(1, feed.GetProperty("orders").GetArrayLength());
            Assert.AreEqual(MarketManager.FeedGeneration, feed.GetProperty("generation").GetString());
        }

        /// <summary>
        /// Discriminates the same `body.Count == null` guard as PlaceOrder, but on the FILL route,
        /// against a genuinely fillable order: a real Active order and a seller vault holding enough
        /// full granite bags to satisfy it. If the guard were relaxed to default 1, this would
        /// succeed and write a transaction (and take a bag); the guard must refuse it untouched.
        /// </summary>
        [TestMethod]
        public async Task Fill_OmittedCount_IsRefused()
        {
            var buyer = new MarketActor(SellerAccount, SellerCharacter, "Marketseller");
            wallet.Seed(SellerCharacter, 1000);
            var order = MarketManager.PlaceOrder(buyer, Granite, MarketBuyOrderKind.SalvageBag, 2, 10, MarketChannel.Web).Value;

            store.SeedGroupOf(AccountId, new List<WorldObject> { GraniteBag(), GraniteBag() });

            var token = await LoginAsync();
            var (status, json) = await Send(Request(HttpMethod.Post, $"/v1/orders/{order.Id}/fill", bearer: token,
                body: $"{{\"seller_character_guid\":{CharacterGuid}}}"));

            Assert.AreEqual(HttpStatusCode.Conflict, status);
            Assert.AreEqual("count_unavailable", json.GetProperty("error").GetString());
            Assert.AreEqual(0, repo.Transactions.Count, "no transaction row may be written");
            Assert.AreEqual(2, store.CountMatching(AccountId, wo => MarketSalvageMaterials.IsFullBagOf(wo, Granite)),
                "nothing may be taken from the seller's vault");
        }

        /// <summary>
        /// Discriminates DELETE /v1/orders/{id} threading the SESSION's account into CancelOrder,
        /// never an account named anywhere else. The order's true owner is a different account than
        /// the session; if the route resolved ownership incorrectly (or omitted the check the manager
        /// already makes) this call would refund and close the order instead of refusing it.
        /// </summary>
        [TestMethod]
        public async Task CancelOrder_ByAnotherAccount_IsForbidden()
        {
            var owner = new MarketActor(SellerAccount, SellerCharacter, "Marketseller");
            wallet.Seed(SellerCharacter, 1000);
            var order = MarketManager.PlaceOrder(owner, Granite, MarketBuyOrderKind.SalvageBag, 2, 10, MarketChannel.Web).Value;

            var token = await LoginAsync();
            var (status, json) = await Send(Request(HttpMethod.Delete, $"/v1/orders/{order.Id}", bearer: token));

            Assert.AreEqual(HttpStatusCode.Forbidden, status);
            Assert.AreEqual("not_owner", json.GetProperty("error").GetString());

            var live = MarketManager.GetBuyOrder(order.Id);
            Assert.AreEqual(MarketBuyOrderStatus.Active, live.Status, "the order must stay open");
            Assert.AreEqual(980, wallet.GetBalanceMmd(SellerCharacter), "no refund may be issued to the real owner");
        }

        /// <summary>
        /// The published status for every refusal, member by member, the same exhaustiveness guard
        /// MarketManagerTests puts on the wire codes. Only a handful of these entries are reachable
        /// through a route in this suite, and a member with no entry falls through to 500 - which a
        /// client reads as "the server broke" for what is really a conflict it can act on.
        ///
        /// The literals are deliberate rather than StatusCodes constants: this table IS the contract in
        /// market-api-v1.yaml, and asserting a constant against itself would prove nothing.
        /// </summary>
        [TestMethod]
        public void ErrorStatuses_ArePublishedForEveryRefusal()
        {
            var expected = new Dictionary<MarketError, int>
            {
                { MarketError.BadCredentials,    401 },
                { MarketError.NotOwner,          403 },
                { MarketError.ListingNotActive,  404 },
                { MarketError.ItemNotFound,      404 },
                { MarketError.PriceChanged,      409 },
                { MarketError.AlreadyListed,     409 },
                { MarketError.CountUnavailable,  409 },
                { MarketError.InsufficientFunds, 402 },
                { MarketError.InvalidPrice,      400 },
                { MarketError.RateLimited,       429 },
                { MarketError.VaultUnavailable,  503 },
                { MarketError.VaultFull,         409 },
                { MarketError.Timeout,           503 },
                { MarketError.Disabled,          503 },
                { MarketError.LedgerUnknown,     500 },
                { MarketError.ServerError,       500 },
                { MarketError.ItemListed,        409 },
                { MarketError.BarrelUnavailable, 503 },
                { MarketError.ListingLimit,      409 },
                { MarketError.OrderExists,       409 },
                { MarketError.OrderNotActive,    404 },
                { MarketError.NoMatchingItems,   409 },
                { MarketError.InvalidMaterial,   400 },
                { MarketError.BuyOrdersDisabled, 503 },
                { MarketError.OrderBusy,         409 },
                { MarketError.NoHammerForMaterial, 400 },
                { MarketError.SheetNotFound,     404 },
                { MarketError.SheetsDisabled,    503 },
                { MarketError.SheetBusy,         503 },
                { MarketError.NotAdmin,          403 },
                { MarketError.AdminDisabled,     503 },
                { MarketError.SettingNotFound,   404 },
                { MarketError.SettingSensitive,  403 },
                { MarketError.InvalidSettingValue, 400 },
                { MarketError.SettingChanged,    409 },
                { MarketError.InvalidAnnouncement, 400 },
                { MarketError.CommandsDisabled,  503 },
                { MarketError.InvalidCommandText, 400 },
                { MarketError.UnknownCommand,    404 },
                { MarketError.CommandNotPermitted, 403 },
                { MarketError.CommandInGameOnly, 409 },
                { MarketError.CommandBusy,       409 },
                { MarketError.NoCharacterOnline, 409 },
                { MarketError.CommandNotStarted, 503 },
                { MarketError.WorldEventsDisabled, 503 },
                { MarketError.WorldEventRunning, 409 },
                { MarketError.WorldEventNotRunning, 409 },
                { MarketError.WorldEventRunChanged, 409 },
                { MarketError.InvalidLocation,   400 },
                { MarketError.SourceNotWebStartable, 400 },
                { MarketError.WorldEventRefused, 400 },
                { MarketError.VaultLoading,      503 },
                { MarketError.SuitBuilderDisabled, 503 },
                { MarketError.CharacterOffline,  409 },
                { MarketError.NotInVaultArea,    409 },
                { MarketError.PackFull,          409 },
                { MarketError.TransferInProgress, 409 },
                { MarketError.TooManyItems,      400 },
                { MarketError.CharacterBusy,     409 },
                { MarketError.VaultPanelOpen,    409 },
                { MarketError.InvalidTransfer,   400 },
                { MarketError.TransferNotFound,  404 },
                { MarketError.InPvp,             409 },
            };

            foreach (var kvp in expected)
                Assert.AreEqual(kvp.Value, MarketApiHost.StatusFor(kvp.Key), $"wrong status for {kvp.Key}");

            foreach (MarketError value in Enum.GetValues(typeof(MarketError)))
            {
                if (value == MarketError.None)
                    continue;

                Assert.IsTrue(expected.ContainsKey(value), $"MarketError.{value} has no published status");
            }
        }

        // ---- Character Sheet (Docs/CharacterSheet/DESIGN.md 4.1) ----

        [TestMethod]
        public async Task Sheet_NoKey_Is401Empty()
        {
            using var r = await Raw(HttpMethod.Get, "/v1/sheets/Ab3dE5gH9k", key: null);
            Assert.AreEqual(HttpStatusCode.Unauthorized, r.StatusCode);
            Assert.AreEqual(0, (await r.Content.ReadAsByteArrayAsync()).Length);
        }

        [TestMethod]
        public async Task Sheet_NotFound_Disabled_Busy_MapToPublishedCodes()
        {
            sheets.NextSheet = new SheetResult(SheetOutcome.NotFound, null);
            await AssertError("/v1/sheets/Ab3dE5gH9k", HttpStatusCode.NotFound, "sheet_not_found");
            sheets.NextSheet = new SheetResult(SheetOutcome.Disabled, null);
            await AssertError("/v1/sheets/Ab3dE5gH9k", HttpStatusCode.ServiceUnavailable, "sheets_disabled");
            sheets.NextSheet = new SheetResult(SheetOutcome.Busy, null);
            await AssertError("/v1/sheets/Ab3dE5gH9k", HttpStatusCode.ServiceUnavailable, "sheet_busy");
        }

        [TestMethod]
        public async Task Sheet_Ok_NeedsNoSession_AndSerializesSnakeCase()
        {
            sheets.NextSheet = new SheetResult(SheetOutcome.Ok, new CharacterSheet { Name = "Weftwalker", Level = 275 });
            using var r = await Raw(HttpMethod.Get, "/v1/sheets/Ab3dE5gH9k");
            Assert.AreEqual(HttpStatusCode.OK, r.StatusCode);
            StringAssert.Contains(await r.Content.ReadAsStringAsync(), "\"class_abilities\"");
        }

        [TestMethod]
        public async Task SheetLink_OtherAccountsCharacter_IsRefusedAndServiceNotCalled()
        {
            var token = await LoginAsync();
            using var r = await Raw(HttpMethod.Post, $"/v1/accounts/me/characters/{VictimCharacter}/sheet-link", bearer: token, json: "{\"rotate\":true}");
            Assert.AreEqual(HttpStatusCode.Forbidden, r.StatusCode);
            Assert.AreEqual(0, sheets.EnableCalls);
        }

        [TestMethod]
        public void StatusFor_SheetMembers()
        {
            Assert.AreEqual(404, MarketApiHost.StatusFor(MarketError.SheetNotFound));
            Assert.AreEqual(503, MarketApiHost.StatusFor(MarketError.SheetsDisabled));
            Assert.AreEqual(503, MarketApiHost.StatusFor(MarketError.SheetBusy));
        }

        [TestMethod]
        public async Task SheetLink_Get_OwnCharacter_ReturnsTheLink()
        {
            var token = await LoginAsync();
            sheets.NextLink = new LinkResult(SheetOutcome.Ok, new SheetLink { Enabled = true, Slug = "Ab3dE5gH9k", Url = "https://char.example.test/Ab3dE5gH9k" });

            var (status, json) = await Send(Request(HttpMethod.Get, $"/v1/accounts/me/characters/{CharacterGuid}/sheet-link", bearer: token));

            Assert.AreEqual(HttpStatusCode.OK, status);
            Assert.AreEqual("Ab3dE5gH9k", json.GetProperty("slug").GetString());
            Assert.AreEqual(1, sheets.GetLinkCalls);
        }

        [TestMethod]
        public async Task SheetLink_Post_OwnCharacter_CallsEnableOrRotate()
        {
            var token = await LoginAsync();
            using var r = await Raw(HttpMethod.Post, $"/v1/accounts/me/characters/{CharacterGuid}/sheet-link", bearer: token, json: "{\"rotate\":true}");
            Assert.AreEqual(HttpStatusCode.OK, r.StatusCode);
            Assert.AreEqual(1, sheets.EnableCalls);
        }

        [TestMethod]
        public async Task SheetLink_Delete_OwnCharacter_CallsDisable()
        {
            var token = await LoginAsync();
            var (status, _) = await Send(Request(HttpMethod.Delete, $"/v1/accounts/me/characters/{CharacterGuid}/sheet-link", bearer: token));
            Assert.AreEqual(HttpStatusCode.OK, status);
            Assert.AreEqual(1, sheets.DisableCalls);
        }

        // ---- /v1/admin/* (PLAN-P1.md section 1) ----

        private static Account AdminAccount(uint accessLevel = 5, DateTime? banExpireTime = null) =>
            new Account { AccountId = AccountId, AccountName = "weft", AccessLevel = accessLevel, BanExpireTime = banExpireTime };

        /// <summary>Restarts the host with the same base wiring as Setup(), plus the admin delegates under test.</summary>
        private async Task RestartWithAdminOptions(
            Func<uint, Account> getAccountById,
            Func<bool> adminWebEnabled,
            Func<IEnumerable<(string key, string type, string current, string @default, string description)>> enumerateSettings = null,
            Action<IAction> enqueueWorldAction = null,
            bool wireEnqueue = true,
            int writeRatePerMinute = 20,
            int requestTimeoutMs = 3000,
            AdminChatFeed chatFeed = null,
            Func<string, int> sendAnnouncement = null,
            Action<string, string> writeAdminAudit = null,
            int announcePerMinute = 6,
            WebCommandListService commands = null,
            Func<bool> adminWebCommandsEnabled = null,
            WebCommandDispatcher commandDispatcher = null)
        {
            MarketApiHost.Stop();
            MarketApiHost.Start(new MarketApiOptions
            {
                ListenUrl = "http://127.0.0.1:0",
                SharedKey = Key,
                MaxInFlight = 8,
                WriteRatePerMinute = writeRatePerMinute,
                RequestTimeoutMs = requestTimeoutMs,
                LoginRatePerMinutePerAccount = 50,
                LoginRatePerMinutePerIp = 500,
                Wallet = wallet,
                AuthenticateAccount = (name, password) => name == "tester" && password == "correct-horse" ? AccountId : 0u,
                ListCharacters = accountId => accountId == AccountId
                    ? new List<(uint, string)> { (CharacterGuid, "Marketbuyer") }
                    : new List<(uint, string)>(),
                GetAccountById = getAccountById,
                AdminWebEnabled = adminWebEnabled,
                EnumerateSettings = enumerateSettings ?? (() => new List<(string key, string type, string current, string @default, string description)>
                {
                    ("pk_server", "bool", "False", "False", "PvP switch"),
                }),
                EnqueueWorldAction = wireEnqueue ? (enqueueWorldAction ?? (a => a.Act())) : null,
                ChatFeed = chatFeed,
                SendAnnouncement = sendAnnouncement,
                WriteAdminAudit = writeAdminAudit,
                AdminAnnounceRatePerMinute = announcePerMinute,
                Commands = commands,
                AdminWebCommandsEnabled = adminWebCommandsEnabled,
                CommandDispatcher = commandDispatcher,
            });

            client.Dispose();
            client = new HttpClient { BaseAddress = new Uri(MarketApiHost.ListeningUrl) };
        }

        [TestMethod]
        public async Task Admin_NoKey_401Empty()
        {
            await RestartWithAdminOptions(_ => AdminAccount(), () => true);

            using var r = await client.SendAsync(Request(HttpMethod.Get, "/v1/admin/me", key: null));
            Assert.AreEqual(HttpStatusCode.Unauthorized, r.StatusCode);
            Assert.AreEqual(0, (await r.Content.ReadAsByteArrayAsync()).Length,
                "a wrong or missing key is dropped WITHOUT a body, like every other route");
        }

        [TestMethod]
        public async Task Admin_NoBearer_401BadCredentials()
        {
            await RestartWithAdminOptions(_ => AdminAccount(), () => true);

            var (status, json) = await Send(Request(HttpMethod.Get, "/v1/admin/me"));
            Assert.AreEqual(HttpStatusCode.Unauthorized, status);
            Assert.AreEqual("bad_credentials", json.GetProperty("error").GetString());
        }

        [TestMethod]
        public async Task Admin_NonAdmin_403_SettingsNotEnumerated()
        {
            var enumerateCalls = 0;
            await RestartWithAdminOptions(
                _ => new Account { AccountId = AccountId, AccountName = "tester", AccessLevel = 1 },
                () => true,
                () => { enumerateCalls++; return new List<(string key, string type, string current, string @default, string description)>(); });

            var token = await LoginAsync();
            var (status, json) = await Send(Request(HttpMethod.Get, "/v1/admin/settings", bearer: token));

            Assert.AreEqual(HttpStatusCode.Forbidden, status);
            Assert.AreEqual("not_admin", json.GetProperty("error").GetString());
            Assert.AreEqual(0, enumerateCalls, "a refused caller must never trigger settings enumeration");
        }

        [TestMethod]
        public async Task Admin_MissingRow_403()
        {
            await RestartWithAdminOptions(_ => null, () => true);

            var token = await LoginAsync();
            var (status, json) = await Send(Request(HttpMethod.Get, "/v1/admin/me", bearer: token));

            Assert.AreEqual(HttpStatusCode.Forbidden, status);
            Assert.AreEqual("not_admin", json.GetProperty("error").GetString());
        }

        [TestMethod]
        public async Task Admin_DemotedMidSession_403()
        {
            var accessLevel = 5u;
            await RestartWithAdminOptions(_ => new Account { AccountId = AccountId, AccountName = "weft", AccessLevel = accessLevel }, () => true);

            var token = await LoginAsync();

            var (firstStatus, _) = await Send(Request(HttpMethod.Get, "/v1/admin/me", bearer: token));
            Assert.AreEqual(HttpStatusCode.OK, firstStatus);

            accessLevel = 1;

            var (status, json) = await Send(Request(HttpMethod.Get, "/v1/admin/me", bearer: token));
            Assert.AreEqual(HttpStatusCode.Forbidden, status);
            Assert.AreEqual("not_admin", json.GetProperty("error").GetString(),
                "a demoted account must be refused on its very next request - no cached grant");
        }

        [TestMethod]
        public async Task Admin_ReaderThrows_500()
        {
            await RestartWithAdminOptions(_ => throw new InvalidOperationException("db down"), () => true);

            var token = await LoginAsync();
            var (status, json) = await Send(Request(HttpMethod.Get, "/v1/admin/me", bearer: token));

            Assert.AreEqual(HttpStatusCode.InternalServerError, status);
            Assert.AreEqual("server_error", json.GetProperty("error").GetString(),
                "a read that threw is refused, never granted");
        }

        [TestMethod]
        public async Task Admin_Disabled_Admin_503()
        {
            await RestartWithAdminOptions(_ => AdminAccount(), () => false);

            var token = await LoginAsync();
            var (status, json) = await Send(Request(HttpMethod.Get, "/v1/admin/me", bearer: token));

            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
            Assert.AreEqual("admin_disabled", json.GetProperty("error").GetString());
        }

        [TestMethod]
        public async Task Admin_Disabled_NonAdmin_403()
        {
            // The panel switch must NEVER be revealed to a non-Admin: this must answer not_admin, never
            // admin_disabled, even though AdminWebEnabled would answer false.
            await RestartWithAdminOptions(_ => new Account { AccountId = AccountId, AccountName = "tester", AccessLevel = 1 }, () => false);

            var token = await LoginAsync();
            var (status, json) = await Send(Request(HttpMethod.Get, "/v1/admin/me", bearer: token));

            Assert.AreEqual(HttpStatusCode.Forbidden, status);
            Assert.AreEqual("not_admin", json.GetProperty("error").GetString());
        }

        [TestMethod]
        public async Task Admin_MarketKillSwitchOff_StillServes()
        {
            // The market_enabled trading kill switch has no effect on admin routes (PLAN-P1.md section 1).
            PropertyManager.ModifyBool("market_enabled", false);
            try
            {
                await RestartWithAdminOptions(_ => AdminAccount(), () => true);

                var token = await LoginAsync();
                var (status, _) = await Send(Request(HttpMethod.Get, "/v1/admin/me", bearer: token));

                Assert.AreEqual(HttpStatusCode.OK, status);
            }
            finally
            {
                PropertyManager.ModifyBool("market_enabled", true);
            }
        }

        [TestMethod]
        public async Task AdminMe_ReturnsIdAndName()
        {
            await RestartWithAdminOptions(_ => AdminAccount(), () => true);

            var token = await LoginAsync();
            var (status, json) = await Send(Request(HttpMethod.Get, "/v1/admin/me", bearer: token));

            Assert.AreEqual(HttpStatusCode.OK, status);
            Assert.AreEqual((int)AccountId, json.GetProperty("account_id").GetInt32());
            Assert.AreEqual("weft", json.GetProperty("account_name").GetString());
        }

        [TestMethod]
        public async Task AdminSettings_SnakeCase_TabOrder_SensitiveOmitted()
        {
            await RestartWithAdminOptions(_ => AdminAccount(), () => true,
                () => new List<(string key, string type, string current, string @default, string description)>
                {
                    ("discord_webhook_url_audit", "string", "https://example.test/secret", "", "Discord webhook"),
                    ("pk_server", "bool", "False", "False", "PvP switch"),
                });

            var token = await LoginAsync();
            var (status, json) = await Send(Request(HttpMethod.Get, "/v1/admin/settings", bearer: token));

            Assert.AreEqual(HttpStatusCode.OK, status);

            var tabs = json.GetProperty("tabs").EnumerateArray().ToList();
            Assert.AreEqual(10, tabs.Count);
            CollectionAssert.AreEqual(
                new[] { "server", "social", "combat", "pvp", "items", "economy", "progression", "class_abilities", "threads", "world_events" },
                tabs.Select(t => t.GetProperty("id").GetString()).ToList());

            var settings = json.GetProperty("settings").EnumerateArray().ToList();
            Assert.AreEqual(2, settings.Count);

            var pk = settings.Single(s => s.GetProperty("key").GetString() == "pk_server");
            Assert.AreEqual("bool", pk.GetProperty("type").GetString());
            Assert.AreEqual("False", pk.GetProperty("current_value").GetString());
            Assert.AreEqual("False", pk.GetProperty("default_value").GetString());
            Assert.AreEqual("pvp", pk.GetProperty("tab").GetString());
            Assert.IsFalse(pk.GetProperty("sensitive").GetBoolean());

            var webhook = settings.Single(s => s.GetProperty("key").GetString() == "discord_webhook_url_audit");
            Assert.IsTrue(webhook.GetProperty("sensitive").GetBoolean());
            Assert.IsFalse(webhook.TryGetProperty("current_value", out _), "a sensitive row must OMIT current_value entirely");
            Assert.IsFalse(webhook.TryGetProperty("default_value", out _), "a sensitive row must OMIT default_value entirely");
        }

        [TestMethod]
        public async Task AdminSettings_NoStore()
        {
            await RestartWithAdminOptions(_ => AdminAccount(), () => true);

            var token = await LoginAsync();
            using var r = await Raw(HttpMethod.Get, "/v1/admin/settings", bearer: token);

            Assert.AreEqual(HttpStatusCode.OK, r.StatusCode);
            Assert.AreEqual("no-store", r.Headers.CacheControl?.ToString());
        }

        // ---- POST /v1/admin/commands/run (PLAN-P4.md section 2, P4b) ----

        private readonly List<WebCommandDispatcher> runDispatchers = new List<WebCommandDispatcher>();
        private readonly List<System.Threading.ManualResetEventSlim> runGates = new List<System.Threading.ManualResetEventSlim>();

        private const string RunPath = "/v1/admin/commands/run";

        private sealed class RunFixture
        {
            public readonly FakeWebCommandCatalog Catalog = new FakeWebCommandCatalog();
            public readonly FakeWebCommandWorld World = new FakeWebCommandWorld();
            public readonly Dictionary<CommandHandlerInfo, ResolvedCommand> Resolutions = new Dictionary<CommandHandlerInfo, ResolvedCommand>();
            public WebCommandDispatcher Dispatcher;

            public WebCmdHandlerProbe Add(string name, string bucket, AccessLevel access = AccessLevel.Developer)
            {
                var probe = new WebCmdHandlerProbe();
                var info = Catalog.Add(WebCmd.Info(name, probe.Handler, access));
                Resolutions[info] = WebCmd.Resolved(bucket);
                return probe;
            }
        }

        private RunFixture NewRunFixture(int timeoutMs = 5000)
        {
            var fixture = new RunFixture();
            fixture.World.Character = new WebCommandOnlineCharacter(WebCmd.BareSession(), null, "+Weft");
            fixture.Dispatcher = new WebCommandDispatcher(fixture.Catalog,
                info => fixture.Resolutions.TryGetValue(info, out var resolved) ? resolved : WebCmd.Resolved(WebCommandBuckets.InGameOnly),
                fixture.World, () => Environment.TickCount64, timeoutMs);
            runDispatchers.Add(fixture.Dispatcher);
            return fixture;
        }

        private Task RestartForRun(RunFixture fixture, Func<uint, Account> account = null, Func<bool> adminWebEnabled = null, Func<bool> commandsEnabled = null, int writeRatePerMinute = 20) =>
            RestartWithAdminOptions(account ?? (_ => AdminAccount()), adminWebEnabled ?? (() => true),
                writeRatePerMinute: writeRatePerMinute,
                adminWebCommandsEnabled: commandsEnabled ?? (() => true),
                commandDispatcher: fixture?.Dispatcher);

        private static string RunBody(string text) => JsonSerializer.Serialize(new { text });

        private Task<(HttpStatusCode status, JsonElement json)> PostRun(string token, string body) =>
            Send(Request(HttpMethod.Post, RunPath, bearer: token, body: body));

        [TestMethod]
        public async Task AdminRun_200_Output()
        {
            var fixture = NewRunFixture();
            var probe = fixture.Add("webecho", WebCommandBuckets.Web);
            probe.Body = (s, p) => ACE.Server.Command.Handlers.CommandHandlerHelper.WriteOutputInfo(s, "Bool property successfully updated!");
            await RestartForRun(fixture);

            var token = await LoginAsync();
            using var r = await Raw(HttpMethod.Post, RunPath, bearer: token, json: RunBody("@webecho"));

            Assert.AreEqual(HttpStatusCode.OK, r.StatusCode);
            Assert.AreEqual("no-store", r.Headers.CacheControl?.ToString());

            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
            var json = doc.RootElement;

            CollectionAssert.AreEquivalent(new[] { "command", "bucket", "result", "ran", "character_name", "elapsed_ms", "truncated", "output" },
                json.EnumerateObject().Select(p => p.Name).ToList());
            Assert.AreEqual("webecho", json.GetProperty("command").GetString());
            Assert.AreEqual("web", json.GetProperty("bucket").GetString());
            Assert.AreEqual("ok", json.GetProperty("result").GetString());
            Assert.IsTrue(json.GetProperty("ran").GetBoolean());
            Assert.AreEqual(JsonValueKind.Null, json.GetProperty("character_name").ValueKind, "written as an explicit null for the web bucket");
            Assert.IsTrue(json.GetProperty("elapsed_ms").GetInt64() >= 0);
            Assert.IsFalse(json.GetProperty("truncated").GetBoolean());

            var output = json.GetProperty("output").EnumerateArray().ToList();
            Assert.AreEqual(1, output.Count);
            Assert.AreEqual("info", output[0].GetProperty("level").GetString());
            Assert.AreEqual("Bool property successfully updated!", output[0].GetProperty("text").GetString());
            Assert.AreEqual(1, probe.Calls);
        }

        [TestMethod]
        public async Task AdminRun_CharacterBucket_200_NamesCharacter()
        {
            var fixture = NewRunFixture();
            fixture.Add("charcmd", WebCommandBuckets.InGameCharacter);
            await RestartForRun(fixture);

            var token = await LoginAsync();
            var (status, json) = await PostRun(token, RunBody("charcmd"));

            Assert.AreEqual(HttpStatusCode.OK, status);
            Assert.AreEqual("in_game_character", json.GetProperty("bucket").GetString());
            Assert.AreEqual("+Weft", json.GetProperty("character_name").GetString());
        }

        [TestMethod]
        [DataRow("   ", false, HttpStatusCode.BadRequest, "invalid_command_text", DisplayName = "AdminRun_StatusTable_Row8_InvalidCommandText")]
        [DataRow("nosuch", false, HttpStatusCode.NotFound, "unknown_command", DisplayName = "AdminRun_StatusTable_Row9_UnknownCommand")]
        [DataRow("toohigh", false, HttpStatusCode.Forbidden, "command_not_permitted", DisplayName = "AdminRun_StatusTable_Row10_CommandNotPermitted")]
        [DataRow("gameonly", false, HttpStatusCode.Conflict, "command_in_game_only", DisplayName = "AdminRun_StatusTable_Row11_CommandInGameOnly")]
        [DataRow("charcmd", false, HttpStatusCode.Conflict, "no_character_online", DisplayName = "AdminRun_StatusTable_Row13_NoCharacterOnline")]
        [DataRow("charcmd", true, HttpStatusCode.ServiceUnavailable, "command_not_started", DisplayName = "AdminRun_StatusTable_Row14_CommandNotStarted")]
        public async Task AdminRun_StatusTable(string text, bool characterOnline, HttpStatusCode expectedStatus, string expectedCode)
        {
            var fixture = NewRunFixture(timeoutMs: 150);
            var probes = new[]
            {
                // Above Admin: the route only ever authorizes Admin, so an attribute above it is how row 10 is reached.
                fixture.Add("toohigh", WebCommandBuckets.Web, (AccessLevel)6),
                fixture.Add("gameonly", WebCommandBuckets.InGameOnly),
                fixture.Add("charcmd", WebCommandBuckets.InGameCharacter),
            };

            if (!characterOnline)
                fixture.World.Character = null;

            fixture.World.HoldActions = true;
            await RestartForRun(fixture);

            var token = await LoginAsync();
            var (status, json) = await PostRun(token, RunBody(text));

            Assert.AreEqual(expectedStatus, status);
            Assert.AreEqual(expectedCode, json.GetProperty("error").GetString());
            Assert.AreEqual(MarketErrorCodes.ToMessage(ErrorForCode(expectedCode)), json.GetProperty("message").GetString());
            Assert.IsFalse(json.TryGetProperty("busy_command", out _), "busy_command appears only on command_busy");
            Assert.IsTrue(probes.All(p => p.Calls == 0), "no refusal runs a handler");
        }

        private static MarketError ErrorForCode(string code) =>
            Enum.GetValues(typeof(MarketError)).Cast<MarketError>().Single(e => e != MarketError.None && MarketErrorCodes.ToCode(e) == code);

        [TestMethod]
        public async Task AdminRun_CommandBusy_EnvelopeCarriesBusyCommand()
        {
            var fixture = NewRunFixture(timeoutMs: 2000);
            var slow = fixture.Add("verify-slow", WebCommandBuckets.Web);
            slow.Gate = new System.Threading.ManualResetEventSlim(false);
            runGates.Add(slow.Gate);
            var ping = fixture.Add("ping", WebCommandBuckets.Web);
            await RestartForRun(fixture);

            var token = await LoginAsync();

            var (firstStatus, first) = await PostRun(token, RunBody("verify-slow"));
            Assert.IsTrue(slow.Entered.Wait(10_000), "the slow handler must have been entered");
            Assert.AreEqual(HttpStatusCode.OK, firstStatus, "a started command that outlives the wait is a 200, never an error");
            Assert.AreEqual("timeout", first.GetProperty("result").GetString());
            Assert.IsTrue(first.GetProperty("ran").GetBoolean());

            var (status, json) = await PostRun(token, RunBody("ping"));

            Assert.AreEqual(HttpStatusCode.Conflict, status);
            Assert.AreEqual("command_busy", json.GetProperty("error").GetString());
            Assert.AreEqual("verify-slow", json.GetProperty("busy_command").GetString());
            Assert.AreEqual(0, ping.Calls);

            slow.Gate.Set();
        }

        [TestMethod]
        public async Task AdminRun_DemotedMidSession_403_HandlerNotInvoked()
        {
            var fixture = NewRunFixture();
            var probe = fixture.Add("webecho", WebCommandBuckets.Web);
            var accessLevel = 5u;
            await RestartForRun(fixture, account: _ => new Account { AccountId = AccountId, AccountName = "weft", AccessLevel = accessLevel });

            var token = await LoginAsync();

            var (firstStatus, _) = await PostRun(token, RunBody("webecho"));
            Assert.AreEqual(HttpStatusCode.OK, firstStatus);
            Assert.AreEqual(1, probe.Calls);

            accessLevel = 4;

            var (status, json) = await PostRun(token, RunBody("webecho"));

            Assert.AreEqual(HttpStatusCode.Forbidden, status);
            Assert.AreEqual("not_admin", json.GetProperty("error").GetString());
            Assert.AreEqual(1, probe.Calls, "the demoted account's run is refused before the dispatcher");
            Assert.AreEqual(1, fixture.Catalog.LookupCalls, "the dispatcher was never reached the second time");
        }

        [TestMethod]
        public async Task AdminRun_WriteBucket_429()
        {
            var fixture = NewRunFixture();
            var probe = fixture.Add("webecho", WebCommandBuckets.Web);
            await RestartForRun(fixture, writeRatePerMinute: 1);

            var token = await LoginAsync();

            var (firstStatus, _) = await PostRun(token, RunBody("webecho"));
            Assert.AreEqual(HttpStatusCode.OK, firstStatus);

            var (status, json) = await PostRun(token, RunBody("webecho"));
            Assert.AreEqual(HttpStatusCode.TooManyRequests, status);
            Assert.AreEqual("rate_limited", json.GetProperty("error").GetString());
            Assert.AreEqual(1, probe.Calls);
        }

        [TestMethod]
        [DataRow(null, DisplayName = "AdminRun_Body_Empty_400")]
        [DataRow("{not json", DisplayName = "AdminRun_Body_MalformedJson_400")]
        [DataRow("{}", DisplayName = "AdminRun_Body_MissingText_400")]
        [DataRow("{\"text\":null}", DisplayName = "AdminRun_Body_NullText_400")]
        [DataRow("{\"text\":5}", DisplayName = "AdminRun_Body_NonStringText_400")]
        public async Task AdminRun_Body_Invalid_400(string body)
        {
            var fixture = NewRunFixture();
            var probe = fixture.Add("webecho", WebCommandBuckets.Web);
            await RestartForRun(fixture);

            var token = await LoginAsync();
            var (status, json) = await PostRun(token, body);

            Assert.AreEqual(HttpStatusCode.BadRequest, status);
            Assert.AreEqual("invalid_command_text", json.GetProperty("error").GetString());
            Assert.AreEqual(0, fixture.Catalog.LookupCalls);
            Assert.AreEqual(0, probe.Calls);
        }

        /// <summary>A body of exactly <paramref name="targetBytes"/> UTF-8 bytes whose text is valid; the rest is an ignored multi-byte pad.</summary>
        private static string PaddedRunBody(string text, int targetBytes)
        {
            var prefix = "{\"text\":" + JsonSerializer.Serialize(text) + ",\"pad\":\"";
            const string suffix = "\"}";
            var padBytes = targetBytes - Encoding.UTF8.GetByteCount(prefix) - Encoding.UTF8.GetByteCount(suffix);
            var body = prefix + new string((char)0xE9, padBytes / 2) + (padBytes % 2 == 1 ? "a" : "") + suffix;

            Assert.AreEqual(targetBytes, Encoding.UTF8.GetByteCount(body), "the constructed body has the wrong byte count");
            return body;
        }

        [TestMethod]
        public async Task AdminRun_Body_Over8KiB_400_DispatcherNotCalled()
        {
            var fixture = NewRunFixture();
            var probe = fixture.Add("webecho", WebCommandBuckets.Web);
            await RestartForRun(fixture, writeRatePerMinute: 100);

            var token = await LoginAsync();

            // CONTROL: exactly MaxAdminBodyBytes with a valid text runs, so the refusal below is the size rule.
            var (okStatus, _) = await PostRun(token, PaddedRunBody("webecho", MarketApiHost.MaxAdminBodyBytes));
            Assert.AreEqual(HttpStatusCode.OK, okStatus);
            Assert.AreEqual(1, probe.Calls);

            var over = PaddedRunBody("webecho", MarketApiHost.MaxAdminBodyBytes + 1);
            Assert.IsTrue(over.Length < MarketApiHost.MaxAdminBodyBytes, "multi-byte characters: the BYTE count, not string.Length, must trip the limit");

            var (status, json) = await PostRun(token, over);

            Assert.AreEqual(HttpStatusCode.BadRequest, status);
            Assert.AreEqual("invalid_command_text", json.GetProperty("error").GetString());
            Assert.AreEqual(1, probe.Calls, "the over-size body's valid text must never reach the dispatcher");
            Assert.AreEqual(1, fixture.Catalog.LookupCalls);
        }

        [TestMethod]
        public async Task AdminRun_CommandsDisabled_503_DispatcherNotCalled()
        {
            var fixture = NewRunFixture();
            var probe = fixture.Add("webecho", WebCommandBuckets.Web);
            await RestartForRun(fixture, commandsEnabled: () => false);

            var token = await LoginAsync();
            var (status, json) = await PostRun(token, RunBody("webecho"));

            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
            Assert.AreEqual("commands_disabled", json.GetProperty("error").GetString());
            Assert.AreEqual(0, fixture.Catalog.LookupCalls);
            Assert.AreEqual(0, probe.Calls);
        }

        [TestMethod]
        public async Task AdminRun_AdminDisabled_WinsOverCommandsDisabled()
        {
            var fixture = NewRunFixture();
            await RestartForRun(fixture, adminWebEnabled: () => false, commandsEnabled: () => false);

            var token = await LoginAsync();
            var (status, json) = await PostRun(token, RunBody("webecho"));

            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
            Assert.AreEqual("admin_disabled", json.GetProperty("error").GetString(), "row 6 precedes row 7, as on the GET route");
        }

        [TestMethod]
        public async Task AdminRun_NonAdmin_403_SwitchNotRevealed()
        {
            var fixture = NewRunFixture();
            var probe = fixture.Add("webecho", WebCommandBuckets.Web);
            await RestartForRun(fixture, account: _ => new Account { AccountId = AccountId, AccountName = "tester", AccessLevel = 1 }, commandsEnabled: () => false);

            var token = await LoginAsync();
            var (status, json) = await PostRun(token, RunBody("webecho"));

            Assert.AreEqual(HttpStatusCode.Forbidden, status);
            Assert.AreEqual("not_admin", json.GetProperty("error").GetString());
            Assert.AreEqual(0, probe.Calls);
        }

        [TestMethod]
        public async Task AdminRun_DispatcherNotWired_500()
        {
            await RestartForRun(null);

            var token = await LoginAsync();
            var (status, json) = await PostRun(token, RunBody("webecho"));

            Assert.AreEqual(HttpStatusCode.InternalServerError, status);
            Assert.AreEqual("server_error", json.GetProperty("error").GetString());
        }

        /// <summary>The market copies these byte for byte (PLAN-P4.md section 3.4), so they are pinned here.</summary>
        [TestMethod]
        public void CommandRunErrorMessages_AreTheFinalWording()
        {
            var expected = new Dictionary<MarketError, string>
            {
                { MarketError.InvalidCommandText, "That command text is missing, empty, longer than 1000 characters, or contains a control character, an invisible formatting character, a line or paragraph separator, or an unpaired surrogate." },
                { MarketError.UnknownCommand, "There is no server command with that name." },
                { MarketError.CommandNotPermitted, "That command needs a higher access level than this account or its online character has. If the account was promoted recently, relog the character." },
                { MarketError.CommandInGameOnly, "That command is not available from the web console." },
                { MarketError.CommandBusy, "The web console is already running a command. Try again when it finishes." },
                { MarketError.NoCharacterOnline, "No character of this account is online, so that in-game character command was not run." },
                { MarketError.CommandNotStarted, "The world did not start that command in time. It was not run." },
                { MarketError.WorldEventsDisabled, "World events are turned off on this server." },
                { MarketError.WorldEventRunning, "A world event is already running. Stop it before starting another." },
                { MarketError.WorldEventNotRunning, "No world event is running." },
                { MarketError.WorldEventRunChanged, "The running world event changed since this page last read it. Check the status before stopping." },
                { MarketError.InvalidLocation, "That location is not valid for this world event." },
                { MarketError.SourceNotWebStartable, "That event source cannot be started from the web." },
                { MarketError.WorldEventRefused, "The server refused that world event request." },
                { MarketError.VaultLoading, "Your vault is still loading, try again in a moment." },
                { MarketError.SuitBuilderDisabled, "The suit builder is turned off on this server." },
                { MarketError.CharacterOffline, "That character is not online. Log it in to move items from your vault." },
                { MarketError.NotInVaultArea, "That character is not standing where your vault can be used." },
                { MarketError.PackFull, "That character's pack does not have the room or the burden for everything asked for. Nothing was moved." },
                { MarketError.TransferInProgress, "A vault transfer for this account is already running. Wait for it to finish." },
                { MarketError.TooManyItems, "That is too many items for one transfer. Move at most 24 at a time." },
                { MarketError.CharacterBusy, "That character is too busy to receive items right now." },
                { MarketError.VaultPanelOpen, "That character is standing at a vault vendor. Step away from it first." },
                { MarketError.InvalidTransfer, "That transfer request is missing a field or has one out of range." },
                { MarketError.TransferNotFound, "There is no such transfer on this account." },
                { MarketError.InPvp, "That character is in a PvP match or under a PvP template and cannot receive items from the vault." },
            };

            foreach (var kvp in expected)
                Assert.AreEqual(kvp.Value, MarketErrorCodes.ToMessage(kvp.Key), $"message for {kvp.Key}");
        }

        // ---- GET /v1/admin/commands (PLAN-P4.md section 2, P4a) ----

        private sealed class FakeCommandCatalog : ICommandCatalog
        {
            private readonly IReadOnlyList<CommandHandlerInfo> commands;
            public int Calls;

            public FakeCommandCatalog(IReadOnlyList<CommandHandlerInfo> commands)
            {
                this.commands = commands;
            }

            public IReadOnlyList<CommandHandlerInfo> GetCommands()
            {
                Calls++;
                return commands;
            }

            public bool TryGetCommandInfo(string name, out CommandHandlerInfo info)
            {
                info = commands.FirstOrDefault(c => string.Equals(c.Attribute.Command, name, StringComparison.OrdinalIgnoreCase));
                return info != null;
            }

            public CommandHandlerResponse GetCommandHandler(ACE.Server.Network.Session session, string command, string[] parameters, out CommandHandlerInfo info) =>
                throw new NotSupportedException("the GET list never resolves a handler");
        }

        /// <summary>Lives in the TEST assembly, so a command bound to it resolves as a mod.</summary>
        public static void FakeModCommand(ACE.Server.Network.Session session, params string[] parameters)
        {
        }

        private static readonly Lazy<List<(string Command, string Handler, CommandHandlerAttribute Attribute, MethodInfo Method)>> RegisteredCommands =
            new Lazy<List<(string, string, CommandHandlerAttribute, MethodInfo)>>(() => CommandClassification.ReflectRegistered(typeof(CommandManager).Assembly.GetTypes()));

        private static CommandHandlerInfo LiveCommand(string command)
        {
            var match = RegisteredCommands.Value.Single(r => r.Command == command);
            return new CommandHandlerInfo { Attribute = match.Attribute, Handler = Delegate.CreateDelegate(typeof(CommandHandler), match.Method) };
        }

        private static CommandHandlerInfo ModCommand() => new CommandHandlerInfo
        {
            Attribute = new CommandHandlerAttribute("p4a-mod-command", AccessLevel.Developer, CommandHandlerFlag.None, "A mod command.", "<x>"),
            Handler = Delegate.CreateDelegate(typeof(CommandHandler), typeof(MarketApiTests).GetMethod(nameof(FakeModCommand))),
        };

        private static FakeCommandCatalog StandardCatalog() => new FakeCommandCatalog(new[]
        {
            LiveCommand("teleallto"), ModCommand(), LiveCommand("serverstatus"), LiveCommand("fix-spell-bars"),
        });

        private static WebCommandListService ListService(FakeCommandCatalog catalog, Func<uint, string> characterName = null) =>
            new WebCommandListService(catalog, CommandClassification.LoadEmbedded(), characterName ?? (id => id == AccountId ? "+Weft" : null));

        private static readonly string[] AdminCommandFields =
            { "name", "description", "usage", "access_level", "parameter_count", "flags", "bucket", "capture", "source", "permitted", "note" };

        [TestMethod]
        public async Task AdminCommands_SnakeCase_OrderedByName_BucketsAndCharacter()
        {
            var catalog = StandardCatalog();
            await RestartWithAdminOptions(_ => AdminAccount(), () => true, commands: ListService(catalog), adminWebCommandsEnabled: () => true);

            var token = await LoginAsync();
            var (status, json) = await Send(Request(HttpMethod.Get, "/v1/admin/commands", bearer: token));

            Assert.AreEqual(HttpStatusCode.OK, status);

            var character = json.GetProperty("character");
            Assert.IsTrue(character.GetProperty("online").GetBoolean());
            Assert.AreEqual("+Weft", character.GetProperty("name").GetString());

            var commands = json.GetProperty("commands").EnumerateArray().ToList();
            CollectionAssert.AreEqual(new[] { "fix-spell-bars", "p4a-mod-command", "serverstatus", "teleallto" },
                commands.Select(c => c.GetProperty("name").GetString()).ToList(), "commands are ordered by name, ordinal");

            foreach (var c in commands)
                foreach (var field in AdminCommandFields)
                    Assert.IsTrue(c.TryGetProperty(field, out _), $"{c.GetProperty("name").GetString()} is missing required field {field}");

            JsonElement Named(string name) => commands.Single(c => c.GetProperty("name").GetString() == name);
            List<string> Flags(JsonElement c) => c.GetProperty("flags").EnumerateArray().Select(f => f.GetString()).ToList();

            var status1 = Named("serverstatus");
            var serverstatusAttribute = LiveCommand("serverstatus").Attribute;
            Assert.AreEqual("web", status1.GetProperty("bucket").GetString());
            Assert.AreEqual("server", status1.GetProperty("source").GetString());
            Assert.AreEqual("captured", status1.GetProperty("capture").GetString());
            Assert.AreEqual(WebCommandListService.AccessLevelWire(serverstatusAttribute.Access), status1.GetProperty("access_level").GetString());
            Assert.AreEqual(serverstatusAttribute.ParameterCount, status1.GetProperty("parameter_count").GetInt32());
            Assert.AreEqual(serverstatusAttribute.Description, status1.GetProperty("description").GetString());
            Assert.IsTrue(status1.GetProperty("permitted").GetBoolean());
            Assert.AreEqual(0, Flags(status1).Count);

            var tele = Named("teleallto");
            Assert.AreEqual("in_game_character", tele.GetProperty("bucket").GetString());
            CollectionAssert.Contains(Flags(tele), "requires_world");

            var spellBars = Named("fix-spell-bars");
            Assert.AreEqual("in_game_only", spellBars.GetProperty("bucket").GetString());
            CollectionAssert.AreEqual(new[] { "console_invoke", "long_running" }, Flags(spellBars));

            var mod = Named("p4a-mod-command");
            Assert.AreEqual("in_game_only", mod.GetProperty("bucket").GetString());
            Assert.AreEqual("mod", mod.GetProperty("source").GetString());
            Assert.AreEqual("uncaptured", mod.GetProperty("capture").GetString());
            Assert.AreEqual("developer", mod.GetProperty("access_level").GetString());
            Assert.AreEqual(-1, mod.GetProperty("parameter_count").GetInt32());
            Assert.AreEqual("<x>", mod.GetProperty("usage").GetString());
            Assert.IsFalse(string.IsNullOrEmpty(mod.GetProperty("note").GetString()));
        }

        [TestMethod]
        public async Task AdminCommands_OfflineCharacter_NameOmitted()
        {
            await RestartWithAdminOptions(_ => AdminAccount(), () => true, commands: ListService(StandardCatalog(), _ => null), adminWebCommandsEnabled: () => true);

            var token = await LoginAsync();
            var (status, json) = await Send(Request(HttpMethod.Get, "/v1/admin/commands", bearer: token));

            Assert.AreEqual(HttpStatusCode.OK, status);
            var character = json.GetProperty("character");
            Assert.IsFalse(character.GetProperty("online").GetBoolean());
            Assert.IsFalse(character.TryGetProperty("name", out _), "name is omitted when online is false");
        }

        [TestMethod]
        public async Task AdminCommands_NonAdmin_403_NotEnumerated()
        {
            var catalog = StandardCatalog();
            var lookups = 0;
            await RestartWithAdminOptions(
                _ => new Account { AccountId = AccountId, AccountName = "tester", AccessLevel = 4 },
                () => true,
                commands: ListService(catalog, _ => { lookups++; return "+Weft"; }),
                adminWebCommandsEnabled: () => true);

            var token = await LoginAsync();
            var (status, json) = await Send(Request(HttpMethod.Get, "/v1/admin/commands", bearer: token));

            Assert.AreEqual(HttpStatusCode.Forbidden, status);
            Assert.AreEqual("not_admin", json.GetProperty("error").GetString());
            Assert.AreEqual(0, catalog.Calls, "a refused caller must never trigger command enumeration");
            Assert.AreEqual(0, lookups, "a refused caller must never trigger a character lookup");
        }

        [TestMethod]
        public async Task AdminCommands_CommandsDisabled_503()
        {
            var catalog = StandardCatalog();
            await RestartWithAdminOptions(_ => AdminAccount(), () => true, commands: ListService(catalog), adminWebCommandsEnabled: () => false);

            var token = await LoginAsync();
            var (status, json) = await Send(Request(HttpMethod.Get, "/v1/admin/commands", bearer: token));

            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
            Assert.AreEqual("commands_disabled", json.GetProperty("error").GetString());
            Assert.AreEqual(0, catalog.Calls);

            // the Settings route is unaffected by the console switch (R6)
            var (settingsStatus, _) = await Send(Request(HttpMethod.Get, "/v1/admin/settings", bearer: token));
            Assert.AreEqual(HttpStatusCode.OK, settingsStatus);
        }

        [TestMethod]
        public async Task AdminCommands_CommandsDisabled_NonAdmin_403()
        {
            await RestartWithAdminOptions(_ => new Account { AccountId = AccountId, AccountName = "tester", AccessLevel = 1 }, () => true,
                commands: ListService(StandardCatalog()), adminWebCommandsEnabled: () => false);

            var token = await LoginAsync();
            var (status, json) = await Send(Request(HttpMethod.Get, "/v1/admin/commands", bearer: token));

            Assert.AreEqual(HttpStatusCode.Forbidden, status);
            Assert.AreEqual("not_admin", json.GetProperty("error").GetString(), "the console switch must never be revealed to a non-Admin");
        }

        [TestMethod]
        public async Task AdminCommands_AdminDisabled_WinsOverCommandsDisabled()
        {
            await RestartWithAdminOptions(_ => AdminAccount(), () => false, commands: ListService(StandardCatalog()), adminWebCommandsEnabled: () => false);

            var token = await LoginAsync();
            var (status, json) = await Send(Request(HttpMethod.Get, "/v1/admin/commands", bearer: token));

            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
            Assert.AreEqual("admin_disabled", json.GetProperty("error").GetString(), "row 6 (admin_disabled) precedes row 7 (commands_disabled)");
        }

        [TestMethod]
        public async Task AdminCommands_SwitchNotWired_CommandsDisabled()
        {
            await RestartWithAdminOptions(_ => AdminAccount(), () => true, commands: ListService(StandardCatalog()), adminWebCommandsEnabled: null);

            var token = await LoginAsync();
            var (status, json) = await Send(Request(HttpMethod.Get, "/v1/admin/commands", bearer: token));

            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
            Assert.AreEqual("commands_disabled", json.GetProperty("error").GetString(), "an unwired switch fails closed");
        }

        [TestMethod]
        public async Task AdminCommands_ServiceNotWired_500()
        {
            await RestartWithAdminOptions(_ => AdminAccount(), () => true, commands: null, adminWebCommandsEnabled: () => true);

            var token = await LoginAsync();
            var (status, json) = await Send(Request(HttpMethod.Get, "/v1/admin/commands", bearer: token));

            Assert.AreEqual(HttpStatusCode.InternalServerError, status);
            Assert.AreEqual("server_error", json.GetProperty("error").GetString());
        }

        [TestMethod]
        public async Task AdminCommands_CharacterLookupThrows_500()
        {
            await RestartWithAdminOptions(_ => AdminAccount(), () => true,
                commands: ListService(StandardCatalog(), _ => throw new InvalidOperationException("Sequence contains more than one matching element")),
                adminWebCommandsEnabled: () => true);

            var token = await LoginAsync();
            var (status, json) = await Send(Request(HttpMethod.Get, "/v1/admin/commands", bearer: token));

            Assert.AreEqual(HttpStatusCode.InternalServerError, status);
            Assert.AreEqual("server_error", json.GetProperty("error").GetString());
        }

        [TestMethod]
        public async Task AdminCommands_NoStore()
        {
            await RestartWithAdminOptions(_ => AdminAccount(), () => true, commands: ListService(StandardCatalog()), adminWebCommandsEnabled: () => true);

            var token = await LoginAsync();
            using var r = await Raw(HttpMethod.Get, "/v1/admin/commands", bearer: token);

            Assert.AreEqual(HttpStatusCode.OK, r.StatusCode);
            Assert.AreEqual("no-store", r.Headers.CacheControl?.ToString());
        }

        // ---- POST /v1/admin/settings/{key} (PLAN-P2.md section 1-2) ----

        private static string EditBody(string value, string expectedCurrent) =>
            JsonSerializer.Serialize(new { value, expected_current = expectedCurrent });

        [TestMethod]
        public async Task AdminEdit_NoKey_401Empty_NoMutation()
        {
            await RestartWithAdminOptions(_ => AdminAccount(), () => true);

            // Seed pk_server into the process-static cache first: GetBool on an uncached key falls
            // through to DatabaseManager.ShardConfig, which is null (NREs) with no live shard database.
            PropertyManager.ModifyBool("pk_server", false);
            var priorValue = PropertyManager.GetBool("pk_server").Item;
            try
            {
                using var r = await Raw(HttpMethod.Post, "/v1/admin/settings/pk_server", key: null, json: EditBody("True", "False"));

                Assert.AreEqual(HttpStatusCode.Unauthorized, r.StatusCode);
                Assert.AreEqual(0, (await r.Content.ReadAsByteArrayAsync()).Length);
                Assert.AreEqual(priorValue, PropertyManager.GetBool("pk_server").Item);
            }
            finally
            {
                PropertyManager.ModifyBool("pk_server", priorValue);
            }
        }

        [TestMethod]
        public async Task AdminEdit_NoBearer_401BadCredentials()
        {
            await RestartWithAdminOptions(_ => AdminAccount(), () => true);

            var (status, json) = await Send(Request(HttpMethod.Post, "/v1/admin/settings/pk_server", body: EditBody("True", "False")));

            Assert.AreEqual(HttpStatusCode.Unauthorized, status);
            Assert.AreEqual("bad_credentials", json.GetProperty("error").GetString());
        }

        [TestMethod]
        public async Task AdminEdit_NonAdmin_403_NoMutation_NothingQueued()
        {
            var queued = 0;
            await RestartWithAdminOptions(
                _ => new Account { AccountId = AccountId, AccountName = "tester", AccessLevel = 1 },
                () => true,
                enqueueWorldAction: a => { queued++; a.Act(); });

            PropertyManager.ModifyBool("pk_server", false);
            var priorAuditAs = PropertyAdminService.AuditAs;
            var auditCalls = 0;
            PropertyAdminService.AuditAs = (l, m) => auditCalls++;
            try
            {
                var token = await LoginAsync();
                var (status, json) = await Send(Request(HttpMethod.Post, "/v1/admin/settings/pk_server", bearer: token, body: EditBody("True", "False")));

                Assert.AreEqual(HttpStatusCode.Forbidden, status);
                Assert.AreEqual("not_admin", json.GetProperty("error").GetString());
                Assert.AreEqual(0, queued);
                Assert.AreEqual(0, auditCalls);
                Assert.IsFalse(PropertyManager.GetBool("pk_server").Item);
            }
            finally
            {
                PropertyAdminService.AuditAs = priorAuditAs;
            }
        }

        [TestMethod]
        public async Task AdminEdit_DemotedMidSession_403_NoMutation()
        {
            var accessLevel = 5u;
            await RestartWithAdminOptions(_ => new Account { AccountId = AccountId, AccountName = "weft", AccessLevel = accessLevel }, () => true);

            PropertyManager.ModifyBool("pk_server", false);
            var token = await LoginAsync();

            var (firstStatus, _) = await Send(Request(HttpMethod.Get, "/v1/admin/me", bearer: token));
            Assert.AreEqual(HttpStatusCode.OK, firstStatus);

            accessLevel = 4;

            var (status, json) = await Send(Request(HttpMethod.Post, "/v1/admin/settings/pk_server", bearer: token, body: EditBody("True", "False")));

            Assert.AreEqual(HttpStatusCode.Forbidden, status);
            Assert.AreEqual("not_admin", json.GetProperty("error").GetString());
            Assert.IsFalse(PropertyManager.GetBool("pk_server").Item);
        }

        [TestMethod]
        public async Task AdminEdit_Disabled_Admin_503()
        {
            await RestartWithAdminOptions(_ => AdminAccount(), () => false);

            var token = await LoginAsync();
            var (status, json) = await Send(Request(HttpMethod.Post, "/v1/admin/settings/pk_server", bearer: token, body: EditBody("True", "False")));

            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
            Assert.AreEqual("admin_disabled", json.GetProperty("error").GetString());
        }

        [TestMethod]
        public async Task AdminEdit_Disabled_NonAdmin_403()
        {
            await RestartWithAdminOptions(_ => new Account { AccountId = AccountId, AccountName = "tester", AccessLevel = 1 }, () => false);

            var token = await LoginAsync();
            var (status, json) = await Send(Request(HttpMethod.Post, "/v1/admin/settings/pk_server", bearer: token, body: EditBody("True", "False")));

            Assert.AreEqual(HttpStatusCode.Forbidden, status);
            Assert.AreEqual("not_admin", json.GetProperty("error").GetString());
        }

        [TestMethod]
        public async Task AdminEdit_WriteBucketEmpty_429()
        {
            await RestartWithAdminOptions(_ => AdminAccount(), () => true, writeRatePerMinute: 1);

            PropertyManager.ModifyBool("pk_server", false);
            var priorAuditAs = PropertyAdminService.AuditAs;
            var priorSideEffect = PropertyAdminService.WorldTypeChanged;
            PropertyAdminService.AuditAs = (l, m) => { };
            // The pk_server side effect calls the REAL PlayerManager.UpdatePKStatusForAllPlayers unless
            // stubbed, which reads pk_respite_timer - an uncached key that NREs with no live shard
            // database in this test environment. Not this test's concern; stub it out.
            PropertyAdminService.WorldTypeChanged = (k, v) => { };
            try
            {
                var token = await LoginAsync();

                var (first, _) = await Send(Request(HttpMethod.Post, "/v1/admin/settings/pk_server", bearer: token, body: EditBody("True", "False")));
                Assert.AreEqual(HttpStatusCode.OK, first);

                var (status, json) = await Send(Request(HttpMethod.Post, "/v1/admin/settings/pk_server", bearer: token, body: EditBody("False", "True")));
                Assert.AreEqual(HttpStatusCode.TooManyRequests, status);
                Assert.AreEqual("rate_limited", json.GetProperty("error").GetString());
            }
            finally
            {
                PropertyAdminService.AuditAs = priorAuditAs;
                PropertyAdminService.WorldTypeChanged = priorSideEffect;
                PropertyManager.ModifyBool("pk_server", false);
            }
        }

        [TestMethod]
        public async Task AdminEdit_UnknownKey_404()
        {
            await RestartWithAdminOptions(_ => AdminAccount(), () => true);

            var token = await LoginAsync();
            var (status, json) = await Send(Request(HttpMethod.Post, "/v1/admin/settings/not_a_real_property_key", bearer: token, body: EditBody("True", "False")));

            Assert.AreEqual(HttpStatusCode.NotFound, status);
            Assert.AreEqual("setting_not_found", json.GetProperty("error").GetString());
        }

        [TestMethod]
        public async Task AdminEdit_MalformedKey_404()
        {
            await RestartWithAdminOptions(_ => AdminAccount(), () => true);

            var token = await LoginAsync();
            var (status, json) = await Send(Request(HttpMethod.Post, "/v1/admin/settings/Pk_Server", bearer: token, body: EditBody("True", "False")));

            Assert.AreEqual(HttpStatusCode.NotFound, status);
            Assert.AreEqual("setting_not_found", json.GetProperty("error").GetString());
        }

        [TestMethod]
        public async Task AdminEdit_SensitiveKey_403_ValueIndependent_NothingQueued()
        {
            var queued = 0;
            await RestartWithAdminOptions(_ => AdminAccount(), () => true, enqueueWorldAction: a => { queued++; a.Act(); });

            var token = await LoginAsync();

            var (status1, json1) = await Send(Request(HttpMethod.Post, "/v1/admin/settings/discord_webhook_url_audit", bearer: token, body: EditBody("https://a.example.test/x", "irrelevant-1")));
            var (status2, json2) = await Send(Request(HttpMethod.Post, "/v1/admin/settings/discord_webhook_url_audit", bearer: token, body: EditBody("https://b.example.test/y", "irrelevant-2")));

            Assert.AreEqual(HttpStatusCode.Forbidden, status1);
            Assert.AreEqual(HttpStatusCode.Forbidden, status2);
            Assert.AreEqual("setting_sensitive", json1.GetProperty("error").GetString());
            Assert.AreEqual(json1.GetRawText(), json2.GetRawText(), "the refusal must be byte-identical regardless of the posted value");
            Assert.AreEqual(0, queued);
        }

        [TestMethod]
        public async Task AdminEdit_MissingExpectedCurrent_400()
        {
            await RestartWithAdminOptions(_ => AdminAccount(), () => true);

            var token = await LoginAsync();
            var (status, json) = await Send(Request(HttpMethod.Post, "/v1/admin/settings/pk_server", bearer: token, body: "{\"value\":\"True\"}"));

            Assert.AreEqual(HttpStatusCode.BadRequest, status);
            Assert.AreEqual("invalid_setting_value", json.GetProperty("error").GetString());
        }

        [TestMethod]
        public async Task AdminEdit_MissingValue_400()
        {
            await RestartWithAdminOptions(_ => AdminAccount(), () => true);

            var token = await LoginAsync();
            var (status, json) = await Send(Request(HttpMethod.Post, "/v1/admin/settings/pk_server", bearer: token, body: "{\"expected_current\":\"False\"}"));

            Assert.AreEqual(HttpStatusCode.BadRequest, status);
            Assert.AreEqual("invalid_setting_value", json.GetProperty("error").GetString());
        }

        [TestMethod]
        public async Task AdminEdit_NumberValue_400()
        {
            await RestartWithAdminOptions(_ => AdminAccount(), () => true);

            var token = await LoginAsync();
            var (status, json) = await Send(Request(HttpMethod.Post, "/v1/admin/settings/account_vault_entry_cap", bearer: token, body: "{\"value\":5,\"expected_current\":\"500\"}"));

            Assert.AreEqual(HttpStatusCode.BadRequest, status);
            Assert.AreEqual("invalid_setting_value", json.GetProperty("error").GetString());
        }

        [TestMethod]
        public async Task AdminEdit_BadDouble_400()
        {
            await RestartWithAdminOptions(_ => AdminAccount(), () => true);

            PropertyManager.ModifyDouble("pk_respite_timer", 300);
            var token = await LoginAsync();
            var (status, json) = await Send(Request(HttpMethod.Post, "/v1/admin/settings/pk_respite_timer", bearer: token, body: EditBody("1,000.5", "300")));

            Assert.AreEqual(HttpStatusCode.BadRequest, status);
            Assert.AreEqual("invalid_setting_value", json.GetProperty("error").GetString());
        }

        [TestMethod]
        public async Task AdminEdit_StaleExpected_409_CarriesCurrentValue_NoMutation()
        {
            await RestartWithAdminOptions(_ => AdminAccount(), () => true);

            PropertyManager.ModifyBool("pk_server", false);
            var token = await LoginAsync();
            var (status, json) = await Send(Request(HttpMethod.Post, "/v1/admin/settings/pk_server", bearer: token, body: EditBody("True", "True")));

            Assert.AreEqual(HttpStatusCode.Conflict, status);
            Assert.AreEqual("setting_changed", json.GetProperty("error").GetString());
            Assert.AreEqual("False", json.GetProperty("current_value").GetString());
            Assert.IsFalse(PropertyManager.GetBool("pk_server").Item);
        }

        [TestMethod]
        public async Task AdminEdit_Success_200_SnakeCaseShape_AuditNamesWebAccount()
        {
            await RestartWithAdminOptions(_ => AdminAccount(), () => true);

            PropertyManager.ModifyBool("pk_server", false);
            var priorAuditAs = PropertyAdminService.AuditAs;
            var priorSideEffect = PropertyAdminService.WorldTypeChanged;
            (string label, string message)? captured = null;
            PropertyAdminService.AuditAs = (l, m) => captured = (l, m);
            // Not this test's concern - stub the pk_server side effect so it never touches the
            // uncached pk_respite_timer key (see AdminEdit_WriteBucketEmpty_429).
            PropertyAdminService.WorldTypeChanged = (k, v) => { };
            try
            {
                var token = await LoginAsync();
                var (status, json) = await Send(Request(HttpMethod.Post, "/v1/admin/settings/pk_server", bearer: token, body: EditBody("True", "False")));

                Assert.AreEqual(HttpStatusCode.OK, status);
                Assert.AreEqual("pk_server", json.GetProperty("key").GetString());
                Assert.AreEqual("bool", json.GetProperty("type").GetString());
                Assert.AreEqual("False", json.GetProperty("previous_value").GetString());
                Assert.AreEqual("True", json.GetProperty("current_value").GetString());
                Assert.IsTrue(json.GetProperty("changed").GetBoolean());

                Assert.IsNotNull(captured);
                Assert.AreEqual("weft", captured.Value.label);
                StringAssert.Contains(captured.Value.message, "via the web admin panel");
            }
            finally
            {
                PropertyAdminService.AuditAs = priorAuditAs;
                PropertyAdminService.WorldTypeChanged = priorSideEffect;
                PropertyManager.ModifyBool("pk_server", false);
            }
        }

        [TestMethod]
        public async Task AdminEdit_SameValue_200_ChangedFalse_NoAudit()
        {
            await RestartWithAdminOptions(_ => AdminAccount(), () => true);

            PropertyManager.ModifyBool("pk_server", false);
            var priorAuditAs = PropertyAdminService.AuditAs;
            var auditCalls = 0;
            PropertyAdminService.AuditAs = (l, m) => auditCalls++;
            try
            {
                var token = await LoginAsync();
                var (status, json) = await Send(Request(HttpMethod.Post, "/v1/admin/settings/pk_server", bearer: token, body: EditBody("False", "False")));

                Assert.AreEqual(HttpStatusCode.OK, status);
                Assert.IsFalse(json.GetProperty("changed").GetBoolean());
                Assert.AreEqual(0, auditCalls);
            }
            finally
            {
                PropertyAdminService.AuditAs = priorAuditAs;
            }
        }

        [TestMethod]
        public async Task AdminEdit_QueueNeverRuns_503Timeout()
        {
            await RestartWithAdminOptions(_ => AdminAccount(), () => true, enqueueWorldAction: a => { }, requestTimeoutMs: 100);

            var token = await LoginAsync();
            var (status, json) = await Send(Request(HttpMethod.Post, "/v1/admin/settings/pk_server", bearer: token, body: EditBody("True", "False")));

            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
            Assert.AreEqual("timeout", json.GetProperty("error").GetString());
        }

        [TestMethod]
        public async Task AdminEdit_QueuedWorkThrows_500()
        {
            // Simulates the queued edit throwing (PLAN-P2.md order 10): EnqueueWorldAction itself
            // faulting is caught by Guard's outer catch exactly like a throw inside the queued work.
            await RestartWithAdminOptions(_ => AdminAccount(), () => true, enqueueWorldAction: a => throw new InvalidOperationException("boom"));

            var token = await LoginAsync();
            var (status, json) = await Send(Request(HttpMethod.Post, "/v1/admin/settings/pk_server", bearer: token, body: EditBody("True", "False")));

            Assert.AreEqual(HttpStatusCode.InternalServerError, status);
            Assert.AreEqual("server_error", json.GetProperty("error").GetString());
        }

        [TestMethod]
        public async Task AdminEdit_EnqueueNotConfigured_500()
        {
            await RestartWithAdminOptions(_ => AdminAccount(), () => true, wireEnqueue: false);

            var token = await LoginAsync();
            var (status, json) = await Send(Request(HttpMethod.Post, "/v1/admin/settings/pk_server", bearer: token, body: EditBody("True", "False")));

            Assert.AreEqual(HttpStatusCode.InternalServerError, status);
            Assert.AreEqual("server_error", json.GetProperty("error").GetString());
        }

        [TestMethod]
        public async Task AdminEdit_PkServer_FiresSideEffectOnce()
        {
            await RestartWithAdminOptions(_ => AdminAccount(), () => true);

            PropertyManager.ModifyBool("pk_server", false);
            var priorAuditAs = PropertyAdminService.AuditAs;
            var priorSideEffect = PropertyAdminService.WorldTypeChanged;
            PropertyAdminService.AuditAs = (l, m) => { };
            var sideEffectCalls = 0;
            PropertyAdminService.WorldTypeChanged = (k, v) => sideEffectCalls++;
            try
            {
                var token = await LoginAsync();
                var (status, _) = await Send(Request(HttpMethod.Post, "/v1/admin/settings/pk_server", bearer: token, body: EditBody("True", "False")));

                Assert.AreEqual(HttpStatusCode.OK, status);
                Assert.AreEqual(1, sideEffectCalls);
            }
            finally
            {
                PropertyAdminService.AuditAs = priorAuditAs;
                PropertyAdminService.WorldTypeChanged = priorSideEffect;
                PropertyManager.ModifyBool("pk_server", false);
            }
        }

        [TestMethod]
        public async Task AdminEdit_NoStore()
        {
            await RestartWithAdminOptions(_ => AdminAccount(), () => true);

            PropertyManager.ModifyBool("pk_server", false);
            var priorAuditAs = PropertyAdminService.AuditAs;
            var priorSideEffect = PropertyAdminService.WorldTypeChanged;
            PropertyAdminService.AuditAs = (l, m) => { };
            // Not this test's concern - stub the pk_server side effect so it never touches the
            // uncached pk_respite_timer key (see AdminEdit_WriteBucketEmpty_429).
            PropertyAdminService.WorldTypeChanged = (k, v) => { };
            try
            {
                var token = await LoginAsync();
                using var r = await Raw(HttpMethod.Post, "/v1/admin/settings/pk_server", bearer: token, json: EditBody("True", "False"));

                Assert.AreEqual(HttpStatusCode.OK, r.StatusCode);
                Assert.AreEqual("no-store", r.Headers.CacheControl?.ToString());
            }
            finally
            {
                PropertyAdminService.AuditAs = priorAuditAs;
                PropertyAdminService.WorldTypeChanged = priorSideEffect;
                PropertyManager.ModifyBool("pk_server", false);
            }
        }

        [TestMethod]
        public async Task AdminEdit_BodyOver8KiB_400_NoMutation()
        {
            await RestartWithAdminOptions(_ => AdminAccount(), () => true);

            PropertyManager.ModifyString("server_motd", "");
            var token = await LoginAsync();

            var padded = EditBody(new string('a', 8300), "");
            var (paddedStatus, paddedJson) = await Send(Request(HttpMethod.Post, "/v1/admin/settings/server_motd", bearer: token, body: padded));

            Assert.AreEqual(HttpStatusCode.BadRequest, paddedStatus);
            Assert.AreEqual("invalid_setting_value", paddedJson.GetProperty("error").GetString());
            Assert.AreEqual("", PropertyManager.GetString("server_motd").Item);

            // CONTROL: the same shape, unpadded, succeeds.
            var priorAuditAs = PropertyAdminService.AuditAs;
            PropertyAdminService.AuditAs = (l, m) => { };
            try
            {
                var small = EditBody("hi", "");
                var (smallStatus, _) = await Send(Request(HttpMethod.Post, "/v1/admin/settings/server_motd", bearer: token, body: small));
                Assert.AreEqual(HttpStatusCode.OK, smallStatus);
            }
            finally
            {
                PropertyAdminService.AuditAs = priorAuditAs;
                PropertyManager.ModifyString("server_motd", "");
            }
        }

        [TestMethod]
        public async Task AdminEdit_NullMetadata_403_NoMutation()
        {
            var queued = 0;
            await RestartWithAdminOptions(_ => AdminAccount(), () => true, enqueueWorldAction: a => { queued++; a.Act(); });

            var field = typeof(MarketApiHost).GetField("settingsMetadata", BindingFlags.NonPublic | BindingFlags.Static);
            var prior = field.GetValue(null);
            try
            {
                field.SetValue(null, null);

                PropertyManager.ModifyBool("chat_log_debug", false);
                var token = await LoginAsync();
                var (status, json) = await Send(Request(HttpMethod.Post, "/v1/admin/settings/chat_log_debug", bearer: token, body: EditBody("True", "False")));

                Assert.AreEqual(HttpStatusCode.Forbidden, status);
                Assert.AreEqual("setting_sensitive", json.GetProperty("error").GetString());
                Assert.AreEqual(0, queued);
                Assert.IsFalse(PropertyManager.GetBool("chat_log_debug").Item);
            }
            finally
            {
                field.SetValue(null, prior);
            }
        }

        [TestMethod]
        public async Task AdminEdit_KeyWithNoMetadataRow_403_NoMutation()
        {
            var queued = 0;
            await RestartWithAdminOptions(_ => AdminAccount(), () => true, enqueueWorldAction: a => { queued++; a.Act(); });

            var field = typeof(MarketApiHost).GetField("settingsMetadata", BindingFlags.NonPublic | BindingFlags.Static);
            var prior = field.GetValue(null);
            try
            {
                field.SetValue(null, ConfigMetadata.Parse(ConfigMetadata.HeaderLine + "\n"));

                PropertyManager.ModifyBool("chat_log_debug", false);
                var token = await LoginAsync();
                var (status, json) = await Send(Request(HttpMethod.Post, "/v1/admin/settings/chat_log_debug", bearer: token, body: EditBody("True", "False")));

                Assert.AreEqual(HttpStatusCode.Forbidden, status);
                Assert.AreEqual("setting_sensitive", json.GetProperty("error").GetString());
                Assert.AreEqual(0, queued);
                Assert.IsFalse(PropertyManager.GetBool("chat_log_debug").Item);
            }
            finally
            {
                field.SetValue(null, prior);
            }
        }

        // ---- GET /v1/admin/chat, POST /v1/admin/announce (PLAN-P3.md sections 1-2) ----

        [TestMethod]
        public async Task AdminChat_NoKey_401Empty()
        {
            await RestartWithAdminOptions(_ => AdminAccount(), () => true);

            using var r = await client.SendAsync(Request(HttpMethod.Get, "/v1/admin/chat", key: null));
            Assert.AreEqual(HttpStatusCode.Unauthorized, r.StatusCode);
            Assert.AreEqual(0, (await r.Content.ReadAsByteArrayAsync()).Length);
        }

        [TestMethod]
        public async Task AdminChat_NonAdmin_403_NoLinesInBody()
        {
            var feed = new AdminChatFeed();
            feed.Capture(ChatType.General, TurbineChatChannel.General, "Weftwalker", "unique-marker-xyz");

            await RestartWithAdminOptions(
                _ => new Account { AccountId = AccountId, AccountName = "tester", AccessLevel = 1 },
                () => true,
                chatFeed: feed);

            var token = await LoginAsync();
            var (status, text) = await SendRaw(Request(HttpMethod.Get, "/v1/admin/chat", bearer: token));

            Assert.AreEqual(HttpStatusCode.Forbidden, status);
            Assert.IsFalse(text.Contains("unique-marker-xyz"), "a refused caller must never receive buffered chat lines");
        }

        [TestMethod]
        public async Task AdminChat_DemotedMidSession_403()
        {
            var accessLevel = 5u;
            await RestartWithAdminOptions(_ => new Account { AccountId = AccountId, AccountName = "weft", AccessLevel = accessLevel }, () => true);

            var token = await LoginAsync();

            var (firstStatus, _) = await Send(Request(HttpMethod.Get, "/v1/admin/chat", bearer: token));
            Assert.AreEqual(HttpStatusCode.OK, firstStatus);

            accessLevel = 1;

            var (status, json) = await Send(Request(HttpMethod.Get, "/v1/admin/chat", bearer: token));
            Assert.AreEqual(HttpStatusCode.Forbidden, status);
            Assert.AreEqual("not_admin", json.GetProperty("error").GetString());
        }

        [TestMethod]
        public async Task AdminChat_Disabled_Admin_503()
        {
            await RestartWithAdminOptions(_ => AdminAccount(), () => false);

            var token = await LoginAsync();
            var (status, json) = await Send(Request(HttpMethod.Get, "/v1/admin/chat", bearer: token));

            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
            Assert.AreEqual("admin_disabled", json.GetProperty("error").GetString());
        }

        [TestMethod]
        public async Task AdminChat_Disabled_NonAdmin_403()
        {
            await RestartWithAdminOptions(_ => new Account { AccountId = AccountId, AccountName = "tester", AccessLevel = 1 }, () => false);

            var token = await LoginAsync();
            var (status, json) = await Send(Request(HttpMethod.Get, "/v1/admin/chat", bearer: token));

            Assert.AreEqual(HttpStatusCode.Forbidden, status);
            Assert.AreEqual("not_admin", json.GetProperty("error").GetString());
        }

        [TestMethod]
        public async Task AdminChat_SnakeCaseFields_SinceExclusive_NoStore()
        {
            var feed = new AdminChatFeed();
            feed.Capture(ChatType.General, TurbineChatChannel.General, "Weftwalker", "hi one");
            feed.Capture(ChatType.Trade, TurbineChatChannel.Trade, "Weftwalker", "hi two");

            await RestartWithAdminOptions(_ => AdminAccount(), () => true, chatFeed: feed);

            var token = await LoginAsync();
            using var r = await Raw(HttpMethod.Get, $"/v1/admin/chat?since=1&generation={feed.Generation}", bearer: token);

            Assert.AreEqual(HttpStatusCode.OK, r.StatusCode);
            Assert.AreEqual("no-store", r.Headers.CacheControl?.ToString());

            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
            var root = doc.RootElement;

            Assert.AreEqual(feed.Generation, root.GetProperty("generation").GetString());
            Assert.AreEqual(2, root.GetProperty("head_seq").GetInt64());
            Assert.AreEqual(1, root.GetProperty("oldest_seq").GetInt64());
            Assert.IsFalse(root.GetProperty("gap").GetBoolean());

            var lines = root.GetProperty("lines").EnumerateArray().ToList();
            Assert.AreEqual(1, lines.Count, "since=1 is exclusive, so only seq 2 is returned");
            Assert.AreEqual(2, lines[0].GetProperty("seq").GetInt64());
            Assert.AreEqual("trade", lines[0].GetProperty("channel").GetString());
            Assert.AreEqual("Weftwalker", lines[0].GetProperty("sender").GetString());
            Assert.AreEqual("hi two", lines[0].GetProperty("text").GetString());
            Assert.IsFalse(lines[0].GetProperty("truncated").GetBoolean());
        }

        [TestMethod]
        public async Task AdminChat_GapSignal_OverTheWire()
        {
            var feed = new AdminChatFeed(5);
            for (var i = 0; i < 12; i++)
                feed.Capture(ChatType.General, TurbineChatChannel.General, "x", $"msg{i}");

            await RestartWithAdminOptions(_ => AdminAccount(), () => true, chatFeed: feed);

            var token = await LoginAsync();
            var (status, json) = await Send(Request(HttpMethod.Get, $"/v1/admin/chat?since=2&generation={feed.Generation}", bearer: token));

            Assert.AreEqual(HttpStatusCode.OK, status);
            Assert.IsTrue(json.GetProperty("gap").GetBoolean());

            var lines = json.GetProperty("lines").EnumerateArray().ToList();
            Assert.AreEqual(8, lines.First().GetProperty("seq").GetInt64());
        }

        [DataTestMethod]
        [DataRow("abc")]
        [DataRow("-5")]
        public async Task AdminChat_MalformedOrNegativeSince_TailMode_Not400(string sinceValue)
        {
            var feed = new AdminChatFeed();
            feed.Capture(ChatType.General, TurbineChatChannel.General, "x", "hi");

            await RestartWithAdminOptions(_ => AdminAccount(), () => true, chatFeed: feed);

            var token = await LoginAsync();
            var (status, json) = await Send(Request(HttpMethod.Get, $"/v1/admin/chat?since={sinceValue}", bearer: token));

            Assert.AreEqual(HttpStatusCode.OK, status);
            Assert.IsFalse(json.GetProperty("gap").GetBoolean());
            Assert.AreEqual(1, json.GetProperty("lines").GetArrayLength());
        }

        private async Task<(HttpStatusCode status, string text)> SendRaw(HttpRequestMessage request)
        {
            using var response = await client.SendAsync(request);
            return (response.StatusCode, await response.Content.ReadAsStringAsync());
        }

        private static string AnnounceBody(string text) => JsonSerializer.Serialize(new { text });

        [TestMethod]
        public async Task Announce_NoKey_401Empty()
        {
            await RestartWithAdminOptions(_ => AdminAccount(), () => true);

            using var r = await client.SendAsync(Request(HttpMethod.Post, "/v1/admin/announce", key: null, body: AnnounceBody("hi")));
            Assert.AreEqual(HttpStatusCode.Unauthorized, r.StatusCode);
            Assert.AreEqual(0, (await r.Content.ReadAsByteArrayAsync()).Length);
        }

        [TestMethod]
        public async Task Announce_NonAdmin_403_NoAuditNoBroadcast()
        {
            var calls = new List<string>();
            await RestartWithAdminOptions(
                _ => new Account { AccountId = AccountId, AccountName = "tester", AccessLevel = 1 },
                () => true,
                sendAnnouncement: t => { calls.Add("broadcast"); return 1; },
                writeAdminAudit: (l, m) => calls.Add("audit"));

            var token = await LoginAsync();
            var (status, json) = await Send(Request(HttpMethod.Post, "/v1/admin/announce", bearer: token, body: AnnounceBody("hi")));

            Assert.AreEqual(HttpStatusCode.Forbidden, status);
            Assert.AreEqual("not_admin", json.GetProperty("error").GetString());
            Assert.AreEqual(0, calls.Count);
        }

        [TestMethod]
        public async Task Announce_DemotedMidSession_403_NoBroadcast()
        {
            var accessLevel = 5u;
            var calls = new List<string>();
            await RestartWithAdminOptions(
                _ => new Account { AccountId = AccountId, AccountName = "weft", AccessLevel = accessLevel },
                () => true,
                sendAnnouncement: t => { calls.Add("broadcast"); return 1; },
                writeAdminAudit: (l, m) => calls.Add("audit"));

            var token = await LoginAsync();

            var (firstStatus, _) = await Send(Request(HttpMethod.Get, "/v1/admin/me", bearer: token));
            Assert.AreEqual(HttpStatusCode.OK, firstStatus);

            accessLevel = 1;

            var (status, json) = await Send(Request(HttpMethod.Post, "/v1/admin/announce", bearer: token, body: AnnounceBody("hi")));
            Assert.AreEqual(HttpStatusCode.Forbidden, status);
            Assert.AreEqual("not_admin", json.GetProperty("error").GetString());
            Assert.AreEqual(0, calls.Count);
        }

        [TestMethod]
        public async Task Announce_Disabled_Admin_503_NoBroadcast()
        {
            var calls = new List<string>();
            await RestartWithAdminOptions(
                _ => AdminAccount(),
                () => false,
                sendAnnouncement: t => { calls.Add("broadcast"); return 1; },
                writeAdminAudit: (l, m) => calls.Add("audit"));

            var token = await LoginAsync();
            var (status, json) = await Send(Request(HttpMethod.Post, "/v1/admin/announce", bearer: token, body: AnnounceBody("hi")));

            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
            Assert.AreEqual("admin_disabled", json.GetProperty("error").GetString());
            Assert.AreEqual(0, calls.Count);
        }

        [TestMethod]
        public async Task Announce_Disabled_NonAdmin_403()
        {
            await RestartWithAdminOptions(_ => new Account { AccountId = AccountId, AccountName = "tester", AccessLevel = 1 }, () => false);

            var token = await LoginAsync();
            var (status, json) = await Send(Request(HttpMethod.Post, "/v1/admin/announce", bearer: token, body: AnnounceBody("hi")));

            Assert.AreEqual(HttpStatusCode.Forbidden, status);
            Assert.AreEqual("not_admin", json.GetProperty("error").GetString());
        }

        [DataTestMethod]
        [DataRow("{\"text\":\"\"}")]
        [DataRow("{\"text\":\"   \"}")]
        [DataRow("{\"text\":\"\\r\\n\"}")]
        [DataRow("{}")]
        [DataRow("{not valid json")]
        public async Task Announce_EmptyOrWhitespace_400_InvalidAnnouncement(string body)
        {
            var calls = new List<string>();
            await RestartWithAdminOptions(
                _ => AdminAccount(),
                () => true,
                sendAnnouncement: t => { calls.Add("broadcast"); return 1; },
                writeAdminAudit: (l, m) => calls.Add("audit"));

            var token = await LoginAsync();
            var (status, json) = await Send(Request(HttpMethod.Post, "/v1/admin/announce", bearer: token, body: body));

            Assert.AreEqual(HttpStatusCode.BadRequest, status);
            Assert.AreEqual("invalid_announcement", json.GetProperty("error").GetString());
            Assert.AreEqual(0, calls.Count);
        }

        [TestMethod]
        public async Task Announce_TooLong_400()
        {
            await RestartWithAdminOptions(_ => AdminAccount(), () => true);

            var token = await LoginAsync();
            var (status, json) = await Send(Request(HttpMethod.Post, "/v1/admin/announce", bearer: token, body: AnnounceBody(new string('a', 501))));

            Assert.AreEqual(HttpStatusCode.BadRequest, status);
            Assert.AreEqual("invalid_announcement", json.GetProperty("error").GetString());
        }

        [TestMethod]
        public async Task Announce_Exactly500_200()
        {
            await RestartWithAdminOptions(_ => AdminAccount(), () => true,
                sendAnnouncement: t => 3, writeAdminAudit: (l, m) => { });

            var token = await LoginAsync();
            var (status, _) = await Send(Request(HttpMethod.Post, "/v1/admin/announce", bearer: token, body: AnnounceBody(new string('a', 500))));

            Assert.AreEqual(HttpStatusCode.OK, status);
        }

        [TestMethod]
        public async Task Announce_BodyOver8KiB_400()
        {
            var calls = new List<string>();
            await RestartWithAdminOptions(
                _ => AdminAccount(),
                () => true,
                sendAnnouncement: t => { calls.Add("broadcast"); return 1; },
                writeAdminAudit: (l, m) => calls.Add("audit"));

            var token = await LoginAsync();

            // A syntactically valid body padded well over 8 KiB.
            var overBody = JsonSerializer.Serialize(new { text = "hi", pad = new string('a', 8300) });
            var (overStatus, overJson) = await Send(Request(HttpMethod.Post, "/v1/admin/announce", bearer: token, body: overBody));

            Assert.AreEqual(HttpStatusCode.BadRequest, overStatus);
            Assert.AreEqual("invalid_announcement", overJson.GetProperty("error").GetString());
            Assert.AreEqual(0, calls.Count);

            // CONTROL: a body of exactly MaxAdminBodyBytes UTF-8 bytes with valid text is 200. Reads
            // MarketApiHost.MaxAdminBodyBytes (internal, InternalsVisibleTo) rather than a separate
            // literal, so raising the shared constant cannot leave this control stale (code review,
            // 2026-09-15).
            var prefix = "{\"text\":\"hi\",\"pad\":\"";
            var suffix = "\"}";
            var padLength = MarketApiHost.MaxAdminBodyBytes - prefix.Length - suffix.Length;
            Assert.IsTrue(padLength > 0, "the fixed prefix/suffix must leave room for padding");
            var exactBody = prefix + new string('a', padLength) + suffix;
            Assert.AreEqual(MarketApiHost.MaxAdminBodyBytes, Encoding.UTF8.GetByteCount(exactBody), "the constructed body must be exactly MaxAdminBodyBytes bytes");

            var (exactStatus, _) = await Send(Request(HttpMethod.Post, "/v1/admin/announce", bearer: token, body: exactBody));
            Assert.AreEqual(HttpStatusCode.OK, exactStatus);
        }

        [TestMethod]
        public async Task Announce_ControlCharsBecomeSpaces()
        {
            string received = null;
            await RestartWithAdminOptions(_ => AdminAccount(), () => true,
                sendAnnouncement: t => { received = t; return 4; },
                writeAdminAudit: (l, m) => { });

            var token = await LoginAsync();
            var (status, json) = await Send(Request(HttpMethod.Post, "/v1/admin/announce", bearer: token, body: AnnounceBody("a\nb")));

            Assert.AreEqual(HttpStatusCode.OK, status);
            Assert.AreEqual("a b", received);
            Assert.AreEqual("Broadcast from System> a b", json.GetProperty("text").GetString());
        }

        [TestMethod]
        public async Task Announce_AuditLineNamesAccount_BeforeBroadcast()
        {
            var order = new List<string>();
            string capturedActor = null;
            string capturedMessage = null;

            await RestartWithAdminOptions(_ => AdminAccount(), () => true,
                sendAnnouncement: t => { order.Add("broadcast"); return 2; },
                writeAdminAudit: (l, m) => { order.Add("audit"); capturedActor = l; capturedMessage = m; });

            var token = await LoginAsync();
            var (status, _) = await Send(Request(HttpMethod.Post, "/v1/admin/announce", bearer: token, body: AnnounceBody("hi")));

            Assert.AreEqual(HttpStatusCode.OK, status);
            Assert.AreEqual("weft", capturedActor);
            StringAssert.Contains(capturedMessage, "hi");
            CollectionAssert.AreEqual(new[] { "audit", "broadcast" }, order);
        }

        [TestMethod]
        public async Task Announce_AuditThrows_500_NothingBroadcast()
        {
            var broadcastCalls = 0;
            await RestartWithAdminOptions(_ => AdminAccount(), () => true,
                sendAnnouncement: t => { broadcastCalls++; return 1; },
                writeAdminAudit: (l, m) => throw new InvalidOperationException("audit sink down"));

            var token = await LoginAsync();
            var (status, json) = await Send(Request(HttpMethod.Post, "/v1/admin/announce", bearer: token, body: AnnounceBody("hi")));

            Assert.AreEqual(HttpStatusCode.InternalServerError, status);
            Assert.AreEqual("server_error", json.GetProperty("error").GetString());
            Assert.AreEqual(0, broadcastCalls);
        }

        [TestMethod]
        public async Task Announce_DelegatesMissing_500()
        {
            await RestartWithAdminOptions(_ => AdminAccount(), () => true);

            var token = await LoginAsync();
            var (status, json) = await Send(Request(HttpMethod.Post, "/v1/admin/announce", bearer: token, body: AnnounceBody("hi")));

            Assert.AreEqual(HttpStatusCode.InternalServerError, status);
            Assert.AreEqual("server_error", json.GetProperty("error").GetString());
        }

        [TestMethod]
        public async Task Announce_SeventhInAMinute_429()
        {
            await RestartWithAdminOptions(_ => AdminAccount(), () => true,
                sendAnnouncement: t => 1, writeAdminAudit: (l, m) => { }, announcePerMinute: 6);

            var token = await LoginAsync();

            for (var i = 0; i < 6; i++)
            {
                var (status, _) = await Send(Request(HttpMethod.Post, "/v1/admin/announce", bearer: token, body: AnnounceBody($"hi {i}")));
                Assert.AreEqual(HttpStatusCode.OK, status, $"announcement {i} must succeed within the budget");
            }

            var (seventhStatus, seventhJson) = await Send(Request(HttpMethod.Post, "/v1/admin/announce", bearer: token, body: AnnounceBody("hi 7")));
            Assert.AreEqual(HttpStatusCode.TooManyRequests, seventhStatus);
            Assert.AreEqual("rate_limited", seventhJson.GetProperty("error").GetString());
        }

        [TestMethod]
        public async Task Announce_InvalidDoesNotSpendAnnounceBudget()
        {
            await RestartWithAdminOptions(_ => AdminAccount(), () => true,
                sendAnnouncement: t => 1, writeAdminAudit: (l, m) => { }, announcePerMinute: 6);

            var token = await LoginAsync();

            for (var i = 0; i < 10; i++)
            {
                var (status, json) = await Send(Request(HttpMethod.Post, "/v1/admin/announce", bearer: token, body: AnnounceBody("")));
                Assert.AreEqual(HttpStatusCode.BadRequest, status);
                Assert.AreEqual("invalid_announcement", json.GetProperty("error").GetString());
            }

            for (var i = 0; i < 6; i++)
            {
                var (status, _) = await Send(Request(HttpMethod.Post, "/v1/admin/announce", bearer: token, body: AnnounceBody($"valid {i}")));
                Assert.AreEqual(HttpStatusCode.OK, status, $"valid announcement {i} must not have been starved by the invalid attempts");
            }
        }

        [TestMethod]
        public async Task Announce_200_TextRecipientsSentAt()
        {
            await RestartWithAdminOptions(_ => AdminAccount(), () => true,
                sendAnnouncement: t => 12, writeAdminAudit: (l, m) => { });

            var token = await LoginAsync();
            var before = DateTime.UtcNow;
            var (status, json) = await Send(Request(HttpMethod.Post, "/v1/admin/announce", bearer: token, body: AnnounceBody("The server restarts in 10 minutes.")));
            var after = DateTime.UtcNow;

            Assert.AreEqual(HttpStatusCode.OK, status);
            Assert.AreEqual("Broadcast from System> The server restarts in 10 minutes.", json.GetProperty("text").GetString());
            Assert.AreEqual(12, json.GetProperty("recipients").GetInt32());

            var sentAt = json.GetProperty("sent_at").GetDateTime();
            Assert.IsTrue(sentAt >= before.AddSeconds(-1) && sentAt <= after.AddSeconds(1));
        }
    }
}
