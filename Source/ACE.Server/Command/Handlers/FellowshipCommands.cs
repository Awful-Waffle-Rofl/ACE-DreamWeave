using System.Collections.Generic;
using System.Linq;
using System.Text;

using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.Command.Handlers
{
    /// <summary>
    /// WaffleACE: server-managed fellowship controls. The in-client fellowship panel is slow and clunky, so
    /// these commands drive the same in-memory <see cref="Fellowship"/> model the client UI uses — every path
    /// reuses the existing Player/Fellowship methods that emit the client GameEvents, so the panel stays in sync.
    /// </summary>
    public static class FellowshipCommands
    {
        [CommandHandler("fship", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 0,
            "Server-managed fellowship controls.",
            "<subcommand>\n" +
            "  create [name]        - create a fellowship (XP sharing always on)\n" +
            "  add [playername]     - invite a player to your fellowship\n" +
            "  join [playername]    - join the fellowship that a player is in\n" +
            "  addlandblock         - add every fellowship-less player on your landblock to your fellowship\n" +
            "  joinlandblock        - join the largest open fellowship on your landblock, or create one\n" +
            "  noleech [on|off]     - (leader) toggle auto-ejection of members not contributing XP\n" +
            "  quit                 - leave your fellowship\n" +
            "  disband              - (leader) disband your fellowship\n" +
            "  list                 - list open fellowships (this landblock first, then server-wide)")]
        public static void HandleFship(Session session, params string[] parameters)
        {
            var player = session.Player;

            if (parameters.Length == 0)
            {
                Msg(session, "Usage: /fship <create|add|join|addlandblock|joinlandblock|noleech|quit|disband|list>. See /help fship.");
                return;
            }

            var sub = parameters[0].ToLowerInvariant();
            var rest = parameters.Skip(1).ToArray();

            switch (sub)
            {
                case "create":     HandleCreate(player, rest); break;
                case "add":        HandleAdd(player, rest); break;
                case "join":       HandleJoin(player, rest); break;
                case "addlandblock": HandleAddLandblock(player); break;
                case "joinlandblock": HandleJoinLandblock(player); break;
                case "noleech":    HandleLeech(player, rest); break;
                case "leech":      HandleLeech(player, rest); break;   // backward-compat alias
                case "quit":       HandleQuit(player); break;
                case "disband":    HandleDisband(player); break;
                case "list":       HandleList(player); break;
                default:
                    Msg(session, $"Unknown /fship subcommand: {sub}");
                    break;
            }
        }

        private static void HandleCreate(Player player, string[] args)
        {
            if (player.Fellowship != null)
            {
                Msg(player, "You are already in a fellowship. Use /fship quit first.");
                return;
            }

            var name = args.Length > 0 ? string.Join(" ", args).Trim() : $"{player.Name}'s Fellowship";

            // XP sharing is always on for command-created fellowships (design decision).
            player.FellowshipCreate(name, true);

            if (player.Fellowship != null)
                Msg(player, $"Created fellowship \"{player.Fellowship.FellowshipName}\".");
        }

        private static void HandleAdd(Player player, string[] args)
        {
            if (args.Length == 0)
            {
                Msg(player, "Usage: /fship add [playername]");
                return;
            }

            if (player.Fellowship == null)
            {
                Msg(player, "You are not in a fellowship. Use /fship create first.");
                return;
            }

            var target = PlayerManager.GetOnlinePlayer(string.Join(" ", args));

            if (target == null)
            {
                Msg(player, "That player is not online.");
                return;
            }

            if (target == player)
            {
                Msg(player, "You cannot add yourself.");
                return;
            }

            // Reuses the standard recruit flow: honors leader/openness, Olthoi, busy and
            // ignore-fellowship-requests rules, and sends the target the accept/decline prompt.
            player.FellowshipRecruit(target);
        }

        private static void HandleJoin(Player player, string[] args)
        {
            if (args.Length == 0)
            {
                Msg(player, "Usage: /fship join [playername]");
                return;
            }

            if (player.Fellowship != null)
            {
                Msg(player, "You are already in a fellowship. Use /fship quit first.");
                return;
            }

            if (player.IsOlthoiPlayer)
            {
                Msg(player, "An Olthoi cannot join a fellowship.");
                return;
            }

            var target = PlayerManager.GetOnlinePlayer(string.Join(" ", args));

            if (target == null)
            {
                Msg(player, "That player is not online.");
                return;
            }

            var fellowship = target.Fellowship;

            if (fellowship == null)
            {
                Msg(player, $"{target.Name} is not in a fellowship.");
                return;
            }

            if (!TryDirectJoin(player, fellowship))
                return;

            Msg(player, $"You have joined the fellowship \"{fellowship.FellowshipName}\".");
        }

        private static void HandleAddLandblock(Player player)
        {
            if (player.CurrentLandblock == null)
            {
                Msg(player, "You are not on a landblock.");
                return;
            }

            // create a default fellowship if the caller doesn't have one yet
            if (player.Fellowship == null)
            {
                player.FellowshipCreate($"{player.Name}'s Fellowship", true);

                if (player.Fellowship == null)
                    return;

                Msg(player, $"Created fellowship \"{player.Fellowship.FellowshipName}\".");
            }

            var fellowship = player.Fellowship;

            if (player.Guid.Full != fellowship.FellowshipLeaderGuid && !fellowship.Open)
            {
                Msg(player, "Only the fellowship leader can mass-add players (or the fellowship must be open).");
                return;
            }

            var candidates = PlayerManager.GetAllOnline()
                .Where(p => p != player
                    && p.CurrentLandblock == player.CurrentLandblock
                    && p.Fellowship == null
                    && !p.IsOlthoiPlayer
                    && !p.GetCharacterOption(CharacterOption.IgnoreFellowshipRequests)
                    && fellowship.GetLeechLockoutRemaining(p) <= 0)
                .ToList();

            if (candidates.Count == 0)
            {
                Msg(player, "No unfellowed players found on your landblock.");
                return;
            }

            var added = 0;
            var full = false;

            foreach (var candidate in candidates)
            {
                if (fellowship.GetFellowshipMembers().Count >= Fellowship.MaxFellows)
                {
                    full = true;
                    break;
                }

                fellowship.AddConfirmedMember(player, candidate, true);
                added++;
            }

            Msg(player, $"Added {added} player(s) to your fellowship.{(full ? " Fellowship is now full; some players were skipped." : "")}");
        }

        private static void HandleJoinLandblock(Player player)
        {
            if (player.Fellowship != null)
            {
                Msg(player, "You are already in a fellowship. Use /fship quit first.");
                return;
            }

            if (player.IsOlthoiPlayer)
            {
                Msg(player, "An Olthoi cannot join a fellowship.");
                return;
            }

            if (player.CurrentLandblock == null)
            {
                Msg(player, "You are not on a landblock.");
                return;
            }

            // distinct open, joinable fellowships represented on this landblock, largest first
            var best = PlayerManager.GetAllOnline()
                .Where(p => p != player && p.CurrentLandblock == player.CurrentLandblock && p.Fellowship != null)
                .Select(p => p.Fellowship)
                .Distinct()
                .Where(f => f.Open && !f.IsLocked && f.GetFellowshipMembers().Count < Fellowship.MaxFellows)
                .OrderByDescending(f => f.GetFellowshipMembers().Count)
                .FirstOrDefault();

            if (best != null)
            {
                if (TryDirectJoin(player, best))
                    Msg(player, $"You have joined the fellowship \"{best.FellowshipName}\".");

                return;
            }

            // none found: create a fresh default fellowship
            player.FellowshipCreate($"{player.Name}'s Fellowship", true);

            if (player.Fellowship != null)
                Msg(player, $"No open fellowship found on your landblock — created \"{player.Fellowship.FellowshipName}\".");
        }

        private static void HandleLeech(Player player, string[] args)
        {
            if (player.Fellowship == null)
            {
                Msg(player, "You are not in a fellowship.");
                return;
            }

            if (player.Guid.Full != player.Fellowship.FellowshipLeaderGuid)
            {
                Msg(player, "Only the fellowship leader can change leech management.");
                return;
            }

            if (args.Length == 0)
            {
                Msg(player, $"Leech prevention is currently {(player.Fellowship.LeechManagementEnabled ? "ON" : "OFF")}. Usage: /fship noleech [on|off]");
                return;
            }

            var arg = args[0].ToLowerInvariant();

            if (arg == "on")
                player.Fellowship.LeechManagementEnabled = true;
            else if (arg == "off")
                player.Fellowship.LeechManagementEnabled = false;
            else
            {
                Msg(player, "Usage: /fship noleech [on|off]");
                return;
            }

            var timeout = PropertyManager.GetLong("fellowship_leech_timeout").Item;
            var lockout = PropertyManager.GetLong("fellowship_leech_rejoin_lockout").Item;

            player.Fellowship.BroadcastToFellow(player.Fellowship.LeechManagementEnabled
                ? $"Leech management enabled: members who do not contribute XP to the fellowship for over {timeout / 60} minute(s) will be removed and locked out for {Fellowship.LockoutMinutes(lockout)} minute(s)."
                : "Leech management disabled.");
        }

        private static void HandleQuit(Player player)
        {
            if (player.Fellowship == null)
            {
                Msg(player, "You are not in a fellowship.");
                return;
            }

            player.FellowshipQuit(false);
            Msg(player, "You have left the fellowship.");
        }

        private static void HandleDisband(Player player)
        {
            if (player.Fellowship == null)
            {
                Msg(player, "You are not in a fellowship.");
                return;
            }

            if (player.Guid.Full != player.Fellowship.FellowshipLeaderGuid)
            {
                Msg(player, "Only the fellowship leader can disband the fellowship.");
                return;
            }

            player.FellowshipQuit(true);
            Msg(player, "You have disbanded the fellowship.");
        }

        private static void HandleList(Player player)
        {
            var all = FellowshipManager.GetAllFellowships();

            var open = all
                .Select(f => new { Fellowship = f, Members = f.GetFellowshipMembers() })
                .Where(x => x.Fellowship.Open && x.Members.Count > 0)
                .ToList();

            if (open.Count == 0)
            {
                Msg(player, "There are no open fellowships on the server.");
                return;
            }

            var landblock = player.CurrentLandblock;

            var local = open.Where(x => landblock != null && x.Members.Values.Any(m => m.CurrentLandblock == landblock)).ToList();
            var elsewhere = open.Except(local).ToList();

            var sb = new StringBuilder();
            sb.AppendLine("Open fellowships:");

            if (local.Count > 0)
            {
                sb.AppendLine("-- On your landblock --");
                foreach (var x in local.OrderByDescending(x => x.Members.Count))
                {
                    var here = x.Members.Values.Count(m => m.CurrentLandblock == landblock);
                    sb.AppendLine($"  {x.Fellowship.FellowshipName} — leader {LeaderName(x.Fellowship, x.Members)}, {x.Members.Count}/{Fellowship.MaxFellows} member(s) ({here} here)");
                }
            }

            if (elsewhere.Count > 0)
            {
                sb.AppendLine("-- Elsewhere --");
                foreach (var x in elsewhere.OrderByDescending(x => x.Members.Count))
                    sb.AppendLine($"  {x.Fellowship.FellowshipName} — leader {LeaderName(x.Fellowship, x.Members)}, {x.Members.Count}/{Fellowship.MaxFellows} member(s)");
            }

            Msg(player, sb.ToString().TrimEnd());
        }

        /// <summary>
        /// Free-join a fellowship directly (no accept/decline prompt), applying the same guards the client
        /// invite path enforces. Returns false and messages the player on failure.
        /// </summary>
        private static bool TryDirectJoin(Player player, Fellowship fellowship)
        {
            if (fellowship.IsLocked)
            {
                Msg(player, "That fellowship is locked and cannot be joined.");
                return false;
            }

            var lockoutRemaining = fellowship.GetLeechLockoutRemaining(player);

            if (lockoutRemaining > 0)
            {
                Msg(player, $"You were removed from that fellowship for inactivity and cannot rejoin for another {Fellowship.LockoutMinutes(lockoutRemaining)} minute(s).");
                return false;
            }

            if (fellowship.GetFellowshipMembers().Count >= Fellowship.MaxFellows)
            {
                Msg(player, "That fellowship is full.");
                return false;
            }

            // AddConfirmedMember is the post-accept add path; inviter must be a live member so
            // player.Fellowship is assigned from it. Use the leader if online, else any live member.
            var members = fellowship.GetFellowshipMembers();
            var inviter = members.TryGetValue(fellowship.FellowshipLeaderGuid, out var leader) ? leader : members.Values.FirstOrDefault();

            if (inviter == null)
            {
                Msg(player, "That fellowship has no available members to join through.");
                return false;
            }

            fellowship.AddConfirmedMember(inviter, player, true);
            return true;
        }

        private static string LeaderName(Fellowship fellowship, Dictionary<uint, Player> members)
        {
            if (members.TryGetValue(fellowship.FellowshipLeaderGuid, out var leader))
                return leader.Name;

            var offline = PlayerManager.FindByGuid(fellowship.FellowshipLeaderGuid);
            return offline?.Name ?? "?";
        }

        private static void Msg(Session session, string text)
        {
            session.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));
        }

        private static void Msg(Player player, string text)
        {
            player.Session.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));
        }
    }
}
