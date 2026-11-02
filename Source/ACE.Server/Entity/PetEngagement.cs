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
    /// THREADING. This runs on the pet's landblock tick and reads another object's state: the monster's
    /// AttackTarget field and, when that is a Pet, the pet's P_PetOwner field. Both are single reference
    /// reads (atomic in .NET) compared by reference only - no dictionary, no enchantment registry, no lock
    /// - so a monster owned by another landblock group's thread can at worst hand back a value one tick
    /// stale, never a torn or throwing read. Callers that go on to WRITE the monster (the taunt pulse)
    /// carry their own same-landblock-group guard; see CombatPet.TickTauntPulse.
    /// </summary>
    public static class PetEngagement
    {
        public static bool IsEngagedWithOwner(Creature monster, Player owner)
        {
            if (monster == null || owner == null)
                return false;

            // read once: a second read could observe a different target
            var target = monster.AttackTarget;

            if (target == null)
                return false;

            if (ReferenceEquals(target, owner))
                return true;

            return target is Pet pet && ReferenceEquals(pet.P_PetOwner, owner);
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
