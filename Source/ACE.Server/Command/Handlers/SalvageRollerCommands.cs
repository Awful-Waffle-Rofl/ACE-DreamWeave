using System;
using System.Linq;
using System.Text.RegularExpressions;

using ACE.Entity.Enum;
using ACE.Server.Managers.Market;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.SalvageRoller;
using ACE.Server.WorldObjects;

namespace ACE.Server.Command.Handlers
{
    public enum SalvageRollerCommandKind
    {
        Usage,
        Status,
        Search,
        ClearSearch,
    }

    public sealed class SalvageRollerCommandResult
    {
        public SalvageRollerCommandKind Kind { get; set; }

        /// <summary>The search text for <see cref="SalvageRollerCommandKind.Search"/>, joined from every token after "search".</summary>
        public string Pattern { get; set; }
    }

    /// <summary>What the command core needs from the player; the live implementation wraps a Player, a test supplies a fake.</summary>
    internal interface ISalvageRollerCommandTarget
    {
        string DescribeStatus();

        /// <summary>True when the player has a live view on a live station (only a station Use creates one).</summary>
        bool HasLiveView { get; }

        void Tell(string text);

        /// <summary>Sets (or clears, with null) the player's own view filter. Never another player's.</summary>
        void SetFilter(Regex filter);

        /// <summary>Rows the player's filter shows.</summary>
        int VisibleCount { get; }

        /// <summary>Rows the player's view holds, shown or not.</summary>
        int RowCount { get; }

        /// <summary>Re-sends the player's own vendor panel (the filtered view).</summary>
        void ReopenPanel();
    }

    /// <summary>
    /// Salvage Roller (Docs/SalvageRoller/DESIGN.md 5.1, 5.4 step 6): /salvageroller status and
    /// /salvageroller search [pattern]. Thin by the house pattern: parse, then a testable core over
    /// <see cref="ISalvageRollerCommandTarget"/>. Player-facing text here is provisional (the owner reviews it).
    /// </summary>
    public static class SalvageRollerCommands
    {
        internal const string HelpText =
            "status | search [pattern]\n" +
            "  status           - what your Salvage Roller batch is doing\n" +
            "  search <pattern> - show only the results whose name, spells or mods match the pattern\n" +
            "  search           - clear the filter and show every result";

        internal const string UsageMessage = "Usage: /salvageroller status | /salvageroller search [pattern]";

        internal const string NoWindowMessage = "You have no Salvage Roller results open. Use the Salvage Roller first.";

        internal const string SearchClearedMessage = "Salvage Roller filter cleared.";

        [CommandHandler("salvageroller", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 0,
            "Check your Salvage Roller batch, or filter its results.",
            HelpText)]
        public static void HandleSalvageRoller(Session session, params string[] parameters)
        {
            var player = session.Player;

            if (player == null)
                return;

            Dispatch(Parse(parameters), new PlayerTarget(player));
        }

        /// <summary>
        /// Pure recognition of a leading "/salvageroller", the same rules as MuleCommands.IsMuleSlashCommand: acclient
        /// sends a command it does not ship with as ordinary local chat, so GameActionTalk rewrites this one onto the
        /// "@" path before the chat fallthrough. A bare "/salvageroller" matches; with arguments it matches only when
        /// the next character is whitespace, so "/salvagerollerX" is left alone as ordinary chat.
        /// </summary>
        internal static bool IsSalvageRollerSlashCommand(string message)
        {
            const string word = "/salvageroller";

            if (string.IsNullOrEmpty(message) || !message.StartsWith(word, StringComparison.OrdinalIgnoreCase))
                return false;

            return message.Length == word.Length || char.IsWhiteSpace(message[word.Length]);
        }

        /// <summary>Pure. No arguments or an unknown word is a usage line; "search" with nothing after it clears the filter.</summary>
        public static SalvageRollerCommandResult Parse(string[] parameters)
        {
            if (parameters == null || parameters.Length == 0)
                return new SalvageRollerCommandResult { Kind = SalvageRollerCommandKind.Usage };

            var verb = parameters[0].Trim();

            if (string.Equals(verb, "status", StringComparison.OrdinalIgnoreCase))
                return new SalvageRollerCommandResult { Kind = SalvageRollerCommandKind.Status };

            if (string.Equals(verb, "search", StringComparison.OrdinalIgnoreCase))
            {
                var pattern = string.Join(" ", parameters.Skip(1)).Trim();

                return pattern.Length == 0
                    ? new SalvageRollerCommandResult { Kind = SalvageRollerCommandKind.ClearSearch }
                    : new SalvageRollerCommandResult { Kind = SalvageRollerCommandKind.Search, Pattern = pattern };
            }

            return new SalvageRollerCommandResult { Kind = SalvageRollerCommandKind.Usage };
        }

        internal static void Dispatch(SalvageRollerCommandResult result, ISalvageRollerCommandTarget target)
        {
            switch (result.Kind)
            {
                case SalvageRollerCommandKind.Status:
                    target.Tell(target.DescribeStatus());
                    return;

                case SalvageRollerCommandKind.Search:
                case SalvageRollerCommandKind.ClearSearch:
                    break;

                default:
                    target.Tell(UsageMessage);
                    return;
            }

            if (!target.HasLiveView)
            {
                target.Tell(NoWindowMessage);
                return;
            }

            if (result.Kind == SalvageRollerCommandKind.ClearSearch)
            {
                target.SetFilter(null);
                target.ReopenPanel();
                target.Tell(SearchClearedMessage);
                return;
            }

            if (!ItemTextSearch.TryCompile(result.Pattern, out Regex regex, out var compileError))
            {
                target.Tell(compileError);
                return;
            }

            target.SetFilter(regex);
            target.ReopenPanel();
            target.Tell($"Salvage Roller filter \"{result.Pattern}\": {target.VisibleCount} of {target.RowCount} results shown.");
        }

        private sealed class PlayerTarget : ISalvageRollerCommandTarget
        {
            private readonly Player player;

            public PlayerTarget(Player player) => this.player = player;

            public string DescribeStatus() => SalvageRollerManager.DescribeStatus(player);

            public bool HasLiveView => player.GetLiveSalvageRollerStation() != null;

            public void SetFilter(Regex filter) => player.GetLiveSalvageRollerStation()?.SetSearchFilter(player.Guid.Full, filter);

            public int VisibleCount => player.GetLiveSalvageRollerStation()?.VisibleCount(player.Guid.Full) ?? 0;

            public int RowCount => player.GetLiveSalvageRollerStation()?.RowCount(player.Guid.Full) ?? 0;

            public void Tell(string text)
            {
                if (player.Session == null || string.IsNullOrEmpty(text))
                    return;

                player.Session.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.System));
            }

            public void ReopenPanel() => player.GetLiveSalvageRollerStation()?.ApproachVendor(player);
        }
    }
}
