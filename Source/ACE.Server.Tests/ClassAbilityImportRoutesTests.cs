using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.ClassAbilities;
using ACE.Server.Managers;
using ACE.Server.Managers.CharacterSheets;
using ACE.Server.Managers.Market;

namespace ACE.Server.Tests
{
    /// <summary>
    /// GET /v1/accounts/me/characters/{guid}/class-abilities over a real loopback Kestrel with fakes behind every
    /// seam (the SuitRoutesTests shape).
    /// </summary>
    [TestClass]
    public class ClassAbilityImportRoutesTests
    {
        private const string Key = "caimport-test-key";
        private const uint AccountId = 9401;
        private const uint CharacterGuid = 0x50000601;
        private const uint StrangerCharacter = 0x50000602;

        private FakeMarketRepository repo;
        private FakeMarketItemStore store;
        private FakeMarketWallet wallet;
        private FakeCharacterSheetService sheets;
        private HttpClient client;

        [TestInitialize]
        public void Setup()
        {
            repo = new FakeMarketRepository();
            store = new FakeMarketItemStore();
            wallet = new FakeMarketWallet();
            sheets = new FakeCharacterSheetService();

            MarketManagerTests.SeedMarketTunables();
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
                    ? new List<(uint, string)> { (CharacterGuid, "Importee") }
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
        }

        private async Task<string> LoginAsync()
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/v1/session") { Content = new StringContent("{\"account_name\":\"tester\",\"password\":\"pw\"}", Encoding.UTF8, "application/json") };
            request.Headers.Add("X-Market-Key", Key);

            var (status, json) = await Send(request);
            Assert.AreEqual(HttpStatusCode.OK, status);
            return json.GetProperty("session_token").GetString();
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

        private async Task<(HttpStatusCode status, JsonElement json)> Import(string guid, string query = "")
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/accounts/me/characters/{guid}/class-abilities{query}");
            request.Headers.Add("X-Market-Key", Key);
            request.Headers.Add("Authorization", $"Bearer {await LoginAsync()}");
            return await Send(request);
        }

        private static string Quests(IEnumerable<string> names) => "?" + string.Join("&", names.Select(n => "quest=" + Uri.EscapeDataString(n)));

        private void SeedProfile(params ImportQuestRow[] rows)
        {
            sheets.NextOwnerProfile = new OwnerProfileResult(SheetOutcome.Ok, new OwnerProfile
            {
                CharacterGuid = CharacterGuid,
                Name = "Importee",
                Level = 180,
                Online = true,
                ClassAbilityImport = ClassAbilityImportProjection.Project(rows, 5, 2, 44, 6, true),
            });
        }

        [TestMethod]
        public async Task OtherAccountsCharacter_Is403NotOwner_AndTheServiceIsNotCalled()
        {
            var (status, json) = await Import(StrangerCharacter.ToString());

            Assert.AreEqual(HttpStatusCode.Forbidden, status);
            Assert.AreEqual("not_owner", json.GetProperty("error").GetString());
            Assert.AreEqual(0, sheets.GetOwnerProfileCalls);
        }

        [TestMethod]
        public async Task GuidOutOfRange_IsNotOwner_AndNonNumericIs404AtRouting()
        {
            foreach (var guid in new[] { "0", "-5", "99999999999" })
            {
                var (status, json) = await Import(guid);
                Assert.AreEqual(HttpStatusCode.Forbidden, status, guid);
                Assert.AreEqual("not_owner", json.GetProperty("error").GetString(), guid);
            }

            var (routing, _) = await Import("abc");
            Assert.AreEqual(HttpStatusCode.NotFound, routing);
            Assert.AreEqual(0, sheets.GetOwnerProfileCalls);
        }

        [TestMethod]
        public async Task OwnCharacter_ReturnsTheDocumentedShape()
        {
            var multishot = ClassAbilityRegistry.Abilities.Values.First(d => d.Implemented);
            var old = RetiredClassAbilities.All().First();

            SeedProfile(
                new ImportQuestRow(ClassAbilityRegistry.QuestKeyPrefix + multishot.Name, 1),
                new ImportQuestRow(ClassAbilityRegistry.QuestKeyPrefix + old.Name, 1),
                new ImportQuestRow("SomeCapQuest", 1));

            var (status, json) = await Import(CharacterGuid.ToString(), Quests(new[] { "SomeCapQuest" }));

            Assert.AreEqual(HttpStatusCode.OK, status);
            Assert.AreEqual(CharacterGuid, sheets.LastOwnerProfileGuid);
            Assert.AreEqual(CharacterGuid, json.GetProperty("character_guid").GetUInt32());
            Assert.AreEqual("Importee", json.GetProperty("name").GetString());
            Assert.AreEqual(180, json.GetProperty("level").GetInt32());
            Assert.IsTrue(json.GetProperty("online").GetBoolean());
            Assert.AreEqual(5, json.GetProperty("lum_points_bought").GetInt32());
            Assert.AreEqual(2, json.GetProperty("xp_points_bought").GetInt32());
            Assert.AreEqual(44, json.GetProperty("points_earned").GetInt32());
            Assert.AreEqual(6, json.GetProperty("points_available").GetInt32());
            Assert.IsTrue(json.GetProperty("respec_pending").GetBoolean());

            CollectionAssert.AreEqual(new[] { "SomeCapQuest" }, json.GetProperty("quests_completed").EnumerateArray().Select(e => e.GetString()).ToArray());

            var rank = json.GetProperty("ranks").EnumerateArray().Single();
            Assert.AreEqual(multishot.Name, rank.GetProperty("id").GetString());
            Assert.AreEqual(1, rank.GetProperty("rank").GetInt32());

            var retired = json.GetProperty("retired_ranks").EnumerateArray().Single();
            Assert.AreEqual(old.Name, retired.GetProperty("id").GetString());

            var names = json.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            CollectionAssert.AreEqual(new[]
            {
                "character_guid", "level", "lum_points_bought", "name", "online", "points_available", "points_earned",
                "quests_completed", "ranks", "respec_pending", "retired_ranks", "xp_points_bought",
            }, names);
        }

        [TestMethod]
        public async Task NoQuestParams_YieldsAnEmptyQuestList_NotAnError()
        {
            SeedProfile(new ImportQuestRow("SomeCapQuest", 1));

            var (status, json) = await Import(CharacterGuid.ToString());

            Assert.AreEqual(HttpStatusCode.OK, status);
            Assert.AreEqual(0, json.GetProperty("quests_completed").GetArrayLength());
        }

        [TestMethod]
        public async Task Busy_IsSheetBusy503_NotFound_IsSheetNotFound404()
        {
            sheets.NextOwnerProfile = new OwnerProfileResult(SheetOutcome.Busy, null);
            var (busyStatus, busy) = await Import(CharacterGuid.ToString());
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, busyStatus);
            Assert.AreEqual("sheet_busy", busy.GetProperty("error").GetString());

            sheets.NextOwnerProfile = new OwnerProfileResult(SheetOutcome.NotFound, null);
            var (missingStatus, missing) = await Import(CharacterGuid.ToString());
            Assert.AreEqual(HttpStatusCode.NotFound, missingStatus);
            Assert.AreEqual("sheet_not_found", missing.GetProperty("error").GetString());
        }

        [TestMethod]
        public async Task NoSession_Is401BadCredentials()
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/accounts/me/characters/{CharacterGuid}/class-abilities");
            request.Headers.Add("X-Market-Key", Key);

            var (status, json) = await Send(request);

            Assert.AreEqual(HttpStatusCode.Unauthorized, status);
            Assert.AreEqual("bad_credentials", json.GetProperty("error").GetString());
        }

        [TestMethod]
        public async Task QuestFilter_EchoesRequestedSpelling_IgnoresMalformed_AndTheValuesPastTheCap()
        {
            var rows = Enumerable.Range(0, 40).Select(i => new ImportQuestRow($"Quest{i}", 1)).Concat(new[] { new ImportQuestRow("bad name", 1), new ImportQuestRow("quest-1", 1) }).ToArray();
            SeedProfile(rows);

            // malformed values first: they must be ignored, not abort the request, and the cap counts raw values
            var requested = new List<string> { "bad name", "QUEST0", "quest-1" };
            requested.AddRange(Enumerable.Range(2, 38).Select(i => $"Quest{i}"));

            var (status, json) = await Import(CharacterGuid.ToString(), Quests(requested));
            Assert.AreEqual(HttpStatusCode.OK, status);

            var echoed = json.GetProperty("quests_completed").EnumerateArray().Select(e => e.GetString()).ToArray();

            Assert.AreEqual("QUEST0", echoed[0], "echoed in the requested spelling");
            Assert.IsFalse(echoed.Contains("bad name"));
            Assert.IsFalse(echoed.Contains("quest-1"));
            Assert.AreEqual(ClassAbilityImportProjection.MaxQuestParams - 2, echoed.Length, "32 raw values read, 2 of them malformed");
            Assert.IsFalse(echoed.Contains("Quest39"), "a value past the 32nd is ignored");
        }

        [TestMethod]
        public async Task QuestFilter_AMalformedOnlyList_IsStillA200()
        {
            SeedProfile(new ImportQuestRow("SomeCapQuest", 1));

            var (status, json) = await Import(CharacterGuid.ToString(), Quests(new[] { "has space", new string('a', 65), "semi;colon" }));

            Assert.AreEqual(HttpStatusCode.OK, status);
            Assert.AreEqual(0, json.GetProperty("quests_completed").GetArrayLength());
        }

        [TestMethod]
        public async Task MissingImportBlock_IsServerError500_NeverAnEmptyBuild()
        {
            sheets.NextOwnerProfile = new OwnerProfileResult(SheetOutcome.Ok, new OwnerProfile { CharacterGuid = CharacterGuid, Name = "Importee", Level = 180 });

            var (status, json) = await Import(CharacterGuid.ToString());

            Assert.AreEqual(HttpStatusCode.InternalServerError, status);
            Assert.AreEqual("server_error", json.GetProperty("error").GetString());
        }
    }
}
