using System;
using System.Collections.Generic;

using log4net;

using ACE.Common;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity.Actions;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.Entity
{
    /// <summary>
    /// Tiger Eye salvage, the ARMOR counterpart of the Prismatic Drift Stone.
    ///
    /// A FULL bag of tiger eye used on a clean (never tinkered, never imbued, never tinker-logged) piece of
    /// armor in the player's pack grants between <see cref="MinTinkers"/> and <see cref="MaxTinkers"/> steel
    /// tinkers at once, always succeeds, consumes the whole bag, and permanently closes the item's tinkering
    /// future (NumTimesTinkered is set to <see cref="LockedTinkerCount"/>). The armor gain is
    /// <see cref="SteelArmorLevelPerTinker"/> per tinker, and the TinkerLog records one
    /// <see cref="MaterialType.Steel"/> entry per tinker, so the item reads afterwards exactly as though the
    /// player had spent that many real steel applications on it.
    ///
    /// WHAT THIS IS NOT. Tiger eye used to be the low tier of the equipment-mod system
    /// (ACE.Server.EquipmentMods): a full bag minted one mod on an unrated item at a FIXED, deterministic
    /// potency, applied through EquipmentModManager's own UseObjectOnTarget. That path was removed when this
    /// manager was built, because the two would otherwise have raced for the same bag - RecipeManager still
    /// claims tiger eye's bag entirely on its own, ahead of the equipment-mod intercept, so that half of the
    /// split still holds unchanged.
    ///
    /// ApplyToArmor DOES call into EquipmentModManager now (repo-owner ruling, 2026-08-08): when its mintMod
    /// parameter is true and the target is an eligible mod carrier (EquipmentModManager.IsEligibleTarget,
    /// which excludes shields), one mod is minted alongside the steel tinkers, at NORMAL potency - the exact
    /// same EquipmentModRoller.RollPotency roll Obsidian uses, via the shared EquipmentModManager.MintMods -
    /// never the removed deterministic floor. That old fixed roll is not coming back: with the terminal seal
    /// below (MatchesAppliedSignature) permanently refusing Obsidian on a tiger-eye'd item, a bottom-locked
    /// roll would leave that item stuck at the worst possible mod forever, with no rework path. See Apply for
    /// where mintMod is decided. <see cref="DefaultMaxStructure"/> and <see cref="IsFullBag"/> stay restated
    /// below rather than reused, because bag-fullness classification is unrelated to the mod grant and the
    /// two managers' bags must never be confused for one another.
    ///
    /// THE TIGER EYE HAMMER (2026-08-08) is the exception to that isolation: it is a multi-charge fuel
    /// source accepted alongside a full bag in <see cref="VerifyUseRequirements"/> and spent through
    /// <see cref="ACE.Server.Entity.SalvageTool.TryConsume"/>, exactly the way EquipmentModManager already
    /// accepts the Obsidian/Tourmaline/Amethyst/Serpentine Hammers. This does NOT reopen the isolation
    /// above: <see cref="ACE.Server.Entity.SalvageTool"/> lives in this same ACE.Server.Entity namespace,
    /// not in ACE.Server.EquipmentMods, so depending on it creates no coupling to EquipmentModManager's own
    /// bag-fullness or mod-minting logic. IsFullBag/DefaultMaxStructure are still restated locally for the
    /// reason given above; SalvageTool is a genuinely shared, namespace-neutral type and reusing it is the
    /// same choice EquipmentModManager and WeaponModManager already made.
    ///
    /// THERE IS NO SERVER-SIDE CONFIRMATION, and that is deliberate rather than an omission. The source is
    /// ItemType.TinkeringMaterial, so the client fires its own generic tinkering-material confirmation ("Are
    /// you sure you want to apply the X to the Y?") entirely client-side before the server is ever contacted,
    /// and it cannot be suppressed from here - see RecipeManager.UseObjectOnTarget's salvage-bag intercepts,
    /// which record that a second server-side panel was pure duplication and was removed. This differs from
    /// PrismaticDriftStone, whose own dialog is necessary only because the stone is a Gem and therefore gets no
    /// client prompt at all.
    ///
    /// The UX shape - busy/combat guards, VerifyUseRequirements, a ClapHands chain that re-verifies inside the
    /// action, then SendUseDoneEvent - follows Entity/PrismaticDriftStone.cs and Entity/CorePlating.cs.
    /// </summary>
    public static class TigerEyeArmorTinker
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// The salvage material this manager answers to. MaterialType 42 (0x2A) is the retail salvage bag
        /// weenie 21081 "materialtigereye", and the fork's pre-filled clone 1001751.
        /// </summary>
        public const MaterialType SourceMaterial = MaterialType.TigerEye;

        /// <summary>
        /// The material written into the TinkerLog, once per granted tinker: a steel armor tinker is what this
        /// bag is emulating, so the item's tinker history has to say steel.
        /// </summary>
        public const MaterialType TinkerMaterial = MaterialType.Steel;

        /// <summary>
        /// Armor level granted per tinker. A retail steel armor tinker is a FLAT +20, not a percentage - see
        /// RecipeManager.TryMutateNative, case 0x38000011 (Steel), in ACE.Server/Managers/RecipeManager.cs,
        /// which is `target.ArmorLevel += 20;`. That legacy mirror is the readable reference for what the live
        /// DAT mutation script does, and this manager writes the same property change directly.
        /// </summary>
        public const int SteelArmorLevelPerTinker = 20;

        public const int MinTinkers = 1;
        public const int MaxTinkers = 5;

        /// <summary>
        /// NumTimesTinkered written on success. The item is treated as fully tinkered no matter how many
        /// tinkers actually landed - this is the permanent lock, not a count of what was applied. It matches
        /// the retail ten-tinker budget, so ordinary tinkering refuses the item afterwards.
        /// </summary>
        public const int LockedTinkerCount = 10;

        /// <summary>
        /// Default salvage bag capacity. The salvage bag weenies carry no MaxStructure of their own - the
        /// salvaging code treats a missing MaxStructure as 100 (Player_Crafting.TryAddSalvage), so a real
        /// in-game bag has Structure set and MaxStructure null. Any fullness test must use the same fallback
        /// or it will reject every legitimate bag. Restated here rather than borrowed from
        /// EquipmentModManager.DefaultMaxStructure on purpose (see the class remarks).
        /// </summary>
        public const int DefaultMaxStructure = 100;

        /// <summary>
        /// What one application did. Returned by <see cref="ApplyToArmor"/> so the caller can report it
        /// without re-deriving anything.
        /// </summary>
        public class Result
        {
            /// <summary>How many steel tinkers landed, in [<see cref="MinTinkers"/>, <see cref="MaxTinkers"/>].</summary>
            public int NumTinkers;

            /// <summary>The total armor level added: <see cref="NumTinkers"/> x <see cref="SteelArmorLevelPerTinker"/>.</summary>
            public int ArmorLevelGained;

            /// <summary>The materials appended to the item's TinkerLog: one steel entry per tinker.</summary>
            public List<MaterialType> Materials = new List<MaterialType>();

            /// <summary>
            /// The report line(s) for the equipment mod minted by this application, if any - see
            /// <see cref="ApplyToArmor"/>'s mintMod parameter. Empty (never null) when no mod was minted,
            /// whether because mintMod was false or the target failed EquipmentModManager.IsEligibleTarget
            /// (e.g. a shield).
            /// </summary>
            public List<string> ModsApplied = new List<string>();
        }

        // ---------------- source classification ----------------

        /// <summary>
        /// TRUE if this item type / material pair is a bag of tiger eye salvage. Both halves matter: a raw
        /// tiger eye GEM carries the same MaterialType but is ItemType.Gem, not TinkeringMaterial, so it must
        /// not be mistaken for a bag.
        ///
        /// ItemType.TinkeringTool is deliberately NOT accepted, and cannot be made to work: the client
        /// intercepts the use of an ItemType.TinkeringTool item to open its own salvage panel and sends
        /// GameActionCreateTinkeringTool (0x027D) instead, so a use-on-target never reaches the server at all.
        /// </summary>
        public static bool IsTigerEyeSalvage(ItemType itemType, MaterialType? materialType)
        {
            if (itemType != ItemType.TinkeringMaterial)
                return false;

            return materialType == SourceMaterial;
        }

        public static bool IsTigerEyeSalvage(WorldObject source) => source != null && IsTigerEyeSalvage(source.ItemType, source.MaterialType);

        /// <summary>
        /// TRUE if a salvage bag holds a FULL unit of material. A whole bag is the price of one application,
        /// so a partial bag is refused rather than partially consumed - there is no fractional application.
        /// </summary>
        public static bool IsFullBag(int? structure, int? maxStructure)
        {
            var max = maxStructure ?? DefaultMaxStructure;

            return max > 0 && (structure ?? 0) >= max;
        }

        public static bool IsFullBag(WorldObject source) => source != null && IsFullBag(source.Structure, source.MaxStructure);

        /// <summary>
        /// TRUE if a source carries a whole application's worth of tiger eye: a FULL bag, or a salvage tool
        /// with at least one charge left. This is THE fuel rule, and <see cref="VerifyUseRequirements"/> is
        /// its only consumer - the two arms must never be spelled out separately at a call site.
        ///
        /// WHY THIS IS A METHOD RATHER THAN AN INLINE DISJUNCTION. The sibling manager records the failure
        /// this prevents: a caller that assembles a rule by hand can assemble it differently from the server,
        /// and then the test and the server agree with themselves while disagreeing with each other (see
        /// ACE.Server.WeaponMods.WeaponModManager's ResolveRefusal(action, target) remarks - "There is now one
        /// wiring, and it is this one"). The tiger eye tests exercise THIS method, so reverting either arm
        /// fails them; asserting IsFullBag(x) || HasUsableCharge(x) in the test would not.
        /// </summary>
        public static bool IsUsableSource(WorldObject source) => IsFullBag(source) || SalvageTool.HasUsableCharge(source);

        // ---------------- target classification ----------------

        /// <summary>
        /// TRUE for a clothing item that covers ONLY an extremity - head, hands or feet. The comparison is
        /// EQUALITY rather than a flag test on purpose: that is what excludes boots and robes, which cover an
        /// extremity plus a non-extremity and so carry more than one bit.
        ///
        /// Mirrors retail's own steel gate at ACE.Server/Managers/RecipeManager_New.cs:277.
        /// </summary>
        public static bool IsExtremityClothing(ItemType itemType, EquipMask validLocations)
        {
            if (itemType != ItemType.Clothing)
                return false;

            return validLocations == EquipMask.HeadWear
                || validLocations == EquipMask.HandWear
                || validLocations == EquipMask.FootWear;
        }

        /// <summary>
        /// The item classes a steel tinker may be applied to: armor outright, or clothing that covers only an
        /// extremity. Mirrors RecipeManager_New.cs:273-280, which is the gate retail's armor-tinker materials
        /// go through. A shield fails it because a shield is ItemType.Armor only when the data says so - the
        /// armor-level and clean-item gates around this one carry the rest of the rule.
        /// </summary>
        public static bool IsEligibleItemClass(ItemType itemType, EquipMask validLocations)
        {
            return itemType == ItemType.Armor || IsExtremityClothing(itemType, validLocations);
        }

        public static bool IsEligibleItemClass(WorldObject target)
        {
            if (target == null)
                return false;

            return IsEligibleItemClass(target.ItemType, target.ValidLocations ?? EquipMask.None);
        }

        /// <summary>
        /// TRUE for an item that carries the base properties a steel tinker needs: a workmanship (so it is a
        /// loot-generated item rather than a static one) and an armor level to raise. Mirrors
        /// RecipeManager_New.cs:270-271.
        /// </summary>
        public static bool HasTinkerableArmor(WorldObject target)
        {
            if (target == null)
                return false;

            return target.Workmanship != null && target.HasArmorLevel();
        }

        /// <summary>
        /// A clean item has never been tinkered, imbued, or tinker-logged. This manager refuses anything else -
        /// it has no slot accounting of its own, it just consumes them all.
        /// </summary>
        public static bool IsCleanArmor(WorldObject target)
        {
            if (target == null)
                return false;

            if (target.NumTimesTinkered != 0)
                return false;

            if (target.ImbuedEffect != ImbuedEffectType.Undef)
                return false;

            if (!string.IsNullOrEmpty(target.TinkerLog))
                return false;

            return true;
        }

        /// <summary>
        /// TRUE when an item's state matches what <see cref="ApplyToArmor"/> leaves behind. Other systems that
        /// rewrite an item's gear ratings or mods (ACE.Server.EquipmentMods) call this to refuse the item
        /// outright, because this manager promises the player the item "can never be tinkered again" and the
        /// repo-owner ruling of 2026-08-08 makes that promise extend to Obsidian too: a Tiger Eye'd item is a
        /// one-way commitment, refused by both the Convert and the Reroll path.
        ///
        /// THIS IS A SIGNATURE, NOT A MARKER - the manager writes no dedicated property, so there is nothing on
        /// the item that says "tiger eye did this"; the signature below is the closest thing available. Unlike
        /// PrismaticDriftStone's equivalent (see its own remarks), this one is UNFORGEABLE by hand tinkering, not
        /// merely unlikely: retail steel adds exactly 1 to NumTimesTinkered per application, so N hand-applied
        /// steel tinkers leave NumTimesTinkered == N, and matching NumTimesTinkered == <see cref="LockedTinkerCount"/>
        /// (10) by hand requires exactly 10 steel log entries. This check requires the log to hold between
        /// <see cref="MinTinkers"/> (1) and <see cref="MaxTinkers"/> (5) entries - strictly fewer than 10 - so no
        /// sequence of hand tinkers can ever produce both conditions at once. Only <see cref="ApplyToArmor"/>'s
        /// own shortcut - stamping the lock immediately, independent of how many log entries were written - can
        /// produce this combination.
        /// </summary>
        public static bool MatchesAppliedSignature(WorldObject target)
        {
            if (target == null)
                return false;

            if (target.NumTimesTinkered != LockedTinkerCount)
                return false;

            var log = target.TinkerLog;

            if (string.IsNullOrWhiteSpace(log))
                return false;

            var entries = new List<uint>();

            foreach (var raw in log.Split(','))
            {
                if (!uint.TryParse(raw.Trim(), out var value))
                    return false;

                entries.Add(value);
            }

            if (entries.Count < MinTinkers || entries.Count > MaxTinkers)
                return false;

            var material = (uint)TinkerMaterial;

            foreach (var entry in entries)
            {
                if (entry != material)
                    return false;
            }

            return true;
        }

        // ---------------- entry point ----------------

        /// <summary>
        /// The player uses a bag of tiger eye salvage on a piece of armor. Mirrors
        /// PrismaticDriftStone.UseObjectOnTarget's busy/combat/motion shape, minus its confirmation round trip:
        /// the client has already asked and been answered before this is ever reached (see the class remarks),
        /// so this runs straight through the animation to the apply.
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

            var useError = VerifyUseRequirements(player, source, target);

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
                var reverifyError = VerifyUseRequirements(player, source, target);

                if (reverifyError != WeenieError.None)
                {
                    player.SendUseDoneEvent(reverifyError);
                    return;
                }

                Apply(player, source, target);
            });

            actionChain.AddAction(player, () => player.IsBusy = false);

            actionChain.EnqueueChain();

            player.NextUseTime = DateTime.UtcNow.AddSeconds(nextUseTime);
        }

        // ---------------- verification ----------------

        /// <summary>
        /// Every requirement, in the order a player most usefully learns about them. Each refusal explains
        /// itself in its own sentence, so a player is never left guessing which of several rules they tripped.
        /// </summary>
        public static WeenieError VerifyUseRequirements(Player player, WorldObject source, WorldObject target)
        {
            if (source == null || target == null)
                return WeenieError.YouDoNotPassCraftingRequirements;

            if (source == target)
            {
                SendCraftMessage(player, $"You can't use the {source.Name} on itself.");
                return WeenieError.YouDoNotPassCraftingRequirements;
            }

            // dispatch guard: the intercept in RecipeManager already tested this, so a failure here means the
            // caller was wrong rather than the player, and there is nothing useful to tell them
            if (!IsTigerEyeSalvage(source))
                return WeenieError.YouDoNotPassCraftingRequirements;

            // inventory only - both the bag and the armor must be somewhere the player can move items
            if (player.FindObject(source.Guid.Full, Player.SearchLocations.LocationsICanMove) == null)
            {
                SendCraftMessage(player, $"The {source.Name} must be somewhere you can move it from.");
                return WeenieError.YouDoNotPassCraftingRequirements;
            }

            if (player.FindObject(target.Guid.Full, Player.SearchLocations.LocationsICanMove) == null)
            {
                SendCraftMessage(player, $"You must place the {target.Name} somewhere you can move it from first.");
                return WeenieError.YouDoNotPassCraftingRequirements;
            }

            // A full bag OR a salvage tool with a charge left. A PARTIAL BAG IS STILL REFUSED, and the two
            // cases are not the same thing: a bag's Structure is a FRACTION of one unit of salvage, so half a
            // bag cannot pay for a whole application, while a tool's Structure is a COUNT of whole
            // applications, so 7 of 10 pays for one perfectly well. PropertyInt.SalvageToolCharges (9035) is
            // the ONLY thing that tells the two apart - see ACE.Server.Entity.SalvageTool, which owns the
            // either/or so this rule exists in exactly one place.
            if (!IsUsableSource(source))
            {
                // a spent tool should not exist - it is destroyed on its last use - but never tell a player
                // holding one that it "is not full", which would send them looking for a fuller Hammer
                if (SalvageTool.IsSalvageTool(source))
                    SendCraftMessage(player, $"The {source.Name} has no uses remaining.");
                else
                    SendCraftMessage(player, $"The {source.Name} is not full. A complete unit of salvage is required.");

                return WeenieError.YouDoNotPassCraftingRequirements;
            }

            if (!HasTinkerableArmor(target))
            {
                SendCraftMessage(player, $"The {target.Name} has no workmanship or no armor level, so a steel tinker has nothing to work with.");
                return WeenieError.YouDoNotPassCraftingRequirements;
            }

            if (!IsEligibleItemClass(target))
            {
                SendCraftMessage(player, $"The {target.Name} cannot take a steel tinker. Only armor and clothing, excluding underclothes, can.");
                return WeenieError.YouDoNotPassCraftingRequirements;
            }

            if (!target.IsEnchantable)
            {
                SendCraftMessage(player, $"The {target.Name} resists magic entirely and cannot be tinkered with steel.");
                return WeenieError.YouDoNotPassCraftingRequirements;
            }

            if (!IsCleanArmor(target))
            {
                SendCraftMessage(player, $"The {target.Name} has already been tinkered or imbued. This salvage only works on untouched equipment.");
                return WeenieError.YouDoNotPassCraftingRequirements;
            }

            return WeenieError.None;
        }

        // ---------------- application ----------------

        private static void Apply(Player player, WorldObject source, WorldObject target)
        {
            // The mod grant is additive to the steel tinkers (repo-owner ruling, 2026-08-08), but it belongs
            // to the equipment-mod system, not to this always-on intercept - RecipeManager's own remarks on
            // the call above explain why TigerEyeArmorTinker itself consults no property. Gating the MOD half
            // on equipment_mods_enabled means turning that system off stops it from minting mods here too,
            // even though the steel tinkers keep firing regardless.
            var mintMod = PropertyManager.GetBool("equipment_mods_enabled").Item;

            var result = ApplyToArmor(target, mintMod);

            target.ChangesDetected = true;
            target.SaveBiotaToDatabase();

            // the source may be destroyed by the consume below, so its name is captured first
            var sourceName = source.Name;
            var wasSalvageTool = SalvageTool.IsSalvageTool(source);

            if (!SalvageTool.TryConsume(player, source, out var remaining))
            {
                log.Error($"TigerEyeArmorTinker.Apply({player.Name}, {sourceName}, {target.Name}): failed to consume the salvage bag");
                player.SendUseDoneEvent(WeenieError.CraftGeneralErrorNoUiMsg);
                return;
            }

            UpdateObj(player, target);

            var plural = result.NumTinkers == 1 ? "tinker" : "tinkers";

            SendCraftMessage(player, $"The tiger eye works {result.NumTinkers} steel {plural} into your {target.Name}, raising its armor by {result.ArmorLevelGained}. It can never be tinkered again.");

            foreach (var line in result.ModsApplied)
                SendCraftMessage(player, $"- {line}");

            var chargeMessage = SalvageTool.GetChargeMessage(sourceName, wasSalvageTool, remaining);

            if (chargeMessage != null)
                SendCraftMessage(player, chargeMessage);

            player.SendUseDoneEvent();
        }

        /// <summary>
        /// The whole mechanic, with no player or networking in it - the rolled tinker count, the armor gain,
        /// the TinkerLog, the permanent tinker lock, and (additively) the equipment-mod grant. Split out so
        /// it is directly testable.
        ///
        /// Callers are responsible for having verified eligibility first (see
        /// <see cref="VerifyUseRequirements"/>); this method does not re-check. DATABASE-FREE except for the
        /// property writes EquipmentModManager.MintMods performs on <paramref name="target"/> itself, which
        /// is the same shape every other write in this method already has - no player, session or DB call.
        /// </summary>
        /// <param name="mintMod">
        /// Whether the additive equipment-mod grant (repo-owner ruling, 2026-08-08) should be attempted.
        /// Callers pass the equipment-mod system's own on/off switch here (see Apply) - this method itself
        /// stays pure and reads no property. When true, a mod is minted ONLY if
        /// <see cref="EquipmentModManager.IsEligibleTarget"/> also accepts the target (this is what excludes
        /// shields, which pass every steel-tinker gate but never carry gear ratings). The steel tinkers, the
        /// armor gain, the TinkerLog and the lock all happen regardless of <paramref name="mintMod"/> or
        /// eligibility - the mod is purely additive and never a precondition for them, and vice versa.
        /// </param>
        public static Result ApplyToArmor(WorldObject target, bool mintMod)
        {
            var result = new Result
            {
                // ThreadSafeRandom.Next's max is INCLUSIVE in this project, so MaxTinkers is passed directly
                // and is genuinely reachable - the same reason PrismaticDriftStone passes MaxBoosts directly.
                NumTinkers = ThreadSafeRandom.Next(MinTinkers, MaxTinkers),
            };

            result.ArmorLevelGained = SteelArmorLevelPerTinker * result.NumTinkers;

            target.ArmorLevel = (target.ArmorLevel ?? 0) + result.ArmorLevelGained;

            for (var i = 0; i < result.NumTinkers; i++)
                result.Materials.Add(TinkerMaterial);

            AppendTinkerLog(target, result.Materials);

            // the permanent lock: fully tinkered regardless of how many tinkers landed
            target.NumTimesTinkered = LockedTinkerCount;

            // ADDITIVE MOD GRANT (repo-owner ruling, 2026-08-08). See the class remarks and this method's own
            // <param> doc for the full reasoning. IsEligibleTarget is what keeps a shield from becoming a mod
            // carrier even though it passes IsEligibleItemClass above (every shield weenie is
            // ItemType.Armor) - lootgen never rolls gear ratings on a shield, and Obsidian excludes it for
            // the same reason, so Tiger Eye's grant must match.
            if (mintMod && EquipmentModManager.IsEligibleTarget(target))
            {
                result.ModsApplied = EquipmentModManager.MintMods(target, 1);
                target.SetProperty(PropertyInt.GearModCapacity, 1);
            }

            return result;
        }

        /// <summary>Same comma separated MaterialType id format as RecipeManager.HandleTinkerLog.</summary>
        private static void AppendTinkerLog(WorldObject target, IEnumerable<MaterialType> materials)
        {
            foreach (var material in materials)
            {
                if (target.TinkerLog != null)
                    target.TinkerLog += ",";

                target.TinkerLog += (uint)material;
            }
        }

        private static void SendCraftMessage(Player player, string message)
        {
            player.Session.Network.EnqueueSend(new GameMessageSystemChat(message, ChatMessageType.Craft));
        }

        /// <summary>
        /// Sends an UpdateObj to the client for the modified item.
        /// Mirrors RecipeManager.UpdateObj - the client moves an updated item to the first container slot, so
        /// the server has to mimic that for persistence.
        /// </summary>
        private static void UpdateObj(Player player, WorldObject obj)
        {
            player.EnqueueBroadcast(new GameMessageUpdateObject(obj));

            if (obj.CurrentWieldedLocation != null)
            {
                player.EnqueueBroadcast(new GameMessageObjDescEvent(player));
                return;
            }

            var invObj = player.FindObject(obj.Guid.Full, Player.SearchLocations.MyInventory);

            if (invObj != null)
                player.MoveItemToFirstContainerSlot(obj);
        }
    }
}
