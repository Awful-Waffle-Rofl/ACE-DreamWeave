using System;

using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities
{
    /// <summary>
    /// Pure rules for Pinning Shot's defensive rider: a creature PINNED by an archer deals less damage to every
    /// Player and every CombatPet while the pin holds. The reduction is a flat base (30%, the same at every
    /// Pinning Shot rank - rank buys only the pin chance) that Assess Creature multiplies, the amount the
    /// multiply adds capped by its own tunable.
    ///
    /// THE READ SIDE IS FOUR SITES AND THEY MUST STAY IDENTICAL. A creature carrying a live pin reduction
    /// deals reduced HEALTH damage to every Player and every CombatPet through each of the four places a
    /// monster's damage reaches a victim:
    ///   1. physical (melee and missile): DamageEvent.CalculateDamage, after DoCalculateDamage;
    ///   2. spell projectiles: SpellProjectile.DamageTarget, health branch, beside the Soul Tether pet line;
    ///   3. Harm: WorldObject_Magic.HandleCastSpell_Boost, before the cloak damage proc;
    ///   4. Drain Health: WorldObject_Magic.HandleCastSpell_Transfer, before the cloak damage proc.
    /// Every site gates on <see cref="AppliesTo"/>, reads the SAME accessor
    /// (<see cref="Creature.GetPinOutgoingDamageMod"/>) on the attacker, applies it ONCE per hit before the
    /// defender's pre-write mitigation and before any cloakProcPercent capture, and adds no rounding of its own.
    /// PinReductionWiringSourceScanTests pins all four.
    ///
    /// Out of scope: damage-over-time ticks and the monster-effect riders. The reduction runs through the
    /// post-pin movement slow (pin end + class_ability_pinningshot_slow_seconds), or ends with the pin when the
    /// slow was not applied.
    /// </summary>
    public static class PinReductionMath
    {
        /// <summary>
        /// The largest reduction the rider can ever grant. A runaway tunable cannot reach 100% (a monster that
        /// deals no damage at all is a different mechanic), and cannot go negative into a damage boost.
        /// </summary>
        public const double MaxReduction = 0.9;

        /// <summary>
        /// The fraction of outgoing damage a pinned creature loses: the flat <paramref name="baseReduction"/>
        /// plus the Assess Creature affinity term, where the affinity term is the amount the multiplier ADDS to
        /// the base, CAPPED by <paramref name="affinityCap"/>. Pinning Shot's proc-chance shape
        /// (PinningShotAbility.Chance) applied to a magnitude. Pure for testability.
        ///
        /// <paramref name="affinityMultiplier"/> is Player.GetClassAbilityAffinityMultiplier(AssessCreature): a
        /// factor >= 1.0 that is exactly 1.0 at zero effective skill. Floored at 1.0 here as well, so a caller
        /// that hands over 0.0 degrades to the flat base rather than multiplying it away. A cap of 0 or less
        /// means uncapped. Returns 0 for a non-positive base, and never more than <see cref="MaxReduction"/>.
        /// </summary>
        public static double Magnitude(double baseReduction, double affinityMultiplier, double affinityCap)
        {
            if (baseReduction <= 0.0)
                return 0.0;

            var added = baseReduction * Math.Max(1.0, affinityMultiplier) - baseReduction;

            if (affinityCap > 0.0)
                added = Math.Min(added, affinityCap);

            return Math.Min(MaxReduction, Math.Max(0.0, baseReduction + added));
        }

        /// <summary>The damage multiplier a reduction leaves: 1 - reduction, never below 1 - MaxReduction.</summary>
        public static float ToMultiplier(double reduction)
        {
            if (reduction <= 0.0)
                return 1.0f;

            return (float)(1.0 - Math.Min(MaxReduction, reduction));
        }

        /// <summary>
        /// THE SHARED GATE of all four read sites: the attacker is a creature that is not a Player, and the
        /// defender is a Player or a CombatPet, and they are not the same creature. Anything else (a monster
        /// hitting another monster, a pet or player attacking, a self hit) is untouched.
        /// </summary>
        public static bool AppliesTo(Creature attacker, Creature defender)
        {
            if (attacker == null || defender == null || ReferenceEquals(attacker, defender))
                return false;

            if (attacker is Player)
                return false;

            return defender is Player || defender is CombatPet;
        }

        /// <summary>
        /// Whether a creature can CARRY the reduction. Players, pets and game pieces never do
        /// (ElementalProficiencyMath.IsEligibleTarget): it is a PvE debuff on a monster.
        /// </summary>
        public static bool IsEligibleVictim(Creature target) => ElementalProficiencyMath.IsEligibleTarget(target);
    }
}
