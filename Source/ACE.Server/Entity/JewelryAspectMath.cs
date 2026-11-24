using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Entity;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.WorldObjects;

namespace ACE.Server.Entity
{
    /// <summary>The three aspect types a jewelry slot can roll (Docs/JewelryAspects/DESIGN.md).</summary>
    public enum AspectType
    {
        Attack,
        Defense,
        Utility,
    }

    /// <summary>How many of a piece's aspects (at most ten) are each type. An aspected piece has counts totalling 1 to 10.</summary>
    public struct AspectCounts
    {
        public int Attack;
        public int Defense;
        public int Utility;

        public AspectCounts(int attack, int defense, int utility)
        {
            Attack = attack;
            Defense = defense;
            Utility = utility;
        }

        public int Total => Attack + Defense + Utility;

        public int this[AspectType type]
        {
            get
            {
                switch (type)
                {
                    case AspectType.Attack: return Attack;
                    case AspectType.Defense: return Defense;
                    case AspectType.Utility: return Utility;
                    default: throw new ArgumentOutOfRangeException(nameof(type));
                }
            }
            set
            {
                switch (type)
                {
                    case AspectType.Attack: Attack = value; break;
                    case AspectType.Defense: Defense = value; break;
                    case AspectType.Utility: Utility = value; break;
                    default: throw new ArgumentOutOfRangeException(nameof(type));
                }
            }
        }
    }

    /// <summary>
    /// Pure helpers for Jewelry Aspects: the piece test, the property read and write, the add and the swap, and the
    /// three layer formulas (Fortitude, Penetration, Resolve). No world state is read except the aspect properties and
    /// the item type and valid locations off the item passed in. Every random draw goes through a caller-supplied
    /// <c>next(exclusiveMax)</c> so tests can seed it.
    /// </summary>
    public static class JewelryAspectMath
    {
        /// <summary>The number of aspect slots on an aspected piece.</summary>
        public const int SlotCount = 10;

        /// <summary>The largest Penetration amplify k that <see cref="Penetrate"/> honours; a larger finite k is clamped to it.</summary>
        public const double MaxPenetrationAmplify = 10.0;

        /// <summary>The Penetration amplify k as <see cref="Penetrate"/> reads it: NaN, negative or infinite is 0, a finite value above <see cref="MaxPenetrationAmplify"/> is clamped to it.</summary>
        public static double ClampAmplify(double k)
        {
            if (double.IsNaN(k) || double.IsInfinity(k) || k <= 0.0)
                return 0.0;

            return Math.Min(k, MaxPenetrationAmplify);
        }

        private const EquipMask AspectableLocations = EquipMask.NeckWear | EquipMask.WristWear | EquipMask.FingerWear;

        /// <summary>
        /// TRUE for a ring, bracelet or necklace: ItemType Jewelry and a non-zero ValidLocations that lies entirely within
        /// Neck, Wrist and Finger. A trinket, crown, cloak or sigil is not one (their locations leave that mask).
        /// </summary>
        public static bool IsAspectablePiece(WorldObject item)
        {
            if (item == null || item.ItemType != ItemType.Jewelry)
                return false;

            var locations = item.ValidLocations ?? EquipMask.None;

            return locations != EquipMask.None && (locations & ~AspectableLocations) == EquipMask.None;
        }

        /// <summary>The stored counts; an absent property reads as 0.</summary>
        public static AspectCounts ReadCounts(WorldObject item)
        {
            if (item == null)
                return default;

            return new AspectCounts(
                item.GetProperty(PropertyInt.JewelryAspectAttack) ?? 0,
                item.GetProperty(PropertyInt.JewelryAspectDefense) ?? 0,
                item.GetProperty(PropertyInt.JewelryAspectUtility) ?? 0);
        }

        public static void WriteCounts(WorldObject item, AspectCounts counts)
        {
            item.SetProperty(PropertyInt.JewelryAspectAttack, counts.Attack);
            item.SetProperty(PropertyInt.JewelryAspectDefense, counts.Defense);
            item.SetProperty(PropertyInt.JewelryAspectUtility, counts.Utility);
        }

        /// <summary>
        /// The counts summed over every aspected piece (1 to 10 aspects) in <paramref name="equipped"/> whose CurrentWieldedLocation
        /// intersects Neck, Wrist or Finger. A piece in any other slot, or with no aspect or an invalid count, adds nothing.
        /// </summary>
        public static AspectCounts SumWorn(IEnumerable<WorldObject> equipped)
        {
            var totals = new AspectCounts();

            if (equipped == null)
                return totals;

            foreach (var item in equipped)
            {
                if (item == null || ((item.CurrentWieldedLocation ?? EquipMask.None) & AspectableLocations) == EquipMask.None)
                    continue;

                if (!IsAspected(item))
                    continue;

                var counts = ReadCounts(item);

                totals.Attack += counts.Attack;
                totals.Defense += counts.Defense;
                totals.Utility += counts.Utility;
            }

            return totals;
        }

        /// <summary>TRUE when the item's counts are all non-negative and total 1 to 10 (a piece fills toward 10 one aspect at a time).</summary>
        public static bool IsAspected(WorldObject item)
        {
            if (item == null)
                return false;

            var counts = ReadCounts(item);

            return counts.Attack >= 0 && counts.Defense >= 0 && counts.Utility >= 0 && counts.Total >= 1 && counts.Total <= SlotCount;
        }

        /// <summary>
        /// The fill step: adds ONE aspect of a uniform random type (<paramref name="next"/>(3)). Only valid below 10; a full
        /// piece swaps instead.
        /// </summary>
        public static AspectCounts Add(AspectCounts counts, Func<int, int> next, out AspectType added)
        {
            if (counts.Attack < 0 || counts.Defense < 0 || counts.Utility < 0 || counts.Total >= SlotCount)
                throw new ArgumentException("Aspect counts must be non-negative and total below 10 to add an aspect", nameof(counts));

            var draw = next(3);

            if (draw < 0 || draw > 2)
                throw new InvalidOperationException($"Add draw {draw} is outside 0..2");

            added = (AspectType)draw;
            counts[added]++;

            return counts;
        }

        /// <summary>
        /// Moves exactly one slot: picks the type to take from with probability count/10 (<paramref name="next"/>(10)),
        /// then the type to give it to, one of the other two with equal chance (<paramref name="next"/>(2)). The counts
        /// must sum to 10, so the type picked always has a slot to give and from is never equal to to.
        /// </summary>
        public static AspectCounts Swap(AspectCounts counts, Func<int, int> next, out AspectType from, out AspectType to)
        {
            if (counts.Attack < 0 || counts.Defense < 0 || counts.Utility < 0 || counts.Total != SlotCount)
                throw new ArgumentException("Aspect counts must be non-negative and sum to 10", nameof(counts));

            var pick = next(SlotCount);

            if (pick < 0 || pick >= SlotCount)
                throw new InvalidOperationException($"Swap draw {pick} is outside 0..9");

            from = AspectType.Attack;
            var remaining = pick;

            foreach (AspectType type in Enum.GetValues(typeof(AspectType)))
            {
                if (remaining < counts[type])
                {
                    from = type;
                    break;
                }

                remaining -= counts[type];
            }

            var other = next(2);

            if (other < 0 || other > 1)
                throw new InvalidOperationException($"Swap draw {other} is outside 0..1");

            to = AspectType.Attack;
            var seen = 0;

            foreach (AspectType type in Enum.GetValues(typeof(AspectType)))
            {
                if (type == from)
                    continue;

                if (seen == other)
                {
                    to = type;
                    break;
                }

                seen++;
            }

            counts[from]--;
            counts[to]++;

            return counts;
        }

        /// <summary>
        /// The layer total for a worn slot count: min(slots * perSlot, cap). 0.0 when disabled, PvP, or when slots, perSlot
        /// or cap is negative or not finite. The cap is clamped to [0, 1].
        /// </summary>
        public static double Layer(int slots, double perSlot, double cap, bool enabled, bool pvp)
        {
            if (!enabled || pvp || slots <= 0)
                return 0.0;

            if (double.IsNaN(perSlot) || double.IsInfinity(perSlot) || perSlot <= 0.0)
                return 0.0;

            if (double.IsNaN(cap) || double.IsInfinity(cap) || cap <= 0.0)
                return 0.0;

            cap = Math.Min(cap, 1.0);

            return Math.Min(slots * perSlot, cap);
        }

        /// <summary>
        /// The fraction of a fully eligible hit that <see cref="ApplyFortitude"/> leaves, before its whole-point rounding:
        /// 1 - min(f, 1), and 1.0 for a NaN or non-positive f. Character Details shows this.
        /// </summary>
        public static double FortitudeMultiplier(double f)
        {
            if (double.IsNaN(f) || f <= 0.0)
                return 1.0;

            return 1.0 - Math.Min(f, 1.0);
        }

        /// <summary>
        /// Fortitude: damage minus round(eligible * f), never below 0. <paramref name="eligible"/> is the part of the hit
        /// Fortitude may touch (the whole hit for a PvE non-self source). A NaN or non-positive f changes nothing.
        /// </summary>
        public static uint ApplyFortitude(uint damage, uint eligible, double f)
        {
            if (double.IsNaN(f) || f <= 0.0 || damage == 0 || eligible == 0)
                return damage;

            var reduction = Math.Round(eligible * Math.Min(f, 1.0), MidpointRounding.AwayFromZero);

            if (reduction >= damage)
                return 0;

            return damage - (uint)reduction;
        }

        /// <summary>
        /// Penetration: the larger of two results. (1) Removal: a below-1 defensive multiplier moves toward 1 by fraction p
        /// of the gap (a multiplier of 1 or more is left as is). (2) Amplify: the multiplier is scaled by (1 + p * k).
        /// So a resisted target never ends lower than removal alone gives, a neutral target (1.0) takes 1 + p * k, and a
        /// weak target (above 1) is scaled up too. A NaN or non-positive p changes nothing; p is capped at 1. A NaN or
        /// negative or infinite k (including +Infinity) counts as 0 (no amplify), a finite k above <see cref="MaxPenetrationAmplify"/> is clamped to it, and k = 0 reproduces the removal-only behaviour exactly.
        /// </summary>
        public static float Penetrate(float mod, double p, double k)
        {
            if (double.IsNaN(p) || p <= 0.0)
                return mod;

            var cappedP = Math.Min(p, 1.0);
            var penetrated = mod < 1.0f ? mod + (1.0 - mod) * cappedP : (double)mod;

            if (double.IsNaN(k) || double.IsInfinity(k) || k <= 0.0)
                return (float)penetrated;

            k = Math.Min(k, MaxPenetrationAmplify);

            return (float)Math.Max(penetrated, mod * (1.0 + cappedP * k));
        }

        /// <summary>
        /// Resolve's kill roll: TRUE when a uniform <paramref name="draw"/> in [0, 1) is below <paramref name="chance"/>.
        /// A chance of 1 or more always succeeds and a chance of 0, a negative or a NaN never does, whatever the draw.
        /// </summary>
        public static bool ResolveRolls(double chance, double draw)
        {
            if (double.IsNaN(chance) || chance <= 0.0)
                return false;

            if (chance >= 1.0)
                return true;

            return !double.IsNaN(draw) && draw < chance;
        }

        /// <summary>
        /// One entry chosen uniformly: <paramref name="next"/>(0, Count - 1), inclusive at both ends like ThreadSafeRandom.Next(int, int).
        /// Null for an empty or null list.
        /// </summary>
        public static PropertiesEnchantmentRegistry PickOne(IReadOnlyList<PropertiesEnchantmentRegistry> eligible, Func<int, int, int> next)
        {
            if (eligible == null || eligible.Count == 0)
                return null;

            var index = next(0, eligible.Count - 1);

            if (index < 0 || index >= eligible.Count)
                throw new InvalidOperationException($"Resolve pick {index} is outside 0..{eligible.Count - 1}");

            return eligible[index];
        }

        /// <summary>
        /// Every layer a successful Resolve removes for <paramref name="chosen"/>: the eligible (<see cref="SelectResolvable"/>)
        /// entries in the chosen entry's spell category (whatever their spell id, exactly like Cleanse's
        /// DispellingEdgeAbility.SelectAllLayers), so removing one layer cannot just expose the next. A copy that is not eligible
        /// (a player's cast, an item effect, a self-cast) stays. The chosen entry is always
        /// included.
        /// </summary>
        public static List<PropertiesEnchantmentRegistry> SelectAllLayers(PropertiesEnchantmentRegistry chosen, IEnumerable<PropertiesEnchantmentRegistry> categoryEntries, uint playerGuid, Func<int, bool> isHarmful)
        {
            if (chosen == null)
                return new List<PropertiesEnchantmentRegistry>();

            var layers = SelectResolvable(categoryEntries, playerGuid, isHarmful)
                .Where(e => e.SpellCategory == chosen.SpellCategory)
                .ToList();

            if (!layers.Contains(chosen))
                layers.Add(chosen);

            return layers;
        }

        /// <summary>
        /// The debuffs Resolve may remove from a player: entries that are timed and a real spell id (the shared
        /// dispellable rule, so vitae and item-sourced effects are out), harmful (<paramref name="isHarmful"/>, asked
        /// about each survivor), and cast by a monster: a non-zero caster guid that is neither a player guid nor the
        /// player itself. The creature dispel exemption (EnchantmentManager.CreatureDispelExempt) lists only beneficial
        /// progression buffs, so the harmful test already excludes everything it names.
        /// </summary>
        public static List<PropertiesEnchantmentRegistry> SelectResolvable(IEnumerable<PropertiesEnchantmentRegistry> candidates, uint playerGuid, Func<int, bool> isHarmful)
        {
            if (candidates == null || isHarmful == null)
                return new List<PropertiesEnchantmentRegistry>();

            return DispellingEdgeAbility.SelectDispellable(candidates.ToList())
                .Where(e => e.CasterObjectId != 0 && e.CasterObjectId != playerGuid && !ObjectGuid.IsPlayer(e.CasterObjectId) && isHarmful(e.SpellId))
                .ToList();
        }
    }
}