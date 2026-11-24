using ACE.Server.Avatars.Gauntlet;

namespace ACE.Server.WorldObjects
{
    partial class Creature
    {
        /// <summary>
        /// The Allegiance Gauntlet run a creature was spawned for (GauntletSpawner.Prepare), or null. THE creature-side
        /// membership test: non-null means a Gauntlet owns this creature. In memory only, never persisted and never networked
        /// (the P_ back-reference convention, like P_TrialRep): the arena is an ephemeral copy, and its creatures go away with
        /// it. Read by Creature_Death three times: the kill-progress gate (FeedsKillProgressHooks), the corpse local
        /// (<see cref="IsGauntletCorpselessDeath"/>) and the death hook after the Balance Trials hook.
        /// </summary>
        public IGauntletCreatureOwner P_Gauntlet;

        /// <summary>The wave this creature was spawned for (IGauntletCreatureOwner.CurrentWave at Prepare). Meaningful only while P_Gauntlet is set.</summary>
        public int P_GauntletWave;

        /// <summary>The max health this creature entered the world with (after the clamp): the score fraction's denominator for it.</summary>
        public uint P_GauntletSpawnMaxHealth;

        /// <summary>
        /// A Gauntlet creature leaves no corpse, so no loot falls and nothing outlives the arena. OR-ed into Die's corpse local
        /// beside the World Events pooled-loot and Pinnacle predicates. Pure.
        /// </summary>
        internal static bool IsGauntletCorpselessDeath(bool gauntletCreature) => gauntletCreature;
    }
}
