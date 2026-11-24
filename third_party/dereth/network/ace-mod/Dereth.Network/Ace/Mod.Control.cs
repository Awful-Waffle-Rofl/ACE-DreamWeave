// Local modification (not upstream's): the server's own settings switch the network live, read
// from ACE's PropertyManager on the world thread (never at Initialize: ACE loads mods before it
// loads its settings). Each is a bool, true unless the server says otherwise. The first read that
// fails counts as true; a later read that fails keeps the last value, so a passing fault neither
// lifts a veto nor brings a switched-off network back.
//
//   dereth_network_enabled         false: off the hub, every hook passing to ACE; true: back on
//   dereth_network_traffic_log     the traffic log (Core/TrafficLog.cs) on or off
//   dereth_network_room_mirroring  false: the operator's veto on mirroring this world's own rooms
//                                  into Discord, both ways (Core/NetworkWorld.Fork.cs)
//
// The switching itself is Core/LiveSwitch.cs's LiveControl, which the harness drives: switched off,
// the runtime is parked rather than dropped; switched on, the same world half, store and host come
// back on a fresh link once the old link's task has ended, after the logins and logouts missed
// meanwhile are reconciled. The names guard stays on throughout: a local name holding `@` could
// pass for a remote character whenever the network comes back.

using ACE.Server.Managers;
using log4net;
using log4net.Appender;
using log4net.Core;

namespace Dereth.Network;

public sealed partial class Mod
{
    internal const string EnabledKey = "dereth_network_enabled";
    internal const string TrafficLogKey = "dereth_network_traffic_log";
    internal const string RoomMirroringKey = "dereth_network_room_mirroring";

    private static LiveControl<Runtime>? control;
    private static TrafficLog? traffic;
    private static volatile bool namesGuarded;
    private static volatile bool stopped;

    // The world thread's own: the settings as last read.
    private static bool enabledSetting = true;
    private static bool trafficSetting = true;
    private static bool mirroringSetting = true;
    private static bool? vetoShown;

    /// <summary>The traffic log, once the network has started; null before.</summary>
    internal static TrafficLog? Traffic => Volatile.Read(ref traffic);

    /// <summary>Names holding `@` are refused: set once their patch is on, whether or not the network is switched on.</summary>
    internal static bool NamesGuarded => namesGuarded;

    /// <summary>The mod is stopping or stopped: no link starts and nothing is switched.</summary>
    internal static bool Stopped => stopped;

    /// <summary>
    /// At Start, once the link's address and key are known: makes the traffic log (off until the
    /// first poll reads its setting) and the switch, which keeps the address and key for the links
    /// it makes after a switch off and on.
    /// </summary>
    private static TrafficLog StartControl(Uri link, string key, string folder)
    {
        var log = new TrafficLog(TrafficDirectory(folder), key, (l, m) => Log(l, m));
        Volatile.Write(ref traffic, log);
        Volatile.Write(ref control, new LiveControl<Runtime>(new LiveSwitch(Monotonic),
            () => Current,
            r => Volatile.Write(ref current, r),
            r => r.World,
            r => r.Link,
            p => new Runtime
            {
                World = p.World,
                Link = new HubLink(link, key, (l, m) => Log(l, m), tap: Traffic),
                Writer = p.Writer,
                Host = p.Host,
            }));
        return log;
    }

    /// <summary>
    /// For Stop, before anything else: the stop shows in the traffic log, nothing is switched or
    /// started again (a switch in progress is waited out), and the runtime to save is handed back,
    /// the parked one if the network is switched off.
    /// </summary>
    private static Runtime? TakeParked()
    {
        stopped = true;
        Traffic?.Meta("mod.stop", 0);
        var c = Interlocked.Exchange(ref control, null);
        return c?.Stop();
    }

    /// <summary>At Dispose: the traffic log writes what waits (a second at most) and closes.</summary>
    private static void StopControl()
    {
        namesGuarded = false;
        try
        {
            Volatile.Read(ref traffic)?.Stop(TimeSpan.FromSeconds(1));
        }
        catch (Exception e)
        {
            Log(LogLevel.Warn, $"closing the traffic log: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>
    /// On the world thread, at the top of every tick and before the network's null check: reads the
    /// settings at most once a second and switches the network to match. Never throws.
    /// </summary>
    internal static void Poll()
    {
        var c = Volatile.Read(ref control);
        if (c is null || stopped)
            return;
        try
        {
            if (c.Switch.Due())
                PollNow(c);
        }
        catch (Exception e)
        {
            Fault("the network's server settings", e, LogLevel.Warn);
        }
    }

    private static void PollNow(LiveControl<Runtime> c)
    {
        if (stopped)
            return;
        enabledSetting = Read(EnabledKey, enabledSetting);
        trafficSetting = Read(TrafficLogKey, trafficSetting);
        mirroringSetting = Read(RoomMirroringKey, mirroringSetting);

        var log = Traffic;
        if (log is not null && log.Enabled != trafficSetting)
        {
            if (!trafficSetting)
                log.Meta("traffic.off", 0, TrafficLogKey + " is false");
            log.Enabled = trafficSetting;
            if (trafficSetting)
                log.Meta("traffic.on", 0, TrafficLogKey + " is true");
            Log(LogLevel.Info, trafficSetting ? $"the traffic log is on, writing to {log.Directory}" : "the traffic log is off");
        }

        if (c.World is { } world)
        {
            world.Config.RoomMirroringVetoed = !mirroringSetting;
            if (vetoShown != !mirroringSetting)
            {
                // Said at the first read only when it is vetoed, then at every change.
                if (vetoShown is not null || !mirroringSetting)
                    Log(LogLevel.Info, mirroringSetting
                        ? "this world's own rooms are mirrored into Discord as the hub says again"
                        : $"{RoomMirroringKey} is false: no line goes between this world's own rooms and Discord, either way (the Global room is untouched)");
                vetoShown = !mirroringSetting;
            }
        }

        switch (c.Apply(enabledSetting))
        {
            case LiveSwitch.Step.Off:
                Volatile.Write(ref offReason, $"the server setting {EnabledKey} is false");
                Traffic?.Meta("switch.off", 0, EnabledKey + " is false");
                Log(LogLevel.Info, $"switched off by the server setting {EnabledKey}: off the hub, and everything is ACE's until it is true again");
                break;
            case LiveSwitch.Step.On:
                Volatile.Write(ref offReason, null);
                Traffic?.Meta("switch.on", 0, EnabledKey + " is true");
                Log(LogLevel.Info, $"switched on by the server setting {EnabledKey}: linking to the hub again");
                break;
        }
    }

    /// <summary>A setting as the server has it now; the last value when it cannot be read.</summary>
    private static bool Read(string key, bool last)
    {
        try
        {
            return PropertyManager.GetBool(key, true).Item;
        }
        catch (Exception e)
        {
            Fault("reading the server setting " + key, e, LogLevel.Warn);
            return last;
        }
    }

    /// <summary>
    /// Where the traffic log writes: beside the server's own log (the folder of the first log4net
    /// file appender, found inside forwarding appenders too), else the mod's folder's `logs`; then
    /// `dereth-network`.
    /// </summary>
    private static string TrafficDirectory(string folder)
    {
        string? dir = null;
        try
        {
            var seen = new HashSet<IAppender>();
            foreach (var repo in LogManager.GetAllRepositories())
            {
                foreach (var a in repo.GetAppenders())
                {
                    dir = FileAppenderDirectory(a, seen);
                    if (dir is not null)
                        break;
                }
                if (dir is not null)
                    break;
            }
        }
        catch (Exception e)
        {
            Log(LogLevel.Warn, $"the server's log folder cannot be found ({e.GetType().Name}); the traffic log goes beside the mod");
        }
        return Path.Combine(dir ?? Path.Combine(folder, "logs"), "dereth-network");
    }

    private static string? FileAppenderDirectory(IAppender a, HashSet<IAppender> seen)
    {
        if (!seen.Add(a))
            return null;
        if (a is FileAppender f && !string.IsNullOrEmpty(f.File))
            return Path.GetDirectoryName(Path.GetFullPath(f.File));
        if (a is IAppenderAttachable nested)
        {
            foreach (var inner in nested.Appenders)
            {
                if (FileAppenderDirectory(inner, seen) is { } d)
                    return d;
            }
        }
        return null;
    }
}
