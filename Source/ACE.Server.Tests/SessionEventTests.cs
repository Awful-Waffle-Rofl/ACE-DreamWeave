using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Common;
using ACE.Server.Managers.Analytics;
using ACE.Server.Network;

namespace ACE.Server.Tests
{
    /// <summary>
    /// IP integrity session capture (Docs/Monitoring/IP-INTEGRITY-DESIGN.md sections 3 and 6): the enqueue
    /// path, the writer's open/close/boot-cleanup bookkeeping, the ASN classifier, and the two hook call sites.
    ///
    /// A Player cannot be constructed in this harness (see the notes in MuleCommerceTests), so the call-site
    /// tests bind the hook to the exact method that must contain it by reading that method's body out of the
    /// source. They are source scans, not executions, and say so: what they prove is that the hook is in the
    /// named method and ordered after the named commit point, and that deleting it fails the test.
    /// </summary>
    [TestClass]
    public class SessionEventTests
    {
        // AnalyticsManager's queue and enabled flag are process statics and leak between test classes, so every
        // test starts from a known state and leaves analytics disabled for whoever runs next.
        [TestInitialize]
        public void Setup()
        {
            AnalyticsManager.SetEnabledForTests(true);
            AnalyticsManager.ResetSessionQueueForTests();
        }

        [TestCleanup]
        public void Teardown()
        {
            AnalyticsManager.SetEnabledForTests(false);
            AnalyticsManager.ResetSessionQueueForTests();
            AnalyticsManager.SetSessionWriterForTests(null);
            AnalyticsManager.SetMaintenanceSeamsForTests(null, null, 0);
        }

        // ---------------------------------------------------------------- enqueue path

        /// <summary>A Session with only the three properties the login hook reads. GetUninitializedObject skips the
        /// constructor (which needs a live ConnectionListener); the auto-property backing fields are set directly.</summary>
        private static Session FakeSession(uint accountId, string account, IPEndPoint endPoint)
        {
            var session = (Session)RuntimeHelpers.GetUninitializedObject(typeof(Session));

            void Set(string property, object value)
            {
                var field = typeof(Session).GetField($"<{property}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(field, $"Session.{property} is no longer an auto-property; update this fake");
                field.SetValue(session, value);
            }

            Set("AccountId", accountId);
            Set("Account", account);
            Set("EndPointC2S", endPoint);
            return session;
        }

        [TestMethod]
        public void LoginHook_CapturesAccountCharacterAndClientAddress()
        {
            var session = FakeSession(7, "acct", new IPEndPoint(IPAddress.Parse("203.0.113.9"), 9000));

            AnalyticsManager.RecordSessionLogin(session, 0x50000042, "Wren");

            var rows = AnalyticsManager.DrainSessionQueueForTests();
            Assert.AreEqual(1, rows.Count);
            var row = rows[0];
            Assert.AreEqual(SessionEventKind.Open, row.Kind);
            Assert.AreEqual(7u, row.AccountId);
            Assert.AreEqual("acct", row.AccountName);
            Assert.AreEqual(0x50000042u, row.CharacterId);
            Assert.AreEqual("Wren", row.CharacterName);
            CollectionAssert.AreEqual(new byte[] { 203, 0, 113, 9 }, row.Ip);
        }

        [TestMethod]
        public void LoginHook_StoresAnIpv4MappedAddressAsIpv4()
        {
            var session = FakeSession(1, "a", new IPEndPoint(IPAddress.Parse("203.0.113.9").MapToIPv6(), 9000));

            AnalyticsManager.RecordSessionLogin(session, 1, "c");

            CollectionAssert.AreEqual(new byte[] { 203, 0, 113, 9 }, AnalyticsManager.DrainSessionQueueForTests().Single().Ip);
        }

        [TestMethod]
        public void LoginHook_KeepsAnIpv6AddressAt16Bytes()
        {
            var session = FakeSession(1, "a", new IPEndPoint(IPAddress.Parse("2001:db8::1"), 9000));

            AnalyticsManager.RecordSessionLogin(session, 1, "c");

            Assert.AreEqual(16, AnalyticsManager.DrainSessionQueueForTests().Single().Ip.Length);
        }

        [TestMethod]
        public void LoginHook_WithNoEndpoint_RecordsNothing()
        {
            AnalyticsManager.RecordSessionLogin(FakeSession(1, "a", null), 1, "c");

            Assert.AreEqual(0, AnalyticsManager.DrainSessionQueueForTests().Count);
        }

        [TestMethod]
        public void Hooks_AreNoOpsWhileAnalyticsIsDisabled()
        {
            AnalyticsManager.SetEnabledForTests(false);

            AnalyticsManager.RecordSessionLogin(FakeSession(1, "a", new IPEndPoint(IPAddress.Loopback, 1)), 1, "c");
            AnalyticsManager.RecordSessionLogout(1);

            Assert.AreEqual(0, AnalyticsManager.DrainSessionQueueForTests().Count);
        }

        [TestMethod]
        public void LogoutHook_EnqueuesAClose()
        {
            AnalyticsManager.RecordSessionLogout(0x50000042);

            var row = AnalyticsManager.DrainSessionQueueForTests().Single();
            Assert.AreEqual(SessionEventKind.Close, row.Kind);
            Assert.AreEqual(0x50000042u, row.CharacterId);
        }

        [TestMethod]
        public void Names_AreClampedToTheColumnWidth()
        {
            var longName = new string('x', 200);

            AnalyticsManager.RecordSessionLogin(FakeSession(1, longName, new IPEndPoint(IPAddress.Loopback, 1)), 1, longName);

            var row = AnalyticsManager.DrainSessionQueueForTests().Single();
            Assert.AreEqual(64, row.AccountName.Length);
            Assert.AreEqual(64, row.CharacterName.Length);
        }

        [TestMethod]
        public void FullQueue_DropsAndCounts_InsteadOfGrowing()
        {
            for (var i = 0; i < 10_050; i++)
                AnalyticsManager.RecordSessionLogout((uint)i);

            Assert.AreEqual(10_000, AnalyticsManager.DrainSessionQueueForTests().Count);
            Assert.AreEqual(50, AnalyticsManager.SessionEventsDropped);
        }

        [TestMethod]
        public void Events_ArriveInEnqueueOrder_SoACloseFollowsItsOpen()
        {
            var session = FakeSession(1, "a", new IPEndPoint(IPAddress.Loopback, 1));

            AnalyticsManager.RecordSessionLogin(session, 5, "c");
            AnalyticsManager.RecordSessionLogout(5);

            var kinds = AnalyticsManager.DrainSessionQueueForTests().Select(r => r.Kind).ToArray();
            CollectionAssert.AreEqual(new[] { SessionEventKind.Open, SessionEventKind.Close }, kinds);
        }

        // ---------------------------------------------------------------- writer bookkeeping

        private sealed class FakeStore : ISessionStore
        {
            public readonly List<string> Calls = new List<string>();
            public readonly List<(SessionEventRow Row, AsnResult Asn)> Inserts = new List<(SessionEventRow, AsnResult)>();
            public readonly List<long> TouchedIds = new List<long>();
            public bool CleanupThrows;
            public bool InsertThrowsOnce;
            public bool CloseAtLastSeenThrows;
            private long next = 100;

            public void CloseAbandonedSessions()
            {
                Calls.Add("boot");
                if (CleanupThrows)
                    throw new InvalidOperationException("db down");
            }

            public long InsertOpen(SessionEventRow row, AsnResult asn)
            {
                if (InsertThrowsOnce)
                {
                    InsertThrowsOnce = false;
                    throw new InvalidOperationException("insert failed");
                }

                Inserts.Add((row, asn));
                Calls.Add($"open:{next}");
                return next++;
            }

            public void Close(long id, DateTime ts, string reason) => Calls.Add($"close:{id}@{ts:HH:mm:ss}:{reason}");

            public void CloseAtLastSeen(long id, string reason)
            {
                Calls.Add($"lastseen:{id}:{reason}");
                if (CloseAtLastSeenThrows)
                    throw new InvalidOperationException("close failed");
            }

            public void Touch(IReadOnlyCollection<long> ids, DateTime ts)
            {
                Calls.Add("touch");
                TouchedIds.AddRange(ids);
            }
        }

        private static SessionEventRow Open(uint character, string ip = "198.51.100.1")
            => new SessionEventRow
            {
                Kind = SessionEventKind.Open,
                Ts = new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc),
                CharacterId = character,
                AccountId = 1,
                AccountName = "a",
                CharacterName = "c",
                Ip = IPAddress.Parse(ip).GetAddressBytes()
            };

        private static SessionEventRow Close(uint character, int second = 30)
            => new SessionEventRow
            {
                Kind = SessionEventKind.Close,
                Ts = new DateTime(2026, 9, 30, 12, 0, second, DateTimeKind.Utc),
                CharacterId = character
            };

        [TestMethod]
        public void BootCleanup_RunsTheStoresCloseAbandonedStep()
        {
            var store = new FakeStore();

            new SessionEventWriter(store, null).StartUp();

            CollectionAssert.AreEqual(new[] { "boot" }, store.Calls);
        }

        [TestMethod]
        public void BootCleanup_FailureDoesNotEscapeTheWriter()
        {
            var store = new FakeStore { CleanupThrows = true };

            new SessionEventWriter(store, null).StartUp();   // must not throw

            Assert.AreEqual(1, store.Calls.Count);
        }

        [TestMethod]
        public void BootCleanupSql_ClosesOnlyOpenRowsAtTheirLastSeenTime()
        {
            var sql = File.ReadAllText(Path.Combine(RepoRoot(), "ACE.Server", "Managers", "Analytics", "AnalyticsDatabase.cs"));

            StringAssert.Contains(sql, "UPDATE `session_event` SET `logout_utc` = `last_seen_utc`, `close_reason` = 'boot' WHERE `logout_utc` IS NULL;");
        }

        [TestMethod]
        public void Open_InsertsWithTheClassifiersResultAndTracksTheRow()
        {
            var store = new FakeStore();
            using var classifier = new AsnClassifier("db", "list", _ => throw new FileNotFoundException());
            var writer = new SessionEventWriter(store, classifier);

            writer.Process(new[] { Open(5) });

            Assert.AreEqual(1, store.Inserts.Count);
            Assert.AreEqual(1, writer.OpenCount);
            Assert.IsNull(store.Inserts[0].Asn.Asn, "no readable database: the ASN columns are NULL, the row is still written");
        }

        [TestMethod]
        public void Close_ClosesTheMatchingOpenRowAtTheEventTime()
        {
            var store = new FakeStore();
            var writer = new SessionEventWriter(store, null);

            writer.Process(new[] { Open(5), Open(6), Close(5, 42) });

            CollectionAssert.AreEqual(new[] { "open:100", "open:101", "close:100@12:00:42:logout" }, store.Calls);
            Assert.AreEqual(1, writer.OpenCount);
        }

        [TestMethod]
        public void Close_WithNoOpenRow_IsANoOp()
        {
            var store = new FakeStore();

            new SessionEventWriter(store, null).Process(new[] { Close(9) });

            Assert.AreEqual(0, store.Calls.Count);
        }

        [TestMethod]
        public void Open_ForACharacterStillOpen_EndsTheStaleRowAtItsLastSeen()
        {
            var store = new FakeStore();
            var writer = new SessionEventWriter(store, null);

            writer.Process(new[] { Open(5), Open(5) });

            CollectionAssert.AreEqual(new[] { "open:100", "lastseen:100:relogin", "open:101" }, store.Calls);
            Assert.AreEqual(1, writer.OpenCount);
        }

        [TestMethod]
        public void Process_OneFailedInsertDoesNotDiscardTheRestOfTheBatch()
        {
            var store = new FakeStore { InsertThrowsOnce = true };
            var writer = new SessionEventWriter(store, null);

            writer.Process(new[] { Open(5), Open(6) });

            Assert.AreEqual(1, store.Inserts.Count);
            Assert.AreEqual(6u, store.Inserts[0].Row.CharacterId);
        }

        [TestMethod]
        public void Advance_TouchesOnlineSessions_AndClosesOnesWhoseCharacterLeft()
        {
            var store = new FakeStore();
            var writer = new SessionEventWriter(store, null);
            writer.Process(new[] { Open(5), Open(6) });
            store.Calls.Clear();

            writer.Advance(id => id == 5, DateTime.UtcNow);

            CollectionAssert.AreEqual(new[] { "lastseen:101:orphan", "touch" }, store.Calls);
            CollectionAssert.AreEqual(new long[] { 100 }, store.TouchedIds);
            Assert.AreEqual(1, writer.OpenCount);
        }

        [TestMethod]
        public void Advance_WithNothingOpen_TouchesNothing()
        {
            var store = new FakeStore();

            new SessionEventWriter(store, null).Advance(_ => true, DateTime.UtcNow);

            Assert.AreEqual(0, store.Calls.Count);
        }

        // ---------------------------------------------------------------- ASN classifier

        private sealed class FakeReader : IAsnReader
        {
            public readonly Dictionary<string, (uint Asn, string Org)> Map;
            public bool Throws;
            public bool Disposed;

            public FakeReader(Dictionary<string, (uint, string)> map) => Map = map;

            public bool TryLookup(IPAddress ip, out uint asn, out string org)
            {
                if (Throws)
                    throw new InvalidOperationException("corrupt");

                if (Map.TryGetValue(ip.ToString(), out var hit))
                {
                    asn = hit.Asn;
                    org = hit.Org;
                    return true;
                }

                asn = 0;
                org = null;
                return false;
            }

            public void Dispose() => Disposed = true;
        }

        private static string TempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "ace-asn-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static byte[] Ip(string s) => IPAddress.Parse(s).GetAddressBytes();

        private static Dictionary<string, (uint, string)> Db() => new Dictionary<string, (uint, string)>
        {
            ["198.51.100.1"] = (64500, "Example Hosting"),
            ["192.0.2.7"] = (64501, "Example Residential")
        };

        [TestMethod]
        public void Classifier_FilesPresent_ResolvesAsnOrgAndHosting()
        {
            var dir = TempDir();
            var dbPath = Path.Combine(dir, "asn.mmdb");
            var listPath = Path.Combine(dir, "hosting.txt");
            File.WriteAllText(dbPath, "x");
            File.WriteAllText(listPath, "# vpn\nAS64500  # trailing comment\n\n");

            using var classifier = new AsnClassifier(dbPath, listPath, _ => new FakeReader(Db()));

            var hosting = classifier.Classify(Ip("198.51.100.1"));
            Assert.AreEqual(64500u, hosting.Asn);
            Assert.AreEqual("Example Hosting", hosting.Org);
            Assert.AreEqual(true, hosting.IsHosting);

            var residential = classifier.Classify(Ip("192.0.2.7"));
            Assert.AreEqual(64501u, residential.Asn);
            Assert.AreEqual(false, residential.IsHosting, "ASN known and list loaded, not listed: 0, not NULL");
        }

        [TestMethod]
        public void Classifier_AddressNotInTheDatabase_IsAllNull()
        {
            var dir = TempDir();
            var dbPath = Path.Combine(dir, "asn.mmdb");
            var listPath = Path.Combine(dir, "hosting.txt");
            File.WriteAllText(dbPath, "x");
            File.WriteAllText(listPath, "64500");

            using var classifier = new AsnClassifier(dbPath, listPath, _ => new FakeReader(Db()));

            var result = classifier.Classify(Ip("10.0.0.1"));
            Assert.IsNull(result.Asn);
            Assert.IsNull(result.Org);
            Assert.IsNull(result.IsHosting);
        }

        [TestMethod]
        public void Classifier_DatabaseMissing_IsAllNullAndNeverThrows()
        {
            var dir = TempDir();
            var listPath = Path.Combine(dir, "hosting.txt");
            File.WriteAllText(listPath, "64500");

            using var classifier = new AsnClassifier(Path.Combine(dir, "absent.mmdb"), listPath, _ => throw new AssertFailedException("factory must not run for a missing file"));

            var result = classifier.Classify(Ip("198.51.100.1"));
            Assert.IsNull(result.Asn);
            Assert.IsNull(result.IsHosting, "a list without an ASN to look up is unknown, not 0");
        }

        [TestMethod]
        public void Classifier_ListMissing_KeepsTheAsnButLeavesHostingNull()
        {
            var dir = TempDir();
            var dbPath = Path.Combine(dir, "asn.mmdb");
            File.WriteAllText(dbPath, "x");

            using var classifier = new AsnClassifier(dbPath, Path.Combine(dir, "absent.txt"), _ => new FakeReader(Db()));

            var result = classifier.Classify(Ip("198.51.100.1"));
            Assert.AreEqual(64500u, result.Asn);
            Assert.IsNull(result.IsHosting, "no list installed must not read as 'nobody is on a VPN'");
        }

        [TestMethod]
        public void Classifier_NoPathsConfigured_IsAllNull()
        {
            using var classifier = new AsnClassifier("", null, _ => throw new AssertFailedException("factory must not run"));

            var result = classifier.Classify(Ip("198.51.100.1"));
            Assert.IsNull(result.Asn);
            Assert.IsNull(result.IsHosting);
        }

        [TestMethod]
        public void Classifier_ReloadsTheListWhenItsModifiedTimeChanges()
        {
            var dir = TempDir();
            var dbPath = Path.Combine(dir, "asn.mmdb");
            var listPath = Path.Combine(dir, "hosting.txt");
            File.WriteAllText(dbPath, "x");
            File.WriteAllText(listPath, "64999");
            File.SetLastWriteTimeUtc(listPath, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

            using var classifier = new AsnClassifier(dbPath, listPath, _ => new FakeReader(Db()));
            Assert.AreEqual(false, classifier.Classify(Ip("198.51.100.1")).IsHosting);

            File.WriteAllText(listPath, "64500");
            File.SetLastWriteTimeUtc(listPath, new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));

            Assert.AreEqual(true, classifier.Classify(Ip("198.51.100.1")).IsHosting);
        }

        [TestMethod]
        public void Classifier_ListEditedWithoutAnMtimeChange_IsNotReread()
        {
            var dir = TempDir();
            var dbPath = Path.Combine(dir, "asn.mmdb");
            var listPath = Path.Combine(dir, "hosting.txt");
            File.WriteAllText(dbPath, "x");
            File.WriteAllText(listPath, "64999");
            var stamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(listPath, stamp);

            using var classifier = new AsnClassifier(dbPath, listPath, _ => new FakeReader(Db()));
            Assert.AreEqual(false, classifier.Classify(Ip("198.51.100.1")).IsHosting);

            File.WriteAllText(listPath, "64500");
            File.SetLastWriteTimeUtc(listPath, stamp);

            Assert.AreEqual(false, classifier.Classify(Ip("198.51.100.1")).IsHosting, "the reload key is the modified time");
        }

        [TestMethod]
        public void Classifier_ReloadsTheDatabaseWhenItsModifiedTimeChanges_AndDisposesTheOldReader()
        {
            var dir = TempDir();
            var dbPath = Path.Combine(dir, "asn.mmdb");
            File.WriteAllText(dbPath, "x");
            File.SetLastWriteTimeUtc(dbPath, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

            var readers = new List<FakeReader>();
            using var classifier = new AsnClassifier(dbPath, null, _ =>
            {
                var r = new FakeReader(readers.Count == 0
                    ? Db()
                    : new Dictionary<string, (uint, string)> { ["198.51.100.1"] = (65000, "Moved") });
                readers.Add(r);
                return r;
            });

            Assert.AreEqual(64500u, classifier.Classify(Ip("198.51.100.1")).Asn);
            Assert.AreEqual(64500u, classifier.Classify(Ip("198.51.100.1")).Asn);
            Assert.AreEqual(1, readers.Count, "an unchanged file is opened once, not per login");

            File.SetLastWriteTimeUtc(dbPath, new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));

            Assert.AreEqual(65000u, classifier.Classify(Ip("198.51.100.1")).Asn);
            Assert.AreEqual(2, readers.Count);
            Assert.IsTrue(readers[0].Disposed, "the replaced reader must be released so the file can be replaced on disk");
        }

        [TestMethod]
        public void Classifier_UnreadableDatabase_IsAttemptedOncePerFileVersion_AndNeverThrows()
        {
            var dir = TempDir();
            var dbPath = Path.Combine(dir, "asn.mmdb");
            File.WriteAllText(dbPath, "x");

            var attempts = 0;
            using var classifier = new AsnClassifier(dbPath, null, _ =>
            {
                attempts++;
                throw new InvalidDataException("not an mmdb");
            });

            for (var i = 0; i < 5; i++)
                Assert.IsNull(classifier.Classify(Ip("198.51.100.1")).Asn);

            Assert.AreEqual(1, attempts, "one attempt (and so one warning) per file version, not one per login");
        }

        [TestMethod]
        public void Classifier_RealMmdbReaderOnAGarbageFile_DegradesToNull()
        {
            var dir = TempDir();
            var dbPath = Path.Combine(dir, "garbage.mmdb");
            File.WriteAllText(dbPath, "this is not a MaxMind database");

            using var classifier = new AsnClassifier(dbPath, null);   // default factory = the real MmdbAsnReader

            Assert.IsNull(classifier.Classify(Ip("198.51.100.1")).Asn);
        }

        [TestMethod]
        public void Classifier_ALookupThatThrows_YieldsNullInsteadOfEscaping()
        {
            var dir = TempDir();
            var dbPath = Path.Combine(dir, "asn.mmdb");
            File.WriteAllText(dbPath, "x");

            using var classifier = new AsnClassifier(dbPath, null, _ => new FakeReader(Db()) { Throws = true });

            Assert.IsNull(classifier.Classify(Ip("198.51.100.1")).Asn);
        }

        [TestMethod]
        public void ParseHostingList_HandlesCommentsBlanksPrefixesAndJunk()
        {
            var parsed = AsnClassifier.ParseHostingList(new[]
            {
                "# header",
                "",
                "   ",
                "13335",
                "AS16509   # Amazon",
                "as14061",
                "not-a-number",
                "0",
                "99999999999999"
            }, out var skipped);

            CollectionAssert.AreEquivalent(new uint[] { 13335, 16509, 14061 }, parsed.ToArray());
            Assert.AreEqual(3, skipped);
        }

        // ---------------------------------------------------------------- config and schema

        [TestMethod]
        public void Config_IpRetentionDefaultsTo30Days_AndPathsDefaultToNone()
        {
            var server = new GameConfiguration();

            Assert.AreEqual(30u, server.AnalyticsIpRetentionDays);
            Assert.AreEqual(string.Empty, server.AnalyticsAsnDatabasePath);
            Assert.AreEqual(string.Empty, server.AnalyticsHostingAsnListPath);
        }

        [TestMethod]
        public void ConfigExample_DocumentsEveryAnalyticsKey_WithValuesMatchingTheCodeDefaults()
        {
            var text = File.ReadAllText(Path.Combine(RepoRoot(), "ACE.Server", "Config.js.example"));
            var parsed = JsonSerializer.Deserialize<MasterConfiguration>(text, ConfigManager.SerializerOptions);
            var defaults = new GameConfiguration();

            Assert.IsNotNull(parsed);
            Assert.IsFalse(parsed.Server.EnableAnalytics, "the example must ship with analytics off");
            Assert.AreEqual(defaults.AnalyticsFlushIntervalSeconds, parsed.Server.AnalyticsFlushIntervalSeconds);
            Assert.AreEqual(defaults.AnalyticsRetentionDays, parsed.Server.AnalyticsRetentionDays);
            Assert.AreEqual(defaults.AnalyticsChatRetentionDays, parsed.Server.AnalyticsChatRetentionDays);
            Assert.AreEqual(defaults.AnalyticsIpRetentionDays, parsed.Server.AnalyticsIpRetentionDays);
            Assert.AreEqual(string.Empty, parsed.Server.AnalyticsAsnDatabasePath);
            Assert.AreEqual(string.Empty, parsed.Server.AnalyticsHostingAsnListPath);
            Assert.AreEqual("ace_analytics", parsed.MySql.Analytics.Database);

            // Parsing alone would pass for a key that is merely absent (it falls back to the default), so also
            // require that each key is literally present in the file.
            foreach (var key in new[] { "EnableAnalytics", "AnalyticsFlushIntervalSeconds", "AnalyticsRetentionDays",
                "AnalyticsChatRetentionDays", "AnalyticsIpRetentionDays", "AnalyticsAsnDatabasePath", "AnalyticsHostingAsnListPath" })
                StringAssert.Contains(text, $"\"{key}\":");
        }

        [TestMethod]
        public void Schema_SessionEventColumnsMatchTheDesign()
        {
            var sql = File.ReadAllText(Path.Combine(RepoRoot(), "ACE.Server", "Managers", "Analytics", "ace_analytics.sql"));
            var start = sql.IndexOf("CREATE TABLE IF NOT EXISTS `session_event`", StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, "session_event table is missing from ace_analytics.sql");
            var table = sql.Substring(start, sql.IndexOf("ENGINE=InnoDB", start, StringComparison.Ordinal) - start);

            foreach (var column in new[]
            {
                "`id`             BIGINT UNSIGNED NOT NULL AUTO_INCREMENT",
                "`account_id`     INT UNSIGNED    NOT NULL",
                "`account_name`   VARCHAR(64)     NOT NULL",
                "`character_id`   INT UNSIGNED    NOT NULL",
                "`character_name` VARCHAR(64)     NOT NULL",
                "`ip`             VARBINARY(16)   NOT NULL",
                "`asn`            INT UNSIGNED    NULL",
                "`asn_org`        VARCHAR(128)    NULL",
                "`is_hosting`     TINYINT(1)      NULL",
                "`login_utc`      DATETIME        NOT NULL",
                "`last_seen_utc`  DATETIME        NOT NULL",
                "`logout_utc`     DATETIME        NULL",
                "`close_reason`   VARCHAR(8)      NULL"
            })
                StringAssert.Contains(table, column);

            foreach (var index in new[]
            {
                "(`login_utc`)", "(`account_id`, `login_utc`)", "(`ip`, `login_utc`)", "(`character_id`, `login_utc`)"
            })
                StringAssert.Contains(table, index);
        }

        // ---------------------------------------------------------------- close_reason, failure isolation, shutdown

        [TestMethod]
        public void CloseReasons_AreExactlyTheFiveTheDashboardReads_AndFitTheColumn()
        {
            CollectionAssert.AreEqual(new[] { "logout", "orphan", "boot", "relogin", "shutdown" }, SessionCloseReason.All);

            foreach (var reason in SessionCloseReason.All)
                Assert.IsTrue(reason.Length <= 8, $"'{reason}' overflows close_reason VARCHAR(8)");
        }

        [TestMethod]
        public void Schema_CloseReasonSitsRightAfterLogoutUtc()
        {
            var sql = File.ReadAllText(Path.Combine(RepoRoot(), "ACE.Server", "Managers", "Analytics", "ace_analytics.sql"));
            var logout = sql.IndexOf("`logout_utc`     DATETIME        NULL,", StringComparison.Ordinal);
            var reason = sql.IndexOf("`close_reason`   VARCHAR(8)      NULL,", StringComparison.Ordinal);

            Assert.IsTrue(logout >= 0 && reason > logout);
            Assert.AreEqual("", sql.Substring(logout + "`logout_utc`     DATETIME        NULL,".Length, reason - logout - "`logout_utc`     DATETIME        NULL,".Length).Trim(),
                "close_reason must be the very next column after logout_utc");
        }

        [TestMethod]
        public void SessionWriter_UsesTheRightReasonForEachCloseKind()
        {
            var store = new FakeStore();
            var writer = new SessionEventWriter(store, null);

            writer.Process(new[] { Open(1), Close(1) });                 // logout
            writer.Process(new[] { Open(2), Open(2) });                  // relogin (then 2 stays open)
            writer.Process(new[] { Open(3) });
            writer.Advance(id => id == 2, DateTime.UtcNow);              // 3 is orphaned
            writer.CloseAll(DateTime.UtcNow);                            // 2 is closed by shutdown

            var all = string.Join("|", store.Calls);
            StringAssert.Contains(all, ":logout");
            StringAssert.Contains(all, ":relogin");
            StringAssert.Contains(all, ":orphan");
            StringAssert.Contains(all, ":shutdown");
            Assert.AreEqual(0, writer.OpenCount);
        }

        [TestMethod]
        public void Open_WhenClosingTheStaleRowThrows_StillInsertsTheNewSession()
        {
            var store = new FakeStore { CloseAtLastSeenThrows = true };
            var writer = new SessionEventWriter(store, null);

            writer.Process(new[] { Open(5), Open(5) });

            Assert.AreEqual(2, store.Inserts.Count, "the new login must get a row even when the stale close fails");
            Assert.AreEqual(1, writer.OpenCount);
        }

        [TestMethod]
        public void Maintenance_StillPrunesRawIps_WhenTheFlushThrows()
        {
            var pruned = new List<int>();
            AnalyticsManager.SetMaintenanceSeamsForTests(() => new HashSet<uint>(), days => pruned.Add(days), 30);

            AnalyticsManager.RunMaintenanceForTests(() => throw new InvalidOperationException("WriteFlush: database unreachable"));

            CollectionAssert.AreEqual(new[] { 30 }, pruned, "a failing flush must not stop the session_event prune");
        }

        [TestMethod]
        public void Maintenance_StillPrunesRawIps_WhenTheSessionAdvanceThrows()
        {
            var pruned = new List<int>();
            AnalyticsManager.SetMaintenanceSeamsForTests(() => throw new InvalidOperationException("roster unavailable"), days => pruned.Add(days), 30);

            AnalyticsManager.RunMaintenanceForTests(() => { });

            CollectionAssert.AreEqual(new[] { 30 }, pruned);
        }

        [TestMethod]
        public void Maintenance_AdvancesLastSeenForOnlineSessions_EvenWhenTheFlushThrows()
        {
            var store = new FakeStore();
            var writer = new SessionEventWriter(store, null);
            writer.Process(new[] { Open(5) });
            AnalyticsManager.SetSessionWriterForTests(writer);
            AnalyticsManager.SetMaintenanceSeamsForTests(() => new HashSet<uint> { 5 }, _ => { }, 30);

            AnalyticsManager.RunMaintenanceForTests(() => throw new InvalidOperationException("flush down"));

            CollectionAssert.AreEqual(new long[] { 100 }, store.TouchedIds);
        }

        [TestMethod]
        public void FinalSessionPass_AppliesQueuedLogoutsThenClosesTheRestAsShutdown()
        {
            var store = new FakeStore();
            AnalyticsManager.SetSessionWriterForTests(new SessionEventWriter(store, null));

            var ip = IPAddress.Parse("198.51.100.1");
            AnalyticsManager.EnqueueSessionOpen(1, "a", 5, "c5", ip, DateTime.UtcNow);
            AnalyticsManager.EnqueueSessionOpen(1, "a", 6, "c6", ip, DateTime.UtcNow);
            AnalyticsManager.EnqueueSessionClose(5, DateTime.UtcNow);

            AnalyticsManager.FinalSessionPass();

            Assert.AreEqual(4, store.Calls.Count, string.Join("|", store.Calls));
            Assert.AreEqual("open:100", store.Calls[0]);
            Assert.AreEqual("open:101", store.Calls[1]);
            StringAssert.StartsWith(store.Calls[2], "close:100@");
            StringAssert.EndsWith(store.Calls[2], ":logout");
            StringAssert.StartsWith(store.Calls[3], "close:101@");
            StringAssert.EndsWith(store.Calls[3], ":shutdown");
        }

        // ---------------------------------------------------------------- call sites (source scans)
        //
        // These read a method body out of the source and look for a hook statement in it. A bare IndexOf is not
        // enough, and a code review proved it: a COMMENTED-OUT call still matched. So the scan (1) blanks every
        // comment and string/char literal first, (2) requires the call to begin a statement line, (3) requires
        // the previous significant character to end a statement or open a block (rejecting a brace-less
        // `if (x)` guard) and (4) requires the exact brace depth (rejecting `if (false) { ... }` and lambdas).

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ACE.Server", "Managers", "WorldManager.cs")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find ACE.Server/Managers/WorldManager.cs by walking up from {AppContext.BaseDirectory}");

            return dir.FullName;
        }

        private static string Source(params string[] parts)
            => File.ReadAllText(Path.Combine(new[] { RepoRoot(), "ACE.Server" }.Concat(parts).ToArray()));

        /// <summary>
        /// Same length as the input, with every // and /* */ comment and the CONTENT of every string and char
        /// literal replaced by spaces (newlines kept), so braces and call text inside them cannot be matched.
        /// </summary>
        internal static string StripCommentsAndLiterals(string src)
        {
            var sb = new StringBuilder(src.Length);
            var i = 0;

            char At(int k) => k < src.Length ? src[k] : '\0';

            while (i < src.Length)
            {
                var c = src[i];

                if (c == '/' && At(i + 1) == '/')
                {
                    while (i < src.Length && src[i] != '\n') { sb.Append(' '); i++; }
                }
                else if (c == '/' && At(i + 1) == '*')
                {
                    sb.Append("  ");
                    i += 2;
                    while (i < src.Length && !(src[i] == '*' && At(i + 1) == '/'))
                    {
                        sb.Append(src[i] == '\n' ? '\n' : ' ');
                        i++;
                    }
                    if (i < src.Length) { sb.Append("  "); i += 2; }
                }
                else if (c == '"')
                {
                    var verbatim = (i >= 1 && src[i - 1] == '@') || (i >= 2 && src[i - 1] == '$' && src[i - 2] == '@');
                    sb.Append('"');
                    i++;
                    while (i < src.Length)
                    {
                        if (verbatim)
                        {
                            if (src[i] == '"' && At(i + 1) == '"') { sb.Append("  "); i += 2; continue; }
                            if (src[i] == '"') break;
                        }
                        else
                        {
                            if (src[i] == '\\') { sb.Append("  "); i += 2; continue; }
                            if (src[i] == '"') break;
                        }
                        sb.Append(src[i] == '\n' ? '\n' : ' ');
                        i++;
                    }
                    if (i < src.Length) { sb.Append('"'); i++; }
                }
                else if (c == '\'')
                {
                    sb.Append('\'');
                    i++;
                    while (i < src.Length && src[i] != '\'')
                    {
                        if (src[i] == '\\') { sb.Append("  "); i += 2; continue; }
                        sb.Append(' ');
                        i++;
                    }
                    if (i < src.Length) { sb.Append('\''); i++; }
                }
                else
                {
                    sb.Append(c);
                    i++;
                }
            }

            return sb.ToString();
        }

        /// <summary>The body (braces included) of the method whose declaration contains <paramref name="signature"/>, from already-stripped source.</summary>
        private static string BodyOf(string stripped, string signature)
        {
            var at = stripped.IndexOf(signature, StringComparison.Ordinal);
            Assert.IsTrue(at >= 0, $"method '{signature}' not found - did it move?");
            Assert.AreEqual(at, stripped.LastIndexOf(signature, StringComparison.Ordinal), $"'{signature}' is ambiguous");

            var open = stripped.IndexOf('{', at);
            var depth = 0;

            for (var i = open; i < stripped.Length; i++)
            {
                if (stripped[i] == '{') depth++;
                else if (stripped[i] == '}' && --depth == 0)
                    return stripped.Substring(open, i - open + 1);
            }

            Assert.Fail($"unbalanced braces in '{signature}'");
            return null;
        }

        private static int DepthAt(string body, int index)
        {
            var depth = 0;
            for (var i = 0; i < index; i++)
            {
                if (body[i] == '{') depth++;
                else if (body[i] == '}') depth--;
            }
            return depth;
        }

        /// <summary>
        /// Index of a line that is exactly <paramref name="statement"/> (a regex, matched from the start of a line),
        /// preceded by a statement end or block open, at brace depth <paramref name="depth"/> within the body
        /// (1 = the method's own top level). Fails with <paramref name="what"/> if no occurrence qualifies.
        /// </summary>
        private static int StatementAt(string body, string statement, int depth, string what)
        {
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(body, "(?m)^[ \\t]*(" + statement + ")"))
            {
                var at = m.Groups[1].Index;

                var k = at - 1;
                while (k >= 0 && char.IsWhiteSpace(body[k])) k--;

                if (k >= 0 && (body[k] == ';' || body[k] == '{' || body[k] == '}') && DepthAt(body, at) == depth)
                    return at;
            }

            Assert.Fail($"{what}: no live statement '{statement}' at brace depth {depth} (commented out, guarded, nested or missing?)");
            return -1;
        }

        [TestMethod]
        public void StripCommentsAndLiterals_BlanksCommentsAndLiteralContents_AndKeepsLength()
        {
            const string src = "a(); // x { y\n/* z { */ b(\"{ // \"); c('{'); d(@\"q\"\"{\");";
            var stripped = StripCommentsAndLiterals(src);

            Assert.AreEqual(src.Length, stripped.Length);
            Assert.IsFalse(stripped.Contains("{"), stripped);
            StringAssert.Contains(stripped, "a();");
            StringAssert.Contains(stripped, "b(");
            StringAssert.Contains(stripped, "d(");
        }

        [TestMethod]
        public void StatementAt_RejectsCommentedGuardedNestedAndBracelessCalls()
        {
            string Body(string inner) => BodyOf(StripCommentsAndLiterals("void M() {\n" + inner + "\n}"), "void M()");
            const string call = "Hook\\(\\);";

            Assert.IsTrue(StatementAt(Body("    Hook();"), call, 1, "live") >= 0);

            foreach (var bad in new[]
            {
                "    // Hook();",
                "    /* Hook(); */",
                "    if (false) { Hook(); }",
                "    if (false)\n        Hook();",
                "    Run(() => {\n        Hook();\n    });"
            })
            {
                var rejected = false;
                try { StatementAt(Body(bad), call, 1, "x"); }
                catch (AssertFailedException) { rejected = true; }
                Assert.IsTrue(rejected, $"should have been rejected: {bad}");
            }
        }

        [TestMethod]
        public void LoginCallSite_IsInDoPlayerEnterWorldInner_AfterTheLoginIsCommitted()
        {
            var body = BodyOf(StripCommentsAndLiterals(Source("Managers", "WorldManager.cs")), "private static void DoPlayerEnterWorld_Inner(");

            var hookAt = StatementAt(body, "(?:Analytics\\.)?AnalyticsManager\\.RecordSessionLogin\\(session, character\\.Id, character\\.Name\\);", 1, "login hook");

            // Commit points, in the order they happen: online, placed in the world, THEN recorded.
            var online = StatementAt(body, "session\\.Player\\.PlayerEnterWorld\\(\\);", 1, "online anchor");
            var placed = StatementAt(body, "var success = LandblockManager\\.AddObject\\(session\\.Player, true\\);", 1, "placed anchor");
            Assert.IsTrue(hookAt > online, "the login must be recorded only after the player is online");
            Assert.IsTrue(hookAt > placed, "the login must be recorded only after the player is placed on a landblock");

            Assert.AreEqual(1, System.Text.RegularExpressions.Regex.Matches(body, "RecordSessionLogin\\(").Count, "exactly one login hook");
        }

        [TestMethod]
        public void LogoutCallSite_IsInFinalizeLogout_AfterTheOnlineToOfflineSwitch()
        {
            var player = StripCommentsAndLiterals(Source("WorldObjects", "Player.cs"));
            var body = BodyOf(player, "private void FinalizeLogout()");

            var hookAt = StatementAt(body, "AnalyticsManager\\.RecordSessionLogout\\(Guid\\.Full\\);", 1, "logout hook");
            var switched = StatementAt(body, "PlayerManager\\.SwitchPlayerFromOnlineToOffline\\(this\\);", 1, "offline-switch anchor");
            Assert.IsTrue(hookAt > switched, "a logout must be recorded only after the player is offline");

            // The reason the hook is here and not in LogOut_Final: ForceLogoff reaches FinalizeLogout without it.
            StatementAt(BodyOf(player, "public void ForceLogoff()"), "FinalizeLogout\\(\\);", 1, "ForceLogoff -> FinalizeLogout");
            Assert.IsFalse(BodyOf(player, "private void LogOut_Final(").Contains("RecordSessionLogout"),
                "hooking LogOut_Final as well would double-record every animated logout");
            Assert.AreEqual(1, System.Text.RegularExpressions.Regex.Matches(player, "RecordSessionLogout\\(").Count, "exactly one logout hook in Player.cs");
        }

        [TestMethod]
        public void WriterWiring_BootCleanupFirst_DrainEachTick_MaintenanceAfterFlush_FinalPassAfterTheLoop()
        {
            var manager = StripCommentsAndLiterals(Source("Managers", "Analytics", "AnalyticsManager.cs"));

            var loop = BodyOf(manager, "private static void WorkerLoop()");
            var start = StatementAt(loop, "sessionWriter\\?\\.StartUp\\(\\);", 1, "boot cleanup");
            var whileAt = loop.IndexOf("while (running)", StringComparison.Ordinal);
            Assert.IsTrue(whileAt > start, "boot cleanup must run on the writer thread before the drain loop");
            var sleepAt = StatementAt(loop, "Thread\\.Sleep\\(1000\\);", 2, "loop sleep");
            var exitAt = StatementAt(loop, "if \\(!running\\)\\s*break;", 2, "shutdown check after the sleep");
            Assert.IsTrue(exitAt > sleepAt, "the shutdown check must follow the sleep");
            Assert.IsTrue(exitAt < loop.IndexOf("DrainSessions();", StringComparison.Ordinal), "the shutdown check must precede all interval work");
            Assert.IsTrue(exitAt < loop.IndexOf("RunInterval(", StringComparison.Ordinal), "a shutdown must skip the flush and go straight to the final pass");
            StatementAt(loop, "DrainSessions\\(\\);", 2, "per-tick session drain");
            StatementAt(loop, "RunInterval\\(Flush, Maintenance\\);", 2, "flush + maintenance");
            var finalAt = StatementAt(loop, "FinalSessionPass\\(\\);", 1, "final session pass");
            Assert.IsTrue(finalAt > whileAt, "the final pass must follow the loop");

            var maintenance = BodyOf(manager, "private static void Maintenance(DateTime now)");
            var prune = StatementAt(maintenance, "\\(pruneSessionsOverrideForTests \\?\\? AnalyticsDatabase\\.PruneSessions\\)\\(ipRetentionDays\\);", 2, "session prune");
            StatementAt(maintenance, "sessionWriter\\?\\.Advance\\(onlineIds\\.Contains, now\\);", 2, "session advance");
            Assert.IsTrue(prune < maintenance.IndexOf("AnalyticsDatabase.PruneRates(", StringComparison.Ordinal), "the raw-IP prune must come first");

            // Regression: neither may live in Flush again, where a throwing WriteFlush skips them.
            var flush = BodyOf(manager, "private static void Flush()");
            Assert.IsFalse(flush.Contains("PruneSessions"), "PruneSessions must not be in Flush");
            Assert.IsFalse(flush.Contains("Advance("), "the session advance must not be in Flush");

            StatementAt(BodyOf(manager, "public static void Initialize()"),
                "ipRetentionDays = \\(int\\)Math\\.Max\\(1, config\\.AnalyticsIpRetentionDays\\);", 1, "ip retention config");
        }

        [TestMethod]
        public void ShutdownWiring_AnalyticsStopsFromTheSharedDrainStep_AndTheWorkerJoinsBounded()
        {
            var server = StripCommentsAndLiterals(Source("Managers", "ServerManager.cs"));
            StatementAt(BodyOf(server, "private static void DrainShardQueue("),
                "ACE\\.Server\\.Managers\\.Analytics\\.AnalyticsManager\\.Shutdown\\(\\);", 2, "analytics stop in the shared drain step");

            var manager = StripCommentsAndLiterals(Source("Managers", "Analytics", "AnalyticsManager.cs"));
            var shutdown = BodyOf(manager, "public static void Shutdown()");
            StatementAt(shutdown, "running = false;", 1, "run flag cleared");
            StatementAt(shutdown, "thread\\.Join\\(TimeSpan\\.FromSeconds\\(5\\)\\);", 2, "bounded join");
        }

        [TestMethod]
        public void CommittedHostingAsnList_ParsesCleanly_AndExcludesResidentialGoogleFiber()
        {
            // RepoRoot() is the Source directory (parent of ACE.Server); the seed list lives under deploy/ beside it.
            var path = Path.Combine(RepoRoot(), "..", "deploy", "analytics", "hosting-asns.txt");
            Assert.IsTrue(File.Exists(path), $"committed seed list not found at {path}");

            var parsed = AsnClassifier.ParseHostingList(File.ReadAllLines(path), out var skipped);

            Assert.IsTrue(parsed.Count >= 100, $"the committed hosting ASN list parsed to only {parsed.Count} ASNs (expected >= 100)");
            Assert.AreEqual(0, skipped, "the committed hosting ASN list has lines the server cannot parse");

            // Precision rule: a false positive marks a real player's ISP as a VPN. Well-known residential,
            // mobile and transit carriers must never be listed (7922 Comcast, 7018 AT&T, 701 Verizon,
            // 21928 T-Mobile, 20115 Charter, 22773 Cox, 174 Cogent, 3356 Lumen, 16591 Google Fiber).
            foreach (var denied in new[] { 7922u, 7018u, 701u, 21928u, 20115u, 22773u, 174u, 3356u, 16591u })
                Assert.IsFalse(parsed.Contains(denied), $"AS{denied} is a residential/mobile/transit carrier and must not be in the hosting list (precision over recall)");

            // Positive control: the parser really read the file's ASN lines (AWS and Hetzner are seeded).
            Assert.IsTrue(parsed.Contains(16509u), "AS16509 (Amazon) expected in the seed list");
            Assert.IsTrue(parsed.Contains(24940u), "AS24940 (Hetzner) expected in the seed list");
        }
    }
}