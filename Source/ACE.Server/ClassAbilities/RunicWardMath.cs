using System;

namespace ACE.Server.ClassAbilities
{
    /// <summary>
    /// Pure arithmetic for the Spellsword T2 entry Runic Ward: each landed weapon hit inscribes a ward worth
    /// 3/6/9% of that hit's damage by rank, the pool is capped at 10/20/30% of the Spellsword's maximum
    /// health by rank, it absorbs incoming damage, and it lapses 12 seconds after the last weapon hit. It
    /// drains ONLY by absorbing: the war-spell spend (and its Spend helper) was removed 2026-10-24.
    ///
    /// Kept as a pure static for the same reason <see cref="SanguineWardMath"/> is: Player's static
    /// initializer cannot run under the unit test host, so anything left inline on Player is untestable.
    ///
    /// TWO DIFFERENCES FROM SANGUINE WARD, BOTH DELIBERATE, because the two pools look alike and a reader
    /// who assumes they are the same shape will get each one wrong:
    ///  1. IT ACCUMULATES. <see cref="Inscribe"/> ADDS to whatever is still standing (up to the cap), where
    ///     SanguineWardMath.Grant discards it. Runic Ward is built by many small hits rather than granted
    ///     whole by one cast, so refreshing instead of adding would delete the ramp that is the ability.
    ///  2. IT IS CAPPED, at a per-rank fraction of maximum health, because an accumulating pool with no
    ///     ceiling is unbounded temporary HP. Sanguine Ward needs no cap: one cast's worth is its own ceiling.
    ///
    /// Like Sanguine Ward it is TEMPORARY HP, NOT DAMAGE REDUCTION, and must not be "improved" into DR
    /// later: damage reduction is a shared additive axis (POWER-LEDGER lists Mana Barrier and Battle
    /// Hardened on it) that would need a cross-class ceiling check, while an absorb pool touches neither the
    /// mitigation nor the avoidance budget. The ward SHIELD (owner ruling 2026-10-24) is the one exception
    /// and it lives outside this file: Player.GetWardShieldLevel feeds Creature.GetShieldMod a flat shield
    /// armor level while the pool is up.
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
        /// (3/6/9% at ranks 1-3), multiplied by the Item Tinkering affinity factor. 0 for rank 0 /
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
        /// The pool's ceiling as a fraction of maximum health at a given rank: the r1/r2/r3 tunables
        /// (0.10 / 0.20 / 0.30). Ranks above 3 clamp to the rank-3 value; 0 for rank &lt;= 0 (no ward can be
        /// held without the ability). The Item Tinkering affinity never touches this - it rides the per-hit
        /// gain only.
        /// </summary>
        public static double CapFraction(int rank, double capR1, double capR2, double capR3)
        {
            if (rank <= 0)
                return 0.0;

            if (rank == 1)
                return Math.Max(0.0, capR1);

            if (rank == 2)
                return Math.Max(0.0, capR2);

            return Math.Max(0.0, capR3);
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
    }
}
