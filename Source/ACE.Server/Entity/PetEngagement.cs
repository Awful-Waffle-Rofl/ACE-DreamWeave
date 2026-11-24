using ACE.Entity.Enum;
using ACE.Server.WorldObjects;

namespace ACE.Server.Entity
{
    /// <summary>
    /// Custom Summons: what "already in combat" means for a pet that never starts a fight (PropertyBool
    /// SummonNeverInitiates - the Archer and every caster), and for the Tank's taunt pulse, which only ever
    /// pulls monsters that are already fighting its side.
    ///
    /// ENGAGED = the monster's current AttackTarget is the owner, or a pet the owner owns (this pet or a
    /// sibling). Nothing else counts: a monster fighting another player, or another player's pet, is not
    /// engaged, and neither is an idle one (Sleep and MoveToHome both clear AttackTarget).
    ///
    /// ONE EXCEPTION, a creature that never attacks (<see cref="NeverAttacks"/>: Tolerance NoAttack, such as
    /// the Matron Knot's Hardened Hive Rock 1007554): it never takes an AttackTarget, so the rule above could
    /// never call it engaged and a never-initiates pet would never help break it (Discord bug report
    /// 1570812629348393113, 2026-11-15). It counts as engaged with an owner while that owner, or a pet of that
    /// owner, has damaged it within the last <see cref="NeverAttacksEngagementSeconds"/> seconds
    /// (<see cref="RecordDamage"/>, from DamageHistory.Add; every hit refreshes the time). Nothing is woken by
    /// this: a NoAttack creature never aggroes or retaliates, which is the whole reason the never-initiates rule
    /// exists. The window keeps a target dummy hit once from staying the owner's fight for the rest of the session.
    ///
    /// THREADING. This runs on the pet's landblock tick and reads another object's state: the monster's
    /// AttackTarget field and, when that is a Pet, the pet's P_PetOwner field. Both are single reference
    /// reads (atomic in .NET) compared by reference only - no enchantment registry, no lock - so a monster
    /// owned by another landblock group's thread can at worst hand back a value one tick stale, never a torn
    /// or throwing read. The never-attacks exception first reads the creature's owner-damage map
    /// (Creature.DamagedByOwners: one reference read of an immutable map swapped whole, then a lookup in it,
    /// still no lock), and only when that map holds a recent hit by this owner does it read Tolerance, which
    /// goes through the biota's read lock. A normal monster never has a map, so it never reaches that lock.
    /// Callers that go on to WRITE the monster (the taunt pulse) carry their own same-landblock-group guard;
    /// see CombatPet.TickTauntPulse.
    /// </summary>
    public static class PetEngagement
    {
        /// <summary>How long after the owner side's latest hit a never-attacking creature stays engaged with that owner.</summary>
        public const double NeverAttacksEngagementSeconds = 60.0;

        /// <summary>
        /// Whether a pet is held to the never-initiates rule right now: it is by design (the weenie flag or a caster)
        /// AND its owner has not turned /summonaggro on. A pet that is not by design is never held.
        /// </summary>
        public static bool EffectiveNeverInitiates(bool byDesign, bool ownerAggroOn) => byDesign && !ownerAggroOn;

        public static bool IsEngagedWithOwner(Creature monster, Player owner) => IsEngagedWithOwner(monster, owner, Timers.RunningTime);

        /// <summary><see cref="IsEngagedWithOwner(Creature, Player)"/> at an explicit clock reading (tests).</summary>
        internal static bool IsEngagedWithOwner(Creature monster, Player owner, double now)
        {
            if (monster == null || owner == null)
                return false;

            // read once: a second read could observe a different target
            var target = monster.AttackTarget;

            if (target != null)
            {
                if (ReferenceEquals(target, owner))
                    return true;

                if (target is Pet pet && ReferenceEquals(pet.P_PetOwner, owner))
                    return true;
            }

            // a creature that never attacks never takes a target: the owner's side damaging it recently is its engagement.
            // The lock-free record first, so a normal monster (no record) never reaches the locked Tolerance read; the
            // Tolerance re-check stays, so a creature whose NoAttack is lifted falls back to the AttackTarget rule at once.
            return monster.WasDamagedByOwner(owner.Guid.Full, now, NeverAttacksEngagementSeconds) && NeverAttacks(monster);
        }

        /// <summary>
        /// A creature that never attacks or retaliates: Tolerance carries NoAttack (the Matron's Hive Rock, retail
        /// Cave-in style breakables). The same bit Player_Monster's wake check and the pet retaliate exclusion read.
        /// </summary>
        public static bool NeverAttacks(Creature monster) =>
            monster != null && !(monster is Player) && (monster.Tolerance & Tolerance.NoAttack) != 0;

        /// <summary>
        /// The owner whose side <paramref name="attacker"/> fights on: the player itself, or a pet's owner. Null for
        /// anything else (a monster, a trap, an ownerless pet).
        /// </summary>
        public static Player OwnerSideOf(WorldObject attacker) => attacker switch
        {
            Player player => player,
            Pet pet => pet.P_PetOwner,
            _ => null,
        };

        /// <summary>
        /// Called by DamageHistory.Add for every hit that removed health: when the attacker is a player or a player's pet
        /// and the victim never attacks (<see cref="NeverAttacks"/>), that player's latest-hit time on the victim is set to
        /// now, and the victim counts as engaged with that player's side for <see cref="NeverAttacksEngagementSeconds"/>.
        /// Nothing is recorded for any other victim, so a normal monster's engagement stays exactly its AttackTarget. The
        /// owner test runs first (type tests only), so a monster's or an ownerless hit never reads Tolerance.
        /// </summary>
        public static void RecordDamage(Creature victim, WorldObject attacker) => RecordDamage(victim, attacker, Timers.RunningTime);

        /// <summary><see cref="RecordDamage(Creature, WorldObject)"/> at an explicit clock reading (tests).</summary>
        internal static void RecordDamage(Creature victim, WorldObject attacker, double now)
        {
            if (victim == null)
                return;

            var owner = OwnerSideOf(attacker);

            if (owner == null || !NeverAttacks(victim))
                return;

            victim.RecordDamageByOwner(owner.Guid.Full, now);
        }

        /// <summary>
        /// Whether a pet keeps the target it already holds, as far as engagement goes (HandleFindTarget's
        /// currentValid also requires the target alive and visible). An unflagged pet always keeps it - today's
        /// rule. A never-initiates pet keeps it only while the monster is still engaged with the owner's side,
        /// so it lets go of a monster that has turned on another player, gone home, or fallen asleep.
        /// </summary>
        public static bool HoldsCurrentTarget(bool neverInitiates, Creature current, Player owner) =>
            !neverInitiates || IsEngagedWithOwner(current, owner);

        /// <summary>
        /// The Tank pulse's per-monster rule, pure: a living, awake, non-player, non-pet monster engaged with
        /// <paramref name="owner"/> and within <paramref name="radius"/> metres. The Awake test is belt and
        /// braces - an engaged monster is awake - so the pulse can never reach ApplyTaunt's WakeUp branch.
        /// </summary>
        public static bool IsTauntPulseCandidate(Creature monster, Player owner, double distance, double radius)
        {
            if (monster == null || monster is Player || monster is CombatPet || monster.IsDead || monster.Teleporting)
                return false;

            // the player Taunt's own exclusion (TauntAbility.Activate): a non-attackable creature with no
            // targeting tactic is scenery, never a monster to pull
            if (!monster.Attackable && monster.TargetingTactic == TargetingTactic.None)
                return false;

            if (monster.MonsterState != Creature.State.Awake)
                return false;

            if (distance > radius)
                return false;

            return IsEngagedWithOwner(monster, owner);
        }
    }
}
