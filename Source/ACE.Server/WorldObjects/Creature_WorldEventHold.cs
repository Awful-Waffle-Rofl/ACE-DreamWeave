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

            // Spread out: the target with the FEWEST other seekers already on it, nearest first among ties, so
            // the wave splits across the caravans and the defenders have to split up too.
            var usable = new List<Creature>();
            var attackerCounts = new List<int>();
            var distances = new List<double>();

            var otherSeekers = evt.Spawner.LiveWaveCreatures;

            foreach (var target in targets)
            {
                if (!IsUsableSeekTarget(target))
                    continue;

                var attackers = 0;

                foreach (var other in otherSeekers)
                {
                    if (other != this && other.WorldEventSeeker && other.AttackTarget == target)
                        attackers++;
                }

                usable.Add(target);
                attackerCounts.Add(attackers);
                distances.Add(PhysicsObj.get_distance_sq_to_object(target.PhysicsObj, true));
            }

            var index = ACE.Server.WorldEvents.WorldEventHoldRules.PickLeastAttacked(attackerCounts, distances, MaxChaseRangeSq);

            if (index < 0)
                return false;

            var best = usable[index];

            if (!IsVisibleTarget(best))
                PhysicsObj.ObjMaint.AddVisibleTargets(new List<PhysicsObj> { best.PhysicsObj });

            if (!IsVisibleTarget(best))
                return false;

            var previous = AttackTarget;

            AttackTarget = best;

            if (AttackTarget != previous)
            {
                EmoteManager.OnNewEnemy(AttackTarget);

                log.Debug($"[WORLDEVENT] seeker 0x{Guid.Full:X8} targets {best.Name} 0x{best.Guid.Full:X8} (was {previous?.Name ?? "none"})");
            }

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

        /// <summary>
        /// Hold runs: the seeker lock. Called from every path that would wake a monster AIMED AT whoever
        /// disturbed it - a player walking into range (Creature.AlertMonster), a player's hit
        /// (Player.OnAttackMonster), a packmate's alert (AlertFriendly) and a creature's hit
        /// (MonsterOnAttackMonster). For a seeker with a protected target in reach it aims the monster at that
        /// target instead and wakes it, and the caller skips its own retarget. Only an explicit taunt
        /// (ApplyTaunt / TauntTarget) can still pull a seeker off. False, changing nothing, for every creature
        /// that is not a seeker.
        /// </summary>
        public bool TryKeepWorldEventSeekerLock(WorldObject disturbedBy)
        {
            if (!WorldEventSeeker)
                return false;

            var previous = AttackTarget;

            if (!TrySelectWorldEventSeekTarget())
                return false;

            var woke = MonsterState != State.Awake;

            if (woke)
                WakeUp(alertNearby: false);

            // Only when the lock changed something (it woke, or it pulled the seeker back onto a caravan),
            // so a player hitting a seeker that is already on its caravan does not log every hit.
            if (woke || previous != AttackTarget)
                log.Debug($"[WORLDEVENT] seeker 0x{Guid.Full:X8} stays on {(AttackTarget as Creature)?.Name ?? "?"} 0x{AttackTarget?.Guid.Full ?? 0:X8} " +
                          $"(ignored {disturbedBy?.Name ?? "?"}; was {previous?.Name ?? "none"})");

            return true;
        }

        /// <summary>
        /// Per attacker: when it last landed a scaled hit on this protected target (Time.GetUnixTime). Only a
        /// protected target ever fills it.
        /// </summary>
        private Dictionary<uint, double> worldEventProtectedLastHitAt;

        /// <summary>
        /// Hold runs (protect.undefendedSeconds above 0): a hit on this protected target from a monster of its
        /// own run is replaced by a slice of the target's max health sized to the time since that attacker's
        /// previous hit, so ONE attacker hitting it continuously destroys it in undefendedSeconds whatever its
        /// weapon damage - see WorldEventHoldRules.ProtectedHitDamage. Every other hit (players, DoT ticks with
        /// no source, other runs, a goal with no undefendedSeconds) is returned unchanged. Called first thing
        /// in TakeDamage, on this creature's landblock thread.
        /// </summary>
        public float ScaleWorldEventProtectedHit(WorldObject source, float amount)
        {
            return ScaleWorldEventProtectedHit(source, amount, ACE.Common.Time.GetUnixTime());
        }

        /// <summary>The cap bucket (see WorldEventHoldRules.CapProtectedHit): health this target may still lose right now, and when it was last topped up.</summary>
        private double worldEventProtectedCapTokens;
        private double worldEventProtectedCapLastAt;

        /// <summary>
        /// <see cref="ScaleWorldEventProtectedHit(WorldObject, float)"/> at a given clock (the test seam):
        /// first the time-based slice (protect.undefendedSeconds), then the per-target rate cap
        /// (protect.minDeathSeconds), each only when its setting is above 0.
        /// </summary>
        public float ScaleWorldEventProtectedHit(WorldObject source, float amount, double now)
        {
            if (!WorldEventProtected || !(amount > 0) || !(source is Creature attacker) || !attacker.IsWorldEventProtectedTargetFor(this))
                return amount;

            var protect = P_WorldEvent?.Composition?.Goal?.Protect;

            if (protect == null)
                return amount;

            var maxHealth = Health.MaxValue;

            if (protect.UndefendedSeconds > 0)
            {
                worldEventProtectedLastHitAt ??= new Dictionary<uint, double>();

                double? last = worldEventProtectedLastHitAt.TryGetValue(attacker.Guid.Full, out var at) ? at : (double?)null;

                worldEventProtectedLastHitAt[attacker.Guid.Full] = now;

                var interval = ACE.Server.WorldEvents.WorldEventHoldRules.HitInterval(last, now);

                amount = ACE.Server.WorldEvents.WorldEventHoldRules.ProtectedHitDamage(maxHealth, interval, protect.UndefendedSeconds);
            }

            if (protect.MinDeathSeconds > 0)
            {
                amount = ACE.Server.WorldEvents.WorldEventHoldRules.CapProtectedHit(amount, maxHealth, protect.MinDeathSeconds,
                    ref worldEventProtectedCapTokens, ref worldEventProtectedCapLastAt, now);
            }

            return amount;
        }

        private bool IsUsableSeekTarget(Creature target)
        {
            return target != null && target.PhysicsObj != null && !target.IsDestroyed && !target.IsDead
                && IsWorldEventProtectedTargetFor(target);
        }
    }
}
