using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using log4net;

namespace ACE.Server.Avatars.Gauntlet;

/// <summary>
/// The Allegiance Gauntlet run manager: the start gates (one run per Avatar, the server cap, the cooldown, a configured
/// arena), the run registry, the world tick, the startup close of open history rows, and the admin list and abort.
///
/// THREADING (the IGauntletRunStarter contract). <see cref="CheckCanStart"/> and <see cref="TryStart"/> are THREAD-SAFE and
/// are called synchronously from the vendor buy path on a landblock thread: both take the registry lock, and TryStart does
/// no world work at all. It only registers a run in Purchased; the copy is loaded by the next world tick
/// (<see cref="TickAt"/>, world thread, beside AvatarManager.Tick), which drives every run.
///
/// RESERVATION OWNERSHIP. From a true TryStart the run owns the purchase's Favor reservation: it commits it when wave 1
/// spawns and releases it on any end before that. TryStart never throws: any fault inside it undoes its own registration
/// and returns false, and on false the CALLER releases the reservation (GauntletPurchase.TryBuy), exactly once.
///
/// The cooldown is read from the record cache (GauntletRecords.LastRunAt, persisted on the record row through
/// NoteRunCommitted only) and from this process's own commits, whichever is later, so a record cache that failed to load
/// still enforces it for this session.
/// </summary>
public sealed class GauntletManager : IGauntletRunStarter
{
    private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    /// <summary>The world tick cadence.</summary>
    public const double TickSeconds = 0.5;

    public const string NotAvailableLine = "The Allegiance Gauntlet is not available right now.";
    public const string AlreadyRunningLine = "Your allegiance already has an Allegiance Gauntlet under way.";
    public const string CapLine = "The realm cannot host another Allegiance Gauntlet right now. Try again later.";
    public const string NoArenaLine = "No Allegiance Gauntlet arena is configured.";

    private readonly GauntletEnvironment env;
    private readonly object runsSync = new object();
    private readonly Dictionary<uint, GauntletRun> runs = new Dictionary<uint, GauntletRun>();
    private readonly Dictionary<uint, DateTime> committedThisSession = new Dictionary<uint, DateTime>();
    private uint nextKey;
    private DateTime nextTickAt = DateTime.MinValue;

    internal GauntletManager(GauntletEnvironment env)
    {
        this.env = env ?? throw new ArgumentNullException(nameof(env));
    }

    // ---- the live instance -------------------------------------------------------------------------------------------

    /// <summary>The live manager; null until <see cref="Initialize"/>.</summary>
    public static GauntletManager Live { get; private set; }

    /// <summary>
    /// Startup, after ThreadDungeonManager.Initialize (the site store reads the Thread catalog). Closes every open history row
    /// as Restart (synchronously, before any run of this process exists), loads the arena sites, and installs the starter
    /// (GauntletRunStarter.Current). Never throws.
    /// </summary>
    public static void Initialize()
    {
        try
        {
            GauntletSiteStore.Current = GauntletSiteStore.LoadFromServerConfig();
        }
        catch (Exception ex)
        {
            log.Error("[GAUNTLET] loading the arena sites threw; no arena is configured", ex);
            GauntletSiteStore.Current = GauntletSiteStore.Empty;
        }

        var manager = new GauntletManager(LiveGauntletEnvironment.Build());
        manager.Start();
        Live = manager;
        GauntletRunStarter.Current = manager;
    }

    /// <summary>The startup half that needs no statics: close open rows. Instance method so tests drive it with a fake store.</summary>
    internal void Start()
    {
        try
        {
            if (env.Store.CloseOpenRuns(env.UtcNow(), out var closed))
                log.Info($"[GAUNTLET] closed {closed} open run row(s) as Restart");
            else
                log.Error("[GAUNTLET] closing open run rows failed; they stay open until the next startup");
        }
        catch (Exception ex)
        {
            log.Error("[GAUNTLET] closing open run rows threw", ex);
        }
    }

    /// <summary>WorldManager.UpdateGameWorld, world thread. Self-rate-limited.</summary>
    public static void Tick() => Live?.TickThrottled();

    /// <summary>The admin list (empty before Initialize).</summary>
    public static IReadOnlyList<GauntletRunInfo> List() => Live?.ListRuns() ?? Array.Empty<GauntletRunInfo>();

    /// <summary>The admin abort of an Avatar's live run; returns the line for the admin.</summary>
    public static string Abort(uint avatarId, bool refund) => Live?.AbortRun(avatarId, refund) ?? NotAvailableLine;

    /// <summary>How long the Avatar's cooldown has left (null when it may start a run now, or before Initialize).</summary>
    public static TimeSpan? CooldownLeft(uint avatarId) => Live?.CooldownLeftFor(avatarId, Live.env.UtcNow());

    /// <summary>True when the feature switch (allegiance_gauntlet_enabled) is on; false before Initialize.</summary>
    public static bool IsEnabled => Live != null && GauntletTuning.Sanitized(Live.env.Tuning() ?? GauntletTuning.Defaults).Enabled;

    /// <summary>The live run of an Avatar (any non-terminal state), or null.</summary>
    public static GauntletRun ForAvatar(uint avatarId) => Live?.FindByAvatar(avatarId);

    /// <summary>The live run whose arena copy is <paramref name="instance"/>, or null (the death hold and feed exclusion read this).</summary>
    public static GauntletRun ForInstance(uint instance) => Live?.FindByInstance(instance);

    // ---- IGauntletRunStarter -----------------------------------------------------------------------------------------

    /// <inheritdoc/>
    public string CheckCanStart(uint avatarId, DateTime nowUtc)
    {
        try
        {
            lock (runsSync)
                return CheckLocked(avatarId, nowUtc, env.Tuning());
        }
        catch (Exception ex)
        {
            log.Error($"[GAUNTLET] CheckCanStart for Avatar {avatarId} threw", ex);
            return NotAvailableLine;
        }
    }

    private string CheckLocked(uint avatarId, DateTime nowUtc, GauntletTuning tuning)
    {
        var t = GauntletTuning.Sanitized(tuning ?? GauntletTuning.Defaults);

        if (runs.Values.Any(r => r.AvatarId == avatarId && !r.IsTerminal))
            return AlreadyRunningLine;

        if (runs.Values.Count(r => !r.IsTerminal) >= t.MaxConcurrent)
            return CapLine;

        var last = LastCommit(avatarId);

        if (last.HasValue)
        {
            var ready = last.Value.AddHours(t.CooldownHours);

            if (ready > nowUtc)
                return $"Your Avatar must rest before another Allegiance Gauntlet: {CooldownText(ready - nowUtc)} remain.";
        }

        if (CandidateSites(t).Count == 0)
            return NoArenaLine;

        return null;
    }

    /// <summary>"5h 12m", "12m", or "1m" for anything under a minute.</summary>
    internal static string CooldownText(TimeSpan left)
    {
        var minutes = (long)Math.Ceiling(Math.Max(0, left.TotalMinutes));
        if (minutes < 1) minutes = 1;
        var h = minutes / 60;
        var m = minutes % 60;
        return h > 0 ? $"{h}h {m}m" : $"{m}m";
    }

    internal TimeSpan? CooldownLeftFor(uint avatarId, DateTime nowUtc)
    {
        var t = GauntletTuning.Sanitized(env.Tuning() ?? GauntletTuning.Defaults);

        lock (runsSync)
        {
            var last = LastCommit(avatarId);

            if (!last.HasValue)
                return null;

            var left = last.Value.AddHours(t.CooldownHours) - nowUtc;
            return left > TimeSpan.Zero ? left : null;
        }
    }

    private DateTime? LastCommit(uint avatarId)
    {
        DateTime? fromRecords = null;

        try
        {
            fromRecords = env.Records.LastRunAt(avatarId);
        }
        catch (Exception ex)
        {
            log.Error($"[GAUNTLET] reading the cooldown of Avatar {avatarId} threw", ex);
        }

        committedThisSession.TryGetValue(avatarId, out var local);
        DateTime? fromSession = local == default ? null : local;

        if (fromRecords == null) return fromSession;
        if (fromSession == null) return fromRecords;
        return fromRecords > fromSession ? fromRecords : fromSession;
    }

    /// <summary>The configured sites: the store's, narrowed by allegiance_gauntlet_sites when it names any.</summary>
    private List<GauntletSiteDef> CandidateSites(GauntletTuning t)
    {
        var all = (env.Sites() ?? Array.Empty<GauntletSiteDef>()).Where(s => s?.Outdoor != null && s.Centre != null).ToList();
        var names = (t.Sites ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (names.Length == 0)
            return all;

        return names.Select(n => all.FirstOrDefault(s => string.Equals(s.Id, n, StringComparison.Ordinal))).Where(s => s != null).Distinct().ToList();
    }

    /// <inheritdoc/>
    public bool TryStart(GauntletStartRequest request, out string refusal)
    {
        refusal = null;
        GauntletRun run = null;
        uint key = 0;

        try
        {
            if (request == null)
            {
                refusal = NotAvailableLine;
                return false;
            }

            var tuning = GauntletTuning.Sanitized(env.Tuning() ?? GauntletTuning.Defaults);

            lock (runsSync)
            {
                // The checks again, in the same critical section as the insert, so two buys cannot both pass.
                refusal = CheckLocked(request.AvatarId, request.NowUtc, tuning);

                if (refusal != null)
                    return false;

                var sites = CandidateSites(tuning);
                var site = sites[env.Random.Next(sites.Count)];

                key = ++nextKey == 0 ? ++nextKey : nextKey;
                run = new GauntletRun(key, request, site, env.Random.Next(), tuning, env);
                runs[key] = run;
            }

            var started = run;
            env.Store.InsertRun(run.NewRunRow(), (ok, id) => started.OnInserted(ok, id));

            log.Info($"[GAUNTLET] run {key} purchased for Avatar {request.AvatarId} by 0x{request.BuyerGuid:X8} at {run.Site.Id} ({request.Price} Favor reserved)");
            return true;
        }
        catch (Exception ex)
        {
            // Never throw once the run could own the reservation: undo the registration and let the caller release.
            log.Error($"[GAUNTLET] TryStart for Avatar {request?.AvatarId} threw; nothing started", ex);

            if (key != 0)
            {
                lock (runsSync)
                    runs.Remove(key);

                if (run != null)
                    run.Hold.State = GauntletState.Aborted;
            }

            refusal = NotAvailableLine;
            return false;
        }
    }

    // ---- the tick ----------------------------------------------------------------------------------------------------

    private void TickThrottled()
    {
        var now = env.UtcNow();

        if (now < nextTickAt)
            return;

        nextTickAt = now.AddSeconds(TickSeconds);
        TickAt(now);
    }

    /// <summary>One sweep at <paramref name="now"/>, unthrottled (the test entry point). World thread.</summary>
    internal void TickAt(DateTime now)
    {
        List<GauntletRun> snapshot;

        lock (runsSync)
            snapshot = runs.Values.ToList();

        foreach (var run in snapshot)
        {
            try
            {
                run.Tick(now);
                NoteCommit(run);
            }
            catch (Exception ex)
            {
                log.Error($"[GAUNTLET] {run} tick threw", ex);
            }

            if (run.IsTerminal)
            {
                run.FlushOnRemove();

                lock (runsSync)
                    runs.Remove(run.RunKey);
            }
        }
    }

    /// <summary>Remembers a commit for this session's cooldown backstop (the persisted cooldown goes through NoteRunCommitted).</summary>
    private void NoteCommit(GauntletRun run)
    {
        if (run.CommittedAt is not { } at)
            return;

        lock (runsSync)
        {
            if (!committedThisSession.TryGetValue(run.AvatarId, out var known) || known < at)
                committedThisSession[run.AvatarId] = at;
        }
    }

    // ---- reads and admin ---------------------------------------------------------------------------------------------

    internal IReadOnlyList<GauntletRunInfo> ListRuns()
    {
        var now = env.UtcNow();

        lock (runsSync)
            return runs.Values.OrderBy(r => r.RunKey).Select(r => r.Info(now)).ToList();
    }

    internal GauntletRun FindByAvatar(uint avatarId)
    {
        lock (runsSync)
            return runs.Values.FirstOrDefault(r => r.AvatarId == avatarId && !r.IsTerminal);
    }

    internal GauntletRun FindByInstance(uint instance)
    {
        if (instance == 0)
            return null;

        lock (runsSync)
            return runs.Values.FirstOrDefault(r => r.Instance == instance && !r.IsTerminal);
    }

    internal int RunCount
    {
        get
        {
            lock (runsSync)
                return runs.Count;
        }
    }

    internal string AbortRun(uint avatarId, bool refund)
    {
        var run = FindByAvatar(avatarId);

        if (run == null)
            return $"Avatar {avatarId} has no Allegiance Gauntlet running.";

        return run.Abort(refund, env.UtcNow());
    }
}
