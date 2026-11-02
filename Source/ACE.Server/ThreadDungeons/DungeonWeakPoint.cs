using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using ACE.Entity.Enum;
using ACE.Entity.Models;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// The Threads "weak point" (owner ruling 2026-10-24): every Thread creature has the body parts of ONE
    /// attack height (High, Medium or Low) stored at a reduced BaseArmor, so a player who aims at that height
    /// hits softer armour. Data only - no combat hook and no new property: the cut is written into the
    /// creature's own PropertiesBodyPart, which every damage read already goes through.
    ///
    /// PURE. No PropertyManager, no Creature, no logging, no run RNG: the selection is a function of
    /// (family id, wcid, the creature's body parts) alone, so two spawns of one creature in one family always
    /// cut the same parts and the run's seeded rng (and so the whole population plan) is never advanced.
    ///
    /// Part to height: the height with the strictly greatest summed quadrant weight (High is HLF+HRF+HLB+HRB,
    /// Medium is MLF+MRF+MLB+MRB, Low is LLF+LRF+LLB+LRB). A tie within <see cref="TieEpsilon"/> and a part
    /// with no weight at all belong to NO height. The BH field is never read.
    ///
    /// Fallback "random third": a family listed as <c>random</c>, or a creature with no part dominant at the
    /// wanted height, cuts round(hittable / 3, AwayFromZero) (minimum 1) of its hittable parts, picked by
    /// <see cref="StableSeed"/> and <see cref="SplitMix64"/>. Never string.GetHashCode (randomised per process),
    /// never the run rng.
    ///
    /// THE SHARED-REFERENCE TRAP. Biota.PropertiesBodyPart on a new Creature is the SAME reference as the
    /// cached weenie's (ACE.Entity/Adapter/WeenieConverter.cs), and a plain trash spawn reaches this step still
    /// sharing it: none of the other body-part writers clones unless it changes something. So
    /// <see cref="Apply"/> builds a FRESH dictionary holding a Clone() of EVERY part whenever it writes
    /// anything, and never writes into the existing dictionary or the existing part objects. Writing in place
    /// would cut every instance of the creature server-wide, re-cut on every spawn (geometric decay), and feed
    /// the decayed values into the plan-time band standards (DungeonStatProfile.FromWeenie).
    /// </summary>
    public static class DungeonWeakPoint
    {
        /// <summary>Two height sums closer than this are a tie. A tie gives the part no height.</summary>
        public const double TieEpsilon = 1e-4;

        /// <summary>How a <see cref="Selection"/> was reached.</summary>
        public enum SelectionMode
        {
            /// <summary>Every part dominant at the wanted height.</summary>
            Height,

            /// <summary>The family has no height differential (<c>random</c>): a third of the hittable parts.</summary>
            RandomThirdFamily,

            /// <summary>The family wanted a height but no hittable part of this creature is dominant at it: a third of the hittable parts.</summary>
            RandomThirdFallback,
        }

        /// <summary>The parts chosen for one creature, sorted by part key, and why.</summary>
        public sealed class Selection
        {
            public SelectionMode Mode { get; }

            /// <summary>The wanted height, or null for <see cref="SelectionMode.RandomThirdFamily"/>.</summary>
            public AttackHeight? Height { get; }

            public IReadOnlyList<CombatBodyPart> Parts { get; }

            public Selection(SelectionMode mode, AttackHeight? height, IReadOnlyList<CombatBodyPart> parts)
            {
                Mode = mode;
                Height = height;
                Parts = parts;
            }
        }

        /// <summary>The token weak-points.json uses for a family with no height differential.</summary>
        public const string RandomToken = "random";

        /// <summary>
        /// Parses a weak-points.json height token: high, medium, low (lower case, exact) or <c>random</c>, which
        /// yields a null height. False for anything else, including null.
        /// </summary>
        public static bool TryParseHeight(string token, out AttackHeight? height)
        {
            height = null;

            switch (token)
            {
                case "high": height = AttackHeight.High; return true;
                case "medium": height = AttackHeight.Medium; return true;
                case "low": height = AttackHeight.Low; return true;
                case RandomToken: return true;
                default: return false;
            }
        }

        private static double HighWeight(PropertiesBodyPart p) => (double)p.HLF + p.HRF + p.HLB + p.HRB;
        private static double MediumWeight(PropertiesBodyPart p) => (double)p.MLF + p.MRF + p.MLB + p.MRB;
        private static double LowWeight(PropertiesBodyPart p) => (double)p.LLF + p.LRF + p.LLB + p.LRB;

        /// <summary>True when the part can be hit at all: its quadrant weights sum to more than zero.</summary>
        public static bool IsHittable(PropertiesBodyPart p)
            => p != null && HighWeight(p) + MediumWeight(p) + LowWeight(p) > 0;

        /// <summary>
        /// The one height with the strictly greatest summed quadrant weight, or null when the part is not
        /// hittable or its two best heights tie within <see cref="TieEpsilon"/>.
        /// </summary>
        public static AttackHeight? DominantHeight(PropertiesBodyPart p)
        {
            if (!IsHittable(p))
                return null;

            var high = HighWeight(p);
            var medium = MediumWeight(p);
            var low = LowWeight(p);

            AttackHeight best;
            double top, second;

            if (high >= medium && high >= low)
            {
                best = AttackHeight.High;
                top = high;
                second = Math.Max(medium, low);
            }
            else if (medium >= high && medium >= low)
            {
                best = AttackHeight.Medium;
                top = medium;
                second = Math.Max(high, low);
            }
            else
            {
                best = AttackHeight.Low;
                top = low;
                second = Math.Max(high, medium);
            }

            return top - second <= TieEpsilon ? (AttackHeight?)null : best;
        }

        /// <summary>round(hittable / 3), halves away from zero, never fewer than one.</summary>
        public static int RandomThirdCount(int hittable)
            => Math.Max(1, (int)Math.Round(hittable / 3.0, MidpointRounding.AwayFromZero));

        /// <summary>FNV-1a 64 over the UTF-8 bytes of familyId + "|" + wcid. Stable across processes and platforms.</summary>
        public static ulong StableSeed(string familyId, uint wcid)
        {
            var bytes = Encoding.UTF8.GetBytes((familyId ?? "") + "|" + wcid.ToString(System.Globalization.CultureInfo.InvariantCulture));

            unchecked
            {
                var hash = 14695981039346656037UL;

                foreach (var b in bytes)
                {
                    hash ^= b;
                    hash *= 1099511628211UL;
                }

                return hash;
            }
        }

        /// <summary>One step of the SplitMix64 generator, taking the state to mix.</summary>
        public static ulong SplitMix64(ulong x)
        {
            unchecked
            {
                var z = x + 0x9E3779B97F4A7C15UL;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return z ^ (z >> 31);
            }
        }

        /// <summary>
        /// Chooses the parts to cut. <paramref name="height"/> null means the family has no height
        /// differential. The result is sorted by part key, whatever order the dictionary enumerates in.
        /// </summary>
        public static Selection Select(IDictionary<CombatBodyPart, PropertiesBodyPart> parts, AttackHeight? height, string familyId, uint wcid)
        {
            if (parts == null || parts.Count == 0)
                return new Selection(height == null ? SelectionMode.RandomThirdFamily : SelectionMode.RandomThirdFallback, height, new List<CombatBodyPart>());

            if (height != null)
            {
                var dominant = parts
                    .Where(kv => DominantHeight(kv.Value) == height)
                    .Select(kv => kv.Key)
                    .OrderBy(k => (int)k)
                    .ToList();

                if (dominant.Count > 0)
                    return new Selection(SelectionMode.Height, height, dominant);
            }

            var hittable = parts.Where(kv => IsHittable(kv.Value)).Select(kv => kv.Key).ToList();
            var take = hittable.Count == 0 ? 0 : RandomThirdCount(hittable.Count);
            var seed = StableSeed(familyId, wcid);

            var chosen = hittable
                .OrderBy(k => SplitMix64(seed ^ (ulong)(long)(int)k))
                .ThenBy(k => (int)k)
                .Take(take)
                .OrderBy(k => (int)k)
                .ToList();

            return new Selection(height == null ? SelectionMode.RandomThirdFamily : SelectionMode.RandomThirdFallback, height, chosen);
        }

        /// <summary>
        /// baseArmor x (1 - cut), halves away from zero. A cut at or below zero (or NaN) and a base armour at or
        /// below zero leave the value alone (a negative armour must never get stronger); a cut at or above one reads as one (armour 0).
        /// </summary>
        public static int CutArmor(int baseArmor, double cut)
        {
            if (!(cut > 0) || baseArmor <= 0)
                return baseArmor;

            return (int)Math.Round(baseArmor * (1.0 - Math.Min(cut, 1.0)), MidpointRounding.AwayFromZero);
        }

        /// <summary>
        /// Cuts the selected parts' BaseArmor on <paramref name="biota"/>. When at least one value changes it
        /// builds a FRESH dictionary holding a Clone() of EVERY part, writes the cut values into the clones and
        /// assigns the dictionary to the biota - the existing dictionary and the existing part objects are never
        /// written (see the class comment). When nothing changes it returns an empty list and allocates and
        /// assigns nothing. Takes the RUNTIME biota (ACE.Entity.Models.Biota), never the EF entity.
        /// </summary>
        public static List<(CombatBodyPart Part, int From, int To)> Apply(ACE.Entity.Models.Biota biota, Selection selection, double cut)
        {
            var changes = new List<(CombatBodyPart Part, int From, int To)>();
            var parts = biota?.PropertiesBodyPart;

            if (parts == null || parts.Count == 0 || selection == null || selection.Parts.Count == 0)
                return changes;

            foreach (var key in selection.Parts)
            {
                if (!parts.TryGetValue(key, out var part) || part == null)
                    continue;

                var to = CutArmor(part.BaseArmor, cut);

                if (to != part.BaseArmor)
                    changes.Add((key, part.BaseArmor, to));
            }

            if (changes.Count == 0)
                return changes;

            var fresh = new Dictionary<CombatBodyPart, PropertiesBodyPart>(parts.Count);

            foreach (var kvp in parts)
                fresh[kvp.Key] = kvp.Value?.Clone();

            foreach (var (part, _, to) in changes)
                fresh[part].BaseArmor = to;

            biota.PropertiesBodyPart = fresh;

            return changes;
        }
    }
}
