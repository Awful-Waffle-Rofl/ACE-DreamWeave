using System;
using System.Collections.Generic;
using System.Reflection;

using ACE.Common;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.MlDigsite.Mechanics;
using ACE.Server.WorldObjects;

using log4net;

using Position = ACE.Entity.Position;

namespace ACE.Server.MlDigsite
{
    /// <summary>
    /// One mechanic module. Modules are STATELESS SINGLETONS - every mutable value lives in
    /// <see cref="MlDigsiteBossMechanicState"/>, keyed by mechanic - which is the same rule
    /// MonsterEffectHooks.cs states for effect handlers and for the same reason: one instance serves every
    /// live encounter at once.
    ///
    /// <see cref="Tick"/> is called once per driver tick (1 s) for EACH SLOT that carries this mechanic. A
    /// module that runs on a cadence asks <see cref="MlDigsiteMechanicContext.CadenceDue"/> rather than
    /// keeping its own clock; a module that watches something continuous (an immune threshold, an interrupt
    /// countdown) simply reads it every tick.
    /// </summary>
    public interface IMlDigsiteMechanic
    {
        MlDigsiteMechanic Kind { get; }

        void Tick(MlDigsiteMechanicContext ctx);
    }

    /// <summary>
    /// The Boss Rush mechanic driver: the set roll, the per-tick dispatch, and every service the five modules
    /// are allowed to reach the world through.
    ///
    /// WHY THIS LIVES HERE AND NOT IN MonsterEffects. Four of the five mechanics need OWNERSHIP - adds,
    /// ground markers and an interrupt object must be destroyed when the encounter ends, and
    /// MlDigsiteOrphanFilter must be able to sweep them after a hard restart. The digsite system is the only
    /// thing in this codebase that already has that; MonsterEffects has none and is architecturally committed
    /// to having none (stateless handlers, a fixed six-field state struct, no hook that sees another
    /// creature). The ONE capability MonsterEffects genuinely has and this driver does not - a damage filter
    /// wired at all three damage sinks - is the one thing taken from it, as the "immune" effect kind.
    ///
    /// THREADING. <see cref="Drive"/> runs on the WORLD thread, from MlDigsiteManager.Tick, which runs after
    /// LandblockManager.Tick has joined. <see cref="OnAddDied"/> runs on whichever LANDBLOCK thread killed the
    /// add and only records and schedules. <see cref="TryInterrupt"/> runs on the landblock thread that
    /// handled the player's use and likewise only records and schedules. Every WRITE to the world - a spawn,
    /// a prop, a vital - is handed to the owning landblock's action queue, never performed inline, because
    /// the world thread owns no landblock (the same discipline MlDigsiteManager.DestroyHeld
    /// already applies).
    ///
    /// PER-TICK COST, stated rather than assumed. For an encounter that is not Boss Rush, or that carries no
    /// driver state, this is one field read. For a Boss Rush encounter it is: one lock and one count check on
    /// the step queue (which returns null and allocates NOTHING when nothing is due - the common case), one
    /// health fraction read, and one small context object per occupied slot. Nothing here walks the online
    /// player list on a tick: that walk happens only inside <see cref="MlDigsiteMechanicContext.RadialHit"/>
    /// and <see cref="MlDigsiteMechanicContext.Participants"/>, which are reached only from a RESOLVE - a few
    /// times a minute per encounter - never from a cadence check. A module that called RadialHit on the tick
    /// cadence rather than on a resolve would be a defect.
    /// </summary>
    public static class MlDigsiteBossMechanics
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// The five modules, one per mechanic. Immune50 shares ImmunePhasesMechanic - it is that module with
        /// its threshold list narrowed to 0.50 alone (MlDigsiteBossMechanicRules.ImmuneThresholds), not a
        /// sixth implementation.
        /// </summary>
        private static readonly VolatileAddsMechanic volatileAdds = new VolatileAddsMechanic();
        private static readonly DrumCadenceMechanic drumCadence = new DrumCadenceMechanic();
        private static readonly ImmunePhasesMechanic immunePhases = new ImmunePhasesMechanic();
        private static readonly InterruptObjectMechanic interruptObject = new InterruptObjectMechanic();
        private static readonly SafeZonesMechanic safeZones = new SafeZonesMechanic();

        /// <summary>
        /// The driver's own rng. A PLAIN static, not a ThreadSafeRandom and not one per call, because every
        /// draw from it happens on the world thread inside <see cref="Drive"/> - the two landblock-thread
        /// entry points below roll nothing. The same argument MlDigsiteManager's own nextTick/nextReap
        /// statics rest on. Seeded from ThreadSafeRandom once, excluding int.MaxValue because
        /// ThreadSafeRandom.Next(int, int) is INCLUSIVE of its upper bound and overflows there.
        /// </summary>
        private static readonly Random driverRng = new Random(ThreadSafeRandom.Next(0, int.MaxValue - 1));

        // ---- the roll ------------------------------------------------------------------------------------

        /// <summary>
        /// Rolls this Boss Rush encounter's mechanic set and attaches the driver state. Called EXACTLY ONCE,
        /// from MlDigsiteManager.OpenEncounter's BossRush branch, immediately BEFORE the boss is placed and
        /// so before any player can have hit it. The ordering is load bearing in both directions:
        /// MlDigsiteSpawner reads the attached set to compose the boss's immune filter record and to scale a
        /// later add, and every other reader either sees the finished state or sees null.
        ///
        /// Returns the set that was attached, or null when nothing was (the driver is switched off, the table
        /// is empty, or a state is somehow already attached). A null is not an error: a Boss Rush with no
        /// driver state is exactly the fight this encounter type was before the mechanics landed.
        ///
        /// Rolls through the rng the SPAWN already carries rather than <see cref="driverRng"/>, because this
        /// runs on whatever thread completed the dig, not on the world thread.
        /// </summary>
        /// <param name="forcedSetId">
        /// A one-encounter override for ml_digsite_bossrush_set_force, from a treasure map's
        /// PropertyInt.TreasureMapForcedMechanicSet (/testtreasuremap bossrush &lt;setId&gt;). 0 (the
        /// default) defers to the live tunable exactly as before; a positive value pins that set for THIS
        /// roll only and never writes the tunable, so it can never leak into a later, unrelated dig.
        /// </param>
        public static MlDigsiteMechanicSet? Roll(MlDigsiteEncounter encounter, Random rng, long forcedSetId = 0)
        {
            if (encounter == null || encounter.Type != MlDigsiteType.BossRush)
                return null;

            if (!MlDigsiteTunables.BossRushMechanicsEnabled)
                return null;

            var setForce = forcedSetId > 0 ? forcedSetId : MlDigsiteTunables.BossRushSetForce;

            var set = MlDigsiteBossMechanicRules.RollSet(MlDigsiteTunables.BossRushSets, setForce, rng);

            if (set == null)
                return null;

            if (!encounter.TryAttachBossMechanics(new MlDigsiteBossMechanicState(set.Value, DateTime.UtcNow)))
                return null;

            return set;
        }

        // ---- the tick ------------------------------------------------------------------------------------

        /// <summary>
        /// One driver tick for one encounter, from the BossRush branch of MlDigsiteManager.Drive. Guarded by
        /// that method's own try/catch, and each step and each slot is additionally guarded here, so one bad
        /// mechanic cannot stop the other one or the rest of the encounter.
        /// </summary>
        public static void Drive(MlDigsiteEncounter encounter, DateTime now)
        {
            var state = encounter?.BossMechanics;

            if (state == null || !MlDigsiteTunables.BossRushMechanicsEnabled)
                return;

            // Due steps FIRST, and deliberately before the boss liveness check: a fuse lit by an add that died
            // a second ago should still land even on the tick the boss itself fell. The step's own closure
            // re-checks everything it touches.
            var due = state.TakeDueSteps(now);

            if (due != null)
            {
                for (var i = 0; i < due.Count; i++)
                {
                    try
                    {
                        due[i]();
                    }
                    catch (Exception ex)
                    {
                        log.Error($"[ML_DIGSITE] {encounter} boss mechanic step threw", ex);
                    }
                }
            }

            var boss = encounter.ObjectiveCreature;

            if (boss == null || boss.IsDestroyed || boss.IsDead)
                return;

            RunSlot(encounter, state, boss, now, MlDigsiteMechanicSlot.Main, state.Set.Main);
            RunSlot(encounter, state, boss, now, MlDigsiteMechanicSlot.Secondary, state.Set.Secondary);
        }

        private static void RunSlot(MlDigsiteEncounter encounter, MlDigsiteBossMechanicState state, Creature boss,
            DateTime now, MlDigsiteMechanicSlot slot, MlDigsiteMechanic mechanic)
        {
            var module = ModuleFor(mechanic);

            if (module == null)
                return;

            try
            {
                module.Tick(new MlDigsiteMechanicContext(encounter, state, boss, now, slot, mechanic, ArgsFor(mechanic), driverRng));
            }
            catch (Exception ex)
            {
                log.Error($"[ML_DIGSITE] {encounter} boss mechanic {mechanic} ({slot}) threw", ex);
            }
        }

        private static IMlDigsiteMechanic ModuleFor(MlDigsiteMechanic mechanic)
        {
            switch (mechanic)
            {
                case MlDigsiteMechanic.Volatile: return volatileAdds;
                case MlDigsiteMechanic.Drums: return drumCadence;
                case MlDigsiteMechanic.ImmunePhases: return immunePhases;
                case MlDigsiteMechanic.Immune50: return immunePhases;
                case MlDigsiteMechanic.Interrupt: return interruptObject;
                case MlDigsiteMechanic.SafeZones: return safeZones;
                default: return null;
            }
        }

        private static MlDigsiteMechanicArgs ArgsFor(MlDigsiteMechanic mechanic)
        {
            switch (mechanic)
            {
                case MlDigsiteMechanic.Volatile: return MlDigsiteTunables.BossRushVolatileArgs;
                case MlDigsiteMechanic.Drums: return MlDigsiteTunables.BossRushDrumsArgs;
                case MlDigsiteMechanic.ImmunePhases: return MlDigsiteTunables.BossRushImmuneArgs;
                case MlDigsiteMechanic.Immune50: return MlDigsiteTunables.BossRushImmuneArgs;
                case MlDigsiteMechanic.Interrupt: return MlDigsiteTunables.BossRushInterruptArgs;
                case MlDigsiteMechanic.SafeZones: return MlDigsiteTunables.BossRushSafeZonesArgs;
                default: return MlDigsiteMechanicArgs.Empty;
            }
        }

        // ---- the add death hook --------------------------------------------------------------------------

        /// <summary>
        /// One mechanic add has died. Routed from MlDigsiteManager.OnEncounterCreatureDied when
        /// MlDigsiteEncounter.NoteCreatureDeath returns MlDigsiteDeathKind.Add - which means this runs on a
        /// LANDBLOCK thread, inside Creature.Die, while the dying add still has its Location.
        ///
        /// It only RECORDS and SCHEDULES. The detonation itself is a step on the world-thread driver tick, so
        /// the damage pass never runs from inside a death.
        ///
        /// BOTH HALVES ARE OFFERED THE DEATH; each decides for itself whether it is theirs. The immune half
        /// acts only on an add belonging to the running phase, and the volatile half is GATED ON THE SET
        /// carrying the Volatile mechanic in either slot - an add called by an immune phase in a set with no
        /// volatile slot is a gate to be killed, not a bomb, and detonating it would punish exactly the thing
        /// the phase is asking players to do. Calling both is what lets an add that is part of an immune
        /// phase in a set that ALSO runs volatile detonate as well, since the two mechanics can occupy the
        /// two slots of one set.
        /// </summary>
        public static void OnAddDied(MlDigsiteEncounter encounter, Creature add)
        {
            var state = encounter?.BossMechanics;

            if (state == null || add == null)
                return;

            try
            {
                ImmunePhasesMechanic.OnAddDied(encounter, state, add);
                VolatileAddsMechanic.OnAddDied(encounter, state, add);
            }
            catch (Exception ex)
            {
                log.Error($"[ML_DIGSITE] {encounter} boss mechanic add-death hook threw for 0x{add.Guid.Full:X8}", ex);
            }
        }

        // ---- the interrupt object's use hook ---------------------------------------------------------------

        /// <summary>
        /// A player used this encounter's interrupt object. Reached from MlDigsiteInterruptObject.TryHandleUse,
        /// on the landblock thread that handled the use. Returns true when the use actually cancelled a
        /// pending hit, so the caller can tell the player something useful either way.
        /// </summary>
        public static bool TryInterrupt(MlDigsiteEncounter encounter, Player player)
        {
            var state = encounter?.BossMechanics;

            if (state == null || player == null)
                return false;

            try
            {
                return InterruptObjectMechanic.TryUse(encounter, state, player);
            }
            catch (Exception ex)
            {
                log.Error($"[ML_DIGSITE] {encounter} interrupt use threw for {player.Name}", ex);
                return false;
            }
        }

        // ---- the stop --------------------------------------------------------------------------------------

        /// <summary>
        /// Stops the driver for an encounter that is ending. Called from MlDigsiteManager's cleanup, BEFORE
        /// the held objects are destroyed, so no step can fire at a position whose objects have just gone.
        /// Every prop and add the driver placed is in the encounter's held list and is destroyed by that same
        /// cleanup, so there is nothing to take out of the world here.
        /// </summary>
        public static void Stop(MlDigsiteEncounter encounter)
        {
            var state = encounter?.BossMechanics;

            if (state == null)
                return;

            var dropped = state.ClearSteps();

            if (dropped > 0)
                log.Debug($"[ML_DIGSITE] {encounter} boss mechanic driver stopped with {dropped} pending step(s) dropped");
        }
    }

    /// <summary>
    /// Everything one module is allowed to see and do on one tick, and the ONLY route it has to the world.
    /// Built fresh per slot per tick (two small objects a second for a live Boss Rush encounter, of which
    /// there can be at most ml_digsite_max_concurrent) rather than reused and mutated, because a step's
    /// closure captures whatever it needs and a reused context would hand a step run seconds later the values
    /// of a different tick.
    ///
    /// THE INVARIANTS EVERY MODULE FOLLOWS, stated here because they are what keeps five modules built to one
    /// design from diverging:
    ///   1. no module holds state of its own - everything mutable is on <see cref="State"/>;
    ///   2. no module calls PropertyManager - every number comes from <see cref="Args"/>, or, in the two
    ///      landblock-thread entry points that have no context, from MlDigsiteTunables' own parsed args;
    ///   3. every missing arg falls back to the documented default, never to 0 or blank;
    ///   4. every player-facing line goes through <see cref="Say"/> (everyone at the dig) or
    ///      <see cref="Tell"/> (one player), never a hand-rolled GameMessageSystemChat loop;
    ///   5. every world object goes through MlDigsiteProps (props) or MlDigsiteSpawner (creatures);
    ///   6. every delayed action goes through <see cref="Schedule"/> - no ActionChain, no timer;
    ///   7. all damage goes through <see cref="RadialHit"/> or <see cref="Hit"/> - never TakeDamage directly,
    ///      never a Hotspot;
    ///   8. the decision half of each mechanic is a pure static on MlDigsiteBossMechanicRules, and the tests
    ///      target that half;
    ///   9. nothing writes a persisted property;
    ///  10. every log line starts "[ML_DIGSITE] {encounter}";
    ///  11. every line a module sends to EVERYONE at the dig goes out as ChatMessageType.WorldBroadcast.
    ///
    /// Number 11 is the one a reconciliation pass over the five finished modules actually caught: four of
    /// them passed WorldBroadcast and the drum cadence alone took <see cref="Say"/>'s Broadcast default, so
    /// the same class of line arrived on two different channels depending on which mechanic sent it. The
    /// digsite's own convention is that its loud moments - the opening line, a mini-boss arriving - are
    /// WorldBroadcast, and a mechanic telegraph a player has seconds to react to is at least that urgent;
    /// losing a drum beat to combat spam defeats the mechanic, because counting the beats IS the mechanic.
    /// <see cref="Tell"/> is a different case and keeps its Broadcast default: it is one line to one player
    /// about their own margin, not a call to move.
    /// </summary>
    public sealed class MlDigsiteMechanicContext
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public MlDigsiteMechanicContext(MlDigsiteEncounter encounter, MlDigsiteBossMechanicState state, Creature boss,
            DateTime now, MlDigsiteMechanicSlot slot, MlDigsiteMechanic mechanic, MlDigsiteMechanicArgs args, Random rng)
        {
            Encounter = encounter;
            State = state;
            Boss = boss;
            Now = now;
            Slot = slot;
            Mechanic = mechanic;
            Args = args ?? MlDigsiteMechanicArgs.Empty;
            Rng = rng ?? new Random();
        }

        public MlDigsiteEncounter Encounter { get; }

        public MlDigsiteBossMechanicState State { get; }

        /// <summary>The live boss. Never null on a Tick call; a step's closure must re-check it.</summary>
        public Creature Boss { get; }

        public DateTime Now { get; }

        public MlDigsiteMechanicSlot Slot { get; }

        /// <summary>
        /// Which mechanic token this slot was authored with. Distinguishes Immune50 from ImmunePhases, which
        /// are the same module.
        /// </summary>
        public MlDigsiteMechanic Mechanic { get; }

        public MlDigsiteMechanicArgs Args { get; }

        /// <summary>World-thread-only rng. See MlDigsiteBossMechanics.driverRng.</summary>
        public Random Rng { get; }

        public Position Anchor => Encounter?.Anchor;

        // ---- cadence -------------------------------------------------------------------------------------

        /// <summary>
        /// Whether this slot's mechanic is due to run its cycle, applying the SECONDARY multiplier when this
        /// is a secondary slot. At most once per interval, and never on the very first tick of an encounter.
        /// </summary>
        public bool CadenceDue(double everySeconds)
        {
            var seconds = MlDigsiteBossMechanicRules.CadenceSeconds(everySeconds, Slot, MlDigsiteTunables.BossRushSecondaryCadenceMult);

            return State.TryClaimCadence(Slot, Now, seconds);
        }

        /// <summary>
        /// Queues a delayed action on the driver's own step queue. <paramref name="seconds"/> is quantised to
        /// the 1 s driver tick, so 1.4 and 1.0 are the same beat; this is the design's one real constraint and
        /// every cadence default is chosen around it.
        /// </summary>
        public void Schedule(double seconds, Action step)
        {
            if (step == null)
                return;

            if (!double.IsFinite(seconds) || seconds < 0.0)
                seconds = 0.0;

            State.Schedule(Now + TimeSpan.FromSeconds(seconds), step);
        }

        // ---- talking -------------------------------------------------------------------------------------

        /// <summary>
        /// One line to everyone at the digsite, through the system's single announcement route
        /// (MlDigsiteManager.Announce - radius-scoped, never server-wide, already exception-guarded).
        ///
        /// EVERY telegraph is also a chat line, not only a visual. A player whose client has not yet received
        /// a marker's create packet sees no glow; the line is what makes the mechanic fair anyway.
        /// </summary>
        public void Say(string line, ChatMessageType type = ChatMessageType.Broadcast)
            => MlDigsiteManager.Announce(Encounter, line, type);

        /// <summary>
        /// One line to ONE player - a warning that names how much margin they have left, which is information
        /// about them and would be noise to everyone else. Best-effort and never allowed to break a mechanic:
        /// a player who logged out between the decision and the send simply does not hear it.
        /// </summary>
        public void Tell(Player player, string line, ChatMessageType type = ChatMessageType.Broadcast)
        {
            if (player == null || string.IsNullOrEmpty(line))
                return;

            try
            {
                player.Session?.Network.EnqueueSend(new Network.GameMessages.Messages.GameMessageSystemChat(line, type));
            }
            catch (Exception ex)
            {
                log.Error($"[ML_DIGSITE] {Encounter} mechanic tell threw for {player.Name}", ex);
            }
        }

        /// <summary>
        /// A sound played from the boss, so it is positional. Queued on the boss's own landblock like every
        /// other write from the world thread, and best-effort: a missed sound costs a cue, never the mechanic.
        /// </summary>
        public void Sound(Sound sound)
        {
            var landblock = Boss?.CurrentLandblock;

            if (landblock == null)
                return;

            var target = Boss;

            landblock.EnqueueAction(new ActionEventDelegate(() =>
            {
                try
                {
                    if (!target.IsDestroyed)
                        target.EnqueueBroadcast(new Network.GameMessages.Messages.GameMessageSound(target.Guid, sound, 1.0f));
                }
                catch (Exception ex)
                {
                    log.Error($"[ML_DIGSITE] {Encounter} mechanic sound broadcast threw", ex);
                }
            }));
        }

        /// <summary>
        /// A visible motion played on the boss - the same queued-on-the-boss's-landblock, best-effort shape as
        /// <see cref="Sound"/> above. <paramref name="motion"/> == MotionCommand.Invalid (0) is the off switch,
        /// so a module can pass a raw tunable value straight through (MlDigsiteTunables' own "0 = no animation"
        /// convention).
        ///
        /// ALSO SKIPS when the boss's OWN MotionTable has no animation for this motion in its current stance
        /// (Physics.Animation.MotionTable.GetAnimationLength &lt;= 0) - read live, not cached, because the boss's
        /// stance can change between ticks. That is what keeps a mistuned motion tunable inert (no broadcast,
        /// no error) instead of a bad value ever reaching EnqueueBroadcastMotion.
        /// </summary>
        public void Motion(MotionCommand motion)
        {
            if (motion == MotionCommand.Invalid)
                return;

            var landblock = Boss?.CurrentLandblock;

            if (landblock == null)
                return;

            var target = Boss;
            var stance = target.CurrentMotionState?.Stance ?? MotionStance.NonCombat;

            if (Physics.Animation.MotionTable.GetAnimationLength(target.MotionTableId, stance, motion) <= 0f)
                return;

            landblock.EnqueueAction(new ActionEventDelegate(() =>
            {
                try
                {
                    if (!target.IsDestroyed)
                        target.EnqueueBroadcastMotion(new Motion(stance, motion));
                }
                catch (Exception ex)
                {
                    log.Error($"[ML_DIGSITE] {Encounter} mechanic motion broadcast threw", ex);
                }
            }));
        }

        // ---- real spell casts ----------------------------------------------------------------------------

        /// <summary>
        /// Launches <paramref name="spell"/>'s real projectiles from <paramref name="caster"/> at
        /// <paramref name="target"/> - the drum cadence's Ring/Wall resolve (round 18 owner ruling: RoZ
        /// playtest feedback that the shapes read as invisible damage). Queued on the CASTER's OWN
        /// landblock, not the boss's - an add can be ticking on a different landblock thread than the boss -
        /// the same queued-action shape as <see cref="Sound"/> and <see cref="Motion"/>, and best-effort: a
        /// caster destroyed or dead by the time the queued action runs is silently skipped, costing the cast,
        /// never the mechanic (which has already drawn its telegraph and sent its chat line regardless).
        ///
        /// DELIBERATELY GOES AROUND <see cref="Hit"/>/<see cref="RadialHit"/> (invariant 7's one stated
        /// exception, see this context's own remarks). A launched spell projectile is a real, physically
        /// simulated object that resolves its own damage through SpellProjectile.CalculateDamage /
        /// DamageTarget on collision - the same retail damage pipeline every player-facing war spell already
        /// uses - not a number this driver could compute and replicate. It also skips the normal cast windup
        /// (TryCastSpell's resist roll, mana check, cast animation): the drum's own beats ARE the windup.
        /// CreateSpellProjectiles is the same low-level entry class-ability procs use for exactly this reason
        /// (RunebladeAbility, SpellbladeAbility, SpellstormAbility all call it directly rather than
        /// TryCastSpell).
        /// </summary>
        public void CastSpellProjectiles(Creature caster, Spell spell, WorldObject target)
        {
            if (caster == null || spell == null || spell.NotFound)
                return;

            var landblock = caster.CurrentLandblock;

            if (landblock == null)
                return;

            var encounter = Encounter;

            landblock.EnqueueAction(new ActionEventDelegate(() =>
            {
                try
                {
                    if (caster.IsDestroyed || caster.IsDead)
                        return;

                    caster.CreateSpellProjectiles(spell, target, null);
                }
                catch (Exception ex)
                {
                    log.Error($"[ML_DIGSITE] {encounter} mechanic spell cast threw for 0x{caster.Guid.Full:X8}", ex);
                }
            }));
        }

        // ---- the audience --------------------------------------------------------------------------------

        /// <summary>
        /// Everyone the encounter counts as present: online, alive (staff included), same instance, inside
        /// ml_digsite_audience_radius_metres of the anchor.
        ///
        /// THIS WALKS THE ONLINE PLAYER LIST. Call it from a RESOLVE, never from a cadence check - the
        /// existing code already reserves that walk for the 15 s reap rather than the 1 s tick, and a module
        /// that called it every tick would be a defect.
        /// </summary>
        public List<Player> Participants()
            => MlDigsiteAudience.Participants(Anchor, MlDigsiteTunables.AudienceRadiusMetres);

        // ---- damage --------------------------------------------------------------------------------------

        /// <summary>
        /// Damages every participant within <paramref name="radius"/> metres of <paramref name="centre"/>.
        /// Returns how many were hit.
        ///
        /// NOT A HOTSPOT, and that is not a style choice. A Hotspot's damage radius comes from its Setup's
        /// CylSphere and is SCALE-INVARIANT (see the header of Content/sql/weenies/1002555 Weeping
        /// Blackglass.sql, written from PhysicsObj.is_touching / PartArray.GetCylSphere): the only data-side
        /// lever on it is a different model. A radius we want to retune live therefore has to be measured in
        /// C#, which is what this does - the same shape as Hotspot.Activate, without the object.
        /// </summary>
        public int RadialHit(Position centre, double radius, double damage, DamageType damageType, bool nonLethal = false)
        {
            if (centre == null || !(radius > 0.0) || !(damage > 0.0))
                return 0;

            var hit = 0;

            foreach (var player in Participants())
            {
                var loc = player?.Location;

                if (loc == null || loc.Instance != centre.Instance)
                    continue;

                if (loc.DistanceTo(centre) > radius)
                    continue;

                Hit(player, damage, damageType, nonLethal);
                hit++;
            }

            return hit;
        }

        /// <summary>
        /// Damages one player, ON THEIR OWN LANDBLOCK'S QUEUE. The driver runs on the world thread, and a
        /// vital write plus the packets that follow it belong to the landblock that owns the player.
        ///
        /// The resistance mod is applied the same way Hotspot.Activate applies it, with the boss as the
        /// source so the hit is attributed to the thing that threw it (and so the damage history, the
        /// retaliation and the death message all name the boss rather than nobody).
        ///
        /// <paramref name="nonLethal"/> caps the damage at one point short of the player's CURRENT health,
        /// read inside the queued action so it is the value at the moment of the write. That is the concrete
        /// form of "room for 2 mistakes": a guarantee by construction, not a tuning hope.
        /// </summary>
        public void Hit(Player player, double damage, DamageType damageType, bool nonLethal = false)
        {
            if (player == null || !(damage > 0.0))
                return;

            var source = Boss;
            var encounter = Encounter;

            player.EnqueueAction(new ActionEventDelegate(() =>
            {
                try
                {
                    if (player.IsDead || player.IsDestroyed || source == null || source.IsDestroyed)
                        return;

                    var resisted = damage * player.GetResistanceMod(damageType, source, null);

                    if (!(resisted > 0.0))
                        return;

                    var amount = (uint)Math.Round(resisted, MidpointRounding.AwayFromZero);

                    if (nonLethal)
                        amount = MlDigsiteBossMechanicRules.NonLethalDamage(amount, player.Health.Current);

                    if (amount == 0)
                        return;

                    player.TakeDamage(source, damageType, amount, BodyPart.Foot);
                }
                catch (Exception ex)
                {
                    log.Error($"[ML_DIGSITE] {encounter} mechanic damage threw for {player.Name}", ex);
                }
            }));
        }

        // ---- telegraphs ----------------------------------------------------------------------------------

        /// <summary>
        /// Puts a marker on the ground at each of <paramref name="points"/> and takes the PREVIOUS cycle's
        /// markers back out, so two cycles' lights never stand together. Nothing is scheduled to remove
        /// these: the next Telegraph call or <see cref="ClearMarkers"/> does it, and the encounter's own
        /// cleanup destroys anything left whatever ends the fight.
        ///
        /// <paramref name="safe"/> picks which marker weenie is used, so ground that is safe and ground that
        /// is about to be hit never look the same. A marker wcid of 0 places nothing, which is how an
        /// operator turns the visuals off without turning the mechanic off - the chat telegraph still goes.
        /// </summary>
        public void Telegraph(IReadOnlyList<Position> points, bool safe = false)
        {
            ClearMarkers();

            if (points == null || points.Count == 0)
                return;

            var wcid = safe ? MlDigsiteTunables.BossRushSafeMarkerWcid : MlDigsiteTunables.BossRushHazardMarkerWcid;

            if (wcid == 0)
                return;

            var state = State;

            for (var i = 0; i < points.Count; i++)
                MlDigsiteProps.TryPlace(Encounter, wcid, points[i], 0, wo => state.AddMarker(wo));
        }

        /// <summary>
        /// A short-lived marker burst at <paramref name="centre"/>: the visual half of a hit that has ALREADY
        /// landed (design section 5.1 step 6, the volatile detonation), cleared again by a step of its own
        /// <paramref name="seconds"/> later.
        ///
        /// DELIBERATELY NOT <see cref="Telegraph"/>. Telegraph markers are the driver's ONE shared set and
        /// every call to it takes the previous ones down, so a burst routed through it would erase a drum or
        /// safe-zone telegraph the other slot of the same set had just drawn - sets 2 and 4 both pair
        /// volatile with a mechanic that telegraphs. These markers are held in this call's own closure
        /// instead and never touch <see cref="State"/>'s marker list.
        ///
        /// Every marker is still an MlDigsiteProps prop, so it is stamped with the encounter id, takes
        /// TimeToRot = -1 and is adopted into the encounter's held list before it enters the world. If the
        /// encounter ends before the removal step runs, the step is dropped with the rest of the queue and
        /// the encounter's own cleanup destroys the markers - which is why nothing here has to survive a
        /// teardown.
        /// </summary>
        public void Burst(Position centre, double radius, int markers, double seconds)
        {
            var wcid = MlDigsiteTunables.BossRushHazardMarkerWcid;

            if (State == null || centre == null || wcid == 0 || markers < 1 || !(radius > 0.0))
                return;

            var points = WorldEvents.WorldEventGeometry.Ring(centre, (float)radius, markers, 0.0f, Rng);

            if (points.Count == 0)
                return;

            // The placements run on the anchor landblock's thread and this list is read back on the world
            // thread by the removal step, so it is locked rather than merely captured.
            var placed = new List<WorldObject>();

            for (var i = 0; i < points.Count; i++)
            {
                MlDigsiteProps.TryPlace(Encounter, wcid, points[i], 0, wo =>
                {
                    if (wo == null)
                        return;

                    lock (placed)
                        placed.Add(wo);
                });
            }

            Schedule(seconds, () =>
            {
                lock (placed)
                {
                    for (var i = 0; i < placed.Count; i++)
                        MlDigsiteProps.Remove(placed[i]);

                    placed.Clear();
                }
            });
        }

        /// <summary>Takes every marker this driver currently has standing back out of the world.</summary>
        public void ClearMarkers()
        {
            var standing = State.TakeMarkers();

            for (var i = 0; i < standing.Count; i++)
                MlDigsiteProps.Remove(standing[i]);
        }
    }
}
