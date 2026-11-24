using ACE.Server.ThreadDungeons;

namespace ACE.Server.WorldObjects
{
    partial class WorldObject
    {
        /// <summary>
        /// Thread encounters (WaffleACE, Docs/Threads/ENCOUNTERS.md): the run encounter this object was placed by, set
        /// before EnterWorld by ThreadEncounterInstance.Track and null on everything else. Purely in-memory and NEVER
        /// persisted - the same runtime back-reference convention as P_PuzzleGate (WorldObject_PuzzleGate.cs). The run is
        /// reached through it (P_ThreadEncounter.Run). Cleared by the framework when the run ends.
        /// </summary>
        public ThreadEncounterInstance P_ThreadEncounter;
    }

    partial class Creature
    {
        /// <summary>
        /// Thread encounters: true for a run creature an encounter added after populate whose death must NOT count toward
        /// the run's clear (a Wayfarer's Shrine guardian). Set before EnterWorld by ThreadDungeonSpawner.ShapeCreature from
        /// CreatureShapeOptions.ExcludedFromClear; in memory only, never persisted (a run creature is never saved). Its
        /// death still banks pooled loot and pays XP and luminance as normal; ThreadDungeonManager.TryRecordBonusKill
        /// counts it as a bonus kill instead of a clear kill.
        /// </summary>
        public bool IsThreadBonusCreature;
    }
}
