namespace ACE.Server.ClassAbilities
{
    /// <summary>
    /// What one qualifying life cast does to the Blood Charge pool. The two values are EXHAUSTIVE AND
    /// MUTUALLY EXCLUSIVE by construction, which is the entire point of the type: a cast either builds the
    /// pool or spends it, and there is no representable state in which it does both.
    ///
    /// User ruling, live test 2026-08-03: "It is not clear when hecatomb is exsanguinating vs gaining a
    /// blood charge. It seems random ... exsang when full charges and on cast of heca/raven - otherwise
    /// accrue a blood charge. Cannot accrue a blood charge in the same spell attack as exsang."
    ///
    /// Before this existed the two answers were independent - a bool for the burst, an unconditional grant
    /// afterwards - so a single Hecatomb could burst the pool and then immediately put a charge back into
    /// it. Making them one value is what makes the exclusion structural rather than a rule someone has to
    /// remember at each call site.
    /// </summary>
    public enum BloodChargeCastOutcome
    {
        /// <summary>
        /// This cast BUILDS the pool: no burst, and the cast's own charge is granted when it lands.
        /// </summary>
        Accrue,

        /// <summary>
        /// This cast SPENDS the whole pool: the burst multiplier applies and NO charge is granted, so the
        /// pool restarts from empty on the next cast.
        /// </summary>
        Burst,
    }

    /// <summary>
    /// Pure math for EXSANGUINATE (Blood Mage T3 game-changer): a Martyr's Hecatomb or Curse of Raven Fury
    /// cast at a FULL Blood Charge pool consumes the entire pool and applies it at a multiple of its normal
    /// per-charge value, and against a target under Weakened Blood additionally ignores part of that
    /// target's HealthDrain resistance for the strike.
    ///
    /// Kept as a static so the firing decision, the burst and the resistance-ignore arithmetic are
    /// unit-testable without a live Player (Player's static initializer cannot run under the test host).
    /// The stateful half - reading the pool and clearing it - lives in Player_ClassAbilityBuffs.cs.
    /// </summary>
    public static class ExsanguinateMath
    {
        /// <summary>
        /// THE FIRING DECISION, and the only place it is made. Returns
        /// <see cref="BloodChargeCastOutcome.Burst"/> only when all of the following hold, and
        /// <see cref="BloodChargeCastOutcome.Accrue"/> in every other case:
        ///
        ///  - <paramref name="castEligible"/>: the cast is a Martyr's Hecatomb or Curse of Raven Fury. Harm
        ///    and Drain build the pool but can never spend it, and their call site passes false as a literal.
        ///  - <paramref name="hasExsanguinate"/>: the player owns the ability at all.
        ///  - <paramref name="stackCap"/> is positive: without Sanguine Reserve there is no pool, so there is
        ///    nothing to spend and Exsanguinate is inert - the T3-builds-on-T1 pattern.
        ///  - the pool is FULL: <paramref name="stacks"/> has reached the rank's cap (3/4/5 by Sanguine
        ///    Reserve rank).
        ///
        /// WHY FULL, AND WHY NOTHING ELSE. The predecessor fired on ANY non-empty pool and gated repeats on a
        /// 10-second internal cooldown. That made the outcome of an identical cast depend on wall-clock time
        /// since a previous burst, which the player has no readout for, so Hecatomb appeared to burst at
        /// random and for a random magnitude (a 1-charge burst is +21%, a 5-charge burst +105%). A full pool
        /// is a gate the player can SEE - they counted every charge into it - and it self-limits to one burst
        /// per 3-5 casts without any clock at all. There is deliberately no cooldown term here: any
        /// time-based gate reintroduces exactly the invisible condition this replaced.
        ///
        /// Stacks above the cap are treated as full rather than rejected, matching how the rest of the kit
        /// clamps an out-of-range value instead of failing on it.
        /// </summary>
        public static BloodChargeCastOutcome Resolve(bool castEligible, bool hasExsanguinate, int stacks, int stackCap)
        {
            if (!castEligible || !hasExsanguinate)
                return BloodChargeCastOutcome.Accrue;

            if (stackCap <= 0 || stacks < stackCap)
                return BloodChargeCastOutcome.Accrue;

            return BloodChargeCastOutcome.Burst;
        }

        /// <summary>
        /// TRUE when a cast with this outcome grants its own Blood Charge on landing. This is the second half
        /// of the mutual exclusion and it is deliberately derived from the SAME value that decided the burst,
        /// rather than being a second independent test: an exsanguinating cast never accrues.
        /// </summary>
        public static bool GrantsCharge(BloodChargeCastOutcome outcome) => outcome == BloodChargeCastOutcome.Accrue;

        /// <summary>
        /// TRUE when a cast with this outcome spends the pool. The inverse of <see cref="GrantsCharge"/> -
        /// stated as its own function so both damage sites read the decision rather than comparing enum
        /// values inline.
        /// </summary>
        public static bool Bursts(BloodChargeCastOutcome outcome) => outcome == BloodChargeCastOutcome.Burst;

        /// <summary>
        /// The burst multiplier: the same 1 + stacks * perStack ramp Sanguine Reserve applies, with the
        /// per-charge value scaled by <paramref name="burstMultiplier"/>. At the design defaults (5 charges,
        /// +7% each, 3x) this is 1 + 5 * 0.07 * 3 = 2.05, i.e. +105% against Sanguine Reserve's +35%.
        ///
        /// This deliberately reads the SAME per-charge value Sanguine Reserve uses rather than carrying its
        /// own: BLOOD-MAGE-DESIGN sec 3 makes the containment lever the burst multiplier (3.0 -> 2.5), not
        /// the per-charge value, and that only holds if the two are the same number.
        ///
        /// A burst multiplier at or below 1.0 degrades to the plain ramp rather than reducing damage, so a
        /// mis-set tunable can never make Exsanguinate worse than not spending the pool.
        ///
        /// <paramref name="gearBurstMultiplier"/> is the SANGUINATE equipment mod
        /// (EquipmentModId.Sanguinate), added to the burst multiplier on the SAME AXIS as the tunable -
        /// burstMultiplier + gear - rather than multiplied into the result (DESIGN.md 3.3). It defaults to
        /// 0 and adding 0.0 is exact, so an unmodded burst is bit-identical.
        ///
        /// THE DEGRADE FLOOR IS APPLIED TO THE SUM, NOT BEFORE IT. That ordering is what keeps this on one
        /// axis: floor-then-add would let a mis-set tunable of 0.5 be rescued to 1.0 and THEN take the gear
        /// term on top, which is a different (and larger) answer than the axis rule states. At the shipped
        /// tunable of 3.0 the two orderings agree exactly; they differ only for a tunable already below 1.0,
        /// where the ability itself is mis-configured.
        ///
        /// <paramref name="gearPerStack"/> is a DIFFERENT MOD ON A DIFFERENT ABILITY - BLOOD CHARGE
        /// (EquipmentModId.BloodCharge), machinery on Sanguine Reserve - and it is forwarded through here
        /// because this method IS Sanguine Reserve's ramp: it delegates to
        /// <see cref="BloodChargeMath.DamageMultiplier"/>, so without the forward a bursting cast would
        /// silently drop the mod at exactly the moment the pool pays off most.
        ///
        /// IT IS SCALED BY THE BURST, THE SAME WAY THE ABILITY'S OWN PER-CHARGE VALUE IS. The class comment
        /// above makes "the burst reads the SAME per-charge value Sanguine Reserve uses" a load-bearing
        /// property; a gear term that raises that per-charge value and is then NOT multiplied would break it
        /// and would leave Blood Charge worth a shrinking share of the strike it is normalized against.
        /// If that is ever retuned, the lever is dropping the `* burstMultiplier` on this one argument.
        /// </summary>
        public static float BurstMultiplier(int stacks, double perStack, double burstMultiplier, double gearBurstMultiplier = 0.0, double gearPerStack = 0.0)
        {
            burstMultiplier += gearBurstMultiplier;

            if (burstMultiplier < 1.0)
                burstMultiplier = 1.0;

            return BloodChargeMath.DamageMultiplier(stacks, perStack * burstMultiplier, gearPerStack * burstMultiplier);
        }

        /// <summary>
        /// The damage multiplier that represents "ignore <paramref name="ignoreFraction"/> of the target's
        /// HealthDrain resistance", expressed as a ratio the caller multiplies into the damage so the
        /// resistance query itself is left alone.
        ///
        /// <paramref name="resistanceOnly"/> is the target's RESISTANCE-side product only - the
        /// ResistHealthDrain property, natural resistance and the Life Resist rating - and deliberately NOT
        /// the vulnerability term. Folding the vulnerability in would invert the effect: under Weakened
        /// Blood the combined modifier is 2.0+ and "moving it toward 1.0" would then CUT damage, which is
        /// the opposite of what the ability says. See Creature.GetHealthDrainResistanceOnly.
        ///
        /// Model: the ignore moves the resistance a fraction of the way toward 1.0 (no resistance), so
        /// r' = r + f * (1 - r), and the returned multiplier is r' / r. Returns 1.0 - a no-op - when the
        /// target is not actually resistant (r >= 1.0), when r is non-positive, or when the fraction is
        /// non-positive. Full ignore (f = 1) returns exactly 1 / r.
        /// </summary>
        public static float ResistanceIgnoreMultiplier(double resistanceOnly, double ignoreFraction)
        {
            if (resistanceOnly <= 0.0 || resistanceOnly >= 1.0 || ignoreFraction <= 0.0)
                return 1.0f;

            if (ignoreFraction > 1.0)
                ignoreFraction = 1.0;

            var adjusted = resistanceOnly + ignoreFraction * (1.0 - resistanceOnly);

            return (float)(adjusted / resistanceOnly);
        }
    }
}
