using System;
using System.Collections.Generic;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>One row of the world DB's treasure_wielded table, reduced to what the set-rolling machine reads.</summary>
    public readonly struct WieldedRow
    {
        public uint Wcid { get; }
        public float Probability { get; }
        public bool SetStart { get; }
        public bool HasSubSet { get; }
        public bool ContinuesPreviousSet { get; }

        public WieldedRow(uint wcid, float probability, bool setStart, bool hasSubSet, bool continuesPreviousSet)
        {
            Wcid = wcid;
            Probability = probability;
            SetStart = setStart;
            HasSubSet = hasSubSet;
            ContinuesPreviousSet = continuesPreviousSet;
        }
    }

    /// <summary>
    /// The exact probability that a wielded-treasure table rolls a set containing an item satisfying a predicate
    /// (here: a launcher or thrown weapon), reproducing WorldObject.GenerateWieldedTreasureSets
    /// (WorldObject_Equipment.cs:42-110) without drawing random numbers.
    ///
    /// The engine draws one uniform number per set and walks the rows accumulating their probabilities; the first row
    /// whose running total exceeds the number wins, and a row with SetStart (or a running total already at 1.0) opens a
    /// new set with a fresh number. A winning row with HasSubSet recurses into the rows after it with a fresh number and
    /// a skip flag; a LOSING row's subset is still walked (to keep the cursor aligned) but its rows are skipped.
    ///
    /// Here the uniform number is carried as an interval and split lazily at each comparison, so every branch keeps its
    /// exact probability weight. The machine advances in lockstep across branches because which row is processed next
    /// never depends on the random number, only on the table.
    /// </summary>
    public static class WieldedTreasureMath
    {
        private sealed class Node
        {
            public double Weight = 1.0;
            public bool Hit;
            public double Lo;
            public double Hi = 1.0;
            public float Probability;
            public bool Rolled;
            public bool Continued;
            public bool SkipNext;
            public int Origin;

            public Node Copy() => (Node)MemberwiseClone();
        }

        /// <summary>
        /// P(at least one generated item satisfies <paramref name="matches"/>). <paramref name="rows"/> must be in the
        /// order the engine reads them (the table's Id order). Returns 0 for a null or empty table.
        /// </summary>
        public static double ProbabilityOfAny(IReadOnlyList<WieldedRow> rows, Func<uint, bool> matches)
        {
            if (rows == null || rows.Count == 0 || matches == null)
                return 0.0;

            var cur = 0;
            var result = Run(new List<Node> { new Node() }, rows, matches, ref cur, false);
            var total = 0.0;

            foreach (var n in result)
                if (n.Hit)
                    total += n.Weight;

            return Math.Clamp(total, 0.0, 1.0);
        }

        private static List<Node> Run(List<Node> nodes, IReadOnlyList<WieldedRow> items, Func<uint, bool> matches, ref int cur, bool skip)
        {
            // Entry: every node starts a fresh frame (rng = a new uniform draw, probability 0, nothing rolled).
            foreach (var n in nodes)
            {
                n.Lo = 0.0;
                n.Hi = 1.0;
                n.Probability = 0.0f;
                n.Rolled = false;
                n.Continued = false;
            }

            for (; cur < items.Count; cur++)
            {
                var item = items[cur];

                if (item.ContinuesPreviousSet)
                {
                    // Lockstep: Continued is the same for every node of a frame.
                    if (!nodes[0].Continued)
                    {
                        cur--;
                        return nodes;
                    }

                    foreach (var n in nodes)
                        n.Continued = false;
                }

                if (!skip)
                {
                    var next = new List<Node>(nodes.Count);

                    foreach (var n in nodes)
                    {
                        if (item.SetStart || n.Probability >= 1.0f)
                        {
                            n.Lo = 0.0;
                            n.Hi = 1.0;
                            n.Probability = 0.0f;
                            n.Rolled = false;
                        }

                        n.Probability += item.Probability;

                        if (n.Rolled)
                        {
                            n.SkipNext = true;
                            next.Add(n);
                            continue;
                        }

                        double p = n.Probability;

                        if (n.Hi <= p)
                        {
                            Win(n, item, matches);
                            next.Add(n);
                        }
                        else if (n.Lo >= p)
                        {
                            n.SkipNext = true;
                            next.Add(n);
                        }
                        else
                        {
                            var span = n.Hi - n.Lo;
                            var yes = n.Copy();
                            yes.Weight = n.Weight * (p - n.Lo) / span;
                            yes.Hi = p;
                            Win(yes, item, matches);

                            n.Weight *= (n.Hi - p) / span;
                            n.Lo = p;
                            n.SkipNext = true;

                            next.Add(yes);
                            next.Add(n);
                        }
                    }

                    nodes = Merge(next);
                }
                else
                {
                    foreach (var n in nodes)
                        n.SkipNext = true;
                }

                if (item.HasSubSet)
                {
                    cur++;
                    var start = cur;
                    var end = cur;
                    var merged = new List<Node>();

                    foreach (var group in new[] { true, false })
                    {
                        var members = nodes.FindAll(n => n.SkipNext == group);

                        if (members.Count == 0)
                            continue;

                        // Children carry the weight and the hit flag; the parent frame is restored afterwards.
                        var children = new List<Node>(members.Count);

                        for (var k = 0; k < members.Count; k++)
                            children.Add(new Node { Weight = members[k].Weight, Hit = members[k].Hit, Origin = k });

                        cur = start;
                        var returned = Run(children, items, matches, ref cur, group);
                        end = cur;

                        foreach (var child in returned)
                        {
                            var parent = members[child.Origin];
                            var restored = parent.Copy();
                            restored.Weight = child.Weight;
                            restored.Hit = child.Hit;
                            restored.Continued = true;
                            merged.Add(restored);
                        }
                    }

                    cur = end;
                    nodes = Merge(merged);
                }
            }

            return nodes;
        }

        /// <summary>
        /// Folds nodes whose future behaviour is identical (same frame state, same hit flag, same origin) into one with the
        /// summed weight. Without this the branch count doubles at every independent set and a 300-row table never finishes.
        /// </summary>
        private static List<Node> Merge(List<Node> nodes)
        {
            var byKey = new Dictionary<(int, bool, double, double, float, bool, bool, bool), Node>();
            var result = new List<Node>(nodes.Count);

            foreach (var n in nodes)
            {
                var key = (n.Origin, n.Hit, n.Lo, n.Hi, n.Probability, n.Rolled, n.Continued, n.SkipNext);

                if (byKey.TryGetValue(key, out var existing))
                    existing.Weight += n.Weight;
                else
                {
                    byKey[key] = n;
                    result.Add(n);
                }
            }

            return result;
        }

        private static void Win(Node n, WieldedRow item, Func<uint, bool> matches)
        {
            n.Rolled = true;
            n.SkipNext = false;

            if (matches(item.Wcid))
                n.Hit = true;
        }
    }
}
