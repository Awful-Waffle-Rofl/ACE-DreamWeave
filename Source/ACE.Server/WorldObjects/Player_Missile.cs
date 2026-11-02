using System;
using System.Collections.Generic;
using System.Numerics;

using ACE.Entity.Enum;
using ACE.Server.ClassAbilities;
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

            // Battleground spawn protection ends when the shot starts, not only when it connects.
            EndPvpSpawnProtection();

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

                // Pinning Shot: ONE roll for the whole volley (primary, extras, overflow arrows and any Double
                // Volley re-fire), carried on each projectile so no arrow re-rolls. Null = not learned.
                var volleyPinRoll = RollClassAbilityVolleyPin();

                var projectile = LaunchProjectile(launcher, ammo, target, origin, orientation, velocity, volleyPinRoll: volleyPinRoll);
                UpdateAmmoAfterLaunch(ammo);

                // PvP conduct: a shot at an enemy is activity even if the arrow misses (a hit also stamps at the damage gate)
                // (LaunchProjectile can return null after the ammo and stamina are spent; nothing was fired then)
                if (projectile != null)
                    StampPvpAttackAttempt(target);

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
                var shotMultipliers = WeaponMultiShotMultipliers(weapon);

                AddClassAbilityMissileShots(shotMultipliers);

                // captured so a Double Volley re-fire can repeat the exact same extra-target spread
                var volleyExtraShots = new List<(Creature target, float mult, bool isPrimary)>();

                // Every cleave-style arrow at the primary (overflow arrows, the Double Volley re-fire) resolves
                // damage directly and its cosmetic projectile never collides, so nothing physical stops it at a
                // wall. Gate them on the same line-of-sight test GetMultiShotTargets applies to secondary
                // targets; the real primary projectile above still collides normally either way. Evaluated
                // lazily and at most once: the sight test builds a physics object and runs a transition, and
                // an ordinary shot with no cleave arrows must not pay for it.
                var cleaveVolley = creature != null && creature is not Player;
                bool? primaryInSightCache = null;
                bool PrimaryInSight() => primaryInSightCache ??= IsDirectVisible(creature);

                // the extra arrows themselves (secondary targets, overflow onto the primary, the sight gate)
                // are the Creature-level volley a combat pet also fires - see Creature.FireMultiShotVolley
                if (shotMultipliers.Count > 0 && cleaveVolley)
                    volleyExtraShots = FireMultiShotVolley(launcher, ammo, weapon, creature, projectileSpeed, shotMultipliers, PrimaryInSight, volleyPinRoll);

                // class ability: Double Volley - a chance to immediately fire a full second volley at no
                // ammo/stamina cost. The repeat is entirely cleave-style (a cosmetic primary arrow at the
                // main target plus the same extra-target spread), so it never re-enters the attack state
                // machine or consumes resources. PvP-excluded like multi-shot.
                if (cleaveVolley && TryClassAbilityDoubleVolley())
                {
                    // Double Volley's re-fire at creature is still the player's real target, so it records normally.
                    // The re-fire shares the volley's single Pinning Shot roll (owner ruling), as do its overflow arrows.
                    // Out of sight, only the re-fire at the primary is dropped; volleyExtraShots already holds no
                    // overflow arrows then, and its secondary targets each passed their own sight check.
                    if (PrimaryInSight())
                        FireMultiShotArrow(launcher, ammo, creature, projectileSpeed, 1.0f, primaryTarget: true, volleyPinRoll: volleyPinRoll);

                    foreach (var (extraTarget, mult, isPrimary) in volleyExtraShots)
                        // same spread as above - secondary extras must not steal the pet's assist target; an
                        // overflow arrow at the primary is the real target and records normally.
                        FireMultiShotArrow(launcher, ammo, extraTarget, projectileSpeed, mult, primaryTarget: isPrimary, volleyPinRoll: volleyPinRoll);
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
        /// A multi-shot arrow (Creature.FireMultiShotArrow) arriving: resolved through the player's own
        /// DamageTarget, exactly the call the arrow always made before the volley was lifted to Creature.
        /// </summary>
        protected override void ResolveMultiShotArrowImpact(Creature target, WorldObject cosmeticProjectile, float damageMultiplier, bool primaryTarget)
        {
            DamageTarget(target, cosmeticProjectile, damageMultiplier, primaryTarget);
        }

        // TODO: the damage pipeline currently uses the creature ammo instead of the projectile
        // for calculating damage. when the last arrow is launched, the player ammo will be null
        // give projectiles an owner, and have the damage pipeline take the actual damage source object
        // (ie. the arrow-in-flight, or a melee weapon)

        public override float GetAimHeight(WorldObject target)
        {
            // AttackHeight is null for a player who has not attacked since login, and a Reflect return arrow
            // is aimed from exactly such a player - Medium, the same default DamageEvent applies
            switch (AttackHeight ?? ACE.Entity.Enum.AttackHeight.Medium)
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
