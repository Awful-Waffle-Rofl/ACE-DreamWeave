using System;
using System.Globalization;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.ClassAbilities;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;

namespace ACE.Server.Command.Handlers
{
    /// <summary>
    /// Player-facing summary of the equipment-mod system (Source/ACE.Server/EquipmentMods/*): what mods are
    /// currently active across a player's equipped gear, and (in items mode) which item carries which mod.
    /// Everything here is read-only and computed live through the same paths the combat hooks and the
    /// appraisal panel use (EquipmentModDisplay, Creature_EquipmentMods), so this command can never drift
    /// from what is actually applied.
    /// </summary>
    public static class EquipmentModCommands
    {
        [CommandHandler("mods", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 0,
            "Summarize the equipment mods on your equipped armor, clothing and jewelry",
            "[items]")]
        public static void HandleMods(Session session, params string[] parameters)
        {
            if (!PropertyManager.GetBool("equipment_mods_enabled").Item)
            {
                Reply(session, "Equipment mods are not currently enabled on this server.");
                return;
            }

            var subcommand = parameters.Length > 0 ? parameters[0].ToLowerInvariant() : "summary";

            switch (subcommand)
            {
                case "summary":
                    HandleSummary(session);
                    break;
                case "items":
                    HandleItems(session);
                    break;
                default:
                    Reply(session, "Usage: /mods [items]");
                    break;
            }
        }

        private static void Reply(Session session, string message)
        {
            session.Network.EnqueueSend(new GameMessageSystemChat(message, ChatMessageType.Broadcast));
        }

        /// <summary>
        /// One line per mod type present across equipped gear, in registry order, aggregated the same way the
        /// combat hooks read it (Creature.GetEquippedModPotencySum / GetEquippedModValue).
        /// </summary>
        private static void HandleSummary(Session session)
        {
            var player = session.Player;

            var equipped = player.EquippedObjects.Values
                .Select(item => (item, mods: EquipmentModDisplay.GetMods(item).Where(m => EquipmentModRoller.Clamp01(m.Potency) > 0.0).ToList()))
                .Where(entry => entry.mods.Count > 0)
                .ToList();

            if (equipped.Count == 0)
            {
                Reply(session, "You have no equipment mods on your equipped gear.");
                return;
            }

            var modTypeCount = EquipmentModRegistry.AllMods.Count(m => player.GetEquippedModPotencySum(m.Id) > 0.0);
            var itemCount = equipped.Count;

            Reply(session, $"Equipment mods: {modTypeCount} type{(modTypeCount == 1 ? "" : "s")} across {itemCount} item{(itemCount == 1 ? "" : "s")}.");

            foreach (var definition in EquipmentModRegistry.AllMods)
            {
                var potencySum = player.GetEquippedModPotencySum(definition.Id);

                if (potencySum <= 0.0)
                    continue;

                var appliedValue = player.GetEquippedModValue(definition.Id);
                var n = equipped.Count(entry => entry.mods.Any(m => m.Definition.Id == definition.Id));

                var uncappedSum = player.EquippedObjects.Values
                    .Sum(item => EquipmentModRoller.Clamp01(item.GetProperty(definition.Property) ?? 0.0));
                var capped = definition.StackCap > 0.0 && uncappedSum > definition.StackCap;

                // Abilities[] would THROW for a linked ability that is not registered, and an id reserved
                // ahead of its handler is exactly that (Break Armor, Phase 0 of the Berserker/Rogue balance
                // pass) - as is a retired one. Falling back to the mod's own display name keeps this listing
                // from crashing on gear that is already rolled and in players' hands.
                var inactiveAbilityName = !definition.Standalone && player.GetClassAbilityRank(definition.LinkedAbility) == 0
                    ? (ClassAbilityRegistry.Abilities.TryGetValue(definition.LinkedAbility, out var linked) ? linked.DisplayName : definition.DisplayName)
                    : null;

                Reply(session, FormatSummaryLine(definition, appliedValue, n, potencySum, capped, inactiveAbilityName));
            }
        }

        /// <summary>
        /// "- {DisplayName}: {formatted applied value} ({n} item(s), {pct}% of one perfect roll{capNote}){inactiveNote}"
        /// Factored out as a pure function so the format can be pinned by a test without a live world.
        /// </summary>
        public static string FormatSummaryLine(EquipmentModDefinition definition, double appliedValue, int itemCount, double potencySum, bool capped, string inactiveAbilityName)
        {
            var sumPct = (int)Math.Round(potencySum * 100.0, MidpointRounding.AwayFromZero);
            var capNote = capped ? ", capped" : "";
            var inactiveNote = inactiveAbilityName != null ? $" [inactive - requires {inactiveAbilityName}]" : "";

            return $"- {definition.DisplayName}: {definition.Format(appliedValue)} ({itemCount} item{(itemCount == 1 ? "" : "s")}, {sumPct}% of one perfect roll{capNote}){inactiveNote}";
        }

        /// <summary>
        /// One header line per item carrying mods, followed by each mod on that item via EquipmentModDisplay.Describe
        /// - the same rendering the appraisal panel uses.
        /// </summary>
        private static void HandleItems(Session session)
        {
            var player = session.Player;

            var equipped = player.EquippedObjects.Values
                .Select(item => (item, mods: EquipmentModDisplay.GetMods(item).Where(m => EquipmentModRoller.Clamp01(m.Potency) > 0.0).ToList()))
                .Where(entry => entry.mods.Count > 0)
                .ToList();

            if (equipped.Count == 0)
            {
                Reply(session, "You have no equipment mods on your equipped gear.");
                return;
            }

            foreach (var (item, mods) in equipped)
            {
                var capacity = item.GetProperty(PropertyInt.GearModCapacity) ?? 0;
                var capacityNote = capacity > 0 ? $"{mods.Count}/{capacity}" : $"{mods.Count}";
                var name = item.NameWithMaterial ?? item.Name;

                Reply(session, $"{name} ({capacityNote}):");

                foreach (var (definition, potency) in mods)
                    Reply(session, $"  - {EquipmentModDisplay.Describe(definition, potency)}");
            }
        }
    }
}
