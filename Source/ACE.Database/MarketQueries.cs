using System;
using System.Collections.Generic;

namespace ACE.Database
{
    /// <summary>
    /// Filter objects for the market investigation reads (Docs/Market/DESIGN.md section 8.1).
    ///
    /// ONE OBJECT PER TABLE rather than a DAO method per filter combination: /marketadmin needs
    /// transactions by account, by listing and by wcid, listings by seller and by wcid, and rejected
    /// attempts by four different keys, and enumerating those as separate methods would be a dozen
    /// near-identical queries to keep in step with each other.
    ///
    /// EVERY FIELD IS NULLABLE AND NULL MEANS "NO FILTER". An all-null query is therefore "the whole
    /// table, newest first, clamped to the DAO ceiling" - which is why <see cref="Limit"/> exists and
    /// why the DAO clamps it rather than trusting it.
    ///
    /// These live in ACE.Database, not in ACE.Server.Managers.Market alongside the rest of the market
    /// types, because ACE.Database cannot reference ACE.Server: the DAO methods that take them are on
    /// ShardDatabase.
    /// </summary>
    public class MarketRejectQuery
    {
        /// <summary>The account whose attempts to return. Null means every account.</summary>
        public uint? AccountId { get; set; }

        public uint? CharacterGuid { get; set; }

        public uint? ListingId { get; set; }

        public uint? Wcid { get; set; }

        /// <summary>A MarketErrorCodes.ToCode string, or repository_refused. Matched exactly.</summary>
        public string ReasonCode { get; set; }

        /// <summary>Inclusive lower bound on the UTC timestamp.</summary>
        public DateTime? FromUtc { get; set; }

        /// <summary>Exclusive upper bound on the UTC timestamp.</summary>
        public DateTime? ToUtc { get; set; }

        /// <summary>Rows to return, newest first. Clamped by the DAO; null takes the DAO default.</summary>
        public int? Limit { get; set; }
    }

    /// <summary>Filter over market_transaction. See <see cref="MarketRejectQuery"/> for the shared contract.</summary>
    public class MarketTransactionQuery
    {
        /// <summary>
        /// Matches rows where this account is the buyer OR the seller. That is the shape a dispute
        /// takes ("what did this player do on the market"), and neither side alone answers it.
        /// </summary>
        public uint? EitherSideAccountId { get; set; }

        public uint? ListingId { get; set; }

        /// <summary>
        /// Matches the fills of ONE Wanted buy order. Reads market_transaction_buy_order_idx, which
        /// exists for exactly this question and had no filter able to ask it until now: an ordinary
        /// sale carries a NULL buy_Order_Id, so this never widens a listing query by accident.
        /// </summary>
        public uint? BuyOrderId { get; set; }

        public uint? Wcid { get; set; }

        /// <summary>A MarketTransactionStatus value. Null means every status.</summary>
        public int? Status { get; set; }

        public DateTime? FromUtc { get; set; }

        public DateTime? ToUtc { get; set; }

        public int? Limit { get; set; }
    }

    /// <summary>
    /// What the Completed fills of one Wanted buy order add up to: bags delivered and escrow paid out.
    ///
    /// The boot reconciliation (WANTED-DESIGN 6.5) compares these against the order's own
    /// count_Remaining and escrow_Mmd, because a fill's transaction row completes the instant custody
    /// changes while the order's decrement is a separate write after the seller's credit - so a process
    /// death between them leaves a Completed fill over an order still reading its pre-fill numbers.
    /// </summary>
    public readonly struct MarketBuyOrderFillTotals
    {
        public MarketBuyOrderFillTotals(int count, long totalMmd)
        {
            Count = count;
            TotalMmd = totalMmd;
        }

        /// <summary>Bags delivered by every Completed fill of the order.</summary>
        public int Count { get; }

        /// <summary>MMD those fills paid out of the order's escrow.</summary>
        public long TotalMmd { get; }
    }

    /// <summary>Filter over market_listing. See <see cref="MarketRejectQuery"/> for the shared contract.</summary>
    public class MarketListingQuery
    {
        public uint? SellerAccountId { get; set; }

        public uint? Wcid { get; set; }

        /// <summary>A MarketListingStatus value. Null means every status, closed listings included.</summary>
        public int? Status { get; set; }

        /// <summary>Inclusive lower bound on created_At, UTC.</summary>
        public DateTime? FromUtc { get; set; }

        /// <summary>Exclusive upper bound on created_At, UTC.</summary>
        public DateTime? ToUtc { get; set; }

        public int? Limit { get; set; }
    }

    /// <summary>
    /// One row of <see cref="ShardDatabase.UpdateMarketListingSnapshots"/>'s input: rewrite THIS
    /// listing's snapshot_Json and nothing else.
    ///
    /// A whole-row update is deliberately not available here. The snapshot backfill re-projects a
    /// listing that is still open, so status, count, active_Item_Guid and closed_At may all be
    /// changed by a concurrent buy or delist between the read and the write; writing a whole row
    /// built from the pre-read copy would revert them. Naming the ONE column keeps the backfill
    /// unable to undo a sale even when it loses that race.
    ///
    /// Lives in ACE.Database for the same reason the query objects above do - ACE.Database cannot
    /// reference ACE.Server, and the DAO method that takes this is on ShardDatabase.
    /// </summary>
    public class MarketListingSnapshotUpdate
    {
        public uint ListingId { get; set; }

        /// <summary>The serialized ListingSnapshot to store. Never null - snapshot_Json is NOT NULL.</summary>
        public string SnapshotJson { get; set; }
    }

    /// <summary>
    /// What one <see cref="ShardDatabase.UpdateMarketListingSnapshots"/> batch did, PER ROW.
    ///
    /// A plain row count is not enough here, and the difference is the whole reason this type
    /// exists. "The statement ran and matched nothing" means the listing closed between its
    /// projection and this write - benign, expected, and self-correcting. "The statement THREW"
    /// means the row is still open and its snapshot is now permanently out of step with the copy
    /// the caller has already put in memory. Folding the second into the first (which a bare
    /// `rows - affected` subtraction does) hides a failing write behind a counter an operator reads
    /// as normal churn, and re-running the pass forever would never surface it.
    ///
    /// <see cref="Updated"/> + <see cref="Missed"/> + <see cref="Failed"/> must equal the number of
    /// rows submitted whenever <see cref="BatchFailed"/> is false; a caller that finds otherwise
    /// must attribute the difference to failure, never to a close.
    /// </summary>
    public class MarketListingSnapshotWriteResult
    {
        /// <summary>The batch could not run at all. The three counters below are then meaningless.</summary>
        public bool BatchFailed { get; set; }

        /// <summary>Rows the statement actually changed.</summary>
        public int Updated { get; set; }

        /// <summary>Rows whose statement RAN and matched nothing: the listing is no longer Active.</summary>
        public int Missed { get; set; }

        /// <summary>Rows whose statement THREW, or that were malformed. Never a concurrent close.</summary>
        public int Failed { get; set; }

        /// <summary>
        /// WHICH listings failed, so the caller can retry exactly those and nothing else.
        ///
        /// A count alone is not enough: the caller writes its in-memory copy BEFORE this call (so a
        /// concurrent close carries the new snapshot with it), which means a row that failed here
        /// re-projects to something byte-identical to memory on the next pass and would be dismissed
        /// as unchanged. Without the ids there is no way to tell that row apart from one that is
        /// genuinely up to date, and it would stay stale in the database forever.
        ///
        /// Shorter than <see cref="Failed"/> when a submitted row was too malformed to name.
        /// </summary>
        public List<uint> FailedListingIds { get; set; } = new List<uint>();
    }
}
