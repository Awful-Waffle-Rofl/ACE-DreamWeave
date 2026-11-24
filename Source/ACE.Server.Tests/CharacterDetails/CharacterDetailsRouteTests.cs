using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Managers.Market;

namespace ACE.Server.Tests.CharacterDetails
{
    /// <summary>
    /// GET /v1/accounts/me/characters/{guid}/details over a real loopback Kestrel with a fake
    /// <see cref="ICharacterDetailsService"/> (the ClassAbilityPanelRouteTests shape). The switch is a Func, so no
    /// PropertyManager value is read here.
    /// </summary>
    [TestClass]
    public class CharacterDetailsRouteTests
    {
        private const string Key = "details-test-key";
        private const uint AccountId = 9601;
        private const uint CharacterGuid = 0x50000B01;
        private const uint ForeignCharacterGuid = 0x50000BFF;

        private HttpClient client;
        private FakeDetails details;
        private bool enabled;

        private sealed class FakeDetails : ICharacterDetailsService
        {
            public int Reads;
            public CharacterDetailsReadResult Answer;

            public CharacterDetailsReadResult Read(uint accountId, uint characterGuid)
            {
                Reads++;
                return Answer ?? new CharacterDetailsReadResult(MarketError.None, new ACE.Server.Managers.Market.CharacterDetails
                {
                    CharacterGuid = characterGuid,
                    Level = 300,
                    BaselineAvailable = true,
                    BaselineNote = CharacterDetailsProjector.BaselineTooltip,
                    Sections = new List<CharacterDetailsSection>
                    {
                        new CharacterDetailsSection { Key = "offense", Title = "Offense", Kind = "list",
                            Rows = new List<CharacterDetailsRow> { new CharacterDetailsRow { Key = "hit_melee", Label = "Melee hit chance", Value = "87.3%" } } },
                    },
                });
            }
        }

        private sealed class FakePanel : IClassAbilityPanelService
        {
            public string PeekRevision(uint characterGuid) => "r:1";
            public ClassAbilityPanelReadResult Read(uint accountId, uint characterGuid, bool launchBound)
                => new ClassAbilityPanelReadResult(MarketError.None, new ClassAbilityPanelSnapshot { CharacterGuid = characterGuid, Revision = "r:1" });
            public ClassAbilityPanelChangeResult Apply(uint accountId, uint characterGuid, ClassAbilityPanelChange change)
                => new ClassAbilityPanelChangeResult(MarketError.InvalidChange);
        }

        [TestInitialize]
        public void Setup()
        {
            details = new FakeDetails();
            enabled = true;
        }

        [TestCleanup]
        public void Teardown()
        {
            client?.Dispose();
            MarketApiHost.Stop();
        }

        private void Start(bool nullService = false, bool nullSwitch = false, int readRate = 120)
        {
            MarketApiHost.Start(new MarketApiOptions
            {
                ListenUrl = "http://127.0.0.1:0",
                SharedKey = Key,
                MaxInFlight = 8,
                RequestTimeoutMs = 3000,
                AuthenticateAccount = (name, password) => name == "tester" && password == "pw" ? AccountId : 0u,
                ListCharacters = accountId => accountId == AccountId ? new List<(uint, string)> { (CharacterGuid, "Detailer") } : new List<(uint, string)>(),
                ClassAbilityPanel = new FakePanel(),
                ClassAbilityPanelEnabled = () => true,
                ClassAbilityPanelReadRatePerMinute = readRate,
                CharacterDetails = nullService ? null : details,
                CharacterDetailsEnabled = nullSwitch ? null : () => enabled,
            });

            client = new HttpClient { BaseAddress = new Uri(MarketApiHost.ListeningUrl) };
        }

        private async Task<(HttpStatusCode status, JsonElement json)> Get(string path, string bearer)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Add("X-Market-Key", Key);
            if (bearer != null)
                request.Headers.Add("Authorization", $"Bearer {bearer}");

            using var response = await client.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();

            if (string.IsNullOrWhiteSpace(text))
                return (response.StatusCode, default);

            using var doc = JsonDocument.Parse(text);
            return (response.StatusCode, doc.RootElement.Clone());
        }

        private async Task<string> Session()
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/v1/session")
            {
                Content = new StringContent("{\"account_name\":\"tester\",\"password\":\"pw\"}", Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("X-Market-Key", Key);

            using var response = await client.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return doc.RootElement.GetProperty("session_token").GetString();
        }

        private static string DetailsPath(uint guid) => $"/v1/accounts/me/characters/{guid}/details";

        private static string Code(JsonElement json) => json.GetProperty("error").GetString();

        [TestMethod]
        public async Task Get_OwnOnlineCharacter_Is200WithTheSnapshot()
        {
            Start();
            var (status, json) = await Get(DetailsPath(CharacterGuid), await Session());

            Assert.AreEqual(HttpStatusCode.OK, status);
            Assert.AreEqual(CharacterGuid, json.GetProperty("character_guid").GetUInt32());
            Assert.AreEqual(CharacterDetailsProjector.BaselineTooltip, json.GetProperty("baseline_note").GetString());
            Assert.AreEqual("hit_melee", json.GetProperty("sections")[0].GetProperty("rows")[0].GetProperty("key").GetString());
            Assert.AreEqual(1, details.Reads);
        }

        [TestMethod]
        public async Task Get_SwitchOff_Is503DetailsDisabled_WithoutAServiceCall()
        {
            Start();
            var token = await Session();

            enabled = false;
            var (status, json) = await Get(DetailsPath(CharacterGuid), token);
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
            Assert.AreEqual("details_disabled", Code(json));
            Assert.AreEqual(0, details.Reads);
        }

        [TestMethod]
        public async Task Get_NullSwitch_ReadsOff()
        {
            Start(nullSwitch: true);
            var (status, json) = await Get(DetailsPath(CharacterGuid), await Session());
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
            Assert.AreEqual("details_disabled", Code(json));
        }

        [TestMethod]
        public async Task Get_NotOnTheRoster_Is403NotOwner()
        {
            Start();
            var (status, json) = await Get(DetailsPath(ForeignCharacterGuid), await Session());
            Assert.AreEqual(HttpStatusCode.Forbidden, status);
            Assert.AreEqual("not_owner", Code(json));
            Assert.AreEqual(0, details.Reads);
        }

        [TestMethod]
        public async Task Get_Offline_Is409CharacterOffline()
        {
            Start();
            details.Answer = new CharacterDetailsReadResult(MarketError.CharacterOffline, null);
            var (status, json) = await Get(DetailsPath(CharacterGuid), await Session());
            Assert.AreEqual(HttpStatusCode.Conflict, status);
            Assert.AreEqual("character_offline", Code(json));
        }

        [TestMethod]
        public async Task Get_Timeout_Is503Timeout_WithTheDetailsSentence()
        {
            Start();
            details.Answer = new CharacterDetailsReadResult(MarketError.Timeout, null);
            var (status, json) = await Get(DetailsPath(CharacterGuid), await Session());
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
            Assert.AreEqual("timeout", Code(json));
            Assert.AreEqual(MarketApiHost.CharacterDetailsTimeoutMessage, json.GetProperty("message").GetString());
        }

        [TestMethod]
        public async Task Get_NoService_Is500()
        {
            Start(nullService: true);
            var (status, json) = await Get(DetailsPath(CharacterGuid), await Session());
            Assert.AreEqual(HttpStatusCode.InternalServerError, status);
            Assert.AreEqual("server_error", Code(json));
        }

        [TestMethod]
        public async Task Get_NoSession_Is401()
        {
            Start();
            var (status, _) = await Get(DetailsPath(CharacterGuid), null);
            Assert.AreEqual(HttpStatusCode.Unauthorized, status);
            Assert.AreEqual(0, details.Reads);
        }

        [TestMethod]
        public async Task Get_SharesThePanelReadBucket()
        {
            Start(readRate: 1);
            var token = await Session();

            Assert.AreEqual(HttpStatusCode.OK, (await Get($"/v1/accounts/me/characters/{CharacterGuid}/class-abilities/panel", token)).status, "the panel read takes the one token");

            var (status, json) = await Get(DetailsPath(CharacterGuid), token);
            Assert.AreEqual((HttpStatusCode)429, status, "the details read draws on the same per-account panel read bucket");
            Assert.AreEqual(0, details.Reads);
        }

        [TestMethod]
        public void ErrorCode_DetailsDisabled_IsPublished()
        {
            Assert.AreEqual("details_disabled", MarketErrorCodes.ToCode(MarketError.DetailsDisabled));
            Assert.AreEqual("Character details are turned off on this server.", MarketErrorCodes.ToMessage(MarketError.DetailsDisabled));
        }
    }
}
