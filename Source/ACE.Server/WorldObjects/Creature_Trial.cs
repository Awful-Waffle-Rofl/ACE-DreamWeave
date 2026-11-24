using ACE.Server.BalanceTrials;
using ACE.Server.ThreadDungeons;

namespace ACE.Server.WorldObjects
{
    partial class Creature
    {
        /// <summary>
        /// The Balance Trials rep a creature was spawned for (TrialSpawner.Prepare), or null. THE creature-side membership test:
        /// non-null means a trial owns this creature. In memory only, never persisted and never networked (the P_ back-reference
        /// convention, like P_SpeedCourseCreature): a trial room is an ephemeral copy, and its creatures go away with it. Read by
        /// OnDeath through <see cref="FeedsKillProgressHooks"/>, so a trial kill feeds no kill-fill vessel and no mule-form token.
        /// </summary>
        public TrialRep P_TrialRep;

        /// <summary>The plan role this trial creature was placed as (TrialSpawner.Prepare). Meaningful only while P_TrialRep is set.</summary>
        public DungeonRole P_TrialRole;

        /// <summary>
        /// The kill award OnDeath_GrantXP paid the trial's player for this creature: the rounded XpOverride and LuminanceAward
        /// shares, before the player's own modifiers. Written on the room thread by TrialHooks.OnKillAward, read by
        /// TrialManager.OnTrialCreatureDied for the kill event. Never the run's XP total (that comes from the grant site).
        /// </summary>
        public long P_TrialKillXp;

        /// <inheritdoc cref="P_TrialKillXp"/>
        public long P_TrialKillLum;
    }
}
