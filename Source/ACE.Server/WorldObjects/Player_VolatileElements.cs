using ACE.Common;
using ACE.Entity.Enum;
using ACE.Server.ClassAbilities;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Managers;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// The Player half of Volatile Elements (Spellsword T2, 2026-10-30). The arithmetic lives in
    /// VolatileElementsMath and the handler as pure statics; this file only reads the rank, the tunables and the
    /// affinity and calls them. PvE only through ArchmageResistanceMath.IsPveTarget, and a suppressed player
    /// (PK facet, arena mask) reads rank 0 through TryGetClassAbility.
    /// </summary>
    partial class Player
    {
        /// <summary>
        /// The type this weapon hit on <paramref name="target"/> is re-typed to, or null to leave the hit's
        /// <paramref name="original"/> type alone. Called exactly once per DamageEvent from
        /// DamageEvent.DoCalculateDamage. Null, with no random draw, when the ability is not owned, the target is
        /// not a PvE target, or the original type is not eligible (VolatileElementsMath.IsEligibleOriginal).
        /// </summary>
        public DamageType? RollVolatileElement(Creature target, DamageType original)
        {
            // cheap early outs ahead of the rank and tunable reads; Decide re-checks both
            if (!ArchmageResistanceMath.IsPveTarget(target) || !VolatileElementsMath.IsEligibleOriginal(original))
                return null;

            if (!TryGetClassAbility(ClassAbilityId.VolatileElements, out var rank) || rank <= 0)
                return null;

            var chance = VolatileElementsMath.Chance(rank,
                PropertyManager.GetDouble("class_ability_volatileelements_chance_per_rank").Item,
                GetClassAbilityAffinityMultiplier(Skill.ItemTinkering),
                PropertyManager.GetDouble("class_ability_affinity_chance_cap").Item);

            return RollVolatileElementCore(target, original, chance);
        }

        /// <summary>
        /// The delegate-building half of <see cref="RollVolatileElement"/>, split out so ITS lambdas live here.
        /// A lambda capturing <paramref name="target"/> makes the compiler allocate the closure at the entry of
        /// the method that declares the captured variable; kept in RollVolatileElement, that allocation would run
        /// on every player-vs-monster hit, owners and non-owners alike, before any early out. Here it runs only
        /// for an owner whose hit is actually rolled.
        /// </summary>
        private static DamageType? RollVolatileElementCore(Creature target, DamageType original, double chance)
        {
            return VolatileElementsMath.Decide(ArchmageResistanceMath.IsPveTarget(target), original, chance,
                () => ThreadSafeRandom.Next(0.0f, 1.0f),
                ThreadSafeRandom.Next,
                type => ArchmageResistanceMath.InnateResistance(target, Creature.GetResistanceType(type)));
        }
    }
}
