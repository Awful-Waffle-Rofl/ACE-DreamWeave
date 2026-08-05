using System;

using ACE.Server.WorldObjects;

namespace ACE.Server.WeaponMods
{
    /// <summary>
    /// The pure arithmetic behind every Tier B effect, plus the ONE accessor that reads a modifier off a
    /// weapon. Deliberately free of Player, Session and database so the conditions and the rounding can be
    /// tested directly - the hooks in Player_WeaponMods.cs are thin wrappers over the functions here.
    ///
    /// THE WEAPON-ONLY RULE (HARD). <see cref="ReadWeaponOnly"/> reads exactly ONE item and never enumerates
    /// anything. Do not reach for Creature.GetEquippedModValue or Creature.GetEquippedModPotencySum next door:
    /// those SUM a mod's stored value across every equipped item, which is right for the armor system (mods
    /// land on up to a dozen pieces and stack additively) and wrong here by a factor of however many items a
    /// player is wearing. A weapon mod lives on ONE item - the weapon - and a summing read would let a player
    /// stack Life Leech off a helmet.
    ///
    /// The name says "weapon only" for that reason. If a future hook needs a value, it calls this with the
    /// specific item that carries it.
    /// </summary>
    public static class WeaponModCombat
    {
        // ---------------- the weapon-only accessor ----------------

        /// <summary>
        /// A Tier B modifier's applied magnitude on ONE item, as a fraction, or 0 when the item does not carry
        /// it. Reads the live weapon_mods_enabled gate - the system's single master switch - so every caller is
        /// inert while it is off even on a weapon that already carries a record from when it was on.
        ///
        /// See the class remarks for why this takes a single item rather than a creature.
        /// </summary>
        public static double ReadWeaponOnly(WorldObject weapon, WeaponModId modId) =>
            ReadWeaponOnly(weapon, modId, WeaponModRegistry.Enabled());

        /// <summary>
        /// <see cref="ReadWeaponOnly(WorldObject, WeaponModId)"/> at an EXPLICIT gate state. Pure apart from
        /// the item read, so both gate states can be exercised without touching PropertyManager.
        /// </summary>
        public static double ReadWeaponOnly(WorldObject weapon, WeaponModId modId, bool enabled)
        {
            if (!enabled || weapon == null)
                return 0.0;

            if (!WeaponModRegistry.TryGet(modId, out var definition) || definition.Tier != WeaponModTier.B)
                return 0.0;

            var magnitude = weapon.GetProperty(definition.Record);

            if (magnitude == null || double.IsNaN(magnitude.Value) || magnitude.Value <= 0.0)
                return 0.0;

            return magnitude.Value;
        }

        // ---------------- conditions ----------------

        /// <summary>
        /// TRUE when the target is at FULL health, which is Ambush's whole condition - "the first strike"
        /// against a target is exactly the strike that finds it undamaged, so no per-target state is needed to
        /// express it.
        ///
        /// Shaped after ExecutionerAbility.IsExecuteRange, the nearest existing idiom, including its treatment
        /// of a target with no maximum health: never in range, rather than dividing by zero or defaulting to
        /// true. Current above maximum (a buffed vital mid-recalculation) still reads as full.
        /// </summary>
        public static bool IsFullHealth(Creature target)
        {
            var max = target?.Health.MaxValue ?? 0;

            if (max == 0)
                return false;

            return target.Health.Current >= max;
        }

        /// <summary>
        /// Overload's roll: TRUE when this cast is free. <paramref name="chance"/> is the modifier's magnitude
        /// read as a PROBABILITY (0.20 = 20% of casts cost nothing), compared against a caller-supplied uniform
        /// draw in [0, 1). Pure for testability - the live call site draws via ThreadSafeRandom.
        ///
        /// A chance of 0 (unequipped, or the gate off) never fires, and a chance of 1 or more always does.
        /// </summary>
        public static bool RollsFree(double chance, double roll)
        {
            if (double.IsNaN(chance) || chance <= 0.0 || double.IsNaN(roll))
                return false;

            return roll < chance;
        }

        // ---------------- magnitudes ----------------

        /// <summary>
        /// A conditional-damage modifier as a damage multiplier: 1.0 when it is absent, 1 + fraction when it is
        /// held. Kept as a named function rather than an inline "1 + x" so the NaN and negative cases have one
        /// answer instead of one per call site.
        /// </summary>
        public static double DamageMultiplier(double fraction)
        {
            if (double.IsNaN(fraction) || fraction <= 0.0)
                return 1.0;

            return 1.0 + fraction;
        }

        /// <summary>
        /// Second Wind's payout: whole points of a vital to restore, as a fraction of its MAXIMUM. Rounded half
        /// up and floored at 0. A max of 0 (a vital that has not been initialised) restores nothing rather than
        /// throwing.
        /// </summary>
        public static int RestoreFromMax(uint maxValue, double fraction)
        {
            if (maxValue == 0 || double.IsNaN(fraction) || fraction <= 0.0)
                return 0;

            var exact = maxValue * fraction;

            var restored = (int)Math.Round(exact, MidpointRounding.AwayFromZero);

            return Math.Max(0, restored);
        }

        /// <summary>
        /// One hit's leech, in whole points, accrued against a carried remainder.
        ///
        /// WHY ACCRUAL RATHER THAN A PER-HIT ROUND. A leech is a small percentage of one hit: at the 4% maximum
        /// a 20-damage hit is worth 0.8 of a point, and a low roll on a weak hit is worth a tenth of one. Round
        /// each hit independently and every one of those becomes zero, so the modifier is permanently dead on
        /// exactly the characters who would notice it most. The sub-point remainder is carried instead and pays
        /// out once it adds up.
        ///
        /// This is deliberately the SAME algorithm as
        /// <see cref="ACE.Server.ClassAbilities.Abilities.BloodlustAbility.AccrueHeal"/>, which solved the
        /// identical problem for the Bloodlust equipment mod, and the tests assert the two agree over a sweep
        /// so they cannot drift. It is reimplemented rather than called so ACE.Server.WeaponMods keeps no
        /// dependency on the class-ability catalog.
        ///
        /// Rounding the running total half up leaves the carry in [-0.5, 0.5], which is what makes the scheme
        /// DRIFT-FREE: after any number of hits the total leeched is within half a point of the exact total, so
        /// accrual changes WHEN the value arrives and never HOW MUCH.
        /// </summary>
        public static int AccrueVital(double carry, double exactAmount, out double newCarry)
        {
            if (double.IsNaN(carry) || double.IsInfinity(carry))
                carry = 0.0;

            newCarry = carry;

            if (double.IsNaN(exactAmount) || double.IsInfinity(exactAmount) || exactAmount <= 0.0)
                return 0;

            var total = carry + exactAmount;
            var amount = (int)Math.Round(total, MidpointRounding.AwayFromZero);

            if (amount <= 0)
            {
                newCarry = total;
                return 0;
            }

            newCarry = total - amount;
            return amount;
        }

        /// <summary>
        /// The exact (fractional) leech one hit is worth: damage x fraction, never negative. Split from
        /// <see cref="AccrueVital"/> so the two halves - what a hit earns, and when it pays out - can be
        /// asserted separately.
        /// </summary>
        public static double LeechAmount(double damage, double fraction)
        {
            if (double.IsNaN(damage) || double.IsNaN(fraction) || damage <= 0.0 || fraction <= 0.0)
                return 0.0;

            return damage * fraction;
        }
    }
}
