using System;
using System.Collections.Generic;
using System.Linq;

using log4net;

using ACE.Common.Extensions;
using ACE.Server.Entity.AccountVault;
using ACE.Server.WorldObjects;

namespace ACE.Server.Managers.Market
{
    /// <summary>
    /// The reference IMarketItemStore over AccountVaultStore (DESIGN 4.1); no market rule lives here.
    /// THE SELLER IS THE ACTOR, NOT THE BUYER: TryWithdraw re-resolves authorization on every call and a
    /// buyer holds no grant on the seller's vault. Every mutation must run inside RunSerialized.
    /// </summary>
    public class VaultMarketItemStore : IMarketItemStore
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private readonly Func<uint, AccountVaultStore> storeResolver;

        /// <summary>Production wiring: every account's store comes from AccountVaultManager.</summary>
        public VaultMarketItemStore()
            : this(null)
        {
        }

        /// <summary>
        /// THE INJECTION SEAM, and the only one. AccountVaultManager.GetStore hardcodes the production
        /// shard backend, so without this nothing in the test assembly could drive the real custody
        /// code below against a real AccountVaultStore - which is exactly where the partial-delivery
        /// duplicate lived while every fake-driven purchase test stayed green. Null means production.
        /// </summary>
        internal VaultMarketItemStore(Func<uint, AccountVaultStore> storeResolver)
        {
            this.storeResolver = storeResolver ?? AccountVaultManager.GetStore;
        }

        private AccountVaultStore ResolveStore(uint accountId) => storeResolver(accountId);

        public bool IsReady(uint accountId, out string failReason)
        {
            failReason = AccountVaultStore.UnavailableMessage;

            var store = ResolveStore(accountId);

            if (store == null)
                return false;

            return store.TryCheckReady(out failReason);
        }

        public IReadOnlyList<VaultEntry> GetEntries(uint accountId, int offset, int limit)
        {
            var store = ResolveStore(accountId);

            // A negative limit means "everything from the offset on".
            return store == null ? new List<VaultEntry>() : store.GetEntries(offset, limit);
        }

        /// <summary>
        /// Enqueue runs the work on the CALLING thread under the store's drain lock and returns only once
        /// it has run. False means the store was retired by the idle sweep and nothing ran; retry once at
        /// most, because a second retirement in a row means looping would spin.
        /// </summary>
        public bool RunSerialized(uint accountId, Action work)
        {
            if (work == null)
                return false;

            for (var attempt = 0; attempt < 2; attempt++)
            {
                var store = ResolveStore(accountId);

                if (store == null)
                    return false;

                if (store.Enqueue(work, out var thrown))
                {
                    if (thrown == null)
                        return true;

                    // A refusal means nothing happened; a throw means the work got some unknown distance
                    // through. Rethrown, so the caller's SafeSerialized reports the region as not having
                    // run cleanly. The caller DOES then compensate, but only for what it can prove never
                    // moved: a give records each item's outcome in a MarketDelivery that survives the
                    // throw, and only its Undelivered items are returned to the seller.
                    log.Error($"[MARKET] serialized vault work threw for account {accountId}: {thrown.GetFullMessage()}");
                    throw new InvalidOperationException($"vault work for account {accountId} threw", thrown);
                }
            }

            log.Warn($"[MARKET] could not run serialized vault work for account {accountId}: the store was retired twice in a row.");
            return false;
        }

        public bool Holds(uint accountId, uint? itemGuid, uint wcid, string classKey, int count)
        {
            var store = ResolveStore(accountId);

            if (store == null)
                return false;

            return HoldsCount(store.GetEntries(0, -1), itemGuid, wcid, classKey, count);
        }

        /// <summary>
        /// Whether these vault rows still back a listing of <paramref name="count"/>. Both branches
        /// test the COUNT, and the stored-biota one has to: a group row is N whole biotas on one line,
        /// so a listing of 3 stops being backed the moment the row falls to 2 even though the
        /// representative guid is still there. Reading only the guid would answer "yes" for a row that
        /// can no longer deliver, which is the wrong direction for an invalidation check.
        ///
        /// Split out of <see cref="Holds"/> so it can be exercised against rows a test builds
        /// directly: Holds resolves its store through AccountVaultManager.GetStore, which hardcodes
        /// the production backend and offers no injection seam (the same reason
        /// <see cref="WithdrawAmountForSale"/> and <see cref="ClassifyDepositFailure"/> are internal).
        /// </summary>
        internal static bool HoldsCount(IReadOnlyList<VaultEntry> entries, uint? itemGuid, uint wcid, string classKey, int count)
        {
            if (entries == null)
                return false;

            foreach (var entry in entries)
            {
                // A CLASS listing is backed by the class LINE its display id names, and by nothing
                // else: not a ledger row of the same wcid, not another line of it.
                if (classKey != null)
                {
                    if (entry.Kind == VaultEntryKind.Class && entry.ClassDisplayId == classKey && entry.Count >= count)
                        return true;

                    continue;
                }

                // Every NON-class listing skips class rows outright: the guid arm would otherwise
                // match a class row's ObjectGuid.Invalid against a caller's 0, and the ledger arm
                // would answer a ledger listing with a class row's item count.
                if (entry.Kind == VaultEntryKind.Class)
                    continue;

                if (itemGuid != null)
                {
                    if (entry.Kind == VaultEntryKind.StoredItem && entry.Guid.Full == itemGuid.Value && entry.Count >= count)
                        return true;
                }
                else if (entry.Kind == VaultEntryKind.Ledger && entry.Wcid == wcid && entry.Count >= count)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// PREFLIGHT, per <see cref="IMarketItemStore.CanReceive"/>: no BiotaDatabaseLock, no mutation,
        /// no serialized region. store null or not ready -&gt; VaultUnavailable.
        ///
        /// A ledger sale (itemGuid null) tops up the buyer's existing ledger row for free when the
        /// buyer already holds a nonzero one for this wcid (DESIGN 7.3 - a ledger deposit onto a held
        /// row adds no entry); a fresh row costs one. A stored-biota sale (itemGuid set) costs one
        /// entry PER BIOTA delivered: whether any of them would actually join an existing GROUP row
        /// depends on the items themselves (workmanship, structure), which this preflight does not
        /// have without taking them from the seller first, so it deliberately answers the conservative
        /// way - it can only refuse a purchase that would have been free, never admit one past the cap.
        ///
        /// That per-biota count is why the number is not simply 1. A GROUP listing sells whole
        /// members, so buying 3 of a group deposits THREE separate objects into the buyer's vault, and
        /// a preflight that answered 1 would be undercounting - the forbidden direction, because it
        /// admits a purchase the cap should have refused.
        /// </summary>
        public bool CanReceive(uint buyerAccountId, uint? itemGuid, uint wcid, string classKey, int count, out MarketError error)
        {
            error = MarketError.None;

            var store = ResolveStore(buyerAccountId);

            if (store == null || !store.TryCheckReady(out _))
            {
                error = MarketError.VaultUnavailable;
                return false;
            }

            var newEntries = classKey != null
                ? NewEntriesForClassPurchase(count, AccountVaultStore.ClassStorageEnabled, HasClassLine(store, classKey))
                : NewEntriesForPurchase(itemGuid, count, itemGuid == null && HasLedgerRow(store, wcid));

            if (!store.HasRoomFor(newEntries))
            {
                error = MarketError.VaultFull;
                return false;
            }

            return true;
        }

        /// <summary>
        /// How many vault entries a delivery of this purchase could need, at worst. See
        /// <see cref="CanReceive"/> for the rule; split out for the same no-injection-seam reason
        /// <see cref="HoldsCount"/> is.
        ///
        /// The floor of 1 is not decoration: a count of zero never reaches here (Buy refuses it), and
        /// answering 0 for a stored sale would ask HasRoomFor whether a full vault has room for
        /// nothing, which it does.
        /// </summary>
        internal static int NewEntriesForPurchase(uint? itemGuid, int count, bool buyerHoldsLedgerRow)
        {
            if (itemGuid == null)
                return buyerHoldsLedgerRow ? 0 : 1;

            return count < 1 ? 1 : count;
        }

        /// <summary>
        /// The worst-case entry cost of delivering <paramref name="count"/> items of one CLASS line.
        ///
        /// With the counted tier ON at the buyer, every delivered item re-classifies into the buyer's
        /// class rows, and the cap counts class rows by DISPLAY GROUP (AccountVaultStore.AddsEntryLocked):
        /// the whole delivery lands on one line, which costs nothing if the buyer already draws it and
        /// one entry if not - even when the seller's line spanned several member rows, because those
        /// differ only below the display bucket.
        ///
        /// With the tier OFF, TryDeposit stores each item as an ordinary biota, and whether any of
        /// them would join an existing group depends on the items themselves, so this answers the
        /// conservative way the stored-biota arm of <see cref="NewEntriesForPurchase"/> does: one
        /// entry per item. It can refuse a purchase that would have been free, never admit one past
        /// the cap.
        /// </summary>
        internal static int NewEntriesForClassPurchase(int count, bool classStorageEnabled, bool buyerDrawsTheLine)
        {
            if (classStorageEnabled)
                return buyerDrawsTheLine ? 0 : 1;

            return count < 1 ? 1 : count;
        }

        /// <summary>Whether the buyer already draws the class line with this display id.</summary>
        private static bool HasClassLine(AccountVaultStore store, string classKey)
        {
            foreach (var entry in store.GetEntries(0, -1))
            {
                if (entry.Kind == VaultEntryKind.Class && entry.ClassDisplayId == classKey && entry.Count > 0)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Whether the buyer already draws a nonzero LEDGER row for this wcid, which is what makes a
        /// ledger delivery cost no new entry.
        ///
        /// A class row of the same wcid does NOT count, and must not: a ledger delivery lands on
        /// account_vault_stack, which is a different row that would still have to be created.
        /// </summary>
        private static bool HasLedgerRow(AccountVaultStore store, uint wcid)
        {
            foreach (var entry in store.GetEntries(0, -1))
            {
                if (entry.Kind == VaultEntryKind.Ledger && entry.Wcid == wcid && entry.Count > 0)
                    return true;
            }

            return false;
        }

        public bool TryTakeForSale(uint sellerAccountId, uint? itemGuid, uint wcid, string classKey, int count,
                                   MarketActor auditActor, out List<WorldObject> taken, out MarketError error)
        {
            taken = new List<WorldObject>();
            error = MarketError.None;

            var store = ResolveStore(sellerAccountId);

            if (store == null)
            {
                error = MarketError.VaultUnavailable;
                return false;
            }

            // A class listing resolves its LINE by display id and takes k items off it through the
            // ordinary TryWithdraw -> WithdrawFromClass path, so each object carries the same pooled
            // share a withdrawal would have handed out and the seller's pool drops by exactly that.
            var entry = FindEntry(store, itemGuid, wcid, classKey);

            if (entry == null)
            {
                error = MarketError.ItemNotFound;
                return false;
            }

            if (entry.Count < count)
            {
                error = MarketError.CountUnavailable;
                return false;
            }

            var amount = WithdrawAmountForSale(entry, count);

            // ANCHOR-STABLE TAKE. A group listing pins market_listing.item_Guid to the group's
            // representative, Members[0], and nothing rewrites that guid as the listing sells down.
            // Taking from the FRONT would hand the representative to the first buyer and leave an
            // Active listing naming a guid the seller no longer holds. Taking from the BACK removes it
            // only on the sale that empties the row, and that sale drives the listing's remaining
            // count to zero, where ApplySale closes it and clears both active keys.
            //
            // The invariant this buys: while a group listing is Active with Count > 0,
            // market_listing.item_Guid names a member that is still in the seller's vault and is still
            // that group's representative.
            //
            // SCOPE, so the invariant is not read as stronger than it is: SELLING never breaks it. A
            // seller DEPOSITING another equivalent item still can, because Container.TryAddToInventory
            // inserts at PlacementPosition 0 and the grouping order is that position, so the new item
            // becomes the representative. That is unchanged, pre-existing behaviour and it fails safe -
            // FindEntry then matches no row and the sale is refused with item_not_found rather than
            // delivering anything. Withdrawing through the seller's own panel delists the whole row
            // (the pre-withdraw hook), which is today's deliberate fail-safe and is out of scope here.
            var takeOrder = TakeOrderForSale(entry);

            if (!store.TryWithdraw(entry, amount, SellerActor(auditActor, sellerAccountId), out var withdrawn, out var failReason, takeOrder))
            {
                log.Warn($"[MARKET] TryWithdraw refused for seller account {sellerAccountId}, wcid {wcid}: {failReason}");
                error = failReason == AccountVaultStore.UnavailableMessage || failReason == AccountVaultStore.StillLoadingMessage
                    ? MarketError.VaultUnavailable
                    : MarketError.ListingNotActive;
                return false;
            }

            taken = withdrawn;
            return true;
        }

        /// <summary>
        /// How much a sale withdraws from the seller's vault, which depends on the entry KIND. All
        /// THREE kinds are spelled out here on purpose: this rule was written with only two of them in
        /// mind (ledger versus "a stored biota"), and the case it left out - a group - was a live
        /// over-delivery. A group row was treated as a single stored biota and handed over WHOLE, so a
        /// buyer paying for one salvage bag received every equivalent bag on that line.
        ///
        /// LEDGER: divisible units with no biota behind them, so the listed count is exactly what moves.
        ///
        /// GROUP: several separate WHOLE biotas drawn on one panel line.
        /// <see cref="VaultEntry.Count"/> is the number of MEMBERS, not a stack size, so taking it
        /// would hand over all of them for one item's price. A group takes the listed count in whole
        /// members; <see cref="AccountVaultStore.TryWithdraw"/> dispatches those to WithdrawGroup,
        /// which is all or nothing for exactly that many and refuses more than the row holds. A group
        /// listing may carry a count above 1 (MarketManager.List bounds it by the row's member count),
        /// so this is the one stored-biota shape where the listed count is the right amount.
        ///
        /// SINGLE STORED BIOTA: indivisible, and comes out WHOLE even when that is a stored stack of
        /// 200 listed as one thing - TryWithdraw refuses any other amount for one, so the entry's own
        /// count is the only amount it will accept. The listed count must NOT be used here: a listing
        /// over a LONE stored biota is pinned to 1 by MarketManager.List, and taking 1 would be
        /// refused.
        ///
        /// Internal rather than inlined so a test can exercise the distinction against a REAL
        /// AccountVaultStore, for the same reason <see cref="ClassifyDepositFailure"/> is - see its
        /// remarks for why AccountVaultManager.GetStore cannot be pointed at a test double.
        /// </summary>
        /// <remarks>
        /// A counted CLASS line is the fourth shape, and it takes the DIVISIBLE arm: its Count is a
        /// number of separate items, exactly like a group's, so the listed count is the right amount
        /// and handing over the whole line would be the over-delivery this method's remarks describe.
        /// TryWithdraw dispatches it to WithdrawFromClass, which drains its member rows front-first.
        /// </remarks>
        internal static int WithdrawAmountForSale(VaultEntry entry, int count)
            => entry.Kind == VaultEntryKind.StoredItem && !entry.IsGroup ? (int)entry.Count : count;

        /// <summary>
        /// Which end of a group row a sale takes from. See <see cref="TryTakeForSale"/> for the anchor
        /// argument; internal for the same reason <see cref="WithdrawAmountForSale"/> is, so a test can
        /// pair this decision with the real AccountVaultStore withdraw it dispatches rather than
        /// choosing an order of its own and proving nothing about the production one.
        /// </summary>
        internal static GroupTakeOrder TakeOrderForSale(VaultEntry entry)
            => entry.IsGroup ? GroupTakeOrder.Back : GroupTakeOrder.Front;

        /// <summary>
        /// Deposits <paramref name="items"/> in order and stops at the first failure, recording every
        /// item's outcome in <paramref name="delivery"/> as it goes.
        ///
        /// IT IS NOT ALL OR NOTHING, and it cannot be made so. An item deposited earlier in the batch is
        /// already the buyer's: a ledger or class deposit has credited the buyer's row and DESTROYED the
        /// carrier object, and a stored biota has been re-parented into the buyer's vault container.
        /// Returning either to the seller is a duplicate - the destroyed carrier is re-credited to the
        /// seller's ledger, or re-inserted into the seller's vault by a save queued after its own
        /// removal. So the caller turns a failure part way into a PARTIAL sale and returns only
        /// <see cref="MarketDelivery.Undelivered"/>.
        ///
        /// A THROW IS CAUGHT HERE, per item, because this is the one place that can still classify the
        /// item the throw interrupted (<see cref="ClassifyDeposit"/>). Letting it escape would leave
        /// the caller with an item it can neither prove delivered nor safely return.
        /// </summary>
        public bool TryGiveToBuyer(uint buyerAccountId, IReadOnlyList<WorldObject> items,
                                   MarketActor auditActor, MarketDelivery delivery, out MarketError error)
        {
            error = MarketError.None;

            if (delivery == null)
                throw new ArgumentNullException(nameof(delivery), "a give must record what it delivered; see MarketDelivery");

            var store = ResolveStore(buyerAccountId);

            if (store == null)
            {
                error = MarketError.VaultUnavailable;
                return false;
            }

            var actor = new VaultActor(buyerAccountId, auditActor.CharacterGuid, auditActor.Name);

            // ONE re-read for the whole delivery instead of one per item, and NOTHING ELSE changes.
            //
            // Every deposit below still runs at its own item's position, commits on its own, and is
            // classified on its own; the loop, its per-item throw handling and its per-item LastDepositCommit
            // reading are untouched, because those decide whether each item stays with the buyer or goes back
            // to the seller and a wrong answer there is a duplicate or a loss. What the window defers is only
            // the deposit's phase-D whole-account re-read, which the batched deposit path introduced as one
            // read per CALL - correct for a sale, which is one call for N items, and 50 whole-account reads on
            // the world thread for a 50-item delivery, which is N calls of one item. On the class table each
            // of those drags every class row's canonical_Form TEXT.
            //
            // The window's own re-read runs from the using's finally, so a return out of the loop below - and
            // there is one on every failure arm - still closes it exactly once. See AccountVaultStore's
            // BeginDepositWindow for what the deferral is allowed to assume.
            using (store.BeginDepositWindow())
            {
                return GiveToBuyerCore(store, buyerAccountId, items, actor, delivery, ref error);
            }
        }

        /// <summary>
        /// <see cref="TryGiveToBuyer"/>'s per-item loop, unchanged from when it was inlined there. Split out
        /// only so the coalescing deposit window can wrap it in a <c>using</c> without re-indenting - and
        /// therefore obscuring - the custody logic every one of its arms carries.
        /// </summary>
        private static bool GiveToBuyerCore(AccountVaultStore store, uint buyerAccountId, IReadOnlyList<WorldObject> items,
                                            VaultActor actor, MarketDelivery delivery, ref MarketError error)
        {
            foreach (var item in items)
            {
                delivery.Begin(item);

                bool deposited;
                string failReason;
                Exception thrown = null;

                try
                {
                    deposited = store.TryDeposit(item, actor, out failReason);
                }
                catch (Exception ex)
                {
                    deposited = false;
                    failReason = null;
                    thrown = ex;
                }

                var outcome = ClassifyDeposit(deposited, store.LastDepositCommit, item);

                if (outcome == DepositOutcome.Delivered)
                {
                    delivery.MarkDelivered(item);

                    if (thrown == null)
                        continue;

                    // Landed and THEN threw: the item is the buyer's, and the rest of the batch is not
                    // attempted, because the store is in whatever state the throw left it.
                    log.Error($"[MARKET] the deposit of 0x{item.Guid.Full:X8} into buyer account {buyerAccountId} threw AFTER it committed; it stays with the buyer and the rest of the batch is not attempted: {thrown.GetFullMessage()}");
                    error = MarketError.VaultUnavailable;
                    return false;
                }

                if (outcome == DepositOutcome.Ambiguous)
                {
                    delivery.MarkAmbiguous(item);
                    log.Error($"[MARKET] the deposit of 0x{item.Guid.Full:X8} into buyer account {buyerAccountId} has an UNKNOWN outcome ({(thrown != null ? "it threw: " + thrown.GetFullMessage() : failReason)}). It is being kept on the buyer's side and must not be returned to the seller.");
                    error = MarketError.VaultUnavailable;
                    return false;
                }

                delivery.MarkNotDelivered(item);

                if (thrown != null)
                {
                    log.Error($"[MARKET] the deposit of 0x{item.Guid.Full:X8} into buyer account {buyerAccountId} threw before it committed; it is still the caller's: {thrown.GetFullMessage()}");
                    error = MarketError.VaultUnavailable;
                    return false;
                }

                error = ClassifyDepositFailure(failReason);
                log.Error($"[MARKET] could not deposit 0x{item.Guid.Full:X8} into buyer account {buyerAccountId}: {failReason}. Classified as {error}. {delivery.Kept.Count} earlier item(s) in this batch already reached the buyer and stay there; the caller owns only the items not yet deposited.");
                return false;
            }

            return true;
        }

        /// <summary>Where one deposit left its item. See <see cref="ClassifyDeposit"/>.</summary>
        internal enum DepositOutcome
        {
            NotDelivered,
            Ambiguous,
            Delivered,
        }

        /// <summary>
        /// Decides from OBSERVABLE STATE whether an item handed to TryDeposit is now the buyer's, and
        /// resolves every doubt toward "do not return it", because a wrong return is a duplicate and a
        /// wrong charge is a refund an operator can make.
        ///
        /// Delivered, on any one of:
        /// - TryDeposit returned true;
        /// - the store's own commit marker says Committed (the credit landed, or the biota was added to
        ///   a vault container, and something after that threw);
        /// - the item carries a ContainerId. TryDeposit refuses an item that is still parented, so a
        ///   ContainerId now can only be the buyer's vault container;
        /// - the item IsDestroyed. The deposit arms destroy the carrier only after their credit has
        ///   committed, and nothing else in a give destroys the item handed in.
        ///
        /// Ambiguous when the commit marker says Unknown: a credit that reported Failed, or a throw from
        /// inside the credit call itself.
        ///
        /// NotDelivered only when none of the above holds and the marker says None - which TryDeposit
        /// resets before its first statement, so a stale marker from the previous item cannot leak in.
        /// </summary>
        internal static DepositOutcome ClassifyDeposit(bool deposited, VaultDepositCommit commit, WorldObject item)
        {
            if (deposited || commit == VaultDepositCommit.Committed)
                return DepositOutcome.Delivered;

            if (item != null && (item.ContainerId != null || item.IsDestroyed))
                return DepositOutcome.Delivered;

            return commit == VaultDepositCommit.Unknown ? DepositOutcome.Ambiguous : DepositOutcome.NotDelivered;
        }

        /// <summary>
        /// Turns an <see cref="AccountVaultStore.TryDeposit"/> failure reason into a MarketError. The
        /// vault-full refusal is not a transient condition like every other TryDeposit failure - the
        /// buyer must free a slot before buying, so it gets its own error and the client must not
        /// retry automatically. Everything else stays VaultUnavailable.
        ///
        /// Extracted to its own method (rather than inlined in <see cref="TryGiveToBuyer"/>) so a test
        /// can exercise this exact classification against a failReason produced by a REAL
        /// AccountVaultStore, without needing to point AccountVaultManager.GetStore at a test double -
        /// GetStore hardcodes the production ShardAccountVaultBackend and offers no injection seam.
        /// </summary>
        internal static MarketError ClassifyDepositFailure(string failReason)
            => AccountVaultStore.IsFullMessage(failReason) ? MarketError.VaultFull : MarketError.VaultUnavailable;

        public bool TryReturnToSeller(uint sellerAccountId, IReadOnlyList<WorldObject> items, uint wcid,
                                      bool asLedger, string classKey, MarketActor auditActor)
        {
            var store = ResolveStore(sellerAccountId);

            if (store == null)
            {
                log.Error($"[MARKET] LOST ITEM RISK: no vault store for seller account {sellerAccountId}; the caller still holds {items.Count} item(s) and must not destroy them.");
                return false;
            }

            if (classKey != null && asLedger)
            {
                // A caller bug with two opposite readings. Resolved toward the class, which is where a
                // class listing's objects came from; the ledger would credit a different holding.
                log.Error($"[MARKET] TryReturnToSeller was handed both asLedger and class line {classKey} for seller account {sellerAccountId}; returning to the class. This is a caller defect.");
            }

            var actor = SellerActor(auditActor, sellerAccountId);
            var allReturned = true;

            foreach (var item in items)
            {
                // A class-sourced object goes back INTO ITS CLASS, never to the stack ledger: the
                // ledger is a different holding, and its value would leave the seller's pool for good.
                var returned = classKey != null
                    ? store.TryReturnWithdrawnToClass(item, actor)
                    : asLedger
                        ? store.TryReturnWithdrawnToLedger(item, wcid, item.StackSize ?? 1, actor)
                        : store.TryReturnWithdrawn(item, actor);

                if (returned)
                    continue;

                allReturned = false;
                log.Error($"[MARKET] LOST ITEM RISK: could not return 0x{item.Guid.Full:X8} (wcid {wcid}) to seller account {sellerAccountId}. It must NEVER be destroyed; recover it manually from account_vault_log and market_transaction.");
            }

            return allReturned;
        }

        public int CountMatching(uint accountId, Func<WorldObject, bool> predicate)
        {
            var store = ResolveStore(accountId);
            if (store == null || predicate == null || !store.TryCheckReady(out _))
                return 0;
            return CountMatchingEntries(store.GetEntries(0, -1), predicate, store.World);
        }

        /// <summary>
        /// Split out, like HoldsCount, so a test can drive it without AccountVaultManager.GetStore -
        /// which is why the world source is a PARAMETER rather than resolved from the account inside.
        ///
        /// A LEDGER ROW COUNTS, and getting that wrong was a live defect rather than a refinement: a
        /// forge-fresh full salvage bag and a full-charge Hammer are both provably identical to their
        /// own weenie, so <see cref="ACE.Server.Entity.AccountVault.VaultCollapse"/> collapses them on
        /// deposit and the NORMAL case for an item a buy order wants is a ledger row with no biota
        /// behind it. Skipping those rows told a seller holding exactly the wanted item that they held
        /// none of it, and there is no way to fill from what cannot be counted.
        ///
        /// The row is tested through a PROBE - one fresh instance of its wcid, per
        /// <see cref="DescribeLedger"/>'s pattern - and that is sound precisely because collapse is a
        /// whole-biota diff against a fresh weenie: a ledger row is by construction a row of objects
        /// indistinguishable from the probe, so the probe's answer IS every unit's answer. It is
        /// probed once per DISTINCT wcid, because the answer cannot differ between two rows of one
        /// wcid and a probe costs an object construction and a destroy.
        /// </summary>
        internal static int CountMatchingEntries(IReadOnlyList<VaultEntry> entries, Func<WorldObject, bool> predicate,
                                                 IAccountVaultWorldSource world)
        {
            var total = 0;
            var probed = new Dictionary<uint, bool>();

            foreach (var entry in entries)
            {
                // A counted CLASS row does not participate in the market in v1, so it counts for
                // nothing here. Skipped EXPLICITLY rather than left to fall through the member loop
                // with an empty Members list: the two happen to produce the same number today, and one
                // of them says why.
                if (entry.Kind == VaultEntryKind.Class)
                    continue;

                if (entry.Kind == VaultEntryKind.Ledger)
                {
                    if (LedgerRowMatches(entry, predicate, world, probed))
                        total += LedgerUnitsAvailable(entry);

                    continue;
                }

                foreach (var member in entry.Members)
                    if (predicate(member)) total++;
            }
            return total;
        }

        /// <summary>
        /// Whether a fresh instance of this ledger row's wcid satisfies the predicate, memoized in
        /// <paramref name="probed"/> so one call probes each wcid once however many rows carry it.
        ///
        /// TWO THINGS MAKE A ROW COUNT, not one. The predicate has to pass, and the wcid has to be
        /// effectively NON-STACKABLE - MaxStackSize absent or 1. The second clause is what keeps "one
        /// unit" and "one object" the same number, and that identity is load bearing in both
        /// directions: <see cref="CountMatchingEntries"/> promises units while
        /// <see cref="TryTakeMatching"/> must deliver exactly <c>count</c> OBJECTS (its own final gate
        /// and MarketManager_Orders both test taken.Count), and a ledger withdraw of k units of a
        /// stackable wcid packs them into ceil(k / MaxStackSize) objects instead of k. Counting those
        /// units would promise a number the take could never deliver, and every fill would refuse
        /// after writing a Pending row.
        ///
        /// It excludes nothing orderable today: both order predicates require a Structure and its max
        /// (SalvageForge.IsEligibleBag / IsEligibleHammer), the retail salvage weenies carry
        /// MaxStackSize 1, and the fork's own full-bag and Hammer weenies declare MaxStackSize = 1
        /// explicitly - weenie_properties_int type 11, verified in Content/sql/weenies for 1003241
        /// (:20) and 1001910 (:82) and matching in every sibling bag and Hammer. A stackable ledger row
        /// therefore still contributes 0, exactly as every ledger row used to.
        ///
        /// A null world source, an unknown wcid and an empty row all answer false. The probe is
        /// destroyed in a finally, as DescribeLedger's is: leaking one guid per row per fill attempt
        /// would drain the dynamic range on a busy shard.
        /// </summary>
        private static bool LedgerRowMatches(VaultEntry entry, Func<WorldObject, bool> predicate,
                                             IAccountVaultWorldSource world, Dictionary<uint, bool> probed)
        {
            if (world == null || entry.Count <= 0)
                return false;

            if (probed.TryGetValue(entry.Wcid, out var cached))
                return cached;

            var matches = false;
            var probe = world.CreateNewWorldObject(entry.Wcid);

            if (probe != null)
            {
                try
                {
                    matches = (probe.MaxStackSize ?? 1) <= 1 && predicate(probe);
                }
                finally
                {
                    world.DestroyItem(probe);
                }
            }
            else
            {
                log.Warn($"[MARKET] could not instantiate wcid {entry.Wcid} to test a ledger row against an order predicate; the row counts as no match.");
            }

            probed[entry.Wcid] = matches;
            return matches;
        }

        /// <summary>
        /// A ledger row's units as an int. <see cref="VaultEntry.Count"/> is a long and a ledger row
        /// has no MaxStackSize ceiling on it, so a hoard larger than int.MaxValue is expressible;
        /// clamping is the only answer that cannot overflow into a negative count, and the take is
        /// bounded by what the order asks for anyway.
        /// </summary>
        private static int LedgerUnitsAvailable(VaultEntry entry)
            => entry.Count >= int.MaxValue ? int.MaxValue : (int)entry.Count;

        public bool TryTakeMatching(uint sellerAccountId, Func<WorldObject, bool> predicate, int count,
                                    MarketActor auditActor, out List<WorldObject> taken,
                                    out MarketTakeReceipt receipt, out MarketError error)
        {
            taken = new List<WorldObject>();
            error = MarketError.None;

            // Assigned up front so no early refusal can leave it unset, and re-issued for real only
            // where objects actually change hands. Every failure exit below empties `taken`, so Empty
            // is not a guess there - it is the truth about a list with nothing in it.
            receipt = MarketTakeReceipt.Empty;

            var store = ResolveStore(sellerAccountId);
            if (store == null) { error = MarketError.VaultUnavailable; return false; }
            if (predicate == null || count < 1) { error = MarketError.CountUnavailable; return false; }

            var actor = SellerActor(auditActor, sellerAccountId);
            var needed = count;
            var world = store.World;

            // Probe memo for the ledger rows, shared with nothing: one answer per wcid per call, as
            // CountMatchingEntries does. See LedgerRowMatches for why a probe answers for the row.
            var probed = new Dictionary<uint, bool>();

            // PROVENANCE, and the unwind cannot be correct without it. An object built by a ledger
            // withdraw was never counted against the entry cap (one ledger row is one entry however
            // many units it holds), so filing it back as a stored biota would turn one entry into as
            // many entries as there were units - AccountVaultStore.TryReturnWithdrawn says so in its
            // own remarks, and it is the exemption it tells callers not to generalize back out. The
            // objects are indistinguishable from a withdrawn stored biota once they are in hand, so
            // the only place the difference exists is here, at the withdraw that produced them.
            var ledgerSourced = new HashSet<uint>();

            // Snapshot the entry list once: TryWithdraw invalidates the store's grouping memo, and
            // walking a list the store is re-projecting under us would revisit or skip rows.
            var entries = store.GetEntries(0, -1).ToList();

            // The loop is wrapped because it withdraws MORE THAN ONCE, which no other market take does
            // (TryTakeForSale does exactly one, so it has no partial state to strand). A throw from the
            // second or later withdraw would otherwise propagate out through RunSerialized - which
            // deliberately rethrows - into SafeSerialized, which swallows it and reports false; the
            // caller then takes its "the region never ran" branch, which does NOT unwind, and every
            // item already in `taken` is out of the seller's vault, in no vault at all, referenced by
            // nothing and named in no log. Unwinding here runs while the region is still open, which
            // is the only place TryReturnToSeller can be called without nesting one.
            //
            // GetEntries above is deliberately OUTSIDE: a throw there has taken nothing, so the
            // existing generic handling is already correct for it.
            try
            {
                foreach (var entry in entries)
                {
                    if (needed == 0) break;

                    // A counted CLASS row does not participate in the market in v1, so a buy order
                    // never fills from one. Matching CountMatchingEntries exactly, which is what keeps
                    // the promised count and the delivered count the same number.
                    if (entry.Kind == VaultEntryKind.Class)
                        continue;

                    int amount;

                    if (entry.Kind == VaultEntryKind.Ledger)
                    {
                        // The probe is a CHEAP FILTER and nothing more - it decides whether to open
                        // this row at all. Authority still rests with the per-item re-check below,
                        // exactly as it does for a group's members.
                        if (!LedgerRowMatches(entry, predicate, world, probed)) continue;

                        // Units, and one unit is one object here (LedgerRowMatches refuses a stackable
                        // wcid for that reason), so this never asks for more objects than are needed.
                        amount = Math.Min(needed, LedgerUnitsAvailable(entry));
                    }
                    else
                    {
                        // Every member re-checked individually, never assumed from the representative.
                        var matching = entry.Members.Count(predicate);
                        if (matching == 0) continue;

                        // A lone stored biota comes out whole (WithdrawAmountForSale's rule); a group takes k members from the BACK.
                        amount = entry.IsGroup ? Math.Min(needed, matching) : (int)entry.Count;
                    }

                    var order = TakeOrderForSale(entry);

                    if (!store.TryWithdraw(entry, amount, actor, out var withdrawn, out var failReason, order))
                    {
                        log.Warn($"[MARKET] TryTakeMatching: TryWithdraw refused for seller account {sellerAccountId}: {failReason}");
                        continue;
                    }

                    foreach (var item in withdrawn)
                    {
                        taken.Add(item);

                        // Recorded BEFORE the predicate re-check, so an object the re-check rejects is
                        // still returned the way it came out. The unwind's correctness must not depend
                        // on the predicate agreeing with the probe.
                        if (entry.Kind == VaultEntryKind.Ledger) ledgerSourced.Add(item.Guid.Full);

                        if (needed > 0 && predicate(item)) needed--;
                    }
                }
            }
            catch (Exception ex)
            {
                log.Error($"[MARKET] TryTakeMatching threw after taking {taken.Count} item(s) for seller account {sellerAccountId}: {ex.GetFullMessage()}");

                UnwindToSeller(sellerAccountId, taken, new MarketTakeReceipt(ledgerSourced), auditActor, "after a throw");

                taken = new List<WorldObject>();

                // Not NoMatchingItems: the seller's holdings are unknown after a throw, and telling
                // them they hold nothing would be a guess. VaultUnavailable is the honest answer.
                error = MarketError.VaultUnavailable;
                return false;
            }

            receipt = new MarketTakeReceipt(ledgerSourced);

            if (needed > 0 || taken.Count != count)
            {
                // Shortfall (or a whole-row take that overshot): put EVERYTHING back, inside this same
                // region. TryReturnToSeller is a plain method, not a region-opener, so this cannot nest.
                UnwindToSeller(sellerAccountId, taken, receipt, auditActor, "on a shortfall");

                taken = new List<WorldObject>();
                receipt = MarketTakeReceipt.Empty;
                error = MarketError.NoMatchingItems;
                return false;
            }

            return true;
        }

        /// <summary>
        /// The caller-facing undo of a <see cref="TryTakeMatching"/>, per
        /// <see cref="IMarketItemStore.UndoTake"/>. It is deliberately the SAME method the take's own
        /// internal unwind uses rather than a second copy of the split: two implementations of "put
        /// each object back the way it came out" would drift, and the shape they would drift into is
        /// the one this whole receipt exists to prevent.
        /// </summary>
        public bool UndoTake(uint sellerAccountId, IReadOnlyList<WorldObject> items, MarketTakeReceipt receipt,
                             MarketActor auditActor)
            => UnwindToSeller(sellerAccountId, items, receipt, auditActor, "undoing a take");

        /// <summary>
        /// Puts a take back, and SURVIVES the return itself failing. The single unwind path for both of
        /// <see cref="TryTakeMatching"/>'s failure exits AND for <see cref="UndoTake"/>, which is how a
        /// caller unwinds a take that succeeded and could not then be delivered - all three go through
        /// this one body so they cannot drift apart.
        ///
        /// THE INNER CATCH IS LOAD BEARING - DO NOT REMOVE IT. TryReturnToSeller calls
        /// AccountVaultStore.TryReturnWithdrawn directly and has no catch of its own, and that method
        /// THROWS; <see cref="ACE.Server.Entity.AccountVault.VaultPackDelivery.TryReturnWithdrawn"/>
        /// (the facet restore's return path) wraps the identical pair of calls for exactly this reason, and
        /// records that the vault's state may be INCONSISTENT afterwards rather than merely unchanged.
        ///
        /// It matters most on the throwing exit, whose whole trigger is a withdraw failing on a shard
        /// write: the unwind is a shard write to the same store, in the same region, microseconds
        /// later, so a CORRELATED failure is the most likely shape this path will ever see rather than
        /// the least. An escaping throw would propagate through RunSerialized, be swallowed by
        /// SafeSerialized into a bare false, and land the caller in its "the seller's region never ran"
        /// branch, which does not unwind - re-creating the exact stranding this unwind exists to
        /// prevent, and leaving `taken` (an out parameter, written through a byref) holding items the
        /// caller believes it never received.
        ///
        /// Nothing can put the bags back once the vault refuses them. What this guarantees is that the
        /// loss is REPORTED, with every guid in the LOST ITEM RISK line, and that the caller gets a
        /// refusal it can act on instead of an exception it cannot.
        ///
        /// IT SPLITS BY PROVENANCE, and the split is not tidiness. An object that came out of a LEDGER
        /// row goes back to the ledger; one that came out of a container goes back to a container. The
        /// two undos are not interchangeable in either direction, and both mistakes are silent:
        /// TryReturnWithdrawn skips the entry cap on the reasoning that a stored biota was already
        /// counted while it sat there, which is false for a ledger-derived object and turns one entry
        /// into one entry PER UNIT (that method's own remarks say so and tell callers not to
        /// generalize the exemption out); while TryReturnWithdrawnToLedger DESTROYS the object it is
        /// given and credits units in its place, which for a real stored biota would be exactly the
        /// item degradation VaultCollapse exists to prevent.
        ///
        /// The ledger half is grouped by the object's OWN wcid rather than by any wcid the caller
        /// supplies: a predicate matches on material and shape, not on class id, so one take can span
        /// several wcids and crediting them all to one row would move units between items.
        /// </summary>
        private bool UnwindToSeller(uint sellerAccountId, IReadOnlyList<WorldObject> taken, MarketTakeReceipt receipt,
                                    MarketActor auditActor, string why)
        {
            if (taken == null || taken.Count == 0)
                return true;

            if (receipt == null)
            {
                // A caller bug, and it is resolved toward the mistake that can be undone. Filing a
                // ledger-derived object as a stored biota bypasses the entry cap and leaves units that
                // cannot re-collapse, which a player still holds and an operator can reconcile;
                // crediting a real stored biota to the ledger destroys it outright. So an unknown
                // provenance is treated as stored, and the bug is made loud rather than quiet.
                log.Error($"[MARKET] a take for seller account {sellerAccountId} is being unwound {why} with NO receipt, so provenance is unknown. Every item is being filed as a stored biota, which is the direction that never destroys one. The receipt from TryTakeMatching must be handed back to UndoTake - this is a caller defect.");
                receipt = MarketTakeReceipt.Empty;
            }

            var returned = false;

            try
            {
                var stored = taken.Where(i => !receipt.IsLedgerSourced(i)).ToList();

                // Non-short-circuiting on purpose, all the way down: one refused group must not stop
                // the rest from going back, and the false still reaches the LOST ITEM RISK line below.
                var allReturned = true;

                if (stored.Count > 0)
                {
                    // The wcid is unused on this branch (TryReturnWithdrawn reads the item's own), but
                    // passing a real one rather than 0 keeps the argument honest if it ever is used.
                    // classKey null: TryTakeMatching never draws on a class row, so nothing it took is
                    // class-sourced.
                    allReturned &= TryReturnToSeller(sellerAccountId, stored, stored[0].WeenieClassId, asLedger: false, classKey: null, auditActor);
                }

                foreach (var group in taken.Where(receipt.IsLedgerSourced).GroupBy(i => i.WeenieClassId))
                {
                    allReturned &= TryReturnToSeller(sellerAccountId, group.ToList(), group.Key, asLedger: true, classKey: null, auditActor);
                }

                returned = allReturned;
            }
            catch (Exception ex)
            {
                log.Error($"[MARKET] the unwind of a take ITSELF threw returning {taken.Count} item(s) to seller account {sellerAccountId} {why}. The vault's state may be INCONSISTENT and needs manual reconciliation against account_vault_log: {ex.GetFullMessage()}");
            }

            // A throw is reported as a loss too: TryReturnToSeller returns items one at a time, so a
            // throw part way leaves an unknown split and every guid below is the honest superset.
            if (!returned)
                log.Error($"[MARKET] LOST ITEM RISK unwinding a take: could not return {taken.Count} item(s) to seller account {sellerAccountId} {why}: {string.Join(", ", taken.Select(i => $"0x{i.Guid.Full:X8}"))}");

            return returned;
        }

        public bool TryBarrel(uint accountId, uint? itemGuid, uint wcid, string classKey, int count, MarketActor auditActor, out MarketError error)
        {
            error = MarketError.None;

            var store = ResolveStore(accountId);

            if (store == null)
            {
                error = MarketError.VaultUnavailable;
                return false;
            }

            var entry = FindEntry(store, itemGuid, wcid, classKey);

            if (entry == null)
            {
                error = MarketError.ItemNotFound;
                return false;
            }

            if (entry.Count < count)
            {
                error = MarketError.CountUnavailable;
                return false;
            }

            // The SAME amount rule a sale uses, deliberately shared rather than re-derived: a ledger
            // row and a group row take the asked-for count, while a lone stored biota is indivisible
            // and comes out whole even when it is a stack of 200. AccountVaultStore.TryBarrel refuses
            // any other amount for one, exactly as TryWithdraw does.
            var amount = WithdrawAmountForSale(entry, count);

            // The account is its own actor here, so the vault's owner branch authorizes it. A barrel
            // is never performed on someone else's behalf.
            var actor = new VaultActor(accountId, auditActor.CharacterGuid, auditActor.Name);

            if (store.TryBarrel(entry, amount, actor, out var failReason))
                return true;

            error = ClassifyBarrelFailure(failReason);

            log.Warn($"[MARKET] TryBarrel refused for account {accountId}, wcid {wcid}: {failReason}. Classified as {error}.");

            return false;
        }

        /// <summary>
        /// Turns an <see cref="AccountVaultStore.TryBarrel"/> failure reason into a MarketError,
        /// extracted for the same reason <see cref="ClassifyDepositFailure"/> is: a test can then
        /// exercise this exact mapping against strings a REAL AccountVaultStore produced, without
        /// pointing AccountVaultManager.GetStore at a double.
        ///
        /// Everything unrecognised falls to VaultUnavailable rather than ServerError, because every
        /// remaining refusal the store can produce - "that is no longer in your vault", a permission
        /// message - is a condition of that account's vault rather than a fault in the server.
        /// </summary>
        internal static MarketError ClassifyBarrelFailure(string failReason)
        {
            if (failReason == AccountVaultStore.ItemListedMessage)
                return MarketError.ItemListed;

            if (failReason == AccountVaultStore.BarrelUnavailableMessage || failReason == AccountVaultStore.BarrelFullMessage)
                return MarketError.BarrelUnavailable;

            return MarketError.VaultUnavailable;
        }

        /// <summary>
        /// The projection for a collapsed ledger stack, which has no biota to read. Builds a probe through
        /// the account's own world source and destroys it again, as the ledger withdraw path does.
        /// Takes the account id because GetStore(0) returns null by design.
        /// </summary>
        public ListingSnapshot DescribeLedger(uint accountId, uint wcid)
        {
            var store = ResolveStore(accountId);
            var world = store?.World;

            if (world == null)
                return new ListingSnapshot { Wcid = wcid, Name = $"Item {wcid}" };

            var probe = world.CreateNewWorldObject(wcid);

            if (probe == null)
                return new ListingSnapshot { Wcid = wcid, Name = $"Item {wcid}" };

            try
            {
                return MarketSnapshot.FromLedgerProbe(probe, wcid);
            }
            finally
            {
                world.DestroyItem(probe);
            }
        }

        /// <summary>
        /// The projection for a counted CLASS line, which has no biota to read either: the line's
        /// REPRESENTATIVE payload is materialized with the line's pooled share as its Value - exactly
        /// what PersonalVendor draws for the same line - projected through MarketSnapshot.FromItem, and
        /// destroyed again in a finally, as <see cref="DescribeLedger"/> destroys its probe. Leaking one
        /// guid per listing or per vault view would drain the dynamic range on a busy shard.
        ///
        /// Falls back to a name-only snapshot (the line's own Name override, else "Item wcid") when the
        /// payload cannot be read or built, and never throws, so one bad row cannot fail a whole vault
        /// view.
        /// </summary>
        public ListingSnapshot DescribeClass(uint accountId, VaultEntry entry) => DescribeClass(accountId, entry, out _);

        /// <summary>
        /// <see cref="DescribeClass(uint, VaultEntry)"/>, reporting whether the snapshot came from a real
        /// build (<paramref name="built"/> true) or is the name-only fallback, so a caller that caches
        /// never pins a fallback produced by a transient failure.
        /// </summary>
        public ListingSnapshot DescribeClass(uint accountId, VaultEntry entry, out bool built)
        {
            built = false;

            if (entry == null)
                return new ListingSnapshot { Name = "Item 0" };

            var fallback = new ListingSnapshot { Wcid = entry.Wcid, Name = entry.DisplayName ?? $"Item {entry.Wcid}" };

            var world = ResolveStore(accountId)?.World;

            if (world == null || entry.Kind != VaultEntryKind.Class)
                return fallback;

            if (!VaultItemClass.TryParseCanonicalForm(entry.CanonicalForm, out var classWcid, out var overrides, out _) || classWcid != entry.Wcid)
            {
                log.Error($"[MARKET] could not read back the canonical form of class {entry.ClassKey} (line {entry.ClassDisplayId}, wcid {entry.Wcid}, account {accountId}); describing it by name only.");
                return fallback;
            }

            WorldObject display = null;

            try
            {
                display = world.MaterializeClass(entry.Wcid, overrides, AccountVaultStore.PooledShare(entry.TotalValue, entry.Count));

                if (display == null)
                    return fallback;

                var snapshot = MarketSnapshot.FromItem(display);

                if (string.IsNullOrEmpty(snapshot.Name))
                    snapshot.Name = fallback.Name;

                built = true;
                return snapshot;
            }
            catch (Exception ex)
            {
                log.Error($"[MARKET] could not describe class line {entry.ClassDisplayId} (wcid {entry.Wcid}, account {accountId}); describing it by name only: {ex.GetFullMessage()}");
                return fallback;
            }
            finally
            {
                if (display != null)
                {
                    try
                    {
                        world.DestroyItem(display);
                    }
                    catch (Exception ex)
                    {
                        log.Error($"[MARKET] could not destroy the display object for class line {entry.ClassDisplayId}; a guid has been leaked: {ex.GetFullMessage()}");
                    }
                }
            }
        }

        /// <summary>
        /// The suit-builder projection of one CLASS line, built from the same materialized display object
        /// <see cref="DescribeClass(uint, VaultEntry, out bool)"/> uses (so a class's overrides, e.g. a rename,
        /// are what the projector sees). Null when the line cannot be built right now - the caller then falls
        /// back to the template weenie. Never throws.
        /// </summary>
        public Suit.SuitItem DescribeClassForSuit(uint accountId, VaultEntry entry, string source)
        {
            if (entry == null || entry.Kind != VaultEntryKind.Class)
                return null;

            var world = ResolveStore(accountId)?.World;

            if (world == null)
                return null;

            if (!VaultItemClass.TryParseCanonicalForm(entry.CanonicalForm, out var classWcid, out var overrides, out _) || classWcid != entry.Wcid)
                return null;

            WorldObject display = null;

            try
            {
                display = world.MaterializeClass(entry.Wcid, overrides, AccountVaultStore.PooledShare(entry.TotalValue, entry.Count));

                if (display == null)
                    return null;

                if (!Suit.SuitItemProjector.IsSuitRelevant(display))
                    return null;

                return Suit.SuitItemProjector.FromLedgerProbe(display, entry.Wcid, entry.ClassDisplayId, (int)Math.Min(entry.Count, int.MaxValue), source);
            }
            catch (Exception ex)
            {
                log.Error($"[MARKET] could not project class line {entry.ClassDisplayId} (wcid {entry.Wcid}, account {accountId}) for the suit builder: {ex.GetFullMessage()}");
                return null;
            }
            finally
            {
                if (display != null)
                {
                    try
                    {
                        world.DestroyItem(display);
                    }
                    catch (Exception ex)
                    {
                        log.Error($"[MARKET] could not destroy the display object for class line {entry.ClassDisplayId}; a guid has been leaked: {ex.GetFullMessage()}");
                    }
                }
            }
        }

        /// <summary>
        /// The seller's own actor, so the vault's owner branch authorizes the withdraw. Never takes the
        /// audit actor's account: an actor whose account does not match the vault is denied by design.
        /// </summary>
        private static VaultActor SellerActor(MarketActor listingSeller, uint sellerAccountId)
            => new VaultActor(sellerAccountId, listingSeller.CharacterGuid, listingSeller.Name);

        /// <summary>
        /// The vault row a listing transacts against, or null.
        ///
        /// A CLASS LISTING (<paramref name="classKey"/> non-null) finds the class LINE whose display id
        /// it names, and nothing else - the wcid and guid are not consulted, so a ledger row or another
        /// line of the same wcid can never answer for it.
        ///
        /// EVERY OTHER LISTING NEVER FINDS A CLASS ROW. This method hands a row to a real withdraw, so
        /// letting a class row answer a ledger or stored-item listing would sell a holding nobody
        /// listed. The guid arm tests the kind EXACTLY rather than "not a ledger row", because a class
        /// row's Guid is ObjectGuid.Invalid and a caller that passed 0 would otherwise match it.
        ///
        /// This is a near-exact duplicate of MarketManager.FindEntry, and the duplication is live: a
        /// rule changed in one of them silently misses the other. Both were changed together, and
        /// <see cref="MatchesEntry"/> is now the ONE predicate both call.
        /// </summary>
        private static VaultEntry FindEntry(AccountVaultStore store, uint? itemGuid, uint wcid, string classKey)
        {
            foreach (var entry in store.GetEntries(0, -1))
            {
                if (MatchesEntry(entry, itemGuid, wcid, classKey))
                    return entry;
            }

            return null;
        }

        /// <summary>
        /// Whether <paramref name="entry"/> is the vault row a listing of (itemGuid, wcid, classKey)
        /// names. The single rule behind BOTH FindEntry copies (this class's and MarketManager's), so
        /// the two can no longer drift apart.
        /// </summary>
        internal static bool MatchesEntry(VaultEntry entry, uint? itemGuid, uint wcid, string classKey)
        {
            if (entry == null)
                return false;

            if (classKey != null)
                return entry.Kind == VaultEntryKind.Class && entry.ClassDisplayId == classKey;

            if (entry.Kind == VaultEntryKind.Class)
                return false;

            if (itemGuid != null)
                return entry.Kind == VaultEntryKind.StoredItem && entry.Guid.Full == itemGuid.Value;

            return entry.Kind == VaultEntryKind.Ledger && entry.Wcid == wcid;
        }
    }
}
