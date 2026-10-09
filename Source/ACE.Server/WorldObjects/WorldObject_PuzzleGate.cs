using ACE.Server.PuzzleGates;

namespace ACE.Server.WorldObjects
{
    partial class WorldObject
    {
        /// <summary>
        /// Puzzle gates (WaffleACE): the placement a spawned puzzle LEVER belongs to, set before EnterWorld by
        /// PuzzleGateManager and null on everything else in the world. Purely in-memory and NEVER persisted -
        /// the same runtime back-reference convention as P_ObjectiveLock (WorldObject_Objective.cs). Every
        /// object that carries it is an IsTransientSpawn, so there is no persisted state for it to disagree with.
        /// <para/>
        /// It carries no part of the answer: which lever is correct lives only in the placement.
        /// </summary>
        public PuzzleGatePlacement P_PuzzleGate;

        /// <summary>
        /// Puzzle gates (WaffleACE): true on every GATE object PuzzleGateManager spawns (a door, each barrier panel,
        /// a focal object), set before EnterWorld, in memory only. ThreadDungeonSpawner.UnlockDoors skips a door
        /// carrying it - the run door pass unlocks every other Door in a copy, and a puzzle gate must stay locked
        /// until it is solved (monsters with AiOptions open an unlocked door by walking into it).
        /// </summary>
        public bool IsPuzzleGateObject;
    }

    partial class Creature
    {
        /// <summary>
        /// Puzzle gates (WaffleACE): true for a creature PuzzleGateManager spawned as a wrong-answer ambush. Set
        /// before EnterWorld, in memory only, never persisted (the creature is an IsTransientSpawn anyway).
        /// <para/>
        /// A wrong lever pull is free and repeatable every few seconds, so an ambush that paid out would be a
        /// farm. Creature.OnDeath and Creature.Die read this through <see cref="IsRewardlessDeath"/> and grant
        /// nothing for such a death: no corpse or treasure, no XP or luminance, no kill-task credit, no on-kill
        /// player hooks, no death emote, no death spawn. See Creature_Death.cs.
        /// </summary>
        public bool IsPuzzleAmbush;
    }
}
