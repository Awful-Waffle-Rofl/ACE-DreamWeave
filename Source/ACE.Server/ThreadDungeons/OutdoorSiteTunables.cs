using System.Collections.Generic;

using ACE.Server.Managers;
using ACE.Server.ThreadDungeons.Defs;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// Defaults and live readers for the outdoor-site (Open World Threads) tunables. The Default* constants
    /// are the single source for both the PropertyManager default rows and the pure code paths (the store's
    /// warning-band lint takes the band as a parameter and defaults it here), so a test never needs
    /// PropertyManager. Every key is default ON; see Content/dungeons/dynamic/outdoor/README.md.
    /// </summary>
    public static class OutdoorSiteTunables
    {
        public const bool DefaultOutdoorEnabled = true;
        public const long DefaultMaxConcurrentRuns = 5;
        public const double DefaultWarningBandM = 15.0;
        public const bool DefaultUnravelEnabled = true;
        public const double DefaultUnravelStartPct = 5.0;
        public const double DefaultUnravelStepPct = 5.0;
        public const bool DefaultBackstopEnabled = true;
        public const bool DefaultRingEnabled = true;
        public const double DefaultRingSpacingM = 8.0;
        public const double DefaultDropMinInsideM = 30.0;
        public const bool DefaultUnravelLethal = false;
        public const double DefaultUnravelGraceS = 10.0;
        public const double DefaultBackstopM = 40.0;
        public const double DefaultContainmentMarginM = 2.0;
        public const double DefaultInteriorAwarenessM = 12.0;
        public const bool DefaultLazyBufferRing = true;

        public const string OutdoorEnabledKey = "dynamic_dungeons_outdoor_enabled";
        public const string MaxConcurrentRunsKey = "dynamic_dungeons_outdoor_max_concurrent_runs";
        public const string WarningBandKey = "dynamic_dungeons_outdoor_warning_band_m";
        public const string UnravelEnabledKey = "dynamic_dungeons_outdoor_unravel_enabled";
        public const string UnravelStartPctKey = "dynamic_dungeons_outdoor_unravel_start_pct";
        public const string UnravelStepPctKey = "dynamic_dungeons_outdoor_unravel_step_pct";
        public const string BackstopEnabledKey = "dynamic_dungeons_outdoor_backstop_enabled";
        public const string RingEnabledKey = "dynamic_dungeons_outdoor_ring_enabled";
        public const string RingSpacingKey = "dynamic_dungeons_outdoor_ring_spacing_m";
        public const string DropMinInsideKey = "dynamic_dungeons_outdoor_drop_min_inside_m";
        public const string UnravelLethalKey = "dynamic_dungeons_outdoor_unravel_lethal";
        public const string UnravelGraceKey = "dynamic_dungeons_outdoor_unravel_grace_s";
        public const string BackstopMKey = "dynamic_dungeons_outdoor_backstop_m";
        public const string ContainmentMarginKey = "dynamic_dungeons_outdoor_containment_margin_m";
        public const string InteriorAwarenessKey = "dynamic_dungeons_outdoor_interior_awareness_m";
        public const string LazyBufferRingKey = "dynamic_dungeons_outdoor_lazy_buffer_ring";

        public static bool OutdoorEnabled => PropertyManager.GetBool(OutdoorEnabledKey).Item;
        public static long MaxConcurrentRuns => PropertyManager.GetLong(MaxConcurrentRunsKey).Item;
        public static double WarningBandM => PropertyManager.GetDouble(WarningBandKey).Item;
        public static bool UnravelEnabled => PropertyManager.GetBool(UnravelEnabledKey).Item;
        public static double UnravelStartPct => PropertyManager.GetDouble(UnravelStartPctKey).Item;
        public static double UnravelStepPct => PropertyManager.GetDouble(UnravelStepPctKey).Item;
        public static bool BackstopEnabled => PropertyManager.GetBool(BackstopEnabledKey).Item;
        public static bool RingEnabled => PropertyManager.GetBool(RingEnabledKey).Item;
        public static double RingSpacingM => PropertyManager.GetDouble(RingSpacingKey).Item;
        public static double DropMinInsideM => PropertyManager.GetDouble(DropMinInsideKey).Item;
        public static bool UnravelLethal => PropertyManager.GetBool(UnravelLethalKey).Item;
        public static double UnravelGraceS => PropertyManager.GetDouble(UnravelGraceKey).Item;
        public static double BackstopM => PropertyManager.GetDouble(BackstopMKey).Item;
        public static double ContainmentMarginM => PropertyManager.GetDouble(ContainmentMarginKey).Item;
        public static double InteriorAwarenessM => PropertyManager.GetDouble(InteriorAwarenessKey).Item;    }

    /// <summary>
    /// The rotation predicate: may this index row be drawn right now? Pure. Dungeon rows are never affected
    /// (they always pass here; their own Enabled/level checks stay with the caller).
    /// </summary>
    public static class OutdoorRotation
    {
        /// <param name="liveOutdoorRuns">Outdoor-kind runs currently live.</param>
        /// <param name="maxConcurrent">dynamic_dungeons_outdoor_max_concurrent_runs; an outdoor row is dropped
        /// once liveOutdoorRuns >= maxConcurrent; a value of 0 (or less) means UNLIMITED, as for dynamic_dungeons_max_concurrent_runs.</param>
        public static bool IsEligible(DungeonEntryDef entry, bool outdoorEnabled, int liveOutdoorRuns, long maxConcurrent)
        {
            if (entry == null) return false;
            if (!entry.IsOutdoor) return true;
            if (!outdoorEnabled) return false;
            return maxConcurrent <= 0 || liveOutdoorRuns < maxConcurrent;
        }

        /// <summary>
        /// The rows an "any" gem of <paramref name="level"/> may draw: enabled, level in range, and
        /// <see cref="IsEligible"/>. Pure; ThreadDungeonGemHandler.ResolveDungeon feeds it the store's rows and
        /// then picks one with the spec's seeded Random.
        /// </summary>
        public static List<DungeonEntryDef> Candidates(IEnumerable<DungeonEntryDef> rows, int level, bool outdoorEnabled, int liveOutdoorRuns, long maxConcurrent)
        {
            var result = new List<DungeonEntryDef>();
            foreach (var d in rows)
                if (d.Enabled && level >= d.MinLevel && level <= d.MaxLevel && IsEligible(d, outdoorEnabled, liveOutdoorRuns, maxConcurrent))
                    result.Add(d);
            return result;
        }

        /// <summary>
        /// Picks one row from <paramref name="candidates"/> (already level- and eligibility-filtered, in store order):
        /// ONE uniform draw over the whole list, outdoor sites, cities and dungeons alike, so every eligible row has the
        /// same odds whatever its kind. Null for an empty pool.
        /// </summary>
        public static DungeonEntryDef Pick(IReadOnlyList<DungeonEntryDef> candidates, System.Random rng)
        {
            if (candidates == null || candidates.Count == 0) return null;
            return candidates[rng.Next(candidates.Count)];
        }

        /// <summary>
        /// The whole gem resolution, pure: a gem bound to an id gets that row or null (never re-rolled); an "any" gem
        /// draws one row uniformly from <see cref="Candidates"/> with <see cref="Pick"/> using Random(seed).
        /// </summary>
        public static DungeonEntryDef Resolve(string dungeonId, IReadOnlyDictionary<string, DungeonEntryDef> rows, int level, int seed,
            bool outdoorEnabled, int liveOutdoorRuns, long maxConcurrent)
        {
            if (dungeonId != DungeonGemSpec.Any)
            {
                rows.TryGetValue(dungeonId, out var bound);
                return Bound(bound, outdoorEnabled, liveOutdoorRuns, maxConcurrent);
            }
            return Pick(Candidates(rows.Values, level, outdoorEnabled, liveOutdoorRuns, maxConcurrent), new System.Random(seed));
        }

        /// <summary>
        /// The gem-use entry point (WP3a orchestrator default, 2026-10-20): <see cref="Resolve"/> with outdoor rows turned off
        /// for a Thread-Guide gem, so a guide run never rolls an outdoor site and new players always get dungeons. With outdoor
        /// off, Candidates holds dungeons only and Pick draws uniformly among them. Non-guide gems draw from every eligible row.
        /// </summary>
        public static DungeonEntryDef ResolveForGem(bool isGuide, string dungeonId, IReadOnlyDictionary<string, DungeonEntryDef> rows, int level, int seed,
            bool outdoorEnabled, int liveOutdoorRuns, long maxConcurrent)
            => Resolve(dungeonId, rows, level, seed, outdoorEnabled && !isGuide, liveOutdoorRuns, maxConcurrent);

        /// <summary>A gem bound to a specific row: the row itself when eligible, else null (a disabled outdoor kill switch refuses it).</summary>
        public static DungeonEntryDef Bound(DungeonEntryDef bound, bool outdoorEnabled, int liveOutdoorRuns, long maxConcurrent)
            => bound != null && IsEligible(bound, outdoorEnabled, liveOutdoorRuns, maxConcurrent) ? bound : null;
    }
}
