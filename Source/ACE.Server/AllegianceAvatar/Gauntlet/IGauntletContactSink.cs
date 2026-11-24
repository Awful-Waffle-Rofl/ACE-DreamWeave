using ACE.Server.WorldObjects;

namespace ACE.Server.Avatars.Gauntlet;

/// <summary>
/// The seam between contact credit (plan Task 11, GauntletHooks) and the run (plan Task 7, GauntletRun,
/// which implements this beside <see cref="IGauntletCreatureOwner"/>). GauntletHooks resolves the run from
/// the Gauntlet creature on either side of a hit (Creature.P_Gauntlet) and, when that owner is also an
/// IGauntletContactSink, reports the member. Kept separate from IGauntletCreatureOwner so Task 6's
/// fakes do not change.
/// </summary>
public interface IGauntletContactSink
{
    /// <summary>
    /// A member had combat contact with a Gauntlet creature during <paramref name="wave"/> (the creature's
    /// P_GauntletWave). Called on the thread that applied the hit; the run records distinct waves per
    /// character and must be safe to call from any landblock thread. <paramref name="active"/> is true when the member
    /// (or their own pet) landed the hit, false when the creature hit the member: only active contact counts toward a
    /// crate, while either kind counts the member as having fought.
    /// </summary>
    void OnMemberContact(Player member, int wave, bool active);
}
