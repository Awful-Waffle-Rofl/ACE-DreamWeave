using System;

using ACE.Common;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.RefireStations
{
    /// <summary>
    /// Shared plumbing for the Marketplace "refire station" NPCs (WaffleACE, DreamWeave, 2026-08-18):
    /// Workmanship Reforge (WorkmanshipReforgeStation, wcid 1002750), Arcane Alignment Table
    /// (ArcaneAlignmentStation, wcid 1002751) and Defense Requirement Reforge (DefenseReforgeStation, wcid
    /// 1002752). All three follow the same shape - a player GIVES a piece of equipment, the station asks a
    /// yes/no confirmation to pay N Trade Notes (MMDs, wcid 20630) to reroll one property on it, and on
    /// confirmation the item is charged and mutated in place. The item is NEVER taken from the player.
    ///
    /// ONE give-intercept dispatches on marker bools in Player_Inventory.GiveObjectToNPC; this class is the
    /// ONE shared confirm+charge template every station's HandleGive delegates into, parameterised by note
    /// cost, refusal/prompt text, and per-station eligibility (<see cref="EligibilityCheck"/>) and apply
    /// (<see cref="ApplyAction"/>) delegates. A station's own file owns only what actually differs: its
    /// eligibility rule and what "apply" means for its one property.
    /// </summary>
    public static class RefireStationCommon
    {
        /// <summary>
        /// Re-checkable eligibility gate: verifies the item is currently workable and returns WeenieError.None,
        /// or sets <paramref name="refusal"/> to the station's player-facing text (sent as a plain system chat
        /// line, not a Tell - the stations do not speak) and returns a non-None WeenieError.
        /// Run once when the give arrives and again on confirmation, since the confirmation dialog gives the
        /// player time to move, equip or drop the item.
        /// </summary>
        public delegate WeenieError EligibilityCheck(Player player, WorldObject item, out string refusal);

        /// <summary>
        /// Mutates <paramref name="item"/>, saves it and refreshes it for the client, and returns the
        /// player-facing success message. Only called after the charge succeeds - charge and apply are
        /// atomic from the caller's point of view (HandleConfirm charges first, then applies; if apply were to
        /// throw, the charge has already landed, same trade-off the original Workmanship Reforge made).
        /// </summary>
        public delegate string ApplyAction(Player player, WorldObject item);

        /// <summary>
        /// The give entry point, called from Player_Inventory.GiveObjectToNPC once the give target's marker
        /// bool has been checked. <paramref name="enabledPropertyName"/> is a PropertyManager bool checked
        /// before anything else; pass null to skip that gate entirely (a station with no kill-switch).
        /// </summary>
        public static void HandleGive(
            Player player,
            WorldObject station,
            WorldObject item,
            string enabledPropertyName,
            int costNotes,
            string insufficientFundsMessage,
            EligibilityCheck verify,
            Func<Player, WorldObject, string> buildPrompt,
            ApplyAction apply)
        {
            if (enabledPropertyName != null && !PropertyManager.GetBool(enabledPropertyName).Item)
            {
                SendCraftMessage(player, $"The {station.Name} is not taking work right now.");
                return;
            }

            var error = verify(player, item, out var refusal);

            if (error != WeenieError.None)
            {
                SendCraftMessage(player, refusal);
                return;
            }

            var itemGuid = item.Guid.Full;
            var prompt = buildPrompt(player, item);

            if (!player.ConfirmationManager.EnqueueSend(
                    new Confirmation_Custom(player.Guid, () => HandleConfirm(player, station, itemGuid, costNotes, insufficientFundsMessage, verify, apply)),
                    prompt))
            {
                player.SendWeenieError(WeenieError.ConfirmationInProgress);
            }
        }

        /// <summary>
        /// Runs on confirmation. Re-verifies eligibility against current state, then charges and applies
        /// atomically - the charge and the apply either both happen or neither does (apply only runs after a
        /// successful charge).
        /// </summary>
        private static void HandleConfirm(
            Player player,
            WorldObject station,
            uint itemGuid,
            int costNotes,
            string insufficientFundsMessage,
            EligibilityCheck verify,
            ApplyAction apply)
        {
            var item = player.FindObject(itemGuid, Player.SearchLocations.MyInventory);

            var error = verify(player, item, out var refusal);

            if (error != WeenieError.None)
            {
                SendCraftMessage(player, refusal);
                return;
            }

            if (!TryCharge(player, costNotes))
            {
                SendCraftMessage(player, insufficientFundsMessage);
                return;
            }

            var message = apply(player, item);

            SendCraftMessage(player, message);
        }

        /// <summary>
        /// Charges <paramref name="costNotes"/> Trade Notes (MMDs, wcid 20630) pack-first, then from banked
        /// pyreals at Player.MmdValue per note, via Player.SpendCurrency (Player_Commerce.cs) - the reused
        /// "shop hook bridge" debit (PR #644) with its own "[BANK] Debited ..." messaging. Returns FALSE
        /// without charging anything on a shortfall (checked twice: once here directly, once again inside
        /// SpendCurrency itself, which only returns null on a shortfall it independently detects - e.g. a
        /// concurrent spend racing the two counts apart).
        /// </summary>
        public static bool TryCharge(Player player, int costNotes)
        {
            var packNotes = player.GetNumInventoryItemsOfWCID(Player.MmdWcid);
            var bankedNotes = player.GetBankedAlternateCurrency(Player.MmdWcid);

            if (packNotes + bankedNotes < costNotes)
                return false;

            var spent = player.SpendCurrency(Player.MmdWcid, (uint)costNotes, destroy: true);

            return spent != null;
        }

        /// <summary>
        /// Splits <paramref name="totalCost"/> Trade Notes between the player's pack and their bank: up to the
        /// full cost comes from <paramref name="packNotes"/> already in the pack, and whatever is left (0 if
        /// the pack alone covers it) comes from the bank. Pure and Player-free so the split itself is
        /// unit-testable apart from the actual currency debit (see WorkmanshipRefireTests.cs).
        /// </summary>
        public static (int fromPack, int fromBank) SplitCost(int packNotes, int totalCost)
        {
            var fromPack = Math.Clamp(packNotes, 0, totalCost);

            return (fromPack, totalCost - fromPack);
        }

        /// <summary>
        /// TRUE if the item.s StackSize is above 1 (a null StackSize, like an unstackable item, is 1). Pure,
        /// shared by all three stations. VerifyEligible - each station owns its own refusal text (2026-08-18
        /// round 2: the refusal now names the station by name, in third person, so it cannot be one shared
        /// constant) - see RefireStationsTests.cs for the test.
        /// </summary>
        public static bool IsStacked(int? stackSize) => (stackSize ?? 1) > 1;

        /// <summary>
        /// The pool a "shift by a random amount, never zero" station draws its delta from: -10..-1 or +1..+5.
        /// Zero is deliberately excluded (owner decision, 2026-08-18) - every use must change something.
        /// Shared by ArcaneAlignmentStation and DefenseReforgeStation; the Workmanship Reforge keeps its own
        /// RollDifferent (a "never equal to the old discrete value" rule over the 4-10 tier-6 chance table, a different
        /// shape from a signed delta over an open-ended difficulty).
        /// </summary>
        public static readonly int[] DeltaPool = { -10, -9, -8, -7, -6, -5, -4, -3, -2, -1, 1, 2, 3, 4, 5 };

        /// <summary>
        /// Draws one delta uniformly from <see cref="DeltaPool"/>.
        /// </summary>
        public static int RollDelta() => DeltaPool[ThreadSafeRandom.Next(0, DeltaPool.Length - 1)];

        /// <summary>
        /// Applies <paramref name="delta"/> to <paramref name="current"/> and clamps to [0, ceiling]. The
        /// charge always happens even if the clamp leaves the value unchanged (e.g. already at 0 and delta is
        /// negative, or already at ceiling and delta is positive) - that is the gamble, per the owner's design.
        /// </summary>
        public static int ClampDelta(int current, int delta, int ceiling) => Math.Clamp(current + delta, 0, ceiling);

        // ---------------- messaging ----------------

        /// <summary>
        /// Sends a Tell FROM the given NPC. The three refire stations themselves are NOT speaking NPCs
        /// (2026-08-18 round 2) and no longer use this for their own refusal/insufficient-funds text - see
        /// SendCraftMessage below for that. The only caller left is the RefireStationAttendant give-refusal
        /// intercept in Player_Inventory.GiveObjectToNPC, since the attendants DO speak.
        /// </summary>
        public static void SendStationTell(Player player, WorldObject station, string message)
        {
            player.Session.Network.EnqueueSend(new GameEventTell(station, message, player, ChatMessageType.Tell));
        }

        public static void SendCraftMessage(Player player, string message)
        {
            player.Session.Network.EnqueueSend(new GameMessageSystemChat(message, ChatMessageType.Craft));
        }

        /// <summary>
        /// Sends an UpdateObj for the modified item, mirroring RecipeManager.UpdateObj. This is what refreshes
        /// the mutated property on the appraisal panel without a relog.
        /// </summary>
        public static void UpdateObj(Player player, WorldObject item)
        {
            player.EnqueueBroadcast(new GameMessageUpdateObject(item));

            // the client moves an item to the first container slot when it receives an UpdateObject,
            // so mirror that server side for persistence
            if (player.FindObject(item.Guid.Full, Player.SearchLocations.MyInventory) != null)
                player.MoveItemToFirstContainerSlot(item);

            // the UpdateObject above just rebuilt this object client-side, which drops any persistent
            // particle aura (VisualEffectManager). item is inventory-only by construction here, so reopen
            // the once-only send guard rather than resending: sending to a packed item would attach the
            // script to nothing and spend the guard that equipping it later depends on.
            VisualEffectManager.Forget(player.Session, item);
        }
    }
}
