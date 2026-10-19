using System;
using System.Collections.Generic;

using ACE.Entity.Enum;
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

        /// <summary>Runtime-only, Hold breach waves: the one target this seeker must go for while it stands (0 = none).</summary>
        public uint WorldEventSeekForcedGuid { get; set; }

        /// <summary>
        /// Runtime-only, Hold mixed waves: this seeker prefers any other target over this one (the breach squad's),
        /// so the rest of the wave spreads over the other targets. Taken only when nothing else stands. 0 = none.
        /// </summary>
        public uint WorldEventSeekAvoidGuid { get; set; }

        /// <summary>
        /// Runtime-only, Hold protect.showHealthBar: the client is told this protected target is attackable, so it
        /// draws a health bar (and a red name). Server-side Attackable stays false and every player and pet attack
        /// is still refused (Player.CanDamage, CheckPKStatusVsTarget, Creature.CanDamage).
        /// </summary>
        public bool WorldEventShowHealthBar { get; set; }

        /// <summary>The Attackable bit the CLIENT is sent: true for a protected target that shows its health bar.</summary>
        public override bool ClientAttackable => base.ClientAttackable || (WorldEventProtected && WorldEventShowHealthBar);

        /// <summary>Runtime-only, Hold finale champion: this seeker always picks the healthiest target.</summary>
        public bool WorldEventSeekHealthiest { get; set; }

        /// <summary>
        /// Runtime-only, Hold hunter waves: this monster ignores the targets and hunts players, a player who has
        /// recently healed a target first. Never a seeker.
        /// </summary>
        public bool WorldEventHunter { get; set; }

        /// <summary>Runtime-only, Hold runner waves: multiplies this monster's run rate (GetRunRate). 1 for everyone else.</summary>
        public float WorldEventRunRateMult { get; set; } = 1.0f;

        /// <summary>
        /// Runtime-only, Hold runner waves: multiplies this monster's melee swing speed (and divides the pause
        /// between swings), and speeds its time-based damage to a protected target by the same factor. 1 for
        /// everyone else.
        /// </summary>
        public float WorldEventAttackSpeedMult { get; set; } = 1.0f;

        /// <summary>
        /// Runtime-only, Hold ranged waves: the distance in metres this monster stops at from its target and
        /// attacks from, when it has a ranged attack (TickWorldEventRangedHold). 0 for everyone else.
        /// </summary>
        public float WorldEventHoldDistance { get; set; }

        /// <summary>Runtime-only, Hold finale: this monster is the finale boss (bigger, tougher, its effect replayed).</summary>
        public bool WorldEventFinaleBoss { get; set; }

        /// <summary>
        /// Runtime-only, Hold waves: the role whose settings tune this monster (world_events_hold_&lt;role&gt;_*): melee, runner,
        /// ranged, caster or corrupted. None for everything else (the finale boss has its own).
        /// </summary>
        public ACE.Server.WorldEvents.HoldDamageRole WorldEventHoldRole { get; set; }

        /// <summary>
        /// This monster's hits on a player are multiplied by this (world_events_hold_&lt;role&gt;_player_damage_mult and the
        /// boss settings; 1 for any creature of no World Event). Read on every hit, so a change applies live.
        /// </summary>
        public double WorldEventPlayerDamageMult => P_WorldEvent == null ? 1.0 : ACE.Server.WorldEvents.HoldSettings.PlayerDamageMultFor(this);

        /// <summary>This monster's hits on a knight are multiplied by this (world_events_hold_&lt;role&gt;_npc_damage_mult; live), before the cap.</summary>
        public double WorldEventNpcDamageMult => P_WorldEvent == null ? 1.0 : ACE.Server.WorldEvents.HoldSettings.NpcDamageMultFor(this);

        /// <summary>
        /// Runtime-only, Hold finale boss (protect.bossTargetsProtected): the player a damage pull holds it on, until
        /// <see cref="WorldEventBossPullUntil"/> (unix time). Set by WorldEvent.TickHoldFinaleBoss on this boss's landblock thread.
        /// </summary>
        public Player WorldEventBossPullTarget { get; set; }

        /// <summary>When the damage pull ends (unix time; 0 = none).</summary>
        public double WorldEventBossPullUntil { get; set; }

        /// <summary>Recent player damage to this finale boss: when, whose (a pet's counts for its owner), how much. Guarded by itself.</summary>
        private readonly List<(double At, uint Player, double Amount)> worldEventBossDamage = new List<(double, uint, double)>();

        /// <summary>The longest window kept in <see cref="worldEventBossDamage"/>, in seconds.</summary>
        private const double WorldEventBossDamageKeepSeconds = 30;

        /// <summary>
        /// Records a player's (or their pet's) damage to this finale boss, for the pull (WorldEventCombatCreditHook, every
        /// damaging write). A no-op for every creature that is not a finale boss. Any thread.
        /// </summary>
        public void NoteWorldEventBossDamage(WorldObject attacker, uint amount, double now)
        {
            if (!WorldEventFinaleBoss || amount == 0)
                return;

            var player = attacker as Player ?? (attacker as Pet)?.P_PetOwner;

            if (player == null)
                return;

            lock (worldEventBossDamage)
            {
                worldEventBossDamage.RemoveAll(e => now - e.At > WorldEventBossDamageKeepSeconds);
                worldEventBossDamage.Add((now, player.Guid.Full, amount));
            }
        }

        /// <summary>A copy of the recent damage log (any thread).</summary>
        public List<(double At, uint Player, double Amount)> WorldEventBossDamageSnapshot()
        {
            lock (worldEventBossDamage)
                return new List<(double, uint, double)>(worldEventBossDamage);
        }

        /// <summary>
        /// Who holds this seeker away from the protected targets right now: a live taunt (ApplyTaunt), else a live finale
        /// boss damage pull. Null when nothing does, which is every seeker but a taunted or pulled one.
        /// </summary>
        public Creature WorldEventSeekerHeldBy(double now)
        {
            if (TauntTarget != null && ACE.Server.Entity.Timers.RunningTime < TauntExpiry && !TauntTarget.IsDead)
                return TauntTarget;

            if (WorldEventBossPullTarget is { } pull && now < WorldEventBossPullUntil && !pull.IsDead && !pull.Teleporting && pull.PhysicsObj != null)
                return pull;

            return null;
        }

        /// <summary>Runtime-only, Hold mixed waves: a corrupted priority target (protect.corrupted).</summary>
        public bool WorldEventCorrupted { get; set; }

        /// <summary>
        /// Runtime-only, Hold corrupted monsters: the client palette laid over this creature's whole palette
        /// (protect.corrupted.tintPalette; see WorldEventTint). 0 for every other creature. Set before EnterWorld.
        /// </summary>
        public uint WorldEventTintPalette { get; set; }

        /// <summary>Runtime-only, Hold mixed waves: this ranged monster carries the run's ward (protect.ward).</summary>
        public bool WorldEventWarded { get; set; }

        /// <summary>When the ward, once shattered, comes back (unix time; 0 = never shattered).</summary>
        public double WorldEventWardDownUntil { get; set; }

        /// <summary>True from a shatter until the restore effect has been played (WorldEvent's effect tick).</summary>
        public bool WorldEventWardRestorePending { get; set; }

        /// <summary>Whether this monster's ward is up at <paramref name="now"/>.</summary>
        public bool IsWorldEventWardUp(double now) => WorldEventWarded && now >= WorldEventWardDownUntil;

        /// <summary>
        /// A hit of <paramref name="kind"/> on this monster, through its Hold ward (protect.ward): cut while the ward
        /// is up, and a breaking kind shatters it for protect.ward.shatterSeconds with the break effect. Returns the
        /// damage to apply; unchanged for every creature without a ward. Called on this creature's landblock thread
        /// from DamageEvent (melee, missile), SpellProjectile and life magic (magic).
        /// </summary>
        public float ApplyWorldEventWard(WorldEvents.Defs.WorldEventCombatKinds kind, float damage)
        {
            return ApplyWorldEventWard(kind, damage, ACE.Common.Time.GetUnixTime());
        }

        /// <summary><see cref="ApplyWorldEventWard(WorldEvents.Defs.WorldEventCombatKinds, float)"/> at a given clock (the test seam).</summary>
        public float ApplyWorldEventWard(WorldEvents.Defs.WorldEventCombatKinds kind, float damage, double now)
        {
            if (!WorldEventWarded)
                return damage;

            var ward = P_WorldEvent?.Composition?.Goal?.Protect?.Ward;

            if (ward == null)
                return damage;

            var result = WorldEvents.WorldEventHoldRules.WardHit(damage, kind, IsWorldEventWardUp(now), ward.Reduction,
                ward.ReducesKinds, ward.BreakOnKinds, out var outcome);

            if (outcome == WorldEvents.WorldEventHoldRules.WardOutcome.Shattered)
            {
                WorldEventWardDownUntil = now + Math.Clamp(ward.ShatterSeconds, 1, 120);
                WorldEventWardRestorePending = true;

                if (WorldEvents.WorldEventHoldRules.TryParseBossEffect(ward.BreakEffect, out var effect) && effect != PlayScript.Invalid && CurrentLandblock != null)
                    EnqueueBroadcast(new ACE.Server.Network.GameMessages.Messages.GameMessageScript(Guid, effect, 1.0f));

                log.Debug($"[WORLDEVENT] run={P_WorldEvent?.RunId} ward on {Name} 0x{Guid.Full:X8} shattered by {kind} for {ward.ShatterSeconds:0} s");
            }

            return result;
        }

        /// <summary>
        /// How much harder this wave monster hits a protected target because of the run's corrupted monsters
        /// (protect.corrupted): selfMult for a corrupted one, empowerMult inside a living corrupted monster's
        /// aura, else 1. Read on the protected target's landblock thread; the positions are a moment old at worst.
        /// </summary>
        public double WorldEventEmpowerFactor()
        {
            var evt = P_WorldEvent;
            var protect = evt?.Composition?.Goal?.Protect;

            if (protect?.WaveMix == null)
                return 1.0;

            var corrupted = protect.EffectiveCorrupted;

            if (WorldEventCorrupted)
                return WorldEvents.WorldEventHoldRules.EmpowerFactor(true, null, corrupted.EmpowerRadius, corrupted.EmpowerMult, corrupted.SelfMult);

            if (Location == null)
                return 1.0;

            double? nearest = null;

            foreach (var other in evt.Spawner.LiveWaveCreatures)
            {
                if (other == null || other == this || !other.WorldEventCorrupted || other.IsDead || other.Location == null)
                    continue;

                var d = Location.DistanceTo(other.Location);

                if (nearest == null || d < nearest)
                    nearest = d;
            }

            return WorldEvents.WorldEventHoldRules.EmpowerFactor(false, nearest, corrupted.EmpowerRadius, corrupted.EmpowerMult, corrupted.SelfMult);
        }

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

            // A live taunt or a finale boss damage pull holds it on that player: a hit from anyone else (the seeker lock)
            // must not send it back to a target early.
            if (WorldEventSeekerHeldBy(ACE.Common.Time.GetUnixTime()) is { } holder)
            {
                if (!IsVisibleTarget(holder))
                    PhysicsObj.ObjMaint.AddVisibleTargets(new List<PhysicsObj> { holder.PhysicsObj });

                if (IsVisibleTarget(holder))
                {
                    if (AttackTarget != holder)
                    {
                        AttackTarget = holder;
                        EmoteManager.OnNewEnemy(holder);
                    }

                    return true;
                }
            }

            // Keep the current target while it stands - unless a breach has named a different one.
            if (AttackTarget is Creature current && current.WorldEventProtected && IsUsableSeekTarget(current)
                && IsVisibleTarget(current) && (WorldEventSeekForcedGuid == 0 || current.Guid.Full == WorldEventSeekForcedGuid))
                return true;

            var targets = evt.Spawner.LiveProtectedCreatures;

            if (targets.Count == 0)
            {
                WorldEventSeeker = false;
                return false;
            }

            var usable = new List<Creature>();
            var attackerCounts = new List<int>();
            var distances = new List<double>();
            var health = new List<(uint, uint)>();

            var otherSeekers = evt.Spawner.LiveWaveCreatures;

            foreach (var target in targets)
            {
                if (!IsUsableSeekTarget(target))
                    continue;

                var distSq = PhysicsObj.get_distance_sq_to_object(target.PhysicsObj, true);

                if (distSq > MaxChaseRangeSq)
                    continue;

                var attackers = 0;

                foreach (var other in otherSeekers)
                {
                    if (other != this && other.WorldEventSeeker && other.AttackTarget == target)
                        attackers++;
                }

                usable.Add(target);
                attackerCounts.Add(attackers);
                distances.Add(distSq);
                health.Add((target.Health.Current, target.Health.MaxValue));
            }

            Creature best = null;

            // Breach: the one target the whole wave was sent at, while it stands.
            if (WorldEventSeekForcedGuid != 0)
            {
                best = usable.Find(t => t.Guid.Full == WorldEventSeekForcedGuid);

                if (best == null)
                    WorldEventSeekForcedGuid = 0;
            }

            // Finale champion: the healthiest target.
            if (best == null && WorldEventSeekHealthiest)
            {
                var healthiest = ACE.Server.WorldEvents.WorldEventHoldRules.PickHealthiest(health);

                if (healthiest >= 0)
                    best = usable[healthiest];
            }

            // Otherwise spread out: the target with the FEWEST other seekers already on it, nearest first among
            // ties, so the wave splits across the targets and the defenders have to split up too.
            if (best == null)
            {
                // A mixed wave's seekers, runners and ranged leave the breach squad's target alone while another stands.
                var counts = ACE.Server.WorldEvents.WorldEventHoldRules.AvoidTarget(attackerCounts,
                    usable.ConvertAll(t => t.Guid.Full), WorldEventSeekAvoidGuid);

                var index = ACE.Server.WorldEvents.WorldEventHoldRules.PickLeastAttacked(counts, distances, MaxChaseRangeSq);

                if (index < 0)
                    return false;

                best = usable[index];
            }

            if (!IsVisibleTarget(best))
                PhysicsObj.ObjMaint.AddVisibleTargets(new List<PhysicsObj> { best.PhysicsObj });

            if (!IsVisibleTarget(best))
                return false;

            var previous = AttackTarget;

            AttackTarget = best;

            if (AttackTarget != previous)
            {
                EmoteManager.OnNewEnemy(AttackTarget);

                log.Info($"[WORLDEVENT] run={evt.RunId} seeker {Name} 0x{Guid.Full:X8} targets {best.Name} 0x{best.Guid.Full:X8} " +
                         $"rule={(WorldEventSeekForcedGuid != 0 ? "breach" : WorldEventSeekHealthiest ? "healthiest" : "spread")} (was {previous?.Name ?? "none"})");
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

            if (WorldEventHunter && TrySelectWorldEventHunterTarget())
            {
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

        /// <summary>
        /// Hold hunter waves: aim this hunter at a player who has healed one of the run's targets recently,
        /// the nearest such healer within chase range, else at the nearest player. Called on wake and from
        /// FindNextTarget, so a hunter turns onto whoever starts healing. False when nobody is in reach.
        /// </summary>
        public bool TrySelectWorldEventHunterTarget()
        {
            if (!WorldEventHunter || PhysicsObj == null || P_WorldEvent == null)
                return false;

            Player pick = null;
            var bestDistSq = double.MaxValue;

            foreach (var healer in P_WorldEvent.RecentProtectedHealers(ACE.Common.Time.GetUnixTime()))
            {
                if (healer == null || healer.IsDead || healer.Teleporting || !healer.Attackable || healer.PhysicsObj == null)
                    continue;

                var distSq = PhysicsObj.get_distance_sq_to_object(healer.PhysicsObj, true);

                if (distSq <= MaxChaseRangeSq && distSq < bestDistSq)
                {
                    pick = healer;
                    bestDistSq = distSq;
                }
            }

            pick ??= NearestWakeTargetPlayer();

            if (pick == null)
                return false;

            if (!IsVisibleTarget(pick))
                PhysicsObj.ObjMaint.AddVisibleTargets(new List<PhysicsObj> { pick.PhysicsObj });

            if (!IsVisibleTarget(pick))
                return false;

            if (AttackTarget != pick)
            {
                log.Info($"[WORLDEVENT] run={P_WorldEvent.RunId} hunter {Name} 0x{Guid.Full:X8} targets {pick.Name} (was {AttackTarget?.Name ?? "none"})");
                AttackTarget = pick;
            }

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
        public bool TryRestoreWorldEventSeekerLock()
        {
            if (!WorldEventSeeker || IsDead || IsDestroyed || PhysicsObj == null)
                return false;

            if (AttackTarget is Creature current && current.WorldEventProtected)
                return false;

            // An active taunt (or a finale boss damage pull) is the one thing allowed to hold a seeker elsewhere.
            if (WorldEventSeekerHeldBy(ACE.Common.Time.GetUnixTime()) != null)
                return false;

            var was = AttackTarget;

            if (!TrySelectWorldEventSeekTarget())
                return false;

            log.Warn($"[WORLDEVENT] run={P_WorldEvent?.RunId} seeker {Name} 0x{Guid.Full:X8} had strayed onto {was?.Name ?? "nothing"}; " +
                     $"back on {AttackTarget?.Name} 0x{AttackTarget?.Guid.Full ?? 0:X8}");

            return true;
        }

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

            // Only when the lock changed something (it woke, or it pulled the seeker back onto a target),
            // so a player hitting a seeker that is already on its target does not log every hit.
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

        /// <summary>The finale boss's own bucket on this target (protect.bossMinKillSeconds).</summary>
        private double worldEventBossCapTokens;
        private double worldEventBossCapLastAt;

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

            // Heal reduction after a hit (protect.healReductionAfterHit) is timed from here.
            worldEventProtectedLastMonsterHitAt = now;

            var maxHealth = Health.MaxValue;

            // The finale boss (protect.bossTargetsProtected): its hits are sized so that alone it needs bossMinKillSeconds to
            // empty a target and its own bucket holds them to that; they also count against the shared minDeathSeconds cap (not the aura).
            if (attacker.WorldEventFinaleBoss && protect.BossTargetsProtected && protect.BossMinKillSeconds > 0)
            {
                worldEventProtectedLastHitAt ??= new Dictionary<uint, double>();

                double? lastBoss = worldEventProtectedLastHitAt.TryGetValue(attacker.Guid.Full, out var bossAt) ? bossAt : (double?)null;

                worldEventProtectedLastHitAt[attacker.Guid.Full] = now;

                var slice = ACE.Server.WorldEvents.WorldEventFinaleBoss.HitDamage(maxHealth,
                    ACE.Server.WorldEvents.WorldEventHoldRules.HitInterval(lastBoss, now), protect.BossMinKillSeconds);

                var dealt = ACE.Server.WorldEvents.WorldEventFinaleBoss.CapHit(slice, maxHealth, protect.BossMinKillSeconds,
                    ref worldEventBossCapTokens, ref worldEventBossCapLastAt, now);

                // world_events_hold_knight_damage_taken_mult (1 = as before): after the boss's own bucket, before the shared floor.
                dealt = (float)(dealt * protect.KnightDamageTakenMult);

                // And the shared floor: the boss and the wave together never empty a target faster than minDeathSeconds.
                if (protect.MinDeathSeconds > 0)
                    dealt = ACE.Server.WorldEvents.WorldEventHoldRules.CapProtectedHit(dealt, maxHealth, protect.MinDeathSeconds,
                        ref worldEventProtectedCapTokens, ref worldEventProtectedCapLastAt, now);

                P_WorldEvent?.NoteFinaleBossHit(attacker, this, dealt, now);

                return dealt;
            }

            if (protect.UndefendedSeconds > 0)
            {
                worldEventProtectedLastHitAt ??= new Dictionary<uint, double>();

                double? last = worldEventProtectedLastHitAt.TryGetValue(attacker.Guid.Full, out var at) ? at : (double?)null;

                worldEventProtectedLastHitAt[attacker.Guid.Full] = now;

                var interval = ACE.Server.WorldEvents.WorldEventHoldRules.HitInterval(last, now);

                amount = ACE.Server.WorldEvents.WorldEventHoldRules.ProtectedHitDamage(maxHealth, interval,
                    protect.EffectiveUndefendedSeconds(P_WorldEvent?.ResolvedAudienceCount ?? 1),
                    attacker.WorldEventAttackSpeedMult);
            }

            // Corrupted monsters (protect.corrupted): one hits harder itself, and empowers the wave monsters near it.
            // Before the cap, so even an empowered crowd cannot beat minDeathSeconds.
            amount = (float)(amount * attacker.WorldEventEmpowerFactor());

            // world_events_hold_knight_damage_taken_mult (1 = as before), also before the cap.
            amount = (float)(amount * protect.KnightDamageTakenMult);

            // world_events_hold_<role>_npc_damage_mult (1 = as before): this monster's role, also before the cap.
            amount = (float)(amount * attacker.WorldEventNpcDamageMult);

            // Every run monster's hit goes through the cap, a corrupted assault monster's included: on 2026-10-19 its
            // uncapped hits stacked on top of a capped wave and killed a knight in 26 s against a 60 s floor.
            if (protect.MinDeathSeconds > 0)
            {
                amount = ACE.Server.WorldEvents.WorldEventHoldRules.CapProtectedHit(amount, maxHealth, protect.MinDeathSeconds,
                    ref worldEventProtectedCapTokens, ref worldEventProtectedCapLastAt, now);
            }

            return amount;
        }

        /// <summary>The player-heal cap bucket (WorldEventHoldRules.ConvertPlayerHeal): what may still be healed now, and when it was last topped up.</summary>
        private double worldEventProtectedHealTokens;
        private double worldEventProtectedHealLastAt;

        /// <summary>
        /// True when players may heal this creature: a living Hold protected target whose goal allows player
        /// healing (protect.maxHealPerTenSeconds above 0). Every other creature: false.
        /// </summary>
        public bool AcceptsWorldEventPlayerHeal =>
            WorldEventProtected && !IsDead && (P_WorldEvent?.Composition?.Goal?.Protect?.PlayerHealAllowed ?? false);

        /// <summary>
        /// The only spells a player may cast on a protected target (D6 - pure): a beneficial Boost that raises
        /// HEALTH (Heal Other and its kin). Everything else - buffs, other vitals, anything harmful - stays
        /// refused.
        /// </summary>
        public static bool IsWorldEventProtectedHealSpell(bool isBeneficial, ACE.Entity.Enum.SpellType type,
            ACE.Entity.Enum.DamageType vital, int boost, int maxBoost)
        {
            return isBeneficial && type == ACE.Entity.Enum.SpellType.Boost && vital == ACE.Entity.Enum.DamageType.Health
                && Math.Max(boost, maxBoost) > 0;
        }

        /// <summary>
        /// The spell-target rule for a protected target (D6 - pure), as Player.IsInvalidTarget applies it: only a
        /// health heal, and only while the goal allows player healing, is a valid target.
        /// </summary>
        public static bool IsInvalidWorldEventProtectedSpellTarget(bool acceptsPlayerHeal, bool isHealSpell)
        {
            return !(acceptsPlayerHeal && isHealSpell);
        }

        /// <summary><see cref="IsWorldEventProtectedHealSpell(bool, ACE.Entity.Enum.SpellType, ACE.Entity.Enum.DamageType, int, int)"/> for a spell.</summary>
        public static bool IsWorldEventProtectedHealSpell(ACE.Server.Entity.Spell spell)
        {
            return spell != null && IsWorldEventProtectedHealSpell(spell.IsBeneficial, spell.MetaSpellType,
                spell.VitalDamageType, spell.Boost, spell.MaxBoost);
        }

        /// <summary>
        /// A player's heal (spell or kit) on this protected target, converted to the health actually restored:
        /// a share of max health sized against a typical player heal, capped per ten seconds
        /// (WorldEventHoldRules.ConvertPlayerHeal). Returns <paramref name="rawHeal"/> unchanged for any
        /// creature that is not protected.
        /// </summary>
        public int ConvertWorldEventProtectedHeal(double rawHeal)
        {
            return ConvertWorldEventProtectedHeal(rawHeal, ACE.Common.Time.GetUnixTime());
        }

        /// <summary><see cref="ConvertWorldEventProtectedHeal(double)"/> at a given clock (the test seam).</summary>
        public int ConvertWorldEventProtectedHeal(double rawHeal, double now)
        {
            return ConvertWorldEventProtectedHeal(rawHeal, now, null);
        }

        /// <summary>When a run monster last hit this protected target (0 = never). The heal-reduction window reads it.</summary>
        private double worldEventProtectedLastMonsterHitAt;

        /// <summary>
        /// <see cref="ConvertWorldEventProtectedHeal(double, double)"/> knowing who healed: the heal is reduced
        /// while the target was hit recently (protect.healReductionAfterHit), then converted and capped, and the
        /// healer is remembered by the run so hunter waves can go for them.
        /// </summary>
        public int ConvertWorldEventProtectedHeal(double rawHeal, double now, Player healer)
        {
            if (!WorldEventProtected)
                return (int)Math.Round(rawHeal);

            var protect = P_WorldEvent?.Composition?.Goal?.Protect;

            if (protect == null)
                return 0;

            double? sinceHit = worldEventProtectedLastMonsterHitAt > 0 ? now - worldEventProtectedLastMonsterHitAt : (double?)null;

            var factor = ACE.Server.WorldEvents.WorldEventHoldRules.HealAfterHitFactor(sinceHit,
                protect.HealReductionAfterHit, protect.HealReductionSeconds);

            if (healer != null)
                P_WorldEvent?.NoteProtectedHealer(healer, now);

            // world_events_hold_player_heal off: player heals restore nothing; player_heal_mult scales what one heal is worth (1 = as before).
            if (!protect.PlayerHealAllowed)
                return 0;

            var converted = ACE.Server.WorldEvents.WorldEventHoldRules.ConvertPlayerHeal(rawHeal * factor * protect.PlayerHealMult, Health.MaxValue, protect.MaxHealPerTenSeconds,
                ref worldEventProtectedHealTokens, ref worldEventProtectedHealLastAt, now);

            // A player's heal counts against the same per-wave budget as every other source (only what it can
            // actually restore is charged). No budget set: the converted heal as before.
            if (!(protect.HealBudgetPerWave > 0))
                return converted;

            return TakeWorldEventHealBudget(Math.Min(converted, (int)(Health.MaxValue - Health.Current)));
        }

        /// <summary>The wave this target's healing budget (and rescue cap) is counting for.</summary>
        private int worldEventHealBudgetWave = -1;

        /// <summary>Health regained this wave, every source together (protect.healBudgetPerWave).</summary>
        private int worldEventHealBudgetSpent;

        /// <summary>Health regained this wave from rescues (protect.rescueCapPerWave).</summary>
        private int worldEventRescueSpent;

        /// <summary>Health regained this wave, every source together. Tests and the log read it.</summary>
        public int WorldEventHealSpentThisWave
        {
            get
            {
                RollWorldEventHealBudget();
                return worldEventHealBudgetSpent;
            }
        }

        /// <summary>Starts a fresh budget when the run has started a new wave since the last heal.</summary>
        private void RollWorldEventHealBudget()
        {
            var wave = P_WorldEvent?.HoldWaveNumber ?? 0;

            if (wave == worldEventHealBudgetWave)
                return;

            worldEventHealBudgetWave = wave;
            worldEventHealBudgetSpent = 0;
            worldEventRescueSpent = 0;
        }

        /// <summary>The share of <paramref name="requested"/> the per-wave budget lets through, spent now.</summary>
        private int TakeWorldEventHealBudget(int requested)
        {
            if (requested <= 0)
                return 0;

            RollWorldEventHealBudget();

            var budget = P_WorldEvent?.Composition?.Goal?.Protect?.HealBudgetPerWave ?? 0;
            var allowed = ACE.Server.WorldEvents.WorldEventHoldRules.HealWithinBudget(requested, Health.MaxValue, budget, worldEventHealBudgetSpent);

            worldEventHealBudgetSpent += allowed;

            return allowed;
        }

        /// <summary>
        /// Every non-player heal on a protected target goes through here (shrine, corrupted kills, rescues, clear
        /// regeneration, the between-waves heal): <paramref name="share"/> of max health, reduced while the target was
        /// hit recently (protect.healReductionAfterHit for protect.healReductionSeconds), then held to the per-wave
        /// budget (protect.healBudgetPerWave), and a rescue also to protect.rescueCapPerWave. Writes the health and
        /// returns what was restored. Must run on this target's landblock thread.
        /// </summary>
        public int ApplyWorldEventProtectedHeal(double share, bool isRescue, double now, out ACE.Server.WorldEvents.WorldEventHoldRules.ProtectedHealOutcome outcome)
        {
            outcome = ACE.Server.WorldEvents.WorldEventHoldRules.ProtectedHealOutcome.None;

            var protect = P_WorldEvent?.Composition?.Goal?.Protect;

            if (!WorldEventProtected || IsDead || protect == null || !(share > 0) || !double.IsFinite(share))
                return 0;

            RollWorldEventHealBudget();

            var missing = (int)(Health.MaxValue - Health.Current);

            if (missing <= 0)
            {
                outcome = ACE.Server.WorldEvents.WorldEventHoldRules.ProtectedHealOutcome.Full;
                return 0;
            }

            double? sinceHit = worldEventProtectedLastMonsterHitAt > 0 ? now - worldEventProtectedLastMonsterHitAt : (double?)null;
            var factor = ACE.Server.WorldEvents.WorldEventHoldRules.HealAfterHitFactor(sinceHit, protect.HealReductionAfterHit, protect.HealReductionSeconds);

            var requested = (int)Math.Floor(Math.Min(1.0, share) * Health.MaxValue * factor);

            if (isRescue)
            {
                var rescueRoom = (int)Math.Floor(Math.Clamp(protect.RescueCapPerWave, 0, 1) * Health.MaxValue) - worldEventRescueSpent;
                requested = Math.Min(requested, Math.Max(0, rescueRoom));
            }

            requested = Math.Min(requested, missing);

            var allowed = TakeWorldEventHealBudget(requested);

            if (allowed <= 0)
            {
                outcome = requested > 0 ? ACE.Server.WorldEvents.WorldEventHoldRules.ProtectedHealOutcome.BudgetSpent
                    : ACE.Server.WorldEvents.WorldEventHoldRules.ProtectedHealOutcome.None;
                return 0;
            }

            var healed = UpdateVitalDelta(Health, allowed);

            if (healed > 0)
                DamageHistory.OnHeal((uint)healed);

            if (isRescue)
                worldEventRescueSpent += Math.Max(0, healed);

            outcome = healed > 0 ? ACE.Server.WorldEvents.WorldEventHoldRules.ProtectedHealOutcome.Healed
                : ACE.Server.WorldEvents.WorldEventHoldRules.ProtectedHealOutcome.Full;

            return Math.Max(0, healed);
        }

        /// <summary>When a run monster last hit this protected target (0 = never), for the heal reduction and the simulation.</summary>
        public double WorldEventLastMonsterHitAt => worldEventProtectedLastMonsterHitAt;

        /// <summary>
        /// Hold ranged waves (<see cref="WorldEventHoldDistance"/> above 0), called from the monster tick once
        /// the target distance is known: a monster with a ranged attack picks it (its missile weapon with ammo, or
        /// one of its offensive spells, never a buff or a debuff), walks in to the hold distance (or to the
        /// attack's own range when that is shorter), stops, faces the target and attacks from there.
        ///
        /// Facing is set directly. A stock turn (StartTurn) marks the monster as moving, and this hold stops a
        /// moving monster on its next tick, so the turn was cancelled every time and a held monster never faced
        /// its target and never attacked (seen live 2026-10-18). Returns false, leaving the stock AI in charge,
        /// only for a monster with no ranged attack at all. Monster thread only.
        /// </summary>
        public bool TickWorldEventRangedHold(float targetDist)
        {
            if (WorldEventHoldDistance <= 0 || AttackTarget == null || IsDead || PhysicsObj == null)
                return false;

            var canMissile = WorldEventCanFireMissile();
            var canMagic = WorldEventHasOffensiveSpell();

            if (!WorldEvents.WorldEventHoldRules.HoldsRange(WorldEventHoldDistance, canMissile, canMagic))
                return false;

            // The attack first, so the monster knows how close it has to stand.
            if (!WorldEventHasRangedAttackChosen(canMissile) && !SelectWorldEventRangedAttack(canMissile, canMagic))
                return false;

            var hold = WorldEventHoldDistance;

            // An attack whose own range is shorter than the hold distance stops at its own range.
            if (MaxRange > 0 && MaxRange < hold)
                hold = Math.Max(1.0f, MaxRange - 1.0f);

            if (WorldEvents.WorldEventHoldRules.RangedHoldShouldApproach(targetDist, hold))
            {
                if (IsMoving)
                    Movement();
                else
                    StartWorldEventRangedApproach();

                return true;
            }

            if (IsMoving)
                StopWorldEventRangedApproach();

            IsTurning = false;

            if (!IsFacing(AttackTarget))
                FaceTargetDirectly(AttackTarget);

            if (AttackReady())
                Attack();

            return true;
        }

        /// <summary>True when the attack already chosen is a ranged one the hold can use: the missile weapon, or an offensive spell.</summary>
        private bool WorldEventHasRangedAttackChosen(bool canMissile)
        {
            if (CurrentAttack == CombatType.Missile)
                return canMissile;

            if (CurrentAttack == CombatType.Magic && CurrentSpell != null)
                return WorldEvents.WorldEventRangedMembers.OffensiveSpellLookup(CurrentSpell.Id);

            return false;
        }

        /// <summary>
        /// A ranged attack for the hold: the missile weapon when it can fire, else one of the offensive spells
        /// (weighted by their own chances). False when neither is available.
        /// </summary>
        private bool SelectWorldEventRangedAttack(bool canMissile, bool canMagic)
        {
            if (canMissile)
            {
                CurrentAttack = CombatType.Missile;
                MaxRange = GetMaxRange();
                return true;
            }

            if (canMagic)
            {
                var spellId = 0;

                try
                {
                    spellId = WorldEvents.WorldEventRangedMembers.PickOffensiveSpell(Biota.PropertiesSpellBook,
                        WorldEvents.WorldEventRangedMembers.OffensiveSpellLookup, ACE.Common.ThreadSafeRandom.Next(0.0f, 1.0f));
                }
                catch (Exception ex)
                {
                    log.Warn($"[WORLDEVENT] ranged hold spell pick for {Name} 0x{Guid.Full:X8} failed", ex);
                }

                if (spellId != 0)
                {
                    CurrentSpell = new ACE.Server.Entity.Spell(spellId);
                    CurrentAttack = CombatType.Magic;
                    MaxRange = GetMaxRange();
                    return true;
                }
            }

            CurrentAttack = null;
            return false;
        }

        /// <summary>A thrown weapon, or a launcher with ammunition equipped: what RangeAttack needs to fire.</summary>
        private bool WorldEventCanFireMissile()
        {
            var weapon = GetEquippedMissileWeapon();

            return weapon != null && (!weapon.IsAmmoLauncher || GetEquippedAmmo() != null);
        }

        /// <summary>Cached: whether this monster's spellbook holds an offensive spell (a projectile or Harm).</summary>
        private bool? worldEventHasOffensiveSpell;

        private bool WorldEventHasOffensiveSpell()
        {
            if (worldEventHasOffensiveSpell == null)
            {
                try
                {
                    var book = Biota.PropertiesSpellBook;

                    worldEventHasOffensiveSpell = book != null && WorldEvents.WorldEventRangedMembers.PickOffensiveSpell(book,
                        WorldEvents.WorldEventRangedMembers.OffensiveSpellLookup, 0) != 0;
                }
                catch (Exception ex)
                {
                    log.Warn($"[WORLDEVENT] offensive spell check for {Name} 0x{Guid.Full:X8} failed", ex);
                    worldEventHasOffensiveSpell = false;
                }
            }

            return worldEventHasOffensiveSpell.Value;
        }

        /// <summary>The stock chase move (StartTurn's move half), used to walk a ranged monster in; the hold tick stops it at the hold distance.</summary>
        private void StartWorldEventRangedApproach()
        {
            if (!MoveReady())
                return;

            if (MoveSpeed == 0.0f)
                GetMovementSpeed();

            MoveTo(AttackTarget, RunRate);

            IsMoving = true;
            LastMoveTime = ACE.Server.Entity.Timers.RunningTime;
            NextCancelTime = LastMoveTime + ACE.Common.ThreadSafeRandom.Next(2, 4);

            var mvp = new ACE.Server.Physics.Animation.MovementParameters();
            mvp.Flags &= ~ACE.Server.Physics.Animation.MovementParamFlags.CanWalk;
            mvp.Flags |= GetMonsterMoveToFlags(false, DisableSticky);
            mvp.Speed = CurrentMovementSlowFactor;

            PhysicsObj.MoveToObject(AttackTarget.PhysicsObj, mvp);
            PhysicsObj.UpdateTime = ACE.Server.Physics.Common.PhysicsTimer.CurrentTime;
        }

        /// <summary>Stops the approach where the monster stands, without dropping its target (CancelMoveTo would re-target).</summary>
        private void StopWorldEventRangedApproach()
        {
            PhysicsObj.MovementManager.MoveToManager.CancelMoveTo(WeenieError.ActionCancelled);
            PhysicsObj.MovementManager.MoveToManager.FailProgressCount = 0;

            EnqueueBroadcastMotion(new ACE.Server.Entity.Motion(CurrentMotionState.Stance, MotionCommand.Ready));

            IsMoving = false;
        }

        /// <summary>
        /// One log line per player heal attempt on a protected target (spell or kit): accepted with the amounts,
        /// or refused with the reason, so an in-game "heals do nothing" report can be read straight off the log.
        /// </summary>
        public static void LogWorldEventProtectedHeal(Creature target, Player healer, string what)
        {
            log.Info($"[WORLDEVENT] run={target?.P_WorldEvent?.RunId} protected heal on {target?.Name} 0x{target?.Guid.Full ?? 0:X8} " +
                     $"by {healer?.Name ?? "?"}: {what} (health {target?.Health.Current}/{target?.Health.MaxValue})");
        }

        /// <summary>When this player was last sent a protected target's health line, and for which target (rate limit).</summary>
        private uint worldEventHealthLineTarget;
        private double worldEventHealthLineAt;

        /// <summary>
        /// Assessing a protected target: the player gets "Sir Donovan is at 64% health." in chat, since the
        /// client draws no health bar for a non-attackable creature. Once per target per 3 seconds (the client
        /// re-requests an open appraisal). A no-op for anything that is not a protected target.
        /// </summary>
        public void TrySendWorldEventProtectedHealth(WorldObject target)
        {
            if (!(this is Player player) || !(target is Creature creature) || !creature.WorldEventProtected || creature.IsDead)
                return;

            var now = ACE.Common.Time.GetUnixTime();

            if (worldEventHealthLineTarget == creature.Guid.Full && now - worldEventHealthLineAt < 3.0)
                return;

            worldEventHealthLineTarget = creature.Guid.Full;
            worldEventHealthLineAt = now;

            player.Session?.Network.EnqueueSend(new ACE.Server.Network.GameMessages.Messages.GameMessageSystemChat(
                ACE.Server.WorldEvents.WorldEventHoldRules.ProtectedHealthLine(creature.Name, creature.Health.Current, creature.Health.MaxValue),
                ChatMessageType.Broadcast));
        }

        private bool IsUsableSeekTarget(Creature target)
        {
            return target != null && target.PhysicsObj != null && !target.IsDestroyed && !target.IsDead
                && IsWorldEventProtectedTargetFor(target);
        }
    }
}
