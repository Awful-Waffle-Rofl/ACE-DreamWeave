using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.WorldObjects;

namespace ACE.Server.RefireStations
{
    /// <summary>
    /// The Arcane Alignment Table: a Marketplace NPC (wcid 1002751, PropertyBool.ArcaneAlignmentTable). A
    /// player GIVES a magic item to the table; it asks a yes/no confirmation to pay 5 Trade Notes (250,000,
    /// wcid 20630) to shift the item's Arcane Lore requirement (PropertyInt.ItemDifficulty, 109) by a random
    /// signed amount - down by as much as ten, up by as much as five, but never above what it was born with.
    /// The item is never taken from the player.
    ///
    /// CEILING is recorded the first time this station touches an item: PropertyInt.ArcaneLoreOriginal (9044)
    /// is set to the item's ItemDifficulty at that moment and never overwritten again, so a repeated alignment
    /// clamps against the item's ORIGINAL requirement, not whatever it drifted to most recently.
    ///
    /// DELTA is drawn from RefireStationCommon.DeltaPool (-10..-1 or +1..+5, zero excluded by owner decision -
    /// every use must change something) and applied via RefireStationCommon.ClampDelta. The charge happens
    /// even when the clamp leaves the value unchanged (e.g. already at 0 with a negative delta, or already at
    /// the ceiling with a positive one) - that is the gamble.
    /// </summary>
    public static class ArcaneAlignmentStation
    {
        public const int AlignCostNotes = 5;

        private const string InsufficientFundsMessage = "You need 5 Trade Notes (250,000), in your pack or as 1,250,000 banked pyreals, to use the Arcane Alignment Table.";

        private const string StackedRefusal = "The Arcane Alignment Table cannot work on a stack - split it first.";

        // ---------------- pure helpers ----------------

        /// <summary>
        /// TRUE if this item has a positive Arcane Lore requirement for the table to shift. A null or
        /// non-positive ItemDifficulty is not an "arcane" item in this sense (a piece of gear with no
        /// requirement, or a malformed/zeroed one).
        /// </summary>
        public static bool IsEligibleItem(int? itemDifficulty) => itemDifficulty.HasValue && itemDifficulty.Value > 0;

        // ---------------- eligibility (needs a live Player/WorldObject) ----------------

        public static WeenieError VerifyEligible(Player player, WorldObject item, out string refusal)
        {
            refusal = null;

            if (item == null)
            {
                refusal = "The Arcane Alignment Table needs a real magic item handed to it.";
                return WeenieError.YouDoNotPassCraftingRequirements;
            }

            if (player.FindObject(item.Guid.Full, Player.SearchLocations.MyInventory) == null)
            {
                refusal = "The Arcane Alignment Table only works on a piece in your pack, not one you are wearing.";
                return WeenieError.YouDoNotPassCraftingRequirements;
            }

            if (RefireStationCommon.IsStacked(item.StackSize))
            {
                refusal = StackedRefusal;
                return WeenieError.YouDoNotPassCraftingRequirements;
            }

            if (!IsEligibleItem(item.ItemDifficulty))
            {
                refusal = "The Arcane Alignment Table cannot align that: it has no Arcane Lore requirement.";
                return WeenieError.YouDoNotPassCraftingRequirements;
            }

            if (player.IsBusy)
            {
                refusal = "You are too busy for that right now.";
                return WeenieError.YoureTooBusy;
            }

            return WeenieError.None;
        }

        // ---------------- entry point (Player_Inventory.GiveObjectToNPC) ----------------

        public static void HandleGive(Player player, WorldObject table, WorldObject item)
        {
            RefireStationCommon.HandleGive(
                player, table, item,
                enabledPropertyName: null,
                costNotes: AlignCostNotes,
                insufficientFundsMessage: InsufficientFundsMessage,
                verify: VerifyEligible,
                buildPrompt: BuildPrompt,
                apply: Apply);
        }

        private static string BuildPrompt(Player player, WorldObject item)
        {
            var current = item.ItemDifficulty.Value;
            var ceiling = PeekCeiling(item);

            return $"Shift the Arcane Lore requirement of {item.Name} (currently {current}) for 5 Trade Notes (250,000)? " +
                   $"Result is random (-10 to +5), never above {ceiling}.";
        }

        private static string Apply(Player player, WorldObject item)
        {
            var current = item.ItemDifficulty.Value;
            var ceiling = RecordCeilingIfFirstUse(item);

            var delta = RefireStationCommon.RollDelta();
            var updated = RefireStationCommon.ClampDelta(current, delta, ceiling);

            item.ItemDifficulty = updated;
            item.ChangesDetected = true;
            item.SaveBiotaToDatabase();

            RefireStationCommon.UpdateObj(player, item);

            return $"The Arcane Alignment Table shifts the {item.Name}. Arcane Lore {current} -> {updated}. 5 Trade Notes (250,000) spent.";
        }

        /// <summary>
        /// Read-only preview of the ceiling for the confirmation prompt - the recorded ArcaneLoreOriginal if
        /// this item was aligned before, otherwise its current ItemDifficulty. Deliberately does NOT write the
        /// property (that only happens on a confirmed, charged apply - see <see cref="RecordCeilingIfFirstUse"/>),
        /// so a cancelled confirmation leaves the item completely untouched.
        /// </summary>
        private static int PeekCeiling(WorldObject item)
        {
            return item.GetProperty(PropertyInt.ArcaneLoreOriginal) ?? item.ItemDifficulty.Value;
        }

        /// <summary>
        /// Reads PropertyInt.ArcaneLoreOriginal off the item if present; otherwise this is the item's FIRST
        /// alignment, so records its current ItemDifficulty as the ceiling and returns that. Only called from
        /// Apply, after the charge has already succeeded, so the marker is written and persisted together with
        /// the rest of this align (same SaveBiotaToDatabase call in Apply covers both).
        /// </summary>
        private static int RecordCeilingIfFirstUse(WorldObject item)
        {
            var recorded = item.GetProperty(PropertyInt.ArcaneLoreOriginal);

            if (recorded.HasValue)
                return recorded.Value;

            var ceiling = item.ItemDifficulty.Value;
            item.SetProperty(PropertyInt.ArcaneLoreOriginal, ceiling);

            return ceiling;
        }
    }
}
