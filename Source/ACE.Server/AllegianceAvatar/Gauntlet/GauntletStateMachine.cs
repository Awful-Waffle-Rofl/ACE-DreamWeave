namespace ACE.Server.Avatars.Gauntlet;

/// <summary>A Gauntlet run's states (the spec's state table). The wave number n of Active(n) and Intermission(n) lives with the run, not here.</summary>
public enum GauntletState
{
    Purchased,
    Staging,
    Active,
    Intermission,
    Ending,
    Rewarding,
    Closed,
    Aborted,
}

/// <summary>What can happen to a run. The last three never change a state: only the wave timer ends a run (owner ruling).</summary>
public enum GauntletEvent
{
    /// <summary>The instance loaded and the site is ready.</summary>
    Loaded,
    LoadFailed,
    /// <summary>The staging countdown ended with at least one member inside.</summary>
    StagingOverWithMembers,
    /// <summary>The staging countdown ended with nobody inside.</summary>
    StagingOverEmpty,
    /// <summary>Every planned spawn entered and none is alive.</summary>
    WaveCleared,
    /// <summary>The wave's time limit ran out.</summary>
    TimerExpired,
    IntermissionOver,
    /// <summary>Score, record and announcement are written.</summary>
    EndingDone,
    ClaimWindowOver,
    /// <summary>The instance unloaded or failed.</summary>
    InstanceLost,
    AdminAbort,
    /// <summary>Nobody is inside. Never ends a run.</summary>
    ArenaEmpty,
    /// <summary>Every member is dead or held. Never ends a run.</summary>
    MembersWiped,
    /// <summary>The Avatar fighter fell. Never ends a run.</summary>
    FighterDied,
}

/// <summary>
/// The run state machine as a pure function. An event with no row for the state leaves the state unchanged
/// (<see cref="TryNext"/> reports that). Closed and Aborted are terminal.
/// </summary>
public static class GauntletStateMachine
{
    public static bool IsTerminal(GauntletState state) => state == GauntletState.Closed || state == GauntletState.Aborted;

    public static GauntletState Next(GauntletState state, GauntletEvent ev) => TryNext(state, ev, out var next) ? next : state;

    public static bool TryNext(GauntletState state, GauntletEvent ev, out GauntletState next)
    {
        next = state;

        switch (state)
        {
            case GauntletState.Purchased:
                switch (ev)
                {
                    case GauntletEvent.Loaded: next = GauntletState.Staging; return true;
                    case GauntletEvent.LoadFailed:
                    case GauntletEvent.InstanceLost:
                    case GauntletEvent.AdminAbort: next = GauntletState.Aborted; return true;
                }
                return false;

            case GauntletState.Staging:
                switch (ev)
                {
                    case GauntletEvent.StagingOverWithMembers: next = GauntletState.Active; return true;
                    case GauntletEvent.StagingOverEmpty:
                    case GauntletEvent.InstanceLost:
                    case GauntletEvent.AdminAbort: next = GauntletState.Aborted; return true;
                }
                return false;

            case GauntletState.Active:
                switch (ev)
                {
                    case GauntletEvent.WaveCleared: next = GauntletState.Intermission; return true;
                    case GauntletEvent.TimerExpired:
                    case GauntletEvent.InstanceLost:
                    case GauntletEvent.AdminAbort: next = GauntletState.Ending; return true;
                }
                return false;

            case GauntletState.Intermission:
                switch (ev)
                {
                    case GauntletEvent.IntermissionOver: next = GauntletState.Active; return true;
                    case GauntletEvent.InstanceLost:
                    case GauntletEvent.AdminAbort: next = GauntletState.Ending; return true;
                }
                return false;

            case GauntletState.Ending:
                if (ev == GauntletEvent.EndingDone) { next = GauntletState.Rewarding; return true; }
                return false;

            case GauntletState.Rewarding:
                switch (ev)
                {
                    case GauntletEvent.ClaimWindowOver:
                    case GauntletEvent.InstanceLost:
                    case GauntletEvent.AdminAbort: next = GauntletState.Closed; return true;
                }
                return false;

            default:
                return false;
        }
    }
}
