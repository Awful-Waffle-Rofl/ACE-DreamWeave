using ACE.Server.WorldObjects;

namespace ACE.Server.Realms
{
    /// <summary>
    /// An <see cref="IEphemeralHold"/> that also decides who may enter its copy (an Allegiance Gauntlet arena, which has
    /// no Owner and no Thread run). EphemeralRealm.Accepts asks it right after the Thread puzzle refusal, and its answer is
    /// final both ways: the Owner, allowed-list and fellowship rules are never consulted for a copy whose hold admits.
    /// Read from the teleport and login-reroute paths on any thread, so an implementation must be thread-safe.
    /// </summary>
    public interface IEphemeralAdmission
    {
        /// <summary>True when <paramref name="player"/> may enter (or stay in, on a relog) this copy. Null is never admitted.</summary>
        bool Admits(Player player);
    }
}
