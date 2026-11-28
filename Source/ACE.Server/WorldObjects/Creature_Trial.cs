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

        /// <summary>
        /// The start-of-rep aggro grace (scenario startGraceSecs): the TrialManager.Now() until which this wave-0 trial creature
        /// is held, set by TrialSpawner.Spawn before EnterWorld; 0 = not held, which is every creature outside a trial. In
        /// memory only, like P_TrialRep. While held, <see cref="WakeUp"/> refuses every wake (the spawn scan, a player or pet
        /// walking near, a friend's alert, an appraisal), and a sleeping creature never targets, moves or attacks
        /// (Monster_Tick returns at its !IsAwake block). An attack on it from a player or pet, or a taunt, breaks the hold
        /// (<see cref="BreakTrialHold"/>) just before the ordinary retaliation wake. At expiry, Monster_Tick clears the hold and
        /// re-runs the spawn scan, so the creature acquires exactly as it would have at spawn.
        /// </summary>
        public double P_TrialHoldUntil;

        /// <summary>
        /// True while the start grace holds this creature at <paramref name="now"/> (TrialManager.Now()). Only a trial creature
        /// (P_TrialRep set) is ever held. A hold found expired is cleared here.
        /// </summary>
        internal bool IsTrialHeld(double now)
        {
            if (P_TrialHoldUntil == 0.0)
                return false;

            if (P_TrialRep == null || now >= P_TrialHoldUntil)
            {
                P_TrialHoldUntil = 0.0;
                return false;
            }

            return true;
        }

        /// <summary>
        /// WakeUp's gate: true (and the caller's AttackTarget dropped) when the start grace holds this creature now. One
        /// double compare for every creature that is not held.
        /// </summary>
        private bool TrialHoldRefusesWake()
        {
            if (P_TrialHoldUntil == 0.0 || !IsTrialHeld(TrialManager.Now()))
                return false;

            // Every WakeUp caller sets AttackTarget first; a held creature stays asleep with no target.
            AttackTarget = null;
            return true;
        }

        /// <summary>
        /// Ends the start grace for this creature alone (it was attacked by the player or a pet, or taunted), so the wake that
        /// follows goes through. Its held siblings keep waiting. Counted on the rep as an early wake when the hold was still live.
        /// </summary>
        public void BreakTrialHold()
        {
            if (P_TrialHoldUntil == 0.0)
                return;

            var wasHeld = IsTrialHeld(TrialManager.Now());
            P_TrialHoldUntil = 0.0;

            if (wasHeld)
                P_TrialRep?.NoteGraceEarlyWake();
        }

        /// <summary>
        /// Monster_Tick's release step, called only while P_TrialHoldUntil is set: true when the hold ran out this tick (it is
        /// cleared), so the caller re-runs the spawn scan for a creature still asleep.
        /// </summary>
        internal bool TrialHoldExpired(double now) => P_TrialHoldUntil != 0.0 && !IsTrialHeld(now);
    }
}
