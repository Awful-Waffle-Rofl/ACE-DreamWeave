using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Server.Knots;
using ACE.Server.WorldObjects;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// The Wandering Peddler's pure payment and build helpers (owner rulings 2026-11-16; buy-time payment 2026-11-20,
    /// accounting in ThreadDungeonRun_Peddler.cs). A buy (ThreadPeddlerVendor.TryBuy) builds each purchase here
    /// (<see cref="BuildPurchase"/>), checks the buyer's pack against what was built (<see cref="PackNeeds"/>), then the
    /// run removes exactly the price in RANDOM eligible items from the buyer's held pile
    /// (ThreadDungeonRun.TryChargePeddlerPurchase, choosing with <see cref="PickPaymentItems"/> among the items
    /// <see cref="ItemEligibleForPayment"/> accepts), and the vendor puts the built objects in the buyer's pack at once.
    /// Nothing is held for the clear: neither delivery seam (ThreadCachePlacer.Execute, ThreadGroupCacheDelivery.
    /// ServeMemberCore) touches the peddler's accounting beyond the hold gate.
    ///
    /// Conservation lives in the vendor: a built object is destroyed if the buy is refused before the charge, and after
    /// the charge it is either in the pack or, when the pack refuses it, in the buyer's held pile as a peddler purchase
    /// (<see cref="PurchaseFixedOrderBase"/>), so it reaches their Thread Cache.
    /// </summary>
    public static class ThreadPeddlerPayment
    {
        /// <summary>
        /// A purchase that could not be placed in the pack sorts AFTER the fixed boss bonus (whose FixedOrder is its 0-based
        /// index in a short fixed list, ThreadLootStacking.SortPileForPlacement), in purchase order.
        /// </summary>
        public const int PurchaseFixedOrderBase = 1_000_000;

        /// <summary>The prize-table entry a purchase names. Production: the live Thread store's encounter prize table, by id.</summary>
        internal static Func<string, ThreadEncounterPrizeEntry> EntryLookup = id =>
            (ThreadDungeonManager.Store?.Encounters?.Prize ?? ThreadEncounterPrizeTable.Empty).Entries.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.Ordinal));

        /// <summary>
        /// May this pile item be taken as payment? Never a FIXED item (the boss bonus: Trade Note stack, Durable Legendary
        /// Key, salvage bags), a Knot-family item (Fiber, Pattern, Knot), a rare, or a peddler purchase. Pure.
        /// </summary>
        public static bool ItemEligibleForPayment(HeldPileItem held)
        {
            if (held?.Item == null || held.Item.IsDestroyed)
                return false;

            if (held.FixedOrder.HasValue || held.PeddlerPurchase || held.RareFinderName != null)
                return false;

            // A Trade Note (MMD) is never payment, even one that dropped outside the fixed boss bonus.
            if ((held.Item.ItemType & ItemType.PromissoryNote) != 0)
                return false;

            return !KnotItems.IsKnotFamily(held.Item);
        }

        /// <summary>
        /// min(n, count) items drawn uniformly at random without replacement (a partial Fisher-Yates over a copy). The
        /// input is not changed. Empty for n &lt;= 0. Pure over <paramref name="rng"/>.
        /// </summary>
        public static List<T> PickPaymentItems<T>(IReadOnlyList<T> eligible, int n, Random rng)
        {
            var pool = eligible?.ToList() ?? new List<T>();
            var take = Math.Min(Math.Max(0, n), pool.Count);

            for (var i = 0; i < take; i++)
            {
                var j = i + rng.Next(pool.Count - i);
                (pool[i], pool[j]) = (pool[j], pool[i]);
            }

            return pool.GetRange(0, take);
        }

        /// <summary>
        /// The prize pick a purchase is delivered as. The entry is rolled ALONE through ThreadEncounterPrize.Roll (at weight
        /// 1, so an entry the prize roll itself never draws can still be sold), so two salvage purchases are two independent
        /// bag-count and material draws; a Fiber is at <paramref name="knotLevel"/>; a Pattern is for the stored pinnacle.
        /// Null when the entry cannot be built now (no Knot level, no stored pinnacle, no weenie). Pure over <paramref name="rng"/>.
        /// </summary>
        internal static ThreadEncounterPrizePick PickFor(ThreadEncounterPrizeEntry entry, PeddlerPurchase purchase, int? knotLevel, Func<uint, bool> exists, Random rng)
        {
            if (entry == null || purchase == null)
                return null;

            var alone = new ThreadEncounterPrizeEntry(entry.Id, entry.Wcids, entry.Qty, 1.0, entry.Salvage, entry.IsFiber, entry.IsPattern);
            var pool = purchase.Pattern != null ? new[] { purchase.Pattern } : Array.Empty<PinnacleDef>();

            return ThreadEncounterPrize.Roll(new[] { alone }, exists, rng, null, knotLevel, pool);
        }

        /// <summary>
        /// The Knots inputs a Fiber or Pattern purchase is built against, read at the buy: the same gate as the encounter
        /// prize and the Knots drop (ThreadEncounterPrize.KnotsInputs - pooled loot, a stamped level,
        /// dynamic_dungeons_fiber_enabled, the live pinnacle pool). Closed, a Knot purchase builds nothing and the buy is refused.
        /// </summary>
        internal static Func<ThreadDungeonRun, (int? FiberLevel, IReadOnlyList<PinnacleDef> PatternPool)> KnotsInputs = ThreadEncounterPrize.KnotsInputs;

        /// <summary>
        /// One purchase's objects, unplaced (ThreadEncounterPrize.BuildObjects: an item entry's quantity as stacks of at most
        /// MaxStackSize, a salvage entry as bags with their quality fields, a Fiber or Pattern through the Knots seam).
        /// Empty when it cannot be built now (no entry, the Knots gate closed, a Pattern whose pinnacle has left the pool, no
        /// weenie). If collecting the built objects throws, what was collected is destroyed and the exception propagates.
        /// </summary>
        internal static List<WorldObject> BuildPurchase(ThreadDungeonRun run, PeddlerPurchase purchase, Random rng)
        {
            var objects = new List<WorldObject>();
            var entry = purchase == null ? null : EntryLookup(purchase.EntryId);

            if (entry == null)
                return objects;

            int? fiberLevel = null;

            if (entry.IsFiber || entry.IsPattern)
            {
                var (level, pool) = KnotsInputs(run);
                fiberLevel = level;

                // A Pattern only while its pinnacle is still one the Knots drop could roll.
                if (entry.IsPattern && (purchase.Pattern == null || pool == null || !pool.Any(p => p != null && string.Equals(p.Key, purchase.Pattern.Key, StringComparison.Ordinal))))
                    return objects;
            }

            var pick = PickFor(entry, purchase, fiberLevel, ThreadEncounterPrize.WeenieExists, rng ?? Random.Shared);

            if (pick == null)
                return objects;

            var built = ThreadEncounterPrize.BuildObjects(pick, 0, rng ?? Random.Shared);

            try
            {
                foreach (var (item, _) in built)
                {
                    if (item != null)
                        objects.Add(item);
                }
            }
            catch
            {
                foreach (var (item, _) in built)
                {
                    try { if (item != null && !item.IsDestroyed) item.Destroy(); } catch { }
                }

                throw;
            }

            return objects;
        }

        /// <summary>
        /// What <paramref name="objects"/> need in a pack, counted the way ItemsToReceive counts a retail buy: the summed
        /// EncumbranceVal, and one slot each, a container slot for an object with UseBackpackSlot (a pack) and an inventory
        /// slot otherwise. Pure.
        /// </summary>
        public static (int InventorySlots, int ContainerSlots, long Burden) PackNeeds(IEnumerable<WorldObject> objects)
        {
            var inventory = 0;
            var container = 0;
            var burden = 0L;

            foreach (var item in objects ?? Enumerable.Empty<WorldObject>())
            {
                if (item == null)
                    continue;

                burden += item.EncumbranceVal ?? 0;

                if (item.UseBackpackSlot)
                    container++;
                else
                    inventory++;
            }

            return (inventory, container, burden);
        }
    }
}
