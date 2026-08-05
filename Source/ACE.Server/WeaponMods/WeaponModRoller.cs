using System;
using System.Collections.Generic;

using log4net;

using ACE.Common;
using ACE.Entity.Enum;
using ACE.Server.Managers;

namespace ACE.Server.WeaponMods
{
    /// <summary>
    /// Every random draw this system makes. The resolution half of each draw is a pure function taking the roll
    /// as an argument, so the arithmetic is testable without an injectable RNG (the sibling system does not have
    /// one either).
    /// </summary>
    public static class WeaponModRoller
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private static bool loggedBadChances;

        // ---------------- special count ----------------

        /// <summary>
        /// The three special_chance tunables, sanitized. They are CUMULATIVE - P(at least one), P(at least two),
        /// P(at least three) - because that is how the odds are reasoned about, so they must decrease
        /// monotonically. A bad set is CLAMPED rather than thrown, so a typo in a live tunable cannot produce a
        /// negative probability band mid-craft; the first time it trips is logged once.
        /// </summary>
        public static (double One, double Two, double Three) SanitizeChances(double c1, double c2, double c3)
        {
            var one = WeaponModValue.Clamp01(c1);
            var two = WeaponModValue.Clamp01(c2);
            var three = WeaponModValue.Clamp01(c3);

            var bad = two > one || three > two || c1 != one || c2 != two || c3 != three;

            if (two > one)
                two = one;

            if (three > two)
                three = two;

            if (bad && !loggedBadChances)
            {
                loggedBadChances = true;
                log.Error($"WeaponModRoller: weapon_mod_special_chance_1/2/3 are not a monotonically decreasing set in [0, 1] ({c1}, {c2}, {c3}) - clamped to ({one}, {two}, {three})");
            }

            return (one, two, three);
        }

        /// <summary>
        /// How many specials a roll of <paramref name="roll"/> in [0, 1) buys, given the three cumulative odds.
        /// Pure. At the defaults (0.35, 0.10, 0.02) this is none 65%, one 25%, two 8%, three 2%.
        /// </summary>
        public static int ResolveSpecialCount(double roll, double c1, double c2, double c3)
        {
            var (one, two, three) = SanitizeChances(c1, c2, c3);

            if (double.IsNaN(roll))
                return 0;

            if (roll < three)
                return 3;

            if (roll < two)
                return 2;

            if (roll < one)
                return 1;

            return 0;
        }

        /// <summary>Rolls the special count against the live tunables.</summary>
        public static int RollSpecialCount()
        {
            var roll = ThreadSafeRandom.Next(0.0f, 1.0f);

            return ResolveSpecialCount(roll,
                PropertyManager.GetDouble("weapon_mod_special_chance_1").Item,
                PropertyManager.GetDouble("weapon_mod_special_chance_2").Item,
                PropertyManager.GetDouble("weapon_mod_special_chance_3").Item);
        }

        /// <summary>
        /// The HARD clamp on how many specials a weapon may end up holding: never more than
        /// <see cref="WeaponModRegistry.MaxSpecials"/>, and never more than the slots left after imbues. Applied
        /// on top of the odds table rather than being left implied by it, so raising
        /// weapon_mod_special_chance_3 can never produce a fourth.
        /// </summary>
        public static int ClampSpecialCount(int rolled, int reservedImbueSlots)
        {
            var free = WeaponModRegistry.TotalSlots - WeaponModTinkerSet.ClampReserved(reservedImbueSlots);
            var cap = Math.Min(WeaponModRegistry.MaxSpecials, free);

            return Math.Clamp(rolled, 0, Math.Max(0, cap));
        }

        // ---------------- special identity ----------------

        /// <summary>
        /// Draws <paramref name="count"/> DISTINCT specials from a weapon class's pool. A weapon never holds the
        /// same special twice, so anything in <paramref name="exclude"/> is off the table too. Returns fewer than
        /// asked only when the pool runs out, which cannot happen at a cap of 3 against pools of 8-10.
        /// </summary>
        public static List<WeaponModDefinition> RollDistinctSpecials(WeaponClass weaponClass, int count, ICollection<WeaponModId> exclude = null)
        {
            var rolled = new List<WeaponModDefinition>();

            if (count <= 0)
                return rolled;

            var taken = exclude == null ? new HashSet<WeaponModId>() : new HashSet<WeaponModId>(exclude);

            for (var i = 0; i < count; i++)
            {
                var next = RollSpecial(weaponClass, taken);

                if (next == null)
                    break;

                taken.Add(next.Id);
                rolled.Add(next);
            }

            return rolled;
        }

        /// <summary>
        /// One special drawn uniformly from a class pool, skipping anything already held. NULL when every entry
        /// in the pool is excluded.
        /// </summary>
        public static WeaponModDefinition RollSpecial(WeaponClass weaponClass, ICollection<WeaponModId> exclude)
        {
            var pool = WeaponModRegistry.Pool(weaponClass);
            var candidates = new List<WeaponModDefinition>();

            foreach (var definition in pool)
            {
                if (exclude == null || !exclude.Contains(definition.Id))
                    candidates.Add(definition);
            }

            if (candidates.Count == 0)
                return null;

            return candidates[ThreadSafeRandom.Next(0, candidates.Count - 1)];
        }

        // ---------------- magnitude ----------------

        /// <summary>
        /// A uniform potency roll over [MinPotency, 1]. The floor keeps a rolled special off the dead band where
        /// an integer-quantized native would round it away to nothing.
        /// </summary>
        public static double RollPotency(WeaponModDefinition definition)
        {
            var floor = MinPotency(definition);

            return WeaponModValue.Clamp01(floor + ThreadSafeRandom.Next(0.0f, 1.0f) * (1.0 - floor));
        }

        /// <summary>A definition's potency floor, sanitized into [0, 1). A floor of 1 or more is treated as no floor.</summary>
        public static double MinPotency(WeaponModDefinition definition)
        {
            if (definition == null)
                return 0.0;

            var floor = definition.MinPotency;

            if (double.IsNaN(floor) || floor <= 0.0 || floor >= 1.0)
                return 0.0;

            return floor;
        }

        // ---------------- layer 1 tinkers ----------------

        /// <summary>
        /// Draws <paramref name="count"/> materials UNIFORMLY WITH REPLACEMENT from the weapon class's pool.
        /// No focus material and no weighting: the mushy spread is what keeps hand-tinkering the stronger play
        /// (design section 1), and any change that makes this competitive with a focused set breaks that.
        /// </summary>
        public static List<MaterialType> RollTinkers(WeaponClass weaponClass, int count)
        {
            var rolled = new List<MaterialType>();

            if (count <= 0)
                return rolled;

            var pool = WeaponTinkerTable.Pool(weaponClass);

            if (pool.Count == 0)
                return rolled;

            for (var i = 0; i < count; i++)
                rolled.Add(pool[ThreadSafeRandom.Next(0, pool.Count - 1)].Material);

            return rolled;
        }

        /// <summary>One material drawn uniformly from a class pool. NULL when the class has no pool.</summary>
        public static MaterialType? RollTinker(WeaponClass weaponClass) => RollTinker(weaponClass, null);

        /// <summary>
        /// One material drawn uniformly from a class pool, with <paramref name="exclude"/> taken OUT of the draw.
        /// NULL only when the class has no pool at all.
        ///
        /// WHY THE EXCLUSION EXISTS. The Amethyst swap removes one filled slot and adds one modifier. When both
        /// halves land on layer 1 and the draw returns the material just removed, the player pays a whole salvage
        /// bag for a guaranteed no-op - live play produced "Lost: 1 Granite tinker / Gained: 1 Granite tinker" -
        /// and it reads as a bug rather than as bad luck. So the swap excludes the removed material from the
        /// replacement draw. It is EXCLUSION, not draw-and-retry: a retry loop has no bound on a degenerate pool,
        /// and every bail-out this system has must be mirrored in WeaponModManager.CanApply or the bag is
        /// consumed for a refusal.
        ///
        /// SPECIALS ARE DELIBERATELY NOT EXCLUDED the same way. Losing a special and regaining the same one is a
        /// real outcome, because the magnitude is REROLLED - the player can come out ahead. Only the
        /// tinker-replaces-tinker path is a guaranteed no-op, so only it is excluded.
        ///
        /// THE DEGENERATE POOL FALLS BACK RATHER THAN FAILING (HARD). Pools today are melee 4, missile 3,
        /// caster 4, so excluding one always leaves at least 2 (asserted in the tests, not assumed). If a future
        /// pool were ever cut to a single material the exclusion would empty the candidate list, and the two
        /// obvious alternatives are both worse than a same-material draw: returning NULL would make ApplySwap
        /// bail out AFTER the bag was consumed, which is exactly the hazard CanApply exists to make unreachable,
        /// and looping until something else is drawn would never terminate. The fallback is to ignore the
        /// exclusion, so the swap still completes and the worst case is the no-op it used to be.
        /// </summary>
        public static MaterialType? RollTinker(WeaponClass weaponClass, MaterialType? exclude)
        {
            var pool = WeaponTinkerTable.Pool(weaponClass);

            if (pool.Count == 0)
                return null;

            if (exclude == null)
                return pool[ThreadSafeRandom.Next(0, pool.Count - 1)].Material;

            var candidates = new List<MaterialType>();

            foreach (var material in pool)
            {
                if (material.Material != exclude.Value)
                    candidates.Add(material.Material);
            }

            // single-entry pool, or a pool of nothing but the excluded material: fall back to the unfiltered
            // draw rather than returning null or spinning
            if (candidates.Count == 0)
                return pool[ThreadSafeRandom.Next(0, pool.Count - 1)].Material;

            return candidates[ThreadSafeRandom.Next(0, candidates.Count - 1)];
        }

        /// <summary>A single [0, 1) draw, for the swap's special-versus-tinker coin flip and its removal index.</summary>
        public static double NextUnit() => ThreadSafeRandom.Next(0.0f, 1.0f);

        public static int NextIndex(int countExclusive) => countExclusive <= 1 ? 0 : ThreadSafeRandom.Next(0, countExclusive - 1);
    }
}
