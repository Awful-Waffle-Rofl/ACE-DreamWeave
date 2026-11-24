using System;

using ACE.Entity.Enum;
using ACE.Server.ClassAbilities;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// The owner's side of Hunter's Echo (Archer T3): the one live Echo, the cooldown stamp, and the cloak absorb.
    /// The Echo itself lives in CombatPet_HuntersEcho.cs; the trigger is HuntersEchoAbility (IItemProcAbility).
    ///
    /// THE CLOAK ABSORB RUNS AT THE ROLL SITE, NOT IN THE PROC HOOK. Player.TakeDamage and SpellProjectile.DamageTarget
    /// roll the cloak spell proc ahead of their health write; on a winning roll they call
    /// <see cref="TryHuntersEchoAbsorb"/>, which summons (or refreshes) the Echo right there and hands it the hit,
    /// and only then zero the hit for the player. Summoning first means a failed placement leaves the player
    /// taking the hit as before - nothing is ever zeroed for an Echo that does not exist. The proc's later
    /// Cloak.CompleteSpellProc reaches HuntersEchoAbility.OnItemProc for the same cloak, which sees
    /// <see cref="huntersEchoAbsorbedCloak"/> and only re-runs the taunt (Taunt rides the same hook and may have
    /// just pulled more monsters), so one proc never triggers the Echo twice.
    /// </summary>
    partial class Player
    {
        /// <summary>The player's live Echo, or null. Runtime only; never persisted.</summary>
        public CombatPet HuntersEcho { get; internal set; }

        /// <summary>Timers.RunningTime of the last summon or refresh (0 = never), for the cooldown tunable.</summary>
        private double huntersEchoLastTrigger;

        /// <summary>
        /// The cloak whose spell proc already summoned the Echo through the absorb on the current hit, so the proc's
        /// own IItemProcAbility dispatch does not trigger it a second time. Cleared by that dispatch, or by
        /// <see cref="ClearHuntersEchoAbsorb"/> when the hit returns before completing its proc.
        /// </summary>
        private WorldObject huntersEchoAbsorbedCloak;

        /// <summary>
        /// Whether a cloak spell proc on a hit from <paramref name="source"/> may hand that hit to the Echo, decided
        /// BEFORE the roll (it widens the roll gate to a hit that would kill). The ability is held, class abilities,
        /// the ability and the absorb are all enabled, the attacker is a live PvE enemy (never a player), the cooldown
        /// is ready and the player stands in a landblock the Echo can be placed in. NEVER for a hazard hit
        /// (<paramref name="isHazard"/>, from HazardContext.HitPlayers through Player.TakeDamage): owner ruling R3 is that
        /// no ward soaks a hazard, and an Echo absorb is a ward in that sense.
        ///
        /// ORDER: the hazard flag and the ability-held lookup (a dictionary read) run before any setting lookup, since
        /// this runs on every incoming hit taken with a spell-proc cloak equipped.
        /// </summary>
        public bool CanReserveHuntersEchoAbsorb(WorldObject source, bool isHazard)
        {
            if (isHazard)
                return false;

            if (GetClassAbilityRank(ClassAbilityId.HuntersEcho) <= 0)
                return false;

            if (!PropertyManager.GetBool("class_ability_huntersecho_absorb_enabled").Item)
                return false;

            if (!CanTriggerHuntersEcho())
                return false;

            return ClassAbilityAttackerFilter.EnemyCreature(source, this) != null && !(source is Player) && !(source is CombatPet);
        }

        /// <summary>
        /// The trigger gate shared by every path: the ability held (TryGetClassAbility also honours
        /// class_abilities_enabled and suppression), the ability enabled, a placeable spot, a missile weapon wielded (in
        /// any combat mode), and the cooldown ready.
        /// </summary>
        private bool CanTriggerHuntersEcho()
        {
            if (!PropertyManager.GetBool("class_ability_huntersecho_enabled").Item)
                return false;

            if (!TryGetClassAbility(ClassAbilityId.HuntersEcho, out var rank) || rank <= 0)
                return false;

            // IsLoggingOut: a cloak proc during the logout animation must not spawn a fresh Echo after logout has
            // already ended the old one (Player.cs, EndHuntersEcho("ownerlogout"))
            if (IsDead || Teleporting || IsLoggingOut || CurrentLandblock == null || Location == null)
                return false;

            // owner rulings 2026-11-11: only while holding a missile weapon, and "Bow in peace mode also counts" - a
            // missile weapon wielded, any combat mode. Swapping weapons afterwards does not end the Echo: it keeps the
            // weapon it was summoned with (CaptureHuntersEchoWeapons). Read with the Echo weapon scope suspended, so
            // a trigger from inside an Echo hit judges what the owner REALLY holds, never the Echo's bow
            if (GetEquippedMissileWeaponIgnoringHuntersEchoScope() == null)
                return false;

            return HuntersEchoMath.CooldownReady(Timers.RunningTime, huntersEchoLastTrigger,
                PropertyManager.GetDouble("class_ability_huntersecho_cooldown_seconds").Item);
        }

        /// <summary>
        /// A cloak spell proc's absorb, from the roll site: summons or refreshes the Echo and writes
        /// <paramref name="amount"/> (the hit as the player would have taken it, already mitigated) to the Echo.
        /// Returns TRUE only when an Echo now exists and took the hit; the caller then zeroes the hit for the player.
        /// The caller has already checked <see cref="CanReserveHuntersEchoAbsorb"/> and won the roll.
        /// </summary>
        public bool TryHuntersEchoAbsorb(WorldObject source, DamageType damageType, uint amount, WorldObject cloak)
        {
            var echo = TriggerHuntersEcho(absorbed: true);

            if (echo == null)
                return false;

            echo.ApplyHuntersEchoAbsorbedHit(source, damageType, amount);

            huntersEchoAbsorbedCloak = cloak;

            return true;
        }

        /// <summary>
        /// Called from HuntersEchoAbility.OnItemProc: TRUE when this proc already summoned the Echo through the absorb
        /// (and clears the mark), so the hook only re-runs the taunt.
        /// </summary>
        internal bool ConsumeHuntersEchoAbsorb(WorldObject procSource)
        {
            if (huntersEchoAbsorbedCloak == null || !ReferenceEquals(huntersEchoAbsorbedCloak, procSource))
                return false;

            huntersEchoAbsorbedCloak = null;
            return true;
        }

        /// <summary>Drops the absorb mark when a hit returns without completing its cloak proc.</summary>
        internal void ClearHuntersEchoAbsorb() => huntersEchoAbsorbedCloak = null;

        /// <summary>The accuracy bar of this player's most recent missile shot this session, or null (none yet). Runtime only.</summary>
        private float? lastMissileShotAccuracy;

        /// <summary>The reload animation speed of this player's most recent missile shot this session, or null. Runtime only.</summary>
        private float? lastMissileShotAnimSpeed;

        /// <summary>
        /// While an Echo arrow resolves through <see cref="DamageTargetAsHuntersEcho"/>: the accuracy bar it was fired
        /// with, which <see cref="AccuracyLevel"/> returns instead of the live bar. Null at every other time.
        /// </summary>
        private float? huntersEchoAccuracyOverride;

        /// <summary>
        /// Called by Player.LaunchMissile for every shot that reaches its reload: the bar it was fired at and the speed it
        /// reloads at. Two field writes, no setting read, so it is safe on every shot of every player.
        /// </summary>
        internal void RecordMissileShotForHuntersEcho(float accuracyLevel, float animSpeed)
        {
            lastMissileShotAccuracy = accuracyLevel;
            lastMissileShotAnimSpeed = animSpeed;
        }

        /// <summary>
        /// The shot an Echo named <paramref name="echoName"/> takes from this player now (at spawn and at refresh): the
        /// most recent missile shot's bar and reload speed, or, with none this session, a full bar
        /// (HuntersEchoMath.DefaultShotAccuracyLevel) at the speed this player would reload the bow it holds now.
        /// </summary>
        internal HuntersEchoShot CaptureHuntersEchoShot(string echoName)
        {
            var liveAnimSpeed = lastMissileShotAnimSpeed == null ? GetAnimSpeed() : 0.0f;

            return HuntersEchoShot.Resolve(echoName, lastMissileShotAccuracy, lastMissileShotAnimSpeed, liveAnimSpeed);
        }

        /// <summary>
        /// One Echo arrow's hit through this player's own <see cref="DamageTarget"/>, with <see cref="AccuracyLevel"/>
        /// reading <paramref name="accuracyLevel"/> (the bar the arrow was fired with) for the duration of the call, so
        /// its to-hit (GetAccuracyMod via GetEffectiveAttackSkill) and the bar-gated Recklessness term are the snapshot's,
        /// never the owner's live bar. The weapon set the arrow was fired with (<paramref name="weapons"/>) answers every
        /// weapon getter for the same call (<see cref="WithHuntersEchoWeapons{T}"/>), so the hit is the summon-time
        /// weapon's whatever the owner holds now. Both restored in a finally. Runs on this player's own queue (the Echo's
        /// MultiShotImpactActor), the same thread every other AccuracyLevel reader runs on.
        /// </summary>
        internal DamageEvent DamageTargetAsHuntersEcho(Creature target, WorldObject projectile, float damageMultiplier, bool primaryTarget, float accuracyLevel, HuntersEchoWeapons weapons)
        {
            var previous = huntersEchoAccuracyOverride;
            huntersEchoAccuracyOverride = accuracyLevel;

            try
            {
                return WithHuntersEchoWeapons(weapons, () => DamageTarget(target, projectile, damageMultiplier, primaryTarget));
            }
            finally
            {
                huntersEchoAccuracyOverride = previous;
            }
        }

        /// <summary>
        /// Runs <paramref name="action"/> with this player's weapon getters answering from <paramref name="weapons"/>
        /// (Creature.HuntersEchoWeaponScope), restored in a finally. A null set runs it unscoped.
        /// </summary>
        internal T WithHuntersEchoWeapons<T>(HuntersEchoWeapons weapons, Func<T> action)
        {
            if (weapons == null)
                return action();

            var previous = EnterHuntersEchoWeaponScope(weapons);

            try
            {
                return action();
            }
            finally
            {
                RestoreHuntersEchoWeaponScope(previous);
            }
        }

        /// <summary>
        /// The owner's REAL missile weapon, launcher and ammo-slot item now (at spawn and at refresh, both of which the
        /// trigger gate only allows with a missile weapon wielded), or null with none. Read with any Echo weapon scope
        /// suspended, so a refresh from inside an Echo hit takes what the owner really holds.
        /// </summary>
        internal HuntersEchoWeapons CaptureHuntersEchoWeapons() => WithoutHuntersEchoWeaponScope(() =>
        {
            var weapon = GetEquippedMissileWeapon();

            if (weapon == null)
                return null;

            return new HuntersEchoWeapons(weapon, GetEquippedMissileLauncher(), weapon.IsAmmoLauncher ? GetEquippedAmmo() : null);
        });

        /// <summary>
        /// The combat mode the Recklessness gates read (GetRecklessnessMod, IsRecklessAttackQualifying): inside an Echo
        /// volley or arrow (HuntersEchoWeaponScope) the attack is a missile attack, exactly as if the owner were in
        /// missile mode (orchestrator ruling 2026-11-11: the owner's peace mode must not zero an Echo hit's
        /// Recklessness); otherwise the owner's own combat mode.
        /// </summary>
        internal CombatMode RecklessnessCombatMode => HuntersEchoWeaponScope != null ? CombatMode.Missile : CombatMode;

        /// <summary>
        /// Whether <paramref name="item"/> is still this player's: not destroyed, and wielded by them or anywhere in their
        /// inventory tree (main pack and side packs). A snapshot weapon that fails this stops the Echo FIRING; the Echo
        /// itself lives on (owner ruling 2026-11-11 leaves the Echo alive across weapon swaps).
        /// </summary>
        internal bool OwnsHuntersEchoItem(WorldObject item)
        {
            if (item == null || item.IsDestroyed)
                return false;

            return ReferenceEquals(GetEquippedItem(item.Guid), item) || ReferenceEquals(GetInventoryItem(item.Guid), item);
        }

        /// <summary>
        /// Summons the Echo, or refreshes the live one (health, defenses, timer, taunt). A live Echo on another landblock
        /// is ended and a new one summoned here. Returns the Echo, or null when the gate refused or it could not be
        /// placed. Sends the owner's chat line.
        /// </summary>
        internal CombatPet TriggerHuntersEcho(bool absorbed)
        {
            if (!CanTriggerHuntersEcho())
                return null;

            var seconds = HuntersEchoAbility.DurationFor(this);

            if (seconds <= 0.0)
                return null;

            var echo = HuntersEcho;

            // a dead or destroyed Echo, or one the owner can no longer stand on (another landblock or instance), is
            // replaced; a live one in the owner's landblock and instance is refreshed AND moved back onto the owner
            // (HuntersEchoMath.RefreshAction), so it never absorbs a hit standing where the owner used to be
            if (echo != null && (echo.IsDestroyed || echo.IsDead
                || HuntersEchoMath.RefreshAction(ReferenceEquals(echo.CurrentLandblock, CurrentLandblock),
                    echo.Location != null && Location != null && echo.Location.Instance == Location.Instance) == EchoRefresh.Replace))
            {
                QueueEndHuntersEcho(echo, "replaced");
                echo = null;
            }

            int taunted = 0;
            var refreshed = echo != null;

            if (refreshed)
            {
                // health, defenses, timer and taunt INLINE, so an absorbed hit lands on the refreshed Echo
                taunted = echo.RefreshHuntersEcho(this, seconds);

                // the move back onto the owner is QUEUED on the Echo's own action queue, never run here: this can be
                // inside a projectile-collision callback during the landblock physics pass (SpellProjectile.DamageTarget
                // via OnCollideObject in Landblock.TickPhysics), and a physics teleport there is the pet-teleport
                // precedent's reason for running on the pet's own queue. A move the physics refuses leaves the live Echo
                // where it stands (RepositionHuntersEcho returns false and nothing is replaced).
                var refreshedEcho = echo;
                refreshedEcho.EnqueueAction(new ActionEventDelegate(() =>
                {
                    if (!refreshedEcho.IsDestroyed && ReferenceEquals(HuntersEcho, refreshedEcho) && ReferenceEquals(refreshedEcho.CurrentLandblock, CurrentLandblock))
                        refreshedEcho.RepositionHuntersEcho(this);
                }));
            }
            else
            {
                echo = CombatPet.SpawnHuntersEcho(this, seconds);

                if (echo == null)
                    return null;

                HuntersEcho = echo;

                taunted = echo.TauntForHuntersEcho(this);
            }

            huntersEchoLastTrigger = Timers.RunningTime;

            CombatPet.NotifyHuntersEcho(this, refreshed, absorbed, taunted);

            return echo;
        }

        /// <summary>Ends the live Echo, if any, with <paramref name="reason"/> (logout).</summary>
        internal void EndHuntersEcho(string reason)
        {
            var echo = HuntersEcho;

            if (echo != null)
                QueueEndHuntersEcho(echo, reason);
        }

        /// <summary>
        /// Ends <paramref name="echo"/> from the OWNER's thread. The owner's link is dropped here and now (so this
        /// player never touches it again and a new Echo can take the slot), and the destroy runs on the Echo's OWN
        /// action queue, since the Echo may sit in another landblock group (the replace path is exactly that case).
        ///
        /// WHY IT CANNOT BE LOST. WorldObject.EnqueueAction hands the action to the Echo's landblock queue while it has a
        /// CurrentLandblock, and that queue is drained on the Echo's own tick whether or not the owner is still in the
        /// world, so a logout does not strand it. Two situations drop it, and both are covered: a creature with no
        /// CurrentLandblock has its action discarded, so that case ends the Echo directly (it is in no landblock, so no
        /// tick can race it); and should the queued action somehow never run, the Echo's own tick ends it as
        /// "superseded" on its next pass (HuntersEchoEndReason: it is no longer its owner's HuntersEcho), with the
        /// pet Lifespan as the last backstop.
        /// </summary>
        internal void QueueEndHuntersEcho(CombatPet echo, string reason)
        {
            if (ReferenceEquals(HuntersEcho, echo))
                HuntersEcho = null;

            if (echo == null || echo.IsDestroyed)
                return;

            if (echo.CurrentLandblock == null)
            {
                echo.EndHuntersEcho(reason);
                return;
            }

            echo.EnqueueAction(new ActionEventDelegate(() => echo.EndHuntersEcho(reason)));
        }
    }
}
