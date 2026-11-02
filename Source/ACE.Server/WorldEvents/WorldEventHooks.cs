using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldEvents.Hold;
using ACE.Server.WorldObjects;

using ShardBiota = ACE.Database.Models.Shard.Biota;

namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// The one seam between shared engine code and World Events. Every World Events check, hook call and
    /// lifecycle call made from outside Source/ACE.Server/WorldEvents/ (WorldObjects, Entity, Managers,
    /// Program, and the systems that read the shared species tables) goes through a member of this class,
    /// so the whole engine-facing surface of the system can be audited in this one file.
    /// WorldEventHooksCallSiteTests enforces that by source scan.
    ///
    /// Rules every member follows:
    ///   * It forwards to the existing implementation (WorldEventManager, WorldEventCombatCreditHook,
    ///     WorldEventBossDamageHook, the Creature sky-drop partial, ...). Behaviour lives there, not here.
    ///   * Where the check is a pure decision it has a pure core over primitive values, next to a thin
    ///     adapter that takes the WorldObject and reads exactly what the original call site read, in the
    ///     same order and with the same short-circuiting.
    ///   * For an object no run owns, every member returns the identity value (false, the same list, or a
    ///     no-op) after the same field reads the original site made: no allocation and no lock is added on
    ///     a hot path.
    /// </summary>
    public static class WorldEventHooks
    {
        // ---- lifecycle -------------------------------------------------------------------------------------

        /// <summary>Program startup: loads the axis store and builds the catalog. Never throws.</summary>
        public static void Initialize() => WorldEventManager.Initialize();

        /// <summary>World heartbeat. The caller keeps its own try/catch around this.</summary>
        public static void Tick() => WorldEventManager.Tick();

        /// <summary>Server shutdown: ends any run cleanly.</summary>
        public static void OnShutdown() => WorldEventManager.OnShutdown();

        /// <summary>
        /// The loaded axis store, never null (it starts as WorldEventAxisStore.Empty). Read by systems that
        /// share its species tables (Pinnacles, Speed Courses, Threads). Exposed as the store itself, not its
        /// tables, so each caller keeps its own null handling exactly as it was.
        /// </summary>
        public static WorldEventAxisStore Store
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => WorldEventManager.Store;
        }

        /// <summary>
        /// Program startup: the admin panel's World Event start-page service, wired to the world thread
        /// (<paramref name="enqueue"/>) and the Audit channel (<paramref name="writeAudit"/>).
        /// </summary>
        public static WorldEventAdminService CreateAdminService(Action<IAction> enqueue, Action<string, string> writeAudit, int timeoutMs)
            => WorldEventAdminService.CreateLive(enqueue, writeAudit, timeoutMs);

        // ---- landblock load and persistence ----------------------------------------------------------------

        /// <summary>
        /// Landblock dynamic load: drops every shard biota a crashed run left behind (PropertyInt.WorldEventId)
        /// and queues its deletion. Never throws; on failure hands back <paramref name="dynamics"/> unfiltered.
        /// </summary>
        public static List<ShardBiota> FilterOrphanedDynamics(ushort landblockId, uint instance, List<ShardBiota> dynamics)
            => WorldEventOrphanFilter.FilterAndSweep(landblockId, instance, dynamics);

        /// <summary>Pure core of <see cref="IsTransientEventObject(WorldObject)"/>: a run stamp means "never persist".</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsTransientEventObject(int? runIdStamp) => runIdStamp != null;

        /// <summary>
        /// True when <paramref name="wo"/> carries a run stamp (PropertyInt.WorldEventId): everything a running
        /// event spawned is transient by construction and must not be saved to the shard.
        /// </summary>
        public static bool IsTransientEventObject(WorldObject wo)
            => IsTransientEventObject(wo.GetProperty(PropertyInt.WorldEventId));

        /// <summary>
        /// A generator just placed <paramref name="wo"/>. Adopts it into the current run when the generator
        /// is one the run holds. One static field read when no run exists.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void OnGeneratedIntoWorld(WorldObject wo, WorldObject generator)
            => WorldEventManager.OnGeneratedIntoWorld(wo, generator);

        // ---- combat credit and boss damage -----------------------------------------------------------------

        /// <summary>
        /// DamageHistory.Add recorded a health hit. HOT PATH: for a hit no run creature is part of, the
        /// implementation returns after a field read and a type test or two.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void OnDamageRecorded(Creature victim, WorldObject attacker, uint amount)
            => WorldEventCombatCreditHook.OnDamageRecorded(victim, attacker, amount);

        /// <summary>
        /// A landed hit that wrote no health (fully absorbed, or a stamina/mana attack). Same direction rules
        /// and the same cheap rejection as <see cref="OnDamageRecorded"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void OnCombatContact(Creature victim, WorldObject attacker)
            => WorldEventCombatCreditHook.OnCombatContact(victim, attacker);

        /// <summary>
        /// Pure: true when a player victim in this state never actually takes the recorded damage
        /// (Invincible, or under lifestone protection). Also reused by the PvP damage credit.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool VictimStateRefusesCredit(bool invincible, bool underLifestoneProtection)
            => WorldEventCombatCreditHook.VictimStateRefusesCredit(invincible, underLifestoneProtection);

        /// <summary>
        /// Measured boss damage scaling: a hit landed on <paramref name="defender"/>. Inert (two field reads)
        /// for every hit not dealt by a world-event boss.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void NoteBossHit(WorldObject source, Player defender, int damageTaken, bool crit)
            => WorldEventBossDamageHook.NoteHit(source, defender, damageTaken, crit);

        // ---- creature death and pooled loot ----------------------------------------------------------------

        /// <summary>
        /// Pure core of <see cref="OnCreatureDied"/>: a death is reported to the run only when BOTH the
        /// in-memory back-reference and the persisted run stamp are present, so a creature from a finished
        /// run is inert.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool ReportsDeathToRun(bool hasEventReference, int? runIdStamp)
            => hasEventReference && runIdStamp != null;

        /// <summary>
        /// Creature.Die: report this death to the run that spawned it. The back-reference is read first and
        /// the property only when it is set, exactly as the original inline check did.
        /// </summary>
        public static void OnCreatureDied(Creature creature, DamageHistoryInfo lastDamager, DamageHistoryInfo topDamager)
        {
            if (creature.P_WorldEvent == null)
                return;

            if (ReportsDeathToRun(true, creature.GetProperty(PropertyInt.WorldEventId)))
                WorldEventManager.OnEventCreatureDied(creature, lastDamager, topDamager);
        }

        /// <summary>
        /// Pure core of <see cref="IsPooledLootDeath(Creature)"/>: both run keys, the run's per-run latch on,
        /// and not the run's boss. The rule itself lives in Creature.IsWorldEventPooledDeath.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsPooledLootDeath(bool hasEventReference, int? runIdStamp, bool runLatchedOn, bool isBoss)
            => Creature.IsWorldEventPooledDeath(hasEventReference, runIdStamp, runLatchedOn, isBoss);

        /// <summary>
        /// Creature.Die: true when this death banks its loot on the run instead of leaving a corpse. The
        /// back-reference test comes first and keeps the property read off every non-event death; the
        /// expression is the one that sat inline in Die.
        /// </summary>
        public static bool IsPooledLootDeath(Creature creature)
            => creature.P_WorldEvent != null
               && Creature.IsWorldEventPooledDeath(creature.P_WorldEvent, creature.GetProperty(PropertyInt.WorldEventId), creature.Guid.Full);

        /// <summary>Banks a pooled kill on its run. May throw; Die catches and falls back to a corpse.</summary>
        public static bool BankPooledKill(Creature creature, DamageHistoryInfo killer)
            => WorldEventLootBank.BankKill(creature, killer);

        // ---- player death ----------------------------------------------------------------------------------

        /// <summary>Pure core of <see cref="ProtectsDeathAt(Position)"/>; see WorldEventManager.ProtectsDeathAtCore.</summary>
        public static bool ProtectsDeathAt(bool worldEventsEnabled, bool deathProtectionEnabled,
            WorldEventState? state, Position loc, Position anchor, float radius)
            => WorldEventManager.ProtectsDeathAtCore(worldEventsEnabled, deathProtectionEnabled, state, loc, anchor, radius);

        /// <summary>
        /// Asheron's Protection: a death at <paramref name="loc"/>, inside an Announced or Active run's radius,
        /// waives the normal death penalties. Never throws.
        /// </summary>
        public static bool ProtectsDeathAt(Position loc) => WorldEventManager.ProtectsDeathAt(loc);

        /// <summary>
        /// The death-penalty skips (item loss, slag and PK loot): true while this player's current death is an
        /// Asheron's Protection death, latched once by Player.Die from <see cref="ProtectsDeathAt(Position)"/>.
        /// One property read; false for every death outside a run.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsProtectedDeathInProgress(Player player) => player.WorldEventDeathInProgress;

        /// <summary>Presence payout: a player died. One field read when no run exists; never throws.</summary>
        public static void OnPlayerDied(Player player) => WorldEventManager.OnPlayerDied(player);

        // ---- use handlers ----------------------------------------------------------------------------------

        /// <summary>
        /// Gem.ActOnUse: the event reward crate (PropertyBool.WorldEventRewardCrate). True when the crate
        /// handled the use; the marker test is inside the handler.
        /// </summary>
        public static bool TryUseRewardCrate(WorldObject crate, Player player)
            => WorldEventRewardCrate.TryHandleUse(crate, player);

        /// <summary>Pure core of <see cref="TryUseCache"/>'s opt-in: PropertyBool.WorldEventCache is exactly true.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsEventCache(bool? worldEventCacheFlag) => worldEventCacheFlag == true;

        /// <summary>
        /// GenericObject.ActOnUse: the Weave Cache reward giver. The handler runs only for an object that opts
        /// in by PropertyBool.WorldEventCache; true when it handled the use.
        /// </summary>
        public static bool TryUseCache(WorldObject cache, Player player)
            => IsEventCache(cache.GetProperty(PropertyBool.WorldEventCache)) && WorldEventCacheHandler.TryHandleUse(cache, player);

        // ---- objective creatures and the monster tick ------------------------------------------------------

        /// <summary>Pure core of <see cref="IsObjectiveCreature(WorldObject)"/>: PropertyBool.WorldEventObjective is exactly true.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsObjectiveCreature(bool? worldEventObjectiveFlag) => worldEventObjectiveFlag == true;

        /// <summary>
        /// Read once at construction (Creature.SetEphemeralValues) and cached on Creature.WorldEventObjective:
        /// true for a Rift or Element Portal pillar.
        /// </summary>
        public static bool IsObjectiveCreature(WorldObject wo)
            => IsObjectiveCreature(wo.GetProperty(PropertyBool.WorldEventObjective));

        /// <summary>Pure core of <see cref="SkipsMonsterTick(Creature)"/>; the rule lives in WorldEventObjectiveRules.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool SkipsMonsterTick(bool worldEventObjective)
            => WorldEventObjectiveRules.SkipsMonsterTick(worldEventObjective);

        /// <summary>Monster_Tick: an objective creature never thinks. One cached field read.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool SkipsMonsterTick(Creature creature)
            => SkipsMonsterTick(creature.WorldEventObjective);

        // ---- sky drop --------------------------------------------------------------------------------------

        /// <summary>
        /// True while <paramref name="creature"/> is still falling in from a world-event sky spawn: the physics
        /// tick always runs for it and the monster tick holds. One field read; false for every other creature.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsSkyDropping(Creature creature) => creature.WorldEventSkyDrop;

        /// <summary>Pure core of <see cref="CompleteSkyDropAcquire(Creature)"/>: acquire only when a landing raised the flag.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool CompletesSkyDropAcquire(bool pendingAcquire) => pendingAcquire;

        /// <summary>
        /// Monster_Tick, the first tick after a sky-drop landing: acquire a target. One field read and no call
        /// for every creature that did not just land.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void CompleteSkyDropAcquire(Creature creature)
        {
            if (CompletesSkyDropAcquire(creature.WorldEventSkyDropPendingAcquire))
                creature.CompleteSkyDropAcquire();
        }

        /// <summary>
        /// The physics tick, for a creature already known to be sky-dropping: its fall is over when it stands on
        /// walkable ground or its deadline has passed. The pure rule is WorldObject.SkyDropSettled.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool SkyDropSettled(bool onWalkable, double now, Creature creature)
            => WorldObject.SkyDropSettled(onWalkable, now, creature.WorldEventSkyDropDeadline);

        // ---- event NPCs ------------------------------------------------------------------------------------

        /// <summary>
        /// EmoteManager.PlaysStyledIdle: an event NPC placed by WorldEventSpawner.PlaceNpcs may play its idle in
        /// a styled stance. One field read; false for every other creature.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsStyledIdleNpc(Creature creature) => creature.WorldEventStyledIdle;

        // ---- Hold: reach and damage (H4a) ----------------------------------------------------------------------
        //
        // Every member: the identity case is "the creature has no Hold tag" - one field read (Creature.WorldEventHold)
        // and the input handed back unchanged, with no arithmetic. Identity is the tag's Kind and RunId plus the run's
        // IsActive, never the P_WorldEvent back-reference and never PropertyInt.WorldEventId.

        /// <summary>Pure core of <see cref="HoldPhysicsRunIds(Creature)"/>: (knightRun, monsterRun), (0, 0) for no tag.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static (uint KnightRun, uint MonsterRun) HoldPhysicsRunIds(HoldUnitKind? kind, uint runId)
            => WorldEventHoldRules.PhysicsRunIds(kind, runId);

        /// <summary>
        /// WeenieObject's constructor (during EnterWorld, after the spawner set the tag): the run ids the physics layer
        /// caches, so the per-object visibility filter is a field compare. (0, 0) for every creature with no tag.
        /// </summary>
        public static (uint KnightRun, uint MonsterRun) HoldPhysicsRunIds(Creature creature)
        {
            var tag = creature.WorldEventHold;

            return tag == null ? default : HoldPhysicsRunIds(tag.Kind, tag.RunId);
        }

        /// <summary>
        /// ObjectMaint (hot physics path, cached fields only): a knight is an attack target for a monster of its own run.
        /// False after one compare for every object that is not a knight (knightRun 0).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool PhysicsIsHoldKnightTargetFor(uint knightRun, bool attackerIsMonster, uint attackerRun)
            => WorldEventHoldRules.PhysicsIsKnightTargetFor(knightRun, attackerIsMonster, attackerRun);

        /// <summary>ObjectMaint: the cached knight run id marks a knight (it tracks no targets of its own). One compare.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool PhysicsIsHoldKnight(uint knightRun) => knightRun != 0;

        /// <summary>Pure core of <see cref="TryHoldCanDamage"/>; the rule is WorldEventHoldRules.MayDamageKnight.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool HoldMayDamageKnight(uint knightRun, bool attackerIsPetOrPlayer, HoldUnitKind? attackerKind, uint attackerRun, bool runActive)
            => WorldEventHoldRules.MayDamageKnight(knightRun, attackerIsPetOrPlayer, attackerKind, attackerRun, runActive);

        /// <summary>
        /// Creature.CanDamage (monster or pet attacking a non-player): true when <paramref name="target"/> is a knight, with
        /// <paramref name="canDamage"/> the decision (only a monster of its own active run). False - decide as before -
        /// for every target that is not a knight, after one field read.
        /// </summary>
        public static bool TryHoldCanDamage(Creature attacker, Creature target, out bool canDamage)
        {
            var tag = target.WorldEventHold;

            if (tag == null || tag.Kind != HoldUnitKind.Knight)
            {
                canDamage = false;
                return false;
            }

            canDamage = attacker.IsHoldKnightAttackerOf(target);
            return true;
        }

        /// <summary>Pure core of <see cref="IsHoldKnight(Creature)"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsHoldKnight(HoldUnitKind? kind) => kind == HoldUnitKind.Knight;

        /// <summary>
        /// True for a Hold knight: it is never picked by a normal targeting tactic, never retaliates or wakes, and a
        /// monster aimed at one never recruits its pack. One field read; false for null and every other creature.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsHoldKnight(Creature creature) => IsHoldKnight(creature?.WorldEventHold?.Kind);

        /// <summary>
        /// Player.CanDamage and CheckPKStatusVsTarget: a knight refuses every player attack and harmful spell (the cleave,
        /// multi-shot and AoE candidate filters ask CheckPKStatusVsTarget, not CanDamage). One field read.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool RefusesPlayerHarm(Creature target) => IsHoldKnight(target);

        /// <summary>
        /// DamageEvent, Monster_Melee and TryResistSpell: <paramref name="attacker"/> is a monster of the active run that
        /// placed knight <paramref name="target"/>. Such a hit always lands (no evade, no armor, no resist; its size is set
        /// in TakeDamage). False after one field read of the target for everything else.
        /// </summary>
        public static bool IsHoldKnightTargetFor(Creature attacker, Creature target)
        {
            if (target?.WorldEventHold == null || attacker == null)
                return false;

            return attacker.IsHoldKnightAttackerOf(target);
        }

        /// <summary>WorldObject.TryResistSpell: a knight never resists its own run's monsters' spells. False after one type test and field read for everything else.</summary>
        public static bool HoldKnightNeverResists(WorldObject caster, WorldObject target)
            => target is Creature knight && knight.WorldEventHold != null && caster is Creature attacker && IsHoldKnightTargetFor(attacker, knight);

        /// <summary>
        /// DamageEvent, SpellProjectile and life-magic Harm on a player: the attacker's Hold multiplier (its role's
        /// player_damage_mult; the finale boss's own with its enrage). Exactly 1.0 for a null attacker and every creature
        /// with no tag, after one field read; the call site skips its write when the value is 1.
        /// </summary>
        public static double HoldPlayerDamageMult(Creature attacker)
        {
            if (attacker?.WorldEventHold == null)
                return 1.0;

            return attacker.HoldPlayerDamageMult(ACE.Common.Time.GetUnixTime());
        }

        /// <summary>
        /// DamageEvent (melee, missile), SpellProjectile and life-magic Harm (magic) on a non-player: the Hold ward on a
        /// warded ranged wave monster. <paramref name="damage"/> unchanged for every creature with no tag or no ward.
        /// </summary>
        public static float HoldWardDamage(Creature defender, WorldEventCombatKinds kind, float damage)
        {
            var tag = defender?.WorldEventHold;

            if (tag == null || !tag.Warded)
                return damage;

            return defender.ApplyHoldWard(kind, damage, ACE.Common.Time.GetUnixTime());
        }

        /// <summary>
        /// Monster TakeDamage, SpellProjectile.DamageTarget and life-magic Harm: a run monster's hit on knight
        /// <paramref name="knight"/>, re-sized to the run's pacing (undefended_seconds, min_death_seconds, the corrupted
        /// factor, the role and finale multipliers). <paramref name="amount"/> unchanged for every creature that is not a
        /// knight, after one field read.
        /// </summary>
        public static float ScaleHoldKnightHit(Creature knight, WorldObject source, float amount)
        {
            var tag = knight.WorldEventHold;

            if (tag == null || tag.Kind != HoldUnitKind.Knight)
                return amount;

            return knight.ScaleHoldKnightHit(source, amount, ACE.Common.Time.GetUnixTime());
        }

        /// <summary>
        /// Monster FindNextTarget, after the Taunt hold: a seeker takes a knight, a hunter a recent healer. False (normal
        /// targeting) after one field read for every creature with no tag.
        /// </summary>
        public static bool TrySelectHoldTarget(Creature monster)
        {
            if (monster.WorldEventHold == null)
                return false;

            return monster.TrySelectHoldTarget();
        }

        /// <summary>
        /// The seeker lock (AlertMonster, MonsterOnAttackMonster, AlertFriendly, Player.OnAttackMonster): a seeker woken or
        /// hit stays on its knight, and the caller skips its own retarget when this returns true. False after one field read
        /// for every creature that is not a seeker.
        /// </summary>
        public static bool KeepsHoldSeekerLock(Creature monster, WorldObject disturbedBy)
        {
            var tag = monster.WorldEventHold;

            if (tag == null || !tag.Seeker)
                return false;

            return monster.TryKeepHoldSeekerLock(disturbedBy);
        }

        /// <summary>
        /// WorldEventCombatCreditHook.OnDamageRecorded: a player's (or a player's pet's) damage on the Hold finale boss is
        /// logged on the boss for its pull (Creature.HoldBossDamageSnapshot, read by the finale controller). Nothing after
        /// one field read for every victim that is not a finale boss. Any thread.
        /// </summary>
        public static void NoteHoldBossDamage(Creature victim, WorldObject attacker, uint amount)
        {
            if (victim?.WorldEventHold?.Kind != HoldUnitKind.FinaleBoss)
                return;

            victim.NoteHoldBossDamage(attacker, amount, ACE.Common.Time.GetUnixTime());
        }

        // ---- Hold: heals, pacing and cosmetics (H4b) ------------------------------------------------------------

        /// <summary>
        /// Healer kit and life-magic heal (the Boost health branch): a player's heal of <paramref name="raw"/> points on a
        /// knight. True when <paramref name="target"/> is a knight - the caller then does NOT write the heal itself: the run
        /// remembers the healer (for hunters) now, and the conversion, the full-health refusal, the budget and the write all
        /// happen in an action queued on the KNIGHT's landblock (WorldEventHoldHeals), which then sends the player the
        /// result line (<paramref name="kitRemaining"/> is the kit's uses-left tail, null for a spell). False after one field
        /// read for every creature that is not a knight.
        /// </summary>
        public static bool TryQueuePlayerHeal(Player healer, Creature target, double raw, bool critical, string kitRemaining, ChatMessageType chatType)
        {
            var tag = target?.WorldEventHold;

            if (tag == null || tag.Kind != HoldUnitKind.Knight)
                return false;

            tag.Run?.NoteKnightHealer(healer, ACE.Common.Time.GetUnixTime());

            var knightName = target.Name;

            void Reply(WorldEventHoldHeals.Result result)
            {
                var text = WorldEventHoldRules.PlayerHealLine(knightName, result.Healed, result.Outcome, critical, kitRemaining);

                if (!string.IsNullOrEmpty(text))
                    healer?.Session?.Network.EnqueueSend(new GameMessageSystemChat(text, chatType));
            }

            if (!WorldEventHoldHeals.QueuePlayerHeal(target, raw, Reply))
                Reply(new WorldEventHoldHeals.Result(0, HoldHealOutcome.Refused));

            return true;
        }

        /// <summary>Pure core of <see cref="IsInvalidHoldSpellTarget"/>: only a health heal, and only while the knight accepts player heals.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsInvalidHoldKnightSpell(bool acceptsPlayerHeal, bool isHealSpell) => !(acceptsPlayerHeal && isHealSpell);

        /// <summary>
        /// Player.IsInvalidTarget: true when <paramref name="target"/> is a knight, with <paramref name="invalid"/> the decision
        /// (a health heal while player_heal is on is the one valid spell). False - decide as before - after one field read for
        /// every other target.
        /// </summary>
        public static bool IsInvalidHoldSpellTarget(Creature target, Spell spell, out bool invalid)
        {
            if (target?.WorldEventHold?.Kind != HoldUnitKind.Knight)
            {
                invalid = false;
                return false;
            }

            var isHeal = spell != null && WorldEventHoldRules.IsKnightHealSpell(spell.IsBeneficial, spell.MetaSpellType, spell.VitalDamageType, spell.Boost, spell.MaxBoost);

            invalid = IsInvalidHoldKnightSpell(target.AcceptsHoldPlayerHeal, isHeal);
            return true;
        }

        /// <summary>Healer.HandleActionUseOnTarget: true for a knight a player's kit may heal now (alive, its run active, player_heal on). False after one field read for everything else.</summary>
        public static bool HoldAcceptsPlayerHeal(Creature target) => target?.WorldEventHold != null && target.AcceptsHoldPlayerHeal;

        /// <summary>Healer.HandleActionUseOnTarget: true for a Hold knight (the kit path is the knight's, accepted or refused). One field read.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsHoldKnightHealTarget(WorldObject target) => target is Creature c && IsHoldKnight(c);

        /// <summary>Pure core of <see cref="TryUseHoldShrine"/>: only an object a run stamped can be a run's shrine.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool MayBeHoldShrine(int? runIdStamp) => runIdStamp != null && runIdStamp.Value != 0;

        /// <summary>
        /// GenericObject.ActOnUse, next to TryUseCache: a Shrine of Restoration of the running Hold run. True when the run
        /// handled the use (its reply, if any, is sent here). False after one property read for every object no run stamped.
        /// </summary>
        public static bool TryUseHoldShrine(WorldObject shrine, Player player)
        {
            var stamp = shrine.GetProperty(PropertyInt.WorldEventId);

            if (!MayBeHoldShrine(stamp) || !(WorldEventManager.Current?.Objective is Objectives.HoldObjective hold) || hold.Run.RunId != (uint)stamp.Value)
                return false;

            if (!hold.Run.TryUseShrine(shrine, player, ACE.Common.Time.GetUnixTime(), out var reply))
                return false;

            if (!string.IsNullOrEmpty(reply))
                player?.Session?.Network.EnqueueSend(new GameMessageSystemChat(reply, ChatMessageType.Broadcast));

            return true;
        }

        /// <summary>Monster_Navigation.GetRunRate (never for a player): a runner's run-speed multiplier. <paramref name="runRate"/> unchanged after one field read for every creature with no tag.</summary>
        public static float HoldRunRate(Creature creature, float runRate)
        {
            var tag = creature.WorldEventHold;

            return tag == null ? runRate : WorldEventHoldRules.ScaledRunRate(runRate, tag.RunRateMult);
        }

        /// <summary>Monster_Melee.MeleeAttack, after ScaleWeakenedBloodSlowDuration: a runner's shorter recovery. Unchanged after one field read for every creature with no tag.</summary>
        public static double HoldMeleeDelay(Creature creature, double meleeDelay)
        {
            var tag = creature.WorldEventHold;

            return tag == null ? meleeDelay : WorldEventHoldRules.ScaledMeleeDelay(meleeDelay, tag.AttackSpeedMult);
        }

        /// <summary>Monster_Melee.DoSwingMotion: a runner's faster swing animation. Unchanged after one field read for every creature with no tag.</summary>
        public static float HoldAnimSpeed(Creature creature, float animSpeed)
        {
            var tag = creature.WorldEventHold;

            return tag == null ? animSpeed : WorldEventHoldRules.ScaledAnimSpeed(animSpeed, tag.AttackSpeedMult);
        }

        /// <summary>
        /// Monster_Tick, once the target distance is known: a ranged wave monster keeps its distance and attacks from there.
        /// True when the hold handled this tick (the caller returns). False after one field read for every creature with no tag.
        /// </summary>
        public static bool TickHoldRangedKeepDistance(Creature monster, float targetDist)
        {
            var tag = monster.WorldEventHold;

            if (tag == null || !(tag.HoldDistance > 0))
                return false;

            return monster.TickHoldRangedKeepDistance(targetDist);
        }

        /// <summary>Pure core of <see cref="HoldClientAttackable"/>: a knight that shows its health bar is sent as attackable.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool HoldClientAttackable(bool attackable, bool isKnight, bool showHealthBar) => attackable || (isKnight && showHealthBar);

        /// <summary>
        /// WorldObject_Networking: the Attackable bit sent to the CLIENT. A knight with protect.showHealthBar is sent as
        /// attackable so the client draws its health bar, while the server keeps refusing every player and pet attack on it.
        /// <paramref name="attackable"/> unchanged for everything else (no read at all when it is already true).
        /// </summary>
        public static bool HoldClientAttackable(WorldObject wo, bool attackable)
        {
            if (attackable || !(wo is Creature creature))
                return attackable;

            var tag = creature.WorldEventHold;

            return tag != null && HoldClientAttackable(attackable, tag.Kind == HoldUnitKind.Knight, tag.ShowHealthBar);
        }

        /// <summary>WorldObject.SerializeModelData, on the freshly calculated ObjDesc: a corrupted monster's tint over its whole palette. Nothing for every object that is not a creature with a tinted tag (one type test and one field read).</summary>
        public static void ApplyHoldTint(WorldObject wo, ACE.Entity.ObjDesc objDesc)
        {
            if (!(wo is Creature creature))
                return;

            var tag = creature.WorldEventHold;

            if (tag == null || tag.TintPalette == 0)
                return;

            WorldEventTint.Apply(objDesc, tag.TintPalette);
        }

        /// <summary>Player.HandleActionIdentifyObject: assessing a knight also says its health in chat (throttled). Nothing for any other target after one type test and field read.</summary>
        public static void SendHoldKnightHealthLine(Player player, WorldObject target)
        {
            if (!IsHoldKnightHealthLineTarget(target))
                return;

            player.SendHoldKnightHealthLine((Creature)target, ACE.Common.Time.GetUnixTime());
        }

        /// <summary>Pure core of <see cref="SendHoldKnightHealthLine"/>: only a Hold knight gets a health line.</summary>
        public static bool IsHoldKnightHealthLineTarget(WorldObject target) => target is Creature knight && knight.WorldEventHold?.Kind == HoldUnitKind.Knight;
    }
}
