using System.Collections.Generic;

using ACE.Server.Physics;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// World Events Hold goal ("protect the targets"): the two runtime-only flags the goal puts on creatures,
    /// and the seeker's target pick.
    ///
    /// Why it is gated. Stock ACE monsters only ever target players, combat pets, faction mobs and FoeType
    /// matches, so a friendly ward would never be attacked. The Hold goal opens exactly one extra route,
    /// keyed on <see cref="WorldEventProtected"/> (on the ward) and the shared run stamp: a monster placed by
    /// the SAME run may target and damage it. Both flags are set only by WorldEventSpawner, are never
    /// persisted (a world-event creature is never saved to the shard), and default to false, so every other
    /// creature in the world behaves exactly as before.
    /// </summary>
    partial class Creature
    {
        /// <summary>
        /// Runtime-only: this creature is a Hold goal's protected target. It never thinks (Monster_Tick skips
        /// it), never retaliates, cannot be harmed by players or pets, and can be targeted and damaged only by
        /// monsters from its own run. Set only by WorldEventSpawner, before EnterWorld.
        /// </summary>
        public bool WorldEventProtected { get; set; }

        /// <summary>
        /// Runtime-only: this wave monster is one of the protect.seekShare share that goes straight for the
        /// run's protected targets instead of the players. Set only by WorldEventSpawner, before EnterWorld.
        /// Cleared once no protected target is left, so the monster falls back to ordinary targeting.
        /// </summary>
        public bool WorldEventSeeker { get; set; }

        /// <summary>
        /// True when <paramref name="target"/> is a protected target and this creature is a monster placed by
        /// the same run. The only way any monster may damage a protected target.
        /// </summary>
        public bool IsWorldEventProtectedTargetFor(Creature target)
        {
            if (target == null || !target.WorldEventProtected || this is Player || this is CombatPet)
                return false;

            return P_WorldEvent != null && ReferenceEquals(P_WorldEvent, target.P_WorldEvent);
        }

        /// <summary>
        /// The seeker's pick, called from FindNextTarget: keep the current protected target while it stands,
        /// otherwise take the nearest living protected target of this run within chase range, making sure it
        /// is in this monster's visible-target list so the tick loop keeps it. Returns false (and normal
        /// targeting runs) when there is none in reach; clears <see cref="WorldEventSeeker"/> once none is
        /// left at all.
        /// </summary>
        public bool TrySelectWorldEventSeekTarget()
        {
            if (!WorldEventSeeker || PhysicsObj == null)
                return false;

            var evt = P_WorldEvent;

            if (evt == null || evt.State != ACE.Server.WorldEvents.WorldEventState.Active)
            {
                WorldEventSeeker = false;
                return false;
            }

            if (AttackTarget is Creature current && current.WorldEventProtected && IsUsableSeekTarget(current)
                && IsVisibleTarget(current))
                return true;

            var targets = evt.Spawner.LiveProtectedCreatures;

            if (targets.Count == 0)
            {
                WorldEventSeeker = false;
                return false;
            }

            Creature best = null;
            var bestDistSq = double.MaxValue;

            foreach (var target in targets)
            {
                if (!IsUsableSeekTarget(target))
                    continue;

                var distSq = PhysicsObj.get_distance_sq_to_object(target.PhysicsObj, true);

                if (distSq > MaxChaseRangeSq || distSq >= bestDistSq)
                    continue;

                best = target;
                bestDistSq = distSq;
            }

            if (best == null)
                return false;

            if (!IsVisibleTarget(best))
                PhysicsObj.ObjMaint.AddVisibleTargets(new List<PhysicsObj> { best.PhysicsObj });

            if (!IsVisibleTarget(best))
                return false;

            var previous = AttackTarget;

            AttackTarget = best;

            if (AttackTarget != previous)
                EmoteManager.OnNewEnemy(AttackTarget);

            return true;
        }

        /// <summary>
        /// Hold runs only (WorldEvent's per-tick wake pass): a wave monster that is still asleep gets an
        /// attack target and wakes, instead of standing at the spawn ring until a player happens to walk
        /// within its awareness range. A seeker takes the nearest protected target; everything else takes
        /// the nearest attackable player within chase range. Returns true when it woke. Must run on this
        /// creature's landblock thread (it touches AttackTarget, MonsterState and the physics target list).
        /// </summary>
        public bool TryWorldEventHoldWake()
        {
            if (IsDead || IsDestroyed || PhysicsObj == null || MonsterState != State.Idle)
                return false;

            if (WorldEventSeeker && TrySelectWorldEventSeekTarget())
            {
                // alertNearby false: a seeker never recruits its pack (see AlertFriendly), and every other
                // wave monster in a Hold run is woken by this same pass anyway.
                WakeUp(alertNearby: false);
                return true;
            }

            var player = NearestWakeTargetPlayer();

            if (player == null)
                return false;

            if (!IsVisibleTarget(player))
                PhysicsObj.ObjMaint.AddVisibleTargets(new List<PhysicsObj> { player.PhysicsObj });

            if (!IsVisibleTarget(player))
                return false;

            AttackTarget = player;
            WakeUp(alertNearby: false);
            return true;
        }

        /// <summary>The nearest living, attackable, non-teleporting player this monster knows of, within chase range.</summary>
        private Player NearestWakeTargetPlayer()
        {
            var players = new List<Player>();

            foreach (var creature in PhysicsObj.ObjMaint.GetVisibleTargetsValuesOfTypeCreature())
            {
                if (creature is Player p)
                    players.Add(p);
            }

            foreach (var p in PhysicsObj.ObjMaint.GetKnownPlayersValuesAsPlayer())
            {
                if (p != null && !players.Contains(p))
                    players.Add(p);
            }

            var usable = new List<Player>();
            var distances = new List<double>();

            foreach (var p in players)
            {
                if (p.IsDead || p.Teleporting || !p.Attackable || p.PhysicsObj == null)
                    continue;

                usable.Add(p);
                distances.Add(PhysicsObj.get_distance_sq_to_object(p.PhysicsObj, true));
            }

            var index = ACE.Server.WorldEvents.WorldEventHoldRules.NearestWithin(distances, MaxChaseRangeSq);

            return index >= 0 ? usable[index] : null;
        }

        private bool IsUsableSeekTarget(Creature target)
        {
            return target != null && target.PhysicsObj != null && !target.IsDestroyed && !target.IsDead
                && IsWorldEventProtectedTargetFor(target);
        }
    }
}
