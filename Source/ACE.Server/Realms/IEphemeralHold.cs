using System;

namespace ACE.Server.Realms
{
    /// <summary>
    /// Something other than a Thread run that keeps an ephemeral copy loaded past its idle window (a held Pinnacle
    /// fight). EphemeralRealm.Hold carries one; never set together with EphemeralRealm.Run.
    /// </summary>
    public interface IEphemeralHold
    {
        /// <summary>The idle window the landblock heartbeat should use instead of the default, or null for the default.</summary>
        TimeSpan? UnloadIntervalOverride { get; }
    }
}
