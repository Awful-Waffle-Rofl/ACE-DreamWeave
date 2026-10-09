using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

using ACE.Database.Models.Auth;
using ACE.Server.Managers.Market;
using ACE.Server.WorldEvents;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The five /v1/admin/world-events routes over a real loopback Kestrel with a FAKE
    /// IWorldEventAdminService: every refusal code renders with its published status and its extra field
    /// (run_id or reason), the admin gate runs before the service is ever called, and a body is parsed
    /// into the DTO the service receives. The game-side rules are WorldEventAdminServiceTests'.
    /// </summary>
    [TestClass]
    public class WorldEventAdminRouteTests
    {
        private const string Key = "test-shared-key";
        private const uint AccountId = 9301;

        private HttpClient client;
        private FakeService service;
        private uint accessLevel;
        private bool adminWebEnabled;

        private sealed class FakeService : IWorldEventAdminService
        {
            public WorldEventAdminOutcome Next = WorldEventAdminOutcome.Ok(new { ok = true });
            public int Calls;
            public WorldEventStartBody LastStart;
            public WorldEventStopBody LastStop;
            public string LastAccount;
            public bool StartBodyWasNull;

            public WorldEventAdminOutcome Catalog() { Calls++; return Next; }
            public WorldEventAdminOutcome Status() { Calls++; return Next; }

            public WorldEventAdminOutcome Preview(WorldEventStartBody body)
            {
                Calls++;
                LastStart = body;
                StartBodyWasNull = body == null;
                return Next;
            }

            public WorldEventAdminOutcome Start(WorldEventStartBody body, string accountName)
            {
                Calls++;
                LastStart = body;
                StartBodyWasNull = body == null;
                LastAccount = accountName;
                return Next;
            }

            public WorldEventAdminOutcome Stop(WorldEventStopBody body, string accountName)
            {
                Calls++;
                LastStop = body;
                LastAccount = accountName;
                return Next;
            }
        }

        [TestInitialize]
        public void Setup()
        {
            service = new FakeService();
            accessLevel = 5;
            adminWebEnabled = true;
            StartHost(service);
        }

        private void StartHost(IWorldEventAdminService worldEvents)
        {
            MarketApiHost.Stop();
            MarketApiHost.Start(new MarketApiOptions
            {
                ListenUrl = "http://127.0.0.1:0",
                SharedKey = Key,
                MaxInFlight = 8,
                WriteRatePerMinute = 500,
                RequestTimeoutMs = 3000,
                LoginRatePerMinutePerAccount = 50,
                LoginRatePerMinutePerIp = 500,
                AuthenticateAccount = (name, password) => name == "weft" && password == "pw" ? AccountId : 0u,
                ListCharacters = _ => new List<(uint, string)>(),
                GetAccountById = id => new Account { AccountId = AccountId, AccountName = "weft", AccessLevel = accessLevel },
                AdminWebEnabled = () => adminWebEnabled,
                WorldEvents = worldEvents,
            });

            client?.Dispose();
            client = new HttpClient { BaseAddress = new Uri(MarketApiHost.ListeningUrl) };
        }

        [TestCleanup]
        public void Teardown()
        {
            client?.Dispose();
            MarketApiHost.Stop();
        }

        private async Task<string> Login()
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/session");
            request.Headers.Add("X-Market-Key", Key);
            request.Content = new StringContent("{\"account_name\":\"weft\",\"password\":\"pw\"}", Encoding.UTF8, "application/json");

            using var response = await client.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return doc.RootElement.GetProperty("session_token").GetString();
        }

        private async Task<(HttpStatusCode status, JsonElement json)> Send(HttpMethod method, string path, string body = null)
        {
            var token = await Login();

            using var request = new HttpRequestMessage(method, path);
            request.Headers.Add("X-Market-Key", Key);
            request.Headers.Add("Authorization", $"Bearer {token}");

            if (body != null)
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");

            using var response = await client.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();

            if (string.IsNullOrWhiteSpace(text))
                return (response.StatusCode, default);

            using var doc = JsonDocument.Parse(text);
            return (response.StatusCode, doc.RootElement.Clone());
        }

        private const string StartBody =
            "{\"random\":false,\"source_id\":\"element_portal_fire\",\"family_ids\":[\"emberwrought\",\"frostbound\"]," +
            "\"goal_id\":\"kill_count\",\"boss_id\":\"auto\",\"reward_id\":\"standard\",\"teaser_seconds\":60,\"min_duration_seconds\":300," +
            "\"location\":{\"label\":\"Zaikhal\",\"realm_id\":0,\"cell\":2156920844,\"x\":36.86,\"y\":79.69,\"z\":124.0,\"qw\":1,\"qx\":0,\"qy\":0,\"qz\":0}}";

        public static IEnumerable<object[]> Refusals => new[]
        {
            new object[] { MarketError.WorldEventsDisabled, 503, "world_events_disabled", null, null },
            new object[] { MarketError.WorldEventRunning, 409, "world_event_running", 7u, null },
            new object[] { MarketError.WorldEventNotRunning, 409, "world_event_not_running", null, null },
            new object[] { MarketError.WorldEventRunChanged, 409, "world_event_run_changed", 8u, null },
            new object[] { MarketError.InvalidLocation, 400, "invalid_location", null, "location.x must be a finite number" },
            new object[] { MarketError.SourceNotWebStartable, 400, "source_not_web_startable", null, null },
            new object[] { MarketError.WorldEventRefused, 400, "world_event_refused", null, "unknown family 'x' (known: a)" },
            new object[] { MarketError.Timeout, 503, "timeout", null, null },
        };

        [TestMethod]
        [DynamicData(nameof(Refusals))]
        public async Task EveryRefusal_RendersItsCodeStatusAndExtra(MarketError error, int status, string code, uint? runId, string reason)
        {
            service.Next = WorldEventAdminOutcome.Fail(error, runId, reason);

            foreach (var (method, path, body) in new[]
            {
                (HttpMethod.Post, "/v1/admin/world-events/start", StartBody),
                (HttpMethod.Post, "/v1/admin/world-events/preview", StartBody),
                (HttpMethod.Post, "/v1/admin/world-events/stop", "{\"mode\":\"abort\",\"expected_run_id\":7}"),
                (HttpMethod.Get, "/v1/admin/world-events/status", null),
                (HttpMethod.Get, "/v1/admin/world-events/catalog", null),
            })
            {
                var (actual, json) = await Send(method, path, body);

                Assert.AreEqual(status, (int)actual, $"{path}: status");
                Assert.AreEqual(code, json.GetProperty("error").GetString(), $"{path}: code");
                Assert.IsFalse(string.IsNullOrEmpty(json.GetProperty("message").GetString()), $"{path}: message");

                if (runId == null)
                    Assert.IsFalse(json.TryGetProperty("run_id", out _), $"{path}: run_id must be absent for {code}");
                else
                    Assert.AreEqual(runId.Value, json.GetProperty("run_id").GetUInt32(), $"{path}: run_id");

                if (reason == null)
                    Assert.IsFalse(json.TryGetProperty("reason", out _), $"{path}: reason must be absent for {code}");
                else
                    Assert.AreEqual(reason, json.GetProperty("reason").GetString(), $"{path}: reason");
            }
        }

        [TestMethod]
        public async Task Success_RendersTheServiceBody()
        {
            service.Next = WorldEventAdminOutcome.Ok(new { run_id = 12u, state = "idle" });

            var (status, json) = await Send(HttpMethod.Post, "/v1/admin/world-events/start", StartBody);

            Assert.AreEqual(HttpStatusCode.OK, status);
            Assert.AreEqual(12u, json.GetProperty("run_id").GetUInt32());
            Assert.AreEqual("idle", json.GetProperty("state").GetString());
        }

        [TestMethod]
        public async Task StartBody_IsParsedSnakeCase_AndTheAccountNameIsPassed()
        {
            await Send(HttpMethod.Post, "/v1/admin/world-events/start", StartBody);

            var body = service.LastStart;
            Assert.IsNotNull(body);
            Assert.AreEqual("weft", service.LastAccount);
            Assert.AreEqual(false, body.Random);
            Assert.AreEqual("element_portal_fire", body.SourceId);
            CollectionAssert.AreEqual(new[] { "emberwrought", "frostbound" }, body.FamilyIds);
            Assert.AreEqual("auto", body.BossId);
            Assert.AreEqual(60, body.TeaserSeconds);
            Assert.AreEqual(300, body.MinDurationSeconds);
            Assert.AreEqual(0x8090000CL, body.Location.Cell);
            Assert.AreEqual(36.86, body.Location.X);
            Assert.AreEqual("Zaikhal", body.Location.Label);
        }

        [TestMethod]
        public async Task StopBody_IsParsed()
        {
            await Send(HttpMethod.Post, "/v1/admin/world-events/stop", "{\"mode\":\"fail\",\"expected_run_id\":41}");

            Assert.AreEqual("fail", service.LastStop.Mode);
            Assert.AreEqual(41L, service.LastStop.ExpectedRunId);
        }

        [TestMethod]
        public async Task MalformedBody_ReachesTheServiceAsNull()
        {
            await Send(HttpMethod.Post, "/v1/admin/world-events/start", "{not json");
            Assert.IsTrue(service.StartBodyWasNull);

            await Send(HttpMethod.Post, "/v1/admin/world-events/start", StartBody);
            Assert.IsFalse(service.StartBodyWasNull, "control: a well-formed body arrives parsed");
        }

        [TestMethod]
        public async Task NonAdmin_IsRefusedBeforeTheServiceIsCalled()
        {
            accessLevel = 4;

            var (status, json) = await Send(HttpMethod.Post, "/v1/admin/world-events/start", StartBody);

            Assert.AreEqual(HttpStatusCode.Forbidden, status);
            Assert.AreEqual("not_admin", json.GetProperty("error").GetString());
            Assert.AreEqual(0, service.Calls);

            accessLevel = 5;
            await Send(HttpMethod.Post, "/v1/admin/world-events/start", StartBody);
            Assert.AreEqual(1, service.Calls, "control: the same request as an Admin reaches the service");
        }

        [TestMethod]
        public async Task AdminWebDisabled_IsRefusedBeforeTheServiceIsCalled()
        {
            adminWebEnabled = false;

            var (status, json) = await Send(HttpMethod.Get, "/v1/admin/world-events/status");

            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, status);
            Assert.AreEqual("admin_disabled", json.GetProperty("error").GetString());
            Assert.AreEqual(0, service.Calls);
        }

        [TestMethod]
        public async Task NoServiceWired_IsServerError()
        {
            StartHost(null);

            var (status, json) = await Send(HttpMethod.Get, "/v1/admin/world-events/catalog");

            Assert.AreEqual(HttpStatusCode.InternalServerError, status);
            Assert.AreEqual("server_error", json.GetProperty("error").GetString());
        }
    }
}
