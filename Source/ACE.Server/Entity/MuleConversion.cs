using log4net;

using ACE.Entity.Enum;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.Entity
{
    /// <summary>
    /// Mule system (WaffleACE) entry point. Reached from EmoteType.MuleConversion, fired by the conversion
    /// NPC at the end of its confirmation chain (wcid 1001940). The NPC's Use emote branches on IsMule
    /// FIRST (InqBoolStat on PropertyBool.IsMule) so an already-converted character reaches this same
    /// entry point directly, without re-running the two confirmations.
    /// </summary>
    public static class MuleConversion
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// Converts a character to a mule, if it is still eligible, and hands over a Bearer's Pack either way
        /// (once for a fresh conversion, or every time an existing mule interacts again - there is no limit on
        /// repeat pack grants). Eligibility is re-checked HERE rather than trusted from the NPC dialogue:
        /// reaching the emote proves only that the player clicked through the confirmations (or already carries
        /// IsMule), and state can change (or the NPC can be reached by another route) in between.
        /// Both paths also hand over an Aetheria Mana Stone when the character does not already carry one -
        /// a mule cannot quest for it, and revealing aetheria is routine mule work.
        /// </summary>
        public static void HandleMuleRequest(Player player)
        {
            if (player == null)
                return;

            if (player.IsMule)
            {
                GrantMulePack(player);
                GrantAetheriaManaStoneIfMissing(player);
                return;
            }

            if (!Player.IsEligibleForMuleConversion(player, out var reason))
            {
                player.SendMessage(reason, ChatMessageType.Broadcast);
                return;
            }

            player.ApplyMuleConversion();

            // Irreversible and otherwise unauditable - there is no un-mule path, so this log line is the only
            // record that a given character was converted, and by which account.
            log.Info($"[MULE] {player.Name} (0x{player.Guid}) converted to a mule by account '{player.Account?.AccountName ?? "unknown"}' ({player.Account?.AccountId.ToString() ?? "?"}).");

            player.SendMessage("You are now a mule. You can carry, trade and store, but you will never again earn experience, fight, or advance. This cannot be undone.", ChatMessageType.Broadcast);

            GrantMulePack(player);
            GrantAetheriaManaStoneIfMissing(player);
        }

        /// <summary>
        /// Hands over one Aetheria Mana Stone (retail wcid 42645) unless the character already carries one
        /// anywhere in its inventory, side packs included. Using the stone on aetheria does not consume it
        /// (Aetheria.ActivateSigil never touches the source), so one is all a mule ever needs. Same
        /// create / try-add / destroy-on-failure pattern as GrantMulePack.
        /// </summary>
        private static void GrantAetheriaManaStoneIfMissing(Player player)
        {
            if (player.GetInventoryItemsOfWCID(Aetheria.AetheriaManaStone).Count > 0)
                return;

            var stone = WorldObjectFactory.CreateNewWorldObject(Aetheria.AetheriaManaStone);

            if (stone == null)
            {
                log.Warn($"[MULE] {player.Name} (0x{player.Guid}) - failed to create Aetheria Mana Stone: weenie {Aetheria.AetheriaManaStone} couldn't be found.");
                return;
            }

            if (!player.TryCreateInInventoryWithNetworking(stone))
            {
                stone.Destroy();
                player.SendMessage("You have no room for an Aetheria Mana Stone. Come back when you can carry it.", ChatMessageType.Broadcast);
                return;
            }

            log.Info($"[MULE] {player.Name} (0x{player.Guid}) granted an Aetheria Mana Stone (wcid {Aetheria.AetheriaManaStone}).");
            player.SendMessage("You receive an Aetheria Mana Stone.", ChatMessageType.Broadcast);
        }

        /// <summary>
        /// Creates and hands over one Bearer's Pack (wcid from the 'mule_pack_wcid' config, default 1001941).
        /// If the player's pack is full, this says so and does NOT silently destroy the pack by never creating
        /// it - it creates the item, tries the add, and only destroys the orphaned instance on a failed add
        /// (same pattern as StageTestCommands' self-grant commands).
        /// </summary>
        private static void GrantMulePack(Player player)
        {
            var wcid = (uint)PropertyManager.GetLong("mule_pack_wcid").Item;

            var pack = WorldObjectFactory.CreateNewWorldObject(wcid);

            if (pack == null)
            {
                log.Warn($"[MULE] {player.Name} (0x{player.Guid}) - failed to create Bearer's Pack: weenie {wcid} ('mule_pack_wcid') couldn't be found.");
                return;
            }

            if (!player.TryCreateInInventoryWithNetworking(pack))
            {
                pack.Destroy();
                player.SendMessage("Your hands are full. Come back when you can carry it.", ChatMessageType.Broadcast);
                return;
            }

            log.Info($"[MULE] {player.Name} (0x{player.Guid}) granted a Bearer's Pack (wcid {wcid}).");
            player.SendMessage("A Bearer's Pack settles onto your back.", ChatMessageType.Broadcast);
        }
    }
}
