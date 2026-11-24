using System;

using ACE.Common;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.Entity
{
    /// <summary>
    /// Cloak Mantle gems. A Mantle gem (PropertyBool.CloakRearGuard) gives a cloak the rear guard; an Amplifier gem
    /// (PropertyFloat.CloakProcAmplifier) multiplies the cloak's existing proc. Used on a cloak, the gem stamps the
    /// cloak and is consumed. Keyed on the PROPERTY a Gem carries, never on a wcid, so a second gem weenie is pure
    /// content. A cloak that happens to carry the same property is never a source (the source must be a Gem).
    ///
    /// The flow mirrors <see cref="PrismaticDriftStone.UseObjectOnTarget"/>: busy/combat checks, verify, confirm,
    /// clap animation, re-verify, apply, save, consume, UpdateObj, chat, and a use-done event on every branch. These
    /// are not tinkers, so there is no TinkerLock gate.
    /// </summary>
    public static class CloakMantleApplicator
    {
        public const string RefusalNotCloak = "Only a cloak can take that.";
        public const string RefusalHasMantle = "That cloak already carries a Mantle.";
        public const string RefusalCannotLevel = "That cloak cannot level, so a Mantle would never guard it.";
        public const string RefusalNoProc = "That cloak has no proc to amplify.";
        public static string RefusalHasAmplifier(WorldObject target) => $"The {target.Name} already has a Weave Amplifier.";

        public static bool IsMantle(WorldObject wo)
        {
            return wo is Gem && wo.GetProperty(PropertyBool.CloakRearGuard) == true;
        }

        public static bool IsAmplifier(WorldObject wo)
        {
            return wo is Gem && (wo.GetProperty(PropertyFloat.CloakProcAmplifier) ?? 0.0) > 0.0;
        }

        public const string RollMinKey = "cloak_amplifier_roll_min_pct";
        public const string RollMaxKey = "cloak_amplifier_roll_max_pct";
        public const long RollPctFloor = 1;
        public const long RollPctCeiling = 500;

        /// <summary>
        /// The inclusive whole-percent range an Amplifier rolls in when applied, from the two tunables. Each is clamped to
        /// 1-500; when min is above max, max is used for both.
        /// </summary>
        public static (int Min, int Max) GetRollRangePct()
        {
            var min = Math.Clamp(PropertyManager.GetLong(RollMinKey, 100).Item, RollPctFloor, RollPctCeiling);
            var max = Math.Clamp(PropertyManager.GetLong(RollMaxKey, 200).Item, RollPctFloor, RollPctCeiling);

            if (min > max)
                min = max;

            return ((int)min, (int)max);
        }

        /// <summary>
        /// Rolls an Amplifier's strength: a uniform whole percent in the tunable range. Called once per successful
        /// application, from Apply, after the imbue destroy roll has been survived; the gem is only a marker and is
        /// never mutated, so every gem is identical until it is used.
        /// </summary>
        public static int RollAmplifierPct()
        {
            var (min, max) = GetRollRangePct();

            // ThreadSafeRandom.Next(min, max) is inclusive of max (ACE.Common/ThreadSafeRandom.cs: Next(min, max + 1))
            return ThreadSafeRandom.Next(min, max);
        }

        /// <summary>"100% to 200%", or "150%" when the range collapses to one value.</summary>
        public static string RollRangeText()
        {
            var (min, max) = GetRollRangePct();
            return min == max ? $"{min}%" : $"{min}% to {max}%";
        }

        /// <summary>A whole-number percentage of an amplifier fraction (0.25 -> 25).</summary>
        public static int Pct(double amplifier)
        {
            return (int)Math.Round(amplifier * 100.0, MidpointRounding.AwayFromZero);
        }

        /// <summary>Null when the gem may be applied to the target, otherwise the refusal text. First match wins.</summary>
        public static string Verify(WorldObject source, WorldObject target)
        {
            if (source == null || target == null || !Cloak.IsCloak(target))
                return RefusalNotCloak;

            if (IsMantle(source))
            {
                if (target.GetProperty(PropertyBool.CloakRearGuard) == true)
                    return RefusalHasMantle;

                // the rear guard scales with item level; a cloak that cannot level would stay at 0 forever
                if (!target.HasItemLevel)
                    return RefusalCannotLevel;

                return null;
            }

            if (IsAmplifier(source))
            {
                if (!Cloak.HasDamageProc(target) && !Cloak.HasProcSpell(target))
                    return RefusalNoProc;

                // no repeats: a cloak holds one Amplifier, whatever its strength
                if ((target.GetProperty(PropertyFloat.CloakProcAmplifier) ?? 0.0) > 0.0)
                    return RefusalHasAmplifier(target);

                return null;
            }

            // not a Mantle or Amplifier gem: nothing here can be applied
            return RefusalNotCloak;
        }

        /// <summary>
        /// Stamps the target only. Callers verify first; this does not re-check. An Amplifier stamps amplifierPct
        /// (the roll made by Apply); the gem's own value is a marker and is never read for magnitude.
        /// </summary>
        public static void ApplyTo(WorldObject source, WorldObject target, int amplifierPct)
        {
            if (IsAmplifier(source) && amplifierPct <= 0)
                throw new ArgumentOutOfRangeException(nameof(amplifierPct), amplifierPct, "An Amplifier must be applied with its rolled percent.");

            ApplyValues(IsMantle(source), IsAmplifier(source) ? amplifierPct / 100.0 : 0.0, target);
        }

        /// <summary>Stamps from values captured off the gem, so the gem may already be consumed (destroyed) when this runs.</summary>
        private static void ApplyValues(bool isMantle, double amplifier, WorldObject target)
        {
            if (isMantle)
                target.SetProperty(PropertyBool.CloakRearGuard, true);
            else if (amplifier > 0.0)
                target.SetProperty(PropertyFloat.CloakProcAmplifier, amplifier);
        }

        public static string GetConfirmationText(WorldObject source, WorldObject target)
        {
            if (IsAmplifier(source))
                return $"Amplify the proc of {target.Name} by {RollRangeText()}? This cannot be undone.";

            return $"Apply the Mantle to {target.Name}? This cannot be undone.";
        }

        /// <summary>The confirmation the player sees: the gem specifics plus the imbue risk.</summary>
        public static string GetConfirmationText(WorldObject source, WorldObject target, int chancePercent)
        {
            return GetConfirmationText(source, target) + ImbueGemRisk.ChanceSuffix(chancePercent, target.Name);
        }

        public static string AmplifierSuccessText(WorldObject target, int pct)
        {
            return $"Your {target.Name}'s proc is now {pct}% stronger.";
        }

        public static string MantleSuccessText(WorldObject target)
        {
            return $"The Mantle settles into your {target.Name}. It now guards your back, growing stronger as the cloak levels.";
        }

        public static string GetSuccessText(WorldObject source, WorldObject target, int amplifierPct)
        {
            if (IsAmplifier(source))
            {
                if (amplifierPct <= 0)
                    throw new ArgumentOutOfRangeException(nameof(amplifierPct), amplifierPct, "An Amplifier must be described with its rolled percent.");

                return AmplifierSuccessText(target, amplifierPct);
            }

            return MantleSuccessText(target);
        }

        /// <summary>Player-level checks shared by the first pass and the confirmed re-entry.</summary>
        private static WeenieError VerifyUseRequirements(Player player, WorldObject source, WorldObject target)
        {
            if (source == null || target == null)
                return WeenieError.YouDoNotPassCraftingRequirements;

            if (source == target)
            {
                player.SendTransientError($"You can't use the {source.Name} on itself.");
                return WeenieError.YouDoNotPassCraftingRequirements;
            }

            if (!IsMantle(source) && !IsAmplifier(source))
                return WeenieError.YouDoNotPassCraftingRequirements;

            // a failure destroys the cloak, so a cloak in an open trade is never touched
            if (GreenJadeManager.InTradeWindow(player, source, target))
                return WeenieError.TradeItemBeingTraded;

            if (player.FindObject(source.Guid.Full, Player.SearchLocations.LocationsICanMove) == null)
                return WeenieError.YouDoNotPassCraftingRequirements;

            // the cloak must be in the pack and not worn: a failed roll destroys it, and equipped gear is never at risk
            var inventoryRefusal = GreenJadeManager.TargetInventoryRefusal(player, target);
            if (inventoryRefusal != null)
            {
                player.SendTransientError(inventoryRefusal);
                return WeenieError.YouDoNotPassCraftingRequirements;
            }

            var refusal = Verify(source, target);
            if (refusal != null)
            {
                player.SendTransientError(refusal);
                return WeenieError.YouDoNotPassCraftingRequirements;
            }

            return WeenieError.None;
        }

        public static void UseObjectOnTarget(Player player, WorldObject source, WorldObject target, bool confirmed = false)
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

            // flat gate: the skill only has to be trained; refused before the confirmation, nothing consumed
            var skillRefusal = ImbueGemRisk.SkillRefusal(ImbueGemRisk.CloakGemSkill, player.GetCreatureSkill(ImbueGemRisk.CloakGemSkill).AdvancementClass, source.Name);
            if (skillRefusal != null)
            {
                player.SendTransientError(skillRefusal);
                player.SendUseDoneEvent(WeenieError.YouDoNotPassCraftingRequirements);
                return;
            }

            if (!confirmed)
            {
                var chancePercent = ImbueGemRisk.DisplayPercent(ImbueGemRisk.ChanceFor(player));

                if (!player.ConfirmationManager.EnqueueSend(new Confirmation_CloakMantle(player.Guid, source.Guid, target.Guid), GetConfirmationText(source, target, chancePercent)))
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
                // re-verify: the player had a dialog and an animation's worth of time to change the world
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
            // capture everything the gem supplies BEFORE it is consumed: the consume destroys the gem object
            var failText = ImbueGemRisk.FailureText(source.Name, target.Name);
            var mantleText = MantleSuccessText(target);
            var isMantle = IsMantle(source);
            var isAmplifier = IsAmplifier(source);

            // the gem is consumed on success AND on failure; not a tinker, so no tinker counters or log
            if (!player.TryConsumeFromInventoryWithNetworking(source, 1))
            {
                player.SendUseDoneEvent(WeenieError.CraftGeneralErrorNoUiMsg);
                return;
            }

            if (ImbueGemRisk.Resolve(true, ImbueGemRisk.Roll(ImbueGemRisk.ChanceFor(player))) == ImbueGemOutcome.Destroy)
            {
                ImbueGemRisk.DestroyTarget(player, target, failText);

                player.SendUseDoneEvent();
                return;
            }

            // the Amplifier's strength is rolled here, once, only now that the cloak has survived the imbue
            var amplifierPct = isAmplifier ? RollAmplifierPct() : 0;
            var text = isAmplifier ? AmplifierSuccessText(target, amplifierPct) : mantleText;

            ApplyValues(isMantle, amplifierPct / 100.0, target);

            target.SaveBiotaToDatabase();

            PrismaticDriftStone.UpdateObj(player, target);

            player.Session.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Craft));

            player.SendUseDoneEvent();
        }
    }
}
