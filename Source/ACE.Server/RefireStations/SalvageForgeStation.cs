using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;

namespace ACE.Server.RefireStations
{
    /// <summary>
    /// The Salvage Forge: a Marketplace NPC (wcid 1002652, PropertyBool.SalvageForgeStation), the SIXTH
    /// RefireStation (WaffleACE, DreamWeave, 2026-08-23). A player GIVES the forge one full salvage bag of a
    /// supported material - Tiger Eye, Obsidian, Tourmaline, Amethyst or Serpentine - and it asks a yes/no
    /// confirmation to pay 5 Trade Notes to consume that bag plus enough OTHER full bags of the same material
    /// (priced by the player's own tinkering skill, same as ever) and forge the material's 10-charge Hammer
    /// straight into the player's pack.
    ///
    /// THIS REPLACES THE HOLLOW HAMMER AS THE ENTRY POINT to forging a Hammer - vendors stop selling the
    /// Hollow Hammer (content-side change, not this file) in favour of players simply handing the forge a
    /// bag. Entity.SalvageForge.UseObjectOnTarget (the Hollow Hammer's own "use tool on bag" mechanic) STAYS
    /// LIVE and completely unchanged in behaviour, because a player who already holds a Hollow Hammer must
    /// still be able to spend it - this station only changes how a player who does NOT have one gets started.
    ///
    /// BOTH PATHS SHARE Entity.SalvageForge.TryForgeHammer for the actual bag-consumption and Hammer-creation
    /// work (<see cref="Apply"/> below just supplies the material lookup, the required-bag count, and the
    /// item being handed over) - see that method's remarks for exactly how the Hollow Hammer path threads its
    /// own extra "consume the Hollow Hammer" step through the same call without this station needing to know
    /// it exists. This is deliberate: with two live entry points to the identical mutation, a shared
    /// implementation is the only way they cannot silently drift apart on quantity, pricing-by-skill, or which
    /// bags get eaten.
    ///
    /// UNLIKE THE HOLLOW HAMMER, this path costs Trade Notes (5, via RefireStationCommon.HandleGive/TryCharge)
    /// on top of the salvage itself, and asks a confirmation dialog first - the Hollow Hammer path never
    /// needed either, because ItemType.TinkeringMaterial already makes the client show its own generic
    /// tinkering confirmation before the server sees a "use" at all (see SalvageForge.cs's class remarks); the
    /// give-to-NPC path has no such client-side gate, so RefireStationCommon supplies the confirmation here.
    ///
    /// ELIGIBILITY reuses Entity.SalvageForge's rules directly (MaterialTable, IsEligibleBag, IsFullBag,
    /// BagsRequired, CountFullBags) and Entity.SalvageTool.IsSalvageTool, rather than restating any of them -
    /// see <see cref="VerifyEligible"/>. Only the Trade Note cost and the give/confirm shape are new.
    /// </summary>
    public static class SalvageForgeStation
    {
        /// <summary>Trade Notes charged per forge, on top of the salvage itself.</summary>
        public const int ForgeCostNotes = 5;

        /// <summary>
        /// The BANKED-pyreal equivalent of <see cref="ForgeCostNotes"/> Trade Notes, DERIVED from
        /// Player.MmdValue (Source/ACE.Server/WorldObjects/Player_Bank.cs:46, an internal const currently
        /// 250,000 pyreals per note) rather than a literal copied from a sibling file. This is the ONLY place
        /// a derived pyreal figure belongs - the "(250,000)" every RefireStation prints after "N Trade Notes"
        /// (including this one, in InsufficientFundsMessage/BuildPrompt below) is a FIXED literal, because it
        /// is not a total at all: it is the MMD item's own display name, "Trade Note (250,000)" (see
        /// Player_Bank.cs:46's comment, "MMDs (Trade Note (250,000))"). That string is constant regardless of
        /// how many notes are being charged, so WorkmanshipReforgeStation (10 notes), BrewersCauldronStation
        /// (25 notes) and every other sibling all correctly show "(250,000)" too - they do not disagree with
        /// each other or carry a bug; see each of their own InsufficientFundsMessage constants for the
        /// pattern this one follows: "N Trade Notes (250,000), ... as {N x 250,000} banked pyreals".
        /// </summary>
        public const long ForgeCostPyreals = ForgeCostNotes * Player.MmdValue;

        // "(250,000)" here is the MMD item's own display name (Player_Bank.cs:20/46: "Trade Note (250,000)"),
        // not a computed total - keep it a literal, matching every sibling RefireStation's prompt/refusal text.
        private static readonly string InsufficientFundsMessage =
            $"You need {ForgeCostNotes} Trade Notes (250,000), in your pack or as {ForgeCostPyreals:N0} banked pyreals, to use the Salvage Forge.";

        // ---------------- eligibility (needs a live Player/WorldObject) ----------------

        /// <summary>
        /// Re-checkable eligibility gate, run once when the bag is given and again on confirmation (the
        /// confirmation dialog gives the player time to move, spend or drop bags). Order and refusal text per
        /// spec, reusing Entity.SalvageForge's own rules throughout so this can never drift from the Hollow
        /// Hammer path's idea of what counts as an eligible bag or how many are required.
        /// </summary>
        public static WeenieError VerifyEligible(Player player, WorldObject item, out string refusal)
        {
            refusal = null;

            if (item == null)
            {
                refusal = "The Salvage Forge needs a bag handed to it.";
                return WeenieError.YouDoNotPassCraftingRequirements;
            }

            if (item.ItemType != ItemType.TinkeringMaterial || item.MaterialType == null
                || !SalvageForge.MaterialTable.TryGetValue(item.MaterialType.Value, out var info))
            {
                refusal = "The Salvage Forge only takes a full bag of tiger eye, obsidian, tourmaline, amethyst or serpentine salvage.";
                return WeenieError.YouDoNotPassCraftingRequirements;
            }

            if (SalvageTool.IsSalvageTool(item))
            {
                refusal = $"The {item.Name} is a Hammer, not a bag of salvage. Hand the Salvage Forge a full bag instead.";
                return WeenieError.YouDoNotPassCraftingRequirements;
            }

            if (!SalvageForge.IsFullBag(item))
            {
                refusal = $"The {item.Name} is not full. A complete unit of salvage is required.";
                return WeenieError.YouDoNotPassCraftingRequirements;
            }

            var skill = player.GetCreatureSkill(info.Skill).Current;
            var required = SalvageForge.BagsRequired((int)skill);
            var have = SalvageForge.CountFullBags(player, info.Material);

            if (have < required)
            {
                var skillName = SalvageForge.SkillName(info.Skill);
                refusal = $"You need {required} full bags of {info.DisplayName} salvage to forge a hammer with your {skillName} of {skill}; you have {have}.";
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

        public static void HandleGive(Player player, WorldObject forge, WorldObject item)
        {
            RefireStationCommon.HandleGive(
                player, forge, item,
                enabledPropertyName: null,
                costNotes: ForgeCostNotes,
                insufficientFundsMessage: InsufficientFundsMessage,
                verify: VerifyEligible,
                buildPrompt: BuildPrompt,
                apply: Apply);
        }

        private static string BuildPrompt(Player player, WorldObject item)
        {
            var info = SalvageForge.MaterialTable[item.MaterialType.Value];
            var skill = player.GetCreatureSkill(info.Skill).Current;
            var required = SalvageForge.BagsRequired((int)skill);

            // "(250,000)" is the MMD item's own display name (see ForgeCostPyreals' doc comment), a literal,
            // not {ForgeCostPyreals:N0} - matching every sibling RefireStation's prompt.
            return $"Forge a {info.DisplayName} Hammer? The Salvage Forge takes {required} full bag{(required == 1 ? "" : "s")} " +
                   $"of {info.DisplayName} salvage from your pack and {ForgeCostNotes} Trade Notes (250,000).";
        }

        /// <summary>
        /// Consumes <paramref name="item"/> as the target bag (plus enough other full bags of the same
        /// material) and creates the resulting Hammer, via the SHARED Entity.SalvageForge.TryForgeHammer - see
        /// its remarks. Nothing extra to consume between the bags and the Hammer here (afterBagsConsumed:
        /// null): the Trade Notes were already charged by RefireStationCommon.HandleConfirm before this ran.
        /// A failure still returns a string (RefireStationCommon.HandleConfirm always sends whatever this
        /// returns as a chat line, with no separate failure path of its own) - TryForgeHammer's own message is
        /// used when it set one (the "Contact staff" case), and a station-specific fallback otherwise, since
        /// the Notes are already spent by the time Apply runs regardless of outcome.
        /// </summary>
        private static string Apply(Player player, WorldObject item)
        {
            var info = SalvageForge.MaterialTable[item.MaterialType.Value];
            var skill = player.GetCreatureSkill(info.Skill).Current;
            var required = SalvageForge.BagsRequired((int)skill);

            var forged = SalvageForge.TryForgeHammer(player, item, info, required, afterBagsConsumed: null, out var message);

            if (!forged)
                return message ?? $"Something went wrong forging the {info.DisplayName} Hammer. Contact staff if any salvage was lost - your Trade Notes were already spent.";

            return message;
        }
    }
}
