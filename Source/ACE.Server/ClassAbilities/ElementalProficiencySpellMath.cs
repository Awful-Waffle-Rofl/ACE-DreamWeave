using System;

using ACE.Entity.Enum;

namespace ACE.Server.ClassAbilities
{
    /// <summary>
    /// The pure core of Elemental Proficiency's INSTANT spell-damage effects (Fire, Slash, Pierce), kept off
    /// Player so it is testable in a host where Player's static initializer cannot run. Player's
    /// Player_ElementalProficiency partial reads the rank, affinity and gear and calls these; the arithmetic of a
    /// single magnitude is ElementalProficiencyMath.Magnitude, which reads the tunables.
    ///
    /// PIERCE TAKES THE LARGER OF ITS TWO SHAPES (owner ruling 2026-11-02). On each hit a Pierce spell gets
    /// EXACTLY ONE of: the resistance ignore (folded into Arcane Hollow's fraction at the innate-override site)
    /// or the flat x(1 + p) bonus (at the final-damage site), whichever gives more damage. The choice is made once
    /// per hit, in <see cref="DecideSpellInnate"/>, against the innate value AFTER any Elemental Inversion
    /// substitution, and both sites read that one decision (<see cref="PierceShape"/>). An IMMUNE target (the
    /// original type's innate &lt;= 0) gets neither.
    /// </summary>
    public static class ElementalProficiencySpellMath
    {
        /// <summary>Which Pierce shape a hit takes. None for a non-pierce hit, no rank, or an immune target.</summary>
        public enum PierceShape
        {
            None,
            Ignore,
            Flat,
        }

        /// <summary>
        /// Final-damage multiplier: 1 + the Fire magnitude for a Fire spell; 1 + the Pierce magnitude for a Pierce
        /// spell whose hit chose <see cref="PierceShape.Flat"/>; otherwise 1. <paramref name="magnitude"/> is the
        /// already scaled magnitude for the spell's own type.
        /// </summary>
        public static double DamageMultiplier(DamageType type, double magnitude, PierceShape pierceShape)
        {
            if (magnitude <= 0.0)
                return 1.0;

            if (type == DamageType.Fire)
                return 1.0 + magnitude;

            if (type == DamageType.Pierce && pierceShape == PierceShape.Flat)
                return 1.0 + magnitude;

            return 1.0;
        }

        /// <summary>Two independent ignore fractions stacked as 1 - (1 - h)(1 - p), each clamped to 0..1.</summary>
        public static double CombineIgnoreFractions(double hollow, double pierce)
        {
            var h = Math.Clamp(hollow, 0.0, 1.0);
            var p = Math.Clamp(pierce, 0.0, 1.0);

            return 1.0 - (1.0 - h) * (1.0 - p);
        }

        /// <summary>
        /// The larger-of decision. <paramref name="innateWithout"/> is the innate value the lookup would use with
        /// Inversion and Arcane Hollow but NO pierce; <paramref name="innateWith"/> the same with the pierce fraction
        /// folded in. Ignore gain = with / without, flat gain = 1 + magnitude; Ignore only when it is strictly
        /// greater (a tie goes to Flat, which is the same damage). None for no magnitude or a non-positive innate.
        /// </summary>
        public static PierceShape ChoosePierceShape(double magnitude, double innateWithout, double innateWith)
        {
            if (magnitude <= 0.0 || innateWithout <= 0.0)
                return PierceShape.None;

            return innateWith / innateWithout > 1.0 + magnitude ? PierceShape.Ignore : PierceShape.Flat;
        }

        /// <summary>
        /// The whole per-hit innate decision at the war/void SPELL site, Pierce included, as a pure function: the
        /// innate override for the resistance lookup (null to leave it alone) and, through
        /// <paramref name="pierceShape"/>, the Pierce shape the final-damage site must apply.
        ///
        /// Anything that is not a Pierce hit with a positive magnitude is exactly
        /// ArchmageResistanceMath.Decide (magic damage) and reports <see cref="PierceShape.None"/>. A Pierce hit
        /// rolls Elemental Inversion at most once (never for a non-PvE or immune target, or a zero chance), applies
        /// the substitution, then compares Arcane Hollow alone against Hollow with the pierce fraction folded in
        /// (<see cref="ChoosePierceShape"/>). The override carries the pierce fold only when Ignore wins.
        /// </summary>
        public static double? DecideSpellInnate(bool pveTarget, ResistanceType originalType, double inversionChance,
            double hollowFraction, double pierceMagnitude, Func<double> roll, Func<ResistanceType, double> innate,
            out PierceShape pierceShape)
        {
            pierceShape = PierceShape.None;

            if (originalType != ResistanceType.Pierce || pierceMagnitude <= 0.0)
                return ArchmageResistanceMath.Decide(pveTarget, originalType, true, inversionChance, hollowFraction, roll, innate);

            if (!pveTarget)
                return null;

            var original = innate(originalType);

            // immunity is respected: no roll, no override, and neither Pierce shape
            if (original <= 0.0)
                return null;

            var rolled = inversionChance > 0.0 ? roll() : 1.0;

            var inverted = ArchmageResistanceMath.InversionLands(inversionChance, rolled)
                ? ArchmageResistanceMath.WeakestInnate(innate)
                : original;

            var without = ArchmageResistanceMath.HollowInnate(inverted, hollowFraction);
            var with = ArchmageResistanceMath.HollowInnate(inverted, CombineIgnoreFractions(hollowFraction, pierceMagnitude));

            pierceShape = ChoosePierceShape(pierceMagnitude, without, with);

            var value = pierceShape == PierceShape.Ignore ? with : without;

            return value == original ? (double?)null : value;
        }
    }
}
