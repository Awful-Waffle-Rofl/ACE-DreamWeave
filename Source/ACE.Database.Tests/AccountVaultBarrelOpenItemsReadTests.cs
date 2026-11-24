using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Database.Tests
{
    /// <summary>
    /// ShardDatabase.GetOpenAccountVaultBarrelItems against a real shard database. READ-ONLY: it writes
    /// nothing, so unlike the round-trip classes it needs no opt-in, only a reachable shard.
    ///
    /// What it proves is that the query translates and runs: the DAO turns any exception into NULL, so a
    /// LINQ shape Pomelo cannot translate would come back null here. The existing GetAccountVaultBarrels
    /// is the control - if that one also reads null, the table is missing from this database and the
    /// test is inconclusive rather than failed.
    /// </summary>
    [TestClass]
    public class AccountVaultBarrelOpenItemsReadTests
    {
        /// <summary>An account id no real account uses.</summary>
        private const uint UnusedAccountId = 0x7FFFFF02;

        [TestMethod]
        public void GetOpenAccountVaultBarrelItems_Translates_AndReadsAnEmptyListForAnUnusedAccount()
        {
            TestEnvironment.InitializeConfig();

            var shardDb = new ShardDatabase();

            bool reachable;

            try
            {
                reachable = shardDb.Exists(false);
            }
            catch
            {
                reachable = false;
            }

            if (!reachable)
                Assert.Inconclusive("Skipped: the shard database is not reachable with the configured Config.js.");

            if (shardDb.GetAccountVaultBarrels(UnusedAccountId, 1) == null)
                Assert.Inconclusive("Skipped: account_vault_barrel cannot be read on this database (the control read failed too).");

            var rows = shardDb.GetOpenAccountVaultBarrelItems(UnusedAccountId, ShardDatabase.MaxAccountVaultBarrelRows + 1);

            Assert.IsNotNull(rows, "null means the read threw: the query did not translate or run");
            Assert.AreEqual(0, rows.Count, "an unused account has no open item rows");
        }
    }
}
