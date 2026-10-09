using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

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
    /// POST /v1/accounts/me/suit/transfers and GET /v1/accounts/me/suit/transfers/{transfer_id}, over a real
    /// loopback Kestrel (the SuitRoutesTests shape) with the transfer service behind a fake world seam over a
    /// real AccountVaultStore.
    /// </summary>
    [TestClass]
    public class SuitTransferRoutesTests
    {
        private const string Key = "suit-transfer-key";
        private const uint AccountId = 9501;
        private const uint OtherAccountId = 9502;
        private const uint CharacterGuid = 0x50000951;
        private const uint OtherCharacterGuid = 0x50000952;
        private const uint RingWcid = 95001;

        private HttpClient client;
        private bool suitEnabled;
        private FakeSuitTransferWorld world;
        private Container vaultContainer;
        private int writeRate;

        [TestInitialize]
        public void Setup()
        {
            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_entry_cap", 100000));
            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_landblock",
                DefaultPropertyManager.DefaultLongProperties["account_vault_landblock"].Item));
            VaultClassTestConfig.Seed();

            suitEnabled = true;
            writeRate = 20;
            StartHost();
        }

        private void StartHost()
        {
            var backend = new FakeVaultBackend();
            var vaultWorld = new FakeVaultWorld();

            vaultContainer = FakeVaultWorld.MakeContainer(255);
            vaultWorld.Containers[vaultContainer.Guid.Full] = vaultContainer;
            backend.Vaults.Add(new ShardAccountVault { Id = 9951, AccountId = AccountId, ContainerGuid = vaultContainer.Guid.Full, CreatedAt = new DateTime(2026, 1, 1) });

            world = new FakeSuitTransferWorld { Store = new AccountVaultStore(AccountId, backend, vaultWorld) };
            world.Online[(AccountId, CharacterGuid)] = new SuitTransferCharacter(AccountId, CharacterGuid, "Mover", null, null);

            MarketApiHost.Start(new MarketApiOptions
            {
                ListenUrl = "http://127.0.0.1:0",
                SharedKey = Key,
                MaxInFlight = 8,
                WriteRatePerMinute = writeRate,
                RequestTimeoutMs = 3000,
                AuthenticateAccount = (name, password) => password != "pw" ? 0u : name == "tester" ? AccountId : name == "other" ? OtherAccountId : 0u,
                ListCharacters = accountId => accountId == AccountId
                    ? new List<(uint, string)> { (CharacterGuid, "Mover") }
                    : new List<(uint, string)> { (OtherCharacterGuid, "Stranger") },
                SuitBuilderEnabled = () => suitEnabled,
                SuitTransfers = new SuitTransferService(world),
            });

            client = new HttpClient { BaseAddress = new Uri(MarketApiHost.ListeningUrl) };
        }

        [TestCleanup]
        public void Teardown()
        {
            client?.Dispose();
            MarketApiHost.Stop();
        }

        private WorldObject SeedRing()
        {
            var ring = FakeVaultWorld.MakeStack(RingWcid, 1, 1);
            Assert.IsTrue(vaultContainer.TryAddToInventory(ring));
            return ring;
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

        private async Task<string> LoginAsync(string name = "tester")
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/v1/session") { Content = new StringContent($"{{\"account_name\":\"{name}\",\"password\":\"pw\"}}", Encoding.UTF8, "application/json") };
            request.Headers.Add("X-Market-Key", Key);

            var (status, json) = await Send(request);
            Assert.AreEqual(HttpStatusCode.OK, status);
            return json.GetProperty("session_token").GetString();
        }

        private static HttpRequestMessage Post(string bearer, string body)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/v1/accounts/me/suit/transfers") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            request.Headers.Add("X-Market-Key", Key);
            request.Headers.Add("Authorization", $"Bearer {bearer}");
            return request;
        }

        private static HttpRequestMessage Get(string bearer, string id)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/accounts/me/suit/transfers/{id}");
            request.Headers.Add("X-Market-Key", Key);
            request.Headers.Add("Authorization", $"Bearer {bearer}");
            return request;
        }

        private static string Body(uint characterGuid, string key, uint itemGuid, bool? allowListed = null) =>
            $"{{\"character_guid\":{characterGuid},\"idempotency_key\":\"{key}\",\"items\":[{{\"item_guid\":{itemGuid},\"wcid\":{RingWcid},\"count\":1}}]{(allowListed == null ? "" : $",\"allow_listed\":{allowListed.Value.ToString().ToLowerInvariant()}")}}}";

        [TestMethod]
        public async Task Post_Is202WithATransferId_AndAReplayAnswersTheSameId()
        {
            var ring = SeedRing();
            var token = await LoginAsync();

            var (status, json) = await Send(Post(token, Body(CharacterGuid, "replay-1", ring.Guid.Full)));
            Assert.AreEqual(HttpStatusCode.Accepted, status);
            var id = json.GetProperty("transfer_id").GetString();
            Assert.IsFalse(string.IsNullOrEmpty(id));

            var (replayStatus, replayJson) = await Send(Post(token, Body(CharacterGuid, "replay-1", ring.Guid.Full)));
            Assert.AreEqual(HttpStatusCode.Accepted, replayStatus);
            Assert.AreEqual(id, replayJson.GetProperty("transfer_id").GetString());
            Assert.AreEqual(1, world.WorldQueue.Count, "the replay enqueued nothing");
        }

        [TestMethod]
        public async Task Get_ByTheOwner_ReportsSnakeCaseStateAndOutcomes()
        {
            var ring = SeedRing();
            var token = await LoginAsync();

            var (_, json) = await Send(Post(token, Body(CharacterGuid, "get-1", ring.Guid.Full)));
            var id = json.GetProperty("transfer_id").GetString();

            var (queuedStatus, queued) = await Send(Get(token, id));
            Assert.AreEqual(HttpStatusCode.OK, queuedStatus);
            Assert.AreEqual("queued", queued.GetProperty("state").GetString());
            Assert.AreEqual("pending", queued.GetProperty("items")[0].GetProperty("outcome").GetString());
            Assert.IsFalse(queued.TryGetProperty("reason", out _), "no reason until something refuses");

            world.Pump();

            var (doneStatus, done) = await Send(Get(token, id));
            Assert.AreEqual(HttpStatusCode.OK, doneStatus);
            Assert.AreEqual(id, done.GetProperty("transfer_id").GetString());
            Assert.AreEqual("completed", done.GetProperty("state").GetString());

            var item = done.GetProperty("items")[0];
            Assert.AreEqual(ring.Guid.Full, item.GetProperty("item_guid").GetUInt32());
            Assert.AreEqual(RingWcid, item.GetProperty("wcid").GetUInt32());
            Assert.AreEqual(1, item.GetProperty("count").GetInt32());
            Assert.AreEqual("delivered", item.GetProperty("outcome").GetString());
            Assert.IsFalse(item.TryGetProperty("class_key", out _));
        }

        [TestMethod]
        public async Task Get_ARejectedTransfer_CarriesTheReasonCode()
        {
            var ring = SeedRing();
            world.GateError = MarketError.NotInVaultArea;
            var token = await LoginAsync();

            var (_, json) = await Send(Post(token, Body(CharacterGuid, "rej-1", ring.Guid.Full)));
            world.Pump();

            var (_, done) = await Send(Get(token, json.GetProperty("transfer_id").GetString()));
            Assert.AreEqual("rejected", done.GetProperty("state").GetString());
            Assert.AreEqual("not_in_vault_area", done.GetProperty("reason").GetString());
            Assert.AreEqual("refused", done.GetProperty("items")[0].GetProperty("outcome").GetString());
        }

        [TestMethod]
        public async Task Get_ByAnotherAccount_Is404TransferNotFound()
        {
            var ring = SeedRing();
            var token = await LoginAsync();

            var (_, json) = await Send(Post(token, Body(CharacterGuid, "own-1", ring.Guid.Full)));
            var id = json.GetProperty("transfer_id").GetString();

            var (status, error) = await Send(Get(await LoginAsync("other"), id));
            Assert.AreEqual(HttpStatusCode.NotFound, status);
            Assert.AreEqual("transfer_not_found", error.GetProperty("error").GetString());
        }

        /// <summary>Account B posting account A's idempotency key gets its own, NEW, string transfer_id.</summary>
        [TestMethod]
        public async Task Post_SameKeyFromAnotherAccount_IsANewTransferId()
        {
            var ring = SeedRing();
            world.Online[(OtherAccountId, OtherCharacterGuid)] = new SuitTransferCharacter(OtherAccountId, OtherCharacterGuid, "Stranger", null, null);

            var (aStatus, a) = await Send(Post(await LoginAsync(), Body(CharacterGuid, "shared-key", ring.Guid.Full)));
            var (bStatus, b) = await Send(Post(await LoginAsync("other"), Body(OtherCharacterGuid, "shared-key", ring.Guid.Full)));

            Assert.AreEqual(HttpStatusCode.Accepted, aStatus);
            Assert.AreEqual(HttpStatusCode.Accepted, bStatus);
            Assert.AreEqual(JsonValueKind.String, a.GetProperty("transfer_id").ValueKind);
            Assert.AreEqual(JsonValueKind.String, b.GetProperty("transfer_id").ValueKind);
            Assert.AreNotEqual(a.GetProperty("transfer_id").GetString(), b.GetProperty("transfer_id").GetString());

            // B polling A's id is 404, not 403: existence is not confirmed.
            var (pollStatus, poll) = await Send(Get(await LoginAsync("other"), a.GetProperty("transfer_id").GetString()));
            Assert.AreEqual(HttpStatusCode.NotFound, pollStatus);
            Assert.AreEqual("transfer_not_found", poll.GetProperty("error").GetString());
        }

        [TestMethod]
        public async Task Post_AnotherAccountsCharacter_Is403NotOwner()
        {
            var ring = SeedRing();

            var (status, error) = await Send(Post(await LoginAsync(), Body(OtherCharacterGuid, "own-2", ring.Guid.Full)));
            Assert.AreEqual(HttpStatusCode.Forbidden, status);
            Assert.AreEqual("not_owner", error.GetProperty("error").GetString());
            Assert.AreEqual(0, world.WorldQueue.Count);
        }

        [TestMethod]
        public async Task Post_OfflineCharacter_Is409CharacterOffline()
        {
            var ring = SeedRing();
            world.Online.Clear();

            var (status, error) = await Send(Post(await LoginAsync(), Body(CharacterGuid, "off-1", ring.Guid.Full)));
            Assert.AreEqual(HttpStatusCode.Conflict, status);
            Assert.AreEqual("character_offline", error.GetProperty("error").GetString());
        }

        [TestMethod]
        public async Task Post_WhenDisabled_Is503SuitBuilderDisabled()
        {
            var ring = SeedRing();
            suitEnabled = false;

            var (status, error) = await Send(Post(await LoginAsync(), Body(CharacterGuid, "dis-1", ring.Guid.Full)));
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
            Assert.AreEqual("suit_builder_disabled", error.GetProperty("error").GetString());
        }

        [TestMethod]
        public async Task Post_MalformedOrOversized_Is400()
        {
            var token = await LoginAsync();

            var (badStatus, bad) = await Send(Post(token, "{\"character_guid\":" + CharacterGuid + ",\"idempotency_key\":\"k\",\"items\":[{\"wcid\":1}]}"));
            Assert.AreEqual(HttpStatusCode.BadRequest, badStatus, "an item with no count");
            Assert.AreEqual("invalid_transfer", bad.GetProperty("error").GetString());

            var many = string.Join(",", Enumerable.Range(0, 25).Select(i => $"{{\"wcid\":{RingWcid},\"count\":1}}"));
            var (manyStatus, manyError) = await Send(Post(token, $"{{\"character_guid\":{CharacterGuid},\"idempotency_key\":\"k\",\"items\":[{many}]}}"));
            Assert.AreEqual(HttpStatusCode.BadRequest, manyStatus);
            Assert.AreEqual("too_many_items", manyError.GetProperty("error").GetString());
        }

        /// <summary>
        /// F8: the line cap is checked straight after the body is read, before any line is built, so 25 lines
        /// that would each be malformed answer too_many_items, never invalid_transfer.
        /// </summary>
        [TestMethod]
        public async Task Post_TooManyLines_IsTooManyItems_BeforeAnyLineIsJudged()
        {
            var token = await LoginAsync();

            var many = string.Join(",", Enumerable.Range(0, 25).Select(i => "{\"wcid\":1}"));
            var (status, error) = await Send(Post(token, $"{{\"character_guid\":{CharacterGuid},\"idempotency_key\":\"k\",\"items\":[{many}]}}"));

            Assert.AreEqual(HttpStatusCode.BadRequest, status);
            Assert.AreEqual("too_many_items", error.GetProperty("error").GetString());
        }

        /// <summary>A valid body sent with a UTF-8 byte order mark is accepted: the suit transfer body reader strips the mark.</summary>
        [TestMethod]
        public async Task Post_WithAByteOrderMark_Is202()
        {
            var ring = SeedRing();
            var token = await LoginAsync();

            var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(Body(CharacterGuid, "bom-1", ring.Guid.Full))).ToArray();
            var request = Post(token, "");
            request.Content = new ByteArrayContent(bytes);
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

            var (status, json) = await Send(request);
            Assert.AreEqual(HttpStatusCode.Accepted, status, json.ValueKind == JsonValueKind.Object ? json.ToString() : "no body");
            Assert.AreEqual(1, world.WorldQueue.Count);
        }

        /// <summary>F8: a body over the 64 KiB cap is invalid_transfer even when it is otherwise a valid request.</summary>
        [TestMethod]
        public async Task Post_BodyOverTheCap_IsInvalidTransfer()
        {
            var ring = SeedRing();
            var token = await LoginAsync();

            var valid = Body(CharacterGuid, "cap-1", ring.Guid.Full);
            var padded = valid.Substring(0, valid.Length - 1) + new string(' ', MarketApiHost.MaxSuitTransferBodyBytes) + "}";

            var (bigStatus, big) = await Send(Post(token, padded));
            Assert.AreEqual(HttpStatusCode.BadRequest, bigStatus);
            Assert.AreEqual("invalid_transfer", big.GetProperty("error").GetString());
            Assert.AreEqual(0, world.WorldQueue.Count);

            // Control: the same request under the cap (still padded) is accepted.
            var underCap = valid.Substring(0, valid.Length - 1) + new string(' ', MarketApiHost.MaxSuitTransferBodyBytes - valid.Length - 16) + "}";
            var (okStatus, _) = await Send(Post(token, underCap));
            Assert.AreEqual(HttpStatusCode.Accepted, okStatus);
        }

        /// <summary>The POST is a WRITE: with a write bucket of one per minute the second POST is rate limited.</summary>
        [TestMethod]
        public async Task Post_TakesTheWriteBucket()
        {
            client.Dispose();
            MarketApiHost.Stop();
            writeRate = 1;
            StartHost();

            var ring = SeedRing();
            var token = await LoginAsync();

            var (first, _) = await Send(Post(token, Body(CharacterGuid, "w-1", ring.Guid.Full)));
            Assert.AreEqual(HttpStatusCode.Accepted, first);

            var (second, error) = await Send(Post(token, Body(CharacterGuid, "w-1", ring.Guid.Full)));
            Assert.AreEqual((HttpStatusCode)429, second);
            Assert.AreEqual("rate_limited", error.GetProperty("error").GetString());

            // The status route is a read and stays available.
            var (_, json) = await Send(Get(token, "unknown"));
            Assert.AreEqual("transfer_not_found", json.GetProperty("error").GetString());
        }
    }
}
