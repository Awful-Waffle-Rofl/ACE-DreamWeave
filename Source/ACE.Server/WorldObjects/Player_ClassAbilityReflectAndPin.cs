using ACE.Common;
using ACE.Entity.Enum;
using ACE.Server.ClassAbilities;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Managers;
using ACE.Server.Pvp.Rules;

namespace ACE.Server.WorldObjects
{
    partial class Player
    {
        // The two class-ability overhaul entries that needed a site of their own rather than an existing
        // hook: Reflect (Vanguard T3), dispatched by hand from the two projectile collision handlers, and
        // the player-facing half of Pinning Shot (Archer T2), whose roll rides IOutgoingDamageAbility but
        // whose message needs the private combat-message helper on Player.

        /// <summary>
        /// Reflect (Vanguard T3): rolls whether an incoming PROJECTILE bounces back at whoever fired it.
        /// Returns TRUE when it did, in which case the caller must apply NOTHING to this player - the
        /// projectile has already resolved against the firer.
        ///
        /// CALLED FROM THE TWO SITES A PROJECTILE CAN REACH A PLAYER, which share no common sink:
        /// ProjectileCollisionHelper.OnCollideObject (arrows, crossbow bolts, thrown weapons) and
        /// SpellProjectile.OnCollideObject (war bolts, void projectiles). Both call in AFTER the damage
        /// figure is final and BEFORE it is applied, so the number reflected is exactly the number this
        /// player would otherwise have taken.
        ///
        /// PROJECTILES ONLY IS RE-DECIDED HERE, from the attack's own CombatType, rather than being left to
        /// the fact that today's two callers happen to be projectile handlers -
        /// ReflectMagicAbility.IsReflectableCombatType refuses Melee outright. A future site wired to a
        /// melee path would be refused rather than silently widening the ability onto a second, much larger
        /// coverage area on the same roll.
        ///
        /// PvE ONLY: a Player firer is refused, so a reflected projectile never resolves against another
        /// player. Self-damage and a dead firer are refused for the same reason every other dispatch does.
        ///
        /// THE LATCH IS WHY TWO REFLECTING ACTORS TERMINATE. Reflected damage is applied through the firer's
        /// ordinary TakeDamage, which runs its monster-effect incoming filters - and a monster carrying a
        /// "reflect" effect answers by damaging this player, whose Thorns answers by damaging the monster
        /// again. Creature.TryEnterReflect is claimed around the whole application and released in a finally,
        /// so the first re-entrant reflect attempt anywhere in that chain is refused outright (the latch is
        /// depth 1 and [ThreadStatic]). The exchange is therefore bounded no matter what either side carries.
        /// A refused latch means no reflect: the projectile resolves normally, which is the correct fallback
        /// because the only way to be inside someone else's reflect already is to be resolving a bounce.
        /// </summary>
        public bool TryReflectProjectile(Creature firer, CombatType combatType, DamageType damageType, float damage)
        {
            // hot path (every incoming projectile): a player with no class abilities at all pays only a
            // cheap dictionary-count check
            if (GetClassAbilityCache().Count == 0)
                return false;

            if (!ReflectMagicAbility.IsReflectableCombatType(combatType))
                return false;

            if (firer == null || PvpClassifier.IsPvp(firer, this) || firer == this || firer.IsDead)
                return false;

            if (damage <= 0.0f)
                return false;

            // TryGetClassAbility already gates on class_abilities_enabled and a learned rank
            if (!TryGetClassAbility(ClassAbilityId.ReflectMagic, out var rank))
                return false;

            // Missile Defense is MULTIPLICATIVE on Reflect's own rank bonus (2026-09-12 overhaul), not an
            // additive rider beside it. At zero effective Missile Defense the factor is exactly 1.0 and the
            // chance is bit-identical to rank alone. class_ability_affinity_chance_cap, passed below, bounds
            // the AMOUNT the multiply adds rather than a raw skill quotient.
            var missileDefense = GetClassAbilityAffinityMultiplier(Skill.MissileDefense);

            var chance = ReflectMagicAbility.ReflectChance(rank,
                PropertyManager.GetDouble("class_ability_reflectmagic_chance_base").Item,
                PropertyManager.GetDouble("class_ability_reflectmagic_chance_step").Item,
                missileDefense,
                PropertyManager.GetDouble("class_ability_affinity_chance_cap").Item);

            if (ThreadSafeRandom.Next(0.0f, 1.0f) > chance)
                return false;

            // The roll is spent before the latch is asked for, deliberately: a refused latch is vanishingly
            // rare (it means this projectile arrived while a reflect was already resolving on this thread)
            // and paying a roll for it is cheaper than holding the latch across the roll.
            if (!Creature.TryEnterReflect())
                return false;

            try
            {
                // Attributed to THIS PLAYER, not to the firer, so kill credit, aggro and DamageHistory work
                // through the normal path - the same attribution Thorns uses for its reflect.
                firer.TakeDamage(this, damageType, damage);

                SendClassAbilityCombatMessage($"You reflect {firer.Name}'s projectile back at it!");
            }
            finally
            {
                Creature.ExitReflect();
            }

            return true;
        }

        /// <summary>
        /// The player-facing half of a landed Pinning Shot. Separate from the handler only because the
        /// combat-message helper is private to Player; the roll, the affinity and the cap all live in
        /// PinningShotAbility, which is what ClassAbilityAffinityDeclarationTests scans.
        /// </summary>
        public void OnClassAbilityPinApplied(Creature target, double seconds)
        {
            if (target == null)
                return;

            SendClassAbilityCombatMessage($"Your shot pins {target.Name} in place for {seconds:N0} seconds!");
        }
    }
}
