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
        /// The four special_chance tunables, sanitized. They are CUMULATIVE - P(at least one), P(at least two),
        /// P(at least three), P(all four) - because that is how the odds are reasoned about, so they must
        /// decrease monotonically. A bad set is CLAMPED rather than thrown, so a typo in a live tunable cannot
        /// produce a negative probability band mid-craft; the first time it trips is logged once.
        ///
        /// weapon_mod_special_chance_4 arrived on 2026-08-06 with the cap raise from 3 to 4 and DEFAULTS TO 0,
        /// so the fourth band is empty until the magnitude pass sets it. A zero fourth chance is a perfectly
        /// well formed monotone set and must never be reported as bad.
        /// </summary>
        public static (double One, double Two, double Three, double Four) SanitizeChances(double c1, double c2, double c3, double c4)
        {
            var one = WeaponModValue.Clamp01(c1);
            var two = WeaponModValue.Clamp01(c2);
            var three = WeaponModValue.Clamp01(c3);
            var four = WeaponModValue.Clamp01(c4);

            var bad = two > one || three > two || four > three || c1 != one || c2 != two || c3 != three || c4 != four;

            if (two > one)
                two = one;

            if (three > two)
                three = two;

            if (four > three)
                four = three;

            if (bad && !loggedBadChances)
            {
                loggedBadChances = true;
                log.Error($"WeaponModRoller: weapon_mod_special_chance_1/2/3/4 are not a monotonically decreasing set in [0, 1] ({c1}, {c2}, {c3}, {c4}) - clamped to ({one}, {two}, {three}, {four})");
            }

            return (one, two, three, four);
        }

        /// <summary>
        /// How many specials a roll of <paramref name="roll"/> in [0, 1) buys, given the four cumulative odds.
        /// Pure. At the defaults (0.35, 0.10, 0.02, 0.00) this is none 65%, one 25%, two 8%, three 2%, four 0%.
        /// </summary>
        public static int ResolveSpecialCount(double roll, double c1, double c2, double c3, double c4)
        {
            var (one, two, three, four) = SanitizeChances(c1, c2, c3, c4);

            if (double.IsNaN(roll))
                return 0;

            // THE FOUR BAND IS TESTED WITH A STRICT "<" LIKE THE OTHERS, which is what keeps a chance of 0
            // unreachable: ThreadSafeRandom.Next(0f, 1f) can return exactly 0, and "roll <= four" would then
            // hand out a fourth special on a tunable that is switched off. Do not relax it.
            if (roll < four)
                return 4;

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
                PropertyManager.GetDouble("weapon_mod_special_chance_3").Item,
                PropertyManager.GetDouble("weapon_mod_special_chance_4").Item);
        }

        /// <summary>
        /// The HARD clamp on how many specials a weapon may end up holding: never more than
        /// <see cref="WeaponModRegistry.MaxSpecials"/>. Applied on top of the odds table rather than being left
        /// implied by it, so raising weapon_mod_special_chance_4 can never produce a fifth.
        ///
        /// THE SLOT TERM IS GONE (2026-08-06). This used to also clamp against "the slots left after imbues",
        /// because a special cost a tinker slot. Since the decoupling it does not: specials have their own
        /// budget and are bounded by <see cref="WeaponModRegistry.MaxSpecials"/> alone. A weapon whose ten
        /// slots are ENTIRELY reserved still never reaches here, because
        /// <see cref="WeaponModManager.ResolveRefusal"/> refuses the whole use with NoAvailableSlots first -
        /// that refusal, not this clamp, is what keeps an unworkable weapon unworkable.
        /// </summary>
        public static int ClampSpecialCount(int rolled) => Math.Clamp(rolled, 0, WeaponModRegistry.MaxSpecials);

        // ---------------- special identity ----------------

        /// <summary>
        /// TRUE when a rolled SET must lead with a damage-relevant special. The tunable arrived on 2026-08-06
        /// and DEFAULTS TO FALSE, so the draw is unchanged until it is switched on.
        /// </summary>
        public static bool GuaranteeDamageSpecial() => PropertyManager.GetBool("weapon_mod_guarantee_damage_special").Item;

        /// <summary>
        /// Draws <paramref name="count"/> DISTINCT specials from a weapon class's pool, against the live
        /// weapon_mod_guarantee_damage_special tunable. See the explicit-flag overload for the rule.
        /// </summary>
        public static List<WeaponModDefinition> RollDistinctSpecials(WeaponClass weaponClass, int count, ICollection<WeaponModId> exclude = null) =>
            RollDistinctSpecials(weaponClass, count, exclude, GuaranteeDamageSpecial());

        /// <summary>
        /// Draws <paramref name="count"/> DISTINCT specials from a weapon class's pool. A weapon never holds the
        /// same special twice, so anything in <paramref name="exclude"/> is off the table too. Returns fewer than
        /// asked only when the pool runs out, which the Tier A caster pool (3 deep against a cap of 4) does.
        ///
        /// WITH <paramref name="guaranteeDamageFirst"/> SET, draw number ONE comes from
        /// <see cref="WeaponModRegistry.DamagePool"/> - the AffectsSingleTargetDamage subset of the same class
        /// pool - and every later draw comes from the full class pool as before. That is what stops a set being
        /// entirely utility, which is the failure the tunable exists to prevent.
        ///
        /// IT IS THE FIRST DRAW, NOT THE FIRST SPECIAL THE WEAPON ENDS UP HOLDING. The distinction matters
        /// because <see cref="WeaponModManager.ApplyReroll"/> then DROPS any drawn special whose magnitude
        /// resolves to zero (weapon_mod_magnitude_scale = 0 mutes the whole layer), so a guaranteed damage draw
        /// can still be dropped on the way to the weapon. The alternative - re-drawing until a damage special
        /// survives - would make the scale-0 mute unreachable and could not terminate.
        ///
        /// THE GUARANTEE IS BEST-EFFORT, NOT A POSTCONDITION. <see cref="RollSpecial"/> falls back to the full
        /// pool when the damage subset is empty or entirely excluded, because returning null there would silently
        /// cost the caller a special rather than merely a preference.
        /// </summary>
        public static List<WeaponModDefinition> RollDistinctSpecials(WeaponClass weaponClass, int count, ICollection<WeaponModId> exclude, bool guaranteeDamageFirst)
        {
            var rolled = new List<WeaponModDefinition>();

            if (count <= 0)
                return rolled;

            var taken = exclude == null ? new HashSet<WeaponModId>() : new HashSet<WeaponModId>(exclude);

            for (var i = 0; i < count; i++)
            {
                var next = RollSpecial(weaponClass, taken, guaranteeDamageFirst && i == 0);

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
        public static WeaponModDefinition RollSpecial(WeaponClass weaponClass, ICollection<WeaponModId> exclude) =>
            RollSpecial(weaponClass, exclude, false);

        /// <summary>
        /// One special drawn uniformly from a class pool, skipping anything already held. With
        /// <paramref name="damageOnly"/> set the draw is restricted to
        /// <see cref="WeaponModRegistry.DamagePool"/>.
        ///
        /// THE RESTRICTION FALLS BACK RATHER THAN FAILING (HARD), and it is the same reasoning as
        /// <see cref="RollTinker(WeaponClass, MaterialType?)"/>'s degenerate-pool fallback. A class whose damage
        /// subset is empty, or one whose damage rows are all already held, must still get a special: returning
        /// null here would silently shorten the set, and on the swap path it would reach
        /// <see cref="WeaponModManager.ApplySwap"/> after the salvage bag was already consumed. So the damage
        /// restriction is dropped and the full class pool is drawn from instead. Only when the FULL pool is
        /// exhausted does this return null.
        /// </summary>
        public static WeaponModDefinition RollSpecial(WeaponClass weaponClass, ICollection<WeaponModId> exclude, bool damageOnly)
        {
            if (damageOnly)
            {
                var preferred = Candidates(WeaponModRegistry.DamagePool(weaponClass), exclude);

                if (preferred.Count > 0)
                    return preferred[ThreadSafeRandom.Next(0, preferred.Count - 1)];
            }

            var candidates = Candidates(WeaponModRegistry.Pool(weaponClass), exclude);

            if (candidates.Count == 0)
                return null;

            return candidates[ThreadSafeRandom.Next(0, candidates.Count - 1)];
        }

        private static List<WeaponModDefinition> Candidates(IReadOnlyList<WeaponModDefinition> pool, ICollection<WeaponModId> exclude)
        {
            var candidates = new List<WeaponModDefinition>();

            foreach (var definition in pool)
            {
                if (exclude == null || !exclude.Contains(definition.Id))
                    candidates.Add(definition);
            }

            return candidates;
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
