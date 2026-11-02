using System;
using System.Globalization;

using ACE.Entity.Enum;
using ACE.Server.Knots;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;

namespace ACE.Server.Command.Handlers
{
    /// <summary>Player commands for Knots (Docs/Knots/KNOTS-DESIGN.md).</summary>
    public static class KnotCommands
    {
        private const string Usage = "Usage: /knot lower <level>  (lowers the selected Knot; minimum Level 200)";

        [CommandHandler("knot", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 0,
            "Knot controls.",
            "lower <level>\n" +
            "  lower <level> - lower the selected Knot (or your only Knot) to a lower level, never below 200. Irreversible; asks first")]
        public static void HandleKnot(Session session, params string[] parameters)
        {
            var player = session.Player;

            if (player == null)
                return;

            if (parameters.Length != 2 || !parameters[0].Equals("lower", StringComparison.OrdinalIgnoreCase)
                || !int.TryParse(parameters[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var level))
            {
                session.Network.EnqueueSend(new GameMessageSystemChat(Usage, ChatMessageType.Broadcast));
                return;
            }

            KnotLowering.Begin(player, level);
        }
    }
}
