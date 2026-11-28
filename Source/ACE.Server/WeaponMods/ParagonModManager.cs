using System;
using System.Collections.Generic;

using log4net;

using ACE.Database;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Pvp.Templates;
using ACE.Server.WorldObjects;

namespace ACE.Server.WeaponMods
{
    /// <summary>
    /// The Paragon weapon-mod gem flows (Paragon 51-55): applying a vendor-bought gem to a fully leveled Paragon
    /// weapon, and clearing every gem with the reset item for a partial token refund. Entered from
    /// RecipeManager.UseObjectOnTarget, which CLAIMS both sources by property (never by wcid):
    ///
    ///   gem   - carries PropertyInt.ParagonModGemModifier (the WeaponModId it grants)
    ///   reset - carries PropertyBool.ParagonModReset
    ///
    /// This is deliberately NOT part of <see cref="WeaponModManager"/>: that class's STOP contract (nothing is ever
    /// player-targetable) stays true for Tourmaline and Amethyst. A gem IS a chosen modifier, which is the whole
    /// point of this system, so it lives in its own manager and writes only the Paragon storage
    /// (<see cref="ParagonModRegistry.Slots"/>, ParagonModTokensSpent, ItemMaxLevel, ItemTotalXp). It never writes
    /// a native weapon-mod property or a Tourmaline record.
    ///
    /// Flow shape is WeaponModManager's: busy and peace-mode guards, requirements checked before the animation,
    /// the ClapHands chain, a SECOND check inside the queued action, price taken before the weapon is touched,
    /// then ChangesDetected / SaveBiotaToDatabase and a GameMessageUpdateObject.
    /// </summary>
    public static class ParagonModManager
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>The set ids of the three Paragon weapon sets: ParagonMissile 138, ParagonCaster 139, ParagonMelee 140.</summary>
        public static readonly int[] ParagonSetIds =
        {
            (int)EquipmentSet.ParagonMissile,
            (int)EquipmentSet.ParagonCaster,
            (int)EquipmentSet.ParagonMelee,
        };

        /// <summary>The item level a Paragon weapon starts at, and the level a clear returns it to.</summary>
        public const int BaseMaxLevel = 50;

        /// <summary>The item level at which a weapon holds all five gems.</summary>
        public const int CapMaxLevel = BaseMaxLevel + ParagonModRegistry.MaxGems;

        /// <summary>The server tunable for the refunded fraction of tokens spent. Default 0.8.</summary>
        public const string RefundFractionKey = "paragon_mod_refund_fraction";

        public enum ParagonModAction
        {
            None,
            ApplyGem,
            Reset,
        }

        /// <summary>Why a gem or reset use is refused. Kept apart from message text so the rules are unit-testable without a Player.</summary>
        public enum ParagonModRefusal
        {
            None,

            /// <summary>weapon_mods_enabled is off.</summary>
            Disabled,

            /// <summary>The gem or reset item is not in the player's pack (equipped or elsewhere).</summary>
            SourceNotInPack,

            /// <summary>The weapon is not in the player's pack (it may be equipped).</summary>
            TargetNotInPack,

            /// <summary>The target is not a melee weapon, missile weapon or caster.</summary>
            NotAWeapon,

            /// <summary>The weapon is not in one of the three Paragon sets (138, 139, 140).</summary>
            NotParagonSet,

            /// <summary>
            /// The weapon's ItemXpStyle is not one whose XP round-trips to its level across 50-55 (only Fixed and
            /// FixedPlusBase do; ScalesWithLevel doubles per level and overflows a long).
            /// </summary>
            UnsupportedXpStyle,

            /// <summary>
            /// The XP total for the next level does not fit a long or does not read back as that level, so writing it
            /// would leave the weapon at the wrong level. Refused before the gem is consumed.
            /// </summary>
            XpWouldOverflow,

            /// <summary>ItemMaxLevel is missing, below 50, or above 55: not a Paragon weapon in the gem range.</summary>
            NotParagonLevel,

            /// <summary>The weapon already holds five gems (ItemMaxLevel 55).</summary>
            AtMaxGems,

            /// <summary>The weapon has not reached its current ItemMaxLevel yet.</summary>
            NotFullyLeveled,

            /// <summary>The number of filled slots does not equal ItemMaxLevel - 50, or the slots have a gap.</summary>
            SlotDesync,

            /// <summary>The gem's modifier id is missing or is not a live catalog row.</summary>
            UnknownMod,

            /// <summary>The gem's modifier does not apply to this weapon's class (e.g. Tension is missile-only).</summary>
            WrongClass,

            /// <summary>Reset: the weapon holds no gems.</summary>
            NothingToReset,

            /// <summary>Reset: a refund is owed but the reset item names no usable token currency.</summary>
            MissingCurrency,

            /// <summary>Reset: the refund stack would not fit in the player's pack.</summary>
            NoRoomForRefund,
        }

        // ---------------- classification (pure) ----------------

        /// <summary>TRUE when the item is a Paragon gem (carries a granted modifier id).</summary>
        public static bool IsGem(WorldObject source) => source != null && source.GetProperty(PropertyInt.ParagonModGemModifier) != null;

        /// <summary>TRUE when the item is the Paragon reset item.</summary>
        public static bool IsResetItem(WorldObject source) => source != null && (source.GetProperty(PropertyBool.ParagonModReset) ?? false);

        /// <summary>TRUE for either Paragon source. Detected by property, never by wcid. The RecipeManager and TinkerLock hook.</summary>
        public static bool IsParagonSource(WorldObject source) => IsGem(source) || IsResetItem(source);

        public static ParagonModAction ResolveAction(WorldObject source)
        {
            if (IsGem(source))
                return ParagonModAction.ApplyGem;

            return IsResetItem(source) ? ParagonModAction.Reset : ParagonModAction.None;
        }

        public static bool IsParagonSet(int? setId) => setId != null && Array.IndexOf(ParagonSetIds, setId.Value) >= 0;

        /// <summary>
        /// TRUE for the item XP styles whose total XP converts back to the same level across 50-55: Fixed and
        /// FixedPlusBase. ScalesWithLevel doubles per level, so its level-51 total overflows a long.
        /// </summary>
        public static bool IsSupportedXpStyle(ItemXpStyle? style) => style == ItemXpStyle.Fixed || style == ItemXpStyle.FixedPlusBase;

        /// <summary>
        /// Pure: TRUE when the XP total of level <paramref name="nextMax"/> fits a long (ItemTotalXp is one) and converts
        /// back to exactly that level. The arithmetic wraps silently on overflow, so the read-back is the proof.
        /// </summary>
        public static bool NextTotalReadsBack(ulong baseXp, int nextMax, ItemXpStyle style)
        {
            if (baseXp == 0 || !IsSupportedXpStyle(style))
                return false;

            var total = ExperienceSystem.ItemLevelToTotalXP(nextMax, baseXp, nextMax, style);

            return total <= long.MaxValue && ExperienceSystem.ItemTotalXPToLevel(total, baseXp, nextMax, style) == nextMax;
        }

        /// <summary>
        /// <see cref="NextTotalReadsBack(ulong, int, ItemXpStyle)"/> for a weapon's own base XP, style and max level + 1.
        /// FALSE for an item without level data.
        /// </summary>
        public static bool NextTotalReadsBack(WorldObject weapon)
        {
            if (weapon == null || !weapon.HasItemLevel)
                return false;

            return NextTotalReadsBack((ulong)weapon.ItemBaseXp.Value, weapon.ItemMaxLevel.Value + 1, weapon.ItemXpStyle.Value);
        }

        /// <summary>
        /// The weapon's current item level, safe on every item. WorldObject.ItemLevel dereferences ItemTotalXp, which
        /// HasItemLevel does not check, so a never-leveled item with base XP, style and max level set but no
        /// ItemTotalXp throws there. Null when the item has no level data; 0 when it has it but no XP yet.
        /// </summary>
        public static int? CurrentLevel(WorldObject item)
        {
            if (item == null || !item.HasItemLevel)
                return null;

            return item.ItemTotalXp == null ? 0 : item.ItemLevel;
        }

        /// <summary>
        /// How many Paragon slots a weapon holds: the length of the filled prefix, or -1 when the slots are
        /// malformed (a slot filled after an empty one). -1 never equals a valid count, so it reads as a desync.
        /// </summary>
        public static int ReadFilledSlots(WorldObject weapon)
        {
            var prefix = 0;
            var total = 0;
            var gap = false;

            foreach (var slot in ParagonModRegistry.Slots)
            {
                if (weapon.GetProperty(slot) == null)
                {
                    gap = true;
                    continue;
                }

                total++;

                if (!gap)
                    prefix++;
            }

            return prefix == total ? prefix : -1;
        }

        // ---------------- the rule tables (pure) ----------------

        /// <summary>
        /// Every requirement for applying a gem, over plain values. Order is the order a player should fix
        /// things in. <paramref name="level"/> is the weapon's CURRENT item level (null when it has none);
        /// <paramref name="filledSlots"/> is <see cref="ReadFilledSlots"/>; <paramref name="gemModifier"/> is the
        /// gem's raw ParagonModGemModifier.
        /// </summary>
        public static ParagonModRefusal ResolveGemRefusal(bool enabled, bool sourceInPack, bool targetInPack, WeaponClass weaponClass,
            int? setId, int? maxLevel, int? level, int filledSlots, int? gemModifier, ItemXpStyle? xpStyle, bool nextTotalReadsBack)
        {
            if (!enabled)
                return ParagonModRefusal.Disabled;

            if (!sourceInPack)
                return ParagonModRefusal.SourceNotInPack;

            if (!targetInPack)
                return ParagonModRefusal.TargetNotInPack;

            if (weaponClass == WeaponClass.None || weaponClass == WeaponClass.Shield)
                return ParagonModRefusal.NotAWeapon;

            if (!IsParagonSet(setId))
                return ParagonModRefusal.NotParagonSet;

            if (!IsSupportedXpStyle(xpStyle))
                return ParagonModRefusal.UnsupportedXpStyle;

            if (maxLevel == null || maxLevel.Value < BaseMaxLevel || maxLevel.Value > CapMaxLevel)
                return ParagonModRefusal.NotParagonLevel;

            if (maxLevel.Value >= CapMaxLevel)
                return ParagonModRefusal.AtMaxGems;

            if (level == null || level.Value != maxLevel.Value)
                return ParagonModRefusal.NotFullyLeveled;

            if (filledSlots != maxLevel.Value - BaseMaxLevel)
                return ParagonModRefusal.SlotDesync;

            if (!nextTotalReadsBack)
                return ParagonModRefusal.XpWouldOverflow;

            if (gemModifier == null || !WeaponModRegistry.TryGet((WeaponModId)gemModifier.Value, out var definition))
                return ParagonModRefusal.UnknownMod;

            if (!definition.AppliesTo(weaponClass))
                return ParagonModRefusal.WrongClass;

            return ParagonModRefusal.None;
        }

        /// <summary>
        /// Every requirement for the reset item. <paramref name="refundCount"/> is the tokens owed
        /// (<see cref="RefundFor"/>); <paramref name="currencyWcid"/> is the reset item's AlternateCurrency;
        /// <paramref name="roomForRefund"/> is whether the refund stack(s) fit, which a caller computes only when
        /// a refund is owed.
        /// </summary>
        public static ParagonModRefusal ResolveResetRefusal(bool enabled, bool sourceInPack, bool targetInPack, WeaponClass weaponClass,
            int? setId, int filledSlots, int refundCount, uint? currencyWcid, bool roomForRefund)
        {
            if (!enabled)
                return ParagonModRefusal.Disabled;

            if (!sourceInPack)
                return ParagonModRefusal.SourceNotInPack;

            if (!targetInPack)
                return ParagonModRefusal.TargetNotInPack;

            if (weaponClass == WeaponClass.None || weaponClass == WeaponClass.Shield)
                return ParagonModRefusal.NotAWeapon;

            if (!IsParagonSet(setId))
                return ParagonModRefusal.NotParagonSet;

            // any slot at all counts, including a malformed run (-1): the reset exists to repair those too
            if (filledSlots == 0)
                return ParagonModRefusal.NothingToReset;

            if (refundCount > 0)
            {
                if (currencyWcid == null)
                    return ParagonModRefusal.MissingCurrency;

                if (!roomForRefund)
                    return ParagonModRefusal.NoRoomForRefund;
            }

            return ParagonModRefusal.None;
        }

        /// <summary>
        /// floor(tokensSpent x fraction). The fraction is clamped to [0, 1] (a tunable above 1 must not mint
        /// tokens) and NaN reads 0. A small epsilon keeps a product like 0.29 x 100 = 28.999999999999996 from
        /// flooring one token low.
        /// </summary>
        public static int RefundFor(int tokensSpent, double fraction)
        {
            if (tokensSpent <= 0 || double.IsNaN(fraction) || fraction <= 0.0)
                return 0;

            var clamped = Math.Min(1.0, fraction);

            return (int)Math.Floor(tokensSpent * clamped + 1e-9);
        }

        public static string RefusalMessage(ParagonModRefusal refusal, string sourceName, string targetName)
        {
            switch (refusal)
            {
                case ParagonModRefusal.None:
                    return null;

                case ParagonModRefusal.Disabled:
                    return "Paragon weapon mods are disabled right now.";

                case ParagonModRefusal.SourceNotInPack:
                    return $"The {sourceName} must be in your inventory.";

                case ParagonModRefusal.TargetNotInPack:
                    return $"You must remove the {targetName} and place it in your inventory first.";

                case ParagonModRefusal.NotAWeapon:
                    return $"The {targetName} is not a weapon. Only melee weapons, missile weapons and casters can carry Paragon mods.";

                case ParagonModRefusal.NotParagonSet:
                    return $"The {targetName} is not a Paragon weapon.";

                case ParagonModRefusal.UnsupportedXpStyle:
                    return $"The {targetName} levels in a way Paragon mods cannot follow.";

                case ParagonModRefusal.XpWouldOverflow:
                    return $"The {targetName}'s experience is too large for another Paragon level.";

                case ParagonModRefusal.NotParagonLevel:
                    return $"The {targetName} is not in the Paragon level range for this.";

                case ParagonModRefusal.AtMaxGems:
                    return $"The {targetName} already holds all five Paragon mods.";

                case ParagonModRefusal.NotFullyLeveled:
                    return $"The {targetName} must be fully leveled before it can take another Paragon mod.";

                case ParagonModRefusal.SlotDesync:
                    return $"The {targetName}'s Paragon mods do not match its level. Use a Paragon reset on it first.";

                case ParagonModRefusal.UnknownMod:
                    return $"The {sourceName} does not hold a usable modifier.";

                case ParagonModRefusal.WrongClass:
                    return $"The {sourceName}'s modifier cannot be used on the {targetName}.";

                case ParagonModRefusal.NothingToReset:
                    return $"The {targetName} has no Paragon mods to reset.";

                case ParagonModRefusal.MissingCurrency:
                    return $"The {sourceName} cannot name the tokens to refund. Nothing was changed.";

                case ParagonModRefusal.NoRoomForRefund:
                    return "You do not have room in your pack for the refunded tokens. Nothing was changed.";

                default:
                    return $"The {sourceName} cannot be used on the {targetName}.";
            }
        }

        // ---------------- the mutations (no Player needed) ----------------

        /// <summary>
        /// Writes one gem: the next empty slot, ParagonModTokensSpent += cost, ItemMaxLevel + 1, and ItemTotalXp set
        /// to exactly the XP of the new max level, so the weapon reads newMax/newMax and the mod is live at once.
        /// The caller has already passed <see cref="ResolveGemRefusal"/>. Returns false, writing nothing, if there
        /// is no empty slot or the item carries no level data.
        /// </summary>
        public static bool ApplyGemTo(WorldObject weapon, int modId, int tokenCost)
        {
            if (weapon == null || !weapon.HasItemLevel)
                return false;

            // checked BEFORE any XP arithmetic: ScalesWithLevel overflows, and its level loop never ends on a wrapped value
            if (!IsSupportedXpStyle(weapon.ItemXpStyle))
                return false;

            var filled = ReadFilledSlots(weapon);

            if (filled < 0 || filled >= ParagonModRegistry.MaxGems)
                return false;

            // backstop for the same rule Evaluate applies before the gem is consumed: the new total must fit a long
            // and read back as exactly the new max, or the weapon would not read newMax/newMax
            if (!NextTotalReadsBack(weapon))
                return false;

            var nextMax = weapon.ItemMaxLevel.Value + 1;
            var nextTotal = ExperienceSystem.ItemLevelToTotalXP(nextMax, (ulong)weapon.ItemBaseXp.Value, nextMax, weapon.ItemXpStyle.Value);

            weapon.SetProperty(ParagonModRegistry.Slots[filled], modId);

            weapon.SetProperty(PropertyInt.ParagonModTokensSpent, (weapon.GetProperty(PropertyInt.ParagonModTokensSpent) ?? 0) + Math.Max(0, tokenCost));

            weapon.ItemMaxLevel = nextMax;
            weapon.ItemTotalXp = (long)nextTotal;

            return true;
        }

        /// <summary>
        /// Removes every Paragon gem: ItemMaxLevel back to 50, ItemTotalXp = min(current, XP of level 50), all five
        /// slots and ParagonModTokensSpent removed. Returns the tokens that had been spent (0 when absent). Writes
        /// no native and no Tourmaline record.
        /// </summary>
        public static int ClearGems(WorldObject weapon)
        {
            if (weapon == null)
                return 0;

            var spent = weapon.GetProperty(PropertyInt.ParagonModTokensSpent) ?? 0;

            foreach (var slot in ParagonModRegistry.Slots)
                weapon.RemoveProperty(slot);

            weapon.RemoveProperty(PropertyInt.ParagonModTokensSpent);

            // XP is rewritten only for a style whose totals this class understands; any other style (ScalesWithLevel)
            // keeps its ItemTotalXp and only the max level is reset
            if (weapon.HasItemLevel && IsSupportedXpStyle(weapon.ItemXpStyle))
            {
                var baseXp = (ulong)weapon.ItemBaseXp.Value;
                var style = weapon.ItemXpStyle.Value;
                var level50Xp = (long)ExperienceSystem.ItemLevelToTotalXP(BaseMaxLevel, baseXp, BaseMaxLevel, style);

                weapon.ItemMaxLevel = BaseMaxLevel;
                weapon.ItemTotalXp = Math.Min(weapon.ItemTotalXp ?? 0, level50Xp);
            }
            else if (weapon.ItemMaxLevel != null)
            {
                weapon.ItemMaxLevel = BaseMaxLevel;
            }

            return spent;
        }

        // ---------------- entry point ----------------

        public static void UseObjectOnTarget(Player player, WorldObject source, WorldObject target, bool confirmed = false)
        {
            // CLAIMED even with the system off: falling through would hand a Paragon item to the cookbook, which
            // has no row for it and would only answer "cannot be used on". The Green Jade precedent.
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

            var useError = VerifyUseRequirements(player, source, target, out var firstAction);

            if (useError != WeenieError.None)
            {
                player.SendUseDoneEvent(useError);
                return;
            }

            // Overflow prompt: after every refusal passed and BEFORE any animation or consume. Follows
            // CloakMantleApplicator.UseObjectOnTarget: the use ends here, the Yes answer re-enters with confirmed = true.
            if (ResolveGemFlow(firstAction == ParagonModAction.ApplyGem && GemWouldOverflow(source, target), confirmed) == GemFlow.Prompt)
            {
                var modId = source.GetProperty(PropertyInt.ParagonModGemModifier).Value;
                var modName = WeaponModRegistry.TryGet((WeaponModId)modId, out var promptDefinition) ? promptDefinition.DisplayName : $"modifier {modId}";

                if (!player.ConfirmationManager.EnqueueSend(new Confirmation_ParagonOverflow(player.Guid, source.Guid, target.Guid), OverflowConfirmationText(modName)))
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
                // TOCTOU GUARD - DO NOT DELETE. The player had the whole ClapHands animation to move, drop, equip
                // or destroy either item, so every requirement is checked again against the state as it is NOW.
                var reverifyError = VerifyUseRequirements(player, source, target, out var action);

                if (reverifyError != WeenieError.None)
                {
                    player.SendUseDoneEvent(reverifyError);
                    return;
                }

                if (action == ParagonModAction.ApplyGem)
                    HandleApplyGem(player, source, target);
                else
                    HandleReset(player, source, target);
            });

            actionChain.AddAction(player, () => player.IsBusy = false);

            actionChain.EnqueueChain();

            player.NextUseTime = DateTime.UtcNow.AddSeconds(nextUseTime);
        }

        // ---------------- overflow confirmation (pure decision + text) ----------------

        /// <summary>What a gem use does next: raise the overflow prompt, or run the apply path.</summary>
        public enum GemFlow
        {
            Apply,
            Prompt,
        }

        /// <summary>
        /// Pure: the prompt-vs-apply decision. An overflowing gem prompts unless the player already confirmed;
        /// every other gem, and every confirmed gem, applies.
        /// </summary>
        public static GemFlow ResolveGemFlow(bool overflows, bool confirmed) =>
            overflows && !confirmed ? GemFlow.Prompt : GemFlow.Apply;

        /// <summary>The prompt text, verbatim. <paramref name="modName"/> is the registry display name.</summary>
        public static string OverflowConfirmationText(string modName) =>
            $"This Paragon Amber would push {modName} past a perfect roll, so part of it would be wasted. Apply it anyway?";

        /// <summary>TRUE when this gem, applied to this weapon now, would pass a perfect roll for its mod.</summary>
        public static bool GemWouldOverflow(WorldObject source, WorldObject target)
        {
            var modId = source?.GetProperty(PropertyInt.ParagonModGemModifier);

            return modId != null && WeaponModParagon.WouldOverflow(target, (WeaponModId)modId.Value);
        }

        /// <summary>
        /// The Yes answer of the overflow prompt. The dialog was async, so nothing is trusted: both items are looked
        /// up again, a vanished one gets the normal refusal text, and the use re-enters
        /// <see cref="UseObjectOnTarget"/> with confirmed = true, which re-runs every requirement check.
        /// </summary>
        internal static void OnOverflowConfirmed(Player player, ObjectGuid sourceGuid, ObjectGuid targetGuid)
        {
            var source = player.FindObject(sourceGuid.Full, Player.SearchLocations.LocationsICanMove);
            var target = player.FindObject(targetGuid.Full, Player.SearchLocations.LocationsICanMove);

            if (source == null)
            {
                SendCraftMessage(player, RefusalMessage(ParagonModRefusal.SourceNotInPack, "Paragon Amber", "weapon"));
                return;
            }

            if (target == null)
            {
                SendCraftMessage(player, RefusalMessage(ParagonModRefusal.TargetNotInPack, "Paragon Amber", "weapon"));
                return;
            }

            // The Yes path enters below RecipeManager's gates, so the ones that run before its Paragon intercept
            // are repeated here (same order: mule, PvP template, TinkerLock).
            if (player.MuleBlocked(MuleAction.Craft))
            {
                player.SendUseDoneEvent();
                return;
            }

            var templateRefusal = player.PvpTemplateBlocked(PvpTemplateAction.UseWithTarget, source, target);

            if (templateRefusal != null)
            {
                player.SendPvpTemplateRefusal(templateRefusal);
                player.SendUseDoneEvent();
                return;
            }

            if (TinkerLock.IsRefused(source, target))
            {
                SendCraftMessage(player, TinkerLock.RefusalMessage(source, target));
                player.SendUseDoneEvent();
                return;
            }

            UseObjectOnTarget(player, source, target, true);
        }

        // ---------------- verification ----------------

        /// <summary>
        /// Evaluates the use against the live player, source and target and returns the refusal. Sends nothing.
        /// </summary>
        public static ParagonModRefusal Evaluate(Player player, WorldObject source, WorldObject target, out ParagonModAction action)
        {
            action = ResolveAction(source);

            var enabled = WeaponModRegistry.Enabled();
            var sourceInPack = player.FindObject(source.Guid.Full, Player.SearchLocations.MyInventory) != null;
            var targetInPack = player.FindObject(target.Guid.Full, Player.SearchLocations.MyInventory) != null;

            return EvaluateWith(player, action, source, target, enabled, sourceInPack, targetInPack);
        }

        /// <summary>
        /// The same evaluation with the gate and the pack-location facts supplied, so the admin command can run the
        /// weapon-state rules on an equipped weapon. <paramref name="player"/> is used only for the refund room check.
        /// </summary>
        internal static ParagonModRefusal EvaluateWith(Player player, ParagonModAction action, WorldObject source, WorldObject target,
            bool enabled, bool sourceInPack, bool targetInPack)
        {
            var weaponClass = WeaponClassifier.Classify(target);
            var setId = target.GetProperty(PropertyInt.EquipmentSetId);
            var filled = ReadFilledSlots(target);

            if (action == ParagonModAction.ApplyGem)
            {
                // The level is read only once the target is known to be a Paragon weapon, and through CurrentLevel,
                // which cannot throw on a never-leveled item (WorldObject.ItemLevel does).
                var level = weaponClass != WeaponClass.None && weaponClass != WeaponClass.Shield && IsParagonSet(setId) ? CurrentLevel(target) : null;

                return ResolveGemRefusal(enabled, sourceInPack, targetInPack, weaponClass, setId, target.ItemMaxLevel, level,
                    filled, source.GetProperty(PropertyInt.ParagonModGemModifier), target.ItemXpStyle,
                    level != null && NextTotalReadsBack(target));
            }

            if (action != ParagonModAction.Reset)
                return ParagonModRefusal.UnknownMod;

            var spent = target.GetProperty(PropertyInt.ParagonModTokensSpent) ?? 0;
            var refund = RefundFor(spent, PropertyManager.GetDouble(RefundFractionKey).Item);
            var currency = source.GetProperty(PropertyDataId.AlternateCurrency);

            var roomForRefund = true;

            if (refund > 0 && currency != null && enabled && sourceInPack && targetInPack)
            {
                // No WorldObject is built to ask this: the stack count and burden come from the token weenie's own
                // template numbers (PlanRefund), and the count/burden form of CanAddToInventory answers it.
                var plan = PlanRefundFor(currency.Value, refund);

                if (plan == null)
                {
                    // the currency weenie does not exist: same player-facing answer as no currency at all
                    currency = null;
                }
                else
                {
                    roomForRefund = player.CanAddToInventory(0, plan.Value.Stacks, plan.Value.Burden);
                }
            }

            return ResolveResetRefusal(enabled, sourceInPack, targetInPack, weaponClass, setId, filled, refund, currency, roomForRefund);
        }

        public static WeenieError VerifyUseRequirements(Player player, WorldObject source, WorldObject target, out ParagonModAction action)
        {
            action = ResolveAction(source);

            if (source == target)
            {
                SendCraftMessage(player, $"You can't use the {source.Name} on itself.");
                return WeenieError.YouDoNotPassCraftingRequirements;
            }

            var refusal = Evaluate(player, source, target, out action);

            if (refusal == ParagonModRefusal.None)
                return WeenieError.None;

            SendCraftMessage(player, RefusalMessage(refusal, source.Name, target.Name));

            action = ParagonModAction.None;

            return WeenieError.YouDoNotPassCraftingRequirements;
        }

        // ---------------- application ----------------

        /// <summary>
        /// PRICE FIRST, THEN MUTATE: the gem is consumed before the weapon is touched, and the consume's only failure
        /// happens before it removes anything, so a failure leaves both items as they were.
        /// </summary>
        private static void HandleApplyGem(Player player, WorldObject source, WorldObject target)
        {
            var modId = source.GetProperty(PropertyInt.ParagonModGemModifier).Value;
            var cost = source.GetProperty(PropertyInt.ParagonModGemTokenCost) ?? 1;
            var sourceName = source.Name;

            if (!player.TryConsumeFromInventoryWithNetworking(source, 1))
            {
                log.Error($"ParagonModManager.HandleApplyGem({player.Name}, {sourceName}, {target.Name}): failed to consume the gem");
                player.SendUseDoneEvent(WeenieError.CraftGeneralErrorNoUiMsg);
                return;
            }

            if (!ApplyGemTo(target, modId, cost))
            {
                // unreachable: the second verify covers every condition ApplyGemTo checks
                log.Error($"ParagonModManager.HandleApplyGem({player.Name}, {sourceName}, {target.Name}): the apply wrote nothing AFTER the gem was consumed");
                player.SendUseDoneEvent(WeenieError.CraftGeneralErrorNoUiMsg);
                return;
            }

            target.ChangesDetected = true;
            target.SaveBiotaToDatabase();

            UpdateObj(player, target);

            var name = WeaponModRegistry.TryGet((WeaponModId)modId, out var definition) ? definition.DisplayName : $"modifier {modId}";

            SendCraftMessage(player, $"The {sourceName} fuses with your {target.Name}: +{(int)Math.Round(ParagonModRegistry.FractionPerGem * 100)}% of a perfect {name}. It now reads level {target.ItemMaxLevel}/{target.ItemMaxLevel}.");

            player.SendUseDoneEvent();
        }

        private static void HandleReset(Player player, WorldObject source, WorldObject target)
        {
            var sourceName = source.Name;
            var spent = target.GetProperty(PropertyInt.ParagonModTokensSpent) ?? 0;
            var refund = RefundFor(spent, PropertyManager.GetDouble(RefundFractionKey).Item);

            List<WorldObject> stacks = null;

            if (refund > 0)
            {
                var currency = source.GetProperty(PropertyDataId.AlternateCurrency);

                stacks = currency == null ? null : BuildRefundStacks(currency.Value, refund);

                // the second verify just proved all of this; a null here means the world changed in the same tick
                if (stacks == null || !player.CanAddToInventory(stacks))
                {
                    if (stacks != null)
                    {
                        foreach (var stack in stacks)
                            stack.Destroy();
                    }

                    SendCraftMessage(player, RefusalMessage(ParagonModRefusal.NoRoomForRefund, sourceName, target.Name));
                    player.SendUseDoneEvent(WeenieError.CraftGeneralErrorNoUiMsg);
                    return;
                }
            }

            if (!player.TryConsumeFromInventoryWithNetworking(source, 1))
            {
                log.Error($"ParagonModManager.HandleReset({player.Name}, {sourceName}, {target.Name}): failed to consume the reset item");

                if (stacks != null)
                {
                    foreach (var stack in stacks)
                        stack.Destroy();
                }

                player.SendUseDoneEvent(WeenieError.CraftGeneralErrorNoUiMsg);
                return;
            }

            ClearGems(target);

            target.ChangesDetected = true;
            target.SaveBiotaToDatabase();

            UpdateObj(player, target);

            var granted = 0;

            if (stacks != null)
            {
                foreach (var stack in stacks)
                {
                    var amount = stack.StackSize ?? 1;

                    if (player.TryCreateInInventoryWithNetworking(stack))
                    {
                        granted += amount;
                    }
                    else
                    {
                        log.Error($"ParagonModManager.HandleReset({player.Name}, {sourceName}, {target.Name}): could not place a refund stack of {amount} AFTER the reset item was consumed");
                        stack.Destroy();
                    }
                }
            }

            var resetLine = $"The {sourceName} unravels every Paragon mod on your {target.Name}, which returns to level {BaseMaxLevel}.";

            if (refund > 0 && granted >= refund)
                resetLine += $" {granted} token{(granted == 1 ? "" : "s")} refunded.";
            else if (refund > 0)
                resetLine += $" Only {granted} of {refund} refunded tokens could be placed in your pack. Contact an admin about the rest.";

            SendCraftMessage(player, resetLine);

            player.SendUseDoneEvent();
        }

        /// <summary>
        /// Pure: how many pack slots and how much burden a refund of <paramref name="count"/> tokens needs, given the
        /// token weenie's MaxStackSize (absent reads 1) and per-unit burden. Conservative: it assumes no existing
        /// stack absorbs any of it.
        /// </summary>
        public static (int Stacks, int Burden) PlanRefund(int count, int maxStackSize, int unitBurden)
        {
            if (count <= 0)
                return (0, 0);

            var perStack = Math.Max(1, maxStackSize);
            var stacks = (count + perStack - 1) / perStack;

            return (stacks, Math.Max(0, unitBurden) * count);
        }

        /// <summary>
        /// <see cref="PlanRefund"/> for a currency wcid, read from the cached world weenie template. Null when the
        /// weenie does not exist.
        /// </summary>
        internal static (int Stacks, int Burden)? PlanRefundFor(uint currencyWcid, int count)
        {
            var weenie = DatabaseManager.World.GetCachedWeenie(currencyWcid);

            if (weenie == null)
                return null;

            var maxStack = 1;
            var unitBurden = 0;

            if (weenie.PropertiesInt != null)
            {
                if (weenie.PropertiesInt.TryGetValue(PropertyInt.MaxStackSize, out var max))
                    maxStack = max;

                if (weenie.PropertiesInt.TryGetValue(PropertyInt.StackUnitEncumbrance, out var unit))
                    unitBurden = unit;
                else if (weenie.PropertiesInt.TryGetValue(PropertyInt.EncumbranceVal, out var burden))
                    unitBurden = burden;
            }

            return PlanRefund(count, maxStack, unitBurden);
        }

        /// <summary>
        /// Builds the refund as one or more stacks of the currency weenie, or null when the weenie cannot be
        /// created. The caller destroys any stack it does not hand to the player.
        /// </summary>
        internal static List<WorldObject> BuildRefundStacks(uint currencyWcid, int count)
        {
            var stacks = new List<WorldObject>();
            var remaining = count;

            while (remaining > 0)
            {
                var item = WorldObjectFactory.CreateNewWorldObject(currencyWcid);

                if (item == null)
                {
                    foreach (var stack in stacks)
                        stack.Destroy();

                    return null;
                }

                var perStack = Math.Max(1, (int)(item.MaxStackSize ?? 1));
                var amount = Math.Min(perStack, remaining);

                if (amount > 1)
                    item.SetStackSize(amount);

                stacks.Add(item);
                remaining -= amount;
            }

            return stacks;
        }

        // ---------------- plumbing ----------------

        private static void SendCraftMessage(Player player, string message)
        {
            player.Session.Network.EnqueueSend(new GameMessageSystemChat(message, ChatMessageType.Craft));
        }

        /// <summary>
        /// UpdateObject for the changed weapon. For a weapon in the pack (every player-flow target) it follows
        /// WeaponModManager.UpdateObj exactly: the update, the container re-slot that rebuilds the object
        /// client-side, then VisualEffectManager.Forget so the next equip re-sends any aura. A wielded weapon (only
        /// the admin command can reach one) gets the bare update and no Forget, because forgetting a LIVE emitter
        /// would let the next equip stack a second one. See PackCreateEffectRecordTests.UpdateObjectSites_MatchTheAudit.
        /// </summary>
        internal static void UpdateObj(Player player, WorldObject target)
        {
            player.EnqueueBroadcast(new GameMessageUpdateObject(target));

            if (player.FindObject(target.Guid.Full, Player.SearchLocations.MyInventory) != null)
            {
                player.MoveItemToFirstContainerSlot(target);

                if (player.Session != null)
                    VisualEffectManager.Forget(player.Session, target);
            }
        }
    }
}
