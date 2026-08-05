using System;
using System.Collections.Generic;
using System.Linq;

using log4net;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.EquipmentMods
{
    /// <summary>
    /// The application flow: using a salvage bag of a designated material on a piece of eligible gear to add,
    /// convert into, or reroll equipment mods. Entered from RecipeManager.UseObjectOnTarget before the cookbook
    /// lookup, because there is no cookbook row that could express this (it would need one row per target wcid,
    /// and the RNG needs C# regardless).
    ///
    /// Three actions, decided entirely by the material and the target's current state:
    ///   LOW TIER  (TigerEye) - an unrated, unmodded item gains ONE random mod at the guaranteed
    ///                          equipment_mod_lowtier_potency floor. Capacity is set to 1.
    ///   CONVERT   (Obsidian) - a rated item's gear rating points are destroyed and become that many random
    ///                          mods at uniform random potency. Irreversible.
    ///   REROLL    (Obsidian) - an already-converted item's mods are all replaced by freshly rolled types and
    ///                          potencies, keeping the mod count. Also irreversible.
    ///
    /// THE CONFIRMATION GATE IS THE CLIENT'S OWN, NOT OURS. Both Obsidian paths destroy something the player
    /// cannot get back, and that still deserves a confirmation - it is simply satisfied one layer up. The
    /// client fires its generic tinkering-material confirmation ("Are you sure you want to apply the X to the
    /// Y? The Y may be destroyed.") entirely client-side, before the server is contacted, and it cannot be
    /// suppressed from here. VERIFIED by the repo owner in live play TWICE, on both sides: 2026-07-30 with
    /// Tourmaline on a weapon carrying NO imbues (so it is the generic prompt on every use of tinkering
    /// material against a target, not an imbue-specific warning), and 2026-08-01 with Obsidian on this side,
    /// which is what reported the duplication here. A server-side dialog on top of it produced two panels back
    /// to back for one gesture, so this system no longer raises one. What our dialog was the only place to say - what a
    /// conversion or a reroll actually destroys - now lives on the salvage bags' own LongDesc, so a player
    /// reads it when appraising the bag, BEFORE the use
    /// (Content/sql/patches/equipment_mods_salvage_bag_description.sql).
    ///
    /// Application is 100% success with no destroy-on-failure: the roll IS the gamble. Targets must be in the
    /// player's inventory and never equipped, which sidesteps the equipped-items rating cache entirely (that
    /// cache is only rebuilt on equip/unequip, so mutating a worn item's ratings would desync it).
    ///
    /// The UX shape - busy/combat guards, VerifyUseRequirements, a ClapHands chain that re-verifies inside the
    /// action, then SendUseDoneEvent - follows Entity/CorePlating.cs, the closest existing use-item-on-item
    /// mechanic that is pure C# rather than a recipe.
    /// </summary>
    public static class EquipmentModManager
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// Low-tier material: consumes a full bag to add one mod at the minimum potency floor.
        /// TigerEye (0x2A) is front-loaded - abundant in tiers 1-3 and collapsing roughly 40x above tier 4 -
        /// so endgame players trade down-tier for it rather than farming it. See the Phase 0 material scarcity
        /// analysis; MaterialType 42 is the salvage bag weenie 21081 "materialtigereye".
        /// </summary>
        public const MaterialType LowTierMaterial = MaterialType.TigerEye;

        /// <summary>
        /// High-tier material: converts gear ratings into mods, and rerolls already-converted items.
        /// Obsidian (0x45) has a flat supply curve, stockpilable at every tier. MaterialType 69 is the salvage
        /// bag weenie 21063 "materialobsidian".
        /// </summary>
        public const MaterialType HighTierMaterial = MaterialType.Obsidian;

        /// <summary>
        /// Default salvage bag capacity. The salvage bag weenies carry no MaxStructure of their own - the
        /// salvaging code treats a missing MaxStructure as 100 (Player_Crafting.TryAddSalvage), so a real
        /// in-game bag has Structure set and MaxStructure null. Any fullness test must use the same fallback
        /// or it will reject every legitimate bag.
        /// </summary>
        public const int DefaultMaxStructure = 100;

        /// <summary>
        /// Slots where gear ratings can be applied: armor, clothing, cloaks, classic jewelry (neck, wrist,
        /// finger) and the trinket slot. Mirrors the item-class branches of
        /// LootGenerationFactory_Clothing.TryMutateGearRating, which reaches exactly these through
        /// roll.HasArmorLevel / IsClothing / IsCloak / IsJewelry.
        ///
        /// Trinkets ARE rating carriers, despite occupying a ready slot rather than a jewelry one. The six
        /// loot trinkets - wcids 41483 compass, 41484 goggles, 41485 pocketwatch, 41486 puzzlebox,
        /// 41487 mechanicalscarab, 41488 top - are entries in Factories/Tables/Wcids/JewelryWcids.cs, in all
        /// three tier tables. A jewelry roll therefore stamps TreasureItemType.Jewelry on the TreasureRoll
        /// (LootGenerationFactory_Jewelry.cs), TryMutateGearRating takes its roll.IsJewelry branch, and the
        /// item gets GearHealingBoost or GearMaxHealth at tier 8 (1 to 3 points, per
        /// GearRatingChance.ClothingJewelryRating). Their ValidLocations (PropertyInt 9) is 0x04000000 =
        /// EquipMask.TrinketOne, so this mask has to name TrinketOne explicitly - otherwise Obsidian refuses
        /// the one non-armor, non-jewelry item class whose ratings it exists to convert.
        ///
        /// Still deliberately NOT EquipMask.Jewelry wholesale: that constant also covers the three sigil
        /// slots, and sigils stay out. Aetheria is produced by its own mundane add-on path
        /// (LootGenerationFactory.TryRollAetheria -> CreateAetheria -> MutateAetheria), which never builds a
        /// TreasureRoll and so never reaches TryMutateGearRating; MutateAetheria writes only ItemMaxLevel and
        /// IconOverlayId, and ACE.Server/Entity/Aetheria.cs contains no Gear* property at all. Not a rating
        /// carrier, so not a mod carrier.
        /// </summary>
        public const EquipMask EligibleSlots = EquipMask.Clothing | EquipMask.Armor | EquipMask.Cloak
            | EquipMask.NeckWear | EquipMask.WristWear | EquipMask.FingerWear | EquipMask.TrinketOne;

        /// <summary>
        /// The ten live gear rating properties, in the same order as the equipped-items rating cache in
        /// Creature_Equipment.cs. Conversion sums these and then clears every one of them.
        /// </summary>
        public static readonly PropertyInt[] GearRatingProperties =
        {
            PropertyInt.GearDamage,
            PropertyInt.GearDamageResist,
            PropertyInt.GearCrit,
            PropertyInt.GearCritResist,
            PropertyInt.GearCritDamage,
            PropertyInt.GearCritDamageResist,
            PropertyInt.GearHealingBoost,
            PropertyInt.GearMaxHealth,
            PropertyInt.GearPKDamageRating,
            PropertyInt.GearPKDamageResistRating,
        };

        /// <summary>
        /// Which of the three application paths a source/target pair resolves to.
        /// </summary>
        public enum ModAction
        {
            None,

            /// <summary>Low-tier: add one mod at the guaranteed potency floor to an unmodded, unrated item.</summary>
            LowTierApply,

            /// <summary>High-tier: destroy N gear rating points and roll N random mods in their place.</summary>
            Convert,

            /// <summary>High-tier: replace every mod on an already-converted item with fresh rolls.</summary>
            Reroll,
        }

        /// <summary>
        /// Why a source/target pair resolves to no action. Kept separate from the message text so the rules
        /// stay unit testable without a live player.
        /// </summary>
        public enum ModActionRefusal
        {
            None,

            /// <summary>Low tier cannot touch a rated item - that is what the high-tier conversion is for.</summary>
            LowTierOnRatedItem,

            /// <summary>Low tier only ever grants the FIRST mod; an already-modified item is out of reach.</summary>
            LowTierOnModifiedItem,

            /// <summary>High tier found neither ratings to convert nor mods to reroll.</summary>
            NothingToConvertOrReroll,
        }

        // ---------------- classification (pure, no WorldObject needed) ----------------

        /// <summary>
        /// TRUE if this item type / material pair is one of the designated equipment-mod salvage bags. Both
        /// halves matter: a raw TigerEye gem carries the same MaterialType but is ItemType.Gem, not
        /// TinkeringMaterial, so it must not be mistaken for a bag.
        /// </summary>
        public static bool IsModMaterial(ItemType itemType, MaterialType? materialType)
        {
            // TinkeringTool was tried here on 2026-08-01 and reverted the same day. It is not merely
            // unnecessary - it is unusable: the client intercepts the use of an ItemType.TinkeringTool item to
            // open its own salvage panel and sends GameActionCreateTinkeringTool (0x027D) instead, so a
            // use-on-target never reaches the server.
            if (itemType != ItemType.TinkeringMaterial)
                return false;

            return materialType == LowTierMaterial || materialType == HighTierMaterial;
        }

        public static bool IsModMaterial(WorldObject source) => source != null && IsModMaterial(source.ItemType, source.MaterialType);

        /// <summary>
        /// TRUE if a salvage bag holds a FULL unit of material. A whole bag is the price of one application
        /// (design decision 7), so a partial bag is rejected rather than partially consumed.
        /// </summary>
        public static bool IsFullBag(int? structure, int? maxStructure)
        {
            var max = maxStructure ?? DefaultMaxStructure;

            return max > 0 && (structure ?? 0) >= max;
        }

        public static bool IsFullBag(WorldObject source) => source != null && IsFullBag(source.Structure, source.MaxStructure);

        /// <summary>
        /// TRUE if an item's equip slots make it a legal mod carrier. Shields are excluded even though they
        /// have an armor level, matching lootgen (shields never roll gear ratings).
        /// </summary>
        public static bool IsEligibleSlot(EquipMask validLocations, bool isShield)
        {
            if (isShield)
                return false;

            return (validLocations & EligibleSlots) != 0;
        }

        public static bool IsEligibleTarget(WorldObject target)
        {
            if (target == null)
                return false;

            return IsEligibleSlot(target.ValidLocations ?? EquipMask.None, target.IsShield);
        }

        // ---------------- state reads ----------------

        /// <summary>
        /// Total gear rating points on an item - the number of mods a conversion will produce.
        /// </summary>
        public static int SumGearRatings(WorldObject target)
        {
            if (target == null)
                return 0;

            return GearRatingProperties.Sum(p => target.GetProperty(p) ?? 0);
        }

        /// <summary>
        /// How many mods an item currently carries.
        /// </summary>
        public static int GetModCount(WorldObject target) => EquipmentModDisplay.GetMods(target).Count;

        public static int GetModCapacity(WorldObject target) => target?.GetProperty(PropertyInt.GearModCapacity) ?? 0;

        /// <summary>
        /// The capacity stamped on an item by a conversion: the born-rating bound (design decision 4). Adding
        /// any pre-existing mod count is purely defensive - a rated item should carry no mods - but it means a
        /// content or admin oddity grows the capacity instead of orphaning a mod above it.
        /// </summary>
        public static int ComputeConvertedCapacity(int existingModCount, int ratingPoints) => existingModCount + ratingPoints;

        /// <summary>
        /// The whole path-selection rule set, expressed over plain numbers so it can be exercised without a
        /// live player, item or session. <paramref name="count"/> is the number of mods the action produces.
        /// </summary>
        public static ModActionRefusal ResolveAction(MaterialType material, int ratingPoints, int modCount, int capacity, out ModAction action, out int count)
        {
            action = ModAction.None;
            count = 0;

            if (material == LowTierMaterial)
            {
                if (ratingPoints > 0)
                    return ModActionRefusal.LowTierOnRatedItem;

                if (capacity > 0 || modCount > 0)
                    return ModActionRefusal.LowTierOnModifiedItem;

                // an unrated item's capacity is 1 (design decision 4)
                action = ModAction.LowTierApply;
                count = 1;

                return ModActionRefusal.None;
            }

            if (ratingPoints > 0)
            {
                action = ModAction.Convert;
                count = ratingPoints;

                return ModActionRefusal.None;
            }

            if (capacity > 0 && modCount > 0)
            {
                action = ModAction.Reroll;
                count = capacity;

                return ModActionRefusal.None;
            }

            return ModActionRefusal.NothingToConvertOrReroll;
        }

        // ---------------- entry point ----------------

        /// <summary>
        /// The player uses a designated salvage bag on a piece of gear. Mirrors CorePlating.UseObjectOnTarget:
        /// guards, verify, clap, re-verify, act. There is no confirmed flag and no re-entry: the client has
        /// already asked and been answered before this is ever reached (see the class remarks), so this runs
        /// straight through the animation to the apply.
        /// </summary>
        public static void UseObjectOnTarget(Player player, WorldObject source, WorldObject target)
        {
            if (player.IsBusy)
            {
                player.SendUseDoneEvent(WeenieError.YoureTooBusy);
                return;
            }

            var allowCraftInCombat = PropertyManager.GetBool("allow_combat_mode_crafting").Item;

            if (!allowCraftInCombat && player.CombatMode != CombatMode.NonCombat)
            {
                player.SendUseDoneEvent(WeenieError.YouMustBeInPeaceModeToTrade);
                return;
            }

            var useError = VerifyUseRequirements(player, source, target, out var action, out var count);

            if (useError != WeenieError.None)
            {
                player.SendUseDoneEvent(useError);
                return;
            }

            var motionCommand = MotionCommand.ClapHands;

            var actionChain = new ActionChain();
            var nextUseTime = 0.0f;

            player.IsBusy = true;

            if (allowCraftInCombat && player.CombatMode != CombatMode.NonCombat)
            {
                // drop out of combat mode - only reachable when allow_combat_mode_crafting is set,
                // otherwise the guard above already aborted
                var stanceTime = player.SetCombatMode(CombatMode.NonCombat);
                actionChain.AddDelaySeconds(stanceTime);

                nextUseTime += stanceTime;
            }

            var currentStance = player.CurrentMotionState.Stance;   // expected to be MotionStance.NonCombat

            var clapTime = Physics.Animation.MotionTable.GetAnimationLength(player.MotionTableId, currentStance, motionCommand);

            actionChain.AddAction(player, () => player.SendMotionAsCommands(motionCommand, currentStance));
            actionChain.AddDelaySeconds(clapTime);

            nextUseTime += clapTime;

            actionChain.AddAction(player, () =>
            {
                // TOCTOU GUARD - DO NOT DELETE. The player had the whole ClapHands animation to move, drop,
                // equip or destroy either item, so every requirement is checked a second time here, against
                // the state as it is NOW rather than as it was when the chain was queued.
                var reverifyError = VerifyUseRequirements(player, source, target, out var reverifiedAction, out var reverifiedCount);

                if (reverifyError != WeenieError.None)
                {
                    player.SendUseDoneEvent(reverifyError);
                    return;
                }

                HandleApply(player, source, target, reverifiedAction, reverifiedCount);
            });

            actionChain.AddAction(player, () => player.IsBusy = false);

            actionChain.EnqueueChain();

            player.NextUseTime = DateTime.UtcNow.AddSeconds(nextUseTime);
        }

        // ---------------- verification ----------------

        /// <summary>
        /// Decides which action a source/target pair resolves to, and explains any refusal to the player.
        /// <paramref name="count"/> is the number of mods the action will produce.
        /// </summary>
        public static WeenieError VerifyUseRequirements(Player player, WorldObject source, WorldObject target, out ModAction action, out int count)
        {
            action = ModAction.None;
            count = 0;

            if (!PropertyManager.GetBool("equipment_mods_enabled").Item)
                return WeenieError.YouDoNotPassCraftingRequirements;

            if (source == target)
            {
                player.SendTransientError($"You can't use the {source.Name} on itself.");
                return WeenieError.YouDoNotPassCraftingRequirements;
            }

            if (!IsModMaterial(source))
                return WeenieError.YouDoNotPassCraftingRequirements;

            // inventory only, never equipped: mutating a worn item's gear ratings would desync the
            // equipped-items rating cache, which is only rebuilt on equip/unequip
            if (player.FindObject(source.Guid.Full, Player.SearchLocations.MyInventory) == null)
            {
                SendCraftMessage(player, $"The {source.Name} must be in your inventory.");
                return WeenieError.YouDoNotPassCraftingRequirements;
            }

            if (player.FindObject(target.Guid.Full, Player.SearchLocations.MyInventory) == null)
            {
                SendCraftMessage(player, $"You must remove the {target.Name} and place it in your inventory first.");
                return WeenieError.YouDoNotPassCraftingRequirements;
            }

            // A full bag OR a salvage tool with a charge left. A PARTIAL BAG IS STILL REFUSED, and the two
            // cases are not the same thing: a bag's Structure is a FRACTION of one unit of salvage, so half a
            // bag cannot pay for a whole application, while a tool's Structure is a COUNT of whole
            // applications, so 7 of 10 pays for one perfectly well. PropertyInt.SalvageToolCharges (9035) is
            // the ONLY thing that tells the two apart - see ACE.Server.Entity.SalvageTool, which owns the
            // either/or so this rule exists in exactly one place.
            if (!IsFullBag(source) && !SalvageTool.HasUsableCharge(source))
            {
                // a spent tool should not exist - it is destroyed on its last use - but never tell a player
                // holding one that it "is not full", which would send them looking for a fuller Hammer
                if (SalvageTool.IsSalvageTool(source))
                    SendCraftMessage(player, $"The {source.Name} has no uses remaining.");
                else
                    SendCraftMessage(player, $"The {source.Name} is not full. A complete unit of salvage is required.");

                return WeenieError.YouDoNotPassCraftingRequirements;
            }

            if (!IsEligibleTarget(target))
            {
                SendCraftMessage(player, $"The {target.Name} cannot carry equipment mods. Only armor, clothing, cloaks and jewelry can.");
                return WeenieError.YouDoNotPassCraftingRequirements;
            }

            var refusal = ResolveAction(source.MaterialType ?? MaterialType.Unknown, SumGearRatings(target), GetModCount(target), GetModCapacity(target), out action, out count);

            switch (refusal)
            {
                case ModActionRefusal.None:
                    return WeenieError.None;

                case ModActionRefusal.LowTierOnRatedItem:
                    SendCraftMessage(player, $"The {target.Name} carries gear ratings. Use obsidian to convert those ratings into mods instead.");
                    break;

                case ModActionRefusal.LowTierOnModifiedItem:
                    SendCraftMessage(player, $"The {target.Name} has already been modified and cannot take another mod this way.");
                    break;

                default:
                    SendCraftMessage(player, $"The {target.Name} has no gear ratings to convert and no mods to reroll.");
                    break;
            }

            return WeenieError.YouDoNotPassCraftingRequirements;
        }

        // ---------------- application ----------------

        private static void HandleApply(Player player, WorldObject source, WorldObject target, ModAction action, int count)
        {
            List<EquipmentModId> rolled;

            // the low tier grants a fixed potency per mod rather than a roll; the high tier gambles
            var lowTier = action == ModAction.LowTierApply;

            switch (action)
            {
                case ModAction.LowTierApply:

                    rolled = EquipmentModRoller.RollDistinctModTypes(count);

                    target.SetProperty(PropertyInt.GearModCapacity, count);
                    break;

                case ModAction.Convert:

                    // the ratings are the price: clear all ten, then mint that many mods.
                    // These are client-known properties, so push the zeroed values rather than only
                    // relying on the object resend.
                    foreach (var rating in GearRatingProperties)
                    {
                        if (target.GetProperty(rating) != null)
                            player.UpdateProperty(target, rating, null);
                    }

                    // defensive: a rated item should never already carry mods, but if content or an admin
                    // edit produced one, keep it and grow capacity rather than silently dropping it
                    var existing = EquipmentModDisplay.GetMods(target).Select(m => m.Definition.Id).ToList();

                    rolled = EquipmentModRoller.RollDistinctModTypes(count, existing);

                    target.SetProperty(PropertyInt.GearModCapacity, ComputeConvertedCapacity(existing.Count, count));
                    break;

                case ModAction.Reroll:

                    ClearMods(target);

                    rolled = EquipmentModRoller.RollDistinctModTypes(count);
                    break;

                default:

                    player.SendUseDoneEvent(WeenieError.CraftGeneralErrorNoUiMsg);
                    return;
            }

            if (rolled.Count != count)
            {
                // unreachable with 27 catalog types and a maximum capacity of 3; a short list would mean the
                // registry shrank below the capacities already stamped on live items
                log.Error($"EquipmentModManager.HandleApply({player.Name}, {source.Name}, {target.Name}): asked for {count} distinct mods, rolled {rolled.Count}");
            }

            var applied = new List<string>();

            foreach (var modId in rolled)
            {
                var definition = EquipmentModRegistry.Get(modId);

                // both branches honor the mod's effective MinPotency floor (its own, or the catalog default),
                // so no mod can be written at a potency whose effect rounds away to nothing
                var potency = EquipmentModRoller.Clamp01(lowTier
                    ? EquipmentModRoller.LowTierPotency(definition)
                    : EquipmentModRoller.RollPotency(definition));

                // written with SetProperty rather than player.UpdateProperty on purpose: UpdateProperty would
                // push a GameMessagePublicUpdatePropertyFloat carrying the raw potency scalar, and the whole
                // point of the 8100-8199 band is that the client never sees it. The object resend below
                // carries only weenie-header fields, so the scalar stays server side.
                target.SetProperty(definition.Property, potency);

                applied.Add(EquipmentModDisplay.Describe(definition, potency));
            }

            target.ChangesDetected = true;
            target.SaveBiotaToDatabase();

            // the source may be destroyed by the consume below, so its name is captured first
            var sourceName = source.Name;
            var wasSalvageTool = SalvageTool.IsSalvageTool(source);

            if (!SalvageTool.TryConsume(player, source, out var remaining))
            {
                log.Error($"EquipmentModManager.HandleApply({player.Name}, {sourceName}, {target.Name}): failed to consume the salvage bag");
                player.SendUseDoneEvent(WeenieError.CraftGeneralErrorNoUiMsg);
                return;
            }

            UpdateObj(player, target);

            var verb = action == ModAction.Convert ? "converts the gear ratings on" : action == ModAction.Reroll ? "reforges the mods on" : "settles into";

            SendCraftMessage(player, $"The salvage {verb} your {target.Name}.");

            foreach (var line in applied)
                SendCraftMessage(player, $"- {line}");

            var chargeMessage = SalvageTool.GetChargeMessage(sourceName, wasSalvageTool, remaining);

            if (chargeMessage != null)
                SendCraftMessage(player, chargeMessage);

            player.SendUseDoneEvent();
        }

        /// <summary>
        /// Removes every mod potency from an item. Capacity is deliberately left alone - it is the item's
        /// born-rating bound and must survive a reroll.
        /// </summary>
        public static void ClearMods(WorldObject target)
        {
            if (target == null)
                return;

            foreach (var definition in EquipmentModRegistry.AllMods)
            {
                if (target.GetProperty(definition.Property) != null)
                    target.RemoveProperty(definition.Property);
            }
        }

        private static void SendCraftMessage(Player player, string message)
        {
            player.Session.Network.EnqueueSend(new GameMessageSystemChat(message, ChatMessageType.Craft));
        }

        /// <summary>
        /// Sends an UpdateObj for the modified target, mirroring RecipeManager.UpdateObj. The equipped branch
        /// of that helper is omitted: targets here are verified inventory-only by construction.
        /// </summary>
        private static void UpdateObj(Player player, WorldObject target)
        {
            player.EnqueueBroadcast(new GameMessageUpdateObject(target));

            // the client moves an item to the first container slot when it receives an UpdateObject,
            // so mirror that server side for persistence
            if (player.FindObject(target.Guid.Full, Player.SearchLocations.MyInventory) != null)
                player.MoveItemToFirstContainerSlot(target);
        }
    }
}
