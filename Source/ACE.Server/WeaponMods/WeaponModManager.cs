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

            // REMOVED 2026-08-07 (repo-owner directive), and deliberately NOT re-addable without revisiting the
            // ruling behind them:
            //
            //   SwapNeedsFullBudget - required NumTimesTinkered == 10. "Traditional tinkers should have no
            //                         bearing" on Amethyst, and since the 2026-08-06 rework it does not touch
            //                         tinkers at all, so gating it on the tinker counter gated it on something
            //                         it neither reads nor writes.
            //   SwapNeedsASpecial   - required at least one held special. Amethyst now ADDS one when the
            //                         weapon holds none, so there is no such thing as nothing to work with.
            //
            // Both are gone rather than kept-but-unreachable: an enum member no rule can return is a rule a
            // future reader will restore by accident.
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
            bool passesIntegrityGate, int reservedSlots)
        {
            if (action == WeaponModAction.None)
                return WeaponModRefusal.NotAModMaterial;

            if (weaponClass == WeaponClass.None)
                return WeaponModRefusal.NotAWeapon;

            if (!hasWorkmanship)
                return WeaponModRefusal.NoWorkmanship;

            // THE TWO TINKER GATES ARE REROLL-ONLY as of 2026-08-07 (repo-owner directive: "traditional tinkers
            // should have no bearing" on Amethyst).
            //
            // Both describe the state of the TINKER budget - whether there is room in it, and whether the log
            // accounting for it can be trusted. Tourmaline still cares because ApplyReroll runs against a
            // weapon whose tinker composition it is preserving and echoing. Amethyst neither reads nor writes
            // a tinker, a log or the counter, so gating it on any of them gated it on state it cannot touch.
            //
            // NOTE THE DELIBERATE ASYMMETRY: a ten-Oak or log-mismatched weapon is now Amethyst-able while
            // still being refused by Tourmaline. That is the directive as given, not an oversight.
            if (action == WeaponModAction.Reroll)
            {
                // reservedSlots is imbues PLUS every log entry this system cannot reverse, so a weapon whose
                // whole budget is unreversible (ten Oak) is refused here rather than silently refilled on top
                if (WeaponModTinkerSet.AvailableSlots(reservedSlots) <= 0)
                    return WeaponModRefusal.NoAvailableSlots;

                if (!passesIntegrityGate)
                    return WeaponModRefusal.TinkerLogMismatch;
            }

            // NO SWAP-SPECIFIC REFUSAL REMAINS. Amethyst applies to any workmanship-bearing weapon that is not
            // drift-stone locked: it ADDS a special when the weapon holds none, and rerolls one when it holds
            // any number including the cap. The two conditions that used to sit here - a full tinker budget,
            // and at least one held special - were both removed on 2026-08-07. See the WeaponModRefusal
            // remarks for why they are deleted rather than kept unreachable.
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

            if (action == WeaponModAction.Reroll)
            {
                // REWORKED 2026-08-07 to a special-only reroll: it no longer refills the tinker budget, so the
                // material-pool check that used to guard this branch is gone with it. What it needs instead is
                // the same thing the swap needs - a class pool with at least one row to draw from. Unlike the
                // swap it does NOT require an existing special, because this is the only path to a first one.
                return WeaponModRegistry.Pool(weaponClass).Count > 0;
            }

            // Swap: the special count no longer gates anything (2026-08-07). Amethyst ADDS a special to a
            // weapon holding none and rerolls one on a weapon holding any number up to the cap, so every
            // count from 0 to MaxSpecials is a working case rather than a refusal.
            //
            // The class pool still has to hold at least one row: ApplySwap's replacement draw degrades to "no
            // replacement" rather than throwing when every row is already held, but a wholly empty pool means
            // the swap cannot even ATTEMPT one, which is the bail-out this has to mirror.
            return WeaponModRegistry.Pool(weaponClass).Count > 0;
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

            // Captured BEFORE the roll: the tier transition is what drives the aura and the announcement,
            // not the tier itself. Re-sending on an unchanged tier would add a SECOND emitter rather than
            // replacing the first (see VisualEffectManager), so "still Exceptional" must be a no-op.
            var tierBefore = WeaponQualityTiers.Evaluate(target);

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

            HandleTierTransition(player, target, tierBefore);

            target.ChangesDetected = true;
            target.SaveBiotaToDatabase();

            UpdateObj(player, target);

            // MUST come after UpdateObj. That refresh rebuilds the object client-side and takes any
            // running particle emitter with it, so a script sent before it is simply discarded - which is
            // exactly why the aura never appeared while the tier messages read correctly.
            ResetAuraForRebuild(player, target);

            SendCraftMessage(player, action == WeaponModAction.Reroll
                ? $"The salvage reworks every special modifier on your {target.Name}."
                : $"The salvage reworks a single special modifier on your {target.Name}.");

            foreach (var line in lines)
                SendCraftMessage(player, $"- {line}");

            var chargeMessage = SalvageTool.GetChargeMessage(sourceName, wasSalvageTool, remaining);

            if (chargeMessage != null)
                SendCraftMessage(player, chargeMessage);

            player.SendUseDoneEvent();
        }

        /// <summary>
        /// The Tourmaline reroll: SPECIAL-ONLY as of 2026-08-07 (repo-owner directive). It rerolls the whole
        /// special set - how many, which ones, and at what magnitude - and touches NOTHING else.
        ///
        /// It no longer reforges layer 1. The player's chosen tinker composition, the imbue, NumTimesTinkered
        /// and both logs all survive untouched. That is the point: the traditional crafting process stays
        /// exactly as it was, and this system becomes a layer ON TOP of it rather than a replacement that
        /// gambles away a deliberately-built tinker base every time a player wants a different special.
        ///
        /// THIS SUPERSEDES THE PRE-2026-08-07 SHAPE, which reversed every known tinker and redrew the entire
        /// budget uniformly from the class material pool. Because of that, this path no longer needs a material
        /// pool at all, and the "cannot proceed without one" refusal that guarded it is correspondingly gone.
        ///
        /// Division of labour with the other bag: Amethyst rerolls exactly ONE held special; this rerolls the
        /// entire set including its size, and is the only way to gain a FIRST special.
        /// </summary>
        public static List<string> ApplyReroll(WorldObject target, WeaponClass weaponClass, double workmanship)
        {
            if (target == null || weaponClass == WeaponClass.None)
                return null;

            // 1. clear the specials the weapon currently carries. Each native property is walked back by its
            //    recorded magnitude and the record itself is REMOVED, never SetProperty(0). Layer 1 is not
            //    touched, so ReverseTinkers is deliberately NOT called here any more.
            WeaponModTinkerSet.ClearSpecials(target);

            // 2. roll a fresh set. Since the 2026-08-06 decoupling the clamp does not consider the tinker
            //    budget - specials draw on their own allowance, so the count is independent of how heavily
            //    the player has tinkered the weapon.
            var specialCount = WeaponModRoller.ClampSpecialCount(WeaponModRoller.RollSpecialCount());
            var drawn = WeaponModRoller.RollDistinctSpecials(weaponClass, specialCount);

            var lines = new List<string>();

            foreach (var definition in drawn)
            {
                // The roll produces a FRACTION - potency scaled by workmanship - and that is what a Tier B row
                // stores. The magnitude is resolved from it for the liveness test and the craft line; a Tier A
                // row stores that magnitude instead. WeaponModTinkerSet.ApplySpecialAtFraction owns the split.
                var fraction = WeaponModValue.RollFraction(WeaponModRoller.RollPotency(definition), workmanship);
                var scale = WeaponModValue.MagnitudeScale();
                var magnitude = WeaponModValue.MagnitudeFromFraction(definition, fraction, scale);

                // A magnitude that resolves to zero is not applied at all - no record is written. Since the
                // decoupling there is no slot for it to hand back either, so it simply does not appear. This
                // is what makes weapon_mod_magnitude_scale = 0 read as "mute the layer" rather than burning
                // slots on 0.0 records. See WeaponModValue.IsLiveMagnitude.
                if (!WeaponModValue.IsLiveMagnitude(definition, magnitude))
                    continue;

                WeaponModTinkerSet.ApplySpecialAtFraction(target, definition, fraction, scale);

                lines.Add(WeaponModDisplay.Describe(definition, magnitude, scale));
            }

            if (lines.Count == 0)
                lines.Add("No special modifier took hold.");

            // 3. NumTimesTinkered, WeaponModTinkerCount and both logs are left EXACTLY as they were - this path
            //    no longer writes a composition, because it no longer changes one. The tinkers are echoed only
            //    so the player can see the whole weapon in one message.
            var composition = WeaponModDisplay.DescribeComposition(WeaponModTinkerSet.ReadComposition(target));

            if (composition.Length > 0)
                lines.Add($"Tinkers (unchanged): {composition}");

            return lines;
        }

        /// <summary>
        /// The Amethyst swap: REWORKED 2026-08-06 (repo-owner directive, separately approved) into a
        /// SPECIAL-ONLY REROLL. It removes ONE random held special and replaces it with a fresh one drawn from
        /// the full class pool, distinct from whatever specials remain. It NEVER touches tinkers, and NEVER
        /// applies the damage-first guarantee (weapon_mod_guarantee_damage_special) - that guarantee is
        /// drop/reroll-only, an explicit ruling, so a swap replacement can land on a utility special same as
        /// any other draw.
        ///
        /// THIS SUPERSEDES THE PRE-2026-08-06 SHAPE, which traded one random filled slot (tinker OR special)
        /// for one random modifier (a special at weapon_mod_swap_special_chance, else a tinker) and refilled
        /// the tinker budget to compensate. That shape is gone along with the tunable's role in it -
        /// weapon_mod_swap_special_chance no longer applies on THIS path; a reroll always replaces,
        /// unconditionally. See Docs/WeaponMods/DESIGN.md section 8 for the updated Amethyst description.
        ///
        /// SwapAtSpecialCap IS GONE, and that is the point of the rework: rerolling a special AT the cap is now
        /// the tool's whole purpose, where before the cap made it a strictly worse way to gain a special that
        /// the reroll or a below-cap swap already offered.
        ///
        /// AS OF 2026-08-07 IT HAS NO PRECONDITION ON THE WEAPON'S STATE AT ALL (repo-owner directive:
        /// "Amethyst should be able to be applied basically always... traditional tinkers should have no
        /// bearing"). Both conditions that used to gate it are gone - a full tinker budget, and holding at
        /// least one special. The behaviour is now uniform across every special count:
        ///
        ///   0 specials       -> ADDS one. Amethyst is a second route to a first special, not just Tourmaline.
        ///   1..cap specials  -> removes one at random and draws a replacement. The count does not change.
        ///
        /// So the count only ever moves 0 -> 1, and never grows past that. What remains outside this method:
        /// the weapon must be a workmanship-bearing weapon and must not be drift-stone locked.
        /// </summary>
        public static List<string> ApplySwap(WorldObject target, WeaponClass weaponClass, double workmanship)
        {
            if (target == null || weaponClass == WeaponClass.None)
                return null;

            var specials = WeaponModTinkerSet.ReadSpecials(target);

            var lines = new List<string>();

            // ---- remove one random held special, IF the weapon holds any ----
            //
            // A ZERO-SPECIAL WEAPON IS NOT A REFUSAL as of 2026-08-07 (repo-owner directive): Amethyst adds a
            // first special rather than turning the player away, so this whole step is simply skipped and the
            // draw below becomes a pure addition. That makes Amethyst a second route to a first special
            // alongside Tourmaline, which was previously the only one.
            if (specials.Count > 0)
            {
                var index = WeaponModRoller.NextIndex(specials.Count);
                var (removedSpecial, _) = specials[index];

                WeaponModTinkerSet.ReverseSpecial(target, removedSpecial);
                lines.Add($"Lost: {removedSpecial.DisplayName}");
            }

            // ---- roll a replacement, distinct from whatever specials remain ----
            //
            // The removed special is NOT excluded from this draw - "distinct from those STILL HELD" means the
            // remaining ones only, so a reroll can legitimately land back on the identity it just lost.
            var stillHeld = WeaponModTinkerSet.ReadSpecials(target).Select(s => s.Definition.Id).ToList();

            // the plain 2-arg overload - NOT the damageOnly one - so no damage-first guarantee applies here,
            // per the explicit ruling that the guarantee is drop/reroll-only
            var addedSpecial = WeaponModRoller.RollSpecial(weaponClass, stillHeld);
            var addedScale = WeaponModValue.MagnitudeScale();
            var addedFraction = addedSpecial == null
                ? 0.0
                : WeaponModValue.RollFraction(WeaponModRoller.RollPotency(addedSpecial), workmanship);
            var addedMagnitude = addedSpecial == null
                ? 0.0
                : WeaponModValue.MagnitudeFromFraction(addedSpecial, addedFraction, addedScale);

            // a special that resolves to zero magnitude is not applied - the removed slot is simply lost this
            // use, same rule as everywhere else a rolled magnitude can land on zero
            // (WeaponModValue.IsLiveMagnitude)
            if (addedSpecial != null && !WeaponModValue.IsLiveMagnitude(addedSpecial, addedMagnitude))
                addedSpecial = null;

            if (addedSpecial != null)
            {
                WeaponModTinkerSet.ApplySpecialAtFraction(target, addedSpecial, addedFraction, addedScale);

                lines.Add($"Gained: {WeaponModDisplay.Describe(addedSpecial, addedMagnitude, addedScale)}");
            }

            // Reachable only on a weapon that held NOTHING and drew nothing - an empty class pool, or a
            // magnitude muted to zero by weapon_mod_magnitude_scale. Before the zero-special case was allowed
            // through, "Lost:" was always present and this list could never be empty. An empty list is not
            // null, so HandleApply does not log it as a CanApply desync; it just leaves the player with no
            // message at all, which is why this line exists. Mirrors ApplyReroll's equivalent.
            if (lines.Count == 0)
                lines.Add("No special modifier took hold.");

            // tinkers, NumTimesTinkered and both logs are UNTOUCHED - this path never reaches them at all, so
            // there is no WriteComposition call here, deliberately

            return lines;
        }

        // ---------------- plumbing ----------------

        private static void SendCraftMessage(Player player, string message)
        {
            player.Session.Network.EnqueueSend(new GameMessageSystemChat(message, ChatMessageType.Craft));
        }

        /// <summary>
        /// Grants or removes the quality-tier aura after a reroll or swap, and announces a gain in teal.
        ///
        /// Only a CHANGE of tier does anything. Two reasons, and the first is a correctness constraint
        /// rather than tidiness: a sent script allocates a fresh particle emitter every time (handle 0),
        /// and those emitters are endless, so re-sending on a weapon that was already Exceptional would
        /// leave two running and the aura would visibly brighten with each craft.
        ///
        /// LOSS IS NOT INSTANT ON THE CLIENT. A handle-0 emitter cannot be stopped by id -
        /// ParticleManager.StopParticleEmitter and DestroyParticleEmitter both refuse handle 0 - so the
        /// only thing that clears one is the client rebuilding the object. Clearing the property stops
        /// it coming back, and it disappears on the next rebuild (relog, zoning, re-equip). Whether the
        /// craft's own UpdateObj round-trip counts as a rebuild is NOT established; if it does, the aura
        /// vanishes immediately and this comment is merely pessimistic.
        /// </summary>
        private static void HandleTierTransition(Player player, WorldObject target, WeaponQualityTier tierBefore)
        {
            var tierAfter = WeaponQualityTiers.Evaluate(target);

            if (tierAfter == tierBefore)
                return;

            var scriptBefore = WeaponQualityTiers.ScriptFor(tierBefore);
            var scriptAfter = WeaponQualityTiers.ScriptFor(tierAfter);

            if (scriptAfter != scriptBefore)
                target.VisualEffectScript = scriptAfter;

            var total = WeaponQualityTiers.TotalIntensity(target);

            if (tierAfter > tierBefore)
            {
                // Teal. ChatMessageType.Advancement (0x0D) is the only true teal the client has - see the
                // verified palette catalog in ChatMessageType.cs, whose doc comments are otherwise wrong.
                player.Session.Network.EnqueueSend(new GameMessageSystemChat(
                    $"{WeaponQualityTiers.NameFor(tierAfter)}! Your {target.Name} totals {total}% across its modifiers and takes on a visible aura.",
                    ChatMessageType.Advancement));
            }
            else if (tierBefore != WeaponQualityTier.None)
            {
                var lost = WeaponQualityTiers.NameFor(tierBefore);

                SendCraftMessage(player, tierAfter == WeaponQualityTier.None
                    ? $"Your {target.Name} falls to {total}%, below the {lost} threshold, and its aura fades."
                    : $"Your {target.Name} falls to {total}%, down from {lost} to {WeaponQualityTiers.NameFor(tierAfter)}.");
            }
        }

        /// <summary>
        /// Clears this client's "already has an effect" record for the crafted weapon, so the aura is sent
        /// afresh the next time the weapon is actually drawn.
        ///
        /// It deliberately does NOT send anything. A tinker target is inventory-only by construction (see
        /// UpdateObj above), so at this moment the weapon is in the pack, where the client knows about it
        /// but does not render it. Sending there attaches the script to nothing AND consumes the single
        /// send that equipping it later depends on, leaving the weapon permanently bare - the exact failure
        /// that made this look non-deterministic.
        ///
        /// Equipping is the natural and sufficient trigger: TryEquipObjectWithNetworking calls SendTo, which
        /// reads the property this craft just set or cleared. So a weapon that gained a tier lights up when
        /// worn, and one that lost it stays dark, with no timing involved.
        ///
        /// An earlier version delayed three seconds and then sent. That appeared to work only because the
        /// delay gave the player time to re-equip, so the send sometimes landed on a drawn weapon - and it
        /// risked a SECOND emitter whenever the equip path had already sent one inside the window.
        /// </summary>
        private static void ResetAuraForRebuild(Player player, WorldObject target)
        {
            if (player?.Session == null || target == null)
                return;

            VisualEffectManager.Forget(player.Session, target);
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
