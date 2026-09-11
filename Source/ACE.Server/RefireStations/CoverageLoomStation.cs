using System.Collections.Generic;

using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.WorldObjects;

namespace ACE.Server.RefireStations
{
    /// <summary>
    /// The Weaver's Loom: a Marketplace NPC (wcid 1002757, PropertyBool.CoverageLoom). A player GIVES an
    /// undergarment (a shirt or a pair of pants) whose coverage is only partial - a shirt that stops at the
    /// elbow, breeches that stop at the knee - and it asks a yes/no confirmation to pay 10 Trade Notes
    /// (250,000, wcid 20630) to draw the weave out to full coverage of whichever half (upper body / lower
    /// body) the garment already touches. The result is FIXED, never random - unlike the other three
    /// RefireStations, there is no gamble here. The item is never taken from the player.
    ///
    /// COVERAGE is PropertyInt.ClothingPriority (id 4), a CoverageMask flag set (ACE.Entity.Enum.CoverageMask).
    /// It is mechanically load-bearing, not cosmetic: AL/enchantments from equipped Clothing are only summed
    /// for a struck body part if the piece's ClothingPriority covers that part (Creature_BodyPart.cs,
    /// Monster_Melee.cs), and it participates in equip-slot conflict checks (Creature_Equipment.cs). The LOOK
    /// of the garment is entirely driven by its ClothingBase (dat-owned; see Creature_Networking.cs's ObjDesc
    /// build). Visual follow-up (2026-08-18, Docs/Economy/COVERAGE-LOOM-VISUAL-SPEC.md): where the item's
    /// ClothingBase has a mapped full-coverage sibling (<see cref="VisualSiblings"/>) and the item carries no
    /// own texture_map/palette rows, ClothingBase/SetupTableId/IconId are swapped to the sibling so the
    /// garment also LOOKS full - same colour, same PaletteTemplate/Shade, untouched. An unmapped garment (or
    /// one with its own visual rows) stays mechanical-only: coverage upgrades, the look does not change.
    ///
    /// The upgrade also raises PropertyInt.ValidLocations (EquipMask, id 9) so the newly-covered slots can
    /// actually be worn - a shirt that only covered the chest could only ever be worn in the ChestWear slot;
    /// once it also covers the arms it must accept UpperArmWear/LowerArmWear too, or the extra coverage would
    /// be unwearable dead weight.
    /// </summary>
    public static class CoverageLoomStation
    {
        public const int LoomCostNotes = 10;

        private const string InsufficientFundsMessage = "You need 10 Trade Notes (250,000), in your pack or as 2,500,000 banked pyreals, to use the Weaver's Loom.";

        private const string StackedRefusal = "The Weaver's Loom cannot work on a stack - split it first.";

        // ---------------- coverage constants ----------------

        /// <summary>
        /// The full-shirt coverage mask: chest, upper arms, lower arms (retail Shirt family, e.g. 104).
        /// </summary>
        public const CoverageMask ShirtFull = CoverageMask.UnderwearChest | CoverageMask.UnderwearUpperArms | CoverageMask.UnderwearLowerArms;

        /// <summary>
        /// The full-pants coverage mask: girth (abdomen), upper legs, lower legs (retail Pants family, e.g. 22).
        /// </summary>
        public const CoverageMask PantsFull = CoverageMask.UnderwearAbdomen | CoverageMask.UnderwearUpperLegs | CoverageMask.UnderwearLowerLegs;

        /// <summary>
        /// Every non-underwear coverage bit: all Outerwear* plus Head, Hands, Feet - exactly the set
        /// CoverageMaskHelper.Outerwear already names (CoverageMask.cs:35). An item that carries any of
        /// these is not the kind of bare undergarment the Loom works on.
        /// </summary>
        private const CoverageMask NonUnderwear = (CoverageMask)CoverageMaskHelper.Outerwear;

        // ---------------- pure helpers ----------------

        /// <summary>
        /// TRUE if <paramref name="coverage"/> touches any part of the shirt half (chest, upper arms or lower
        /// arms).
        /// </summary>
        public static bool TouchesShirt(CoverageMask coverage) => (coverage & ShirtFull) != 0;

        /// <summary>
        /// TRUE if <paramref name="coverage"/> touches any part of the pants LEGS (upper or lower leg).
        /// Deliberately excludes abdomen alone - the Kimono Top (120) carries abdomen coverage but is a FULL
        /// shirt, not a partial pants garment, so abdomen by itself must not flip this on.
        /// </summary>
        public static bool TouchesPants(CoverageMask coverage) =>
            (coverage & (CoverageMask.UnderwearUpperLegs | CoverageMask.UnderwearLowerLegs)) != 0;

        /// <summary>
        /// TRUE if <paramref name="coverage"/> is an undergarment the Loom can work on: it carries no
        /// non-underwear (outerwear/head/hands/feet) bits, and it touches at least one of the shirt or pants
        /// halves. A null coverage (no ClothingPriority at all) is never an undergarment.
        /// </summary>
        public static bool IsUndergarment(CoverageMask? coverage)
        {
            if (!coverage.HasValue)
                return false;

            var c = coverage.Value;

            if ((c & NonUnderwear) != 0)
                return false;

            return TouchesShirt(c) || TouchesPants(c);
        }

        /// <summary>
        /// TRUE if every half <paramref name="coverage"/> touches is already at full coverage for that half.
        /// Precondition: <see cref="IsUndergarment"/> is true for <paramref name="coverage"/>.
        /// </summary>
        public static bool HasFullCoverage(CoverageMask coverage)
        {
            if (TouchesShirt(coverage) && (coverage & ShirtFull) != ShirtFull)
                return false;

            if (TouchesPants(coverage) && (coverage & PantsFull) != PantsFull)
                return false;

            return true;
        }

        /// <summary>
        /// Returns <paramref name="coverage"/> upgraded to full coverage on every half it touches. The stray
        /// retail Unknown (0x1) bit is dropped - retail's own full garments (Shirt 104, Pants 22) never carry
        /// it, so this matches what a "properly full" garment looks like.
        /// </summary>
        public static CoverageMask UpgradeCoverage(CoverageMask coverage)
        {
            var c = coverage & ~CoverageMask.Unknown;

            if (TouchesShirt(coverage))
                c |= ShirtFull;

            if (TouchesPants(coverage))
                c |= PantsFull;

            return c;
        }

        /// <summary>
        /// Returns <paramref name="validLocations"/> with the newly-covered slots added, so the upgraded
        /// garment can still be worn in every slot its new coverage now protects.
        /// </summary>
        public static EquipMask UpgradeValidLocations(CoverageMask coverage, EquipMask validLocations)
        {
            var v = validLocations;

            if (TouchesShirt(coverage))
                v |= EquipMask.ChestWear | EquipMask.UpperArmWear | EquipMask.LowerArmWear;

            if (TouchesPants(coverage))
                v |= EquipMask.AbdomenWear | EquipMask.UpperLegWear | EquipMask.LowerLegWear;

            return v;
        }

        /// <summary>
        /// Comma-joined, human-readable list of the underwear coverage bits present in <paramref name="coverage"/>,
        /// in a fixed order: chest, upper arms, lower arms, girth, upper legs, lower legs. An empty set (no
        /// underwear bits at all) reads as "none".
        /// </summary>
        public static string Describe(CoverageMask coverage)
        {
            var parts = new System.Collections.Generic.List<string>();

            if ((coverage & CoverageMask.UnderwearChest) != 0)
                parts.Add("chest");
            if ((coverage & CoverageMask.UnderwearUpperArms) != 0)
                parts.Add("upper arms");
            if ((coverage & CoverageMask.UnderwearLowerArms) != 0)
                parts.Add("lower arms");
            if ((coverage & CoverageMask.UnderwearAbdomen) != 0)
                parts.Add("girth");
            if ((coverage & CoverageMask.UnderwearUpperLegs) != 0)
                parts.Add("upper legs");
            if ((coverage & CoverageMask.UnderwearLowerLegs) != 0)
                parts.Add("lower legs");

            return parts.Count == 0 ? "none" : string.Join(", ", parts);
        }

        // ---------------- visual siblings (partial ClothingBase -> full ClothingBase) ----------------

        /// <summary>
        /// One full-coverage sibling target: the ClothingBase and Setup to swap onto the item, and the
        /// retail Icon DID of the target weenie (written directly so the appraisal/DB row is coherent even
        /// on an item with no PaletteTemplate at all - see VisualSiblings' doc comment). Name is the
        /// sibling's own retail display name, used only in the player-facing prompt/message clause - the
        /// item itself always keeps ITS OWN Name (owner decision 2026-08-18: never rename).
        /// </summary>
        public readonly struct VisualSibling
        {
            public readonly uint ClothingBase;
            public readonly uint Setup;
            public readonly uint Icon;
            public readonly string Name;

            public VisualSibling(uint clothingBase, uint setup, uint icon, string name)
            {
                ClothingBase = clothingBase;
                Setup = setup;
                Icon = icon;
                Name = name;
            }
        }

        /// <summary>
        /// Partial-garment ClothingBase -> full-coverage sibling, recon 2026-08-18 (ACE.DatLoader dump of
        /// client_portal.dat, per-part rows for human male Setup 0x02000001; female 0x0200004E is identical
        /// in shape) - see Docs/Economy/COVERAGE-LOOM-VISUAL-SPEC.md section 1 for the full per-part table.
        /// Every mapped pair carries the SAME PaletteTemplate key set and palette sets/ranges, so a
        /// (PaletteTemplate, Shade) copied unchanged (this station never touches either) lands on the same
        /// colour band on the sibling. Every shirt-family target uses Setup 0x020000D4 except the Viamontian
        /// Lace Shirt (0x020001A6); every pants-family target uses Setup 0x020000DD. Deliberately no
        /// fallback/default target - a ClothingBase not in this table means no visual change, mechanical
        /// upgrade only (<see cref="TryGetVisualSibling"/>).
        ///
        /// Icon DIDs verified 2026-08-18 against local ace_world:
        /// SELECT object_Id,type,HEX(value) FROM weenie_properties_d_i_d WHERE object_Id IN
        /// (130,2587,2588,2590,2591,28607,127,2601,2598,2600) AND type=8.
        ///
        /// Owner decisions 2026-08-18 (spec section 5): vests (Jerkin/Doublet/Vest) -> full shirt is YES
        /// despite the silhouette change (vest to sleeved shirt, same colour); the item keeps its OWN Name,
        /// never renamed to the sibling's; Wide Breeches -> Pantaloons was CONFIRMED correct by the owner
        /// (2026-08-18).
        /// </summary>
        public static readonly IReadOnlyDictionary<uint, VisualSibling> VisualSiblings = new Dictionary<uint, VisualSibling>
        {
            // Tunic 134 (0M 9 10 13) -> Shirt 130 (0 9 10 11 13 14): forearms are TEXTURE-only adds
            [0x10000003] = new VisualSibling(0x10000001, 0x020000D4, 0x06000FF0, "Shirt"),
            // Loose Tunic 2593 (0M 9 10M 13M) -> Shirt 2587 (0 9 10 11M 13 14M): forearm MODEL adds
            [0x10000101] = new VisualSibling(0x100000FA, 0x020000D4, 0x06000FF0, "Shirt"),
            // Flared Tunic 2594 (0M 9 10M 13M) -> Flared Shirt 2588 (0M 9 10M 11M 13M 14M): forearm MODEL adds
            [0x10000102] = new VisualSibling(0x100000FB, 0x020000D4, 0x06000FF0, "Flared Shirt"),
            // Baggy Tunic 2595 (0M 9 10M 13M) -> Baggy Shirt 2590 (0M 9 10M 11M 13M 14M): forearm MODEL adds
            [0x10000103] = new VisualSibling(0x100000FD, 0x020000D4, 0x06000FF0, "Baggy Shirt"),
            // Puffy Tunic 2592 (0 9 10M 13M) -> Puffy Shirt 2591 (0 9 10M 11M 13M 14M): forearm MODEL adds
            [0x10000100] = new VisualSibling(0x100000FE, 0x020000D4, 0x06000FF0, "Puffy Shirt"),
            // Jerkin 124 (0 9, chest-only) -> Shirt 130 (0 9 10 11 13 14): vest -> full sleeved shirt (owner: YES)
            [0x1000001D] = new VisualSibling(0x10000001, 0x020000D4, 0x06000FF0, "Shirt"),
            // Doublet 2596 (0M 9, chest-only) -> Shirt 130 (0 9 10 11 13 14): as above
            [0x100000FF] = new VisualSibling(0x10000001, 0x020000D4, 0x06000FF0, "Shirt"),
            // Vest 28609 (9, chest-only) -> Lace Shirt 28607 (9 10M 11M 13M 14M): Viamontian family
            [0x100005B7] = new VisualSibling(0x100005B6, 0x020001A6, 0x060057F4, "Lace Shirt"),
            // Breeches 117 (0 1 5) -> Pants 127 (0 1 2 5 6): shins are TEXTURE-only adds
            [0x1000001B] = new VisualSibling(0x10000002, 0x020000DD, 0x06000FEA, "Pants"),
            // Loose Breeches 2602 (0 1M 5M) -> Loose Pants 2601 (0M 1M 2M 5M 6M)
            [0x100000F7] = new VisualSibling(0x100000F6, 0x020000DD, 0x06000FEA, "Loose Pants"),
            // Baggy Breeches 2603 (0 1M 5M) -> Baggy Pants 2598 (0 1M 2M 5M 6M)
            [0x100000F8] = new VisualSibling(0x100000F3, 0x020000DD, 0x06000FEA, "Baggy Pants"),
            // Wide Breeches 2604 (0M 1M 5M) -> Pantaloons 2600 (0M 1M 2M 5M 6M) - owner-confirmed sibling 2026-08-18
            [0x100000F9] = new VisualSibling(0x100000F5, 0x020000DD, 0x06000FE7, "Pantaloons"),
        };

        /// <summary>
        /// TRUE if <paramref name="item"/> carries any of its OWN anim_part, texture_map or palette rows (a
        /// fork-authored look per #652's worn-row merge, e.g. a custom recolour or part swap authored directly
        /// on the item rather than via its ClothingTable). A ClothingBase swap would desync those part-keyed
        /// rows from the new ClothingBase's part layout, so <see cref="TryGetVisualSibling"/> must never apply
        /// over one. This must match the SAME triple gate WorldObject.CalculateObjDesc uses to early-return an
        /// item's own raw rows instead of rendering its ClothingBase (WorldObject_Networking.cs:945-954,
        /// mirrored at Creature_Networking.cs:132 and Corpse.cs:147) - an AnimPart-only item is just as
        /// "own-rendered" as a texture/palette-only one, and would otherwise be swapped and then render its OLD
        /// part-keyed models against the NEW Setup.
        /// </summary>
        public static bool HasOwnVisualRows(WorldObject item) =>
            HasOwnVisualRows(
                item.Biota.PropertiesAnimPart.GetCount(item.BiotaDatabaseLock),
                item.Biota.PropertiesTextureMap.GetCount(item.BiotaDatabaseLock),
                item.Biota.PropertiesPalette.GetCount(item.BiotaDatabaseLock));

        /// <summary>
        /// Pure form of <see cref="HasOwnVisualRows(WorldObject)"/>, testable without constructing a
        /// WorldObject/Biota: TRUE if any of the three row counts is greater than zero.
        /// </summary>
        public static bool HasOwnVisualRows(int animPartCount, int textureCount, int paletteCount) =>
            animPartCount > 0 || textureCount > 0 || paletteCount > 0;

        /// <summary>
        /// Pure lookup: FALSE (no sibling) when <paramref name="clothingBase"/> is null, unmapped in
        /// <see cref="VisualSiblings"/>, or <paramref name="hasOwnVisualRows"/> is true (the item carries its
        /// own anim_part/texture_map/palette rows a swap would desync - see <see cref="HasOwnVisualRows(WorldObject)"/>).
        /// </summary>
        public static bool TryGetVisualSibling(uint? clothingBase, bool hasOwnVisualRows, out VisualSibling sibling)
        {
            sibling = default;

            if (!clothingBase.HasValue || hasOwnVisualRows)
                return false;

            return VisualSiblings.TryGetValue(clothingBase.Value, out sibling);
        }

        /// <summary>
        /// The clause appended to the prompt/success message: names the sibling when a visual swap applies,
        /// or says the look stays as-is when it does not. Factored out pure so it is unit-testable without a
        /// live WorldObject (CoverageLoomTests.cs).
        /// </summary>
        public static string VisualClause(bool hasSibling, string siblingName) =>
            hasSibling
                ? $" The look becomes the full-coverage {siblingName}."
                : " The look stays as it is - only the coverage changes.";

        // ---------------- eligibility (needs a live Player/WorldObject) ----------------

        public static WeenieError VerifyEligible(Player player, WorldObject item, out string refusal)
        {
            refusal = null;

            if (item == null)
            {
                refusal = "The Weaver's Loom needs a garment handed to it.";
                return WeenieError.YouDoNotPassCraftingRequirements;
            }

            if (player.FindObject(item.Guid.Full, Player.SearchLocations.MyInventory) == null)
            {
                refusal = "The Weaver's Loom only works on a garment in your pack, not one you are wearing.";
                return WeenieError.YouDoNotPassCraftingRequirements;
            }

            if (RefireStationCommon.IsStacked(item.StackSize))
            {
                refusal = StackedRefusal;
                return WeenieError.YouDoNotPassCraftingRequirements;
            }

            if (!(item is Clothing) || !IsUndergarment(item.ClothingPriority))
            {
                refusal = "The Weaver's Loom cannot rework that: it is not an undergarment (a shirt, tunic, breeches or pants).";
                return WeenieError.YouDoNotPassCraftingRequirements;
            }

            if (HasFullCoverage(item.ClothingPriority.Value))
            {
                refusal = "The Weaver's Loom cannot rework that: it already has full coverage.";
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

        public static void HandleGive(Player player, WorldObject loom, WorldObject item)
        {
            RefireStationCommon.HandleGive(
                player, loom, item,
                enabledPropertyName: null,
                costNotes: LoomCostNotes,
                insufficientFundsMessage: InsufficientFundsMessage,
                verify: VerifyEligible,
                buildPrompt: BuildPrompt,
                apply: Apply);
        }

        private static string BuildPrompt(Player player, WorldObject item)
        {
            var current = item.ClothingPriority.Value;
            var upgraded = UpgradeCoverage(current);
            var hasSibling = TryGetVisualSibling(item.ClothingBase, HasOwnVisualRows(item), out var sibling);

            return $"Draw out the weave of {item.Name} to full coverage ({Describe(current)} -> {Describe(upgraded)}) for 10 Trade Notes (250,000)? The result is fixed, not random.{VisualClause(hasSibling, sibling.Name)}";
        }

        private static string Apply(Player player, WorldObject item)
        {
            var before = item.ClothingPriority.Value;
            var after = UpgradeCoverage(before);
            var validLocations = item.ValidLocations ?? EquipMask.None;
            var upgradedValidLocations = UpgradeValidLocations(before, validLocations);

            item.ClothingPriority = after;
            item.ValidLocations = upgradedValidLocations;

            var hasSibling = TryGetVisualSibling(item.ClothingBase, HasOwnVisualRows(item), out var sibling);

            if (hasSibling)
            {
                item.ClothingBase = sibling.ClothingBase;
                item.SetupTableId = sibling.Setup;
                item.IconId = sibling.Icon;
            }

            item.ChangesDetected = true;
            item.SaveBiotaToDatabase();

            RefireStationCommon.UpdateObj(player, item);

            return $"The Weaver's Loom reworks the {item.Name}. Coverage: {Describe(before)} -> {Describe(after)}.{VisualClause(hasSibling, sibling.Name)} 10 Trade Notes (250,000) spent.";
        }
    }
}
