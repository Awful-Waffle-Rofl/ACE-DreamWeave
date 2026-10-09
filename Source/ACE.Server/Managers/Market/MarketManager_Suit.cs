using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Common.Extensions;
using ACE.Server.Entity.AccountVault;
using ACE.Server.Managers.Market.Suit;

namespace ACE.Server.Managers.Market
{
    public static partial class MarketManager
    {
        /// <summary>
        /// The suit builder's view of one account's vault (GET /v1/accounts/me/suit/inventory): every stored
        /// item, ledger row and class line that could sit in a suit, as <see cref="SuitItem"/>s.
        ///
        /// NOT gated on <see cref="Enabled"/>: the suit builder has its own switch (suit_builder_enabled), and a
        /// player must still be able to plan a suit while trading is paused. Readiness goes through the item
        /// store's IsReady, which is AccountVaultStore.TryCheckReady in production (never IsLoaded, which
        /// reports a store mid-load as simply "not loaded" and never starts the load). Not ready answers
        /// <see cref="MarketError.VaultLoading"/> and an EMPTY list must therefore never be read as "nothing in
        /// the vault".
        ///
        /// Each entry is filtered with <see cref="SuitItemProjector.IsSuitRelevant(int?)"/> BEFORE it is projected,
        /// so a vault full of salvage and weapons costs nothing beyond one property read per row. A ledger row is
        /// projected from its template weenie (WeenieLookup); a class line from a materialized display object
        /// when the production store is wired, else from its template weenie.
        ///
        /// <c>listed</c> follows <see cref="ListingForEntry"/>, the rule the vault view uses, and mirrors what
        /// <see cref="OnVaultWithdraw"/> does to the listing:
        ///  - A stored GROUP: any withdraw from the group passes the REPRESENTATIVE's guid to the pre-withdraw
        ///    hook, which closes the WHOLE listing, so when a listing covers a group EVERY member is listed.
        ///  - A ledger row or class line: the listing survives a withdraw that leaves at least its count, so a
        ///    listing of 2 over a row of 4 is emitted as TWO items - the free remainder (count 4 - 2, listed
        ///    false, skipped when 0) and the listed part (count 2, listed true).
        /// </summary>
        public static bool TryGetSuitInventory(uint accountId, out IReadOnlyList<SuitItem> items, out MarketError error)
        {
            var result = new List<SuitItem>();
            items = result;
            error = MarketError.None;

            var store = itemStore;

            if (store == null || !store.IsReady(accountId, out _))
            {
                error = MarketError.VaultLoading;
                return false;
            }

            var active = GetActiveListingsForAccount(accountId);

            foreach (var entry in store.GetEntries(accountId, 0, -1))
            {
                try
                {
                    switch (entry.Kind)
                    {
                        case VaultEntryKind.StoredItem:
                            AddStored(result, entry, active);
                            break;

                        case VaultEntryKind.Ledger:
                        case VaultEntryKind.Class:
                            AddBiotaless(result, accountId, entry, active);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    // One unreadable row is skipped, never the whole inventory.
                    log.Error($"[MARKET] could not project vault wcid {entry.Wcid} for the suit builder; skipping it: {ex.GetFullMessage()}");
                }
            }

            return true;
        }

        /// <summary>
        /// The suit transfer's listing question for one vault row: the ACTIVE listing the vault view draws on
        /// it (<see cref="ListingForEntry"/>, the same match <see cref="TryGetSuitInventory"/> uses for
        /// <c>listed</c>), as its listed count, or null when the row carries none. For a stored group pass the
        /// GROUP row, whose Guid is the representative a group listing names.
        /// </summary>
        internal static int? SuitListedCount(uint accountId, VaultEntry row)
        {
            if (row == null)
                return null;

            var listing = ListingForEntry(GetActiveListingsForAccount(accountId), row);

            return listing == null ? (int?)null : Math.Max(1, listing.Count);
        }

        private static void AddStored(List<SuitItem> result, VaultEntry entry, IReadOnlyList<MarketListing> active)
        {
            var members = entry.Members != null && entry.Members.Count > 0
                ? entry.Members
                : (entry.WorldObject != null ? new[] { entry.WorldObject } : Array.Empty<ACE.Server.WorldObjects.WorldObject>());

            // Any withdraw from a group names the representative and closes the whole listing, so a
            // listing over the group makes every member listed. See the remarks on TryGetSuitInventory.
            var listed = ListingForEntry(active, entry) != null;

            foreach (var member in members)
            {
                // BEFORE projecting: a non-suit member costs one property read.
                if (!SuitItemProjector.IsSuitRelevant(member))
                    continue;

                var projected = SuitItemProjector.FromItem(member, SuitItem.SourceVault, null, member.StackSize ?? 1);

                result.Add(listed ? projected with { Listed = true } : projected);
            }
        }

        private static void AddBiotaless(List<SuitItem> result, uint accountId, VaultEntry entry, IReadOnlyList<MarketListing> active)
        {
            var weenie = WeenieLookup?.Invoke(entry.Wcid);

            if (weenie == null || !SuitItemProjector.IsSuitRelevant(weenie))
                return;

            var isClass = entry.Kind == VaultEntryKind.Class;
            var classKey = isClass ? entry.ClassDisplayId : null;
            var count = (int)Math.Min(entry.Count, int.MaxValue);

            var projected = isClass && itemStore is VaultMarketItemStore vaultStore
                ? vaultStore.DescribeClassForSuit(accountId, entry, SuitItem.SourceVault)
                : null;

            projected ??= SuitItemProjector.FromWeenie(weenie, SuitItem.SourceVault, classKey, count);

            var listing = ListingForEntry(active, entry);

            if (listing == null)
            {
                result.Add(projected);
                return;
            }

            // The keep-alive rule (OnVaultWithdraw): the listing covers listing.Count units of the line.
            // Split the line into its free remainder and its listed part.
            var listedCount = Math.Min(count, Math.Max(1, listing.Count));
            var freeCount = count - listedCount;

            if (freeCount > 0)
                result.Add(projected with { Count = freeCount, Listed = false });

            result.Add(projected with { Count = listedCount, Listed = true });
        }
    }
}
