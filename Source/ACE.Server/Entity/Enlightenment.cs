using System;
using System.Linq;

using ACE.DatLoader;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.ClassAbilities;
using ACE.Server.WorldObjects;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;

namespace ACE.Server.Entity
{
    /// <summary>
    /// DreamWeave enlightenment rework: an unlimited endgame prestige loop that also raises the character's
    /// personal level cap. Each enlightenment costs 1,000,000 luminance times the next enlightenment number,
    /// requires the character to be at its personal maximum level (275 + 5 per enlightenment), and:
    ///
    /// RESETS  level -> 1, total experience -> 0, unassigned experience -> 0, all skills untrained (skill
    ///         credits refunded, spent XP is NOT refunded), equipped items moved to the pack, enchantments dispelled.
    /// KEEPS   attribute ranks/XP, vital ranks/XP, aetheria, luminance auras, societies, banked luminance,
    ///         quest flags, class abilities.
    /// GRANTS  a permanent, non-redistributable +1 to all six attributes and +1 to all specialized skills
    ///         (folded in getter-only via CreatureAttribute/CreatureSkill so an attribute reset cannot touch
    ///         it), plus +5 to the personal maximum level.
    ///
    /// Triggered by the /enl player command or an Enlightenment emote; both route through
    /// <see cref="HandleEnlightenmentRequest"/>, which gates, confirms, re-verifies, and executes.
    /// </summary>
    public class Enlightenment
    {
        /// <summary>The luminance cost of the player's next enlightenment: 1,000,000 x the next enlightenment number.</summary>
        public static long GetCost(Player player) => 1_000_000L * (player.Enlightenment + 1);

        /// <summary>
        /// Entry point for both the /enl command and the Enlightenment emote. Gates on
        /// <see cref="VerifyRequirements"/>, shows a confirmation dialog, and on confirm re-verifies (state can
        /// change while the dialog is open) before running the reset.
        /// </summary>
        public static void HandleEnlightenmentRequest(Player player, bool confirmed)
        {
            if (!VerifyRequirements(player))
                return;

            if (!confirmed)
            {
                var cost = GetCost(player);
                var enlNext = player.Enlightenment + 1;
                var newMaxLevel = EnlightenmentXpCurve.GetMaxLevelForEnlightenment(enlNext);

                // class-ability-point outcome of THIS enlightenment (DESIGN.md sec 2c), shown only when the
                // lane is active. owed can exceed 1 for a character being caught up from before this lane existed.
                // Kept short: the AC client's confirmation panel is fixed-size and silently CLIPS trailing
                // text past ~560 chars, so the worst-case variant must stay under that.
                var capLine = "";
                if (PropertyManager.GetBool("class_abilities_enabled").Item)
                {
                    var owed = EnlightenmentCapMilestones.EntitledCount(enlNext) - player.EnlightenmentClassAbilityPointsGranted;
                    if (owed > 0)
                        capLine = $"You will receive {owed} class ability point{(owed == 1 ? "" : "s")}.\n\n";
                    else
                        capLine = $"Next class ability point: Enlightenment {EnlightenmentCapMilestones.NextMilestoneAfter(enlNext)}.\n\n";
                }

                var msg = $"Are you sure you want to attain Enlightenment {enlNext}?\n\n" +
                    $"Cost: {cost:N0} luminance.\n\n" +
                    "You will reset to level 1 with 0 XP (unassigned XP included). All skills are untrained: skill credits refunded, experience not. Equipment moves to your pack; enchantments are dispelled.\n\n" +
                    "You KEEP attributes, vitals, aetheria, luminance auras, societies, banked luminance, and quest flags.\n\n" +
                    $"You gain permanently: +1 all attributes, +1 all specialized skills, +5 max level (new cap: {newMaxLevel}).\n\n" +
                    capLine +
                    "You will be returned to your lifestone.";

                if (!player.ConfirmationManager.EnqueueSend(new Confirmation_Custom(player.Guid, () => HandleEnlightenmentRequest(player, true)), msg))
                    player.SendWeenieError(WeenieError.ConfirmationInProgress);

                return;
            }

            // re-verify at confirm time - level, luminance, combat state etc. may have changed while the dialog was open
            if (!VerifyRequirements(player))
                return;

            Execute(player);
        }

        public static bool VerifyRequirements(Player player)
        {
            var maxLevel = player.GetPlayerMaxLevel();
            if (player.Level < maxLevel)
            {
                player.Session.Network.EnqueueSend(new GameMessageSystemChat($"You must be level {maxLevel} for your next enlightenment.", ChatMessageType.Broadcast));
                return false;
            }

            if (player.IsBusy || player.Teleporting || player.suicideInProgress)
            {
                player.Session.Network.EnqueueSend(new GameMessageSystemChat("You cannot enlighten while teleporting or otherwise busy.", ChatMessageType.Broadcast));
                return false;
            }

            if (player.CombatMode != CombatMode.NonCombat)
            {
                player.Session.Network.EnqueueSend(new GameMessageSystemChat("You must be in non-combat mode for enlightenment.", ChatMessageType.Broadcast));
                return false;
            }

            if (player.IsTrading)
            {
                player.Session.Network.EnqueueSend(new GameMessageSystemChat("You cannot enlighten while trading.", ChatMessageType.Broadcast));
                return false;
            }

            if (player.HasVitae)
            {
                player.Session.Network.EnqueueSend(new GameMessageSystemChat("You cannot enlighten while you have a Vitae penalty.", ChatMessageType.Broadcast));
                return false;
            }

            if (player.GetFreeInventorySlots() < 25)
            {
                player.Session.Network.EnqueueSend(new GameMessageSystemChat("You must have at least 25 free inventory slots in your main pack for enlightenment.", ChatMessageType.Broadcast));
                return false;
            }

            var cost = GetCost(player);
            if ((player.AvailableLuminance ?? 0) + player.BankedLuminance < cost)
            {
                player.Session.Network.EnqueueSend(new GameMessageSystemChat($"Enlightenment {player.Enlightenment + 1} costs {cost:N0} luminance.", ChatMessageType.Broadcast));
                return false;
            }

            return true;
        }

        private static void Execute(Player player)
        {
            var cost = GetCost(player);

            // 1. pay the luminance cost (available first, then bank) - abort without any changes if it fails
            if (!player.TrySpendLuminanceIncludingBank(cost))
            {
                player.Session.Network.EnqueueSend(new GameMessageSystemChat($"Enlightenment {player.Enlightenment + 1} costs {cost:N0} luminance.", ChatMessageType.Broadcast));
                return;
            }

            // 2. move equipped items to the pack before skill loss invalidates their wield requirements
            DequipAllItems(player);

            // 3. dispel active enchantments
            player.EnchantmentManager.DispelAllEnchantments();

            // 4. untrain all skills (refunds credits, not XP), recompute available + total skill credits
            RemoveSkills(player);

            // 5. reset level and total experience (AvailableExperience already zeroed inside RemoveSkills)
            RemoveLevel(player);

            // 6. increment the enlightenment count - this is what raises the +1/enl stat floor and the personal cap
            player.Enlightenment += 1;
            player.Session.Network.EnqueueSend(new GameMessagePrivateUpdatePropertyInt(player, PropertyInt.Enlightenment, player.Enlightenment));

            // 7. refresh the client: the +1/enl attribute floor rides NetworkStartingValue (attributes cascade
            //    into derived skills/vitals), vitals rebuild from the raised attributes, and the +1/enl floor on
            //    specialized skills is resent (no skills are specialized right after a reset, but this keeps the
            //    path correct and mirrors the class-ability SendEnhancedStatUpdate precedent)
            foreach (var attribute in player.Attributes.Values)
                player.Session.Network.EnqueueSend(new GameMessagePrivateUpdateAttribute(player, attribute));

            player.SetMaxVitals();

            // the client's own max-health formula adds Enlightenment * 2, and the count above just raised it;
            // resend the corrected GearMaxHealth (see Player.GetNetworkGearMaxHealth) so the health bar's
            // maximum stays on the server's value instead of drifting 2 higher with every enlightenment
            player.Session.Network.EnqueueSend(new GameMessagePrivateUpdatePropertyInt(player, PropertyInt.GearMaxHealth, player.GetNetworkGearMaxHealth()));

            foreach (var kvp in player.Skills)
            {
                if (kvp.Value.AdvancementClass == SkillAdvancementClass.Specialized)
                    player.Session.Network.EnqueueSend(new GameMessagePrivateUpdateSkill(player, kvp.Value));
            }

            // 8. notify the player, and optionally announce server-wide (tunable)
            player.SendMessage($"You have attained Enlightenment {player.Enlightenment} and view the world with new eyes.", ChatMessageType.Broadcast);
            player.SendMessage("Your available skill credits have been restored.", ChatMessageType.Broadcast);

            if (PropertyManager.GetBool("enlightenment_broadcast_enabled").Item)
            {
                var broadcast = $"{player.Name} has attained Enlightenment {player.Enlightenment}!";
                PlayerManager.BroadcastToAll(new GameMessageSystemChat(broadcast, ChatMessageType.WorldBroadcast));
                PlayerManager.LogBroadcastChat(Channel.AllBroadcast, null, broadcast);
            }

            // 9. pay out any enlightenment-milestone class ability points now owed (DESIGN.md sec 2c); the new
            //    Enlightenment count above may have crossed a schedule milestone. Idempotent + gated internally.
            player.GrantEnlightenmentClassAbilityPoints();

            // 10. send the player to their lifestone - an immediate teleport, not the standard lifestone-recall
            //    wind-up (no motion animation, no delay chain). Skip silently for a character that never attuned.
            if (player.Sanctuary != null)
                player.Teleport(new Position(player.Sanctuary));

            // 11. persist
            player.SaveBiotaToDatabase();
        }

        public static void DequipAllItems(Player player)
        {
            var equippedObjects = player.EquippedObjects.Keys.ToList();

            foreach (var equippedObject in equippedObjects)
                player.HandleActionPutItemInContainer(equippedObject.Full, player.Guid.Full, 0);
        }

        public static void RemoveLevel(Player player)
        {
            player.TotalExperience = 0;
            player.Session.Network.EnqueueSend(new GameMessagePrivateUpdatePropertyInt64(player, PropertyInt64.TotalExperience, player.TotalExperience ?? 0));

            player.Level = 1;
            player.Session.Network.EnqueueSend(new GameMessagePrivateUpdatePropertyInt(player, PropertyInt.Level, player.Level ?? 0));
        }

        public static void RemoveSkills(Player player)
        {
            var propertyCount = Enum.GetNames(typeof(Skill)).Length;
            for (var i = 1; i < propertyCount; i++)
            {
                var skill = (Skill)i;

                player.ResetSkill(skill, false);
            }

            player.AvailableExperience = 0;
            player.Session.Network.EnqueueSend(new GameMessagePrivateUpdatePropertyInt64(player, PropertyInt64.AvailableExperience, 0));

            var heritageGroup = DatManager.PortalDat.CharGen.HeritageGroups[(uint)player.Heritage];
            var availableSkillCredits = 0;

            availableSkillCredits += (int)heritageGroup.SkillCredits; // base skill credits allowed

            availableSkillCredits += player.QuestManager.GetCurrentSolves("ArantahKill1");       // additional quest skill credit
            availableSkillCredits += player.QuestManager.GetCurrentSolves("OswaldManualCompleted");  // additional quest skill credit
            availableSkillCredits += player.QuestManager.GetCurrentSolves("LumAugSkillQuest");   // additional quest skill credits

            player.AvailableSkillCredits = availableSkillCredits;

            player.Session.Network.EnqueueSend(new GameMessagePrivateUpdatePropertyInt(player, PropertyInt.AvailableSkillCredits, player.AvailableSkillCredits ?? 0));

            // TotalSkillCredits must be reset to the same recomputed value. CheckForLevelup increments BOTH
            // AvailableSkillCredits and TotalSkillCredits at each credit-granting level; resetting only Available
            // would let every enlightenment cycle permanently inflate TotalSkillCredits as the character re-levels.
            player.TotalSkillCredits = availableSkillCredits;
            player.Session.Network.EnqueueSend(new GameMessagePrivateUpdatePropertyInt(player, PropertyInt.TotalSkillCredits, player.TotalSkillCredits ?? 0));
        }
    }
}
