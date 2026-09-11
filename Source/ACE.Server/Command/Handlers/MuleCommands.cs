using System;

using ACE.Entity.Enum;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.Command.Handlers
{
    /// <summary>
    /// Mule Vendor (Docs/MuleVendor/DESIGN.md section 13, and the audit half of section 10): the
    /// player-facing /mule command family - summon, grant, revoke, access, log.
    ///
    /// House pattern (DESIGN 11.1, /dn's template at PlayerCommands.cs:1244-1256): this handler is
    /// thin. It only parses and validates parameters, then delegates every piece of actual work to a
    /// HandleActionX method on Player. Nothing here touches AccountVaultStore, AccountVaultManager or
    /// MuleSummonHandler directly.
    /// </summary>
    public static class MuleCommands
    {
        [CommandHandler("mule", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 0,
            "Summon your account vault's vendor, or manage who can use it.",
            MuleCommandParser.HelpText)]
        public static void HandleMule(Session session, params string[] parameters)
        {
            var player = session.Player;

            if (player == null)
                return;

            Dispatch(MuleCommandParser.Parse(parameters), new PlayerMuleCommandTarget(player));
        }

        /// <summary>
        /// Fix round 1, F1: the dispatch switch itself, pulled out of <see cref="HandleMule"/> and
        /// driven through <see cref="IMuleCommandTarget"/> so it is reachable from a test with no live
        /// Session/Player - see MuleCommandDispatchTests.cs. Before this fix nothing in the test suite
        /// executed this switch at all: transposing the Grant and Revoke arms made "/mule grant Bob
        /// withdraw" REVOKE and "/mule revoke Bob" GRANT WITHDRAW ACCESS while the full suite stayed
        /// green (confirmed locally - see the fix's report for the before line).
        /// </summary>
        /// <summary>
        /// Fix round B, B1: pure recognition of a leading "/mule" slash command, extracted so
        /// GameActionTalk.Handle can intercept it BEFORE its "@" command branch with no live Session -
        /// see GameActionTalk.cs's own remarks for why this one command earns that intercept.
        ///
        /// A bare "/mule" (no arguments) matches. "/mule ..." with anything after the word matches only
        /// when the next character is whitespace, so "/mulesomething" is a different word entirely and
        /// is left alone to fall through as ordinary chat like any other unrecognised text.
        /// </summary>
        internal static bool IsMuleSlashCommand(string message)
        {
            if (string.IsNullOrEmpty(message) || !message.StartsWith("/mule", StringComparison.OrdinalIgnoreCase))
                return false;

            return message.Length == 5 || char.IsWhiteSpace(message[5]);
        }

        internal static void Dispatch(MuleCommandResult result, IMuleCommandTarget target)
        {
            switch (result.Kind)
            {
                case MuleCommandKind.SummonOwn:
                    target.SummonMule("");
                    break;

                case MuleCommandKind.SummonOther:
                    target.SummonMule(result.Name);
                    break;

                case MuleCommandKind.Grant:
                    target.MuleGrant(result.Name, result.CanWithdraw);
                    break;

                case MuleCommandKind.Revoke:
                    target.MuleRevoke(result.Name);
                    break;

                case MuleCommandKind.Access:
                    target.MuleAccess();
                    break;

                case MuleCommandKind.Log:
                    target.MuleLog();
                    break;

                case MuleCommandKind.Search:
                    target.MuleSearch(result.Pattern);
                    break;

                case MuleCommandKind.Help:
                    target.MuleHelp();
                    break;

                case MuleCommandKind.UsageError:
                default:
                    target.UsageError(result.UsageError ?? MuleCommandParser.UsageMessage);
                    break;
            }
        }
    }

    /// <summary>
    /// Fix round 1, F1: the seam <see cref="MuleCommands.Dispatch"/> is driven through, so the dispatch
    /// switch is testable without a live Session/Player. One method per <see cref="MuleCommandKind"/>,
    /// deliberately - a recording test double can then assert exactly one distinct call per kind.
    /// </summary>
    internal interface IMuleCommandTarget
    {
        void SummonMule(string playerName);
        void MuleGrant(string name, bool canWithdraw);
        void MuleRevoke(string name);
        void MuleAccess();
        void MuleLog();
        void MuleSearch(string pattern);
        void MuleHelp();
        void UsageError(string message);
    }

    /// <summary>
    /// Production <see cref="IMuleCommandTarget"/>: a thin adapter over a live Player's HandleActionX
    /// methods. Fix round 1, F9: usage errors go through GameMessageSystemChat, the same persistent
    /// chat-log channel every success path already uses - SendTransientError's floating text does not
    /// persist, and a usage string is the one message a player needs to be able to read twice.
    /// </summary>
    internal sealed class PlayerMuleCommandTarget : IMuleCommandTarget
    {
        private readonly Player player;

        public PlayerMuleCommandTarget(Player player)
        {
            this.player = player;
        }

        public void SummonMule(string playerName) => player.HandleActionSummonMule(playerName);

        public void MuleGrant(string name, bool canWithdraw) => player.HandleActionMuleGrant(name, canWithdraw);

        public void MuleRevoke(string name) => player.HandleActionMuleRevoke(name);

        public void MuleAccess() => player.HandleActionMuleAccess();

        public void MuleLog() => player.HandleActionMuleLog();

        public void MuleSearch(string pattern) => player.HandleActionMuleSearch(pattern);

        /// <summary>
        /// Deliberately NOT routed through a HandleActionX on Player the way every other arm is: help
        /// touches no vault state, needs no store, and has no cooldown to respect, so giving it a
        /// Player method would only add an indirection with nothing in it.
        /// </summary>
        public void MuleHelp()
        {
            foreach (var line in MuleCommandParser.HelpLines)
                player.Session.Network.EnqueueSend(new GameMessageSystemChat(line, ChatMessageType.System));
        }

        public void UsageError(string message) =>
            player.Session.Network.EnqueueSend(new GameMessageSystemChat(message, ChatMessageType.System));
    }

    /// <summary>What /mule's parameters resolved to. See <see cref="MuleCommandParser"/>.</summary>
    public enum MuleCommandKind
    {
        SummonOwn,
        SummonOther,
        Grant,
        Revoke,
        Access,
        Log,
        Search,
        Help,
        UsageError
    }

    /// <summary>
    /// The outcome of parsing /mule's parameters. Immutable, and built only through the named factory
    /// methods below so an invalid combination (e.g. Grant with a null Name) cannot be constructed.
    /// </summary>
    public sealed class MuleCommandResult
    {
        public MuleCommandKind Kind { get; }

        /// <summary>The target character name, for SummonOther, Grant and Revoke. Null otherwise.</summary>
        public string Name { get; }

        /// <summary>Only meaningful for Grant.</summary>
        public bool CanWithdraw { get; }

        /// <summary>Only meaningful for UsageError.</summary>
        public string UsageError { get; }

        /// <summary>
        /// Only meaningful for Search - the free-text pattern to filter on. May be an empty string,
        /// meaning "clear the filter", but is never null for a Search result.
        /// </summary>
        public string Pattern { get; }

        private MuleCommandResult(MuleCommandKind kind, string name, bool canWithdraw, string usageError, string pattern)
        {
            Kind = kind;
            Name = name;
            CanWithdraw = canWithdraw;
            UsageError = usageError;
            Pattern = pattern;
        }

        public static MuleCommandResult SummonOwn() => new MuleCommandResult(MuleCommandKind.SummonOwn, null, false, null, null);
        public static MuleCommandResult SummonOther(string name) => new MuleCommandResult(MuleCommandKind.SummonOther, name, false, null, null);
        public static MuleCommandResult Grant(string name, bool canWithdraw) => new MuleCommandResult(MuleCommandKind.Grant, name, canWithdraw, null, null);
        public static MuleCommandResult Revoke(string name) => new MuleCommandResult(MuleCommandKind.Revoke, name, false, null, null);
        public static MuleCommandResult Access() => new MuleCommandResult(MuleCommandKind.Access, null, false, null, null);
        public static MuleCommandResult Help() => new MuleCommandResult(MuleCommandKind.Help, null, false, null, null);
        public static MuleCommandResult Log() => new MuleCommandResult(MuleCommandKind.Log, null, false, null, null);
        public static MuleCommandResult Search(string pattern) => new MuleCommandResult(MuleCommandKind.Search, null, false, null, pattern ?? string.Empty);
        public static MuleCommandResult Error(string message) => new MuleCommandResult(MuleCommandKind.UsageError, null, false, message, null);
    }

    /// <summary>
    /// Pure parse of /mule's parameters (DESIGN section 13), extracted from the command handler so it
    /// is testable with no live Session/Player - see MuleCommandParseTests.cs.
    ///
    /// The parse is ambiguous by construction: "grant", "revoke", "access" and "log" are reserved
    /// words that could also be character names. RESERVED WORDS ARE RESOLVED FIRST, always, so a
    /// player literally named one of them cannot be summoned by /mule &lt;name&gt; - that is an
    /// accepted cost of the spec's chosen surface (DESIGN 13), not a bug. /mule access still shows
    /// that owner's grant, so they are not left wondering whether the sharing silently failed.
    ///
    /// A PLAYER NAME IS EXACTLY ONE ARGUMENT. A name with no space is written bare; a name with a
    /// space must be wrapped in double quotes, admin "+" prefix included:
    ///
    ///     /mule grant Bob withdraw
    ///     /mule grant "+Testing thing" withdraw
    ///
    /// This costs nothing to support, because CommandManager.ParseCommand already reassembles a quoted
    /// multi-token argument into one parameter before a handler ever sees it, on the console path and
    /// on the in-game path alike (GameActionTalk.Handle:57 calls the same method). So by the time Parse
    /// runs, a correctly quoted name has arrived as a single element and needs no reassembly here.
    ///
    /// Earlier rounds instead string.Join'd every trailing token, matching what AdminCommands.cs does
    /// for @gag, @teleto, @teletome and @telereturn. That accepted an unquoted "grant Bob Smith", and
    /// the repo owner's ruling on 2026-08-28 is that it should not: requiring the quotes is what makes
    /// the trailing "withdraw" token UNAMBIGUOUS. Under the join, a character named "Bob Withdraw"
    /// could not be granted deposit-only at all, because the parser had no way to tell that last token
    /// from the flag. Quoted, it can - "Bob Withdraw" is one argument and any bare trailing "withdraw"
    /// is always the flag.
    ///
    /// So extra unquoted tokens are now a usage error rather than a silent join, and the error names
    /// the fix rather than reprinting the usage line: a player who typed a two-word name unquoted has
    /// made exactly one mistake and needs exactly one correction.
    /// </summary>
    public static class MuleCommandParser
    {
        public const string UsageMessage =
            "Usage: /mule [playername] | grant <name> [withdraw] | revoke <name> | access | log | search [pattern] | help";

        /// <summary>
        /// The one copy of /mule's help. Used BOTH as the CommandHandler attribute's usage string and
        /// as what "/mule help" prints in game, so the two cannot drift - the attribute needs a compile
        /// time constant, which is why this is a const and the per-line split below is derived from it
        /// rather than maintained beside it.
        /// </summary>
        public const string HelpText =
            "/mule                          - summon your own vault's vendor\n"
            + "/mule <playername>             - summon a vault you have been granted access to\n"
            + "/mule grant <name> [withdraw]  - share deposit-only access, or add withdraw, with <name>\n"
            + "/mule revoke <name>            - remove <name>'s access to your vault\n"
            + "/mule access                   - list who can access your vault\n"
            + "/mule log                      - recent activity on your vault\n"
            + "/mule search <pattern>         - show only vault entries whose name or spells match <pattern> (regex, case-insensitive, no double quotes); bare /mule search clears it\n"
            + "/mule help                     - show this list\n"
            + "A name with a space must be quoted: /mule grant \"+Testing thing\" withdraw";

        /// <summary>
        /// <see cref="HelpText"/> as one chat line each. Sent line by line rather than as a single
        /// embedded-newline message, because a system chat line is the unit the client wraps and logs -
        /// one long message with newlines in it is not reliably rendered as the aligned block this text
        /// is written to be.
        /// </summary>
        public static readonly string[] HelpLines = HelpText.Split('\n');

        /// <summary>
        /// Appended to a successful summon so the other subcommands are discoverable at all. Summoning
        /// is the ONLY thing most players will ever do with /mule, and nothing else in the feature
        /// mentions that sharing, the access list or the audit log exist. Emitted on the shared summon
        /// path in MuleSummonHandler.TrySummon rather than at the command call site, for the same
        /// reason the vault-fullness line is: the contract item never touches this class.
        /// </summary>
        public const string HelpHint = "Type /mule help for sharing, access and log options.";

        private const string GrantUsage = "Usage: /mule grant <name> [withdraw]";
        private const string RevokeUsage = "Usage: /mule revoke <name>";

        /// <summary>
        /// Shown instead of a usage line when the only thing wrong is an unquoted name with a space in
        /// it, which is by far the likeliest way to get this wrong. Naming the fix beats reprinting the
        /// grammar: the player already knows the grammar, they just did not know the name was one
        /// argument. The example carries the "+" so an admin character reads as covered.
        /// </summary>
        public const string QuoteNameMessage =
            "A player name is one argument. Wrap a name containing a space in quotes, e.g. /mule grant \"+Testing thing\" withdraw";
        private const string AccessUsage = "Usage: /mule access";
        private const string LogUsage = "Usage: /mule log";
        private const string HelpUsage = "Usage: /mule help";

        /// <summary>
        /// The inverse of this parser's own rule, for building a /mule command that is shown TO a
        /// player to type back. A name with no space is returned bare; a name with any whitespace comes
        /// back wrapped in double quotes. The admin "+" prefix needs no quoting on its own - it is not
        /// whitespace and CommandManager.ParseCommand passes it through untouched - so "+Awfulwaffle"
        /// stays bare and only "+Testing thing" is wrapped.
        ///
        /// This exists because the grant notification tells the grantee the exact command to type
        /// (Player_Mule_Vendor.HandleActionMuleGrant). Interpolating a spaced owner name straight into
        /// it produced an instruction that could not work, and the one player guaranteed to see it is
        /// the one who has no other way to find out the command.
        /// </summary>
        public static string QuoteNameIfNeeded(string name)
        {
            if (string.IsNullOrEmpty(name))
                return name;

            foreach (var c in name)
            {
                if (char.IsWhiteSpace(c))
                    return "\"" + name + "\"";
            }

            return name;
        }

        public static MuleCommandResult Parse(string[] parameters)
        {
            // Defence, not a production path: CommandManager.GetCommandHandler returns InvalidCommand
            // for a null parameters array and GameActionTalk only ever invokes a handler on Ok, so the
            // live /mule path can never actually deliver null here. Parse is public, though, and must
            // not throw for a caller that does.
            if (parameters == null || parameters.Length == 0)
                return MuleCommandResult.SummonOwn();

            var head = parameters[0];

            if (string.Equals(head, "grant", StringComparison.OrdinalIgnoreCase))
                return ParseGrant(parameters);

            if (string.Equals(head, "revoke", StringComparison.OrdinalIgnoreCase))
                return ParseRevoke(parameters);

            if (string.Equals(head, "access", StringComparison.OrdinalIgnoreCase))
                return parameters.Length == 1 ? MuleCommandResult.Access() : MuleCommandResult.Error(AccessUsage);

            if (string.Equals(head, "log", StringComparison.OrdinalIgnoreCase))
                return parameters.Length == 1 ? MuleCommandResult.Log() : MuleCommandResult.Error(LogUsage);

            // "search" joins the reserved words too, but unlike them its argument is free text, not a
            // quoted single name: everything after "search" is rejoined with single spaces (the client
            // splits chat on spaces, so a multi-word pattern arrives as several parameters - see
            // /market search's identical handling in MarketCommands.cs). A bare "/mule search" is valid
            // and means "clear the filter", so it is not a usage error the way a bare "grant" is.
            if (string.Equals(head, "search", StringComparison.OrdinalIgnoreCase))
                return MuleCommandResult.Search(parameters.Length > 1 ? string.Join(" ", parameters, 1, parameters.Length - 1) : string.Empty);

            // "help" joins grant/revoke/access/log as a reserved word, with the same accepted cost this
            // class's remarks already record: a character literally named Help cannot be summoned by
            // "/mule Help". Unlike the others it takes no argument at all, so anything after it is a
            // usage error rather than a name.
            if (string.Equals(head, "help", StringComparison.OrdinalIgnoreCase))
                return parameters.Length == 1 ? MuleCommandResult.Help() : MuleCommandResult.Error(HelpUsage);

            // Not a reserved word, so it is a player name - and a name is exactly one argument (see
            // this class's remarks). A correctly quoted "Bob Smith" has already been reassembled into
            // this single element by CommandManager.ParseCommand; anything left over is an unquoted
            // multi-word name.
            if (parameters.Length > 1)
                return MuleCommandResult.Error(QuoteNameMessage);

            return string.IsNullOrWhiteSpace(head) ? MuleCommandResult.Error(UsageMessage) : MuleCommandResult.SummonOther(head);
        }

        private static MuleCommandResult ParseGrant(string[] parameters)
        {
            // Shape is exactly [grant, name] or [grant, name, withdraw]. The trailing flag is consumed
            // first, and only when there is a name in front of it - "grant withdraw" on its own names
            // a character called Withdraw, and granting THEM deposit-only has to stay possible.
            var end = parameters.Length;

            var canWithdraw = end > 2 && string.Equals(parameters[end - 1], "withdraw", StringComparison.OrdinalIgnoreCase);

            if (canWithdraw)
                end--;

            if (end < 2)
                return MuleCommandResult.Error(GrantUsage);

            // Exactly one argument may remain, and it is the name. More than one means the name was
            // written with spaces and no quotes.
            if (end > 2)
                return MuleCommandResult.Error(QuoteNameMessage);

            var name = parameters[1];

            return string.IsNullOrWhiteSpace(name) ? MuleCommandResult.Error(GrantUsage) : MuleCommandResult.Grant(name, canWithdraw);
        }

        private static MuleCommandResult ParseRevoke(string[] parameters)
        {
            if (parameters.Length < 2)
                return MuleCommandResult.Error(RevokeUsage);

            if (parameters.Length > 2)
                return MuleCommandResult.Error(QuoteNameMessage);

            var name = parameters[1];

            return string.IsNullOrWhiteSpace(name) ? MuleCommandResult.Error(RevokeUsage) : MuleCommandResult.Revoke(name);
        }
    }
}
