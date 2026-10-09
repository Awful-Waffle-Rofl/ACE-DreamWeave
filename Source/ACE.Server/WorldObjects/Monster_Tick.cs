using System;
using System.Diagnostics;

using ACE.Entity.Enum;
using ACE.Server.WorldEvents;

namespace ACE.Server.WorldObjects
{
    partial class Creature
    {
        protected const double monsterTickInterval = 0.2;

        public double NextMonsterTickTime;

        private bool firstUpdate = true;

        /// <summary>
        /// Primary dispatch for monster think
        /// </summary>
        public void Monster_Tick(double currentUnixTime)
        {
            if (IsChessPiece && this is GamePiece gamePiece)
            {
                // faster than vtable?
                gamePiece.Tick(currentUnixTime);
                return;
            }

            if (IsPassivePet && this is Pet pet)
            {
                pet.Tick(currentUnixTime);
                return;
            }

            NextMonsterTickTime = currentUnixTime + monsterTickInterval;

            // Pinning Shot's post-pin movement slow (WaffleACE): restore full speed the moment the window
            // closes. BEFORE the !IsAwake return on purpose - a creature that went to sleep mid-slow must still
            // clear the state, or it would wake up slowed with no timer left to end it.
            //
            // HOT PATH: every creature in every non-dormant landblock, every 0.2s. The guard is one double
            // compare for the overwhelmingly common unslowed case, and nothing else belongs inside it - in
            // particular no PropertyManager read; the factor was resolved once, at pin time.
            if (ClassAbilityMoveSlowUntil != 0.0)
                TickClassAbilityMoveSlow();

            if (!IsAwake)
            {
                if (MonsterState == State.Return)
                    MonsterState = State.Idle;

                if (IsFactionMob || HasFoeType)
                    FactionMob_CheckMonsters();

                return;
            }

            if (IsDead) return;

            // WaffleACE World Events: an objective creature (Rift, Element Portal pillar - PropertyBool 9026
            // WorldEventObjective) is a stationary HP sink. It is Attackable so players can hit it, which makes
            // IsMonster true and lets an attack wake it, but it must never think: no target search, no movement,
            // no attack, no emote-driven turn. TargetingTactic None does NOT achieve that (Monster_Awareness
            // substitutes Random|TopDamager for None), so this is the single place the rule is enforced.
            if (WorldEventObjectiveRules.SkipsMonsterTick(WorldEventObjective)) return;

            // World events sky-drop (WaffleACE, WP-17): a creature still falling in from a world-event sky
            // spawn does nothing at all - no target search, no movement, no attack. Returning HERE rather
            // than later is deliberate: the Sleep() below would clear IsAwake and, without
            // WorldObject_Tick's matching rule, park the creature in mid-air. Creature.OnSkyDropLanded
            // clears the flag once it is down, and the next tick proceeds normally.
            //
            // A dropping creature is normally still ASLEEP and has already returned at the !IsAwake block
            // above; this covers the case where a player woke it by attacking it in the air.
            if (WorldEventSkyDrop) return;

            // WP-17: the first tick after a landing. The physics tick that landed it woke it and raised
            // this flag but deliberately did NOT acquire a target there - see Creature.CompleteSkyDropAcquire
            // for why that call cannot be made from inside UpdateObjectPhysics. From here on this creature
            // is an ordinary monster.
            if (WorldEventSkyDropPendingAcquire)
                CompleteSkyDropAcquire();

            if (EmoteManager.IsBusy) return;

            HandleFindTarget();

            CheckMissHome();    // tickrate?

            if (AttackTarget == null && MonsterState != State.Return)
            {
                // a combat pet with nothing to fight runs back to its owner (CombatPet_Follow) instead of standing
                // where its last fight ended - CombatPet.Sleep() is a no-op, so this branch used to do nothing for
                // pets. Every other creature goes to sleep as before.
                if (this is CombatPet idlePet)
                    idlePet.TickFollowOwner();
                else
                    Sleep();

                return;
            }

            if (MonsterState == State.Return)
            {
                Movement();
                return;
            }

            var combatPet = this as CombatPet;

            var creatureTarget = AttackTarget as Creature;

            if (creatureTarget != null && (creatureTarget.IsDead || (combatPet == null && !IsVisibleTarget(creatureTarget))))
            {
                FindNextTarget();
                return;
            }

            if (firstUpdate)
            {
                if (CurrentMotionState.Stance == MotionStance.NonCombat)
                    DoAttackStance();

                if (IsAnimating)
                {
                    //PhysicsObj.ShowPendingMotions();
                    PhysicsObj.update_object(Location.Instance);
                    return;
                }

                firstUpdate = false;
            }

            // select a new weapon if missile launcher is out of ammo
            var weapon = GetEquippedWeapon();
            /*if (weapon != null && weapon.IsAmmoLauncher)
            {
                var ammo = GetEquippedAmmo();
                if (ammo == null)
                    SwitchToMeleeAttack();
            }*/

            if (weapon == null && CurrentAttack != null && CurrentAttack == CombatType.Missile)
            {
                EquipInventoryItems(true);
                DoAttackStance();
                CurrentAttack = null;
            }

            // decide current type of attack
            if (CurrentAttack == null)
            {
                CurrentAttack = GetNextAttackType();
                MaxRange = GetMaxRange();

                //if (CurrentAttack == AttackType.Magic)
                //MaxRange = MaxMeleeRange;   // FIXME: server position sync
            }

            if (PhysicsObj.IsSticky)
                UpdatePosition(false);

            // get distance to target
            var targetDist = GetDistanceToTarget();
            //Console.WriteLine($"{Name} ({Guid}) - Dist: {targetDist}");

            if (CurrentAttack != CombatType.Missile)
            {
                if (targetDist > MaxRange || (!IsFacing(AttackTarget) && !IsSelfCast()))
                {
                    // turn / move towards
                    if (!IsTurning && !IsMoving)
                        StartTurn();
                    else
                        Movement();
                }
                else
                {
                    // perform attack
                    if (AttackReady())
                    {
                        Attack();

                        // the unreachable-target rule: a pet attacking its target has reached it
                        combatPet?.NotifyAttackOnTarget();
                    }
                }
            }
            else
            {
                if (IsTurning || IsMoving)
                {
                    Movement();
                    return;
                }

                if (!IsFacing(AttackTarget))
                {
                    StartTurn();
                }
                else if (targetDist <= MaxRange)
                {
                    // perform attack
                    if (AttackReady())
                    {
                        Attack();

                        // the unreachable-target rule: a pet attacking its target has reached it
                        combatPet?.NotifyAttackOnTarget();
                    }
                }
                else
                {
                    // monster switches to melee combat immediately,
                    // if target is beyond max range?

                    // should ranged mobs only get CurrentTargets within MaxRange?
                    //Console.WriteLine($"{Name}.MissileAttack({AttackTarget.Name}): targetDist={targetDist}, MaxRange={MaxRange}, switching to melee");
                    TrySwitchToMeleeAttack();
                }
            }

            // pets drawing aggro
            if (combatPet != null)
                combatPet.PetCheckMonsters();
        }
    }
}
