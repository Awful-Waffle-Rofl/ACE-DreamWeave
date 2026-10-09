using System;

namespace ACE.Server.ClassAbilities
{
    /// <summary>
    /// Pure arithmetic for the Spellsword T2 entry Runic Ward: each landed weapon hit inscribes a ward worth
    /// 3/5/7% of that hit's damage by rank, the pool is capped at 15% of the Spellsword's maximum health, it
    /// absorbs incoming damage, it lapses 12 seconds after the last weapon hit, and landing a war spell
    /// SPENDS the whole remaining pool into that spell's damage.
    ///
    /// Kept as a pure static for the same reason <see cref="SanguineWardMath"/> is: Player's static
    /// initializer cannot run under the unit test host, so anything left inline on Player is untestable.
    ///
    /// THREE DIFFERENCES FROM SANGUINE WARD, ALL DELIBERATE, because the two pools look alike and a reader
    /// who assumes they are the same shape will get each one wrong:
    ///  1. IT ACCUMULATES. <see cref="Inscribe"/> ADDS to whatever is still standing (up to the cap), where
    ///     SanguineWardMath.Grant discards it. Runic Ward is built by many small hits rather than granted
    ///     whole by one cast, so refreshing instead of adding would delete the ramp that is the ability.
    ///  2. IT IS CAPPED, at a fraction of maximum health, because an accumulating pool with no ceiling is
    ///     unbounded temporary HP. Sanguine Ward needs no cap: one cast's worth is its own ceiling.
    ///  3. IT CAN BE SPENT ON PURPOSE (<see cref="Spend"/>), which is the decision the entry exists to pose.
    ///     Sanguine Ward only ever expires unused.
    ///
    /// Like Sanguine Ward it is TEMPORARY HP, NOT DAMAGE REDUCTION, and must not be "improved" into DR
    /// later: damage reduction is a shared additive axis (POWER-LEDGER lists Mana Barrier and Battle
    /// Hardened on it) that would need a cross-class ceiling check, while an absorb pool touches neither the
    /// mitigation nor the avoidance budget.
    /// </summary>
    public static class RunicWardMath
    {
        /// <summary>
        /// A live ward: how much absorb is left, and the unix time it lapses at. Immutable - every
        /// transition returns a new value, so a caller assigns rather than mutates.
        /// </summary>
        public readonly struct WardState
        {
            public uint Amount { get; init; }
            public double ExpireTime { get; init; }
        }

        /// <summary>The result of running one incoming hit through the ward.</summary>
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
        /// The fraction of a landed weapon hit's damage that goes into the ward: base + (rank-1)*step
        /// (3/5/7% at ranks 1-3), multiplied by the Item Tinkering affinity factor. 0 for rank 0 /
        /// unlearned, so the caller can apply it unconditionally.
        ///
        /// <paramref name="affinityMultiplier"/> is <see cref="ClassAbilityAffinity.Multiplier"/>'s return
        /// value, which is never below 1.0; a value below 1.0 (only reachable by a caller passing something
        /// else) is treated as 1.0 rather than shrinking the ward.
        /// </summary>
        public static double GainFraction(int rank, double gainBase, double gainStep, double affinityMultiplier)
        {
            if (rank <= 0)
                return 0.0;

            var rankFraction = Math.Max(0.0, gainBase + (rank - 1) * gainStep);

            return rankFraction * (affinityMultiplier < 1.0 ? 1.0 : affinityMultiplier);
        }

        /// <summary>
        /// The ward's hard ceiling in points: <paramref name="capFraction"/> of maximum health, rounded.
        /// A non-positive fraction or a zero maximum means no ward can be held at all.
        /// </summary>
        public static uint Cap(uint maxHealth, double capFraction)
        {
            if (maxHealth == 0 || capFraction <= 0.0)
                return 0;

            var scaled = Math.Round(maxHealth * capFraction, MidpointRounding.AwayFromZero);

            return scaled >= uint.MaxValue ? uint.MaxValue : (uint)scaled;
        }

        /// <summary>
        /// Adds one landed weapon hit's contribution to the ward and refreshes its window.
        ///
        /// ACCUMULATES rather than refreshing the amount - see the type doc. A ward whose window had already
        /// lapsed before this hit starts again from zero rather than reviving a stale pool, so the 12 second
        /// window is a real deadline and not merely a display timer.
        ///
        /// The pool is clamped to <paramref name="cap"/> AFTER the addition, so a hit that would overflow
        /// tops the ward off instead of being wasted, and a ward already at the cap still refreshes its
        /// window (a Spellsword who keeps swinging keeps the ward up).
        /// </summary>
        public static WardState Inscribe(in WardState current, uint hitDamage, double gainFraction, uint cap, double now, double durationSeconds)
        {
            if (cap == 0 || durationSeconds <= 0.0)
                return default;

            var standing = IsExpired(current, now) ? 0u : current.Amount;

            var added = 0u;

            if (hitDamage > 0 && gainFraction > 0.0)
            {
                var scaled = Math.Round(hitDamage * gainFraction, MidpointRounding.AwayFromZero);
                added = scaled >= uint.MaxValue ? uint.MaxValue : (uint)scaled;
            }

            var total = standing > uint.MaxValue - added ? uint.MaxValue : standing + added;

            if (total > cap)
                total = cap;

            if (total == 0)
                return default;

            return new WardState { Amount = total, ExpireTime = now + durationSeconds };
        }

        /// <summary>TRUE once the ward's window has lapsed. An empty ward is treated as expired.</summary>
        public static bool IsExpired(in WardState state, double now) => state.Amount == 0 || now > state.ExpireTime;

        /// <summary>
        /// Runs one incoming hit through the ward: the ward eats up to what it holds, the rest reaches
        /// Health, and the ward is drained by exactly what it ate.
        ///
        /// ABSORB, NOT HEAL - this only ever reduces the hit, exactly like Sanguine Ward. Nothing here
        /// returns health to the player, so a warded Spellsword is never net-positive on the pool.
        ///
        /// A LETHAL BLOW A STANDING WARD CAN COVER IS NOT LETHAL, and that is a property of running before
        /// the health write rather than of anything in here: the caller writes DamageAfterWard to the vital
        /// and only then checks for death, so leaving less damage than the defender has Health has saved
        /// them. An absorb applied after the write (or to a figure already clamped at current health) cannot
        /// do that - see ManaBarrierAbility for the incident that established this.
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

        /// <summary>
        /// What a landed war spell cashes the ward in for: the WHOLE remaining pool, or 0 if the window has
        /// lapsed. The caller clears the ward and adds this to the spell's damage.
        ///
        /// All or nothing on purpose - a partial spend would let a Spellsword keep a defensive pool AND
        /// collect the offensive payoff, which is precisely the choice the entry exists to force.
        /// </summary>
        public static uint Spend(in WardState state, double now) => IsExpired(state, now) ? 0u : state.Amount;
    }
}
