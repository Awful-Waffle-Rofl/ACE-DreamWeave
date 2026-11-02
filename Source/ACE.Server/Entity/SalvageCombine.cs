using System;

using ACE.Server.WorldObjects;

namespace ACE.Server.Entity
{
    /// <summary>
    /// The arithmetic of pouring one salvage bag into another, pulled out of Player.TryAddSalvage and
    /// Player.AddSalvage so the hand-combine path and the mule partial-salvage planner share ONE
    /// implementation. Everything here is static and int-based: no WorldObject, no PropertyManager, no
    /// skill lookup, so it runs under the unit-test host and cannot drift from the shipped game math.
    ///
    /// Only the bag-source (TinkeringMaterial) branch lives here. Fresh salvaging, where the source is a
    /// loot item, stays in Player.TryAddSalvage and applies the salvage skill factor to value there.
    /// </summary>
    public static class SalvageCombine
    {
        /// <summary>One salvage bag's combine-relevant state, as raw ints.</summary>
        public readonly struct BagState
        {
            public readonly int Structure;
            public readonly int Iw;
            public readonly int Nim;
            public readonly int Value;

            public BagState(int structure, int iw, int nim, int value)
            {
                Structure = structure;
                Iw = iw;
                Nim = nim;
                Value = value;
            }
        }

        public readonly struct PourResult
        {
            /// <summary>The target after the pour.</summary>
            public readonly BagState Target;

            /// <summary>
            /// What is left of the source. Structure is 0 when the source fit entirely; otherwise Structure,
            /// Iw and Nim are the untransferred remainder (as SplitSalvage rewrites the source in the hand
            /// path) and Value is the source's ORIGINAL value, which the next pour apportions again.
            /// </summary>
            public readonly BagState SourceRemainder;

            /// <summary>Structure actually moved into the target (TryAddSalvage's return value).</summary>
            public readonly int Added;

            /// <summary>Value lost to the per-bag cap on this pour.</summary>
            public readonly long ValueCappedOff;

            public PourResult(BagState target, BagState sourceRemainder, int added, long valueCappedOff)
            {
                Target = target;
                SourceRemainder = sourceRemainder;
                Added = added;
                ValueCappedOff = valueCappedOff;
            }

            public bool SourceFullyPoured => SourceRemainder.Structure <= 0;
        }

        /// <summary>
        /// The value a pour of <paramref name="added"/> structure carries out of a source bag: the source's
        /// value apportioned by structure over <paramref name="sourceTotalStructure"/> (the structure the
        /// source held when the whole combine began, NOT what is left of it), with no skill factor. Float
        /// arithmetic and banker's rounding are deliberate: they are what AddSalvage always did.
        /// </summary>
        public static int ValueShare(int sourceValue, int added, int sourceTotalStructure)
        {
            if (sourceTotalStructure <= 0)
                return 0;

            var valueFactor = (float)added / sourceTotalStructure;

            return (int)Math.Round(sourceValue * valueFactor);
        }

        /// <summary>Adds to a bag's value and clamps to the cap, reporting how much the clamp removed.</summary>
        public static int CapValue(int currentValue, int addedValue, int valueCap, out long cappedOff)
        {
            var uncapped = (long)currentValue + addedValue;
            var capped = Math.Min(uncapped, valueCap);

            cappedOff = uncapped - capped;

            return (int)capped;
        }

        /// <summary>
        /// Pours <paramref name="source"/> into <paramref name="target"/> up to <paramref name="maxStructure"/>.
        /// source.Structure is the structure still to be poured. <paramref name="sourceTotalStructure"/>
        /// (null means source.Structure) is the basis the source's value is apportioned over, and must be
        /// passed unchanged across the successive pours of one source bag. Workmanship sum and item count
        /// move by Player.SplitSalvage, so each side keeps the source's average to within its rounding.
        /// </summary>
        public static PourResult Pour(BagState target, BagState source, int maxStructure, long valueCap, int? sourceTotalStructure = null)
        {
            var space = maxStructure - target.Structure;
            var amount = Math.Min(source.Structure, space);

            Player.SplitSalvage(Math.Max(1, source.Nim), source.Iw, amount, source.Structure,
                                out var addNum, out var addIw, out var remNum, out var remIw);

            var partial = amount < source.Structure;

            var addedValue = ValueShare(source.Value, amount, sourceTotalStructure ?? source.Structure);
            var cap = (int)Math.Clamp(valueCap, 0L, int.MaxValue);
            var newValue = CapValue(target.Value, addedValue, cap, out var cappedOff);

            var newTarget = new BagState(target.Structure + amount, target.Iw + addIw, target.Nim + addNum, newValue);

            // a full transfer leaves remNum 0, and a bag with 0 items divides by zero in the Workmanship
            // getter, so the remainder is only meaningful on a partial transfer
            var remainder = partial
                ? new BagState(source.Structure - amount, remIw, remNum, source.Value)
                : new BagState(0, source.Iw, source.Nim, source.Value);

            return new PourResult(newTarget, remainder, amount, cappedOff);
        }
    }
}
