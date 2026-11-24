using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;

using log4net;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.HazardMechanics;
using ACE.Server.WorldObjects;

using Spell = ACE.Server.Entity.Spell;

namespace ACE.Server.ClassAbilities
{
    /// <summary>
    /// One Echo Cast wisp (Archmage T3 rework, Docs/ClassAbilities/ARCHMAGE-ECHO-CAST-WISP-DESIGN.md, plan section 2):
    /// a plain controller, NOT a WorldObject subclass, holding a Generic body (wcids 1008600-1008604, one per school,
    /// <see cref="EchoWispMath.BodyWcidFor"/>). The body is ethereal, unattackable and not a Creature, so nothing can
    /// target or hit it by construction. It spawns about 1.5 m to the caster's side at the caster's height and never
    /// moves; for its life it recasts the one spell it carries as the caster's own first-hand cast
    /// (<see cref="Player.CastAsEchoWisp"/>), launched from the body.
    ///
    /// THREADS. The watch loop is a self-rescheduling ActionChain on the BODY (0.5 s tick), so it runs on the body's
    /// landblock. It only reads the clock and the owner's presence fields (the CombatPet.HuntersEchoEndReason shape).
    /// Every cast, and everything a cast decides with (target pick, the done set, the facing), runs on the OWNER's
    /// action queue (<see cref="CastOnOwnerThread"/>), which re-checks the end conditions before casting. That routing
    /// is REQUIRED, not a nicety: CastAsEchoWisp's latch check-then-set is not atomic, so it must only ever be armed on
    /// the owner's own thread (plan ruling on #2297). The body thread and the owner thread share only
    /// <see cref="ended"/> (Interlocked) and the immutable fields.
    ///
    /// NO RECURSION. A wisp cast goes through CastAsEchoWisp -> HandleCastSpell, never through DoCastSpell_Inner or a
    /// Spellsword proc, which are the only callers of Player.TryTriggerEchoWisp; and everything a wisp cast launches
    /// carries SpellProjectile.IsEchoWispCast to impact time.
    /// </summary>
    public sealed class EchoWisp
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public Player Owner { get; }

        public WorldObject Body { get; }

        public Spell Spell { get; }

        public EchoWispSpellKind Kind { get; }

        /// <summary>A Restore wisp's restored vital (Health for Heal, Stamina for Revitalize); Undef for every other kind.</summary>
        public DamageType RestoreVital { get; }

        /// <summary>A ring or wall cast with no target: the wisp still picks a target to face, then launches untargeted.</summary>
        public bool Targetless { get; }

        public uint LifeProjectileDamage { get; }

        public bool InheritsProcCasterSkill { get; }

        /// <summary>A Spellsword proc wisp's proc damage scale (1.0 otherwise), stamped on every cast's scope.</summary>
        public float ProcDamageScale { get; }

        /// <summary>A Spellsword proc wisp's Volatile Elements rend source (null otherwise).</summary>
        public DamageType? RendSourceDamageType { get; }

        /// <summary>The weapon a Spellsword proc wisp's proc launched from (null for a hand-cast wisp).</summary>
        public WorldObject ProcWeapon { get; }

        public double ExpiresAt { get; }

        private readonly double intervalMin;
        private readonly double intervalMax;

        // body thread only
        private double nextCastAt;

        // owner thread only
        private Creature currentTarget;
        private readonly HashSet<uint> done = new HashSet<uint>();

        private int ended;

        /// <summary>Why the wisp ended (null while it lives).</summary>
        public string EndedReason { get; private set; }

        public bool IsEnded => Volatile.Read(ref ended) != 0;

        internal EchoWisp(Player owner, WorldObject body, Spell spell, Creature startTarget, uint lifeProjectileDamage, bool inheritsProcCasterSkill,
            float procDamageScale, DamageType? rendSourceDamageType, WorldObject procWeapon, double now, double durationSeconds, double intervalMinSeconds, double intervalMaxSeconds, double firstIntervalUnit)
        {
            Owner = owner;
            Body = body;
            Spell = spell;
            Kind = spell != null ? EchoWispMath.KindOf(spell.MetaSpellType, spell.IsBeneficial, RestoreVitalOf(spell)) : EchoWispSpellKind.Damage;
            RestoreVital = Kind == EchoWispSpellKind.Restore ? RestoreVitalOf(spell) : DamageType.Undef;
            Targetless = startTarget == null;
            currentTarget = startTarget;
            LifeProjectileDamage = lifeProjectileDamage;
            InheritsProcCasterSkill = inheritsProcCasterSkill;
            ProcDamageScale = procDamageScale;
            RendSourceDamageType = rendSourceDamageType;
            ProcWeapon = procWeapon;

            (intervalMin, intervalMax) = EchoWispMath.ClampIntervals(intervalMinSeconds, intervalMaxSeconds);

            ExpiresAt = now + EchoWispMath.ClampDuration(durationSeconds);

            // the first cast is one interval after the spawn (design: 3-4 casts in a 9 s life)
            nextCastAt = now + EchoWispMath.Interval(intervalMin, intervalMax, firstIntervalUnit);
        }

        /// <summary>
        /// The boosted vital a Restore test reads (Spell.VitalDamageType, from the world database's spell row), read only for
        /// a beneficial Boost and Undef otherwise, or when the spell carries no database row (a dat-only Spell), so no other
        /// spell ever touches it.
        /// </summary>
        internal static DamageType RestoreVitalOf(Spell spell) =>
            spell != null && spell.MetaSpellType == SpellType.Boost && spell.IsBeneficial && spell._spell != null ? spell.VitalDamageType : DamageType.Undef;

        // ---- spawn -----------------------------------------------------------------------------------------

        /// <summary>
        /// The first of the caster's spawn points (<see cref="EchoWispMath.SpawnCandidates"/>: right, left, behind, in front,
        /// then the four diagonals) that is not taken by one of the caster's live wisps (<paramref name="occupied"/>, in the
        /// caster's frame; <see cref="EchoWispMath.IsSpotTaken"/>) and that <paramref name="resolve"/> places in a cell, as a
        /// world position with the caster's heading and instance; null when none does. Never the caster's own spot.
        /// </summary>
        internal static Position ChooseSpawnPosition(Vector3 casterOrigin, Quaternion casterRotation, uint instance, Func<Vector3, (uint Cell, Vector3 Point)?> resolve,
            IReadOnlyCollection<Vector3> occupied = null)
        {
            foreach (var candidate in EchoWispMath.SpawnCandidates(casterOrigin, casterRotation))
            {
                if (EchoWispMath.IsSpotTaken(candidate, occupied))
                    continue;

                var resolved = resolve(candidate);

                if (resolved.HasValue)
                    return new Position(resolved.Value.Cell, resolved.Value.Point, casterRotation) { Instance = instance };
            }

            return null;
        }

        /// <summary>
        /// Everything set on the body before EnterWorld (the hazard spawn pattern, HazardContext.Spawn): the location,
        /// the translucency through HuntersEchoMath.Translucency (clamped 0-0.95, so it can never be fully invisible;
        /// left unset below the wire threshold), TimeToRot = duration + 30 s (so an orphaned body rots on its own),
        /// IsTransientSpawn, and the caster's projectile cast height, so the wisp's bolts leave it at the height the
        /// caster's own would.
        /// </summary>
        internal static void PrepareBody(WorldObject body, Position at, double durationSeconds, double translucencyTunable, float castHeight)
        {
            body.Location = at;

            var translucency = HuntersEchoMath.Translucency(translucencyTunable);
            if (translucency >= 0.001f)
                body.Translucency = translucency;

            body.TimeToRot = EchoWispMath.TimeToRot(durationSeconds);
            body.IsTransientSpawn = true;
            body.ProjectileCastHeight = castHeight;
        }

        /// <summary>
        /// Spawns a wisp carrying <paramref name="spell"/> beside <paramref name="owner"/> and starts its watch loop.
        /// Runs on the owner's thread (a hand cast's commit or a Spellsword proc). Returns null, with nothing left in the
        /// world, when no body could be created or placed.
        /// </summary>
        internal static EchoWisp TrySpawn(Player owner, Spell spell, Creature target, uint lifeProjectileDamage, bool inheritsProcCasterSkill,
            float procDamageScale, DamageType? rendSourceDamageType, WorldObject procWeapon, double durationSeconds, double intervalMinSeconds, double intervalMaxSeconds, double translucencyTunable)
        {
            var landblock = owner?.CurrentLandblock;
            var physics = owner?.PhysicsObj;

            if (landblock == null || physics == null || owner.Location == null || EchoWispMath.ClampDuration(durationSeconds) <= 0.0)
                return null;

            var instance = owner.Location.Instance;

            var at = ChooseSpawnPosition(physics.Position.Frame.Origin, physics.Position.Frame.Orientation, instance,
                point => HazardGround.TryResolveCell(landblock, instance, point, false, out var cell, out var resolved) ? (cell, resolved) : ((uint, Vector3)?)null,
                owner.EchoWispSpots(physics.Position));

            if (at == null)
            {
                log.Debug($"[ECHO WISP] {owner.Name}: no cell beside the caster for a wisp");
                return null;
            }

            var body = WorldObjectFactory.CreateNewWorldObject(EchoWispMath.BodyWcidFor(spell.School));

            if (body == null)
            {
                log.Warn($"[ECHO WISP] {owner.Name}: wisp body {EchoWispMath.BodyWcidFor(spell.School)} is not in the world database");
                return null;
            }

            PrepareBody(body, at, durationSeconds, translucencyTunable, owner.CastHeight);

            bool entered;

            try
            {
                entered = body.EnterWorld();
            }
            catch (Exception ex)
            {
                log.Error($"[ECHO WISP] {owner.Name}: EnterWorld threw for wisp body {body.WeenieClassId}", ex);
                entered = false;
            }

            if (!entered)
            {
                if (!body.IsDestroyed)
                    body.Destroy();

                log.Debug($"[ECHO WISP] {owner.Name}: wisp body did not enter the world at {at.ToLOCString()}");
                return null;
            }

            var wisp = new EchoWisp(owner, body, spell, target, lifeProjectileDamage, inheritsProcCasterSkill, procDamageScale, rendSourceDamageType, procWeapon,
                Timers.RunningTime, durationSeconds, intervalMinSeconds, intervalMaxSeconds, ThreadSafeRandomUnit());

            owner.AddEchoWisp(wisp);

            wisp.ScheduleWatch();

            return wisp;
        }

        private static double ThreadSafeRandomUnit() => ACE.Common.ThreadSafeRandom.Next(0.0f, 1.0f);

        // ---- watch loop (body thread) ------------------------------------------------------------------------

        private void ScheduleWatch()
        {
            var chain = new ActionChain();
            chain.AddDelaySeconds(EchoWispMath.WatchTickSeconds);
            chain.AddAction(Body, Watch);
            chain.EnqueueChain();
        }

        /// <summary>
        /// One 0.5 s watch tick on the body's thread: end the wisp when its time or its caster says so, else hand a due
        /// cast to the owner's queue and reschedule.
        /// </summary>
        private void Watch()
        {
            if (IsEnded)
                return;

            // a body destroyed under the wisp (rot, a landblock unload) must still end it, or its cap slot leaks
            if (Body.IsDestroyed)
            {
                End("bodylost", onBodyThread: true);
                return;
            }

            var reason = CurrentEndReason();

            if (reason != null)
            {
                End(reason, onBodyThread: true);
                return;
            }

            var now = Timers.RunningTime;

            if (now >= nextCastAt)
            {
                nextCastAt = now + EchoWispMath.Interval(intervalMin, intervalMax, ThreadSafeRandomUnit());
                Owner.EnqueueAction(new ActionEventDelegate(CastOnOwnerThread));
            }

            ScheduleWatch();
        }

        /// <summary>The end reason right now (<see cref="EchoWispMath.EndReason"/>) from the live owner and body.</summary>
        internal string CurrentEndReason()
        {
            var owner = Owner;
            var body = Body;

            var bodyPresent = body != null && !body.IsDestroyed && body.Location != null && body.CurrentLandblock != null;
            var ownerPresent = owner != null && !owner.IsDestroyed && owner.PhysicsObj != null && owner.Location != null && owner.CurrentLandblock != null;

            var sameLandblock = bodyPresent && ownerPresent && ReferenceEquals(owner.CurrentLandblock, body.CurrentLandblock);
            var sameInstance = sameLandblock && owner.Location.Instance == body.Location.Instance;
            var distance = sameInstance ? owner.Location.DistanceTo(body.Location) : float.NaN;

            return EchoWispMath.EndReason(Timers.RunningTime, ExpiresAt, bodyPresent, ownerPresent,
                ownerPresent && owner.IsDead, ownerPresent && owner.Teleporting, ownerPresent && owner.ClassAbilitySuppressed, sameLandblock, sameInstance, distance);
        }

        // ---- one cast (owner thread) -------------------------------------------------------------------------

        /// <summary>
        /// One wisp cast, on the OWNER's action queue: re-check the end conditions (the owner may have died, portaled or
        /// logged out since the body's tick queued this), pick the target (keep the current one while it is still legal,
        /// else the nearest legal one in range and sight of the wisp), face it, and cast. With no target the wisp idles
        /// and the next tick tries again.
        ///
        /// DIAGNOSTICS. Every call that gets past the end re-check logs exactly ONE DEBUG line (an end logs its own, in
        /// End), so a wisp logs at most once per cast: "idle" with the pick tally (<see cref="EchoWispPickTally"/>:
        /// the held target's fate, candidates seen, turned away by reason, out of range, sight failures), or "cast at"
        /// with the outcome, the refusal reason and the projectiles launched and blocked. One grep of "[ECHO WISP]" in a
        /// stage log then says why a wisp did or did not cast.
        /// </summary>
        private void CastOnOwnerThread()
        {
            if (IsEnded)
                return;

            var reason = CurrentEndReason();

            if (reason != null)
            {
                End(reason, onBodyThread: false);
                return;
            }

            var debug = log.IsDebugEnabled;

            var tally = debug && Kind != EchoWispSpellKind.Restore ? new EchoWispPickTally() : null;

            string restoreIdle = null;

            var target = Kind == EchoWispSpellKind.Restore ? PickRestoreTarget(out restoreIdle) : PickTarget(tally);

            if (target == null)
            {
                currentTarget = null;

                if (debug)
                    log.Debug($"[ECHO WISP] {Owner.Name}'s wisp ({Spell.Name}) idle, no target: {(tally != null ? tally.Describe() : restoreIdle)}");

                return;
            }

            currentTarget = target;

            Face(target);

            var scope = new EchoWispCastScope(Body, LifeProjectileDamage, InheritsProcCasterSkill, ProcDamageScale, RendSourceDamageType, ProcWeapon);

            var outcome = Owner.CastAsEchoWisp(scope, Spell, Targetless ? null : target);

            if (EchoWispMath.IsDoneAfter(Kind, outcome))
                done.Add(target.Guid.Full);

            if (debug)
                log.Debug($"[ECHO WISP] {Owner.Name}'s wisp ({Spell.Name}) cast at {target.Name} 0x{target.Guid.Full:X8}: {outcome}"
                    + (scope.RefusedBecause != null ? $" ({scope.RefusedBecause})" : "")
                    + $", projectiles {scope.ProjectilesLaunched} launched {scope.ProjectilesBlocked} blocked; "
                    + (tally != null ? tally.Describe() : $"restore {RestoreVital} at {VitalFractionOf(target, RestoreVital):0%} before the cast"));
        }

        /// <summary>
        /// A Restore wisp's target for this cast (<see cref="EchoWispMath.SelectRestoreTarget"/>), or null with
        /// <paramref name="idleReason"/> set. A Self spell only ever lands on the caster, so its one candidate is the caster
        /// and nothing limits its reach; it idles while the caster is full. An Other spell starts on its original target
        /// and, once that one is full, takes the most hurt of the caster and the fellows the caster can see, within the
        /// spell's range of the wisp and in its line of sight. Monsters, pets and players outside the fellowship are never
        /// candidates (<see cref="RestoreVerdict"/>).
        /// </summary>
        private Creature PickRestoreTarget(out string idleReason)
        {
            var owner = Owner;
            var body = Body;
            var vital = RestoreVital;

            if (Spell.IsSelfTargeted)
            {
                var self = EchoWispMath.SelectRestoreTarget<Creature>(owner, null, c => RestoreVerdict(c, owner, done) == EchoWispIneligible.None,
                    c => VitalFractionOf(c, vital), c => true, out idleReason);

                if (self == null && idleReason == EchoWispMath.IdleNobodyHurt)
                    idleReason = IdleCasterAtFull;

                return self;
            }

            var skill = owner.GetCreatureSkill(Spell.School);
            var range = EchoWispMath.CastRange(Player.MaxSpellRange(Spell.BaseRangeConstant, Spell.BaseRangeMod, skill.InitLevel + skill.Ranks));

            return EchoWispMath.SelectRestoreTarget(currentTarget, RestoreCandidates(owner),
                c => RestoreVerdict(c, owner, done) == EchoWispIneligible.None,
                c => VitalFractionOf(c, vital),
                c =>
                {
                    var distance = c.Location == null || body.Location == null ? float.NaN : body.Location.Distance2D(c.Location);
                    return !float.IsNaN(distance) && distance <= range && body.IsDirectVisible(c);
                },
                out idleReason);
        }

        /// <summary>Why a Self Restore wisp idles: the caster is not missing any of the vital.</summary>
        internal const string IdleCasterAtFull = "nobody hurt (caster at full)";

        /// <summary>The caster first, then every player the caster can see (the legality test keeps only the fellows).</summary>
        private static List<Creature> RestoreCandidates(Player owner)
        {
            var candidates = new List<Creature> { owner };

            foreach (var obj in owner.PhysicsObj.ObjMaint.GetVisibleObjectsValuesWhere(o => o.WeenieObj?.WorldObject is Player))
            {
                if (obj.WeenieObj?.WorldObject is Player player && !ReferenceEquals(player, owner))
                    candidates.Add(player);
            }

            return candidates;
        }

        /// <summary>
        /// Why <paramref name="candidate"/> is not a legal Restore target for <paramref name="owner"/>'s wisp, or None
        /// (<see cref="EchoWispMath.RestoreIneligible"/>): players only, and only the caster or a fellow
        /// (<see cref="EchoWispEngagement.IsCasterOrFellow"/>), and never a fellow in a PvP state
        /// (<see cref="EchoWispEngagement.IsPvpSafeRestoreTarget"/>, the cast guard's own predicate), tested before any vital
        /// is read; then alive, visible, not portaling, and not done.
        /// </summary>
        internal static EchoWispIneligible RestoreVerdict(Creature candidate, Player owner, ICollection<uint> done)
        {
            if (candidate is not Player player)
                return EchoWispIneligible.NotPlayer;

            if (!EchoWispEngagement.IsCasterOrFellow(player, owner))
                return EchoWispIneligible.NotCasterOrFellow;

            if (!EchoWispEngagement.IsPvpSafeRestoreTarget(player, owner))
                return EchoWispIneligible.NotPvpSafe;

            return EchoWispMath.RestoreIneligible(true, true, true, player.IsDead, player.Visibility, player.Teleporting, done != null && done.Contains(player.Guid.Full));
        }

        /// <summary>current / max of <paramref name="vital"/> on <paramref name="creature"/> (<see cref="EchoWispMath.VitalFraction"/>): Health or Stamina; full for anything else.</summary>
        internal static float VitalFractionOf(Creature creature, DamageType vital)
        {
            switch (vital)
            {
                case DamageType.Health:
                    return EchoWispMath.VitalFraction(creature.Health.Current, creature.Health.MaxValue);
                case DamageType.Stamina:
                    return EchoWispMath.VitalFraction(creature.Stamina.Current, creature.Stamina.MaxValue);
                default:
                    return 1.0f;
            }
        }

        /// <summary>
        /// The wisp's target for this cast (<see cref="HuntersEchoMath.SelectTarget"/> over the owner's visible non-player
        /// creatures, range and sight measured from the wisp body), or null. <paramref name="tally"/>, when given, records
        /// what the pick saw through the same delegates the pick runs, so it counts exactly what was decided.
        /// </summary>
        private Creature PickTarget(EchoWispPickTally tally)
        {
            var owner = Owner;
            var body = Body;

            // read once: the owner's /summonaggro is live and takes the biota lock
            var summonAggro = owner.SummonAggro;

            var skill = owner.GetCreatureSkill(Spell.School);
            var range = EchoWispMath.CastRange(Player.MaxSpellRange(Spell.BaseRangeConstant, Spell.BaseRangeMod, skill.InitLevel + skill.Ranks));

            var current = currentTarget;

            var candidates = owner.PhysicsObj.ObjMaint.GetVisibleObjectsValuesOfTypeNonPlayerCreature();

            if (tally != null)
            {
                tally.Range = range;
                tally.Candidates = candidates.Count;
                tally.HadHeld = current != null;

                // the held target may be gone from the owner's view and still be judged first
                if (current != null && !candidates.Contains(current))
                    tally.Candidates++;
            }

            return HuntersEchoMath.SelectTarget(current, candidates,
                creature =>
                {
                    var held = ReferenceEquals(creature, current);
                    var verdict = LegalTargetVerdict(creature, summonAggro, isStartingOrHeldTarget: held);

                    if (tally != null)
                    {
                        if (held)
                            tally.HeldIneligible = verdict;
                        else
                            tally.RecordIneligible(verdict);
                    }

                    return verdict == EchoWispIneligible.None;
                },
                creature =>
                {
                    var distance = creature.Location == null || body.Location == null ? float.NaN : body.Location.Distance2D(creature.Location);

                    if (tally != null)
                    {
                        var inRange = !float.IsNaN(distance) && distance <= range;

                        if (ReferenceEquals(creature, current))
                            tally.HeldOutOfRange = !inRange;
                        else if (inRange)
                            tally.InRange++;
                        else
                            tally.OutOfRange++;
                    }

                    return distance;
                },
                range,
                creature =>
                {
                    var visible = body.IsDirectVisible(creature);

                    if (tally != null)
                    {
                        if (ReferenceEquals(creature, current))
                            tally.HeldSightFailed = !visible;
                        else
                        {
                            tally.SightTested++;
                            if (!visible)
                                tally.SightFailed++;
                        }
                    }

                    return visible;
                },
                HuntersEchoMath.MaxSightTestsPerPick,
                null);
        }

        /// <summary>
        /// Why <paramref name="creature"/> is not a legal wisp target, or None when it is (<see cref="EchoWispMath.IneligibleReason"/>,
        /// the reason form of EchoWispMath.IsEligible). The cheap field checks run first; the engagement,
        /// faction and enchantment reads run only for a creature that passes them, and only when the answer can matter.
        /// The target the wisp already holds - first the one the original cast was aimed at (design rule 1) - is not held
        /// to the engagement test, so a damage wisp stays on it until it dies even when the caster opened on an idle
        /// monster. A debuff is judged on the object the cast would land on (WorldObject.ItemEnchantmentLandingItem for
        /// an Item Enchantment spell: the shield or weapon it redirects to); with nothing to land on, the creature is not
        /// a target at all.
        /// </summary>
        private EchoWispIneligible LegalTargetVerdict(Creature creature, bool summonAggro, bool isStartingOrHeldTarget)
        {
            if (creature == null)
                return EchoWispIneligible.Dead;

            var isPlayer = creature is Player;
            var isPet = creature is Pet;
            var isDead = creature.IsDead;
            var attackable = creature.Attackable;
            var hidden = creature.Visibility;
            var isDone = done.Contains(creature.Guid.Full);

            if (!EchoWispMath.PassesCheapChecks(isPlayer, isPet, isDead, attackable, hidden, isDone))
                return EchoWispMath.IneligibleReason(Kind, isPlayer, isPet, isDead, attackable, hidden, false, true, summonAggro, false, isDone);

            if (creature.Teleporting)
                return EchoWispIneligible.Teleporting;

            var sameFactionNotRetaliating = Owner.SameFaction(creature) && !(creature.PhysicsObj != null && creature.HasRetaliateTarget(Owner));

            if (sameFactionNotRetaliating)
                return EchoWispIneligible.SameFaction;

            // the engagement read is skipped whenever its answer cannot change the result
            var engaged = isStartingOrHeldTarget || summonAggro || EchoWispEngagement.IsEngaged(creature, Owner);

            var carries = false;

            if (Kind == EchoWispSpellKind.Debuff)
            {
                var holder = DebuffHolder(creature);

                if (holder == null)
                    return EchoWispIneligible.NoDebuffHolder;

                carries = EchoWispMath.CarriesDebuff(holder.EnchantmentManager.GetEnchantments(Spell.Category).Select(e => e.PowerLevel), Spell.Power);
            }

            return EchoWispMath.IneligibleReason(Kind, isPlayer, isPet, isDead, attackable, hidden, sameFactionNotRetaliating, engaged, summonAggro, carries, isDone);
        }

        /// <summary>The object this wisp's debuff lands on when cast at <paramref name="creature"/> (null: nothing to land on).</summary>
        private WorldObject DebuffHolder(Creature creature) =>
            Spell.School == MagicSchool.ItemEnchantment ? WorldObject.ItemEnchantmentLandingItem(Spell, creature) : creature;

        /// <summary>
        /// Turns the body to face <paramref name="target"/>, server-side only (no broadcast: a glow has no visible
        /// facing), so a targetless ring or wall launches toward it (LaunchSpellProjectiles takes an untargeted launch's
        /// heading from the geometry source's physics frame).
        /// </summary>
        private void Face(WorldObject target)
        {
            var body = Body;

            if (body.PhysicsObj == null || target?.PhysicsObj == null)
                return;

            // GetOffset is the body-to-target vector across a landblock seam as well
            var facing = EchoWispMath.FacingToward(Vector3.Zero, body.PhysicsObj.Position.GetOffset(target.PhysicsObj.Position));

            if (facing == null)
                return;

            body.Location.Rotation = facing.Value;
            body.PhysicsObj.Position.Frame.Orientation = facing.Value;
            body.PhysicsObj.set_frame(body.PhysicsObj.Position.Frame);
        }

        // ---- end ---------------------------------------------------------------------------------------------

        /// <summary>
        /// Ends the wisp once (idempotent across threads): unlinks it from its owner and destroys the body, directly when
        /// on the body's own thread (or when the body is in no landblock, so no tick can race it), else on the body's
        /// action queue (the CombatPet.QueueEndHuntersEcho shape).
        /// </summary>
        internal void End(string reason, bool onBodyThread)
        {
            if (Interlocked.Exchange(ref ended, 1) != 0)
                return;

            EndedReason = reason;

            Owner?.RemoveEchoWisp(this);

            var body = Body;

            log.Debug($"[ECHO WISP] {Owner?.Name}'s wisp ({Spell?.Name}) ended: {reason}");

            if (body == null || body.IsDestroyed)
                return;

            if (onBodyThread || body.CurrentLandblock == null)
                body.Destroy();
            else
                body.EnqueueAction(new ActionEventDelegate(() =>
                {
                    if (!body.IsDestroyed)
                        body.Destroy();
                }));
        }
    }
}
