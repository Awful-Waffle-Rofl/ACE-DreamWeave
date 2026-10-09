using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

using ACE.Server.WorldEvents.Defs;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// Process-wide memo for the FAMILY-INDEPENDENT intermediate results
    /// <see cref="DungeonPopulationBuilder.Build"/> recomputes on every run start.
    ///
    /// THE PROBLEM. Build runs on the WORLD THREAD, once per run start, and stage measured it at 67 ms on the
    /// first run after a restart and 17 ms warm. Several runs starting in the same tick stack that cost. Almost
    /// all of it is spent re-deriving things that do not depend on the gem's seed or on which family it happens
    /// to roll: the physical fit projection of the whole species roster, the eligibility-band ladder search, the
    /// cross-family band median health, and - by far the most expensive - the band standard, which builds a
    /// <see cref="DungeonStatProfile"/> (three dictionaries, walking a weenie's skill, body-part and attribute
    /// collections) for every role-0 member of every table.
    ///
    /// WHAT IS CACHED, AND WHAT IS NOT. Only pure functions of (the species tables, the gem LEVEL, the roster
    /// BAND, the band low floor, and the dials that feed them). Nothing that depends on the run's seed, its
    /// drawn family, its pools, its picks or its boss is memoised: the plan is still built entirely by
    /// <see cref="DungeonPopulationBuilder.Build"/> from a fresh <see cref="Random"/> every time, so a given
    /// (seed, inputs) pair yields the identical plan whether the cache was cold or warm. That equality is the
    /// hard requirement and it is pinned by test rather than by this paragraph
    /// (ACE.Server.Tests.ThreadDungeons.ThreadPlanCacheTests).
    ///
    /// EVERY CACHED VALUE IS IMMUTABLE AND IS SHARED BY REFERENCE ACROSS RUNS. <see cref="DungeonStatProfile"/>
    /// and <see cref="DungeonBandStandard"/> are immutable by construction; <see cref="DungeonRosterBand"/> is a
    /// readonly struct; and the <see cref="DungeonFitFilter.FitProjection"/> is a dictionary of tables the
    /// builder only ever READS (every pool it draws is a fresh list - DungeonRosterSelector.PoolFor and friends
    /// allocate). A future edit that mutated a projected table in place would corrupt every later run, so it
    /// must not be made: the projection is a shared snapshot, not scratch space.
    ///
    /// KEYING, AND WHY THE TABLE DICTIONARY IS PART OF THE KEY BY REFERENCE. Both content stores publish by
    /// SWAPPING the whole object (WorldEventManager.Reload assigns a new WorldEventAxisStore;
    /// ThreadDungeonManager.Reload a new ThreadDungeonStore), so a reloaded roster arrives as a DIFFERENT
    /// dictionary instance and every key built from the old one simply stops matching. That makes a content
    /// reload self-invalidating rather than dependent on somebody remembering to call
    /// <see cref="Invalidate"/> - which those paths do call anyway, belt and braces, because the entries the old
    /// reference keys would otherwise sit in the cache until they aged out of the bound.
    ///
    /// THE TUNABLES ARE IN THE KEY, NOT IN AN INVALIDATION HOOK. dynamic_dungeons_roster_band_low/high arrive
    /// as the <see cref="DungeonRosterBand"/>, dynamic_dungeons_band_low_floor as the floor,
    /// dynamic_dungeons_min_eligible_families as the extra term, and the fit filter's switch and headroom
    /// margin are folded into the caller's fit key. An admin who edits one of those gets a cache MISS on the
    /// new value and a HIT again on the old one, which is both correct and cheaper than flushing.
    ///
    /// WHAT STILL NEEDS AN EXPLICIT INVALIDATE: the live WEENIE data behind levelOf/healthOf/profileOf and the
    /// fit predicate. That comes from DatabaseManager.World's weenie cache, which an admin can clear or
    /// repopulate under the server (/clearcache weenie, the /import content path). Those call sites call
    /// <see cref="Invalidate"/>. ACE.Database.WorldDatabaseWithEntityCache is deliberately NOT given a hook of
    /// its own here - ACE.Database does not reference ACE.Server, so the call has to sit on this side.
    ///
    /// BOUNDED, NEVER EVICTING. Each map has a hard entry cap; once full it stops STORING and every further
    /// miss simply recomputes. That is the right failure mode for a cache whose whole job is a saving: it
    /// degrades to today's cost instead of growing without limit or evicting something another run is about to
    /// want. The caps are sized well above the shipped content (see each constant) so the degraded path is not
    /// reachable in practice.
    ///
    /// THREADING. Build runs on the world thread and is the only writer; <see cref="Invalidate"/> can be called
    /// from a command handler on another thread and publishes a brand-new <see cref="State"/> with one
    /// reference write. A run that is mid-Build keeps the snapshot it took at
    /// <see cref="ForRun"/> for the rest of that build, so it can never mix pre- and post-invalidation values
    /// inside one plan.
    /// </summary>
    public static class ThreadPlanCache
    {
        /// <summary>
        /// dynamic_dungeons_plan_cache's compile-time default: ON, per the fork convention that a new server
        /// setting ships enabled. False reproduces today's behaviour exactly - <see cref="ForRun"/> returns
        /// null, the builder takes its pre-cache path, and not one map is read or written.
        ///
        /// Held here rather than only in PropertyManager so the registration and the spawner's read cannot
        /// drift apart, the same contract <see cref="DungeonFitFilter.DefaultFitFilterEnabled"/> uses.
        /// </summary>
        public const bool DefaultPlanCacheEnabled = true;

        /// <summary>
        /// Cap on the per-wcid scalar maps (level, health). The universe is every species member row plus every
        /// bosses.json row - about 1,700 wcids on the shipped content - so this is roughly 19x headroom. An int
        /// or uint per entry: about 0.5 MB if it were ever filled.
        /// </summary>
        public const int MaxScalarWcidEntries = 32768;

        /// <summary>
        /// Cap on the per-wcid <see cref="DungeonStatProfile"/> map. A profile is the expensive entry here - up
        /// to three small dictionaries, call it 1 KB - so the cap is tighter than the scalar one while still
        /// leaving about 4.8x headroom over the shipped roster. About 8 MB if it were ever filled; about 1.7 MB
        /// at the shipped roster size.
        /// </summary>
        public const int MaxProfileEntries = 8192;

        /// <summary>
        /// Cap on each band-scoped map (eligibility band, eligible family count, band median health, band
        /// standard). Keyed on (tables, level, band, floor, threshold, reach-up scale), so the live universe is
        /// one entry per run LEVEL per distinct dial setting. Run levels run 1 to DungeonGemSpec.MaxRunLevel
        /// (500 since the 2026-10-08 ceiling split, up from 375), and above the pivot a reach-up run and a
        /// switched-off one key separately, so 1024 covers the whole ladder twice over with room for a dial
        /// change; the band standard is the heaviest entry at roughly 2 KB, so about 2 MB per map if it were
        /// ever filled.
        /// </summary>
        public const int MaxBandEntries = 1024;

        /// <summary>
        /// Cap on the fit-projection map. One entry per (species tables, dungeon, clearance height, headroom
        /// margin); 13 curated dungeons ship, so this is 2.5x headroom. Each entry is a copy of the whole
        /// species dictionary's SHAPE (a table object and a member list per family, sharing the member rows by
        /// reference), roughly 30 KB - about 1 MB if it were ever filled.
        /// </summary>
        public const int MaxProjectionEntries = 32;

        /// <summary>
        /// One generation of the cache. Replaced wholesale by <see cref="Invalidate"/> rather than cleared in
        /// place, so a build already holding a snapshot finishes against a consistent set of values.
        /// </summary>
        internal sealed class State
        {
            public readonly BoundedMap<uint, int> Levels = new BoundedMap<uint, int>(MaxScalarWcidEntries);
            public readonly BoundedMap<uint, uint> Healths = new BoundedMap<uint, uint>(MaxScalarWcidEntries);
            public readonly BoundedMap<uint, DungeonStatProfile> Profiles = new BoundedMap<uint, DungeonStatProfile>(MaxProfileEntries);
            public readonly BoundedMap<FitKey, DungeonFitFilter.FitProjection> Projections = new BoundedMap<FitKey, DungeonFitFilter.FitProjection>(MaxProjectionEntries);
            public readonly BoundedMap<BandKey, DungeonRosterBand> EligibilityBands = new BoundedMap<BandKey, DungeonRosterBand>(MaxBandEntries);
            public readonly BoundedMap<BandKey, int> FamilyCounts = new BoundedMap<BandKey, int>(MaxBandEntries);
            public readonly BoundedMap<BandKey, uint> BandMedians = new BoundedMap<BandKey, uint>(MaxBandEntries);
            public readonly BoundedMap<BandKey, DungeonBandStandard> BandStandards = new BoundedMap<BandKey, DungeonBandStandard>(MaxBandEntries);
        }

        private static State state = new State();

        /// <summary>
        /// The last value <see cref="ForRun"/> saw for the kill switch, so turning the cache back ON drops
        /// whatever it was holding when it went off. Without this, content edited while the switch was off
        /// would be served from pre-edit entries the moment it came back on: the invalidation hooks run, but
        /// they run against a cache nobody is reading, and the stale entries survive.
        ///
        /// Written only from <see cref="ForRun"/>, which runs on the world thread.
        /// </summary>
        private static bool lastEnabled = DefaultPlanCacheEnabled;

        /// <summary>
        /// Drops every memoised value. Called from the content-reload and weenie-cache-clear paths; safe from
        /// any thread and safe to call while a plan is being built.
        /// </summary>
        public static void Invalidate() => Volatile.Write(ref state, new State());

        /// <summary>
        /// The memo for ONE plan build, or NULL when the cache is switched off - which the builder reads as
        /// "compute everything", its pre-cache path.
        ///
        /// <paramref name="enabled"/> is passed IN rather than read here, so this type stays free of
        /// PropertyManager for exactly the reason <see cref="DungeonFitFilter"/> and
        /// <see cref="DungeonPopulationBuilder"/> are: the tunable read belongs at the spawner, on the world
        /// thread, with every other dial, and every decision below is then exercisable in a unit test with no
        /// database and no live config.
        ///
        /// <paramref name="fitKey"/> is the stable identity of the fit predicate - dungeon id, clearance height
        /// and headroom margin. NULL means "this predicate has no stable identity", under which the projection
        /// is computed and not stored; a delegate alone cannot be a cache key.
        /// </summary>
        public static ThreadPlanMemo ForRun(bool enabled, string fitKey, Func<uint, int> levelOf, Func<uint, uint> healthOf,
            Func<uint, DungeonStatProfile> profileOf)
        {
            if (enabled && !lastEnabled)
                Invalidate();

            lastEnabled = enabled;

            if (!enabled)
                return null;

            return new ThreadPlanMemo(Volatile.Read(ref state), fitKey, levelOf, healthOf, profileOf);
        }

        /// <summary>How many entries each map currently holds. Diagnostics and tests only.</summary>
        public static ThreadPlanCacheCounts Counts
        {
            get
            {
                var s = Volatile.Read(ref state);

                return new ThreadPlanCacheCounts(s.Levels.Count, s.Healths.Count, s.Profiles.Count, s.Projections.Count,
                    s.EligibilityBands.Count, s.FamilyCounts.Count, s.BandMedians.Count, s.BandStandards.Count);
            }
        }

        /// <summary>
        /// A concurrent dictionary with a hard entry cap and an O(1) count.
        ///
        /// ConcurrentDictionary.Count takes every internal lock, so consulting it on each miss would put a
        /// lock convoy on the one path this whole type exists to make cheap. The counter is maintained by the
        /// single writer instead. It can only ever be raced past the cap by the number of threads storing at
        /// once, which is one (the world thread), and a handful of entries over a cap of hundreds would be
        /// harmless anyway - the cap is a bound on growth, not a precise quota.
        /// </summary>
        internal sealed class BoundedMap<TKey, TValue>
        {
            private readonly ConcurrentDictionary<TKey, TValue> map = new ConcurrentDictionary<TKey, TValue>();
            private readonly int max;
            private int count;

            public BoundedMap(int max) => this.max = max;

            public int Count => Volatile.Read(ref count);

            public int Max => max;

            public bool TryGet(TKey key, out TValue value) => map.TryGetValue(key, out value);

            /// <summary>Stores <paramref name="value"/> unless the map is already at its cap. Never evicts.</summary>
            public void TryStore(TKey key, TValue value)
            {
                if (Volatile.Read(ref count) >= max)
                    return;

                if (map.TryAdd(key, value))
                    Interlocked.Increment(ref count);
            }
        }

        /// <summary>
        /// The key for every band-scoped memo: the species tables BY REFERENCE, the gem level, the roster
        /// band's two edges, the band low floor, and one extra integer term (the eligibility threshold, or 0
        /// where the memo has none).
        ///
        /// ONE KEY TYPE, ONE MAP PER MEMO. Two memos can legitimately build the identical key - the band median
        /// and the eligible-family count both key on (tables, level, band) and neither has a floor or a
        /// threshold term - and they do not collide because each lives in its own
        /// <see cref="BoundedMap{TKey,TValue}"/>. A new memo must get its own map for the same reason, never a
        /// shared one distinguished by a value type.
        ///
        /// The tables are compared by REFERENCE rather than by content, deliberately: comparing a 62-table,
        /// 1,668-row dictionary structurally on every lookup would cost more than the computation being
        /// memoised, and both content stores publish a reload by swapping the whole object, so reference
        /// identity is exactly "the same roster".
        ///
        /// THE REACH-UP SCALE IS IN THE KEY (2026-10-08). Above the pivot the builder hands these memos a PROJECTED
        /// levelOf (round(raw x k)), and the same (tables, level, band) is then a different roster: keyed without
        /// k, a run built with dynamic_dungeons_reach_up off would be served the eligibility band, family count,
        /// health median or band standard a reach-up run stored at the same level, or the reverse, for as long as
        /// the cache lived. Keying on k itself rather than on the switch is the stricter form: k is exactly the
        /// input that changes the result, it is 1.0 whenever the projection is off, and at or below the pivot a
        /// switched-on and a switched-off run correctly share one entry because there they ARE the same roster.
        ///
        /// The doubles are compared with double.Equals rather than ==, so a NaN floor - which a limits object
        /// built directly in code can carry - keys consistently instead of never matching itself. The builder
        /// treats NaN as a documented value on that axis, so it must key like one.
        /// </summary>
        internal readonly struct BandKey : IEquatable<BandKey>
        {
            private readonly object tables;
            private readonly int level;
            private readonly double bandLow;
            private readonly double bandHigh;
            private readonly double lowFloor;
            private readonly int extra;
            private readonly double levelScale;

            public BandKey(object tables, int level, DungeonRosterBand band, double lowFloor, int extra, double levelScale = 1.0)
            {
                this.tables = tables;
                this.level = level;
                bandLow = band.Low;
                bandHigh = band.High;
                this.lowFloor = lowFloor;
                this.extra = extra;
                this.levelScale = levelScale;
            }

            public bool Equals(BandKey other)
                => ReferenceEquals(tables, other.tables) && level == other.level && bandLow.Equals(other.bandLow)
                   && bandHigh.Equals(other.bandHigh) && lowFloor.Equals(other.lowFloor) && extra == other.extra
                   && levelScale.Equals(other.levelScale);

            public override bool Equals(object obj) => obj is BandKey other && Equals(other);

            public override int GetHashCode()
                => HashCode.Combine(RuntimeHelpers.GetHashCode(tables), level, bandLow, bandHigh, lowFloor, extra, levelScale);
        }

        /// <summary>
        /// The key for the fit projection: the species tables by reference (same reasoning as
        /// <see cref="BandKey"/>) plus the caller's stable identity for the predicate.
        /// </summary>
        internal readonly struct FitKey : IEquatable<FitKey>
        {
            private readonly object tables;
            private readonly string predicate;

            public FitKey(object tables, string predicate)
            {
                this.tables = tables;
                this.predicate = predicate;
            }

            public bool Equals(FitKey other)
                => ReferenceEquals(tables, other.tables) && string.Equals(predicate, other.predicate, StringComparison.Ordinal);

            public override bool Equals(object obj) => obj is FitKey other && Equals(other);

            public override int GetHashCode()
                => HashCode.Combine(RuntimeHelpers.GetHashCode(tables), predicate == null ? 0 : StringComparer.Ordinal.GetHashCode(predicate));
        }
    }

    /// <summary>Entry counts per map, for diagnostics and for the cache-bound test.</summary>
    public readonly struct ThreadPlanCacheCounts
    {
        public ThreadPlanCacheCounts(int levels, int healths, int profiles, int projections, int eligibilityBands,
            int familyCounts, int bandMedians, int bandStandards)
        {
            Levels = levels;
            Healths = healths;
            Profiles = profiles;
            Projections = projections;
            EligibilityBands = eligibilityBands;
            FamilyCounts = familyCounts;
            BandMedians = bandMedians;
            BandStandards = bandStandards;
        }

        public int Levels { get; }
        public int Healths { get; }
        public int Profiles { get; }
        public int Projections { get; }
        public int EligibilityBands { get; }
        public int FamilyCounts { get; }
        public int BandMedians { get; }
        public int BandStandards { get; }

        public override string ToString()
            => $"levels={Levels} healths={Healths} profiles={Profiles} projections={Projections} " +
               $"eligibilityBands={EligibilityBands} familyCounts={FamilyCounts} bandMedians={BandMedians} bandStandards={BandStandards}";
    }

    /// <summary>
    /// One plan build's view of <see cref="ThreadPlanCache"/>: a snapshot of the cache generation, the fit
    /// predicate's stable identity, and the three live weenie delegates it memoises.
    ///
    /// Every method here is a drop-in for the pure function it wraps and must return exactly what that function
    /// would have returned - that is the determinism contract the whole feature rests on, and the reason none
    /// of these methods does anything but look up, delegate and store.
    ///
    /// <see cref="Hits"/> and <see cref="Lookups"/> count the STRUCTURAL memos only (the projection, the two
    /// band searches, the median and the standard), not the per-wcid ones. That is what makes
    /// <see cref="AllCached"/> mean "every expensive intermediate came from cache" rather than "no weenie was
    /// looked at for the first time this process" - a single new boss row would flip the latter on an otherwise
    /// fully warm run and the number would stop meaning anything.
    /// </summary>
    public sealed class ThreadPlanMemo
    {
        private readonly ThreadPlanCache.State state;
        private readonly string fitKey;
        private readonly Func<uint, int> innerLevelOf;
        private readonly Func<uint, uint> innerHealthOf;
        private readonly Func<uint, DungeonStatProfile> innerProfileOf;

        private int lookups;
        private int hits;

        internal ThreadPlanMemo(ThreadPlanCache.State state, string fitKey, Func<uint, int> levelOf, Func<uint, uint> healthOf,
            Func<uint, DungeonStatProfile> profileOf)
        {
            this.state = state;
            this.fitKey = fitKey;
            innerLevelOf = levelOf;
            innerHealthOf = healthOf;
            innerProfileOf = profileOf;
        }

        /// <summary>Structural memo lookups this build made.</summary>
        public int Lookups => lookups;

        /// <summary>Structural memo lookups this build served from cache.</summary>
        public int Hits => hits;

        /// <summary>True when this build took at least one structural lookup and every one of them hit.</summary>
        public bool AllCached => lookups > 0 && hits == lookups;

        // ---- per-wcid memos ------------------------------------------------------------------------------
        // Not counted in Hits/Lookups; see the class comment. Each is a straight pass-through on a miss, so the
        // value a caller sees is always exactly what the inner delegate returned for that wcid.

        public int LevelOf(uint wcid)
        {
            if (state.Levels.TryGet(wcid, out var cached))
                return cached;

            var value = innerLevelOf(wcid);
            state.Levels.TryStore(wcid, value);

            return value;
        }

        public uint HealthOf(uint wcid)
        {
            if (state.Healths.TryGet(wcid, out var cached))
                return cached;

            var value = innerHealthOf(wcid);
            state.Healths.TryStore(wcid, value);

            return value;
        }

        public DungeonStatProfile ProfileOf(uint wcid)
        {
            if (state.Profiles.TryGet(wcid, out var cached))
                return cached;

            var value = innerProfileOf(wcid);
            state.Profiles.TryStore(wcid, value);

            return value;
        }

        // ---- structural memos ----------------------------------------------------------------------------

        /// <summary>
        /// <see cref="DungeonFitFilter.Project"/>, memoised on (tables, fit key). With no fit key the
        /// projection is computed and discarded, because a bare delegate has no identity to key on.
        /// </summary>
        public DungeonFitFilter.FitProjection Project(IReadOnlyDictionary<string, SpeciesTableDef> species, Func<uint, bool> fits)
        {
            // No key, or a predicate the key cannot describe: compute and discard. A fit key names a dungeon's
            // clearance and margin, so it says nothing about a NULL predicate ("no constraint"), and storing
            // an unconstrained copy under a constrained key would hand a later run the wrong roster.
            if (fitKey == null || fits == null)
                return DungeonFitFilter.Project(species, fits);

            var key = new ThreadPlanCache.FitKey(species, fitKey);
            lookups++;

            if (state.Projections.TryGet(key, out var cached))
            {
                hits++;
                return cached;
            }

            var built = DungeonFitFilter.Project(species, fits);
            state.Projections.TryStore(key, built);

            return built;
        }

        /// <summary><see cref="DungeonRosterSelector.ResolveEligibilityBand"/>, memoised.</summary>
        public DungeonRosterBand ResolveEligibilityBand(IReadOnlyDictionary<string, SpeciesTableDef> tables, int level,
            Func<uint, int> levelOf, DungeonRosterBand natural, double lowFloorRatio, int minEligibleFamilies, double levelScale = 1.0)
        {
            var key = new ThreadPlanCache.BandKey(tables, level, natural, lowFloorRatio, minEligibleFamilies, levelScale);
            lookups++;

            if (state.EligibilityBands.TryGet(key, out var cached))
            {
                hits++;
                return cached;
            }

            var value = DungeonRosterSelector.ResolveEligibilityBand(tables, level, levelOf, natural, lowFloorRatio, minEligibleFamilies);
            state.EligibilityBands.TryStore(key, value);

            return value;
        }

        /// <summary><see cref="DungeonRosterSelector.EligibleFamilyCount"/>, memoised.</summary>
        public int EligibleFamilyCount(IReadOnlyDictionary<string, SpeciesTableDef> tables, int level,
            Func<uint, int> levelOf, DungeonRosterBand band, double levelScale = 1.0)
        {
            var key = new ThreadPlanCache.BandKey(tables, level, band, 0.0, 0, levelScale);
            lookups++;

            if (state.FamilyCounts.TryGet(key, out var cached))
            {
                hits++;
                return cached;
            }

            var value = DungeonRosterSelector.EligibleFamilyCount(tables, level, levelOf, band);
            state.FamilyCounts.TryStore(key, value);

            return value;
        }

        /// <summary><see cref="DungeonRosterSelector.BandMedianHealth"/>, memoised.</summary>
        public uint BandMedianHealth(IReadOnlyDictionary<string, SpeciesTableDef> tables, int level,
            Func<uint, int> levelOf, Func<uint, uint> healthOf, DungeonRosterBand band, double levelScale = 1.0)
        {
            var key = new ThreadPlanCache.BandKey(tables, level, band, 0.0, 0, levelScale);
            lookups++;

            if (state.BandMedians.TryGet(key, out var cached))
            {
                hits++;
                return cached;
            }

            var value = DungeonRosterSelector.BandMedianHealth(tables, level, levelOf, healthOf, band);
            state.BandMedians.TryStore(key, value);

            return value;
        }

        /// <summary><see cref="DungeonBandStandard.Compute"/>, memoised - the most expensive of the five.</summary>
        public DungeonBandStandard BandStandard(IReadOnlyDictionary<string, SpeciesTableDef> tables, int level,
            Func<uint, DungeonStatProfile> profileOf, DungeonRosterBand natural, double lowFloorRatio, double levelScale = 1.0)
        {
            // "No profile delegate" is a DIFFERENT input, not a different value of the same one: Compute
            // returns Empty for it. The key cannot express that, so this path is never stored - otherwise an
            // Empty standard could be served to a later caller that did supply a delegate.
            if (profileOf == null)
                return DungeonBandStandard.Compute(tables, level, null, natural, lowFloorRatio);

            var key = new ThreadPlanCache.BandKey(tables, level, natural, lowFloorRatio, 0, levelScale);
            lookups++;

            if (state.BandStandards.TryGet(key, out var cached))
            {
                hits++;
                return cached;
            }

            var value = DungeonBandStandard.Compute(tables, level, profileOf, natural, lowFloorRatio);
            state.BandStandards.TryStore(key, value);

            return value;
        }
    }
}
