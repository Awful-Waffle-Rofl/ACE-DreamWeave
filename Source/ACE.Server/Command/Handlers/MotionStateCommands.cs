using System;

using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.WorldObjects;

namespace ACE.Server.Command.Handlers
{
    /// <summary>
    /// /motionstate: admin diagnostic for the motion-stall watchdog (see WorldObjects.MotionStallWatchdog).
    /// Shows one online character's server-side animation queue and pending move-to/turn/use, and with
    /// 'clear' drops the queue the HandleExitWorld way regardless of the motion_stall_autoclear tunable.
    ///
    /// Threading: the physics state may only be read or changed on the world thread. An in-game command already
    /// runs there (NetworkManager.InboundMessageQueue.RunActions in the world loop), so it answers synchronously.
    /// A console command (null session) does not, so its work is queued on the target player's own action queue
    /// and answers a moment later. For the same reason the web admin panel cannot run it: its web bucket invokes
    /// handlers on a dedicated worker with a null session, and output written after the handler returns is past
    /// the sealed WebCommandContext, so the row in command-classification.tsv is an in_game_only override.
    /// </summary>
    public static class MotionStateCommands
    {
        [CommandHandler("motionstate", AccessLevel.Admin, CommandHandlerFlag.None, 1,
            "Show (or clear) a character's server-side pending animation queue, for stuck-use diagnosis.",
            "<character name> [clear]\n" +
            "Prints IsAnimating, the pending motions, time since the queue last changed, Contact/OnWalkable, any pending move-to/turn/use and FastTick.\n" +
            "clear drops the pending motions (server state only, nothing is sent to the client) and logs a [MOTION_STALL] line with trigger=admin.")]
        public static void HandleMotionState(Session session, params string[] parameters)
        {
            if (!TryResolve(parameters, out var found, out var isOnline, out var clear, out var name))
            {
                CommandHandlerHelper.WriteOutputInfo(session, name.Length == 0
                    ? "Usage: /motionstate <character name> [clear]"
                    : $"motionstate: no character named '{name}'.");
                return;
            }

            var player = isOnline ? PlayerManager.GetOnlinePlayer(found.Guid) : null;
            if (player == null)
            {
                CommandHandlerHelper.WriteOutputInfo(session, $"motionstate: {found.Name} is not online.");
                return;
            }

            if (session != null)
            {
                // in-game: already on the world thread
                Run(session, player, clear);
                return;
            }

            // console: hop onto the world thread through the player's own action queue
            player.EnqueueAction(new ActionEventDelegate(() => Run(session, player, clear)));
        }

        /// <summary>
        /// Name parsing: the whole argument string is tried as a name first, so a character literally named
        /// "Mr Clear" is found as such. Only when that finds no one, and there is more than one word, is a trailing
        /// "clear" taken as the flag. On failure, name is the name that was last tried ("" when there was none).
        /// </summary>
        internal static bool TryResolve(string[] parameters, out IPlayer found, out bool isOnline, out bool clear, out string name)
        {
            return TryResolve<IPlayer>(parameters, n =>
                {
                    var p = PlayerManager.FindByName(n, out var online);
                    return (p, online);
                },
                out found, out isOnline, out clear, out name);
        }

        /// <summary>The parsing above against an injectable lookup, for tests.</summary>
        internal static bool TryResolve<T>(string[] parameters, Func<string, (T player, bool online)> lookup,
            out T found, out bool isOnline, out bool clear, out string name) where T : class
        {
            found = null;
            isOnline = false;
            clear = false;
            name = parameters == null ? string.Empty : string.Join(" ", parameters).Trim();

            if (name.Length == 0)
                return false;

            var whole = lookup(name);
            if (whole.player != null)
            {
                found = whole.player;
                isOnline = whole.online;
                return true;
            }

            if (parameters.Length >= 2 && parameters[parameters.Length - 1].Equals("clear", StringComparison.OrdinalIgnoreCase))
            {
                var shorter = string.Join(" ", parameters, 0, parameters.Length - 1).Trim();
                if (shorter.Length > 0)
                {
                    var hit = lookup(shorter);
                    if (hit.player != null)
                    {
                        found = hit.player;
                        isOnline = hit.online;
                        clear = true;
                        name = shorter;
                        return true;
                    }
                }
            }

            return false;
        }

        private static void Run(Session session, Player player, bool clear)
        {
            try
            {
                CommandHandlerHelper.WriteOutputInfo(session, player.DescribeMotionState());

                if (clear)
                {
                    var line = player.ClearMotionStall("admin");
                    CommandHandlerHelper.WriteOutputInfo(session, line ?? $"motionstate: {player.Name} has no motion interpreter; nothing cleared.");
                }
            }
            catch (Exception ex)
            {
                CommandHandlerHelper.WriteOutputInfo(session, "motionstate: failed: " + ex.Message);
            }
        }
    }
}
