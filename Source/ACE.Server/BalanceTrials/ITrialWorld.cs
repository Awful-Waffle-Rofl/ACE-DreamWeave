using System.Collections.Generic;

using ACE.Server.ThreadDungeons;

namespace ACE.Server.BalanceTrials
{
    /// <summary>The seam between the pure trial session and the live world.</summary>
    public interface ITrialWorld
    {
        bool RoomLoaded { get; }

        /// <summary>Live creatures in the room that the trial did not spawn.</summary>
        int LiveForeignCreatures();

        /// <summary>Spawns one wave and returns the guids that entered the world. An empty list is a failed wave.</summary>
        IReadOnlyList<uint> SpawnWave(IReadOnlyList<DungeonSpawnPlanEntry> wave, TrialRep rep);

        bool IsAlive(uint guid);

        /// <summary>Destroys every creature the trial spawned.</summary>
        void DestroyAll();

        bool PlayerOnline { get; }

        bool PlayerInRoom { get; }

        void ReturnPlayerToStart();
    }
}
