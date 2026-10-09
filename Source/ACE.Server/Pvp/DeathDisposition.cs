using System;

namespace ACE.Server.Pvp
{
    /// <summary>
    /// What <see cref="IRespawnPolicy.OnDeath"/> decides for a participant who just died. Every v1 arena
    /// mode uses <see cref="NoRespawnPolicy"/>, which always returns Eliminate; Respawn exists for a later
    /// battleground mode (design's "battlegrounds will use" extension points).
    /// </summary>
    public abstract record DeathDisposition
    {
        public sealed record EliminateDisposition : DeathDisposition;

        /// <summary>Delay is the pen time. SpawnPointIndex -1 means the coordinator picks the next team spawn point round-robin.</summary>
        public sealed record RespawnDisposition(TimeSpan Delay, int SpawnPointIndex) : DeathDisposition;

        public static readonly DeathDisposition Eliminate = new EliminateDisposition();

        public static DeathDisposition Respawn(TimeSpan delay, int spawnPointIndex) => new RespawnDisposition(delay, spawnPointIndex);
    }
}
