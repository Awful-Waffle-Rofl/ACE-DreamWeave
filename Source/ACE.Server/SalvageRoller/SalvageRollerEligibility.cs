using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Database;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Entity.AccountVault;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.Pvp.Templates;
using ACE.Server.SpellReroll;
using ACE.Server.WeaponMods;
using ACE.Server.WorldObjects;

namespace ACE.Server.SalvageRoller
{
    /// <summary>
    /// Whether a sell basket may become a Salvage Roller batch (Docs/SalvageRoller/DESIGN.md 5.3). ONE function,
    /// <see cref="VerifyBasket"/>, run by the window's sell hook before anything is detached. It sends nothing: the
    /// caller shows <c>refusal</c>.
    ///
    /// The order is DESIGN.md 5.3's: the switch, the basket's shape, material routing, the manual entry checks, the
    /// fuel, the target, and last the account state (no open batch, escrow loaded). The first failure wins and the
    /// WHOLE basket is refused (owner ruling 8): nothing is trimmed or handed back piecemeal.
    ///
    /// TARGET ELIGIBILITY IS THE MANUAL PATH'S, word for word. The target's inventory rule and its state rules come
    /// from the same manager functions the bag paths call (step 4's extraction), so an item a bag would refuse is
    /// refused here with the same line, and one a bag would accept is accepted. That includes Retained and Attuned
    /// targets: nothing on the manual path checks either (DESIGN.md section 4), so nothing here does.
    ///
    /// The checks that read a live Player go through <see cref="Actor"/>, which production builds from the Player, so
    /// the rules run under the test harness, which cannot construct one.
    /// </summary>
    public static class SalvageRollerEligibility
    {
        // ---------------- refusal text (the Salvage Roller's own; the manual path has no equivalent) ----------------

        public const string DisabledMessage = "The Salvage Roller is not taking new work right now.";

        public const string LinesDroppedMessage = "Something in that sale could not be taken, so nothing was. Offer the item and its Hammers again.";

        public const string NoTargetMessage = "Offer one item to rework, together with the Hammers to spend on it.";

        public const string TooManyTargetsMessage = "The Salvage Roller works one item at a time. Offer a single item with its Hammers.";

        public const string MixedMaterialsMessage = "Every Hammer in one batch must be of the same material.";

        public const string TigerEyeMessage = "The Salvage Roller does not work tiger eye.";

        public const string UnsupportedMaterialMessage = "The Salvage Roller works only tourmaline, amethyst, obsidian, serpentine and green jade.";

        public const string BusyMessage = "You are too busy to use the Salvage Roller right now.";

        public const string PeaceModeMessage = "You must be in peace mode to use the Salvage Roller.";

        public const string BatchReadFailedMessage = "The Salvage Roller cannot read your batch right now. Try again in a moment.";

        public const string BatchExistsMessage = "You already have a batch at the Salvage Roller. Take one of its results first.";

        public static string NoHammersMessage(string targetName) => $"Offer full Hammers with the {targetName}. Each charge is one roll.";

        public static string NotAHammerMessage(string name) => $"The {name} is not a Hammer. The Salvage Roller takes full Hammers only, not salvage bags.";

        public static string NotFullMessage(string name) => $"The {name} is not full. The Salvage Roller takes full Hammers only.";

        public static string OverCapMessage(int cap, int offered) =>
            cap % 10 == 0
                ? $"The Salvage Roller takes at most {cap} rolls ({cap / 10} Hammers); you offered {offered}."
                : $"The Salvage Roller takes at most {cap} rolls; you offered {offered}.";

        public static string SwitchedOffMessage(MaterialType material) => $"The Salvage Roller cannot work {MaterialName(material)} right now.";

        /// <summary>The manual path's fuel inventory line (WeaponModManager / SpellRerollManager VerifyUseRequirements).</summary>
        public static string FuelNotInInventoryMessage(string name) => $"The {name} must be in your inventory.";

        /// <summary>
        /// The manual path's target inventory line, identical in all three managers' TargetInventoryRefusal
        /// (SalvageRollerEligibilityTests pins the four copies against each other).
        /// </summary>
        public static string TargetNotInInventoryMessage(string name) => $"You must remove the {name} and place it in your inventory first.";

        // ---------------- tunables ----------------

        /// <summary>salvage_roller_max_candidates, clamped to 1..<see cref="SalvageRollerEngine.MaxRolls"/>.</summary>
        public static int MaxRolls()
        {
            var value = PropertyManager.GetLong("salvage_roller_max_candidates", SalvageRollerEngine.MaxRolls).Item;

            return (int)Math.Clamp(value, 1, SalvageRollerEngine.MaxRolls);
        }

        /// <summary>The number of rolls a set of full Hammers pays for: one per charge (owner ruling 7).</summary>
        public static int RollCount(IEnumerable<WorldObject> hammers) => hammers?.Sum(h => SalvageTool.GetRemaining(h)) ?? 0;

        // ---------------- seams for the checks that read the database or the account store ----------------

        /// <summary>
        /// Reads whether a character has an open batch: (read succeeded, batch exists). A failed read refuses the
        /// basket, because "could not read" must never pass for "no batch". Test seam.
        /// </summary>
        internal static Func<uint, (bool Ok, bool Exists)> BatchProbe = characterId =>
        {
            var ok = DatabaseManager.Shard.BaseDatabase.TryGetSalvageBatchForCharacter(characterId, out var batch);
            return (ok, batch != null);
        };

        /// <summary>Whether the account's escrow store and containers are loaded and usable, with its refusal. Test seam.</summary>
        internal static Func<uint, (bool Ready, string Reason)> EscrowProbe = accountId => ProbeEscrow(AccountVaultManager.GetStore(accountId));

        /// <summary>
        /// The sale's refusal while the escrow's first load is still arriving. The sale is refused here, before anything
        /// is detached, so nothing has been taken; the station's Use starts the load, so this is normally gone by the
        /// time a player has dragged their basket in. Provisional text: the owner reviews it.
        /// </summary>
        public const string EscrowLoadingMessage = "The Salvage Roller is still loading your account's escrow. Nothing was taken; sell again in a moment.";

        /// <summary>
        /// <see cref="EscrowProbe"/>'s body over one store. Not ready while the index has been read cleanly is a load in
        /// progress (the escrow containers' inventories arrive asynchronously) and gets <see cref="EscrowLoadingMessage"/>;
        /// a failed read and a degraded escrow keep the store's own lines, because neither is a wait.
        /// </summary>
        internal static (bool Ready, string Reason) ProbeEscrow(AccountVaultStore store)
        {
            if (store == null)
                return (false, AccountVaultStore.EscrowUnavailableMessage);

            if (store.IsEscrowReady(out var reason))
                return (true, null);

            if (reason == AccountVaultStore.EscrowUnavailableMessage && store.IsIndexLoaded && !store.LastLoadFailed)
                return (false, EscrowLoadingMessage);

            return (false, reason ?? AccountVaultStore.EscrowUnavailableMessage);
        }

        /// <summary>
        /// TinkerLock's refusal for a source on a target, or null. Test seam: production is the manual path's own
        /// TinkerLock.IsRefused / RefusalMessage pair, which reads the world cook_book for a locked target.
        /// </summary>
        internal static Func<WorldObject, WorldObject, string> TinkerLockRefusal = (source, target) =>
            TinkerLock.IsRefused(source, target) ? TinkerLock.RefusalMessage(source, target) : null;

        /// <summary>
        /// The Serpentine target-state rule. Test seam: production is SpellRerollManager.TargetStateRefusal itself,
        /// which plans against the world database's weenie and loot tables and so cannot run under the harness.
        /// </summary>
        internal static Func<WorldObject, string> SerpentineTargetStateRefusal = SpellRerollManager.TargetStateRefusal;

        // ---------------- the player, as the rules read it ----------------

        /// <summary>The parts of a Player the rules read. Production: <see cref="For(Player)"/>.</summary>
        internal sealed class Actor
        {
            public uint CharacterId;
            public uint AccountId;
            public bool IsMule;
            public bool IsBusy;
            public bool InCombatMode;
            public Func<WorldObject, bool> InInventory;
            public Func<WorldObject, WorldObject, string> PvpTemplateRefusal;

            public static Actor For(Player player) => new Actor
            {
                CharacterId = player.Guid.Full,
                AccountId = player.Account?.AccountId ?? 0,
                IsMule = player.MuleBlocked(MuleAction.Craft, notify: false),
                IsBusy = player.IsBusy,
                InCombatMode = player.CombatMode != CombatMode.NonCombat,
                InInventory = wo => player.FindObject(wo.Guid.Full, Player.SearchLocations.MyInventory) != null,
                PvpTemplateRefusal = (source, target) => player.PvpTemplateBlocked(PvpTemplateAction.UseWithTarget, source, target),
            };
        }

        // ---------------- the verify ----------------

        /// <summary>
        /// True when <paramref name="offered"/> is exactly one eligible target plus one or more full Hammers of one
        /// material, nothing was dropped from the sale, and the player may open a batch. Then <paramref name="action"/>,
        /// <paramref name="target"/> and <paramref name="hammers"/> are set and <paramref name="refusal"/> is null.
        /// Otherwise the outs are None/null/empty and <paramref name="refusal"/> is the line to show.
        ///
        /// <paramref name="requestedCount"/> is how many lines the client asked to sell. Any line the sell path dropped
        /// (VerifySellItems drops bad lines one by one rather than failing the sale) refuses the whole basket, so the
        /// two counts must match.
        /// </summary>
        public static bool VerifyBasket(Player player, IReadOnlyList<WorldObject> offered, int requestedCount,
            out SalvageRollerAction action, out WorldObject target, out List<WorldObject> hammers, out string refusal)
        {
            if (player == null)
                throw new ArgumentNullException(nameof(player));

            return VerifyBasket(Actor.For(player), offered, requestedCount, out action, out target, out hammers, out refusal);
        }

        internal static bool VerifyBasket(Actor actor, IReadOnlyList<WorldObject> offered, int requestedCount,
            out SalvageRollerAction action, out WorldObject target, out List<WorldObject> hammers, out string refusal)
        {
            action = SalvageRollerAction.None;
            target = null;
            hammers = new List<WorldObject>();

            return Verify(actor, offered, requestedCount, postDetach: false, out action, out target, out hammers, out refusal);
        }

        /// <summary>
        /// The post-detach backstop (DESIGN 5.4 step 4.1): the same rules as <see cref="VerifyBasket(Player, IReadOnlyList{WorldObject}, int, out SalvageRollerAction, out WorldObject, out List{WorldObject}, out string)"/>,
        /// run by the sell pipeline on the items the sell path has ALREADY detached. It skips exactly the two
        /// rules that detaching breaks by itself: that each item is in the player's inventory (it no longer is),
        /// and that the player is not busy (the sale in progress may hold them busy). Every other rule, the
        /// switch and the one-batch probe included, runs as before. The requested count is the offered count,
        /// because the pre-detach gate has already matched the two.
        /// </summary>
        public static bool VerifyDetachedBasket(Player player, IReadOnlyList<WorldObject> detached,
            out SalvageRollerAction action, out WorldObject target, out List<WorldObject> hammers, out string refusal)
        {
            if (player == null)
                throw new ArgumentNullException(nameof(player));

            return VerifyDetachedBasket(Actor.For(player), detached, out action, out target, out hammers, out refusal);
        }

        internal static bool VerifyDetachedBasket(Actor actor, IReadOnlyList<WorldObject> detached,
            out SalvageRollerAction action, out WorldObject target, out List<WorldObject> hammers, out string refusal)
        {
            return Verify(actor, detached, detached?.Count ?? 0, postDetach: true, out action, out target, out hammers, out refusal);
        }

        private static bool Verify(Actor actor, IReadOnlyList<WorldObject> offered, int requestedCount, bool postDetach,
            out SalvageRollerAction action, out WorldObject target, out List<WorldObject> hammers, out string refusal)
        {
            action = SalvageRollerAction.None;
            target = null;
            hammers = new List<WorldObject>();

            refusal = Check(actor, offered, requestedCount, postDetach, out var resolvedAction, out var resolvedTarget, out var resolvedHammers);

            if (refusal != null)
                return false;

            action = resolvedAction;
            target = resolvedTarget;
            hammers = resolvedHammers;
            return true;
        }

        private static string Check(Actor actor, IReadOnlyList<WorldObject> offered, int requestedCount, bool postDetach,
            out SalvageRollerAction action, out WorldObject target, out List<WorldObject> hammers)
        {
            action = SalvageRollerAction.None;
            target = null;
            hammers = new List<WorldObject>();

            // 1. the switch. New batches only: take and reconcile never come through here.
            if (!PropertyManager.GetBool("salvage_roller_enabled").Item)
                return DisabledMessage;

            // 2. the basket's shape
            if (offered == null || offered.Count != requestedCount || offered.Any(wo => wo == null))
                return LinesDroppedMessage;

            var fuel = offered.Where(IsFuelShaped).ToList();
            var others = offered.Where(wo => !IsFuelShaped(wo)).ToList();

            if (others.Count == 0)
                return NoTargetMessage;

            if (others.Count > 1)
                return TooManyTargetsMessage;

            var item = others[0];

            if (fuel.Count == 0)
                return NoHammersMessage(item.Name);

            foreach (var wo in fuel)
            {
                if (!SalvageTool.IsSalvageTool(wo))
                    return NotAHammerMessage(wo.Name);
            }

            var material = fuel[0].MaterialType;

            if (fuel.Any(wo => wo.MaterialType != material))
                return MixedMaterialsMessage;

            var rolls = RollCount(fuel);
            var cap = MaxRolls();

            if (rolls > cap)
                return OverCapMessage(cap, rolls);

            // 3. material routing, behind the same switches the manual claims check
            SalvageRollerAction routed;
            Func<WorldObject, bool> materialTest;

            switch (material)
            {
                case MaterialType.TigerEye:
                    return TigerEyeMessage;

                case MaterialType.Tourmaline:
                case MaterialType.Amethyst:
                    if (!PropertyManager.GetBool("weapon_mods_enabled").Item)
                        return SwitchedOffMessage(material.Value);
                    routed = material == MaterialType.Tourmaline ? SalvageRollerAction.TourmalineReroll : SalvageRollerAction.AmethystSwap;
                    materialTest = WeaponModManager.IsModMaterial;
                    break;

                case MaterialType.Obsidian:
                    if (!PropertyManager.GetBool("equipment_mods_enabled").Item)
                        return SwitchedOffMessage(material.Value);
                    routed = SalvageRollerAction.ObsidianConvert;   // Convert or Reroll is settled by the target below
                    materialTest = EquipmentModManager.IsModMaterial;
                    break;

                case MaterialType.Serpentine:
                    if (!PropertyManager.GetBool("serpentine_reroll_enabled").Item)
                        return SwitchedOffMessage(material.Value);
                    routed = SalvageRollerAction.SerpentineReroll;
                    materialTest = SpellRerollManager.IsRerollMaterial;
                    break;

                case MaterialType.GreenJade:
                    if (!PropertyManager.GetBool("jewelry_aspects_enabled").Item)
                        return SwitchedOffMessage(material.Value);
                    routed = SalvageRollerAction.GreenJadeSwap;
                    materialTest = GreenJadeManager.IsGreenJadeMaterial;
                    break;

                default:
                    return UnsupportedMaterialMessage;
            }

            // 4. the manual entry checks (RecipeManager.UseObjectOnTarget's head), with each Hammer as the source.
            //    Self-use cannot arise: a Hammer is never the target.
            if (actor.IsMule)
                return Player.GetMuleRefusal(MuleAction.Craft);

            foreach (var hammer in fuel)
            {
                var templateRefusal = actor.PvpTemplateRefusal(hammer, item);

                if (templateRefusal != null)
                    return templateRefusal;
            }

            // not post-detach: the sale that detached these items may itself be what holds the player busy
            if (!postDetach && actor.IsBusy)
                return BusyMessage;

            if (!PropertyManager.GetBool("allow_combat_mode_crafting").Item && actor.InCombatMode)
                return PeaceModeMessage;

            foreach (var hammer in fuel)
            {
                var lockRefusal = TinkerLockRefusal(hammer, item);

                if (lockRefusal != null)
                    return lockRefusal;
            }

            // 5. the fuel: the manager's material test, in the pack, and FULL (stricter than manual, owner ruling 7)
            foreach (var hammer in fuel)
            {
                if (!materialTest(hammer))
                    return UnsupportedMaterialMessage;

                // not post-detach: the sell path has already taken every item out of the pack
                if (!postDetach && !actor.InInventory(hammer))
                    return FuelNotInInventoryMessage(hammer.Name);

                if (!SalvageForge.IsEligibleHammer(hammer, material.Value))
                    return NotFullMessage(hammer.Name);
            }

            // 6. the target: the manager's own target-only rules, in the bag path's order (inventory, then state)
            // a worn Green Jade target gets the hand path's worn line, not the pack line: production InInventory searches
            // MyInventory only, so a worn piece would otherwise read as "not in your inventory". (TargetStateRefusal keeps
            // the same check as the post-detach backstop, where this pre-detach block is skipped.)
            if (!postDetach && routed == SalvageRollerAction.GreenJadeSwap && (item.CurrentWieldedLocation ?? EquipMask.None) != EquipMask.None)
                return $"Take the {item.Name} off first.";

            if (!postDetach && !actor.InInventory(item))
                return TargetNotInInventoryMessage(item.Name);

            var stateRefusal = TargetStateRefusal(routed, fuel[0].Name, item, out var resolved);

            if (stateRefusal != null)
                return stateRefusal;

            // 7. the account: no open batch, and the escrow loaded
            var (readOk, exists) = BatchProbe(actor.CharacterId);

            if (!readOk)
                return BatchReadFailedMessage;

            if (exists)
                return BatchExistsMessage;

            var (ready, reason) = EscrowProbe(actor.AccountId);

            if (!ready)
                return reason ?? AccountVaultStore.EscrowUnavailableMessage;

            action = resolved;
            target = item;
            hammers = fuel;
            return null;
        }

        /// <summary>
        /// The target's state rules for <paramref name="routed"/>, through the manager functions the bag paths call, or
        /// null when it is eligible. For Obsidian this is also where Convert or Reroll is decided, exactly as the bag
        /// path decides it, so <paramref name="resolved"/> may differ from <paramref name="routed"/>.
        ///
        /// For a weapon, WeaponModManager.CanApply is checked too: the bag path checks it after the fuel is
        /// verified and refuses with a generic error, while the roller must not escrow a batch it cannot roll.
        /// </summary>
        internal static string TargetStateRefusal(SalvageRollerAction routed, string hammerName, WorldObject target, out SalvageRollerAction resolved)
        {
            resolved = routed;

            switch (routed)
            {
                case SalvageRollerAction.TourmalineReroll:
                case SalvageRollerAction.AmethystSwap:
                {
                    var weaponAction = routed == SalvageRollerAction.TourmalineReroll
                        ? WeaponModManager.WeaponModAction.Reroll
                        : WeaponModManager.WeaponModAction.Swap;

                    var refusal = WeaponModManager.TargetStateRefusal(hammerName, target, weaponAction);

                    if (refusal != null)
                        return refusal;

                    if (!WeaponModManager.CanApply(target, WeaponClassifier.Classify(target), weaponAction))
                        return $"The {hammerName} cannot be used on the {target.Name}.";

                    return null;
                }

                case SalvageRollerAction.ObsidianConvert:
                case SalvageRollerAction.ObsidianReroll:
                {
                    var refusal = EquipmentModManager.TargetStateRefusal(target, false, out var modAction, out _);

                    if (refusal != null)
                        return refusal;

                    resolved = modAction == EquipmentModManager.ModAction.Reroll ? SalvageRollerAction.ObsidianReroll : SalvageRollerAction.ObsidianConvert;
                    return null;
                }

                case SalvageRollerAction.SerpentineReroll:
                    return SerpentineTargetStateRefusal(target);

                case SalvageRollerAction.GreenJadeSwap:
                    // the hand-apply rules: a worn piece first (TargetInventoryRefusal's worn line; the pack line is the
                    // caller's InInventory check), then the piece and aspected tests
                    if ((target.CurrentWieldedLocation ?? EquipMask.None) != EquipMask.None)
                        return $"Take the {target.Name} off first.";

                    return GreenJadeManager.TargetStateRefusal(target);

                default:
                    return UnsupportedMaterialMessage;
            }
        }

        /// <summary>
        /// Fuel-shaped: tinkering material. A full Hammer, a part-used one and a salvage bag all qualify here; the
        /// shape check then refuses a bag, and the fuel check a part-used Hammer, each with its own line.
        /// </summary>
        internal static bool IsFuelShaped(WorldObject wo) => wo.ItemType == ItemType.TinkeringMaterial;

        internal static string MaterialName(MaterialType material)
        {
            switch (material)
            {
                case MaterialType.Tourmaline: return "tourmaline";
                case MaterialType.Amethyst: return "amethyst";
                case MaterialType.Obsidian: return "obsidian";
                case MaterialType.Serpentine: return "serpentine";
                case MaterialType.GreenJade: return "green jade";
                default: return material.ToString().ToLowerInvariant();
            }
        }
    }
}
