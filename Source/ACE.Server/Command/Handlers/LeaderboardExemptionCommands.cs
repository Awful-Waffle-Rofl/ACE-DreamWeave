using System.Linq;
using System.Text;

using ACE.Entity.Enum;
using ACE.Database;
using ACE.Server.Managers;
using ACE.Server.Network;

namespace ACE.Server.Command.Handlers
{
    /// <summary>
    /// Admin tooling for the /top leaderboard exemption list (LeaderboardExemptionManager). Accounts at
    /// AccessLevel Sentinel or above, and characters carrying the staff PropertyBools, are exempt inherently and
    /// never need to appear here - this command manages the additional list of staff alternate accounts that run
    /// at normal Player access.
    /// </summary>
    public static class LeaderboardExemptionCommands
    {
        private const string Usage = "Usage: top-exempt list | add <account> | remove <account>";

        [CommandHandler("top-exempt", AccessLevel.Admin, CommandHandlerFlag.None, 0,
            "Manage the /top leaderboard exemption list.", "list | add <account> | remove <account>")]
        public static void HandleTopExempt(Session session, params string[] parameters)
        {
            var sub = parameters.Length > 0 ? parameters[0].ToLowerInvariant() : "list";

            switch (sub)
            {
                case "list":
                    HandleList(session);
                    return;

                case "add":
                    if (parameters.Length < 2)
                    {
                        CommandHandlerHelper.WriteOutputInfo(session, Usage, ChatMessageType.Broadcast);
                        return;
                    }

                    HandleAdd(session, parameters[1]);
                    return;

                case "remove":
                    if (parameters.Length < 2)
                    {
                        CommandHandlerHelper.WriteOutputInfo(session, Usage, ChatMessageType.Broadcast);
                        return;
                    }

                    HandleRemove(session, parameters[1]);
                    return;

                default:
                    CommandHandlerHelper.WriteOutputInfo(session, Usage, ChatMessageType.Broadcast);
                    return;
            }
        }

        private static void HandleList(Session session)
        {
            var names = LeaderboardExemptionManager.GetExemptAccountNames().OrderBy(n => n, System.StringComparer.OrdinalIgnoreCase).ToList();

            var output = new StringBuilder();
            output.AppendLine("=== Leaderboard Exemption List ===");
            output.AppendLine($"Accounts at {LeaderboardExemptionManager.InherentExemptAccessLevel} access or above are exempt automatically and are not listed here.");

            if (names.Count == 0)
            {
                output.AppendLine("(none)");
            }
            else
            {
                for (var i = 0; i < names.Count; i++)
                    output.AppendLine($"{i + 1}. {names[i]}");
            }

            CommandHandlerHelper.WriteOutputInfo(session, output.ToString(), ChatMessageType.Broadcast);
        }

        private static void HandleAdd(Session session, string accountName)
        {
            var account = DatabaseManager.Authentication.GetAccountByName(accountName);
            if (account == null)
            {
                CommandHandlerHelper.WriteOutputInfo(session,
                    $"No account named '{accountName}' exists. (This list takes ACCOUNT names, not character names.)",
                    ChatMessageType.Broadcast);
                return;
            }

            if (!LeaderboardExemptionManager.TryAddAccount(account.AccountName, out var message))
            {
                CommandHandlerHelper.WriteOutputInfo(session, message, ChatMessageType.Broadcast);
                return;
            }

            CommandHandlerHelper.WriteOutputInfo(session, message, ChatMessageType.Broadcast);

            var invoker = session?.Player?.Name ?? "CONSOLE";
            PlayerManager.BroadcastToAuditChannel(session?.Player, $"{invoker} added account {account.AccountName} to the /top leaderboard exemption list");
        }

        private static void HandleRemove(Session session, string accountName)
        {
            // Deliberately does not require the account to still exist - a deleted account must still be
            // removable from this list.
            if (!LeaderboardExemptionManager.TryRemoveAccount(accountName, out var message))
            {
                CommandHandlerHelper.WriteOutputInfo(session, message, ChatMessageType.Broadcast);
                return;
            }

            CommandHandlerHelper.WriteOutputInfo(session, message, ChatMessageType.Broadcast);

            var invoker = session?.Player?.Name ?? "CONSOLE";
            PlayerManager.BroadcastToAuditChannel(session?.Player, $"{invoker} removed account {accountName} from the /top leaderboard exemption list");
        }
    }
}
