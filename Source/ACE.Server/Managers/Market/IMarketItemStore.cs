using System;
using System.Collections.Generic;

using ACE.Server.Entity.AccountVault;
using ACE.Server.WorldObjects;

namespace ACE.Server.Managers.Market
{
    /// <summary>
    /// The item-custody seam (DESIGN 4.1 and 10; reference impl VaultMarketItemStore/AccountVaultStore).
    /// SERIALIZATION IS THE CONTRACT: every mutator must run inside <see cref="RunSerialized"/> for that
    /// account (may throw otherwise); NEVER nest two accounts' regions - it deadlocks A-buys-from-B against B-buys-from-A. A read that FAILS is never one that found nothing: <see cref="IsReady"/> gives a reason.
    /// </summary>
    public interface IMarketItemStore
    {
        /// <summary>False when this account's store cannot be served right now, with the player-facing reason.</summary>
        bool IsReady(uint accountId, out string failReason);

        /// <summary>One page of holdings in the store's own order. Empty when not ready - check <see cref="IsReady"/> to tell empty from unavailable.</summary>
        IReadOnlyList<VaultEntry> GetEntries(uint accountId, int offset, int limit);

        /// <summary>Runs <paramref name="work"/> serialized against every other mutation on this account. False means it did NOT run.</summary>
        bool RunSerialized(uint accountId, Action work);

        /// <summary>
        /// True if the account holds at least <paramref name="count"/> units. Stored biota: pass its guid
        /// and count 1; ledger stack: pass a null guid and the wcid. For invalidation, never as a substitute for the real take.
        /// </summary>
        bool Holds(uint accountId, uint? itemGuid, uint wcid, int count);

        /// <summary>
        /// PREFLIGHT ONLY: true when <paramref name="buyerAccountId"/>'s store looks able to accept what
        /// this listing would deliver (same shape as <see cref="TryTakeForSale"/>'s item identity - a
        /// stored biota's guid, or a null guid plus wcid for a ledger stack - and the count being bought).
        /// This is advisory and checked BEFORE any value moves (no Pending row, no debit), so a refusal
        /// that is knowable up front never produces one. It is never a substitute for
        /// <see cref="TryGiveToBuyer"/>'s real result: the vault can fill between this check and the
        /// actual give, and that path still unwinds correctly via <see cref="TryReturnToSeller"/>.
        /// </summary>
        bool CanReceive(uint buyerAccountId, uint? itemGuid, uint wcid, int count, out MarketError error);

        /// <summary>
        /// Takes items into the caller's hands. MUST run inside <see cref="RunSerialized"/> for the seller.
        /// THE CALLER OWNS EVERY RETURNED OBJECT and must discharge it via <see cref="TryGiveToBuyer"/> or
        /// <see cref="TryReturnToSeller"/>; destroying one removes it from seller and buyer at once.
        /// </summary>
        bool TryTakeForSale(uint sellerAccountId, uint? itemGuid, uint wcid, int count,
                            MarketActor auditActor, out List<WorldObject> taken, out MarketError error);

        /// <summary>
        /// Puts taken items into the buyer's holdings. MUST run inside <see cref="RunSerialized"/> for the
        /// buyer. On false the caller still owns the items and must return them to the seller.
        /// </summary>
        bool TryGiveToBuyer(uint buyerAccountId, IReadOnlyList<WorldObject> items,
                            MarketActor auditActor, out MarketError error);

        /// <summary>
        /// The compensation path. MUST run inside <see cref="RunSerialized"/> for the seller.
        /// <paramref name="asLedger"/> true credits units of <paramref name="wcid"/> back rather than storing biotas.
        /// False is the worst outcome the market can produce: log every guid, and do not destroy the items.
        /// </summary>
        bool TryReturnToSeller(uint sellerAccountId, IReadOnlyList<WorldObject> items, uint wcid,
                               bool asLedger, MarketActor auditActor);

        /// <summary>
        /// Destroys one of the account's own holdings by feeding it to that account's barrel
        /// (Docs/Market/GIVEAWAY-BULK-BARREL-DESIGN.md section 6). MUST run inside
        /// <see cref="RunSerialized"/> for that account.
        ///
        /// This is the ONE method here that neither takes items into the caller's hands nor puts any
        /// there, and nothing is returned: on true the holding is gone from the account's view and the
        /// store owns whatever is left of it. There is therefore no unwind contract and no object for
        /// the caller to discharge - which is exactly why it is a store operation rather than a
        /// take-then-destroy in the manager, since a take-then-destroy would put a real item in a
        /// caller's hands with destruction as its only ending.
        ///
        /// Item identity is the same shape as everywhere else here: a stored biota's guid, or a null
        /// guid plus the wcid for a ledger stack.
        /// </summary>
        bool TryBarrel(uint accountId, uint? itemGuid, uint wcid, int count, MarketActor auditActor, out MarketError error);

        /// <summary>
        /// PREFLIGHT ONLY: how many holdings in this account satisfy <paramref name="predicate"/>.
        /// No region, no mutation, advisory - never a substitute for <see cref="TryTakeMatching"/>'s real
        /// result. 0 when the store is not ready.
        ///
        /// A COLLAPSED LEDGER ROW COUNTS, one per unit, even though it has no biota to test: collapse
        /// only ever fires on an item provably identical to a fresh instance of its own wcid, so a
        /// fresh instance answers for every unit on the row. That is the normal shape for the items
        /// buy orders want - a forge-fresh full salvage bag and a full-charge Hammer both collapse -
        /// so a store that skipped those rows told a seller holding exactly the wanted item that they
        /// held none.
        /// </summary>
        int CountMatching(uint accountId, Func<WorldObject, bool> predicate);

        /// <summary>
        /// Takes exactly <paramref name="count"/> whole holdings that satisfy <paramref name="predicate"/>
        /// into the caller's hands, ALL OR NOTHING. MUST run inside <see cref="RunSerialized"/> for the seller.
        /// Every member of a group row is tested individually, never assumed from its representative, and a
        /// collapsed ledger row is drawn on for as many units as are still needed (see <see cref="CountMatching"/>).
        /// On a shortfall everything already taken is returned to the seller INSIDE the same region (never
        /// by opening another), <paramref name="taken"/> is empty and the error is NoMatchingItems.
        /// THE CALLER OWNS EVERY RETURNED OBJECT, as for <see cref="TryTakeForSale"/>.
        ///
        /// A TAKE MAY THEREFORE BE MIXED, and <paramref name="taken"/> does not say which object came
        /// from where - once withdrawn the two are indistinguishable. <paramref name="receipt"/> is how
        /// the store remembers, and it is the ONLY thing that makes a later compensation correct: a
        /// caller unwinding this take must call <see cref="UndoTake"/> with it, never
        /// <see cref="TryReturnToSeller"/>, whose single asLedger flag cannot describe a mixed batch.
        /// Never null, and <see cref="MarketTakeReceipt.Empty"/> on every failure exit, where
        /// <paramref name="taken"/> is empty anyway.
        /// </summary>
        bool TryTakeMatching(uint sellerAccountId, Func<WorldObject, bool> predicate, int count,
                             MarketActor auditActor, out List<WorldObject> taken,
                             out MarketTakeReceipt receipt, out MarketError error);

        /// <summary>
        /// The compensation path FOR <see cref="TryTakeMatching"/>, and the only correct one: it puts
        /// each object back the way that take produced it, reading <paramref name="receipt"/> rather
        /// than asking the caller which kind anything was. MUST run inside <see cref="RunSerialized"/>
        /// for the seller. False is the worst outcome the market can produce, exactly as for
        /// <see cref="TryReturnToSeller"/>: every guid is logged and no item is destroyed.
        ///
        /// <see cref="TryReturnToSeller"/> remains for the single-kind takes (<see cref="TryTakeForSale"/>),
        /// where the listing's own shape decides and there is nothing to remember.
        /// </summary>
        bool UndoTake(uint sellerAccountId, IReadOnlyList<WorldObject> items, MarketTakeReceipt receipt,
                      MarketActor auditActor);
    }

    /// <summary>
    /// The provenance half of a take's result: which of the objects it handed over came out of a
    /// COLLAPSED LEDGER row rather than out of a vault container.
    ///
    /// It exists because the undo is not one operation. A ledger-derived object must be credited back
    /// to the ledger and its carrier destroyed; a stored biota must go back into a container. Doing
    /// either the wrong way round is silent, and the two mistakes are not even the same size. Filing a
    /// ledger-derived object as a biota bypasses the entry cap - AccountVaultStore.TryReturnWithdrawn
    /// skips that check on the reasoning that a stored biota was already counted while it sat there,
    /// says so in its own remarks, and tells callers not to generalize the exemption out - and leaves
    /// units that can never re-collapse. Crediting a real stored biota to the ledger DESTROYS it and
    /// replaces it with vendor-fresh units, which is the item degradation VaultCollapse exists to
    /// prevent. So an unknown provenance is resolved toward "stored biota", the direction that never
    /// destroys anything.
    ///
    /// OPAQUE ON PURPOSE. A caller takes one from <see cref="IMarketItemStore.TryTakeMatching"/> and
    /// hands it straight back to <see cref="IMarketItemStore.UndoTake"/>. It cannot read one, cannot
    /// build a meaningful one, and so cannot be tempted to decide provenance itself - a decision that
    /// belongs to the store that did the withdrawing and to nothing else, because a caller holding a
    /// WorldObject has no way to tell the two apart.
    /// </summary>
    public sealed class MarketTakeReceipt
    {
        private readonly HashSet<uint> ledgerSourced;

        /// <summary>Copies the set, so the store's own live bookkeeping cannot change a receipt already issued.</summary>
        internal MarketTakeReceipt(IEnumerable<uint> ledgerSourcedGuids)
        {
            ledgerSourced = ledgerSourcedGuids == null ? new HashSet<uint>() : new HashSet<uint>(ledgerSourcedGuids);
        }

        /// <summary>A take that drew on no ledger row. Also the honest answer for a take that handed over nothing.</summary>
        public static MarketTakeReceipt Empty { get; } = new MarketTakeReceipt(null);

        internal bool IsLedgerSourced(WorldObject item) => item != null && ledgerSourced.Contains(item.Guid.Full);
    }
}
