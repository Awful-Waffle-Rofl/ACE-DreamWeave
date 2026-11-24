using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;

using ACE.Database;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.WorldEvents;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.Avatars.Gauntlet;

/// <summary>What a claim needs from the run that placed its caches. <see cref="GauntletRewards"/> adapts a <see cref="GauntletRun"/>.</summary>
internal interface IGauntletClaimRun
{
    /// <summary>The run id written to the daily claim row (never 0).</summary>
    uint ClaimRunId { get; }

    /// <summary>True when <paramref name="player"/> is a current member of the run's allegiance (fail-closed).</summary>
    bool IsMember(Player player);

    /// <summary>True when the character fought in enough waves to earn a crate.</summary>
    bool IsEligible(uint characterGuid);

    /// <summary>The waves counted toward a crate for the character, and the waves a crate needs (the refusal line's counts).</summary>
    (int Counted, int Required) WaveCredit(uint characterGuid);

    /// <summary>A claim paid a crate (the run's crates_Claimed count). Called once per paid claim, outside the window's lock.</summary>
    void NoteCrateClaimed();
}

/// <summary>The daily claim table behind the queue (SerializedShardDatabase). Callbacks arrive on the database worker thread.</summary>
internal interface IGauntletClaimStore
{
    void Insert(uint accountId, DateTime utcDay, uint runId, uint characterId, string token, Action<GauntletClaimInsertResult> callback);

    void Delete(uint accountId, DateTime utcDay, uint runId, uint characterId, Action<bool, int> callback);
}

/// <summary>The production claim store.</summary>
internal sealed class LiveGauntletClaimStore : IGauntletClaimStore
{
    public void Insert(uint accountId, DateTime utcDay, uint runId, uint characterId, string token, Action<GauntletClaimInsertResult> callback)
        => DatabaseManager.Shard.TryInsertGauntletDailyClaim(accountId, utcDay, runId, characterId, callback, token);

    public void Delete(uint accountId, DateTime utcDay, uint runId, uint characterId, Action<bool, int> callback)
        => DatabaseManager.Shard.DeleteGauntletDailyClaim(accountId, utcDay, runId, characterId, callback);
}

/// <summary>How one claim ended. <see cref="Paid"/> is the only result that gave a crate.</summary>
public enum GauntletClaimResult
{
    Paid,
    NotRewarding,
    NoAccount,
    NotMember,
    NotEligible,
    AlreadyClaimedCharacter,
    AlreadyClaimedAccount,
    AlreadyClaimedIp,

    /// <summary>The account's crate for this UTC day was taken (by another run or another character).</summary>
    AlreadyClaimedToday,

    /// <summary>This character's earlier claim is still being written.</summary>
    InProgress,

    StoreFailed,
    CrateFailed,
    GiveFailed,

    /// <summary>The claimant logged out (or is logging out) while the daily row was being written: nothing given, the day kept.</summary>
    PlayerGone,
}

/// <summary>
/// One run's claim window: what its caches pay, the gates, and the claim state. <see cref="Sync"/> is the one lock the claim
/// pipeline takes around every check-and-reserve and every mark or release, so a check and the act it guards are never
/// separated by another claim's act (the WorldEventCacheHandler rule).
/// </summary>
internal sealed class GauntletClaimWindow
{
    internal readonly object Sync = new object();

    public IGauntletClaimRun Run { get; }
    public string Profile { get; }
    public double Multiplier { get; }
    public uint CrateWcid { get; }
    public DateTime EndsAtUtc { get; }
    public bool GateByCharacter { get; init; } = true;
    public bool GateByAccount { get; init; } = true;
    public bool GateByIp { get; init; } = true;
    public ISet<string> ExemptIps { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The name stamped on each crate (GauntletRewards.CrateName), or null to keep the weenie's name.</summary>
    public string CrateName { get; init; }

    // Every set below is guarded by Sync. A character, account or household is RESERVED in the claimed sets at the moment its
    // claim passes the checks, so a second claim arriving before the first finishes already sees it; a claim that does not pay
    // gives its reservation back.
    internal readonly HashSet<uint> ClaimedCharacters = new HashSet<uint>();
    internal readonly HashSet<uint> ClaimedAccounts = new HashSet<uint>();
    internal readonly HashSet<string> ClaimedIps = new HashSet<string>();
    internal readonly HashSet<uint> InFlight = new HashSet<uint>();

    /// <summary>One token per logical claim (a character's claim in this window), kept across a Failed so the resubmit reuses it.</summary>
    internal readonly Dictionary<uint, string> Tokens = new Dictionary<uint, string>();

    internal bool Closed;
    internal int PaidCount;

    public GauntletClaimWindow(IGauntletClaimRun run, string profile, double multiplier, uint crateWcid, DateTime endsAtUtc)
    {
        Run = run;
        Profile = profile;
        Multiplier = multiplier;
        CrateWcid = crateWcid;
        EndsAtUtc = endsAtUtc;
    }

    /// <summary>Stops all further claims (the window is over or the run closed).</summary>
    public void Close()
    {
        lock (Sync)
            Closed = true;
    }

    public int Paid
    {
        get
        {
            lock (Sync)
                return PaidCount;
        }
    }
}

/// <summary>
/// The claim pipeline for a Gauntlet crate. The ORDER is the race-safety argument:
///
///  1. Membership and eligibility (no window state).
///  2. ONE critical section on the window: window open, the gates (character, account, household) against the claimed sets,
///     which already include every claim in flight; then RESERVE the character, account and household in those sets and mark
///     the character in flight. Two simultaneous claims by one character cannot both pass: the second finds the reservation.
///     The token is made here, once per logical claim, and kept until the claim ends other than Failed.
///  3. INSERT the daily row (account, UTC day) through the queue. The primary key is the cross-run, cross-character arbiter:
///     only one claim per account per day gets Inserted (or its own row back on a retry).
///  4. Only then CREATE the crate, stamp the tier profile and multiplier on it, and give it.
///  5. A crate that cannot be created or given DELETES the daily row (so the day is not spent) and releases the reservation
///     only after that delete has answered, so a retry never races its own undo.
///
/// The reservation matters beyond a tidy count: the token is reused for a character's retries, so the table alone would
/// answer a second simultaneous claim by the same character with OwnRowOnRetry (a success) and pay twice.
///
/// Callbacks arrive on the database thread and hop to the world thread (<see cref="RunOnWorld"/>) before touching a player.
/// </summary>
internal static class GauntletClaims
{
    private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

    public const string NotMemberLine = "The cache is not yours to open.";
    /// <summary>The refusal a member who fought too few waves gets, with their counts.</summary>
    public static string NotEligibleLine(int counted, int required) => GauntletRun.ShortLine(counted, required);
    public const string SpentLine = "This cache is spent.";
    public const string TodayLine = "Your account has already taken a Gauntlet crate today.";
    public const string InProgressLine = "Your claim is still being settled. Try again in a moment.";
    public const string StoreFailedLine = "The cache could not be opened just now. Use it again in a moment.";
    public const string NothingLine = "The cache holds nothing you can take.";
    public const string PaidLine = "The cache yields a Gauntlet crate.";

    /// <summary>Test seam: the daily claim table.</summary>
    internal static IGauntletClaimStore Store = new LiveGauntletClaimStore();

    /// <summary>Test seam: the clock.</summary>
    internal static Func<DateTime> UtcNow = () => DateTime.UtcNow;

    /// <summary>Test seam: hops from the database thread to the world thread.</summary>
    internal static Action<Action> RunOnWorld = work => WorldManager.EnqueueAction(new ActionEventDelegate(work));

    /// <summary>Test seam: creates the crate weenie (null when it does not exist).</summary>
    internal static Func<uint, WorldObject> CreateCrate = wcid => WorldObjectFactory.CreateNewWorldObject(wcid);

    /// <summary>Test seam: gives the crate to the player. <paramref name="cache"/> is null for a direct delivery.</summary>
    internal static Func<WorldObject, Player, WorldObject, bool> Give = DefaultGive;

    /// <summary>Test seam: every line sent to a claimant.</summary>
    internal static Action<Player, string> Tell = DefaultTell;

    /// <summary>Test seam: true when the claimant is no longer in the world to receive a crate (no session, or logging out).</summary>
    internal static Func<Player, bool> IsGone = DefaultIsGone;

    internal static bool DefaultIsGone(Player player) => player == null || player.Session == null || player.IsLoggingOut;

    internal static void ResetSeams()
    {
        Store = new LiveGauntletClaimStore();
        UtcNow = () => DateTime.UtcNow;
        RunOnWorld = work => WorldManager.EnqueueAction(new ActionEventDelegate(work));
        CreateCrate = wcid => WorldObjectFactory.CreateNewWorldObject(wcid);
        Give = DefaultGive;
        Tell = DefaultTell;
        IsGone = DefaultIsGone;
    }

    private static bool DefaultGive(WorldObject cache, Player player, WorldObject crate)
    {
        if (cache != null)
            return player.TryCreateForGive(cache, crate);

        if (!player.TryCreateInInventoryWithNetworking(crate))
            return false;

        Tell(player, PaidLine);
        return true;
    }

    private static void DefaultTell(Player player, string text)
    {
        if (player?.Session?.Network == null || string.IsNullOrEmpty(text))
            return;

        try
        {
            player.SendTransientError(text);
        }
        catch (Exception ex)
        {
            log.Debug($"[GAUNTLET] could not send a claim line to {player.Name}", ex);
        }
    }

    private sealed class Ticket
    {
        public uint Guid;
        public uint AccountId;
        public uint RunId;
        public DateTime Day;
        public string Token;
        public string IpKey;
        public bool AddedCharacter;
        public bool AddedAccount;
        public bool AddedIp;
    }

    /// <summary>
    /// Runs one claim. <paramref name="cache"/> is the cache object (null for a direct delivery). <paramref name="done"/> is
    /// called exactly once with the outcome: inline for a refusal, later (after the database answers) for anything that
    /// reached the table. Never throws.
    /// </summary>
    public static void Claim(GauntletClaimWindow w, WorldObject cache, Player player, uint accountId, string ip, string householdKey, Action<GauntletClaimResult> done = null)
    {
        try
        {
            ClaimCore(w, cache, player, accountId, ip, householdKey, done);
        }
        catch (Exception ex)
        {
            log.Error($"[GAUNTLET] claim threw for {player?.Name} (0x{player?.Guid.Full:X8})", ex);
            Finish(done, GauntletClaimResult.StoreFailed);
        }
    }

    private static void Finish(Action<GauntletClaimResult> done, GauntletClaimResult result)
    {
        try
        {
            done?.Invoke(result);
        }
        catch (Exception ex)
        {
            log.Error("[GAUNTLET] claim completion callback threw", ex);
        }
    }

    private static void Refuse(Action<GauntletClaimResult> done, Player player, GauntletClaimResult result, string line, string detail)
    {
        if (!string.IsNullOrEmpty(line))
            Tell(player, line);

        log.Info($"[GAUNTLET] claim char={player.Name} guid=0x{player.Guid.Full:X8} result=denied:{result}{detail}");
        Finish(done, result);
    }

    private static void ClaimCore(GauntletClaimWindow w, WorldObject cache, Player player, uint accountId, string ip, string householdKey, Action<GauntletClaimResult> done)
    {
        var guid = player.Guid.Full;

        if (accountId == 0)
        {
            Refuse(done, player, GauntletClaimResult.NoAccount, NothingLine, "");
            return;
        }

        // 1. Who may claim. Neither read touches the window, so both stay outside its lock.
        if (!w.Run.IsMember(player))
        {
            Refuse(done, player, GauntletClaimResult.NotMember, NotMemberLine, "");
            return;
        }

        if (!w.Run.IsEligible(guid))
        {
            var (counted, required) = w.Run.WaveCredit(guid);
            Refuse(done, player, GauntletClaimResult.NotEligible, NotEligibleLine(counted, required), "");
            return;
        }

        var now = UtcNow();
        var runId = w.Run.ClaimRunId;
        var ticket = new Ticket { Guid = guid, AccountId = accountId, RunId = runId, Day = now.Date, IpKey = WorldEventClaimRules.NormalizeIp(householdKey) };

        // 2. Check and reserve, in ONE critical section.
        ClaimDenial denial;
        var inFlight = false;

        lock (w.Sync)
        {
            if (w.Closed || now >= w.EndsAtUtc)
            {
                denial = ClaimDenial.NotRewarding;
            }
            else
            {
                denial = WorldEventClaimRules.Evaluate(guid, accountId, ip, householdKey, w.ClaimedCharacters, w.ClaimedAccounts, w.ClaimedIps,
                    w.ExemptIps, w.GateByCharacter, w.GateByAccount, w.GateByIp);

                inFlight = denial == ClaimDenial.AlreadyClaimedCharacter && w.InFlight.Contains(guid);

                if (denial == ClaimDenial.None)
                {
                    ticket.AddedCharacter = w.ClaimedCharacters.Add(guid);
                    ticket.AddedAccount = w.ClaimedAccounts.Add(accountId);
                    ticket.AddedIp = ticket.IpKey.Length > 0 && w.ClaimedIps.Add(ticket.IpKey);
                    w.InFlight.Add(guid);

                    if (!w.Tokens.TryGetValue(guid, out var token))
                        w.Tokens[guid] = token = ShardDatabase.NewGauntletClaimToken();

                    ticket.Token = token;
                }
            }
        }

        if (denial != ClaimDenial.None)
        {
            if (denial == ClaimDenial.NotRewarding)
                Refuse(done, player, GauntletClaimResult.NotRewarding, SpentLine, "");
            else if (inFlight)
                Refuse(done, player, GauntletClaimResult.InProgress, InProgressLine, "");
            else
                Refuse(done, player, (GauntletClaimResult)Enum.Parse(typeof(GauntletClaimResult), denial.ToString()),
                    WorldEventClaimRules.DenialMessage(denial, cache?.Name), $" account={accountId}");

            return;
        }

        // 3. The daily row, BEFORE the crate.
        try
        {
            Store.Insert(accountId, ticket.Day, runId, guid, ticket.Token, result => RunOnWorld(() => OnInserted(w, cache, player, ticket, result, done)));
        }
        catch (Exception ex)
        {
            log.Error($"[GAUNTLET] claim insert could not be queued for account {accountId}", ex);
            Release(w, ticket, discardToken: false);
            Tell(player, StoreFailedLine);
            Finish(done, GauntletClaimResult.StoreFailed);
        }
    }

    /// <summary>Gives back a reservation. <paramref name="discardToken"/> ends the logical claim; false keeps the token for a resubmit.</summary>
    private static void Release(GauntletClaimWindow w, Ticket t, bool discardToken)
    {
        lock (w.Sync)
        {
            if (t.AddedCharacter)
                w.ClaimedCharacters.Remove(t.Guid);

            if (t.AddedAccount)
                w.ClaimedAccounts.Remove(t.AccountId);

            if (t.AddedIp)
                w.ClaimedIps.Remove(t.IpKey);

            w.InFlight.Remove(t.Guid);

            if (discardToken)
                w.Tokens.Remove(t.Guid);
        }
    }

    private static void OnInserted(GauntletClaimWindow w, WorldObject cache, Player player, Ticket t, GauntletClaimInsertResult result, Action<GauntletClaimResult> done)
    {
        try
        {
            switch (result)
            {
                case GauntletClaimInsertResult.Inserted:
                case GauntletClaimInsertResult.OwnRowOnRetry:
                    GiveCrate(w, cache, player, t, done);
                    return;

                case GauntletClaimInsertResult.AlreadyClaimedToday:
                    Release(w, t, discardToken: true);
                    Refuse(done, player, GauntletClaimResult.AlreadyClaimedToday, TodayLine, $" account={t.AccountId}");
                    return;

                default:
                    // Failed: unknown whether the row committed. Keep the token so the next use resubmits the same logical claim.
                    Release(w, t, discardToken: false);
                    Refuse(done, player, GauntletClaimResult.StoreFailed, StoreFailedLine, $" account={t.AccountId}");
                    return;
            }
        }
        catch (Exception ex)
        {
            log.Error($"[GAUNTLET] claim completion threw for account {t.AccountId}", ex);
            Release(w, t, discardToken: false);
            Finish(done, GauntletClaimResult.StoreFailed);
        }
    }

    private static void GiveCrate(GauntletClaimWindow w, WorldObject cache, Player player, Ticket t, Action<GauntletClaimResult> done)
    {
        // The daily row took a database round trip: a claimant who left meanwhile gets nothing, so the day goes back.
        bool gone;

        try
        {
            gone = IsGone(player);
        }
        catch (Exception ex)
        {
            log.Error($"[GAUNTLET] claim for account {t.AccountId}: the presence check threw; treated as gone", ex);
            gone = true;
        }

        if (gone)
        {
            UndoDailyRow(w, t, () =>
            {
                log.Info($"[GAUNTLET] claim char={player?.Name} guid=0x{t.Guid:X8} account={t.AccountId} run={t.RunId} result=denied:{GauntletClaimResult.PlayerGone} - left before the crate, daily row removed");
                Finish(done, GauntletClaimResult.PlayerGone);
            });
            return;
        }

        WorldObject crate = null;
        var gave = false;
        var created = false;

        try
        {
            crate = CreateCrate(w.CrateWcid);
            created = crate != null;

            if (created)
            {
                // Stamped BEFORE the give, so the object a player receives already carries what its roll reads
                // (WorldEventRewardCrate.Open: PropertyFloat.WorldEventRewardMultiplier, PropertyString.WorldEventRewardProfile).
                WorldEventHooks.StampRewardCrate(crate, w.Profile, w.Multiplier);

                if (!string.IsNullOrEmpty(w.CrateName))
                    crate.Name = w.CrateName;

                gave = Give(cache, player, crate);
            }
        }
        catch (Exception ex)
        {
            log.Error($"[GAUNTLET] crate wcid {w.CrateWcid} for {player.Name} threw", ex);
            gave = false;
        }

        if (gave)
        {
            lock (w.Sync)
            {
                // Already in the claimed sets from the reservation: the claim simply stays marked.
                w.InFlight.Remove(t.Guid);
                w.Tokens.Remove(t.Guid);
                w.PaidCount++;
            }

            try
            {
                w.Run.NoteCrateClaimed();
            }
            catch (Exception ex)
            {
                log.Error($"[GAUNTLET] claim for account {t.AccountId}: counting the paid crate on run {t.RunId} threw", ex);
            }

            log.Info($"[GAUNTLET] claim char={player.Name} guid=0x{t.Guid:X8} account={t.AccountId} run={t.RunId} result=granted profile={w.Profile} mult={w.Multiplier:0.###}");
            Finish(done, GauntletClaimResult.Paid);
            return;
        }

        if (crate != null)
            TryDestroy(crate);

        var failure = created ? GauntletClaimResult.GiveFailed : GauntletClaimResult.CrateFailed;

        if (!created)
        {
            log.Error($"[GAUNTLET] claim for account {t.AccountId}: crate wcid {w.CrateWcid} did not create");
            Tell(player, NothingLine);
        }

        // The daily row goes back so the day is not spent on a crate nobody received. The reservation stays until the delete has
        // answered, so a retry can never race its own undo.
        UndoDailyRow(w, t, () =>
        {
            log.Info($"[GAUNTLET] claim char={player.Name} guid=0x{t.Guid:X8} account={t.AccountId} run={t.RunId} result=denied:{failure} - daily row removed, still retryable");
            Finish(done, failure);
        });
    }

    private static void UndoDailyRow(GauntletClaimWindow w, Ticket t, Action then)
    {
        void Answered(bool ok, int deleted)
        {
            // A row that could not be deleted is still ours (its token is kept), so the next use finds it as OwnRowOnRetry.
            if (!ok)
                log.Error($"[GAUNTLET] the daily claim row for account {t.AccountId} could not be removed; the day stays spent until the next use retries it");

            Release(w, t, discardToken: ok);
            then();
        }

        try
        {
            Store.Delete(t.AccountId, t.Day, t.RunId, t.Guid, (ok, deleted) => RunOnWorld(() => Answered(ok, deleted)));
        }
        catch (Exception ex)
        {
            log.Error($"[GAUNTLET] the daily claim undo for account {t.AccountId} could not be queued", ex);
            Answered(false, 0);
        }
    }

    private static void TryDestroy(WorldObject wo)
    {
        try
        {
            wo.Destroy();
        }
        catch (Exception ex)
        {
            log.Debug($"[GAUNTLET] could not destroy an undelivered crate 0x{wo.Guid.Full:X8}", ex);
        }
    }
}

/// <summary>
/// The Gauntlet reward cache: a Generic object carrying PropertyBool.AllegianceGauntletCache, dispatched from
/// GenericObject.ActOnUse. A use finds its claim window by the cache's guid in <see cref="Register"/>'s registry and runs
/// <see cref="GauntletClaims.Claim"/> with the player's account, address and household. Opening the crate it pays needs no code
/// here: WorldEventRewardCrate reads the profile and multiplier stamped on it.
/// </summary>
public static class GauntletCacheHandler
{
    private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

    private static readonly ConcurrentDictionary<uint, GauntletClaimWindow> windows = new ConcurrentDictionary<uint, GauntletClaimWindow>();

    internal static void Register(uint cacheGuid, GauntletClaimWindow window)
    {
        if (window == null)
            throw new ArgumentNullException(nameof(window));

        windows[cacheGuid] = window;
    }

    internal static void Unregister(uint cacheGuid) => windows.TryRemove(cacheGuid, out _);

    internal static void ResetRegistry() => windows.Clear();

    /// <summary>
    /// The claimant's account, raw address and household key. The raw address is only what world_events_ip_exempt is matched
    /// against (WorldEventClaimRules.Evaluate); the limit key is the household key. This is the one place the Gauntlet reads it.
    /// </summary>
    internal static (uint AccountId, string Ip, string Household) Identify(Player player)
    {
        var session = player?.Session;
        return (session?.AccountId ?? 0, session?.EndPointC2S?.Address?.ToString(), IpLimitManager.HouseholdKey(session));
    }

    /// <summary>
    /// GenericObject.ActOnUse: true when <paramref name="cache"/> is a Gauntlet cache (the use is handled here, paid or refused).
    /// False after one property read for every other object. Never throws.
    /// </summary>
    public static bool TryHandleUse(WorldObject cache, Player player)
    {
        if (cache == null || player == null || cache.GetProperty(PropertyBool.AllegianceGauntletCache) != true)
            return false;

        try
        {
            if (!windows.TryGetValue(cache.Guid.Full, out var window))
            {
                GauntletClaims.Tell(player, GauntletClaims.SpentLine);
                return true;
            }

            var (accountId, ip, household) = Identify(player);

            GauntletClaims.Claim(window, cache, player, accountId, ip, household);
        }
        catch (Exception ex)
        {
            log.Error($"[GAUNTLET] cache use by {player.Name} threw", ex);
            GauntletClaims.Tell(player, GauntletClaims.NothingLine);
        }

        return true;
    }
}
