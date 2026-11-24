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
    /// health, plus the per-wcid stat profiles they are built from, and the damage ceiling's per-slot-level
    /// baseline (<see cref="DamageCeilingFor"/>, WorldEventDamageCeiling). WE gets its own memo rather than reusing
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

            // The stat-curve side (world_events_stat_curve). Separate maps so StandardFor/MedianHealthFor and
            // their Counts are exactly what they were before the curve existed.
            public readonly Dictionary<Key, DungeonBandStandard> CurveStandards = new Dictionary<Key, DungeonBandStandard>();
            public readonly Dictionary<Key, uint> CurveHealth = new Dictionary<Key, uint>();
            public readonly Dictionary<object, CurveAnchor> Curves = new Dictionary<object, CurveAnchor>(ReferenceComparer.Instance);

            // The damage ceiling (world_events_damage_ceiling): one whole baseline per (tables, slot level).
            public readonly Dictionary<Key, DamageCeilingBaseline> DamageCeilings = new Dictionary<Key, DamageCeilingBaseline>();

            // (tables, slot level) pairs whose baseline compute THREW this generation: tried once, never retried until
            // Clear, and never stored as an Empty baseline (a failure is not "no median").
            public readonly HashSet<Key> FailedDamageCeilings = new HashSet<Key>();

            // The damage ceiling's planning cache: ONE WE-owned scratch ThreadPlanMemo per species-table load, so every
            // slot level shares the roster classification, the per-wcid reads and the band measurements the Threads
            // construction makes. Replaced when the tables reference changes; dropped with the generation by Clear.
            public object PlanMemoTables;
            public ThreadPlanMemo PlanMemo;
        }

        /// <summary>One species-table generation's pivot and anchored curve (null curve = no pivot).</summary>
        public sealed class CurveAnchor
        {
            public CurveAnchor(int pivot, DungeonStatCurve.Anchored curve)
            {
                Pivot = pivot;
                Curve = curve;
            }

            /// <summary>floor(AuthoredTopLevel / Threads' band high); 0 = no pivot.</summary>
            public int Pivot { get; }

            /// <summary>The Threads construction anchored at <see cref="Pivot"/>; null when there is no pivot.</summary>
            public DungeonStatCurve.Anchored Curve { get; }
        }

        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();

            public new bool Equals(object x, object y) => ReferenceEquals(x, y);

            public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
        }

        /// <summary>Bound on <see cref="CurveAnchorFor"/>'s map (one entry per species-table generation).</summary>
        public const int MaxCurves = 16;

        // ---- the Threads COMPILED DEFAULTS the WE curve is built from (never a live dynamic_dungeons_* value) ----

        /// <summary>Threads' natural band high edge: the pivot is floor(AuthoredTopLevel / this).</summary>
        public const double CurveBandHigh = DungeonPopulationLimits.DefaultRosterBandHigh;

        /// <summary>Threads' natural band low edge, for the anchor's sample.</summary>
        public const double CurveBandLow = DungeonPopulationLimits.DefaultRosterBandLow;

        /// <summary>Threads' adaptive-band low floor, for the anchor's sample.</summary>
        public const double CurveLowFloor = DungeonPopulationLimits.DefaultBandLowFloorRatio;

        public const double CurveDefenseRate = DungeonPopulationLimits.DefaultDefenseCurveRateAbove375;
        public const double CurveMagicDefenseRate = DungeonPopulationLimits.DefaultMagicDefenseCurveRateAbove300;
        public const double CurveDamageRate = DungeonPopulationLimits.DefaultDamageCurveRateAbovePivot;

        /// <summary>
        /// World Events does NOT take the Threads 400 knee (owner ruling 2026-11-17 softens Threads only). Weapon attack above
        /// 400 keeps its unsoftened slope (1.0), and melee defense above 400 keeps the 0.5 defense rate it had (the knee rate
        /// REPLACES the defense rate there, so passing <see cref="CurveDefenseRate"/> reproduces the old curve exactly).
        /// </summary>
        public const double CurveWeaponAttackRateAbove400 = 1.0;
        public const double CurveMeleeDefenseRateAbove400 = CurveDefenseRate;

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

        /// <summary>The stat-curve maps' entry counts, for tests and diagnostics: (standards, health, curves).</summary>
        public static (int Standards, int Health, int Curves) CurveCounts
        {
            get
            {
                var s = Volatile.Read(ref state);
                return (s.CurveStandards.Count, s.CurveHealth.Count, s.Curves.Count);
            }
        }

        /// <summary>The damage ceiling map's entry count, for tests and diagnostics.</summary>
        public static int DamageCeilingCount => Volatile.Read(ref state).DamageCeilings.Count;

        /// <summary>How many (tables, level) baseline computes failed this generation, for tests and diagnostics.</summary>
        public static int FailedDamageCeilingCount => Volatile.Read(ref state).FailedDamageCeilings.Count;

        /// <summary>
        /// The damage ceiling's baseline for a slot level (WorldEventDamageCeiling), memoized by (tables REFERENCE,
        /// level). <paramref name="compute"/> runs on a miss and must be the pure function of exactly those two inputs
        /// (WorldEventDamageCeiling.ComputeLive: the Threads construction at compiled default limits). Every result is
        /// kept, an empty one included, as ThreadPlanCache keeps the Threads baseline. World thread only, like every map
        /// here; a command must call WorldEventDamageCeiling directly instead.
        ///
        /// A compute that THROWS is recorded in a per-generation failed set and the exception propagates (the caller logs it
        /// once); every later call for that (tables, level) in the same generation returns NULL without computing - "no
        /// baseline", which the ceiling reads as "do not cap" - so a wave does not pay, or log, the failure once per
        /// creature. The failure is never stored as an Empty baseline, and <see cref="Clear"/> allows a retry.
        /// </summary>
        public static DamageCeilingBaseline DamageCeilingFor(IReadOnlyDictionary<string, SpeciesTableDef> tables, int level, Func<DamageCeilingBaseline> compute)
        {
            if (tables == null || compute == null || level <= 0)
                return DamageCeilingBaseline.Empty;

            var s = Volatile.Read(ref state);
            var key = new Key(tables, level, 0.0, 0.0, 0.0, 0);

            if (s.DamageCeilings.TryGetValue(key, out var cached))
                return cached;

            if (s.FailedDamageCeilings.Contains(key))
                return null;

            DamageCeilingBaseline value;

            try
            {
                value = compute() ?? DamageCeilingBaseline.Empty;
            }
            catch
            {
                if (s.FailedDamageCeilings.Count >= MaxEntries)
                    s.FailedDamageCeilings.Clear();

                s.FailedDamageCeilings.Add(key);
                throw;
            }

            if (s.DamageCeilings.Count >= MaxEntries)
                s.DamageCeilings.Clear();

            s.DamageCeilings[key] = value;

            return value;
        }

        /// <summary>
        /// The damage ceiling's planning cache for one species-table load (see State.PlanMemo): the memo
        /// <paramref name="create"/> built for these <paramref name="tables"/> (by REFERENCE), or a fresh one when the
        /// tables changed or none exists yet. It never touches ThreadPlanCache's shared generation (it is a
        /// ThreadPlanMemo.Scratch). World thread only, like every map here; null for null input.
        /// </summary>
        public static ThreadPlanMemo DamageCeilingPlanMemoFor(IReadOnlyDictionary<string, SpeciesTableDef> tables, Func<ThreadPlanMemo> create)
        {
            if (tables == null || create == null)
                return null;

            var s = Volatile.Read(ref state);

            if (s.PlanMemo != null && ReferenceEquals(s.PlanMemoTables, tables))
                return s.PlanMemo;

            s.PlanMemo = create();
            s.PlanMemoTables = s.PlanMemo != null ? tables : null;

            return s.PlanMemo;
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

        // ---- the stat curve (world_events_stat_curve) ------------------------------------------------------
        //
        // Above the pivot World Events follow the SAME extrapolated curve Threads uses: the measured standard at the
        // pivot, built exactly as DungeonPopulationBuilder builds the Threads anchor (Threads' natural band and low
        // floor, k = 1), carried up by DungeonStatCurve's fitted slopes at the Threads COMPILED DEFAULT rates. The
        // pivot is floor(AuthoredTopLevel / Threads' band high) over WE's own species tables. From 301 to the pivot the
        // WE standard keeps WE's own band and only its magic defense follows the retail curve (as Threads does at
        // 301..pivot). At or below 300, or with the dial off, the result is StandardFor's own object, unchanged.

        /// <summary>
        /// The pivot and anchored curve for one species-table generation, memoized by table REFERENCE. Pure over
        /// (tables, profiles): the anchor is DungeonBandStandard.Compute(tables, pivot, profile,
        /// [<see cref="CurveBandLow"/>, <see cref="CurveBandHigh"/>], <see cref="CurveLowFloor"/>), the Threads
        /// construction. Null for null inputs; a roster with no role-0 member yields pivot 0 and a null curve.
        /// </summary>
        public static CurveAnchor CurveAnchorFor(IReadOnlyDictionary<string, SpeciesTableDef> tables, Func<uint, DungeonStatProfile> resolveProfile)
        {
            if (tables == null || resolveProfile == null)
                return null;

            var s = Volatile.Read(ref state);

            if (s.Curves.TryGetValue(tables, out var cached))
                return cached;

            var pivot = DungeonPopulationBuilder.ReachUpPivot(
                DungeonPopulationBuilder.AuthoredTopLevel(tables, w => ProfileOf(w, resolveProfile).Level), CurveBandHigh);

            DungeonStatCurve.Anchored curve = null;

            if (pivot > 0)
            {
                var anchor = DungeonBandStandard.Compute(tables, pivot, w => ProfileOf(w, resolveProfile),
                    new DungeonRosterBand(CurveBandLow, CurveBandHigh), DungeonPopulationBuilder.SanitizedBandLowFloor(CurveLowFloor));

                curve = new DungeonStatCurve.Anchored(anchor, pivot, CurveDefenseRate, CurveMagicDefenseRate, CurveDamageRate,
                    CurveWeaponAttackRateAbove400, CurveMeleeDefenseRateAbove400);
            }

            var value = new CurveAnchor(pivot, curve);

            if (s.Curves.Count >= MaxCurves)
                s.Curves.Clear();

            s.Curves[tables] = value;

            return value;
        }

        /// <summary>
        /// Which construction <see cref="CurveStandardFor"/> uses at <paramref name="level"/> (pure): Measured
        /// (StandardFor unchanged), RetailMagicDefense (StandardFor with retail magic defense), or Curve.
        /// </summary>
        public enum CurveRegime
        {
            Measured,
            RetailMagicDefense,
            Curve,
        }

        /// <summary>The regime decision (pure, so a test can pin every boundary).</summary>
        public static CurveRegime RegimeFor(bool statCurve, int level, int pivot)
        {
            if (!statCurve || level <= DungeonStatCurve.DefenseSoftenLevel)
                return CurveRegime.Measured;

            return pivot > 0 && level > pivot ? CurveRegime.Curve : CurveRegime.RetailMagicDefense;
        }

        /// <summary>
        /// The band standard World Events uplift and guard against when the stat-curve dial may be on. With
        /// <paramref name="statCurve"/> off, or at or below level 300, this IS <see cref="StandardFor"/> (the same
        /// object). From 301 to the pivot: StandardFor with its magic defense replaced by the retail curve. Above the
        /// pivot: the Threads curve's standard at <paramref name="level"/> (see <see cref="CurveAnchorFor"/>).
        /// </summary>
        public static DungeonBandStandard CurveStandardFor(IReadOnlyDictionary<string, SpeciesTableDef> tables, int level,
            double low, double high, double lowFloor, Func<uint, DungeonStatProfile> resolveProfile, bool statCurve,
            int maxHighEdge = WorldEventRosterSelector.MaxConsideredLevel)
        {
            if (tables == null || resolveProfile == null)
                return DungeonBandStandard.Empty;

            if (RegimeFor(statCurve, level, int.MaxValue) == CurveRegime.Measured)
                return StandardFor(tables, level, low, high, lowFloor, resolveProfile, maxHighEdge);

            var s = Volatile.Read(ref state);
            var key = new Key(tables, level, low, high, lowFloor, maxHighEdge);

            if (s.CurveStandards.TryGetValue(key, out var cached))
                return cached;

            var anchor = CurveAnchorFor(tables, resolveProfile);

            var value = RegimeFor(true, level, anchor?.Pivot ?? 0) == CurveRegime.Curve
                ? anchor.Curve.StandardAt(level)
                : DungeonStatCurve.WithRetailMagicDefense(StandardFor(tables, level, low, high, lowFloor, resolveProfile, maxHighEdge),
                    level, CurveMagicDefenseRate);

            if (s.CurveStandards.Count >= MaxEntries)
                s.CurveStandards.Clear();

            s.CurveStandards[key] = value;

            return value;
        }

        /// <summary>
        /// The health floor World Events uplift to when the stat-curve dial may be on. Above the pivot (dial on):
        /// max(the rounded Threads health curve at <paramref name="level"/> (DungeonHealthCurve.Target at its compiled
        /// default anchors and top level), <see cref="MedianHealthFor"/>) - upward-only relative to the band median;
        /// everywhere else exactly <see cref="MedianHealthFor"/>. 0 = no floor.
        /// </summary>
        public static uint CurveMedianHealthFor(IReadOnlyDictionary<string, SpeciesTableDef> tables, int level,
            double low, double high, Func<uint, DungeonStatProfile> resolveProfile, bool statCurve,
            int maxHighEdge = WorldEventRosterSelector.MaxConsideredLevel)
        {
            if (tables == null || resolveProfile == null)
                return 0;

            if (RegimeFor(statCurve, level, int.MaxValue) == CurveRegime.Measured)
                return MedianHealthFor(tables, level, low, high, resolveProfile, maxHighEdge);

            var anchor = CurveAnchorFor(tables, resolveProfile);

            if (RegimeFor(true, level, anchor?.Pivot ?? 0) != CurveRegime.Curve)
                return MedianHealthFor(tables, level, low, high, resolveProfile, maxHighEdge);

            var s = Volatile.Read(ref state);
            var key = new Key(tables, level, low, high, 0.0, maxHighEdge);

            if (s.CurveHealth.TryGetValue(key, out var cached))
                return cached;

            // Upward-only relative to the band median (orchestrator ruling): the curve takes over only where it is
            // HIGHER than what MedianHealthFor gives this slot, so a slot just above the pivot (whose WE band still
            // reaches the authored top) keeps its median, and a slot with no median (0, above the roster) gets the curve.
            var value = Math.Max(CurveHealthTarget(level), MedianHealthFor(tables, level, low, high, resolveProfile, maxHighEdge));

            if (s.CurveHealth.Count >= MaxEntries)
                s.CurveHealth.Clear();

            s.CurveHealth[key] = value;

            return value;
        }

        /// <summary>
        /// The Threads health curve at <paramref name="level"/> at its COMPILED defaults, rounded (pure). 0 when the
        /// compiled default switch is off or the level is not positive.
        /// </summary>
        public static uint CurveHealthTarget(int level)
        {
            // A local, not the constant itself, so the compiled-default switch reads as a branch rather than dead code.
            var enabled = DungeonPopulationLimits.DefaultHealthCurveEnabled;

            if (!enabled)
                return 0;

            var target = DungeonHealthCurve.Target(level, DungeonPopulationLimits.DefaultHealthCurveAnchorLow,
                DungeonPopulationLimits.DefaultHealthCurveAnchorHigh, DungeonPopulationLimits.DefaultHealthCurveTopLevel);

            return (uint)Math.Clamp(Math.Round(target), 0, int.MaxValue);
        }
    }
}
