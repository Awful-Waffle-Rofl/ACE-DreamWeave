using System.Collections.Generic;

using ACE.Server.WorldObjects;

namespace ACE.Server.Avatars.Gauntlet;

/// <summary>
/// What a Gauntlet creature needs from the run that spawned it: the seam between the wave code (Task 6:
/// <see cref="GauntletWavePlanner"/>, <see cref="GauntletSpawner"/>, Creature.P_Gauntlet) and the run lifecycle (Task 7's
/// GauntletRun, which implements this). Nothing here starts, ends or scores a run; it is only what a creature reads at spawn,
/// at wake and at death.
///
/// Threading. <see cref="GauntletSpawner"/> reads <see cref="CurrentWave"/> and the circle on the arena landblock's queue
/// while it shapes a creature; <see cref="IsLive"/> and <see cref="OnlineMembersInside"/> are read on the creature's own
/// landblock thread when it is woken; <see cref="OnGauntletCreatureDied"/> is called from Creature.Die on the creature's
/// landblock thread. An implementation must make each of these safe to call from that thread (a run that is ticked on the
/// world thread enqueues or locks).
/// </summary>
public interface IGauntletCreatureOwner
{
    /// <summary>
    /// The liveness flag: true while the run still owns its waves (Active or Intermission). False once it has ended, been
    /// aborted or lost its instance, so a creature that dies or wakes after that is inert: its death reports nothing
    /// (<see cref="GauntletSpawner.OnCreatureDied"/>) and it is never woken.
    /// </summary>
    bool IsLive { get; }

    /// <summary>
    /// The wave the run is fighting. A creature is stamped with it when it is shaped (Creature.P_GauntletWave), so the
    /// creature belongs to the wave that was current when it spawned; a death whose stamp differs from the current wave is
    /// a straggler of an earlier wave, which the run may ignore.
    /// </summary>
    int CurrentWave { get; }

    /// <summary>The fight circle's centre, in global outdoor metres (OutdoorSiteGeometry.ToGlobalX of the site's centre).</summary>
    double CentreGx { get; }

    /// <inheritdoc cref="CentreGx"/>
    double CentreGy { get; }

    /// <summary>The fight circle's radius in horizontal metres: the site's radius. The creature perimeter is built from this and the centre.</summary>
    double Radius { get; }

    /// <summary>
    /// The online members of the bound allegiance standing inside the arena copy right now (alive or dead; the wake skips the
    /// dead and the teleporting). The wake picks the nearest of them. Never null; empty when nobody is inside.
    /// </summary>
    IEnumerable<Player> OnlineMembersInside();

    /// <summary>
    /// The death callback: called once per Gauntlet creature death, from Creature.Die after the dieEntered guard and after
    /// OnDeath_GrantXP has paid the kill (through <see cref="GauntletSpawner.OnCreatureDied"/>, which skips it when the run
    /// is no longer live and catches a throw so Die always completes).
    /// </summary>
    void OnGauntletCreatureDied(Creature creature);
}
