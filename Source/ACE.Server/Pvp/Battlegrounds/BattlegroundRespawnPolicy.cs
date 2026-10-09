using System;

namespace ACE.Server.Pvp.Battlegrounds
{
    /// <summary>
    /// The respawn policy every battleground mode shares (Docs/Pvp/BATTLEGROUNDS.md "Respawn"). Stateless: one
    /// instance serves every match of the mode through <see cref="PvpModeDefinition.Respawn"/>. It always returns
    /// <see cref="DeathDisposition.Respawn"/> with the configured pen delay and a spawn point index of -1, which
    /// means "the coordinator picks the next spawn point round-robin".
    /// </summary>
    public sealed class BattlegroundRespawnPolicy : IRespawnPolicy
    {
        /// <summary>The spawn point index that asks the coordinator to choose the next team spawn round-robin.</summary>
        public const int CoordinatorChoosesSpawn = -1;

        private readonly TimeSpan _delay;

        public BattlegroundRespawnPolicy(int respawnSeconds)
        {
            _delay = TimeSpan.FromSeconds(respawnSeconds);
        }

        public DeathDisposition OnDeath(IMatchView m, PvpParticipant p) => DeathDisposition.Respawn(_delay, CoordinatorChoosesSpawn);
    }
}