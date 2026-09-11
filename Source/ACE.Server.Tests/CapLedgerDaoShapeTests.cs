using System;
using System.IO;
using System.Reflection;

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
    }
}
