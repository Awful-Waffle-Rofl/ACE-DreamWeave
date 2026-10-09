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
    /// Mule Vendor vault DAO. Three things are asserted here that a live-database test would not catch
    /// as cheaply: the exact method surface Task 4 codes against, the persistence traps this layer must
    /// never reintroduce (a user-initiated EF transaction, and a read-modify-write on the stack ledger),
    /// and the read failure contract that keeps a transient outage from reading as "this account owns
    /// nothing".
    /// </summary>
    [TestClass]
    public class AccountVaultDaoShapeTests
    {
        private static string DaoSourcePath()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Source", "ACE.Database", "ShardDatabase_AccountVault.cs")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find ShardDatabase_AccountVault.cs by walking up from {AppContext.BaseDirectory}");

            return Path.Combine(dir.FullName, "Source", "ACE.Database", "ShardDatabase_AccountVault.cs");
        }

        /// <summary>
        /// Line endings normalised to \n. Most of the working tree is CRLF while the index is LF, so a
        /// test that matched on "\n\n" against the raw file would pass or fail depending on how the file
        /// was checked out.
        /// </summary>
        private static string DaoSource() => File.ReadAllText(DaoSourcePath()).Replace("\r\n", "\n");

        /// <summary>
        /// One member's text, from its signature to wherever the next member begins.
        /// </summary>
        private static string MemberBody(string src, string signature)
        {
            var start = src.IndexOf(signature, StringComparison.Ordinal);

            Assert.IsTrue(start >= 0, $"could not find the signature '{signature}' in the DAO source");

            var end = src.Length;

            foreach (var marker in new[] { "\n\n        /// <summary>", "\n\n        public", "\n\n        private" })
            {
                var i = src.IndexOf(marker, start + signature.Length, StringComparison.Ordinal);

                if (i >= 0 && i < end)
                    end = i;
            }

            return src.Substring(start, end - start);
        }

        private static readonly string[] ReadMethodSignatures =
        {
            "public List<AccountVault> GetAccountVaults(uint accountId)",
            "public List<AccountVaultStack> GetAccountVaultStacks(uint accountId)",
            "public List<AccountVaultGrant> GetAccountVaultGrants(uint ownerAccountId)",
            "public List<AccountVaultGrant> GetAccountVaultGrantsForGrantee(uint granteeCharacterGuid)",
            "public List<AccountVaultLog> GetAccountVaultLog(uint ownerAccountId, int limit)",
        };

        [TestMethod]
        public void ShardDatabase_ExposesEveryVaultMethod()
        {
            var expected = new[]
            {
                "GetAccountVaults", "AddAccountVault", "DeleteAccountVault",
                "GetAccountVaultStacks", "TryAdjustAccountVaultStack", "TryAdjustAccountVaultStackBatch", "DeleteEmptyAccountVaultStacks",
                "GetAccountVaultGrants", "GetAccountVaultGrantsForGrantee", "UpsertAccountVaultGrant", "DeleteAccountVaultGrant",
                "AddAccountVaultLog", "GetAccountVaultLog",
                "GetAccountVaultClasses", "TryAdjustAccountVaultClass", "TryAdjustAccountVaultClassBatch", "DeleteEmptyAccountVaultClasses",
            };

            foreach (var name in expected)
                Assert.IsNotNull(typeof(ShardDatabase).GetMethod(name, BindingFlags.Public | BindingFlags.Instance),
                    $"ShardDatabase.{name} is missing");
        }

        [TestMethod]
        public void Dao_NeverOpensAUserInitiatedTransaction()
        {
            // All three DbContexts enable EnableRetryOnFailure, and MySqlRetryingExecutionStrategy
            // refuses a user-initiated transaction outright. Anything spanning statements must go
            // through CreateExecutionStrategy().Execute with an idempotent delegate instead.
            var src = DaoSource();

            Assert.IsFalse(src.Contains("BeginTransaction"),
                "BeginTransaction throws under the retrying execution strategy this context enables");
        }

        /// <summary>
        /// R3, stated as the invariant that actually holds rather than as the presence of one SQL
        /// keyword.
        ///
        /// The earlier version of this test asserted only that the file contained
        /// "ON DUPLICATE KEY UPDATE". That string still survives in the ensure-row statement and in the
        /// prose explaining why the single-statement form was rejected, so it would have kept passing
        /// after someone replaced the delta with `row.Count += delta; SaveChanges();` - the exact
        /// lost-update this test exists to prevent - and it read as coverage while providing none.
        /// </summary>
        [TestMethod]
        public void StackDelta_IsAppliedByTheDatabaseWithTheGuardInTheWhere()
        {
            var src = DaoSource();

            // 1. The database applies the delta, in one statement, to its own current value.
            StringAssert.Contains(src, "SET `count` = `count` + {2}",
                "the ledger delta must be applied by the database in one statement, not computed in C#");

            // 2. The non-negative guard is in the WHERE, which is what makes a refusal report zero rows
            //    under either UseAffectedRows setting. Moving it into an IF() in the SET clause silently
            //    reinstates the item-duplication bug, because CLIENT_FOUND_ROWS then reports 1 for a
            //    refused update.
            StringAssert.Contains(src, "WHERE `account_Id` = {0} AND `wcid` = {1} AND `count` + {2} >= 0",
                "the non-negative guard must live in the WHERE clause, not in the SET clause");

            // 3. No C# read-modify-write on Count anywhere in the file. `newCount = row.Count;` is an
            //    assignment FROM Count and has no dot before the target; `s.Count <= 0` is a comparison.
            var readModifyWrite = Regex.Matches(src, @"\.Count\s*(\+=|-=|=(?!=))");

            Assert.AreEqual(0, readModifyWrite.Count,
                "a C# read-modify-write on Count is risk R3, the lost update this DAO exists to avoid; " +
                $"found: {string.Join(", ", readModifyWrite.Cast<Match>().Select(m => m.Value))}");
        }

        /// <summary>
        /// The BATCHED deposit form, asserted on exactly the points the single-row statement is, because
        /// its multi-row shape makes two of them sharper rather than softer.
        ///
        /// The guard's home matters MORE here than on the single-row statement. There, a guard moved
        /// into an IF() corrupts one answer: a refused row reports one affected row under
        /// CLIENT_FOUND_ROWS and reads as a success. Here the affected count is ALSO the only evidence
        /// of WHICH keys the UPDATE credited, so the same move corrupts the partition that decides which
        /// keys the follow-up upsert carries - and that upsert's duplicate branch ADDS, so a key
        /// wrongly believed uncredited is credited TWICE.
        ///
        /// These assert the SKELETON constants rather than the generated SQL, which is what the DAO can
        /// be pinned on from source text: the CASE and IN lists are built from positional placeholders
        /// at call time, so only the fixed frame is a literal.
        /// </summary>
        [TestMethod]
        public void StackBatchDelta_IsAppliedByTheDatabaseWithTheGuardInTheWhere()
        {
            var src = DaoSource();

            // 1. The database applies every delta, in one statement, to its own current values. A CASE
            //    over the key column is what carries N deltas in one SET.
            StringAssert.Contains(src, "UPDATE `account_vault_stack` SET `count` = `count` + <CASE> ",
                "the batched ledger deltas must be applied by the database in one statement, not computed in C#");

            // 2. The non-negative guard is in the WHERE, never in an IF() in the SET clause.
            StringAssert.Contains(src, "WHERE `account_Id` = {0} AND `wcid` IN (<KEYS>) AND `count` + <CASE> >= 0",
                "the non-negative guard must live in the WHERE clause of the batched UPDATE, not in the SET clause");

            // 3. The fallback for the genuinely absent keys ADDS at the database too, so a stale
            //    "already present" oracle costs a duplicate branch rather than a lost or doubled delta.
            StringAssert.Contains(src, "ON DUPLICATE KEY UPDATE `count` = `count` + VALUES(`count`)",
                "the batched upsert must add the delta at the database, not overwrite the count with a value computed in C#");

            // 4. No C# read-modify-write on Count anywhere in the file, and no user-initiated
            //    transaction anywhere in it either. Both are asserted whole-file by
            //    StackDelta_IsAppliedByTheDatabaseWithTheGuardInTheWhere and
            //    Dao_NeverOpensAUserInitiatedTransaction, so the batch inherits them by construction -
            //    which is precisely why the spec requires these methods to live in THIS file.
            Assert.IsFalse(src.Contains("BeginTransaction"),
                "the batched form must not open a user-initiated transaction either; each statement is its own implicit one");

            var readModifyWrite = Regex.Matches(src, @"\.Count\s*(\+=|-=|=(?!=))");

            Assert.AreEqual(0, readModifyWrite.Count,
                "a C# read-modify-write on Count is risk R3, and a batched one loses N updates rather than one; " +
                $"found: {string.Join(", ", readModifyWrite.Cast<Match>().Select(m => m.Value))}");
        }

        [TestMethod]
        public void ReadMethods_AreNoTracking()
        {
            // Every read here feeds an in-memory store that owns its own copies. Tracking them would
            // hold a context alive past the using block for no benefit.
            var src = DaoSource();
            var occurrences = src.Split(new[] { "QueryTrackingBehavior.NoTracking" }, StringSplitOptions.None).Length - 1;

            Assert.IsTrue(occurrences >= 5, $"expected NoTracking on all five read methods, found {occurrences}");
        }

        /// <summary>
        /// A MySqlException escaping one of these reaches a player-facing handler path (the summon
        /// command and the vendor panel). Each must catch and return null.
        ///
        /// Null specifically, never an empty list: DESIGN 7.1 has insertion scan the account's vaults
        /// oldest-first and create a new one when all are full, so answering a transient failure with an
        /// empty list makes Task 4 create a duplicate vault, the player's items appear gone, and
        /// flapping accumulates orphan containers.
        /// </summary>
        [TestMethod]
        public void ReadMethods_CatchAndReturnNullOnFailure()
        {
            var src = DaoSource();

            foreach (var signature in ReadMethodSignatures)
            {
                var body = MemberBody(src, signature);

                StringAssert.Contains(body, "catch (Exception",
                    $"{signature} lets a database exception escape into a player-facing handler path");

                StringAssert.Contains(body, "return null;",
                    $"{signature} must return null on failure, never an empty list");
            }
        }

        // ---------------- the class ledger, in its own file ----------------

        /// <summary>
        /// The counted item-class ledger lives in ShardDatabase_AccountVaultClass.cs, so every
        /// source-text assertion above reads the WRONG file for it. These repeat the same three
        /// invariants against the class DAO rather than widening DaoSource to concatenate both, so a
        /// failure names which ledger broke.
        /// </summary>
        private static string ClassDaoSource()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Source", "ACE.Database", "ShardDatabase_AccountVaultClass.cs")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find ShardDatabase_AccountVaultClass.cs by walking up from {AppContext.BaseDirectory}");

            return File.ReadAllText(Path.Combine(dir.FullName, "Source", "ACE.Database", "ShardDatabase_AccountVaultClass.cs")).Replace("\r\n", "\n");
        }

        [TestMethod]
        public void ClassDao_NeverOpensAUserInitiatedTransaction()
        {
            Assert.IsFalse(ClassDaoSource().Contains("BeginTransaction"),
                "BeginTransaction throws under the retrying execution strategy this context enables");
        }

        /// <summary>
        /// The class row carries TWO counters that must move together or not at all: the item count and
        /// the pooled value. A second UPDATE, or a C# read-modify-write on either, reintroduces R3 and
        /// can also leave count and total_Value disagreeing, which is the one state the pooled-value
        /// invariant cannot recover from.
        /// </summary>
        [TestMethod]
        public void ClassDelta_MovesCountAndValueInOneGuardedStatement()
        {
            var src = ClassDaoSource();

            StringAssert.Contains(src, "SET `count` = `count` + {2}, `total_Value` = `total_Value` + {3}",
                "count and total_Value must move in ONE statement, applied by the database to its own current values");

            StringAssert.Contains(src, "WHERE `account_Id` = {0} AND `class_Key` = {1} AND `count` + {2} >= 0 AND `total_Value` + {3} >= 0",
                "both non-negative guards must live in the WHERE clause, not in the SET clause");

            var readModifyWrite = Regex.Matches(src, @"\.(Count|TotalValue)\s*(\+=|-=|=(?!=))");

            Assert.AreEqual(0, readModifyWrite.Count,
                "a C# read-modify-write on Count or TotalValue is risk R3, the lost update this DAO exists to avoid; " +
                $"found: {string.Join(", ", readModifyWrite.Cast<Match>().Select(m => m.Value))}");
        }

        /// <summary>
        /// The BATCHED class deposit form, asserted on the same points as the stack batch plus the one
        /// invariant only this ledger has: count and total_Value still move in ONE statement, so a row
        /// can never say it holds N items worth a total that belongs to N+1.
        /// </summary>
        [TestMethod]
        public void ClassBatchDelta_MovesCountAndValueInOneGuardedStatement()
        {
            var src = ClassDaoSource();

            StringAssert.Contains(src, "UPDATE `account_vault_class` SET `count` = `count` + <COUNTCASE>, `total_Value` = `total_Value` + <VALUECASE> ",
                "count and total_Value must move in ONE batched statement, applied by the database to its own current values");

            StringAssert.Contains(src, "WHERE `account_Id` = {0} AND `class_Key` IN (<KEYS>) AND `count` + <COUNTCASE> >= 0 AND `total_Value` + <VALUECASE> >= 0",
                "both non-negative guards must live in the WHERE clause of the batched UPDATE, not in the SET clause");

            StringAssert.Contains(src, "ON DUPLICATE KEY UPDATE `count` = `count` + VALUES(`count`), `total_Value` = `total_Value` + VALUES(`total_Value`)",
                "the batched upsert must add BOTH deltas at the database, in one statement, so the two columns cannot diverge");

            Assert.IsFalse(src.Contains("BeginTransaction"),
                "the batched form must not open a user-initiated transaction either; each statement is its own implicit one");

            var readModifyWrite = Regex.Matches(src, @"\.(Count|TotalValue)\s*(\+=|-=|=(?!=))");

            Assert.AreEqual(0, readModifyWrite.Count,
                "a C# read-modify-write on Count or TotalValue is risk R3, and a batched one loses N updates rather than one; " +
                $"found: {string.Join(", ", readModifyWrite.Cast<Match>().Select(m => m.Value))}");
        }

        [TestMethod]
        public void ClassReadMethod_CatchesAndReturnsNullOnFailure()
        {
            var src = ClassDaoSource();
            var body = MemberBody(src, "public List<AccountVaultClass> GetAccountVaultClasses(uint accountId)");

            StringAssert.Contains(body, "QueryTrackingBehavior.NoTracking",
                "GetAccountVaultClasses must not track, like every other vault read");

            StringAssert.Contains(body, "catch (Exception",
                "GetAccountVaultClasses lets a database exception escape into a player-facing handler path");

            StringAssert.Contains(body, "return null;",
                "GetAccountVaultClasses must return null on failure, never an empty list - an empty list reads as " +
                "'this account holds no classes', which would let a deposit start a second row for a class that already exists");
        }
    }
}
