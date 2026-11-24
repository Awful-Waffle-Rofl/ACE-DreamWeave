using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Entity.Actions;
using ACE.Server.Managers.Market;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The class ability web panel routes over a real loopback Kestrel (the WebLaunchRouteTests shape), with a fake
    /// <see cref="IClassAbilityPanelService"/>: the route's own job is the authorization order, the request shape, the
    /// gates and the envelope, and every world-side answer is passed through as the service gives it. The switch is
    /// a Func, so no PropertyManager value is read here.
    /// </summary>
    [TestClass]
    public class ClassAbilityPanelRouteTests
    {
        private const string Key = "panel-test-key";
        private const uint AccountId = 9501;
        private const uint CharacterGuid = 0x50000A01;
        private const uint OtherCharacterGuid = 0x50000A02;
        private const uint ForeignCharacterGuid = 0x50000AFF;

        private HttpClient client;
        private WebLaunchCodeStore store;
        private FakePanelService panel;
        private bool panelEnabled;
        private List<(uint, string)> roster;

        private sealed class FakePanelService : IClassAbilityPanelService
        {
            public string Revision = "abc:1";
            public int Reads;
            public int Applies;
            public bool LastLaunchBound;
            public ClassAbilityPanelChange LastChange;
            public ClassAbilityPanelReadResult ReadAnswer;
            public ClassAbilityPanelChangeResult ApplyAnswer;
            public int DelayMs;

            public string PeekRevision(uint characterGuid) => Revision;

            public ClassAbilityPanelReadResult Read(uint accountId, uint characterGuid, bool launchBound)
            {
                Reads++;
                LastLaunchBound = launchBound;
                if (DelayMs > 0)
                    System.Threading.Thread.Sleep(DelayMs);
                return ReadAnswer ?? new ClassAbilityPanelReadResult(MarketError.None, Snapshot(characterGuid, Revision, launchBound));
            }

            public ClassAbilityPanelChangeResult Apply(uint accountId, uint characterGuid, ClassAbilityPanelChange change)
            {
                Applies++;
                LastChange = change;
                if (DelayMs > 0)
                    System.Threading.Thread.Sleep(DelayMs);
                return ApplyAnswer ?? new ClassAbilityPanelChangeResult(MarketError.None, null, Snapshot(characterGuid, Revision, true));
            }
        }

        private static ClassAbilityPanelSnapshot Snapshot(uint guid, string revision, bool writable) => new ClassAbilityPanelSnapshot
        {
            CharacterGuid = guid,
            Name = "Paneller",
            Level = 100,
            Online = true,
            Revision = revision,
            Writable = writable,
            WriteRefusal = writable ? null : ClassAbilityPanelModel.NotLaunchSessionRefusal,
        };

        [TestInitialize]
        public void Setup()
        {
            store = new WebLaunchCodeStore(() => new DateTime(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc));
            panel = new FakePanelService();
            panelEnabled = true;
            roster = new List<(uint, string)> { (CharacterGuid, "Paneller"), (OtherCharacterGuid, "Alt") };
        }

        [TestCleanup]
        public void Teardown()
        {
            client?.Dispose();
            MarketApiHost.Stop();
        }

        /// <param name="panelBurst">The panel write bucket's capacity; null keeps it equal to <paramref name="panelRate"/>.</param>
        private void Start(int panelRate = 600, int marketWriteRate = 20, bool nullService = false, bool nullSwitch = false, int readRate = 120, int requestTimeoutMs = 3000,
            IClassAbilityPanelService service = null, int? panelBurst = null)
        {
            MarketApiHost.Start(new MarketApiOptions
            {
                ListenUrl = "http://127.0.0.1:0",
                SharedKey = Key,
                MaxInFlight = 8,
                WriteRatePerMinute = marketWriteRate,
                RequestTimeoutMs = requestTimeoutMs,
                AuthenticateAccount = (name, password) => name == "tester" && password == "pw" ? AccountId : 0u,
                ListCharacters = accountId => accountId == AccountId ? roster.ToList() : new List<(uint, string)>(),
                LaunchCodes = store,
                WebLaunchEnabled = () => true,
                ClassAbilityPanel = nullService ? null : (service ?? panel),
                ClassAbilityPanelEnabled = nullSwitch ? null : () => panelEnabled,
                ClassAbilityPanelWriteRatePerMinute = panelRate,
                ClassAbilityPanelWriteBurst = panelBurst ?? panelRate,
                ClassAbilityPanelReadRatePerMinute = readRate,
            });

            client = new HttpClient { BaseAddress = new Uri(MarketApiHost.ListeningUrl) };
        }

        private async Task<(HttpStatusCode status, JsonElement json)> Send(HttpMethod method, string path, string body = null, string bearer = null)
        {
            var request = new HttpRequestMessage(method, path);
            if (body != null)
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");

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

        private async Task<string> PasswordSession()
        {
            var (status, json) = await Send(HttpMethod.Post, "/v1/session", "{\"account_name\":\"tester\",\"password\":\"pw\"}");
            Assert.AreEqual(HttpStatusCode.OK, status);
            return json.GetProperty("session_token").GetString();
        }

        private async Task<string> LaunchSession(uint issuingGuid)
        {
            Assert.IsTrue(store.TryIssue(AccountId, issuingGuid, out var code));
            var (status, json) = await Send(HttpMethod.Post, "/v1/session/launch", "{\"code\":\"" + code + "\"}");
            Assert.AreEqual(HttpStatusCode.OK, status);
            return json.GetProperty("session_token").GetString();
        }

        private static string PanelPath(uint guid) => $"/v1/accounts/me/characters/{guid}/class-abilities/panel";

        private static string ChangesPath(uint guid) => PanelPath(guid) + "/changes";

        private static string Ability => ClassAbilityPanelModel.AbilitySet[0].Name;

        private static string Change(int from, int to, string ability = null) =>
            "{\"ability\":\"" + (ability ?? Ability) + "\",\"from_rank\":" + from + ",\"to_rank\":" + to + "}";

        private static string Code(JsonElement json) => json.GetProperty("error").GetString();

        // ---- GET ----

        [TestMethod]
        public async Task Get_PasswordSession_ReadsTheSnapshot_NotLaunchBound()
        {
            Start();
            var token = await PasswordSession();

            var (status, json) = await Send(HttpMethod.Get, PanelPath(CharacterGuid), bearer: token);

            Assert.AreEqual(HttpStatusCode.OK, status);
            Assert.AreEqual(CharacterGuid, json.GetProperty("character_guid").GetUInt32());
            Assert.AreEqual(1, panel.Reads);
            Assert.IsFalse(panel.LastLaunchBound, "a password session is never launch-bound");
            Assert.AreEqual(JsonValueKind.String, json.GetProperty("write_refusal").ValueKind);
        }

        [TestMethod]
        public async Task Get_LaunchSessionForThisCharacter_IsLaunchBound_AndOtherCharacterIsNot()
        {
            Start();
            var token = await LaunchSession(CharacterGuid);

            Assert.AreEqual(HttpStatusCode.OK, (await Send(HttpMethod.Get, PanelPath(CharacterGuid), bearer: token)).status);
            Assert.IsTrue(panel.LastLaunchBound);

            Assert.AreEqual(HttpStatusCode.OK, (await Send(HttpMethod.Get, PanelPath(OtherCharacterGuid), bearer: token)).status);
            Assert.IsFalse(panel.LastLaunchBound, "a launch session is bound to the issuing character only");
        }

        [TestMethod]
        public async Task Get_NoSession_Is401()
        {
            Start();

            var (status, json) = await Send(HttpMethod.Get, PanelPath(CharacterGuid));

            Assert.AreEqual(HttpStatusCode.Unauthorized, status);
            Assert.AreEqual("bad_credentials", Code(json));
        }

        [TestMethod]
        public async Task Get_AnotherAccountsCharacter_Is403NotOwner()
        {
            Start();
            var token = await PasswordSession();

            var (status, json) = await Send(HttpMethod.Get, PanelPath(ForeignCharacterGuid), bearer: token);

            Assert.AreEqual(HttpStatusCode.Forbidden, status);
            Assert.AreEqual("not_owner", Code(json));
            Assert.AreEqual(0, panel.Reads);
        }

        [TestMethod]
        public async Task Get_Offline_Is200ReadOnly_AsTheServiceAnswers()
        {
            Start();
            var token = await PasswordSession();
            var offline = Snapshot(CharacterGuid, ClassAbilityPanelModel.OfflineRevision, false);
            offline.Online = false;
            offline.WriteRefusal = ClassAbilityPanelModel.OfflineRefusal;
            panel.ReadAnswer = new ClassAbilityPanelReadResult(MarketError.None, offline);

            var (status, json) = await Send(HttpMethod.Get, PanelPath(CharacterGuid), bearer: token);

            Assert.AreEqual(HttpStatusCode.OK, status);
            Assert.IsFalse(json.GetProperty("online").GetBoolean());
            Assert.IsFalse(json.GetProperty("writable").GetBoolean());
            Assert.AreEqual(ClassAbilityPanelModel.OfflineRefusal, json.GetProperty("write_refusal").GetString());
            Assert.AreEqual("offline", json.GetProperty("revision").GetString());
        }

        [TestMethod]
        public async Task Get_SheetBusy_Is503()
        {
            Start();
            var token = await PasswordSession();
            panel.ReadAnswer = new ClassAbilityPanelReadResult(MarketError.SheetBusy, null);

            var (status, json) = await Send(HttpMethod.Get, PanelPath(CharacterGuid), bearer: token);

            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
            Assert.AreEqual("sheet_busy", Code(json));
        }

        [TestMethod]
        public async Task Get_SinceMatches_AnswersUnchanged_WithoutAReadVisit()
        {
            Start();
            var token = await PasswordSession();
            panel.Revision = "e1:7";

            var (status, json) = await Send(HttpMethod.Get, PanelPath(CharacterGuid) + "?since=e1:7", bearer: token);

            Assert.AreEqual(HttpStatusCode.OK, status);
            Assert.IsTrue(json.GetProperty("unchanged").GetBoolean());
            Assert.AreEqual("e1:7", json.GetProperty("revision").GetString());
            Assert.AreEqual(2, json.EnumerateObject().Count(), "the fast path answers exactly {unchanged, revision}");
            Assert.AreEqual(0, panel.Reads, "the fast path must not visit the world thread");
        }

        [TestMethod]
        public async Task Get_SinceDiffers_AnswersAFullSnapshot()
        {
            Start();
            var token = await PasswordSession();
            panel.Revision = "e1:8";

            var (status, json) = await Send(HttpMethod.Get, PanelPath(CharacterGuid) + "?since=e1:7", bearer: token);

            Assert.AreEqual(HttpStatusCode.OK, status);
            Assert.IsFalse(json.TryGetProperty("unchanged", out _));
            Assert.AreEqual("e1:8", json.GetProperty("revision").GetString());
            Assert.AreEqual(1, panel.Reads);
        }

        [TestMethod]
        public async Task Get_SinceOnAnotherAccountsCharacter_IsStill403()
        {
            Start();
            var token = await PasswordSession();
            panel.Revision = "e1:7";

            var (status, json) = await Send(HttpMethod.Get, PanelPath(ForeignCharacterGuid) + "?since=e1:7", bearer: token);

            Assert.AreEqual(HttpStatusCode.Forbidden, status);
            Assert.AreEqual("not_owner", Code(json));
        }

        [TestMethod]
        public async Task Get_PanelSwitchOff_Is503PanelDisabled()
        {
            Start();
            var token = await PasswordSession();
            panelEnabled = false;

            var (status, json) = await Send(HttpMethod.Get, PanelPath(CharacterGuid), bearer: token);

            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
            Assert.AreEqual("panel_disabled", Code(json));
            Assert.AreEqual(0, panel.Reads);
        }

        [TestMethod]
        public async Task Get_UnwiredSwitch_ReadsOff()
        {
            Start(nullSwitch: true);
            var token = await PasswordSession();

            var (status, json) = await Send(HttpMethod.Get, PanelPath(CharacterGuid), bearer: token);

            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
            Assert.AreEqual("panel_disabled", Code(json));
        }

        [TestMethod]
        public async Task Get_Timeout_Is503Timeout()
        {
            Start();
            var token = await PasswordSession();
            panel.ReadAnswer = new ClassAbilityPanelReadResult(MarketError.Timeout, null);

            var (status, json) = await Send(HttpMethod.Get, PanelPath(CharacterGuid), bearer: token);

            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
            Assert.AreEqual("timeout", Code(json));
            Assert.AreEqual(ClassAbilityPanelModel.ReadTimeoutMessage, json.GetProperty("message").GetString());
        }

        [TestMethod]
        public async Task Get_RequestWrapperTimeout_UsesThePanelSentence()
        {
            Start(requestTimeoutMs: 200);
            var token = await PasswordSession();
            panel.DelayMs = 1000;

            var (status, json) = await Send(HttpMethod.Get, PanelPath(CharacterGuid), bearer: token);

            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
            Assert.AreEqual("timeout", Code(json));
            Assert.AreEqual(ClassAbilityPanelModel.ReadTimeoutMessage, json.GetProperty("message").GetString());
        }

        [TestMethod]
        public async Task Get_OwnReadBucket_Is429()
        {
            Start(readRate: 3);
            var token = await PasswordSession();

            for (var i = 0; i < 3; i++)
                Assert.AreEqual(HttpStatusCode.OK, (await Send(HttpMethod.Get, PanelPath(CharacterGuid) + (i == 0 ? "" : "?since=abc:1"), bearer: token)).status, $"read {i}");

            var (status, json) = await Send(HttpMethod.Get, PanelPath(CharacterGuid) + "?since=abc:1", bearer: token);
            Assert.AreEqual(HttpStatusCode.TooManyRequests, status, "the since poll counts against the read bucket too");
            Assert.AreEqual("rate_limited", Code(json));
        }

        [TestMethod]
        public async Task Post_DoesNotSpendTheReadBucket()
        {
            Start(readRate: 1);
            var token = await LaunchSession(CharacterGuid);

            for (var i = 0; i < 3; i++)
                Assert.AreEqual(HttpStatusCode.OK, (await Send(HttpMethod.Post, ChangesPath(CharacterGuid), Change(0, 1), token)).status, $"write {i}");

            Assert.AreEqual(HttpStatusCode.OK, (await Send(HttpMethod.Get, PanelPath(CharacterGuid), bearer: token)).status);
        }

        // ---- POST: authorization, in the contract's order ----

        [TestMethod]
        public async Task Post_PasswordSession_Is403NotLaunchSession()
        {
            Start();
            var token = await PasswordSession();

            var (status, json) = await Send(HttpMethod.Post, ChangesPath(CharacterGuid), Change(0, 1), token);

            Assert.AreEqual(HttpStatusCode.Forbidden, status);
            Assert.AreEqual("not_launch_session", Code(json));
            Assert.AreEqual(0, panel.Applies);
        }

        [TestMethod]
        public async Task Post_LaunchNamingNoCharacter_Is403NotLaunchSession()
        {
            Start();
            var token = await LaunchSession(0);

            var (status, json) = await Send(HttpMethod.Post, ChangesPath(CharacterGuid), Change(0, 1), token);

            Assert.AreEqual(HttpStatusCode.Forbidden, status);
            Assert.AreEqual("not_launch_session", Code(json));
            Assert.AreEqual(0, panel.Applies);
        }

        [TestMethod]
        public async Task Post_LaunchBoundToADifferentCharacter_Is403NotLaunchSession()
        {
            Start();
            var token = await LaunchSession(OtherCharacterGuid);

            var (status, json) = await Send(HttpMethod.Post, ChangesPath(CharacterGuid), Change(0, 1), token);

            Assert.AreEqual(HttpStatusCode.Forbidden, status);
            Assert.AreEqual("not_launch_session", Code(json));
            Assert.AreEqual(0, panel.Applies);
        }

        [TestMethod]
        public async Task Post_NotLaunchSessionBeatsAForeignGuid()
        {
            Start();
            var token = await PasswordSession();

            var (status, json) = await Send(HttpMethod.Post, ChangesPath(ForeignCharacterGuid), Change(0, 1), token);

            Assert.AreEqual(HttpStatusCode.Forbidden, status);
            Assert.AreEqual("not_launch_session", Code(json), "step 1 is checked before step 2");
        }

        [TestMethod]
        public async Task Post_BoundCharacterNoLongerOnTheRoster_Is403NotOwner()
        {
            Start();
            var token = await LaunchSession(CharacterGuid);
            roster.RemoveAll(c => c.Item1 == CharacterGuid);

            var (status, json) = await Send(HttpMethod.Post, ChangesPath(CharacterGuid), Change(0, 1), token);

            Assert.AreEqual(HttpStatusCode.Forbidden, status);
            Assert.AreEqual("not_owner", Code(json));
            Assert.AreEqual(0, panel.Applies);
        }

        [TestMethod]
        public async Task Post_Offline_Is409CharacterOffline()
        {
            Start();
            var token = await LaunchSession(CharacterGuid);
            panel.ApplyAnswer = new ClassAbilityPanelChangeResult(MarketError.CharacterOffline);

            var (status, json) = await Send(HttpMethod.Post, ChangesPath(CharacterGuid), Change(0, 1), token);

            Assert.AreEqual(HttpStatusCode.Conflict, status);
            Assert.AreEqual("character_offline", Code(json));
            Assert.AreEqual(ClassAbilityPanelModel.OfflineRefusal, json.GetProperty("message").GetString(), "the panel sentence, never the vault one");
            Assert.IsFalse(json.TryGetProperty("panel", out _), "omitted when the service has no offline snapshot");
        }

        [TestMethod]
        public async Task Post_Offline_CarriesTheOfflineSnapshot_WhenTheServiceHasOne()
        {
            Start();
            var token = await LaunchSession(CharacterGuid);
            var offline = Snapshot(CharacterGuid, ClassAbilityPanelModel.OfflineRevision, false);
            offline.Online = false;
            offline.WriteRefusal = ClassAbilityPanelModel.OfflineRefusal;
            panel.ApplyAnswer = new ClassAbilityPanelChangeResult(MarketError.CharacterOffline, ClassAbilityPanelModel.OfflineRefusal, offline);

            var (status, json) = await Send(HttpMethod.Post, ChangesPath(CharacterGuid), Change(0, 1), token);

            Assert.AreEqual(HttpStatusCode.Conflict, status);
            Assert.AreEqual("character_offline", Code(json));
            Assert.AreEqual(ClassAbilityPanelModel.OfflineRefusal, json.GetProperty("message").GetString());
            Assert.IsFalse(json.GetProperty("panel").GetProperty("online").GetBoolean());
            Assert.AreEqual("offline", json.GetProperty("panel").GetProperty("revision").GetString());
        }

        [TestMethod]
        public async Task Post_OutsidePermittedArea_Is409WithTheServersSentence()
        {
            Start();
            var token = await LaunchSession(CharacterGuid);
            const string sentence = "You can only change class abilities in the Marketplace.";
            var outside = Snapshot(CharacterGuid, "e:5", false);
            outside.WriteRefusal = sentence;
            panel.ApplyAnswer = new ClassAbilityPanelChangeResult(MarketError.NotInVaultArea, sentence, outside);

            var (status, json) = await Send(HttpMethod.Post, ChangesPath(CharacterGuid), Change(0, 1), token);

            Assert.AreEqual(HttpStatusCode.Conflict, status);
            Assert.AreEqual("not_in_vault_area", Code(json));
            Assert.AreEqual(sentence, json.GetProperty("message").GetString());
            Assert.IsFalse(json.GetProperty("panel").GetProperty("writable").GetBoolean(), "a fresh panel rides on the refusal");
            Assert.AreEqual(sentence, json.GetProperty("panel").GetProperty("write_refusal").GetString());
        }

        [TestMethod]
        public async Task Post_Stale_Is409AndCarriesAFreshPanel()
        {
            Start();
            var token = await LaunchSession(CharacterGuid);
            panel.ApplyAnswer = new ClassAbilityPanelChangeResult(MarketError.Stale, null, Snapshot(CharacterGuid, "e:9", true));

            var (status, json) = await Send(HttpMethod.Post, ChangesPath(CharacterGuid), Change(2, 3), token);

            Assert.AreEqual(HttpStatusCode.Conflict, status);
            Assert.AreEqual("stale", Code(json));
            Assert.AreEqual(MarketErrorCodes.ToMessage(MarketError.Stale), json.GetProperty("message").GetString());
            Assert.AreEqual("e:9", json.GetProperty("panel").GetProperty("revision").GetString());
            Assert.AreEqual(2, panel.LastChange.FromRank);
        }

        [TestMethod]
        public async Task Post_RuleRefused_Is422WithTheSentenceAndAPanel()
        {
            Start();
            var token = await LaunchSession(CharacterGuid);
            const string sentence = "You already hold a training token for X - use it first.";
            panel.ApplyAnswer = new ClassAbilityPanelChangeResult(MarketError.RuleRefused, sentence, Snapshot(CharacterGuid, "e:3", true));

            var (status, json) = await Send(HttpMethod.Post, ChangesPath(CharacterGuid), Change(0, 1), token);

            Assert.AreEqual((HttpStatusCode)422, status);
            Assert.AreEqual("rule_refused", Code(json));
            Assert.AreEqual(sentence, json.GetProperty("message").GetString());
            Assert.AreEqual(JsonValueKind.Object, json.GetProperty("panel").ValueKind);
        }

        [TestMethod]
        public async Task Post_Timeout_Is503Timeout()
        {
            Start();
            var token = await LaunchSession(CharacterGuid);
            panel.ApplyAnswer = new ClassAbilityPanelChangeResult(MarketError.Timeout);

            var (status, json) = await Send(HttpMethod.Post, ChangesPath(CharacterGuid), Change(0, 1), token);

            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
            Assert.AreEqual("timeout", Code(json));
            Assert.AreEqual(ClassAbilityPanelModel.ChangeTimeoutMessage, json.GetProperty("message").GetString());
        }

        [TestMethod]
        public async Task Post_Timeout_RequestWrapper_UsesThePanelSentence()
        {
            Start(requestTimeoutMs: 200);
            var token = await LaunchSession(CharacterGuid);
            panel.DelayMs = 1000;

            var (status, json) = await Send(HttpMethod.Post, ChangesPath(CharacterGuid), Change(0, 1), token);

            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
            Assert.AreEqual("timeout", Code(json));
            Assert.AreEqual(ClassAbilityPanelModel.ChangeTimeoutMessage, json.GetProperty("message").GetString());
        }

        [TestMethod]
        public async Task Post_Applied_Is200WithAppliedAndPanel()
        {
            Start();
            var token = await LaunchSession(CharacterGuid);

            var (status, json) = await Send(HttpMethod.Post, ChangesPath(CharacterGuid), Change(1, 2), token);

            Assert.AreEqual(HttpStatusCode.OK, status);
            Assert.IsTrue(json.GetProperty("applied").GetBoolean());
            Assert.AreEqual(CharacterGuid, json.GetProperty("panel").GetProperty("character_guid").GetUInt32());
            Assert.AreEqual(ClassAbilityPanelChangeKind.Raise, panel.LastChange.Kind);
            Assert.AreEqual(Ability, panel.LastChange.Ability.Name);
        }

        [TestMethod]
        public async Task Post_KindsReachTheService()
        {
            Start();
            var token = await LaunchSession(CharacterGuid);

            Assert.AreEqual(HttpStatusCode.OK, (await Send(HttpMethod.Post, ChangesPath(CharacterGuid), Change(3, 2), token)).status);
            Assert.AreEqual(ClassAbilityPanelChangeKind.Lower, panel.LastChange.Kind);

            Assert.AreEqual(HttpStatusCode.OK, (await Send(HttpMethod.Post, ChangesPath(CharacterGuid), Change(3, 0), token)).status);
            Assert.AreEqual(ClassAbilityPanelChangeKind.Unlearn, panel.LastChange.Kind);

            Assert.AreEqual(HttpStatusCode.OK, (await Send(HttpMethod.Post, ChangesPath(CharacterGuid), Change(1, 0), token)).status);
            Assert.AreEqual(ClassAbilityPanelChangeKind.Unlearn, panel.LastChange.Kind);
        }

        // ---- POST: the request itself ----

        [TestMethod]
        public async Task Post_InvalidToRank_Is400InvalidChange()
        {
            Start();
            var token = await LaunchSession(CharacterGuid);

            foreach (var body in new[] { Change(1, 3), Change(3, 1), Change(0, 0), Change(2, 2), Change(-1, 0) })
            {
                var (status, json) = await Send(HttpMethod.Post, ChangesPath(CharacterGuid), body, token);
                Assert.AreEqual(HttpStatusCode.BadRequest, status, body);
                Assert.AreEqual("invalid_change", Code(json), body);
            }

            Assert.AreEqual(0, panel.Applies);
        }

        [TestMethod]
        public async Task Post_UnknownOrOutOfSetAbility_OrMalformedBody_Is400InvalidChange()
        {
            Start();
            var token = await LaunchSession(CharacterGuid);

            // an unhomed Enhanced-stat ability exists in the registry but is outside the panel set
            var unhomed = ACE.Server.ClassAbilities.ClassAbilityRegistry.Abilities.Values.First(d => d.AbilityClass == ACE.Server.ClassAbilities.ClassAbilityClass.None).Name;

            foreach (var body in new[] { Change(0, 1, "no_such_ability"), Change(0, 1, unhomed), "not json", "{}", "{\"ability\":\"" + Ability + "\",\"to_rank\":1}" })
            {
                var (status, json) = await Send(HttpMethod.Post, ChangesPath(CharacterGuid), body, token);
                Assert.AreEqual(HttpStatusCode.BadRequest, status, body);
                Assert.AreEqual("invalid_change", Code(json), body);
            }

            Assert.AreEqual(0, panel.Applies);
        }

        [TestMethod]
        public async Task Post_PanelSwitchOff_Is503PanelDisabled()
        {
            Start();
            var token = await LaunchSession(CharacterGuid);
            panelEnabled = false;

            var (status, json) = await Send(HttpMethod.Post, ChangesPath(CharacterGuid), Change(0, 1), token);

            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
            Assert.AreEqual("panel_disabled", Code(json));
            Assert.AreEqual(0, panel.Applies);
        }

        [TestMethod]
        public async Task Post_UnwiredService_Is500()
        {
            Start(nullService: true);
            var token = await LaunchSession(CharacterGuid);

            var (status, json) = await Send(HttpMethod.Post, ChangesPath(CharacterGuid), Change(0, 1), token);

            Assert.AreEqual(HttpStatusCode.InternalServerError, status);
            Assert.AreEqual("server_error", Code(json));
        }

        // ---- the panel's own gate and bucket ----

        [TestMethod]
        public async Task Post_OwnWriteBucket_Is429_AndNeverSpendsTheMarketBucket()
        {
            Start(panelRate: 3, marketWriteRate: 1);
            var token = await LaunchSession(CharacterGuid);

            // three panel writes pass although the market's own write bucket holds only one token
            for (var i = 0; i < 3; i++)
                Assert.AreEqual(HttpStatusCode.OK, (await Send(HttpMethod.Post, ChangesPath(CharacterGuid), Change(0, 1), token)).status, $"write {i}");

            var (status, json) = await Send(HttpMethod.Post, ChangesPath(CharacterGuid), Change(0, 1), token);
            Assert.AreEqual(HttpStatusCode.TooManyRequests, status);
            Assert.AreEqual("rate_limited", Code(json));
        }

        /// <summary>The shipped panel write bucket: 20 back to back pass, the 21st in the same burst is rate_limited.</summary>
        [TestMethod]
        public async Task Post_ShippedWriteBucket_The21stInABurst_IsRateLimited()
        {
            var shipped = new MarketApiOptions();
            Start(panelRate: shipped.ClassAbilityPanelWriteRatePerMinute, panelBurst: shipped.ClassAbilityPanelWriteBurst);
            var token = await LaunchSession(CharacterGuid);

            var clock = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 0; i < 20; i++)
                Assert.AreEqual(HttpStatusCode.OK, (await Send(HttpMethod.Post, ChangesPath(CharacterGuid), Change(0, 1), token)).status, $"write {i + 1}");

            var (status, json) = await Send(HttpMethod.Post, ChangesPath(CharacterGuid), Change(0, 1), token);
            clock.Stop();

            // the bucket refills one token a second, so a burst slower than that could legitimately earn a 21st
            if (clock.Elapsed >= TimeSpan.FromSeconds(1) && status == HttpStatusCode.OK)
                Assert.Inconclusive($"the burst took {clock.Elapsed.TotalMilliseconds:0} ms, long enough to refill a token");

            Assert.AreEqual(HttpStatusCode.TooManyRequests, status, "write 21");
            Assert.AreEqual("rate_limited", Code(json));
        }

        [TestMethod]
        public async Task Get_TakesNoWriteToken()
        {
            Start(panelRate: 1);
            var token = await LaunchSession(CharacterGuid);

            for (var i = 0; i < 5; i++)
                Assert.AreEqual(HttpStatusCode.OK, (await Send(HttpMethod.Get, PanelPath(CharacterGuid), bearer: token)).status);

            Assert.AreEqual(HttpStatusCode.OK, (await Send(HttpMethod.Post, ChangesPath(CharacterGuid), Change(0, 1), token)).status);
        }

        [TestMethod]
        public void Defaults_BurstOf20_ThenOneASecond_AndTheWorldWaitIsBelowTheRequestTimeout()
        {
            var o = new MarketApiOptions();

            // a burst of 20 changes back to back, then the refill of 60 a minute (one a second)
            Assert.AreEqual(20, o.ClassAbilityPanelWriteBurst);
            Assert.AreEqual(60, o.ClassAbilityPanelWriteRatePerMinute);
            Assert.AreEqual(120, o.WriteRatePerMinute, "the market's own write bucket is unchanged");
            Assert.AreEqual(8, o.ClassAbilityPanelMaxInFlight);
            Assert.AreEqual(1500, o.ClassAbilityPanelWorldTimeoutMs);
            Assert.IsTrue(o.ClassAbilityPanelWorldTimeoutMs < o.RequestTimeoutMs);
            Assert.AreEqual(120, o.ClassAbilityPanelReadRatePerMinute);
            Assert.IsTrue(o.ClassAbilityPanelReadRatePerMinute >= 30 * 2, "a visible tab polls 30 a minute; room for two");
        }

        [TestMethod]
        public void WorldTimeout_ClampsBelowTheRequestTimeout()
        {
            Assert.AreEqual(1500, MarketApiOptions.EffectivePanelWorldTimeoutMs(1500, 3000), "the default is untouched");
            Assert.AreEqual(1500, MarketApiOptions.EffectivePanelWorldTimeoutMs(5000, 10000), "never above 1500");
            Assert.AreEqual(1000, MarketApiOptions.EffectivePanelWorldTimeoutMs(1500, 1500), "RequestTimeoutMs - 500");
            Assert.AreEqual(100, MarketApiOptions.EffectivePanelWorldTimeoutMs(1500, 300), "never below 100 from the request side");
            Assert.AreEqual(800, MarketApiOptions.EffectivePanelWorldTimeoutMs(800, 3000), "a smaller configured value is kept");
            Assert.AreEqual(1, MarketApiOptions.EffectivePanelWorldTimeoutMs(0, 3000), "a nonsense configured value is floored at 1 ms");
        }

        // ---- the session binding ----

        [TestMethod]
        public void SessionStore_BindsOnlyWhenAsked()
        {
            var sessions = new MarketSessionStore(10);

            var unbound = sessions.Issue(AccountId);
            Assert.IsTrue(sessions.TryResolve(unbound, out var account, out var bound));
            Assert.AreEqual(AccountId, account);
            Assert.AreEqual(0u, bound);

            var launch = sessions.Issue(AccountId, CharacterGuid);
            Assert.IsTrue(sessions.TryResolve(launch, out account, out bound));
            Assert.AreEqual(AccountId, account);
            Assert.AreEqual(CharacterGuid, bound);

            Assert.IsTrue(sessions.TryResolve(launch, out account), "the old overload still resolves");
            Assert.AreEqual(AccountId, account);
        }

        // ---- the live service's offline paths, through the route (no online Player exists in this process) ----

        private sealed class StubSheets : ACE.Server.Managers.CharacterSheets.ICharacterSheetService
        {
            public ACE.Server.Managers.CharacterSheets.OwnerProfileResult Answer;
            public int Calls;

            public ACE.Server.Managers.CharacterSheets.OwnerProfileResult GetOwnerProfile(uint characterGuid)
            {
                Calls++;
                return Answer;
            }

            public ACE.Server.Managers.CharacterSheets.SheetResult GetSheet(string slug) => throw new NotSupportedException();
            public ACE.Server.Managers.CharacterSheets.DirectoryResult ListPublished() => throw new NotSupportedException();
            public ACE.Server.Managers.CharacterSheets.LinkResult GetLink(uint characterGuid) => throw new NotSupportedException();
            public ACE.Server.Managers.CharacterSheets.LinkResult EnableOrRotate(uint characterGuid, bool rotate) => throw new NotSupportedException();
            public ACE.Server.Managers.CharacterSheets.LinkResult Disable(uint characterGuid) => throw new NotSupportedException();
        }

        private static ACE.Server.Managers.CharacterSheets.OwnerProfileResult OfflineProfile()
        {
            var first = ClassAbilityPanelModel.AbilitySet[0];
            var profile = new ACE.Server.Managers.CharacterSheets.OwnerProfile { CharacterGuid = CharacterGuid, Name = "Paneller", Level = 50, Online = false };
            profile.ClassAbilityImport = new ACE.Server.Managers.CharacterSheets.ClassAbilityImportBlock
            {
                PointsAvailable = 4,
                PointsEarned = 9,
                Ranks = new List<ACE.Server.Managers.CharacterSheets.ImportRank> { new ACE.Server.Managers.CharacterSheets.ImportRank { Id = first.Name, Rank = 1 } },
            };
            return new ACE.Server.Managers.CharacterSheets.OwnerProfileResult(ACE.Server.Managers.CharacterSheets.SheetOutcome.Ok, profile);
        }

        private static readonly string[] TierKeys = { "class_ability_tier2_cap_required", "class_ability_tier2_spent_required", "class_ability_tier3_cap_required", "class_ability_tier3_spent_required" };

        /// <summary>The offline snapshot reads the four tier thresholds (ClassAbilityPanelTierRequirements.Live); seed them, then put the shipped defaults back.</summary>
        private static async Task WithTierKeys(Func<Task> body)
        {
            foreach (var key in TierKeys)
                Assert.IsTrue(ACE.Server.Managers.PropertyManager.ModifyLong(key, ACE.Server.Managers.DefaultPropertyManager.DefaultLongProperties[key].Item), key);

            try
            {
                await body();
            }
            finally
            {
                foreach (var key in TierKeys)
                    ACE.Server.Managers.PropertyManager.ModifyLong(key, ACE.Server.Managers.DefaultPropertyManager.DefaultLongProperties[key].Item);
            }
        }

        [TestMethod]
        public Task LivePost_NoOnlinePlayer_SheetsOk_IsOffline_WithTheOfflineSnapshot() => WithTierKeys(async () =>
        {
            var sheets = new StubSheets { Answer = OfflineProfile() };
            Start(service: new LiveClassAbilityPanelService(a => a.Act(), () => sheets, 1500));
            var token = await LaunchSession(CharacterGuid);

            var (status, json) = await Send(HttpMethod.Post, ChangesPath(CharacterGuid), Change(0, 1), token);

            Assert.AreEqual(HttpStatusCode.Conflict, status);
            Assert.AreEqual("character_offline", Code(json));
            Assert.AreEqual(ClassAbilityPanelModel.OfflineRefusal, json.GetProperty("message").GetString());
            Assert.AreEqual(1, sheets.Calls, "control: the offline path was taken");

            var snapshot = json.GetProperty("panel");
            Assert.IsFalse(snapshot.GetProperty("online").GetBoolean());
            Assert.AreEqual("offline", snapshot.GetProperty("revision").GetString());
            Assert.IsFalse(snapshot.GetProperty("writable").GetBoolean());
            Assert.AreEqual(ClassAbilityPanelModel.OfflineRefusal, snapshot.GetProperty("write_refusal").GetString());
            Assert.AreEqual(4, snapshot.GetProperty("points_available").GetInt32());
            Assert.AreEqual(1, snapshot.GetProperty("abilities")[0].GetProperty("rank").GetInt32());

            // Envelope shape only: the live offline panel serializes with no readout key. This is not the readout guard -
            // OfflineSubject.Readout is a constant null - that is Build_Offline_CarriesNoReadout_AndNeverAsks (rules tests).
            Assert.IsTrue(snapshot.GetProperty("abilities").EnumerateArray().All(a => !a.TryGetProperty("readout", out _)),
                "envelope shape: an offline panel's ability rows carry no readout key");
        });

        /// <summary>The switches are re-read on the world thread: off by then answers panel_disabled (no player online: no panel).</summary>
        [TestMethod]
        public async Task LivePost_SwitchOffAtApplyTime_IsPanelDisabled()
        {
            var sheets = new StubSheets { Answer = OfflineProfile() };
            var applyTimeSwitch = false;
            Start(service: new LiveClassAbilityPanelService(a => a.Act(), () => sheets, 1500, panelEnabled: () => applyTimeSwitch));
            var token = await LaunchSession(CharacterGuid);

            var (status, json) = await Send(HttpMethod.Post, ChangesPath(CharacterGuid), Change(0, 1), token);

            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
            Assert.AreEqual("panel_disabled", Code(json));
            Assert.AreEqual("The class ability panel is turned off on this server.", json.GetProperty("message").GetString());
            Assert.IsFalse(json.TryGetProperty("panel", out _), "no character online, so no snapshot");
            Assert.AreEqual(0, sheets.Calls, "refused before the offline path");
        }

        /// <summary>
        /// A world-thread fault (here the switch read itself throws) answers like a timeout, outcome unknown, because a
        /// fault can come after the change applied.
        /// </summary>
        [TestMethod]
        public async Task LivePost_WorldFault_AnswersTheTimeoutShape()
        {
            Start(service: new LiveClassAbilityPanelService(a => a.Act(), () => null, 1500,
                panelEnabled: () => throw new InvalidOperationException("the world call faulted")));
            var token = await LaunchSession(CharacterGuid);

            var (status, json) = await Send(HttpMethod.Post, ChangesPath(CharacterGuid), Change(0, 1), token);

            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
            Assert.AreEqual("timeout", Code(json));
            Assert.AreEqual(ClassAbilityPanelModel.ChangeTimeoutMessage, json.GetProperty("message").GetString());
        }

        [TestMethod]
        public Task LivePost_NoOnlinePlayer_SheetsBusyOrUnwired_IsOffline_WithNoPanel() => WithTierKeys(async () =>
        {
            var sheets = new StubSheets { Answer = new ACE.Server.Managers.CharacterSheets.OwnerProfileResult(ACE.Server.Managers.CharacterSheets.SheetOutcome.Busy, null) };
            var useNull = false;
            Start(service: new LiveClassAbilityPanelService(a => a.Act(), () => useNull ? null : sheets, 1500));
            var token = await LaunchSession(CharacterGuid);

            foreach (var unwired in new[] { false, true })
            {
                useNull = unwired;

                var (status, json) = await Send(HttpMethod.Post, ChangesPath(CharacterGuid), Change(0, 1), token);

                Assert.AreEqual(HttpStatusCode.Conflict, status, unwired ? "sheets null" : "sheets busy");
                Assert.AreEqual("character_offline", Code(json));
                Assert.AreEqual(ClassAbilityPanelModel.OfflineRefusal, json.GetProperty("message").GetString());
                Assert.IsFalse(json.TryGetProperty("panel", out _), unwired ? "sheets null: no panel" : "sheets busy: no panel");
            }

            Assert.AreEqual(1, sheets.Calls, "control: the busy stub was asked once, the null case never reached it");
        });

        [TestMethod]
        public async Task LiveGet_TierRows_CarryTheSeededThresholds()
        {
            try
            {
                ACE.Server.Managers.PropertyManager.ModifyLong(TierKeys[0], 13);
                ACE.Server.Managers.PropertyManager.ModifyLong(TierKeys[1], 14);
                ACE.Server.Managers.PropertyManager.ModifyLong(TierKeys[2], 23);
                ACE.Server.Managers.PropertyManager.ModifyLong(TierKeys[3], 24);

                var sheets = new StubSheets { Answer = OfflineProfile() };
                Start(service: new LiveClassAbilityPanelService(a => a.Act(), () => sheets, 1500));
                var token = await PasswordSession();

                var (status, json) = await Send(HttpMethod.Get, PanelPath(CharacterGuid), bearer: token);

                Assert.AreEqual(HttpStatusCode.OK, status);
                foreach (var cls in json.GetProperty("classes").EnumerateArray())
                {
                    var tiers = cls.GetProperty("tiers");
                    Assert.AreEqual(0, tiers[0].GetProperty("earned_required").GetInt32());
                    Assert.AreEqual(0, tiers[0].GetProperty("spent_required").GetInt32());
                    Assert.AreEqual(13, tiers[1].GetProperty("earned_required").GetInt32());
                    Assert.AreEqual(14, tiers[1].GetProperty("spent_required").GetInt32());
                    Assert.AreEqual(23, tiers[2].GetProperty("earned_required").GetInt32());
                    Assert.AreEqual(24, tiers[2].GetProperty("spent_required").GetInt32());

                    // the stub character earned 9: below both thresholds, so tiers 2 and 3 are locked with a sentence
                    Assert.IsFalse(tiers[1].GetProperty("unlocked").GetBoolean());
                    Assert.AreEqual(JsonValueKind.String, tiers[1].GetProperty("requirement").ValueKind);
                }
            }
            finally
            {
                foreach (var key in TierKeys)
                    ACE.Server.Managers.PropertyManager.ModifyLong(key, ACE.Server.Managers.DefaultPropertyManager.DefaultLongProperties[key].Item);
            }
        }

        [TestMethod]
        public Task LiveGet_NoOnlinePlayer_ReadsTheOfflinePath() => WithTierKeys(async () =>
        {
            var sheets = new StubSheets { Answer = OfflineProfile() };
            var useNull = false;
            Start(service: new LiveClassAbilityPanelService(a => a.Act(), () => useNull ? null : sheets, 1500));
            var token = await LaunchSession(CharacterGuid);

            var (status, json) = await Send(HttpMethod.Get, PanelPath(CharacterGuid), bearer: token);

            Assert.AreEqual(HttpStatusCode.OK, status);
            Assert.IsFalse(json.GetProperty("online").GetBoolean());
            Assert.AreEqual("offline", json.GetProperty("revision").GetString());
            Assert.AreEqual(ClassAbilityPanelModel.OfflineRefusal, json.GetProperty("write_refusal").GetString(), "launch-bound but offline");

            // the since fast path for an offline character answers on the fixed revision without another read
            var (fastStatus, fast) = await Send(HttpMethod.Get, PanelPath(CharacterGuid) + "?since=offline", bearer: token);
            Assert.AreEqual(HttpStatusCode.OK, fastStatus);
            Assert.IsTrue(fast.GetProperty("unchanged").GetBoolean());
            Assert.AreEqual(1, sheets.Calls);

            useNull = true;
            var (nullStatus, nullJson) = await Send(HttpMethod.Get, PanelPath(CharacterGuid), bearer: token);
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, nullStatus);
            Assert.AreEqual("sheets_disabled", Code(nullJson));
        });

        // ---- the live service's world wait ----

        [TestMethod]
        public void LiveService_WorldNeverRunsTheChange_IsTimeout_AndTheActionSkipsItselfLater()
        {
            var queued = new ConcurrentQueue<IAction>();
            var service = new LiveClassAbilityPanelService(queued.Enqueue, () => null, 50);
            var change = new ClassAbilityPanelChange(ClassAbilityPanelModel.AbilitySet[0], 0, ClassAbilityPanelChangeKind.Raise);

            var result = service.Apply(AccountId, CharacterGuid, change);

            Assert.AreEqual(MarketError.Timeout, result.Error);
            Assert.AreEqual(ClassAbilityPanelModel.ChangeTimeoutMessage, result.Message);
            Assert.AreEqual(1, queued.Count, "control: the change was queued on the world");

            // the world gets to it late: abandoned, so it must skip the work rather than touch a Player
            Assert.IsTrue(queued.TryDequeue(out var late));
            late.Act();
        }
    }
}
