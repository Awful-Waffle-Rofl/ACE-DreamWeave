using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;

using ACE.Entity.Enum;
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
        private const string IpLimitUsage =
            "Usage:\n"
            + "  iplimit [show]                        - show current IP active-player limit config and state\n"
            + "  iplimit landblock list                 - list mule landblocks\n"
            + "  iplimit landblock add <token>          - add a mule landblock: 016C (any realm) or 01F5@1 (realm 1 only)\n"
            + "  iplimit landblock remove <token>       - remove a mule landblock\n"
            + "  iplimit exempt list                    - list exempt accounts\n"
            + "  iplimit exempt add <account>           - add an exempt account\n"
            + "  iplimit exempt remove <account>        - remove an exempt account";

        [CommandHandler("iplimit", AccessLevel.Admin, CommandHandlerFlag.None, 0,
            "Shows or manages the IP-based active-player limit (mule landblocks and exempt accounts).",
            "[show | landblock <list|add|remove> [token] | exempt <list|add|remove> [account]]")]
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

            var muleLandblocks = IpLimitManager.ListMuleLandblocks();
            sb.AppendLine(muleLandblocks.Count > 0
                ? $"  mule_landblocks: {string.Join(", ", muleLandblocks)}"
                : "  mule_landblocks: (none)");

            var exemptAccounts = IpLimitManager.ListExemptAccounts();
            sb.AppendLine(exemptAccounts.Count > 0
                ? $"  ip_limit_exempt_accounts: {string.Join(", ", exemptAccounts)}"
                : "  ip_limit_exempt_accounts: (none)");

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
                    if (IpLimitManager.TryAddExemptAccount(accountName, out var message))
                    {
                        CommandHandlerHelper.WriteOutputInfo(session, message, ChatMessageType.Broadcast);
                        PropertyManager.ResyncVariables();
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
                    if (IpLimitManager.TryRemoveExemptAccount(accountName, out var message))
                    {
                        CommandHandlerHelper.WriteOutputInfo(session, message, ChatMessageType.Broadcast);
                        PropertyManager.ResyncVariables();
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
        }

        [CommandHandler("iplimitwho", AccessLevel.Sentinel, CommandHandlerFlag.None, 0,
            "Shows current online-player occupancy grouped by client IP, for the IP active-player limit.",
            "[ip address | account name]")]
        public static void HandleIpLimitWho(Session session, params string[] parameters)
        {
            var filter = parameters.Length > 0 ? parameters[0] : null;

            var maxFree = IpLimitManager.ClampCap(PropertyManager.GetLong("ip_limit_max_free").Item);
            var maxConfined = IpLimitManager.ClampCap(PropertyManager.GetLong("ip_limit_max_confined").Item);
            var totalCap = maxFree + maxConfined;

            var groups = new Dictionary<IPAddress, List<IpLimitWhoRow>>();

            foreach (var player in PlayerManager.GetAllOnline())
            {
                var playerSession = player.Session;
                if (playerSession == null)
                    continue;

                var address = playerSession.EndPointC2S?.Address;
                if (address == null)
                    continue;

                if (!string.IsNullOrEmpty(filter))
                {
                    var matchesIp = string.Equals(address.ToString(), filter, StringComparison.OrdinalIgnoreCase);
                    var matchesAccount = !string.IsNullOrEmpty(playerSession.Account) && string.Equals(playerSession.Account, filter, StringComparison.OrdinalIgnoreCase);
                    if (!matchesIp && !matchesAccount)
                        continue;
                }

                if (!groups.TryGetValue(address, out var rows))
                {
                    rows = new List<IpLimitWhoRow>();
                    groups[address] = rows;
                }

                rows.Add(new IpLimitWhoRow
                {
                    Player = player,
                    Exempt = IpLimitManager.IsExempt(playerSession),
                });
            }

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

                    sb.AppendLine($"    {player.Name} (account: {player.Session?.Account ?? "?"}, level {player.Level ?? 0}, landblock {landblockText}) [{string.Join(", ", markers)}]");
                }
            }

            CommandHandlerHelper.WriteOutputInfo(session, sb.ToString(), ChatMessageType.Broadcast);
        }
    }
}
