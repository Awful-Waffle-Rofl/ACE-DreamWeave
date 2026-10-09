using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database;

namespace ACE.Server.Tests
{
    /// <summary>
    /// CAP audit ledger DAO shape (round 2). Modelled on AccountVaultDaoShapeTests: a source-text test
    /// that pins the method surface round 3's admin commands code against, and forbids the persistence
    /// traps this layer must never reintroduce.
    ///
    /// ACE.Server.Tests deliberately has no database, so what a live test would prove (that the four
    /// methods round-trip against real MySQL) stays a manual step. What IS cheap to prove here is that
    /// nobody has reached for a user-initiated EF transaction, that the read clamp still exists, and
    /// that the audit upsert still preserves its first-seen timestamp - three things that fail silently
    /// in production and are invisible in a diff review.
    /// </summary>
    [TestClass]
    public class CapLedgerDaoShapeTests
    {
        private static string DaoSourcePath()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Source", "ACE.Database", "ShardDatabase_CapLedger.cs")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find ShardDatabase_CapLedger.cs by walking up from {AppContext.BaseDirectory}");

            return Path.Combine(dir.FullName, "Source", "ACE.Database", "ShardDatabase_CapLedger.cs");
        }

        /// <summary>
        /// Line endings normalised to \n, same reason as AccountVaultDaoShapeTests: most of the working
        /// tree is CRLF while the index is LF, so a raw match could pass or fail by checkout setting.
        /// </summary>
        private static string DaoSource() => File.ReadAllText(DaoSourcePath()).Replace("\r\n", "\n");

        private static readonly string[] ExpectedSignatures =
        {
            "public bool AddCapLedgerRow(CharacterCapLedger row)",
            "public bool UpsertCapAudit(CharacterCapAudit row)",
            "public List<CharacterCapLedger> GetCapLedger(uint characterId, int limit)",
            "public CharacterCapAudit GetCapAudit(uint characterId, out bool found)",
            "public List<CharacterCapAudit> GetCapAuditFailures(int limit)",
        };

        [TestMethod]
        public void Dao_DeclaresAllFourMethodSignatures()
        {
            var src = DaoSource();

            foreach (var signature in ExpectedSignatures)
                StringAssert.Contains(src, signature, $"ShardDatabase_CapLedger.cs no longer declares '{signature}'");
        }

        [TestMethod]
        public void ShardDatabase_ExposesEveryCapLedgerMethod()
        {
            var expected = new[] { "AddCapLedgerRow", "UpsertCapAudit", "GetCapLedger", "GetCapAudit", "GetCapAuditFailures" };

            foreach (var name in expected)
                Assert.IsNotNull(typeof(ShardDatabase).GetMethod(name, BindingFlags.Public | BindingFlags.Instance),
                    $"ShardDatabase.{name} is missing");
        }

        /// <summary>
        /// The queue wrappers exist and take a callback, which is what makes a CAP ledger write
        /// fire-and-forget off the world thread instead of a MySQL round trip inside a landblock tick.
        /// </summary>
        [TestMethod]
        public void SerializedShardDatabase_ExposesEveryCapLedgerWrapper()
        {
            var expected = new[] { "AddCapLedgerRow", "UpsertCapAudit", "GetCapLedger", "GetCapAudit", "GetCapAuditFailures" };

            foreach (var name in expected)
                Assert.IsNotNull(typeof(SerializedShardDatabase).GetMethod(name, BindingFlags.Public | BindingFlags.Instance),
                    $"SerializedShardDatabase.{name} is missing - the ledger write is not queued");
        }

        /// <summary>
        /// GetCapAudit's three-outcome contract (round 3, closing the /caaudit single-row gap):
        /// found's return type must be bool, not carried by the row's nullability alone, so "no row"
        /// and "read failed" cannot collapse onto the same signal.
        /// </summary>
        [TestMethod]
        public void GetCapAudit_FoundParameterIsAnOutBool()
        {
            var method = typeof(ShardDatabase).GetMethod("GetCapAudit", BindingFlags.Public | BindingFlags.Instance);
            Assert.IsNotNull(method, "ShardDatabase.GetCapAudit is missing");

            var parameters = method.GetParameters();
            Assert.AreEqual(2, parameters.Length, "GetCapAudit must take exactly (uint characterId, out bool found)");
            Assert.AreEqual(typeof(bool), parameters[1].ParameterType.GetElementType(), "the second parameter must be an out bool");
            Assert.IsTrue(parameters[1].IsOut, "the second parameter must be declared out, not just a bool return field");
        }

        [TestMethod]
        public void Dao_NeverOpensAUserInitiatedTransaction()
        {
            // All three DbContexts enable EnableRetryOnFailure, and MySqlRetryingExecutionStrategy
            // refuses a user-initiated transaction outright: "The configured execution strategy
            // 'MySqlRetryingExecutionStrategy' does not support user-initiated transactions." Anything
            // spanning statements must go through CreateExecutionStrategy().Execute with an idempotent
            // delegate instead. Nothing in this DAO needs to.
            var src = DaoSource();

            Assert.IsFalse(src.Contains("BeginTransaction"),
                "BeginTransaction throws under the retrying execution strategy ShardDbContext enables");
        }

        [TestMethod]
        public void ReadClamp_ConstantStillExists()
        {
            var src = DaoSource();

            StringAssert.Contains(src, "MaxCapLedgerRows",
                "the read clamp constant is gone - an admin passing int.MaxValue would materialise a whole table");

            Assert.IsNotNull(typeof(ShardDatabase).GetField("MaxCapLedgerRows", BindingFlags.Public | BindingFlags.Static),
                "ShardDatabase.MaxCapLedgerRows is missing");
        }

        /// <summary>
        /// The audit upsert must never overwrite first_Detected_At. It is the only record of HOW LONG a
        /// character has been out of balance, and the sweep upserts the same row repeatedly, so a plain
        /// VALUES() on that column would reset it to "just now" on every pass and erase the answer.
        /// </summary>
        [TestMethod]
        public void AuditUpsert_PreservesTheFirstSeenTimestamp()
        {
            var src = DaoSource();

            StringAssert.Contains(src, "ON DUPLICATE KEY UPDATE",
                "the audit summary must be a single upsert, not a read-modify-write");

            StringAssert.Contains(src, "COALESCE(`first_Detected_At`, VALUES(`first_Detected_At`))",
                "first_Detected_At must be coalesced, never overwritten");
        }

        /// <summary>
        /// Pure string-shape check, no database: every named @placeholder in UpsertCapAuditSql has
        /// exactly one corresponding `new MySqlParameter("@name"` binding inside UpsertCapAudit's
        /// method body, both sets are exactly 11 wide, and neither side carries a duplicate name.
        /// This is what catches a dropped, duplicated or reordered-into-mismatch parameter as a
        /// source-text fact, independent of CapLedgerAuditTests (ACE.Database.Tests), which proves
        /// the same binding behaviorally but only runs with ACE_DISPOSABLE_SHARD_DB=1 and therefore
        /// never runs in CI.
        ///
        /// The read is anchored to the UpsertCapAuditSql constant (via its declaration through the
        /// next ';') and to the UpsertCapAudit method body (via its signature through the next
        /// method's signature, GetCapLedger) specifically - not the file as a whole - so an unrelated
        /// future SQL constant or method elsewhere in this file cannot vacuously satisfy it.
        /// </summary>
        [TestMethod]
        public void AuditUpsertSql_EveryNamedPlaceholderIsBoundExactlyOnce_AndBothSetsAreEleven()
        {
            var src = DaoSource();

            var sqlStart = src.IndexOf("private const string UpsertCapAuditSql =", StringComparison.Ordinal);
            Assert.IsTrue(sqlStart >= 0, "UpsertCapAuditSql constant declaration not found");

            var sqlEnd = src.IndexOf(";", sqlStart, StringComparison.Ordinal);
            Assert.IsTrue(sqlEnd > sqlStart, "UpsertCapAuditSql constant has no terminating ';'");

            var sqlText = src.Substring(sqlStart, sqlEnd - sqlStart);

            var methodStart = src.IndexOf("public bool UpsertCapAudit(CharacterCapAudit row)", StringComparison.Ordinal);
            Assert.IsTrue(methodStart >= 0, "UpsertCapAudit method signature not found");

            var methodEnd = src.IndexOf("public List<CharacterCapLedger> GetCapLedger", methodStart, StringComparison.Ordinal);
            Assert.IsTrue(methodEnd > methodStart, "could not bound the end of UpsertCapAudit's method body against the next method, GetCapLedger");

            var methodText = src.Substring(methodStart, methodEnd - methodStart);

            var placeholderNames = Regex.Matches(sqlText, "@[A-Za-z][A-Za-z0-9]*")
                .Select(m => m.Value)
                .ToList();

            var parameterNames = Regex.Matches(methodText, "new MySqlParameter\\(\"(@[A-Za-z][A-Za-z0-9]*)\"")
                .Select(m => m.Groups[1].Value)
                .ToList();

            Assert.AreEqual(11, placeholderNames.Count,
                $"UpsertCapAuditSql must reference exactly 11 @placeholders, found {placeholderNames.Count}: {string.Join(", ", placeholderNames)}");
            Assert.AreEqual(11, placeholderNames.Distinct().Count(),
                $"UpsertCapAuditSql's @placeholders must all be distinct, found: {string.Join(", ", placeholderNames)}");

            Assert.AreEqual(11, parameterNames.Count,
                $"UpsertCapAudit must bind exactly 11 MySqlParameters, found {parameterNames.Count}: {string.Join(", ", parameterNames)}");
            Assert.AreEqual(11, parameterNames.Distinct().Count(),
                $"UpsertCapAudit must not bind the same parameter name twice, found: {string.Join(", ", parameterNames)}");

            CollectionAssert.AreEquivalent(placeholderNames.Distinct().ToList(), parameterNames.Distinct().ToList(),
                "every @placeholder in UpsertCapAuditSql must have exactly one corresponding MySqlParameter binding in UpsertCapAudit, and vice versa");
        }
    }
}
