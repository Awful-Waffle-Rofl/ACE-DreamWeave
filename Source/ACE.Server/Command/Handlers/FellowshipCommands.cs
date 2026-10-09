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
            "  join <playername> [password] - join the fellowship that a player is in (password required if Closed and passworded)\n" +
            "  addlandblock         - invite every fellowship-less player on your landblock (auto-accept honoured)\n" +
            "  joinlandblock [password] - join the largest open (or matching passworded) fellowship on your landblock, or create one\n" +
            "  password [<pw>|off]  - (leader) set/clear the join password; bare shows whether one is set\n" +
            "  noleech [on|off]     - (leader) toggle auto-ejection of members not contributing XP\n" +
            "  quit                 - leave your fellowship\n" +
            "  disband              - (leader) disband your fellowship\n" +
            "  list                 - list open (or passworded) fellowships (this landblock first, then server-wide)")]
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

            // Battlegrounds (Docs/Pvp/BATTLEGROUNDS.md "Team fellowships"): every subcommand that changes a fellowship is
            // refused while the player's team fellowship is fixed. Bare /fship and list only read, so they still answer.
            if (IsMutatingSubcommand(sub) && player.PvpTeamFellowshipBlocked())
                return;

            switch (sub)
            {
                case "create":     HandleCreate(player, rest); break;
                case "add":        HandleAdd(player, rest); break;
                case "join":       HandleJoin(player, rest); break;
                case "addlandblock": HandleAddLandblock(player); break;
                case "joinlandblock": HandleJoinLandblock(player, rest); break;
                case "password":  HandlePassword(player, rest); break;
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

        /// <summary>The /fship subcommands that change a fellowship (everything but bare /fship and list).</summary>
        internal static bool IsMutatingSubcommand(string sub)
        {
            switch (sub)
            {
                case "create":
                case "add":
                case "join":
                case "addlandblock":
                case "joinlandblock":
                case "password":
                case "noleech":
                case "leech":
                case "quit":
                case "disband":
                    return true;

                default:
                    return false;
            }
        }

        private const string UsageLine = "Usage: /fship <create|add|join|addlandblock|joinlandblock|password|noleech|quit|disband|list>";

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

            if (fellowship.HasPassword)
                sb.AppendLine($"  Join password: {fellowship.Password}");

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
                Msg(player, "Usage: /fship join <playername> [password]");
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

            // player names can contain spaces, so try the whole argument list as a name first; only if that
            // fails to resolve an online player, and there are 2+ tokens, fall back to treating the last
            // token as a password and the rest as the name.
            var target = PlayerManager.GetOnlinePlayer(string.Join(" ", args));
            string password = null;

            if (target == null && args.Length >= 2)
            {
                password = args[^1];
                target = PlayerManager.GetOnlinePlayer(string.Join(" ", args.Take(args.Length - 1)));
            }

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

            if (!TryDirectJoin(player, fellowship, password, FellowshipJoinEntryPath.Join, target.Name))
                return;

            Msg(player, $"You have joined the fellowship \"{fellowship.FellowshipName}\".");
        }

        /// <summary>
        /// WaffleACE: leader-only join-password control. Bare shows any current member whether one is set
        /// (and, per the design, the password itself - see Fellowship.Password's doc comment). "off" clears
        /// it; anything else attempts to set it.
        /// </summary>
        private static void HandlePassword(Player player, string[] args)
        {
            if (player.Fellowship == null)
            {
                Msg(player, "You are not in a fellowship.");
                return;
            }

            var fellowship = player.Fellowship;

            if (args.Length == 0)
            {
                Msg(player, fellowship.HasPassword
                    ? $"This fellowship's join password is: {fellowship.Password}"
                    : "This fellowship has no join password set.");
                return;
            }

            if (player.Guid.Full != fellowship.FellowshipLeaderGuid)
            {
                Msg(player, "Only the fellowship leader can change the join password.");
                return;
            }

            if (args.Length == 1 && string.Equals(args[0], "off", StringComparison.OrdinalIgnoreCase))
            {
                if (!fellowship.HasPassword)
                {
                    Msg(player, "This fellowship has no join password set.");
                    return;
                }

                fellowship.ClearPassword();
                Msg(player, "Join password removed.");
                fellowship.BroadcastToFellow("The fellowship join password was removed.");
                return;
            }

            // validate FIRST - a rejected value (spaces, >32 chars) must leave an Open fellowship untouched,
            // not Closed with no password. Only a value that will actually be accepted may close the
            // fellowship. Then close before SetPassword, so the fellowship is never observably
            // Open-and-passworded even for the instant between the two calls; UpdateOpenness(true) elsewhere
            // is what clears a password again on open, and this order guarantees the reverse can never race it.
            var raw = string.Join(" ", args);

            if (!Fellowship.TryNormalizePassword(raw, out _, out var validationError))
            {
                Msg(player, validationError);
                return;
            }

            if (fellowship.Open)
                fellowship.UpdateOpenness(false);

            var error = fellowship.SetPassword(raw);

            if (error != null)
            {
                Msg(player, error);
                return;
            }

            Msg(player, "Join password set. Do not reuse your account password.");
            fellowship.BroadcastToFellow("The fellowship leader set a join password.");
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

        /// <summary>
        /// WaffleACE: one /fship joinlandblock candidate fellowship's relevant facts, precomputed by the
        /// caller (Fellowship.CheckPassword/IsPasswordGuessLockedOut both read PropertyManager, so the
        /// selection logic itself stays a pure static). 'Id' is an index into the caller's own candidate
        /// list, not a guid. 'GuessLockedOut'/'PasswordMatches' are only meaningful when 'Open' is false.
        /// </summary>
        internal readonly record struct JoinLandblockCandidate(int Id, bool Open, bool GuessLockedOut, bool PasswordMatches, int MemberCount);

        /// <summary>
        /// WaffleACE: pure selection/guess-recording rule behind /fship joinlandblock.
        ///
        /// When no password was supplied, or there are no Closed passworded candidates at all, behavior is
        /// unchanged from before passwords existed: pick the largest Open unlocked candidate, or null (the
        /// caller then creates a fresh fellowship).
        ///
        /// When a password was supplied and at least one Closed passworded candidate exists: locked-out
        /// candidates are skipped entirely (never evaluated, never re-recorded). Among the rest, if any Open
        /// candidate or password-matching candidate exists, the largest one wins. If none match at all, no
        /// fellowship is created - instead every EVALUATED (non-skipped) passworded candidate gets a failed
        /// attempt recorded, since the player did direct a guess at each of them.
        /// </summary>
        internal static (int? SelectedId, List<int> RecordFailureIds, bool NoMatch) SelectJoinLandblockCandidate(
            IReadOnlyList<JoinLandblockCandidate> candidates, bool passwordSupplied)
        {
            var passwordCandidates = candidates.Where(c => !c.Open).ToList();

            if (!passwordSupplied || passwordCandidates.Count == 0)
            {
                var openBest = candidates.Where(c => c.Open)
                    .OrderByDescending(c => c.MemberCount)
                    .Select(c => (int?)c.Id)
                    .FirstOrDefault();

                return (openBest, new List<int>(), false);
            }

            var evaluated = passwordCandidates.Where(c => !c.GuessLockedOut).ToList();

            var eligibleIds = new HashSet<int>(evaluated.Where(c => c.PasswordMatches).Select(c => c.Id));

            foreach (var open in candidates.Where(c => c.Open))
                eligibleIds.Add(open.Id);

            var best = candidates.Where(c => eligibleIds.Contains(c.Id))
                .OrderByDescending(c => c.MemberCount)
                .ToList();

            if (best.Count > 0)
                return (best[0].Id, new List<int>(), false);

            return (null, evaluated.Select(c => c.Id).ToList(), true);
        }

        private static void HandleJoinLandblock(Player player, string[] args)
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

            var password = args.Length > 0 ? string.Join(" ", args) : null;
            var passwordSupplied = !string.IsNullOrEmpty(password);

            // distinct unlocked, non-full fellowships represented on this landblock. Closed fellowships with
            // no password are never self-service candidates here, same as before passwords existed.
            var landblockFellowships = PlayerManager.GetAllOnline()
                .Where(p => p != player && p.CurrentLandblock == player.CurrentLandblock && p.Fellowship != null)
                .Select(p => p.Fellowship)
                .Distinct()
                .Where(f => !f.IsLocked && f.GetFellowshipMembers().Count < Fellowship.MaxFellows
                    && (f.Open || f.HasPassword))
                .ToList();

            var byId = new Dictionary<int, Fellowship>();
            var candidateInfos = new List<JoinLandblockCandidate>();

            for (var i = 0; i < landblockFellowships.Count; i++)
            {
                var f = landblockFellowships[i];
                byId[i] = f;

                if (f.Open)
                {
                    candidateInfos.Add(new JoinLandblockCandidate(i, true, false, false, f.GetFellowshipMembers().Count));
                    continue;
                }

                // Closed + passworded: only evaluate a guess against it when the player actually supplied
                // one - a mismatch is only recorded once the player has directed a guess at this fellowship.
                var guessLockedOut = passwordSupplied && f.IsPasswordGuessLockedOut(player.Guid.Full, out _);
                var matches = passwordSupplied && !guessLockedOut && f.CheckPassword(password);

                candidateInfos.Add(new JoinLandblockCandidate(i, false, guessLockedOut, matches, f.GetFellowshipMembers().Count));
            }

            var selection = SelectJoinLandblockCandidate(candidateInfos, passwordSupplied);

            foreach (var failId in selection.RecordFailureIds)
                byId[failId].RecordFailedPasswordAttempt(player.Guid.Full);

            if (selection.SelectedId.HasValue)
            {
                var best = byId[selection.SelectedId.Value];

                if (TryDirectJoin(player, best, password))
                    Msg(player, $"You have joined the fellowship \"{best.FellowshipName}\".");

                return;
            }

            if (selection.NoMatch)
            {
                Msg(player, "No fellowship on your landblock matches that password.");
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

            // joinable fellowships: Open ones as before, plus Closed unlocked ones that carry a join
            // password (tagged below) - a Closed fellowship with no password stays invite-only and hidden.
            var listable = all
                .Select(f => new { Fellowship = f, Members = f.GetFellowshipMembers() })
                .Where(x => x.Members.Count > 0 && !x.Fellowship.IsLocked && (x.Fellowship.Open || x.Fellowship.HasPassword))
                .ToList();

            if (listable.Count == 0)
            {
                Msg(player, "There are no joinable fellowships on the server.");
                return;
            }

            var landblock = player.CurrentLandblock;

            var local = listable.Where(x => landblock != null && x.Members.Values.Any(m => m.CurrentLandblock == landblock)).ToList();
            var elsewhere = listable.Except(local).ToList();

            var sb = new StringBuilder();
            sb.AppendLine("Joinable fellowships:");

            if (local.Count > 0)
            {
                sb.AppendLine("-- On your landblock --");
                foreach (var x in local.OrderByDescending(x => x.Members.Count))
                {
                    var here = x.Members.Values.Count(m => m.CurrentLandblock == landblock);
                    var tag = x.Fellowship.Open ? "" : " [password]";
                    sb.AppendLine($"  {x.Fellowship.FellowshipName}{tag} - leader {LeaderName(x.Fellowship, x.Members)}, {x.Members.Count}/{Fellowship.MaxFellows} member(s) ({here} here)");
                }
            }

            if (elsewhere.Count > 0)
            {
                sb.AppendLine("-- Elsewhere --");
                foreach (var x in elsewhere.OrderByDescending(x => x.Members.Count))
                {
                    var tag = x.Fellowship.Open ? "" : " [password]";
                    sb.AppendLine($"  {x.Fellowship.FellowshipName}{tag} - leader {LeaderName(x.Fellowship, x.Members)}, {x.Members.Count}/{Fellowship.MaxFellows} member(s)");
                }
            }

            Msg(player, sb.ToString().TrimEnd());
        }

        /// <summary>
        /// WaffleACE: which self-service entry point is attempting a join, so <see cref="TryDirectJoin"/> can
        /// build a password-required hint pointing back at that exact path.
        /// </summary>
        internal enum FellowshipJoinEntryPath
        {
            Join,
            XpTell
        }

        /// <summary>
        /// WaffleACE: the "requires a password" hint text for a given entry path, naming the exact retry
        /// command. 'targetName' is the player name the joiner actually typed/resolved (for Join) or the tell
        /// recipient's name (for XpTell) - never a literal placeholder. Pure so it is unit testable.
        /// </summary>
        internal static string BuildPasswordRequiredHint(FellowshipJoinEntryPath entryPath, string targetName)
        {
            return entryPath switch
            {
                FellowshipJoinEntryPath.XpTell => $"That fellowship requires a password. Use /tell {targetName} xp <password>",
                _ => $"That fellowship requires a password. Use /fship join {targetName} <password>",
            };
        }

        /// <summary>
        /// WaffleACE: parses a tell body against the "xp" / "xp &lt;password&gt;" convention vtank uses to
        /// request a fellowship invite - the first token must be "xp" (case-insensitive) and at most one more
        /// token may follow. Pure so it is unit testable without a live tell. Returns false (and leaves
        /// 'password' null) for anything else, including "xpp" or "xp a b".
        /// </summary>
        internal static bool TryParseXpTellBody(string message, out string password)
        {
            password = null;

            var trimmed = message?.Trim();

            if (string.IsNullOrEmpty(trimmed))
                return false;

            var tokens = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (tokens.Length == 0 || tokens.Length > 2)
                return false;

            if (!string.Equals(tokens[0], "xp", StringComparison.OrdinalIgnoreCase))
                return false;

            if (tokens.Length == 2)
                password = tokens[1];

            return true;
        }

        /// <summary>
        /// WaffleACE: the "xp" tell convention players use with vtank to request a fellowship invite. Sending a
        /// tell whose body matches <see cref="TryParseXpTellBody"/> to a fellowshipped player attempts to
        /// free-join the sender into the recipient's fellowship, through the same <see cref="TryDirectJoin"/>
        /// guards as /fship join. Called from <see cref="Network.GameAction.Actions.GameActionTell"/> after the
        /// tell itself has already been delivered.
        /// </summary>
        internal static void TryJoinViaXpTell(Player sender, Player recipient, string password)
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

            if (TryDirectJoin(sender, fellowship, password, FellowshipJoinEntryPath.XpTell, recipient.Name))
                Msg(sender, $"You have joined the fellowship \"{fellowship.FellowshipName}\".");
        }

        /// <summary>
        /// Free-join a fellowship directly (no accept/decline prompt), applying the same guards the client
        /// invite path enforces. Returns false and messages the player on failure. 'entryPath'/'targetName'
        /// are only used to build the password-required hint text when one is needed.
        /// </summary>
        internal static bool TryDirectJoin(Player player, Fellowship fellowship, string password = null,
            FellowshipJoinEntryPath entryPath = FellowshipJoinEntryPath.Join, string targetName = null)
        {
            if (player.PvpTeamFellowshipBlocked())
                return false;

            if (fellowship.IsLocked)
            {
                Msg(player, "That fellowship is locked and cannot be joined.");
                return false;
            }

            if (!fellowship.Open)
            {
                if (!fellowship.HasPassword)
                {
                    Msg(player, "That fellowship is closed. Ask its leader for an invite, or use /fship list to find an open one.");
                    return false;
                }

                if (string.IsNullOrEmpty(password))
                {
                    Msg(player, BuildPasswordRequiredHint(entryPath, targetName ?? fellowship.FellowshipName));
                    return false;
                }

                if (fellowship.IsPasswordGuessLockedOut(player.Guid.Full, out var remaining))
                {
                    Msg(player, $"Too many wrong passwords for that fellowship. Try again in {Fellowship.LockoutMinutes(remaining)} minute(s).");
                    return false;
                }

                if (!fellowship.CheckPassword(password))
                {
                    fellowship.RecordFailedPasswordAttempt(player.Guid.Full);
                    Msg(player, "That password is incorrect.");
                    return false;
                }

                fellowship.ClearPasswordAttempts(player.Guid.Full);
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
