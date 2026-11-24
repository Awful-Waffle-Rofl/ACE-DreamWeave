using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using log4net;

using ACE.Server.ThreadDungeons;
using ACE.Server.WorldObjects;

using Position = ACE.Entity.Position;

namespace ACE.Server.Avatars.Gauntlet;

/// <summary>
/// What the death hold needs from a run (Task 9). <see cref="GauntletRun"/> implements it; tests hand in a fake, because a
/// real run needs a loaded copy before <see cref="PenPosition"/> or <see cref="NextEntryPoint"/> answer.
/// </summary>
internal interface IGauntletHoldRun
{
    uint RunKey { get; }

    /// <summary>The arena copy's instance (0 before it loaded).</summary>
    uint Instance { get; }

    /// <summary>True once the run is Closed or Aborted.</summary>
    bool IsTerminal { get; }

    /// <summary>The site's death pen, in the arena's instance.</summary>
    Position PenPosition { get; }

    /// <summary>The next arrival point in rotation, in the arena's instance (null with no entries or no copy).</summary>
    Position NextEntryPoint();

    double CentreGx { get; }
    double CentreGy { get; }
    double Radius { get; }

    /// <summary>allegiance_gauntlet_death_hold_seconds as the run captured it (its sanitized tuning).</summary>
    long DeathHoldSeconds { get; }

    /// <summary>The per-wave death count (GauntletRun.OnMemberDied).</summary>
    void OnMemberDied(Player player);
}

/// <summary>What one 1 Hz check of a player decides (<see cref="GauntletDeathHolds.Decide"/>).</summary>
internal enum GauntletHoldStep
{
    /// <summary>Not held.</summary>
    None,

    /// <summary>Held, outside the circle, time left: nothing to do.</summary>
    Wait,

    /// <summary>Held, standing in the arena inside the fight circle, time left: back to the pen.</summary>
    SnapBack,

    /// <summary>The hold is over and the player is in the arena: full vitals, protection cleared, to an entry point.</summary>
    Release,

    /// <summary>The hold is over (or its run is), and the player is not in the arena: forget it, move nobody.</summary>
    Drop,
}

/// <summary>
/// The registry of death holds, keyed by character guid (a character is in at most one arena at a time). It is in memory
/// only, on purpose: a run never survives a restart (the startup close), so a hold never needs to either. A relog while
/// the run lives finds the same entry by guid, so the Player object a login builds is held, snapped back or released by
/// its first check exactly as the old one would have been. Thread-safe: deaths and checks come from landblock threads,
/// <see cref="ReleaseAll"/> from the world thread.
/// </summary>
internal sealed class GauntletDeathHolds
{
    private readonly Func<DateTime> utcNow;
    private readonly object sync = new object();
    private readonly Dictionary<uint, Entry> holds = new Dictionary<uint, Entry>();
    private int count;

    private readonly record struct Entry(IGauntletHoldRun Run, DateTime Until);

    public GauntletDeathHolds(Func<DateTime> utcNow)
    {
        this.utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
    }

    /// <summary>The number of holds. One volatile read: the per-player check returns on 0 without taking the lock.</summary>
    public int Count => Volatile.Read(ref count);

    /// <summary>Starts (or restarts) <paramref name="guid"/>'s hold in <paramref name="run"/>, for the run's hold seconds from now.</summary>
    public void Begin(IGauntletHoldRun run, uint guid)
    {
        if (run == null)
            throw new ArgumentNullException(nameof(run));

        var until = utcNow().AddSeconds(Math.Max(0L, run.DeathHoldSeconds));

        lock (sync)
        {
            holds[guid] = new Entry(run, until);
            Volatile.Write(ref count, holds.Count);
        }
    }

    /// <summary>Seconds left on <paramref name="guid"/>'s hold in <paramref name="run"/>; 0 when not held there or the hold is over.</summary>
    public double SecondsLeft(IGauntletHoldRun run, uint guid)
    {
        if (run == null)
            return 0;

        lock (sync)
        {
            if (!holds.TryGetValue(guid, out var e) || !ReferenceEquals(e.Run, run))
                return 0;

            return Math.Max(0.0, (e.Until - utcNow()).TotalSeconds);
        }
    }

    /// <summary>The run <paramref name="guid"/> is held in, or null.</summary>
    public IGauntletHoldRun RunOf(uint guid)
    {
        lock (sync)
            return holds.TryGetValue(guid, out var e) ? e.Run : null;
    }

    /// <summary>Ends <paramref name="guid"/>'s hold, only when it is still the one in <paramref name="run"/>. True when one was removed.</summary>
    public bool End(uint guid, IGauntletHoldRun run)
    {
        lock (sync)
        {
            if (!holds.TryGetValue(guid, out var e) || !ReferenceEquals(e.Run, run))
                return false;

            holds.Remove(guid);
            Volatile.Write(ref count, holds.Count);
            return true;
        }
    }

    /// <summary>Ends every hold in <paramref name="run"/> (the run closed); returns the guids released.</summary>
    public IReadOnlyList<uint> ReleaseAll(IGauntletHoldRun run)
    {
        lock (sync)
        {
            var guids = holds.Where(kv => ReferenceEquals(kv.Value.Run, run)).Select(kv => kv.Key).ToList();

            foreach (var g in guids)
                holds.Remove(g);

            Volatile.Write(ref count, holds.Count);
            return guids;
        }
    }

    /// <summary>
    /// One check of <paramref name="guid"/> standing at <paramref name="location"/>. Returns the step and, for every step but
    /// <see cref="GauntletHoldStep.None"/>, the run and the seconds left. Ends the hold itself on Release and Drop, so a step
    /// is acted on once.
    /// </summary>
    public GauntletHoldStep Check(uint guid, Position location, out IGauntletHoldRun run, out double secondsLeft)
    {
        run = null;
        secondsLeft = 0;

        Entry e;
        lock (sync)
        {
            if (!holds.TryGetValue(guid, out e))
                return GauntletHoldStep.None;
        }

        run = e.Run;
        secondsLeft = Math.Max(0.0, (e.Until - utcNow()).TotalSeconds);

        var inArena = location != null && run.Instance != 0 && location.Instance == run.Instance;
        var insideCircle = inArena && InsideSnapZone(run, location);
        var step = Decide(run.IsTerminal, inArena, insideCircle, secondsLeft <= 0);

        if (step == GauntletHoldStep.Release || step == GauntletHoldStep.Drop)
            End(guid, run);

        return step;
    }

    /// <summary>The 1 Hz rule. Pure.</summary>
    public static GauntletHoldStep Decide(bool runTerminal, bool inArena, bool insideCircle, bool expired)
    {
        if (runTerminal)
            return GauntletHoldStep.Drop;

        if (expired)
            return inArena ? GauntletHoldStep.Release : GauntletHoldStep.Drop;

        return insideCircle ? GauntletHoldStep.SnapBack : GauntletHoldStep.Wait;
    }

    /// <summary>
    /// Metres past the fight circle's edge a held player is still snapped back from: a held player at the edge could
    /// otherwise strike, heal or buff into the circle. The pen is at least allegiance_gauntlet_pen_min_distance (100) past
    /// the edge (the site lint), so it always lies outside this zone.
    /// </summary>
    public const double SnapBackMargin = 75.0;

    /// <summary>True when <paramref name="location"/> is within the run's fight circle (global outdoor metres, 2D).</summary>
    public static bool InsideCircle(IGauntletHoldRun run, Position location) => Within(run, location, run.Radius);

    /// <summary>True when <paramref name="location"/> is within the circle plus <see cref="SnapBackMargin"/>: a held player there is snapped back.</summary>
    public static bool InsideSnapZone(IGauntletHoldRun run, Position location) => Within(run, location, run.Radius + SnapBackMargin);

    private static bool Within(IGauntletHoldRun run, Position location, double radius)
    {
        var lb = (ushort)(location.Cell >> 16);
        var dx = OutdoorSiteGeometry.ToGlobalX(lb, location.PositionX) - run.CentreGx;
        var dy = OutdoorSiteGeometry.ToGlobalY(lb, location.PositionY) - run.CentreGy;
        return dx * dx + dy * dy <= radius * radius;
    }
}

/// <summary>What a check does to a player. The live one calls the Player_Gauntlet methods; tests record.</summary>
internal interface IGauntletHoldEffects
{
    void SnapBack(Player player, Position pen, int secondsLeft);
    void Release(Player player, Position entry);
}

/// <summary>
/// Task 9, the death hold, installed on <see cref="GauntletRunHooks.Live"/> by LiveGauntletEnvironment.Build. A member who dies
/// inside a live arena copy is latched (Player.Die, through <see cref="LatchDeath"/>), sent to the site's pen with no vitae,
/// no item loss, no corpse and every buff kept, held for allegiance_gauntlet_death_hold_seconds, snapped back to the pen if
/// they walk into the circle, and then released: lifestone protection cleared, full vitals, and a teleport to the run's next
/// entry point (the Battlegrounds respawn steps, Docs/Pvp/BATTLEGROUNDS.md "Respawn" 6).
/// </summary>
internal sealed class LiveGauntletDeathHooks : IGauntletDeathHooks
{
    private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    public const string ReleaseLine = "You rise again and return to the fight!";

    /// <summary>The one live instance; the Player partial reaches it through here.</summary>
    public static LiveGauntletDeathHooks Instance { get; private set; } = new LiveGauntletDeathHooks(() => DateTime.UtcNow);

    /// <summary>Test seam: the live run whose arena copy is the instance (GauntletManager.ForInstance).</summary>
    internal static Func<uint, IGauntletHoldRun> RunForInstance = DefaultRunForInstance;

    private static IGauntletHoldRun DefaultRunForInstance(uint instance) => GauntletManager.ForInstance(instance);

    public GauntletDeathHolds Holds { get; }

    /// <summary>What a check does to a player (the live Player_Gauntlet methods by default).</summary>
    internal IGauntletHoldEffects Effects { get; set; } = LivePlayerEffects.Instance;

    internal LiveGauntletDeathHooks(Func<DateTime> utcNow)
    {
        Holds = new GauntletDeathHolds(utcNow);
    }

    /// <summary>Test seam: swaps the live instance (a stepped clock); <see cref="ResetSeams"/> puts a fresh one back.</summary>
    internal static void Install(LiveGauntletDeathHooks hooks) => Instance = hooks ?? throw new ArgumentNullException(nameof(hooks));

    internal static void ResetSeams()
    {
        Instance = new LiveGauntletDeathHooks(() => DateTime.UtcNow);
        RunForInstance = DefaultRunForInstance;
    }

    // ---- the death moment ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Player.Die, at the moment of death: the run whose live copy <paramref name="instance"/> is, or null. When there is
    /// one, the death is counted on the run and the hold begins now. Instance 0 (the open world) and a terminal run never
    /// latch. The caller wraps this, so a fault reads as "not a Gauntlet death".
    /// </summary>
    public IGauntletHoldRun LatchDeath(Player player, uint guid, uint instance)
    {
        if (instance == 0)
            return null;

        var run = RunForInstance(instance);

        if (run == null || run.IsTerminal)
            return null;

        Holds.Begin(run, guid);

        try
        {
            run.OnMemberDied(player);
        }
        catch (Exception ex)
        {
            log.Error($"[GAUNTLET] run {run.RunKey} death count threw for 0x{guid:X8}", ex);
        }

        return run;
    }

    /// <summary>ThreadSafeTeleportOnDeath: the pen of the run <paramref name="guid"/> is held in, or null (no hold, or the run ended).</summary>
    public Position PenFor(uint guid)
    {
        var run = Holds.RunOf(guid);

        if (run == null || run.IsTerminal)
            return null;

        var pen = run.PenPosition;
        return pen != null ? new Position(pen) : null;
    }

    // ---- the 1 Hz check -------------------------------------------------------------------------------------------------

    /// <summary>
    /// One check of a player (Player.GauntletHoldTick, the player's own landblock thread). <paramref name="busy"/> (dead, in the
    /// death sequence, teleporting, or not yet in the world after a login) skips it: the next check, a second later, decides.
    /// </summary>
    public GauntletHoldStep TickPlayer(Player player, uint guid, Position location, bool busy)
    {
        if (busy || Holds.Count == 0)
            return GauntletHoldStep.None;

        var step = Holds.Check(guid, location, out var run, out var left);

        switch (step)
        {
            case GauntletHoldStep.SnapBack:
                var pen = run.PenPosition;
                if (pen != null)
                    Effects.SnapBack(player, new Position(pen), (int)Math.Ceiling(left));
                break;

            case GauntletHoldStep.Release:
                var entry = run.NextEntryPoint();
                Effects.Release(player, entry != null ? new Position(entry) : null);
                log.Info($"[GAUNTLET] run {run.RunKey}: 0x{guid:X8} released from the death hold");
                break;
        }

        return step;
    }

    // ---- IGauntletDeathHooks ------------------------------------------------------------------------------------------------

    public double HoldSecondsLeft(GauntletRun run, Player player)
        => player == null ? 0 : Holds.SecondsLeft(run, player.Guid.Full);

    public void ReleaseAll(GauntletRun run)
    {
        var released = Holds.ReleaseAll(run);

        if (released.Count > 0)
            log.Info($"[GAUNTLET] run {run?.RunKey}: {released.Count} death hold(s) ended with the run");
    }

    private sealed class LivePlayerEffects : IGauntletHoldEffects
    {
        public static readonly LivePlayerEffects Instance = new LivePlayerEffects();

        public void SnapBack(Player player, Position pen, int secondsLeft) => player.GauntletSnapBackToPen(pen, secondsLeft);

        public void Release(Player player, Position entry) => player.ReleaseFromGauntletHold(entry);
    }
}
