using System;
using System.Threading.Tasks;

using log4net;

using ACE.Entity.Enum;
using ACE.Server.Managers.CharacterSheets;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.Command.Handlers
{
    public enum CharSheetCommandKind
    {
        Show,
        Off,
        Rotate,
        Usage
    }

    /// <summary>
    /// /charsheet parsing and reply text (Docs/CharacterSheet/DESIGN.md). Separated from the handler for
    /// no-Session testability, the same reason MarketCommands splits parse/dispatch from HandleMarket.
    /// </summary>
    internal static class CharacterSheetCommandText
    {
        internal const string UsageText = "Usage: /charsheet (show or create your public link), /charsheet rotate (new link), /charsheet off (hide it).";
        internal const string DisabledText = "Character sheets are not currently enabled on this server.";
        internal const string FailedText = "Your character sheet could not be updated right now, try again shortly.";
        internal const string OffText = "Your character sheet is now hidden. Its link no longer works.";

        internal static CharSheetCommandKind Parse(string[] parameters)
        {
            if (parameters == null || parameters.Length == 0)
                return CharSheetCommandKind.Show;

            if (parameters.Length == 1)
            {
                if (string.Equals(parameters[0], "off", StringComparison.OrdinalIgnoreCase))
                    return CharSheetCommandKind.Off;

                if (string.Equals(parameters[0], "rotate", StringComparison.OrdinalIgnoreCase))
                    return CharSheetCommandKind.Rotate;
            }

            return CharSheetCommandKind.Usage;
        }

        internal static string Reply(CharSheetCommandKind kind, LinkResult result, bool wasAlreadyOn)
        {
            if (kind == CharSheetCommandKind.Usage)
                return UsageText;

            if (result.Outcome == SheetOutcome.Disabled)
                return DisabledText;

            if (result.Outcome != SheetOutcome.Ok || result.Link == null)
                return FailedText;

            switch (kind)
            {
                case CharSheetCommandKind.Off:
                    return OffText;

                case CharSheetCommandKind.Rotate:
                    return $"Your character sheet has a new link: {result.Link.Url} - the old link no longer works.";

                case CharSheetCommandKind.Show:
                default:
                    return wasAlreadyOn
                        ? $"Your character sheet: {result.Link.Url}"
                        : $"Your character sheet is now public: {result.Link.Url} - anyone with this link can see your equipped gear, attributes, vitals, skills, class abilities and leaderboard ranks. Use /charsheet off to hide it, or /charsheet rotate for a new link.";
            }
        }
    }

    /// <summary>
    /// The in-game surface for a player's public character sheet link (Docs/CharacterSheet/DESIGN.md).
    /// THIN like MarketCommands: parses, applies the cooldown, then hands off to the shard-DB-backed
    /// ICharacterSheetService on a pool thread so the world thread never blocks on it.
    /// </summary>
    public static class CharacterSheetCommands
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        [CommandHandler("charsheet", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 0,
            "Publish a web character sheet for this character.",
            "[off | rotate]")]
        public static void HandleCharSheet(Session session, params string[] parameters)
        {
            var player = session?.Player;

            if (player == null)
                return;

            var kind = CharacterSheetCommandText.Parse(parameters);

            if (kind == CharSheetCommandKind.Usage)
            {
                session.Network.EnqueueSend(new GameMessageSystemChat(CharacterSheetCommandText.UsageText, ChatMessageType.System));
                return;
            }

            // Same 1s per-character cooldown /market applies to its mutating subcommands.
            if ((DateTime.UtcNow - player.LastBankCommandTime).TotalSeconds < 1.0)
            {
                session.Network.EnqueueSend(new GameMessageSystemChat("You are using charsheet commands too quickly.", ChatMessageType.System));
                return;
            }

            player.LastBankCommandTime = DateTime.UtcNow;

            var guid = player.Guid.Full;

            Task.Run(() =>
            {
                string reply;

                try
                {
                    reply = Execute(CharacterSheetService.Instance, guid, kind);
                }
                catch (Exception ex)
                {
                    log.Error($"[CHARSHEET] /charsheet failed for 0x{guid:X8}: {ex}");
                    reply = CharacterSheetCommandText.FailedText;
                }

                session.Network.EnqueueSend(new GameMessageSystemChat(reply, ChatMessageType.System));
            });
        }

        /// <summary>
        /// The decision logic, kept free of Session/Player so it can be tested against a fake service.
        /// </summary>
        internal static string Execute(ICharacterSheetService svc, uint guid, CharSheetCommandKind kind)
        {
            switch (kind)
            {
                case CharSheetCommandKind.Off:
                    return CharacterSheetCommandText.Reply(kind, svc.Disable(guid), false);

                case CharSheetCommandKind.Rotate:
                    return CharacterSheetCommandText.Reply(kind, svc.EnableOrRotate(guid, true), false);

                case CharSheetCommandKind.Show:
                default:
                    var current = svc.GetLink(guid);

                    if (current.Outcome != SheetOutcome.Ok)
                        return CharacterSheetCommandText.Reply(kind, current, false);

                    var wasAlreadyOn = current.Link != null && current.Link.Enabled;

                    if (wasAlreadyOn)
                        return CharacterSheetCommandText.Reply(kind, current, true);

                    return CharacterSheetCommandText.Reply(kind, svc.EnableOrRotate(guid, false), wasAlreadyOn);
            }
        }
    }
}
