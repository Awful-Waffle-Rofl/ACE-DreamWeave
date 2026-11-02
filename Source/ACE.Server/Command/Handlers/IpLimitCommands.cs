using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;

using log4net;

using ACE.Database;
using ACE.Database.Models.Auth;
using ACE.Entity.Enum;
using ACE.Server.Entity.RewardClaims;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.WorldObjects;

namespace ACE.Server.Command.Handlers
{
    /// <summary>
    /// WaffleACE: admin command surface for the IP-based active-player limit rule engine in
    /// <see cref="IpLimitManager"/>. This file owns only the command layer - the rule itself, the config keys,
    /// and the mule_landblocks / ip_limit_exempt_accounts CSV helpers live in IpLimitManager, which this file
    /// calls into rather than duplicating.
    /// </summary>
    public static class IpLimitCommands
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private const string IpLimitUsage =
            "Usage:\n"
            + "  iplimit [show]                        - show current IP active-player limit config and state\n"
            + "  iplimit landblock list                 - list mule landblocks\n"
            + "  iplimit landblock add <token>          - add a mule landblock: 016C (any realm) or 01F5@1 (realm 1 only)\n"
            + "  iplimit landblock remove <token>       - remove a mule landblock\n"
            + "  iplimit exempt list                    - list exempt accounts\n"
            + "  iplimit exempt add <account>           - add an exempt account\n"
            + "  iplimit exempt remove <account>        - remove an exempt account\n"
            + "                                           (an exempt account is its own household of one, not a bypass)\n"
            + "  iplimit cluster list                   - list household clusters\n"
            + "  iplimit cluster show <name>            - members of a cluster, and who is online under it\n"
            + "  iplimit cluster create <name> <account>... - create a cluster (name: a-z 0-9 -, up to 32)\n"
            + "  iplimit cluster add <name> <account>   - add an account to a cluster\n"
            + "  iplimit cluster remove <name> <account> - remove an account (an emptied cluster is deleted)\n"
            + "  iplimit cluster delete <name>          - delete a cluster";

        [CommandHandler("iplimit", AccessLevel.Admin, CommandHandlerFlag.None, 0,
            "Shows or manages the IP-based active-player limit (mule landblocks, exempt accounts and household clusters).",
            "[show | landblock <list|add|remove> [token] | exempt <list|add|remove> [account] | cluster <list|show|create|add|remove|delete> [name] [account...]]")]
        public static void HandleIpLimit(Session session, params string[] parameters)
        {
            if (parameters.Length == 0 || string.Equals(parameters[0], "show", StringComparison.OrdinalIgnoreCase))
            {
                ShowState(session);
                return;
            }

            switch (parameters[0].ToLowerInvariant())
            {
                case "landblock":
                    HandleLandblockSubcommand(session, parameters);
                    return;

                case "exempt":
                    HandleExemptSubcommand(session, parameters);
                    return;

                case "cluster":
                    HandleClusterSubcommand(session, parameters);
                    return;

                default:
                    CommandHandlerHelper.WriteOutputInfo(session, IpLimitUsage, ChatMessageType.Broadcast);
                    return;
            }
        }

        private static void ShowState(Session session)
        {
            var maxFree = IpLimitManager.ClampCap(PropertyManager.GetLong("ip_limit_max_free").Item);
            var maxConfined = IpLimitManager.ClampCap(PropertyManager.GetLong("ip_limit_max_confined").Item);

            var sb = new StringBuilder();
            sb.AppendLine("IP active-player limit:");
            sb.AppendLine($"  ip_limit_enabled: {IpLimitManager.Enabled}");
            sb.AppendLine($"  ip_limit_max_free: {maxFree}");
            sb.AppendLine($"  ip_limit_max_confined: {maxConfined}");
            sb.AppendLine($"  total cap (max_free + max_confined): {maxFree + maxConfined}");
            sb.AppendLine($"  ip_limit_exempt_access_level: {PropertyManager.GetLong("ip_limit_exempt_access_level").Item}");
            sb.AppendLine($"  ip_limit_grace_seconds: {PropertyManager.GetLong("ip_limit_grace_seconds").Item}");
            sb.AppendLine($"  ip_limit_sweep_seconds: {PropertyManager.GetLong("ip_limit_sweep_seconds").Item}");
            sb.AppendLine($"  ip_limit_enforce_on_arrival: {PropertyManager.GetBool("ip_limit_enforce_on_arrival").Item}");

            var muleLandblocks = IpLimitManager.ListMuleLandblocks();
            sb.AppendLine(muleLandblocks.Count > 0
                ? $"  mule_landblocks: {string.Join(", ", muleLandblocks)}"
                : "  mule_landblocks: (none)");

            var exemptAccounts = IpLimitManager.ListExemptAccounts();
            sb.AppendLine(exemptAccounts.Count > 0
                ? $"  ip_limit_exempt_accounts: {string.Join(", ", exemptAccounts)}"
                : "  ip_limit_exempt_accounts: (none)");

            var clusters = IpLimitManager.ReadClusters();
            if (clusters.Count == 0)
            {
                sb.AppendLine("  ip_household_clusters: (none)");
            }
            else
            {
                sb.AppendLine($"  ip_household_clusters: {clusters.Count} cluster(s)");
                foreach (var name in clusters.Names)
                    sb.AppendLine($"    {name}: {clusters.MembersOf(name).Count} account(s)");
            }

            CommandHandlerHelper.WriteOutputInfo(session, sb.ToString(), ChatMessageType.Broadcast);
        }

        private static void HandleLandblockSubcommand(Session session, string[] parameters)
        {
            if (parameters.Length < 2)
            {
                CommandHandlerHelper.WriteOutputInfo(session, IpLimitUsage, ChatMessageType.Broadcast);
                return;
            }

            switch (parameters[1].ToLowerInvariant())
            {
                case "list":
                {
                    var muleLandblocks = IpLimitManager.ListMuleLandblocks();
                    CommandHandlerHelper.WriteOutputInfo(session, muleLandblocks.Count > 0
                        ? $"mule_landblocks: {string.Join(", ", muleLandblocks)}"
                        : "mule_landblocks: (none)", ChatMessageType.Broadcast);
                    return;
                }

                case "add":
                {
                    if (parameters.Length < 3)
                    {
                        CommandHandlerHelper.WriteOutputInfo(session, IpLimitUsage, ChatMessageType.Broadcast);
                        return;
                    }

                    var token = parameters[2];
                    if (IpLimitManager.TryAddMuleLandblock(token, out var message))
                    {
                        CommandHandlerHelper.WriteOutputInfo(session, message, ChatMessageType.Broadcast);
                        PropertyManager.ResyncVariables();
                        PlayerManager.BroadcastToAuditChannel(session?.Player, $"{DescribeIssuer(session)} added mule landblock '{token}' to the IP active-player limit.");
                    }
                    else
                    {
                        CommandHandlerHelper.WriteOutputInfo(session, message, ChatMessageType.Broadcast);
                    }
                    return;
                }

                case "remove":
                {
                    if (parameters.Length < 3)
                    {
                        CommandHandlerHelper.WriteOutputInfo(session, IpLimitUsage, ChatMessageType.Broadcast);
                        return;
                    }

                    var token = parameters[2];
                    if (IpLimitManager.TryRemoveMuleLandblock(token, out var message))
                    {
                        CommandHandlerHelper.WriteOutputInfo(session, message, ChatMessageType.Broadcast);
                        PropertyManager.ResyncVariables();
                        PlayerManager.BroadcastToAuditChannel(session?.Player, $"{DescribeIssuer(session)} removed mule landblock '{token}' from the IP active-player limit.");
                    }
                    else
                    {
                        CommandHandlerHelper.WriteOutputInfo(session, message, ChatMessageType.Broadcast);
                    }
                    return;
                }

                default:
                    CommandHandlerHelper.WriteOutputInfo(session, IpLimitUsage, ChatMessageType.Broadcast);
                    return;
            }
        }

        private static void HandleExemptSubcommand(Session session, string[] parameters)
        {
            if (parameters.Length < 2)
            {
                CommandHandlerHelper.WriteOutputInfo(session, IpLimitUsage, ChatMessageType.Broadcast);
                return;
            }

            switch (parameters[1].ToLowerInvariant())
            {
                case "list":
                {
                    var exemptAccounts = IpLimitManager.ListExemptAccounts();
                    CommandHandlerHelper.WriteOutputInfo(session, exemptAccounts.Count > 0
                        ? $"ip_limit_exempt_accounts: {string.Join(", ", exemptAccounts)}"
                        : "ip_limit_exempt_accounts: (none)", ChatMessageType.Broadcast);
                    return;
                }

                case "add":
                {
                    if (parameters.Length < 3)
                    {
                        CommandHandlerHelper.WriteOutputInfo(session, IpLimitUsage, ChatMessageType.Broadcast);
                        return;
                    }

                    var accountName = parameters[2];
                    var listsBefore = IpLimitManager.ReadHouseholdLists();

                    var target = DatabaseManager.Authentication.GetAccountByName(accountName);
                    var exemptRefusal = target == null ? null : IpLimitManager.ExemptMembershipRefusal(target.AccountId, target.AccountName, listsBefore);
                    if (exemptRefusal != null)
                    {
                        CommandHandlerHelper.WriteOutputInfo(session, exemptRefusal, ChatMessageType.Broadcast);
                        return;
                    }

                    if (IpLimitManager.TryAddExemptAccount(accountName, out var message))
                    {
                        CommandHandlerHelper.WriteOutputInfo(session, message, ChatMessageType.Broadcast);
                        PropertyManager.ResyncVariables();
                        NotifyExemptChange(session, accountName, listsBefore);
                        PlayerManager.BroadcastToAuditChannel(session?.Player, $"{DescribeIssuer(session)} added exempt account '{accountName}' to the IP active-player limit.");
                    }
                    else
                    {
                        CommandHandlerHelper.WriteOutputInfo(session, message, ChatMessageType.Broadcast);
                    }
                    return;
                }

                case "remove":
                {
                    if (parameters.Length < 3)
                    {
                        CommandHandlerHelper.WriteOutputInfo(session, IpLimitUsage, ChatMessageType.Broadcast);
                        return;
                    }

                    var accountName = parameters[2];
                    var listsBefore = IpLimitManager.ReadHouseholdLists();
                    if (IpLimitManager.TryRemoveExemptAccount(accountName, out var message))
                    {
                        CommandHandlerHelper.WriteOutputInfo(session, message, ChatMessageType.Broadcast);
                        PropertyManager.ResyncVariables();
                        NotifyExemptChange(session, accountName, listsBefore);
                        PlayerManager.BroadcastToAuditChannel(session?.Player, $"{DescribeIssuer(session)} removed exempt account '{accountName}' from the IP active-player limit.");
                    }
                    else
                    {
                        CommandHandlerHelper.WriteOutputInfo(session, message, ChatMessageType.Broadcast);
                    }
                    return;
                }

                default:
                    CommandHandlerHelper.WriteOutputInfo(session, IpLimitUsage, ChatMessageType.Broadcast);
                    return;
            }
        }

        // Routes an exempt-list change through the membership seam. Unknown account names are skipped: the list
        // accepts any verbatim name, but only a real account has a key to move.
        private static void NotifyExemptChange(Session session, string accountName, IpLimitManager.HouseholdLists listsBefore)
        {
            var account = DatabaseManager.Authentication.GetAccountByName(accountName);
            if (account == null)
                return;

            var listsAfter = IpLimitManager.ReadHouseholdLists();

            CarryForwardRewardClaims(session, new[]
            {
                new MembershipChange(
                    account.AccountId,
                    account.AccountName,
                    IpLimitManager.AddressIndependentKey(account.AccountId, account.AccountName, listsBefore),
                    IpLimitManager.AddressIndependentKey(account.AccountId, account.AccountName, listsAfter)),
            });
        }

        private sealed class MembershipChange
        {
            public MembershipChange(uint accountId, string accountName, string oldKey, string newKey)
            {
                AccountId = accountId;
                AccountName = accountName;
                OldKey = oldKey;
                NewKey = newKey;
            }

            public uint AccountId { get; }
            public string AccountName { get; }
            public string OldKey { get; }
            public string NewKey { get; }
        }

        // Every reward-claim carry-forward runs on ONE pool-thread chain, in the order the commands were issued:
        // the re-key blocks on the shard database, so it must not run on the world thread, and two quick changes
        // to the same account (add, then remove) must land in that order or the final keys would be the older
        // command's.
        private static readonly object carryForwardGate = new object();
        private static System.Threading.Tasks.Task carryForwardChain = System.Threading.Tasks.Task.CompletedTask;

        // Routes each membership change through IpLimitManager.OnClusterMembershipChanged (which carries the
        // account's reward_claim rows to its new household) and replies with what moved. A change whose key did
        // not move is dropped here, without a database round trip.
        private static void CarryForwardRewardClaims(Session session, IEnumerable<MembershipChange> changes)
        {
            var pending = changes.Where(c => RewardClaimCarryForward.ShouldRekey(c.OldKey, c.NewKey)).ToList();
            if (pending.Count == 0)
                return;

            lock (carryForwardGate)
            {
                carryForwardChain = carryForwardChain.ContinueWith(_ =>
                {
                    var sb = new StringBuilder();

                    foreach (var change in pending)
                    {
                        RewardClaimCarryForwardOutcome outcome;

                        try
                        {
                            outcome = IpLimitManager.OnClusterMembershipChanged(change.AccountId, change.OldKey, change.NewKey);
                        }
                        catch (Exception ex)
                        {
                            log.Error($"[IP household] reward claim carry-forward failed for account {change.AccountId} ({change.OldKey ?? "address"} -> {change.NewKey ?? "address"}): {ex}");
                            outcome = null;
                        }

                        sb.AppendLine(RewardClaimCarryForward.Describe(change.AccountName ?? $"(account #{change.AccountId})", outcome));
                    }

                    Say(session, sb.ToString().TrimEnd());
                }, System.Threading.Tasks.TaskScheduler.Default);
            }
        }

        // ==================================================================================
        // iplimit cluster ...
        // ==================================================================================

        private static void Say(Session session, string text) => CommandHandlerHelper.WriteOutputInfo(session, text, ChatMessageType.Broadcast);

        private static string AccountLabel(uint accountId)
        {
            var account = DatabaseManager.Authentication.GetAccountById(accountId);
            return account?.AccountName ?? $"(account #{accountId})";
        }

        private static void HandleClusterSubcommand(Session session, string[] parameters)
        {
            if (parameters.Length < 2)
            {
                Say(session, IpLimitUsage);
                return;
            }

            var clusters = IpLimitManager.ReadClusters();

            switch (parameters[1].ToLowerInvariant())
            {
                case "list":
                {
                    if (clusters.Count == 0)
                    {
                        Say(session, "ip_household_clusters: (none)");
                        return;
                    }

                    var sb = new StringBuilder();
                    sb.AppendLine("ip_household_clusters:");
                    foreach (var name in clusters.Names)
                        sb.AppendLine($"  {name}: {string.Join(", ", clusters.MembersOf(name).Select(AccountLabel))}");

                    Say(session, sb.ToString());
                    return;
                }

                case "show":
                {
                    if (parameters.Length < 3)
                    {
                        Say(session, IpLimitUsage);
                        return;
                    }

                    var name = parameters[2].ToLowerInvariant();
                    var members = clusters.MembersOf(name);
                    if (members.Count == 0)
                    {
                        Say(session, $"No cluster named '{name}'.");
                        return;
                    }

                    var key = IpLimitManager.ClusterKey(name);
                    var lists = IpLimitManager.ReadHouseholdLists();

                    var sb = new StringBuilder();
                    sb.AppendLine($"Cluster '{name}' (key {key}): {string.Join(", ", members.Select(AccountLabel))}");

                    var online = PlayerManager.GetAllOnline()
                        .Where(p => p.Session != null && string.Equals(IpLimitManager.HouseholdKey(p.Session, lists), key, StringComparison.Ordinal))
                        .ToList();

                    if (online.Count == 0)
                    {
                        sb.AppendLine("  online: (none)");
                    }
                    else
                    {
                        sb.AppendLine($"  online: {online.Count}");
                        foreach (var player in online)
                            sb.AppendLine($"    {player.Name} (account: {player.Session.Account}, level {player.Level ?? 0}, address {player.Session.EndPointC2S?.Address})");
                    }

                    Say(session, sb.ToString());
                    return;
                }

                case "create":
                {
                    if (parameters.Length < 3)
                    {
                        Say(session, IpLimitUsage);
                        return;
                    }

                    var name = parameters[2].ToLowerInvariant();
                    if (!AccountClusters.IsValidName(name))
                    {
                        Say(session, $"'{name}' is not a valid cluster name (a-z, 0-9 and '-', 1 to {AccountClusters.MaxNameLength} characters).");
                        return;
                    }

                    if (clusters.MembersOf(name).Count > 0)
                    {
                        Say(session, $"Cluster '{name}' already exists.");
                        return;
                    }

                    if (parameters.Length < 4)
                    {
                        Say(session, "A cluster needs at least one account. Usage: iplimit cluster create <name> <account>...");
                        return;
                    }

                    var members = new List<Account>();
                    foreach (var accountName in parameters.Skip(3))
                    {
                        if (!TryResolveAccountForCluster(session, clusters, accountName, name, members, out var account))
                            return;

                        members.Add(account);
                    }

                    ApplyClusterChange(session, clusters, name, members.Select(a => a.AccountId).ToList(),
                        members.Select(a => (a.AccountId, a.AccountName)),$"{DescribeIssuer(session)} created IP household cluster '{name}' with {members.Count} account(s).");
                    return;
                }

                case "add":
                {
                    if (parameters.Length < 4)
                    {
                        Say(session, IpLimitUsage);
                        return;
                    }

                    var name = parameters[2].ToLowerInvariant();
                    var current = clusters.MembersOf(name);
                    if (current.Count == 0)
                    {
                        Say(session, $"No cluster named '{name}'.");
                        return;
                    }

                    if (!TryResolveAccountForCluster(session, clusters, parameters[3], name, new List<Account>(), out var account))
                        return;

                    if (current.Contains(account.AccountId))
                    {
                        Say(session, $"'{account.AccountName}' is already in cluster '{name}'.");
                        return;
                    }

                    ApplyClusterChange(session, clusters, name, current.Concat(new[] { account.AccountId }).ToList(),
                        new[] { (account.AccountId, account.AccountName) }, $"{DescribeIssuer(session)} added account '{account.AccountName}' to IP household cluster '{name}'.");
                    return;
                }

                case "remove":
                {
                    if (parameters.Length < 4)
                    {
                        Say(session, IpLimitUsage);
                        return;
                    }

                    var name = parameters[2].ToLowerInvariant();
                    var current = clusters.MembersOf(name);
                    if (current.Count == 0)
                    {
                        Say(session, $"No cluster named '{name}'.");
                        return;
                    }

                    var account = DatabaseManager.Authentication.GetAccountByName(parameters[3]);
                    if (account == null)
                    {
                        Say(session, $"No account named '{parameters[3]}'.");
                        return;
                    }

                    if (!current.Contains(account.AccountId))
                    {
                        Say(session, $"'{account.AccountName}' is not in cluster '{name}'.");
                        return;
                    }

                    var remaining = current.Where(id => id != account.AccountId).ToList();
                    var emptied = remaining.Count == 0 ? " The cluster is now empty and was deleted." : "";

                    ApplyClusterChange(session, clusters, name, remaining, new[] { (account.AccountId, account.AccountName) },
                        $"{DescribeIssuer(session)} removed account '{account.AccountName}' from IP household cluster '{name}'.{emptied}");
                    return;
                }

                case "delete":
                {
                    if (parameters.Length < 3)
                    {
                        Say(session, IpLimitUsage);
                        return;
                    }

                    var name = parameters[2].ToLowerInvariant();
                    var current = clusters.MembersOf(name);
                    if (current.Count == 0)
                    {
                        Say(session, $"No cluster named '{name}'.");
                        return;
                    }

                    // Every member id gets a change, including one whose account row is gone (null name): its claim
                    // rows would otherwise stay on cluster:<name>.
                    var affected = current.Select(id => (Id: id, Name: DatabaseManager.Authentication.GetAccountById(id)?.AccountName)).ToList();

                    ApplyClusterChange(session, clusters, name, new List<uint>(), affected,
                        $"{DescribeIssuer(session)} deleted IP household cluster '{name}' ({current.Count} account(s)).");
                    return;
                }

                default:
                    Say(session, IpLimitUsage);
                    return;
            }
        }

        // Resolves an account name for a cluster membership. Refuses (and says why) an unknown name, an account
        // already in ANOTHER cluster, or a name repeated within `pending` (the accounts of the create in progress).
        private static bool TryResolveAccountForCluster(Session session, AccountClusters clusters, string accountName, string targetCluster, List<Account> pending, out Account account)
        {
            account = DatabaseManager.Authentication.GetAccountByName(accountName);

            if (account == null)
            {
                Say(session, $"No account named '{accountName}'.");
                return false;
            }

            var refusal = IpLimitManager.ClusterMembershipRefusal(account.AccountId, account.AccountName, targetCluster, IpLimitManager.ReadHouseholdLists());
            if (refusal != null)
            {
                Say(session, refusal);
                return false;
            }

            var accountId = account.AccountId;
            if (pending.Any(a => a.AccountId == accountId))
            {
                Say(session, $"'{account.AccountName}' is listed twice.");
                return false;
            }

            return true;
        }

        // The single write path for every cluster change: writes the canonical value, resyncs, audits, and routes
        // each affected account through the membership seam with its key before and after.
        private static void ApplyClusterChange(Session session, AccountClusters before, string name, IReadOnlyCollection<uint> newMembers, IEnumerable<(uint Id, string Name)> affected, string auditText)
        {
            var listsBefore = IpLimitManager.ReadHouseholdLists();

            var updated = before.With(name, newMembers);

            if (!IpLimitManager.TryWriteClusters(updated, out var failure))
            {
                Say(session, failure);
                return;
            }

            PropertyManager.ResyncVariables();

            var listsAfter = IpLimitManager.ReadHouseholdLists();

            var changes = affected.Select(a => new MembershipChange(
                a.Id,
                a.Name,
                IpLimitManager.AddressIndependentKey(a.Id, a.Name, listsBefore),
                IpLimitManager.AddressIndependentKey(a.Id, a.Name, listsAfter))).ToList();

            Say(session, auditText);
            PlayerManager.BroadcastToAuditChannel(session?.Player, auditText);

            CarryForwardRewardClaims(session, changes);
        }

        /// <summary>
        /// Names the issuer of a mutation for the audit channel line. Falls back to "console" when run from the
        /// server console (no session/player), which is a supported way to run this command since it declares
        /// CommandHandlerFlag.None.
        /// </summary>
        private static string DescribeIssuer(Session session)
        {
            return session?.Player?.Name ?? "console";
        }

        /// <summary>
        /// The short, player-facing label for /iplimitwho's CONFINED (...) marker.
        /// </summary>
        private static string DescribeConfinementReason(IpLimitConfinementReason reason)
        {
            switch (reason)
            {
                case IpLimitConfinementReason.MuleLandblock:
                    return "Market";
                case IpLimitConfinementReason.Housing:
                    return "Housing";
                case IpLimitConfinementReason.PvpArena:
                    return "Arena";
                case IpLimitConfinementReason.SpeedRun:
                case IpLimitConfinementReason.ProvingGrounds:
                    return "Proving Grounds";
                case IpLimitConfinementReason.ActivityTail:
                    return "recently active";
                case IpLimitConfinementReason.Teleporting:
                    return "teleporting";
                default:
                    return reason.ToString();
            }
        }

        // ==================================================================================
        // /iplimitwho - read-only occupancy readout
        // ==================================================================================

        private class IpLimitWhoRow
        {
            public Player Player;
            public bool Exempt;
            public string Key;
        }

        [CommandHandler("iplimitwho", AccessLevel.Sentinel, CommandHandlerFlag.None, 0,
            "Shows current online-player occupancy grouped by household (client IP, or cluster / exempt account), for the IP active-player limit.",
            "[ip address | account name | cluster name]")]
        public static void HandleIpLimitWho(Session session, params string[] parameters)
        {
            var filter = parameters.Length > 0 ? parameters[0] : null;

            var maxFree = IpLimitManager.ClampCap(PropertyManager.GetLong("ip_limit_max_free").Item);
            var maxConfined = IpLimitManager.ClampCap(PropertyManager.GetLong("ip_limit_max_confined").Item);
            var totalCap = maxFree + maxConfined;

            var lists = IpLimitManager.ReadHouseholdLists();
            var filterCluster = string.IsNullOrEmpty(filter) ? null : IpLimitManager.ClusterKey(filter.ToLowerInvariant());

            var matching = new List<IpLimitWhoRow>();

            foreach (var player in PlayerManager.GetAllOnline())
            {
                var playerSession = player.Session;
                if (playerSession == null)
                    continue;

                var address = playerSession.EndPointC2S?.Address;
                var key = IpLimitManager.HouseholdKey(playerSession, lists);
                if (key == null)
                    continue;

                if (!string.IsNullOrEmpty(filter))
                {
                    var matchesIp = address != null && string.Equals(address.ToString(), filter, StringComparison.OrdinalIgnoreCase);
                    var matchesAccount = !string.IsNullOrEmpty(playerSession.Account) && string.Equals(playerSession.Account, filter, StringComparison.OrdinalIgnoreCase);
                    var matchesCluster = string.Equals(key, filterCluster, StringComparison.Ordinal);
                    if (!matchesIp && !matchesAccount && !matchesCluster)
                        continue;
                }

                matching.Add(new IpLimitWhoRow
                {
                    Player = player,
                    Exempt = IpLimitManager.IsExempt(playerSession),
                    Key = key,
                });
            }

            var groups = IpLimitManager.GroupByHousehold(matching, r => r.Key);

            if (groups.Count == 0)
            {
                CommandHandlerHelper.WriteOutputInfo(session, "No matching online players found.", ChatMessageType.Broadcast);
                return;
            }

            var sb = new StringBuilder();

            foreach (var group in groups.OrderByDescending(g => g.Value.Count))
            {
                var rows = group.Value;

                // Exempt rows are shown in the per-character output below (with an EXEMPT marker) but must be
                // excluded here - every real enforcement path (the sweep's grouping loop and GetResidents)
                // strips exempt sessions from the candidate pool before applying the caps, so counting them
                // here would flag groups the actual rule would never touch.
                var nonExemptRows = rows.Where(r => !r.Exempt).ToList();
                var nonConfinedCount = nonExemptRows.Count(r => !IpLimitManager.IsConfined(r.Player));
                var overLimit = nonConfinedCount > maxFree || nonExemptRows.Count > totalCap;

                sb.AppendLine($"{group.Key} - {rows.Count} character(s){(overLimit ? " - OVER LIMIT" : "")}");

                foreach (var row in rows)
                {
                    var player = row.Player;
                    var reason = IpLimitManager.GetConfinementReason(player);
                    var landblock = player.Location?.LandblockShort;
                    var landblockText = landblock.HasValue ? ((ushort)landblock.Value).ToString("X4") : "----";

                    var markers = new List<string> { reason == IpLimitConfinementReason.None ? "FREE" : $"CONFINED ({DescribeConfinementReason(reason)})" };
                    if (row.Exempt)
                        markers.Add("EXEMPT");

                    sb.AppendLine($"    {player.Name} (account: {player.Session?.Account ?? "?"}, address {player.Session?.EndPointC2S?.Address}, level {player.Level ?? 0}, landblock {landblockText}) [{string.Join(", ", markers)}]");
                }
            }

            CommandHandlerHelper.WriteOutputInfo(session, sb.ToString(), ChatMessageType.Broadcast);
        }
    }
}
