using System;
using System.Collections.Generic;
using System.Numerics;

namespace ACE.Server.HazardMechanics
{
    /// <summary>
    /// The pillars that are burning, and when each one strikes (shared invariant 6: pure, no world, no clock of its own).
    /// A pillar is added when it starts; every driver tick asks <see cref="Due"/> which of them deal damage THIS tick.
    ///
    /// RULES, each one tested:
    ///   - a pillar strikes on every tick inside [start, start + duration);
    ///   - a pillar whose whole life fits between two ticks (burst shorter than the tick) still strikes EXACTLY ONCE, on
    ///     the first tick that sees it, then expires;
    ///   - a pillar whose source is dead never strikes and is dropped;
    ///   - a pillar with no source is never added (damage nobody can see is unfair).
    /// </summary>
    public sealed class WispBurnBook
    {
        public sealed class Entry
        {
            /// <summary>The effect host that is the damage source (an opaque handle: the book only asks whether it is alive).</summary>
            public object Source { get; internal set; }

            public Vector3 Centre { get; internal set; }

            public double Start { get; internal set; }

            public double Until { get; internal set; }

            internal bool Struck { get; set; }

            /// <summary>Set by <see cref="StrikeFirst"/>: the next <see cref="Due"/> leaves this entry out once, as it was struck in that tick already.</summary>
            internal bool SkipNextDue { get; set; }
        }

        private readonly List<Entry> entries = new List<Entry>();

        public int Count => entries.Count;

        /// <summary>Adds a pillar; returns its entry (null for a sourceless one, which is never added).</summary>
        public Entry Add(object source, Vector3 centre, double start, double duration)
        {
            if (source == null)
                return null;

            var entry = new Entry { Source = source, Centre = centre, Start = start, Until = start + Math.Max(0.0, duration) };
            entries.Add(entry);
            return entry;
        }

        /// <summary>
        /// A delayed pillar's first strike, run from a scheduled step rather than from <see cref="Due"/>: true when it
        /// should strike now (alive and not struck yet). The step runs before the host's own destroy step in the same tick,
        /// so a late tick past the whole burst still strikes once. Due then leaves the entry out for the rest of this tick.
        /// </summary>
        public bool StrikeFirst(Entry entry, Func<object, bool> alive)
        {
            if (entry == null || entry.Struck || !entries.Contains(entry) || !alive(entry.Source))
                return false;

            entry.Struck = true;
            entry.SkipNextDue = true;
            return true;
        }

        /// <summary>The pillars that strike at <paramref name="now"/>. Expired and dead ones are removed.</summary>
        public IReadOnlyList<Entry> Due(double now, Func<object, bool> alive)
        {
            var strikes = new List<Entry>();

            for (var i = entries.Count - 1; i >= 0; i--)
            {
                var e = entries[i];

                if (!alive(e.Source))
                {
                    entries.RemoveAt(i);
                    continue;
                }

                if (e.SkipNextDue)
                {
                    e.SkipNextDue = false;

                    if (now >= e.Until)
                        entries.RemoveAt(i);

                    continue;
                }

                if (now < e.Start)
                    continue;

                if (now < e.Until)
                {
                    e.Struck = true;
                    strikes.Add(e);
                    continue;
                }

                // the burst was over before this tick saw it: one strike, never zero, never two
                if (!e.Struck)
                {
                    e.Struck = true;
                    strikes.Add(e);
                }

                entries.RemoveAt(i);
            }

            strikes.Reverse(); // entries were walked backwards; report in the order they were added
            return strikes;
        }
    }
}
