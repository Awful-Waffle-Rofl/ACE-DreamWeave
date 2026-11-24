using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Pvp.Rules;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// Custom Summons Task 6: the caster pet's tick. Monster_Tick hands a caster pet (IsCasterPet) to TickCaster
    /// right after target acquisition, and returns: a caster never reaches the melee/missile attack branch, so it
    /// never melees and never takes GetNextAttackType's melee fallback.
    ///
    /// The decision is PetCasterBrain.Decide, pure. This file is the world-touching half: BuildCasterContext
    /// reads the world into a CasterContext, and TickCaster carries the decision out.
    ///
    /// THREADING. This runs on the pet's landblock tick, on its group's thread. Every read of another object's
    /// state below is guarded:
    ///   - the OWNER (vitals, enchantment registry, pet slots, position) only after CanFollowOwner, which
    ///     requires the owner in the world, not teleporting, in the pet's instance and in the pet's landblock
    ///     group (PetFollowRules.SharesTickThread). Any failure is Follow, and nothing of the owner is read;
    ///   - a SIBLING pet (vitals, position) only when it shares the pet's tick thread the same way;
    ///   - an ENEMY's enchantment registry (BuildVulnerabilityProfile) only for enemies in the pet's own
    ///     landblock group - the guard TryRollSpell and CastSpell carry - and enemies outside it are not listed;
    ///   - an enemy's AttackTarget through PetEngagement.IsEngagedWithOwner, a single reference read (see there).
    /// </summary>
    partial class CombatPet
    {
        /// <summary>
        /// TRUE for a pet that runs the caster brain: its weenie carries PropertyBool.SummonCasterBrain, or it was
        /// summoned with a bound spell (ApplySummonStats). Cached; never changes while the pet is out.
        /// </summary>
        public bool IsCasterPet { get; private set; }

        /// <summary>This pet's spells as the brain sees them, built on the first caster tick (BuildCasterSpells).</summary>
        private List<CasterSpell> casterSpells;

        // reused every tick, so a caster tick allocates nothing in the steady state
        private readonly List<CasterAlly> casterSiblings = new List<CasterAlly>();
        private readonly List<Pet> casterSiblingPets = new List<Pet>();
        private readonly List<Creature> casterEnemies = new List<Creature>();
        private readonly List<float> casterEnemyDistances = new List<float>();
        private readonly CasterContext casterContext = new CasterContext();

        private Func<IReadOnlyDictionary<SpellCategory, CarriedEnchantment>> casterOwnerLayers;
        private Func<IReadOnlyDictionary<SpellCategory, CarriedEnchantment>> casterPetLayers;

        /// <summary>TRUE when a bound spell is a strip, so an enemy snapshot also resolves each layer's alignment and school.</summary>
        private bool casterHasStrip;
        private Func<int, VulnerabilityProfile> casterEnemyProfile;
        private Func<int, bool> casterEnemyHasLos;

        /// <summary>The last decision TickCaster acted on. Diagnostics and tests only.</summary>
        public CasterDecision LastCasterDecision { get; private set; } = CasterDecision.Follow;

        /// <summary>
        /// The enemy the last decision cast at or chased, or null. HandleFindTarget reads it (CasterHoldsTarget) so
        /// the owner-assist pick does not pull a caster off an enemy its brain just chose over the assist target -
        /// without it the two swap the target back and forth on alternate ticks.
        /// </summary>
        private WorldObject lastCasterEnemy;

        /// <summary>The ally a MoveInRange toward a sibling pet is running at, while that MoveTo is in flight.</summary>
        private WorldObject casterMoveTarget;

        /// <summary>
        /// One caster tick. Builds the context, decides, acts:
        ///   - Follow: drop any pending attack and run the idle follow (CombatPet_Follow);
        ///   - Cast on an enemy, the owner or a sibling: the explicit-target cast (Creature.TryStartExplicitCast),
        ///     once the magic delay has passed;
        ///   - MoveInRange of an enemy: the stock monster chase on AttackTarget (StartTurn, Movement) with the
        ///     brain's range written in;
        ///   - MoveInRange of the owner: the idle follow, which runs the pet back to them; of a sibling: a MoveTo.
        /// </summary>
        public void TickCaster()
        {
            var decision = PetCasterBrain.Decide(BuildCasterContext());

            LastCasterDecision = decision;

            lastCasterEnemy = decision.Target == CasterTargetKind.Enemy ? casterEnemies[decision.TargetIndex] : null;

            if (decision.Target != CasterTargetKind.Sibling || decision.Action != CasterAction.MoveInRange)
                casterMoveTarget = null;

            if (decision.Action == CasterAction.Follow)
            {
                ClearCasterAttack();
                TickFollowOwner();
                return;
            }

            if (decision.Target == CasterTargetKind.Enemy)
            {
                TickCasterHarmful(decision);
                return;
            }

            if (decision.Action == CasterAction.MoveInRange)
            {
                ClearCasterAttack();

                // the owner: the idle follow runs the pet back (with its teleport triggers). A sibling: a plain
                // MoveTo toward it (ruling 2026-11-02: a sibling out of heal range is closed on, not skipped).
                if (decision.Target == CasterTargetKind.Owner)
                    TickFollowOwner();
                else
                    TickMoveToAlly(casterSiblingPets[decision.TargetIndex]);

                return;
            }

            var target = decision.Target == CasterTargetKind.Self ? this : decision.Target == CasterTargetKind.Owner ? (WorldObject)P_PetOwner : casterSiblingPets[decision.TargetIndex];

            ClearCasterAttack();

            var spell = SpellFor(decision.SpellId);

            if (PrepareExplicitCast(spell, target))
                TryStartExplicitCast(spell, target, true);
        }

        /// <summary>
        /// TRUE when the pet may start <paramref name="spell"/> at <paramref name="target"/> now: the magic delay has
        /// passed, nothing is animating, and the pet can pay the mana (the target-exact cost TryStartExplicitCast
        /// charges). Only then is any follow or move in flight ended, so the cast happens where the pet stands -
        /// a pet that cannot cast keeps following. While not ready, a follow already in flight keeps advancing
        /// (its physics only moves through TickFollowOwner's UpdatePosition).
        /// </summary>
        private bool PrepareExplicitCast(Spell spell, WorldObject target)
        {
            if (!CasterCastReady() || !CanPayCastMana(spell, target))
            {
                if (isFollowingOwner)
                    TickFollowOwner();

                return false;
            }

            StopFollowingOwner();

            if (IsMoving || IsTurning)
                CancelPhysicsMoveTo();

            return true;
        }

        /// <summary>
        /// A MoveTo toward a sibling pet the brain wants to heal from out of range. Advances a MoveTo already
        /// heading for that ally, otherwise cancels whatever is in flight and issues one (IssueFollowMove, the
        /// follow's own MoveTo, which stops at FollowArriveDistance). The sibling is on this pet's tick thread
        /// (CollectSiblings), so the move never carries the pet into another group's landblock by itself.
        /// </summary>
        private void TickMoveToAlly(WorldObject ally)
        {
            StopFollowingOwner();

            if (IsMoving && ReferenceEquals(casterMoveTarget, ally))
            {
                UpdatePosition();
                return;
            }

            if (IsMoving || IsTurning)
                CancelPhysicsMoveTo();

            casterMoveTarget = IssueFollowMove(ally) ? ally : null;
        }

        /// <summary>
        /// Whether the owner-assist pick should leave <paramref name="current"/> alone (CombatPet.HandleFindTarget ->
        /// PetAssistTargeting.Decide's casterHoldsCurrent): this is a caster, its last decision cast at or chased
        /// exactly this enemy, and that enemy is still a valid, engaged target (<paramref name="currentValid"/>).
        /// </summary>
        internal bool CasterHoldsTarget(WorldObject current, bool currentValid) =>
            IsCasterPet && currentValid && current != null && ReferenceEquals(lastCasterEnemy, current);

        /// <summary>For tests: the enemy the last caster decision named.</summary>
        internal void SetLastCasterEnemyForTest(WorldObject enemy) => lastCasterEnemy = enemy;

        /// <summary>
        /// A harmful cast (or the approach for one) on enemy <paramref name="decision"/>.TargetIndex. The brain has
        /// already chosen the spell, so neither GetNextAttackType nor GetMaxRange ever runs for a caster - which
        /// is what keeps it off their melee fallback.
        /// </summary>
        private void TickCasterHarmful(CasterDecision decision)
        {
            var enemy = casterEnemies[decision.TargetIndex];

            if (decision.Action == CasterAction.Cast)
            {
                // in range: any chase toward it ends here (the chase MoveTo is Sticky and would otherwise carry
                // the pet into melee range), and the cast goes out on the enemy the brain chose - which need not
                // be AttackTarget, so no target switch (and no tug-of-war with the assist pick) is needed for it
                ClearCasterAttack();

                var spell = SpellFor(decision.SpellId);

                if (!PrepareExplicitCast(spell, enemy))
                    return;

                if (TryStartExplicitCast(spell, enemy, false) && ReferenceEquals(enemy, AttackTarget))
                    NotifyAttackOnTarget();

                return;
            }

            // out of range: chase it. The chase keys on AttackTarget, so the pet takes this enemy as its target.
            if (!ReferenceEquals(AttackTarget, enemy))
                SwitchAttackTarget(enemy);

            StopFollowingOwner();

            // the stock chase (StartTurn issues the MoveTo, Movement advances it). CurrentAttack Magic plus the
            // range override make StartTurn/GetMovementParameters measure against the brain's range (a ring's
            // 4.5 m reach, or UnboundedHarmfulReach for a spell with no range data), so the pet closes in.
            var range = RangeOf(decision.SpellId);

            CurrentAttack = CombatType.Magic;
            MaxRange = range;
            casterSpellRange = range;

            if (PhysicsObj.IsSticky)
                UpdatePosition(false);

            if (!IsTurning && !IsMoving)
                StartTurn();
            else
                Movement();
        }

        private static Spell SpellFor(int spellId) => new Spell(spellId);

        private float RangeOf(int spellId)
        {
            foreach (var spell in casterSpells)
            {
                if (spell.SpellId == spellId)
                    return spell.Range;
            }

            return float.PositiveInfinity;
        }

        /// <summary>
        /// The range the brain resolved for the spell this caster is approaching with (PetCasterBrain.ResolveRange),
        /// which GetSpellMaxRange reports instead of the bare formula while it is set. It differs from the formula
        /// only for a harmful spell with no range data at all (unbounded, so StartTurn would only ever turn in
        /// place) - it is what makes the pet close to UnboundedHarmfulReach before it fires. Rings are not that
        /// case: their formula range is already their reach (see UnboundedHarmfulReach).
        /// </summary>
        private float? casterSpellRange;

        internal override float? CasterSpellRangeOverride => casterSpellRange;

        /// <summary>Leaves the harmful-cast state so a stale spell or range never leaks into a later decision.</summary>
        private void ClearCasterAttack()
        {
            casterSpellRange = null;

            // a harmful approach was in progress: cancel its MoveTo/TurnTo and release the stick as well, the
            // same clean break a target switch makes (AbandonCurrentApproach)
            if (CurrentAttack != null)
                AbandonCurrentApproach();
        }

        /// <summary>
        /// The same gate MagicAttack's cadence sets: the magic delay has passed and no animation is playing. The
        /// magic delay here is <see cref="CasterMagicDelay"/>, which a cast-speed effect (Mana Rush, ramp
        /// axis=castspeed on=cast) shortens; an ordinary monster's NextMagicAttackTime does not.
        ///
        /// The physics step before the IsAnimating read is load-bearing, exactly as in AttackReady and MoveReady.
        /// Nothing else steps an animating awake monster's physics: the landblock skips it
        /// (WorldObject.ShouldRunCreaturePhysics), and a caster that is not following never calls UpdatePosition.
        /// Cutting a follow MoveTo (PrepareExplicitCast's StopFollowingOwner) or arriving at the owner leaves stop
        /// motions pending, so IsAnimating stays true until physics is stepped. A gate that only READ it stalled
        /// forever: the pet cast its first spell, then neither cast nor moved again while an enemy stayed in range.
        /// </summary>
        private bool CasterCastReady()
        {
            var now = Timers.RunningTime;

            var cooledDown = now >= PrevAttackTime + CasterMagicDelay && now >= NextAttackTime;

            if (!cooledDown)
                return false;

            PhysicsObj.update_object(Location.Instance);
            UpdatePosition_SyncLocation();

            return !PhysicsObj.IsAnimating;
        }

        /// <summary>
        /// The caster pet's magic-delay gate in seconds: AiUseMagicDelay (3 s unset, Weakened Blood slow
        /// included) divided by the cast-axis monster-effect multiplier (capped by monster_effect_speed_cap), so
        /// Mana Rush speeds the whole cast cycle and not only the wind-up and recovery animations. With no
        /// effects it is exactly NextMagicAttackTime - PrevAttackTime.
        /// </summary>
        internal double CasterMagicDelay => ScaleMonsterEffectCastDelayForPet();

        /// <summary>
        /// Reads the world into the reused CasterContext. See the class remarks for the guard on every read.
        /// </summary>
        internal CasterContext BuildCasterContext()
        {
            var ctx = casterContext;

            casterSiblings.Clear();
            casterSiblingPets.Clear();
            casterEnemies.Clear();
            casterEnemyDistances.Clear();

            ctx.Siblings = casterSiblings;
            ctx.EnemyDistances = casterEnemyDistances;

            // the pet's own mana, so the brain never chooses a spell it cannot pay for (it would choose it again on
            // every tick and never act). A pet that does not spend mana is never limited.
            ctx.PetMana = UsesManaToCast ? Mana.Current : uint.MaxValue;

            var owner = P_PetOwner;

            // owner unavailable: not one field of the owner is read past this point. The thread check
            // (CanFollowOwner, which also covers a null owner) runs BEFORE IsDead, so the owner's vitals are never
            // read across landblock groups.
            if (!CanFollowOwner(owner) || owner.IsDead)
            {
                ctx.OwnerAvailable = false;
                ctx.Spells = Array.Empty<CasterSpell>();
                return ctx;
            }

            ctx.OwnerAvailable = true;

            casterSpells ??= BuildCasterSpells();
            ctx.Spells = casterSpells;

            ctx.Owner = new CasterAlly(VitalsOf(owner), GetCylinderDistance(owner));

            CollectSiblings(owner.CurrentActivePet);
            CollectSiblings(owner.SecondaryActivePet);

            casterOwnerLayers ??= ReadOwnerTopLayers;
            casterPetLayers ??= ReadPetTopLayers;
            ctx.PetTopLayers = casterPetLayers;
            ctx.OwnerTopLayers = casterOwnerLayers;

            CollectCasterEnemies(PhysicsObj.ObjMaint.GetVisibleTargetsValuesOfTypeCreature(), GetCylinderDistance, casterEnemies, casterEnemyDistances);

            casterEnemyProfile ??= i => BuildVulnerabilityProfile(casterEnemies[i], casterHasStrip);
            ctx.EnemyProfile = casterEnemyProfile;

            // the projectile line-of-sight test Monster_Combat.GetMaxRange makes (IsDirectVisible), asked lazily
            casterEnemyHasLos ??= i => IsDirectVisible(casterEnemies[i]);
            ctx.EnemyHasLos = casterEnemyHasLos;

            return ctx;
        }

        /// <summary>
        /// FALSE when <paramref name="spell"/> is a Heal-kind spell (PetCasterBrain.ClassifySpell) whose target no
        /// longer needs it at release: the vital is back at or above PetCasterBrain.HealThreshold, or the target
        /// can no longer be read safely (the owner or sibling left this pet's tick thread). Likewise a cleanse
        /// (Cleanse or CleanseSelf) whose target no longer carries a harmful layer it can remove
        /// (PetCasterBrain.CleanseStillNeeded). Every other spell is TRUE - buffs and harmful spells keep their own rules. Monster_Magic.TryStartExplicitCast asks this when
        /// the pre-cast ends, before CastSpellAt.
        /// </summary>
        internal bool FriendlyCastStillWanted(Spell spell, WorldObject target)
        {
            // classified from the Spell in hand, the same rule CasterSpellFactsCache memoizes
            var kind = PetCasterBrain.ClassifySpell(spell.MetaSpellType, spell.IsBeneficial, spell.IsSelfTargeted, spell.Boost, spell.VitalDamageType, spell.Align, out var vital);

            // a cleanse is wanted only while its target still carries something it can remove; the target's snapshot is
            // read behind the same guard the decision used (the owner: CanFollowOwner; the pet itself: its own thread)
            if (kind == CasterSpellKind.Cleanse || kind == CasterSpellKind.CleanseSelf)
            {
                var forOwner = kind == CasterSpellKind.Cleanse;

                if (forOwner ? !ReferenceEquals(target, P_PetOwner) || !CanFollowOwner(P_PetOwner) : !ReferenceEquals(target, this))
                    return false;

                return PetCasterBrain.CleanseStillNeeded(forOwner ? ReadOwnerTopLayers() : ReadPetTopLayers(), spell.DispelSchool, spell.MinPower, spell.MaxPower);
            }

            if (kind != CasterSpellKind.Heal)
                return true;

            if (!(target is Creature creature))
                return false;

            // the same thread guards the decision used: nothing of a creature in another landblock group is read
            var readable = ReferenceEquals(creature, P_PetOwner) ? CanFollowOwner(P_PetOwner) : SharesCasterTickThread(creature);

            if (!readable)
                return false;

            return PetCasterBrain.HealStillNeeded(VitalsOf(creature), vital);
        }

        /// <summary>The pet's mana after a dropped cast returns <paramref name="used"/>, capped at the maximum.</summary>
        internal static uint RefundedMana(uint current, uint max, uint used) => (uint)Math.Min((ulong)current + used, max);

        private static CasterVitals VitalsOf(Creature creature) => new CasterVitals(
            CasterVitals.Fraction(creature.Health.Current, creature.Health.MaxValue),
            CasterVitals.Fraction(creature.Stamina.Current, creature.Stamina.MaxValue),
            CasterVitals.Fraction(creature.Mana.Current, creature.Mana.MaxValue));

        /// <summary>A sibling is the owner's other combat pet, alive, in the world, and on this pet's tick thread.</summary>
        private void CollectSiblings(Pet candidate)
        {
            if (!(candidate is CombatPet sibling) || ReferenceEquals(sibling, this))
                return;

            // the thread check first: nothing else of a sibling in another landblock group is read
            if (!SharesCasterTickThread(sibling) || sibling.IsDestroyed || sibling.PhysicsObj == null || sibling.IsDead)
                return;

            casterSiblingPets.Add(sibling);
            casterSiblings.Add(new CasterAlly(VitalsOf(sibling), GetCylinderDistance(sibling)));
        }

        private bool SharesCasterTickThread(WorldObject other)
        {
            var mine = CurrentLandblock;
            var theirs = other.CurrentLandblock;

            if (mine == null || theirs == null)
                return false;

            return PetFollowRules.SharesTickThread(mine == theirs, mine.CurrentLandblockGroup, theirs.CurrentLandblockGroup);
        }

        /// <summary>The owner's enchantment top layers, from one copy of the registry taken under its lock.</summary>
        private IReadOnlyDictionary<SpellCategory, CarriedEnchantment> ReadOwnerTopLayers()
        {
            var owner = P_PetOwner;

            if (owner?.EnchantmentManager == null || !owner.EnchantmentManager.HasEnchantments)
                return null;

            return BuildDispelLayers(owner);
        }

        /// <summary>
        /// <paramref name="subject"/>'s enchantment top layers from one copy of its registry, each with its alignment, school
        /// and dispellability resolved (EnchantmentDispelFactsCache.Resolve), and with the PvP dispel vulnerability lock
        /// applied the way EnchantmentManager.SelectDispel applies it: while subject (a Player) is inside its PK lock window,
        /// a vulnerability ANOTHER player cast on it is not dispellable. The lock's own predicate decides
        /// (PvpRules.ExcludeLockedVulnerabilities), without the applied-count Report a real dispel makes. A non-player
        /// subject is never locked. The caller has already made the read safe (CanFollowOwner for the owner).
        /// </summary>
        internal static IReadOnlyDictionary<SpellCategory, CarriedEnchantment> BuildDispelLayers(Creature subject)
        {
            if (subject?.EnchantmentManager == null || !subject.EnchantmentManager.HasEnchantments)
                return null;

            return BuildDispelLayers(subject, subject.Biota.PropertiesEnchantmentRegistry.Clone(subject.BiotaDatabaseLock));
        }

        /// <summary>The registry half of <see cref="BuildDispelLayers(Creature)"/>, over an already-cloned registry.</summary>
        internal static IReadOnlyDictionary<SpellCategory, CarriedEnchantment> BuildDispelLayers(WorldObject subject, List<PropertiesEnchantmentRegistry> registry)
        {
            var open = PvpRules.ExcludeLockedVulnerabilities(subject, registry);

            if (ReferenceEquals(open, registry))
                return VulnerabilityProfile.BuildTopLayers(registry, EnchantmentDispelFactsCache.Resolve);

            var locked = new HashSet<PropertiesEnchantmentRegistry>(registry.Except(open));

            return VulnerabilityProfile.BuildTopLayers(registry, e =>
            {
                var facts = EnchantmentDispelFactsCache.Resolve(e);

                return facts.HasValue && locked.Contains(e)
                    ? new EnchantmentDispelFacts(facts.Value.IsBeneficial, facts.Value.School, false)
                    : facts;
            });
        }

        /// <summary>
        /// The pet's OWN enchantment top layers, each with its alignment, school and dispellability resolved (a
        /// CleanseSelf's question). The pet's registry belongs to this tick thread, so no thread guard is needed.
        /// </summary>
        private IReadOnlyDictionary<SpellCategory, CarriedEnchantment> ReadPetTopLayers()
        {
            if (EnchantmentManager == null || !EnchantmentManager.HasEnchantments)
                return null;

            return BuildDispelLayers(this);
        }

        /// <summary>
        /// The harmful-spell candidates, in preference order, into <paramref name="enemies"/> (with their
        /// distances alongside): only monsters engaged with the owner's side (AcceptsPetTarget with the
        /// never-initiates rule, which a caster always carries by design - it never starts a fight - unless its
        /// owner has turned /summonaggro on, when any valid monster qualifies - a caster then starts a fight only by
        /// casting, it does not wake monsters), only those on this pet's tick thread, inside MaxChaseRange (as
        /// IsValidAssistTarget requires - Movement() abandons any chase past it); with /summonaggro on, an UNENGAGED
        /// candidate must also be inside this pet's VisualAwarenessRangeSq, the same bound FindNextTarget applies
        /// (an engaged one keeps the MaxChaseRange bound alone), the current AttackTarget first, then the rest nearest first, at most MaxCasterEnemies.
        /// The thread check runs first, so nothing of a monster in another landblock group is read.
        /// </summary>
        internal void CollectCasterEnemies(IEnumerable<Creature> visible, Func<WorldObject, float> distanceTo, List<Creature> enemies, List<float> distances)
        {
            enemies.Clear();
            distances.Clear();

            Creature current = null;
            var currentDistance = 0.0f;

            // read once: the owner's /summonaggro setting is live and the same answer must apply to every candidate
            var neverInitiates = NeverInitiates;

            // by design but released by the owner's toggle: unengaged candidates are then fair game, but only inside awareness range
            var aggroOn = NeverInitiatesByDesign && !neverInitiates;

            foreach (var creature in visible)
            {
                if (creature == null || ReferenceEquals(creature, this) || !SharesCasterTickThread(creature) || !AcceptsPetTarget(creature, neverInitiates))
                    continue;

                var distance = distanceTo(creature);

                if (distance >= MaxChaseRange)
                    continue;

                // FindNextTarget's own test: squared cylinder distance against VisualAwarenessRangeSq, rejected when greater
                if (aggroOn && distance * distance > VisualAwarenessRangeSq && !PetEngagement.IsEngagedWithOwner(creature, P_PetOwner))
                    continue;

                if (ReferenceEquals(creature, AttackTarget))
                {
                    current = creature;
                    currentDistance = distance;
                    continue;
                }

                // insertion by distance: the list is tiny
                var at = enemies.Count;
                while (at > 0 && distances[at - 1] > distance)
                    at--;

                enemies.Insert(at, creature);
                distances.Insert(at, distance);
            }

            if (current != null)
            {
                enemies.Insert(0, current);
                distances.Insert(0, currentDistance);
            }

            if (enemies.Count > MaxCasterEnemies)
            {
                enemies.RemoveRange(MaxCasterEnemies, enemies.Count - MaxCasterEnemies);
                distances.RemoveRange(MaxCasterEnemies, distances.Count - MaxCasterEnemies);
            }
        }

        /// <summary>
        /// The most enemies one decision weighs. Each one the brain reaches costs a registry copy
        /// (BuildVulnerabilityProfile), and it only moves past an enemy when every harmful spell is Redundant on it.
        /// </summary>
        internal const int MaxCasterEnemies = 6;

        /// <summary>
        /// This pet's castable spells, from its runtime spellbook (the bound spells ApplySummonStats added, plus any
        /// the weenie carries), with each range resolved at this pet's own magic skill in that spell's school.
        /// </summary>
        private List<CasterSpell> BuildCasterSpells()
        {
            var result = new List<CasterSpell>();

            if (Biota.PropertiesSpellBook == null)
                return result;

            foreach (var spellId in Biota.GetKnownSpellsIds(BiotaDatabaseLock))
            {
                var facts = CasterSpellFactsCache.Get(spellId);

                // item enchantments target items, never a creature: refused at binding, and skipped here too for a
                // spell the WEENIE carries, which never went through binding
                if (facts.Kind == CasterSpellKind.Unusable || facts.School == MagicSchool.ItemEnchantment)
                    continue;

                var skill = GetCreatureSkill(facts.School);
                var rangeSkill = skill == null ? 0u : skill.InitLevel + skill.Ranks;

                var range = PetCasterBrain.ResolveRange(facts.Kind, facts.BaseRangeConstant, facts.BaseRangeMod, rangeSkill, Player.MaxRadarRange_Outdoors);

                // Cached once. Deterministic only because caster pets never train ManaConversion (pinned by CustomSummonWeenieTests.Casters_DoNotTrainManaConversion); with it trained this would freeze one random roll.
                var manaCost = CalculateManaUsage(this, new Spell(spellId), null);

                result.Add(new CasterSpell(spellId, facts.Kind, facts.Vital, facts.Category, facts.Power, facts.Shape, range, manaCost, facts.IsProjectile, facts.DispelSchool, facts.MinPower, facts.MaxPower));

                if (facts.Kind == CasterSpellKind.Strip)
                    casterHasStrip = true;
            }

            return result;
        }

        /// <summary>For tests: the spells the brain would weigh, supplied directly (no dat, no spell table).</summary>
        internal void SetCasterSpellsForTest(List<CasterSpell> spells) => casterSpells = spells;
    }
}
