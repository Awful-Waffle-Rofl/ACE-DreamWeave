using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using ACE.Common;
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
    /// these commands drive the same in-memory <see cref="Fellowship"/> model the client UI uses - every path
    /// reuses the existing Player/Fellowship methods that emit the client GameEvents, so the panel stays in sync.
    /// </summary>
    public static class FellowshipCommands
    {
        [CommandHandler("fship", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 0,
            "Server-managed fellowship controls.",
            "<subcommand>\n" +
            "  create [name]        - create a fellowship (XP sharing always on)\n" +
            "  add [playername]     - invite a player to your fellowship\n" +
            "  join [playername]    - join the OPEN fellowship that a player is in\n" +
            "  addlandblock         - invite every fellowship-less player on your landblock (auto-accept honoured)\n" +
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
                HandleStatus(player);
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

        private const string UsageLine = "Usage: /fship <create|add|join|addlandblock|joinlandblock|noleech|quit|disband|list>";

        /// <summary>
        /// Bare /fship: a light summary of the fellowship the player is currently in, then the usage line.
        /// Players not in a fellowship just get the usage line.
        /// </summary>
        private static void HandleStatus(Player player)
        {
            var fellowship = player.Fellowship;

            if (fellowship == null)
            {
                Msg(player, "You are not in a fellowship.");
                Msg(player, UsageLine);
                return;
            }

            var members = fellowship.GetFellowshipMembers();

            var sb = new StringBuilder();
            sb.AppendLine($"Fellowship: {fellowship.FellowshipName}");
            sb.AppendLine($"  Leader: {LeaderName(fellowship, members)}");
            sb.AppendLine($"  Members: {members.Count}/{Fellowship.MaxFellows}");
            sb.AppendLine($"  Leech prevention: {(fellowship.LeechManagementEnabled ? "ON" : "OFF")}");
            sb.Append(UsageLine);

            Msg(player, sb.ToString());
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

        /// <summary>
        /// WaffleACE: per-caller last-use unix timestamp for /fship addlandblock, gating 'fellowship_addlandblock_cooldown'.
        /// The command invites everyone unfellowed on the caller's landblock in one shot, so it is rate-limited
        /// to stop invite spam. Guarded by <see cref="_addLandblockCooldownLock"/> since commands run on
        /// per-player session threads.
        /// </summary>
        private static readonly Dictionary<uint, double> _addLandblockLastUse = new Dictionary<uint, double>();
        private static readonly object _addLandblockCooldownLock = new object();

        /// <summary>
        /// WaffleACE: whether an online character is staff, and so must never be swept into a mass invite.
        /// A "+Named" admin or moderator standing on a landblock to watch or moderate is not a party member,
        /// and pulling one in silently changes the XP split for everyone actually fighting.
        ///
        /// Two independent tests, because either one alone leaks. <see cref="Player.IsPlussed"/> is the "+Name"
        /// flag itself, but for Sentinel..Admin it is only stamped onto the character when
        /// Server.Accounts.OverrideCharacterPermissions is on (WorldManager.cs:143), so on its own the rule
        /// would hang on a config toggle; the session access level is read directly and does not. Advocates are
        /// deliberately NOT excluded - an advocate is a player with a support flag, not staff, the same reading
        /// <see cref="WorldEvents.WorldEventAudienceSampler.CountsTowardAudience"/> takes. A character with no
        /// session falls back to the persisted flag alone, so an unknown access level never on its own makes
        /// someone staff: the exclusion is for known staff, never for merely unknown.
        /// </summary>
        private static bool IsStaff(Player player)
        {
            return player.IsPlussed || (player.Session != null && player.Session.AccessLevel >= AccessLevel.Sentinel);
        }

        private static void HandleAddLandblock(Player player)
        {
            var cooldown = PropertyManager.GetLong("fellowship_addlandblock_cooldown").Item;
            var now = Time.GetUnixTime();

            lock (_addLandblockCooldownLock)
            {
                if (_addLandblockLastUse.TryGetValue(player.Guid.Full, out var lastUse))
                {
                    var remaining = lastUse + cooldown - now;

                    if (remaining > 0)
                    {
                        Msg(player, $"You can use /fship addlandblock again in {(int)Math.Ceiling(remaining)} second(s).");
                        return;
                    }
                }

                _addLandblockLastUse[player.Guid.Full] = now;
            }

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
                    && !IsStaff(p)
                    && !p.GetCharacterOption(CharacterOption.IgnoreFellowshipRequests)
                    && !p.MuleBlocked(MuleAction.JoinFellowship, notify: false)
                    && fellowship.GetLeechLockoutRemaining(p) <= 0)
                .ToList();

            if (candidates.Count == 0)
            {
                Msg(player, "No unfellowed players found on your landblock.");
                return;
            }

            var invited = 0;
            var auto = 0;

            foreach (var candidate in candidates)
            {
                if (fellowship.GetFellowshipMembers().Count + invited >= Fellowship.MaxFellows)
                    break;

                // AddFellowshipMember already applies leech lockout, busy, and roster-full rules, and either
                // auto-adds (AutomaticallyAcceptFellowshipRequests) or sends the standard accept/decline dialog.
                fellowship.AddFellowshipMember(player, candidate);
                invited++;

                if (candidate.Fellowship == fellowship)
                    auto++;
            }

            var pending = invited - auto;

            Msg(player, $"Invited {invited} player(s) to your fellowship ({auto} joined automatically, {pending} asked to confirm).");
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
                Msg(player, $"No open fellowship found on your landblock - created \"{player.Fellowship.FellowshipName}\".");
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
                    sb.AppendLine($"  {x.Fellowship.FellowshipName} - leader {LeaderName(x.Fellowship, x.Members)}, {x.Members.Count}/{Fellowship.MaxFellows} member(s) ({here} here)");
                }
            }

            if (elsewhere.Count > 0)
            {
                sb.AppendLine("-- Elsewhere --");
                foreach (var x in elsewhere.OrderByDescending(x => x.Members.Count))
                    sb.AppendLine($"  {x.Fellowship.FellowshipName} - leader {LeaderName(x.Fellowship, x.Members)}, {x.Members.Count}/{Fellowship.MaxFellows} member(s)");
            }

            Msg(player, sb.ToString().TrimEnd());
        }

        /// <summary>
        /// WaffleACE: the "xp" tell convention players use with vtank to request a fellowship invite. Sending a
        /// tell whose body is exactly "xp" to a fellowshipped player attempts to free-join the sender into the
        /// recipient's fellowship, through the same <see cref="TryDirectJoin"/> guards as /fship join. Called from
        /// <see cref="Network.GameAction.Actions.GameActionTell"/> after the tell itself has already been delivered.
        /// </summary>
        internal static void TryJoinViaXpTell(Player sender, Player recipient)
        {
            if (sender.Fellowship != null)
            {
                Msg(sender, "You are already in a fellowship. Use /fship quit first.");
                return;
            }

            if (sender.IsOlthoiPlayer)
            {
                Msg(sender, "An Olthoi cannot join a fellowship.");
                return;
            }

            var fellowship = recipient.Fellowship;

            if (fellowship == null)
                return;

            if (TryDirectJoin(sender, fellowship))
                Msg(sender, $"You have joined the fellowship \"{fellowship.FellowshipName}\".");
        }

        /// <summary>
        /// Free-join a fellowship directly (no accept/decline prompt), applying the same guards the client
        /// invite path enforces. Returns false and messages the player on failure.
        /// </summary>
        internal static bool TryDirectJoin(Player player, Fellowship fellowship)
        {
            if (fellowship.IsLocked)
            {
                Msg(player, "That fellowship is locked and cannot be joined.");
                return false;
            }

            if (!fellowship.Open)
            {
                Msg(player, "That fellowship is closed. Ask its leader for an invite, or use /fship list to find an open one.");
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
