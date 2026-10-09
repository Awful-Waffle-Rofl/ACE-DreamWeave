using System;
using System.IO;
using System.Linq;
using System.Reflection;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Account capacity upgrade DAO shape, modelled on CapLedgerDaoShapeTests: a source-text test that
    /// pins the method surface the manager codes against and the transaction traps this layer must
    /// never reintroduce. What a live test would prove (that the purchase round-trips against real
    /// MySQL) stays a manual step, because ACE.Server.Tests has no database.
    /// </summary>
    [TestClass]
    public class AccountCapacityUpgradeDaoShapeTests
    {
        private const string DaoFile = "ShardDatabase_AccountCapacityUpgrade.cs";

        private static string DaoSource()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Source", "ACE.Database", DaoFile)))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find {DaoFile} by walking up from {AppContext.BaseDirectory}");

            return File.ReadAllText(Path.Combine(dir.FullName, "Source", "ACE.Database", DaoFile)).Replace("\r\n", "\n");
        }

        /// <summary>
        /// The DAO with every // and /// comment line removed, so prose that NAMES a forbidden call (the
        /// class header explains why a bare BeginTransaction throws) cannot fail or satisfy a code check.
        /// </summary>
        private static string DaoCode()
            => string.Join("\n", DaoSource().Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        /// <summary>The body of TryPurchaseCapacityUpgrade, from its signature to the next member's doc comment.</summary>
        private static string PurchaseBody()
        {
            var src = DaoCode();
            var start = src.IndexOf("public CapacityUpgradePurchaseResult TryPurchaseCapacityUpgrade(", StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, "TryPurchaseCapacityUpgrade is missing");

            var end = src.IndexOf("private static long ReadBankBalanceInTransaction", start, StringComparison.Ordinal);
            Assert.IsTrue(end > start, "ReadBankBalanceInTransaction no longer follows TryPurchaseCapacityUpgrade");

            return src.Substring(start, end - start);
        }

        [TestMethod]
        public void Dao_DeclaresTheExpectedSignatures()
        {
            var src = DaoSource();

            StringAssert.Contains(src, "public (bool Ok, Dictionary<CapacityUpgradeKind, int> Counts) GetAccountCapacityUpgrades(uint accountId)");
            StringAssert.Contains(src, "public CapacityUpgradePurchaseResult TryPurchaseCapacityUpgrade(uint accountId, CapacityUpgradeKind kind, int expectedCount, long costMmd, long costPyreals, string token, uint characterGuid, out long newBalance)");

            Assert.IsNotNull(typeof(ShardDatabase).GetMethod("GetAccountCapacityUpgrades", BindingFlags.Public | BindingFlags.Instance));
            Assert.IsNotNull(typeof(ShardDatabase).GetMethod("TryPurchaseCapacityUpgrade", BindingFlags.Public | BindingFlags.Instance));
        }

        /// <summary>
        /// A bare BeginTransaction throws under the retrying execution strategy ShardDbContext enables.
        /// The transaction must exist, and must be opened only after CreateExecutionStrategy, inside
        /// Execute. Exactly one BeginTransaction in the file.
        /// </summary>
        [TestMethod]
        public void Purchase_OpensItsTransactionOnlyInsideTheExecutionStrategy()
        {
            var src = DaoCode();
            var body = PurchaseBody();

            var begins = body.Split(new[] { "BeginTransaction(" }, StringSplitOptions.None).Length - 1;
            Assert.AreEqual(1, begins, "exactly one BeginTransaction in the purchase");
            Assert.AreEqual(1, src.Split(new[] { "BeginTransaction(" }, StringSplitOptions.None).Length - 1, "no other BeginTransaction anywhere in the DAO");

            var strategy = body.IndexOf("CreateExecutionStrategy()", StringComparison.Ordinal);
            var execute = body.IndexOf("strategy.Execute(", StringComparison.Ordinal);
            var begin = body.IndexOf("BeginTransaction(", StringComparison.Ordinal);

            Assert.IsTrue(strategy >= 0 && execute > strategy && begin > execute,
                "BeginTransaction must be opened inside strategy.Execute(...)");
        }

        /// <summary>
        /// The idempotency check must precede every write, or a strategy re-run after a lost commit ack
        /// would debit twice.
        /// </summary>
        [TestMethod]
        public void Purchase_ChecksItsOwnTokenBeforeAnyWrite()
        {
            var body = PurchaseBody();

            var tokenCheck = body.IndexOf("AccountCapacityUpgradePurchase", StringComparison.Ordinal);
            var debit = body.IndexOf("DebitAccountBankForUpgradeSql", StringComparison.Ordinal);
            var ensure = body.IndexOf("EnsureCapacityUpgradeRowSql", StringComparison.Ordinal);
            var increment = body.IndexOf("IncrementCapacityUpgradeSql", StringComparison.Ordinal);
            var insert = body.IndexOf("InsertCapacityUpgradePurchaseSql", StringComparison.Ordinal);
            var commit = body.LastIndexOf("transaction.Commit()", StringComparison.Ordinal);

            Assert.IsTrue(tokenCheck >= 0, "the token check is missing");
            Assert.IsTrue(tokenCheck < debit && debit < ensure && ensure < increment && increment < insert && insert < commit,
                "order must be: token check, debit, ensure row, guarded increment, purchase row, commit");
        }

        [TestMethod]
        public void Sql_GuardsLiveInTheWhereClauses()
        {
            var src = DaoSource();

            StringAssert.Contains(src, "WHERE `account_Id` = {1} AND `banked_Pyreals` >= {0}", "the debit must be guarded by the balance in its WHERE");
            StringAssert.Contains(src, "WHERE `account_Id` = {0} AND `upgrade_Kind` = {1} AND `upgrade_Count` = {2}", "the increment must be guarded by the expected count in its WHERE");
            StringAssert.Contains(src, "ON DUPLICATE KEY UPDATE `upgrade_Count` = `upgrade_Count`", "ensure-row must never reset a count");
        }

        /// <summary>Every statement goes through ExecuteSqlRaw with placeholders; nothing is interpolated into SQL.</summary>
        [TestMethod]
        public void Sql_IsNeverInterpolated()
        {
            var body = PurchaseBody();

            Assert.IsFalse(body.Contains("ExecuteSqlRaw($"), "interpolated SQL");
            Assert.IsFalse(body.Contains("ExecuteSqlInterpolated"), "use the placeholder form, matching the bank DAO");
            Assert.IsFalse(body.Contains("FromSqlRaw($"), "interpolated SQL");
        }

        /// <summary>A thrown exception may follow a landed commit, so it must map to Unknown, never a refusal.</summary>
        [TestMethod]
        public void Purchase_ExceptionIsUnknown()
        {
            var body = PurchaseBody();

            var catchAt = body.LastIndexOf("catch (Exception", StringComparison.Ordinal);
            Assert.IsTrue(catchAt >= 0, "the purchase must catch");

            var afterCatch = body.Substring(catchAt);
            StringAssert.Contains(afterCatch, "return CapacityUpgradePurchaseResult.Unknown;");
            Assert.IsFalse(afterCatch.Contains("InsufficientFunds") || afterCatch.Contains("PriceChanged"), "an exception is never a refusal");
        }

        [TestMethod]
        public void RequestValidation_RefusesMalformedPurchases()
        {
            const string token = "0123456789abcdef0123456789abcdef";

            Assert.IsTrue(ShardDatabase.IsValidCapacityUpgradePurchase(1, CapacityUpgradeKind.MuleVault, 0, 500, 125_000_000, token));
            Assert.IsTrue(ShardDatabase.IsValidCapacityUpgradePurchase(1, CapacityUpgradeKind.MarketListings, 99, 1, 250_000, token));

            Assert.IsFalse(ShardDatabase.IsValidCapacityUpgradePurchase(0, CapacityUpgradeKind.MuleVault, 0, 500, 125_000_000, token), "account 0");
            Assert.IsFalse(ShardDatabase.IsValidCapacityUpgradePurchase(1, (CapacityUpgradeKind)0, 0, 500, 125_000_000, token), "kind 0");
            Assert.IsFalse(ShardDatabase.IsValidCapacityUpgradePurchase(1, (CapacityUpgradeKind)3, 0, 500, 125_000_000, token), "kind 3");
            Assert.IsFalse(ShardDatabase.IsValidCapacityUpgradePurchase(1, CapacityUpgradeKind.MuleVault, -1, 500, 125_000_000, token), "negative expected");
            Assert.IsFalse(ShardDatabase.IsValidCapacityUpgradePurchase(1, CapacityUpgradeKind.MuleVault, int.MaxValue, 500, 125_000_000, token), "expected + 1 overflows int");
            Assert.IsFalse(ShardDatabase.IsValidCapacityUpgradePurchase(1, CapacityUpgradeKind.MuleVault, 0, 0, 125_000_000, token), "zero MMD");
            Assert.IsFalse(ShardDatabase.IsValidCapacityUpgradePurchase(1, CapacityUpgradeKind.MuleVault, 0, 500, 0, token), "zero pyreals");
            Assert.IsFalse(ShardDatabase.IsValidCapacityUpgradePurchase(1, CapacityUpgradeKind.MuleVault, 0, 500, -1, token), "negative pyreals");
            Assert.IsFalse(ShardDatabase.IsValidCapacityUpgradePurchase(1, CapacityUpgradeKind.MuleVault, 0, 500, 125_000_000, null), "null token");
            Assert.IsFalse(ShardDatabase.IsValidCapacityUpgradePurchase(1, CapacityUpgradeKind.MuleVault, 0, 500, 125_000_000, token.Substring(1)), "short token");
            Assert.IsFalse(ShardDatabase.IsValidCapacityUpgradePurchase(1, CapacityUpgradeKind.MuleVault, 0, 500, 125_000_000, token.ToUpperInvariant()), "upper-case token");
            Assert.IsFalse(ShardDatabase.IsValidCapacityUpgradePurchase(1, CapacityUpgradeKind.MuleVault, 0, 500, 125_000_000, "g" + token.Substring(1)), "non-hex token");
        }
    }
}
