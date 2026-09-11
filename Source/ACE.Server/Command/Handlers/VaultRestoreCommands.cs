using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using ACE.Database;
using ACE.Database.Models.Shard;
using ACE.Entity.Enum;
using ACE.Server.Entity.AccountVault;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.Command.Handlers
{
    /// <summary>
    /// The barrel's administrator surface (Docs/Market/GIVEAWAY-BULK-BARREL-DESIGN.md section 6.4):
    /// see what an account has thrown away, and put one of those things back.
    ///
    /// DELIBERATELY NOT A /marketadmin SUBCOMMAND. That command's own header states it is READ ONLY so
    /// that it can sit at Sentinel; adding a write would either break that property or force the whole
    /// investigation surface up to Admin. This one writes, so it lives apart and sits at Admin.
    ///
    /// Structured like MarketAdminCommands: a parser returning a result, a dispatch switch, and an
    /// IVaultRestoreCommandTarget seam, so the parsing and the dispatch are testable with no Session
    /// and no shard.
    /// </summary>
    public static class VaultRestoreCommands
    {
        [CommandHandler("vaultrestore", AccessLevel.Admin, CommandHandlerFlag.RequiresWorld, 0,
            "List and restore items an account has fed to its vault barrel.",
            VaultRestoreCommandParser.HelpText)]
        public static void HandleVaultRestore(Session session, params string[] parameters)
        {
            var player = session?.Player;

            if (player == null)
                return;

            var result = VaultRestoreCommandParser.Parse(parameters);

            // Both real subcommands open a ShardDbContext and BLOCK on it, inline on a world thread -
            // the same shape and the same five seconds as /marketadmin and the /mule vault reads. Help
            // and the usage error cost nothing, so gating them would only make the cooldown message
            // harder to understand.
            if (NeedsCooldown(result.Kind))
            {
                if ((DateTime.UtcNow - player.LastVaultRestoreCommandTime).TotalSeconds < CooldownSeconds)
                {
                    session.Network.EnqueueSend(new GameMessageSystemChat(
                        "[VAULTRESTORE] You have used a vault restore command too recently. Try again in a few seconds.",
                        ChatMessageType.System));
                    return;
                }

                player.LastVaultRestoreCommandTime = DateTime.UtcNow;
            }

            Dispatch(result, new PlayerVaultRestoreCommandTarget(player));
        }

        /// <summary>Matches /marketadmin's cooldown; see that constant's remarks for the reasoning.</summary>
        internal const double CooldownSeconds = 5.0;

        private static bool NeedsCooldown(VaultRestoreCommandKind kind)
            => kind != VaultRestoreCommandKind.Help && kind != VaultRestoreCommandKind.UsageError;

        internal static void Dispatch(VaultRestoreCommandResult result, IVaultRestoreCommandTarget target)
        {
            switch (result.Kind)
            {
                case VaultRestoreCommandKind.List:
                    target.ShowList(result.Name, result.Limit);
                    break;

                case VaultRestoreCommandKind.Restore:
                    target.Restore(result.RowId);
                    break;

                case VaultRestoreCommandKind.Help:
                    target.ShowHelp();
                    break;

                case VaultRestoreCommandKind.UsageError:
                default:
                    target.UsageError(result.UsageError ?? VaultRestoreCommandParser.UsageMessage);
                    break;
            }
        }
    }

    internal enum VaultRestoreCommandKind
    {
        List,
        Restore,
        Help,
        UsageError,
    }

    internal sealed class VaultRestoreCommandResult
    {
        public VaultRestoreCommandKind Kind;

        /// <summary>An account name, or a character name on it. Only the List arm uses it.</summary>
        public string Name;

        /// <summary>An account_vault_barrel row id. Only the Restore arm uses it.</summary>
        public uint RowId;

        /// <summary>Rows to render, already clamped by the parser.</summary>
        public int Limit;

        public string UsageError;
    }

    internal static class VaultRestoreCommandParser
    {
        public const string HelpText =
            "list <account or character> [n] | <rowId>\n" +
            "/vaultrestore list <name> [n]  - what that account has barreled, newest first\n" +
            "/vaultrestore <rowId>          - put that row's item or units back in the account's vault\n" +
            "n defaults to 20 and is capped at 50. A restored or purged row is never restorable again.";

        public const string UsageMessage = "[VAULTRESTORE] Type /vaultrestore for the list of commands.";

        /// <summary>Rows per page when none is given. A chat window shows about this many usefully.</summary>
        public const int DefaultLimit = 20;

        /// <summary>The chat-window ceiling. The DAO clamps again at 1000; this is the readability cap.</summary>
        public const int MaxLimit = 50;

        public static VaultRestoreCommandResult Parse(string[] parameters)
        {
            if (parameters == null || parameters.Length == 0)
                return new VaultRestoreCommandResult { Kind = VaultRestoreCommandKind.Help };

            var sub = parameters[0].ToLowerInvariant();

            switch (sub)
            {
                case "help":
                case "?":
                    return new VaultRestoreCommandResult { Kind = VaultRestoreCommandKind.Help };

                case "list":
                case "l":
                {
                    if (parameters.Length < 2)
                        return Usage("[VAULTRESTORE] /vaultrestore list <account or character> [n]");

                    var limit = DefaultLimit;

                    if (parameters.Length > 2)
                    {
                        // Unparsable is an ERROR, never the default: silently showing 20 rows to
                        // somebody who asked for 5 answers a question they did not ask.
                        if (!int.TryParse(parameters[2], out limit) || limit < 1)
                            return Usage($"[VAULTRESTORE] n must be 1 or more (capped at {MaxLimit}).");

                        if (limit > MaxLimit)
                            limit = MaxLimit;
                    }

                    return new VaultRestoreCommandResult
                    {
                        Kind = VaultRestoreCommandKind.List,
                        Name = parameters[1],
                        Limit = limit,
                    };
                }
            }

            // The bare form is a row id. Anything that is not a positive number is a usage error
            // rather than a guess: this command WRITES, and acting on a misread argument would hand
            // somebody an item from a row nobody named.
            if (!uint.TryParse(parameters[0], out var rowId) || rowId == 0)
                return Usage(UsageMessage);

            return new VaultRestoreCommandResult
            {
                Kind = VaultRestoreCommandKind.Restore,
                RowId = rowId,
            };
        }

        private static VaultRestoreCommandResult Usage(string message)
            => new VaultRestoreCommandResult { Kind = VaultRestoreCommandKind.UsageError, UsageError = message };
    }

    /// <summary>
    /// The seam Dispatch is driven through, for the same reason IMarketAdminCommandTarget exists: one
    /// method per kind, so a recording double proves exactly one distinct call per arm and a
    /// transposition fails the test.
    /// </summary>
    internal interface IVaultRestoreCommandTarget
    {
        void ShowList(string name, int limit);
        void Restore(uint rowId);
        void ShowHelp();
        void UsageError(string message);
    }

    /// <summary>
    /// Production target. Reads barrel rows through the same DAO the store writes them with, so an
    /// administrator is never shown a second, differently-cached view of the same rows.
    ///
    /// A NULL from any read means the read FAILED and renders as "unreadable", never as "no results" -
    /// telling an administrator a player threw nothing away because MySQL was briefly down is the one
    /// wrong answer this command can give.
    /// </summary>
    internal sealed class PlayerVaultRestoreCommandTarget : IVaultRestoreCommandTarget
    {
        internal const string UnreadableMessage = "The barrel log is unreadable right now. Try again in a moment.";

        private readonly Player player;

        public PlayerVaultRestoreCommandTarget(Player player)
        {
            this.player = player;
        }

        private void Msg(string message)
            => player.Session.Network.EnqueueSend(new GameMessageSystemChat($"[VAULTRESTORE] {message}", ChatMessageType.System));

        /// <summary>
        /// Resolves a name to an account id, trying the ACCOUNT name first and then a character name
        /// on it. Both are accepted because the design names this argument an account while every
        /// neighbouring vault command takes a character name, and an administrator holding one of the
        /// two should not have to know which. <paramref name="what"/> says which one matched, so the
        /// reader knows what they are looking at.
        /// </summary>
        private bool TryResolve(string name, out uint accountId, out string what)
        {
            accountId = 0;
            what = null;

            if (string.IsNullOrWhiteSpace(name))
                return false;

            var byAccount = DatabaseManager.Authentication.GetAccountIdByName(name);

            if (byAccount != 0)
            {
                accountId = byAccount;
                what = $"account {name} ({byAccount})";
                return true;
            }

            if (AccountVaultManager.TryResolveCharacter(name, out _, out var canonical, out var resolved) && resolved != 0)
            {
                accountId = resolved;
                what = $"{canonical ?? name}'s account ({resolved})";
                return true;
            }

            return false;
        }

        public void ShowList(string name, int limit)
        {
            if (!TryResolve(name, out var accountId, out var what))
            {
                Msg($"No account or character called {name}.");
                return;
            }

            var rows = DatabaseManager.Shard.BaseDatabase.GetAccountVaultBarrels(accountId, limit);

            if (rows == null)
            {
                Msg(UnreadableMessage);
                return;
            }

            if (rows.Count == 0)
            {
                Msg($"{what} has barreled nothing.");
                return;
            }

            Msg(Render(what, rows, RetentionDays()));
        }

        /// <summary>
        /// The live retention window, so the "days left" column reflects the tunable rather than a
        /// number baked in here. Falls back to the registered default if the read throws.
        /// </summary>
        private static int RetentionDays()
        {
            try
            {
                var days = PropertyManager.GetLong(AccountVaultBarrelReaper.RetentionKey, 30).Item;

                if (days < AccountVaultBarrelReaper.MinRetentionDays)
                    return AccountVaultBarrelReaper.MinRetentionDays;

                return days > AccountVaultBarrelReaper.MaxRetentionDays ? AccountVaultBarrelReaper.MaxRetentionDays : (int)days;
            }
            catch (Exception)
            {
                return 30;
            }
        }

        /// <summary>
        /// One line per row. A closed row is shown too, and says WHY it is closed: an administrator
        /// looking for something a player asked about needs to see "you already got that back" and
        /// "retention destroyed that" as answers, not as an absence.
        /// </summary>
        internal static string Render(string what, IReadOnlyList<AccountVaultBarrel> rows, int retentionDays)
        {
            var text = new StringBuilder();

            text.Append($"Barrel rows for {what}, newest first:");

            var now = DateTime.UtcNow;

            foreach (var row in rows)
            {
                var age = now - row.BarreledAt;
                var state = row.RestoredAt != null
                    ? "RESTORED"
                    : row.PurgedAt != null
                        ? "PURGED"
                        : $"{Math.Max(0, retentionDays - (int)age.TotalDays)}d left";

                var kind = row.ItemGuid != null ? $"0x{row.ItemGuid.Value:X8}" : $"ledger wcid {row.Wcid}";

                text.Append($"\n  {row.Id}: {row.ItemName} x{row.Count} ({kind}) - by {row.ActorCharacterName} {Ago(age)} - {state}");
            }

            return text.ToString();
        }

        private static string Ago(TimeSpan age)
        {
            if (age.TotalDays >= 1)
                return $"{(int)age.TotalDays}d ago";

            if (age.TotalHours >= 1)
                return $"{(int)age.TotalHours}h ago";

            if (age.TotalMinutes >= 1)
                return $"{(int)age.TotalMinutes}m ago";

            return "just now";
        }

        public void Restore(uint rowId)
        {
            var row = DatabaseManager.Shard.BaseDatabase.GetAccountVaultBarrel(rowId);

            if (row == null)
            {
                // A single-row read cannot tell "no such row" from "the read failed", so the message
                // says both rather than asserting the one that happens to be more likely.
                Msg($"No barrel row {rowId} could be read. It may not exist, or the shard may be unreachable.");
                return;
            }

            var store = AccountVaultManager.GetStore(row.AccountId);

            if (store == null)
            {
                Msg($"Barrel row {rowId} belongs to account {row.AccountId}, whose vault could not be opened.");
                return;
            }

            var ok = false;
            string failReason = null;

            // The store re-reads the row inside its own serialized region and decides there; the read
            // above is only for the account id. That double read is deliberate - the decision has to
            // be made where a concurrent restore cannot interleave with it.
            if (!store.Enqueue(() => ok = store.TryRestoreFromBarrel(rowId, VaultActor.From(player), out failReason), out var thrown))
            {
                Msg("That account's vault is busy or was just retired. Try again in a moment.");
                return;
            }

            if (thrown != null)
            {
                Msg($"The restore threw and may have got part way through: {thrown.Message}. Check the log before retrying.");
                return;
            }

            if (!ok)
            {
                // The store's refusal sentence, verbatim. It already distinguishes already-restored
                // from purged from no-room, and rewording it here would lose that.
                Msg(failReason ?? "The restore was refused.");
                return;
            }

            Msg($"Restored row {rowId}: {row.ItemName} x{row.Count} back into account {row.AccountId}.");
        }

        public void ShowHelp()
        {
            Msg(VaultRestoreCommandParser.HelpText);
        }

        public void UsageError(string message)
        {
            Msg(message);
        }
    }
}
