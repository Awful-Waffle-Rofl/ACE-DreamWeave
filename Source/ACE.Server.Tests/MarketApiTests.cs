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
using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.Entity;
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
    }
}
