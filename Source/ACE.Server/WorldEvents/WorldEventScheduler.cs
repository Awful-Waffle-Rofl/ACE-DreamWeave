using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using log4net;

using ACE.Server.Managers;
using ACE.Server.WorldEvents.Defs;

namespace ACE.Server.WorldEvents
{
    /// <summary>How a scheduler start attempt ended.</summary>
    public enum WorldEventSchedulerStart
    {
        Started,

        /// <summary>An event is running (another start won the race): wait, do not count the attempt.</summary>
        Busy,

        Refused
    }

    /// <summary>The last thing the scheduler did with a slot occurrence, for /worldevent status.</summary>
    public sealed class WorldEventSchedulerOutcome
    {
        public bool Started { get; init; }
        public string SlotKey { get; init; }
        public string Anchor { get; init; }
        public uint RunId { get; init; }
        public string Reason { get; init; }
        public DateTime AtUtc { get; init; }
    }

    /// <summary>
    /// Everything the scheduler touches outside itself, as delegates, so the rules run under a unit test with no
    /// world, session or PropertyManager. <see cref="WorldEventScheduler.CreateLive"/> is the production wiring.
    /// </summary>
    public sealed class WorldEventSchedulerSeams
    {
        public Func<DateTime> UtcNow { get; set; }

        /// <summary>world_events_enabled AND world_events_auto_enabled.</summary>
        public Func<bool> Enabled { get; set; }

        public Func<string> ScheduleText { get; set; }
        public Func<string> DefaultZoneId { get; set; }
        public Func<long> LateMinutes { get; set; }

        /// <summary>Zone id to zone; never null (the live one falls back to a fixed UTC-8 offset).</summary>
        public Func<string, TimeZoneInfo> ResolveZone { get; set; }

        public Func<bool> ShutdownPending { get; set; }
        public Func<bool> EventRunning { get; set; }
        public Func<IReadOnlyList<AnchorDef>> Anchors { get; set; }

        /// <summary>Builds the random-start request for an anchor (the admin web path), or a refusal sentence.</summary>
        public Func<AnchorDef, (WorldEventRequest Request, string Refusal)> BuildRequest { get; set; }

        /// <summary>The ClearanceRadius of a source id, or null when the id is unknown.</summary>
        public Func<string, float?> ClearanceOf { get; set; }

        public Func<WorldEventRequest, (WorldEventSchedulerStart Result, uint RunId, string Error)> Start { get; set; }

        /// <summary>Random.Next(maxExclusive).</summary>
        public Func<int, int> NextInt { get; set; }

        public Action<string> LogInfo { get; set; }
        public Action<string> LogWarn { get; set; }

        /// <summary>The Audit channel line writer (actor label, message); optional.</summary>
        public Action<string, string> Audit { get; set; }
    }

    /// <summary>
    /// The World Events daily scheduler. Ticked once a second from <see cref="WorldEventManager.Tick"/>; fires each due
    /// slot occurrence at most once through the same random-start path the web admin page uses.
    /// </summary>
    public sealed class WorldEventScheduler
    {
        public const string LogTag = "[WORLDEVENT-SCHED]";
        public const string Invoker = "scheduler";

        /// <summary>How many anchors one occurrence tries before it gives up.</summary>
        public const int MaxAnchorAttempts = 3;

        /// <summary>How many of the most recent anchors a new pick avoids.</summary>
        public const int RecentAnchorMemory = 3;

        private static readonly TimeSpan KeyRetention = TimeSpan.FromDays(2);

        private readonly WorldEventSchedulerSeams seams;
        private readonly object gate = new object();

        // key -> occurrence start, for every occurrence already fired or skipped.
        private readonly Dictionary<string, DateTime> resolved = new Dictionary<string, DateTime>();

        // key -> occurrence, blocked on a running event and still inside its late window.
        private readonly Dictionary<string, WorldEventOccurrence> waiting = new Dictionary<string, WorldEventOccurrence>();

        private readonly List<string> recentAnchors = new List<string>();
        private readonly HashSet<string> warnedMalformed = new HashSet<string>();

        private string parsedFor;
        private List<WorldEventSlot> parsedSlots = new List<WorldEventSlot>();

        public WorldEventSchedulerOutcome LastOutcome { get; private set; }

        /// <summary>How many fired or skipped occurrence keys are held (pruned after two days). For tests.</summary>
        internal int ResolvedCount
        {
            get { lock (gate) return resolved.Count; }
        }

        public WorldEventScheduler(WorldEventSchedulerSeams seams)
        {
            this.seams = seams;
        }

        /// <summary>Anchors picked most recently, newest last.</summary>
        public IReadOnlyList<string> RecentAnchors
        {
            get { lock (gate) return recentAnchors.ToList(); }
        }

        /// <summary>
        /// One second of scheduler time. Never throws.
        /// </summary>
        public void Tick()
        {
            try
            {
                lock (gate)
                    TickLocked();
            }
            catch (Exception ex)
            {
                seams.LogWarn?.Invoke($"{LogTag} tick threw: {ex}");
            }
        }

        private void TickLocked()
        {
            if (!seams.Enabled())
                return;

            var now = seams.UtcNow();
            var late = WorldEventSchedule.ClampLateMinutes(seams.LateMinutes());
            var slots = GetSlots();

            Prune(now);

            var minute = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);

            foreach (var pending in waiting.Values.ToList())
            {
                if (minute > pending.LateUntilUtc(late))
                    Skip(pending, "event running", now);
            }

            foreach (var occurrence in WorldEventSchedule.Due(slots, now, late))
            {
                if (resolved.ContainsKey(occurrence.Key))
                    continue;

                if (seams.ShutdownPending())
                {
                    Skip(occurrence, "shutdown pending", now);
                    continue;
                }

                if (seams.EventRunning())
                {
                    waiting[occurrence.Key] = occurrence;
                    continue;
                }

                try
                {
                    TryFire(occurrence, now);
                }
                catch (Exception ex)
                {
                    // A seam that throws must not be retried every second for the whole late window, and a throw after the
                    // start took hold must not let a second event fire: the occurrence is over.
                    seams.LogWarn?.Invoke($"{LogTag} slot {occurrence.Key} threw; skipping it: {ex}");
                    Skip(occurrence, "error: " + ex.Message, now);
                }
            }
        }

        private List<WorldEventSlot> GetSlots()
        {
            var text = seams.ScheduleText() ?? string.Empty;
            var zone = seams.DefaultZoneId() ?? string.Empty;
            var signature = zone + "\n" + text;

            if (signature == parsedFor)
                return parsedSlots;

            var malformed = new List<string>();
            parsedSlots = WorldEventSchedule.Parse(text, zone, seams.ResolveZone, malformed);
            parsedFor = signature;

            foreach (var entry in malformed)
                if (warnedMalformed.Add(entry))
                    seams.LogWarn?.Invoke($"{LogTag} world_events_schedule entry '{entry}' is not HH:mm or HH:mm <time zone id> (24 hour); skipped");

            return parsedSlots;
        }

        private void Prune(DateTime now)
        {
            foreach (var key in resolved.Where(kv => now - kv.Value > KeyRetention).Select(kv => kv.Key).ToList())
                resolved.Remove(key);

            foreach (var key in waiting.Where(kv => now - kv.Value.StartUtc > KeyRetention).Select(kv => kv.Key).ToList())
                waiting.Remove(key);
        }

        private void TryFire(WorldEventOccurrence occurrence, DateTime now)
        {
            var anchors = (seams.Anchors() ?? new List<AnchorDef>()).Where(a => a != null && a.MaxRadius > 0).ToList();

            if (anchors.Count == 0)
            {
                Skip(occurrence, "no anchor can host a source (anchors.json has none with maxRadius above 0)", now);
                return;
            }

            var candidates = anchors.Where(a => !recentAnchors.Contains(a.Id)).ToList();

            if (candidates.Count == 0)
                candidates = anchors;

            candidates = Shuffle(candidates);

            string lastRefusal = null;

            foreach (var anchor in candidates.Take(MaxAnchorAttempts))
            {
                var (request, refusal) = seams.BuildRequest(anchor);

                if (request == null)
                {
                    lastRefusal = $"{anchor.DisplayName}: {refusal}";
                    continue;
                }

                var fitting = WorldEventAnchorRules.NarrowSources(request.AllowedSourceIds, anchor.MaxRadius, seams.ClearanceOf);

                if (fitting.Count == 0)
                {
                    lastRefusal = $"{anchor.DisplayName}: no source clears its max radius {anchor.MaxRadius:0.##}";
                    continue;
                }

                request.AllowedSourceIds = fitting;

                var (result, runId, error) = seams.Start(request);

                if (result == WorldEventSchedulerStart.Busy)
                {
                    waiting[occurrence.Key] = occurrence;
                    return;
                }

                if (result == WorldEventSchedulerStart.Started)
                {
                    waiting.Remove(occurrence.Key);
                    resolved[occurrence.Key] = occurrence.StartUtc;
                    recentAnchors.Add(anchor.Id);

                    while (recentAnchors.Count > RecentAnchorMemory)
                        recentAnchors.RemoveAt(0);

                    LastOutcome = new WorldEventSchedulerOutcome
                    {
                        Started = true, SlotKey = occurrence.Key, Anchor = anchor.DisplayName, RunId = runId, AtUtc = now
                    };

                    seams.LogInfo?.Invoke($"{LogTag} slot {occurrence.Key} started run {runId} at {anchor.DisplayName} ({anchor.Id})");

                    try
                    {
                        seams.Audit?.Invoke(Invoker,
                            $"[WORLDEVENT] started world event run {runId} at {anchor.DisplayName}, from the daily scheduler (slot {occurrence.Key})");
                    }
                    catch (Exception ex)
                    {
                        seams.LogWarn?.Invoke($"{LogTag} run {runId} audit line failed: {ex.Message}");
                    }

                    return;
                }

                lastRefusal = $"{anchor.DisplayName}: {error}";
            }

            Skip(occurrence, $"{MaxAnchorAttempts} anchors refused (last: {lastRefusal ?? "none tried"})", now);
        }

        private void Skip(WorldEventOccurrence occurrence, string reason, DateTime now)
        {
            waiting.Remove(occurrence.Key);
            resolved[occurrence.Key] = occurrence.StartUtc;

            LastOutcome = new WorldEventSchedulerOutcome { Started = false, SlotKey = occurrence.Key, Reason = reason, AtUtc = now };

            seams.LogInfo?.Invoke($"{LogTag} slot {occurrence.Key} skipped: {reason}");
        }

        private List<T> Shuffle<T>(List<T> items)
        {
            var copy = items.ToList();

            for (var i = copy.Count - 1; i > 0; i--)
            {
                var j = seams.NextInt(i + 1);
                (copy[i], copy[j]) = (copy[j], copy[i]);
            }

            return copy;
        }

        /// <summary>The earliest pending occurrence's start (UTC) - a waiting or due-but-unresolved slot (past start) counts - or null when the auto scheduler is off or no slot exists.</summary>
        public DateTime? NextOccurrenceUtc()
        {
            lock (gate)
            {
                if (!seams.Enabled())
                    return null;

                var now = seams.UtcNow();
                var slots = GetSlots();
                var late = WorldEventSchedule.ClampLateMinutes(seams.LateMinutes());
                DateTime? best = WorldEventSchedule.Next(slots, now)?.StartUtc;

                // A waiting or due-but-unresolved occurrence is about to fire (or is held behind a running event): its start is
                // already past, which gives the caller a negative delta, so it counts as imminent.
                foreach (var pending in waiting.Values.Concat(WorldEventSchedule.Due(slots, now, late)))
                {
                    if (resolved.ContainsKey(pending.Key))
                        continue;

                    if (best == null || pending.StartUtc < best.Value)
                        best = pending.StartUtc;
                }

                return best;
            }
        }

        /// <summary>Status lines for /worldevent status.</summary>
        public List<string> StatusLines()
        {
            lock (gate)
            {
                var lines = new List<string>();

                try
                {
                    var now = seams.UtcNow();
                    var enabled = seams.Enabled();
                    var slots = GetSlots();
                    var next = WorldEventSchedule.Next(slots, now);

                    var nextText = next == null
                        ? "none (no slots)"
                        : $"{next.StartUtc:yyyy-MM-dd HH:mm} UTC = {next.StartLocal:yyyy-MM-dd HH:mm} {next.Slot.ZoneId} ({next.Key})";

                    lines.Add($"Scheduler: {(enabled ? "on" : "off (world_events_enabled and world_events_auto_enabled)")}, " +
                              $"{slots.Count} slot(s), next {nextText}");

                    var last = LastOutcome;

                    lines.Add(last == null
                        ? "Scheduler last: nothing since boot"
                        : last.Started
                            ? $"Scheduler last: started run {last.RunId} at {last.Anchor} ({last.SlotKey}) at {last.AtUtc:yyyy-MM-dd HH:mm:ss} UTC"
                            : $"Scheduler last: skipped {last.SlotKey}: {last.Reason} at {last.AtUtc:yyyy-MM-dd HH:mm:ss} UTC");
                }
                catch (Exception ex)
                {
                    lines.Add($"Scheduler: status unavailable ({ex.Message})");
                }

                return lines;
            }
        }

        // ---- live wiring ---------------------------------------------------------------------------------------

        private static readonly ILog log = LogManager.GetLogger(typeof(WorldEventScheduler));

        private static readonly TimeZoneInfo FallbackZone = TimeZoneInfo.CreateCustomTimeZone(
            "WorldEventScheduleFallbackUtcMinus8", TimeSpan.FromHours(-8), "UTC-8", "UTC-8");

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, TimeZoneInfo> ZoneCache = new();
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> ZoneWarned = new();

        private static readonly Lazy<WorldEventScheduler> LiveInstance = new Lazy<WorldEventScheduler>(CreateLive);

        /// <summary>Audit channel writer (actor label, message); set once at startup from Program.cs, null in tests.</summary>
        public static Action<string, string> AuditWriter { get; set; }

        public static WorldEventScheduler Live => LiveInstance.Value;

        /// <summary>Called from <see cref="WorldEventManager.Tick"/> every manager second, whether or not an event is running.</summary>
        public static void TickLive()
        {
            try
            {
                Live.Tick();
            }
            catch (Exception ex)
            {
                log.Error($"{LogTag} live tick threw", ex);
            }
        }

        /// <summary>The random-start body for a city: the web admin request the scheduler hands to TryBuildRequest.</summary>
        public static WorldEventStartBody BodyFor(AnchorDef anchor)
        {
            return new WorldEventStartBody
            {
                Random = true,
                Location = new WorldEventLocationBody
                {
                    Label = anchor.DisplayName,
                    RealmId = anchor.RealmId,
                    Cell = anchor.CellId,
                    X = anchor.OriginX,
                    Y = anchor.OriginY,
                    Z = anchor.OriginZ,
                    Qw = anchor.AnglesW,
                    Qx = anchor.AnglesX,
                    Qy = anchor.AnglesY,
                    Qz = anchor.AnglesZ
                }
            };
        }

        /// <summary>
        /// Maps what <see cref="WorldEventManager.TryStart(WorldEventRequest, out WorldEvent, out string, out WorldEventStartRefusal, out uint)"/>
        /// answered onto the scheduler's three outcomes: AlreadyRunning is Busy (wait, do not burn the occurrence), any other
        /// refusal is Refused, and a start with no run id (a dry run) is Refused.
        /// </summary>
        internal static (WorldEventSchedulerStart Result, uint RunId, string Error) MapStart(bool started, uint runId, string error,
            WorldEventStartRefusal refusal, uint runningRunId)
        {
            if (started)
                return runId == 0
                    ? (WorldEventSchedulerStart.Refused, 0u, "the start returned no event")
                    : (WorldEventSchedulerStart.Started, runId, null);

            return refusal == WorldEventStartRefusal.AlreadyRunning
                ? (WorldEventSchedulerStart.Busy, runningRunId, error)
                : (WorldEventSchedulerStart.Refused, 0u, error);
        }

        /// <summary>The slot's zone: an IANA or Windows id, or a fixed UTC-8 offset (one warning) when the host cannot resolve it.</summary>
        public static TimeZoneInfo ResolveZoneLive(string zoneId)
        {
            return ZoneCache.GetOrAdd(zoneId ?? string.Empty, id =>
            {
                try
                {
                    return TimeZoneInfo.FindSystemTimeZoneById(id);
                }
                catch (Exception ex) when (ex is TimeZoneNotFoundException || ex is InvalidTimeZoneException)
                {
                    if (ZoneWarned.TryAdd(id, true))
                        log.Warn($"{LogTag} time zone '{id}' could not be resolved (ids are case-sensitive IANA ids, e.g. America/Los_Angeles); " +
                                 "slots in it use a fixed UTC-8 offset, which is an hour off during daylight saving", ex);

                    return FallbackZone;
                }
            });
        }

        private static WorldEventScheduler CreateLive()
        {
            var rng = new Random();
            Func<ushort, bool> isRealmKnown = realmId => realmId == 0 || RealmManager.GetRealm(realmId) != null;

            return new WorldEventScheduler(new WorldEventSchedulerSeams
            {
                UtcNow = () => DateTime.UtcNow,
                Enabled = () => WorldEventManager.Enabled && PropertyManager.GetBool("world_events_auto_enabled").Item,
                ScheduleText = () => PropertyManager.GetString("world_events_schedule").Item,
                DefaultZoneId = () => PropertyManager.GetString("world_events_schedule_timezone").Item,
                LateMinutes = () => PropertyManager.GetLong("world_events_schedule_late_minutes").Item,
                ResolveZone = ResolveZoneLive,
                ShutdownPending = () => ServerManager.ShutdownInitiated,
                EventRunning = () =>
                {
                    var current = WorldEventManager.Current;
                    return current != null && current.State != WorldEventState.Done;
                },
                Anchors = () => WorldEventManager.Store?.Anchors.Values.OrderBy(a => a.Id, StringComparer.Ordinal).ToList(),
                BuildRequest = anchor =>
                {
                    var refused = WorldEventAdminService.TryBuildRequest(BodyFor(anchor), WorldEventManager.Store, isRealmKnown,
                        WorldEvent.TunableTeaserLeadSeconds(), Invoker, out var request, out _);

                    return refused == null ? (request, null) : (null, refused.Reason ?? refused.Error.ToString());
                },
                ClearanceOf = id => WorldEventManager.Store != null && WorldEventManager.Store.Sources.TryGetValue(id, out var source)
                    ? source.ClearanceRadius
                    : null,
                Start = request =>
                {
                    var started = WorldEventManager.TryStart(request, out var evt, out var error, out var refusal, out var runningRunId);
                    return MapStart(started, evt?.RunId ?? 0u, error, refusal, runningRunId);
                },
                NextInt = max => rng.Next(max),
                LogInfo = message => log.Info(message),
                LogWarn = message => log.Warn(message),
                Audit = (actor, message) => AuditWriter?.Invoke(actor, message)
            });
        }
    }
}
