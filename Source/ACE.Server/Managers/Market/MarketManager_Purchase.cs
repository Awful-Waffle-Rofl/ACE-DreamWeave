using System;
using System.Collections.Generic;
using System.Linq;

using log4net;

using ACE.Common.Extensions;
using ACE.Server.WorldObjects;

using ShardMarketTransaction = ACE.Database.Models.Shard.MarketTransaction;

namespace ACE.Server.Managers.Market
{
    /// <summary>
    /// The purchase transaction (DESIGN 5.4) and its boot recovery. The row is marked Completed the instant custody changes, before the non-unwinding steps 5 and 6.
    /// Money before goods, deliberately: a failed debit costs nobody, a failed goods move after moving has no clean unwind.
    /// STEPS 3 AND 4 (withdraw seller, deposit buyer) ARE SEPARATE SERIALIZED REGIONS, NEVER NESTED - nesting lets "A buys from B" and "B buys from A" deadlock; the items live only in this method's local list between them.
    /// </summary>
    public static partial class MarketManager
    {
        private static readonly ILog purchaseLog = LogManager.GetLogger("MarketPurchase");

        /// <summary>
        /// The largest per-unit price, and the largest count * price total, the market will handle.
        /// It IS Player.TryCreditBankedMmd's bound: above it a credit is refused AFTER the goods
        /// moved, and the underlying notes * MmdValue would wrap long.
        /// </summary>
        public const long MaxPriceMmd = long.MaxValue / Player.MmdValue;

        /// <summary>
        /// One refused Buy attempt, enqueued and turned into the failure the caller returns. The same
        /// MarketError produces both, so the recorded code can never disagree with the returned one.
        ///
        /// This is an ENQUEUE and nothing else - see MarketRejectionLog's threading contract. Step 0
        /// stays allocation-cheap and DB-free, which is not a style preference: it is the fix for the
        /// 2026-09-02 incident where a retried failed buy click produced duplicate Pending/Refunded rows.
        /// </summary>
        private static MarketResult<MarketTransaction> RefuseBuy(MarketError error, MarketChannel channel, MarketActor buyer,
                                                                 uint listingId, MarketListing listing, int count, long priceMmd,
                                                                 string detail = null)
        {
            MarketRejectionLog.Record(MarketRejectOperation.Buy, error, channel, buyer, listingId,
                                      listing?.ItemGuid, listing?.Wcid ?? 0, count, priceMmd, detail);

            return MarketResult<MarketTransaction>.Fail(error);
        }

        public static MarketResult<MarketTransaction> Buy(MarketActor buyer, uint listingId, int count,
                                                          long expectedPriceMmd, MarketChannel channel)
        {
            // NOT recorded: the kill switch being off is an operator state, not a player event.
            if (!Enabled)
                return MarketResult<MarketTransaction>.Fail(MarketError.Disabled);

            // ---- step 0: validate. Nothing has moved and nothing may. ----
            // Every refusal below writes one market_rejected_attempt row (DESIGN 5.5). Past this step
            // only the failed Pending-row write does, because that is the one later branch where the
            // row provably does not exist; once it does, market_transaction is the record.

            if (buyer.AccountId == 0 || buyer.CharacterGuid == 0)
                return RefuseBuy(MarketError.BadCredentials, channel, buyer, listingId, null, count, expectedPriceMmd);

            if (count < 1)
                return RefuseBuy(MarketError.CountUnavailable, channel, buyer, listingId, null, count, expectedPriceMmd);

            var listing = GetListing(listingId);

            if (listing == null || listing.Status != MarketListingStatus.Active)
                return RefuseBuy(MarketError.ListingNotActive, channel, buyer, listingId, listing, count, expectedPriceMmd,
                                 listing == null ? "no such listing in the index" : $"listing is {listing.Status.ToString().ToLowerInvariant()}");

            if (listing.SellerAccountId == buyer.AccountId)
                return RefuseBuy(MarketError.NotOwner, channel, buyer, listingId, listing, count, expectedPriceMmd,
                                 "buying own listing");

            if (listing.Count < count)
                return RefuseBuy(MarketError.CountUnavailable, channel, buyer, listingId, listing, count, expectedPriceMmd,
                                 $"the listing holds {listing.Count}");

            // A stale web price is NEVER silently repriced (DESIGN 5.4). The live price travels back
            // so the web app can offer the real number instead of re-fetching and guessing.
            if (expectedPriceMmd != listing.PriceMmd)
            {
                // Recorded with the price the BUYER quoted, because that plus the detail line is what
                // reconstructs the dispute; the live price alone would look like nothing went wrong.
                MarketRejectionLog.Record(MarketRejectOperation.Buy, MarketError.PriceChanged, channel, buyer, listingId,
                                          listing.ItemGuid, listing.Wcid, count, expectedPriceMmd,
                                          $"live price {listing.PriceMmd}");

                return MarketResult<MarketTransaction>.PriceChanged(listing.PriceMmd);
            }

            // Division, never a checked multiply: an OverflowException escaping here would be a 500
            // on the money path. List() caps the per-unit price; only the product needs this.
            if (listing.PriceMmd < 0 || listing.PriceMmd > MaxPriceMmd / count)
                return RefuseBuy(MarketError.InvalidPrice, channel, buyer, listingId, listing, count, listing.PriceMmd,
                                 $"count {count} times the price overflows the bank bound");

            if (!itemStore.IsReady(listing.SellerAccountId, out _))
                return RefuseBuy(MarketError.VaultUnavailable, channel, buyer, listingId, listing, count, listing.PriceMmd,
                                 "the seller's vault is not ready");

            // The buyer's vault must satisfy the store's IsLoaded rule; the manager loads it on
            // demand and returns vault_unavailable rather than blocking (DESIGN 5.4).
            if (!itemStore.IsReady(buyer.AccountId, out _))
                return RefuseBuy(MarketError.VaultUnavailable, channel, buyer, listingId, listing, count, listing.PriceMmd,
                                 "the buyer's vault is not ready");

            // Still step 0, still nothing moved: a full or unavailable buyer vault is knowable up
            // front (2026-09-02, five Pending/Refunded rows from one full-vault buy click that a web
            // re-post kept retrying past step 4's real check). Refusing here means no Pending row and
            // no debit for a purchase that was never going to deliver - step 4's TryGiveToBuyer/unwind
            // stays the actual guarantee; this only stops the refusal from costing anything first.
            if (!itemStore.CanReceive(buyer.AccountId, listing.ItemGuid, listing.Wcid, count, out var roomError))
                return RefuseBuy(roomError, channel, buyer, listingId, listing, count, listing.PriceMmd,
                                 "the buyer's vault cannot receive it");

            var total = listing.PriceMmd * count;

            // ---- step 1: the Pending row, BEFORE any value moves ----

            var row = new ShardMarketTransaction
            {
                ListingId = listing.Id,
                BuyerAccountId = buyer.AccountId,
                BuyerCharacterGuid = buyer.CharacterGuid,
                BuyerCharacterName = buyer.Name,
                SellerAccountId = listing.SellerAccountId,
                SellerCharacterGuid = listing.SellerCharacterGuid,
                SellerCharacterName = listing.SellerCharacterName,
                Wcid = listing.Wcid,
                ItemName = listing.Snapshot?.Name ?? $"Item {listing.Wcid}",
                Count = count,
                PriceMmdTotal = total,
                Timestamp = DateTime.UtcNow,
                Channel = (int)channel,
                Status = (int)MarketTransactionStatus.Pending,
            };

            if (!repository.AddTransaction(row))
            {
                purchaseLog.Error($"[MARKET] could not write the Pending row for listing {listing.Id}; refusing before anything moves.");

                // The SAME synthetic code List uses for its AddListing refusal, and for the same reason:
                // nothing was written either way, so market_transaction holds nothing and the buyer was
                // told server_error. Without this row the attempt has no trace anywhere at all, and an
                // investigator handed "I tried to buy this and it failed" would find no evidence it ever
                // happened. Invariant 4 is not breached - it excludes attempts that REACH the Pending
                // row, and this is the one branch where the row provably does not exist.
                MarketRejectionLog.Record(MarketRejectOperation.Buy, MarketRejectionLog.RepositoryRefusedCode, channel,
                                          buyer, listing.Id, listing.ItemGuid, listing.Wcid, count, listing.PriceMmd,
                                          "AddTransaction failed; the Pending row was never written");

                return MarketResult<MarketTransaction>.Fail(MarketError.ServerError);
            }

            // ---- step 2: debit the buyer ----

            if (!wallet.TryDebit(buyer.CharacterGuid, total, out var debitError))
            {
                // A refused debit and an UNKNOWN one resolve to different statuses, and the difference
                // is the whole point: Failed says the buyer provably was not charged, DebitLedgerUnknown
                // says nobody knows. Recording both as Failed would bury the only rows where a buyer can
                // be out of pocket with nothing delivered, and boot recovery would never find them
                // either - it scans Pending only, and this row is resolved.
                Resolve(row, debitError == MarketError.LedgerUnknown
                    ? MarketTransactionStatus.DebitLedgerUnknown
                    : MarketTransactionStatus.Failed);

                return MarketResult<MarketTransaction>.Fail(debitError);
            }

            // ---- step 3: take from the seller, on the SELLER's queue ----

            List<WorldObject> taken = null;
            var takeError = MarketError.None;
            var took = false;
            var sellerActor = new MarketActor(listing.SellerAccountId, listing.SellerCharacterGuid, listing.SellerCharacterName);

            // Claimed across the take, because it withdraws from the vault and would otherwise trip the
            // market's own auto-delist against the very listing being sold.
            BeginSale(listing.Id);

            bool sellerRegionRan;

            try
            {
                sellerRegionRan = SafeSerialized(listing.SellerAccountId, () =>
                {
                    took = itemStore.TryTakeForSale(listing.SellerAccountId, listing.ItemGuid, listing.Wcid, count,
                                                    sellerActor, out taken, out takeError);
                });
            }
            finally
            {
                EndSale(listing.Id);
            }

            if (!sellerRegionRan || !took || taken == null || taken.Count == 0)
            {
                RefundBuyer(row, buyer, total, "the seller's items could not be taken");
                Resolve(row, MarketTransactionStatus.Refunded);
                return MarketResult<MarketTransaction>.Fail(takeError == MarketError.None ? MarketError.VaultUnavailable : takeError);
            }

            // ---- step 4: give to the buyer, on the BUYER's queue, in a SEPARATE region ----

            var gave = false;
            var giveError = MarketError.None;

            var buyerRegionRan = SafeSerialized(buyer.AccountId, () =>
            {
                gave = itemStore.TryGiveToBuyer(buyer.AccountId, taken, buyer, out giveError);
            });

            if (!buyerRegionRan || !gave)
            {
                var asLedger = listing.ItemGuid == null;

                var returned = SafeSerialized(listing.SellerAccountId,
                    () => itemStore.TryReturnToSeller(listing.SellerAccountId, taken, listing.Wcid, asLedger, sellerActor));

                if (!returned)
                {
                    // Named guids, because only a human can fix this and only from these lines plus market_transaction.
                    purchaseLog.Error($"[MARKET] LOST ITEM RISK on transaction {row.Id}: could not deposit into buyer account {buyer.AccountId} AND could not return to seller account {listing.SellerAccountId}. Items: {string.Join(", ", taken.Select(i => $"0x{i.Guid.Full:X8}"))}. The buyer is being refunded regardless.");
                }

                RefundBuyer(row, buyer, total, "the item could not be delivered");
                Resolve(row, MarketTransactionStatus.Refunded);
                return MarketResult<MarketTransaction>.Fail(giveError == MarketError.None ? MarketError.VaultUnavailable : giveError);
            }

            // ---- the row completes HERE, the instant custody changes ----
            // Nothing below unwinds, and a partial sale leaves the listing Active, so a row still
            // Pending after delivery would be refunded by boot recovery to a buyer holding the goods.
            Resolve(row, MarketTransactionStatus.Completed);

            // ---- step 5: pay the seller. A failure here does NOT unwind. ----

            if (!wallet.TryCredit(listing.SellerCharacterGuid, total))
                purchaseLog.Error($"[MARKET] CREDIT LOST on transaction {row.Id}: {total} MMD could not be paid to character 0x{listing.SellerCharacterGuid:X8} ({listing.SellerCharacterName}). The buyer HAS the item, so this is NOT unwound - pay the seller by hand from market_transaction.");

            // ---- step 6: close or decrement the listing ----

            if (!ApplySale(listing, count))
                purchaseLog.Error($"[MARKET] transaction {row.Id} completed but listing {listing.Id} could not be updated. It will be corrected by the next invalidation pass.");

            ACE.Server.Managers.Analytics.AnalyticsManager.RecordMarketSale(
                row.BuyerCharacterGuid, row.BuyerCharacterName,
                row.SellerCharacterGuid, row.SellerCharacterName,
                row.Wcid, row.ItemName, row.Count, row.PriceMmdTotal);

            // ---- step 7: tell whichever side is online. Never before Completed, never inside an unwind. ----
            // Both channels reach here, so this is the one hook that covers the web app and /market buy.
            MarketNotifier.NotifySale(row);

            return MarketResult<MarketTransaction>.Success(TrackTransaction(row));
        }

        /// <summary>
        /// Runs a serialized region and converts a throw into false. An exception escaping a vault
        /// region would skip the compensation below it and leave a Pending row plus a debited buyer.
        /// </summary>
        private static bool SafeSerialized(uint accountId, Action work)
        {
            try
            {
                return itemStore.RunSerialized(accountId, work);
            }
            catch (Exception ex)
            {
                purchaseLog.Error($"[MARKET] a serialized vault region threw for account {accountId}: {ex.GetFullMessage()}");
                return false;
            }
        }

        private static void RefundBuyer(ShardMarketTransaction row, MarketActor buyer, long total, string why)
        {
            if (wallet.TryCredit(buyer.CharacterGuid, total))
                return;

            purchaseLog.Error($"[MARKET] REFUND LOST on transaction {row.Id}: {total} MMD could not be returned to character 0x{buyer.CharacterGuid:X8} after {why}. Refund by hand from market_transaction.");
        }

        /// <summary>
        /// Writes the terminal status and publishes the row to the history feed. A lost UPDATE after
        /// custody changed is NOT self-healing: the row stays Pending and boot recovery refunds it.
        /// </summary>
        private static void Resolve(ShardMarketTransaction row, MarketTransactionStatus status)
        {
            row.Status = (int)status;

            if (!repository.UpdateTransaction(row))
                purchaseLog.Error($"[MARKET] could not persist transaction {row.Id} as {status}. It stays Pending on disk, so if custody already changed boot recovery will REFUND a completed sale. Reconcile by hand from market_transaction.");

            TrackTransaction(row);
        }

        private static MarketTransaction TrackTransaction(ShardMarketTransaction row)
        {
            var tx = new MarketTransaction
            {
                Id = row.Id,
                ListingId = row.ListingId,

                // Carried here as well as in FromRow: this is the LIVE path (a fill publishes its row
                // through Resolve/TrackTransaction, not through the boot preload), so without it every
                // fill would reach the history feed and the API answer reading as an ordinary sale.
                BuyOrderId = row.BuyOrderId,
                BuyerAccountId = row.BuyerAccountId,
                BuyerCharacterGuid = row.BuyerCharacterGuid,
                BuyerCharacterName = row.BuyerCharacterName,
                SellerAccountId = row.SellerAccountId,
                SellerCharacterGuid = row.SellerCharacterGuid,
                SellerCharacterName = row.SellerCharacterName,
                Wcid = row.Wcid,
                ItemName = row.ItemName,
                Count = row.Count,
                PriceMmdTotal = row.PriceMmdTotal,
                Timestamp = row.Timestamp,
                Channel = (MarketChannel)row.Channel,
                Status = (MarketTransactionStatus)row.Status,
            };

            lock (indexLock)
            {
                tx.Seq = ++changeSequence;
                transactions[tx.Id] = tx;
            }

            return tx;
        }

        /// <summary>
        /// Boot recovery (DESIGN 5.4, "Restart mid-purchase"): any row still Pending past <paramref name="olderThanMs"/> belongs to a purchase this process died inside.
        /// A delivered sale is already Completed, so a Pending row means custody never changed and this refunds - a crash before the debit lands pays back money never spent, since charging for nothing is the one outcome a player cannot investigate.
        /// The listing is NOT the signal: a partial sale stays Active with a decremented count and never reaches Sold.
        /// A NULL read means the read FAILED and recovery is SKIPPED entirely.
        /// </summary>
        public static void RecoverPendingTransactions(int olderThanMs)
        {
            // The raw field: an interrupted purchase must be refunded even with the kill switch off.
            if (!enabled)
                return;

            var cutoff = DateTime.UtcNow.AddMilliseconds(-Math.Max(0, olderThanMs));
            var pending = repository.GetPendingTransactions(cutoff);

            if (pending == null)
            {
                purchaseLog.Error("[MARKET] could not read pending transactions; boot recovery is SKIPPED rather than concluding there are none. Interrupted purchases stay Pending until a later successful read.");
                return;
            }

            foreach (var row in pending)
            {
                // A fill's Pending row means custody never changed and no money moved (WANTED-DESIGN 6.2
                // completes the row the instant custody changes and the escrow is untouched before that),
                // so refunding it would pay the ORDER's buyer a price they never paid on this row.
                if (row.BuyOrderId != null)
                {
                    purchaseLog.Warn($"[MARKET] boot recovery: transaction {row.Id} is a Pending order fill; marking it Failed with no refund.");
                    Resolve(row, MarketTransactionStatus.Failed);
                    continue;
                }

                var listing = GetListing(row.ListingId);

                if (listing != null && listing.Status == MarketListingStatus.Sold)
                {
                    // The sale reached step 6 and only the final UPDATE was lost. Complete it.
                    purchaseLog.Warn($"[MARKET] boot recovery: transaction {row.Id} is Pending over a Sold listing; completing it.");
                    Resolve(row, MarketTransactionStatus.Completed);

                    // The sale completes HERE rather than in Buy, so this is the only place it can be
                    // announced: without this, a purchase that finished across a restart tells nobody.
                    // NotifySale swallows its own failures, so recovery of the NEXT row is never at risk.
                    MarketNotifier.NotifySale(row);
                    continue;
                }

                purchaseLog.Warn($"[MARKET] boot recovery: refunding {row.PriceMmdTotal} MMD to character 0x{row.BuyerCharacterGuid:X8} for interrupted transaction {row.Id} on listing {row.ListingId}.");

                if (!wallet.TryCredit(row.BuyerCharacterGuid, row.PriceMmdTotal))
                    purchaseLog.Error($"[MARKET] REFUND LOST during boot recovery of transaction {row.Id}: {row.PriceMmdTotal} MMD to character 0x{row.BuyerCharacterGuid:X8}. Refund by hand.");

                Resolve(row, MarketTransactionStatus.Refunded);
            }
        }
    }
}
