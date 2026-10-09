using System;

using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.MonsterEffects;
using ACE.Server.Network.GameMessages.Messages;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// Helper class for arrows / bolts / thrown weapons
    /// outside of the WorldObject hierarchy
    /// </summary>
    public static class ProjectileCollisionHelper
    {
        public static void OnCollideObject(WorldObject worldObject, WorldObject target)
        {
            if (!worldObject.PhysicsObj.is_active()) return;

            //Console.WriteLine($"Projectile.OnCollideObject - {WorldObject.Name} ({WorldObject.Guid}) -> {target.Name} ({target.Guid})");

            if (worldObject.ProjectileTarget == null || worldObject.ProjectileTarget != target)
            {
                //Console.WriteLine("Unintended projectile target! (should be " + ProjectileTarget.Guid.Full.ToString("X8") + " - " + ProjectileTarget.Name + ")");
                OnCollideEnvironment(worldObject);
                return;
            }

            // take damage
            var sourceCreature = worldObject.ProjectileSource as Creature;
            var sourcePlayer = worldObject.ProjectileSource as Player;
            var targetCreature = target as Creature;

            DamageEvent damageEvent = null;

            // Reflect (Vanguard T3): set when the target player bounced this projectile straight back at its
            // firer. The projectile has then already resolved against the firer, so the player takes nothing
            // here AND the firer's on-hit item procs further down must not fire against the player either -
            // nothing of this shot reached them.
            var reflectedProjectile = false;

            if (targetCreature != null && targetCreature.IsAlive)
            {
                if (sourcePlayer != null)
                {
                    // player damage monster or player
                    damageEvent = sourcePlayer.DamageTarget(targetCreature, worldObject);

                    if (damageEvent != null && damageEvent.HasDamage)
                        worldObject.EnqueueBroadcast(new GameMessageSound(worldObject.Guid, Sound.Collision, 1.0f));
                }
                else if (sourceCreature != null && sourceCreature.AttackTarget != null)
                {
                    // todo: clean this up
                    var targetPlayer = sourceCreature.AttackTarget as Player;

                    damageEvent = DamageEvent.CalculateDamage(sourceCreature, targetCreature, worldObject);

                    // survival arena engagement-stall test: a monster missile reaching a player counts whether it
                    // hit, was evaded or was reflected (see Player.NoteSurvivalEngagement). Keyed on the creature
                    // actually struck, not on targetPlayer above, which is the firer's AttackTarget.
                    (targetCreature as Player)?.NoteSurvivalEngagement(sourceCreature);

                    if (targetPlayer != null)
                    {
                        // Reflect (Vanguard T3): the arrow, bolt or thrown weapon bounces back at whoever
                        // fired it. Rolled only on a hit that would ACTUALLY have landed - a shot the player
                        // evaded, blocked or parried carries no damage to reflect and is left to the
                        // avoidance path below, so reflect never double-dips with the avoidance tier.
                        reflectedProjectile = damageEvent.HasDamage
                            && targetPlayer.TryReflectProjectile(sourceCreature, CombatType.Missile, damageEvent.DamageType, damageEvent.Damage);

                        // monster damage player
                        if (damageEvent.HasDamage && !reflectedProjectile)
                        {
                            targetPlayer.TakeDamage(sourceCreature, damageEvent);

                            // blood splatter?

                            if (damageEvent.ShieldMod != 1.0f)
                            {
                                var shieldSkill = targetPlayer.GetCreatureSkill(Skill.Shield);
                                Proficiency.OnSuccessUse(targetPlayer, shieldSkill, shieldSkill.Current);   // ??
                            }

                            // handle Dirty Fighting
                            if (sourceCreature.GetCreatureSkill(Skill.DirtyFighting).AdvancementClass >= SkillAdvancementClass.Trained)
                                sourceCreature.FightDirty(targetPlayer, damageEvent.Weapon);
                        }
                        else if (!damageEvent.HasDamage && !damageEvent.Blocked && !damageEvent.Parried)
                            // a class-ability block/parry is not an evade (own message, no Defense proficiency).
                            // The !HasDamage term is load bearing now that the branch above can decline a
                            // landed hit: a REFLECTED shot did land, and must not report an evade.
                            targetPlayer.OnEvade(sourceCreature, CombatType.Missile);
                    }
                    else
                    {
                        // monster damage pet
                        if (damageEvent.HasDamage)
                        {
                            targetCreature.TakeDamage(sourceCreature, damageEvent.DamageType, damageEvent.Damage, false, IncomingDamageOrigin.DirectHit);

                            // blood splatter?

                            // handle Dirty Fighting
                            if (sourceCreature.GetCreatureSkill(Skill.DirtyFighting).AdvancementClass >= SkillAdvancementClass.Trained)
                                sourceCreature.FightDirty(targetCreature, damageEvent.Weapon);
                        }

                        if (!(targetCreature is CombatPet))
                        {
                            // faction mobs and foetype
                            sourceCreature.MonsterOnAttackMonster(targetCreature);
                        }
                    }
                }

                // handle target procs
                if (damageEvent != null && damageEvent.HasDamage && !reflectedProjectile)
                {
                    bool threadSafe = true;

                    if (LandblockManager.CurrentlyTickingLandblockGroupsMultiThreaded)
                    {
                        // Ok... if we got here, we're likely in the parallel landblock physics processing.
                        if (worldObject.CurrentLandblock == null || sourceCreature.CurrentLandblock == null || targetCreature.CurrentLandblock == null || worldObject.CurrentLandblock.CurrentLandblockGroup != sourceCreature.CurrentLandblock.CurrentLandblockGroup || sourceCreature.CurrentLandblock.CurrentLandblockGroup != targetCreature.CurrentLandblock.CurrentLandblockGroup)
                            threadSafe = false;
                    }

                    if (threadSafe)
                        // This can result in spell projectiles being added to either sourceCreature or targetCreature landblock.
                        // worldObject is hitting targetCreature, so they should almost always be in the same landblock
                        worldObject.TryProcEquippedItems(sourceCreature, targetCreature, false, worldObject.ProjectileLauncher);
                    else
                    {
                        // sourceCreature and creatureTarget are now in different landblock groups.
                        // What has likely happened is that sourceCreature sent a projectile toward creatureTarget. Before impact, sourceCreature was teleported away.
                        // To perform this fully thread safe, we would enqueue the work onto worldManager.
                        // WorldManager.EnqueueAction(new ActionEventDelegate(() => sourceCreature.TryProcEquippedItems(targetCreature, false)));
                        // But, to keep it simple, we will just ignore it and not bother with TryProcEquippedItems for this particular impact.
                    }
                }
            }

            worldObject.CurrentLandblock?.RemoveWorldObject(worldObject.Guid, showError: !worldObject.PhysicsObj.entering_world);
            worldObject.PhysicsObj.set_active(false);

            worldObject.HitMsg = true;
        }

        public static void OnCollideEnvironment(WorldObject worldObject)
        {
            if (!worldObject.PhysicsObj.is_active()) return;

            // do not send 'Your missile attack hit the environment' messages to player,
            // if projectile is still in the process of spawning into world.
            if (worldObject.PhysicsObj.entering_world)
                return;

            //Console.WriteLine($"Projectile.OnCollideEnvironment({WorldObject.Name} - {WorldObject.Guid})");

            worldObject.CurrentLandblock?.RemoveWorldObject(worldObject.Guid, showError: !worldObject.PhysicsObj.entering_world);
            worldObject.PhysicsObj.set_active(false);

            if (!worldObject.IsCosmeticProjectile)
            {
                if (worldObject.ProjectileSource is Player player)
                {
                    player.Session.Network.EnqueueSend(new GameMessageSystemChat("Your missile attack hit the environment.", ChatMessageType.Broadcast));
                }
                else if (worldObject.ProjectileSource is Creature creature)
                {
                    creature.MonsterProjectile_OnCollideEnvironment();
                }
            }

            worldObject.HitMsg = true;
        }
    }
}
