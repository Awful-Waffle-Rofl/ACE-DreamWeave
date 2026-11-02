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
    /// Any OBJECT passed into a memoised measure (a roster classifier, a delegate's configuration) contributes its
    /// own inputs to the key too: a key that names only the measure's direct arguments serves one roster's answer to
    /// another roster built from different tunables (code review 2026-10-23, the archetype standards' roster floor).
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

            // The archetype uplift (dynamic_dungeons_archetype_uplift). Read only by a plan with an uplifted entry and the
            // switch on, so a run that tags nothing never touches these maps.
            public readonly BoundedMap<uint, DungeonStatProfile> ArchetypeProfiles = new BoundedMap<uint, DungeonStatProfile>(MaxProfileEntries);
            public readonly BoundedMap<BandKey, RosterArchetypes> Rosters = new BoundedMap<BandKey, RosterArchetypes>(MaxRosterEntries);
            public readonly BoundedMap<BandKey, DungeonArchetypeStandards> ArchetypeStandards = new BoundedMap<BandKey, DungeonArchetypeStandards>(MaxBandEntries);

            // The damage ceiling (dynamic_dungeons_damage_ceiling). Read only by a plan built with the switch on and a
            // per-hit delegate, so a run without either never touches these maps. The per-wcid read, and the whole baseline
            // (code review 2026-10-26, F5 option b) keyed on the tables, the level and the FULL limits value (BaselineKey),
            // so it can never be served under a key that misses one of the dials it reads.
            // Code review 2026-10-26 F2: these two CLEAR and start over at their cap (logged once) instead of silently
            // ceasing to cache, so a long-lived server that fills one keeps paying the miss cost once per entry, not forever.
            public readonly BoundedMap<uint, CreaturePerHit> PerHits = new BoundedMap<uint, CreaturePerHit>(MaxProfileEntries, "PerHits");
            public readonly BoundedMap<BaselineKey, DamageCeilingBaseline> DamageCeilingBaselines = new BoundedMap<BaselineKey, DamageCeilingBaseline>(MaxBandEntries, "DamageCeilingBaselines");
        }

        /// <summary>
        /// Cap on the roster-classifier map: one entry per (species tables, roster band, band low floor), so the live
        /// universe is one per distinct dial setting. Each entry memoises up to one classification per roster wcid and
        /// one cross-archetype standard per authored level, roughly 1 MB at the shipped roster.
        /// </summary>
        public const int MaxRosterEntries = 16;

        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

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

        /// <summary>The last archetype key <see cref="ForRun"/> saw (the spell strip's dials). World thread only.</summary>
        private static string lastArchetypeKey;

        /// <summary>
        /// Drops every memoised value. Called from the content-reload and weenie-cache-clear paths; safe from
        /// any thread and safe to call while a plan is being built.
        /// </summary>
        public static void Invalidate()
        {
            Volatile.Write(ref state, new State());

            // The archetype uplift's live resolver memoises spell and wielded-treasure reductions read off the same world
            // cache, so it is dropped with everything else.
            ArchetypeFactsResolver.ResetLive();
        }

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
            Func<uint, DungeonStatProfile> profileOf, Func<uint, DungeonStatProfile> archetypeProfileOf = null, string archetypeKey = null,
            Func<uint, CreaturePerHit> perHitOf = null)
        {
            if (enabled && !lastEnabled)
                Invalidate();

            // The archetype maps hold classifications read off the post-strip spell book, so a changed strip setting
            // (archetypeKey, SpellStripConfig.Key) must not be served the old ones. A dial change is rare; the whole
            // generation is dropped rather than tracking which map depends on it.
            if (enabled && archetypeKey != null)
            {
                if (lastArchetypeKey != null && lastArchetypeKey != archetypeKey)
                    Invalidate();

                lastArchetypeKey = archetypeKey;
            }

            lastEnabled = enabled;

            if (!enabled)
                return null;

            return new ThreadPlanMemo(Volatile.Read(ref state), fitKey, levelOf, healthOf, profileOf, archetypeProfileOf, perHitOf);
        }

        /// <summary>How many entries each map currently holds. Diagnostics and tests only.</summary>
        public static ThreadPlanCacheCounts Counts
        {
            get
            {
                var s = Volatile.Read(ref state);

                return new ThreadPlanCacheCounts(s.Levels.Count, s.Healths.Count, s.Profiles.Count, s.Projections.Count,
                    s.EligibilityBands.Count, s.FamilyCounts.Count, s.BandMedians.Count, s.BandStandards.Count,
                    s.ArchetypeProfiles.Count, s.Rosters.Count, s.ArchetypeStandards.Count, s.PerHits.Count, s.DamageCeilingBaselines.Count);
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
            private readonly string clearWhenFull;
            private int count;
            private int warned;

            /// <param name="clearWhenFull">
            /// Null (every original map): stop storing at the cap, never evict. A name: at the cap, CLEAR the map and keep
            /// storing, logging one Warn per map instance naming it.
            /// </param>
            public BoundedMap(int max, string clearWhenFull = null)
            {
                this.max = max;
                this.clearWhenFull = clearWhenFull;
            }

            public int Count => Volatile.Read(ref count);

            public int Max => max;

            public bool TryGet(TKey key, out TValue value) => map.TryGetValue(key, out value);

            /// <summary>Stores <paramref name="value"/> unless the map is already at its cap (or, for a clear-when-full map, clears it first).</summary>
            public void TryStore(TKey key, TValue value)
            {
                if (Volatile.Read(ref count) >= max)
                {
                    if (clearWhenFull == null)
                        return;

                    map.Clear();
                    Volatile.Write(ref count, 0);

                    if (Interlocked.Exchange(ref warned, 1) == 0)
                        log.Warn($"[DYNDUNGEON] plan cache: {clearWhenFull} reached its cap of {max} entries and was cleared; it refills from here");
                }

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
            private readonly double rosterFloor;

            /// <param name="rosterFloor">
            /// The band floor the measure's ROSTER classifier was built with, for a memo whose result depends on a roster
            /// (the archetype standards); 0 for every key that takes no roster.
            /// </param>
            public BandKey(object tables, int level, DungeonRosterBand band, double lowFloor, int extra, double levelScale = 1.0, double rosterFloor = 0.0)
            {
                this.tables = tables;
                this.level = level;
                bandLow = band.Low;
                bandHigh = band.High;
                this.lowFloor = lowFloor;
                this.extra = extra;
                this.levelScale = levelScale;
                this.rosterFloor = rosterFloor;
            }

            public bool Equals(BandKey other)
                => ReferenceEquals(tables, other.tables) && level == other.level && bandLow.Equals(other.bandLow)
                   && bandHigh.Equals(other.bandHigh) && lowFloor.Equals(other.lowFloor) && extra == other.extra
                   && levelScale.Equals(other.levelScale) && rosterFloor.Equals(other.rosterFloor);

            public override bool Equals(object obj) => obj is BandKey other && Equals(other);

            public override int GetHashCode()
                => HashCode.Combine(RuntimeHelpers.GetHashCode(tables), level, bandLow, bandHigh, lowFloor, extra, levelScale, rosterFloor);
        }

        /// <summary>
        /// The key for the damage ceiling's baseline memo: the species tables by reference (same reasoning as
        /// <see cref="BandKey"/>), the gem level, the WHOLE <see cref="DungeonPopulationLimits"/> value with its per-run group
        /// snapshot dropped (DungeonPopulationLimits.WithoutGroup; the baseline never reads the group), the solo plan's
        /// additive DamageRating (the archetype prediction's DamageRating offset reads it), and whether a band-standard and
        /// an archetype profile delegate were supplied. The limits compare by the struct's own field-by-field value equality,
        /// so a dial added to it later is in the key without anyone listing it here; one lookup per plan build is cheap.
        /// </summary>
        internal readonly struct BaselineKey : IEquatable<BaselineKey>
        {
            private readonly object tables;
            private readonly int level;
            private readonly DungeonPopulationLimits limits;
            private readonly int gemDamageRating;
            private readonly bool hasProfiles;
            private readonly bool hasArchetypeProfiles;

            public BaselineKey(object tables, int level, DungeonPopulationLimits limits, int gemDamageRating, bool hasProfiles, bool hasArchetypeProfiles)
            {
                this.tables = tables;
                this.level = level;
                this.limits = limits.WithoutGroup();
                this.gemDamageRating = gemDamageRating;
                this.hasProfiles = hasProfiles;
                this.hasArchetypeProfiles = hasArchetypeProfiles;
            }

            public bool Equals(BaselineKey other)
                => ReferenceEquals(tables, other.tables) && level == other.level && gemDamageRating == other.gemDamageRating
                   && hasProfiles == other.hasProfiles && hasArchetypeProfiles == other.hasArchetypeProfiles && limits.Equals(other.limits);

            public override bool Equals(object obj) => obj is BaselineKey other && Equals(other);

            public override int GetHashCode()
                => HashCode.Combine(RuntimeHelpers.GetHashCode(tables), level, gemDamageRating, hasProfiles, hasArchetypeProfiles);
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
            int familyCounts, int bandMedians, int bandStandards, int archetypeProfiles = 0, int rosters = 0, int archetypeStandards = 0,
            int perHits = 0, int damageCeilingBaselines = 0)
        {
            PerHits = perHits;
            DamageCeilingBaselines = damageCeilingBaselines;
            ArchetypeProfiles = archetypeProfiles;
            Rosters = rosters;
            ArchetypeStandards = archetypeStandards;
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

        /// <summary>The archetype uplift's three maps (profiles with facts, roster classifiers, per-archetype standards).</summary>
        public int ArchetypeProfiles { get; }
        public int Rosters { get; }
        public int ArchetypeStandards { get; }

        /// <summary>The damage ceiling's two maps (per-wcid per-hit reads, whole baselines).</summary>
        public int PerHits { get; }
        public int DamageCeilingBaselines { get; }

        public override string ToString()
            => $"levels={Levels} healths={Healths} profiles={Profiles} projections={Projections} " +
               $"eligibilityBands={EligibilityBands} familyCounts={FamilyCounts} bandMedians={BandMedians} bandStandards={BandStandards} " +
               $"archetypeProfiles={ArchetypeProfiles} rosters={Rosters} archetypeStandards={ArchetypeStandards} " +
               $"perHits={PerHits} damageCeilingBaselines={DamageCeilingBaselines}";
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
        private readonly Func<uint, DungeonStatProfile> innerArchetypeProfileOf;
        private readonly Func<uint, CreaturePerHit> innerPerHitOf;

        private int lookups;
        private int hits;

        internal ThreadPlanMemo(ThreadPlanCache.State state, string fitKey, Func<uint, int> levelOf, Func<uint, uint> healthOf,
            Func<uint, DungeonStatProfile> profileOf, Func<uint, DungeonStatProfile> archetypeProfileOf = null,
            Func<uint, CreaturePerHit> perHitOf = null)
        {
            this.state = state;
            this.fitKey = fitKey;
            innerLevelOf = levelOf;
            innerHealthOf = healthOf;
            innerProfileOf = profileOf;
            innerArchetypeProfileOf = archetypeProfileOf;
            innerPerHitOf = perHitOf;
        }

        /// <summary>
        /// A memo over a PRIVATE, fresh cache generation, unconnected to <see cref="ThreadPlanCache"/>'s shared one and to its
        /// kill switch: for a caller with the plan cache off that still needs a memo-shaped helper for one computation (the
        /// damage ceiling's archetype context), and whose result must equal the cache-on one. Thrown away with the caller.
        /// </summary>
        internal static ThreadPlanMemo Scratch(Func<uint, int> levelOf, Func<uint, DungeonStatProfile> profileOf,
            Func<uint, DungeonStatProfile> archetypeProfileOf, Func<uint, CreaturePerHit> perHitOf)
            => new ThreadPlanMemo(new ThreadPlanCache.State(), null, levelOf, null, profileOf, archetypeProfileOf, perHitOf);

        /// <summary>True when this memo was handed a per-hit delegate (<see cref="PerHitOf"/> is then usable).</summary>
        public bool HasPerHits => innerPerHitOf != null;

        /// <summary>The damage ceiling's per-wcid read (<see cref="CreaturePerHit"/>), memoised in its own map.</summary>
        public CreaturePerHit PerHitOf(uint wcid)
        {
            if (state.PerHits.TryGet(wcid, out var cached))
                return cached;

            var value = innerPerHitOf?.Invoke(wcid) ?? CreaturePerHit.Empty;
            state.PerHits.TryStore(wcid, value);

            return value;
        }

        /// <summary>True when this memo was handed an archetype profile delegate (<see cref="ArchetypeProfileOf"/> is then usable).</summary>
        public bool HasArchetypeProfiles => innerArchetypeProfileOf != null;

        /// <summary>
        /// The archetype uplift's profile (DungeonStatProfile with ArchetypeFacts), memoised per wcid in its OWN map: the
        /// plain profile map must keep holding fact-free profiles, or a plan with no uplift would start paying for facts.
        /// </summary>
        public DungeonStatProfile ArchetypeProfileOf(uint wcid)
        {
            if (state.ArchetypeProfiles.TryGet(wcid, out var cached))
                return cached;

            var value = innerArchetypeProfileOf?.Invoke(wcid) ?? DungeonStatProfile.Empty;
            state.ArchetypeProfiles.TryStore(wcid, value);

            return value;
        }

        /// <summary>
        /// The roster classifier for (tables, band, floor), memoised. Its key carries every input the classification
        /// reads: the tables by reference, both roster band edges and the band low floor (the cross-archetype standard the
        /// melee split divides by is measured with them). The classifier thresholds are compile-time constants, and the
        /// profile delegate is this memo's own (weenie data, invalidated with the whole cache). Level and scale are fixed
        /// at 0 and 1: a classification is unprojected by definition.
        /// </summary>
        public RosterArchetypes Roster(IReadOnlyDictionary<string, SpeciesTableDef> tables, DungeonRosterBand band, double lowFloorRatio,
            Func<uint, DungeonStatProfile> profileOf)
        {
            var key = new ThreadPlanCache.BandKey(tables, 0, band, lowFloorRatio, 0, 1.0);
            lookups++;

            if (state.Rosters.TryGet(key, out var cached))
            {
                hits++;
                return cached;
            }

            var value = new RosterArchetypes(tables, band, lowFloorRatio, profileOf);
            state.Rosters.TryStore(key, value);

            return value;
        }

        /// <summary>
        /// <see cref="DungeonArchetypeStandards.Measure"/>, memoised. THE TUNABLES ARE IN THE KEY: the tables reference,
        /// the level, both roster band edges, the SAMPLE's band low floor (each archetype's sample widens down to it; the
        /// natural-only run-level measurement passes 1.0 here), the reach-up scale of the sample's profiles, and the
        /// ROSTER's band floor <paramref name="rosterFloor"/>. The roster partitions the sample, and it classifies each
        /// member against the cross-archetype standard measured with ITS floor (RosterArchetypes.StandardAt), so the
        /// sample floor and the roster floor are separate inputs and can differ: a run-level measurement at sample floor
        /// 1.0 still depends on the configured band_low_floor through its roster. The run's offense and defense ratios,
        /// the stat curve's softening and the health curve are applied AFTER this measurement (ArchetypeUpliftContext),
        /// so they are deliberately not part of what is cached. The roster must be the one <see cref="Roster"/> returns
        /// for the same tables and band at <paramref name="rosterFloor"/>.
        /// </summary>
        public DungeonArchetypeStandards ArchetypeStandards(IReadOnlyDictionary<string, SpeciesTableDef> tables, int level,
            Func<uint, DungeonStatProfile> profileOf, RosterArchetypes roster, DungeonRosterBand natural, double lowFloorRatio, double levelScale,
            double rosterFloor)
        {
            var key = new ThreadPlanCache.BandKey(tables, level, natural, lowFloorRatio, 0, levelScale, rosterFloor);
            lookups++;

            if (state.ArchetypeStandards.TryGet(key, out var cached))
            {
                hits++;
                return cached;
            }

            var value = DungeonArchetypeStandards.Measure(tables, level, profileOf, roster, natural, lowFloorRatio);
            state.ArchetypeStandards.TryStore(key, value);

            return value;
        }

        /// <summary>
        /// The damage ceiling's whole baseline (DungeonPopulationBuilder.DamageCeilingBaselineFor), memoised on
        /// <see cref="ThreadPlanCache.BaselineKey"/>. <paramref name="compute"/> runs on a miss and must be the pure
        /// function of exactly the key's inputs (plus this memo's own weenie delegates).
        /// </summary>
        public DamageCeilingBaseline DamageCeilingBaseline(IReadOnlyDictionary<string, SpeciesTableDef> tables, int level, DungeonPopulationLimits limits,
            int gemDamageRating, bool hasProfiles, bool hasArchetypeProfiles, Func<DamageCeilingBaseline> compute)
        {
            var key = new ThreadPlanCache.BaselineKey(tables, level, limits, gemDamageRating, hasProfiles, hasArchetypeProfiles);
            lookups++;

            if (state.DamageCeilingBaselines.TryGet(key, out var cached))
            {
                hits++;
                return cached;
            }

            var value = compute();
            state.DamageCeilingBaselines.TryStore(key, value);

            return value;
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
