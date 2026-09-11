using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
                    PropertyManager.GetBool("dynamic_dungeons_uplift_rename", DungeonPopulationLimits.DefaultUpliftRename).Item);

                plan = DungeonPopulationBuilder.Build(run.Spec, run.Dungeon, ThreadDungeonManager.Store,
                    WorldEventManager.Store.SpeciesTables, LiveLevelOf, LiveHealthOf, limits, new Random(run.Spec.Seed), LiveProfileOf,
                    ResolveFitPredicate(run.Dungeon));
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
                     $"hp x{plan.HealthMultiplier:0.##} bossHp x{plan.BossHealthMultiplier:0.##} bossHpFloor x{plan.BossHealthFloorRatio:0.##} trashHpFloor={plan.TrashHealthFloor} xp x{plan.XpMultiplier:0.##} bossXp x{plan.BossXpMultiplier:0.##} lum x{plan.LumMultiplier:0.##} xpScale x{plan.XpScale:0.##} lumScale x{plan.LumScale:0.##} " +
                     $"dr={plan.DamageRating} bossDr={plan.BossDamageRating} cr={plan.CritRating} cdr={plan.CritDamageRating} drr={plan.DamageResistRating} bossDrr={plan.BossDamageResistRating} " +
                     $"tier={plan.Profile.Tier} loot={plan.Profile.ItemMaxAmount}/{plan.Profile.MagicItemMaxAmount}/{plan.Profile.MundaneItemMaxAmount}");

            var batchSize = Math.Max(1, ClampToInt(PropertyManager.GetLong("dynamic_dungeons_spawn_batch_size").Item));
            var queue = new Queue<DungeonSpawnPlanEntry>(plan.Entries);
            var spawned = 0;
            var bossPlaced = false;

            // Invariant I2's right-hand side, accumulated across every batch: the largest Health.MaxValue any
            // non-boss creature in this run actually entered the world with. The boss entry is LAST in
            // plan.Entries (I6), so by the time TryPlace reaches it this is final. Observed rather than
            // predicted, because a creature's real MaxValue folds a dat-driven attribute formula the pure
            // builder cannot evaluate.
            var observedMaxNonBoss = 0u;
            var doorsUnlocked = 0;
            var doorPassDone = false;

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

                try
                {
                    // TECH-DESIGN S5 / ruling R16: every DefaultLocked door in a run copy is unlocked before
                    // the first creature lands, so a private dungeon never gates itself behind a key nothing
                    // drops. In the same delegate chain, and so on the same landblock thread, as the placement.
                    if (!doorPassDone)
                    {
                        doorPassDone = true;

                        try { doorsUnlocked = UnlockDoors(landblock); }
                        catch (Exception ex) { log.Error($"[DYNDUNGEON] {run} door pass threw", ex); }
                    }

                    var n = 0;

                    while (queue.Count > 0 && n < batchSize)
                    {
                        var entry = queue.Dequeue();
                        n++;

                        if (!TryPlace(run, landblock, plan, entry, observedMaxNonBoss, out var placedMaxHealth))
                            continue;

                        spawned++;

                        if (entry.Role == DungeonRole.Boss)
                            bossPlaced = true;
                        else if (placedMaxHealth > observedMaxNonBoss)
                            observedMaxNonBoss = placedMaxHealth;
                    }

                    if (queue.Count > 0)
                    {
                        landblock.EnqueueAction(new ActionEventDelegate(PlaceBatch));
                        finished = false;
                    }
                }
                catch (Exception ex)
                {
                    log.Error($"[DYNDUNGEON] {run} placement batch threw; finishing the populate with {spawned} placed", ex);
                    finished = true;
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
                run.MarkRewardProfile(plan.Profile, plan.LootQuantityMult);

                run.MarkPopulated(plan.Entries.Count, spawned, bossPlaced ? plan.BossWcid : 0u, plan.BossWcid);
                ForgetRun(run.RunId);

                log.Info($"[DYNDUNGEON] {run} populated: {spawned}/{plan.Entries.Count} placed, {doorsUnlocked} door(s) unlocked" +
                         (plan.BossWcid != 0 && !bossPlaced ? $" (boss wcid {plan.BossWcid} FAILED to place)" : ""));

                // A run that placed nothing is Cleared the moment it is populated, and no creature death
                // will ever arrive to announce it. The manager owns the player-facing wording.
                ThreadDungeonManager.OnRunPopulated(run);
            }

            landblock.EnqueueAction(new ActionEventDelegate(PlaceBatch));
        }

        /// <summary>
        /// TECH-DESIGN S5, ruling R16: unlock every locked door in a run's private copy. Runs once, on the
        /// landblock's action queue, before the first creature is placed.
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
        private static int UnlockDoors(Landblock landblock)
        {
            var unlocked = 0;

            foreach (var wo in landblock.GetAllWorldObjectsForDiagnostics())
            {
                if (!(wo is Door door))
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
        /// The non-boss health target: floor-then-multiply (owner ruling 2026-09-07). <paramref name="floor"/>
        /// (plan.TrashHealthFloor) raises <paramref name="authoredMax"/> BEFORE <paramref name="hpMult"/> is
        /// applied - the opposite order from <see cref="DungeonRewardMath.BossHealthTarget"/>, which
        /// multiplies first and floors the result against the observed pack maximum. The two floors measure
        /// different things (a band standard here, a pack maximum there); do not unify their order.
        ///
        /// A FLOOR only: a creature already at or above <paramref name="floor"/> is untouched by this term,
        /// and <paramref name="floor"/> = 0 (the axis disabled, or an empty band pool) makes this identical to
        /// the pre-floor formula.
        ///
        /// Pure and side-effect free on purpose, on the same contract as SanitizeDoubleDial: the log lines
        /// live in the caller so this exact arithmetic - not a restatement of it - is what the unit tests pin.
        /// </summary>
        internal static int NonBossHealthTarget(uint authoredMax, double hpMult, uint floor)
        {
            var baseHealth = Math.Max(authoredMax, floor);
            return (int)Math.Clamp(Math.Round(baseHealth * hpMult), 1, int.MaxValue);
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
        /// </summary>
        private static Func<uint, bool> ResolveFitPredicate(DungeonEntryDef dungeon)
        {
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
        {
            if (weenie == null)
                return 0;

            if (weenie.PropertiesAttribute2nd == null
                || !weenie.PropertiesAttribute2nd.TryGetValue(PropertyAttribute2nd.MaxHealth, out var maxHealth))
                return 0;

            var endurance = 0u;
            if (weenie.PropertiesAttribute != null
                && weenie.PropertiesAttribute.TryGetValue(PropertyAttribute.Endurance, out var enduranceAttr))
                endurance = enduranceAttr.InitLevel;

            return HealthFromAttributes(maxHealth.InitLevel, maxHealth.LevelFromCP, endurance);
        }

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
        {
            if (weenie == null)
                return DungeonStatProfile.Empty;

            var level = weenie.GetProperty(PropertyInt.Level) ?? 0;

            var skills = new Dictionary<Skill, uint>();

            if (weenie.PropertiesSkill != null)
            {
                foreach (var kvp in weenie.PropertiesSkill)
                {
                    if (kvp.Value != null)
                        skills[kvp.Key] = kvp.Value.InitLevel;
                }
            }

            var maxDamage = 0;
            var maxArmor = 0;

            if (weenie.PropertiesBodyPart != null)
            {
                foreach (var kvp in weenie.PropertiesBodyPart)
                {
                    if (kvp.Value == null)
                        continue;

                    if (kvp.Value.DVal > maxDamage) maxDamage = kvp.Value.DVal;
                    if (kvp.Value.BaseArmor > maxArmor) maxArmor = kvp.Value.BaseArmor;
                }
            }

            // Clamped at 0: a negative authored DVal or BaseArmor would be nonsense data, and a negative
            // reaching a uint cast wraps to something enormous rather than failing.
            return new DungeonStatProfile(level, HealthOfWeenie(weenie), skills,
                (uint)Math.Max(0, maxDamage), (uint)Math.Max(0, maxArmor));
        }

        /// <summary>
        /// Pure core of <see cref="LiveHealthOf"/>: CreatureVital.GetMaxValue's formula
        /// (StartingValue + Ranks + attr) applied to a weenie's authored MaxHealth/Endurance rows -
        /// maxHealthInitLevel (StartingValue) plus maxHealthLevelFromCP (Ranks, the CP-invested portion of
        /// health - some role-0 trash weenies carry InitLevel 0 with the entire health value parked here)
        /// plus floor(enduranceInitLevel / 2). Split out from LiveHealthOf so this arithmetic can be unit
        /// tested without a live weenie/DB read.
        /// </summary>
        internal static uint HealthFromAttributes(uint maxHealthInitLevel, uint maxHealthLevelFromCP, uint enduranceInitLevel)
        {
            return maxHealthInitLevel + maxHealthLevelFromCP + enduranceInitLevel / 2;
        }

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
        /// SPELLS are also absent, and that is a known and accepted limitation of this change rather than an
        /// oversight: an uplifted caster throws the spell tier its weenie was authored with, so its melee and
        /// its skills reach the band standard while its nukes do not. Substituting spell tiers is a separate
        /// piece of design.
        /// </summary>
        /// <param name="authoredLevel">
        /// The creature's OWN weenie level, captured by the caller BEFORE anything was stamped on it. It is
        /// passed in rather than read here because by the time this runs the boss level stamp has already
        /// executed, so <c>creature.Level</c> is no longer the authored value for a promoted boss - reading it
        /// here made the log line say "uplifted to level 200 (authored 220)", which reads backwards. Used for
        /// the diagnostic ONLY; the level floor below reads the creature's CURRENT level, because that is what
        /// makes the floor upward-only when the boss stamp has already raised it.
        /// </param>
        private static void ApplyUplift(ThreadDungeonRun run, Creature creature, DungeonSpawnPlanEntry entry, DungeonSpawnPlan plan,
            int authoredLevel)
        {
            if (entry.UpliftLevel > (creature.Level ?? 0))
                creature.Level = entry.UpliftLevel;

            var standard = plan.BandStandard;

            if (standard == null || standard.IsEmpty)
            {
                // The band had nothing to measure - no profile delegate, or no in-band member with usable
                // data at any width. The level floor above still stands (it needs no sample); the rest is
                // skipped rather than guessed at.
                log.Info($"[DYNDUNGEON] {run} {UpliftLogLine(entry.Wcid, entry.UpliftLevel, authoredLevel, 0, 0, null)}");
                return;
            }

            var skillsRaised = 0;

            foreach (var kvp in standard.SkillMedians)
            {
                if (kvp.Value == 0)
                    continue;

                // add: false is load-bearing - see the class comment above.
                var skill = creature.GetCreatureSkill(kvp.Key, false);

                if (skill == null || kvp.Value <= skill.InitLevel)
                    continue;

                skill.InitLevel = kvp.Value;
                skillsRaised++;
            }

            var partsScaled = ApplyBodyPartUplift(creature.Biota, standard.MaxBodyDamage, standard.MaxBaseArmor);

            log.Info($"[DYNDUNGEON] {run} {UpliftLogLine(entry.Wcid, entry.UpliftLevel, authoredLevel, skillsRaised, partsScaled, standard)}");
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
            DungeonBandStandard standard)
        {
            var head = $"wcid {wcid} uplifted to level {upliftLevel} (authored {authoredLevel}); ";

            if (standard == null || standard.IsEmpty)
                return head + "no band standard available, stats untouched";

            return head + $"{skillsRaised} skill(s) raised, {partsScaled} body part(s) scaled toward " +
                   $"dmg {standard.MaxBodyDamage} / armor {standard.MaxBaseArmor}";
        }

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
        {
            var parts = biota?.PropertiesBodyPart;

            if (parts == null || parts.Count == 0)
                return 0;

            var ownMaxDamage = 0;
            var ownMaxArmor = 0;

            foreach (var kvp in parts)
            {
                if (kvp.Value == null)
                    continue;

                if (kvp.Value.DVal > ownMaxDamage) ownMaxDamage = kvp.Value.DVal;
                if (kvp.Value.BaseArmor > ownMaxArmor) ownMaxArmor = kvp.Value.BaseArmor;
            }

            var damageRatio = UpliftRatio(ownMaxDamage, standardMaxDamage);
            var armorRatio = UpliftRatio(ownMaxArmor, standardMaxArmor);

            // Already at or above the standard on both axes: touch nothing, and in particular do NOT clone,
            // so an uplift that changes nothing also allocates nothing.
            if (damageRatio <= 1.0 && armorRatio <= 1.0)
                return 0;

            var fresh = new Dictionary<CombatBodyPart, PropertiesBodyPart>(parts.Count);
            var scaled = 0;

            foreach (var kvp in parts)
            {
                var part = kvp.Value?.Clone();

                if (part != null)
                {
                    part.DVal = ScaleBodyValue(part.DVal, damageRatio);
                    part.BaseArmor = ScaleBodyValue(part.BaseArmor, armorRatio);
                    scaled++;
                }

                fresh[kvp.Key] = part;
            }

            biota.PropertiesBodyPart = fresh;

            return scaled;
        }

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
        {
            if (standardMax == 0 || ownMax <= 0)
                return 1.0;

            var ratio = standardMax / (double)ownMax;

            return ratio > 1.0 ? ratio : 1.0;
        }

        /// <summary>
        /// One body-part value scaled by <paramref name="ratio"/>. The lower clamp is the ORIGINAL value, not
        /// zero: that is what makes invariant I-C ("no uplift axis ever lowers a value") true by construction
        /// rather than by the caller having checked the ratio first, and it costs one compare.
        /// </summary>
        internal static int ScaleBodyValue(int value, double ratio)
        {
            if (value <= 0 || double.IsNaN(ratio) || ratio <= 1.0)
                return value;

            return (int)Math.Clamp(Math.Round(value * ratio), value, int.MaxValue);
        }

        /// <summary>
        /// Creates and places one plan entry. Returns true only when the creature actually entered the world.
        /// Runs on the landblock's action queue.
        /// </summary>
        /// <param name="observedMaxNonBoss">
        /// The largest Health.MaxValue any non-boss creature in this run has entered the world with so far.
        /// Read only when <paramref name="entry"/> is the Boss, where it is invariant I2's right-hand side.
        /// </param>
        /// <param name="placedMaxHealth">
        /// The creature's final Health.MaxValue, for the caller's running maximum. 0 when nothing was placed.
        /// </param>
        private static bool TryPlace(ThreadDungeonRun run, Landblock landblock, DungeonSpawnPlan plan, DungeonSpawnPlanEntry entry,
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
                return false;
            }

            if (!(wo is Creature creature))
            {
                log.Warn($"[DYNDUNGEON] {run} wcid {entry.Wcid} did not resolve to a Creature; skipped");
                run.RecordPlacement(DungeonRunTelemetry.Phases.Place, DungeonRunTelemetry.Reasons.NotCreature, entry.Wcid, entry.Role);
                wo?.Destroy();
                return false;
            }

            // A creature that cannot be killed can never contribute to the run's weighted clear progress
            // (owner ruling R28) - checked before any property write, same as the other refusal paths below.
            if (!DungeonPopulationBuilder.IsKillable(creature.Attackable, creature.PlayerKillerStatus))
            {
                log.Error($"[DYNDUNGEON] {run} wcid {entry.Wcid} ({entry.Role}) is not attackable or is an NPC; not placed");
                run.RecordPlacement(DungeonRunTelemetry.Phases.Place, DungeonRunTelemetry.Reasons.NotKillable, entry.Wcid, entry.Role);
                creature.Destroy();
                return false;
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
                return false;
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

            // 5. Room-blanketing and named-drain spells are removed from the spell book (owner ruling
            // 2026-09-08). See DungeonSpellFilter for the exact rule (NumProjectiles > 1, or a banned id)
            // and its safety guard (never empty a book on a creature with no damaging body part). This is
            // safe to mutate in place: PropertiesSpellBook is ALWAYS a fresh Dictionary copy out of
            // WeenieConverter.ConvertToBiota (WeenieConverter.cs:46-47) - unlike PropertiesCreateList,
            // PropertiesEmote, PropertiesEventFilter, PropertiesGenerator and PropertiesBodyPart above, it is
            // never one of the collections shared by reference with the cached weenie, so nothing here can
            // reach a retail landblock's copy of this weenie.
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
                        log.Info($"[DYNDUNGEON] {run} wcid {entry.Wcid} stripped {removed.Count} spell(s) from spell book: {string.Join(", ", removed)}");
                    }
                    else if (flaggedCount > 0 && flaggedCount == originalCount)
                    {
                        // The safety guard refused: every spell in the book was flagged and the creature has
                        // no damaging body part to fall back on. Left exactly as authored.
                        log.Warn($"[DYNDUNGEON] {run} wcid {entry.Wcid} spell book would be emptied by the AoE/drain filter and has no damaging body part; left untouched");
                    }
                }
            }

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
                var baseMax = creature.Health.MaxValue;
                var target = DungeonRewardMath.BossHealthTarget(baseMax, hpMult, observedMaxNonBoss, plan.BossHealthFloorRatio);
                var clamped = WorldEventSpawner.ApplyHealth(creature, target);
                var achieved = creature.Health.MaxValue;
                var ratio = observedMaxNonBoss > 0 ? (double)achieved / observedMaxNonBoss : 0.0;

                log.Info($"[DYNDUNGEON] {run} boss wcid {entry.Wcid} level {plan.BossLevel} (authored {creature.Level ?? 0}) " +
                         $"hp {baseMax} -> target {target} achieved {achieved} (packMax {observedMaxNonBoss}, ratio {ratio:0.00} vs floor {plan.BossHealthFloorRatio:0.00}) " +
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
                if (observedMaxNonBoss > 0 && plan.BossHealthFloorRatio > 0 && ratio < plan.BossHealthFloorRatio - 1e-9)
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
                if (plan.TrashHealthFloor > creature.Health.MaxValue)
                    log.Info($"[DYNDUNGEON] {run} wcid {entry.Wcid} hp {creature.Health.MaxValue} floored to {plan.TrashHealthFloor} (band median floor)");

                var target = NonBossHealthTarget(creature.Health.MaxValue, hpMult, plan.TrashHealthFloor);

                if (target != creature.Health.MaxValue
                    && WorldEventSpawner.ApplyHealth(creature, target))
                    log.Warn($"[DYNDUNGEON] {run} wcid {entry.Wcid} health target {target} unreachable; StartingValue clamped to 1 (MaxValue={creature.Health.MaxValue})");
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

            // Ratings are ADDITIVE on top of whatever the weenie already carries.
            var dr = isBoss ? plan.BossDamageRating : plan.DamageRating;
            var drr = isBoss ? plan.BossDamageResistRating : plan.DamageResistRating;

            if (dr != 0) creature.DamageRating = (creature.DamageRating ?? 0) + dr;
            if (plan.CritRating != 0) creature.CritRating = (creature.CritRating ?? 0) + plan.CritRating;
            if (plan.CritDamageRating != 0) creature.CritDamageRating = (creature.CritDamageRating ?? 0) + plan.CritDamageRating;
            if (drr != 0) creature.DamageResistRating = (creature.DamageResistRating ?? 0) + drr;

            // Run speed: Creature.GetRunRate reads GetCreatureSkill(Skill.Run).Current
            // (Monster_Navigation.cs:377-378), and InitLevel is the persisted base that Current is built from.
            if (Math.Abs(plan.RunSpeedMult - 1.0) > 1e-9 && plan.RunSpeedMult > 0)
            {
                var runSkill = creature.GetCreatureSkill(Skill.Run);

                if (runSkill != null)
                    runSkill.InitLevel = (uint)Math.Clamp(Math.Round(runSkill.InitLevel * plan.RunSpeedMult), 0, uint.MaxValue);
            }

            // Shield-ignoring (press v2's shield_hollow). WorldObject_Weapon.GetIgnoreShieldMod reads the
            // ATTACKER's own PropertyFloat.IgnoreShield and Math.Max's it against the wielded weapon's
            // (WorldObject_Weapon.cs:881-884), so a monster with no weapon still benefits; it is consumed as
            // attacker.GetIgnoreShieldMod(weapon) from inside the DEFENDER's Creature_Combat.GetShieldMod,
            // which is the monster-attacks-player direction. Math.Max against whatever the weenie already
            // carries, for the same reason the value itself is a max: the axis is a single best share, not a
            // sum, so a gem must never LOWER a creature that was authored with more.
            if (plan.IgnoreShield > 0)
                creature.IgnoreShield = Math.Max(creature.IgnoreShield ?? 0.0, plan.IgnoreShield);

            // Hollow (press v2). Both are booleans with no partial form - see DungeonRewardMath.Hollow - and
            // Monster_Melee.cs:429-430 reads the monster's OWN flags, so writing them here is what makes the
            // effect reach the player.
            if (plan.Hollow)
            {
                creature.IgnoreMagicArmor = true;
                creature.IgnoreMagicResist = true;
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
                ApplyUplift(run, creature, entry, plan, ownLevel);

            // ---- rewards ----
            // The luminance gate reads the level the creature ACTUALLY carries, which for a boss is the
            // stamped plan.BossLevel written a few lines above, and for an uplifted creature is the uplift
            // level the block above stamped - not its authored one in either case. This is the gate, not a
            // second scaling term: the award itself still derives from the single baseXp anchor below, so
            // there is exactly one place a run's XP and luminance are priced.
            var rewardLevel = isBoss && plan.BossLevel > 0 ? plan.BossLevel : Math.Max(ownLevel, entry.UpliftLevel);
            var baseXp = DungeonPopulationBuilder.BaseXp(ThreadDungeonManager.Store.XpLadder, ownXpOverride, ownLevel, entry.Role, plan.BossLevel,
                entry.UpliftLevel);

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
                    log.Warn($"[DYNDUNGEON] {run} EnterWorld refused wcid {entry.Wcid} at {creature.Location.ToLOCString()}");
                    run.RecordPlacement(DungeonRunTelemetry.Phases.Place, DungeonRunTelemetry.Reasons.EnterWorldRefused, entry.Wcid, entry.Role);
                    creature.Destroy();
                    return false;
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
                return false;
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

            // Telemetry: the entry reached the world. This only ever touches RECOVERY ledger rows (today,
            // only anchor_fallback_used, which has no writer yet), never the terminal failure codes above -
            // see ThreadDungeonRun.MarkEntryPlaced for why crediting a terminal row because a SIBLING of the
            // same wcid and role succeeded would be a lie. It is wired now rather than with that branch so
            // the mechanism exists and is covered before anything depends on it.
            run.MarkEntryPlaced(entry.Wcid, entry.Role);

            return true;
        }
    }
}
