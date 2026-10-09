using ACE.Entity.Enum;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Realms;

namespace ACE.Server.Command.Handlers
{
    /// <summary>
    /// ACRealms port Phase 2: realm registry inspection and home-realm administration.
    /// Instance-routing commands (telerealm, enter-instance, exitinstance) live with
    /// the routing layer.
    /// </summary>
    public static class RealmCommands
    {
        [CommandHandler("realm", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 0, "Shows which realm and instance you are currently in.")]
        public static void HandleRealm(Session session, params string[] parameters)
        {
            if (session?.Player == null)
                return;

            session.Network.EnqueueSend(new GameMessageSystemChat(RealmLine.ForPosition(session.Player.Location), ChatMessageType.Broadcast));
        }

        [CommandHandler("realms", AccessLevel.Developer, CommandHandlerFlag.None, 0, "Lists all realms in the registry.")]
        public static void HandleRealms(Session session, params string[] parameters)
        {
            foreach (var realm in RealmManager.GetAllRealms())
                CommandHandlerHelper.WriteOutputInfo(session, realm.ToString());
        }

        [CommandHandler("get-home-realm", AccessLevel.Admin, CommandHandlerFlag.None, 1, "Shows a player's home realm.", "<player name>")]
        public static void HandleGetHomeRealm(Session session, params string[] parameters)
        {
            var playerName = string.Join(" ", parameters);
            var player = PlayerManager.GetOnlinePlayer(playerName);
            if (player == null)
            {
                CommandHandlerHelper.WriteOutputInfo(session, $"Couldn't find online player: {playerName}");
                return;
            }

            var realm = RealmManager.GetRealm(player.HomeRealm);
            CommandHandlerHelper.WriteOutputInfo(session, $"{player.Name}'s home realm: {realm?.ToString() ?? $"{player.HomeRealm} (not in registry!)"}");
        }

        [CommandHandler("set-home-realm", AccessLevel.Admin, CommandHandlerFlag.None, 2, "Sets a player's home realm.", "<realm id> <player name>")]
        public static void HandleSetHomeRealm(Session session, params string[] parameters)
        {
            if (!ushort.TryParse(parameters[0], out var realmId))
            {
                CommandHandlerHelper.WriteOutputInfo(session, $"Invalid realm id: {parameters[0]}");
                return;
            }

            var realm = RealmManager.GetRealm(realmId);
            if (realm == null)
            {
                CommandHandlerHelper.WriteOutputInfo(session, $"Realm {realmId} is not in the registry.");
                return;
            }

            var playerName = string.Join(" ", parameters, 1, parameters.Length - 1);
            var player = PlayerManager.GetOnlinePlayer(playerName);
            if (player == null)
            {
                CommandHandlerHelper.WriteOutputInfo(session, $"Couldn't find online player: {playerName}");
                return;
            }

            player.HomeRealm = realmId;
            CommandHandlerHelper.WriteOutputInfo(session, $"{player.Name}'s home realm set to {realm}.");
        }
    }
}
