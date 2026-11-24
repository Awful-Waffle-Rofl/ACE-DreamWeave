// `/derethnetwork` is the mod's only command (this fork registers no `dn` or `dng`: DreamWeave's own `/dn` is the Drift Network recall).
// ACE's mod loader registers it (Meta.json: RegisterCommands). The
// retail client sends a command it does not know to the world as
// `Communication_Talk` (`0x0015`) with `/` rewritten to `@`, so `/derethnetwork on` arrives as `@derethnetwork on`.
//
// Every player may use it, and so may the server console. `/derethnetwork admin ...` is the operator's
// (Sentinel and above, and the console), and so is `/derethnetwork verify <token>` (Admin, and the console);
// anything else is the player's own.

using ACE.Entity.Enum;
using ACE.Server.Command;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;

namespace Dereth.Network;

public static class NetCommands
{
    private const string Usage =
        "on | off | friends [add|remove <Name@world>] | global <text> | squelch|unsquelch <world> | channel ... | link | unlink | help";

    [CommandHandler("derethnetwork", AccessLevel.Player, CommandHandlerFlag.None,
        "The Dereth Network: tells, friends and channels with other worlds", Usage)]
    public static void HandleDn(Session session, params string[] parameters)
    {
        parameters ??= [];
        if (parameters.Length > 0 && parameters[0].Equals("admin", StringComparison.OrdinalIgnoreCase)
            && IsSentinel(session))
        {
            Admin(session, parameters[1..]);
            return;
        }
        if (parameters.Length > 0 && parameters[0].Equals("verify", StringComparison.OrdinalIgnoreCase)
            && IsAdmin(session))
        {
            Verify(session, parameters[1..]);
            return;
        }
        if (session is null)
        {
            Output(null, NetworkWorld.AdminHelp("derethnetwork"));
            return;
        }
        // A player's own words need the player in the world.
        if (session.Player is not { } player || player.CurrentLandblock is null)
            return;
        var r = Mod.Current;
        if (r is null)
        {
            Output(session, NetworkWorld.NotOnNetworkLine);
            return;
        }
        try
        {
            r.World.Dn(player.Guid.Full, parameters);
        }
        catch (Exception e)
        {
            r.Fault("/derethnetwork", e);
        }
    }

    /// <summary>
    /// Whether the command's user is Sentinel or above (the console always is), as ACE's command
    /// table decides it for a row of that level.
    /// </summary>
    private static bool IsSentinel(Session? session)
    {
        if (session is null)
            return true;
        return session.Player is { } p && (p.IsSentinel || p.IsEnvoy || p.IsArch || p.IsAdmin);
    }

    /// <summary>Whether the command's user is an Admin (the console always is).</summary>
    private static bool IsAdmin(Session? session) => session is null || session.Player is { IsAdmin: true };

    /// <summary>`/derethnetwork verify &lt;token&gt;`: the answer comes later, on the world thread.</summary>
    private static void Verify(Session? session, string[] parameters)
    {
        var r = Mod.Current;
        if (r is null)
        {
            Output(session, NetworkWorld.NotOnNetworkLine);
            return;
        }
        try
        {
            var player = session?.Player;
            Output(session, r.World.Verify(player?.Guid.Full, player?.Name ?? "the console", parameters));
        }
        catch (Exception e)
        {
            r.Fault("/derethnetwork verify", e);
        }
    }

    /// <summary>
    /// `/derethnetwork admin ...`: the operator's blocks, where the world carries the Global room (the hub's
    /// answer comes later, on the world thread), and the network's state. With the network off, a
    /// word that would change something says so, with the error that kept it off when one was
    /// logged, and any other word shows the operator's help.
    /// </summary>
    private static void Admin(Session? session, string[] parameters)
    {
        var r = Mod.Current;
        string text;
        if (r is null)
        {
            text = parameters.Length > 0 && parameters[0] is "block" or "unblock" or "global"
                ? NetworkWorld.NotOnNetworkLine + (Mod.OffReason is { } why ? $"\nThe error that kept it off: {why}" : "")
                : NetworkWorld.AdminHelp(session?.Player is null ? "derethnetwork" : "@derethnetwork");
        }
        else
        {
            try
            {
                text = r.World.Admin(parameters, session?.Player?.Guid.Full);
            }
            catch (Exception e)
            {
                r.Fault("/derethnetwork admin", e);
                return;
            }
        }
        Output(session, text);
    }

    /// <summary>A command's answer: to the player who typed it, or to the log for the server console.</summary>
    internal static void Output(Session? session, string text)
    {
        if (session?.Player is not null)
            session.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));
        else
            Mod.Log(LogLevel.Info, text);
    }
}
