using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

using log4net;

using ACE.Common;
using ACE.Database.Models.Shard;

namespace ACE.Server.Avatars;

public enum AvatarFeedSource
{
    Base,
    Thread,
    WorldEvent,
    Fellowship,
}

/// <summary>
/// One XP grant offered to an allegiance's Avatar. Amount is raw received XP. The ledger's raw column stores
/// contribution POINTS before the daily curve (the same unit as credited and LifetimeXp), not this raw XP.
/// </summary>
public readonly record struct AvatarFeedEvent(uint MonarchGuid, uint AccountId, uint CharacterGuid, long Amount, AvatarFeedSource Source);

/// <summary>
/// The in-memory owner of every Allegiance Avatar.
///
/// THREADS. Enqueue is called from GrantXP on any landblock thread: it only adds to a ConcurrentQueue and never
/// touches the store or the lock. Tick and OnShutdown run on the world thread (the roster source walks
/// AllegianceManager's unlocked dictionary). Store callbacks arrive on another thread. All state is guarded by
/// one private lock, and nothing calls the store (or WorldEventManager) while holding it.
///
/// CREDITING. A drain only accumulates each (Avatar, account, UTC day)'s contribution points, as a double so
/// fractions of a point carry over. The daily curve is applied once per such key per flush interval, to the
/// summed whole points, so the curve runs once per account per batch, not once per kill. AvatarAccountCaps
/// credits curve(day's raw total) minus what was already credited, so the day's credit does not depend on how
/// the raw total was split into batches or across Avatars. LifetimeXp and Favor move once per flushSeconds.
///
/// FAILED LOAD. If either startup read fails the manager stays disabled for the session: no feed, no founding,
/// no flush, no reservations. Starting empty would found a second Avatar for every allegiance that has one.
/// </summary>
public static partial class AvatarManager
{
    private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    private const double TickIntervalSeconds = 1.0;
    private const double RecomputeIntervalSeconds = 600.0;
    private const double SecondsPerDay = 86400.0;

    // Absorbs the last-ulp error of summed fractional points before flooring (0.1 x 1000 = 99.99999999999986).
    private const double PointEpsilon = 1e-6;

    private sealed class Pending
    {
        public double Points;
        public uint LastCharacter;
    }

    private static readonly object sync = new object();

    /// <summary>
    /// Level rises seen while crediting, one per Avatar (a later rise in the same window extends the same event).
    /// Guarded by sync. Filled under the lock, drained by DispatchLevelUps once the lock is released.
    /// </summary>
    private static readonly List<AvatarLevelUpEvent> levelUps = new List<AvatarLevelUpEvent>();

    /// <summary>Test hook: whether the calling thread holds the manager lock.</summary>
    internal static bool SyncHeldByCurrentThread => System.Threading.Monitor.IsEntered(sync);

    /// <summary>Records a rise from <paramref name="oldLevel"/> to the Avatar's current level. Caller holds sync.</summary>
    private static void NoteLevelUp(AvatarState s, int oldLevel, AvatarTuning t)
    {
        var newLevel = s.Level(t);
        if (newLevel <= oldLevel)
            return;

        var i = levelUps.FindIndex(e => e.AvatarId == s.AvatarId);
        if (i >= 0)
            levelUps[i] = levelUps[i] with { MonarchGuid = s.MonarchGuid, NewLevel = newLevel };
        else
            levelUps.Add(new AvatarLevelUpEvent(s.AvatarId, s.MonarchGuid, oldLevel, newLevel));
    }

    /// <summary>
    /// Hands the collected level rises to AvatarLevelUp. Must be called with sync NOT held: the dispatch touches NPCs,
    /// the world and chat. Safe from any thread; each event is dispatched once.
    /// </summary>
    private static void DispatchLevelUps(AvatarTuning t)
    {
        List<AvatarLevelUpEvent> batch;
        lock (sync)
        {
            if (levelUps.Count == 0)
                return;
            batch = new List<AvatarLevelUpEvent>(levelUps);
            levelUps.Clear();
        }

        foreach (var e in batch)
        {
            try
            {
                AvatarLevelUp.Dispatch(e, t);
            }
            catch (Exception ex)
            {
                log.Error($"[AVATAR] Level-up dispatch for Avatar {e.AvatarId} failed.", ex);
            }
        }
    }

    private static ConcurrentQueue<AvatarFeedEvent> feed = new ConcurrentQueue<AvatarFeedEvent>();

    /// <summary>The level pool feed: (monarch, raw XP a member received). Same lock-free shape as <see cref="feed"/>.</summary>
    private static ConcurrentQueue<(uint Monarch, long Amount)> levelFeed = new ConcurrentQueue<(uint Monarch, long Amount)>();
    private static volatile bool enabled;

    private static IAvatarStore store;
    private static Func<AvatarTuning> tuning;
    private static Func<double> clock;
    private static Func<AccountActivityIndex> activitySource;
    private static Func<IReadOnlyList<AvatarRoster>> rosterSource;
    private static Func<uint, AvatarRoster> rosterForSource;

    /// <summary>
    /// Whether a bound monarch guid still leads, is now a vassal, or is gone. Null turns the recompute's binding repair
    /// off: the test seam's default, because without players every guid would look gone.
    /// </summary>
    private static Func<uint, AvatarMonarchStatus> monarchStatusSource;

    /// <summary>
    /// The activity index from the last recompute, patched in place by TouchAccount on every login so a monarch
    /// returning on any character gets Keeper rights back at once, not up to ten minutes later.
    /// </summary>
    private static AccountActivityIndex liveActivity = new AccountActivityIndex();

    private static Dictionary<uint, AvatarState> byId = new Dictionary<uint, AvatarState>();
    private static Dictionary<uint, uint> byMonarch = new Dictionary<uint, uint>();
    private static Dictionary<uint, AvatarLedger> ledgers = new Dictionary<uint, AvatarLedger>();
    private static AvatarAccountCaps caps = new AvatarAccountCaps();
    private static Dictionary<(uint AvatarId, uint AccountId, DateTime Day), Pending> pending = new Dictionary<(uint, uint, DateTime), Pending>();
    private static HashSet<uint> dirtyAvatars = new HashSet<uint>();

    /// <summary>Monarch guids with an Insert in flight. Never founded again until the callback lands.</summary>
    private static HashSet<uint> pendingFoundings = new HashSet<uint>();

    /// <summary>Monarch guids whose founding outcome is unknown (Insert false and the reload failed). Never founded this session.</summary>
    private static HashSet<uint> blockedFoundings = new HashSet<uint>();

    /// <summary>
    /// Monarch guid -> the Avatar that released it (rebind, absorb, retire) and whose row still holds it in the store
    /// until a flush lands. Founding skips these guids: an Insert for one would hit allegiance_avatar_monarch_uidx and
    /// block founding for that monarch for the session.
    /// </summary>
    private static Dictionary<uint, uint> vacatedUnflushed = new Dictionary<uint, uint>();

    /// <summary>Batches handed to the store whose callback has not landed yet.</summary>
    private static List<(List<AllegianceAvatar> Avatars, List<AllegianceAvatarContribution> Rows)> outstanding = new();
    private static double lastTick;
    private static double lastCredit;
    private static double lastRecompute;
    private static bool recomputedOnce;
    private static int serverActiveAccounts;
    private static DateTime lastPruneDay;

    /// <summary>False until a successful Initialize, and for the rest of the session after a failed load.</summary>
    public static bool Enabled => enabled;

    /// <summary>Production startup: the shard store, live tunables, wall clock, PlayerManager activity and AllegianceManager rosters.</summary>
    public static void Initialize()
    {
        Initialize(
            new ShardAvatarStore(),
            AvatarTuning.FromPropertyManager,
            Time.GetUnixTime,
            () => AccountActivityIndex.FromPlayerManager(Time.GetUnixTime()),
            () => AvatarRosters.FromAllegianceManager(),
            monarchGuid => AvatarRosters.ForMonarch(monarchGuid),
            AvatarLifecycle.LiveMonarchStatus);
    }

    /// <summary>
    /// The test seam. Replaces every collaborator and all state, then loads every Avatar and the contribution
    /// window (today minus topContributorsDays) so today's caps and the top list survive a restart.
    /// <paramref name="rosterFor"/> is the one-allegiance roster the lifecycle hooks read; null means "pick it out of
    /// <paramref name="rosters"/>". <paramref name="monarchStatus"/> feeds the recompute's binding repair; null leaves it off.
    /// </summary>
    public static void Initialize(IAvatarStore store, Func<AvatarTuning> tuning, Func<double> clockUnix, Func<AccountActivityIndex> activity, Func<IReadOnlyList<AvatarRoster>> rosters,
        Func<uint, AvatarRoster> rosterFor = null, Func<uint, AvatarMonarchStatus> monarchStatus = null)
    {
        lock (sync)
        {
            enabled = false;
            AvatarManager.store = store;
            AvatarManager.tuning = tuning;
            clock = clockUnix;
            activitySource = activity;
            rosterSource = rosters;
            monarchStatusSource = monarchStatus;
            rosterForSource = rosterFor ?? (monarch => (rosters() ?? Array.Empty<AvatarRoster>()).FirstOrDefault(r => r.MonarchGuid == monarch));
            liveActivity = new AccountActivityIndex();

            feed = new ConcurrentQueue<AvatarFeedEvent>();
            levelFeed = new ConcurrentQueue<(uint Monarch, long Amount)>();
            byId = new Dictionary<uint, AvatarState>();
            byMonarch = new Dictionary<uint, uint>();
            ledgers = new Dictionary<uint, AvatarLedger>();
            caps = new AvatarAccountCaps();
            pending = new Dictionary<(uint, uint, DateTime), Pending>();
            levelUps.Clear();
            dirtyAvatars = new HashSet<uint>();
            pendingFoundings = new HashSet<uint>();
            blockedFoundings = new HashSet<uint>();
            vacatedUnflushed = new Dictionary<uint, uint>();
            outstanding = new();
            recomputedOnce = false;
            serverActiveAccounts = 0;
            lastPruneDay = default;
        }

        var now = clockUnix();
        var t = tuning();
        var today = DayOf(now);

        List<AllegianceAvatar> rows;
        List<AllegianceAvatarContribution> contributions;
        try
        {
            rows = store.LoadAll();
            contributions = rows == null ? null : store.LoadContributionsSince(today.AddDays(-Math.Max(0, t.TopContributorsDays)));
        }
        catch (Exception ex)
        {
            log.Error("[AVATAR] Startup load threw; Allegiance Avatars are DISABLED for this session.", ex);
            return;
        }

        if (rows == null || contributions == null)
        {
            log.Error($"[AVATAR] Startup load failed ({(rows == null ? "avatars" : "contributions")}); Allegiance Avatars are DISABLED for this session. Nothing is fed, founded or flushed until a restart reads them.");
            return;
        }

        lock (sync)
        {
            foreach (var row in rows)
                AddLoaded(ShardAvatarStore.FromEntity(row));

            foreach (var group in contributions.GroupBy(c => c.AvatarId))
            {
                if (ledgers.TryGetValue(group.Key, out var ledger))
                    ledger.Restore(group);
            }

            caps.Restore(contributions.Where(c => c.Day.Date == today));

            lastTick = double.NegativeInfinity;
            lastCredit = now;
            lastRecompute = now;
            enabled = true;
        }

        log.Info($"[AVATAR] Loaded {rows.Count} Avatars and {contributions.Count} contribution rows.");
    }

    /// <summary>Kill path. Lock free and store free: only adds to the queue.</summary>
    public static void Enqueue(in AvatarFeedEvent e)
    {
        if (!enabled || e.Amount <= 0)
            return;

        feed.Enqueue(e);
    }

    /// <summary>Level pool path. Lock free and store free: only adds to the queue. Amount is raw XP a member received.</summary>
    public static void EnqueueLevel(uint monarchGuid, long amount)
    {
        if (!enabled || amount <= 0)
            return;

        levelFeed.Enqueue((monarchGuid, amount));
    }

    /// <summary>World thread. Rate limited to once a second; the recompute, daily and flush steps gate on time.</summary>
    public static void Tick()
    {
        if (!enabled)
            return;

        var now = clock();
        if (now - lastTick < TickIntervalSeconds)
            return;
        lastTick = now;

        var t = tuning();

        if (t.Enabled)
        {
            // The first tick recomputes before draining, so the roster scale is known before any feed is weighed.
            if (!recomputedOnce || now - lastRecompute >= RecomputeIntervalSeconds)
                Recompute(now, t);

            Drain(now, t);
            DecayDaily(now, t);
        }
        else
        {
            // Switched off by the operator: nothing is fed, founded or decayed, but what was already
            // earned is still credited and written.
            while (feed.TryDequeue(out _)) { }
            while (levelFeed.TryDequeue(out _)) { }
        }

        if (now - lastCredit >= Math.Max(1, t.FlushSeconds))
        {
            lastCredit = now;
            CreditPending(now, t);
            DispatchLevelUps(t);
            Flush(force: false);
        }
    }

    /// <summary>World thread, at world stop and ahead of the shard drain: credits what is queued and flushes regardless of the interval.</summary>
    public static void OnShutdown()
    {
        if (!enabled)
            return;

        try
        {
            var now = clock();
            var t = tuning();
            Drain(now, t);
            CreditPending(now, t);
            DispatchLevelUps(t);
            Flush(force: true);
        }
        catch (Exception ex)
        {
            log.Error("[AVATAR] Shutdown flush failed; up to one flush interval of contribution is lost.", ex);
        }
    }

    // ---- readers ---------------------------------------------------------------------------------------------

    /// <summary>The Avatar bound to that monarch, or null.</summary>
    public static AvatarState ForMonarch(uint monarchGuid)
    {
        lock (sync)
            return byMonarch.TryGetValue(monarchGuid, out var id) && byId.TryGetValue(id, out var s) ? s : null;
    }

    public static AvatarState Get(uint avatarId)
    {
        lock (sync)
            return byId.TryGetValue(avatarId, out var s) ? s : null;
    }

    /// <summary>A snapshot list of every Avatar in memory, in any state.</summary>
    public static IReadOnlyCollection<AvatarState> All
    {
        get
        {
            lock (sync)
                return byId.Values.ToList();
        }
    }

    /// <summary>The Avatar's top contributing accounts over the last topContributorsDays.</summary>
    public static IReadOnlyList<(uint AccountId, long Credited)> TopContributors(uint avatarId, int count) =>
        TopContributors(avatarId, count, tuning().TopContributorsDays);

    /// <summary>The Avatar's top contributing accounts over the last <paramref name="days"/> days.</summary>
    public static IReadOnlyList<(uint AccountId, long Credited)> TopContributors(uint avatarId, int count, long days)
    {
        var since = DayOf(clock()).AddDays(-Math.Max(0, days));
        lock (sync)
            return ledgers.TryGetValue(avatarId, out var ledger) ? ledger.Top(since, count) : Array.Empty<(uint, long)>();
    }

    // ---- Favor reservation (Task 12) -------------------------------------------------------------------------

    /// <summary>Holds <paramref name="price"/> for a purchase. True only when Favor - ReservedFavor covers it.</summary>
    public static bool TryReserveFavor(uint avatarId, ulong price)
    {
        if (!enabled)
            return false;

        lock (sync)
        {
            if (!byId.TryGetValue(avatarId, out var s) || s.AvailableFavor < price)
                return false;

            s.ReservedFavor += price;
            return true;
        }
    }

    /// <summary>Spends a reservation: Favor and ReservedFavor each drop by <paramref name="price"/>, saturating at 0.</summary>
    public static void CommitDebit(uint avatarId, ulong price)
    {
        lock (sync)
        {
            if (!byId.TryGetValue(avatarId, out var s))
                return;

            // A reservation that is gone (the binding ended and a later one started) must never debit the new binding's Favor.
            if (s.ReservedFavor < price)
            {
                log.Warn($"[AVATAR] CommitDebit of {price} on Avatar {avatarId} skipped: only {s.ReservedFavor} is reserved (the reservation was lost).");
                return;
            }

            s.Favor = s.Favor > price ? s.Favor - price : 0;
            s.ReservedFavor = s.ReservedFavor > price ? s.ReservedFavor - price : 0;
            dirtyAvatars.Add(avatarId);
        }
    }

    /// <summary>
    /// Credits <paramref name="amount"/> of already-committed Favor back to the Avatar (the Gauntlet admin refund after wave 1).
    /// Unlike a purchase release this ADDS to Favor, so it respects allegiance_avatar_favor_cap: Favor never ends above the cap,
    /// so a refund to an Avatar that earned its way back to the cap in the meantime returns less than <paramref name="amount"/>.
    /// Marked dirty, so it flushes with the other Favor mutations as the new absolute value. False when the Avatar is unknown
    /// (nothing to credit); true otherwise, including a refund the cap clamped to nothing.
    /// </summary>
    public static bool RefundFavor(uint avatarId, ulong amount)
    {
        var t = tuning();

        lock (sync)
        {
            if (!byId.TryGetValue(avatarId, out var s))
                return false;

            var cap = (ulong)Math.Max(0L, t.FavorCap);
            var total = s.Favor > ulong.MaxValue - amount ? ulong.MaxValue : s.Favor + amount;
            var next = Math.Min(cap, total);

            // Never lower Favor: an Avatar already above the cap (the cap was cut at runtime) keeps what it has.
            if (next > s.Favor)
                s.Favor = next;

            dirtyAvatars.Add(avatarId);
            return true;
        }
    }

    /// <summary>Returns a reservation unspent, saturating at 0.</summary>
    public static void ReleaseReservation(uint avatarId, ulong price)
    {
        lock (sync)
        {
            if (byId.TryGetValue(avatarId, out var s))
                s.ReservedFavor = s.ReservedFavor > price ? s.ReservedFavor - price : 0;
        }
    }

    // ---- tick steps ------------------------------------------------------------------------------------------

    /// <summary>Step 1: weigh each queued event and add it to its (Avatar, account, day) pending total.</summary>
    private static void Drain(double now, AvatarTuning t)
    {
        var day = DayOf(now);

        lock (sync)
        {
            while (feed.TryDequeue(out var e))
            {
                if (!byMonarch.TryGetValue(e.MonarchGuid, out var avatarId) || !byId.TryGetValue(avatarId, out var s))
                    continue;
                // An Inactive Avatar still accrues: Inactive only stops its sales (AvatarVendor.IsLive) and lets its Favor decay.
                if (s.State != AvatarBindState.Bound)
                    continue;

                var points = e.Amount * t.BaseRate * Multiplier(e.Source, t)
                    * AvatarCurves.RosterScale(s.ActiveAccounts, serverActiveAccounts, t);
                if (!(points > 0) || double.IsInfinity(points))
                    continue;

                var key = (avatarId, e.AccountId, day);
                if (!pending.TryGetValue(key, out var p))
                {
                    p = new Pending();
                    pending[key] = p;
                }
                p.Points += points;
                p.LastCharacter = e.CharacterGuid;
            }

            DrainLevelFeed(t);
        }
    }

    /// <summary>
    /// The level pool: a flat share of the raw XP, with no source multiplier, roster scale or daily cap. Takes the manager
    /// lock itself (re-entrant), so TryAbsorb can drain what is queued before it reads the source's pool.
    /// </summary>
    private static void DrainLevelFeed(AvatarTuning t)
    {
        lock (sync)
        {
            while (levelFeed.TryDequeue(out var l))
            {
                if (!byMonarch.TryGetValue(l.Monarch, out var levelAvatarId) || !byId.TryGetValue(levelAvatarId, out var ls))
                    continue;
                if (ls.State != AvatarBindState.Bound)
                    continue;

                var r = ls.LevelXpRemainder + l.Amount * t.LevelXpFraction;
                if (!(r > 0) || double.IsNaN(r))
                    continue;

                var whole = Math.Floor(r);
                var before = ls.Level(t);
                ls.LevelXp = whole >= 18446744073709551615.0 || ls.LevelXp > ulong.MaxValue - (ulong)whole
                    ? ulong.MaxValue
                    : ls.LevelXp + (ulong)whole;
                ls.LevelXpRemainder = double.IsInfinity(r) ? 0.0 : r - whole;
                NoteLevelUp(ls, before, t);
                dirtyAvatars.Add(levelAvatarId);
            }
        }
    }

    /// <summary>
    /// Applies the account-day curve once per pending (Avatar, account, day) to its whole points, records the
    /// ledger row, and moves the meters. The fraction left over carries to the next interval of the same day.
    /// </summary>
    private static void CreditPending(double now, AvatarTuning t, uint? onlyAvatarId = null)
    {
        var nowUtc = Time.GetDateTimeFromTimestamp(now);
        var today = DayOf(now);

        lock (sync)
        {
            // Deterministic order, so which Avatar fills an account's shared cap first does not depend on hashing.
            foreach (var key in pending.Keys.Where(k => onlyAvatarId == null || k.AvatarId == onlyAvatarId.Value)
                .OrderBy(k => k.Day).ThenBy(k => k.AvatarId).ThenBy(k => k.AccountId).ToList())
            {
                var p = pending[key];
                var whole = Math.Floor(p.Points + PointEpsilon);
                var contribution = whole >= long.MaxValue ? long.MaxValue : (long)whole;

                p.Points = Math.Max(0.0, p.Points - whole);

                if (contribution <= 0)
                {
                    // A sub-point remainder of a past day can never be credited.
                    if (key.Day < today)
                        pending.Remove(key);
                    continue;
                }

                if (!byId.TryGetValue(key.AvatarId, out var s))
                {
                    pending.Remove(key);
                    continue;
                }

                var credited = caps.Credit(key.AccountId, key.Day, contribution, t);
                Ledger(key.AvatarId).Record(key.AccountId, p.LastCharacter, contribution, credited, key.Day);

                if (credited > 0)
                {
                    s.LifetimeXp = s.LifetimeXp > ulong.MaxValue - (ulong)credited ? ulong.MaxValue : s.LifetimeXp + (ulong)credited;
                    s.Favor = AvatarCurves.AddFavor(s.Favor, (ulong)credited, t);
                    s.LastContributionUtc = nowUtc;
                }
                dirtyAvatars.Add(key.AvatarId);

                if (key.Day < today)
                    pending.Remove(key);
            }
        }
    }

    /// <summary>
    /// Step 2: rebuilds the activity index, refreshes Active and the Keeper per Avatar, repairs any binding whose
    /// monarch no longer leads, and founds Avatars for rosters with enough active accounts and none bound. The
    /// collaborators are read before the lock is taken. The repair runs before the founding pass, so an allegiance
    /// that a stray Avatar's monarch has joined adopts it instead of founding a new one and absorbing it.
    /// </summary>
    private static void Recompute(double now, AvatarTuning t)
    {
        lastRecompute = now;
        recomputedOnce = true;

        var activity = activitySource() ?? new AccountActivityIndex();
        var rosters = rosterSource() ?? Array.Empty<AvatarRoster>();
        var window = (int)Math.Clamp(t.ActiveWindowDays, 0L, int.MaxValue);
        var minActive = Math.Max(1L, t.MinActiveAccounts);
        var today = DayOf(now);

        var toFound = new List<AllegianceAvatar>();
        var keeperWork = new List<(uint Monarch, AvatarRoster Roster)>();
        var reconcileWork = new List<(uint AvatarId, uint Monarch)>();

        lock (sync)
        {
            liveActivity = activity;
            serverActiveAccounts = activity.CountActive(now, window);

            var rosterByMonarch = new Dictionary<uint, AvatarRoster>();
            foreach (var roster in rosters)
                rosterByMonarch[roster.MonarchGuid] = roster;

            foreach (var s in byId.Values)
            {
                if (s.State != AvatarBindState.Bound || !s.MonarchGuid.HasValue)
                    continue;

                rosterByMonarch.TryGetValue(s.MonarchGuid.Value, out var roster);
                var activeAccounts = roster == null ? 0 : CountActiveAccounts(roster, activity, now, window);
                var active = activeAccounts >= minActive;

                if (s.Active != active)
                    s.Version++;
                s.Active = active;
                s.ActiveAccounts = activeAccounts;

                keeperWork.Add((s.MonarchGuid.Value, roster));
                if (!pendingFoundings.Contains(s.MonarchGuid.Value))
                    reconcileWork.Add((s.AvatarId, s.MonarchGuid.Value));
            }
        }

        // The Keeper, per roster, outside the lock: a change is announced and touches the NPCs.
        foreach (var (monarch, roster) in keeperWork)
            AvatarLifecycle.RecomputeKeeper(monarch, roster);

        // Repair, outside the lock: a Bound Avatar whose monarch is now a vassal moves to that player's allegiance
        // (adopted, or absorbed when it has an Avatar); one whose monarch is gone takes the monarch-delete path.
        if (monarchStatusSource != null)
        {
            foreach (var (avatarId, monarch) in reconcileWork)
            {
                try
                {
                    var status = monarchStatusSource(monarch);
                    if (status != AvatarMonarchStatus.Monarch)
                        AvatarLifecycle.Reconcile(avatarId, monarch, status);
                }
                catch (Exception ex)
                {
                    log.Error($"[AVATAR] Reconcile of Avatar {avatarId} (monarch 0x{monarch:X8}) failed.", ex);
                }
            }
        }

        lock (sync)
        {
            foreach (var roster in rosters)
            {
                var monarch = roster.MonarchGuid;
                if (byMonarch.ContainsKey(monarch) || pendingFoundings.Contains(monarch) || blockedFoundings.Contains(monarch)
                    || vacatedUnflushed.ContainsKey(monarch))
                    continue;
                if (CountActiveAccounts(roster, activity, now, window) < minActive)
                    continue;

                pendingFoundings.Add(monarch);
                toFound.Add(new AllegianceAvatar
                {
                    MonarchGuid = monarch,
                    State = (byte)AvatarBindState.Bound,
                    LastDecayDay = today,
                });
            }
        }

        foreach (var row in toFound)
        {
            var monarch = row.MonarchGuid.Value;
            var activeAccounts = rosters.Where(r => r.MonarchGuid == monarch).Select(r => CountActiveAccounts(r, activity, now, window)).FirstOrDefault();
            try
            {
                // Everything the callback needs is captured here: it runs on the shard worker, which must not
                // read PropertyManager or the world clock.
                store.Insert(row, (ok, id) => OnFounded(monarch, ok, id, activeAccounts, minActive, today));
            }
            catch (Exception ex)
            {
                // Unknown whether anything was queued: never risk a second Avatar for this monarch.
                log.Error($"[AVATAR] Founding for monarch 0x{monarch:X8} threw at enqueue; founding blocked for this session.", ex);
                lock (sync)
                {
                    pendingFoundings.Remove(monarch);
                    blockedFoundings.Add(monarch);
                }
            }
        }

        // The Seat re-check, every recompute (10 minutes): after the Keepers are settled, a Seat whose mansion is no
        // longer the monarch's or the Keeper's is cleared, and a seated NPC missing from a loaded landblock comes back.
        try
        {
            AvatarNpcs.RecheckSeats();
        }
        catch (Exception ex)
        {
            log.Error("[AVATAR] Seat re-check failed.", ex);
        }
    }

    /// <summary>
    /// Insert callback (store thread). On false the row may still exist (a retry collided with our own commit),
    /// so the store is reloaded and the row for this monarch adopted. If the reload fails the outcome is unknown
    /// and founding stays blocked for the session; if it succeeds with no row, founding may try again later.
    /// A reloaded row that is not Bound, or that is already in memory, also blocks founding for that monarch:
    /// the guid is still held by a row the manager will not bind, so another Insert could only collide again.
    /// </summary>
    private static void OnFounded(uint monarch, bool ok, uint avatarId, int activeAccounts, long minActive, DateTime today)
    {
        try
        {
            AvatarState founded = null;
            var blocked = false;

            if (ok && avatarId != 0)
            {
                founded = new AvatarState
                {
                    AvatarId = avatarId,
                    MonarchGuid = monarch,
                    State = AvatarBindState.Bound,
                    LastDecayDay = today,
                };
            }
            else
            {
                var reloaded = store.LoadAll();
                if (reloaded == null)
                {
                    blocked = true;
                }
                else
                {
                    var row = reloaded.FirstOrDefault(r => r.MonarchGuid == monarch && r.State == (byte)AvatarBindState.Bound)
                        ?? reloaded.FirstOrDefault(r => r.MonarchGuid == monarch);
                    if (row != null)
                        founded = ShardAvatarStore.FromEntity(row);
                }
            }

            lock (sync)
            {
                pendingFoundings.Remove(monarch);
                if (blocked)
                {
                    blockedFoundings.Add(monarch);
                    log.Error($"[AVATAR] Founding for monarch 0x{monarch:X8} failed and the reload failed; founding blocked for this session.");
                    return;
                }

                if (founded == null)
                    return;

                if (founded.State != AvatarBindState.Bound || byId.ContainsKey(founded.AvatarId))
                {
                    blockedFoundings.Add(monarch);
                    log.Error($"[AVATAR] Founding for monarch 0x{monarch:X8} found avatar_Id {founded.AvatarId} ({founded.State}{(byId.ContainsKey(founded.AvatarId) ? ", already loaded" : "")}) holding the guid; founding blocked for this session.");
                    return;
                }

                founded.KeeperGuid = monarch;
                founded.ActiveAccounts = activeAccounts;
                founded.Active = founded.State == AvatarBindState.Bound && activeAccounts >= minActive;
                AddLoaded(founded);
            }

            log.Info($"[AVATAR] Avatar {founded.AvatarId} founded for monarch 0x{monarch:X8}{(ok ? "" : " (adopted after a failed insert)")}.");
        }
        catch (Exception ex)
        {
            lock (sync)
            {
                pendingFoundings.Remove(monarch);
                blockedFoundings.Add(monarch);
            }
            log.Error($"[AVATAR] Founding callback for monarch 0x{monarch:X8} threw; founding blocked for this session.", ex);
        }
    }

    /// <summary>
    /// Step 3: once per UTC day per bound Avatar, decays the unreserved Favor only. Reserved Favor is held for
    /// a purchase in flight, so a CommitDebit after decay can never underflow.
    /// </summary>
    private static void DecayDaily(double now, AvatarTuning t)
    {
        var today = DayOf(now);

        lock (sync)
        {
            foreach (var s in byId.Values)
            {
                if (s.State != AvatarBindState.Bound || s.LastDecayDay == today)
                    continue;

                var available = s.AvailableFavor;
                var kept = AvatarCurves.DecayFavor(available, s.LastContributionUtc, !s.Active, today, t);
                s.Favor = s.ReservedFavor + Math.Min(kept, available);
                s.LastDecayDay = today;
                dirtyAvatars.Add(s.AvatarId);
            }

            // Once a day: ledger rows older than the top-contributor window are stored and no longer read.
            if (lastPruneDay != today)
            {
                lastPruneDay = today;
                var oldest = today.AddDays(-Math.Max(0, t.TopContributorsDays));
                foreach (var ledger in ledgers.Values)
                    ledger.Prune(oldest);
            }
        }
    }

    /// <summary>
    /// Step 4: snapshots every dirty Avatar and ledger row and hands them to the store as one batch. A failed
    /// batch re-marks the same keys dirty; the next one carries the current absolute values. One batch in flight
    /// at a time, except at shutdown: a forced flush first re-marks every outstanding batch's keys, so a batch
    /// that later fails after shutdown has already been carried by the forced one.
    /// </summary>
    private static void Flush(bool force)
    {
        List<AllegianceAvatar> avatars;
        List<AllegianceAvatarContribution> rows;
        (List<AllegianceAvatar>, List<AllegianceAvatarContribution>) batch;

        lock (sync)
        {
            if (outstanding.Count > 0 && !force)
                return;

            if (force)
            {
                foreach (var (a, r) in outstanding)
                    MarkDirty(a, r);
            }

            avatars = dirtyAvatars.Where(byId.ContainsKey).Select(id => ShardAvatarStore.ToEntity(byId[id])).ToList();
            rows = ledgers.Values.SelectMany(l => l.TakeDirty()).ToList();
            dirtyAvatars.Clear();

            if (avatars.Count == 0 && rows.Count == 0)
                return;

            batch = (avatars, rows);
            outstanding.Add(batch);
        }

        try
        {
            store.SaveBatch(avatars, rows, ok => OnFlushed(ok, batch));
        }
        catch (Exception ex)
        {
            log.Error("[AVATAR] Flush threw at enqueue; the rows stay dirty for the next interval.", ex);
            OnFlushed(false, batch);
        }
    }

    private static void OnFlushed(bool ok, (List<AllegianceAvatar> Avatars, List<AllegianceAvatarContribution> Rows) batch)
    {
        var (avatars, rows) = batch;

        lock (sync)
        {
            outstanding.Remove(batch);
            if (ok)
            {
                // A released monarch guid is free in the store once a row that no longer holds it has been written.
                foreach (var a in avatars)
                {
                    foreach (var guid in vacatedUnflushed.Where(v => v.Value == a.AvatarId && a.MonarchGuid != v.Key).Select(v => v.Key).ToList())
                        vacatedUnflushed.Remove(guid);
                }
                return;
            }

            MarkDirty(avatars, rows);
        }

        log.Warn($"[AVATAR] Flush of {avatars.Count} Avatars and {rows.Count} contribution rows failed; retrying next interval.");
    }

    // ---- helpers ---------------------------------------------------------------------------------------------

    /// <summary>Caller holds the lock. Re-marks a batch's Avatars and ledger keys; the next snapshot carries current values.</summary>
    private static void MarkDirty(List<AllegianceAvatar> avatars, List<AllegianceAvatarContribution> rows)
    {
        foreach (var a in avatars)
            dirtyAvatars.Add(a.AvatarId);
        foreach (var group in rows.GroupBy(r => r.AvatarId))
        {
            if (ledgers.TryGetValue(group.Key, out var ledger))
                ledger.MarkDirty(group);
        }
    }

    /// <summary>Distinct active accounts in the roster. Members include the monarch at depth 0, so nothing is counted twice.</summary>
    private static int CountActiveAccounts(AvatarRoster roster, AccountActivityIndex activity, double now, int window)
    {
        var accounts = new HashSet<uint>();
        foreach (var m in roster.Members)
        {
            if (activity.IsActive(m.AccountId, now, window))
                accounts.Add(m.AccountId);
        }
        return accounts.Count;
    }

    private static double Multiplier(AvatarFeedSource source, AvatarTuning t) => source switch
    {
        AvatarFeedSource.Thread => t.ThreadMultiplier,
        AvatarFeedSource.WorldEvent => t.WorldEventMultiplier,
        AvatarFeedSource.Fellowship => t.FellowshipMultiplier,
        _ => 1.0,
    };

    private static AvatarLedger Ledger(uint avatarId)
    {
        if (!ledgers.TryGetValue(avatarId, out var ledger))
        {
            ledger = new AvatarLedger(avatarId);
            ledgers[avatarId] = ledger;
        }
        return ledger;
    }

    /// <summary>Caller holds the lock.</summary>
    private static void AddLoaded(AvatarState s)
    {
        byId[s.AvatarId] = s;
        if (s.State == AvatarBindState.Bound && s.MonarchGuid.HasValue)
            byMonarch[s.MonarchGuid.Value] = s.AvatarId;
        Ledger(s.AvatarId);
    }

    private static DateTime DayOf(double unix) => DateTime.SpecifyKind(Time.GetDateTimeFromTimestamp(unix).Date, DateTimeKind.Utc);
}
