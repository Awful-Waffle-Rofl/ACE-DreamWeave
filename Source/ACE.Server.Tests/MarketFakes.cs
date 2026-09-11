using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using ACE.Database;
using ACE.Server.Entity.AccountVault;
using ACE.Server.Managers.Market;
using ACE.Server.WorldObjects;

using MarketRejectedAttempt = ACE.Database.Models.Shard.MarketRejectedAttempt;

using ShardMarketListing = ACE.Database.Models.Shard.MarketListing;
using ShardMarketTransaction = ACE.Database.Models.Shard.MarketTransaction;
using ShardMarketBuyOrder = ACE.Database.Models.Shard.MarketBuyOrder;

namespace ACE.Server.Tests
{
    /// <summary>
    /// In-memory stand-in for market_listing and market_transaction. Models the two behaviours the
    /// manager rests on: a failed read returns null while an empty one returns an empty list, and
    /// AddListing refuses a second Active listing over the same item or (account, wcid) ledger row.
    /// </summary>
    internal class FakeMarketRepository : IMarketRepository
    {
        public readonly List<ShardMarketListing> Listings = new List<ShardMarketListing>();
        public readonly List<ShardMarketTransaction> Transactions = new List<ShardMarketTransaction>();

        /// <summary>The rejected-attempt audit table, in memory. Written only by the drain, never by Record.</summary>
        public readonly List<MarketRejectedAttempt> RejectedAttempts = new List<MarketRejectedAttempt>();

        public readonly List<ShardMarketBuyOrder> BuyOrders = new List<ShardMarketBuyOrder>();
        public bool FailBuyOrderRead;
        public bool FailPendingOrderRead;
        public bool FailAddBuyOrder;
        public bool FailUpdateBuyOrder;
        public int AddBuyOrderCalls;
        public int UpdateBuyOrderCalls;
        private uint nextBuyOrderId = 1;

        /// <summary>
        /// When set, the FIRST AddBuyOrder call BLOCKS until the test releases this event, which parks
        /// one placement inside its insert round trip - past the uniqueness check and short of the
        /// index insert - so a second placement can be driven through that exact window.
        ///
        /// Never a timed sleep, for the reason FakeMarketItemStore.StoreHold records: a sleep is a race
        /// by construction and a starved thread pool can finish the "slow" call before the probe lands.
        /// Only the first call waits, so the second placement is not parked behind the first and cannot
        /// deadlock the pair.
        /// </summary>
        public System.Threading.ManualResetEventSlim AddBuyOrderHold
        {
            get => addBuyOrderHold;
            set
            {
                addBuyOrderHold = value;
                addBuyOrderHoldsUsed = 0;
                AddBuyOrderEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        private System.Threading.ManualResetEventSlim addBuyOrderHold;

        private int addBuyOrderHoldsUsed;

        /// <summary>Completes the instant the held AddBuyOrder call begins waiting, so a test can rendezvous with the window rather than guess at it.</summary>
        public TaskCompletionSource<bool> AddBuyOrderEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool FailListingRead;
        public bool FailPendingRead;
        public bool FailAddListing;
        public bool FailUpdateListing;

        /// <summary>
        /// The whole snapshot-backfill batch fails OUTRIGHT, as opposed to running and matching no
        /// rows. Kept apart from <see cref="FailUpdateListing"/> because the two methods have
        /// different contracts - one returns a bool, the other a per-row result.
        /// </summary>
        public bool FailUpdateListingSnapshots;

        /// <summary>
        /// The batch THROWS out of the call instead of reporting a failure. Distinct from
        /// <see cref="FailUpdateListingSnapshots"/>, which models a DAO that caught its own error and
        /// answered honestly: this models one that did not, which is what an unattended caller has to
        /// survive.
        /// </summary>
        public bool ThrowFromUpdateListingSnapshots;

        /// <summary>
        /// Listing ids whose snapshot write THROWS inside an otherwise healthy batch, exactly as
        /// ShardDatabase's per-row catch produces. The listing stays Active and stays un-rewritten,
        /// which is the case that must NOT be reported as a concurrent close - so a test can prove
        /// the two are told apart rather than one derived from the other by subtraction.
        /// </summary>
        public readonly HashSet<uint> ThrowOnSnapshotWriteListingIds = new HashSet<uint>();

        /// <summary>
        /// Listing ids the fake accepts and then reports on in NO counter at all - not Updated, not
        /// Missed, not Failed - so the result does not account for every row it was handed.
        ///
        /// Unreachable through the real DAO today, where every row increments exactly one counter.
        /// It exists so the caller's "the repository under-reported" branch is exercised rather than
        /// merely written: an unaccounted row is one whose outcome nobody knows, which deserves at
        /// least the treatment a known failure gets.
        /// </summary>
        public readonly HashSet<uint> UnderReportSnapshotWriteListingIds = new HashSet<uint>();
        public bool FailAddTransaction;
        public bool FailUpdateTransaction;

        /// <summary>
        /// The investigation reads' failure switches. Kept one per read rather than one shared flag so
        /// a test can prove that a null from ONE read is not mistaken for "no results" by a caller that
        /// runs several.
        /// </summary>
        public bool FailRejectRead;
        public bool FailTransactionQueryRead;
        public bool FailListingQueryRead;
        public bool FailListingRowRead;
        public bool FailAddRejectedAttempts;

        public int AddListingCalls;
        public int UpdateListingCalls;

        /// <summary>Times the snapshot backfill reached the repository at all. Zero proves a pass wrote nothing.</summary>
        public int UpdateListingSnapshotsCalls;

        /// <summary>Every row the backfill submitted, in order, whether or not it matched an Active listing.</summary>
        public readonly List<MarketListingSnapshotUpdate> SnapshotWrites = new List<MarketListingSnapshotUpdate>();

        public int AddTransactionCalls;
        public int UpdateTransactionCalls;
        public int AddRejectedAttemptsCalls;
        public int PruneRejectedAttemptsCalls;

        private uint nextRejectId = 1;

        private uint nextListingId = 1;
        private uint nextTransactionId = 1;

        public List<ShardMarketListing> GetAllListings()
        {
            if (FailListingRead)
                return null;

            return Listings.OrderBy(l => l.Id).Select(Clone).ToList();
        }

        public bool AddListing(ShardMarketListing row)
        {
            AddListingCalls++;

            if (FailAddListing || row == null)
                return false;

            // The two UNIQUE keys, modelled. MySQL treats each NULL as distinct, so only non-null values collide.
            if (row.ActiveItemGuid != null && Listings.Any(l => l.ActiveItemGuid == row.ActiveItemGuid))
                return false;

            if (row.ActiveLedgerWcid != null &&
                Listings.Any(l => l.SellerAccountId == row.SellerAccountId && l.ActiveLedgerWcid == row.ActiveLedgerWcid))
                return false;

            if (row.CreatedAt == default)
                row.CreatedAt = DateTime.UtcNow;

            row.Id = nextListingId++;
            Listings.Add(Clone(row));
            return true;
        }

        public bool UpdateListing(ShardMarketListing row)
        {
            UpdateListingCalls++;

            if (FailUpdateListing || row == null || row.Id == 0)
                return false;

            var index = Listings.FindIndex(l => l.Id == row.Id);

            if (index < 0)
                return false;

            Listings[index] = Clone(row);
            return true;
        }

        /// <summary>
        /// Models ShardDatabase.UpdateMarketListingSnapshots: it rewrites ONLY snapshot_Json, and
        /// only on rows whose status is still 0 (Active). The status predicate is modelled rather
        /// than assumed, because "the row closed between the projection and the write" is the case
        /// the caller's closed_mid_pass counter exists for, and a fake that ignored status could not
        /// produce it.
        /// </summary>
        public MarketListingSnapshotWriteResult UpdateListingSnapshots(IReadOnlyList<MarketListingSnapshotUpdate> rows)
        {
            UpdateListingSnapshotsCalls++;

            if (ThrowFromUpdateListingSnapshots)
                throw new InvalidOperationException("the shard is unreachable");

            // BatchFailed, matching the DAO: the batch did not run at all, as opposed to running and
            // matching nothing. Conflating the two is what would let a shard outage read as "every
            // listing closed while I was looking at it".
            if (FailUpdateListingSnapshots)
                return new MarketListingSnapshotWriteResult { BatchFailed = true };

            var result = new MarketListingSnapshotWriteResult();

            if (rows == null)
                return result;

            foreach (var row in rows)
            {
                if (row == null)
                {
                    result.Failed++;
                    continue;
                }

                SnapshotWrites.Add(row);

                // The DAO's per-row catch, modelled: this row's statement threw, the rest of the
                // batch carries on, and the listing is very likely STILL ACTIVE. It must never be
                // reported the same way as a row that ran and matched nothing.
                if (ThrowOnSnapshotWriteListingIds.Contains(row.ListingId))
                {
                    result.Failed++;
                    result.FailedListingIds.Add(row.ListingId);
                    continue;
                }

                // Swallowed silently: no update, and no counter. The caller sees a result whose
                // three counters do not add up to what it submitted.
                if (UnderReportSnapshotWriteListingIds.Contains(row.ListingId))
                    continue;

                // The `status = 0` predicate, modelled rather than assumed, because "the row closed
                // between the projection and the write" is the case the caller's closed_mid_pass
                // counter exists for and a fake that ignored status could not produce it.
                var index = Listings.FindIndex(l => l.Id == row.ListingId && l.Status == 0);

                if (index < 0)
                {
                    result.Missed++;
                    continue;
                }

                Listings[index].SnapshotJson = row.SnapshotJson;
                result.Updated++;
            }

            return result;
        }

        public bool AddTransaction(ShardMarketTransaction row)
        {
            AddTransactionCalls++;

            if (FailAddTransaction || row == null)
                return false;

            if (row.Timestamp == default)
                row.Timestamp = DateTime.UtcNow;

            row.Id = nextTransactionId++;
            Transactions.Add(Clone(row));
            return true;
        }

        public bool UpdateTransaction(ShardMarketTransaction row)
        {
            UpdateTransactionCalls++;

            if (FailUpdateTransaction || row == null || row.Id == 0)
                return false;

            var index = Transactions.FindIndex(t => t.Id == row.Id);

            if (index < 0)
                return false;

            Transactions[index] = Clone(row);
            return true;
        }

        public List<ShardMarketTransaction> GetTransactions(int limit)
        {
            return Transactions.OrderByDescending(t => t.Id).Take(Math.Max(1, limit)).Select(Clone).ToList();
        }

        public List<ShardMarketTransaction> GetPendingTransactions(DateTime olderThanUtc)
        {
            if (FailPendingRead)
                return null;

            return Transactions.Where(t => t.Status == 0 && t.Timestamp < olderThanUtc)
                               .OrderBy(t => t.Id)
                               .Select(Clone)
                               .ToList();
        }

        // ---- investigation surface ----

        public bool AddRejectedAttempts(IReadOnlyList<MarketRejectedAttempt> rows)
        {
            AddRejectedAttemptsCalls++;

            if (FailAddRejectedAttempts)
                return false;

            if (rows == null)
                return true;

            foreach (var row in rows)
            {
                if (row == null)
                    continue;

                if (row.Timestamp == default)
                    row.Timestamp = DateTime.UtcNow;

                row.Id = nextRejectId++;
                RejectedAttempts.Add(Clone(row));
            }

            return true;
        }

        public List<MarketRejectedAttempt> FindRejectedAttempts(MarketRejectQuery query)
        {
            if (FailRejectRead)
                return null;

            var q = query ?? new MarketRejectQuery();

            return RejectedAttempts
                .Where(r => q.AccountId == null || r.AccountId == q.AccountId.Value)
                .Where(r => q.CharacterGuid == null || r.CharacterGuid == q.CharacterGuid.Value)
                .Where(r => q.ListingId == null || r.ListingId == q.ListingId.Value)
                .Where(r => q.Wcid == null || r.Wcid == q.Wcid.Value)
                .Where(r => string.IsNullOrEmpty(q.ReasonCode) || r.ReasonCode == q.ReasonCode)
                .Where(r => q.FromUtc == null || r.Timestamp >= q.FromUtc.Value)
                .Where(r => q.ToUtc == null || r.Timestamp < q.ToUtc.Value)
                .OrderByDescending(r => r.Timestamp)
                .ThenByDescending(r => r.Id)
                .Take(Clamp(q.Limit))
                .Select(Clone)
                .ToList();
        }

        public List<ShardMarketTransaction> FindTransactions(MarketTransactionQuery query)
        {
            if (FailTransactionQueryRead)
                return null;

            var q = query ?? new MarketTransactionQuery();

            return Transactions
                .Where(t => q.EitherSideAccountId == null
                            || t.BuyerAccountId == q.EitherSideAccountId.Value
                            || t.SellerAccountId == q.EitherSideAccountId.Value)
                .Where(t => q.ListingId == null || t.ListingId == q.ListingId.Value)
                .Where(t => q.BuyOrderId == null || t.BuyOrderId == q.BuyOrderId.Value)
                .Where(t => q.Wcid == null || t.Wcid == q.Wcid.Value)
                .Where(t => q.Status == null || t.Status == q.Status.Value)
                .Where(t => q.FromUtc == null || t.Timestamp >= q.FromUtc.Value)
                .Where(t => q.ToUtc == null || t.Timestamp < q.ToUtc.Value)
                .OrderByDescending(t => t.Timestamp)
                .ThenByDescending(t => t.Id)
                .Take(Clamp(q.Limit))
                .Select(Clone)
                .ToList();
        }

        public List<ShardMarketListing> FindListings(MarketListingQuery query)
        {
            if (FailListingQueryRead)
                return null;

            var q = query ?? new MarketListingQuery();

            return Listings
                .Where(l => q.SellerAccountId == null || l.SellerAccountId == q.SellerAccountId.Value)
                .Where(l => q.Wcid == null || l.Wcid == q.Wcid.Value)
                .Where(l => q.Status == null || l.Status == q.Status.Value)
                .Where(l => q.FromUtc == null || l.CreatedAt >= q.FromUtc.Value)
                .Where(l => q.ToUtc == null || l.CreatedAt < q.ToUtc.Value)
                .OrderByDescending(l => l.CreatedAt)
                .ThenByDescending(l => l.Id)
                .Take(Clamp(q.Limit))
                .Select(Clone)
                .ToList();
        }

        public ShardMarketListing GetListingRow(uint id)
        {
            if (FailListingRowRead)
                return null;

            var row = Listings.FirstOrDefault(l => l.Id == id);

            return row == null ? null : Clone(row);
        }

        public int PruneRejectedAttempts(DateTime olderThanUtc)
        {
            PruneRejectedAttemptsCalls++;

            return RejectedAttempts.RemoveAll(r => r.Timestamp < olderThanUtc);
        }

        public List<ShardMarketBuyOrder> GetAllBuyOrders()
            => FailBuyOrderRead ? null : BuyOrders.OrderBy(o => o.Id).Select(Clone).ToList();

        public bool AddBuyOrder(ShardMarketBuyOrder row)
        {
            AddBuyOrderCalls++;

            var hold = addBuyOrderHold;

            if (hold != null && System.Threading.Interlocked.Increment(ref addBuyOrderHoldsUsed) == 1)
            {
                AddBuyOrderEntered.TrySetResult(true);

                // Bounded, so a failed assertion cannot wedge the whole test run.
                hold.Wait(TimeSpan.FromSeconds(10));
            }

            if (FailAddBuyOrder || row == null) return false;

            // The UNIQUE (buyer_Account_Id, active_Material, order_Kind) key, modelled: NULL never
            // collides, and a BAG order never collides with a HAMMER order of the same material.
            if (row.ActiveMaterial != null && BuyOrders.Any(o => o.BuyerAccountId == row.BuyerAccountId && o.ActiveMaterial == row.ActiveMaterial && o.OrderKind == row.OrderKind))
                return false;

            if (row.CreatedAt == default) row.CreatedAt = DateTime.UtcNow;
            row.Id = nextBuyOrderId++;
            BuyOrders.Add(Clone(row));
            return true;
        }

        public bool UpdateBuyOrder(ShardMarketBuyOrder row)
        {
            UpdateBuyOrderCalls++;
            if (FailUpdateBuyOrder || row == null || row.Id == 0) return false;

            var index = BuyOrders.FindIndex(o => o.Id == row.Id);
            if (index < 0) return false;

            // The UNIQUE key again, on the way to Active.
            if (row.ActiveMaterial != null && BuyOrders.Any(o => o.Id != row.Id && o.BuyerAccountId == row.BuyerAccountId && o.ActiveMaterial == row.ActiveMaterial && o.OrderKind == row.OrderKind))
                return false;

            BuyOrders[index] = Clone(row);
            return true;
        }

        public List<ShardMarketBuyOrder> GetPendingBuyOrders(DateTime olderThanUtc)
            => FailPendingOrderRead ? null : BuyOrders.Where(o => o.Status == 0 && o.CreatedAt < olderThanUtc).OrderBy(o => o.Id).Select(Clone).ToList();

        /// <summary>The boot reconciliation's read failing, as opposed to finding no fills. Kept apart from every other read switch for the reason the class doc gives.</summary>
        public bool FailFillTotalsRead;

        public Dictionary<uint, MarketBuyOrderFillTotals> SumCompletedFillsByOrder(IReadOnlyList<uint> orderIds)
        {
            if (FailFillTotalsRead)
                return null;

            if (orderIds == null || orderIds.Count == 0)
                return new Dictionary<uint, MarketBuyOrderFillTotals>();

            var ids = new HashSet<uint>(orderIds);

            return Transactions
                .Where(t => t.BuyOrderId != null && t.Status == (int)MarketTransactionStatus.Completed && ids.Contains(t.BuyOrderId.Value))
                .GroupBy(t => t.BuyOrderId.Value)
                .ToDictionary(g => g.Key, g => new MarketBuyOrderFillTotals(g.Sum(t => t.Count), g.Sum(t => t.PriceMmdTotal)));
        }

        private static ShardMarketBuyOrder Clone(ShardMarketBuyOrder o) => new ShardMarketBuyOrder
        {
            Id = o.Id, BuyerAccountId = o.BuyerAccountId, BuyerCharacterGuid = o.BuyerCharacterGuid, BuyerCharacterName = o.BuyerCharacterName,
            MaterialType = o.MaterialType, OrderKind = o.OrderKind, Wcid = o.Wcid, PriceMmd = o.PriceMmd, CountTotal = o.CountTotal, CountRemaining = o.CountRemaining,
            EscrowMmd = o.EscrowMmd, Status = o.Status, ActiveMaterial = o.ActiveMaterial, CreatedAt = o.CreatedAt, ExpiresAt = o.ExpiresAt, ClosedAt = o.ClosedAt,
        };

        /// <summary>Mirrors ShardDatabase.ClampMarketRows: null takes the default, out of range clamps.</summary>
        private static int Clamp(int? limit)
        {
            var value = limit ?? ShardDatabase.DefaultMarketQueryRows;

            if (value < 1)
                return 1;

            return value > ShardDatabase.MaxMarketTransactionRows ? ShardDatabase.MaxMarketTransactionRows : value;
        }

        private static MarketRejectedAttempt Clone(MarketRejectedAttempt r) => new MarketRejectedAttempt
        {
            Id = r.Id,
            Operation = r.Operation,
            ReasonCode = r.ReasonCode,
            AccountId = r.AccountId,
            CharacterGuid = r.CharacterGuid,
            CharacterName = r.CharacterName,
            ListingId = r.ListingId,
            ItemGuid = r.ItemGuid,
            Wcid = r.Wcid,
            Count = r.Count,
            PriceMmd = r.PriceMmd,
            Channel = r.Channel,
            Timestamp = r.Timestamp,
            Detail = r.Detail,
        };

        /// <summary>Every read hands back a COPY, so the manager's index is provably built from what it read rather than aliasing it.</summary>
        private static ShardMarketListing Clone(ShardMarketListing r) => new ShardMarketListing
        {
            Id = r.Id,
            SellerAccountId = r.SellerAccountId,
            SellerCharacterGuid = r.SellerCharacterGuid,
            SellerCharacterName = r.SellerCharacterName,
            ItemGuid = r.ItemGuid,
            ActiveItemGuid = r.ActiveItemGuid,
            ActiveLedgerWcid = r.ActiveLedgerWcid,
            Wcid = r.Wcid,
            Count = r.Count,
            PriceMmd = r.PriceMmd,
            Status = r.Status,
            CreatedAt = r.CreatedAt,
            ClosedAt = r.ClosedAt,
            SnapshotJson = r.SnapshotJson,
        };

        private static ShardMarketTransaction Clone(ShardMarketTransaction r) => new ShardMarketTransaction
        {
            Id = r.Id,
            ListingId = r.ListingId,
            BuyOrderId = r.BuyOrderId,
            BuyerAccountId = r.BuyerAccountId,
            BuyerCharacterGuid = r.BuyerCharacterGuid,
            BuyerCharacterName = r.BuyerCharacterName,
            SellerAccountId = r.SellerAccountId,
            SellerCharacterGuid = r.SellerCharacterGuid,
            SellerCharacterName = r.SellerCharacterName,
            Wcid = r.Wcid,
            ItemName = r.ItemName,
            Count = r.Count,
            PriceMmdTotal = r.PriceMmdTotal,
            Timestamp = r.Timestamp,
            Channel = r.Channel,
            Status = r.Status,
        };
    }

    /// <summary>
    /// In-memory item store: a stored item is a real Stackable from FakeVaultWorld.MakeStack, a ledger
    /// row is a count against a wcid. RunSerialized records nesting depth so a test can assert the
    /// purchase path never holds two accounts' regions at once.
    /// </summary>
    internal class FakeMarketItemStore : IMarketItemStore
    {
        public readonly Dictionary<uint, List<WorldObject>> Items = new Dictionary<uint, List<WorldObject>>();
        public readonly Dictionary<(uint account, uint wcid), long> Ledger = new Dictionary<(uint, uint), long>();

        /// <summary>
        /// GROUP rows, per account: each inner list is one panel line standing for several separate
        /// WHOLE biotas (VaultEntry.ForGroup). Held apart from <see cref="Items"/> and seeded
        /// explicitly rather than derived, because the rule that decides which stored items group -
        /// VaultCollapse.AreGroupable over a bucket key - lives in AccountVaultStore and is covered by
        /// AccountVaultStoreTests against the real one. Re-deriving it here would test the copy.
        ///
        /// The ORDER of an inner list is the contract: index 0 is the representative, whose guid the
        /// row carries and a listing pins itself to, and a market sale takes members from the BACK.
        /// </summary>
        public readonly Dictionary<uint, List<List<WorldObject>>> Groups = new Dictionary<uint, List<List<WorldObject>>>();

        public readonly HashSet<uint> NotReadyAccounts = new HashSet<uint>();
        public readonly HashSet<uint> RefuseSerializedAccounts = new HashSet<uint>();
        public readonly HashSet<uint> FailTakeAccounts = new HashSet<uint>();
        public readonly HashSet<uint> FailGiveAccounts = new HashSet<uint>();
        public readonly HashSet<uint> FailReturnAccounts = new HashSet<uint>();

        /// <summary>
        /// Models the buyer's vault refusing a deposit because it is full (VaultMarketItemStore
        /// classifies AccountVaultStore.TryDeposit's cap refusal into MarketError.VaultFull rather than
        /// VaultUnavailable). Distinct from FailGiveAccounts, which stands in for every other deposit
        /// failure.
        /// </summary>
        public readonly HashSet<uint> FullVaultAccounts = new HashSet<uint>();

        /// <summary>
        /// Models the buyer's vault reporting full at the CanReceive preflight (step 0, before the
        /// Pending row), distinct from <see cref="FullVaultAccounts"/> which only bites at the real
        /// give (step 4) - keeping the two apart lets one test cover the preflight short-circuit and
        /// another keep covering the give-time unwind.
        /// </summary>
        public readonly HashSet<uint> PreflightFullVaultAccounts = new HashSet<uint>();

        public readonly HashSet<uint> FailTakeMatchingAccounts = new HashSet<uint>();

        /// <summary>
        /// The Nth withdraw inside TryTakeMatching THROWS (1-based; 0 never throws). Distinct from
        /// <see cref="FailTakeMatchingAccounts"/>, which models a store that caught its own error and
        /// answered honestly: this models one that did not, MID-LOOP, with items already out of the
        /// seller's vault. That state exists only in TryTakeMatching - every other market take does
        /// exactly one withdraw - and it is what the real store's own unwind catch exists for.
        /// </summary>
        public int ThrowOnTakeMatchingWithdraw;

        /// <summary>
        /// TryReturnToSeller THROWS. The real AccountVaultStore.TryReturnWithdrawn does
        /// (Player_Facets.TryReturnWithdrawnToVault at Player_Facets.cs:909 wraps it for exactly that
        /// reason), and TryReturnToSeller calls it without a catch of its own.
        ///
        /// Set alongside <see cref="ThrowOnTakeMatchingWithdraw"/> this models the CORRELATED failure,
        /// which is the most likely shape TryTakeMatching's unwind will ever see: the trigger is a
        /// withdraw throwing on a shard write error, and the unwind is a shard write to the same store,
        /// in the same region, microseconds later.
        /// </summary>
        public bool ThrowOnReturnToSeller;

        public int TakeCalls;
        public int GiveCalls;
        public int ReturnCalls;

        /// <summary>Deepest simultaneous RunSerialized nesting seen. Must never exceed 1 for two DIFFERENT accounts.</summary>
        public int MaxNestedAccounts;

        /// <summary>
        /// Deepest RunSerialized nesting on ONE THREAD, whatever the accounts. Must never exceed 1.
        ///
        /// Held apart from <see cref="MaxNestedAccounts"/> because that one counts DISTINCT accounts,
        /// so a region opened on the seller inside a region already open on the seller reads as depth
        /// 1 and hides itself. That is exactly the shape TryTakeMatching's unwind would take if
        /// TryReturnToSeller ever became a region opener, which is the regression this counter exists
        /// to catch. Written under `gate` because the concurrency tests drive two threads at one store.
        /// </summary>
        public int MaxRegionDepth;

        [ThreadStatic]
        private static int regionDepth;

        /// <summary>Times a serialized region's work() threw. Zero is the store contract: nothing inside a region may escape it.</summary>
        public int SerializedRegionThrows;

        /// <summary>Milliseconds slept on every store touch, so a test can outrun the request timeout.</summary>
        public int StoreDelayMs
        {
            get => storeDelayMs;
            set
            {
                storeDelayMs = value;

                // Reset on every assignment so a stale signal from a previous StoreDelayMs setting
                // (or a previous test run) can never be mistaken for this one having started.
                DelayEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        private int storeDelayMs;

        /// <summary>
        /// When set, every store touch BLOCKS until the test releases this event, instead of sleeping
        /// for StoreDelayMs. Takes precedence over StoreDelayMs. A timed sleep is a race by
        /// construction: on a starved CI thread pool the test's own continuation after
        /// <see cref="DelayEntered"/> can take longer than the sleep (thread injection runs at about
        /// one thread per half second), so the "slow" request finished before the probe was even
        /// sent (expected TooManyRequests, actual OK, 2026-09-02). A hold has no window to miss.
        /// The wait is bounded so a failed assertion cannot wedge the Kestrel host for the rest of
        /// the test run.
        /// </summary>
        public System.Threading.ManualResetEventSlim StoreHold
        {
            get => storeHold;
            set
            {
                storeHold = value;
                DelayEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        private System.Threading.ManualResetEventSlim storeHold;

        /// <summary>
        /// Completes the instant this store begins its delay (StoreDelayMs sleep or StoreHold wait),
        /// so a test can await a real rendezvous with "the delayed call has started" instead of
        /// guessing with Task.Delay.
        /// </summary>
        public TaskCompletionSource<bool> DelayEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly object gate = new object();
        private readonly List<uint> openAccounts = new List<uint>();

        public void SeedItem(uint accountId, WorldObject item)
        {
            if (!Items.TryGetValue(accountId, out var list))
                Items[accountId] = list = new List<WorldObject>();

            list.Add(item);
        }

        /// <summary>
        /// How this fake builds ONE unit of a collapsed ledger wcid, standing in for the real store's
        /// IAccountVaultWorldSource.CreateNewWorldObject. A ledger row has no biota, so a predicate can
        /// only be applied to a fresh instance of the wcid - which is sound because collapse fires only
        /// on an item provably identical to one.
        ///
        /// A wcid with no factory here models a wcid the world can no longer instantiate: the real
        /// store counts and takes nothing for it, and so does this.
        /// </summary>
        public readonly Dictionary<uint, Func<WorldObject>> LedgerUnitFactories = new Dictionary<uint, Func<WorldObject>>();

        /// <summary>
        /// <paramref name="unitFactory"/> is what makes a row PARTICIPATE in matching. Left null the
        /// row still shows in GetEntries and still backs a ledger listing, which is every pre-existing
        /// caller's use of this method.
        /// </summary>
        public void SeedLedger(uint accountId, uint wcid, long count, Func<WorldObject> unitFactory = null)
        {
            Ledger[(accountId, wcid)] = count;

            if (unitFactory != null)
                LedgerUnitFactories[wcid] = unitFactory;
        }

        /// <summary>
        /// Mirrors VaultMarketItemStore.LedgerRowMatches, INCLUDING the non-stackable clause: one unit
        /// must be one object, or a count the take cannot deliver would be reported. A fake more
        /// permissive than the store it stands in for is the one thing these fakes must never be.
        /// </summary>
        private bool LedgerUnitMatches(uint wcid, Func<WorldObject, bool> predicate)
        {
            if (!LedgerUnitFactories.TryGetValue(wcid, out var factory))
                return false;

            var probe = factory();

            return probe != null && (probe.MaxStackSize ?? 1) <= 1 && predicate(probe);
        }

        /// <summary>
        /// One group row of <paramref name="members"/> equivalent whole biotas of <paramref name="wcid"/>,
        /// in representative-first order. Returns the live member list so a test can assert on what is
        /// left after a partial sale.
        /// </summary>
        public List<WorldObject> SeedGroup(uint accountId, uint wcid, int members, int maxStackSize = 1)
        {
            var group = new List<WorldObject>();

            for (var i = 0; i < members; i++)
                group.Add(FakeVaultWorld.MakeStack(wcid, 1, maxStackSize));

            if (!Groups.TryGetValue(accountId, out var list))
                Groups[accountId] = list = new List<List<WorldObject>>();

            list.Add(group);
            return group;
        }

        /// <summary>A group row of pre-built members (representative first). For salvage-shaped tests that need MaterialType and Structure set.</summary>
        public List<WorldObject> SeedGroupOf(uint accountId, List<WorldObject> members)
        {
            if (!Groups.TryGetValue(accountId, out var list))
                Groups[accountId] = list = new List<List<WorldObject>>();
            list.Add(members);
            return members;
        }

        /// <summary>
        /// The seeded group row this guid is the REPRESENTATIVE of, or null. Never matches a
        /// non-leading member: the representative guid is the only identity a listing carries.
        /// Matches a row that has sold down to one member too - AccountVaultStore emits that as an
        /// ORDINARY item entry, and every caller here mirrors that rule rather than assuming a group.
        /// </summary>
        private List<WorldObject> FindGroup(uint accountId, uint itemGuid)
        {
            if (!Groups.TryGetValue(accountId, out var list))
                return null;

            return list.FirstOrDefault(g => g.Count > 0 && g[0].Guid.Full == itemGuid);
        }

        private void Delay()
        {
            var hold = storeHold;

            if (hold != null)
            {
                DelayEntered.TrySetResult(true);
                hold.Wait(TimeSpan.FromSeconds(10));
                return;
            }

            if (StoreDelayMs > 0)
            {
                DelayEntered.TrySetResult(true);
                System.Threading.Thread.Sleep(StoreDelayMs);
            }
        }

        public long LedgerCount(uint accountId, uint wcid)
            => Ledger.TryGetValue((accountId, wcid), out var c) ? c : 0;

        public bool IsReady(uint accountId, out string failReason)
        {
            if (NotReadyAccounts.Contains(accountId))
            {
                failReason = AccountVaultStore.UnavailableMessage;
                return false;
            }

            failReason = null;
            return true;
        }

        /// <summary>
        /// How many times a vault has been enumerated. The backfill's cost is dominated by this, so
        /// "it did no work" is only checkable against a counter that sees the vault reads.
        /// </summary>
        public int GetEntriesCalls;

        public IReadOnlyList<VaultEntry> GetEntries(uint accountId, int offset, int limit)
        {
            GetEntriesCalls++;

            Delay();

            if (NotReadyAccounts.Contains(accountId))
                return new List<VaultEntry>();

            var entries = new List<VaultEntry>();

            if (Items.TryGetValue(accountId, out var list))
                entries.AddRange(list.Select(VaultEntry.ForItem));

            // Exactly AccountVaultStore.EnumerateEntriesLocked's rule: a row of one is an ORDINARY
            // item entry, never a group of one, so its Count is its own StackSize again and a partial
            // withdraw of it is refused again.
            if (Groups.TryGetValue(accountId, out var groups))
                // ToList, because VaultEntry.ForGroup takes ownership of the list it is handed and the
                // seeded one keeps being mutated by later takes. Sharing it would let an entry a test
                // captured before a sale silently change under it.
                entries.AddRange(groups.Where(g => g.Count > 0)
                                       .Select(g => g.Count > 1 ? VaultEntry.ForGroup(g.ToList()) : VaultEntry.ForItem(g[0])));

            entries.AddRange(Ledger.Where(kvp => kvp.Key.account == accountId && kvp.Value > 0)
                                   .OrderBy(kvp => kvp.Key.wcid)
                                   .Select(kvp => VaultEntry.ForLedger(kvp.Key.wcid, kvp.Value)));

            if (offset < 0)
                offset = 0;

            var page = entries.Skip(offset);

            return (limit > 0 ? page.Take(limit) : page).ToList();
        }

        public bool RunSerialized(uint accountId, Action work)
        {
            if (work == null || RefuseSerializedAccounts.Contains(accountId))
                return false;

            Delay();

            lock (gate)
            {
                openAccounts.Add(accountId);
                MaxNestedAccounts = Math.Max(MaxNestedAccounts, openAccounts.Distinct().Count());

                regionDepth++;
                MaxRegionDepth = Math.Max(MaxRegionDepth, regionDepth);
            }

            try
            {
                work();
                return true;
            }
            catch (Exception)
            {
                SerializedRegionThrows++;
                throw;
            }
            finally
            {
                lock (gate)
                {
                    openAccounts.Remove(accountId);
                    regionDepth--;
                }
            }
        }

        public bool Holds(uint accountId, uint? itemGuid, uint wcid, int count)
        {
            if (itemGuid == null)
                return LedgerCount(accountId, wcid) >= count;

            var group = FindGroup(accountId, itemGuid.Value);

            if (group != null)
                return group.Count >= count;

            return Items.TryGetValue(accountId, out var list) && list.Any(i => i.Guid.Full == itemGuid.Value);
        }

        public bool CanReceive(uint buyerAccountId, uint? itemGuid, uint wcid, int count, out MarketError error)
        {
            error = MarketError.None;

            if (NotReadyAccounts.Contains(buyerAccountId))
            {
                error = MarketError.VaultUnavailable;
                return false;
            }

            if (PreflightFullVaultAccounts.Contains(buyerAccountId))
            {
                error = MarketError.VaultFull;
                return false;
            }

            return true;
        }

        public bool TryTakeForSale(uint sellerAccountId, uint? itemGuid, uint wcid, int count,
                                   MarketActor auditActor, out List<WorldObject> taken, out MarketError error)
        {
            TakeCalls++;
            taken = new List<WorldObject>();
            error = MarketError.None;

            if (FailTakeAccounts.Contains(sellerAccountId))
            {
                error = MarketError.VaultUnavailable;
                return false;
            }

            if (itemGuid == null)
            {
                if (LedgerCount(sellerAccountId, wcid) < count)
                {
                    error = MarketError.CountUnavailable;
                    return false;
                }

                // Where the real AccountVaultStore.TryWithdraw fires it: after every refusal, before
                // anything moves. Without this the fake cannot see the market delisting its own sale.
                AccountVaultStore.PreWithdrawHook?.Invoke(sellerAccountId, null, wcid, count);

                Ledger[(sellerAccountId, wcid)] = LedgerCount(sellerAccountId, wcid) - count;
                taken.Add(FakeVaultWorld.MakeStack(wcid, count, 100));
                return true;
            }

            var group = FindGroup(sellerAccountId, itemGuid.Value);

            if (group != null && group.Count > 1)
            {
                if (count < 1 || count > group.Count)
                {
                    error = MarketError.CountUnavailable;
                    return false;
                }

                AccountVaultStore.PreWithdrawHook?.Invoke(sellerAccountId, itemGuid, wcid, count);

                // From the BACK, as VaultMarketItemStore asks the real store to. The representative at
                // index 0 therefore survives every sale that does not empty the row, which is what
                // keeps the listing's own item_Guid pointing at something the seller still holds.
                taken.AddRange(group.GetRange(group.Count - count, count));
                group.RemoveRange(group.Count - count, count);
                return true;
            }

            if (group != null)
            {
                // Down to one member: an ordinary stored item again, taken whole.
                AccountVaultStore.PreWithdrawHook?.Invoke(sellerAccountId, itemGuid, wcid, group[0].StackSize ?? 1);

                taken.Add(group[0]);
                group.RemoveAt(0);
                return true;
            }

            if (!Items.TryGetValue(sellerAccountId, out var list))
            {
                error = MarketError.ItemNotFound;
                return false;
            }

            var item = list.FirstOrDefault(i => i.Guid.Full == itemGuid.Value);

            if (item == null)
            {
                error = MarketError.ItemNotFound;
                return false;
            }

            // A stored biota comes out whole, so the amount is its own count, as the real store computes it.
            AccountVaultStore.PreWithdrawHook?.Invoke(sellerAccountId, itemGuid, wcid, item.StackSize ?? 1);

            list.Remove(item);
            taken.Add(item);
            return true;
        }

        public int CountMatching(uint accountId, Func<WorldObject, bool> predicate)
        {
            if (NotReadyAccounts.Contains(accountId)) return 0;
            var total = 0;
            if (Items.TryGetValue(accountId, out var items)) total += items.Count(predicate);
            if (Groups.TryGetValue(accountId, out var groups)) total += groups.Sum(g => g.Count(predicate));

            // Ledger rows count one per UNIT, as VaultMarketItemStore.CountMatchingEntries does.
            foreach (var row in Ledger.Where(kvp => kvp.Key.account == accountId && kvp.Value > 0).ToList())
                if (LedgerUnitMatches(row.Key.wcid, predicate)) total += (int)row.Value;

            return total;
        }

        public bool TryTakeMatching(uint sellerAccountId, Func<WorldObject, bool> predicate, int count,
                                    MarketActor auditActor, out List<WorldObject> taken,
                                    out MarketTakeReceipt receipt, out MarketError error)
        {
            TakeCalls++;
            taken = new List<WorldObject>();
            error = MarketError.None;

            // Assigned up front and re-issued only where objects change hands, as the real store does.
            receipt = MarketTakeReceipt.Empty;

            if (FailTakeMatchingAccounts.Contains(sellerAccountId)) { error = MarketError.VaultUnavailable; return false; }

            var needed = count;
            var withdraws = 0;

            // Provenance, as VaultMarketItemStore.TryTakeMatching keeps it: a ledger-derived object
            // must go back to the LEDGER on an unwind, never into a container.
            var ledgerSourced = new HashSet<uint>();

            // The unwind catch mirrors VaultMarketItemStore.TryTakeMatching's, and for the same reason:
            // this is the only market take that withdraws more than once, so it is the only one whose
            // throw can leave items out of the seller's vault and in nobody's. Nothing above it helps -
            // RunSerialized rethrows and SafeSerialized swallows into a bare false - so a fake without
            // it would let the manager pass a green suite over a real store that strands bags.
            try
            {
                if (Groups.TryGetValue(sellerAccountId, out var groups))
                {
                    foreach (var group in groups)
                    {
                        if (needed == 0) break;
                        var matching = group.Count(predicate);
                        if (matching == 0) continue;
                        var amount = Math.Min(needed, matching);

                        if (ThrowOnTakeMatchingWithdraw > 0 && ++withdraws == ThrowOnTakeMatchingWithdraw)
                            throw new InvalidOperationException($"withdraw {withdraws} threw for account {sellerAccountId}");

                        // Where the real store fires it, before anything moves. From the BACK, as TakeOrderForSale asks.
                        AccountVaultStore.PreWithdrawHook?.Invoke(sellerAccountId, group[0].Guid.Full, group[0].WeenieClassId, amount);

                        // Taken from the BACK unconditionally, same as VaultMarketItemStore.TryTakeMatching's
                        // withdraw - the slice can contain a non-matching member. Everything withdrawn goes
                        // into taken; needed is decremented ONLY for members that actually pass the predicate,
                        // so a non-matching tail item never masquerades as progress.
                        var slice = group.GetRange(group.Count - amount, amount);
                        group.RemoveRange(group.Count - amount, amount);

                        foreach (var item in slice)
                        {
                            taken.Add(item);
                            if (needed > 0 && predicate(item))
                                needed--;
                        }
                    }
                }

                if (needed > 0 && Items.TryGetValue(sellerAccountId, out var items))
                {
                    foreach (var item in items.Where(predicate).ToList())
                    {
                        if (needed == 0) break;

                        if (ThrowOnTakeMatchingWithdraw > 0 && ++withdraws == ThrowOnTakeMatchingWithdraw)
                            throw new InvalidOperationException($"withdraw {withdraws} threw for account {sellerAccountId}");

                        AccountVaultStore.PreWithdrawHook?.Invoke(sellerAccountId, item.Guid.Full, item.WeenieClassId, item.StackSize ?? 1);
                        items.Remove(item);
                        taken.Add(item);
                        needed--;
                    }
                }

                // Ledger rows LAST, matching GetEntries' own order (items, groups, ledger).
                foreach (var row in Ledger.Where(kvp => kvp.Key.account == sellerAccountId && kvp.Value > 0).ToList())
                {
                    if (needed == 0) break;
                    if (!LedgerUnitMatches(row.Key.wcid, predicate)) continue;

                    var amount = (int)Math.Min(needed, row.Value);

                    if (ThrowOnTakeMatchingWithdraw > 0 && ++withdraws == ThrowOnTakeMatchingWithdraw)
                        throw new InvalidOperationException($"withdraw {withdraws} threw for account {sellerAccountId}");

                    // A ledger withdraw carries a NULL guid, which is how the real pre-withdraw hook
                    // tells the auto-delist that a whole ledger row moved rather than one biota.
                    AccountVaultStore.PreWithdrawHook?.Invoke(sellerAccountId, null, row.Key.wcid, amount);

                    Ledger[row.Key] = row.Value - amount;

                    var factory = LedgerUnitFactories[row.Key.wcid];

                    for (var i = 0; i < amount; i++)
                    {
                        // One OBJECT per unit, which is what the non-stackable clause in
                        // LedgerUnitMatches guarantees the real store's CalcPayoutStackSizes split
                        // produces for these wcids.
                        var built = factory();
                        taken.Add(built);
                        ledgerSourced.Add(built.Guid.Full);
                        if (needed > 0 && predicate(built)) needed--;
                    }
                }
            }
            catch (Exception)
            {
                UnwindToSeller(sellerAccountId, taken, new MarketTakeReceipt(ledgerSourced), auditActor);

                taken = new List<WorldObject>();

                // Not NoMatchingItems: after a throw the seller's holdings are unknown, so claiming
                // they hold nothing would be a guess. The real store answers the same way.
                error = MarketError.VaultUnavailable;
                return false;
            }

            receipt = new MarketTakeReceipt(ledgerSourced);

            if (needed > 0 || taken.Count != count)
            {
                // Shortfall: everything goes back, inside this region, through the same return path the manager would use.
                UnwindToSeller(sellerAccountId, taken, receipt, auditActor);
                taken = new List<WorldObject>();
                receipt = MarketTakeReceipt.Empty;
                error = MarketError.NoMatchingItems;
                return false;
            }

            return true;
        }

        /// <summary>
        /// Mirrors VaultMarketItemStore.UnwindToSeller, INNER CATCH INCLUDED. The real
        /// TryReturnToSeller calls AccountVaultStore.TryReturnWithdrawn with no catch of its own and
        /// that method throws (Player_Facets.cs:909 wraps the identical pair for that reason), so a
        /// fake whose unwind cannot fail would be more permissive than the store it stands in for -
        /// which is the whole hazard MarketFakeMatchingTakeTests exists to keep shut.
        /// </summary>
        private bool UnwindToSeller(uint sellerAccountId, IReadOnlyList<WorldObject> taken, MarketTakeReceipt receipt,
                                    MarketActor auditActor)
        {
            if (taken == null || taken.Count == 0)
                return true;

            // A missing receipt resolves toward "stored biota", the direction that never destroys an
            // item, exactly as the real unwind does.
            receipt ??= MarketTakeReceipt.Empty;

            var returned = false;

            try
            {
                // Split by provenance exactly as the real unwind does - see its remarks for why the
                // two undos are not interchangeable in either direction.
                var stored = taken.Where(i => !receipt.IsLedgerSourced(i)).ToList();
                var allReturned = true;

                if (stored.Count > 0)
                    allReturned &= TryReturnToSeller(sellerAccountId, stored, stored[0].WeenieClassId, false, auditActor);

                foreach (var group in taken.Where(receipt.IsLedgerSourced).GroupBy(i => i.WeenieClassId))
                    allReturned &= TryReturnToSeller(sellerAccountId, group.ToList(), group.Key, true, auditActor);

                returned = allReturned;
            }
            catch (Exception)
            {
                // Reported by the real store; here the loss is simply left in place for the test to see.
            }

            return returned;
        }

        /// <summary>
        /// Mirrors VaultMarketItemStore.UndoTake: the caller-facing undo of a TryTakeMatching, routed
        /// through the SAME unwind body the take's own failure exits use so the fake cannot be more
        /// permissive than the store about provenance either.
        /// </summary>
        public bool UndoTake(uint sellerAccountId, IReadOnlyList<WorldObject> items, MarketTakeReceipt receipt,
                             MarketActor auditActor)
            => UnwindToSeller(sellerAccountId, items, receipt, auditActor);

        public bool TryGiveToBuyer(uint buyerAccountId, IReadOnlyList<WorldObject> items,
                                   MarketActor auditActor, out MarketError error)
        {
            GiveCalls++;
            error = MarketError.None;

            if (FailGiveAccounts.Contains(buyerAccountId))
            {
                error = MarketError.VaultUnavailable;
                return false;
            }

            if (FullVaultAccounts.Contains(buyerAccountId))
            {
                error = MarketError.VaultFull;
                return false;
            }

            foreach (var item in items)
                SeedItem(buyerAccountId, item);

            return true;
        }

        public bool TryReturnToSeller(uint sellerAccountId, IReadOnlyList<WorldObject> items, uint wcid,
                                      bool asLedger, MarketActor auditActor)
        {
            ReturnCalls++;

            // After the counter, so a test can still prove the unwind was ATTEMPTED, and before any
            // item moves, which is the worst case: nothing came back.
            if (ThrowOnReturnToSeller)
                throw new InvalidOperationException($"the return to seller account {sellerAccountId} threw");

            if (FailReturnAccounts.Contains(sellerAccountId))
                return false;

            if (asLedger)
            {
                foreach (var item in items)
                    Ledger[(sellerAccountId, wcid)] = LedgerCount(sellerAccountId, wcid) + (item.StackSize ?? 1);

                return true;
            }

            foreach (var item in items)
            {
                // Back where it came from, when it came from a group: appended to the BACK of the row
                // it was taken from, so an unwound sale leaves the representative where it was.
                var group = Groups.TryGetValue(sellerAccountId, out var rows)
                    ? rows.FirstOrDefault(g => g.Count > 0 && g[0].WeenieClassId == wcid)
                    : null;

                if (group != null)
                    group.Add(item);
                else
                    SeedItem(sellerAccountId, item);
            }

            return true;
        }

        /// <summary>Barrel calls, so a test can prove one was or was not attempted.</summary>
        public int BarrelCalls;

        /// <summary>Models the account's barrel being unreachable - not loaded, uncreatable, or full.</summary>
        public readonly HashSet<uint> BarrelUnavailableAccounts = new HashSet<uint>();

        /// <summary>
        /// What was barreled, in order: (account, itemGuid, wcid, count). The holdings themselves are
        /// REMOVED rather than moved to a second collection, because from the market's side that is
        /// exactly what a barreling is - the row is gone from every read this store answers.
        /// </summary>
        public readonly List<(uint account, uint? itemGuid, uint wcid, int count)> Barreled =
            new List<(uint, uint?, uint, int)>();

        public bool TryBarrel(uint accountId, uint? itemGuid, uint wcid, int count, MarketActor auditActor, out MarketError error)
        {
            BarrelCalls++;
            error = MarketError.None;

            if (NotReadyAccounts.Contains(accountId))
            {
                error = MarketError.VaultUnavailable;
                return false;
            }

            if (BarrelUnavailableAccounts.Contains(accountId))
            {
                error = MarketError.BarrelUnavailable;
                return false;
            }

            if (itemGuid == null)
            {
                if (LedgerCount(accountId, wcid) < count)
                {
                    error = MarketError.CountUnavailable;
                    return false;
                }

                Ledger[(accountId, wcid)] = LedgerCount(accountId, wcid) - count;
                Barreled.Add((accountId, null, wcid, count));
                return true;
            }

            var group = FindGroup(accountId, itemGuid.Value);

            if (group != null)
            {
                if (count < 1 || count > group.Count)
                {
                    error = MarketError.CountUnavailable;
                    return false;
                }

                // From the FRONT, matching AccountVaultStore.BarrelStoredItems and VaultEntry.Members'
                // documented ordering. A sale takes from the back to protect a listing's anchor; a
                // barreling has no anchor to protect, and a listed row is refused before it gets here.
                group.RemoveRange(0, count);
                Barreled.Add((accountId, itemGuid, wcid, count));
                return true;
            }

            if (!Items.TryGetValue(accountId, out var list))
            {
                error = MarketError.ItemNotFound;
                return false;
            }

            var item = list.FirstOrDefault(i => i.Guid.Full == itemGuid.Value);

            if (item == null)
            {
                error = MarketError.ItemNotFound;
                return false;
            }

            list.Remove(item);
            Barreled.Add((accountId, itemGuid, wcid, count));
            return true;
        }
    }

    /// <summary>
    /// In-memory wallet, in WHOLE MMD. Models the one routine failure (insufficient funds), the ones
    /// that are not (a credit that does not land, a debit that errors), and the one that is neither a
    /// success nor a refusal (a debit whose ledger state is unknown).
    /// </summary>
    internal class FakeMarketWallet : IMarketWallet
    {
        public readonly Dictionary<uint, long> Balances = new Dictionary<uint, long>();

        public readonly HashSet<uint> FailDebitCharacters = new HashSet<uint>();
        public readonly HashSet<uint> FailCreditCharacters = new HashSet<uint>();

        /// <summary>
        /// Debit comes back with the ledger state UNKNOWN rather than refused. The balance is left
        /// alone because that is the point of the case: nobody, this fake included, knows whether the
        /// notes moved, so a test may assert on the persisted status but never on the balance.
        /// </summary>
        public readonly HashSet<uint> LedgerUnknownDebitCharacters = new HashSet<uint>();

        /// <summary>Credit dies rather than refusing: stands in for the process not reaching the next line.</summary>
        public readonly HashSet<uint> ThrowCreditCharacters = new HashSet<uint>();

        public int DebitCalls;
        public int CreditCalls;

        public void Seed(uint characterGuid, long mmd) => Balances[characterGuid] = mmd;

        public long GetBalanceMmd(uint characterGuid)
            => Balances.TryGetValue(characterGuid, out var v) ? v : 0;

        public bool TryDebit(uint characterGuid, long amountMmd, out MarketError error)
        {
            DebitCalls++;

            if (FailDebitCharacters.Contains(characterGuid))
            {
                error = MarketError.ServerError;
                return false;
            }

            if (LedgerUnknownDebitCharacters.Contains(characterGuid))
            {
                error = MarketError.LedgerUnknown;
                return false;
            }

            if (amountMmd < 0 || GetBalanceMmd(characterGuid) < amountMmd)
            {
                error = MarketError.InsufficientFunds;
                return false;
            }

            Balances[characterGuid] = GetBalanceMmd(characterGuid) - amountMmd;
            error = MarketError.None;
            return true;
        }

        public bool TryCredit(uint characterGuid, long amountMmd)
        {
            CreditCalls++;

            if (ThrowCreditCharacters.Contains(characterGuid))
                throw new InvalidOperationException($"simulated death crediting 0x{characterGuid:X8}");

            if (FailCreditCharacters.Contains(characterGuid))
                return false;

            Balances[characterGuid] = GetBalanceMmd(characterGuid) + amountMmd;
            return true;
        }
    }
}
