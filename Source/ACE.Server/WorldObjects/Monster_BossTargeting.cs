using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Managers;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// WaffleACE fork, World Events boss targeting and scale (2026-10-08).
    ///
    /// Owner report: world event bosses were frequently stuck running into other characters on the way to a
    /// target that was not the closest one - the authored TargetingTactic (Random / TopDamager / LastDamager
    /// and friends) kept a boss walking through a crowd toward somebody at the back. A creature carrying
    /// <see cref="ForceNearestPlayerTarget"/> instead targets by distance among its (tether filtered) visible
    /// players, re-evaluated every world_events_boss_retarget_seconds, and drops a MoveTo still heading for the
    /// previous target so a new choice takes effect on the next tick.
    ///
    /// Owner ruling (2026-10-08) on how it switches, so it does not flip between two players at similar range:
    ///   1. Engagement lock - a player target the boss is in melee range of (the IsMeleeRange test MeleeReady
    ///      gates every swing on) is kept no matter who else is nearer, until it leaves that range, dies, leaves
    ///      the tether or stops being a candidate. Judged by distance, not combat mode, so a caster boss in
    ///      melee range of its target holds it too.
    ///   2. Hysteresis - otherwise the current player target is kept while it is within
    ///      world_events_boss_retarget_margin metres of the nearest player; past that, the nearest player wins.
    ///   3. Taunt still outranks both, and with no player candidate the boss takes the nearest combat pet.
    ///
    /// Runtime-only, never persisted: WorldEventSpawner sets it on every WorldEventSpawnKind.Boss spawn
    /// before EnterWorld, and nothing else in the server sets it.
    /// </summary>
    partial class Creature
    {
        /// <summary>
        /// When true, FindNextTarget ignores the authored TargetingTactic and picks by distance among Player
        /// candidates under the engagement lock and hysteresis rules in the class remarks (the nearest
        /// non-player candidate - a combat pet - only when no player is a candidate). The Taunt class ability
        /// still overrides it. Runtime-only, never persisted.
        /// </summary>
        public bool ForceNearestPlayerTarget;

        /// <summary>The retail retarget cadence SetNextTargetTime has always used.</summary>
        public const double DefaultRetargetSeconds = 5.0;

        /// <summary>The shipped world_events_boss_retarget_margin, and the fallback for an unusable value.</summary>
        public const double DefaultRetargetMargin = 1.0;

        /// <summary>
        /// The retarget interval for a <see cref="ForceNearestPlayerTarget"/> creature. A non-finite or
        /// non-positive configured value falls back to the retail <see cref="DefaultRetargetSeconds"/> rather
        /// than to zero, which would re-run target selection on every 0.2 s monster tick. Pure; the value
        /// comes from world_events_boss_retarget_seconds.
        /// </summary>
        public static double ResolveBossRetargetSeconds(double configured)
        {
            return double.IsFinite(configured) && configured > 0.0 ? configured : DefaultRetargetSeconds;
        }

        /// <summary>
        /// The hysteresis margin in metres. Zero is valid (switch the moment anyone is strictly nearer); a
        /// non-finite or negative configured value falls back to <see cref="DefaultRetargetMargin"/>. Pure; the
        /// value comes from world_events_boss_retarget_margin.
        /// </summary>
        public static double ResolveBossRetargetMargin(double configured)
        {
            return double.IsFinite(configured) && configured >= 0.0 ? configured : DefaultRetargetMargin;
        }

        /// <summary>
        /// The nearest candidate flagged as a player; when no candidate is a player, the nearest candidate of
        /// any kind; null for an empty list. <see cref="SelectBossTarget{T}"/> with no previous target.
        /// </summary>
        public static T SelectNearestPlayerTarget<T>(IEnumerable<(T Target, float Distance, bool IsPlayer)> candidates) where T : class
        {
            return SelectBossTarget(candidates, null, false, 0f);
        }

        /// <summary>
        /// The <see cref="ForceNearestPlayerTarget"/> target choice. Pure - the caller supplies the distances
        /// and the melee-range verdict, so this never touches physics.
        ///
        /// <paramref name="prev"/> only counts when it is itself a PLAYER candidate in the list (so a dead,
        /// out-of-tether or no-longer-visible target, or a pet, is never held). Then:
        ///   1. <paramref name="prevInMeleeRange"/> - prev is kept outright (engagement lock);
        ///   2. prev is kept while its distance is at most nearest player + <paramref name="margin"/>;
        ///   3. otherwise the nearest player. With no player candidate, the nearest candidate of any kind (a
        ///      combat pet, so the boss still fights). A closer non-player always loses to a farther player.
        /// Ties keep input order.
        /// </summary>
        public static T SelectBossTarget<T>(IEnumerable<(T Target, float Distance, bool IsPlayer)> candidates,
            T prev, bool prevInMeleeRange, float margin) where T : class
        {
            T nearestPlayer = null;
            var nearestPlayerDist = float.MaxValue;

            T nearestAny = null;
            var nearestAnyDist = float.MaxValue;

            var prevIsPlayerCandidate = false;
            var prevDist = float.MaxValue;

            foreach (var (target, distance, isPlayer) in candidates)
            {
                if (target == null)
                    continue;

                if (nearestAny == null || distance < nearestAnyDist)
                {
                    nearestAny = target;
                    nearestAnyDist = distance;
                }

                if (!isPlayer)
                    continue;

                if (nearestPlayer == null || distance < nearestPlayerDist)
                {
                    nearestPlayer = target;
                    nearestPlayerDist = distance;
                }

                if (prev != null && ReferenceEquals(target, prev))
                {
                    prevIsPlayerCandidate = true;
                    prevDist = distance;
                }
            }

            if (nearestPlayer == null)
                return nearestAny;

            if (prevIsPlayerCandidate)
            {
                // 1. engagement lock
                if (prevInMeleeRange)
                    return prev;

                // 2. hysteresis
                if (prevDist <= nearestPlayerDist + margin)
                    return prev;
            }

            return nearestPlayer;
        }

        /// <summary>
        /// The <see cref="ForceNearestPlayerTarget"/> branch of FindNextTarget. <paramref name="visibleTargets"/>
        /// is the already tether-filtered, non-empty candidate list.
        /// </summary>
        private void FindNearestPlayerTarget(List<Creature> visibleTargets)
        {
            var distances = BuildTargetDistance(visibleTargets);

            var prev = AttackTarget as Creature;

            if (prev != null && prev.IsDead)
                prev = null;

            // The engagement lock's range test is IsMeleeRange - GetDistanceToTarget() <= MaxMeleeRange, the
            // same test MeleeReady gates every monster melee swing on - measured against prev while it is still
            // AttackTarget. Distance only, regardless of CurrentAttack, per the owner ruling.
            // Only for a still-valid candidate: a target with no physics object, or one the tether filter or
            // visibility has dropped, must never be range-tested into the lock.
            var prevInMeleeRange = prev is Player prevPlayer && prevPlayer.PhysicsObj != null
                && visibleTargets.Contains(prevPlayer) && IsMeleeRange();

            var margin = (float)ResolveBossRetargetMargin(PropertyManager.GetDouble("world_events_boss_retarget_margin").Item);

            AttackTarget = SelectBossTarget(distances.Select(td => (td.Target, td.Distance, td.Target is Player)),
                prev, prevInMeleeRange, margin);

            AbandonStaleApproach();
        }

        /// <summary>
        /// Cancels a MoveTo/TurnTo still aimed at an object other than the current AttackTarget.
        ///
        /// AttackTarget is a plain field: changing it does not redirect the physics MoveTo that StartTurn
        /// issued toward the previous target (MoveToManager tracks SoughtObjectID on its own), and Monster_Tick
        /// only calls StartTurn again once IsMoving is false. Without this a boss that has just switched to the
        /// closest player keeps running at the old one until that move completes or trips the stuck cancel -
        /// exactly the "running into other characters" behaviour this feature exists to stop.
        ///
        /// Deliberately NOT Creature.CancelMoveTo(): that also calls ResetAttack() and FindNextTarget(), and
        /// pushes NextMoveTime a full second out (same reasoning as CombatPet.CancelPhysicsMoveTo). Awake state
        /// only, so a boss walking home (State.Return, a position MoveTo) is never interrupted.
        /// </summary>
        private void AbandonStaleApproach()
        {
            if (MonsterState != State.Awake || AttackTarget == null)
                return;

            if (!IsMoving && !IsTurning)
                return;

            var moveToManager = PhysicsObj?.MovementManager?.MoveToManager;
            var targetPhysicsId = AttackTarget.PhysicsObj?.ID ?? 0;

            if (moveToManager != null && targetPhysicsId != 0 && moveToManager.SoughtObjectID == targetPhysicsId)
                return;

            if (moveToManager != null)
            {
                moveToManager.CancelMoveTo(WeenieError.ActionCancelled);
                moveToManager.FailProgressCount = 0;
            }

            // a melee approach may have stuck the boss to the old target; a fresh MoveTo would unstick it
            // (MoveToManager.PerformMovement), but a ranged boss turning in place never issues one
            PhysicsObj?.unstick_from_object();

            IsMoving = false;
            IsTurning = false;

            // Stop the run on the clients and resync, so they do not keep dead-reckoning the boss toward the
            // old target for the rest of the leg they were told about (same pair as Creature_ClassAbilityPin).
            // NextMoveTime is deliberately not pushed out: the next Monster_Tick sees !IsTurning && !IsMoving
            // and StartTurn() issues the move toward the new target.
            EnqueueBroadcastMotion(new Motion(CurrentMotionState.Stance, MotionCommand.Ready));
            SendUpdatePosition();
        }

        /// <summary>
        /// The retarget interval SetNextTargetTime uses for this creature: the live tunable for a
        /// <see cref="ForceNearestPlayerTarget"/> creature, the retail 5 s for everything else.
        /// </summary>
        private double GetRetargetSeconds()
        {
            return ResolveRetargetSeconds(ForceNearestPlayerTarget,
                () => PropertyManager.GetDouble("world_events_boss_retarget_seconds").Item);
        }

        /// <summary>
        /// The pure half of <see cref="GetRetargetSeconds"/>: the retail 5 s, without invoking
        /// <paramref name="readConfigured"/> at all, for an unflagged creature (every ordinary monster, so the
        /// hot path never touches PropertyManager); the resolved tunable for a flagged one.
        /// </summary>
        public static double ResolveRetargetSeconds(bool forceNearestPlayerTarget, Func<double> readConfigured)
        {
            if (!forceNearestPlayerTarget)
                return DefaultRetargetSeconds;

            return ResolveBossRetargetSeconds(readConfigured());
        }

        // ---- scale ------------------------------------------------------------------------------------

        /// <summary>
        /// Item guid -> the ObjScale the item carried before a runtime-only scale multiplier was applied to it
        /// (World Events boss scale). Restored by <see cref="RestoreRuntimeItemScale"/> when the item leaves the
        /// creature on death, so a looted weapon does not keep the boss's shrink in its persisted
        /// PropertyFloat.DefaultScale. Null on every creature that was never scaled.
        /// </summary>
        private Dictionary<uint, float?> runtimeItemScaleOriginals;

        /// <summary>
        /// Multiplies this creature's ObjScale, and that of every item it holds (equipped and in its inventory,
        /// since Monster_Tick / Monster_Missile can equip an inventory weapon mid-fight), by
        /// <paramref name="multiplier"/>. Each item keeps its own scale as a ratio to the body. Must run before
        /// EnterWorld so the first CreateObject already carries the new size; PhysicsObj is kept in sync for
        /// anything that already has one. Returns the number of items scaled.
        /// </summary>
        public int ApplyRuntimeScaleMultiplier(float multiplier)
        {
            ObjScale = ScaleBy(ObjScale, multiplier);
            PhysicsObj?.SetScaleStatic(ObjScale.Value);

            var count = 0;

            foreach (var item in EquippedObjects.Values.Concat(Inventory.Values).ToList())
            {
                if (item == null)
                    continue;

                runtimeItemScaleOriginals ??= new Dictionary<uint, float?>();

                if (!runtimeItemScaleOriginals.ContainsKey(item.Guid.Full))
                    runtimeItemScaleOriginals[item.Guid.Full] = item.ObjScale;

                item.ObjScale = ScaleBy(item.ObjScale, multiplier);
                item.PhysicsObj?.SetScaleStatic(item.ObjScale.Value);

                count++;
            }

            return count;
        }

        /// <summary>
        /// Puts back the ObjScale an item carried before <see cref="ApplyRuntimeScaleMultiplier"/> touched it.
        /// A no-op for any item that was never scaled, which is every item on every other creature.
        /// </summary>
        public void RestoreRuntimeItemScale(WorldObject item)
        {
            if (item == null)
                return;

            if (!TryResolveRestoredScale(runtimeItemScaleOriginals, item.Guid.Full, out var original))
                return;

            // a null original goes back through the ObjScale setter as RemoveProperty, so an item that never
            // carried a DefaultScale row does not gain one
            item.ObjScale = original;
            item.PhysicsObj?.SetScaleStatic(original ?? 1.0f);
        }

        /// <summary>
        /// Takes the recorded pre-scale ObjScale for <paramref name="itemGuid"/> out of
        /// <paramref name="originals"/>. False (nothing to restore) for a null map or an item never recorded;
        /// true otherwise, with <paramref name="restored"/> exactly the recorded value - null included, which
        /// means "had no scale of its own", not 1.0. Pure apart from removing the entry it consumes.
        /// </summary>
        public static bool TryResolveRestoredScale(IDictionary<uint, float?> originals, uint itemGuid, out float? restored)
        {
            restored = null;

            if (originals == null)
                return false;

            return originals.Remove(itemGuid, out restored);
        }

        /// <summary>
        /// <c>(current ?? 1.0) * multiplier</c> - the same recipe MlDigsiteSpawner and PuzzleGateManager use.
        /// Pure.
        /// </summary>
        public static float ScaleBy(float? current, float multiplier)
        {
            return (current ?? 1.0f) * multiplier;
        }
    }
}
