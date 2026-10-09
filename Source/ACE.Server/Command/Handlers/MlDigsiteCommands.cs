using System;

using ACE.Entity.Enum;
using ACE.Server.MlDigsite;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.Command.Handlers
{
    /// <summary>
    /// /digsite: a player-facing status check for a live digsite encounter (round 13 feedback item E, part
    /// 3), on the same command attribute/access pattern as <see cref="FellowshipCommands"/>
    /// (AccessLevel.Player, RequiresWorld), plus one subcommand:
    ///
    ///   /digsite        - the status line for the encounter the caller is standing in. Read-only.
    ///   /digsite bail   - OWNER ONLY: ends the caller's own encounter after a Yes/No
    ///                     (Confirmation_DigsiteBail) and pays the tier it reached. Anyone else is refused
    ///                     with a message naming the owner.
    /// </summary>
    public static class MlDigsiteCommands
    {
        [CommandHandler("digsite", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 0,
            "Shows the status of the digsite encounter you are currently at. /digsite bail ends one you dug and pays what it reached.",
            "[bail]")]
        public static void HandleDigsite(Session session, params string[] parameters)
        {
            var player = session?.Player;

            if (player == null)
                return;

            if (parameters != null && parameters.Length > 0 && string.Equals(parameters[0], "bail", StringComparison.OrdinalIgnoreCase))
            {
                HandleBail(player);
                return;
            }

            var encounter = MlDigsiteManager.FindParticipantEncounter(player);

            if (encounter == null)
            {
                Say(player, "You are not at a digsite encounter right now.");
                return;
            }

            Say(player, MlDigsiteManager.BuildStatusLine(encounter, DateTime.UtcNow));
        }

        /// <summary>
        /// /digsite bail. Refused (with MlDigsiteRules.BailRefusal's message) unless the caller owns a live
        /// encounter; otherwise sends the Yes/No. Nothing ends until the Yes, and the Yes re-validates.
        /// </summary>
        private static void HandleBail(Player player)
        {
            var owned = MlDigsiteManager.FindOwnedEncounter(player);

            var refusal = MlDigsiteRules.BailRefusal(owned != null,
                owned == null ? MlDigsiteManager.FindParticipantEncounter(player)?.DiggerName : null);

            if (refusal != null)
            {
                Say(player, refusal);
                return;
            }

            var confirmation = new Confirmation_DigsiteBail(player.Guid, owned.EncounterId, DateTime.UtcNow);
            var prompt = MlDigsiteRules.BailPrompt(MlDigsiteManager.CurrentTierPercent(owned));

            if (!player.ConfirmationManager.EnqueueSend(confirmation, prompt))
                Say(player, "Answer the question already on your screen first.");
        }

        private static void Say(Player player, string message)
            => player.Session?.Network.EnqueueSend(new GameMessageSystemChat(message, ChatMessageType.Broadcast));
    }
}
