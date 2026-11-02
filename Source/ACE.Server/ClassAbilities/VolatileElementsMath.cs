using System;
using System.Collections.Generic;

using ACE.Entity.Enum;
using ACE.Server.WorldObjects;
using ACE.Server.WorldObjects.Entity;

namespace ACE.Server.ClassAbilities
{
    /// <summary>
    /// The pure core of Volatile Elements (Spellsword T2, owner rulings 2026-10-30). Every landed melee or
    /// missile WEAPON hit a player lands on a monster is re-typed to one of the eight damage types in
    /// <see cref="Pool"/>. With chance (rank x per-rank, times the Item Tinkering affinity, the affinity's added
    /// amount capped) the new type is the monster's WEAKEST innate resist - the HIGHEST innate ResistX among the
    /// eight, ties broken uniformly among the tied types - otherwise it is a uniform pick from all eight (which
    /// can also land on the weakest). Immune types are NOT skipped: a re-typed hit can do 0.
    ///
    /// Everything type-keyed then follows the rolled type (armor, vulnerabilities/protections, innate resist,
    /// shields, the same hit's Spellblade/Runeblade/Spellstorm/Sundermark procs). Rend is the one exception
    /// that looks back: the weapon keeps the better of the rend for its ORIGINAL type and the rend for the
    /// rolled type (<see cref="ResolveWeaponResistanceMod"/>), so a fire-rend weapon that rolls cold keeps its
    /// fire rend.
    ///
    /// ELIGIBLE ORIGINAL TYPE (<see cref="IsEligibleOriginal"/>): the hit's own type must be made only of the
    /// eight pool types - a single one of them, or a combined physical/elemental flag such as Slash|Pierce.
    /// Undef, Health, Stamina, Mana, Base and anything carrying one of those bits never rolls. A player weapon
    /// hit essentially always passes (Player.GetDamageType resolves a weapon to a single physical or elemental
    /// type, and unarmed is Bludgeon); the gate exists so a non-weapon type that ever reaches the seam is left
    /// alone rather than re-typed.
    /// </summary>
    public static class VolatileElementsMath
    {
        /// <summary>The eight types a hit can be re-typed to, in a fixed order (also the tie-break order).</summary>
        public static readonly DamageType[] Pool =
        {
            DamageType.Slash,
            DamageType.Pierce,
            DamageType.Bludgeon,
            DamageType.Fire,
            DamageType.Cold,
            DamageType.Acid,
            DamageType.Electric,
            DamageType.Nether,
        };

        /// <summary>The union of <see cref="Pool"/>.</summary>
        public static readonly DamageType PoolMask =
            DamageType.Slash | DamageType.Pierce | DamageType.Bludgeon | DamageType.Fire |
            DamageType.Cold | DamageType.Acid | DamageType.Electric | DamageType.Nether;

        /// <summary>
        /// TRUE when a hit of <paramref name="original"/> may be re-typed: non-Undef and made ONLY of pool bits.
        /// See the class comment.
        /// </summary>
        public static bool IsEligibleOriginal(DamageType original)
        {
            return original != DamageType.Undef && (original & ~PoolMask) == 0;
        }

        /// <summary>
        /// The success chance: rank x <paramref name="chancePerRank"/>, MULTIPLIED by
        /// <paramref name="affinityMultiplier"/> (Player.GetClassAbilityAffinityMultiplier, a factor &gt;= 1),
        /// with the AMOUNT that multiply adds clamped to <paramref name="affinityCap"/> (0 = uncapped) - the same
        /// shape as SundermarkAbility.Chance, without a gear term. 0 at rank 0. Pure.
        /// </summary>
        public static double Chance(int rank, double chancePerRank, double affinityMultiplier, double affinityCap)
        {
            if (rank <= 0)
                return 0.0;

            var rankBonus = rank * chancePerRank;

            var added = rankBonus * affinityMultiplier - rankBonus;

            if (affinityCap > 0.0)
                added = Math.Min(added, affinityCap);

            added = Math.Max(0.0, added);

            return Math.Max(0.0, rankBonus + added);
        }

        /// <summary>
        /// Every pool type tied for the HIGHEST innate multiplier (the weakest resist: a higher multiplier lets
        /// more damage through), in <see cref="Pool"/> order. Never empty. Pure over the supplied lookup.
        /// </summary>
        public static DamageType[] WeakestTypes(Func<DamageType, double> innate)
        {
            var best = double.NegativeInfinity;
            var tied = new List<DamageType>(Pool.Length);

            foreach (var type in Pool)
            {
                var value = innate(type);

                if (value > best)
                {
                    best = value;
                    tied.Clear();
                    tied.Add(type);
                }
                else if (value == best)
                {
                    tied.Add(type);
                }
            }

            return tied.ToArray();
        }

        /// <summary>
        /// The whole per-hit decision, pure. Returns the type the hit is re-typed to, or null to leave it alone.
        ///
        /// Null, with NO random draw at all, for a non-PvE hit or an ineligible original type. Otherwise
        /// <paramref name="roll"/> (a uniform double in [0, 1)) is drawn once when 0 &lt; chance &lt; 1; a chance
        /// &gt;= 1 always succeeds and a chance &lt;= 0 always falls through, neither drawing it. On success the
        /// result is the weakest type (<see cref="WeakestTypes"/>), a tie picked through
        /// <paramref name="nextInclusive"/>; on failure a uniform pick of all eight through
        /// <paramref name="nextInclusive"/>(0, 7). <paramref name="nextInclusive"/> is INCLUSIVE at both ends,
        /// matching ThreadSafeRandom.Next(int, int).
        /// </summary>
        public static DamageType? Decide(bool pveTarget, DamageType original, double chance, Func<double> roll,
            Func<int, int, int> nextInclusive, Func<DamageType, double> innate)
        {
            if (!pveTarget || !IsEligibleOriginal(original))
                return null;

            var success = chance >= 1.0 || (chance > 0.0 && roll() < chance);

            if (success)
            {
                var weakest = WeakestTypes(innate);

                return weakest.Length == 1
                    ? weakest[0]
                    : weakest[Math.Clamp(nextInclusive(0, weakest.Length - 1), 0, weakest.Length - 1)];
            }

            return Pool[Math.Clamp(nextInclusive(0, Pool.Length - 1), 0, Pool.Length - 1)];
        }

        /// <summary>
        /// The weapon's WeaponResistanceMod (rend / fixed resistance cleaving) for a hit: the plain lookup on
        /// <paramref name="rolled"/> when Volatile Elements did not fire (<paramref name="original"/> null) or
        /// landed back on the same type, otherwise the MAX of the lookup on the original type and on the rolled
        /// type, so a rend never goes missing because the hit was re-typed. <paramref name="rendFor"/> is
        /// WorldObject.GetWeaponResistanceModifier bound to the weapon, wielder and skill. Pure.
        /// </summary>
        public static float ResolveWeaponResistanceMod(Func<DamageType, float> rendFor, DamageType? original, DamageType rolled)
        {
            var rolledMod = rendFor(rolled);

            if (original == null || original.Value == rolled)
                return rolledMod;

            return Math.Max(rendFor(original.Value), rolledMod);
        }

        /// <summary>
        /// <see cref="ResolveWeaponResistanceMod(Func{DamageType, float}, DamageType?, DamageType)"/> bound to
        /// WorldObject.GetWeaponResistanceModifier, for the two live call sites (DamageEvent.ResolveDefenderHalf
        /// and the war/void branch of SpellProjectile.CalculateDamage). A direct overload rather than a lambda at
        /// those sites: a lambda capturing their parameters would allocate a closure on EVERY hit, re-typed or
        /// not, on two of the hottest paths in combat. Same rule, same order of lookups.
        /// </summary>
        public static float ResolveWeaponResistanceMod(WorldObject weapon, Creature wielder, CreatureSkill skill, DamageType? original, DamageType rolled)
        {
            var rolledMod = WorldObject.GetWeaponResistanceModifier(weapon, wielder, skill, rolled);

            if (original == null || original.Value == rolled)
                return rolledMod;

            return Math.Max(WorldObject.GetWeaponResistanceModifier(weapon, wielder, skill, original.Value), rolledMod);
        }
    }
}
