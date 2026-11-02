using System;
using System.Collections.Generic;

using ACE.Server.WorldObjects;

namespace ACE.Server.Entity.AccountVault
{
    /// <summary>
    /// Pure planner for combining the partial salvage bags of ONE group (same wcid, any workmanship) into
    /// as few bags as possible: full bags plus at most one partial. It pours bags
    /// with <see cref="SalvageCombine.Pour"/>, the same arithmetic the hand-combine path runs, and rounds each
    /// bag that comes out full with Player.RoundBagWorkmanship, the same rounding the game applies.
    ///
    /// No PropertyManager reads and no WorldObject construction: the caller supplies raw ints, the value
    /// cap and the max structure, and applies the returned outputs itself. The material allowlist, the
    /// salvage-tool exclusion and listing checks belong to the store, not here.
    ///
    /// Value: each pour apportions the source's value by structure and clamps the target to the cap, as
    /// AddSalvage does. The two fragments of a split source round independently, so total value out can
    /// differ from total value in by rounding (at most 1 per split) on top of <see cref="PlanResult.ValueCappedOff"/>;
    /// <see cref="PlanResult.ValueRoundingDelta"/> reports that remainder.
    /// </summary>
    public static class SalvagePartialCombinePlanner
    {
        /// <summary>Inclusive bounds of a legal whole-workmanship average (the Workmanship getter's range).</summary>
        public const int MinWorkmanship = 1;
        public const int MaxWorkmanship = 10;

        public readonly struct PlanInput
        {
            public readonly int Structure;
            public readonly int Iw;
            public readonly int Nim;
            public readonly int Value;

            public PlanInput(int structure, int iw, int nim, int value)
            {
                Structure = structure;
                Iw = iw;
                Nim = nim;
                Value = value;
            }

            public static implicit operator PlanInput((int Structure, int Iw, int Nim, int Value) t)
                => new PlanInput(t.Structure, t.Iw, t.Nim, t.Value);
        }

        public readonly struct PlanOutput
        {
            public readonly int Structure;
            public readonly int Iw;
            public readonly int Nim;
            public readonly int Value;
            public readonly string Name;

            /// <summary>True when Structure reached max structure (and Iw was rounded to a whole workmanship).</summary>
            public readonly bool IsFull;

            public PlanOutput(int structure, int iw, int nim, int value, string name, bool isFull)
            {
                Structure = structure;
                Iw = iw;
                Nim = nim;
                Value = value;
                Name = name;
                IsFull = isFull;
            }
        }

        public sealed class PlanResult
        {
            /// <summary>All full except possibly the last. Empty for a no-op or aborted plan.</summary>
            public IReadOnlyList<PlanOutput> Outputs { get; }

            /// <summary>Total value lost to the per-bag cap across every pour.</summary>
            public long ValueCappedOff { get; }

            /// <summary>
            /// Total value in minus total value out minus <see cref="ValueCappedOff"/>: the integer rounding of
            /// split fragments. 0 when no source was split.
            /// </summary>
            public long ValueRoundingDelta { get; }

            /// <summary>An output's raw average workmanship would leave [1, 10]; the whole group is left alone.</summary>
            public bool Aborted { get; }

            /// <summary>Fewer than two inputs; nothing to combine.</summary>
            public bool IsNoOp { get; }

            public PlanResult(IReadOnlyList<PlanOutput> outputs, long valueCappedOff, long valueRoundingDelta, bool aborted, bool isNoOp)
            {
                Outputs = outputs;
                ValueCappedOff = valueCappedOff;
                ValueRoundingDelta = valueRoundingDelta;
                Aborted = aborted;
                IsNoOp = isNoOp;
            }
        }

        /// <summary>
        /// A bag is combinable when it is a partial (0 &lt; structure &lt; max), carries workmanship and item
        /// count, its raw average workmanship is a legal [1, 10] value, and it still has the name the combine
        /// writes (a renamed bag is the player's, not ours). Raw ints only: never the Workmanship getter,
        /// which rewrites ItemWorkmanship when the average is out of range.
        /// </summary>
        public static bool IsEligible(int structure, int? iw, int? nim, string name, int maxStructure)
        {
            if (structure <= 0 || structure >= maxStructure)
                return false;

            if (iw == null || nim == null || iw.Value <= 0 || nim.Value <= 0)
                return false;

            var quotient = (double)iw.Value / nim.Value;
            if (quotient < MinWorkmanship || quotient > MaxWorkmanship)
                return false;

            return name == $"Salvage ({structure})";
        }

        /// <summary>
        /// Plans one group. <paramref name="inputs"/> must already be in a deterministic order (the caller
        /// sorts): the first bag is the target and each later bag is poured in; a target that reaches
        /// <paramref name="maxStructure"/> becomes a full output and any remainder of the source starts the
        /// next target.
        /// </summary>
        public static PlanResult Plan(IReadOnlyList<(int Structure, int Iw, int Nim, int Value)> inputs, int maxStructure, long valueCap)
        {
            if (inputs == null || inputs.Count < 2)
                return new PlanResult(Array.Empty<PlanOutput>(), 0, 0, aborted: false, isNoOp: true);

            long valueIn = 0;
            foreach (var input in inputs)
                valueIn += input.Value;

            var states = new List<SalvageCombine.BagState>();
            long cappedOff = 0;

            var cur = new SalvageCombine.BagState(inputs[0].Structure, inputs[0].Iw, inputs[0].Nim, inputs[0].Value);

            for (var i = 1; i < inputs.Count; i++)
            {
                var src = new SalvageCombine.BagState(inputs[i].Structure, inputs[i].Iw, inputs[i].Nim, inputs[i].Value);
                var basis = src.Structure;

                var pending = src;

                while (pending.Structure > 0)
                {
                    if (cur.Structure >= maxStructure)
                    {
                        states.Add(cur);
                        cur = new SalvageCombine.BagState(0, 0, 0, 0);
                    }

                    var pour = SalvageCombine.Pour(cur, pending, maxStructure, valueCap, basis);

                    // cannot happen while cur has space and pending has structure; guards an endless loop
                    if (pour.Added <= 0)
                        return new PlanResult(Array.Empty<PlanOutput>(), 0, 0, aborted: true, isNoOp: false);

                    cur = pour.Target;
                    cappedOff += pour.ValueCappedOff;
                    pending = pour.SourceRemainder;
                }
            }

            states.Add(cur);

            var outputs = new List<PlanOutput>();
            long valueOut = 0;

            foreach (var s in states)
            {
                if (s.Nim <= 0 || s.Iw <= 0)
                    return new PlanResult(Array.Empty<PlanOutput>(), 0, 0, aborted: true, isNoOp: false);

                var quotient = (double)s.Iw / s.Nim;
                if (quotient < MinWorkmanship || quotient > MaxWorkmanship)
                    return new PlanResult(Array.Empty<PlanOutput>(), 0, 0, aborted: true, isNoOp: false);

                var full = s.Structure >= maxStructure;
                var iw = full ? Player.RoundBagWorkmanship(s.Iw, s.Nim) : s.Iw;

                outputs.Add(new PlanOutput(s.Structure, iw, s.Nim, s.Value, $"Salvage ({s.Structure})", full));
                valueOut += s.Value;
            }

            return new PlanResult(outputs, cappedOff, valueIn - valueOut - cappedOff, aborted: false, isNoOp: false);
        }
    }
}
