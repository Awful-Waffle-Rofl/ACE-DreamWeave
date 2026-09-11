using System;
using System.Collections.Generic;

using ACE.Database;

// MarketRejectedAttempt has no same-named runtime DTO, so it is imported plainly; MarketListing and
// MarketTransaction do, and keep their aliases below.
using MarketRejectedAttempt = ACE.Database.Models.Shard.MarketRejectedAttempt;

using ShardMarketListing = ACE.Database.Models.Shard.MarketListing;
using ShardMarketTransaction = ACE.Database.Models.Shard.MarketTransaction;
using ShardMarketBuyOrder = ACE.Database.Models.Shard.MarketBuyOrder;

namespace ACE.Server.Managers.Market
{
    /// <summary>
    /// The persistence seam so MarketManager is unit-testable with no MySQL (mirrors IAccountVaultBackend).
    /// NULL FROM A READ MEANS THE READ FAILED, EMPTY means genuinely nothing - conflating them empties
    /// the index at boot. Traffics in the EF ROW types, aliased since the runtime DTOs share their names.
    /// </summary>
    public interface IMarketRepository
    {
        /// <summary>Every listing row, oldest first. NULL means the read FAILED.</summary>
        List<ShardMarketListing> GetAllListings();

        /// <summary>Inserts and writes the generated id back onto the row. False includes a UNIQUE refusal.</summary>
        bool AddListing(ShardMarketListing row);

        bool UpdateListing(ShardMarketListing row);

        /// <summary>
        /// Rewrites snapshot_Json on listings that are STILL ACTIVE, touching no other column.
        /// Reports PER ROW: a row that ran and matched nothing (Missed) means the listing closed
        /// mid-pass, while a row that threw (Failed) means it is probably still open and now stale
        /// on disk. Those two must never be collapsed into one number - see the result type.
        ///
        /// Separate from <see cref="UpdateListing"/> rather than expressed through it, because that
        /// one writes a WHOLE row from a copy the caller read earlier and would carry status, count
        /// and the two active_* keys backwards over a concurrent buy or delist.
        /// </summary>
        MarketListingSnapshotWriteResult UpdateListingSnapshots(IReadOnlyList<MarketListingSnapshotUpdate> rows);

        /// <summary>Inserts and writes the generated id back onto the row.</summary>
        bool AddTransaction(ShardMarketTransaction row);

        bool UpdateTransaction(ShardMarketTransaction row);

        /// <summary>Most recent transactions, newest first. NULL means the read FAILED.</summary>
        List<ShardMarketTransaction> GetTransactions(int limit);

        /// <summary>Pending rows stamped before <paramref name="olderThanUtc"/>. NULL means the read FAILED, and boot recovery must then be SKIPPED.</summary>
        List<ShardMarketTransaction> GetPendingTransactions(DateTime olderThanUtc);

        // ---- Wanted buy orders (WANTED-DESIGN 5.1) ----

        /// <summary>Every buy order row, oldest first. NULL means the read FAILED.</summary>
        List<ShardMarketBuyOrder> GetAllBuyOrders();

        /// <summary>Inserts and writes the generated id back onto the row. False includes the UNIQUE (account, active_Material) refusal.</summary>
        bool AddBuyOrder(ShardMarketBuyOrder row);

        bool UpdateBuyOrder(ShardMarketBuyOrder row);

        /// <summary>Pending order rows stamped before <paramref name="olderThanUtc"/>. NULL means the read FAILED, and recovery must then be SKIPPED.</summary>
        List<ShardMarketBuyOrder> GetPendingBuyOrders(DateTime olderThanUtc);

        /// <summary>
        /// What the COMPLETED fills of each order add up to (WANTED-DESIGN 6.5). NULL means the read
        /// FAILED and the boot reconciliation must then be SKIPPED; an EMPTY dictionary means none of
        /// these orders has ever been filled. An order MISSING from a non-null result is a real answer
        /// of zero, not an unknown.
        /// </summary>
        Dictionary<uint, MarketBuyOrderFillTotals> SumCompletedFillsByOrder(IReadOnlyList<uint> orderIds);

        // ---- investigation surface (DESIGN 5.5 and 8.1) ----

        /// <summary>
        /// Appends a batch of refused attempts. Called ONLY from the MarketRejectionLog writer
        /// thread, never from a request path. False means the batch was lost, which is logged and
        /// swallowed rather than surfaced: a logging outage must not become a market outage.
        /// </summary>
        bool AddRejectedAttempts(IReadOnlyList<MarketRejectedAttempt> rows);

        /// <summary>Refused attempts matching the filter, newest first. NULL means the read FAILED.</summary>
        List<MarketRejectedAttempt> FindRejectedAttempts(MarketRejectQuery query);

        /// <summary>Transactions matching the filter, newest first. NULL means the read FAILED.</summary>
        List<ShardMarketTransaction> FindTransactions(MarketTransactionQuery query);

        /// <summary>Listings matching the filter, newest first. NULL means the read FAILED.</summary>
        List<ShardMarketListing> FindListings(MarketListingQuery query);

        /// <summary>One listing by id, read from the DATABASE so closed listings resolve. NULL means missing OR unreadable.</summary>
        ShardMarketListing GetListingRow(uint id);

        /// <summary>Drops refused attempts stamped before <paramref name="olderThanUtc"/>; returns the row count.</summary>
        int PruneRejectedAttempts(DateTime olderThanUtc);
    }

    /// <summary>
    /// Production repository: straight delegation to the market DAO on DatabaseManager.Shard's base
    /// ShardDatabase. Listing and history operations, not biota operations, so deliberately NOT on
    /// SerializedShardDatabase's worker thread; serialization comes from MarketManager's index lock.
    /// </summary>
    public class ShardMarketRepository : IMarketRepository
    {
        private static ShardDatabase Db => DatabaseManager.Shard.BaseDatabase;

        public List<ShardMarketListing> GetAllListings() => Db.GetAllMarketListings();

        public bool AddListing(ShardMarketListing row) => Db.AddMarketListing(row);

        public bool UpdateListing(ShardMarketListing row) => Db.UpdateMarketListing(row);

        public MarketListingSnapshotWriteResult UpdateListingSnapshots(IReadOnlyList<MarketListingSnapshotUpdate> rows)
            => Db.UpdateMarketListingSnapshots(rows);

        public bool AddTransaction(ShardMarketTransaction row) => Db.AddMarketTransaction(row);

        public bool UpdateTransaction(ShardMarketTransaction row) => Db.UpdateMarketTransaction(row);

        public List<ShardMarketTransaction> GetTransactions(int limit) => Db.GetMarketTransactions(limit);

        public List<ShardMarketTransaction> GetPendingTransactions(DateTime olderThanUtc)
            => Db.GetPendingMarketTransactions(olderThanUtc);

        public List<ShardMarketBuyOrder> GetAllBuyOrders() => Db.GetAllMarketBuyOrders();
        public bool AddBuyOrder(ShardMarketBuyOrder row) => Db.AddMarketBuyOrder(row);
        public bool UpdateBuyOrder(ShardMarketBuyOrder row) => Db.UpdateMarketBuyOrder(row);
        public List<ShardMarketBuyOrder> GetPendingBuyOrders(DateTime olderThanUtc) => Db.GetPendingMarketBuyOrders(olderThanUtc);

        public Dictionary<uint, MarketBuyOrderFillTotals> SumCompletedFillsByOrder(IReadOnlyList<uint> orderIds)
            => Db.SumCompletedMarketFillsByOrder(orderIds);

        public bool AddRejectedAttempts(IReadOnlyList<MarketRejectedAttempt> rows)
            => Db.AddMarketRejectedAttempts(rows);

        public List<MarketRejectedAttempt> FindRejectedAttempts(MarketRejectQuery query)
            => Db.FindMarketRejectedAttempts(query);

        public List<ShardMarketTransaction> FindTransactions(MarketTransactionQuery query)
            => Db.FindMarketTransactions(query);

        public List<ShardMarketListing> FindListings(MarketListingQuery query)
            => Db.FindMarketListings(query);

        public ShardMarketListing GetListingRow(uint id) => Db.GetMarketListing(id);

        public int PruneRejectedAttempts(DateTime olderThanUtc)
            => Db.PruneMarketRejectedAttempts(olderThanUtc);
    }
}
