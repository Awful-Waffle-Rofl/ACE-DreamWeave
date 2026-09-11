using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Database;
using ACE.Server.WorldObjects;

namespace ACE.Server.Entity
{
    /// <summary>
    /// Helper class to verify player has enough free inventory slots / container slots / burden to receive some items
    /// </summary>
    public class ItemsToReceive
    {
        /// <summary>
        /// The player to receive the items
        /// </summary>
        private Player player { get; set; }

        public int RequiredInventorySlots { get; set; }
        public int RequiredContainerSlots { get; set; }
        public int RequiredBurden { get; set; }

        /// <summary>
        /// The total amount of items needing to be created.
        /// </summary>
        public int RequiredSlots => RequiredContainerSlots + RequiredInventorySlots;

        private int playerFreeInventorySlots { get; set; }
        private int playerFreeContainerSlots { get; set; }
        private int playerAvailableBurden { get; set; }

        public bool PlayerOutOfInventorySlots => RequiredInventorySlots > playerFreeInventorySlots;
        public bool PlayerOutOfContainerSlots => RequiredContainerSlots > playerFreeContainerSlots;
        public bool PlayerExceedsAvailableBurden => RequiredBurden > playerAvailableBurden;

        public bool PlayerExceedsLimits => PlayerOutOfInventorySlots || PlayerOutOfContainerSlots || PlayerExceedsAvailableBurden;

        public ItemsToReceive(Player player)
        {
            this.player = player;

            playerFreeInventorySlots = player.GetFreeInventorySlots();
            playerFreeContainerSlots = player.GetFreeContainerSlots();
            playerAvailableBurden = player.GetAvailableBurden();
        }

        public bool Add(uint weenieClassId, int amount)
        {
            return Process(weenieClassId, amount);
        }

        public bool Remove(uint weenieClassId, int amount)
        {
            return Process(weenieClassId, amount, true);
        }

        /// <summary>
        /// Credits back the burden/slot capacity that consuming a turn-in item frees up, so a reward
        /// pre-flight check can account for the space the outgoing item itself vacates - not just the
        /// space the incoming reward needs. Uses the item's own live values (never GetCachedWeenie, since
        /// this is a mutated instance - e.g. a partial stack), so callers must pass the actual WorldObject
        /// being consumed.
        ///
        /// A partial stack consumption (amountConsumed less than the item's current StackSize) leaves the
        /// stack itself in place - only its per-unit burden is freed, no slot. Consuming the whole item
        /// (or more) frees its full EncumbranceVal and one slot, charged to whichever counter
        /// (RequiredContainerSlots/RequiredInventorySlots) the item itself is charged against.
        /// </summary>
        public void CreditOutgoing(WorldObject item, int amountConsumed)
        {
            if (item == null)
                return;

            var currentStackSize = item.StackSize ?? 1;

            if (amountConsumed < currentStackSize)
            {
                RequiredBurden -= amountConsumed * (item.StackUnitEncumbrance ?? 0);
                return;
            }

            RequiredBurden -= item.EncumbranceVal ?? 0;

            if (item.UseBackpackSlot)
                RequiredContainerSlots -= 1;
            else
                RequiredInventorySlots -= 1;
        }

        /// <summary>
        /// Reserves raw inventory slots for rewards whose weenie is not knowable ahead of time - a
        /// CreateTreasure emote row rolls its item at execution time, so there is no wcid to look a burden
        /// or a container requirement up from. One slot per row, zero burden: deliberately the cheapest
        /// honest reservation, since over-charging burden here would refuse gives that would have fit.
        /// </summary>
        public bool AddUnknownItemSlots(int count)
        {
            RequiredInventorySlots += count;

            return !PlayerExceedsLimits;
        }

        private bool Process(uint weenieClassId, int amount, bool negate = false)
        {
            var requiredSlots = GetItemSlotAndBurdenRequirements(weenieClassId, amount, out var requiredEncumbrance, out var itemRequiresBackpackSlot);

            if (negate)
            {
                requiredSlots *= -1;
                requiredEncumbrance *= -1;
            }

            RequiredBurden += requiredEncumbrance;

            if (itemRequiresBackpackSlot)
                RequiredContainerSlots += requiredSlots;
            else
                RequiredInventorySlots += requiredSlots;

            return !PlayerExceedsLimits;
        }

        /// <summary>
        /// Returns the number of slots required for the items
        /// </summary>
        private int GetItemSlotAndBurdenRequirements(uint weenieClassId, int amount, out int requiredEncumbrance, out bool requiresBackpackSlot)
        {
            requiredEncumbrance = 0;
            requiresBackpackSlot = false;

            var item = DatabaseManager.World.GetCachedWeenie(weenieClassId);

            if (item == null || item.IsVendorService())
                return 0;

            requiresBackpackSlot = item.RequiresBackpackSlotOrIsContainer();

            if (!item.IsStackable())
            {
                requiredEncumbrance = amount * item.GetProperty(PropertyInt.EncumbranceVal) ?? 0;
                return amount;
            }

            var itemStackUnitEncumbrance = item.GetStackUnitEncumbrance();

            var itemMaxStackSize = item.GetMaxStackSize();

            requiredEncumbrance = amount * itemStackUnitEncumbrance;

            var itemStacks = amount / itemMaxStackSize;

            if (amount % itemMaxStackSize > 0)
                itemStacks++;

            return itemStacks;
        }
    }
}
