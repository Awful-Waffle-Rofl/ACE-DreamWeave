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
            "public List<AccountVaultLog> GetAccountVaultLog(uint ownerAccountId, int limit)",
        };

        [TestMethod]
        public void ShardDatabase_ExposesEveryVaultMethod()
        {
            var expected = new[]
            {
                "GetAccountVaults", "AddAccountVault", "DeleteAccountVault",
                "GetAccountVaultStacks", "TryAdjustAccountVaultStack", "DeleteEmptyAccountVaultStacks",
                "GetAccountVaultGrants", "UpsertAccountVaultGrant", "DeleteAccountVaultGrant",
                "AddAccountVaultLog", "GetAccountVaultLog",
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

        [TestMethod]
        public void ReadMethods_AreNoTracking()
        {
            // Every read here feeds an in-memory store that owns its own copies. Tracking them would
            // hold a context alive past the using block for no benefit.
            var src = DaoSource();
            var occurrences = src.Split(new[] { "QueryTrackingBehavior.NoTracking" }, StringSplitOptions.None).Length - 1;

            Assert.IsTrue(occurrences >= 4, $"expected NoTracking on all four read methods, found {occurrences}");
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
    }
}
