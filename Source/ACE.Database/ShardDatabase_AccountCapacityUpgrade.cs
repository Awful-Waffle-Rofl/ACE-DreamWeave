using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.EntityFrameworkCore;

using ACE.Common.Extensions;
using ACE.Database.Models.Shard;

namespace ACE.Database
{
    /// <summary>
    /// Account capacity upgrades - the account_capacity_upgrade and account_capacity_upgrade_purchase
    /// tables (Database/Updates/Shard/2026-09-17-00-Add-Account-Capacity-Upgrade.sql).
    ///
    /// Ledger operations, not biota operations, so like the account bank DAO these deliberately do NOT
    /// run through SerializedShardDatabase's worker thread. The caller is AccountCapacityUpgradeManager,
    /// which serializes per account itself and routes the purchase through
    /// AccountBankManager.TryAdjustVia so the bank cache is updated from this method's answer.
    ///
    /// THE PURCHASE IS THE ONE PLACE A USER-INITIATED TRANSACTION IS OPENED, and it is opened ONLY inside
    /// context.Database.CreateExecutionStrategy().Execute(...). ShardDbContext.OnConfiguring enables
    /// EnableRetryOnFailure, and MySqlRetryingExecutionStrategy refuses a bare BeginTransaction()
    /// outright. The strategy may also re-run the whole delegate after a commit whose acknowledgement was
    /// lost, so the delegate is idempotent by construction: its FIRST statement looks for this attempt's
    /// own purchase token, and finding it means the purchase already committed.
    ///
    /// READ FAILURE CONTRACT: <see cref="GetAccountCapacityUpgrades"/> returns Ok = false when the read
    /// FAILED and Ok = true with an empty dictionary when the account owns no upgrades. A caller must
    /// never quote a price off a failed read - it would quote the n = 0 price to an account that owns
    /// upgrades - and must never cache one.
    /// </summary>
    public partial class ShardDatabase
    {
        /// <summary>
        /// The debit. Guard in the WHERE, not in an IF() inside an upsert, for the reason documented on
        /// ApplyAccountBankDeltaSql: ACE's connection runs with CLIENT_FOUND_ROWS, and a WHERE that
        /// matches nothing reports zero rows under either setting. The cost is always positive, so a
        /// matched row is always a changed row and the count is unambiguous. The subtraction cannot
        /// underflow: the guard admits only balances at least as large as the cost.
        /// </summary>
        private const string DebitAccountBankForUpgradeSql =
            "UPDATE `account_bank` SET `banked_Pyreals` = `banked_Pyreals` - {0}, `updated_At` = UTC_TIMESTAMP() " +
            "WHERE `account_Id` = {1} AND `banked_Pyreals` >= {0}";

        /// <summary>
        /// Ensures the count row exists at 0 without touching an existing count. The duplicate branch
        /// assigns the count to itself, so it cannot lose a concurrent increment.
        /// </summary>
        private const string EnsureCapacityUpgradeRowSql =
            "INSERT INTO `account_capacity_upgrade` (`account_Id`, `upgrade_Kind`, `upgrade_Count`, `updated_At`) " +
            "VALUES ({0}, {1}, 0, UTC_TIMESTAMP()) " +
            "ON DUPLICATE KEY UPDATE `upgrade_Count` = `upgrade_Count`";

        /// <summary>
        /// The guarded increment. Matches only when the count is still the one the price was quoted
        /// against; zero rows means the price changed and the whole transaction rolls back.
        /// </summary>
        private const string IncrementCapacityUpgradeSql =
            "UPDATE `account_capacity_upgrade` SET `upgrade_Count` = `upgrade_Count` + 1, `updated_At` = UTC_TIMESTAMP() " +
            "WHERE `account_Id` = {0} AND `upgrade_Kind` = {1} AND `upgrade_Count` = {2}";

        private const string InsertCapacityUpgradePurchaseSql =
            "INSERT INTO `account_capacity_upgrade_purchase` " +
            "(`purchase_Token`, `account_Id`, `upgrade_Kind`, `upgrade_Number`, `cost_Mmd`, `cost_Pyreals`, `character_Guid`, `purchased_At`) " +
            "VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {6}, UTC_TIMESTAMP())";

        /// <summary>The purchase token length: a Guid formatted "N".</summary>
        public const int CapacityUpgradePurchaseTokenLength = 32;

        /// <summary>
        /// Every capacity upgrade count one account owns, keyed by kind. A kind with no row is absent,
        /// which means a count of 0.
        ///
        /// Returns <c>(false, null)</c> when the READ FAILED and <c>(true, empty)</c> when the account
        /// owns nothing. The two must never be conflated - see the class header.
        /// </summary>
        public (bool Ok, Dictionary<CapacityUpgradeKind, int> Counts) GetAccountCapacityUpgrades(uint accountId)
        {
            try
            {
                using (var context = new ShardDbContext())
                {
                    context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                    var rows = context.AccountCapacityUpgrade
                        .Where(u => u.AccountId == accountId)
                        .Select(u => new { u.UpgradeKind, u.UpgradeCount })
                        .ToList();

                    var counts = new Dictionary<CapacityUpgradeKind, int>();

                    foreach (var row in rows)
                    {
                        // The column is int unsigned; the server works in int. No purchase path can come
                        // near int.MaxValue (the hard cap is a few hundred at most), so this clamp only
                        // guards a hand-edited row from wrapping negative.
                        counts[(CapacityUpgradeKind)row.UpgradeKind] = row.UpgradeCount > int.MaxValue ? int.MaxValue : (int)row.UpgradeCount;
                    }

                    return (true, counts);
                }
            }
            catch (Exception ex)
            {
                log.Error($"[UPGRADE] GetAccountCapacityUpgrades failed for account {accountId}: {ex.GetFullMessage()}");
                return (false, null);
            }
        }

        /// <summary>
        /// Buys one capacity upgrade: debits <paramref name="costPyreals"/> from the account bank, moves
        /// the count for <paramref name="kind"/> from <paramref name="expectedCount"/> to
        /// expectedCount + 1, and writes the purchase row, all in ONE transaction.
        ///
        /// Steps, in order, inside the execution strategy:
        ///   1. This attempt's token already recorded for this account and kind: the purchase committed
        ///      on an earlier run of this delegate. Report Applied with the current balance.
        ///   2. Guarded debit. Zero rows: roll back, InsufficientFunds.
        ///   3. Ensure the count row exists.
        ///   4. Guarded increment against the expected count. Zero rows: roll back, PriceChanged.
        ///   5. Insert the purchase row, read the post-debit balance, commit.
        ///
        /// The balance is read INSIDE the transaction, after the debit. The debit holds the bank row's
        /// exclusive lock until commit, so that value is exactly the committed balance and there is no
        /// separate read-back that could fail after the commit.
        ///
        /// ANY EXCEPTION IS <see cref="CapacityUpgradePurchaseResult.Unknown"/>, never a refusal: the
        /// commit may have landed before the throw.
        ///
        /// <paramref name="newBalance"/> is meaningful ONLY for Applied; it is 0 for every other value.
        /// </summary>
        public CapacityUpgradePurchaseResult TryPurchaseCapacityUpgrade(uint accountId, CapacityUpgradeKind kind, int expectedCount, long costMmd, long costPyreals, string token, uint characterGuid, out long newBalance)
        {
            newBalance = 0;

            if (!IsValidCapacityUpgradePurchase(accountId, kind, expectedCount, costMmd, costPyreals, token))
            {
                log.Error($"[UPGRADE] TryPurchaseCapacityUpgrade refused a malformed request: account {accountId}, kind {(byte)kind}, expected {expectedCount}, cost {costMmd} MMD / {costPyreals} pyreals, token length {token?.Length ?? -1}.");
                return CapacityUpgradePurchaseResult.InvalidRequest;
            }

            var kindValue = (byte)kind;
            var expected = (uint)expectedCount;
            var upgradeNumber = expected + 1;

            try
            {
                using (var context = new ShardDbContext())
                {
                    var strategy = context.Database.CreateExecutionStrategy();

                    var outcome = strategy.Execute(() =>
                    {
                        using (var transaction = context.Database.BeginTransaction())
                        {
                            // 1. Idempotency. Must stay FIRST: every statement after it moves money or
                            // a count, and a re-run that skipped this check would charge twice.
                            var alreadyCommitted = context.AccountCapacityUpgradePurchase
                                .AsNoTracking()
                                .Any(p => p.PurchaseToken == token && p.AccountId == accountId && p.UpgradeKind == kindValue);

                            if (alreadyCommitted)
                            {
                                var current = ReadBankBalanceInTransaction(context, accountId);
                                transaction.Commit();
                                return (Result: CapacityUpgradePurchaseResult.Applied, Balance: current);
                            }

                            // 2. Debit.
                            if (context.Database.ExecuteSqlRaw(DebitAccountBankForUpgradeSql, costPyreals, accountId) == 0)
                            {
                                transaction.Rollback();
                                return (Result: CapacityUpgradePurchaseResult.InsufficientFunds, Balance: 0L);
                            }

                            // 3. Count row.
                            context.Database.ExecuteSqlRaw(EnsureCapacityUpgradeRowSql, accountId, kindValue);

                            // 4. Guarded increment.
                            if (context.Database.ExecuteSqlRaw(IncrementCapacityUpgradeSql, accountId, kindValue, expected) == 0)
                            {
                                transaction.Rollback();
                                return (Result: CapacityUpgradePurchaseResult.PriceChanged, Balance: 0L);
                            }

                            // 5. Ledger row, balance, commit.
                            context.Database.ExecuteSqlRaw(InsertCapacityUpgradePurchaseSql, token, accountId, kindValue, upgradeNumber, costMmd, costPyreals, characterGuid);

                            var after = ReadBankBalanceInTransaction(context, accountId);

                            transaction.Commit();

                            return (Result: CapacityUpgradePurchaseResult.Applied, Balance: after);
                        }
                    });

                    newBalance = outcome.Result == CapacityUpgradePurchaseResult.Applied ? outcome.Balance : 0;
                    return outcome.Result;
                }
            }
            catch (Exception ex)
            {
                log.Error($"[UPGRADE] TryPurchaseCapacityUpgrade threw for account {accountId}, kind {kind}, expected {expectedCount}, cost {costMmd} MMD / {costPyreals} pyreals, token {token}: {ex.GetFullMessage()}. Whether it committed is UNKNOWN.");

                newBalance = 0;
                return CapacityUpgradePurchaseResult.Unknown;
            }
        }

        /// <summary>
        /// The bank balance as this transaction sees it. The row must exist - step 2 matched it, or on
        /// the idempotent path an earlier run of this purchase did - so a missing row is thrown rather
        /// than reported as 0, which a caller would cache as a real balance.
        /// </summary>
        private static long ReadBankBalanceInTransaction(ShardDbContext context, uint accountId)
        {
            var balance = context.AccountBank
                .AsNoTracking()
                .Where(b => b.AccountId == accountId)
                .Select(b => (long?)b.BankedPyreals)
                .FirstOrDefault();

            if (balance == null)
                throw new InvalidOperationException($"account_bank row for account {accountId} is missing after a capacity upgrade debit");

            return balance.Value;
        }

        /// <summary>
        /// The request shape the purchase accepts. Checked before any statement, so a malformed call
        /// can never be mistaken for a refusal by the database or for an unknown outcome.
        /// </summary>
        public static bool IsValidCapacityUpgradePurchase(uint accountId, CapacityUpgradeKind kind, int expectedCount, long costMmd, long costPyreals, string token)
        {
            if (accountId == 0)
                return false;

            if (kind != CapacityUpgradeKind.MuleVault && kind != CapacityUpgradeKind.MarketListings)
                return false;

            // int.MaxValue excluded so expected + 1 still fits the int the server caches.
            if (expectedCount < 0 || expectedCount == int.MaxValue)
                return false;

            if (costMmd <= 0 || costPyreals <= 0)
                return false;

            if (token == null || token.Length != CapacityUpgradePurchaseTokenLength)
                return false;

            // The column is ascii_bin; lower-case hex is what Guid "N" produces and all it may hold.
            foreach (var c in token)
            {
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                    return false;
            }

            return true;
        }
    }
}
