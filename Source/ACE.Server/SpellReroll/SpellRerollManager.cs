using System;
using System.Collections.Generic;

using log4net;

using ACE.Common;
using ACE.Database;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.SpellReroll
{
    /// <summary>
    /// The Serpentine spell reroll: using a Bag or Hammer of Serpentine salvage on a piece of gear replaces its
    /// non-cantrip item enchantments with different ones of the SAME LEVEL, drawn from the pool of spells that
    /// item could legitimately have been generated with. Armor carrying Incantation of Armor Self (level 8),
    /// Flame Bane 7 and Invulnerability 7 comes back with one random level 8 and two random level 7
    /// enchantments.
    ///
    /// Entered from RecipeManager.UseObjectOnTarget BEFORE the cookbook lookup, in the same shape as the
    /// equipment-mod and weapon-mod claims that sit just above it. THAT PLACEMENT IS THE FEATURE, not an
    /// optimisation: a claim at the top of UseObjectOnTarget returns before GetRecipe, VerifyRequirements and
    /// TryMutate ever run, and TryMutate is the only caller of HandleTinkerLog. So a reroll never increments
    /// NumTimesTinkered and never appends to TinkerLog, which is what keeps a player rerolling for the roll
    /// they want from flooding the item's tinker history and burning its retail tinker budget.
    ///
    /// INVENTORY ONLY, NEVER EQUIPPED - this one is an exploit guard, not a convenience. An applied item
    /// enchantment is keyed by (spellId, item.Guid) and is removed on dequip by iterating the item's CURRENT
    /// spell book (Creature_Equipment.cs -> Creature_Magic.cs). Swapping the book underneath a worn item would
    /// leave the old enchantments applied with nothing left to remove them, permanently, while the player also
    /// gains the new ones on the next re-equip. That is unbounded buff duplication, so a worn target is
    /// refused outright.
    ///
    /// LEVEL IS PRESERVED, SO THE DIFFICULTY BOOKKEEPING IS NOT TOUCHED. ItemSpellcraft and ItemDifficulty are
    /// deliberately left exactly as they were: they are a function of the spell LEVELS on the item, and no
    /// level changes. ItemMaxMana is likewise left alone, and that IS a known small inaccuracy - two spells of
    /// the same level can carry different BaseMana, so an item's mana capacity can end up slightly stale
    /// relative to a freshly generated equivalent. Accepted for now rather than half-fixed: recomputing it
    /// properly means reproducing the generation-side mana budget, which is a separate piece of work.
    /// </summary>
    public static class SpellRerollManager
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// The designated material. Serpentine (0x47) is unused by any other fork system, and by any retail
        /// recipe that would collide with the claim below.
        /// </summary>
        public const MaterialType RerollMaterial = MaterialType.Serpentine;

        /// <summary>
        /// Default salvage bag capacity, matching EquipmentModManager: the salvage bag weenies carry no
        /// MaxStructure of their own and the salvaging code treats a missing MaxStructure as 100
        /// (Player_Crafting.TryAddSalvage), so any fullness test must use the same fallback.
        /// </summary>
        public const int DefaultMaxStructure = 100;

        // ---------------- classification (pure, no WorldObject needed) ----------------

        /// <summary>
        /// TRUE if this item type / material pair is the Serpentine salvage bag or Hammer. Both halves matter:
        /// a raw Serpentine gem carries the same MaterialType but is ItemType.Gem, not TinkeringMaterial.
        /// </summary>
        public static bool IsRerollMaterial(ItemType itemType, MaterialType? materialType)
        {
            if (itemType != ItemType.TinkeringMaterial)
                return false;

            return materialType == RerollMaterial;
        }

        public static bool IsRerollMaterial(WorldObject source) => source != null && IsRerollMaterial(source.ItemType, source.MaterialType);

        public static bool IsFullBag(int? structure, int? maxStructure)
        {
            var max = maxStructure ?? DefaultMaxStructure;

            return max > 0 && (structure ?? 0) >= max;
        }

        public static bool IsFullBag(WorldObject source) => source != null && IsFullBag(source.Structure, source.MaxStructure);

        // ---------------- entry point ----------------

        /// <summary>
        /// The player uses a Serpentine bag or Hammer on a piece of gear. Mirrors
        /// EquipmentModManager.UseObjectOnTarget: guards, verify, clap, re-verify, act. There is no confirmed
        /// flag and no re-entry - the client fires its own generic tinkering-material confirmation before the
        /// server is ever contacted, so this runs straight through the animation to the apply.
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
                // EQUIP or destroy either item, so every requirement is checked a second time here against the
                // state as it is NOW. The equipped-target check in particular is an exploit guard, and an
                // exploit guard that only runs before an animation is no guard at all.
                var reverifyError = VerifyUseRequirements(player, source, target);

                if (reverifyError != WeenieError.None)
                {
                    player.SendUseDoneEvent(reverifyError);
                    return;
                }

                HandleApply(player, source, target);
            });

            actionChain.AddAction(player, () => player.IsBusy = false);

            actionChain.EnqueueChain();

            player.NextUseTime = DateTime.UtcNow.AddSeconds(nextUseTime);
        }

        // ---------------- verification ----------------

        public static WeenieError VerifyUseRequirements(Player player, WorldObject source, WorldObject target)
        {
            if (!PropertyManager.GetBool("serpentine_reroll_enabled").Item)
                return WeenieError.YouDoNotPassCraftingRequirements;

            if (source == null || target == null)
                return WeenieError.YouDoNotPassCraftingRequirements;

            if (source == target)
            {
                player.SendTransientError($"You can't use the {source.Name} on itself.");
                return WeenieError.YouDoNotPassCraftingRequirements;
            }

            if (!IsRerollMaterial(source))
                return WeenieError.YouDoNotPassCraftingRequirements;

            if (player.FindObject(source.Guid.Full, Player.SearchLocations.MyInventory) == null)
            {
                SendCraftMessage(player, $"The {source.Name} must be in your inventory.");
                return WeenieError.YouDoNotPassCraftingRequirements;
            }

            // INVENTORY ONLY, NEVER EQUIPPED. See the class remarks: item enchantments are keyed
            // (spellId, item.Guid) and are removed on dequip by iterating the item's CURRENT spell book, so
            // swapping the book on a worn item strands the old enchantments applied forever and grants the new
            // ones on top at the next re-equip. This is the buff-duplication guard, not a convenience.
            if (player.FindObject(target.Guid.Full, Player.SearchLocations.MyInventory) == null)
            {
                SendCraftMessage(player, $"You must remove the {target.Name} and place it in your inventory first.");
                return WeenieError.YouDoNotPassCraftingRequirements;
            }

            // A full bag OR a salvage tool with a charge left, exactly as the two mod managers gate it. A
            // bag's Structure is a FRACTION of one unit of salvage so a partial bag cannot pay for an
            // application; a Hammer's Structure is a COUNT of whole applications, so 7 of 10 pays fine.
            if (!IsFullBag(source) && !SalvageTool.HasUsableCharge(source))
            {
                if (SalvageTool.IsSalvageTool(source))
                    SendCraftMessage(player, $"The {source.Name} has no uses remaining.");
                else
                    SendCraftMessage(player, $"The {source.Name} is not full. A complete unit of salvage is required.");

                return WeenieError.YouDoNotPassCraftingRequirements;
            }

            // Feasibility is decided by the SAME planner that performs the reroll, so there is exactly one
            // place that knows what is rerollable. Planned here with a fixed picker rather than the RNG: the
            // question is only whether ANY swap is possible, and the first pending spell's candidate list does
            // not depend on the picker at all.
            var plan = BuildPlan(target, n => 0, out var refusal);

            if (refusal != RerollRefusal.None)
            {
                SendCraftMessage(player, GetRefusalMessage(refusal, target));
                return WeenieError.YouDoNotPassCraftingRequirements;
            }

            if (plan.Swaps.Count == 0)
            {
                SendCraftMessage(player, GetRefusalMessage(RerollRefusal.NothingToReroll, target));
                return WeenieError.YouDoNotPassCraftingRequirements;
            }

            return WeenieError.None;
        }

        /// <summary>
        /// The craft-channel line for each refusal. Kept apart from the decision so the rules stay testable.
        /// </summary>
        public static string GetRefusalMessage(RerollRefusal refusal, WorldObject target)
        {
            var name = target?.Name ?? "item";

            switch (refusal)
            {
                case RerollRefusal.Unclassifiable:
                    // The common case by far: a quest reward or other granted item. It was never rolled from a
                    // treasure profile, so there is no pool to draw a replacement from. Name the rule plainly -
                    // a player who reads this should not go looking for a bug.
                    return $"The {name} was not found as treasure, so the salvage has no pattern to draw from. Only looted equipment can be reforged.";

                case RerollRefusal.NoSpellSelectionCode:
                    return $"The enchantments on the {name} cannot be safely reforged. Its magic does not follow any pattern the salvage recognises.";

                case RerollRefusal.EmptyPool:
                    return $"The enchantments on the {name} cannot be safely reforged. There is nothing this item could be given in their place.";

                default:
                    return $"The {name} has no item enchantments that can be reforged.";
            }
        }

        // ---------------- planning ----------------

        /// <summary>
        /// Builds the reroll plan for a live item.
        ///
        /// EXACT PARITY WITH GENERATION IS THE WHOLE CONTRACT HERE, so every input is taken from generation's
        /// own code rather than re-derived:
        ///   - LootGenerationFactory.GetTreasureRoll classifies the item against the very loot tables
        ///     generation rolls from (extracted from MutateItem so there is one copy, not two);
        ///   - SpellRerollTables.GetItemSpellSource reproduces RollItemSpells' outer gate and four branches;
        ///   - LootGenerationFactory.GetSpellSelectionCode_Dynamic is the code generation actually uses for
        ///     enchantments. NOT WorldObject.SpellSelectionCode / TsysMutationData, which is the route sitting
        ///     commented out above it in RollEnchantments. The two disagree, and where they disagree the
        ///     TsysMutationData group is a pool the item could never have rolled from.
        ///
        /// THE TWO ARMOR LEVELS ARE DELIBERATELY DIFFERENT VALUES, and getting this backwards is the easiest
        /// way to break parity here. Generation reads them at different times on purpose:
        ///   - roll.BaseArmorLevel is captured from a freshly created, UNMUTATED object (CreateAndMutateWcid),
        ///     and feeds the two `BaseArmorLevel > 20` tests in GetSpellCode_Dynamic_ClothingArmor. So it is
        ///     read HERE OFF THE WEENIE. Passing the item's live value instead would push high-workmanship
        ///     headwear and boots into codes 10 and 11 that generation would have given 13 or 18.
        ///   - roll.HasArmorLevel(wo) reads the object's LIVE ArmorLevel, and by the time generation asks, it
        ///     has already been mutated: MutateArmor calls AssignArmorLevel BEFORE AssignMagic
        ///     (LootGenerationFactory_Clothing.cs), and AssignMagic is what reaches RollItemSpells and
        ///     RollEnchantments. So it is passed the LIVE item, which is the same state generation saw.
        ///
        /// Returns a plan and a refusal. WHEN <paramref name="refusal"/> IS NOT None THE PLAN IS EMPTY AND
        /// MUST NOT BE APPLIED: there is deliberately no partial or substitute pool, because a reroll from the
        /// wrong pool is a worse outcome than no reroll at all.
        /// </summary>
        public static SpellRerollPlan BuildPlan(WorldObject target, Func<int, int> next, out RerollRefusal refusal)
        {
            refusal = RerollRefusal.None;

            var roll = LootGenerationFactory.GetTreasureRoll(target, GetBaseArmorLevel(target));

            if (roll == null)
            {
                refusal = RerollRefusal.Unclassifiable;
                return new SpellRerollPlan();
            }

            var source = SpellRerollTables.GetItemSpellSource(roll, roll.HasArmorLevel(target));

            var spellSelectionCode = LootGenerationFactory.GetSpellSelectionCode_Dynamic(target, roll);

            if (!SpellRerollTables.IsValidSpellSelectionCode(spellSelectionCode))
            {
                refusal = RerollRefusal.NoSpellSelectionCode;
                return new SpellRerollPlan();
            }

            var pool = SpellRerollTables.BuildCandidatePool(source, spellSelectionCode);

            if (pool.Count == 0)
            {
                refusal = RerollRefusal.EmptyPool;
                return new SpellRerollPlan();
            }

            var currentSpells = target.Biota.GetKnownSpellsIds(target.BiotaDatabaseLock);

            return SpellRerollTables.Plan(currentSpells, pool, target.SpellDID, target.ProcSpell, next);
        }

        /// <summary>
        /// The BASE weenie's armor level - what generation captured before it mutated the item. Falls back to
        /// the item's own value only if the weenie cannot be read at all, which for a loot item means the
        /// world database has lost its template; the fallback keeps the reroll working rather than refusing on
        /// an infrastructure fault, at the cost of possibly picking a neighbouring clothing/armor code.
        /// </summary>
        private static int GetBaseArmorLevel(WorldObject target)
        {
            var weenie = DatabaseManager.World.GetCachedWeenie(target.WeenieClassId);

            if (weenie == null)
            {
                log.Warn($"SpellRerollManager.GetBaseArmorLevel({target.Name}, {target.WeenieClassId}) - no cached weenie; falling back to the item's live armor level");
                return target.ArmorLevel ?? 0;
            }

            return weenie.GetProperty(PropertyInt.ArmorLevel) ?? 0;
        }

        // ---------------- application ----------------

        private static void HandleApply(Player player, WorldObject source, WorldObject target)
        {
            var plan = BuildPlan(target, n => ThreadSafeRandom.Next(0, n - 1), out var refusal);

            if (refusal != RerollRefusal.None || plan.Swaps.Count == 0)
            {
                // reachable only if the item changed underneath the verify above; never charge for nothing
                SendCraftMessage(player, GetRefusalMessage(refusal, target));
                player.SendUseDoneEvent(WeenieError.YouDoNotPassCraftingRequirements);
                return;
            }

            foreach (var spell in plan.Skipped)
                log.Warn($"SpellRerollManager.HandleApply({player.Name}, {target.Name}): left spell {spell} in place - no usable level progression or no legal replacement");

            // capture the old cast probabilities before removing anything. Lootgen and RecipeManager.AddSpell
            // both write the 2.0f default, but a hand-authored weenie is free to carry another value, and a
            // reroll has no business editing it.
            var oldSpells = new HashSet<int>();

            foreach (var swap in plan.Swaps)
                oldSpells.Add(swap.OldSpell);

            var probabilities = target.Biota.GetMatchingSpells(oldSpells, target.BiotaDatabaseLock);

            foreach (var swap in plan.Swaps)
            {
                if (!target.Biota.TryRemoveKnownSpell(swap.OldSpell, target.BiotaDatabaseLock))
                {
                    log.Error($"SpellRerollManager.HandleApply({player.Name}, {target.Name}): failed to remove spell {swap.OldSpell}");
                    continue;
                }

                var probability = probabilities.TryGetValue(swap.OldSpell, out var p) ? p : 2.0f;

                target.Biota.GetOrAddKnownSpell(swap.NewSpell, target.BiotaDatabaseLock, out _, probability);
            }

            // ItemSpellcraft / ItemDifficulty are intentionally NOT adjusted - every swap preserves its
            // spell's level, so the arcane lore requirement the item was generated with still holds exactly.
            target.ChangesDetected = true;
            target.SaveBiotaToDatabase();

            // the source may be destroyed by the consume below, so its name is captured first
            var sourceName = source.Name;
            var wasSalvageTool = SalvageTool.IsSalvageTool(source);

            if (!SalvageTool.TryConsume(player, source, out var remaining))
            {
                log.Error($"SpellRerollManager.HandleApply({player.Name}, {sourceName}, {target.Name}): failed to consume the salvage");
                player.SendUseDoneEvent(WeenieError.CraftGeneralErrorNoUiMsg);
                return;
            }

            UpdateObj(player, target);

            SendCraftMessage(player, $"The salvage reforges the enchantments on your {target.Name}.");

            foreach (var swap in plan.Swaps)
                SendCraftMessage(player, $"- {GetSpellName(swap.OldSpell)} becomes {GetSpellName(swap.NewSpell)}.");

            var chargeMessage = SalvageTool.GetChargeMessage(sourceName, wasSalvageTool, remaining);

            if (chargeMessage != null)
                SendCraftMessage(player, chargeMessage);

            player.SendUseDoneEvent();
        }

        /// <summary>
        /// A spell's display name, falling back to the raw id rather than throwing if the DAT does not know it.
        ///
        /// Loaded DAT-only (loadDB: false), because the name is the only thing wanted. That is also why the
        /// null test is on _spellBase directly and NOT on Spell.NotFound: NotFound is
        /// (_spellBase == null || _spell == null), and _spell is null BY CONSTRUCTION whenever loadDB is
        /// false, so NotFound would be true for every spell in the game here. Spell.Name dereferences
        /// _spellBase with no guard of its own.
        /// </summary>
        private static string GetSpellName(int spellId)
        {
            var spell = new Spell(spellId, false);

            if (spell._spellBase == null || string.IsNullOrEmpty(spell.Name))
                return $"spell {spellId}";

            return spell.Name;
        }

        private static void SendCraftMessage(Player player, string message)
        {
            player.Session.Network.EnqueueSend(new GameMessageSystemChat(message, ChatMessageType.Craft));
        }

        /// <summary>
        /// Sends an UpdateObj for the modified target, mirroring RecipeManager.UpdateObj. The equipped branch
        /// of that helper is omitted: targets here are verified inventory-only by construction.
        /// </summary>
        private static void UpdateObj(Player player, WorldObject target)
        {
            player.EnqueueBroadcast(new GameMessageUpdateObject(target));

            // the client moves an item to the first container slot when it receives an UpdateObject,
            // so mirror that server side for persistence
            if (player.FindObject(target.Guid.Full, Player.SearchLocations.MyInventory) != null)
                player.MoveItemToFirstContainerSlot(target);

            // the UpdateObject above just rebuilt this object client-side, which drops any persistent
            // particle aura (VisualEffectManager). target is inventory-only by construction here, so
            // reopen the once-only send guard rather than resending: sending to a packed item would
            // attach the script to nothing and spend the guard that equipping it later depends on.
            VisualEffectManager.Forget(player.Session, target);
        }
    }
}
