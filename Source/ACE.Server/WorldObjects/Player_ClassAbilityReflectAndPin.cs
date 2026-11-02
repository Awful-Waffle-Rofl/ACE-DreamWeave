using System;
using System.Numerics;

using ACE.Common;
using ACE.Entity.Enum;
using ACE.Server.ClassAbilities;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.MonsterEffects;
using ACE.Server.MonsterEffects.Effects;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Physics.Extensions;
using ACE.Server.Pvp.Rules;

namespace ACE.Server.WorldObjects
{
    partial class Player
    {
        // The two class-ability overhaul entries that needed a site of their own rather than an existing
        // hook: Reflect (Vanguard T3), whose bounce is dispatched by hand from the two projectile collision
        // handlers (its creature-spell immunity window rides the pre-write hook, and its state lives here), and
        // the player-facing half of Pinning Shot (Archer T2), whose roll rides IOutgoingDamageAbility but
        // whose message needs the private combat-message helper on Player.

        /// <summary>
        /// Reflect (Vanguard T3): rolls whether an incoming PROJECTILE bounces back at whoever fired it.
        /// Returns TRUE when it did, in which case the caller must apply NOTHING of the shot to this player -
        /// no damage, no monster-effect riders, no item procs - and hands the bounce to
        /// <see cref="LaunchReflectedMissile"/> or <see cref="LaunchReflectedSpell"/>.
        ///
        /// CALLED FROM THE TWO SITES A PROJECTILE CAN REACH A PLAYER, which share no common sink:
        /// ProjectileCollisionHelper.OnCollideObject (arrows, crossbow bolts, thrown weapons) and
        /// SpellProjectile.OnCollideObject (war bolts, void projectiles). Both call in once the shot is known
        /// to land on this player and BEFORE its monster-effect riders are dispatched, so a bounced shot's
        /// DoT, debuff and leech never reach the player at all.
        ///
        /// THE BOUNCE RESOLVES AGAINST THE FIRER, owner ruling 2026-10-23: the firer's own rolled attack is
        /// kept and the defender half is resolved against the FIRER's defenses (armor, resistances, shield,
        /// ratings, magic absorb; DamageEvent.CalculateReflectedMissile / SpellProjectile.
        /// CalculateReflectedDamage). A cosmetic copy of the projectile flies back, and the damage, the chat
        /// line and the reflected riders land when it ARRIVES (ApplyReflectBounce).
        ///
        /// PROJECTILES ONLY IS RE-DECIDED HERE, from the attack's own CombatType, rather than being left to
        /// the fact that today's two callers happen to be projectile handlers -
        /// ReflectMagicAbility.IsReflectableCombatType refuses Melee outright. A future site wired to a
        /// melee path would be refused rather than silently widening the ability onto a second, much larger
        /// coverage area on the same roll.
        ///
        /// PvE ONLY: a Player firer is refused, so a reflected projectile never resolves against another
        /// player. Self-damage and a dead firer are refused for the same reason every other dispatch does.
        ///
        /// WHY TWO REFLECTING ACTORS STILL TERMINATE, NOW THAT THE BOUNCE HOLDS NO LATCH. The bounce, and the
        /// Thorns answer below, both reach the firer through Creature.TakeDamage as SECONDARY damage, so they
        /// run the firer's monster-effect incoming filters (a ward absorbs, an immune boss takes nothing) -
        /// which they deliberately skipped while this method held the latch. The only incoming handler that
        /// deals damage back is ReflectEffect, and it answers a DirectHit only, so neither can be answered.
        /// The read-only <see cref="Creature.IsReflectResolving"/> still refuses a bounce attempted while some
        /// reflect is resolving further up this thread, so no bounce can start inside another's resolution.
        ///
        /// THORNS ANSWERS A BOUNCE (owner ruling 2026-10-22), synchronously at the bounce, for a DirectHit
        /// only. <paramref name="origin"/> is REQUIRED, not defaulted, so a dropped argument cannot quietly let
        /// a Secondary spell projectile (proc, Cascade hop, AOE splash, Echo copy) trigger Thorns: a missile
        /// is always DirectHit, a spell projectile passes its own IncomingDamageOrigin.
        /// </summary>
        public bool TryReflectProjectile(Creature firer, CombatType combatType, DamageType damageType, float damage, IncomingDamageOrigin origin)
        {
            // hot path (every incoming projectile): a player with no class abilities at all pays only a
            // cheap dictionary-count check
            if (GetClassAbilityCache().Count == 0)
                return false;

            if (!ReflectMagicAbility.IsReflectableCombatType(combatType))
                return false;

            if (firer == null || PvpClassifier.IsPvp(firer, this) || firer == this || firer.IsDead)
                return false;

            if (damage <= 0.0f)
                return false;

            // a bounce never starts inside another reflect's resolution - see the summary. A read, not a
            // claim: the bounce and its Thorns answer must run the firer's incoming-damage effects.
            if (Creature.IsReflectResolving)
                return false;

            // TryGetClassAbility already gates on class_abilities_enabled and a learned rank
            if (!TryGetClassAbility(ClassAbilityId.ReflectMagic, out var rank))
                return false;

            // Magic Defense is MULTIPLICATIVE on Reflect's own rank bonus, with the added amount capped by
            // class_ability_affinity_chance_cap. The affinity read lives in ReflectMagicAbility.ChanceFor, so
            // ClassAbilityAffinityDeclarationTests finds it in the handler's own file.
            var chance = ReflectMagicAbility.ChanceFor(this, rank);

            if (ThreadSafeRandom.Next(0.0f, 1.0f) > chance)
                return false;

            // Thorns still answers a bounced projectile (owner ruling 2026-10-22): the bounce means the hit
            // never reaches TakeDamage or the magic Thorns sites, so it is wired here. Synchronous, at the
            // bounce, and OUTSIDE any latch (owner ruling 2026-10-23): it reaches the firer through its ordinary
            // TakeDamage as Secondary damage, so a ward absorbs it and an immune boss ignores it, and a monster
            // reflect effect, which answers a DirectHit only, cannot answer it. A Secondary spell projectile
            // (proc, Cascade hop, AOE splash, Echo copy) is refused exactly as ApplyThornsOnMagicHit refuses it.
            // The close-range gate and the dead check are inside ApplyThornsReflect.
            if (origin == IncomingDamageOrigin.DirectHit)
                ApplyThornsReflect(firer, combatType == CombatType.Magic ? GetThornsMagicReflectDamageType(damageType) : damageType, 1.0);

            // The bounce skips this player's TakeDamage / DamageTarget (the caller returns on TRUE before
            // either runs), so the cloak spell proc those sites roll would otherwise never see a reflected
            // hit. Rolled here instead, synchronously at the bounce like the Thorns answer above (not at the
            // cosmetic's arrival), on the damage this player would have taken as a fraction of max health. Same single
            // entry point as every other site, so cooldown, item-level gate, the Cloaked in Power floor and
            // the Taunt hook apply unchanged.
            var reflectCloak = EquippedCloak;

            if (reflectCloak != null && Cloak.HasProcSpell(reflectCloak))
                Cloak.TryProcSpell(this, firer, reflectCloak, damage / Health.MaxValue);

            return true;
        }

        /// <summary>
        /// The longest a bounce waits for its cosmetic projectile to arrive, in seconds. A guard against a
        /// degenerate flight-time estimate rather than a tuning knob - the same order as the multi-shot arrow
        /// precedent (FireMultiShotArrow), whose flights land well inside it.
        /// </summary>
        internal const double MaxReflectBounceDelaySeconds = 3.0;

        /// <summary>
        /// The delay a bounce lands after: the cosmetic's estimated flight time, clamped to
        /// [0, <see cref="MaxReflectBounceDelaySeconds"/>]. A non-finite estimate lands at once.
        /// </summary>
        internal static double ReflectBounceDelay(float flightTime)
        {
            if (float.IsNaN(flightTime) || float.IsInfinity(flightTime) || flightTime <= 0.0f)
                return 0.0;

            return Math.Min(flightTime, MaxReflectBounceDelaySeconds);
        }

        /// <summary>
        /// A bounced MISSILE (arrow, crossbow bolt, thrown weapon), from ProjectileCollisionHelper once
        /// TryReflectProjectile has said yes. Resolves the bounce's damage NOW against the firer's defenses
        /// (DamageEvent.CalculateReflectedMissile), launches a cosmetic copy of <paramref name="incoming"/>
        /// back at the firer, and lands the damage and the reflected riders when it arrives.
        /// <paramref name="incomingEvent"/> is the shot as it was calculated against this player, riders
        /// NOT yet applied.
        /// </summary>
        public void LaunchReflectedMissile(Creature firer, WorldObject incoming, DamageEvent incomingEvent)
        {
            if (firer == null || firer.IsDead || incomingEvent == null)
                return;

            // the debug-damage dump the deferred CalculateDamage held back; the riders it also held back are
            // never applied to this player
            incomingEvent.HandleLogging(firer, incomingEvent.Defender);

            var reflected = DamageEvent.CalculateReflectedMissile(incomingEvent, this);

            var damage = reflected.Damage;
            var damageType = incomingEvent.DamageType;
            var projectileName = string.IsNullOrEmpty(incoming?.Name) ? "projectile" : incoming.Name;

            // what the shot would have dealt THIS player - the figure a mirrored leech sizes itself from
            var riderDamage = incomingEvent.Damage;

            var cosmetic = LaunchReflectCosmeticMissile(firer, incoming, out var flightTime);

            ScheduleReflectBounce(firer, cosmetic != null, flightTime,
                () => ApplyReflectBounce(firer, damageType, damage, projectileName, MonsterEffectTrigger.Hit, damageType, riderDamage));
        }

        /// <summary>
        /// A bounced SPELL projectile (war or void bolt), from SpellProjectile.OnCollideObject once
        /// TryReflectProjectile has said yes, and BEFORE the caster's spell-hit monster effects ran. The
        /// caster's magic-damage ramp is read here (a pure read, the number RampEffect would have applied) and
        /// kept as part of its attack; the rest resolves against the caster's defenses
        /// (SpellProjectile.CalculateReflectedDamage). <paramref name="spellDamage"/> is what the bolt would
        /// have dealt this player before the ramp - only a rider's sizing figure.
        /// </summary>
        public void LaunchReflectedSpell(Creature firer, SpellProjectile incoming, bool critical, float spellDamage)
        {
            if (firer == null || firer.IsDead || incoming?.Spell == null)
                return;

            var ramp = firer.GetMonsterEffectRampMultiplier(MonsterRampAxis.MagicDamage);

            var damage = incoming.CalculateReflectedDamage(firer, critical, ramp);

            var spell = incoming.Spell;

            var cosmetic = LaunchCosmeticSpellProjectile(spell, firer, out var flightTime);

            ScheduleReflectBounce(firer, cosmetic != null, flightTime,
                () => ApplyReflectBounce(firer, spell.DamageType, damage, spell.Name, MonsterEffectTrigger.SpellHit, spell.DamageType, spellDamage));
        }

        /// <summary>
        /// The cosmetic return arrow: a copy of the incoming projectile (its own wcid, the firer's launcher)
        /// flying from this player at the firer, at the firer's own projectile speed. Never damages anything -
        /// see Creature.LaunchProjectile's cosmetic parameter.
        /// </summary>
        private WorldObject LaunchReflectCosmeticMissile(Creature firer, WorldObject incoming, out float flightTime)
        {
            flightTime = 0.0f;

            if (incoming == null || PhysicsObj == null || firer.PhysicsObj == null)
                return null;

            var speed = firer.GetProjectileSpeed();

            var aimVelocity = GetAimVelocity(firer, speed);

            if (!aimVelocity.IsValid())
                return null;

            var aimLevel = GetAimLevel(aimVelocity);

            var localOrigin = GetProjectileSpawnOrigin(incoming.WeenieClassId, aimLevel);

            var velocity = CalculateProjectileVelocity(localOrigin, firer, speed, out Vector3 origin, out Quaternion orientation, out flightTime);

            if (velocity == Vector3.Zero)
                return null;

            return LaunchProjectile(incoming.ProjectileLauncher, incoming, firer, origin, orientation, velocity, cosmetic: true);
        }

        /// <summary>
        /// Lands <paramref name="land"/> when the cosmetic arrives: after its flight time, or at once (with an
        /// impact effect on the firer standing in for the flight) when no cosmetic could be launched. The
        /// chain runs on the FIRER; the arrival re-checks, through <see cref="CanLandReflectBounce"/>, that the
        /// bounce can still land, and is dropped silently when it cannot.
        /// </summary>
        private void ScheduleReflectBounce(Creature firer, bool launched, float flightTime, Action land)
        {
            if (!launched)
                firer.EnqueueBroadcast(new GameMessageScript(firer.Guid, PlayScript.Explode));

            var chain = new ActionChain();
            chain.AddDelaySeconds(launched ? ReflectBounceDelay(flightTime) : 0.0);
            chain.AddAction(firer, () =>
            {
                if (CanLandReflectBounce(firer))
                    land();
            });
            chain.EnqueueChain();
        }

        /// <summary>
        /// Whether a bounce can still land: the firer is alive, this reflecting player is alive, not in the
        /// death process and not teleporting (a dead or departing player must not hit back, nor leech or apply
        /// riders), both are still in the world, and - during the parallel landblock tick - both are in the
        /// same landblock group, so the arrival never writes across threads (the same test
        /// ProjectileCollisionHelper's item-proc site makes).
        /// </summary>
        internal bool CanLandReflectBounce(Creature firer)
        {
            if (firer == null || firer.IsDead)
                return false;

            if (IsDead || IsInDeathProcess || Teleporting)
                return false;

            if (CurrentLandblock == null || firer.CurrentLandblock == null)
                return false;

            if (LandblockManager.CurrentlyTickingLandblockGroupsMultiThreaded && CurrentLandblock.CurrentLandblockGroup != firer.CurrentLandblock.CurrentLandblockGroup)
                return false;

            return true;
        }

        /// <summary>
        /// The arrival of a bounce: applies <paramref name="damage"/> (already resolved against the firer's
        /// defenses) to the firer, reports the damage ACTUALLY applied, then answers the shot's reflected riders
        /// (Creature.ApplyReflectedMonsterEffectRiders: a DoT or debuff lands on the firer, a leech is mirrored
        /// onto this player). Returns the health actually removed.
        ///
        /// SECONDARY DAMAGE through the firer's ordinary TakeDamage, attributed to THIS PLAYER (kill credit,
        /// aggro and DamageHistory work through the normal path, as Thorns does), so the firer's wards, mana
        /// barrier and immunity all apply, and its reflect effect, which answers a DirectHit only, cannot
        /// answer it. Directly callable so a test can drive it - the ActionChain that normally calls it never
        /// runs in the test harness.
        /// </summary>
        internal uint ApplyReflectBounce(Creature firer, DamageType damageType, float damage, string projectileName, MonsterEffectTrigger riderTrigger, DamageType riderDamageType, float riderDamage)
        {
            if (firer == null || firer.IsDead)
                return 0;

            var applied = 0u;

            // captured BEFORE the hit, which can zero the pool as a side effect - the same order as the
            // spell-hit attacker line in SpellProjectile.DamageTarget
            var wardWasUp = false;

            if (damage > 0.0f)
            {
                wardWasUp = firer.TryGetMonsterWardRemaining(out _);

                applied = firer.TakeDamage(this, damageType, damage, false, IncomingDamageOrigin.Secondary);
            }

            var line = FormatReflectBounceLine(firer.Name, projectileName, applied);

            if (wardWasUp)
                line += WardEffect.FormatHitSuffix(firer.TryGetMonsterWardRemaining(out var wardRemaining) ? wardRemaining : 0);

            // null-tolerant only so a session-less player (the unit-test harness) can drive the real arrival;
            // a logged-in player always has both. The same squelch rule Thorns' own line uses.
            if (Session != null && SquelchManager != null && !SquelchManager.Squelches.Contains(firer, ChatMessageType.CombatSelf))
                Session.Network.EnqueueSend(new GameMessageSystemChat(line, ChatMessageType.CombatSelf));

            firer.ApplyReflectedMonsterEffectRiders(this, riderTrigger, riderDamageType, riderDamage);

            return applied;
        }

        /// <summary>The bounce's chat line, from the damage actually applied. Pure, so the text is testable.</summary>
        internal static string FormatReflectBounceLine(string firerName, string projectileName, uint damage)
        {
            return $"You reflect {firerName}'s {projectileName} back at it for {damage:N0} points of damage!";
        }

        /// <summary>
        /// Unix time Reflect's creature-spell immunity window closes at; 0 means it has never armed. A TIME,
        /// the same shape as Adrenaline's window: NEVER PERSISTED (it is under half a second long and resets
        /// naturally when the Player object is rebuilt on login) and mutated only from the pre-write damage
        /// dispatch, which runs on the player's landblock thread, so no locking is needed. No heartbeat entry
        /// and no expiry message - a window this short lapses silently on its next read.
        /// </summary>
        private double reflectImmunityWindowEndTime;

        // ONE DoT HEARTBEAT IS ONE HIT (owner ruling 2026-10-17). EnchantmentManager.HeartBeat_DamageOverTime
        // applies its Undef, Nether and Aetheria ticks as three separate dispatches; while its pass is open,
        // each DoT tick decides against the window as it stood when the pass began
        // (reflectDotPassWindowEnd), and the window arms at most once (reflectDotPassArmed). Same thread and
        // same non-persisted contract as the window itself.
        private bool reflectDotPassActive;
        private double reflectDotPassWindowEnd;
        private bool reflectDotPassArmed;

        /// <summary>Opens a DoT heartbeat pass: snapshots the window so the pass's ticks cannot block each other.</summary>
        public void BeginReflectDotPass()
        {
            reflectDotPassActive = true;
            reflectDotPassWindowEnd = reflectImmunityWindowEndTime;
            reflectDotPassArmed = false;
        }

        /// <summary>Closes the DoT heartbeat pass. Called from a finally.</summary>
        public void EndReflectDotPass()
        {
            reflectDotPassActive = false;
            reflectDotPassArmed = false;
        }

        /// <summary>
        /// Runs one hit through Reflect's immunity window and returns what is left of it. Called from
        /// ReflectMagicAbility.ModifyIncomingDamage with the window length already computed there (the
        /// Magic Defense affinity is read in the handler, not here). The rules are
        /// ReflectMagicAbility.DecideImmunity's; this only owns the state and the chat line, which is sent
        /// once per blocked hit.
        /// </summary>
        public uint ApplyReflectImmunityWindow(uint damage, uint enemySpellShare, double windowSeconds, bool isDamageOverTime)
        {
            ReflectMagicAbility.ImmunityDecision decision;

            if (isDamageOverTime && reflectDotPassActive)
            {
                decision = ReflectMagicAbility.DecideDotPassTick(Time.GetUnixTime(), reflectDotPassWindowEnd, reflectImmunityWindowEndTime,
                    reflectDotPassArmed, enemySpellShare, damage, windowSeconds);

                if (decision.WindowEnd != reflectImmunityWindowEndTime)
                    reflectDotPassArmed = true;
            }
            else
            {
                decision = ReflectMagicAbility.DecideImmunity(Time.GetUnixTime(), reflectImmunityWindowEndTime, enemySpellShare, damage, windowSeconds);
            }

            reflectImmunityWindowEndTime = decision.WindowEnd;

            if (decision.Blocked > 0)
                SendClassAbilityCombatMessage($"Your reflective guard turns aside {decision.Blocked} points of spell damage!");

            return decision.Damage;
        }

        /// <summary>
        /// The player-facing half of a landed Pinning Shot. Separate from the handler only because the
        /// combat-message helper is private to Player; the roll, the affinity and the cap all live in
        /// PinningShotAbility, which is what ClassAbilityAffinityDeclarationTests scans.
        /// </summary>
        public void OnClassAbilityPinApplied(Creature target, double seconds)
        {
            if (target == null)
                return;

            SendClassAbilityCombatMessage($"Your shot pins {target.Name} in place for {seconds:N0} seconds!");
        }
    }
}
