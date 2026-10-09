using ACE.Entity.Enum;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.Managers
{
    /// <summary>
    /// The world-broadcast send shared by the in-game /gamecast command and the web admin panel's
    /// POST /v1/admin/announce (PLAN-P3.md section 2). No static fields, so there is no
    /// static-initializer trap.
    /// </summary>
    public static class Gamecast
    {
        public static string Format(string senderName, string text) => $"Broadcast from {senderName}> {text}";

        /// <summary>
        /// Sends <paramref name="text"/> to every online session as a WorldBroadcast system chat line,
        /// then writes it to the broadcast chat log. Returns the number of sessions it was enqueued
        /// to. <paramref name="sender"/> null (e.g. a web-originated announcement, or the console) is
        /// shown as "System".
        /// </summary>
        public static int Broadcast(Player sender, string text)
        {
            var msg = Format(sender != null ? sender.Name : "System", text);
            var gameMessage = new GameMessageSystemChat(msg, ChatMessageType.WorldBroadcast);

            var recipients = 0;

            foreach (var player in PlayerManager.GetAllOnline())
            {
                player.Session.Network.EnqueueSend(gameMessage);
                recipients++;
            }

            PlayerManager.LogBroadcastChat(Channel.AllBroadcast, sender, msg);

            return recipients;
        }
    }
}
