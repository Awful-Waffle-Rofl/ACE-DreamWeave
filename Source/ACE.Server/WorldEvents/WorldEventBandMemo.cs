using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

using ACE.Server.ThreadDungeons;
using ACE.Server.WorldEvents.Defs;

namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// World Events' own memo of the band standard (<see cref="DungeonBandStandard"/>) and the band median
    /// health, plus the per-wcid stat profiles they are built from. WE gets its own memo rather than reusing
    /// ThreadPlanCache because that cache clears itself when the Threads on/off switch flips and is sized for
    /// Threads' key space; sharing it would couple two systems' lifetimes.
    ///
    /// A standard is a pure function of (species tables, slot level, band edges, low floor), and building one
    /// walks every role-0 member of every table once per widening step, so an uncached wave of 40 slots
    /// would redo that walk per slot. The key holds the species tables BY REFERENCE: a catalog or axis
    /// reload publishes a new dictionary, so nothing keyed on the old one can match it.
    ///
    /// THREADING. The world thread is the only writer (wave picks and spawn construction both run there).
    /// <see cref="Clear"/> may be called from any thread (a reload command) and swaps in a fresh generation
    /// with a volatile write rather than clearing in place, so a world-thread writer mid-update lands in the
    /// discarded generation and never corrupts the new one.
    ///
    /// BOUNDED. Each map holds at most <see cref="MaxEntries"/> (standards, health) or
    /// <see cref="MaxProfiles"/> (profiles) entries; one that reaches its cap is emptied wholesale, which is
    /// simpler than eviction and correct because every entry is recomputable.
    /// </summary>
    public static class WorldEventBandMemo
    {
        public const int MaxEntries = 512;
        public const int MaxProfiles = 4096;

        private readonly struct Key : IEquatable<Key>
        {
            private readonly object tables;
            private readonly int level;
            private readonly double low;
            private readonly double high;
            private readonly double floor;
            private readonly int maxHighEdge;

            public Key(object tables, int level, double low, double high, double floor, int maxHighEdge)
            {
                this.tables = tables;
                this.level = level;
                this.low = low;
                this.high = high;
                this.floor = floor;
                this.maxHighEdge = maxHighEdge;
            }

            public bool Equals(Key other)
                => ReferenceEquals(tables, other.tables) && level == other.level && low.Equals(other.low)
                   && high.Equals(other.high) && floor.Equals(other.floor) && maxHighEdge == other.maxHighEdge;

            public override bool Equals(object obj) => obj is Key other && Equals(other);

            public override int GetHashCode() => HashCode.Combine(RuntimeHelpers.GetHashCode(tables), level, low, high, floor, maxHighEdge);
        }

        private sealed class State
        {
            public readonly Dictionary<Key, DungeonBandStandard> Standards = new Dictionary<Key, DungeonBandStandard>();
            public readonly Dictionary<Key, uint> Health = new Dictionary<Key, uint>();
            public readonly Dictionary<uint, DungeonStatProfile> Profiles = new Dictionary<uint, DungeonStatProfile>();
        }

        private static State state = new State();

        /// <summary>Drops every memoized entry. Safe from any thread.</summary>
        public static void Clear() => Volatile.Write(ref state, new State());

        /// <summary>Entry counts, for tests and diagnostics: (standards, health, profiles).</summary>
        public static (int Standards, int Health, int Profiles) Counts
        {
            get
            {
                var s = Volatile.Read(ref state);
                return (s.Standards.Count, s.Health.Count, s.Profiles.Count);
            }
        }

        /// <summary>
        /// One wcid's stat profile, resolved once per generation through <paramref name="resolve"/>.
        /// </summary>
        public static DungeonStatProfile ProfileOf(uint wcid, Func<uint, DungeonStatProfile> resolve)
        {
            var s = Volatile.Read(ref state);

            if (s.Profiles.TryGetValue(wcid, out var cached))
                return cached;

            var profile = resolve(wcid) ?? DungeonStatProfile.Empty;

            if (s.Profiles.Count >= MaxProfiles)
                s.Profiles.Clear();

            s.Profiles[wcid] = profile;

            return profile;
        }

        /// <summary>
        /// The band standard for a slot level under WE's band. The sample band is
        /// [floor(L * <paramref name="low"/>), min(<paramref name="maxHighEdge"/>, ceil(L * <paramref name="high"/>))]
        /// and widens its low edge down to <paramref name="lowFloor"/> exactly as Threads' does.
        /// </summary>
        public static DungeonBandStandard StandardFor(IReadOnlyDictionary<string, SpeciesTableDef> tables, int level,
            double low, double high, double lowFloor, Func<uint, DungeonStatProfile> resolveProfile,
            int maxHighEdge = WorldEventRosterSelector.MaxConsideredLevel)
        {
            if (tables == null || resolveProfile == null)
                return DungeonBandStandard.Empty;

            var s = Volatile.Read(ref state);
            var key = new Key(tables, level, low, high, lowFloor, maxHighEdge);

            if (s.Standards.TryGetValue(key, out var cached))
                return cached;

            var value = DungeonBandStandard.Compute(tables, level, w => ProfileOf(w, resolveProfile),
                new DungeonRosterBand(low, high), lowFloor, maxHighEdge: maxHighEdge);

            if (s.Standards.Count >= MaxEntries)
                s.Standards.Clear();

            s.Standards[key] = value;

            return value;
        }

        /// <summary>
        /// DungeonRosterSelector.BandMedianHealth over WE's band, memoized. 0 means "no data / no floor".
        /// </summary>
        public static uint MedianHealthFor(IReadOnlyDictionary<string, SpeciesTableDef> tables, int level,
            double low, double high, Func<uint, DungeonStatProfile> resolveProfile,
            int maxHighEdge = WorldEventRosterSelector.MaxConsideredLevel)
        {
            if (tables == null || resolveProfile == null)
                return 0;

            var s = Volatile.Read(ref state);
            var key = new Key(tables, level, low, high, 0.0, maxHighEdge);

            if (s.Health.TryGetValue(key, out var cached))
                return cached;

            var value = DungeonRosterSelector.BandMedianHealth(tables, level,
                w => ProfileOf(w, resolveProfile).Level, w => ProfileOf(w, resolveProfile).Health,
                new DungeonRosterBand(low, high), maxHighEdge);

            if (s.Health.Count >= MaxEntries)
                s.Health.Clear();

            s.Health[key] = value;

            return value;
        }
    }
}
