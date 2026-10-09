using System.Collections.Generic;

using ACE.Entity.Enum.Properties;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.Entity
{
    /// <summary>
    /// A multi-use salvage tool: an item that stands in for a full salvage bag but survives the use, spending
    /// one of a finite pool of charges instead of being destroyed outright. FIVE Hammer items stand in for a
    /// bag of a real material - Tiger Eye (1001918), Obsidian (1001912), Tourmaline (1001910), Amethyst
    /// (1001911) and Serpentine (1001916) - and SalvageForge.MaterialTable is the authoritative enumeration of
    /// them: each has a sealed vendor wrapper (1001913/1001914/1001915/1001917/1001919), and any code that
    /// needs "the list of Hammers" must read that dictionary rather than restate the five here.
    ///
    /// A SIXTH weenie carries SalvageToolCharges without being one of them: 1001909 Awfully OP Mod Hammer, the
    /// alpha-test maximizer, has capacity 100 and NO MaterialType at all. Every consumer that matches on
    /// MaterialType therefore skips it for free, which is why it needs no exclusion anywhere - see
    /// SalvageForge.IsEligibleHammer. It is also Attuned and Bonded, so it never reaches a vault or a trade.
    ///
    /// The Hollow Hammer (1002650) is NOT a carrier at all: no MaterialType, no Structure, no
    /// SalvageToolCharges. It is the crafting tool used to forge one of the five.
    ///
    /// THE CHARGE COUNT LIVES ON PropertyInt.Structure, and MaxStructure is the tool's capacity. That is the
    /// whole reason the split is shaped this way: Structure/MaxStructure is what the CLIENT draws its own green
    /// uses-remaining bar from on the appraisal panel, exactly as it does for a summoning device
    /// (ACE.Server.WorldObjects.PetDevice) or a healing kit, so a player reads the remaining uses off the panel
    /// without the server rendering a number for them. PropertyInt.SalvageToolCharges (9035) is NOT the live
    /// count: it is the tool's fixed CAPACITY and its identity marker - its presence with a value above 0 is
    /// the only thing that distinguishes a salvage tool from an ordinary bag, and it must be authored equal to
    /// MaxStructure.
    ///
    /// THE COST of carrying charges in Structure is that a Hammer is no longer "full" by the bag rule: a bag's
    /// Structure is a FRACTION of one unit of salvage and both managers require it to be complete, while a
    /// tool's Structure is a COUNT of whole applications and 7 of 10 is perfectly usable. So
    /// EquipmentModManager and WeaponModManager each carry ONE explicit allowance at their fullness gate,
    /// expressed through <see cref="HasUsableCharge"/> so the rule lives in exactly one place. The partial-bag
    /// refusal itself is untouched, and 9035 is what keeps the two cases apart.
    ///
    /// ItemType.TinkeringTool was tried on 2026-08-01 so a vendor could stock a Hammer directly, and reverted
    /// the same day: the client intercepts the use of an ItemType.TinkeringTool item to open its own salvage
    /// panel and sends GameActionCreateTinkeringTool (0x027D), so the use-on-target never reaches the server.
    ///
    /// PropertyInt.SalvageToolCharges (9035) carries no [AssessmentProperty]: the client does not know the id,
    /// so the capacity is never sent to it and <see cref="GetAppraisalLines"/> is the only player-facing
    /// surface for it. Structure, by contrast, IS a property the client knows, which is what makes the bar
    /// possible and why <see cref="TryConsume"/> pushes it after every decrement.
    /// </summary>
    public static class SalvageTool
    {
        /// <summary>
        /// TRUE only for an item that actually declares a salvage-tool capacity. An ordinary salvage bag does
        /// not carry the property at all, so it is FALSE for every bag regardless of how full it is. Says
        /// nothing about how many charges are LEFT - see <see cref="HasUsableCharge"/> for that.
        /// </summary>
        public static bool IsSalvageTool(WorldObject source) => GetCapacity(source) > 0;

        /// <summary>
        /// The tool's fixed capacity, 0 when the property is absent. This is the AUTHORED value from the
        /// weenie and never changes in play; it doubles as the marker that the item is a tool at all.
        /// </summary>
        public static int GetCapacity(WorldObject source) => source?.GetProperty(PropertyInt.SalvageToolCharges) ?? 0;

        /// <summary>Applications left right now, read off Structure. 0 when absent.</summary>
        public static int GetRemaining(WorldObject source) => source?.Structure ?? 0;

        /// <summary>
        /// TRUE for a salvage tool with at least one application left. FALSE for everything else, INCLUDING
        /// every ordinary salvage bag - a bag is judged by the managers' IsFullBag rule instead, and a partial
        /// bag stays refused.
        /// </summary>
        public static bool HasUsableCharge(WorldObject source) => IsSalvageTool(source) && GetRemaining(source) > 0;

        /// <summary>
        /// THE single place either mod system pays the price for an application, whichever kind of source it
        /// was given.
        ///
        /// An ordinary bag is consumed whole, exactly as before. A tool with charges to spare loses one off
        /// Structure and stays in the pack; a tool down to its last charge is consumed whole like a bag, so the
        /// item never lingers at zero. <paramref name="remaining"/> is what is left afterwards, 0 whenever the
        /// object is gone.
        ///
        /// A FALSE RETURN LEAVES THE SOURCE UNTOUCHED, which is what lets a caller take the price before it
        /// mutates the target: for a whole-object consume, TryConsumeFromInventoryWithNetworking's only failure
        /// is TryRemoveFromInventory returning false, which happens before it removes, destroys or renumbers
        /// anything (see the "PRICE FIRST, THEN MUTATE" remarks on WeaponModManager.HandleApply). The
        /// decrement branch cannot fail at all.
        /// </summary>
        public static bool TryConsume(Player player, WorldObject source, out int remaining)
        {
            remaining = 0;

            if (!IsSalvageTool(source))
                return player.TryConsumeFromInventoryWithNetworking(source);

            var charges = GetRemaining(source);

            if (charges <= 1)
            {
                // the last charge: the tool goes the way of a bag
                return player.TryConsumeFromInventoryWithNetworking(source);
            }

            remaining = charges - 1;

            // WorldObject.Structure is ushort?; remaining is one below a value that was already in range
            source.Structure = (ushort)remaining;

            // push the new count so the client's green uses-remaining bar moves immediately, without waiting
            // for a re-appraisal. Same mechanism a summoning device uses - see PetDevice.ActOnUse.
            player.Session.Network.EnqueueSend(new GameMessagePublicUpdatePropertyInt(source, PropertyInt.Structure, remaining));

            source.ChangesDetected = true;
            source.SaveBiotaToDatabase();

            return true;
        }

        /// <summary>
        /// The craft-channel line reported after a successful application, or NULL when the source was an
        /// ordinary bag and there is nothing to say.
        ///
        /// Takes the NAME rather than the WorldObject on purpose. On the last charge the object is destroyed by
        /// <see cref="TryConsume"/>, so a caller that passed the object here would be reading a destroyed one;
        /// with a string parameter the only way to call it is to have captured the name BEFORE the consume,
        /// which is the correct order anyway.
        /// </summary>
        public static string GetChargeMessage(string name, bool wasSalvageTool, int remaining)
        {
            if (!wasSalvageTool)
                return null;

            if (remaining <= 0)
                return $"The {name} crumbles to dust.";

            return $"The {name} has {remaining} use{(remaining == 1 ? "" : "s")} remaining.";
        }

        /// <summary>
        /// The charge line for an item's appraisal panel. Empty for anything that is not a salvage tool - the
        /// caller decides whether a block is worth printing. Mirrors
        /// ACE.Server.EquipmentMods.EquipmentModDisplay.GetAppraisalLines.
        ///
        /// KEPT even though the client now draws its own bar: the bar carries no number label, so this is the
        /// only place a player reads the actual count and the capacity it is measured against.
        /// </summary>
        public static List<string> GetAppraisalLines(WorldObject wo)
        {
            var lines = new List<string>();

            if (!IsSalvageTool(wo))
                return lines;

            lines.Add($"- Uses remaining: {GetRemaining(wo)} of {GetCapacity(wo)}");

            return lines;
        }
    }
}
