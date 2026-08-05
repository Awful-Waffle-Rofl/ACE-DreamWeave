using System;
using System.Collections.Generic;
using System.Numerics;

using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Physics.Animation;

namespace ACE.Server.WorldObjects
{
    partial class Player
    {
        private float _accuracyLevel;

        public float AccuracyLevel
        {
            get => IsExhausted ? 0.0f : _accuracyLevel;
            set => _accuracyLevel = value;
        }

        public Creature MissileTarget;

        public PowerAccuracy GetAccuracyRange()
        {
            if (AccuracyLevel < 0.33f)
                return PowerAccuracy.Low;
            else if (AccuracyLevel < 0.66f)
                return PowerAccuracy.Medium;
            else
                return PowerAccuracy.High;
        }

        /// <summary>
        /// Called by network packet handler 0xA - GameActionTargetedMissileAttack
        /// </summary>
        /// <param name="targetGuid">The target guid</param>
        /// <param name="attackHeight">The attack height 1-3</param>
        /// <param name="accuracyLevel">The 0-1 accuracy bar level</param>
        public void HandleActionTargetedMissileAttack(uint targetGuid, uint attackHeight, float accuracyLevel)
        {
            //log.Info($"-");

            // Mule (WaffleACE): a mule never fights. OnAttackDone releases the client's attack sequence,
            // matching the other refusal branches below.
            if (MuleBlocked(MuleAction.MissileAttack))
            {
                OnAttackDone();
                return;
            }

            if (CombatMode != CombatMode.Missile)
            {
                log.Warn($"{Name}.HandleActionTargetedMissileAttack({targetGuid:X8}, {attackHeight}, {accuracyLevel}) - CombatMode mismatch {CombatMode}, LastCombatMode: {LastCombatMode}");

                if (LastCombatMode == CombatMode.Missile)
                    CombatMode = CombatMode.Missile;
                else
                {
                    OnAttackDone();
                    return;
                }
            }

            if (IsBusy || Teleporting || suicideInProgress)
            {
                SendWeenieError(WeenieError.YoureTooBusy);
                OnAttackDone();
                return;
            }

            if (IsJumping)
            {
                SendWeenieError(WeenieError.YouCantDoThatWhileInTheAir);
                OnAttackDone();
                return;
            }

            if (PKLogout)
            {
                SendWeenieError(WeenieError.YouHaveBeenInPKBattleTooRecently);
                OnAttackDone();
                return;
            }

            var weapon = GetEquippedMissileWeapon();
            var ammo = GetEquippedAmmo();

            // sanity check
            accuracyLevel = Math.Clamp(accuracyLevel, 0.0f, 1.0f);

            if (weapon == null || weapon.IsAmmoLauncher && ammo == null)
            {
                OnAttackDone();
                return;
            }

            AttackHeight = (AttackHeight)attackHeight;
            AttackQueue.Add(accuracyLevel);

            if (MissileTarget == null)
                AccuracyLevel = accuracyLevel;  // verify

            // get world object of target guid
            var target = CurrentLandblock?.GetObject(targetGuid) as Creature;
            if (target == null || target.Teleporting)
            {
                //log.Warn($"{Name}.HandleActionTargetedMissileAttack({targetGuid:X8}, {AttackHeight}, {accuracyLevel}) - couldn't find creature target guid");
                OnAttackDone();
                return;
            }

            if (Attacking || MissileTarget != null && MissileTarget.IsAlive)
                return;

            if (!CanDamage(target))
            {
                SendTransientError($"You cannot attack {target.Name}");
                OnAttackDone();
                return;
            }

            //log.Info($"{Name}.HandleActionTargetedMissileAttack({targetGuid:X8}, {attackHeight}, {accuracyLevel})");

            AttackTarget = target;
            MissileTarget = target;

            var attackSequence = ++AttackSequence;

            // record stance here and pass it along
            // accounts for odd client behavior with swapping bows during repeat attacks
            var stance = CurrentMotionState.Stance;

            // turn if required
            var rotateTime = Rotate(target);
            var actionChain = new ActionChain();

            var delayTime = rotateTime;
            if (NextRefillTime > DateTime.UtcNow.AddSeconds(delayTime))
                delayTime = (float)(NextRefillTime - DateTime.UtcNow).TotalSeconds;

            actionChain.AddDelaySeconds(delayTime);

            // do missile attack
            actionChain.AddAction(this, () => LaunchMissile(target, attackSequence, stance));
            actionChain.EnqueueChain();
        }

        /// <summary>
        /// Launches a missile attack from player to target
        /// </summary>
        public void LaunchMissile(WorldObject target, int attackSequence, MotionStance stance, bool subsequent = false)
        {
            if (AttackSequence != attackSequence)
                return;

            var weapon = GetEquippedMissileWeapon();
            if (weapon == null || CombatMode == CombatMode.NonCombat)
            {
                OnAttackDone();
                return;
            }

            var ammo = weapon.IsAmmoLauncher ? GetEquippedAmmo() : weapon;
            if (ammo == null)
            {
                OnAttackDone();
                return;
            }

            var launcher = GetEquippedMissileLauncher();

            var creature = target as Creature;
            if (!IsAlive || IsBusy || MissileTarget == null || creature == null || !creature.IsAlive || suicideInProgress)
            {
                OnAttackDone();
                return;
            }

            if (!TargetInRange(target))
            {
                // this must also be sent to actually display the transient message
                SendWeenieError(WeenieError.MissileOutOfRange);

                // this prevents the accuracy bar from refilling when 'repeat attacks' is enabled
                OnAttackDone();

                return;
            }

            var actionChain = new ActionChain();

            if (subsequent && !IsFacing(target))
            {
                var rotateTime = Rotate(target);
                actionChain.AddDelaySeconds(rotateTime);
            }

            // launch animation
            // point of no return beyond this point -- cannot be cancelled
            actionChain.AddAction(this, () => Attacking = true);

            if (subsequent)
            {
                // client shows hourglass, until attack done is received
                // retail only did this for subsequent attacks w/ repeat attacks on
                Session.Network.EnqueueSend(new GameEventCombatCommenceAttack(Session));
            }

            var projectileSpeed = GetProjectileSpeed();

            // get z-angle for aim motion
            var aimVelocity = GetAimVelocity(target, projectileSpeed);

            var aimLevel = GetAimLevel(aimVelocity);

            // calculate projectile spawn pos and velocity
            var localOrigin = GetProjectileSpawnOrigin(ammo.WeenieClassId, aimLevel);

            var velocity = CalculateProjectileVelocity(localOrigin, target, projectileSpeed, out Vector3 origin, out Quaternion orientation, out _);

            //Console.WriteLine($"Velocity: {velocity}");

            if (velocity == Vector3.Zero)
            {
                // pre-check succeeded, but actual velocity calculation failed
                SendWeenieError(WeenieError.MissileOutOfRange);

                // this prevents the accuracy bar from refilling when 'repeat attacks' is enabled
                Attacking = false;
                OnAttackDone();
                return;
            }

            var launchTime = EnqueueMotionPersist(actionChain, aimLevel);

            // launch projectile
            actionChain.AddAction(this, () =>
            {
                // handle self-procs
                TryProcEquippedItems(this, this, true, weapon);

                var sound = GetLaunchMissileSound(weapon);
                EnqueueBroadcast(new GameMessageSound(Guid, sound, 1.0f));

                // stamina usage
                // TODO: ensure enough stamina for attack
                // TODO: verify formulas - double/triple cost for bow/xbow?
                var staminaCost = GetAttackStamina(GetAccuracyRange());
                UpdateVitalDelta(Stamina, -staminaCost);

                var projectile = LaunchProjectile(launcher, ammo, target, origin, orientation, velocity);
                UpdateAmmoAfterLaunch(ammo);

                // PROTOTYPE: multi-shot - damage is still resolved directly (cleave-style) rather than via
                // ProjectileCollisionHelper, so one acquired creature can never block another from being hit.
                // See GetMultiShotTargets. Each extra target gets a real-flight-time cosmetic arrow, and damage
                // is delayed to land when that arrow actually arrives (see LaunchCosmeticMultiShotProjectile's
                // out flightTime) rather than the instant the shot is fired, so it doesn't look/feel like extra
                // targets take damage before the arrow reaches them.
                // PvP exclusion: multi-shot doesn't fire at all if the primary target is a player.
                // Extra arrows come from two stacking sources: a multi-shot weapon and class abilities
                // (e.g. Multishot). One damage multiplier per extra shot: weapon-granted arrows use
                // the weapon's own multiplier, skill-granted arrows each supply their own.
                var shotMultipliers = new List<float>();

                for (var i = 0; i < weapon.MultiShotCount; i++)
                    shotMultipliers.Add(weapon.MultiShotDamageMultiplier);

                AddClassAbilityMissileShots(shotMultipliers);

                // captured so a Double Volley re-fire can repeat the exact same extra-target spread
                var volleyExtraShots = new List<(Creature target, float mult)>();

                if (shotMultipliers.Count > 0 && creature != null && creature is not Player)
                {
                    var multiShotTargets = GetMultiShotTargets(creature, weapon, shotMultipliers.Count);

                    var shotIndex = 0;
                    foreach (var extraTarget in multiShotTargets)
                    {
                        var damageMultiplier = shotMultipliers[shotIndex];
                        shotIndex++;

                        FireMultiShotArrow(launcher, ammo, extraTarget, projectileSpeed, damageMultiplier);
                        volleyExtraShots.Add((extraTarget, damageMultiplier));
                    }
                }

                // class ability: Double Volley - a chance to immediately fire a full second volley at no
                // ammo/stamina cost. The repeat is entirely cleave-style (a cosmetic primary arrow at the
                // main target plus the same extra-target spread), so it never re-enters the attack state
                // machine or consumes resources. PvP-excluded like multi-shot.
                if (creature != null && creature is not Player && TryClassAbilityDoubleVolley())
                {
                    FireMultiShotArrow(launcher, ammo, creature, projectileSpeed, 1.0f);

                    foreach (var (extraTarget, mult) in volleyExtraShots)
                        FireMultiShotArrow(launcher, ammo, extraTarget, projectileSpeed, mult);
                }
            });

            // ammo remaining?
            if (!ammo.UnlimitedUse && (ammo.StackSize == null || ammo.StackSize <= 1))
            {
                actionChain.AddAction(this, () =>
                {
                    Session.Network.EnqueueSend(new GameEventCommunicationTransientString(Session, "You are out of ammunition!"));
                    SetCombatMode(CombatMode.NonCombat);
                    Attacking = false;
                    OnAttackDone();
                });

                actionChain.EnqueueChain();
                return;
            }

            // reload animation
            var animSpeed = GetAnimSpeed();
            var reloadTime = EnqueueMotionPersist(actionChain, stance, MotionCommand.Reload, animSpeed);

            // reset for next projectile
            EnqueueMotionPersist(actionChain, stance, MotionCommand.Ready);
            var linkTime = MotionTable.GetAnimationLength(MotionTableId, stance, MotionCommand.Reload, MotionCommand.Ready);
            //var cycleTime = MotionTable.GetCycleLength(MotionTableId, CurrentMotionState.Stance, MotionCommand.Ready);

            actionChain.AddAction(this, () =>
            {
                if (CombatMode == CombatMode.Missile)
                    EnqueueBroadcast(new GameMessageParentEvent(this, ammo, ACE.Entity.Enum.ParentLocation.RightHand, ACE.Entity.Enum.Placement.RightHandCombat));
            }); 

            actionChain.AddDelaySeconds(linkTime);

            actionChain.AddAction(this, () =>
            {
                Attacking = false;

                if (creature.IsAlive && GetCharacterOption(CharacterOption.AutoRepeatAttacks) && !IsBusy && !AttackCancelled)
                {
                    // client starts refilling accuracy bar
                    Session.Network.EnqueueSend(new GameEventAttackDone(Session));

                    AccuracyLevel = AttackQueue.Fetch();

                    // can be cancelled, but cannot be pre-empted with another attack
                    var nextAttack = new ActionChain();
                    var nextRefillTime = AccuracyLevel;

                    NextRefillTime = DateTime.UtcNow.AddSeconds(nextRefillTime);
                    nextAttack.AddDelaySeconds(nextRefillTime);

                    // perform next attack
                    nextAttack.AddAction(this, () => { LaunchMissile(target, attackSequence, stance, true); });
                    nextAttack.EnqueueChain();
                }
                else
                    OnAttackDone();
            });

            actionChain.EnqueueChain();

            if (UnderLifestoneProtection)
                LifestoneProtectionDispel();
        }

        /// <summary>
        /// PROTOTYPE: spawns a purely visual arrow flying at an extra multi-shot target, and returns it so the
        /// caller can also use it as the damageSource for DamageTarget (it carries ProjectileSource/ProjectileLauncher/
        /// ProjectileAmmo from LaunchProjectile, which DamageEvent.CalculateDamage requires to correctly resolve
        /// missile combat/weapon - see the call site in LaunchMissile). Damage is NOT resolved by this projectile
        /// colliding - ProjectileTarget is cleared (and IsCosmeticProjectile set) right after creation so
        /// ProjectileCollisionHelper never applies a second, redundant hit or a hit/miss message for it, no matter
        /// what it physically collides with (or fails to). Instead the caller delays its own DamageTarget call by
        /// flightTime, so the extra target takes damage when the (visual) arrow actually arrives, not the instant
        /// it's fired. The physics engine's own homing/target tracking (set inside LaunchProjectile, independent of
        /// the WorldObject-level ProjectileTarget field) still points at the real extraTarget, so the flight itself
        /// looks correct.
        /// </summary>
        /// <summary>
        /// Fires one cleave-style multi-shot arrow at a target: spawns the cosmetic projectile and schedules
        /// its delayed DamageTarget so damage lands when the (visual) arrow arrives. Shared by the normal
        /// multi-shot spread and the Double Volley class-ability re-fire. No-op if the projectile can't spawn.
        /// </summary>
        private void FireMultiShotArrow(WorldObject launcher, WorldObject ammo, Creature target, float projectileSpeed, float damageMultiplier)
        {
            // The cosmetic projectile must be spawned BEFORE damage is calculated, and used as the
            // damageSource - DamageEvent.CalculateDamage classifies CombatType.Missile (and resolves
            // Weapon) purely from damageSource.ProjectileSource/ProjectileLauncher being set, which only
            // a real (even if visual-only) projectile object carries.
            var cosmeticProjectile = LaunchCosmeticMultiShotProjectile(launcher, ammo, target, projectileSpeed, out var flightTime);

            if (cosmeticProjectile == null)
                return;

            var impactChain = new ActionChain();
            impactChain.AddDelaySeconds(flightTime);
            impactChain.AddAction(this, () => DamageTarget(target, cosmeticProjectile, damageMultiplier));
            impactChain.EnqueueChain();
        }

        private WorldObject LaunchCosmeticMultiShotProjectile(WorldObject launcher, WorldObject ammo, WorldObject extraTarget, float projectileSpeed, out float flightTime)
        {
            var aimVelocity = GetAimVelocity(extraTarget, projectileSpeed);
            var aimLevel = GetAimLevel(aimVelocity);

            var localOrigin = GetProjectileSpawnOrigin(ammo.WeenieClassId, aimLevel);

            var velocity = CalculateProjectileVelocity(localOrigin, extraTarget, projectileSpeed, out Vector3 origin, out Quaternion orientation, out flightTime);

            if (velocity == Vector3.Zero)
                return null;

            var cosmeticProjectile = LaunchProjectile(launcher, ammo, extraTarget, origin, orientation, velocity);

            if (cosmeticProjectile == null)
                return null;

            cosmeticProjectile.IsCosmeticProjectile = true;
            cosmeticProjectile.ProjectileTarget = null;

            return cosmeticProjectile;
        }

        // TODO: the damage pipeline currently uses the creature ammo instead of the projectile
        // for calculating damage. when the last arrow is launched, the player ammo will be null
        // give projectiles an owner, and have the damage pipeline take the actual damage source object
        // (ie. the arrow-in-flight, or a melee weapon)

        public override float GetAimHeight(WorldObject target)
        {
            switch (AttackHeight.Value)
            {
                case ACE.Entity.Enum.AttackHeight.High: return 1.0f;
                case ACE.Entity.Enum.AttackHeight.Medium: return 2.0f;
                //case AttackHeight.Low: return target.Height;
                case ACE.Entity.Enum.AttackHeight.Low: return 3.0f;
            }
            return 2.0f;
        }

        public override void UpdateAmmoAfterLaunch(WorldObject ammo)
        {
            //if (ammo.UnlimitedUse)
            //    return;

            // hide previously held ammo
            EnqueueBroadcast(new GameMessagePickupEvent(ammo));

            if (ammo.UnlimitedUse)
                return;

            if (ammo.StackSize == null || ammo.StackSize <= 1)
                TryDequipObjectWithNetworking(ammo.Guid, out _, DequipObjectAction.ConsumeItem);
            else
                TryConsumeFromInventoryWithNetworking(ammo, 1);
        }

        public bool TargetInRange(WorldObject target)
        {
            // 2d or 3d distance?
            var dist = Location.DistanceTo(target.Location);

            var maxRange = GetMaxMissileRange();

            return dist <= maxRange;
        }
    }
}
