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
        /// </summary>
        public static void HandleMuleRequest(Player player)
        {
            if (player == null)
                return;

            if (player.IsMule)
            {
                GrantMulePack(player);
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
