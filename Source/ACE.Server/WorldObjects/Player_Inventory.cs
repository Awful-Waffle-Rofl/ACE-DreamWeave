using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;

using ACE.Database;
using ACE.DatLoader;
using ACE.DatLoader.FileTypes;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Managers.Analytics;
using ACE.Server.Network;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages.Messages;

namespace ACE.Server.WorldObjects
{
    partial class Player
    {
        /// <summary>
        /// Returns all inventory, side slot items, items in side containers, and all wielded items.
        /// <para />
        /// Deduped by guid: an object is returned at most once even if corrupt shard data puts it in more
        /// than one source collection at the same time (a biota carrying both a Container and a Wielder
        /// instance-id lands in Inventory and EquippedObjects both). Callers rely on that guarantee - both
        /// <see cref="AuditItemSpells"/> and Player_Commerce.VerifySellItems build a guid-keyed
        /// <c>ToDictionary</c> straight off this list, which throws on a duplicate key. AuditItemSpells runs
        /// during login on the world thread, where that throw killed the whole server process.
        /// </summary>
        public List<WorldObject> GetAllPossessions()
        {
            return GetAllPossessions(Inventory.Values, EquippedObjects.Values, LogDuplicatePossession);
        }

        /// <summary>
        /// Flattening + guid dedup half of <see cref="GetAllPossessions()"/>, split out from the player's
        /// live collections so it is testable without a Player. <paramref name="onDuplicate"/> is invoked
        /// once per dropped duplicate occurrence.
        /// </summary>
        internal static List<WorldObject> GetAllPossessions(ICollection<WorldObject> inventory, ICollection<WorldObject> equippedObjects, Action<WorldObject> onDuplicate = null)
        {
            var results = new List<WorldObject>(inventory.Count + equippedObjects.Count);
            var seen = new HashSet<ObjectGuid>();

            void tryAdd(WorldObject item)
            {
                if (seen.Add(item.Guid))
                    results.Add(item);
                else
                    onDuplicate?.Invoke(item);
            }

            foreach (var item in inventory)
                tryAdd(item);

            foreach (var item in inventory)
            {
                if (item is Container container)
                {
                    foreach (var subItem in container.Inventory.Values)
                        tryAdd(subItem);
                }
            }

            foreach (var item in equippedObjects)
                tryAdd(item);

            return results;
        }

        /// <summary>
        /// A duplicate here is always corrupt persisted state, never a legitimate runtime condition, so it
        /// is surfaced with everything an operator needs to locate and repair the row.
        /// </summary>
        private void LogDuplicatePossession(WorldObject item)
        {
            log.Warn($"{Name} (0x{Guid.Full:X8}).GetAllPossessions(): possession 0x{item.Guid.Full:X8} ({item.Name}, wcid {item.WeenieClassId}) appears in more than one of Inventory / side container / EquippedObjects; dropping the duplicate. This is corrupt shard data: inspect ace_shard.biota_properties_i_i_d for object_Id {item.Guid.Full}, where type 2 (Container) and type 3 (Wielder) must not both be set.");
        }

        /// <summary>
        /// The SINGLE authority for a Player's carrying capacity. Every player-facing capacity site routes
        /// through here - the three pickup-gate overloads and GetAvailableBurden below, AugmentationDevice's
        /// BurdenLimit message, Player.HandleActionJump's stamina cost, Player.GetBurdenMod (which drives the
        /// run / jump / defense degradation via Creature_Combat.GetBurdenMod), Creature.GetRunRate's player
        /// branch, and the physics layer's WeenieObject.InqBurden.
        ///
        /// Routing them all here is load-bearing for the mule override below: if only the pickup gate knew
        /// about it, a loaded mule would be waved through the gate and then crushed by the burden movement
        /// penalty, which reads a capacity the gate never used.
        ///
        /// EncumbranceSystem.EncumbranceCapacity is left in place but now has NO callers - every one of its
        /// former call sites was a player path and all of them moved here. It clamps the augmentation bonus at
        /// 150 where this does not, but AugmentationType.BurdenLimit maxes at rank 5 (AugmentationDevice.cs
        /// AugmentationMaxRank) and 30 * 5 == 150 is not greater than 150, so the two agreed for every rank a
        /// player can legitimately hold; they diverged only above rank 5, which takes a direct property write.
        /// Collapsing onto this implementation therefore changes nothing for any legitimately augmented player.
        /// </summary>
        public int GetEncumbranceCapacity()
        {
            // This is the single authority for a Player's capacity; every player-facing site routes through
            // it, so the pickup gate and the burden movement penalty cannot disagree.
            //
            // A MULE HAS NO OVERRIDE HERE, deliberately, and one was tried and removed. Overriding this
            // number is invisible to the client: verified in game 2026-08-02, the client derives the burden
            // bar from Strength itself and ignores a server-sent PropertyInt.EncumbranceCapacity, so a mule
            // carrying almost nothing still displayed a heavily loaded bar while the server correctly allowed
            // the load. Capacity must therefore be bought with the Strength attribute (Player_Mule sets it
            // from 'mule_strength') so client and server derive the same number from the same input.
            var strength = Attributes[PropertyAttribute.Strength].Current;

            return (int)((150 * strength) + (AugmentationIncreasedCarryingCapacity * 30 * strength));
        }

        public bool HasEnoughBurdenToAddToInventory(WorldObject worldObject)
        {
            // We can still pick up null items, for some reason, we have the conditional for that
            return (EncumbranceVal + (worldObject.EncumbranceVal ?? 0) <= (GetEncumbranceCapacity() * 3));
        }

        public bool HasEnoughBurdenToAddToInventory(int totalEncumbranceToCheck)
        {
            return (EncumbranceVal + totalEncumbranceToCheck <= (GetEncumbranceCapacity() * 3));
        }

        public int GetAvailableBurden()
        {
            return (GetEncumbranceCapacity() * 3) - EncumbranceVal ?? 0;
        }

        public bool HasEnoughBurdenToAddToInventory(List<WorldObject> worldObjects)
        {
            var burdenTotal = 0;

            foreach (var worldObject in worldObjects)
                burdenTotal += worldObject.EncumbranceVal ?? 0;

            return (EncumbranceVal + burdenTotal <= (GetEncumbranceCapacity() * 3));
        }

        /// <summary>
        /// If enough burden is available, this will try to add (via create) an item to the main pack. If the main pack is full, it will try to add it to the first side pack with room.
        /// </summary>
        public bool TryCreateInInventoryWithNetworking(WorldObject item)
        {
            return TryCreateInInventoryWithNetworking(item, out _);
        }

        /// <summary>
        /// If enough burden is available, this will try to add (via create) an item to the main pack. If the main pack is full, it will try to add it to the first side pack with room.
        /// </summary>
        public bool TryCreateInInventoryWithNetworking(WorldObject item, out Container container)
        {
            if (!TryAddToInventory(item, out container)) // We don't have enough burden available or no empty pack slot.
                return false;

            Session.Network.EnqueueSend(new GameMessageCreateObject(item));

            // Deliberately NO VisualEffectManager.SendTo here. An item in a pack is known to the client
            // but not drawn, so a script sent now has nothing to attach to - and because SendTo is
            // once-per-lifetime, that wasted send would then suppress the real one at equip time.
            // Effects are sent where the object becomes VISIBLE: equip, login-with-it-equipped, or
            // entering view on the ground / on another player.

            if (item is Container itemAsContainer)
            {
                Session.Network.EnqueueSend(new GameEventViewContents(Session, itemAsContainer));

                foreach (var obj in itemAsContainer.Inventory.Values)
                    Session.Network.EnqueueSend(new GameMessageCreateObject(obj, Adminvision, false));
            }

            Session.Network.EnqueueSend(
                new GameEventItemServerSaysContainId(Session, item, container),
                new GameMessagePrivateUpdatePropertyInt(this, PropertyInt.EncumbranceVal, EncumbranceVal ?? 0));

            if (item.WeenieType == WeenieType.Coin || item.WeenieType == WeenieType.Container)
                UpdateCoinValue();

            item.SaveBiotaToDatabase();

            return true;
        }

        public bool TryConsumeFromInventoryWithNetworking(WorldObject item, int amount = int.MaxValue)
        {
            if (amount >= (item.StackSize ?? 1))
            {
                if (!TryRemoveFromInventory(item.Guid, out item))
                    return false;

                Session.Network.EnqueueSend(new GameMessageInventoryRemoveObject(item));

                item.Destroy();
            }
            else
            {
                var stack = FindObject(item.Guid, SearchLocations.MyInventory | SearchLocations.MyEquippedItems, out var stackFoundInContainer, out var stackRootOwner, out _);

                if (stack == null || stackRootOwner == null)
                    return false;

                if (!AdjustStack(stack, -amount, stackFoundInContainer, stackRootOwner))
                    return false;

                Session.Network.EnqueueSend(new GameMessageSetStackSize(stack));
            }

            Session.Network.EnqueueSend(new GameMessagePrivateUpdatePropertyInt(this, PropertyInt.EncumbranceVal, EncumbranceVal ?? 0));

            if (item.WeenieType == WeenieType.Coin)
                UpdateCoinValue();

            return true;
        }

        public bool TryConsumeFromInventoryWithNetworking(uint wcid, int amount = int.MaxValue)
        {
            var items = GetInventoryItemsOfWCID(wcid);
            //items = items.OrderBy(o => o.Value).ToList();

            var leftReq = amount;
            foreach (var item in items)
            {
                var removeNum = Math.Min(leftReq, item.StackSize ?? 1);
                if (!TryConsumeFromInventoryWithNetworking(item, removeNum))
                    return false;

                leftReq -= removeNum;
                if (leftReq <= 0)
                    break;
            }
            return true;
        }

        /// <summary>
        /// Creates the collection a bulk caller hands to the deferred-save overloads of
        /// TryRemoveFromInventoryWithNetworking / TryDequipObjectWithNetworking, so it can issue exactly one
        /// SaveBiotasInParallel for the whole batch instead of one per item. Exposed as a factory so call sites
        /// do not have to name the tuple type (and pull in the usings it needs) just to declare a local.
        /// </summary>
        public static Collection<(Biota biota, ReaderWriterLockSlim rwLock)> NewDeferredSaveList()
        {
            return new Collection<(Biota biota, ReaderWriterLockSlim rwLock)>();
        }

        /// <summary>
        /// Enqueues one shard save for everything a bulk caller collected.
        /// <para />
        /// MUST be called from inside the same synchronous handler invocation that did the removals - batching
        /// within one handler is the point, deferring across ticks or behind a timer is not: a crash between the
        /// removal and the save would leave a sold/dropped item's row still pointing at the previous owner.
        /// <para />
        /// It also MUST be called before any code that deletes those same biotas (a vendor sale's
        /// RemoveBiotaFromDatabase / Destroy). SerializedShardDatabase is one FIFO queue drained by one thread,
        /// so enqueue order is execution order: save-then-remove deletes the row, remove-then-save resurrects it.
        /// <para />
        /// An empty collection is a no-op, so an unchanged item never burns a queue slot.
        /// </summary>
        public static void FlushDeferredSaves(ICollection<(Biota biota, ReaderWriterLockSlim rwLock)> biotas)
        {
            if (biotas == null || biotas.Count == 0)
                return;

            DatabaseManager.Shard.SaveBiotasInParallel(biotas, null);
        }

        /// <summary>
        /// Collects, without enqueueing, exactly the biotas DeepSave would have saved: the item itself if it has
        /// pending changes, plus - for a container - every sub-item with pending changes.
        /// </summary>
        private void CollectDeepSave(WorldObject item, ICollection<(Biota biota, ReaderWriterLockSlim rwLock)> biotas)
        {
            if (item.ChangesDetected)
            {
                item.SaveBiotaToDatabase(false);
                biotas.Add((item.Biota, item.BiotaDatabaseLock));
            }

            // if the player is dropping a container to the landblock,
            // we must ensure any items within the container also have the correct properties
            if (item is Container container)
            {
                foreach (var subItem in container.Inventory.Values)
                {
                    if (subItem.ChangesDetected)
                    {
                        subItem.SaveBiotaToDatabase(false);
                        biotas.Add((subItem.Biota, subItem.BiotaDatabaseLock));
                    }
                }
            }
        }

        /// <summary>
        /// Persists an item that has just gone off-player.
        /// <para />
        /// When deferredSaves is non-null the biotas are only collected into it, and the caller is responsible for
        /// calling FlushDeferredSaves once after its loop. That turns a bulk action over N items from N
        /// un-mergeable queue entries (each its own context and transaction, each head-of-line blocking every
        /// other player's shard work) into one.
        /// </summary>
        private void DeepSave(WorldObject item, ICollection<(Biota biota, ReaderWriterLockSlim rwLock)> deferredSaves = null)
        {
            if (deferredSaves != null)
            {
                CollectDeepSave(item, deferredSaves);
                return;
            }

            var biotas = NewDeferredSaveList();

            CollectDeepSave(item, biotas);

            FlushDeferredSaves(biotas);
        }

        public enum RemoveFromInventoryAction
        {
            None,

            ToWieldedSlot,

            DropItem,
            GiveItem,
            TradeItem,
            SellItem,

            ToCorpseOnDeath,

            ConsumeItem,
            SpendItem
        }

        public bool TryRemoveFromInventoryWithNetworking(uint objectGuid, out WorldObject item, RemoveFromInventoryAction removeFromInventoryAction, ICollection<(Biota biota, ReaderWriterLockSlim rwLock)> deferredSaves = null)
        {
            return TryRemoveFromInventoryWithNetworking(new ObjectGuid(objectGuid), out item, removeFromInventoryAction, deferredSaves); // todo fix
        }

        /// <summary>
        /// When deferredSaves is non-null, the off-player save for this item is collected into it instead of being
        /// enqueued immediately; the caller must call Player.FlushDeferredSaves once, in the same handler, before
        /// anything deletes these biotas. See DeepSave.
        /// </summary>
        public bool TryRemoveFromInventoryWithNetworking(ObjectGuid objectGuid, out WorldObject item, RemoveFromInventoryAction removeFromInventoryAction, ICollection<(Biota biota, ReaderWriterLockSlim rwLock)> deferredSaves = null)
        {
            if (!TryRemoveFromInventory(objectGuid, out item))
                return false;

            if (removeFromInventoryAction != RemoveFromInventoryAction.SellItem && removeFromInventoryAction != RemoveFromInventoryAction.GiveItem)
                Session.Network.EnqueueSend(new GameMessagePublicUpdateInstanceID(item, PropertyInstanceId.Container, ObjectGuid.Invalid));

            if (removeFromInventoryAction == RemoveFromInventoryAction.GiveItem || removeFromInventoryAction == RemoveFromInventoryAction.SpendItem || removeFromInventoryAction == RemoveFromInventoryAction.ToCorpseOnDeath)
                Session.Network.EnqueueSend(new GameMessageInventoryRemoveObject(item));

            if (removeFromInventoryAction != RemoveFromInventoryAction.ToWieldedSlot)
            {
                // The item has gone off-player, so we must do some additional work

                Session.Network.EnqueueSend(new GameMessagePrivateUpdatePropertyInt(this, PropertyInt.EncumbranceVal, EncumbranceVal ?? 0));

                if (item.WeenieType == WeenieType.Coin || item.WeenieType == WeenieType.Container)
                    UpdateCoinValue();

                // We must update the database with the latest ContainerId and WielderId properties.
                // If we don't, the player can drop the item, log out, and log back in. If the landblock hasn't queued a database save in that time,
                // the player will end up loading with this object in their inventory even though the landblock is the true owner. This is because
                // when we load player inventory, the database still has the record that shows this player as the ContainerId for the item.
                DeepSave(item, deferredSaves);
            }

            if (removeFromInventoryAction == RemoveFromInventoryAction.ConsumeItem || removeFromInventoryAction == RemoveFromInventoryAction.TradeItem)
            {
                Session.Network.EnqueueSend(new GameMessageDeleteObject(item));
            }

            return true;
        }

        public void TryShuffleStance(EquipMask wieldedLocation)
        {
            //Console.WriteLine($"{Name}.TryStanceShuffle({wieldedLocation})");

            // do the appropriate combat stance shuffling, based on the item types
            // todo: instead of switching the weapon immediately, the weapon should be swapped in the middle of the animation chain

            // todo: find better / more appropriate logic for this
            // should this be based on something else, such as CombatUse?
            if ((wieldedLocation & EquipMask.Selectable) == 0) return;

            if (CombatMode != CombatMode.NonCombat && CombatMode != CombatMode.Undef)
            {
                switch (wieldedLocation)
                {
                    case EquipMask.MissileWeapon:

                        HandleActionChangeCombatMode(CombatMode.Missile);
                        break;

                    case EquipMask.Held:
                        HandleActionChangeCombatMode(CombatMode.Magic);
                        break;

                    case EquipMask.Shield:

                        var weapon = GetEquippedWeapon(true);

                        if (weapon != null && weapon.DefaultCombatStyle == CombatStyle.ThrownWeapon)
                            HandleActionChangeCombatMode(CombatMode.Missile);
                        else
                            HandleActionChangeCombatMode(CombatMode.Melee);
                        break;

                    default:
                        HandleActionChangeCombatMode(CombatMode.Melee);
                        break;
                }
            }
        }

        /// <summary>
        /// This will set the CurrentWieldedLocation property to wieldedLocation and the Wielder property to this guid and will add it to the EquippedObjects dictionary.<para />
        /// It will also increase the EncumbranceVal and Value.
        /// </summary>
        public bool TryEquipObjectWithNetworking(WorldObject item, EquipMask wieldedLocation)
        {
            if (!TryEquipObjectWithBroadcasting(item, wieldedLocation))
                return false;

            Session.Network.EnqueueSend(
                new GameMessagePublicUpdateInstanceID(item, PropertyInstanceId.Wielder, ObjectGuid.Invalid),
                new GameMessagePublicUpdatePropertyInt(item, PropertyInt.CurrentWieldedLocation, 0),
                new GameEventWieldItem(Session, item.Guid.Full, wieldedLocation),
                new GameMessageSound(Guid, Sound.WieldObject));

            if (item.GearMaxHealth != null)
                HandleMaxHealthUpdate();

            // Equipping is where an item first becomes DRAWN for its owner - in a pack it is known to
            // the client but not rendered, and equipping only reparents it rather than re-creating it.
            // This is the single choke point for every equip, which the earlier hook was not: that one
            // sat inside the `wasEquipped` branch of DoHandleActionGetAndWieldItem, which only covers
            // moving an already-equipped item between slots, so equipping from the pack skipped it.
            VisualEffectManager.SendTo(Session, item);

            TryShuffleStance(wieldedLocation);

            // handle item spells
            if (item.ItemCurMana > 0)
                TryActivateSpells(item);

            // handle equipment sets
            if (item.HasItemSet)
                EquipItemFromSet(item);

            return true;
        }

        private bool TryActivateSpells(WorldObject item)
        {
            // check activation requirements
            var result = item.CheckUseRequirements(this);

            if (!result.Success)
            {
                if (result.Message != null)
                    Session.Network.EnqueueSend(result.Message);

                return false;
            }

            // handle special case
            if (item.ItemCurMana == 1)
            {
                item.ItemCurMana = 0;
                return false;
            }

            var isAffecting = true;     // ??

            foreach (var spell in item.Biota.GetKnownSpellsIds(BiotaDatabaseLock))
            {
                if (item.HasProcSpell((uint)spell))
                    continue;

                if (spell == item.SpellDID)
                    continue;

                var success = CreateItemSpell(item, (uint)spell);

                if (success)
                    isAffecting = true;
            }

            if (isAffecting)
            {
                item.OnSpellsActivated();
                item.ItemCurMana--;
            }

            return true;
        }

        public enum DequipObjectAction
        {
            None,

            DequipToPack,
            DequipToOffPlayerContainer,

            DropItem,
            GiveItem,
            TradeItem,
            SellItem,

            ToCorpseOnDeath,

            ConsumeItem
        }

        public bool TryDequipObjectWithNetworking(uint objectGuid, out WorldObject item, DequipObjectAction dequipObjectAction, ICollection<(Biota biota, ReaderWriterLockSlim rwLock)> deferredSaves = null)
        {
            return TryDequipObjectWithNetworking(new ObjectGuid(objectGuid), out item, dequipObjectAction, deferredSaves); // todo fix this
        }

        /// <summary>
        /// This will remove the Wielder and CurrentWieldedLocation properties on the item and will remove it from the EquippedObjects dictionary.<para />
        /// It does not add it to inventory as you could be unwielding to the ground or a chest.<para />
        /// It will also decrease the EncumbranceVal and Value.<para />
        /// When deferredSaves is non-null, the off-player save for this item is collected into it instead of being
        /// enqueued immediately; the caller must call Player.FlushDeferredSaves once, in the same handler, before
        /// anything deletes these biotas. See DeepSave.
        /// </summary>
        public bool TryDequipObjectWithNetworking(ObjectGuid objectGuid, out WorldObject item, DequipObjectAction dequipObjectAction, ICollection<(Biota biota, ReaderWriterLockSlim rwLock)> deferredSaves = null)
        {
            if (!TryDequipObjectWithBroadcasting(objectGuid, out item, out var wieldedLocation, (dequipObjectAction == DequipObjectAction.DropItem)))
                return false;

            Session.Network.EnqueueSend(
                new GameMessagePublicUpdateInstanceID(item, PropertyInstanceId.Wielder, ObjectGuid.Invalid),
                new GameMessagePublicUpdatePropertyInt(item, PropertyInt.CurrentWieldedLocation, 0),
                new GameMessagePickupEvent(item),
                new GameMessageSound(Guid, Sound.UnwieldObject));

            // handle equipment sets
            if (item.HasItemSet)
                DequipItemFromSet(item);

            if (item.GearMaxHealth != null)
                HandleMaxHealthUpdate();

            if (dequipObjectAction == DequipObjectAction.ToCorpseOnDeath || dequipObjectAction == DequipObjectAction.TradeItem)
                Session.Network.EnqueueSend(new GameMessageDeleteObject(item));

            if (dequipObjectAction == DequipObjectAction.ConsumeItem)
            {
                Session.Network.EnqueueSend(new GameMessageDeleteObject(item));
                item.Destroy();
            }

            if (dequipObjectAction != DequipObjectAction.DequipToPack)
            {
                // The item has gone off-player, so we must do some additional work

                Session.Network.EnqueueSend(new GameMessagePrivateUpdatePropertyInt(this, PropertyInt.EncumbranceVal, EncumbranceVal ?? 0));

                // We must update the database with the latest ContainerId and WielderId properties.
                // If we don't, the player can drop the item, log out, and log back in. If the landblock hasn't queued a database save in that time,
                // the player will end up loading with this object in their inventory even though the landblock is the true owner. This is because
                // when we load player inventory, the database still has the record that shows this player as the ContainerId for the item.
                DeepSave(item, deferredSaves);
            }

            if (dequipObjectAction != DequipObjectAction.ToCorpseOnDeath)
            {
                if (RequiresStanceSwap(wieldedLocation))
                {
                    var newCombatMode = CombatMode.Melee;

                    if (CombatMode == CombatMode.Missile && wieldedLocation == EquipMask.MissileAmmo)
                        newCombatMode = CombatMode.NonCombat;

                    var weapon = GetEquippedWeapon(true);

                    if (weapon != null && weapon.DefaultCombatStyle == CombatStyle.ThrownWeapon)
                        newCombatMode = CombatMode.Missile;

                    HandleActionChangeCombatMode(newCombatMode);
                }
            }

            return true;
        }

        /// <summary>
        /// Returns TRUE if unwielding an item from wieldedLocation
        /// should cause the player to switch to a new stance
        /// </summary>
        public bool RequiresStanceSwap(EquipMask wieldedLocation, bool checkMissile = true)
        {
            if (CombatMode == CombatMode.NonCombat)
                return false;

            switch (wieldedLocation)
            {
                case EquipMask.MeleeWeapon:
                case EquipMask.MissileWeapon:
                case EquipMask.Held:
                case EquipMask.Shield:
                case EquipMask.TwoHanded:
                    return true;
            }

            if (checkMissile && CombatMode == CombatMode.Missile && wieldedLocation == EquipMask.MissileAmmo)
                return true;

            return false;
        }


        // =====================================
        // Helper Functions - Inventory Movement
        // =====================================

        [Flags]
        public enum SearchLocations
        {
            None                = 0x00,
            MyInventory         = 0x01,
            MyEquippedItems     = 0x02,
            Landblock           = 0x04,
            LastUsedContainer   = 0x08,
            WieldedByOther      = 0x10,
            TradedByOther       = 0x20,
            ObjectsKnownByMe    = 0x40,
            LastUsedHook        = 0x80,
            LocationsICanMove   = MyInventory | MyEquippedItems | Landblock | LastUsedContainer,
            Everywhere          = 0xFF
        }

        public WorldObject FindObject(uint objectGuid, SearchLocations searchLocations)
        {
            return FindObject(new ObjectGuid(objectGuid), searchLocations, out _, out _, out _);
        }

        public WorldObject FindObject(uint objectGuid, SearchLocations searchLocations, out Container foundInContainer, out Container rootOwner, out bool wasEquipped)
        {
            return FindObject(new ObjectGuid(objectGuid), searchLocations, out foundInContainer, out rootOwner, out wasEquipped); // todo Fix this so it's not creating a new ObjectGuid
        }

        public WorldObject FindObject(ObjectGuid objectGuid, SearchLocations searchLocations, out Container foundInContainer, out Container rootOwner, out bool wasEquipped)
        {
            WorldObject result;

            foundInContainer = null;
            rootOwner = null;
            wasEquipped = false;

            if (objectGuid == Guid) // Object we're looking for is us
            {
                rootOwner = this;
                return this;
            }

            if (searchLocations.HasFlag(SearchLocations.MyInventory))
            {
                result = GetInventoryItem(objectGuid, out foundInContainer);

                if (result != null)
                {
                    rootOwner = this;
                    return result;
                }
            }

            if (searchLocations.HasFlag(SearchLocations.MyEquippedItems))
            {
                result = GetEquippedItem(objectGuid);

                if (result != null)
                {
                    rootOwner = this;
                    wasEquipped = true;
                    return result;
                }
            }

            if (searchLocations.HasFlag(SearchLocations.Landblock))
            {
                result = CurrentLandblock?.GetObject(objectGuid);

                if (result != null)
                    return result;
            }

            if (searchLocations.HasFlag(SearchLocations.LastUsedContainer))
            {
                if (CurrentLandblock?.GetObject(LastOpenedContainerId) is Container lastOpenedContainer)
                {
                    if (lastOpenedContainer is Vendor lastUsedVendor)
                    {
                        if (lastUsedVendor.TryGetItemForSale(objectGuid, out result))
                        {
                            rootOwner = lastUsedVendor;
                            return result;
                        }
                    }
                    if (lastOpenedContainer.IsOpen && lastOpenedContainer.Viewer == Guid.Full)
                    {
                        result = lastOpenedContainer.GetInventoryItem(objectGuid, out foundInContainer);

                        if (result != null)
                        {
                            rootOwner = lastOpenedContainer;
                            return result;
                        }
                    }
                }
            }

            if (searchLocations.HasFlag(SearchLocations.WieldedByOther))
            {
                result = CurrentLandblock?.GetWieldedObject(objectGuid);

                if (result != null)
                {
                    rootOwner = result.Wielder as Container;
                    return result;
                }
            }

            if (searchLocations.HasFlag(SearchLocations.TradedByOther))
            {
                if (IsTrading && TradePartner != ObjectGuid.Invalid)
                {
                    if (CurrentLandblock?.GetObject(TradePartner) is Player currentTradePartner)
                    {
                        if (currentTradePartner.ItemsInTradeWindow.Contains(objectGuid))
                        {
                            result = currentTradePartner.GetEquippedItem(objectGuid) ?? currentTradePartner.GetInventoryItem(objectGuid);

                            if (result != null)
                                return result;
                        }
                    }
                }
            }

            if (searchLocations.HasFlag(SearchLocations.ObjectsKnownByMe))
            {
                result = GetKnownObjects().FirstOrDefault(o => o.Guid == objectGuid);

                if (result != null)
                    return result;
            }

            if (searchLocations.HasFlag(SearchLocations.LastUsedHook))
            {
                if (CurrentLandblock?.GetObject(LasUsedHookId) is Hook lastUsedHook)
                {
                    result = lastUsedHook.GetInventoryItem(objectGuid, out foundInContainer);

                    if (result != null)
                    {
                        rootOwner = lastUsedHook;
                        return result;
                    }
                }
            }

            return null;
        }

        // WaffleACE: GetPickupAnimationSpeed() moved to Player_PickupBoons.cs (composes the tunable with any
        // claimed pick-up speed quest boons)

        /// <summary>
        /// This would be used if you need to pickup something without a MoveTo action.
        /// It will broadcast the pickup motion and add a delay for the animation length
        /// </summary>
        private ActionChain StartPickupChain()
        {
            StopExistingMoveToChains();

            ActionChain pickupChain = new ActionChain();

            // start picking up item animation
            SendUpdatePosition();

            var speed = GetPickupAnimationSpeed();

            var motion = new Motion(CurrentMotionState.Stance, MotionPickup, speed);

            EnqueueBroadcastMotion(motion);

            // Wait for animation to progress
            var motionTable = DatManager.PortalDat.ReadFromDat<MotionTable>(MotionTableId);
            var pickupAnimationLength = motionTable.GetAnimationLength(CurrentMotionState.Stance, MotionPickup, MotionCommand.Ready);
            pickupChain.AddDelaySeconds(pickupAnimationLength / speed);

            return pickupChain;
        }

        private MotionCommand GetPickupMotion(WorldObject objectWereReachingToward)
        {
            if (objectWereReachingToward.Location == null)
                return MotionCommand.Invalid;

            // hack for jump looting --

            // in retail, this bug was a result of actions running on motion callbacks,
            // when the motions exited the animation queue

            // since a 'crouch down' motion cannot be performed while jumping,
            // this crouch down motion exited the animation queue immediately

            // here we are just skipping the animation if the player is jumping
            if (IsJumping && PropertyManager.GetBool("allow_jump_loot").Item)
                return MotionCommand.Invalid;

            MotionCommand pickupMotion;

            var item_location_z = objectWereReachingToward.Location.PositionZ;

            if (!(objectWereReachingToward is Corpse))
                item_location_z += objectWereReachingToward.Height * 0.5f;

            if (item_location_z >= Location.PositionZ + (Height * 0.90))
                pickupMotion = MotionCommand.Pickup20; // Reach up
            else if (item_location_z >= Location.PositionZ + (Height * 0.70))
                pickupMotion = MotionCommand.Pickup15; // Reach over and up just a little bit
            else if (item_location_z >= Location.PositionZ + (Height * 0.50))
                pickupMotion = MotionCommand.Pickup10; // Reach over and down just a little bit
            else if (item_location_z >= Location.PositionZ + (Height * 0.20))
                pickupMotion = MotionCommand.Pickup5; // Bend down a little bit
            else
                pickupMotion = MotionCommand.Pickup; // At foot height or lower

            return pickupMotion;
        }

        /// <summary>
        /// This would be used if your pickup action first requires a MoveTo action
        /// It will add a chain to broadcast the pickup motion and then add a delay for the animation length
        /// </summary>
        private ActionChain AddPickupChainToMoveToChain(MotionCommand pickupMotion)
        {
            if (pickupMotion == MotionCommand.Invalid)
                return new ActionChain();

            // start picking up item animation
            SendUpdatePosition();

            var speed = GetPickupAnimationSpeed();

            var motion = new Motion(CurrentMotionState.Stance, pickupMotion, speed);

            EnqueueBroadcastMotion(motion);

            // Wait for animation to progress
            var motionTable = DatManager.PortalDat.ReadFromDat<MotionTable>(MotionTableId);
            var pickupAnimationLength = motionTable.GetAnimationLength(CurrentMotionState.Stance, pickupMotion, MotionCommand.Ready);

            var pickupChain = new ActionChain();
            pickupChain.AddDelaySeconds(pickupAnimationLength / speed);

            return pickupChain;
        }

        /// <summary>
        /// If you want to subtract from a stack, amount should be negative.
        /// </summary>
        private bool AdjustStack(WorldObject stack, int amount, Container container, Container rootContainer)
        {
            if (stack.StackSize + amount <= 0 || stack.StackSize + amount > stack.MaxStackSize)
            {
                log.WarnFormat("Player 0x{0:X8}:{1} tried to adjust stack by an invalid amount amount ({4}) 0x{2:X8}:{3}.", Guid.Full, Name, stack.Guid.Full, stack.Name, amount);
                return false;
            }

            stack.StackSize += amount;
            stack.EncumbranceVal = (stack.StackUnitEncumbrance ?? 0) * (stack.StackSize ?? 1);
            stack.Value = (stack.StackUnitValue ?? 0) * (stack.StackSize ?? 1);

            if (container != null)
            {
                // We add to these values because amount will be negative if we're subtracting from a stack, so we want to add a negative number.
                container.EncumbranceVal += (stack.StackUnitEncumbrance ?? 0) * amount;
                container.Value += (stack.StackUnitValue ?? 0) * amount;
            }

            if (rootContainer != null && rootContainer != container)
            {
                rootContainer.EncumbranceVal += (stack.StackUnitEncumbrance ?? 0) * amount;
                rootContainer.Value += (stack.StackUnitValue ?? 0) * amount;
            }

            return true;
        }

        public PickupState PickupState { get; set; }
        public Action NextPickup { get; set; }

        public void StartPickup()
        {
            PickupState = PickupState.Start;
            IsBusy = true;
        }

        public void EnqueuePickupDone(MotionCommand pickupMotion)
        {
            PickupState = PickupState.Return;

            var speed = GetPickupAnimationSpeed();

            var returnStance = new Motion(CurrentMotionState.Stance, MotionCommand.Ready, speed);
            EnqueueBroadcastMotion(returnStance);

            var animTime = DatManager.PortalDat.ReadFromDat<MotionTable>(MotionTableId).GetAnimationLength(pickupMotion);

            var actionChain = new ActionChain();
            actionChain.AddDelaySeconds(animTime / speed);
            actionChain.AddAction(this, () =>
            {
                // WaffleACE: undo the sped-up Ready broadcast so the idle cycle doesn't stay fast
                if (speed != 1.0f)
                    EnqueueBroadcastMotion(new Motion(CurrentMotionState.Stance));

                PickupState = PickupState.None;
                IsBusy = false;

                if (NextPickup != null)
                {
                    NextPickup();
                    NextPickup = null;
                }
            });
            actionChain.EnqueueChain();
        }

        private bool HandleActionPutItemInContainer_Verify(uint itemGuid, uint containerGuid, int placement,
            out Container itemRootOwner, out WorldObject item, out Container containerRootOwner, out Container container, out bool itemWasEquipped)
        {
            itemRootOwner = null;
            item = null;
            container = null;
            containerRootOwner = null;
            itemWasEquipped = false;

            if (suicideInProgress)
            {
                Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.YoureTooBusy));
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, itemGuid));
                return false;
            }

            if (IsBusy)
            {
                if (PickupState != PickupState.Return || NextPickup != null)
                {
                    Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.YoureTooBusy));
                    Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, itemGuid));
                }
                else
                    NextPickup = () => { HandleActionPutItemInContainer(itemGuid, containerGuid, placement); };

                return false;
            }

            //OnPutItemInContainer(itemGuid, containerGuid, placement);

            item = FindObject(itemGuid, SearchLocations.LocationsICanMove, out _, out itemRootOwner, out itemWasEquipped);
            container = FindObject(containerGuid, SearchLocations.MyInventory | SearchLocations.Landblock | SearchLocations.LastUsedContainer, out _, out containerRootOwner, out _) as Container;

            if (item == null)
            {
                Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, "Source item not found!")); // Custom error message
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, itemGuid));
                return false;
            }

            if (!item.Guid.IsDynamic() || item is Creature || item.Stuck)
            {
                log.WarnFormat("Player 0x{0:X8}:{1} tried to move item 0x{2:X8}:{3}.", Guid.Full, Name, item.Guid.Full, item.Name);
                Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.Stuck));
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, itemGuid));
                return false;
            }

            if (itemRootOwner != this && containerRootOwner == this && !HasEnoughBurdenToAddToInventory(item))
            {
                Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, "You are too encumbered to carry that!"));
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, itemGuid));
                return false;
            }

            if (container == null)
            {
                Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, "Target container not found!")); // Custom error message
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, itemGuid));
                return false;
            }

            if (container is Corpse)
            {
                Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, $"You cannot put {item.Name} in that.")); // Custom error message
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, itemGuid));
                return false;
            }

            if (IsTrading && item.IsBeingTradedOrContainsItemBeingTraded(ItemsInTradeWindow))
            {
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, itemGuid, WeenieError.TradeItemBeingTraded));
                return false;
            }

            if (containerRootOwner != this) // Is our target on the landscape?
            {
                if (itemRootOwner == this && item.IsAttunedOrContainsAttuned)
                {
                    Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, itemGuid, WeenieError.AttunedItem));
                    return false;
                }

                if (itemRootOwner == this && item is PetDevice petDevice && petDevice.Pet is not null)
                {
                    Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, "You must unsummon your pet before you can transfer this item!"));
                    Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, itemGuid, WeenieError.None));
                    return false;
                }

                if (containerRootOwner != null && !containerRootOwner.IsOpen)
                {
                    Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, itemGuid, WeenieError.TheContainerIsClosed));
                    return false;
                }
            }

            if (containerRootOwner is Corpse corpse)
            {
                if (!corpse.IsMonster)
                {
                    Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, itemGuid, WeenieError.Dead));
                    return false;
                }
            }

            if (container is Hook hook)
            {
                if (PropertyManager.GetBool("house_hook_limit").Item)
                {
                    if (hook.House.HouseMaxHooksUsable != -1 && hook.House.HouseCurrentHooksUsable <= 0)
                    {
                        Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, itemGuid, WeenieError.YouHaveUsedAllTheHooks));
                        return false;
                    }
                }

                if (PropertyManager.GetBool("house_hookgroup_limit").Item)
                {
                    var itemHookGroup = item.HookGroup ?? HookGroupType.Undef;
                    var houseHookGroupMax = hook.House.GetHookGroupMaxCount(itemHookGroup);
                    var houseHookGroupCurrent = hook.House.GetHookGroupCurrentCount(itemHookGroup);
                    if (houseHookGroupMax != -1 && houseHookGroupCurrent >= houseHookGroupMax)
                    {
                        Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, itemGuid));
                        Session.Player.SendWeenieErrorWithString(WeenieErrorWithString.MaxNumberOf_Hooked, itemHookGroup.ToSentence());
                        return false;
                    }
                }
            }

            if (item is Container)
            {
                // Blocking all attempts to put containers in things that aren't Players and Storage. This may not be retail, but at this time appears to be best catch all solution to Quest stamp bypass issue.
                if (container is not Player && container is not Storage)
                {
                    //Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, $"You cannot put {item.Name} in that.")); // Custom error message
                    Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, itemGuid));
                    return false;
                }
            }

            //if (container is Storage storage)
            //{
            //    if (!storage.IsOpen || storage.Viewer != Guid.Full)
            //    {
            //        Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, itemGuid, WeenieError.TheContainerIsClosed));
            //        return false;
            //    }
            //}

            //if (container is Chest chest)
            //{
            //    if (!chest.IsOpen || chest.Viewer != Guid.Full)
            //    {
            //        Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, itemGuid, WeenieError.TheContainerIsClosed));
            //        return false;
            //    }
            //}

            if (containerRootOwner == null) // container is on landscape, so you must have it open
            {
                if (!container.IsOpen || container.Viewer != Guid.Full)
                {
                    Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, itemGuid, WeenieError.TheContainerIsClosed));
                    return false;
                }
            }

            return true;
        }

        // =========================================
        // Game Action Handlers - Inventory Movement 
        // =========================================

        /// <summary>
        /// This method processes the Game Action (F7B1) Put Item In Container (0x0019)
        /// This is raised when we:
        /// - move an item around in our inventory.
        /// - dequip an item.
        /// - Pickup an item off of the landblock or a container on the landblock
        /// - Put an item into a container on the landblock
        /// - Move an item between containers on a landblock
        /// </summary>
        public void HandleActionPutItemInContainer(uint itemGuid, uint containerGuid, int placement = 0)
        {
            //Console.WriteLine($"{Name}.HandleActionPutItemInContainer({itemGuid:X8}, {containerGuid:X8}, {placement})");

            if (!HandleActionPutItemInContainer_Verify(itemGuid, containerGuid, placement,
                out Container itemRootOwner, out WorldObject item, out Container containerRootOwner, out Container container, out bool itemWasEquipped))
            {
                return;
            }

            // Unconditional, matching the split and merge handlers. This direction happens to be closed
            // today by the later container-ownership check, but that check exists for a different
            // reason and could be changed without anyone noticing it was load-bearing here.
            if (itemRootOwner is Vendor || containerRootOwner is Vendor || container is Vendor)
            {
                Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.NotAllTheItemsAreAvailable));
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, itemGuid));
                return;
            }

            if ((itemRootOwner == this && containerRootOwner != this) || (itemRootOwner != this && containerRootOwner == this)) // Movement is between the player and the world
            {
                var itemAsContainer = item as Container;

                // Checking to see if item to pick is an container itself and IsOpen
                if (itemAsContainer != null && !VerifyContainerOpenStatus(itemAsContainer, item))
                    return;

                WorldObject moveToTarget;
                if (itemRootOwner == this)
                    moveToTarget = containerRootOwner ?? container; // Movement is from player
                else
                    moveToTarget = itemRootOwner ?? item; // Movement is too player

                CreateMoveToChain(moveToTarget, (success) =>
                {
                    if (CurrentLandblock == null) // Maybe we were teleported as we were motioning to pick up the item
                    {
                        Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, itemGuid, WeenieError.ActionCancelled));
                        return;
                    }

                    if (!success)
                    {
                        Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, itemGuid, WeenieError.ActionCancelled));
                        return;
                    }

                    // Was this item picked up by someone else?
                    if (itemRootOwner == null && item.CurrentLandblock == null)
                    {
                        Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, itemGuid, WeenieError.ActionCancelled));
                        return;
                    }

                    if (itemRootOwner != this && containerRootOwner == this)
                    {
                        // moving from world container to player
                        if (item.IsUniqueOrContainsUnique && !CheckUniques(item))
                        {
                            Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, itemGuid));
                            return;
                        }
                    }

                    StartPickup();

                    var pickupMotion = GetPickupMotion(moveToTarget);
                    var pickupChain = AddPickupChainToMoveToChain(pickupMotion);

                    pickupChain.AddAction(this, () =>
                    {
                        // Was this item picked up by someone else?
                        if (itemRootOwner == null && item.CurrentLandblock == null)
                        {
                            Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, itemGuid, WeenieError.ActionCancelled));
                            EnqueuePickupDone(pickupMotion);
                            return;
                        }

                        // Checking to see if item to pick is an container itself and IsOpen
                        if (!VerifyContainerOpenStatus(itemAsContainer, item))
                        {
                            EnqueuePickupDone(pickupMotion);
                            return;
                        }

                        if (!VerifyQuest(item, itemRootOwner, out bool questSolve, out bool isFromAPlayerCorpse))
                        {
                            // InventoryServerSaveFailed previously sent in QuestManager
                            EnqueuePickupDone(pickupMotion);
                            return;
                        }

                        if (DoHandleActionPutItemInContainer(item, itemRootOwner, itemWasEquipped, container, containerRootOwner, placement))
                        {
                            Session.Network.EnqueueSend(new GameMessagePrivateUpdatePropertyInt(this, PropertyInt.EncumbranceVal, EncumbranceVal ?? 0));

                            if (item.WeenieType == WeenieType.Coin || item.WeenieType == WeenieType.Container)
                                UpdateCoinValue();

                            if (itemRootOwner == this)
                            {
                                item.EmoteManager.OnDrop(this);
                                EnqueueBroadcast(new GameMessageSound(Guid, Sound.DropItem));
                            }
                            else if (containerRootOwner == this)
                            {
                                if (itemAsContainer != null) // We're picking up a pack
                                {
                                    Session.Network.EnqueueSend(new GameEventViewContents(Session, itemAsContainer));

                                    foreach (var packItem in itemAsContainer.Inventory)
                                        Session.Network.EnqueueSend(new GameMessageCreateObject(packItem.Value));
                                }

                                EnqueueBroadcast(new GameMessageSound(Guid, Sound.PickUpItem));

                                item.EmoteManager.OnPickup(this);
                                item.NotifyOfEvent(RegenerationType.PickUp);

                                // speed challenge (WaffleACE): picking a season's objective item up off the
                                // ground is a third way into the completion funnel, alongside using a goal
                                // object (WorldObject_Use.cs) and killing a boss (Creature_Death.cs). The
                                // instance check runs FIRST, not the item's flag, because it short-circuits on the
                                // overwhelmingly common case: almost every pickup in the game is not a speed run,
                                // so an ordinary pickup pays one property read and an instance compare rather than
                                // two property reads. The funnel re-validates the run and gates on the season's
                                // ObjectiveWcid itself, so this call site does not need to re-check that.
                                if (IsInSpeedChallengeInstance && item.GetProperty(PropertyBool.SpeedChallengeGoal) == true)
                                {
                                    // A stackable objective item merges onto an existing stack through
                                    // DoHandleActionStackableMerge instead of this path, so it would never reach
                                    // OnPickup or the completion funnel, and the run could never be completed with
                                    // no visible cause. MaxStackSize is what declares the item STACKABLE; StackSize
                                    // is only how many are in this particular pile, and reads 1 for the first one
                                    // ever dropped - so checking StackSize here would miss exactly the case that
                                    // breaks later. Content authoring cannot make the item non-stackable from
                                    // here, so log loudly rather than fail silently.
                                    if (item.MaxStackSize.HasValue && item.MaxStackSize.Value > 1)
                                        log.Error($"[SPEED] {item.Name} (0x{item.Guid}, wcid {item.WeenieClassId}) is flagged SpeedChallengeGoal but is STACKABLE (MaxStackSize {item.MaxStackSize.Value}); a merged pickup never reaches OnPickup, so this objective item can never finish a run. The objective item must not be stackable.");

                                    TryFinishSpeedChallenge(item);
                                }

                                if (questSolve)
                                    item.EmoteManager.OnQuest(this);

                                if (isFromAPlayerCorpse)
                                {
                                    log.InfoFormat("[CORPSE] {0} (0x{1}) picked up {2} (0x{3}) from {4} (0x{5})", Name, Guid, item.Name, item.Guid, itemRootOwner.Name, itemRootOwner.Guid);
                                    item.SaveBiotaToDatabase();
                                }
                            }

                            if (PropertyManager.GetBool("house_hook_limit").Item)
                            {
                                if (container is Hook toHook && toHook.House.HouseMaxHooksUsable != -1 && toHook.House.HouseCurrentHooksUsable <= 0)
                                {
                                    SendWeenieError(WeenieError.YouAreNowUsingMaxHooks);
                                }
                                else if (itemRootOwner is Hook fromHook && fromHook.House.HouseMaxHooksUsable != -1 && fromHook.House.HouseCurrentHooksUsable == 1)
                                {
                                    SendWeenieError(WeenieError.YouAreNoLongerUsingMaxHooks);
                                }
                            }

                            if (PropertyManager.GetBool("house_hookgroup_limit").Item)
                            {
                                if (container is Hook toHook)
                                {
                                    var itemHookGroup = item.HookGroup ?? HookGroupType.Undef;
                                    var houseHookGroupMax = toHook.House.GetHookGroupMaxCount(itemHookGroup);
                                    var houseHookGroupCurrent = toHook.House.GetHookGroupCurrentCount(itemHookGroup);

                                    if (houseHookGroupMax != -1 && houseHookGroupCurrent >= houseHookGroupMax)
                                        SendWeenieErrorWithString(WeenieErrorWithString.MaxNumberOf_HookedUntilOneIsRemoved, itemHookGroup.ToSentence());
                                }
                                else if (itemRootOwner is Hook fromHook)
                                {
                                    var itemHookGroup = item.HookGroup ?? HookGroupType.Undef;
                                    var houseHookGroupMax = fromHook.House.GetHookGroupMaxCount(itemHookGroup);
                                    var houseHookGroupCurrent = fromHook.House.GetHookGroupCurrentCount(itemHookGroup);

                                    if (houseHookGroupMax != -1 && houseHookGroupCurrent == houseHookGroupMax - 1)
                                        SendWeenieErrorWithString(WeenieErrorWithString.NoLongerMaxNumberOf_Hooked, itemHookGroup.ToSentence());
                                }
                            }
                        }
                        EnqueuePickupDone(pickupMotion);
                    });

                    pickupChain.EnqueueChain();

                }, null, false);    // if player is within UseRadius of moveToTarget, do not perform rotation
            }
            else // This is a self-contained movement
            {
                var _containerRootOwner = containerRootOwner ?? container;

                if (_containerRootOwner != this && _containerRootOwner != itemRootOwner)
                {
                    // this *should* be a self-contained movement..
                    // duplicated check/message from client
                    Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, itemGuid));
                    SendTransientError($"You must first pick up the {item.Name}");
                    return;
                }

                var wieldedLocation = item.CurrentWieldedLocation ?? EquipMask.None;

                // note that special sequence for swapping arrows while in missile combat
                // is not handled here, but is still handled downstream
                if (RequiresStanceSwap(wieldedLocation, false))
                {
                    HandleActionChangeCombatMode(CombatMode.Melee, true, () =>
                    {
                        if (!HandleActionPutItemInContainer_Verify(itemGuid, containerGuid, placement,
                            out Container itemRootOwner, out WorldObject item, out Container containerRootOwner, out Container container, out bool itemWasEquipped))
                        {
                            return;
                        }
                        DoHandleActionPutItemInContainer(item, itemRootOwner, itemWasEquipped, container, containerRootOwner, placement);
                    });
                }
                else
                {
                    DoHandleActionPutItemInContainer(item, itemRootOwner, itemWasEquipped, container, containerRootOwner, placement);
                }
            }
        }

        private bool VerifyContainerOpenStatus(Container itemAsContainer, WorldObject item)
        {
            // Checking to see if item to pick is an container itself and IsOpen
            if (itemAsContainer != null && itemAsContainer.IsOpen)
            {
                if (itemAsContainer.Viewer == Session.Player.Guid.Full)
                {
                    // We're the one that has it open. Close it before picking it up
                    itemAsContainer.Close(this);
                }
                else
                {
                    // We're not who has it open. Can't pick up something someone else is viewing!
                    Session.Network.EnqueueSend(new GameEventWeenieErrorWithString(Session, WeenieErrorWithString.The_IsCurrentlyInUse, item.Name));
                    Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, item.Guid.Full));
                    return false;
                }
            }
            return true;
        }

        private bool VerifyQuest(WorldObject item, Container itemRootOwner, out bool questSolve, out bool isFromAPlayerCorpse)
        {
            questSolve = false;
            isFromAPlayerCorpse = false;

            if (item.QuestRestriction != null && !QuestManager.HasQuest(item.QuestRestriction))
            {
                QuestManager.HandleNoQuestError(item);
                return false;
            }

            //var questSolve = false;
            //var isFromMyCorpse = false;
            //var isFromAPlayerCorpse = false;
            //var isFromMyHook = false;
            //var isFromMyStorage = false;

            //if (itemRootOwner != this && containerRootOwner == this && item.Quest != null) // We're picking up a quest item
            //{
            //    if ( itemRootOwner != null && (itemRootOwner.WeenieType == WeenieType.Corpse || itemRootOwner.WeenieType == WeenieType.Hook || itemRootOwner.WeenieType == WeenieType.Storage))
            //    {
            //        if (itemRootOwner is Corpse && itemRootOwner.VictimId.HasValue && itemRootOwner.VictimId.Value == Guid.Full)
            //            isFromMyCorpse = true;
            //        if (itemRootOwner is Hook && itemRootOwner.HouseOwner.HasValue && itemRootOwner.HouseOwner.Value == Guid.Full)
            //            isFromMyHook = true;
            //        if (itemRootOwner is Storage && itemRootOwner.HouseOwner.HasValue && itemRootOwner.HouseOwner.Value == Guid.Full)
            //            isFromMyStorage = true;
            //    }

            //    if (!QuestManager.CanSolve(item.Quest) && !isFromMyCorpse && !isFromMyHook && !isFromMyStorage)
            //    {
            //        QuestManager.HandleSolveError(item.Quest);
            //        EnqueuePickupDone(pickupMotion);
            //        return;
            //    }
            //    else
            //    {
            //        if (!isFromMyCorpse && !isFromMyHook && !isFromMyStorage)
            //            questSolve = true;
            //    }
            //}

            var itemFoundOnCorpse = itemRootOwner is Corpse;

            if (itemFoundOnCorpse && itemRootOwner.Level > 0)
                isFromAPlayerCorpse = true;

            if (item.Quest != null) // We're picking up an item with a quest stamp that can also be a timer/limiter
            {
                var itemFoundOnMyCorpse = itemFoundOnCorpse && (itemRootOwner.VictimId == Guid.Full);
                if (item.GeneratorId != null || (itemFoundOnCorpse && !itemFoundOnMyCorpse)) // item is controlled by a generator or is on a corpse that is not my own
                {
                    if (QuestManager.CanSolve(item.Quest))
                    {
                        questSolve = true;
                    }
                    else
                    {
                        QuestManager.HandleSolveError(item.Quest);
                        return false;
                    }
                }
            }
            return true;
        }

        private bool DoHandleActionPutItemInContainer(WorldObject item, Container itemRootOwner, bool itemWasEquipped, Container container, Container containerRootOwner, int placement)
        {
            //Console.WriteLine($"-> DoHandleActionPutItemInContainer({item.Name}, {itemRootOwner?.Name}, {itemWasEquipped}, {container?.Name}, {containerRootOwner?.Name}, {placement})");

            Position prevLocation = null;
            Landblock prevLandblock = null;

            var prevContainer = item.Container;

            OnPutItemInContainer(item.Guid.Full, container.Guid.Full, placement);

            if (item.CurrentLandblock != null) // Movement is an item pickup off the landblock
            {
                prevLocation = new Position(item.Location);
                prevLandblock = item.CurrentLandblock;

                item.CurrentLandblock.RemoveWorldObject(item.Guid, false, true);
                item.Location = null;
            }
            else if (itemWasEquipped) // Movement is an equipped item to a container on the landblock
            {
                var dequipObjectAction = containerRootOwner == this ? DequipObjectAction.DequipToPack : DequipObjectAction.DequipToOffPlayerContainer;

                if (!TryDequipObjectWithNetworking(item.Guid, out _, dequipObjectAction))
                {
                    Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, "TryDequipObjectWithNetworking failed!")); // Custom error message
                    Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, item.Guid.Full));
                    return false;
                }
            }
            else // Movement is within the same pack or between packs in a container on the landblock
            {
                var itemRootCreature = itemRootOwner as Creature;

                if (itemRootOwner != null && !itemRootOwner.TryRemoveFromInventory(item.Guid) && (itemRootCreature == null || !itemRootCreature.TryDequipObject(item.Guid, out _, out _)))
                {
                    Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, "TryRemoveFromInventory failed!")); // Custom error message
                    Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, item.Guid.Full));
                }

                if (itemRootOwner == this && containerRootOwner != this)
                {
                    // We must update the database with the latest ContainerId and WielderId properties.
                    // If we don't, the player can drop the item, log out, and log back in. If the landblock hasn't queued a database save in that time,
                    // the player will end up loading with this object in their inventory even though the landblock is the true owner. This is because
                    // when we load player inventory, the database still has the record that shows this player as the ContainerId for the item.
                    DeepSave(item);
                }
            }

            var burdenCheck = itemRootOwner != this && containerRootOwner == this;

            if (!container.TryAddToInventory(item, placement, true, burdenCheck))
            {
                Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, $"Unable to put {item.Name} into container")); // Custom error message
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, item.Guid.Full));

                if (prevLocation != null)
                {
                    var landblockReturn = new ActionChain();

                    landblockReturn.AddDelaySeconds(1);
                    landblockReturn.AddAction(prevLandblock, () => RemoveTrackedObject(item, false));
                    landblockReturn.AddDelaySeconds(1);
                    landblockReturn.AddAction(prevLandblock, () =>
                    {
                        item.Location = new Position(prevLocation);
                        LandblockManager.AddObject(item);
                    });
                    landblockReturn.EnqueueChain();
                }
                else if (itemRootOwner == null || !itemRootOwner.TryAddToInventory(item))
                {
                    log.Error($"{Name}.DoHandleActionPutItemInContainer({item.Name} ({item.Guid}), {itemRootOwner?.Name} ({itemRootOwner?.Guid}), {itemWasEquipped}, {container.Name} ({container.Guid}), {containerRootOwner?.Name} ({containerRootOwner?.Guid}), {placement}) - removed item from original location, failed to add to new container, failed to re-add to original location");
                }

                return false;
            }

            if (container != containerRootOwner && containerRootOwner != null)
            {
                containerRootOwner.EncumbranceVal += (item.EncumbranceVal ?? 0);
                containerRootOwner.Value += (item.Value ?? 0);
            }

            // when moving from a non-stuck container to a different container,
            // the database must be synced immediately
            if (prevContainer != null && !prevContainer.Stuck && container != prevContainer)
                item.SaveBiotaToDatabase();

            Session.Network.EnqueueSend(
                new GameMessagePublicUpdateInstanceID(item, PropertyInstanceId.Container, container.Guid),
                new GameEventItemServerSaysContainId(Session, item, container));

            return true;
        }

        /// <summary>
        /// This method processes the Game Action (F7B1) Drop Item (0x001B)
        /// This is raised when we:
        /// - drop an equipped item
        /// - drop an item from inventory
        /// </summary>
        public void HandleActionDropItem(uint itemGuid)
        {
            if (IsBusy || Teleporting || suicideInProgress)
            {
                Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.YoureTooBusy));
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, itemGuid));
                return;
            }

            // Proving Grounds (WaffleACE): putting anything on the floor inside the survival arena ends the run -
            // see Player_SurvivalChallenge.TryEndSurvivalChallengeOnDrop for why. The drop is refused rather than
            // completed, so the item is not stranded in the instance the player is being pulled out of.
            if (TryEndSurvivalChallengeOnDrop())
            {
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, itemGuid));
                return;
            }

            var item = FindObject(itemGuid, SearchLocations.MyInventory | SearchLocations.MyEquippedItems, out _, out _, out var wasEquipped);

            if (item == null)
            {
                Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, "Item not found!")); // Custom error message
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, itemGuid));
                return;
            }

            if (item.IsAttunedOrContainsAttuned)
            {
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, itemGuid, WeenieError.AttunedItem));
                return;
            }

            if (item is PetDevice petDevice && petDevice.Pet is not null)
            {
                Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, "You must unsummon your pet before you can drop this item!"));
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, itemGuid, WeenieError.None));
                return;
            }

            if (IsTrading && item.IsBeingTradedOrContainsItemBeingTraded(ItemsInTradeWindow))
            {
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, item.Guid.Full, WeenieError.TradeItemBeingTraded));
                return;
            }

            var speed = GetPickupAnimationSpeed();

            var actionChain = StartPickupChain();

            actionChain.AddAction(this, () =>
            {
                if (CurrentLandblock == null) // Maybe we were teleported as we were motioning to drop the item
                {
                    Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, item.Guid.Full, WeenieError.ActionCancelled));
                    return;
                }

                if (wasEquipped)
                {
                    if (!TryDequipObjectWithNetworking(item.Guid.Full, out _, DequipObjectAction.DropItem))
                    {
                        Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, "Failed to dequip item!")); // Custom error message
                        Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, item.Guid.Full));
                        return;
                    }
                }
                else
                {
                    if (!TryRemoveFromInventoryWithNetworking(item.Guid.Full, out _, RemoveFromInventoryAction.DropItem))
                    {
                        Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, "Failed to remove item from inventory!")); // Custom error message
                        Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, item.Guid.Full));
                        return;
                    }
                }

                if (TryDropItem(item))
                {
                    // drop success
                    Session.Network.EnqueueSend(
                        new GameMessagePublicUpdateInstanceID(item, PropertyInstanceId.Container, ObjectGuid.Invalid),
                        new GameEventItemServerSaysMoveItem(Session, item),
                        new GameMessageUpdatePosition(item));

                    EnqueueBroadcast(new GameMessageSound(Guid, Sound.DropItem));

                    item.EmoteManager.OnDrop(this);
                }
                else
                {
                    // drop failed, re-add to inventory
                    if (TryAddToInventory(item))
                    {
                        Session.Network.EnqueueSend(new GameMessagePrivateUpdatePropertyInt(this, PropertyInt.EncumbranceVal, EncumbranceVal ?? 0));

                        if (item.WeenieType == WeenieType.Coin || item.WeenieType == WeenieType.Container)
                            UpdateCoinValue();

                        Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, item.Guid.Full));
                    }
                    else
                        log.Warn($"0x{item.Guid}:{item.Name} for player {Name} lost from HandleActionDropItem failure.");
                }

                var returnStance = new Motion(CurrentMotionState.Stance, MotionCommand.Ready, speed);
                EnqueueBroadcastMotion(returnStance);
            });

            // WaffleACE: undo the sped-up Ready broadcast so the idle cycle doesn't stay fast
            if (speed != 1.0f)
            {
                var pickupAnimationLength = DatManager.PortalDat.ReadFromDat<MotionTable>(MotionTableId).GetAnimationLength(MotionPickup);
                actionChain.AddDelaySeconds(pickupAnimationLength / speed);
                actionChain.AddAction(this, () => EnqueueBroadcastMotion(new Motion(CurrentMotionState.Stance)));
            }

            actionChain.EnqueueChain();
        }

        private bool TryDropItem(WorldObject item)
        {
            item.Location = new Position(Location);
            item.Placement = ACE.Entity.Enum.Placement.Resting;  // This is needed to make items lay flat on the ground.

            // increased precision for non-ethereal objects
            var ethereal = item.Ethereal;
            item.Ethereal = true;

            if (!CurrentLandblock.AddWorldObject(item))
                return false;

            // use radius?
            var targetPos = Location.InFrontOf(1.1f);
            targetPos.LandblockId = new LandblockId(targetPos.GetCell());

            // try slide to new position
            var transit = item.PhysicsObj.transition(item.PhysicsObj.Position, new Physics.Common.Position(targetPos), false);

            if (transit != null && transit.SpherePath.CurCell != null)
            {
                item.PhysicsObj.SetPositionInternal(transit);

                item.SyncLocation();

                item.SendUpdatePosition(true);
            }
            item.Ethereal = ethereal;

            if (item.Ethereal == null)
            {
                var defaultPhysicsState = (PhysicsState)(item.GetProperty(PropertyInt.PhysicsState) ?? 0);

                if (defaultPhysicsState.HasFlag(PhysicsState.Ethereal))
                    item.Ethereal = true;
                else
                    item.Ethereal = false;
            }

            item.EnqueueBroadcastPhysicsState();

            return true;
        }

        /// <summary>
        /// This method processes the Game Action (F7B1) Get And Wield Item (0x001A)
        /// This is raised when we:
        /// - try to wield an item in from inventory
        /// - try to wield an item in a chest
        /// - try to wield an item on the landscape
        /// - try to transfer a wielded item to another wield location
        /// </summary>
        public void HandleActionGetAndWieldItem(uint itemGuid, EquipMask wieldedLocation)
        {
            //Console.WriteLine($"{Name}.HandleActionGetAndWieldItem({itemGuid:X8}, {wieldedLocation})");

            // todo fix this, it seems IsAnimating is always true for a player
            // todo we need to know when a player is busy to avoid additional actions during that time
            /*if (IsAnimating)
            {
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, WeenieError.YoureTooBusy));
                return;
            }*/

            var item = FindObject(new ObjectGuid(itemGuid), SearchLocations.LocationsICanMove, out var fromContainer, out var rootOwner, out var wasEquipped);

            if (item == null)
            {
                Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, "Item not found!")); // Custom error message
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, itemGuid));
                return;
            }

            // A vendor-rooted item is never wieldable directly. FindObject's LastUsedContainer branch resolves
            // any guid through the vendor's TryGetItemForSale and reports the vendor as rootOwner, and for the
            // account vault that stock is the real persisted biota rather than disposable vendor stock. Without
            // this guard the item ends up wielded while its ContainerId still points at the vault container,
            // which duplicates it on the next load and bypasses the vault's withdraw authorization entirely.
            // Mirrors the identical refusal in HandleActionPutItemInContainer.
            if (rootOwner is Vendor)
            {
                Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.NotAllTheItemsAreAvailable));
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, itemGuid));
                return;
            }

            if (!item.Guid.IsDynamic() || item is Creature || item.Stuck)
            {
                log.WarnFormat("Player 0x{0:X8}:{1} tried to move item 0x{2:X8}:{3}.", Guid.Full, Name, item.Guid.Full, item.Name);
                Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.Stuck));
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, itemGuid));
                return;
            }

            if (rootOwner != this && !HasEnoughBurdenToAddToInventory(item))
            {
                Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, "You are too encumbered to carry that!"));
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, itemGuid));
                return;
            }

            if (!item.ValidLocations.HasValue || item.ValidLocations == EquipMask.None)
            {
                log.WarnFormat("Player 0x{0:X8}:{1} tried to wield item 0x{2:X8}:{3} to {4} (0x{4:X}), not in item's validlocatiions {5} (0x{5:X}).", Guid.Full, Name, item.Guid.Full, item.Name, wieldedLocation, item.ValidLocations ?? 0);
                Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.InvalidInventoryLocation));
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, itemGuid));
                return;
            }

            if (rootOwner != this) // Item is on the landscape, or in a landblock chest
            {
                if (CombatMode != CombatMode.NonCombat)
                {
                    Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, "Cannot pick that up and wield it while not at peace!")); // Custom error message
                    Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, itemGuid));
                    return;
                }

                CreateMoveToChain(rootOwner ?? item, (success) =>
                {
                    if (CurrentLandblock == null) // Maybe we were teleported as we were motioning to pick up the item
                    {
                        Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, itemGuid, WeenieError.ActionCancelled));
                        return;
                    }

                    if (!success)
                    {
                        Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, itemGuid, WeenieError.ActionCancelled));
                        return;
                    }

                    // Was this item picked up by someone else?
                    if (rootOwner == null && item.CurrentLandblock == null)
                    {
                        Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, itemGuid, WeenieError.ActionCancelled));
                        return;
                    }

                    StartPickup();

                    var pickupMotion = GetPickupMotion(rootOwner ?? item);
                    var pickupChain = AddPickupChainToMoveToChain(pickupMotion);

                    pickupChain.AddAction(this, () =>
                    {
                        // Was this item picked up by someone else?
                        if (rootOwner == null && item.CurrentLandblock == null)
                        {
                            Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, itemGuid, WeenieError.ActionCancelled));
                            EnqueuePickupDone(pickupMotion);
                            return;
                        }

                        if (!VerifyQuest(item, rootOwner, out bool questSolve, out bool isFromAPlayerCorpse))
                        {
                            // InventoryServerSaveFailed previously sent in QuestManager
                            EnqueuePickupDone(pickupMotion);
                            return;
                        }

                        if (DoHandleActionGetAndWieldItem(item, fromContainer, rootOwner, wasEquipped, wieldedLocation))
                        {
                            Session.Network.EnqueueSend(new GameMessagePrivateUpdatePropertyInt(this, PropertyInt.EncumbranceVal, EncumbranceVal ?? 0));

                            EnqueueBroadcast(new GameMessageSound(Guid, Sound.PickUpItem));

                            item.EmoteManager.OnPickup(this);
                            item.NotifyOfEvent(RegenerationType.PickUp);

                            // speed challenge (WaffleACE): picking a season's objective item up off the
                            // ground is a third way into the completion funnel, alongside using a goal
                            // object (WorldObject_Use.cs) and killing a boss (Creature_Death.cs). The
                            // instance check runs FIRST, not the item's flag, because it short-circuits on the
                            // overwhelmingly common case: almost every pickup in the game is not a speed run,
                            // so an ordinary pickup pays one property read and an instance compare rather than
                            // two property reads. The funnel re-validates the run and gates on the season's
                            // ObjectiveWcid itself, so this call site does not need to re-check that.
                            if (IsInSpeedChallengeInstance && item.GetProperty(PropertyBool.SpeedChallengeGoal) == true)
                            {
                                // A stackable objective item merges onto an existing stack through
                                // DoHandleActionStackableMerge instead of this path, so it would never reach
                                // OnPickup or the completion funnel, and the run could never be completed with
                                // no visible cause. MaxStackSize is what declares the item STACKABLE; StackSize
                                // is only how many are in this particular pile, and reads 1 for the first one
                                // ever dropped - so checking StackSize here would miss exactly the case that
                                // breaks later. Content authoring cannot make the item non-stackable from
                                // here, so log loudly rather than fail silently.
                                if (item.MaxStackSize.HasValue && item.MaxStackSize.Value > 1)
                                    log.Error($"[SPEED] {item.Name} (0x{item.Guid}, wcid {item.WeenieClassId}) is flagged SpeedChallengeGoal but is STACKABLE (MaxStackSize {item.MaxStackSize.Value}); a merged pickup never reaches OnPickup, so this objective item can never finish a run. The objective item must not be stackable.");

                                TryFinishSpeedChallenge(item);
                            }

                            if (questSolve)
                                item.EmoteManager.OnQuest(this);

                            if (isFromAPlayerCorpse)
                            {
                                log.InfoFormat("[CORPSE] {0} (0x{1}) picked up and wielded {2} (0x{3}) from {4} (0x{5})", Name, Guid, item.Name, item.Guid, rootOwner.Name, rootOwner.Guid);
                                item.SaveBiotaToDatabase();
                            }
                        }
                        EnqueuePickupDone(pickupMotion);
                    });

                    pickupChain.EnqueueChain();

                }, null, false);    // if player is within UseRadius of moveToTarget, do not perform rotation
            }
            else
            {
                DoHandleActionGetAndWieldItem(item, fromContainer, rootOwner, wasEquipped, wieldedLocation);
            }
        }

        private bool DoHandleActionGetAndWieldItem(WorldObject item, Container fromContainer, Container itemRootOwner, bool wasEquipped, EquipMask wieldedLocation, bool fromSplit = false)
        {
            // Console.WriteLine($"-> DoHandleActionGetAndWieldItem({item.Name}, {itemRootOwner?.Name}, {wasEquipped}, {wieldedLocation})");

            var wieldError = CheckWieldRequirements(item);

            if (wieldError != WeenieError.None)
            {
                // client doesnt show specific wieldError here, just '<item> can't be wielded'?
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, item.Guid.Full, wieldError));
                return false;
            }

            // the client handles dequipping a lot of conflicting items automatically,
            // but there are some cases it misses that must be handled specifically here:
            if (((item.CurrentWieldedLocation ?? 0) & EquipMask.SelectablePlusAmmo) == 0 && !CheckWeaponCollision(item, wieldedLocation))
            {
                // Is this generic message good enough? -- '<item> can't be wielded'?
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, item.Guid.Full, wieldError));
                return false;
            }

            // Unwield wand/missile launcher/two-handed if dual wielding
            if (wieldedLocation == EquipMask.Shield && !item.IsShield)
            {
                var mainWeapon = GetEquippedMeleeWeapon(true);

                if (mainWeapon != null && !mainWeapon.IsTwoHanded)
                    mainWeapon = null;

                mainWeapon = mainWeapon ?? GetEquippedMissileWeapon() ?? GetEquippedWand();

                // special case: instead of sending the typical DequipItem -> GetAndWieldItem here,
                // the client just sends GetAndWieldItem, and the server is responsible for detecting if DequipItem is needed

                if (mainWeapon != null)
                {
                    // this wasn't a thing in retail, and can bug out the client during laggy conditions

                    // if main-hand slot is filled with anything other than a 1-handed melee weapon, send error
                    Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, item.Guid.Full, WeenieError.ConflictingInventoryLocation));
                    return false;

                    /*if (CombatMode != CombatMode.NonCombat)
                    {
                        HandleActionChangeCombatMode(CombatMode.Melee, true, () =>
                        {
                            if (!DoHandleActionGetAndWieldItem_DequipItemToInventory(mainWeapon, item))
                                return;

                            DoHandleActionGetAndWieldItem(item, itemRootOwner, wasEquipped, wieldedLocation);
                        });
                        return true;
                    }
                    else if (!DoHandleActionGetAndWieldItem_DequipItemToInventory(mainWeapon, item))
                    {
                        return false;
                    }*/
                }
            }

            // Unwield dual weapon if equipping thrown weapon
            if (wieldedLocation == EquipMask.MissileWeapon)
            {
                var dualWield = GetDualWieldWeapon();

                // special case: instead of sending the typical DequipItem -> GetAndWieldItem here,
                // the client just sends GetAndWieldItem, and the server is responsible for detecting if DequipItem is needed

                if (dualWield != null)
                {
                    // this wasn't a thing in retail, and can bug out the client during laggy conditions

                    // if wielding an off-hand weapon, send error
                    Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, item.Guid.Full, WeenieError.ConflictingInventoryLocation));
                    return false;

                    /*if (CombatMode != CombatMode.NonCombat)
                    {
                        HandleActionChangeCombatMode(CombatMode.Melee, true, () =>
                        {
                            if (!DoHandleActionGetAndWieldItem_DequipItemToInventory(dualWield, item))
                                return;

                            DoHandleActionGetAndWieldItem(item, itemRootOwner, wasEquipped, wieldedLocation);
                        });
                        return true;
                    }
                    else if (!DoHandleActionGetAndWieldItem_DequipItemToInventory(dualWield, item))
                    {
                        return false;
                    }*/
                }
            }

            if (((item.ValidLocations ?? 0) & wieldedLocation) == 0)
            {
                if (item.ValidLocations == EquipMask.MeleeWeapon && wieldedLocation == EquipMask.Shield)
                {
                    // allow dual wielding
                }
                else
                {
                    log.Warn($"{Name} tried to wield {item.Name} ({item.Guid}) in slot {wieldedLocation}, which doesn't match valid slots {item.ValidLocations}");
                    Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, item.Guid.Full));
                    return false;
                }
            }

            // client bug: equip wand or bow
            // then equip melee weapon instead, then swap melee weapon to offhand slot
            // client automatically sends a request to wield the wand/bow again, only this time with EquipMask.MeleeWeapon
            // this client bug will still exist for melee weapons
            if (wieldedLocation == EquipMask.MeleeWeapon && ((item.ValidLocations ?? EquipMask.None) & wieldedLocation) == 0)
            {
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, item.Guid.Full));
                return false;
            }

            // verify Aetheria slot, client doesn't handle this
            if ((wieldedLocation & EquipMask.Sigil) != 0)
            {
                if (wieldedLocation.HasFlag(EquipMask.SigilOne)   && !AetheriaFlags.HasFlag(AetheriaBitfield.Blue) ||
                    wieldedLocation.HasFlag(EquipMask.SigilTwo)   && !AetheriaFlags.HasFlag(AetheriaBitfield.Yellow) ||
                    wieldedLocation.HasFlag(EquipMask.SigilThree) && !AetheriaFlags.HasFlag(AetheriaBitfield.Red))
                {
                    Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, item.Guid.Full));
                    return false;
                }
            }

            // TODO: this handles armor slots,
            // trinkets and weapons would need to be handled a bit differently

            // TODO: slots view is bugged here
            // for both slots view and non-slots view, the client is oddly sending 2 packets, similar to dual wielding weapon swapping
            // for non-slots view, the 2 packets it sends both have the full coverage slots in wieldedLocation
            // for slots view, it sends the correct packet first, with the full coverage, and then it sends a packet with coverage for just 1 slot
            // this bugs out CurrentWieldedLocation, as it won't be covering all of the slots... so for armor/clothing we set wieldedLocation to item.ValidLocations here
            if (item is Clothing)
                wieldedLocation = item.ValidLocations ?? 0;

            // verify item slot is valid
            // restricting this to two-handed for now, as without that clamp, it bugs out dual wielding and possibly other things
            if (item.WeaponSkill == Skill.TwoHandedCombat && (wieldedLocation & item.ValidLocations) == 0)
            {
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, item.Guid.Full));
                return false;
            }

            if (!WieldedLocationIsAvailable(item, wieldedLocation))
            {
                // filtering to just armor here, or else trinkets and dual wielding breaks
                //var existing = GetEquippedClothingArmor(item.ClothingPriority ?? 0).FirstOrDefault();
                var existing = GetEquippedItems(item, wieldedLocation).FirstOrDefault();

                Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, $"You must remove your {existing?.Name} to wield {item.Name}"));
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, item.Guid.Full));
                return false;
            }

            if (wasEquipped) // Movement is an equipped item to another equipped item slot
            {
                var prevLocation = item.CurrentWieldedLocation;

                item.CurrentWieldedLocation = wieldedLocation;
                Session.Network.EnqueueSend(new GameMessagePublicUpdatePropertyInt(item, PropertyInt.CurrentWieldedLocation, (int)wieldedLocation));

                Session.Network.EnqueueSend(new GameEventWieldItem(Session, item.Guid.Full, wieldedLocation));

                // handle swapping melee weapon between hands
                if (IsInChildLocation(item))
                {
                    ResetChild(item);
                    EnqueueBroadcast(new GameMessageParentEvent(this, item));

                    // handle swapping dual-wielded weapons
                    if (IsDoubleSend)
                        HandleActionGetAndWieldItem(Prev_PutItemInContainer[0].ItemGuid, (EquipMask)prevLocation);
                    else
                        Session.Network.EnqueueSend(new GameMessageSound(Guid, Sound.WieldObject));
                }

                // perform stance swapping if necessary
                TryShuffleStance(wieldedLocation);

                return true;
            }

            if (item.CurrentLandblock != null) // Movement is an item pickup off the landblock
            {
                item.CurrentLandblock.RemoveWorldObject(item.Guid, false, true);
                item.Location = null;
            }
            else // Movement is within the same pack or between packs in a container on the landblock
            {
                if (!fromSplit && !itemRootOwner.TryRemoveFromInventory(item.Guid))
                {
                    Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, "TryRemoveFromInventory failed!")); // Custom error message
                    Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, item.Guid.Full));

                    // Fail closed. Equipping an item we failed to detach leaves it both wielded and still
                    // parented to its old container, which is a duplication on the next load.
                    return false;
                }
            }

            if (!TryEquipObjectWithNetworking(item, wieldedLocation))
            {
                Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, "TryEquipObjectWithNetworking failed!")); // Custom error message
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, item.Guid.Full));

                // todo: So the item isn't lost, we should try to put the item in the players inventory, or if that's full, on the landblock.
                log.WarnFormat("Item 0x{0:X8}:{1} for player {2} lost from DoHandleActionGetAndWieldItem failure.", item.Guid.Full, item.Name, Name);

                return false;
            }

            // if wielding from a loose container, we must save immediately
            if (fromContainer != null && !fromContainer.Stuck)
                item.SaveBiotaToDatabase();

            return true;
        }

        /// <summary>
        /// Client will automatically send any unequip (PutItemInContainer) message before the GetAndWield, but misses some instances and can be memory hacked to ignore others.
        //  Let's just make sure our status is accurate before actually equipping an item! 
        /// </summary>
        /// <param name="item">The weapon we are attempting to equip</param>
        /// <returns>True if the items were successfuly remove and the new item can attempt to be equipped, otherwise false</returns>
        private bool CheckWeaponCollision(WorldObject item = null, EquipMask? wieldedLocation = null, CombatMode? combatMode = null)
        {
            // Client actually allows these equip scenarios:
            // Shield with a Two-Handed weapon.

            combatMode = combatMode ?? CombatMode;

            WorldObject offhand, mainhand, ammo;
            if (item != null && wieldedLocation != null)
            {
                // Equipping a new item
                switch (wieldedLocation)
                {
                    case EquipMask.Shield:
                        // Remove any items in the shield/offhand slot, two-handed weapons, missile weapons or casters
                        offhand = GetEquippedOffHand();
                        if (offhand != null)
                        {
                            log.Warn($"'{Name}' tried to wield '{item.Name}' ({item.Guid}) in slot {wieldedLocation}, but is occupied by '{offhand.Name}'");
                            return false;
                        }

                        mainhand = GetEquippedMainHand();
                        // Remove any Two Handed, Caster (magic), or Missile Weapons
                        if (mainhand != null && (mainhand.IsTwoHanded || mainhand.IsCaster || mainhand.IsAmmoLauncher))
                        {
                            log.Warn($"'{Name}' tried to wield '{item.Name}' ({item.Guid}) in slot {wieldedLocation}, which conflicts with '{mainhand.Name}'");
                            return false;
                        }

                        break;
                    case EquipMask.MissileWeapon:
                        // Should not have any items in either hand for ammo launchers (bows, atlatls)
                        // Thrown weapons (ie. phials) can have a shield
                        offhand = GetEquippedOffHand();
                        if (offhand != null && item.IsAmmoLauncher)
                        {
                            log.Warn($"'{Name}' tried to wield '{item.Name}' ({item.Guid}) in slot {wieldedLocation}, which conflicts with '{offhand.Name}'");
                            return false;
                        }

                        mainhand = GetEquippedMainHand();
                        if (mainhand != null)
                        {
                            log.Warn($"'{Name}' tried to wield '{item.Name}' ({item.Guid}) in slot {wieldedLocation}, which conflicts with '{mainhand.Name}'");
                            return false;
                        }

                        // Ensure our ammo types align properly
                        ammo = GetEquippedAmmo();
                        if (item.AmmoType != null && ammo != null && ammo.AmmoType != item.AmmoType)
                        {
                            log.Warn($"'{Name}' tried to wield '{item.Name}' ({item.Guid}), AmmoType: {item.AmmoType} in slot {wieldedLocation}, which conflicts with ammo of '{ammo.Name}' ({ammo.AmmoType})");
                            return false;
                        }

                        break;
                    case EquipMask.MissileAmmo:
                        // Ensure our ammo types align properly
                        mainhand = GetEquippedMainHand();
                        if (mainhand != null && mainhand.AmmoType != null && item.AmmoType != null && mainhand.AmmoType != item.AmmoType)
                        {
                            log.Warn($"'{Name}' tried to wield '{item.Name}' ({item.Guid}), AmmoType: {item.AmmoType} in slot {wieldedLocation}, which conflicts with AmmoType of '{mainhand.Name}' ({mainhand.AmmoType})");
                            return false;
                        }

                        break;
                    case EquipMask.TwoHanded:
                        // Should not have any items in the shield/offhand slot, two-handed weapons, missile weapons or casters
                        offhand = GetEquippedOffHand();
                        if (offhand != null)
                        {
                            log.Warn($"'{Name}' tried to wield '{item.Name}' ({item.Guid}) in slot {wieldedLocation}, which conflicts with '{offhand.Name}'");
                            return false;
                        }

                        mainhand = GetEquippedMainHand();
                        // Remove anything in the main hand!
                        if (mainhand != null)
                        {
                            log.Warn($"'{Name}' tried to wield '{item.Name}' ({item.Guid}) in slot {wieldedLocation}, which conflicts with '{mainhand.Name}'");
                            return false;
                        }
                        break;
                    case EquipMask.MeleeWeapon:
                        // Should not have any Caster, Missile, or TwoHanders equipped
                        offhand = GetEquippedOffHand();
                        if (offhand != null && (offhand.IsTwoHanded || offhand.IsCaster || offhand.IsAmmoLauncher))
                        {
                            log.Warn($"'{Name}' tried to wield '{item.Name}' ({item.Guid}) in slot {wieldedLocation}, which conflicts with '{offhand.Name}'");
                            return false;
                        }

                        mainhand = GetEquippedMainHand();
                        if (mainhand != null && (mainhand.IsTwoHanded || mainhand.IsCaster || mainhand.IsAmmoLauncher))
                        {
                            log.Warn($"'{Name}' tried to wield '{item.Name}' ({item.Guid}) in slot {wieldedLocation}, which conflicts with '{mainhand.Name}'");
                            return false;
                        }
                        break;

                    case EquipMask.Held:
                        // Should not have any items in offhand slot for casters only
                        if (item.IsCaster)
                        {
                            offhand = GetEquippedOffHand();
                            if (offhand != null)
                            {
                                log.Warn($"'{Name}' tried to wield '{item.Name}' ({item.Guid}) in slot {wieldedLocation}, which conflicts with '{offhand.Name}'");
                                return false;
                            }
                        }

                        // Should not have any items still in mainhand slot
                        mainhand = GetEquippedMainHand();
                        if (mainhand != null)
                        {
                            log.Warn($"'{Name}' tried to wield '{item.Name}' ({item.Guid}) in slot {wieldedLocation}, which conflicts with '{mainhand.Name}'");
                            return false;
                        }

                        // items such as 23307 - Ball of Gunk have EquipMask.Held and no DefaultCombatMode
                        // can only be wielded in NonCombat mode
                        if (combatMode != CombatMode.NonCombat && item.DefaultCombatStyle == null)
                        {
                            log.Warn($"'{Name}' tried to wield '{item.Name}' ({item.Guid}) in slot {wieldedLocation}, which conflicts with {combatMode} combat mode");
                            return false;
                        }
                        break;
                }
            }
            else
            {
                // changing combat mode

                // Just do a quick sanity check to ensure the player isn't wielding two weapons they shouldn't
                mainhand = GetEquippedMainHand();
                offhand = GetEquippedOffHand();

                // Wielding just one item is perfectly fine...its when they have two if might be suspect
                if (mainhand != null)
                {
                    if (offhand != null)
                    {
                        // Can't wield these with anything else!
                        if (mainhand.IsTwoHanded || mainhand.IsAmmoLauncher || mainhand.IsCaster)
                        {
                            log.Warn($"'{Name}' is illegally wielding '{mainhand.Name}' ({mainhand.Guid}) and {offhand.Name}' ({offhand.Guid})");
                            return false;
                        }
                    }

                    // Ensure our ammo matches up properly
                    if (mainhand.IsAmmoLauncher)
                    {
                        ammo = GetEquippedAmmo();
                        if (ammo != null && ammo.AmmoType != null && mainhand.AmmoType != null && ammo.AmmoType != mainhand.AmmoType)
                        {
                            log.Warn($"'{Name}' is illegally wielding '{mainhand.Name}' ({mainhand.Guid}) with ammo {ammo.Name}' ({ammo.AmmoType})");
                            return false;
                        }
                    }

                    // items such as 23307 - Ball of Gunk have EquipMask.Held and no DefaultCombatMode
                    // they can be placed in main hand in NonCombat mode, but trying to wield them in combat mode results in the client-side error
                    // 'You can't enter combat mode while wielding the <item>'
                    // however, this client-side check can be bypassed with vtank

                    if (mainhand.DefaultCombatStyle == null)
                    {
                        log.Warn($"'{Name}' is illegally wielding '{mainhand.Name}' ({mainhand.Guid}) in {combatMode} combat mode");
                        return false;
                    }
                }
            }

            // All good at this point
            return true;
        }


        private bool DoHandleActionGetAndWieldItem_DequipItemToInventory(WorldObject mainWeapon, WorldObject item)
        {
            if (!TryDequipObjectWithNetworking(mainWeapon.Guid, out var dequippedItem, DequipObjectAction.DequipToPack))
            {
                Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, "Failed to dequip existing weapon!")); // Custom error message
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, item.Guid.Full));
                return false;
            }

            if (!TryCreateInInventoryWithNetworking(dequippedItem))
            {
                Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, "Failed to add dequip back into inventory!")); // Custom error message
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, item.Guid.Full));

                // todo: if this happens, we should just put back the dequipped item to where it was

                return false;
            }
            return true;
        }

        private WeenieError CheckWieldRequirements(WorldObject item)
        {
            if (!PropertyManager.GetBool("use_wield_requirements").Item)
                return WeenieError.None;

            var heritageSpecificArmor = item.GetProperty(PropertyInt.HeritageSpecificArmor);
            if (IsOlthoiPlayer)
            {
                if (heritageSpecificArmor == null || (HeritageGroup)heritageSpecificArmor != HeritageGroup)
                    return WeenieError.HeritageRequiresSpecificArmor;
            }
            else if (IsGearKnightPlayer)
            {
                if (((item.ValidLocations & (EquipMask.Clothing | EquipMask.Armor)) != 0)
                    && (heritageSpecificArmor == null || (HeritageGroup)heritageSpecificArmor != HeritageGroup))
                    return WeenieError.HeritageRequiresSpecificArmor;
            }    
            else
            {
                if (heritageSpecificArmor != null && (HeritageGroup)heritageSpecificArmor != HeritageGroup)
                    return WeenieError.ArmorRequiresSpecificHeritage;
            }

            var allowedWielder = item.GetProperty(PropertyInstanceId.AllowedWielder);
            if (allowedWielder != null && (allowedWielder != Guid.Full))
                return WeenieError.YouDoNotOwnThatItem; // Unsure of the exact message

            var result = CheckWieldRequirement(item.WieldRequirements, item.WieldSkillType, item.WieldDifficulty);
            if (result != WeenieError.None)
                return result;

            result = CheckWieldRequirement(item.WieldRequirements2, item.WieldSkillType2, item.WieldDifficulty2);
            if (result != WeenieError.None)
                return result;

            result = CheckWieldRequirement(item.WieldRequirements3, item.WieldSkillType3, item.WieldDifficulty3);
            if (result != WeenieError.None)
                return result;

            result = CheckWieldRequirement(item.WieldRequirements4, item.WieldSkillType4, item.WieldDifficulty4);
            if (result != WeenieError.None)
                return result;

            return WeenieError.None;
        }

        private WeenieError CheckWieldRequirement(WieldRequirement wieldRequirement, int? wieldSkillType, int? wieldDifficulty)
        {
            var skillOrAttribute = wieldSkillType ?? 0;
            var difficulty = (uint)(wieldDifficulty ?? 0);

            switch (wieldRequirement)
            {
                case WieldRequirement.Skill:

                    // verify skill level - current / buffed
                    var skill = GetCreatureSkill(ConvertToMoASkill((Skill)skillOrAttribute), false);
                    if (skill.Current < difficulty)
                        return WeenieError.SkillTooLow;
                    break;

                case WieldRequirement.RawSkill:

                    // verify skill level - base
                    skill = GetCreatureSkill(ConvertToMoASkill((Skill)skillOrAttribute), false);
                    if (skill.Base < difficulty)
                        return WeenieError.SkillTooLow;
                    break;

                case WieldRequirement.Attrib:

                    // verify primary attribute - current / buffed
                    Attributes.TryGetValue((PropertyAttribute)skillOrAttribute, out var attribute);
                    if (attribute.Current < difficulty)
                        return WeenieError.SkillTooLow;
                    break;

                case WieldRequirement.RawAttrib:

                    // verify primary attribute - base
                    Attributes.TryGetValue((PropertyAttribute)skillOrAttribute, out attribute);
                    if (attribute.Base < difficulty)
                        return WeenieError.SkillTooLow;
                    break;

                case WieldRequirement.SecondaryAttrib:

                    // verify vital - current maxvalue
                    Vitals.TryGetValue((PropertyAttribute2nd)skillOrAttribute, out var vital);
                    if (vital.MaxValue < difficulty)
                        return WeenieError.SkillTooLow;
                    break;

                case WieldRequirement.RawSecondaryAttrib:

                    // verify vital - base
                    Vitals.TryGetValue((PropertyAttribute2nd)skillOrAttribute, out vital);
                    if (vital.Base < difficulty)
                        return WeenieError.SkillTooLow;
                    break;

                case WieldRequirement.Level:

                    // verify player level
                    if ((Level ?? 1) < difficulty)
                        return WeenieError.LevelTooLow;
                    break;

                case WieldRequirement.Training:

                    // verify skill is trained / specialized
                    skill = GetCreatureSkill(ConvertToMoASkill((Skill)skillOrAttribute), false);
                    if ((int)skill.AdvancementClass < difficulty)
                        return WeenieError.SkillTooLow;
                    break;

                case WieldRequirement.IntStat:      // unused in PY16

                    // verify PropertyInt minimum
                    var propInt = GetProperty((PropertyInt)skillOrAttribute) ?? 0;
                    if (propInt < difficulty)
                        return WeenieError.SkillTooLow;
                    break;

                case WieldRequirement.BoolStat:     // unused in PY16

                    // verify PropertyBool equal
                    var propBool = GetProperty((PropertyBool)skillOrAttribute) ?? false;
                    if (propBool != Convert.ToBoolean(difficulty))
                        return WeenieError.SkillTooLow;
                    break;

                case WieldRequirement.CreatureType:

                    // verify creature type
                    var creatureType = CreatureType ?? ACE.Entity.Enum.CreatureType.Invalid;
                    if ((int)creatureType != difficulty)
                        return WeenieError.SkillTooLow;
                    break;

                case WieldRequirement.HeritageType:

                    // verify heritage type
                    if ((int)HeritageGroup != difficulty)
                        return WeenieError.ArmorRequiresSpecificHeritage;
                    break;
            }

            return WeenieError.None;
        }


        // =========================================
        // Game Action Handlers - Inventory Stacking 
        // =========================================

        /// <summary>
        /// This method processes the Game Action (F7B1) Stackable Split To Container (0x0055)
        /// This is raised when we:
        /// - try to split a stack into the same container
        /// - try to split a stack off of the landblock into a container
        /// - try to split a stack into a different container that doesn't already have a stack that can support a merge
        /// </summary>
        public void HandleActionStackableSplitToContainer(uint stackId, uint containerId, int placementPosition, int amount)
        {
            //Console.WriteLine($"{Name}.HandleActionStackableSplitToContainer({stackId:X8}, {containerId:X8}, {placementPosition}, {amount})");

            if (IsBusy || Teleporting || suicideInProgress)
            {
                Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.YoureTooBusy));
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, stackId));
                return;
            }

            if (amount <= 0)
            {
                log.WarnFormat("Player 0x{0:X8}:{1} tried to split item with invalid amount ({3}) 0x{2:X8}.", Guid.Full, Name, stackId, amount);
                Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, "Split amount not valid!")); // Custom error message
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, stackId));
                return;
            }

            var stack = FindObject(new ObjectGuid(stackId), SearchLocations.LocationsICanMove, out var stackFoundInContainer, out var stackRootOwner, out _);
            var container = FindObject(new ObjectGuid(containerId), SearchLocations.MyInventory | SearchLocations.Landblock | SearchLocations.LastUsedContainer, out _, out var containerRootOwner, out _) as Container;

            if (stack == null)
            {
                Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, "Source stack not found!")); // Custom error message
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, stackId));
                return;
            }

            // A vendor-rooted object is never a valid endpoint for an inventory movement. This runs
            // BEFORE the player-to-world classification below, not inside it: when neither endpoint is
            // the player (vault stock to a landblock container, or one vault row to another) that
            // branch is not taken and a nested guard is skipped entirely. PersonalVendor puts real,
            // persisted player property and a durable ledger behind TryGetItemForSale, so a skipped
            // refusal here mints items out of the panel.
            //
            // `container is Vendor` is needed in addition to `containerRootOwner is Vendor` because
            // FindObject's Landblock arm returns without assigning rootOwner, so the vendor's own guid
            // sent as the destination arrives with a null containerRootOwner.
            if (stackRootOwner is Vendor || containerRootOwner is Vendor || container is Vendor)
            {
                Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, "You cannot split that."));
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, stackId));
                return;
            }

            if (!stack.Guid.IsDynamic() || stack.Stuck)
            {
                log.WarnFormat("Player 0x{0:X8}:{1} tried to move item 0x{2:X8}:{3}.", Guid.Full, Name, stack.Guid.Full, stack.Name);
                Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.Stuck));
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, stackId));
                return;
            }

            var isStackable = stack is Stackable;
            if (!isStackable)
            {
                log.WarnFormat("Player 0x{0:X8}:{1} tried to split an item 0x{2:X8}:{3} that is not stackable.", Guid.Full, Name, stack.Guid.Full, stack.Name);
                //Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.Stuck));
                Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, "You cannot split that!")); // Custom error message
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, stackId));
                return;
            }

            if (stackRootOwner != this && containerRootOwner == this && !HasEnoughBurdenToAddToInventory(stack))
            {
                Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, "You are too encumbered to carry that!"));
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, stackId));
                return;
            }

            if (container == null)
            {
                Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, "Target container not found!")); // Custom error message
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, stackId));
                return;
            }

            if (container is Corpse)
            {
                Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, $"You cannot put {stack.Name} in that.")); // Custom error message
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, stackId));
                return;
            }

            if (stack.StackSize == null || stack.StackSize == 0)
            {
                log.WarnFormat("Player 0x{0:X8}:{1} tried to split invalid item 0x{2:X8}:{3}.", Guid.Full, Name, stack.Guid.Full, stack.Name);
                Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, "Stack not valid!")); // Custom error message
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, stackId));
                return;
            }

            if (stack.StackSize <= amount)
            {
                log.WarnFormat("Player 0x{0:X8}:{1} tried to split item with invalid amount ({4}) 0x{2:X8}:{3}.", Guid.Full, Name, stack.Guid.Full, stack.Name, amount);
                Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, "Split amount not valid!")); // Custom error message
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, stackId));
                return;
            }

            if (IsTrading && stack.IsBeingTradedOrContainsItemBeingTraded(ItemsInTradeWindow))
            {
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, stackId, WeenieError.TradeItemBeingTraded));
                return;
            }

            if (stack.IsAttunedOrContainsAttuned && stackRootOwner == this && containerRootOwner != this)
            {
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, stackId));
                return;
            }

            if ((stackRootOwner == this && containerRootOwner != this)  || (stackRootOwner != this && containerRootOwner == this)) // Movement is between the player and the world
            {
                WorldObject moveToObject;

                if (stackRootOwner == this)
                    moveToObject = containerRootOwner ?? container;
                else
                    moveToObject = stackRootOwner ?? stack;

                var stackOriginalContainer = stack.ContainerId;

                CreateMoveToChain(moveToObject, (success) =>
                {
                    if (CurrentLandblock == null) // Maybe we were teleported as we were motioning to split the item
                    {
                        Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, stackId, WeenieError.ActionCancelled));
                        return;
                    }

                    if (!success)
                    {
                        Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, stackId, WeenieError.ActionCancelled));
                        return;
                    }

                    // We make sure the stack is still valid. It could have changed during our movement
                    if (stackOriginalContainer != stack.ContainerId || stack.StackSize <= amount)
                    {
                        log.DebugFormat("Player 0x{0:X8}:{1} tried to split an item that's no longer valid 0x{2:X8}:{3}.", Guid.Full, Name, stack.Guid.Full, stack.Name);
                        Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, "Split failed!")); // Custom error message
                        Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, stackId, WeenieError.ActionCancelled));
                        return;
                    }

                    StartPickup();

                    var pickupMotion = GetPickupMotion(moveToObject);
                    var pickupChain = AddPickupChainToMoveToChain(pickupMotion);

                    pickupChain.AddAction(this, () =>
                    {
                        // We make sure the stack is still valid. It could have changed during our pickup animation
                        if (stackOriginalContainer != stack.ContainerId || stack.StackSize <= amount)
                        {
                            log.DebugFormat("Player 0x{0:X8}:{1} tried to split an item that's no longer valid 0x{2:X8}:{3}.", Guid.Full, Name, stack.Guid.Full, stack.Name);
                            Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, "Split failed!")); // Custom error message
                            Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, stackId, WeenieError.ActionCancelled));
                            EnqueuePickupDone(pickupMotion);
                            return;
                        }

                        var newStack = WorldObjectFactory.CreateNewWorldObject(stack.WeenieClassId);

                        if (newStack == null)
                        {
                            // this should never happen under normal circumstances,
                            // but can happen if the player has an item in their inventory that is no longer in the world database
                            Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, stackId, WeenieError.ActionCancelled));
                            EnqueuePickupDone(pickupMotion);
                            return;
                        }

                        newStack.SetStackSize(amount);

                        if (DoHandleActionStackableSplitToContainer(stack, stackFoundInContainer, stackRootOwner, container, containerRootOwner, newStack, placementPosition, amount))
                        {
                            Session.Network.EnqueueSend(new GameMessagePrivateUpdatePropertyInt(this, PropertyInt.EncumbranceVal, EncumbranceVal ?? 0));

                            if (stack.WeenieType == WeenieType.Coin)
                                UpdateCoinValue();

                            if (stackRootOwner == this)
                                EnqueueBroadcast(new GameMessageSound(Guid, Sound.DropItem));
                            else if (containerRootOwner == this)
                                EnqueueBroadcast(new GameMessageSound(Guid, Sound.PickUpItem));
                        }
                        EnqueuePickupDone(pickupMotion);
                    });

                    pickupChain.EnqueueChain();

                }, null, false);    // if player is within UseRadius of moveToTarget, do not perform rotation
            }
            else // This is a self-contained movement
            {
                var newStack = WorldObjectFactory.CreateNewWorldObject(stack.WeenieClassId);

                if (newStack == null)
                {
                    // this should never happen under normal circumstances,
                    // but can happen if the player has an item in their inventory that is no longer in the world database
                    Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, stackId));
                    return;
                }

                newStack.SetStackSize(amount);

                DoHandleActionStackableSplitToContainer(stack, stackFoundInContainer, stackRootOwner, container, containerRootOwner, newStack, placementPosition, amount);
            }
        }

        private bool DoHandleActionStackableSplitToContainer(WorldObject stack, Container stackFoundInContainer, Container stackRootOwner, Container container, Container containerRootOwner, WorldObject newStack, int placementPosition, int amount)
        {
            //Console.WriteLine($"{Name}.DoHandleActionStackableSplitToContainer({stack?.Name}, {stackFoundInContainer?.Name}, {stackRootOwner?.Name}, {container?.Name}, {containerRootOwner?.Name}, {newStack?.Name}, {placementPosition}, {amount})");

            // Decrement the source stack BEFORE creating/placing the new split stack. If we added the
            // new stack first and the source decrement then failed, the player would be left holding
            // both the full source stack and the new stack (item dupe). Order matches the 3D-split path.
            if (!AdjustStack(stack, -amount, stackFoundInContainer, stackRootOwner))
            {
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, stack.Guid.Full));
                return false;
            }

            // Now try to place the new stack. If that fails, roll the source stack back to its
            // original size so no units are lost.
            if (!container.TryAddToInventory(newStack, placementPosition, true))
            {
                AdjustStack(stack, amount, stackFoundInContainer, stackRootOwner);

                if (stackRootOwner == null)
                    EnqueueBroadcast(new GameMessageSetStackSize(stack));
                else
                    Session.Network.EnqueueSend(new GameMessageSetStackSize(stack));

                Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, "TryAddToInventory failed!")); // Custom error message
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, stack.Guid.Full));
                return false;
            }

            if (container != containerRootOwner && containerRootOwner != null)
            {
                containerRootOwner.EncumbranceVal += (stack.StackUnitEncumbrance * amount);
                containerRootOwner.Value += (stack.StackUnitValue * amount);
            }

            Session.Network.EnqueueSend(new GameMessageCreateObject(newStack));
            Session.Network.EnqueueSend(new GameEventItemServerSaysContainId(Session, newStack, container));

            if (stackRootOwner == null)
                EnqueueBroadcast(new GameMessageSetStackSize(stack));
            else
                Session.Network.EnqueueSend(new GameMessageSetStackSize(stack));

            return true;
        }

        /// <summary>
        /// This method processes the Game Action (F7B1) Stackable Split To 3D (0x0056)
        /// This is raised when we:
        /// - try to split a stack onto the landblock
        /// </summary>
        public void HandleActionStackableSplitTo3D(uint stackId, int amount)
        {
            if (IsBusy || Teleporting || suicideInProgress)
            {
                Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.YoureTooBusy));
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, stackId));
                return;
            }

            // Proving Grounds (WaffleACE): putting anything on the floor inside the survival arena ends the run -
            // see Player_SurvivalChallenge.TryEndSurvivalChallengeOnDrop for why. The drop is refused rather than
            // completed, so the item is not stranded in the instance the player is being pulled out of.
            if (TryEndSurvivalChallengeOnDrop())
            {
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, stackId));
                return;
            }

            if (amount <= 0)
            {
                log.WarnFormat("Player 0x{0:X8}:{1} tried to split item with invalid amount ({3}) 0x{2:X8}.", Guid.Full, Name, stackId, amount);
                Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, "Split amount not valid!")); // Custom error message
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, stackId));
                return;
            }

            var stack = FindObject(new ObjectGuid(stackId), SearchLocations.MyInventory | SearchLocations.MyEquippedItems, out var stackFoundInContainer, out var stackRootOwner, out _);

            if (stack == null)
            {
                Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, "Stack not found!")); // Custom error message
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, stackId));
                return;
            }

            var isStackable = stack is Stackable;
            if (!isStackable)
            {
                log.WarnFormat("Player 0x{0:X8}:{1} tried to split an item 0x{2:X8}:{3} that is not stackable.", Guid.Full, Name, stack.Guid.Full, stack.Name);
                //Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.Stuck));
                Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, "You cannot split that!")); // Custom error message
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, stackId));
                return;
            }

            if (stack.StackSize == null || stack.StackSize == 0)
            {
                log.WarnFormat("Player 0x{0:X8}:{1} tried to split invalid item 0x{2:X8}:{3}.", Guid.Full, Name, stack.Guid.Full, stack.Name);
                Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, "Stack not valid!")); // Custom error message
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, stackId));
                return;
            }

            if (stack.StackSize <= amount)
            {
                log.WarnFormat("Player 0x{0:X8}:{1} tried to split item with invalid amount ({4}) 0x{2:X8}:{3}.", Guid.Full, Name, stack.Guid.Full, stack.Name, amount);
                Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, "Split amount not valid!")); // Custom error message
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, stackId));
                return;
            }

            if (stack.IsAttunedOrContainsAttuned)
            {
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, stackId, WeenieError.AttunedItem));
                return;
            }

            if (IsTrading && stack.IsBeingTradedOrContainsItemBeingTraded(ItemsInTradeWindow))
            {
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, stackId, WeenieError.TradeItemBeingTraded));
                return;
            }

            var speed = GetPickupAnimationSpeed();

            var actionChain = StartPickupChain();

            actionChain.AddAction(this, () =>
            {
                if (CurrentLandblock == null) // Maybe we were teleported as we were motioning to drop the item
                {
                    Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, stackId, WeenieError.ActionCancelled));
                    return;
                }

                if (!AdjustStack(stack, -amount, stackFoundInContainer, stackRootOwner))
                {
                    Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, stackId, WeenieError.ActionCancelled));
                    return;
                }

                Session.Network.EnqueueSend(new GameMessageSetStackSize(stack));

                var newStack = WorldObjectFactory.CreateNewWorldObject(stack.WeenieClassId);

                if (newStack == null)
                {
                    // this should never happen under normal circumstances,
                    // but can happen if the player has an item in their inventory that is no longer in the world database
                    Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, stackId, WeenieError.ActionCancelled));
                    return;
                }

                newStack.SetStackSize(amount);

                Session.Network.EnqueueSend(new GameMessagePrivateUpdatePropertyInt(this, PropertyInt.EncumbranceVal, EncumbranceVal ?? 0));

                if (stack.WeenieType == WeenieType.Coin)
                    UpdateCoinValue();

                if (TryDropItem(newStack))
                {
                    EnqueueBroadcast(new GameMessageSound(Guid, Sound.DropItem));
                }
                else
                {
                    // restore original stack
                    if (AdjustStack(stack, amount, stackFoundInContainer, stackRootOwner))
                    {
                        Session.Network.EnqueueSend(new GameMessagePrivateUpdatePropertyInt(this, PropertyInt.EncumbranceVal, EncumbranceVal ?? 0));

                        if (stack.WeenieType == WeenieType.Coin)
                            UpdateCoinValue();
                    }
                    else
                        log.WarnFormat("Partial stack 0x{0:X8}:{1} for player {2} lost from HandleActionStackableSplitTo3D failure.", stack.Guid.Full, stack.Name, Name);

                    newStack.Destroy();
                }

                var returnStance = new Motion(CurrentMotionState.Stance, MotionCommand.Ready, speed);
                EnqueueBroadcastMotion(returnStance);
            });

            // WaffleACE: undo the sped-up Ready broadcast so the idle cycle doesn't stay fast
            if (speed != 1.0f)
            {
                var pickupAnimationLength = DatManager.PortalDat.ReadFromDat<MotionTable>(MotionTableId).GetAnimationLength(MotionPickup);
                actionChain.AddDelaySeconds(pickupAnimationLength / speed);
                actionChain.AddAction(this, () => EnqueueBroadcastMotion(new Motion(CurrentMotionState.Stance)));
            }

            actionChain.EnqueueChain();
        }

        public void HandleActionStackableSplitToWield(uint stackId, EquipMask wieldedLocation, int amount)
        {
            //Console.WriteLine($"{Name}.HandleActionStackableSplitToWield({stackId:X8}, {wieldedLocation}, {amount})");

            if (amount <= 0)
            {
                log.WarnFormat("Player 0x{0:X8}:{1} tried to split item with invalid amount ({3}) 0x{2:X8}.", Guid.Full, Name, stackId, amount);
                Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, "Split amount not valid!")); // Custom error message
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, stackId));
                return;
            }

            var stack = FindObject(new ObjectGuid(stackId), SearchLocations.LocationsICanMove, out var stackFoundInContainer, out var stackRootOwner, out _);

            if (stack == null)
            {
                Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, "Source stack not found!")); // Custom error message
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, stackId));
                return;
            }

            if (!stack.Guid.IsDynamic() || stack.Stuck)
            {
                log.WarnFormat("Player 0x{0:X8}:{1} tried to move item 0x{2:X8}:{3}.", Guid.Full, Name, stack.Guid.Full, stack.Name);
                Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.Stuck));
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, stackId));
                return;
            }

            var isStackable = stack is Stackable;
            if (!isStackable)
            {
                log.WarnFormat("Player 0x{0:X8}:{1} tried to split an item 0x{2:X8}:{3} that is not stackable.", Guid.Full, Name, stack.Guid.Full, stack.Name);
                //Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.Stuck));
                Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, "You cannot split that!")); // Custom error message
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, stackId));
                return;
            }

            if (stackRootOwner != this && !HasEnoughBurdenToAddToInventory(stack))
            {
                Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, "You are too encumbered to carry that!"));
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, stackId));
                return;
            }

            if (stack.StackSize == null || stack.StackSize == 0)
            {
                log.WarnFormat("Player 0x{0:X8}:{1} tried to split invalid item 0x{2:X8}:{3}.", Guid.Full, Name, stack.Guid.Full, stack.Name);
                Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, "Stack not valid!")); // Custom error message
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, stackId));
                return;
            }

            if (stack.StackSize <= amount)
            {
                log.WarnFormat("Player 0x{0:X8}:{1} tried to split item with invalid amount ({4}) 0x{2:X8}:{3}.", Guid.Full, Name, stack.Guid.Full, stack.Name, amount);
                Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, "Split amount not valid!")); // Custom error message
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, stackId));
                return;
            }

            if (IsTrading && stack.IsBeingTradedOrContainsItemBeingTraded(ItemsInTradeWindow))
            {
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, stackId, WeenieError.TradeItemBeingTraded));
                return;
            }

            if (stack.IsAttunedOrContainsAttuned)
            {
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, stackId));
                return;
            }

            if (!stack.ValidLocations.HasValue || stack.ValidLocations == EquipMask.None)
            {
                log.WarnFormat("Player 0x{0:X8}:{1} tried to wield item 0x{2:X8}:{3} to {4} (0x{4:X}), not in item's validlocatiions {5} (0x{5:X}).", Guid.Full, Name, stack.Guid.Full, stack.Name, wieldedLocation, stack.ValidLocations ?? 0);
                Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.InvalidInventoryLocation));
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, stackId));
                return;
            }

            if (stackRootOwner != this) // Movement is between the player and the world
            {
                if (stackRootOwner is Vendor)
                {
                    Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, "You cannot merge from vendor")); // Custom error message
                    Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, stackId));
                    return;
                }

                WorldObject moveToObject;

                moveToObject = stackRootOwner ?? stack;

                var stackOriginalContainer = stack.ContainerId;

                CreateMoveToChain(moveToObject, (success) =>
                {
                    if (CurrentLandblock == null) // Maybe we were teleported as we were motioning to split the item
                    {
                        Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, stackId, WeenieError.ActionCancelled));
                        return;
                    }

                    if (!success)
                    {
                        Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, stackId, WeenieError.ActionCancelled));
                        return;
                    }

                    // We make sure the stack is still valid. It could have changed during our movement
                    if (stackOriginalContainer != stack.ContainerId || stack.StackSize <= amount)
                    {
                        log.DebugFormat("Player 0x{0:X8}:{1} tried to split an item that's no longer valid 0x{2:X8}:{3}.", Guid.Full, Name, stack.Guid.Full, stack.Name);
                        Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, "Split failed!")); // Custom error message
                        Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, stackId, WeenieError.ActionCancelled));
                        return;
                    }

                    StartPickup();

                    var pickupMotion = GetPickupMotion(moveToObject);
                    var pickupChain = AddPickupChainToMoveToChain(pickupMotion);

                    pickupChain.AddAction(this, () =>
                    {
                        // We make sure the stack is still valid. It could have changed during our pickup animation
                        if (stackOriginalContainer != stack.ContainerId || stack.StackSize <= amount)
                        {
                            log.DebugFormat("Player 0x{0:X8}:{1} tried to split an item that's no longer valid 0x{2:X8}:{3}.", Guid.Full, Name, stack.Guid.Full, stack.Name);
                            Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, "Split failed!")); // Custom error message
                            Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, stackId, WeenieError.ActionCancelled));
                            EnqueuePickupDone(pickupMotion);
                            return;
                        }

                        if (!AdjustStack(stack, -amount, stackFoundInContainer, stackRootOwner))
                        {
                            Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, stackId, WeenieError.ActionCancelled));
                            return;
                        }

                        if (stackRootOwner == null)
                            EnqueueBroadcast(new GameMessageSetStackSize(stack));
                        else
                            Session.Network.EnqueueSend(new GameMessageSetStackSize(stack));

                        var newStack = WorldObjectFactory.CreateNewWorldObject(stack.WeenieClassId);

                        if (newStack == null)
                        {
                            // this should never happen under normal circumstances,
                            // but can happen if the player has an item in their inventory that is no longer in the world database
                            Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, stackId, WeenieError.ActionCancelled));
                            EnqueuePickupDone(pickupMotion);
                            return;
                        }

                        newStack.SetStackSize(amount);

                        if (DoHandleActionGetAndWieldItem(newStack, stackFoundInContainer, stackRootOwner, false, wieldedLocation, true))
                        {
                            Session.Network.EnqueueSend(new GameMessagePrivateUpdatePropertyInt(this, PropertyInt.EncumbranceVal, EncumbranceVal ?? 0));

                            if (stack.WeenieType == WeenieType.Coin)
                                UpdateCoinValue();

                            EnqueueBroadcast(new GameMessageSound(Guid, Sound.PickUpItem));

                            TrackObject(newStack);
                        }
                        else
                        {
                            // restore original stack
                            if (AdjustStack(stack, amount, stackFoundInContainer, stackRootOwner))
                            {
                                Session.Network.EnqueueSend(new GameMessagePrivateUpdatePropertyInt(this, PropertyInt.EncumbranceVal, EncumbranceVal ?? 0));

                                if (stack.WeenieType == WeenieType.Coin)
                                    UpdateCoinValue();

                                if (stackRootOwner == null)
                                    EnqueueBroadcast(new GameMessageSetStackSize(stack));
                                else
                                    Session.Network.EnqueueSend(new GameMessageSetStackSize(stack));
                            }
                            else
                                log.WarnFormat("Partial stack 0x{0:X8}:{1} for player {2} lost from HandleActionStackableSplitToWield failure.", stack.Guid.Full, stack.Name, Name);

                            newStack.Destroy();
                        }
                        EnqueuePickupDone(pickupMotion);
                    });

                    pickupChain.EnqueueChain();

                }, null, false);    // if player is within UseRadius of moveToTarget, do not perform rotation
            }
            else // This is a self-contained movement
            {
                if (!AdjustStack(stack, -amount, stackFoundInContainer, stackRootOwner))
                {
                    Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, stackId, WeenieError.ActionCancelled));
                    return;
                }

                Session.Network.EnqueueSend(new GameMessageSetStackSize(stack));

                var newStack = WorldObjectFactory.CreateNewWorldObject(stack.WeenieClassId);

                if (newStack == null)
                {
                    // this should never happen under normal circumstances,
                    // but can happen if the player has an item in their inventory that is no longer in the world database
                    Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, stackId));
                    return;
                }

                newStack.SetStackSize(amount);

                if (!DoHandleActionGetAndWieldItem(newStack, stackFoundInContainer, stackRootOwner, false, wieldedLocation, true))
                {
                    // restore original stack
                    if (AdjustStack(stack, amount, stackFoundInContainer, stackRootOwner))
                    {
                        Session.Network.EnqueueSend(new GameMessagePrivateUpdatePropertyInt(this, PropertyInt.EncumbranceVal, EncumbranceVal ?? 0));

                        if (stack.WeenieType == WeenieType.Coin)
                            UpdateCoinValue();

                        Session.Network.EnqueueSend(new GameMessageSetStackSize(stack));
                    }
                    else
                        log.WarnFormat("Partial stack 0x{0:X8}:{1} for player {2} lost from HandleActionStackableSplitToWield failure.", stack.Guid.Full, stack.Name, Name);

                    newStack.Destroy();
                }
                else
                {
                    TrackObject(newStack);
                }
            }
        }

        /// <summary>
        /// This method processes the Game Action (F7B1) Stackable Merge (0x0054)
        /// This is raised when we:
        /// - try to merge two stacks stack in the same container
        /// - try to merge two stacks stack in different container
        /// - try to merge a stack from the landblock into a container
        /// - try to split a stack into a different container that has a stack that can support a merge
        /// </summary>
        public void HandleActionStackableMerge(uint mergeFromGuid, uint mergeToGuid, int amount)
        {
            //Console.WriteLine($"HandleActionStackableMerge({mergeFromGuid:X8}, {mergeToGuid:X8}, {amount})");

            if (IsBusy || Teleporting || suicideInProgress)
            {
                Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.YoureTooBusy));
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, mergeFromGuid));
                return;
            }

            if (amount <= 0)
            {
                log.WarnFormat("Player 0x{0}:{1} tried to merge item with invalid amount ({3}) 0x{2:X8}.", Guid.Full, Name, mergeFromGuid, amount);
                Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, "Merge amount not valid!")); // Custom error message
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, mergeFromGuid));
                return;
            }

            var sourceStack = FindObject(mergeFromGuid, SearchLocations.LocationsICanMove, out _, out var sourceStackRootOwner, out _);
            var targetStack = FindObject(mergeToGuid, SearchLocations.LocationsICanMove, out _, out var targetStackRootOwner, out _);

            if (sourceStack == null)
            {
                Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, "Source stack not found!")); // Custom error message
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, mergeFromGuid));
                return;
            }

            if (!sourceStack.Guid.IsDynamic() || sourceStack.Stuck)
            {
                log.WarnFormat("Player 0x{0:X8}:{1} tried to move item 0x{2:X8}:{3}.", Guid.Full, Name, sourceStack.Guid.Full, sourceStack.Name);
                Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.Stuck));
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, mergeFromGuid));
                return;
            }

            if (sourceStackRootOwner != this && targetStackRootOwner == this && !HasEnoughBurdenToAddToInventory(sourceStack))
            {
                Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, "You are too encumbered to carry that!"));
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, mergeFromGuid));
                return;
            }

            if (targetStack == null)
            {
                Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, "Target stack not found!")); // Custom error message
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, mergeFromGuid));
                return;
            }

            // Unconditional, and two-sided on BOTH the root owner and the direct object. Nested inside
            // the player-to-world classification below, the root-owner half is skipped whenever BOTH
            // stacks are vendor-rooted, and the partial-merge arm then credits a real stored vault biota
            // from a disposable ledger display that costs the ledger nothing. The target test is equally
            // load-bearing in the opposite direction: merging a player's own stack INTO a display object
            // destroys those units on the next RebuildView.
            //
            // The two direct-object tests are what the split and put-item guards already carry as
            // `container is Vendor`, for the same reason: FindObject's Landblock arm returns the object
            // without assigning rootOwner, so a client naming the vendor's own guid arrives here with a
            // null root owner and passes every root-owner test. Merge had no such disjunct, so that case
            // was refused only eight checks later by `targetIsStackable` - a Vendor is a Creature and so
            // a Container, never a Stackable - which is precisely the "closed by a check that exists for
            // a different reason" shape this guard was hoisted to eliminate.
            if (sourceStackRootOwner is Vendor || targetStackRootOwner is Vendor || sourceStack is Vendor || targetStack is Vendor)
            {
                Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, "You cannot merge that."));
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, sourceStack.Guid.Full));
                return;
            }

            if (!targetStack.Guid.IsDynamic() || targetStack.Stuck)
            {
                log.WarnFormat("Player 0x{0:X8}:{1} tried to move item 0x{2:X8}:{3}.", Guid.Full, Name, targetStack.Guid.Full, targetStack.Name);
                Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.Stuck));
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, mergeToGuid));
                return;
            }

            if (targetStackRootOwner is Corpse)
            {
                Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, $"You cannot put {sourceStack.Name} in that.")); // Custom error message
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, mergeFromGuid));
                return;
            }

            var sourceIsStackable = sourceStack is Stackable;
            var targetIsStackable = targetStack is Stackable;
            if (!sourceIsStackable || !targetIsStackable)
            {
                Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, "You cannot merge those items!")); // Custom error message
                if (!sourceIsStackable)
                {
                    log.WarnFormat("Player 0x{0:X8}:{1} tried to merge an item 0x{2:X8}:{3} that is not stackable.", Guid.Full, Name, sourceStack.Guid.Full, sourceStack.Name);
                    Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, mergeFromGuid));
                }
                else
                {
                    log.WarnFormat("Player 0x{0:X8}:{1} tried to merge an item 0x{2:X8}:{3} that is not stackable.", Guid.Full, Name, targetStack.Guid.Full, targetStack.Name);
                    Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, mergeToGuid));
                }
                return;
            }

            if (sourceStack.WeenieClassId != targetStack.WeenieClassId)
            {
                log.WarnFormat("Player 0x{0:X8}:{1} tried to merge different items 0x{2:X8}:{3} and 0x{4:X8}:{5}.", Guid.Full, Name, sourceStack.Guid.Full, sourceStack.Name, targetStack.Guid.Full, targetStack.Name);
                Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, "Stacks not compatible!")); // Custom error message
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, mergeFromGuid, WeenieError.YouCannotMergeDifferentStacks));
                return;
            }

            if (sourceStack.StackSize == null || sourceStack.StackSize == 0)
            {
                log.WarnFormat("Player 0x{0:X8}:{1} tried to merge invalid source item 0x{2:X8}:{3}.", Guid.Full, Name, sourceStack.Guid.Full, sourceStack.Name);
                Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, "Stack not valid!")); // Custom error message
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, mergeFromGuid));
                return;
            }

            if (targetStack.StackSize == null || targetStack.StackSize == 0 || targetStack.StackSize == targetStack.MaxStackSize)
            {
                log.WarnFormat("Player 0x{0:X8}:{1} tried to merge invalid target item 0x{2:X8}:{3}.", Guid.Full, Name, targetStack.Guid.Full, targetStack.Name);
                Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, "Target not valid!")); // Custom error message
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, mergeFromGuid));
                return;
            }

            if (sourceStack.StackSize < amount)
            {
                log.WarnFormat("Player 0x{0}:{1} tried to merge item with invalid amount ({4}) 0x{2:X8}:{3}.", Guid.Full, Name, sourceStack.Guid.Full, sourceStack.Name, amount);
                Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, "Merge amount not valid!")); // Custom error message
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, mergeFromGuid));
                return;
            }

            if (targetStackRootOwner == this && !CanMergeToInventory(sourceStack, targetStack, amount))
            {
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, sourceStack.Guid.Full, WeenieError.None));
                return;
            }

            if (IsTrading)
            {
                if (sourceStack.IsBeingTradedOrContainsItemBeingTraded(ItemsInTradeWindow))
                {
                    Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, mergeFromGuid, WeenieError.TradeItemBeingTraded));
                    return;
                }
                if (targetStack.IsBeingTradedOrContainsItemBeingTraded(ItemsInTradeWindow))
                {
                    Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, mergeToGuid, WeenieError.TradeItemBeingTraded));
                    return;
                }
            }

            if (sourceStack.IsAttunedOrContainsAttuned && sourceStackRootOwner == this && targetStackRootOwner != this)
            {
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, sourceStack.Guid.Full));
                return;
            }

            if ((sourceStackRootOwner == this && targetStackRootOwner != this)  || (sourceStackRootOwner != this && targetStackRootOwner == this)) // Movement is between the player and the world
            {
                WorldObject moveToObject;

                if (sourceStackRootOwner == this)
                    moveToObject = targetStackRootOwner ?? targetStack;
                else
                    moveToObject = sourceStackRootOwner ?? sourceStack;

                var sourceStackOriginalContainer = sourceStack.ContainerId;

                CreateMoveToChain(moveToObject, (success) =>
                {
                    if (CurrentLandblock == null) // Maybe we were teleported as we were motioning to split the item
                    {
                        Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, mergeFromGuid, WeenieError.ActionCancelled));
                        return;
                    }

                    if (!success)
                    {
                        Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, mergeFromGuid, WeenieError.ActionCancelled));
                        return;
                    }

                    // We make sure the stack is still valid. It could have changed during our movement
                    if (sourceStackOriginalContainer != sourceStack.ContainerId || sourceStack.StackSize < amount)
                    {
                        log.DebugFormat("Player 0x{0}:{1} tried to merge an item that's no longer valid 0x{2:X8}:{3}.", Guid.Full, Name, sourceStack.Guid.Full, sourceStack.Name);
                        Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, "Merge Failed!")); // Custom error message
                        Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, mergeFromGuid, WeenieError.ActionCancelled));
                        return;
                    }

                    StartPickup();

                    var pickupMotion = GetPickupMotion(moveToObject);
                    var pickupChain = AddPickupChainToMoveToChain(pickupMotion);

                    pickupChain.AddAction(this, () =>
                    {
                        // We make sure the stack is still valid. It could have changed during our pickup animation
                        if (sourceStackOriginalContainer != sourceStack.ContainerId || sourceStack.StackSize < amount)
                        {
                            log.DebugFormat("Player 0x{0}:{1} tried to merge an item that's no longer valid 0x{2:X8}:{3}.", Guid.Full, Name, sourceStack.Guid.Full, sourceStack.Name);
                            Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, "Merge Failed!")); // Custom error message
                            Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, mergeFromGuid, WeenieError.ActionCancelled));
                            EnqueuePickupDone(pickupMotion);
                            return;
                        }

                        if (DoHandleActionStackableMerge(sourceStack, targetStack, amount))
                        {
                            // If the client used the R key to merge a partial stack from the landscape, it also tries to add the "ghosted" item of the picked up stack to the inventory as well.
                            if (sourceStackRootOwner != this && sourceStack.StackSize > 0)
                                Session.Network.EnqueueSend(new GameMessageCreateObject(sourceStack));

                            Session.Network.EnqueueSend(new GameMessagePrivateUpdatePropertyInt(this, PropertyInt.EncumbranceVal, EncumbranceVal ?? 0));

                            if (sourceStack.WeenieType == WeenieType.Coin)
                                UpdateCoinValue();

                            if (sourceStackRootOwner == this)
                                EnqueueBroadcast(new GameMessageSound(Guid, Sound.DropItem));
                            else if (targetStackRootOwner == this)
                                EnqueueBroadcast(new GameMessageSound(Guid, Sound.PickUpItem));
                        }
                        EnqueuePickupDone(pickupMotion);
                    });

                    pickupChain.EnqueueChain();

                }, null, false);    // if player is within UseRadius of moveToTarget, do not perform rotation

            }
            else // This is a self-contained movement
            {
                DoHandleActionStackableMerge(sourceStack, targetStack, amount);
            }
        }

        private bool DoHandleActionStackableMerge(WorldObject sourceStack, WorldObject targetStack, int amount)
        {
            //Console.WriteLine($"DoHandleActionStackableMerge({sourceStack?.Name}, {targetStack?.Name}, {amount})");

            var previousSourceStackCheck = sourceStack;
            //var previousTargetStackCheck = targetStack;

            sourceStack = FindObject(sourceStack.Guid, SearchLocations.LocationsICanMove, out _, out var sourceStackRootOwner, out _);
            targetStack = FindObject(targetStack.Guid, SearchLocations.LocationsICanMove, out var targetStackFoundInContainer, out var targetStackRootOwner, out _);

            if (sourceStack == null || targetStack == null)
            {
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, previousSourceStackCheck.Guid.Full, WeenieError.None));
                return false;
            }

            if (targetStack == null || targetStack.MaxStackSize < targetStack.StackSize + amount)
            {
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, previousSourceStackCheck.Guid.Full));
                return false;
            }

            if (amount == sourceStack.StackSize && sourceStack.StackSize + targetStack.StackSize <= targetStack.MaxStackSize) // The merge will consume the entire source stack
            {
                Session.Network.EnqueueSend(new GameMessageInventoryRemoveObject(sourceStack));

                if (sourceStackRootOwner != null) // item is contained and not on a landblock
                {
                    var sourceStackRootPlayer = sourceStackRootOwner as Player;

                    if (sourceStackRootOwner.TryRemoveFromInventory(sourceStack.Guid, out var stackToDestroy, true) ||
                        sourceStackRootPlayer != null && sourceStackRootPlayer.TryDequipObjectWithNetworking(sourceStack.Guid, out stackToDestroy, DequipObjectAction.DequipToPack))  // test case: merge equipped phials with another stack in inventory
                    {
                        stackToDestroy?.Destroy();
                    }
                    else
                    {
                        Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, previousSourceStackCheck.Guid.Full));
                        return false;
                    }
                }
                else // item is on the landblock and not contained
                    sourceStack.Destroy();


                if (!AdjustStack(targetStack, amount, targetStackFoundInContainer, targetStackRootOwner))
                    return false;

                if (targetStack.CurrentLandblock != null)
                    targetStack.EnqueueBroadcast(new GameMessageSetStackSize(targetStack));
                else
                    Session.Network.EnqueueSend(new GameMessageSetStackSize(targetStack));
            }
            else // The merge will reduce the size of the source stack
            {
                previousSourceStackCheck = sourceStack;
                //previousTargetStackCheck = targetStack;
                sourceStack = FindObject(sourceStack.Guid, SearchLocations.LocationsICanMove, out var sourceStackFoundInContainer, out sourceStackRootOwner, out _);
                targetStack = FindObject(targetStack.Guid, SearchLocations.LocationsICanMove, out targetStackFoundInContainer, out targetStackRootOwner, out _);

                if (sourceStack == null || sourceStack.StackSize < amount)
                {
                    Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, previousSourceStackCheck.Guid.Full));
                    return false;
                }

                if (targetStack == null || targetStack.MaxStackSize < targetStack.StackSize + amount)
                {
                    Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, previousSourceStackCheck.Guid.Full));
                    return false;
                }

                if (!AdjustStack(sourceStack, -amount, sourceStackFoundInContainer, sourceStackRootOwner))
                {
                    Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, previousSourceStackCheck.Guid.Full));
                    return false;
                }

                if (sourceStack.CurrentLandblock != null)
                    sourceStack.EnqueueBroadcast(new GameMessageSetStackSize(sourceStack));
                else
                    Session.Network.EnqueueSend(new GameMessageSetStackSize(sourceStack));

                if (!AdjustStack(targetStack, amount, targetStackFoundInContainer, targetStackRootOwner))
                    return false;

                if (targetStack.CurrentLandblock != null)
                    targetStack.EnqueueBroadcast(new GameMessageSetStackSize(targetStack));
                else
                    Session.Network.EnqueueSend(new GameMessageSetStackSize(targetStack));
            }

            var itemFoundOnCorpse = sourceStackRootOwner is Corpse;

            var isFromAPlayerCorpse = false;
            if (itemFoundOnCorpse && sourceStackRootOwner.Level > 0)
                isFromAPlayerCorpse = true;

            if (isFromAPlayerCorpse)
            {
                log.Info($"[CORPSE] {Name} (0x{Guid}) merged {amount:N0} {(sourceStack.IsDestroyed ? $"which resulted in the destruction" : $"leaving behind {sourceStack.StackSize:N0}")} of {sourceStack.Name} (0x{sourceStack.Guid}) to {targetStack.Name} (0x{targetStack.Guid}) from {sourceStackRootOwner.Name} (0x{sourceStackRootOwner.Guid})");
                targetStack.SaveBiotaToDatabase();
            }

            return true;
        }


        // =============================================
        // Game Action Handlers - Inventory Give/Receive 
        // =============================================

        /// <summary>
        /// This method processes the Game Action (F7B1) Give Object Request (0x00CD)
        /// This is raised when we:
        /// - try to give an object to another player
        /// - try to give an object to an NPC
        /// </summary>
        public void HandleActionGiveObjectRequest(uint targetGuid, uint itemGuid, int amount)
        {
            if (IsBusy || Teleporting || suicideInProgress)
            {
                Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.YoureTooBusy));
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, itemGuid));
                return;
            }

            if (amount <= 0)
            {
                log.WarnFormat("Player 0x{0:X8}:{1} tried to give item with invalid amount ({3}) 0x{2:X8}.", Guid.Full, Name, itemGuid, amount);
                Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, "Give amount not valid!")); // Custom error message
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, itemGuid));
                return;
            }

            var target = FindObject(targetGuid, SearchLocations.Landblock, out _, out _, out _) as Container;
            var item = FindObject(itemGuid, SearchLocations.MyInventory | SearchLocations.MyEquippedItems, out var itemFoundInContainer, out var itemRootOwner, out var itemWasEquipped);

            if (target == null)
            {
                Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, "Target not found!")); // Custom error message
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, itemGuid));
                return;
            }

            if (item == null)
            {
                Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, "Item not found!")); // Custom error message
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, itemGuid));
                return;
            }

            if (item.StackSize < amount)
            {
                log.WarnFormat("Player 0x{0:X8}:{1} tried to give item with invalid amount ({4}) 0x{2:X8}:{3}.", Guid.Full, Name, item.Guid.Full, item.Name, amount);
                Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, "Give amount not valid!")); // Custom error message
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, itemGuid));
                return;
            }

            if (IsTrading && item.IsBeingTradedOrContainsItemBeingTraded(ItemsInTradeWindow))
            {
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, item.Guid.Full, WeenieError.TradeItemBeingTraded));
                return;
            }

            CreateMoveToChain(target, (success) =>
            {
                if (CurrentLandblock == null) // Maybe we were teleported as we were motioning to pick up the item
                {
                    Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, itemGuid, WeenieError.ActionCancelled));
                    return;
                }

                if (!success)
                {
                    Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, itemGuid, WeenieError.ActionCancelled));
                    return;
                }

                if (target is Player targetAsPlayer)
                    GiveObjectToPlayer(targetAsPlayer, item, itemFoundInContainer, itemRootOwner, itemWasEquipped, amount);
                else
                    GiveObjectToNPC(target, item, itemFoundInContainer, itemRootOwner, itemWasEquipped, amount);

            });    // if player is within UseRadius of moveToTarget, perform rotation?
        }

        private void GiveObjectToPlayer(Player target, WorldObject item, Container itemFoundInContainer, Container itemRootOwner, bool itemWasEquipped, int amount)
        {
            if (item.IsAttunedOrContainsAttuned)
            {
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, item.Guid.Full, WeenieError.AttunedItem));
                return;
            }

            if (item is PetDevice petDevice && petDevice.Pet is not null)
            {
                Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, "You must unsummon your pet before you can transfer this item!"));
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, item.Guid.Full, WeenieError.AttunedItem));
                return;
            }

            if (IsTrading && item.IsBeingTradedOrContainsItemBeingTraded(ItemsInTradeWindow))
            {
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, item.Guid.Full, WeenieError.TradeItemBeingTraded));
                return;
            }

            if (target.IsOlthoiPlayer || IsOlthoiPlayer)
            {
                Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, "Olthoi cannot trade items with other players!")); // Custom error message
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, item.Guid.Full));
                return;
            }

            if ((target.Character.CharacterOptions1 & (int)CharacterOptions1.AllowGive) != (int)CharacterOptions1.AllowGive)
            {
                Session.Network.EnqueueSend(new GameEventWeenieErrorWithString(Session, WeenieErrorWithString._IsNotAcceptingGiftsRightNow, target.Name));
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, item.Guid.Full));
                var msg = new GameMessageSystemChat($"{Name} tries to give you {(item.StackSize > 1 ? $"{item.StackSize} " : "")}{item.GetNameWithMaterial(item.StackSize)}.", ChatMessageType.Broadcast);
                target.Session.Network.EnqueueSend(msg);
                return;
            }

            if (target.IsLoggingOut)
            {
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, item.Guid.Full, WeenieError.None));
                return;
            }

            if (target.IsBusy)
            {
                Session.Network.EnqueueSend(new GameEventWeenieErrorWithString(Session, WeenieErrorWithString._IsTooBusyToAcceptGifts, target.Name));
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, item.Guid.Full));
                var msg = new GameMessageSystemChat($"{Name} tries to give you {(item.StackSize > 1 ? $"{item.StackSize} " : "")}{item.GetNameWithMaterial(item.StackSize)}.", ChatMessageType.Broadcast);
                target.Session.Network.EnqueueSend(msg);
                return;
            }

            // TODO: this seems a bit backwards here...
            // the item is removed from the source player's inventory,
            // and it tries to add to target player's inventory (which does the slot/burden checks, and can also independently fail)
            // these slot/burden checks should be done beforehand, before it tries to remove the item from source player

            if (!target.CanAddToInventory(item))
            {
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, item.Guid.Full, WeenieError.None));
                return;
            }

            if (item.IsUniqueOrContainsUnique && !target.CheckUniques(item, this))
            {
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, item.Guid.Full, WeenieError.None));
                return;
            }

            if (!RemoveItemForGive(item, itemFoundInContainer, itemWasEquipped, itemRootOwner, amount, out WorldObject itemToGive))
                return;

            var actionChain = new ActionChain();
            if (itemWasEquipped)
                actionChain.AddDelaySeconds(0.5f);

            // This is a hack because our Player_Tracking->RemoveTrackedEquippedObject() is doing GameMessageDeleteObject, not GameMessagePickupEvent
            // Without this, when you give an equipped item to a player, the player won't see it appear in their inventory
            // A bug still exists in the following scenario:
            // Player A equips weapon, gives weapon (while equipped) to player B.
            // Player B then gives weapon back to A. Player B is now bugged. The fix is to fix RemoveTrackedEquippedObject

            actionChain.AddAction(this, () =>
            {
                if (!target.TryCreateInInventoryWithNetworking(itemToGive, out _))
                {
                    Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, "TryCreateInInventoryWithNetworking failed!")); // Custom error message

                    // todo: So the item isn't lost, we should try to put the item in the players inventory, or if that's full, on the landblock.

                    Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, itemToGive.Guid.Full));

                    if (!TryCreateInInventoryWithNetworking(itemToGive))
                        log.WarnFormat("Item 0x{0:X8}:{1} for player {2} lost from GiveObjecttoPlayer failure.", item.Guid.Full, item.Name, Name);

                    return;
                }

                // Analytics (Tier-2): player-to-player give succeeded; record the item flow.
                AnalyticsManager.RecordGive(this, target, itemToGive);

                if (item == itemToGive)
                    Session.Network.EnqueueSend(new GameEventItemServerSaysContainId(Session, item, target));

                var stackSize = itemToGive.StackSize ?? 1;

                var stackMsg = stackSize != 1 ? $"{stackSize:N0} " : "";
                var itemName = itemToGive.GetNameWithMaterial(stackSize);

                Session.Network.EnqueueSend(new GameMessageSystemChat($"You give {target.Name} {stackMsg}{itemName}.", ChatMessageType.Broadcast));

                // send DO to source player if not splitting a stack
                if (item == itemToGive)
                    Session.Network.EnqueueSend(new GameMessageDeleteObject(item));

                target.Session.Network.EnqueueSend(new GameMessageSystemChat($"{Name} gives you {stackMsg}{itemName}.", ChatMessageType.Broadcast));

                target.EnqueueBroadcast(new GameMessageSound(target.Guid, Sound.ReceiveItem));
            });

            actionChain.EnqueueChain();
        }

        private void GiveObjectToNPC(WorldObject target, WorldObject item, Container itemFoundInContainer, Container itemRootOwner, bool itemWasEquipped, int amount)
        {
            if (target == null || item == null) return;

            if (item.Name == "IOU" && item.WeenieType == WeenieType.Book && target.Name == "Town Crier")
            {
                HandleIOUTurnIn(target, item);
                return;
            }

            if (item is PetDevice petDevice && petDevice.Pet is not null)
            {
                Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, "You must unsummon your pet before you can transfer this item!"));
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, item.Guid.Full, WeenieError.AttunedItem));
                return;
            }

            if (IsOlthoiPlayer && target.CreatureType != ACE.Entity.Enum.CreatureType.Olthoi)
            {
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, item.Guid.Full));
                Session.Network.EnqueueSend(new GameEventWeenieErrorWithString(Session, WeenieErrorWithString._CowersFromYou, target.Name));
                return;
            }

            if (target.EmoteManager.IsBusy)
            {
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, item.Guid.Full));
                Session.Network.EnqueueSend(new GameEventWeenieErrorWithString(Session, WeenieErrorWithString.AiRefuseItemDuringEmote, target.Name));
                return;
            }

            if (IsTrading && item.IsBeingTradedOrContainsItemBeingTraded(ItemsInTradeWindow))
            {
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, item.Guid.Full, WeenieError.TradeItemBeingTraded));
                return;
            }

            // RefireStations attendants (2026-08-18): flavor-only NPCs standing beside each station that
            // refuse ANY given item and hand it straight back. A data-only Give/Refuse emote row cannot
            // express this - HasGiveOrRefuseEmoteForItem/EmoteManager.GetEmoteSet only ever match a row by
            // the SPECIFIC given item's own wcid, never a wildcard - so this is a code intercept, same
            // pattern as the three stations below, checked first since it never proceeds to any of them.
            if (target.GetProperty(PropertyBool.RefireStationAttendant) == true)
            {
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, item.Guid.Full, WeenieError.TradeAiRefuseEmote));
                Session.Network.EnqueueSend(new GameMessageSystemChat($"You allow {target.Name} to examine your {item.NameWithMaterial}.", ChatMessageType.Broadcast));
                RefireStations.RefireStationCommon.SendStationTell(this, target, "Not me - hand that to the station behind me.");
                return;
            }

            // Refire stations (WaffleACE, DreamWeave): give an item to any RefireStations-marked NPC and it
            // offers a paid property reroll instead of accepting the gift. Intercepted here, BEFORE the
            // AiAcceptEverything/emote give logic below, because none of these stations actually take the
            // item - it stays in the giving player's inventory throughout (confirmation, charge and reroll all
            // happen in place). ONE intercept, dispatching on marker bool, over FIVE stations - see
            // ACE.Server/RefireStations/.
            if (target.GetProperty(PropertyBool.WorkmanshipRefireForge) == true)
            {
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, item.Guid.Full));
                RefireStations.WorkmanshipReforgeStation.HandleGive(this, target, item);
                return;
            }

            if (target.GetProperty(PropertyBool.ArcaneAlignmentTable) == true)
            {
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, item.Guid.Full));
                RefireStations.ArcaneAlignmentStation.HandleGive(this, target, item);
                return;
            }

            if (target.GetProperty(PropertyBool.DefenseRequirementReforge) == true)
            {
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, item.Guid.Full));
                RefireStations.DefenseReforgeStation.HandleGive(this, target, item);
                return;
            }

            if (target.GetProperty(PropertyBool.CoverageLoom) == true)
            {
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, item.Guid.Full));
                RefireStations.CoverageLoomStation.HandleGive(this, target, item);
                return;
            }

            if (target.GetProperty(PropertyBool.BrewersCauldron) == true)
            {
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, item.Guid.Full));
                RefireStations.BrewersCauldronStation.HandleGive(this, target, item);
                return;
            }

            if (target.GetProperty(PropertyBool.SalvageForgeStation) == true)
            {
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, item.Guid.Full));
                RefireStations.SalvageForgeStation.HandleGive(this, target, item);
                return;
            }

            // Threads Fragment Press (wcid 1003614): give it a Thread Gem and it takes the gem apart
            // into a Raw Fragment. An unbound gem goes back for free and unconfirmed; the giver's OWN bound
            // gem is taken too, and if the run it opened is still live the press ENDS that run after a
            // confirmation (this is the only player-facing way out of a run). Someone else's bound gem is
            // refused. Same shape as the stations above, and the ServerSaveFailed does the same job - it
            // bounces the gem back into the pack first, which is exactly why the press can take an item where
            // the refire stations cannot: HandleGive then consumes it from inventory normally. The other half
            // of the press (use a fragment ON it) is a use-on, not a give, and lives in
            // Gem.HandleActionUseOnTarget -> RawFragment.UseObjectOnTarget.
            if (target.GetProperty(PropertyBool.DungeonGemPress) == true)
            {
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, item.Guid.Full));
                ThreadDungeons.FragmentPressStation.HandleGive(this, target, item);
                return;
            }

            // Custom Dreamweave Augmentation brokers (WaffleACE, DreamWeave, 2026-09-05): Fi / Bo / Nacci
            // trade Blank Augmentation Gems for an upgraded augmentation gem at a Fibonacci price. Same
            // shape as the Fragment Press above - the ServerSaveFailed bounces the given gem back into the
            // pack first, and CustomAugBroker then consumes the whole price from inventory by wcid after a
            // confirmation. ONE intercept over all three brokers; which augmentation a given NPC sells
            // comes from its own PropertyInt.AugmentationStat. A data-only Give emote cannot express a
            // price that depends on how many the buyer already owns.
            if (target.GetProperty(PropertyBool.CustomAugBroker) == true)
            {
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, item.Guid.Full));
                CustomAugBroker.HandleGive(this, target, item);
                return;
            }

            var acceptAll = target.AiAcceptEverything && !item.IsStickyAttunedOrContainsStickyAttuned;

            if (target.HasGiveOrRefuseEmoteForItem(item, out var emoteResult) || acceptAll)
            {
                if (acceptAll || (emoteResult.Category == EmoteCategory.Give && target.AllowGive))
                {
                    // Reward pre-flight (WaffleACE): everything below this line CONSUMES the item before a
                    // single emote row runs, so a player who cannot hold the reward loses both. Refuse up
                    // front instead. Gated by emote_give_preflight, OFF by default - while it is off this
                    // returns true without even building a forecast and the flow below is untouched.
                    if (!PreflightEmoteGive(target, item, emoteResult, acceptAll ? amount : 1))
                        return;

                    // for NPCs that accept items with EmoteCategory.Give,
                    // if stacked item, only give 1, ignoring amount indicated, unless they are AiAcceptEverything in which case, take full amount indicated
                    if (RemoveItemForGive(item, itemFoundInContainer, itemWasEquipped, itemRootOwner, acceptAll ? amount : 1, out WorldObject itemToGive))
                    {
                        if (item == itemToGive)
                            Session.Network.EnqueueSend(new GameEventItemServerSaysContainId(Session, item, target));

                        var stackSize = itemToGive.StackSize ?? 1;

                        var stackMsg = stackSize != 1 ? $"{stackSize:N0} " : "";
                        var itemName = itemToGive.GetNameWithMaterial(stackSize);

                        Session.Network.EnqueueSend(new GameMessageSystemChat($"You give {target.Name} {stackMsg}{itemName}.", ChatMessageType.Broadcast));
                        target.EnqueueBroadcast(new GameMessageSound(target.Guid, Sound.ReceiveItem));

                        target.EmoteManager.ExecuteEmoteSet(emoteResult, this);

                        itemToGive.Destroy();
                    }
                }
                else if (emoteResult.Category == EmoteCategory.Refuse)
                {
                    // Item rejected by npc
                    Session.Network.EnqueueSend(new GameMessageSystemChat($"You allow {target.Name} to examine your {item.NameWithMaterial}.", ChatMessageType.Broadcast));
                    Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, item.Guid.Full, WeenieError.TradeAiRefuseEmote));

                    target.EmoteManager.ExecuteEmoteSet(emoteResult, this);
                }
                else
                {
                    Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, item.Guid.Full));
                    Session.Network.EnqueueSend(new GameEventWeenieErrorWithString(Session, WeenieErrorWithString._IsNotAcceptingGiftsRightNow, target.Name));
                    return;
                }
            }
            else
            {
                if (item.WeenieType == WeenieType.Deed && target.AllowGive && target.AiAcceptEverything) // http://acpedia.org/wiki/Housing_FAQ#House_deeds
                {                    
                    var stackSize = item.StackSize ?? 1;

                    var stackMsg = stackSize != 1 ? $"{stackSize} " : "";
                    var itemName = item.GetNameWithMaterial(stackSize);

                    Session.Network.EnqueueSend(new GameMessageSystemChat($"You give {target.Name} {stackMsg}{itemName}.", ChatMessageType.Broadcast));
                    target.EnqueueBroadcast(new GameMessageSound(target.Guid, Sound.ReceiveItem));

                    HandleActionAbandonHouse();

                    return;
                }

                Session.Network.EnqueueSend(new GameEventWeenieErrorWithString(Session, (WeenieErrorWithString)WeenieError.TradeAiDoesntWant, target.Name));
                Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, item.Guid.Full));
            }
        }

        private void HandleIOUTurnIn(WorldObject target, WorldObject iouToTurnIn)
        {
            Session.Network.EnqueueSend(new GameMessageSystemChat($"You allow {target.Name} to examine your {iouToTurnIn.NameWithMaterial}.", ChatMessageType.Broadcast));
            Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, iouToTurnIn.Guid.Full, WeenieError.TradeAiRefuseEmote));

            if (!PropertyManager.GetBool("iou_trades").Item)
            {
                Session.Network.EnqueueSend(new GameEventTell(target, "Sorry! I'm not taking IOUs right now, but if you do wish to discard them, drop them in to the garbage barrels found at the Mana Forges in Hebian-To, Zaikhal, and Cragstone.", this, ChatMessageType.Tell));
                //Session.Network.EnqueueSend(new GameEventWeenieErrorWithString(Session, (WeenieErrorWithString)WeenieError.TradeAiDoesntWant, target.Name));
                //var barrel = WorldObjectFactory.CreateNewWorldObject("ace34726-garbagebarrel");
                //barrel.TimeToRot = 180;
                //barrel.Location = target.Location.InFrontOf(2f);
                //barrel.Location.LandblockId = new LandblockId(barrel.Location.GetCell());
                //barrel.EnterWorld();
                return;
            }

            if (iouToTurnIn is Book book && book.ScribeName == "ACEmulator" && book.ScribeAccount == "prewritten")
            {
                var page = book.GetPage(0);

                if (page != null && page.AuthorName == "ACEmulator" && page.AuthorAccount == "prewritten" && page.AuthorId == uint.MaxValue)
                {
                    var pageText = page.PageText;

                    var split = pageText.Split("\n");

                    if (split.Length > 0)
                    {
                        var success = uint.TryParse(split[0], out var wcid);

                        //Console.WriteLine($"{success} {wcid}");

                        Session.Network.EnqueueSend(new GameEventTell(target, "Ahh, an IOU! You know, I collect these for some reason. Let me see if I have something for it somewhere in my pack...", this, ChatMessageType.Tell));

                        if (success)
                        {
                            var item = WorldObjectFactory.CreateNewWorldObject(wcid);

                            if (item != null)
                            {
                                Session.Network.EnqueueSend(new GameEventTell(target, $"You're in luck! This {item.Name} was just left here the other day.", this, ChatMessageType.Tell));
                                Session.Network.EnqueueSend(new GameEventTell(target, "I'll trade it to you for this IOU.", this, ChatMessageType.Tell));
                                Session.Network.EnqueueSend(new GameMessageSystemChat($"You give {target.Name} {iouToTurnIn.Name}.", ChatMessageType.Broadcast));
                                target.EnqueueBroadcast(new GameMessageSound(target.Guid, Sound.ReceiveItem));

                                RemoveItemForGive(iouToTurnIn, null, false, null, 1, out _, true);
                                success = TryCreateInInventoryWithNetworking(item);

                                if (success)
                                {
                                    Session.Network.EnqueueSend(new GameEventTell(target, "Here you go.", this, ChatMessageType.Tell));
                                    Session.Network.EnqueueSend(new GameMessageSystemChat($"{target.Name} gives you {item.Name}.", ChatMessageType.Broadcast));
                                    target.EnqueueBroadcast(new GameMessageSound(target.Guid, Sound.ReceiveItem));

                                    if (PropertyManager.GetBool("player_receive_immediate_save").Item)
                                        RushNextPlayerSave(5);

                                    log.DebugFormat("[IOU] {0} (0x{1}) traded in a IOU (0x{2}) for {3} which became {4} (0x{5}).", Name, Guid, iouToTurnIn.Guid, wcid, item.Name, item.Guid);
                                }
                                return;
                            }
                            else
                            {
                                Session.Network.EnqueueSend(new GameEventTell(target, "Sorry, doesn't look I've got one of those yet. Check back again later.", this, ChatMessageType.Tell));
                                return;
                            }
                        }
                    }
                }
            }

            Session.Network.EnqueueSend(new GameEventTell(target, "Hmm... Something isn't quite right with this IOU. I can't seem to make out what its for. I'm sorry!", this, ChatMessageType.Tell));
        }

        private bool RemoveItemForGive(WorldObject item, Container itemFoundInContainer, bool itemWasEquipped, Container itemRootOwner, int amount, out WorldObject itemToGive, bool destroy = false)
        {
            if (item.StackSize > 1 && amount < item.StackSize) // We're splitting a stack
            {
                if (!AdjustStack(item, -amount, itemFoundInContainer, itemRootOwner))
                {
                    Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, item.Guid.Full));
                    itemToGive = null;
                    return false;
                }

                Session.Network.EnqueueSend(new GameMessageSetStackSize(item));

                var newStack = WorldObjectFactory.CreateNewWorldObject(item.WeenieClassId);

                if (newStack == null)
                {
                    // this should never happen under normal circumstances,
                    // but can happen if the player has an item in their inventory that is no longer in the world database
                    Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, item.Guid.Full));
                    itemToGive = null;
                    return false;
                }

                newStack.SetStackSize(amount);

                Session.Network.EnqueueSend(new GameMessagePrivateUpdatePropertyInt(this, PropertyInt.EncumbranceVal, EncumbranceVal ?? 0));

                if (item.WeenieType == WeenieType.Coin)
                    UpdateCoinValue();

                itemToGive = newStack;

                return true;
            }

            // We're giving the whole object
            if (itemWasEquipped)
            {
                if (!TryDequipObjectWithNetworking(item.Guid, out _, DequipObjectAction.GiveItem))
                {
                    Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, "TryDequipObjectWithNetworking failed!")); // Custom error message
                    Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, item.Guid.Full));
                    itemToGive = null;
                    return false;
                }
            }
            else
            {
                if (!TryRemoveFromInventoryWithNetworking(item.Guid, out _, RemoveFromInventoryAction.GiveItem))
                {
                    Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, "TryRemoveFromInventoryWithNetworking failed!")); // Custom error message
                    Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, item.Guid.Full));
                    itemToGive = null;
                    return false;
                }
            }

            if (destroy)
            {
                item.Destroy();
                itemToGive = null;
            }
            else
                itemToGive = item;

            return true;
        }


        // ===========================
        // Game Action Handlers - Misc
        // ===========================

        /// <summary>
        /// This method processes the Game Action (F7B1) Set Inscription (0x00BF)
        /// This is raised when we:
        /// - try to inscribe an item
        /// </summary>
        public void HandleActionSetInscription(uint itemGuid, string inscriptionText)
        {
            var item = FindObject(itemGuid, SearchLocations.MyInventory | SearchLocations.MyEquippedItems, out _, out _, out _);

            if (item == null)
            {
                if (this is Sentinel || this is Admin)
                    item = FindObject(itemGuid, SearchLocations.Everywhere, out _, out _, out _);

                if (item == null)
                {
                    log.Error("Player_Inventory HandleActionSetInscription failed");
                    return;
                }
            }

            if (item.Inscribable)
            {
                var doInscribe = false;
                if (string.IsNullOrWhiteSpace(item.ScribeAccount) && string.IsNullOrWhiteSpace(item.ScribeName))
                {
                    doInscribe = true;
                }
                else
                {
                    if (this is Sentinel || this is Admin)
                        doInscribe = true;
                    else if (item.ScribeIID.HasValue && item.ScribeIID == Guid.Full && item.ScribeName == Name && item.ScribeAccount == Account.AccountName)
                        doInscribe = true;
                    else if (item.ScribeName == Name && item.ScribeAccount == Account.AccountName)
                        doInscribe = true;
                }

                if (doInscribe)
                {
                    if (string.IsNullOrEmpty(inscriptionText))
                    {
                        item.Inscription = null;
                        item.ScribeName = null;
                        item.ScribeAccount = null;
                        item.ScribeIID = null;
                    }
                    else
                    {
                        item.Inscription = inscriptionText;
                        item.ScribeName = Name;
                        item.ScribeAccount = Account.AccountName;
                        item.ScribeIID = Guid.Full;
                    }

                    // this response was never recorded occuring from retail servers
                    // Session.Network.EnqueueSend(new GameEventInscriptionResponse(Session, item));

                    // There was no direct response from the servers for this event, client just sent it and moved on.
                }
            }
            else
            {
                // Send some cool you cannot inscribe that item message. Not sure how that was handled live, I could not find a pcap of a failed inscription. Og II
                ChatPacket.SendServerMessage(Session, "Target item cannot be inscribed.", ChatMessageType.System);
            }
        }

        // This handles a peculiar sequence sent by the client in certain scenarios
        // The client will double-send 0x19 PutItemInContainer for the same object
        // (swapping dual wield weapons, swapping ammo types in combat)

        private PutItemInContainerEvent[] Prev_PutItemInContainer = new PutItemInContainerEvent[2];
        private bool IsDoubleSend
        {
            get
            {
                if (Prev_PutItemInContainer[0] == null || Prev_PutItemInContainer[1] == null)
                    return false;

                var isDoubleSend = Prev_PutItemInContainer[0].IsDoubleSend(Prev_PutItemInContainer[1]);

                Prev_PutItemInContainer[1] = null;

                return isDoubleSend;
            }
        }

        private void OnPutItemInContainer(uint itemGuid, uint containerGuid, int placement)
        {
            Prev_PutItemInContainer[1] = Prev_PutItemInContainer[0];
            Prev_PutItemInContainer[0] = new PutItemInContainerEvent(itemGuid, containerGuid, placement);
        }
        
        /// <summary>
        /// Reward pre-flight for an NPC turn-in (WaffleACE). GiveObjectToNPC consumes the given item and
        /// runs the emote set afterwards, and the emote set is what stamps the quest and hands out the
        /// reward - so an over-burdened or full player used to lose the turn-in AND the reward, silently.
        /// This forecasts what <paramref name="emoteSet"/> could hand out, credits the space the outgoing
        /// item itself frees, and returns false when any forecast path would not fit.
        ///
        /// Returning false means the caller must return WITHOUT consuming the item, running the emote set,
        /// or destroying anything: nothing has been stamped and the item is still in the player's pack.
        ///
        /// Fails OPEN in every uncertain case - flag off, no emote set, a truncated forecast, a forecast with
        /// no rewards in it, or the check itself throwing - because refusing a give that would have worked is
        /// worse than the pre-existing behaviour this gate improves on. The exception boundary lives in
        /// <see cref="EmoteGivePreflight.Decide"/>, which is where everything that can throw is called from:
        /// this method runs inside a CreateMoveToChain callback that nothing else catches.
        /// </summary>
        private bool PreflightEmoteGive(WorldObject target, WorldObject item, PropertiesEmote emoteSet, int amountConsumed)
        {
            if (!EmoteGivePreflight.Enabled)
                return true;

            // acceptAll with no matching set: ExecuteEmoteSet(null) is a no-op, so nothing is handed out
            if (emoteSet == null || target == null || item == null)
                return true;

            var context = $"{Name} (0x{Guid}) giving {item.Name} (wcid {item.WeenieClassId}) to {target.Name} (wcid {target.WeenieClassId})";

            if (EmoteGivePreflight.Decide(emoteSet, target.Biota?.PropertiesEmote, path => CostRewardPath(path, item, amountConsumed), context, out var worst))
                return true;

            Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, item.Guid.Full, WeenieError.None));

            var message = EmoteGivePreflight.RefusalMessage(worst, target.Name);

            if (message != null)
                Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, message));

            log.Warn($"[GIVE PREFLIGHT] {context}: refused ({amountConsumed} consumed) - worst forecast path needs {worst.RequiredInventorySlots} pack slot(s), {worst.RequiredContainerSlots} container slot(s), {worst.RequiredBurden} burden");

            return false;
        }

        /// <summary>
        /// Prices ONE forecasted reward path against this player's live free slots and burden, net of the
        /// space the outgoing turn-in frees. Called once per path by <see cref="EmoteGivePreflight.Decide"/>,
        /// inside its try/catch - the ItemsToReceive construction here walks the player's containers and
        /// every <see cref="ItemsToReceive.Add"/> reaches the world database for the reward's weenie, so this
        /// is the throwing half of the gate and must not be called outside that boundary.
        /// </summary>
        private PreflightPathCost CostRewardPath(IReadOnlyList<ForecastedReward> path, WorldObject item, int amountConsumed)
        {
            var itemsToReceive = new ItemsToReceive(this);

            // the turn-in is consumed before any reward arrives, so its space is available to the reward
            itemsToReceive.CreditOutgoing(item, amountConsumed);

            var treasureRows = 0;

            foreach (var reward in path)
            {
                if (reward.IsRandomTreasure)
                    treasureRows++;
                else
                    itemsToReceive.Add(reward.WeenieClassId, reward.Amount);
            }

            if (treasureRows > 0)
                itemsToReceive.AddUnknownItemSlots(treasureRows * EmoteGivePreflight.TreasureInventorySlots);

            return PreflightPathCost.From(itemsToReceive);
        }

        public void GiveFromEmote(WorldObject emoter, uint weenieClassId, int amount = 1, int palette = 0, float shade = 0)
        {
            if (emoter is null || weenieClassId == 0)
            {
                log.Warn($"Player.GiveFromEmote: Emoter is null: {emoter is null} | weenieClassId == 0: {weenieClassId == 0}");

                if (emoter != null)
                    log.Warn($"Player.GiveFromEmote: Emoter is {emoter.Name} (0x{emoter.Guid}) | WCID: {emoter.WeenieClassId}");

                return;
            }
            var itemsToReceive = new ItemsToReceive(this);

            itemsToReceive.Add(weenieClassId, amount);

            var itemStacks = itemsToReceive.RequiredSlots;

            if (itemsToReceive.PlayerExceedsLimits)
            {
                //if (itemsToReceive.PlayerExceedsAvailableBurden)
                //    Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, "You are too encumbered to use that!"));
                //else if (itemsToReceive.PlayerOutOfInventorySlots)
                //    Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, "You do not have enough pack space to use that!"));
                //else if (itemsToReceive.PlayerOutOfContainerSlots)
                //    Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, "You do not have enough container slots to use that!"));

                // Font of Enlightenment and Rebirth tries to give you Attribute Reset Certificate.
                var itemBeingGiven = DatabaseManager.World.GetCachedWeenie(weenieClassId);
                var msg = new GameMessageSystemChat($"{emoter.Name} tries to give you {(itemStacks > 1 ? $"{itemStacks} " : "")}{(itemStacks > 1 ? itemBeingGiven.GetPluralName() : itemBeingGiven.GetName())}.", ChatMessageType.Broadcast);
                Session.Network.EnqueueSend(msg);

                return;
            }

            if (itemStacks > 0)
            {
                var remaining = amount;

                while (remaining > 0)
                {
                    var item = WorldObjectFactory.CreateNewWorldObject(weenieClassId);

                    if (item == null)
                    {
                        log.Warn($"Player.GiveFromEmote: Emoter is {emoter.Name} (0x{emoter.Guid}) | WCID: {emoter.WeenieClassId} is not able to be created.");
                        return;
                    }

                    if (item is Stackable)
                    {
                        var stackSize = Math.Min(remaining, item.MaxStackSize ?? 1);

                        item.SetStackSize(stackSize);
                        remaining -= stackSize;
                    }
                    else
                        remaining--;

                    if (palette > 0)
                        item.PaletteTemplate = palette;
                    if (shade > 0)
                        item.Shade = shade;

                    TryCreateForGive(emoter, item);
                }
            }
            else
            {
                log.Warn($"Player.GiveFromEmote: itemStacks <= 0: emoter: {emoter.Name} (0x{emoter.Guid}) - {emoter.WeenieClassId} | weenieClassId: {weenieClassId} | amount: {amount}");

                if (PropertyManager.GetBool("iou_trades").Item)
                {
                    var item = PlayerFactory.CreateIOU(weenieClassId);
                    TryCreateForGive(emoter, item);
                }
            }
        }

        public bool TryCreateForGive(WorldObject giver, WorldObject itemBeingGiven)
        {
            if (itemBeingGiven.IsUniqueOrContainsUnique && !CheckUniques(itemBeingGiven, giver))
                return false;

            if (!TryCreateInInventoryWithNetworking(itemBeingGiven))
            {
                var msg = new GameMessageSystemChat($"{giver.Name} tries to give you {(itemBeingGiven.StackSize > 1 ? $"{itemBeingGiven.StackSize} " : "")}{itemBeingGiven.GetNameWithMaterial(itemBeingGiven.StackSize)}.", ChatMessageType.Broadcast);
                Session.Network.EnqueueSend(msg);
                return false;
            }

            if (!(giver.GetProperty(PropertyBool.NpcInteractsSilently) ?? false))
            {
                var msg = new GameMessageSystemChat($"{giver.Name} gives you {(itemBeingGiven.StackSize > 1 ? $"{itemBeingGiven.StackSize} " : "")}{itemBeingGiven.GetNameWithMaterial(itemBeingGiven.StackSize)}.", ChatMessageType.Broadcast);
                Session.Network.EnqueueSend(msg);

                EnqueueBroadcast(new GameMessageSound(Guid, Sound.ReceiveItem));
            }

            if (PropertyManager.GetBool("player_receive_immediate_save").Item)
                RushNextPlayerSave(5);

            return true;
        }

        public bool CheckUniques(WorldObject obj, WorldObject giver = null)
        {
            return CheckUniques(new List<WorldObject>() { obj }, giver);
        }

        /// <summary>
        /// Verifies a player can pick up an object that is unique,
        /// or contains uniques.
        public bool CheckUniques(List<WorldObject> objs, WorldObject giver = null)
        {
            var uniqueObjects = new List<WorldObject>();

            foreach (var obj in objs)
                uniqueObjects.AddRange(obj.GetUniqueObjects());

            // build dictionary of wcid => count
            var uniqueTable = new UniqueTable(uniqueObjects);

            // ensure player can add this obj to their inventory
            foreach (var kvp in uniqueTable.Entries)
            {
                var wcid = kvp.Key;
                var entry = kvp.Value;

                var current = GetNumInventoryItemsOfWCID(wcid);

                if (current + entry.Count > entry.Max)
                {
                    var wo = uniqueObjects.FirstOrDefault(i => i.WeenieClassId == wcid);

                    var itemName = entry.Max == 1 ? wo.Name : wo.GetPluralName();

                    var msgTarget = giver is Player player ? player : this;

                    var name = msgTarget == this ? "You" : msgTarget.Name;
                    var msg = $"{name} cannot pick up more than {entry.Max:N0} {itemName}!";

                    msgTarget.Session.Network.EnqueueSend(new GameMessageSystemChat(msg, ChatMessageType.Broadcast));

                    return false;
                }
            }
            return true;
        }

        public void AuditEquippedItems()
        {
            // fixes any 'invisible' equipped items, where CurrentWieldedLocation is None
            // not sure how items could have gotten into this state, possibly from legacy bugs

            var dequipItems = EquippedObjects.Values.Where(i => i.CurrentWieldedLocation == EquipMask.None);

            foreach (var dequipItem in dequipItems)
            {
                log.Warn($"{Name}.AuditEquippedItems() - dequipping {dequipItem.Name} ({dequipItem.Guid})");
                HandleActionPutItemInContainer(dequipItem.Guid.Full, Guid.Full);
            }
        }

        /// <summary>
        /// Returns the Equipped items matching a weenie class id
        /// </summary>
        public List<WorldObject> GetEquippedObjectsOfWCID(uint weenieClassId)
        {
            return EquippedObjects.Values.Where(i => i.WeenieClassId == weenieClassId).ToList();
        }

        public bool TryConsumeFromEquippedObjectsWithNetworking(uint wcid, int amount = int.MaxValue)
        {
            var items = GetEquippedObjectsOfWCID(wcid);

            var leftReq = amount;
            foreach (var item in items)
            {
                var removeNum = Math.Min(leftReq, item.StackSize ?? 1);
                if (!TryConsumeFromEquippedObjectsWithNetworking(item, removeNum))
                    return false;

                leftReq -= removeNum;
                if (leftReq <= 0)
                    break;
            }
            return true;
        }

        public bool TryConsumeFromEquippedObjectsWithNetworking(WorldObject item, int amount = int.MaxValue)
        {
            if (amount >= (item.StackSize ?? 1))
            {
                if (!TryDequipObjectWithNetworking(item.Guid, out _, DequipObjectAction.ConsumeItem))
                    return false;
            }
            else
            {
                var stack = FindObject(item.Guid, SearchLocations.MyInventory | SearchLocations.MyEquippedItems, out var stackFoundInContainer, out var stackRootOwner, out _);

                if (stack == null || stackRootOwner == null)
                    return false;

                if (!AdjustStack(stack, -amount, stackFoundInContainer, stackRootOwner))
                    return false;

                Session.Network.EnqueueSend(new GameMessageSetStackSize(stack));
            }

            Session.Network.EnqueueSend(new GameMessagePrivateUpdatePropertyInt(this, PropertyInt.EncumbranceVal, EncumbranceVal ?? 0));

            if (item.WeenieType == WeenieType.Coin)
                UpdateCoinValue();

            return true;
        }

        /// <summary>
        /// Used with UpdateObject to maintain container placement sync on server
        /// </summary>
        public bool MoveItemToFirstContainerSlot(WorldObject target)
        {
            var container = target.Container as Container;

            if (container == null)
            {
                log.Error($"{Name}.Player_Inventory.MoveItemToFirstContainerSlot() - failed to find target item {target.Name} ({target.Guid}) in player inventory");
                return false;
            }

            if (!TryRemoveFromInventory(target.Guid))
            {
                log.Error($"{Name}.Player_Inventory.MoveItemToFirstContainerSlot() - failed to remove target item {target.Name} ({target.Guid}) from player inventory");
                return false;
            }

            if (!container.TryAddToInventory(target, 0, true, false))
            {
                log.Error($"{Name}.Player_Inventory.MoveItemToFirstContainerSlot() - failed to re-add target item {target.Name} ({target.Guid}) to player inventory");
                return false;
            }

            if (container != this)
            {
                // container is sidepack - update EncumbranceVal and Value for Player
                EncumbranceVal += (target.EncumbranceVal ?? 0);
                Value += (target.Value ?? 0);
            }

            return true;
        }

        /// <summary>
        /// Returns the total # of equipped objects matching a wcid
        /// </summary>
        public int GetNumEquippedObjectsOfWCID(uint weenieClassId)
        {
            return GetEquippedObjectsOfWCID(weenieClassId).Select(i => i.StackSize ?? 1).Sum();
        }
    }
}
