using System;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.Entity
{
    /// <summary>
    /// The Jewelers Aspect Gem (Docs/JewelryAspects/DESIGN.md Part 1). Used on an unaspected ring, bracelet or necklace in the
    /// player's pack that is NOT worn, the gem gives it ONE aspect of a uniform random type (Attack, Defense or Utility), writes the
    /// three counts and is consumed. Green Jade then adds more, up to ten. Keyed on the PROPERTY a Gem carries (PropertyBool.JewelryAspectGem), never on a wcid,
    /// so a second gem weenie is pure content.
    ///
    /// The flow mirrors <see cref="CloakMantleApplicator"/>: busy/combat checks, verify, server confirmation, clap
    /// animation, re-verify, apply, and a use-done event on every exit. It is not a tinker: the retail tinker counter and the
    /// tinker log are never read or written. The mule craft gate sits here (a gem never passes through RecipeManager);
    /// TinkerLock refuses at Player_Use before this is reached.
    /// </summary>
    public static class JewelryAspectApplicator
    {
        public const string RefusalNotJewelry = GreenJadeManager.RefusalNotJewelry;
        public const string RefusalDisabled = GreenJadeManager.RefusalDisabled;

        public static string RefusalHasAspect(WorldObject target) => $"The {target.Name} already has its Aspect.";

        public static bool IsAspectGem(WorldObject wo)
        {
            return wo is Gem && wo.GetProperty(PropertyBool.JewelryAspectGem) == true;
        }

        /// <summary>
        /// Target-state check: null when the target may take an Aspect, otherwise the refusal text. First match wins: the
        /// switch, then a piece that is not a ring, bracelet or necklace, then a piece that already has its Aspect.
        /// </summary>
        public static string Verify(WorldObject target)
        {
            if (!PropertyManager.GetBool("jewelry_aspects_enabled").Item)
                return RefusalDisabled;

            if (!JewelryAspectMath.IsAspectablePiece(target))
                return RefusalNotJewelry;

            if (JewelryAspectMath.IsAspected(target))
                return RefusalHasAspect(target);

            return null;
        }

        /// <summary>Rolls and writes the first aspect. Callers verify first; this does not re-check. Pure of player and network.</summary>
        public static AspectCounts ApplyTo(WorldObject target, Func<int, int> next)
        {
            var counts = JewelryAspectMath.Add(default, next, out _);

            JewelryAspectMath.WriteCounts(target, counts);

            return counts;
        }

        public static string GetConfirmationText(WorldObject target)
        {
            return $"Give the {target.Name} a permanent Aspect? Its first aspect will be rolled at random.";
        }

        /// <summary>The confirmation the player sees: the Aspect specifics plus the imbue risk.</summary>
        public static string GetConfirmationText(WorldObject target, int chancePercent)
        {
            return GetConfirmationText(target) + ImbueGemRisk.ChanceSuffix(chancePercent, target.Name);
        }

        public static string GetSuccessText(WorldObject target, AspectCounts counts)
        {
            return $"The {target.Name} takes on its Aspect: Attack {counts.Attack}, Defense {counts.Defense}, Utility {counts.Utility}.";
        }

        /// <summary>Player-level checks shared by the first pass and the confirmed re-entry.</summary>
        private static WeenieError VerifyUseRequirements(Player player, WorldObject source, WorldObject target)
        {
            if (!PropertyManager.GetBool("jewelry_aspects_enabled").Item)
            {
                player.SendTransientError(RefusalDisabled);
                return WeenieError.YouDoNotPassCraftingRequirements;
            }

            if (source == null || target == null)
                return WeenieError.YouDoNotPassCraftingRequirements;

            if (source == target)
            {
                player.SendTransientError($"You can't use the {source.Name} on itself.");
                return WeenieError.YouDoNotPassCraftingRequirements;
            }

            if (GreenJadeManager.InTradeWindow(player, source, target))
                return WeenieError.TradeItemBeingTraded;

            if (!IsAspectGem(source))
                return WeenieError.YouDoNotPassCraftingRequirements;

            if (!GreenJadeManager.InMyInventory(player, source))
                return WeenieError.YouDoNotPassCraftingRequirements;

            // the target must be in the pack and not worn: worn jewelry feeds live damage layers
            var inventoryRefusal = GreenJadeManager.TargetInventoryRefusal(player, target);
            if (inventoryRefusal != null)
            {
                player.SendTransientError(inventoryRefusal);
                return WeenieError.YouDoNotPassCraftingRequirements;
            }

            var refusal = Verify(target);
            if (refusal != null)
            {
                player.SendTransientError(refusal);
                return WeenieError.YouDoNotPassCraftingRequirements;
            }

            return WeenieError.None;
        }

        public static void UseObjectOnTarget(Player player, WorldObject source, WorldObject target, bool confirmed = false)
        {
            // Mule (WaffleACE): a mule cannot craft or tinker; the gem reaches here from Gem.cs, not RecipeManager
            if (player.MuleBlocked(MuleAction.Craft))
            {
                player.SendUseDoneEvent();
                return;
            }

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

            // flat gate: the skill only has to be trained; refused before the confirmation, nothing consumed
            var skillRefusal = ImbueGemRisk.SkillRefusal(ImbueGemRisk.AspectSkill, player.GetCreatureSkill(ImbueGemRisk.AspectSkill).AdvancementClass, source.Name);
            if (skillRefusal != null)
            {
                player.SendTransientError(skillRefusal);
                player.SendUseDoneEvent(WeenieError.YouDoNotPassCraftingRequirements);
                return;
            }

            if (!confirmed)
            {
                var chancePercent = ImbueGemRisk.DisplayPercent(ImbueGemRisk.ChanceFor(player));

                if (!player.ConfirmationManager.EnqueueSend(new Confirmation_JewelryAspect(player.Guid, source.Guid, target.Guid), GetConfirmationText(target, chancePercent)))
                {
                    player.SendUseDoneEvent(WeenieError.ConfirmationInProgress);
                    return;
                }

                player.SendUseDoneEvent();
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
                // re-verify: the player had a dialog and an animation's worth of time to equip or destroy either item
                if (player.MuleBlocked(MuleAction.Craft))
                {
                    player.SendUseDoneEvent();
                    return;
                }

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

        private static void Apply(Player player, WorldObject source, WorldObject target)
        {
            // PRICE FIRST, THEN MUTATE: a gem that could not be taken must not leave a free Aspect behind
            if (!player.TryConsumeFromInventoryWithNetworking(source, 1))
            {
                player.SendUseDoneEvent(WeenieError.CraftGeneralErrorNoUiMsg);
                return;
            }

            // the gem is spent either way; a failed roll destroys the target. Not a tinker: no tinker counters or log
            if (ImbueGemRisk.Resolve(true, ImbueGemRisk.Roll(ImbueGemRisk.ChanceFor(player))) == ImbueGemOutcome.Destroy)
            {
                ImbueGemRisk.DestroyTarget(player, target, ImbueGemRisk.FailureText(source.Name, target.Name));

                player.SendUseDoneEvent();
                return;
            }

            var counts = ApplyTo(target, GreenJadeManager.DefaultNext);

            target.ChangesDetected = true;
            target.SaveBiotaToDatabase();

            PrismaticDriftStone.UpdateObj(player, target);

            player.Session.Network.EnqueueSend(new GameMessageSystemChat(GetSuccessText(target, counts), ChatMessageType.Craft));

            player.SendUseDoneEvent();
        }
    }
}
