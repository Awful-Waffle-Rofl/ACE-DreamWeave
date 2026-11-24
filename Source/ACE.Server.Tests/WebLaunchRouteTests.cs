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

namespace ACE.Server.Tests
{
    /// <summary>
    /// POST /v1/session/launch over a real loopback Kestrel (the SuitRoutesTests shape). The route reads
    /// a Func&lt;bool&gt; and an injected store, so no PropertyManager value is read here.
    /// </summary>
    [TestClass]
    public class WebLaunchRouteTests
    {
        private const string Key = "launch-test-key";
        private const uint AccountId = 9401;
        private const uint CharacterGuid = 0x50000901;

        private HttpClient client;
        private WebLaunchCodeStore store;
        private DateTime now;
        private bool launchEnabled;

        [TestInitialize]
        public void Setup()
        {
            now = new DateTime(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);
            store = new WebLaunchCodeStore(() => now);
            launchEnabled = true;
        }

        [TestCleanup]
        public void Teardown()
        {
            client?.Dispose();
            MarketApiHost.Stop();
        }

        private void Start(int launchRate = 120, int loginRate = 30, bool nullStore = false, bool nullSwitch = false)
        {
            MarketApiHost.Start(new MarketApiOptions
            {
                ListenUrl = "http://127.0.0.1:0",
                SharedKey = Key,
                MaxInFlight = 8,
                WriteRatePerMinute = 20,
                RequestTimeoutMs = 3000,
                LoginRatePerMinutePerIp = loginRate,
                LaunchRatePerMinutePerIp = launchRate,
                AuthenticateAccount = (name, password) => name == "tester" && password == "pw" ? AccountId : 0u,
                ListCharacters = accountId => accountId == AccountId
                    ? new List<(uint, string)> { (CharacterGuid, "Launcher") }
                    : new List<(uint, string)>(),
                LaunchCodes = nullStore ? null : store,
                WebLaunchEnabled = nullSwitch ? null : () => launchEnabled,
            });

            client = new HttpClient { BaseAddress = new Uri(MarketApiHost.ListeningUrl) };
        }

        private string Issue(uint guid = 0)
        {
            Assert.IsTrue(store.TryIssue(AccountId, guid, out var code));
            return code;
        }

        private async Task<(HttpStatusCode status, JsonElement json, string text)> Post(string path, string body, string key = Key, string bearer = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

            if (key != null)
                request.Headers.Add("X-Market-Key", key);

            if (bearer != null)
                request.Headers.Add("Authorization", $"Bearer {bearer}");

            using var response = await client.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();

            if (string.IsNullOrWhiteSpace(text))
                return (response.StatusCode, default, text);

            using var doc = JsonDocument.Parse(text);
            return (response.StatusCode, doc.RootElement.Clone(), text);
        }

        private Task<(HttpStatusCode status, JsonElement json, string text)> Launch(string code, string key = Key)
            => Post("/v1/session/launch", "{\"code\":\"" + code + "\"}", key);

        private static string Keys(JsonElement json) => string.Join(",", json.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));

        [TestMethod]
        public async Task Launch_NoKey_Is401WithZeroBytes()
        {
            Start();
            var code = Issue();

            var (status, _, text) = await Launch(code, key: null);

            Assert.AreEqual(HttpStatusCode.Unauthorized, status);
            Assert.AreEqual(0, text.Length);
            Assert.AreEqual(1, store.Count, "a keyless call must not touch the store");
        }

        [TestMethod]
        public async Task Launch_ValidCode_ReturnsTheSessionShape()
        {
            Start();

            var (loginStatus, login, _) = await Post("/v1/session", "{\"account_name\":\"tester\",\"password\":\"pw\"}");
            Assert.AreEqual(HttpStatusCode.OK, loginStatus);

            var (status, json, _) = await Launch(Issue());

            Assert.AreEqual(HttpStatusCode.OK, status);
            Assert.AreEqual(Keys(login), Keys(json), "no character was named, so the keys equal /v1/session's");
            Assert.AreEqual(AccountId, json.GetProperty("account_id").GetUInt32());
            Assert.AreEqual(1, json.GetProperty("characters").GetArrayLength());

            var request = new HttpRequestMessage(HttpMethod.Get, "/v1/accounts/me");
            request.Headers.Add("X-Market-Key", Key);
            request.Headers.Add("Authorization", "Bearer " + json.GetProperty("session_token").GetString());
            using var me = await client.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, me.StatusCode);
            using var meDoc = JsonDocument.Parse(await me.Content.ReadAsStringAsync());
            Assert.AreEqual(AccountId, meDoc.RootElement.GetProperty("account_id").GetUInt32());
        }

        [TestMethod]
        public async Task Launch_ReturnsTheIssuingCharacter()
        {
            Start();

            var (_, json, _) = await Launch(Issue(CharacterGuid));

            Assert.AreEqual(CharacterGuid, json.GetProperty("character_guid").GetUInt32());
        }

        [TestMethod]
        public async Task Launch_OmitsACharacterNotOnTheAccount()
        {
            Start();

            var (status, json, _) = await Launch(Issue(0x50000999));

            Assert.AreEqual(HttpStatusCode.OK, status);
            Assert.IsFalse(json.TryGetProperty("character_guid", out _));
        }

        [TestMethod]
        public async Task Launch_SecondUse_Is401BadCredentials()
        {
            Start();
            var code = Issue();

            Assert.AreEqual(HttpStatusCode.OK, (await Launch(code)).status);

            var (status, json, _) = await Launch(code);
            Assert.AreEqual(HttpStatusCode.Unauthorized, status);
            Assert.AreEqual("bad_credentials", json.GetProperty("error").GetString());
        }

        [TestMethod]
        public async Task Launch_Expired_Is401()
        {
            Start();
            var code = Issue();
            now += TimeSpan.FromSeconds(60);

            var (status, json, _) = await Launch(code);

            Assert.AreEqual(HttpStatusCode.Unauthorized, status);
            Assert.AreEqual("bad_credentials", json.GetProperty("error").GetString());
        }

        [TestMethod]
        public async Task Launch_MissingOrEmptyCode_Is401BadCredentials()
        {
            Start();

            foreach (var body in new[] { "{}", "{\"code\":\"\"}", "not json" })
            {
                var (status, json, _) = await Post("/v1/session/launch", body);
                Assert.AreEqual(HttpStatusCode.Unauthorized, status, body);
                Assert.AreEqual("bad_credentials", json.GetProperty("error").GetString(), body);
            }
        }

        [TestMethod]
        public async Task Launch_Disabled_Is503LaunchDisabled_AndLeavesTheCode()
        {
            Start();
            var code = Issue();
            launchEnabled = false;

            var (status, json, _) = await Launch(code);

            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
            Assert.AreEqual("launch_disabled", json.GetProperty("error").GetString());
            Assert.AreEqual(1, store.Count, "a switched-off server must not burn the code");

            launchEnabled = true;
            Assert.AreEqual(HttpStatusCode.OK, (await Launch(code)).status);
        }

        [TestMethod]
        public async Task Launch_NullStore_Is503LaunchDisabled()
        {
            Start(nullStore: true);

            var (status, json, _) = await Launch("x");

            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
            Assert.AreEqual("launch_disabled", json.GetProperty("error").GetString());
        }

        [TestMethod]
        public async Task Launch_DisabledBeatsEmptyBody()
        {
            Start();
            launchEnabled = false;

            var (status, json, _) = await Post("/v1/session/launch", "{}");

            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
            Assert.AreEqual("launch_disabled", json.GetProperty("error").GetString());
        }

        [TestMethod]
        public async Task Launch_NullSwitch_Is503LaunchDisabled_AndLeavesTheCode()
        {
            Start(nullSwitch: true);
            var code = Issue();

            var (status, json, _) = await Launch(code);

            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
            Assert.AreEqual("launch_disabled", json.GetProperty("error").GetString());
            Assert.AreEqual(1, store.Count, "an unwired switch must not burn the code");
        }

        [TestMethod]
        public async Task Launch_OwnIpBucket_Is429()
        {
            Start(launchRate: 2);

            Assert.AreEqual(HttpStatusCode.Unauthorized, (await Launch("a")).status);
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await Launch("b")).status);

            var (status, json, _) = await Launch("c");
            Assert.AreEqual(HttpStatusCode.TooManyRequests, status);
            Assert.AreEqual("rate_limited", json.GetProperty("error").GetString());
        }

        [TestMethod]
        public async Task Launch_DoesNotSpendThePasswordLoginBucket()
        {
            Start(launchRate: 120, loginRate: 1);

            for (var i = 0; i < 3; i++)
                Assert.AreEqual(HttpStatusCode.OK, (await Launch(Issue())).status);

            var (status, _, _) = await Post("/v1/session", "{\"account_name\":\"tester\",\"password\":\"pw\"}");
            Assert.AreEqual(HttpStatusCode.OK, status);
        }

        [TestMethod]
        public async Task Launch_NeverLogsTheCode()
        {
            Start();
            using var cap = new MarketLogCapture();

            var good = Issue();
            Assert.AreEqual(HttpStatusCode.OK, (await Launch(good)).status);

            var bad = Issue();
            now += TimeSpan.FromSeconds(61);
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await Launch(bad)).status);
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await Launch(good)).status);

            var messages = cap.Events.Select(e => e.RenderedMessage ?? string.Empty).ToList();

            Assert.IsTrue(messages.Any(m => m.Contains("launch")), "control: the route logs something");
            Assert.IsFalse(messages.Any(m => m.Contains(good) || m.Contains(bad)), "a launch code must never reach a log line");
        }
    }
}
