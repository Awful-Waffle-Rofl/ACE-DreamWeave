using System;

using ACE.Entity.Enum;
using ACE.Server.Managers;
using ACE.Server.Network;

namespace ACE.Server.Command.Handlers
{
    /// <summary>
    /// /netcapture: admin diagnostic that records the exact bytes the server sends to one flagged
    /// character. State is process memory only (see NetCapture); a restart clears it.
    /// </summary>
    public static class NetCaptureCommands
    {
        [CommandHandler("netcapture", AccessLevel.Admin, CommandHandlerFlag.None, 1,
            "Capture the outbound network messages sent to one character, for client-crash diagnosis.",
            "on <character name> | off <character name> | dump <character name> | status\n" +
            "Flags live in memory only and are cleared by every server restart.\n" +
            "The ring is dumped to a .jsonl file next to the server log when the flagged character's session terminates, or on demand with dump.")]
        public static void HandleNetCapture(Session session, params string[] parameters)
        {
            var sub = parameters[0].ToLowerInvariant();

            if (sub == "status")
            {
                var rows = NetCapture.Status();
                if (rows.Count == 0)
                {
                    CommandHandlerHelper.WriteOutputInfo(session, "netcapture: no characters flagged.");
                    return;
                }
                foreach (var r in rows)
                    CommandHandlerHelper.WriteOutputInfo(session, $"netcapture: {r.Name} (0x{r.Guid:X8}) entries={r.Count} bytes={r.Bytes}");
                return;
            }

            if ((sub != "on" && sub != "off" && sub != "dump") || parameters.Length < 2)
            {
                CommandHandlerHelper.WriteOutputInfo(session, "Usage: /netcapture on|off|dump <character name> | status");
                return;
            }

            var name = string.Join(" ", parameters, 1, parameters.Length - 1).Trim();
            var player = PlayerManager.FindByName(name, out var isOnline);
            if (player == null)
            {
                CommandHandlerHelper.WriteOutputInfo(session, $"netcapture: no character named '{name}'.");
                return;
            }

            var guid = player.Guid.Full;

            switch (sub)
            {
                case "on":
                    if (NetCapture.Enable(guid, player.Name))
                        CommandHandlerHelper.WriteOutputInfo(session, $"netcapture: capturing {player.Name} ({(isOnline ? "online" : "offline")}). Off after any server restart.");
                    else
                        CommandHandlerHelper.WriteOutputInfo(session, $"netcapture: {player.Name} is already flagged.");
                    break;

                case "off":
                    if (NetCapture.Disable(guid))
                        CommandHandlerHelper.WriteOutputInfo(session, $"netcapture: stopped capturing {player.Name}, ring discarded.");
                    else
                        CommandHandlerHelper.WriteOutputInfo(session, $"netcapture: {player.Name} is not flagged.");
                    break;

                case "dump":
                    try
                    {
                        var endpoint = (player as ACE.Server.WorldObjects.Player)?.Session?.EndPointC2S?.ToString();
                        // The write runs on the thread pool; the reply names the intended path.
                        var path = NetCapture.DumpNowAsync(guid, endpoint, "manual", out _);
                        if (path == null)
                            CommandHandlerHelper.WriteOutputInfo(session, $"netcapture: {player.Name} is not flagged.");
                        else
                            CommandHandlerHelper.WriteOutputInfo(session, $"netcapture: writing {path} (see the log for the final path)");
                    }
                    catch (Exception ex)
                    {
                        CommandHandlerHelper.WriteOutputInfo(session, "netcapture: dump failed: " + ex.Message);
                    }
                    break;
            }
        }
    }
}
