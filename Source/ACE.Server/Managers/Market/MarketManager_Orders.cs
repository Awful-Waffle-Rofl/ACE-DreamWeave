using System;
using System.Collections.Generic;
using System.Linq;

using log4net;

using ACE.Server.Managers.Analytics;
using ACE.Server.WorldObjects;

using MaterialType = ACE.Entity.Enum.MaterialType;
using ShardMarketBuyOrder = ACE.Database.Models.Shard.MarketBuyOrder;
using ShardMarketTransaction = ACE.Database.Models.Shard.MarketTransaction;

namespace ACE.Server.Managers.Market
{
    /// <summary>
    /// Wanted buy orders (WANTED-DESIGN section 6): place, fill, cancel, expiry and boot recovery.
    ///
    /// THE PENDING ROW IS WRITTEN BEFORE ANY VALUE MOVES, in both placement and fill, so a process
    /// that dies mid-operation leaves a row boot recovery can act on rather than money that vanished.
    /// THE SELLER'S AND THE BUYER'S VAULT REGIONS ARE NEVER NESTED - two separate SafeSerialized
    /// calls, exactly as Buy does, because nesting lets "A fills B's order" deadlock against
    /// "B fills A's order".
    /// A FILL'S TRANSACTION ROW COMPLETES THE INSTANT CUSTODY CHANGES and nothing after that point
    /// unwinds: a failed seller credit is a logged CREDIT LOST, never a rollback, because the buyer
    /// already holds the bags.
    /// </summary>
    public static partial class MarketManager
    {
        private static readonly ILog orderLog = LogManager.GetLogger("MarketOrders");

        private static readonly Dictionary<uint, MarketBuyOrder> buyOrders = new Dictionary<uint, MarketBuyOrder>();

        /// <summary>
        /// (buyer account, material, KIND) triples a placement is CURRENTLY BUILDING, guarded by
        /// indexLock and held for the whole of <see cref="PlaceOrder"/>'s steps 1 to 4.
        ///
        /// Uniqueness (WANTED-DESIGN 3) is one Active order per account, material AND kind - a buyer
        /// may want bags and Hammers of the same material at once, at very different prices - and the
        /// index alone cannot enforce it: the order is not published until after an insert, a debit and
        /// an update, so two concurrent placements both read the index and both see nothing. Nor can
        /// the database backstop it, which the design assumed it would - the Pending row is inserted
        /// with active_Material NULL and NULLs are distinct under
        /// UNIQUE (buyer_Account_Id, active_Material, order_Kind), so the key can only fire on the
        /// step-3 UPDATE, by which point the buyer has been debited.
        ///
        /// THE KIND IS PART OF THE CLAIM, not just of the check. Dropping it here would collapse the
        /// two kinds back into one claim and refuse a legitimate second order; keeping it in only the
        /// check and not the claim would let two concurrent HAMMER placements both proceed.
        ///
        /// The claim makes the uniqueness statement span the whole placement rather than one instant.
        /// It is taken under the SAME lock acquisition as the existence check and released in a
        /// finally, exactly as a fill's in-flight reservation is.
        /// </summary>
        private static readonly HashSet<(uint Account, int Material, MarketBuyOrderKind Kind)> placementsInFlight
            = new HashSet<(uint, int, MarketBuyOrderKind)>();

        /// <summary>
        /// Set when the boot read of market_buy_order FAILED (WANTED-DESIGN 6.5). The market stays up -
        /// place and fill refuse server_error until a restart, because an empty order index cannot be
        /// told from a real one, and the rest of the market is unaffected.
        /// </summary>
        private static volatile bool buyOrdersReadFailed;

        private static bool OrdersEnabled => Enabled && PropertyManager.GetBool("market_buy_orders_enabled", false).Item;

        /// <summary>Called from Initialize(store, wallet, repo) after the history read, under no lock.</summary>
        private static void LoadOrders(IMarketRepository marketRepository)
        {
            var rows = marketRepository.GetAllBuyOrders();

            if (rows == null)
            {
                buyOrdersReadFailed = true;
                orderLog.Error("[MARKET] could not read market_buy_order; the market boots WITHOUT orders. Place and fill refuse server_error until a restart; the rest of the market is unaffected.");
                return;
            }

            lock (indexLock)
            {
                foreach (var row in rows.OrderBy(r => r.Id))
                {
                    var order = FromRow(row);
                    order.Seq = ++changeSequence;
                    buyOrders[order.Id] = order;
                }
            }

            orderLog.Info($"[MARKET] {rows.Count} buy order row(s) loaded.");
        }

        // ---- row converters ----

        /// <summary>
        /// order_Kind off a row, narrowed to a DEFINED <see cref="MarketBuyOrderKind"/> member.
        ///
        /// Deliberately NOT Enum.IsDefined, which throws on some inputs and boxes on all of them for a
        /// two-member enum; an explicit list is cheaper and cannot surprise. Anything else is logged at
        /// Error with the order id, because an out-of-range kind means a row this build did not write
        /// and an operator needs to see it, then read as SalvageBag - see FromRow for why that is the
        /// safe direction rather than a guess.
        /// </summary>
        private static MarketBuyOrderKind ClampOrderKind(byte value, uint orderId)
        {
            switch (value)
            {
                case (byte)MarketBuyOrderKind.SalvageBag:    return MarketBuyOrderKind.SalvageBag;
                case (byte)MarketBuyOrderKind.SalvageHammer: return MarketBuyOrderKind.SalvageHammer;

                default:
                    orderLog.Error($"[MARKET] order {orderId} carries order_Kind {value}, which this build does not define. Reading it as a salvage bag order, which is the restrictive answer; a Hammer can never fill it. Fix the row by hand.");
                    return MarketBuyOrderKind.SalvageBag;
            }
        }

        private static MarketBuyOrder FromRow(ShardMarketBuyOrder row)
        {
            // The kind decides which weenie the display fields describe, so it is resolved FIRST and
            // the lookup is given it. Reading the bag entry for a hammer order would put a bag's name
            // and icon on the wire for an order only a Hammer can fill.
            //
            // CLAMPED, not cast. order_Kind is a tinyint the database will happily hold 2 in - a hand
            // edit, or a row written by a newer server and then rolled back. An out-of-range value
            // would never be FILLED (MatchesOrder's default matches nothing), but it would still be
            // SERVED: Dto hands the enum to JsonStringEnumConverter, which emits a NUMBER when no name
            // matches, so the orders feed would answer "kind": 2 where the schema declares a required
            // string and a strictly typed client would throw away the whole page rather than one row.
            // Reading an unknown kind as SalvageBag is the safe landing: a bag order can never be
            // filled by a Hammer, so the worst case is an order that shows and fills as the more
            // restrictive of the two kinds.
            var kind = ClampOrderKind(row.OrderKind, row.Id);

            MarketSalvageMaterials.TryGet(row.MaterialType, kind, out var material);
            return new MarketBuyOrder
            {
                Id = row.Id, BuyerAccountId = row.BuyerAccountId, BuyerCharacterGuid = row.BuyerCharacterGuid,
                BuyerCharacterName = row.BuyerCharacterName, MaterialType = row.MaterialType, Kind = kind,
                MaterialName = material?.MaterialName ?? ((MaterialType)row.MaterialType).ToString(),
                Wcid = row.Wcid, BagName = material?.BagName ?? $"Item {row.Wcid}",
                PriceMmd = row.PriceMmd, CountTotal = row.CountTotal, CountRemaining = row.CountRemaining,
                EscrowMmd = row.EscrowMmd, Status = (MarketBuyOrderStatus)row.Status,
                CreatedAt = row.CreatedAt, ExpiresAt = row.ExpiresAt, ClosedAt = row.ClosedAt,
                IconId = material?.IconId ?? 0, IconOverlayId = material?.IconOverlayId, IconUnderlayId = material?.IconUnderlayId,
            };
        }

        private static ShardMarketBuyOrder ToRow(MarketBuyOrder o) => new ShardMarketBuyOrder
        {
            Id = o.Id, BuyerAccountId = o.BuyerAccountId, BuyerCharacterGuid = o.BuyerCharacterGuid,
            BuyerCharacterName = o.BuyerCharacterName, MaterialType = o.MaterialType, OrderKind = (byte)o.Kind, Wcid = o.Wcid,
            PriceMmd = o.PriceMmd, CountTotal = o.CountTotal, CountRemaining = o.CountRemaining, EscrowMmd = o.EscrowMmd,
            Status = (int)o.Status, ActiveMaterial = o.Status == MarketBuyOrderStatus.Active ? (int?)o.MaterialType : null,
            CreatedAt = o.CreatedAt, ExpiresAt = o.ExpiresAt, ClosedAt = o.ClosedAt,
        };

        /// <summary>Feeds hand back COPIES (see Copy(MarketListing)). InFlight is deliberately NOT copied.</summary>
        private static MarketBuyOrder Copy(MarketBuyOrder o) => new MarketBuyOrder
        {
            Id = o.Id, BuyerAccountId = o.BuyerAccountId, BuyerCharacterGuid = o.BuyerCharacterGuid,
            BuyerCharacterName = o.BuyerCharacterName, MaterialType = o.MaterialType, MaterialName = o.MaterialName,
            Kind = o.Kind,
            Wcid = o.Wcid, BagName = o.BagName, PriceMmd = o.PriceMmd, CountTotal = o.CountTotal,
            CountRemaining = o.CountRemaining, EscrowMmd = o.EscrowMmd, Status = o.Status,
            CreatedAt = o.CreatedAt, ExpiresAt = o.ExpiresAt, ClosedAt = o.ClosedAt,
            IconId = o.IconId, IconOverlayId = o.IconOverlayId, IconUnderlayId = o.IconUnderlayId, Seq = o.Seq,
        };

        public static MarketBuyOrder GetBuyOrder(uint orderId)
        {
            lock (indexLock)
                return buyOrders.TryGetValue(orderId, out var o) ? Copy(o) : null;
        }

        public static IReadOnlyList<MarketBuyOrder> GetActiveBuyOrdersForAccount(uint accountId)
        {
            lock (indexLock)
                return buyOrders.Values.Where(o => o.BuyerAccountId == accountId && o.Status == MarketBuyOrderStatus.Active)
                                       .OrderBy(o => o.Id).Select(Copy).ToList();
        }

        /// <summary>Orders feed, same cursor contract as GetListingChanges: nextSeq is the LAST ROW RETURNED.</summary>
        public static IReadOnlyList<MarketBuyOrder> GetOrderChanges(long sinceSeq, int max, out long nextSeq)
        {
            if (max < 1 || max > MaxFeedRows) max = MaxFeedRows;
            lock (indexLock)
            {
                var page = buyOrders.Values.Where(o => o.Seq > sinceSeq).OrderBy(o => o.Seq).Take(max).Select(Copy).ToList();
                nextSeq = page.Count > 0 ? page[page.Count - 1].Seq : changeSequence;
                return page;
            }
        }

        private static long ReadLongClamped(string key, long fallback, long min, long max)
        {
            var value = PropertyManager.GetLong(key, fallback).Item;
            return value < min ? min : value > max ? max : value;
        }

        // ---- place (WANTED-DESIGN 6.1) ----

        private static MarketResult<MarketBuyOrder> RefusePlaceOrder(MarketError error, MarketChannel channel, MarketActor buyer,
                                                                     int materialType, MarketBuyOrderKind kind, int count, long priceMmd, string detail = null)
        {
            // listingId NULL (the column is nullable; NULL already means "named no listing"), the material in wcid's slot would lie, so wcid 0 and the material in detail.
            // The KIND rides in detail for the same reason the material does: an audit row that reads
            // "material 43" cannot tell a refused bag order from a refused hammer order at the same price.
            var what = $"material {materialType} ({kind.ToString().ToLowerInvariant()})";

            MarketRejectionLog.Record(MarketRejectOperation.PlaceOrder, error, channel, buyer, null, null, 0, count, priceMmd,
                                      detail == null ? what : $"{what}; {detail}");
            return MarketResult<MarketBuyOrder>.Fail(error);
        }

        /// <summary>
        /// Posts a Wanted order. <paramref name="kind"/> is REQUIRED rather than defaulted: it decides
        /// what fills the order and therefore what the escrow is being spent on, and a caller that
        /// silently got SalvageBag would have bought the wrong thing with the buyer's money.
        /// </summary>
        public static MarketResult<MarketBuyOrder> PlaceOrder(MarketActor buyer, int materialType, MarketBuyOrderKind kind,
                                                              int count, long priceMmd, MarketChannel channel)
        {
            if (!OrdersEnabled)
                return MarketResult<MarketBuyOrder>.Fail(Enabled ? MarketError.BuyOrdersDisabled : MarketError.Disabled);   // NOT recorded: operator state

            if (buyOrdersReadFailed)
                return RefusePlaceOrder(MarketError.ServerError, channel, buyer, materialType, kind, count, priceMmd, "the order index failed to load at boot");

            if (buyer.AccountId == 0 || buyer.CharacterGuid == 0)
                return RefusePlaceOrder(MarketError.NotOwner, channel, buyer, materialType, kind, count, priceMmd);

            // An unknown kind is refused BEFORE the material lookup, so a future kind whose predicate
            // does not exist yet can never take a buyer's money for an order nothing could ever fill.
            if (kind != MarketBuyOrderKind.SalvageBag && kind != MarketBuyOrderKind.SalvageHammer)
                return RefusePlaceOrder(MarketError.InvalidMaterial, channel, buyer, materialType, kind, count, priceMmd, $"unknown order kind {(int)kind}");

            if (!MarketSalvageMaterials.TryGet(materialType, kind, out var material))
            {
                // Two different refusals, told apart so the buyer knows which to act on: a material the
                // market knows no salvage of at all, or a real material that simply has no Hammer.
                var error = kind == MarketBuyOrderKind.SalvageHammer && MarketSalvageMaterials.TryGet(materialType, out _)
                    ? MarketError.NoHammerForMaterial
                    : MarketError.InvalidMaterial;

                return RefusePlaceOrder(error, channel, buyer, materialType, kind, count, priceMmd);
            }

            // A free order is a request for a gift, not a purchase (WANTED-DESIGN 6.1).
            if (priceMmd < 1 || priceMmd > MaxPriceMmd)
                return RefusePlaceOrder(MarketError.InvalidPrice, channel, buyer, materialType, kind, count, priceMmd);

            var maxPrice = PropertyManager.GetLong("market_max_price_mmd").Item;
            if (maxPrice > 0 && priceMmd > maxPrice)
                return RefusePlaceOrder(MarketError.InvalidPrice, channel, buyer, materialType, kind, count, priceMmd, $"over the market_max_price_mmd soft cap of {maxPrice}");

            var maxCount = (int)ReadLongClamped("market_buy_order_max_count", 100, 1, 10000);
            if (count < 1 || count > maxCount)
                return RefusePlaceOrder(MarketError.CountUnavailable, channel, buyer, materialType, kind, count, priceMmd, $"count must be 1 to {maxCount}");

            // Division, never a checked multiply: the exact guard Buy uses (MarketManager_Purchase.cs).
            if (priceMmd > MaxPriceMmd / count)
                return RefusePlaceOrder(MarketError.InvalidPrice, channel, buyer, materialType, kind, count, priceMmd, "count times price overflows the bank bound");

            // The uniqueness CLAIM (see placementsInFlight). Taken under the same lock acquisition as
            // the existence check and held past the index insert, so a second placement for the same
            // (account, material, KIND) is refused for the whole time this one is building rather than
            // only at the instant it looks. RefusePlaceOrder writes an audit row, so it is called
            // outside the lock; a short-circuited Any means Add never ran, so a refusal never holds a
            // claim. The kind is in BOTH the check and the claim - see placementsInFlight's remarks.
            var claim = (buyer.AccountId, materialType, kind);
            bool exists;

            lock (indexLock)
                exists = buyOrders.Values.Any(o => o.BuyerAccountId == buyer.AccountId && o.MaterialType == materialType
                                                   && o.Kind == kind && o.Status == MarketBuyOrderStatus.Active)
                         || !placementsInFlight.Add(claim);

            if (exists)
                return RefusePlaceOrder(MarketError.OrderExists, channel, buyer, materialType, kind, count, priceMmd);

            try
            {
                var total = priceMmd * count;

                // Courtesy pre-check so the common refusal writes no row; TryDebit below is the authority.
                if (wallet.GetBalanceMmd(buyer.CharacterGuid) < total)
                    return RefusePlaceOrder(MarketError.InsufficientFunds, channel, buyer, materialType, kind, count, priceMmd);

                var days = (int)ReadLongClamped("market_buy_order_max_days", 30, 1, 365);
                var now = DateTime.UtcNow;

                // ---- step 1: the Pending row, BEFORE any value moves ----
                var row = new ShardMarketBuyOrder
                {
                    BuyerAccountId = buyer.AccountId, BuyerCharacterGuid = buyer.CharacterGuid, BuyerCharacterName = buyer.Name,
                    MaterialType = materialType, OrderKind = (byte)kind, Wcid = material.Wcid, PriceMmd = priceMmd,
                    CountTotal = count, CountRemaining = count, EscrowMmd = 0,
                    Status = (int)MarketBuyOrderStatus.Pending, ActiveMaterial = null,
                    CreatedAt = now, ExpiresAt = now.AddDays(days), ClosedAt = null,
                };

                if (!repository.AddBuyOrder(row))
                {
                    orderLog.Error($"[MARKET] could not write the Pending order row for account {buyer.AccountId}, material {materialType}, kind {kind}; refusing before anything moves.");
                    MarketRejectionLog.Record(MarketRejectOperation.PlaceOrder, MarketRejectionLog.RepositoryRefusedCode, channel, buyer,
                                              null, null, 0, count, priceMmd, $"material {materialType} ({kind.ToString().ToLowerInvariant()}); AddBuyOrder failed; the Pending row was never written");
                    return MarketResult<MarketBuyOrder>.Fail(MarketError.ServerError);
                }

                // ---- step 2: debit the buyer into escrow ----
                if (!wallet.TryDebit(buyer.CharacterGuid, total, out var debitError))
                {
                    row.Status = (int)(debitError == MarketError.LedgerUnknown ? MarketBuyOrderStatus.DebitLedgerUnknown : MarketBuyOrderStatus.Failed);
                    row.ClosedAt = DateTime.UtcNow;
                    if (!repository.UpdateBuyOrder(row))
                        orderLog.Error($"[MARKET] could not persist order {row.Id} as {row.Status}; it stays Pending on disk and boot recovery will refund a debit that never happened.");
                    if (debitError == MarketError.LedgerUnknown)
                        orderLog.Error($"[MARKET] LEDGER STATE UNKNOWN placing order {row.Id}: {total} MMD for character 0x{buyer.CharacterGuid:X8} may or may not have left the pool. Reconcile by hand.");
                    return MarketResult<MarketBuyOrder>.Fail(debitError);
                }

                // ---- step 3: Active with the escrow held ----
                row.Status = (int)MarketBuyOrderStatus.Active;
                row.EscrowMmd = total;
                row.ActiveMaterial = materialType;

                if (!repository.UpdateBuyOrder(row))
                    return FailActivation(row, buyer, materialType, kind, count, priceMmd, total, channel);

                // ---- step 4: publish. NOTHING above may publish a half-built order. ----
                var order = FromRow(row);
                lock (indexLock)
                {
                    order.Seq = ++changeSequence;
                    buyOrders[order.Id] = order;
                }

                return MarketResult<MarketBuyOrder>.Success(Copy(order));
            }
            finally
            {
                lock (indexLock)
                    placementsInFlight.Remove(claim);
            }
        }

        /// <summary>
        /// Step 3 could not write the order Active. The buyer HAS been debited and no order exists yet,
        /// and this must never publish one anyway.
        ///
        /// An order that is Active in memory over a Pending row on disk is served as fillable, and its
        /// escrow reads zero on disk. Worse, every later persist of it keeps colliding for as long as a
        /// sibling row holds active_Material, so a PARTIAL fill's step-5 write never lands either: the
        /// disk row stays Pending with count_Total intact, and RecoverPendingOrders refunds
        /// price * count_Total on the next boot. The buyer is paid back in full for bags they kept
        /// while the seller keeps what escrow paid them - MMD created from nothing, bags moved free.
        ///
        /// The row is rewritten Failed FIRST and the debit is credited back only once that write lands,
        /// the persist-before-refund ordering <see cref="CloseOrder"/> uses and for the same reason: a
        /// refund paid over a row that is still Pending on disk is paid a SECOND time by boot recovery.
        /// The Failed shape carries active_Material NULL, so this write cannot collide with whatever
        /// row just took the key - which is precisely the case this path exists for. Until the refund
        /// actually lands, escrow_Mmd stays on the closed row as the REFUND LOST marker.
        /// </summary>
        private static MarketResult<MarketBuyOrder> FailActivation(ShardMarketBuyOrder row, MarketActor buyer, int materialType,
                                                                   MarketBuyOrderKind kind, int count, long priceMmd, long total, MarketChannel channel)
        {
            row.Status = (int)MarketBuyOrderStatus.Failed;
            row.EscrowMmd = total;
            row.ActiveMaterial = null;
            row.ClosedAt = DateTime.UtcNow;

            if (!repository.UpdateBuyOrder(row))
            {
                orderLog.Error($"[MARKET] order {row.Id} could not be written Active and could not then be written Failed; it stays Pending on disk with count_Total {row.CountTotal}, so boot recovery will refund the {total} MMD taken from character 0x{buyer.CharacterGuid:X8}. Nothing is refunded here, because a refund over a Pending row is paid twice.");
            }
            else if (!wallet.TryCredit(buyer.CharacterGuid, total))
            {
                orderLog.Error($"[MARKET] REFUND LOST on order {row.Id}: {total} MMD could not be returned to character 0x{buyer.CharacterGuid:X8} after its activation could not be persisted. Refund by hand; the row keeps escrow_Mmd = {total} as the marker.");
            }
            else
            {
                row.EscrowMmd = 0;

                if (!repository.UpdateBuyOrder(row))
                    orderLog.Error($"[MARKET] order {row.Id} was refunded but its row still reads escrow_Mmd = {total}. Reconcile by hand.");
            }

            MarketRejectionLog.Record(MarketRejectOperation.PlaceOrder, MarketRejectionLog.RepositoryRefusedCode, channel, buyer,
                                      null, null, 0, count, priceMmd, $"material {materialType} ({kind.ToString().ToLowerInvariant()}); order {row.Id} could not be written Active; no order was published");

            return MarketResult<MarketBuyOrder>.Fail(MarketError.ServerError);
        }

        // ---- fill (WANTED-DESIGN 6.2 and 6.6) ----

        private static MarketResult<MarketTransaction> RefuseFill(MarketError error, MarketChannel channel, MarketActor seller,
                                                                  uint orderId, MarketBuyOrder order, int count, string detail = null)
        {
            MarketRejectionLog.Record(MarketRejectOperation.FillOrder, error, channel, seller, null, null, order?.Wcid ?? 0, count,
                                      order?.PriceMmd ?? 0, detail == null ? $"order {orderId}" : $"order {orderId}; {detail}");
            return MarketResult<MarketTransaction>.Fail(error);
        }

        public static MarketResult<MarketTransaction> FillOrder(MarketActor seller, uint orderId, int count, MarketChannel channel)
        {
            if (!OrdersEnabled)
                return MarketResult<MarketTransaction>.Fail(Enabled ? MarketError.BuyOrdersDisabled : MarketError.Disabled);

            if (buyOrdersReadFailed)
                return RefuseFill(MarketError.ServerError, channel, seller, orderId, null, count, "the order index failed to load at boot");

            if (seller.AccountId == 0 || seller.CharacterGuid == 0)
                return RefuseFill(MarketError.NotOwner, channel, seller, orderId, null, count);

            if (count < 1)
                return RefuseFill(MarketError.CountUnavailable, channel, seller, orderId, null, count);

            MarketBuyOrder order;
            lock (indexLock)
                buyOrders.TryGetValue(orderId, out order);

            if (order == null || order.Status != MarketBuyOrderStatus.Active)
                return RefuseFill(MarketError.OrderNotActive, channel, seller, orderId, order, count,
                                  order == null ? "no such order in the index" : $"order is {order.Status.ToString().ToLowerInvariant()}");

            if (order.BuyerAccountId == seller.AccountId)
                return RefuseFill(MarketError.NotOwner, channel, seller, orderId, order, count, "filling own order");

            if (!itemStore.IsReady(seller.AccountId, out _))
                return RefuseFill(MarketError.VaultUnavailable, channel, seller, orderId, order, count, "the seller's vault is not ready");

            if (!itemStore.IsReady(order.BuyerAccountId, out _))
                return RefuseFill(MarketError.VaultUnavailable, channel, seller, orderId, order, count, "the buyer's vault is not ready");

            // A stored-biota delivery costs one entry per bag, so the preflight asks about `count` biotas.
            // classKey null: a Wanted fill never draws on a class line (out of scope, 2026-09-27).
            if (!itemStore.CanReceive(order.BuyerAccountId, order.Wcid /* any non-null guid shape */, order.Wcid, classKey: null, count, out var roomError))
                return RefuseFill(roomError, channel, seller, orderId, order, count, "the buyer's vault cannot receive it");

            // THE ORDER'S KIND TRAVELS WITH ITS MATERIAL into every matching decision. Both are captured
            // here and nowhere else: `matches` is the single predicate handed to CountMatching at step 0
            // and to TryTakeMatching at step 2, so the count and the take can never disagree about what
            // this order accepts. IMarketItemStore takes an opaque predicate and knows nothing about
            // salvage, which is why nothing in the store changed for hammers.
            var materialType = order.MaterialType;
            var orderKind = order.Kind;
            Func<WorldObject, bool> matches = wo => MarketSalvageMaterials.MatchesOrder(wo, materialType, orderKind);

            if (itemStore.CountMatching(seller.AccountId, matches) < count)
                return RefuseFill(MarketError.NoMatchingItems, channel, seller, orderId, order, count);

            // ---- reservation (WANTED-DESIGN 6.6): re-read under the lock, then hold `count` against the order ----
            lock (indexLock)
            {
                if (order.Status != MarketBuyOrderStatus.Active)
                    return RefuseFill(MarketError.OrderNotActive, channel, seller, orderId, order, count, "closed between step 0 and the reservation");

                if (order.CountRemaining - order.InFlight < count)
                    return RefuseFill(MarketError.CountUnavailable, channel, seller, orderId, order, count, $"{order.CountRemaining} remaining, {order.InFlight} in flight");

                if (order.EscrowMmd < order.PriceMmd * count)
                {
                    orderLog.Error($"[MARKET] INVARIANT BREACH on order {order.Id}: escrow {order.EscrowMmd} < {order.PriceMmd} * {count}. Refusing.");
                    return RefuseFill(MarketError.ServerError, channel, seller, orderId, order, count, "escrow invariant breached");
                }

                order.InFlight += count;
            }

            try
            {
                return FillReserved(seller, order, count, matches, channel);
            }
            finally
            {
                lock (indexLock)
                    order.InFlight -= count;
            }
        }

        private static MarketResult<MarketTransaction> FillReserved(MarketActor seller, MarketBuyOrder order, int count,
                                                                    Func<WorldObject, bool> matches, MarketChannel channel)
        {
            var total = order.PriceMmd * count;
            var buyerActor = new MarketActor(order.BuyerAccountId, order.BuyerCharacterGuid, order.BuyerCharacterName);

            // ---- step 1: the Pending row. Buyer columns are the ORDER's buyer; seller columns are the filler. ----
            var row = new ShardMarketTransaction
            {
                ListingId = 0, BuyOrderId = order.Id,
                BuyerAccountId = order.BuyerAccountId, BuyerCharacterGuid = order.BuyerCharacterGuid, BuyerCharacterName = order.BuyerCharacterName,
                SellerAccountId = seller.AccountId, SellerCharacterGuid = seller.CharacterGuid, SellerCharacterName = seller.Name,
                Wcid = order.Wcid, ItemName = order.BagName, Count = count, PriceMmdTotal = total,
                Timestamp = DateTime.UtcNow, Channel = (int)channel, Status = (int)MarketTransactionStatus.Pending,
            };

            if (!repository.AddTransaction(row))
            {
                orderLog.Error($"[MARKET] could not write the Pending fill row for order {order.Id}; refusing before anything moves.");
                MarketRejectionLog.Record(MarketRejectOperation.FillOrder, MarketRejectionLog.RepositoryRefusedCode, channel, seller,
                                          null, null, order.Wcid, count, order.PriceMmd, $"order {order.Id}; AddTransaction failed; the Pending row was never written");
                return MarketResult<MarketTransaction>.Fail(MarketError.ServerError);
            }

            // ---- step 2: take from the seller, on the SELLER's queue. No BeginSale: the pre-withdraw hook auto-delists. ----
            //
            // The RECEIPT travels with `taken` and is not optional bookkeeping. This take can draw on a
            // collapsed ledger row as well as on stored biotas, and once withdrawn the two are
            // indistinguishable objects; only the store that withdrew them knows which is which, and
            // only step 3's unwind needs to know. Nothing here reads it - it is handed straight back.
            List<WorldObject> taken = null; MarketTakeReceipt receipt = null;
            var takeError = MarketError.None; var took = false;

            var sellerRegionRan = SafeSerialized(seller.AccountId, () =>
                took = itemStore.TryTakeMatching(seller.AccountId, matches, count, seller, out taken, out receipt, out takeError));

            if (!sellerRegionRan || !took || taken == null || taken.Count != count)
            {
                // Nothing was debited - the escrow is untouched - so this is Failed, not Refunded.
                Resolve(row, MarketTransactionStatus.Failed);
                return MarketResult<MarketTransaction>.Fail(takeError == MarketError.None ? MarketError.VaultUnavailable : takeError);
            }

            // ---- step 3: give to the buyer, on the BUYER's queue, in a SEPARATE region ----
            var gave = false; var giveError = MarketError.None;

            // Outside the region, so what already landed survives a throw out of it. See MarketDelivery.
            var delivery = new MarketDelivery();

            var buyerRegionRan = SafeSerialized(order.BuyerAccountId, () =>
                gave = itemStore.TryGiveToBuyer(order.BuyerAccountId, taken, buyerActor, delivery, out giveError));

            var filledCount = count;
            var filledTotal = total;

            if (!buyerRegionRan || !gave)
            {
                // ---- a delivery that failed part way is a PARTIAL FILL (owner ruling 2026-09-27) ----
                // Same rule as Buy: what reached the order's buyer stays there and is paid for out of the
                // escrow; only what provably did not is undone.
                LogAmbiguousDeliveries(delivery, row.Id, "fill", order.BuyerAccountId, seller.AccountId);

                var undelivered = delivery.Undelivered(taken);

                if (undelivered.Count > 0)
                {
                    // UndoTake, NOT TryReturnToSeller. This batch can be mixed, and TryReturnToSeller takes
                    // ONE asLedger flag for the whole list - there is no value of it that is right for a
                    // mixed batch, and there is no way for this method to work out which item is which.
                    // The store reads the receipt and puts each object back the way it produced it; the
                    // receipt is keyed by guid, so handing it a subset of the take is exact.
                    var undone = false;

                    var returned = SafeSerialized(seller.AccountId,
                        () => undone = itemStore.UndoTake(seller.AccountId, undelivered, receipt, seller));

                    // The region-ran flag AND the undo's own answer, because a return that ran and refused
                    // strands the items just as completely as one that never ran.
                    if (!returned || !undone)
                        orderLog.Error($"[MARKET] LOST ITEM RISK on fill {row.Id}: could not deposit into buyer account {order.BuyerAccountId} AND could not return to seller account {seller.AccountId}. Items: {string.Join(", ", undelivered.Select(i => $"0x{i.Guid.Full:X8}"))}.");
                }

                // One per object: TryTakeMatching delivers exactly `count` OBJECTS, one unit each
                // (LedgerRowMatches refuses a stackable wcid for exactly that reason).
                var delivered = Math.Min(delivery.Kept.Count, count);

                if (delivered == 0)
                {
                    Resolve(row, MarketTransactionStatus.Failed);
                    return MarketResult<MarketTransaction>.Fail(giveError == MarketError.None ? MarketError.VaultUnavailable : giveError);
                }

                filledCount = delivered;
                filledTotal = order.PriceMmd * delivered;

                orderLog.Warn($"[MARKET] fill {row.Id} on order {order.Id} is PARTIAL: {filledCount} of {count} item(s) reached buyer account {order.BuyerAccountId} and are paid {filledTotal} MMD from escrow; the rest were returned to seller account {seller.AccountId}.");

                // In memory only: persisted in the SAME write as Completed, by Resolve below, for the
                // reason Buy gives. A Pending fill row carrying a partial count would be marked Failed by
                // boot recovery with no payout and no order decrement, over bags the buyer already holds.
                row.Count = filledCount;
                row.PriceMmdTotal = filledTotal;
            }

            // ---- the row completes HERE, the instant custody changes. Nothing below unwinds. ----
            Resolve(row, MarketTransactionStatus.Completed);

            // ---- step 4: pay the seller from escrow ----
            if (!wallet.TryCredit(seller.CharacterGuid, filledTotal))
                orderLog.Error($"[MARKET] CREDIT LOST on fill {row.Id}: {filledTotal} MMD could not be paid to character 0x{seller.CharacterGuid:X8} ({seller.Name}). The buyer HAS the bags; pay the seller by hand from market_transaction.");

            // ---- step 5: decrement the order by what was actually delivered ----
            ShardMarketBuyOrder orderRow;
            lock (indexLock)
            {
                order.CountRemaining -= filledCount;
                order.EscrowMmd -= filledTotal;
                if (order.CountRemaining <= 0)
                {
                    order.Status = MarketBuyOrderStatus.Filled;
                    order.ClosedAt = DateTime.UtcNow;
                }
                order.Seq = ++changeSequence;
                orderRow = ToRow(order);
            }

            if (!repository.UpdateBuyOrder(orderRow))
                orderLog.Error($"[MARKET] fill {row.Id} completed but order {order.Id} could not be updated; its row reads the pre-fill count and escrow. Reconcile by hand.");

            AnalyticsManager.RecordMarketSale(row.BuyerCharacterGuid, row.BuyerCharacterName, row.SellerCharacterGuid, row.SellerCharacterName,
                                              row.Wcid, row.ItemName, row.Count, row.PriceMmdTotal);

            MarketNotifier.NotifyFill(row);

            return MarketResult<MarketTransaction>.Success(TrackTransaction(row));
        }

        // ---- cancel and expiry (WANTED-DESIGN 6.3 / 6.4) ----

        private static MarketResult<MarketBuyOrder> RefuseCancel(MarketError error, MarketChannel channel, MarketActor buyer, uint orderId, MarketBuyOrder order, string detail = null)
        {
            MarketRejectionLog.Record(MarketRejectOperation.CancelOrder, error, channel, buyer, null, null, order?.Wcid ?? 0,
                                      order?.CountRemaining ?? 0, order?.PriceMmd ?? 0, detail == null ? $"order {orderId}" : $"order {orderId}; {detail}");
            return MarketResult<MarketBuyOrder>.Fail(error);
        }

        /// <summary>Allowed with the kill switch OFF: a player must always be able to get their escrow back.</summary>
        public static MarketResult<MarketBuyOrder> CancelOrder(MarketActor buyer, uint orderId, MarketChannel channel)
        {
            if (!enabled)
                return MarketResult<MarketBuyOrder>.Fail(MarketError.Disabled);

            MarketBuyOrder order;
            lock (indexLock)
                buyOrders.TryGetValue(orderId, out order);

            if (order == null)
                return RefuseCancel(MarketError.OrderNotActive, channel, buyer, orderId, null, "no such order in the index");

            if (order.BuyerAccountId != buyer.AccountId || buyer.AccountId == 0)
                return RefuseCancel(MarketError.NotOwner, channel, buyer, orderId, order);

            var closed = CloseOrder(order, MarketBuyOrderStatus.Cancelled, out var closeError);

            if (!closed)
                return RefuseCancel(closeError, channel, buyer, orderId, order,
                                    closeError == MarketError.OrderBusy ? "a fill is in flight"
                                    : closeError == MarketError.ServerError ? "the order row could not be persisted; nothing was refunded and the cancel can be retried"
                                    : null);

            return MarketResult<MarketBuyOrder>.Success(GetBuyOrder(orderId));
        }

        /// <summary>
        /// The single close path for cancel and expiry. Status flips FIRST under the lock and the row
        /// is PERSISTED before the refund, so a lost refund is a closed row still carrying its escrow -
        /// one query finds every one of them - never an Active order whose money has already gone back.
        ///
        /// A FAILED PERSIST REFUSES rather than refunding anyway, the same call
        /// <see cref="Close"/> makes for a listing. Crediting over a row that is still Active on disk
        /// produces exactly the state WANTED-DESIGN 6.3 flips the status to prevent: a restart reloads
        /// that row as Active, and it can then be cancelled for a SECOND refund, or filled, paying a
        /// seller out of escrow that has already gone back to the buyer. The in-memory flip is rolled
        /// back on that path so memory and disk agree again and the owner can simply retry.
        ///
        /// THAT ROLLBACK CARRIES A KNOWN RESIDUAL RISK, accepted rather than engineered away. The
        /// status is flipped under indexLock, but the lock is released for the persist round trip,
        /// so between the flip and the rollback the order reads as non-Active to
        /// <see cref="PlaceOrder"/>'s one-Active-order-per-material check, which then lets the same
        /// buyer post a second order for that material. Reaching it needs an actual persist failure
        /// AND the same buyer racing two of their own requests inside one database round trip, and
        /// the worst it produces is the "Active in memory, Pending on disk" state PlaceOrder's step 3
        /// already logs with a reconcile-by-hand marker. It is strictly narrower than the double
        /// refund it replaced, which needed only the persist failure.
        ///
        /// Refuses OrderBusy while a fill holds a reservation.
        /// </summary>
        private static bool CloseOrder(MarketBuyOrder order, MarketBuyOrderStatus status, out MarketError error)
        {
            error = MarketError.None;
            long refund;
            ShardMarketBuyOrder row;

            MarketBuyOrderStatus previousStatus;
            DateTime? previousClosedAt;
            long previousEscrow;

            lock (indexLock)
            {
                if (order.Status != MarketBuyOrderStatus.Active) { error = MarketError.OrderNotActive; return false; }
                if (order.InFlight > 0) { error = MarketError.OrderBusy; return false; }

                previousStatus = order.Status;
                previousClosedAt = order.ClosedAt;
                previousEscrow = order.EscrowMmd;

                order.Status = status;
                order.ClosedAt = DateTime.UtcNow;
                order.Seq = ++changeSequence;
                refund = order.EscrowMmd;
                row = ToRow(order);
            }

            if (!repository.UpdateBuyOrder(row))
            {
                // Roll the flip back, so the index agrees with the row that is still on disk. Nothing
                // can have moved underneath it in the meantime: a non-Active order is refused by the
                // fill reservation and by this method's own first guard, so the order was closed to
                // every other writer for the whole window. Seq is bumped again rather than restored,
                // because a feed consumer that already saw the closed state must be told to unsee it.
                lock (indexLock)
                {
                    order.Status = previousStatus;
                    order.ClosedAt = previousClosedAt;
                    order.EscrowMmd = previousEscrow;
                    order.Seq = ++changeSequence;
                }

                orderLog.Error($"[MARKET] could not persist order {order.Id} as {status}; it stays Active in memory and in the database, which is the safe direction: nothing was refunded and the owner can retry. Refunding over an Active row would let a restart pay it back a second time.");
                error = MarketError.ServerError;
                return false;
            }

            if (refund > 0 && !wallet.TryCredit(order.BuyerCharacterGuid, refund))
            {
                orderLog.Error($"[MARKET] REFUND LOST closing order {order.Id} as {status}: {refund} MMD to character 0x{order.BuyerCharacterGuid:X8}. Refund by hand; the row keeps escrow_Mmd = {refund} as the marker.");
                return true;
            }

            lock (indexLock)
            {
                order.EscrowMmd = 0;
                row = ToRow(order);
            }

            if (!repository.UpdateBuyOrder(row))
                orderLog.Error($"[MARKET] order {order.Id} was refunded but its row still reads escrow_Mmd = {refund}. Reconcile by hand.");

            return true;
        }

        /// <summary>The hourly pass and the boot pass. Runs on the raw flag. A skipped order is LOGGED and left Active, never refused - expiry has no caller to retry, so the next pass is the retry.</summary>
        public static int ExpireOrders(DateTime nowUtc)
        {
            // repository and wallet as well as the flag, because all three are read WITHOUT the lock.
            // Shutdown nulls the seams and clears the index in one indexLock acquisition and `enabled`
            // is volatile, so the reachable fault is a TORN READ: `enabled` sampled true just before
            // Shutdown takes the lock, `repository` read after it has been nulled. Program.cs makes
            // that reachable by stopping this pass with a bounded 5 second join against a 30 second
            // tick and then calling Shutdown regardless. CloseOrder would then flip an order to
            // Expired, persist it, and NRE on the refund. That is NOT self-healing - the row is Expired
            // carrying non-zero escrow, which is the design's REFUND LOST marker, and the selection
            // below takes only ACTIVE orders, so no later pass ever retries it.
            //
            // The guard narrows the torn read to the span between here and CloseOrder's credit; it
            // cannot close it, because that credit still reads the same unlocked field. The whole pass
            // running after Shutdown is a separate case, closed at its source by the post-sleep
            // `running` re-check in MarketBuyOrderExpiry.WorkerLoop.
            if (!enabled || repository == null || wallet == null)
                return 0;

            List<MarketBuyOrder> due;
            lock (indexLock)
                due = buyOrders.Values.Where(o => o.Status == MarketBuyOrderStatus.Active && o.ExpiresAt <= nowUtc).ToList();

            var expired = 0;
            foreach (var order in due)
            {
                if (CloseOrder(order, MarketBuyOrderStatus.Expired, out var error)) { expired++; continue; }
                if (error == MarketError.OrderBusy)
                    orderLog.Info($"[MARKET] order {order.Id} is due to expire but a fill is in flight; the next pass will retry.");
                else if (error == MarketError.ServerError)
                    orderLog.Error($"[MARKET] order {order.Id} is due to expire but its row could not be persisted; it stays Active and nothing was refunded. The next pass will retry. Escrow is parked past its expiry until one succeeds.");
            }

            if (expired > 0)
                orderLog.Info($"[MARKET] expired {expired} buy order(s).");

            return expired;
        }

        // ---- boot recovery (WANTED-DESIGN 6.5) ----

        /// <summary>
        /// Pending ORDER rows older than the threshold belong to a placement this process died inside.
        /// Refund count_Total * price_Mmd and mark Failed - this can pay back money never debited, and
        /// that is accepted on RecoverPendingTransactions' own rule. A NULL read skips recovery entirely.
        /// </summary>
        public static void RecoverPendingOrders(int olderThanMs)
        {
            if (!enabled)
                return;

            var cutoff = DateTime.UtcNow.AddMilliseconds(-Math.Max(0, olderThanMs));
            var pending = repository.GetPendingBuyOrders(cutoff);

            if (pending == null)
            {
                orderLog.Error("[MARKET] could not read pending buy orders; boot recovery of orders is SKIPPED rather than concluding there are none.");
                return;
            }

            foreach (var row in pending)
            {
                var refund = row.PriceMmd * row.CountTotal;
                orderLog.Warn($"[MARKET] boot recovery: refunding {refund} MMD to character 0x{row.BuyerCharacterGuid:X8} for interrupted order {row.Id}.");

                if (!wallet.TryCredit(row.BuyerCharacterGuid, refund))
                    orderLog.Error($"[MARKET] REFUND LOST during boot recovery of order {row.Id}: {refund} MMD to character 0x{row.BuyerCharacterGuid:X8}. Refund by hand.");

                row.Status = (int)MarketBuyOrderStatus.Failed;
                row.ClosedAt = DateTime.UtcNow;
                row.ActiveMaterial = null;

                if (!repository.UpdateBuyOrder(row))
                    orderLog.Error($"[MARKET] could not persist recovered order {row.Id} as Failed; it will be refunded AGAIN on the next boot. Fix the row by hand.");

                lock (indexLock)
                {
                    if (buyOrders.TryGetValue(row.Id, out var live))
                    {
                        live.Status = MarketBuyOrderStatus.Failed;
                        live.ClosedAt = row.ClosedAt;
                        live.Seq = ++changeSequence;
                    }
                }
            }
        }

        /// <summary>
        /// Boot reconciliation of Active orders against their COMPLETED fills (WANTED-DESIGN 6.5). Runs
        /// after <see cref="RecoverPendingOrders"/>, which is what makes the fill rows it reads final:
        /// RecoverPendingTransactions has already marked every interrupted fill Failed.
        ///
        /// A fill's transaction row completes the INSTANT custody changes, and the order's own
        /// count_Remaining and escrow_Mmd decrement is a separate write on the far side of the seller's
        /// credit. A process death in that window leaves a Completed fill over an order still reading
        /// its pre-fill numbers, and nothing else looks: RecoverPendingOrders reads only Pending order
        /// rows, and RecoverPendingTransactions skips anything that is not Pending. The order can then
        /// be filled its full count AGAIN, or cancelled for the whole original escrow - which is the
        /// escrow-conservation invariant broken, not merely a stale number. The listing path has had
        /// the analogous branch since the start ("Pending over a Sold listing, complete it"); this is
        /// the order path's.
        ///
        /// CORRECTS DOWN ONLY. The Completed fills are evidence that custody changed, so they can only
        /// ever mean the order owes LESS than its row says. A count that reads the other way means a
        /// fill row is missing rather than an order being wrong - most plausibly a Completed fill whose
        /// own persist was lost - and inventing escrow to match it would create the money this method
        /// exists to protect. That case is logged and left alone.
        /// </summary>
        public static int ReconcileFilledOrders()
        {
            if (!enabled || repository == null)
                return 0;

            if (buyOrdersReadFailed)
            {
                orderLog.Error("[MARKET] the order index failed to load at boot, so reconciliation is SKIPPED: an empty index would report every order as needing no correction.");
                return 0;
            }

            List<MarketBuyOrder> active;
            lock (indexLock)
                active = buyOrders.Values.Where(o => o.Status == MarketBuyOrderStatus.Active).ToList();

            if (active.Count == 0)
                return 0;

            var totals = repository.SumCompletedFillsByOrder(active.Select(o => o.Id).ToList());

            if (totals == null)
            {
                orderLog.Error("[MARKET] could not read completed order fills; reconciliation is SKIPPED rather than concluding no order has ever been filled. Every Active order keeps the numbers on its row until a later successful pass.");
                return 0;
            }

            var reconciled = 0;

            foreach (var order in active)
            {
                // A missing entry is a real answer of zero (see the DAO contract), never an unknown.
                totals.TryGetValue(order.Id, out var filled);

                var trueRemaining = order.CountTotal - filled.Count;

                if (filled.TotalMmd != (long)filled.Count * order.PriceMmd)
                    orderLog.Error($"[MARKET] order {order.Id}'s fills paid {filled.TotalMmd} MMD for {filled.Count} bag(s) at a unit price of {order.PriceMmd}. The count is still what reconciliation acts on; investigate the price by hand from market_transaction.");

                if (trueRemaining == order.CountRemaining)
                    continue;

                if (trueRemaining > order.CountRemaining)
                {
                    orderLog.Warn($"[MARKET] order {order.Id} reads {order.CountRemaining} remaining but only {filled.Count} bag(s) of {order.CountTotal} have Completed fills. Nothing is corrected UP: that means a fill row is missing, not that the order owes more. Investigate by hand from market_transaction.");
                    continue;
                }

                if (trueRemaining < 0)
                    orderLog.Error($"[MARKET] order {order.Id} has Completed fills for {filled.Count} bag(s) against a count_Total of {order.CountTotal}. Closing it Filled and investigating is the only safe answer.");

                ShardMarketBuyOrder row;
                int previousRemaining;
                long previousEscrow;

                lock (indexLock)
                {
                    // Captured rather than logged here: log4net appenders do file I/O, and this method
                    // is public static with no boot-only guard, so it must not hold indexLock across
                    // one. Today's only caller is Program.cs before the API opens; that is a fact about
                    // the caller, not a property of this method.
                    previousRemaining = order.CountRemaining;
                    previousEscrow = order.EscrowMmd;

                    order.CountRemaining = Math.Max(0, trueRemaining);
                    order.EscrowMmd = order.CountRemaining * order.PriceMmd;

                    if (order.CountRemaining == 0)
                    {
                        order.Status = MarketBuyOrderStatus.Filled;
                        order.ClosedAt = DateTime.UtcNow;
                    }

                    order.Seq = ++changeSequence;
                    row = ToRow(order);
                }

                orderLog.Warn($"[MARKET] RECONCILED order {order.Id}: count_Remaining {previousRemaining} -> {row.CountRemaining}, escrow_Mmd {previousEscrow} -> {row.EscrowMmd}, against {filled.Count} Completed fill bag(s) of {order.CountTotal}.");

                if (!repository.UpdateBuyOrder(row))
                    orderLog.Error($"[MARKET] order {order.Id} was reconciled in memory but its row could not be written; the row still reads the pre-fill numbers and the next boot will reconcile it again. That repeat is harmless - the correction is computed from the fills, not applied twice.");

                reconciled++;
            }

            if (reconciled > 0)
                orderLog.Warn($"[MARKET] reconciled {reconciled} buy order(s) against their completed fills.");

            return reconciled;
        }
    }
}
