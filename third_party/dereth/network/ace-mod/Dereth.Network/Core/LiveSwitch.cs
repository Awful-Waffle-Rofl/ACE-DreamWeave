// Local modification (not upstream's): the network's live switch, as the mod's world tick drives it
// from the server's settings (Ace/Mod.Control.cs). It holds no link of its own: it decides, and makes
// switching off safe to undo.
//
// Off: the link's task is taken before the link is stopped (HubLink.Stop forgets it and does not
// wait), then the world half forgets the link as if it had gone (remote friends show offline the
// usual way). On: allowed only once that old task has ended, or OldLinkWaitSecs have passed, so this
// world never holds two connections to the hub at once. A fresh link is then made by the caller (a
// stopped HubLink cannot start again); it takes a new epoch from HubLink's own counter.
//
// Everything here runs on the world thread.

namespace Dereth.Network;

public sealed class LiveSwitch
{
    /// <summary>How often the settings are read, in seconds; the first call reads at once.</summary>
    public const double PollEverySecs = 1;

    /// <summary>The longest switching on waits for the old link's task to end, in seconds.</summary>
    public const double OldLinkWaitSecs = 15;

    public enum Step
    {
        None,
        Off,
        On,
    }

    private readonly Func<double> clock;
    private double nextPoll = double.NegativeInfinity;
    private Task? oldLink;
    private double offAt;

    /// <param name="clock">Seconds on a monotonic scale.</param>
    public LiveSwitch(Func<double> clock)
    {
        this.clock = clock;
    }

    /// <summary>Whether the settings are to be read now: at once the first time, then at most once a second.</summary>
    public bool Due()
    {
        var t = clock();
        if (t < nextPoll)
            return false;
        nextPoll = t + PollEverySecs;
        return true;
    }

    /// <summary>What the switch does now, from the setting and the network's state.</summary>
    public Step Decide(bool enabled, bool on, bool parked)
    {
        if (!enabled && on)
            return Step.Off;
        if (enabled && !on && parked && MayResume)
            return Step.On;
        return Step.None;
    }

    /// <summary>Whether a new link may start: the old one's task has ended, or it has had its time.</summary>
    public bool MayResume => oldLink is null || oldLink.IsCompleted || clock() - offAt >= OldLinkWaitSecs;

    /// <summary>
    /// Takes the network off its link: the link's task is kept, the link stopped, and the world half
    /// told the link is gone.
    /// </summary>
    public void TakeOff(NetworkWorld world, HubLink link)
    {
        oldLink = link.Running;
        link.Stop();
        world.LinkGone();
        offAt = clock();
    }

    /// <summary>A new link has been made: the old one is no longer waited for.</summary>
    public void Resumed() => oldLink = null;
}

/// <summary>
/// The switch's sequence over the mod's runtime (`T`, which the mod's ACE half defines), ACE-free so
/// the harness drives it. The running runtime lives where the hooks read it (`current`/`publish`);
/// a switched-off one is parked here.
///
/// Off: who is online is taken, the runtime parked BEFORE the hooks stop seeing it (so a stop in
/// between still finds it to save), the link taken off and the store handed to be saved. On: a new
/// runtime on a fresh link (`relink`), the skipped logins and logouts reconciled, and only then
/// published. Stop takes the state lock, so it never runs inside a switch: after it nothing is
/// switched or published again, and it hands back the runtime to save, parked or running.
/// </summary>
public sealed class LiveControl<T> where T : class
{
    private readonly object state = new();
    private readonly Func<T?> current;
    private readonly Action<T?> publish;
    private readonly Func<T, NetworkWorld> worldOf;
    private readonly Func<T, HubLink> linkOf;
    private readonly Func<T, T> relink;
    private T? parked;
    private IReadOnlyDictionary<uint, OnlineAtOff>? atOff;
    private volatile bool stopped;

    public LiveControl(LiveSwitch sw, Func<T?> current, Action<T?> publish, Func<T, NetworkWorld> worldOf, Func<T, HubLink> linkOf,
        Func<T, T> relink)
    {
        Switch = sw;
        this.current = current;
        this.publish = publish;
        this.worldOf = worldOf;
        this.linkOf = linkOf;
        this.relink = relink;
    }

    public LiveSwitch Switch { get; }

    /// <summary>The switched-off runtime, or null.</summary>
    public T? Parked => Volatile.Read(ref parked);

    /// <summary>Stopped: nothing is switched again.</summary>
    public bool Stopped => stopped;

    /// <summary>The world half, running or parked; null once stopped or before a runtime exists.</summary>
    public NetworkWorld? World => (current() ?? Parked) is { } r ? worldOf(r) : null;

    /// <summary>Switches to `enabled` if the state calls for it now; returns what it did.</summary>
    public LiveSwitch.Step Apply(bool enabled)
    {
        lock (state)
        {
            if (stopped)
                return LiveSwitch.Step.None;
            var r = current();
            var p = Volatile.Read(ref parked);
            var step = Switch.Decide(enabled, r is not null, p is not null);
            if (step == LiveSwitch.Step.Off)
            {
                var world = worldOf(r!);
                atOff = world.OnlineNow();
                Volatile.Write(ref parked, r);
                publish(null);
                Switch.TakeOff(world, linkOf(r!));
                // Handed to the store's writer, which writes it on its own task.
                world.SaveNow();
            }
            else if (step == LiveSwitch.Step.On)
            {
                var next = relink(p!);
                worldOf(next).ResumeAfterOff(atOff ?? new Dictionary<uint, OnlineAtOff>());
                atOff = null;
                Switch.Resumed();
                if (stopped)
                    return LiveSwitch.Step.None;
                Volatile.Write(ref parked, null);
                publish(next);
            }
            return step;
        }
    }

    /// <summary>Stops switching, waiting out a switch in progress; the runtime to save (the parked one, else the running one).</summary>
    public T? Stop()
    {
        stopped = true;
        lock (state)
            return Interlocked.Exchange(ref parked, null) ?? current();
    }
}
