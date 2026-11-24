using System;

namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// The champion HP budget (Slay the Champion / kill_boss): a named boss's maximum health sized from the
    /// POWER of the players present, with no per-event setting needed. Pure maths, no PropertyManager reads -
    /// the dials come in as arguments (BossDef.EffectiveBudget* resolves JSON vs live override).
    ///
    ///   health = clamp(healthPerPower * power ^ exponent, floor, cap)
    ///
    /// power is the audience power sum, sum((min(level, 275) / 275)^2) (WorldEventRosterSelector), so with
    /// the level cap of 275 nearly every player counts as 1.0. Exponent 1 ships: measured per-player DPS shows
    /// no trend with group size, so health is linear in power.
    /// </summary>
    public static class WorldEventBossBudget
    {
        /// <summary>
        /// The boss's maximum health for <paramref name="power"/> (D6 - pure). Never throws.
        ///
        /// A non-finite or non-positive perPower (the "budget off" state) yields the floor; a non-finite or
        /// non-positive exponent reads as 1.0; a negative or non-finite power reads as 0 (so the floor); a
        /// cap at or below the floor means "no headroom", i.e. exactly the floor. The result is bounded by
        /// int.MaxValue - a ratchet raise is handed to Creature.UpdateVitalDelta as an int (BossDef.BaseHealth).
        /// </summary>
        public static uint ResolveHealth(double perPower, double exponent, double power, uint floor, uint cap)
        {
            var ceiling = cap > int.MaxValue ? (uint)int.MaxValue : cap;

            if (floor > int.MaxValue)
                floor = int.MaxValue;

            if (ceiling < floor)
                ceiling = floor;

            if (!double.IsFinite(perPower) || perPower <= 0)
                return floor;

            if (!double.IsFinite(exponent) || exponent <= 0)
                exponent = 1.0;

            if (!double.IsFinite(power) || power < 0)
                power = 0;

            var health = perPower * Math.Pow(power, exponent);

            if (!double.IsFinite(health))
                return ceiling;

            if (health >= ceiling)
                return ceiling;

            if (health <= floor)
                return floor;

            return (uint)Math.Round(health, MidpointRounding.AwayFromZero);
        }

        /// <summary>
        /// <see cref="ResolveHealth"/> expressed as a multiplier on the floor (D6 - pure), which is the form
        /// WorldEvent.BossHealthMult and the spawner's rebase already speak: the boss is re-based to
        /// <paramref name="floor"/> and then multiplied by health / floor. 1.0 for a floor of 0 (a boss that
        /// cannot be sized from a rebase), never below 1.0.
        /// </summary>
        public static double ResolveMult(double perPower, double exponent, double power, uint floor, uint cap)
        {
            if (floor == 0)
                return 1.0;

            var health = ResolveHealth(perPower, exponent, power, floor, cap);

            var mult = (double)health / floor;

            return mult < 1.0 ? 1.0 : mult;
        }

        /// <summary>
        /// The health a late-arrival raise should ADD to a boss that is already standing (D6 - pure): the
        /// max-health increase scaled by the health fraction the boss currently has, so the health BAR keeps
        /// its percentage. A boss at 50% of 20M raised to 22M ends at 11M, not 12M. Today's rule added the
        /// whole delta, which handed a half-dead boss a free 10-point jump in bar percentage.
        ///
        /// 0 when the max did not rise or the boss has no health to scale (maxBefore 0 is treated as full,
        /// the safe direction for a malformed read). A current above the old max reads as full.
        /// </summary>
        public static uint KeepPercentageDelta(uint currentHealth, uint maxBefore, uint maxAfter)
        {
            if (maxAfter <= maxBefore)
                return 0;

            var delta = maxAfter - maxBefore;

            if (maxBefore == 0 || currentHealth >= maxBefore)
                return delta;

            var scaled = Math.Round((double)delta * currentHealth / maxBefore, MidpointRounding.AwayFromZero);

            return scaled >= delta ? delta : (uint)scaled;
        }
    }
}
