using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages.Messages;

namespace ACE.Server.WorldObjects
{
    partial class Player
    {
        // player buying items from vendor

        /// <summary>
        /// Called when player clicks 'Buy Items'
        /// </summary>
        public void HandleActionBuyItem(uint vendorGuid, List<ItemProfile> items)
        {
            if (IsBusy)
            {
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, Guid.Full));
                SendUseDoneEvent(WeenieError.YoureTooBusy);
                return;
            }

            if (IsTrading)
            {
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, Guid.Full));
                SendUseDoneEvent(WeenieError.CantDoThatTradeInProgress);
                return;
            }

            var vendor = CurrentLandblock?.GetObject(vendorGuid) as Vendor;

            if (vendor == null)
            {
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, Guid.Full));
                SendUseDoneEvent(WeenieError.NoObject);
                return;
            }

            // if this succeeds, it automatically calls player.FinalizeBuyTransaction()
            if (!vendor.BuyItems_ValidateTransaction(items, this))
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, Guid.Full));

            SendUseDoneEvent();
        }

        private const uint coinStackWcid = (uint)ACE.Entity.Enum.WeenieClassName.W_COINSTACK_CLASS;

        /// <summary>
        /// Vendor has validated the transactions and sent a list of items for processing.
        /// </summary>
        public void FinalizeBuyTransaction(Vendor vendor, List<WorldObject> genericItems, List<WorldObject> uniqueItems, uint cost)
        {
            // transaction has been validated by this point

            var currencyWcid = vendor.AlternateCurrency ?? coinStackWcid;

            SpendCurrency(currencyWcid, cost, true);

            vendor.MoneyIncome += (int)cost;

            foreach (var item in genericItems)
            {
                var service = item.GetProperty(PropertyBool.VendorService) ?? false;

                if (!service)
                {
                    // errors shouldn't be possible here, since the items were pre-validated, but just in case...
                    if (!TryCreateInInventoryWithNetworking(item))
                    {
                        log.Error($"[VENDOR] {Name}.FinalizeBuyTransaction({vendor.Name}) - couldn't add {item.Name} ({item.Guid}) to player inventory after validation, this shouldn't happen!");

                        item.Destroy();  // cleanup for guid manager
                    }

                    vendor.NumItemsSold++;
                }
                else
                    vendor.ApplyService(item, this);
            }

            foreach (var item in uniqueItems)
            {
                if (TryCreateInInventoryWithNetworking(item))
                {
                    vendor.UniqueItemsForSale.Remove(item.Guid);

                    // this was only for when the unique item was sold to the vendor,
                    // to determine when the item should rot on the vendor. it gets removed now
                    item.SoldTimestamp = null;

                    vendor.NumItemsSold++;
                }
                else
                    log.Error($"[VENDOR] {Name}.FinalizeBuyTransaction({vendor.Name}) - couldn't add {item.Name} ({item.Guid}) to player inventory after validation, this shouldn't happen!");
            }

            Session.Network.EnqueueSend(new GameMessageSound(Guid, Sound.PickUpItem));

            if (PropertyManager.GetBool("player_receive_immediate_save").Item)
                RushNextPlayerSave(5);

            var altCurrencySpent = vendor.AlternateCurrency != null ? cost : 0;

            vendor.ApproachVendor(this, VendorType.Buy, altCurrencySpent);
        }

        // player selling items to vendor

        // whereas most of the logic for buying items is in vendor,
        // most of the logic for selling items is located in player_commerce
        // the functions have similar structure, just in different places
        // there's really no point in there being differences in location,
        // and it might be better to move them all to vendor for consistency.

        /// <summary>
        /// Called when player clicks 'Sell Items'
        /// </summary>
        public void HandleActionSellItem(uint vendorGuid, List<ItemProfile> itemProfiles)
        {
            if (IsBusy)
            {
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, Guid.Full));
                SendUseDoneEvent(WeenieError.YoureTooBusy);
                return;
            }

            var vendor = CurrentLandblock?.GetObject(vendorGuid) as Vendor;

            if (vendor == null)
            {
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, Guid.Full));
                SendUseDoneEvent(WeenieError.NoObject);
                return;
            }

            // perform validations on requested sell items,
            // and filter to list of validated items

            // one difference between sell and buy is here.
            // when an itemProfile is invalid in buy, the entire transaction is failed immediately.
            // when an itemProfile is invalid in sell, we just remove the invalid itemProfiles, and continue onwards
            // this might not be the best for safety, and it's a tradeoff between safety and player convenience
            // should we fail the entire transaction (similar to buy), if there are any invalids in the transaction request?

            var sellList = VerifySellItems(itemProfiles, vendor);

            if (sellList.Count == 0)
            {
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, Guid.Full));
                SendUseDoneEvent();
                return;
            }

            // calculate pyreals to receive
            var payoutCoinAmount = vendor.CalculatePayoutCoinAmount(sellList);

            if (payoutCoinAmount < 0)
            {
                log.Warn($"[VENDOR] {Name} (0x({Guid}) tried to sell something to {vendor.Name} (0x{vendor.Guid}) resulting in a payout of {payoutCoinAmount} pyreals.");

                SendTransientError("Transaction failed.");
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, Guid.Full));

                SendUseDoneEvent();

                return;
            }

            // Always-on banking shop hook: proceeds go straight to the bank, so no pack space is needed
            // to receive coins. Sold items are still removed from the pack below.

            vendor.MoneyOutflow += payoutCoinAmount;

            // remove sell items from player inventory
            //
            // the off-player save each removal needs is collected rather than enqueued per item: a large sale would
            // otherwise put N un-mergeable entries on the single shard queue, each one head-of-line blocking every
            // other player's shard work (including logins). One batched save follows the loop instead.
            var deferredSaves = NewDeferredSaveList();

            foreach (var item in sellList.Values)
            {
                if (TryRemoveFromInventoryWithNetworking(item.Guid, out _, RemoveFromInventoryAction.SellItem, deferredSaves) || TryDequipObjectWithNetworking(item.Guid, out _, DequipObjectAction.SellItem, deferredSaves))
                    Session.Network.EnqueueSend(new GameEventItemServerSaysContainId(Session, item, vendor));
                else
                    log.WarnFormat("[VENDOR] Item 0x{0:X8}:{1} for player {2} not found in HandleActionSellItem.", item.Guid.Full, item.Name, Name); // This shouldn't happen
            }

            // ORDERING: this must be enqueued BEFORE ProcessItemsForPurchase, which issues the
            // RemoveBiotaFromDatabase / Destroy for these same ids. The shard queue is strict FIFO, so
            // save-then-remove leaves the row deleted; the reverse would resurrect a sold item after its delete.
            // Nothing between the loop above and here mutates or destroys any of these items.
            FlushDeferredSaves(deferredSaves);

            // send the list of items to the vendor
            // for the vendor to determine what to do with each item (resell, destroy)
            vendor.ProcessItemsForPurchase(this, sellList);

            // Sale proceeds (vendors always pay out in pyreals). Banking them is the default, since it needs no
            // pack space, but a player can opt out with /bank autodeposit off and be paid in real coin stacks:
            // inventory-reading tools poll the pyreal stacks actually in the pack, so for them the coin has to
            // land there. CalculatePayoutCoinAmount returns an int, so payoutCoinAmount already fits the int
            // CreatePayoutCoinStacks takes - no narrowing is involved - and it is known non-negative here.
            if (BankAutoDeposit)
            {
                var bankBalance = ModifyBankBalance(PropertyInt64.BankedPyreals, payoutCoinAmount);
                UpdateCoinValue();
                RushNextPlayerSave(5);
                Session.Network.EnqueueSend(new GameMessageSystemChat($"[BANK] Deposited {payoutCoinAmount:N0} pyreals to your bank. Balance: {bankBalance:N0}", ChatMessageType.Broadcast));
            }
            else
            {
                // Retail payout path. Anything that will not fit (full pack / over burden) is banked instead of
                // being dropped: no coin may ever be lost, so every stack either reaches the pack or is credited
                // and destroyed (the same cleanup DepositPyreals does for an un-placeable remainder stack).
                long paidToPack = 0;
                long overflowToBank = 0;

                foreach (var stack in CreatePayoutCoinStacks(payoutCoinAmount))
                {
                    var stackValue = stack.StackSize ?? 1;

                    if (TryCreateInInventoryWithNetworking(stack))
                    {
                        paidToPack += stackValue;
                    }
                    else
                    {
                        overflowToBank += stackValue;
                        stack.Destroy();
                    }
                }

                long bankBalance = 0;
                if (overflowToBank > 0)
                    bankBalance = ModifyBankBalance(PropertyInt64.BankedPyreals, overflowToBank);

                UpdateCoinValue();
                RushNextPlayerSave(5);

                if (paidToPack > 0)
                    Session.Network.EnqueueSend(new GameMessageSystemChat($"You receive {paidToPack:N0} pyreals.", ChatMessageType.Broadcast));

                if (overflowToBank > 0)
                    Session.Network.EnqueueSend(new GameMessageSystemChat($"[BANK] Your pack was full, so {overflowToBank:N0} pyreals went to your bank instead. Balance: {bankBalance:N0}", ChatMessageType.Broadcast));
            }

            Session.Network.EnqueueSend(new GameMessageSound(Guid, Sound.PickUpItem));

            SendUseDoneEvent();
        }

        /// <summary>
        /// Filters the list of ItemProfiles the player is attempting to sell to the vendor
        /// to the list of verified WorldObjects in the player's inventory w/ validations
        /// </summary>
        private Dictionary<uint, WorldObject> VerifySellItems(List<ItemProfile> sellItems, Vendor vendor)
        {
            var allPossessions = GetAllPossessions().ToDictionary(i => i.Guid.Full, i => i);

            var acceptedItemTypes = (ItemType)(vendor.MerchandiseItemTypes ?? 0);

            var verified = new Dictionary<uint, WorldObject>();

            foreach (var sellItem in sellItems)
            {
                if (!allPossessions.TryGetValue(sellItem.ObjectGuid, out var wo))
                {
                    log.Warn($"[VENDOR] {Name} tried to sell item {sellItem.ObjectGuid:X8} not in their inventory to {vendor.Name}");
                    continue;
                }

                // verify item profile (unique guids, amount)
                if (verified.ContainsKey(wo.Guid.Full))
                {
                    log.Warn($"[VENDOR] {Name} tried to sell duplicate item {wo.Name} ({wo.Guid}) to {vendor.Name}");
                    continue;
                }

                if (!sellItem.IsValidAmount)
                {
                    log.Warn($"[VENDOR] {Name} tried to sell {sellItem.Amount}x {wo.Name} ({wo.Guid}) to {vendor.Name}");
                    continue;
                }

                if (sellItem.Amount > (wo.StackSize ?? 1))
                {
                    log.Warn($"[VENDOR] {Name} tried to sell {sellItem.Amount}x {wo.Name} ({wo.Guid}) to {vendor.Name}, but they only have {wo.StackSize ?? 1}x");
                    continue;
                }

                // verify wo / vendor / player properties
                if ((acceptedItemTypes & wo.ItemType) == 0 || !wo.IsSellable || wo.Retained)
                {
                    var itemName = (wo.StackSize ?? 1) > 1 ? wo.GetPluralName() : wo.Name;
                    Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, $"The {itemName} is unsellable.")); // retail message did not include item name, leaving in that for now.
                    continue;
                }

                if (wo.Value < 1)
                {
                    var itemName = (wo.StackSize ?? 1) > 1 ? wo.GetPluralName() : wo.Name;
                    Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, $"The {itemName} has no value and cannot be sold.")); // retail message did not include item name, leaving in that for now.
                    continue;
                }

                if (IsTrading && wo.IsBeingTradedOrContainsItemBeingTraded(ItemsInTradeWindow))
                {
                    var itemName = (wo.StackSize ?? 1) > 1 ? wo.GetPluralName() : wo.Name;
                    Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, $"You cannot sell that! The {itemName} is currently being traded.")); // custom message?
                    continue;
                }

                if (wo is Container container && container.Inventory.Count > 0)
                {
                    var itemName = (wo.StackSize ?? 1) > 1 ? wo.GetPluralName() : wo.Name;
                    Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, $"You cannot sell that! The {itemName} must be empty.")); // custom message?
                    continue;
                }

                verified.Add(wo.Guid.Full, wo);
            }

            return verified;
        }

        /// <summary>
        /// Splits <paramref name="amount"/> into per-stack sizes, none exceeding <paramref name="maxStackSize"/>.
        /// </summary>
        public static IReadOnlyList<int> CalcPayoutStackSizes(int amount, int maxStackSize)
        {
            if (maxStackSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxStackSize), maxStackSize, "max stack size must be greater than zero");

            var sizes = new List<int>();

            if (amount <= 0)
                return sizes;

            for (var i = 0; i < amount / maxStackSize; i++)
                sizes.Add(maxStackSize);

            var remainder = amount % maxStackSize;
            if (remainder > 0)
                sizes.Add(remainder);

            return sizes;
        }

        private List<WorldObject> CreatePayoutCoinStacks(int amount)
        {
            var coinStacks = new List<WorldObject>();

            if (amount <= 0)
                return coinStacks;

            // The per-stack cap is read off a created coin object rather than hardcoded: MaxStackSize is
            // world-database data, so a constant here would diverge from the weenie if that value is retuned.
            // The first object doubles as the probe, so nothing is created that does not end up in the list.
            var probe = WorldObjectFactory.CreateNewWorldObject("coinstack");

            var sizes = CalcPayoutStackSizes(amount, probe.MaxStackSize.Value);

            for (var i = 0; i < sizes.Count; i++)
            {
                var currencyStack = i == 0 ? probe : WorldObjectFactory.CreateNewWorldObject("coinstack");

                currencyStack.SetStackSize(sizes[i]);
                coinStacks.Add(currencyStack);
            }

            return coinStacks;
        }

        /// <summary>
        /// The coin total the client is shown. Because the always-on bank shop hook (see SpendCurrency) lets
        /// banked pyreals back a vendor purchase, what the player can actually spend is inventory coin plus
        /// banked pyreals. The client both displays this ("you have Np") and refuses to send a purchase it
        /// believes is unaffordable, so it has to be told the spendable total, not just the carried one.
        ///
        /// CoinValue itself deliberately stays inventory-only: GetNumCoinsDropped (half your coin on death)
        /// and the inventory side of SpendCurrency read it, and neither may ever see banked money.
        /// </summary>
        public int GetSpendableCoinValue() => CalcSpendableCoinValue(CoinValue ?? 0, BankedPyreals);

        /// <summary>
        /// Inventory coin + banked pyreals, as an int the client can hold.
        /// </summary>
        public static int CalcSpendableCoinValue(long inventoryCoin, long bankedPyreals)
        {
            // Each side is clamped before the add: a bank balance is a long, so summing it with coin first
            // could overflow into a negative and show a rich player 0p. The cap only limits what the client
            // is told; the server re-checks the true balance in Vendor.BuyItems_ValidateTransaction.
            var inv = Math.Clamp(inventoryCoin, 0, int.MaxValue);
            var banked = Math.Clamp(bankedPyreals, 0, int.MaxValue);

            return (int)Math.Min(inv + banked, int.MaxValue);
        }

        private int lastSentSpendableCoinValue = -1;

        /// <summary>
        /// Pushes the spendable coin total to the client, if it changed since the last push. Reads the cached
        /// CoinValue instead of walking inventory, so it is also safe to call on a player from another
        /// player's thread - a bank transfer credits its target from the sender's thread.
        /// </summary>
        public void SendSpendableCoinValue()
        {
            var spendable = GetSpendableCoinValue();

            if (spendable == lastSentSpendableCoinValue)
                return;

            lastSentSpendableCoinValue = spendable;

            Session?.Network.EnqueueSend(new GameMessagePrivateUpdatePropertyInt(this, PropertyInt.CoinValue, spendable));
        }

        private void UpdateCoinValue(bool sendUpdateMessageIfChanged = true)
        {
            int coins = 0;

            foreach (var coinStack in GetInventoryItemsOfTypeWeenieType(WeenieType.Coin))
                coins += coinStack.Value ?? 0;

            CoinValue = coins;

            if (sendUpdateMessageIfChanged)
                SendSpendableCoinValue();
        }

        private List<WorldObject> SpendCurrency(uint currentWcid, uint amount, bool destroy = false)
        {
            if (currentWcid == 0 || amount == 0)
                return null;

            var cost = new List<WorldObject>();

            // Always-on banking shop hook: for currencies the bank holds (pyreals + promissory notes),
            // spend from inventory first, then draw the remainder from the bank. Non-bankable alternate
            // currencies fall through to the original inventory-only path below.
            var altName = GetAlternateCurrencyName(currentWcid); // non-null only for promissory notes
            var bankable = currentWcid == coinStackWcid || altName != null;

            if (destroy && bankable)
            {
                long invAmount = currentWcid == coinStackWcid ? (CoinValue ?? 0) : GetNumInventoryItemsOfWCID(currentWcid);
                long fromInv = Math.Min(invAmount, amount);
                long fromBank = amount - fromInv;

                long bankAvail = currentWcid == coinStackWcid ? BankedPyreals : GetBankedAlternateCurrency(currentWcid);
                if (fromBank > bankAvail)
                {
                    // Upstream validation should make this impossible; refuse rather than hand over goods for free.
                    log.Error($"[BANK] {Name} SpendCurrency shortfall on wcid {currentWcid}: need {fromBank} from bank, have {bankAvail}");
                    return null;
                }

                if (fromInv > 0)
                    TryConsumeFromInventoryWithNetworking(currentWcid, (int)fromInv);

                if (fromBank > 0)
                {
                    if (currentWcid == coinStackWcid)
                    {
                        ModifyBankBalance(PropertyInt64.BankedPyreals, -fromBank);
                        UpdateCoinValue();
                        Session.Network.EnqueueSend(new GameMessageSystemChat($"[BANK] Debited {fromBank:N0} pyreals from your bank.", ChatMessageType.Broadcast));
                    }
                    else
                    {
                        DebitBankedAlternateCurrency(currentWcid, fromBank);
                        Session.Network.EnqueueSend(new GameMessageSystemChat($"[BANK] Debited {fromBank:N0} {altName} from your bank.", ChatMessageType.Broadcast));
                    }
                    RushNextPlayerSave(5);
                }

                return cost;
            }

            if (currentWcid == coinStackWcid)
            {
                if (amount > CoinValue)
                    return null;
            }
            if (destroy)
            {
                TryConsumeFromInventoryWithNetworking(currentWcid, (int)amount);
            }
            else
            {
                cost = CollectCurrencyStacks(currentWcid, amount);

                foreach (var stack in cost)
                {
                    if (!TryRemoveFromInventoryWithNetworking(stack.Guid, out _, RemoveFromInventoryAction.SpendItem))
                        UpdateCoinValue(); // this coinstack was created by spliting up an existing one, and not actually added to the players inventory. The existing stack was already adjusted down but we need to update the player's CoinValue, so we do that now.
                }
            }
            return cost;
        }

        private List<WorldObject> CollectCurrencyStacks(uint currencyWcid, uint amount)
        {
            var currencyStacksCollected = new List<WorldObject>();

            var currencyStacksInInventory = GetInventoryItemsOfWCID(currencyWcid);
            //currencyStacksInInventory = currencyStacksInInventory.OrderBy(o => o.Value).ToList();

            var remaining = (int)amount;

            foreach (var stack in currencyStacksInInventory)
            {
                var amountToRemove = Math.Min(remaining, stack.StackSize ?? 1);

                if (stack.StackSize == amountToRemove)
                {
                    currencyStacksCollected.Add(stack);
                }
                else
                {
                    // create new stack
                    var newStack = WorldObjectFactory.CreateNewWorldObject(currencyWcid);
                    newStack.SetStackSize(amountToRemove);
                    currencyStacksCollected.Add(newStack);

                    var stackToAdjust = FindObject(stack.Guid, SearchLocations.MyInventory, out var foundInContainer, out var rootContainer, out _);

                    // adjust existing stack
                    if (stackToAdjust != null)
                    {
                        AdjustStack(stackToAdjust, -amountToRemove, foundInContainer, rootContainer);
                        Session.Network.EnqueueSend(new GameMessageSetStackSize(stackToAdjust));
                        Session.Network.EnqueueSend(new GameMessagePrivateUpdatePropertyInt(this, PropertyInt.EncumbranceVal, EncumbranceVal ?? 0));
                    }
                    // UpdateCoinValue removed -- already called upstream
                }

                remaining -= amountToRemove;

                if (remaining <= 0)
                    break;
            }
            return currencyStacksCollected;
        }
    }
}
