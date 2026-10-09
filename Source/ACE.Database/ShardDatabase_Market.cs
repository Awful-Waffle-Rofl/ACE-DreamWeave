using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.EntityFrameworkCore;

using ACE.Common.Extensions;
using ACE.Database.Models.Shard;

namespace ACE.Database
{
    /// <summary>
    /// Market DAO (Docs/Market/DESIGN.md section 5), off the SerializedShardDatabase worker thread.
    /// Reads return NULL on failure, EMPTY LIST when genuinely empty - MarketManager rebuilds its whole index from this at boot.
    /// </summary>
    public partial class ShardDatabase
    {
        /// <summary>Every listing row, oldest first. NULL means the read FAILED.</summary>
        public List<MarketListing> GetAllMarketListings()
        {
            try
            {
                using (var context = new ShardDbContext())
                {
                    context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                    return context.MarketListing.OrderBy(l => l.Id).ToList();
                }
            }
            catch (Exception ex)
            {
                log.Error($"[MARKET] GetAllMarketListings failed: {ex.GetFullMessage()}");
                return null;
            }
        }

        /// <summary>Active listings only, oldest first. NULL means the read FAILED.</summary>
        public List<MarketListing> GetActiveMarketListings()
        {
            try
            {
                using (var context = new ShardDbContext())
                {
                    context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                    return context.MarketListing.Where(l => l.Status == 0).OrderBy(l => l.Id).ToList();
                }
            }
            catch (Exception ex)
            {
                log.Error($"[MARKET] GetActiveMarketListings failed: {ex.GetFullMessage()}");
                return null;
            }
        }

        /// <summary>
        /// Inserts a listing, writes the generated id back onto <paramref name="row"/>. A UNIQUE
        /// violation on either active_* key is a normal already_listed outcome, not surfaced here.
        /// </summary>
        public bool AddMarketListing(MarketListing row)
        {
            if (row == null)
            {
                log.Error("[MARKET] AddMarketListing called with a null row.");
                return false;
            }

            if (row.CreatedAt == default)
                row.CreatedAt = DateTime.UtcNow;

            try
            {
                using (var context = new ShardDbContext())
                {
                    context.MarketListing.Add(row);
                    context.SaveChanges();
                }

                return true;
            }
            catch (Exception ex)
            {
                log.Error($"[MARKET] AddMarketListing failed for seller {row.SellerAccountId}, wcid {row.Wcid}: {ex.GetFullMessage()}");
                return false;
            }
        }

        public bool UpdateMarketListing(MarketListing row)
        {
            if (row == null || row.Id == 0)
            {
                log.Error("[MARKET] UpdateMarketListing called with a null or unsaved row.");
                return false;
            }

            try
            {
                using (var context = new ShardDbContext())
                {
                    context.MarketListing.Update(row);
                    context.SaveChanges();
                }

                return true;
            }
            catch (Exception ex)
            {
                log.Error($"[MARKET] UpdateMarketListing failed for listing {row.Id}: {ex.GetFullMessage()}");
                return false;
            }
        }

        /// <summary>
        /// Rewrites snapshot_Json on listings that are STILL ACTIVE, and touches no other column.
        ///
        /// REPORTS PER ROW, and the three outcomes are not interchangeable. A row that RAN and
        /// matched nothing is Missed: the listing closed between its projection and this write,
        /// which is benign and self-correcting (it is deliberately not distinguishable from "no such
        /// listing", because at this depth the two are the same fact). A row that THREW is Failed:
        /// that listing is still open and its stored snapshot is now out of step with the copy the
        /// caller has already put in memory, which is a condition an operator has to be told about.
        /// Returning a bare count would force the caller to derive one from the other by
        /// subtraction, and the subtraction cannot tell them apart - it would report a permanently
        /// failing write as ordinary concurrent-close churn.
        ///
        /// DELIBERATELY NOT <see cref="UpdateMarketListing"/>: that is a full-row Update over a copy
        /// the caller read earlier, so it would carry status, count, active_Item_Guid and closed_At
        /// backwards over a concurrent buy or delist. The `Status == 0` predicate here is the second
        /// half of the same guard - a listing that closed mid-pass is skipped rather than reopened.
        ///
        /// Each ExecuteUpdate is its own implicit transaction and no explicit one is opened, exactly
        /// as PruneMarketRejectedAttempts reasons about ExecuteDelete: an explicit transaction would
        /// be refused outright by MySqlRetryingExecutionStrategy (see the repo CLAUDE.md). The
        /// per-row try/catch means one bad row costs its own rewrite and not the whole pass.
        /// </summary>
        public MarketListingSnapshotWriteResult UpdateMarketListingSnapshots(IReadOnlyList<MarketListingSnapshotUpdate> rows)
        {
            var result = new MarketListingSnapshotWriteResult();

            if (rows == null || rows.Count == 0)
                return result;

            try
            {
                using (var context = new ShardDbContext())
                {
                    foreach (var row in rows)
                    {
                        // Counted as a FAILURE rather than skipped. A malformed row is a caller
                        // defect, not a listing that closed, and silently dropping it would leave
                        // the result's three counters not adding up to what was submitted.
                        if (row == null || row.ListingId == 0 || row.SnapshotJson == null)
                        {
                            log.Error($"[MARKET] UpdateMarketListingSnapshots was handed a malformed row (listing {row?.ListingId.ToString() ?? "null"}); it is counted as a failed write.");
                            result.Failed++;
                            continue;
                        }

                        // Hoisted out of the expression trees below rather than read off `row`
                        // inside them, so each query is parameterized on a plain local instead of
                        // capturing the row object.
                        var listingId = row.ListingId;
                        var json = row.SnapshotJson;

                        try
                        {
                            var affected = context.MarketListing
                                .Where(l => l.Id == listingId && l.Status == 0)
                                .ExecuteUpdate(s => s.SetProperty(l => l.SnapshotJson, json));

                            if (affected > 0)
                                result.Updated += affected;
                            else
                                result.Missed++;
                        }
                        catch (Exception ex)
                        {
                            // NOT Missed. This listing may well still be Active, so its stored
                            // snapshot is now behind the caller's in-memory copy and stays that way
                            // until somebody is told.
                            log.Error($"[MARKET] UpdateMarketListingSnapshots could not rewrite listing {listingId}: {ex.GetFullMessage()}. The rest of the batch continues.");
                            result.Failed++;
                            result.FailedListingIds.Add(listingId);
                        }
                    }
                }

                return result;
            }
            catch (Exception ex)
            {
                log.Error($"[MARKET] UpdateMarketListingSnapshots failed for {rows.Count} row(s); none of them were written: {ex.GetFullMessage()}");
                return new MarketListingSnapshotWriteResult { BatchFailed = true };
            }
        }

        public bool AddMarketTransaction(MarketTransaction row)
        {
            if (row == null)
            {
                log.Error("[MARKET] AddMarketTransaction called with a null row.");
                return false;
            }

            if (row.Timestamp == default)
                row.Timestamp = DateTime.UtcNow;

            try
            {
                using (var context = new ShardDbContext())
                {
                    context.MarketTransaction.Add(row);
                    context.SaveChanges();
                }

                return true;
            }
            catch (Exception ex)
            {
                log.Error($"[MARKET] AddMarketTransaction failed for listing {row.ListingId}, buyer {row.BuyerAccountId}: {ex.GetFullMessage()}");
                return false;
            }
        }

        public bool UpdateMarketTransaction(MarketTransaction row)
        {
            if (row == null || row.Id == 0)
            {
                log.Error("[MARKET] UpdateMarketTransaction called with a null or unsaved row.");
                return false;
            }

            try
            {
                using (var context = new ShardDbContext())
                {
                    context.MarketTransaction.Update(row);
                    context.SaveChanges();
                }

                return true;
            }
            catch (Exception ex)
            {
                log.Error($"[MARKET] UpdateMarketTransaction failed for transaction {row.Id}: {ex.GetFullMessage()}");
                return false;
            }
        }

        /// <summary>Hard ceiling GetMarketTransactions clamps to, same rationale as MaxAccountVaultLogRows.</summary>
        public const int MaxMarketTransactionRows = 1000;

        /// <summary>Most recent transactions, newest first. NULL means the read FAILED.</summary>
        public List<MarketTransaction> GetMarketTransactions(int limit)
        {
            if (limit < 1)
                limit = 1;
            else if (limit > MaxMarketTransactionRows)
                limit = MaxMarketTransactionRows;

            try
            {
                using (var context = new ShardDbContext())
                {
                    context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                    return context.MarketTransaction
                        .OrderByDescending(t => t.Timestamp)
                        .ThenByDescending(t => t.Id)
                        .Take(limit)
                        .ToList();
                }
            }
            catch (Exception ex)
            {
                log.Error($"[MARKET] GetMarketTransactions failed for limit {limit}: {ex.GetFullMessage()}");
                return null;
            }
        }

        /// <summary>Pending rows older than <paramref name="olderThanUtc"/> - the boot-recovery input. NULL means the read FAILED.</summary>
        public List<MarketTransaction> GetPendingMarketTransactions(DateTime olderThanUtc)
        {
            try
            {
                using (var context = new ShardDbContext())
                {
                    context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                    return context.MarketTransaction
                        .Where(t => t.Status == 0 && t.Timestamp < olderThanUtc)
                        .OrderBy(t => t.Id)
                        .ToList();
                }
            }
            catch (Exception ex)
            {
                log.Error($"[MARKET] GetPendingMarketTransactions failed: {ex.GetFullMessage()}");
                return null;
            }
        }

        /// <summary>Every buy order row, oldest first. NULL means the read FAILED.</summary>
        public List<MarketBuyOrder> GetAllMarketBuyOrders()
        {
            try
            {
                using (var context = new ShardDbContext())
                {
                    context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
                    return context.MarketBuyOrder.OrderBy(o => o.Id).ToList();
                }
            }
            catch (Exception ex)
            {
                log.Error($"[MARKET] GetAllMarketBuyOrders failed: {ex.GetFullMessage()}");
                return null;
            }
        }

        /// <summary>Inserts an order and writes the generated id back. A UNIQUE refusal on the active key is a normal order_exists outcome.</summary>
        public bool AddMarketBuyOrder(MarketBuyOrder row)
        {
            if (row == null) { log.Error("[MARKET] AddMarketBuyOrder called with a null row."); return false; }
            if (row.CreatedAt == default) row.CreatedAt = DateTime.UtcNow;
            try
            {
                using (var context = new ShardDbContext()) { context.MarketBuyOrder.Add(row); context.SaveChanges(); }
                return true;
            }
            catch (Exception ex)
            {
                log.Error($"[MARKET] AddMarketBuyOrder failed for buyer {row.BuyerAccountId}, material {row.MaterialType}: {ex.GetFullMessage()}");
                return false;
            }
        }

        public bool UpdateMarketBuyOrder(MarketBuyOrder row)
        {
            if (row == null || row.Id == 0) { log.Error("[MARKET] UpdateMarketBuyOrder called with a null or unsaved row."); return false; }
            try
            {
                using (var context = new ShardDbContext()) { context.MarketBuyOrder.Update(row); context.SaveChanges(); }
                return true;
            }
            catch (Exception ex)
            {
                log.Error($"[MARKET] UpdateMarketBuyOrder failed for order {row.Id}: {ex.GetFullMessage()}");
                return false;
            }
        }

        /// <summary>
        /// What the COMPLETED fills of each of <paramref name="orderIds"/> add up to - the boot
        /// reconciliation's input (WANTED-DESIGN 6.5). Reads market_transaction_buy_order_idx.
        ///
        /// NULL means the read FAILED and reconciliation must then be SKIPPED; an EMPTY dictionary
        /// means none of those orders has ever been filled, which is the ordinary case. An order with
        /// no entry is therefore a real answer of zero, not a missing one - the caller must not
        /// distinguish "absent" from "zero", only "null" from "not null".
        /// </summary>
        public Dictionary<uint, MarketBuyOrderFillTotals> SumCompletedMarketFillsByOrder(IReadOnlyList<uint> orderIds)
        {
            if (orderIds == null || orderIds.Count == 0)
                return new Dictionary<uint, MarketBuyOrderFillTotals>();

            try
            {
                var ids = orderIds.Distinct().ToList();

                using (var context = new ShardDbContext())
                {
                    context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                    // Status 1 is MarketTransactionStatus.Completed. The enum lives in ACE.Server, which
                    // this assembly cannot reference; GetPendingMarketTransactions writes its 0 the same way.
                    return context.MarketTransaction
                        .Where(t => t.BuyOrderId != null && t.Status == 1 && ids.Contains(t.BuyOrderId.Value))
                        .GroupBy(t => t.BuyOrderId.Value)
                        .Select(g => new { Id = g.Key, Count = g.Sum(t => t.Count), Total = g.Sum(t => t.PriceMmdTotal) })
                        .ToList()
                        .ToDictionary(r => r.Id, r => new MarketBuyOrderFillTotals(r.Count, r.Total));
                }
            }
            catch (Exception ex)
            {
                log.Error($"[MARKET] SumCompletedMarketFillsByOrder failed for {orderIds.Count} order(s): {ex.GetFullMessage()}");
                return null;
            }
        }

        /// <summary>Pending orders older than <paramref name="olderThanUtc"/> - the boot-recovery input. NULL means the read FAILED.</summary>
        public List<MarketBuyOrder> GetPendingMarketBuyOrders(DateTime olderThanUtc)
        {
            try
            {
                using (var context = new ShardDbContext())
                {
                    context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
                    return context.MarketBuyOrder.Where(o => o.Status == 0 && o.CreatedAt < olderThanUtc).OrderBy(o => o.Id).ToList();
                }
            }
            catch (Exception ex)
            {
                log.Error($"[MARKET] GetPendingMarketBuyOrders failed: {ex.GetFullMessage()}");
                return null;
            }
        }

        // ---- investigation surface (Docs/Market/DESIGN.md sections 5.5 and 8.1) ----
        //
        // Every read below clamps its limit exactly as GetMarketTransactions does, returns NULL on a
        // failed read and an EMPTY LIST when genuinely empty, and opens no explicit transaction: a
        // bare SaveChanges / ExecuteDelete is its own implicit transaction, and an explicit one would
        // be refused outright by MySqlRetryingExecutionStrategy (see the repo CLAUDE.md).

        /// <summary>The default page size a null MarketRejectQuery.Limit takes.</summary>
        public const int DefaultMarketQueryRows = 20;

        private static int ClampMarketRows(int? limit)
        {
            var value = limit ?? DefaultMarketQueryRows;

            if (value < 1)
                return 1;

            return value > MaxMarketTransactionRows ? MaxMarketTransactionRows : value;
        }

        /// <summary>
        /// Appends a batch of refused attempts in one SaveChanges. A failure is logged and swallowed,
        /// exactly as AddAccountVaultLog swallows its own: the audit trail is what makes a dispute
        /// answerable, but a logging outage must not become a market outage. The caller (the
        /// MarketRejectionLog writer thread) has already left the player's request path.
        /// </summary>
        public bool AddMarketRejectedAttempts(IReadOnlyList<MarketRejectedAttempt> rows)
        {
            if (rows == null || rows.Count == 0)
                return true;

            try
            {
                using (var context = new ShardDbContext())
                {
                    foreach (var row in rows)
                    {
                        if (row == null)
                            continue;

                        // An audit row is the one artifact whose time has to be right, and every read
                        // below orders by this column. A row that fell back to the CURRENT_TIMESTAMP
                        // store default would carry the DATABASE SERVER'S LOCAL time among rows in UTC.
                        if (row.Timestamp == default)
                            row.Timestamp = DateTime.UtcNow;

                        context.MarketRejectedAttempt.Add(row);
                    }

                    context.SaveChanges();
                }

                return true;
            }
            catch (Exception ex)
            {
                log.Error($"[MARKET] AddMarketRejectedAttempts failed for {rows.Count} row(s); they are LOST: {ex.GetFullMessage()}");
                return false;
            }
        }

        /// <summary>Refused attempts matching <paramref name="query"/>, newest first. NULL means the read FAILED.</summary>
        public List<MarketRejectedAttempt> FindMarketRejectedAttempts(MarketRejectQuery query)
        {
            var q = query ?? new MarketRejectQuery();
            var limit = ClampMarketRows(q.Limit);

            try
            {
                using (var context = new ShardDbContext())
                {
                    context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                    var rows = context.MarketRejectedAttempt.AsQueryable();

                    if (q.AccountId != null)
                        rows = rows.Where(r => r.AccountId == q.AccountId.Value);

                    if (q.CharacterGuid != null)
                        rows = rows.Where(r => r.CharacterGuid == q.CharacterGuid.Value);

                    if (q.ListingId != null)
                        rows = rows.Where(r => r.ListingId == q.ListingId.Value);

                    if (q.Wcid != null)
                        rows = rows.Where(r => r.Wcid == q.Wcid.Value);

                    if (!string.IsNullOrEmpty(q.ReasonCode))
                        rows = rows.Where(r => r.ReasonCode == q.ReasonCode);

                    if (q.FromUtc != null)
                        rows = rows.Where(r => r.Timestamp >= q.FromUtc.Value);

                    if (q.ToUtc != null)
                        rows = rows.Where(r => r.Timestamp < q.ToUtc.Value);

                    return rows
                        .OrderByDescending(r => r.Timestamp)
                        .ThenByDescending(r => r.Id)
                        .Take(limit)
                        .ToList();
                }
            }
            catch (Exception ex)
            {
                log.Error($"[MARKET] FindMarketRejectedAttempts failed: {ex.GetFullMessage()}");
                return null;
            }
        }

        /// <summary>Transactions matching <paramref name="query"/>, newest first. NULL means the read FAILED.</summary>
        public List<MarketTransaction> FindMarketTransactions(MarketTransactionQuery query)
        {
            var q = query ?? new MarketTransactionQuery();
            var limit = ClampMarketRows(q.Limit);

            try
            {
                using (var context = new ShardDbContext())
                {
                    context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                    var rows = context.MarketTransaction.AsQueryable();

                    if (q.EitherSideAccountId != null)
                        rows = rows.Where(t => t.BuyerAccountId == q.EitherSideAccountId.Value
                                            || t.SellerAccountId == q.EitherSideAccountId.Value);

                    if (q.ListingId != null)
                        rows = rows.Where(t => t.ListingId == q.ListingId.Value);

                    if (q.BuyOrderId != null)
                        rows = rows.Where(t => t.BuyOrderId == q.BuyOrderId.Value);

                    if (q.Wcid != null)
                        rows = rows.Where(t => t.Wcid == q.Wcid.Value);

                    if (q.Status != null)
                        rows = rows.Where(t => t.Status == q.Status.Value);

                    if (q.FromUtc != null)
                        rows = rows.Where(t => t.Timestamp >= q.FromUtc.Value);

                    if (q.ToUtc != null)
                        rows = rows.Where(t => t.Timestamp < q.ToUtc.Value);

                    return rows
                        .OrderByDescending(t => t.Timestamp)
                        .ThenByDescending(t => t.Id)
                        .Take(limit)
                        .ToList();
                }
            }
            catch (Exception ex)
            {
                log.Error($"[MARKET] FindMarketTransactions failed: {ex.GetFullMessage()}");
                return null;
            }
        }

        /// <summary>Listings matching <paramref name="query"/>, newest first. NULL means the read FAILED.</summary>
        public List<MarketListing> FindMarketListings(MarketListingQuery query)
        {
            var q = query ?? new MarketListingQuery();
            var limit = ClampMarketRows(q.Limit);

            try
            {
                using (var context = new ShardDbContext())
                {
                    context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                    var rows = context.MarketListing.AsQueryable();

                    if (q.SellerAccountId != null)
                        rows = rows.Where(l => l.SellerAccountId == q.SellerAccountId.Value);

                    if (q.Wcid != null)
                        rows = rows.Where(l => l.Wcid == q.Wcid.Value);

                    if (q.Status != null)
                        rows = rows.Where(l => l.Status == q.Status.Value);

                    if (q.FromUtc != null)
                        rows = rows.Where(l => l.CreatedAt >= q.FromUtc.Value);

                    if (q.ToUtc != null)
                        rows = rows.Where(l => l.CreatedAt < q.ToUtc.Value);

                    return rows
                        .OrderByDescending(l => l.CreatedAt)
                        .ThenByDescending(l => l.Id)
                        .Take(limit)
                        .ToList();
                }
            }
            catch (Exception ex)
            {
                log.Error($"[MARKET] FindMarketListings failed: {ex.GetFullMessage()}");
                return null;
            }
        }

        /// <summary>
        /// One listing by id, read from the DATABASE rather than the in-memory index, so a closed
        /// listing an investigator is asking about still resolves. NULL means the read failed OR the
        /// listing does not exist - the admin surface treats both as "cannot show it", which is the
        /// only honest reading when the two are indistinguishable at this depth.
        /// </summary>
        public MarketListing GetMarketListing(uint id)
        {
            try
            {
                using (var context = new ShardDbContext())
                {
                    context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                    return context.MarketListing.FirstOrDefault(l => l.Id == id);
                }
            }
            catch (Exception ex)
            {
                log.Error($"[MARKET] GetMarketListing failed for listing {id}: {ex.GetFullMessage()}");
                return null;
            }
        }

        /// <summary>
        /// Drops refused attempts stamped before <paramref name="olderThanUtc"/>. One ExecuteDelete,
        /// which is its own implicit transaction; returns the row count, or 0 if the delete failed.
        /// </summary>
        public int PruneMarketRejectedAttempts(DateTime olderThanUtc)
        {
            try
            {
                using (var context = new ShardDbContext())
                {
                    return context.MarketRejectedAttempt.Where(r => r.Timestamp < olderThanUtc).ExecuteDelete();
                }
            }
            catch (Exception ex)
            {
                log.Error($"[MARKET] PruneMarketRejectedAttempts failed for cutoff {olderThanUtc:u}: {ex.GetFullMessage()}");
                return 0;
            }
        }
    }
}
