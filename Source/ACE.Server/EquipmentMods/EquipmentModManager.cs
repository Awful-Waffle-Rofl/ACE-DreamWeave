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
    /// The application flow: using a salvage bag of the designated material on a piece of eligible gear to
    /// convert into, or reroll, equipment mods. Entered from RecipeManager.UseObjectOnTarget before the
    /// cookbook lookup, because there is no cookbook row that could express this (it would need one row per
    /// target wcid, and the RNG needs C# regardless).
    ///
    /// Two actions, decided entirely by the target's current state:
    ///   CONVERT   (Obsidian) - a rated item's gear rating points are destroyed and become that many random
    ///                          mods at uniform random potency. Irreversible.
    ///   REROLL    (Obsidian) - an already-converted item's mods are all replaced by freshly rolled types and
    ///                          potencies, keeping the mod count. Also irreversible.
    ///
    /// GEAR RATINGS ARE OBSIDIAN'S ONLY WAY IN, THROUGH THIS MANAGER'S OWN ENTRY POINT. This class's
    /// UseObjectOnTarget still answers to nothing but Obsidian (IsModMaterial), and Convert/Reroll still
    /// refuse an unrated, unmodded item outright - that half of the old low-tier removal is unchanged.
    ///
    /// TIGER EYE IS BACK IN, BUT THROUGH ITS OWN DOOR (repo-owner ruling, 2026-08-08). A full bag of
    /// TigerEye, applied through TigerEyeArmorTinker.ApplyToArmor rather than through UseObjectOnTarget
    /// above, now ALSO mints one mod on an eligible target - additively alongside the steel tinkers it
    /// already applies, gated on the equipment_mods_enabled tunable, and excluding shields the same way
    /// IsEligibleSlot always has. The potency is rolled through the shared MintMods below, which uses the
    /// exact same EquipmentModRoller.RollPotency draw Obsidian uses - never the old removed deterministic
    /// floor. See TigerEyeArmorTinker's own remarks for why that floor is not coming back.
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
        /// The ONE material this system answers to: it converts gear ratings into mods, and rerolls
        /// already-converted items. Obsidian (0x45) has a flat supply curve, stockpilable at every tier.
        /// MaterialType 69 is the salvage bag weenie 21063 "materialobsidian".
        ///
        /// The name still says "high tier" because that is what it was designated as against the removed
        /// TigerEye low tier; there is no second tier any more, and adding one would need a design decision
        /// rather than another constant.
        /// </summary>
        public const MaterialType HighTierMaterial = MaterialType.Obsidian;

        /// <summary>
        /// Default salvage bag capacity. The salvage bag weenies carry no MaxStructure of their own - the
        /// salvaging code treats a missing MaxStructure as 100 (Player_Crafting.TryAddSalvage), so a real
        /// in-game bag has Structure set and MaxStructure null. Any fullness test must use the same fallback
        /// or it will reject every legitimate bag.
        /// </summary>
        public const int DefaultMaxStructure = 100;

        // ELIGIBILITY WAS PREVIOUSLY AN EquipMask OVERLAP MASK (EligibleSlots = Clothing | Armor) HERE. Repo-owner
        // ruling 2026-08-30 (second pass, same day): eligibility is armor ONLY, not general clothing - shirts,
        // pants, robes, cloaks, jewelry and trinkets are all excluded, and helms/gauntlets/boots stay eligible
        // only because they happen to be clothing that covers exclusively one extremity. That is exactly the
        // rule TigerEyeArmorTinker.IsEligibleItemClass already implements for the steel-tinker gate, so Obsidian
        // now calls that same predicate instead of keeping a second, independently-drifting one. See
        // IsEligibleSlot below for the shared rule and its doc comment.

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
        /// The BORN-WITH value of each rating in <see cref="GearRatingProperties"/>, INDEX FOR INDEX - the
        /// two arrays are read together by ordinal, so they must stay the same length and the same order.
        ///
        /// This is conversion's source of truth. It exists because a gear rating on a live item is not
        /// necessarily one the item earned: the retail Luminous Amber and Empowered Amber gems ADD rating
        /// points to a finished piece through ordinary recipes (20 of them, ids 8904-8923, each writing
        /// PropertyInt 370-383 straight onto the target via RecipeManager.ModifyInt). Obsidian pays one mod
        /// per rating point, so summing the LIVE ratings would pay out for crafted points as well as rolled
        /// ones - and, because each gem's own "already imbued" gate reads the very rating property a
        /// conversion clears, that overpayment would be repeatable rather than one-off.
        ///
        /// Summing the stamps instead closes both halves at once and leaves the gems alone to police
        /// themselves exactly as they do today: a gem re-applied after a conversion still lands, still adds
        /// its rating, and still cannot be converted, because nothing a player does ever writes a stamp.
        /// </summary>
        public static readonly PropertyInt[] OriginalGearRatingProperties =
        {
            PropertyInt.GearDamageOriginal,
            PropertyInt.GearDamageResistOriginal,
            PropertyInt.GearCritOriginal,
            PropertyInt.GearCritResistOriginal,
            PropertyInt.GearCritDamageOriginal,
            PropertyInt.GearCritDamageResistOriginal,
            PropertyInt.GearHealingBoostOriginal,
            PropertyInt.GearMaxHealthOriginal,
            PropertyInt.GearPKDamageRatingOriginal,
            PropertyInt.GearPKDamageResistRatingOriginal,
        };

        /// <summary>
        /// Which of the three application paths a source/target pair resolves to.
        /// </summary>
        public enum ModAction
        {
            None,

            /// <summary>Destroy N gear rating points and roll N random mods in their place.</summary>
            Convert,

            /// <summary>Replace every mod on an already-converted item with fresh rolls.</summary>
            Reroll,

            /// <summary>
            /// ALPHA-TEST-ONLY: set every mod already on the target to potency 1.0 (the maximum roll).
            /// Never adds or removes a mod. Triggered by PropertyInt.EquipmentModMaximizer on the source
            /// rather than by MaterialType, so it does not go through <see cref="ResolveAction"/>'s rule
            /// table at all - see <see cref="ResolveMaximizeAction"/>.
            /// </summary>
            Maximize,
        }

        /// <summary>
        /// Why a source/target pair resolves to no action. Kept separate from the message text so the rules
        /// stay unit testable without a live player.
        /// </summary>
        public enum ModActionRefusal
        {
            None,

            /// <summary>Found neither ratings to convert nor mods to reroll.</summary>
            NothingToConvertOrReroll,

            /// <summary>ALPHA-TEST-ONLY: the maximizer found no mods on the target to maximize.</summary>
            NothingToMaximize,

            /// <summary>
            /// The target matches TigerEyeArmorTinker.MatchesAppliedSignature - it was finished with tiger eye,
            /// which is a one-way commitment (repo-owner ruling, 2026-08-08). Both Convert and Reroll refuse it.
            /// </summary>
            SealedByTigerEye,
        }

        // ---------------- classification (pure, no WorldObject needed) ----------------

        /// <summary>
        /// TRUE if this item type / material pair is the designated equipment-mod salvage bag. Both halves
        /// matter: a raw Obsidian gem carries the same MaterialType but is ItemType.Gem, not
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

            return materialType == HighTierMaterial;
        }

        public static bool IsModMaterial(WorldObject source) => source != null && IsModMaterial(source.ItemType, source.MaterialType);

        /// <summary>
        /// ALPHA-TEST-ONLY: TRUE if this source is the maximizer tool - identified purely by
        /// PropertyInt.EquipmentModMaximizer's presence, not by MaterialType, since the maximizer has no
        /// designated material of its own. Kept out of production solely by the item's wcid being absent
        /// from Content/prod-manifest.txt.
        /// </summary>
        public static bool IsMaximizerSource(WorldObject source) => source != null && source.GetProperty(PropertyInt.EquipmentModMaximizer) != null;

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
        /// TRUE if an item's item class makes it a legal mod carrier: armor outright, or clothing that covers
        /// ONLY the head, hands or feet. Shirts, pants, robes, cloaks, jewelry (neck/wrist/finger) and the
        /// trinket slot are all excluded, even though several of them still roll gear ratings in lootgen
        /// (LootGenerationFactory_Clothing.TryMutateGearRating is unchanged) - ratings on those item classes
        /// remain as ratings rather than becoming mods. Shields are excluded outright even though they carry
        /// ItemType.Armor, matching lootgen (shields never roll gear ratings).
        ///
        /// Repo-owner ruling 2026-08-30 (second pass, same day, narrowing an earlier "armor and clothing"
        /// pass): this is deliberately the SAME rule TigerEyeArmorTinker.IsEligibleItemClass already applies
        /// to the steel-tinker gate, called directly rather than re-derived, so the two salvage systems can
        /// never drift apart on which items qualify. TigerEyeArmorTinker.IsExtremityClothing is an EQUALITY
        /// test against EquipMask.HeadWear / HandWear / FootWear on purpose - that is what excludes a hooded
        /// robe or a clothing boot that covers an extremity plus a non-extremity slot, since either carries
        /// more than one bit in ValidLocations. A plate helm is ItemType.Armor and passes on that alone; a
        /// cloth or leather helm is ItemType.Clothing and passes only through the extremity equality test.
        ///
        /// Consequence for existing items: an item already converted to mods on a now-excluded item class
        /// (shirt, pants, robe, cloak, jewelry, trinket) keeps its mods working, but can no longer be
        /// rerolled or maximized through Obsidian - IsEligibleTarget below now refuses it as a target.
        /// </summary>
        public static bool IsEligibleSlot(ItemType itemType, EquipMask validLocations, bool isShield)
        {
            if (isShield)
                return false;

            return TigerEyeArmorTinker.IsEligibleItemClass(itemType, validLocations);
        }

        public static bool IsEligibleTarget(WorldObject target)
        {
            if (target == null)
                return false;

            return IsEligibleSlot(target.ItemType, target.ValidLocations ?? EquipMask.None, target.IsShield);
        }

        // ---------------- state reads ----------------

        /// <summary>
        /// Total NATURAL gear rating points on an item - the number of mods a conversion will produce.
        ///
        /// Reads <see cref="OriginalGearRatingProperties"/>, NOT the live ratings, and that distinction is
        /// the whole point: see that array's remarks for why a live rating is not proof the item earned it.
        /// An item whose only ratings are crafted sums to zero and is refused as having nothing to convert.
        ///
        /// Every stamp is written at item creation and never afterwards, so this is stable for an item's
        /// whole life right up until <see cref="ModAction.Convert"/> spends it.
        /// </summary>
        public static int SumGearRatings(WorldObject target)
        {
            if (target == null)
                return 0;

            return OriginalGearRatingProperties.Sum(p => target.GetProperty(p) ?? 0);
        }

        /// <summary>
        /// Records what an item was born with, by copying each live gear rating into its stamp. Call this
        /// ONLY at creation, before any player can reach the item - from the weenie template in
        /// WorldObjectFactory.CreateNewWorldObject, and again from the rolled value in
        /// LootGenerationFactory.TryMutateGearRating, which mutates the object after it is constructed.
        ///
        /// SAFE TO CALL TWICE at creation (the loot path does exactly that) because it OVERWRITES rather
        /// than accumulates: the second call simply re-reads a strictly-later view of the same natural
        /// values. It is NOT safe to call once a player has had the item, because by then a live rating may
        /// be a crafted amber-gem point, and stamping it would hand that point to Obsidian - which is the
        /// exact hole the stamps exist to close.
        ///
        /// Writes with SetProperty, never player.UpdateProperty: the stamps are fork ids in a band no
        /// client knows, and there is no session to push them to at creation time anyway. A zero or absent
        /// rating writes NO row rather than a 0 - the same "clear it, never SetProperty(0)" rule the mod
        /// systems follow everywhere else.
        /// </summary>
        public static void StampOriginalGearRatings(WorldObject wo)
        {
            if (wo == null)
                return;

            for (var i = 0; i < GearRatingProperties.Length; i++)
            {
                var live = wo.GetProperty(GearRatingProperties[i]) ?? 0;
                var stamp = OriginalGearRatingProperties[i];

                if (live > 0)
                    wo.SetProperty(stamp, live);
                else if (wo.GetProperty(stamp) != null)
                    wo.RemoveProperty(stamp);
            }
        }

        /// <summary>
        /// Spends the natural gear ratings on a target: reduces each live rating by exactly what the item
        /// was born with, and clears every born-with stamp. Returns the rating writes the CALLER still owes
        /// the client, as property -> new value (null meaning remove), so the whole calculation stays
        /// testable without a live player - the caller performs them via player.UpdateProperty, which both
        /// writes and pushes.
        ///
        /// A CRAFTED POINT SURVIVES. Subtracting the stamp rather than clearing the property outright is
        /// what leaves an amber gem's contribution on the item after a conversion: it was never part of
        /// what Obsidian bought, so destroying it would be taking something the player was not paid for.
        ///
        /// CLEARING THE STAMPS IS WHAT MAKES A CONVERSION TERMINAL. With them gone
        /// <see cref="SumGearRatings"/> reads zero, so a later application resolves to
        /// <see cref="ModAction.Reroll"/> rather than paying out again - including on an item whose live
        /// rating property has since been re-armed by a gem, which is exactly the case the gems' own
        /// "already imbued" gates cannot see, because that gate reads the live rating a conversion clears.
        /// </summary>
        public static Dictionary<PropertyInt, int?> SpendNaturalRatings(WorldObject target)
        {
            var writes = new Dictionary<PropertyInt, int?>();

            if (target == null)
                return writes;

            for (var i = 0; i < GearRatingProperties.Length; i++)
            {
                var rating = GearRatingProperties[i];
                var stamp = OriginalGearRatingProperties[i];

                var live = target.GetProperty(rating) ?? 0;
                var born = target.GetProperty(stamp) ?? 0;

                // Max, not a bare subtraction: an admin edit or a content change could leave a stamp above
                // the live value, and a negative rating is worse than no rating.
                var remaining = Math.Max(0, live - born);

                if (live != remaining)
                    writes[rating] = remaining > 0 ? remaining : (int?)null;

                if (target.GetProperty(stamp) != null)
                    target.RemoveProperty(stamp);
            }

            return writes;
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
        ///
        /// TAKES NO MATERIAL any more. It used to, to pick between the TigerEye and Obsidian arms; with the
        /// low tier gone there is exactly one material and the only caller has already required it via
        /// <see cref="IsModMaterial"/>, so a material parameter here could only ever hold one value.
        /// </summary>
        public static ModActionRefusal ResolveAction(int ratingPoints, int modCount, int capacity, out ModAction action, out int count)
        {
            action = ModAction.None;
            count = 0;

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

        /// <summary>
        /// ALPHA-TEST-ONLY resolution for the maximizer tool, expressed over a plain mod count so it can be
        /// exercised without a live player, item or session - mirrors <see cref="ResolveAction"/>'s shape.
        /// A target with no mods yet refuses; there is nothing to raise to maximum, and Obsidian salvage is
        /// what mints mods in the first place. <paramref name="count"/> is the number of mods that will be
        /// re-written (the item's current mod count - the set is never changed).
        /// </summary>
        public static ModActionRefusal ResolveMaximizeAction(int modCount, out ModAction action, out int count)
        {
            action = ModAction.None;
            count = 0;

            if (modCount == 0)
                return ModActionRefusal.NothingToMaximize;

            action = ModAction.Maximize;
            count = modCount;

            return ModActionRefusal.None;
        }

        /// <summary>
        /// The tiger-eye seal check. An EXPLICIT check, deliberately kept out of <see cref="ResolveAction"/>'s
        /// value-only rule table because it has to read the item's tinker log, mirroring the weapon side's
        /// ResolveDriftStoneRefusal / PrismaticDriftStone.MatchesAppliedSignature - see that pair's remarks for
        /// why an explicit check earns its keep over folding into the rule table.
        /// </summary>
        public static ModActionRefusal ResolveTigerEyeRefusal(WorldObject target) =>
            TigerEyeArmorTinker.MatchesAppliedSignature(target) ? ModActionRefusal.SealedByTigerEye : ModActionRefusal.None;

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

            var isMaximizer = IsMaximizerSource(source);

            if (!isMaximizer && !IsModMaterial(source))
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
                // Legacy case: an item modded on a now-excluded slot (cloak, jewelry, trinket) before the
                // 2026-08-30 armor-only ruling still carries working mods, so telling its owner the item
                // "cannot carry equipment mods" is false on its face - it visibly does. Give that item a
                // different refusal that says the mods are locked in rather than denying they exist. This
                // branch runs before the isMaximizer split below, so it covers Reroll and the alpha Maximize
                // path identically.
                if (GetModCount(target) > 0)
                    SendCraftMessage(player, $"The {target.Name}'s modifications are locked in. Only armor and clothing, excluding underclothes, can be converted or rerolled now.");
                else
                    SendCraftMessage(player, $"The {target.Name} cannot carry equipment mods. Only armor and clothing, excluding underclothes, can.");

                return WeenieError.YouDoNotPassCraftingRequirements;
            }

            // ADDED 2026-08-07 with the workmanship term. A mod's stored value is now potency x
            // workmanship/10, so an item with NO workmanship would take a mod worth exactly nothing - the
            // "silently mint worthless mods" failure the weapon side refuses for the same reason. NOT
            // hypothetical: 4 of the 963 modded items on stage carry no workmanship row.
            //
            // Checked here rather than inside ResolveAction because that rule table is pure over plain values
            // and has no item to read, and because this must refuse the maximizer path too - it stamps a
            // fraction of its own.
            if (target.Workmanship == null)
            {
                SendCraftMessage(player, $"The {target.Name} has no workmanship, so it cannot hold an equipment mod.");
                return WeenieError.YouDoNotPassCraftingRequirements;
            }

            ModActionRefusal refusal;

            if (isMaximizer)
            {
                // the alpha-only Maximize path is deliberately NOT sealed - it never mints or destroys
                // ratings/mods, so the tiger-eye lockdown does not apply to it
                refusal = ResolveMaximizeAction(GetModCount(target), out action, out count);
            }
            else
            {
                // one wiring, shared with every non-maximizer caller - checked BEFORE ResolveAction so it
                // refuses Convert (ratingPoints > 0) and Reroll (capacity > 0 and modCount > 0) together
                refusal = ResolveTigerEyeRefusal(target);

                if (refusal == ModActionRefusal.None)
                    refusal = ResolveAction(SumGearRatings(target), GetModCount(target), GetModCapacity(target), out action, out count);
            }

            switch (refusal)
            {
                case ModActionRefusal.None:
                    return WeenieError.None;

                case ModActionRefusal.NothingToMaximize:
                    SendCraftMessage(player, $"The {target.Name} has no mods to maximize. Use obsidian salvage on it first to mint mods.");
                    break;

                case ModActionRefusal.SealedByTigerEye:
                    SendCraftMessage(player, $"The {target.Name} was finished with tiger eye. Its tinkering is sealed and no salvage will rework it.");
                    break;

                default:
                    SendCraftMessage(player, $"The {target.Name} has no gear ratings to convert and no mods to reroll.");
                    break;
            }

            return WeenieError.YouDoNotPassCraftingRequirements;
        }

        // ---------------- application ----------------

        /// <summary>
        /// Rolls <paramref name="count"/> distinct mods onto <paramref name="target"/>, writes their
        /// potencies (workmanship folded in), and returns the applied lines in
        /// <see cref="EquipmentModDisplay.Describe"/>'s shared format. Shared by every minting path - Convert
        /// and Reroll below, and Tiger Eye's additive grant
        /// (<see cref="ACE.Server.Entity.TigerEyeArmorTinker.ApplyToArmor"/>) - so a roll, a write and a
        /// report line are formed in exactly one place.
        ///
        /// DOES NOT TOUCH <see cref="PropertyInt.GearModCapacity"/>. Each caller sets its own capacity:
        /// Convert grows it by the rating points just destroyed, Reroll leaves the item's existing born bound
        /// alone, and Tiger Eye stamps a flat 1. Folding capacity in here would have to pick one of those
        /// three rules and get it wrong for the other two.
        /// </summary>
        public static List<string> MintMods(WorldObject target, int count, ICollection<EquipmentModId> exclude = null)
        {
            var rolled = EquipmentModRoller.RollDistinctModTypes(count, exclude);

            if (rolled.Count != count)
            {
                // unreachable with 30 catalog types and a maximum capacity of 3; a short list would mean
                // the registry shrank below the capacities already stamped on live items
                log.Error($"EquipmentModManager.MintMods({target?.Name}): asked for {count} distinct mods, rolled {rolled.Count}");
            }

            var applied = new List<string>();

            foreach (var modId in rolled)
            {
                var definition = EquipmentModRegistry.Get(modId);

                var potency = EquipmentModRoller.Clamp01(EquipmentModRoller.RollPotency(definition));

                // WORKMANSHIP IS FOLDED IN HERE, ONCE (2026-08-07), so the item stores a roll fraction
                // rather than a bare potency - the same shape a weapon mod stores. Every read site takes
                // the stored number at face value and needed no change. A target with no workmanship is
                // refused upstream by every caller (VerifyUseRequirements for Convert/Reroll,
                // HasTinkerableArmor for Tiger Eye), so this never silently stamps zero.
                var fraction = EquipmentModValue.RollFraction(potency, target.Workmanship ?? 0.0f);

                // written with SetProperty rather than player.UpdateProperty on purpose: UpdateProperty
                // would push a GameMessagePublicUpdatePropertyFloat carrying the raw scalar, and the whole
                // point of the 8100-8199 band is that the client never sees it. The object resend below
                // carries only weenie-header fields, so the scalar stays server side.
                target.SetProperty(definition.Property, fraction);

                // reports the FRACTION, not the potency, so the line the player is shown matches what the
                // item now carries and what a later appraisal will render
                applied.Add(EquipmentModDisplay.Describe(definition, fraction));
            }

            return applied;
        }

        private static void HandleApply(Player player, WorldObject source, WorldObject target, ModAction action, int count)
        {
            List<string> applied;

            if (action == ModAction.Maximize)
            {
                // ALPHA-TEST-ONLY: pure property-bag rewrite, no roll and no shared MintMods call below -
                // see MaximizeMods for the whole rule.
                applied = MaximizeMods(target);
            }
            else
            {
                switch (action)
                {
                    case ModAction.Convert:

                        // The NATURAL ratings are the price - see SpendNaturalRatings, which clears the
                        // stamps itself and hands back only the client-known rating writes. They are pushed
                        // rather than left to the object resend because the client already knows them, and
                        // UpdateProperty is what performs the write as well as the push.
                        foreach (var write in SpendNaturalRatings(target))
                            player.UpdateProperty(target, write.Key, write.Value);

                        // defensive: a rated item should never already carry mods, but if content or an
                        // admin edit produced one, keep it and grow capacity rather than silently dropping it
                        var existing = EquipmentModDisplay.GetMods(target).Select(m => m.Definition.Id).ToList();

                        applied = MintMods(target, count, existing);

                        target.SetProperty(PropertyInt.GearModCapacity, ComputeConvertedCapacity(existing.Count, count));
                        break;

                    case ModAction.Reroll:

                        ClearMods(target);

                        applied = MintMods(target, count);
                        break;

                    default:

                        player.SendUseDoneEvent(WeenieError.CraftGeneralErrorNoUiMsg);
                        return;
                }
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

            if (action == ModAction.Maximize)
            {
                SendCraftMessage(player, $"Every mod on your {target.Name} is maximized.");
            }
            else
            {
                // only Convert and Reroll reach here - Maximize took the branch above, and every other action
                // was refused before HandleApply was called
                var verb = action == ModAction.Convert ? "converts the gear ratings on" : "reforges the mods on";

                SendCraftMessage(player, $"The salvage {verb} your {target.Name}.");
            }

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

        /// <summary>
        /// ALPHA-TEST-ONLY: sets every equipment mod already present on <paramref name="target"/> to
        /// potency 1.0, the maximum possible roll. Never adds a mod (a type absent before stays absent
        /// after) and never removes one - only the set of properties already present is touched, read once
        /// up front so the write pass cannot see its own edits. Pure over the WorldObject's property bag:
        /// no player, no networking, no persistence, so it is unit testable without a live Player - see
        /// EquipmentModManager.HandleApply for the caller that adds those.
        ///
        /// Returns the applied lines in <see cref="EquipmentModDisplay.Describe"/>'s shared format, the
        /// same rendering HandleApply reports to the player for every other action.
        /// </summary>
        public static List<string> MaximizeMods(WorldObject target)
        {
            var applied = new List<string>();

            if (target == null)
                return applied;

            // "Maximum" means the best THIS item can hold, not a flat 1.0 (2026-08-07). Stamping 1.0 on a
            // workmanship 7 piece would put it above anything a real roll on that item could produce, and the
            // appraisal bracket would have to clamp to hide it. On a workmanship 10 item - which is what the
            // maximizer is used on in practice - this is still exactly 1.0.
            var fraction = EquipmentModValue.RollFraction(1.0, target.Workmanship ?? 0.0f);

            foreach (var (definition, _) in EquipmentModDisplay.GetMods(target))
            {
                target.SetProperty(definition.Property, fraction);
                applied.Add(EquipmentModDisplay.Describe(definition, fraction));
            }

            return applied;
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

            // the UpdateObject above just rebuilt this object client-side, which drops any persistent
            // particle aura (VisualEffectManager). target is inventory-only by construction here, so
            // reopen the once-only send guard rather than resending: sending to a packed item would
            // attach the script to nothing and spend the guard that equipping it later depends on.
            VisualEffectManager.Forget(player.Session, target);
        }
    }
}
