using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;

using ACE.Database;
using ACE.Entity;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;

using log4net;

namespace ACE.Server.HazardMechanics
{
    /// <summary>
    /// The public API of the hazard mechanics library and its run registry.
    ///
    ///   HazardManager.Start(region, kind, args, host, seed) -> HazardRun (or null with a reason)
    ///   run.Stop(reason) / HazardManager.Stop(run, outcome, reason)
    ///
    /// Start validates everything it can on the CALLER's thread (kill switch, region, kind, pattern, the
    /// mechanic's own Validate, every wcid it needs) and refuses with one line instead of throwing; then it
    /// registers the run and makes sure the region's landblock has a <see cref="HazardDriver"/>. The mechanic's
    /// Start runs on the driver's first tick, on the landblock thread. A spanning region (HazardRegion.ArenaCircle)
    /// is hosted on its ANCHOR landblock's driver and refused unless every landblock it covers is already loaded
    /// (<see cref="SpanRefusal"/>); see HazardSpan for which neighbours a tick may touch.
    ///
    /// STOP IS IDEMPOTENT and OnEnded fires EXACTLY ONCE, outside every lock (HazardRun.TryClaimEnd is the one
    /// gate). The teardown (mechanic OnStop, destroy every owned object) is queued onto the run's landblock. If the
    /// landblock is already gone the queued action never runs. For a SINGLE-landblock run nothing is lost, because an
    /// unloaded landblock already destroyed every non-persisted object (IsTransientSpawn objects never persist). A
    /// SPANNING run can own objects on a neighbour that is still loaded, and the anchor's unload does not touch them
    /// (TimeToRot -1: they would live forever). So every spanning run's teardown is tracked until it runs
    /// (<see cref="SweepPendingTeardowns"/>, from Stop and every reap): when the anchor is dead, or the queued action
    /// was dropped (still unclaimed <see cref="HazardDefaults.ReapStaleSeconds"/> after Stop, e.g. /reload-landblock
    /// cleared the queue), HazardContext.RouteOrphanedTeardown queues a destroy for each owned object on that
    /// object's own landblock.
    ///
    /// REAPING. A landblock UNLOAD always kills the driver chain: Landblock.Unload clears the action queue and the
    /// unloaded copy is never ticked again, so the next tick (sitting in the DelayManager at the time) is enqueued
    /// onto a queue nobody runs. /reload-landblock (Landblock.DestroyAllNonPlayerObjects) also clears the queue but
    /// the landblock object survives and keeps ticking, so it kills the chain only in the narrow window where the
    /// driver's tick action is sitting in that queue; usually the run survives with its objects destroyed under it
    /// (mechanics must check HazardHandle.Alive). <see cref="Reap"/> ends as Reaped every run whose landblock is no
    /// longer the live copy, or whose driver has not ticked for ReapStaleSeconds. It runs on every Start and every
    /// /hazard command AND on one global self-requeuing chain on the world thread (every ReaperSeconds, started by
    /// the first Start, ending when the registry is empty), so a host's OnEnded(Reaped) always fires.
    /// </summary>
    public static class HazardManager
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private static readonly object RegistryLock = new object();
        private static readonly Dictionary<int, HazardRun> Runs = new Dictionary<int, HazardRun>();

        private static int nextId;

        // =============================================================================================
        // Start
        // =============================================================================================

        /// <summary>The API seam for world events and boss scripts. Null when refused; the reason is logged.</summary>
        public static HazardRun Start(HazardRegion region, string kind, HazardArgs args, IHazardHost host, int? seed)
        {
            var run = Start(region, kind, args, host, seed, null, null, out var error);

            if (run == null)
                log.Warn($"[HAZARD] start of '{kind}' refused: {error}");

            return run;
        }

        /// <summary>
        /// Starts <paramref name="kind"/> on <paramref name="region"/>. <paramref name="args"/> are laid OVER the
        /// mechanic's settings string (args win). <paramref name="pattern"/> null: args "pattern", else the
        /// mechanic's default. <paramref name="landblock"/> null: the region's live landblock is looked up (it must
        /// already be loaded; a run never loads a landblock). Null with <paramref name="error"/> when refused.
        /// </summary>
        public static HazardRun Start(HazardRegion region, string kind, HazardArgs args, IHazardHost host, int? seed, string pattern,
            Landblock landblock, out string error)
            => Start(region, kind, args, host, seed, pattern, landblock, null, out error);

        /// <summary>
        /// As above, with the settings layer the caller args are laid over supplied by the caller instead of read
        /// from hazard_mech_&lt;kind&gt; (world events carry their own tuning). Null <paramref name="settingsLayer"/>:
        /// the live hazard_mech_&lt;kind&gt; row, exactly as before.
        /// </summary>
        public static HazardRun Start(HazardRegion region, string kind, HazardArgs args, IHazardHost host, int? seed, string pattern,
            Landblock landblock, HazardArgs settingsLayer, out string error)
            => Start(region, kind, args, host, seed, pattern, landblock, settingsLayer, null, out error);

        /// <summary>
        /// As above, naming where <paramref name="settingsLayer"/> came from (e.g. "world_events_hazard_wisps") so a
        /// refusal caused by a key in that layer points at the right setting. Null <paramref name="settingsLayerName"/>:
        /// the hint names hazard_mech_&lt;kind&gt;, exactly as before.
        /// </summary>
        public static HazardRun Start(HazardRegion region, string kind, HazardArgs args, IHazardHost host, int? seed, string pattern,
            Landblock landblock, HazardArgs settingsLayer, string settingsLayerName, out string error)
        {
            Reap();

            if (!HazardTunables.Enabled)
            {
                error = "hazard mechanics are switched off (hazard_mech_enabled is false).";
                return null;
            }

            if (region == null)
            {
                error = "no region.";
                return null;
            }

            error = region.Validate();

            if (error != null)
                return null;

            if (!HazardMechanicRegistry.TryCreate(kind, out var mechanic))
            {
                error = $"unknown hazard '{kind}'. Known: {string.Join(", ", HazardMechanicRegistry.Kinds)}.";
                return null;
            }

            landblock ??= FindLiveLandblock(region);

            if (landblock == null)
            {
                error = $"landblock 0x{region.Landblock:X4} instance 0x{region.Instance:X8} is not loaded; a hazard run never loads a landblock.";
                return null;
            }

            if (landblock.Id.Landblock != region.Landblock || landblock.Instance != region.Instance)
            {
                error = $"region is on 0x{region.Landblock:X4}/0x{region.Instance:X8} but the landblock given is 0x{landblock.Id.Landblock:X4}/0x{landblock.Instance:X8}.";
                return null;
            }

            if (landblock.IsDungeon == region.Outdoor)
            {
                error = region.Outdoor
                    ? $"region is marked outdoor but landblock 0x{region.Landblock:X4} is a dungeon."
                    : $"region is marked as a dungeon but landblock 0x{region.Landblock:X4} is outdoors.";
                return null;
            }

            error = SpanRefusal(region, (id, instance) => FindLiveLandblock(id, instance));

            if (error != null)
                return null;

            var merged = HazardArgs.Merge(args, SettingsLayerFor(kind, settingsLayer));

            if (!TryResolvePattern(mechanic, pattern ?? merged.GetString("pattern"), out var resolvedPattern, out error))
                return null;

            error = ValidateWithHint(mechanic, region, args, merged, resolvedPattern, settingsLayerName);

            if (error != null)
                return null;

            error = MissingWeenies(mechanic.RequiredWcids(merged));

            if (error != null)
                return null;

            var run = new HazardRun(Interlocked.Increment(ref nextId), mechanic.Kind, resolvedPattern, region, merged,
                seed ?? Random.Shared.Next(1, int.MaxValue), host, mechanic, landblock, DateTime.UtcNow);

            lock (RegistryLock)
                Runs[run.Id] = run;

            HazardDriver.Ensure(landblock);
            EnsureReaper();

            log.Info($"{run.LogPrefix} queued pattern={run.Pattern ?? "-"} seed={run.Seed} region=[{region.Describe()}] args=[{merged}]");

            return run;
        }

        /// <summary>
        /// Null when <paramref name="region"/> may start as far as its neighbours go: always for a single-landblock
        /// region. A spanning region needs a cover (HazardSpan.TryCover: inside the 2 x 2 around its anchor, the
        /// anchor covered, nothing past the world edge) and EVERY covered neighbour already loaded and outdoors -
        /// a run never loads a landblock, so an unloaded one is a refusal, not a load. <paramref name="findLoaded"/>
        /// must never load (live: <see cref="FindLiveLandblock(ushort, uint)"/>). Whether a loaded neighbour is on the
        /// anchor's tick thread is checked per tick, not here (a just-loaded landblock has no group yet).
        /// </summary>
        internal static string SpanRefusal(HazardRegion region, Func<ushort, uint, Landblock> findLoaded)
        {
            if (region == null || !region.SpansLandblocks)
                return null;

            if (!HazardSpan.TryCover(region, out var cover, out var error))
                return error;

            foreach (var (id, dx, dy) in cover)
            {
                if (dx == 0 && dy == 0)
                    continue;

                var lb = findLoaded?.Invoke(id, region.Instance);

                if (lb == null)
                    return $"landblock 0x{id:X4} instance 0x{region.Instance:X8} (covered by the region around anchor 0x{region.Landblock:X4}) is not loaded; a hazard run never loads a landblock.";

                if (lb.IsDungeon)
                    return $"landblock 0x{id:X4} (covered by the region around anchor 0x{region.Landblock:X4}) is a dungeon; a spanning region is outdoors only.";
            }

            return null;
        }

        /// <summary>The loaded copy of landblock <paramref name="landblockId"/> on <paramref name="instance"/>, or null. Never loads one.</summary>
        internal static Landblock FindLiveLandblock(ushort landblockId, uint instance)
            => LandblockManager.GetLoadedLandblock(new LandblockId(((uint)landblockId << 16) | 0xFFFF), instance);

        /// <summary>The layer caller args are merged over: the caller's own layer as-is (no PropertyManager read), else the live settings row.</summary>
        internal static HazardArgs SettingsLayerFor(string kind, HazardArgs settingsLayer)
            => settingsLayer ?? HazardTunables.SettingsFor(kind);

        /// <summary>
        /// A typo in a hazard_mech_&lt;kind&gt; settings row refuses EVERY run, and the refusal names a key the caller
        /// never typed. Every key that the merged args hold, the CALLER's args do not, and the error names (outside its
        /// quoted values, so "dir='speed'" does not name speed) is listed:
        /// " (len, gap from setting hazard_mech_&lt;kind&gt;)". <paramref name="settingName"/>, when given, replaces
        /// "hazard_mech_&lt;kind&gt;" (a caller-supplied settings layer, e.g. world_events_hazard_&lt;kind&gt;). Pure.
        /// </summary>
        internal static string WithSettingHint(string error, string kind, HazardArgs callerArgs, HazardArgs merged, string settingName = null)
        {
            if (string.IsNullOrEmpty(error) || merged == null)
                return error;

            var named = System.Text.RegularExpressions.Regex.Replace(error, "'[^']*'", "''");
            var keys = new List<string>();

            foreach (var key in merged.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                if (callerArgs != null && callerArgs.Has(key))
                    continue;

                // a one-letter key (n) only counts when the message spells it key=
                var pattern = key.Length >= 2
                    ? "(?<![A-Za-z_])" + System.Text.RegularExpressions.Regex.Escape(key) + "(?![A-Za-z_])"
                    : "(?<![A-Za-z_])" + System.Text.RegularExpressions.Regex.Escape(key) + "=";

                if (System.Text.RegularExpressions.Regex.IsMatch(named, pattern))
                    keys.Add(key);
            }

            var source = string.IsNullOrWhiteSpace(settingName) ? $"hazard_mech_{kind}" : settingName.Trim();

            return keys.Count == 0 ? error : $"{error} ({string.Join(", ", keys)} from setting {source})";
        }

        /// <summary>The mechanic's own Validate, with the settings-row hint added. <paramref name="args"/> are the CALLER's args, <paramref name="merged"/> the settings row under them; <paramref name="settingName"/> names that row (null: hazard_mech_&lt;kind&gt;).</summary>
        internal static string ValidateWithHint(IHazardMechanic mechanic, HazardRegion region, HazardArgs args, HazardArgs merged, string pattern, string settingName = null)
            => WithSettingHint(mechanic.Validate(region, merged, pattern), mechanic.Kind, args, merged, settingName);
        /// <summary>A pattern from the mechanic's list (case-insensitive), its default when none was asked for.</summary>
        internal static bool TryResolvePattern(IHazardMechanic mechanic, string asked, out string pattern, out string error)
        {
            pattern = null;
            error = null;

            var patterns = mechanic.Patterns ?? Array.Empty<string>();

            if (patterns.Count == 0)
            {
                if (!string.IsNullOrWhiteSpace(asked))
                {
                    error = $"'{mechanic.Kind}' takes no pattern (got '{asked}').";
                    return false;
                }

                return true;
            }

            if (string.IsNullOrWhiteSpace(asked))
            {
                pattern = patterns[0];
                return true;
            }

            foreach (var p in patterns)
            {
                if (string.Equals(p, asked.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    pattern = p;
                    return true;
                }
            }

            error = $"'{mechanic.Kind}' has no pattern '{asked}'. Patterns: {string.Join(", ", patterns)}.";
            return false;
        }

        /// <summary>Null when every wcid has a weenie; otherwise the refusal line (the command says it, nothing throws).</summary>
        private static string MissingWeenies(IEnumerable<uint> wcids)
        {
            if (wcids == null)
                return null;

            var missing = wcids.Distinct().Where(w => DatabaseManager.World.GetCachedWeenie(w) == null).ToList();

            if (missing.Count == 0)
                return null;

            // Name the content unit for each kind of wcid that is actually missing (not one hard-coded range).
            var hints = new List<string>();

            if (missing.Any(w => w >= 1006950 && w <= 1006999))
                hints.Add("hazard-mechanics content (Content/sql/weenies 1006950-1006999)");

            if (missing.Any(w => w >= 1006800 && w <= 1006803))
                hints.Add("Hill Marker content (owner pvp-battlegrounds, 1006800-1006803)");

            return "missing weenie(s) in the world database: " + string.Join(", ", missing)
                + (hints.Count > 0 ? ". Apply the " + string.Join(" and the ", hints) + " first." : ".");
        }

        // =============================================================================================
        // Stop
        // =============================================================================================

        /// <summary>
        /// Ends <paramref name="run"/>. Idempotent: only the FIRST call does anything and returns true. Queues the
        /// teardown on the run's landblock, then fires the host's OnEnded exactly once, outside every lock.
        /// </summary>
        public static bool Stop(HazardRun run, HazardOutcome outcome, string reason)
        {
            if (run == null)
                return false;

            if (!run.TryClaimEnd(outcome, reason, out var wasStarted))
                return false;

            lock (RegistryLock)
            {
                if (Runs.TryGetValue(run.Id, out var live) && ReferenceEquals(live, run))
                    Runs.Remove(run.Id);
            }

            log.Info($"{run.LogPrefix} ended outcome={outcome} reason={reason} t={run.Context.Now:0.0}s");

            var landblock = run.Landblock;

            if (landblock != null)
            {
                landblock.EnqueueAction(new ActionEventDelegate(() => run.Context.Teardown(outcome, wasStarted)));

                // A spanning run may own objects on a neighbour the anchor's unload never destroys: track its
                // teardown until it runs, and route it at once if the anchor is already dead.
                if (run.Region != null && run.Region.SpansLandblocks)
                {
                    lock (RegistryLock)
                        PendingTeardowns[run.Id] = (run, DateTime.UtcNow);

                    SweepPendingTeardowns(DateTime.UtcNow, IsLandblockLive);
                    EnsureReaper();
                }

                // The last run off this landblock: drop its driver entry now, so a landblock that unloaded under
                // the run (whose driver can never tick again to remove itself) is not held by the map forever.
                if (!HasRunsOn(landblock))
                    HazardDriver.Forget(landblock);
            }

            try
            {
                run.Host.OnEnded(run, outcome);
            }
            catch (Exception ex)
            {
                log.Error($"{run.LogPrefix} host OnEnded threw", ex);
            }

            return true;
        }

        /// <summary>Stops every run matching <paramref name="match"/> (all when null). Returns how many this call ended.</summary>
        public static int StopAll(Func<HazardRun, bool> match, HazardOutcome outcome, string reason)
        {
            var stopped = 0;

            foreach (var run in Snapshot())
                if ((match == null || match(run)) && Stop(run, outcome, reason))
                    stopped++;

            return stopped;
        }

        // =============================================================================================
        // Registry
        // =============================================================================================

        public static List<HazardRun> List()
        {
            Reap();
            return Snapshot().OrderBy(r => r.Id).ToList();
        }

        public static HazardRun Find(int id)
        {
            lock (RegistryLock)
                return Runs.TryGetValue(id, out var run) ? run : null;
        }

        internal static List<HazardRun> Snapshot()
        {
            lock (RegistryLock)
                return Runs.Values.ToList();
        }

        internal static List<HazardRun> RunsOn(Landblock landblock)
        {
            lock (RegistryLock)
                return Runs.Values.Where(r => ReferenceEquals(r.Landblock, landblock)).OrderBy(r => r.Id).ToList();
        }

        internal static bool HasRunsOn(Landblock landblock)
        {
            lock (RegistryLock)
                return Runs.Values.Any(r => ReferenceEquals(r.Landblock, landblock));
        }

        /// <summary>
        /// Ends (Reaped) every run whose landblock is no longer the live copy or whose driver has not ticked for
        /// ReapStaleSeconds. Returns how many. Called on every Start and every /hazard command.
        /// </summary>
        public static int Reap() => Reap(DateTime.UtcNow, IsLandblockLive);

        /// <summary>The reaper with its two inputs injected (tests).</summary>
        internal static int Reap(DateTime nowUtc, Func<Landblock, bool> isLive)
        {
            var reaped = 0;

            foreach (var run in Snapshot())
            {
                var reason = ReapReason(run, nowUtc, isLive);

                if (reason != null && Stop(run, HazardOutcome.Reaped, reason))
                    reaped++;
            }

            // and every driver entry whose landblock is dead, runs or not
            HazardDriver.Prune(isLive);

            // and every spanning run's teardown that can no longer run on its anchor
            SweepPendingTeardowns(nowUtc, isLive);

            return reaped;
        }

        /// <summary>Spanning runs that ended but whose queued Teardown has not run yet, with when they were stopped.</summary>
        private static readonly Dictionary<int, (HazardRun Run, DateTime StoppedUtc)> PendingTeardowns = new Dictionary<int, (HazardRun, DateTime)>();

        /// <summary>
        /// PURE: must an ended spanning run's teardown be routed off its anchor now? Not when the queued Teardown
        /// already ran (<paramref name="claimed"/>). Yes when the anchor is dead (its queue never runs again), or when
        /// it is still unclaimed more than <see cref="HazardDefaults.ReapStaleSeconds"/> after Stop on a live anchor
        /// (the queued action was dropped: /reload-landblock clears the queue but keeps the landblock).
        /// </summary>
        internal static bool OrphanTeardownDue(bool claimed, bool anchorLive, double secondsSinceStop)
            => !claimed && (!anchorLive || secondsSinceStop > HazardDefaults.ReapStaleSeconds);

        /// <summary>
        /// Drops every tracked teardown that ran, and routes (HazardContext.RouteOrphanedTeardown) every one
        /// <see cref="OrphanTeardownDue"/> says can no longer run on its anchor. Returns how many it routed. Safe off
        /// the anchor's thread: an ended run is never ticked again, so nothing else touches its owned list, and the
        /// context's claim keeps the route and a late Teardown from both running.
        /// </summary>
        internal static int SweepPendingTeardowns(DateTime nowUtc, Func<Landblock, bool> isLive)
        {
            List<(HazardRun Run, DateTime StoppedUtc)> pending;

            lock (RegistryLock)
            {
                if (PendingTeardowns.Count == 0)
                    return 0;

                pending = PendingTeardowns.Values.ToList();
            }

            var routed = 0;

            foreach (var (run, stoppedUtc) in pending)
            {
                var claimed = run.Context.TeardownClaimed;
                var live = isLive == null || isLive(run.Landblock);

                if (!claimed && !OrphanTeardownDue(false, live, (nowUtc - stoppedUtc).TotalSeconds))
                    continue;

                if (!claimed)
                {
                    try
                    {
                        run.Context.RouteOrphanedTeardown();
                        routed++;
                    }
                    catch (Exception ex)
                    {
                        log.Error($"{run.LogPrefix} routing the orphaned teardown threw", ex);
                    }
                }

                lock (RegistryLock)
                {
                    if (PendingTeardowns.TryGetValue(run.Id, out var e) && ReferenceEquals(e.Run, run))
                        PendingTeardowns.Remove(run.Id);
                }
            }

            return routed;
        }

        /// <summary>Test seam: track <paramref name="run"/>'s teardown as Stop does for a spanning run.</summary>
        internal static void TrackTeardownForTest(HazardRun run, DateTime stoppedUtc)
        {
            lock (RegistryLock)
                PendingTeardowns[run.Id] = (run, stoppedUtc);
        }

        internal static bool IsTeardownPendingForTest(HazardRun run)
        {
            lock (RegistryLock)
                return run != null && PendingTeardowns.TryGetValue(run.Id, out var e) && ReferenceEquals(e.Run, run);
        }

        /// <summary>Pure: why <paramref name="run"/> is dead, or null when it is alive.</summary>
        internal static string ReapReason(HazardRun run, DateTime nowUtc, Func<Landblock, bool> isLive)
        {
            if (run == null || run.IsEnded)
                return null;

            if (isLive != null && !isLive(run.Landblock))
                return "landblock unloaded";

            var last = run.LastTickUtc ?? run.QueuedUtc;

            if ((nowUtc - last).TotalSeconds > HazardDefaults.ReapStaleSeconds)
                return "driver stopped ticking (landblock reloaded, unloaded or dormant)";

            return null;
        }

        /// <summary>
        /// Is this landblock copy still the live one? Ephemeral: the instance registry still maps to this object
        /// (PvpLiveAdapters' check). Permanent: the landblock id is still loaded for the instance.
        /// </summary>
        internal static bool IsLandblockLive(Landblock landblock)
        {
            if (landblock == null)
                return false;

            if (landblock.IsEphemeral)
                return ReferenceEquals(LandblockManager.GetEphemeralLandblock(landblock.Instance), landblock);

            // The SAME object, not merely the same id: an unload and reload yields a new Landblock for the same key,
            // and runs bound to the old one are dead. GetLoadedLandblock never loads (GetLandblock would).
            return ReferenceEquals(LandblockManager.GetLoadedLandblock(landblock.Id, landblock.Instance), landblock);
        }

        /// <summary>The loaded landblock copy the region names, or null. Never loads one.</summary>
        internal static Landblock FindLiveLandblock(HazardRegion region)
        {
            if (region == null)
                return null;

            Position.ParseInstanceID(region.Instance, out var ephemeral, out _, out _);

            if (ephemeral)
            {
                var lb = LandblockManager.GetEphemeralLandblock(region.Instance);
                return lb != null && lb.Id.Landblock == region.Landblock ? lb : null;
            }

            var id = new LandblockId(((uint)region.Landblock << 16) | 0xFFFF);

            return LandblockManager.GetLoadedLandblock(id, region.Instance);
        }

        // =============================================================================================
        // The global reaper (world thread)
        // =============================================================================================

        private static int reaperRunning;

        /// <summary>Starts the global reap chain if it is not running. Called after every successful Start.</summary>
        private static void EnsureReaper()
        {
            if (Interlocked.CompareExchange(ref reaperRunning, 1, 0) == 0)
                QueueReaper();
        }

        private static void QueueReaper()
        {
            var chain = new ActionChain();
            chain.AddDelaySeconds(HazardDefaults.ReaperSeconds);
            chain.AddAction(WorldManager.ActionQueue, ReaperTick);
            chain.EnqueueChain();
        }

        private static void ReaperTick()
        {
            try
            {
                Reap();
            }
            catch (Exception ex)
            {
                log.Error("[HAZARD] reaper threw", ex);
            }

            // The "stop" decision and every registration share RegistryLock, so a Start racing this either is seen
            // here (the chain continues) or finds reaperRunning already 0 and starts a new chain.
            lock (RegistryLock)
            {
                // a tracked spanning teardown keeps the reaper alive until it ran or was routed
                if (Runs.Count == 0 && PendingTeardowns.Count == 0)
                {
                    Volatile.Write(ref reaperRunning, 0);
                    return;
                }
            }

            QueueReaper();
        }

        // =============================================================================================
        // Driver side (landblock thread)
        // =============================================================================================

        /// <summary>
        /// One driver tick for one run: Start on the first tick, then advance motion, run due Schedule steps, and
        /// Tick. Any exception from the mechanic ends the run as Aborted (logged with the run prefix).
        /// </summary>
        internal static void TickRun(HazardRun run, DateTime nowUtc)
        {
            if (run == null || run.IsEnded)
                return;

            var ctx = run.Context;
            run.LastTickUtc = nowUtc;
            ctx.BeginTick();

            try
            {
                if (run.State == HazardRunState.Pending)
                {
                    if (!run.TryMarkRunning())
                        return;

                    ctx.SetNow(0.0);
                    ctx.Log($"start pattern={run.Pattern ?? "-"} seed={run.Seed}");
                    run.Mechanic.Start(ctx);
                    return;
                }

                ctx.SetNow(HazardRun.MonotonicSeconds - run.StartedAtSeconds);

                ctx.AdvanceMotion();

                if (run.IsEnded)
                    return;

                ctx.RunDue();

                if (run.IsEnded)
                    return;

                run.Mechanic.Tick(ctx);
            }
            catch (Exception ex)
            {
                log.Error($"{run.LogPrefix} mechanic threw", ex);
                Stop(run, HazardOutcome.Aborted, "mechanic threw " + ex.GetType().Name);
            }
        }

        // =============================================================================================
        // Test seams
        // =============================================================================================

        /// <summary>
        /// Registers a run with no landblock and no driver (tests have no world). <paramref name="pattern"/> and
        /// <paramref name="args"/> are the run's as Start would resolve them (none by default).
        /// </summary>
        internal static HazardRun RegisterForTest(string kind, IHazardMechanic mechanic, HazardRegion region, IHazardHost host, DateTime queuedUtc,
            string pattern = null, HazardArgs args = null)
        {
            var run = new HazardRun(Interlocked.Increment(ref nextId), kind, pattern, region, args ?? HazardArgs.Empty, 1, host, mechanic, null, queuedUtc);

            lock (RegistryLock)
                Runs[run.Id] = run;

            return run;
        }

        internal static bool IsRegistered(HazardRun run)
        {
            lock (RegistryLock)
                return run != null && Runs.TryGetValue(run.Id, out var live) && ReferenceEquals(live, run);
        }
    }
}
