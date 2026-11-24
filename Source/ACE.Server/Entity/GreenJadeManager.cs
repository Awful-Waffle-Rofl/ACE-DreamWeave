using System;

using log4net;

using ACE.Common;
using ACE.Entity.Enum;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.SpellReroll;
using ACE.Server.WorldObjects;

namespace ACE.Server.Entity
{
    /// <summary>
    /// Green Jade (Docs/JewelryAspects/DESIGN.md Part 2): a full Green Jade salvage bag, or one charge of a Green Jade
    /// Hammer, used on an ASPECTED ring, bracelet or necklace adds one random aspect while it has fewer than ten and, once it has ten, swaps one of
    /// them for a different type, at random. It ALWAYS succeeds: no skill check, no failure, no item destruction.
    ///
    /// Entered from RecipeManager.UseObjectOnTarget BEFORE the cookbook lookup, in the shape of the Serpentine reroll
    /// (SpellRerollManager). That placement is the feature: GetRecipe, VerifyRequirements and TryMutate (the only caller of
    /// the tinker-log writer) are never reached, so a step never touches the retail tinker counter or the tinker history. There is no
    /// confirmed flag: the client fires its own generic tinkering-material confirmation before the server is contacted.
    ///
    /// INVENTORY ONLY, NEVER EQUIPPED. Worn jewelry feeds Fortitude, Penetration and Resolve live, so a piece is never
    /// rewritten underneath a wearer; the target must be in the player's own pack.
    ///
    /// <see cref="ApplyStep"/> is a pure function of the piece (no player, no network, no save, no fuel) so the Salvage
    /// Roller can run it on a working copy; <see cref="TargetStateRefusal"/> and <see cref="TargetInventoryRefusal"/> are
    /// the target half of the manual path's checks, exposed for the same reuse.
    /// </summary>
    public static class GreenJadeManager
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public const MaterialType GreenJadeMaterial = MaterialType.GreenJade;

        public const string RefusalDisabled = "Jewelry Aspects are disabled right now.";
        public const string RefusalNotJewelry = "Aspects can only be given to rings, bracelets and necklaces.";

        /// <summary>The production draw: a uniform value in [0, n). ThreadSafeRandom.Next's maximum is INCLUSIVE.</summary>
        public static readonly Func<int, int> DefaultNext = n => ThreadSafeRandom.Next(0, n - 1);

        /// <summary>Test seam: whether the target is in the player's own inventory. Production asks FindObject.</summary>
        internal static Func<Player, WorldObject, bool> InMyInventory = (player, target) => player.FindObject(target.Guid.Full, Player.SearchLocations.MyInventory) != null;

        // ---------------- classification ----------------

        /// <summary>TRUE for the Green Jade salvage bag or Hammer: ItemType TinkeringMaterial and MaterialType Green Jade.</summary>
        public static bool IsGreenJadeMaterial(WorldObject source)
        {
            return source != null && source.ItemType == ItemType.TinkeringMaterial && source.MaterialType == GreenJadeMaterial;
        }

        /// <summary>TRUE when either item is in (or inside a container in) the player's open trade window, as Player_Use refuses it.</summary>
        internal static bool InTradeWindow(Player player, WorldObject source, WorldObject target)
        {
            return player.IsTrading
                && (source.IsBeingTradedOrContainsItemBeingTraded(player.ItemsInTradeWindow)
                    || target.IsBeingTradedOrContainsItemBeingTraded(player.ItemsInTradeWindow));
        }

        // ---------------- target-only verification (shared with the Salvage Roller) ----------------

        /// <summary>The refusal line for a worn or out-of-pack target, or null when it is in the player's inventory.</summary>
        internal static string TargetInventoryRefusal(Player player, WorldObject target)
        {
            if ((target.CurrentWieldedLocation ?? EquipMask.None) != EquipMask.None || player.EquippedObjects.ContainsKey(target.Guid))
                return $"Take the {target.Name} off first.";

            if (!InMyInventory(player, target))
                return $"You must remove the {target.Name} and place it in your inventory first.";

            return null;
        }

        /// <summary>
        /// Whether the target can be swapped, as the refusal line the player is sent, or null when it can: a piece that
        /// is not a ring, bracelet or necklace, then a piece with no Aspect yet. The disabled switch is the caller's.
        /// </summary>
        internal static string TargetStateRefusal(WorldObject target)
        {
            if (!JewelryAspectMath.IsAspectablePiece(target))
                return RefusalNotJewelry;

            if (!JewelryAspectMath.IsAspected(target))
                return $"The {target.Name} has no Aspect yet. Use a Jewelers Aspect Gem on it first.";

            return null;
        }

        /// <summary>The whole target-only verify, in the bag path's order. True when eligible; sends nothing.</summary>
        internal static bool VerifyTarget(Player player, WorldObject target, out string refusalMessage)
        {
            refusalMessage = TargetInventoryRefusal(player, target) ?? TargetStateRefusal(target);

            return refusalMessage == null;
        }

        /// <summary>The fuel refusal line (a full bag, or a Hammer with a charge left), or null when the source can pay.</summary>
        internal static string FuelRefusal(WorldObject source)
        {
            if (SpellRerollManager.IsFullBag(source) || SalvageTool.HasUsableCharge(source))
                return null;

            if (SalvageTool.IsSalvageTool(source))
                return $"The {source.Name} has no uses remaining.";

            return $"The {source.Name} is not full. A complete unit of salvage is required.";
        }

        // ---------------- the swap ----------------

        /// <summary>
        /// Applies whichever step the piece is in: below 10 aspects it ADDS one of a uniform random type; at 10 it SWAPS one
        /// slot for a different type. Rewrites the three counts and reports which step ran. Pure - no player, no network, no
        /// save, no fuel. For an add, <paramref name="from"/> and <paramref name="to"/> are both the type added. The piece
        /// must be aspected (1 to 10); the caller verifies.
        /// </summary>
        public static AspectCounts ApplyStep(WorldObject piece, Func<int, int> next, out bool added, out AspectType from, out AspectType to)
        {
            var counts = JewelryAspectMath.ReadCounts(piece);

            if (counts.Total < JewelryAspectMath.SlotCount)
            {
                counts = JewelryAspectMath.Add(counts, next, out to);
                from = to;
                added = true;
            }
            else
            {
                counts = JewelryAspectMath.Swap(counts, next, out from, out to);
                added = false;
            }

            JewelryAspectMath.WriteCounts(piece, counts);

            return counts;
        }

        public static string GetSuccessText(string itemName, bool added, AspectType from, AspectType to, AspectCounts counts)
        {
            if (added)
                return $"The Green Jade adds an aspect to the {itemName}: {to}. Now Attack {counts.Attack}, Defense {counts.Defense}, Utility {counts.Utility}.";

            return $"The Green Jade shifts an aspect on the {itemName}: {from} becomes {to}. Now Attack {counts.Attack}, Defense {counts.Defense}, Utility {counts.Utility}.";
        }

        /// <summary>What a paid step (an add or a swap) produced.</summary>
        internal sealed class StepResult
        {
            public bool Added;
            public AspectType From;
            public AspectType To;
            public AspectCounts Counts;
            public int Remaining;
            public bool WasSalvageTool;
            public string SourceName;
        }

        /// <summary>
        /// PRICE FIRST, THEN MUTATE: <paramref name="consume"/> takes the bag or the Hammer charge (it returns the charges
        /// left, or null when it could not), and only then is the piece rewritten, so a failed price never leaves a free swap.
        /// Returns null, with the piece untouched, when the price could not be taken.
        /// </summary>
        internal static StepResult PayAndStep(WorldObject source, WorldObject target, Func<int, int> next, Func<WorldObject, int?> consume)
        {
            // the source may be destroyed by the consume, so what the messages need is captured first
            var sourceName = source.Name;
            var wasSalvageTool = SalvageTool.IsSalvageTool(source);

            var remaining = consume(source);

            if (remaining == null)
                return null;

            var counts = ApplyStep(target, next, out var added, out var from, out var to);

            return new StepResult { Added = added, From = from, To = to, Counts = counts, Remaining = remaining.Value, WasSalvageTool = wasSalvageTool, SourceName = sourceName };
        }

        // ---------------- entry point ----------------

        /// <summary>
        /// The player uses a Green Jade bag or Hammer on a piece of jewelry: guards, verify, clap, re-verify, act. The mule
        /// craft gate and the TinkerLock sit above this in RecipeManager.UseObjectOnTarget and Player_Use.
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
                if (player.MuleBlocked(MuleAction.Craft))
                {
                    player.SendUseDoneEvent();
                    return;
                }

                // TOCTOU GUARD: the player had the whole animation to equip, drop or destroy either item
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

        public static WeenieError VerifyUseRequirements(Player player, WorldObject source, WorldObject target)
        {
            if (!PropertyManager.GetBool("jewelry_aspects_enabled").Item)
            {
                SendCraftMessage(player, RefusalDisabled);
                return WeenieError.YouDoNotPassCraftingRequirements;
            }

            if (source == null || target == null)
                return WeenieError.YouDoNotPassCraftingRequirements;

            if (source == target)
            {
                player.SendTransientError($"You can't use the {source.Name} on itself.");
                return WeenieError.YouDoNotPassCraftingRequirements;
            }

            if (InTradeWindow(player, source, target))
                return WeenieError.TradeItemBeingTraded;

            if (!IsGreenJadeMaterial(source))
                return WeenieError.YouDoNotPassCraftingRequirements;

            if (!InMyInventory(player, source))
            {
                SendCraftMessage(player, $"The {source.Name} must be in your inventory.");
                return WeenieError.YouDoNotPassCraftingRequirements;
            }

            var targetInventoryRefusal = TargetInventoryRefusal(player, target);

            if (targetInventoryRefusal != null)
            {
                SendCraftMessage(player, targetInventoryRefusal);
                return WeenieError.YouDoNotPassCraftingRequirements;
            }

            var fuelRefusal = FuelRefusal(source);

            if (fuelRefusal != null)
            {
                SendCraftMessage(player, fuelRefusal);
                return WeenieError.YouDoNotPassCraftingRequirements;
            }

            var targetStateRefusal = TargetStateRefusal(target);

            if (targetStateRefusal != null)
            {
                SendCraftMessage(player, targetStateRefusal);
                return WeenieError.YouDoNotPassCraftingRequirements;
            }

            return WeenieError.None;
        }

        private static void HandleApply(Player player, WorldObject source, WorldObject target)
        {
            var result = PayAndStep(source, target, DefaultNext, s => SalvageTool.TryConsume(player, s, out var remaining) ? remaining : (int?)null);

            if (result == null)
            {
                log.Error($"GreenJadeManager.HandleApply({player.Name}, {source.Name}, {target.Name}): failed to consume the salvage");
                player.SendUseDoneEvent(WeenieError.CraftGeneralErrorNoUiMsg);
                return;
            }

            target.ChangesDetected = true;
            target.SaveBiotaToDatabase();

            PrismaticDriftStone.UpdateObj(player, target);

            SendCraftMessage(player, GetSuccessText(target.Name, result.Added, result.From, result.To, result.Counts));

            var chargeMessage = SalvageTool.GetChargeMessage(result.SourceName, result.WasSalvageTool, result.Remaining);

            if (chargeMessage != null)
                SendCraftMessage(player, chargeMessage);

            player.SendUseDoneEvent();
        }

        private static void SendCraftMessage(Player player, string message)
        {
            player.Session.Network.EnqueueSend(new GameMessageSystemChat(message, ChatMessageType.Craft));
        }
    }
}
