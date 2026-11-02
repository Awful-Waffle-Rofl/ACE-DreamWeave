using System;
using System.Collections.Generic;
using System.Threading;

using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Physics;
using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldEvents.Hold;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// World Events Hold: the one immutable run tag a Hold creature (knight, wave monster, finale boss) carries. Follows the
    /// Creature.SetBattlegroundObjective pattern (Creature_BattlegroundObjective.cs): a plain in-memory field, set by the
    /// spawner before EnterWorld and NEVER persisted - Hold creatures are ephemeral. No WorldObject subclass, no property id.
    ///
    /// H4a adds the engine side every WorldEventHooks Hold member forwards to: a knight's hit pacing, the ranged ward, the
    /// player damage multiplier, and the seeker / hunter target pick. Per-creature state that changes during a run lives in
    /// <see cref="HoldCombatState"/>. Its DAMAGE-SIDE fields (hit pacing, the caps, the ward, the pull, the finale boss's
    /// damage log) are written from the ATTACKER's thread too - TakeDamage, SpellProjectile and life magic resolve on the
    /// attacker's landblock, which need not be this creature's - so every read-modify-write of them holds lock(state).
    /// The targeting fields are touched only on this creature's own landblock thread (the monster tick, FindNextTarget).
    /// </summary>
    partial class Creature
    {
        private WorldEventHoldTag worldEventHold;

        /// <summary>
        /// The creature's Hold tag, or null for every creature that is not part of a Hold run. Immutable once set, so any
        /// thread may read it (a volatile read); "is a Hold knight" is WorldEventHold?.Kind == HoldUnitKind.Knight.
        /// </summary>
        public WorldEventHoldTag WorldEventHold => Volatile.Read(ref worldEventHold);

        /// <summary>
        /// Tags this creature for a Hold run. Write-once and atomic: returns false (and changes nothing) for a null tag or
        /// when a tag is already set, and of any number of racing calls exactly one returns true. Called only by the spawner,
        /// before EnterWorld, so every later reader (combat and movement on the landblock thread, the run on the world
        /// thread) sees the tag from the creature's first tick.
        /// </summary>
        public bool SetWorldEventHoldTag(WorldEventHoldTag t)
        {
            if (t == null)
                return false;

            if (Interlocked.CompareExchange(ref worldEventHold, t, null) != null)
                return false;

            // Allocated here, before EnterWorld, so the cross-thread damage paths never race to create it.
            EnsureHoldState();
            return true;
        }

        // ---- per-creature mutable state (own landblock thread only) -------------------------------------------------

        /// <summary>
        /// Everything about a Hold creature that changes during its run. Allocated when the tag is set (or, for an untagged
        /// creature that needs it, once, atomically), so a creature with no tag normally carries none. The damage-side
        /// fields are CROSS-THREAD (the attacker's landblock writes them): hold lock(state) around every read-modify-write.
        /// The world thread never touches it; a world-thread change is enqueued on the creature's landblock.
        /// </summary>
        internal sealed class HoldCombatState
        {
            /// <summary>
            /// Finale boss: recent player damage, coalesced per (player guid, whole unix second), so it is bounded by players x
            /// boss_damage_keep_seconds however fast they hit. Under lock(state).
            /// </summary>
            public Dictionary<(uint Player, long Second), double> BossDamage;

            /// <summary>Finale boss: the whole second the damage log was last pruned in (prunes once per second). Under lock(state).</summary>
            public long BossDamagePrunedSecond = long.MinValue;

            /// <summary>Knight: when each attacker last landed a scaled hit (unix time), keyed by attacker guid.</summary>
            public Dictionary<uint, double> LastHitAt;

            /// <summary>Knight: the shared min_death_seconds bucket.</summary>
            public double CapTokens, CapLastAt;

            /// <summary>Knight: the finale boss's own finale_min_kill_seconds bucket.</summary>
            public double BossCapTokens, BossCapLastAt;

            /// <summary>Knight: when a run monster last hit it (0 = never). The heal reduction window reads it.</summary>
            public double LastMonsterHitAt;

            /// <summary>Warded monster: when a shattered ward comes back (unix time; 0 = never shattered).</summary>
            public double WardDownUntil;

            /// <summary>Warded monster: true from a shatter until the run has played the restore effect.</summary>
            public bool WardRestorePending;

            /// <summary>Seeker: no knight is left standing, so it now targets like an ordinary monster.</summary>
            public bool SeekerRetired;

            /// <summary>Seeker: the forced (breach) knight fell, so the tag's ForcedTargetGuid no longer applies.</summary>
            public bool ForcedTargetCleared;

            /// <summary>Finale boss: a damage pull holds it on this creature until <see cref="PullUntil"/> (unix time).</summary>
            public Creature PullTarget;
            public double PullUntil;

            /// <summary>Knight: the player-heal bucket (player_heal_cap per ten seconds).</summary>
            public double HealTokens, HealLastAt;

            /// <summary>Knight: the wave the heal budget counts for (-1 = none yet), what it has regained this wave, and from rescues.</summary>
            public int BudgetWave = -1, BudgetSpent, RescueSpent;

            /// <summary>Player: the knight it was last sent a health line for, and when (the throttle).</summary>
            public uint HealthLineTarget;
            public double HealthLineAt;

            /// <summary>Ranged monster: whether its spellbook holds an offensive spell (null = not looked yet).</summary>
            public bool? HasOffensiveSpell;

            /// <summary>Knight, cosmetic defense: where it stood facing outward, the monster it faces, its swing rhythm.</summary>
            public ACE.Entity.Position DefendHome;
            public uint DefendFoe;
            public double DefendNextSwingAt, DefendLastFoeAt;
            public bool DefendFacingOutward = true;
            public int DefendSwings;
        }

        private HoldCombatState holdCombatState;

        /// <summary>
        /// This creature's Hold state: the one allocated with the tag, else allocated once (a compare-exchange, so two threads
        /// can never each get their own). Any thread may fetch it; mutate damage-side fields only under lock(state).
        /// </summary>
        internal HoldCombatState HoldState => Volatile.Read(ref holdCombatState) ?? EnsureHoldState();

        private HoldCombatState EnsureHoldState()
        {
            Interlocked.CompareExchange(ref holdCombatState, new HoldCombatState(), null);
            return Volatile.Read(ref holdCombatState);
        }

        /// <summary>When a run monster last hit this knight (0 = never).</summary>
        internal double HoldLastMonsterHitAt
        {
            get
            {
                var s = Volatile.Read(ref holdCombatState);

                if (s == null)
                    return 0;

                lock (s)
                    return s.LastMonsterHitAt;
            }
        }

        /// <summary>
        /// Finale boss pull: holds this boss on <paramref name="target"/> until <paramref name="until"/> (unix time), ahead of
        /// any knight. THE entry point for the finale controller's Apply sink: the controller (world thread) enqueues this on
        /// the boss's landblock (wired by H6).
        /// </summary>
        public void SetHoldPull(Creature target, double until)
        {
            var s = HoldState;

            lock (s)
            {
                s.PullTarget = target;
                s.PullUntil = until;
            }
        }

        /// <summary>
        /// The finale boss took <paramref name="amount"/> from <paramref name="attacker"/>: a player's hit, or a pet's credited
        /// to its owner, is logged for the pull. Anything else, and every creature that is not a finale boss, is ignored. Any
        /// thread (the attacker's landblock records damage).
        /// </summary>
        internal void NoteHoldBossDamage(WorldObject attacker, uint amount, double now)
        {
            var player = attacker as Player ?? (attacker as Pet)?.P_PetOwner;

            if (player != null)
                NoteHoldBossDamage(player.Guid.Full, amount, now);
        }

        /// <summary><see cref="NoteHoldBossDamage(WorldObject, uint, double)"/> with the player already resolved to a guid. Any thread.</summary>
        internal void NoteHoldBossDamage(uint playerGuid, uint amount, double now)
        {
            var tag = WorldEventHold;

            if (tag?.Kind != HoldUnitKind.FinaleBoss || amount == 0 || playerGuid == 0)
                return;

            var keep = HoldBossDamageKeepSeconds(tag);
            var s = HoldState;

            var second = (long)Math.Floor(now);

            lock (s)
            {
                var log = s.BossDamage ??= new Dictionary<(uint, long), double>();

                // Pruned in place, once per new second (removing during enumeration is allowed on .NET Core 3.0+).
                if (second != s.BossDamagePrunedSecond)
                {
                    s.BossDamagePrunedSecond = second;

                    foreach (var key in log.Keys)
                        if (now - key.Second > keep)
                            log.Remove(key);
                }

                log.TryGetValue((playerGuid, second), out var sum);
                log[(playerGuid, second)] = sum + amount;
            }
        }

        /// <summary>
        /// A copy of the finale boss's recent player damage (within boss_damage_keep_seconds of <paramref name="now"/>), one
        /// entry per player per whole second (At = that second), oldest first: what the finale controller reads to decide a
        /// pull. Empty for any other creature. Any thread.
        /// </summary>
        public IReadOnlyList<(double At, uint Player, double Amount)> HoldBossDamageSnapshot(double now)
        {
            var tag = WorldEventHold;
            var s = Volatile.Read(ref holdCombatState);

            if (tag?.Kind != HoldUnitKind.FinaleBoss || s == null)
                return Array.Empty<(double, uint, double)>();

            var keep = HoldBossDamageKeepSeconds(tag);

            var result = new List<(double At, uint Player, double Amount)>();

            lock (s)
            {
                if (s.BossDamage != null)
                {
                    foreach (var kv in s.BossDamage)
                        if (now - kv.Key.Second <= keep)
                            result.Add((kv.Key.Second, kv.Key.Player, kv.Value));
                }
            }

            result.Sort((a, b) => a.At != b.At ? a.At.CompareTo(b.At) : a.Player.CompareTo(b.Player));
            return result;
        }

        private static double HoldBossDamageKeepSeconds(WorldEventHoldTag tag)
        {
            var keep = tag.Run?.Tuning.BossDamageKeepSeconds ?? 30;

            return double.IsFinite(keep) && keep > 0 ? keep : 30;
        }

        // ---- identity -----------------------------------------------------------------------------------------------

        /// <summary>True when this creature (a run monster or the finale boss) may target and damage <paramref name="knight"/>.</summary>
        internal bool IsHoldKnightAttackerOf(Creature knight)
        {
            var kt = knight?.WorldEventHold;

            if (kt == null || kt.Kind != HoldUnitKind.Knight)
                return false;

            var at = WorldEventHold;

            return WorldEventHoldRules.MayDamageKnight(kt.RunId, this is Player || this is CombatPet, at?.Kind, at?.RunId ?? 0, kt.Run?.IsActive == true);
        }

        // ---- damage -------------------------------------------------------------------------------------------------

        /// <summary>
        /// A run monster's hit on this knight, re-sized (TakeDamage, SpellProjectile.DamageTarget, life-magic Harm):
        /// - the finale boss: a slice sized to finale_min_kill_seconds, held by its own bucket, times knight_damage_taken_mult
        ///   and its enrage, then the shared min_death_seconds cap;
        /// - any other run monster: the undefended_seconds slice (when set), times the corrupted self/empower factor,
        ///   knight_damage_taken_mult and its role's npc_damage_mult, then the shared cap.
        /// Every other hit (a player, a DoT with no creature source, another run, an inactive run) comes back unchanged.
        /// Landblock thread of this knight.
        /// </summary>
        internal float ScaleHoldKnightHit(WorldObject source, float amount, double now)
        {
            var tag = WorldEventHold;

            if (tag == null || tag.Kind != HoldUnitKind.Knight || !(amount > 0) || !(source is Creature attacker) || !attacker.IsHoldKnightAttackerOf(this))
                return amount;

            var run = tag.Run;
            var t = run.Tuning;
            var at = attacker.WorldEventHold;
            var s = HoldState;
            var maxHealth = Health.MaxValue;
            var attackerGuid = attacker.Guid.Full;
            var bossPath = at.Kind == HoldUnitKind.FinaleBoss && t.FinaleTargetsKnights && t.FinaleMinKillSeconds > 0;

            // Everything that reaches outside this knight (the run's enrage clock, the wave list) is read BEFORE the lock,
            // and the run is told about a boss hit after it, so lock(state) never nests another lock.
            var enrage = at.Kind == HoldUnitKind.FinaleBoss ? run.FinaleEnrageMult(attacker, now) : 1.0;
            var empower = bossPath ? 1.0 : attacker.HoldEmpowerFactor(t);

            float dealt;

            lock (s)
            {
                s.LastMonsterHitAt = now;

                s.LastHitAt ??= new Dictionary<uint, double>();

                // Forget attackers idle longer than HoldHitMemorySeconds, BEFORE the lookup, so a hit's size never depends on
                // whether some other attacker hit in between. The retention is fixed and far longer than the hit-interval
                // window, so a long gap inside it is still clamped to hit_interval_max_seconds; only an attacker idle past it
                // counts as a first hit again. Removing during enumeration is allowed for Dictionary on .NET Core 3.0+.
                foreach (var kv in s.LastHitAt)
                    if (now - kv.Value > HoldHitMemorySeconds)
                        s.LastHitAt.Remove(kv.Key);

                double? last = s.LastHitAt.TryGetValue(attackerGuid, out var lastAt) ? lastAt : (double?)null;

                s.LastHitAt[attackerGuid] = now;

                var interval = WorldEventHoldRules.HitInterval(last, now, t.HitIntervalSeconds, t.HitIntervalMaxSeconds);

                if (bossPath)
                {
                    var slice = WorldEventHoldRules.ProtectedHitDamage(maxHealth, interval, t.FinaleMinKillSeconds);

                    dealt = WorldEventHoldRules.CapProtectedHit(slice, maxHealth, t.FinaleMinKillSeconds, t.CapBurstSeconds,
                        ref s.BossCapTokens, ref s.BossCapLastAt, now);

                    dealt = (float)(dealt * WorldEventHoldRules.SafeMult(t.KnightDamageTakenMult) * WorldEventHoldRules.SafeMult(enrage));

                    dealt = WorldEventHoldRules.CapProtectedHit(dealt, maxHealth, t.MinDeathSeconds, t.CapBurstSeconds, ref s.CapTokens, ref s.CapLastAt, now);
                }
                else
                {
                    // Contributor parity: ProtectedHitDamage treats an AttackSpeedMult at or below 1 as 1 (only faster swingers grow the slice).
                    if (t.UndefendedSeconds > 0)
                        amount = WorldEventHoldRules.ProtectedHitDamage(maxHealth, interval, t.UndefendedSeconds, at.AttackSpeedMult);

                    // The finale boss outside its own path (finale_target players, or no min kill time) keeps its enrage; a
                    // role's npc_damage_mult otherwise (1 for the boss, which has no role).
                    var roleMult = at.Kind == HoldUnitKind.FinaleBoss ? enrage : t.Role(at.Role).NpcDamageMult;

                    amount = (float)(amount * empower * WorldEventHoldRules.SafeMult(t.KnightDamageTakenMult) * WorldEventHoldRules.SafeMult(roleMult));

                    // Every run monster's hit goes through the cap, a corrupted one's included.
                    dealt = WorldEventHoldRules.CapProtectedHit(amount, maxHealth, t.MinDeathSeconds, t.CapBurstSeconds, ref s.CapTokens, ref s.CapLastAt, now);
                }
            }

            if (bossPath)
                run.NoteFinaleBossHit(attacker, this, dealt, now);

            return dealt;
        }

        /// <summary>
        /// How long a knight remembers an attacker's last hit (seconds). Fixed, independent of the hit-interval settings: it only
        /// bounds the pacing map. An attacker idle longer than this has its next hit sized as a first hit; any shorter gap is
        /// clamped to hit_interval_max_seconds as usual.
        /// </summary>
        internal const double HoldHitMemorySeconds = 60.0;

        /// <summary>How many attackers this knight is pacing right now (tests). Under lock(state).</summary>
        internal int HoldPacedAttackerCount
        {
            get
            {
                var s = Volatile.Read(ref holdCombatState);

                if (s == null)
                    return 0;

                lock (s)
                    return s.LastHitAt?.Count ?? 0;
            }
        }

        /// <summary>
        /// The corrupted factor on this run monster's hits on a knight: corrupted_self_mult for a corrupted one, the empower
        /// multiplier within the empower radius of another living corrupted monster of the run, else 1. Positions are read
        /// from the run's published wave list (a moment old at worst).
        /// </summary>
        internal double HoldEmpowerFactor(WorldEventHoldTuning t)
        {
            var tag = WorldEventHold;

            if (tag == null)
                return 1.0;

            if (tag.Corrupted)
                return WorldEventHoldRules.EmpowerFactor(true, null, t.CorruptedEmpowerRadius, t.CorruptedEmpowerMult, t.CorruptedSelfMult);

            var here = Location;

            if (here == null || tag.Run == null)
                return 1.0;

            double? nearest = null;

            foreach (var other in tag.Run.WaveMonsters)
            {
                if (other == null || other == this || other.IsDead || other.WorldEventHold?.Corrupted != true)
                    continue;

                var there = other.Location;

                if (there == null)
                    continue;

                var d = here.DistanceTo(there);

                if (nearest == null || d < nearest)
                    nearest = d;
            }

            return WorldEventHoldRules.EmpowerFactor(false, nearest, t.CorruptedEmpowerRadius, t.CorruptedEmpowerMult, t.CorruptedSelfMult);
        }

        /// <summary>
        /// This run monster's damage multiplier on a player: its role's player_damage_mult, or for the finale boss
        /// finale_player_damage_mult times its enrage. 1 for a knight and for a creature of an inactive run.
        /// </summary>
        internal double HoldPlayerDamageMult(double now)
        {
            var tag = WorldEventHold;
            var run = tag?.Run;

            if (tag == null || run == null || !run.IsActive || tag.Kind == HoldUnitKind.Knight)
                return 1.0;

            var t = run.Tuning;

            if (tag.Kind == HoldUnitKind.FinaleBoss)
                return WorldEventHoldRules.SafeMult(t.FinalePlayerDamageMult) * WorldEventHoldRules.SafeMult(run.FinaleEnrageMult(this, now));

            return WorldEventHoldRules.SafeMult(t.Role(tag.Role).PlayerDamageMult);
        }

        /// <summary>Whether this monster's ward is up at <paramref name="now"/>. Any thread.</summary>
        internal bool IsHoldWardUp(double now)
        {
            if (WorldEventHold?.Warded != true)
                return false;

            var s = Volatile.Read(ref holdCombatState);

            if (s == null)
                return true;

            lock (s)
                return now >= s.WardDownUntil;
        }

        /// <summary>
        /// A hit of <paramref name="kind"/> on this warded monster: cut while the ward is up, and a breaking kind shatters it
        /// for the ward's shatter time with the break effect. Unchanged for every creature without a ward, and while the run
        /// has no ward block. Any thread (DamageEvent, SpellProjectile and life magic resolve on the ATTACKER's landblock): the
        /// up-check and the shatter are one locked step, so two hits racing cannot both shatter it.
        /// </summary>
        internal float ApplyHoldWard(WorldEventCombatKinds kind, float damage, double now)
        {
            var tag = WorldEventHold;
            var t = tag?.Run?.Tuning;

            if (tag == null || !tag.Warded || t == null || !t.WardEnabled)
                return damage;

            var s = HoldState;
            float result;
            WorldEventHoldRules.WardOutcome outcome;

            lock (s)
            {
                result = WorldEventHoldRules.WardHit(damage, kind, now >= s.WardDownUntil, t.WardReduction, t.WardReduces, t.WardBreakOn, out outcome);

                if (outcome == WorldEventHoldRules.WardOutcome.Shattered)
                {
                    s.WardDownUntil = now + WorldEventHoldRules.WardDownSeconds(t.WardShatterSeconds);
                    s.WardRestorePending = true;
                }
            }

            if (outcome == WorldEventHoldRules.WardOutcome.Shattered)
            {

                if (CurrentLandblock != null && Enum.TryParse<PlayScript>(t.WardBreakEffect, true, out var effect) && effect != PlayScript.Invalid)
                    EnqueueBroadcast(new GameMessageScript(Guid, effect, 1.0f));

                log.Debug($"[WORLDEVENT] run={tag.RunId} ward on {Name} 0x{Guid.Full:X8} shattered by {kind} for {t.WardShatterSeconds:0} s");
            }

            return result;
        }

        // ---- targeting ------------------------------------------------------------------------------------------------

        /// <summary>
        /// FindNextTarget, after the Taunt hold: a seeker goes for a knight, a hunter for a recent healer. False (normal
        /// targeting runs) for anything else, and when nothing is in reach. Landblock thread only.
        /// </summary>
        internal bool TrySelectHoldTarget()
        {
            var tag = WorldEventHold;

            if (tag == null || tag.Kind == HoldUnitKind.Knight || PhysicsObj == null)
                return false;

            if (tag.Seeker && TrySelectHoldSeekTarget(tag))
                return true;

            return tag.Hunter && TrySelectHoldHunterTarget(tag);
        }

        /// <summary>Who holds this seeker away from the knights right now: a live taunt, else a live finale pull; null when nothing does.</summary>
        internal Creature HoldSeekerHeldBy(double now)
        {
            if (TauntTarget != null && Timers.RunningTime < TauntExpiry && !TauntTarget.IsDead)
                return TauntTarget;

            var s = Volatile.Read(ref holdCombatState);

            if (s == null)
                return null;

            Creature pull;
            double until;

            lock (s)
            {
                pull = s.PullTarget;
                until = s.PullUntil;
            }

            if (pull != null && now < until && !pull.IsDead && !pull.Teleporting && pull.PhysicsObj != null)
                return pull;

            return null;
        }

        private bool IsUsableHoldSeekTarget(Creature target)
            => target != null && target.PhysicsObj != null && !target.IsDestroyed && !target.IsDead && IsHoldKnightAttackerOf(target);

        /// <summary>Puts <paramref name="target"/> on this monster's visible-target list (the tick drops a target that is not on it); true when it is there.</summary>
        private bool EnsureHoldVisibleTarget(Creature target)
        {
            if (!IsVisibleTarget(target))
                PhysicsObj.ObjMaint.AddVisibleTargets(new List<PhysicsObj> { target.PhysicsObj });

            return IsVisibleTarget(target);
        }

        private bool TrySelectHoldSeekTarget(WorldEventHoldTag tag)
        {
            var s = HoldState;

            if (s.SeekerRetired)
                return false;

            var run = tag.Run;

            if (run == null || !run.IsActive)
            {
                s.SeekerRetired = true;
                return false;
            }

            var now = ACE.Common.Time.GetUnixTime();

            // A live taunt or finale pull holds it on that creature; a hit from anyone else must not send it back early.
            if (HoldSeekerHeldBy(now) is { } holder && EnsureHoldVisibleTarget(holder))
            {
                if (AttackTarget != holder)
                {
                    AttackTarget = holder;
                    EmoteManager.OnNewEnemy(holder);
                }

                return true;
            }

            var forced = s.ForcedTargetCleared ? 0u : tag.ForcedTargetGuid;

            // Keep the current knight while it stands, unless a breach names a different one.
            if (AttackTarget is Creature current && IsUsableHoldSeekTarget(current) && IsVisibleTarget(current)
                && (forced == 0 || current.Guid.Full == forced))
                return true;

            var usable = new List<Creature>();
            var attackerCounts = new List<int>();
            var distances = new List<double>();
            var health = new List<(uint, uint)>();
            var standing = false;

            var others = run.WaveMonsters;

            foreach (var knight in run.Knights)
            {
                if (!IsUsableHoldSeekTarget(knight))
                    continue;

                standing = true;

                var distSq = PhysicsObj.get_distance_sq_to_object(knight.PhysicsObj, true);

                if (distSq > MaxChaseRangeSq)
                    continue;

                var attackers = 0;

                foreach (var other in others)
                    if (other != null && other != this && other.WorldEventHold?.Seeker == true && other.AttackTarget == knight)
                        attackers++;

                usable.Add(knight);
                attackerCounts.Add(attackers);
                distances.Add(distSq);
                health.Add((knight.Health.Current, knight.Health.MaxValue));
            }

            if (!standing)
            {
                s.SeekerRetired = true;
                return false;
            }

            Creature best = null;

            // Breach: the one knight the wave was sent at, while it stands.
            if (forced != 0)
            {
                best = usable.Find(k => k.Guid.Full == forced);

                // The breach knight fell: the rest of this seeker's life it spreads like any other.
                if (best == null && !IsForcedKnightStanding(run, forced))
                    s.ForcedTargetCleared = true;
            }

            // The finale boss: the healthiest knight.
            if (best == null && tag.Kind == HoldUnitKind.FinaleBoss)
            {
                var i = WorldEventHoldRules.PickHealthiest(health);

                if (i >= 0)
                    best = usable[i];
            }

            // Otherwise spread out: the knight with the fewest other seekers on it, nearest among ties, leaving the avoided
            // knight alone while another stands.
            if (best == null)
            {
                var counts = WorldEventHoldRules.AvoidTarget(attackerCounts, usable.ConvertAll(k => k.Guid.Full), tag.AvoidTargetGuid);
                var i = WorldEventHoldRules.PickLeastAttacked(counts, distances, MaxChaseRangeSq);

                if (i < 0)
                    return false;

                best = usable[i];
            }

            // finale_target both: a player nearer than the chosen knight is attacked instead.
            if (tag.Kind == HoldUnitKind.FinaleBoss && string.Equals(run.Tuning.FinaleTarget, ProtectDef.FinaleTargetBoth, StringComparison.Ordinal)
                && NearerHoldPlayerThan(PhysicsObj.get_distance_sq_to_object(best.PhysicsObj, true)))
                return false;

            if (!EnsureHoldVisibleTarget(best))
                return false;

            var previous = AttackTarget;

            AttackTarget = best;

            if (previous != best)
            {
                EmoteManager.OnNewEnemy(best);

                log.Debug($"[WORLDEVENT] run={tag.RunId} seeker {Name} 0x{Guid.Full:X8} targets {best.Name} 0x{best.Guid.Full:X8} " +
                          $"rule={(forced != 0 && best.Guid.Full == forced ? "breach" : tag.Kind == HoldUnitKind.FinaleBoss ? "healthiest" : "spread")} (was {previous?.Name ?? "none"})");
            }

            return true;
        }

        private static bool IsForcedKnightStanding(IWorldEventHoldRunView run, uint guid)
        {
            foreach (var k in run.Knights)
                if (k != null && k.Guid.Full == guid && !k.IsDead && !k.IsDestroyed)
                    return true;

            return false;
        }

        /// <summary>True when a living player this monster may attack is nearer than <paramref name="distSq"/> (squared metres).</summary>
        private bool NearerHoldPlayerThan(double distSq)
        {
            foreach (var target in GetAttackTargets())
                if (target is Player player && !player.IsDead && player.PhysicsObj != null
                    && PhysicsObj.get_distance_sq_to_object(player.PhysicsObj, true) < distSq)
                    return true;

            return false;
        }

        /// <summary>
        /// A hunter's pick: the nearest player who healed a knight recently (within healer_memory_seconds), within chase
        /// range, else the nearest player. False when nobody is in reach.
        /// </summary>
        private bool TrySelectHoldHunterTarget(WorldEventHoldTag tag)
        {
            var run = tag.Run;

            if (run == null || !run.IsActive)
                return false;

            Player pick = null;
            var bestDistSq = double.MaxValue;

            foreach (var guid in run.RecentHealers(ACE.Common.Time.GetUnixTime()))
            {
                var healer = PlayerManager.GetOnlinePlayer(guid);

                if (healer == null || healer.IsDead || healer.Teleporting || !healer.Attackable || healer.PhysicsObj == null)
                    continue;

                var distSq = PhysicsObj.get_distance_sq_to_object(healer.PhysicsObj, true);

                if (distSq <= MaxChaseRangeSq && distSq < bestDistSq)
                {
                    pick = healer;
                    bestDistSq = distSq;
                }
            }

            pick ??= NearestHoldWakePlayer();

            if (pick == null || !EnsureHoldVisibleTarget(pick))
                return false;

            if (AttackTarget != pick)
            {
                log.Debug($"[WORLDEVENT] run={tag.RunId} hunter {Name} 0x{Guid.Full:X8} targets {pick.Name} (was {AttackTarget?.Name ?? "none"})");
                AttackTarget = pick;
            }

            return true;
        }

        /// <summary>The nearest living, attackable, non-teleporting player this monster knows of, within chase range.</summary>
        private Player NearestHoldWakePlayer()
        {
            var players = new List<Player>();

            foreach (var creature in PhysicsObj.ObjMaint.GetVisibleTargetsValuesOfTypeCreature())
                if (creature is Player p)
                    players.Add(p);

            foreach (var p in PhysicsObj.ObjMaint.GetKnownPlayersValuesAsPlayer())
                if (p != null && !players.Contains(p))
                    players.Add(p);

            var usable = new List<Player>();
            var distances = new List<double>();

            foreach (var p in players)
            {
                if (p.IsDead || p.Teleporting || !p.Attackable || p.PhysicsObj == null)
                    continue;

                usable.Add(p);
                distances.Add(PhysicsObj.get_distance_sq_to_object(p.PhysicsObj, true));
            }

            var i = WorldEventHoldRules.NearestWithin(distances, MaxChaseRangeSq);

            return i >= 0 ? usable[i] : null;
        }

        /// <summary>
        /// The seeker lock, from every path that would wake or retarget a monster AT whoever disturbed it (a player walking
        /// into range, a player's hit, a packmate's alert, a creature's hit): a seeker with a knight in reach is aimed at that
        /// knight instead and woken, and the caller skips its own retarget. Only a taunt (or a finale pull) can still pull a
        /// seeker off. False, changing nothing, for every creature that is not a seeker. Landblock thread only.
        /// </summary>
        internal bool TryKeepHoldSeekerLock(WorldObject disturbedBy)
        {
            var tag = WorldEventHold;

            if (tag == null || !tag.Seeker || IsDead || PhysicsObj == null)
                return false;

            var previous = AttackTarget;

            if (!TrySelectHoldSeekTarget(tag))
                return false;

            var woke = MonsterState != State.Awake;

            if (woke)
                WakeUp(alertNearby: false);

            if (woke || previous != AttackTarget)
                log.Debug($"[WORLDEVENT] run={tag.RunId} seeker 0x{Guid.Full:X8} stays on {AttackTarget?.Name ?? "?"} 0x{AttackTarget?.Guid.Full ?? 0:X8} " +
                          $"(ignored {disturbedBy?.Name ?? "?"}; was {previous?.Name ?? "none"})");

            return true;
        }

        /// <summary>
        /// The run's wake pass (H6 enqueues this on the monster's landblock): a wave monster still asleep is given a target and
        /// woken, instead of standing at its portal until a player walks into its awareness range. A seeker takes a knight, a
        /// hunter a healer, anything else the nearest player. True when it woke. Landblock thread only.
        /// </summary>
        public bool TryHoldWake()
        {
            var tag = WorldEventHold;

            if (tag == null || tag.Kind == HoldUnitKind.Knight || IsDead || IsDestroyed || PhysicsObj == null || MonsterState != State.Idle)
                return false;

            if (TrySelectHoldTarget())
            {
                // alertNearby false: a seeker never recruits its pack, and the run wakes every other wave monster anyway.
                WakeUp(alertNearby: false);
                return true;
            }

            var player = NearestHoldWakePlayer();

            if (player == null || !EnsureHoldVisibleTarget(player))
                return false;

            AttackTarget = player;
            WakeUp(alertNearby: false);
            return true;
        }
        // ---- H4b: heals (knight's own landblock thread only) -----------------------------------------------------------

        /// <summary>
        /// THE heal path for a knight: every source (player kit or spell, shrine, corrupted kill, rescue, regeneration, the
        /// between-waves heal) arrives here through WorldEventHoldHeals.Queue, on this knight's own landblock thread. In
        /// order: a full knight refuses; the after-hit reduction (heal_reduction_after_hit for heal_reduction_seconds); a
        /// player heal is converted and held by player_heal_cap per ten seconds (and refused while player_heal is off); a
        /// share heal is a share of max health, a rescue also held to rescue_cap_per_wave; then the per-wave budget
        /// (heal_budget_per_wave, keyed on the run's wave number) for every source together. Writes the health and returns
        /// what was restored. <paramref name="amount"/> is raw player heal points for <see cref="HoldHealSource.Player"/>,
        /// else a share of max health.
        /// </summary>
        internal int ApplyHoldKnightHeal(double amount, HoldHealSource source, double now, out HoldHealOutcome outcome)
        {
            outcome = HoldHealOutcome.None;

            var tag = WorldEventHold;
            var run = tag?.Run;

            if (tag == null || tag.Kind != HoldUnitKind.Knight || run == null || IsDead || !(amount > 0) || !double.IsFinite(amount))
                return 0;

            if (!run.IsActive)
            {
                outcome = HoldHealOutcome.Refused;
                return 0;
            }

            var t = run.Tuning;
            var s = HoldState;
            var wave = run.WaveNumber;

            var max = Health.MaxValue;
            var missing = (int)(max - Math.Min(max, Health.Current));

            if (missing <= 0)
            {
                outcome = HoldHealOutcome.Full;
                return 0;
            }

            if (source == HoldHealSource.Player)
            {
                var playerMult = double.IsFinite(t.PlayerHealMult) && t.PlayerHealMult >= 0 ? t.PlayerHealMult : 1.0;

                if (!t.PlayerHealEnabled || !(playerMult > 0))
                {
                    outcome = HoldHealOutcome.Refused;
                    return 0;
                }
            }

            int allowed;
            var playerTaken = 0;

            // The budget, the rescue cap, the player-heal bucket and the after-hit window are shared with the damage side,
            // which runs on the attacker's thread: decided and reserved in one locked step. The vital write is outside it.
            lock (s)
            {
                RollHoldHealBudget(s, wave);

                double? sinceHit = s.LastMonsterHitAt > 0 ? now - s.LastMonsterHitAt : (double?)null;
                var factor = WorldEventHoldRules.HealAfterHitFactor(sinceHit, t.HealReductionAfterHit, t.HealReductionSeconds);

                int requested;

                if (source == HoldHealSource.Player)
                {
                    var mult = double.IsFinite(t.PlayerHealMult) && t.PlayerHealMult >= 0 ? t.PlayerHealMult : 1.0;

                    requested = WorldEventHoldRules.ConvertPlayerHeal(amount * factor * mult, max, t.PlayerHealCap, ref s.HealTokens, ref s.HealLastAt, now);

                    if (requested <= 0)
                    {
                        outcome = HoldHealOutcome.Capped;
                        return 0;
                    }

                    playerTaken = requested;
                }
                else
                {
                    requested = WorldEventHoldRules.ShareHeal(amount, max, factor);

                    if (source == HoldHealSource.Rescue)
                        requested = Math.Min(requested, WorldEventHoldRules.RescueRoom(t.RescueCapPerWave, max, s.RescueSpent));
                }

                requested = Math.Min(requested, missing);

                allowed = WorldEventHoldRules.HealWithinBudget(requested, max, t.HealBudgetPerWave, s.BudgetSpent);

                if (allowed <= 0)
                {
                    RefundHoldPlayerHealTokens(s, playerTaken, max, t.PlayerHealCap);
                    outcome = requested > 0 ? HoldHealOutcome.BudgetSpent : HoldHealOutcome.None;
                    return 0;
                }

                s.BudgetSpent += allowed;

                if (source == HoldHealSource.Rescue)
                    s.RescueSpent += allowed;
            }

            var healed = Math.Max(0, UpdateVitalDelta(Health, allowed));

            // Give back what the write did not use (a hit landed in between, or the clamp at max), under the same lock: the
            // budget and rescue cap by the shortfall, and the player-heal bucket by everything it gave that was not restored.
            if (healed < allowed || healed < playerTaken)
            {
                lock (s)
                {
                    if (s.BudgetWave == wave)
                    {
                        s.BudgetSpent = Math.Max(0, s.BudgetSpent - Math.Max(0, allowed - healed));

                        if (source == HoldHealSource.Rescue)
                            s.RescueSpent = Math.Max(0, s.RescueSpent - Math.Max(0, allowed - healed));
                    }

                    RefundHoldPlayerHealTokens(s, playerTaken - healed, max, t.PlayerHealCap);
                }
            }

            if (healed > 0)
                DamageHistory.OnHeal((uint)healed);

            outcome = healed > 0 ? HoldHealOutcome.Healed : HoldHealOutcome.Full;

            return healed;
        }

        /// <summary>
        /// Returns <paramref name="unused"/> health points to the player-heal bucket (never above its ten-second allowance,
        /// player_heal_cap x max health). Under lock(state).
        /// </summary>
        private static void RefundHoldPlayerHealTokens(HoldCombatState s, int unused, uint maxHealth, double playerHealCap)
        {
            if (unused <= 0 || !double.IsFinite(playerHealCap) || !(playerHealCap > 0))
                return;

            s.HealTokens = Math.Min(maxHealth * playerHealCap, s.HealTokens + unused);
        }

        /// <summary>Starts a fresh budget (and rescue cap) when the run has started a new wave since the last heal. Under lock(state).</summary>
        private static void RollHoldHealBudget(HoldCombatState s, int wave)
        {
            if (wave == s.BudgetWave)
                return;

            s.BudgetWave = wave;
            s.BudgetSpent = 0;
            s.RescueSpent = 0;
        }

        /// <summary>Health this knight has regained this wave, every source together (tests and logs). Landblock thread only.</summary>
        internal int HoldHealSpentThisWave
        {
            get
            {
                var s = HoldState;

                lock (s)
                {
                    RollHoldHealBudget(s, WorldEventHold?.Run?.WaveNumber ?? 0);
                    return s.BudgetSpent;
                }
            }
        }

        /// <summary>True when a player's heal is welcome on this creature: a living knight of an active run with player_heal on.</summary>
        internal bool AcceptsHoldPlayerHeal
        {
            get
            {
                var tag = WorldEventHold;

                return tag?.Kind == HoldUnitKind.Knight && !IsDead && tag.Run?.IsActive == true && tag.Run.Tuning.PlayerHealEnabled;
            }
        }

        // ---- H4b: what players see -------------------------------------------------------------------------------------

        /// <summary>
        /// Assessing a knight: the player gets "Sir Donovan is at 64% health." in chat, since the client may draw no health
        /// bar for a non-attackable creature. Once per knight per HealthLineIntervalSeconds (the client re-requests an open
        /// appraisal). Called on the assessing player's thread, and the throttle lives on that player. A no-op otherwise.
        /// </summary>
        internal void SendHoldKnightHealthLine(Creature knight, double now)
        {
            if (!(this is Player player) || knight?.WorldEventHold?.Kind != HoldUnitKind.Knight || knight.IsDead)
                return;

            var s = HoldState;

            if (s.HealthLineTarget == knight.Guid.Full && now - s.HealthLineAt < WorldEventHoldRules.HealthLineIntervalSeconds)
                return;

            s.HealthLineTarget = knight.Guid.Full;
            s.HealthLineAt = now;

            player.Session?.Network.EnqueueSend(new GameMessageSystemChat(
                WorldEventHoldRules.ProtectedHealthLine(knight.Name, knight.Health.Current, knight.Health.MaxValue), ChatMessageType.Broadcast));
        }

        // ---- H4b: the ranged keep-distance (monster tick, own landblock thread) ------------------------------------------

        /// <summary>
        /// A ranged wave monster (tag HoldDistance above 0), from the monster tick once the target distance is known: with a
        /// ranged attack (its missile weapon with ammo, or one of its offensive spells, never a buff) it walks in to the keep
        /// distance (or to the attack's own range when that is shorter), stops, faces the target directly and attacks from
        /// there. Facing is set directly: a stock turn marks the monster as moving, which this hold stops next tick, so a turn
        /// would be cancelled every time. Returns false, leaving the stock AI in charge, for a monster with no ranged attack.
        /// </summary>
        internal bool TickHoldRangedKeepDistance(float targetDist)
        {
            var tag = WorldEventHold;

            if (tag == null || !(tag.HoldDistance > 0) || AttackTarget == null || IsDead || PhysicsObj == null)
                return false;

            var canMissile = HoldCanFireMissile();
            var canMagic = HoldHasOffensiveSpell();

            if (!WorldEventHoldRules.HoldsRange(tag.HoldDistance, canMissile, canMagic))
                return false;

            // The attack first, so the monster knows how close it has to stand.
            if (!HoldHasRangedAttackChosen(canMissile) && !SelectHoldRangedAttack(canMissile, canMagic))
                return false;

            var hold = tag.HoldDistance;

            if (MaxRange > 0 && MaxRange < hold)
                hold = Math.Max(1.0f, MaxRange - 1.0f);

            var slack = tag.Run?.Tuning.RangedSlack ?? 1.5;

            if (WorldEventHoldRules.RangedHoldShouldApproach(targetDist, hold, slack))
            {
                if (IsMoving)
                    Movement();
                else
                    StartHoldRangedApproach();

                return true;
            }

            if (IsMoving)
                StopHoldRangedApproach();

            IsTurning = false;

            if (!IsFacing(AttackTarget))
                FaceTargetDirectly(AttackTarget);

            if (AttackReady())
                Attack();

            return true;
        }

        private bool HoldHasRangedAttackChosen(bool canMissile)
        {
            if (CurrentAttack == CombatType.Missile)
                return canMissile;

            if (CurrentAttack == CombatType.Magic && CurrentSpell != null)
                return IsHoldOffensiveSpell(CurrentSpell);

            return false;
        }

        private bool SelectHoldRangedAttack(bool canMissile, bool canMagic)
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
                    spellId = WorldEventHoldRules.PickOffensiveSpell(Biota.PropertiesSpellBook, IsHoldOffensiveSpell, ACE.Common.ThreadSafeRandom.Next(0.0f, 1.0f));
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
        private bool HoldCanFireMissile()
        {
            var weapon = GetEquippedMissileWeapon();

            return weapon != null && (!weapon.IsAmmoLauncher || GetEquippedAmmo() != null);
        }

        /// <summary>
        /// Whether each spell id is a ranged hold's attack, memoized: the monster tick asks every tick. Cleared with the other
        /// spell caches (/clearcache spell, and a spell re-import) through <see cref="ClearHoldOffensiveSpellCache"/>.
        /// </summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, bool> holdOffensiveSpells = new System.Collections.Concurrent.ConcurrentDictionary<uint, bool>();

        /// <summary>Drops the memoized offensive-spell classifications (the spell caches' clear path).</summary>
        public static void ClearHoldOffensiveSpellCache() => holdOffensiveSpells.Clear();

        /// <summary>Classifies an already built spell (pure on its fields): a projectile, or a harmful health Boost.</summary>
        private static bool ClassifyHoldOffensiveSpell(ACE.Server.Entity.Spell spell)
        {
            if (spell == null || spell.NotFound)
                return false;

            return WorldEventHoldRules.IsOffensiveSpell(null, (int)spell.NumProjectiles, spell.MetaSpellType == SpellType.Boost ? Math.Min(spell.Boost, spell.MaxBoost) : 0, (int)spell.VitalDamageType);
        }

        /// <summary>The current spell's classification, memoized by id; never builds a Spell.</summary>
        private static bool IsHoldOffensiveSpell(ACE.Server.Entity.Spell spell)
        {
            if (spell == null)
                return false;

            try
            {
                return holdOffensiveSpells.GetOrAdd(spell.Id, _ => ClassifyHoldOffensiveSpell(spell));
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>A spellbook id's classification, memoized; builds the Spell only on the first sight of an id.</summary>
        private static bool IsHoldOffensiveSpell(uint spellId)
        {
            try
            {
                return holdOffensiveSpells.GetOrAdd(spellId, id => ClassifyHoldOffensiveSpell(new ACE.Server.Entity.Spell(id)));
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Cached: whether this monster's spellbook holds an offensive spell.</summary>
        private bool HoldHasOffensiveSpell()
        {
            var s = HoldState;

            if (s.HasOffensiveSpell == null)
            {
                try
                {
                    var book = Biota.PropertiesSpellBook;
                    s.HasOffensiveSpell = book != null && WorldEventHoldRules.PickOffensiveSpell(book, IsHoldOffensiveSpell, 0) != 0;
                }
                catch (Exception ex)
                {
                    log.Warn($"[WORLDEVENT] offensive spell check for {Name} 0x{Guid.Full:X8} failed", ex);
                    s.HasOffensiveSpell = false;
                }
            }

            return s.HasOffensiveSpell.Value;
        }

        /// <summary>The stock chase move, used to walk a ranged monster in; the hold tick stops it at the keep distance.</summary>
        private void StartHoldRangedApproach()
        {
            if (!MoveReady())
                return;

            if (MoveSpeed == 0.0f)
                GetMovementSpeed();

            MoveTo(AttackTarget, RunRate);

            IsMoving = true;
            LastMoveTime = Timers.RunningTime;
            NextCancelTime = LastMoveTime + ACE.Common.ThreadSafeRandom.Next(2, 4);

            var mvp = new ACE.Server.Physics.Animation.MovementParameters();
            mvp.Flags &= ~ACE.Server.Physics.Animation.MovementParamFlags.CanWalk;
            mvp.Flags |= GetMonsterMoveToFlags(false, DisableSticky);
            mvp.Speed = CurrentMovementSlowFactor;

            PhysicsObj.MoveToObject(AttackTarget.PhysicsObj, mvp);
            PhysicsObj.UpdateTime = ACE.Server.Physics.Common.PhysicsTimer.CurrentTime;
        }

        /// <summary>Stops the approach where the monster stands, without dropping its target (CancelMoveTo would re-target).</summary>
        private void StopHoldRangedApproach()
        {
            PhysicsObj.MovementManager.MoveToManager.CancelMoveTo(WeenieError.ActionCancelled);
            PhysicsObj.MovementManager.MoveToManager.FailProgressCount = 0;

            EnqueueBroadcastMotion(new Motion(CurrentMotionState.Stance, MotionCommand.Ready));

            IsMoving = false;
        }

        // ---- H4b: the knight's cosmetic defense (own landblock thread) --------------------------------------------------

        /// <summary>The swing animations each motion table has in each stance, found once.</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<(uint Table, MotionStance Stance), MotionCommand[]> holdDefendSwingCache =
            new System.Collections.Concurrent.ConcurrentDictionary<(uint, MotionStance), MotionCommand[]>();

        /// <summary>A swing's length when the motion table gives none.</summary>
        private const float DefaultHoldDefendSwingSeconds = 1.5f;

        /// <summary>
        /// One cosmetic defense step for this knight (protect.defendPose, defend_pose): <paramref name="foe"/> is the run
        /// monster in reach (H6's tick picks it with WorldEventHoldRules.PickDefendFoe), or null. It keeps its combat stance,
        /// turns to the monster and plays swing animations at it, then turns back outward. PURELY VISUAL: a swing is one motion
        /// broadcast whose facing target is the monster - nothing here calls a damage, proc, spell or threat path, sets
        /// AttackTarget, or writes to the monster, and the knight never leaves its spot. A player is never offered or faced.
        /// Returns what it did. Landblock thread of this knight.
        /// </summary>
        public HoldPoseAction TickHoldDefendPose(Creature foe, double now, Random rng)
        {
            var tag = WorldEventHold;
            var t = tag?.Run?.Tuning;

            if (IsDead || tag?.Kind != HoldUnitKind.Knight || t == null || !t.DefendPoseEnabled)
                return HoldPoseAction.None;

            var s = HoldState;

            s.DefendHome ??= Location != null ? new ACE.Entity.Position(Location) : null;

            var inStance = CurrentMotionState != null && CurrentMotionState.Stance != MotionStance.NonCombat
                           && CurrentMotionState.Stance != MotionStance.Invalid;

            if (foe != null && (foe.IsDead || foe.Guid == Guid || foe is Player))
                foe = null;

            if (foe != null)
            {
                s.DefendLastFoeAt = now;
                s.DefendFoe = foe.Guid.Full;
            }
            else
                s.DefendFoe = 0;

            var angle = foe != null && Location != null && foe.Location != null ? GetAngle(foe) : 0;

            var action = WorldEventHoldRules.DecidePose(inStance, foe != null, angle, now, s.DefendNextSwingAt, s.DefendLastFoeAt,
                s.DefendFacingOutward, t.DefendFaceDegrees, t.DefendTurnBackSeconds);

            switch (action)
            {
                case HoldPoseAction.DrawWeapon:
                    if (CurrentLandblock == null)
                        CurrentMotionState = new Motion(MotionStance.HandCombat);    // no world (a test): the stance alone
                    else
                        SetCombatMode(CombatMode.Melee);
                    break;

                case HoldPoseAction.Face:
                    s.DefendFacingOutward = false;

                    if (CurrentLandblock != null)
                        s.DefendNextSwingAt = Math.Max(s.DefendNextSwingAt, now + Rotate(foe));
                    break;

                case HoldPoseAction.Swing:
                    s.DefendFacingOutward = false;
                    PlayHoldDefendSwing(foe, now, t, rng);
                    break;

                case HoldPoseAction.FaceOutward:
                    s.DefendFacingOutward = true;

                    if (CurrentLandblock != null && s.DefendHome != null)
                        TurnTo(s.DefendHome);
                    break;
            }

            return action;
        }

        /// <summary>How many cosmetic swings this knight has played (tests and the log).</summary>
        internal int HoldDefendSwings => holdCombatState?.DefendSwings ?? 0;

        /// <summary>The swing (an animation only), aimed at <paramref name="foe"/>; the next swing after the animation and the rhythm.</summary>
        private MotionCommand PlayHoldDefendSwing(Creature foe, double now, WorldEventHoldTuning t, Random rng)
        {
            rng ??= new Random();

            var stance = CurrentMotionState?.Stance ?? MotionStance.HandCombat;
            var swings = HoldDefendSwingMotions(stance);
            var command = swings[rng.Next(swings.Length)];

            float length = 0;

            try
            {
                length = ACE.Server.Physics.Animation.MotionTable.GetAnimationLength(MotionTableId, stance, command);
            }
            catch (Exception)
            {
                // No motion table readable: the default length keeps the rhythm.
            }

            if (!(length > 0))
                length = DefaultHoldDefendSwingSeconds;

            if (CurrentLandblock != null && foe != null)
            {
                var motion = new Motion(this, command, 1.0f);
                motion.MotionState.TurnSpeed = 2.25f;
                motion.TargetGuid = foe.Guid;

                CurrentMotionState = motion;
                EnqueueBroadcastMotion(motion);
            }

            var s = HoldState;
            s.DefendSwings++;
            s.DefendNextSwingAt = WorldEventHoldRules.NextDefendSwingAt(now, length, rng.NextDouble(), t.DefendSwingSeconds, t.DefendSwingMaxSeconds);

            return command;
        }

        /// <summary>The swing motions this knight's motion table has in <paramref name="stance"/>; the stance's first candidate when none is found.</summary>
        private MotionCommand[] HoldDefendSwingMotions(MotionStance stance)
        {
            return holdDefendSwingCache.GetOrAdd((MotionTableId, stance), key =>
            {
                var candidates = WorldEventHoldRules.DefendSwingCandidates(key.Stance);
                var found = new List<MotionCommand>();

                foreach (var candidate in candidates)
                {
                    try
                    {
                        if (key.Table != 0 && ACE.Server.Physics.Animation.MotionTable.GetAnimationLength(key.Table, key.Stance, candidate) > 0)
                            found.Add(candidate);
                    }
                    catch (Exception)
                    {
                        // An unreadable table: fall through to the first candidate.
                    }
                }

                return found.Count > 0 ? found.ToArray() : new[] { candidates[0] };
            });
        }
    }
}
