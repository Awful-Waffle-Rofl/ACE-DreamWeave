using System;
using System.Threading;

using ACE.Server.Realms;
using ACE.Server.WorldObjects;

namespace ACE.Server.Avatars.Gauntlet;

/// <summary>
/// The realm hold of one Gauntlet arena copy (EphemeralRealm.Hold, attached before the copy loads by
/// RealmManager.GetNewGauntletOutdoorLandblock). It is the three seams the copy needs, driven by the run's
/// <see cref="State"/>:
/// <list type="bullet">
/// <item>admission (<see cref="IEphemeralAdmission"/>): a current member of the run's allegiance, re-read live on every
/// check, while the run is <see cref="GauntletState.Staging"/>, Active, Intermission or Rewarding (<see cref="AdmitsIn"/>);</item>
/// <item>keep-alive (<see cref="IEphemeralHold"/>): the idle window covers the run's whole lifetime, so an empty arena
/// keeps ticking until its own timer ends the run, and lapses to the default the moment the run is terminal;</item>
/// <item>the member hold (<see cref="IFootprintHold"/>): no footprint member unloads on its own idle timer until the run
/// is terminal (Closed or Aborted).</item>
/// </list>
/// The run (Task 7) writes <see cref="State"/> on the world thread; the three reads come from the teleport, login and
/// heartbeat paths on any thread, so the state is published through a volatile field.
/// </summary>
public sealed class GauntletArenaHold : IEphemeralHold, IEphemeralAdmission, IFootprintHold
{
    private readonly Func<Player, bool> isMember;
    private readonly TimeSpan lifetimeWindow;
    private int state;

    /// <param name="isMember">The live membership test, for example <see cref="ForAvatar"/>'s AvatarRights.IsMemberForAdmission.</param>
    /// <param name="lifetimeWindow">The idle window while the run lives; at least the run's longest possible life.</param>
    /// <param name="initial">The run's state when the hold is attached (Purchased: the copy is loading, nobody enters yet).</param>
    public GauntletArenaHold(Func<Player, bool> isMember, TimeSpan lifetimeWindow, GauntletState initial = GauntletState.Purchased)
    {
        this.isMember = isMember ?? throw new ArgumentNullException(nameof(isMember));
        this.lifetimeWindow = lifetimeWindow > TimeSpan.Zero ? lifetimeWindow : throw new ArgumentOutOfRangeException(nameof(lifetimeWindow));
        state = (int)initial;
    }

    /// <summary>
    /// A hold whose membership is "a current member of this Avatar's bound allegiance", read live on every check through
    /// AvatarRights.IsMemberForAdmission (guid-keyed, so it holds on the login reroute, before the player is online).
    /// </summary>
    public static GauntletArenaHold ForAvatar(AvatarState avatar, TimeSpan lifetimeWindow, GauntletState initial = GauntletState.Purchased)
        => new GauntletArenaHold(p => AvatarRights.IsMemberForAdmission(p, avatar), lifetimeWindow, initial);

    public GauntletState State
    {
        get => (GauntletState)Volatile.Read(ref state);
        set => Volatile.Write(ref state, (int)value);
    }

    /// <summary>The run states in which a member may enter: Staging, Active, Intermission and Rewarding (the spec's arena table). Pure.</summary>
    public static bool AdmitsIn(GauntletState s) =>
        s == GauntletState.Staging || s == GauntletState.Active || s == GauntletState.Intermission || s == GauntletState.Rewarding;

    public bool Admits(Player player) => player != null && AdmitsIn(State) && isMember(player);

    public bool HoldsMembers => !GauntletStateMachine.IsTerminal(State);

    public TimeSpan? UnloadIntervalOverride => GauntletStateMachine.IsTerminal(State) ? null : lifetimeWindow;
}
