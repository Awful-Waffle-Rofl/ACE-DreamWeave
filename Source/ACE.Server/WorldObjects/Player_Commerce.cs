using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Database;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Managers.Analytics;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Pvp.Templates;

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
            // PvP template gate: no vendor purchases while templated. Before the vendor is even looked up.
            var templateRefusal = PvpTemplateBlocked(PvpTemplateAction.VendorBuy);
            if (templateRefusal != null)
            {
                SendPvpTemplateRefusal(templateRefusal);
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, Guid.Full));
                SendUseDoneEvent();
                return;
            }

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

            // On success this either finalizes the purchase immediately (FinalizeBuyTransaction), or,
            // for a Drift Network trainer vendor's class-ability token, instead enqueues a live Yes/No
            // confirmation and returns true with nothing yet charged - see
            // Vendor.OfferClassAbilityInstantLearn.
            if (!vendor.BuyItems_ValidateTransaction(items, this))
            {
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, Guid.Full));

                // Re-authorise the shop panel's alternate-currency figure after a REJECTED purchase.
                // GameEventApproachVendor is the only message that carries it, and it is otherwise sent
                // only on open, on sell, and on a SUCCESSFUL buy - so a rejection left the client showing
                // whatever it had decremented locally. The client does maintain that counter itself: it
                // refuses to initiate a purchase it thinks is unaffordable (see GameEventApproachVendor),
                // and the altCurrencySpent compensation there exists precisely because the client applies
                // the transaction cost on its own.
                //
                // This matters most for the Drift Network trainers, where that figure IS the player's Class
                // Ability Point balance: attempting to buy a rank or a class tier they have not unlocked is
                // rejected here with the points untouched, but the panel could still show them gone, which
                // reads as being charged for nothing. VendorType.Undef so no vendor emote fires on a failure.
                //
                // Conditional for a PersonalVendor: the alternate-currency figure this exists to refresh
                // is one a mule does not have (no AlternateCurrency, no create_list), so on a mule this
                // call is only a full panel rebuild plus, for a non-owner, one grants SELECT - on a path
                // a client can loop for free with invalid profiles.
                //
                // It is still made when the window is actually stale. BuyItems_ValidateTransaction IS
                // TryWithdrawTransaction for a mule, so false here means the withdrawal was refused, and
                // one way it is refused is that another window on the same store drained the row this
                // panel is still showing. Without the rebuild that panel stays wrong until the player
                // walks out of range and back, and every retry writes two more permanent audit rows.
                //
                // Also made while a deferred mule refresh is pending for this player: their client is
                // then KNOWN to be showing counts older than the store's (PersonalVendor_WithdrawRefresh),
                // and another viewer's approach may have rebuilt the window - so ViewIsStale reads false
                // and the display guids this client holds for changed rows no longer resolve.
                if (!(vendor is PersonalVendor mule) || mule.ViewIsStale || mule.HasPendingRefreshFor(this))
                    vendor.ApproachVendor(this, VendorType.Undef);
            }

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

            // SpendCurrency returns null ONLY when it refused the charge - a bank shortfall it logs, or an
            // inventory-coin shortfall. Validation is supposed to make that unreachable, but the goods were
            // handed over regardless when it wasn't, which is the one outcome that must never happen; its own
            // shortfall branch says "refuse rather than hand over goods for free" and nothing here honoured
            // it. cost is always >= 1 (GetSellCost floors every item at 1), so null is never the amount == 0
            // early-out.
            // Mule Vendor, fix round 1 (F2): this branch is UNREACHABLE today, and it is NOT the answer
            // to "does withdrawal charge the player?" - do not trust it for that question. This method's
            // only caller is Vendor.BuyItems_FinalTransaction (Vendor.cs:613), and PersonalVendor
            // overrides BuyItems_ValidateTransaction to return TryWithdrawTransaction(...) directly,
            // delivering withdrawn items itself and never calling base - so FinalizeBuyTransaction never
            // runs for a PersonalVendor at all. TryWithdrawTransaction is where the real answer lives
            // (it does not charge; GetBuyCost/GetSellCost are always 0 and no currency call exists on
            // that path). This branch is purely defensive: it exists to catch a future refactor that
            // routes withdrawal back through the retail BuyItems_FinalTransaction flow, where cost would
            // be 0 and SpendCurrency(wcid, 0) returns null (the amount == 0 early-out), which would
            // otherwise abort the transaction and destroy genericItems after goods were already implied.
            // Keep this branch for that future case; just don't read its presence as proof of anything
            // about the current withdrawal path.
            if (vendor is PersonalVendor)
            {
                // no SpendCurrency, no MoneyIncome, no RecordVendorPayment
            }
            else if (SpendCurrency(currencyWcid, cost, true) == null)
            {
                log.Error($"[VENDOR] {Name}.FinalizeBuyTransaction({vendor.Name}) - currency {currencyWcid} x{cost} was refused after validation; aborting the transaction");

                // genericItems were created for this transaction and are owned by nobody, so they are ours to
                // destroy. uniqueItems are NOT: they are still registered in vendor.UniqueItemsForSale and are
                // only removed from it on a successful add below, so leaving them alone returns them to stock.
                foreach (var item in genericItems)
                    item.Destroy();

                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, Guid.Full));
                vendor.ApproachVendor(this, VendorType.Undef);
                return;
            }
            else
            {
                vendor.MoneyIncome += (int)cost;

                // The currency leg is recorded here rather than per item: `cost` is the price of the whole
                // basket, and by this point SpendCurrency has succeeded, so the money definitely moved.
                AnalyticsManager.RecordVendorPayment(this, vendor, true, currencyWcid, cost);
            }

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
                    else
                        AnalyticsManager.RecordVendorBuy(this, vendor, item);

                    vendor.NumItemsSold++;
                }
                else
                {
                    vendor.ApplyService(item, this);

                    // A service is bought and paid for like anything else, so it belongs in the audit
                    // trail even though nothing lands in the pack.
                    AnalyticsManager.RecordVendorBuy(this, vendor, item);
                }
            }

            foreach (var item in uniqueItems)
            {
                if (TryCreateInInventoryWithNetworking(item))
                {
                    vendor.UniqueItemsForSale.Remove(item.Guid);

                    // this was only for when the unique item was sold to the vendor,
                    // to determine when the item should rot on the vendor. it gets removed now
                    item.SoldTimestamp = null;

                    AnalyticsManager.RecordVendorBuy(this, vendor, item);

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
        ///
        /// Nothing here but the per-phase timing scope; the sale itself is <see cref="HandleActionSellItemCore"/>.
        /// Split that way because this handler ran 527.62 ms synchronously on the world thread for one 76-item
        /// mule-vault deposit on prod and only about a quarter of that had ever been attributed to anything -
        /// see <see cref="SellPhaseProfile"/>, which this scope arms and which is the ONLY thing that arms it.
        /// The scope is closed in a finally so an escaping exception cannot leave the instrument armed for
        /// whatever the world thread runs next. Inert (two static bool reads and a 0 return) while
        /// world_tick_slow_log_enabled is off, which is its shipped default.
        /// </summary>
        public void HandleActionSellItem(uint vendorGuid, List<ItemProfile> itemProfiles)
        {
            var handlerScope = SellPhaseProfile.BeginHandler();

            try
            {
                HandleActionSellItemCore(vendorGuid, itemProfiles);
            }
            finally
            {
                SellPhaseProfile.EndHandler(handlerScope);
            }
        }

        private void HandleActionSellItemCore(uint vendorGuid, List<ItemProfile> itemProfiles)
        {
            // PvP template gate: no vendor sales while templated. Before the vendor is even looked up; an issued item
            // is also refused one by one in VerifySellItems, templated or not.
            var templateRefusal = PvpTemplateBlocked(PvpTemplateAction.VendorSell);
            if (templateRefusal != null)
            {
                SendPvpTemplateRefusal(templateRefusal);
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, Guid.Full));
                SendUseDoneEvent();
                return;
            }

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

            // Timed as an ENCLOSING region: VerifySellItems charges SellPhase.Access and SellPhase.Accept from
            // inside itself, and EndExclusive subtracts those, so sell_validate_ms is the possession walk and
            // the item-level gates alone rather than all three added together.
            var validateScope = SellPhaseProfile.Begin();
            var validateCharged = SellPhaseProfile.ChargedTicks;

            var sellList = VerifySellItems(itemProfiles, vendor);

            SellPhaseProfile.EndExclusive(SellPhase.Validate, validateScope, validateCharged);

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
                // Two leaf phases over this loop body, deliberately closed on BOTH branches of the existing
                // condition rather than restructured into a local: the phase boundary must not change which
                // calls run or in what order. SellPhase.Remove covers the detach plus the contain-id message
                // that reports it to the client; SellPhase.Analytics covers the audit row alone.
                var removeScope = SellPhaseProfile.Begin();

                if (TryRemoveFromInventoryWithNetworking(item.Guid, out _, RemoveFromInventoryAction.SellItem, deferredSaves) || TryDequipObjectWithNetworking(item.Guid, out _, DequipObjectAction.SellItem, deferredSaves))
                {
                    Session.Network.EnqueueSend(new GameEventItemServerSaysContainId(Session, item, vendor));

                    SellPhaseProfile.End(SellPhase.Remove, removeScope);

                    // Recorded here, inside the success branch and BEFORE ProcessItemsForPurchase below,
                    // which may destroy these same objects - the row captures values, never the object.
                    var analyticsScope = SellPhaseProfile.Begin();

                    AnalyticsManager.RecordVendorSell(this, vendor, item);

                    SellPhaseProfile.End(SellPhase.Analytics, analyticsScope);
                }
                else
                {
                    SellPhaseProfile.End(SellPhase.Remove, removeScope);

                    log.WarnFormat("[VENDOR] Item 0x{0:X8}:{1} for player {2} not found in HandleActionSellItem.", item.Guid.Full, item.Name, Name); // This shouldn't happen
                }
            }

            // ORDERING: this must be enqueued BEFORE ProcessItemsForPurchase, which issues the
            // RemoveBiotaFromDatabase / Destroy for these same ids. The shard queue is strict FIFO, so
            // save-then-remove leaves the row deleted; the reverse would resurrect a sold item after its delete.
            // Nothing between the loop above and here mutates or destroys any of these items.
            var flushScope = SellPhaseProfile.Begin();

            FlushDeferredSaves(deferredSaves);

            SellPhaseProfile.End(SellPhase.Flush, flushScope);

            // send the list of items to the vendor
            // for the vendor to determine what to do with each item (resell, destroy)
            //
            // Another ENCLOSING region: the per-item vault deposit (SellPhase.Deposit) and the panel rebuild
            // at its tail (SellPhase.Panel) are charged from inside, so sell_purchase_ms is what is left -
            // the resell-or-destroy decision on an ordinary vendor, or the deposit queue overhead and
            // hand-back handling on a mule.
            var purchaseScope = SellPhaseProfile.Begin();
            var purchaseCharged = SellPhaseProfile.ChargedTicks;

            vendor.ProcessItemsForPurchase(this, sellList);

            SellPhaseProfile.EndExclusive(SellPhase.Purchase, purchaseScope, purchaseCharged);

            // Mule Vendor: depositing is not a sale. Zero payout is correct, but the bank branch would
            // still announce a 0-pyreal deposit on every item stored, and RushNextPlayerSave(5) would fire
            // for a balance that did not move.
            if (!(vendor is PersonalVendor))
            {
                // One leaf phase over the whole payout leg. Opened here rather than around the `if` so a mule
                // deposit reports sell_payout_n=0 - "this leg did not run" - instead of a zero that could be
                // read as "this leg is free". There is no return anywhere in this block, so the End below is
                // reached on every path through it.
                var payoutScope = SellPhaseProfile.Begin();

                // Fix round 1 (F3): moved inside this guard - it used to sit above it, unconditionally,
                // which wrote a zero-pyreal AnalyticsManager row for every single item deposited into a
                // vault (a 40-item deposit wrote 40 zero-value rows attributed to a mule, skewing any
                // per-vendor revenue aggregate by row count). RecordVendorSell per item, above, is NOT
                // moved - DESIGN section 13 requires deposits to be audited regardless of payout.
                AnalyticsManager.RecordVendorPayment(this, vendor, false, coinStackWcid, payoutCoinAmount);

                // Sale proceeds (vendors always pay out in pyreals). Banking them is the default, since it needs no
                // pack space, but a player can opt out with /bank autodeposit off and be paid in real coin stacks:
                // inventory-reading tools poll the pyreal stacks actually in the pack, so for them the coin has to
                // land there. CalculatePayoutCoinAmount returns an int, so payoutCoinAmount already fits the int
                // CreatePayoutCoinStacks takes - no narrowing is involved - and it is known non-negative here.
                if (BankAutoDeposit)
                {
                    var credit = TryAdjustBankedPyreals(payoutCoinAmount, "vendor sale payout", out var newBankBalance);

                    if (credit == AccountBankAdjustResult.Applied || credit == AccountBankAdjustResult.AppliedCountUnknown)
                    {
                        var bankBalance = credit == AccountBankAdjustResult.Applied ? newBankBalance : BankedPyreals;

                        UpdateCoinValue();
                        RushNextPlayerSave(5);
                        Session.Network.EnqueueSend(new GameMessageSystemChat($"[BANK] Deposited {payoutCoinAmount:N0} pyreals to your bank. Balance: {bankBalance:N0}", ChatMessageType.Broadcast));
                    }
                    else
                    {
                        // NO COIN IS PAID OUT INSTEAD, on either outcome, and the Failed case is why.
                        // The credit may have landed, and paying the same proceeds into the pack as
                        // well would be a straight duplication of them. Unlike a deposit - where the
                        // goods are still in the player's hands and returning them is the recoverable
                        // direction - the sold items are already gone here, so there is nothing to hand
                        // back and the choice is only between paying twice and paying once. The
                        // AnalyticsManager.RecordVendorPayment row above records the amount either way,
                        // which is what makes this reconcilable.
                        log.Error($"[BANK] {Name} (0x{Guid.Full:X8}): a vendor sale payout of {payoutCoinAmount:N0} pyreals came back {credit} and was NOT paid into the pack either. Reconcile against the vendor payment analytics row.");

                        Session.Network.EnqueueSend(new GameMessageSystemChat($"[BANK] Your sale proceeds of {payoutCoinAmount:N0} pyreals could not be banked. Do not re-sell - an admin needs to check the ledger.", ChatMessageType.Broadcast));
                    }
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
                    var overflowBanked = false;

                    if (overflowToBank > 0)
                    {
                        var credit = TryAdjustBankedPyreals(overflowToBank, "vendor payout overflow", out var newBankBalance);

                        overflowBanked = credit == AccountBankAdjustResult.Applied || credit == AccountBankAdjustResult.AppliedCountUnknown;

                        if (overflowBanked)
                        {
                            bankBalance = credit == AccountBankAdjustResult.Applied ? newBankBalance : BankedPyreals;
                        }
                        else
                        {
                            // The stacks were already destroyed above - they could not be placed, which
                            // is the whole reason they came here - so there is nothing left to hand
                            // back. This line is the only record of what the player is owed.
                            log.Error($"[BANK] {Name} (0x{Guid.Full:X8}): {overflowToBank:N0} pyreals of unplaceable vendor payout came back {credit} from the pool. The stacks are already destroyed; the player is owed that amount.");
                        }
                    }

                    UpdateCoinValue();
                    RushNextPlayerSave(5);

                    if (paidToPack > 0)
                        Session.Network.EnqueueSend(new GameMessageSystemChat($"You receive {paidToPack:N0} pyreals.", ChatMessageType.Broadcast));

                    if (overflowToBank > 0 && overflowBanked)
                        Session.Network.EnqueueSend(new GameMessageSystemChat($"[BANK] Your pack was full, so {overflowToBank:N0} pyreals went to your bank instead. Balance: {bankBalance:N0}", ChatMessageType.Broadcast));
                    else if (overflowToBank > 0)
                        Session.Network.EnqueueSend(new GameMessageSystemChat($"[BANK] Your pack was full and {overflowToBank:N0} pyreals could not be banked either. Do not re-sell - an admin needs to check the ledger.", ChatMessageType.Broadcast));
                }

                SellPhaseProfile.End(SellPhase.Payout, payoutScope);
            }

            Session.Network.EnqueueSend(new GameMessageSound(Guid, Sound.PickUpItem));

            SendUseDoneEvent();
        }

        /// <summary>
        /// The item-level sellability decision <see cref="VerifySellItems"/> applies to every candidate
        /// item, for the gates that need no other Player state - possession, duplicate-guid, amount, and
        /// the in-trade check stay inline in VerifySellItems, since the in-trade check needs
        /// ItemsInTradeWindow, which only a live Player carries. The non-empty-container gate lives HERE
        /// (fix round 1, F5) - it needs no Player state either, just <paramref name="wo"/> itself. Split
        /// out so it is testable without a Player, same rationale as
        /// Player_Inventory.GetAllPossessions's static core.
        ///
        /// Mule Vendor (DESIGN 9.2, section 9.2's Player_Commerce changes; risk R5's sibling economy
        /// risk - a bound item leaving one account's vault and reaching another via a share grant):
        /// <paramref name="isVault"/> bypasses IsSellable, Retained and Value &lt; 1 (see the edit-2
        /// table at the VerifySellItems call site). The vault's rule (user ruling 2026-08-29) is "can I
        /// hand this to another player?" - the same tests GiveObjectToPlayer (Player_Inventory.cs) and
        /// the trade window (Player_Trade.cs) apply: not attuned, not a pet device with its pet out,
        /// not mid-trade. IsSellable is a COIN gate ("this item has no vendor price"), not a transfer
        /// gate - a pyreal nugget (wcid 6354) carries it and hands to any player freely - so it has no
        /// say at a store. MerchandiseItemTypes stays enforced unconditionally: Task 11 authors the
        /// mule weenie's mask to accept every type in data, so a code bypass here is never needed. The
        /// Attuned check is unconditional for every vendor, not just a PersonalVendor - see the remarks
        /// at its call site.
        /// </summary>
        internal static bool IsAcceptableToSell(WorldObject wo, ItemType acceptedItemTypes, bool isVault, out string rejectionMessage)
        {
            // Fix round 1 (F7): local, not a variable computed up front - GetPluralName() falls back to
            // Name.Pluralize() (Humanizer), so it used to run on every call including the accept path.
            // Called only from the rejection branches below.
            string ItemName() => (wo.StackSize ?? 1) > 1 ? wo.GetPluralName() : wo.Name;

            // MerchandiseItemTypes mask: KEEP for every vendor including a PersonalVendor. It is
            // data-authored (see Edit 2 table) - a code bypass would silently widen a content author's
            // decision without anyone revisiting it.
            if ((acceptedItemTypes & wo.ItemType) == 0)
            {
                rejectionMessage = $"The {ItemName()} is unsellable."; // retail message did not include item name, leaving in that for now.
                return false;
            }

            // IsSellable: bypass for a PersonalVendor. It means "no vendor will pay coin for this"
            // (pyreal nuggets, IOUs, some tokens), which is a price statement, not a transfer
            // restriction - none of the give or trade paths read it. A vault pays nothing, so the flag
            // has nothing to protect there. Do not re-widen: live test 2026-08-29 hit a pyreal nugget
            // (wcid 6354) refused with "unsellable" for exactly this reason.
            if (!isVault && !wo.IsSellable)
            {
                rejectionMessage = $"The {ItemName()} is unsellable."; // retail message did not include item name, leaving in that for now.
                return false;
            }

            // Pet device with its pet summoned: refused at a vault, matching GiveObjectToPlayer
            // (Player_Inventory.cs) and Player_Trade.cs, both of which refuse it with the same wording.
            // The live pet holds a reference back to the device; storing the device would orphan it.
            // Vault-only because retail never checked it on a sale (the device is destroyed, not kept).
            if (isVault && wo is PetDevice petDevice && petDevice.Pet is not null)
            {
                rejectionMessage = "You must unsummon your pet before you can transfer this item!";
                return false;
            }

            // Retained: bypass for a PersonalVendor. Retained is a player-set "do not accidentally sell
            // this for coin" flag; depositing is not selling - the item survives, the player owns it,
            // and it is withdrawable. The items a player marks Retained are exactly the ones they most
            // want in a vault.
            if (!isVault && wo.Retained)
            {
                rejectionMessage = $"The {ItemName()} is unsellable."; // retail message did not include item name, leaving in that for now.
                return false;
            }

            // Mule Vendor (DESIGN 9.2): retail never rejected an attuned or bonded sale here - it only
            // declined to RESELL the item (Vendor.cs:713) while still paying out. That is harmless for a
            // coin vendor and wrong for a store, where the item survives and could be withdrawn by a
            // grantee on another account, which is cross-account transfer of a bound item. The check is
            // unconditional rather than PersonalVendor-only: paying a player for an attuned item and
            // then deleting it was never intended behavior on the retail path either.
            // ATTUNED ONLY, and Bonded is deliberately NOT refused. The first build refused both, on
            // the reading that Bonded also means "bound to this character" - it does not.
            // PropertyInt.Bonded is read only by the death handler (Player_Death.cs:549 filters bonded
            // items out of the death drop, :1086 handles BondedStatus.Destroy, :1083 handles Slippery)
            // and appears nowhere in Player_Trade.cs or the give path in Player_Inventory.cs. Bonded
            // means "does not drop on death", full stop, so it says nothing about whether an item may
            // change hands and is not this gate's business. Refusing it cost the vault a large fraction
            // of this fork's own authored gear (341 of 844 authored weenies carry one flag or the other)
            // for nothing. Do not re-widen this to Bonded.
            //
            // Attuned stays refused, and the reason is the VAULT rather than the sale - an attuned item
            // that survives a deposit can be withdrawn by a GRANTEE on another account, which is
            // cross-account transfer of a bound item. Compared with >= rather than ==, because
            // AttunedStatus.Sticky (2) is the STRONGER tier and == lets it straight through;
            // IsAttunedOrContainsAttuned is the exact predicate the give and trade paths use; the
            // Container override recurses into contents, so this stays correct even if a later
            // change lets a non-empty container through the gate below.
            if (wo.IsAttunedOrContainsAttuned)
            {
                rejectionMessage = $"The {ItemName()} is attuned to you and cannot be sold or stored.";
                return false;
            }

            // Value < 1: bypass for a PersonalVendor. It is a price gate. At zero price it means
            // nothing, and zero-value items exist (quest items, some tokens). Keeping it would make a
            // whole class of item permanently unstorable for no stated reason.
            if (!isVault && wo.Value < 1)
            {
                rejectionMessage = $"The {ItemName()} has no value and cannot be sold."; // retail message did not include item name, leaving in that for now.
                return false;
            }

            // Non-empty container: KEEP unconditionally, including for a PersonalVendor. DESIGN 9.2 is
            // explicit: players will try to store a full pack, v1 answers with a clear message, and
            // unpacking item-by-item is a deliberate v2 option rather than an oversight.
            //
            // Fix round 1 (F5): moved in from VerifySellItems - it needs no Player state (just wo
            // itself), so it belongs with the rest of the item-level gates rather than staying inline,
            // where it was untestable except as a tautology against data a test built itself.
            if (wo is Container container && container.Inventory.Count > 0)
            {
                rejectionMessage = $"You cannot sell that! The {ItemName()} must be empty - store what is inside it instead."; // fix round 1 (F6)
                return false;
            }

            rejectionMessage = null;
            return true;
        }

        /// <summary>
        /// Filters the list of ItemProfiles the player is attempting to sell to the vendor
        /// to the list of verified WorldObjects in the player's inventory w/ validations
        /// </summary>
        private Dictionary<uint, WorldObject> VerifySellItems(List<ItemProfile> sellItems, Vendor vendor)
        {
            var allPossessions = GetAllPossessions().ToDictionary(i => i.Guid.Full, i => i);

            var acceptedItemTypes = (ItemType)(vendor.MerchandiseItemTypes ?? 0);

            // Mule Vendor (DESIGN 9.2, edit 2): a PersonalVendor bypasses IsSellable, Retained and
            // Value < 1 - see IsAcceptableToSell's remarks. MerchandiseItemTypes is never bypassed.
            var isVault = vendor is PersonalVendor;

            var verified = new Dictionary<uint, WorldObject>();

            // Populated for EVERY profile that names a real possession, accepted or not. `verified` is
            // only populated on full acceptance, so using it as the duplicate guard means the guard
            // never arms against a vendor that refuses - and a PersonalVendor refuses every profile
            // from an unauthorized actor, each refusal costing its own shard round trip.
            var seen = new HashSet<uint>();

            // Hoisted out of the `if` only so the phase can close before the branch runs; the out locals stay
            // in scope exactly as they were and the call still happens once, here, before the loop.
            var accessScope = SellPhaseProfile.Begin();

            var accessResolved = vendor.TryResolveSellAccess(this, out var resolvedAccess, out var accessFailReason);

            SellPhaseProfile.End(SellPhase.Access, accessScope);

            if (!accessResolved)
            {
                Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, accessFailReason));
                return verified;
            }

            foreach (var sellItem in sellItems)
            {
                if (!allPossessions.TryGetValue(sellItem.ObjectGuid, out var wo))
                {
                    log.Warn($"[VENDOR] {Name} tried to sell item {sellItem.ObjectGuid:X8} not in their inventory to {vendor.Name}");
                    continue;
                }

                // verify item profile (unique guids, amount)
                if (!seen.Add(wo.Guid.Full))
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

                // PvP template gate, per item: an issued item is never sold or stored (a vault deposit is the Vault action).
                var itemTemplateRefusal = PvpTemplateBlocked(isVault ? PvpTemplateAction.Vault : PvpTemplateAction.VendorSell, wo);
                if (itemTemplateRefusal != null)
                {
                    SendPvpTemplateRefusal(itemTemplateRefusal);
                    continue;
                }

                // verify wo / vendor / player properties
                if (!IsAcceptableToSell(wo, acceptedItemTypes, isVault, out var rejectionMessage))
                {
                    Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, rejectionMessage));
                    continue;
                }

                // Fix round 1 (F1): a NON-MUTATING preflight, checked before anything below detaches
                // this item from the player's pack. Retail's own ProcessItemsForPurchase can never
                // decline (it always either resells or destroys), so retail never needed this - a
                // PersonalVendor's CAN decline (full vault, store not loaded, access revoked, or a
                // RequiresPackSlot item like an empty backpack that VerifySellItems's own gates never
                // reject), and without this check the item would already be detached, reported to the
                // client as vendor-owned, and flushed to the shard with a null ContainerId before
                // ProcessItemsForPurchase ever ran - an orphaned biota, not a refused sale. This does
                // NOT replace the hand-back inside PersonalVendor.DepositItems: a preflight is TOCTOU
                // by construction (a second window on the same store can change the answer between this
                // check and the actual deposit, per R3), so both halves ship together.
                //
                // Timed as its own leaf phase (SellPhase.Accept), hoisted out of the `if` for the same
                // scope-closing reason as TryResolveSellAccess above: on a PersonalVendor this reaches
                // CanAcceptCore and, by that method's own remarks, VaultCollapse.IsPristine - a second time
                // per item, since TryDeposit calls it again later. That duplication is one of the things this
                // instrument exists to size, so it must not be folded into sell_validate_ms.
                var acceptScope = SellPhaseProfile.Begin();

                var vendorAccepted = vendor.CanAccept(wo, this, resolvedAccess, out var vendorReason);

                SellPhaseProfile.End(SellPhase.Accept, acceptScope);

                if (!vendorAccepted)
                {
                    Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, vendorReason));
                    continue;
                }

                if (IsTrading && wo.IsBeingTradedOrContainsItemBeingTraded(ItemsInTradeWindow))
                {
                    var itemName = (wo.StackSize ?? 1) > 1 ? wo.GetPluralName() : wo.Name;
                    Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, $"You cannot sell that! The {itemName} is currently being traded.")); // custom message?
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

        // INTERNAL, not private: ACE.Server.RefireStations.RefireStationCommon.TryCharge (shared by the
        // Workmanship Reforge, Arcane Alignment Table and Defense Requirement Reforge) reuses this for its
        // own currency debit (pack MMDs first, banked pyreals for the remainder) rather than writing a second
        // bank-debit routine. destroy:true is required to reach the always-on banking shop hook below; a
        // non-destroy caller only ever gets the inventory-only CollectCurrencyStacks path.
        internal List<WorldObject> SpendCurrency(uint currentWcid, uint amount, bool destroy = false)
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

                // Pyreals and MMDs both draw on the account pool, which is keyed by account id. Without
                // one, BankedPyreals reads as 0 and the shortfall check below would refuse anyway - but
                // it would refuse with a message about the balance, which is not what happened.
                if (fromBank > 0 && Account == null && (currentWcid == coinStackWcid || currentWcid == MmdWcid))
                {
                    BankMsg(BankUnavailableMessage);
                    return null;
                }

                long bankAvail = currentWcid == coinStackWcid ? BankedPyreals : GetBankedAlternateCurrency(currentWcid);
                if (fromBank > bankAvail)
                {
                    // Upstream validation should make this impossible; refuse rather than hand over goods for free.
                    log.Error($"[BANK] {Name} SpendCurrency shortfall on wcid {currentWcid}: need {fromBank} from bank, have {bankAvail}");
                    return null;
                }

                // THE BANK DEBIT RUNS BEFORE THE INVENTORY IS TOUCHED, which is the reverse of how this
                // read while the balance was a biota property that could not fail. Consuming the pack
                // coin first and then finding the pool refuses would take the player's coin and hand
                // over nothing. Nothing has been spent at the point of each `return null` below.
                if (fromBank > 0)
                {
                    if (currentWcid == coinStackWcid)
                    {
                        var debit = TryAdjustBankedPyreals(-fromBank, "vendor purchase", out _);

                        if (debit == AccountBankAdjustResult.Refused)
                        {
                            BankMsg(BankDebitRefusedMessage);
                            return null;
                        }

                        // Failed refuses the purchase, which is the opposite of what a withdraw does
                        // with the same outcome, and deliberately: a withdraw has already taken the
                        // pyreals and owes the player something, while here the goods have not moved
                        // and refusing costs the player nothing beyond a retry. The LEDGER STATE
                        // UNKNOWN line is already written by TryAdjustBankedPyreals.
                        if (debit == AccountBankAdjustResult.Failed)
                        {
                            BankMsg(BankStateUnknownPurchaseMessage);
                            return null;
                        }

                        UpdateCoinValue();
                        Session.Network.EnqueueSend(new GameMessageSystemChat($"[BANK] Debited {fromBank:N0} pyreals from your bank.", ChatMessageType.Broadcast));
                    }
                    else
                    {
                        var debited = DebitBankedAlternateCurrency(currentWcid, fromBank, out var stateUnknown);

                        if (debited == null)
                        {
                            // Only the MMD branch can report an unknown state, because only it is
                            // backed by the account pool; the other two mutate a biota property.
                            BankMsg(stateUnknown ? BankStateUnknownPurchaseMessage : BankDebitRefusedMessage);

                            log.Error($"[BANK] {Name} SpendCurrency could not debit {fromBank} of alternate currency wcid {currentWcid} ({(stateUnknown ? "ledger state unknown" : "refused")}); the purchase is cancelled.");

                            return null;
                        }

                        // MMDs are not their own banked balance - they draw pyreals, so spell that out for the player.
                        var bankMsg = currentWcid == MmdWcid
                            ? $"[BANK] Debited {fromBank:N0} {altName} ({fromBank * MmdValue:N0} pyreals) from your bank."
                            : $"[BANK] Debited {fromBank:N0} {altName} from your bank.";
                        Session.Network.EnqueueSend(new GameMessageSystemChat(bankMsg, ChatMessageType.Broadcast));
                    }
                }

                if (fromInv > 0)
                    TryConsumeFromInventoryWithNetworking(currentWcid, (int)fromInv);

                if (fromBank > 0)
                    RushNextPlayerSave(5);

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
