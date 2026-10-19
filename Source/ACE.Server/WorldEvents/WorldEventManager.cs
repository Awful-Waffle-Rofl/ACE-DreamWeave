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
    /// Why <see cref="WorldEventManager.TryStart(WorldEventRequest, out WorldEvent, out string, out WorldEventStartRefusal)"/>
    /// refused, in its fixed refusal order. The human text still travels in the error string; this is the
    /// value a caller maps on.
    /// </summary>
    public enum WorldEventStartRefusal
    {
        None,
        Disabled,
        AlreadyRunning,
        NoAxes,
        InvalidDurations,
        CompositionFailed,
        ObjectiveFailed
    }

    /// <summary>Why <see cref="WorldEventManager.TryStopCurrent"/> refused.</summary>
    public enum WorldEventStopRefusal
    {
        None,
        NotRunning,
        RunChanged,

        /// <summary>The expected run is current but has already resolved; its claim window may be open.</summary>
        Finished
    }

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

        /// <summary>
        /// World Events chain: a finished run kept in its reward claim window while the next stage of its chain is
        /// <see cref="Current"/>. Still ticked (so its claim window and cleanup run as usual) and still pays its caches;
        /// cleared when it reaches Done. Null otherwise.
        /// </summary>
        public static WorldEvent Previous { get; private set; }

        /// <summary>Runs whose end has been checked for a chain, so each is handed over at most once.</summary>
        private static readonly HashSet<uint> chainChecked = new HashSet<uint>();

        private static readonly Random chainRng = new Random();

        /// <summary>The live run with this id: <see cref="Current"/>, or a chained run's finishing <see cref="Previous"/>. Null otherwise.</summary>
        public static WorldEvent FindRun(uint runId)
        {
            var current = Current;

            if (current != null && current.RunId == runId)
                return current;

            var previous = Previous;

            return previous != null && previous.RunId == runId ? previous : null;
        }

        /// <summary>The other live run beside <paramref name="evt"/> (Current or Previous), or null.</summary>
        private static WorldEvent OtherRun(WorldEvent evt)
        {
            var current = Current;
            var previous = Previous;

            if (current != null && !ReferenceEquals(current, evt) && current.State != WorldEventState.Done)
                return current;

            if (previous != null && !ReferenceEquals(previous, evt) && previous.State != WorldEventState.Done)
                return previous;

            return null;
        }

        /// <summary>
        /// <paramref name="keys"/> without the landblocks another live run holds (a chained stage standing next to the
        /// finishing one): those stay permaloaded for it. Pure apart from reading the two run references.
        /// </summary>
        internal static IReadOnlyList<ulong> WithoutBlocksHeldByOtherRuns(WorldEvent evt, IReadOnlyList<ulong> keys)
        {
            var other = OtherRun(evt);

            if (other == null || keys == null)
                return keys ?? new List<ulong>();

            return keys.Where(k => !other.IsHeldLandblock(k)).ToList();
        }

        /// <summary>
        /// The permaload snapshot a staging run compares against, with the blocks another live run holds counted as not
        /// permaloaded: that run leaves them to this one when it finishes, so this one releases them in its turn.
        /// </summary>
        internal static IReadOnlyDictionary<ulong, bool> PriorWithoutOtherRuns(WorldEvent evt, IReadOnlyDictionary<ulong, bool> prior)
        {
            var other = OtherRun(evt);

            if (other == null || prior == null)
                return prior;

            var copy = new Dictionary<ulong, bool>(prior);

            foreach (var key in prior.Keys)
                if (other.IsHeldLandblock(key))
                    copy[key] = false;

            return copy;
        }

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
        ///
        /// The build runs OUTSIDE <see cref="sync"/> and only the finished store and catalog are published
        /// under it. Program.cs runs this after WorldManager.Initialize has started the world thread, and
        /// the very first <see cref="Tick"/> takes <see cref="sync"/>; while the build held the lock (the
        /// catalog alone is ~16 s of lazy per-wcid world-DB reads on stage), that first Tick blocked for
        /// the whole build and world-loop iteration 1 logged SLOW_TICK total_ms=17274 / 18034 with all of
        /// it in ph_world_managers (stage 2026-09-21 21:14:58 and 19:29:32).
        /// </summary>
        public static void Initialize()
        {
            InitializeWith(LoadStoreAndCatalog);

            // Never throws, and publishes under its own lock (WorldEventTownIndex.Load).
            WorldEventTownIndex.Load();
        }

        /// <summary>
        /// Test seam for <see cref="Initialize"/> (InternalsVisibleTo, ACE.Server.csproj:15): runs
        /// <paramref name="load"/> with <see cref="sync"/> NOT held, then publishes its result under it.
        /// </summary>
        internal static void InitializeWith(Func<(WorldEventAxisStore Store, WorldEventCatalog Catalog)> load)
        {
            try
            {
                var (store, catalog) = load();

                Publish(store, catalog);
            }
            catch (Exception ex)
            {
                log.Error("[WORLDEVENT] Initialize failed; world events will refuse to start", ex);
            }
        }

        /// <summary>
        /// Swaps in a fully built store and its catalog as one step, so no reader holding the lock ever sees
        /// a new store paired with the previous catalog. The synthesized-family warnings are per catalog
        /// build, so they reset here.
        /// </summary>
        private static void Publish(WorldEventAxisStore store, WorldEventCatalog catalog)
        {
            lock (sync)
            {
                Store = store;
                Catalog = catalog;
                warnedSynthesizedFamilies.Clear();
            }
        }

        private static (WorldEventAxisStore Store, WorldEventCatalog Catalog) LoadStoreAndCatalog()
        {
            var store = WorldEventAxisStore.LoadFromServerConfig();

            foreach (var diagnostic in store.Diagnostics)
                log.Warn($"[WORLDEVENT] axis: {diagnostic}");

            // Notes are working-as-intended outcomes, so they stay off the WARN channel - see
            // WorldEventAxisStore.Notes for why that separation matters. The count still appears
            // in the summary line below, so a boot never hides that they happened.
            foreach (var note in store.Notes)
                log.Debug($"[WORLDEVENT] axis note: {note}");

            log.Info($"[WORLDEVENT] axes loaded: sources={store.Sources.Count} families={store.Families.Count} bosses={store.Bosses.Count} goals={store.Goals.Count} rewards={store.Rewards.Count} anchors={store.Anchors.Count} diagnostics={store.Diagnostics.Count} notes={store.Notes.Count}");

            return (store, BuildCatalog(store));
        }

        /// <summary>
        /// Builds the family catalog from a targeted world-database read rather than a full weenie-cache
        /// warm-up (TECH-DESIGN C15, 2026-08-16): one indexed query for every wcid flagged
        /// PropertyBool.WorldEventCreature, unioned with every wcid referenced by a species table
        /// (WorldEventCatalogBuilder.CollectWcids), each then resolved with a lazy per-wcid
        /// DatabaseManager.World.GetCachedWeenie read. This replaces the old CacheAllWeenies() boot warm-up,
        /// which paid a 10+ second cost to iterate every weenie in the world database just to find the
        /// handful carrying the flag.
        ///
        /// Pure with respect to this manager's state: it reads only <paramref name="store"/> and returns the
        /// catalog, so callers can run it without holding <see cref="sync"/> and publish afterwards.
        /// </summary>
        private static WorldEventCatalog BuildCatalog(WorldEventAxisStore store)
        {
            try
            {
                var stopwatch = Stopwatch.StartNew();

                var flagged = DatabaseManager.World.GetWeenieClassIdsWithBool((ushort)PropertyBool.WorldEventCreature, true);

                var tableIds = store.SpeciesTables.Values
                    .SelectMany(t => t?.Members ?? new List<SpeciesMemberDef>())
                    .Select(m => m?.Wcid ?? 0)
                    .Where(w => w != 0)
                    .Distinct()
                    .Count();

                var wcids = WorldEventCatalogBuilder.CollectWcids(flagged, store.SpeciesTables.Values);

                var weenies = wcids.Select(DatabaseManager.World.GetCachedWeenie).Where(w => w != null).ToList();

                var catalog = WorldEventCatalogBuilder.Build(weenies, store.SpeciesTables.Values, out var diagnostics);

                stopwatch.Stop();

                foreach (var diagnostic in diagnostics)
                    log.Warn($"[WORLDEVENT] catalog: {diagnostic}");

                log.Info($"[WORLDEVENT] catalog built: flaggedIds={flagged.Count} tableIds={tableIds} resolved={weenies.Count} families={catalog.Families.Count} members={catalog.MemberCount} tables={store.SpeciesTables.Count} diagnostics={catalog.Diagnostics.Count} in {stopwatch.ElapsedMilliseconds} ms");

                return catalog;
            }
            catch (Exception ex)
            {
                log.Error("[WORLDEVENT] catalog build failed; no family will resolve", ex);
                return WorldEventCatalog.Empty;
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
                WorldEvent previous;

                lock (sync)
                {
                    ClearCurrentIfDone();
                    evt = Current;
                    previous = Previous;
                }

                // A chained run's finishing first stage: its claim window and cleanup go on as usual.
                if (previous != null)
                {
                    try
                    {
                        previous.Tick(now);
                    }
                    catch (Exception ex)
                    {
                        log.Error($"[WORLDEVENT] run={previous.RunId} (finishing, chained) tick threw; finishing it", ex);
                        previous.Finish(WorldEventOutcome.AbortedError);
                    }

                    lock (sync)
                        ClearCurrentIfDone();
                }

                if (evt == null)
                    return;

                // Deliberately ticked outside the lock on a local reference: a Finish raised from inside
                // Tick reenters nothing the manager is iterating.
                evt.Tick(now);

                lock (sync)
                    ClearCurrentIfDone();

                // World Events chain: once a run has ended, a success may hand over to the next stage nearby.
                TryChain(evt);
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

            if (Previous != null && Previous.State == WorldEventState.Done)
            {
                log.Info($"[WORLDEVENT] run={Previous.RunId} cleared (outcome={Previous.Outcome}, finished beside a chained run)");
                Previous = null;
            }
        }

        /// <summary>
        /// World Events chain (goals.json "chain"): called after every tick of the current run. Once it has ended (Resolved
        /// or later), its outcome is checked exactly once:
        ///   * a success with stages left: a spot 250 to 500 m away (chain.minDistance / maxDistance) on open ground, a
        ///     random compatible source, family and goal there, starting chain.countdownSeconds later, announced to the
        ///     world with its place. The finished run moves to <see cref="Previous"/> and keeps its claim window;
        ///   * a failure or an abort, or the last stage: the chain ends.
        /// Everything is logged with the chain's first run id and the stage. Never throws.
        /// </summary>
        private static void TryChain(WorldEvent evt)
        {
            try
            {
                if (evt == null || evt.State < WorldEventState.Resolved)
                    return;

                lock (sync)
                {
                    if (!chainChecked.Add(evt.RunId))
                        return;
                }

                var request = evt.Request;
                var chain = request?.ChainSettings ?? evt.Composition?.Goal?.Chain;
                var maxLength = WorldEventChain.EffectiveMaxLength(request?.ChainMaxLength ?? 0, evt.Composition?.Goal?.Chain);
                var stage = Math.Max(1, request?.ChainStage ?? 1);
                var root = request != null && request.ChainRootRunId != 0 ? request.ChainRootRunId : evt.RunId;

                switch (WorldEventChain.Decide(evt.Outcome, stage, maxLength))
                {
                    case WorldEventChain.Decision.None:
                        return;

                    case WorldEventChain.Decision.EndedByOutcome:
                        log.Info($"[WORLDEVENT] chain root={root} stage {stage}/{maxLength} run={evt.RunId} ended {evt.Outcome}; the chain ends");
                        return;

                    case WorldEventChain.Decision.EndedAtMax:
                        log.Info($"[WORLDEVENT] chain root={root} stage {stage}/{maxLength} run={evt.RunId} succeeded; that was the last stage, the chain is complete");
                        return;
                }

                var origin = evt.Composition?.AnchorPosition;

                // Where the next stage goes: a different town of the Town Network (chain.toAnotherTown), else the old
                // 250 to 500 m spot. Up to three towns are tried, in case a town's edge has no open ground or the next goal's
                // spawn-area check refuses it.
                var spots = new List<(Position Anchor, string Town)>();
                var why = "no other town in the Town Network";
                var originTownName = origin != null ? WorldEventTownIndex.Nearest(origin, WorldEventTownIndex.Towns)?.Town.Name : null;

                if (chain == null || chain.ToAnotherTown)
                {
                    var towns = WorldEventTownIndex.Towns;
                    var originTown = origin != null ? WorldEventTownIndex.Nearest(origin, towns)?.Town.Name : null;
                    var originGlobal = origin?.ToGlobal() ?? default;
                    var tried = new List<string>();

                    for (var attempt = 0; attempt < 3 && origin != null && !origin.Indoors; attempt++)
                    {
                        var town = WorldEventChain.PickTown(towns, originGlobal.X, originGlobal.Y, originTown, chain, chainRng, tried);

                        if (town == null)
                            break;

                        tried.Add(town.Value.Name);

                        if (WorldEventChain.TryPickNearTown(town.Value, origin, chain, chainRng, out var townSpot))
                            spots.Add((townSpot, town.Value.Name));
                        else
                            log.Info($"[WORLDEVENT] chain root={root} stage {stage}/{maxLength}: no open ground at the edge of {town.Value.Name}; trying another town");
                    }

                    if (spots.Count == 0 && tried.Count > 0)
                        why = $"no open ground near {string.Join(", ", tried)}";
                    else if (spots.Count == 0 && originTown != null)
                        why = $"no town other than {originTown} at least {chain?.TownMinDistance ?? 800:0} m away";
                }
                else if (WorldEventChain.TryPickNearbyAnchor(origin, chain, chainRng, out var nearby, out why))
                    spots.Add((nearby, null));

                if (spots.Count == 0)
                {
                    log.Warn($"[WORLDEVENT] chain root={root} stage {stage}/{maxLength} run={evt.RunId} succeeded, but {why}; the chain ends");
                    return;
                }

                WorldEvent started = null;
                Position anchor = null;
                string label = null, townName = null, error = null;

                lock (sync)
                {
                    if (Previous != null && Previous.State != WorldEventState.Done)
                    {
                        log.Warn($"[WORLDEVENT] chain root={root} stage {stage}/{maxLength}: run {Previous.RunId} is still finishing; the chain ends");
                        return;
                    }

                    var retired = false;

                    if (ReferenceEquals(Current, evt))
                    {
                        // The finished run keeps its claim window beside the next stage.
                        Previous = evt;
                        Current = null;
                        retired = true;
                    }
                    else if (Current != null)
                    {
                        log.Warn($"[WORLDEVENT] chain root={root} stage {stage}/{maxLength}: run {Current.RunId} has already started; the chain ends");
                        return;
                    }

                    foreach (var (spot, town) in spots)
                    {
                        var spotLabel = town != null ? WorldEventTownIndex.DescribeLabeled(town, spot) : WorldEventTownIndex.Describe(spot);
                        var next = WorldEventChain.NextRequest(request, chain, stage + 1, maxLength, root, evt.RunId, spot, spotLabel);
                        next.ChainSettings = chain;

                        // The first stage's townReach holds for every later stage, whatever their own goal says.
                        next.TownReach = WorldEventTownReach.Effective(evt.Composition?.Goal?.TownReach, request?.TownReach);
                        next.ExcludeTowns = originTownName != null ? new List<string> { originTownName } : null;

                        // A stage reward rewards.json does not define pays the default instead (flagged at load).
                        if (next.RewardId != null && !Store.Rewards.Keys.Any(k => string.Equals(k, next.RewardId, StringComparison.OrdinalIgnoreCase)))
                            next.RewardId = null;

                        if (TryStart(next, out started, out error))
                        {
                            anchor = spot;
                            label = spotLabel;
                            townName = town;
                            break;
                        }

                        log.Info($"[WORLDEVENT] chain root={root} stage {stage}/{maxLength}: the next stage could not start at {spotLabel} ({error})");
                    }

                    if (started == null)
                    {
                        if (retired && evt.State != WorldEventState.Done)
                        {
                            Current = evt;
                            Previous = null;
                        }

                        log.Warn($"[WORLDEVENT] chain root={root} stage {stage}/{maxLength} run={evt.RunId}: the next stage could not start ({error}); the chain ends");
                        return;
                    }
                }

                // The run may have moved its anchor to open ground (protect.clearArea): name where it really is.
                anchor = started.Composition?.AnchorPosition ?? anchor;
                label = townName != null ? WorldEventTownIndex.DescribeLabeled(townName, anchor) : WorldEventTownIndex.Describe(anchor);

                var family = started.Composition?.FamilyDisplayName;
                var seconds = chain?.CountdownSeconds ?? 120;
                var template = chain?.Announce != null && chain.Announce.Count > 0 ? chain.Announce[chainRng.Next(chain.Announce.Count)] : null;
                var bossName = started.Composition?.Boss?.Kind == BossKind.Named ? started.Composition.Boss.DisplayName : null;
                var line = WorldEventChain.AnnounceLine(template, family, label, seconds, townName, bossName);

                WorldEventAnnouncer.Broadcast(WorldEventAnnouncer.Global(line));

                // The finished stage keeps its cache for its claim window: remind the players there to take it first.
                var reminderTemplate = chain?.ClaimReminder != null && chain.ClaimReminder.Count > 0 ? chain.ClaimReminder[chainRng.Next(chain.ClaimReminder.Count)] : null;
                WorldEventAnnouncer.Local(evt, WorldEventChain.ReminderLine(reminderTemplate, townName ?? label));

                var distance = origin != null ? anchor.DistanceTo(origin) : 0;

                log.Info($"[WORLDEVENT] chain root={root} stage {stage}/{maxLength} run={evt.RunId} succeeded -> stage {stage + 1}/{maxLength} " +
                         $"run={started.RunId} at {label} ({distance:0} m away, {anchor.ToLOCString()}) source={started.Composition?.Source?.Id} " +
                         $"family={string.Join("+", started.Composition?.Families?.Select(f => f?.Id) ?? Enumerable.Empty<string>())} " +
                         $"goal={started.Composition?.Goal?.Id} boss={started.Composition?.Boss?.Id} reward={started.Composition?.Reward?.Id}; " +
                         $"starts in {seconds} s. Broadcast: {line}");
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] chain after run={evt?.RunId} threw; the chain ends", ex);
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
            return TryStart(request, out evt, out error, out _);
        }

        /// <summary>
        /// <see cref="TryStart(WorldEventRequest, out WorldEvent, out string)"/> with the refusal also
        /// reported as a <see cref="WorldEventStartRefusal"/>, so a caller that has to MAP a refusal (the
        /// web Admin panel's error codes) never matches on the human text in <paramref name="error"/>.
        /// <paramref name="runningRunId"/> is the blocking run's id when the refusal is
        /// <see cref="WorldEventStartRefusal.AlreadyRunning"/>, read under the same lock, and 0 otherwise.
        /// </summary>
        public static bool TryStart(WorldEventRequest request, out WorldEvent evt, out string error,
            out WorldEventStartRefusal refusal)
        {
            return TryStart(request, out evt, out error, out refusal, out _);
        }

        public static bool TryStart(WorldEventRequest request, out WorldEvent evt, out string error,
            out WorldEventStartRefusal refusal, out uint runningRunId)
        {
            evt = null;
            error = null;
            refusal = WorldEventStartRefusal.None;
            runningRunId = 0;

            lock (sync)
            {
                ClearCurrentIfDone();

                if (!Enabled)
                {
                    error = "world events are disabled (world_events_enabled)";
                    refusal = WorldEventStartRefusal.Disabled;
                    return false;
                }

                if (Current != null)
                {
                    error = $"an event is already running (run {Current.RunId}, {Current.State})";
                    refusal = WorldEventStartRefusal.AlreadyRunning;
                    runningRunId = Current.RunId;
                    return false;
                }

                if (Store == null || Store.IsEmpty)
                {
                    error = "no axes loaded";
                    refusal = WorldEventStartRefusal.NoAxes;
                    return false;
                }

                // TECH-DESIGN 2.15, ahead of composition because it is a defect in the REQUEST rather than
                // in the axes: a run whose minimum duration meets or exceeds its maximum could only ever
                // end by timing out.
                if (!WorldEvent.ValidateDurations(
                        request?.MinDurationSeconds ?? WorldEvent.DefaultMinDurationSeconds,
                        request?.MaxDurationSeconds ?? WorldEvent.DefaultMaxDurationSeconds, out error))
                {
                    refusal = WorldEventStartRefusal.InvalidDurations;
                    return false;
                }

                if (!TryCompose(request, out var composition, out error))
                {
                    refusal = WorldEventStartRefusal.CompositionFailed;
                    return false;
                }

                // Hold: the hold must be able to finish inside the run, or it could only ever time out.
                if (!WorldEvent.ValidateHoldDuration(composition.Goal,
                        request?.MaxDurationSeconds ?? WorldEvent.DefaultMaxDurationSeconds, out error))
                {
                    refusal = WorldEventStartRefusal.InvalidDurations;
                    return false;
                }

                // Hold protect.clearArea: the knights' ground and every portal's path must be clear of buildings, trees,
                // rocks and other objects. Blocked portals move or are skipped; a blocked anchor moves to the nearest open
                // spot, else the start is refused.
                //
                // goals.json townReach: the spot (after any clear-area move) must be a short walk from a Town Network portal
                // drop. A spot that is not is re-rolled to the edge of the nearest towns, and logged.
                if (!TryResolveStartSpot(composition, request, out var areaPlan, out var movedAnchor, out var rerollLabel, out error))
                {
                    refusal = WorldEventStartRefusal.CompositionFailed;
                    return false;
                }

                if (rerollLabel != null && composition.Anchor != null)
                    composition.Anchor.DisplayName = rerollLabel;

                if (request.DryRun)
                {
                    log.Info($"[WORLDEVENT] dry-run compose {composition.ToAxisSummary()}");
                    return true;
                }

                // The anchor moved to open ground: the composition's position is updated in place, so every reader agrees.
                if (movedAnchor != null)
                {
                    composition.AnchorPosition.LandblockId = movedAnchor.LandblockId;
                    composition.AnchorPosition.Pos = movedAnchor.Pos;
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

                    refusal = WorldEventStartRefusal.ObjectiveFailed;
                    return false;
                }

                candidate.SetObjective(objective);

                if (areaPlan != null)
                    candidate.HoldPortalSpots = areaPlan.Portals;

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

        /// <summary>
        /// Where a run really starts (live: reads the landscape; under <see cref="sync"/>). Two rules, each from goals.json:
        ///   * protect.clearArea (Hold): the knights' ground and the portals' paths are clear, else the nearest open spot
        ///     within its searchRadius;
        ///   * townReach (the goal's, or the one its chain carried): the spot is within maxWalk of a Town Network portal drop.
        /// The asked spot is tried first. When it fails either rule and townReach is set, it is re-rolled to the edge of the
        /// nearest towns (spotMinDistance to spotMaxDistance from each drop, nearest town first), each re-roll checked the
        /// same way and logged. <paramref name="moved"/> is the new spot (null when the asked one stands);
        /// <paramref name="rerollLabel"/> names a re-rolled spot ("Holtburg (42.1N, 33.6E)"), null otherwise.
        /// </summary>
        private static bool TryResolveStartSpot(WorldEventComposition composition, WorldEventRequest request, out AreaPlan areaPlan,
            out Position moved, out string rerollLabel, out string error)
        {
            areaPlan = null;
            moved = null;
            rerollLabel = null;
            error = null;

            var asked = composition?.AnchorPosition;

            if (asked == null)
                return true;

            var goal = composition.Goal;
            var reach = WorldEventTownReach.Effective(goal?.TownReach, request?.TownReach);
            var checksArea = goal?.TypeKind == GoalType.Hold && goal.Protect?.ClearArea != null;
            var towns = WorldEventTownIndex.Towns;
            var tag = (request?.ChainStage ?? 1) > 1 ? $"chain stage {request.ChainStage}" : "start";

            if (reach != null && towns.Count == 0)
            {
                log.Warn($"[WORLDEVENT] {tag} goal={goal?.Id}: townReach is set but the town index is empty; the spot is not checked");
                reach = null;
            }

            var askedGlobal = asked.ToGlobal();
            var rerollTowns = reach != null ? WorldEventTownReach.RerollTowns(towns, askedGlobal.X, askedGlobal.Y, reach, request?.ExcludeTowns) : new List<WorldEventTown>();
            var rng = new Random(ThreadSafeRandom.Next(0, int.MaxValue - 1));
            var failures = new List<string>();

            for (var attempt = 0; attempt <= rerollTowns.Count; attempt++)
            {
                Position spot;
                string town = null;

                if (attempt == 0)
                {
                    spot = asked;

                    if (reach != null && !WorldEventTownReach.IsReachable(spot, towns, reach.MaxWalk))
                    {
                        var where = WorldEventTownReach.Describe(spot, towns);
                        failures.Add($"the asked spot is not reachable from the Town Network ({where})");
                        log.Info($"[WORLDEVENT] {tag} goal={goal?.Id} spot {asked.ToLOCString()} is not reachable from the Town Network " +
                                 $"({where}, more than the {reach.MaxWalk:0} m walk); re-rolling near {string.Join(", ", rerollTowns.Select(t => t.Name))}");
                        continue;
                    }
                }
                else
                {
                    var t = rerollTowns[attempt - 1];
                    town = t.Name;

                    if (!WorldEventChain.TryPickNearTown(t, asked, reach.SpotMinDistance, reach.SpotMaxDistance, rng, out spot))
                    {
                        failures.Add($"no open ground at the edge of {t.Name}");
                        log.Info($"[WORLDEVENT] {tag} goal={goal?.Id} re-roll: no open ground {reach.SpotMinDistance:0} to {reach.SpotMaxDistance:0} m from {t.Name}'s portal drop; trying the next town");
                        continue;
                    }

                    spot.Rotation = asked.Rotation;
                }

                AreaPlan plan = null;
                Position areaMoved = null;

                if (checksArea)
                {
                    var participants = Math.Max(1, WorldEventAudienceSampler.Sample(spot, 60f).Count);

                    if (!WorldEventSpawnArea.TryResolve(spot, goal.Protect, participants, attempt == 0 ? tag : $"{tag} re-roll near {town}",
                            out plan, out areaMoved, out var areaWhy))
                    {
                        if (reach == null)
                        {
                            error = $"no open ground for {goal.Id} here: {areaWhy}. Move to open ground, away from buildings, trees and rocks, and try again";
                            return false;
                        }

                        failures.Add(attempt == 0 ? $"no open ground here ({areaWhy})" : $"no open ground near {town} ({areaWhy})");
                        log.Info($"[WORLDEVENT] {tag} goal={goal?.Id} {(attempt == 0 ? "the asked spot" : $"the re-roll near {town}")} has no open ground ({areaWhy}); " +
                                 (attempt < rerollTowns.Count ? "re-rolling" : "nothing left to try"));
                        continue;
                    }
                }

                var final = areaMoved ?? spot;

                if (reach != null && !WorldEventTownReach.IsReachable(final, towns, reach.MaxWalk))
                {
                    var where = WorldEventTownReach.Describe(final, towns);
                    failures.Add($"the open ground found is not reachable ({where})");
                    log.Info($"[WORLDEVENT] {tag} goal={goal?.Id} the open ground at {final.ToLOCString()} is not reachable from the Town Network ({where}); re-rolling");
                    continue;
                }

                areaPlan = plan;
                moved = attempt == 0 ? areaMoved : final;

                if (reach != null)
                {
                    var where = WorldEventTownReach.Describe(final, towns);

                    if (attempt > 0)
                    {
                        rerollLabel = WorldEventTownIndex.DescribeLabeled(town, final);
                        log.Info($"[WORLDEVENT] {tag} goal={goal?.Id} re-rolled to {rerollLabel} at {final.ToLOCString()} ({where}), " +
                                 $"{final.DistanceTo(asked):0} m from the asked spot");
                    }
                    else
                        log.Info($"[WORLDEVENT] {tag} goal={goal?.Id} spot reachable from the Town Network: {where} (walk up to {reach.MaxWalk:0} m)");
                }

                return true;
            }

            error = $"no spot reachable from the Town Network for {goal?.Id}: {string.Join("; ", failures)}. " +
                    $"Start within {reach?.MaxWalk ?? 0:0} m of a town's portal drop, on open ground";

            log.Warn($"[WORLDEVENT] {tag} goal={goal?.Id} refused: {error}");

            return false;
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

        /// <summary>
        /// Compare-and-stop for a caller that read the run id earlier and must not stop a DIFFERENT run
        /// that started since (the web Admin panel's expected_run_id). The check and the stop share one
        /// hold of <see cref="sync"/>, so nothing can replace the run between them.
        /// <paramref name="runId"/> is the run that was stopped, or on <see cref="WorldEventStopRefusal.RunChanged"/>
        /// the run that is live instead; 0 when nothing is running.
        ///
        /// A run that has already resolved (Resolved, Rewarding or Cleanup) is refused as
        /// <see cref="WorldEventStopRefusal.Finished"/> and left alone: Finish would ignore a second
        /// failed outcome there, and an abort would cut the claim window short and destroy unclaimed caches.
        /// <paramref name="state"/> and <paramref name="finalOutcome"/> are read AFTER the call - on success
        /// they are what the run actually ended as, which is what a caller must report, never the outcome
        /// it asked for.
        /// </summary>
        public static bool TryStopCurrent(uint expectedRunId, WorldEventOutcome outcome, string invoker,
            out WorldEventStopRefusal refusal, out uint runId, out WorldEventState state, out WorldEventOutcome finalOutcome)
        {
            lock (sync)
            {
                ClearCurrentIfDone();

                var evt = Current;

                runId = evt?.RunId ?? 0;
                state = evt?.State ?? WorldEventState.Done;
                finalOutcome = evt?.Outcome ?? WorldEventOutcome.None;

                if (evt == null)
                {
                    refusal = WorldEventStopRefusal.NotRunning;
                    return false;
                }

                if (evt.RunId != expectedRunId)
                {
                    refusal = WorldEventStopRefusal.RunChanged;
                    return false;
                }

                if (evt.State >= WorldEventState.Resolved)
                {
                    refusal = WorldEventStopRefusal.Finished;
                    return false;
                }

                refusal = WorldEventStopRefusal.None;

                StopCurrent(outcome, invoker);

                state = evt.State;
                finalOutcome = evt.Outcome;
                return true;
            }
        }

        /// <summary>Called from the world thread loop as it concedes, before WorldActive goes false.</summary>
        public static void OnShutdown()
        {
            lock (sync)
            {
                if (Previous != null && Previous.State != WorldEventState.Done)
                {
                    try
                    {
                        Previous.Finish(WorldEventOutcome.AbortedShutdown);
                    }
                    catch (Exception ex)
                    {
                        log.Error("[WORLDEVENT] shutdown abort of the finishing chained run threw", ex);
                    }
                }

                Previous = null;

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
        ///
        /// As in <see cref="Initialize"/>, the build runs before <see cref="sync"/> is taken, so a reload
        /// issued from a thread other than the world thread no longer stalls the world tick for the
        /// length of the world-DB reads.
        /// </summary>
        public static string Reload()
        {
            try
            {
                var store = WorldEventAxisStore.LoadFromServerConfig();

                foreach (var diagnostic in store.Diagnostics)
                    log.Warn($"[WORLDEVENT] axis: {diagnostic}");

                var catalog = BuildCatalog(store);
                WorldEventTownIndex.Load();

                lock (sync)
                {
                    Publish(store, catalog);

                    // The species tables above are the Threads population-plan cache's biggest input
                    // (ThreadPlanCache). The swap on the line above already invalidates it implicitly - the
                    // cache keys hold the OLD dictionary by reference, so nothing keyed on it can match the new
                    // one - but those entries would then sit in the cache until they aged out of its bound.
                    // Dropping them here is what makes a reload cost nothing rather than a bound's worth of
                    // dead weight.
                    ACE.Server.ThreadDungeons.ThreadPlanCache.Invalidate();

                    // World Events' own band-standard memo is keyed on the same species-table dictionary and the same
                    // weenie reads; drop it with the swap for the same reason.
                    WorldEventBandMemo.Clear();

                    var running = Current != null && Current.State != WorldEventState.Done
                        ? $" Run {Current.RunId} is in progress and keeps the axis store it staged with."
                        : "";

                    return $"World Events reloaded from {store.ResolvedFolder ?? "no axis folder"}: " +
                           $"{store.Sources.Count} sources, {store.Families.Count} families, {store.Goals.Count} goals, " +
                           $"{store.Rewards.Count} rewards, {store.Anchors.Count} anchors, {store.SpeciesTables.Count} species tables " +
                           $"({store.Diagnostics.Count} axis diagnostics, {store.Notes.Count} notes). " +
                           $"Catalog: {catalog.Families.Count} families, {catalog.MemberCount} members " +
                           $"({catalog.Diagnostics.Count} catalog diagnostics). " +
                           $"Town index: {WorldEventTownIndex.Towns.Count} towns.{running}";
                }
            }
            catch (Exception ex)
            {
                log.Error("[WORLDEVENT] reload failed", ex);
                return $"World Events reload failed: {ex.Message}";
            }
        }

        /// <summary>
        /// One-line status for "/worldevent status". Called from a command handler, i.e. off the world
        /// thread relative to the ledger's own writes. Safe: the ledger locks internally and Records/TopKiller
        /// hand back clones (see WorldEventParticipation's threading remarks), and this method only ever reads.
        /// </summary>
        public static string StatusText()
        {
            var text = StatusTextOfCurrent();
            var previous = Previous;

            if (previous != null && previous.State != WorldEventState.Done)
                text += $" Run {previous.RunId} ({previous.Outcome}) is still in its claim window beside the chained run.";

            var stage = Current?.Request?.ChainStage ?? 1;

            if (stage > 1 && Current.State != WorldEventState.Done)
                text += $" Chain stage {stage}/{Current.Request.ChainMaxLength} (first run {Current.Request.ChainRootRunId}).";

            return text;
        }

        private static string StatusTextOfCurrent()
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
