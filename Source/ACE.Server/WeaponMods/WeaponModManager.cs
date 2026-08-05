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

namespace ACE.Server.WeaponMods
{
    /// <summary>
    /// Two crafting flows onto one shared ten-slot budget, entered from RecipeManager.UseObjectOnTarget before
    /// the cookbook lookup. Modelled on ACE.Server.EquipmentMods.EquipmentModManager, the armor-side sibling:
    /// the busy and peace-mode guards, VerifyUseRequirements before the animation, the ClapHands chain, the
    /// SECOND VerifyUseRequirements inside the queued action, TryConsumeFromInventoryWithNetworking, the
    /// ChangesDetected / SaveBiotaToDatabase pair and the GameMessageUpdateObject broadcast all transfer
    /// unchanged. The sibling's nested Confirmation subclass does NOT - see "the confirmation gate" below.
    ///
    /// THE CONFIRMATION GATE IS THE CLIENT'S OWN, NOT OURS. Design section 8 requires a confirmation on every
    /// use, not just the first, and that requirement still holds - it is simply satisfied one layer up. The
    /// client fires its generic tinkering-material confirmation ("Are you sure you want to apply the X to the
    /// Y? The Y may be destroyed.") entirely client-side, before the server is contacted, and it cannot be
    /// suppressed from here. VERIFIED by the repo owner in live play on 2026-07-30, on a weapon carrying NO
    /// imbues, so it is the generic prompt that gates every use of tinkering material on a target and not an
    /// imbue-specific warning. A server-side dialog on top of it produced two panels back to back for one
    /// gesture, so this system no longer raises one. What our dialog was the only place to say - what a reroll
    /// or a swap actually does - now lives on the two salvage bags' own LongDesc, so a player reads it when
    /// appraising the bag, BEFORE the use (Content/sql/patches/weapon_mods_salvage_bag_description.sql).
    ///
    ///   REROLL (Tourmaline) - strips the weapon's tinkers and specials and refills all ten slots at random,
    ///                         with a chance to convert some of the budget into rare special modifiers.
    ///                         Cheap per attempt, wipes everything, gives a mushy tinker spread.
    ///   SWAP   (Amethyst)   - trades ONE random filled slot for ONE random modifier at a much higher special
    ///                         rate. Expensive per special, but the only way to keep a deliberately chosen
    ///                         tinker base while acquiring them.
    ///
    /// WHY TARGETABILITY IS LOAD-BEARING. The design prices a +101.1% endgame ceiling on the assumption that a
    /// perfect trio is a lottery, not a grind. Two properties guarantee that and neither is optional: nothing is
    /// ever player-targetable, and three specials is a PERMANENT per-weapon bound rather than a per-roll cap. If
    /// this file ever grows a way to choose, replace or reroll a single special, STOP - that invalidates the
    /// power assessment and it has to be re-derived first.
    ///
    /// The intercept placement is also why no bypass code is needed for the retail tinker cap: this runs before
    /// VerifyRequirements and GetTinkerChance, so the data-driven "NumTimesTinkered >= 10" gate cannot apply to
    /// it, while retail tinkering stays bound by that gate.
    /// </summary>
    public static class WeaponModManager
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>Tourmaline (0x2B = 43), the reroll. Salvage bag weenie 21082. Flat supply at every tier.</summary>
        public const MaterialType RerollMaterial = MaterialType.Tourmaline;

        /// <summary>Amethyst (0x0C = 12), the swap. Salvage bag weenie 21036. Same flat supply curve.</summary>
        public const MaterialType SwapMaterial = MaterialType.Amethyst;

        /// <summary>
        /// Default salvage bag capacity. The bag weenies carry no MaxStructure of their own - the salvaging code
        /// treats a missing MaxStructure as 100 (Player_Crafting.TryAddSalvage) - so any fullness test must use
        /// the same fallback or it rejects every legitimate bag.
        /// </summary>
        public const int DefaultMaxStructure = 100;

        public enum WeaponModAction
        {
            None,

            /// <summary>Tourmaline: strip everything and refill all ten slots at random.</summary>
            Reroll,

            /// <summary>Amethyst: trade one random filled slot for one random modifier.</summary>
            Swap,
        }

        /// <summary>
        /// Why a source/target pair resolves to no action. Kept separate from the message text so the rules stay
        /// unit testable without a live player.
        /// </summary>
        public enum WeaponModRefusal
        {
            None,

            /// <summary>The source is not one of the two designated salvage bags.</summary>
            NotAModMaterial,

            /// <summary>The target is not a melee weapon, missile weapon or caster (shields and ammo included).</summary>
            NotAWeapon,

            /// <summary>The target has no workmanship, so its specials would all resolve to nothing.</summary>
            NoWorkmanship,

            /// <summary>
            /// Every slot is spoken for by something this system cannot reverse - imbues, or log entries it does
            /// not own - so there is nothing to reroll or swap.
            /// </summary>
            NoAvailableSlots,

            /// <summary>The weapon was finished by a Prismatic Drift Stone, which promised it could never be tinkered again.</summary>
            DriftStoneLocked,

            /// <summary>The retail tinker log does not account for NumTimesTinkered, so the current set cannot be fully reversed.</summary>
            TinkerLogMismatch,

            /// <summary>A swap trades one slot for another; it is not a way to fill empty ones.</summary>
            SwapNeedsFullBudget,

            /// <summary>Three specials is a permanent bound - no fourth, and no replacing one of the three.</summary>
            SwapAtSpecialCap,
        }

        // ---------------- classification (pure, no WorldObject needed) ----------------

        /// <summary>
        /// TRUE if this item type / material pair is one of the two designated salvage bags. BOTH halves matter:
        /// a raw Tourmaline or Amethyst GEM carries the same MaterialType but is ItemType.Gem, so requiring
        /// ItemType.TinkeringMaterial is what stops a gem being taken for a bag.
        /// </summary>
        public static bool IsModMaterial(ItemType itemType, MaterialType? materialType)
        {
            // TinkeringTool was tried here on 2026-08-01 and reverted the same day. It is not merely
            // unnecessary - it is unusable: the client intercepts the use of an ItemType.TinkeringTool item to
            // open its own salvage panel and sends GameActionCreateTinkeringTool (0x027D) instead, so a
            // use-on-target never reaches the server.
            if (itemType != ItemType.TinkeringMaterial)
                return false;

            return materialType == RerollMaterial || materialType == SwapMaterial;
        }

        public static bool IsModMaterial(WorldObject source) => source != null && IsModMaterial(source.ItemType, source.MaterialType);

        /// <summary>Which flow a material routes to. Anything else routes to neither.</summary>
        public static WeaponModAction ResolveAction(ItemType itemType, MaterialType? materialType)
        {
            if (!IsModMaterial(itemType, materialType))
                return WeaponModAction.None;

            return materialType == RerollMaterial ? WeaponModAction.Reroll : WeaponModAction.Swap;
        }

        public static WeaponModAction ResolveAction(WorldObject source) =>
            source == null ? WeaponModAction.None : ResolveAction(source.ItemType, source.MaterialType);

        /// <summary>TRUE if a salvage bag holds a FULL unit of material. A whole bag is the price of one use.</summary>
        public static bool IsFullBag(int? structure, int? maxStructure)
        {
            var max = maxStructure ?? DefaultMaxStructure;

            return max > 0 && (structure ?? 0) >= max;
        }

        public static bool IsFullBag(WorldObject source) => source != null && IsFullBag(source.Structure, source.MaxStructure);

        /// <summary>
        /// The whole rule set, expressed over plain values so it can be exercised without a live player, item or
        /// session.
        /// </summary>
        public static WeaponModRefusal ResolveRefusal(WeaponModAction action, WeaponClass weaponClass, bool hasWorkmanship,
            bool passesIntegrityGate, int numTimesTinkered, int specialCount, int reservedSlots)
        {
            if (action == WeaponModAction.None)
                return WeaponModRefusal.NotAModMaterial;

            if (weaponClass == WeaponClass.None)
                return WeaponModRefusal.NotAWeapon;

            if (!hasWorkmanship)
                return WeaponModRefusal.NoWorkmanship;

            // reservedSlots is imbues PLUS every log entry this system cannot reverse, so a weapon whose whole
            // budget is unreversible (ten Oak) is refused here rather than silently refilled on top
            if (WeaponModTinkerSet.AvailableSlots(reservedSlots) <= 0)
                return WeaponModRefusal.NoAvailableSlots;

            if (!passesIntegrityGate)
                return WeaponModRefusal.TinkerLogMismatch;

            if (action == WeaponModAction.Swap)
            {
                // requiring a full budget is what stops Amethyst being strictly better than the reroll on a
                // part-tinkered weapon
                if (numTimesTinkered != WeaponModRegistry.TotalSlots)
                    return WeaponModRefusal.SwapNeedsFullBudget;

                if (specialCount >= WeaponModRegistry.MaxSpecials)
                    return WeaponModRefusal.SwapAtSpecialCap;
            }

            return WeaponModRefusal.None;
        }

        /// <summary>
        /// The Prismatic Drift Stone refusal. An EXPLICIT check, deliberately not folded into
        /// <see cref="ResolveRefusal"/>'s value-only rule table because it has to read the weapon's log and
        /// imbue together.
        ///
        /// WHY IT EXISTS AT ALL. A drift-stoned weapon is refused today only by accident: the stone writes
        /// NumTimesTinkered 10 against a log of just MinBoosts+1 .. MaxBoosts+1 = 2..6 entries, so
        /// WeaponModTinkerSet.PassesIntegrityGate rejects it on the count mismatch. That is a coincidence of the
        /// stone's current tuning, not a guarantee - raising PrismaticDriftStone.MaxBoosts to 9 makes the log ten
        /// entries long, the gate passes, and a weapon the game promised "can never be tinkered again" becomes
        /// fully rerollable. This check keeps the promise regardless of how the stone is tuned.
        /// </summary>
        public static WeaponModRefusal ResolveDriftStoneRefusal(WorldObject target) =>
            PrismaticDriftStone.MatchesAppliedSignature(target) ? WeaponModRefusal.DriftStoneLocked : WeaponModRefusal.None;

        /// <summary>
        /// THE refusal decision for an action against a real weapon: the drift-stone check, then the value-only
        /// rule table with every argument read off the item. <see cref="VerifyUseRequirements"/> calls this, so
        /// nothing else needs to know which accessor feeds which argument.
        ///
        /// This overload exists because the wiring itself is the thing that goes wrong. A caller that assembles
        /// those seven arguments by hand can pass <see cref="WeaponModTinkerSet.ReadReservedImbueSlots"/> where
        /// the rule table needs <see cref="WeaponModTinkerSet.ReadReservedSlots"/> - imbues only, rather than
        /// imbues PLUS the log entries this system cannot reverse - and every ten-Oak weapon is then accepted
        /// instead of refused. The scenario suites' Refusal helper did exactly that, and agreed with itself while
        /// disagreeing with the server. There is now one wiring, and it is this one.
        /// </summary>
        public static WeaponModRefusal ResolveRefusal(WeaponModAction action, WorldObject target)
        {
            // mirrors VerifyUseRequirements' order: an unrecognised material never reaches the weapon's state
            if (action == WeaponModAction.None)
                return WeaponModRefusal.NotAModMaterial;

            if (target == null)
                return WeaponModRefusal.NotAWeapon;

            var refusal = ResolveDriftStoneRefusal(target);

            if (refusal != WeaponModRefusal.None)
                return refusal;

            return ResolveRefusal(action, WeaponClassifier.Classify(target), target.Workmanship != null,
                WeaponModTinkerSet.PassesIntegrityGate(target),
                target.GetProperty(PropertyInt.NumTimesTinkered) ?? 0,
                WeaponModTinkerSet.SpecialCount(target),
                WeaponModTinkerSet.ReadReservedSlots(target));
        }

        // ---------------- entry point ----------------

        /// <summary>
        /// The player uses a designated salvage bag on a weapon. There is no confirmed flag and no re-entry: the
        /// client has already asked and been answered before this is ever reached (see the class remarks), so
        /// this runs straight through the animation to the apply.
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

            var useError = VerifyUseRequirements(player, source, target, out var action);

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
                var stanceTime = player.SetCombatMode(CombatMode.NonCombat);
                actionChain.AddDelaySeconds(stanceTime);

                nextUseTime += stanceTime;
            }

            var currentStance = player.CurrentMotionState.Stance;

            var clapTime = Physics.Animation.MotionTable.GetAnimationLength(player.MotionTableId, currentStance, motionCommand);

            actionChain.AddAction(player, () => player.SendMotionAsCommands(motionCommand, currentStance));
            actionChain.AddDelaySeconds(clapTime);

            nextUseTime += clapTime;

            actionChain.AddAction(player, () =>
            {
                // TOCTOU GUARD - DO NOT DELETE. The player had the whole ClapHands animation to move, drop,
                // equip or destroy either item, so every requirement is checked a second time here, against
                // the state as it is NOW rather than as it was when the chain was queued.
                var reverifyError = VerifyUseRequirements(player, source, target, out var reverifiedAction);

                if (reverifyError != WeenieError.None)
                {
                    player.SendUseDoneEvent(reverifyError);
                    return;
                }

                HandleApply(player, source, target, reverifiedAction);
            });

            actionChain.AddAction(player, () => player.IsBusy = false);

            actionChain.EnqueueChain();

            player.NextUseTime = DateTime.UtcNow.AddSeconds(nextUseTime);
        }

        // ---------------- verification ----------------

        public static WeenieError VerifyUseRequirements(Player player, WorldObject source, WorldObject target, out WeaponModAction action)
        {
            action = WeaponModAction.None;

            if (!PropertyManager.GetBool("weapon_mods_enabled").Item)
                return WeenieError.YouDoNotPassCraftingRequirements;

            if (source == target)
            {
                player.SendTransientError($"You can't use the {source.Name} on itself.");
                return WeenieError.YouDoNotPassCraftingRequirements;
            }

            action = ResolveAction(source);

            if (action == WeaponModAction.None)
                return WeenieError.YouDoNotPassCraftingRequirements;

            // inventory only, never equipped: mutating a wielded weapon's ratings would desync the wielder's
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

            // one wiring, shared with every other caller - see the ResolveRefusal(action, target) overload
            var refusal = ResolveRefusal(action, target);

            switch (refusal)
            {
                case WeaponModRefusal.None:
                    return WeenieError.None;

                case WeaponModRefusal.NotAWeapon:
                    SendCraftMessage(player, $"The {target.Name} is not a weapon. Only melee weapons, missile weapons and casters can carry weapon mods.");
                    break;

                case WeaponModRefusal.NoWorkmanship:
                    SendCraftMessage(player, $"The {target.Name} has no workmanship, so it cannot hold a special modifier.");
                    break;

                case WeaponModRefusal.NoAvailableSlots:
                    SendCraftMessage(player, $"Every one of the {target.Name}'s slots is taken by work this salvage cannot undo. There is nothing left to work with.");
                    break;

                case WeaponModRefusal.DriftStoneLocked:
                    SendCraftMessage(player, $"The {target.Name} was finished by a Prismatic Drift Stone. Its tinkering is sealed and no salvage will reopen it.");
                    break;

                case WeaponModRefusal.TinkerLogMismatch:
                    SendCraftMessage(player, $"The {target.Name}'s tinker log does not account for everything applied to it, so its current tinkers cannot be safely removed.");
                    break;

                case WeaponModRefusal.SwapNeedsFullBudget:
                    SendCraftMessage(player, $"The {target.Name} is not fully tinkered. A swap trades one slot for another; it cannot fill an empty one.");
                    break;

                case WeaponModRefusal.SwapAtSpecialCap:
                    SendCraftMessage(player, $"The {target.Name} already carries three special modifiers. Only a full tourmaline reroll can change them now.");
                    break;

                default:
                    SendCraftMessage(player, $"The {source.Name} cannot be used on the {target.Name}.");
                    break;
            }

            action = WeaponModAction.None;

            return WeenieError.YouDoNotPassCraftingRequirements;
        }

        // ---------------- application ----------------

        /// <summary>
        /// Every condition under which <see cref="ApplyReroll"/> / <see cref="ApplySwap"/> would bail out and
        /// return null. Split out so the price can be taken BEFORE the mutation without risking a state where
        /// the bag is gone and nothing happened - see <see cref="HandleApply"/>.
        /// </summary>
        public static bool CanApply(WorldObject target, WeaponClass weaponClass, WeaponModAction action)
        {
            if (target == null || weaponClass == WeaponClass.None || action == WeaponModAction.None)
                return false;

            // ApplySwap draws a replacement tinker from this pool and cannot proceed without one
            if (WeaponTinkerTable.Pool(weaponClass).Count == 0)
                return false;

            if (action == WeaponModAction.Swap)
            {
                var specialCount = WeaponModTinkerSet.SpecialCount(target);

                // mirrors ApplySwap's defence-in-depth guard on the permanent three-special bound. Both halves
                // have to move together: if this one is forgotten the bag is consumed and the apply then refuses,
                // which is exactly the "bag eaten, nothing applied" hazard the consume-first ordering depends on
                // this method to make unreachable.
                if (specialCount >= WeaponModRegistry.MaxSpecials)
                    return false;

                // something this system owns has to exist for the swap to take away
                var removable = WeaponModTinkerSet.ReadComposition(target).Count + specialCount;

                if (removable <= 0)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// PRICE FIRST, THEN MUTATE (HARD). The bag is consumed BEFORE the weapon is touched.
        ///
        /// For a multi-charge salvage tool (the Hammers - see ACE.Server.Entity.SalvageTool) the price is one
        /// charge rather than the whole object, and the same reasoning holds unchanged: the decrement cannot
        /// fail, and the whole-object consume on the last charge fails only where it always did, before
        /// anything is removed.
        ///
        /// The sibling EquipmentModManager.HandleApply (:569-572) mutates, saves, and only then consumes, and a
        /// consume failure there leaves the item modified with the bag still in the player's pack - a free
        /// reroll. That ordering is not forced by anything: for a full-bag consume,
        /// TryConsumeFromInventoryWithNetworking's only failure is TryRemoveFromInventory returning false, which
        /// happens before it removes, destroys or renumbers anything (Player_Inventory.cs:158-168). So a failed
        /// consume here leaves BOTH the bag and the weapon exactly as they were, and there is nothing to roll
        /// back. <see cref="CanApply"/> takes every "the apply would refuse" condition before the consume, so
        /// the opposite hazard - bag eaten, nothing applied - is unreachable rather than merely unlikely.
        ///
        /// EquipmentModManager is deliberately NOT changed to match; it is out of this change's scope.
        /// </summary>
        private static void HandleApply(Player player, WorldObject source, WorldObject target, WeaponModAction action)
        {
            var weaponClass = WeaponClassifier.Classify(target);
            var workmanship = target.Workmanship ?? 0.0f;

            if (!CanApply(target, weaponClass, action))
            {
                log.Error($"WeaponModManager.HandleApply({player.Name}, {source.Name}, {target.Name}): {action} cannot be applied");
                player.SendUseDoneEvent(WeenieError.CraftGeneralErrorNoUiMsg);
                return;
            }

            // the source may be destroyed by the consume below, so its name is captured first
            var sourceName = source.Name;
            var wasSalvageTool = SalvageTool.IsSalvageTool(source);

            // the price, taken before the weapon is touched. A failure here has changed nothing at all.
            if (!SalvageTool.TryConsume(player, source, out var remaining))
            {
                log.Error($"WeaponModManager.HandleApply({player.Name}, {source.Name}, {target.Name}): failed to consume the salvage bag");
                player.SendUseDoneEvent(WeenieError.CraftGeneralErrorNoUiMsg);
                return;
            }

            var lines = action == WeaponModAction.Reroll
                ? ApplyReroll(target, weaponClass, workmanship)
                : ApplySwap(target, weaponClass, workmanship);

            if (lines == null)
            {
                // unreachable: CanApply covers every bail-out condition in both flows. If it ever fires, the bag
                // is already gone, so say so loudly rather than failing quietly.
                log.Error($"WeaponModManager.HandleApply({player.Name}, {source.Name}, {target.Name}): {action} produced no result AFTER the bag was consumed - CanApply is out of sync with the apply path");
                player.SendUseDoneEvent(WeenieError.CraftGeneralErrorNoUiMsg);
                return;
            }

            target.ChangesDetected = true;
            target.SaveBiotaToDatabase();

            UpdateObj(player, target);

            SendCraftMessage(player, action == WeaponModAction.Reroll
                ? $"The salvage reforges your {target.Name}."
                : $"The salvage reworks a single slot on your {target.Name}.");

            foreach (var line in lines)
                SendCraftMessage(player, $"- {line}");

            var chargeMessage = SalvageTool.GetChargeMessage(sourceName, wasSalvageTool, remaining);

            if (chargeMessage != null)
                SendCraftMessage(player, chargeMessage);

            player.SendUseDoneEvent();
        }

        /// <summary>
        /// The Tourmaline reroll. Reverses everything the weapon currently carries from this system, then refills
        /// the whole budget: imbues keep their slots, the special count is rolled from the cumulative odds and
        /// HARD-clamped, and the remainder is drawn uniformly with replacement from the class material pool.
        /// </summary>
        public static List<string> ApplyReroll(WorldObject target, WeaponClass weaponClass, double workmanship)
        {
            if (target == null || weaponClass == WeaponClass.None)
                return null;

            // 0. read the budget BEFORE anything is reversed: the slots this system cannot account for, and the
            //    log entries that own them, both of which have to survive the rewrite intact
            var reserved = WeaponModTinkerSet.ReadReservedSlots(target);
            var preserved = WeaponModTinkerSet.ReadUnaccountedEntries(target);

            // 1. reverse the current set - the layer 1 tinkers this system knows how to reverse, and every
            //    recorded special magnitude (records cleared with RemoveProperty, never SetProperty(0))
            WeaponModTinkerSet.ReverseTinkers(target, WeaponModTinkerSet.ReadComposition(target));
            WeaponModTinkerSet.ClearSpecials(target);

            // 2. imbues AND unreversible log entries keep their slots and are never touched

            // 3. specials: rolled, then clamped to min(3, 10 - reserved)
            var specialCount = WeaponModRoller.ClampSpecialCount(WeaponModRoller.RollSpecialCount(), reserved);
            var drawn = WeaponModRoller.RollDistinctSpecials(weaponClass, specialCount);

            // 3a. resolve every magnitude BEFORE the budget is split, and DROP any that resolves to zero. A
            //     zero-magnitude special is not applied at all: it writes no record and consumes no slot, so its
            //     slot converts to a tinker below. This is what makes weapon_mod_magnitude_scale = 0 read as
            //     "mute the layer" (all ten slots to tinkers) rather than burning three slots on 0.0 records in
            //     the reserved band - see WeaponModValue.IsLiveMagnitude.
            var specials = new List<(WeaponModDefinition Definition, double Magnitude)>();

            foreach (var definition in drawn)
            {
                var magnitude = WeaponModValue.Roll(definition, workmanship);

                if (WeaponModValue.IsLiveMagnitude(definition, magnitude))
                    specials.Add((definition, magnitude));
            }

            // 4. the remainder is layer 1. The tinker count is derived from the specials ACTUALLY APPLIED, never
            //    from the count that was rolled, or a dropped special would take its slot to the grave with it
            //    and the budget would come up short of ten.
            var tinkerCount = WeaponModTinkerSet.ComputeTinkerCount(reserved, specials.Count);
            var tinkers = WeaponModRoller.RollTinkers(weaponClass, tinkerCount);

            // 5. apply
            WeaponModTinkerSet.ApplyTinkers(target, tinkers);

            var lines = new List<string>();

            foreach (var (definition, magnitude) in specials)
            {
                WeaponModTinkerSet.ApplySpecial(target, definition, magnitude);

                lines.Add(WeaponModDisplay.Describe(definition, magnitude));
            }

            // 6. NumTimesTinkered = 10, WeaponModTinkerCount, and BOTH logs REPLACED - with the unaccounted
            //    entries carried forward first, so their slots stay reserved on the next use too
            WeaponModTinkerSet.WriteComposition(target, preserved, tinkers);

            var composition = WeaponModDisplay.DescribeComposition(tinkers);

            if (composition.Length > 0)
                lines.Add($"Tinkers: {composition}");

            return lines;
        }

        /// <summary>
        /// The Amethyst swap: remove ONE random filled slot (tinker or special, never an imbue), then add ONE
        /// random modifier - a special with probability weapon_mod_swap_special_chance, otherwise a random class
        /// tinker.
        ///
        /// Both halves are random by design. It improves the RATE at which specials arrive, never their identity,
        /// and it cannot reroll an existing special's magnitude in place. Those are what make it a gamble rather
        /// than a ratchet, and the design's ceiling is priced on it.
        /// </summary>
        public static List<string> ApplySwap(WorldObject target, WeaponClass weaponClass, double workmanship)
        {
            if (target == null || weaponClass == WeaponClass.None)
                return null;

            var tinkers = WeaponModTinkerSet.ReadComposition(target);
            var specials = WeaponModTinkerSet.ReadSpecials(target);

            // DEFENCE IN DEPTH - DO NOT DELETE THIS AS REDUNDANT. A weapon already holding the full trio is
            // refused here as well as by ResolveRefusal's SwapAtSpecialCap. Without it the swap would remove one
            // of the three and roll a replacement, which IS the "reroll a single special" capability design
            // section 5 says invalidates its power assessment ("Remove either property and the pricing in this
            // section stops holding"): the +101.1% ceiling is priced on a perfect trio being a lottery, and a
            // weapon that can retry one bad special at a fixed bag cost turns it into a grind. A bound that
            // load-bearing does not get a single enforcement point, one refactor away from being bypassed.
            //
            // Shaped as a bail-out like the ones below so CanApply can - and does - mirror it exactly.
            if (specials.Count >= WeaponModRegistry.MaxSpecials)
                return null;

            // slots this system cannot reverse are never candidates for removal and never dropped from the log
            var preserved = WeaponModTinkerSet.ReadUnaccountedEntries(target);

            var removable = tinkers.Count + specials.Count;

            if (removable <= 0)
                return null;

            var lines = new List<string>();

            // ---- remove one random filled slot ----
            var index = WeaponModRoller.NextIndex(removable);

            // set only when the removed slot was a TINKER, and then taken out of the replacement draw below.
            // Null whenever a special was removed, because the exclusion is deliberately confined to the
            // tinker-replaces-tinker path.
            MaterialType? removedMaterial = null;

            if (index < tinkers.Count)
            {
                var material = tinkers[index];

                if (WeaponTinkerTable.TryGet(material, out var removedTinker))
                {
                    removedTinker.Reverse(target, 1);
                    lines.Add($"Lost: 1 {removedTinker.DisplayName} tinker");
                }

                removedMaterial = material;

                tinkers.RemoveAt(index);
            }
            else
            {
                var (removedSpecial, _) = specials[index - tinkers.Count];

                WeaponModTinkerSet.ReverseSpecial(target, removedSpecial);
                lines.Add($"Lost: {removedSpecial.DisplayName}");
            }

            // ---- add one random modifier ----
            var held = WeaponModTinkerSet.ReadSpecials(target).Select(s => s.Definition.Id).ToList();

            var wantsSpecial = held.Count < WeaponModRegistry.MaxSpecials
                && WeaponModRoller.NextUnit() < PropertyManager.GetDouble("weapon_mod_swap_special_chance").Item;

            // a draw that lands on a special the weapon already holds redraws among the ones it does not - a
            // weapon never holds the same special twice
            var addedSpecial = wantsSpecial ? WeaponModRoller.RollSpecial(weaponClass, held) : null;
            var addedMagnitude = addedSpecial == null ? 0.0 : WeaponModValue.Roll(addedSpecial, workmanship);

            // a special that resolves to zero magnitude is not applied and does not take the slot - the draw
            // falls through to a tinker instead, so the swap still trades exactly one slot for exactly one
            // modifier and never writes a dead record into the reserved band (WeaponModValue.IsLiveMagnitude)
            if (addedSpecial != null && !WeaponModValue.IsLiveMagnitude(addedSpecial, addedMagnitude))
                addedSpecial = null;

            if (addedSpecial != null)
            {
                WeaponModTinkerSet.ApplySpecial(target, addedSpecial, addedMagnitude);

                lines.Add($"Gained: {WeaponModDisplay.Describe(addedSpecial, addedMagnitude)}");
            }
            else
            {
                // NEVER hand back the tinker just taken away. A swap that reports "Lost: 1 Granite tinker /
                // Gained: 1 Granite tinker" spends a whole salvage bag on a guaranteed no-op, which is a bad
                // deal and reads as a bug. removedMaterial is null when a SPECIAL was removed, so that path is
                // untouched, and it is null again when the removed tinker is somehow not in this table. The
                // draw itself can never fail on a non-empty pool - see WeaponModRoller.RollTinker's degenerate
                // fallback, which is what keeps CanApply's promise that the apply cannot refuse after the bag
                // has been consumed.
                var material = WeaponModRoller.RollTinker(weaponClass, removedMaterial);

                if (material == null)
                    return null;

                if (WeaponTinkerTable.TryGet(material.Value, out var addedTinker))
                {
                    addedTinker.Apply(target, 1);
                    lines.Add($"Gained: 1 {addedTinker.DisplayName} tinker");
                }

                tinkers.Add(material.Value);
            }

            // NumTimesTinkered stays 10, and both logs are rewritten to the post-swap composition, unaccounted
            // entries first so their slots stay reserved
            WeaponModTinkerSet.WriteComposition(target, preserved, tinkers);

            return lines;
        }

        // ---------------- plumbing ----------------

        private static void SendCraftMessage(Player player, string message)
        {
            player.Session.Network.EnqueueSend(new GameMessageSystemChat(message, ChatMessageType.Craft));
        }

        /// <summary>
        /// Sends an UpdateObj for the modified target, mirroring RecipeManager.UpdateObj. The equipped branch of
        /// that helper is omitted: targets here are verified inventory-only by construction.
        /// </summary>
        private static void UpdateObj(Player player, WorldObject target)
        {
            player.EnqueueBroadcast(new GameMessageUpdateObject(target));

            if (player.FindObject(target.Guid.Full, Player.SearchLocations.MyInventory) != null)
                player.MoveItemToFirstContainerSlot(target);
        }
    }
}
