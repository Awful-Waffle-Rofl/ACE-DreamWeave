using System;

using ACE.Entity.Enum;
using ACE.Server.Factories.Tables;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.RefireStations
{
    /// <summary>
    /// The Workmanship Reforge: a Marketplace NPC (wcid 1002750, PropertyBool.WorkmanshipRefireForge). A player
    /// GIVES a piece of equipment to the forge; it asks a yes/no confirmation to pay 10 Trade Notes (250,000,
    /// wcid 20630) to reroll the item's ItemWorkmanship on the TIER 6 chance table (WorkmanshipChance.Roll(6)),
    /// with the rule that the new value is NEVER equal to the old value. The item is never taken from the
    /// player - only the currency is spent and ItemWorkmanship is mutated in place.
    ///
    /// REPLACES the earlier Amber salvage-material approach (WaffleACE, DreamWeave, 2026-08-17/18): the owner
    /// changed the mechanic before that shipped, so there is no migration path from the old material - it
    /// never reached a released world DB.
    ///
    /// ENTRY POINT is Player_Inventory.GiveObjectToNPC, which intercepts BEFORE the AiAcceptEverything/emote
    /// give logic when the give target carries PropertyBool.WorkmanshipRefireForge. That is a deliberate
    /// asymmetry with the old RecipeManager.UseObjectOnTarget entry point: a give, not a use-on-target, because
    /// the new mechanic is "hand the forge an item", not "use a material on an item".
    ///
    /// NO NEW CHANCE TABLE, same design intent as the material it replaces (repo owner, 2026-08-17): low
    /// workmanship rolls suit traditional tinkering, workmanship 10 is what the newer mod systems (Equipment
    /// Mods, Weapon Mods) want, so 10 must stay the rarest outcome. The T6 table already does this (5% at 10),
    /// so Roll(6) is used unmodified.
    ///
    /// STATION FAMILY (WaffleACE, DreamWeave, 2026-08-18): this was formerly the sole occupant of
    /// ACE.Server.WorkmanshipRefire; two siblings (ArcaneAlignmentStation, DefenseReforgeStation) now share
    /// RefireStationCommon's give -> confirm -> charge template. This class kept its exact prior behaviour -
    /// same messages, same order of checks - moving only the plumbing (charge, messaging, UpdateObj) into the
    /// shared helper.
    /// </summary>
    public static class WorkmanshipReforgeStation
    {
        /// <summary>
        /// The tier the reroll draws from. Fixed at 6, per the design note above - never derived from the
        /// target's own tier or material, because there is no such notion for a generic "any piece of gear
        /// with a workmanship" refire.
        /// </summary>
        public const int RollTier = 6;

        /// <summary>
        /// Total cost in Trade Notes (MMDs, wcid 20630) for one refire.
        /// </summary>
        public const int RefireCostNotes = 10;

        /// <summary>
        /// Bounded retry count for <see cref="RollDifferent"/> before it falls back to a deterministic
        /// neighbouring value. At worst case (T6's narrowest single outcome, 25% at several values) 64 tries
        /// leaves a (0.75)^64 chance of exhausting the loop that is astronomically small - the fallback exists
        /// only so the method has a hard upper bound, not because it is expected to fire in play.
        /// </summary>
        public const int MaxRerollAttempts = 64;

        /// <summary>
        /// The equipment surface this forge targets - decoded from the 33039 TargetType mask the old Amber
        /// material reused (itself borrowed from the Serpentine spell-reroll bag, 1001759): MeleeWeapon (0x1) |
        /// Armor (0x2) | Clothing (0x4) | Jewelry (0x8) | MissileWeapon (0x100) | Caster (0x8000) =
        /// 1 + 2 + 4 + 8 + 256 + 32768 = 33039. Salvage bags (ItemType.TinkeringMaterial) and gems
        /// (ItemType.Gem) are deliberately absent, so neither can ever qualify.
        /// </summary>
        public const ItemType EquipmentTypeMask =
            ItemType.MeleeWeapon | ItemType.Armor | ItemType.Clothing | ItemType.Jewelry | ItemType.MissileWeapon | ItemType.Caster;

        private const string InsufficientFundsMessage = "You need 10 Trade Notes (250,000), in your pack or as 2,500,000 banked pyreals, to use the Workmanship Reforge.";

        private const string StackedRefusal = "The Workmanship Reforge cannot work on a stack - split it first.";

        // ---------------- pure helpers (no WorldObject needed - see WorkmanshipRefireTests.cs) ----------------

        /// <summary>
        /// TRUE if this item type is one the forge will refire (any piece of "equipment" in the 33039 sense).
        /// </summary>
        public static bool IsEligibleItemType(ItemType itemType) => (itemType & EquipmentTypeMask) != ItemType.None;

        /// <summary>
        /// The full eligibility predicate over an item's type and workmanship alone - no null item, must carry
        /// a workmanship, and must be an eligible equipment type. Everything else the forge checks (inventory
        /// location, busy state, combat mode) needs a live Player and lives in <see cref="VerifyEligible"/>.
        /// </summary>
        public static bool IsEligibleItem(ItemType itemType, int? itemWorkmanship) => itemWorkmanship != null && IsEligibleItemType(itemType);

        /// <summary>
        /// Rerolls <paramref name="roll"/> until it differs from <paramref name="oldWorkmanship"/>, bounded by
        /// <see cref="MaxRerollAttempts"/>. If every try comes back equal to the old value (only reachable with
        /// a degenerate/stubbed roll function - the real T6 table always has other mass to land on) it falls
        /// back to a deterministic neighbour: one step down, unless the old value was already the floor (1), in
        /// which case one step up. Either fallback is guaranteed to differ from <paramref name="oldWorkmanship"/>.
        /// </summary>
        public static int RollDifferent(int oldWorkmanship, Func<int> roll)
        {
            for (var i = 0; i < MaxRerollAttempts; i++)
            {
                var candidate = roll();

                if (candidate != oldWorkmanship)
                    return candidate;
            }

            return oldWorkmanship <= 1 ? oldWorkmanship + 1 : oldWorkmanship - 1;
        }

        /// <summary>
        /// Splits the <see cref="RefireCostNotes"/> total between the player's pack and their bank. Thin
        /// wrapper over RefireStationCommon.SplitCost, kept here so the pre-existing test names/call sites
        /// (WorkmanshipRefireManager.SplitCost) still resolve without a signature change.
        /// </summary>
        public static (int fromPack, int fromBank) SplitCost(int packNotes, int totalCost = RefireCostNotes) =>
            RefireStationCommon.SplitCost(packNotes, totalCost);

        // ---------------- eligibility (needs a live Player/WorldObject) ----------------

        /// <summary>
        /// Re-checkable eligibility gate, run once when the give arrives and again right before the charge/
        /// reroll (the confirmation dialog gives the player time to move, equip or drop the item). Returns
        /// WeenieError.None on success; otherwise <paramref name="refusal"/> carries the forge's player-facing
        /// text (sent as a plain system chat line, not a Tell - the forge does not speak) and the return
        /// value is the WeenieError to log/telemetry against.
        /// </summary>
        public static WeenieError VerifyEligible(Player player, WorldObject item, out string refusal)
        {
            refusal = null;

            if (item == null)
            {
                refusal = "The Workmanship Reforge needs a real piece of equipment handed to it.";
                return WeenieError.YouDoNotPassCraftingRequirements;
            }

            if (player.FindObject(item.Guid.Full, Player.SearchLocations.MyInventory) == null)
            {
                refusal = "The Workmanship Reforge only works on a piece in your pack, not one you are wearing.";
                return WeenieError.YouDoNotPassCraftingRequirements;
            }

            if (RefireStationCommon.IsStacked(item.StackSize))
            {
                refusal = StackedRefusal;
                return WeenieError.YouDoNotPassCraftingRequirements;
            }

            if (!IsEligibleItem(item.ItemType, item.ItemWorkmanship))
            {
                refusal = item.ItemWorkmanship == null
                    ? "The Workmanship Reforge cannot refire that: it carries no workmanship."
                    : $"The Workmanship Reforge can only refire weapons, armor, clothing and jewelry - not the {item.Name}.";
                return WeenieError.YouDoNotPassCraftingRequirements;
            }

            if (player.IsBusy)
            {
                refusal = "You are too busy for that right now.";
                return WeenieError.YoureTooBusy;
            }

            var allowCraftInCombat = PropertyManager.GetBool("allow_combat_mode_crafting").Item;

            if (!allowCraftInCombat && player.CombatMode != CombatMode.NonCombat)
            {
                refusal = "You must be in peace mode for that.";
                return WeenieError.YouMustBeInPeaceModeToTrade;
            }

            return WeenieError.None;
        }

        // ---------------- entry point (Player_Inventory.GiveObjectToNPC) ----------------

        /// <summary>
        /// The player gives a piece of equipment to the forge. Verifies eligibility, then asks for
        /// confirmation - the item is NOT taken from the player, it stays in their inventory throughout.
        /// </summary>
        public static void HandleGive(Player player, WorldObject forge, WorldObject item)
        {
            RefireStationCommon.HandleGive(
                player, forge, item,
                enabledPropertyName: "workmanship_refire_enabled",
                costNotes: RefireCostNotes,
                insufficientFundsMessage: InsufficientFundsMessage,
                verify: VerifyEligible,
                buildPrompt: BuildPrompt,
                apply: Apply);
        }

        private static string BuildPrompt(Player player, WorldObject item)
        {
            var oldWorkmanship = item.ItemWorkmanship.Value;

            return $"Refire the workmanship of {item.Name} (currently {oldWorkmanship}) for 10 Trade Notes (250,000)? " +
                   $"The result will differ from {oldWorkmanship} and cannot be undone.";
        }

        private static string Apply(Player player, WorldObject item)
        {
            var oldWorkmanship = item.ItemWorkmanship.Value;
            var newWorkmanship = RollDifferent(oldWorkmanship, () => WorkmanshipChance.Roll(RollTier));

            item.ItemWorkmanship = newWorkmanship;
            item.ChangesDetected = true;
            item.SaveBiotaToDatabase();

            RefireStationCommon.UpdateObj(player, item);

            return $"The Workmanship Reforge refires the {item.Name}. Workmanship {oldWorkmanship} -> {newWorkmanship}. 10 Trade Notes (250,000) spent.";
        }
    }
}
