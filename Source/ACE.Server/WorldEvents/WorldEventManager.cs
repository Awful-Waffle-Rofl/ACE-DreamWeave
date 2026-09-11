using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;

using ACE.Common;
using ACE.Database;
using ACE.Entity;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldEvents.Objectives;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// The World Events entry point (TECH-DESIGN 2.1). Owns the single running event (hard cap of 1 per
    /// decision C14), the composition axis store, and the family catalog.
    ///
    /// NEVER "EventManager" - that name is taken by the retail game-event system in Managers/EventManager.cs.
    ///
    /// Everything here is inert while the "world_events_enabled" server property is false.
    /// </summary>
    public static class WorldEventManager
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// Guards Current / Store / Catalog against a command handler racing the world tick. Monitor is
        /// reentrant, so TryStart can call TryCompose without deadlocking.
        /// </summary>
        private static readonly object sync = new object();

        private static int runIdCounter;

        private static double nextTickTime;

        /// <summary>Family ids already warned about as catalog-only, reset on every catalog rebuild.</summary>
        private static readonly HashSet<string> warnedSynthesizedFamilies = new HashSet<string>();

        /// <summary>The one running event, or null. Cleared as soon as a run reaches Done.</summary>
        public static WorldEvent Current { get; private set; }

        /// <summary>Immutable axis store. Reload swaps the reference; a running event keeps its own snapshot.</summary>
        public static WorldEventAxisStore Store { get; private set; } = WorldEventAxisStore.Empty;

        /// <summary>Family id -> members, built from a targeted flagged-wcid query plus the species tables (C2, C15).</summary>
        public static WorldEventCatalog Catalog { get; private set; } = WorldEventCatalog.Empty;

        public static bool Enabled => PropertyManager.GetBool("world_events_enabled").Item;

        /// <summary>
        /// Whether a player death at <paramref name="loc"/> should waive vitae, item loss and enchantment
        /// purge (Asheron's Protection): the world_events master switch AND world_event_death_protection are
        /// both on, the current run is Announced OR Active (not yet Resolved), and <paramref name="loc"/> is
        /// in the SAME landblock instance as the event anchor and within the anchor's reward radius - the
        /// same containment rule <see cref="WorldEventAudienceSampler.Sample"/> uses for audience/reward
        /// eligibility, and the same radius field (<c>Composition.Source.RewardRadius</c>) every other "is
        /// this player at the event" check in this system already reads (see
        /// <see cref="WorldEvent.SampleAudience"/> and <see cref="WorldEventRewardDelivery.SpawnCaches"/>).
        ///
        /// Announced is included, not just Active, because Asheron's start line broadcasts at the Announced
        /// transition (WorldEvent.Stage), up to AnnounceLeadSeconds (default 30s) before the run reaches
        /// Active - without this, a death in that window would hear "fight without fear" and then take the
        /// normal penalty anyway.
        ///
        /// Called from the player death path (Player_Death.cs), so this must never throw: any internal
        /// failure - a property read, a null composition, anything - is caught and treated as "not
        /// protected", which is the safe direction (a death simply keeps its normal penalties).
        /// </summary>
        public static bool ProtectsDeathAt(Position loc)
        {
            try
            {
                bool worldEventsEnabled;
                bool deathProtectionEnabled;

                try
                {
                    worldEventsEnabled = Enabled;
                    deathProtectionEnabled = PropertyManager.GetBool("world_event_death_protection").Item;
                }
                catch (Exception ex)
                {
                    log.Warn("[WORLDEVENT] could not read world event death protection properties; treating death protection as off", ex);
                    return false;
                }

                var evt = Current;

                return ProtectsDeathAtCore(worldEventsEnabled, deathProtectionEnabled, evt?.State,
                    loc, evt?.Composition?.AnchorPosition, evt?.Composition?.Source?.RewardRadius ?? 0f);
            }
            catch (Exception ex)
            {
                log.Warn("[WORLDEVENT] ProtectsDeathAt threw; treating death protection as off", ex);
                return false;
            }
        }

        /// <summary>
        /// Pure core of <see cref="ProtectsDeathAt"/> (D6 - no PropertyManager, no Player, no Current): takes
        /// every input as a plain value so a unit test can exercise state gating, radius containment, and
        /// instance mismatch without a live event or a property store.
        /// </summary>
        internal static bool ProtectsDeathAtCore(bool worldEventsEnabled, bool deathProtectionEnabled,
            WorldEventState? state, Position loc, Position anchor, float radius)
        {
            if (!worldEventsEnabled || !deathProtectionEnabled)
                return false;

            if (state != WorldEventState.Announced && state != WorldEventState.Active)
                return false;

            if (loc == null || anchor == null || radius <= 0)
                return false;

            if (loc.Instance != anchor.Instance)
                return false;

            return loc.DistanceTo(anchor) <= radius;
        }

        /// <summary>
        /// Called once from Program.cs, after EventManager.Initialize(). Loads the axis store and builds the
        /// family catalog.
        ///
        /// Never throws: a broken axis folder or an unreachable world database must not stop the server
        /// from booting, it just leaves the system unable to start an event.
        /// </summary>
        public static void Initialize()
        {
            lock (sync)
            {
                try
                {
                    Store = WorldEventAxisStore.LoadFromServerConfig();

                    foreach (var diagnostic in Store.Diagnostics)
                        log.Warn($"[WORLDEVENT] axis: {diagnostic}");

                    // Notes are working-as-intended outcomes, so they stay off the WARN channel - see
                    // WorldEventAxisStore.Notes for why that separation matters. The count still appears
                    // in the summary line below, so a boot never hides that they happened.
                    foreach (var note in Store.Notes)
                        log.Debug($"[WORLDEVENT] axis note: {note}");

                    log.Info($"[WORLDEVENT] axes loaded: sources={Store.Sources.Count} families={Store.Families.Count} bosses={Store.Bosses.Count} goals={Store.Goals.Count} rewards={Store.Rewards.Count} anchors={Store.Anchors.Count} diagnostics={Store.Diagnostics.Count} notes={Store.Notes.Count}");

                    BuildCatalog();
                    WorldEventTownIndex.Load();
                }
                catch (Exception ex)
                {
                    log.Error("[WORLDEVENT] Initialize failed; world events will refuse to start", ex);
                }
            }
        }

        /// <summary>
        /// Builds the family catalog from a targeted world-database read rather than a full weenie-cache
        /// warm-up (TECH-DESIGN C15, 2026-08-16): one indexed query for every wcid flagged
        /// PropertyBool.WorldEventCreature, unioned with every wcid referenced by a species table
        /// (WorldEventCatalogBuilder.CollectWcids), each then resolved with a lazy per-wcid
        /// DatabaseManager.World.GetCachedWeenie read. This replaces the old CacheAllWeenies() boot warm-up,
        /// which paid a 10+ second cost to iterate every weenie in the world database just to find the
        /// handful carrying the flag.
        /// </summary>
        private static void BuildCatalog()
        {
            warnedSynthesizedFamilies.Clear();

            try
            {
                var stopwatch = Stopwatch.StartNew();

                var flagged = DatabaseManager.World.GetWeenieClassIdsWithBool((ushort)PropertyBool.WorldEventCreature, true);

                var tableIds = Store.SpeciesTables.Values
                    .SelectMany(t => t?.Members ?? new List<SpeciesMemberDef>())
                    .Select(m => m?.Wcid ?? 0)
                    .Where(w => w != 0)
                    .Distinct()
                    .Count();

                var wcids = WorldEventCatalogBuilder.CollectWcids(flagged, Store.SpeciesTables.Values);

                var weenies = wcids.Select(DatabaseManager.World.GetCachedWeenie).Where(w => w != null).ToList();

                Catalog = WorldEventCatalogBuilder.Build(weenies, Store.SpeciesTables.Values, out var diagnostics);

                stopwatch.Stop();

                foreach (var diagnostic in diagnostics)
                    log.Warn($"[WORLDEVENT] catalog: {diagnostic}");

                log.Info($"[WORLDEVENT] catalog built: flaggedIds={flagged.Count} tableIds={tableIds} resolved={weenies.Count} families={Catalog.Families.Count} members={Catalog.MemberCount} tables={Store.SpeciesTables.Count} diagnostics={Catalog.Diagnostics.Count} in {stopwatch.ElapsedMilliseconds} ms");
            }
            catch (Exception ex)
            {
                log.Error("[WORLDEVENT] catalog build failed; no family will resolve", ex);
                Catalog = WorldEventCatalog.Empty;
            }
        }

        /// <summary>
        /// One second of manager time, self-throttled off the world tick (which runs far faster). Never
        /// throws: anything escaping the running event aborts that event rather than the world thread.
        /// </summary>
        public static void Tick()
        {
            try
            {
                var now = Time.GetUnixTime();

                if (now < nextTickTime)
                    return;

                nextTickTime = now + 1.0;

                WorldEvent evt;

                lock (sync)
                {
                    ClearCurrentIfDone();
                    evt = Current;
                }

                if (evt == null)
                    return;

                // Deliberately ticked outside the lock on a local reference: a Finish raised from inside
                // Tick reenters nothing the manager is iterating.
                evt.Tick(now);

                lock (sync)
                    ClearCurrentIfDone();
            }
            catch (Exception ex)
            {
                log.Error("[WORLDEVENT] Tick threw; aborting the current event", ex);

                try
                {
                    Current?.Finish(WorldEventOutcome.AbortedError);
                }
                catch (Exception inner)
                {
                    log.Error("[WORLDEVENT] abort after a Tick failure also threw", inner);
                }

                lock (sync)
                    ClearCurrentIfDone();
            }
        }

        /// <summary>Must be called under <see cref="sync"/>.</summary>
        private static void ClearCurrentIfDone()
        {
            if (Current != null && Current.State == WorldEventState.Done)
            {
                log.Info($"[WORLDEVENT] run={Current.RunId} cleared (outcome={Current.Outcome})");
                Current = null;
            }
        }

        /// <summary>
        /// Pure resolution of axis ids into definitions - no side effects, nothing staged. Used by
        /// TryStart, by "--dry" and by "/worldevent simulate".
        /// </summary>
        public static bool TryCompose(WorldEventRequest request, out WorldEventComposition composition, out string error)
        {
            var store = Store;

            var composed = WorldEventComposer.TryCompose(store, Catalog, request, out composition, out error);

            // A family that exists in the weenie catalog but not in families.json gets a synthesized
            // definition rather than a refusal (so a throwaway flagged clone works without a content edit).
            // The composer is pure, so the warning is emitted here, once per catalog build.
            if (composed && composition != null && store != null)
            {
                // Every composed family is checked, not just the first: a two-family run can synthesize
                // either half (or both), and a warning that only ever looked at slot A would go silent on
                // exactly the throwaway clone this exists to flag.
                foreach (var family in composition.Families)
                {
                    if (family?.Id == null || store.Families.ContainsKey(family.Id))
                        continue;

                    lock (sync)
                    {
                        if (warnedSynthesizedFamilies.Add(family.Id))
                            log.Warn($"[WORLDEVENT] family '{family.Id}' is in the weenie catalog but not in families.json; using a synthesized definition (displayName = id, no tags)");
                    }
                }
            }

            return composed;
        }

        /// <summary>
        /// Composes and stages a run. The refusal order is fixed so the command handler's message is
        /// predictable: disabled, then already-running, then no axes, then composition, then objective.
        ///
        /// A dry run composes, logs one line and returns true with a null event.
        /// </summary>
        public static bool TryStart(WorldEventRequest request, out WorldEvent evt, out string error)
        {
            evt = null;
            error = null;

            lock (sync)
            {
                ClearCurrentIfDone();

                if (!Enabled)
                {
                    error = "world events are disabled (world_events_enabled)";
                    return false;
                }

                if (Current != null)
                {
                    error = $"an event is already running (run {Current.RunId}, {Current.State})";
                    return false;
                }

                if (Store == null || Store.IsEmpty)
                {
                    error = "no axes loaded";
                    return false;
                }

                // TECH-DESIGN 2.15, ahead of composition because it is a defect in the REQUEST rather than
                // in the axes: a run whose minimum duration meets or exceeds its maximum could only ever
                // end by timing out.
                if (!WorldEvent.ValidateDurations(
                        request?.MinDurationSeconds ?? WorldEvent.DefaultMinDurationSeconds,
                        request?.MaxDurationSeconds ?? WorldEvent.DefaultMaxDurationSeconds, out error))
                    return false;

                if (!TryCompose(request, out var composition, out error))
                    return false;

                if (request.DryRun)
                {
                    log.Info($"[WORLDEVENT] dry-run compose {composition.ToAxisSummary()}");
                    return true;
                }

                // The WP-06 objective factory needs a constructed event to read the composition from, so
                // the event is built first and the objective attached before it is ever staged. A refusal
                // here discards the candidate: Current is untouched and nothing was announced.
                var candidate = new WorldEvent(NextRunId(), composition, request, null,
                    landblockBridge: LiveWorldEventLandblockBridge.Instance);

                var objective = WorldEventObjectiveFactory.Create(candidate, out var objectiveError);

                if (objective == null)
                {
                    error = string.IsNullOrEmpty(objectiveError)
                        ? $"no objective could be created for goal '{composition.Goal?.Id}'"
                        : objectiveError;

                    return false;
                }

                candidate.SetObjective(objective);

                Current = candidate;

                log.Info(composition.ToLogLine(candidate.RunId));

                // Two-family composition (2026-08-29): one line per RANDOMLY composed run saying which
                // pair was drawn and how constrained the draw could be. Warn on any degraded tier, because
                // a tier below FloorAndCaster means the catalog itself could not satisfy the pairing rule -
                // that is a content gap somebody should see, not a per-run accident. Nothing is announced
                // to players either way.
                if (composition.PairingTier != null)
                {
                    var a = composition.Families.Count > 0 ? composition.Families[0]?.Id : "none";
                    var b = composition.Families.Count > 1 ? composition.Families[1]?.Id : "none";

                    var pairingLine = $"[WORLDEVENT] run={candidate.RunId} family pairing tier={composition.PairingTier} " +
                                      $"a={a} b={b} floor={composition.PairingFloorLevel}";

                    if (composition.PairingTier == WorldEventFamilyPairing.PairTier.FloorAndCaster)
                        log.Info(pairingLine);
                    else
                        log.Warn(pairingLine);
                }

                // TECH-DESIGN "Live overrides": one visibility line per run listing which world_events dial
                // overrides are active, so a run's sizing can be explained from the log alone.
                log.Info($"[WORLDEVENT] run={candidate.RunId} {WorldEventOverrides.ActiveOverridesSummary()}");

                // Round-2 review: wave_count_base/wave_count_cap overrides are each individually validated
                // (positive, finite) but never validated as a PAIR - a cap override below the effective
                // base is a content-authoring mistake worth a diagnostic even though it changes nothing:
                // ScaledCount.Resolve's Math.Max(base, Math.Min(cap, raw)) already floors the result at
                // base regardless, so the wave is pinned to base every pick rather than broken. This is
                // NOT the same class of problem the pace window guard fixes (that one silently inverts
                // meaning; this one silently disables the cap) - a log line is enough, no clamp needed.
                var composedTheme = candidate.Composition?.Source;

                if (composedTheme != null && composedTheme.EffectiveWaveCountCap < composedTheme.EffectiveWaveCountBase)
                {
                    log.Warn($"[WORLDEVENT] run={candidate.RunId} wave_count overrides produce cap " +
                             $"({composedTheme.EffectiveWaveCountCap}) below base ({composedTheme.EffectiveWaveCountBase}) " +
                             $"for source '{composedTheme.Id}'; ScaledCount.Resolve still floors at base, so trash size is " +
                             $"pinned to {composedTheme.EffectiveWaveCountBase} every wave");
                }

                candidate.Begin(Time.GetUnixTime());

                evt = candidate;
                return true;
            }
        }

        /// <summary>Admin stop. A no-op with a log line when nothing is running.</summary>
        public static void StopCurrent(WorldEventOutcome outcome, string invoker)
        {
            lock (sync)
            {
                ClearCurrentIfDone();

                if (Current == null)
                {
                    log.Info($"[WORLDEVENT] stop requested by {invoker ?? "unknown"} with outcome={outcome}, but no event is running");
                    return;
                }

                log.Info($"[WORLDEVENT] run={Current.RunId} stop requested by {invoker ?? "unknown"} outcome={outcome}");

                Current.Finish(outcome);

                ClearCurrentIfDone();
            }
        }

        /// <summary>Called from the world thread loop as it concedes, before WorldActive goes false.</summary>
        public static void OnShutdown()
        {
            lock (sync)
            {
                if (Current == null || Current.State == WorldEventState.Done)
                {
                    Current = null;
                    return;
                }

                log.Info($"[WORLDEVENT] run={Current.RunId} aborting for server shutdown");

                try
                {
                    Current.Finish(WorldEventOutcome.AbortedShutdown);
                }
                catch (Exception ex)
                {
                    log.Error("[WORLDEVENT] shutdown abort threw", ex);
                }

                Current = null;
            }
        }

        /// <summary>
        /// Receiver for the Creature.Die hook (the call site is in Creature_Death.cs, behind the
        /// P_WorldEvent back-reference check).
        ///
        /// Deliberately unlocked: it is called from a landblock tick thread and must not contend with the
        /// manager's own second.
        /// </summary>
        /// <summary>
        /// Called from WorldObject.OnGeneration, i.e. from inside EnterWorld on a landblock action queue,
        /// for EVERY object any generator on the server puts into the world (2026-09-05).
        ///
        /// THIS IS A HOT PATH. Retail generators are everywhere and most of them have nothing to do with a
        /// world event, so the common case must cost a bare static field read and return. The three gates,
        /// cheapest first:
        ///   1. <see cref="Current"/> is null - no run at all. One field read, no lock, exactly as
        ///      <see cref="ProtectsDeathAt"/> reads it.
        ///   2. the generator is not standing on a landblock this run holds - IsHeldLandblock reads a
        ///      snapshot field and does one HashSet lookup, still no lock. This is what eliminates every
        ///      retail generator on the rest of the map while a run is up.
        ///   3. only then, the ownership question: has this run ever held the generator's guid? That one
        ///      takes the HeldObjects monitor.
        ///
        /// Never throws into EnterWorld: a failure here must not stop an object entering the world.
        /// </summary>
        public static void OnGeneratedIntoWorld(WorldObject wo, WorldObject generator)
        {
            var evt = Current;

            if (evt == null || wo == null || generator == null)
                return;

            try
            {
                // Structurally impossible (a Player is not generated) and asserted anyway, because the
                // consequence of being wrong is a player character in a run's destroy list.
                if (wo is Player)
                    return;

                var loc = generator.Location ?? wo.Location;

                if (loc == null || !evt.IsHeldLandblock(loc.InstancedLandblock))
                    return;

                if (!WorldEventGeneratedAdds.AdoptsGeneratedChild(runActive: true,
                        generatorHeld: evt.IsHeld(generator.Guid.Full), childIsPlayer: false))
                    return;

                evt.Spawner.AdoptGeneratedAdd(evt, wo);
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] run={evt.RunId} generated-add adoption threw for guid=0x{wo.Guid.Full:X8}", ex);
            }
        }

        public static void OnEventCreatureDied(Creature creature, DamageHistoryInfo lastDamager, DamageHistoryInfo topDamager)
        {
            var evt = Current;

            if (evt == null || evt.State == WorldEventState.Done)
                return;

            // A creature left over from an EARLIER run - one whose destroy was queued but has not drained,
            // or one that outlived a stop - must never advance the run that is live now. The back-reference
            // is the only thing that can tell them apart, since both carry a WorldEventId stamp.
            if (creature?.P_WorldEvent == null || !ReferenceEquals(creature.P_WorldEvent, evt))
                return;

            try
            {
                evt.OnCreatureDied(creature, lastDamager, topDamager);
            }
            catch (Exception ex)
            {
                log.Error("[WORLDEVENT] OnEventCreatureDied threw", ex);
            }
        }

        /// <summary>
        /// Rebuilds the axis store from disk and the family catalog from a fresh targeted world-database
        /// query. BuildCatalog re-runs the flagged-wcid query every time, so a NEW wcid imported by SQL is
        /// picked up by a reload on its own; an EDITED weenie whose wcid is already resident in the world
        /// weenie cache still resolves through GetCachedWeenie to the cached copy, so run the Developer
        /// "clearcache weenie" command before the reload in that case. A running event keeps the store it
        /// staged with.
        /// </summary>
        public static string Reload()
        {
            lock (sync)
            {
                try
                {
                    Store = WorldEventAxisStore.LoadFromServerConfig();

                    foreach (var diagnostic in Store.Diagnostics)
                        log.Warn($"[WORLDEVENT] axis: {diagnostic}");

                    BuildCatalog();
                    WorldEventTownIndex.Load();

                    var running = Current != null && Current.State != WorldEventState.Done
                        ? $" Run {Current.RunId} is in progress and keeps the axis store it staged with."
                        : "";

                    return $"World Events reloaded from {Store.ResolvedFolder ?? "no axis folder"}: " +
                           $"{Store.Sources.Count} sources, {Store.Families.Count} families, {Store.Goals.Count} goals, " +
                           $"{Store.Rewards.Count} rewards, {Store.Anchors.Count} anchors, {Store.SpeciesTables.Count} species tables " +
                           $"({Store.Diagnostics.Count} axis diagnostics, {Store.Notes.Count} notes). " +
                           $"Catalog: {Catalog.Families.Count} families, {Catalog.MemberCount} members " +
                           $"({Catalog.Diagnostics.Count} catalog diagnostics). " +
                           $"Town index: {WorldEventTownIndex.Towns.Count} towns.{running}";
                }
                catch (Exception ex)
                {
                    log.Error("[WORLDEVENT] reload failed", ex);
                    return $"World Events reload failed: {ex.Message}";
                }
            }
        }

        /// <summary>
        /// One-line status for "/worldevent status". Called from a command handler, i.e. off the world
        /// thread relative to the ledger's own writes (see WorldEventParticipation's threading remarks) -
        /// a torn read of Records/Count/TopKiller here is benign, since this method only ever reads.
        /// </summary>
        public static string StatusText()
        {
            var evt = Current;

            // Global config, not per-run, so this is reported whether or not a run is active - the accepted
            // review finding (see the branch's history) was that only "/worldevent simulate" showed it.
            var bandDials = WorldEventRosterSelector.DialSource();
            var bandDialsText = $"band dials: trash x{bandDials.TrashLow:F2}-x{bandDials.TrashHigh:F2}, " +
                                 $"elite x{bandDials.EliteLow:F2}-x{bandDials.EliteHigh:F2}, " +
                                 $"family floor: {WorldEventFamilyPairing.FloorLevel()}";
            var overridesText = WorldEventOverrides.ActiveOverridesSummary();

            if (evt == null || evt.State == WorldEventState.Done)
                return $"World Events: idle. {bandDialsText}. {overridesText}.";

            // Before Stage(), Composition is set but wave/spawner/pace numbers are all meaningless - the
            // audience has not been sampled yet. A short line reporting the teaser countdown instead of the
            // full difficulty readout.
            if (evt.State == WorldEventState.Idle)
            {
                var remaining = Math.Max(0, evt.TeasedAt + evt.TeaserLeadSeconds - Time.GetUnixTime());
                return $"World Events run {evt.RunId}: teaser out, {evt.Composition?.ToAxisSummary()}, staging in {remaining:F0}s.";
            }

            var elapsed = evt.ActiveAt > 0 ? Time.GetUnixTime() - evt.ActiveAt : 0;

            var progress = string.IsNullOrEmpty(evt.ProgressText) ? "n/a" : evt.ProgressText;

            var topKiller = evt.Participation?.TopKiller();

            // WP-21: the resolved objective health, when the theme scales it, purely for /worldevent status
            // visibility - not the health each individual source may since carry, only what the axis
            // currently resolves to for this run's estimated participant count. Independent of the
            // crowd/pace multipliers below, which are a WAVE rule (see WorldEventSpawner.Spawn).
            var objectiveHealth = evt.Composition?.Source?.ObjectiveHealth;
            var objectiveHealthText = objectiveHealth != null
                ? $", objectiveHealth={objectiveHealth.Resolve(Math.Max(0, evt.ResolvedAudienceCount))}"
                : "";

            // TECH-DESIGN 2.15 difficulty readout. "target" is the RATCHETED kill target (n/a for a goal
            // that has none), "minLeft" the seconds still owed on the minimum duration, "crowd"/"pace" the
            // two halves of the spawn health multiplier, "qbonus" the pace controller's quantity dial,
            // "lastClear" how long the last wave took to die, and "ofChampions" how many overflow champions
            // are standing (they are additive ABOVE maxAlive, so they are reported separately).
            var target = evt.Objective?.TargetKills;
            var lastClear = evt.Pace?.LastClearSeconds;

            // C16: "hpCleared"/"hps" are the measured group throughput (HP removed per second of Active
            // time), reported whether or not the flag is on so the two sizing modes can be compared without
            // switching it. "bossMode" is which one this run's boss was sized by; while it is throughput,
            // "bossHps" is the rate the boss was SIZED at (frozen at spawn - the finish line's bossHps is a
            // different quantity, the rate actually achieved against it) and "bossFloor" the health it was
            // rebased to.
            var cleared = evt.Throughput?.TotalHealthCleared ?? 0;
            var hps = evt.Throughput?.Throughput(evt.ActiveSeconds(Time.GetUnixTime())) ?? 0;

            var bossThroughput = evt.BossThroughputActive
                ? $", bossHps={evt.BossThroughputHps:F1}, bossFloor={evt.Composition?.Boss?.EffectiveThroughputFloorHealth ?? 0}"
                : "";

            // TECH-DESIGN 2.16 boss damage readout, appended only while a NAMED boss is actually standing -
            // every field of it is meaningless otherwise. "rating" is what the run believes the boss's
            // DamageRating to be (the authored value until the first adjustment lands), "target" the damage
            // one ordinary hit should do (topMaxHealth / hitsToKill), "observed" the mean of the buffered
            // non-crit hits, and "hits" how many of those are buffered toward the next decision. The five
            // dials themselves are not repeated here - "/worldevent properties" lists them.
            //
            // ONE PendingSnapshot call, not two property reads: hits arrive on landblock threads while this
            // runs off the world thread, and a true advice zeroes the window, so two separate locked reads
            // could pair a pre-reset hit count with a post-reset mean of 0 - a readout saying the boss has
            // landed four hits for an average of nothing.
            var pending = evt.BossDamage.PendingSnapshot();

            var bossDamage = evt.Composition?.Boss?.Kind == BossKind.Named && evt.Spawner.BossGuid != 0
                ? $", boss damage: rating {evt.BossDamageRating} target {evt.BossDamageTargetHit:F0} " +
                  $"observed {pending.Mean:F0} hits {pending.Hits}"
                : "";

            var difficulty = $"target={(target == null ? "n/a" : target.Value.ToString())}, " +
                             $"minLeft={evt.MinDurationRemaining(Time.GetUnixTime()):F0}s, " +
                             $"crowd=x{evt.CrowdHealthMult:F2}, pace=x{(evt.Pace?.HealthMult ?? 1.0):F2}, " +
                             $"qbonus={evt.Pace?.QuantityBonus ?? 0}, " +
                             $"lastClear={(lastClear == null ? "n/a" : $"{lastClear.Value:F0}s")}, " +
                             $"ofChampions={evt.Spawner.OverflowChampionsAlive}, " +
                             $"hpCleared={cleared:F0}, hps={hps:F1}, " +
                             $"bossMode={(evt.BossThroughputActive ? "throughput" : "power")}{bossThroughput}{bossDamage}";

            // "alive" is wave pressure (what MaxAlive budgets); "total" adds the objective spawns and the
            // champion, which are deliberately not charged against that budget. "decor" is the WP-14 scenery
            // and "npcs" the WP-15 attendants, which are in neither - neither is a kill, pressure or an
            // objective. "adds" (2026-09-05) is the same again for objects the run's OWN monsters generated
            // and it adopted purely so cleanup destroys them - a non-zero reading here is the visible
            // symptom of the leak this fix closes, which is why it belongs on the status line.
            return $"World Events run {evt.RunId}: state {evt.State}, {evt.Composition?.ToAxisSummary()}, " +
                   $"wave {evt.WaveIndex}, alive {evt.Spawner.LiveCount} (total {evt.Spawner.LiveTotal}), " +
                   $"decor={evt.Spawner.DecorCount}, npcs={evt.Spawner.NpcCount}, " +
                   $"adds={evt.Spawner.AddCount}{objectiveHealthText}, " +
                   $"progress: {progress}, elapsed {elapsed:F0}s, " +
                   $"{difficulty}, " +
                   $"participants={evt.Participation?.Count ?? 0} topKiller={topKiller?.Name ?? "none"}. " +
                   $"{bandDialsText}. {overridesText}.";
        }

        /// <summary>Monotonic run token, never 0, for the process lifetime.</summary>
        public static uint NextRunId()
        {
            return (uint)Interlocked.Increment(ref runIdCounter);
        }
    }
}
