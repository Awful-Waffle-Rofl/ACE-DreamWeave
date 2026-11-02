using System;

using ACE.Entity.Enum;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities
{
    /// <summary>
    /// The shared pure core of Elemental Inversion (Archmage T1) and Arcane Hollow (Archmage T2), Archmage pass
    /// 2026-10-24, as revised by the owner rulings of the same day.
    ///
    /// BOTH ABILITIES MOVE ONE NUMBER: THE TARGET'S INNATE RESIST TERM. Creature.GetResistanceMod(ResistanceType,
    /// ...) is innate ResistX x the enchantment/vulnerability term for the hit's damage type. Neither ability
    /// changes the damage type or the enchantment term: armor, rend/cleave (the original type's
    /// WeaponResistanceMod), the original type's vulnerabilities and protections, Hunter's Mark and the rest all
    /// read the hit's own type exactly as before. They only hand the lookup a replacement innate value through
    /// its innateOverride parameter. Never a ratio applied after the lookup, never a type swap.
    ///
    /// - Elemental Inversion, on a proc, replaces the innate term with the target's WEAKEST innate value: the
    ///   HIGHEST innate ResistX among <see cref="InnateResistTypes"/>. The set includes the original type, so
    ///   the substituted value is never below the original and a proc can never lower damage.
    /// - Arcane Hollow then moves that innate value the fraction f of the way to 1.0, r' = r + f(1 - r) (the
    ///   Exsanguinate shape, <see cref="ExsanguinateMath.ResistanceIgnoreMultiplier"/>), with no effect when
    ///   r &gt;= 1 or r &lt;= 0. When both apply to one hit, substitution happens first.
    ///
    /// Only hits whose ORIGINAL damage type is in <see cref="InnateResistTypes"/> are eligible: Nether, life
    /// damage (HealthDrain), Drain and DoT ticks never roll and never take the ignore. Immunity is respected:
    /// a target whose innate ResistX for the hit's own type is &lt;= 0 gets no override at all.
    /// </summary>
    public static class ArchmageResistanceMath
    {
        /// <summary>
        /// The seven innate resist types both abilities act on, in tie-break order: the three physical types
        /// and the four classic elements. Nether is deliberately absent (owner ruling 2026-10-24).
        /// </summary>
        public static readonly ResistanceType[] InnateResistTypes =
        {
            ResistanceType.Fire,
            ResistanceType.Cold,
            ResistanceType.Acid,
            ResistanceType.Electric,
            ResistanceType.Pierce,
            ResistanceType.Slash,
            ResistanceType.Bludgeon,
        };

        /// <summary>TRUE for a resistance type in <see cref="InnateResistTypes"/>.</summary>
        public static bool IsCovered(ResistanceType type) => Array.IndexOf(InnateResistTypes, type) >= 0;

        /// <summary>
        /// The PvE gate all three Archmage pass Player methods share: a non-null target that is not a player.
        /// Pure.
        /// </summary>
        public static bool IsPveTarget(Creature target) => target != null && target is not Player;

        /// <summary>
        /// The target's innate (weenie) resist multiplier for one type: ResistX, absent = 1.0. The same term
        /// Creature.GetResistanceMod(ResistanceType) multiplies in. 1.0 for any type outside the eight
        /// physical/elemental ones.
        /// </summary>
        public static double InnateResistance(Creature target, ResistanceType type)
        {
            if (target == null)
                return 1.0;

            switch (type)
            {
                case ResistanceType.Slash:    return target.ResistSlash ?? 1.0;
                case ResistanceType.Pierce:   return target.ResistPierce ?? 1.0;
                case ResistanceType.Bludgeon: return target.ResistBludgeon ?? 1.0;
                case ResistanceType.Fire:     return target.ResistFire ?? 1.0;
                case ResistanceType.Cold:     return target.ResistCold ?? 1.0;
                case ResistanceType.Acid:     return target.ResistAcid ?? 1.0;
                case ResistanceType.Electric: return target.ResistElectric ?? 1.0;
                case ResistanceType.Nether:   return target.ResistNether ?? 1.0;
                default:                      return 1.0;
            }
        }

        /// <summary>
        /// The weakest innate value: the HIGHEST innate multiplier among <see cref="InnateResistTypes"/> (a
        /// higher multiplier lets more damage through). Pure over the supplied lookup.
        /// </summary>
        public static double WeakestInnate(Func<ResistanceType, double> innate)
        {
            var best = innate(InnateResistTypes[0]);

            for (var i = 1; i < InnateResistTypes.Length; i++)
                best = Math.Max(best, innate(InnateResistTypes[i]));

            return best;
        }

        /// <summary>
        /// Arcane Hollow's shape on an innate value: r + f(1 - r) for 0 &lt; r &lt; 1 and f &gt; 0 (f clamped
        /// to 1), otherwise r unchanged. Pure.
        /// </summary>
        public static double HollowInnate(double innate, double ignoreFraction)
        {
            if (innate <= 0.0 || innate >= 1.0 || ignoreFraction <= 0.0)
                return innate;

            return innate + Math.Min(1.0, ignoreFraction) * (1.0 - innate);
        }

        /// <summary>Whether an Elemental Inversion roll lands: a chance of 1 or more always does, else roll &lt; chance. Pure.</summary>
        public static bool InversionLands(double inversionChance, double roll)
        {
            return inversionChance >= 1.0 || (inversionChance > 0.0 && roll < inversionChance);
        }

        /// <summary>
        /// The innate value the resistance lookup should use for this hit, or null to leave it untouched.
        /// Null for a type outside <see cref="InnateResistTypes"/> regardless of the roll. Otherwise the
        /// original innate value, replaced by <see cref="WeakestInnate"/> when <paramref name="roll"/> lands
        /// under <paramref name="inversionChance"/> (a chance of 1 always lands), then moved by
        /// <see cref="HollowInnate"/> with <paramref name="hollowFraction"/>. Null when nothing changed. Pure.
        /// </summary>
        public static double? InnateOverride(ResistanceType originalType, Func<ResistanceType, double> innate,
            double inversionChance, double roll, double hollowFraction)
        {
            if (!IsCovered(originalType))
                return null;

            var original = innate(originalType);

            // Immunity is respected (owner ruling 2026-10-24): a target immune to the hit's own type (innate
            // ResistX <= 0) stays immune - no substitution, and Arcane Hollow's shape leaves r <= 0 alone too.
            if (original <= 0.0)
                return null;

            var value = original;

            if (InversionLands(inversionChance, roll))
                value = WeakestInnate(innate);

            value = HollowInnate(value, hollowFraction);

            return value == original ? (double?)null : value;
        }

        /// <summary>
        /// The whole per-hit decision behind Player.GetArchmageInnateOverride, as a pure function. Returns the
        /// innate override for the lookup, or null to leave it alone. <paramref name="roll"/> is invoked at most
        /// once, and ONLY when an Elemental Inversion roll can matter: never for a non-PvE target, a type outside
        /// <see cref="InnateResistTypes"/> (Nether, HealthDrain), an immune original type, or a zero chance.
        /// <paramref name="hollowFraction"/> counts only when <paramref name="magicDamage"/> is TRUE (Arcane
        /// Hollow is magic damage only).
        /// </summary>
        public static double? Decide(bool pveTarget, ResistanceType originalType, bool magicDamage,
            double inversionChance, double hollowFraction, Func<double> roll, Func<ResistanceType, double> innate)
        {
            if (!pveTarget || !IsCovered(originalType))
                return null;

            var hollow = magicDamage ? hollowFraction : 0.0;

            if (inversionChance <= 0.0 && hollow <= 0.0)
                return null;

            if (innate(originalType) <= 0.0)
                return null;

            var rolled = inversionChance > 0.0 ? roll() : 1.0;

            return InnateOverride(originalType, innate, inversionChance, rolled, hollow);
        }
    }
}
