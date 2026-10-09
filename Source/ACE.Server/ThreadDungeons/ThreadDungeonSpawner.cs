using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using ACE.Database;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.ThreadDungeons.Defs;
using ACE.Server.WorldEvents;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// Populates a run's private landblock exactly once (TECH-DESIGN S7). ThreadDungeonManager.Tick calls
    /// <see cref="TryPopulate"/> on the world thread for every run still in Starting whose landblock reports
    /// CreateWorldObjectsCompleted; everything that touches the landblock then runs on that landblock's own
    /// action queue, in batches, so one large dungeon never stalls a world tick.
    ///
    /// Threading (execution-log ruling R10). TryPopulate runs on the WORLD thread. The delegate chain it
    /// enqueues runs on a LANDBLOCK thread, and landblocks tick in parallel, so:
    ///   * <see cref="populating"/> is a ConcurrentDictionary used as a latch - the world thread may tick
    ///     several times between the enqueue and the completion, and only the first may enqueue a chain;
    ///   * every mutable field of the run is written through its own locked mutators (MarkPopulated), and
    ///     MarkPopulated is called exactly ONCE, from the last queue step;
    ///   * nothing here writes a ThreadDungeonRun field directly.
    ///
    /// Pre-EnterWorld write order is copied from WorldEventSpawner.TryPlace (WorldEventSpawner.cs:1740-1791):
    /// Location, landblock check, run stamp, role, TimeToRot, the standardisation block (corpse, emotes, loot
    /// strip, hostility, spell filter), health, ratings, run skill, XP/luminance, loot profile, THEN
    /// EnterWorld. NOTHING is written after EnterWorld. The rule is not cosmetic: a property written after
    /// EnterWorld can be missed by the create packet the client has already been sent, and (for the run
    /// stamp) leaves a window in which a landblock save could see a run-owned creature as an ordinary
    /// persistable dynamic.
    /// </summary>
    public static class ThreadDungeonSpawner
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// Runs whose populate chain has been enqueued and has not finished. Concurrent because the world
        /// thread adds and a landblock thread removes (R10). The value is unused; there is no ConcurrentSet.
        /// </summary>
        private static readonly ConcurrentDictionary<uint, byte> populating = new ConcurrentDictionary<uint, byte>();

        /// <summary>
        /// Memoized spell -> NumProjectiles lookup for <see cref="DungeonSpellFilter.Strip"/>. Concurrent
        /// because landblocks tick in parallel (R10), so several placement chains can probe the same spell
        /// id at once. NumProjectiles is a plain ace_world column (Spell.NumProjectiles, backed by
        /// WorldDatabaseWithEntityCache.GetCachedSpell) that never changes at runtime, so caching it forever
        /// is safe.
        /// </summary>
        private static readonly ConcurrentDictionary<int, int> spellProjectilesCache = new ConcurrentDictionary<int, int>();

        /// <summary>
        /// Memoized setup id -> UNSCALED movement height, for <see cref="DungeonFitFilter"/>. Concurrent for
        /// the same reason <see cref="spellProjectilesCache"/> is: landblocks tick in parallel (R10), so
        /// several plan builds can probe the same setup at once.
        ///
        /// KEYED ON SETUP ID AND HOLDING THE UNSCALED HEIGHT, never on wcid and never scaled. Two reasons,
        /// both load-bearing:
        ///   - the seven creatures this filter excludes share only THREE rigs between them (0x020007CA at
        ///     scale 1.6, 0x020007D8 at 1.75, 0x02000AAD at 1.0), so one entry serves every weenie on that rig
        ///     and every dungeon;
        ///   - SpherePath.InitSphere scales Center and Radius together, so the height is exactly LINEAR in
        ///     scale - "unscaled height times this weenie's DefaultScale" is not an approximation of the
        ///     scaled measurement, it IS the scaled measurement.
        ///
        /// Caching forever is safe: Setup.Sphere is client dat data, which cannot change while the server is
        /// running, and WorldObject.GetSetupModel is itself already total (never null, never throws) and
        /// memoises its own per-id validity verdict.
        /// </summary>
        private static readonly ConcurrentDictionary<uint, float> setupMovementHeightCache = new ConcurrentDictionary<uint, float>();

        /// <summary>Claims the latch for a run. True for the ONE caller that may enqueue a populate chain.</summary>
        internal static bool TryBeginPopulate(uint runId) => populating.TryAdd(runId, 0);

        /// <summary>
        /// Drops a run's latch. Called by the populate chain when it finishes or gives up, and by
        /// ThreadDungeonManager.EndRun for the case the chain never gets to run its own terminal step:
        /// Landblock.Unload clears the landblock's action queue outright (actionQueue.Clear), so a copy that
        /// unloads mid-populate takes the pending delegate with it and nothing else would ever release the
        /// latch. A leaked latch is permanent - the run id is the ephemeral instance id, and a re-issued id
        /// would find itself already "populating" and never spawn anything.
        ///
        /// Idempotent, and safe for a run id that was never latched.
        /// </summary>
        public static void ForgetRun(uint runId) => populating.TryRemove(runId, out _);

        /// <summary>Test seam: is a populate chain in flight for this run?</summary>
        internal static bool IsPopulating(uint runId) => populating.ContainsKey(runId);

        /// <summary>
        /// Builds the run's plan and enqueues its placement onto <paramref name="landblock"/>. Safe to call
        /// every tick: the state check and the <see cref="populating"/> latch make it a no-op after the first.
        /// </summary>
        public static void TryPopulate(ThreadDungeonRun run, Landblock landblock)
        {
            if (run == null || landblock == null)
                return;

            if (run.State != ThreadDungeonRunState.Starting || !landblock.CreateWorldObjectsCompleted)
                return;

            // The latch, not the state, is what makes this exactly-once. The state stays Starting until the
            // LAST queue step calls MarkPopulated, which can be many world ticks after the chain started.
            if (!TryBeginPopulate(run.RunId))
                return;

            // Telemetry only, and latched HERE rather than at the top of the method: this is the point past
            // which a plan is actually built and a placement chain enqueued, so a run that ends with this
            // still false is one whose copy never came up - which is what separates it from a run whose plan
            // ran and placed nothing. Both report spawned = 0 and they are entirely different faults.
            run.MarkPopulateReached();

            // Pooled loot (Docs/Threads/POOLED-LOOT-CACHE-DESIGN.md section 1). Stamped HERE, before the plan is
            // built and before any creature exists, not beside MarkRewardProfile at the end of the chain: kills
            // land while later batches are still placing (ThreadDungeonRun.RecordKill), and a stamp that late
            // would put those kills under the corpse model and split the run. Explicit fallback true for the
            // same reason as the boss-chest switch (ThreadDungeonRewardSpawner.TrySpawnBossCache).
            //
            // Group runs are ALWAYS pooled (ruling R13): personal piles exist only in the pooled model. The stamp
            // is write-once, so this line has to run before the switch-driven stamp below.
            if (run.IsGroup) run.MarkPooledLoot(true);
            run.MarkPooledLoot(PropertyManager.GetBool("dynamic_dungeons_pooled_loot_enabled", true).Item);

            DungeonSpawnPlan plan;

            try
            {
                var limits = new DungeonPopulationLimits(
                    ClampToInt(PropertyManager.GetLong("dynamic_dungeons_max_monsters_per_run").Item),
                    PropertyManager.GetDouble("dynamic_dungeons_xp_mult_cap").Item,
                    PropertyManager.GetDouble("dynamic_dungeons_lum_mult_cap").Item,
                    ClampToInt(PropertyManager.GetLong("dynamic_dungeons_loot_tier_cap").Item),
                    PropertyManager.GetDouble("dynamic_dungeons_loot_quality_cap").Item,
                    PropertyManager.GetDouble("dynamic_dungeons_loot_quantity_cap").Item,
                    // dynamic_dungeons_modifier_reward_scale (owner ruling): shrinks ONLY the bonus above
                    // neutral of a gem's rolled reward-modifier products (XpMultiplier, BossXpMultiplier,
                    // LumMultiplier, LootQuantityMultiplier), applied AFTER their own caps - see
                    // DungeonPopulationLimits.ModifierRewardScale. Sanitized like the neighbouring reward
                    // dials: NaN/Infinity/negative reads as the compiled default (1.0, neutral); the domain
                    // ceiling IS 1.0, so this is also the "typo ceiling" argument to ReadDoubleDial.
                    ReadDoubleDial("dynamic_dungeons_modifier_reward_scale", DungeonPopulationLimits.DefaultModifierRewardScale, DungeonPopulationLimits.MaxModifierRewardScale),
                    // The boss-uplift terms (invariants I1-I4). Read here for the same reason as everything
                    // above it: the builder is pure and a PropertyManager read throws under the unit-test
                    // harness.
                    //
                    // All four sanitize the same way, and the rule is one sentence: NaN, Infinity or a
                    // negative value falls back to the compiled default, while ZERO is a valid explicit
                    // "disable this axis". A garbled dial must not quietly turn off the invariant it
                    // configures - only an admin typing 0 does that, and then only that one axis. The two
                    // doubles additionally clamp at a typo ceiling; the two floors only saturate, because an
                    // absurd rating floor costs damage numbers rather than an unfinishable run.
                    ReadDoubleDial("dynamic_dungeons_boss_health_floor_ratio", DungeonPopulationLimits.DefaultBossHealthFloorRatio, DungeonPopulationLimits.MaxBossHealthFloorRatio),
                    ReadBossFloor("dynamic_dungeons_boss_damage_rating_floor", DungeonPopulationLimits.DefaultBossDamageRatingFloor),
                    ReadBossFloor("dynamic_dungeons_boss_damage_resist_floor", DungeonPopulationLimits.DefaultBossDamageResistFloor),
                    ReadDoubleDial("dynamic_dungeons_boss_level_margin", DungeonPopulationLimits.DefaultBossLevelMargin, DungeonPopulationLimits.MaxBossLevelMargin),
                    // The owner's headline rate, on the same sanitize contract as the four dials above. Kept
                    // OUT of the XP/lum modifier products on purpose: those end in Math.Min(product, cap), so
                    // a scalar folded in as a synthetic factor would be clipped by the cap and a plain gem
                    // would pay the same as a four-modifier one.
                    ReadDoubleDial("dynamic_dungeons_xp_scale", DungeonPopulationLimits.DefaultXpScale, DungeonPopulationLimits.MaxRewardScale),
                    ReadDoubleDial("dynamic_dungeons_lum_scale", DungeonPopulationLimits.DefaultLumScale, DungeonPopulationLimits.MaxRewardScale),
                    // The gem-level reward scaling curve (owner requirement, 2026-09-07). The master switch
                    // is a plain live bool; the four curve dials are read raw and handed to the pure builder,
                    // which re-sanitizes them itself (DungeonRewardMath.RewardScaleRatio) the same way
                    // XpForKill re-sanitizes rewardScale - so a garbled value here degrades to the compiled
                    // default rather than propagating a NaN into every run's reward math.
                    //
                    // EXPLICIT fallback arguments here, unlike the un-defaulted GetBool/GetDouble calls used
                    // for the OLDER dials above - deliberately, and found in review, 2026-09-07: GetBool's own
                    // built-in fallback is `false` and GetDouble's is `0.0` when a key has no row yet (before
                    // PropertyManager.DoWork's periodic WriteBoolToDB/WriteDoubleToDB seeds one from
                    // DefaultBooleanProperties/DefaultDoubleProperties), and 0.0 is NOT caught by
                    // SanitizeDoubleDial's NaN/Infinity/negative check - it reads as a valid explicit "disable
                    // this axis". For the boss/reward-scalar dials above that ambiguity was already an
                    // accepted, documented design choice; for a brand-new "default true" master switch it is
                    // not - a fresh master switch defaulting to OFF and staying that way until the next
                    // DoWork tick directly contradicts the shipped default. Passing the compiled default as
                    // the explicit fallback makes the feature read as genuinely on from its very first read.
                    PropertyManager.GetBool("dynamic_dungeons_reward_scaling", DungeonPopulationLimits.DefaultRewardScalingEnabled).Item,
                    PropertyManager.GetDouble("dynamic_dungeons_reward_scale_anchor", DungeonPopulationLimits.DefaultRewardScaleAnchor).Item,
                    PropertyManager.GetDouble("dynamic_dungeons_reward_scale_exponent", DungeonPopulationLimits.DefaultRewardScaleExponent).Item,
                    PropertyManager.GetDouble("dynamic_dungeons_reward_scale_floor", DungeonPopulationLimits.DefaultRewardScaleFloor).Item,
                    PropertyManager.GetDouble("dynamic_dungeons_reward_scale_cap", DungeonPopulationLimits.DefaultRewardScaleCap).Item,
                    // dynamic_dungeons_salvage_affinity_chance_cap (owner ruling): the ceiling on a run's
                    // per-kill salvage-affinity chance, AFTER that axis's own (uncapped) reward-scale ratio -
                    // see DungeonPopulationLimits.SalvageAffinityChanceCap. Sanitized the same way as
                    // dynamic_dungeons_modifier_reward_scale: NaN/Infinity/negative reads as the compiled
                    // default (1.0, neutral); the domain ceiling IS 1.0.
                    ReadDoubleDial("dynamic_dungeons_salvage_affinity_chance_cap", DungeonPopulationLimits.DefaultSalvageAffinityChanceCap, DungeonPopulationLimits.MaxSalvageAffinityChanceCap),
                    // The non-boss health floor's ratio, on the same sanitize contract as the boss dials
                    // above: NaN/negative reads as the default, and the value clamps at a typo ceiling.
                    ReadDoubleDial("dynamic_dungeons_trash_health_floor_ratio", DungeonPopulationLimits.DefaultTrashHealthFloorRatio, DungeonPopulationLimits.MaxTrashHealthFloorRatio),
                    // The roster band edges (owner ruling, 2026-09-08: narrow BandHigh from 1.5 to 1.15).
                    // NOT ReadDoubleDial - see ReadBandDial's own doc comment for why zero cannot be treated
                    // as "disable this axis" the way it is for the dials above.
                    ReadBandDial("dynamic_dungeons_roster_band_low", DungeonPopulationLimits.DefaultRosterBandLow, DungeonRosterSelector.MaxBandLow),
                    ReadBandDial("dynamic_dungeons_roster_band_high", DungeonPopulationLimits.DefaultRosterBandHigh, DungeonRosterSelector.MaxBandHigh),
                    // The adaptive band and its uplift. The floor has its own sanitize rule (see
                    // ReadBandFloorDial) because 1.0 - not 0 - is its documented "disabled" value; the two
                    // thresholds are plain counts where 0 legitimately disables that half of the widening.
                    ReadBandFloorDial("dynamic_dungeons_band_low_floor", DungeonPopulationLimits.DefaultBandLowFloorRatio),
                    ReadCountDial("dynamic_dungeons_min_eligible_families", DungeonPopulationLimits.DefaultMinEligibleFamilies),
                    ReadCountDial("dynamic_dungeons_min_family_pool", DungeonPopulationLimits.DefaultMinFamilyPool),
                    // EXPLICIT fallback, same reasoning as the reward-scaling master switch above: GetBool's
                    // own built-in fallback is false before PropertyManager.DoWork seeds a row from
                    // DefaultBooleanProperties, and a "default true" switch that reads false until the next
                    // sync contradicts its own shipped default.
                    PropertyManager.GetBool("dynamic_dungeons_uplift_rename", DungeonPopulationLimits.DefaultUpliftRename).Item,
                    // The band-standard health curve (owner requirement, 2026-09-12). EXPLICIT fallback on the
                    // master switch, same reasoning as dynamic_dungeons_reward_scaling and
                    // dynamic_dungeons_uplift_rename above: GetBool's own built-in fallback is false until
                    // PropertyManager.DoWork seeds a row from DefaultBooleanProperties, so a default-TRUE switch
                    // read without it reads false on a fresh database and contradicts its own shipped default.
                    //
                    // The two anchors go through ReadAnchorDial rather than ReadDoubleDial because zero cannot
                    // be "disable this axis" for an anchor - a zero anchor makes the curve's derived ratio
                    // undefined. The master switch is how the feature is turned off.
                    PropertyManager.GetBool("dynamic_dungeons_health_curve", DungeonPopulationLimits.DefaultHealthCurveEnabled).Item,
                    ReadAnchorDial("dynamic_dungeons_health_curve_anchor_low", DungeonPopulationLimits.DefaultHealthCurveAnchorLow, DungeonHealthCurve.MaxAnchorLow),
                    ReadAnchorDial("dynamic_dungeons_health_curve_anchor_high", DungeonPopulationLimits.DefaultHealthCurveAnchorHigh, DungeonHealthCurve.MaxAnchorHigh),
                    // Boss normalization, the combat-trait strip and boss-row selection (owner requirements
                    // 2026-09-13). Every one SHIPS ON, so every read passes the compiled default as its explicit
                    // fallback: GetBool/GetDouble's own fallbacks are false/0.0 until PropertyManager.DoWork seeds a
                    // row, and 0.0 is a valid "disable this axis" to ReadDoubleDial - the unseeded read would
                    // silently turn the feature off on a fresh database.
                    PropertyManager.GetBool("dynamic_dungeons_boss_normalize", DungeonPopulationLimits.DefaultBossNormalize).Item,
                    ReadShippedDoubleDial("dynamic_dungeons_boss_health_band_ratio", DungeonPopulationLimits.DefaultBossHealthBandRatio, DungeonPopulationLimits.MaxBossHealthBandRatio),
                    ReadShippedDoubleDial("dynamic_dungeons_boss_health_pack_margin", DungeonPopulationLimits.DefaultBossHealthPackMargin, DungeonPopulationLimits.MaxBossHealthPackMargin),
                    ReadShippedDoubleDial("dynamic_dungeons_boss_offense_band_ratio", DungeonPopulationLimits.DefaultBossOffenseBandRatio, DungeonPopulationLimits.MaxBossBandRatio),
                    ReadShippedDoubleDial("dynamic_dungeons_boss_defense_band_ratio", DungeonPopulationLimits.DefaultBossDefenseBandRatio, DungeonPopulationLimits.MaxBossBandRatio),
                    PropertyManager.GetBool("dynamic_dungeons_strip_combat_traits", DungeonPopulationLimits.DefaultStripCombatTraits).Item,
                    ReadShippedDoubleDial("dynamic_dungeons_category_resist_floor", DungeonPopulationLimits.DefaultCategoryResistFloor, DungeonPopulationLimits.MaxCategoryResistFloor),
                    ReadShippedDoubleDial("dynamic_dungeons_category_armor_mod_ceiling", DungeonPopulationLimits.DefaultCategoryArmorModCeiling, DungeonPopulationLimits.MaxCategoryArmorModCeiling),
                    ReadDefenseSkillCapOffsetDial("dynamic_dungeons_defense_skill_cap_offset", DungeonPopulationLimits.DefaultDefenseSkillCapOffset, DungeonPopulationLimits.MaxDefenseSkillCapOffset),
                    PropertyManager.GetBool("dynamic_dungeons_boss_any_all_bands", DungeonPopulationLimits.DefaultBossAnyAllBands).Item,
                    ReadShippedDoubleDial("dynamic_dungeons_boss_family_row_weight", DungeonPopulationLimits.DefaultBossFamilyRowWeight, DungeonPopulationLimits.MaxBossFamilyRowWeight),
                    // Floor on non-boss creatures placed in one run (dynamic_dungeons_min_monsters_per_run).
                    // Same sanitize contract as the other count dials: negative reads as the compiled
                    // default, 0 is a valid explicit "no floor", and the value saturates rather than
                    // wrapping negative. The builder itself bounds this against MaxMonsters, so a
                    // misconfigured minimum above the cap is silently bounded rather than overriding it.
                    ReadCountDial("dynamic_dungeons_min_monsters_per_run", DungeonPopulationLimits.DefaultMinMonsters),
                    // The modifier magnitude curve below level 185 (DungeonModifierLevelScale). Resolved by the
                    // same method the gem's description uses, so the panel and the run cannot disagree.
                    modifierLevelExponent: ResolveModifierLevelExponent(),
                    // Group Threads: the run's lock-time snapshot (TryStart), never a fresh tunable read
                    // (invariant 3). GroupScaling.Solo for a solo run, which the builder leaves untouched.
                    group: run.Group,
                    // The 2026-10-08 run ceiling raise (owner ruling, final). All four SHIP ON, so every read passes
                    // the compiled default as its explicit fallback: GetBool/GetLong/GetDouble's own fallbacks are
                    // false/0/0.0 until PropertyManager.DoWork seeds a row, and an unseeded read would silently turn
                    // reach-up and the stat curve off, clamp the health curve at 375 and freeze effective defense
                    // above 375 on a fresh database.
                    healthCurveTopLevel: ReadHealthCurveTopLevelDial(),
                    reachUp: PropertyManager.GetBool("dynamic_dungeons_reach_up", DungeonPopulationLimits.DefaultReachUp).Item,
                    statCurve: PropertyManager.GetBool("dynamic_dungeons_stat_curve", DungeonPopulationLimits.DefaultStatCurve).Item,
                    defenseCurveRateAbove375: ReadDefenseCurveRateDial());

                var fitsDungeon = ResolveFitPredicate(run.Dungeon, out var fitKey);

                // The population-plan memo (ThreadPlanCache), read on the world thread with every other dial and
                // with the EXPLICIT-FALLBACK GetBool(key, default) form, because it is a default-TRUE switch:
                // GetBool's own fallback is false until PropertyManager.DoWork seeds a row from
                // DefaultBooleanProperties, so the un-defaulted form would read false on a fresh database and
                // quietly contradict the shipped default. Same reasoning as dynamic_dungeons_fit_filter above.
                //
                // NULL when the switch is off, which the builder reads as "compute everything" - its pre-cache
                // path, byte for byte.
                var memo = ThreadPlanCache.ForRun(
                    PropertyManager.GetBool("dynamic_dungeons_plan_cache", ThreadPlanCache.DefaultPlanCacheEnabled).Item,
                    fitKey, LiveLevelOf, LiveHealthOf, LiveProfileOf);

                // The three live weenie delegates, memoised per wcid when the cache is on. Each memoised form
                // returns exactly what its inner delegate returned for that wcid, so swapping them cannot move
                // a single roster decision - it only stops the same weenie being re-read a few thousand times
                // across one build's band ladders.
                Func<uint, int> levelOf = LiveLevelOf;
                Func<uint, uint> healthOf = LiveHealthOf;
                Func<uint, DungeonStatProfile> profileOf = LiveProfileOf;

                if (memo != null)
                {
                    levelOf = memo.LevelOf;
                    healthOf = memo.HealthOf;
                    profileOf = memo.ProfileOf;
                }

                var planWatch = System.Diagnostics.Stopwatch.StartNew();

                plan = DungeonPopulationBuilder.Build(run.Spec, run.Dungeon, ThreadDungeonManager.Store,
                    WorldEventManager.Store.SpeciesTables, levelOf, healthOf, limits, new Random(run.Spec.Seed), profileOf,
                    fitsDungeon, memo);

                // ace.threads.plan.duration and the run summary's planMs. Build runs on the world thread.
                planWatch.Stop();
                ServerMetrics.RecordThreadsPlan(planWatch.Elapsed.TotalMilliseconds, memo?.Hits ?? 0, memo?.Lookups ?? 0);
                run.Perf.RecordPlan(planWatch.ElapsedMilliseconds, plan.EffectiveBandLow, memo?.Hits ?? 0, memo?.Lookups ?? 0);
            }
            catch (Exception ex)
            {
                // An empty run is playable (it has an exit portal and clears immediately); a stuck run is not.
                log.Error($"[DYNDUNGEON] {run} plan build threw; the run opens empty", ex);
                run.MarkPopulated(0, 0, 0);
                ForgetRun(run.RunId);
                ThreadDungeonManager.OnRunPopulated(run);
                return;
            }

            // Group Threads: stamp C_actual and H on the run once, before any creature is queued, so the loot
            // factor (run.TrashLootFactor) is final by the time the first kill can bank. Solo runs never stamp.
            if (run.IsGroup)
                run.MarkGroupResolved(plan.GroupCountActual, plan.GroupHealthMult);

            foreach (var note in plan.Notes)
                log.Warn($"[DYNDUNGEON] {run} plan: {note}");

            // The one PLAN-time entry in the placement ledger. It is read from plan.BossNoCandidate, a
            // structured flag, and NOT by matching the note text above: the notes are free text written for a
            // human reading the log, and telemetry keyed on their wording would break silently the first time
            // one was reworded. wcid 0, because the failure is that there was no wcid to try.
            if (plan.BossNoCandidate)
                run.RecordPlacement(DungeonRunTelemetry.Phases.Plan, DungeonRunTelemetry.Reasons.NoCandidate, 0, DungeonRole.Boss);

            log.Info($"[DYNDUNGEON] {run} plan: family={plan.FamilyId ?? "none"} entries={plan.Entries.Count} boss={plan.BossWcid} bossLevel={plan.BossLevel} " +
                     $"bandLow={plan.EffectiveBandLow}/natural {plan.NaturalBandLow} uplifted={plan.Entries.Count(e => e.UpliftLevel > 0)} " +
                     // Reach-up and the stat curve (2026-10-08): the pivot, the projection scale actually applied, how
                     // many entries were stamped and to what top level, and whether the band standard is the curve's.
                     $"reachUp pivot={plan.ReachUpPivot} k={plan.ReachUpScale:0.###} stamped={plan.Entries.Count(e => e.StampLevel > 0)} " +
                     $"stampMax={plan.Entries.Select(e => e.StampLevel).DefaultIfEmpty(0).Max()} statCurve={(plan.StatCurve != null ? "on" : "off")} " +
                     $"bossNormalize={plan.BossNormalize} bossBase={plan.BossHealthBase:0} (band {plan.BossHealthBandTerm:0}, pack {plan.BossHealthPackTerm:0}, poolMax {plan.PoolMaxBase}) stripTraits={plan.StripCombatTraits} " +
                     $"hp x{plan.HealthMultiplier:0.##} bossHp x{plan.BossHealthMultiplier:0.##} bossHpFloor x{plan.BossHealthFloorRatio:0.##} trashHpFloor={plan.TrashHealthFloor} hpCurve={plan.HealthCurveTarget:0.#} hpNorm=x{plan.HealthNormalizeRatio:0.###} xp x{plan.XpMultiplier:0.##} bossXp x{plan.BossXpMultiplier:0.##} lum x{plan.LumMultiplier:0.##} xpScale x{plan.XpScale:0.##} lumScale x{plan.LumScale:0.##} " +
                     $"dr={plan.DamageRating} bossDr={plan.BossDamageRating} cr={plan.CritRating} cdr={plan.CritDamageRating} drr={plan.DamageResistRating} bossDrr={plan.BossDamageResistRating} " +
                     $"tier={plan.Profile.Tier} loot={plan.Profile.ItemMaxAmount}/{plan.Profile.MagicItemMaxAmount}/{plan.Profile.MundaneItemMaxAmount}" +
                     // Group Threads suffix on group runs only, so a solo plan line stays byte-identical.
                     (run.IsGroup
                         ? $" group n={plan.GroupRosterSize} e={run.Group.Effort:0.###} c={plan.GroupCountActual:0.###}/{plan.GroupCountTarget:0.###} h={plan.GroupHealthMult:0.###} " +
                           $"dr+={plan.GroupDamageRatingBonus} xpf={plan.GroupTrashRewardFactor:0.###}/{plan.GroupBossRewardFactor:0.###}"
                         : ""));

            // Telemetry only - must never break population. See DungeonRunTelemetry.BuildPlanRecord and
            // ThreadDungeonRun.MarkPlan.
            try
            {
                run.MarkPlan(DungeonRunTelemetry.BuildPlanRecord(plan));
            }
            catch (Exception ex)
            {
                log.Error($"[DYNDUNGEON] {run} failed to record plan telemetry", ex);
            }

            var batchSize = Math.Max(1, ClampToInt(PropertyManager.GetLong("dynamic_dungeons_spawn_batch_size").Item));

            // TIME is the unit of a placement step (dynamic_dungeons_spawn_step_budget_ms); the batch size above is
            // only its hard cap. Read once per run, on the world thread with every other dial, WITH the shipped value
            // as the explicit fallback so an unseeded row does not read as GetLong's 0 (which the clamp would turn
            // into the 1 ms floor rather than the shipped 4).
            var stepBudgetMs = ReadSpawnStepBudgetMs();

            var queue = new Queue<DungeonSpawnPlanEntry>(plan.Entries);
            var spawned = 0;
            var bossPlaced = false;

            // Placement fallback (dynamic_dungeons_placement_fallback_attempts). Read ONCE per run, on the world
            // thread with every other dial, WITH the shipped value as the explicit fallback (an unseeded row must
            // not read as 0 = off). SanitizeBossFloor's integer rule: negative reads as the default, 0 is an
            // admin's explicit disable; then the typo ceiling.
            var configuredFallback = PropertyManager.GetLong("dynamic_dungeons_placement_fallback_attempts", DungeonPopulationLimits.DefaultPlacementFallbackAttempts).Item;
            var fallbackAttempts = Math.Min(SanitizeBossFloor(configuredFallback, DungeonPopulationLimits.DefaultPlacementFallbackAttempts), DungeonPopulationLimits.MaxPlacementFallbackAttempts);

            if (fallbackAttempts != configuredFallback)
                log.Warn($"[DYNDUNGEON] dynamic_dungeons_placement_fallback_attempts is configured as {configuredFallback} which is out of range (0 to {DungeonPopulationLimits.MaxPlacementFallbackAttempts}); using {fallbackAttempts} instead");

            // Every point a plan entry was assigned, boss anchor included, so a trash retry prefers a point nobody
            // is standing on. Touched only from this run's landblock queue steps, which run one at a time.
            var occupied = new HashSet<(uint, float, float, float)>(plan.Entries.Where(e => e.Point != null).Select(e => DungeonPlacementFallback.KeyOf(e.Point)));

            // Invariant I2's right-hand side, accumulated across every batch: the largest Health.MaxValue any
            // non-boss creature in this run actually entered the world with. The boss entry is LAST in
            // plan.Entries (I6), so by the time TryPlace reaches it this is final. Observed rather than
            // predicted, because a creature's real MaxValue folds a dat-driven attribute formula the pure
            // builder cannot evaluate.
            var observedMaxNonBoss = 0u;
            var doorsUnlocked = 0;
            var stepIndex = 0;

            // TECH-DESIGN S5 / ruling R16: every DefaultLocked door in a run copy is unlocked before the first
            // creature lands, so a private dungeon never gates itself behind a key nothing drops.
            //
            // ITS OWN CHAIN STEP since 2026-09-21. It used to run inside the first PlaceBatch, on that step's
            // Stopwatch and therefore inside that step's allowance - so the door pass could spend the whole
            // 4 ms budget before the first creature was even attempted, and the first step then ran the door
            // pass PLUS at least one creature (the budget always admits the first attempt) in one world-thread
            // action. Splitting it gives the first creature step a fresh budget and leaves the door pass
            // measurable on its own, which is what the place_batch p99 spike on the first step was.
            //
            // NOT itself budgeted, and it cannot be split as the creature loop is: it is a single pass over
            // landblock.GetAllWorldObjectsForDiagnostics(), and resuming a partial pass would need a cursor
            // into a list the landblock is free to have changed between steps - a door created (or a run copy
            // re-Init'd) mid-pass could be skipped entirely, which is exactly the "gated behind a key nothing
            // drops" failure R16 exists to prevent. Per door the work is two property writes and a broadcast
            // that is a no-op with nobody in range, so the pass is O(objects in the copy) with no DB or
            // network round trip in it. ace.threads.door_pass.duration now measures it, so whether it ever
            // needs splitting is a question the next stage clear answers with data rather than a guess.
            void DoorPass()
            {
                // Same race PlaceBatch guards: the run ended between the enqueue and this step.
                if (run.State == ThreadDungeonRunState.Ended)
                {
                    ForgetRun(run.RunId);
                    log.Info($"[DYNDUNGEON] {run} ended mid-populate; {spawned} placed, {queue.Count} abandoned");
                    return;
                }

                var doorWatch = System.Diagnostics.Stopwatch.StartNew();

                try
                {
                    try { doorsUnlocked = UnlockDoors(landblock); }
                    catch (Exception ex) { log.Error($"[DYNDUNGEON] {run} door pass threw", ex); }

                    doorWatch.Stop();
                    ServerMetrics.RecordThreadsDoorPass(doorWatch.Elapsed.TotalMilliseconds, doorsUnlocked);
                    run.Perf.RecordDoorPass(doorWatch.ElapsedMilliseconds, doorsUnlocked);

                    if (log.IsDebugEnabled)
                    {
                        log.Debug($"[DYNDUNGEON] {run} door pass: unlocked={doorsUnlocked} " +
                                  $"ms={doorWatch.Elapsed.TotalMilliseconds.ToString("0.##", CultureInfo.InvariantCulture)}");
                    }

                    // Puzzle gates (ThreadPuzzlePass): AFTER the door pass, so the unlock can never touch a gate, and
                    // in this same step but outside the door-pass clock, so each placement's queued spawn runs ahead of the first creature batch
                    // enqueued below. Guarded on its own: a puzzle failure leaves the run open and never stops the
                    // creatures from placing.
                    try { ThreadPuzzlePass.Run(run, landblock); }
                    catch (Exception ex) { log.Error($"[DYNDUNGEON] {run} puzzle pass threw; the run opens without puzzles", ex); }

                    // The creature steps start here, each with a Stopwatch and a budget of its own.
                    landblock.EnqueueAction(new ActionEventDelegate(PlaceBatch));
                    return;
                }
                catch (Exception ex)
                {
                    // An exception that escapes an action delegate is swallowed by ActionQueue.RunActions and the
                    // CHAIN IS DROPPED, so a throw here (realistically only the enqueue) must not leave the run
                    // sitting in Starting with nothing to call MarkPopulated. Fall through and run the first
                    // placement step inline: that is the pre-split behaviour, one step doing both jobs, which is
                    // worse for the tick than a split step and still far better than a wedged run.
                    log.Error($"[DYNDUNGEON] {run} door step threw; running the first placement step inline", ex);
                }

                PlaceBatch();
            }

            void PlaceBatch()
            {
                // The run ended under us - a reap, an /dd end, the owner walking out. Stop placing creatures
                // into a copy that is being torn down, and abandon the rest of the plan. EndRun already
                // called ForgetRun; this is the race where Ended landed between the enqueue and this step.
                if (run.State == ThreadDungeonRunState.Ended)
                {
                    ForgetRun(run.RunId);
                    log.Info($"[DYNDUNGEON] {run} ended mid-populate; {spawned} placed, {queue.Count} abandoned");
                    return;
                }

                // Everything that can throw lives inside this try, including the re-enqueue. An exception
                // that escapes an action delegate is swallowed by ActionQueue.RunActions and the CHAIN IS
                // DROPPED: no later step runs, so nothing would ever call MarkPopulated and the run would
                // sit in Starting until its TTL expired three hours later. So a throw finishes the populate
                // with whatever was placed rather than propagating.
                var finished = true;
                var step = ++stepIndex;
                var stepWatch = System.Diagnostics.Stopwatch.StartNew();
                var placedBefore = spawned;

                // Counted in the place delegate rather than taken from RunPlacementStep's return value, so the
                // number is still right on the throw path below (RunPlacementStep never returns then).
                var attempted = 0;
                var stop = PlacementStepStop.Drained;

                try
                {
                    // The step's allowance: stop after the creature during which this step's own Stopwatch crosses
                    // stepBudgetMs, never more than batchSize attempts, and always at least one. The door pass is
                    // its own step now (see DoorPass above), so this Stopwatch times creatures and nothing else.
                    var budget = new ThreadLootRollBudget(batchSize, () => stepWatch.Elapsed.TotalMilliseconds, stepBudgetMs);

                    RunPlacementStep(queue, budget, entry =>
                    {
                        attempted++;

                        if (!TryPlace(run, landblock, plan, entry, observedMaxNonBoss, fallbackAttempts, occupied, out var placedMaxHealth))
                            return;

                        spawned++;

                        if (entry.Role == DungeonRole.Boss)
                            bossPlaced = true;
                        else if (placedMaxHealth > observedMaxNonBoss)
                            observedMaxNonBoss = placedMaxHealth;
                    }, out stop);

                    if (queue.Count > 0)
                    {
                        landblock.EnqueueAction(new ActionEventDelegate(PlaceBatch));
                        finished = false;
                    }
                }
                catch (Exception ex)
                {
                    stop = PlacementStepStop.Error;
                    log.Error($"[DYNDUNGEON] {run} placement batch threw; finishing the populate with {spawned} placed", ex);
                    finished = true;
                }

                // ace.threads.place_batch.duration, per step, with the creatures it placed; the run summary sums it.
                stepWatch.Stop();
                ServerMetrics.RecordThreadsPlaceBatch(stepWatch.Elapsed.TotalMilliseconds, spawned - placedBefore);
                run.Perf.RecordPlaceStep(stepWatch.ElapsedMilliseconds);

                // The per-step DETAIL the aggregate histogram cannot carry: which step, how many creatures it
                // attempted against how many it landed, and WHICH rule ended it. That is what makes a step that
                // overshoots its budget attributable - a step that stopped on time with 5 creatures in 15 ms is a
                // per-creature cost problem, one that stopped on the cap is a cap problem, and one that drained
                // the queue in 15 ms was never bounded at all. Built only when Debug is enabled: the guard keeps
                // both the formatting and the interpolated string itself off the placement path when it is not.
                if (log.IsDebugEnabled)
                {
                    log.Debug(PlacementStepLine(run.ToString(), step, attempted, spawned - placedBefore,
                        stepWatch.Elapsed.TotalMilliseconds, stop, batchSize, stepBudgetMs, queue.Count, spawned, plan.Entries.Count));
                }

                if (!finished)
                    return;

                // Exactly one MarkPopulated per run, from the terminal step. `spawned` counts EVERY creature
                // that entered the world, boss included, and `planned` every plan entry. The run's clear rule
                // (owner ruling R28) derives trash counts by subtracting the boss slot back out of Spawned and
                // Killed, so Spawned still needs to be the ACTUAL count even on the throw path above - a
                // half-placed run is still clearable, just against a smaller trash pool.
                //
                // The boss wcid reported as PLACED is the one that ACTUALLY entered the world - this matters
                // MORE under R28 than it did under the old rule: reporting a boss that failed to place would
                // not just set an unsatisfiable BossSpawned, it would also permanently withhold BossWeight
                // (20% by default) of the run's progress from a boss that can never be killed, capping the run
                // below ClearFraction forever.
                //
                // The INTENDED wcid (plan.BossWcid, whether or not it placed) is reported alongside it purely
                // for telemetry: it is what separates a boss that was drawn and failed to place from a run
                // that never had a boss to draw. It feeds no clear-rule arithmetic.
                // Stamped from the SAME terminal step, immediately before MarkPopulated, and write-once for
                // the same reason MarkPopulated is: it is what the boss cache rolls its loot from
                // (ThreadDungeonRewardSpawner), and a cache built from a re-read of the tunables could pay a
                // different rate than the creatures standing in the copy were built at.
                run.MarkRewardProfile(plan.Profile, plan.LootQuantityMult, plan.SalvageAffinities);

                run.MarkPopulated(plan.Entries.Count, spawned, bossPlaced ? plan.BossWcid : 0u, plan.BossWcid);
                ForgetRun(run.RunId);

                log.Info($"[DYNDUNGEON] {run} populated: {spawned}/{plan.Entries.Count} placed, {doorsUnlocked} door(s) unlocked" +
                         (plan.BossWcid != 0 && !bossPlaced ? $" (boss wcid {plan.BossWcid} FAILED to place)" : ""));

                // A run that placed nothing is Cleared the moment it is populated, and no creature death
                // will ever arrive to announce it. The manager owns the player-facing wording.
                ThreadDungeonManager.OnRunPopulated(run);
            }

            landblock.EnqueueAction(new ActionEventDelegate(DoorPass));
        }

        /// <summary>
        /// Why one placement step stopped. Exactly the three exit conditions of
        /// <see cref="RunPlacementStep{T}"/>'s loop, plus the throw path PlaceBatch's own catch owns.
        /// </summary>
        internal enum PlacementStepStop
        {
            /// <summary>Nothing left to place: the plan queue emptied. The terminal step always reports this.</summary>
            Drained,

            /// <summary>dynamic_dungeons_spawn_batch_size, the step's hard attempt ceiling.</summary>
            Cap,

            /// <summary>dynamic_dungeons_spawn_step_budget_ms: the step's clock crossed its allowance.</summary>
            Budget,

            /// <summary>A throw out of the placement loop or the re-enqueue; the populate finishes with what it has.</summary>
            Error,
        }

        /// <summary>
        /// Resolves a finished step's stop reason from what the loop can actually be stopped by. Pure, so the
        /// reported reason is unit-testable without a landblock - the same contract <see cref="RunPlacementStep{T}"/>
        /// itself is written to.
        ///
        /// DELIBERATELY CLOCK-FREE, and that is the whole point of this function's shape. The first version asked
        /// <see cref="ThreadLootRollBudget.TimeUp"/>, which re-invokes the budget's injected clock
        /// (ThreadLootRollBudget.cs:92). Every read of it is a NEW reading of a clock that keeps running, so a step
        /// that really stopped on the cap could be classified as a budget stop purely because wall-clock crossed the
        /// budget in the gap between the loop's last condition check and this call. Harmless to placement, fatal to
        /// the line's purpose: the reason exists to be COUNTED by a future reader, and a reason that flips with
        /// scheduling noise is worse than no reason. Both values read here - <see cref="ThreadLootRollBudget.Spent"/>
        /// and <see cref="ThreadLootRollBudget.MaxRolls"/> - are facts about work the loop did, and neither moves
        /// once the loop has exited, so this returns the same answer however late it is called.
        ///
        /// Precedence, which that constraint decides rather than taste:
        ///   - an empty queue wins, because a step that placed everything left was not cut short by anything;
        ///   - otherwise a spent cap wins, because it is the one exit condition still observable after the fact. A
        ///     step that spent its whole dynamic_dungeons_spawn_batch_size could not have taken another attempt no
        ///     matter what the clock said, so reporting `cap` for the tie is true, not merely convenient.
        ///   - anything else left the loop through dynamic_dungeons_spawn_step_budget_ms, so it is a budget stop.
        /// This is the reverse of the tie-break this function shipped with on 2026-09-21; the ms figure on the same
        /// line is what says whether a step also overshot its time, so the tie costs the reader nothing.
        /// </summary>
        internal static PlacementStepStop ResolvePlacementStepStop(int queueRemaining, ThreadLootRollBudget budget)
        {
            if (queueRemaining <= 0 || budget == null)
                return PlacementStepStop.Drained;

            return budget.Spent >= budget.MaxRolls ? PlacementStepStop.Cap : PlacementStepStop.Budget;
        }

        /// <summary>
        /// One placement step's Debug line. Pure over its inputs for the same reason the step loop is: the exact
        /// text a log reader greps is what the unit tests pin, not a restatement of it.
        ///
        /// attempted counts every plan entry the step DEQUEUED, placed or not (a failed TryPlace is an attempt and
        /// charges the cap, exactly as it always has), so attempted &gt; placed is the fallback-exhausted signal.
        /// Invariant culture on the millisecond figure so the line is identical on a machine whose decimal
        /// separator is a comma.
        /// </summary>
        internal static string PlacementStepLine(string run, int step, int attempted, int placed, double elapsedMs,
            PlacementStepStop stop, int cap, int budgetMs, int remaining, int spawnedTotal, int planned)
        {
            return $"[DYNDUNGEON] {run} place step {step}: attempted={attempted} placed={placed} " +
                   $"ms={elapsedMs.ToString("0.##", CultureInfo.InvariantCulture)} stop={StopName(stop)} " +
                   $"cap={cap} budgetMs={budgetMs} left={remaining} total={spawnedTotal}/{planned}";
        }

        /// <summary>The log token for a stop reason. Spelled out rather than ToString()'d, so renaming the enum cannot silently rewrite the log format log readers grep.</summary>
        private static string StopName(PlacementStepStop stop)
        {
            switch (stop)
            {
                case PlacementStepStop.Cap: return "cap";
                case PlacementStepStop.Budget: return "budget";
                case PlacementStepStop.Error: return "error";
                default: return "drained";
            }
        }

        /// <summary>
        /// TECH-DESIGN S5, ruling R16: unlock every locked door in a run's private copy. Runs once, as its OWN
        /// step on the landblock's action queue, before the first creature-placing step (see DoorPass).
        ///
        /// Two writes, not one. IsLocked=false is what lets Door.ActOnUse open it at all (Door.cs:93,108-116).
        /// DefaultLocked must go too, because Door.Reset re-locks a DefaultLocked door when its auto-close
        /// timer fires - ResetInterval seconds after the first use, default 30 (Door.cs:210-222). Clearing
        /// only IsLocked would give the player one pass through each door and then lock it behind them.
        /// DefaultLocked is protected on WorldObject (WorldObject_Use.cs:32-36), so it is cleared through
        /// RemoveProperty, which is exactly what that setter does for false.
        ///
        /// The broadcast mirrors WorldObject_Objective.OpenObjectiveGate (WorldObject_Objective.cs:369-373):
        /// the client renders and messages the locked state itself, so a player already tracking the door
        /// needs to be told. It is a no-op when nobody is in range, which is the normal case here.
        ///
        /// No per-guid overrides exist in the MVP DTOs (R16), so this is unconditional: every DefaultLocked
        /// door in a run copy is unlocked, and a curator cannot keep one shut until overrides land.
        /// </summary>
        private static int UnlockDoors(Landblock landblock) => UnlockDoors(landblock.GetAllWorldObjectsForDiagnostics());

        /// <summary>
        /// The pass over a set of objects, split out so the skip rule is unit-tested without a landblock. A puzzle
        /// gate (WorldObject.IsPuzzleGateObject) is SKIPPED: it must stay locked until its puzzle is solved, because
        /// monsters with AiOptions open an unlocked door by walking into it (Door.ActOnUse) and only IsLocked holds
        /// them. The puzzle pass already runs after this one, so the skip is the backstop, not the ordering.
        /// </summary>
        internal static int UnlockDoors(IEnumerable<WorldObject> objects)
        {
            var unlocked = 0;

            foreach (var wo in objects)
            {
                if (!(wo is Door door))
                    continue;

                if (door.IsPuzzleGateObject)
                    continue;

                var defaultLocked = door.GetProperty(PropertyBool.DefaultLocked) ?? false;

                if (!defaultLocked && !door.IsLocked)
                    continue;

                door.RemoveProperty(PropertyBool.DefaultLocked);

                if (door.IsLocked)
                {
                    door.IsLocked = false;
                    door.EnqueueBroadcast(new GameMessagePublicUpdatePropertyBool(door, PropertyBool.Locked, false));
                }

                unlocked++;
            }

            return unlocked;
        }

        /// <summary>
        /// A tunable is a long and every consumer here is an int. A bare cast WRAPS - a value above
        /// int.MaxValue arrives negative and silently disables the cap it was meant to raise - so saturate
        /// instead. Negatives clamp to 0, which the builder already treats as "no slots".
        /// </summary>
        private static int ClampToInt(long value) => (int)Math.Clamp(value, 0L, int.MaxValue);

        /// <summary>The shipped default of dynamic_dungeons_spawn_step_budget_ms.</summary>
        internal const int DefaultSpawnStepBudgetMs = 4;

        /// <summary>dynamic_dungeons_spawn_step_budget_ms is clamped to [<see cref="MinSpawnStepBudgetMs"/>, <see cref="MaxSpawnStepBudgetMs"/>] at read.</summary>
        internal const int MinSpawnStepBudgetMs = 1;
        internal const int MaxSpawnStepBudgetMs = 100;

        /// <summary>
        /// Clamps a configured dynamic_dungeons_spawn_step_budget_ms to [1, 100]. 1 ms still places one creature per
        /// step (progress is guaranteed by the budget, not by this floor); 100 ms is a sanity ceiling generous enough that
        /// dynamic_dungeons_spawn_batch_size normally ends a step first, which approximates the pre-budget behaviour for a live A/B.
        /// </summary>
        internal static int ClampSpawnStepBudgetMs(long configured) => (int)Math.Clamp(configured, MinSpawnStepBudgetMs, MaxSpawnStepBudgetMs);

        private static int ReadSpawnStepBudgetMs()
        {
            var configured = PropertyManager.GetLong("dynamic_dungeons_spawn_step_budget_ms", DefaultSpawnStepBudgetMs).Item;
            var used = ClampSpawnStepBudgetMs(configured);

            if (used != configured)
                log.Warn($"[DYNDUNGEON] dynamic_dungeons_spawn_step_budget_ms is configured as {configured} which is out of range ({MinSpawnStepBudgetMs} to {MaxSpawnStepBudgetMs}); using {used} instead");

            return used;
        }

        /// <summary>
        /// One placement step's loop, pure over its inputs so the stopping rule is unit-testable without a landblock:
        /// dequeue plan entries IN ORDER and hand each to <paramref name="place"/> until the queue is empty or
        /// <paramref name="budget"/> is exhausted. Returns the number of entries attempted this step.
        ///
        /// The budget is a <see cref="ThreadLootRollBudget"/> reused as a plain step allowance (one unit per creature
        /// ATTEMPT, placed or not, exactly as the old `n &lt; batchSize` counter charged a failed TryPlace): its hard
        /// cap is dynamic_dungeons_spawn_batch_size, and a timed budget is exhausted after the attempt during which
        /// its clock crosses dynamic_dungeons_spawn_step_budget_ms. The first attempt of a step is always admitted,
        /// so a step makes progress however slow one creature is.
        ///
        /// An exception from <paramref name="place"/> propagates to the caller, as it did from the inline loop:
        /// PlaceBatch's own catch owns finishing the populate. <paramref name="stop"/> is then never assigned,
        /// which is why PlaceBatch declares it before the try and sets it to Error in the catch.
        ///
        /// <paramref name="stop"/> is classified HERE, where the loop exits, rather than by the caller: it is what
        /// the per-step Debug line reports, and the caller cannot recover it afterwards without re-reading a clock
        /// that has moved on (see <see cref="ResolvePlacementStepStop"/>).
        /// </summary>
        internal static int RunPlacementStep<T>(Queue<T> queue, ThreadLootRollBudget budget, Action<T> place, out PlacementStepStop stop)
        {
            var attempted = 0;

            while (queue.Count > 0 && !budget.Exhausted)
            {
                var entry = queue.Dequeue();
                attempted++;

                place(entry);

                budget.Spend(1);
            }

            stop = ResolvePlacementStepStop(queue.Count, budget);
            return attempted;
        }

        /// <summary>
        /// Resolves the LIVE gem-level reward-scale ratio for <paramref name="level"/> - the same five
        /// tunables <see cref="TryPopulate"/> reads to build a run's <see cref="DungeonPopulationLimits"/>,
        /// exposed here so <see cref="ThreadDungeonGemHandler.ComposeLongDesc(DungeonGemSpec, string, int, int)"/>
        /// can print the EFFECTIVE Experience/Luminance/Loot Quantity totals a run actually pays, without a
        /// second, independently-drifting copy of this resolution living in the gem handler.
        ///
        /// Returns 1.0 (the ratio's own neutral no-op value) when the master switch is off, exactly matching
        /// <see cref="DungeonPopulationBuilder.Build"/>'s own "disabled reproduces today's behaviour" rule.
        /// <see cref="DungeonRewardMath.RewardScaleRatio"/> re-sanitizes the four curve dials itself (NaN,
        /// Infinity, a non-positive anchor, etc. all fall back to the compiled default), so no separate
        /// sanitizing pass is needed here the way the boss/reward-scalar dials above need ReadDoubleDial.
        ///
        /// EXPLICIT fallback arguments on every GetBool/GetDouble call, same reasoning as <see cref="TryPopulate"/>'s
        /// own five reward-scale reads: a key with no DB row yet (before the next PropertyManager.DoWork sync)
        /// must read as the compiled default, not GetBool/GetDouble's own generic false/0.0.
        /// </summary>
        internal static double ResolveRewardScaleRatio(int level)
        {
            if (!PropertyManager.GetBool("dynamic_dungeons_reward_scaling", DungeonPopulationLimits.DefaultRewardScalingEnabled).Item)
                return 1.0;

            return DungeonRewardMath.RewardScaleRatio(level,
                PropertyManager.GetDouble("dynamic_dungeons_reward_scale_anchor", DungeonPopulationLimits.DefaultRewardScaleAnchor).Item,
                PropertyManager.GetDouble("dynamic_dungeons_reward_scale_exponent", DungeonPopulationLimits.DefaultRewardScaleExponent).Item,
                PropertyManager.GetDouble("dynamic_dungeons_reward_scale_floor", DungeonPopulationLimits.DefaultRewardScaleFloor).Item,
                PropertyManager.GetDouble("dynamic_dungeons_reward_scale_cap", DungeonPopulationLimits.DefaultRewardScaleCap).Item);
        }

        /// <summary>
        /// Resolves the LIVE dynamic_dungeons_modifier_level_exponent - the k of the modifier magnitude curve
        /// (<see cref="DungeonModifierLevelScale"/>). Shared by <see cref="TryPopulate"/>, the gem's LongDesc
        /// (ThreadDungeonGemHandler.ComposeLongDesc) and the press summary, so all three apply one value.
        ///
        /// EXPLICIT fallback on the read, same reasoning as the reward-scale reads above: GetDouble's own
        /// fallback is 0.0 before PropertyManager.DoWork seeds a row, and 0.0 is this dial's "off" value - an
        /// unseeded read would silently run every low-level gem at full strength. NaN or Infinity reads as the
        /// compiled default; zero or negative is kept, because k &lt;= 0 is the documented way to turn the curve
        /// off (DungeonModifierLevelScale.Factor returns 1.0 for it).
        /// </summary>
        internal static double ResolveModifierLevelExponent()
            => SanitizeModifierLevelExponent(PropertyManager.GetDouble("dynamic_dungeons_modifier_level_exponent",
                DungeonPopulationLimits.DefaultModifierLevelExponent).Item);

        /// <summary>The pure half of <see cref="ResolveModifierLevelExponent"/>: NaN/Infinity reads as the default.</summary>
        internal static double SanitizeModifierLevelExponent(double value)
            => double.IsNaN(value) || double.IsInfinity(value) ? DungeonPopulationLimits.DefaultModifierLevelExponent : value;

        /// <summary>
        /// Sanitizes one of the run's double dials - the two boss uplift dials and the two reward scalars.
        /// NaN, Infinity or a negative value reads as the compiled default; zero is kept, because zero is the
        /// documented way to disable that axis; anything above the ceiling reads as the ceiling.
        ///
        /// Pure and side-effect free on purpose - the WARN lives in the caller, so a unit test can assert the
        /// arithmetic without a configured log4net or a live PropertyManager.
        /// </summary>
        internal static double SanitizeDoubleDial(double value, double fallback, double ceiling)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0)
                return fallback;

            return Math.Min(value, ceiling);
        }

        /// <summary>
        /// The hollow intensity to write onto a spawning creature, given the plan's rolled intensity and
        /// whatever the weenie already carries. NEVER lowers an authored hollow: if the creature already
        /// carries IgnoreMagicArmor/IgnoreMagicResist (author-set, or the trait strip left it alone), its own
        /// clamped intensity is the floor, and the plan's intensity can only raise it (Math.Max), the same
        /// "never LOWER a creature that was authored with more" rule <see cref="ThreadDungeonSpawner"/> already
        /// applies to IgnoreShield.
        /// </summary>
        internal static double ResolveSpawnedHollowIntensity(double planIntensity, bool creatureAlreadyHollow, double? creatureHollowIntensity)
        {
            var existing = creatureAlreadyHollow ? HollowMath.ClampOrOne(creatureHollowIntensity) : 0.0;

            return Math.Max(existing, planIntensity);
        }

        /// <summary>
        /// One creature's authored health, normalized onto the band-standard health curve: the authored value
        /// times <paramref name="ratio"/> (plan.HealthNormalizeRatio, which is DungeonHealthCurve's target for
        /// the gem's level over the measured cross-family band median). ONE rounding rule, used by BOTH the
        /// trash path and the boss path - the arithmetic is deliberately not written out twice, because two
        /// copies of a rounding rule are two rules the moment either is edited.
        ///
        /// A NO-OP, returning <paramref name="authored"/> untouched, for a <paramref name="ratio"/> of 0 (the
        /// master switch off, or no usable band sample to divide by), exactly 1.0 (a band already sitting on
        /// the curve), or a garbled value (NaN, Infinity, negative). 0 and 1 must be arithmetically identical
        /// to not calling this at all, because that is what makes the switched-off run byte-for-byte the run it
        /// was before the curve existed.
        ///
        /// DOWNWARD REACHABILITY IS A REAL CONSTRAINT AND IT IS ACCEPTED. Health is applied through
        /// WorldEventSpawner.ApplyHealth, which adjusts the live Creature's Health.StartingValue and clamps it
        /// at 1 - so the lowest Health.MaxValue any creature can be driven to is
        /// 1 + MaxHealth.LevelFromCP + Endurance.InitLevel/2. The CP-parked portion of a weenie's health has no
        /// per-instance setter, so a heavily CP-loaded creature cannot be normalized DOWN past it. Measured
        /// over all fourteen Raw Fragment rungs against the local ace_world (2026-09-12, through
        /// DungeonRosterSelector.BandMedianHealth itself), at most 3 creatures per band are affected and only
        /// ONE of them lands above its band's standard at all: wcid 48753, whose authored 3301 is almost
        /// entirely CP-parked (unreachable floor 3302), at 2.4x / 2.1x / 1.8x the standard in the 215, 225 and
        /// 235 bands - so at the 225 rung it sits at 3302 where the standard is 1568. The other two (34979 and
        /// 35161) miss their own normalized target but still land comfortably BELOW the band standard. This
        /// fails SAFE -
        /// rare, slightly tougher than intended, never impossible - and the existing "health target unreachable"
        /// Warn at the call sites below already reports every instance of it. Deliberately NOT worked around:
        /// an exclusion mechanism would remove playable creatures from thin bands, and writing to the weenie
        /// would change the creature everywhere else in the world.
        ///
        /// Pure and side-effect free on purpose, on the same contract as SanitizeDoubleDial: the log lines live
        /// in the caller so this exact arithmetic - not a restatement of it - is what the unit tests pin.
        /// </summary>
        internal static uint NormalizedBase(uint authored, double ratio)
        {
            if (double.IsNaN(ratio) || double.IsInfinity(ratio) || ratio <= 0 || ratio == 1.0)
                return authored;

            return (uint)Math.Clamp(Math.Round(authored * ratio), 1, uint.MaxValue);
        }

        /// <summary>
        /// The non-boss health target: normalize, then floor, then multiply (owner ruling 2026-09-07 for the
        /// floor's position, extended 2026-09-12 for the normalization).
        ///
        /// <paramref name="normalizeRatio"/> scales <paramref name="authoredMax"/> onto the band-standard
        /// curve first (see <see cref="NormalizedBase"/>); <paramref name="floor"/> (plan.TrashHealthFloor)
        /// then raises the result; only then is <paramref name="hpMult"/> applied. The floor-BEFORE-multiply
        /// half is the opposite order from <see cref="DungeonRewardMath.BossHealthTarget"/>, which multiplies
        /// first and floors the result against the observed pack maximum. The two floors measure different
        /// things (a band standard here, a pack maximum there); do not unify their order.
        ///
        /// The normalization comes FIRST because it is a restatement of the creature's own base - what this
        /// creature would carry if the authored ladder were smooth - and both the floor and the gem's
        /// multiplier are expressed against that base, not against the raw weenie value. Normalizing after the
        /// floor would scale the floor itself, which would make the floor mean something different at every
        /// level.
        ///
        /// A FLOOR only: a creature already at or above <paramref name="floor"/> is untouched by that term, and
        /// <paramref name="floor"/> = 0 (the axis disabled, or an empty band pool) leaves only the
        /// normalization. A <paramref name="normalizeRatio"/> of 0 or 1 is arithmetically identical to the
        /// pre-curve three-term formula.
        ///
        /// Pure and side-effect free on purpose, on the same contract as SanitizeDoubleDial: the log lines
        /// live in the caller so this exact arithmetic - not a restatement of it - is what the unit tests pin.
        /// </summary>
        internal static int NonBossHealthTarget(uint authoredMax, double normalizeRatio, double hpMult, uint floor)
        {
            var baseHealth = Math.Max(NormalizedBase(authoredMax, normalizeRatio), floor);
            return (int)Math.Clamp(Math.Round(baseHealth * hpMult), 1, int.MaxValue);
        }

        /// <summary>
        /// The non-boss health WRITE (pure): the <see cref="NonBossHealthTarget"/> computed from the AUTHORED max, or
        /// null when the creature's LIVE max already equals it (nothing to write). The two maxima differ exactly
        /// when the uplift's attribute raise moved Endurance before this runs: the target must come from the
        /// authored value (so the raise changes nothing about final health) while the skip check must see the
        /// live one (so a raised max is still brought back to the target). Writing through ApplyHealth also
        /// refills Health.Current to the new max.
        /// </summary>
        internal static int? NonBossHealthWrite(uint authoredMax, uint liveMax, double normalizeRatio, double hpMult, uint floor)
        {
            var target = NonBossHealthTarget(authoredMax, normalizeRatio, hpMult, floor);

            return target != liveMax ? target : (int?)null;
        }

        /// <summary>
        /// The ratio handed to the LEGACY boss health target (boss normalization off,
        /// DungeonRewardMath.BossHealthTarget), whose floor term is observedMaxNonBoss x ratio. The observed pack
        /// maximum already carries H, so on a group run the ratio is scaled by E / H: the floor term becomes
        /// (pack / H) x E x ratio and the group boss scales by E like the normalized path, instead of by H
        /// (fix round 1, MINOR 3). A solo run (GroupRosterSize 1) returns the plan ratio untouched. A
        /// non-positive or non-finite E or H reads as no scaling.
        /// </summary>
        internal static double LegacyBossFloorRatio(DungeonSpawnPlan plan, double effort)
        {
            if (plan.GroupRosterSize <= 1)
                return plan.BossHealthFloorRatio;

            var h = plan.GroupHealthMult;

            if (double.IsNaN(effort) || double.IsInfinity(effort) || effort <= 0 || double.IsNaN(h) || double.IsInfinity(h) || h <= 0)
                return plan.BossHealthFloorRatio;

            return plan.BossHealthFloorRatio * (effort / h);
        }

        /// <summary>
        /// The damage rating TryPlace adds to a run creature: the plan's pack or (already floored) boss rating,
        /// plus the Group Threads bonus (ruling R15). The bonus is added HERE, after the builder's boss floor
        /// max(mods, DamageRating + floor), so the floor neither swallows it nor counts it twice. The sum
        /// saturates at the int range rather than wrapping, since an uncapped bonus (cap 0) can reach
        /// int.MaxValue. A solo plan's bonus is 0, so its value is exactly the plan rating.
        /// </summary>
        internal static int StampedDamageRating(DungeonSpawnPlan plan, bool isBoss)
        {
            var rating = isBoss ? plan.BossDamageRating : plan.DamageRating;

            if (plan.GroupDamageRatingBonus == 0)
                return rating;

            return (int)Math.Clamp((long)rating + plan.GroupDamageRatingBonus, int.MinValue, int.MaxValue);
        }

        /// <summary>
        /// Sanitizes one of the run's integer dials - the two boss rating floors and the adaptive band's two
        /// count thresholds, which share this rule exactly - on the same shape as SanitizeDoubleDial: a negative
        /// value reads as the compiled default (NOT as 0, which would silently remove the floor the tunable
        /// exists to set), zero is a valid explicit disable, and a value past int.MaxValue saturates rather
        /// than wrapping negative. No typo ceiling here - an absurd rating floor produces silly damage
        /// numbers, not an unfinishable run.
        /// </summary>
        internal static int SanitizeBossFloor(long value, int fallback)
        {
            if (value < 0)
                return fallback;

            return (int)Math.Min(value, int.MaxValue);
        }

        /// <summary>
        /// Sanitizes dynamic_dungeons_defense_skill_cap_offset. Deliberately NOT SanitizeDoubleDial's rule:
        /// there, 0 is the documented way to disable an axis, but 0 is a meaningful, valid cap here ("exactly at
        /// the band's effective median"), so a negative value is this axis's own "disable" instead and passes
        /// through unchanged rather than falling back to the default. NaN or Infinity still reads as the
        /// default, and a value above the ceiling reads as the ceiling, on the usual typo-ceiling contract.
        /// </summary>
        internal static double SanitizeDefenseSkillCapOffset(double value, double fallback, double ceiling)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return fallback;

            if (value < 0)
                return value;

            return Math.Min(value, ceiling);
        }

        /// <summary>
        /// Reads and sanitizes the defense-skill-cap offset dial, warning when the value in use is not the
        /// value configured. Read WITH the explicit fallback argument (this dial SHIPS ON, same reasoning as
        /// ReadShippedDoubleDial): GetDouble's own built-in fallback is 0.0 before PropertyManager.DoWork seeds
        /// a row, which would read as a genuinely-configured "cap at the median" rather than the shipped 100.
        /// </summary>
        private static double ReadDefenseSkillCapOffsetDial(string tunable, double fallback, double ceiling)
        {
            var configured = PropertyManager.GetDouble(tunable, fallback).Item;
            var used = SanitizeDefenseSkillCapOffset(configured, fallback, ceiling);

            if (double.IsNaN(configured) || Math.Abs(used - configured) > 1e-9)
                log.Warn($"[DYNDUNGEON] {tunable} is configured as {configured} which is out of range (negative disables, 0 to {ceiling} otherwise); using {used} instead");

            return used;
        }

        /// <summary>Reads and sanitizes a double dial, warning when the value in use is not the value configured.</summary>
        private static double ReadDoubleDial(string tunable, double fallback, double ceiling)
        {
            var configured = PropertyManager.GetDouble(tunable).Item;
            var used = SanitizeDoubleDial(configured, fallback, ceiling);

            // NaN compares false against everything, so test it explicitly rather than by difference.
            if (double.IsNaN(configured) || Math.Abs(used - configured) > 1e-9)
                log.Warn($"[DYNDUNGEON] {tunable} is configured as {configured} which is out of range (0 to {ceiling}); using {used} instead");

            return used;
        }

        /// <summary>
        /// ReadDoubleDial for a dial added with boss normalization, which SHIPS ON (owner ruling 2026-09-13):
        /// identical sanitizing (SanitizeDoubleDial - NaN/Infinity/negative reads as the default, 0 is an admin's
        /// explicit disable, anything above the ceiling reads as the ceiling), but read WITH the explicit
        /// fallback so an unseeded row reads as the shipped value rather than GetDouble's 0.0, which
        /// SanitizeDoubleDial would accept as "disabled".
        /// </summary>
        private static double ReadShippedDoubleDial(string tunable, double fallback, double ceiling)
        {
            var configured = PropertyManager.GetDouble(tunable, fallback).Item;
            var used = SanitizeDoubleDial(configured, fallback, ceiling);

            if (double.IsNaN(configured) || Math.Abs(used - configured) > 1e-9)
                log.Warn($"[DYNDUNGEON] {tunable} is configured as {configured} which is out of range (0 to {ceiling}); using {used} instead");

            return used;
        }

        /// <summary>
        /// Sanitizes one of the roster band's two edge multipliers. Deliberately NOT SanitizeDoubleDial's rule:
        /// there, 0 is the documented way to disable a reward axis. A band edge has no "disabled" state - a
        /// zero low edge admits every creature in the game regardless of the gem's level, and a zero high edge
        /// collapses the band onto (or below) the low edge. Neither is a meaningful tuning choice, so NaN,
        /// Infinity AND any value <= 0 all read as the compiled default; anything above the ceiling reads as
        /// the ceiling. This is DungeonRosterSelector.DungeonRosterBand's own sanitize rule, restated here so
        /// ReadBandDial can log a WARN against the SAME arithmetic the struct applies - see that struct's doc
        /// comment for the full "why".
        ///
        /// Pure and side-effect free on purpose, on the same contract as SanitizeDoubleDial.
        /// </summary>
        internal static double SanitizeBandDial(double value, double fallback, double ceiling)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
                return fallback;

            return Math.Min(value, ceiling);
        }

        /// <summary>
        /// Reads and sanitizes one of the roster band's two edge multipliers, warning when the value in use is
        /// not the value configured.
        ///
        /// Reads PropertyManager.GetDouble WITH the explicit fallback argument, not the bare overload -
        /// GetDouble's own built-in fallback is 0.0 before PropertyManager.DoWork seeds a row from
        /// DefaultDoubleProperties (see the reward-scaling dials above for the same trap, found in review
        /// 2026-09-07), and 0.0 is NOT caught by SanitizeBandDial's NaN/Infinity/negative check on its own -
        /// it reads as a value, not as "missing". For a band edge that leaked 0.0 is worse than for the reward
        /// scalars above: those degrade to "no bonus", but PickFamily/IsEligible would evaluate an actual
        /// zero-width or inverted-multiplier band and could find almost nothing eligible until the next
        /// DoWork tick fixes it up. Passing the compiled default as the explicit fallback closes that window;
        /// SanitizeBandDial's own <= 0 rule then catches a genuinely-configured zero the same way.
        /// </summary>
        private static double ReadBandDial(string tunable, double fallback, double ceiling)
        {
            var configured = PropertyManager.GetDouble(tunable, fallback).Item;
            var used = SanitizeBandDial(configured, fallback, ceiling);

            if (double.IsNaN(configured) || Math.Abs(used - configured) > 1e-9)
                log.Warn($"[DYNDUNGEON] {tunable} is configured as {configured} which is out of range (above 0, up to {ceiling}); using {used} instead");

            return used;
        }

        /// <summary>
        /// Sanitizes the adaptive band's low-edge floor. A THIRD rule again, and for a third reason: here the
        /// documented "disabled" value is 1.0 (the low edge cannot move below the natural one), not 0 as it is
        /// for SanitizeDoubleDial's reward axes, and not "fall back to the default" as it is for
        /// SanitizeBandDial's edge multipliers.
        ///
        /// NaN, Infinity or a value at or below 0 reads as the compiled default: a zero or negative floor
        /// would let the low edge reach level 0 and admit every creature in the game regardless of the gem,
        /// which is the same degeneracy SanitizeBandDial refuses for the edges themselves. Anything ABOVE 1.0
        /// reads as 1.0 rather than as the default, because a floor above the natural low edge could only
        /// describe widening UPWARD - and the high edge never moves - so the honest reading of it is
        /// "disabled", and clamping there is what makes an admin's 2.0 behave the way its wording implies
        /// instead of silently restoring the shipped 0.60.
        ///
        /// Pure and side-effect free on purpose, on the same contract as SanitizeDoubleDial.
        /// </summary>
        internal static double SanitizeBandFloorDial(double value, double fallback)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
                return fallback;

            return Math.Min(value, 1.0);
        }

        /// <summary>
        /// Reads and sanitizes one of the health curve's two anchors, warning when the value in use is not the
        /// value configured.
        ///
        /// Sanitizing is DungeonHealthCurve.SanitizeAnchor's own rule, called rather than restated so the WARN
        /// and the curve cannot disagree - the same contract ReadBandDial follows with SanitizeBandDial. A
        /// FOURTH rule, and for a fourth reason: an anchor has no "disabled" value at all, so zero falls back
        /// to the compiled default rather than meaning "disable this axis" (SanitizeDoubleDial) or clamping to
        /// a bound (SanitizeBandFloorDial).
        ///
        /// Reads GetDouble WITH the explicit fallback argument, not the bare overload, for the reason
        /// ReadBandDial documents at length: GetDouble's own built-in fallback is 0.0 before
        /// PropertyManager.DoWork seeds a row from DefaultDoubleProperties, and that 0.0 would be
        /// indistinguishable from a genuinely configured zero. Here the two end up at the same place anyway
        /// (both read as the compiled default), so this is belt-and-braces rather than load-bearing - but it
        /// keeps the WARN honest, by not complaining about a value nobody configured.
        /// </summary>
        private static double ReadAnchorDial(string tunable, double fallback, double ceiling)
        {
            var configured = PropertyManager.GetDouble(tunable, fallback).Item;
            var used = DungeonHealthCurve.SanitizeAnchor(configured, fallback, ceiling);

            if (double.IsNaN(configured) || Math.Abs(used - configured) > 1e-9)
                log.Warn($"[DYNDUNGEON] {tunable} is configured as {configured} which is out of range (above 0, up to {ceiling}); using {used} instead");

            return used;
        }

        /// <summary>
        /// Reads and sanitizes dynamic_dungeons_health_curve_top_level, warning when the value in use is not the
        /// value configured. Sanitizing is DungeonHealthCurve.SanitizeTopLevel's own rule (clamp into [375, 500]),
        /// called rather than restated so the WARN and the curve cannot disagree; read WITH the explicit fallback
        /// because GetLong's own fallback is 0 before a row is seeded, which would sanitize to the old 375 clamp.
        /// </summary>
        private static int ReadHealthCurveTopLevelDial()
        {
            const string tunable = "dynamic_dungeons_health_curve_top_level";
            var configured = PropertyManager.GetLong(tunable, DungeonPopulationLimits.DefaultHealthCurveTopLevel).Item;
            var used = DungeonHealthCurve.SanitizeTopLevel(configured);

            if (used != configured)
                log.Warn($"[DYNDUNGEON] {tunable} is configured as {configured} which is out of range ({DungeonHealthCurve.AnchorHighLevel} to {DungeonGemSpec.MaxRunLevel}); using {used} instead");

            return used;
        }

        /// <summary>
        /// Reads and sanitizes dynamic_dungeons_defense_curve_rate_above_375 through DungeonStatCurve's own rule
        /// ([0, 1]; NaN or Infinity reads as the default), warning when the value in use differs. Explicit fallback
        /// for the usual reason: GetDouble's own is 0.0 before a row is seeded, and 0.0 is a legal value here
        /// (defense frozen at 375) that the sanitizer would let through.
        /// </summary>
        private static double ReadDefenseCurveRateDial()
        {
            const string tunable = "dynamic_dungeons_defense_curve_rate_above_375";
            var configured = PropertyManager.GetDouble(tunable, DungeonPopulationLimits.DefaultDefenseCurveRateAbove375).Item;
            var used = DungeonStatCurve.SanitizeDefenseRate(configured);

            if (double.IsNaN(configured) || Math.Abs(used - configured) > 1e-9)
                log.Warn($"[DYNDUNGEON] {tunable} is configured as {configured} which is out of range (0 to 1); using {used} instead");

            return used;
        }

        /// <summary>Reads and sanitizes the band floor, warning when the value in use is not the value configured.</summary>
        private static double ReadBandFloorDial(string tunable, double fallback)
        {
            var configured = PropertyManager.GetDouble(tunable, fallback).Item;
            var used = SanitizeBandFloorDial(configured, fallback);

            if (double.IsNaN(configured) || Math.Abs(used - configured) > 1e-9)
                log.Warn($"[DYNDUNGEON] {tunable} is configured as {configured} which is out of range (above 0, up to 1.0); using {used} instead");

            return used;
        }

        /// <summary>Reads and sanitizes one of the adaptive band's two count thresholds, warning when they differ.</summary>
        private static int ReadCountDial(string tunable, int fallback)
        {
            var configured = PropertyManager.GetLong(tunable, fallback).Item;
            var used = SanitizeBossFloor(configured, fallback);

            if (used != configured)
                log.Warn($"[DYNDUNGEON] {tunable} is configured as {configured} which is out of range (0 to {int.MaxValue}); using {used} instead");

            return used;
        }

        /// <summary>Reads and sanitizes a boss integer floor, warning when the value in use is not the value configured.</summary>
        private static int ReadBossFloor(string tunable, int fallback)
        {
            var configured = PropertyManager.GetLong(tunable).Item;
            var used = SanitizeBossFloor(configured, fallback);

            if (used != configured)
                log.Warn($"[DYNDUNGEON] {tunable} is configured as {configured} which is out of range (0 to {int.MaxValue}); using {used} instead");

            return used;
        }

        /// <summary>
        /// Live weenie level, for the roster band math. Reads the world weenie cache rather than creating an
        /// object: the builder asks about far more wcids than it places.
        /// </summary>
        private static int LiveLevelOf(uint wcid)
        {
            var weenie = DatabaseManager.World.GetCachedWeenie(wcid);
            return weenie?.GetProperty(PropertyInt.Level) ?? 0;
        }

        /// <summary>
        /// Builds the physical fit predicate handed to <see cref="DungeonPopulationBuilder.Build"/>, or NULL
        /// when this dungeon constrains nothing.
        ///
        /// Returning null is the fail-open answer and it costs nothing downstream: the builder treats a null
        /// predicate as "no constraint", builds no projection at all, and produces a byte-identical plan to
        /// the one it produced before this feature existed. There are three ways to get it, and all three are
        /// normal rather than exceptional - the master switch is off, the dungeon has no clearance row, or its
        /// row was never measured (maxCollisionHeightFullAccess at or below zero).
        ///
        /// NO DAT READ IS TAKEN ON ANY OF THOSE PATHS. <see cref="LiveMovementHeightOf"/> is only ever reached
        /// through the returned delegate, so with the switch off the whole feature is inert, not merely
        /// neutral.
        ///
        /// The switch is read with the EXPLICIT-FALLBACK GetBool(key, default) form, and that matters for a
        /// default-TRUE switch: GetBool's own built-in fallback is false until PropertyManager.DoWork seeds a
        /// row from DefaultBooleanProperties, so the un-defaulted form would read false on a fresh database
        /// and quietly contradict the shipped default until the next sync. Same reasoning as
        /// dynamic_dungeons_reward_scaling and dynamic_dungeons_uplift_rename above.
        ///
        /// The clearance row's own `enabled` flag is deliberately NOT consulted: whether a dungeon may be
        /// drawn at all is index.json's decision, and a clearance row is a measurement of geometry either way.
        ///
        /// <paramref name="fitKey"/> is the returned predicate's STABLE IDENTITY, for ThreadPlanCache: the
        /// dungeon id and the two numbers the predicate closes over, round-tripped ("R") so no precision is
        /// lost between the key and the comparison it stands for. It is NULL on every path that returns a null
        /// predicate, and it is derived from exactly the values the closure captures and nothing else - a key
        /// that named fewer of them would serve one dungeon's projection to another.
        /// </summary>
        private static Func<uint, bool> ResolveFitPredicate(DungeonEntryDef dungeon, out string fitKey)
        {
            fitKey = null;

            if (dungeon == null || string.IsNullOrEmpty(dungeon.Id))
                return null;

            if (!PropertyManager.GetBool("dynamic_dungeons_fit_filter", DungeonFitFilter.DefaultFitFilterEnabled).Item)
                return null;

            if (!ThreadDungeonManager.Store.Clearance.TryGetValue(dungeon.Id, out var row) || row == null)
                return null;

            var height = row.MaxCollisionHeightFullAccess;

            if (double.IsNaN(height) || double.IsInfinity(height) || height <= 0)
                return null;

            var margin = PropertyManager.GetDouble("dynamic_dungeons_fit_headroom_margin", DungeonFitFilter.DefaultFitHeadroomMargin).Item;

            // Sanitized HERE, at the read, on the same contract every other Threads dial follows: a garbled
            // value degrades to the compiled default rather than propagating into the predicate. Note the NaN
            // test comes first - Math.Clamp(NaN, ...) returns NaN rather than a bound.
            if (double.IsNaN(margin) || double.IsInfinity(margin) || margin < 0)
                margin = DungeonFitFilter.DefaultFitHeadroomMargin;

            margin = Math.Min(margin, DungeonFitFilter.MaxFitHeadroomMargin);

            fitKey = string.Concat(dungeon.Id, "|", height.ToString("R", CultureInfo.InvariantCulture), "|",
                margin.ToString("R", CultureInfo.InvariantCulture));

            return wcid => DungeonFitFilter.Fits(LiveMovementHeightOf(wcid), height, margin);
        }

        /// <summary>
        /// The height of the body physics will actually try to move through this dungeon's doorways, in
        /// metres, for <see cref="DungeonFitFilter.Fits"/>.
        ///
        /// One world read - the SAME cached weenie <see cref="LiveLevelOf"/> and <see cref="LiveHealthOf"/>
        /// take, so the three can never disagree about a wcid - for two properties: PropertyDataId.Setup names
        /// the rig, and PropertyFloat.DefaultScale sizes it. AN ABSENT DefaultScale IS EXACTLY 1.0, which is
        /// what the physics layer itself uses (PhysicsObj.Scale defaults to 1.0f), not an approximation of it.
        ///
        /// The dat read behind it is memoised on SETUP ID in <see cref="setupMovementHeightCache"/>, unscaled;
        /// see that field for why one entry serves every scale.
        ///
        /// EVERY MISSING-DATA PATH FAILS OPEN, ending at DungeonFitFilter.DummyMovementHeight (0.20 m), which
        /// clears every dungeon in the clearance table: an unresolvable wcid, a weenie with no Setup, a setup
        /// that is not in the dat, and a server with no client dat loaded at all (GetSetupModel hands back the
        /// neutral empty SetupModel, whose Spheres list is empty).
        /// </summary>
        private static float LiveMovementHeightOf(uint wcid)
        {
            var weenie = DatabaseManager.World.GetCachedWeenie(wcid);

            if (weenie == null)
                return DungeonFitFilter.DummyMovementHeight;

            var setupId = weenie.GetProperty(PropertyDataId.Setup) ?? 0;
            var scale = (float)(weenie.GetProperty(PropertyFloat.DefaultScale) ?? 1.0);

            // A garbled DefaultScale must not produce a NaN height that then has to be handled downstream.
            // DungeonFitFilter.Fits is total for NaN and fails open anyway, but keeping the arithmetic clean
            // here means the memoised value is always a real measurement.
            if (float.IsNaN(scale) || float.IsInfinity(scale) || scale <= 0f)
                scale = 1f;

            var unscaled = setupMovementHeightCache.GetOrAdd(setupId,
                id => DungeonFitFilter.MovementHeight(WorldObject.GetSetupModel(id).Spheres, 1f));

            return unscaled * scale;
        }

        /// <summary>
        /// Live weenie authored max health, for the trash health floor's band median
        /// (DungeonRosterSelector.BandMedianHealth). Reads the SAME cached weenie <see cref="LiveLevelOf"/>
        /// reads, never a second data source - SpeciesTableDef intentionally carries no health field of its
        /// own (SpeciesTableDef.cs: the engine never reads Level/Name/Custom/Note from the file, and the live
        /// weenie is the source of truth for everything but Wcid and Role).
        ///
        /// Health.MaxValue (the computed getter on a live Creature) folds a dat-driven attribute formula this
        /// static-weenie read cannot evaluate, so this instead reproduces that formula directly from the
        /// weenie's authored attribute rows. The InitLevel + Endurance/2 portion is verified against live
        /// appraisal in CreatureCombatTests.MaxHealth_AddsHalfEnduranceToInitLevel; the LevelFromCP ("Ranks")
        /// term is required by CreatureVital.GetMaxValue (StartingValue + Ranks + attr, where StartingValue
        /// reads InitLevel and Ranks reads LevelFromCP off the SAME PropertyAttribute2nd row) and must be
        /// included or a CP-loaded weenie's health is undercounted - some role-0 trash weenies carry InitLevel
        /// 0 with the entire health value parked in LevelFromCP.
        ///
        /// Both attribute dictionaries can be null - ACE.Entity.Models.Weenie's class doc comment states only
        /// populated collections are initialized - so a missing weenie or a missing MaxHealth entry returns 0,
        /// which BandMedianHealth reads as "no data" and excludes from its sample rather than as a genuine
        /// zero-health creature.
        /// </summary>
        private static uint LiveHealthOf(uint wcid) => HealthOfWeenie(DatabaseManager.World.GetCachedWeenie(wcid));

        /// <summary>
        /// The weenie-to-health extraction, in ONE place. Both <see cref="LiveHealthOf"/> and
        /// <see cref="ProfileOfWeenie"/> route through this rather than each doing its own dictionary walk:
        /// two copies of a formula whose whole subtlety is which of three authored rows it must include
        /// (the LevelFromCP term was missing once already) is exactly the kind of duplication that goes
        /// wrong silently, because both copies compile and only one is wrong.
        ///
        /// Takes the weenie rather than the wcid so it is unit-testable without a world database.
        /// </summary>
        internal static uint HealthOfWeenie(ACE.Entity.Models.Weenie weenie)
            => DungeonStatProfile.HealthOf(weenie);

        /// <summary>
        /// The band-standard uplift's single data delegate (see <see cref="DungeonStatProfile"/>), resolved
        /// off the SAME cached weenie <see cref="LiveLevelOf"/> and <see cref="LiveHealthOf"/> read, so the
        /// three can never disagree about the same wcid.
        /// </summary>
        private static DungeonStatProfile LiveProfileOf(uint wcid) => ProfileOfWeenie(DatabaseManager.World.GetCachedWeenie(wcid));

        /// <summary>
        /// Pure core of <see cref="LiveProfileOf"/>, taking the weenie so it is unit-testable without a world
        /// database.
        ///
        /// Every collection on ACE.Entity.Models.Weenie can be null - its class doc comment states only
        /// POPULATED collections are initialized - so each is checked before it is walked, and a weenie
        /// missing one simply reports 0 on that axis, which every consumer reads as "no authored data".
        ///
        /// Body-part damage is the MAXIMUM DVal across the parts rather than a mean: Monster_Melee.GetBaseDamage
        /// reads whichever single part the attack animation selected, and GetAttackPart already filters to
        /// parts with a nonzero DVal, so the largest is what the creature can actually hit for and the small
        /// parts are noise. Armour is the maximum for the parallel reason - Creature_BodyPart's armour read is
        /// per struck part, and the maximum is the one comparable number across creatures with different part
        /// counts.
        /// </summary>
        internal static DungeonStatProfile ProfileOfWeenie(ACE.Entity.Models.Weenie weenie)
            => DungeonStatProfile.FromWeenie(weenie);

        /// <summary>
        /// Pure core of <see cref="LiveHealthOf"/>: CreatureVital.GetMaxValue's formula
        /// (StartingValue + Ranks + attr) applied to a weenie's authored MaxHealth/Endurance rows -
        /// maxHealthInitLevel (StartingValue) plus maxHealthLevelFromCP (Ranks, the CP-invested portion of
        /// health - some role-0 trash weenies carry InitLevel 0 with the entire health value parked here)
        /// plus floor(enduranceInitLevel / 2). Split out from LiveHealthOf so this arithmetic can be unit
        /// tested without a live weenie/DB read.
        /// </summary>
        internal static uint HealthFromAttributes(uint maxHealthInitLevel, uint maxHealthLevelFromCP, uint enduranceInitLevel)
            => DungeonStatProfile.HealthFromAttributes(maxHealthInitLevel, maxHealthLevelFromCP, enduranceInitLevel);

        /// <summary>
        /// Removes a run creature's create-on-death rows: the retail weenie's own Contain and
        /// Treasure-without-Wield drops, which Creature_Death.GenerateTreasure instantiates fresh at death
        /// (Creature_Death.cs:819-838). This is where the "odd quest items piggybacking from the original
        /// weenie" come from.
        ///
        /// ASSIGN a fresh collection; do NOT Clear() or Remove() from the existing one. PropertiesCreateList
        /// is one of the FIVE collections WorldObject shares BY REFERENCE with the CACHED weenie
        /// (WorldObject.cs passes referenceWeenieCollectionsForCommonProperties: true, and
        /// ACE.Entity/Adapter/WeenieConverter.cs then does a bare
        /// `result.PropertiesCreateList = weenie.PropertiesCreateList`).
        /// Mutating it in place would strip the create list from every instance of that creature
        /// server-wide, retail landblocks included, until the world cache was invalidated - which for a
        /// vendor (Vendor.cs:141) or a quest NPC would be a severe production bug. The other four shared
        /// collections are PropertiesEmote, PropertiesEventFilter, PropertiesGenerator and
        /// PropertiesBodyPart; the same rule applies to any of them. PropertiesBodyPart is shared from a
        /// SECOND, separate branch of that converter rather than from the block the other four sit in, which
        /// is why it is easy to miss - see ApplyBodyPartUplift, which has to clone it for exactly this reason.
        ///
        /// Clearing the list here is deliberately TOO LATE to affect what the creature wields: GenerateWieldList
        /// reads the same collection (Creature_Equipment.cs:611-636) but runs during construction, from
        /// Creature.SetEphemeralValues (Creature.cs:195-211). The creature keeps its weapon and its
        /// appearance; only the death-time read at Creature_Death.cs:819 is affected.
        /// </summary>
        /// <remarks>
        /// Takes the biota rather than the Creature purely so the assignment-not-mutation rule above is
        /// unit-testable: ACE.Entity.Models.Biota is a POCO the harness can build through WeenieConverter,
        /// while a live Creature is not constructible without a world database. The parameter is the RUNTIME
        /// biota (ACE.Entity.Models.Biota), never the EF entity of the same name.
        /// </remarks>
        /// <summary>
        /// Resolves the live NumProjectiles for a spell id, memoized in <see cref="spellProjectilesCache"/>.
        /// Reads DatabaseManager.World.GetCachedSpell rather than constructing ACE.Server.Entity.Spell: that
        /// type also requires the client dat's SpellBase and reports NotFound without it, which would make
        /// this filter dat-dependent for a value (Spell.NumProjectiles, ACE.Server/Entity/SpellProperties.cs)
        /// that is a plain ace_world database column.
        /// </summary>
        private static int SpellProjectileCountOf(int spellId)
        {
            if (spellId <= 0)
                return 0;

            return spellProjectilesCache.GetOrAdd(spellId, id => DatabaseManager.World.GetCachedSpell((uint)id)?.NumProjectiles ?? 0);
        }

        /// <summary>
        /// True when the creature's biota carries at least one body part with a positive DVal - the same
        /// "can this thing melee for real damage" signal <see cref="ProfileOfWeenie"/> uses for its own
        /// maxDamage axis. Feeds DungeonSpellFilter.Strip's safety guard: a creature with no damaging body
        /// part must never have its spell book emptied outright.
        /// </summary>
        private static bool HasDamagingBodyPart(ACE.Entity.Models.Biota biota)
        {
            var parts = biota?.PropertiesBodyPart;

            if (parts == null)
                return false;

            foreach (var kvp in parts)
            {
                if (kvp.Value != null && kvp.Value.DVal > 0)
                    return true;
            }

            return false;
        }

        internal static void StripCreateList(ACE.Entity.Models.Biota biota)
        {
            if (biota == null)
                return;

            biota.PropertiesCreateList = new System.Collections.ObjectModel.Collection<PropertiesCreateList>();
        }

        /// <summary>
        /// Makes a run creature incapable of generating children (owner ruling 2026-10-05: every mob, boss
        /// included, has its generator stripped). Returns the number of generator profiles removed, 0 when
        /// there was nothing to strip.
        ///
        /// A weenie that carries PropertiesGenerator rows is a generator the moment it is constructed:
        /// WorldObject's constructors call InitializeGenerator (WorldObject.cs:146) which fills
        /// GeneratorProfiles from Biota.PropertiesGenerator, then InitializeHeartbeats (WorldObject.cs:147)
        /// arms NextGeneratorUpdateTime because IsGenerator is true (WorldObject_Tick.cs:72-79). So all three
        /// are reset here, and the collection is REPLACED, never mutated: Biota.PropertiesGenerator is shared
        /// BY REFERENCE with the cached weenie, and a Clear() would strip the generator from every instance
        /// of the wcid server-wide, retail landblocks included.
        ///
        /// Disabling the heartbeat slots matters on its own: Landblock skips a world object whose
        /// NextGeneratorUpdateTime is double.MaxValue when it indexes it (Landblock.cs:1482), so the tick
        /// never reaches Generator_Update/StartGenerator for it.
        /// </summary>
        internal static int StripGenerator(WorldObject creature)
        {
            if (creature == null)
                return 0;

            var profileCount = Math.Max(creature.GeneratorProfiles?.Count ?? 0, creature.Biota?.PropertiesGenerator?.Count ?? 0);

            if (profileCount == 0)
                return 0;

            creature.Biota.PropertiesGenerator = new List<PropertiesGenerator>();
            creature.GeneratorProfiles = new List<GeneratorProfile>();
            creature.NextGeneratorUpdateTime = double.MaxValue;
            creature.NextGeneratorRegenerationTime = double.MaxValue;

            return profileCount;
        }

        /// <summary>
        /// Removes the death-spawn trigger from a run creature: the same "a creature puts un-normalized
        /// children into the run" bug class as <see cref="StripGenerator"/>, through a route that is not a
        /// generator. Creature_Death.cs:112 calls DeathSpawner.TrySpawn, which reads PropertyInt.DeathSpawnWcid
        /// first and returns when it is absent or not positive (DeathSpawner.cs:51-54), so removing that one
        /// key disarms the whole primitive; DeathSpawnCount and DeathSpawnRadius are only read after it
        /// (DeathSpawner.cs:56-59) and are inert alone. Per instance: PropertiesInt is a NEW Dictionary copy
        /// out of WeenieConverter.ConvertToBiota (WeenieConverter.cs:29-30), not one of the collections shared
        /// by reference with the cached weenie. Returns true when a trigger was removed.
        /// </summary>
        internal static bool StripDeathSpawn(WorldObject creature)
        {
            if (creature == null)
                return false;

            var wcid = creature.GetProperty(PropertyInt.DeathSpawnWcid);

            if (wcid == null)
                return false;

            creature.RemoveProperty(PropertyInt.DeathSpawnWcid);

            return true;
        }

        /// <summary>
        /// Clears the corpse-transfer marker on everything a run creature is carrying or wielding, so none of
        /// its gear moves onto the corpse at death (Creature_Death.cs:795-816). Per instance: DestinationType
        /// is a bare field on each ITEM's WorldObject (WorldObject.cs:1104), not a biota property, so nothing
        /// is persisted, nothing is broadcast, and no other creature's copy of the same wcid is touched.
        ///
        /// Undef (0) clears every bit the death filter can test under either setting of the
        /// creatures_drop_createlist_wield tunable (Creature_Death.cs:798).
        /// </summary>
        internal static void StripCorpseTransfer(Creature creature)
        {
            if (creature == null)
                return;

            ClearDestinationType(creature.Inventory.Values);
            ClearDestinationType(creature.EquippedObjects.Values);
        }

        /// <summary>
        /// Per-item sweep that clears DestinationType on every item in the given collection, split out of
        /// StripCorpseTransfer so it is unit-testable without a live Creature - it only needs
        /// IEnumerable&lt;WorldObject&gt;. Undef (0) clears every bit the death filter can test
        /// (Creature_Death.cs:798).
        /// </summary>
        internal static void ClearDestinationType(IEnumerable<WorldObject> items)
        {
            if (items == null)
                return;

            foreach (var item in items)
            {
                if (item != null)
                    item.DestinationType = DestinationType.Undef;
            }
        }

        /// <summary>
        /// Normalizes one creature the adaptive band reached BELOW its natural low edge up to the standard
        /// that band actually carries (<see cref="DungeonBandStandard"/>). Called only for an entry with a
        /// nonzero <see cref="DungeonSpawnPlanEntry.UpliftLevel"/>, and only before EnterWorld.
        ///
        /// Four axes, each of them upward-only:
        ///   * LEVEL - a floor at the uplift level, so the examine panel and every level-gated server rule
        ///     (luminance, rare generation) see the creature the run is actually fielding;
        ///   * SKILLS - a per-skill floor at the band's median InitLevel, for every skill the creature ALREADY
        ///     carries. Never adds a skill it does not have: GetCreatureSkill's default overload would create
        ///     one Untrained, which would hand a melee creature a magic school it was never authored with;
        ///   * BODY-PART DAMAGE and ARMOUR - a RATIO, not a floor, applied to every part. A per-part floor
        ///     would flatten a creature's authored part profile (a wolf's bite and its tail becoming equal),
        ///     while a ratio taken against its own maximum preserves the shape of the creature and only
        ///     changes its magnitude.
        ///
        /// HEALTH is deliberately absent: DungeonSpawnPlan.TrashHealthFloor already floors every non-boss
        /// creature to the band's cross-family median health before the gem's multiplier, and it applies to an
        /// uplifted entry unchanged. A second health path here would double-apply.
        ///
        /// SPELLS are a fifth axis, but scaled EARLIER than the four above: DungeonSpellTier.Raise runs
        /// before the AoE/drain spell strip (see the call site's comment), because a raised id can land on a
        /// banned id or gain projectiles and the strip must see the final book. By the time this method runs
        /// the book is already at the band's spell tier; <paramref name="spellsRaised"/> is only the count,
        /// carried through for the diagnostic line.
        /// </summary>
        /// <param name="authoredLevel">
        /// The creature's OWN weenie level, captured by the caller BEFORE anything was stamped on it. It is
        /// passed in rather than read here because by the time this runs the boss level stamp has already
        /// executed, so <c>creature.Level</c> is no longer the authored value for a promoted boss - reading it
        /// here made the log line say "uplifted to level 200 (authored 220)", which reads backwards. Used for
        /// the diagnostic ONLY; the level floor below reads the creature's CURRENT level, because that is what
        /// makes the floor upward-only when the boss stamp has already raised it.
        /// </param>
        /// <param name="spellsRaised">
        /// How many spell-book entries DungeonSpellTier.Raise already raised, at the call site, before the
        /// AoE/drain strip ran. Diagnostic only - this method does not touch the spell book itself.
        /// </param>
        private static void ApplyUplift(ThreadDungeonRun run, Creature creature, DungeonSpawnPlanEntry entry, DungeonSpawnPlan plan,
            int authoredLevel, int spellsRaised, int attributesRaised, bool upliftAttributes)
        {
            // The writes live in BandUplift.Apply (shared with World Events); this wrapper keeps the Threads
            // log line. An empty or missing standard renders the "no band standard" form inside UpliftLogLine.
            var standard = plan.BandStandard;
            var (skillsRaised, partsScaled) = BandUplift.Apply(creature, entry.UpliftLevel, standard);

            // Effective-skill top-up and the defense-ceiling re-check (dynamic_dungeons_uplift_attributes): after
            // Apply so it measures against the final InitLevels, and after the attribute raise so the attribute
            // term it subtracts is the RAISED one. The cap gate mirrors CombatTraitGuard's (the strip is on).
            var toppedUp = 0;
            var ceilingsSet = 0;

            if (upliftAttributes)
                (toppedUp, ceilingsSet) = BandUplift.TopUpEffectiveSkillsAndCeil(creature, standard, plan.DefenseSkillCapOffset, plan.StripCombatTraits);

            log.Debug($"[DYNDUNGEON] {run} {UpliftLogLine(entry.Wcid, entry.UpliftLevel, authoredLevel, skillsRaised, partsScaled, spellsRaised, standard, attributesRaised, toppedUp, ceilingsSet)}");
        }

        /// <summary>
        /// The uplift diagnostic, as a pure function of the numbers it reports, so a unit test can pin what
        /// the line actually SAYS without a live Creature or a configured log4net.
        ///
        /// <paramref name="authoredLevel"/> is the creature's own weenie level, and it must be the value the
        /// caller captured BEFORE any stamp: for a promoted boss the boss level stamp runs first, so a line
        /// built from the creature's live level would report the STAMPED level as "authored" and read
        /// backwards ("uplifted to level 200 (authored 220)"). A null <paramref name="standard"/> renders the
        /// "no band standard available" form.
        /// </summary>
        internal static string UpliftLogLine(uint wcid, int upliftLevel, int authoredLevel, int skillsRaised, int partsScaled,
            int spellsRaised, DungeonBandStandard standard, int attributesRaised = 0, int skillsToppedUp = 0, int ceilingsSet = 0)
        {
            var head = $"wcid {wcid} uplifted to level {upliftLevel} (authored {authoredLevel}); ";

            if (standard == null || standard.IsEmpty)
                return head + "no band standard available, stats untouched";

            return head + $"{skillsRaised} skill(s) raised, {partsScaled} body part(s) scaled toward " +
                   $"dmg {standard.MaxBodyDamage} / armor {standard.MaxBaseArmor}, {spellsRaised} spell(s) raised to tier {standard.SpellTier}" +
                   (attributesRaised > 0 ? $", {attributesRaised} attribute(s) raised" : "") +
                   (skillsToppedUp > 0 || ceilingsSet > 0 ? $", {skillsToppedUp} skill(s) topped up to the effective median, {ceilingsSet} defense ceiling(s) set" : "");
        }

        /// <summary>
        /// Whether raising Endurance to <paramref name="raisedEndurance"/> still lets max health land EXACTLY on
        /// <paramref name="targetHealth"/> (pure). The raised Endurance contributes raisedEndurance / 2 to max
        /// health (CreatureVital formula); ApplyHealth can only lower StartingValue down to 1, so a target at or
        /// below that contribution is unreachable (authored 100, End raised to 400 -> max 201, not 100). False
        /// means the spawner skips the attribute uplift for that creature.
        /// </summary>
        internal static bool UpliftAttributeRaiseKeepsHealthReachable(int targetHealth, uint raisedEndurance)
            => targetHealth > (int)Math.Min(int.MaxValue, raisedEndurance / 2);

        /// <summary>
        /// Whether the Threads-only attribute uplift applies to one entry (pure, so the gate is unit-testable):
        /// the entry was uplifted, is NOT a boss (a boss is normalized by its own path, or deliberately left
        /// tougher than its pack), the band has a standard, and the kill switch
        /// (dynamic_dungeons_uplift_attributes) is on.
        /// </summary>
        internal static bool UpliftsAttributes(int upliftLevel, DungeonRole role, DungeonBandStandard standard, bool enabled)
            => enabled && upliftLevel > 0 && role != DungeonRole.Boss && standard != null && !standard.IsEmpty;

        /// <summary>
        /// Scales a creature's body-part DVal and BaseArmor toward the band standard's own maxima, and
        /// returns how many parts were rewritten.
        ///
        /// CLONES THE WHOLE COLLECTION BEFORE TOUCHING IT, and this is not optional. PropertiesBodyPart is a
        /// FIFTH collection WorldObject shares BY REFERENCE with the CACHED weenie - WorldObject.cs passes
        /// referenceWeenieCollectionsForCommonProperties: true and ACE.Entity/Adapter/WeenieConverter.cs then
        /// does a bare `result.PropertiesBodyPart = weenie.PropertiesBodyPart` - alongside the four
        /// (PropertiesCreateList, PropertiesEmote, PropertiesEventFilter, PropertiesGenerator) this file
        /// already documents. Mutating a part in place would raise the damage and armour of EVERY instance of
        /// that creature server-wide, on retail landblocks included, until the world cache was invalidated.
        /// Both the dictionary AND each PropertiesBodyPart value have to be copied: a fresh dictionary holding
        /// the same value objects would still write straight through into the cached weenie.
        ///
        /// Nothing caches the old references. Every combat read goes through Biota.PropertiesBodyPart at the
        /// moment it is needed (Monster_Melee.GetAttackPart, BodyParts.GetBodyPart, DamageEvent's constructor),
        /// so replacing the collection before EnterWorld is complete and per-instance.
        ///
        /// Takes the biota rather than the Creature so the clone-not-mutate rule is unit-testable: the runtime
        /// ACE.Entity.Models.Biota is a POCO the harness can build, while a live Creature is not constructible
        /// without a world database. The parameter is the RUNTIME biota, never the EF entity of the same name.
        /// </summary>
        internal static int ApplyBodyPartUplift(ACE.Entity.Models.Biota biota, uint standardMaxDamage, uint standardMaxArmor)
            => BandUplift.ApplyBodyPartUplift(biota, standardMaxDamage, standardMaxArmor);

        /// <summary>
        /// The upward-only ratio between a creature's own maximum on some axis and the band standard's. 1.0 -
        /// a no-op - whenever the standard has no data (0), whenever the creature has none (0 or less), and
        /// whenever the creature already matches or beats the standard.
        ///
        /// A zero own-maximum stays zero on purpose. A ratio cannot lift a zero, and substituting the
        /// standard's absolute value there would be a different (additive) axis with a different failure mode:
        /// a creature authored with no body-part damage at all is one that fights with a wielded weapon or
        /// with spells, and handing it a bite it never had would be inventing content, not normalizing it.
        /// </summary>
        internal static double UpliftRatio(int ownMax, uint standardMax)
            => BandUplift.UpliftRatio(ownMax, standardMax);

        /// <summary>
        /// One body-part value scaled by <paramref name="ratio"/>. The lower clamp is the ORIGINAL value, not
        /// zero: that is what makes invariant I-C ("no uplift axis ever lowers a value") true by construction
        /// rather than by the caller having checked the ratio first, and it costs one compare.
        /// </summary>
        internal static int ScaleBodyValue(int value, double ratio)
            => BandUplift.ScaleBodyValue(value, ratio);

        /// <summary>What one placement attempt came to. Only <see cref="Refused"/> is worth retrying elsewhere.</summary>
        private enum PlaceOutcome
        {
            /// <summary>The creature entered the world.</summary>
            Placed,

            /// <summary>
            /// EnterWorld returned false - the world could not fit the creature at that point. Position-dependent,
            /// so another point may succeed. NO ledger row was written; the caller decides which code applies.
            /// </summary>
            Refused,

            /// <summary>A terminal, position-independent failure. Its ledger row is already written.</summary>
            Failed,
        }

        /// <summary>
        /// Creates and places one plan entry, retrying a refused one at other curated points of the same dungeon
        /// (dynamic_dungeons_placement_fallback_attempts; order from DungeonPlacementFallback.OrderCandidates).
        /// Returns true only when the creature actually entered the world. Runs on the landblock's action queue.
        ///
        /// TELEMETRY, following the ace_analytics.sql dungeon_run_placement contract exactly:
        ///   - a refusal with NO candidate to retry (fallback 0, or nowhere else to go) is the terminal
        ///     enter_world_refused, as it always was;
        ///   - every retry at another point is one anchor_fallback_used attempt (is_failure 0, a recovery);
        ///   - a retry that places calls MarkEntryPlaced, which flips entry_placed on that recovery row;
        ///   - running out of candidates writes fallback_exhausted once (terminal) - and NOT
        ///     enter_world_refused as well, so "is_failure = 1 AND entry_placed = 0" still counts the missing
        ///     creature exactly once.
        /// A plain first-attempt success no longer calls MarkEntryPlaced. It used to, harmlessly, while no
        /// recovery row had a writer; now it would credit a SIBLING's fallback row for an entry that contributed
        /// no attempt to it, which is precisely the lie MarkEntryPlaced's own doc comment rules out.
        ///
        /// Invariant I6 is unaffected: a retry happens inside the same queue step as the first attempt, so the
        /// boss - the last plan entry - still places after every pack creature has placed or given up.
        /// </summary>
        /// <param name="observedMaxNonBoss">
        /// The largest Health.MaxValue any non-boss creature in this run has entered the world with so far.
        /// Read only when <paramref name="entry"/> is the Boss, where it is invariant I2's right-hand side.
        /// </param>
        /// <param name="occupied">
        /// Location keys (DungeonPlacementFallback.KeyOf) of every point a plan entry was assigned, plus every
        /// point a fallback retry has since claimed. Updated here when a retry places.
        /// </param>
        /// <param name="placedMaxHealth">
        /// The creature's final Health.MaxValue, for the caller's running maximum. 0 when nothing was placed.
        /// </param>
        private static bool TryPlace(ThreadDungeonRun run, Landblock landblock, DungeonSpawnPlan plan, DungeonSpawnPlanEntry entry,
            uint observedMaxNonBoss, int fallbackAttempts, HashSet<(uint, float, float, float)> occupied, out uint placedMaxHealth)
        {
            var first = TryPlaceOnce(run, landblock, plan, entry, observedMaxNonBoss, out placedMaxHealth);

            if (first != PlaceOutcome.Refused)
                return first == PlaceOutcome.Placed;

            var candidates = DungeonPlacementFallback.OrderCandidates(run.Dungeon?.Points, entry.Point, run.Dungeon?.BossAnchor,
                entry.Role == DungeonRole.Boss, p => occupied.Contains(DungeonPlacementFallback.KeyOf(p)), fallbackAttempts);

            if (candidates.Count == 0)
            {
                run.RecordPlacement(DungeonRunTelemetry.Phases.Place, DungeonRunTelemetry.Reasons.EnterWorldRefused, entry.Wcid, entry.Role);
                return false;
            }

            foreach (var point in candidates)
            {
                run.RecordPlacement(DungeonRunTelemetry.Phases.Place, DungeonRunTelemetry.Reasons.AnchorFallbackUsed, entry.Wcid, entry.Role);

                // EVERY tag carried through, not just the point: a retry rebuilt without its StampLevel would
                // place the creature at its authored level and stats in a room stamped for its projected one.
                var retry = RetryEntry(entry, point);
                var outcome = TryPlaceOnce(run, landblock, plan, retry, observedMaxNonBoss, out placedMaxHealth);

                if (outcome == PlaceOutcome.Placed)
                {
                    occupied.Add(DungeonPlacementFallback.KeyOf(point));
                    run.MarkEntryPlaced(entry.Wcid, entry.Role);
                    log.Debug($"[DYNDUNGEON] {run} wcid {entry.Wcid} ({entry.Role}) placed by fallback at cell 0x{point.Cell:X8} ({point.X:0.##}, {point.Y:0.##}, {point.Z:0.##})");
                    return true;
                }

                if (outcome == PlaceOutcome.Failed)
                    return false;
            }

            run.RecordPlacement(DungeonRunTelemetry.Phases.Place, DungeonRunTelemetry.Reasons.FallbackExhausted, entry.Wcid, entry.Role);
            log.Warn($"[DYNDUNGEON] {run} wcid {entry.Wcid} ({entry.Role}) refused at its point and at {candidates.Count} fallback point(s); not placed");
            return false;
        }

        /// <summary>
        /// The placement-fallback retry's copy of <paramref name="entry"/> at another <paramref name="point"/>
        /// (pure, so the "every tag survives the retry" rule is pinnable without a landblock). Wcid, role, uplift
        /// and reach-up stamp all carry; only the point moves.
        /// </summary>
        internal static DungeonSpawnPlanEntry RetryEntry(DungeonSpawnPlanEntry entry, DungeonSpawnPointDef point)
            => new DungeonSpawnPlanEntry(entry.Wcid, entry.Role, point, entry.UpliftLevel, entry.StampLevel);

        /// <summary>
        /// The level a placed creature is REWARDED at (pure): the stamped boss level for a boss, otherwise the
        /// highest of its authored level, its uplift and its reach-up stamp. The luminance gate reads this, and the
        /// XP anchor (DungeonPopulationBuilder.BaseXp) is handed the same uplift-or-stamp term, so a stamped creature
        /// is priced at the level it was placed at, never the lower one it was authored at.
        /// </summary>
        internal static int RewardLevel(DungeonSpawnPlanEntry entry, int ownLevel, int bossLevel)
            => entry.Role == DungeonRole.Boss && bossLevel > 0 ? bossLevel : Math.Max(ownLevel, entry.RaisedLevel);

        /// <summary>
        /// The stat curve a non-boss STAMPED entry is scaled on (pure), or null for no stamp scaling: an unstamped
        /// entry, a run with no curve, or a BOSS. A promoted boss can carry a stamp (DungeonPopulationBuilder's
        /// EntryFor tags it like any pack pick), but a normalized boss is SET to the curve standard at the run
        /// level by its own path and a legacy boss is left as authored, so it is never stamp-scaled.
        /// </summary>
        internal static DungeonStatCurve.Anchored StampCurveFor(DungeonSpawnPlanEntry entry, DungeonSpawnPlan plan)
            => entry.Role != DungeonRole.Boss && entry.StampLevel > 0 ? plan.StatCurve : null;

        /// <summary>
        /// The standard the #1173 defense ceiling reads for this entry (pure): the curve at a non-boss entry's own
        /// stamp, or null to fall back to plan.BandStandard (DungeonCreatureNormalizer.StripCombatTraits). The
        /// BOSS exclusion matches <see cref="StampCurveFor"/> (code review 2026-10-08): a stamped promoted boss
        /// was otherwise handed the higher stamp-level standard while every other boss reads the run's.
        /// </summary>
        internal static DungeonBandStandard DefenseCapStandard(DungeonSpawnPlanEntry entry, DungeonSpawnPlan plan)
            => entry.Role != DungeonRole.Boss && entry.StampLevel > 0 && plan.StatCurve != null ? plan.StatCurve.StandardAt(entry.StampLevel) : null;

        /// <summary>
        /// The authored max health captured for a reach-up stamp (pure): <paramref name="preStampMax"/> when the
        /// stamp actually scaled attributes (Endurance moved the live max), otherwise null - nothing moved, so the
        /// health block may read the live max as before.
        /// </summary>
        internal static uint? HealthMaxBeforeStamp(ReachUpStamp.Result stamped, uint preStampMax)
            => stamped.AttributesScaled > 0 ? preStampMax : (uint?)null;

        /// <summary>
        /// The max health the run's health terms are priced from (pure): the authored value captured before the
        /// uplift's attribute raise, else the one captured before a reach-up stamp's attribute scaling, else the
        /// live max. An entry is uplifted OR stamped, never both (DungeonPopulationBuilder's EntryFor).
        /// </summary>
        internal static uint AuthoredHealthMax(uint? beforeUplift, uint? beforeStamp, uint liveMax)
            => beforeUplift ?? beforeStamp ?? liveMax;
        /// <summary>One placement attempt of <paramref name="entry"/> at its own point. See <see cref="TryPlace"/>.</summary>
        private static PlaceOutcome TryPlaceOnce(ThreadDungeonRun run, Landblock landblock, DungeonSpawnPlan plan, DungeonSpawnPlanEntry entry,
            uint observedMaxNonBoss, out uint placedMaxHealth)
        {
            placedMaxHealth = 0;

            WorldObject wo;

            try
            {
                wo = WorldObjectFactory.CreateNewWorldObject(entry.Wcid);
            }
            catch (Exception ex)
            {
                log.Error($"[DYNDUNGEON] {run} could not create wcid {entry.Wcid}", ex);
                run.RecordPlacement(DungeonRunTelemetry.Phases.Place, DungeonRunTelemetry.Reasons.CreateThrew, entry.Wcid, entry.Role);
                return PlaceOutcome.Failed;
            }

            if (!(wo is Creature creature))
            {
                log.Warn($"[DYNDUNGEON] {run} wcid {entry.Wcid} did not resolve to a Creature; skipped");
                run.RecordPlacement(DungeonRunTelemetry.Phases.Place, DungeonRunTelemetry.Reasons.NotCreature, entry.Wcid, entry.Role);
                wo?.Destroy();
                return PlaceOutcome.Failed;
            }

            // A creature that cannot be killed can never contribute to the run's weighted clear progress
            // (owner ruling R28) - checked before any property write, same as the other refusal paths below.
            if (!DungeonPopulationBuilder.IsKillable(creature.Attackable, creature.PlayerKillerStatus))
            {
                log.Error($"[DYNDUNGEON] {run} wcid {entry.Wcid} ({entry.Role}) is not attackable or is an NPC; not placed");
                run.RecordPlacement(DungeonRunTelemetry.Phases.Place, DungeonRunTelemetry.Reasons.NotKillable, entry.Wcid, entry.Role);
                creature.Destroy();
                return PlaceOutcome.Failed;
            }

            var p = entry.Point;
            creature.Location = new Position(p.Cell, p.X, p.Y, p.Z, p.QX, p.QY, p.QZ, p.QW, run.Instance);

            // The same guard WorldEventSpawner.TryPlace takes before adopting a creature: a position on some
            // other landblock would drag the creature out of the copy the run owns and into a block nothing
            // is keeping awake or cleaning up. The store already rejects a spawn file whose points leave the
            // landblock, so this only fires on a curation/merge mistake - which is why it is a warning.
            if (creature.Location.LandblockId.Landblock != landblock.Id.Landblock)
            {
                log.Warn($"[DYNDUNGEON] {run} point {creature.Location.ToLOCString()} is not on landblock 0x{landblock.Id.Landblock:X4}; dropped");
                run.RecordPlacement(DungeonRunTelemetry.Phases.Place, DungeonRunTelemetry.Reasons.OffLandblock, entry.Wcid, entry.Role);
                creature.Destroy();
                return PlaceOutcome.Failed;
            }

            // ---- run ownership. BOTH keys, stamped BEFORE EnterWorld. ----
            // Creature.Die() requires the in-memory back-reference AND the persisted stamp (Creature_Death.cs:158);
            // with only one of them the kill is never counted and the run can never clear. The stamp also has
            // to precede EnterWorld so no save window sees a run creature as an ordinary persistable dynamic
            // (WorldObject_Database.cs:178 is what excludes it).
            creature.SetProperty(PropertyInt.ThreadDungeonRunId, (int)run.RunId);
            creature.P_DungeonRun = run;
            creature.DungeonRole = entry.Role;

            // Mandatory: a run creature's corpse must never outlive the copy.
            creature.TimeToRot = -1;

            // ---- what a run creature is ALLOWED to do (owner ruling, 2026-09-06) ----
            // Every creature in a Thread behaves the same way, whatever weenie the roster drew.

            // 1. It always leaves a corpse. Creature_Death.CreateCorpse skips the corpse entirely when
            // NoCorpse is set and scatters the generated loot on the ground instead (Creature_Death.cs:559-575),
            // which is what the Mukkir Predator (wcid 52780) and 45 other roster-reachable weenies do. A run's
            // whole reward is in that corpse, so the flag is cleared here rather than being fixed weenie by
            // weenie. Safe per instance: PropertiesBool is DEEP-CLONED into a new object's biota
            // (WeenieConverter.cs:77-83, else branch), so this touches only this creature. The bool setter
            // calls RemoveProperty for false, which is exactly right.
            creature.NoCorpse = false;

            // 2. It has no emotes at all. A retail weenie's Death emote can Generate objects, Activate linked
            // objects or drive world-event machinery (StartEvent/StopEvent/LocalSignal) - that is the portal a
            // player watched a dying monster summon inside a private copy - and none of it makes sense in an
            // ephemeral instance nothing else can reach. Flavour text is the only thing lost.
            //
            // ASSIGN a fresh collection; do NOT Clear() or Remove() from the existing one. PropertiesEmote is
            // one of the FIVE collections WorldObject shares BY REFERENCE with the cached weenie
            // (WorldObject.cs passes referenceWeenieCollectionsForCommonProperties: true, and
            // ACE.Entity/Adapter/WeenieConverter.cs then does a bare
            // `result.PropertiesEmote = weenie.PropertiesEmote`).
            // Mutating it in place would strip emotes from the CACHED WEENIE and so from every instance of
            // that creature server-wide, retail landblocks included, until the world cache was invalidated.
            // The other four shared collections are PropertiesCreateList, PropertiesEventFilter,
            // PropertiesGenerator and PropertiesBodyPart; the same rule applies to any of them.
            // PropertiesBodyPart is shared from a SECOND, separate branch of that converter rather than from
            // the block the other four sit in, which is why it is easy to miss - see ApplyBodyPartUplift,
            // which has to clone it for exactly this reason.
            creature.Biota.PropertiesEmote = new System.Collections.ObjectModel.Collection<PropertiesEmote>();

            // 2b. It is never a generator (owner ruling, 2026-10-05: "All mobs should have their generators
            // stripped" - trash, elites, champions and bosses, no exceptions, no tunable). Normalization only
            // reaches the creature itself: a boss stamped to level 83 still spawned five level-200 children
            // from its own weenie generator (run 0x80003B73, wcid 36031), un-normalized, and they killed the
            // player. Same reference-sharing rule as item 2 - see StripGenerator.
            var generatorProfilesStripped = StripGenerator(creature);

            if (generatorProfilesStripped > 0)
                log.Debug($"[DYNDUNGEON] {run} wcid {entry.Wcid} ({entry.Role}) generator stripped ({generatorProfilesStripped} profiles)");

            // 2c. Nor does it spawn anything on death. DeathSpawnWcid is the other route by which a creature
            // puts un-normalized children into the run (Creature_Death.cs:112); see StripDeathSpawn.
            if (StripDeathSpawn(creature))
                log.Debug($"[DYNDUNGEON] {run} wcid {entry.Wcid} ({entry.Role}) death spawn stripped");

            // 3. It drops ONLY what the run's own treasure profile rolls (owner ruling, 2026-09-06: "any
            // original custom loot, quest item drops, or emotes should be removed ... standardize stripping
            // them down to the standard loot tier and # of items dropped rolls").
            //
            // Creature_Death.GenerateTreasure has exactly three sources (Creature_Death.cs:777-840):
            //   1. DeathTreasure, the LootGenerationFactory roll - KEPT, and it is the profile set below.
            //   2. Inventory + EquippedObjects filtered on DestinationType (Creature_Death.cs:798-800).
            //   3. Biota.PropertiesCreateList rows marked Contain, or Treasure-without-Wield
            //      (Creature_Death.cs:819-822), instantiated fresh at death.
            // Sources 2 and 3 are the retail weenie's own drops - the quest items a player found piggybacking
            // on a dungeon mob - and both are removed here. Measured on 2026-09-06 against ace_world: of the
            // 1544 roster wcids in Content/events/axes/species, 1125 carry at least one row source 3 would
            // fire, and 88 of those point at an item with a non-empty PropertyString.Quest.
            StripCreateList(creature.Biota);

            // Source 2. The lever is DestinationType, a bare ephemeral FIELD on WorldObject
            // (WorldObject.cs:1104) whose only behavioural read is the corpse-transfer filter at
            // Creature_Death.cs:800; it is not a biota property, so it is neither persisted nor sent to the
            // client, and clearing it cannot change how the creature fights with the item while alive.
            // BondedStatus.Destroy (Creature_Death.cs:803) is the other documented escape and was rejected:
            // PropertyInt.Bonded is [AssessmentProperty] (PropertyInt.cs:56-57), so it is sent to the client
            // on appraisal and would relabel every run monster's visible weapon.
            //
            // Sweeping BOTH collections rather than only the create list is what covers
            // GenerateInventoryTreasure, which builds items from a treasure table and stamps them
            // DestinationType.Treasure itself (Creature_Equipment.cs:841-849) - those never touch
            // PropertiesCreateList. (GenerateWieldedTreasure items arrive with DestinationType left at Undef,
            // per CreateWieldedTreasure at WorldObject_Equipment.cs:158-183, so they never dropped anyway.)
            // Every item exists by now: GenerateWieldList/GenerateWieldedTreasure/EquipInventoryItems/
            // GenerateInventoryTreasure all run from Creature.SetEphemeralValues (Creature.cs:195-211),
            // i.e. inside the WorldObjectFactory.CreateNewWorldObject call above, and a monster acquires
            // nothing afterwards.
            StripCorpseTransfer(creature);

            // 4. It fights the PLAYER and nothing else. Retail carries three separate reasons for one monster
            // to attack another - a Tolerance that excludes players, faction bits, and FoeType - and the
            // roster is drawn from retail content, so run creatures inherit all three. Shared with World
            // Events, which draws the same roster; see SpawnedCreatureHostility for the measured counts and
            // for why this is per-instance safe. Threads normalizes EVERY creature it places, with no kind
            // gate: DungeonPopulationBuilder.IsKillable above has already refused anything that is not an
            // attackable, non-NPC monster, so there is no objective prop or friendly to exempt.
            SpawnedCreatureHostility.MakeHostileToPlayers(creature);

            // 4a. Reach-up stamp scaling (owner ruling 2026-10-08). A non-boss entry the builder STAMPED at its
            // projected level has its attributes, effective attack and defense skills, melee damage and body armour
            // carried up the stat curve from its authored level to the stamp - see ReachUpStamp. BEFORE 4b, so the
            // defense ceiling below measures the SCALED skill, and before the health block, which therefore has to
            // read the AUTHORED max captured here (healthMaxBeforeStamp) exactly as it does for the uplift's
            // attribute raise. A stamp with the stat curve off (plan.StatCurve null) changes the level only - that
            // is the "flat" fallback dynamic_dungeons_stat_curve = false documents. The level itself is written with
            // the boss level stamp further down, so ownLevel below still reads the AUTHORED level.
            uint? healthMaxBeforeStamp = null;
            var stampCurve = StampCurveFor(entry, plan);

            if (stampCurve != null)
            {
                var authoredLevel = creature.Level ?? 0;
                var preStampMax = creature.Health.MaxValue;
                var stampHealthTarget = NonBossHealthTarget(preStampMax, plan.HealthNormalizeRatio, plan.HealthMultiplier, plan.TrashHealthFloor);
                var stamped = ReachUpStamp.Apply(creature, authoredLevel, entry.StampLevel, stampCurve, stampHealthTarget);

                healthMaxBeforeStamp = HealthMaxBeforeStamp(stamped, preStampMax);

                log.Debug($"[DYNDUNGEON] {run} wcid {entry.Wcid} reach-up stamped {authoredLevel} -> {entry.StampLevel}: " +
                          $"{stamped.AttributesScaled} attribute(s), {stamped.SkillsRetargeted} skill(s), {stamped.PartsScaled} body part(s), {stamped.WeaponsScaled} weapon(s) scaled" +
                          (stamped.AttributesSkipped ? $"; attributes left authored (health target {stampHealthTarget} unreachable with the scaled Endurance)" : ""));
            }

            // 4b. Bypass and immunity traits, and the category-immunity guard (owner ruling 2026-09-13: no run
            // creature may be immune to a whole attack category). BEFORE the gem's own shield_hollow and hollow
            // writes further down, so a gem that grants those still delivers them as advertised - the strip
            // removes what the WEENIE authored, never what the gem adds. Every consumer and every roster count is
            // documented on DungeonCombatNormalizer.
            //
            // The #1173 defense ceiling reads the curve's effective defense standard at the creature's OWN level
            // (2026-10-08): its stamp for a stamped entry, so a creature stamped to 420 is capped against the
            // level-420 standard plus the offset, not the run level's. Everyone else reads plan.BandStandard,
            // which above the pivot is already the curve at the run level.
            if (plan.StripCombatTraits)
            {
                var capStandard = DefenseCapStandard(entry, plan);
                var stripped = DungeonCreatureNormalizer.StripCombatTraits(creature, plan, entry.Role == DungeonRole.Boss, capStandard);

                if (stripped.Count > 0)
                    log.Debug($"[DYNDUNGEON] {run} wcid {entry.Wcid} ({entry.Role}) combat traits normalized: {string.Join(", ", stripped)}");
            }

            // Band-standard spell-tier uplift. Only for an entry the adaptive band reached below its natural
            // low edge (entry.UpliftLevel > 0), same gate as the level/skills/melee/armour axes below - and
            // MUST run before step 5's AoE/drain strip, never after: a raised id can land on a banned id, or
            // change NumProjectiles, and the strip has to see the FINAL book to make that call correctly.
            // Safe to mutate in place for the exact same reason step 5 documents immediately below:
            // PropertiesSpellBook is a fresh Dictionary copy out of WeenieConverter.ConvertToBiota
            // (WeenieConverter.cs:46-47), never one of the collections shared by reference with the cached
            // weenie.
            //
            // A NORMALIZED boss takes the TWO-WAY form instead (DungeonSpellTier.SetTier): a boss authored with
            // tier-8 spells over a tier-5 band comes down to tier 5. Same position, same reason - before the strip.
            // Skipped when the offense ratio is 0, the documented "leave offense authored".
            var spellsRaised = 0;
            var normalizingBoss = entry.Role == DungeonRole.Boss && plan.BossNormalize;

            if (normalizingBoss && plan.BossOffenseBandRatio > 0 && plan.BandStandard != null && !plan.BandStandard.IsEmpty && plan.BandStandard.SpellTier > 0)
                spellsRaised = DungeonSpellTier.SetTier(creature.Biota.PropertiesSpellBook, plan.BandStandard.SpellTier);
            else if (entry.UpliftLevel > 0 && plan.BandStandard != null && !plan.BandStandard.IsEmpty && plan.BandStandard.SpellTier > 0)
                spellsRaised = DungeonSpellTier.Raise(creature.Biota.PropertiesSpellBook, plan.BandStandard.SpellTier);

            // Boss attributes (code review 2026-09-13). A skill's Current is AttributeFormula + InitLevel + Ranks
            // (CreatureSkill.cs:170-179), so setting InitLevel alone (NormalizeBoss, further down) left a boss
            // authored with 500-600 attributes far above its band. The six attributes are SET to the band medians
            // here, and it has to be HERE, before the health block: Endurance feeds Health.MaxValue through the
            // vital formula (CreatureVital.cs:143-147) and ApplyHealth measures its delta against MaxValue
            // (WorldEventSpawner.cs:2007-2021), so the boss's health target must be applied on the FINAL attributes
            // or the achieved health drifts by the Endurance change. ApplyUplift writes no attribute, so nothing
            // later re-raises them.
            if (normalizingBoss && plan.BandStandard != null && !plan.BandStandard.IsEmpty)
            {
                var attributesSet = DungeonCreatureNormalizer.NormalizeBossAttributes(creature, plan);

                if (attributesSet > 0)
                    log.Info($"[DYNDUNGEON] {run} boss wcid {entry.Wcid} {attributesSet} attribute(s) set to the band standard (offense x{plan.BossOffenseBandRatio:0.##}, defense x{plan.BossDefenseBandRatio:0.##})");
            }

            // Uplift attributes (dynamic_dungeons_uplift_attributes, owner ruling 2026-10-06). An uplifted NON-boss
            // creature has had its InitLevels floored to the band median (ApplyUplift) but never its attributes,
            // and a skill's Current is attribute formula + InitLevel + Ranks, so it landed below the band's
            // EFFECTIVE skill. The six attributes are raised UPWARD ONLY to the band medians here, and it has to
            // be HERE, before the health block, for the same reason the boss attributes are: Endurance feeds
            // Health.MaxValue. The health block must therefore measure from the AUTHORED max captured below,
            // never from the post-raise MaxValue, or an uplifted creature's health would drift by the Endurance
            // change. The effective-skill top-up and the defense-ceiling re-check run after ApplyUplift.
            // World Events share BandUplift.Apply and are untouched: this is a separate Threads-only entry.
            var upliftAttributes = UpliftsAttributes(entry.UpliftLevel, entry.Role, plan.BandStandard,
                PropertyManager.GetBool("dynamic_dungeons_uplift_attributes", DungeonPopulationLimits.DefaultUpliftAttributes).Item);
            uint? healthMaxBeforeUplift = null;
            var attributesRaised = 0;

            if (upliftAttributes)
            {
                // A health target at or below the RAISED Endurance's own health contribution cannot be reached:
                // ApplyHealth would clamp StartingValue to 1 and max health would land above the target. That is
                // only reachable with trash_health_floor_ratio 0 or no band median health, but when it happens
                // the whole attribute uplift (and the top-up that depends on it) is skipped for this creature.
                var preRaiseMax = creature.Health.MaxValue;
                var wouldTarget = NonBossHealthTarget(preRaiseMax, plan.HealthNormalizeRatio, plan.HealthMultiplier, plan.TrashHealthFloor);
                var currentEndurance = creature.Attributes.TryGetValue(PropertyAttribute.Endurance, out var enduranceAttribute) && enduranceAttribute != null
                    ? enduranceAttribute.Base : 0u;

                if (!UpliftAttributeRaiseKeepsHealthReachable(wouldTarget, BandUplift.RaisedEndurance(currentEndurance, plan.BandStandard.AttributeMedianFor(PropertyAttribute.Endurance))))
                {
                    upliftAttributes = false;
                    log.Debug($"[DYNDUNGEON] {run} wcid {entry.Wcid} attribute uplift skipped: health target {wouldTarget} is not above the raised Endurance's health term");
                }
                else
                {
                    healthMaxBeforeUplift = preRaiseMax;
                    attributesRaised = BandUplift.RaiseAttributes(creature, plan.BandStandard);
                }
            }

            // 5. Room-blanketing and named-drain spells are removed from the spell book (owner ruling
            // 2026-09-08). See DungeonSpellFilter for the exact rule (NumProjectiles > 1, or a banned id)
            // and its safety guard (never empty a book on a creature with no damaging body part). This is
            // safe to mutate in place: PropertiesSpellBook is ALWAYS a fresh Dictionary copy out of
            // WeenieConverter.ConvertToBiota (WeenieConverter.cs:46-47) - unlike PropertiesCreateList,
            // PropertiesEmote, PropertiesEventFilter, PropertiesGenerator and PropertiesBodyPart above, it is
            // never one of the collections shared by reference with the cached weenie, so nothing here can
            // reach a retail landblock's copy of this weenie. The spell-tier uplift immediately above already
            // ran, so this strip sees the FINAL book - a raised id that lands on a banned id or gains
            // projectiles is still caught here.
            if (PropertyManager.GetBool("dynamic_dungeons_strip_aoe_spells", DungeonSpellFilter.DefaultStripAoeSpellsEnabled).Item)
            {
                var book = creature.Biota.PropertiesSpellBook;

                if (book != null && book.Count > 0)
                {
                    var bannedIds = DungeonSpellFilter.ParseBannedIds(
                        PropertyManager.GetString("dynamic_dungeons_banned_spell_ids", DungeonSpellFilter.DefaultBannedSpellIds).Item);

                    var originalCount = book.Count;
                    var flaggedCount = book.Keys.Count(id => bannedIds.Contains(id) || SpellProjectileCountOf(id) > DungeonSpellFilter.MaxAllowedProjectiles);
                    var hasDamagingBodyPart = HasDamagingBodyPart(creature.Biota);

                    var removed = DungeonSpellFilter.Strip(book, bannedIds, SpellProjectileCountOf, hasDamagingBodyPart);

                    if (removed.Count > 0)
                    {
                        log.Debug($"[DYNDUNGEON] {run} wcid {entry.Wcid} stripped {removed.Count} spell(s) from spell book: {string.Join(", ", removed)}");
                    }
                    else if (flaggedCount > 0 && flaggedCount == originalCount)
                    {
                        // The safety guard refused: every spell in the book was flagged and the creature has
                        // no damaging body part to fall back on. Left exactly as authored.
                        log.Warn($"[DYNDUNGEON] {run} wcid {entry.Wcid} spell book would be emptied by the AoE/drain filter and has no damaging body part; left untouched");
                    }
                }
            }

            // 6. Release an immobile creature that has no ranged attack left, so it can walk to the player
            // instead of turning in place forever (reported symptom 2026-09-15, wcid 51551 "Elite Disciple of
            // Misery"). Must run AFTER step 5's spell strip, not before: the strip can empty a book that
            // started non-empty, and the rule needs to see the FINAL book. See
            // SpawnedCreatureHostility.ShouldReleaseImmobile for the full reasoning.
            if (SpawnedCreatureHostility.ReleaseImmobileWithoutRangedAttack(creature))
                log.Debug($"[DYNDUNGEON] {run} wcid {entry.Wcid} AiImmobile cleared: no ranged attack");

            var isBoss = entry.Role == DungeonRole.Boss;

            // The drawn creature's own retail reward anchors, read BEFORE this method stamps anything on it.
            // Ruling R20 (price off the GEM's level) was REVERSED by the owner on 2026-09-06: a run kill is
            // now worth a fixed multiple of what killing THAT creature outside the dungeon is worth, so a
            // higher-level monster pays more and the baseline is exactly 1.0x retail. See
            // DungeonPopulationBuilder.BaseXp for the resolution rule and why the boss is a special case.
            //
            // Order matters. creature.XpOverride is the WEENIE's value at this point; the rewards block below
            // overwrites it with the run's own figure, and reading it after that would fold RoleRate, the
            // scale and the modifier product back in a second time.
            var ownXpOverride = creature.XpOverride;
            var ownLevel = creature.Level ?? 0;

            // Health goes through CreatureVital.StartingValue, never MaxValue (a computed getter with no
            // setter) - WorldEventSpawner.ApplyHealth is the shared implementation.
            var hpMult = isBoss ? plan.BossHealthMultiplier : plan.HealthMultiplier;

            if (isBoss)
            {
                // Invariant I2 (owner ruling 2026-09-06: "Boss should always be higher level and difficulty
                // compared to standard monsters"). The floor is applied HERE rather than in the plan because
                // it is measured against what the pack ACTUALLY entered the world with; see BossHealthTarget
                // for why the three terms are a max and not a product.
                //
                // THE BOSS RIDES THE SAME NORMALIZATION RATIO AS ITS PACK, and that is not cosmetic symmetry.
                // The boss floor is 4x the OBSERVED pack maximum and it only ever RAISES: if the trash drops
                // 3-5x onto the curve while the boss keeps its raw authored health, term 1 of BossHealthTarget
                // (the boss's own base) wins outright and the boss silently becomes roughly 7x its pack instead
                // of the designed 4x. Normalizing its base by the same ratio keeps the designed relationship at
                // every level. BossHealthTarget itself is untouched - it is handed an already-normalized base.
                //
                // BOSS NORMALIZATION (dynamic_dungeons_boss_normalize, 2026-09-13) replaces all of the above with
                // one plan-time base that owes NOTHING to the boss weenie's authored health:
                // bossBase = max(R x curve, M x the largest non-boss base in the family's pools), then x the
                // boss health multiplier. The authored health is still read for the log line only. The runtime
                // observedMaxNonBoss + 1 term inside NormalizedBossHealthTarget is a guard, not a design term: it
                // covers the one-hit-point odd-endurance rounding gap between the plan's health read and
                // CreatureVital.MaxValue, and any live-vs-plan divergence.
                var authoredBaseMax = creature.Health.MaxValue;
                var normalizedBoss = plan.BossNormalize && plan.BossHealthBase > 0;
                var baseMax = NormalizedBase(authoredBaseMax, plan.HealthNormalizeRatio);
                var target = normalizedBoss
                    ? DungeonRewardMath.NormalizedBossHealthTarget(plan.BossHealthBase, hpMult, observedMaxNonBoss)
                    : DungeonRewardMath.BossHealthTarget(baseMax, hpMult, observedMaxNonBoss, LegacyBossFloorRatio(plan, run.Group.Effort));
                var clamped = WorldEventSpawner.ApplyHealth(creature, target);
                var achieved = creature.Health.MaxValue;
                var ratio = observedMaxNonBoss > 0 ? (double)achieved / observedMaxNonBoss : 0.0;

                if (normalizedBoss)
                    log.Info($"[DYNDUNGEON] {run} boss wcid {entry.Wcid} level {plan.BossLevel} (authored {creature.Level ?? 0}) " +
                             $"hp authored {authoredBaseMax} ignored; base max(band {plan.BossHealthBandTerm:0}, pack {plan.BossHealthPackTerm:0}) = {plan.BossHealthBase:0} x{hpMult:0.##} -> target {target} achieved {achieved} (packMax {observedMaxNonBoss}, ratio {ratio:0.00}) " +
                             $"dr {plan.BossDamageRating} drr {plan.BossDamageResistRating}");
                else
                    log.Info($"[DYNDUNGEON] {run} boss wcid {entry.Wcid} level {plan.BossLevel} (authored {creature.Level ?? 0}) " +
                             $"hp {authoredBaseMax} norm x{plan.HealthNormalizeRatio:0.###} -> {baseMax} -> target {target} achieved {achieved} (packMax {observedMaxNonBoss}, ratio {ratio:0.00} vs floor {plan.BossHealthFloorRatio:0.00}) " +
                             $"dr {plan.BossDamageRating} drr {plan.BossDamageResistRating}");

                // Telemetry: a DIFFICULTY-TUNING signal, not a placement one. Both warnings below sit inside
                // this isBoss branch and so run BEFORE EnterWorld - a boss that lands under its health target
                // has still placed perfectly well - which is why this goes on the run row and must never be
                // written into the placement ledger.
                run.RecordBossHealth(ratio, clamped);

                if (clamped)
                    log.Warn($"[DYNDUNGEON] {run} boss wcid {entry.Wcid} health target {target} unreachable; StartingValue clamped to 1 (MaxValue={achieved})");

                // Not the same condition as `clamped`: a target can be reached exactly and still sit under
                // the floor if the boss's own base health already exceeded it, which is fine, or if rounding
                // shaved it, which is worth seeing. Only complain when there was a pack to measure against.
                //
                // Legacy path only: a normalized boss is not measured against BossHealthFloorRatio at all (its
                // base is the plan's band/pack max), so the warning would misfire on every such run. Its own
                // hard invariant is achieved > packMax, guaranteed by NormalizedBossHealthTarget's +1 guard.
                if (normalizedBoss)
                {
                    if (observedMaxNonBoss > 0 && achieved <= observedMaxNonBoss)
                        log.Warn($"[DYNDUNGEON] {run} boss wcid {entry.Wcid} achieved health {achieved} does not exceed packMax {observedMaxNonBoss}");
                }
                else if (observedMaxNonBoss > 0 && plan.BossHealthFloorRatio > 0 && ratio < plan.BossHealthFloorRatio - 1e-9)
                    log.Warn($"[DYNDUNGEON] {run} boss wcid {entry.Wcid} achieved health ratio {ratio:0.00} is below the floor {plan.BossHealthFloorRatio:0.00} (achieved {achieved}, packMax {observedMaxNonBoss})");
            }
            else
            {
                // The non-boss health floor (owner ruling 2026-09-07): scale UP to the band's cross-family
                // median health BEFORE the gem's own health multiplier is applied, never after. This is the
                // OPPOSITE order from the boss floor above, which multiplies first and floors the RESULT
                // against the pack's observed maximum - the two measure different things (a band standard
                // here, an observed pack maximum there) and unifying their order would break one or the
                // other. Applies to every non-boss entry, elites included: an elite draws from the same
                // tables and the same under-floor members a trash slot would, so exempting it would leave the
                // bug this floor exists for in place for the 1.5x-XP slot.
                //
                // Ahead of the floor sits the band-standard NORMALIZATION (2026-09-12): the authored health is
                // first scaled by plan.HealthNormalizeRatio, so the pack lands on the smooth ladder while the
                // authored spread WITHIN the pack survives. A ratio of 0 (switch off, or no usable band sample)
                // or 1 is a no-op and leaves the floor-then-multiply arithmetic exactly as it was.
                // The base is the AUTHORED max, read before the uplift's attribute raise (healthMaxBeforeUplift),
                // so the target below is exactly what it would be without the raise; the skip check further down
                // compares against the LIVE max for the same reason.
                // healthMaxBeforeStamp is the same capture for a reach-up stamp's attribute scaling (step 4a); the
                // two are exclusive (an entry is uplifted or stamped, never both).
                var authoredMax = AuthoredHealthMax(healthMaxBeforeUplift, healthMaxBeforeStamp, creature.Health.MaxValue);
                var normalized = NormalizedBase(authoredMax, plan.HealthNormalizeRatio);

                // One line covering both terms, so a NORMALIZED creature is as visible in the log as a FLOORED
                // one - without it, a run in which every creature moved would log nothing at all.
                if (normalized != authoredMax || plan.TrashHealthFloor > normalized)
                    log.Debug($"[DYNDUNGEON] {run} wcid {entry.Wcid} hp {authoredMax} normalized x{plan.HealthNormalizeRatio:0.###} to {normalized}" +
                             (plan.TrashHealthFloor > normalized ? $" then floored to {plan.TrashHealthFloor} (band standard floor)" : ""));

                // NonBossHealthWrite compares the target against the LIVE max, not authoredMax: after an Endurance
                // raise the two differ, and skipping on the authored value would leave the creature at its raised
                // (wrong) max instead of the target.
                var healthWrite = NonBossHealthWrite(authoredMax, creature.Health.MaxValue, plan.HealthNormalizeRatio, hpMult, plan.TrashHealthFloor);

                if (healthWrite.HasValue
                    && WorldEventSpawner.ApplyHealth(creature, healthWrite.Value))
                    log.Warn($"[DYNDUNGEON] {run} wcid {entry.Wcid} health target {healthWrite.Value} unreachable; StartingValue clamped to 1 (MaxValue={creature.Health.MaxValue})");
            }

            // Invariant I1. Stamped only on the boss, and only ever UPWARD (the plan's four floors include
            // the boss weenie's own authored level). This is also what makes the boss read as a boss in the
            // client's own examine panel, which is the only place a player can see it before the fight.
            //
            // One side effect, deliberate: Creature_Death.cs:724-733 sets CanGenerateRare unconditionally at
            // Level >= 100. Every shipped Raw Fragment rung is 185 or above and its pack is already past
            // that, so this changes nothing there; the only new case is an admin-issued sub-100 gem, whose
            // boss now qualifies for a rare where before it needed to outlevel its killer.
            if (isBoss && plan.BossLevel > 0)
                creature.Level = plan.BossLevel;

            // The reach-up level stamp (2026-10-08): a non-boss entry the builder stamped is PLACED at its projected
            // level, with or without the stat curve - the examine panel, luminance gating and every level-gated rule
            // see the level the run fielded it at. Upward-only, like every other level write here. The boss is
            // excluded because its own stamp above always outranks the pack (packLevel reads the projected level).
            if (!isBoss && entry.StampLevel > (creature.Level ?? 0))
                creature.Level = entry.StampLevel;

            // Ratings are ADDITIVE on top of whatever the weenie already carries - except on a NORMALIZED boss,
            // whose authored damage, damage-resist and four crit ratings are removed first, so the plan's boss
            // floors and the gem's own rating modifiers are the whole of it (spec B: a hand-authored rating 60
            // boss must not stack its 60 on top of the band's figure).
            if (isBoss && plan.BossNormalize)
            {
                var zeroed = DungeonCreatureNormalizer.ZeroBossRatings(creature);

                if (zeroed > 0)
                    log.Info($"[DYNDUNGEON] {run} boss wcid {entry.Wcid} authored rating(s) zeroed: {zeroed}");
            }

            var dr = StampedDamageRating(plan, isBoss);
            var drr = isBoss ? plan.BossDamageResistRating : plan.DamageResistRating;

            if (dr != 0) creature.DamageRating = (creature.DamageRating ?? 0) + dr;
            if (plan.CritRating != 0) creature.CritRating = (creature.CritRating ?? 0) + plan.CritRating;
            if (plan.CritDamageRating != 0) creature.CritDamageRating = (creature.CritDamageRating ?? 0) + plan.CritDamageRating;
            if (drr != 0) creature.DamageResistRating = (creature.DamageResistRating ?? 0) + drr;


            // Shield-ignoring (press v2's shield_hollow). WorldObject_Weapon.GetIgnoreShieldMod reads the
            // ATTACKER's own PropertyFloat.IgnoreShield and Math.Max's it against the wielded weapon's
            // (WorldObject_Weapon.cs:881-884), so a monster with no weapon still benefits; it is consumed as
            // attacker.GetIgnoreShieldMod(weapon) from inside the DEFENDER's Creature_Combat.GetShieldMod,
            // which is the monster-attacks-player direction. Math.Max against whatever the weenie already
            // carries, for the same reason the value itself is a max: the axis is a single best share, not a
            // sum, so a gem must never LOWER a creature that was authored with more.
            if (plan.IgnoreShield > 0)
                creature.IgnoreShield = Math.Max(creature.IgnoreShield ?? 0.0, plan.IgnoreShield);

            // Hollow (press v2). The flags trigger the effect; PropertyFloat.HollowIntensity carries the
            // rolled fraction (owner ruling 2026-09-17) that every combat-math site now scales by - see
            // ACE.Server.Entity.HollowMath and DungeonRewardMath.Hollow. ResolveSpawnedHollowIntensity never
            // lowers an authored hollow (an author-set flag with no roll, or one the trait strip left alone).
            if (plan.HollowIntensity > 0)
            {
                var alreadyHollow = creature.IgnoreMagicArmor == true || creature.IgnoreMagicResist == true;

                creature.IgnoreMagicArmor = true;
                creature.IgnoreMagicResist = true;
                creature.HollowIntensity = ResolveSpawnedHollowIntensity(plan.HollowIntensity, alreadyHollow, creature.HollowIntensity);
            }

            // ---- band-standard uplift ----
            // ONLY for an entry the adaptive band reached below its natural low edge (I-D). Every axis below
            // is a FLOOR or a ratio applied only when it is greater than 1, so nothing here can ever lower a
            // value (I-C), and an entry with UpliftLevel 0 - which is every entry of every run while
            // dynamic_dungeons_band_low_floor is 1.0 - does not enter this branch at all (I-A, I-E).
            //
            // Placed AFTER the boss level stamp deliberately, so the two compose in one direction only: a
            // promoted boss that was itself uplifted takes max(its stamped boss level, the uplift level), and
            // since plan.BossLevel already floors at gemLevel + 1 the boss stamp always wins (I-G).
            //
            // ownLevel, not creature.Level: the boss level stamp above has already overwritten the latter for
            // a promoted boss, and ApplyUplift reports the value as the creature's AUTHORED level. It is a
            // diagnostic-only argument - the level floor inside still reads the creature's current level, so
            // it stays upward-only.
            if (entry.UpliftLevel > 0)
                ApplyUplift(run, creature, entry, plan, ownLevel, spellsRaised, attributesRaised, upliftAttributes);

            // Run speed: Creature.GetRunRate reads GetCreatureSkill(Skill.Run).Current
            // (Monster_Navigation.cs:377-378), and InitLevel is the persisted base that Current is built from.
            //
            // AFTER ApplyUplift, deliberately (owner ruling 2026-10-06, a pre-existing ordering bug): the uplift's
            // InitLevel floor includes Run, so with this multiplier BEFORE it the floor overwrote part of the gem's
            // run-speed modifier. Multiplying the FINAL InitLevel means the modifier always applies in full. Nothing
            // between here and the uplift reads or writes Run (the boss normalization below sets attack and defense
            // skills only).
            if (Math.Abs(plan.RunSpeedMult - 1.0) > 1e-9 && plan.RunSpeedMult > 0)
            {
                // add: false - never create a Run row the creature was not authored with (the default overload would
                // add an Untrained one at InitLevel 0). A Run-less creature never receives the modifier, before or
                // after the reorder; before it, the uplift floor happened to give the added row a value.
                var runSkill = creature.GetCreatureSkill(Skill.Run, false);

                if (runSkill != null)
                    runSkill.InitLevel = (uint)Math.Clamp(Math.Round(runSkill.InitLevel * plan.RunSpeedMult), 0, uint.MaxValue);
            }

            // ---- boss normalization: offense and defense (spec B) ----
            // AFTER the uplift, so the TWO-WAY set below is what stands for a promoted boss that was also
            // uplifted: the uplift only ever raises, and this is the step that may lower. Attack and defense
            // skills, body-part damage and armour, and health regen go to the band standard x the offense or
            // defense ratio. A ratio of 0 is the admin escape hatch that leaves that half authored; the numbers
            // themselves are decided and unit-tested in DungeonCombatNormalizer.
            if (isBoss && plan.BossNormalize && plan.BandStandard != null && !plan.BandStandard.IsEmpty)
            {
                var (skillsSet, partsChanged, regenSet, weaponsChanged) = DungeonCreatureNormalizer.NormalizeBoss(creature, plan);

                log.Info($"[DYNDUNGEON] {run} boss wcid {entry.Wcid} normalized to band standard (n {plan.BandStandard.SampleCount}): " +
                         $"{skillsSet} skill(s) set, {partsChanged} body part(s) changed, {weaponsChanged} weapon/ammo damage value(s) set, spells retiered {spellsRaised}, " +
                         $"regen {(regenSet ? plan.BandStandard.HealthRate.ToString("0.###") : "authored")} (offense x{plan.BossOffenseBandRatio:0.##}, defense x{plan.BossDefenseBandRatio:0.##})");
            }
            else if (isBoss && plan.BossNormalize)
            {
                log.Warn($"[DYNDUNGEON] {run} boss wcid {entry.Wcid} boss normalization on but the band standard is empty; offense and defense left authored");
            }

            // ---- rewards ----
            // The luminance gate reads the level the creature ACTUALLY carries, which for a boss is the
            // stamped plan.BossLevel written a few lines above, and for an uplifted creature is the uplift
            // level the block above stamped - not its authored one in either case. This is the gate, not a
            // second scaling term: the award itself still derives from the single baseXp anchor below, so
            // there is exactly one place a run's XP and luminance are priced.
            //
            // A reach-up STAMP is priced exactly like an uplift (2026-10-08): RewardLevel and BaseXp both take the
            // entry's RaisedLevel - its uplift or its stamp, whichever is set - so a creature placed at 420 pays
            // at least the ladder's level-420 XP, never the lower level it was authored at.
            var rewardLevel = RewardLevel(entry, ownLevel, plan.BossLevel);
            // bossLadderOnly: a normalized boss is priced off the ladder at its stamped BossLevel alone, never
            // its authored XpOverride or level - the fight is the band's, so the reward is too (spec D).
            var baseXp = DungeonPopulationBuilder.BaseXp(ThreadDungeonManager.Store.XpLadder, ownXpOverride, ownLevel, entry.Role, plan.BossLevel,
                entry.RaisedLevel, bossLadderOnly: plan.BossNormalize);

            // Clamped at int.MaxValue and NOT widened, deliberately (2026-10-08 review): XpOverride is a
            // PropertyInt, so the stored value - and the death path that pays it, Creature_Death's
            // (XpOverride ?? 0) * damagePercent - is int by type. XpFor itself returns a long. Computed at the shipped
            // dials (ThreadsRunCeilingTests.Boss_xp_saturates_the_int_clamp_only_above_the_player_maximum, solo,
            // shipped roster): the worst case stacks BOTH multipliers at dynamic_dungeons_xp_mult_cap (3.0 each,
            // since the run and boss multipliers are capped independently, so 9x). A level-410 boss - the highest a
            // player can press today - prices at 112,383,289 plain and 1,011,449,598 worst case. A level-500 boss
            // (stamped 576) prices at 399,940,216 plain and 3,599,461,946 worst case, which SATURATES. The worst case
            // first reaches 2,147,483,647 at run level 463, so only admin-made gems above 462 with both caps maxed
            // hit this clamp. NOT measured: a group run's reward factor, which multiplies on top.
            creature.XpOverride = (int)Math.Clamp(DungeonPopulationBuilder.XpFor(plan, baseXp, entry.Role), 0, int.MaxValue);

            var lum = DungeonPopulationBuilder.LuminanceFor(plan, baseXp, rewardLevel, entry.Role);

            if (lum > 0)
                creature.LuminanceAward = lum;

            creature.DeathTreasureOverride = plan.Profile;

            // Salvage affinity. A resolved list, not a roll: the per-kill roll is taken at the death path off
            // ThreadSafeRandom, which is what keeps the gem's seeded rng - and so the whole population plan -
            // bit-identical to a gem carrying no affinity modifier.
            if (plan.SalvageAffinities != null && plan.SalvageAffinities.Count > 0)
                creature.P_DungeonSalvageAffinities = plan.SalvageAffinities;

            // Ruling R21: an elite is the only run creature a player can tell apart at a glance, so it says
            // so in its name. Written before EnterWorld like everything else - the create packet the client
            // first sees has to carry it, or the monster is renamed under the player's cursor.
            //
            // The boss carries "Dread " for the same reason and by the same mechanism (owner ruling
            // 2026-09-06). It matters more here than for an elite, because a promoted boss is drawn from the
            // family's own roster and would otherwise be indistinguishable by name from the pack it leads.
            //
            // The uplift prefix goes on FIRST so the role word stays leftmost ("Elite Threadbound Drudge"):
            // the role is what a player scans a room for, while "Threadbound" qualifies which drudge this is.
            // It is written here rather than inside ApplyUplift for the same pre-EnterWorld reason as the
            // role prefixes themselves - the create packet the client first sees has to carry the final name.
            if (entry.UpliftLevel > 0 && plan.UpliftRename)
                creature.Name = DungeonPopulationLimits.UpliftNamePrefix + creature.Name;

            if (entry.Role == DungeonRole.Elite)
                creature.Name = "Elite " + creature.Name;
            else if (isBoss)
                creature.Name = "Dread " + creature.Name;

            try
            {
                if (!creature.EnterWorld())
                {
                    // NO ledger row here. Whether a refusal is terminal (enter_world_refused) or the start of a
                    // recovery (anchor_fallback_used, then possibly fallback_exhausted) is only known to the
                    // caller, TryPlace, which owns the fallback. Destroyed rather than re-positioned: after a
                    // failed AddPhysicsObj this object holds an initialized PhysicsObj with no cell and a cleared
                    // CurrentLandblock (Landblock.cs:1590-1616), and a retry builds a fresh creature instead.
                    log.Warn($"[DYNDUNGEON] {run} EnterWorld refused wcid {entry.Wcid} at {creature.Location.ToLOCString()}");
                    creature.Destroy();
                    return PlaceOutcome.Refused;
                }
            }
            catch (Exception ex)
            {
                log.Error($"[DYNDUNGEON] {run} EnterWorld threw for wcid {entry.Wcid}", ex);
                run.RecordPlacement(DungeonRunTelemetry.Phases.Place, DungeonRunTelemetry.Reasons.EnterWorldThrew, entry.Wcid, entry.Role);
                // Same cleanup the refused path does: a creature that never entered the world is unreachable
                // by anything else, so leaving it undestroyed leaks its guid for the life of the process.
                // Destroy itself is best-effort here - EnterWorld already threw once on this object.
                try { creature.Destroy(); } catch { }
                return PlaceOutcome.Failed;
            }

            // Nothing after EnterWorld. The boss intro emote (EmoteManager.OnGeneration) used to live here.
            // It is removed because emotes are stripped wholesale above: leaving the call in would have been
            // a hole in that strip, since an EmoteCategory.Generation set is as free to Generate a portal as
            // a Death set is, and what it would have played came off the boss's own RETAIL weenie rather
            // than being fork-authored flavour worth preserving. Removing the call rather than making it
            // conditional is what makes "no run creature fires an emote" true by construction.
            //
            // Secondary note, not the justification: a query of ace_world on 2026-09-05 found no
            // EmoteCategory.Generation (9) row on any of the three wcids in
            // Content/dungeons/dynamic/bosses.json (10814, 40927, 41229), so the call was already inert for
            // every shipped boss.
            placedMaxHealth = creature.Health.MaxValue;

            // Telemetry (MarkEntryPlaced) is TryPlace's job now, and only for a placement that went through the
            // fallback - see TryPlace for why a plain success must not touch the recovery rows.
            return PlaceOutcome.Placed;
        }
    }
}
