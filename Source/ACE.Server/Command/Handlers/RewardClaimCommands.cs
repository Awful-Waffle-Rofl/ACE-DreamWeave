using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

using log4net;

using ACE.Database;
using ACE.Database.Models.Shard;
using ACE.Entity.Enum;
using ACE.Server.Entity.RewardClaims;
using ACE.Server.Managers;
using ACE.Server.Network;

namespace ACE.Server.Command.Handlers
{
    public enum RewardClaimCommandKind
    {
        Usage,
        Show,
        Clear
    }

    /// <summary>A parsed /rewardclaim invocation. <see cref="Error"/> non-null means refuse with that text.</summary>
    internal sealed class RewardClaimCommandRequest
    {
        public RewardClaimCommandKind Kind { get; set; }
        public string Key { get; set; }

        /// <summary>Set for `account &lt;name&gt;`.</summary>
        public string AccountName { get; set; }

        /// <summary>Set (normalized) for `ip &lt;addr&gt;`.</summary>
        public string IpKey { get; set; }

        public string Error { get; set; }
    }

    /// <summary>/rewardclaim parsing and reply text, free of Session so it is unit-testable.</summary>
    internal static class RewardClaimCommandText
    {
        internal static readonly string UsageText =
            "Usage:\n"
            + "  rewardclaim show <key> account <name>   - list the reward_claim rows for that account\n"
            + "  rewardclaim show <key> ip <addr>        - list the reward_claim rows for that IP\n"
            + "  rewardclaim clear <key> account <name>  - delete that account's row(s)\n"
            + "  rewardclaim clear <key> ip <addr>       - delete that IP's row(s)\n"
            + "clear deletes WHOLE rows, so it frees both the account AND the IP each row held for that key.\n"
            + PeriodNote + "\n"
            + "Keys: " + string.Join(", ", RewardClaimAllowlist.Entries.Keys.OrderBy(k => k, StringComparer.Ordinal));

        internal static string PeriodNote
        {
            get
            {
                var periodic = RewardClaimAllowlist.Entries.Values
                    .Where(e => e.Period != ClaimPeriod.Forever)
                    .OrderBy(e => e.Key, StringComparer.Ordinal)
                    .Select(e => $"{e.Key} ({e.Period})")
                    .ToList();

                if (periodic.Count == 0)
                    return "No key is periodic.";

                return "Periodic keys are stored per period: for show the bare key means the current period; clear needs the explicit form. Add "
                    + RewardClaimRules.SpeedSeasonSuffix + "<seasonId> / " + RewardClaimRules.UtcDaySuffix + "<yyyyMMdd> to name one. Periodic: "
                    + string.Join(", ", periodic);
            }
        }

        internal static string SuffixExample(ClaimPeriod period)
        {
            switch (period)
            {
                case ClaimPeriod.SpeedSeason:
                    return RewardClaimRules.SpeedSeasonSuffix + "4";
                case ClaimPeriod.UtcDay:
                    return RewardClaimRules.UtcDaySuffix + "20261002";
                default:
                    return "";
            }
        }

        internal static RewardClaimCommandRequest Parse(string[] parameters)
        {
            var usage = new RewardClaimCommandRequest { Kind = RewardClaimCommandKind.Usage };

            if (parameters == null || parameters.Length != 4)
                return usage;

            RewardClaimCommandKind kind;

            if (string.Equals(parameters[0], "show", StringComparison.OrdinalIgnoreCase))
                kind = RewardClaimCommandKind.Show;
            else if (string.Equals(parameters[0], "clear", StringComparison.OrdinalIgnoreCase))
                kind = RewardClaimCommandKind.Clear;
            else
                return usage;

            var key = parameters[1];

            // A periodic key (ClaimPeriod other than Forever) is stored with a period suffix. The admin may
            // name the stored form directly (e.g. ProvingGroundsSpeedTier1@claim#S4) to reach any period. A
            // bare periodic key resolves to the CURRENT period for show only (refused when there is none);
            // clear deletes rows, so it always requires the explicit suffix.
            if (RewardClaimAllowlist.TryGet(key, out var bareEntry) && bareEntry.Period != ClaimPeriod.Forever)
            {
                var current = RewardClaimService.CurrentStoredKey(bareEntry);

                if (kind == RewardClaimCommandKind.Clear)
                    return new RewardClaimCommandRequest { Kind = kind, Key = key, Error = $"'{key}' is a per-{bareEntry.Period} claim; clear needs the explicit stored key. " + (current != null ? $"The current one is {current}." : $"No {bareEntry.Period} is current; name one, e.g. {key}{SuffixExample(bareEntry.Period)}.") };

                if (current == null)
                    return new RewardClaimCommandRequest { Kind = kind, Key = key, Error = $"'{key}' is a per-{bareEntry.Period} claim and no {bareEntry.Period} is current. Name the stored key with its suffix instead, e.g. {key}{SuffixExample(bareEntry.Period)}." };

                key = current;
            }
            else if (!RewardClaimRules.TryParseStoredKey(key, out _))
                return new RewardClaimCommandRequest { Kind = kind, Key = key, Error = $"'{key}' is not a reward claim key. Keys (case-sensitive): {string.Join(", ", RewardClaimAllowlist.Entries.Keys.OrderBy(k => k, StringComparer.Ordinal))}. {PeriodNote}" };

            var value = parameters[3];

            if (string.Equals(parameters[2], "account", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(value))
                    return usage;

                return new RewardClaimCommandRequest { Kind = kind, Key = key, AccountName = value.Trim() };
            }

            if (string.Equals(parameters[2], "ip", StringComparison.OrdinalIgnoreCase))
            {
                // Require a dot or a colon: IPAddress.TryParse also accepts bare integers ("5" is 0.0.0.5),
                // which is never what an admin clearing a claim meant.
                if (string.IsNullOrWhiteSpace(value) || (value.IndexOf('.') < 0 && value.IndexOf(':') < 0) || !IPAddress.TryParse(value.Trim(), out var address))
                    return new RewardClaimCommandRequest { Kind = kind, Key = key, Error = $"'{value}' is not an IP address." };

                return new RewardClaimCommandRequest { Kind = kind, Key = key, IpKey = RewardClaimRules.NormalizeIpKey(address, false) };
            }

            return usage;
        }

        internal static string DescribeSubject(RewardClaimCommandRequest request)
            => request.AccountName != null ? $"account {request.AccountName}" : $"ip {request.IpKey}";

        internal static string FormatRow(RewardClaim row)
            => $"  account {row.AccountId}, ip {row.IpKey ?? "exempt"} (seen {row.IpAddress ?? "-"}), character 0x{row.CharacterId:X8}, npc {row.NpcWcid}, claimed {row.ClaimedAt:yyyy-MM-dd HH:mm:ss} UTC";
    }

    /// <summary>
    /// Admin surface for the reward_claim ledger behind EmoteType.ClaimRewardOnce. Refuses any key not on
    /// RewardClaimAllowlist. The database work runs on a pool thread, like /charsheet.
    /// </summary>
    public static class RewardClaimCommands
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        [CommandHandler("rewardclaim", AccessLevel.Admin, CommandHandlerFlag.None, 0,
            "Shows or clears once-per-account/IP reward claims (Assay Row hammers, Proving Grounds herald tiers). clear deletes whole rows, freeing both the account and the IP for that key.",
            "show|clear <key> account <name> | ip <addr>")]
        public static void HandleRewardClaim(Session session, params string[] parameters)
        {
            var request = RewardClaimCommandText.Parse(parameters);

            if (request.Kind == RewardClaimCommandKind.Usage)
            {
                CommandHandlerHelper.WriteOutputInfo(session, RewardClaimCommandText.UsageText, ChatMessageType.Broadcast);
                return;
            }

            if (request.Error != null)
            {
                CommandHandlerHelper.WriteOutputInfo(session, request.Error, ChatMessageType.Broadcast);
                return;
            }

            var issuer = session?.Player;
            var issuerName = issuer?.Name ?? "console";

            Task.Run(() =>
            {
                string reply;
                bool cleared;

                try
                {
                    reply = Execute(RewardClaimService.Store, name => DatabaseManager.Authentication.GetAccountIdByName(name), request, out cleared);
                }
                catch (Exception ex)
                {
                    log.Error($"[REWARDCLAIM] /rewardclaim failed for {issuerName}: {ex}");
                    reply = "The reward claim command failed; see the server log.";
                    cleared = false;
                }

                if (cleared)
                    PlayerManager.BroadcastToAuditChannel(issuer, $"{issuerName} cleared reward claim {request.Key} for {RewardClaimCommandText.DescribeSubject(request)}. {reply}");

                CommandHandlerHelper.WriteOutputInfo(session, reply, ChatMessageType.Broadcast);
            });
        }

        /// <summary>The command's database half, free of Session. <paramref name="cleared"/> is TRUE when a clear deleted at least one row.</summary>
        internal static string Execute(IRewardClaimStore store, Func<string, uint> accountIdByName, RewardClaimCommandRequest request, out bool cleared)
        {
            cleared = false;

            uint? accountId = null;

            if (request.AccountName != null)
            {
                var id = accountIdByName(request.AccountName);

                if (id == 0)
                    return $"No account named '{request.AccountName}'.";

                accountId = id;
            }

            var subject = RewardClaimCommandText.DescribeSubject(request);

            if (request.Kind == RewardClaimCommandKind.Clear)
            {
                if (!store.Delete(request.Key, accountId, request.IpKey, out var deleted))
                    return $"Could not clear {request.Key} for {subject}: the delete failed.";

                cleared = deleted > 0;

                return deleted == 0
                    ? $"No {request.Key} claim found for {subject}; nothing deleted."
                    : $"Deleted {deleted} {request.Key} claim row(s) for {subject}. Both the account and the IP on each deleted row may claim this key again.";
            }

            if (!store.TryGetClaims(request.Key, accountId, request.IpKey, out var rows))
                return $"Could not read {request.Key} claims for {subject}: the read failed.";

            if (rows == null || rows.Count == 0)
                return $"No {request.Key} claim found for {subject}.";

            var sb = new StringBuilder();
            sb.Append($"{request.Key} claims for {subject}: {rows.Count} row(s)");

            foreach (var row in rows)
                sb.Append('\n').Append(RewardClaimCommandText.FormatRow(row));

            return sb.ToString();
        }
    }
}
