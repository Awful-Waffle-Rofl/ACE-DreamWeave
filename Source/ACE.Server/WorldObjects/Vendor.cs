using System;
using System.Collections.Generic;
using System.Linq;

using log4net;

using ACE.Common;
using ACE.Database;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.ClassAbilities;
using ACE.Server.Entity;
using ACE.Server.Entity.AccountVault;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Managers;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// ** Buy Data Flow **
    ///
    /// Player.HandleActionBuyItem -> Vendor.BuyItems_ValidateTransaction -> Player.FinalizeBuyTransaction -> Vendor.BuyItems_FinalTransaction
    ///     
    /// </summary>
    /// <remarks>
    /// Several members below are virtual solely so PersonalVendor (the Mule Vendor's private-store
    /// window) can replace retail behavior that is actively wrong for a store rather than merely
    /// unsuitable: ProcessItemsForPurchase destroys anything stackable and deletes the biotas it keeps,
    /// RotUniques discards player-sold stock after 300 s, and both cost functions floor at 1. See
    /// Docs/MuleVendor/DESIGN.md section 9.1. No behavior here changed when they were virtualized.
    /// </remarks>
    public class Vendor : Creature
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public readonly Dictionary<ObjectGuid, WorldObject> DefaultItemsForSale = new Dictionary<ObjectGuid, WorldObject>();

        // unique items purchased from other players
        public readonly Dictionary<ObjectGuid, WorldObject> UniqueItemsForSale = new Dictionary<ObjectGuid, WorldObject>();

        private bool inventoryloaded { get; set; }

        /// <summary>
        ///  The last player who used this vendor
        /// </summary>
        private WorldObjectInfo lastPlayerInfo { get; set; }

        public uint? AlternateCurrency
        {
            get => GetProperty(PropertyDataId.AlternateCurrency);
            set { if (!value.HasValue) RemoveProperty(PropertyDataId.AlternateCurrency); else SetProperty(PropertyDataId.AlternateCurrency, value.Value); }
        }

        /// <summary>
        /// A new biota be created taking all of its values from weenie.
        /// </summary>
        public Vendor(Weenie weenie, ObjectGuid guid) : base(weenie, guid)
        {
            SetEphemeralValues();
        }

        /// <summary>
        /// Restore a WorldObject from the database.
        /// </summary>
        public Vendor(Biota biota) : base(biota)
        {
            SetEphemeralValues();
        }

        private void SetEphemeralValues()
        {
            ObjectDescriptionFlags |= ObjectDescriptionFlag.Vendor;

            if (!PropertyManager.GetBool("vendor_shop_uses_generator").Item)
            {
                GeneratorProfiles.RemoveAll(p => p.Biota.WhereCreate.HasFlag(RegenLocationType.Shop));
            }

            OpenForBusiness = ValidateVendorRequirements();
        }

        private bool ValidateVendorRequirements()
        {
            var success = true;

            var currencyWCID = AlternateCurrency ?? (uint)ACE.Entity.Enum.WeenieClassName.W_COINSTACK_CLASS;
            var currencyWeenie = DatabaseManager.World.GetCachedWeenie(currencyWCID);
            if (currencyWeenie == null)
            {
                var errorMsg = $"WCID {currencyWCID}{(AlternateCurrency.HasValue ? ", which comes from PropertyDataId.AlternateCurrency," : "")} is not found in the database, Vendor has been disabled as a result!";
                log.Error($"[VENDOR] {Name} (0x{Guid}:{WeenieClassId}) Currency {errorMsg}");
                success = false;
            }

            if (!MerchandiseItemTypes.HasValue)
            {
                log.Error($"[VENDOR] {Name} (0x{Guid}:{WeenieClassId}) MerchandiseItemTypes is NULL, Vendor has been disabled as a result!");
                success = false;
            }

            if (!MerchandiseMinValue.HasValue)
            {
                log.Error($"[VENDOR] {Name} (0x{Guid}:{WeenieClassId}) MerchandiseMinValue is NULL, Vendor has been disabled as a result!");
                success = false;
            }

            if (!MerchandiseMaxValue.HasValue)
            {
                log.Error($"[VENDOR] {Name} (0x{Guid}:{WeenieClassId}) MerchandiseMaxValue is NULL, Vendor has been disabled as a result!");
                success = false;
            }

            if (!BuyPrice.HasValue)
            {
                log.Error($"[VENDOR] {Name} (0x{Guid}:{WeenieClassId}) BuyPrice is NULL, Vendor has been disabled as a result!");
                success = false;
            }

            if (!SellPrice.HasValue)
            {
                log.Error($"[VENDOR] {Name} (0x{Guid}:{WeenieClassId}) SellPrice is NULL, Vendor has been disabled as a result!");
                success = false;
            }

            return success;
        }

        /// <summary>
        /// Populates this vendor's DefaultItemsForSale
        /// </summary>
        protected virtual void LoadInventory()
        {
            if (inventoryloaded) return;

            var itemsForSale = new Dictionary<(uint weenieClassId, int paletteTemplate, double shade), uint>();

            foreach (var item in Biota.PropertiesCreateList.Where(x => x.DestinationType == DestinationType.Shop))
                LoadInventoryItem(itemsForSale, item.WeenieClassId, item.Palette, item.Shade, item.StackSize);

            //if (Biota.PropertiesGenerator != null && !PropertyManager.GetBool("vendor_shop_uses_generator").Item)
            //{
            //    foreach (var item in Biota.PropertiesGenerator.Where(x => x.WhereCreate.HasFlag(RegenLocationType.Shop)))
            //        LoadInventoryItem(itemsForSale, item.WeenieClassId, (int?)item.PaletteId, item.Shade, item.StackSize);
            //}

            inventoryloaded = true;
        }

        private void LoadInventoryItem(Dictionary<(uint weenieClassId, int paletteTemplate, double shade), uint> itemsForSale,
            uint weenieClassId, int? palette, float? shade, int? stackSize)
        {
            //var itemProfile = (weenieClassId, palette ?? 0, shade ?? 0);

            // let's skip dupes if there are any
            //if (itemsForSale.ContainsKey(itemProfile))
            //    return;

            var wo = WorldObjectFactory.CreateNewWorldObject(weenieClassId);

            if (wo == null) return;

            if (palette > 0)
                wo.PaletteTemplate = palette;

            if (shade > 0)
                wo.Shade = shade;

            wo.ContainerId = Guid.Full;

            wo.CalculateObjDesc();

            //itemsForSale.Add(itemProfile, wo.Guid.Full);

            wo.VendorShopCreateListStackSize = stackSize ?? -1;

            DefaultItemsForSale.Add(wo.Guid, wo);
        }

        /// <summary>
        /// Virtual so PersonalVendor can add a redundant guard here: refuse (and log loudly) if the
        /// item being added is one of its own store's biotas. See Docs/MuleVendor/DESIGN.md section 5
        /// and PersonalVendor.cs. Retail behavior is unchanged - the base implementation below.
        /// </summary>
        public virtual void AddDefaultItem(WorldObject item)
        {
            var existing = GetDefaultItemsByWcid(item.WeenieClassId);

            // add to existing stack?
            if (existing.Count > 0)
            {
                var stackLeft = existing.FirstOrDefault(i => (i.StackSize ?? 1) < (i.MaxStackSize ?? 1));
                if (stackLeft != null)
                {
                    stackLeft.SetStackSize((stackLeft.StackSize ?? 1) + 1);
                    return;
                }
            }

            // create new item
            item.ContainerId = Guid.Full;

            item.CalculateObjDesc();

            DefaultItemsForSale.Add(item.Guid, item);
        }

        /// <summary>
        /// Helper function to replace the previous 'AllItemsForSale' combiner
        /// While AllItemsForSale was a useful concept, it was only used in 2 places, and was inefficient
        /// </summary>
        public virtual void forEachItem(Action<WorldObject> action)
        {
            foreach (var kvp in DefaultItemsForSale)
                action(kvp.Value);

            foreach (var kvp in UniqueItemsForSale)
                action(kvp.Value);
        }

        /// <summary>
        /// Virtual so a subclass whose <see cref="forEachItem"/> enumerates something other than just
        /// these two dictionaries (PersonalVendor also enumerates a private storeItems dictionary -
        /// see Docs/MuleVendor/DESIGN.md section 5) cannot drift from what GameEventApproachVendor
        /// actually serializes. GameEventApproachVendor.cs computes numItems from this property rather
        /// than re-deriving its own count, specifically so the two can never disagree.
        /// </summary>
        public virtual int ItemsForSaleCount => DefaultItemsForSale.Count + UniqueItemsForSale.Count;

        public List<WorldObject> GetDefaultItemsByWcid(uint wcid)
        {
            return DefaultItemsForSale.Values.Where(i => i.WeenieClassId == wcid).ToList();
        }

        /// <summary>
        /// Searches the vendor's inventory for an item
        /// </summary>
        public virtual bool TryGetItemForSale(ObjectGuid itemGuid, out WorldObject itemForSale)
        {
            return DefaultItemsForSale.TryGetValue(itemGuid, out itemForSale) || UniqueItemsForSale.TryGetValue(itemGuid, out itemForSale);
        }

        /// <summary>
        /// This is raised by Player.HandleActionUseItem.<para />
        /// The item does not exist in the players possession.<para />
        /// If the item was outside of range, the player will have been commanded to move using DoMoveTo before ActOnUse is called.<para />
        /// When this is called, it should be assumed that the player is within range.
        /// </summary>
        public override void ActOnUse(WorldObject wo)
        {
            var player = wo as Player;
            if (player == null) return;

            if (player.IsBusy)
            {
                player.SendWeenieError(WeenieError.YoureTooBusy);
                return;
            }

            if (!OpenForBusiness || !ValidateVendorRequirements())
            {
                // should there be some sort of feedback to player here?
                return;
            }

            var rotateTime = Rotate(player);    // vendor rotates towards player

            // TODO: remove this when DelayManager is not forward propagating current tick time

            var actionChain = new ActionChain();
            actionChain.AddDelaySeconds(0.001f);  // force to run after rotate.EnqueueBroadcastAction
            actionChain.AddAction(this, LoadInventory);
            actionChain.AddDelaySeconds(rotateTime);
            actionChain.AddAction(this, () => ApproachVendor(player, VendorType.Open));
            actionChain.EnqueueChain();

            if (lastPlayerInfo == null)
            {
                var closeChain = new ActionChain();
                closeChain.AddDelaySeconds(closeInterval);
                closeChain.AddAction(this, CheckClose);
                closeChain.EnqueueChain();
            }

            lastPlayerInfo = new WorldObjectInfo(player);
        }

        protected virtual void PrepareResetToHome()
        {
            // Reset to Home position
            var resetInterval = ResetInterval ?? 300;
            ResetTimestamp = Time.GetFutureUnixTime(resetInterval);

            var autoResetTimer = new ActionChain();
            autoResetTimer.AddDelaySeconds(resetInterval);
            autoResetTimer.AddAction(this, () => CheckResetToHome());
            autoResetTimer.EnqueueChain();
        }

        /// <summary>
        /// Sends the latest vendor inventory list to player, rotates vendor towards player, and performs the appropriate emote.
        /// </summary>
        /// <param name="action">The action performed by the player</param>
        public virtual void ApproachVendor(Player player, VendorType action = VendorType.Undef, uint altCurrencySpent = 0)
        {
            RotUniques();

            player.Session.Network.EnqueueSend(new GameEventApproachVendor(player.Session, this, altCurrencySpent));

            var rotateTime = Rotate(player); // vendor rotates to player

            if (action != VendorType.Undef)
                DoVendorEmote(action, player);

            player.LastOpenedContainerId = Guid;

            PrepareResetToHome();
        }

        public void DoVendorEmote(VendorType vendorType, WorldObject player)
        {
            switch (vendorType)
            {
                case VendorType.Open:
                    EmoteManager.DoVendorEmote(vendorType, player);
                    break;

                case VendorType.Buy:    // player buys item from vendor
                    EmoteManager.DoVendorEmote(vendorType, player);
                    break;

                case VendorType.Sell:   // player sells item to vendor
                    EmoteManager.DoVendorEmote(vendorType, player);
                    break;

                default:
                    log.Warn($"Vendor.DoVendorEmote - Encountered Unhandled VendorType {vendorType} for {Name} ({WeenieClassId})");
                    break;
            }
        }

        private const float closeInterval = 1.5f;

        /// <summary>
        /// After a player approaches a vendor, this is called every closeInterval seconds
        /// to see if the player is still within the UseRadius of the vendor.
        /// 
        /// If the player has moved away, the vendor Close emote is called (waving goodbye, saying farewell)
        /// </summary>
        public void CheckClose()
        {
            if (lastPlayerInfo == null)
                return;

            var lastPlayer = lastPlayerInfo.TryGetWorldObject() as Player;

            if (lastPlayer == null)
            {
                lastPlayerInfo = null;
                return;
            }

            // handles player logging out at vendor
            if (lastPlayer.CurrentLandblock == null)
            {
                lastPlayerInfo = null;
                return;
            }

            var dist = GetCylinderDistance(lastPlayer);

            if (dist > UseRadius)
            {
                if (lastPlayer.LastOpenedContainerId == Guid)
                    lastPlayer.LastOpenedContainerId = ObjectGuid.Invalid;

                EmoteManager.DoVendorEmote(VendorType.Close, lastPlayer);
                lastPlayerInfo = null;

                return;
            }

            var closeChain = new ActionChain();
            closeChain.AddDelaySeconds(closeInterval);
            closeChain.AddAction(this, CheckClose);
            closeChain.EnqueueChain();
        }

        public void CheckResetToHome()
        {
            if (Time.GetUnixTime() >= ResetTimestamp)
            {
                // are we already at home origin?
                if (Location.Pos.Equals(Home.Pos))
                {
                    // just turnto if required?
                    if (!Location.Rotation.Equals(Home.Rotation))
                    {
                        TurnTo(Home);
                    }
                }
                else
                {
                    MoveTo(Home, GetRunRate(), true, null, 1);
                }
            }
        }

        /// <summary>
        /// Creates world objects for generic items
        /// </summary>
        private List<WorldObject> ItemProfileToWorldObjects(ItemProfile itemProfile)
        {
            var results = new List<WorldObject>();

            var remaining = itemProfile.Amount;

            while (remaining > 0)
            {
                var wo = WorldObjectFactory.CreateNewWorldObject(itemProfile.WeenieClassId);

                if (itemProfile.Palette != null)
                    wo.PaletteTemplate = itemProfile.Palette;

                if (itemProfile.Shade != null)
                    wo.Shade = itemProfile.Shade;

                if ((wo.MaxStackSize ?? 0) > 0)
                {
                    // stackable
                    var currentStackSize = Math.Min(remaining, wo.MaxStackSize.Value);

                    wo.SetStackSize(currentStackSize);
                    results.Add(wo);
                    remaining -= currentStackSize;
                }
                else
                {
                    // non-stackable
                    wo.StackSize = null;
                    results.Add(wo);
                    remaining--;
                }
            }
            return results;
        }

        /// <summary>
        /// Handles validation for player buying items from vendor
        /// </summary>
        public virtual bool BuyItems_ValidateTransaction(List<ItemProfile> itemProfiles, Player player)
        {
            // one difference between buy and sell currently
            // is that if *any* items in the buy transactions are detected as invalid,
            // we reject the entire transaction.
            // this seems to be the "safest" route, however in terms of player convenience
            // where only 1 item has an error from a large purchase set,
            // this might not be the most convenient for the player.

            var defaultItemProfiles = new List<ItemProfile>();
            var uniqueItems = new List<WorldObject>();

            // find item profiles in default and unique items
            foreach (var itemProfile in itemProfiles)
            {
                if (!itemProfile.IsValidAmount)
                {
                    // reject entire transaction immediately
                    player.SendTransientError($"Invalid amount");
                    return false;
                }

                var itemGuid = new ObjectGuid(itemProfile.ObjectGuid);

                // check default items
                if (DefaultItemsForSale.TryGetValue(itemGuid, out var defaultItemForSale))
                {
                    itemProfile.WeenieClassId = defaultItemForSale.WeenieClassId;
                    itemProfile.Palette = defaultItemForSale.PaletteTemplate;
                    itemProfile.Shade = defaultItemForSale.Shade;

                    defaultItemProfiles.Add(itemProfile);
                }
                // check unique items
                else if (UniqueItemsForSale.TryGetValue(itemGuid, out var uniqueItemForSale))
                {
                    uniqueItems.Add(uniqueItemForSale);
                }
            }

            // ensure player has enough free inventory slots / container slots / available burden to receive items
            var itemsToReceive = new ItemsToReceive(player);

            foreach (var defaultItemProfile in defaultItemProfiles)
            {
                itemsToReceive.Add(defaultItemProfile.WeenieClassId, defaultItemProfile.Amount);

                if (itemsToReceive.PlayerExceedsLimits)
                    break;
            }

            if (!itemsToReceive.PlayerExceedsLimits)
            {
                foreach (var uniqueItem in uniqueItems)
                {
                    itemsToReceive.Add(uniqueItem.WeenieClassId, uniqueItem.StackSize ?? 1);

                    if (itemsToReceive.PlayerExceedsLimits)
                        break;
                }
            }

            if (itemsToReceive.PlayerExceedsLimits)
            {
                if (itemsToReceive.PlayerExceedsAvailableBurden)
                    player.Session.Network.EnqueueSend(new GameEventCommunicationTransientString(player.Session, "You are too encumbered to buy that!"));
                else if (itemsToReceive.PlayerOutOfInventorySlots)
                    player.Session.Network.EnqueueSend(new GameEventCommunicationTransientString(player.Session, "You do not have enough pack space to buy that!"));
                else if (itemsToReceive.PlayerOutOfContainerSlots)
                    player.Session.Network.EnqueueSend(new GameEventCommunicationTransientString(player.Session, "You do not have enough container slots to buy that!"));

                return false;
            }

            // ideally the creation of the wo's would be delayed even further,
            // and all validations would be performed on weenies beforehand
            // this would require:
            // - a forEach helper function to iterate through both defaultItemProfiles (ItemProfiles) and uniqueItems (WorldObjects),
            //   so that 2 foreach iterators don't have to be written each time
            // - weenie to have more functions that mimic the functionality of WorldObject

            // create world objects for default items
            var defaultItems = new List<WorldObject>();

            foreach (var defaultItemProfile in defaultItemProfiles)
                defaultItems.AddRange(ItemProfileToWorldObjects(defaultItemProfile));

            var purchaseItems = defaultItems.Concat(uniqueItems).ToList();

            if (IsBusy && purchaseItems.Any(i => i.GetProperty(PropertyBool.VendorService) == true))
            {
                player.SendWeenieErrorWithString(WeenieErrorWithString._IsTooBusyToAcceptGifts, Name);
                CleanupCreatedItems(defaultItems);
                return false;
            }

            // check uniques
            if (!player.CheckUniques(purchaseItems, this))
            {
                CleanupCreatedItems(defaultItems);
                return false;
            }

            // Drift Network trainer vendors: reject a class-ability token the player can't currently learn
            // (wrong rank order, a locked tier, already holding a voucher, or buying more than one) BEFORE
            // charging any class ability points. Ordinary vendors stock no such items and pass straight through.
            if (!ClassAbilityTokensBuyable(purchaseItems, player))
            {
                CleanupCreatedItems(defaultItems);
                return false;
            }

            // calculate price
            // Accumulate in a ulong so a large purchase set can't roll a uint total over into a
            // small number (which would let the player be charged far less than the true cost).
            ulong totalPriceAccum = 0;

            foreach (var item in purchaseItems)
            {
                var cost = GetSellCost(item);

                totalPriceAccum += cost;
            }

            // No legitimate purchase can exceed uint range, and the player can never hold that much
            // currency, so treat an overflowing total as simply unaffordable.
            if (totalPriceAccum > uint.MaxValue)
            {
                CleanupCreatedItems(defaultItems);
                return false;
            }

            uint totalPrice = (uint)totalPriceAccum;

            // verify player has enough currency
            // Banking shop hook (always on): banked pyreals back a coin purchase, and banked promissory
            // notes back their alternate-currency vendor (Absalom Sarraf). Currencies we don't bank
            // contribute 0, so those vendors stay inventory-only.
            if (AlternateCurrency == null)
            {
                if ((player.CoinValue ?? 0) + player.BankedPyreals < totalPrice)
                {
                    CleanupCreatedItems(defaultItems);
                    return false;
                }
            }
            else
            {
                var playerAltCurrency = player.GetNumInventoryItemsOfWCID(AlternateCurrency.Value);

                if (playerAltCurrency + player.GetBankedAlternateCurrency(AlternateCurrency.Value) < totalPrice)
                {
                    CleanupCreatedItems(defaultItems);
                    return false;
                }
            }

            // everything is verified at this point

            // send transaction to player for further processing
            player.FinalizeBuyTransaction(this, defaultItems, uniqueItems, totalPrice);

            return true;
        }

        /// <summary>
        /// Buy-eligibility gate for Drift Network class-ability trainer vendors. A trainer stocks every rank of
        /// its class's tokens; this rejects the whole transaction if the player cannot currently learn one of
        /// the tokens being bought - wrong rank order, a locked tier, already holding a voucher (all via the
        /// shared <see cref="Player.CanBuyClassAbilityToken"/>), or more than one copy of the same token in
        /// the one transaction (a bound one-per-skill voucher is never bought in bulk). Non-token purchases
        /// pass through untouched, so an ordinary vendor is unaffected. On rejection sends a transient error
        /// and returns FALSE.
        /// </summary>
        private static bool ClassAbilityTokensBuyable(List<WorldObject> purchaseItems, Player player)
        {
            var seenTokenSkills = new HashSet<int>();

            foreach (var item in purchaseItems)
            {
                var tokenSkillId = item.GetProperty(PropertyInt.ClassAbilityTokenId) ?? 0;
                if (tokenSkillId <= 0)
                    continue;   // not a class-ability token - ordinary merchandise

                // ONE voucher per skill per TRANSACTION, not merely per pack. Both of the other guards miss
                // a multi-copy buy:
                //  - CanBuyClassAbilityToken's hold test reads GetAllPossessions(), which cannot see the
                //    other items in this same purchase, because none of them are in the pack yet;
                //  - the StackSize test below never fires either, because a quantity above one on a
                //    MaxStackSize-1 token does not arrive as one stack of N. ItemProfileToWorldObjects
                //    takes the STACKABLE branch for it (MaxStackSize 1 is still > 0) and emits N separate
                //    one-item stacks, each of which then passes every per-item check on its own.
                // Without this, buying N copies charged N x cost and handed over N tokens of which only the
                // first was usable - the rest report a rank the player already has and sit dead in the pack.
                if (!seenTokenSkills.Add(tokenSkillId))
                {
                    player.SendTransientError("You can only buy one class ability training token at a time.");
                    return false;
                }

                if ((item.StackSize ?? 1) > 1)
                {
                    player.SendTransientError("You can only buy one class ability training token at a time.");
                    return false;
                }

                // Registry, not Enum.IsDefined: generated Enhanced-stat tokens use synthetic (0x1000+) ids that
                // are registered but not named enum members.
                if (tokenSkillId <= 0 ||
                    !ClassAbilityRegistry.Abilities.TryGetValue((ClassAbilityId)tokenSkillId, out var def))
                {
                    player.SendTransientError($"The {item.Name} is misconfigured and cannot be sold.");
                    return false;
                }

                var tier = item.GetProperty(PropertyInt.ClassAbilityTokenTier) ?? 0;

                if (!player.CanBuyClassAbilityToken(def, tier, out var error))
                {
                    player.SendTransientError(error);
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Price of a purchase item, priced PER UNIT and multiplied by the stack size.
        ///
        /// A quantity-N buy of a stackable arrives here as ONE WorldObject with StackSize = N
        /// (ItemProfileToWorldObjects), whose Value SetStackSize has already rewritten to
        /// StackUnitValue * N. Pricing that aggregate Value directly looks equivalent, but it is not,
        /// because the Math.Max(1, ...) floor and the Ceiling then apply ONCE to the whole stack
        /// instead of once per unit:
        ///   - a Value-0 stackable (whose price comes entirely from the floor) cost 1 for the WHOLE
        ///     stack - 100 Black Market Health Elixirs for 1 MMD on the Marketplace sundries vendor;
        ///   - any item whose per-unit price is fractional was rounded up once rather than N times -
        ///     1000 Promissory Notes at SellPrice 1.25 cost 1250 instead of 2000.
        /// The client prices the same purchase as (unit price) x (quantity) - GameEventApproachVendor
        /// sends it only the sell rate and the item's unit Value, so it cannot do anything else - so
        /// per-unit is also the arithmetic that makes the charge match what the player was shown.
        /// </summary>
        public virtual uint GetSellCost(WorldObject item)
        {
            var stackSize = Math.Max(1, item.StackSize ?? 1);

            return CalcSellCost(SellPrice ?? 1.0, GetStackUnitValue(item.StackUnitValue, item.Value, stackSize), item.ItemType, stackSize);
        }

        /// <summary>
        /// Weenie overload: a weenie's Value is already the UNIT value (a shop listing, never a stack),
        /// so this is the per-unit price and takes no stack multiplier.
        /// </summary>
        public virtual uint GetSellCost(Weenie item) => CalcSellCost(SellPrice ?? 1.0, item.GetValue(), item.GetItemType(), 1);

        /// <summary>
        /// The value of ONE unit of a (possibly stacked) item.
        ///
        /// StackUnitValue is the authoritative per-unit figure and is used whenever it is set. It is NOT
        /// always set: a stackable weenie with no INT 15 row leaves it null while SetStackSize has still
        /// rewritten Value to 0 * StackSize. The stackSize > 1 fallback divides the aggregate Value back
        /// out rather than treating a whole stack's worth as one unit's, which would then be multiplied
        /// by the stack size a second time.
        /// Internal rather than private so ACE.Server.Tests can pin it (InternalsVisibleTo, ACE.Server.csproj:15).
        /// </summary>
        internal static int? GetStackUnitValue(int? stackUnitValue, int? value, int stackSize)
        {
            if (stackUnitValue != null)
                return stackUnitValue;

            if (stackSize > 1)
                return (value ?? 0) / stackSize;

            return value;
        }

        /// <summary>
        /// max(1, ceil(sellRate * unitValue - 0.1)) per unit, times stackSize, clamped to uint range.
        /// Internal rather than private so ACE.Server.Tests can pin it (InternalsVisibleTo, ACE.Server.csproj:15).
        /// </summary>
        internal static uint CalcSellCost(double sellRate, int? unitValue, ItemType? itemType, int stackSize)
        {
            if (itemType == ItemType.PromissoryNote)
                sellRate = 1.15;

            var unitCost = Math.Max(1, (uint)Math.Ceiling(((float)sellRate * (unitValue ?? 0)) - 0.1));

            // Nothing legitimate reaches uint range, and BuyItems_ValidateTransaction rejects a basket
            // total above it anyway, but clamp rather than let a hostile stack size wrap the product.
            var total = (ulong)unitCost * (ulong)Math.Max(1, stackSize);

            return (uint)Math.Min(total, uint.MaxValue);
        }

        /// <summary>
        /// What the vendor PAYS the player for an item. Deliberately still priced off the whole stack's
        /// aggregate Value, unlike GetSellCost above, because it does not share that method's failure
        /// mode: this one Floors rather than Ceilings, so the difference between pricing a stack once
        /// and pricing each unit is under one currency unit no matter how large the stack, and it errs
        /// toward the player rather than away from them. There is a pre-existing quirk at any vendor
        /// with BuyPrice below 1, where splitting a stack into singles first collects the Math.Max(1, ...)
        /// floor once per item instead of once per stack; that is untouched here and out of scope.
        /// </summary>
        public virtual int GetBuyCost(WorldObject item) => GetBuyCost(item.Value, item.ItemType);

        public virtual int GetBuyCost(Weenie item) => GetBuyCost(item.GetValue(), item.GetItemType());

        private int GetBuyCost(int? value, ItemType? itemType)
        {
            var buyRate = BuyPrice ?? 1;
            if (itemType == ItemType.PromissoryNote)
                buyRate = 1.0;

            var cost = Math.Max(1, (int)Math.Floor(((float)buyRate * (value ?? 0)) + 0.1));
            return cost;
        }

        public virtual int CalculatePayoutCoinAmount(Dictionary<uint, WorldObject> items)
        {
            var payout = 0;

            foreach (WorldObject item in items.Values)
                payout += GetBuyCost(item);

            return payout;
        }

        /// <summary>
        /// A NON-MUTATING preflight, checked BEFORE VerifySellItems detaches an item from the player's
        /// pack. Retail's own ProcessItemsForPurchase can never decline (it always either resells or
        /// destroys), so retail never needed this - a subclass whose ProcessItemsForPurchase CAN
        /// decline (PersonalVendor's can, on a full or inaccessible vault) needs a way to say so before
        /// the item is already detached and persisted in that state, not just after. Base is a no-op
        /// pass so ordinary vendors are unaffected. This check is TOCTOU by nature - a second window on
        /// the same store can change the answer between this call and the actual deposit - so whatever
        /// consults it must still handle a later refusal from the deposit itself; this only shrinks the
        /// window, it does not close it.
        /// </summary>
        public virtual bool CanAccept(WorldObject wo, Player player, out string reason)
        {
            reason = null;
            return true;
        }

        /// <summary>
        /// Resolves any per-transaction state the vendor's per-item CanAccept needs, once, before the
        /// caller's profile loop. A plain vendor has none and answers with the permissive default.
        /// </summary>
        public virtual bool TryResolveSellAccess(Player player, out VaultAccess resolvedAccess, out string failReason)
        {
            resolvedAccess = VaultAccess.DepositWithdraw;
            failReason = null;
            return true;
        }

        /// <summary>
        /// Per-item acceptance, given access already resolved by TryResolveSellAccess. The base
        /// implementation ignores the resolved value and defers to the existing single-item overload.
        /// </summary>
        public virtual bool CanAccept(WorldObject wo, Player player, VaultAccess resolvedAccess, out string reason)
        {
            return CanAccept(wo, player, out reason);
        }

        /// <summary>
        /// This will either add the item to the vendors temporary sellables, or destroy it.<para />
        /// In both cases, the item will be removed from the database.<para />
        /// The item should already have been removed from the players inventory
        /// </summary>
        public virtual void ProcessItemsForPurchase(Player player, Dictionary<uint, WorldObject> items)
        {
            foreach (var item in items.Values)
            {
                var resellItem = true;

                // don't resell DestroyOnSell
                if (item.GetProperty(PropertyBool.DestroyOnSell) ?? false)
                    resellItem = false;

                // don't resell Attuned items that can be sold
                if (item.Attuned == AttunedStatus.Attuned)
                    resellItem = false;

                // don't resell stackables?
                if (item.MaxStackSize != null || item.MaxStructure != null)
                    resellItem = false;

                if (resellItem)
                {
                    item.ContainerId = Guid.Full;

                    if (!UniqueItemsForSale.TryAdd(item.Guid, item))
                    {
                        var sellItems = string.Join(", ", items.Values.Select(i => $"{i.Name} ({i.Guid})"));
                        log.Error($"[VENDOR] {Name}.ProcessItemsForPurchase({player.Name}): duplicate item found, sell list: {sellItems}");
                    }

                    item.SoldTimestamp = Time.GetUnixTime();

                    // verify no gap: even though the guid is technically free in the database at this point,
                    // is it still marked as consumed in guid manager, and not marked as freed here?
                    // if player repurchases item sometime later, we must ensure the guid is still marked as consumed for re-add

                    // remove object from shard db, but keep a reference to it in memory
                    // for DestroyOnSell items, these will effectively be destroyed immediately
                    // for other items, if a player re-purchases, it will be added to the shard db again
                    item.RemoveBiotaFromDatabase();
                }
                else
                    item.Destroy();

                NumItemsBought++;
            }

            ApproachVendor(player, VendorType.Sell);
        }

        public void ApplyService(WorldObject item, Player target)
        {
            // verify -- players purchasing multiple services in 1 transaction, and IsBusy state?
            var spell = new Spell(item.SpellDID ?? 0);

            if (spell.NotFound)
                return;

            IsBusy = true;

            var preCastTime = PreCastMotion(target);

            var castChain = new ActionChain();
            castChain.AddDelaySeconds(preCastTime);
            castChain.AddAction(this, () =>
            {
                TryCastSpell(spell, target, this);
                PostCastMotion();
            });

            var postCastTime = GetPostCastTime(spell);

            castChain.AddDelaySeconds(postCastTime);
            castChain.AddAction(this, () => IsBusy = false);

            castChain.EnqueueChain();

            NumServicesSold++;
        }

        /// <summary>
        /// Unique items in the vendor's inventory sold to the vendor by players
        /// expire after vendor_unique_rot_time seconds
        /// </summary>
        protected virtual void RotUniques()
        {
            List<WorldObject> itemsToRemove = null;

            foreach (var uniqueItem in UniqueItemsForSale.Values)
            {
                var soldTime = uniqueItem.SoldTimestamp;

                if (soldTime == null)
                {
                    log.Warn($"[VENDOR] Vendor {Name} has unique item {uniqueItem.Name} ({uniqueItem.Guid}) without a SoldTimestamp -- this shouldn't happen");
                    continue;   // keep in list?
                }

                var rotTime = Time.GetDateTimeFromTimestamp(soldTime.Value);

                rotTime = rotTime.AddSeconds(PropertyManager.GetDouble("vendor_unique_rot_time", 300).Item);

                if (DateTime.UtcNow >= rotTime)
                {
                    if (itemsToRemove == null)
                        itemsToRemove = new List<WorldObject>();

                    itemsToRemove.Add(uniqueItem);
                }
            }
            if (itemsToRemove != null)
            {
                foreach (var itemToRemove in itemsToRemove)
                {
                    log.DebugFormat("[VENDOR] Vendor {0} has discontinued sale of {1} and removed it from its UniqueItemsForSale list.", Name, itemToRemove.Name);
                    UniqueItemsForSale.Remove(itemToRemove.Guid);

                    itemToRemove.Destroy();     // even though it has already been removed from the db at this point, we want to mark as freed in guid manager now
                }
            }
        }

        private static void CleanupCreatedItems(List<WorldObject> createdItems)
        {
            foreach (var createdItem in createdItems)
                createdItem.Destroy();
        }

        public bool OpenForBusiness
        {
            get => GetProperty(PropertyBool.OpenForBusiness) ?? true;
            set { if (value) RemoveProperty(PropertyBool.OpenForBusiness); else SetProperty(PropertyBool.OpenForBusiness, value); }
        }

        public int? MerchandiseItemTypes
        {
            get => GetProperty(PropertyInt.MerchandiseItemTypes);
            set { if (!value.HasValue) RemoveProperty(PropertyInt.MerchandiseItemTypes); else SetProperty(PropertyInt.MerchandiseItemTypes, value.Value); }
        }

        public int? MerchandiseMinValue
        {
            get => GetProperty(PropertyInt.MerchandiseMinValue);
            set { if (!value.HasValue) RemoveProperty(PropertyInt.MerchandiseMinValue); else SetProperty(PropertyInt.MerchandiseMinValue, value.Value); }
        }

        public int? MerchandiseMaxValue
        {
            get => GetProperty(PropertyInt.MerchandiseMaxValue);
            set { if (!value.HasValue) RemoveProperty(PropertyInt.MerchandiseMaxValue); else SetProperty(PropertyInt.MerchandiseMaxValue, value.Value); }
        }

        public double? BuyPrice
        {
            get => GetProperty(PropertyFloat.BuyPrice);
            set { if (!value.HasValue) RemoveProperty(PropertyFloat.BuyPrice); else SetProperty(PropertyFloat.BuyPrice, value.Value); }
        }

        public double? SellPrice
        {
            get => GetProperty(PropertyFloat.SellPrice);
            set { if (!value.HasValue) RemoveProperty(PropertyFloat.SellPrice); else SetProperty(PropertyFloat.SellPrice, value.Value); }
        }

        public bool? DealMagicalItems
        {
            get => GetProperty(PropertyBool.DealMagicalItems);
            set { if (!value.HasValue) RemoveProperty(PropertyBool.DealMagicalItems); else SetProperty(PropertyBool.DealMagicalItems, value.Value); }
        }

        public bool? VendorService
        {
            get => GetProperty(PropertyBool.VendorService);
            set { if (!value.HasValue) RemoveProperty(PropertyBool.VendorService); else SetProperty(PropertyBool.VendorService, value.Value); }
        }

        public int? VendorHappyMean
        {
            get => GetProperty(PropertyInt.VendorHappyMean);
            set { if (!value.HasValue) RemoveProperty(PropertyInt.VendorHappyMean); else SetProperty(PropertyInt.VendorHappyMean, value.Value); }
        }

        public int? VendorHappyVariance
        {
            get => GetProperty(PropertyInt.VendorHappyVariance);
            set { if (!value.HasValue) RemoveProperty(PropertyInt.VendorHappyVariance); else SetProperty(PropertyInt.VendorHappyVariance, value.Value); }
        }

        public int? VendorHappyMaxItems
        {
            get => GetProperty(PropertyInt.VendorHappyMaxItems);
            set { if (!value.HasValue) RemoveProperty(PropertyInt.VendorHappyMaxItems); else SetProperty(PropertyInt.VendorHappyMaxItems, value.Value); }
        }

        public int NumItemsSold
        {
            get => GetProperty(PropertyInt.NumItemsSold) ?? 0;
            set { if (value == 0) RemoveProperty(PropertyInt.NumItemsSold); else SetProperty(PropertyInt.NumItemsSold, value); }
        }

        public int NumItemsBought
        {
            get => GetProperty(PropertyInt.NumItemsBought) ?? 0;
            set { if (value == 0) RemoveProperty(PropertyInt.NumItemsBought); else SetProperty(PropertyInt.NumItemsBought, value); }
        }

        public int NumServicesSold
        {
            get => GetProperty(PropertyInt.NumServicesSold) ?? 0;
            set { if (value == 0) RemoveProperty(PropertyInt.NumServicesSold); else SetProperty(PropertyInt.NumServicesSold, value); }
        }

        public int MoneyIncome
        {
            get => GetProperty(PropertyInt.MoneyIncome) ?? 0;
            set { if (value == 0) RemoveProperty(PropertyInt.MoneyIncome); else SetProperty(PropertyInt.MoneyIncome, value); }
        }

        public int MoneyOutflow
        {
            get => GetProperty(PropertyInt.MoneyOutflow) ?? 0;
            set { if (value == 0) RemoveProperty(PropertyInt.MoneyOutflow); else SetProperty(PropertyInt.MoneyOutflow, value); }
        }
    }
}
