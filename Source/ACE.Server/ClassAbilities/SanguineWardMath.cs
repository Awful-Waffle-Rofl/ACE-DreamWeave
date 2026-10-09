using System;

namespace ACE.Server.ClassAbilities
{
    /// <summary>
    /// Pure arithmetic for the Blood Mage T3 entry Sanguine Ward (BLOOD-MAGE-DESIGN.md sec 3 / 4a-iv).
    ///
    /// Two halves, both keyed off ONE number - the health basis Martyr's Hecatomb / Curse of Raven Fury
    /// computes at the cast site (a percentage of the caster's CURRENT health):
    ///  - the basis is still spent in full for damage, but the caster only actually LOSES
    ///    <see cref="SelfCostFraction"/> of it (80/65/50% by rank);
    ///  - the health that is lost comes back as a damage-absorbing ward worth
    ///    <see cref="WardFraction"/> of the lost amount (50/75/100% by rank), for a fixed duration.
    ///
    /// THREE LOAD-BEARING PROPERTIES, each modelled explicitly here so they are unit-testable without a
    /// live Player (Player's static initializer cannot run under the test host, so anything left inline on
    /// Player is untested):
    ///  1. The ward REFRESHES, IT DOES NOT STACK. <see cref="Grant"/> discards whatever was left of the
    ///     previous ward instead of adding to it. This is what caps the mechanic at one cast's worth and
    ///     is the reason it cannot spiral across a Hecatomb chain.
    ///  2. It EXPIRES UNUSED. <see cref="Absorb"/> returns nothing once the window has lapsed and the
    ///     remainder is simply dropped - the ward is protection while under attack, never a refund.
    ///  3. It ABSORBS INCOMING DAMAGE, IT DOES NOT RESTORE HEALTH. <see cref="Absorb"/> only ever reduces
    ///     an incoming hit; no function here returns health to the caster. After a Hecatomb chain the
    ///     caster is still genuinely at low health, so burst thresholds and death risk are unchanged.
    ///     That is the whole reason the entry is safe under a DPS-parity target, where the health economy
    ///     IS the balance mechanism.
    ///
    /// WHY AN ABSORB AND NOT DAMAGE REDUCTION - do not "improve" this into DR. Damage reduction is a
    /// SHARED axis: POWER-LEDGER records Mana Barrier and Battle Hardened as both `damage-reduction` and
    /// ADDITIVE with each other, so a DR entry here would stack into a cross-class ceiling and need a
    /// ceiling check it does not currently have. Avoidance is separately pooled under
    /// class_ability_avoidance_cap and the ledger records that pool as already binding at its 50% cap, so
    /// a proc there would buy nothing. Temporary HP touches NEITHER budget - that, not flavour, is why
    /// the shape is an absorb.
    /// </summary>
    public static class SanguineWardMath
    {
        /// <summary>
        /// A live ward: how much absorb is left, and the unix time it lapses at. Immutable - every
        /// transition returns a new value, so the caller assigns rather than mutates and there is no way
        /// to accidentally accumulate one into another.
        /// </summary>
        public readonly struct WardState
        {
            public uint Amount { get; init; }
            public double ExpireTime { get; init; }
        }

        /// <summary>
        /// The result of running one incoming hit through the ward.
        /// </summary>
        public readonly struct AbsorbResult
        {
            /// <summary>How much of the hit the ward ate. Never more than the ward held or the hit dealt.</summary>
            public uint Absorbed { get; init; }

            /// <summary>What is left of the hit after the ward. This is what reaches Health.</summary>
            public uint DamageAfterWard { get; init; }

            /// <summary>The ward after the hit - drained by <see cref="Absorbed"/>, or empty once expired.</summary>
            public WardState Remaining { get; init; }
        }

        /// <summary>
        /// The fraction of the spell's health basis the caster ACTUALLY loses: 80/65/50% at ranks 1/2/3,
        /// from the class_ability_sanguine_ward_selfcost_rN tunables.
        ///
        /// Returns 1.0 (the full retail cost) for rank 0 / unlearned, so a caster without the ability is
        /// unaffected - the call site can apply this unconditionally. Rank is clamped rather than
        /// rejected, matching the rest of the class-ability family: a persisted rank above MaxRank
        /// behaves as max rank, never as unlearned.
        /// </summary>
        public static double SelfCostFraction(int rank, double costR1, double costR2, double costR3)
        {
            if (rank <= 0)
                return 1.0;

            var fraction = rank switch
            {
                1 => costR1,
                2 => costR2,
                _ => costR3,
            };

            return Math.Clamp(fraction, 0.0, 1.0);
        }

        /// <summary>
        /// The fraction of the health ACTUALLY LOST that comes back as ward: 50/75/100% at ranks 1/2/3,
        /// from the class_ability_sanguine_ward_absorb_rN tunables. 0 for rank 0 / unlearned.
        ///
        /// Deliberately NOT clamped to 1.0 at the top: the tunable is the authority, and a value above
        /// 100% would still only ward what was lost times that factor - it can never return health, so it
        /// cannot make the caster net-positive on the pool.
        ///
        /// <paramref name="gear"/> is the CLOTTING equipment mod (EquipmentModId.Clotting), added to the
        /// rank's fraction on the same additive axis (DESIGN.md 3.3). Defaults to 0, and adding 0.0 is
        /// exact, so an unmodded ward is bit-identical.
        ///
        /// ONLY THE ABSORB TAKES GEAR. <see cref="SelfCostFraction"/> deliberately takes NO gear term: the
        /// self-cost is the ability's own bargain, the same ruling that keeps Blood Price's health cost
        /// unmoddable (DESIGN.md 2.3). Gear buys a bigger ward, never a cheaper cast.
        /// </summary>
        public static double WardFraction(int rank, double absorbR1, double absorbR2, double absorbR3, double gear = 0.0)
        {
            if (rank <= 0)
                return 0.0;

            var fraction = (rank switch
            {
                1 => absorbR1,
                2 => absorbR2,
                _ => absorbR3,
            }) + gear;

            return Math.Max(0.0, fraction);
        }

        /// <summary>
        /// How much health the caster actually loses, given the spell's full health basis and the
        /// self-cost fraction for their rank. Whole points, rounded.
        /// </summary>
        public static uint SelfCost(uint healthBasis, double selfCostFraction)
        {
            if (healthBasis == 0 || selfCostFraction <= 0.0)
                return 0;

            if (selfCostFraction >= 1.0)
                return healthBasis;

            return (uint)Math.Round(healthBasis * selfCostFraction, MidpointRounding.AwayFromZero);
        }

        /// <summary>
        /// The ward granted by a cast, from the health actually lost and the rank's ward fraction.
        /// Whole points, rounded. Saturates rather than wrapping.
        /// </summary>
        public static uint WardAmount(uint healthLost, double wardFraction)
        {
            if (healthLost == 0 || wardFraction <= 0.0)
                return 0;

            var scaled = Math.Round(healthLost * wardFraction, MidpointRounding.AwayFromZero);

            return scaled >= uint.MaxValue ? uint.MaxValue : (uint)scaled;
        }

        /// <summary>
        /// Grants (or re-grants) the ward.
        ///
        /// REFRESH, NOT STACK - <paramref name="current"/> is deliberately read for nothing: whatever was
        /// left of the previous ward is DISCARDED, not added to the new one, and the timer restarts from
        /// <paramref name="now"/>. It is a parameter purely so the property is visible at the call site
        /// and directly assertable in a test. This is what caps Sanguine Ward at one cast's worth no
        /// matter how fast the caster chains Hecatomb, and it is the reason the entry cannot spiral.
        ///
        /// A cast that produces no ward (rank 0, nothing lost) clears any ward already up rather than
        /// leaving a stale one running - a recast is always the authority on the ward's state.
        /// </summary>
        public static WardState Grant(in WardState current, uint healthLost, double wardFraction, double now, double durationSeconds)
        {
            var amount = WardAmount(healthLost, wardFraction);

            if (amount == 0 || durationSeconds <= 0.0)
                return default;

            return new WardState { Amount = amount, ExpireTime = now + durationSeconds };
        }

        /// <summary>
        /// TRUE once the ward's window has lapsed. An empty ward is treated as expired.
        /// </summary>
        public static bool IsExpired(in WardState state, double now) => state.Amount == 0 || now > state.ExpireTime;

        /// <summary>
        /// Runs one incoming hit through the ward: the ward eats up to what it holds, the rest reaches
        /// Health, and the ward is drained by exactly what it ate. Several hits therefore chew through
        /// one ward partially, in order, until it is empty.
        ///
        /// EXPIRES UNUSED - past the window this absorbs nothing and returns an empty ward, so an unspent
        /// ward is simply gone. It is never converted back into health, healing, or a later absorb.
        ///
        /// Note what this does NOT do: it returns a REDUCED incoming damage figure, never a health gain.
        /// The caller subtracts the reduced amount from Health; there is no path here that adds to it.
        /// </summary>
        public static AbsorbResult Absorb(in WardState state, uint incomingDamage, double now)
        {
            if (IsExpired(state, now))
                return new AbsorbResult { Absorbed = 0, DamageAfterWard = incomingDamage, Remaining = default };

            if (incomingDamage == 0)
                return new AbsorbResult { Absorbed = 0, DamageAfterWard = 0, Remaining = state };

            var absorbed = Math.Min(state.Amount, incomingDamage);

            var remainingAmount = state.Amount - absorbed;

            var remaining = remainingAmount == 0
                ? default
                : new WardState { Amount = remainingAmount, ExpireTime = state.ExpireTime };

            return new AbsorbResult
            {
                Absorbed = absorbed,
                DamageAfterWard = incomingDamage - absorbed,
                Remaining = remaining,
            };
        }
    }
}
